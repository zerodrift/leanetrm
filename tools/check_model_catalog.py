"""Model and price administration for the page assistant; owned synthetic preview only."""
import json
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from assistant_context import Contexts, check_guard, expect, holding, migration_body, refused, refused_at, state, waiting
from preview_client import PreviewClient

MODELS = 'settings/ai/models'
# A leading no-break space, a trailing ideographic space, a right-to-left override and a line separator,
# which 0051's rule admitted, and a C1 next line, which its control class already refused.
ADMITTED_UNSAFE_NAMES = ('\u00a0Synthetic model', 'Synthetic model\u3000', 'Synthetic\u202emodel', 'Synthetic\u2028model')
UNSAFE_NAMES = (*ADMITTED_UNSAFE_NAMES, 'Synthetic\u0085model')
UNICODE_NAME = 'Mod\u00e8le \u65e5\u672c'
# Fixed instants around any plausible run date, so schedules are deterministic.
PAST, LATER, LAST = '2020-01-01T00:00:00Z', '2099-01-01T00:00:00Z', '2099-07-01T00:00:00Z'
# Synthetic rates per million tokens; no vendor's real price ships (D150).
RATES = {'uncachedInput': '1.234', 'cacheRead': '0.0123', 'cacheWrite': '2.5', 'output': '9.87'}
TIER = {'threshold': 123456, 'rates': {'uncachedInput': '2.468', 'cacheRead': '0.0246', 'cacheWrite': '5', 'output': '19.74'}}
APPEND_PRICE = ('assistant.append_price(text,uuid,uuid,integer,uuid,uuid,text,numeric,numeric,numeric,numeric,bigint,'
                'numeric,numeric,numeric,numeric,timestamp with time zone,timestamp with time zone)')


def check_upgrade(query, migration):
    check_guard(query, migration, '0051', 'model_catalog',
                (("ALTER FUNCTION assistant.provider_directory() SET work_mem='64kB';", 'assistant.provider_directory()'),
                 ('GRANT EXECUTE ON FUNCTION assistant.actor() TO openetrm_runtime;', 'assistant.actor()')))


def check_rule_upgrade(query, migration):
    check_guard(query, migration, '0053', 'model_cache_read',
                ((f"ALTER FUNCTION {APPEND_PRICE} SET work_mem='64kB';", APPEND_PRICE),
                 ('GRANT EXECUTE ON FUNCTION assistant.currency_readable(uuid,uuid,text) TO openetrm_runtime;', 'assistant.currency_readable(uuid,uuid,text)')))


def check_name_rule_upgrade(query, migration):
    check_guard(query, migration, '0059', 'model_display_name',
                (("ALTER FUNCTION assistant.is_display_name(text) SET work_mem='64kB';", 'assistant.is_display_name(text)'),
                 ('GRANT EXECUTE ON FUNCTION assistant.is_terms_text(text) TO openetrm_runtime;', 'assistant.is_terms_text(text)')))
    body = migration_body(migration)
    try:
        query(f'BEGIN ISOLATION LEVEL REPEATABLE READ;{body}ROLLBACK;')
    except RuntimeError as error:
        assert 'model_display_name: requires read committed isolation' in str(error), error
    else:
        raise AssertionError('Migration 0059 applied under repeatable read, whose snapshot can predate the writes it waits for')
    print('PASS migration 0059 requires read committed isolation, so its check sees every display name committed before it', flush=True)

    def stored(names):
        """Models stored under 0051's rule with these display names, in a workspace that rolls back with the check."""
        tenant, workspace, provider, models = uuid4(), uuid4(), uuid4(), [str(uuid4()) for _ in names]
        return models, (f"INSERT INTO security.tenants VALUES('{tenant}','synthetic_name_rule');"
                        f"INSERT INTO security.workspaces VALUES('{tenant}','{workspace}','synthetic_name_rule');"
                        f"INSERT INTO assistant.providers(id,tenant_id,workspace_id,vendor,version) VALUES('{provider}','{tenant}','{workspace}','anthropic',1);"
                        + ''.join(f"INSERT INTO assistant.models VALUES('{model}','{tenant}','{workspace}','{provider}','model-synthetic-{n}','{name}',1);"
                                  for n, (model, name) in enumerate(zip(models, names))))

    # Both rules keep their owner, its sole grant, their settings and their volatility.
    kept = ("(SELECT count(*) FROM pg_proc p WHERE p.oid IN('assistant.is_display_name(text)'::regprocedure,'assistant.is_terms_text(text)'::regprocedure)"
            " AND(pg_get_userbyid(p.proowner),p.proacl::text,p.proconfig::text,p.provolatile::text)"
            "=('openetrm_security_owner','{openetrm_security_owner=X/openetrm_security_owner}','{search_path=pg_catalog}','i'))=2")
    # Exactly 80 characters, counted as characters rather than bytes, is still a display name.
    compliant = ('Synthetic model', UNICODE_NAME, '\u65e5' * 80)
    _, rows = stored(compliant)
    names = ','.join(f"'{name}'" for name in compliant)
    query(f"BEGIN;{rows}{body}DO $$BEGIN IF (SELECT array_agg(display_name ORDER BY identifier) FROM assistant.models) IS DISTINCT FROM ARRAY[{names}]"
          f" OR NOT {kept} THEN RAISE EXCEPTION 'A stored name or a rule''s ownership changed';END IF;END$$;ROLLBACK;")
    print("PASS migration 0059 applies over stored display names that follow the rule, changing neither them nor the rules' owner and grants", flush=True)
    models, rows = stored((UNICODE_NAME, *ADMITTED_UNSAFE_NAMES))
    try:
        query(f'BEGIN;{rows}{body}ROLLBACK;')
    except RuntimeError as error:
        # Each refused model is named by identity, vendor and identifier, and no stored name is echoed.
        named = [f'{model} (anthropic model-synthetic-{n})' for n, model in enumerate(models) if n]
        assert ('model_display_name: ' in str(error) and all(m in str(error) for m in named) and models[0] not in str(error)
                and str(error).isascii()), error
    else:
        raise AssertionError('Migration 0059 applied over stored display names that break the rule')
    print('PASS migration 0059 stops on stored display names that break the rule and names each of their models', flush=True)


def run_checks(url, query, psql, manifest, environment):
    contexts = Contexts(query, manifest)
    passed = 0

    def check(name, condition=True):
        nonlocal passed
        assert condition, name
        passed += 1
        print('PASS ' + name, flush=True)

    def counts():
        return query("SELECT (SELECT count(*) FROM assistant.models)||'/'||(SELECT count(*) FROM assistant.price_revisions)"
                     "||'/'||(SELECT count(*) FROM assistant.administration_audit);")

    def add_price(model, sequence, terms='1,1,1,1,NULL,NULL,NULL,NULL,NULL', currency="gen_random_uuid(),gen_random_uuid(),'usd'"):
        # `terms` lists the four rates, the tier threshold and the four tier rates.
        return f"PERFORM assistant.add_price(gen_random_uuid(),{model},{sequence},{currency},{terms},'2020-01-01',NULL);"

    for table in ('models', 'price_revisions'):
        contexts.runtime('admin', expect(f'PERFORM 1 FROM assistant.{table};', '42501'))
    check('the runtime reads models and prices only through administration operations')
    operations = ('PERFORM assistant.model_directory();',
                  "PERFORM assistant.add_model(gen_random_uuid(),'anthropic','denied-model','Denied');",
                  "PERFORM assistant.rename_model(gen_random_uuid(),gen_random_uuid(),1,'Denied');",
                  add_price('gen_random_uuid()', 0),
                  "PERFORM assistant.withdraw_price(gen_random_uuid(),gen_random_uuid(),0,'2020-01-01',NULL);")
    for statement in operations:
        contexts.runtime('viewer', expect(statement, '42501'))
    check('model and price operations recheck ManageModelProviders in the database')
    contexts.delegated_agent('\n'.join(expect(statement, '42501') for statement in operations))
    check('an agent never administers models or prices, even under a current delegation')
    for terms in ('1e12,1,1,1,NULL,NULL,NULL,NULL,NULL', '1,1.0000000000001,1,1,NULL,NULL,NULL,NULL,NULL', '1,1,-1,1,NULL,NULL,NULL,NULL,NULL',
                  '1,1,1,1,10,1,1,1,1e12', 'NULL,1,1,1,NULL,NULL,NULL,NULL,NULL', '1,1,1,NULL,NULL,NULL,NULL,NULL,NULL'):
        contexts.runtime('admin', refused(add_price('gen_random_uuid()', 0, terms), 'model_admin_input'))
    check('the database itself refuses a rate of 10^12 or more, beyond 12 places or negative in either set, and a rate set without uncached input or output')

    admin, pricing, viewer = PreviewClient(url), PreviewClient(url), PreviewClient(url)
    for client, profile in ((admin, 'admin'), (pricing, 'admin'), (viewer, 'viewer')):
        client.bootstrap(); client.call('profile', {'profile': profile})
    # Pricing needs an operator-assigned role with ReadMarketData beside access administration (D152).
    pricing.call('admin/create', {'operationId': str(uuid4()), 'login': 'pricing_admin', 'name': 'Synthetic pricing administrator',
                                  'kind': 'human', 'roles': ['access_administrator', 'viewer']})
    pricing.call('profile', {'profile': 'pricing_admin'})
    denied = [viewer.call(MODELS, expected=403),
              viewer.call(MODELS + '/add', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'identifier': 'denied', 'displayName': 'Denied'},
                          expected=403)]
    check('model administration requires ManageModelProviders', all(d['code'] == 'access_denied' for d in denied))
    directory = admin.call(MODELS)
    check('no model, price or model audit ships, and an administrator without market-data access has no currency choices',
          directory['models'] == [] and directory['audit'] == [] and directory['currencies'] == [])
    # Sealed revisions can repeat a currency, so a choice names its catalog and revision. These two repeat USD
    # and sort one way by identifier and the other by catalog code and revision key.
    other_catalog, alternate, other = '10000000-0000-0000-0000-000000000002', '00000000-0000-4000-8000-000000000051', '00000000-0000-4000-8000-000000000052'
    query(f"INSERT INTO security.reference_catalog_scopes VALUES('{other_catalog}','{manifest['tenantId']}','{manifest['workspaceId']}','read_only');"
          + ''.join(f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{catalog}','{revision}','{key}');"
                    f"INSERT INTO ref_data.currencies VALUES('{catalog}','{revision}','usd','USD','US Dollar',2);"
                    f"UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{revision}');"
                    for catalog, revision, key in ((manifest['referenceCatalogId'], alternate, 'model_price_alternate'), (other_catalog, other, 'model_price_other'))))
    choices = pricing.call(MODELS)['currencies']
    usd = next(c for c in choices
               if (c['catalogId'], c['revisionId'], c['code']) == (manifest['referenceCatalogId'], manifest['referenceRevisionId'], 'usd'))
    named = query("SELECT g.code||' '||r.revision_key FROM ref_data.catalogs g JOIN ref_data.revisions r ON r.catalog_id=g.id "
                  f"WHERE(r.catalog_id,r.id)=('{usd['catalogId']}','{usd['revisionId']}');")
    repeated = [c for c in choices if c['displayCode'] == 'USD']
    names, identities = [(c['catalogCode'], c['revisionKey']) for c in repeated], [(c['catalogId'], c['revisionId']) for c in repeated]
    check('a pricing administrator chooses among sealed currencies it can read, labelled from the reference row and named by catalog and revision',
          usd['displayCode'] == 'USD' and usd['name'] and f"{usd['catalogCode']} {usd['revisionKey']}" == named
          and len(repeated) >= 3 and names == sorted(set(names)) and identities != sorted(identities))
    currency = {key: usd[key] for key in ('catalogId', 'revisionId', 'code')}
    usd_row = f"'{currency['catalogId']}','{currency['revisionId']}','usd'"

    def model_request(vendor, identifier, name='Synthetic model'):
        return {'operationId': str(uuid4()), 'vendor': vendor, 'identifier': identifier, 'displayName': name}

    def refusal(path, body, status, code, client=admin):
        return client.call(path, body, expected=status)['code'] == code

    existing = admin.call('settings/ai/providers')['providers']
    added = model_request('anthropic', 'model-synthetic-a', 'Synthetic model A')
    anthropic = admin.call(MODELS + '/add', added)
    check('adding a model stores its provider, identifier and display name at version 1 without a price',
          (anthropic['vendor'], anthropic['identifier'], anthropic['displayName'], anthropic['version'], anthropic['priceSequence'])
          == ('anthropic', 'model-synthetic-a', 'Synthetic model A', 1, 0))
    check('an identical retry returns the original model', admin.call(MODELS + '/add', added) == anthropic)
    check('a request identity or a provider identifier cannot be reused',
          refusal(MODELS + '/add', {**added, 'identifier': 'model-synthetic-b'}, 409, 'model_admin_conflict')
          and refusal(MODELS + '/add', model_request('anthropic', 'model-synthetic-a'), 409, 'model_admin_conflict')
          and refusal(MODELS + '/add', added, 409, 'model_admin_conflict', client=pricing))
    openai = admin.call(MODELS + '/add', model_request('openai', 'model-synthetic-a', 'Same identifier at another vendor'))
    check('an identifier is unique per provider, not across providers', openai['vendor'] == 'openai' and openai['id'] != anthropic['id'])
    current = admin.call('settings/ai/providers')['providers']
    check("adding a model leaves an existing provider's version and credential unchanged",
          any(p['vendor'] == 'anthropic' and p['version'] > 0 for p in existing) and all(p in current for p in existing))
    before = counts()
    check('a model identifier follows the documented pattern',
          all(refusal(MODELS + '/add', model_request('anthropic', identifier), 422, 'model_identifier_invalid')
              for identifier in ('', '-leading', 'with space', 'a' * 129, 'model/1', 'model\n')))
    check('a display name is trimmed, at most 80 characters, without control characters, and a vendor is supported',
          all(refusal(MODELS + '/add', {**model_request('anthropic', 'model-synthetic-c'), **body}, 422, 'model_admin_input')
              for body in ({'displayName': ''}, {'displayName': ' padded'}, {'displayName': 'n' * 81}, {'displayName': 'tab\tname'},
                           {'displayName': None}, {'identifier': None}, {'vendor': 'acme'}, {'vendor': None}))
          and counts() == before)
    admin.call(MODELS + '/add', {**model_request('anthropic', 'model-synthetic-c'), 'note': 'unlisted'}, expected=400)

    rename = {'operationId': str(uuid4()), 'modelId': anthropic['id'], 'expectedVersion': 1, 'displayName': 'Synthetic model A renamed'}
    renamed = admin.call(MODELS + '/rename', rename)
    check('a rename changes only the display name and advances the model version',
          (renamed['id'], renamed['identifier'], renamed['displayName'], renamed['version'])
          == (anthropic['id'], anthropic['identifier'], 'Synthetic model A renamed', 2))
    check('an identical rename retry returns its original outcome', admin.call(MODELS + '/rename', rename) == renamed)
    check('a stale or unknown model version is a conflict and an invalid name is refused',
          refusal(MODELS + '/rename', {**rename, 'operationId': str(uuid4())}, 409, 'model_admin_conflict')
          and refusal(MODELS + '/rename', {**rename, 'operationId': str(uuid4()), 'modelId': str(uuid4()), 'expectedVersion': 1}, 409, 'model_admin_conflict')
          and refusal(MODELS + '/rename', {**rename, 'operationId': str(uuid4()), 'expectedVersion': 2, 'displayName': ''}, 422, 'model_admin_input'))
    before = counts()
    check('text with a NUL is malformed input, refused before it reaches the database',
          refusal(MODELS + '/add', model_request('anthropic', 'model-synthetic-c', 'nul\x00name'), 422, 'model_admin_input')
          and refusal(MODELS + '/add', model_request('anthropic', 'model\x00synthetic'), 422, 'model_identifier_invalid')
          and refusal(MODELS + '/rename', {**rename, 'operationId': str(uuid4()), 'expectedVersion': 2, 'displayName': 'nul\x00name'}, 422, 'model_admin_input')
          and counts() == before)
    unicode_model = admin.call(MODELS + '/add', model_request('anthropic', 'model-synthetic-unicode', UNICODE_NAME))
    check('a display name may hold letters beyond ASCII, kept exactly', unicode_model['displayName'] == UNICODE_NAME)
    before = counts()
    check('a display name with a space at either end, a C1 control, a bidirectional override or a line separator is refused on add and rename',
          all(refusal(MODELS + '/add', model_request('anthropic', 'model-synthetic-c', name), 422, 'model_admin_input')
              and refusal(MODELS + '/rename', {**rename, 'operationId': str(uuid4()), 'expectedVersion': 2, 'displayName': name}, 422, 'model_admin_input')
              for name in UNSAFE_NAMES)
          and counts() == before)
    listing = admin.call(MODELS)
    check('an unpriced model shows no price from now on',
          [(m['priceSequence'], m['prices'], m['schedule']) for m in listing['models'] if m['id'] == anthropic['id']]
          == [(0, [], [{'from': listing['asOf'], 'to': None, 'sequence': None, 'priced': False}])])
    check('the directory states which models must price cache reads, as Add price requires',
          {m['vendor']: m['cacheReadRequired'] for m in listing['models']} == {'anthropic': False, 'openai': True})

    def price_request(model, sequence, rates=RATES, tier=None, starts=PAST, ends=None, chosen=None):
        return {'operationId': str(uuid4()), 'modelId': model, 'expectedSequence': sequence, 'currency': chosen or currency,
                'rates': rates, 'tier': tier, 'effectiveFrom': starts, 'effectiveTo': ends}

    def withdrawal(model, sequence, starts, ends=None):
        return {'operationId': str(uuid4()), 'modelId': model, 'expectedSequence': sequence, 'effectiveFrom': starts, 'effectiveTo': ends}

    def without(body, member):
        return {key: value for key, value in body.items() if key != member}

    def schedule(directory, model):
        # The model's segments from the directory's instant, which shows as 'now'.
        return [('now' if s['from'] == directory['asOf'] else s['from'][:19], s['to'] and s['to'][:19], s['sequence'], s['priced'])
                for m in directory['models'] if m['id'] == model for s in m['schedule']]

    before = counts()
    check('pricing rechecks market-data access to the currency catalog',
          refusal(MODELS + '/price', price_request(anthropic['id'], 0), 422, 'currency_unavailable') and counts() == before)
    first_price = price_request(anthropic['id'], 0)
    first = pricing.call(MODELS + '/price', first_price)
    check('a price revision binds its model, sequence, exact currency, rates, effective interval and recorder',
          first['model']['priceSequence'] == 1 and (first['price']['sequence'], first['price']['kind']) == (1, 'rates')
          and first['price']['rates'] == RATES and first['price']['tier'] is None
          and {k: first['price']['currency'][k] for k in currency} == currency
          and first['price']['effectiveTo'] is None and first['price']['effectiveFrom'].startswith('2020-01-01T00:00:00')
          and first['price']['recordedBy'] == 'Synthetic pricing administrator')
    check('an identical price retry returns its original outcome, comparing rates by value',
          pricing.call(MODELS + '/price', first_price) == first
          and pricing.call(MODELS + '/price', {**first_price, 'rates': {**RATES, 'uncachedInput': '1.2340'}}) == first)
    check('a price request identity cannot be reused, and a sequence behind or ahead of the stored one is a conflict',
          refusal(MODELS + '/price', {**first_price, 'rates': {**RATES, 'output': '9.88'}}, 409, 'model_admin_conflict', client=pricing)
          and refusal(MODELS + '/price', price_request(anthropic['id'], 0), 409, 'model_admin_conflict', client=pricing)
          and refusal(MODELS + '/price', price_request(anthropic['id'], 5), 409, 'model_admin_conflict', client=pricing))

    before = counts()
    malformed = [{'rates': {**RATES, 'output': value}} for value in ('-1', '1.0000000000001', '1000000000000', '01', '1.', '+1', '1e3', '')]
    malformed += [{'rates': {**RATES, 'uncachedInput': None}}, {'rates': {**RATES, 'output': None}},
                  {'tier': {**TIER, 'rates': {**TIER['rates'], 'cacheWrite': None}}}, {'tier': {**TIER, 'threshold': 0}},
                  {'tier': {**TIER, 'threshold': 10 ** 12}}, {'tier': {**TIER, 'threshold': None}}, {'tier': {**TIER, 'rates': None}},
                  {'currency': {**currency, 'code': 'us\x00d'}},
                  {'effectiveFrom': LATER, 'effectiveTo': LATER}, {'effectiveFrom': LAST, 'effectiveTo': LATER},
                  {'effectiveFrom': '2099-01-01T00:00:00+01:00'}, {'effectiveFrom': '2099-01-01T00:00:00'}, {'effectiveFrom': None},
                  {'effectiveFrom': '2099-01-01T00:00:00.0000001Z'}, {'effectiveTo': '2099-07-01T00:00:00.0000001Z'},
                  {'effectiveFrom': '9999-12-31T00:00:00Z'}, {'effectiveTo': '9999-12-31T00:00:00Z'}]
    check('malformed rates, tiers, text or intervals are refused before any write, and cache read is required where caching is automatic',
          all(refusal(MODELS + '/price', {**price_request(anthropic['id'], 1), **body}, 422, 'model_admin_input', client=pricing) for body in malformed)
          and refusal(MODELS + '/price', price_request(openai['id'], 0, rates={**RATES, 'cacheRead': None}), 422, 'model_admin_input', client=pricing)
          and counts() == before)
    complete = price_request(anthropic['id'], 1)
    # A member repeated under another case binds the same field, so it is as ambiguous as an exact duplicate.
    for body in ({**complete, 'rates': {**RATES, 'output': 15}}, without(complete, 'tier'), without(complete, 'effectiveTo'),
                 *({**complete, 'rates': without(RATES, member)} for member in RATES), {**complete, 'rates': {**RATES, 'Output': '98.7'}}):
        pricing.call(MODELS + '/price', body, expected=400)
    check('a price request names every member once, null where it means not offered, no tier or open-ended', counts() == before)
    draft_revision, foreign_catalog, foreign_revision = uuid4(), uuid4(), uuid4()
    query(f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{manifest['referenceCatalogId']}','{draft_revision}','model_price_draft');"
          f"INSERT INTO ref_data.currencies VALUES('{manifest['referenceCatalogId']}','{draft_revision}','usd','USD','US Dollar',2);"
          f"INSERT INTO ref_data.catalogs(id,code,description) VALUES('{foreign_catalog}','model_price_foreign','Synthetic unscoped catalog');"
          f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{foreign_catalog}','{foreign_revision}','foreign_v1');"
          f"INSERT INTO ref_data.currencies VALUES('{foreign_catalog}','{foreign_revision}','usd','USD','US Dollar',2);"
          f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{foreign_catalog}' AND id='{foreign_revision}';")
    unavailable = [{**currency, 'code': 'xyz'}, {**currency, 'revisionId': str(draft_revision)},
                   {'catalogId': str(foreign_catalog), 'revisionId': str(foreign_revision), 'code': 'usd'}]
    check('a currency that is unknown, in an unsealed revision or in an unscoped catalog is unavailable',
          all(refusal(MODELS + '/price', price_request(anthropic['id'], 1, chosen=chosen), 422, 'currency_unavailable', client=pricing)
              for chosen in unavailable) and counts() == before)

    later = pricing.call(MODELS + '/price', price_request(anthropic['id'], 1, rates={**RATES, 'cacheRead': None},
                                                           tier={**TIER, 'rates': {**TIER['rates'], 'cacheRead': None}}, starts=LATER, ends=LAST))
    correction = pricing.call(MODELS + '/price', price_request(anthropic['id'], 2, rates={**RATES, 'output': '9.870000000001'}, ends=LATER))
    check('a tier carries a complete second rate set, and cache read may be not offered on the Messages API',
          later['price']['tier'] == {'threshold': 123456, 'rates': {**TIER['rates'], 'cacheRead': None}} and later['price']['rates']['cacheRead'] is None
          and correction['price']['rates']['output'] == '9.870000000001')
    openai_later = pricing.call(MODELS + '/price', price_request(openai['id'], 0, starts=LATER))
    listing = pricing.call(MODELS)
    check('the schedule from now pins the highest sequence in force at each instant, and later appends never reprice outside their interval',
          schedule(listing, anthropic['id']) == [('now', '2099-01-01T00:00:00', 3, True), ('2099-01-01T00:00:00', '2099-07-01T00:00:00', 2, True),
                                                 ('2099-07-01T00:00:00', None, 1, True)])
    check('a model whose only price starts later has no price until then',
          schedule(listing, openai['id']) == [('now', '2099-01-01T00:00:00', None, False), ('2099-01-01T00:00:00', None, 1, True)]
          and {m['id'] for m in listing['models']} == {anthropic['id'], openai['id'], unicode_model['id']} and openai_later['price']['sequence'] == 1)
    superseding = pricing.call(MODELS + '/price', price_request(openai['id'], 1))
    check('a correction covering later boundaries merges them into one segment',
          superseding['price']['sequence'] == 2 and schedule(pricing.call(MODELS), openai['id']) == [('now', None, 2, True)])
    pinned = query(f"SELECT string_agg(coalesce((assistant.price_in_force('{anthropic['id']}',t)).sequence::text,'none'),',' ORDER BY t) "
                   "FROM unnest(ARRAY['2019-12-31 23:59:59.999999+00','2020-01-01 00:00:00+00','2098-12-31 23:59:59.999999+00',"
                   "'2099-01-01 00:00:00+00','2099-06-30 23:59:59.999999+00','2099-07-01 00:00:00+00']::timestamptz[]) t;")
    check('pinning uses half-open intervals at every boundary', pinned == 'none,3,3,2,2,1')

    withdraw = withdrawal(openai['id'], 2, LATER, LAST)
    withdrawn = admin.call(MODELS + '/withdraw', withdraw)
    check('a withdrawal needs no market-data access and records only its interval',
          withdrawn['model']['priceSequence'] == 3
          and {k: withdrawn['price'][k] for k in ('sequence', 'kind', 'currency', 'rates', 'tier')}
          == {'sequence': 3, 'kind': 'withdrawal', 'currency': None, 'rates': None, 'tier': None}
          and (withdrawn['price']['effectiveFrom'][:19], withdrawn['price']['effectiveTo'][:19]) == ('2099-01-01T00:00:00', '2099-07-01T00:00:00'))
    check('an identical withdrawal retry returns its original outcome; a reused identity, stale sequence or unknown model is a conflict',
          admin.call(MODELS + '/withdraw', withdraw) == withdrawn
          and refusal(MODELS + '/withdraw', {**withdraw, 'effectiveTo': None}, 409, 'model_admin_conflict')
          and refusal(MODELS + '/price', {**price_request(openai['id'], 3), 'operationId': withdraw['operationId']}, 409, 'model_admin_conflict')
          and refusal(MODELS + '/withdraw', withdrawal(openai['id'], 2, LATER), 409, 'model_admin_conflict')
          and refusal(MODELS + '/withdraw', withdrawal(str(uuid4()), 0, LATER), 409, 'model_admin_conflict'))
    before = counts()
    check('a withdrawal interval is explicit, ordered and in range at microsecond precision',
          all(refusal(MODELS + '/withdraw', {**withdrawal(openai['id'], 3, LATER), **body}, 422, 'model_admin_input')
              for body in ({'effectiveFrom': None}, {'effectiveTo': LATER}, {'effectiveFrom': LAST, 'effectiveTo': LATER},
                           {'effectiveFrom': '2099-01-01T00:00:00+01:00'}, {'effectiveFrom': '9999-12-31T00:00:00Z'},
                           {'effectiveTo': '2099-07-01T00:00:00.0000001Z'}, {'expectedSequence': -1}))
          and counts() == before)
    admin.call(MODELS + '/withdraw', without(withdrawal(openai['id'], 3, LATER), 'effectiveTo'), expected=400)
    listing = pricing.call(MODELS)
    check('a withdrawal governs only its interval: calls starting there are unpriced, and earlier revisions price the rest',
          schedule(listing, openai['id']) == [('now', '2099-01-01T00:00:00', 2, True), ('2099-01-01T00:00:00', '2099-07-01T00:00:00', 3, False),
                                              ('2099-07-01T00:00:00', None, 2, True)])
    pinned = query(f"SELECT string_agg(r.kind||':'||r.sequence,',' ORDER BY t) FROM unnest(ARRAY['2098-12-31 23:59:59.999999+00',"
                   "'2099-01-01 00:00:00+00','2099-06-30 23:59:59.999999+00','2099-07-01 00:00:00+00']::timestamptz[]) t,"
                   f"LATERAL assistant.price_in_force('{openai['id']}',t) r;")
    check('the revision in force throughout a withdrawal is the withdrawal', pinned == 'rates:2,withdrawal:3,withdrawal:3,rates:2')
    prices = [p for m in listing['models'] for p in m['prices']]
    check('revision labels come from the reference rows the reader can see, and a withdrawal has no currency',
          all(p['currency']['displayCode'] == 'USD' for p in prices if p['kind'] == 'rates')
          and [p['currency'] for p in prices if p['kind'] == 'withdrawal'] == [None]
          and all(p['currency']['displayCode'] is None for m in admin.call(MODELS)['models'] for p in m['prices'] if p['kind'] == 'rates'))

    audit, provider_audit = pricing.call(MODELS)['audit'], admin.call('settings/ai/providers')['audit']
    check('the model audit lists each change once, newest first, with its model, and the provider audit lists none of them',
          [(a['action'], a['vendor'], a['identifier']) for a in audit]
          == [('withdraw_price', 'openai', 'model-synthetic-a'), ('add_price', 'openai', 'model-synthetic-a'),
              ('add_price', 'openai', 'model-synthetic-a'), ('add_price', 'anthropic', 'model-synthetic-a'),
              ('add_price', 'anthropic', 'model-synthetic-a'), ('add_price', 'anthropic', 'model-synthetic-a'),
              ('add_model', 'anthropic', 'model-synthetic-unicode'), ('rename_model', 'anthropic', 'model-synthetic-a'),
              ('add_model', 'openai', 'model-synthetic-a'), ('add_model', 'anthropic', 'model-synthetic-a')]
          and provider_audit and all(a['action'] == 'replace_key' for a in provider_audit))

    lines = contexts.second_workspace(
        refused(f"PERFORM assistant.rename_model(gen_random_uuid(),'{anthropic['id']}',2,'Synthetic other workspace');", 'model_admin_conflict') + "\n"
        + refused(add_price(f"'{anthropic['id']}'", 3, currency=usd_row), 'model_admin_conflict') + "\n"
        + refused(f"PERFORM assistant.withdraw_price(gen_random_uuid(),'{anthropic['id']}',3,'2020-01-01',NULL);", 'model_admin_conflict') + "\n"
        "SELECT assistant.model_directory()::text;\n"
        "SELECT assistant.add_model(gen_random_uuid(),'meta','model-synthetic-d','Synthetic model D')::text;\n"
        + refused("PERFORM assistant.credential_slot(gen_random_uuid(),'meta',0,repeat('0',64));", 'model_admin_conflict') + "\n"
        "SELECT assistant.provider_directory()::text;\n"
        "SELECT assistant.credential_slot(gen_random_uuid(),'meta',1,repeat('0',64))::text;\n"
        # A model of each automatically caching vendor, priced without cache read; prints nothing.
        "DO $$ DECLARE v assistant.vendor; m uuid; BEGIN FOREACH v IN ARRAY ARRAY['google','xai','meta']::assistant.vendor[] LOOP\n"
        " m=(assistant.add_model(gen_random_uuid(),v,'model-synthetic-cache','Synthetic cache model')->>'id')::uuid; BEGIN\n"
        " PERFORM assistant.add_price(gen_random_uuid(),m,0,gen_random_uuid(),gen_random_uuid(),'usd',1,NULL,1,1,NULL,NULL,NULL,NULL,NULL,'2020-01-01',NULL);\n"
        " RAISE EXCEPTION 'Expected rejection';\n"
        "EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'model_admin_input' THEN RAISE; END IF; END; END LOOP; END $$;\n"
        "SELECT assistant.model_directory()::text;").splitlines()
    isolated, created, providers, slot, catalog = (json.loads(line) for line in lines[-5:])
    check("another workspace can neither see nor change this workspace's models, and adding its first model creates that provider without a credential",
          isolated['models'] == [] and isolated['audit'] == [] and created['vendor'] == 'meta' and created['version'] == 1
          and [(p['vendor'], p['version'], p['credential']) for p in providers['providers']] == [('meta', 1, None)])
    check('a provider created by Add model takes its first key at provider version 1', slot['credentialVersion'] == 1)
    check('a rate set offers cache read on every protocol that caches automatically, as the directory states',
          {(m['vendor'], m['cacheReadRequired']) for m in catalog['models']} == {('google', True), ('xai', True), ('meta', True)})

    # Two sessions of one administrator send each model command at once: the second waits for the
    # first and replays its outcome. Pricing needs market-data access, so the administrator gains a viewer grant.
    query(f"INSERT INTO security.role_grants VALUES('{uuid4()}','{contexts.administrator}',(SELECT id FROM security.roles WHERE code='viewer'),"
          f"'{contexts.tenant}','{contexts.workspace}',CURRENT_TIMESTAMP-INTERVAL '1 day',CURRENT_TIMESTAMP+INTERVAL '1 day',false);")
    again = PreviewClient(url); again.bootstrap(); again.call('profile', {'profile': 'admin'})
    commands = [(MODELS + '/add', model_request('anthropic', 'model-synthetic-overlap'), 'assistant.add_model'),
                (MODELS + '/rename', {**rename, 'operationId': str(uuid4()), 'expectedVersion': 2, 'displayName': 'Synthetic model A overlap'},
                 'assistant.rename_model'),
                (MODELS + '/price', price_request(anthropic['id'], 3, ends=LATER), 'assistant.add_price'),
                (MODELS + '/withdraw', withdrawal(anthropic['id'], 4, LAST), 'assistant.withdraw_price')]
    with ThreadPoolExecutor(2) as pool:
        def overlapping(path, body, operation):
            def overlap():
                original = pool.submit(admin.call, path, body)
                waiting(query, 1, operation)
                duplicate = pool.submit(again.call, path, body)
                waiting(query, 2, operation)
                return original, duplicate
            original, duplicate = holding(psql, environment, 'LOCK TABLE assistant.models IN EXCLUSIVE MODE', overlap)
            return original.result(timeout=20), duplicate.result(timeout=20)
        outcomes = [overlapping(*command) for command in commands]

        def race():
            # The first administrator holds the model while it waits to store its revision, so
            # the second reads the sequence only once the first has committed.
            winner = pool.submit(admin.call, MODELS + '/price', price_request(anthropic['id'], 5))
            waiting(query, 1, 'assistant.add_price')
            loser = pool.submit(pricing.call, MODELS + '/price', price_request(anthropic['id'], 5), expected=409)
            waiting(query, 2, 'assistant.add_price')
            return winner, loser
        winner, loser = holding(psql, environment, 'LOCK TABLE assistant.price_revisions IN EXCLUSIVE MODE', race)
        won, lost = winner.result(timeout=20), loser.result(timeout=20)
    settled = {m['id']: m for m in admin.call(MODELS)['models']}
    check('an overlapping retry of each model command waits for its original and replays its outcome, writing once',
          all(first == second for first, second in outcomes)
          and sum(m['identifier'] == 'model-synthetic-overlap' for m in settled.values()) == 1 and settled[anthropic['id']]['version'] == 3)
    check('two administrators appending to one model at once store one revision, and the other is a conflict',
          won['price']['sequence'] == 6 and lost['code'] == 'model_admin_conflict' and settled[anthropic['id']]['priceSequence'] == 6)
    # Direct calls from a session in another time zone, retried through the API, which uses UTC.
    price_retry, withdrawal_retry = price_request(anthropic['id'], 6, starts=LAST), withdrawal(anthropic['id'], 7, '2099-08-01T00:00:00Z')
    direct_add = (f"SELECT assistant.add_price('{price_retry['operationId']}','{anthropic['id']}',6,{usd_row},1.234,0.0123,2.5,9.87,"
                  "NULL,NULL,NULL,NULL,NULL,'2099-07-01 00:00:00+00',NULL)::text;")
    *_, direct_price, direct_withdrawal, as_of, key_replaced = contexts.runtime('admin', "SET TimeZone='America/New_York';" + direct_add +
        f"SELECT assistant.withdraw_price('{withdrawal_retry['operationId']}','{anthropic['id']}',7,'2099-08-01 00:00:00+00',NULL)::text;"
        "SELECT assistant.model_directory()->>'asOf';SELECT assistant.provider_directory()->'audit'->0->>'at';").splitlines()
    check("an outcome never depends on its caller's time zone, so the API replays a direct call's retry",
          (admin.call(MODELS + '/price', price_retry), admin.call(MODELS + '/withdraw', withdrawal_retry))
          == (json.loads(direct_price), json.loads(direct_withdrawal))
          and json.loads(direct_price)['price']['effectiveFrom'] == '2099-07-01T00:00:00+00:00'
          and as_of.endswith('+00:00') and key_replaced.endswith('+00:00'))

    # As credential replacement does (0045), each command rechecks its authority after its principal
    # and model row waits, before it replays an earlier outcome, and after its writes. Each case lets
    # the administrator's authority expire while the command waits at the last point its recheck
    # must follow, and names the frame that must refuse it.
    rows = state('models', 'price_revisions', 'providers', 'administration_audit')
    sequence, version = (query(f"SELECT {column} FROM assistant.models m WHERE id='{anthropic['id']}';")
                         for column in ('(SELECT count(*) FROM assistant.price_revisions r WHERE r.model_id=m.id)', 'version'))
    locks = {'principal': f"SELECT id FROM security.principals WHERE id='{contexts.administrator}' FOR NO KEY UPDATE",
             'model': f"SELECT id FROM assistant.models WHERE id='{anthropic['id']}' FOR NO KEY UPDATE",
             # Only ACCESS EXCLUSIVE holds back the replay lookup. It would also hold a new session's
             # first compilation of replayed, whose variable has the audit row type, so that is warmed.
             'audit lookup': 'LOCK TABLE assistant.administration_audit IN ACCESS EXCLUSIVE MODE',
             # EXCLUSIVE lets a command compile and read, so only its audit write, its last, waits.
             'audit write': 'LOCK TABLE assistant.administration_audit IN EXCLUSIVE MODE'}
    warmed = {'audit lookup': 'DO $$DECLARE previous assistant.administration_audit; BEGIN END $$;'}
    # What each command already holds where it waits: past its principal lock, or past its write before the audit's.
    principal, models, revisions = ('security.principals', 'RowShareLock'), ('assistant.models', 'RowExclusiveLock'), \
        ('assistant.price_revisions', 'RowExclusiveLock')
    every, market = 'true', "role_id=(SELECT id FROM security.roles WHERE code='viewer')"
    for refusing, lock, expiry, grants, statement, code, site, held in (
            ('an existing identifier', 'principal', 'grant', every,
             "SELECT assistant.add_model(gen_random_uuid(),'anthropic','model-synthetic-a','Synthetic duplicate');",
             '42501', r'function assistant\.replayed\(.*\) line \d+ at assignment', None),
            ('a stale version', 'model', 'context', every, f"SELECT assistant.rename_model(gen_random_uuid(),'{anthropic['id']}',1,'Synthetic stale rename');",
             '42501', r'function assistant\.rename_model\(.*\) line \d+ at assignment', principal),
            ('a stale sequence', 'model', 'grant', every, add_price(f"'{anthropic['id']}'", 1, currency=usd_row).replace('PERFORM', 'SELECT'),
             '42501', r'function assistant\.append_price\(.*\) line \d+ at assignment', principal),
            ('a replay of an earlier price', 'audit lookup', 'context', every, direct_add,
             '42501', r'function assistant\.replayed\(.*\) line \d+ at assignment', principal),
            ('a stored model', 'audit write', 'context', every,
             "SELECT assistant.add_model(gen_random_uuid(),'anthropic','model-synthetic-expiry','Synthetic expiry');",
             '42501', r'function assistant\.add_model\(.*\) line \d+ at PERFORM', models),
            ('a stored rename', 'audit write', 'grant', every,
             f"SELECT assistant.rename_model(gen_random_uuid(),'{anthropic['id']}',{version},'Synthetic expiry rename');",
             '42501', r'function assistant\.rename_model\(.*\) line \d+ at PERFORM', models),
            ('a stored withdrawal', 'audit write', 'grant', every,
             f"SELECT assistant.withdraw_price(gen_random_uuid(),'{anthropic['id']}',{sequence},'2099-09-01 00:00:00+00',NULL);",
             '42501', r'function assistant\.append_price\(.*\) line \d+ at PERFORM', revisions),
            # Only the market-data grant expires, after the first currency check: the write waits after it.
            ('a stored rate set whose currency read expired', 'audit write', 'grant', market,
             add_price(f"'{anthropic['id']}'", sequence, currency=usd_row).replace('PERFORM', 'SELECT'),
             'currency_unavailable', r'function assistant\.append_price\(.*\) line \d+ at RAISE', revisions)):
        before = query(rows)
        failure = contexts.expiring(psql, environment, expiry, locks[lock], statement, grants, warmed.get(lock), held)
        check(f'{lock} wait with expired {expiry} authority refuses {refusing} at its recheck', refused_at(failure, code, site))
        check('an expired model command leaves every model, price, provider and audit row unchanged', query(rows) == before)

    def audit_entry(action, provider, model, digest='NULL'):
        return ("INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,key_digest,"
                f"after_state,recorded_at,model_id) VALUES('{contexts.tenant}','{contexts.workspace}',gen_random_uuid(),'{contexts.administrator}',"
                f"'{action}',{provider},'{{}}',{digest},'{{}}',now(),{model});")

    def provider(vendor):
        return f"(SELECT id FROM assistant.providers WHERE vendor='{vendor}')"

    def revision(kind, terms):
        # `terms` lists the currency's catalog, revision and code, the four rates, the tier threshold and the four tier rates.
        return f"INSERT INTO assistant.price_revisions VALUES('{anthropic['id']}',99,'{kind}',{terms},'2020-01-01',NULL,'{contexts.administrator}',now());"

    model, digest = f"'{anthropic['id']}'", "repeat('0',64)"
    query(expect(audit_entry('add_price', provider('openai'), model), '23503'))
    for statement in (audit_entry('add_price', provider('anthropic'), 'NULL'), audit_entry('add_price', 'NULL', model),
                      audit_entry('add_price', provider('anthropic'), model, digest), audit_entry('replace_key', provider('anthropic'), model, digest)):
        query(expect(statement, '23514'))
    check("even for the owner, a model change names its model and that model's own provider without a key, and a key replacement names no model")
    no_tier = 'NULL,NULL,NULL,NULL,NULL'
    for kind, terms in (('rates', f'{usd_row},1,1,1,NULL,{no_tier}'), ('rates', f'NULL,NULL,NULL,1,1,1,1,{no_tier}'),
                        ('rates', f'{usd_row},1,1,1,1,10,1,1,NULL,1'), ('rates', f'{usd_row},1,1,1,1,1000000000000,1,1,1,1'),
                        ('withdrawal', f'{usd_row},NULL,NULL,NULL,NULL,{no_tier}'), ('withdrawal', f'NULL,NULL,NULL,1,NULL,NULL,NULL,{no_tier}'),
                        ('estimate', f'NULL,NULL,NULL,NULL,NULL,NULL,NULL,{no_tier}')):
        query(expect(revision(kind, terms), '23514'))
    check('even for the owner, a rate set names its currency, uncached input and output and any tier below 10^12 tokens offers its dimensions, '
          'a withdrawal names none of them, and no other kind exists')
    for name in UNSAFE_NAMES:
        query(expect(f"UPDATE assistant.models SET display_name='{name}',version=version+1 WHERE id={model};", '23514'))
    check('even for the owner, a stored display name follows the same rule')
    # A session without standard conforming strings must read the rule's escapes the same way.
    lax = 'SET standard_conforming_strings=off;'
    query(lax + expect(f"UPDATE assistant.models SET display_name='{UNSAFE_NAMES[0]}',version=version+1 WHERE id={model};", '23514'))
    check('both rules hold in a session without standard conforming strings',
          query(lax + "SELECT assistant.is_display_name('Synthetic model') AND assistant.is_terms_text('Synthetic terms');") == 't')
    for statement in (f"UPDATE assistant.price_revisions SET output=1 WHERE model_id={model} AND kind='rates';",
                      f"DELETE FROM assistant.price_revisions WHERE model_id={model};",
                      # Advancing the version leaves only the identity rule to refuse these changes.
                      f"UPDATE assistant.models SET identifier='renamed',version=version+1 WHERE id={model};",
                      f"UPDATE assistant.models SET provider_id=(SELECT provider_id FROM assistant.models WHERE id='{openai['id']}'),version=version+1 WHERE id={model};",
                      f"UPDATE assistant.models SET version=version+2 WHERE id={model};",
                      f"DELETE FROM assistant.models WHERE id={model};"):
        query(expect(statement, '55000'))
    check('price revisions are immutable and a model keeps its identity, advancing one version at a time, even for the owner')
    print(f'{passed} model catalog checks passed.', flush=True)
