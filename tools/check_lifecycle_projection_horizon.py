"""Certified close-session horizon selection in owned disposable PostgreSQL."""
import json
from pathlib import Path
from uuid import uuid4
from check_reference_editing import main
from close_session_fixture import draft, sql_session
from preview_fixture import trading_fixture, catalogue_fixture, fixture
from check_futures_margin_adoption import successor_grants

_upgrade = {}


def bootstrap(query, manifest):
    query(trading_fixture(manifest['catalogId']))
    query(f"SELECT security.initialize_operating_policies('{manifest['tenantId']}','{manifest['workspaceId']}');")
    query(catalogue_fixture(manifest, 'https://preview.invalid'))


def before_migration(query, migration):
    if migration.name != '0270_lifecycle_projection_horizon.sql':
        return
    reference, market, security, manifest = fixture(Path(__file__).resolve().parents[1])
    for sql in (reference, market, security):
        query(sql)
    bootstrap(query, manifest)
    sql_session(query, manifest, 'synthetic_existing_close', 'test_business', '16:00', ['future'], [])
    token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                  f"'https://preview.invalid','risk','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    old = query("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                f"SELECT security.use_context('{token}');SELECT security.capture_date_context('{manifest['catalogId']}','2020-12-18',NULL);COMMIT;")
    _upgrade.update(manifest=manifest, context=old)


def loaded_fixture(root):
    return ('', '', '', _upgrade['manifest']) if _upgrade else fixture(root)


def checks(query, manifest):
    catalog, definition = manifest['catalogId'], manifest['revisionId']
    if not _upgrade:
        bootstrap(query, manifest)

    def call(sql, profile='risk'):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'https://preview.invalid','{profile}','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        return query("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                     f"SELECT security.use_context('{token}');{sql}COMMIT;")

    def capture(day='2020-12-18'):
        return call(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")

    def publish(code, cutoff='16:00', effective=('2020-12-01', '2021-12-01')):
        body = json.dumps(draft(code, 'test_business', cutoff, ['future'], effective)).replace("'", "''")
        saved = json.loads(call(f"SELECT security.save_close_session('{catalog}','{definition}','{uuid4()}','{body}'::jsonb)::text;", 'editor'))
        operation = uuid4()
        sql = f"SELECT security.publish_close_session('{catalog}','{operation}','{code}',{saved['version']},{saved['generation']})::text;"
        result = json.loads(call(sql, 'approver'))
        count = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
        assert json.loads(call(sql, 'approver')) == result
        assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';") == count
        return result

    old_context = capture()
    session = publish('synthetic_projection_close')
    current = capture()

    if _upgrade:
        existing = lambda context: (f"SELECT security.read_lifecycle_projection_horizon('{catalog}','synthetic_existing_close',1,'{context}',"
                                    "'2020-12-18T16:00:00Z','2020-12-18')::text;")
        try:
            call(existing(_upgrade['context']))
        except RuntimeError as error:
            assert 'projection_horizon_evidence' in str(error), str(error)
        else:
            raise AssertionError('The migration backdated an existing session publication')
        migrated = json.loads(call(existing(current)))
        assert migrated['closes'][0]['date'] == '2020-12-21'
        assert query(f"SELECT k.known_from=security.checked_commit_time(t.publication_xid) FROM market_data.lifecycle_close_session_publications p "
                     "JOIN trading.publications t ON(t.catalog_id,t.publication_order)=(p.catalog_id,p.publication_order) "
                     "JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order) "
                     f"WHERE(p.catalog_id,p.session_code,p.session_version)=('{catalog}','synthetic_existing_close',1);") == 't'

    def read(context, opening='2020-12-18T16:00:00Z', day='2020-12-18', version=1):
        return (f"SELECT security.read_lifecycle_projection_horizon('{catalog}','{session['code']}',{version},'{context}',"
                f"'{opening}','{day}')::text;")

    value = json.loads(call(read(current)))
    assert value['contextId'] == current and value['session']['code'] == session['code'] and value['session']['version'] == 1
    assert value['closes'] == [{'date': '2020-12-21', 'instant': '2020-12-21T16:00:00+00:00', 'offsetSeconds': 0}], value
    assert value['publicationOrder'] > 0 and value['knownFrom'] <= value['knownAt']
    before_cutoff = json.loads(call(read(current, '2020-12-18T15:00:00Z')))
    assert before_cutoff['closes'][0]['date'] == '2020-12-18'

    def reject(sql, code):
        try:
            call(sql)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected ' + code)

    reject(read(old_context), 'projection_horizon_evidence')
    reject(read(current, version=2), 'projection_horizon_evidence')
    reject(read(current, day='2020-12-21'), 'knowledge_coverage')
    # A later published version never changes a retained explicit version's
    # calendar or cutoff. Under the newer context it governs its own start.
    publish('synthetic_projection_close', '17:00', ('2020-12-21', '2021-12-01'))
    newer = capture('2020-12-21')
    later_knowledge_at_opening = capture('2020-12-18')
    transition = json.loads(call(read(later_knowledge_at_opening)))
    assert transition['openingSession']['version'] == 1 and transition['session']['version'] == 2
    assert transition['closes'][0]['instant'] == '2020-12-21T17:00:00+00:00', transition
    private_definition = str(uuid4())
    query(f"INSERT INTO market_data.revisions SELECT catalog_id,'{private_definition}',reference_catalog_id,reference_revision_id,'draft',0 "
          f"FROM market_data.revisions WHERE(catalog_id,id)=('{catalog}','{definition}');"
          f"INSERT INTO market_data.revision_periods VALUES('{catalog}','{private_definition}','2021-02-01','2022-01-01');"
          f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=('{catalog}','{private_definition}');")
    query(f"INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,"
          "calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request,publisher_id,publish_request_id) "
          f"SELECT catalog_id,code,3,'published',2,'{private_definition}',reference_catalog_id,reference_revision_id,calendar_code,'18:00','2021-02-01','2021-12-01',"
          f"actor_id,'{uuid4()}','{{}}',publisher_id,'{uuid4()}' FROM market_data.close_session_versions WHERE(catalog_id,code,version)=('{catalog}','{session['code']}',2);"
          f"INSERT INTO market_data.close_session_curves SELECT catalog_id,code,3,definition_revision_id,curve_code FROM market_data.close_session_curves "
          f"WHERE(catalog_id,code,version)=('{catalog}','{session['code']}',2);")
    sql_session(query, manifest, 'synthetic_private_projection', 'test_business', '16:00', ['future'], [], revision=private_definition,
                curve_sources={'future': definition})
    unused_version_context = capture('2020-12-18')
    assert call(f"SELECT security.use_market_context('{catalog}','{unused_version_context}');"
                f"SELECT security.market_fact_visible('{catalog}','{private_definition}',NULL);") == 'f'
    scoped = json.loads(call(read(unused_version_context)))
    assert [v['version'] for v in scoped['versions']] == [1, 2], scoped['versions']
    reject(f"SELECT security.read_lifecycle_projection_horizon('{catalog}','synthetic_private_projection',1,'{unused_version_context}',"
           "'2020-12-18T16:00:00Z','2020-12-18')::text;", 'projection_horizon_denied')
    assert json.loads(call(read(current)))['closes'] == value['closes']
    reject(read(newer, '2020-12-21T16:00:00Z', '2020-12-21', 1), 'projection_horizon_version')
    revised = json.loads(call(read(newer, '2020-12-21T16:00:00Z', '2020-12-21', 2)))
    assert revised['closes'][0]['instant'] == '2020-12-21T17:00:00+00:00'
    holiday_context = capture('2020-12-31')
    holiday = json.loads(call(read(holiday_context, '2020-12-31T17:00:00Z', '2020-12-31', 2)))
    assert holiday['closes'][0]['date'] == '2021-01-04', holiday
    with successor_grants(query, manifest, 'risk', 'ReadTrades'):
        try:
            call(read(current) + 'SELECT pg_sleep(6);' + read(current))
        except RuntimeError as error:
            assert 'futures_margin_denied' in str(error), str(error)
        else:
            raise AssertionError('A successor grant extended the original economic horizon source admission')
    assert json.loads(call(read(current)))['closes'] == value['closes']
    contract = json.loads(query(f"SELECT jsonb_build_object('id',c.id,'expiry',c.expiry_at,'series',s.id,'reference',s.reference_catalog_id,'revision',s.reference_revision_id)::text "
                                "FROM market_data.contracts c JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id)=(c.catalog_id,c.revision_id,c.id) "
                                f"WHERE(c.catalog_id,c.revision_id,c.kind)=('{catalog}','{definition}','future') ORDER BY c.expiry_at LIMIT 1;"))
    def final_read(context, official='future'):
        return (f"SELECT security.read_lifecycle_projection_final('{catalog}','{definition}','future','{official}','{contract['id']}',"
                f"'{contract['expiry']}','{context}','2020-12-18T16:00:00Z','2020-12-18')::text;")
    before_final = capture()
    assert json.loads(call(final_read(before_final)))['observation'] is None
    native_final = uuid4()
    event = 'synthetic_final_' + str(native_final)
    query("INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
          "source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id) VALUES("
          f"'{catalog}','{native_final}','{definition}','{contract['series']}','{contract['reference']}','{contract['revision']}',"
          f"'fixture','{event}','1','final_contract_settlement','final','{contract['expiry']}',clock_timestamp(),3.7,NULL);")
    after_final = capture()
    assert call(f"SELECT security.use_market_context('{catalog}','{after_final}');SELECT count(*) FROM market_data.observations WHERE id='{native_final}';") == '0'
    actual_final = json.loads(call(final_read(after_final)))
    assert actual_final['observation']['id'] == str(native_final) and str(actual_final['observation']['value']) == '3.7'
    assert actual_final['observation']['known_from'] <= actual_final['knownAt']
    assert json.loads(call(final_read(before_final)))['observation'] is None
    reject(final_read(after_final, 'base_forward'), 'projection_input_denied')
    corrected = uuid4()
    query("INSERT INTO market_data.observations SELECT catalog_id," + f"'{corrected}'::uuid" + ",definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
          "source_code,source_event_id,'2',role,publication_state,observed_at,clock_timestamp(),4.2,id FROM market_data.observations "
          f"WHERE id='{native_final}';")
    corrected_context = capture()
    assert json.loads(call(final_read(corrected_context)))['observation']['id'] == str(corrected)
    assert json.loads(call(final_read(after_final)))['observation']['id'] == str(native_final)
    print('PASS known future primitive final: separate source clock, original cutoff absence, exact native final and explicit correction ancestry', flush=True)
    print('PASS certified economic horizon: exact native session identity, next configured close, immutable publication receipts, old cutoff refusal, version replay and idempotent publication', flush=True)


if __name__ == '__main__':
    main(checks=checks, before_migration=before_migration, fixture_factory=loaded_fixture)
