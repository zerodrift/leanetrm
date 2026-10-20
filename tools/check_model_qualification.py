"""Model-provider qualification: confirmed mappings, terms, attestation, Enable and Disable; owned synthetic preview only."""
import json
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from assistant_context import Contexts, check_guard, expect, holding, refused, refused_at, state, waiting
from preview_client import PreviewClient

PROVIDERS = 'settings/ai/providers'
COMMANDS = ('attest', 'enable', 'disable')
EMPTY = '00000000-0000-0000-0000-000000000000'
TERMS_FIELDS = ('accountType', 'retention', 'training', 'ownerAccess')
SOURCES = ('retentionSource', 'trainingSource', 'ownerAccessSource')
DATES = ('retentionCheckedOn', 'trainingCheckedOn', 'ownerAccessCheckedOn')
REPLACE_CREDENTIAL = 'assistant.replace_credential(uuid,assistant.vendor,integer,uuid,integer,bytea,bytea,bytea,text,text)'
# The ten terms arguments of assistant.record_terms after its operation, vendor and expected version.
SQL_TERMS = ("'Synthetic account','Synthetic retention','https://example.invalid/r','2026-10-01','Synthetic training',"
             "'https://example.invalid/t','2026-10-01','Synthetic owner access','https://example.invalid/o','2026-10-01'")


def check_upgrade(query, migration):
    check_guard(query, migration, '0058', 'provider_qualification',
                ((f"ALTER FUNCTION {REPLACE_CREDENTIAL} SET work_mem='64kB';", REPLACE_CREDENTIAL),
                 ('GRANT EXECUTE ON FUNCTION assistant.provider_state(uuid) TO openetrm_runtime;', 'assistant.provider_state(uuid)')))


def terms(vendor, expected, **changes):
    """A complete, synthetic terms request; each source is an explicitly invalid domain."""
    return {'operationId': str(uuid4()), 'vendor': vendor, 'expectedVersion': expected,
            'accountType': 'Synthetic commercial API organization',
            'retention': 'Synthetic: inputs and outputs are deleted within 30 days.',
            'retentionSource': 'https://example.invalid/synthetic/retention', 'retentionCheckedOn': '2026-10-01',
            'training': 'Synthetic: submitted content is not used for training.',
            'trainingSource': 'https://example.invalid/synthetic/training', 'trainingCheckedOn': '2026-10-01',
            'ownerAccess': 'Synthetic: organization owners can view request logs.',
            'ownerAccessSource': 'https://example.invalid/synthetic/owner-access', 'ownerAccessCheckedOn': '2026-10-01', **changes}


def command(vendor, expected):
    return {'operationId': str(uuid4()), 'vendor': vendor, 'expectedVersion': expected}


def run_checks(url, query, psql, manifest, environment):
    contexts = Contexts(query, manifest)
    passed = 0

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)

    def providers():
        return {p['vendor']: p for p in admin.call(PROVIDERS)['providers']}

    def refusal(path, body, status, code):
        return admin.call(f'{PROVIDERS}/{path}', body, expected=status)['code'] == code

    def provider_id(vendor):
        return query(f"SELECT id FROM assistant.providers WHERE vendor='{vendor}' AND workspace_id='{contexts.workspace}';")

    mappings = json.loads(query("SELECT json_agg(json_build_object('vendor',vendor,'mapping',mapping,'confirmedOn',confirmed_on,'sources',sources)) "
                                "FROM assistant.confirmed_mappings;"))
    check('the release confirms exactly one usage mapping, the Messages mapping, with its published sources, and its adapter applies it',
          [(m['vendor'], m['mapping'], m['confirmedOn']) for m in mappings] == [('anthropic', 'messages_v1', '2026-10-20')]
          and all(s.startswith('https://platform.claude.com/docs/en/') for s in mappings[0]['sources'])
          and query("SELECT string_agg(vendor||' '||mapping,',') FROM assistant.adapter_mappings;") == 'anthropic messages_v1')
    for table in ('confirmed_mappings', 'adapter_mappings', 'provider_terms', 'attestations'):
        contexts.runtime('admin', expect(f'PERFORM 1 FROM assistant.{table};', '42501'))
    check('the runtime reads mappings, terms and attestations only through administration operations')
    for statement in ("UPDATE assistant.confirmed_mappings SET confirmed_on=confirmed_on;", "DELETE FROM assistant.confirmed_mappings;"):
        query(expect(statement, '55000'))
    check('a confirmed mapping is immutable')
    check('a source may be checked on any date that has begun somewhere by then, never a later or unbounded one',
          query("SELECT string_agg(assistant.is_checked_on(d::date,at::timestamptz)::text,',' ORDER BY n) FROM(VALUES"
                "(1,'2026-10-21','2026-10-20 10:00:00+00'),(2,'2026-10-21','2026-10-20 09:59:59.999999+00'),"
                "(3,'2026-10-20','2026-10-20 00:00:00+00'),(4,'-infinity','2026-10-20 10:00:00+00'))v(n,d,at);") == 'true,false,true,false')

    admin, viewer = PreviewClient(url), PreviewClient(url)
    for client, profile in ((admin, 'admin'), (viewer, 'viewer')):
        client.bootstrap(); client.call('profile', {'profile': profile})
    before = providers()
    anthropic, openai = before['anthropic'], before['openai']
    check("a provider starts disabled and unqualified, without terms or attestation, and only a vendor whose adapter applies a confirmed mapping names it",
          all(not p['enabled'] and not p['qualified'] and p['terms'] is None and p['attestation'] is None for p in before.values())
          and anthropic['mapping'] == {'mapping': 'messages_v1', 'confirmedOn': '2026-10-20', 'sources': mappings[0]['sources']}
          and all(p['mapping'] is None for v, p in before.items() if v != 'anthropic'))
    check('the earlier checks left keys for the Anthropic, OpenAI, Google and xAI providers',
          all(before[v]['credential'] is not None for v in ('anthropic', 'openai', 'google', 'xai')))
    for path, body in (('terms', terms('anthropic', anthropic['version'])), ('attest', command('anthropic', anthropic['version'])),
                       ('enable', command('anthropic', anthropic['version'])), ('disable', command('anthropic', anthropic['version']))):
        viewer.call(f'{PROVIDERS}/{path}', body, expected=403)
    check('every qualification command requires ManageModelProviders')
    version = anthropic['version']
    for statement in (f"PERFORM assistant.record_terms(gen_random_uuid(),'anthropic',{version},{SQL_TERMS});",
                      *(f"PERFORM assistant.{name}(gen_random_uuid(),'anthropic',{version});" for name in ('attest', 'enable_provider', 'disable_provider'))):
        contexts.runtime('viewer', expect(statement, '42501'))
        contexts.delegated_agent(expect(statement, '42501'))
    check('the database itself refuses every qualification command to a viewer, and to an agent even under a current delegation of ManageModelProviders')
    check('a provider without terms or an attestation cannot be enabled',
          refusal('enable', command('anthropic', version), 409, 'model_provider_unqualified'))
    check('a provider without terms cannot be attested', refusal('attest', command('anthropic', version), 409, 'model_admin_conflict'))

    rows = state('providers', 'provider_terms', 'attestations', 'administration_audit')
    unchanged = query(rows)
    text = ('', ' Padded ', '\u00a0Padded', 'Padded\u3000', 'x' * 1001, 'Line\nbreak', 'Synthetic\u0085text', 'Synthetic\u202etext', 'Synthetic\u2028text')
    sources = ('http://example.invalid/synthetic', 'https://example .invalid/synthetic', 'https://' + 'a' * 2041, 'example.invalid/synthetic', '',
               'https://.', 'https://-synthetic.invalid/r', 'https://example.invalid:443/r', 'https://user@example.invalid/r', 'https://example.invalid/r\u00e9')
    invalid = [{field: value} for field in TERMS_FIELDS for value in text] \
        + [{field: value} for field in SOURCES for value in sources] \
        + [{field: value} for field in DATES for value in ('2026-13-01', '2026-10-1', '2999-01-01', '')] \
        + [{field: 'Synthetic\0text'} for field in (*TERMS_FIELDS, *SOURCES)] \
        + [{field: None} for field in (*TERMS_FIELDS, *SOURCES, *DATES)] + [{'vendor': 'synthetic'}, {'expectedVersion': -1}, {'operationId': EMPTY}]
    check('each terms field is required in its documented form: 1 to 1000 characters without surrounding spaces, control, bidirectional '
          'control or line separator characters; an https source naming a host in at most 2048 visible ASCII characters; '
          'and a calendar date that has begun',
          all(refusal('terms', {**terms('anthropic', version), **change}, 422, 'model_admin_input') for change in invalid))
    malformed = ({'vendor': 'synthetic'}, {'vendor': None}, {'expectedVersion': None}, {'expectedVersion': -1}, {'operationId': EMPTY})
    check('Attest, Enable and Disable require a supported vendor, a request identity and a version that is not negative',
          all(refusal(path, {**command('anthropic', version), **change}, 422, 'model_admin_input') for path in COMMANDS for change in malformed))
    for path, body in (('terms', terms('anthropic', version)), *((path, command('anthropic', version)) for path in COMMANDS)):
        admin.call(f'{PROVIDERS}/{path}', {**body, 'note': 'unlisted'}, expected=400)
    check('a qualification request naming a member that is not part of it is a bad request')
    check('refused qualification requests write nothing', query(rows) == unchanged)

    first = terms('anthropic', version)
    recorded = admin.call(f'{PROVIDERS}/terms', first)
    check('recording terms stores them as version 1 with their sources and dates, advances the provider and leaves it disabled',
          recorded['version'] == version + 1 and not recorded['enabled'] and not recorded['qualified'] and recorded['attestation'] is None
          and recorded['terms']['version'] == 1 and recorded['terms']['retentionSource'] == first['retentionSource']
          and recorded['terms']['ownerAccessCheckedOn'] == '2026-10-01' and recorded['terms']['recordedBy'] and recorded['terms']['recordedAt'])
    check('an identical terms retry returns the original outcome', admin.call(f'{PROVIDERS}/terms', first) == recorded)
    check('a terms request identity cannot be reused for other terms', refusal('terms', {**first, 'retention': 'Synthetic: kept for 90 days.'}, 409, 'model_admin_conflict'))
    check('terms against a stale provider version are a conflict', refusal('terms', terms('anthropic', version), 409, 'model_admin_conflict'))
    version = recorded['version']
    check('a request identity cannot be reused for another command',
          refusal('attest', {**command('anthropic', version), 'operationId': first['operationId']}, 409, 'model_admin_conflict'))

    check('an attestation against a stale provider version is a conflict', refusal('attest', command('anthropic', version - 1), 409, 'model_admin_conflict'))
    attest = command('anthropic', version)
    attested = admin.call(f'{PROVIDERS}/attest', attest)
    check('an attestation binds to the current key and terms versions and qualifies a provider with a confirmed mapping, without enabling it',
          attested['version'] == version + 1 and attested['qualified'] and not attested['enabled']
          and attested['attestation']['credentialVersion'] == anthropic['credential']['version'] and attested['attestation']['termsVersion'] == 1
          and attested['attestation']['attestedBy'] and attested['attestation']['attestedAt'])
    check('an identical attestation retry returns the original outcome', admin.call(f'{PROVIDERS}/attest', attest) == attested)
    version = attested['version']
    check('the same key and terms cannot be attested twice', refusal('attest', command('anthropic', version), 409, 'model_admin_conflict'))
    check('disabling a disabled provider is a conflict', refusal('disable', command('anthropic', version), 409, 'model_admin_conflict'))
    check('Enable against a stale provider version is a conflict', refusal('enable', command('anthropic', version - 1), 409, 'model_admin_conflict'))
    enable = command('anthropic', version)
    enabled = admin.call(f'{PROVIDERS}/enable', enable)
    check('a qualified provider is enabled by Enable alone, which advances its version', enabled['enabled'] and enabled['version'] == version + 1)
    check('an identical Enable retry returns the original outcome', admin.call(f'{PROVIDERS}/enable', enable) == enabled)
    version = enabled['version']
    check('enabling an enabled provider is a conflict', refusal('enable', command('anthropic', version), 409, 'model_admin_conflict'))
    check('Disable against a stale provider version is a conflict', refusal('disable', command('anthropic', version - 1), 409, 'model_admin_conflict'))

    key = {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': version, 'key': 'sk-synthetic-qualification-0000-WXYZ'}
    replaced = admin.call(f'{PROVIDERS}/key', key)
    check('Replace key disables the provider and leaves no current attestation in the same change',
          not replaced['enabled'] and replaced['attestation'] is None and not replaced['qualified'] and replaced['terms']['version'] == 1)
    version = replaced['version']
    check('after Replace key the provider cannot be enabled until it is attested again',
          refusal('enable', command('anthropic', version), 409, 'model_provider_unqualified'))
    reattested = admin.call(f'{PROVIDERS}/attest', command('anthropic', version))
    check('a new attestation binds to the new key', reattested['qualified'] and reattested['attestation']['credentialVersion'] == replaced['credential']['version']
          and not reattested['enabled'])
    again = admin.call(f'{PROVIDERS}/enable', command('anthropic', reattested['version']))
    long_text = 'Synthetic: inputs are deleted within 7 days; données, Daten, 日本語.'.ljust(1000, '.')
    long_source = 'https://example.invalid/' + 'r' * 2024
    revised = admin.call(f'{PROVIDERS}/terms', terms('anthropic', again['version'], retention=long_text, retentionSource=long_source))
    check('new terms disable the provider and leave no current attestation, since the attestation also binds to the terms',
          again['enabled'] and not revised['enabled'] and revised['attestation'] is None and not revised['qualified'] and revised['terms']['version'] == 2)
    check('terms text of 1000 characters in any script and a source of 2048 are kept exactly',
          revised['terms']['retention'] == long_text and revised['terms']['retentionSource'] == long_source)
    version = revised['version']
    check('after new terms the provider cannot be enabled until it is attested again',
          refusal('enable', command('anthropic', version), 409, 'model_provider_unqualified'))
    final = admin.call(f'{PROVIDERS}/attest', command('anthropic', version))
    final = admin.call(f'{PROVIDERS}/enable', command('anthropic', final['version']))
    disable = command('anthropic', final['version'])
    disabled = admin.call(f'{PROVIDERS}/disable', disable)
    check('Disable turns an enabled provider off and keeps its attestation',
          not disabled['enabled'] and disabled['qualified'] and disabled['attestation']['termsVersion'] == 2)
    check('an identical Disable retry returns the original outcome', admin.call(f'{PROVIDERS}/disable', disable) == disabled)
    resumed = admin.call(f'{PROVIDERS}/enable', command('anthropic', disabled['version']))
    check('Enable alone resumes a disabled provider whose attestation is current', resumed['enabled'] and resumed['attestation'] == disabled['attestation'])
    disabled = admin.call(f'{PROVIDERS}/disable', command('anthropic', resumed['version']))

    responses = admin.call(f'{PROVIDERS}/terms', terms('openai', openai['version']))
    responses = admin.call(f'{PROVIDERS}/attest', command('openai', responses['version']))
    check('a provider with a key, terms and an attestation but no confirmed usage mapping stays unqualified',
          not responses['qualified'] and responses['attestation'] is not None
          and refusal('enable', command('openai', responses['version']), 409, 'model_provider_unqualified'))
    check("qualification follows the mapping the vendor's adapter applies, not any mapping confirmed for the vendor",
          query("BEGIN;INSERT INTO assistant.confirmed_mappings VALUES('openai','synthetic_responses_v1','2026-10-20',ARRAY['https://example.invalid/usage']);"
                "INSERT INTO assistant.mapping_efforts VALUES('openai','synthetic_responses_v1',ARRAY['low']);"
                f"SELECT assistant.provider_qualified('{provider_id('openai')}');"
                "INSERT INTO assistant.adapter_mappings VALUES('openai','synthetic_responses_v1');"
                f"SELECT assistant.provider_qualified('{provider_id('openai')}');ROLLBACK;").splitlines() == ['f', 't'])

    audit = admin.call(PROVIDERS)['audit']
    check('the audit lists each qualification change once, newest first',
          [(a['action'], a['vendor']) for a in audit[:9]] == [
              ('attest', 'openai'), ('record_terms', 'openai'), ('disable_provider', 'anthropic'), ('enable_provider', 'anthropic'),
              ('disable_provider', 'anthropic'), ('enable_provider', 'anthropic'), ('attest', 'anthropic'), ('record_terms', 'anthropic'),
              ('enable_provider', 'anthropic')])
    stored = json.loads(query("SELECT json_agg(request ORDER BY recorded_at) FROM assistant.administration_audit WHERE action='record_terms';"))
    check('an audited terms request keeps every field as entered', stored[0] == {k: v for k, v in first.items() if k != 'operationId'} | {'action': 'record_terms'})

    lines = contexts.second_workspace(
        refused("PERFORM assistant.attest(gen_random_uuid(),'anthropic',0);", 'model_admin_conflict') + "\n"
        "SELECT assistant.provider_directory()::text;\n"
        f"SELECT assistant.record_terms(gen_random_uuid(),'anthropic',0,{SQL_TERMS})::text;\n"
        + refused("PERFORM assistant.attest(gen_random_uuid(),'anthropic',1);", 'model_admin_conflict')).splitlines()
    isolated, created = json.loads(lines[-2]), json.loads(lines[-1])
    check("another workspace sees none of this workspace's providers, terms, attestations or audit, and the same release mappings",
          isolated['providers'] == [] and isolated['audit'] == [] and list(isolated['mappings']) == ['anthropic'])
    check("a vendor's first terms create its provider at version 1 without a key, which cannot be attested",
          created['version'] == 1 and created['credential'] is None and created['terms']['version'] == 1 and not created['enabled'])

    provider = provider_id('anthropic')

    def audit_entry(action, target, digest='NULL'):
        return ("INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,key_digest,"
                f"after_state,recorded_at) VALUES('{contexts.tenant}','{contexts.workspace}',gen_random_uuid(),'{contexts.administrator}',"
                f"'{action}',{target},'{{}}',{digest},'{{}}',now());")
    for action in ('record_terms', 'attest', 'enable_provider', 'disable_provider'):
        for statement in (audit_entry(action, 'NULL'), audit_entry(action, f"'{provider}'", "repeat('0',64)")):
            query(expect(statement, '23514'))
    check('even for the owner, a qualification change names its provider and no key')

    def terms_row(account="'Synthetic account'", source="'https://example.invalid/r'", checked_on="'2026-10-01'"):
        return (f"INSERT INTO assistant.provider_terms VALUES('{provider}',99,{account},'Synthetic retention',{source},{checked_on},"
                "'Synthetic training','https://example.invalid/t','2026-10-01','Synthetic owner access','https://example.invalid/o','2026-10-01',"
                f"'{contexts.administrator}',now());")
    for statement in (terms_row(account="' Padded '"), terms_row(source="'http://example.invalid/r'"), terms_row(checked_on="'-infinity'"),
                      "INSERT INTO assistant.confirmed_mappings VALUES('openai','synthetic_v1','2026-10-20',ARRAY['http://example.invalid/usage']);",
                      "INSERT INTO assistant.confirmed_mappings VALUES('openai','Synthetic v1','2026-10-20',ARRAY['https://example.invalid/usage']);"):
        query(expect(statement, '23514'))
    for statement in (*(f"INSERT INTO assistant.attestations VALUES('{provider}',{credential_version},{terms_version},'{contexts.administrator}',now());"
                        for credential_version, terms_version in ((99, 1), (1, 99))),
                      "INSERT INTO assistant.adapter_mappings VALUES('openai','synthetic_unconfirmed');"):
        query(expect(statement, '23503'))
    check('even for the owner, terms and mappings keep their documented forms, an attestation names an existing key and terms version, '
          'and an adapter applies only a confirmed mapping')
    for statement in ("UPDATE assistant.provider_terms SET retention=retention;", "DELETE FROM assistant.attestations;"):
        query(expect(statement, '55000'))
    check('terms versions and attestations are immutable')
    contexts.runtime('admin', refused(f"PERFORM assistant.enable_provider('{EMPTY}','anthropic',1);", 'model_admin_input'))
    check('the database itself refuses an empty operation')

    # Two sessions of one administrator send each command at once, each held at its first write:
    # the second waits for the first and replays its outcome.
    duplicate = PreviewClient(url); duplicate.bootstrap(); duplicate.call('profile', {'profile': 'admin'})
    with ThreadPoolExecutor(2) as pool:
        def overlapping(path, body, operation, table):
            def overlap():
                original = pool.submit(admin.call, f'{PROVIDERS}/{path}', body)
                waiting(query, 1, operation)
                retry = pool.submit(duplicate.call, f'{PROVIDERS}/{path}', body)
                waiting(query, 2, operation)
                return original, retry
            original, retry = holding(psql, environment, f'LOCK TABLE assistant.{table} IN EXCLUSIVE MODE', overlap)
            return body['operationId'], original.result(timeout=20), retry.result(timeout=20)
        outcomes = [overlapping('terms', terms('xai', before['xai']['version']), 'assistant.record_terms', 'provider_terms')]
        outcomes.append(overlapping('attest', command('xai', outcomes[-1][1]['version']), 'assistant.attest', 'attestations'))
        outcomes.append(overlapping('enable', command('anthropic', disabled['version']), 'assistant.enable_provider', 'administration_audit'))
        outcomes.append(overlapping('disable', command('anthropic', outcomes[-1][1]['version']), 'assistant.disable_provider', 'administration_audit'))
    operations = ','.join(f"'{operation}'" for operation, *_ in outcomes)
    check('an overlapping retry of each qualification command waits for its original and replays its outcome, writing once',
          all(original == retry for _, original, retry in outcomes)
          and query(f"SELECT count(*) FROM assistant.administration_audit WHERE operation_id IN({operations});") == '4'
          and outcomes[1][1]['attestation']['termsVersion'] == 1 and not outcomes[3][1]['enabled'])
    disabled = outcomes[3][1]
    contexts.runtime('admin', refused(f"PERFORM assistant.attest(gen_random_uuid(),'anthropic',{disabled['version']});", 'model_admin_conflict'))
    check('the database itself refuses a second attestation of the same key and terms with its documented code')

    # As Replace key does (0045), each command rechecks its authority after its provider row wait and
    # after its writes. Each case lets the request context expire while the command waits at the point
    # its recheck must follow, and names the frame that must refuse it. Attest needs an unattested pair,
    # so it acts on a provider that has a key and now terms.
    google = admin.call(f'{PROVIDERS}/terms', terms('google', before['google']['version']))

    def row_lock(target):
        return f"SELECT id FROM assistant.providers WHERE id='{target}' FOR NO KEY UPDATE"
    audit_write = 'LOCK TABLE assistant.administration_audit IN EXCLUSIVE MODE'
    enable = f"SELECT assistant.enable_provider(gen_random_uuid(),'anthropic',{disabled['version']});"
    record = f"SELECT assistant.record_terms(gen_random_uuid(),'anthropic',{disabled['version']},{SQL_TERMS});"
    attest_terms = f"SELECT assistant.attest(gen_random_uuid(),'google',{google['version']});"
    principal = ('security.principals', 'RowShareLock')
    for name, wait, lock, statement, site, held in (
            ('Enable', 'its provider row', row_lock(provider), enable, r'switch_provider\(.*\) line \d+ at assignment', principal),
            ('Record terms', 'its provider row', row_lock(provider), record, r'record_terms\(.*\) line \d+ at assignment', principal),
            ('Attest', 'its provider row', row_lock(provider_id('google')), attest_terms, r'attest\(.*\) line \d+ at assignment', principal),
            ('Enable', 'its audit write', audit_write, enable, r'switch_provider\(.*\) line \d+ at PERFORM', ('assistant.providers', 'RowExclusiveLock')),
            ('Record terms', 'its audit write', audit_write, record, r'record_terms\(.*\) line \d+ at PERFORM',
             ('assistant.provider_terms', 'RowExclusiveLock')),
            ('Attest', 'its audit write', audit_write, attest_terms, r'attest\(.*\) line \d+ at PERFORM', ('assistant.attestations', 'RowExclusiveLock'))):
        before_rows = query(rows)
        failure = contexts.expiring(psql, environment, 'context', lock, statement, held=held)
        check(f'{name} waiting for {wait} with an expired request context is refused at its recheck',
              refused_at(failure, '42501', rf'function assistant\.{site}'))
        check(f'an expired {name} leaves every provider, terms, attestation and audit row unchanged', query(rows) == before_rows)
    print(f'{passed} model qualification checks passed.', flush=True)
    return passed
