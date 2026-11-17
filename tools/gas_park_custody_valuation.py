"""Independent native-service and complete-book valuation checkpoints."""
import json
import os
from pathlib import Path
import subprocess
from uuid import uuid4


def opaque_keys(f, accept_policy, return_policy):
    """Native scalar limits and managed readers retain the same opaque keys."""
    from gas_park_loan_fixture import rejected
    supplementary = '\U00010300'
    receipt_key, returned_key = supplementary * 128, supplementary * 65
    business_key = supplementary * 128
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    book = f.book_scope('synthetic_opaque_custody_keys')
    body = f.booking(book, 'provider', None, fee='0')
    body['point'] = 'synthetic_gas_point_unpriced'
    booked = f.book(body)
    trade = booked['tradeId']
    agreement = f.custody_evidence(trade, '\u00a0', 'custody_agreement', '720', '2020-12-19T08:00:00Z')
    before = f.publication()
    for key in ['a' * 129, supplementary * 129]:
        rejected(lambda: f.custody_evidence(trade, key, 'custody_agreement', '720', '2020-12-19T08:00:00Z'), 'gas_custody_evidence')
    assert f.publication() == before and f.read(trade)['evidence'][0]['sourceKey'] == '\u00a0'
    checkpoint(f, booked, book, '0', '0', [], 'opaque_keys_booked', provider=True, omit_rates=True, cash_bindings=0,
        portfolio_refusal='risk_position')
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    receipt = f.custody_evidence(trade, receipt_key, 'custody_receipt', '720', '2020-12-20T10:00:00Z')
    f.record(trade, 'accept', dict(expectedVersion=f.read(trade)['stateVersion'], agreementEvidenceId=agreement['id'],
        receiptEvidenceId=receipt['id'], effectiveAt='2020-12-20T10:00:00Z', allocations=[]), accept_policy)
    checkpoint(f, booked, book, '0', '0', [], 'opaque_keys_accepted', provider=True, omit_rates=True, cash_bindings=0,
        portfolio_refusal='risk_position')
    observed = f.custody_evidence(trade, returned_key, 'custody_return', '1', '2020-12-20T12:00:00Z')
    returning = dict(expectedVersion=f.read(trade)['stateVersion'], businessKey=business_key, evidenceId=observed['id'],
        quantity='1', effectiveAt='2020-12-20T12:00:00Z', allocations=[])
    f.record(trade, 'return', returning, return_policy)
    state = f.read(trade)
    assert state['acceptance']['agreementSourceKey'] == '\u00a0' and state['acceptance']['receiptSourceKey'] == receipt_key
    assert state['returns'][0]['businessKey'] == business_key and state['returns'][0]['evidenceSourceKey'] == returned_key
    assert state['balances']['inCustody'] == '719'
    before = f.publication()
    for key in ['a' * 129, supplementary * 129]:
        rejected(lambda: f.record(trade, 'return', {**returning, 'businessKey': key}, return_policy), 'gas_custody_input')
    assert f.record(trade, 'return', returning, return_policy)['changed'] is False and f.publication() == before
    checkpoint(f, booked, book, '0', '0', [], 'opaque_keys_partial_return', provider=True, omit_rates=True, cash_bindings=0,
        portfolio_refusal='risk_position')
    print('PASS opaque custody keys retain NBSP and128 Unicode scalars across pinned evidence/returns;129 scalars refuse without effects.', flush=True)


def checkpoint(f, booked, book, service_value, total_value, lots, name, *, provider=False, omit_rates=False, cash_bindings=1,
    native_ownership=None, omit_commodity_marks=None, portfolio_refusal=None):
    dotnet = os.environ.get('DOTNET')
    if not dotnet:
        return
    market = f.valuation_inputs()
    directory = Path(f.query('SHOW unix_socket_directories;')).resolve()
    packet = dict(host=str(directory), manifest=f.manifest, at=f.at, businessDate=f.day, contextId=f.capture(),
        bookId=book, tradeId=booked['tradeId'], contractId=f.contract, markId=market['markId'], rateIds=market['rateIds'],
        nativeOwnership=not provider if native_ownership is None else native_ownership, expectedCashBindings=cash_bindings,
        expectedNativePositions=[dict(ownerId=lot, bindingId=lot) for lot in lots], expectedServicePv=service_value, expectedBase=total_value,
        omitCommodityMarks=provider if omit_commodity_marks is None else omit_commodity_marks, omitRateMarks=omit_rates, checkpoint=name)
    if portfolio_refusal:
        packet['expectedPortfolioRefusal'] = portfolio_refusal
    run_packet(f, packet, name)


def fee_input_demand(f, accept_policy):
    """Due, paid and zero cash need neither a gas mark nor an unused rate."""
    if not os.environ.get('DOTNET'):
        return
    from gas_park_loan_fixture import literal
    for direction, signed in [('pay', '-12.5'), ('receive', '12.5'), ('pay', '0')]:
        name = 'provider_' + direction + ('_zero' if signed == '0' else '')
        book = f.book_scope('synthetic_input_demand_' + name)
        f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
        body = f.booking(book, 'provider', None, fee='0' if signed == '0' else '12.5', direction=direction)
        body['point'] = 'synthetic_gas_point_unpriced'
        booked = f.book(body)
        if signed == '0':
            checkpoint(f, booked, book, '0', '0', [], name + '_booked', provider=True, omit_rates=True, cash_bindings=0,
                portfolio_refusal='risk_position')
        f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
        agreement = f.custody_evidence(booked['tradeId'], name + '_agreement', 'custody_agreement', '720', '2020-12-20T09:00:00Z')
        receipt = f.custody_evidence(booked['tradeId'], name + '_receipt', 'custody_receipt', '720', '2020-12-20T10:00:00Z')
        f.record(booked['tradeId'], 'accept', dict(expectedVersion=f.read(booked['tradeId'])['stateVersion'],
            agreementEvidenceId=agreement['id'], receiptEvidenceId=receipt['id'], effectiveAt='2020-12-20T10:00:00Z', allocations=[]), accept_policy)
        if signed == '0':
            assert f.read(booked['tradeId'])['cash'] is None
            checkpoint(f, booked, book, '0', '0', [], name + '_accepted', provider=True, omit_rates=True, cash_bindings=0,
                portfolio_refusal='risk_position')
            account, _ = f.account('synthetic_zero_fee_independent_stock', book=book)
            policy = f.bind(account, 'gas.owned.receive_title')
            when = '2020-12-18T08:00:00Z'
            title = f.evidence(account, 'synthetic_zero_fee_independent_title', 'title_receipt', '100', when)
            stock = f.native(account, 'receive_title', dict(evidenceId=title, quantity='100', titleAt=when, availableAt=when,
                originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=policy)['id']
            checkpoint(f, booked, book, '0', '300', [stock], name + '_independent_stock_unchanged', provider=True,
                omit_rates=True, cash_bindings=0, native_ownership=True, omit_commodity_marks=False)
            continue
        if direction == 'receive':
            f.at, f.day = '2020-12-22T16:00:00Z', '2020-12-22'
            checkpoint(f, booked, book, signed, signed, [], name + '_due_no_rates', provider=True, omit_rates=True)
            continue
        state = f.read(booked['tradeId'])
        payment = dict(operationId=str(uuid4()), tradeId=booked['tradeId'], bindingId=state['cash']['bindingId'], allocationId=str(uuid4()),
            expectedRevision=0, policy='explicit_paid_cash_nominal_due_v1', paidAt='2020-12-20T15:00:00Z', amount=signed)
        outcome = json.loads(f.call(f"SELECT security.record_cash_payment('{f.catalog}',{literal(payment)},'{f.at}')::text;"))
        assert json.loads(f.call(f"SELECT security.cash_payment_status('{f.catalog}',{literal(payment)})::text;"))['outcome'] == outcome
        checkpoint(f, booked, book, '0', '0', [], name + '_paid_no_rates', provider=True, omit_rates=True)
        f.at, f.day = '2020-12-22T16:00:00Z', '2020-12-22'
        old = f.capture()
        assert len(f.read(booked['tradeId'], context=old)['cash']['payments']) == 1
        f.at, f.day = '2020-12-23T16:00:00Z', '2020-12-23'
        corrected = {**payment, 'operationId': str(uuid4()), 'expectedRevision': 1, 'paidAt': '2020-12-23T15:00:00Z'}
        f.call(f"SELECT security.record_cash_payment('{f.catalog}',{literal(corrected)},'{f.at}');")
        f.at, f.day = '2020-12-22T16:00:00Z', '2020-12-22'
        assert len(f.read(booked['tradeId'], context=old)['cash']['payments']) == 1
        assert len(f.read(booked['tradeId'])['cash']['payments']) == 2
        checkpoint(f, booked, book, signed, signed, [], name + '_future_bank_head_unpaid', provider=True, omit_rates=True)
        f.at, f.day = '2020-12-23T16:00:00Z', '2020-12-23'
        checkpoint(f, booked, book, '0', '0', [], name + '_future_bank_head_paid', provider=True, omit_rates=True)
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    print('PASS fee signs, empty-economic refusal, unchanged independent stock, due/paid input demand and knowledge-first future bank correction preserve exact input requirements.', flush=True)


def directory_refusal(f):
    """A new supported origin must not hide an unrelated incomplete trade."""
    if not os.environ.get('DOTNET'):
        return
    book, orphan, operation = f.book_scope('synthetic_missing_origin'), str(uuid4()), str(uuid4())
    f.query(f"BEGIN;INSERT INTO trading.publications SELECT '{f.catalog}',max(publication_order)+1,pg_current_xact_id() "
        f"FROM trading.publications WHERE catalog_id='{f.catalog}';"
        f"INSERT INTO trading.trades SELECT catalog_id,'{orphan}','{book}','{operation}',actor_id,'{{}}'::jsonb,"
        f"(SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{f.catalog}'),'{f.at}'::timestamptz "
        f"FROM trading.trades WHERE catalog_id='{f.catalog}' LIMIT 1;COMMIT;")
    assert f.query(f"SELECT count(*) FROM trading.booking_facts WHERE(catalog_id,trade_id)=('{f.catalog}','{orphan}');") == '1'
    packet = dict(host=f.query('SHOW unix_socket_directories;'), manifest=f.manifest, at=f.at, businessDate=f.day,
        contextId=f.capture(), bookId=book, tradeId=orphan, expectedDirectoryRefusal='position_facts', checkpoint='unsupported_origin')
    run_packet(f, packet, 'unsupported_origin')


def run_packet(f, packet, name):
    dotnet = os.environ['DOTNET']
    directory = Path(f.query('SHOW unix_socket_directories;')).resolve()
    path = directory / ('custody-valuation-' + name + '.json')
    path.write_text(json.dumps(packet)); path.chmod(0o600)
    root = Path(__file__).resolve().parents[1]
    binary = Path(os.environ.get('CUSTODY_PRICE_BINARIES', root / 'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0')) / 'OpenEtrm.Api.Checks.dll'
    before = f.publication()
    result = subprocess.run([str(Path(dotnet).resolve()), str(binary.resolve()), '--gas-park-custody-native', str(path)],
        cwd=root, capture_output=True, text=True, timeout=120)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    assert f.publication() == before, 'A valuation checkpoint published a financial event.'
    print(result.stdout.strip(), flush=True)
