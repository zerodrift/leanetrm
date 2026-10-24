#!/usr/bin/env python3
"""Verify durable risk ownership only in a newly allocated stock PostgreSQL cluster."""
import argparse
import json
import os
from pathlib import Path
import select
import shlex
import shutil
import subprocess
import tempfile
from uuid import uuid4

from preview_fixture import fixture, trading_fixture

ROOT = Path(__file__).resolve().parents[1]


def provision_worker(query, manifest, login='preview_risk_worker'):
    if not login.replace('_', '').isalnum():
        raise ValueError('Worker fixture login must be a simple identifier')
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    service = str(uuid4())
    query(f"""CREATE ROLE {login} LOGIN IN ROLE openetrm_risk_worker;
    INSERT INTO security.principals VALUES('{service}','service',true);
    INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{service}','{login}','Synthetic risk worker',true,1);
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{service}',id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='risk_analyst';
    INSERT INTO risk.workers VALUES('{login}','{service}','{tenant}','{workspace}','{catalog}',true);""")


def run_checks(query, manifest, psql=None, environment=None):
    assert query("SELECT to_regclass('risk.jobs') IS NOT NULL;") == 't', 'Durable risk storage is missing'
    catalog, revision, tenant, workspace = (manifest[key] for key in ('catalogId', 'revisionId', 'tenantId', 'workspaceId'))
    provision_worker(query, manifest, 'synthetic_risk_worker')
    provision_worker(query, manifest, 'synthetic_other_worker')
    frame_seed = manifest['frames'][0]
    at = frame_seed['at']
    frame = {'id': str(uuid4()), 'name': 'Synthetic retained inputs', 'at': at,
             'observationIds': frame_seed['observationIds'], 'curveContracts': frame_seed['curveContracts']}

    def token(profile='risk'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def user(sql, profile='risk'):
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token(profile)}');{sql};COMMIT;")

    context = user(f"SELECT security.capture_date_context('{catalog}','{at[:10]}',NULL)")
    frame['contextId'] = context
    books = json.loads(user(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');SELECT coalesce(jsonb_agg(id),'[]'::jsonb) FROM trading.books WHERE catalog_id='{catalog}'"))
    assert books, 'Fixture must contain an explicit book scope'
    request = {'context': {'snapshotId': frame['id'], 'contextId': context, 'knowledgeMode': 'retained', 'convention': 'fixed_total_cash_execution_pnl_v1'},
               'bookIds': books, 'portfolioIds': [], 'bumps': [{'seriesId': '90000000-0000-0000-0000-000000000001', 'quote': 'test_gas_outright', 'size': '0.01', 'convention': 'absolute_quote_v1'}],
               'convention': 'synthetic_risk_check_v1', 'numericalPolicy': 'synthetic_exact_v1', 'detail': 'books', 'stress': [], 'engineVersion': 'synthetic_engine_v1'}
    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"

    def submit(operation=None, payload=None):
        return json.loads(user(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');SELECT risk.submit('{catalog}','{operation or uuid4()}','{context}','{revision}','{at}',{literal(payload or request)},{literal(frame)})"))

    def worker(sql, login='synthetic_risk_worker'):
        return query(f"SET SESSION AUTHORIZATION {login};{sql};")

    def reject(sql, expected):
        try:
            sql()
            raise AssertionError(f'Accepted operation expected to reject with {expected}')
        except RuntimeError as error:
            assert expected in str(error), str(error)

    operation = str(uuid4())
    job = submit(operation)
    assert job['state'] == 'queued'
    assert submit(operation)['jobId'] == job['jobId']
    reject(lambda: submit(operation, {**request, 'detail': 'holdings'}), 'risk_job_conflict')
    reject(lambda: user(f"SELECT risk.read('{job['jobId']}')", 'trader'), 'risk_job_denied')
    assert query("SELECT has_table_privilege('openetrm_runtime','risk.jobs','SELECT');") == 'f'
    assert query("SELECT has_function_privilege('openetrm_runtime','risk.claim(integer,text)','EXECUTE');") == 'f'
    assert query("SELECT has_table_privilege('synthetic_risk_worker','market_data.observations','INSERT');") == 'f'
    assert query("SELECT has_function_privilege('synthetic_risk_worker','security.use_context(uuid)','EXECUTE');") == 'f'
    assert query("SELECT pg_has_role('synthetic_risk_worker','openetrm_runtime','MEMBER');") == 'f'
    assert worker("SELECT risk.claim(30,'different_engine_v1') IS NULL") == 't'
    assert json.loads(user(f"SELECT risk.read('{job['jobId']}')"))['state'] == 'queued'
    claim = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    assert claim['jobId'] == job['jobId'] and claim['request'] == request and claim['frame'] == frame
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    reject(lambda: worker(f"SELECT risk.finish('{job['jobId']}','{uuid4()}','{{\"value\":\"9\"}}',NULL)"), 'risk_job_fence')
    reject(lambda: worker(f"SELECT risk.finish('{job['jobId']}','{claim['leaseToken']}','{{\"value\":\"9\"}}',NULL)", 'synthetic_other_worker'), 'risk_job_fence')
    bound = worker(f"BEGIN;SELECT risk.bind('{job['jobId']}','{claim['leaseToken']}');SELECT count(*) FROM market_data.revisions WHERE catalog_id='{catalog}' AND id='{revision}';COMMIT")
    assert bound.splitlines()[-1] == '1'
    assert worker(f"SELECT count(*) FROM market_data.revisions WHERE catalog_id='{catalog}'") == '0'
    expired_service = query(f"""BEGIN;SET SESSION AUTHORIZATION synthetic_risk_worker;
    SELECT risk.bind('{job['jobId']}','{claim['leaseToken']}');RESET SESSION AUTHORIZATION;
    UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.03 seconds'
     WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='synthetic_risk_worker');
    SET SESSION AUTHORIZATION synthetic_risk_worker;SELECT pg_sleep(0.06);
    SELECT count(*) FROM market_data.revisions WHERE catalog_id='{catalog}';ROLLBACK;""")
    assert expired_service.splitlines()[-1] == '0'
    assert worker(f"SELECT risk.heartbeat('{job['jobId']}','{claim['leaseToken']}',30)") == 'running'
    worker(f"SELECT risk.finish('{job['jobId']}','{claim['leaseToken']}','{{\"value\":\"9\"}}',NULL)")
    completed = json.loads(user(f"SELECT risk.read('{job['jobId']}')"))
    assert completed['state'] == 'completed' and completed['result'] == {'value': '9'}
    assert json.loads(user(f"SELECT risk.cancel('{job['jobId']}')"))['state'] == 'completed'
    reject(lambda: worker(f"SELECT risk.finish('{job['jobId']}','{claim['leaseToken']}','{{}}',NULL)"), 'risk_job_fence')
    print('PASS risk jobs: exact captured inputs, idempotency, private ownership, transaction-local reads and single terminal publication', flush=True)

    if psql is not None:
        first, second = submit(), submit()
        process = subprocess.Popen([str(x) for x in psql], cwd=ROOT, env=environment,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            process.stdin.write("SET SESSION AUTHORIZATION synthetic_risk_worker;BEGIN;SELECT risk.claim(30,'synthetic_engine_v1');\n")
            process.stdin.flush()
            assert select.select([process.stdout], [], [], 10)[0], 'First claim did not become ready'
            claimed_first = json.loads(process.stdout.readline())
            claimed_second = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')", 'synthetic_other_worker'))
            assert claimed_first['jobId'] == first['jobId'] and claimed_second['jobId'] == second['jobId']
            process.stdin.write('COMMIT;\n\\q\n')
            process.stdin.flush()
            assert process.wait(timeout=10) == 0
            worker(f"SELECT risk.finish('{first['jobId']}','{claimed_first['leaseToken']}',NULL,'synthetic_failure')")
            worker(f"SELECT risk.finish('{second['jobId']}','{claimed_second['leaseToken']}',NULL,'synthetic_failure')", 'synthetic_other_worker')
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
        print('PASS risk worker concurrency: an uncommitted claim locks only its own job', flush=True)

    queued = submit()
    assert json.loads(user(f"SELECT risk.cancel('{queued['jobId']}')"))['state'] == 'cancelled'
    running = submit()
    active = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    assert active['jobId'] == running['jobId']
    assert json.loads(user(f"SELECT risk.cancel('{running['jobId']}')"))['state'] == 'cancellation_requested'
    assert worker(f"SELECT risk.heartbeat('{running['jobId']}','{active['leaseToken']}',30)") == 'cancellation_requested'
    assert worker(f"SELECT risk.finish('{running['jobId']}','{active['leaseToken']}','{{}}',NULL)") == 'cancellation_requested'
    assert worker(f"SELECT risk.acknowledge_cancel('{running['jobId']}','{active['leaseToken']}')") == 'cancelled'
    delayed = submit()
    delayed_claim = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    user(f"SELECT risk.cancel('{delayed['jobId']}')")
    query(f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{delayed['jobId']}';")
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    assert json.loads(user(f"SELECT risk.read('{delayed['jobId']}')"))['state'] == 'cancellation_requested'
    reject(lambda: worker(f"SELECT risk.finish('{delayed['jobId']}','{delayed_claim['leaseToken']}','{{}}',NULL)"), 'risk_job_fence')
    assert worker(f"SELECT risk.acknowledge_cancel('{delayed['jobId']}','{delayed_claim['leaseToken']}')") == 'cancelled'
    print('PASS risk cancellation: queued cancellation, worker acknowledgment and cancellation-first publication rejection', flush=True)

    interrupted = submit()
    old = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    query(f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{interrupted['jobId']}';")
    new = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')", 'synthetic_other_worker'))
    assert new['jobId'] == old['jobId'] and new['leaseToken'] != old['leaseToken'] and new['attempt'] == old['attempt'] + 1
    assert new['frame'] == old['frame'] and new['request'] == old['request']
    reject(lambda: worker(f"SELECT risk.finish('{old['jobId']}','{old['leaseToken']}','{{}}',NULL)"), 'risk_job_fence')
    worker(f"SELECT risk.finish('{new['jobId']}','{new['leaseToken']}',NULL,'synthetic_failure')", 'synthetic_other_worker')
    assert json.loads(user(f"SELECT risk.read('{new['jobId']}')"))['state'] == 'failed'
    print('PASS risk recovery: expired worker ownership retries identical pins and stale workers cannot publish', flush=True)

    oversized = submit()
    oversized_claim = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    reject(lambda: worker(f"SELECT risk.finish('{oversized['jobId']}','{oversized_claim['leaseToken']}',jsonb_build_object('payload',repeat('x',32000001)),NULL)"), 'risk_result_limit')
    worker(f"SELECT risk.finish('{oversized['jobId']}','{oversized_claim['leaseToken']}',NULL,'risk_result_limit')")
    oversized_result = json.loads(user(f"SELECT risk.read('{oversized['jobId']}')"))
    assert oversized_result['state'] == 'failed' and oversized_result['errorCode'] == 'risk_result_limit' and oversized_result['result'] is None
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    print('PASS risk result bounds: oversized output rejects and becomes a terminal structured failure without retrying', flush=True)

    revoked = submit()
    proof = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    actor = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk';")
    query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{actor}';UPDATE security.workspace_memberships SET enabled=true WHERE principal_id='{actor}';")
    reject(lambda: worker(f"SELECT risk.finish('{revoked['jobId']}','{proof['leaseToken']}','{{}}',NULL)"), 'risk_job_authority_changed')
    query(f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{revoked['jobId']}';")
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    assert json.loads(user(f"SELECT risk.read('{revoked['jobId']}')"))['state'] == 'failed'
    fresh = submit()
    query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{actor}';")
    reject(lambda: user(f"SELECT risk.read('{fresh['jobId']}')"), 'risk_job_denied')
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    query(f"UPDATE security.workspace_memberships SET enabled=true WHERE principal_id='{actor}';")
    print('PASS risk authorization: queued and running revoke/restore invalidation, current result denial and no authority revival', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    args = parser.parse_args()
    bins = {name: args.pg_bin.resolve() / name for name in ('initdb', 'pg_ctl', 'psql')}
    for binary in bins.values():
        if not binary.is_file():
            parser.error(f'Missing installed binary: {binary}')
    environment = {k: v for k, v in os.environ.items() if not k.startswith('PG')}
    environment['PGTZ'] = 'UTC'
    directory = Path(tempfile.mkdtemp(prefix='openetrm-risk-db-', dir='/tmp')).resolve()
    data = directory / 'data'
    startup_attempted = False
    safe_to_remove = True
    def run(command, **kwargs):
        return subprocess.run([str(x) for x in command], cwd=ROOT, env=environment, text=True, capture_output=True, timeout=30, **kwargs)
    try:
        result = run([bins['initdb'], '-D', data, '--auth-local=trust', '--auth-host=reject', '--encoding=UTF8', '--locale=C', '--no-instructions', '--username=preview_owner'])
        if result.returncode:
            raise RuntimeError(result.stderr)
        startup_attempted, safe_to_remove = True, False
        result = run([bins['pg_ctl'], '-D', data, '-w', '-t', '15', '-l', directory / 'postgres.log', '-o', shlex.join(['-k', str(directory), '-h', '', '-p', '55439', '-c', 'track_commit_timestamp=on']), 'start'])
        if result.returncode:
            raise RuntimeError(result.stderr)
        psql = [bins['psql'], '-X', '-h', directory, '-p', '55439', '-U', 'preview_owner', '-d', 'postgres', '-qAt', '-v', 'ON_ERROR_STOP=1']
        def query(sql):
            result = run(psql, input=sql)
            if result.returncode:
                raise RuntimeError(result.stderr)
            return result.stdout.strip()
        assert Path(query('SHOW data_directory;')).resolve() == data
        for path in sorted((ROOT / 'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            query(path.read_text())
        reference, market, security, manifest = fixture(ROOT)
        for sql in (reference, market, security):
            query(sql)
        query(trading_fixture(manifest['catalogId']))
        run_checks(query, manifest, psql, environment)
    finally:
        if startup_attempted:
            stopped = run([bins['pg_ctl'], '-D', data, '-m', 'immediate', '-w', '-t', '15', 'stop'])
            safe_to_remove = stopped.returncode == 0 or not (data / 'postmaster.pid').exists()
        if safe_to_remove:
            shutil.rmtree(directory)
        else:
            print(f'Owned cluster retained because shutdown is unconfirmed: {directory}')


if __name__ == '__main__':
    main()
