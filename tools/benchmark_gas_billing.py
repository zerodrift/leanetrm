"""Measured synthetic scope discovery and a separate, real child-execution sample."""
from collections import Counter
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from decimal import Decimal
import json
import os
from pathlib import Path
import platform
import statistics
import subprocess
import time
from uuid import uuid4

from check_booked_side_programs import runtime_query
from native_read_transport import select_context_inputs
from preview_client import PreviewClient, initial_frame


def measurement_client(url):
    # Fixture preparation may use most of the preview's 30-minute login. Each
    # measured phase gets ordinary fresh authority before selecting its inputs.
    client = PreviewClient(url); client.bootstrap(); client.call('profile', {'profile': 'trader'})
    return client


def measure_claim(query, manifest, dotnet):
    host = Path(query('SHOW unix_socket_directories;'))
    assert (host/'.openetrm-preview').is_file()
    claim = json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{uuid4()}',120)::text;"))
    assert claim is not None
    packet = host/'gas-billing-benchmark-worker.json'
    packet.write_text(json.dumps(dict(manifest=manifest, host=str(host), claim=claim, measure=True))); packet.chmod(0o600)
    began = time.perf_counter()
    completed = subprocess.run([str(dotnet), 'run', '--no-build', '--no-restore', '--project', 'tests/OpenEtrm.Api.Checks', '--',
        '--lifecycle-schedule-run', str(packet)], cwd=Path(__file__).resolve().parents[1], text=True, capture_output=True, timeout=60)
    if completed.returncode:
        raise RuntimeError(completed.stdout+completed.stderr)
    elapsed = (time.perf_counter()-began)*1000
    processing = json.loads(next(line.split('=', 1)[1] for line in completed.stdout.splitlines() if line.startswith('LIFECYCLE_MEASUREMENT=')))
    if processing['peakProcessBytes'] <= 0:
        processing['peakProcessBytes'] = None  # This platform does not expose the counter.
    return claim, elapsed, processing


def measure_discovery_page(c, query, manifest, source, *, dotnet):
    """Execute one maximum-sized page, then cancel its unexecuted sample jobs."""
    assert source['pageLimit'] == 8 and source['dispatchLimit'] == 4
    now = datetime.now(timezone.utc)
    accepted = c.call('gas/billing/enroll', dict(enrollmentId=str(uuid4()), expectedRevision=0, jobId=None, expectedJobVersion=None,
        source=source, schedule=dict(requestId=str(uuid4()), name='Synthetic maximum-page measurement', operation='lifecycle_action_v1',
            dueAt=(now-timedelta(seconds=1)).isoformat(), expiresAt=(now+timedelta(hours=1)).isoformat(), intervalSeconds=None,
            timeZone='UTC', misfire='run_once', overlap='forbid', timeoutSeconds=30, maximumAttempts=2, profileId=None, profileVersion=None)))
    job = accepted['job']['id']
    claim, elapsed, processing = measure_claim(query, manifest, dotnet)
    assert claim['jobId'] == job
    report = c.call('jobs/lifecycle/'+job)
    assert sum(report['scope']['counts'].values()) == 8 and report['scope']['childJobs'] == 4 and report['scope']['executed'] == 0
    assert report['scope']['scanSourceRunId'] and not report['scope']['completedSourceRunId']
    children = json.loads(query(f"SELECT jsonb_agg(job_id) FROM automation.gas_billing_children WHERE profile_id=(SELECT lifecycle_profile_id FROM automation.jobs WHERE id='{job}');"))
    for identifier in [job, *children]:
        c.call('jobs/control', dict(requestId=str(uuid4()), id=identifier, action='cancel',
            expectedVersion=int(query(f"SELECT version FROM automation.jobs WHERE id='{identifier}';"))))
    return dict(assessedOccurrences=8, dispatchedChildren=4, executedBills=0, processMs=round(elapsed, 2),
                managedProcessing=processing, scopeScanComplete=False)


def run(c, query, manifest, terms, *, dotnet, api_pid):
    catalog = manifest['catalogId']
    owned = Path(query('SHOW unix_socket_directories;'))
    if not (owned/'.openetrm-preview').is_file():
        raise ValueError('Use the owned disposable benchmark database.')
    query("ALTER SYSTEM SET track_functions='all';")
    query('SELECT pg_reload_conf();')
    frame = {**{k: terms[k] for k in ('convention', 'knowledgeMode')},
             'snapshotId': initial_frame(c.call('workspace'), '2021-01-31')['id']}
    now = datetime.now(timezone.utc)
    operating = dict(convention='gas_processing_instant_utc_v1', businessDate=now.date().isoformat(),
                     evaluationAt=now.isoformat(), knowledgeMode='current', retained=None)
    results = []

    def native_time():
        return json.loads(query("SELECT coalesce(jsonb_object_agg(funcname,jsonb_build_object('calls',calls,'totalMs',total_time,'selfMs',self_time)),'{}') FROM pg_stat_user_functions WHERE schemaname='security' AND funcname IN('read_gas_billing_page','read_gas_billing_components','read_gas_billing_inputs');"))

    def measure_scan(book, days):
        reader = measurement_client(c.url)
        body = dict(context=operating, bookIds=[book], **{'from': '2021-01-01',
            'to': (datetime(2021, 1, 1)+timedelta(days=days)).date().isoformat()},
            selectionPolicy='final_allocated_billing_v1', profile='gas_final_daily_entry_cash_v1', after=None, limit=8)
        samples, targets, states = [], set(), Counter()
        max_bytes = rss = 0
        native_before = native_time()
        start = time.perf_counter()
        while True:
            began = time.perf_counter()
            page = reader.call('gas/billing/worklist', body, timeout=35)
            samples.append((time.perf_counter()-began)*1000)
            max_bytes = max(max_bytes, len(json.dumps(page).encode()))
            rss = max(rss, int(subprocess.check_output(['ps', '-o', 'rss=', '-p', str(api_pid)], text=True).strip()))
            for row in page['items']:
                key = (row['selection']['tradeId'], row['selection']['cashSlot'], row['selection']['day'])
                assert key not in targets
                targets.add(key)
                states[row['state']] += 1
            if not page['hasMore']:
                break
            assert page['next'] is not None and page['next'] != body['after']
            body.update(after=page['next'], context={**operating, 'knowledgeMode': 'retained', 'retained': page['context']})
            if len(samples) % 100 == 0:
                print(f'BENCHMARK gas discovery: {len(targets)} assessed occurrences', flush=True)
        elapsed_ms = (time.perf_counter()-start)*1000
        time.sleep(1.1)  # Allow stock PostgreSQL's function statistics to flush.
        native_after = native_time()
        functions = {name: {key: round(value-native_before.get(name, {}).get(key, 0), 2) for key, value in values.items()}
                     for name, values in native_after.items()}
        for values in functions.values():
            if values['calls'] == 0:
                values.update(totalMs=None, selfMs=None)
        return dict(occurrences=len(targets), pages=len(samples), elapsedMs=round(elapsed_ms, 2),
                    firstPageMs=round(samples[0], 2), medianPageMs=round(statistics.median(samples), 2),
                    maximumPageMs=round(max(samples), 2), states=dict(states), maximumResponseBytes=max_bytes,
                    observedApiRssKiB=rss, databaseFunctionTimes=functions,
                    databaseStatistics='Asynchronously published PostgreSQL counters may lag the last request; nested function times are not total database wall time.')

    scopes = []
    for count, days in ((1, 2), (120, 31)):
        book = str(uuid4())
        query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic billing workload {count}',publication_order,effective_at FROM trading.books WHERE(catalog_id,id)=('{catalog}','{terms['bookId']}');")
        start = time.perf_counter()
        trades, bindings, quantity_count, late = [], set(), 0, None
        for index in range(count):
            body = {**deepcopy(terms), 'operationId': str(uuid4()), 'bookId': book}
            body['physicalSides'][0]['deliveryTo'] = (datetime(2021, 1, 1)+timedelta(days=days)).date().isoformat()
            body['cashSides'][0]['price'] = str(Decimal('3')+Decimal(index)/1000)
            saved = c.call('gas/composition/book', body)
            trade = saved['tradeId']; trades.append(trade)
            bindings.add(query(f"SELECT instrument_id FROM trading.gas_trade_revisions WHERE trade_id='{trade}' AND revision=1;"))
            grid = c.call('gas/quantities/read', dict(context=frame, tradeId=trade, selectionPolicy='operational_projection_v1'))
            obligation = grid['rows'][0]['obligationId']
            calls = []
            for day in range(1, days+1):
                for kind in ('scheduled', 'allocated'):
                    if kind == 'allocated' and days == 31 and (day == 31 or day == 15 and index % 4 == 0):
                        continue
                    request = dict(context=frame, tradeId=trade, expectedRevision=1, operationId=str(uuid4()),
                        obligationId=obligation, day=f'2021-01-{day:02}', kind=kind,
                        status='final' if kind == 'allocated' else 'provisional', quantity='100',
                        expectedPreviousId=None, evidence='Synthetic billing workload')
                    encoded = json.dumps(request).replace("'", "''")
                    calls.append(f"PERFORM security.record_gas_daily_quantity('{catalog}','{encoded}'::jsonb,'2021-01-31T08:00:00Z','{grid['context']['contextId']}');")
                    quantity_count += 1
                    if index == 0 and day == 1 and kind == 'allocated':
                        late = request
            runtime_query(query, manifest, select_context_inputs(catalog, grid['context'])+'DO $seed$ BEGIN '+''.join(calls)+' END $seed$;')
            if (index+1) % 20 == 0:
                print(f'BENCHMARK gas setup: {index+1} distinct contracts', flush=True)
        setup_ms = (time.perf_counter()-start)*1000
        assert len(bindings) == count
        discovery = measure_scan(book, days)
        assert discovery['occurrences'] == count*days*2
        assert discovery['states'] == ({'ready': 4} if days == 2 else {'ready': 7140, 'waiting_for_final': 300})
        results.append(dict(uniqueContracts=count, uniqueBindings=len(bindings), gasDays=days, paymentsPerContract=2,
            quantityFacts=quantity_count, scenarios=0, setupMs=round(setup_ms, 2), discovery=discovery))
        scopes.append((book, trades, late))

    # The large read does not enqueue 7,440 children. Execution is a separately
    # measured four-occurrence sample through the same durable scheduler path.
    c = measurement_client(c.url)
    book, trades, _ = scopes[0]
    policy = str(uuid4())
    c.call('lifecycle/policies/save', dict(operationId=str(uuid4()), policyId=policy, expectedRevision=0,
        action='gas.bill_daily', mode='automatic', fromAt='2020-01-01T00:00:00Z', untilAt='2030-01-01T00:00:00Z',
        implementation='explicit_lifecycle_action_policy_v1'))
    source = dict(bookIds=[book], **{'from': '2021-01-01', 'to': '2021-01-03'},
        discoveryPolicy='gas_billing_scope_reassessment_v1', selectionPolicy='final_allocated_billing_v1',
        billingProfile='gas_final_daily_entry_cash_v1', roundingPolicy='currency_minor_units_to_even_v1',
        scheduledPolicy='manual_scheduled_quantity_v1', processingConvention=operating['convention'],
        knowledgePolicy='current_knowledge_at_execution_v1', policyId=policy, policyRevision=1, mode='automatic',
        readinessDelaySeconds=0, pageLimit=8, dispatchLimit=4, childTimeoutSeconds=30, childMaximumAttempts=2)
    now = datetime.now(timezone.utc)
    accepted = c.call('gas/billing/enroll', dict(enrollmentId=str(uuid4()), expectedRevision=0, jobId=None, expectedJobVersion=None,
        source=source, schedule=dict(requestId=str(uuid4()), name='Synthetic measured billing execution', operation='lifecycle_action_v1',
            dueAt=(now-timedelta(seconds=1)).isoformat(), expiresAt=(now+timedelta(hours=1)).isoformat(), intervalSeconds=None,
            timeZone='UTC', misfire='run_once', overlap='forbid', timeoutSeconds=30, maximumAttempts=2, profileId=None, profileVersion=None)))
    samples, processing = [], []; start = time.perf_counter()
    for _ in range(5):
        _, elapsed, measured = measure_claim(query, manifest, dotnet)
        samples.append(elapsed); processing.append(measured)
    report = c.call('jobs/lifecycle/'+accepted['job']['id'])
    assert report['scope']['executed'] == 4 and report['scope']['childJobs'] == 4
    execution = dict(uniqueContracts=1, occurrences=4, sourceFacts=4, discoveryRuns=1, financialRuns=4,
        elapsedMs=round((time.perf_counter()-start)*1000, 2), discoveryProcessMs=round(samples[0], 2),
        childProcessMedianMs=round(statistics.median(samples[1:]), 2), childProcessMaximumMs=round(max(samples[1:]), 2),
        managedProcessing=processing,
        processOverhead='Each sample starts the managed check executable and includes CLI startup; no resident-worker throughput claim.')
    # Reassess a late old-day revision independently after the full retained scan.
    book, trades, late = scopes[1]
    maximum_page = measure_discovery_page(c, query, manifest, {**source, 'bookIds': [book], 'to': '2021-02-01'}, dotnet=dotnet)
    old = query(f"SELECT r.id FROM trading.gas_quantity_revisions r JOIN trading.gas_quantity_series s ON(s.catalog_id,s.id)=(r.catalog_id,r.series_id) WHERE s.obligation_id='{late['obligationId']}' AND s.kind='allocated' AND r.day='2021-01-01' ORDER BY r.revision DESC LIMIT 1;")
    late_frame = {**frame, 'snapshotId': initial_frame(c.call('workspace'), '2021-01-31')['id']}
    c.call('gas/quantities/record', {**late, 'context': late_frame, 'operationId': str(uuid4()), 'quantity': '98', 'expectedPreviousId': old})
    begin = time.perf_counter()
    changed = c.call('gas/billing/operating-preview', dict(context=operating, selection=dict(tradeId=trades[0], expectedRevision=1,
        cashSlot=1, day='2021-01-01', selectionPolicy='final_allocated_billing_v1', profile='gas_final_daily_entry_cash_v1',
        decimalPlaces=2, roundingPolicy='currency_minor_units_to_even_v1')))
    assert changed['amount'] == '-294' and changed['reconciliation']['status'] == 'difference'
    return dict(workload='gas_billing_scope_v1', environment=dict(os=platform.platform(), architecture=platform.machine(),
        logicalCpus=os.cpu_count(), cpu=subprocess.check_output(['sysctl','-n','machdep.cpu.brand_string'],text=True).strip() if platform.system()=='Darwin' else platform.processor(),
        sdk='10.0.402', database='stock PostgreSQL '+query('SHOW server_version;')),
        cacheState='API and database warmed by authorized fixture setup; no cold-process or quantity-result-cache claim.',
        discovery=results, execution=execution, maximumPageExecution=maximum_page,
        lateOldDayReassessmentMs=round((time.perf_counter()-begin)*1000, 2))
