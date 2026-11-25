"""Saved operating-right trades through the owned public application routes."""
from copy import deepcopy
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from fractions import Fraction
import time
from uuid import uuid4
from exact_values import fraction
from preview_client import initial_frame
from preview_fixture import trade_booking


def original_physical_authority(client, query, manifest, selected):
    """A later grant cannot replace the selected read's original authority."""
    role=query("SELECT id FROM security.roles WHERE code='trader';")
    actor=query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    assert role and actor and '\n' not in role+actor
    assert query(f"SELECT EXISTS(SELECT 1 FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadPhysical'));")=='t'
    catalog=manifest['catalogId']
    heads_sql=("SELECT jsonb_build_array("
        f"(SELECT coalesce(max(publication_order),0) FROM trading.publications WHERE catalog_id='{catalog}'),"
        f"(SELECT coalesce(max(event_order),0) FROM market_data.knowledge_events WHERE catalog_id='{catalog}'));")
    for missing in (False,True):
        temporary,first,successor=(str(uuid4()) for _ in range(3))
        name='synthetic_operating_side_wait_'+temporary[:8]
        with ThreadPoolExecutor(max_workers=2) as pool:
            holder=pool.submit(query,f"SET application_name='{name}';BEGIN;LOCK TABLE trading.power_operating_instruments IN ACCESS EXCLUSIVE MODE;SELECT pg_sleep(3.5);COMMIT;")
            running=None
            installed=False
            try:
                deadline=time.monotonic()+1.5
                while query(f"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='{name}' AND wait_event='PgSleep');")!='t':
                    assert time.monotonic()<deadline,'The owned operating-source lock did not become active.'
                    time.sleep(.01)
                original='' if missing else f"INSERT INTO security.role_grants VALUES('{first}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '1.5 seconds',false);"
                query(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadPhysical');"
                    f"INSERT INTO security.roles VALUES('{temporary}','synthetic_side_timer_{temporary[:8]}','Synthetic original Side read authority');"
                    f"INSERT INTO security.role_permissions VALUES('{temporary}','ReadPhysical');"+original+
                    f"INSERT INTO security.role_grants VALUES('{successor}','{actor}','{temporary}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()+interval '2 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
                installed=True
                original_until=query(f"SELECT security.grant_until('{actor}','{manifest['tenantId']}','{manifest['workspaceId']}','ReadPhysical',clock_timestamp());")
                assert (original_until=='-infinity') is missing,'The synthetic entry grant did not isolate the intended original ReadPhysical authority.'
                epoch,heads=query('SELECT generation FROM security.epoch;'),query(heads_sql)
                running=pool.submit(client.call,'physical/power-trades/get',selected,expected=403,timeout=12)
                blocked=False
                deadline=time.monotonic()+1.2
                while time.monotonic()<deadline and not running.done():
                    blocked=query("SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid "
                        "WHERE l.relation='trading.power_operating_instruments'::regclass AND NOT l.granted "
                        "AND a.usename='preview_runtime' AND a.wait_event_type='Lock' AND a.query LIKE '%security.operating_position_scope%');")=='t'
                    if blocked:break
                    time.sleep(.01)
                assert blocked is not missing, 'Original ReadPhysical capture must precede the protected source wait, and absent authority must refuse before it.'
                if blocked:
                    assert query(f"SELECT clock_timestamp()<granted_to FROM security.role_grants WHERE id='{first}';")=='t', \
                        'Observe the protected source wait while the original ReadPhysical grant is still active.'
                    assert query("SELECT NOT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid "
                        "WHERE l.relation='trading.power_operating_instruments'::regclass AND NOT l.granted AND a.usename='preview_authenticator');")=='t', \
                        'The synthetic lock must not postpone identity issuance before the original permission capture.'
                running.result(timeout=12);holder.result(timeout=8)
                assert query('SELECT generation FROM security.epoch;')==epoch
                assert query(f"SELECT granted_from<=clock_timestamp() AND clock_timestamp()<granted_to FROM security.role_grants WHERE id='{successor}';")=='t'
                assert client.call('physical/power-trades/get',selected)['retainedSource']['contextId']==selected['context']['contextId'], \
                    'A new read with the active successor must work without rewriting the selected source.'
                assert query(heads_sql)==heads,'Readonly Side admission published a market or trading fact.'
            finally:
                try:
                    holder.result(timeout=8)
                    if running is not None:running.result(timeout=12)
                finally:
                    if installed:
                        query(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{first}','{successor}');"
                            f"DELETE FROM security.role_permissions WHERE role_id='{temporary}';DELETE FROM security.roles WHERE id='{temporary}';"
                            f"INSERT INTO security.role_permissions VALUES('{role}','ReadPhysical');COMMIT;")
    print('PASS selected operating Side HTTP authority: expired and absent original ReadPhysical refuse despite active successor, unchanged epoch and unchanged publication heads',flush=True)


def run_checks(client, query, manifest, plan, *, operating_predecessors, other_plans=()):
    client.call('profile', {'profile': 'trader'})
    frame=initial_frame(client.call('workspace'),'2020-12-15')['id']
    route='physical/power-trades/'
    catalog=client.call(route+'catalog',{'snapshotId':frame})
    assert catalog['canBook'] and catalog['knownAt']
    assert 'test_utc' in catalog['operating']['operatingBookingClockCodes']
    request=dict(operationId=str(uuid4()),tradeId=None,expectedRevision=0,bookId=catalog['books'][0]['id'],
        legalEntityId=catalog['legalEntities'][0]['id'],legalEntityRevision=catalog['legalEntities'][0]['revision'],
        counterpartyId=catalog['counterparties'][0]['id'],counterpartyRevision=catalog['counterparties'][0]['revision'],
        reason='Synthetic supplied operating-right contract',convention=catalog['convention'],plan=deepcopy(plan))
    request['plan']['snapshotId']=frame
    assert fraction(client.call(route+'preview',request)['valuation']['value'])==610
    before=query('SELECT count(*) FROM trading.trades;')
    saved=client.call(route+'book',request,timeout=60)
    assert saved['changed'] and saved['revision']==1
    assert client.call(route+'book',request)==saved
    assert client.call(route+'status',{'operationId':request['operationId']})==saved
    assert int(query('SELECT count(*) FROM trading.trades;'))==int(before)+1
    conflict=deepcopy(request);conflict['reason']='Different synthetic review'
    assert client.call(route+'book',conflict,expected=409)['code']=='operating_request_conflict'
    duplicate=deepcopy(request);duplicate['operationId']=str(uuid4())
    unchanged=client.call(route+'book',duplicate,timeout=60)
    assert not unchanged['changed'] and unchanged['tradeId']==saved['tradeId']
    assert int(query('SELECT count(*) FROM trading.trades;'))==int(before)+1
    selected=dict(snapshotId=frame,tradeId=saved['tradeId'])
    reopened=client.call(route+'get',selected)
    assert reopened['instrumentId']==saved['instrumentId'] and len(reopened['sides'])==2
    assert len({s['sideId'] for s in reopened['sides']})==2
    assert len({s['scheduleId'] for s in reopened['sides']})==2
    assert reopened['terms']==request, 'Reopening preserves the original reviewed command.'
    before_booking=dict(snapshotId=frame,convention='trading_workspace_v1',knowledgeMode='retained',contextId=catalog['contextId'])
    retained_target=dict(bindingId=saved['bindingId'],revision=saved['revision'])
    assert client.call(route+'get',{**selected,**retained_target,'context':before_booking},expected=422)['code']=='position_unavailable', \
        'An explicit pre-booking cut must not recapture current knowledge to reveal a later operating right.'
    client.call(route+'valuation',{**selected,**retained_target,'context':before_booking},expected=422)
    directory=client.call(route+'directory',dict(snapshotId=frame,bookIds=[request['bookId']],offset=0,limit=50))
    assert any(t['tradeId']==saved['tradeId'] for t in directory['trades'])
    value=client.call(route+'valuation',selected)
    assert fraction(value['valuation']['value'])==610
    assert [s['coefficient'] for s in value['valuation']['sides']]==['1','-1']
    assert all(row['evidenceId'] is None for side in value['valuation']['sides'] for row in side['rows'])
    context=dict(snapshotId=frame,convention='trading_workspace_v1',knowledgeMode='current')
    facts=client.call('trading/positions',dict(context=context,bookIds=[request['bookId']],portfolioIds=[],offset=0,limit=64))
    fact=next(p for p in facts['positions'] if p['tradeId']==saved['tradeId'])
    assert fact['instrumentType']=='operating_right' and fact['bookedQuantity']=='1'
    retained_selection=dict(context,knowledgeMode='retained',contextId=facts['context']['contextId'])
    retained_get={**selected,**retained_target,'context':retained_selection}
    exact_retained=client.call(route+'get',retained_get)
    assert exact_retained['context']['contextId']==retained_selection['contextId']
    assert exact_retained['bindingId']==saved['bindingId'] and exact_retained['revision']==saved['revision']
    assert exact_retained['sides']==reopened['sides']
    assert exact_retained['retainedSource']['snapshotId']==frame
    assert exact_retained['retainedSource']['contextId']==retained_selection['contextId']
    assert datetime.fromisoformat(exact_retained['retainedSource']['asOf'])==datetime.fromisoformat(facts['asOf'])
    assert exact_retained['retainedSource']['businessDate']==facts['context']['effectiveOn']
    assert datetime.fromisoformat(exact_retained['retainedSource']['knownAt'])==datetime.fromisoformat(facts['context']['knownAt'])
    client.call(route+'get',{**retained_get,'bindingId':str(uuid4())},expected=422)
    client.call(route+'get',{**retained_get,'revision':2},expected=422)
    client.call(route+'get',{**retained_get,'snapshotId':str(uuid4())},expected=422)
    query("CREATE POLICY side_test_hidden_operating_book ON trading.books AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);")
    try:
        assert client.call(route+'get',retained_get,expected=422)['code']=='position_unavailable', \
            'An exact operating Side read with no visible book must remain a structured unavailable result.'
    finally:
        query("DROP POLICY side_test_hidden_operating_book ON trading.books;")
    original_physical_authority(client,query,manifest,retained_get)
    holding=client.call('trading/position',dict(context=context,tradeId=saved['tradeId'],bindingId=saved['bindingId'],revision=1,instrumentType='operating_right'))['holding']
    assert Fraction(holding['presentValue'])==610 and holding['sideSchedules']
    upgrade_positions=[dict(kind='battery',request=request,saved=saved,value=610)]
    for kind,physical_plan,expected_value,expected_gross in other_plans:
        other=deepcopy(request)
        other.update(operationId=str(uuid4()),reason='Synthetic '+kind+' operating-right contract',plan=deepcopy(physical_plan))
        other['plan']['snapshotId']=frame
        assert fraction(client.call(route+'preview',other)['valuation']['value'])==expected_value
        receipt=client.call(route+'book',other,timeout=60)
        assert receipt['changed'] and receipt['revision']==1
        assert client.call(route+'book',other)==receipt
        exact=dict(snapshotId=frame,tradeId=receipt['tradeId'])
        trade=client.call(route+'get',exact)
        assert trade['resourceKind']==kind and trade['facilityId']==physical_plan['facilityId']
        assert trade['terms']==other and len(trade['sides'])==len(expected_gross)
        assert len({side['scheduleId'] for side in trade['sides']})==len(expected_gross)
        priced=client.call(route+'valuation',exact)
        assert fraction(priced['valuation']['value'])==expected_value
        assert {side['name']:fraction(side['grossValue']) for side in priced['valuation']['sides']}==expected_gross
        assert all(row['evidenceId'] is None for side in priced['valuation']['sides'] for row in side['rows'])
        ordinary=client.call('trading/position',dict(context=context,tradeId=receipt['tradeId'],bindingId=receipt['bindingId'],revision=1,instrumentType='operating_right'))['holding']
        assert Fraction(ordinary['presentValue'])==expected_value and ordinary['sideSchedules'] is True
        print('PASS '+kind+' operating trade HTTP: native booking, exact replay/reopening, independent retained gross Sides and ordinary position PV',flush=True)
        upgrade_positions.append(dict(kind=kind,request=other,saved=receipt,value=expected_value))
    from check_operating_populated_upgrade import run_checks as check_populated_upgrade
    check_populated_upgrade(query,manifest,upgrade_positions,operating_predecessors)
    for position in upgrade_positions:
        receipt=position['saved'];exact=dict(snapshotId=frame,tradeId=receipt['tradeId'])
        assert client.call(route+'get',exact)['terms']==position['request']
        assert fraction(client.call(route+'valuation',exact)['valuation']['value'])==position['value']
        assert client.call(route+'book',position['request'])==receipt
    print('PASS post-upgrade operating HTTP: original commands reopen, value and replay with current helpers and preserved native programs',flush=True)
    risk=client.call('portfolio/risk/catalog',dict(context=context,bookIds=[request['bookId']],portfolioIds=[],tradeIds=[saved['tradeId']]))
    assert any(m['measure']=='operating_contractual_present_value' for m in risk['measures'])
    future_context=dict(snapshotId=frame,convention='contract_lot_execution_pnl_v2',knowledgeMode='current')
    future_contract=client.call('futures/catalog',future_context)['contracts'][0]
    hedge=client.call('futures/book',dict(future_context,**trade_booking(),operationId=str(uuid4()),bookId=request['bookId'],
        curve=future_contract['curve'],contractId=future_contract['id'],definitionRevisionId=future_contract['definitionRevisionId'],lots='1',executionPrice='2.9'))
    held_hedge=client.call('trading/position',dict(context=context,tradeId=hedge['tradeId'],bindingId=hedge['bindingId'],revision=1,instrumentType='future'))['holding']
    hedge_catalog=client.call('portfolio/risk/catalog',dict(context=context,bookIds=[request['bookId']],portfolioIds=[],tradeIds=[saved['tradeId'],hedge['tradeId']]))
    factor=next(t for t in hedge_catalog['scenarioTargets'] if t['kind']!='delivery_bucket' and t['factor']['target']['curve']['code']==future_contract['curve'])
    hedge_context=dict(context,knowledgeMode='retained',contextId=hedge_catalog['context']['contextId'])
    hedge_request=dict(context=hedge_context,bookIds=[request['bookId']],operatingTradeId=saved['tradeId'],hedgeTradeIds=[hedge['tradeId']],
        bumps=[dict(factor=factor['factor'],size='1',convention=hedge_catalog['bumpConvention'])],convention='operating_right_price_hedge_v1',
        numericalPolicy=hedge_catalog['numericalPolicy'],pricingPolicy=hedge_catalog['pricingPolicy'],aggregationPolicy='shared_price_changes_v1')
    hedged=client.call(route+'hedges',hedge_request)
    assert hedged['valuation']['context']['contextId']==hedge_context['contextId']
    assert len(hedged['valuation']['positions'])==2
    assert all(Fraction(r['operating']['upPnl'])==0 and r['net']==r['hedges'] for r in hedged['priceChanges'])
    assert any(Fraction(r['hedges']['upPnl'])!=0 for r in hedged['priceChanges'])
    empty=deepcopy(hedge_request);empty['bumps']=[]
    assert client.call(route+'hedges',empty,expected=422)['code']=='power_hedge_contract'
    from check_power_market_http import run_checks as check_market_price
    check_market_price(client,request,hedge['tradeId'],context)
    client.select_inputs(client.observed_inputs[retained_selection['contextId']])
    assert client.call(route+'get',retained_get)==exact_retained, \
        'Later facility and operating publications must not change the exact retained operating source or Side membership receipt.'
    assert client.call(route+'get',selected)['sides']==reopened['sides'], \
        'The explicit-current legacy operating inspector must remain compatible.'
    overlap=deepcopy(request);overlap['operationId']=str(uuid4())
    overlap['plan']['batteryDispatch'][0]['chargeMw']='8'
    overlap['plan']['batteryDispatch'][1]['dischargeMw']='6.48'
    assert client.call(route+'book',overlap,expected=409,timeout=60)['code']=='operating_right_overlap'
    check_composition(client,query,manifest,request,route,frame,context)
    # Exact retained reopening cannot substitute a new facility revision or a new
    # plan, and a historical view cannot admit another contractual right.
    client.call('market/context',dict(effectiveOn='2020-12-15',knownAt=value['knownAt'],mode='at',selection='latest_correction'))
    historical=initial_frame(client.call('workspace'),'2020-12-15')['id']
    retained=dict(snapshotId=historical,tradeId=saved['tradeId'])
    a=client.call(route+'valuation',retained)
    assert a==client.call(route+'valuation',retained)
    assert fraction(a['valuation']['value'])==610
    assert client.call(route+'book',request,expected=422)['code']=='historical_read_only'
    client.call('market/current',{})
    client.call('profile',{'profile':'editor'})
    assert client.call(route+'status',{'operationId':request['operationId']},expected=403)['code']=='access_denied'
    print('PASS operating trades HTTP: durable booking, first outcomes, semantic no-op, exclusive rights, retained Side PV, ordinary positions and certified reopening',flush=True)
    return dict(tradeId=saved['tradeId'],tradeReference=reopened['tradeReference'],facilityId=reopened['facilityId'],
        facilityVersion=reopened['facilityVersion'],bookId=request['bookId'],snapshotId=frame,
        hedge=dict(tradeId=hedge['tradeId'],tradeReference=held_hedge['tradeReference'],bookId=request['bookId'],curveCode=future_contract['curve']))


def check_composition(client,query,manifest,legacy,route,frame,context):
    from datetime import date,timedelta
    request=deepcopy(legacy);request['operationId']=str(uuid4())
    request['reason']='Synthetic independent operating Side assembly'
    plan=request['plan'];financial=plan['valuation']
    day=(date.fromisoformat(plan['grid']['operatingDate'])+timedelta(days=10)).isoformat()
    plan['grid']['operatingDate']=day;plan['batteryState']['at']=day+'T00:00:00Z'
    def side(key,name,source,price,coefficient,days,kind='payment'):
        return dict(key=key,name=name,quantitySource=source,coefficient=coefficient,kind=kind,price=deepcopy(price),
            paymentRule=dict(financial['paymentRule'],days=days) if kind=='payment' else None)
    plan['valuation']=None
    plan['composition']=dict(implementation='operating_side_composition_v2',perspective=financial['perspective'],measure=financial['measure'],
        currencyCode=financial['currencyCode'],rightQuantityCode=financial['rightQuantityCode'],discounting=False,sides=[
        side('output','Synthetic output','discharging_energy',financial['energyPrice'],'1',18,'delivery'),
        side('charging','Synthetic charging','charging_energy',financial['chargingPrice'],'-1',18),
        side('fee_a','Synthetic fee','grid_throughput',dict(financial['energyPrice'],fixedPrice='1'),'-1',7),
        side('fee_b','Synthetic fee','grid_throughput',dict(financial['energyPrice'],fixedPrice='2'),'-1',25),
        side('access','Synthetic access','horizon_right',dict(financial['energyPrice'],quoteCode='test_resource_right_quote',fixedPrice='5'),'-1',10)])
    expected=Fraction(5507,10)
    preview=client.call(route+'preview',request)
    assert fraction(preview['valuation']['value'])==expected
    saved=client.call(route+'book',request,timeout=60)
    assert saved['changed'] and client.call(route+'book',request)==saved
    exact=dict(snapshotId=frame,tradeId=saved['tradeId'])
    reopened=client.call(route+'get',exact)
    assert reopened['terms']==request and len(reopened['sides'])==5
    assert len({s['sideId'] for s in reopened['sides']})==5
    value=client.call(route+'valuation',exact)['valuation']
    assert fraction(value['value'])==expected
    assert [s['coefficient'] for s in value['sides']]==['1','-1','-1','-1','-1']
    assert value['sides'][2]['rows'][0]['paymentAt']!=value['sides'][3]['rows'][0]['paymentAt']
    assert value['sides'][0]['rows'][0]['paymentAt']==value['sides'][0]['rows'][0]['interval']['end']
    ordinary=client.call('trading/position',dict(context=context,tradeId=saved['tradeId'],bindingId=saved['bindingId'],revision=1,instrumentType='operating_right'))['holding']
    assert Fraction(ordinary['presentValue'])==expected and ordinary['sideSchedules'] is True
    duplicate=deepcopy(request);duplicate['operationId']=str(uuid4())
    assert client.call(route+'book',duplicate,timeout=60)['tradeId']==saved['tradeId']
    # A changed sign is a different contract and cannot spend the same exclusive resource right.
    conflict=deepcopy(duplicate);conflict['operationId']=str(uuid4());conflict['plan']['composition']['sides'][2]['coefficient']='1'
    assert client.call(route+'book',conflict,expected=409,timeout=60)['code']=='operating_right_overlap'
    reused=deepcopy(request);reused['operationId']=str(uuid4())
    next_day=(date.fromisoformat(day)+timedelta(days=1)).isoformat()
    reused['plan']['grid']['operatingDate']=next_day;reused['plan']['batteryState']['at']=next_day+'T00:00:00Z'
    same=deepcopy(reused['plan']['composition']['sides'][2]);same['key']='fee_reused'
    reused['plan']['composition']['sides'].append(same)
    repeated=client.call(route+'book',reused,timeout=60)
    repeated_read=client.call(route+'get',dict(snapshotId=frame,tradeId=repeated['tradeId']))
    assert repeated_read['sides'][2]['sideId']==repeated_read['sides'][5]['sideId']
    repeated_value=client.call(route+'valuation',dict(snapshotId=frame,tradeId=repeated['tradeId']))['valuation']
    assert fraction(repeated_value['value'])==Fraction(5326,10) and len(repeated_value['sides'])==6
    repeated_holding=client.call('trading/position',dict(context=context,tradeId=repeated['tradeId'],bindingId=repeated['bindingId'],revision=1,instrumentType='operating_right'))['holding']
    assert Fraction(repeated_holding['presentValue'])==Fraction(5326,10)
    position=dict(context=dict(context),tradeId=repeated['tradeId'],bindingId=repeated['bindingId'],revision=1,instrumentType='operating_right')
    inspection=dict(position=position,sideSlot=None,contribution=None,offset=0,limit=32)
    directory=client.call('trading/side-schedules',inspection)
    assert len(directory['sides'])==6
    position['context'].update(knowledgeMode='retained',contextId=directory['contextId'])
    for side,total in zip(directory['sides'],map(Fraction,['810','-200','-18.1','-36.2','-5','-18.1'])):
        selected=dict(inspection,sideSlot=side['slot'])
        page=client.call('trading/side-schedules',selected)['page']
        assert page['manifest']==side['manifest'] and sum(Fraction(row['presentValue']) for row in page['rows'])==total
        detail=client.call('trading/side-schedules',dict(selected,contribution=page['rows'][0]['key']))['page']
        assert detail['rows'] and detail['valueBasis']=='unit_side_v1'
    from check_operating_composition_authority import run_checks as check_authority
    check_authority(query,manifest,request,saved)
    print('PASS operating Side assembly HTTP: repeated display names/sources, five concrete schedules, delivery, independent dates, exact saved PV, replay and exclusivity',flush=True)
