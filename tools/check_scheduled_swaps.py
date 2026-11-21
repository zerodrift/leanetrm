"""Synthetic end-to-end scheduled swap contracts; independent cash oracles."""
from synthetic_side_calendars import all_dates_policy
from copy import deepcopy
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient
from lifecycle_swap_client import LifecycleSwapClient,SYNTHETIC_SWAP_REVIEW
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
    c.select_inputs(c.observed_inputs[valued_context['contextId']])
    c.select_inputs(c.observed_inputs[late_context['contextId']])
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


def check_running_amendment(c,w,context,body,composed,currency,query):
    """A swap whose January is settled is amended from February forward at mid-January.

    January keeps its terms, its determination and its run settings; February is repriced and March is added. Cash is an
    independent decimal oracle over the stated quantities, agreed prices and one separately priced March mark.
    """
    partial=next(f for f in w['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    at={**context,'snapshotId':partial['id']}
    running=deepcopy(body);running['operationId']=str(uuid4())
    trade=c.call('swaps/book',running)['tradeId']
    policies=dict(duePolicy='swap_due_nominal_v1',recognitionPolicy='swap_completed_period_trading_pnl_v1')
    january=dict(context=at,tradeId=trade,expectedRevision=1,membershipOrdinal=1,paymentOrdinal=0,currency=currency,decimalPlaces=2,
        roundingPolicy='currency_minor_units_to_even_v1')
    settled=c.call('swaps/settlement/determine',dict(preview=january,operationId=str(uuid4()),expectedVersion=0,
        previewHash=c.call('swaps/settlement/preview',january)['previewHash'],correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None,**policies))
    fixed_january={**january,'membershipOrdinal':0}
    fixed_profile=c.configure(fixed_january,'swap.determine')
    # February's floating side stays as booked and its fixed side is repriced below; only the first keeps its run settings.
    floating_february=c.configure({**january,'paymentOrdinal':1},'swap.determine')
    fixed_february=c.configure({**fixed_january,'paymentOrdinal':1},'swap.determine')
    inspect=dict(context=at,tradeId=trade,revision=None,**policies)
    opening=c.call('swaps/settlement/inspect',inspect)
    def remaining(read):return {(p['membershipOrdinal'],p['ordinal']):Fraction(p['remainingValue']) for p in read['valuation']['payments']}
    detail=c.call('swaps/editor',dict(context=at,tradeId=trade,revision=None))
    assert detail['canAmend'] and detail['lockedPeriods']==[dict(deliveryFrom='2021-01-01',deliveryTo='2021-02-01',reason='settled')],detail.get('lockedPeriods')
    # The amendment restates the terms the read returns, as the ticket does, not the request that booked them.
    amend=deepcopy(running);amend.update(snapshotId=partial['id'],operationId=str(uuid4()),tradeId=trade,expectedRevision=1,effectiveAt=partial['at'],
        reason='Synthetic extension of a running swap',scheduled=deepcopy(detail['state']['terms']['scheduled']))
    def refused(change,code,*words):
        request=deepcopy(amend);change(request['scheduled'])
        for path in ('swaps/preview','swaps/amend'):
            refusal=c.call(path,request,expected=422)
            assert refusal['code']==code and all(word in refusal['message'] for word in words),refusal
    refused(lambda s:s['first']['periods'][0]['fixed'].update(price='4'),'swap_amendment_locked','January 2021','settled')
    refused(lambda s:s['second']['periods'][0].update(amount='151'),'swap_amendment_locked','January 2021')
    def shorten(s):
        for side in (s['first'],s['second']):side['periods']=side['periods'][:1]
    refused(shorten,'swap_amendment_term')
    for change in (dict(firstCoefficient='1'),dict(discounting=True)):
        refusal=c.call('swaps/preview',{**deepcopy(amend),**change},expected=422)
        assert refusal['code']=='swap_amendment_locked' and 'direction and the discounting' in refusal['message'],refusal
    def reindexed(s):s['second']['periods'][1]['floating']['settlement']=dict(mode='override',index='synthetic_other_index',projectionPolicy='forward_curve_no_basis_v1')
    refused(reindexed,'swap_amendment_locked','price settlement')
    # An As of before the swap's latest settlement activity: January settled in mid-January, the amendment dated in December.
    behind=deepcopy(running);behind['operationId']=str(uuid4())
    behind_trade=c.call('swaps/book',behind)['tradeId']
    behind_january={**january,'tradeId':behind_trade}
    c.call('swaps/settlement/determine',dict(preview=behind_january,operationId=str(uuid4()),expectedVersion=0,
        previewHash=c.call('swaps/settlement/preview',behind_january)['previewHash'],correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None,**policies))
    booked_at=next(f['at'] for f in w['frames'] if f['id']==context['snapshotId'])
    backdated=deepcopy(running);backdated.update(operationId=str(uuid4()),tradeId=behind_trade,expectedRevision=1,effectiveAt=booked_at,reason='Synthetic back-dated repricing')
    backdated['scheduled']['first']['periods'][1]['fixed']['price']='3.75'
    for path in ('swaps/preview','swaps/amend'):
        refusal=c.call(path,backdated,expected=422)
        assert refusal['code']=='swap_amendment_backdated' and '2021-01-15 08:00 UTC' in refusal['message'] and 'Amend behind settlement' in refusal['message'],refusal
    earlier_read=c.call('swaps/editor',dict(context=context,tradeId=behind_trade,revision=None))
    assert not earlier_read['canAmend'] and '2021-01-15 08:00 UTC' in earlier_read['amendUnavailableReason'] and 'lifecycleReason' not in earlier_read,earlier_read['amendUnavailableReason']
    # A composed swap's amendment is not previewed by its save, so only the owned write's guard can refuse it.
    single=deepcopy(composed);single['operationId']=str(uuid4())
    single_trade=c.call('swaps/book',single)['tradeId']
    single_january={**january,'tradeId':single_trade}
    c.call('swaps/settlement/determine',dict(preview=single_january,operationId=str(uuid4()),expectedVersion=0,
        previewHash=c.call('swaps/settlement/preview',single_january)['previewHash'],correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None,**policies))
    late=deepcopy(composed);late.update(operationId=str(uuid4()),tradeId=single_trade,expectedRevision=1,effectiveAt=booked_at,reason='Synthetic back-dated composed repricing')
    late['fixed']['price']='3.75'
    assert c.call('swaps/amend',late,expected=422)['code']=='swap_amendment_backdated'
    assert '2021-01-15 08:00 UTC' in c.call('swaps/preview',late,expected=422)['message']
    if query:
        assert query("SELECT string_agg(p.permission,',' ORDER BY p.permission) FROM security.role_permissions p JOIN security.roles r ON r.id=p.role_id WHERE r.code='trade_supervisor';").strip()=='AmendBehindSettlement,AmendTrade,ReadMarketData,ReadTradeParties,ReadTrades'
        held="FROM security.role_permissions WHERE permission='AmendBehindSettlement' AND role_id=(SELECT id FROM security.roles WHERE code='trader')"
        query("INSERT INTO security.role_permissions SELECT id,'AmendBehindSettlement' FROM security.roles WHERE code='trader';")
        try:
            # The permission admits the earlier As of; it does not unlock what is already settled.
            settled_change=deepcopy(backdated);settled_change['operationId']=str(uuid4());settled_change['scheduled']['second']['periods'][0]['amount']='151'
            assert c.call('swaps/amend',settled_change,expected=422)['code']=='settlement_amendment_unsupported'
            assert c.call('swaps/amend',backdated)['revision']==2
        finally:query(f"DELETE {held};")
        assert query(f"SELECT count(*) {held};").strip()=='0'
        again=deepcopy(backdated);again.update(operationId=str(uuid4()),expectedRevision=2);again['scheduled']['first']['periods'][1]['fixed']['price']='3.8'
        assert c.call('swaps/amend',again,expected=422)['code']=='swap_amendment_backdated'
    print('PASS an amendment dated before the latest settlement activity is refused for scheduled and composed swaps, and admitted only with the Amend behind settlement permission, which leaves a settled month locked')
    # A swap that has not started cannot gain a month that has: February and March, then January in mid-January.
    def shifted(body,months):
        for kind,side in (('fixed',body['scheduled']['first']),('floating',body['scheduled']['second'])):
            side['periods']=[deepcopy(side['periods'][min(n,len(side['periods'])-1)]) for n in range(len(months))]
            for period,(start,end) in zip(side['periods'],months):period[kind].update(deliveryFrom=start,deliveryTo=end)
        return body
    spring=[('2021-02-01','2021-03-01'),('2021-03-01','2021-04-01')]
    later=shifted(deepcopy(running),spring);later['operationId']=str(uuid4())
    later_trade=c.call('swaps/book',later)['tradeId']
    unstarted=c.call('swaps/editor',dict(context=at,tradeId=later_trade,revision=None))
    assert unstarted['canAmend'] and unstarted['lockedPeriods']==[]
    earlier=shifted(deepcopy(amend),[('2021-01-01','2021-02-01')]+spring);earlier.update(operationId=str(uuid4()),tradeId=later_trade)
    refusal=c.call('swaps/amend',earlier,expected=422)
    assert refusal['code']=='swap_amendment_locked' and 'January 2021 has already started' in refusal['message'],refusal
    print('PASS a running swap keeps its direction, discounting and index; a swap that has not started cannot gain a month that has')
    def allocated(body):
        for side in (body['scheduled']['first'],body['scheduled']['second']):
            side.update(basis='contract_total',amount='590',allocation='calendar_days')
            for period in side['periods']:period['amount']=None
        return body
    whole=allocated(deepcopy(running));whole['operationId']=str(uuid4())
    whole_trade=c.call('swaps/book',whole)['tradeId']
    assert c.call('swaps/editor',dict(context=at,tradeId=whole_trade,revision=None))['lockedPeriods']==[dict(deliveryFrom='2021-01-01',deliveryTo='2021-02-01',reason='in_progress')]
    longer=allocated(deepcopy(amend));longer.update(operationId=str(uuid4()),tradeId=whole_trade)
    for kind,side in (('fixed',longer['scheduled']['first']),('floating',longer['scheduled']['second'])):
        added=deepcopy(side['periods'][1]);added[kind].update(deliveryFrom='2021-03-01',deliveryTo='2021-04-01');side['periods'].append(added)
    refusal=c.call('swaps/preview',longer,expected=422)
    assert refusal['code']=='swap_amendment_locked' and 'contract total' in refusal['message'] and 'January 2021 is in progress' in refusal['message'],refusal
    repriced=allocated(deepcopy(amend));repriced.update(operationId=str(uuid4()),tradeId=whole_trade);repriced['scheduled']['first']['periods'][1]['fixed']['price']='3.25'
    assert c.call('swaps/amend',repriced)['revision']==2
    print('PASS a contract total cannot be re-spread over a longer term once a month has started; a later month can still be repriced')
    first,second=amend['scheduled']['first'],amend['scheduled']['second']
    march=('2021-03-01','2021-04-01')
    def moved(period,kind):
        value=deepcopy(period);value[kind].update(deliveryFrom=march[0],deliveryTo=march[1]);return value
    first['periods'][1]['fixed']['price']='3.5'
    first['periods'].append({**moved(first['periods'][1],'fixed'),'amount':'50'});first['periods'][2]['fixed']['price']='3'
    second['periods'].append({**moved(second['periods'][1],'floating'),'amount':'60'})
    single=deepcopy(running);single.update(snapshotId=partial['id'],operationId=str(uuid4()))
    single['scheduled']['first']['periods']=[first['periods'][2]];single['scheduled']['second']['periods']=[second['periods'][2]]
    march_mark=Fraction(c.call('swaps/preview',single)['valuation']['sides'][1]['payments'][0]['tradeCashAmount'])/60
    # The trade pays fixed and receives floating; discounting is off, so each month's value is its signed cash.
    expected={**remaining(opening),(0,1):Fraction(-700),(0,2):Fraction(-150),(1,2):60*march_mark}
    preview=c.call('swaps/preview',amend)
    assert preview['valuation'] is None and remaining(dict(valuation=preview['settlementValuation']))==expected
    assert preview['changeReview']['action']=='amend_trade' and preview['expectedRevision']==1
    ticket=c.call('swaps/scheduled-price',dict(terms=amend,priceMode='agreed',parRatePolicy=None))
    assert ticket['valuation'] is None and remaining(dict(valuation=ticket['settlementValuation']))==expected and ticket['changeReview']==preview['changeReview']
    amended=c.call('swaps/amend',amend)
    assert amended['revision']==2 and c.call('swaps/amend',amend)==amended
    closing=c.call('swaps/settlement/inspect',inspect)
    assert remaining(closing)==expected and closing['settlement']['version']==opening['settlement']['version']
    assert [d['id'] for d in closing['settlement']['determinations']]==[settled['determinationId']]
    held=next(h for h in c.call('trading/blotter',dict(context={**at,'convention':'trading_workspace_v1'},
        view=dict(mode='trades',bookIds=[],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)))['holdings'] if h['tradeId']==trade)
    assert held['presentValue']==closing['valuation']['remainingValue'] and Fraction(held['presentValue'])==sum(expected.values()),held
    again=c.call('swaps/settlement/preview',{**january,'expectedRevision':2})
    assert again['previousTotal']==settled['total'] and not again['inputsChanged']
    print('PASS a running swap is amended from February forward: settled January keeps its terms and determination, February is repriced and March is added')
    profiles=[p for p in c.view(at)['swapProfiles'] if p['trade_id']==trade]
    carried={(p['id'],int(p['origin_revision'])) for p in profiles}
    assert {(fixed_profile,2),(floating_february,2)}<=carried and (fixed_february,2) not in carried,profiles
    fixed_january['expectedRevision']=2
    paid=c.call('swaps/settlement/determine',dict(preview=fixed_january,operationId=str(uuid4()),expectedVersion=closing['settlement']['version'],
        previewHash=c.call('swaps/settlement/preview',fixed_january)['previewHash'],correctionPolicy='swap_explicit_correction_due_v1',correctionDueAt=None,**policies))
    assert paid['total']=='-300'
    print('PASS the run settings of unchanged months carry to the amended revision, a repriced month keeps none, and January settles under the carried ones')
    bridge=c.call('swaps/settlement/pnl',dict(opening={**at,'knowledgeMode':'retained','contextId':opening['context']['contextId']},closing=at,
        tradeIds=[trade],bookIds=[],contractChanges='closing_context_contract_changes_v1',**policies))['report']['trades'][0]['bridge']
    impact=Fraction(-100)+Fraction(-150)+60*march_mark
    assert Fraction(bridge['contractualAmendment'])==impact and Fraction(bridge['continuingEconomics'])==0 and Fraction(bridge['economicPnl'])==impact,bridge
    print('PASS the amendment of a running swap reports as one continuing trade with its own impact, independent of market movement')
    again=c.call('swaps/editor',dict(context=at,tradeId=trade,revision=None))
    assert again['canAmend'] and again['state']['revision']==2 and again['lockedPeriods']==detail['lockedPeriods']
    second=remaining(c.call('swaps/settlement/inspect',inspect))
    resized=deepcopy(amend);resized.update(operationId=str(uuid4()),expectedRevision=2,reason='Synthetic second amendment',scheduled=deepcopy(again['state']['terms']['scheduled']))
    resized['scheduled']['second']['periods'][2]['amount']='70'
    assert c.call('swaps/amend',resized)['revision']==3
    assert remaining(c.call('swaps/settlement/inspect',inspect))=={**second,(1,2):70*march_mark}
    print('PASS a second amendment of the running swap restates a later month from the read terms and keeps every other month')
    if query:
        assert query(f"SELECT count(DISTINCT settlement_data.swap_owned_period_identity(p.catalog_id,p.id)) FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i USING(catalog_id,instrument_id) JOIN trading.swap_side_periods p ON p.catalog_id=i.catalog_id AND p.side_id IN(i.scheduled_first_id,i.scheduled_second_id) WHERE r.trade_id='{trade}' AND p.delivery_from='2021-01-01';").strip()=='2'
        print('PASS both revisions hold January under one period identity per side')
        def started(delivery,fixing,at):
            return query(f"SELECT trading.swap_period_started('{delivery}',{fixing},'{at}');").strip()=='t'
        assert not started('2021-02-01','NULL','2021-01-31T23:59:59Z') and started('2021-02-01','NULL','2021-02-01T00:00:00Z')
        assert started('2021-02-01',"'2021-01-27T00:00:00Z'",'2021-01-27T00:00:00Z') and not started('2021-02-01',"'2021-01-27T00:00:00Z'",'2021-01-26T23:59:59Z')
        first,second=(query(f"SELECT instrument_id||','||quantity FROM trading.swap_trade_revisions WHERE trade_id='{trade}' AND revision={n};").strip().split(',') for n in (1,2))
        def proof(booked,kept,month):
            return f"SELECT settlement_data.require_retained_swap_periods(catalog_id,'{booked[0]}',{booked[1]},'{kept[0]}',{kept[1]},ARRAY[DATE '{month}']) FROM trading.swap_trade_revisions WHERE trade_id='{trade}' AND revision=1;"
        def breaks(sql,code):
            try:query(sql)
            except RuntimeError as error:assert code in str(error),str(error)
            else:raise AssertionError('Expected refusal: '+code)
        query(proof(first,second,'2021-01-01'))
        breaks(proof(first,second,'2021-02-01'),'swap_amendment_locked')
        breaks(proof(second,first,'2021-01-01'),'swap_amendment_term')
        breaks(proof(first,(first[0],str(Decimal(first[1])*2)),'2021-01-01'),'swap_amendment_locked')
        print('PASS the native proof admits an unchanged started month and refuses a repriced month, a changed quantity and a shorter term')


def run_checks(url,query=None,manifest=None,*,run_worker):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    c=LifecycleSwapClient(c,**SYNTHETIC_SWAP_REVIEW)
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
        return dict(calendarPolicy=all_dates_policy(observation=not fixed_side), basis='period_total',quantity=index['quantityChoices'][0]['specification'],amount=None,allocation=None,priceApplication='period_average_times_quantity_v1',power=None,
            periods=[dict(amount=amount,fixed=fixed(*period) if fixed_side else None,floating=None if fixed_side else floating(*period)) for period,amount in zip(periods,amounts)])
    body={**context,'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'executedAt':None,'quantity':None,'fixedCoefficient':None,'discounting':False,'fixed':None,'floating':None,
          'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'swapType':'scheduled_financial_swap','firstCoefficient':'-1',
          'scheduled':dict(implementation='whole_term_scheduled_financial_swap_v1',first=side(True,['100','200']),second=side(False,['150','100'])),**trade_booking()}
    if query:
        from check_swap_settlement import check_pnl_scope
        check_pnl_scope(c,query,body,context,f['at'])
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
    # Exact retained instruments support quantity scaling; a second position must
    # not rebuild its graph or repeat its unit valuation within this read.
    report_request=dict(context={**context,'convention':'trading_workspace_v1'},
        view=dict(mode='trades',bookIds=[body['bookId']],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None),bookingCatalog='directory')
    before_shared=c.call('trading/workspace',report_request)
    doubled=deepcopy(body);doubled['operationId']=str(uuid4())
    for side in (doubled['scheduled']['first'],doubled['scheduled']['second']):
        for period in side['periods']:period['amount']=str(Decimal(period['amount'])*2)
    doubled_saved=c.call('swaps/book',doubled)
    shared=c.call('trading/workspace',report_request)
    original_row=next(r for r in shared['blotter']['holdings'] if r['tradeId']==saved['tradeId'])
    doubled_row=next(r for r in shared['blotter']['holdings'] if r['tradeId']==doubled_saved['tradeId'])
    assert original_row['instrumentId']==doubled_row['instrumentId']
    assert Fraction(original_row['presentValue'])==Fraction(expected) and Fraction(doubled_row['presentValue'])==2*Fraction(expected)
    assert shared['blotter']['work']['swapTrades']==before_shared['blotter']['work']['swapTrades']+1
    assert shared['blotter']['work']['swapValuations']==before_shared['blotter']['work']['swapValuations'], 'Each exact scheduled instrument is valued once per read, regardless of held quantity'
    retained_request={**report_request,'context':{**report_request['context'],'knowledgeMode':'retained','contextId':before_shared['catalog']['context']['contextId']}}
    retained_shared=c.call('trading/workspace',retained_request)
    assert all(retained_shared['blotter'][key]==before_shared['blotter'][key] for key in ('holdings','work','context','asOf')), 'Reuse must not carry positions or values across knowledge contexts'
    print('PASS scheduled positions share one exact unit valuation while retaining independent quantities and historical scope')
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
    changed_shared=c.call('trading/workspace',report_request)['blotter']
    assert changed_shared['work']['swapValuations']==shared['blotter']['work']['swapValuations']+1
    changed_row=next(r for r in changed_shared['holdings'] if r['tradeId']==saved['tradeId'])
    unchanged_row=next(r for r in changed_shared['holdings'] if r['tradeId']==doubled_saved['tradeId'])
    assert changed_row['instrumentId']!=unchanged_row['instrumentId'] and Fraction(changed_row['presentValue'])==Fraction(expected-30) and Fraction(unchanged_row['presentValue'])==2*Fraction(expected)
    print('PASS an amended scheduled instrument gets its own valuation without changing another position in the original instrument')
    assert c.call('swaps/amend',amend)==amended
    if query:
        # Commercial terms decide the admission. Under a credit hold a memo correction still carries the admitted terms while
        # a quantity change is refused, and an explicitly chosen other agreement makes even a memo correction commercial.
        admissions=lambda:{row['revision']:row for row in c.call('desk/directory',dict(snapshotId=f['id']))['trades'] if row['trade_id']==saved['tradeId']}
        parties="(catalog_id,legal_entity_id,counterparty_id)=('{}','{}','{}')".format(CAT,trade_booking()['legalEntityId'],trade_booking()['counterpartyId'])
        def decide(revision,status):
            query(f"INSERT INTO desk.clearances SELECT catalog_id,gen_random_uuid(),{revision},legal_entity_id,legal_entity_revision,counterparty_id,counterparty_revision,product,"
                  f"clock_timestamp(),NULL,'{status}','Synthetic credit review',actor_id,publication_order,sha256(convert_to('{status}{revision}','UTF8')) "
                  f"FROM desk.clearances WHERE {parties} AND product='financial_swap' AND revision=1;")
        decide(2,'hold')
        larger=deepcopy(amend);larger.update(operationId=str(uuid4()),expectedRevision=2);larger['scheduled']['first']['periods'][1]['amount']='220'
        assert c.call('swaps/amend',larger,expected=422)['code']=='desk_credit_hold'
        noted=deepcopy(amend);noted.update(operationId=str(uuid4()),expectedRevision=2,memo='Synthetic desk note',reason='Synthetic memo correction')
        assert c.call('swaps/amend',noted)['revision']==3
        decide(3,'approved')
        admitted=admissions()
        assert [admitted[n]['carried_from'] for n in (1,2,3)]==[None,None,2],admitted
        assert all(admitted[3][term]==admitted[2][term] for term in ('agreement_id','clearance_id','executed_at')),admitted
        other=query(f"INSERT INTO desk.agreements SELECT catalog_id,gen_random_uuid(),'second_swap_master',1,legal_entity_id,legal_entity_revision,counterparty_id,counterparty_revision,"
                    f"'Second synthetic master',agreement_type,ARRAY['financial_swap'],clock_timestamp(),NULL,'executed',procedure,provisions,filename,digest,actor_id,publication_order,"
                    f"sha256(convert_to('second_swap_master','UTF8')) FROM desk.agreements WHERE {parties} AND 'financial_swap'=ANY(products) AND revision=1 RETURNING id;").strip()
        chosen=deepcopy(noted);chosen.update(operationId=str(uuid4()),expectedRevision=3,memo='Synthetic desk note under the second master')
        assert c.call('desk/choose',dict(operationId=chosen['operationId'],agreementId=other))=={'agreementId':other}
        assert c.call('swaps/amend',chosen)['revision']==4
        query(f"INSERT INTO desk.agreements SELECT catalog_id,gen_random_uuid(),code,2,legal_entity_id,legal_entity_revision,counterparty_id,counterparty_revision,name,agreement_type,products,"
              f"clock_timestamp(),NULL,'terminated',procedure,provisions,filename,digest,actor_id,publication_order,sha256(convert_to('second_swap_master ended','UTF8')) "
              f"FROM desk.agreements WHERE catalog_id='{CAT}' AND id='{other}';")
        admitted=admissions()
        assert (admitted[4]['carried_from'],admitted[4]['agreement_id'])==(None,other) and admitted[3]['agreement_id']!=other,admitted
        print('PASS a quantity amendment awaits its own confirmation and respects a credit hold; a memo correction carries the admitted terms unless another agreement is chosen')
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
    ordinary={k:v for k,v in body.items() if k!='scheduled'};ordinary.update(operationId=str(uuid4()),quantity='100',fixedCoefficient='-1',firstCoefficient=None,swapType='financial_fixed_floating',fixed=deepcopy(body['scheduled']['first']['periods'][0]['fixed']),floating=deepcopy(body['scheduled']['second']['periods'][0]['floating']))
    ordinary['fixed']['calendarPolicy']=deepcopy(body['scheduled']['first']['calendarPolicy'])
    ordinary['floating']['calendarPolicy']=deepcopy(body['scheduled']['second']['calendarPolicy'])
    ordinary_saved=c.call('swaps/book',ordinary)
    assert c.call('swaps/editor',dict(context=context,tradeId=ordinary_saved['tradeId'],revision=None))['valuation']['presentValue']=='10'
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
    assert clocked_preview['calculation']['completion']['source']=='retained_contract_clock_v1'
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
    acknowledged=c.call('swaps/settlement/determine',{**determine,'operationId':str(uuid4()),'expectedVersion':1,'previewHash':current['previewHash']})
    assert acknowledged['determinationId']==accepted['determinationId'] and acknowledged['version']==1 and acknowledged['currentVersion']==1
    c.call('swaps/settlement/determine',{**determine,'previewHash':'f'*64},expected=409)
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
    c.call('swaps/settlement/payment',{**payment,'operationId':str(uuid4()),'expectedVersion':2,'amount':'20','reference':'Synthetic refused over-allocation'},expected=422)
    reversal={**payment,'operationId':str(uuid4()),'expectedVersion':2,'amount':'-40','reversesId':paid['paymentId'],'reference':'Synthetic reversal'}
    reversed_payment=c.call('swaps/settlement/payment',reversal)
    assert reversed_payment['version']==3
    acknowledged=c.call('swaps/settlement/payment',{**reversal,'operationId':str(uuid4()),'expectedVersion':3})
    assert acknowledged['paymentId']==reversed_payment['paymentId'] and acknowledged['version']==3 and acknowledged['currentVersion']==3
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
    net_fixture=run_netting(c,w,context,ordinary,value['currency'],url)
    from check_swap_settlement import run_lifecycle,run_foundation,run_late_cash,run_cross_market_bridge,run_newtrade_basis_refusal
    run_cross_market_bridge(c,ordinary,context)
    run_newtrade_basis_refusal(c,body,context)
    run_lifecycle(c,w,context,ordinary_saved['tradeId'],value['currency'])
    run_foundation(c,query,url,body,context,settlement_context,f['at'],value['currency'])
    february_context={**context,'snapshotId':next(frame['id'] for frame in w['frames'] if frame['at'].startswith('2021-02-01T08:00:00'))}
    run_late_cash(c,ordinary,settlement_context,february_context,value['currency'],partial['at'])
    from check_swap_settlement import run_future_boundary
    run_future_boundary(c,body,context,february_context)
    gross=dict(context=settlement_context,tradeId=offset['tradeId'],expectedRevision=1,membershipOrdinal=0,paymentOrdinal=0,
        currency=value['currency'],decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    shared_periods=[p for p in c.view(settlement_context)['swapOccurrences'] if p['trade_id']==offset['tradeId'] and p['payment_ordinal']==0]
    assert len(shared_periods)==2 and {(p['membership_ordinal'],p['payment_ordinal']) for p in shared_periods}=={(0,0),(1,0)}
    assert len({p['occurrence_id'] for p in shared_periods})==1
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
    shared_offers=[a for a in c.view(settlement_context)['actions'] if a['target']['scopeId']==offset['tradeId'] and a['action']=='swap.determine']
    assert len(shared_offers)==2 and len({a['profileId'] for a in shared_offers})==2 and len({a['occurrenceId'] for a in shared_offers})==1
    assert all(a['profileId']==a['selection']['profileId'] for a in shared_offers)
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
    check_contract_observations(c,context,body,query,run_worker=run_worker,manifest=manifest)
    agent_preview=dict(context={**context,'snapshotId':partial['id']},tradeId=mixed_saved['tradeId'],expectedRevision=1,membershipOrdinal=0,paymentOrdinal=0,
        currency=value['currency'],decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    c.configure(agent_preview,'swap.determine');c.configure(agent_preview,'swap.payment')
    check_running_amendment(c,w,context,body,ordinary,value['currency'],query)
    return dict(frame=partial,tradeId=mixed_saved['tradeId'],currency=value['currency'],netTradeId=net_fixture['tradeId'])
