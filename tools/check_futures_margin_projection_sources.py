"""Official past-margin source closure in an owned PostgreSQL fixture."""
import json
from uuid import uuid4

from check_reference_editing import main
from check_futures_margin_journal import checks as journal_checks


def checks(query, manifest):
    journal_checks(query, manifest)
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    at = '2020-12-20T16:00:00Z'
    day = '2020-12-20'
    def issue():
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def call(sql):
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{issue()}');{sql}COMMIT;")

    def capture():
        return call(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")

    owner = json.loads(query("SELECT to_jsonb(a)::text FROM settlement_data.futures_margin_adoptions a "
                             f"WHERE catalog_id='{catalog}' ORDER BY publication_order LIMIT 1;"))
    def read(context, *, trade=None, binding=None, extra='', until='original_until'):
        return call(f"SELECT security.use_market_context('{catalog}','{context}');"
                    f"SELECT security.use_trading_context('{catalog}','{at}','{day}');"
                    "CREATE TEMP TABLE source_deadline AS SELECT security.admit_trading_business_source("
                    f"'{catalog}','{context}','{at}','{day}') AS original_until;{extra}"
                    "SELECT settlement_data.read_futures_margin_sources("
                    f"'{catalog}','{trade or owner['trade_id']}','{binding or owner['binding_id']}',"
                    f"'{context}','{at}','{day}',{until})::text FROM source_deadline;")

    before = capture()
    baseline = json.loads(read(before))
    assert baseline['contextId'] == before and baseline['businessDate'] == day
    rows = baseline['observations']
    assert len(rows) >= 2 and len({r['observation']['id'] for r in rows}) == len(rows)
    assert {r['observation']['observed_at'] for r in rows} == {'2020-12-18T17:00:00+00:00', '2020-12-19T17:00:00+00:00'}
    assert {r['observation']['role'] for r in rows} == {'daily_settlement'}
    first = next(r['observation'] for r in rows if r['observation']['observed_at'] == '2020-12-18T17:00:00+00:00'
                 and r['observation']['corrects_id'] is None)
    head = query("SELECT o.id FROM market_data.observations o WHERE "
                 f"catalog_id='{catalog}' AND source_event_id='{first['source_event_id']}' "
                 "AND NOT EXISTS(SELECT 1 FROM market_data.observations n WHERE n.catalog_id=o.catalog_id AND n.corrects_id=o.id);")
    correction = str(uuid4())
    query("INSERT INTO market_data.observations SELECT catalog_id,"
          f"'{correction}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
          "source_event_id,'projection_source_revision','daily_settlement','final',observed_at,clock_timestamp(),value+.1,id "
          f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{head}');")
    after = capture()
    revised = json.loads(read(after))
    assert {r['observation']['id'] for r in revised['observations']} == {r['observation']['id'] for r in rows} | {correction}
    retained = json.loads(read(before))
    assert {k:v for k,v in retained.items() if k!='authorityUntil'} == {k:v for k,v in baseline.items() if k!='authorityUntil'}, \
        'A historical prefix must retain all and only its original source revisions under newly checked current authority.'
    assert all(r['knownAt'] <= revised['knownAt'] for r in revised['observations'])
    for arguments, code in [({'binding':str(uuid4())}, 'futures_margin_unavailable'),
                            ({'trade':str(uuid4())}, 'futures_margin_unavailable'),
                            ({'until':"original_until+interval '1 second'"}, 'trading_source_denied'),
                            ({'extra':"SELECT pg_sleep(.06);", 'until':"clock_timestamp()-interval '1 second'"}, 'futures_margin_denied')]:
        try:
            read(after, **arguments)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code)
    assert query("SELECT has_table_privilege('openetrm_risk_worker','market_data.market_knowledge_receipts','SELECT');") == 'f'
    assert query("SELECT has_function_privilege('openetrm_runtime','settlement_data.futures_margin_projection_source_rows(settlement_data.futures_margin_policies,uuid,timestamptz,timestamptz)','EXECUTE');") == 'f'
    print('PASS past margin source closure: native adopted scope, complete correction ancestry, certified historical cutoff, original deadline and no raw worker receipt grants', flush=True)


if __name__ == '__main__':
    main(checks=checks)
