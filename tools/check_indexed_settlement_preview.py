"""Independent interval-weighted indexed cash checks in the owned HTTP fixture."""
from copy import deepcopy
from fractions import Fraction as F
import json
import time
from uuid import uuid4
from exact_values import fraction
from power_collection_fixture import Collector


def run_checks(client, query, manifest, runtime, encoded, payload, frame, grid, cutoff, revise, publish, cadence):
    catalog=manifest['catalogId'];config=manifest['ppa']['paymentIndexes'][str(cadence)]
    count=0
    def check(name,condition=True):
        nonlocal count
        assert condition,name
        count+=1;print('PASS indexed settlement HTTP: '+name,flush=True)
    def save(body):
        return json.loads(runtime(f"SELECT security.save_ppa_booking('{catalog}','{uuid4()}',{encoded(body)});"))
    def mapping(binding,revision):
        envelope=json.loads(runtime(f"SELECT security.read_ppa_terms('{catalog}','{binding['instrumentId']}');"))
        side=next(s for s in envelope['sideIds'] if s['role']=='indexed_payment')
        return dict(bindingId=binding['bindingId'],originRevision=revision,instrumentId=binding['instrumentId'],sideId=side['sideId'],role='indexed_payment',ordinal=0)
    indexed=deepcopy(payload);payment=indexed['terms']['memberships'][1];payment['role']='indexed_payment'
    payment['side'].update(kind='indexed',fixedPrice=None,indexPrice=dict(curve=config['curve'],quantity='test_power_quantity',multiplier='1',indexMultiplier='2',settlement=None,adder='3'))
    booked=save(indexed);origin=mapping(booked['bindings'][0],1)
    from check_settlement_preview import POLICIES
    policies={**POLICIES,'contract':'ppa_monthly_gross_indexed_v1'}
    body=dict(snapshotId=frame,tradeId=booked['tradeId'],expectedRevision=1,original=origin,mappings=[origin],policies=policies)
    planned=client.call('settlement/payment-lineages/plan',body)
    check('explicit indexed lineage retains its calculation contract',planned['lineage']['contract']==policies['contract'])
    lineage=planned['lineage']['id']
    def preview(expected=200,known=None,revision=1):
        return client.call('settlement/ppa/preview',dict(snapshotId=frame,lineageId=lineage,expectedRevision=revision,month='2020-11-01',knownAt=known or cutoff()),expected=expected)
    check('missing native index refuses the entire occurrence',preview(expected=422)['code']=='interval_actual_missing')
    collector=Collector(query,manifest,{'payment':config})
    from zoneinfo import ZoneInfo
    by_day={}
    for i,(start,end) in enumerate(grid):by_day.setdefault(start.astimezone(ZoneInfo('America/Chicago' if cadence==15 else 'America/New_York')).date(),[]).append(i)
    prices=[str(i%7-3) for i in range(len(grid))]
    def price_day(day,indices,values,revision):
        return collector.publish('payment',str(day),grid[indices[0]][0],len(indices),cadence,values,revision if cadence==60 else None)
    receipts={}
    for day,indices in by_day.items():receipts[day]=price_day(day,indices,[prices[i] for i in indices],1)
    # Vary two meter cells only; other finality heads remain reusable by prior checks.
    meter_values=['0','25'] if cadence==60 else ['0','6.25']
    rev=revise(meter_values);publish(rev,[0,1])
    quantities=[F(0),F(20 if cadence==60 else 5)]+[F(40 if cadence==60 else 10)]*(len(grid)-2)
    expected=sum(q*(2*F(p)+3)*-1 for q,p in zip(quantities,prices))
    started=time.monotonic();base=preview();elapsed=time.monotonic()-started;old=cutoff()
    check('native quantities times transformed indexes match independent exact sum',fraction(base['exactCash'])==expected and len(base['components'])==len(grid))
    sources={row['curve']['code']:row for row in base['proposalPins']['priceSources']}
    check('deduplicated receipt reconstructs every exact cash component',all(
        c['index']['curve'] in sources and sources[c['index']['curve']]['unit'] and
        fraction(c['paymentPrice'])==2*fraction(c['index']['price'])+3 and
        fraction(c['cash'])==-fraction(c['quantity'])*fraction(c['paymentPrice'])
        for c in base['components']))
    check('zero meter retains evidence and full denominator',fraction(base['components'][0]['quantity'])==0 and fraction(base['components'][0]['normalization']['capacityBasis'])==100)
    check('explicit zero index still applies adder',fraction(base['components'][3]['index']['price'])==0 and fraction(base['components'][3]['paymentPrice'])==3)
    check('payment source is independent of physical location valuation',base['proposalPins']['priceSources'][0]['sourceId']==config['sourceId'] and config['sourceId']!=manifest['intervalCurves']['source'])
    check('collected prices are never labelled contractually final',all(c['index']['state']=='Actual' and c['index']['publication']=='collected' and c['index']['contractuallyFinal'] is False for c in base['components']))
    check('native evidence retains exact batch revision and original payment date',all(c['index']['actual']['revision']==(1 if cadence==60 else None) and c['index']['actual']['batchId'] in {r['batchId'] for r in receipts.values()} for c in base['components']) and base['due']['at'].startswith('2020-12-10T00:00:00'))
    from pathlib import Path
    import sys
    sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'specs/settlement'))
    from reference_model import round_cash, ROUND
    check('one occurrence rounding retains exact difference',fraction(base['roundedCash'])==round_cash(expected,ROUND) and fraction(base['roundingDifference'])==round_cash(expected,ROUND)-expected)
    check('repeated hour retains distinct UTC keys',len({(c['key']['start'],c['key']['end']) for c in base['components']})==len(grid))
    print(f'MEASURE indexed settlement: {len(grid)} cells HTTP {elapsed:.6f} seconds, {len(json.dumps(base).encode())} decoded JSON bytes',flush=True)
    first_day=next(iter(by_day));indices=by_day[first_day];corrected=[prices[i] for i in indices];corrected[1]='11'
    correction=price_day(first_day,indices,corrected,2)
    changed=preview()
    check('later-known single-interval price correction changes only its weighted cash',fraction(changed['exactCash'])==expected-quantities[1]*2*(11-F(prices[1])) and changed['components'][1]['index']['actual']['batchId']==correction['batchId'])
    check('certified old cutoff replays original prices and meter heads',fraction(preview(known=old)['exactCash'])==expected)
    amendment=deepcopy(indexed);amendment.update(action='amended',tradeId=booked['tradeId'],expectedRevision=1,boundaryAt=grid[2][0].isoformat(),coverageFrom=grid[2][0].isoformat(),quantity='60')
    amendment['terms']['memberships'][1]['side']['indexPrice'].update(indexMultiplier='0.5',adder='-1')
    amended=save(amendment);tail=next(b for b in amended['bindings'] if b['bindingId']!=origin['bindingId'])
    check('unmapped indexed amendment refuses partial cash',preview(expected=422,revision=2)['code']=='settlement_manifest')
    client.call('settlement/payment-lineages/plan',{**body,'expectedRevision':2,'mappings':[origin,mapping(tail,2)]})
    actual_prices=list(prices);actual_prices[1]='11'
    amended_expected=sum(-q*(2*F(p)+3) if i<2 else -q*F(3,4)*(F(p)/2-1) for i,(q,p) in enumerate(zip(quantities,actual_prices)))
    current=preview(revision=2)
    check('amended coverage preserves original MW, multiplier, adder and due date',fraction(current['exactCash'])==amended_expected and current['due']==base['due'] and current['occurrence']==base['occurrence'])
    check('prior trade head remains reproducible at original cutoff',fraction(preview(known=old)['exactCash'])==expected)
    termination={**amendment,'action':'terminated','expectedRevision':2,'boundaryAt':grid[4][0].isoformat(),'coverageFrom':None,'coverageTo':None,'quantity':None,'terms':None}
    save(termination);ended=preview(revision=3)
    end_expected=sum(-q*(2*F(p)+3) if i<2 else -q*F(3,4)*(F(p)/2-1) for i,(q,p) in enumerate(zip(quantities[:4],actual_prices[:4])))
    check('termination retains exact native prefix without missing-price fallback',fraction(ended['exactCash'])==end_expected and len(ended['components'])==4 and ended['occurrence']==base['occurrence'])
    # Return the shared meter fixture to its exact original quantities for fixed checks.
    rev=revise(['50' if cadence==60 else '12.5']*2);publish(rev,[0,1])
    print(f'{count} indexed settlement HTTP checks passed ({cadence}-minute)',flush=True)
