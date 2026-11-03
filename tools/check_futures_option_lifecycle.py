"""Independent behavior oracles for booking listed futures options with premium paid up front in the owned exchange
example. Option facts are the public ON/LO specifications (multiplier, premium tick); amounts are computed here."""
import hashlib
import json
import threading
import time
import uuid
from datetime import date, timedelta
from decimal import Decimal
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
          len(options) == 61 and all(o['instrumentId'] == str(uuid.UUID(hashlib.sha256(
              f"futures_option_v1:{reference[0]}:{reference[1]}:{o['series']}".encode()).hexdigest()[:32])) for o in options))
    check('each listed series carries its product multiplier and premium tick from reference data',
          all((Decimal(o['multiplier']), Decimal(o['premiumTick'])) == SPECIFICATION[o['product']] for o in options))
    on = next(o for o in options if o['product'] == 'nymex_on' and o['right'] == 'call')
    lo = next(o for o in options if o['product'] == 'nymex_lo' and o['right'] == 'put')
    parties = {'legalEntityId': catalog['legalEntities'][0]['id'], 'legalEntityRevision': catalog['legalEntities'][0]['revision'],
               'counterpartyId': catalog['counterparties'][0]['id'], 'counterpartyRevision': catalog['counterparties'][0]['revision']}
    book = catalog['books'][0]['id']

    def body(option, lots, premium, **changes):
        return {'operationId': str(uuid4()), **context, 'bookId': book, 'series': option['series'], **side_lots(lots), 'premium': premium,
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
        c.call('options/book', body(lo, '1', '1.25'), expected=422), c.call('options/book', body(lo, '1', '1.25', series='nymex_lo_29991201_p1.00'), expected=422),
        c.call('options/book', long_call, expected=422)))
    check('a trader whose reference scope is withdrawn cannot book, and an unseen series is refused exactly as a nonexistent one',
          seen == missing and seen['code'] == 'option_definition' and rows() == before, (seen, missing))
    check('a retried booking whose series the trader can no longer see is refused as unseen, not replayed', replayed == seen, replayed)
    check('once the scope is restored the same retry returns its first outcome', c.call('options/book', long_call) == booked and rows() == before)
    check('a nonexistent series is refused with the same code', c.call('options/book', body(lo, '1', '1.25', series='nymex_lo_29991201_p1.00'), expected=422) == seen)
    withdrawn('market_catalog_scopes', 'catalog_id', manifest['catalogId'], lambda: c.call('options/book', body(lo, '1', '1.25'), expected=403))
    check('a trader without market read access cannot book', rows() == before)
    return options


# A tool result may hold at most this many bytes (RuntimeSet.ToolResultBytes); the catalog keeps a quarter of it free.
TOOL_RESULT_BYTES, MARGIN = 64 * 1024, Decimal('0.75')


def check_agent_catalog(url, query, manifest, fake):
    """The page assistant's futures option catalog in the exchange example: one turn reading every listed series, which
    equals the options/catalog route's series at the turn's frame, within the tool-result bound with a stated margin."""
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
    agent = fixture.publish(fixture.save(fixture.create('option catalog'), fixture.model(model_name), tools=('futures_option_catalog',)))
    c.call('profile', {'profile': 'trader'})
    c.call('assistant/grants/grant', {'operationId': str(uuid4()), 'agentId': agent['id'], 'version': 1})
    fake.respond(stream(tool_use(model_name, [('toolu_option_catalog', 'futures_option_catalog', {})])),
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
    content = next(b['content'] for r in fake.received[since:] for m in json.loads(r['body'])['messages'] if m['role'] == 'user'
                   for b in m['content'] if b['type'] == 'tool_result' and b['tool_use_id'] == 'toolu_option_catalog')
    read = json.loads(content)
    page = c.call('options/catalog', {'snapshotId': frame['id'], 'convention': CONVENTION, 'knowledgeMode': 'current'})
    size = len(content.encode())
    assert current['state'] == 'completed' and read['operation'] == 'trades.futures_option_catalog_v1' and len(page['options']) == 61 \
        and read['result'] == {'convention': CONVENTION, 'options': page['options']}, (current, read.get('operation'))
    print(f"PASS the agent's futures option catalog equals options/catalog for all 61 listed series at the turn's frame", flush=True)
    assert size <= TOOL_RESULT_BYTES * MARGIN, size
    print(f'PASS the 61-series catalog result is {size} bytes, within {MARGIN:%} of the {TOOL_RESULT_BYTES}-byte tool-result bound', flush=True)


def run_lifecycle_checks(url, query, manifest):
    """Exercise, assignment, instructions and expiry of booked ON options. Futures booked from an event are read back
    from the futures booking tables; expiry outcomes on the expiry day's settlement run in rolled-back transactions."""
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

    output = []

    def check(name, condition, actual=None):
        assert condition, f'{name}: {actual if actual is not None else output}'
        print('PASS ' + name, flush=True)

    def booked(option, lots):
        return c.call('options/book', {'operationId': str(uuid4()), **context, 'bookId': book, 'series': option['series'], **side_lots(lots),
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
    check('an exercise request retried with different lots is refused',
          c.call('options/exercise', {**exercise, 'lots': '2'}, expected=409)['code'] == 'option_conflict' and counts() == before)
    check('exercising more than the open lots is refused', event('exercise', long_call, 422, lots='3')['code'] == 'option_lots_exceeded' and counts() == before)
    for lots in ['0', '-1', '1.5']:
        assert event('exercise', long_call, 422, lots=lots)['code'] == 'option_event_input', lots
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
          event('instruction', long_call, 422, instruction='abandon')['code'] == 'option_contrary_instructions_not_accepted' and
          event('instruction', long_call, 422, instruction='exercise')['code'] == 'option_contrary_instructions_not_accepted' and counts() == before)
    check('an unknown instruction is refused', event('instruction', long_call, 422, instruction='hold')['code'] == 'option_event_input' and counts() == before)
    check('expiry before the option\'s expiry instant is refused, writes nothing and leaves the holding open',
          event('expiry', long_call, 422)['code'] == 'option_expiry_not_reached' and counts() == before and holding(long_call, {})['openLots'] == '2')
    missing = event('exercise', str(uuid4()), 422, lots='1')
    check('a nonexistent trade is refused', missing['code'] == 'option_trade', missing)

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
    unseen = withdrawn('reference_catalog_scopes', 'catalog_id', reference[0], lambda: event('exercise', long_call, 422, lots='1'))
    check('a trader whose reference scope is withdrawn cannot exercise, and the trade is refused exactly as a nonexistent one',
          unseen == missing and counts() == before, unseen)
    retried = withdrawn('reference_catalog_scopes', 'catalog_id', reference[0], lambda: c.call('options/exercise', exercise, expected=422))
    check('a retried exercise does not return its recorded outcome once the trader\'s reference scope is withdrawn', retried == missing, retried)
    check('a holding read without its context is an input error',
          c.call('options/holding', {'context': None, 'tradeId': long_call}, expected=422)['code'] == 'option_context')
    withdrawn('market_catalog_scopes', 'catalog_id', catalog_id, lambda: event('exercise', long_call, 403, lots='1'))
    check('a trader without market read access cannot exercise', counts() == before)

    # The authorized SQL operations under the trader's own request context, in transactions that roll back.
    long_put = booked(put, '1')
    settled = counts()
    token = lambda: query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                          f"'https://exchange-preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    R, M = f"'{reference[0]}','{reference[1]}'", f"'{catalog_id}','{manifest['revisionId']}'"

    def market_series(where):
        return query(f"""SELECT s.id FROM market_data.series s JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id)
            WHERE (s.catalog_id,s.revision_id)=({M}) AND k.kind='future' AND {where} ORDER BY k.futures_series_code LIMIT 1;""")
    underlying = market_series(f"k.futures_series_code='{call['underlyingSeries']}'")
    other_month = market_series(f"k.futures_series_code LIKE 'nymex_ng_%' AND k.futures_series_code<>'{call['underlyingSeries']}'")
    expiry_day = query(f"SELECT last_trading_date FROM ref_data.futures_option_series WHERE (catalog_id,revision_id,code)=({R},'{call['series']}');")
    settlement_id, other_id = '7a000000-0000-0000-0000-00000000f008', '7a000000-0000-0000-0000-00000000f009'
    # A past expiry for the ON series held here: the frame's date at the configured 14:30 New York close.
    past_day, past_expiry = '2026-10-16', "TIMESTAMPTZ '2026-10-16T18:30:00Z'"

    def expired(cutoff='NULL'):
        """The held ON series' rows moved to a past expiry, so the recording time is after it."""
        return ("ALTER TABLE ref_data.futures_option_series DISABLE TRIGGER USER;"
                f"UPDATE ref_data.futures_option_series SET last_trading_date='{past_day}',expiry_at={past_expiry},instruction_cutoff_at={cutoff} "
                f"WHERE (catalog_id,revision_id,code) IN(({R},'{call['series']}'),({R},'{put['series']}'));"
                "ALTER TABLE ref_data.futures_option_series ENABLE TRIGGER USER;")

    def settle(price, day=past_day, role='daily_settlement', state='final', series=None, ident=settlement_id):
        """A synthetic settlement of the underlying, observed and received at 18:00 UTC on a date."""
        return (f"INSERT INTO market_data.observations VALUES('{catalog_id}','{ident}','{manifest['revisionId']}','{series or underlying}',{R},'fixture',"
                f"'synthetic_expiry_{uuid4().hex}','1','{role}','{state}','{day}T18:00:00Z','{day}T18:00:00Z',{price},NULL);")

    def enter():
        return f"SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token()}');"

    def lines(statements, setup=''):
        """Runs statements as the trader in one transaction that rolls back, after a setup run as the owner; returns their output."""
        output[:] = [line for line in query(f"BEGIN;{setup}{enter()}{statements}ROLLBACK;").splitlines() if line]
        return list(output)

    def sql(statements, setup=''):
        return lines(statements, setup)[-1:]

    first_request = '7b000000-0000-0000-0000-00000000f008'

    def expire(trade, request=first_request, observations=f"ARRAY['{settlement_id}']::uuid[]", field='outcome'):
        return f"SELECT security.process_futures_option_expiry('{catalog_id}','{request}','{trade}','{frame['id']}',{observations})->>'{field}';"

    def refused(statement, code):
        return (f"DO $$ BEGIN PERFORM {statement.removeprefix('SELECT ').rstrip(';')};RAISE EXCEPTION 'Expected rejection';"
                f"EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'{code}' THEN RAISE;END IF;END $$;SELECT 'refused';")

    def owner(statement):
        return 'RESET SESSION AUTHORIZATION;' + statement

    def linked(trade):
        """The outcome of a trade's expiry, the lots of the future it booked and its remaining lots, read back as the owner."""
        return owner(f"SELECT concat_ws('|',e.outcome,coalesce((SELECT trim_scale(r.lots)::text FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id)=(e.catalog_id,e.futures_trade_id)),'none'),"
                     f"trim_scale(e.remaining_lots)) FROM trading.futures_option_events e WHERE e.catalog_id='{catalog_id}' AND e.trade_id='{trade}' AND e.kind='expiry';")
    expiries = owner(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog_id}' AND kind='expiry';")
    strike = Decimal(call['strike'])
    itm, otm = strike + Decimal('0.5'), strike - Decimal('0.5')
    call_events = owner(f"SELECT count(*) FROM trading.futures_option_events WHERE catalog_id='{catalog_id}' AND trade_id='{long_call}';")
    check('an exercise dated after the moment it is recorded is refused and writes nothing',
          sql(refused(f"SELECT security.record_futures_option_event('{catalog_id}','{uuid4()}','{long_call}','exercise',1,clock_timestamp()+interval '1 minute')",
                      'option_event_future') + call_events) == ['1'])
    check('expiry before the option\'s expiry instant is refused and writes nothing, even with the expiry date\'s settlement selected',
          sql(refused(expire(long_call), 'option_expiry_not_reached') + expiries, settle(itm, expiry_day)) == ['0'])
    check('at expiry an in-the-money long call is exercised automatically into long futures for its open lots',
          sql(expire(long_call) + linked(long_call), expired() + settle(itm)) == ['exercised|2|0'])
    check('exactly at the money a call is exercised and a put abandoned, as the stored rule states',
          sql(expire(long_call) + linked(long_call), expired() + settle(strike)) == ['exercised|2|0'] and
          sql(expire(long_put) + linked(long_put), expired() + settle(Decimal(put['strike']))) == ['abandoned|none|0'])
    check('an out-of-the-money call is abandoned and books nothing', sql(expire(long_call) + linked(long_call), expired() + settle(otm)) == ['abandoned|none|0'])
    check('at expiry an in-the-money short call is assigned into a short future for its open lot',
          sql(expire(short_call) + linked(short_call), expired() + settle(itm)) == ['exercised|-1|0'])
    check('expiry records the selected settlement and the snapshot, and the linked future\'s request names them',
          sql(expire(long_call) + owner(f"""SELECT concat_ws('|',e.settlement_observation_id,e.snapshot_id,t.request->'optionEvent'->>'snapshot',t.request->>'settlementObservation',
              e.request ? 'observations') FROM trading.futures_option_events e JOIN trading.trades t ON(t.catalog_id,t.id)=(e.catalog_id,e.futures_trade_id)
              WHERE e.catalog_id='{catalog_id}' AND e.kind='expiry';"""), expired() + settle(itm)) == ['|'.join([settlement_id, frame['id'], frame['id'], settlement_id, 'f'])])
    call_expiry = expire(long_call).removeprefix('SELECT ').replace("->>'outcome';", '')
    retried = lines(f"DO $$DECLARE first jsonb={call_expiry};BEGIN IF {call_expiry} IS DISTINCT FROM first THEN RAISE EXCEPTION 'retry differs';END IF;END $$;SELECT 't';" +
                    expire(long_call, str(uuid4()), field='operationId') + expiries, expired() + settle(itm))
    check('retrying an expiry returns its first outcome; recording the same expiry again returns the existing record and writes nothing',
          retried[-3:] == ['t', first_request, '1'], retried)
    check('a second expiry of the holding on a different settlement is a conflict',
          sql(expire(long_call) + refused(expire(long_call, str(uuid4()), f"ARRAY['{other_id}']::uuid[]"), 'option_expiry_conflict'),
              expired() + settle(itm) + settle(itm, ident=other_id)) == ['refused'])
    check('with the settlement removed from the selected inputs, expiry is refused and writes nothing',
          sql(refused(expire(long_call, observations="ARRAY[]::uuid[]"), 'option_expiry_settlement_missing') + expiries, expired() + settle(itm)) == ['0'])
    other_quote = '7c000000-0000-0000-0000-00000000f008'
    quote_series = ("ALTER TABLE market_data.series DISABLE TRIGGER USER;"
                    f"INSERT INTO market_data.series SELECT catalog_id,revision_id,'{other_quote}','option_check_other_quote',reference_catalog_id,reference_revision_id,contract_id,'nymex_on_quote',NULL "
                    f"FROM market_data.series WHERE (catalog_id,id)=('{catalog_id}','{underlying}');ALTER TABLE market_data.series ENABLE TRIGGER USER;")
    for name, setup in [('a settlement on another date', settle(itm, '2026-10-15')),
                        ('a trader mark', settle(itm, role='trader_mark')),
                        ('a preliminary settlement', settle(itm, state='preliminary')),
                        ('another contract\'s settlement', settle(itm, series=other_month)),
                        ('a settlement in another quote', quote_series + settle(itm, series=other_quote))]:
        check(f'{name} is not the expiry settlement', sql(refused(expire(long_call), 'option_expiry_settlement_missing'), expired() + setup) == ['refused'])
    check('two final settlements of the underlying on the expiry date are ambiguous',
          sql(refused(expire(long_call, observations=f"ARRAY['{settlement_id}','{other_id}']::uuid[]"), 'option_expiry_settlement_ambiguous'),
              expired() + settle(itm) + settle(otm, ident=other_id)) == ['refused'])
    unknown = ("ALTER TABLE ref_data.futures_option_rules DISABLE TRIGGER USER;"
               f"UPDATE ref_data.futures_option_rules SET evaluation='unlisted_rule_v1' WHERE (catalog_id,revision_id,kind)=({R},'automatic_exercise');"
               "ALTER TABLE ref_data.futures_option_rules ENABLE TRIGGER USER;")
    check('an automatic exercise rule with an unknown evaluation is a structured error',
          sql(refused(expire(long_call), 'option_exercise_rule_unknown'), unknown + expired() + settle(itm)) == ['refused'])

    # A product that accepts contrary instructions, with its configured cutoff: a synthetic change to the ON rows.
    def instructions(cutoff):
        return ("ALTER TABLE ref_data.futures_option_products DISABLE TRIGGER USER;ALTER TABLE ref_data.futures_option_series DISABLE TRIGGER USER;"
                f"UPDATE ref_data.futures_option_products SET contrary_instructions_allowed=true WHERE (catalog_id,revision_id,code)=({R},'nymex_on');"
                f"UPDATE ref_data.futures_option_series SET instruction_cutoff_at={cutoff} WHERE (catalog_id,revision_id,product_code)=({R},'nymex_on');"
                "ALTER TABLE ref_data.futures_option_products ENABLE TRIGGER USER;ALTER TABLE ref_data.futures_option_series ENABLE TRIGGER USER;")

    def instruct(kind, trade=long_call, at=frame['at'], field='kind'):
        return f"SELECT security.record_futures_option_event('{catalog_id}','{uuid4()}','{trade}','{kind}',NULL,'{at}')->>'{field}';"
    open_until = instructions('expiry_at')
    # Filed while the cutoff lies ahead; the series then passes its expiry, and expiry is recorded in a new request context.
    def then_expired(price):
        return ('RESET SESSION AUTHORIZATION;DELETE FROM security.request_contexts WHERE backend_pid=pg_backend_pid();' +
                expired(past_expiry) + settle(price) + enter())
    check('where a product accepts instructions, an abandon instruction on an in-the-money option means expiry books nothing',
          sql(instruct('abandon_instruction') + then_expired(itm) + expire(long_call) + linked(long_call),
              open_until) == ['abandoned|none|0'])
    check('an exercise instruction on an out-of-the-money option means expiry books futures',
          sql(instruct('exercise_instruction') + then_expired(otm) + expire(long_call) + linked(long_call),
              open_until) == ['exercised|2|0'])
    check('an instruction after the configured cutoff is refused',
          sql(refused(instruct('exercise_instruction'), 'option_instruction_closed'), instructions("TIMESTAMPTZ '2026-10-16T15:00:00Z'")) == ['refused'])
    check('an instruction recorded after the cutoff is refused even from an older frame before it',
          sql(refused(instruct('exercise_instruction'), 'option_instruction_closed'), instructions("TIMESTAMPTZ '2026-10-16T17:00:00Z'")) == ['refused'])
    check('a product that accepts instructions but has no configured cutoff refuses them',
          sql(refused(instruct('exercise_instruction'), 'option_instruction_cutoff_unknown'), instructions('NULL')) == ['refused'])
    check('a short holding files no instructions', sql(refused(instruct('abandon_instruction', short_call), 'option_event_side'), open_until) == ['refused'])
    check('instructions follow the cutoff, not the exercise calendar', sql(instruct('exercise_instruction', at='2026-10-20T16:00:00Z'), open_until) == ['exercise_instruction'])
    repeated = lines(instruct('exercise_instruction', field='eventId') + instruct('exercise_instruction', field='eventId') +
                     refused(instruct('abandon_instruction'), 'option_instruction_conflict'), open_until)
    check('the same instruction recorded again returns the existing record; a contrary instruction is a conflict',
          len(repeated) >= 3 and repeated[-3] == repeated[-2] and repeated[-1] == 'refused', repeated)
    check('an instruction timed before the trade was executed is refused',
          sql(refused(instruct('exercise_instruction', at='2026-10-15T16:00:00Z'), 'option_instruction_window'), open_until) == ['refused'])
    close_all = f"SELECT security.record_futures_option_event('{catalog_id}','{uuid4()}','{long_call}','exercise',2,'{frame['at']}')->>'remainingLots';"
    check('a holding whose lots are all exercised files no instruction',
          lines(close_all + refused(instruct('exercise_instruction'), 'option_holding_closed'), open_until)[-2:] == ['0', 'refused'], output)
    check('a holding whose lots are all exercised has nothing to expire',
          lines(close_all + refused(expire(long_call), 'option_holding_closed'), expired() + settle(itm))[-2:] == ['0', 'refused'], output)

    # Early exercise dates follow the underlying's trading calendar and end on the option's expiry date.
    def exercise_at(at, code, setup=''):
        return sql(refused(f"SELECT security.record_futures_option_event('{catalog_id}','{uuid4()}','{long_call}','exercise',1,'{at}');", code), setup)
    # An event cannot be dated after it is recorded, so the series is moved to a past expiry and exercised on a later past date.
    check('exercise after the option\'s expiry date is refused',
          exercise_at(f'{date.fromisoformat(past_day) + timedelta(days=3)}T16:00:00Z', 'option_exercise_window', expired()) == ['refused'])
    check('exercise before the option trade was executed is refused', exercise_at('2026-10-15T16:00:00Z', 'option_exercise_window') == ['refused'])
    check('exercise on a date outside the trading calendar is refused', exercise_at('2026-10-20T16:00:00Z', 'option_calendar') == ['refused'])
    check('nothing was recorded by the rolled-back checks', counts() == settled, counts())

    # Two sessions race to exercise the same single lot: the booking lock serializes them and the second sees no open lot.
    race = booked(call, '1')
    exercise_race = f"SELECT security.record_futures_option_event('{catalog_id}','{{}}','{race}','exercise',1,'{frame['at']}')->>'remainingLots';"
    first_token, second_token = token(), token()
    results = {}

    def session(name, statements):
        results[name] = query(f"BEGIN;SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{first_token if name == 'first' else second_token}');{statements}COMMIT;").splitlines()[-1]
    holder = threading.Thread(target=session, args=('first', exercise_race.format(uuid4()) + 'SELECT pg_sleep(2);SELECT 1;'))
    holder.start()
    deadline = time.monotonic() + 10
    while query("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND objid=730849102 AND granted;") == '0':
        assert time.monotonic() < deadline and holder.is_alive()
        time.sleep(.05)
    session('second', refused(exercise_race.format(uuid4()), 'option_lots_exceeded'))
    holder.join()
    check('two sessions racing to exercise the same lot: one exercises it and the other is refused',
          results == {'first': '1', 'second': 'refused'} and holding(race, {})['openLots'] == '0', results)
