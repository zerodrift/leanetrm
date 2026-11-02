"""Independent behavior oracles for booking listed futures options with premium paid up front in the owned exchange
example. Option facts are the public ON/LO specifications (multiplier, premium tick); amounts are computed here."""
import hashlib
import json
import time
import uuid
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient

CONVENTION = 'futures_option_premium_v1'
# Public specification facts: currency per option contract per unit of premium, and the premium tick.
SPECIFICATION = {'nymex_on': (Decimal('10000'), Decimal('0.001')), 'nymex_lo': (Decimal('1000'), Decimal('0.01'))}


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
        return {'operationId': str(uuid4()), **context, 'bookId': book, 'series': option['series'], 'lots': lots, 'premium': premium,
                'premiumPaymentDate': '2026-10-19', 'premiumStyle': 'premium_paid_up_front', 'memo': 'Synthetic option booking', **parties, **changes}

    def stored(trade):
        return query(f"""SELECT concat_ws('|',trim_scale(r.lots),trim_scale(r.premium),r.premium_style,o.series_code,p.direction,trim_scale(p.amount),p.payment_date,
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

    def amount(premium, product, lots):
        return format((Decimal(premium) * SPECIFICATION[product][0] * abs(lots)).normalize(), 'f')

    long_call = body(on, '2', '0.125')
    booked = c.call('options/book', long_call)
    held = stored(booked['tradeId'])
    check('a long ON call books its exact option instrument and a premium of premium × multiplier × lots paid on the booked date',
          booked['optionInstrumentId'] == on['instrumentId'] and booked['premiumInstrumentId'] != on['instrumentId'] and
          held == '|'.join(['2', '0.125', 'premium_paid_up_front', on['series'], 'pay', amount('0.125', 'nymex_on', 2),
                            '2026-10-19', 'USD', booked['optionInstrumentId'], booked['premiumInstrumentId'], 'trader', 't',
                            'Synthetic option booking', parties['legalEntityId'], parties['counterpartyId']]), held)
    short_put = body(lo, '-3', '1.25')
    sold = c.call('options/book', short_put)
    held = stored(sold['tradeId'])
    check('a short LO put books the premium received for its absolute lots',
          held.split('|')[:8] == ['-3', '1.25', 'premium_paid_up_front', lo['series'], 'receive', amount('1.25', 'nymex_lo', -3), '2026-10-19', 'USD'], held)

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
    check('futures-style margined premium is unsupported', refused({'premiumStyle': 'futures_style_margin'}, 'option_unsupported'))
    check('an unknown premium style is refused', refused({'premiumStyle': 'premium_on_expiry'}, 'option_booking_input'))
    check('a missing premium payment date is refused', refused({'premiumPaymentDate': None}, 'option_booking_input'))
    check('zero lots are refused', refused({'lots': '0'}, 'option_booking_input'))
    check('fractional lots are refused', refused({'lots': '1.5'}, 'option_booking_input'))
    check('a negative premium is refused', refused({'premium': '-0.125'}, 'option_booking_input'))
    check('a premium off the product premium tick is refused', refused({'premium': '0.1255'}, 'option_booking_input'))
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
    raw(lo['series'], frame['at'], 'option_unsupported',
        "ALTER TABLE ref_data.futures_option_products DISABLE TRIGGER USER;"
        f"UPDATE ref_data.futures_option_products SET premium_style='futures_style_margin' WHERE (catalog_id,revision_id,code)=('{reference[0]}','{reference[1]}','nymex_lo');"
        "ALTER TABLE ref_data.futures_option_products ENABLE TRIGGER USER;")
    check('premium paid up front on a futures-style margined product is option_unsupported', rows() == before)

    # Booking reads the option definition under the trader's own market and reference read authority.
    def withdrawn(table, column, catalog, work):
        held = query(f"SELECT coalesce(json_agg(s),'[]')::text FROM security.{table} s WHERE {column}='{catalog}';")
        query(f"DELETE FROM security.{table} WHERE {column}='{catalog}';")
        try:
            return work()
        finally:
            query(f"INSERT INTO security.{table} SELECT * FROM json_populate_recordset(NULL::security.{table},'" + held.replace("'", "''") + "'::json);")
    seen, missing = withdrawn('reference_catalog_scopes', 'catalog_id', reference[0], lambda: (
        c.call('options/book', body(lo, '1', '1.25'), expected=422), c.call('options/book', body(lo, '1', '1.25', series='nymex_lo_29991201_p1.00'), expected=422)))
    check('a trader whose reference scope is withdrawn cannot book, and an unseen series is refused exactly as a nonexistent one',
          seen == missing and seen['code'] == 'option_definition' and rows() == before, (seen, missing))
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
