"""Owned synthetic qualification of scope scans and source-only child actions."""
from copy import deepcopy
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import subprocess
import time
from uuid import uuid4
from check_booked_side_programs import runtime_query


def run_checks(c, query, manifest, terms, frame, *, dotnet):
    catalog = manifest['catalogId']
    book = str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic daily billing scope',publication_order,effective_at FROM trading.books WHERE(catalog_id,id)=('{catalog}','{terms['bookId']}');")
    if query("SELECT to_regrole('pilot_fixture_scheduler') IS NULL;") == 't':
        from check_pilot_close import prepare
        prepare(query, manifest)
    query("INSERT INTO security.role_permissions SELECT role_id,'AmendTrade' FROM security.role_permissions WHERE permission='ImportObservations' ON CONFLICT DO NOTHING;")
    body = {**deepcopy(terms), 'bookId': book, 'operationId': str(uuid4())}
    saved = c.call('gas/composition/book', body)
    trade = saved['tradeId']
    rows = c.call('gas/quantities/read', dict(context=frame, tradeId=trade, selectionPolicy='operational_projection_v1'))
    obligation = rows['rows'][0]['obligationId']
    def quantity(day, kind, value, previous=None, status=None):
        return c.call('gas/quantities/record', dict(context=frame, tradeId=trade, expectedRevision=1,
            operationId=str(uuid4()), obligationId=obligation, day=day, quantity=value, kind=kind,
            status=status or ('final' if kind == 'allocated' else 'provisional'), expectedPreviousId=previous,
            evidence='Synthetic recurring billing source'))
    quantity('2021-01-01', 'scheduled', '100')
    first = quantity('2021-01-01', 'allocated', '100')
    policy = str(uuid4())
    c.call('lifecycle/policies/save', dict(operationId=str(uuid4()), policyId=policy, expectedRevision=0,
        action='gas.bill_daily', mode='automatic', fromAt='2020-01-01T00:00:00Z', untilAt='2030-01-01T00:00:00Z',
        implementation='explicit_lifecycle_action_policy_v1'))
    now = datetime.now(timezone.utc)
    source = dict(bookIds=[book], **{'from': '2021-01-01', 'to': '2021-01-03'},
        discoveryPolicy='gas_billing_scope_reassessment_v1', selectionPolicy='final_allocated_billing_v1',
        billingProfile='gas_final_daily_entry_cash_v1', roundingPolicy='currency_minor_units_to_even_v1',
        scheduledPolicy='manual_scheduled_quantity_v1', processingConvention='gas_processing_instant_utc_v1',
        knowledgePolicy='current_knowledge_at_execution_v1', policyId=policy, policyRevision=1, mode='automatic',
        readinessDelaySeconds=0, pageLimit=2, dispatchLimit=2, childTimeoutSeconds=30, childMaximumAttempts=2)
    operating = dict(convention=source['processingConvention'], businessDate=now.date().isoformat(),
        evaluationAt=now.isoformat(), knowledgeMode='current', retained=None)
    choices = c.call('gas/billing/enrollment-choices', operating)
    assert choices['canEnroll'] and any(b['id'] == book for b in choices['books'])
    assert any(p['id'] == policy and p['revision'] == 1 and p['mode'] == 'automatic' for p in choices['policies'])
    preview = c.call('gas/billing/enrollment-preview', dict(context=operating, source=source, after=None))
    assert [i['state'] for i in preview['items']] == ['ready', 'waiting_for_final']
    request = dict(enrollmentId=str(uuid4()), expectedRevision=0, jobId=None, expectedJobVersion=None, source=source,
        schedule=dict(requestId=str(uuid4()), name='Synthetic daily billing scan', operation='lifecycle_action_v1',
            dueAt=(now-timedelta(seconds=1)).isoformat(), expiresAt=(now+timedelta(hours=1)).isoformat(),
            intervalSeconds=None, timeZone='UTC', misfire='run_once', overlap='forbid', timeoutSeconds=30,
            maximumAttempts=2, profileId=None, profileVersion=None))
    accepted = c.call('gas/billing/enroll', request)
    assert c.call('gas/billing/enroll', request) == accepted
    job = accepted['job']['id']
    assert query(f"SELECT count(*) FROM automation.gas_billing_enrollments WHERE job_id='{job}';") == '1'
    host = query('SHOW unix_socket_directories;')
    def claim_next():
        raw = query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{uuid4()}',120)::text;")
        return json.loads(raw) if raw else None
    def work(claim):
        path = Path(host)/'gas-enrollment-worker.json'
        path.write_text(json.dumps(dict(manifest=manifest, host=host, claim=claim))); path.chmod(0o600)
        checked = subprocess.run([str(dotnet), 'run', '--no-build', '--no-restore', '--project', 'tests/OpenEtrm.Api.Checks', '--',
            '--lifecycle-schedule-run', str(path)], cwd=Path(__file__).resolve().parents[1], capture_output=True, text=True, timeout=60)
        if checked.returncode:
            raise RuntimeError(checked.stdout+checked.stderr)
    def drain():
        for _ in range(20):
            claim = claim_next()
            if claim is not None:
                work(claim)
            elif query(f"SELECT state FROM automation.jobs WHERE id='{job}';") == 'completed':
                break
            else:
                time.sleep(1.05)
        else:
            raise AssertionError('The bounded one-off scan did not finish')
    with ThreadPoolExecutor(max_workers=2) as workers:
        claims = list(workers.map(lambda _: claim_next(), range(2)))
    assert sum(claim is not None for claim in claims) == 1, claims
    claim = next(claim for claim in claims if claim is not None)
    assert claim and claim['jobId'] == job
    path = Path(host)/'gas-enrollment-native.json'
    generic = {**request['schedule'], 'requestId': str(uuid4()), 'operation': 'worker_check'}
    quantity_request = dict(context=frame, tradeId=trade, expectedRevision=1, operationId=str(uuid4()),
        obligationId=obligation, day='2021-01-01', quantity='91', kind='allocated', status='final',
        expectedPreviousId=first['revisionId'], evidence='Synthetic scoped write refusal')
    path.write_text(json.dumps(dict(manifest=manifest, host=host, claim=claim, genericJob=generic, enrollment=request,
        quantityRequest=quantity_request, quantityAt='2021-01-15T08:00:00Z'))); path.chmod(0o600)
    counts = "SELECT (SELECT count(*) FROM automation.jobs)||':'||(SELECT count(*) FROM automation.gas_billing_assessments)||':'||(SELECT count(*) FROM automation.gas_billing_scan_results)||':'||(SELECT count(*) FROM trading.gas_quantity_revisions);"
    before = query(counts)
    checked = subprocess.run([str(dotnet), 'run', '--no-build', '--no-restore', '--project', 'tests/OpenEtrm.Api.Checks', '--',
        '--gas-billing-enrollment-native', str(path)], cwd=Path(__file__).resolve().parents[1], capture_output=True, text=True, timeout=60)
    if checked.returncode: raise RuntimeError(checked.stdout+checked.stderr)
    print(checked.stdout.strip(), flush=True)
    assert query(counts) == before
    # Inputs published after first admission must remain outside every page
    # and child in this already-started scan.
    quantity('2021-01-02', 'scheduled', '100')
    quantity('2021-01-02', 'allocated', '100')
    work(claim)
    drain()
    report = c.call('jobs/lifecycle/'+job)
    assert report['scope']['executed'] == 2 and report['scope']['childJobs'] == 2, report
    assert report['scope']['counts']['waiting_for_final'] == 2, report
    assert query(f"SELECT count(*) FROM trading.gas_cash_determinations WHERE trade_id='{trade}';") == '2'
    assert query(f"SELECT count(DISTINCT ROW(r.input_catalog_id,r.input_business_on,r.input_known_at,r.input_system_id,r.input_timeline)) FROM automation.runs r WHERE r.job_id='{job}' OR r.job_id IN(SELECT child.job_id FROM automation.gas_billing_children child JOIN automation.gas_billing_enrollments e ON e.profile_id=child.profile_id WHERE e.job_id='{job}');") == '1'
    assert query(f"SELECT count(*) FROM automation.gas_billing_assessments a JOIN automation.gas_billing_enrollments e ON e.profile_id=a.profile_id WHERE e.job_id='{job}';") == '4'
    child_job = query(f"SELECT child.job_id FROM automation.gas_billing_children child JOIN automation.gas_billing_enrollments e ON e.profile_id=child.profile_id WHERE e.job_id='{job}' ORDER BY child.job_id LIMIT 1;")
    c.call('jobs/control', dict(requestId=str(uuid4()), id=child_job, action='run_again',
        expectedVersion=int(query(f"SELECT version FROM automation.jobs WHERE id='{child_job}';"))))
    repeated = claim_next(); assert repeated and repeated['jobId'] == child_job
    work(repeated)
    report = c.call('jobs/lifecycle/'+job)
    assert report['scope']['executed'] == 2, 'An acknowledged replay was counted as another bill: '+str(report['scope'])
    assert query(f"SELECT count(*) FROM trading.gas_cash_determinations WHERE trade_id='{trade}';") == '2'
    # A later correction and an old previously missing day are revisited in the
    # next complete cycle. The correction remains a review item.
    quantity('2021-01-01', 'allocated', '90', first['revisionId'])
    quantity('2021-01-02', 'scheduled', '100')
    quantity('2021-01-02', 'allocated', '100')
    version = int(query(f"SELECT version FROM automation.jobs WHERE id='{job}';"))
    c.call('jobs/control', dict(requestId=str(uuid4()), id=job, expectedVersion=version, action='run_again'))
    drain()
    report = c.call('jobs/lifecycle/'+job)
    assert report['scope']['executed'] == 4 and report['scope']['childJobs'] == 4, report
    assert report['scope']['counts']['needs_correction'] == 2, report
    assert query(f"SELECT count(*) FROM trading.gas_cash_determinations WHERE trade_id='{trade}';") == '4'
    print('PASS gas scope: complete bounded one-off scan, exact child lineage, retained first cut, late old days and reviewed-only corrections', flush=True)

    # Reactivating a completed enrollment shares the ordinary owner quota,
    # including paused and blocked jobs. All rows belong to this disposable scope.
    review_policy = str(uuid4())
    c.call('lifecycle/policies/save', dict(operationId=str(uuid4()), policyId=review_policy, expectedRevision=0,
        action='gas.bill_daily', mode='review', fromAt='2020-01-01T00:00:00Z', untilAt='2030-01-01T00:00:00Z',
        implementation='explicit_lifecycle_action_policy_v1'))
    other = c.call('gas/composition/book', {**deepcopy(body), 'operationId': str(uuid4())})
    other_rows = c.call('gas/quantities/read', dict(context=frame, tradeId=other['tradeId'], selectionPolicy='operational_projection_v1'))
    c.call('gas/quantities/record', dict(context=frame, tradeId=other['tradeId'], expectedRevision=1, operationId=str(uuid4()),
        obligationId=other_rows['rows'][0]['obligationId'], day='2021-01-01', kind='allocated', status='final', quantity='80',
        expectedPreviousId=None, evidence='Synthetic unmatched final requiring review'))
    generic_jobs = []
    for i in range(64):
        future = datetime.now(timezone.utc)+timedelta(days=1)
        generic_jobs.append(c.call('jobs/create', {**generic, 'requestId': str(uuid4()), 'name': 'Synthetic quota '+str(i),
            'dueAt': future.isoformat(), 'expiresAt': (future+timedelta(hours=1)).isoformat()}))
    # Preparing paused/blocked fixtures does not grant them additional quota.
    query("UPDATE automation.jobs SET state='paused' WHERE id='"+generic_jobs[0]['id']+"';")
    query("UPDATE automation.jobs SET state='blocked' WHERE id='"+generic_jobs[1]['id']+"';")
    def update_source(revision, selected_source):
        return dict(enrollmentId=request['enrollmentId'], expectedRevision=revision, jobId=job,
            expectedJobVersion=int(query(f"SELECT version FROM automation.jobs WHERE id='{job}';")), source=selected_source,
            schedule={**request['schedule'], 'requestId': str(uuid4()), 'dueAt': (datetime.now(timezone.utc)-timedelta(seconds=1)).isoformat(), 'intervalSeconds': 60})
    reviewed_source = {**source, 'policyId': review_policy, 'mode': 'review'}
    update = update_source(1, reviewed_source)
    assert c.call('gas/billing/enroll', update, expected=422)['code'] == 'job_limit'
    def cancel_generic(index):
        task = generic_jobs[index]
        c.call('jobs/control', dict(requestId=str(uuid4()), id=task['id'],
            expectedVersion=int(query(f"SELECT version FROM automation.jobs WHERE id='{task['id']}';")), action='cancel'))
    cancel_generic(63)
    active = c.call('gas/billing/enroll', update)['job']
    assert active['enrollmentRevision'] == 2
    # Expedite only the fixture's wakeups. Every continuation still has to use
    # the original scan's business time and first-admitted input coordinates.
    def wake():
        query(f"UPDATE automation.jobs SET next_due_at=clock_timestamp()-interval '1 second' WHERE id='{job}' AND state='active';")
    for _ in range(12):
        claim = claim_next()
        if claim is None: wake(); continue
        assert claim['jobId'] == job
        work(claim)
        if query(f"SELECT count(*) FROM automation.gas_billing_scan_results s JOIN automation.runs r ON r.id=s.run_id WHERE r.job_id='{job}' AND s.backpressure;") != '0': break
    else: raise AssertionError('A full owner queue did not retain backpressure')
    assert query(f"SELECT count(*) FROM automation.gas_billing_children WHERE profile_id=(SELECT lifecycle_profile_id FROM automation.jobs WHERE id='{job}');") == '0'
    cancel_generic(62)
    wake()
    # With one free slot the next retained page queues one review, keeps the
    # other candidate pending, and never records a financial determination.
    for _ in range(12):
        claim = claim_next()
        if claim is None: wake(); continue
        work(claim)
        count = int(query(f"SELECT count(*) FROM automation.gas_billing_children WHERE profile_id=(SELECT lifecycle_profile_id FROM automation.jobs WHERE id='{job}');"))
        if count: break
    else: raise AssertionError('Releasing capacity did not dispatch the retained candidate')
    before_cash = query(f"SELECT count(*) FROM trading.gas_cash_determinations WHERE trade_id='{other['tradeId']}';")
    assert before_cash == '0'
    child = claim_next(); assert child and child['jobId'] != job
    work(child)
    proposal = c.call('jobs/lifecycle/'+child['jobId'])
    assert proposal['results'][0]['mode'] == 'review' and proposal['results'][0]['proposal']
    assert query(f"SELECT count(*) FROM trading.gas_cash_determinations WHERE trade_id='{other['tradeId']}';") == '0'
    wake(); parent = claim_next(); assert parent and parent['jobId'] == job; work(parent)
    queued = query(f"SELECT count(*) FROM automation.gas_billing_children c JOIN automation.jobs j ON j.id=c.job_id WHERE c.profile_id=(SELECT lifecycle_profile_id FROM automation.jobs WHERE id='{job}') AND j.state='active';")
    assert queued == '1'
    c.call('jobs/control', dict(requestId=str(uuid4()), id=job, action='pause',
        expectedVersion=int(query(f"SELECT version FROM automation.jobs WHERE id='{job}';"))))
    old_profile = query(f"SELECT lifecycle_profile_id FROM automation.jobs WHERE id='{job}';")
    changed = update_source(2, {**reviewed_source, 'from': '2021-01-02'})
    amended = c.call('gas/billing/enroll', changed)['job']; assert amended['enrollmentRevision'] == 3
    assert query(f"SELECT count(*) FROM automation.gas_billing_children c JOIN automation.jobs j ON j.id=c.job_id WHERE c.profile_id='{old_profile}' AND j.state='active';") == '0'
    for _ in range(12):
        claim = claim_next()
        if claim is None: wake(); continue
        assert claim['jobId'] == job; work(claim)
        report = c.call('jobs/lifecycle/'+job)
        if report['scope']['completedSourceRunId'] and not report['scope']['scanSourceRunId']: break
    else: raise AssertionError('The updated bounded recurring scan did not finish')
    assert query(f"SELECT mod(extract(epoch FROM(j.next_due_at-root.due_at)),60)=0 FROM automation.jobs j JOIN automation.gas_billing_scan_progress p ON p.profile_id=j.lifecycle_profile_id JOIN automation.runs root ON root.id=p.completed_source_run_id WHERE j.id='{job}';") == 't'
    c.call('jobs/control', dict(requestId=str(uuid4()), id=job, action='pause',
        expectedVersion=int(query(f"SELECT version FROM automation.jobs WHERE id='{job}';"))))
    assert claim_next() is None
    for index in range(62): cancel_generic(index)
    print('PASS gas enrollment controls: paused/blocked owner quota, retained backpressure, review-only child, versioned scope cancellation and original recurring phase', flush=True)
    return dict(bookId=book, policyId=review_policy, jobId=job)
