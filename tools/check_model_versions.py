"""Model capability versions and model Enable and Disable; owned synthetic preview only."""
import json
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from assistant_context import Contexts, check_guard, expect, holding, migration_body, refused, refused_at, state, waiting

MODELS = 'settings/ai/models'
PROVIDERS = 'settings/ai/providers'
EMPTY = '00000000-0000-0000-0000-000000000000'
FACTS = ('outputLimit', 'reasoning', 'identifierStability', 'acceptedServed', 'tools', 'streaming')
COLUMNS = ('output_limit', 'reasoning', 'identifier_stability', 'accepted_served', 'tools', 'streaming')
SOURCES = tuple(f'{fact}Source' for fact in FACTS)
DATES = tuple(f'{fact}CheckedOn' for fact in FACTS)
MODEL_STATE = 'assistant.model_state(uuid)'
# A release that confirms a second Messages mapping and states its efforts; each check rolls it back.
CONFIRM_SECOND = "INSERT INTO assistant.confirmed_mappings VALUES('anthropic','synthetic_messages_v2','2026-10-20',ARRAY['https://example.invalid/usage']);"
SECOND_MAPPING = CONFIRM_SECOND + "INSERT INTO assistant.mapping_efforts VALUES('anthropic','synthetic_messages_v2',ARRAY['high','max']);"


def check_upgrade(query, migration):
    check_guard(query, migration, '0060', 'model_versions',
                ((f"ALTER FUNCTION {MODEL_STATE} SET work_mem='64kB';", MODEL_STATE),
                 ('GRANT EXECUTE ON FUNCTION assistant.provider_qualified(uuid) TO openetrm_runtime;', 'assistant.provider_qualified(uuid)')))
    body = migration_body(migration)
    repeated = "ARRAY['https://example.invalid/usage','https://example.invalid/usage']"
    try:
        query(f"BEGIN;INSERT INTO assistant.confirmed_mappings VALUES('openai','synthetic_repeated_v1','2026-10-20',{repeated});{body}ROLLBACK;")
    except RuntimeError as error:
        assert 'confirmed_mappings_sources_check' in str(error), error
    else:
        raise AssertionError('Migration 0060 applied over a confirmed mapping whose sources repeat')
    print('PASS migration 0060 refuses to apply over a confirmed mapping whose sources are not a list of distinct sources', flush=True)


def check_directory_upgrade(query, migration):
    check_guard(query, migration, '0061', 'directory_efforts',
                (("ALTER FUNCTION assistant.provider_directory() SET work_mem='64kB';", 'assistant.provider_directory()'),
                 ('GRANT EXECUTE ON FUNCTION assistant.provider_state(uuid) TO openetrm_runtime;', 'assistant.provider_state(uuid)')))


def capabilities(model, expected, **changes):
    """A complete, synthetic capability version; each source is an explicitly invalid domain."""
    facts = {'outputLimit': 64000, 'reasoningEfforts': ['low', 'medium', 'high'], 'acceptedServed': ['model-synthetic-capable-served']}
    for fact in FACTS:
        facts[f'{fact}Source'] = f'https://example.invalid/synthetic/{fact}'
        facts[f'{fact}CheckedOn'] = '2026-10-01'
    return {'operationId': str(uuid4()), 'modelId': model, 'expectedVersion': expected, **facts, **changes}


def command(model, expected):
    return {'operationId': str(uuid4()), 'modelId': model, 'expectedVersion': expected}


def run_checks(query, psql, manifest, environment, admin, viewer, again):
    """`admin` and `again` are two sessions of the preview administrator, `viewer` one of the viewer, shared with the other modules."""
    contexts = Contexts(query, manifest)
    passed = 0

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)

    def refusal(path, body, status, code):
        return admin.call(f'{MODELS}/{path}', body, expected=status)['code'] == code

    def model(identifier):
        return next(m for m in admin.call(MODELS)['models'] if m['identifier'] == identifier)

    def provider(vendor):
        return next(p for p in admin.call(PROVIDERS)['providers'] if p['vendor'] == vendor)

    def sql_edit(model_id, expected, efforts="ARRAY['low']", served='ARRAY[]::text[]'):
        source, date = "'https://example.invalid/s'", "'2026-10-01'"
        return (f"assistant.edit_model(gen_random_uuid(),'{model_id}',{expected},64000,{source},{date},{efforts},{source},{date},"
                f"{source},{date},{served},{source},{date},{source},{date},{source},{date})")

    for table in ('model_versions', 'mapping_efforts'):
        contexts.runtime('admin', expect(f'PERFORM 1 FROM assistant.{table};', '42501'))
    check('the runtime reads capability versions and mapping efforts only through administration operations')
    check('the Messages mapping states the reasoning efforts low, medium, high, xhigh and max',
          query("SELECT array_to_string(efforts,',') FROM assistant.mapping_efforts WHERE(vendor,mapping)=('anthropic','messages_v1');")
          == 'low,medium,high,xhigh,max')
    query(f'BEGIN;{SECOND_MAPPING}ROLLBACK;')
    for efforts in ('ARRAY[]::text[]', "ARRAY['high','high']", "ARRAY[['high','max']]", "'[0:0]={high}'::text[]", "ARRAY['high',NULL]", "ARRAY['High']"):
        query(f'BEGIN;{CONFIRM_SECOND}' + expect(f"INSERT INTO assistant.mapping_efforts VALUES('anthropic','synthetic_messages_v2',{efforts});", '23514')
              + 'ROLLBACK;')
    query(expect("INSERT INTO assistant.mapping_efforts VALUES('anthropic','synthetic_unconfirmed',ARRAY['high']);", '23503'))
    query(f'BEGIN;{CONFIRM_SECOND}' + expect("UPDATE assistant.adapter_mappings SET mapping='synthetic_messages_v2' WHERE vendor='anthropic';", '23503')
          + 'ROLLBACK;')
    for statement in ('UPDATE assistant.mapping_efforts SET efforts=efforts;', 'DELETE FROM assistant.mapping_efforts;'):
        query(expect(statement, '55000'))
    check('even for the owner, a confirmed mapping states its efforts once, as a list of distinct lowercase words, '
          'and an adapter applies only a mapping whose efforts are stated')
    for sources in ("ARRAY[['https://example.invalid/usage']]", "'[0:0]={https://example.invalid/usage}'::text[]",
                    "ARRAY['https://example.invalid/usage','https://example.invalid/usage']"):
        query(expect(f"INSERT INTO assistant.confirmed_mappings VALUES('anthropic','synthetic_messages_v3','2026-10-20',{sources});", '23514'))
    check("even for the owner, a confirmed mapping's sources are a list of distinct sources")

    capable = admin.call(f'{MODELS}/add', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'identifier': 'model-synthetic-capable',
                                            'displayName': 'Synthetic capable model'})
    unadapted = admin.call(f'{MODELS}/add', {'operationId': str(uuid4()), 'vendor': 'openai', 'identifier': 'model-synthetic-unadapted',
                                              'displayName': 'Synthetic unadapted model'})
    check('an added model starts disabled and unqualified, without a capability version',
          not capable['enabled'] and not capable['qualified'] and capable['capability'] is None and capable['version'] == 1)
    for path, body in (('edit', capabilities(capable['id'], 1)), ('enable', command(capable['id'], 1)), ('disable', command(capable['id'], 1))):
        viewer.call(f'{MODELS}/{path}', body, expected=403)
    check('every model capability command requires ManageModelProviders')
    for statement in (f"PERFORM {sql_edit(capable['id'], 1)};",
                      *(f"PERFORM assistant.{name}(gen_random_uuid(),'{capable['id']}',1);" for name in ('enable_model', 'disable_model'))):
        contexts.runtime('viewer', expect(statement, '42501'))
        contexts.delegated_agent(expect(statement, '42501'))
    check('the database itself refuses every model capability command to a viewer, and to an agent even under a current delegation')
    check('a model without a capability version cannot be enabled, whatever its provider',
          refusal('enable', command(capable['id'], 1), 409, 'model_unqualified') and refusal('enable', command(unadapted['id'], 1), 409, 'model_unqualified'))
    check("a model whose vendor's adapter applies no confirmed mapping gets no capability version",
          refusal('edit', capabilities(unadapted['id'], 1), 409, 'model_mapping_unconfirmed'))

    rows = state('models', 'model_versions', 'administration_audit')
    unchanged = query(rows)
    # 4294967297 would wrap to 1 if narrowed to a database integer unchecked.
    invalid = [{'outputLimit': value} for value in (0, -1, 10000001, 2147483648, 4294967297, 9007199254740991, None)] \
        + [{'reasoningEfforts': value} for value in ([], ['turbo'], ['low', 'low'], ['Low'], ['low\0'], [None], None)] \
        + [{'acceptedServed': value} for value in (['model-synthetic-capable'], ['model-synthetic-x', 'model-synthetic-x'],
                                                   [f'model-synthetic-{n}' for n in range(17)], [None], None)] \
        + [{field: value} for field in SOURCES for value in ('http://example.invalid/synthetic', 'https://.', '', 'https://example.invalid/r\0', None)] \
        + [{field: value} for field in DATES for value in ('2026-13-01', '2026-10-1', '2999-01-01', '', None)] \
        + [{'expectedVersion': 0}, {'expectedVersion': -1}, {'expectedVersion': None}, {'operationId': EMPTY}, {'modelId': EMPTY}]
    check('each capability fact is required in its documented form: an output limit of 1 to 10,000,000 tokens, a non-empty set of '
          'the mapping\'s reasoning efforts, at most 16 accepted served identifiers other than the model\'s own, '
          'and an https source and a begun calendar date for each fact',
          all(refusal('edit', {**capabilities(capable['id'], 1), **change}, 422, 'model_admin_input') for change in invalid))
    check('an accepted served identifier must be a valid model identifier',
          all(refusal('edit', capabilities(capable['id'], 1, acceptedServed=[value]), 422, 'model_identifier_invalid')
              for value in ('bad identifier', '-leading', 'x' * 129, 'model-synthetic\0served')))
    malformed = ({'modelId': EMPTY}, {'expectedVersion': None}, {'expectedVersion': 0}, {'expectedVersion': -1}, {'operationId': EMPTY})
    check('Enable and Disable require a model, a request identity and a version of at least 1',
          all(refusal(path, {**command(capable['id'], 1), **change}, 422, 'model_admin_input') for path in ('enable', 'disable') for change in malformed))
    for path, body in (('edit', capabilities(capable['id'], 1)), ('enable', command(capable['id'], 1)), ('disable', command(capable['id'], 1))):
        admin.call(f'{MODELS}/{path}', {**body, 'note': 'unlisted'}, expected=400)
    check('a model capability request naming a member that is not part of it is a bad request')
    for efforts, served in (("ARRAY[['low','medium'],['high','max']]", 'ARRAY[]::text[]'), ("'[2:2]={low}'::text[]", 'ARRAY[]::text[]'),
                            ("ARRAY['low']", "ARRAY[['model-synthetic-a'],['model-synthetic-b']]"), ("ARRAY['low']", "'[0:0]={model-synthetic-a}'::text[]")):
        contexts.runtime('admin', refused(f"PERFORM {sql_edit(capable['id'], 1, efforts, served)};", 'model_admin_input'))
    check('the database itself refuses efforts and served identifiers that are not a list in one dimension numbered from 1')
    check('refused model capability requests write nothing', query(rows) == unchanged)

    first = capabilities(capable['id'], 1)
    recorded = admin.call(f'{MODELS}/edit', first)
    capability = recorded['capability']
    check('recording capabilities stores version 1 under the mapping the adapter applies, advances the model and leaves it disabled',
          recorded['version'] == 2 and not recorded['enabled'] and recorded['qualified'] and capability['version'] == 1
          and capability['mapping'] == 'messages_v1' and capability['outputLimit'] == 64000
          and capability['reasoningEfforts'] == ['low', 'medium', 'high'] and capability['acceptedServed'] == ['model-synthetic-capable-served']
          and all(capability[field] == first[field] for field in (*SOURCES, *DATES)) and capability['recordedBy'] and capability['recordedAt'])
    check('an identical capability retry returns the original outcome', admin.call(f'{MODELS}/edit', first) == recorded)
    check('a capability request identity cannot be reused for other facts', refusal('edit', {**first, 'outputLimit': 32000}, 409, 'model_admin_conflict'))
    check('capabilities against a stale model version are a conflict', refusal('edit', capabilities(capable['id'], 1), 409, 'model_admin_conflict'))
    check('a request identity cannot be reused for another command',
          refusal('enable', {**command(capable['id'], 2), 'operationId': first['operationId']}, 409, 'model_admin_conflict'))
    stored = json.loads(query(f"SELECT request FROM assistant.administration_audit WHERE operation_id='{first['operationId']}';"))
    check('an audited capability request keeps every fact as entered, naming its model as the other model commands do',
          stored == {k: v for k, v in first.items() if k not in ('operationId', 'modelId')} | {'action': 'edit_model', 'model': first['modelId']})

    anthropic = provider('anthropic')
    check('the earlier checks left the Messages provider qualified and disabled', anthropic['qualified'] and not anthropic['enabled'])
    check("the Providers directory states the reasoning efforts the applied mapping's adapter sends",
          anthropic['mapping']['mapping'] == 'messages_v1' and anthropic['mapping']['efforts'] == ['low', 'medium', 'high', 'xhigh', 'max'])
    check('a qualified model on a disabled provider cannot be enabled', refusal('enable', command(capable['id'], 2), 409, 'model_provider_disabled'))
    anthropic = admin.call(f'{PROVIDERS}/enable', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': anthropic['version']})
    check('Enable against a stale model version is a conflict', refusal('enable', command(capable['id'], 1), 409, 'model_admin_conflict'))
    enable = command(capable['id'], 2)
    enabled = admin.call(f'{MODELS}/enable', enable)
    check('a qualified model on an enabled, qualified provider is enabled by Enable, which advances its version',
          enabled['enabled'] and enabled['version'] == 3 and enabled['capability'] == capability)
    check('an identical Enable retry returns the original outcome', admin.call(f'{MODELS}/enable', enable) == enabled)
    check('enabling an enabled model is a conflict', refusal('enable', command(capable['id'], 3), 409, 'model_admin_conflict'))
    revised = admin.call(f'{MODELS}/edit', capabilities(capable['id'], 3, outputLimit=10000000, reasoningEfforts=['max', 'xhigh'], acceptedServed=[]))
    check('new capabilities on an enabled model record the next version and leave it enabled',
          revised['enabled'] and revised['qualified'] and revised['version'] == 4 and revised['capability']['version'] == 2
          and revised['capability']['outputLimit'] == 10000000 and revised['capability']['reasoningEfforts'] == ['max', 'xhigh']
          and revised['capability']['acceptedServed'] == [])

    # New terms leave the provider unqualified and disabled; Disable needs nothing from it.
    terms = {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': anthropic['version'],
             'accountType': 'Synthetic commercial API organization', 'retention': 'Synthetic: inputs are deleted within 7 days.',
             'retentionSource': 'https://example.invalid/r', 'retentionCheckedOn': '2026-10-01',
             'training': 'Synthetic: content is not used for training.', 'trainingSource': 'https://example.invalid/t', 'trainingCheckedOn': '2026-10-01',
             'ownerAccess': 'Synthetic: owners can view logs.', 'ownerAccessSource': 'https://example.invalid/o', 'ownerAccessCheckedOn': '2026-10-01'}
    anthropic = admin.call(f'{PROVIDERS}/terms', terms)
    check('Disable against a stale model version is a conflict', refusal('disable', command(capable['id'], 3), 409, 'model_admin_conflict'))
    disable = command(capable['id'], 4)
    disabled = admin.call(f'{MODELS}/disable', disable)
    check("Disable turns an enabled model off whatever its provider's state, and keeps its capability version",
          not anthropic['qualified'] and not anthropic['enabled'] and not disabled['enabled'] and disabled['qualified']
          and disabled['version'] == 5 and disabled['capability'] == revised['capability'])
    check('an identical Disable retry returns the original outcome', admin.call(f'{MODELS}/disable', disable) == disabled)
    check('disabling a disabled model is a conflict', refusal('disable', command(capable['id'], 5), 409, 'model_admin_conflict'))
    check('a qualified model on an unqualified provider cannot be enabled', refusal('enable', command(capable['id'], 5), 409, 'model_provider_unqualified'))
    anthropic = admin.call(f'{PROVIDERS}/attest', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': anthropic['version']})
    anthropic = admin.call(f'{PROVIDERS}/enable', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': anthropic['version']})
    check('once its provider is qualified and enabled again, Enable enables the model',
          admin.call(f'{MODELS}/enable', command(capable['id'], 5))['enabled'])

    # A release repoints the Messages adapter to the second mapping, rolled back with the check. A third,
    # confirmed after it and not applied, states other efforts, so only the applied mapping's can match.
    repoint = (SECOND_MAPPING + "UPDATE assistant.adapter_mappings SET mapping='synthetic_messages_v2' WHERE vendor='anthropic';"
               "INSERT INTO assistant.confirmed_mappings VALUES('anthropic','synthetic_messages_v3','2026-10-20',ARRAY['https://example.invalid/usage']);"
               "INSERT INTO assistant.mapping_efforts VALUES('anthropic','synthetic_messages_v3',ARRAY['low']);")
    unstated, stated = sql_edit(capable['id'], 7, "ARRAY['low']"), sql_edit(capable['id'], 7, "ARRAY['max']")
    after_repoint, recorded_again, enabled_again, directory = (json.loads(line) for line in contexts.rolled_back(
        repoint, 'admin', 'human_interactive', contexts.workspace, 'NULL',
        f"SELECT assistant.disable_model(gen_random_uuid(),'{capable['id']}',6)::text;\n"
        + refused(f"PERFORM assistant.enable_model(gen_random_uuid(),'{capable['id']}',7);", 'model_unqualified') + '\n'
        + refused(f'PERFORM {unstated};', 'model_admin_input') + '\n'
        + f'SELECT {stated}::text;\n'
        + f"SELECT assistant.enable_model(gen_random_uuid(),'{capable['id']}',8)::text;\n"
        + 'SELECT assistant.provider_directory()::text;').splitlines()[-4:])
    check("once a release repoints the adapter's mapping, Enable refuses the model and Disable still turns it off, "
          'until a version recorded under the applied mapping and its efforts qualifies it again',
          not after_repoint['qualified'] and not after_repoint['enabled'] and recorded_again['qualified']
          and recorded_again['capability']['mapping'] == 'synthetic_messages_v2' and recorded_again['capability']['reasoningEfforts'] == ['max']
          and enabled_again['enabled'])
    check("the Providers directory states the efforts of the mapping the adapter applies now",
          directory['mappings']['anthropic']['mapping'] == 'synthetic_messages_v2' and directory['mappings']['anthropic']['efforts'] == ['high', 'max'])
    listed = model('model-synthetic-capable')
    check('the directory shows each model with its state and current capability version, and a model without one as unqualified',
          listed['enabled'] and listed['qualified'] and listed['capability']['version'] == 2
          and not model('model-synthetic-unadapted')['qualified'] and model('model-synthetic-unadapted')['capability'] is None)
    audit = [(a['action'], a['identifier']) for a in admin.call(MODELS)['audit'] if a['identifier'] == 'model-synthetic-capable']
    check('the model audit lists each capability change once, newest first',
          audit[:5] == [('enable_model', 'model-synthetic-capable'), ('disable_model', 'model-synthetic-capable'),
                        ('edit_model', 'model-synthetic-capable'), ('enable_model', 'model-synthetic-capable'),
                        ('edit_model', 'model-synthetic-capable')])

    lines = contexts.second_workspace(
        refused(f"PERFORM assistant.disable_model(gen_random_uuid(),'{capable['id']}',6);", 'model_admin_conflict') + "\n"
        + refused(f"PERFORM {sql_edit(capable['id'], 6)};", 'model_admin_conflict') + "\n"
        "SELECT assistant.model_directory()::text;").splitlines()
    check("another workspace can neither see nor change this workspace's model capabilities", json.loads(lines[-1])['models'] == [])

    def version_row(**changes):
        values = {'model_id': f"'{capable['id']}'", 'version': '99', 'provider_id': f"(SELECT provider_id FROM assistant.models WHERE id='{capable['id']}')",
                  'vendor': "'anthropic'", 'mapping': "'messages_v1'", 'output_limit': '64000', 'reasoning_efforts': "ARRAY['low']",
                  'accepted_served': 'ARRAY[]::text[]', **{f'{fact}_source': "'https://example.invalid/s'" for fact in COLUMNS},
                  **{f'{fact}_checked_on': "'2026-10-01'" for fact in COLUMNS}, 'recorded_by': f"'{contexts.administrator}'", 'recorded_at': 'now()', **changes}
        return f"INSERT INTO assistant.model_versions({','.join(values)}) VALUES({','.join(values.values())});"
    query(f'BEGIN;{version_row()}ROLLBACK;')
    seventeen = 'ARRAY[' + ','.join(f"'model-synthetic-{n}'" for n in range(17)) + ']'
    for statement in (version_row(output_limit='0'), version_row(output_limit='10000001'), version_row(reasoning_efforts='ARRAY[]::text[]'),
                      version_row(reasoning_efforts="ARRAY['low','low']"), version_row(reasoning_efforts="ARRAY[['low']]"),
                      version_row(reasoning_efforts="ARRAY['turbo']"), version_row(accepted_served="ARRAY['bad identifier']"),
                      version_row(accepted_served="ARRAY['model-synthetic-x','model-synthetic-x']"),
                      version_row(accepted_served="'[0:0]={model-synthetic-x}'::text[]"), version_row(accepted_served=seventeen),
                      version_row(accepted_served="ARRAY['model-synthetic-capable']"),
                      *(version_row(**{f'{fact}_source': "'http://example.invalid/s'"}) for fact in COLUMNS),
                      *(version_row(**{f'{fact}_checked_on': date}) for fact in COLUMNS for date in ("'-infinity'", "'2999-01-01'"))):
        query(expect(statement, '23514'))
    responses = ("INSERT INTO assistant.confirmed_mappings VALUES('openai','synthetic_responses_v1','2026-10-20',ARRAY['https://example.invalid/usage']);"
                 "INSERT INTO assistant.mapping_efforts VALUES('openai','synthetic_responses_v1',ARRAY['low']);")
    openai = f"(SELECT id FROM assistant.providers WHERE vendor='openai' AND workspace_id='{contexts.workspace}')"
    for statement in (version_row(mapping="'synthetic_unconfirmed'"), CONFIRM_SECOND + version_row(mapping="'synthetic_messages_v2'"),
                      responses + version_row(vendor="'openai'", mapping="'synthetic_responses_v1'"),
                      responses + version_row(vendor="'openai'", mapping="'synthetic_responses_v1'", provider_id=openai)):
        query(expect(statement, '23503'))
    check("even for the owner, a capability version keeps each fact's form, with every fact checked on a date begun when it was recorded, "
          "names only efforts its mapping states and identifiers other than its model's own, and names its model's own provider and vendor "
          "and a mapping whose efforts are stated")

    def model_audit(action):
        return ("INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,"
                f"after_state,recorded_at,model_id) VALUES('{contexts.tenant}','{contexts.workspace}',gen_random_uuid(),'{contexts.administrator}',"
                f"'{action}',(SELECT id FROM assistant.providers WHERE vendor='anthropic' AND workspace_id='{contexts.workspace}'),'{{}}','{{}}',now(),NULL);")
    for action in ('edit_model', 'enable_model', 'disable_model'):
        query(expect(model_audit(action), '23514'))
    check('even for the owner, a model capability change names its model')
    for statement in ("UPDATE assistant.model_versions SET output_limit=output_limit;", "DELETE FROM assistant.model_versions;"):
        query(expect(statement, '55000'))
    check('capability versions are immutable')
    contexts.runtime('admin', refused(f"PERFORM assistant.enable_model('{EMPTY}','{capable['id']}',6);", 'model_admin_input'))
    check('the database itself refuses an empty operation')

    # Two sessions of one administrator send each command at once, each held at its first write:
    # the second waits for the first and replays its outcome.
    with ThreadPoolExecutor(2) as pool:
        def overlapping(path, body, operation, table):
            def overlap():
                original = pool.submit(admin.call, f'{MODELS}/{path}', body)
                waiting(query, 1, operation)
                retry = pool.submit(again.call, f'{MODELS}/{path}', body)
                waiting(query, 2, operation)
                return original, retry
            original, retry = holding(psql, environment, f'LOCK TABLE assistant.{table} IN EXCLUSIVE MODE', overlap)
            return body['operationId'], original.result(timeout=20), retry.result(timeout=20)
        outcomes = [overlapping('disable', command(capable['id'], 6), 'assistant.disable_model', 'administration_audit')]
        bounds = capabilities(capable['id'], outcomes[-1][1]['version'], outputLimit=1, acceptedServed=[f'model-synthetic-served-{n}' for n in range(16)])
        outcomes.append(overlapping('edit', bounds, 'assistant.edit_model', 'model_versions'))
        outcomes.append(overlapping('enable', command(capable['id'], outcomes[-1][1]['version']), 'assistant.enable_model', 'administration_audit'))
    operations = ','.join(f"'{operation}'" for operation, *_ in outcomes)
    check('an overlapping retry of each model capability command waits for its original and replays its outcome, writing once',
          all(original == retry for _, original, retry in outcomes)
          and query(f"SELECT count(*) FROM assistant.administration_audit WHERE operation_id IN({operations});") == '3'
          and outcomes[1][1]['capability']['version'] == 3 and outcomes[2][1]['enabled'])
    check('a version records the smallest output limit and the most accepted served identifiers',
          outcomes[1][1]['capability']['outputLimit'] == 1 and outcomes[1][1]['capability']['acceptedServed'] == bounds['acceptedServed'])
    current = outcomes[2][1]['version']
    disabled = admin.call(f'{MODELS}/disable', command(capable['id'], current))

    # Each command rechecks its authority after its model row wait and after its writes; each case names
    # the frame that must refuse it once the request context has expired.
    model_row = f"SELECT id FROM assistant.models WHERE id='{capable['id']}' FOR NO KEY UPDATE"
    audit_write = 'LOCK TABLE assistant.administration_audit IN EXCLUSIVE MODE'
    edit = f"SELECT {sql_edit(capable['id'], disabled['version'])};"
    enable = f"SELECT assistant.enable_model(gen_random_uuid(),'{capable['id']}',{disabled['version']});"
    principal = ('security.principals', 'RowShareLock')
    for name, wait, lock, statement, site, held in (
            ('Edit', 'its model row', model_row, edit, r'edit_model\(.*\) line \d+ at assignment', principal),
            ('Enable', 'its model row', model_row, enable, r'switch_model\(.*\) line \d+ at assignment', principal),
            ('Edit', 'its audit write', audit_write, edit, r'edit_model\(.*\) line \d+ at PERFORM', ('assistant.model_versions', 'RowExclusiveLock')),
            ('Enable', 'its audit write', audit_write, enable, r'switch_model\(.*\) line \d+ at PERFORM', ('assistant.models', 'RowExclusiveLock'))):
        before_rows = query(rows)
        failure = contexts.expiring(psql, environment, 'context', lock, statement, held=held)
        check(f'{name} waiting for {wait} with an expired request context is refused at its recheck',
              refused_at(failure, '42501', rf'function assistant\.{site}'))
        check(f'an expired {name} leaves every model, capability version and audit row unchanged', query(rows) == before_rows)
    print(f'{passed} model version checks passed.', flush=True)
    return passed
