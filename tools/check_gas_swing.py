"""Physical gas swing booking through HTTP and the authenticated native writer."""
from copy import deepcopy
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal
import json
from uuid import uuid4

from assistant_context import check_guard
from check_booked_side_programs import runtime_query
from check_fact_anchor_upgrade import run_checks as check_upgrade_guard
from check_gas_pricing import request, staged_booking
from native_read_transport import select_context_inputs
from preview_client import PreviewClient, initial_frame
from synthetic_side_calendars import side_calendar_policy


WRITER = 'security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'
SHAPE = ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,'
         'market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric,date[],timestamptz[])')
TABLES = ('gas_swing_rights', 'gas_swing_members', 'gas_swing_days', 'gas_swing_pricing')


def overload_guard(query,migration,number,guard,signatures):
    changes=[(f"CREATE FUNCTION {signature.split('(')[0]}(boolean) RETURNS boolean LANGUAGE sql AS 'SELECT $1';",signature)
             for signature in signatures]
    check_guard(query,migration.read_text(),number,guard,changes)


def predecessor(query, migration):
    overload_guard(query,migration,'0403','gas_swing_upgrade',[WRITER])
    check_upgrade_guard(query, migration, number='0403', guard='gas_swing_upgrade', signatures=[WRITER])


def period_predecessor(query, migration):
    activated=[WRITER,SHAPE,'security.record_gas_cash_payment(uuid,jsonb,timestamptz)',
        'security.gas_amendment_refusal(uuid,uuid,integer,uuid,timestamptz)','security.record_gas_swing_elections(uuid,jsonb,timestamptz)']
    overload_guard(query,migration,'0405','gas_swing_period_upgrade',activated)
    check_upgrade_guard(query, migration, number='0405', guard='gas_swing_period_upgrade', signatures=activated)
    finalizers=('security.check_operation_seals()','security.finish_operation_read()')
    check_guard(query,migration.read_text(),'0405','gas_swing_period_upgrade',
        [(f'REVOKE EXECUTE ON FUNCTION {helper} FROM openetrm_risk_worker;',helper) for helper in finalizers])
    signatures=','.join(f"'{helper}'::regprocedure" for helper in finalizers)
    contracts=f"SELECT to_jsonb(p) FROM pg_proc p WHERE oid IN({signatures}) ORDER BY proname"
    before=query(contracts)
    # Execute the actual final transaction body and force rollback immediately before commit.
    final = migration.read_text()
    try:
        query(final[:final.rindex('COMMIT;')] + "DO $$BEGIN RAISE EXCEPTION 'synthetic final rollback';END$$;COMMIT;")
    except RuntimeError as error:
        if 'synthetic final rollback' not in str(error): raise
    else:
        raise AssertionError('forced activation rollback committed')
    from check_gas_swing_upgrade import owner_only_prefix
    owner_only_prefix(query, 'rolled-back 0405')
    assert query(contracts)==before, 'Rolled-back activation changed the operation finalizers'
    return query(f"SELECT pg_get_functiondef('{SHAPE}'::regprocedure)")


def overlapping_upgrade(client, query, manifest, prior, context, base, right):
    from check_gas_exercise import amend_rights
    from check_gas_swing_routes import upgraded_book_checks
    extended = SHAPE[:-1]+',text)'
    strict = query(f"SELECT pg_get_functiondef('{extended}'::regprocedure)")
    old_signature = 'charge_due timestamp with time zone[])'
    assert prior.count(old_signature)==1
    monthly = prior.replace(old_signature,'charge_due timestamp with time zone[], swing_source text)')
    book = str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic predecessor swing',publication_order,effective_at FROM trading.books LIMIT 1;")
    ordinary = deepcopy(base)
    ordinary.update(bookId=book,operationId=str(uuid4()))
    ordinary['physicalSides'][0]['deliveryPoint'] = 'synthetic_swing_overlap_point'
    query(monthly)
    try:
        companion = client.call('gas/composition/book',ordinary)
        original = client.call('gas/composition/book',{**ordinary,'operationId':str(uuid4())})
        fixed = {**right,'dailyMinimum':'1000','dailyMaximum':'1000','totalMinimum':None,'totalMaximum':None,'totalBasis':None}
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
        query(strict)
    assert sealed_programs()==before
    upgraded_book_checks(client,context,book,swing,companion)


def run_checks(url, query, manifest, period_prior, run_worker, browse=None, *, check_prefix,book_native):
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
    from check_gas_swing_activation import january_http, january_native, signed_parcel, booking_fields, earned_cash_and_amendment, long_transport
    from check_gas_swing_activation import january
    right = dict(january(base)['swingRights'][0],entries=['strike','gas'],dailyMinimum='700',dailyMaximum='1100',
        limitPeriod='term',totalBasis='per_period_v1',totalMinimum='3000',totalMaximum='3100',changeCount=None,countRule=None)
    pricing = right['pricing']
    booking_fields(client,query,manifest,base)
    january_native(client,query,manifest,context,base)
    january_http(client,context,base)
    signed_parcel(client,query,manifest,context,base)
    earned_cash_and_amendment(client,query,manifest,context,base)
    def terms(trade):
        return json.loads(query(f"SELECT trading.gas_swing_terms('{manifest['catalogId']}','{trade['instrumentId']}')"))

    def book(body):
        saved = client.call('gas/composition/book', body)
        assert client.call('gas/composition/book', body) == saved
        return saved

    plain = book(base)
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
    assert terms(saved) == [dict(ordinal=0, physical_slot=0, slots=[0, 1], holder='us',
        daily_minimum=[7,10], daily_maximum=[11,10], planning_step=[1,10], total_minimum=[3,1], total_maximum=[31,10],
        quantity_policy=right['quantityPolicy'], limit_period='term',total_basis='per_period_v1',change_count=None,count_rule=None,
        plan_rule=right['planRule'],time_rule=right['deadlineRule'],lead_hours=6,model=pricing['model'],day_count=pricing['dayCount'],
        numerical_policy=pricing['numericalPolicy'],marks_convention=pricing['marksConvention'],
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
        'dailyMaximum':'1000', 'totalMinimum':None, 'totalMaximum':None, 'totalBasis':None}]}
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
    book({**body, 'operationId':str(uuid4()), 'physicalSides':[{**source,'deliveryFrom':'2021-01-01'}],
        'swingRights':[{**right,'totalMinimum':None,'totalMaximum':None,'totalBasis':None}]})
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
    route_book=str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{route_book}',portfolio_id,'Synthetic swing routes',publication_order,effective_at FROM trading.books LIMIT 1;")
    outside['bookId']=route_book
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
    from check_gas_swing_routes import book_coverage_case, check_coverage_ticket, reported_cash_checks
    reported_cash_checks(client, query, manifest, context, outside_saved)
    coverage,coverage_context=book_coverage_case(client,context,body)
    check_coverage_ticket(client,coverage,coverage_context,priced=False)
    browser_failure=None
    if browse:
        # Zero is outside the mandatory contract's daily limits. Qualify it on its own optional right.
        optional = book({**body,'operationId':str(uuid4()),'swingRights':[{**right,'dailyMinimum':'0','limitPeriod':'delivery_month','totalMinimum':None,'totalMaximum':'1005','totalBasis':'per_day_v1','changeCount':1,'countRule':'differs_from_base_v1'}]})
        january = initial_frame(client.call('workspace'),'2021-01-31')
        position = dict(context={**context,'snapshotId':january['id'],'convention':'trading_workspace_v1'},
            tradeId=outside_saved['tradeId'],bindingId=outside_saved['instrumentId'],revision=1,instrumentType='physical_gas')
        cashout_reference = client.call('trading/position',position)['holding']['tradeReference']
        try:browse(dict(optionalTrade=optional,coverageTrade=coverage,cashoutTrade={**outside_saved,'tradeReference':cashout_reference}))
        except Exception as error:browser_failure=error
    writer.call('volatility-marks/record',dict(context=context,definitionRevisionId=body['definitionRevisionId'],
        operationId=str(uuid4()),reason='Synthetic February coverage check',curve='synthetic_swing_long_location',
        marks=[dict(month='2021-02',volatility='0',expectedRevision=0)]))
    check_coverage_ticket(client,coverage,coverage_context,priced=True)
    long_transport(client,context,base,book_native)
    check_prefix(base)
    from check_gas_swing_routes import route_checks
    route_checks(client,query,manifest,context,{**base,'operationId':str(uuid4()),'swingRights':[right]},run_worker,route_book,outside_saved)
    if browser_failure:raise browser_failure


def observed_operation(operation, read):
    """Sample only the disposable fixture's own evidence while an ordinary operation is in flight."""
    from concurrent.futures import ThreadPoolExecutor
    from time import monotonic, sleep
    samples=[]
    with ThreadPoolExecutor(max_workers=1) as pool:
        start=monotonic()
        running=pool.submit(operation)
        while not running.done():
            sample=read()
            if sample:
                samples.append(sample)
            sleep(.035)
        return running.result(),monotonic()-start,samples


def workload_routes(url, query, manifest, run_worker, api_pid, length):
    from concurrent.futures import ThreadPoolExecutor
    from threading import Event
    import subprocess
    from time import monotonic
    stopped=Event()
    samples=[]
    def sample():
        while not stopped.is_set():
            reading=subprocess.run(['ps','-o','rss=','-p',str(api_pid)],capture_output=True,text=True,check=True)
            samples.append((monotonic(),int(reading.stdout.strip())))
            stopped.wait(.07)
    with ThreadPoolExecutor(max_workers=1) as pool:
        watcher=pool.submit(sample)
        try:
            observations,workers=workload_values(url,query,manifest,run_worker,samples,(length,) if length else (90,365))
        finally:
            stopped.set()
            watcher.result()  # An interrupted sampler invalidates the RSS measurement.
            if samples:
                print(f'WORKLOAD Debug API baseline {samples[0][1]} KiB sampled peak {max(m for _,m in samples)} KiB',flush=True)
    from gas_swing_reference import check_long_value
    for days,value,changes in observations:
        check_long_value(days,value,**changes)
    print(f'PASS {len(observations)} retained and isolated-shock values against independently conditioned periods',flush=True)
    require_host_memory(max(rss for _,rss in samples),workers)
    print('PASS ordinary process RSS within 2 GiB for the API and 1 GiB for each worker',flush=True)


def cancellation_elapsed(start, observed):
    elapsed = observed - start
    if elapsed > 1:
        raise AssertionError(f'Native worker cancellation exceeded one second ({elapsed:.3f}s)')
    return elapsed


def require_host_memory(api_kib,workers_kib):
    if api_kib>2*1024**2:
        raise AssertionError(f'API RSS {api_kib} KiB exceeded the 2 GiB qualification threshold')
    for job,rss in workers_kib.items():
        if rss <= 0:
            raise AssertionError(f'{job} worker has no positive RSS observation')
        if rss>1024**2:
            raise AssertionError(f'{job} worker RSS {rss} KiB exceeded the 1 GiB qualification threshold')


def workload_values(url, query, manifest, run_worker, samples, lengths):
    """Qualify single-binding seasonal and monthly jobs under the ordinary preview budgets."""
    from time import monotonic
    from check_gas_swing_activation import january
    from preview_fixture import gas_purchase

    from check_gas_swing_upgrade import resource_cases
    resource_cases(query)
    trader, writer, risk = (PreviewClient(url) for _ in range(3))
    for client, profile in ((trader,'trader'),(writer,'editor'),(risk,'risk')):
        client.bootstrap()
        client.call('profile',dict(profile=profile))
    frame = initial_frame(trader.call('workspace'),'2040-12-31')
    context = dict(snapshotId=frame['id'],convention='physical_gas_fixed_quantity_v4',knowledgeMode='current')
    template = gas_purchase(context,trader.call('gas/catalog',context),point='synthetic_swing_workload_point',fee=None)
    source, = template['physicalSides']
    source.update(quantityBasis='per_operating_day',deliveryFrom='2041-01-01')
    strike, = template['cashSides']
    strike.update(code='strike',paymentAt='2042-01-15T00:00:00Z')
    definition = template['definitionRevisionId']
    writer_frame=initial_frame(writer.call('workspace'),'2020-12-18')
    risk_frame=initial_frame(risk.call('workspace'),'2040-12-31')
    writer.call('volatility-marks/record',dict(context=dict(context,snapshotId=writer_frame['id']),definitionRevisionId=definition,
        curve='synthetic_swing_workload_location',operationId=str(uuid4()),reason='Synthetic marked swing workload',
        marks=[dict(month=f'2041-{month:02d}',volatility='0.20' if month%2 else '0.25',expectedRevision=0) for month in range(1,13)]))
    values_to_verify=[]
    worker_peaks={}
    for length in lengths:
        body = january(template)
        body['physicalSides'][0].update(deliveryFrom='2041-01-01',deliveryTo=(date(2041,1,1)+timedelta(days=length)).isoformat())
        if length==90:
            body['swingRights'][0].update(totalMaximum='90100',limitPeriod='term',totalBasis='per_period_v1')
        start = monotonic()
        operation = body['operationId']
        proof_sql = f"""SELECT json_build_object('frame',f.frame_id,'valuation',f.valuation_at,'until',f.authority_until,
            'requestUntil',q.expires_at) FROM security.gas_swing_booking_frames f JOIN security.request_contexts q
            ON q.id=f.request_id WHERE f.operation_id='{operation}'"""
        saved,elapsed,proofs=observed_operation(lambda:trader.call('gas/composition/book',body),lambda:query.raw(proof_sql))
        assert proofs, 'No original booking attestation was observed before commit'
        proof=json.loads(proofs[-1])
        actual_commit=datetime.fromisoformat(query.raw(f"""SELECT pg_xact_commit_timestamp(xmin) FROM trading.gas_trade_revisions
            WHERE (trade_id,operation_id)=('{saved["tradeId"]}','{operation}')"""))
        headroom=[(datetime.fromisoformat(proof[k])-actual_commit).total_seconds() for k in ('until','requestUntil')]
        assert elapsed<15 and proof['frame']==frame['id'] and min(headroom)>0 and not query.raw(proof_sql)
        print(f'WORKLOAD {length} booking {elapsed:.3f}s authority/request seconds remaining at commit {headroom} '
              f'API peak {max((rss for t,rss in samples if t>=start),default=0)} KiB',flush=True)
        start = monotonic()
        editor = trader.call('gas/composition/editor',dict(context=context,tradeId=saved['tradeId']))['valuation']
        read_seconds=monotonic()-start
        assert read_seconds<15
        print(f'WORKLOAD {length} retained {read_seconds:.3f}s PV {editor["presentValue"]} '
              f'API peak {max((rss for t,rss in samples if t>=start),default=0)} KiB',flush=True)
        scope = dict(context=dict(context,snapshotId=risk_frame['id'],convention='trading_workspace_v1'),bookIds=[],portfolioIds=[],tradeIds=[saved['tradeId']])
        catalog = risk.call('portfolio/risk/catalog',scope)
        months = 3 if length==90 else 12
        assert not catalog['excludedPositions'] and len(catalog['factors'])==len(catalog['volatilityInputs'])==months
        pinned = dict(scope['context'],knowledgeMode='retained',contextId=catalog['context']['contextId'])
        contract = catalog['joint']
        values_to_verify.append((length,editor['presentValue'],{}))
        mark_month={m['revisionId']:int(m['month'][5:7]) for m in editor['swingRights'][0]['marks']}
        original_identity = None
        pair_start=monotonic()
        completed=[]
        for kind, inputs in [('price',catalog['factors']),('volatility',catalog['volatilityInputs'])]:
            scenarios = []
            cases={}
            for entry in inputs:
                month=int(entry['deliveryFrom'][5:7]) if kind=='price' else mark_month[entry['id']]
                for direction, amount in [('up','0.01'),('down','-0.01')]:
                    label = f'{kind}-{month:02d}-{direction}'
                    cases[label]={kind:(month,amount)}
                    sample = dict(id=label,label=label,shifts=[])
                    if kind=='price':
                        sample['shifts']=[dict(factor=entry['factor'],size=amount,convention=contract['shiftConvention'])]
                    else:
                        sample['volatility']=[dict(inputId=entry['id'],size=amount,convention=contract['volatilityShiftConvention'])]
                    scenarios.append(sample)
            assert set(cases)=={f'{kind}-{month:02d}-{direction}' for month in range(1,months+1) for direction in ('up','down')}
            request = dict(scope,context=pinned,detail='books',convention=catalog['convention'],numericalPolicy=catalog['numericalPolicy'],
                pricingPolicy=catalog['pricingPolicy'],stress=[],bumps=[],joint=dict(convention=contract['convention'],derivatives=None,
                scenarios=dict(id='swing-'+kind,version='1',convention=contract['scenarioConvention'],scenarios=scenarios)))
            start = monotonic()
            job = risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request))
            print(f'WORKLOAD {length} {kind} submitted {monotonic()-start:.3f}s',flush=True)
            job_id=job['id']
            lease_sql=f"SELECT lease_expires_at FROM risk.jobs WHERE id='{job_id}' AND state='running'"
            peak,worker_elapsed,leases=observed_operation(lambda:run_worker(measure=True),lambda:query.raw(lease_sql))
            result = risk.call('portfolio/risk/jobs/'+job_id)
            clock_row=json.loads(query.raw(f"""SELECT json_build_object('started',started_at,'commit',pg_xact_commit_timestamp(xmin),
                'hasResult',result IS NOT NULL,'leaseReleased',lease_token IS NULL AND worker_login IS NULL)
                FROM risk.jobs WHERE id='{job_id}'"""))
            assert clock_row['leaseReleased'] and clock_row['hasResult']==(result['state']=='completed')
            committed=datetime.fromisoformat(clock_row['commit'])
            execution_elapsed=(committed-datetime.fromisoformat(clock_row['started'])).total_seconds()
            assert leases
            lease_headroom=(datetime.fromisoformat(leases[-1])-committed).total_seconds()
            print(f'WORKLOAD {length} {kind} process {worker_elapsed:.3f}s commit {execution_elapsed:.3f}s '
                  f'execution remaining {60-execution_elapsed:.3f}s lease remaining {lease_headroom:.3f}s '
                  f'observed lease deadlines {len(set(leases))}',flush=True)
            print(f'WORKLOAD {length} {kind} worker peak {peak} KiB elapsed {monotonic()-start:.3f}s',flush=True)
            assert result['state']=='completed',(length,kind,result['state'],result.get('error'))
            assert 0<=execution_elapsed<60 and lease_headroom>0
            report = result['result']
            joint = report['joint']
            assert [sample['id'] for sample in joint['samples']]==[s['id'] for s in scenarios]
            assert {sample['totals'][0]['values']['base'] for sample in joint['samples']}=={editor['presentValue']}
            for sample in joint['samples']:
                total,=sample['totals']
                values_to_verify.append((length,total['values']['value'],cases[sample['id']]))
            identity={key:report[key] for key in ('snapshotId','asOf','context','scenario','modelContracts','inputs')}
            identity['baseTotals']=joint['baseTotals']
            if original_identity is None:
                original_identity=identity
            else:
                assert original_identity==identity, 'Separate jobs lost their common input identity or base value'
            work=report['work']
            assert (work['positionCount'],work['uniqueInstruments'],work['uniqueMarketContracts'])==(1,1,months)
            assert work['scenarioCount']==work['unitPricerCalls']==len(scenarios)+1
            assert work['totalUnitPricerCalls']==work['unitPricerCalls']+work['preparationUnitPricerCalls']
            assert work['preparationUnitPricerCalls']==1 and work['reusedUnitScenarioValues']==0
            worker_peaks[f'{length}-{kind}']=peak
            completed.append(work)
            print(f'WORKLOAD {length} {kind} result work '+json.dumps(work),flush=True)
            print(f'WORKLOAD {length} {kind} isolated shocks '+json.dumps(scenarios),flush=True)
            print(f'PASS {length} {kind} worker {len(scenarios)} child scenarios {monotonic()-start:.3f}s {peak} KiB',flush=True)

        print(f'WORKLOAD {length} two committed jobs elapsed {monotonic()-pair_start:.3f}s including both submitted requests and reads, '
              f'{sum(w["scenarioCount"] for w in completed)} base-plus-child scenarios, '
              f'{sum(w["totalUnitPricerCalls"] for w in completed)} unit calls (including both preparations), '
              f'{sum(w["reusedUnitScenarioValues"] for w in completed)} reused unit scenario values',flush=True)
        warm_start=monotonic()
        repeated=trader.call('gas/composition/editor',dict(context=context,tradeId=saved['tradeId']))['valuation']
        assert repeated['presentValue']==editor['presentValue']
        print(f'WORKLOAD {length} repeated retained read {monotonic()-warm_start:.3f}s',flush=True)
        if length==90:
            ids=dict(tradeId=saved['tradeId'],instrumentId=saved['instrumentId'])
            heads=trader.call('gas/swing/read',dict(context=context,**ids))['heads']
            day='2041-02-15'
            at='2040-12-31T12:00:00Z'
            statement=dict(day=day,quantity='1050',exercisingParty='us',submittedAt=at,effectiveAt=at,
                evidence='Synthetic seasonal off-grid reservation')
            trader.call('gas/swing/record',dict(context=context,**ids,operationId=str(uuid4()),expectedRevision=1,right=0,
                expectedHeads=[{k:h[k] for k in ('day','revisionId')} for h in heads],elections=[statement]))
            reserved=trader.call('gas/composition/editor',dict(context=context,tradeId=saved['tradeId']))['valuation']
            right,=reserved['swingRights']
            assert right['modeledDays']==89 and Decimal(reserved['presentValue'])-Decimal(right['counted'])==Decimal('52.5')
            values_to_verify.append((length,right['counted'],dict(election=45)))
        else:
            cut=dict(context,snapshotId=initial_frame(trader.call('workspace'),'2041-01-02')['id'])
            missed=trader.call('gas/composition/editor',dict(context=cut,tradeId=saved['tradeId']))['valuation']
            right,=missed['swingRights']
            assert missed['incomplete'] and missed['unassessedDays']==31 and right['modeledDays']==334
            assert [d['state'] for d in right['days'][:31]]==['missing_election']*2+['period_unassessed']*29
            values_to_verify.append((length,right['counted'],dict(missed=True)))

            # Run the same complete retained request. Cancel a real running attempt
            # and wait for its ordinary heartbeat to acknowledge the command.
            from concurrent.futures import ThreadPoolExecutor
            from time import sleep
            cancelled=risk.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request))['id']
            with ThreadPoolExecutor(max_workers=1) as pool:
                running=pool.submit(run_worker,measure=True)
                limit=monotonic()+20
                while query.raw(f"SELECT state='running' AND clock_timestamp()>started_at+interval '2 seconds' FROM risk.jobs WHERE id='{cancelled}'")!='t':
                    if running.done() or monotonic()>limit:
                        raise AssertionError('Could not observe the running cancellation workload')
                    sleep(.05)
                start=monotonic()
                risk.call(f'portfolio/risk/jobs/{cancelled}/cancel',{})
                while True:
                    acknowledged = query.raw(f"SELECT state='cancelled' AND result IS NULL AND lease_token IS NULL "
                                             f"AND worker_login IS NULL FROM risk.jobs WHERE id='{cancelled}'") == 't'
                    observed = monotonic()
                    elapsed = cancellation_elapsed(start, observed)
                    if acknowledged:
                        break
                    sleep(.01)
                worker_peaks[f'{length}-cancelled']=running.result()
            terminal=risk.call('portfolio/risk/jobs/'+cancelled)
            assert terminal['state']=='cancelled' and terminal.get('result') is None
            assert query.raw("SELECT count(*) FROM pg_stat_activity WHERE usename='preview_risk_worker'")=='0'
            print(f'PASS running annual job acknowledged native cancellation and released its lease without a result in {elapsed:.3f}s; '
                  f'worker exit and administrative checks took another {monotonic()-observed:.3f}s',flush=True)

    return values_to_verify,worker_peaks


def kernel_workloads(root, dotnet, no_build):
    import os
    import platform
    import subprocess
    import tempfile
    from gas_swing_reference import check_long_value
    cases=[(1,'standard'),(3,'standard'),(3,'dense'),(3,'dst'),(31,'standard'),(31,'dst'),(90,'standard'),
           (90,'reserved'),(365,'standard'),(365,'missed'),(1,'prefix'),(1,'memory')]
    project=root/'tests/OpenEtrm.MarketData.Checks'
    dll=project/'bin/Release/net10.0/OpenEtrm.MarketData.Checks.dll'
    if not no_build:
        subprocess.run([dotnet,'build',str(project),'-c','Release'],cwd=root,check=True)
    from hashlib import sha256
    artifacts=(dll,dll.parent/'OpenEtrm.MarketData.dll',dll.with_suffix('.runtimeconfig.json'))
    print('KERNEL Release host '+json.dumps(dict(host=platform.platform(),
        sha256={f.name:sha256(f.read_bytes()).hexdigest() for f in artifacts})),flush=True)
    for days,variant in cases:
        with tempfile.TemporaryFile(mode='w+t') as out:
            process=subprocess.Popen([dotnet,str(dll),'--gas-swing-unit-workload',str(days),variant],cwd=root,stdout=out)
            _,status,usage=os.wait4(process.pid,0)
            process.returncode=os.waitstatus_to_exitcode(status)
            if process.returncode:
                raise RuntimeError(f'{days} {variant} kernel exited {process.returncode}')
            out.seek(0)
            runs=[json.loads(line) for line in out]
        peak=usage.ru_maxrss if platform.system()=='Darwin' else usage.ru_maxrss*1024
        assert len(runs)==2 and runs[0]['Value']==runs[1]['Value'] and peak<=512*1024**2
        for row in runs:
            if variant=='dst':
                # Through March 11, the last deadline precedes the regular grid by
                # one hour. Each of the subsequent 28 off-phase deadlines adds a split.
                assert row['Estimate']['Steps']==(9 if days==3 else 149)
            assert row['Milliseconds']<=120000 and row['AllocatedBytes']>0
            print('KERNEL '+json.dumps(dict(row,Build='Release',ProcessPeakRssBytes=peak)),flush=True)
        if days in (90,365) and variant in ('standard','reserved','missed'):
            parts=check_long_value(days,runs[0]['Value'],election=45 if variant=='reserved' else None,missed=variant=='missed')
            print(f'PASS {days} {variant} independent period values from the common inception '+','.join(map(str,parts)),flush=True)


if __name__ == '__main__':
    import argparse
    import os
    from pathlib import Path
    import subprocess
    import sys

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workloads',action='store_true',required=True)
    parser.add_argument('--no-build',action='store_true')
    section=parser.add_mutually_exclusive_group()
    section.add_argument('--kernel-only',action='store_true')
    section.add_argument('--routes-only',action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    if not __debug__:
        parser.error('Workload qualification requires Python assertions enabled.')
    if not args.routes_only:
        kernel_workloads(root,os.environ['DOTNET'],args.no_build)
    if not args.kernel_only:
        failures=[]
        for days in (90,365):
            preview=subprocess.run([sys.executable,str(root/'tools/run_preview.py'),'--check','--gas-swing-workload-check',
                                    '--gas-swing-workload-days',str(days),'--pg-bin',os.environ['PG_BIN'],'--dotnet',os.environ['DOTNET'],
                                    *(['--no-build'] if args.no_build or days==365 and not failures else [])],cwd=root)
            if preview.returncode:
                failures.append(days)
        if failures:
            raise SystemExit(f'Qualification failed for the following ordinary host workloads: {failures}')
