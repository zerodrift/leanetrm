"""Exact independent rate oracles, including large cancelling denominators."""
from datetime import datetime,timedelta,timezone
from fractions import Fraction
from math import isqrt
from uuid import uuid4

from gas_transport_capacity_fixture import literal


def cancelling_rates(f):
    primes=[];candidate=100003
    while len(primes)<220:
        if all(candidate%d for d in range(2,isqrt(candidate)+1)):primes.append(candidate)
        candidate+=2
    operation='71111111-1111-1111-1111-000000000099'
    definition=f.definition('synthetic_exact_rate_oracle')
    start=datetime(2032,1,1,tzinfo=timezone.utc)
    instant=lambda value:value.isoformat(timespec='microseconds').replace('+00:00','Z')
    claims=[]
    for prime in primes:
        duration=prime**3
        for quantity in [1,duration-1]:
            claims.append(dict(pathId=definition['paths'][0]['pathId'],receiptFrom=instant(start),receiptTo=instant(start+timedelta(microseconds=duration)),
                receiptQuantity=str(quantity),deliveryFrom=instant(start),deliveryTo=instant(start+timedelta(microseconds=duration)),deliveryQuantity=str(quantity)))
    command=dict(kind='plan',entitlementId=definition['entitlementId'],expectedStateVersion=0,expectedPreviousId=None,businessKey='synthetic_exact_sum',
        versionId='71111111-1111-1111-1111-000000000001',admissionPolicy='complete_feasible_replacement_v1',evidence='Synthetic paired exact-rate oracle',claims=claims)
    normalized=f.normalize([dict(operationId=operation,command=command)])[0]['command']
    total=Fraction(0);largest=0
    for claim in sorted(normalized['claims'],key=lambda row:row['claimId']):
        duration=(datetime.fromisoformat(claim['receiptTo'])-datetime.fromisoformat(claim['receiptFrom']))//timedelta(microseconds=1)
        total+=Fraction(int(claim['receiptQuantity'])*3600000000,duration)
        largest=max(largest,total.numerator.bit_length(),total.denominator.bit_length())
    assert total==792000000000 and largest>4096
    # Invoke the production accumulator itself in the managed canonical ID order.
    # Each pair sums to3,600,000,000/hour, independent of its prime denominator.
    f.query("DO $oracle$DECLARE claim jsonb;rate numeric[];total numeric[]=ARRAY[0::numeric,1::numeric];BEGIN "
        f"FOR claim IN SELECT x FROM jsonb_array_elements({literal(normalized['claims'])})x ORDER BY(x->>'claimId')::uuid LOOP "
        "rate=trading.gas_transport_capacity_rate((claim->>'receiptQuantity')::numeric,(claim->>'receiptFrom')::timestamptz,(claim->>'receiptTo')::timestamptz);"
        "total=trading.gas_transport_capacity_add(total,rate);END LOOP;"
        "IF total<>ARRAY[792000000000::numeric,1::numeric] THEN RAISE EXCEPTION 'Synthetic exact-rate mismatch';END IF;END;$oracle$;")
    print('PASS native exact accumulator retains6275-bit intermediate fractions and returns the independent40-bit integer792000000000/1 without order-dependent refusal.',flush=True)

    # A real producer/receipt/read control uses the same non-decimal rates with
    # two pairs. Three exact event intervals are independently known integers.
    definition=f.definition('synthetic_exact_rate_records')
    end=datetime(2090,1,1,tzinfo=timezone.utc)
    for limits in [definition['agreement'],*[p['limits'] for p in definition['paths']],*[p['limits'] for p in definition['receipts']],*[p['limits'] for p in definition['deliveries']]]:
        limits[0].update(**{'from':instant(start)},through=instant(end),maximumPerHour='1000000000000000')
    definition.update(**{'from':instant(start)},through=instant(end))
    version=str(uuid4());f.record(definition,operation=version)
    facts=f.read(definition['entitlementId'])
    path=next(p['id'] for p in facts['paths'] if p['path_key']=='synthetic_path_a')
    command.update(entitlementId=definition['entitlementId'],versionId=version,expectedStateVersion=facts['stateVersion'],businessKey='synthetic_two_exact_pairs',
        claims=[{**c,'pathId':path} for c in claims[:4]])
    operation=str(uuid4());command=f.normalize([dict(operationId=operation,command=command)])[0]['command']
    points=[start,*[start+timedelta(microseconds=p**3) for p in primes[:2]],end]
    rows=[]
    for axis,point,path_id in [('agreement',None,None),('receipt','synthetic_gas_point_a',None),('delivery','synthetic_gas_point_b',None),('route',None,path)]:
        for i,rate in enumerate([7200000000,3600000000,0]):
            active=sorted(c['claimId'] for c in command['claims'] if datetime.fromisoformat(c['receiptTo'])>=points[i+1])
            rows.append(dict(axis=axis,point=point,pathId=path_id,**{'from':instant(points[i])},through=instant(points[i+1]),claims=active,
                used=dict(n=str(rate),d='1'),maximum=dict(n='1000000000000000',d='1'),withinLimit=True))
    other_path=next(p['id'] for p in facts['paths'] if p['path_key']=='synthetic_path_d')
    for axis,point,path_id in [('receipt','synthetic_gas_point_d',None),('route',None,other_path)]:
        rows.append(dict(axis=axis,point=point,pathId=path_id,**{'from':instant(start)},through=instant(end),claims=[],
            used=dict(n='0',d='1'),maximum=dict(n='1000000000000000',d='1'),withinLimit=True))
    witness=dict(implementation='gas_transport_capacity_witness_v1',versionId=version,admissionPolicy='complete_feasible_replacement_v1',intervals=rows)
    accepted=f.record(command,witness,operation=operation)
    saved=f.read(definition['entitlementId'])
    assert len(saved['claims'])==4 and saved['plans'][0]['assessment']==witness
    assert f.record(command,witness,operation=operation)==accepted
    print('PASS public native plan retains four exact paired-fraction claims,14 complete witness intervals and7200000000→3600000000→0 hourly rates; receipt replay is unchanged.',flush=True)
