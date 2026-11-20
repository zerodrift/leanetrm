"""Independent source, authority and unsupported-correction counterexamples."""
from concurrent.futures import ThreadPoolExecutor
import json
import time

from gas_park_loan_fixture import rejected


def checks(f, booking, booked, accepted, operation, original_context, advance, read, command, partial_return):
    catalog, trade = f.catalog, booked['tradeId']
    original_at, original_day = f.at, f.day
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    selected = f.capture()
    role = f.query("SELECT r.role_id FROM security.role_grants r JOIN security.identity_bindings i ON i.principal_id=r.principal_id "
        "WHERE i.issuer='https://preview.invalid' AND i.subject='trader';")
    f.query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadGasOwnedHoldings');")
    try:
        bound = f"SELECT security.use_market_context('{catalog}','{selected}');SELECT security.use_trading_context('{catalog}','{f.at}');"
        assert f.call(bound + f"SELECT security.require_gas_park_loan_scope('{catalog}','{selected}','{f.at}',ARRAY['{booking['bookId']}']::uuid[],ARRAY[]::uuid[]);") == 't'
        assert f.call(bound + f"SELECT security.require_gas_park_loan_scope('{catalog}','{selected}','{f.at}',ARRAY['d1000000-0000-0000-0000-000000000002']::uuid[],ARRAY[]::uuid[]);") == 'f'
        identities = f.call(bound + f"SELECT security.risk_gas_park_loan_trade_heads('{catalog}',ARRAY['{booking['bookId']}']::uuid[],ARRAY['{trade}']::uuid[])::text;")
        assert json.loads(identities) == dict(trade_id=trade, book_id=booking['bookId'], revision=1)
        f.call(f"SELECT security.require_gas_park_loan_presence('{catalog}','{selected}','{f.at}',ARRAY['{booking['bookId']}']::uuid[],ARRAY[]::uuid[],"
            f"settlement_data.futures_margin_preview_authority('{catalog}'));")
        rejected(lambda: read(selected), 'gas_owned_denied')
        rejected(lambda: command('advance', advance, operation=operation, context=original_context), 'gas_owned_denied')
        rejected(lambda: f.call(f"SELECT security.gas_park_loan_booking_status('{catalog}','{booking['operationId']}');"), 'gas_owned_denied')
    finally:
        f.query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');")
    guard = f"SELECT security.require_gas_park_loan_presence('{catalog}','{selected}','{f.at}',ARRAY['{booking['bookId']}']::uuid[],ARRAY[]::uuid[],"
    rejected(lambda: f.call(guard + "clock_timestamp()-interval '1 second');"), 'futures_margin_denied')
    f.query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadTrades');")
    try:
        rejected(lambda: f.call(guard + "clock_timestamp()+interval '1 minute');"), 'futures_margin_denied')
    finally:
        f.query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadTrades');")
    assert command('advance', advance, operation=operation, context=original_context) == accepted
    grants_until = f.query(f"SELECT granted_to FROM security.role_grants WHERE role_id='{role}' AND principal_id="
        "(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader');")
    command_count = f.query(f"SELECT count(*) FROM trading.gas_park_loan_commands WHERE catalog_id='{catalog}';")
    f.query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.75 seconds' WHERE role_id='{role}' AND principal_id="
        "(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader');")
    try:
        with ThreadPoolExecutor(max_workers=1) as pool:
            blocked = pool.submit(f.query, "SET application_name='park_loan_authority_wait';BEGIN;"
                "SELECT pg_advisory_xact_lock(730849102);SELECT pg_sleep(2);COMMIT;")
            deadline = time.monotonic() + 3
            while f.query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='park_loan_authority_wait' AND wait_event='PgSleep');") != 't':
                assert time.monotonic() < deadline
                time.sleep(.01)
            try:
                rejected(lambda: command('advance', advance, context=original_context), 'gas_owned_denied')
            finally:
                blocked.result()
    finally:
        f.query(f"UPDATE security.role_grants SET granted_to='{grants_until}' WHERE role_id='{role}' AND principal_id="
            "(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader');")
    assert f.query(f"SELECT count(*) FROM trading.gas_park_loan_commands WHERE catalog_id='{catalog}';") == command_count
    f.at, f.day = original_at, original_day

    # A known correction which moves payment later must remove the old payment
    # from an earlier business-time view; its previous revision cannot revive.
    fee_id = accepted['feeId']
    payment_key = f.query(f"SELECT business_key FROM trading.gas_owned_fee_payments WHERE(catalog_id,fee_id,revision)=('{catalog}','{fee_id}',1);")
    from native_read_transport import captured_input
    payment_cutoff = captured_input(f.query,f.capture())['known_at']
    command('fee_payment', dict(expectedVersion=read()['stateVersion'], feeId=fee_id, businessKey=payment_key,
        expectedRevision=1, paidAt='2020-12-23T11:00:00Z', amount='-5', evidence='Synthetic corrected bank payment time'))
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    assert read()['fee']['paid'] == '0', 'An older payment revision revived before the corrected payment time.'
    assert read()['fee']['unpaid'] == '-12.5'
    assert read(f.capture(cutoff=payment_cutoff))['fee']['paid'] == '-5'
    f.at, f.day = original_at, original_day
    assert read()['fee']['paid'] == '-5'
    print('PASS Park and Loan payment corrections resolve knowledge revisions before business-time cash applicability', flush=True)

    # A revision of a linked return cannot be accepted through an ordinary
    # inventory amendment and leave a different service outstanding balance.
    repayment = f.query(f"SELECT p.consumption_id FROM trading.gas_owned_repayments p JOIN trading.gas_park_loan_returns r "
        f"ON(r.catalog_id,r.repayment_id)=(p.catalog_id,p.id) WHERE(r.catalog_id,r.trade_id,r.business_key)="
        f"('{catalog}','{trade}','{partial_return['businessKey']}');")
    rejected(lambda: f.query("BEGIN;SET LOCAL ROLE openetrm_security_owner;"
        "INSERT INTO trading.gas_owned_consumption_quantities SELECT (jsonb_populate_record(NULL::trading.gas_owned_consumption_quantities,"
        f"to_jsonb(q)||jsonb_build_object('revision',2,'previous_revision',1,'quantity',240))).* "
        f"FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id,q.revision)=('{catalog}','{repayment}',1);COMMIT;"),
        'gas_park_loan_correction_required')
    assert read()['balances']['outstandingPrincipal'] == '0'

    context = f.capture()
    cutoff = captured_input(f.query,context)['known_at']
    assert not read(context)['reconciliationRequired']
    original_evidence = f.query(f"SELECT source_key FROM trading.gas_owned_evidence WHERE(catalog_id,id)=('{catalog}','{partial_return['evidenceId']}');")
    corrected = f.evidence(booking['ownedAccountId'], original_evidence, 'loan_return', '240', '2020-12-20T12:00:00Z', revision=1)
    assert read()['reconciliationRequired']
    assert not read(f.capture(cutoff=cutoff))['reconciliationRequired']
    rejected(lambda: command('return', {**partial_return, 'expectedVersion': read()['stateVersion'], 'evidenceId': corrected,
        'quantity': '240'}), 'gas_park_loan_correction_required')
    assert read()['balances']['acceptedReturn'] == '720'
    assert f.query(f"SELECT count(*) FROM trading.gas_park_loan_returns WHERE(catalog_id,trade_id)=('{catalog}','{trade}');") == '2'
    print('PASS Park and Loan current authority on retained history/receipts, exact original recovery, linked native amendment refusal and source-known correction disclosure', flush=True)
