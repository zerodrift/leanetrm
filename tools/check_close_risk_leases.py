"""Actual existing-close worker lease and completion checks in an owned database."""
from datetime import datetime, timedelta, timezone
from copy import deepcopy
from contextlib import contextmanager
from fractions import Fraction
import json
from pathlib import Path
import subprocess
import time
from uuid import uuid4

from benchmark_ppa_http import HttpFixture, terms, expected_value
from exact_values import instant, fraction
from preview_client import PreviewClient


def check_work(work, cells, admission):
    """Independent count for ten years, five sides and three monthly factors."""
    months = 10*12
    scenarios = 1+2*3*months
    assert work['uniqueInstruments'] == work['uniqueValuations'] == work['positionCount'] == 1
    assert work['scenarioCount'] == work['unitPricerCalls'] == scenarios
    assert work['volumeComputations'] == months
    assert work['rowEvaluations'] == cells*5*scenarios
    assert admission['declaredRows'] == cells*5 and admission['declaredRowScenarios'] == cells*5*scenarios
    assert work['resolvedCurveMonths']+work['reusedCurveMonths'] == 2*months*scenarios
    # PpaReads prepares the base before the risk loop. The connected cache
    # supplies those base values without new evaluation or explicit-parent reuse.
    # Each signed direct-factor shock evaluates one node and reuses the other
    # factors from its parent. These counters exclude the earlier preparation.
    factors = 3*months
    assert work['rebuiltCurveNodes'] == 2*factors, work
    assert work['reusedCurveNodes'] == 2*factors*(factors-1), work


def run_checks(url, query, manifest, dotnet, owned_root, app_environment, psql, environment):
    root = Path(owned_root).resolve()
    repository = Path(__file__).resolve().parents[1]
    assert (root / '.openetrm-preview').is_file() and (root / 'data/postmaster.pid').is_file()
    from check_pilot_close import prepare
    service = prepare(query, manifest)
    owner = PreviewClient(url); owner.bootstrap(); owner.call('profile', {'profile': 'approver'})
    fixture = HttpFixture(url, manifest, None)
    fixture.heads = {s['id']: s['head'] for s in fixture.editor.call('interval-series')['series']}
    first, last = '2031-11-01', '2041-11-01'
    fixture.load(60, first, last)
    book = str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{book}','d0000000-0000-0000-0000-000000000001','Synthetic renewable risk',1,'2020-01-01T00:00:00Z');")
    fixture.book(terms(manifest, first=first, last=last), first+'T04:00:00Z', last+'T04:00:00Z', book=book)
    small_book = str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{small_book}','d0000000-0000-0000-0000-000000000001','Synthetic terminal ordering',1,'2020-01-01T00:00:00Z');")
    fixture.book(terms(manifest), '2031-11-02T04:00:00Z', '2031-11-03T05:00:00Z', book=small_book)
    frame = next(f for f in owner.call('workspace')['frames'] if instant(f['at']) == instant(manifest['closeRisk']['at']) and 'test_existing_close_energy' not in f['curveContracts'] and not f.get('ppaBundle'))
    remaining = (instant(manifest['closeRisk']['at']) - datetime.now(timezone.utc)).total_seconds()
    if remaining > 0:
        print(f'Waiting {remaining:.1f}s for the selected close instant.', flush=True)
        time.sleep(remaining)
    close = owner.call('market/closes/register', dict(requestId=str(uuid4()), snapshotId=frame['id'], name='Synthetic renewable close'))
    policy = owner.call('jobs/pilot-close/catalog')['valuationPolicyChoices'][0]['policy']
    now = datetime.now(timezone.utc)
    body = {'schedule': {'requestId': str(uuid4()), 'name': 'Synthetic renewable completion', 'operation': 'close_book_risk_v1',
        'dueAt': (now+timedelta(minutes=10)).isoformat(), 'expiresAt': (now+timedelta(hours=1)).isoformat(),
        'intervalSeconds': None, 'timeZone': 'UTC', 'misfire': 'run_once', 'overlap': 'forbid', 'timeoutSeconds': 1800,
        'maximumAttempts': 3, 'profileId': None, 'profileVersion': None},
        'definition': {'closeId': close['id'], 'bookIds': [book], 'knownAtPolicy': 'valuation_instant_v1',
            'pendingActuals': 'require_actuals_v1', 'coveragePolicy': 'ppa_coverage_v1',
            'revisionPolicy': 'exact_bundle_whole_operation_v1', 'ppaImplementation': 'ppa_capabilities_v1', 'valuationPolicy': policy}}
    scheduler, runtime = root/'lease.scheduler.connection', root/'lease.runtime.connection'
    for path, login in ((scheduler, 'pilot_fixture_scheduler'), (runtime, 'pilot_fixture_runtime')):
        path.write_text(f'Host={root};Port=55439;Database=postgres;Username={login};SSL Mode=Disable;Application Name=synthetic-renewable-risk')
        path.chmod(0o600)
    worker_env = {k: v for k, v in app_environment.items() if not k.startswith(('OPENETRM_PREVIEW_', 'ASPNETCORE_'))}
    worker_env.update(OPENETRM_WORKER_CONNECTION_FILE=str(scheduler), OPENETRM_PILOT_RUNTIME_CONNECTION_FILE=str(runtime),
        OPENETRM_PILOT_MANIFEST_FILE=str(root/'manifest.json'), OPENETRM_WORKER_POLL_SECONDS='1',
        OPENETRM_WORKER_LEASE_SECONDS='15', ASPNETCORE_URLS='not-an-http-listener')
    published = root/'lease-worker'
    built = subprocess.run([str(dotnet), 'publish', 'src/OpenEtrm.Worker', '--no-restore', '-c', 'Release', '-o', str(published)], cwd=repository, env=worker_env, capture_output=True, text=True, timeout=120)
    assert built.returncode == 0, built.stdout+built.stderr
    command = [str(dotnet), str(published/'OpenEtrm.Worker.dll'), '--once']
    evidence = {'leaseSeconds': 15, 'renewalIntervalSeconds': 5, 'faults': [], 'renewals': []}
    def create(timeout=1800, selected_book=book):
        request = deepcopy(body)
        request['schedule'].update(requestId=str(uuid4()), timeoutSeconds=timeout)
        request['definition']['bookIds'] = [selected_book]
        job = owner.call('jobs/close-book-risk/create', request)
        query(f"UPDATE automation.jobs SET next_due_at=clock_timestamp()-interval '1 second' WHERE id='{job['id']}';")
        return job
    def state(job):
        value = query(f"SELECT row_to_json(r)::text FROM automation.runs r WHERE job_id='{job['id']}';")
        return json.loads(value) if value else None
    def report_count(run):
        return int(query(f"SELECT count(*) FROM automation.pilot_reports WHERE run_id='{run['id']}';"))
    def pins(job):
        return query(f"SELECT to_jsonb(p)::text FROM automation.close_risk_profiles p JOIN automation.jobs j ON j.close_risk_profile_id=p.id WHERE j.id='{job['id']}';")
    def cancel(job):
        version = int(query(f"SELECT version FROM automation.jobs WHERE id='{job['id']}';"))
        return owner.call('jobs/control', dict(requestId=str(uuid4()), id=job['id'], action='cancel', expectedVersion=version))
    def until(predicate, seconds=30):
        deadline = time.monotonic()+seconds
        while time.monotonic() < deadline:
            result = predicate()
            if result: return result
            time.sleep(.1)
        raise AssertionError('Timed out waiting for an observed durable transition')
    @contextmanager
    def running(label):
        with (root/(label+'.log')).open('w+') as log:
            worker = subprocess.Popen(command, cwd=repository, env=worker_env, stdout=log, stderr=log)
            try: yield worker, log
            except BaseException:
                log.flush(); log.seek(0); print(log.read(), flush=True)
                raise
            finally:
                if worker.poll() is None:
                    worker.terminate()
                    try: worker.wait(timeout=10)
                    except subprocess.TimeoutExpired: worker.kill(); worker.wait(timeout=10)
    def cpu(job, worker, after_fence=0):
        initial = until(lambda: (r if r and r['fence'] > after_fence else None) if (r := state(job)) else None)
        def prepared():
            assert worker.poll() is None, 'Worker exited before completing preparation'
            return query(f"SELECT count(*) FROM automation.pilot_run_contexts p JOIN security.request_contexts q ON q.id=p.context_id WHERE p.run_id='{initial['id']}' AND p.fence={initial['fence']} AND q.backend_pid IS NOT NULL;") != '0'
        until(prepared)
        assert query("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic-renewable-risk' AND usename='pilot_fixture_runtime' AND xact_start IS NOT NULL;") == '0', 'CPU valuation retained a database transaction'
        return initial
    def no_report(job):
        run = state(job)
        assert report_count(run) == 0 and run['state'] != 'succeeded', run
    def denied_old(run, token):
        query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;DO $$BEGIN PERFORM security.use_context('{token}');PERFORM automation.check_close_risk_run();RAISE EXCEPTION 'Stale worker admitted';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;")
        result = subprocess.run(psql, input=f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.renew_close_risk_lease('{run['id']}',{run['fence']},'{run['worker_id']}',15);", env=environment, capture_output=True, text=True, timeout=10)
        assert result.returncode != 0 and 'job_fence' in result.stderr, result.stderr

    # Success-first cancellation reconciles the terminal outcome, even when
    # the caller observed the version before publication committed.
    small_job = create(selected_book=small_book)
    with running('success-first') as (worker, log):
        worker.wait(timeout=120); log.seek(0)
        assert worker.returncode == 0, log.read()
    small_run = state(small_job)
    assert small_run['state'] == 'succeeded' and report_count(small_run) == 1
    completed_cancel = dict(requestId=str(uuid4()), id=small_job['id'], action='cancel', expectedVersion=small_job['version'])
    outcome = owner.call('jobs/control', completed_cancel)
    assert outcome['state'] == 'completed' and state(small_job) == small_run
    assert owner.call('jobs/control', completed_cancel) == outcome
    changed_cancel = {**completed_cancel, 'action': 'pause'}
    assert owner.call('jobs/control', changed_cancel, expected=409)['code'] == 'job_conflict'
    viewer = PreviewClient(url); viewer.bootstrap(); viewer.call('profile', {'profile': 'viewer'})
    viewer.call('jobs/control', completed_cancel, expected=403)
    print('PASS durable completion: success-first cancellation reconciles the completed outcome', flush=True)

    # Faults are injected only after actual preparation committed and actual
    # scenario evaluation began. No timing sleep replaces risk calculation.
    for fault in ('renewal_failure', 'cancellation', 'authority_loss', 'overall_deadline'):
        job = create(45 if fault == 'overall_deadline' else 1800)
        original_pins = pins(job)
        with running(fault) as (worker, log):
            started = time.monotonic(); initial = cpu(job, worker)
            prepared_seconds = time.monotonic()-started
            fault_at = time.monotonic()
            if fault == 'renewal_failure':
                query('REVOKE EXECUTE ON FUNCTION automation.renew_close_risk_lease(uuid,bigint,uuid,integer) FROM openetrm_scheduler;')
            elif fault == 'cancellation': cancel(job)
            elif fault == 'authority_loss':
                query(f"UPDATE security.principals SET enabled=false WHERE id='{service}';")
            mutation_seconds = time.monotonic()-fault_at
            try:
                worker.wait(timeout=60)
                assert worker.returncode != 0, 'A refused occurrence was reported as completed'
                no_report(job)
                final = state(job)
                assert final['deadline_at'] == initial['deadline_at'] and pins(job) == original_pins
                assert instant(final['lease_until']) <= instant(final['deadline_at'])
            finally:
                if fault == 'renewal_failure':
                    query('GRANT EXECUTE ON FUNCTION automation.renew_close_risk_lease(uuid,bigint,uuid,integer) TO openetrm_scheduler;')
                if fault == 'authority_loss':
                    query(f"UPDATE security.principals SET enabled=true WHERE id='{service}';")
            assert mutation_seconds < 5, 'Control/authority mutation was blocked by CPU calculation'
            evidence['faults'].append(dict(fault=fault, preparationSeconds=prepared_seconds, mutationSeconds=mutation_seconds, stoppedSeconds=time.monotonic()-fault_at))
            if fault != 'cancellation': cancel(job)
            print(f'PASS durable completion: {fault}, no report, unchanged pins/deadline; preparation {prepared_seconds:.3f}s, control {mutation_seconds:.3f}s', flush=True)

    # Kill a real calculating worker after an observed renewal, then replace
    # only after natural lease expiry. The admitted cutoff excludes a correction
    # made while the first worker owns the occurrence.
    job = create(); original_pins = pins(job)
    with running('worker-death') as (worker, log):
        initial = cpu(job, worker)
        until(lambda: instant(state(job)['lease_until']) > instant(initial['lease_until']))
        token = query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{initial['id']}',{initial['fence']},'{initial['worker_id']}');")
        fixture.revise(manifest['ppaBookings']['workloads']['60']['forecast'], first+'T04:00:00Z', ['0.9']*25)
        worker.kill(); worker.wait(timeout=10)
        no_report(job)
    until(lambda: query(f"SELECT lease_until<=clock_timestamp() FROM automation.runs WHERE id='{initial['id']}';") == 't', 20)
    denied_old(initial, token)
    from pg_commit_ack_proxy import CommitAckProxy
    def committed():
        run = state(job)
        count = report_count(run)
        return dict(runId=run['id'], state=run['state'], reportCount=count) if run['state'] == 'succeeded' and count == 1 else None
    with CommitAckProxy(root/'.s.PGSQL.55439', root/'ack-proxy', committed) as proxy:
        for path in (scheduler, runtime): path.write_text(path.read_text().replace(f'Host={root};', f'Host={proxy.directory};'))
        with running('replacement-lost-ack') as (worker, log):
            started = time.monotonic()
            replacement = cpu(job, worker, initial['fence'])
            assert replacement['id'] == initial['id'] and replacement['fence'] == initial['fence']+1
            assert replacement['attempt'] == initial['attempt']+1 and replacement['deadline_at'] == initial['deadline_at']
            assert pins(job) == original_pins
            denied_old(initial, token)
            last_lease = replacement['lease_until']
            while worker.poll() is None and time.monotonic()-started < 1800:
                current = state(job)
                assert current['deadline_at'] == initial['deadline_at']
                if current['lease_until'] != last_lease:
                    evidence['renewals'].append(dict(observedSeconds=time.monotonic()-started, leaseUntil=current['lease_until']))
                    last_lease = current['lease_until']
                time.sleep(.25)
            worker.wait(timeout=10); log.seek(0)
            assert worker.returncode == 0, {
                'workerLog': log.read(), 'commitAcknowledgement': proxy.events,
                'proxyErrors': proxy.errors, 'durableState': state(job),
                'reportCount': report_count(state(job)),
            }
            elapsed = time.monotonic()-started
            assert elapsed > 120, f'The real calculation only took {elapsed:.3f}s'
            assert proxy.dropped.is_set() and not proxy.errors, (proxy.events, proxy.errors)
            evidence.update(elapsedSeconds=elapsed, commitAcknowledgement=proxy.events)
    for path in (scheduler, runtime): path.write_text(path.read_text().replace(f'Host={root}/ack-proxy;', f'Host={root};'))
    final = state(job)
    assert final['state'] == 'succeeded' and report_count(final) == 1
    assert query(f"SELECT count(*) FROM automation.audit WHERE run_id='{final['id']}' AND action='succeeded';") == '1'
    report = owner.call('jobs/pilot-close/results?runId='+final['id'])
    assert report['context']['contextId'] == job['admission']['contextId'] and pins(job) == original_pins
    expected, cells, hours, peak = expected_value(first, last, 'America/New_York')
    assert len(report['totals']) == 1 and fraction(report['totals'][0]['value']) == expected
    assert len(report['risk']) == 360 and len(report['positions']) == 1
    # The fixture's monthly peak/off-peak energy and monthly certificates are
    # independently integrated with Python civil-time membership and Fractions.
    for risk in report['risk']:
        label = risk['factor']['label']; dates = label.split(' (')[1].rstrip(')').split(' — ')
        _, _, h, p = expected_value(dates[0], dates[1], 'America/New_York')
        delta = 40*(p if 'peak' in label and 'off' not in label else h-p if 'off' in label else h)
        shock = Fraction(risk['factor']['bumpSize'])
        assert fraction(risk['delta']['value']) == delta, (label, risk['delta'], delta)
        assert fraction(risk['gamma']['value']) == 0
        assert fraction(risk['upPnl']) == delta*shock and fraction(risk['downPnl']) == -delta*shock
        assert fraction(risk['up']) == expected+delta*shock and fraction(risk['down']) == expected-delta*shock
    assert all(fraction(b[k]) == 0 for b in report['ppaBalances'] for k in ('overdueKnownBalance', 'overdueEstimated'))
    check_work(report['work'], cells, job['admission'])
    evidence.update(expectedBase=str(expected), intervalRows=cells, admittedWork=job['admission'], actualWork=report['work'])
    print('DURABLE_COMPLETION_EVIDENCE='+json.dumps(evidence, sort_keys=True), flush=True)
    print(f'PASS durable completion: real ten-year hourly risk {elapsed:.3f}s; independent complete report, replacement, pinned correction and lost COMMIT acknowledgement', flush=True)
    with running('completed-retry') as (worker, log):
        worker.wait(timeout=15); assert worker.returncode == 0
    assert report_count(final) == 1 and state(job) == final
    # A completion that committed first remains the outcome of later cancel.
    outcome = cancel(job)
    assert outcome['state'] == 'completed' and state(job) == final
    print('PASS durable completion: completed retries and later cancellation preserve the single committed outcome', flush=True)
