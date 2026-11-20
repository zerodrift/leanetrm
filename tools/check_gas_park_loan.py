"""Qualify booked Park and Loan against a fresh stock PostgreSQL instance."""
import json
import os
from decimal import Decimal
from uuid import uuid4

from check_reference_editing import main
from gas_park_loan_fixture import ParkLoanFixture, fixture, literal, rejected
from gas_park_loan_valuation import checkpoint


def checks(query, manifest):
    assert query("SELECT assistant.is_prompt_page('blotter_new_park_loan');") == 't'
    assert query("SELECT assistant.is_prompt_page('synthetic_unlisted_page');") == 'f'
    f = ParkLoanFixture(query, manifest)
    from gas_park_loan_presence import checks as presence
    presence(f, 'd1000000-0000-0000-0000-000000000001', f.capture(), f.at)
    if not os.environ.get('DOTNET'):
        print('Native-only qualification; set DOTNET to the pinned SDK executable to include managed whole-book PV/scenario checkpoints.', flush=True)
    for kind, perspective, direction in [('loan', 'customer', 1), ('park', 'provider', 1),
                                         ('loan', 'provider', -1), ('park', 'customer', -1)]:
        label = kind + '_' + perspective
        owned, book = f.account(label)
        claim = f.account(label + '_claim', kind='operator_balance', book=book)[0] if direction < 0 else None
        for account, actions in [(owned, ['gas.owned.receive_title', 'gas.owned.dispatch', 'gas.loan.borrow',
                                         'gas.loan.repay', 'gas.cash.record_payment']),
                                 (claim, ['gas.operator.record', 'gas.operator.settle', 'gas.cash.record_payment'])]:
            if account:
                for action in actions:
                    f.bind(account, action)
        f.at = '2020-12-19T16:00:00Z'; f.day = '2020-12-19'
        body = dict(operationId=str(uuid4()), profile='physical_gas_park_loan_v1',
            convention='physical_gas_fixed_quantity_v4', knowledgeMode='current', contextId=f.capture(), snapshotId=str(uuid4()),
            bookId=book, definitionRevisionId=manifest['revisionId'], legalEntityId=f.entity, legalEntityRevision=1,
            counterpartyId=f.party, counterpartyRevision=1, ownedAccountId=owned, claimAccountId=claim,
            serviceKind=kind, perspective=perspective, quantity='720', advanceAt='2020-12-20T10:00:00Z',
            returnAt='2020-12-21T12:00:00Z', fixedFeeAmount='-12.5' if perspective == 'customer' else '12.5',
            feeDirection='pay' if perspective == 'customer' else 'receive',
            feeDueAt='2020-12-22T12:00:00Z', titlePolicy='fungible_gas_title_transfer_return_v1',
            returnPolicy='same_quantity_partial_returns_v1', returnTiming='accepted_advance_then_returns_v1',
            carrying='nominal_mark_v1')
        book_sql = f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(body)},'{f.at}')::text;"
        publication = f.publication()
        rejected(lambda: f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal({**body, 'operationId': str(uuid4()), 'feeDueAt': '2020-12-20T09:59:59Z'})},'{f.at}');"), 'gas_park_loan_fee')
        for selected in [{**body, 'operationId': str(uuid4()), 'feeDueAt': body['advanceAt']},
                         {**body, 'operationId': str(uuid4()), 'fixedFeeAmount': '0', 'feeDueAt': None}]:
            assert json.loads(f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(selected)},'{f.at}')::text;", rollback=True))['revision'] == 1
        rejected(lambda: f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal({**body, 'operationId': str(uuid4()), 'bookId': 'd1000000-0000-0000-0000-000000000002'})},'{f.at}');"), 'gas_park_loan_account')
        assert f.publication() == publication
        booked = json.loads(f.call(book_sql))
        publication = f.publication()
        assert json.loads(f.call(book_sql)) == booked
        assert f.publication() == publication
        rejected(lambda: f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal({**body, 'quantity': '721'})},'{f.at}');"), 'gas_park_loan_conflict')
        status = json.loads(f.call(f"SELECT security.gas_park_loan_booking_status('{f.catalog}','{body['operationId']}')::text;"))
        normalized = {**body, 'advanceAt': '2020-12-20T10:00:00.000000Z', 'returnAt': '2020-12-21T12:00:00.000000Z',
            'feeDueAt': '2020-12-22T12:00:00.000000Z'}
        assert status['found'] and status['request'] == normalized and status['outcome'] == booked
        trade = booked['tradeId']

        def read(context=None):
            selected = context or f.capture()
            return json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{selected}');"
                f"SELECT security.use_trading_context('{f.catalog}','{f.at}');"
                f"SELECT security.read_gas_park_loan('{f.catalog}','{trade}','{selected}','{f.at}')::text;"))

        def command(action, value, *, operation=None, context=None, preview=False):
            return json.loads(f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{operation or uuid4()}',"
                f"'{trade}','{action}',{literal(value)},'{f.at}','{context or f.capture()}',{str(preview).lower()})::text;"))

        initial = read()
        facts = initial['booking']
        assert facts['implementation'] == 'server_booking_utc_v1'
        assert facts['booker_login'] == 'trader' and facts['booker_name'] == 'Synthetic trader'
        assert facts['trade_date'] == facts['executed_at'][:10]
        assert initial['tradeReference'] > 0 and initial['instrumentReference'] > 0
        assert initial['terms']['principalDirection'] == direction
        assert initial['advance'] is None and initial['balances'] == {
            'acceptedPrincipal': '0', 'acceptedReturn': '0', 'outstandingPrincipal': '0'}
        assert f.holdings(book)['positions'] == [], 'A future booked commitment created actual owned gas.'
        checkpoint(f, booked, book, Decimal(0), Decimal(body['fixedFeeAmount']), 3, label + ' booked before advance')
        f.at = '2020-12-20T16:00:00Z'; f.day = '2020-12-20'
        at = '2020-12-20T10:00:00Z'
        agreement = f.evidence(owned, label + '_agreement', 'loan_agreement', '720', at)
        title = f.evidence(owned, label + '_advance', 'title_receipt' if direction > 0 else 'physical_delivery', '720', at)
        claim_evidence = f.evidence(claim, label + '_claim', 'operator_statement', '720', at) if claim else None
        lot = None
        if direction < 0:
            supply = f.evidence(owned, label + '_funding', 'title_receipt', '720', '2020-12-19T08:00:00Z')
            policy = query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)="
                f"('{f.catalog}','{owned}','gas.owned.receive_title') ORDER BY revision DESC LIMIT 1;")
            lot = f.native(owned, 'receive_title', dict(evidenceId=supply, quantity='720', titleAt='2020-12-19T08:00:00Z',
                availableAt='2020-12-19T08:00:00Z', originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=policy)['id']
        exposure = Decimal(0 if direction > 0 else 720)
        checkpoint(f, booked, book, exposure, Decimal(body['fixedFeeAmount']), 3, label + ' immediately before native handoff')
        advance = dict(expectedVersion=read()['stateVersion'], agreementEvidenceId=agreement, evidenceId=title,
            claimEvidenceId=claim_evidence, lotId=lot, reservationId=None, effectiveAt=at)
        publication = f.publication()
        reviewed_context = f.capture()
        reviewed = command('advance', advance, context=reviewed_context, preview=True)
        assert f.publication() == publication and read()['advance'] is None
        if claim:
            f.bind(claim, 'gas.operator.record')
            publication = f.publication()
            rejected(lambda: command('advance', advance, context=reviewed_context), 'gas_owned_policy_changed')
            assert read()['advance'] is None and f.publication() == publication
            assert f.holdings(book)['positions'][0]['remaining_quantity'] == '720'
        operation, context = str(uuid4()), f.capture()
        accepted = command('advance', advance, operation=operation, context=context)
        assert command('advance', advance, operation=operation, context=context) == accepted
        upper = {**advance, 'agreementEvidenceId': agreement.upper(), 'claimEvidenceId': claim_evidence.upper() if claim_evidence else None}
        assert command('advance', upper, operation=operation, context=context) == accepted
        receipt = json.loads(f.call(f"SELECT security.gas_park_loan_command_receipt('{f.catalog}','{operation}','{trade}','advance',"
            f"{literal(advance)},'{f.at}','{context}')::text;"))
        assert receipt == accepted
        publication = f.publication()
        assert command('advance', advance)['changed'] is False
        assert command('advance', upper)['changed'] is False
        assert f.publication() == publication
        assert read()['balances']['outstandingPrincipal'] == '720'
        assert Decimal(read()['terms']['fixedFeeAmount']) == Decimal(body['fixedFeeAmount'])
        assert reviewed['action'] == accepted['action'] == 'advance'
        checkpoint(f, booked, book, exposure, Decimal(body['fixedFeeAmount']), 0, label + ' immediately after native handoff')
        owned_return = f.evidence(owned, label + '_return', 'loan_return' if direction > 0 else 'title_receipt',
            '270', '2020-12-20T12:00:00Z')
        returned = dict(expectedVersion=read()['stateVersion'], businessKey=label + '_partial',
            evidenceId=owned_return, lotId=accepted['stockId'] if direction > 0 else None,
            reservationId=None, quantity='270', effectiveAt='2020-12-20T12:00:00Z')
        command('return', returned)
        stray = f.evidence(owned, label + '_unlinked_return', 'loan_return' if direction > 0 else 'title_receipt',
            '1', '2020-12-20T13:00:00Z')
        action = 'repay' if direction > 0 else 'operator_settle'
        policy_action = 'gas.loan.repay' if direction > 0 else 'gas.operator.settle'
        native_owner = owned if direction > 0 else claim
        policy = query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)="
            f"('{f.catalog}','{native_owner}','{policy_action}') ORDER BY revision DESC LIMIT 1;")
        unlinked = (dict(loanId=accepted['loanId'], lotId=accepted['stockId'], evidenceId=stray, quantity='1', reservationId=None,
                        effectiveAt='2020-12-20T13:00:00Z') if direction > 0 else
                    dict(positionId=accepted['claimId'], quantity='1', ownedAccountId=owned, lotId=None, reservationId=None,
                         evidenceId=stray, effectiveAt='2020-12-20T13:00:00Z', availableAt='2020-12-20T13:00:00Z', sourceObservationId=None))
        rejected(lambda: f.native(native_owner, action, unlinked, policy=policy), 'gas_park_loan_counterpart')
        from native_read_transport import captured_input
        before_final = captured_input(query,f.capture())['known_at']
        assert read()['balances'] == {'acceptedPrincipal': '720', 'acceptedReturn': '270', 'outstandingPrincipal': '450'}
        fee = read()['advance']['feeId']
        payment = dict(expectedVersion=read()['stateVersion'], feeId=fee, businessKey=label + '_payment',
            expectedRevision=0, paidAt='2020-12-20T14:00:00Z', amount='-5' if perspective == 'customer' else '5',
            evidence='Synthetic independently recorded payment')
        command('fee_payment', payment)
        publication = f.publication()
        assert command('fee_payment', {**payment, 'expectedRevision': 1})['changed'] is False
        assert f.publication() == publication
        assert read()['balances']['outstandingPrincipal'] == '450'
        unpaid = Decimal('-7.5' if perspective == 'customer' else '7.5')
        checkpoint(f, booked, book, exposure, unpaid, 0, label + ' partial return and independent fee payment')
        f.at = '2020-12-23T16:00:00Z'; f.day = '2020-12-23'
        assert read()['balances']['outstandingPrincipal'] == '450', 'A due date manufactured principal performance.'
        final_evidence = f.evidence(owned, label + '_final', 'loan_return' if direction > 0 else 'title_receipt',
            '450', '2020-12-23T10:00:00Z')
        command('return', dict(expectedVersion=read()['stateVersion'], businessKey=label + '_final',
            evidenceId=final_evidence, lotId=accepted['stockId'] if direction > 0 else None,
            reservationId=None, quantity='450', effectiveAt='2020-12-23T10:00:00Z'))
        assert read()['balances']['outstandingPrincipal'] == '0'
        assert read(f.capture(cutoff=before_final))['balances']['outstandingPrincipal'] == '450'
        held = f.holdings(book)
        assert sum((Decimal(row['amount']) for row in held['fees']), Decimal(0)) == Decimal(body['fixedFeeAmount'])
        assert len(held['fees']) == 1
        assert Decimal(read()['fee']['unpaid']) == Decimal('-7.5' if perspective == 'customer' else '7.5')
        checkpoint(f, booked, book, exposure, unpaid, 0, label + ' overdue final return and surviving fee')
        if kind == 'loan' and perspective == 'customer':
            from gas_park_loan_adversarial import checks as adversarial
            adversarial(f, body, booked, accepted, operation, context, advance, read, command, returned)
        print('PASS booked ' + label + ': no future stock, full accepted advance, partial/final return, surviving fixed fee, preview rollback and exact retries', flush=True)
    from gas_park_loan_stock_race import checks as stock_race
    stock_race(f, body)


if __name__ == '__main__':
    main(checks=checks, fixture_factory=fixture)
