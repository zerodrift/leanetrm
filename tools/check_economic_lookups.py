"""Economic-key retention and lookup checks on an owned synthetic installation."""
import json
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking


def run_checks(url, query, manifest, client=None):
    c = client or PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    future_context = {**context, 'convention': 'fixed_total_cash_execution_pnl_v1'}
    futures = c.call('futures/catalog', future_context)
    member = futures['contracts'][0]
    c.call('profile',{'profile':'viewer'})
    market=c.call('evaluate',{'snapshotId':frame['id'],'curve':member['curve']})
    assert next(p for p in market['points'] if p['contractId']==member['id'])['instrumentId']==member['instrumentId']
    c.call('profile',{'profile':'trader'})
    future_body = {**future_context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': futures['books'][0]['id'],
                   'curve': member['curve'], 'contractId': member['id'],
                   'lots': '2', 'executionPrice': '3'}
    previous = int(query("SELECT count(*) FROM trading.futures_revision_markets;"))
    first = c.call('futures/book', future_body)
    second = c.call('futures/book', {**future_body, 'operationId': str(uuid4()), 'lots': '-1', 'executionPrice': '3.1'})
    assert member['instrumentId'] == first['instrumentId'] == second['instrumentId']
    assert query("SELECT count(*) FROM trading.futures_sides;") == '0'
    assert query("SELECT count(*) FROM trading.instrument_sides;") == '0'
    assert query("SELECT count(*) FROM trading.futures_contract_references WHERE instrument_id='"+first['instrumentId']+"';") == '1'
    assert int(query("SELECT count(*) FROM trading.futures_revision_markets;")) == previous+2
    assert query("SELECT bool_and(f.instrument_id=security.futures_contract_id(f.reference_catalog_id,f.reference_revision_id,f.series_code)) FROM trading.futures_contract_references f;") == 't'
    assert query("SELECT array_agg(a.attname ORDER BY k.n)::text FROM pg_constraint c CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY k(attnum,n) JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum WHERE c.conrelid='ref_data.futures_series'::regclass AND c.contype='p';") == '{catalog_id,revision_id,code}'
    print('PASS curve and trade use one futures contract; exact-series primary key lookup creates no side or wrapper definition', flush=True)
    target = first['tradeId']
    saved = query(f"SELECT row_to_json(m)::text FROM trading.futures_revision_markets m WHERE trade_id='{target}';")
    query(f"ALTER TABLE trading.futures_revision_markets DISABLE TRIGGER immutable_record; DELETE FROM trading.futures_revision_markets WHERE trade_id='{target}'; ALTER TABLE trading.futures_revision_markets ENABLE TRIGGER immutable_record;")
    try:
        error = c.call('futures/holdings',future_context,expected=422)
        assert error['code']=='futures_definition'
        assert c.call('futures/status',{'operationId':future_body['operationId']},expected=422)['code']=='futures_definition'
        assert c.call('futures/book',future_body,expected=422)['code']=='futures_definition'
        bad = json.loads(saved); bad['series_code'] = 'unknown_series'
        bad_text = json.dumps(bad).replace("'","''")
        query("DO $$ BEGIN INSERT INTO trading.futures_revision_markets SELECT * FROM jsonb_populate_record(NULL::trading.futures_revision_markets,'"+bad_text+"'::jsonb); RAISE EXCEPTION 'Expected exact-key rejection'; EXCEPTION WHEN foreign_key_violation THEN NULL; END $$;")
    finally:
        query("INSERT INTO trading.futures_revision_markets SELECT * FROM jsonb_populate_record(NULL::trading.futures_revision_markets,'"+saved.replace("'","''")+"'::jsonb);")
    assert query("SELECT NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('futures_contract_id','retain_futures_market','prepare_futures_binding') AND has_function_privilege('preview_runtime',p.oid,'EXECUTE'));") == 't'
    assert query("SELECT count(*)=2 AND bool_and(relrowsecurity AND relforcerowsecurity) FROM pg_class WHERE oid IN('trading.futures_contract_references'::regclass,'trading.futures_revision_markets'::regclass);") == 't'
    print('PASS missing pricing bindings fail closed; exact-key FKs and runtime boundaries reject invalid associations',flush=True)
    available = [m for m in futures['contracts'] if query("SELECT count(*) FROM trading.instruments WHERE id='"+m['instrumentId']+"';")=='0']
    assert available, 'The synthetic key workload requires an unbooked future.'
    collision = available[0]
    token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                  f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    booking_json = json.dumps(trade_booking()).replace("'","''")
    query(f"""BEGIN;
     INSERT INTO trading.instruments SELECT '{manifest['catalogId']}','{collision['instrumentId']}',min(publication_order),'2020-01-01T00:00:00Z' FROM trading.publications;
     SET SESSION AUTHORIZATION preview_runtime; SELECT security.use_context('{token}');
     DO $$ BEGIN
      PERFORM security.book_futures_with_parties('{manifest['catalogId']}','{uuid4()}','{future_body['bookId']}','{manifest['revisionId']}',
       '{collision['curve']}','{collision['id']}','2020-12-18T16:00:00Z',NULL,1,3,'fixed_total_cash_execution_pnl_v1',NULL,NULL,NULL,'{booking_json}'::jsonb);
      RAISE EXCEPTION 'Expected canonical identity collision rejection';
     EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'futures_identity' THEN RAISE; END IF; END $$;
     ROLLBACK;""")
    print('PASS an occupied canonical UUID cannot bypass the complete exact-series primary key',flush=True)
    catalog = c.call('swaps/catalog', context)
    index = next(i for i in catalog['indices'] if i['curve'] == 'base_forward')
    rule = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 18, 'dayOfMonth': None,
            'dayBasis': 'calendar_days', 'shortMonth': None, 'adjustment': 'unadjusted', 'calendar': None,
            'implementation': 'monthly_payment_date_rules_v1'}
    body = {**context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': catalog['books'][0]['id'],
            'definitionRevisionId': index['definitionRevisionId'],
            'commodity': index['commodity'], 'quantity': '100', 'fixedCoefficient': '-1', 'discounting': False,
            'fixed': {'price': '3', 'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule': rule},
            'floating': {'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'],
                         'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01',
                         'paymentSchedule': rule, 'resetFrequency': 'monthly', 'averagingMethod': 'none'}}
    plain = c.call('swaps/book', body)
    discounted = c.call('swaps/book', {**body, 'operationId': str(uuid4()), 'discounting': True})
    assert plain['instrumentId'] != discounted['instrumentId'], 'Discounting must distinguish instrument identities.'
    repeated = c.call('swaps/book', {**body, 'operationId': str(uuid4()), 'quantity': '200'})
    assert repeated['instrumentId'] == plain['instrumentId'], 'Trade quantity must not distinguish instrument identities.'
    for booked, expected in ((plain, False), (discounted, True)):
        state = c.call('swaps/editor', {'context': context, 'tradeId': booked['tradeId'], 'revision': None})['state']
        assert state['terms']['discounting'] is expected
        assert state['terms']['floating']['settlement'] == body['floating']['settlement'], 'The stored identity keeps its explicit settlement choice.'
    keys = query("SELECT array_agg(a.attname ORDER BY k.n)::text FROM pg_constraint c CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY k(attnum,n) JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum WHERE c.conrelid='trading.swap_floating_sides'::regclass AND c.contype='u' GROUP BY c.oid;").splitlines()
    assert '{catalog_id,identity_hash}' in keys, f'Floating-side identity must be unique within its catalog: {keys}'
    assert query("SELECT NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='trading.swap_floating_sides'::regclass AND conname='swap_floating_economic_key');") == 't', 'The old key omits the selected settlement index.'
    floating = query("SELECT floating_side_id FROM trading.swap_instruments WHERE instrument_id='"+plain['instrumentId']+"';")
    assert query(f"SELECT definition_version='FloatingSide_v8' AND octet_length(identity_hash)=32 AND settlement_mode='curve_target' AND settlement_index_code=index_code AND settlement_projection_policy IS NULL FROM trading.swap_floating_sides WHERE id='{floating}';") == 't'
    spread_body={**body,'operationId':str(uuid4()),'floating':{**body['floating'],'spread':'0.2'}}
    spread_trade=c.call('swaps/book',spread_body)
    assert spread_trade['instrumentId']!=plain['instrumentId'], 'A fixed floating spread distinguishes economic sides.'
    same_spread=c.call('swaps/book',{**spread_body,'operationId':str(uuid4()),'quantity':'200','floating':{**spread_body['floating'],'spread':'0.20'}})
    assert same_spread['instrumentId']==spread_trade['instrumentId'], 'Equal exact spreads reuse the retained side across quantities.'
    explicit_zero=c.call('swaps/book',{**body,'operationId':str(uuid4()),'floating':{**body['floating'],'spread':'0'}})
    assert explicit_zero['instrumentId']==plain['instrumentId'], 'The explicit zero spread preserves the baseline economic lookup.'
    query(f"ALTER TABLE trading.swap_reset_projections DISABLE TRIGGER immutable_record; UPDATE trading.swap_reset_projections SET ordinal=1 WHERE side_id='{floating}'; ALTER TABLE trading.swap_reset_projections ENABLE TRIGGER immutable_record;")
    try:
        error = c.call('swaps/book',{**body,'operationId':str(uuid4())},expected=422)
        assert error['code']=='swap_projection'
    finally:
        query(f"ALTER TABLE trading.swap_reset_projections DISABLE TRIGGER immutable_record; UPDATE trading.swap_reset_projections SET ordinal=0 WHERE side_id='{floating}'; ALTER TABLE trading.swap_reset_projections ENABLE TRIGGER immutable_record;")
    series_before = query('SELECT count(*) FROM market_data.series;')
    later = c.call('swaps/book',{**body,'operationId':str(uuid4()),'floating':{**body['floating'],'paymentSchedule':{**rule,'days':19}}})
    assert later['instrumentId'] != plain['instrumentId']
    assert query('SELECT count(*) FROM market_data.series;')==series_before
    print('PASS floating economic-key lookup excludes projection aliases, validates retained bindings and shares market factors across payment dates',flush=True)
    print('PASS discounting distinguishes instrument keys; equal terms reuse the retained instrument across trade quantities', flush=True)
