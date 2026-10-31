"""The streamed Blotter valuation against trading/workspace, timed in an owned synthetic preview.

Workloads: the standard example's mixed book (eight futures, sixteen financial swaps, eight heat-rate swaps and five
three-day PPAs, booked through ordinary operations), then the same book with one long-schedule hourly five-side PPA
in a second book. Each workload is read at one retained context: one unmeasured warm-up, then SAMPLES alternating
trading/workspace and trading/workspace/stream requests on one client, each with fresh request-owned valuation. A
stream sample records the time to its first rows frame and to complete. A client daemon samples the API's RSS.
"""
from datetime import datetime, timezone
import json
from pathlib import Path
import statistics
import time

from benchmark_interval_series import environment
from benchmark_ppa_http import HttpFixture, ProcessRssSampler, api_artifacts, expected_value, instant, midnight, terms
from check_blotter_stream import STREAM, Reader, grammar, rows
from preview_client import PreviewClient
from seed_demo_instruments import seed_heat_rates, seed_ppas
from seed_preview_positions import BOOKS, seed_positions
from seed_preview_ppa import seed_ppa
from seed_preview_swaps import seed_swaps

SAMPLES = 5
LONG_BOOK = 'd1000000-0000-0000-0000-000000000003'
# The longest whole-year hourly term whose Blotter completes inside the unchanged 15-second request deadline.
LONG_FROM, LONG_TO = '2031-11-01', '2051-11-01'


def p50(values):
    return statistics.median(values)


def seeded(url, manifest, run_id, owned_root):
    """The standard example's mixed book, booked through ordinary operations."""
    seed_ppa(url, manifest)
    c = PreviewClient(url)
    c.bootstrap()
    c.call('profile', {'profile': 'trader'})
    assert len(seed_ppas(c, manifest)) == 5
    seed_positions(url, run_id, owned_root)
    seed_swaps(url, run_id, owned_root)
    assert len(seed_heat_rates(c)) == 8
    return c


def retained(c, context, view):
    """A request at the retained context that names the current knowledge, and its workspace rows."""
    current = c.call('trading/workspace', {'context': context, 'view': view, 'bookingCatalog': 'directory', 'summaries': []})
    at = {**context, 'knowledgeMode': 'retained', 'contextId': current['catalog']['context']['contextId']}
    return {'context': at, 'view': view, 'bookingCatalog': 'directory', 'summaries': []}


def timed_stream(c, body):
    started = time.perf_counter()
    frames, first = [], None
    for frame in c.stream(STREAM, body):
        if frame['type'] == 'rows' and first is None:
            first = time.perf_counter()-started
        frames.append(frame)
    return frames, first*1000, (time.perf_counter()-started)*1000


def measure(c, body, api_pid):
    """Warm-up, then alternating workspace and stream samples; the stream's rows equal the workspace's in every sample."""
    workspace = c.call('trading/workspace', body)
    frames, _, _ = timed_stream(c, body)
    grammar(frames)
    assert rows(frames) == workspace['blotter']['holdings'], 'the stream rows differ from the workspace rows'
    samples = {'workspaceMs': [], 'streamFirstRowsMs': [], 'streamCompleteMs': []}
    sampler = ProcessRssSampler(api_pid)
    with sampler:
        for _ in range(SAMPLES):
            started = time.perf_counter()
            assert c.call('trading/workspace', body)['blotter']['holdings'] == workspace['blotter']['holdings']
            samples['workspaceMs'].append((time.perf_counter()-started)*1000)
            sampled, first, complete = timed_stream(c, body)
            assert rows(sampled) == workspace['blotter']['holdings'] and sampled[-1]['type'] == 'complete'
            samples['streamFirstRowsMs'].append(first)
            samples['streamCompleteMs'].append(complete)
    assert sampler.report()['status'] == 'sampled', sampler.report()
    held = workspace['blotter']['holdings']
    by_type = {}
    for row in held:
        by_type.setdefault(row['instrumentType'], set()).add(row['contractId'])
    blotter = workspace['blotter']
    return {'samples': samples, 'p50': {name: p50(values) for name, values in samples.items()},
            'positions': len(held), 'trades': len({r['tradeId'] for r in held}),
            'unvalued': blotter['unvaluedPositions'],
            'uniqueContracts': {kind: len(ids) for kind, ids in sorted(by_type.items())},
            'evaluatedWork': {'futuresEvaluatedContracts': blotter['evaluatedContracts'], **blotter['work'],
                              'ppaBindings': sum(r['instrumentType'] == 'ppa' for r in held)},
            'framesByProduct': {kind: sum(any(r['instrumentType'] == kind for r in f['rows']) for f in frames if f['type'] == 'rows')
                                for kind in sorted(by_type)},
            'rowsFrames': sum(f['type'] == 'rows' for f in frames),
            'apiRss': sampler.report(), 'rows': held}


def check_incremental(c, body, long_trade):
    """On the long-PPA scope, the first rows frame arrives before the long PPA's row and before complete."""
    reader = Reader(c, body)
    first = reader.until(lambda f: f['type'] == 'rows')
    frames = reader.finish()
    complete = next(at for at, f in frames if f['type'] == 'complete')
    holding = next(i for i, (_, f) in enumerate(frames) if any(r['tradeId'] == long_trade for r in f.get('rows', [])))
    assert first < holding and frames[first][0] < complete, (first, holding)
    print(f'PASS blotter stream incremental: the first rows frame (frame {first}) precedes the long PPA (frame {holding}) '
          f'by {(frames[holding][0]-frames[first][0])*1000:.0f} ms', flush=True)


def run_benchmark(url, manifest, query, *, dotnet, api_pid, run_id, owned_root, output):
    c = seeded(url, manifest, run_id, owned_root)
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2020-12-18T16:00:00'))
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    view = {'mode': 'trades', 'bookIds': [BOOKS[1]], 'portfolioIds': [], 'groupBy': [], 'measures': [],
            'pivotConvention': None, 'priceNumericalPolicy': None}
    standard = measure(c, retained(c, context, view), api_pid)
    print('BLOTTER_STREAM_STANDARD='+json.dumps({**standard['p50'], 'framesByProduct': standard['framesByProduct']}), flush=True)
    assert standard['positions'] == 33 and standard['unvalued'] == 0, (standard['positions'], standard['unvalued'])

    fixture = HttpFixture(url, manifest, None)
    config = manifest['ppaBookings']['workloads']['60']
    _, cells, _, _ = expected_value(LONG_FROM, LONG_TO, config['zone'])
    assert fixture.load(60, LONG_FROM, LONG_TO) == cells
    start, end = (instant(midnight(day, config['zone'])) for day in (LONG_FROM, LONG_TO))
    # A twenty-year durable booking can outlast one client attempt; the same operation is resumed, never repeated.
    booked, _ = fixture.book_resuming(terms(manifest, 60, LONG_FROM, LONG_TO), start, end, book=LONG_BOOK)
    long_view = {**view, 'bookIds': [BOOKS[1], LONG_BOOK]}
    body = retained(c, context, long_view)
    long = measure(c, body, api_pid)
    print('BLOTTER_STREAM_LONG_PPA='+json.dumps({**long['p50'], 'framesByProduct': long['framesByProduct']}), flush=True)
    row = next(r for r in long['rows'] if r['tradeId'] == booked['tradeId'])
    assert row['presentValue'] is not None and long['unvalued'] == 0, row['unavailableReason']
    check_incremental(c, body, booked['tradeId'])

    limit = standard['p50']['workspaceMs']*1.1+50
    runs = {'standard': standard, 'longPpa': long}
    described = environment(query, dotnet)
    described['apiArtifacts'] = api_artifacts(api_pid)
    report = {
        'measuredAtUtc': datetime.now(timezone.utc).isoformat(),
        'workload': {
            'protocol': __doc__.strip(), 'samples': SAMPLES, 'requestDeadlineSeconds': 15, 'context': 'retained, 2020-12-18 close',
            'standard': {'books': 1, 'trades': standard['trades']},
            'longPpa': {'books': 2, 'trades': long['trades'], 'from': LONG_FROM, 'to': LONG_TO,
                        'years': int(LONG_TO[:4])-int(LONG_FROM[:4]), 'clock': config['clock'], 'intervalMinutes': 60,
                        'nativeIntervals': cells, 'sides': 5, 'rowEvaluations': 5*cells, 'quantityMw': '80'}},
        'positions': {name: run['positions'] for name, run in runs.items()},
        'uniqueContracts': {name: run['uniqueContracts'] for name, run in runs.items()},
        'evaluatedWork': {name: {**run['evaluatedWork'], 'scenarios': 1} for name, run in runs.items()},
        **{key: {name: {'samples': run['samples'][key], 'p50': run['p50'][key]} for name, run in runs.items()}
           for key in ('workspaceMs', 'streamFirstRowsMs', 'streamCompleteMs')},
        'framesByProduct': {name: {**run['framesByProduct'], 'rowsFrames': run['rowsFrames']} for name, run in runs.items()},
        'apiRssBytes': {name: run['apiRss'] for name, run in runs.items()},
        'environment': described,
        'performance': {'standardStreamCompleteP50Ms': standard['p50']['streamCompleteMs'], 'limitMs': limit,
                        'rule': 'standard streamCompleteMs p50 <= workspaceMs p50 x 1.1 + 50'}}
    Path(output).write_text(json.dumps(report, indent=2))
    print('BLOTTER_STREAM_BENCHMARK='+json.dumps({k: report[k] for k in ('positions', 'workspaceMs', 'streamFirstRowsMs', 'streamCompleteMs',
                                                                           'framesByProduct', 'performance')}), flush=True)
    assert standard['p50']['streamCompleteMs'] <= limit, f"stream p50 {standard['p50']['streamCompleteMs']:.0f} ms exceeds {limit:.0f} ms"
    print('PASS blotter stream performance: the standard stream completes within 1.1 x the workspace p50 + 50 ms', flush=True)
    return report
