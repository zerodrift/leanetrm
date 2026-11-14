"""Actual futures margin, correction closure and shared cash in owned PostgreSQL."""
import json
from decimal import Decimal
import os
from pathlib import Path
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_futures_margin_adoption import successor_grants
from preview_fixture import catalogue_fixture, trade_booking, trading_fixture


def checks(query, manifest):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    query(trading_fixture(catalog))
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest, 'https://preview.invalid'))

    def literal(value):
        return "'"+json.dumps(value).replace("'", "''")+"'::jsonb"

    def issue(profile):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def call(sql, profile='trader'):
        return query("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                     f"SELECT security.use_context('{issue(profile)}');{sql}COMMIT;")

    def capture(day='2020-12-20'):
        return call(f"SELECT security.capture_date_context('{catalog}','{day}',NULL);")

    def rejected(sql, code, profile='trader'):
        try:
            call(sql,profile)
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code)

    contract = '50000000-0000-0000-0000-000000000001'
    book = 'd1000000-0000-0000-0000-000000000001'
    booked = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                             f"'{manifest['revisionId']}','future','{contract}','2020-12-18T15:00:00Z',NULL,2,3,"
                             f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;"))
    occurrences = [dict(id=str(uuid4()), at=at, dueAt=due, phase=phase) for at, due, phase in
                   [('2020-12-18T17:00:00Z', '2020-12-19T12:00:00Z', 'daily'),
                    ('2020-12-19T17:00:00Z', '2020-12-20T12:00:00Z', 'daily'),
                    ('2020-12-28T15:30:00Z', '2020-12-29T12:00:00Z', 'final')]]
    policy = dict(id=str(uuid4()), definitionRevisionId=manifest['revisionId'], contractId=contract, settlementCurve='future',
                  referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'], currency='usd',
                  fromAt='2020-01-01T00:00:00Z', untilAt='2021-01-01T00:00:00Z', opening='include_opening_instant',
                  increment='0.01', tie='to_even', stage='payment_total', roundingScope='held_binding_difference_payment_total_v1',
                  implementation='dated_futures_margin_policy_v1', occurrences=occurrences)
    saved = dict(operationId=str(uuid4()), expectedRevision=0, contextId=capture(), asOf='2020-12-20T16:00:00Z', policy=policy)
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal(saved)});", 'editor')
    adoption = dict(operationId=str(uuid4()), tradeId=booked['tradeId'], bindingId=booked['bindingId'], expectedRevision=1,
                    policyId=policy['id'], policyRevision=1, contextId=capture(), asOf='2020-12-20T16:00:00Z',
                    adoption='native_execution_inception_v1')
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal(adoption)});")
    context = capture()
    initial = json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');"
                              f"SELECT security.use_trading_context('{catalog}','2020-12-20T16:00:00Z');"
                              f"SELECT settlement_data.read_futures_margin_state('{catalog}','{booked['tradeId']}',"
                              f"'{booked['bindingId']}','{context}','2020-12-20T16:00:00Z')::text;"))
    assert initial['version'] == 1 and initial['determinations'] == []
    print('PASS native journal starts from the exact adopted inception with an empty accepted prefix', flush=True)
    observations = []
    for at, price in [(occurrences[0]['at'], '3.4'), (occurrences[1]['at'], '3.3'), ('2020-12-20T15:00:00Z', '3.5')]:
        identity = str(uuid4())
        query(f"INSERT INTO market_data.observations SELECT catalog_id,'{identity}',definition_revision_id,series_id,reference_catalog_id,"
              f"reference_revision_id,source_code,'margin_journal_{identity}','1',role,'final','{at}',clock_timestamp(),{price},NULL "
              f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','70000000-0000-0000-0000-000000000001');")
        observations.append(identity)
    size = Decimal(query(f"SELECT contract_size FROM ref_data.futures_products WHERE(catalog_id,revision_id,code)="
                         f"('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','test_gas_future');"))*2
    totals = [size*Decimal('.4'), size*Decimal('-.1')]
    steps = [dict(occurrenceId=o['id'], previousDeterminationId=None, sourceObservationId=observation, previousBasis=basis,
                  exactVariation=str(total), roundedTotal=str(total), adjustment=str(total), adjustmentDue=o['dueAt'], noChange=False)
             for o, observation, basis, total in zip(occurrences, observations, ['3', '3.4'], totals)]
    request = dict(operationId=str(uuid4()),tradeId=booked['tradeId'],bindingId=booked['bindingId'],expectedRevision=1,expectedMarginRevision=1,
                   contextId=capture(),asOf='2020-12-20T16:00:00Z',throughOccurrence=occurrences[1]['id'],
                   implementation='native_futures_margin_journal_v1',correctionDue=[])
    request['previewHash'] = '0'*64  # Native-only operands below have an independent numerical oracle.
    result = json.loads(call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(request)},{literal(steps)})::text;"))
    assert result['version'] == 2
    context = capture()
    state = json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');"
                            f"SELECT security.use_trading_context('{catalog}','2020-12-20T16:00:00Z');"
                            f"SELECT settlement_data.read_futures_margin_state('{catalog}','{booked['tradeId']}',"
                            f"'{booked['bindingId']}','{context}','2020-12-20T16:00:00Z')::text;"))
    assert state['version'] == 2 and len(state['determinations']) == 2 and len(state['obligations']) == 2
    assert sum(Decimal(o['adjustment']) for o in state['obligations']) == sum(totals)
    print('PASS accepted daily margin atomically retains causal determinations and distinct shared cash memberships', flush=True)
    failures = []
    unchanged = [{**s, 'previousDeterminationId': d['id'], 'adjustment': '0', 'adjustmentDue': None, 'noChange': True}
                 for s, d in zip(steps,state['determinations'])]
    for field, value in [('expectedRevision', None), ('expectedMarginRevision', 'invalid')]:
        malformed = {**request,'operationId':str(uuid4()),field:value}
        try:
            rejected(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(malformed)},{literal(unchanged)});", 'futures_margin_input')
        except AssertionError as error:
            failures.append(str(error)+' for '+field)

    # A later occurrence created this shared currency/due definition first.
    # A separately owned earlier obligation must still see the same definition.
    from copy import deepcopy
    earlier_policy = deepcopy(policy)
    earlier_policy['id'] = str(uuid4())
    earlier_policy['occurrences'] = [deepcopy(occurrences[0]),deepcopy(occurrences[2])]
    earlier_policy['occurrences'][0]['dueAt'] = occurrences[1]['dueAt']
    earlier_booked = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                                    f"'{manifest['revisionId']}','future','{contract}','2020-12-18T15:00:00Z',NULL,2,3,"
                                    f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;"))
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal({**saved,'operationId':str(uuid4()),'contextId':capture(),'policy':earlier_policy})});",'editor')
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal({**adoption,'operationId':str(uuid4()),'tradeId':earlier_booked['tradeId'],'bindingId':earlier_booked['bindingId'],'policyId':earlier_policy['id'],'contextId':capture()})});")
    earlier_request = {**request,'operationId':str(uuid4()),'tradeId':earlier_booked['tradeId'],'bindingId':earlier_booked['bindingId'],
                       'contextId':capture(),'throughOccurrence':earlier_policy['occurrences'][0]['id']}
    earlier_steps = [{**steps[0],'adjustmentDue':occurrences[1]['dueAt']}]
    call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(earlier_request)},{literal(earlier_steps)});")
    early_context = capture('2020-12-18')
    visible_headers = call(f"SELECT security.use_market_context('{catalog}','{early_context}');"
                           f"SELECT security.use_trading_context('{catalog}','2020-12-18T18:00:00Z');"
                           "SELECT count(*) FROM trading.cash_payment_memberships b JOIN trading.cash_payment_instruments i "
                           "ON(i.catalog_id,i.instrument_id)=(b.catalog_id,b.instrument_id) "
                           f"WHERE(b.catalog_id,b.trade_id)=('{catalog}','{earlier_booked['tradeId']}');")
    if visible_headers != '1':
        failures.append('A shared due definition created later in business time hid an earlier owned obligation')
    assert not failures, '\n'.join(failures)
    print('PASS native no-op versions remain explicit; shared cash definitions preserve earlier holding visibility', flush=True)

    samples = [dict(contextId=request['contextId'],basis='3',cashCount=0,expectedPv=str(size*Decimal('.5'))),
               dict(contextId=context,basis='3.3',cashCount=2,expectedPv=str(size*Decimal('.5')))]
    payment = dict(operationId=str(uuid4()), allocationId=str(uuid4()), tradeId=booked['tradeId'],
                   bindingId=state['obligations'][0]['cash_binding_id'], expectedRevision=0, paidAt='2020-12-20T15:30:00Z',
                   amount=str(size*Decimal('.15')),policy='explicit_paid_cash_nominal_due_v1')
    call(f"SELECT security.record_cash_payment('{catalog}',{literal(payment)},'2020-12-20T16:00:00Z');")
    paid_context = capture()
    samples.append(dict(contextId=paid_context,basis='3.3',cashCount=2,expectedPv=str(size*Decimal('.35'))))

    def read_state(context):
        return json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');"
                               f"SELECT security.use_trading_context('{catalog}','2020-12-20T16:00:00Z');"
                               f"SELECT settlement_data.read_futures_margin_state('{catalog}','{booked['tradeId']}',"
                               f"'{booked['bindingId']}','{context}','2020-12-20T16:00:00Z')::text;"))

    correction = str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,"
          f"reference_revision_id,source_code,source_event_id,'2',role,'final',observed_at,clock_timestamp(),3.6,id "
          f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{observations[0]}');")
    changed_steps = [{**steps[0], 'previousDeterminationId':state['determinations'][0]['id'], 'sourceObservationId':correction,
                      'exactVariation':str(size*Decimal('.6')), 'roundedTotal':str(size*Decimal('.6')), 'adjustment':str(size*Decimal('.2')),
                      'adjustmentDue':'2020-12-20T15:45:00Z'},
                     {**steps[1], 'previousDeterminationId':state['determinations'][1]['id'], 'previousBasis':'3.6',
                      'exactVariation':str(size*Decimal('-.3')), 'roundedTotal':str(size*Decimal('-.3')), 'adjustment':str(size*Decimal('-.2')),
                      'adjustmentDue':'2020-12-20T15:45:00Z'}]
    corrected_request = {**request,'operationId':str(uuid4()),'contextId':capture(),'expectedMarginRevision':2,
                         'throughOccurrence':occurrences[0]['id'],
                         'correctionDue':[dict(occurrenceId=o['id'],dueAt='2020-12-20T15:45:00Z') for o in occurrences[:2]]}
    rejected(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(corrected_request)},{literal(changed_steps[:1])});",'futures_margin_coverage')
    corrected = json.loads(call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(corrected_request)},{literal(changed_steps)})::text;"))
    assert corrected['version'] == 3 and corrected['changed'] == 2
    assert json.loads(call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(corrected_request)},'[]')::text;")) == corrected
    corrected_context = capture()
    corrected_state = read_state(corrected_context)
    assert len(corrected_state['obligations']) == 4
    assert sum(Decimal(o['adjustment']) for o in corrected_state['obligations']) == sum(totals)
    assert corrected_state['determinations'][1]['basis_id'] == corrected_state['determinations'][0]['id']
    samples.append(dict(contextId=corrected_context,basis='3.3',cashCount=4,expectedPv=str(size*Decimal('.35'))))

    restated = str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{restated}',definition_revision_id,series_id,reference_catalog_id,"
          f"reference_revision_id,source_code,source_event_id,'3',role,'final',observed_at,clock_timestamp(),value,id "
          f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{correction}');")
    restated_steps = [{**s,'previousDeterminationId':d['id'],'adjustment':'0','adjustmentDue':None}
                      for s,d in zip(changed_steps,corrected_state['determinations'])]
    restated_steps[0]['sourceObservationId'] = restated
    restated_request = {**corrected_request,'operationId':str(uuid4()),'contextId':capture(),'expectedMarginRevision':3,'correctionDue':[]}
    same_price = json.loads(call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(restated_request)},{literal(restated_steps)})::text;"))
    assert same_price['version'] == 4 and same_price['changed'] == 2
    restated_context = capture()
    restated_state = read_state(restated_context)
    assert len(restated_state['obligations']) == 4
    assert restated_state['determinations'][1]['basis_id'] == restated_state['determinations'][0]['id']
    assert restated_state['determinations'][0]['source_observation_id'] == restated
    samples.append(dict(contextId=restated_context,basis='3.3',cashCount=4,expectedPv=str(size*Decimal('.35'))))
    # Returning a prior immutable financial fact must not reapply it or move
    # today's basis backwards, even after a different source was accepted.
    prior_fact = {**request,'operationId':str(uuid4())}
    before_fact = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    acknowledged = json.loads(call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(prior_fact)},{literal(steps)})::text;"))
    assert acknowledged['version'] == 2 and acknowledged['currentVersion'] == 4 and acknowledged['changed'] == 0
    assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';") == before_fact
    print('PASS native correction closure: successors restate, original and adjustment cash survive, same-price source changes retain zero-cash causal evidence',flush=True)
    final_source = str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{final_source}',definition_revision_id,series_id,reference_catalog_id,"
          f"reference_revision_id,source_code,'margin_journal_terminal','1','final_contract_settlement','final','{occurrences[2]['at']}',"
          f"clock_timestamp(),3.7,NULL FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{observations[0]}');")
    final_steps = [{**s,'previousDeterminationId':d['id'],'noChange':True} for s,d in zip(restated_steps,restated_state['determinations'])]
    final_steps.append(dict(occurrenceId=occurrences[2]['id'],previousDeterminationId=None,sourceObservationId=final_source,previousBasis='3.3',
                            exactVariation=str(size*Decimal('.4')),roundedTotal=str(size*Decimal('.4')),adjustment=str(size*Decimal('.4')),
                            adjustmentDue=occurrences[2]['dueAt'],noChange=False))
    final_request = {**request,'operationId':str(uuid4()),'contextId':capture('2020-12-28'),'asOf':'2020-12-28T16:00:00Z',
                     'expectedMarginRevision':4,'throughOccurrence':occurrences[2]['id']}
    final = json.loads(call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(final_request)},{literal(final_steps)})::text;"))
    assert final['version'] == 5 and final['changed'] == 1
    samples.append(dict(contextId=capture('2020-12-28'),basis='3.7',cashCount=5,noOpenMark=True,discount='0.95',
                        at='2020-12-28T16:00:00Z',expectedPv=str(size*(Decimal('.15')+Decimal('.4')*Decimal('.95')))))
    for sample in samples:
        sample.update(token=issue('risk'))
        sample.setdefault('at','2020-12-20T16:00:00Z')
    host = query('SHOW unix_socket_directories;')
    path = Path(host)/'margin-journal-check.json'
    path.write_text(json.dumps(dict(manifest=manifest,host=host,samples=samples,tradeId=booked['tradeId'],mark=observations[2],contract=contract)))
    path.chmod(0o600)
    result = subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
                             '--','--futures-margin-journal',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)


    amended_trade = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                                   f"'{manifest['revisionId']}','future','{contract}','2020-12-18T16:00:00Z',NULL,2,3,"
                                   f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;"))
    call(f"SELECT security.amend_futures_with_parties('{catalog}','{uuid4()}','{amended_trade['tradeId']}',1,'{book}',"
         f"'{manifest['revisionId']}','future','{contract}','2020-12-18T16:30:00Z','2020-12-18T16:30:00Z',NULL,3,3,"
         f"'Synthetic amended origin','contract_lot_execution_pnl_v2','unprocessed_futures_booking_amendment_v1',NULL,NULL,NULL,{literal(trade_booking())});")
    amended_policy = deepcopy(policy)
    amended_policy['id'] = str(uuid4())
    amended_policy['occurrences'] = [dict(id=str(uuid4()),at='2020-12-18T16:15:00Z',dueAt=occurrences[1]['dueAt'],phase='daily'),occurrences[2]]
    call(f"SELECT settlement_data.save_futures_margin_policy('{catalog}',{literal({**saved,'operationId':str(uuid4()),'contextId':capture(),'policy':amended_policy})});",'editor')
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal({**adoption,'operationId':str(uuid4()),'tradeId':amended_trade['tradeId'],'bindingId':amended_trade['bindingId'],'expectedRevision':2,'policyId':amended_policy['id'],'contextId':capture()})});")
    early_source = str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{early_source}',definition_revision_id,series_id,reference_catalog_id,"
          f"reference_revision_id,source_code,'margin_early_amendment','1',role,'final','2020-12-18T16:15:00Z',clock_timestamp(),3.4,NULL "
          f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{observations[0]}');")
    amended_request = {**request,'operationId':str(uuid4()),'tradeId':amended_trade['tradeId'],'bindingId':amended_trade['bindingId'],
                       'expectedRevision':2,'contextId':capture(),'throughOccurrence':amended_policy['occurrences'][0]['id']}
    early_total = str(size*Decimal('.6'))
    early_steps = [{**steps[0],'occurrenceId':amended_policy['occurrences'][0]['id'],'sourceObservationId':early_source,
                    'exactVariation':early_total,'roundedTotal':early_total,'adjustment':early_total,'adjustmentDue':occurrences[1]['dueAt']}]
    rejected(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(amended_request)},{literal(early_steps)});",'futures_margin_continuity')
    print('PASS amended-origin margin refuses required events before the held financial revision takes effect',flush=True)
    path.write_text(json.dumps(dict(manifest=manifest,host=host,command=dict(request=amended_request,expectContinuity=True))))
    result = subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
                             '--','--futures-margin-journal',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)

    managed_trade = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                                   f"'{manifest['revisionId']}','future','{contract}','2020-12-18T15:00:00Z',NULL,2,3,"
                                   f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;"))
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal({**adoption,'operationId':str(uuid4()),'tradeId':managed_trade['tradeId'],'bindingId':managed_trade['bindingId'],'contextId':capture()})});")
    managed_request = {**request,'operationId':str(uuid4()),'tradeId':managed_trade['tradeId'],'bindingId':managed_trade['bindingId'],'contextId':capture()}
    before_managed = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    path.write_text(json.dumps(dict(manifest=manifest,host=host,command=dict(request=managed_request,expectedAdjustment=str(size*Decimal('.3'))))))
    result = subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
                             '--','--futures-margin-journal',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    print(result.stdout.strip(),flush=True)
    assert int(query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")) == int(before_managed)+1
    assert query(f"SELECT count(*) FROM settlement_data.futures_margin_runs WHERE(catalog_id,trade_id)=('{catalog}','{managed_trade['tradeId']}');") == '1'
    with successor_grants(query,manifest,'trader','ReadTrades'):
        for expire in (True,False):
            path.write_text(json.dumps(dict(manifest=manifest,host=host,command=dict(request=managed_request,previewExpires=expire))))
            result = subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
                                     '--','--futures-margin-journal',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
            if result.returncode:
                raise RuntimeError(result.stdout+result.stderr)
            print(result.stdout.strip(),flush=True)

    reviewed_trade = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                                    f"'{manifest['revisionId']}','future','{contract}','2020-12-18T15:00:00Z',NULL,2,3,"
                                    f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;"))
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal({**adoption,'operationId':str(uuid4()),'tradeId':reviewed_trade['tradeId'],'bindingId':reviewed_trade['bindingId'],'contextId':capture()})});")
    reviewed_request = {**request,'operationId':str(uuid4()),'tradeId':reviewed_trade['tradeId'],'bindingId':reviewed_trade['bindingId'],'contextId':capture()}
    path.write_text(json.dumps(dict(manifest=manifest,host=host,command=dict(request=reviewed_request,hashOnly=True))))
    result = subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
                             '--','--futures-margin-journal',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    reviewed_hash = next(line.removeprefix('REVIEW_HASH:') for line in result.stdout.splitlines() if line.startswith('REVIEW_HASH:'))
    latest = str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{latest}',definition_revision_id,series_id,reference_catalog_id,"
          f"reference_revision_id,source_code,source_event_id,'4',role,'final',observed_at,clock_timestamp(),3.8,id "
          f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{restated}');")
    stale_request = {**reviewed_request,'contextId':capture(),'previewHash':reviewed_hash}
    before_stale = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    path.write_text(json.dumps(dict(manifest=manifest,host=host,command=dict(request=stale_request,expectStale=True))))
    result = subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
                             '--','--futures-margin-journal',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout+result.stderr)
    assert query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';") == before_stale
    print(result.stdout.strip(),flush=True)

    # Privileged fixture construction deliberately disagrees about accepted
    # trading/source prefixes. No immutable context or receipt is modified.
    guarded_trade = json.loads(call(f"SELECT security.book_futures_with_parties('{catalog}','{uuid4()}','{book}',"
                                   f"'{manifest['revisionId']}','future','{contract}','2020-12-18T15:00:00Z',NULL,2,3,"
                                   f"'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(trade_booking())})::text;"))
    call(f"SELECT settlement_data.adopt_futures_margin('{catalog}',{literal({**adoption,'operationId':str(uuid4()),'tradeId':guarded_trade['tradeId'],'bindingId':guarded_trade['bindingId'],'contextId':capture()})});")
    guarded_request = {**request,'operationId':str(uuid4()),'tradeId':guarded_trade['tradeId'],'bindingId':guarded_trade['bindingId'],'contextId':capture()}
    current_first = query(f"SELECT id FROM market_data.observations WHERE(catalog_id,source_event_id,source_revision_id)="
                          f"('{catalog}','margin_journal_{observations[0]}','4');")
    # This test is appended after the official source correction above.
    guarded_steps = [{**steps[0],'sourceObservationId':current_first,'exactVariation':str(size*Decimal('.8')),
                      'roundedTotal':str(size*Decimal('.8')),'adjustment':str(size*Decimal('.8'))},
                     {**steps[1],'previousBasis':'3.8','exactVariation':str(size*Decimal('-.5')),
                      'roundedTotal':str(size*Decimal('-.5')),'adjustment':str(size*Decimal('-.5'))}]
    call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(guarded_request)},{literal(guarded_steps)});")

    def guarded_state(context):
        return json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');"
                               f"SELECT security.use_trading_context('{catalog}','2020-12-20T16:00:00Z');"
                               f"SELECT settlement_data.read_futures_margin_state('{catalog}','{guarded_trade['tradeId']}',"
                               f"'{guarded_trade['bindingId']}','{context}','2020-12-20T16:00:00Z')::text;"))

    prior_source = observations[1]
    for revision in (2,3):
        baseline = capture()
        accepted = guarded_state(baseline)
        missing = str(uuid4())
        query(f"INSERT INTO market_data.observations SELECT catalog_id,'{missing}',definition_revision_id,series_id,reference_catalog_id,"
              f"reference_revision_id,source_code,source_event_id,'{revision}',role,'final',observed_at,clock_timestamp(),3.4,id "
              f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{prior_source}');")
        changed = [{**s,'previousDeterminationId':d['id'],'adjustment':'0','adjustmentDue':None,'noChange':True}
                   for s,d in zip(guarded_steps,accepted['determinations'])]
        changed[1].update(sourceObservationId=missing,exactVariation=str(size*Decimal('-.4')),roundedTotal=str(size*Decimal('-.4')),noChange=False)
        if revision == 2:
            changed[1].update(adjustment=str(size*Decimal('.1')),adjustmentDue='2020-12-20T15:50:00Z')
        guarded_request.update(operationId=str(uuid4()),contextId=capture(),expectedMarginRevision=revision,
                               correctionDue=[dict(occurrenceId=occurrences[1]['id'],dueAt='2020-12-20T15:50:00Z')] if revision==2 else [])
        call(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(guarded_request)},{literal(changed)});")
        actual = capture()
        malformed = str(uuid4())
        assert query(f"SELECT bool_and(k.known_from<=t.known_at-interval '1 microsecond') FROM settlement_data.futures_margin_runs r "
                     "JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order) "
                     f"JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=('{catalog}','{actual}') "
                     f"WHERE(r.catalog_id,r.trade_id)=('{catalog}','{guarded_trade['tradeId']}');") == 't'
        query(f"INSERT INTO market_data.temporal_contexts SELECT catalog_id,'{malformed}',effective_on,known_at-interval '1 microsecond',"
              f"known_through,coverage_from,certified_through FROM market_data.temporal_contexts WHERE(catalog_id,id)=('{catalog}','{actual}');"
              f"INSERT INTO market_data.market_contexts SELECT catalog_id,'{malformed}',known_through FROM market_data.market_contexts "
              f"WHERE(catalog_id,context_id)=('{catalog}','{baseline}');"
              f"INSERT INTO trading.contexts SELECT catalog_id,'{malformed}',known_through FROM trading.contexts WHERE(catalog_id,context_id)=('{catalog}','{actual}');")
        rejected(f"SELECT security.use_market_context('{catalog}','{malformed}');"
                 f"SELECT security.use_trading_context('{catalog}','2020-12-20T16:00:00Z');"
                 f"SELECT settlement_data.read_futures_margin_state('{catalog}','{guarded_trade['tradeId']}',"
                 f"'{guarded_trade['bindingId']}','{malformed}','2020-12-20T16:00:00Z');",'futures_margin_context')
        prior_source = missing
        guarded_steps = changed
    print('PASS latest accepted heads cannot fall back through a missing source prefix, including same-price lineage',flush=True)

    for sql, code in [(
        f"BEGIN;INSERT INTO settlement_data.futures_margin_determinations SELECT(jsonb_populate_record(NULL::settlement_data.futures_margin_determinations,"
        f"to_jsonb(d)||jsonb_build_object('id','{uuid4()}'))).* FROM settlement_data.futures_margin_determinations d "
        f"WHERE(d.catalog_id,d.trade_id)=('{catalog}','{booked['tradeId']}') LIMIT 1;COMMIT;",'futures_margin_run_sealed'),(
        f"BEGIN;INSERT INTO trading.publications SELECT '{catalog}',max(publication_order)+1,pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';"
        "INSERT INTO settlement_data.futures_margin_runs SELECT(jsonb_populate_record(NULL::settlement_data.futures_margin_runs,"
        f"to_jsonb(r)||jsonb_build_object('revision',100,'determination_count',1,'publication_order',(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}')))).* "
        f"FROM settlement_data.futures_margin_runs r WHERE(r.catalog_id,r.trade_id)=('{catalog}','{booked['tradeId']}') LIMIT 1;"
        "SET CONSTRAINTS ALL IMMEDIATE;COMMIT;",'futures_margin_run_incomplete')]:
        try:
            query(sql)
        except RuntimeError as error:
            assert code in str(error),str(error)
        else:
            raise AssertionError('Expected '+code)
    rejected(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal({**request,'expectedRevision':2})},'[]');",'futures_margin_command_conflict')
    trader = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    risk = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk';")
    grant = str(uuid4())
    query(f"INSERT INTO security.role_grants SELECT '{grant}','{risk}',role_id,tenant_id,workspace_id,granted_from,granted_to,revoked "
          f"FROM security.role_grants WHERE principal_id='{trader}';")
    try:
        rejected(f"SELECT settlement_data.determine_futures_margin('{catalog}',{literal(request)},'[]');",'futures_margin_command_conflict','risk')
    finally:
        query(f"DELETE FROM security.role_grants WHERE id='{grant}';")
    print('PASS margin run sealing, complete membership and actor/payload receipt isolation',flush=True)


if __name__ == '__main__':
    main(checks=checks)
