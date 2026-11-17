"""Qualify selected financial Transport PRICE with the real retained worker."""
import json
import os
from pathlib import Path
import shutil
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_risk_jobs_db import provision_worker
from check_gas_park_loan_worker import differences
from gas_transport_financial_fixture import FinancialTransportFixture, fixture
from gas_transport_financial_managed import _publication_heads

ROOT = Path(__file__).resolve().parents[1]


def checks(query, manifest):
    if not os.environ.get('DOTNET'):
        raise RuntimeError('DOTNET must select the installed SDK for required financial Transport worker qualification.')
    f = FinancialTransportFixture(query, manifest)
    directory = Path(query('SHOW unix_socket_directories;')).resolve()
    assert directory.name.startswith('oetrm-ref-')
    customer = f.book_financial(f.booking(key='synthetic_customer_worker'))
    f.record_financial(f.mark(customer['tradeId'], customer['financialStateId']))
    provider_book, definition, capacity = f.new_capacity('synthetic_provider_worker_capacity')
    provider_body = f.booking(key='synthetic_provider_worker', perspective='provider', direction='receive')
    provider_body.update(bookId=provider_book, entitlementId=definition['entitlementId'], capacityVersionId=capacity['versionId'])
    provider = f.book_financial(provider_body)
    provider_mark = f.mark(provider['tradeId'], provider['financialStateId'])
    provider_mark.update(curveCode='provider_transport_rights', contractId=f.provider_contract,
        seriesId=f.provider_series, observationId=f.provider_observation)
    f.record_financial(provider_mark)
    login = 'synthetic_transport_price_worker'
    provision_worker(query, manifest, login)
    # Current financial value consumes accepted rights and cash, not operating
    # access or an invented stock account, on either side of the worker boundary.
    query("DELETE FROM security.role_permissions WHERE role_id IN(SELECT id FROM security.roles WHERE code IN('trader','risk_analyst')) "
        "AND permission IN('ReadGasOperations','RecordGasOperations','ConfigureGasServices','ReadGasOwnedHoldings','RecordGasOwnedHoldings','ReadFacilityQuantities','RecordFacilityQuantities');")
    frozen = directory / 'financial-price-binaries'
    shutil.copytree(Path(os.environ.get('GAS_TRANSPORT_FINANCIAL_BINARIES', ROOT / 'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0')), frozen)
    dotnet = str(Path(os.environ['DOTNET']).resolve())
    connection = directory / 'financial-price-connection'
    connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=15;Maximum Pool Size=2')
    connection.chmod(0o600)
    config = directory / 'financial-price-worker.json'
    config.write_text(json.dumps(dict(connectionFile=str(connection), leaseSeconds=30, pollSeconds=1, timeoutSeconds=60)))
    config.chmod(0o600)

    def managed(path, verify=False):
        result = subprocess.run([dotnet, str(frozen / 'OpenEtrm.Api.Checks.dll'),
            '--gas-transport-financial-worker-verify' if verify else '--gas-transport-financial-worker-submit', str(path)],
            cwd=ROOT, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        print(result.stdout.strip(), flush=True)
        return json.loads(path.with_suffix('.financial-price-worker.json').read_text())

    def submit(booked, book, label, expected):
        source = f.read_financial(booked['tradeId'])
        mark = source['mark']
        packet = dict(host=str(directory), manifest=manifest, contextId=f.capture(), asOf=f.valuation_at,
            bookId=book, tradeId=booked['tradeId'], observationIds=[mark['observationId'], *f.rate_observations[f.valuation_at]],
            curveContracts={mark['curveCode']: [mark['contractId']]}, pricingPolicy='base', bumpSize='1',
            expectedBase=expected[0], expectedUp=expected[1], expectedDown=expected[2],
            expectedCurrency=dict(catalogId=f.reference, revisionId=f.valuation_reference, code='usd'), checkpoint=label)
        path = directory / (label + '.json')
        path.write_text(json.dumps(packet)); path.chmod(0o600)
        before = _publication_heads(f)
        saved = managed(path)
        assert _publication_heads(f) == before, 'Financial PRICE admission published a financial, market or operating effect.'
        assert query(f"SELECT NOT risk.needs_gas(j) AND risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        return path, saved, packet

    def bound(saved, sql):
        query(f"SET SESSION AUTHORIZATION {login};BEGIN;DO $probe$ DECLARE j jsonb;BEGIN "
            f"j=risk.claim(30,'{saved['engineVersion']}');IF j IS NULL THEN RAISE EXCEPTION 'missing_financial_job';END IF;"
            "PERFORM risk.bind(coalesce(j->>'id',j->>'jobId')::uuid,(j->>'leaseToken')::uuid);"
            f"{sql};END$probe$;ROLLBACK;")

    def scope_and_authority(saved, packet, foreign_trade):
        args = [f"'{f.catalog}'", f"'{packet['contextId']}'", f"'{packet['asOf']}'::timestamptz",
            f"ARRAY['{packet['bookId']}']::uuid[]", f"ARRAY['{packet['tradeId']}']::uuid[]"]
        def read(values):
            return f"security.gas_transport_financial_directory({','.join(values)})"
        bound(saved, f"IF jsonb_array_length({read(args)})<>1 THEN RAISE EXCEPTION 'missing_exact_financial';END IF")
        for index, replacement in [(2, args[2]+"+interval '1 second'"),
                (3, f"ARRAY['{uuid4()}']::uuid[]"), (4, f"ARRAY['{foreign_trade}']::uuid[]")]:
            changed = list(args); changed[index] = replacement
            try:
                bound(saved, f"PERFORM {read(changed)}")
            except RuntimeError as error:
                assert 'risk_job_scope' in str(error), str(error)
            else:
                raise AssertionError('A financial worker read escaped its exact retained source/book/trade scope.')
        actor = query(f"SELECT actor_id FROM risk.jobs WHERE id='{saved['id']}';")
        denied = query(f"BEGIN;DELETE FROM security.role_permissions WHERE permission='ReadTrades' AND role_id IN("
            f"SELECT role_id FROM security.role_grants WHERE principal_id='{actor}');"
            f"SELECT NOT risk.authorized(j) AND security.principal_has_permission(j.actor_id,j.tenant_id,j.workspace_id,'RunScenario',clock_timestamp()) "
            f"AND security.principal_has_permission(j.actor_id,j.tenant_id,j.workspace_id,'ReadMarketData',clock_timestamp()) FROM risk.jobs j WHERE id='{saved['id']}';ROLLBACK;")
        assert denied == 't', denied
        query("BEGIN;DELETE FROM security.role_permissions WHERE permission='ReadTrades' AND role_id=(SELECT id FROM security.roles WHERE code='risk_analyst');"
            f"SET SESSION AUTHORIZATION {login};DO $probe$ DECLARE j jsonb;BEGIN BEGIN j=risk.claim(30,'{saved['engineVersion']}');"
            "IF j IS NOT NULL THEN RAISE EXCEPTION 'financial_worker_without_trade_authority';END IF;EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$probe$;ROLLBACK;")
        assert query(f"SELECT state='queued' AND risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        assert query(f"SELECT NOT has_table_privilege('{login}','trading.gas_transport_financial_trades','SELECT') "
            f"AND NOT has_function_privilege('{login}','security.book_gas_transport_financial(uuid,jsonb,timestamptz)','EXECUTE') "
            f"AND NOT has_function_privilege('{login}','security.record_gas_transport_financial(uuid,uuid,jsonb,timestamptz,uuid,timestamptz)','EXECUTE') "
            f"AND NOT has_function_privilege('{login}','security.gas_transport_financial_capacity_facts(uuid,uuid,timestamptz,uuid)','EXECUTE');") == 't'
        print('PASS financial PRICE authority: exact source/instant/books/trades, original actor and current worker reads, no operating/stock or producer grants.', flush=True)

    def run(path, saved):
        before = _publication_heads(f)
        result = subprocess.run([dotnet, str(frozen / 'OpenEtrm.Api.dll'), '--risk-worker', 'once'], cwd=ROOT,
            env={**os.environ, 'OPENETRM_RISK_WORKER_CONFIG': str(config)}, capture_output=True, text=True, timeout=90)
        actual = json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{saved['id']}';"))
        assert result.returncode == 0 and actual['state'] == 'completed' and actual['result'] == saved['report'], dict(
            state=actual['state'], error=actual['error'], differences=differences(saved['report'], actual['result'])[:12], diagnostics=result.stdout + result.stderr)
        managed(path, True)
        assert _publication_heads(f) == before, 'A retained financial PRICE job published a financial, market or operating effect.'

    path, saved, packet = submit(customer, f.book, 'customer_signed_rights_and_fee', ('7.5', '8.5', '6.5'))
    scope_and_authority(saved, packet, provider['tradeId'])
    corrected = str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||"
        f"jsonb_build_object('id','{corrected}','corrects_id',id,'source_revision_id','2','value',21,'received_at',clock_timestamp()))).* "
        f"FROM market_data.observations o WHERE(catalog_id,id)=('{f.catalog}','{f.observation}');")
    assert f.read_financial(customer['tradeId'])['unpricedReason'] == 'rights_mark_corrected'
    run(path, saved)
    # The corrected customer is outside this exact provider selection. It cannot
    # poison a separate genuine signed package or revive the customer's old mark.
    path, saved, packet = submit(provider, provider_book, 'provider_independent_signed_rights_and_fee', ('-7.5', '-6.5', '-8.5'))
    scope_and_authority(saved, packet, customer['tradeId'])
    run(path, saved)
    print('PASS financial retained PRICE: exact R2 native rights/cash identities, independent signed scenarios, frozen knowledge after correction, unrelated scope and ordinary worker parity.', flush=True)


if __name__ == '__main__':
    if not os.environ.get('DOTNET'):
        raise RuntimeError('DOTNET must select the installed SDK for required financial Transport worker qualification.')
    main(checks=checks, fixture_factory=fixture)
