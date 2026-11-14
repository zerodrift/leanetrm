"""Independent behavior oracles for booking listed futures options with premium paid up front in the owned exchange
example. Option facts are the public ON/LO specifications (multiplier, premium tick); amounts are computed here."""
import hashlib
import json
import threading
import time
import uuid
from datetime import date, datetime, timedelta
from decimal import Decimal
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from preview_client import PreviewClient

CONVENTION = 'futures_option_premium_v1'
# Public specification facts: currency per option contract per unit of premium, and the premium tick.
SPECIFICATION = {'nymex_on': (Decimal('10000'), Decimal('0.001')), 'nymex_lo': (Decimal('1000'), Decimal('0.01'))}



def side_lots(lots):
    """A signed lot count as the booking states it: a side and an unsigned whole number of lots."""
    return {'side': 'sell' if lots.startswith('-') else 'buy', 'lots': lots.lstrip('-')}

def run_checks(url, query, manifest):
    base = PreviewClient(url); base.bootstrap()
    c = PreviewClient(url, 'exchange'); c.opener = base.opener; c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2026-10-16'))
    context = {'snapshotId': frame['id'], 'convention': CONVENTION, 'knowledgeMode': 'current'}

    def check(name, condition, actual=None):
        assert condition, f'{name}: {actual}'
        print('PASS ' + name, flush=True)

    catalog = c.call('options/catalog', context)
    options = catalog['options']
    reference = (manifest['referenceCatalogId'], manifest['referenceRevisionId'])
    check('the option catalog lists every unexpired listed ON and LO series with its exact-version instrument identity',
          len(options) == 141 and all(o['instrumentId'] == str(uuid.UUID(hashlib.sha256(
              f"futures_option_v1:{reference[0]}:{reference[1]}:{o['series']}".encode()).hexdigest()[:32])) for o in options))
    check('each listed series carries its product multiplier and premium tick from reference data',
          all((Decimal(o['multiplier']), Decimal(o['premiumTick'])) == SPECIFICATION[o['product']] for o in options))
    on = next(o for o in options if o['product'] == 'nymex_on' and o['right'] == 'call')
    lo = next(o for o in options if o['product'] == 'nymex_lo' and o['right'] == 'put')
    parties = {'legalEntityId': catalog['legalEntities'][0]['id'], 'legalEntityRevision': catalog['legalEntities'][0]['revision'],
               'counterpartyId': catalog['counterparties'][0]['id'], 'counterpartyRevision': catalog['counterparties'][0]['revision']}
    book = catalog['books'][0]['id']

    def body(option, lots, premium, **changes):
        return {'operationId': str(uuid4()), **context, 'bookId': book, 'series': option['series'], 'definitionRevisionId': option['definitionRevisionId'], **side_lots(lots), 'premium': premium,
                'premiumPaymentDate': '2026-10-19', 'premiumStyle': 'premium_paid_up_front', 'memo': 'Synthetic option booking', **parties, **changes}

    def stored(trade):
        return query(f"""SELECT concat_ws('|',trim_scale(r.lots),trim_scale(r.premium),r.premium_style,o.series_code,trim_scale(r.premium_quantity),p.payment_date,
            cur.display_code,r.option_instrument_id,r.premium_instrument_id,b.booker_login,b.trade_date=(now() AT TIME ZONE 'UTC')::date,r.memo,
            r.legal_entity_id,r.counterparty_id)
            FROM trading.futures_option_trade_revisions r JOIN trading.futures_option_references o ON(o.catalog_id,o.instrument_id)=(r.catalog_id,r.option_instrument_id)
            JOIN trading.cash_payment_instruments p ON(p.catalog_id,p.instrument_id)=(r.catalog_id,r.premium_instrument_id)
            JOIN ref_data.currencies cur ON(cur.catalog_id,cur.revision_id,cur.code)=(p.reference_catalog_id,p.reference_revision_id,p.currency_code)
            JOIN trading.booking_facts b ON(b.catalog_id,b.trade_id)=(r.catalog_id,r.trade_id)
            WHERE r.catalog_id='{manifest['catalogId']}' AND r.trade_id='{trade}';""")

    def rows():
        return query(f"SELECT concat_ws('|',(SELECT count(*) FROM trading.trades WHERE catalog_id='{manifest['catalogId']}'),"
                     f"(SELECT count(*) FROM trading.instruments WHERE catalog_id='{manifest['catalogId']}'),"
                     f"(SELECT count(*) FROM trading.futures_option_trade_revisions WHERE catalog_id='{manifest['catalogId']}'));")

    def cash(premium, product, lots):
        """The premium cash the trade holds, in currency units: premium × multiplier × lots, negative (paid) for a long holding."""
        return format((-Decimal(premium) * SPECIFICATION[product][0] * lots).normalize(), 'f')

    def cash_instrument(currency, day):
        return str(uuid.UUID(hashlib.sha256(f"cash_payment_v1:{reference[0]}:{reference[1]}:{currency}:{day}".encode()).hexdigest()[:32]))

    long_call = body(on, '2', '0.125')
    booked = c.call('options/book', long_call)
    held = stored(booked['tradeId'])
    check('a long ON call books its exact option instrument and pays premium × multiplier × lots in the unit USD cash instrument of the booked date',
          booked['optionInstrumentId'] == on['instrumentId'] and booked['premiumInstrumentId'] == cash_instrument('usd', '2026-10-19') and
          held == '|'.join(['2', '0.125', 'premium_paid_up_front', on['series'], cash('0.125', 'nymex_on', 2),
                            '2026-10-19', 'USD', booked['optionInstrumentId'], booked['premiumInstrumentId'], 'trader', 't',
                            'Synthetic option booking', parties['legalEntityId'], parties['counterpartyId']]), held)
    short_put = body(lo, '-3', '1.25')
    sold = c.call('options/book', short_put)
    held = stored(sold['tradeId'])
    check('a short LO put receives its premium in the same unit cash instrument, at a positive quantity',
          held.split('|')[:7] == ['-3', '1.25', 'premium_paid_up_front', lo['series'], cash('1.25', 'nymex_lo', -3), '2026-10-19', 'USD']
          and sold['premiumInstrumentId'] == booked['premiumInstrumentId'], held)

    before = rows()
    check('retrying an operation returns its first outcome and records nothing new',
          c.call('options/book', long_call) == booked and rows() == before)
    check('the status of an operation is its first outcome; an unknown operation is not found',
          c.call('options/status', {'operationId': long_call['operationId']}) == booked and
          c.call('options/status', {'operationId': str(uuid4())}) == {'found': False})
    check('an operation retried with different terms is refused',
          c.call('options/book', {**long_call, 'lots': '3'}, expected=409)['code'] == 'option_conflict' and rows() == before)

    def refused(changes, code, expected=422):
        return c.call('options/book', {**body(on, '1', '0.125'), **changes}, expected=expected)['code'] == code
    margined = c.call('options/book', {**body(on, '1', '0.125'), 'premiumStyle': 'futures_style_margin'}, expected=422)
    check('futures-style margined premium is unsupported, and the refusal says only premium paid up front is bookable',
          margined['code'] == 'option_unsupported' and 'paid up front' in margined['message'] and 'margined' in margined['message'].lower(), margined)
    check('an unknown premium style is refused', refused({'premiumStyle': 'premium_on_expiry'}, 'option_booking_input'))
    check('a missing premium payment date is refused', refused({'premiumPaymentDate': None}, 'option_booking_input'))
    check('zero lots are refused', refused({'lots': '0'}, 'option_booking_input'))
    check('fractional lots are refused', refused({'lots': '1.5'}, 'option_booking_input'))
    check('lots are an unsigned whole number: the side states the direction, and a signed count is refused',
          refused({'lots': '-1'}, 'option_booking_input') and refused({'lots': '+1'}, 'option_booking_input'))
    check('a missing or unknown side is refused', refused({'side': None}, 'option_booking_input') and refused({'side': 'short'}, 'option_booking_input'))
    check('a negative premium is refused', refused({'premium': '-0.125'}, 'option_booking_input'))
    check('a premium off the product premium tick is refused', refused({'premium': '0.1255'}, 'option_booking_input'))
    early = c.call('options/book', {**body(on, '1', '0.125'), 'premiumPaymentDate': '2026-10-15'}, expected=422)
    check('a premium payment date before the UTC date of the trade\'s execution is refused, and the refusal states the side, '
          'unsigned whole lots and payment date the booking needs', early['code'] == 'option_booking_input' and
          'a side and a whole number of lots' in early['message'] and 'on or after the execution date' in early['message'] and 'signed' not in early['message'], early)
    check('server-owned execution time cannot be supplied', refused({'executedAt': frame['at']}, 'trade_booking_read_only'))
    check('unmapped request members are refused', c.call('options/book', body(on, '1', '0.125', strike='3.25'), expected=400)['code'] == 'request_format')
    check('nothing is recorded for refused bookings', rows() == before)

    c.call('profile', {'profile': 'risk'})
    c.call('options/book', body(on, '1', '0.125'), expected=403)
    check('a user without booking permission is refused and records nothing', rows() == before)
    c.call('profile', {'profile': 'trader'})

    # The authorized operation itself refuses execution at or after the option's expiry instant, and premium paid up front on a
    # product whose own premium style is futures-style margin.
    def raw(series, valuation, code, setup=''):
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'https://exchange-preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        query(f"""BEGIN;{setup}SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
          DO $$ BEGIN
            PERFORM security.book_futures_option('{manifest['catalogId']}','{uuid4()}','{book}','{manifest['revisionId']}','{series}',1,1.25,'2026-10-19',
              'premium_paid_up_front','{valuation}',NULL,'{CONVENTION}',NULL,NULL,NULL,'{json.dumps(parties)}'::jsonb);
            RAISE EXCEPTION 'Expected rejection';
          EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'{code}' THEN RAISE;END IF;END $$;ROLLBACK;""")
    expiry = query(f"SELECT expiry_at FROM ref_data.futures_option_series WHERE (catalog_id,revision_id,code)=('{reference[0]}','{reference[1]}','{lo['series']}');")
    raw(lo['series'], expiry, 'option_booking_input')
    check('the booking operation refuses execution at the option expiry instant', rows() == before)
    raw(lo['series'], frame['at'], 'option_product_margined',
        "ALTER TABLE ref_data.futures_option_products DISABLE TRIGGER USER;"
        f"UPDATE ref_data.futures_option_products SET premium_style='futures_style_margin' WHERE (catalog_id,revision_id,code)=('{reference[0]}','{reference[1]}','nymex_lo');"
        "ALTER TABLE ref_data.futures_option_products ENABLE TRIGGER USER;")
    check('a product whose own premium is futures-style margined is refused with its own code, not as an unsupported request', rows() == before)

    # Booking reads the option definition under the trader's own market and reference read authority.
    def withdrawn(table, column, catalog, work):
        held = query(f"SELECT coalesce(json_agg(s),'[]')::text FROM security.{table} s WHERE {column}='{catalog}';")
        query(f"DELETE FROM security.{table} WHERE {column}='{catalog}';")
        try:
            return work()
        finally:
            query(f"INSERT INTO security.{table} SELECT * FROM json_populate_recordset(NULL::security.{table},'" + held.replace("'", "''") + "'::json);")
    seen, missing, replayed = withdrawn('reference_catalog_scopes', 'catalog_id', reference[0], lambda: (
        c.call('options/book', body(lo, '1', '1.25'), expected=403), c.call('options/book', body(lo, '1', '1.25', series='nymex_lo_29991201_p1.00'), expected=403),
        c.call('options/book', long_call, expected=403)))
    check('withdrawn reference scope denies the exact source without disclosing whether a series exists',
          seen == missing and seen['code'] == 'access_denied' and rows() == before, (seen, missing))
    check('an accepted booking cannot be replayed after reference authority is withdrawn', replayed == seen, replayed)
    check('once the scope is restored the same retry returns its first outcome', c.call('options/book', long_call) == booked and rows() == before)
    check('an authorized request for a nonexistent series is refused',
          c.call('options/book', body(lo, '1', '1.25', series='nymex_lo_29991201_p1.00'), expected=422)['code'] == 'option_definition')
    withdrawn('market_catalog_scopes', 'catalog_id', manifest['catalogId'], lambda: c.call('options/book', body(lo, '1', '1.25'), expected=403))
    check('a trader without market read access cannot book', rows() == before)
    return options


# A tool result may hold at most this many bytes (RuntimeSet.ToolResultBytes); the catalog keeps a quarter of it free.
TOOL_RESULT_BYTES, MARGIN = 64 * 1024, Decimal('0.75')


def check_agent_catalog(url, query, manifest, fake):
    """The page assistant's futures option catalog in the exchange example: one turn that finds each option product's listed
    expiry dates in the futures_option_series reference table, then reads each product and expiry, each equal to that
    product's series of that expiry in the options/catalog route at the turn's frame and within the tool-result bound with a
    stated margin, and refuses a missing input, an unknown product and an expiry date the product does not list."""
    from assistant_fixture import Fixture
    from fake_model_provider import recording, stream, tool_use
    base = PreviewClient(url); base.bootstrap()
    c = PreviewClient(url, 'exchange'); c.opener = base.opener; c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2026-10-16'))
    stamp = uuid4().hex[:8]
    c.call('profile', {'profile': 'admin'})
    policies=c.call('settings/operating-policies')['policies']
    assert {p['kind'] for p in policies}=={'assistant','access'} and all(
        p['current']['version']==1 and p['current']['origin']=='carried_forward' for p in policies)
    assert query(f"SELECT count(*) FROM security.operating_policy_versions WHERE tenant_id='{manifest['tenantId']}' AND workspace_id='{manifest['workspaceId']}';")=='2'
    print('PASS the exchange workspace has its own explicitly provisioned assistant and access policies before agent admission',flush=True)
    administrator = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://exchange-preview.invalid' AND subject='admin';")
    fixture = Fixture(c, query, manifest, administrator, stamp, 'option catalog')
    fixture.qualify()
    model_name = f'model-option-catalog-{stamp}'
    agent = fixture.publish(fixture.save(fixture.create('option catalog'), fixture.model(model_name),
                                         tools=('futures_option_catalog', 'read_reference_data'), toolSteps=16))
    c.call('profile', {'profile': 'trader'})
    c.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': agent['id'], 'version': 1})
    # Each product's expiry dates with a series unexpired at the frame, selected here in SQL independently of the tool.
    R = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    listed = [tuple(row.split('|')) for row in query(
        f"SELECT DISTINCT product_code||'|'||last_trading_date FROM ref_data.futures_option_series WHERE (catalog_id,revision_id)=({R}) "
        f"AND expiry_at>'{frame['at']}'::timestamptz ORDER BY 1;").split()]
    products = sorted({product for product, _ in listed})
    assert products == ['nymex_lo', 'nymex_on'] and len(listed) == 10, listed
    discovered = {f'toolu_series_{product}': {'table': 'futures_option_series', 'search': product} for product in products}
    reads = {f"toolu_{product}_{expiry.replace('-', '')}": {'product': product, 'expiry': expiry} for product, expiry in listed}
    refusals = {'toolu_missing': {'product': 'nymex_on'}, 'toolu_unknown': {'product': 'nymex_unlisted', 'expiry': listed[0][1]},
                'toolu_unlisted': {'product': 'nymex_on', 'expiry': '2026-11-25'}}
    fake.respond(stream(tool_use(model_name, [(call, 'read_reference_data', body) for call, body in discovered.items()]
                                 + [(call, 'futures_option_catalog', body) for call, body in {**reads, **refusals}.items()])),
                 stream(recording(model_name, text='These are the listed options.')))
    since = len(fake.received)
    turn = c.call('assistant/turns/ask', {'operationId': str(uuid4()), 'conversationId': None, 'agentId': agent['id'], 'version': 1,
        'question': 'Which options can I book?', 'pageReport': {'page': 'curves', 'selection': {'view': 'curves', 'curve': None}},
        'knowledge': {'mode': 'current', 'contextId': None, 'frameId': frame['id']}})['turn']
    deadline = time.monotonic() + 60
    while (current := next(t for t in c.call(f"assistant/conversations/{turn['conversationId']}?after=0")['turns'] if t['id'] == turn['id']))['state'] not in (
            'completed', 'failed', 'cancelled', 'interrupted'):
        assert time.monotonic() < deadline, current
        time.sleep(.05)
    contents = {b['tool_use_id']: b['content'] for r in fake.received[since:] for m in json.loads(r['body'])['messages'] if m['role'] == 'user'
                for b in m['content'] if b['type'] == 'tool_result'}
    assert current['state'] == 'completed', current
    found = {call: json.loads(contents[call]) for call in discovered}

    def expiries(read, product):
        """The expiry dates a reference read lists for the product's series unexpired at the frame."""
        return sorted({(row['product_code'], row['last_trading_date']) for row in read['result']['rows'] if row['product_code'] == product
                       and datetime.fromisoformat(row['expiry_at']) > datetime.fromisoformat(frame['at'])})
    assert all(read.get('operation') == 'reference_data.table_v1' and expiries(read, discovered[call]['search']) == [
        pair for pair in listed if pair[0] == discovered[call]['search']] for call, read in found.items()), \
        {call: read.get('code', read.get('operation')) for call, read in found.items()}
    print("PASS the futures_option_series reference table, searched by product, lists each product's expiry dates the catalog reads", flush=True)
    page = c.call('options/catalog', {'snapshotId': frame['id'], 'convention': CONVENTION, 'knowledgeMode': 'current'})
    series = {row['code']: row['last_trading_date'] for row in json.loads(query(
        f"SELECT coalesce(json_agg(json_build_object('code',code,'last_trading_date',last_trading_date)),'[]')::text FROM ref_data.futures_option_series WHERE (catalog_id,revision_id)=({R});"))}
    results = {call: json.loads(contents[call]) for call in reads}
    assert len(page['options']) == 141 and all(
        read.get('operation') == 'trades.futures_option_catalog_v2' and read['result'] == {'convention': CONVENTION, 'options': [
            o for o in page['options'] if o['product'] == body['product'] and series[o['series']] == body['expiry']]}
        and read['result']['options'] for body, read in zip(reads.values(), results.values())) \
        and sum(len(read['result']['options']) for read in results.values()) == 141, {call: read.get('code', read.get('operation')) for call, read in results.items()}
    print("PASS the agent's futures option catalog equals options/catalog's series of each product and expiry at the turn's frame, together all 141", flush=True)
    # Each read reports the turn's pinned knowledge, cutoff and As of frame, and records the Blotter's accesses.
    pinned = {'knowledge': 'working_data', 'contextId': current['context']['contextId'], 'cutoff': current['context']['cutoff']}
    assert all({k: v for k, v in read['context'].items() if k != 'frame'} == pinned and read['context']['frame']['id'] == frame['id']
               for read in results.values()), {call: read['context'] for call, read in results.items()}
    catalog, references = str(uuid.UUID(manifest['catalogId'])), str(uuid.UUID(manifest['referenceCatalogId']))
    blotter = [{'access': 'market_access', 'permission': 'ReadMarketData', 'market': catalog, 'reference': None},
               {'access': 'reference_access', 'permission': 'ReadMarketData', 'market': None, 'reference': references},
               {'access': 'trading_access', 'permission': 'ReadTrades', 'market': catalog, 'reference': None}]
    recorded = json.loads(query(
        "SELECT coalesce(json_agg(json_build_object('operation',s.operation,'code',s.code,'access',(SELECT coalesce(json_agg(json_build_object("
        "'access',a.access,'permission',a.permission,'market',a.market_catalog_id,'reference',a.reference_catalog_id) ORDER BY a.access),'[]') "
        "FROM assistant.tool_read_access a WHERE (a.turn_id,a.step)=(s.turn_id,s.ordinal))) ORDER BY s.ordinal),'[]')::text "
        f"FROM assistant.tool_steps s WHERE s.turn_id='{turn['id']}' AND s.tool='futures_option_catalog' AND s.code IS NULL;"))
    assert len(recorded) == len(reads) and all(step['operation'] == 'trades.futures_option_catalog_v2' and step['access'] == blotter for step in recorded), recorded
    print("PASS each catalog read reports the turn's pinned knowledge, cutoff and As of frame, under the Blotter's accesses", flush=True)
    sizes = {call: len(contents[call].encode()) for call in reads}
    assert all(size <= TOOL_RESULT_BYTES * MARGIN for size in sizes.values()), sizes
    print(f"PASS each product and expiry's catalog result is within {MARGIN:%} of the {TOOL_RESULT_BYTES}-byte tool-result bound, in bytes: "
          + ', '.join(f"{body['product']} {body['expiry']} {sizes[call]}" for call, body in reads.items()), flush=True)
    refused = {call: json.loads(contents[call])['code'] for call in refusals}
    assert refused == {'toolu_missing': 'tool_arguments_invalid', 'toolu_unknown': 'option_definition', 'toolu_unlisted': 'option_definition'}, refused
    print('PASS the catalog refuses a missing input, an unknown product and an expiry date the product does not list, with no default', flush=True)


def run_lifecycle_checks(url, query, manifest):
    """Qualified shared exercise/assignment and current authority over real booked ON rights.

    Historical expiry rules use separately labelled synthetic reference bundles in
    check_lifecycle_option_expiries.py, rather than changing accepted series.
    """
    base = PreviewClient(url); base.bootstrap()
    c = PreviewClient(url, 'exchange'); c.opener = base.opener; c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2026-10-16'))
    context = {'snapshotId': frame['id'], 'convention': CONVENTION, 'knowledgeMode': 'current'}
    catalog = c.call('options/catalog', context)
    catalog_id, reference = manifest['catalogId'], (manifest['referenceCatalogId'], manifest['referenceRevisionId'])
    call = next(o for o in catalog['options'] if o['product'] == 'nymex_on' and o['right'] == 'call')
    put = next(o for o in catalog['options'] if o['product'] == 'nymex_on' and o['right'] == 'put' and o['underlyingSeries'] == call['underlyingSeries'])
    parties = {'legalEntityId': catalog['legalEntities'][0]['id'], 'legalEntityRevision': catalog['legalEntities'][0]['revision'],
               'counterpartyId': catalog['counterparties'][-1]['id'], 'counterpartyRevision': catalog['counterparties'][-1]['revision']}
    book = catalog['books'][-1]['id']
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings'])p WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['ReadTrades','ReadGasOwnedHoldings'])p WHERE permission='WriteMarketDefinitions' ON CONFLICT DO NOTHING;")
    from lifecycle_option_client import LifecycleOptionClient
    c=LifecycleOptionClient(c,query,manifest,issuer='https://exchange-preview.invalid',context=context,as_of=frame['at'],business_date='2026-10-16',
        from_at='2026-10-01T00:00:00Z',until_at='2030-01-01T00:00:00Z',mark_curve='nymex_ng',carrying='currency_curve_v1')

    output = []

    def check(name, condition, actual=None):
        assert condition, f'{name}: {actual if actual is not None else output}'
        print('PASS ' + name, flush=True)

    def booked(option, lots):
        return c.call('options/book', {'operationId': str(uuid4()), **context, 'bookId': book, 'series': option['series'], 'definitionRevisionId': option['definitionRevisionId'], **side_lots(lots),
                                       'premium': '0.125', 'premiumPaymentDate': '2026-10-19', 'premiumStyle': 'premium_paid_up_front', **parties})['tradeId']

    def event(path, trade, expected=200, **fields):
        return c.call('options/' + path, {'operationId': str(uuid4()), **context, 'tradeId': trade, **fields}, expected=expected)

    def future(trade):
        """The linked futures trade as booked: lots, execution price, book, parties, series, booker and execution time."""
        return query(f"""SELECT concat_ws('|',trim_scale(r.lots),trim_scale(r.execution_price),r.book_id,r.legal_entity_id,r.counterparty_id,
            f.series_code,b.booker_login,r.execution_at=e.business_at)
            FROM trading.trade_revisions r JOIN trading.futures_contract_references f ON(f.catalog_id,f.instrument_id)=(r.catalog_id,r.instrument_id)
            JOIN trading.booking_facts b ON(b.catalog_id,b.trade_id)=(r.catalog_id,r.trade_id)
            JOIN trading.futures_option_events e ON(e.catalog_id,e.futures_trade_id)=(r.catalog_id,r.trade_id)
            WHERE r.catalog_id='{catalog_id}' AND r.trade_id='{trade}';""")

    def counts():
        return query(f"SELECT concat_ws('|',(SELECT count(*) FROM trading.trades WHERE catalog_id='{catalog_id}'),"
                     f"(SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog_id}'));")

    def expected_future(lots, option):
        return '|'.join([lots, option['strike'], book, parties['legalEntityId'], parties['counterpartyId'], option['underlyingSeries'], 'trader', 't'])

    def holding(trade, knowledge):
        return c.call('options/holding', {'context': {**context, **knowledge}, 'tradeId': trade})

    # Early exercise and its retry.
    long_call = booked(call, '5')
    before_event = c.call('options/catalog', context)['context']['contextId']
    exercise = {'operationId': str(uuid4()), **context, 'tradeId': long_call, 'lots': '3'}
    exercised = c.call('options/exercise', exercise)
    check('a partial early exercise of 3 of 5 lots books 3 long NG futures at the strike in the option\'s book and parties and leaves 2 open',
          future(exercised['futuresTradeId']) == expected_future('3', call) and exercised['remainingLots'] == '2' and
          holding(long_call, {})['openLots'] == '2', (future(exercised['futuresTradeId']), exercised))
    before = counts()
    check('retrying an exercise request returns its first outcome and the same futures trade, recording nothing',
          c.call('options/exercise', exercise) == exercised and counts() == before)
    acknowledgement=str(uuid4());ack=c.acknowledge(exercise['operationId'],acknowledgement)
    check('a new command acknowledging the same exercise returns its own operation identity and the existing native event',
          ack=={**exercised,'operationId':acknowledgement,'acknowledgedOperationId':exercise['operationId'],'semanticAcknowledgement':True} and counts()==before,ack)
    check('an exercise request retried with different lots is refused',
          c.call('options/exercise', {**exercise, 'lots': '2'}, expected=409)['code'] == 'lifecycle_command_conflict' and counts() == before)
    check('exercising more than the open lots is refused', event('exercise', long_call, 422, lots='3')['code'] == 'option_lots_exceeded' and counts() == before)
    for lots in ['0', '-1', '1.5']:
        assert event('exercise', long_call, 422, lots=lots)['code'] == 'option_lots', lots
    check('zero, negative and fractional event lots are refused', counts() == before)
    retained = holding(long_call, {'knowledgeMode': 'retained', 'contextId': before_event})
    check('a historical read before the exercise shows the holding\'s five open lots and no events',
          retained['openLots'] == '5' and retained['events'] == [] and len(holding(long_call, {})['events']) == 1, retained)

    # Assignment of a short call books a short future; a holding's side decides which event applies.
    short_call = booked(call, '-2')
    assigned = event('assignment', short_call, lots='1')
    check('assignment of a short call books a short future at the strike', future(assigned['futuresTradeId']) == expected_future('-1', call), future(assigned['futuresTradeId']))
    before = counts()
    check('a short holding cannot be exercised and a long holding cannot be assigned',
          event('exercise', short_call, 422, lots='1')['code'] == 'option_event_side' and
          event('assignment', long_call, 422, lots='1')['code'] == 'option_event_side' and counts() == before)
    check('ON does not accept contrary instructions, as its product reference row states',
          event('instruction', long_call, 422, instruction='abandon')['code'] == 'option_instruction_closed' and
          event('instruction', long_call, 422, instruction='exercise')['code'] == 'option_instruction_closed' and counts() == before)
    check('an unknown instruction is refused', event('instruction', long_call, 422, instruction='hold')['code'] == 'lifecycle_input' and counts() == before)
    check('expiry before the option\'s expiry instant is refused, writes nothing and leaves the holding open',
          event('expiry', long_call, 422)['code'] == 'option_expiry_not_reached' and counts() == before and holding(long_call, {})['openLots'] == '2')
    missing = event('exercise', str(uuid4()), 422, lots='1')
    check('a nonexistent trade is refused', missing['code'] == 'lifecycle_option_trade', missing)

    # Authority mirrors booking.
    c.call('profile', {'profile': 'risk'})
    c.call('options/exercise', {'operationId': str(uuid4()), **context, 'tradeId': long_call, 'lots': '1'}, expected=403)
    c.call('profile', {'profile': 'trader'})
    check('a user without booking permission cannot exercise and nothing is recorded', counts() == before)

    def withdrawn(table, column, catalog, work):
        held = query(f"SELECT coalesce(json_agg(s),'[]')::text FROM security.{table} s WHERE {column}='{catalog}';")
        query(f"DELETE FROM security.{table} WHERE {column}='{catalog}';")
        try:
            return work()
        finally:
            query(f"INSERT INTO security.{table} SELECT * FROM json_populate_recordset(NULL::security.{table},'" + held.replace("'", "''") + "'::json);")
    unseen = withdrawn('reference_catalog_scopes', 'catalog_id', reference[0], lambda: event('exercise', long_call, 403, lots='1'))
    check('a trader whose reference scope is withdrawn cannot exercise through the shared action',
          unseen['code']=='gas_owned_denied' and counts() == before, unseen)
    retried = withdrawn('reference_catalog_scopes', 'catalog_id', reference[0], lambda: c.call('options/exercise', exercise, expected=403))
    check('a retried exercise does not return its recorded outcome once the trader\'s reference scope is withdrawn', retried['code']=='lifecycle_denied', retried)
    check('a holding read without its context is an input error',
          c.call('options/holding', {'context': None, 'tradeId': long_call}, expected=422)['code'] == 'option_context')
    withdrawn('market_catalog_scopes', 'catalog_id', catalog_id, lambda: event('exercise', long_call, 403, lots='1'))
    check('a trader without market read access cannot exercise', counts() == before)

    # Both reviews see the same single open right. Their distinct observed
    # event identities make these competing exercises, rather than a retry or
    # acknowledgement of one fact. The native publication fence must admit one.
    raced=booked(call,'1')
    requests=[]
    for _ in range(2):
        command=dict(operationId=str(uuid4()),**context,tradeId=raced,lots='1')
        request=c.request('exercise',command)
        request['previewHash']=c.client.call('lifecycle/preview',request)['hash']
        requests.append(request)
    start=threading.Barrier(2)
    def compete(request):
        start.wait(timeout=10)
        native={**request,'context':dict(contextId=request['context']['contextId'],asOf=c.as_of,businessDate=c.business_date)}
        try:return ('accepted',json.loads(c.native(f"SELECT lifecycle.execute_option_action('{catalog_id}',{c.lit(native)})::text;")))
        except RuntimeError as error:
            if 'lifecycle_event_revision' not in str(error):raise
            return ('refused',{'code':'lifecycle_event_revision'})
    with ThreadPoolExecutor(max_workers=2) as sessions:
        raced_results=list(sessions.map(compete,requests))
    accepted=[value for status,value in raced_results if status=='accepted']
    refused=[value for status,value in raced_results if status=='refused']
    check('two reviewed exercises competing for one lot create one event and linked future',
          len(accepted)==len(refused)==1 and refused[0]['code']=='lifecycle_event_revision' and
          holding(raced,{})['openLots']=='0' and query(f"SELECT count(*) FROM trading.futures_option_events e JOIN settlement_data.futures_margin_adoptions a ON(a.catalog_id,a.trade_id,a.publication_order)=(e.catalog_id,e.futures_trade_id,e.publication_order) WHERE(e.catalog_id,e.trade_id)=('{catalog_id}','{raced}');")=='1',raced_results)

    assert query("SELECT NOT has_function_privilege('preview_runtime','security.record_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz)','EXECUTE') AND NOT has_function_privilege('preview_runtime','security.process_futures_option_expiry(uuid,uuid,uuid,uuid,uuid[])','EXECUTE');")=='t'
    print('PASS shared option lifecycle: explicit v2 delivery enrolment, qualified events, first outcomes and current authority; retired direct producers unavailable',flush=True)
