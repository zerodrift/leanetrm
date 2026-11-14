"""Saved margin policies and exact native booking adoption in owned PostgreSQL."""
from copy import deepcopy
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from decimal import Decimal
import json
import os
from pathlib import Path
import subprocess
import time
from uuid import uuid4

from check_reference_editing import main
from preview_fixture import catalogue_fixture, fixture, trade_booking, trading_fixture


def margin_fixture(root):
    reference, market, security, manifest = fixture(root)
    marker = "UPDATE market_data.revisions SET state='sealed'"
    assert market.count(marker) == 1
    additional = f"""
INSERT INTO market_data.curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,commodity_code,
 location_code,profile_code,quote_code,settlement_target_code,rule,direct_role,direct_source_code,name)
 SELECT catalog_id,revision_id,'future_alternate',reference_catalog_id,reference_revision_id,commodity_code,
 location_code,profile_code,quote_code,settlement_target_code,rule,direct_role,direct_source_code,'Synthetic alternate futures mark'
 FROM market_data.curves WHERE(catalog_id,revision_id,code)=('{manifest['catalogId']}','{manifest['revisionId']}','future');
INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'future_alternate',contract_id,ordinal
 FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{manifest['catalogId']}','{manifest['revisionId']}','future');
"""
    return reference, market.replace(marker, additional+marker), security, manifest


def checks(query, manifest):
    catalog = manifest['catalogId']
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    query(trading_fixture(catalog))
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest, 'https://preview.invalid'))

    def clone_profile(source, target):
        original = query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='{source}';")
        principal = str(uuid4())
        query(f"BEGIN;INSERT INTO security.principals VALUES('{principal}','human',true);"
              f"INSERT INTO security.identity_bindings VALUES('https://preview.invalid','{target}','{principal}','human_interactive');"
              f"INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{principal}','{target}','Synthetic secondary {source}',true,1);"
              f"INSERT INTO security.role_grants SELECT gen_random_uuid(),'{principal}',role_id,tenant_id,workspace_id,granted_from,granted_to,revoked "
              f"FROM security.role_grants WHERE principal_id='{original}';COMMIT;")

    clone_profile('editor', 'secondary_editor')
    clone_profile('trader', 'secondary_trader')

    def issue(profile):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def call(sql, profile='trader', application=None):
        token = issue(profile)
        return query((f"SET application_name='{application}';" if application else '')+"SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                     f"SELECT security.use_context('{token}');{sql}COMMIT;")

    def capture():
        return call(f"SELECT security.capture_date_context('{catalog}','2020-12-18',NULL);")

    def literal(body):
        return "'"+json.dumps(body).replace("'", "''")+"'::jsonb"

    def rejected(sql, code, profile='trader'):
        try:
            call(sql, profile)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code)

    contract = '50000000-0000-0000-0000-000000000001'
    book = 'd1000000-0000-0000-0000-000000000001'
    parties = literal(trade_booking())
    booked = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                             f"'{manifest['revisionId']}','future','{contract}','2020-12-18T16:00:00Z',NULL,2,3,"
                             f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{parties})::text;"))
    trade, binding = booked['tradeId'], booked['bindingId']
    original_context = capture()
    policy = dict(id=str(uuid4()), definitionRevisionId=manifest['revisionId'], contractId=contract, settlementCurve='future',
                  referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'], currency='usd',
                  fromAt='2020-01-01T00:00:00Z', untilAt='2021-01-01T00:00:00Z', opening='include_opening_instant',
                  increment='0.01', tie='to_even', stage='payment_total', roundingScope='held_binding_difference_payment_total_v1',
                  implementation='dated_futures_margin_policy_v1',
                  occurrences=[dict(id=str(uuid4()), at='2020-12-18T16:00:00Z', dueAt='2020-12-19T12:00:00Z', phase='daily'),
                               dict(id=str(uuid4()), at='2020-12-28T15:30:00Z', dueAt='2020-12-29T12:00:00Z', phase='final')])
    save = dict(operationId=str(uuid4()), expectedRevision=0, contextId=original_context, asOf='2020-12-18T16:00:00Z', policy=policy)

    def save_policy(body):
        return json.loads(call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(body)})::text;", 'editor'))

    saved = save_policy(save)
    assert saved['revision'] == 1 and saved['policyId'] == policy['id']
    assert save_policy(save) == saved
    head = query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    duplicate = save_policy({**save, 'operationId': str(uuid4())})
    assert duplicate['revision'] == 1 and duplicate['publicationOrder'] == saved['publicationOrder']
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == head
    revised = deepcopy(save)
    revised.update(operationId=str(uuid4()), expectedRevision=1)
    revised['policy']['increment'] = '0.03'
    assert save_policy(revised)['revision'] == 2
    restored = save_policy({**save, 'operationId': str(uuid4()), 'expectedRevision': 2})
    assert restored['revision'] == 1, 'A same-fact save returns the exact existing policy version for explicit selection'
    print('PASS saved margin policy: versioned terms, exact retry and distinct same-fact receipts without duplicate configuration', flush=True)

    request = dict(operationId=str(uuid4()), tradeId=trade, bindingId=binding, expectedRevision=1,
                   policyId=policy['id'], policyRevision=1, contextId=capture(), asOf='2020-12-18T16:00:00Z',
                   adoption='native_execution_inception_v1')

    def adopt(body):
        return json.loads(call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(body)})::text;"))

    rejected(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal({**request, 'contextId': original_context})});", 'futures_margin_context')
    adopted = adopt(request)
    assert adopted['originRevision'] == 1 and adopted['policyRevision'] == 1
    assert adopt(request) == adopted
    before_duplicate = query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    assert adopt({**request, 'operationId': str(uuid4())})['publicationOrder'] == adopted['publicationOrder']
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == before_duplicate
    assert query(f"SELECT count(*) FROM settlement_data.futures_margin_adoptions WHERE catalog_id='{catalog}';") == '1'

    def visible(context, at='2020-12-18T16:00:00Z', selected_trade=trade):
        return call(f"SELECT security.use_market_context('{catalog}','{context}');"
                    f"SELECT security.use_trading_context('{catalog}','{at}');"
                    f"SELECT count(*) FROM settlement_data.futures_margin_adoptions WHERE(catalog_id,trade_id)=('{catalog}','{selected_trade}');")

    assert visible(request['contextId']) == '0', 'A preview context cannot certify the adoption it preceded'
    accepted_context = capture()
    assert visible(accepted_context) == '1'
    print('PASS native margin adoption: exact saved policy, original booking, durable retries and post-commit certified visibility', flush=True)
    reader = json.loads(call(f"SELECT security.use_market_context('{catalog}','{accepted_context}');"
                             "SELECT jsonb_build_array(security.has_permission('ReadTrades'),"
                             "(SELECT count(*) FROM settlement_data.futures_margin_policies),"
                             "(SELECT count(*) FROM settlement_data.futures_margin_policy_occurrences),"
                             "(SELECT count(*) FROM settlement_data.futures_margin_adoptions));", 'viewer'))
    assert reader == [False, 2, 4, 0], 'Policy configuration visibility must remain independent of trade-read authority'
    assert call(f"SELECT security.use_market_context('{catalog}','{original_context}');"
                "SELECT count(*) FROM settlement_data.futures_margin_policies;", 'viewer') == '0'
    for instant in ('2020-12-18T16:00:00', '2020-12-18T16:00:00+01:00', '2020-12-18T16:00:00.0000001Z'):
        malformed = {**save, 'operationId': str(uuid4()), 'asOf': instant}
        rejected(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(malformed)});", 'futures_margin_instant', 'editor')
    print('PASS policy reads: market-only reader sees the certified configuration without held-trade disclosure; UTC precision is explicit', flush=True)

    def amend(lots=2, curve='future', memo='Synthetic amendment'):
        return (f"SELECT security.amend_futures_with_parties('{catalog}','{uuid4()}','{trade}',1,'{book}','{manifest['revisionId']}',"
                f"'{curve}','{contract}','2020-12-18T16:30:00Z','2020-12-18T16:30:00Z',NULL,{lots},3,'Synthetic continuity check',"
                f"'contract_lot_execution_pnl_v2','unprocessed_futures_booking_amendment_v1',NULL,NULL,'{memo}',{parties});")

    rejected(amend(lots=3), 'futures_margin_continuity')
    rejected(amend(curve='future_alternate'), 'futures_margin_continuity')
    json.loads(call(amend()))
    assert query(f"SELECT max(revision) FROM trading.trade_revisions WHERE(catalog_id,trade_id)=('{catalog}','{trade}');") == '2'
    assert query(f"SELECT origin_revision FROM settlement_data.futures_margin_adoptions WHERE(catalog_id,trade_id,binding_id)=('{catalog}','{trade}','{binding}');") == '1'
    print('PASS adopted futures continuity: quantity and pricing transitions refuse; memo revision retains stable adoption and exact origin', flush=True)
    failures = []
    assert adopt(request) == adopted, 'Original request replay survives a later metadata head'
    before_noop = query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    try:
        acknowledged = adopt({**request, 'operationId': str(uuid4())})
        assert acknowledged['originRevision'] == 1 and acknowledged['publicationOrder'] == adopted['publicationOrder']
        assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';") == before_noop
    except (RuntimeError, AssertionError) as error:
        failures.append('Same economic adoption after memo revision: '+str(error))

    later_booking = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                                   f"'{manifest['revisionId']}','future','{contract}','2020-12-18T16:00:00Z',NULL,2,3,"
                                   f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{parties})::text;"))
    later_trade, later_binding = later_booking['tradeId'], later_booking['bindingId']
    call(f"SELECT security.amend_futures_with_parties('{catalog}','{uuid4()}','{later_trade}',1,'{book}','{manifest['revisionId']}',"
         f"'future','{contract}','2020-12-18T16:30:00Z','2020-12-18T16:30:00Z',NULL,3,3,'Synthetic pre-adoption quantity revision',"
         f"'contract_lot_execution_pnl_v2','unprocessed_futures_booking_amendment_v1',NULL,NULL,NULL,{parties});")
    later_request = {**request, 'operationId': str(uuid4()), 'tradeId': later_trade, 'bindingId': later_binding,
                     'expectedRevision': 2, 'asOf': '2020-12-18T16:40:00Z', 'contextId': capture()}
    later_adoption = adopt(later_request)
    assert later_adoption['originRevision'] == 2
    later_context = capture()
    if visible(later_context, '2020-12-18T16:15:00Z', later_trade) != '0':
        failures.append('Adoption became visible before its origin booking revision took effect')
    assert visible(later_context, '2020-12-18T16:40:00Z', later_trade) == '1'
    assert not failures, '\n'.join(failures)
    print('PASS adoption reconciliation after memo heads and independent execution/binding-effective clocks', flush=True)

    for expression, profile in [(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(save)});", 'viewer'),
                                (f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(request)});", 'viewer')]:
        rejected(expression, 'futures_margin_denied', profile)
    rejected(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(save)});", 'futures_margin_command_conflict', 'secondary_editor')
    rejected(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(request)});", 'futures_margin_command_conflict', 'secondary_trader')
    changed = deepcopy(save)
    changed['policy']['increment'] = '0.02'
    rejected(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(changed)});", 'futures_margin_command_conflict', 'editor')
    rejected(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal({**request, 'policyRevision': 2})});", 'futures_margin_command_conflict')
    rejected(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal({**request, 'operationId': str(uuid4()), 'policyRevision': 2})});", 'futures_margin_adoption_conflict')
    print('PASS margin command authority: denied reader, actor isolation, changed-payload conflict and immutable selected-policy adoption', flush=True)

    observation = str(uuid4())
    query("INSERT INTO market_data.observations "
          f"SELECT catalog_id,'{observation}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,"
          "'margin_inception_pv','1',role,'final','2020-12-18T16:35:00Z',clock_timestamp(),3.2,NULL "
          f"FROM market_data.observations WHERE catalog_id='{catalog}' AND id='70000000-0000-0000-0000-000000000001';")
    value_context = capture()
    size = Decimal(query(f"SELECT contract_size FROM ref_data.futures_products WHERE(catalog_id,revision_id,code)="
                         f"('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','test_gas_future');"))
    samples = [dict(contextId=request['contextId'], tradeId=trade, at='2020-12-18T16:40:00Z', available=False, priced=False),
               dict(contextId=later_context, tradeId=later_trade, at='2020-12-18T16:15:00Z', available=False, priced=False)]
    for selected_trade, lots in ((trade, 2), (later_trade, 3)):
        known = query(f"SELECT k.known_from FROM settlement_data.futures_margin_adoptions a JOIN trading.knowledge_receipts k "
                      "ON(k.catalog_id,k.publication_order)=(a.catalog_id,a.publication_order) "
                      f"WHERE(a.catalog_id,a.trade_id)=('{catalog}','{selected_trade}');")
        samples.append(dict(contextId=value_context, tradeId=selected_trade, at='2020-12-18T16:40:00Z', available=True,
                            priced=True, adoptedKnownAt=known, expectedPv=str(Decimal('0.2')*size*lots)))
    for sample in samples:
        sample['token'] = issue('risk')
    host = query('SHOW unix_socket_directories;')
    assert Path(host).resolve().name.startswith('oetrm-ref-')
    packet = dict(manifest=manifest, host=host, samples=samples, observation=observation, contract=contract, executionAt='2020-12-18T16:00:00Z')
    path = Path(host)/'margin-adoption-check.json'
    path.write_text(json.dumps(packet))
    path.chmod(0o600)
    def managed_check(packet):
        path.write_text(json.dumps(packet))
        result = subprocess.run([os.environ.get('DOTNET', 'dotnet'), 'run', '--no-build', '--no-restore', '--project',
                                 'tests/OpenEtrm.Api.Checks', '--', '--futures-margin-adoption', str(path)],
                                cwd=Path(__file__).resolve().parents[1], capture_output=True, text=True, timeout=60)
        if result.returncode:
            raise RuntimeError(result.stdout+result.stderr)
        print(result.stdout.strip(), flush=True)

    managed_failures = []
    try:
        managed_check(packet)
    except RuntimeError as error:
        managed_failures.append(str(error))

    def native_rejected(sql, code):
        try:
            query(sql)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code)

    native_rejected(f"BEGIN;INSERT INTO settlement_data.futures_margin_policy_occurrences "
                    f"SELECT catalog_id,policy_id,policy_revision,'{uuid4()}','2020-12-19T16:00:00Z','2020-12-20T12:00:00Z','daily',publication_order "
                    f"FROM settlement_data.futures_margin_policy_occurrences WHERE(catalog_id,policy_id,policy_revision)=('{catalog}','{policy['id']}',1) LIMIT 1;COMMIT;",
                    'futures_margin_policy_sealed')
    for bad_count, bad_hash in ((3, None), (2, '0'*64)):
        new_id = str(uuid4())
        patch = dict(id=new_id, occurrence_count=bad_count)
        if bad_hash is not None:
            patch['terms_hash'] = bad_hash
        native_rejected(f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',coalesce(max(publication_order),0)+1,pg_current_xact_id() "
                        f"FROM trading.publications WHERE catalog_id='{catalog}';"
                        f"INSERT INTO settlement_data.futures_margin_policies SELECT (jsonb_populate_record(NULL::settlement_data.futures_margin_policies,"
                        f"to_jsonb(p)||{literal(patch)}||jsonb_build_object('publication_order',(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')))).* "
                        f"FROM settlement_data.futures_margin_policies p WHERE(p.catalog_id,p.id,p.revision)=('{catalog}','{policy['id']}',1);"
                        f"INSERT INTO settlement_data.futures_margin_policy_occurrences SELECT catalog_id,'{new_id}',1,id,at,due_at,phase,"
                        f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}') FROM settlement_data.futures_margin_policy_occurrences "
                        f"WHERE(catalog_id,policy_id,policy_revision)=('{catalog}','{policy['id']}',1);SET CONSTRAINTS ALL IMMEDIATE;COMMIT;",
                        'futures_margin_policy_seal')
    normalized = deepcopy(save)
    normalized.update(operationId=str(uuid4()), expectedRevision=2)
    normalized['policy']['increment'] = '0.0100'
    normalized['policy']['occurrences'].reverse()
    native_normalized = json.loads(call("SET TIME ZONE 'Asia/Tokyo';"+
                                       f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(normalized)})::text;", 'editor'))
    assert native_normalized['revision'] == 1
    print('PASS complete policy sealing: post-commit append, incomplete set and changed digest refuse; clock, scale and transport ordering preserve identity', flush=True)

    def wait_for(sql, expected='1', seconds=2):
        deadline = time.monotonic()+seconds
        while query(sql) != expected:
            assert time.monotonic() < deadline, 'The expected native lock wait was not observed'
            time.sleep(.02)

    # Both admission orders are exercised at the real shared publication fence.
    for adoption_first in (True, False):
        fresh = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                                f"'{manifest['revisionId']}','future','{contract}','2020-12-18T16:00:00Z',NULL,2,3,"
                                f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{parties})::text;"))
        fresh_trade = fresh['tradeId']
        adoption_body = {**request, 'operationId': str(uuid4()), 'tradeId': fresh_trade, 'bindingId': fresh['bindingId'], 'contextId': capture()}
        app = 'margin_race_'+uuid4().hex
        statements = {
            'adopt': f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(adoption_body)})::text;",
            'amend': f"SELECT security.amend_futures_with_parties('{catalog}','{uuid4()}','{fresh_trade}',1,'{book}','{manifest['revisionId']}',"
                     f"'future','{contract}','2020-12-18T16:30:00Z','2020-12-18T16:30:00Z',NULL,3,3,'Synthetic adoption race',"
                     f"'contract_lot_execution_pnl_v2','unprocessed_futures_booking_amendment_v1',NULL,NULL,NULL,{parties})::text;"}

        def competing(kind):
            try:
                return call(statements[kind], application=app+'_'+kind), None
            except RuntimeError as error:
                return None, str(error)

        first, second = ('adopt', 'amend') if adoption_first else ('amend', 'adopt')
        with ThreadPoolExecutor(max_workers=3) as pool:
            holder = pool.submit(query, f"SET application_name='{app}_holder';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(4);COMMIT;")
            wait_for(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{app}_holder' AND wait_event='PgSleep';")
            first_work = pool.submit(competing, first)
            wait_for(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.application_name='{app}_{first}' "
                     "AND l.locktype='advisory' AND l.classid=0 AND l.objid=730849102 AND NOT l.granted;")
            second_work = pool.submit(competing, second)
            wait_for(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.application_name='{app}_{second}' "
                     "AND l.locktype='advisory' AND l.classid=0 AND l.objid=730849102 AND NOT l.granted;")
            holder.result(timeout=6)
            success, unexpected = first_work.result(timeout=5)
            _, refusal = second_work.result(timeout=5)
            assert success is not None and unexpected is None, unexpected
            assert refusal is not None and ('futures_margin_continuity' if adoption_first else 'futures_revision_conflict') in refusal, refusal
        assert query(f"SELECT max(revision) FROM trading.trade_revisions WHERE(catalog_id,trade_id)=('{catalog}','{fresh_trade}');") == ('1' if adoption_first else '2')
        assert query(f"SELECT count(*) FROM settlement_data.futures_margin_adoptions WHERE(catalog_id,trade_id)=('{catalog}','{fresh_trade}');") == ('1' if adoption_first else '0')
    print('PASS concurrent adoption/amendment: both observed publication-fence orders preserve exact expected heads and atomic financial scope', flush=True)

    # A later pre-existing grant cannot extend an already admitted operation.
    # The real relation wait proves expiry occurs after admission, not before it.
    @contextmanager
    def successor_grants(profile, permission):
        principal = query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='{profile}';")
        saved_permissions = query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE p.permission='{permission}' "
                                  f"AND p.role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
        role_a, role_b, grant_a, grant_b = (str(uuid4()) for _ in range(4))
        app = 'margin_authority_'+uuid4().hex
        query(f"BEGIN;INSERT INTO security.roles VALUES('{role_a}','{app}_a','Synthetic initial margin authority'),"
              f"('{role_b}','{app}_b','Synthetic successor margin authority');"
              f"DELETE FROM security.role_permissions WHERE permission='{permission}' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');"
              f"INSERT INTO security.role_permissions VALUES('{role_a}','{permission}'),('{role_b}','{permission}');"
              f"INSERT INTO security.role_grants VALUES('{grant_a}','{principal}','{role_a}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '3 seconds',false),"
              f"('{grant_b}','{principal}','{role_b}','{tenant}','{workspace}',clock_timestamp()+interval '3 seconds',clock_timestamp()+interval '100 seconds',false);COMMIT;")
        try:
            yield app
        finally:
            restored_permissions = saved_permissions.replace("'", "''")
            query(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{grant_a}','{grant_b}');"
                  f"DELETE FROM security.role_permissions WHERE role_id IN('{role_a}','{role_b}');"
                  f"DELETE FROM security.roles WHERE id IN('{role_a}','{role_b}');"
                  f"INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'{restored_permissions}'::jsonb) ON CONFLICT DO NOTHING;COMMIT;")

    with successor_grants('trader', 'AmendTrade') as app:
        with ThreadPoolExecutor(max_workers=2) as pool:
            holder = pool.submit(query, f"SET application_name='{app}_holder';BEGIN;LOCK TABLE settlement_data.futures_margin_commands IN ACCESS EXCLUSIVE MODE;SELECT pg_sleep(6);COMMIT;")
            deadline = time.monotonic()+2
            while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{app}_holder' AND wait_event='PgSleep';") != '1':
                assert time.monotonic() < deadline
                time.sleep(.02)

            def waiting_replay():
                try:
                    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(request)})::text;", application=app)
                except RuntimeError as error:
                    return str(error)
                return None

            waiter = pool.submit(waiting_replay)
            deadline = time.monotonic()+2
            while query(f"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a USING(pid) WHERE a.application_name='{app}' "
                        "AND NOT l.granted AND l.relation='settlement_data.futures_margin_commands'::regclass;") != '1':
                assert time.monotonic() < deadline and not waiter.done()
                time.sleep(.02)
            holder.result(timeout=8)
            error = waiter.result(timeout=4)
            assert error is not None and 'futures_margin_denied' in error, 'An admitted replay chained successor authority across its original deadline'
        assert adopt(request) == adopted, 'A fresh request can use the now-current successor grant'
    print('PASS margin authority: original grant deadline survives a real receipt wait; a fresh successor-authorized request remains usable', flush=True)

    fresh = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                            f"'{manifest['revisionId']}','future','{contract}','2020-12-18T16:00:00Z',NULL,2,3,"
                            f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{parties})::text;"))
    fresh_adoption = {**request, 'operationId': str(uuid4()), 'tradeId': fresh['tradeId'],
                      'bindingId': fresh['bindingId'], 'contextId': capture()}
    fresh_policy = deepcopy(save)
    fresh_policy.update(operationId=str(uuid4()), expectedRevision=0)
    fresh_policy['policy']['id'] = str(uuid4())

    def financial_counts():
        return query(f"SELECT jsonb_build_array((SELECT count(*) FROM settlement_data.futures_margin_policies WHERE catalog_id='{catalog}'),"
                     f"(SELECT count(*) FROM settlement_data.futures_margin_adoptions WHERE catalog_id='{catalog}'),"
                     f"(SELECT count(*) FROM settlement_data.futures_margin_commands WHERE catalog_id='{catalog}'),"
                     f"(SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}'));")

    cases = [('editor', 'WriteMarketDefinitions', 'save_futures_margin_policy', fresh_policy),
             ('editor', 'WriteMarketDefinitions', 'save_futures_margin_policy', {**save, 'operationId': str(uuid4())}),
             ('trader', 'AmendTrade', 'adopt_futures_margin', fresh_adoption)]
    for profile, permission, operation, body in cases:
        before = financial_counts()
        expression = f"SELECT settlement_data.{operation}('{catalog}',{literal(body)})::text;"
        with successor_grants(profile, permission):
            # The function has already returned successfully; its original
            # authority must still cover the enclosing transaction's commit.
            rejected(expression+"SELECT pg_sleep(6);", 'futures_margin_denied', profile)
            assert financial_counts() == before, 'Expired commit retained a financial fact or command receipt'
            assert json.loads(call(expression, profile))['operationId'] == body['operationId']
    print('PASS deferred margin authority: expiry after operation return rolls back new policy, same-fact receipt and adoption at commit; fresh successor requests succeed', flush=True)

    for profile, permission, operation, body in cases:
        # Each is now an existing fact: a new request must retain a receipt, and
        # still cannot use the old deferred check to bypass commit admission.
        body = {**body, 'operationId': str(uuid4())}
        before = financial_counts()
        command_check = dict(profile=profile, kind='policy_save' if operation=='save_futures_margin_policy' else 'adopt', request=body)
        with successor_grants(profile, permission):
            try:
                managed_check(dict(manifest=manifest,host=host,command={**command_check,'expire':True}))
            except RuntimeError as error:
                managed_failures.append(str(error))
            if financial_counts() != before:
                managed_failures.append('The owned expired request retained a receipt')
            managed_check(dict(manifest=manifest,host=host,command={**command_check,'expire':False}))
    assert not managed_failures, '\n'.join(managed_failures)

    principal = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    digest = uuid4().hex+uuid4().hex
    query(f"INSERT INTO security.identity_bindings VALUES('https://preview.invalid','{principal}','{principal}','human_interactive');"
          f"INSERT INTO security.hosted_accounts(principal_id,issuer,tenant_id,workspace_id,login,password_hash,credential_version) "
          f"VALUES('{principal}','https://preview.invalid','{tenant}','{workspace}','synthetic_margin_user','synthetic_nonlogin_hash',1);"
          f"INSERT INTO security.hosted_sessions(token_digest,principal_id,credential_version,expires_at) "
          f"VALUES('{digest}','{principal}',1,clock_timestamp()+interval '3 seconds');")
    hosted_token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_hosted_context('{digest}','preview_runtime');")
    hosted_body = {**request, 'operationId': str(uuid4())}
    before = financial_counts()
    native_rejected("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                    f"SELECT security.use_context('{hosted_token}');"
                    f"DO $check$ DECLARE admitted_until timestamptz;BEGIN "
                    f"admitted_until=settlement_data.futures_margin_command_authority('{catalog}','adopt');"
                    f"PERFORM settlement_data.adopt_futures_margin('{catalog}',{literal(hosted_body)});"
                    "SET CONSTRAINTS ALL IMMEDIATE;PERFORM pg_sleep(6);"
                    f"PERFORM settlement_data.finish_futures_margin_command('{catalog}','{hosted_body['operationId']}','adopt',"
                    f"{literal(hosted_body)},admitted_until);END $check$;COMMIT;", 'futures_margin_denied')
    assert financial_counts() == before, 'Hosted expiry retained a command receipt'
    print('PASS hosted margin commit admission: original sign-in expiry remains a boundary after the deferred check was flushed', flush=True)


if __name__ == '__main__':
    main(checks=checks, fixture_factory=margin_fixture)
