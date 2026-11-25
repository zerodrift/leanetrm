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
    def write(kind, details, day='2021-01-01'):
        proposal = dict(context=after, tradeId=trade, expectedRevision=1, day=day, kind=kind, details=details)
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
    rows = read()
    assert [day['accounts'][0]['statement']['balance'] if day['accounts'][0]['statement'] else None for day in rows[0]['days']] == ['300', '300', '-200', None], rows
    earlier = {**after, 'knowledgeMode': 'retained', 'contextId': frozen['contextId']}
    assert read(earlier)[0]['days'][2]['accounts'][0]['statement'] is None
    c.call('date-schedules/context', dict(effectiveOn='2021-01-05', mode='current'))
    write('service', dict(obligationId=obligation, holderRole='counterparty', externalReference='synthetic-other-holder',
        expectedPreviousId=None, validFrom='2021-01-01', validTo='2021-01-05', active=True, statementPolicy=pair['statementPolicy'],
        termsReference='Synthetic counterparty service', evidence='Synthetic counterparty service evidence'))
    assert all([a['accountId'] for a in d['accounts']] == [account['id']] for d in read()[0]['days'])
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
    write('statement', dict(accountId=account['id'], serviceVersionId=account['version_id'], externalReference='synthetic-2021-01-01',
        expectedPreviousId=heads[0]['id'], balance=None, status='withdrawn', reportedAt='2021-01-05T00:00:00Z', includedMovementId=None,
        evidence='Synthetic withdrawn statement'))
    assert read()[0]['days'][0]['accounts'][0]['statement']['status'] == 'withdrawn'
    # Account and service scope is selected under both temporal axes.
    frozen_service = c.call('gas/quantities/read', dict(context=after, tradeId=trade, selectionPolicy='operational_projection_v1'))['context']
    write('service', dict(obligationId=obligation, holderRole='legal_entity', externalReference='synthetic-cashout-account',
        expectedPreviousId=account['version_id'], validFrom='2021-01-02', validTo='2021-01-05', active=True,
        statementPolicy=pair['statementPolicy'], termsReference='Synthetic revised service dates', evidence='Synthetic service correction'))
    assert not read()[0]['days'][0]['accounts']
    assert read({**after, 'knowledgeMode': 'retained', 'contextId': frozen_service['contextId']})[0]['days'][0]['accounts']
    earlier_business = {**after, 'snapshotId': initial_frame(c.call('workspace'), '2021-01-02')['id']}
    assert all(not day['accounts'] for day in read(earlier_business, at='2021-01-02T08:00:00Z')[0]['days'])
    # Operating permissions remain separate. Financial authority alone discloses
    # the exact same statement heads; losing trade authority refuses, not [] .
    query(f"DELETE FROM security.role_grants WHERE principal_id='{principal}' AND role_id IN(SELECT id FROM security.roles WHERE code IN('gas_operator','gas_service_admin'))")
    assert read()[0]['days'][1]['accounts'][0]['statement']['status'] == 'final'
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
