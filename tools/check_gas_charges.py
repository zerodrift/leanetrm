"""Synthetic gas charges booked with explicit fixed payment terms and calendar pins."""
from copy import deepcopy
from datetime import datetime, timezone
from fractions import Fraction
import json
import re
from uuid import uuid4

from check_booked_side_programs import capture_context, runtime_query
from check_gas_in_term import select_captured_inputs
from check_gas_pricing import request, staged_booking
from check_portfolio_risk import VIEW, financial
from delivery_quantity_fixture import add_fixture as add_location
from gas_pricing_fixture import ADMINISTRATOR
from preview_fixture import CURRENCY, M, R, RCAT, RREV, identity
from preview_client import PreviewClient, initial_frame
from synthetic_side_calendars import side_calendar_policy


REFERENCE_DATES = ('2021-04-01T08:00:00Z', '2021-04-11T15:00:00Z', '2021-05-02T15:00:00Z')
REFERENCE_POINT = 'synthetic_charge_reference'


def utc_instant(value):
    # Retain all fractional digits, including ticks finer than a Python microsecond.
    parts = re.fullmatch(r'(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d+)?(Z|[+-]\d{2}:\d{2})', value)
    assert parts, value
    seconds, fraction, offset = parts.groups()
    return datetime.fromisoformat(seconds + offset).astimezone(timezone.utc), Fraction(fraction or '0')


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


def add_reference_fixture(reference, market, security, manifest):
    clock = 'synthetic_charge_east'
    refs = f"""INSERT INTO ref_data.clock_rules VALUES({R},'{clock}','fixed_offset',50400,NULL,NULL);
        INSERT INTO ref_data.calendars VALUES({R},'synthetic_charge_year_one','{clock}',0,'0001-01-01','0001-03-01');"""
    market = market.replace('UPDATE ref_data.revisions SET', refs + '\nUPDATE ref_data.revisions SET', 1)
    reference, market, security, manifest = add_location(reference, market, security, manifest,
        prefix='synthetic_charge_east', number=94800, curve_name='Synthetic eastern gas day', clock=clock)
    template = next(f for f in manifest['frames'] if f['at'] == '2021-01-15T08:00:00Z')
    manifest['frames'].extend({**deepcopy(template), 'at': at} for at in REFERENCE_DATES)
    # The physical purchase runs through May 4, so the May mark still moves
    # its value after the April capacity row has fully accrued.
    return add_location(reference, market, security, manifest, price='3.10', prefix=REFERENCE_POINT,
                        number=94700, curve_name='Synthetic charge reference location', start_minute=900,
                        months=(('2021-04-01', '2021-05-01'), ('2021-05-01', '2021-06-01')))


def reference_routes(c, query, manifest, body, run_worker, admin_load):
    workspace = c.call('workspace')
    frames = [initial_frame(workspace, at[:10]) for at in REFERENCE_DATES]
    assert [(utc_instant(f['at']), f['originCloseId']) for f in frames] == [(utc_instant(at), f['id']) for at, f in zip(REFERENCE_DATES, frames)]
    contexts = [dict(snapshotId=f['id'], convention=body['convention'], knowledgeMode='current') for f in frames]
    point = next(p for p in c.call('gas/catalog', contexts[0])['locations'] if p['deliveryPoint'] == REFERENCE_POINT + '_point')
    physical = {**body['physicalSides'][0], **{k: point[k] for k in ('pipeline', 'zone', 'deliveryPoint', 'product')},
                'deliveryFrom': '2021-04-01', 'deliveryTo': '2021-05-05'}
    lump, capacity = body['chargeSides']
    charges = [
        {**lump, 'code': 'same_day', 'paymentAt': '2021-04-01T00:00:00Z'},
        {**lump, 'code': 'one_week', 'paymentAt': '2021-04-08T00:00:00Z'},
        {**capacity, 'from': '2021-04-01', 'to': '2021-05-01'},
    ]
    cash = dict(code='price', role='consideration', direction='pay', source='gas', stage='delivery',
                share='1', price='3', tariffCode=None, paymentAt='2021-05-10T00:00:00Z', calendarPolicy=lump['calendarPolicy'])
    template = {**body, 'physicalSides': [physical], 'cashSides': [cash]}
    variants = [(1, []), (1, [0]), (1, [1]), (1, [2]), (1, [0, 1, 2]), (2, [0, 1, 2])]
    def book(context, started):
        holdings = []
        for size, entries in variants:
            terms = {**template, **context, 'operationId': str(uuid4()),
                     'physicalSides': [{**physical, 'quantity': str(10000 * size)}],
                     'chargeSides': [charges[i] for i in entries]}
            if started:
                if size == 1 and len(entries) == len(charges):
                    refused = c.call('gas/composition/book', terms, expected=422)
                    assert refused['code'] == 'gas_delivery_lifecycle', refused
                result = admin_load(dict(administrator=ADMINISTRATOR[0], composition=terms))
                assert result.returncode == 0, result.stderr
                held = json.loads(result.stdout)
            else:
                draft = c.call('gas/composition/preview', terms)
                assert_valuation('draft preview', draft, size, entries, baseline[0], reference[0], components=bool(entries))
                held = c.call('gas/composition/book', terms)
            holdings.append((held, terms))
        return holdings

    # Fixed-price physical purchase at 3.10 less ordinary consideration at
    # 3.00. The active April and May gas days are known from the fixture.
    # Physical PV, fixed consideration PV, position owed, position to come.
    baseline = [(1054000, -1020000, 0, 34000),
                (744000, -1020000, -300000, 24000),
                (93000, -1020000, -930000, 3000)]
    # Each pair is (realized unsettled, unrealized) for one charge.
    # Keeping each charge alone with the same purchase exposes its split even
    # on routes that publish the split only at trade level.
    reference = [
        [(-5000, 0), (0, -5000), (0, -6000)],
        [(-5000, 0), (-5000, 0), (-2000, -4000)],
        [(-5000, 0), (-5000, 0), (-6000, 0)],
    ]
    marks = [{'2021-04-01': 30, '2021-05-01': 4}, {'2021-04-01': 20, '2021-05-01': 4}, {'2021-05-01': 3}]
    money = ('realizedUnsettled', 'unrealized')

    def expected(size, entries, purchase, charges):
        physical, cash, owed, remaining = [x * size for x in purchase]
        sides = [physical, cash, *(sum(charges[i]) for i in entries)]
        split = (owed + sum(charges[i][0] for i in entries), remaining + sum(charges[i][1] for i in entries))
        return sides, split

    def assert_valuation(route, value, size, entries, purchase, charges, *, components=True):
        sides, split = expected(size, entries, purchase, charges)
        actual_sides = [Fraction(s['presentValue']) for s in value['sides']]
        assert actual_sides == sides, (route, entries, actual_sides, sides)
        assert Fraction(value['presentValue']) == sum(sides), (route, entries, value['presentValue'])
        if components:
            assert tuple(Fraction(value['measures'][k]) for k in money) == split, (route, entries, value['measures'])

    risk = PreviewClient(c.url)
    risk.bootstrap()
    risk.call('profile', dict(profile='risk'))

    def routes(holdings, context, purchase, amounts, active_months, *, queued=False):
        shared = {**context, 'convention': 'trading_workspace_v1'}
        scope = dict(context=shared, bookIds=[], portfolioIds=[], tradeIds=[held['tradeId'] for held, _ in holdings])
        catalog = risk.call('portfolio/risk/catalog', scope)
        assert not catalog['excludedPositions'], catalog['excludedPositions']
        factors = {f['deliveryFrom']: f for f in catalog['factors'] if f['curves'] == [REFERENCE_POINT + '_location']}
        assert factors.keys() == active_months.keys() and len(catalog['factors']) == len(factors), catalog['factors']
        risk_request = {**scope, 'context': {**shared, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']},
                        'detail': 'holdings', 'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
                        'pricingPolicy': catalog['pricingPolicy'], 'stress': [],
                        'bumps': [dict(factor=f['factor'], size='1', convention=catalog['bumpConvention']) for f in factors.values()]}
        report = risk.call('portfolio/risk', risk_request)
        assert [f['factor']['deliveryFrom'] for f in report['factors']] == list(factors), report['factors']
        assert not report['excludedPositions'], report['excludedPositions']
        if queued:
            job = risk.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=risk_request))
            run_worker()
            finished = risk.call('portfolio/risk/jobs/' + job['id'])
            assert finished['state'] == 'completed' and financial(finished['result']) == financial(report), finished
        blotter = {h['tradeId']: h for h in c.call('trading/blotter', dict(context=shared, view=VIEW))['holdings']}
        for (held, terms), (size, entries) in zip(holdings, variants):
            trade = held['tradeId']
            editor = c.call('gas/composition/editor', dict(context=context, tradeId=trade))['valuation']
            preview = c.call('gas/composition/preview', {**terms, 'snapshotId': context['snapshotId'], 'knowledgeMode': context['knowledgeMode'],
                                                        'previewTradeId': trade, 'previewContextId': context.get('contextId')})
            assert_valuation('editor', editor, size, entries, purchase, amounts)
            assert_valuation('held preview', preview, size, entries, purchase, amounts)
            assert_valuation('Blotter', {**blotter[trade], 'measures': blotter[trade]}, size, entries, purchase, amounts)
            sides, split = expected(size, entries, purchase, amounts)
            for factor in report['factors']:
                month = factor['factor']['deliveryFrom']
                move = size * 10000 * active_months[month]
                position = next(p for p in factor['positions'] if p['tradeId'] == trade)
                assert [e['slot'] for e in position['entries']] == list(range(len(sides))), position
                for lane, change in [('base', 0), ('up', move), ('down', -move)]:
                    assert [Fraction(e[lane]) for e in position['entries']] == [sides[0] + change, *sides[1:]], (month, trade, lane, position)
                    assert Fraction(position['values'][lane]) == sum(sides) + change, position
                    assert tuple(Fraction(position['components'][k][lane]) for k in money) == (split[0], split[1] + change), position

    def inspect(holdings, context, amounts, paid, deliveries, states, *, paid_at=None):
        term_dates = [('2021-03-30', '2021-03-31', '2021-04-01'), ('2021-04-06', '2021-04-07', '2021-04-08'),
                      ('2021-04-01', '2021-05-01', '2021-05-02')]
        for held, _ in holdings[-2:]:
            position = dict(context={**context, 'convention': 'trading_workspace_v1'}, tradeId=held['tradeId'],
                            bindingId=held['instrumentId'], revision=1, instrumentType='physical_gas')
            [physical_row] = c.call('trading/side-schedules', dict(position=position, sideSlot=0, contribution=None, offset=0, limit=1))['page']['rows']
            assert physical_row['lifecycle']['cash'] == 'NotApplicable' and 'remaining' not in physical_row, physical_row
            for i, (owed, remaining) in enumerate(amounts):
                page = c.call('trading/side-schedules', dict(position=position, sideSlot=i + 2, contribution=None, offset=0, limit=8))['page']
                [row] = page['rows']
                first, last, due = term_dates[i]
                assert (row['deliveryFrom'], row['deliveryTo']) == (first, last), row
                assert tuple(utc_instant(row[k]) for k in ('valueAt', 'paymentAt', 'start', 'end')) == tuple(
                    utc_instant(at) for at in (due + 'T00:00:00Z', due + 'T00:00:00Z', first + 'T15:00:00Z', last + 'T15:00:00Z')), row
                assert Fraction(row['quantity']) == (-1 if i < 2 else -300000), row
                assert (row['lifecycle']['delivery'], row['lifecycle']['cash']) == (deliveries[i], states[i]), row
                assert (utc_instant(row['paidAt']) if row['paidAt'] else None) == (utc_instant(paid_at) if paid[i] else None), row
                assert [Fraction(row[k]) for k in ('accrued', 'paid', 'outstanding', 'remaining', 'presentValue')] == [
                    owed + paid[i], paid[i], owed, remaining, owed + remaining], row

    def payment(trade, slot, amount, paid_at, as_of, key):
        wire = json.dumps(dict(operationId=str(uuid4()), tradeId=trade, expectedRevision=1, slot=slot, businessKey=key,
                               paidAt=paid_at, currency=CURRENCY, amount=str(amount), allocations=[dict(occurrence=0, amount=str(amount))]))
        statement = f"SELECT security.record_gas_cash_payment('{manifest['catalogId']}','{wire}'::jsonb,'{as_of}');"
        return json.loads(runtime_query(query, manifest, statement).splitlines()[-1])

    first = book(contexts[0], started=False)
    routes(first, contexts[0], baseline[0], reference[0], marks[0])
    inspect(first, contexts[0], reference[0], [0, 0, 0], ['Ended', 'NotStarted', 'NotStarted'], ['AccruedUnpaid', 'Remaining', 'Remaining'])

    ten = book(contexts[1], started=True)
    routes(ten, contexts[1], baseline[1], reference[1], marks[1])
    inspect(ten, contexts[1], reference[1], [0, 0, 0], ['Ended', 'Ended', 'InProgress'], ['AccruedUnpaid'] * 3)
    cutoff = capture_context(query, manifest, '2021-04-11').splitlines()[-1]
    trade = ten[-2][0]['tradeId']
    try:
        payment(trade, 4, -1000, '2021-04-02T14:59:59.999999Z', REFERENCE_DATES[1], 'reference_early')
    except RuntimeError as error:
        assert 'gas_payment_occurrence' in str(error), error
    else:
        raise AssertionError('Payment applied before the first 15:00 gas-day end')
    for i, slot in [(3, 2), (4, 4), (5, 4)]:
        assert payment(ten[i][0]['tradeId'], slot, -1000, REFERENCE_DATES[1], REFERENCE_DATES[1], 'reference_partial')['revision'] == 1
    partial = [*reference[1][:2], (-1000, -4000)]
    routes(ten, contexts[1], baseline[1], partial, marks[1], queued=True)
    inspect(ten, contexts[1], partial, [0, 0, -1000], ['Ended', 'Ended', 'InProgress'], ['AccruedUnpaid', 'AccruedUnpaid', 'PartPaid'], paid_at=REFERENCE_DATES[1])
    for client in (c, risk):
        select_captured_inputs(client, query, cutoff)
    historical = {**contexts[1], 'knowledgeMode': 'retained', 'contextId': cutoff}
    routes(ten, historical, baseline[1], reference[1], marks[1], queued=True)
    inspect(ten, historical, reference[1], [0, 0, 0], ['Ended', 'Ended', 'InProgress'], ['AccruedUnpaid'] * 3)

    last = book(contexts[2], started=True)
    routes(last, contexts[2], baseline[2], reference[2], marks[2])
    inspect(last, contexts[2], reference[2], [0, 0, 0], ['Ended'] * 3, ['AccruedUnpaid'] * 3)
    billing_refusal(c, query, manifest, last[-2][0], contexts[2])
    paid = [-5000, -5000, -6000]
    for ((held, _), (_, entries)) in zip(last, variants):
        for slot, i in enumerate(entries, start=2):
            assert payment(held['tradeId'], slot, paid[i], REFERENCE_DATES[2], REFERENCE_DATES[2], f'reference_paid_{i}')['revision'] == 1
    settled = [(0, 0)] * 3
    routes(last, contexts[2], baseline[2], settled, marks[2], queued=True)
    inspect(last, contexts[2], settled, paid, ['Ended'] * 3, ['Paid'] * 3, paid_at=REFERENCE_DATES[2])
    print('PASS gas charge reference matrix: three closes, full and partial payment, prior cutoff, twice the size, and unchanged charge PV under risk bumps', flush=True)


def billing_refusal(c, query, manifest, held, context):
    trade = held['tradeId']
    day = '2021-04-01'
    selection = c.call('gas/quantities/read', dict(context=context, tradeId=trade, selectionPolicy='operational_projection_v1'))
    obligation = next(r['obligationId'] for r in selection['rows'] if r['day'] == day)
    c.call('gas/quantities/record', dict(context=context, tradeId=trade, expectedRevision=1, operationId=str(uuid4()),
        obligationId=obligation, kind='allocated', status='final', day=day, quantity='9000', evidence='Synthetic charge billing boundary', expectedPreviousId=None))
    request = dict(context=context, tradeId=trade, expectedRevision=1, cashSlot=1, day=day,
                   selectionPolicy='final_allocated_billing_v1', profile='gas_final_daily_fixed_cash_v1',
                   decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')
    accepted = c.call('gas/billing/preview', request)
    assert Fraction(accepted['amount']) == -27000, accepted
    calculation = json.dumps(dict(sourceId=accepted['sourceId'], amount='-27000', numerator='-27000', denominator='1'))
    for slot in (2, 3, 4):
        preview = {**request, 'cashSlot': slot}
        refused = c.call('gas/billing/preview', preview, expected=422)
        assert refused['code'] == 'gas_billing_profile', refused
        wire = json.dumps(dict(preview=preview, operationId=str(uuid4()), previewHash=accepted['previewHash'],
                               sourceId=accepted['sourceId'], expectedPreviousId=None, correctionDueAt=None,
                               reason=None, evidence='Synthetic charge billing boundary'))
        try:
            runtime_query(query, manifest, f"SELECT security.record_gas_billing('{manifest['catalogId']}','{wire}'::jsonb,"
                          f"'{REFERENCE_DATES[2]}','{accepted['context']['contextId']}','{calculation}'::jsonb);")
        except RuntimeError as error:
            assert 'gas_billing_profile' in str(error), error
        else:
            raise AssertionError('Native final billing accepted a charge')
    print('PASS final billing admits ordinary fixed consideration and structurally refuses charge slots', flush=True)


def run_checks(url, query, manifest, run_worker, admin_load, check_boundary):
    c = PreviewClient(url)
    c.bootstrap()
    c.call('profile', dict(profile='trader'))
    context, body = request(c)
    calendar = body['cashSides'][0]['calendarPolicy']['payment']['calendar']
    calendar_policy = side_calendar_policy(calendar, delivery=True, observation=False, payment=True)
    body.update(profile='fixed_gas_composition_v1', cashSides=[])
    body['physicalSides'][0].pop('quantityPolicy')
    plain_schedule = c.call('gas/composition/preview', body)['schedule']
    assert (plain_schedule['deliveredLeaves'], plain_schedule['leaves']) == (0, 3) and 'progressBasis' not in plain_schedule, plain_schedule
    plain = c.call('gas/composition/book', body)
    assert query(f"SELECT count(*) FROM trading.gas_component_terms WHERE instrument_id='{plain['instrumentId']}' AND role='charge';") == '0'
    lump = dict(code='premium', basis='lump_sum', direction='pay', price='5000', clockSide='gas',
                dailyQuantity=None, paymentAt='2020-12-18T00:00:00Z', paymentDay=None, **{'from': None, 'to': None}, calendarPolicy=calendar_policy)
    capacity = dict(code='capacity', basis='capacity', direction='pay', price='0.02', clockSide='gas',
                    dailyQuantity='10000', paymentAt=None, paymentDay=2, **{'from': '2021-01-20', 'to': '2021-03-11'}, calendarPolicy=calendar_policy)
    stub = {**body, 'physicalSides': [{**body['physicalSides'][0], 'deliveryFrom': '2021-01-31', 'deliveryTo': '2021-02-01'}],
            'chargeSides': [{**capacity, 'from': '2021-01-31', 'to': '2021-02-03'}]}
    stub_schedule = c.call('gas/composition/preview', stub)['schedule']
    assert (stub_schedule['progressBasis'], stub_schedule['deliveredLeaves'], stub_schedule['leaves']) == ('native_schedule_rows_v1', 0, 3), stub_schedule
    cells = {row['key']: row['cells'] for row in stub_schedule['rows']}
    assert [(key, Fraction(cells[key]['capacity.amount']['value'])) for key in ('2021-01', '2021-02')] == [('2021-01', -200), ('2021-02', -400)], cells
    assert all(cells['2021-01-31']['capacity.' + field]['value'] is None for field in ('quantity', 'price', 'amount')), cells
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
    overview = c.call('trading/gas', dict(context={**context, 'convention': 'trading_workspace_v1'},
        tradeId=booked['tradeId'], bindingId=booked['instrumentId'], revision=1, instrumentType='physical_gas'))
    [group] = overview['sideGroups']
    assert group['bindingId'] == group['instrumentId'] == booked['instrumentId'], group
    native = json.loads(query(f"""SELECT jsonb_agg(jsonb_build_array(slot,side_id) ORDER BY slot)
        FROM trading.gas_memberships WHERE instrument_id='{booked['instrumentId']}';"""))
    assert [[s['slot'], s['sideId']] for s in group['sides']] == native, group
    assert [(s['label'], s['roleLabel'], s['directionLabel']) for s in group['sides'][1:]] == [
        ('premium', 'Contractual gas charge', 'Company pays'), ('capacity', 'Contractual gas charge', 'Company pays')], group
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
    point = next(p for p in c.call('gas/catalog',context)['locations'] if p['deliveryPoint']=='synthetic_charge_east_point')
    year_one = {**calendar, 'code':'synthetic_charge_year_one'}
    east = {**body, 'physicalSides':[{**body['physicalSides'][0], **{key:point[key] for key in ('pipeline','zone','deliveryPoint','product')}}],
            'chargeSides':[{**lump, 'calendarPolicy':side_calendar_policy(year_one,delivery=True,observation=False,payment=True)}]}
    amendment_checks(c, query, manifest, context, body)
    check_boundary(east)
    non_unit_quote_checks(c,query,manifest,context,body)
    payment_checks(c,query,manifest,body,booked,context,draft['currency'])
    rejection_checks(c,query,manifest,body)
    lifecycle_admission(c,query,manifest,context,body,calendar_policy)
    exercise_premium(c,url,context,body,calendar_policy)
    print('PASS gas charges: independent terms, own clock, monthly payments, booked values, replay and native refusals',flush=True)
    reference_routes(c, query, manifest, body, run_worker, admin_load)


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


def amendment_checks(c, query, manifest, context, body):
    # At this close the selected 15:00 gas day starts exactly at the amendment instant.
    at = REFERENCE_DATES[1]
    context = {**context, 'snapshotId':initial_frame(c.call('workspace'),at[:10])['id']}
    point = next(p for p in c.call('gas/catalog',context)['locations'] if p['deliveryPoint']==REFERENCE_POINT+'_point')
    physical = {**body['physicalSides'][0], **{key:point[key] for key in ('pipeline','zone','deliveryPoint','product')},
                'deliveryFrom':'2021-04-12', 'deliveryTo':'2021-05-05'}
    policy = 'future_gas_composition_replace_v1'
    lump, capacity = body['chargeSides']
    lump = {**lump, 'paymentAt':'2021-04-11T00:00:00Z'}
    future = {**lump, 'paymentAt':'2021-04-22T00:00:00Z'}
    started = {**capacity, 'from':'2021-04-11', 'to':'2021-04-21'}

    def book(entry, quantity='10000'):
        terms = {**body, **context, 'operationId':str(uuid4()), 'chargeSides':[entry],
                 'physicalSides':[{**physical, 'quantity':quantity}]}
        return c.call('gas/composition/book', terms), terms

    def pay(held, paid_at, recorded_at):
        capture_context(query, manifest, recorded_at[:10])
        wire = json.dumps(dict(operationId=str(uuid4()), tradeId=held['tradeId'], expectedRevision=held['revision'], slot=1,
            businessKey='synthetic_premium', paidAt=paid_at, currency=CURRENCY, amount='-5000',
            allocations=[dict(occurrence=0, amount='-5000')]))
        receipt = runtime_query(query, manifest, f"SELECT security.record_gas_cash_payment('{manifest['catalogId']}','{wire}'::jsonb,'{recorded_at}');")
        assert json.loads(receipt.splitlines()[-1])['revision'] == 1

    def state(trade):
        return query(f"""SELECT jsonb_build_array(
            (SELECT jsonb_agg(to_jsonb(r) ORDER BY revision) FROM trading.gas_trade_revisions r WHERE trade_id='{trade}'),
            (SELECT jsonb_agg(jsonb_build_array(to_jsonb(p),to_jsonb(a)) ORDER BY p.id,a.occurrence)
             FROM trading.gas_cash_payments p JOIN trading.gas_cash_payment_allocations a ON(a.catalog_id,a.payment_id)=(p.catalog_id,p.id)
             WHERE p.trade_id='{trade}'),
            (SELECT count(*) FROM trading.gas_amendment_commands WHERE trade_id='{trade}'));""")

    def ticket(held):
        return c.call('tickets/trade', dict(context=context, tradeId=held['tradeId'], bindingId=held['instrumentId'], revision=held['revision']))

    def change(held, draft):
        return dict(context=context, draft=draft, tradeId=held['tradeId'], bindingId=held['instrumentId'], expectedRevision=held['revision'],
                    definitionRevisionId=body['definitionRevisionId'], reason='Synthetic replacement', policy=policy)

    cases = [
        ('settled upfront premium', lump, lump, (lump['paymentAt'], at), 'gas_amendment_started', False),
        ('original capacity in its first day', started, {**started, 'from':'2021-04-12'}, None, 'gas_amendment_started', False),
        ('proposed capacity in its first day', {**started,'from':'2021-04-12'}, started, None, 'gas_amendment_started', True),
        ('known later-effective payment', future, {**future, 'price':'4999'}, (future['paymentAt'], '2021-04-23T16:00:00Z'), 'gas_amendment_payment', False),
    ]
    for name, original, proposed, payment, refusal, available in cases:
        held, _ = book(original)
        if payment:
            cutoff = capture_context(query, manifest, at[:10]).splitlines()[-1]
            pay(held, *payment)
            if refusal=='gas_amendment_payment':
                catalog = manifest['catalogId']
                earlier = runtime_query(query, manifest, f'''SELECT security.use_market_context('{catalog}','{cutoff}');
                    SELECT security.use_trading_context('{catalog}','{at}');
                    SELECT security.gas_amendment_refusal('{catalog}','{held['tradeId']}',1,'{held['instrumentId']}','{at}') IS NULL;''')
                assert earlier.splitlines()[-1]=='t', 'The earlier knowledge view must exclude the later payment'
        quantity = '20000' if name=='settled upfront premium' else '10000'
        _, candidate = book(proposed, quantity)  # The replacement programs are valid for a fresh booking.
        opened = ticket(held)
        assert opened['actions']['amend']['allowed']==available, (name, opened['actions']['amend'])
        if name=='settled upfront premium':
            assert Fraction(c.call('gas/composition/editor', dict(context=context, tradeId=held['tradeId']))['valuation']['sides'][1]['presentValue'])==0
        draft = deepcopy(opened['draft'])
        next(s for s in draft['sides'] if s['code']=='gas')['values']['quantity']=quantity
        values = next(s for s in draft['sides'] if s['code']==original['code'])['values']
        for term, field in [('from','charge_start'), ('paymentAt','charge_payment_at'), ('price','charge_price')]:
            if original[term]!=proposed[term]:
                values[field]=proposed[term]
        request = change(held, draft)
        before = state(held['tradeId'])
        for path in ('tickets/amend-preview','tickets/amend'):
            args = request if path.endswith('preview') else {**request,'operationId':str(uuid4())}
            assert c.call(path, args, expected=422)['code']==refusal, name
        native = {**candidate, 'tradeId':held['tradeId'], 'expectedRevision':1, 'bindingId':held['instrumentId'],
                  'reason':request['reason'], 'amendmentPolicy':policy, 'operationId':str(uuid4())}
        staged_booking(query, manifest, candidate, native, refusal, at=at)
        assert state(held['tradeId'])==before, name
        print('PASS future gas replacement refuses '+name+' and retains the original revision, binding and cash',flush=True)

    held, _ = book(future)
    opened = ticket(held)
    assert opened['actions']['amend']['allowed']
    draft = deepcopy(opened['draft'])
    next(s for s in draft['sides'] if s['code']==future['code'])['values']['charge_payment_at']='2021-04-23T00:00:00Z'
    request = change(held, draft)
    c.call('tickets/amend-preview', request)
    command = {**request, 'operationId':str(uuid4())}
    amended = c.call('tickets/amend', command)
    assert amended['revision']==2 and amended['instrumentId']!=held['instrumentId'] and amended['tradeId']==held['tradeId']
    pay(amended, '2021-04-23T00:00:00Z', '2021-04-23T16:00:00Z')
    assert not ticket(amended)['actions']['amend']['allowed']
    before = state(held['tradeId'])
    assert c.call('tickets/amend', command)==amended and state(held['tradeId'])==before
    print('PASS wholly future unpaid gas charge replacement and completed replay after a payment on its successor',flush=True)
