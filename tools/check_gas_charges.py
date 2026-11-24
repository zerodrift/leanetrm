"""Synthetic gas charges booked with explicit fixed payment terms and calendar pins."""
from copy import deepcopy
from fractions import Fraction
import json
from uuid import uuid4

from check_booked_side_programs import runtime_query
from check_gas_pricing import request, staged_booking
from delivery_quantity_fixture import add_fixture as add_location
from preview_fixture import CURRENCY, M, R, RCAT, RREV, identity
from preview_client import PreviewClient
from synthetic_side_calendars import side_calendar_policy


def add_quote_fixture(reference,market,security,manifest):
    # Publish a distinct quote, settlement index and mapped point with the same
    # explicit pipeline clock and authority as the ordinary location fixture.
    prefix='synthetic_charge'
    quote=prefix+'_quote'
    index=prefix+'_index'
    target=prefix+'_target'
    number=94501
    reference,market,security,manifest=add_location(reference,market,security,manifest,price='3.10',
        prefix=prefix,number=number,curve_name='Synthetic charge location',quote=quote,target=target)
    refs=f"""INSERT INTO ref_data.quote_specifications
        SELECT catalog_id,revision_id,'{quote}',commodity_code,kind,quantity_code,currency_code,2,trading_tick,clearing_increment,minimum,maximum
        FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=({R},'test_gas_outright');
        INSERT INTO ref_data.price_index_definitions
        SELECT catalog_id,revision_id,'{index}',commodity_code,location_code,currency_code,quote_unit_code,2,calendar_code
        FROM ref_data.price_index_definitions WHERE(catalog_id,revision_id,code)=({R},'test_price');
        INSERT INTO ref_data.settlement_targets VALUES({R},'{target}','index','{index}',NULL);"""
    fixing=f"""INSERT INTO market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,
        reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation)
        VALUES({M},'{index}','2021-01-01','2021-02-01',{R},'{quote}','{identity('9',number)}','fixture',
        '2020-12-31T12:00:00Z','published_index_observations_v1');"""
    market=market.replace('UPDATE ref_data.revisions SET',refs+'\nUPDATE ref_data.revisions SET',1)
    market=market.replace('UPDATE market_data.revisions SET',fixing+'\nUPDATE market_data.revisions SET',1)
    return reference,market,security,manifest


def run_checks(url, query, manifest):
    c = PreviewClient(url)
    c.bootstrap()
    c.call('profile', dict(profile='trader'))
    context, body = request(c)
    calendar = body['cashSides'][0]['calendarPolicy']['payment']['calendar']
    calendar_policy = side_calendar_policy(calendar, delivery=True, observation=False, payment=True)
    body.update(profile='fixed_gas_composition_v1', cashSides=[])
    body['physicalSides'][0].pop('quantityPolicy')
    plain = c.call('gas/composition/book', body)
    assert query(f"SELECT count(*) FROM trading.gas_component_terms WHERE instrument_id='{plain['instrumentId']}' AND role='charge';") == '0'
    lump = dict(code='premium', basis='lump_sum', direction='pay', price='5000', clockSide='gas',
                dailyQuantity=None, paymentAt='2020-12-18T00:00:00Z', paymentDay=None, **{'from': None, 'to': None}, calendarPolicy=calendar_policy)
    capacity = dict(code='capacity', basis='capacity', direction='pay', price='0.02', clockSide='gas',
                    dailyQuantity='10000', paymentAt=None, paymentDay=2, **{'from': '2021-01-20', 'to': '2021-03-11'}, calendarPolicy=calendar_policy)
    body.update(operationId=str(uuid4()), chargeSides=[lump,capacity])
    draft = c.call('gas/composition/preview', body)
    booked = c.call('gas/composition/book', body)
    assert c.call('gas/composition/book', body) == booked
    loaded = c.call('gas/composition/editor', dict(context=context, tradeId=booked['tradeId']))
    for value in [draft,loaded['valuation']]:
        assert [Fraction(s['presentValue']) for s in value['sides'][1:]] == [-5000,-10000], value['sides']
        assert Fraction(value['measures']['realizedUnsettled']) == -5000
    terms = json.loads(query(f"""SELECT jsonb_agg(jsonb_build_array(code,charge_basis,charge_quantity_n,charge_quantity_d,clock_slot,
        source_slot,stage,delivery_from,delivery_to,payment_day,payment_at AT TIME ZONE 'UTC') ORDER BY slot)
        FROM trading.gas_component_terms WHERE instrument_id='{booked['instrumentId']}' AND role='charge';"""))
    assert terms == [['premium','lump_sum',1,1,0,None,None,'2020-12-17','2020-12-18',None,'2020-12-18T00:00:00'],
                     ['capacity','capacity',10000,1,0,None,None,'2021-01-20','2021-03-11',2,None]], terms
    payments = json.loads(query(f"""SELECT jsonb_agg(jsonb_build_array(r.delivery_from,r.delivery_to,r.value_at AT TIME ZONE 'UTC') ORDER BY r.payment_ordinal)
        FROM trading.instrument_side_schedules s JOIN trading.side_schedule_rows r USING(catalog_id,schedule_id)
        WHERE s.instrument_id='{booked['instrumentId']}' AND s.slot=2 AND r.operation='output_guard';"""))
    assert payments == [['2021-01-20','2021-02-01','2021-02-02T00:00:00'],['2021-02-01','2021-03-01','2021-03-02T00:00:00'],
                        ['2021-03-01','2021-03-11','2021-04-02T00:00:00']], payments
    assert loaded['terms']['chargeSides'] == body['chargeSides']
    assert query(f"SELECT payment_convention FROM trading.gas_component_terms WHERE instrument_id='{booked['instrumentId']}' AND slot=2;") == 'next_month_day_utc_v1'
    ratios=query(f"SELECT string_agg(ratio_n::text||'/'||ratio_d::text,',' ORDER BY slot) FROM trading.gas_component_terms WHERE instrument_id='{booked['instrumentId']}' AND role='charge';")
    assert ratios=='1/10000,1/1',ratios
    # A table writer must also identify an actual physical clock witness.
    try:
        query(f"""BEGIN;
            INSERT INTO trading.gas_memberships SELECT catalog_id,instrument_id,3,side_id FROM trading.gas_memberships
            WHERE instrument_id='{booked['instrumentId']}' AND slot=2;
            INSERT INTO trading.gas_component_terms SELECT (jsonb_populate_record(m,jsonb_build_object('slot',3,'code','bad_clock','clock_slot',1))).*
            FROM trading.gas_component_terms m WHERE instrument_id='{booked['instrumentId']}' AND slot=2;
            ROLLBACK;""")
    except RuntimeError as error:
        assert 'gas_composition_input' in str(error),error
    else:
        raise AssertionError('A charge referenced a payment as its physical clock')
    # The literal code "true" is valid. Its JSON boolean namesake is not a string.
    named=deepcopy(body)
    named.update(operationId=str(uuid4()),chargeSides=[{**lump,'clockSide':'true'}])
    named['physicalSides'][0]['code']='true'
    valid=c.call('gas/composition/book',named)
    assert c.call('gas/composition/editor',dict(context=context,tradeId=valid['tradeId']))['terms']['chargeSides'][0]['clockSide']=='true'
    boolean=deepcopy(named)
    boolean['operationId']=str(uuid4())
    boolean['chargeSides'][0]['clockSide']=True
    staged_booking(query,manifest,named,boolean,'gas_composition_input')
    non_unit_quote_checks(c,query,manifest,context,body)
    payment_checks(c,query,manifest,body,booked,context,draft['currency'])
    rejection_checks(c,query,manifest,body)
    lifecycle_admission(c,query,manifest,context,body,calendar_policy)
    exercise_premium(c,url,context,body,calendar_policy)
    print('PASS gas charges: independent terms, own clock, monthly payments, booked values, replay and native refusals',flush=True)


def non_unit_quote_checks(c,query,manifest,context,body):
    point=next(p for p in c.call('gas/catalog',context)['locations'] if p['deliveryPoint']=='synthetic_charge_point')
    selected=deepcopy(body)
    selected['operationId']=str(uuid4())
    selected['physicalSides'][0].update({key:point[key] for key in ('pipeline','zone','deliveryPoint','product')})
    booked=c.call('gas/composition/book',selected)
    loaded=c.call('gas/composition/editor',dict(context=context,tradeId=booked['tradeId']))
    assert loaded['terms']['chargeSides']==selected['chargeSides']
    assert [Fraction(s['presentValue']) for s in loaded['valuation']['sides'][1:]]==[-5000,-10000]
    terms=json.loads(query(f"""SELECT jsonb_agg(jsonb_build_array(m.code,m.price::text,s.economic_identity::jsonb->'DateQuoteKey',
        s.economic_identity::jsonb#>'{{Price,Price}}',r.parameters->>'numerator',r.parameters->>'denominator',r.quote_multiplier::text)
        ORDER BY m.slot) FROM trading.gas_component_terms m JOIN trading.gas_memberships gm USING(catalog_id,instrument_id,slot)
        JOIN trading.sides s ON(s.catalog_id,s.id)=(gm.catalog_id,gm.side_id)
        JOIN trading.instrument_side_schedules h ON(h.catalog_id,h.instrument_id,h.slot)=(m.catalog_id,m.instrument_id,m.slot)
        JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.operation)=(h.catalog_id,h.schedule_id,'constant_price')
        WHERE m.instrument_id='{booked['instrumentId']}' AND m.role='charge';"""))
    def ref(code):
        return ['reference',['reference_revision',RCAT,RREV],code]
    quote=ref('synthetic_charge_quote')
    unit=['price_unit',ref(CURRENCY),ref('test_gas_quantity'),'2']
    expected=[('premium','5000','2500'),('capacity','0.02','0.01')]
    assert len(terms)==len(expected),terms
    for (code,nominal,literal),(saved,price,quote_key,typed,n,d,multiplier) in zip(expected,terms):
        assert (saved,Fraction(price),quote_key,typed)==(code,Fraction(nominal),quote,['price',literal,unit]),terms
        assert Fraction(int(n),int(d))==Fraction(literal) and Fraction(multiplier)==2,terms
    # The staged literal 2500 agrees with truncation or nearest rounding of the
    # half-ulp difference. Only exact representation admits a charge.
    for invalid_nominal in ['5000.000000000000000001','5002']:
        invalid=deepcopy(selected)
        invalid['operationId']=str(uuid4())
        invalid['chargeSides'][0]['price']=invalid_nominal
        staged_booking(query,manifest,selected,invalid,'gas_composition_input')
    print('PASS multiplier-two gas quote: retained price, units, schedule literals, stated cash and exact native refusals',flush=True)


def payment_checks(c,query,manifest,body,booked,context,currency):
    lump=body['chargeSides'][0]
    # The first owned capacity day lies after physical delivery. Its boundary, to the
    # microsecond, controls payment admission independently of the payment date.
    def record(at, slot, occurrence, amount, key, held=booked):
        payment = dict(operationId=str(uuid4()), tradeId=held['tradeId'], expectedRevision=1, slot=slot,
                       businessKey=key, paidAt=at, currency=currency, amount=amount,
                       allocations=[dict(occurrence=occurrence,amount=amount)])
        wire=json.dumps(payment)
        statement=f"SELECT security.record_gas_cash_payment('{manifest['catalogId']}','{wire}'::jsonb,'2021-02-03T00:00:00Z');"
        return json.loads(runtime_query(query,manifest,statement).splitlines()[-1])
    try:
        record('2021-01-20T23:59:59.999999Z',2,0,'-10','capacity_partial')
    except RuntimeError as error:
        assert 'gas_payment_occurrence' in str(error),error
    else:
        raise AssertionError('Unended capacity day was paid')
    first=record('2021-01-21T00:00:00Z',2,0,'-10','capacity_partial')
    assert record('2021-01-21T00:00:00Z',2,0,'-10','capacity_partial')['paymentId']==first['paymentId']
    assert record('2020-12-18T00:00:00Z',1,0,'-5000','lump_sum_payment')['revision']==1
    signed=c.call('gas/composition/book',{**body,'operationId':str(uuid4()),'chargeSides':[{**lump,'price':'-50'},{**lump,'code':'zero','price':'0'}]})
    before=c.call('gas/composition/editor',dict(context=context,tradeId=signed['tradeId']))['valuation']
    assert [Fraction(s['presentValue']) for s in before['sides'][1:]]==[50,0]
    paid=record('2020-12-18T00:00:00Z',1,0,'50','negative_nominal',signed)
    assert paid['revision']==1
    # Direct inserts bypass the command but still encounter the deferred conservation check.
    forged=str(uuid4())
    try:
        query(f"""BEGIN;
            INSERT INTO trading.gas_cash_payments SELECT (jsonb_populate_record(p,jsonb_build_object('id','{forged}','operation_id','{uuid4()}',
                'business_key','wrong_direction','amount',-50))).* FROM trading.gas_cash_payments p WHERE id='{paid['paymentId']}';
            INSERT INTO trading.gas_cash_payment_allocations SELECT (jsonb_populate_record(a,jsonb_build_object('payment_id','{forged}','amount',-50))).*
                FROM trading.gas_cash_payment_allocations a WHERE payment_id='{paid['paymentId']}';
            SET CONSTRAINTS ALL IMMEDIATE; ROLLBACK;""")
    except RuntimeError as error:
        assert 'gas_payment_allocation' in str(error),error
    else:
        raise AssertionError('Deferred allocation check accepted payment in the opposite direction')
    signed_value=c.call('gas/composition/editor',dict(context=context,tradeId=signed['tradeId']))['valuation']
    assert [Fraction(s['presentValue']) for s in signed_value['sides'][1:]]==[0,0]
    for slot,amount in [(1,'-50'),(2,'1'),(2,'0')]:
        try:
            record('2020-12-18T00:00:00Z',slot,0,amount,'invalid_nominal',signed)
        except RuntimeError as error:
            assert 'gas_payment_amount' in str(error),error
        else:
            raise AssertionError('Payment contradicted the signed nominal amount')


def rejection_checks(c,query,manifest,body):
    lump=body['chargeSides'][0]
    bad = []
    for changes in [dict(clockSide='premium'),dict(paymentDay=29),dict(dailyQuantity='0'),dict(paymentAt=lump['paymentAt']),{'from':'2021-02-30'}]:
        invalid=deepcopy(body)
        invalid['chargeSides'][1].update(changes)
        bad.append(invalid)
    mixed=deepcopy(body)
    mixed['chargeSides'][0]['dailyQuantity']='1'
    bad.append(mixed)
    ninth=deepcopy(body)
    ninth['chargeSides']=[{**lump,'code':f'premium_{i}'} for i in range(8)]
    bad.append(ninth)
    covered=deepcopy(body)
    covered['exerciseRights']=[dict(entries=['premium'],period='delivery_day',holder='us',settlement='cash_automatic',leadHours=None)]
    bad.append(covered)
    for invalid in bad:
        invalid['operationId']=str(uuid4())
        assert c.call('gas/composition/book',invalid,expected=422)['code']=='gas_composition_input'
        staged_booking(query,manifest,body,invalid,'gas_composition_input')
    for changes in [dict(source='gas'),dict(stage='delivery'),dict(price='4999'),dict(calendarPolicy=None)]:
        invalid=deepcopy(body)
        invalid['operationId']=str(uuid4())
        invalid['chargeSides'][0].update(changes)
        staged_booking(query,manifest,body,invalid,'gas_composition_input')


def lifecycle_admission(c,query,manifest,context,body,calendar_policy):
    lump,capacity=body['chargeSides']
    # An ordinary payment is matched to the same physical term, quantity and
    # selected source. The sealed two-month fixed program belongs only to a charge.
    multiple=deepcopy(body)
    path=next(p for p in c.call('gas/catalog',context)['locations'] if p['deliveryPoint']=='synthetic_gas_point_a')
    multiple['physicalSides'][0].update({k:path[k] for k in ('pipeline','zone','deliveryPoint','product')},
                                       deliveryFrom='2021-01-30',deliveryTo='2021-02-03')
    multiple.update(operationId=str(uuid4()),chargeSides=[{**capacity,'from':'2021-01-30','to':'2021-02-03'}])
    c.call('gas/composition/book',multiple)
    following=deepcopy(multiple)
    following.pop('chargeSides')
    following.update(operationId=str(uuid4()),cashSides=[dict(code='capacity',role='consideration',direction='pay',source='gas',
        stage='delivery',share='1',price='0.02',tariffCode=None,paymentAt='2021-03-02T00:00:00Z',calendarPolicy=calendar_policy)])
    c.call('gas/composition/preview',following)
    staged_booking(query,manifest,multiple,following,'gas_schedule_completion')

    # A nominal payment that is already overdue can be booked against future
    # physical delivery. An ordinary trade whose physical delivery started still refuses.
    overdue=deepcopy(body)
    overdue.update(operationId=str(uuid4()),chargeSides=[{**lump,'paymentAt':'2020-12-17T00:00:00Z'}])
    clocked=deepcopy(overdue)
    other=next(p for p in c.call('gas/catalog',context)['locations'] if p['deliveryPoint']=='synthetic_gas_point_c')
    clocked['physicalSides'][0].update({k:other[k] for k in ('pipeline','zone','deliveryPoint','product')})
    clocked['operationId']=str(uuid4())
    one_am=c.call('gas/composition/book',clocked)
    assert query(f"SELECT delivery_from FROM trading.gas_component_terms WHERE instrument_id='{one_am['instrumentId']}' AND role='charge';")=='2020-12-15'
    late=c.call('gas/composition/book',overdue)
    late_value=c.call('gas/composition/editor',dict(context=context,tradeId=late['tradeId']))['valuation']
    assert Fraction(late_value['measures']['realizedUnsettled'])==-5000
    after=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:'))
    following.update(snapshotId=after['id'],operationId=str(uuid4()))
    following['physicalSides'][0].update(deliveryFrom='2021-01-13',deliveryTo='2021-01-14')
    following['cashSides'][0].update(paymentAt='2021-01-14T00:00:00Z')
    assert c.call('gas/composition/book',following,expected=422)['code']=='gas_delivery_lifecycle'


def exercise_premium(c,url,context,body,calendar_policy):
    lump=body['chargeSides'][0]
    exercise=deepcopy(body)
    exercise['chargeSides']=[lump]
    exercise.update(operationId=str(uuid4()),exerciseRights=[dict(entries=['gas','strike'],period='delivery_day',holder='us',settlement='physical',leadHours=6)])
    exercise['cashSides']=[dict(code='strike',role='consideration',direction='pay',source='gas',stage='delivery',share='1',
        price='3',tariffCode=None,paymentAt='2021-02-10T00:00:00Z',calendarPolicy=calendar_policy)]
    exercise_with=c.call('gas/composition/book',exercise)
    exercise_without=c.call('gas/composition/book',{**exercise,'operationId':str(uuid4()),'chargeSides':[]})
    writer=PreviewClient(url)
    writer.bootstrap()
    writer.call('profile',dict(profile='editor'))
    writer.call('volatility-marks/record',dict(context=context,definitionRevisionId=body['definitionRevisionId'],
        operationId=str(uuid4()),reason='Synthetic capacity premium check',curve='synthetic_quantity_location',
        marks=[dict(month='2021-01',expectedRevision=0,volatility='0.5')]))
    def valuation(held):
        return c.call('gas/composition/editor',dict(context=context,tradeId=held['tradeId']))['valuation']
    with_premium=valuation(exercise_with)
    without_premium=valuation(exercise_without)
    assert with_premium['groups']==without_premium['groups'] and with_premium['groups']
    assert Fraction(with_premium['presentValue'])-Fraction(without_premium['presentValue'])==-5000
