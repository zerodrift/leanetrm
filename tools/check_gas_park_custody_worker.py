"""Qualify retained custody PRICE using the ordinary calculation and real worker."""
import json
import os
from pathlib import Path
import shutil
import subprocess

from check_reference_editing import main
from check_risk_jobs_db import provision_worker
from gas_park_custody_fixture import CustodyFixture, fixture
from check_gas_park_loan_worker import differences
from gas_park_custody_worker_probes import CustodyWorkerProbes
from gas_park_custody_setup_probes import qualify_setup_authority

ROOT = Path(__file__).resolve().parents[1]


def checks(query, manifest):
    print('Loaded custody migrations into the owned disposable cluster.', flush=True)
    f = CustodyFixture(query, manifest)
    directory = Path(query('SHOW unix_socket_directories;')).resolve()
    assert directory.name.startswith('oetrm-ref-')
    market = f.valuation_inputs()
    login = 'synthetic_custody_price_worker'
    provision_worker(query, manifest, login)
    # A provider's custody role has no native stock permission. Its ordinary
    # scenario and custody-read authority are deliberately independent.
    query("DELETE FROM security.role_permissions WHERE permission='ReadGasOwnedHoldings';"
          "INSERT INTO security.role_permissions SELECT role_id,'ReadGasOperations' FROM security.role_permissions "
          "WHERE permission='RunScenario' ON CONFLICT DO NOTHING;"
          "INSERT INTO security.role_permissions SELECT role_id,'RunValuation' FROM security.role_permissions "
          "WHERE permission='RunScenario' ON CONFLICT DO NOTHING;")
    probes = CustodyWorkerProbes(f, login)
    frozen = directory / 'custody-price-binaries'
    shutil.copytree(Path(os.environ.get('CUSTODY_PRICE_BINARIES', ROOT / 'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0')), frozen)
    dotnet = str(Path(os.environ['DOTNET']).resolve())
    connection = directory / 'custody-price-connection'
    connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2')
    connection.chmod(0o600)
    config = directory / 'custody-price-worker.json'
    config.write_text(json.dumps(dict(connectionFile=str(connection), leaseSeconds=30, pollSeconds=1, timeoutSeconds=60)))
    config.chmod(0o600)

    def managed(path, verify=False, native=False):
        result = subprocess.run([dotnet, str(frozen / 'OpenEtrm.Api.Checks.dll'),
            '--gas-park-custody-native' if native else '--gas-park-custody-worker-verify' if verify else '--gas-park-custody-worker-submit', str(path)],
            cwd=ROOT, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        print(result.stdout.strip(), flush=True)
        if native:
            return None
        return json.loads(path.with_suffix('.custody-price-worker.json').read_text())

    def submit(booked, book, fee, label, *, selected=True, opening_context=None):
        packet = dict(host=str(directory), manifest=manifest, at=f.at, businessDate=f.day, contextId=f.capture(),
            bookId=book, tradeId=booked['tradeId'], contractId=f.contract, markId=market['markId'], rateIds=market['rateIds'],
            expectedBase=fee, expectedUp=fee, expectedDown=fee, expectedCashBindings=1, expectedNativePositions=[],
            nativeOwnership=False, selectedTrade=selected, requirePnlRefusal=True, checkpoint=label)
        if opening_context:
            packet['pnlOpeningContextId'] = opening_context
        path = directory / (label + '.json')
        path.write_text(json.dumps(packet)); path.chmod(0o600)
        return path, managed(path)

    def pnl(booked, book, label):
        packet = dict(host=str(directory), manifest=manifest, at=f.at, businessDate=f.day, contextId=f.capture(),
            bookId=book, tradeId=booked['tradeId'], contractId=f.contract, markId=market['markId'], rateIds=market['rateIds'],
            requirePnlRefusal=True, pnlOnly=True, checkpoint=label)
        path = directory / (label + '.json')
        path.write_text(json.dumps(packet)); path.chmod(0o600)
        managed(path, native=True)

    def run(path, saved):
        before = query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),"
            "(SELECT count(*) FROM trading.gas_park_custody_commands),(SELECT count(*) FROM settlement_data.cash_payment_commands))::text;")
        result = subprocess.run([dotnet, str(frozen / 'OpenEtrm.Api.dll'), '--risk-worker', 'once'], cwd=ROOT,
            env={**os.environ, 'OPENETRM_RISK_WORKER_CONFIG': str(config)}, capture_output=True, text=True, timeout=90)
        actual = json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{saved['id']}';"))
        assert result.returncode == 0 and actual['state'] == 'completed' and actual['result'] == saved['report'], dict(
            state=actual['state'], error=actual['error'], differences=differences(saved['report'], actual['result'])[:12], diagnostics=result.stdout + result.stderr)
        managed(path, True)
        assert before == query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),"
            "(SELECT count(*) FROM trading.gas_park_custody_commands),(SELECT count(*) FROM settlement_data.cash_payment_commands))::text;")

    book = f.book_scope('unpriced provider custody')
    f.at, f.day = '2020-12-19T16:00:00Z', '2020-12-19'
    body = f.booking(book, 'provider', None)
    body['point'] = 'synthetic_gas_point_unpriced'
    booked = f.book(body)
    state = f.read(booked['tradeId'])
    assert state['ownedAccountId'] is None and state['reservations'] == [] and state['cash'] is None
    expiry = probes.expiring_worker()
    path, saved = submit(booked, book, '-12.5', 'provider_conditional_fee', opening_context=body['contextId'])
    probes.expired_worker(path, saved, *expiry)
    probes.scopes(path, saved, body['contextId'])
    probes.actor_authority(saved)
    probes.delegator_authority(saved)
    # Change actual membership after admission. Replaying the original certified
    # source must retain its conditional binding, rather than current cash.
    agreement = f.custody_evidence(booked['tradeId'], 'provider_agreement', 'custody_agreement', '720', '2020-12-19T08:00:00Z')
    f.at, f.day = '2020-12-20T10:00:00Z', '2020-12-20'
    receipt = f.custody_evidence(booked['tradeId'], 'provider_receipt', 'custody_receipt', '720', f.at)
    policy = f.custody_policy('accept')
    current = f.read(booked['tradeId'])
    f.record(booked['tradeId'], 'accept', dict(expectedVersion=current['stateVersion'], agreementEvidenceId=agreement['id'],
        receiptEvidenceId=receipt['id'], effectiveAt=f.at, allocations=[]), policy)
    run(path, saved)
    deadline = probes.renewing_original()
    path, saved = submit(booked, book, '-12.5', 'provider_independent_cash')
    probes.original_deadline(saved, deadline)
    run(path, saved)
    assert query(f"SELECT NOT has_table_privilege('{login}','trading.gas_park_custody_instruments','SELECT') "
        f"AND NOT has_function_privilege('{login}','security.book_gas_park_custody(uuid,jsonb,timestamptz)','EXECUTE') "
        f"AND NOT has_function_privilege('{login}','security.record_gas_park_custody_evidence(uuid,uuid,uuid,jsonb,timestamptz,uuid)','EXECUTE');") == 't'
    print('PASS provider custody PRICE: real retained worker, unpriced custody point, no owned gas, source replay and no producer grants.', flush=True)
    f.at, f.day = '2020-12-20T16:00:00Z', '2020-12-20'
    returned = f.custody_evidence(booked['tradeId'], 'provider_final_return', 'custody_return', '720', '2020-12-20T11:00:00Z')
    return_policy = f.custody_policy('return')
    state = f.read(booked['tradeId'])
    f.record(booked['tradeId'], 'return', dict(expectedVersion=state['stateVersion'], businessKey='provider_final_return',
        evidenceId=returned['id'], quantity='720', effectiveAt='2020-12-20T11:00:00Z', allocations=[]), return_policy)
    state = f.read(booked['tradeId'])
    assert state['balances']['inCustody'] == '0' and state['cash']['amount'] == '-12.5'
    pnl(booked, book, 'completed_custody_independent_cash')
    f.at, f.day = '2020-12-19T17:00:00Z', '2020-12-19'
    zero_book = f.book_scope('zero fee custody')
    zero_body = f.booking(zero_book, 'provider', None, fee='0')
    zero_body['point'] = 'synthetic_gas_point_unpriced'
    zero = f.book(zero_body)
    assert f.read(zero['tradeId'])['cash'] is None
    pnl(zero, zero_book, 'zero_fee_custody')
    # A booked customer needs original owned-gas read authority even when no
    # native stock exists yet. No future lot is invented to satisfy that read.
    query("INSERT INTO security.role_permissions SELECT role_id,'ReadGasOwnedHoldings' FROM security.role_permissions "
        "WHERE permission='RunScenario' ON CONFLICT DO NOTHING;")
    f.at, f.day = '2020-12-19T17:00:00Z', '2020-12-19'
    owned, customer_book = f.account('custody_retained_customer')
    customer_body = f.booking(customer_book, 'customer', owned)
    customer = f.book(customer_body)
    path, saved = submit(customer, customer_book, '-12.5', 'customer_before_native_stock', selected=False)
    probes.scopes(path, saved, customer_body['contextId'], customer=True)
    probes.future_customer(path, saved)
    probes.actor_authority(saved, 'ReadGasOwnedHoldings')
    run(path, saved)
    print('PASS customer custody PRICE before native stock: exact whole-book source and original owned-gas authority without invented assets.', flush=True)
    qualify_setup_authority(f, frozen, dotnet, customer, booked)


if __name__ == '__main__':
    main(checks=checks, fixture_factory=fixture)
