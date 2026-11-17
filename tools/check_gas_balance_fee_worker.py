"""Qualify retained current-clock PRICE for the selected balance-time fee plan."""
import json
import os
from pathlib import Path
import shutil
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_risk_jobs_db import provision_worker
from check_gas_park_loan_worker import differences
from gas_balance_fee_fixture import booking
from gas_park_loan_fixture import ParkLoanFixture, fixture, literal, rejected

ROOT = Path(__file__).resolve().parents[1]


def checks(query, manifest):
    f = ParkLoanFixture(query, manifest)
    directory = Path(query('SHOW unix_socket_directories;')).resolve()
    assert directory.name.startswith('oetrm-ref-')
    dotnet = str(Path(os.environ['DOTNET']).resolve())
    frozen = directory / 'balance-price-binaries'
    shutil.copytree(Path(os.environ.get('BALANCE_FEE_PRICE_BINARIES', ROOT / 'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0')), frozen)
    stamps = {path.name: path.read_bytes() for path in frozen.glob('OpenEtrm.*.dll')}
    market = f.valuation_inputs()
    worker = 'synthetic_balance_price_worker'
    provision_worker(query, manifest, worker)
    query("INSERT INTO security.role_permissions SELECT role_id,'ReadGasOwnedHoldings' FROM security.role_permissions "
          "WHERE permission='RunScenario' ON CONFLICT DO NOTHING;")
    connection = directory / 'balance-price-connection'
    connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={worker};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2')
    connection.chmod(0o600)
    config = directory / 'balance-price-worker.json'
    config.write_text(json.dumps(dict(connectionFile=str(connection), leaseSeconds=30, pollSeconds=1, timeoutSeconds=60)))
    config.chmod(0o600)
    owned, book = f.account('balance_price_borrower')
    for action in ['gas.loan.borrow', 'gas.loan.repay', 'gas.cash.record_payment', 'gas.park_loan.fee_determine']:
        f.bind(owned, action)
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    body = booking(f, owned, None, book)
    booked = json.loads(f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(body)},'{f.at}')::text;"))
    trade = booked['tradeId']

    def read():
        context = f.capture()
        return json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
            f"SELECT security.read_gas_park_loan('{f.catalog}','{trade}','{context}','{f.at}')::text;"))

    def command(action, value):
        return json.loads(f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{uuid4()}','{trade}','{action}',"
            f"{literal(value)},'{f.at}','{f.capture()}',false)::text;"))

    def managed(path, verify=False):
        result = subprocess.run([dotnet, str(frozen / 'OpenEtrm.Api.Checks.dll'),
            '--gas-park-loan-worker-verify' if verify else '--gas-park-loan-worker-submit', str(path)],
            cwd=ROOT, text=True, capture_output=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        print(result.stdout.strip(), flush=True)
        return json.loads(path.with_suffix('.price-worker.json').read_text())

    def submit(label, fee, components, *, native=True, selected=False):
        context = f.capture()
        holdings = f.holdings(book, context=context)
        packet = dict(host=str(directory), manifest=manifest, at=f.at, businessDate=f.day, contextId=context,
            bookId=book, tradeId=trade, contractId=f.contract, markId=market['markId'], rateIds=market['rateIds'],
            expectedBase=fee, expectedUp=fee, expectedDown=fee, expectedServiceComponents=components,
            nativeOwnership=native, selectedTrade=selected, checkpoint=label,
            expectedNativePositions=[dict(ownerId=row['id'], bindingId=row['id']) for row in holdings['positions']] +
                [dict(ownerId=row['position_id'], bindingId=row['id']) for key in ['fees', 'deliveryCash'] for row in holdings[key]])
        path = directory / (label + '.json')
        path.write_text(json.dumps(packet)); path.chmod(0o600)
        saved = managed(path)
        assert query(f"SELECT risk.needs_gas(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        return path, saved

    def run(path, saved):
        count_sql = "SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM trading.gas_park_loan_fee_determinations)," \
            "(SELECT count(*) FROM trading.gas_owned_fees),(SELECT count(*) FROM trading.gas_owned_fee_payments))::text;"
        before = query(count_sql)
        result = subprocess.run([dotnet, str(frozen / 'OpenEtrm.Api.dll'), '--risk-worker', 'once'], cwd=ROOT,
            env={**os.environ, 'OPENETRM_RISK_WORKER_CONFIG': str(config)}, text=True, capture_output=True, timeout=90)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        actual = json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{saved['id']}';"))
        assert actual['state'] == 'completed' and actual['result'] == saved['report'], dict(state=actual['state'], error=actual['error'],
            differences=differences(saved['report'], actual['result'])[:12], diagnostics=result.stdout + result.stderr)
        managed(path, True)
        assert before == query(count_sql), 'PRICE evaluation published financial facts.'

    before = submit('balance_unadvanced', '-8.64', 3, native=False)
    def bound_probe(path, saved, *, context=None, selected_trade=None):
        packet = json.loads(path.read_text())
        query(f"SET SESSION AUTHORIZATION {worker};BEGIN;DO $scope$ DECLARE j jsonb;source jsonb;BEGIN "
            f"j=risk.claim(30,'{saved['engineVersion']}');IF coalesce(j->>'id',j->>'jobId')<>'{saved['id']}' THEN RAISE EXCEPTION 'unexpected_job';END IF;"
            "PERFORM risk.bind(coalesce(j->>'id',j->>'jobId')::uuid,(j->>'leaseToken')::uuid);"
            f"source=security.read_gas_park_loan('{f.catalog}','{selected_trade or trade}','{context or packet['contextId']}','{packet['at']}');"
            "IF source->>'implementation'<>'physical_gas_park_loan_balance_time_v1' THEN RAISE EXCEPTION 'wrong_profile';END IF;"
            "END$scope$;ROLLBACK;")
    bound_probe(*before)
    rejected(lambda: bound_probe(*before, context=str(uuid4())), 'risk_job_scope')
    rejected(lambda: bound_probe(*before, selected_trade=str(uuid4())), 'risk_job_scope')
    original = query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    role = str(uuid4())
    assert query(f"BEGIN;INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_balance_actor','Synthetic balance actor');"
        f"INSERT INTO security.role_permissions SELECT DISTINCT '{role}'::uuid,permission FROM security.role_permissions WHERE role_id IN("
        f"SELECT role_id FROM security.role_grants WHERE principal_id='{original}') AND permission<>'ReadGasOwnedHoldings';"
        f"DELETE FROM security.role_grants WHERE principal_id='{original}';INSERT INTO security.role_grants "
        f"SELECT gen_random_uuid(),'{original}','{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp(),'9999-12-31',false;"
        f"SELECT NOT risk.authorized(j) FROM risk.jobs j WHERE id='{before[1]['id']}';ROLLBACK;") == 't'
    selected = submit('balance_selected_unadvanced', '-8.64', 3, native=False, selected=True)
    f.at, f.day = '2020-12-20T11:00:00Z', '2020-12-20'
    late = submit('balance_late_unaccepted', '-5.76', 3, native=False)
    f.at = '2020-12-20T16:00:00Z'
    agreement = f.evidence(owned, 'balance_price_agreement', 'loan_agreement', '720', '2020-12-20T10:00:00Z')
    receipt = f.evidence(owned, 'balance_price_receipt', 'title_receipt', '720', '2020-12-20T10:00:00Z')
    advanced = command('advance', dict(expectedVersion=read()['stateVersion'], agreementEvidenceId=agreement,
        evidenceId=receipt, claimEvidenceId=None, lotId=None, reservationId=None, effectiveAt='2020-12-20T10:00:00Z'))
    for pending in [before, selected, late]:
        run(*pending)
    actual = submit('balance_accepted_advance', '-8.64', 1)
    run(*actual)

    def determine(previous, through, key):
        return command('fee_determine', dict(expectedVersion=read()['stateVersion'], previousDeterminationId=previous,
            through=through, effectiveAt=through, businessKey=key, evidence='Synthetic cumulative balance fee'))

    first = determine(None, '2020-12-20T11:00:00Z', 'balance_price_first')
    run(*submit('balance_determined_unpaid', '-8.64', 1))
    command('fee_payment', dict(expectedVersion=read()['stateVersion'], feeId=first['feeId'], businessKey='balance_price_bank',
        expectedRevision=0, paidAt='2020-12-20T11:05:00Z', amount='-2.88', evidence='Synthetic independent bank payment'))
    paid = submit('balance_paid_before_backdated_return', '-5.76', 1)
    evidence = f.evidence(owned, 'balance_price_partial', 'loan_return', '270', '2020-12-20T10:30:00Z')
    command('return', dict(expectedVersion=read()['stateVersion'], businessKey='balance_price_partial', evidenceId=evidence,
        lotId=advanced['stockId'], reservationId=None, quantity='270', effectiveAt='2020-12-20T10:30:00Z'))
    unrestated = submit('balance_backdated_before_restatement', '-3.06', 1)
    restated = determine(first['id'], '2020-12-20T11:00:00Z', 'balance_price_restated')
    assert restated['contribution'] == '0.54'
    run(*paid)
    run(*unrestated)
    run(*submit('balance_refund_independent_cash', '-3.06', 1))
    evidence = f.evidence(owned, 'balance_price_final', 'loan_return', '450', '2020-12-20T12:00:00Z')
    command('return', dict(expectedVersion=read()['stateVersion'], businessKey='balance_price_final', evidenceId=evidence,
        lotId=advanced['stockId'], reservationId=None, quantity='450', effectiveAt='2020-12-20T12:00:00Z'))
    last = determine(restated['id'], '2020-12-20T12:00:00Z', 'balance_price_final_fee')
    assert last['roundedTotal'] == '-4.14' and last['contribution'] == '-1.8'
    run(*submit('balance_completed_principal_cash_survives', '-1.26', 0))
    assert query(f"SELECT NOT has_table_privilege('{worker}','trading.gas_park_loan_fee_determinations','SELECT') "
        f"AND NOT has_function_privilege('{worker}','security.record_gas_park_loan(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean)','EXECUTE');") == 't'
    assert all((frozen / name).read_bytes() == data for name, data in stamps.items())
    def projection_fence(selected, selected_book=book):
        context = f.capture()
        return f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
            f"SELECT security.gas_park_loan_projection_fence('{f.catalog}','{context}','{f.at}','2020-12-22T16:00:00Z',"
            f"ARRAY['{selected_book}']::uuid[],ARRAY['{selected}']::uuid[]);") == 't'
    fenced = {'fully_determined_cash_only': projection_fence(trade)}
    for label, rate in [('zero_rate', '0'), ('rounded_zero', '0.000001')]:
        f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
        candidate = booking(f, owned, None, book, rate=rate)
        extra = json.loads(f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(candidate)},'{f.at}')::text;"))
        f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
        extra_agreement = f.evidence(owned, 'balance_fence_' + label + '_agreement', 'loan_agreement', '720', '2020-12-20T10:00:00Z')
        extra_receipt = f.evidence(owned, 'balance_fence_' + label + '_receipt', 'title_receipt', '720', '2020-12-20T10:00:00Z')
        context = f.capture()
        state = json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{context}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
            f"SELECT security.read_gas_park_loan('{f.catalog}','{extra['tradeId']}','{context}','{f.at}')::text;"))
        f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{uuid4()}','{extra['tradeId']}','advance',"
            f"{literal(dict(expectedVersion=state['stateVersion'], agreementEvidenceId=extra_agreement, evidenceId=extra_receipt, claimEvidenceId=None, lotId=None, reservationId=None, effectiveAt='2020-12-20T10:00:00Z'))},"
            f"'{f.at}','{context}',false);")
        fenced[label] = projection_fence(extra['tradeId'])
    assert not projection_fence(str(uuid4())), 'An excluded trade scope inherited another service\'s economic refusal.'
    empty_book = f.account('balance_empty_scope')[1]
    assert not projection_fence(trade, empty_book), 'An unrelated empty book inherited another service\'s economic refusal.'
    print('Balance-fee economic fences: ' + json.dumps(fenced, sort_keys=True), flush=True)
    assert all(fenced.values()), 'Zero service components do not qualify complete economic continuation for this selected profile.'
    print('PASS balance fee PRICE: nine actual queued jobs, exact ordinary report parity, accepted-event/checkpoint replay, independent paid/refund cash and no financial writes.', flush=True)


if __name__ == '__main__':
    if not os.environ.get('DOTNET'):
        raise ValueError('Set DOTNET to this tree\'s installed SDK executable; frozen managed binaries are required.')
    main(checks=checks, fixture_factory=fixture)
