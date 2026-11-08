"""In-term gas admission, recorded payments and the administrator load, against the synthetic in-term demo."""
from copy import deepcopy
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal
from fractions import Fraction
import json
import re
from uuid import uuid4
from assistant_context import check_guard, migration_body
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking
from check_booked_side_programs import runtime_query, capture_context
from gas_in_term_fixture import ADMINISTRATOR, BOOK_ONLY_ADMINISTRATOR, CLOSE, DISABLED_ADMINISTRATOR, POINT, TERM, custom_curves, fixing, marks, premium_curve, published_at, trade

LOADER = 'preview_gas_in_term_loader'

STARTED = {'whole_gas_day_delivery_v1', 'accrued_payment_v1'}


def run_checks(url, query, manifest, run_worker, admin_load, browse=None):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    catalog = manifest['catalogId']
    frame = initial_frame(c.call('workspace'), '2026-11-06')
    def publish(graph):
        c.call('profile', {'profile': 'editor'})
        saved = c.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'definition': graph, 'layout': {}})
        c.call('profile', {'profile': 'approver'})
        return c.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': saved['id'], 'version': saved['version'], 'generation': saved['generation']})
    sources = {s['code']: s for s in c.call('custom-curves')['sources']}
    curve = custom_curves(publish, sources, catalog)
    c.call('profile', {'profile': 'trader'})
    context = dict(snapshotId=frame['id'], convention='physical_gas_fixed_quantity_v4', knowledgeMode='current')
    gas = c.call('gas/catalog', context)
    location = next(p for p in gas['locations'] if p['deliveryPoint'] == POINT)
    body = trade(context, location, gas['books'], gas['gasSetupId'], curve)
    encoded = json.dumps(body).replace("'", "''")
    def sql_value(statement, **options):
        return runtime_query(query, manifest, statement, **options).splitlines()[-1]
    def load(composition):
        outcome = admin_load({'administrator': ADMINISTRATOR[0], 'composition': composition})
        assert outcome.returncode == 0, outcome.stderr
        return json.loads(outcome.stdout)
    def refused(statement, code, **options):
        try: runtime_query(query, manifest, statement, **options)
        except RuntimeError as error: assert code in str(error), error
        else: raise AssertionError(f'Accepted instead of {code}')

    # (a) A started draft has no recorded cash to value its ended days with: the ticket's preview and booking refuse it
    # before staging anything, so the booking operation, which admits only staged programs, books nothing either.
    assert c.call('gas/composition/preview', body, expected=422)['code'] == 'gas_delivery_lifecycle'
    started = c.call('gas/composition/book', body, expected=422)
    assert started['code'] == 'gas_delivery_lifecycle', started
    assert query(f"SELECT count(*) FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=('{catalog}','{body['operationId']}');") == '0'
    refused(f"SELECT security.book_gas_composition('{catalog}','{encoded}'::jsonb,'{CLOSE}');", 'gas_schedule')
    print('PASS a started gas composition is refused by the ticket preview and booking before staging, and the booking operation books nothing', flush=True)

    # (b) A named administrator loads the started composition through the dedicated loader login (D359). The
    # API's runtime login cannot execute the load; the loader holds exactly runtime's privileges plus the load.
    # A context whose knowledge precedes the load.
    before_load = capture_context(query, manifest, '2026-11-06').splitlines()[-1]
    refused(f"SELECT security.load_gas_composition_in_term('{catalog}','{encoded}'::jsonb,'{CLOSE}');", 'permission denied for function load_gas_composition_in_term')
    assert query("SELECT has_function_privilege('openetrm_runtime','security.load_gas_composition_in_term(uuid,jsonb,timestamptz)','EXECUTE'),"
                 "has_function_privilege('openetrm_runtime','security.write_gas_composition(uuid,jsonb,timestamptz,boolean)','EXECUTE');") == 'f|f'
    query(f"CREATE ROLE {LOADER} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS INHERIT IN ROLE openetrm_runtime,openetrm_gas_in_term_loader;")
    beyond = lambda kind, objects, selected, privileges: (f"(SELECT count(*) FROM {objects} o CROSS JOIN unnest(ARRAY[{privileges}]) x(p)"
                                                          f" WHERE {selected} AND has_{kind}_privilege('{LOADER}',o.oid,x.p) AND NOT has_{kind}_privilege('openetrm_runtime',o.oid,x.p))")
    assert query("SELECT r.rolcanlogin,(SELECT count(*) FROM pg_auth_members WHERE member=r.oid),"
                 f"(SELECT string_agg(o.oid::regprocedure::text,',') FROM pg_proc o WHERE has_function_privilege('{LOADER}',o.oid,'EXECUTE') AND NOT has_function_privilege('openetrm_runtime',o.oid,'EXECUTE')),"
                 + beyond('table', 'pg_class', "o.relkind IN('r','v','m','p','f')", "'SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER'") + ',' +
                 beyond('sequence', 'pg_class', "o.relkind='S'", "'USAGE','SELECT','UPDATE'") + ',' + beyond('schema', 'pg_namespace', 'true', "'USAGE','CREATE'") +
                 ",pg_has_role('preview_runtime','openetrm_gas_in_term_loader','MEMBER') FROM pg_roles r WHERE r.rolname='openetrm_gas_in_term_loader';") == \
        'f|0|security.load_gas_composition_in_term(uuid,jsonb,timestamp with time zone)|0|0|0|f'
    print('PASS the runtime login cannot load; the loader login holds runtime privileges plus only the in-term load', flush=True)

    # The request is the ticket's composition naming a recorded close, under its own operation.
    assert frame.get('originCloseId') == frame['id'], frame
    def written():
        return query(f"SELECT (SELECT count(*) FROM trading.trades WHERE catalog_id='{catalog}'),(SELECT count(*) FROM trading.gas_trade_revisions WHERE catalog_id='{catalog}'),"
                     f"(SELECT count(*) FROM trading.side_schedule_requests WHERE catalog_id='{catalog}'),(SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}');")
    in_term = {**body, 'operationId': str(uuid4())}
    loaded = load(in_term)
    rows = written()
    assert load(in_term) == loaded and written() == rows
    administrator = ADMINISTRATOR[1]
    assert query(f"SELECT t.actor_id,r.actor_id FROM trading.trades t JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id)=(t.catalog_id,t.id)"
                 f" WHERE(t.catalog_id,t.id)=('{catalog}','{loaded['tradeId']}');") == f'{administrator}|{administrator}'
    assert lifecycles(query, catalog, loaded['instrumentId']) == {0: {'whole_gas_day_delivery_v1'}, 1: {'accrued_payment_v1'}}
    trade_id = loaded['tradeId']
    print('PASS the named administrator loads the started composition once through the loader, with whole gas-day and accrued payment programs', flush=True)
    # (d) An unknown or disabled administrator, or one without booking authority or its market reads, is refused and writes nothing.
    for login, code in (('in_term_unbound_administrator', 'gas_in_term_identity'), (DISABLED_ADMINISTRATOR[0], 'gas_in_term_identity'), ('admin', 'gas_in_term_book_authority'),
                        (BOOK_ONLY_ADMINISTRATOR[0], 'gas_in_term_market_read_authority')):
        attempt = {**body, 'operationId': str(uuid4())}
        outcome = admin_load({'administrator': login, 'composition': attempt})
        assert outcome.returncode == 1 and json.loads(outcome.stderr)['code'] == code, (login, outcome.stdout, outcome.stderr)
        assert query(f"SELECT count(*) FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=('{catalog}','{attempt['operationId']}');") == '0'
    assert written() == rows
    print('PASS an unknown, disabled or unauthorized administrator, or one without market reads, is refused with its own structured error and writes nothing', flush=True)

    # A forward composition booked through the ticket now selects the started-delivery lifecycles too.
    forward = deepcopy(body); forward.update(operationId=str(uuid4()), profile='fixed_gas_composition_v1')
    forward['physicalSides'][0].update(deliveryFrom='2026-12-01', deliveryTo='2027-01-01')
    forward['cashSides'] = [dict(code='payment', role='consideration', direction='pay', source='gas', stage='delivery', price='3.5', tariffCode=None, paymentAt='2027-01-10T00:00:00Z')]
    other = c.call('gas/composition/book', forward)
    assert lifecycles(query, catalog, other['instrumentId']) == {0: {'whole_gas_day_delivery_v1'}, 1: {'accrued_payment_v1'}}
    # A non-composition booking keeps its own future lifecycles.
    standard = initial_frame(c.call('workspace'), '2020-12-18')
    swap_context = {'snapshotId': standard['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    index = next(i for i in c.call('swaps/catalog', swap_context)['indices'] if i['curve'] == 'base_forward')
    rule = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 18, 'dayOfMonth': None, 'dayBasis': 'calendar_days', 'shortMonth': None, 'adjustment': 'unadjusted', 'calendar': None, 'implementation': 'monthly_payment_date_rules_v1'}
    leg = {'currency': index['currencyCode'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'paymentSchedule': rule}
    swap = c.call('swaps/book', {**swap_context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': gas['books'][0]['id'], 'definitionRevisionId': index['definitionRevisionId'],
                                 'commodity': index['commodity'], 'quantity': '100', 'fixedCoefficient': '-1', 'discounting': False, 'fixed': {'price': '3', **leg},
                                 'floating': {'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve': index['curve'], 'index': index['index'],
                                              **leg, 'resetFrequency': 'monthly', 'averagingMethod': 'none'}})
    swap_lifecycles = set().union(*lifecycles(query, catalog, swap['instrumentId']).values())
    assert swap_lifecycles and not swap_lifecycles & STARTED, swap_lifecycles
    print('PASS ticket compositions retain started-delivery lifecycles while a swap keeps its own', flush=True)

    # (c) Payments are idempotent by operation and by fact; a changed amount is a later-known revision.
    capture_context(query, manifest, '2026-11-06')
    # A historical view between payment and recording sees a payment only once its receipt is known.
    business = '2026-10-20'
    before_receipts = capture_context(query, manifest, business).splitlines()[-1]
    record = lambda payment: f"SELECT security.record_gas_cash_payment('{catalog}','{json.dumps(payment)}'::jsonb,'{CLOSE}');"
    def september(amount, last):
        return dict(operationId=str(uuid4()), tradeId=trade_id, expectedRevision=1, slot=1, businessKey='synthetic-2026-09-remittance', paidAt='2026-10-10T00:00:00Z',
                    currency='usd', amount=amount, allocations=[dict(occurrence=n, amount='-290' if n < 29 else last) for n in range(30)])
    partial = september('-8600', '-190')
    original = json.loads(sql_value(record(partial)))
    assert json.loads(sql_value(record(partial))) == original and original['revision'] == 1
    rows = lambda: query(f"SELECT count(*) FROM trading.gas_cash_payments WHERE(catalog_id,trade_id)=('{catalog}','{trade_id}');")
    assert rows() == '1'
    capture_context(query, manifest, '2026-11-06')
    corrected = september('-8700', '-290')
    revision = json.loads(sql_value(record(corrected)))
    assert (revision['revision'], revision['previousId']) == (2, original['paymentId']) and revision['paymentId'] != original['paymentId']
    assert json.loads(sql_value(record({**corrected, 'operationId': str(uuid4())})))['paymentId'] == revision['paymentId'] and rows() == '2'
    # A retried no-op keeps its first outcome after a later revision of the same business key.
    same = {**corrected, 'operationId': str(uuid4())}
    assert json.loads(sql_value(record(same)))['paymentId'] == revision['paymentId']
    later = json.loads(sql_value(record(september('-8650', '-240'))))
    assert later['revision'] == 3 and json.loads(sql_value(record(same)))['paymentId'] == revision['paymentId'] and rows() == '3'
    restored = json.loads(sql_value(record(september('-8700', '-290'))))
    assert (restored['revision'], restored['previousId']) == (4, later['paymentId']) and rows() == '4'
    capture_context(query, manifest, '2026-11-06')
    after_receipts = capture_context(query, manifest, business).splitlines()[-1]
    def visible(context):
        return sql_value(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{business}T00:00:00Z');"
                         f"SELECT count(*) FROM trading.gas_cash_payments WHERE(catalog_id,trade_id)=('{catalog}','{trade_id}');")
    assert (visible(before_receipts), visible(after_receipts)) == ('0', '4')
    known = query(f"SELECT bool_and(later.known_from>earlier.known_from) FROM trading.gas_cash_payments p JOIN trading.knowledge_receipts later ON(later.catalog_id,later.publication_order)=(p.catalog_id,p.publication_order)"
                  f" JOIN trading.gas_cash_payments o ON(o.catalog_id,o.id)=(p.catalog_id,p.previous_id) JOIN trading.knowledge_receipts earlier ON(earlier.catalog_id,earlier.publication_order)=(o.catalog_id,o.publication_order)"
                  f" WHERE(p.catalog_id,p.id)=('{catalog}','{revision['paymentId']}');")
    assert known == 't', known
    assert query(f"SELECT count(*),sum(amount) FROM trading.gas_cash_payment_allocations WHERE(catalog_id,payment_id)=('{catalog}','{revision['paymentId']}');") == '30|-8700'
    refused(record({**partial, 'amount': '-8700'}), 'gas_payment_conflict')
    for change, code in [(dict(amount='-8700', allocations=[dict(occurrence=0, amount='-8600')]), 'gas_payment_amount'),
                         (dict(amount='290', allocations=[dict(occurrence=0, amount='290')]), 'gas_payment_amount'),
                         (dict(currency='eur'), 'gas_payment_currency'),
                         (dict(amount='-290', allocations=[dict(occurrence=80, amount='-290')]), 'gas_payment_occurrence'),
                         (dict(amount='-290', allocations=[dict(occurrence=122, amount='-290')]), 'gas_payment_occurrence'),
                         (dict(slot=0), 'gas_payment_slot'), (dict(paidAt='2026-11-07T00:00:00Z'), 'gas_payment_input'), (dict(paidAt='2026-10-10'), 'gas_payment_input')]:
        refused(record({**september('-8700', '-290'), **change}), code)
    print('PASS gas payments: exact retries, one row per fact, a later-known revision, conserved allocations and explicit refusals', flush=True)

    # (d) A payment can allocate only to its own membership's retained occurrences.
    other_schedule, other_row = query(f"SELECT s.schedule_id,r.ordinal FROM trading.instrument_side_schedules s JOIN trading.side_schedule_rows r USING(catalog_id,schedule_id)"
                                      f" WHERE(s.catalog_id,s.instrument_id,s.slot,r.local_key)=('{catalog}','{other['instrumentId']}',1,'payment/0/value');").split('|')
    copy = f"INSERT INTO trading.gas_cash_payments SELECT catalog_id,'{uuid4()}',trade_id,trade_revision,{{instrument}},slot,'synthetic-foreign',1,NULL,paid_at,reference_catalog_id,reference_revision_id,currency_code,-290,'{uuid4()}',actor_id,publication_order FROM trading.gas_cash_payments WHERE id='{revision['paymentId']}' RETURNING id"
    for statement, code in [(copy.format(instrument=f"'{other['instrumentId']}'") + ';', 'gas_cash_payments_catalog_id_trade_id_trade_revision_instr_fkey'),
                            (f"WITH p AS({copy.format(instrument='instrument_id')}) INSERT INTO trading.gas_cash_payment_allocations SELECT '{catalog}',id,0,'{other_schedule}',{other_row},-290 FROM p;SET CONSTRAINTS ALL IMMEDIATE;", 'gas_payment_allocation')]:
        try: query('BEGIN;' + statement + 'ROLLBACK;')
        except RuntimeError as error: assert code in str(error), error
        else: raise AssertionError(f'A foreign occurrence was accepted instead of {code}')
    assert query("SELECT has_table_privilege('openetrm_runtime','trading.gas_cash_payments','INSERT'),has_table_privilege('openetrm_runtime','trading.gas_cash_payment_allocations','INSERT');") == 'f|f'
    print('PASS gas payment allocations name only their own trade, membership and retained occurrence', flush=True)
    shown = valuation_checks(c, query, manifest, run_worker, frame, context, body, trade_id, loaded['instrumentId'], before_load, load)
    # The browser reads the Blotter before the refusing trades below are planted: they make the portfolio total refuse, by design.
    if browse: browse(shown)
    delivery_mark_checks(c, frame, context, body, publish, sources)
    payment_revision_checks(c, query, manifest, frame, context, body, load)
    refusal_checks(c, query, manifest, frame, body, trade_id, loaded['instrumentId'], other, revision['paymentId'], load)


def valuation_checks(c, query, manifest, run_worker, frame, context, body, trade_id, binding, before_load, load):
    """Measures, Side states, retained risk and history of the loaded in-term composition at the close."""
    close = datetime(2026, 11, 6, 21, tzinfo=timezone.utc)
    ended = lambda day: datetime(day.year, day.month, day.day, 15, tzinfo=timezone.utc) + timedelta(days=1) <= close
    days = [TERM[0] + timedelta(days=n) for n in range((TERM[1] - TERM[0]).days)]
    location = {}
    for _, start, end, hub, basis in marks():
        for n in range((end - start).days): location[start + timedelta(days=n)] = hub + basis
    fixed = lambda day: published_at(day) <= CLOSE
    paid = {day: Decimal('-290') for day in days if day.month == 9}
    # The trade selects no discounting, so every factor is exactly 1 (each inspected row reports it below).
    remaining_physical = sum(100 * location[d] for d in days if not ended(d))
    remaining_cash = sum(-100 * (fixing(d) if fixed(d) else location[d]) for d in days if not ended(d))
    outstanding = sum(-100 * fixing(d) - paid.get(d, 0) for d in days if ended(d))
    total = remaining_physical + remaining_cash + outstanding
    due = lambda d: date(d.year + d.month // 12, d.month % 12 + 1, 10)
    expected = {}
    for d in days:
        if d not in paid: expected[due(d)] = expected.get(due(d), 0) - 100 * (fixing(d) if fixed(d) else location[d])
    assert (outstanding, total) == (Decimal('-10855'), Decimal('-10855')), (outstanding, total)
    assert expected == {date(2026, 11, 10): Decimal('-9300'), date(2026, 12, 10): Decimal('-9648'), date(2027, 1, 10): Decimal('-10912')}, expected
    working = dict(snapshotId=frame['id'], convention=context['convention'], knowledgeMode='current')
    read = c.call('gas/composition/editor', dict(context=working, tradeId=trade_id))['valuation']
    measures = read['measures']
    assert (measures['label'], measures['discounting']) == ('Unrealized + realized unsettled', 'undiscounted'), measures
    assert [Fraction(measures[k]) for k in ('unrealized', 'realizedUnsettled', 'total')] == [remaining_physical + remaining_cash, outstanding, total], measures
    assert Fraction(read['presentValue']) == total
    assert {datetime.fromisoformat(x['dueAt']).date(): Decimal(x['amount']) for x in measures['expectedCash']} == expected, measures['expectedCash']
    # The booked-trade read the ticket and the trade view draw from carries the same measures and their currency.
    booked = c.call('tickets/trade', dict(context=working, tradeId=trade_id, bindingId=binding, revision=1))
    assert (booked['measures'], booked['currency']) == (measures, read['currency']), booked['measures']
    print('PASS the in-term read reports unrealized, realized unsettled, their labelled sum and undiscounted expected cash', flush=True)

    # The Side inspector shows each gas day's delivery, price and settlement state.
    position = dict(context={**working, 'convention': 'trading_workspace_v1'}, tradeId=trade_id, bindingId=binding, revision=1, instrumentType='physical_gas')
    def rows(slot):
        request = dict(position=position, sideSlot=slot, contribution=None, offset=0, limit=128)
        first = c.call('trading/side-schedules', request)
        found = first['page']['rows'] + c.call('trading/side-schedules', {**request, 'offset': 128})['page']['rows'] if first['page']['total'] > 128 else first['page']['rows']
        assert len(found) == first['page']['total'] == 122
        return {date.fromisoformat((r['deliveryFrom'] or r['operatingDate'])): r for r in found}
    cash, physical = rows(1), rows(0)
    assert all(Fraction(r['discountFactor']) == 1 for r in cash.values() if r['discountFactor'] is not None)
    september = cash[date(2026, 9, 1)]
    assert (september['lifecycle']['cash'], september['paid'], september['outstanding'], september['paidAt'][:10], september['lifecycle']['coverage']) == ('Paid', '-290', '0', '2026-10-10', None), september
    october = cash[date(2026, 10, 15)]
    assert (october['lifecycle']['delivery'], october['lifecycle']['cash'], october['accrued'], october['outstanding'], october['quantityState'], october['lifecycle']['coverage']) == \
        ('Ended', 'AccruedUnpaid', '-300', '-300', 'contract_estimate_allocation_pending_v1', 'RealizedUnsettled'), october
    assert Fraction(october['presentValue']) == -300
    for day in (date(2026, 11, 7), date(2026, 11, 8), date(2026, 11, 9)):
        row = cash[day]
        assert (row['lifecycle']['delivery'], row['lifecycle']['cash'], row['lifecycle']['coverage'], row['price']) == ('NotStarted', 'Remaining', 'Unrealized', '3.22'), row
        assert 'PublishedFixing' in row['priceStates'] and Fraction(row['presentValue']) == -322, row
    assert cash[date(2026, 11, 6)]['lifecycle']['delivery'] == 'InProgress' and 'Projected' in cash[date(2026, 11, 10)]['priceStates']
    assert sum(Fraction(r['presentValue']) for r in cash.values()) == remaining_cash + outstanding
    gone = physical[date(2026, 10, 15)]
    assert (gone['lifecycle']['delivery'], gone['lifecycle']['cash'], gone['lifecycle']['coverage'], gone['quantityState'], Fraction(gone['presentValue'])) == \
        ('Ended', 'NotApplicable', None, 'contract_estimate_allocation_pending_v1', 0), gone
    assert physical[date(2026, 11, 6)]['lifecycle']['cash'] == 'NotApplicable' and Fraction(physical[date(2026, 11, 6)]['presentValue']) == 318
    # Undelivered physical days, fixed or not, read the location curve's mark (here equal to the fixing), never the payment fixing.
    for day in (date(2026, 11, 6), date(2026, 11, 7), date(2026, 11, 9), date(2026, 11, 10)):
        assert 'Projected' in physical[day]['priceStates'] and 'PublishedFixing' not in physical[day]['priceStates'] and Fraction(physical[day]['price']) == location[day], physical[day]
    assert sum(Fraction(r['presentValue']) for r in physical.values()) == remaining_physical
    print('PASS the Side inspector shows paid, accrued and remaining gas days with fixed price states, and physical days with no settlement', flush=True)

    # The retained risk worker reproduces the total; a shock moves only fixed, undelivered physical days.
    c.call('profile', {'profile': 'risk'})
    shared = {**working, 'convention': 'trading_workspace_v1'}
    scope = dict(context=shared, bookIds=[], portfolioIds=[], tradeIds=[trade_id])
    catalog = c.call('portfolio/risk/catalog', scope)
    assert not catalog['excludedPositions'], catalog['excludedPositions']
    def covered(factor):
        start, end = date.fromisoformat(factor['deliveryFrom']), date.fromisoformat(factor['deliveryTo'])
        return {start + timedelta(days=n) for n in range((end - start).days)}
    # Nov 3 has ended: its accrued cash and delivered day must not move under a shock to its marks.
    probes = [date(2026, 11, 3), date(2026, 11, 6), date(2026, 11, 7), date(2026, 11, 20), date(2026, 12, 15)]
    selected = [f for f in catalog['factors'] if covered(f) & set(probes)][:16]
    assert {d for f in selected for d in covered(f) & set(probes)} == set(probes), selected
    request = {**scope, 'context': {**shared, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']}, 'detail': 'holdings',
               'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'], 'pricingPolicy': catalog['pricingPolicy'], 'stress': [],
               'bumps': [dict(factor=f['factor'], size='1', convention=catalog['bumpConvention']) for f in selected]}
    result = c.call('portfolio/risk', request)
    from check_portfolio_risk import financial
    job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request)); run_worker()
    done = c.call('portfolio/risk/jobs/' + job['id'])
    assert done['state'] == 'completed' and financial(done['result']) == financial(result), done
    for factor in result['factors']:
        values = factor['positions'][0]['values']
        moved = covered(factor['factor']) & {d for d in days if fixed(d) and not ended(d)}
        assert Fraction(values['base']) == total and Fraction(values['delta']) == 100 * len(moved), (factor['factor'], values)
    c.call('profile', {'profile': 'trader'})
    # The worker reads payments only within a claimed lease and never writes them.
    assert query("BEGIN;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;SELECT count(*) FROM trading.gas_cash_payments;ROLLBACK;") == '0'
    assert query("SELECT has_table_privilege('openetrm_risk_worker','trading.gas_cash_payments','INSERT'),has_table_privilege('openetrm_risk_worker','trading.gas_cash_payment_allocations','SELECT'),"
                 "has_table_privilege('openetrm_risk_worker','trading.gas_cash_payment_commands','SELECT');") == 'f|t|f'
    print('PASS retained risk and its worker reproduce the total; accrued days are frozen and only fixed undelivered physical days move', flush=True)

    # A context known before the load sees no trade.
    refused = c.call('gas/composition/editor', dict(context={**working, 'knowledgeMode': 'retained', 'contextId': before_load}, tradeId=trade_id), expected=422)
    assert refused['code'] == 'gas_composition_input', refused
    print('PASS a historical context known before the load reads no in-term trade', flush=True)
    determined = final_determination(c, query, manifest, frame, context, body, load)
    # What the browser check must find on screen, from the oracle above: the figures as the page groups them, the trades by their Blotter reference.
    refs = {h['tradeId']: h['tradeReference'] for h in c.call('trading/blotter', dict(context={**working, 'convention': 'trading_workspace_v1'},
            view=dict(mode='trades', bookIds=[], portfolioIds=[], groupBy=[], measures=[], pivotConvention=None)))['holdings']}
    grouped = lambda value: f'{Decimal(value).normalize():,f}'
    return dict(tradeId=trade_id, instrumentId=binding, tradeReference=refs[trade_id], currency=read['currency'],
                unrealized=grouped(remaining_physical + remaining_cash), realizedUnsettled=grouped(outstanding), total=grouped(total),
                expectedCash=[grouped(expected[d]) for d in sorted(expected)], determined=dict(determined, tradeReference=refs[determined['tradeId']], amount='-303.8'))


def delivery_mark_checks(c, frame, context, body, publish, sources):
    """D359: a fixed gas day not yet delivered values its physical leg at the location curve's own mark and its payment at the fixing."""
    nov7 = deepcopy(body); nov7.update(operationId=str(uuid4()))
    nov7['physicalSides'][0].update(deliveryFrom='2026-11-07', deliveryTo='2026-11-08')
    working = dict(snapshotId=frame['id'], convention=context['convention'], knowledgeMode='current')
    def priced(curve):
        nov7['physicalSides'][0]['forecastCurve'] = {**body['physicalSides'][0]['forecastCurve'], 'curveId': curve['id'], 'version': curve['version']}
        return nov7
    premium, unmarked = premium_curve(publish, sources), premium_curve(publish, sources, omit='2026-11-07')
    c.call('profile', {'profile': 'trader'})
    # Without a mark for the undelivered day the trade refuses; the published 3.22 fixing never stands in for it.
    gap = c.call('gas/composition/preview', priced(unmarked), expected=422)
    assert gap['code'] == 'custom_projection', gap
    booked = c.call('gas/composition/book', priced(premium))
    read = c.call('gas/composition/editor', dict(context=working, tradeId=booked['tradeId']))['valuation']
    # 100 MMBtu received at the 3.40 mark against the index paid at its 3.22 fixing: +18.00, undiscounted.
    assert [Fraction(read['measures'][k]) for k in ('unrealized', 'realizedUnsettled', 'total')] == [18, 0, 18] and Fraction(read['presentValue']) == 18, read['measures']
    position = dict(context={**working, 'convention': 'trading_workspace_v1'}, tradeId=booked['tradeId'], bindingId=booked['instrumentId'], revision=1, instrumentType='physical_gas')
    [physical], [cash] = (c.call('trading/side-schedules', dict(position=position, sideSlot=slot, contribution=None, offset=0, limit=8))['page']['rows'] for slot in (0, 1))
    assert (physical['price'], Fraction(physical['presentValue']), physical['lifecycle']['coverage']) == ('3.4', 340, 'Unrealized') and 'PublishedFixing' not in physical['priceStates'], physical
    assert (cash['price'], Fraction(cash['presentValue'])) == ('3.22', -322) and 'PublishedFixing' in cash['priceStates'], cash
    view = dict(mode='trades', bookIds=[], portfolioIds=[], groupBy=[], measures=[], pivotConvention=None)
    held = next(h for h in c.call('trading/blotter', dict(context={**working, 'convention': 'trading_workspace_v1'}, view=view))['holdings'] if h['tradeId'] == booked['tradeId'])
    assert (held['measure'], held['presentValue'], held['unrealized'], held['realizedUnsettled']) == ('gas_lifecycle_value_v1', '18', '18', '0'), held
    print('PASS a fixed, undelivered gas day delivers at its 3.40 location mark against its 3.22 fixing: unrealized +18; with no mark it refuses', flush=True)


def final_determination(c, query, manifest, frame, context, body, load):
    """A fixed-price in-term day with a D338 final determination shows it beside the contract-estimate accrual."""
    single = deepcopy(body); single.update(operationId=str(uuid4()), profile='fixed_gas_composition_v1')
    single['physicalSides'][0].update(deliveryFrom='2026-11-01', deliveryTo='2026-11-02')
    single['cashSides'] = [dict(code='payment', role='consideration', direction='pay', source='gas', stage='delivery', price='3.1', tariffCode=None, paymentAt='2026-12-10T00:00:00Z')]
    staged = c.call('gas/composition/book', single, expected=422)
    assert staged['code'] == 'gas_delivery_lifecycle', staged
    loaded = load({**single, 'operationId': str(uuid4())})
    working = dict(snapshotId=frame['id'], convention=context['convention'], knowledgeMode='current')
    trade = loaded['tradeId']
    def inspected():
        position = dict(context={**working, 'convention': 'trading_workspace_v1'}, tradeId=trade, bindingId=loaded['instrumentId'], revision=1, instrumentType='physical_gas')
        return c.call('trading/side-schedules', dict(position=position, sideSlot=1, contribution=None, offset=0, limit=8))['page']['rows']
    before = c.call('gas/composition/editor', dict(context=working, tradeId=trade))['valuation']['measures']
    assert [Fraction(before[k]) for k in ('unrealized', 'realizedUnsettled', 'total')] == [0, -310, -310], before
    assert [r['finalDetermination'] for r in inspected()] == [None]
    obligation = c.call('gas/quantities/read', dict(context=working, tradeId=trade, selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    c.call('gas/quantities/record', dict(context=working, tradeId=trade, expectedRevision=1, operationId=str(uuid4()), obligationId=obligation, kind='allocated', status='final',
                                         day='2026-11-01', quantity='98', evidence='Synthetic final allocation', expectedPreviousId=None))
    preview = dict(context=working, tradeId=trade, expectedRevision=1, cashSlot=1, day='2026-11-01', selectionPolicy='final_allocated_billing_v1',
                   profile='gas_final_daily_fixed_cash_v1', decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')
    determined = c.call('gas/billing/preview', preview)
    assert determined['amount'] == '-303.8', determined
    c.call('gas/billing/determine', dict(preview=preview, operationId=str(uuid4()), previewHash=determined['previewHash'], sourceId=determined['sourceId'],
                                         expectedPreviousId=None, correctionDueAt=None, reason=None, evidence='Synthetic final billing review'))
    [row] = inspected()
    assert (row['finalDetermination'], row['accrued'], row['outstanding'], row['lifecycle']['cash']) == ('-303.8', '-310', '-310', 'AccruedUnpaid'), row
    assert c.call('gas/composition/editor', dict(context=working, tradeId=trade))['valuation']['measures'] == before
    print('PASS a D338 final determination is shown beside the contract-estimate accrual and changes neither the accrual nor the measures', flush=True)
    return dict(tradeId=trade, instrumentId=loaded['instrumentId'])


def payment_revision_checks(c, query, manifest, frame, context, body, load):
    """A later revision that moves a payment's paid instant past the view's business time leaves that view unpaid."""
    catalog = manifest['catalogId']
    single = deepcopy(body); single.update(profile='fixed_gas_composition_v1')
    single['physicalSides'][0].update(deliveryFrom='2026-11-02', deliveryTo='2026-11-03')
    single['cashSides'] = [dict(code='payment', role='consideration', direction='pay', source='gas', stage='delivery', price='3.1', tariffCode=None, paymentAt='2026-12-10T00:00:00Z')]
    loaded = load({**single, 'operationId': str(uuid4())})
    def record(paid, as_of):
        payment = dict(operationId=str(uuid4()), tradeId=loaded['tradeId'], expectedRevision=1, slot=1, businessKey='synthetic-2026-11-02-remittance', paidAt=paid,
                       currency='usd', amount='-310', allocations=[dict(occurrence=0, amount='-310')])
        statement = f"SELECT security.record_gas_cash_payment('{catalog}','{json.dumps(payment)}'::jsonb,'{as_of}');"
        return json.loads(runtime_query(query, manifest, statement).splitlines()[-1])['revision']
    # Revision 1 is paid before the close; revision 2, recorded the next day, moves the paid instant past the close.
    capture_context(query, manifest, '2026-11-06')
    assert record('2026-11-04T00:00:00Z', CLOSE) == 1
    before_revision = capture_context(query, manifest, '2026-11-06').splitlines()[-1]
    assert record('2026-11-07T00:00:00Z', '2026-11-07T12:00:00Z') == 2
    capture_context(query, manifest, '2026-11-06')
    working = dict(snapshotId=frame['id'], convention=context['convention'], knowledgeMode='current')
    def shown(view):
        position = dict(context={**view, 'convention': 'trading_workspace_v1'}, tradeId=loaded['tradeId'], bindingId=loaded['instrumentId'], revision=1, instrumentType='physical_gas')
        [row] = c.call('trading/side-schedules', dict(position=position, sideSlot=1, contribution=None, offset=0, limit=8))['page']['rows']
        outstanding = c.call('gas/composition/editor', dict(context=view, tradeId=loaded['tradeId']))['valuation']['measures']['realizedUnsettled']
        return row['lifecycle']['cash'], row['paidAt'] and row['paidAt'][:10], Fraction(outstanding)
    # The close lies between the two paid instants: the latest known revision applies and is not yet paid.
    assert shown(working) == ('AccruedUnpaid', None, -310), shown(working)
    # Knowledge before revision 2 sees revision 1, paid before the close.
    assert shown({**working, 'knowledgeMode': 'retained', 'contextId': before_revision}) == ('Paid', '2026-11-04', 0)
    print('PASS the latest known payment revision applies by knowledge; one paid after the view stays unpaid there, never its earlier revision', flush=True)


def refusal_checks(c, query, manifest, frame, body, trade_id, binding, forward, payment, load):
    """Trade-specific refusals stay on their own rows; a portfolio holding a refusing trade refuses its total."""
    catalog = manifest['catalogId']
    # Review Focus 5: an equivalent in-term composition whose stored programs keep the future-delivery lifecycles,
    # as a trade booked before 0215 holds them (content, its digest and its rows rewritten together).
    # Its own term (from October), so it shares no retained schedule with the in-term fixture.
    copy = deepcopy(body); copy['operationId'] = str(uuid4()); copy['physicalSides'][0]['deliveryFrom'] = '2026-10-01'
    assert c.call('gas/composition/book', copy, expected=422)['code'] == 'gas_delivery_lifecycle'
    old = load({**copy, 'operationId': str(uuid4())})
    schedules = f"(SELECT schedule_id FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id)=('{catalog}','{old['instrumentId']}'))"
    query("BEGIN;SET LOCAL session_replication_role=replica;"
          f"UPDATE trading.side_schedule_programs p SET content=x.content,content_hash=sha256(x.content) FROM(SELECT schedule_id,convert_to(replace(replace(convert_from(content,'UTF8'),"
          f"'\"whole_gas_day_delivery_v1\"','\"future_gas_delivery_v1\"'),'\"accrued_payment_v1\"','\"undue_payment_v1\"'),'UTF8') content FROM trading.side_schedule_programs"
          f" WHERE catalog_id='{catalog}' AND schedule_id IN {schedules})x WHERE(p.catalog_id,p.schedule_id)=('{catalog}',x.schedule_id);"
          f"UPDATE trading.side_schedule_rows SET parameters=jsonb_set(parameters,'{{lifecycle}}',to_jsonb(CASE parameters->>'lifecycle' WHEN 'whole_gas_day_delivery_v1' THEN 'future_gas_delivery_v1' ELSE 'undue_payment_v1' END))"
          f" WHERE catalog_id='{catalog}' AND schedule_id IN {schedules} AND operation='output_guard';COMMIT;")
    assert lifecycles(query, catalog, old['instrumentId']) == {0: {'future_gas_delivery_v1'}, 1: {'undue_payment_v1'}}
    assert set().union(*lifecycles(query, catalog, binding).values()) == STARTED
    # A forward composition given a recorded payment against a cash day that has not started: its own refusal.
    other_schedule, other_row = query(f"SELECT s.schedule_id,r.ordinal FROM trading.instrument_side_schedules s JOIN trading.side_schedule_rows r USING(catalog_id,schedule_id)"
                                      f" WHERE(s.catalog_id,s.instrument_id,s.slot,r.local_key)=('{catalog}','{forward['instrumentId']}',1,'payment/0/value');").split('|')
    early = str(uuid4())
    query(f"BEGIN;INSERT INTO trading.gas_cash_payments SELECT catalog_id,'{early}','{forward['tradeId']}',1,'{forward['instrumentId']}',1,'synthetic-early',1,NULL,'2026-11-01T00:00:00Z',"
          f"reference_catalog_id,reference_revision_id,currency_code,-290,'{uuid4()}',actor_id,publication_order FROM trading.gas_cash_payments WHERE id='{payment}';"
          f"INSERT INTO trading.gas_cash_payment_allocations VALUES('{catalog}','{early}',0,'{other_schedule}',{other_row},-290);COMMIT;")
    working = dict(snapshotId=frame['id'], convention='trading_workspace_v1', knowledgeMode='current')
    view = dict(mode='trades', bookIds=[], portfolioIds=[], groupBy=[], measures=[], pivotConvention=None)
    rows = {h['tradeId']: h for h in c.call('trading/blotter', dict(context=working, view=view))['holdings']}
    assert (rows[old['tradeId']]['unavailableReason'], rows[old['tradeId']]['presentValue']) == ('gas_delivery_lifecycle', None), rows[old['tradeId']]
    assert (rows[forward['tradeId']]['unavailableReason'], rows[forward['tradeId']]['presentValue']) == ('payment_state', None), rows[forward['tradeId']]
    held = rows[trade_id]
    assert (held['unavailableReason'], held['presentValue'], held['measure'], held['measureLabel'], held['discounting'], held['unrealized'], held['realizedUnsettled']) == \
        (None, '-10855', 'gas_lifecycle_value_v1', 'Unrealized + realized unsettled', 'undiscounted', '0', '-10855'), held
    # The inspector shows each refusing Side's retained plan with its own reason: the old trade's started
    # physical Side, and either Side of the trade whose recorded cash refuses.
    for selected, slot, code in ((old, 0, 'gas_delivery_lifecycle'), (forward, 0, 'payment_state'), (forward, 1, 'payment_state')):
        position = dict(context=working, tradeId=selected['tradeId'], bindingId=selected['instrumentId'], revision=1, instrumentType='physical_gas')
        plan = c.call('trading/side-schedules', dict(position=position, sideSlot=slot, contribution=None, offset=0, limit=4))['page']['rows']
        assert plan and all(r['presentValue'] is None and r['lifecycle']['unavailableReason'] == code for r in plan), plan
    # A portfolio of the in-term trade and the old one refuses its total rather than omitting the old trade.
    c.call('profile', {'profile': 'risk'})
    scope = dict(context=working, bookIds=[], portfolioIds=[], tradeIds=[trade_id, old['tradeId']])
    mixed = c.call('portfolio/risk/catalog', scope)
    assert not mixed['excludedPositions'], mixed['excludedPositions']
    request = {**scope, 'context': {**working, 'knowledgeMode': 'retained', 'contextId': mixed['context']['contextId']}, 'detail': 'holdings', 'convention': mixed['convention'],
               'numericalPolicy': mixed['numericalPolicy'], 'pricingPolicy': mixed['pricingPolicy'], 'stress': [],
               'bumps': [dict(factor=mixed['factors'][0]['factor'], size='1', convention=mixed['bumpConvention'])]}
    refused = c.call('portfolio/risk', request, expected=422)
    assert refused['code'] == 'gas_delivery_lifecycle', refused
    assert not c.call('portfolio/risk/catalog', dict(context=working, bookIds=[], portfolioIds=[], tradeIds=[trade_id]))['excludedPositions']
    c.call('profile', {'profile': 'trader'})
    print('PASS trade-specific refusals stay on their own Blotter and inspector rows; a portfolio holding an old-lifecycle trade after its start refuses', flush=True)


def lifecycles(query, catalog, instrument):
    rows = query(f"SELECT s.slot,r.parameters->>'lifecycle' FROM trading.instrument_side_schedules s JOIN trading.side_schedule_rows r USING(catalog_id,schedule_id)"
                 f" WHERE(s.catalog_id,s.instrument_id,r.operation)=('{catalog}','{instrument}','output_guard') GROUP BY 1,2;")
    found = {}
    for line in rows.splitlines():
        slot, lifecycle = line.split('|'); found.setdefault(int(slot), set()).add(lifecycle)
    return found


def upgrade_checks(query, migration):
    """Before 0215: pinned helpers refuse drift, and stored future-lifecycle rows are neither changed nor re-validated."""
    text = migration.read_text()
    signatures = re.findall(r"^\s*\('([^']+)','[a-f0-9]{64}'", text, re.MULTILINE)
    assert len(signatures) == 7
    changes = [(change, signature) for signature in signatures for change in (
        f"ALTER FUNCTION {signature} SET work_mem='64kB';", f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
        f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
        f"DO $drift$ DECLARE body text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(body,'AS $function$','AS $function$ /* synthetic drift */');END$drift$;")]
    check_guard(query, text, '0215', 'in_term_upgrade', changes)
    try: query("BEGIN;CREATE FUNCTION security.write_gas_composition(uuid,jsonb,timestamptz,boolean) RETURNS jsonb LANGUAGE sql AS 'SELECT NULL::jsonb';" + migration_body(text) + 'ROLLBACK;')
    except RuntimeError as error: assert 'in_term_upgrade: unexpected private writer' in str(error), error
    else: raise AssertionError('A preexisting private writer was admitted')
    # A composition stored before 0215: its physical and cash guards keep their future lifecycles.
    schedule = '7f000000-0000-0000-0000-000000000215'
    def guard(ordinal, key, lifecycle, measure, grid):
        parameters = json.dumps(dict(lifecycle=lifecycle, measure=measure, pricePolicy='selected_context_v1', averaging='none', firstFixingAt='none',
                                     paymentRule='explicit_payment_date_v1', unadjustedPayment='none'))
        occurrence = "NULL,NULL" if grid else "'payment/0','synthetic-terms'"
        return (f"INSERT INTO trading.side_schedule_rows(catalog_id,schedule_id,ordinal,local_key,role,operation,operation_version,output_kind,currency_catalog_id,currency_revision_id,currency_code,"
                f"occurrence_key,terms_digest,parameters,grid_ordinal) VALUES('40000000-0000-0000-0000-000000000001','{schedule}',{ordinal},'{key}','root','output_guard',1,'money',"
                f"'10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','usd',{occurrence},'{parameters}',{0 if grid else 'NULL'});")
    stored = f"(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.ordinal) FROM trading.side_schedule_rows r WHERE r.schedule_id='{schedule}')"
    query("BEGIN;SET LOCAL session_replication_role=replica;" + guard(0, 'gas/0/value', 'future_gas_delivery_v1', 'value_excluding_overdue_v1', True) +
          guard(1, 'payment/0/value', 'undue_payment_v1', 'present_value_v1', False) + f"CREATE TEMP TABLE in_term_before AS SELECT {stored} AS value;SET LOCAL session_replication_role=origin;" +
          migration_body(text) + f"""DO $check$ BEGIN
 IF (SELECT value FROM in_term_before) IS DISTINCT FROM {stored} OR (SELECT array_agg(parameters->>'lifecycle' ORDER BY ordinal) FROM trading.side_schedule_rows WHERE schedule_id='{schedule}')<>ARRAY['future_gas_delivery_v1','undue_payment_v1']
 THEN RAISE EXCEPTION 'A stored future-lifecycle composition changed during the in-term upgrade';END IF;
END $check$;ROLLBACK;""")
    print('PASS in-term upgrade: seven helper bodies, owners, ACLs and configurations are pinned; stored future-lifecycle programs are left unchanged', flush=True)
