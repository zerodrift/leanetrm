"""Business-scenario checks for the owned gas basis and facility demo."""
from decimal import Decimal
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from seed_gas_basis_demo import seed


def run_checks(url, run_id, root, query, manifest):
    demo = seed(url, run_id, root)
    repeated=seed(url, run_id, root)
    assert all(repeated[k]==demo[k] for k in ['trades','obligations','ledgerId','sourceId','planId']), 'Retrying the demo must reuse every business fact'
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frames=c.call('workspace')
    frame=initial_frame(frames,'2020-12-18')['id']
    quantity_context={**demo['quantityContext'],'snapshotId':next(f['id'] for f in frames['frames'] if f['at'].startswith('2021-01-15T08:00:00'))}
    evaluated = {curve: c.call('evaluate', dict(snapshotId=frame, curve=curve)) for curve in ['base_forward', 'gas_basis', 'local_gas']}
    assert [p['value'] for p in evaluated['gas_basis']['points']] == ['0.2', '0.21', '0.22', '0.23', '0.24', '0.25']
    for base, basis, local in zip(*(evaluated[curve]['points'] for curve in ['base_forward', 'gas_basis', 'local_gas'])):
        assert Decimal(base['value']) + Decimal(basis['value']) == Decimal(local['value'])
    assert {r['curve'] for r in evaluated['local_gas']['trace']} >= {'base_forward', 'gas_basis', 'local_gas'}
    # Each native basis tenor has 250 dated weekday closes, never inferred all-in prices.
    counts = query("SELECT count(DISTINCT observed_at::date),count(DISTINCT series_id) FROM market_data.observations WHERE source_code='fixture' AND observed_at>='2020-01-06' AND observed_at<'2020-12-19' AND series_id IN(SELECT s.id FROM market_data.series s JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(s.catalog_id,s.revision_id,s.contract_id) WHERE m.curve_code='gas_basis' AND s.quote_code='test_gas_basis');")
    assert counts == '250|6', counts
    context = demo['context']
    view = c.call('facilities/quantities/read', dict(context=context, ledgerId=demo['ledgerId']))
    assert len({a['counterparty'] for a in view['facts']['allocations']})==2
    assert len({a['book'] for a in view['facts']['allocations']})==2
    assert view['balance']['allocated'] == '95' and view['balance']['available'] == '5' and view['balance']['excess'] == '10'
    command = dict(kind='adopt', ledgerId=demo['ledgerId'], planRevisionId=demo['planId'], sourceRevisionId=demo['sourceId'], expectedPreviousId=None, evidence='Synthetic final allocation acceptance')
    request = dict(context=context, operationId=str(uuid4()), effectiveAt='2021-01-15T08:00:00Z', ownerId=view['ownerId'], command=command)
    priced=c.call('gas/composition/editor',dict(context={**demo['bookingContext'],'snapshotId':frame},tradeId=demo['trades'][0]))
    assert Decimal(priced['valuation']['presentValue'])==20
    adopted = c.call('facilities/quantities/record', request)
    assert c.call('facilities/quantities/record', request) == adopted
    for trade, amount in zip(demo['trades'], ['60', '25', '10']):
        final = c.call('gas/quantities/read', dict(context=quantity_context, tradeId=trade, selectionPolicy='final_facility_allocated_billing_v1'))
        assert final['rows'][0]['selected'] == dict(numerator=amount, denominator='1')
        normalized=final['rows'][0]['normalizedInput']
        assert Fraction(int(normalized['numerator']),int(normalized['denominator']))*100==int(amount)
        manual = c.call('gas/quantities/read', dict(context=quantity_context, tradeId=trade, selectionPolicy='operational_projection_v1'))
        assert manual['rows'][0]['selected'] == dict(numerator='80', denominator='1')
    repriced=c.call('gas/composition/editor',dict(context={**demo['bookingContext'],'snapshotId':frame},tradeId=demo['trades'][0]))
    assert repriced['valuation']['presentValue']==priced['valuation']['presentValue']
    # The normal history endpoint must expose the synthetic marks, not only a fixture table.
    history=c.call('history',dict(snapshotId=frame,curve='gas_basis',contractId=evaluated['gas_basis']['points'][0]['contractId'],revisionPolicy='exact_snapshot_pins_v1'))
    samples=[p for p in history['points'] if p['at'][:10]<='2020-12-18' and p['value'] is not None]
    assert len(samples)==100, len(samples)
    before_view=c.call('gas/quantities/read',dict(context=quantity_context,tradeId=demo['trades'][0],selectionPolicy='final_facility_allocated_billing_v1'))
    retained={**quantity_context,'knowledgeMode':'retained','contextId':before_view['context']['contextId']}
    def final(n, expected=200, selected_context=None):
        return c.call('gas/quantities/read',dict(context=selected_context or quantity_context,tradeId=demo['trades'][n],selectionPolicy='final_facility_allocated_billing_v1'),expected=expected)
    def record(body, expected=200):
        return c.call('facilities/quantities/record',dict(context=context,operationId=str(uuid4()),effectiveAt='2021-01-15T08:00:00Z',ownerId=view['ownerId'],command=body),expected=expected)
    same=record(command)['outcome'];assert same['adoptionId']==adopted['outcome']['adoptionId']
    changed_source=record(dict(kind='source',ledgerId=demo['ledgerId'],expectedPreviousId=demo['sourceId'],quantity='90',evidence='Synthetic corrected flow'))['outcome']['revisionId']
    assert final(0)['rows'][0]['selected']==dict(numerator='60',denominator='1'), 'Unaccepted source corrections cannot change accepted inputs'
    assert c.call('facilities/quantities/record',request)==adopted, 'A retry of the original adoption cannot reinterpret corrected sources'
    record({**command,'expectedPreviousId':adopted['outcome']['adoptionId'],'evidence':'Synthetic new acceptance of stale source'},expected=409)
    def plan(amounts, previous, source=changed_source):
        return record(dict(kind='allocate',ledgerId=demo['ledgerId'],expectedPreviousId=previous,sourceRevisionId=source,evidence=f'Synthetic allocation plan replacing {previous}',
                           allocations=[dict(obligationId=o,tradeRevision=1,quantity=q) for o,q in zip(demo['obligations'],amounts) if q is not None]))['outcome']['revisionId']
    def accept(plan_id, previous):
        return record({**command,'planRevisionId':plan_id,'sourceRevisionId':changed_source,'expectedPreviousId':previous})['outcome']['adoptionId']
    p2=plan(['55','25','10'],demo['planId'])
    from check_facility_adoption_integrity import check as check_integrity
    check_integrity(query,manifest,demo['ledgerId'],p2)
    a2=accept(p2,adopted['outcome']['adoptionId'])
    assert final(0)['rows'][0]['selected']==dict(numerator='55',denominator='1')
    assert final(0,selected_context=retained)['rows'][0]['selected']==dict(numerator='60',denominator='1')
    # Concurrent approvals of one predecessor cannot both advance the whole-plan head.
    from concurrent.futures import ThreadPoolExecutor
    clients=[PreviewClient(url),PreviewClient(url)]
    for client in clients:client.bootstrap();client.call('profile',{'profile':'trader'})
    def competing(item):
        n,client=item
        owner=client.call('facilities/quantities/read',dict(context=context,ledgerId=demo['ledgerId']))['ownerId']
        body={**command,'planRevisionId':p2,'sourceRevisionId':changed_source,'expectedPreviousId':a2,'evidence':f'Synthetic concurrent approval {n}'}
        try:
            return client.call('facilities/quantities/record',dict(context=context,operationId=str(uuid4()),effectiveAt='2021-01-15T08:00:00Z',ownerId=owner,command=body))['outcome']['adoptionId']
        except AssertionError as error:
            if 'got 409:' not in str(error):raise
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:results=list(pool.map(competing,enumerate(clients)))
    assert sum(r is not None for r in results)==1, results
    a2=next(r for r in results if r is not None)
    unchanged=final(1)['rows'][0]
    p3=plan(['54','25','10'],p2);a3=accept(p3,a2)
    assert final(1)['rows'][0]==unchanged, "Another target changing must not alter this trade's final-input projection"
    p4=plan(['54','25',None],p3);a4=accept(p4,a3)
    final(2,expected=422)
    p5=plan(['54','25','10'],p4);a5=accept(p5,a4)
    assert final(2)['rows'][0]['selected']==dict(numerator='10',denominator='1')
    # Withdrawal uses accepted pins even after a later source correction makes new adoption ineligible.
    record(dict(kind='source',ledgerId=demo['ledgerId'],expectedPreviousId=changed_source,quantity='80',evidence='Synthetic later source correction'))
    record(dict(kind='retract_adoption',ledgerId=demo['ledgerId'],planRevisionId=p5,sourceRevisionId=changed_source,expectedPreviousId=a5,evidence='Synthetic acceptance withdrawal'))
    for n in range(3):final(n,expected=422)
    past=record(command)['outcome']
    assert past['adoptionId']==adopted['outcome']['adoptionId']
    for n in range(3):final(n,expected=422)
    assert final(0,selected_context=retained)['rows'][0]['selected']==dict(numerator='60',denominator='1')
    # Working authority is rechecked on the original outcome, with no new business mutation.
    query("DELETE FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='trader') AND permission='AmendTrade';")
    try:
        c.call('facilities/quantities/status',dict(operationId=request['operationId'],ownerId=view['ownerId']),expected=403)
    finally:
        query("INSERT INTO security.role_permissions SELECT id,'AmendTrade' FROM security.roles WHERE code='trader';")
    assert c.call('facilities/quantities/status',dict(operationId=request['operationId'],ownerId=view['ownerId']))==adopted
    # A first empty-plan adoption has no target authorization loop to mask an expiry bug.
    from preview_fixture import gas_purchase
    booking_context={**demo['bookingContext'],'snapshotId':frame}
    gas_catalog=c.call('gas/catalog',booking_context)
    empty_terms=gas_purchase(booking_context,gas_catalog,fee=None)
    empty_terms['physicalSides'][0].update(deliveryFrom='2021-01-02',deliveryTo='2021-01-03',quantityBasis='per_operating_day',quantity='100')
    empty_trade=c.call('gas/composition/book',empty_terms)['tradeId']
    empty_obligation=c.call('gas/quantities/read',dict(context=quantity_context,tradeId=empty_trade,selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    empty_ledger=record(dict(kind='create',obligationId=empty_obligation,day='2021-01-02',facility='synthetic_gas_point_a'))['outcome']['ledgerId']
    empty_source=record(dict(kind='source',ledgerId=empty_ledger,expectedPreviousId=None,quantity='0',evidence='Synthetic explicit zero source'))['outcome']['revisionId']
    empty_plan=record(dict(kind='allocate',ledgerId=empty_ledger,expectedPreviousId=None,sourceRevisionId=empty_source,evidence='Synthetic empty allocation plan',allocations=[]))['outcome']['revisionId']
    empty_request=dict(context=context,operationId=str(uuid4()),effectiveAt='2021-01-15T08:00:00Z',ownerId=view['ownerId'],command=dict(kind='adopt',ledgerId=empty_ledger,planRevisionId=empty_plan,sourceRevisionId=empty_source,expectedPreviousId=None,evidence='Synthetic accepted empty plan'))
    c.call('facilities/quantities/record',empty_request)
    c.call('gas/quantities/read',dict(context=quantity_context,tradeId=empty_trade,selectionPolicy='final_facility_allocated_billing_v1'),expected=422)
    from check_facility_adoption_expiry import check as check_expiry
    check_expiry(query,manifest,empty_request['operationId'])
    # Independent risk-history reader sees the complete joint dated benchmark/basis inputs.
    risk=PreviewClient(url);risk.bootstrap();risk.call('profile',{'profile':'risk'})
    risk_frame=initial_frame(risk.call('workspace'),'2020-12-18')['id']
    scope=dict(context=dict(snapshotId=risk_frame,convention='trading_workspace_v1',knowledgeMode='current'),bookIds=[],portfolioIds=[],tradeIds=demo['trades'])
    catalog=risk.call('portfolio/risk/catalog',scope)
    scope['context'].update(knowledgeMode='retained',contextId=catalog['context']['contextId'])
    policy=dict(convention='dated_factor_common_session_changes_v1',numericalPolicy='exact_18_fractional_abs_lt_1e24_v1',basis='fixed_current_dated_input_mapping_v1',revisions='unique_visible_correction_head_v1',alignment='exact_session_close_v1',transform='native_absolute_change_v1',missing='common_complete_adjacent_sessions_v1',regimes='exact_source_role_state_unit_contract_v1',minimumSamples=200)
    aligned=risk.call('portfolio/risk/history',dict(scope=scope,pricingPolicy=catalog['pricingPolicy'],policy=policy,calendar=dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=manifest['referenceRevisionId']),code='weekdays_utc'),**{'from':'2020-01-06','to':'2020-12-19'},closeTime='16:00:00',ambiguousTime='reject',invalidTime='reject'),timeout=120)
    assert aligned['coverage']['available'] and len(aligned['manifest']['sessions'])==250 and len(aligned['manifest']['samples'])==249
    assert len(aligned['manifest']['columns'])==3, 'Gas depends on the explicitly declared futures, forward bridge and regional basis inputs'
    print('PASS gas basis demo: explicit benchmark+basis pricing, dated history, retry-safe setup, conservation, final adoption and unchanged contractual inputs', flush=True)
