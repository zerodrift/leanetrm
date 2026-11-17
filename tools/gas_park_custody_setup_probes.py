"""Original-authority setup reads across an actual first-source lock wait."""
import json
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from uuid import uuid4


def qualify_setup_authority(f, binaries, dotnet, customer, provider):
    query = f.query
    directory = Path(query('SHOW unix_socket_directories;')).resolve()
    actor = query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    tenant, workspace = (f.manifest[key] for key in ('tenantId', 'workspaceId'))
    failures = []
    for label, owned_initial, denial, booked in [
            ('customer_late_owned', False, True, customer),
            ('customer_expired_original_owned', True, True, customer),
            ('customer_expired_original_ops', True, True, customer),
            ('provider_no_owned', False, False, provider)]:
        context = f.capture()
        base, custody, owned = (str(uuid4()) for _ in range(3))
        query(f"INSERT INTO security.roles VALUES('{base}','synthetic_setup_base_{base[:8]}','Synthetic setup base {base[:8]}'),"
            f"('{custody}','synthetic_setup_ops_{custody[:8]}','Synthetic setup custody read {custody[:8]}'),"
            f"('{owned}','synthetic_setup_owned_{owned[:8]}','Synthetic setup stock read {owned[:8]}');"
            f"INSERT INTO security.role_permissions SELECT DISTINCT '{base}'::uuid,permission FROM security.role_permissions WHERE role_id IN("
            f"SELECT role_id FROM security.role_grants WHERE principal_id='{actor}') AND permission NOT IN('ReadGasOwnedHoldings','ReadGasOperations');"
            f"INSERT INTO security.role_permissions VALUES('{custody}','ReadGasOperations'),('{owned}','ReadGasOwnedHoldings');"
            f"DELETE FROM security.role_grants WHERE principal_id='{actor}';"
            f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{base}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);")
        boundary = query("SELECT clock_timestamp()+interval '12 seconds';")
        if label == 'customer_expired_original_ops':
            query(f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{custody}','{tenant}','{workspace}',clock_timestamp(),'{boundary}',false),"
                f"(gen_random_uuid(),'{actor}','{custody}','{tenant}','{workspace}','{boundary}','9999-12-31',false);")
        else:
            query(f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{custody}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);")
        if label == 'customer_expired_original_owned':
            query(f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{owned}','{tenant}','{workspace}',clock_timestamp(),'{boundary}',false),"
                f"(gen_random_uuid(),'{actor}','{owned}','{tenant}','{workspace}','{boundary}','9999-12-31',false);")
        elif owned_initial or label == 'customer_late_owned':
            from_at = 'clock_timestamp()' if owned_initial else f"'{boundary}'::timestamptz"
            query(f"INSERT INTO security.role_grants VALUES(gen_random_uuid(),'{actor}','{owned}','{tenant}','{workspace}',{from_at},'9999-12-31',false);")
        packet = dict(host=str(directory), manifest=f.manifest, contextId=context, at=f.at, businessDate=f.day,
            tradeId=booked['tradeId'], instrumentId=booked['instrumentId'], setupAuthorityProbe=True,
            expectedInitialOwned=owned_initial, expectedDenial=denial, checkpoint=label)
        if label == 'customer_expired_original_ops':
            packet['expectedOpsUntil'] = boundary
        if label == 'customer_expired_original_owned':
            packet['expectedOwnedUntil'] = boundary
        path = directory / (label + '.json')
        path.write_text(json.dumps(packet)); path.chmod(0o600)
        generation = query('SELECT generation FROM security.epoch;')
        publication = f.publication()
        with ThreadPoolExecutor(max_workers=2) as pool:
            holder = pool.submit(query, "SET application_name='synthetic_custody_setup_source_wait';BEGIN;"
                "LOCK TABLE trading.history_boundaries IN ACCESS EXCLUSIVE MODE;"
                + (f"SELECT pg_sleep(greatest(0,extract(epoch FROM '{boundary}'::timestamptz-clock_timestamp()))+0.1);" if denial else "SELECT pg_sleep(1);") + "COMMIT;")
            for _ in range(200):
                if query("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE application_name='synthetic_custody_setup_source_wait' AND wait_event='PgSleep');") == 't':
                    break
                time.sleep(0.01)
            else:
                raise AssertionError('The setup source lock was not established.')
            running = pool.submit(subprocess.run, [dotnet, str(Path(binaries) / 'OpenEtrm.Api.Checks.dll'), '--gas-park-custody-native', str(path)],
                cwd=Path(__file__).resolve().parents[1], capture_output=True, text=True, timeout=60)
            waited = False
            blocked = set()
            for _ in range(500):
                blocked.update(query("SELECT query FROM pg_stat_activity WHERE usename='preview_runtime' AND wait_event_type='Lock';").splitlines())
                if any(statement.startswith('SELECT security.admit_trading_business_source') for statement in blocked):
                    waited = True
                    break
                if running.done():
                    break
                time.sleep(0.01)
            result = running.result(timeout=60)
            holder.result(timeout=30)
        assert waited, 'The setup request never reached its source-admission history wait: ' + repr(blocked) + result.stdout + result.stderr
        assert path.with_suffix('.setup-entry.json').is_file(), 'Original authority was not observed before the first source read.'
        assert generation == query('SELECT generation FROM security.epoch;'), 'Timed grants must not change security generation.'
        assert publication == f.publication(), 'A setup authority probe published a financial fact.'
        if denial:
            # Both mandatory and stock permissions are valid now. A refusal
            # therefore concerns original admission, not a missing current grant.
            assert f.read(booked['tradeId'])['tradeId'] == booked['tradeId']
        if result.returncode:
            print('FAIL custody setup original authority: ' + label + '\n' + result.stdout + result.stderr, flush=True)
            failures.append(label)
        else:
            print(result.stdout.strip(), flush=True)
    if failures:
        raise AssertionError('\n'.join(failures))
    print('PASS custody setup: missing original stock read and expired original custody read refuse after a real source wait; provider needs no stock grant.', flush=True)
