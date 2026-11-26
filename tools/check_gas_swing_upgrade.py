"""Inspect the actual native contract between the unpublished swing migrations."""

WRITER = 'security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'
PUBLISHED = '1918e843d3f761ae794312ab0ce254df33ef0197aedd8004e1893454bc701098'


def owner_only_prefix(query, prefix):
    digest = query(f"SELECT encode(sha256(convert_to(pg_get_functiondef('{WRITER}'::regprocedure),'UTF8')),'hex')")
    exposed = query("""
        WITH callers AS (SELECT unnest(ARRAY['openetrm_runtime','openetrm_risk_worker']) AS role),
        accessible AS (
          SELECT role, p.oid::regprocedure::text AS object FROM callers CROSS JOIN pg_proc p
          JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('security','trading')
          AND p.proname LIKE '%gas_swing%' AND has_function_privilege(role,p.oid,'EXECUTE')
          UNION ALL
          SELECT role, t.oid::regtype::text FROM callers CROSS JOIN pg_type t
          WHERE t.oid=to_regtype('trading.gas_swing_ratio') AND has_type_privilege(role,t.oid,'USAGE')
          UNION ALL
          SELECT role, c.oid::regclass::text FROM callers CROSS JOIN pg_class c
          JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='trading' AND c.relkind='r'
          AND c.relname LIKE 'gas_swing_%' AND (has_any_column_privilege(role,c.oid,'SELECT,INSERT,UPDATE,REFERENCES')
            OR has_table_privilege(role,c.oid,'DELETE,TRUNCATE,TRIGGER'))
        ) SELECT role||' '||object FROM accessible ORDER BY role,object""")
    if digest != PUBLISHED or exposed:
        raise AssertionError(f'{prefix} must retain published writer {PUBLISHED}, found {digest}; accessible swing objects: {exposed}')
    for role in ('openetrm_runtime','openetrm_risk_worker'):
        for function,signature in (('trading.gas_swing_terms','NULL::uuid,NULL::uuid'),
                ('security.gas_swing_write_until','NULL::uuid'),
                ('security.require_gas_swing_binding','NULL::uuid,NULL::uuid,NULL::uuid'),
                ('security.require_gas_swing_receipt','NULL::uuid,NULL::uuid,NULL::timestamptz'),
                ('security.record_gas_swing_elections','NULL::uuid,NULL::jsonb,NULL::timestamptz'),
                ('security.read_gas_swing_elections','NULL::uuid,NULL::uuid,NULL::uuid'),
                ('trading.save_gas_swing','NULL::uuid,NULL::uuid,NULL::uuid,NULL::jsonb,NULL::timestamptz')):
            try:query(f'SET ROLE {role};SELECT {function}({signature});')
            except RuntimeError as error:
                if not any(word in str(error) for word in ('does not exist','permission denied')):raise
            else:raise AssertionError(f'{prefix} allowed {role} to call {function}')
    print(f'PASS {prefix} retains published 0402 composition writer and owner-only swing operations', flush=True)


def resource_cases(query):
    import json
    from pathlib import Path
    fixture = json.loads((Path(__file__).resolve().parents[1]/'tests/fixtures/gas-swing-unit-resource-v2.json').read_text())
    for case in fixture['Cases']:
        origin = f"timestamptz '{fixture['Origin']}'"
        call = f"""SELECT trading.gas_swing_booking_work(
            ARRAY[date '2041-01-01'],ARRAY[{origin}+{case['FirstDeadlineTicks']}*interval '6 hours'],{origin},
            1,0,{case['DailyMaximum']},1,'term',NULL,NULL,NULL,NULL)"""
        try:
            work = json.loads(query(call))
        except RuntimeError as error:
            if case['Refusal'] is None or 'swing_numerical_scope' not in str(error) or case['Refusal'] not in str(error):
                raise
        else:
            if case['Refusal'] is not None or (work['operations'],work['peakBytes']) != (case['Operations'],case['Bytes']):
                raise AssertionError((case,work))
    for days,period,upper,basis,expected in (
        (90,'term',90100,'per_period_v1',(357,3698739,5130080)),
        (365,'delivery_month',1005,'per_day_v1',(1457,59016138,23071872))):
        call=f"""SELECT trading.gas_swing_booking_work(array_agg(date '2041-01-01'+g ORDER BY g),
          array_agg(timestamptz '2040-12-31 18:00 UTC'+g*interval '1 day' ORDER BY g),timestamptz '2040-12-31 12:00 UTC',
          1000,900,1100,100,'{period}',NULL,{upper},'{basis}',2) FROM generate_series(0,{days-1}) g"""
        actual=json.loads(query(call))
        assert (actual['timeSteps'],actual['operations'],actual['peakBytes'])==expected,(days,actual)
        try:query(call.replace('2040-12-31 12:00','2010-12-31 12:00'))
        except RuntimeError as error:
            if 'swing_numerical_scope' not in str(error):raise
        else:raise AssertionError('Admitted a thirty-year first deadline')
    print('PASS native structural work matches the portable operation, byte and daily fan-out fixtures',flush=True)


def install_chain(query, migrations):
    from check_fact_anchor_upgrade import run_checks
    from check_gas_swing import predecessor, period_predecessor, overload_guard
    first, elections, final = migrations
    predecessor(query, first)
    query(first.read_text())
    owner_only_prefix(query,'0403')
    resource_cases(query)
    overload_guard(query,elections,'0404','gas_swing_election_upgrade',['security.require_gas_cashout_binding(uuid,uuid,uuid)'])
    run_checks(query,elections,number='0404',guard='gas_swing_election_upgrade',
               signatures=['security.require_gas_cashout_binding(uuid,uuid,uuid)'])
    query(elections.read_text())
    owner_only_prefix(query,'0404')
    shape = period_predecessor(query,final)
    query(final.read_text())
    return shape


def native_prefixes(query, book, manifest, migrations, base, label, retained=None):
    from copy import deepcopy
    import json
    from uuid import uuid4
    from check_booked_side_programs import capture_context,runtime_query
    from check_gas_exercise import stage_gas_composition
    from check_gas_swing_activation import january

    if retained is None:
        assert query('SELECT count(*) FROM trading.gas_trade_revisions')=='0'
    capture_context(query,manifest,'2020-12-18')
    at='2020-12-18T16:00:00Z'
    def verify(prefix):
        owner_only_prefix(query,prefix)
        body=deepcopy(base)
        body['operationId']=str(uuid4())
        receipt=book(body)
        saved=query(f"SELECT request::text FROM trading.trades WHERE id='{receipt['tradeId']}'").replace("'","''")
        replay=runtime_query(query,manifest,f"SELECT security.book_gas_composition('{manifest['catalogId']}','{saved}'::jsonb,'{at}');")
        assert json.loads(replay.splitlines()[-1])==receipt
        before=query('SELECT count(*) FROM trading.trades')
        proposed=dict(json.loads(saved),operationId=str(uuid4()),swingRights=january(body)['swingRights'])
        try:stage_gas_composition(query,manifest,receipt['instrumentId'],proposed,at)
        except RuntimeError as error:
            if 'gas_composition_input' not in str(error):raise
        else:raise AssertionError(f'{prefix} admitted an expanded native booking')
        assert query('SELECT count(*) FROM trading.trades')==before
        if retained:retained()
        print(f'PASS {label} {prefix} runtime ordinary booking/replay and expanded booking refusal without any trade',flush=True)
    verify('0402')
    for migration in migrations[:2]:
        query(migration.read_text())
        verify(migration.name[:4])
    final=migrations[-1].read_text()
    try:query(final[:final.rindex('COMMIT;')]+"DO $$BEGIN RAISE EXCEPTION 'synthetic rollback';END$$;COMMIT;")
    except RuntimeError as error:
        if 'synthetic rollback' not in str(error):raise
    else:raise AssertionError('final rollback fixture committed')
    verify('rolled-back 0405')
    query(final)
    if retained:retained()


def populated_chain(query, book, manifest, migrations, base):
    """Rehearse the actual chain in a registered, independently seeded 0402 sibling."""
    import json
    from copy import deepcopy
    from uuid import uuid4
    from check_booked_side_programs import capture_context,runtime_query
    from preview_fixture import CURRENCY
    from synthetic_side_calendars import side_calendar_policy

    cat=manifest['catalogId']
    at='2020-12-18T16:00:00Z'
    ordinary=deepcopy(base)
    ordinary.update(operationId=str(uuid4()))
    ordinary['physicalSides'][0].update(deliveryFrom='2021-01-13',deliveryTo='2021-01-16')
    calendar=ordinary['cashSides'][0]['calendarPolicy']['payment']['calendar']
    assert query("SELECT to_regclass('trading.gas_swing_rights') IS NULL")=='t'
    def saved(body):
        receipt=book(body)
        stored=json.loads(query(f"SELECT request::text FROM trading.trades WHERE id='{receipt['tradeId']}'"))
        return stored,receipt
    plain=saved(ordinary)
    binary=saved(dict(ordinary,operationId=str(uuid4()),exerciseRights=[dict(entries=['gas','strike'],
        period='delivery_day',holder='us',settlement='physical',leadHours=6)]))
    charges=saved(dict(ordinary,operationId=str(uuid4()),chargeSides=[dict(code='premium',basis='lump_sum',direction='pay',
        price='2',clockSide='gas',dailyQuantity=None,paymentAt=at,paymentDay=None,
        **{'from':None,'to':None},calendarPolicy=side_calendar_policy(calendar,delivery=True,observation=False,payment=True))]))
    cashout=saved(dict(ordinary,profile='indexed_gas_composition_v1',operationId=str(uuid4()),imbalanceCashouts=[dict(code='balance',profile='daily_index_cashout_v1',
        side='gas',index='synthetic_entry_daily',spreadOwedToUs='0',spreadOwedByUs='0',paymentDay=15,
        statementPolicy='final_statement_balance_v1',statementBasis='daily_imbalance_v1',accountNetting='net_accounts_v1',
        calendarPolicy=side_calendar_policy(calendar,delivery=True,observation=True,payment=True))]))
    capture_context(query,manifest,'2020-12-18')
    payment=dict(operationId=str(uuid4()),tradeId=charges[1]['tradeId'],expectedRevision=1,slot=2,businessKey='synthetic-prefix-cash',
                 paidAt=at,currency=CURRENCY,amount='-1',allocations=[dict(occurrence=0,amount='-1')])
    def native(function,body):
        wire=json.dumps(body).replace("'","''")
        return json.loads(runtime_query(query,manifest,f"SELECT security.{function}('{cat}','{wire}'::jsonb,'{at}');").splitlines()[-1])
    paid=native('record_gas_cash_payment',payment)
    preserved=(plain,binary,charges,cashout)
    ids=','.join("'"+r['instrumentId']+"'" for _,r in preserved)
    def snapshot():
        return query(f"""SELECT to_jsonb(s)::text FROM trading.instrument_side_schedules s WHERE instrument_id IN({ids})
          UNION ALL SELECT to_jsonb(i)::text FROM trading.instruments i WHERE id IN({ids})
          UNION ALL SELECT to_jsonb(g)::text FROM trading.gas_instruments g WHERE instrument_id IN({ids})
          UNION ALL SELECT to_jsonb(m)::text FROM trading.gas_memberships m WHERE instrument_id IN({ids})
          UNION ALL SELECT to_jsonb(t)::text FROM trading.gas_component_terms t WHERE instrument_id IN({ids})
          UNION ALL SELECT to_jsonb(p)::text FROM trading.side_schedule_programs p JOIN trading.instrument_side_schedules s
           USING(catalog_id,schedule_id) WHERE s.instrument_id IN({ids})
          UNION ALL SELECT to_jsonb(h)::text FROM trading.gas_trade_revisions h WHERE instrument_id IN({ids})
          UNION ALL SELECT to_jsonb(s)::text FROM trading.sides s JOIN trading.instrument_side_schedules m
           ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id) WHERE m.instrument_id IN({ids})
          UNION ALL SELECT to_jsonb(p)::text FROM trading.gas_cash_payments p WHERE instrument_id IN({ids})
          UNION ALL SELECT to_jsonb(a)::text FROM trading.gas_cash_payment_allocations a WHERE payment_id='{paid['paymentId']}'
          UNION ALL SELECT trading.gas_exercise_terms('{cat}','{binary[1]['instrumentId']}')::text ORDER BY 1;""")
    before=snapshot()
    def unchanged():
        for body,receipt in preserved:
            assert native('book_gas_composition',body)==receipt
        assert native('record_gas_cash_payment',payment)==paid
        assert snapshot()==before
    native_prefixes(query,book,manifest,migrations,ordinary,'populated',unchanged)
    print('PASS populated 0402 gas, D448, D450 charge/payment and D454 retained native terms, identity, programs and original command receipts survive actual activation',flush=True)
