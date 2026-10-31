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

from benchmark_ppa_http import HttpFixture, terms, expected_value, midnight
from exact_values import instant, fraction
from preview_client import PreviewClient


def check_work(work, cells, admission):
    """Independent count for twelve native months, two sides and energy factors."""
    months, factors = 12, 24
    scenarios = 1+2*factors
    assert work['uniqueInstruments'] == work['uniqueValuations'] == work['positionCount'] == 1
    assert work['scenarioCount'] == work['unitPricerCalls'] == scenarios
    assert work['volumeComputations'] == months
    assert admission['declaredRows'] == cells*2 and admission['declaredRowScenarios'] == cells*2*scenarios
    # Both base sides evaluate every interval. Each month's peak and off-peak
    # shocks recompute only that physical month, once in each direction.
    assert work['rowEvaluations'] == cells*(2+2*2), work
    assert work['uniqueSideCalculations'] == 2+2*factors, work
    assert work['calibratedBlocks'] == 2*months and work['reusedBlocks'] == 0, work
    assert work['scheduleGraphEvaluatedNodes'] > 0 and work['scheduleGraphReusedReads'] > 0, work
    assert work['resolvedCurveMonths'] == months and work['reusedCurveMonths'] == 0, work
    # Connected execution creates no native curve receipts. Its own graph
    # counters retain actual evaluations/reads, including base preparation.
    assert work['rebuiltCurveNodes'] == work['reusedCurveNodes'] == 0, work
    # Each of the 24 source factors is evaluated in base and its two shocks;
    # unchanged roots are read again while pricing the remaining monthly cells.
    assert work['curveGraphEvaluatedNodes'] >= 3*factors, work
    assert work['curveGraphReusedReads'] > 0, work


def run_checks(url, query, manifest, dotnet, owned_root, app_environment, psql, environment):
    root = Path(owned_root).resolve()
    repository = Path(__file__).resolve().parents[1]
    assert (root / '.openetrm-preview').is_file() and (root / 'data/postmaster.pid').is_file()
    from check_pilot_close import prepare
    service = prepare(query, manifest)
    owner = PreviewClient(url); owner.bootstrap(); owner.call('profile', {'profile': 'approver'})
    fixture = HttpFixture(url, manifest, None)
    fixture.heads = {s['id']: s['head'] for s in fixture.editor.call('interval-series')['series']}
    first, last = '2031-11-01', '2032-11-01'
    minutes = 15
    config = manifest['ppaBookings']['workloads'][str(minutes)]
    begin, end = (midnight(day, config['zone']).isoformat() for day in (first, last))
    cells = fixture.load(minutes, first, last)
    book = str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{book}','d0000000-0000-0000-0000-000000000001','Synthetic renewable risk',1,'2020-01-01T00:00:00Z');")
    fixture.book_resuming(terms(manifest, minutes, first, last, five=False), begin, end, book=book)
    small_book = str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{small_book}','d0000000-0000-0000-0000-000000000001','Synthetic terminal ordering',1,'2020-01-01T00:00:00Z');")
    fixture.book(terms(manifest, minutes, five=False),
        midnight('2031-11-02', config['zone']).isoformat(), midnight('2031-11-03', config['zone']).isoformat(), book=small_book)
    frame = next(f for f in owner.call('workspace')['frames'] if instant(f['at']) == instant(manifest['closeRisk']['at']) and 'test_existing_close_energy' not in f['curveContracts'] and not f.get('ppaBundle'))
    remaining = (instant(manifest['closeRisk']['at']) - datetime.now(timezone.utc)).total_seconds()
    if remaining > 0:
        print(f'Waiting {remaining:.1f}s for the selected close instant.', flush=True)
        while remaining > 0:
            time.sleep(min(remaining, 30))
            remaining = (instant(manifest['closeRisk']['at']) - datetime.now(timezone.utc)).total_seconds()
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
        OPENETRM_WORKER_LEASE_SECONDS='5', ASPNETCORE_URLS='not-an-http-listener')
    published = root/'lease-worker'
    built = subprocess.run([str(dotnet), 'publish', 'src/OpenEtrm.Worker', '--no-restore', '-c', 'Release', '-o', str(published)], cwd=repository, env=worker_env, capture_output=True, text=True, timeout=120)
    assert built.returncode == 0, built.stdout+built.stderr
    command = [str(dotnet), str(published/'OpenEtrm.Worker.dll'), '--once']
    evidence = {'leaseSeconds': 5, 'renewalIntervalSeconds': 5/3, 'faults': [], 'renewals': [], 'admissions': [],
        'apiConfiguration': 'Debug', 'workerConfiguration': 'Release',
        'workload': {'months': 12, 'intervalMinutes': minutes, 'sides': 2, 'instruments': 1, 'bindings': 1,
            'books': 1, 'nativeIntervals': cells, 'factors': 24, 'scenarios': 49, 'clock': config['clock']},
        'calculationTransactionReleaseObserved': False}
    def create(timeout=1800, selected_book=book):
        request = deepcopy(body)
        request['schedule'].update(requestId=str(uuid4()), timeoutSeconds=timeout)
        request['definition']['bookIds'] = [selected_book]
        started = time.monotonic()
        try:
            job = owner.call('jobs/close-book-risk/create', request)
        except BaseException:
            print(f'Close-risk admission failed after {time.monotonic()-started:.3f}s; timeout {timeout}s, large workload {selected_book == book}', flush=True)
            raise
        evidence['admissions'].append(dict(elapsedSeconds=time.monotonic()-started, timeoutSeconds=timeout, largeWorkload=selected_book == book))
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
    def claimed(job, worker, after_fence=0):
        def observed():
            assert worker.poll() is None, 'Worker exited before the expected ownership transition'
            run = state(job)
            return run if run and run['fence'] > after_fence else None
        return until(observed)
    def phase(run):
        if run['state'] != 'running':
            return run['state']
        committed = query(f"SELECT count(*) FROM automation.pilot_run_contexts p JOIN security.request_contexts q ON q.id=p.context_id WHERE p.run_id='{run['id']}' AND p.fence={run['fence']} AND q.backend_pid IS NOT NULL;") != '0'
        transactions = int(query("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic-renewable-risk' AND usename='pilot_fixture_runtime' AND xact_start IS NOT NULL;"))
        if not committed:
            return 'preparation'
        if transactions:
            return 'publication'
        # The preparation context becomes visible only after its transaction
        # commits. This observation qualifies the actual transaction-free phase.
        current_state = query(f"SELECT state FROM automation.runs WHERE id='{run['id']}';")
        if current_state != 'running':
            return current_state
        evidence['calculationTransactionReleaseObserved'] = True
        return 'calculation'
    def renewed(job, worker, initial):
        def observed():
            assert worker.poll() is None, 'The real workload finished before a lease renewal was observed'
            current = state(job)
            return current if instant(current['lease_until']) > instant(initial['lease_until']) else None
        current = until(observed)
        return current, phase(current)
    def preparing(job, worker, initial):
        def observed():
            assert worker.poll() is None, 'Worker finished before cold preparation was observed'
            return query("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic-renewable-risk' AND usename='pilot_fixture_runtime' AND xact_start IS NOT NULL;") != '0'
        until(observed)
        return state(job), 'preparation'
    def calculating(job, worker, initial):
        def prepared():
            assert worker.poll() is None, 'Worker finished before a calculation-phase fault could be observed'
            return query(f"SELECT count(*) FROM automation.pilot_run_contexts p JOIN security.request_contexts q ON q.id=p.context_id WHERE p.run_id='{initial['id']}' AND p.fence={initial['fence']} AND q.backend_pid IS NOT NULL;") != '0'
        until(prepared)
        assert query("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic-renewable-risk' AND usename='pilot_fixture_runtime' AND xact_start IS NOT NULL;") == '0', 'CPU valuation retained a database transaction'
        current = state(job)
        assert current['state'] == 'running', 'The real calculation finished before its fault could be injected'
        assert instant(current['lease_until']) > instant(initial['lease_until']), 'Cold preparation did not renew the lease'
        evidence['calculationTransactionReleaseObserved'] = True
        return current, 'calculation'
    def no_report(job):
        run = state(job)
        assert report_count(run) == 0 and run['state'] != 'succeeded', run
    def denied_old(run, token):
        query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;DO $$BEGIN PERFORM security.use_context('{token}');PERFORM automation.check_close_risk_run();RAISE EXCEPTION 'Stale worker admitted';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;")
        result = subprocess.run(psql, input=f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.renew_close_risk_lease('{run['id']}',{run['fence']},'{run['worker_id']}',5);", env=environment, capture_output=True, text=True, timeout=10)
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

    # Control and authority mutations retain the transaction-free calculation
    # gate. The five-second overall deadline expires during cold preparation;
    # its capped lease cannot extend beyond that deadline.
    # No delay substitutes for preparation or calculation.
    for fault in ('renewal_failure', 'cancellation', 'authority_loss', 'overall_deadline'):
        job = create(5 if fault == 'overall_deadline' else 1800)
        original_pins = pins(job)
        with running(fault) as (worker, log):
            started = time.monotonic(); initial = claimed(job, worker)
            renewed_run, fault_phase = (preparing if fault == 'overall_deadline' else calculating)(job, worker, initial)
            ready_seconds = time.monotonic()-started
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
                if fault == 'overall_deadline':
                    assert datetime.now(timezone.utc) >= instant(initial['deadline_at']), 'Worker stopped before its selected overall deadline'
            finally:
                if fault == 'renewal_failure':
                    query('GRANT EXECUTE ON FUNCTION automation.renew_close_risk_lease(uuid,bigint,uuid,integer) TO openetrm_scheduler;')
                if fault == 'authority_loss':
                    query(f"UPDATE security.principals SET enabled=true WHERE id='{service}';")
            assert mutation_seconds < 5, dict(message='Control/authority mutation was blocked by CPU calculation', fault=fault, phase=fault_phase, mutationSeconds=mutation_seconds)
            evidence['faults'].append(dict(fault=fault, phase=fault_phase, faultReadySeconds=ready_seconds,
                observedLeaseUntil=renewed_run['lease_until'], mutationSeconds=mutation_seconds, stoppedSeconds=time.monotonic()-fault_at))
            if fault != 'cancellation': cancel(job)
            print(f'PASS durable completion: {fault} during {fault_phase}, no report, unchanged pins/deadline; fault ready {ready_seconds:.3f}s, control {mutation_seconds:.3f}s', flush=True)

    # Kill a real worker after an observed renewal, recording its phase. Replace
    # only after natural lease expiry. The admitted cutoff excludes a correction
    # made while the first worker owns the occurrence.
    job = create(); original_pins = pins(job)
    with running('worker-death') as (worker, log):
        initial = claimed(job, worker)
        renewed_run, death_phase = renewed(job, worker, initial)
        evidence['workerDeath'] = dict(phase=death_phase, leaseUntil=renewed_run['lease_until'])
        token = query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{initial['id']}',{initial['fence']},'{initial['worker_id']}');")
        fixture.revise(config['forecast'], begin, ['0.9']*25)
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
            replacement = claimed(job, worker, initial['fence'])
            assert replacement['id'] == initial['id'] and replacement['fence'] == initial['fence']+1
            assert replacement['attempt'] == initial['attempt']+1 and replacement['deadline_at'] == initial['deadline_at']
            assert pins(job) == original_pins
            denied_old(initial, token)
            last_lease = replacement['lease_until']
            while worker.poll() is None and time.monotonic()-started < 1800:
                current = state(job)
                assert current['deadline_at'] == initial['deadline_at']
                if current['lease_until'] != last_lease:
                    evidence['renewals'].append(dict(observedSeconds=time.monotonic()-started, leaseUntil=current['lease_until'], phase=phase(current)))
                    last_lease = current['lease_until']
                time.sleep(.1)
            worker.wait(timeout=10); log.seek(0)
            assert worker.returncode == 0, {
                'workerLog': log.read(), 'commitAcknowledgement': proxy.events,
                'proxyErrors': proxy.errors, 'durableState': state(job),
                'reportCount': report_count(state(job)),
            }
            elapsed = time.monotonic()-started
            assert elapsed > evidence['leaseSeconds'] and evidence['renewals'], f'The real preparation/calculation did not cross an observed five-second lease: {elapsed:.3f}s'
            assert proxy.dropped.is_set() and not proxy.errors, (proxy.events, proxy.errors)
            evidence.update(elapsedSeconds=elapsed, commitAcknowledgement=proxy.events)
    for path in (scheduler, runtime): path.write_text(path.read_text().replace(f'Host={root}/ack-proxy;', f'Host={root};'))
    final = state(job)
    assert final['state'] == 'succeeded' and report_count(final) == 1
    assert query(f"SELECT count(*) FROM automation.audit WHERE run_id='{final['id']}' AND action='succeeded';") == '1'
    report = owner.call('jobs/pilot-close/results?runId='+final['id'])
    assert report['context']['contextId'] == job['admission']['contextId'] and pins(job) == original_pins
    five_side_value, expected_cells, hours, peak = expected_value(first, last, config['zone'], minutes, config['peakStart'])
    assert cells == expected_cells
    expected = five_side_value-80*hours
    assert len(report['totals']) == 1 and fraction(report['totals'][0]['value']) == expected
    assert len(report['risk']) == 24 and len(report['positions']) == 1
    # Native peak/off-peak energy is independently integrated with the fixture's
    # declared civil clock and exact Fractions. This book has no certificates.
    observed_factors = set()
    expected_factors = {}
    for ordinal, (year, month) in enumerate([(2031, 11), (2031, 12), *((2032, month) for month in range(1, 11))]):
        start, end = f'{year:04}-{month:02}-01', f'{year+(month==12):04}-{month%12+1:02}-01'
        for block in ('peak', 'off'):
            expected_factors[f'test_ppa_long_{minutes}_{block}_{ordinal} ({start} — {end})'] = (start, end, block)
    for risk in report['risk']:
        label = risk['factor']['label']
        assert label in expected_factors and label not in observed_factors, label
        start, end, block = expected_factors[label]
        _, _, h, p = expected_value(start, end, config['zone'], minutes, config['peakStart'])
        observed_factors.add(label)
        delta = 40*(h-p if block == 'off' else p)
        shock = Fraction(risk['factor']['bumpSize'])
        assert fraction(risk['delta']['value']) == delta, (label, risk['delta'], delta)
        assert fraction(risk['gamma']['value']) == 0
        assert fraction(risk['upPnl']) == delta*shock and fraction(risk['downPnl']) == -delta*shock
        assert fraction(risk['up']) == expected+delta*shock and fraction(risk['down']) == expected-delta*shock
    assert observed_factors == set(expected_factors), observed_factors
    assert all(fraction(b[k]) == 0 for b in report['ppaBalances'] for k in ('overdueKnownBalance', 'overdueEstimated'))
    print('DURABLE_COMPLETION_WORK='+json.dumps(dict(actual=report['work'], admission=job['admission']), sort_keys=True), flush=True)
    check_work(report['work'], cells, job['admission'])
    evidence.update(expectedBase=str(expected), intervalRows=cells, admittedWork=job['admission'], actualWork=report['work'])
    print('DURABLE_COMPLETION_EVIDENCE='+json.dumps(evidence, sort_keys=True), flush=True)
    print(f'PASS durable completion: real twelve-month quarter-hour preparation/risk {elapsed:.3f}s; independent complete report, replacement, pinned correction and lost COMMIT acknowledgement', flush=True)
    with running('completed-retry') as (worker, log):
        worker.wait(timeout=15); assert worker.returncode == 0
    assert report_count(final) == 1 and state(job) == final
    # A completion that committed first remains the outcome of later cancel.
    outcome = cancel(job)
    assert outcome['state'] == 'completed' and state(job) == final
    print('PASS durable completion: completed retries and later cancellation preserve the single committed outcome', flush=True)
