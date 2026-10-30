"""Synthetic end-to-end scheduled swap contracts; independent cash oracles."""
from copy import deepcopy
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking, CAT, R, REV, identity, installed_tzdb
from datetime import datetime, timezone
import json
from fractions import Fraction
from check_blotter_desk_fields import check_money_totals, exact, total, unvalued
from check_blotter_valuation_status import BY_BOOK, blotter, book_key, excluded, key, risk_catalog, risk_request, unvalued_rows
from check_swap_settlement import check_settlement_keeps_facts, check_stated_sides, held_row

# A close after the January determinations whose pins leave out the February future, so that no swap still projecting February can be
# valued there. The scheduled power markets are observed there as at every close.
UNPRICED_FEBRUARY='2021-01-16T08:00:00'


def prepare(market,manifest):
    for frame in manifest['frames']:frame['observationIds']=list(frame['observationIds'])
    base=next(f for f in manifest['frames'] if f['at'].startswith('2021-01-31T08:00:00'))
    for at in ('2021-02-01T08:00:00Z','2021-03-18T00:00:00Z','2021-03-19T08:00:00Z','2021-03-20T00:00:00Z'):
        manifest['frames'].append({**base,'at':at,'observationIds':list(base['observationIds'])})
    partial=next(f for f in manifest['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    february_future=identity('7',4000000+1*10000+101)
    assert february_future in partial['observationIds']
    manifest['frames'].append({**partial,'at':UNPRICED_FEBRUARY+'Z','observationIds':[o for o in partial['observationIds'] if o!=february_future]})
    reference=f"INSERT INTO ref_data.quantity_specifications VALUES ({R},'synthetic_gas_energy_b','test_gas','test_gas_energy','energy_b','energy');\nINSERT INTO ref_data.measurement_bases VALUES ({R},'synthetic_power_rate','test_power','power');\nINSERT INTO ref_data.quantity_specifications VALUES ({R},'synthetic_power_mw','test_power','synthetic_power_rate','megawatt','power');\nINSERT INTO ref_data.clock_rules(catalog_id,revision_id,code,kind,iana_zone,tzdb_version) VALUES ({R},'synthetic_london','iana','Europe/London','{installed_tzdb()}');"
    reference+=f"\nINSERT INTO ref_data.clock_rules(catalog_id,revision_id,code,kind,iana_zone,tzdb_version) VALUES ({R},'synthetic_new_york','iana','America/New_York','{installed_tzdb()}');"
    market=market.replace('UPDATE ref_data.revisions SET',reference+'\nUPDATE ref_data.revisions SET',1)
    definitions=[f"INSERT INTO market_data.curves VALUES ('{CAT}','{REV}','scheduled_power',{R},'test_power','test_hub_a','test_power_outright','test_uniform','test_power_target','direct','fixture','trader_mark','Synthetic scheduled power');"]
    observations=[]
    for ordinal,(start,end,price) in enumerate([('2021-10-01','2021-11-01','45'),('2021-11-01','2021-12-01','50')]):
        contract,series=identity('5',85000+ordinal),identity('9',85000+ordinal)
        definitions.extend([f"INSERT INTO market_data.contracts VALUES ('{CAT}','{REV}','{contract}',{R},'forward','{start}','{end}','{end} 00:00:00+00',NULL);",
            f"INSERT INTO market_data.curve_members VALUES ('{CAT}','{REV}','scheduled_power','{contract}',{ordinal});",
            f"INSERT INTO market_data.series VALUES ('{CAT}','{REV}','{series}','scheduled_power_{ordinal}',{R},'{contract}','test_power_outright',NULL);",
            f"INSERT INTO market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation) VALUES ('{CAT}','{REV}','test_power_price','{start}','{end}',{R},'test_power_outright','{series}','fixture','{start} 00:00:00+00','published_index_observations_v1');"])
        for sample,frame in enumerate(manifest['frames']):
            oid=identity('7',8500000+ordinal*100+sample);frame['observationIds'].append(oid)
            observations.append(f"INSERT INTO market_data.observations VALUES ('{CAT}','{oid}','{REV}','{series}',{R},'fixture','scheduled_{ordinal}_{sample}','1','trader_mark','final','{frame['at']}','{datetime.now(timezone.utc).isoformat()}',{price},NULL);")
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+'\n'+'\n'.join(observations)+'\nCOMMIT;\n'
    from check_contract_observation_booking import prepare as prepare_contract_observations
    return prepare_contract_observations(market,manifest)


def check_unvalued_lifecycle(c,workspace,trade,run_worker):
    """A swap whose settlement lifecycle is under way, at a close that cannot value what remains of it (D220).

    Its row states why, with no PV, mark or lifecycle measures, and every other fact it states where the close values it; totals leave
    it out, and a risk run, the same run queued and the risk catalog list it with that reason.
    """
    def frame(at):return next(f for f in workspace['frames'] if f['at'].startswith(at))
    valued,valued_context=blotter(c,frame('2021-01-15T08:00:00'))
    late,late_context=blotter(c,frame(UNPRICED_FEBRUARY))
    before,after=(next(r for r in read['holdings'] if r['tradeId']==trade) for read in (valued,late))
    assert before['settlement']=='Active' and before['unavailableReason'] is None and before['lifecycle'] is not None,before
    assert after['unavailableReason']=='missing_observation' and after['settlement']=='Active',after
    assert after['presentValue'] is None and after['mark'] is None and after['lifecycle'] is None,after
    changed={k for k in before.keys()|after.keys() if before.get(k)!=after.get(k)}
    assert changed<={'presentValue','mark','lifecycle','unavailableReason'},sorted(changed)
    rows=late['holdings']
    group=next(g for g in blotter(c,frame(UNPRICED_FEBRUARY),BY_BOOK)[0]['groups'] if book_key(g)==book_key(after))
    held=[r for r in rows if book_key(r)==book_key(after)]
    assert group['unvaluedPositions']==unvalued(held)>0 and exact(group['presentValue'])==total(held,'presentValue'),group
    assert late['unvaluedPositions']==unvalued(rows)
    def check(name,condition):assert condition,name
    # The desk's money totals (D223) add the valued positions across quantity units and count those they leave out, this one among them.
    check_money_totals(lambda view:blotter(c,frame(UNPRICED_FEBRUARY),view)[0],check,rows,'the unpriced-February close')
    c.call('profile',{'profile':'risk'})
    # The risk report's presets shock what a position is priced from. At the close that values the swap, what remains of its lifecycle
    # moves under a contract target the catalog names as held, in parts of at most sixteen shocks.
    valued_catalog=risk_catalog(c,valued_context)
    targets={t['id']:t for t in valued_catalog['scenarioTargets']}
    def moves(ids):
        request={**risk_request(valued_catalog,valued_context,None),'bumps':[{'factor':targets[i]['factor'],'size':'0.01','convention':valued_catalog['bumpConvention']} for i in ids]}
        return any(p['tradeId']==trade and Fraction(p['values']['delta'])!=0 for f in c.call('portfolio/risk',request)['factors'] for p in f['positions'])
    ids=valued_catalog['heldContractTargets']
    assert ids and all(targets[i]['kind']=='contract' for i in ids) and any(moves(ids[i:i+16]) for i in range(0,len(ids),16)),ids
    choices=risk_catalog(c,late_context)
    factor=choices['factors'][0]['factor']
    report=c.call('portfolio/risk',risk_request(choices,late_context,factor))
    assert key(after) in {key(e) for e in report['excludedPositions']} and excluded(report)==unvalued_rows(rows),report['excludedPositions']
    assert report['work']['positionCount']==sum(r['unavailableReason'] is None for r in rows)
    assert excluded(choices)==excluded(report)
    job=c.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':risk_request(choices,late_context,factor)})
    run_worker()
    completed=c.call('portfolio/risk/jobs/'+job['id'])
    assert completed['state']=='completed' and completed['result']['excludedPositions']==report['excludedPositions'],completed
    c.call('profile',{'profile':'trader'})
    print('PASS a swap whose settlement lifecycle the close cannot value states why with no PV or mark and keeps its facts; totals, a risk run, its job and the risk catalog leave it out and list it, and where the close values it, it moves under the held contract targets')


def run_checks(url,query=None,manifest=None,*,run_worker):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    w=c.call('workspace');f=next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context=dict(snapshotId=f['id'],knowledgeMode='current',convention='independent_side_financial_swap_v2')
    catalog=c.call('swaps/catalog',context)
    index=next(i for i in catalog['indices'] if i['curve']=='base_forward')
    payment=dict(frequency='monthly',rule='days_after_period_end',days=18,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    def floating(start,end):
        return dict(settlement=dict(mode='curve_target',index=None,projectionPolicy=None),curve=index['curve'],index=index['index'],currency=index['currencyCode'],deliveryFrom=start,deliveryTo=end,paymentSchedule=payment,resetFrequency='monthly',averagingMethod='none',spread='0')
    def fixed(start,end):
        return dict(price='3',currency=index['currencyCode'],deliveryFrom=start,deliveryTo=end,paymentSchedule=payment)
    periods=[('2021-01-01','2021-02-01'),('2021-02-01','2021-03-01')]
    def side(fixed_side,amounts):
        return dict(basis='period_total',quantity=index['quantityChoices'][0]['specification'],amount=None,allocation=None,priceApplication='period_average_times_quantity_v1',power=None,
            periods=[dict(amount=amount,fixed=fixed(*period) if fixed_side else None,floating=None if fixed_side else floating(*period)) for period,amount in zip(periods,amounts)])
    body={**context,'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'executedAt':None,'quantity':None,'fixedCoefficient':None,'discounting':False,'fixed':None,'floating':None,
          'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'swapType':'scheduled_financial_swap','firstCoefficient':'-1',
          'scheduled':dict(implementation='whole_term_scheduled_financial_swap_v1',first=side(True,['100','200']),second=side(False,['150','100'])),**trade_booking()}
    missing_selection=deepcopy(body);del missing_selection['scheduled']['second']['periods'][1]['floating']['settlement']
    assert c.call('swaps/preview',missing_selection,expected=400)['code']=='request_format'
    null_selection=deepcopy(body);null_selection['scheduled']['second']['periods'][1]['floating']['settlement']=None
    assert c.call('swaps/preview',null_selection,expected=422)['code']=='price_settlement_selection'
    def preview(b):return c.call('swaps/preview',b)['valuation']
    value=preview(body)
    # Floating marks are taken from the independently priced one-month contracts;
    # quantities and cash aggregation below are an independent decimal oracle.
    payments=[p for s in value['sides'] for p in s['payments']]
    assert [Decimal(p['resolvedQuantity']) for p in payments]==[100,200,150,100]
    expected=sum(Decimal(p['tradeCashAmount']) for p in payments)
    assert expected==Decimal(value['presentValue'])
    assert [Decimal(p['tradeCashAmount']) for p in payments[:2]]==[-300,-600]
    saved=c.call('swaps/book',body);assert c.call('swaps/book',body)==saved
    detail=c.call('swaps/editor',dict(context=context,tradeId=saved['tradeId'],revision=None))
    assert detail['valuation']['presentValue']==value['presentValue']
    assert detail['state']['terms']['scheduled']['first']['basis']=='period_total'
    daily=deepcopy(body);daily['operationId']=str(uuid4())
    for s in daily['scheduled'].values():
        if isinstance(s,dict):
            s['basis']='calendar_day_rate'
            for p in s['periods']:p['amount']='10'
    daily_value=preview(daily)
    assert [Decimal(p['resolvedQuantity']) for s in daily_value['sides'] for p in s['payments']]==[310,280,310,280]
    total=deepcopy(body);total['operationId']=str(uuid4())
    for s in (total['scheduled']['first'],total['scheduled']['second']):
        s.update(basis='contract_total',amount='590',allocation='calendar_days')
        for p in s['periods']:p['amount']=None
    total_value=preview(total)
    assert [Decimal(p['resolvedQuantity']) for s in total_value['sides'] for p in s['payments']]==[310,280,310,280]
    assert total_value['presentValue']==daily_value['presentValue']
    invalid=deepcopy(total);invalid['scheduled']['first']['allocation']=None
    c.call('swaps/preview',invalid,expected=422)
    invalid=deepcopy(body);invalid['scheduled']['first']['periods'][0]['fixed']['price']=None
    c.call('swaps/preview',invalid,expected=422)
    amend=deepcopy(body);amend.update(operationId=str(uuid4()),tradeId=saved['tradeId'],expectedRevision=1,effectiveAt=f['at'],reason='Synthetic explicit period quantity amendment')
    unchanged=deepcopy(amend)
    assert c.call('swaps/preview',unchanged)['changeReview']['action']=='keep_booked_terms'
    c.call('swaps/amend',unchanged,expected=422)
    amend['scheduled']['first']['periods'][1]['amount']='210'
    priced=c.call('swaps/scheduled-price',dict(terms=amend,priceMode='agreed',parRatePolicy=None))
    assert priced['expectedRevision']==1 and priced['changeReview']['bookedRevision']==1
    review=c.call('swaps/preview',amend)['changeReview']
    assert review['action']=='amend_trade' and review['bookedRevision']==1
    assert not any(r['field']=='Quantity' for r in review['changes'])
    assert any(r['field'].endswith('periods / 2 / amount') and r['before']=='200' and r['after']=='210' for r in review['changes'])
    amended=c.call('swaps/amend',amend);assert amended['revision']==2
    original=c.call('swaps/editor',dict(context=context,tradeId=saved['tradeId'],revision=1))
    assert original['valuation']['presentValue']==value['presentValue']
    changed=c.call('swaps/editor',dict(context=context,tradeId=saved['tradeId'],revision=2))
    assert Decimal(changed['valuation']['presentValue'])==expected-30
    assert c.call('swaps/amend',amend)==amended
    converted=deepcopy(body)
    other=next(q['specification'] for q in index['quantityChoices'] if q['specification']['code']=='synthetic_gas_energy_b')
    for s in (converted['scheduled']['first'],converted['scheduled']['second']):
        s['quantity']=other
        for p in s['periods']:p['amount']=str(Decimal(p['amount'])*4)
    assert preview(converted)['presentValue']==value['presentValue']
    thirds=deepcopy(total)
    for name,amount in [('first','100'),('second','50')]:
        s=thirds['scheduled'][name];s.update(amount=amount,allocation='equal_periods')
        p=deepcopy(s['periods'][-1]);(p['fixed'] or p['floating']).update(deliveryFrom='2021-03-01',deliveryTo='2021-04-01');s['periods'].append(p)
    thirds_value=preview(thirds)
    assert [p['resolvedQuantity'] for p in thirds_value['sides'][0]['payments']]==['100/3']*3
    assert Decimal(thirds_value['presentValue'])==Decimal('-142.25')
    thirds['operationId']=str(uuid4());thirds_saved=c.call('swaps/book',thirds)
    thirds_read=c.call('swaps/editor',dict(context=context,tradeId=thirds_saved['tradeId'],revision=None))
    assert thirds_read['valuation']['presentValue']==thirds_value['presentValue']
    discounted=deepcopy(body);discounted['discounting']=True
    for p in discounted['scheduled']['second']['periods']:p['floating']['paymentSchedule']={**payment,'days':46}
    discounted_value=preview(discounted)
    from fractions import Fraction
    assert sum((Fraction(p['tradeCashAmount'])*Fraction(p['discountFactor']) for side in discounted_value['sides'] for p in side['payments']),Fraction())==Fraction(discounted_value['presentValue'])
    assert discounted_value['sides'][0]['payments'][0]['paymentDate']!=discounted_value['sides'][1]['payments'][0]['paymentDate']
    reversed_body=deepcopy(body);reversed_body['firstCoefficient']='1'
    assert Decimal(preview(reversed_body)['presentValue'])==-expected
    mixed_prices=deepcopy(body);mixed_prices['operationId']=str(uuid4());mixed_prices['scheduled']['first']['periods'][1]['fixed']['price']='4';mixed_prices['scheduled']['second']['periods'][1]['floating']['curve']='local_gas'
    mixed_saved=c.call('swaps/book',mixed_prices)
    report=c.call('trading/blotter',dict(context={**context,'convention':'trading_workspace_v1'},view=dict(mode='trades',bookIds=[],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)))
    uniform=next(row for row in report['holdings'] if row['tradeId']==saved['tradeId'])
    assert uniform['fixedPrice']=='3',uniform
    reported=next(r for r in report['holdings'] if r['tradeId']==mixed_saved['tradeId'])
    assert reported['quantity'] is None and reported['fixedPrice'] is None and reported['mark'] is None
    assert reported['deliveryTo']=='2021-03-01' and set(reported['projectionCurves'])=={'base_forward','local_gas'}
    legacy={k:v for k,v in body.items() if k!='scheduled'};legacy.update(operationId=str(uuid4()),quantity='100',fixedCoefficient='-1',firstCoefficient=None,swapType='financial_fixed_floating',fixed=deepcopy(body['scheduled']['first']['periods'][0]['fixed']),floating=deepcopy(body['scheduled']['second']['periods'][0]['floating']))
    legacy_saved=c.call('swaps/book',legacy)
    assert c.call('swaps/editor',dict(context=context,tradeId=legacy_saved['tradeId'],revision=None))['valuation']['presentValue']=='10'
    negative=deepcopy(body);negative['scheduled']['first']['periods'][0]['fixed']['price']='-3';negative['scheduled']['first']['periods'][1]['fixed']['price']='-4'
    assert Decimal(preview(negative)['presentValue'])==expected+2000
    shifted=deepcopy(body)
    for n,p in enumerate(shifted['scheduled']['second']['periods']):p['floating'].update(deliveryFrom=['2021-02-01','2021-03-01'][n],deliveryTo=['2021-03-01','2021-04-01'][n])
    shifted_value=preview(shifted)
    assert [p['deliveryFrom'] for p in shifted_value['sides'][1]['payments']]==['2021-02-01','2021-03-01']
    # Base_forward = future + explicitly quoted bridge basis: February3.155, March3.21.
    assert Decimal(shifted_value['presentValue'])==Decimal('-105.75')
    par=deepcopy(body)
    for p in par['scheduled']['first']['periods']:p['fixed']['price']=None
    par_result=c.call('swaps/scheduled-price',dict(terms=par,priceMode='par',parRatePolicy=dict(implementation='par_fixed_price_to_even_v1',decimalPlaces=8,tieRule='to_even')))
    assert all(p['fixed']['price'] for p in par_result['terms']['scheduled']['first']['periods'])
    assert abs(Decimal(par_result['valuation']['presentValue']))<=Decimal('0.000003')
    c.call('swaps/scheduled-price',dict(terms=par,priceMode='agreed',parRatePolicy=None),expected=422)
    identical=deepcopy(body);identical['operationId']=str(uuid4());identical['scheduled']['first']=deepcopy(identical['scheduled']['second'])
    offset=c.call('swaps/book',identical)
    offset_view=c.call('swaps/editor',dict(context=context,tradeId=offset['tradeId'],revision=None))
    assert Decimal(offset_view['valuation']['presentValue'])==0
    power_index=next(i for i in catalog['indices'] if i['curve']=='scheduled_power')
    power=deepcopy(body);power.update(operationId=str(uuid4()),commodity=power_index['commodity'])
    mw=next(q['specification'] for q in catalog['scheduledOptions']['powerQuantities'] if q['commodity']==power_index['commodity'])
    london=next(q['reference'] for q in catalog['scheduledOptions']['clocks'] if q['reference']['code']=='synthetic_london')
    for slot,name in enumerate(['first','second']):
        side=power['scheduled'][name];side.update(basis='selected_interval_power',quantity=mw,power=dict(clock=london,product='all_hours',startHour=None,endHour=None,saturday=None,calendar=None))
        for n,p in enumerate(side['periods']):
            p['amount']='2' if slot==0 else '1'
            term=p['fixed'] or p['floating'];term.update(deliveryFrom=['2021-10-01','2021-11-01'][n],deliveryTo=['2021-11-01','2021-12-01'][n])
            if slot==0:term['price']='40'
            else:term.update(curve=power_index['curve'],index=power_index['index'])
    power_value=preview(power)
    assert [Decimal(p['resolvedQuantity']) for s in power_value['sides'] for p in s['payments']]==[1490,1440,745,720]
    assert Decimal(power_value['presentValue'])==-47675
    power_saved=c.call('swaps/book',power)
    retained=c.call('swaps/editor',dict(context=context,tradeId=power_saved['tradeId'],revision=None))
    assert retained['valuation']['presentValue']==power_value['presentValue']
    if query:
        assert query(f"SELECT count(*)::text FROM trading.swap_power_quantity_intervals WHERE trade_id='{power_saved['tradeId']}';").strip()=='2930'
    lineage=c.call('swaps/quantity-schedule',dict(context=context,tradeId=power_saved['tradeId'],revision=1))
    assert [q['slot'] for q in lineage['quantityInputs']]==[0,1] and lineage['intervals'][0]['slot']==0 and lineage['intervals'][0]['period']==0
    assert len(lineage['intervals'])==2930 and lineage['quantityInputs'][0]['periods'][0]['deliveryHours']=='745'
    retained_context={**context,'knowledgeMode':'retained','contextId':retained['context']['contextId']}
    replay=c.call('swaps/quantity-schedule',dict(context=retained_context,tradeId=power_saved['tradeId'],revision=1))
    assert replay['intervals']==lineage['intervals'] and replay['quantityInputs']==lineage['quantityInputs']
    assert lineage['canBook'] and not replay['canBook']
    if query:
        # The narrow bulk reader must not turn an unbound runtime into an owner read.
        query(f"""SET SESSION AUTHORIZATION preview_runtime; DO $$BEGIN
         BEGIN PERFORM security.read_swap_power_quantity_intervals('{CAT}','{power_saved['tradeId']}',1);
          RAISE EXCEPTION 'Unbound quantity read succeeded';
         EXCEPTION WHEN insufficient_privilege THEN NULL;END;
        END$$; RESET SESSION AUTHORIZATION;""")
        query('REVOKE EXECUTE ON FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer) FROM openetrm_runtime;')
        try:
            assert c.call('swaps/quantity-schedule',dict(context=context,tradeId=power_saved['tradeId'],revision=1),expected=403)['code']=='access_denied'
        finally:
            query('GRANT EXECUTE ON FUNCTION security.read_swap_power_quantity_intervals(uuid,uuid,integer) TO openetrm_runtime;')
    if query and run_worker is not None:
        from check_swap_quantity_worker import run as check_quantity_worker
        check_quantity_worker(url,query,manifest,f['at'],power_saved['tradeId'])
    print('PASS retained power quantity read: 2930 exact intervals fit the normal request deadline; unbound authority refuses',flush=True)
    assert retained['quantityInputs'][1]['periods'][1]['deliveryHours']=='720'
    new_york=next(q['reference'] for q in catalog['scheduledOptions']['clocks'] if q['reference']['code']=='synthetic_new_york')
    clocked=deepcopy(power);clocked['operationId']=str(uuid4())
    for side in (clocked['scheduled']['first'],clocked['scheduled']['second']):side['power']['clock']=new_york
    clocked_value=preview(clocked)
    assert [p['resolvedQuantity'] for s in clocked_value['sides'] for p in s['payments']]==['1488','1442','744','721']
    assert clocked_value['presentValue']=='-47670'
    clocked_saved=c.call('swaps/book',clocked)
    clocked_request=dict(context=context,tradeId=clocked_saved['tradeId'],expectedRevision=1,membershipOrdinal=0,paymentOrdinal=0,
        currency=clocked_value['currency'],decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    clocked_preview=c.call('swaps/settlement/preview',clocked_request)
    assert clocked_preview['calculation']['completion']['at'].startswith('2021-11-01T04:00:00')
    assert clocked_preview['calculation']['completion']['source']=='retained_quantity_interval_end_v1'
    c.call('swaps/settlement/determine',dict(preview=clocked_request,operationId=str(uuid4()),expectedVersion=0,previewHash=clocked_preview['previewHash'],
        duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None))
    if query:
        assert query(f"SELECT extract(hour FROM completion_at AT TIME ZONE 'UTC') FROM settlement_data.swap_occurrences WHERE trade_id='{clocked_saved['tradeId']}';").strip()=='4'
    print('PASS clocked financial settlement pins retained New York interval completion, independent of UTC date labels')
    old_context={**context,'snapshotId':next(f['id'] for f in w['frames'] if f['at'].startswith('2020-12-15'))}
    c.call('swaps/quantity-schedule',dict(context=old_context,tradeId=power_saved['tradeId'],revision=1),expected=422)
    c.call('profile',{'profile':'risk'})
    selected={**context,'convention':'trading_workspace_v1'}
    risk_catalog=c.call('portfolio/risk/catalog',dict(context=selected,bookIds=[body['bookId']],portfolioIds=[]))
    risk=c.call('portfolio/risk',dict(context={**selected,'knowledgeMode':'retained','contextId':risk_catalog['context']['contextId']},bookIds=[body['bookId']],portfolioIds=[],detail='holdings',bumps=[dict(factor=f['factor'],size='0.1',convention=risk_catalog['bumpConvention']) for f in risk_catalog['factors']],stress=[],convention=risk_catalog['convention'],numericalPolicy=risk_catalog['numericalPolicy'],pricingPolicy=risk_catalog['pricingPolicy']))
    power_factors=[f for f in risk['factors'] if any(p['tradeId']==power_saved['tradeId'] and Decimal(p['values']['delta'])!=0 for p in f['positions'])]
    assert sorted(Decimal(next(p['values']['delta'] for p in f['positions'] if p['tradeId']==power_saved['tradeId'])) for f in power_factors)==[720,745]
    c.call('profile',{'profile':'trader'})
    partial=next(f for f in w['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    mixed=c.call('swaps/editor',dict(context={**context,'snapshotId':partial['id']},tradeId=saved['tradeId'],revision=1))
    assert [p['resets'][0]['fixingState'] for p in mixed['valuation']['sides'][1]['payments']]==['Actual','Projected']
    assert not mixed['canAmend']
    settlement_context={**context,'snapshotId':partial['id']}
    settlement=dict(context=settlement_context,tradeId=thirds_saved['tradeId'],expectedRevision=1,membershipOrdinal=1,paymentOrdinal=0,
        currency=thirds_value['currency'],decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    determined=c.call('swaps/settlement/preview',settlement)
    assert determined['calculation']['exactAmount']=='175/3'
    assert determined['calculation']['amount']=='58.33'
    assert determined['previousTotal']=='0' and determined['proposedContribution']=='58.33' and determined['inputsChanged']
    assert determined['calculation']['resolvedQuantity']=='50/3'
    assert determined['calculation']['prices'][0]['state']==3
    assert determined['context']['contextId']
    c.call('swaps/settlement/preview',{**settlement,'paymentOrdinal':1},expected=422)
    c.call('swaps/settlement/preview',{**settlement,'expectedRevision':2},expected=409)
    c.call('swaps/settlement/preview',{**settlement,'roundingPolicy':''},expected=422)
    c.call('swaps/settlement/preview',{**settlement,'decimalPlaces':0},expected=422)
    for required in ('membershipOrdinal','paymentOrdinal','decimalPlaces','expectedRevision'):
        missing={k:v for k,v in settlement.items() if k!=required}
        c.call('swaps/settlement/preview',missing,expected=400)
    determine=dict(preview=settlement,operationId=str(uuid4()),expectedVersion=0,previewHash=determined['previewHash'],
        duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',
        correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None)
    inspect=dict(context=settlement_context,tradeId=thirds_saved['tradeId'],revision=None,duePolicy=determine['duePolicy'],recognitionPolicy=determine['recognitionPolicy'])
    initial_lifecycle=c.call('swaps/settlement/inspect',inspect)
    projected=held_row(c,settlement_context,thirds_saved['tradeId'])
    accepted=c.call('swaps/settlement/determine',determine)
    assert accepted['total']=='58.33' and accepted['adjustment']=='58.33' and accepted['version']==1
    assert c.call('swaps/settlement/determine',determine)==accepted
    historical={**settlement,'context':{**settlement_context,'knowledgeMode':'retained','contextId':determined['context']['contextId']}}
    assert c.call('swaps/settlement/preview',historical)['settlement']['version']==0
    current=c.call('swaps/settlement/preview',settlement)
    assert current['settlement']['version']==1 and len(current['settlement']['determinations'])==1
    assert current['previousTotal']=='58.33' and current['proposedContribution']=='0' and not current['inputsChanged']
    c.call('swaps/settlement/determine',{**determine,'operationId':str(uuid4()),'expectedVersion':1,'previewHash':current['previewHash']},expected=422)
    c.call('swaps/settlement/determine',{**determine,'previewHash':'changed'},expected=409)
    if query:
        assert query(f"SELECT count(*) FROM settlement_data.swap_determinations WHERE trade_id='{thirds_saved['tradeId']}';").strip()=='1'
    print('PASS swap settlement determination: durable original, exact retry, immutable history and certified replay')
    payment=dict(context=settlement_context,tradeId=thirds_saved['tradeId'],operationId=str(uuid4()),expectedRevision=1,expectedVersion=1,
        determinationId=accepted['determinationId'],amount='40',currency=thirds_value['currency'],effectiveAt=partial['at'],reference='Synthetic partial cash receipt',reversesId=None)
    paid=c.call('swaps/settlement/payment',payment)
    assert paid['amount']=='40' and paid['version']==2
    assert c.call('swaps/settlement/payment',payment)==paid
    balance=c.call('swaps/settlement/preview',settlement)['settlement']
    assert balance['version']==2 and len(balance['payments'])==1
    c.call('swaps/settlement/payment',{**payment,'operationId':str(uuid4()),'expectedVersion':2,'amount':'20'},expected=422)
    reversal={**payment,'operationId':str(uuid4()),'expectedVersion':2,'amount':'-40','reversesId':paid['paymentId'],'reference':'Synthetic reversal'}
    reversed_payment=c.call('swaps/settlement/payment',reversal)
    assert reversed_payment['version']==3
    c.call('swaps/settlement/payment',{**reversal,'operationId':str(uuid4()),'expectedVersion':3},expected=422)
    completed=c.call('swaps/settlement/payment',{**payment,'operationId':str(uuid4()),'expectedVersion':3,'amount':'58.33','reference':'Synthetic full receipt'})
    assert completed['version']==4
    lifecycle=c.call('swaps/settlement/inspect',inspect)
    from fractions import Fraction as F
    assert lifecycle['valuation']['realized']=='0'
    paid_january=next(p for p in lifecycle['valuation']['payments'] if p['membershipOrdinal']==1 and p['ordinal']==0)
    assert paid_january['state']=='Paid' and paid_january['remainingValue']=='0'
    for old,new in zip(initial_lifecycle['valuation']['payments'],lifecycle['valuation']['payments']):
        if old['membershipOrdinal']==1 and old['ordinal']==0:continue
        assert old['remainingValue']==new['remainingValue']
    assert F(lifecycle['valuation']['remainingValue'])+F(lifecycle['valuation']['netCash'])-F(initial_lifecycle['valuation']['remainingValue'])==F('-1/300')
    blotter=c.call('trading/blotter',dict(context={**settlement_context,'convention':'trading_workspace_v1'},view=dict(mode='trades',bookIds=[],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)))
    settled=next(h for h in blotter['holdings'] if h['tradeId']==thirds_saved['tradeId'])
    assert settled['presentValue']==lifecycle['valuation']['remainingValue']
    print('PASS settlement-aware HTTP and blotter: paid January excluded once, later months unchanged, currency rounding conserved')
    check_settlement_keeps_facts(projected,settled,'a scheduled swap')
    check_stated_sides(c,settlement_context,thirds_saved['tradeId'],[{'slot':0,'kind':'fixed','coefficient':'-1'},{'slot':1,'kind':'floating','coefficient':'1'}])
    # A receive-fixed swap's read states its own directions, which a backend answering every read with one pair would not.
    receive=deepcopy(thirds);receive.update(operationId=str(uuid4()),firstCoefficient='1')
    received=c.call('swaps/book',receive)
    receive_settlement={**settlement,'tradeId':received['tradeId']}
    receive_preview=c.call('swaps/settlement/preview',receive_settlement)
    c.call('swaps/settlement/determine',{**determine,'preview':receive_settlement,'operationId':str(uuid4()),'expectedVersion':0,'previewHash':receive_preview['previewHash']})
    check_stated_sides(c,settlement_context,received['tradeId'],[{'slot':0,'kind':'fixed','coefficient':'1'},{'slot':1,'kind':'floating','coefficient':'-1'}])
    print('PASS a scheduled swap whose settlement starts keeps its Blotter facts, and its read states its sides\' kinds and directions, pay or receive fixed')
    bridge=c.call('swaps/settlement/pnl',dict(opening={**settlement_context,'knowledgeMode':'retained','contextId':initial_lifecycle['context']['contextId']},
        closing=settlement_context,tradeIds=[thirds_saved['tradeId']],bookIds=[],contractChanges='reject_contract_changes_v1',
        duePolicy=determine['duePolicy'],recognitionPolicy=determine['recognitionPolicy']))['report']['trades'][0]['bridge']
    assert F(bridge['economicPnl'])==F('-1/300') and F(bridge['roundingEffectMovement'])==F('-1/300')
    check_unvalued_lifecycle(c,w,thirds_saved['tradeId'],run_worker)
    from check_swap_netting import run as run_netting
    net_fixture=run_netting(c,w,context,legacy,value['currency'],url)
    from check_swap_settlement import run_lifecycle,run_foundation,run_late_cash,run_cross_market_bridge,run_newtrade_basis_refusal
    run_cross_market_bridge(c,legacy,context)
    run_newtrade_basis_refusal(c,body,context)
    run_lifecycle(c,w,context,legacy_saved['tradeId'],value['currency'])
    run_foundation(c,query,url,body,context,settlement_context,f['at'],value['currency'])
    february_context={**context,'snapshotId':next(frame['id'] for frame in w['frames'] if frame['at'].startswith('2021-02-01T08:00:00'))}
    run_late_cash(c,legacy,settlement_context,february_context,value['currency'],partial['at'])
    from check_swap_settlement import run_future_boundary
    run_future_boundary(c,body,context,february_context)
    gross=dict(context=settlement_context,tradeId=offset['tradeId'],expectedRevision=1,membershipOrdinal=0,paymentOrdinal=0,
        currency=value['currency'],decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    negative=c.call('swaps/settlement/preview',gross)
    assert negative['calculation']['amount']=='-525'
    def accept_gross(request,calculation,version):
        return c.call('swaps/settlement/determine',dict(preview=request,operationId=str(uuid4()),expectedVersion=version,previewHash=calculation['previewHash'],
            duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1',correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None))
    accept_gross(gross,negative,0)
    gross={**gross,'membershipOrdinal':1};positive=c.call('swaps/settlement/preview',gross)
    assert positive['calculation']['amount']=='525' and positive['calculation']['sidePayment']==negative['calculation']['sidePayment']
    assert positive['calculation']['occurrence']!=negative['calculation']['occurrence']
    accept_gross(gross,positive,1)
    gross_balances=c.call('swaps/settlement/inspect',dict(context=settlement_context,tradeId=offset['tradeId'],revision=None,
        duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1'))
    assert len(gross_balances['balances'])==2 and gross_balances['valuation']['determinedOutstanding']=='0'
    print('PASS floating/floating gross settlement retains two independent obligations even when both memberships share the same generated SidePaymentKey')
    print('PASS swap cash: partial receipt, exact retry, over-allocation refusal, reversal and full receipt')
    print('PASS swap settlement HTTP: exact gross final cash, rational quantity, currency rounding, certified context and stale-head refusal')
    print('PASS scheduled swap HTTP: period totals, daily rates, total allocation, cash/PV, replay and exact revision amendment')
    from check_scheduled_observations import run as check_scheduled_observations
    check_scheduled_observations(c,context,manifest)
    from check_contract_observation_booking import run as check_contract_observations
    check_contract_observations(c,context,body,query,run_worker=run_worker)
    return dict(frame=partial,tradeId=mixed_saved['tradeId'],currency=value['currency'],netTradeId=net_fixture['tradeId'])
