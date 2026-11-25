"""Independent synthetic cash-out booking, historical statements and authority checks."""
from copy import deepcopy
import json
from uuid import uuid4

from check_booked_side_programs import runtime_query
from check_gas_pricing import request, staged_booking
from preview_client import PreviewClient, initial_frame
from preview_fixture import CURRENCY


def run_checks(url, query, manifest, run_worker, admin_load):
    c = PreviewClient(url)
    c.bootstrap()
    c.call('profile', dict(profile='trader'))
    before, template = request(c)
    body = deepcopy(template)
    body['physicalSides'][0].update(deliveryFrom='2021-01-01', deliveryTo='2021-01-05')
    body['physicalSides'][0].pop('quantityPolicy')
    body['cashSides'] = []
    pair = dict(code='balance', profile='daily_index_cashout_v1', side='gas', index='synthetic_entry_daily',
                spreadOwedToUs='0', spreadOwedByUs='0', paymentDay=15,
                statementPolicy='final_statement_balance_v1', statementBasis='daily_imbalance_v1', accountNetting='net_accounts_v1',
                calendarPolicy=template['cashSides'][0]['calendarPolicy'])
    body['imbalanceCashouts'] = [pair]
    preview = c.call('gas/composition/preview', body)
    assert preview['unassessedDays'] == 0
    assert all(row['state'] == 'not_applicable' and row['amount'] is None for row in preview['imbalanceRows'])
    booked = c.call('gas/composition/book', body)
    assert c.call('gas/composition/book', body) == booked
    cat, trade, binding = manifest['catalogId'], booked['tradeId'], booked['instrumentId']
    stored = json.loads(query(f"SELECT jsonb_agg(jsonb_build_array(slot,role,balance_side,cashout_profile,statement_basis,account_netting,statement_policy) ORDER BY slot) FROM trading.gas_component_terms WHERE instrument_id='{binding}'"))
    assert stored == [[0, 'physical', None, None, None, None, None],
                      [1, 'imbalance_cashout', 'owed_to_us', pair['profile'], pair['statementBasis'], pair['accountNetting'], pair['statementPolicy']],
                      [2, 'imbalance_cashout', 'owed_by_us', pair['profile'], pair['statementBasis'], pair['accountNetting'], pair['statementPolicy']]], stored
    for field, value in [('side', 'missing'), ('statementBasis', 'unknown'), ('spreadOwedToUs', None), ('accountNetting', 'unknown')]:
        bad = deepcopy(body)
        bad['operationId'] = str(uuid4())
        if value is None:
            bad['imbalanceCashouts'][0].pop(field)
        else:
            bad['imbalanceCashouts'][0][field] = value
        assert c.call('gas/composition/book', bad, expected=422)['code'] == 'gas_composition_input'
        staged_booking(query, manifest, body, bad, 'gas_composition_input')
    linked = deepcopy(body)
    linked['operationId'] = str(uuid4())
    linked['physicalSides'].append({**linked['physicalSides'][0], 'code': 'linked', 'quantity': None,
                                    'link': dict(source='gas', formula='gross_less_fraction', fraction='0.02')})
    assert c.call('gas/composition/book', linked, expected=422)['code'] == 'gas_composition_input'

    after = {**before, 'snapshotId': initial_frame(c.call('workspace'), '2021-01-05')['id']}
    def read_binding(context=None, at='2021-01-05T08:00:00Z'):
        from native_read_transport import select_context_inputs
        source = c.call('gas/quantities/read', dict(context=context or after, tradeId=trade, selectionPolicy='operational_projection_v1'))['context']
        bind = select_context_inputs(cat, source) + f"SELECT security.use_market_context('{cat}','{source['contextId']}');SELECT security.use_trading_context('{cat}','{at}');"
        return bind
    def read(context=None, profile='trader', at='2021-01-05T08:00:00Z'):
        return json.loads(runtime_query(query, manifest, read_binding(context, at) + f"SELECT security.read_gas_valuation_balances('{cat}','{trade}','{binding}');", profile=profile).splitlines()[-1])

    # No account is a complete read with empty dated scopes, never a booking refusal.
    empty = read()
    assert len(empty) == 1 and all(not day['accounts'] for day in empty[0]['days']), empty
    principal = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader'")
    for role in ['gas_operator', 'gas_service_admin']:
        query(f"INSERT INTO security.role_grants SELECT '{uuid4()}','{principal}',id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false FROM security.roles WHERE code='{role}'")
    obligation = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    def write(kind, details, day='2021-01-01', trade_id=None):
        proposal = dict(context=after, tradeId=trade_id or trade, expectedRevision=1, day=day, kind=kind, details=details)
        preview = c.call('gas/operations/preview', proposal)
        command = dict(preview=proposal, operationId=str(uuid4()), previewHash=preview['previewHash'])
        path = 'gas/operations/configure' if kind == 'service' else 'gas/operations/record'
        result = c.call(path, command)
        assert c.call(path, command) == result
        return result
    own = write('service', dict(obligationId=obligation, holderRole='legal_entity', externalReference='synthetic-cashout-account',
        expectedPreviousId=None, validFrom='2021-01-01', validTo='2021-01-05', active=True, statementPolicy=pair['statementPolicy'],
        termsReference='Synthetic operator agreement', evidence='Synthetic service evidence'))
    accounts = c.call('gas/operations/read', dict(context=after, tradeId=trade, day='2021-01-01'))['state']['accounts']
    account = next(a for a in accounts if a['id'] == own['accountId'])
    heads = []
    frozen = None
    for day, amount in [('2021-01-01', '300'), ('2021-01-02', '300'), ('2021-01-03', '-200')]:
        if day == '2021-01-03':
            frozen = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))['context']
        heads.append(write('statement', dict(accountId=account['id'], serviceVersionId=account['version_id'], externalReference='synthetic-'+day,
            expectedPreviousId=None, balance=amount, status='final', reportedAt='2021-01-05T00:00:00Z', includedMovementId=None,
            evidence='Synthetic signed statement'), day))
    route_values(c, after, body, booked, [0, 1830, -640], 1, run_worker)
    rows = read()
    assert [day['accounts'][0]['statement']['balance'] if day['accounts'][0]['statement'] else None for day in rows[0]['days']] == ['300', '300', '-200', None], rows
    earlier = {**after, 'knowledgeMode': 'retained', 'contextId': frozen['contextId']}
    c.select_inputs(frozen)
    assert read(earlier)[0]['days'][2]['accounts'][0]['statement'] is None
    c.call('date-schedules/context', dict(effectiveOn='2021-01-05', mode='current'))
    before_d4 = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))['context']
    write('statement', dict(accountId=account['id'], serviceVersionId=account['version_id'], externalReference='synthetic-2021-01-04',
        expectedPreviousId=None, balance='-200', status='final', reportedAt='2021-01-05T00:00:00Z', includedMovementId=None,
        evidence='Synthetic late fourth day statement'), '2021-01-04')
    route_values(c, after, body, booked, [0, 1830, -1300], 0, run_worker, queued=False)
    c.select_inputs(before_d4)
    historical = c.call('gas/composition/editor', dict(context={**after, 'knowledgeMode': 'retained', 'contextId': before_d4['contextId']}, tradeId=trade))['valuation']
    assert historical['unassessedDays'] == 1 and historical['presentValue'] == '1190', historical
    c.call('date-schedules/context', dict(effectiveOn='2021-01-05', mode='current'))
    # The administrator loads the same cash-out profile on a started term.
    from gas_pricing_fixture import ADMINISTRATOR
    def load_pair(suffix, basis, index='synthetic_entry_daily', spread='0'):
        terms = deepcopy(body)
        terms.update(snapshotId=after['snapshotId'], operationId=str(uuid4()))
        terms['imbalanceCashouts'][0].update(statementBasis=basis, index=index, spreadOwedToUs=spread, spreadOwedByUs=spread)
        loaded = admin_load(dict(administrator=ADMINISTRATOR[0], composition=terms))
        assert loaded.returncode == 0, loaded.stderr
        held = json.loads(loaded.stdout)
        source = c.call('gas/quantities/read', dict(context=after, tradeId=held['tradeId'], selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
        owner = write('service', dict(obligationId=source, holderRole='legal_entity', externalReference='synthetic-'+suffix+'-account',
            expectedPreviousId=None, validFrom='2021-01-01', validTo='2021-01-05', active=True, statementPolicy=pair['statementPolicy'],
            termsReference='Synthetic service terms', evidence='Synthetic service evidence'), trade_id=held['tradeId'])
        accounts = c.call('gas/operations/read', dict(context=after, tradeId=held['tradeId'], day='2021-01-01'))['state']['accounts']
        version = next(account['version_id'] for account in accounts if account['id'] == owner['accountId'])
        for day, amount in [('2021-01-01', '300'), ('2021-01-02', '300'), ('2021-01-03', '-200')]:
            write('statement', dict(accountId=owner['accountId'], serviceVersionId=version, externalReference='synthetic-'+suffix+'-'+day,
                expectedPreviousId=None, balance=amount, status='final', reportedAt='2021-01-05T00:00:00Z', includedMovementId=None,
                evidence='Synthetic operator statement'), day, held['tradeId'])
        return terms, held, owner, version
    monthly, month_trade, month_account, month_version = load_pair('monthly', 'month_to_date_balance_v1')
    route_values(c, after, monthly, month_trade, [0, 900, -1600], 1, run_worker)
    delayed, delayed_trade, _, _ = load_pair('delayed', 'daily_imbalance_v1', 'synthetic_entry_delayed')
    forecast = route_values(c, after, delayed, delayed_trade, [0, 1830, -640], 1, run_worker, unpublished=True)
    assert forecast['measures']['unrealized'] == '-640' and forecast['measures']['realizedUnsettled'] == '1830'
    _, negative_trade, _, _ = load_pair('negative', 'daily_imbalance_v1', spread='-4')
    negative = c.call('gas/composition/editor', dict(context=after, tradeId=negative_trade['tradeId']))['valuation']
    negative_day = next(row for row in negative['imbalanceRows'] if row['day'] == '2021-01-01' and row['balanceSide'] == 'owed_to_us')
    assert negative_day['amount'] == '-300', negative_day
    payment = dict(operationId=str(uuid4()), tradeId=negative_trade['tradeId'], expectedRevision=1, slot=1,
        businessKey='synthetic-negative-index', paidAt='2021-01-05T01:00:00Z', currency=CURRENCY, amount='-300',
        allocations=[dict(occurrence=0, amount='-300')])
    try:
        runtime_query(query, manifest, f"SELECT security.record_gas_cash_payment('{cat}','{json.dumps(payment)}'::jsonb,'2021-01-05T08:00:00Z');")
    except RuntimeError as error:
        assert 'gas_payment_amount' in str(error), str(error)
    else:
        raise AssertionError('A payment against the entry direction at a negative price was accepted')
    counterparty = write('service', dict(obligationId=obligation, holderRole='counterparty', externalReference='synthetic-other-holder',
        expectedPreviousId=None, validFrom='2021-01-01', validTo='2021-01-05', active=True, statementPolicy=pair['statementPolicy'],
        termsReference='Synthetic counterparty service', evidence='Synthetic counterparty service evidence'))
    assert all([a['accountId'] for a in d['accounts']] == [account['id']] for d in read()[0]['days'])
    counterparty_version = next(a['version_id'] for a in c.call('gas/operations/read', dict(context=after, tradeId=trade, day='2021-01-01'))['state']['accounts'] if a['id'] == counterparty['accountId'])
    counterparty_head = write('statement', dict(accountId=counterparty['accountId'], serviceVersionId=counterparty_version,
        externalReference='synthetic-counterparty-statement', expectedPreviousId=None, balance='-100', status='final',
        reportedAt='2021-01-05T00:00:00Z', includedMovementId=None, evidence='Synthetic counterparty balance'))
    net = dict(operationId=str(uuid4()), tradeId=trade, expectedRevision=1, sourceSlot=0, month='2021-01-01',
               paidAt='2021-01-05T01:00:00Z', currency=CURRENCY, amount='1190',
               toUs=[dict(occurrence=0, amount='900'), dict(occurrence=1, amount='930')],
               byUs=[dict(occurrence=2, amount='-640')])
    def settle(command):
        wire = json.dumps(command).replace("'", "''")
        return json.loads(runtime_query(query, manifest, f"SELECT security.record_gas_cash_settlement('{cat}','{wire}'::jsonb,'2021-01-05T08:00:00Z');"))
    # A failed net conservation check rolls back both child payment facts.
    before_payments = query(f"SELECT count(*) FROM trading.gas_cash_payments WHERE trade_id='{trade}'")
    try:
        settle({**net, 'operationId': str(uuid4()), 'amount': '1191'})
    except RuntimeError as error:
        assert 'gas_settlement_amount' in str(error), str(error)
    else:
        raise AssertionError('A net amount inconsistent with its directional allocations was accepted')
    assert query(f"SELECT count(*) FROM trading.gas_cash_payments WHERE trade_id='{trade}'") == before_payments
    settled = settle(net)
    assert settle(net) == settled
    same = settle({**net, 'operationId': str(uuid4())})
    assert same['settlementId'] == settled['settlementId']
    amounts = json.loads(query(f"SELECT jsonb_agg(jsonb_build_array(p.slot,p.amount::text) ORDER BY p.slot) FROM trading.gas_cash_settlements s JOIN trading.gas_cash_payments p ON p.catalog_id=s.catalog_id AND p.id IN(s.to_us_payment_id,s.by_us_payment_id) WHERE s.id='{settled['settlementId']}'"))
    assert amounts == [[1, '1830'], [2, '-640']], amounts
    try:
        settle({**net, 'operationId': str(uuid4()), 'amount': '1200', 'toUs': [dict(occurrence=0, amount='910'), dict(occurrence=1, amount='930')]})
    except RuntimeError as error:
        assert 'gas_settlement_conflict' in str(error), str(error)
    else:
        raise AssertionError('A second net settlement of the month was accepted')
    correction = write('statement', dict(accountId=account['id'], serviceVersionId=account['version_id'], externalReference='synthetic-2021-01-01',
        expectedPreviousId=heads[0]['id'], balance='-100', status='final', reportedAt='2021-01-05T00:00:00Z', includedMovementId=None,
        evidence='Synthetic sign correction'))
    corrected = c.call('gas/composition/editor', dict(context=after, tradeId=trade))['valuation']
    received = next(row for row in corrected['imbalanceRows'] if row['day'] == '2021-01-01' and row['balanceSide'] == 'owed_to_us')
    paid = next(row for row in corrected['imbalanceRows'] if row['day'] == '2021-01-01' and row['balanceSide'] == 'owed_by_us')
    assert (received['accrued'], received['paid'], received['outstanding'], received['cashState']) == ('0', '900', '-900', 'Overpaid'), received
    assert paid['accrued'] == '-300', paid
    def transfer(buyer, buyer_version, seller, seller_version, owner_trade, reference):
        agreed = write('imbalance', dict(buyerAccountId=buyer, buyerVersionId=buyer_version, sellerAccountId=seller,
            sellerVersionId=seller_version, externalReference=reference, quantity='50', price='3',
            agreedAt='2021-01-05T01:00:00Z', dueAt='2021-02-02T00:00:00Z', decimalPlaces=2,
            roundingPolicy='currency_minor_units_to_even_v1', profile='paired_offsetting_imbalance_v1',
            evidence='Synthetic paired transfer'), trade_id=owner_trade)
        for side in ['counterparty', 'pipeline']:
            write('response', dict(nominationId=None, imbalanceId=agreed['id'], side=side, decision='approved', quantity='50',
                externalReference=reference+'-'+side, respondedAt='2021-01-05T02:00:00Z', expectedPreviousId=None,
                evidence='Synthetic transfer approval'), trade_id=owner_trade)
        write('imbalance_apply', dict(imbalanceId=agreed['id'], evidence='Synthetic accepted transfer'), trade_id=owner_trade)
    transfer(counterparty['accountId'], counterparty_version, month_account['accountId'], month_version,
        month_trade['tradeId'], 'synthetic-month-transfer')
    # This revised report excludes the earlier movement, so current balance is
    # +150. A second accepted movement sends 50 to the daily-basis owned account.
    write('statement', dict(accountId=counterparty['accountId'], serviceVersionId=counterparty_version,
        externalReference='synthetic-counterparty-statement', expectedPreviousId=counterparty_head['id'], balance='100', status='final',
        reportedAt='2021-01-05T00:00:00Z', includedMovementId=None, evidence='Synthetic corrected counterparty balance'))
    transfer(account['id'], account['version_id'], counterparty['accountId'], counterparty_version, trade, 'synthetic-daily-transfer')
    from check_portfolio_risk import VIEW
    holdings = c.call('trading/blotter', dict(context={**after, 'convention': 'trading_workspace_v1'}, view=VIEW))['holdings']
    assert next(row for row in holdings if row['tradeId'] == month_trade['tradeId'])['unavailableReason'] == 'gas_imbalance_transfer'
    assert next(row for row in holdings if row['tradeId'] == trade)['presentValue'] == '-1710'
    write('statement', dict(accountId=account['id'], serviceVersionId=account['version_id'], externalReference='synthetic-2021-01-01',
        expectedPreviousId=correction['id'], balance=None, status='withdrawn', reportedAt='2021-01-05T00:00:00Z', includedMovementId=None,
        evidence='Synthetic withdrawn statement'))
    assert read()[0]['days'][0]['accounts'][0]['statement']['status'] == 'withdrawn'
    withdrawn = c.call('gas/composition/editor', dict(context=after, tradeId=trade))['valuation']
    paid_unknown = next(row for row in withdrawn['imbalanceRows'] if row['day'] == '2021-01-01' and row['balanceSide'] == 'owed_to_us')
    assert paid_unknown['cashState'] == 'UnassessedPaid' and paid_unknown['paid'] == '900' and paid_unknown['accrued'] is None and paid_unknown['outstanding'] is None, paid_unknown
    assert paid_unknown['statements'][0]['status'] == 'withdrawn'
    # Account and service scope is selected under both temporal axes.
    frozen_service = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))['context']
    write('service', dict(obligationId=obligation, holderRole='legal_entity', externalReference='synthetic-cashout-account',
        expectedPreviousId=account['version_id'], validFrom='2021-01-02', validTo='2021-01-05', active=True,
        statementPolicy=pair['statementPolicy'], termsReference='Synthetic revised service dates', evidence='Synthetic service correction'))
    assert not read()[0]['days'][0]['accounts']
    c.select_inputs(frozen_service)
    assert read({**after, 'knowledgeMode': 'retained', 'contextId': frozen_service['contextId']})[0]['days'][0]['accounts']
    c.call('date-schedules/context', dict(effectiveOn='2021-01-05', mode='current'))
    earlier_business = {**after, 'snapshotId': initial_frame(c.call('workspace'), '2021-01-02')['id']}
    assert all(not day['accounts'] for day in read(earlier_business, at='2021-01-02T08:00:00Z')[0]['days'])
    # Operating permissions remain separate. Financial authority alone discloses
    # the exact same statement heads; losing trade authority refuses, not [] .
    query(f"DELETE FROM security.role_grants WHERE principal_id='{principal}' AND role_id IN(SELECT id FROM security.roles WHERE code IN('gas_operator','gas_service_admin'))")
    assert read()[0]['days'][1]['accounts'][0]['statement']['status'] == 'final'
    financial_only = c.call('gas/composition/editor', dict(context=after, tradeId=trade))['valuation']
    assert financial_only['incomplete'] and financial_only['unassessedDays'] == 1
    denied = f"DO $test$ BEGIN BEGIN PERFORM security.read_gas_valuation_balances('{cat}','{trade}','{binding}');RAISE EXCEPTION 'read_disclosed';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$test$;"
    runtime_query(query, manifest, read_binding() + f"RESET SESSION AUTHORIZATION;DELETE FROM security.role_grants WHERE principal_id='{principal}';SET LOCAL SESSION AUTHORIZATION preview_runtime;" + denied, commit=False)
    worker_scope(c, query, manifest, before, booked)
    print('PASS cash-out native booking: complete immutable pair, replay, independent refusals and scoped statement heads', flush=True)


def worker_scope(c, query, manifest, context, booked):
    cat, trade, binding = manifest['catalogId'], booked['tradeId'], booked['instrumentId']
    reader = f"SELECT security.read_gas_valuation_balances('{cat}','{trade}','{binding}');"
    denied = f"DO $test$ BEGIN BEGIN PERFORM security.read_gas_valuation_balances('{cat}','{trade}','{binding}');RAISE EXCEPTION 'read_disclosed';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$test$;"
    query('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;' + denied + 'ROLLBACK;')
    c = PreviewClient(c.url)
    c.bootstrap()
    c.call('profile', dict(profile='risk'))
    scope = dict(context={**context, 'convention': 'trading_workspace_v1'}, bookIds=[], portfolioIds=[], tradeIds=[trade])
    catalog = c.call('portfolio/risk/catalog', scope)
    request = {**scope, 'context': {**scope['context'], 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']},
               'detail': 'holdings', 'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
               'pricingPolicy': catalog['pricingPolicy'], 'stress': [],
               'bumps': [dict(factor=f['factor'], size='1', convention=catalog['bumpConvention']) for f in catalog['factors']]}
    job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request))
    engine = query(f"SELECT request->>'engineVersion' FROM risk.jobs WHERE id='{job['id']}'")
    claim = json.loads(query(f"SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.claim(120,'{engine}');"))
    assert claim['jobId'] == job['id']
    bind = f"SELECT risk.bind('{job['id']}','{claim['leaseToken']}');"
    def worker(statement):
        return query('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;' + bind + statement + 'ROLLBACK;')
    assert len(json.loads(worker(reader).splitlines()[-1])) == 1
    for statement in [reader.replace(binding, str(uuid4())), reader.replace(trade, str(uuid4())), reader.replace(cat, str(uuid4()))]:
        # Inspect SQLSTATE in the native session instead of accepting any error.
        perform = statement.replace('SELECT ', 'PERFORM ', 1)
        worker("DO $test$ BEGIN BEGIN " + perform + "RAISE EXCEPTION 'read_disclosed';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$test$;")
    drift = f"RESET SESSION AUTHORIZATION;UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{job['id']}';SET LOCAL SESSION AUTHORIZATION preview_risk_worker;"
    worker(drift + denied)
    query(f"SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.finish('{job['id']}','{claim['leaseToken']}',NULL,'synthetic_probe_complete');")
    print('PASS cash-out statement authority: active worker lease, exact trade/binding/catalog scope and SQLSTATE 42501 outside it', flush=True)


def upgrade_checks(query, migration):
    from check_fact_anchor_upgrade import run_checks
    run_checks(query, migration, number='0401', guard='gas_imbalance_upgrade', signatures=(
        'security.write_gas_composition(uuid,jsonb,timestamptz,boolean)',
        'trading.request_side_calendar_policy(jsonb,integer)',
        'security.record_gas_cash_payment(uuid,jsonb,timestamptz)',
        'security.publish_admitted_side_schedules(uuid,uuid,uuid)'))


def route_values(c, context, body, booked, expected, missing, run_worker, queued=True, unpublished=False):
    from fractions import Fraction
    from check_portfolio_risk import VIEW, financial
    trade = booked['tradeId']
    value = c.call('gas/composition/editor', dict(context=context, tradeId=trade))['valuation']
    held_preview = c.call('gas/composition/preview', {**body, 'snapshotId': context['snapshotId'], 'previewTradeId': trade})
    assert [side['presentValue'] for side in held_preview['sides']] == [side['presentValue'] for side in value['sides']]
    assert held_preview['unassessedDays'] == missing and held_preview['incomplete'] == bool(missing)
    assert [Fraction(side['presentValue']) for side in value['sides']] == expected, value
    assert [side['incomplete'] for side in value['sides']] == [False, bool(missing), bool(missing)], value['sides']
    assert value['unassessedDays'] == missing and value['incomplete'] == bool(missing), value
    ticket = c.call('tickets/trade', dict(context=context, tradeId=trade, bindingId=booked['instrumentId'], revision=1))
    assert ticket['imbalanceRows'] == value['imbalanceRows']
    assert ticket['unassessedDays'] == missing and ticket['incomplete'] == bool(missing)
    assert ticket['scheduleEntries'] == value['scheduleEntries']
    assert [(entry['code'], entry['owner']) for entry in ticket['scheduleEntries']] == [('gas', 'gas'), ('balance.to_us', 'balance'), ('balance.by_us', 'balance')]
    assert set(ticket['sideSlots']) == {entry['code'] for entry in ticket['scheduleEntries']}
    assert value['schedule'] is not None, value.get('scheduleRefusal')
    day = next(row for row in value['schedule']['rows'] if row['key'] == '2021-01-04')
    if missing:
        assert day['cells']['balance.to_us.quantity']['value'] is None
        assert day['cells']['balance.by_us.amount']['value'] is None
    assert value['schedule']['unassessedDays'] == missing
    row = next(row for row in value['imbalanceRows'] if row['day'] == '2021-01-01' and row['entry'] == 'balance.to_us')
    assert row['quantity'] == '300' and row['reportedBalance'] == '300' and row['published'] and row['amount'] == '900', row
    shared = {**context, 'convention': 'trading_workspace_v1'}
    blotter = next(row for row in c.call('trading/blotter', dict(context=shared, view=VIEW))['holdings'] if row['tradeId'] == trade)
    assert Fraction(blotter['presentValue']) == sum(expected) and blotter['unassessedDays'] == missing and blotter['incomplete'] == bool(missing), blotter
    # The installed sensitivity request requires an explicit active factor.
    # Keep a one-unit future delivery beside the fully fixed cash-out; assertions
    # still inspect the cash-out's own values and the whole portfolio coverage.
    if not hasattr(c, '_cashout_probe'):
        probe = deepcopy(body)
        probe.update(operationId=str(uuid4()), profile='fixed_gas_composition_v1')
        probe.pop('imbalanceCashouts')
        probe['cashSides'] = []
        probe['physicalSides'][0].update(deliveryFrom='2021-01-06', deliveryTo='2021-01-07', quantity='1')
        c._cashout_probe = c.call('gas/composition/book', probe)['tradeId']
    c.call('profile', dict(profile='risk'))
    scope = dict(context=shared, bookIds=[], portfolioIds=[], tradeIds=[trade, c._cashout_probe])
    catalog = c.call('portfolio/risk/catalog', scope)
    request = {**scope, 'context': {**shared, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']},
        'detail': 'holdings', 'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'],
        'pricingPolicy': catalog['pricingPolicy'], 'stress': [],
        'bumps': [dict(factor=f['factor'], size='0.1', convention=catalog['bumpConvention']) for f in catalog['factors'][:16]]}
    result = c.call('portfolio/risk', request)
    assert result['unassessedDays'] == missing and result['incomplete'] == bool(missing), result
    for factor in result['factors']:
        assert factor['totals'][0]['unassessedDays'] == missing
        assert Fraction(factor['totals'][0]['values']['base']) == sum(expected) + Fraction('3.10')
        position = next(position for position in factor['positions'] if position['tradeId'] == trade)
        assert Fraction(position['values']['base']) == sum(expected)
        assert [Fraction(entry['base']) for entry in position['entries']] == expected
        assert position['unassessedDays'] == missing and position['incomplete'] == bool(missing)
        expected_delta = -200 if unpublished and factor['factor']['deliveryFrom'] == '2021-01-03' else 0
        assert Fraction(position['values']['delta']) == expected_delta, (factor['factor'], position)
        assert Fraction(position['components']['realizedUnsettled']['up']) == Fraction(position['components']['realizedUnsettled']['base'])
    # Alternate risk presentations retain the same base coverage marker.
    joint_request = {**request, 'bumps': [], 'joint': dict(convention='portfolio_joint_risk_v1', derivatives=None,
        scenarios=dict(id='synthetic_cashout_stress', version='1', convention='independent_joint_absolute_curve_scenarios_v1',
            scenarios=[dict(id='up', label='Synthetic upward mark', shifts=[dict(factor=request['bumps'][0]['factor'], size='0.1',
                convention='absolute_curve_coordinate_parent_additive_v1')])]))}
    joint = c.call('portfolio/risk', joint_request)
    assert joint['unassessedDays'] == missing and joint['incomplete'] == bool(missing), joint
    assert joint['joint']['baseTotals'][0]['unassessedDays'] == missing
    if queued:
        job = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request))
        run_worker()
        done = c.call('portfolio/risk/jobs/' + job['id'])
        assert done['state'] == 'completed' and financial(done['result']) == financial(result), done
        assert done['result']['unassessedDays'] == missing
    c.call('profile', dict(profile='trader'))
    print('PASS cash-out routes: exact entry values, unknown days, incomplete totals, statement evidence and retained queued risk', flush=True)
    return value


def browser_authority(query, manifest):
    """Explicit operating authority for the synthetic browser operator."""
    principal = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader'")
    for role in ['gas_operator', 'gas_service_admin']:
        query(f"INSERT INTO security.role_grants SELECT '{uuid4()}','{principal}',id,'{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '1 hour',false FROM security.roles WHERE code='{role}'")
