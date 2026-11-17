"""Qualify actual frozen-clock service PRICE jobs on disposable stock PostgreSQL."""
import json
import os
from pathlib import Path
import shutil
import subprocess
from uuid import uuid4

from check_reference_editing import main
from check_risk_jobs_db import provision_worker
from gas_park_loan_fixture import ParkLoanFixture, fixture, literal, rejected

ROOT = Path(__file__).resolve().parents[1]


def differences(expected, actual, path='$'):
    if isinstance(expected, dict) and isinstance(actual, dict):
        return [item for key in sorted(set(expected) | set(actual)) for item in
            ([path + '.' + key] if key not in expected or key not in actual else differences(expected[key], actual[key], path + '.' + key))]
    if isinstance(expected, list) and isinstance(actual, list) and len(expected) == len(actual):
        return [item for index, (left, right) in enumerate(zip(expected, actual)) for item in differences(left, right, f'{path}[{index}]')]
    return [] if expected == actual else [path]


def checks(query, manifest):
    f = ParkLoanFixture(query, manifest)
    directory = Path(query('SHOW unix_socket_directories;')).resolve()
    assert directory.name.startswith('oetrm-ref-')
    market = f.valuation_inputs()
    login = 'synthetic_service_price_worker'
    provision_worker(query, manifest, login)
    query("INSERT INTO security.role_permissions SELECT role_id,'ReadGasOwnedHoldings' FROM security.role_permissions "
          "WHERE permission='RunScenario' ON CONFLICT DO NOTHING;")
    frozen = directory / 'service-price-binaries'
    shutil.copytree(Path(os.environ.get('PARK_LOAN_PRICE_BINARIES', ROOT / 'tests/OpenEtrm.Api.Checks/bin/Debug/net10.0')), frozen)
    stamps = {p.name: p.read_bytes() for p in frozen.glob('OpenEtrm.*.dll')}
    dotnet = str(Path(os.environ['DOTNET']).resolve())
    connection = directory / 'service-price-connection'
    connection.write_text(f'Host={directory};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=2')
    connection.chmod(0o600)
    config = directory / 'service-price-worker.json'
    config.write_text(json.dumps(dict(connectionFile=str(connection), leaseSeconds=30, pollSeconds=1, timeoutSeconds=60)))
    config.chmod(0o600)

    def managed(path, verify=False):
        result = subprocess.run([dotnet, str(frozen / 'OpenEtrm.Api.Checks.dll'),
            '--gas-park-loan-worker-verify' if verify else '--gas-park-loan-worker-submit', str(path)],
            cwd=ROOT, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        print(result.stdout.strip(), flush=True)
        return json.loads(path.with_suffix('.price-worker.json').read_text())

    def submit(booked, book, exposure, fee, components, label, native=True, selected_trade=False):
        packet = dict(host=str(directory), manifest=manifest, at=f.at, businessDate=f.day, contextId=f.capture(),
            bookId=book, tradeId=booked['tradeId'], contractId=f.contract, markId=market['markId'], rateIds=market['rateIds'],
            expectedBase=str(exposure * 3 + fee), expectedUp=str(exposure * 4 + fee), expectedDown=str(exposure * 2 + fee),
            expectedServiceComponents=components, checkpoint=label, nativeOwnership=native, selectedTrade=selected_trade)
        native_holdings = f.holdings(book, context=packet['contextId'])
        packet['expectedNativePositions'] = [dict(ownerId=row['id'], bindingId=row['id']) for row in native_holdings['positions']] + [
            dict(ownerId=row['position_id'], bindingId=row['id']) for key in ['fees', 'deliveryCash'] for row in native_holdings[key]]
        path = directory / (label + '.json')
        path.write_text(json.dumps(packet)); path.chmod(0o600)
        saved = managed(path)
        assert query(f"SELECT risk.needs_gas(j) FROM risk.jobs j WHERE id='{saved['id']}';") == 't', \
            'A retained service requires current original-actor and worker gas authority even before native holdings exist.'
        if not native:
            retained_request = json.loads(query(f"SELECT request::text FROM risk.jobs WHERE id='{saved['id']}';"))
            for change in [dict(context_id=booked['beforeContextId']),
                           dict(request={**retained_request, 'bookIds': ['d1000000-0000-0000-0000-000000000001']}),
                           dict(request={**retained_request, 'tradeIds': [str(uuid4())]})]:
                assert query(f"SELECT NOT risk.needs_gas(jsonb_populate_record(NULL::risk.jobs,to_jsonb(j)||{literal(change)})) "
                    f"FROM risk.jobs j WHERE id='{saved['id']}';") == 't', 'Service discovery escaped its source-known book/trade scope.'
        return path, saved

    def run(path, saved):
        counts = query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),"
            "(SELECT count(*) FROM trading.gas_park_loan_commands),(SELECT count(*) FROM trading.gas_owned_commands))::text;")
        result = subprocess.run([dotnet, str(frozen / 'OpenEtrm.Api.dll'), '--risk-worker', 'once'], cwd=ROOT,
            env={**os.environ, 'OPENETRM_RISK_WORKER_CONFIG': str(config)}, capture_output=True, text=True, timeout=90)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        actual = json.loads(query(f"SELECT jsonb_build_object('state',state,'error',error_code,'result',result)::text FROM risk.jobs WHERE id='{saved['id']}';"))
        assert actual['state'] == 'completed' and actual['result'] == saved['report'], dict(state=actual['state'], error=actual['error'],
            differencePaths=differences(saved['report'], actual['result'])[:12], workerDiagnostics=result.stdout + result.stderr)
        managed(path, True)
        assert counts == query("SELECT jsonb_build_array((SELECT count(*) FROM trading.publications),(SELECT count(*) FROM market_data.observations),"
            "(SELECT count(*) FROM trading.gas_park_loan_commands),(SELECT count(*) FROM trading.gas_owned_commands))::text;")

    def scope_probes(path, saved):
        packet = json.loads(path.read_text())
        args = [f"'{f.catalog}'", f"'{packet['contextId']}'", f"'{packet['at']}'::timestamptz",
            f"ARRAY['{packet['bookId']}']::uuid[]", 'ARRAY[]::uuid[]',
            f"ARRAY['{packet['tradeId']}']::uuid[]" if packet['selectedTrade'] else 'ARRAY[]::uuid[]']

        def probe_call(sql):
            query(f"SET SESSION AUTHORIZATION {login};BEGIN;DO $probe$ DECLARE j jsonb;BEGIN "
                f"j=risk.claim(30,'{saved['engineVersion']}');PERFORM risk.bind(coalesce(j->>'id',j->>'jobId')::uuid,(j->>'leaseToken')::uuid);"
                f"PERFORM {sql};END$probe$;ROLLBACK;")

        def probe(arguments):
            probe_call(f"security.gas_park_loan_directory({','.join(arguments)})")

        probe(args)
        presence = [args[0],args[1],args[2],args[3],args[5],f"settlement_data.futures_margin_preview_authority('{f.catalog}')"]
        probe_call(f"security.require_gas_park_loan_presence({','.join(presence)})")
        for index, value in [(1,f"'{uuid4()}'"),(2,f"'{packet['at']}'::timestamptz+interval '1 second'"),
                             (3,f"ARRAY['{uuid4()}']::uuid[]"),(4,f"ARRAY['{uuid4()}']::uuid[]")]:
            forged=list(presence);forged[index]=value
            rejected(lambda: probe_call(f"security.require_gas_park_loan_presence({','.join(forged)})"), 'risk_job_scope')
        rejected(lambda: probe_call(f"security.require_gas_park_loan_presence({','.join(presence[:-1])},clock_timestamp()-interval '1 second')"), 'futures_margin_denied')
        rejected(lambda: probe([f"'{uuid4()}'", *args[1:]]), 'gas_owned_denied')
        for index, value in [(1, f"'{uuid4()}'"),
                             (2, f"'{packet['at']}'::timestamptz+interval '1 second'"),
                             (3, 'ARRAY[]::uuid[]'), (3, f"ARRAY['{uuid4()}']::uuid[]"),
                             (4, f"ARRAY['{uuid4()}']::uuid[]"), (5, f"ARRAY['{uuid4()}']::uuid[]")]:
            forged = list(args); forged[index] = value
            rejected(lambda: probe(forged), 'risk_job_scope')
        foreign = query(f"SELECT trade_id FROM trading.gas_park_loan_trades WHERE trade_id<>'{packet['tradeId']}' LIMIT 1;") or str(uuid4())
        rejected(lambda: probe_call(f"security.read_gas_park_loan('{f.catalog}','{foreign}','{packet['contextId']}','{packet['at']}')"), 'risk_job_scope')
        rejected(lambda: probe_call(f"security.gas_park_loan_trade_head('{f.catalog}','{foreign}')"), 'risk_job_scope')
        identities = [args[0], args[3], args[5] if packet['selectedTrade'] else 'NULL::uuid[]']
        probe_call(f"security.risk_gas_park_loan_trade_heads({','.join(identities)})")
        rejected(lambda: probe_call(f"security.risk_gas_park_loan_trade_heads('{uuid4()}',{identities[1]},{identities[2]})"), 'gas_park_loan_denied')
        rejected(lambda: probe_call(f"security.risk_gas_park_loan_trade_heads({identities[0]},ARRAY[]::uuid[],{identities[2]})"), 'risk_trade_scope')
        for index, value in [(1, f"ARRAY['{uuid4()}']::uuid[]"),
                             (2, f"ARRAY['{uuid4()}']::uuid[]")]:
            forged = list(identities); forged[index] = value
            rejected(lambda: probe_call(f"security.risk_gas_park_loan_trade_heads({','.join(forged)})"), 'risk_job_scope')
        assert query(f"SELECT NOT has_table_privilege('{login}','trading.gas_park_loan_trades','SELECT') "
            f"AND NOT has_function_privilege('{login}','security.book_gas_park_loan(uuid,jsonb,timestamptz)','EXECUTE') "
            f"AND NOT has_function_privilege('{login}','security.record_gas_park_loan(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean)','EXECUTE');") == 't'
        print('PASS service worker source and scope fences: exact catalog/context/instant/books/trades, no producer or raw-term grants', flush=True)

    def expiring_worker():
        alternate = 'synthetic_service_expiring_worker'
        provision_worker(query, manifest, alternate)
        base, gas = str(uuid4()), str(uuid4())
        actor = query(f"SELECT principal_id FROM risk.workers WHERE login='{alternate}';")
        query(f"INSERT INTO security.roles(id,code,name) VALUES('{base}','synthetic_service_worker_base','Synthetic worker base'),"
            f"('{gas}','synthetic_service_worker_gas','Synthetic worker gas');"
            f"INSERT INTO security.role_permissions SELECT '{base}',permission FROM security.role_permissions "
            "WHERE role_id=(SELECT id FROM security.roles WHERE code='risk_analyst') AND permission<>'ReadGasOwnedHoldings';"
            f"INSERT INTO security.role_permissions VALUES('{gas}','ReadGasOwnedHoldings');"
            f"DELETE FROM security.role_grants WHERE principal_id='{actor}';"
            f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{base}','{manifest['tenantId']}','{manifest['workspaceId']}',"
            f"clock_timestamp(),'9999-12-31',false),(gen_random_uuid(),'{actor}','{gas}','{manifest['tenantId']}','{manifest['workspaceId']}',"
            "clock_timestamp(),clock_timestamp()+interval '15 seconds',false);")
        deadline = query(f"SELECT granted_to FROM security.role_grants WHERE principal_id='{actor}' AND role_id='{gas}';")
        return alternate, deadline

    def expired_authority(path, saved, alternate, deadline):
        packet = json.loads(path.read_text())
        generation = query('SELECT generation FROM security.epoch;')
        query(f"SET SESSION AUTHORIZATION {alternate};BEGIN;DO $expiry$ DECLARE j jsonb;selected jsonb;BEGIN "
            f"j=risk.claim(30,'{saved['engineVersion']}');IF j IS NULL THEN RAISE EXCEPTION 'expected_current_worker_authority';END IF;"
            "PERFORM risk.bind(coalesce(j->>'id',j->>'jobId')::uuid,(j->>'leaseToken')::uuid);"
            f"selected=security.gas_park_loan_directory('{f.catalog}','{packet['contextId']}','{packet['at']}',ARRAY['{packet['bookId']}']::uuid[],ARRAY[]::uuid[],ARRAY[]::uuid[]);"
            f"IF jsonb_array_length(selected)<>1 OR selected->0->>'tradeId'<>'{packet['tradeId']}' THEN RAISE EXCEPTION 'missing_exact_service';END IF;"
            f"PERFORM pg_sleep(greatest(0,extract(epoch FROM '{deadline}'::timestamptz-clock_timestamp()))+0.05);"
            "BEGIN "
            f"PERFORM security.gas_park_loan_directory('{f.catalog}','{packet['contextId']}','{packet['at']}',ARRAY['{packet['bookId']}']::uuid[],ARRAY[]::uuid[],ARRAY[]::uuid[]);"
            "RAISE EXCEPTION 'expired_worker_was_allowed';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$expiry$;ROLLBACK;")
        assert generation == query('SELECT generation FROM security.epoch;'), 'Clock expiry changed the security generation instead of isolating current worker authority.'
        assert query(f"SELECT state='queued' AND security_generation=(SELECT generation FROM security.epoch) AND risk.authorized(j) "
            f"FROM risk.jobs j WHERE id='{saved['id']}';") == 't'
        assert query(f"SELECT NOT security.principal_has_permission(principal_id,tenant_id,workspace_id,'ReadGasOwnedHoldings',clock_timestamp()) "
            f"AND security.principal_has_permission(principal_id,tenant_id,workspace_id,'ReadTrades',clock_timestamp()) "
            f"AND security.principal_has_permission(principal_id,tenant_id,workspace_id,'RunScenario',clock_timestamp()) FROM risk.workers WHERE login='{alternate}';") == 't'
        print('PASS current worker gas authority: valid claimed read then gas-only grant expiry denies with unchanged epoch and original-actor access', flush=True)

    def actor_authority(saved):
        original = query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
        # Roll back this isolated revocation and its epoch: the queued source and
        # its original security generation survive for the actual worker replay.
        role = str(uuid4())
        access = query(f"BEGIN;INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_service_actor','Synthetic service actor');"
            f"INSERT INTO security.role_permissions SELECT DISTINCT '{role}'::uuid,permission FROM security.role_permissions WHERE role_id IN("
            f"SELECT role_id FROM security.role_grants WHERE principal_id='{original}') AND permission<>'ReadGasOwnedHoldings';"
            f"DELETE FROM security.role_grants WHERE principal_id='{original}';INSERT INTO security.role_grants "
            f"SELECT gen_random_uuid(),'{original}','{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp(),'9999-12-31',false;"
            f"SELECT NOT risk.authorized(j) FROM risk.jobs j WHERE id='{saved['id']}';"
            f"SET SESSION AUTHORIZATION {login};SELECT risk.claim(30,'{saved['engineVersion']}') IS NULL;ROLLBACK;")
        assert access == 't\nt', access
        print('PASS retained service original-actor authority: worker grant cannot replace revoked current gas access', flush=True)

    for kind, perspective, direction in [('loan', 'customer', 1), ('park', 'provider', 1),
                                         ('loan', 'provider', -1), ('park', 'customer', -1)]:
        label = kind + '_' + perspective
        f.at = '2020-12-20T16:00:00Z'; f.day = '2020-12-20'
        owned, book = f.account(label)
        claim = f.account(label + '_claim', kind='operator_balance', book=book)[0] if direction < 0 else None
        for account, actions in [(owned, ['gas.owned.receive_title', 'gas.owned.dispatch', 'gas.loan.borrow', 'gas.loan.repay']),
                                 (claim, ['gas.operator.record', 'gas.operator.settle'])]:
            if account:
                for action in actions:
                    f.bind(account, action)
        fee = -12 if perspective == 'customer' else 12
        f.at = '2020-12-19T16:00:00Z'; f.day = '2020-12-19'
        body = dict(operationId=str(uuid4()), profile='physical_gas_park_loan_v1', convention='physical_gas_fixed_quantity_v4',
            knowledgeMode='current', contextId=f.capture(), snapshotId=str(uuid4()), bookId=book,
            definitionRevisionId=manifest['revisionId'], legalEntityId=f.entity, legalEntityRevision=1,
            counterpartyId=f.party, counterpartyRevision=1, ownedAccountId=owned, claimAccountId=claim,
            serviceKind=kind, perspective=perspective, quantity='720', advanceAt='2020-12-20T10:00:00Z',
            returnAt='2020-12-21T12:00:00Z', fixedFeeAmount=str(fee), feeDirection='pay' if fee < 0 else 'receive',
            feeDueAt='2020-12-22T12:00:00Z', titlePolicy='fungible_gas_title_transfer_return_v1',
            returnPolicy='same_quantity_partial_returns_v1', returnTiming='accepted_advance_then_returns_v1',
            carrying='nominal_mark_v1')
        booked = json.loads(f.call(f"SELECT security.book_gas_park_loan('{f.catalog}',{literal(body)},'{f.at}')::text;"))
        booked['beforeContextId'] = body['contextId']
        f.at = '2020-12-20T16:00:00Z'; f.day = '2020-12-20'
        # No native holdings or native ownership policy are needed for the two
        # service commitments and independent fee. They still require gas access.
        expiry = expiring_worker() if kind == 'loan' and perspective == 'customer' else None
        before = submit(booked, book, 0, fee, 3, label + '_unadvanced', native=False)
        if expiry is not None:
            expired_authority(*before, *expiry)
        scope_probes(*before)
        if kind == 'loan' and perspective == 'customer':
            actor_authority(before[1])
        selected_job = submit(booked, book, 0, fee, 3, label + '_selected_trade', native=False, selected_trade=True) if kind == 'loan' and perspective == 'customer' else None
        if direction < 0:
            evidence = f.evidence(owned, label + '_funding', 'title_receipt', '720', '2020-12-19T08:00:00Z')
            policy = query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)="
                f"('{f.catalog}','{owned}','gas.owned.receive_title') ORDER BY revision DESC LIMIT 1;")
            lot = f.native(owned, 'receive_title', dict(evidenceId=evidence, quantity='720', titleAt='2020-12-19T08:00:00Z',
                availableAt='2020-12-19T08:00:00Z', originPolicy='opening_owned_stock_v1', carrying='nominal_mark_v1'), policy=policy)['id']
        else:
            lot = None
        agreement = f.evidence(owned, label + '_agreement', 'loan_agreement', '720', '2020-12-20T10:00:00Z')
        title = f.evidence(owned, label + '_advance', 'title_receipt' if direction > 0 else 'physical_delivery', '720', '2020-12-20T10:00:00Z')
        claim_evidence = f.evidence(claim, label + '_claim', 'operator_statement', '720', '2020-12-20T10:00:00Z') if claim else None
        selected = f.capture()
        state = json.loads(f.call(f"SELECT security.use_market_context('{f.catalog}','{selected}');SELECT security.use_trading_context('{f.catalog}','{f.at}');"
            f"SELECT security.read_gas_park_loan('{f.catalog}','{booked['tradeId']}','{selected}','{f.at}')::text;"))
        action = dict(expectedVersion=state['stateVersion'], agreementEvidenceId=agreement, evidenceId=title,
            claimEvidenceId=claim_evidence, lotId=lot, reservationId=None, effectiveAt='2020-12-20T10:00:00Z')
        f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{uuid4()}','{booked['tradeId']}','advance',"
            f"{literal(action)},'{f.at}','{selected}',false);")
        # A later accepted handoff cannot enter the earlier job's source context.
        run(*before)
        if selected_job is not None:
            scope_probes(*selected_job)
            run(*selected_job)
        after = submit(booked, book, 0 if direction > 0 else 720, fee, 0, label + '_advanced')
        if kind == 'loan' and perspective == 'customer':
            actor_authority(after[1])
        run(*after)
        print('PASS actual frozen PRICE worker: ' + label + ', exact before/after ownership, complete report parity and no financial writes', flush=True)
    assert all((frozen / name).read_bytes() == value for name, value in stamps.items())


if __name__ == '__main__':
    if not os.environ.get('DOTNET'):
        raise ValueError('Set DOTNET to the installed SDK executable; actual managed workers are required.')
    built = subprocess.run([os.environ['DOTNET'], 'build', 'tests/OpenEtrm.Api.Checks'], cwd=ROOT, capture_output=True, text=True, timeout=120)
    if built.returncode:
        raise RuntimeError(built.stdout + built.stderr)
    main(checks=checks, fixture_factory=fixture)
