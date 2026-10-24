"""Twenty-year audited interval series workloads through the launcher's owned synthetic preview.

An editor drives the same authorized HTTP routes as the Interval series screen; its import bodies
(about 1.2 MB) exceed the default 32 KiB request limit and fit the revise route's 8 MiB limit. Every
step checks its own interval counts and values, and a refusal, timeout or wrong count fails the run
naming the workload and step. The report is a dictionary of measurements; the launcher prints it as
JSON (docs/implementation.md, "Audited interval series")."""
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import platform
import subprocess
import time
from uuid import uuid4

from preview_client import PreviewClient

ROOT = Path(__file__).resolve().parents[1]
# Both this client (tools/preview_client.py) and the browser transport (AbortSignal.timeout(20000) in web/src/app.ts)
# abandon a request after 20 seconds, so a slower revision fails the run instead of being reported.
REQUEST_TIMEOUT_SECONDS = 20
CYCLE = ['0.1', '0.2', '0.3', '0.4', '0.5', '0.6', '0.7', '0.8', '0.9']
WINDOW_START = datetime(2036, 7, 1, 5, tzinfo=timezone.utc)
READ_WINDOW = timedelta(days=366)
CHANGE_WINDOW = timedelta(days=31)
CORRECTION_INTERVALS = 10_000
# Each workload is one series over 2027-01-01T05:00Z to 2047-01-01T05:00Z, imported as the listed
# (first year, end year, intervals) revisions; the counts are declared here and checked against the spans.
WORKLOADS = [
    {'name': 'hourly_20y', 'minutes': 60, 'intervals': 175_320, 'revisions': [(2027, 2047, 175_320)]},
    {'name': 'quarter_hour_20y', 'minutes': 15, 'intervals': 701_280,
     'revisions': [(2027, 2032, 175_296), (2032, 2037, 175_392), (2037, 2042, 175_296), (2042, 2047, 175_296)]},
]
# Ten full rewrites of one hourly series, so that every interval holds ten stored revisions.
REWRITES = {'name': 'hourly_rewrites', 'minutes': 60, 'intervals': 10 * 175_320, 'revisions': [(2027, 2047, 175_320)] * 10}


def midnight_eastern(year):
    """The first instant of a year on synthetic_eastern in standard time, as a UTC instant."""
    return datetime(year, 1, 1, 5, tzinfo=timezone.utc)


SERIES_START = midnight_eastern(2027)


def instant(at):
    return at.strftime('%Y-%m-%dT%H:%M:%SZ')


def stored(at):
    """An instant as reads render it."""
    return instant(at).replace('Z', '+00:00')


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def cycle_value(index, shift=0):
    """The value an import gives the interval `index` steps after the series start; each rewrite shifts the cycle."""
    return CYCLE[(index + shift) % len(CYCLE)]


def corrected_value(index):
    return cycle_value(index) + '5'


def plan(workload):
    """The workload's imports as (first start, interval count), after checking spans against declared counts."""
    step = timedelta(minutes=workload['minutes'])
    imports = []
    for first_year, end_year, declared in workload['revisions']:
        first, end = midnight_eastern(first_year), midnight_eastern(end_year)
        count, remainder = divmod(end - first, step)
        require(not remainder and count == declared,
                f"{workload['name']}: {first_year}-{end_year} holds {count} intervals, declared {declared}")
        imports.append((first, count))
    total = sum(count for _, count in imports)
    require(total == workload['intervals'], f"{workload['name']}: revisions total {total}, declared {workload['intervals']}")
    return imports


def verify_values(label, rows, first, step, count, revision_at, shift):
    """Rows must be exactly the cycling values of `count` consecutive intervals from `first`, each from the revision
    `revision_at` names for its start."""
    require(len(rows) == count, f'{label}: read {len(rows)} intervals, expected {count}')
    offset = (first - SERIES_START) // step
    expected = [(stored(first + k * step), cycle_value(offset + k, shift), revision_at(first + k * step)) for k in range(count)]
    require([(row['start'], row['value'], row['revision']) for row in rows] == expected, f'{label}: values differ from the imported cycle')


def revision(series, expected, first, values, reason):
    return {'requestId': str(uuid4()), 'seriesId': series, 'expectedRevision': expected, 'reason': reason,
            'segments': [{'start': instant(first), 'values': values}]}


def timed(label, action):
    """(elapsed seconds, response) of one HTTP step; a failure names the step."""
    started = time.perf_counter()
    try:
        response = action()
    except Exception as error:
        raise RuntimeError(f'{label} failed: {error}') from error
    return time.perf_counter() - started, response


def storage(query):
    """Heap-and-TOAST and index bytes of every interval series table."""
    table, index = query("""SELECT coalesce(sum(pg_table_size(c.oid)),0),coalesce(sum(pg_indexes_size(c.oid)),0)
        FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
        WHERE n.nspname='market_data' AND c.relkind='r' AND c.relname LIKE 'interval!_series%' ESCAPE '!';""").split('|')
    return int(table), int(index)


def window(client, name, series, first, span, label):
    # Each read pins a fresh certified cutoff, as the screen does on every catalog load.
    _, catalog = timed(f'{name}: fetch the cutoff for {label}', lambda: client.call('interval-series'))
    return {'seriesId': series, 'from': instant(first), 'to': instant(first + span), 'knownAt': catalog['knownAt']}


def import_all(client, workload, imports, shifts):
    """Defines the workload's series and imports its revisions, the n-th with cycle shift shifts[n]."""
    name, step = workload['name'], timedelta(minutes=workload['minutes'])
    _, defined = timed(f'{name}: define', lambda: client.call('interval-series/define', {
        'requestId': str(uuid4()), 'code': f'synthetic_benchmark_{name}', 'name': f'Synthetic benchmark {name}',
        'kind': 'capacity_factor_forecast', 'specification': 'test_zero_rate', 'clock': 'synthetic_eastern', 'intervalMinutes': workload['minutes']}))
    series = defined['seriesId']
    head, request_bytes, revise_seconds, imported = 0, [], [], 0
    for (first, count), shift in zip(imports, shifts, strict=True):
        first_index = (first - SERIES_START) // step
        body = revision(series, head, first, [cycle_value(first_index + k, shift) for k in range(count)], f'Synthetic {name} import')
        request_bytes.append(len(json.dumps(body).encode()))
        seconds, receipt = timed(f'{name}: import revision {head + 1}', lambda: client.call('interval-series/revise', body))
        require(receipt == {'requestId': body['requestId'], 'seriesId': series, 'revision': head + 1, 'intervals': count},
                f'{name}: import revision {head + 1} saved {receipt}')
        revise_seconds.append(seconds)
        head += 1
        imported += receipt['intervals']
    require(imported == workload['intervals'], f"{name}: saved {imported} intervals, expected {workload['intervals']}")
    return series, head, request_bytes, revise_seconds


def read_window(client, name, series, step, head, revision_at, shift):
    """The 366-day read at a cutoff after the last import, checked against the imported values."""
    read = window(client, name, series, WINDOW_START, READ_WINDOW, 'the 366-day read')
    seconds, values = timed(f'{name}: read 366 days', lambda: client.call('interval-series/values', read))
    verify_values(f'{name}: read 366 days', values['rows'], WINDOW_START, step, READ_WINDOW // step, revision_at, shift)
    require(values['head'] == head, f"{name}: read head {values['head']}, expected {head}")
    return read, seconds, values


def run_workload(client, query, api_pid, workload, imports):
    name, minutes = workload['name'], workload['minutes']
    step = timedelta(minutes=minutes)
    offset = (WINDOW_START - SERIES_START) // step

    _, (table_before, index_before) = timed(f'{name}: measure storage before', lambda: storage(query))
    series, head, request_bytes, revise_seconds = import_all(client, workload, imports, [0] * len(imports))
    imported = sum(count for _, count in imports)
    holding = lambda at: max(number for number, (first, _) in enumerate(imports, 1) if first <= at)
    original = holding(WINDOW_START)
    read, read_seconds, values = read_window(client, name, series, step, head, holding, 0)
    read_count = len(values['rows'])

    correction = revision(series, head, WINDOW_START, [corrected_value(offset + k) for k in range(CORRECTION_INTERVALS)],
                          f'Synthetic {name} correction')
    correction_seconds, receipt = timed(f'{name}: correct {CORRECTION_INTERVALS} intervals',
                                        lambda: client.call('interval-series/revise', correction))
    require(receipt == {'requestId': correction['requestId'], 'seriesId': series, 'revision': head + 1, 'intervals': CORRECTION_INTERVALS},
            f'{name}: correction saved {receipt}')

    # The correction leaves the earlier knowledge cutoff's values untouched.
    _, earlier = timed(f'{name}: re-read 366 days at the earlier cutoff', lambda: client.call('interval-series/values', read))
    require(earlier['head'] == head and earlier['rows'] == values['rows'], f'{name}: the earlier cutoff now reads differently')

    changes = window(client, name, series, WINDOW_START, CHANGE_WINDOW, 'the change log')
    changes_seconds, log = timed(f'{name}: change log for 31 days', lambda: client.call('interval-series/changes', changes))
    changed = CHANGE_WINDOW // step
    expected_rows = [(stored(WINDOW_START + k * step), head + 1, original, cycle_value(offset + k), corrected_value(offset + k)) for k in range(changed)]
    expected_rows += [(stored(WINDOW_START + k * step), original, None, None, cycle_value(offset + k)) for k in range(changed)]
    require(len(log['rows']) == 2 * changed, f"{name}: the change log has {len(log['rows'])} rows, expected {2 * changed}")
    require([(row['start'], row['revision'], row['previousRevision'], row['previousValue'], row['value']) for row in log['rows']] == expected_rows,
            f'{name}: the change log differs from the import and the correction')
    require([(row['revision'], row['intervals'], row['intervalsInWindow']) for row in log['revisions']] ==
            [(head + 1, CORRECTION_INTERVALS, changed), (original, imports[original - 1][1], changed)],
            f'{name}: the revision summary differs from the import and the correction')

    _, (table_after, index_after) = timed(f'{name}: measure storage after', lambda: storage(query))
    _, resident = timed(f'{name}: measure API memory', lambda: api_resident_bytes(api_pid))
    return {'name': name, 'clock': 'synthetic_eastern', 'intervalMinutes': minutes, 'intervals': imported,
            'revisionIntervals': [count for _, count in imports], 'requestBytes': request_bytes,
            'reviseSeconds': [round(s, 3) for s in revise_seconds],
            'read366DaysIntervals': read_count, 'read366DaysSeconds': round(read_seconds, 3),
            'correction10000Seconds': round(correction_seconds, 3),
            'changes31DaysRows': len(log['rows']), 'changes31DaysSeconds': round(changes_seconds, 3),
            'maxReviseSeconds': round(max([*revise_seconds, correction_seconds]), 3), 'requestTimeoutSeconds': REQUEST_TIMEOUT_SECONDS,
            'tableBytes': table_after - table_before, 'indexBytes': index_after - index_before, 'apiResidentBytesAfter': resident}


def run_rewrites(client, query, api_pid, workload, imports):
    """Successive full rewrites of one series, each timed, then one 366-day read of the last: the read and every later
    write meet as many stored revisions per interval as there were rewrites."""
    name, step = workload['name'], timedelta(minutes=workload['minutes'])
    _, (table_before, index_before) = timed(f'{name}: measure storage before', lambda: storage(query))
    series, head, request_bytes, revise_seconds = import_all(client, workload, imports, range(len(imports)))
    _, read_seconds, values = read_window(client, name, series, step, head, lambda at: head, head - 1)
    _, (table_after, index_after) = timed(f'{name}: measure storage after', lambda: storage(query))
    _, resident = timed(f'{name}: measure API memory', lambda: api_resident_bytes(api_pid))
    return {'name': name, 'clock': 'synthetic_eastern', 'intervalMinutes': workload['minutes'], 'intervals': workload['intervals'],
            'revisionIntervals': [count for _, count in imports], 'requestBytes': request_bytes,
            'reviseSeconds': [round(s, 3) for s in revise_seconds], 'revisionsPerInterval': head,
            'read366DaysIntervals': len(values['rows']), 'read366DaysSeconds': round(read_seconds, 3),
            'maxReviseSeconds': round(max(revise_seconds), 3), 'requestTimeoutSeconds': REQUEST_TIMEOUT_SECONDS,
            'tableBytes': table_after - table_before, 'indexBytes': index_after - index_before, 'apiResidentBytesAfter': resident}


def api_resident_bytes(pid):
    """Resident set of the launcher-owned API process now (not a peak)."""
    kibibytes = subprocess.run(['ps', '-o', 'rss=', '-p', str(pid)], capture_output=True, text=True, check=True, timeout=10).stdout
    return int(kibibytes) * 1024


def environment(query, dotnet):
    sdk = subprocess.run([str(dotnet), '--version'], cwd=ROOT, capture_output=True, text=True, check=True, timeout=30,
                         env={**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1'}).stdout.strip()
    return {'postgres': query('SHOW server_version;').split()[0], 'dotnet': sdk,
            'cpu': f'{platform.machine()}, {os.cpu_count()} logical processors',
            'memoryBytes': os.sysconf('SC_PAGE_SIZE') * os.sysconf('SC_PHYS_PAGES'), 'os': platform.platform(),
            'topology': 'One machine: client, API (the launcher\'s Debug build) and PostgreSQL; loopback HTTP and a Unix-socket database.'}


def run_benchmark(url, query, dotnet, api_pid):
    """The report for every workload; api_pid is the launcher-owned API process whose memory is sampled."""
    planned = [(run_workload, workload, plan(workload)) for workload in WORKLOADS] + [(run_rewrites, REWRITES, plan(REWRITES))]
    client = PreviewClient(url)
    timed('start session', client.bootstrap)
    timed('select the editor profile', lambda: client.call('profile', {'profile': 'editor'}))
    _, described = timed('describe the environment', lambda: environment(query, dotnet))
    return {'environment': described,
            'workloads': [run(client, query, api_pid, workload, imports) for run, workload, imports in planned]}
