"""Physical gas swing booking through HTTP and the authenticated native writer."""
from copy import deepcopy
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal
import json
from uuid import uuid4

from assistant_context import migration_body
from check_booked_side_programs import runtime_query
from check_fact_anchor_upgrade import run_checks as check_upgrade_guard
from check_gas_pricing import request, staged_booking
from native_read_transport import select_context_inputs
from preview_client import PreviewClient
from synthetic_side_calendars import side_calendar_policy


WRITER = 'security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'
SHAPE = ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,'
         'market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric,date[],timestamptz[])')
TABLES = ('gas_swing_rights', 'gas_swing_members', 'gas_swing_days', 'gas_swing_pricing')


def predecessor(query, migration):
    check_upgrade_guard(query, migration, number='0403', guard='gas_swing_upgrade', signatures=[WRITER])
    return query(f"SELECT pg_get_functiondef('{WRITER}'::regprocedure)"), migration_body(migration.read_text())


def period_predecessor(query, migration):
    check_upgrade_guard(query, migration, number='0405', guard='gas_swing_period_upgrade', signatures=[WRITER, SHAPE])
    definitions = [query(f"SELECT pg_get_functiondef('{signature}'::regprocedure)") for signature in (SHAPE, WRITER)]
    restore = ';'.join(definitions)+f';DROP FUNCTION {SHAPE[:-1]},text);'
    return restore, migration_body(migration.read_text())


def populated_upgrade(query, manifest, prior, period_prior, body, booked):
    # Rehearse the upgrade over a real no-right booking in a rollback-only transaction.
    # Keep later migrations' objects intact, including dependencies on the current tables.
    writer, migration = prior
    catalog = manifest['catalogId']
    binding = booked['instrumentId']
    wire = json.dumps(body).replace("'", "''")
    expected = json.dumps(booked)
    replay = f"""DO $proof$ BEGIN
     IF security.book_gas_composition('{catalog}','{wire}'::jsonb,'2020-12-18T16:00:00Z') IS DISTINCT FROM '{expected}'::jsonb
     THEN RAISE EXCEPTION 'swing_upgrade_replay';END IF;END$proof$;"""
    snapshot = f"""SELECT to_jsonb(s) AS row FROM trading.gas_component_terms s WHERE instrument_id='{binding}'
        UNION ALL SELECT to_jsonb(s) FROM trading.instrument_side_schedules s WHERE instrument_id='{binding}'
        UNION ALL SELECT to_jsonb(p) FROM trading.side_schedule_programs p JOIN trading.instrument_side_schedules s
        USING(catalog_id,schedule_id) WHERE s.instrument_id='{binding}'
        UNION ALL SELECT to_jsonb(s) FROM trading.gas_trade_revisions s WHERE instrument_id='{binding}'"""
    isolate = 'CREATE SCHEMA swing_upgrade_fixture;' + ''.join(
        f'ALTER TABLE trading.{name} SET SCHEMA swing_upgrade_fixture;' for name in TABLES)
    isolate += ('ALTER INDEX trading.gas_swing_published_slot RENAME TO previous_swing_slot;'
                'ALTER FUNCTION trading.gas_swing_terms(uuid,uuid) SET SCHEMA swing_upgrade_fixture;'
                'ALTER FUNCTION trading.save_gas_swing(uuid,uuid,uuid,jsonb) SET SCHEMA swing_upgrade_fixture;')
    prefix = f'{isolate}{writer};CREATE TEMP TABLE previous_gas AS {snapshot};'
    saved_rows = 'SELECT row FROM (' + snapshot + ') saved'
    proof = f"""DO $proof$ BEGIN IF trading.gas_swing_terms('{catalog}','{binding}')<>'[]' OR EXISTS(
        (TABLE previous_gas EXCEPT ALL {saved_rows}) UNION ALL
        ({saved_rows} EXCEPT ALL TABLE previous_gas))
        THEN RAISE EXCEPTION 'swing_upgrade_history';END IF;END$proof$;"""
    runtime_query(query, manifest, replay + 'RESET SESSION AUTHORIZATION;' + migration + proof +
                  'SET LOCAL SESSION AUTHORIZATION preview_runtime;' + replay, prefix=prefix, commit=False)
    restore, period_migration = period_prior
    prefix = f"{restore}CREATE TEMP TABLE previous_gas AS {snapshot};"
    runtime_query(query, manifest, replay + 'RESET SESSION AUTHORIZATION;' + period_migration + proof +
                  'SET LOCAL SESSION AUTHORIZATION preview_runtime;' + replay, prefix=prefix, commit=False)
    print('PASS populated 0405 preserves no-right predecessor command replay, trade, program bytes and schedule bindings',flush=True)
    print('PASS populated 0402 writer replay and 0403 upgrade preserve the trade, components, program bytes and schedule bindings', flush=True)


def overlapping_upgrade(client, query, manifest, prior, context, base, right):
    from check_gas_exercise import amend_rights
    from check_gas_swing_routes import upgraded_book_checks
    restore, migration = prior
    book = str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic predecessor swing',publication_order,effective_at FROM trading.books LIMIT 1;")
    ordinary = deepcopy(base)
    ordinary.update(bookId=book,operationId=str(uuid4()))
    ordinary['physicalSides'][0]['deliveryPoint'] = 'synthetic_swing_overlap_point'
    query("BEGIN;"+restore+"COMMIT;")
    try:
        companion = client.call('gas/composition/book',ordinary)
        original = client.call('gas/composition/book',{**ordinary,'operationId':str(uuid4())})
        fixed = {**right,'dailyMinimum':'1000','dailyMaximum':'1000','totalMinimum':'3000','totalMaximum':'3000'}
        swing = amend_rights(query,manifest,original,[],'2020-12-18T16:00:00Z',swing_rights=[fixed])
        def sealed_programs():
            return query(f"""SELECT s.instrument_id,s.slot,s.schedule_id,encode(sha256(convert_to(to_jsonb(p)::text,'UTF8')),'hex')
                FROM trading.instrument_side_schedules s JOIN trading.side_schedule_programs p USING(catalog_id,schedule_id)
                WHERE s.instrument_id IN ('{swing['instrumentId']}','{companion['instrumentId']}')
                ORDER BY s.instrument_id,s.slot;""")
        before = sealed_programs()
        assert len(before.splitlines())==4 and swing['revision']==2
        assert json.loads(query(f"SELECT trading.gas_swing_terms('{manifest['catalogId']}','{swing['instrumentId']}')"))[0]['slots']==[0,1]
    finally:
        query('BEGIN;'+migration+'COMMIT;')
    assert sealed_programs()==before
    upgraded_book_checks(client,context,book,swing,companion)


def run_checks(url, query, manifest, prior, period_prior, run_worker, browse=None):
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', dict(profile='trader'))
    context, template = request(client)
    catalog = client.call('gas/catalog', context)
    path = next(p for p in catalog['locations'] if p['deliveryPoint'] == 'synthetic_swing_point')
    base = deepcopy(template)
    source = base['physicalSides'][0]
    source.pop('quantityPolicy')
    source.update({key: path[key] for key in ('pipeline', 'zone', 'deliveryPoint', 'product')},
                  quantity='1000', deliveryFrom='2021-01-31', deliveryTo='2021-02-03')
    calendar = source['calendarPolicy']['delivery']['calendar']
    base.update(profile='fixed_gas_composition_v1', cashSides=[dict(code='strike', role='consideration',
        source='gas', direction='pay', stage='delivery', share='1', price='3', tariffCode=None,
        paymentAt='2021-04-10T00:00:00Z', calendarPolicy=side_calendar_policy(calendar, delivery=True,
            observation=False, payment=True))])
    pricing = dict(model='gas_swing_one_factor_lognormal_v1', dayCount='act_365_fixed_instant_v1',
        numericalPolicy='swing_trinomial_6h_half_even_18_v1', marksConvention='curve_marks_log_return_held_v1')
    right = dict(side='gas', entries=['strike', 'gas'], holder='us', increment='100', dailyMinimum='700',
        dailyMaximum='1100', totalMinimum='3000', totalMaximum='3100', leadHours=6,
        quantityPolicy='swing_elected_delivery_quantity_v1', pricing=pricing)

    def terms(trade):
        return json.loads(query(f"SELECT trading.gas_swing_terms('{manifest['catalogId']}','{trade['instrumentId']}')"))

    def book(body):
        saved = client.call('gas/composition/book', body)
        assert client.call('gas/composition/book', body) == saved
        return saved

    plain = book(base)
    populated_upgrade(query, manifest, prior, period_prior, base, plain)
    assert terms(plain) == []
    for absent in (None, []):
        assert terms(book({**base, 'operationId': str(uuid4()), 'swingRights': absent})) == []
    def cut(trade):
        return client.call('gas/quantities/read', dict(context=context, tradeId=trade['tradeId'],
            selectionPolicy='operational_projection_v1'))['context']

    before = cut(plain)
    body = {**base, 'operationId': str(uuid4()), 'swingRights': [right]}
    saved = book(body)
    overlapping_upgrade(client, query, manifest, period_prior, context, base, right)
    for point in ('synthetic_swing_gap_point','synthetic_swing_overlap_point'):
        invalid = {**body,'operationId':str(uuid4()),'physicalSides':[{**source,'deliveryPoint':point}]}
        error = client.call('gas/composition/book',invalid,expected=422)
        assert (error.get('code'),error.get('side'),error.get('field'))==('gas_composition_input',source['code'],'delivery_point'),error
        staged_booking(query,manifest,body,invalid,'gas_composition_input',fields=dict(side=source['code'],field='delivery_point'))
    first = date(2021, 1, 31)
    assert terms(saved) == [dict(ordinal=0, physical_slot=0, slots=[0, 1], holder='us', reference_lots=10,
        daily_minimum=7, daily_maximum=11, total_minimum=30, total_maximum=31, quantity_policy=right['quantityPolicy'],
        time_rule='lead_before_first_gas_day_v1', lead_hours=6, model=pricing['model'], day_count=pricing['dayCount'],
        numerical_policy=pricing['numericalPolicy'], marks_convention=pricing['marksConvention'],
        days=[dict(day=str(first+timedelta(days=n)),
                   exercise_at=(datetime(2021, 1, 31, 3, 15, tzinfo=timezone.utc)+timedelta(days=n)).isoformat()) for n in range(3)])]
    catalog_id = manifest['catalogId']
    for selected, count in [(before, 0), (cut(saved), 1)]:
        coordinates = select_context_inputs(catalog_id, selected)
        read = f"""SELECT security.use_market_context('{catalog_id}','{selected['contextId']}');
            SELECT security.use_trading_context('{catalog_id}','2020-12-18T16:00:00Z');
            SELECT jsonb_array_length(trading.gas_swing_terms('{catalog_id}','{saved['instrumentId']}'));"""
        assert runtime_query(query, manifest, coordinates + read).splitlines()[-1] == str(count)
    forced = {**base, 'operationId':str(uuid4()), 'swingRights':[{**right, 'dailyMinimum':'1000',
        'dailyMaximum':'1000', 'totalMinimum':'3000', 'totalMaximum':'3000'}]}
    fixed = book(forced)
    fixed_view = client.call('gas/composition/editor', dict(context=context, tradeId=fixed['tradeId']))
    assert Decimal(fixed_view['valuation']['presentValue']) == Decimal('300')
    # The financial header and its child terms have independent visibility predicates.
    # Exercise the real held loader with the header readable and its pricing child withheld.
    query(f"""CREATE POLICY swing_hidden_price ON trading.gas_swing_pricing AS RESTRICTIVE FOR SELECT
        TO openetrm_runtime USING (instrument_id <> '{fixed['instrumentId']}'::uuid);""")
    try:
        predecessor_view = client.call('gas/composition/editor', dict(context=context, tradeId=plain['tradeId']))
        assert predecessor_view['valuation']['presentValue'] == '300'
        assert client.call('gas/composition/editor', dict(context=context, tradeId=fixed['tradeId']),
                           expected=422)['code'] == 'holder_choice_inputs'
    finally:
        query('DROP POLICY swing_hidden_price ON trading.gas_swing_pricing;')
    print('PASS held swing header with hidden pricing refuses; predecessor no-right header retains PV 300', flush=True)
    for endpoint, payload in [('gas/composition/editor', dict(context=context, tradeId=saved['tradeId'])),
                              ('gas/composition/preview', body)]:
        assert client.call(endpoint, payload, expected=422)['code'] == 'exercise_marks'
    deny = query(f"SET SESSION AUTHORIZATION preview_runtime;SELECT trading.gas_swing_terms('{manifest['catalogId']}','{saved['instrumentId']}');")
    assert deny == '[]'
    for table in TABLES:
        try:
            query(f"BEGIN;DELETE FROM trading.{table} WHERE instrument_id='{saved['instrumentId']}';ROLLBACK;")
        except RuntimeError as error:
            assert 'Gas components are immutable' in str(error), error
        else:
            raise AssertionError(f'Mutable {table}')

    another = book({**base, 'operationId':str(uuid4()),
        'physicalSides':[{**source, 'deliveryFrom':'2021-01-27', 'deliveryTo':'2021-01-30'}]})
    binding = another['instrumentId']
    original = saved['instrumentId']
    def incomplete_booking(overrides, pricing, expected):
        row = f"to_jsonb(r)||jsonb_build_object('instrument_id','{binding}'::uuid){overrides}"
        copy_pricing = f"""INSERT INTO trading.gas_swing_pricing SELECT
            (jsonb_populate_record(NULL::trading.gas_swing_pricing,to_jsonb(p)||jsonb_build_object('instrument_id','{binding}'::uuid))).*
            FROM trading.gas_swing_pricing p WHERE p.instrument_id='{original}';""" if pricing else ''
        try:
            query(f"""BEGIN;INSERT INTO trading.gas_swing_rights SELECT
                (jsonb_populate_record(NULL::trading.gas_swing_rights,{row})).* FROM trading.gas_swing_rights r
                WHERE instrument_id='{original}';{copy_pricing}COMMIT;""")
        except RuntimeError as error:
            assert expected in str(error), error
        else:
            raise AssertionError(f'Committed an incomplete binding without {expected}')
        assert terms(another) == []
    incomplete_booking('', True, 'swing_published_schedule')
    exact_schedule = f"""||jsonb_build_object('schedule_id',(SELECT schedule_id FROM trading.instrument_side_schedules
        WHERE instrument_id='{binding}' AND slot=0))"""
    incomplete_booking(exact_schedule, False, 'swing_required_pricing')

    def reject(original, rights, code):
        invalid = {**original, 'operationId': str(uuid4()), 'swingRights': rights}
        assert client.call('gas/composition/book', invalid, expected=422)['code'] == code, invalid
        staged_booking(query, manifest, original, invalid, code)

    for edits, code in [
        (dict(entries=['gas','gas']), 'swing_right'), (dict(entries=['gas']), 'swing_right'),
        (dict(dailyMinimum='0700'), 'swing_right'), (dict(dailyMinimum='707'), 'swing_right'), (dict(totalMinimum='3400', totalMaximum='3500'), 'swing_feasibility'),
        (dict(totalMaximum='2050'), 'swing_right'), (dict(pricing=None), 'swing_model_scope'),
        (dict(pricing={**pricing, 'model':'unknown'}), 'swing_model_scope'), (dict(side='strike'), 'swing_right'),
        (dict(leadHours=-1), 'swing_right'), (dict(leadHours=2147483647), 'swing_right'),
        (dict(quantityPolicy='delivery_schedule_v1'), 'swing_right')]:
        reject(base, [{**right, **edits}], code)
    reject(base, [right, right], 'swing_right')
    for changes in (dict(swingRights=[{**right, 'unknownKey': 1}]),
                    dict(swingRights=[{**right, 'pricing': {**pricing, 'unknownKey': 1}}]), dict(unknownKey=1)):
        invalid = {**body, **changes, 'operationId': str(uuid4())}
        assert client.call('gas/composition/book', invalid, expected=400)['code'] == 'request_format'
        code = 'gas_composition_input' if 'unknownKey' in changes else ('swing_right' if 'unknownKey' in changes['swingRights'][0] else 'swing_model_scope')
        staged_booking(query, manifest, base, invalid, code)

    # ASP.NET's normal Web options accept case-insensitive keys and quoted numbers.
    upper_lead = {k:v for k,v in right.items() if k != 'leadHours'} | {'LEADHOURS':6}
    upper_model = {k:v for k,v in pricing.items() if k != 'model'} | {'MODEL':pricing['model']}
    for extra, native_code in [
        ({'swingRights':[{**right, 'leadHours':'6'}]}, 'swing_right'),
        ({'swingRights':[upper_lead]}, 'swing_right'),
        ({'swingRights':[{**right, 'pricing':upper_model}]}, 'swing_model_scope'),
        ({'SWINGRIGHTS':[right]}, 'gas_composition_input')]:
        invalid = {**base, **extra, 'operationId':str(uuid4())}
        assert client.call('gas/composition/book', invalid, expected=400)['code'] == 'request_format'
        staged_booking(query, manifest, base, invalid, native_code)
    print('PASS HTTP and native swing booking reject a quoted lead and wrong-case right, pricing and section names', flush=True)

    def source_case(physical, code='swing_right'):
        original = {**base, 'operationId': str(uuid4()), 'physicalSides': physical}
        book(original)
        reject(original, [right], code)

    source_case([{**source, 'quantityPolicy':'delivery_schedule_v1'}])
    source_case([{**source, 'quantityBasis':'period_total_equal_days'}])
    source_case([{**source, 'deliveryFrom':'2021-01-01'}])
    source_case([source, {**source, 'code':'linked', 'direction':'deliver', 'quantity':None,
                          'link':dict(source='gas', formula='gross_less_fraction', fraction='0.02')}])
    adjusted = next(p for p in catalog['locations'] if p.get('valuation') and
                    Decimal(p['valuation']['groupAdjustment']) + Decimal(p['valuation']['locationAdjustment']) != 0)
    source_case([{**source, **{key:adjusted[key] for key in ('pipeline','zone','deliveryPoint','product')}}], 'swing_model_scope')
    short = {**base, 'operationId':str(uuid4()), 'cashSides':[{**base['cashSides'][0], 'deliveryFrom':'2021-02-01'}]}
    book(short)
    reject(short, [right], 'swing_right')
    indexed = deepcopy(template)
    indexed['operationId'] = str(uuid4())
    indexed['physicalSides'][0].pop('quantityPolicy')
    indexed['physicalSides'][0]['quantity'] = '1000'
    indexed['cashSides'] = [{**indexed['cashSides'][0], 'code':'strike'}]
    indexed['cashSides'][0].pop('part')
    book(indexed)
    reject(indexed, [right], 'swing_right')
    exercise = {**base, 'operationId':str(uuid4()), 'exerciseRights':[dict(entries=['gas','strike'], period='delivery_day',
                holder='us', settlement='physical', leadHours=8)]}
    book(exercise)
    reject(exercise, [right], 'swing_right')
    # A same-source reported-balance pair and an independently owned premium retain their own quantities.
    outside = deepcopy(body)
    outside.update(operationId=str(uuid4()), profile='indexed_gas_composition_v1')
    outside['physicalSides'][0].update(deliveryFrom='2021-01-13', deliveryTo='2021-01-16')
    outside['imbalanceCashouts'] = [dict(code='balance', profile='daily_index_cashout_v1', side='gas',
        index='synthetic_entry_daily', spreadOwedToUs='0', spreadOwedByUs='0', paymentDay=15,
        statementPolicy='final_statement_balance_v1', statementBasis='daily_imbalance_v1', accountNetting='net_accounts_v1',
        calendarPolicy=template['cashSides'][0]['calendarPolicy'])]
    outside['chargeSides'] = [dict(code='premium', basis='lump_sum', direction='pay', price='2', clockSide='gas',
        dailyQuantity=None, paymentDay=None, paymentAt='2021-02-10T00:00:00Z', calendarPolicy=base['cashSides'][0]['calendarPolicy'],
        **{'from':None,'to':None})]
    outside_saved = book(outside)
    assert terms(outside_saved)[0]['slots'] == [0, 1]
    assert json.loads(query(f"SELECT jsonb_agg(role ORDER BY slot) FROM trading.gas_component_terms WHERE instrument_id='{outside_saved['instrumentId']}';")) == [
        'physical','consideration','imbalance_cashout','imbalance_cashout','charge']
    for excluded in ('balance.to_us','premium'):
        reject(outside, [{**right, 'entries':[*right['entries'], excluded]}], 'swing_right')
    print('PASS physical gas swing cross-month booking, exact non-midnight deadlines, native parity, missing marks and immutable scoped terms', flush=True)
    from check_gas_swing_elections import election_checks
    election_checks(client, query, manifest, context, body, plain, run_worker)
    writer = PreviewClient(url)
    writer.bootstrap()
    writer.call('profile',dict(profile='editor'))
    writer.call('volatility-marks/record', dict(context=context,definitionRevisionId=body['definitionRevisionId'],
        operationId=str(uuid4()),reason='Synthetic swing monthly inputs',curve='synthetic_swing_location',
        marks=[dict(month=m,volatility='0',expectedRevision=0) for m in ('2021-01','2021-02')]))
    assert client.call('gas/composition/preview',body)['presentValue']=='310'
    if browse:
        # Zero is outside the mandatory contract's daily limits. Qualify it on its own optional right.
        optional = book({**body,'operationId':str(uuid4()),'swingRights':[{**right,'dailyMinimum':'0','totalMinimum':'0','totalMaximum':'1000'}]})
        browse(dict(optionalTrade=optional))
    from check_gas_swing_routes import reported_cash_checks, route_checks
    reported_cash_checks(client, query, manifest, context, outside_saved)
    route_checks(client, query, manifest, context, body, run_worker)


def measure_workloads(dotnet, output):
    """One fresh process per contract/scenario, two consecutive calls to the production pricing kernel."""
    from pathlib import Path
    import platform
    import subprocess
    import tempfile

    root = Path(__file__).resolve().parents[1]
    dll = root / 'tests/OpenEtrm.MarketData.Checks/bin/Debug/net10.0/OpenEtrm.MarketData.Checks.dll'
    cases = []
    with tempfile.TemporaryDirectory(prefix='openetrm-swing-work-') as directory:
        sample = Path(directory) / 'sample.json'
        for days in (1, 3, 31):
            for maximum in (1, 4):
                for scenario in ('base', 'price', 'volatility'):
                    subprocess.run([str(dotnet), str(dll), '--gas-swing-workload', str(days), str(maximum), scenario, str(sample)],
                                   cwd=root, check=True, timeout=120)
                    cases.append(json.loads(sample.read_text()))
    output.write_text(json.dumps(dict(scope='SwingChoice.Value pricing kernel', contracts=6, scenariosPerContract=3,
        valuations=36, processConcurrency=1, os=platform.platform(), machine=platform.machine(), cases=cases), indent=2)+'\n')


if __name__ == '__main__':
    import argparse
    from pathlib import Path

    parser = argparse.ArgumentParser(description='Measure bounded gas swing pricing workloads.')
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--workloads', type=Path, required=True, help='Write private workload measurements to this JSON file.')
    args = parser.parse_args()
    measure_workloads(args.dotnet.resolve(), args.workloads)
