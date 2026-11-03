#!/usr/bin/env python3
"""Qualify native fit authority after advisory waits using owned stock PostgreSQL."""
import argparse
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from uuid import uuid4

SOURCE = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description='Refuse volatility-fit replay and reuse after token expiry during an observed database lock wait.')
parser.add_argument('--pg-bin', type=Path, required=True)
PG = parser.parse_args().pg_bin.resolve()
sys.path.insert(0, str(SOURCE / 'tools'))
from preview_fixture import fixture as baseline
from exchange_fixture import fixture as exchange

env = {k: v for k, v in os.environ.items() if not k.startswith('PG')}
env['PGTZ'] = 'UTC'
owned = Path(tempfile.mkdtemp(prefix='openetrm-vol-expiry-', dir='/tmp')).resolve()
data = owned / 'data'
marker = str(uuid4())
(owned / '.check-owned').write_text(marker)
port = '55449'
psql = [str(PG / 'psql'), '-X', '-h', str(owned), '-p', port, '-U', 'preview_owner', '-d', 'postgres', '-qAt', '-v', 'ON_ERROR_STOP=1']


def query(sql, checked=True):
    r = subprocess.run(psql, input=sql, text=True, capture_output=True, env=env, timeout=60, cwd=SOURCE)
    if checked and r.returncode:
        raise RuntimeError(r.stderr)
    return r.stdout.strip() if checked else r


def literal(value):
    return "'" + str(value).replace("'", "''") + "'"


startup_attempted = False
try:
    subprocess.run([str(PG / 'initdb'), '-D', str(data), '--auth-local=trust', '--auth-host=reject', '--encoding=UTF8', '--locale=C', '--no-instructions', '--username=preview_owner'],
                   check=True, capture_output=True, env=env, cwd=SOURCE)
    startup_attempted = True
    subprocess.run([str(PG / 'pg_ctl'), '-D', str(data), '-w', '-t', '15', '-l', str(owned / 'postgres.log'), '-o',
                    shlex.join(['-k', str(owned), '-h', '', '-p', port, '-c', 'track_commit_timestamp=on']), 'start'],
                   check=True, capture_output=True, env=env, cwd=SOURCE)
    assert Path(query('SHOW data_directory;')).resolve() == data
    for path in sorted((SOURCE / 'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
        query(path.read_text())
    reference, market, security, _ = baseline(SOURCE)
    for sql in (reference, market, security):
        query(sql)
    reference, market, security, trading, manifest = exchange(SOURCE)
    for sql in (reference, market, security, trading):
        query(sql)
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    actor = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','trader');")
    role, grant = str(uuid4()), str(uuid4())
    query(f"INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_surface_fitter','Synthetic surface fitter');"
          f"INSERT INTO security.role_permissions VALUES('{role}','PublishMarketData');"
          f"INSERT INTO security.role_grants VALUES('{grant}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);")
    frame_at = next(f['at'] for f in manifest['frames'] if f['at'].startswith('2026-10-16'))
    day = frame_at[:10]
    pin = query(f"SELECT id FROM market_data.observations WHERE(catalog_id,definition_revision_id,observed_at)=('{catalog}','{manifest['revisionId']}','{frame_at}') ORDER BY id LIMIT 1;")

    def token(ttl=60):
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',{ttl});")

    context = query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');"
                    f"SELECT security.capture_date_context('{catalog}','{day}',NULL);COMMIT;").splitlines()[-1]
    product = {'referenceCatalogId': manifest['referenceCatalogId'], 'referenceRevisionId': manifest['referenceRevisionId'], 'code': 'nymex_on'}
    request = {'businessDate': day, 'convention': 'vol_surface_fit_v1', 'engineVersion': 'synthetic_expiry_engine_v1',
               'futuresConvention': manifest['futuresConvention'], 'model': 'smile_quadratic_log_moneyness_v1',
               'product': product, 'solverPolicy': 'american_futures_option_crr_v1'}
    frame = {'contextId': context, 'id': str(uuid4()), 'at': frame_at, 'observationIds': [pin], 'curveContracts': {}, 'frozenObservationIds': []}

    def submit(operation):
        return f"risk.submit_vol_surface_fit('{catalog}','{operation}','{context}','{manifest['revisionId']}','{frame_at}',{literal(json.dumps(request))}::jsonb,{literal(json.dumps(frame))}::jsonb)"

    def bind(issued, application):
        return (f"SET application_name='{application}';SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{issued}');"
                f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{frame_at}');")

    first = str(uuid4())
    initial = query(bind(token(), 'synthetic_vol_initial') + f'SELECT {submit(first)};COMMIT;')
    assert 'queued' in initial
    original_job = json.loads(initial.splitlines()[-1])['jobId']

    def run_case(mode):
        operation = first if mode == 'replay' else str(uuid4())
        if mode == 'replay':
            text = tenant + workspace + actor + operation
            seed = 730849136
        else:
            text = tenant + workspace + actor + catalog + product['referenceCatalogId'] + product['referenceRevisionId'] + product['code'] + '|' + day + '|' + request['model']
            seed = 730849138
        app = 'synthetic_vol_' + mode
        holder = subprocess.Popen(psql, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env, cwd=SOURCE)
        holder.stdin.write(f"SET application_name='{app}_holder';BEGIN;SELECT pg_advisory_xact_lock(hashtextextended({literal(text)},{seed}));\n")
        holder.stdin.flush()
        waiter = None
        try:
            deadline = time.monotonic() + 5
            while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{app}_holder' AND state='idle in transaction';") != '1':
                assert time.monotonic() < deadline
                time.sleep(.02)
            issued = token(3)
            expiry = query(f"SELECT expires_at FROM security.request_contexts WHERE id='{issued}';")
            before_aliases = query('SELECT count(*) FROM risk.vol_surface_fit_requests;')
            stdout = {}
            def waiting():
                stdout['r'] = query(bind(issued, app) + f"SELECT {submit(operation)};SELECT 'permission_after='||security.has_permission('PublishMarketData');COMMIT;", checked=False)
            waiter = threading.Thread(target=waiting)
            waiter.start()
            deadline = time.monotonic() + 5
            while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{app}' AND wait_event_type='Lock' AND wait_event='advisory';") != '1':
                assert time.monotonic() < deadline and waiter.is_alive(), stdout
                time.sleep(.02)
            while query(f"SELECT clock_timestamp()>={literal(expiry)}::timestamptz;") != 't':
                time.sleep(.05)
            assert query(f"SELECT expires_at<=clock_timestamp() FROM security.request_contexts WHERE id='{issued}';") == 't'
            holder.stdin.write('COMMIT;\n');holder.stdin.flush();holder.stdin.close()
            holder.wait(timeout=5)
            waiter.join(timeout=10)
            assert not waiter.is_alive()
            r = stdout['r']
            after_aliases = query('SELECT count(*) FROM risk.vol_surface_fit_requests;')
            assert r.returncode != 0 and 'risk_job_denied' in r.stderr, 'Expired fit authority was accepted after an observed lock wait: ' + mode
            assert after_aliases == before_aliases, 'An expired request recorded a new alias'
            print('PASS native fit ' + mode + ': observed lock wait, natural token expiry, refusal and no alias write', flush=True)
        finally:
            if holder.poll() is None:
                holder.terminate();holder.wait(timeout=5)
            if waiter is not None and waiter.is_alive():
                waiter.join(timeout=5)

    # Healthy replay and queued-key reuse still retain the first outcome.
    replay = query(bind(token(), 'synthetic_vol_replay_valid') + f'SELECT {submit(first)};COMMIT;')
    assert json.loads(replay.splitlines()[-1])['jobId'] == original_job
    reuse = query(bind(token(), 'synthetic_vol_reuse_valid') + f'SELECT {submit(str(uuid4()))};COMMIT;')
    assert json.loads(reuse.splitlines()[-1])['jobId'] == original_job
    run_case('replay')
    run_case('queued_key_reuse')
finally:
    if startup_attempted:
        assert (owned / '.check-owned').read_text() == marker
        assert data.resolve() == owned / 'data' and not data.is_symlink()
        subprocess.run([str(PG / 'pg_ctl'), '-D', str(data), '-m', 'immediate', '-w', '-t', '15', 'stop'], capture_output=True, env=env, cwd=SOURCE, timeout=20)
        status = subprocess.run([str(PG / 'pg_ctl'), '-D', str(data), 'status'], capture_output=True, env=env, cwd=SOURCE, timeout=5)
        if status.returncode != 3:
            raise RuntimeError('Owned PostgreSQL stop is uncertain; retain its directory: ' + str(owned))
    shutil.rmtree(owned)

print('PASS native volatility-fit expiry and valid replay/reuse checks; owned cluster removed.', flush=True)
