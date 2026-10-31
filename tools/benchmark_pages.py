#!/usr/bin/env python3
"""Measure page readiness using only a newly owned synthetic database and browser."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shlex
import shutil
import socket
import subprocess
import tempfile
import time
from urllib.request import urlopen
from urllib.parse import urlencode
from uuid import uuid4

from owned_process import stop_owned_group
from preview_client import PreviewClient, initial_frame
from check_risk_jobs_db import provision_worker
from preview_fixture import fixture, trading_fixture

ROOT = Path(__file__).resolve().parents[1]


def summary(values):
    ordered = sorted(values)
    return {'samplesMs': values, 'p50Ms': ordered[len(ordered)//2],
            'p95Ms': ordered[min(len(ordered)-1, int(len(ordered)*.95))], 'maximumMs': max(values)}


def artifact_hash(directory):
    digest = hashlib.sha256()
    for path in sorted(directory.rglob('*')):
        if path.is_file():
            digest.update(str(path.relative_to(directory)).encode()+b'\0')
            digest.update(hashlib.sha256(path.read_bytes()).digest())
    return digest.hexdigest()


def seed_dashboard(url, query, run_worker):
    """The risk persona's saved dashboard: one Latest levels widget of up to twelve curves, one Price chart and one Positions widget whose
    definition shocks every held contract, with one completed run at the newest close."""
    risk = PreviewClient(url)
    risk.bootstrap()
    risk.call('profile', {'profile': 'risk'})
    frame = initial_frame(risk.call('workspace'), '2020-12-18')
    current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    catalog = risk.call('portfolio/risk/catalog', {'context': current, 'bookIds': [], 'portfolioIds': []})
    retained = {**current, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']}
    books = sorted(b['bookId'] for b in catalog['books'])
    held = risk.call('portfolio/risk/catalog', {'context': retained, 'bookIds': books, 'portfolioIds': []})
    targets = {t['id']: t for t in held['scenarioTargets']}
    units = {json.dumps(targets[t]['priceUnit'], sort_keys=True): targets[t]['priceUnit'] for t in held['heldContractTargets']}
    definition = risk.call('risk/definitions', dict(requestId=str(uuid4()), definitionId=None, expectedVersion=None, name='Synthetic benchmark delta',
                                                    bookIds=books, preset='held_contracts', sizes=[dict(unit=unit, size='0.5') for unit in units.values()]))
    queued = risk.call(f"risk/definitions/{definition['definitionId']}/run", dict(requestId=str(uuid4()), version=definition['version'], context=retained))
    for _ in range(len(queued['jobs'])+5):
        if query("SELECT count(*) FROM risk.jobs WHERE state IN('queued','running')") == '0':
            break
        run_worker()
    if query(f"SELECT count(*) FROM risk.definition_run_jobs p JOIN risk.jobs j ON j.id=p.job_id WHERE p.run_id='{queued['runId']}' AND j.state='completed'") != str(len(queued['jobs'])):
        raise RuntimeError('The benchmark definition run did not complete.')
    curves = [c['code'] for c in risk.call('workspace/navigation')['curves'] if c['marketType'] != 'rates'][:12]
    chart = risk.call('market/curve-shapes', {'snapshotId': frame['id'], 'curves': ['local_power']})['curves'][0]
    def widget(kind, config, y, h):
        return dict(id=str(uuid4()), kind=kind, config=config, board=dict(x=0, y=y, w=12, h=h), morning=dict(section='prices_positions', order=y))
    widgets = [widget('latest_levels', {'curves': curves}, 0, 4), widget('price_chart', {'curve': 'local_power', 'contractId': chart['front']['contractId']}, 4, 6),
               widget('positions', {'definitionId': definition['definitionId']}, 10, 6)]
    risk.call('dashboard/layout', dict(requestId=str(uuid4()), expectedVersion=None, layout=dict(version=1, arrangement='board', widgets=widgets)))
    return {'widgets': {w['kind']: 1 for w in widgets}, 'levelCurves': len(curves), 'definitions': 1, 'definitionBooks': len(books),
            'definitionPriceUnits': len(units), 'heldContractTargets': len(held['heldContractTargets']), 'completedRuns': 1, 'runParts': len(queued['jobs'])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    parser.add_argument('--dotnet', type=Path, required=True)
    parser.add_argument('--browser', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='Private report path outside the repository')
    parser.add_argument('--additional-observations', type=int, default=12000)
    parser.add_argument('--additional-reference-locations', type=int, default=4000)
    parser.add_argument('--additional-futures-series', type=int, default=2000)
    parser.add_argument('--samples', type=int, default=5)
    parser.add_argument('--workspace-endpoint', choices=['workspace', 'workspace/catalog'], default='workspace/catalog')
    parser.add_argument('--latency-ms', type=float, default=40, help='Explicit browser request latency; 0 for loopback')
    parser.add_argument('--budget-ms', type=float, default=1000)
    parser.add_argument('--enforce-budget', action='store_true', help='Fail this benchmark when any page p95 exceeds its budget')
    args = parser.parse_args()
    if not 1 <= args.samples <= 20 or not 0 <= args.additional_observations <= 50000 or not 0 <= args.additional_reference_locations <= 20000 or not 0 <= args.additional_futures_series <= 10000 or not 0 <= args.latency_ms <= 1000 or args.budget_ms <= 0:
        parser.error('Choose 1–20 samples, 0–50000 additional observations, 0–20000 additional reference locations, 0–10000 additional futures series and explicit nonnegative latency/positive budget.')
    if args.output.resolve().is_relative_to(ROOT):
        parser.error('Keep generated benchmark reports outside the repository.')
    bins = {n: args.pg_bin.resolve()/n for n in ('initdb', 'pg_ctl', 'psql')}
    for binary in [args.dotnet, args.browser, *bins.values()]:
        if not binary.is_file():
            parser.error(f'Missing installed binary: {binary}')
    dll = ROOT/'src/OpenEtrm.Api/bin/Debug/net10.0/OpenEtrm.Api.dll'
    if not dll.is_file() or not (ROOT/'web/dist/index.html').is_file():
        parser.error('Build the API and web assets before this benchmark.')
    env = {k: v for k, v in os.environ.items() if not k.startswith(('PG', 'OPENETRM_', 'ASPNETCORE_', 'DOTNET_'))}
    env.update(PGTZ='UTC', DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
    temporary = Path(tempfile.mkdtemp(prefix='openetrm-page-bench-', dir='/tmp')).resolve()
    os.chmod(temporary, 0o700)
    data = temporary/'data'
    run_id = str(uuid4())
    (temporary/'.openetrm-preview').write_text(run_id)
    api = browser = None
    attempted = stopped = False
    report = None
    log = None

    def run(command, **kwargs):
        return subprocess.run([str(v) for v in command], cwd=ROOT, env=env, text=True,
                              capture_output=True, timeout=120, check=True, **kwargs)

    def query(sql):
        return run([bins['psql'], '-X', '-h', temporary, '-p', '55439', '-U', 'preview_owner',
                    '-d', 'postgres', '-qAt', '-v', 'ON_ERROR_STOP=1'], input=sql).stdout.strip()

    try:
        run([bins['initdb'], '-D', data, '--auth-local=trust', '--auth-host=reject', '--encoding=UTF8', '--locale=C', '--no-instructions', '--username=preview_owner'])
        attempted = True
        run([bins['pg_ctl'], '-D', data, '-w', '-t', '15', '-l', temporary/'postgres.log', '-o',
             shlex.join(['-k', str(temporary), '-h', '', '-p', '55439', '-c', 'track_commit_timestamp=on']), 'start'])
        if Path(query('SHOW data_directory')).resolve() != data:
            raise RuntimeError('Owned database directory does not match; refusing to seed.')
        migrations = sorted((ROOT/'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql'))
        applied_migrations = []
        for migration in migrations:
            content = migration.read_bytes()
            query(content.decode())
            applied_migrations.append({'name': migration.name, 'sha256': hashlib.sha256(content).hexdigest()})
        reference, market, security, manifest = fixture(ROOT)
        # The reference inspector consumes this larger pinned catalog. Locations
        # add no pricing exposure and never enter the selected contract bindings.
        extra_locations = f"""INSERT INTO ref_data.locations
          SELECT (jsonb_populate_record(NULL::ref_data.locations,to_jsonb(t)||jsonb_build_object(
            'code','benchmark_location_'||n,'name','Synthetic benchmark location '||n))).*
          FROM ref_data.locations t CROSS JOIN generate_series(1,{args.additional_reference_locations})n
          WHERE t.catalog_id='{manifest['referenceCatalogId']}' AND t.revision_id='{manifest['referenceRevisionId']}'
            AND t.code='test_hub_a';\n"""
        extra_futures = f"""INSERT INTO ref_data.futures_series
          SELECT (jsonb_populate_record(NULL::ref_data.futures_series,to_jsonb(t)||jsonb_build_object(
            'code','benchmark_series_'||n,'series_label','Synthetic benchmark series '||n))).*
          FROM ref_data.futures_series t CROSS JOIN generate_series(1,{args.additional_futures_series})n
          WHERE t.catalog_id='{manifest['referenceCatalogId']}' AND t.revision_id='{manifest['referenceRevisionId']}'
            AND t.code='test_gas_202102';
          INSERT INTO ref_data.futures_milestones
          SELECT (jsonb_populate_record(NULL::ref_data.futures_milestones,to_jsonb(t)||jsonb_build_object(
            'series_code','benchmark_series_'||n))).*
          FROM ref_data.futures_milestones t CROSS JOIN generate_series(1,{args.additional_futures_series})n
          WHERE t.catalog_id='{manifest['referenceCatalogId']}' AND t.revision_id='{manifest['referenceRevisionId']}'
            AND t.series_code='test_gas_202102';\n"""
        market = market.replace('UPDATE ref_data.revisions SET', extra_locations+extra_futures+'UPDATE ref_data.revisions SET', 1)
        from swap_fixture import fixture as swap_fixture
        swap_market, swap_manifest = swap_fixture()
        for sql in [reference, market, swap_market, security, trading_fixture(manifest['catalogId'], swap_defaults=True)]:
            query(sql)
        manifest['swapMarkets'] = [swap_manifest]
        (temporary/'manifest.json').write_text(json.dumps(manifest))
        shutil.copytree(ROOT/'web/dist', temporary/'web')
        shutil.copytree(dll.parent, temporary/'api')
        source_identity = {
            'commit': run(['git', 'rev-parse', 'HEAD']).stdout.strip(),
            'dirty': bool(run(['git', 'status', '--porcelain']).stdout),
            'trackedDiffSha256': hashlib.sha256(run(['git', 'diff', '--binary', 'HEAD']).stdout.encode()).hexdigest(),
            'untrackedFileSha256': {name: hashlib.sha256((ROOT/name).read_bytes()).hexdigest()
                                    for name in run(['git', 'ls-files', '--others', '--exclude-standard', '-z']).stdout.split('\0') if name},
        }
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        url = f'http://127.0.0.1:{port}'
        app_env = {**env, 'OPENETRM_PREVIEW_ROOT': str(temporary), 'OPENETRM_PREVIEW_PORT': str(port),
                   'OPENETRM_PREVIEW_WEB': str(temporary/'web'),
                   'OPENETRM_PREVIEW_RUNTIME': f'Host={temporary};Port=55439;Database=postgres;Username=preview_runtime;SSL Mode=Disable',
                   'OPENETRM_PREVIEW_AUTHENTICATOR': f'Host={temporary};Port=55439;Database=postgres;Username=preview_authenticator;SSL Mode=Disable'}
        log = (temporary/'api.log').open('w')
        api = subprocess.Popen([str(args.dotnet.resolve()), str(temporary/'api'/dll.name), '--local-preview'],
                               cwd=ROOT, env=app_env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        deadline = time.monotonic()+20
        while True:
            if api.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError('Owned API failed to become ready: '+(temporary/'api.log').read_text())
            try:
                with urlopen(url+'/api/health', timeout=.5) as response:
                    health = json.load(response)
                if health != {'mode': 'local-synthetic-preview', 'runId': run_id}:
                    raise RuntimeError('HTTP target is not the owned synthetic application.')
                break
            except OSError:
                time.sleep(.1)
        from seed_preview_positions import seed_positions
        from seed_preview_swaps import seed_swaps
        seed_positions(url, run_id, temporary)
        seed_swaps(url, run_id, temporary)
        # Delivery prices opens on this week of two sources, so its readiness includes the interval chart and table.
        from delivery_price_seed import seed as seed_delivery_prices
        seed_delivery_prices(query, manifest)
        # The Dashboard opens on a saved layout of every widget kind, its Positions definition already run by an owned risk worker.
        provision_worker(query, manifest)
        worker_connection = temporary/'risk-worker-connection'
        worker_connection.write_text(f'Host={temporary};Port=55439;Database=postgres;Username=preview_risk_worker;SSL Mode=Disable')
        worker_config = temporary/'risk-worker.json'
        worker_config.write_text(json.dumps({'connectionFile': str(worker_connection), 'leaseSeconds': 30, 'pollSeconds': 1, 'timeoutSeconds': 60}))
        worker_env = {**env, 'OPENETRM_RISK_WORKER_CONFIG': str(worker_config)}
        dashboard = seed_dashboard(url, query, lambda: subprocess.run([str(args.dotnet.resolve()), str(temporary/'api'/dll.name), '--risk-worker', 'once'],
                                                                       cwd=ROOT, env=worker_env, check=True, timeout=90, capture_output=True))
        # Independent unused observations exercise workspace scaling without
        # changing any pinned price, contract, correction chain or numerical result.
        query(f"""WITH originals AS MATERIALIZED (
          SELECT o.*,row_number() OVER(ORDER BY id) n,count(*) OVER() total
          FROM market_data.observations o WHERE catalog_id='{manifest['catalogId']}'
          AND definition_revision_id='{manifest['revisionId']}')
        INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
          source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
        SELECT o.catalog_id,md5('synthetic_page_benchmark_'||g.n)::uuid,o.definition_revision_id,o.series_id,
          o.reference_catalog_id,o.reference_revision_id,o.source_code,'benchmark_'||g.n,'1',o.role,o.publication_state,
          o.observed_at,clock_timestamp(),o.value,NULL FROM generate_series(1,{args.additional_observations})g(n)
          JOIN originals o ON o.n=1+(g.n-1)%o.total;
        ANALYZE;""")
        counts = json.loads(query("""SELECT json_build_object(
          'observations',(SELECT count(*) FROM market_data.observations),
          'series',(SELECT count(*) FROM market_data.series),'contracts',(SELECT count(*) FROM market_data.contracts),
          'curves',(SELECT count(*) FROM market_data.curves),'trades',(SELECT count(*) FROM trading.trades),
          'swaps',(SELECT count(DISTINCT trade_id) FROM trading.swap_trade_revisions))"""))
        reference_tables = query("SELECT tablename FROM pg_tables WHERE schemaname='ref_data' ORDER BY tablename").splitlines()
        reference_counts = ' UNION ALL '.join('SELECT count(*) n FROM ref_data."'+name.replace('"', '""')+'"' for name in reference_tables)
        counts['referenceRows'] = int(query('SELECT sum(n) FROM ('+reference_counts+') counts'))
        added_series = int(query(f"SELECT count(*) FROM ref_data.futures_series WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND code LIKE 'benchmark_series_%'"))
        added_locations = int(query(f"SELECT count(*) FROM ref_data.locations WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND code LIKE 'benchmark_location_%'"))
        if (added_series, added_locations) != (args.additional_futures_series, args.additional_reference_locations):
            raise RuntimeError('The explicit synthetic reference workload was not seeded completely.')
        counts['additionalFutureMilestones'] = int(query(f"SELECT count(*) FROM ref_data.futures_milestones WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND series_code LIKE 'benchmark_series_%'"))
        client = PreviewClient(url)
        client.bootstrap()
        client.call('profile', {'profile': 'viewer'})
        inspector = []
        for table in ['locations', 'futures_series']:
            expected = json.loads(query(f"SELECT coalesce(json_agg(code ORDER BY code),'[]'::json) FROM ref_data.{table} WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}'"))
            if len(expected) <= 200:
                continue
            pages = []
            for offset in [0, 200]:
                start = time.perf_counter()
                page = client.call('reference/catalog?' + urlencode({'table': table, 'offset': offset}))
                elapsed = round((time.perf_counter()-start)*1000, 2)
                if ([row['code'] for row in page['rows']] != expected[offset:offset+200]
                        or page['hasMore'] != (len(expected) > offset+200)
                        or page['offset'] != offset or page['pageSize'] != 200):
                    raise RuntimeError('Reference pagination changed the exact selected catalog: '+table)
                pages.append({'offset': offset, 'rows': len(page['rows']), 'readyMs': elapsed})
            found = client.call('reference/catalog?' + urlencode({'table': table, 'search': expected[-1]}))
            if expected[-1] not in {row['code'] for row in found['rows']}:
                raise RuntimeError('Reference search did not inspect records beyond the first page: '+table)
            inspector.append({'table': table, 'catalogRows': len(expected), 'pages': pages,
                              'searchBeyondFirstPageVerified': True})
        timings = []
        workspace = None
        for _ in range(args.samples):
            start = time.perf_counter()
            workspace = client.call(args.workspace_endpoint)
            timings.append(round((time.perf_counter()-start)*1000, 2))
        selected = next(f for f in workspace['frames'] if f['at'].startswith('2020-12-18'))
        evaluation = []
        for _ in range(args.samples):
            start = time.perf_counter()
            result = client.call('evaluate', {'snapshotId': selected['id'], 'curve': 'local_power', 'includeSources': True})
            evaluation.append(round((time.perf_counter()-start)*1000, 2))
            if result['points'][0]['value'] != '29.1':
                raise RuntimeError('Synthetic benchmark changed a pinned numerical result.')
        target = temporary/'browser-target.json'
        target.write_text(json.dumps({'suite': 'business_browser_v1', 'url': url, 'runId': run_id,
                                     'samples': args.samples, 'latencyMs': args.latency_ms, 'budgetMs': args.budget_ms}))
        browser = subprocess.Popen(['node', str(ROOT/'tests/browser/page-performance.mjs'), str(target), str(args.browser.resolve())],
                                   cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, start_new_session=True)
        output, errors = browser.communicate(timeout=300)
        if browser.returncode:
            raise RuntimeError('Owned browser benchmark failed: '+errors+'\n'+output)
        browser_report = json.loads(output)
        report = {'schema': 'synthetic_page_performance_v1', 'budgetMs': args.budget_ms,
                  'environment': {'os': platform.platform(), 'architecture': platform.machine(),
                                  'dotnet': run([args.dotnet, '--version']).stdout.strip(),
                                  'postgres': run([bins['psql'], '--version']).stdout.strip(),
                                  'browser': run([args.browser, '--version']).stdout.strip(),
                                  'source': source_identity,
                                  'databaseMigrations': applied_migrations,
                                  'apiFilesSha256': artifact_hash(temporary/'api'),
                                  'webFilesSha256': artifact_hash(temporary/'web'),
                                  'apiRssKiB': int(run(['ps', '-o', 'rss=', '-p', str(api.pid)]).stdout.strip())},
                  'workload': {**counts, 'additionalUnselectedObservations': args.additional_observations,
                               'additionalReferenceLocations': args.additional_reference_locations,
                               'additionalFuturesSeries': args.additional_futures_series,
                               'uniquePinnedInputs': len({o for f in manifest['frames'] for o in f['observationIds']}),
                               'scenarios': 0, 'apiCurveEvaluatedConstituents': result['evaluated'], 'dashboard': dashboard,
                               'apiDatabaseTransport': 'local Unix socket; stock PostgreSQL; runtime RLS enabled'},
                  'api': {'workspace': {**summary(timings), 'endpoint': args.workspace_endpoint, 'decodedJsonBytes': len(json.dumps(workspace).encode()),
                                       'observationsReturned': len(workspace['observations'])},
                          'evaluate': summary(evaluation), 'referenceInspector': inspector}, 'browser': browser_report,
                  'limits': 'Single client, synthetic standard contracts. Browser latency is explicit. No hosted TLS/proxy, concurrency or production qualification claim.'}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2)+'\n')
        print(json.dumps({'report': str(args.output.resolve()), 'workspaceP95Ms': report['api']['workspace']['p95Ms'],
                          'pageBudgetPassed': browser_report['budgetPassed']}), flush=True)
    finally:
        cleanup_ok = True
        for process in [browser, api]:
            if process is not None:
                try:
                    stop_owned_group(process)
                except Exception:
                    cleanup_ok = False
        if log is not None:
            log.close()
        if attempted:
            status = subprocess.run([str(bins['pg_ctl']), '-D', str(data), 'status'], env=env, capture_output=True)
            if status.returncode == 0:
                subprocess.run([str(bins['pg_ctl']), '-D', str(data), '-m', 'immediate', '-w', '-t', '15', 'stop'], env=env, capture_output=True, timeout=20, check=True)
            status = subprocess.run([str(bins['pg_ctl']), '-D', str(data), 'status'], env=env, capture_output=True)
            stopped = status.returncode == 3
        if cleanup_ok and (not attempted or stopped):
            shutil.rmtree(temporary)
        else:
            raise RuntimeError('Cleanup incomplete; retained owned private directory '+str(temporary))
    if args.enforce_budget and not report['browser']['budgetPassed']:
        raise SystemExit('Page readiness exceeds the explicit benchmark budget; see report.')


if __name__ == '__main__':
    main()
