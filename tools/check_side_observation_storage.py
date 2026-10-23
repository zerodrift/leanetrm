"""Exact foreign keys, raw admission and current-only Side observation writes."""
import json
from copy import deepcopy
from uuid import uuid4


def run_checks(query, manifest, observed, weighted):
    catalog = manifest['catalogId']
    reference = manifest['referenceCatalogId']
    revision = manifest['referenceRevisionId']

    def reject(statement, state, message=None):
        guard = 'NULL;' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE;END IF;"
        return f"DO $$BEGIN {statement} RAISE EXCEPTION 'Expected raw rejection'; EXCEPTION WHEN SQLSTATE '{state}' THEN {guard}END$$;"

    def literal(value):
        return "'" + str(value).replace("'", "''") + "'"

    def command(body):
        a, b = body['fixed'], body['floating']
        arguments = [catalog, str(uuid4()), None, None, body['bookId'], body['definitionRevisionId'], reference, revision,
                     b['curve'], b['index'], a['currency'], b['currency'], a['deliveryFrom'], a['deliveryTo'], '2021-02-18T00:00:00Z',
                     b['deliveryFrom'], b['deliveryTo'], '2021-02-18T00:00:00Z', b['averagingMethod'], a['price'], body['quantity'], '-1', False,
                     '2020-12-18T16:00:00Z', None, '2020-12-18T16:00:00Z', 'Initial booking', body['convention'],
                     json.dumps(a['paymentSchedule']), json.dumps(b['paymentSchedule']), body['commodity'], None, None, None,
                     json.dumps({key: body[key] for key in ('legalEntityId', 'legalEntityRevision', 'counterpartyId', 'counterpartyRevision')}),
                     '{}', None, json.dumps(b.get('observationSchedule'))]
        return 'PERFORM security.save_observation_swap_with_parties(' + ','.join('NULL' if value is None else 'false' if value is False else literal(value) for value in arguments) + ');'

    def runtime(statement):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement}COMMIT;"

    def composed(first, second):
        parties = {key: weighted[key] for key in ('legalEntityId', 'legalEntityRevision', 'counterpartyId', 'counterpartyRevision')}
        arguments = [catalog, str(uuid4()), None, None, weighted['bookId'], weighted['definitionRevisionId'], reference, revision,
                     weighted['commodity'], None, json.dumps(first), json.dumps(second), '100', '1', False,
                     '2020-12-18T16:00:00Z', '2020-12-18T16:00:00Z', 'Initial booking', weighted['convention'], None,
                     json.dumps(parties), '{}']
        return 'PERFORM security.save_composed_swap(' + ','.join('NULL' if value is None else 'false' if value is False else literal(value) for value in arguments) + ');'

    before = query('SELECT count(*) FROM trading.publications;')
    bad = deepcopy(observed); bad['floating']['observationSchedule']['calendar'] = None
    query(runtime(reject(command(bad), 'P0001', 'swap_averaging')))
    bad = deepcopy(weighted); bad['floating']['observationSchedule']['quantities'][0]['quantity']['value'] = '0'
    query(runtime(reject(command(bad), 'P0001', 'swap_averaging')))
    bad = deepcopy(observed); bad['floating']['curve'] = 'base_forward'; bad['definitionRevisionId'] = manifest['revisionId']
    query(runtime(reject(command(bad), 'P0001', 'swap_projection')))
    first = {**weighted['floating'], 'spread': '0'}
    second = {**observed['floating'], 'spread': '0'}
    bad = deepcopy(first); bad['observationSchedule']['quantities'][0]['quantity']['value'] = '0'
    query(runtime(reject(composed(bad, second), 'P0001', 'swap_averaging')))
    bad = deepcopy(second); bad['observationSchedule']['calendar'] = None
    query(runtime(reject(composed(first, bad), 'P0001', 'swap_averaging')))
    assert query('SELECT count(*) FROM trading.publications;') == before
    assert query(runtime("SELECT NOT has_function_privilege(current_user,(SELECT p.oid FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname='save_projected_swap_core'),'EXECUTE');")) == 't'
    assert query(runtime("SELECT count(*)=4 AND bool_and(NOT has_function_privilege(current_user,p.oid,'EXECUTE')) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('swap_observation_rows','swap_observation_sampling','retain_floating_swap_side','retained_swap_fixing_evidence');")) == 't'
    query(runtime(reject("PERFORM security.swap_observation_rows(NULL::uuid,NULL::uuid,NULL::uuid,NULL::uuid,NULL::text,NULL::text,NULL::date,NULL::date,NULL::text,NULL::jsonb);", '42501')))
    print('PASS observation storage: both composed slots and legacy writes enforce positive quantities, exact source cadence, required calendars and private helpers', flush=True)

    def clone(table, overrides, predicate):
        replacement = json.dumps(overrides).replace("'", "''")
        return f"INSERT INTO trading.{table} SELECT(jsonb_populate_record(NULL::trading.{table},to_jsonb(s)||'{replacement}'::jsonb)).* FROM trading.{table} s WHERE {predicate} LIMIT 1;"

    schedule = query("SELECT id FROM trading.swap_observation_schedules WHERE averaging='quantity_weighted' LIMIT 1;")
    def header(change):
        values = {'id': str(uuid4()), 'identity_hash': '\\x' + uuid4().hex + uuid4().hex, **change}
        return clone('swap_observation_schedules', values, "averaging='quantity_weighted'")
    for change in [{'reference_revision_id': str(uuid4())}, {'averaging': 'observation_arithmetic', 'calendar_code': 'missing_calendar'}]:
        query('BEGIN;' + reject(header(change), '23503') + 'ROLLBACK;')
    other = query(f"SELECT code FROM ref_data.quantity_specifications WHERE catalog_id='{reference}' AND revision_id='{revision}' AND code<>(SELECT quantity_code FROM trading.swap_observation_quantities LIMIT 1) ORDER BY code LIMIT 1;")
    for change, state in [({'contract_id': str(uuid4())}, '23503'), ({'quantity_code': other}, '23503'), ({'amount': '0'}, '23514')]:
        spare = str(uuid4())
        values = {'schedule_id': spare, 'ordinal': 100, **change}
        query('BEGIN;' + header({'id': spare}) + reject(clone('swap_observation_quantities', values, f"schedule_id='{schedule}'"), state) + 'ROLLBACK;')
    query('BEGIN;' + reject(clone('swap_floating_sides', {'id': str(uuid4()), 'observation_schedule_id': schedule}, "averaging='observation_arithmetic'"), '23503') + 'ROLLBACK;')
    for table in ('swap_observation_schedules', 'swap_observation_quantities'):
        assert query(f"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='trading.{table}'::regclass;") == 't'
        assert query(f"SET SESSION AUTHORIZATION preview_runtime;SELECT count(*) FROM trading.{table};") == '0'
        query(runtime(reject(f'INSERT INTO trading.{table} SELECT * FROM trading.{table} WHERE false;', '42501')))
        query('BEGIN;' + reject(f'UPDATE trading.{table} SET catalog_id=catalog_id;', '55000') + 'ROLLBACK;')
    print('PASS observation storage: exact source/calendar/quantity/Side foreign keys, positive amounts, forced temporal RLS and immutable records', flush=True)
