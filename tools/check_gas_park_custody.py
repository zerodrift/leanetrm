"""Qualify retained customer title, foreign custody, reservations and independent cash."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from check_reference_editing import main
from gas_park_custody_fixture import CustodyFixture, PROFILE, fixture
from gas_park_loan_fixture import rejected, literal
from gas_park_custody_valuation import checkpoint, directory_refusal, fee_input_demand, opaque_keys


def checks(query, manifest):
    f = CustodyFixture(query, manifest)
    owned, book = f.account('custody_customer')
    title_policy = f.bind(owned, 'gas.owned.receive_title')
    title = f.evidence(owned, 'custody_customer_owned', 'title_receipt', '1000', '2020-12-18T08:00:00Z')
    lot = f.native(owned, 'receive_title', dict(evidenceId=title, quantity='1000', titleAt='2020-12-18T08:00:00Z',
        availableAt='2020-12-18T08:00:00Z', originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=title_policy)
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    booked = f.book(f.booking(book, 'customer', owned))
    state = f.read(booked['tradeId'])
    assert state['implementation'] == PROFILE and state['acceptance'] is None
    assert state['balances']['inCustody'] == '0'
    assert state['cash'] is None, 'Booking a prospective custody agreement created accepted cash.'
    held = f.holdings(book)
    assert len(held['positions']) == 1 and held['positions'][0]['id'] == lot['id']
    assert held['positions'][0]['remaining_quantity'] == '1000'
    assert query(f"SELECT count(*) FROM trading.gas_owned_reservations WHERE(catalog_id,lot_id)=('{f.catalog}','{lot['id']}');") == '0'
    assert query(f"SELECT count(*) FROM trading.gas_owned_loans WHERE catalog_id='{f.catalog}';") == '0'
    publication = f.publication()
    for source_key in ['synthetic\ncontrol', 'synthetic\u0085control']:
        rejected(lambda: f.custody_evidence(booked['tradeId'], source_key, 'custody_agreement', '720',
            '2020-12-19T08:00:00Z'), 'gas_custody_evidence')
    assert f.publication() == publication and f.read(booked['tradeId'])['evidence'] == []
    padded = f.custody_evidence(booked['tradeId'], ' synthetic_canonical_key ', 'custody_agreement', '720', '2020-12-19T08:00:00Z')
    assert f.read(booked['tradeId'])['evidence'][0]['sourceKey'] == 'synthetic_canonical_key'
    publication = f.publication()
    assert f.custody_evidence(booked['tradeId'], 'synthetic_canonical_key', 'custody_agreement', '720',
        '2020-12-19T08:00:00Z')['id'] == padded['id'] and f.publication() == publication
    raw = dict(sourceKey='\u00a0synthetic_opaque_key\u00a0', expectedRevision=0, kind='custody_agreement', quantity='720',
        observedAt='2020-12-19T08:00:00Z', evidence='Synthetic evidence\nretains its explicit second line')
    f.call(f"SELECT security.record_gas_park_custody_evidence('{f.catalog}','{uuid4()}','{booked['tradeId']}',{literal(raw)},'{f.at}','{f.capture()}');")
    assert any(row['sourceKey'] == raw['sourceKey'] and row['evidence'] == raw['evidence'] for row in f.read(booked['tradeId'])['evidence'])
    invalid = dict(id=str(uuid4()), operation_id=str(uuid4()), source_key='synthetic\nraw_table_key', revision=1, previous_id=None)
    rejected(lambda: query("BEGIN;INSERT INTO trading.gas_park_custody_evidence SELECT(jsonb_populate_record(NULL::trading.gas_park_custody_evidence,"
        f"to_jsonb(e)||{literal(invalid)})).* FROM trading.gas_park_custody_evidence e WHERE catalog_id='{f.catalog}' LIMIT 1;ROLLBACK;"), 'source_key_check')
    print('PASS native evidence canonicalizes ASCII key padding, refuses C0/C1 key controls, and preserves opaque NBSP keys and multiline free evidence.', flush=True)
    checkpoint(f, booked, book, '-12.5', '2987.5', [lot['id']], 'customer_booked')
    print('PASS custody booking retains customer stock1000, creates no reservation before acceptance, and creates no loan or actual fee.', flush=True)
    role = query("SELECT r.role_id FROM security.role_permissions r WHERE r.permission='AmendTrade' AND EXISTS(SELECT 1 FROM security.role_grants g "
        "JOIN security.identity_bindings i ON i.principal_id=g.principal_id WHERE g.role_id=r.role_id AND i.subject='trader');")
    query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadGasOperations');")
    try:
        missing_operation = str(uuid4())
        missing = json.loads(f.call(f"SELECT security.gas_park_custody_booking_status('{f.catalog}','{missing_operation}')::text;"))
        assert missing['found'] is False and missing['inventory'] is False and missing['authorityUntil']
        f.call(f"SELECT security.require_gas_park_custody_booking_status('{f.catalog}','{missing_operation}','{missing['authorityUntil']}',false,false);")
        recovered = json.loads(f.call(f"SELECT security.gas_park_custody_booking_status('{f.catalog}','{booked['operationId']}')::text;"))
        assert recovered['found'] and recovered['outcome']['tradeId'] == booked['tradeId']
        assert recovered['inventory'] is True
        f.call(f"SELECT security.require_gas_park_custody_booking_status('{f.catalog}','{booked['operationId']}','{recovered['authorityUntil']}',true,true);")
    finally:
        query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOperations');")
    print('PASS booking status retains BookTrade admission without adding custody operations permission.', flush=True)
    raw_permissions = ['AmendTrade', 'ReadGasOwnedHoldings', 'RecordGasOwnedHoldings']
    query(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission IN('AmendTrade','ReadGasOwnedHoldings','RecordGasOwnedHoldings');")
    try:
        observed = f.custody_evidence(booked['tradeId'], 'synthetic_agreement', 'custody_agreement', '720', '2020-12-19T08:00:00Z')
        assert observed['revision'] == 1
    finally:
        for permission in raw_permissions:
            query(f"INSERT INTO security.role_permissions VALUES('{role}','{permission}');")
    print('PASS a custody operator records raw customer evidence without owned-gas or bank-payment amendment authority.', flush=True)
    query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','AmendTrade');")
    assert f.call("SELECT security.has_permission('AmendTrade');") == 'f'
    reserve_policy = f.bind(owned, 'gas.owned.reserve')
    release_policy = f.bind(owned, 'gas.owned.release')
    accept_policy, return_policy = f.custody_policy('accept'), f.custody_policy('return')
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    receipt = f.custody_evidence(booked['tradeId'], 'synthetic_custody_arrived', 'custody_receipt', '720', '2020-12-20T10:00:00Z')
    state = f.read(booked['tradeId'])
    checkpoint(f, booked, book, '-12.5', '2987.5', [lot['id']], 'customer_before_acceptance')
    accept_context, accept_operation = f.capture(), str(uuid4())
    accept_body = dict(expectedVersion=state['stateVersion'], agreementEvidenceId=observed['id'], receiptEvidenceId=receipt['id'],
        effectiveAt='2020-12-20T10:00:00Z', allocations=[dict(lotId=lot['id'], quantity='720')])
    accepted = f.record(booked['tradeId'], 'accept', accept_body, accept_policy, context=accept_context, operation=accept_operation)
    publication = f.publication()
    acknowledged = f.record(booked['tradeId'], 'accept', accept_body, accept_policy, context=accept_context)
    assert acknowledged['changed'] is False and acknowledged['id'] == accepted['id'] and acknowledged['quantity'] == '720'
    assert f.publication() == publication
    before = f.read(booked['tradeId'], context=accept_context)
    assert before['acceptance'] is None and before['balances']['inCustody'] == '0' and before['cash'] is None
    assert f.record(booked['tradeId'], 'accept', accept_body, accept_policy, context=accept_context, operation=accept_operation) == accepted
    print('PASS a fresh semantic acknowledgement returns its original sealed event even with the original pre-event source cut; the old read still has custody0/no cash and exact replay is unchanged.', flush=True)
    f.at = '2020-12-20T09:00:00Z'
    early_ack = f.record(booked['tradeId'], 'accept', accept_body, accept_policy, context=accept_context)
    assert early_ack['changed'] is False and early_ack['id'] == accepted['id'] and early_ack['custodyAfter'] == '720'
    early_view = f.read(booked['tradeId'], context=accept_context)
    assert early_view['acceptance'] is None and early_view['balances']['inCustody'] == '0' and early_view['cash'] is None
    f.at = '2020-12-20T16:00:00Z'
    print('PASS a fresh semantic acknowledgement can prove its sealed event beyond the selected business instant without making it actual in that view.', flush=True)
    state = f.read(booked['tradeId'])
    assert state['balances']['inCustody'] == '720' and state['balances']['reserved'] == '720'
    assert state['cash']['amount'] == '-12.5' and len(state['reservations']) == 1
    checkpoint(f, booked, book, '-12.5', '2987.5', [lot['id']], 'customer_accepted')
    assert f.holdings(book)['positions'][0]['remaining_quantity'] == '1000'
    rejected(lambda: f.native(owned, 'release', dict(reservationId=state['reservations'][0]['reservationId'],
        effectiveAt='2020-12-20T11:00:00Z'), policy=release_policy), 'gas_custody_reservation_protected')
    delivery = f.evidence(owned, 'synthetic_custody_forbidden_sale', 'physical_delivery', '10', '2020-12-20T11:00:00Z')
    dispatch_policy = f.bind(owned, 'gas.owned.dispatch')
    rejected(lambda: f.native(owned, 'dispatch', dict(lotId=lot['id'], evidenceId=delivery, quantity='10',
        reservationId=state['reservations'][0]['reservationId'], effectiveAt='2020-12-20T11:00:00Z'),
        policy=dispatch_policy), 'gas_custody_reservation_protected')
    # Free stock remains independently usable under its own title/physical evidence.
    f.native(owned, 'dispatch', dict(lotId=lot['id'], evidenceId=delivery, quantity='10',
        reservationId=None, effectiveAt='2020-12-20T11:00:00Z'), policy=dispatch_policy)
    returned = f.custody_evidence(booked['tradeId'], 'synthetic_custody_return_one', 'custody_return', '270', '2020-12-20T12:00:00Z')
    state = f.read(booked['tradeId'])
    return_body = dict(expectedVersion=state['stateVersion'], businessKey=' synthetic_return_one ',
        evidenceId=returned['id'], quantity='270', effectiveAt='2020-12-20T12:00:00Z',
        allocations=[dict(reservationId=state['reservations'][0]['reservationId'], quantity='270')])
    f.record(booked['tradeId'], 'return', return_body, return_policy)
    state = f.read(booked['tradeId'])
    assert state['returns'][0]['businessKey'] == 'synthetic_return_one'
    publication = f.publication()
    assert f.record(booked['tradeId'], 'return', {**return_body, 'businessKey': 'synthetic_return_one'}, return_policy)['changed'] is False
    for business_key in ['synthetic\nreturn', 'synthetic\u0085return']:
        rejected(lambda: f.record(booked['tradeId'], 'return', {**return_body, 'businessKey': business_key}, return_policy), 'gas_custody_input')
    invalid = dict(id=str(uuid4()), operation_id=str(uuid4()), business_key='synthetic\u0085raw_table_return')
    rejected(lambda: query("BEGIN;INSERT INTO trading.gas_park_custody_returns SELECT(jsonb_populate_record(NULL::trading.gas_park_custody_returns,"
        f"to_jsonb(r)||{literal(invalid)})).* FROM trading.gas_park_custody_returns r WHERE catalog_id='{f.catalog}' LIMIT 1;ROLLBACK;"), 'business_key_check')
    assert f.publication() == publication
    assert state['balances']['inCustody'] == '450' and state['reservations'][0]['quantity'] == '450'
    assert state['cash']['amount'] == '-12.5' and f.holdings(book)['positions'][0]['remaining_quantity'] == '990'
    checkpoint(f, booked, book, '-12.5', '2957.5', [lot['id']], 'customer_partial_return')
    print('PASS custody720 then return270 preserves independent title and cash; generic reserved dispatch refuses while free stock10 remains usable.', flush=True)
    old_context = f.capture()
    late = f.custody_evidence(booked['tradeId'], 'synthetic_earlier_return', 'custody_return', '1', '2020-12-20T11:30:00Z')
    state = f.read(booked['tradeId'])
    rejected(lambda: f.record(booked['tradeId'], 'return', dict(expectedVersion=state['stateVersion'], businessKey='synthetic_late_report',
        evidenceId=late['id'], quantity='1', effectiveAt='2020-12-20T11:30:00Z',
        allocations=[dict(reservationId=state['reservations'][0]['reservationId'], quantity='1')]), return_policy), 'gas_custody_return_reconciliation_required')
    expired = dict(operationId=str(uuid4()), policyId=reserve_policy, expectedRevision=1, action='gas.owned.reserve', mode='manual',
        fromAt='2020-01-01T00:00:00Z', untilAt='2020-12-19T00:00:00Z', implementation='explicit_lifecycle_action_policy_v1')
    f.call(f"SELECT lifecycle.save_policy('{f.catalog}',{literal(expired)});")
    partial = f.custody_evidence(booked['tradeId'], 'synthetic_missing_reserve_right', 'custody_return', '1', '2020-12-20T12:00:00Z')
    state = f.read(booked['tradeId'])
    rejected(lambda: f.record(booked['tradeId'], 'return', dict(expectedVersion=state['stateVersion'], businessKey='synthetic_partial_without_reserve',
        evidenceId=partial['id'], quantity='1', effectiveAt='2020-12-20T12:00:00Z',
        allocations=[dict(reservationId=state['reservations'][0]['reservationId'], quantity='1')]), return_policy), 'gas_custody_policy_changed')
    final = f.custody_evidence(booked['tradeId'], 'synthetic_final_return', 'custody_return', '450', '2020-12-20T12:00:00Z')
    state = f.read(booked['tradeId'])
    f.record(booked['tradeId'], 'return', dict(expectedVersion=state['stateVersion'], businessKey='synthetic_final',
        evidenceId=final['id'], quantity='450', effectiveAt='2020-12-20T12:00:00Z',
        allocations=[dict(reservationId=state['reservations'][0]['reservationId'], quantity='450')]), return_policy)
    state = f.read(booked['tradeId'])
    assert state['balances']['inCustody'] == '0' and state['reservations'] == [] and state['cash']['amount'] == '-12.5'
    assert f.read(booked['tradeId'], context=old_context)['balances']['inCustody'] == '450'
    checkpoint(f, booked, book, '-12.5', '2957.5', [lot['id']], 'customer_final_return')
    query(f"INSERT INTO security.role_permissions VALUES('{role}','AmendTrade');")
    print('PASS a customer operator without AmendTrade accepts and returns custody; equal-instant final return releases remainder once, earlier application refuses, and old knowledge retains450.', flush=True)

    # Provider foreign custody has no inventory account, mark or owned-gas permission.
    provider_book = f.book_scope('foreign_custody')
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    body = f.booking(provider_book, 'provider', None, direction='receive')
    body['point'] = 'synthetic_gas_point_unpriced'
    owned_rows = query(f"SELECT count(*) FROM trading.gas_owned_accounts WHERE catalog_id='{f.catalog}';")
    permissions = ['AmendTrade', 'ReadGasOwnedHoldings', 'RecordGasOwnedHoldings']
    query(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission IN ('AmendTrade','ReadGasOwnedHoldings','RecordGasOwnedHoldings');")
    try:
        assert f.call("SELECT security.has_permission('ReadGasOwnedHoldings') OR security.has_permission('RecordGasOwnedHoldings') OR security.has_permission('AmendTrade');") == 'f'
        provider = f.book(body)
        checkpoint(f, provider, provider_book, '12.5', '12.5', [], 'provider_booked_no_gas_inputs', provider=True)
        agreement = f.custody_evidence(provider['tradeId'], 'synthetic_provider_agreement', 'custody_agreement', '720', '2020-12-19T08:00:00Z')
        f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
        arrived = f.custody_evidence(provider['tradeId'], 'synthetic_provider_received', 'custody_receipt', '720', '2020-12-20T10:00:00Z')
        state = f.read(provider['tradeId'])
        f.record(provider['tradeId'], 'accept', dict(expectedVersion=state['stateVersion'], agreementEvidenceId=agreement['id'],
            receiptEvidenceId=arrived['id'], effectiveAt='2020-12-20T10:00:00Z', allocations=[]), accept_policy)
        state = f.read(provider['tradeId'])
        assert state['ownedAccountId'] is None and state['reservations'] == []
        assert state['balances']['inCustody'] == '720' and state['balances']['reserved'] == '0' and state['cash']['amount'] == '12.5'
        checkpoint(f, provider, provider_book, '12.5', '12.5', [], 'provider_accepted_no_gas_inputs', provider=True)
        returned = f.custody_evidence(provider['tradeId'], 'synthetic_provider_returned', 'custody_return', '270', '2020-12-20T12:00:00Z')
        state = f.read(provider['tradeId'])
        f.record(provider['tradeId'], 'return', dict(expectedVersion=state['stateVersion'], businessKey='synthetic_provider_return',
            evidenceId=returned['id'], quantity='270', effectiveAt='2020-12-20T12:00:00Z', allocations=[]), return_policy)
        state = f.read(provider['tradeId'])
        assert state['balances']['inCustody'] == '450' and state['cash']['amount'] == '12.5'
        checkpoint(f, provider, provider_book, '12.5', '12.5', [], 'provider_partial_no_gas_inputs', provider=True)
        payment = dict(operationId=str(uuid4()), tradeId=provider['tradeId'], bindingId=state['cash']['bindingId'],
            allocationId=str(uuid4()), expectedRevision=0, policy='explicit_paid_cash_nominal_due_v1', paidAt='2020-12-20T15:00:00Z', amount='12.5')
        rejected(lambda: f.call(f"SELECT security.record_cash_payment('{f.catalog}',{literal(payment)},'{f.at}');"), 'cash_payment_denied')
        assert query(f"SELECT count(*) FROM trading.gas_owned_accounts WHERE catalog_id='{f.catalog}';") == owned_rows
    finally:
        for permission in permissions:
            query(f"INSERT INTO security.role_permissions VALUES('{role}','{permission}');")
    print('PASS provider books/observes/accepts/partially returns unpriced foreign gas without owned account, mark, owned-gas or AmendTrade; shared bank payment separately denies.', flush=True)
    query("""CREATE FUNCTION trading.synthetic_custody_bad_context(catalog uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
    DECLARE c trading.gas_park_custody_commands;op uuid=gen_random_uuid();BEGIN
      SELECT * INTO STRICT c FROM trading.gas_park_custody_commands WHERE catalog_id=catalog AND action='return' LIMIT 1;
      INSERT INTO trading.gas_park_custody_commands VALUES(catalog,op,c.trade_id,c.actor_id,c.action,
        jsonb_set(c.request,'{contextId}',to_jsonb(gen_random_uuid())),c.outcome||jsonb_build_object('operationId',op,'changed',false),
        clock_timestamp()+interval '30 seconds',c.policy_id,c.policy_revision);
    END$$;ALTER FUNCTION trading.synthetic_custody_bad_context(uuid) OWNER TO openetrm_security_owner;
    GRANT EXECUTE ON FUNCTION trading.synthetic_custody_bad_context(uuid) TO openetrm_runtime;""")
    rejected(lambda: f.call(f"SELECT trading.synthetic_custody_bad_context('{f.catalog}');SET CONSTRAINTS ALL IMMEDIATE;", rollback=True), 'gas_custody_context')
    print('PASS a fresh semantic acknowledgement cannot assert a nonexistent source context.', flush=True)
    payment['operationId'], payment['allocationId'], payment['amount'] = str(uuid4()), str(uuid4()), '13'
    rejected(lambda: f.call(f"SELECT security.record_cash_payment('{f.catalog}',{literal(payment)},'{f.at}');SET CONSTRAINTS ALL IMMEDIATE;", rollback=True), 'gas_custody_cash_payment')
    print('PASS ordinary custody fee payment independently enforces signed accepted cash capacity.', flush=True)

    first = {**payment, 'operationId': str(uuid4()), 'allocationId': str(uuid4()), 'amount': '13', 'expectedRevision': 0}
    second = {**first, 'operationId': str(uuid4()), 'amount': '5', 'expectedRevision': 1}
    rejected(lambda: f.call(f"SELECT security.record_cash_payment('{f.catalog}',{literal(first)},'{f.at}');SELECT security.record_cash_payment('{f.catalog}',{literal(second)},'{f.at}');SET CONSTRAINTS ALL IMMEDIATE;", rollback=True), 'gas_custody_cash_payment')
    print('PASS an oversized payment revision cannot be hidden by a same-transaction smaller correction.', flush=True)

    query("""CREATE FUNCTION trading.synthetic_custody_bad_receipt(catalog uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
    DECLARE c trading.gas_park_custody_commands;op uuid=gen_random_uuid();BEGIN
      SELECT * INTO STRICT c FROM trading.gas_park_custody_commands WHERE catalog_id=catalog AND action='return' LIMIT 1;
      INSERT INTO trading.gas_park_custody_commands VALUES(catalog,op,c.trade_id,c.actor_id,c.action,
        jsonb_set(c.request,'{body,quantity}','"999"'),c.outcome||jsonb_build_object('operationId',op,'changed',false),
        clock_timestamp()+interval '30 seconds',c.policy_id,c.policy_revision);
    END$$;ALTER FUNCTION trading.synthetic_custody_bad_receipt(uuid) OWNER TO openetrm_security_owner;
    GRANT EXECUTE ON FUNCTION trading.synthetic_custody_bad_receipt(uuid) TO openetrm_runtime;""")
    rejected(lambda: f.call(f"SELECT trading.synthetic_custody_bad_receipt('{f.catalog}');SET CONSTRAINTS ALL IMMEDIATE;", rollback=True), 'gas_custody_receipt')
    print('PASS a future definer cannot attach a forged quantity receipt to a real accepted return.', flush=True)

    f.at, f.day = '2020-12-18T16:00:00Z', '2020-12-18'
    early = f.capture()
    fence_sql = f"SELECT security.use_market_context('{f.catalog}','{early}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
    def fence(horizon, selected):
        trades = 'ARRAY[]::uuid[]' if selected is None else "ARRAY['" + selected + "']::uuid[]"
        return f.call(fence_sql + f"SELECT security.gas_park_custody_projection_fence('{f.catalog}','{early}','{f.at}','{horizon}',ARRAY['{book}']::uuid[],{trades});")
    assert fence('2020-12-20T16:00:00Z', None) == 't', 'Known future custody birth was omitted from the projection fence.'
    assert fence('2020-12-18T17:00:00Z', None) == 'f'
    assert fence('2020-12-20T16:00:00Z', str(uuid4())) == 'f'
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    context = f.capture()
    scope_sql = f"SELECT security.use_market_context('{f.catalog}','{context}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
    assert f.call(scope_sql + f"SELECT security.gas_park_custody_projection_fence('{f.catalog}','{context}','{f.at}','2020-12-22T16:00:00Z',ARRAY['{book}']::uuid[],ARRAY[]::uuid[]);") == 't'
    # Immutable presence remains usable without acquiring custody/stock permissions.
    query(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission IN('ReadGasOperations','ReadGasOwnedHoldings');")
    try:
        assert f.call(scope_sql + f"SELECT security.require_gas_park_custody_scope('{f.catalog}','{context}','{f.at}',ARRAY['{provider_book}']::uuid[],ARRAY['{provider['tradeId']}']::uuid[]);") == 't'
        assert f.call(scope_sql + f"SELECT security.require_gas_park_custody_scope('{f.catalog}','{context}','{f.at}',ARRAY['{book}']::uuid[],ARRAY['{provider['tradeId']}']::uuid[]);") == 'f'
    finally:
        query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOperations'),('{role}','ReadGasOwnedHoldings');")
    print('PASS the economic fence includes completed cash-only and source-known future birth, excludes later/excluded births, and existence probes add no custody or stock permission.', flush=True)

    query("""CREATE FUNCTION trading.synthetic_custody_bad_booking(catalog uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
    DECLARE t trading.gas_park_custody_trades;i trading.gas_park_custody_instruments;g trading.trades;
     pub integer;new_trade uuid=gen_random_uuid();new_instrument uuid=gen_random_uuid();op uuid=gen_random_uuid();BEGIN
     PERFORM pg_advisory_xact_lock(730849102);
     SELECT * INTO STRICT t FROM trading.gas_park_custody_trades WHERE catalog_id=catalog LIMIT 1;
     SELECT * INTO STRICT i FROM trading.gas_park_custody_instruments WHERE(catalog_id,instrument_id)=(catalog,t.instrument_id);
     SELECT * INTO STRICT g FROM trading.trades WHERE(catalog_id,id)=(catalog,t.trade_id);
     SELECT max(publication_order)+1 INTO pub FROM trading.publications WHERE catalog_id=catalog;
     INSERT INTO trading.publications VALUES(catalog,pub,pg_current_xact_id());
     INSERT INTO trading.instruments VALUES(catalog,new_instrument,pub,i.effective_at);
     INSERT INTO trading.gas_park_custody_instruments SELECT(jsonb_populate_record(NULL::trading.gas_park_custody_instruments,
      to_jsonb(i)||jsonb_build_object('instrument_id',new_instrument,'publication_order',pub))).*;
     PERFORM security.stamp_trade_booking(catalog);
     INSERT INTO trading.trades VALUES(catalog,new_trade,g.book_id,op,g.actor_id,jsonb_set(g.request,'{operationId}',to_jsonb(op)),pub,g.effective_at);
     INSERT INTO trading.gas_park_custody_trades SELECT(jsonb_populate_record(NULL::trading.gas_park_custody_trades,
      to_jsonb(t)||jsonb_build_object('trade_id',new_trade,'instrument_id',new_instrument,'operation_id',op,'publication_order',pub,'quantity',999))).*;
    END$$;ALTER FUNCTION trading.synthetic_custody_bad_booking(uuid) OWNER TO openetrm_security_owner;
    GRANT EXECUTE ON FUNCTION trading.synthetic_custody_bad_booking(uuid) TO openetrm_runtime;""")
    rejected(lambda: f.call(f"SELECT trading.synthetic_custody_bad_booking('{f.catalog}');SET CONSTRAINTS ALL IMMEDIATE;", rollback=True), 'gas_custody_booking_seal')
    print('PASS typed custody booking quantity cannot diverge from the retained authorized request.', flush=True)

    other, other_book = f.account('future_funded')
    title_policy = f.bind(other, 'gas.owned.receive_title')
    title = f.evidence(other, 'synthetic_other_title', 'title_receipt', '1000', '2020-12-18T08:00:00Z')
    other_lot = f.native(other, 'receive_title', dict(evidenceId=title, quantity='1000', titleAt='2020-12-18T08:00:00Z',
        availableAt='2020-12-18T08:00:00Z', originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=title_policy)
    sale_policy = f.bind(other, 'gas.owned.dispatch')
    sale = f.evidence(other, 'synthetic_future_stock_use', 'physical_delivery', '500', '2020-12-20T14:00:00Z')
    f.native(other, 'dispatch', dict(lotId=other_lot['id'], evidenceId=sale, quantity='500', reservationId=None,
        effectiveAt='2020-12-20T14:00:00Z'), policy=sale_policy)
    f.bind(other, 'gas.owned.reserve')
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    future = f.book(f.booking(other_book, 'customer', other))
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    agreement = f.custody_evidence(future['tradeId'], 'synthetic_future_stock_agreement', 'custody_agreement', '720', '2020-12-20T09:00:00Z')
    receipt = f.custody_evidence(future['tradeId'], 'synthetic_future_stock_receipt', 'custody_receipt', '720', '2020-12-20T10:00:00Z')
    future_state = f.read(future['tradeId'])
    rejected(lambda: f.record(future['tradeId'], 'accept', dict(expectedVersion=future_state['stateVersion'],
        agreementEvidenceId=agreement['id'], receiptEvidenceId=receipt['id'], effectiveAt='2020-12-20T10:00:00Z',
        allocations=[dict(lotId=other_lot['id'], quantity='720')]), accept_policy, preview=True), 'gas_stock_reservation')
    assert f.read(future['tradeId'])['acceptance'] is None
    print('PASS native preview checks later stock-use boundaries and leaves no accepted custody or cash.', flush=True)

    prior_cut = f.capture()
    correction = f.custody_evidence(provider['tradeId'], 'synthetic_provider_agreement', 'custody_agreement', '721', '2020-12-19T08:00:00Z', revision=1)
    observed = f.custody_evidence(provider['tradeId'], 'synthetic_corrected_source_return', 'custody_return', '1', '2020-12-20T13:00:00Z')
    context = f.capture();state = f.read(provider['tradeId'], context=context)
    assert state['reconciliationRequired'] and not f.read(provider['tradeId'], context=prior_cut)['reconciliationRequired']
    rejected(lambda: f.record(provider['tradeId'], 'return', dict(expectedVersion=state['stateVersion'], businessKey='synthetic_after_correction',
        evidenceId=observed['id'], quantity='1', effectiveAt='2020-12-20T13:00:00Z', allocations=[]), return_policy, context=context), 'gas_custody_correction_required')
    query("""CREATE FUNCTION trading.synthetic_custody_corrected_return(catalog uuid,trade uuid,context uuid,head integer,selected_evidence uuid,policy uuid) RETURNS void
    LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
    DECLARE pub integer;op uuid=gen_random_uuid();id uuid=gen_random_uuid();actor uuid;e trading.gas_park_custody_evidence;body jsonb;request jsonb;outcome jsonb;BEGIN
      PERFORM pg_advisory_xact_lock(730849102);
      SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
      SELECT x.* INTO STRICT e FROM trading.gas_park_custody_evidence x WHERE(x.catalog_id,x.id)=(catalog,selected_evidence);
      SELECT max(publication_order)+1 INTO pub FROM trading.publications WHERE catalog_id=catalog;
      INSERT INTO trading.publications VALUES(catalog,pub,pg_current_xact_id());
      INSERT INTO trading.gas_park_custody_returns VALUES(catalog,id,op,trade,'synthetic_after_correction',e.source_key,e.id,1,context,head,policy,1,actor,pub,'2020-12-20T13:00:00Z');
      body=trading.gas_park_custody_recorded_body(catalog,'return',id);
      request=jsonb_build_object('tradeId',trade,'action','return','body',body,'asOf',TIMESTAMPTZ '2020-12-20T16:00:00Z','contextId',context,'policyId',policy,'policyRevision',1);
      outcome=trading.gas_park_custody_recorded_outcome(catalog,'return',id)||jsonb_build_object('operationId',op,'changed',true);
      INSERT INTO trading.gas_park_custody_commands VALUES(catalog,op,trade,actor,'return',request,outcome,clock_timestamp()+interval '30 seconds',policy,1);
    END$$;ALTER FUNCTION trading.synthetic_custody_corrected_return(uuid,uuid,uuid,integer,uuid,uuid) OWNER TO openetrm_security_owner;
    GRANT EXECUTE ON FUNCTION trading.synthetic_custody_corrected_return(uuid,uuid,uuid,integer,uuid,uuid) TO openetrm_runtime;""")
    rejected(lambda: f.call(f"SELECT trading.synthetic_custody_corrected_return('{f.catalog}','{provider['tradeId']}','{context}',{state['stateVersion']},'{observed['id']}','{return_policy}');SET CONSTRAINTS ALL IMMEDIATE;", rollback=True), 'gas_custody_correction_required')
    print('PASS corrected pinned observations retain history and independently block public and deferred-definer application.', flush=True)

    context = f.capture()
    scope_sql = f"SELECT security.use_market_context('{f.catalog}','{context}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
    query(f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission IN('ReadGasOwnedHoldings','RecordGasOwnedHoldings');")
    try:
        discovered = json.loads(f.call(scope_sql + f"SELECT security.gas_park_custody_discovery_directory('{f.catalog}','{context}','{f.at}',false)::text;"))
        assert {row['tradeId'] for row in discovered} == {provider['tradeId']}
        rejected(lambda: f.call(scope_sql + f"SELECT security.gas_park_custody_directory('{f.catalog}','{context}','{f.at}',ARRAY['{book}','{provider_book}']::uuid[],ARRAY[]::uuid[],ARRAY[]::uuid[]);"), 'gas_owned_denied')
    finally:
        query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings'),('{role}','RecordGasOwnedHoldings');")
    print('PASS provider-only discovery is explicit and does not weaken complete financial scope reads.', flush=True)

    status_payment = {**payment, 'operationId': str(uuid4()), 'allocationId': str(uuid4()), 'amount': '5', 'expectedRevision': 0}
    publication = f.publication()
    missing = json.loads(f.call(f"SELECT security.cash_payment_status('{f.catalog}',{literal(status_payment)})::text;"))
    assert missing['found'] is False and f.publication() == publication
    paid = json.loads(f.call(f"SELECT security.record_cash_payment('{f.catalog}',{literal(status_payment)},'{f.at}')::text;"))
    publication = f.publication()
    recovered = json.loads(f.call(f"SELECT security.cash_payment_status('{f.catalog}',{literal(status_payment)})::text;"))
    assert recovered['found'] and recovered['outcome'] == paid and f.publication() == publication
    rejected(lambda: f.call(f"SELECT security.cash_payment_status('{f.catalog}',{literal({**status_payment, 'amount': '6'})});"), 'cash_payment_conflict')
    rejected(lambda: f.call(f"SELECT security.cash_payment_status('{f.catalog}',{literal({**status_payment, 'bindingId': str(uuid4())})});"), 'cash_payment_denied')
    # Another authorized operator cannot recover this actor-bound receipt.
    other_actor = query("SELECT principal_id FROM security.identity_bindings WHERE subject='viewer';")
    other_grant = str(uuid4())
    query(f"INSERT INTO security.role_grants VALUES('{other_grant}','{other_actor}','{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '30 seconds',false);")
    try:
        rejected(lambda: f.call(f"SELECT security.cash_payment_status('{f.catalog}',{literal(status_payment)});", profile='viewer'), 'cash_payment_conflict')
    finally:
        query(f"DELETE FROM security.role_grants WHERE id='{other_grant}';")
    print('PASS cash status returns only the original actor/payload receipt, needs no frame, and never publishes financial facts.', flush=True)

    body = f.booking(book, 'customer', owned)
    body.update(parkAt='2020-12-21T10:00:00Z', returnAt='2020-12-22T12:00:00Z', feeDueAt='2020-12-23T12:00:00Z')
    owned_role, grant_one, grant_two = str(uuid4()), str(uuid4()), str(uuid4())
    actor = query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    with ThreadPoolExecutor(max_workers=1) as pool:
        blocker = pool.submit(query, "SET application_name='synthetic_custody_deadline';BEGIN;SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(8);COMMIT;")
        ready_by = time.monotonic() + 5
        while query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='synthetic_custody_deadline' AND l.locktype='advisory' AND l.granted;") != '1':
            assert time.monotonic() < ready_by, 'Owned publication-lock fixture did not become ready.'
            time.sleep(.02)
        query(f"BEGIN;DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadGasOwnedHoldings');"
            f"INSERT INTO security.roles(id,code,name) VALUES('{owned_role}','synthetic_custody_expiry_{owned_role[:8]}','Synthetic custody owned read expiry');"
            f"INSERT INTO security.role_permissions VALUES('{owned_role}','ReadGasOwnedHoldings');"
            f"INSERT INTO security.role_grants VALUES('{grant_one}','{actor}','{owned_role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 second',clock_timestamp()+interval '2 seconds',false),"
            f"('{grant_two}','{actor}','{owned_role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()+interval '4 seconds',clock_timestamp()+interval '30 seconds',false);COMMIT;")
        try:
            rejected(lambda: f.book(body), 'gas_custody_denied')
            blocker.result(timeout=15)
            assert f.call("SELECT security.has_permission('ReadGasOwnedHoldings');") == 't', 'The successor grant was not active; expiry refusal would be confounded.'
            assert json.loads(f.call(f"SELECT security.gas_park_custody_booking_status('{f.catalog}','{body['operationId']}')::text;"))['found'] is False
        finally:
            blocker.result(timeout=15)
            query(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{grant_one}','{grant_two}');DELETE FROM security.role_permissions WHERE role_id='{owned_role}';DELETE FROM security.roles WHERE id='{owned_role}';"
                f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');COMMIT;")
    print('PASS a customer booking preserves its original owned-authority deadline across a real publication lock despite a later active successor grant.', flush=True)
    from gas_park_custody_seals import no_use, reservation_owner, source_currency
    reservation_owner(f, accept_policy, return_policy)
    source_currency(f, accept_policy, return_policy)
    no_use(f, accept_policy)
    fee_input_demand(f, accept_policy)
    opaque_keys(f, accept_policy, return_policy)
    directory_refusal(f)
if __name__ == '__main__':
    main(checks=checks, fixture_factory=fixture)
