"""Independent enabled-discount receipt control for the existing-close worker."""
from copy import deepcopy
from datetime import date
from fractions import Fraction
from uuid import uuid4
from exact_values import fraction, instant, rounded
from check_ppa_bookings_http import profile_terms


def prepare_discount(reference,market,security,manifest):
    """One exact native hour supports the historical discount control."""
    from datetime import datetime,timezone
    from preview_fixture import CAT,REV,R,M,identity
    manifest=deepcopy(manifest)
    code='test_existing_close_discount_hour'
    contract,series,observation=(identity('d',991000+n) for n in range(3))
    source=manifest['ppaBookings']['workloads']['60']['source']
    definitions=[
        f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_power','test_hub_a','test_power_outright','test_ppa_long_profile_60','test_power_target','direct','fixture','trader_mark','Synthetic discount hour');",
        f"INSERT INTO market_data.contracts VALUES({M},'{contract}',{R},'forward','2021-05-01','2021-05-02','2021-05-01T05:00:00Z',NULL);",
        f"INSERT INTO market_data.contract_intervals VALUES({M},'{contract}','2021-05-01T04:00:00Z','2021-05-01T05:00:00Z','2021-05-01',0,0,1);",
        f"INSERT INTO market_data.series VALUES({M},'{series}','test_existing_close_discount_hour',{R},'{contract}','test_power_outright',NULL);",
        f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{contract}',0);",
        f"INSERT INTO market_data.curve_physical_price_sources VALUES({M},'{code}',{R},'{source}');",
        f"INSERT INTO market_data.curve_actual_rules VALUES({M},'{code}','interval_end',60,'interval_actual_due_v1');"]
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+f"\nINSERT INTO market_data.observations VALUES('{CAT}','{observation}','{REV}','{series}',{R},'fixture','synthetic_discount_hour','1','trader_mark','final','2020-12-18T16:00:00Z','{datetime.now(timezone.utc).isoformat()}',31.52,NULL);\nCOMMIT;\n"
    frame=deepcopy(next(f for f in manifest['frames'] if instant(f['at']).isoformat()=='2020-12-18T16:00:00+00:00' and not f.get('ppaBundle')))
    frame['curveContracts']={code:[contract]};frame['observationIds'].append(observation)
    manifest['frames'].append(frame);manifest['closeRisk']['discountCurve']=code
    pair=deepcopy(next(f for f in manifest['frames'] if instant(f['at']).isoformat()=='2020-12-18T16:00:00+00:00' and 'base_forward' in f['curveContracts'] and not f.get('ppaBundle')))
    pair['name']='Synthetic existing-close floating pair'
    pair['curveContracts']={'base_forward':pair['curveContracts']['base_forward']}
    manifest['frames'].append(pair)
    return reference,market,security,manifest


def run_checks(owner, fixture, query, manifest, request, due, work):
    frames=owner.call('workspace')['frames']
    frame=next(f for f in frames if instant(f['at']).isoformat()=='2020-12-18T16:00:00+00:00' and set(f['curveContracts'])=={manifest['closeRisk']['discountCurve']} and not f.get('ppaBundle'))
    close=owner.call('market/closes/register',{'requestId':str(uuid4()),'snapshotId':frame['id'],'name':'Synthetic discounted existing close'})
    book=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{book}','d0000000-0000-0000-0000-000000000001','Synthetic PPA discount receipt',1,'2020-01-01T00:00:00Z');")
    terms=profile_terms(manifest)
    terms['delivery'].update(**{'from':'2021-05-01','to':'2021-06-01'})
    terms['discounting']=True
    floating,fixed=terms['memberships']
    profile=deepcopy(floating['side']['quantity'])
    floating['role']='indexed_payment'
    floating['side'].update(kind='indexed',location=None,indexPrice={'curve':manifest['closeRisk']['discountCurve'],'quantity':'test_power_quantity','multiplier':'1','indexMultiplier':'1','adder':'0'})
    fixed['side']['quantity']=deepcopy(profile)
    for member in terms['memberships']:
        member['side']['paymentRule']['dayOfMonth']=18
    old=fixture.frame
    try:
        fixture.frame=next(f for f in fixture.client.call('workspace')['frames'] if instant(f['at'])==instant(frame['at']) and not f.get('ppaBundle'))['id']
        trade,_=fixture.book(terms,'2021-05-01T04:00:00Z','2021-05-01T05:00:00Z',book=book)
    finally:
        fixture.frame=old
    body=deepcopy(request);body['schedule']['requestId']=str(uuid4())
    body['definition'].update(closeId=close['id'],bookIds=[book])
    job=owner.call('jobs/close-book-risk/create',body);due(job);report=work(job)
    # One MW-hour per MW, 80 MW, independent May mark31.52 less strike40.
    # June18 is the exact3% simple ACT/365F pillar, no interpolation.
    days=(date(2021,6,18)-date(2020,12,18)).days
    factor=Fraction(rounded(1/(1+Fraction(3,100)*Fraction(days,365)),18))
    assert fraction(report['totals'][0]['value'])==80*(Fraction('31.52')-40)*factor,report['totals']
    assert {p['tradeId'] for p in report['positions']}=={trade['tradeId']}
    discounts=report['discountInputs'];assert len(discounts)==2,discounts
    for evidence in discounts:
        value=evidence['input']
        assert value['enabled'] and Fraction(value['factor'])==factor and value['mappingId']=='d2000000-0000-0000-0000-000000000001'
        assert value['paymentDate']=='2021-06-18' and value['curve']['code']=='interest_rates'
        assert len(value['observations'])==1 and value['observations'][0]['maturity']=='2021-06-18'
        assert Fraction(value['observations'][0]['annualRate'])==Fraction(3,100)
    assert query(f"SELECT count(*) FROM automation.pilot_report_discounts WHERE report_id='{report['reportId']}' AND ppa_side_id IS NOT NULL;")=='2'
    print('PASS close risk: enabled PPA discount retains exact side, mapping, payment, pillar and numerical policy receipt with independent PV',flush=True)
    check_floating_pair(owner,fixture.client,query,manifest,request,due,work)


def check_floating_pair(owner,trader,query,manifest,request,due,work):
    """Existing-close scope and receipts retain both independent floating sides."""
    import json
    from preview_fixture import trade_booking
    frames=trader.call('workspace')['frames']
    frame=next(f for f in frames if instant(f['at']).isoformat()=='2020-12-18T16:00:00+00:00' and set(f['curveContracts'])=={'base_forward'} and not f.get('ppaBundle'))
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    catalog=trader.call('swaps/catalog',context)
    index=next(i for i in catalog['indices'] if i['curve']=='base_forward' and i['definitionRevisionId']==manifest['revisionId'])
    book=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{book}','d0000000-0000-0000-0000-000000000001','Synthetic floating pair close',1,'2020-01-01T00:00:00Z');")
    payment={'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    floating={'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':payment,'resetFrequency':'monthly','averagingMethod':'none','spread':'0.1'}
    trade=trader.call('swaps/book',{**context,**trade_booking(),'operationId':str(uuid4()),'bookId':book,'definitionRevisionId':index['definitionRevisionId'],
        'commodity':index['commodity'],'quantity':'10','firstCoefficient':'-1','swapType':'financial_floating_floating','firstFloating':floating,'floating':{**floating,'spread':'0.2'},'discounting':False})
    owner_frame=next(f for f in owner.call('workspace')['frames'] if instant(f['at'])==instant(frame['at']) and f['curveContracts']==frame['curveContracts'] and
        set(f['observationIds'])==set(frame['observationIds']) and set(f.get('frozenObservationIds') or [])==set(frame.get('frozenObservationIds') or []) and not f.get('ppaBundle'))
    close=owner.call('market/closes/register',{'requestId':str(uuid4()),'snapshotId':owner_frame['id'],'name':'Synthetic floating pair close'})
    body=deepcopy(request);body['schedule']['requestId']=str(uuid4())
    body['definition'].update(closeId=close['id'],bookIds=[book])
    job=owner.call('jobs/close-book-risk/create',body);due(job);report=work(job)
    assert {p['tradeId'] for p in report['positions']}=={trade['tradeId']}
    assert len(report['totals'])==1 and fraction(report['totals'][0]['value'])==1
    assert report['risk'] and all(fraction(r['delta']['value'])==fraction(r['gamma']['value'])==0 for r in report['risk'])
    sides=json.loads(query(f"SELECT row_to_json(i) FROM trading.swap_instruments i WHERE instrument_id='{trade['instrumentId']}';"))
    assert sides['fixed_side_id'] is None and sides['first_floating_side_id']!=sides['floating_side_id']
    assert {d['payment']['sideId'] for d in report['discountInputs']}=={sides['first_floating_side_id'],sides['floating_side_id']}
    assert all(not d['input']['enabled'] and d['input']['factor']=='1' for d in report['discountInputs'])
    assert query(f"SELECT count(*) FROM automation.pilot_report_discounts WHERE report_id='{report['reportId']}' AND first_floating_side_id='{sides['first_floating_side_id']}' AND ppa_side_id IS NULL;")=='1'
    print('PASS close risk: floating pair admission, exact spread PV, opposing sensitivity and independent membership receipts',flush=True)
