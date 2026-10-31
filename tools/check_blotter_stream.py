"""The streamed Blotter valuation against trading/workspace, its oracle, in an owned synthetic preview.

The stream values the scope once and sends start, rows and complete: every row, the totals, summaries and catalog
equal the workspace's for the same retained context. Mid-run behavior is made deterministic by holding a lock on the
interval-series values only the PPA valuation reads: the run sends start and every position before the PPAs, then
waits on the lock while the check acts.
"""
import threading
import time
from assistant_context import holding

STREAM = 'trading/workspace/stream'
CONVENTION = 'trading_workspace_v1'
# What the workspace adds to its catalog and the stream states in start, before any row.
RIGHTS = ('context', 'asOf', 'canRunDealRisk', 'canBook', 'canAmendTrades', 'bookingUnavailableReason')
# Members only the terminal frame carries, computed from the complete scope.
FINAL = ('totals', 'groups', 'summaries', 'catalog', 'blotter')
# Read by the PPA valuation after start, and by nothing before it or by any other position.
PPA_VALUES = 'market_data.interval_series_values'
# The API's single-line console log prefix of an error, such as an exception that escapes the application.
FAILED = 'fail: '


def pivot(view, *dimensions, convention='trading_blotter_totals_v1'):
    """A pivot of the view's own books and portfolios."""
    return {**view, 'mode': 'pivot', 'groupBy': list(dimensions), 'measures': ['presentValue', 'executionPnl', 'tradeCount'],
            'pivotConvention': convention, 'priceNumericalPolicy': None}


class Streams:
    """Streams and their workspace oracle; every frame read is kept to check the shape of any error frame."""

    def __init__(self, c):
        self.c, self.frames = c, []

    def read(self, body):
        frames = list(self.c.stream(STREAM, body))
        self.frames.extend(frames)
        return frames

    def errors(self):
        return [f for f in self.frames if f['type'] == 'error']


class Reader:
    """A stream read on its own thread, each frame stamped as it arrives, so the caller can act while it runs."""

    def __init__(self, c, body, streams=None):
        self.frames, self.error, self.done = [], None, False
        self.changed = threading.Condition()
        self.thread = threading.Thread(target=self._read, args=(c, body, streams), daemon=True)
        self.thread.start()

    def _read(self, c, body, streams):
        try:
            for frame in c.stream(STREAM, body):
                with self.changed:
                    self.frames.append((time.perf_counter(), frame))
                    self.changed.notify_all()
                if streams is not None:
                    streams.frames.append(frame)
        except Exception as error:
            self.error = error
        finally:
            with self.changed:
                self.done = True
                self.changed.notify_all()

    def until(self, arrived, timeout=20):
        """The index of the first frame for which arrived holds; fails if the stream ends or times out first."""
        deadline = time.perf_counter()+timeout
        with self.changed:
            while True:
                found = next((i for i, (_, f) in enumerate(self.frames) if arrived(f)), None)
                if found is not None:
                    return found
                assert not self.done, f'the stream ended before the awaited frame: {[f["type"] for _, f in self.frames]} {self.error}'
                remaining = deadline-time.perf_counter()
                assert remaining > 0, 'the awaited frame did not arrive in time'
                self.changed.wait(remaining)

    def finish(self, timeout=20):
        self.thread.join(timeout)
        assert not self.thread.is_alive(), 'the stream did not end in time'
        assert self.error is None, self.error
        return self.frames


def lock_waiters(query, table):
    """The backends waiting for a lock on table."""
    return query(f"SELECT string_agg(pid::text,',' ORDER BY pid) FROM pg_locks WHERE relation='{table}'::regclass AND NOT granted;")


def until(condition, seconds, message):
    """Polls condition; returns the seconds it took to hold."""
    began = time.monotonic()
    while not condition():
        assert time.monotonic()-began < seconds, message
        time.sleep(.02)
    return time.monotonic()-began


def grammar(frames):
    """Start, one or more rows frames, then complete; valued rises to the announced positions; final members come last."""
    types = [f['type'] for f in frames]
    assert types[0] == 'start' and types[-1] == 'complete' and len(types) >= 3 and set(types[1:-1]) == {'rows'}, types
    valued = [f['valued'] for f in frames[1:-1]]
    assert all(a < b for a, b in zip(valued, valued[1:])) and valued[-1] == frames[0]['positions'], valued
    assert frames[-1]['valued'] == frames[-1]['positions'] == frames[0]['positions'], frames[-1]
    assert not any(member in f for f in frames[:-1] for member in FINAL), [f['type'] for f in frames if any(m in f for m in FINAL)]


def rows(frames):
    return [row for f in frames if f['type'] == 'rows' for row in f['rows']]


def whole(frames):
    """No trade is split across frames."""
    seen = {}
    for index, f in enumerate(frames):
        for trade in {r['tradeId'] for r in f.get('rows', []) if f['type'] == 'rows'}:
            assert trade not in seen, f'trade {trade} spans frames {seen[trade]} and {index}'
            seen[trade] = index


def equals_workspace(frames, workspace):
    """Rows, blotter, summaries, catalog and the start rights are the workspace's."""
    start, complete = frames[0], frames[-1]
    blotter = {k: v for k, v in workspace['blotter'].items() if k != 'holdings'}
    assert complete['blotter'] == blotter, 'complete.blotter differs from the workspace blotter'
    assert complete['summaries'] == workspace['summaries'], 'complete.summaries differ from the workspace summaries'
    assert complete['catalog'] == workspace['catalog'], 'complete.catalog differs from the workspace catalog'
    assert {k: start[k] for k in RIGHTS} == {k: workspace['catalog'][k] for k in RIGHTS}, (start, {k: workspace['catalog'][k] for k in RIGHTS})


def run_checks(c, context, view, workspace_rows, original_context, query, psql, environment, log):
    """context is current knowledge on working data; original_context the retained context before the later bookings;
    log is the API's log file."""
    s = Streams(c)
    def body(selected=None, catalog='directory', summaries=(), at=None):
        return {'context': at or retained, 'view': selected or view, 'bookingCatalog': catalog, 'summaries': list(summaries)}
    def positions(at):
        return c.call('trading/positions', {'context': at, 'bookIds': view['bookIds'], 'portfolioIds': view['portfolioIds'], 'offset': 0, 'limit': 1})['total']

    working = s.read({'context': context, 'view': view, 'bookingCatalog': 'directory', 'summaries': []})
    start = working[0]
    retained = {**context, 'knowledgeMode': 'retained', 'contextId': start['context']['contextId']}
    current = c.call('trading/workspace', {'context': context, 'view': view, 'bookingCatalog': 'directory', 'summaries': []})
    assert start['canBook'] is True and start['canAmendTrades'] is True and start['bookingUnavailableReason'] is None, start
    assert {k: start[k] for k in RIGHTS if k != 'context'} == {k: current['catalog'][k] for k in RIGHTS if k != 'context'}
    again = s.read(body())
    assert rows(again) == rows(working) == workspace_rows, 'the retained context named by start does not reproduce the working rows'
    print('PASS blotter stream working_stream_keeps_rights: current knowledge streams with booking rights and names the context that reproduces it', flush=True)

    grammar(again)
    assert again[0]['positions'] == positions(retained) == len(workspace_rows), (again[0]['positions'], positions(retained))
    print('PASS blotter stream frame_grammar: start states the positions total, valued rises to it, totals and catalog come only in complete', flush=True)

    summaries = [pivot(view, 'book'), pivot(view, 'instrumentType'), pivot(view, 'commodity', 'instrumentType'),
                 pivot(view, 'portfolio', convention='trading_blotter_money_totals_v1')]
    runs = []
    for catalog in ('directory', 'priced_contracts'):
        for count in (0, 1, 4):
            request = body(catalog=catalog, summaries=summaries[:count])
            frames, workspace = s.read(request), c.call('trading/workspace', request)
            grammar(frames)
            assert rows(frames) == workspace['blotter']['holdings'], f'{catalog} with {count} summaries: rows differ from the workspace'
            equals_workspace(frames, workspace)
            runs.append(frames)
    print('PASS blotter stream oracle_trades: rows in order, blotter, summaries, catalog and rights equal trading/workspace for both catalogs and 0, 1 and 4 summaries', flush=True)

    for selected in summaries[:2]:
        request = body(selected)
        frames, workspace = s.read(request), c.call('trading/workspace', request)
        grammar(frames)
        assert all(f['rows'] == [] for f in frames if f['type'] == 'rows')
        assert frames[-1]['blotter']['groups'] == workspace['blotter']['groups'] and frames[-1]['blotter']['groups']
        equals_workspace(frames, workspace)
    print('PASS blotter stream oracle_pivot: pivot rows frames are empty and complete groups equal the workspace groups', flush=True)

    for frames in [working, again, *runs]:
        whole(frames)
    print('PASS blotter stream trades_whole: no trade spans two frames', flush=True)

    swap_trades = {r['tradeId'] for r in workspace_rows if r['instrumentType'] in ('financial_swap', 'heat_rate')}
    swap_frames = [[r['tradeId'] for r in f['rows']] for f in again[1:-1] if any(r['instrumentType'] in ('financial_swap', 'heat_rate') for r in f['rows'])]
    assert swap_trades and len(swap_frames) == len(swap_trades), (len(swap_frames), len(swap_trades))
    assert all(len(set(trades)) == 1 and trades[0] in swap_trades for trades in swap_frames), swap_frames
    print('PASS blotter stream swap_units: each financial swap and heat-rate trade streams in a rows frame of its own', flush=True)

    ppa_trades = {r['tradeId'] for r in workspace_rows if r['instrumentType'] == 'ppa'}
    ppa_frames = [f['rows'] for f in again[1:-1] if any(r['instrumentType'] == 'ppa' for r in f['rows'])]
    assert ppa_trades and len(ppa_frames) == len(ppa_trades), (len(ppa_frames), len(ppa_trades))
    assert all(len({r['tradeId'] for r in unit}) == 1 and unit[0]['tradeId'] in ppa_trades for unit in ppa_frames), ppa_frames
    statuses = [r['unavailableReason'] is None for unit in ppa_frames for r in unit]
    assert statuses == sorted(statuses, reverse=True), 'a valued PPA follows an unvalued one'
    print('PASS blotter stream ppa_units: each PPA trade streams in a rows frame of its own, valued trades before unvalued ones', flush=True)

    first, second = s.read(body()), s.read(body())
    assert rows(first) == rows(second) and first[-1] == second[-1] and first[0] == second[0]
    print('PASS blotter stream deterministic: two streams of one retained request give identical rows, start and complete', flush=True)

    earlier = body(at=original_context)
    frames, workspace = s.read(earlier), c.call('trading/workspace', earlier)
    grammar(frames)
    assert len(rows(frames)) == 5 and all(r['instrumentType'] == 'ppa' for r in rows(frames)) and rows(frames) == workspace['blotter']['holdings']
    equals_workspace(frames, workspace)
    print('PASS blotter stream historical: the retained context before the later bookings streams only its five PPA rows, as the workspace reads it', flush=True)

    unknown = {**body(), 'unexpected': True}
    invalid = body({**view, 'mode': 'columns'})
    convention = {**body(), 'context': {**retained, 'convention': 'independent_side_financial_swap_v2'}}
    for name, request, status in (('unknown member', unknown, 400), ('invalid view', invalid, 422), ('wrong convention', convention, 422)):
        refused = c.call('trading/workspace', request, expected=status)
        assert c.call(STREAM, request, expected=status) == refused, name
    print('PASS blotter stream refusals: an unknown member, an invalid view and a wrong convention are refused as trading/workspace refuses them, before any stream', flush=True)

    before = sum(r['instrumentType'] != 'ppa' for r in workspace_rows)
    assert 0 < before < len(workspace_rows)
    def blocked(work):
        """Runs work while the PPA valuation waits on its values, once every earlier position has been sent."""
        return holding(psql, environment, f'LOCK TABLE {PPA_VALUES} IN ACCESS EXCLUSIVE MODE', work)
    def waiting(reader):
        trigger = reader.until(lambda f: f['type'] == 'rows' and f['valued'] >= before)
        assert reader.frames[trigger][1]['valued'] == before, 'a PPA row arrived with the earlier positions'
        until(lambda: lock_waiters(query, PPA_VALUES) != '', 5, 'the run did not reach the PPA values')
        return trigger

    def read_while_valuing():
        reader = Reader(c, body(), s)
        waiting(reader)
        page = c.call('trading/positions', {'context': retained, 'bookIds': view['bookIds'], 'portfolioIds': view['portfolioIds'], 'offset': 0, 'limit': 1}, timeout=5)
        assert page['total'] == len(workspace_rows) and not reader.done and lock_waiters(query, PPA_VALUES) != '', page['total']
        return reader
    frames = [f for _, f in blocked(read_while_valuing).finish()]
    grammar(frames)
    assert rows(frames) == workspace_rows
    print('PASS blotter stream gate_released: trading/positions answers on the same session while the stream waits mid-run, and the stream then completes', flush=True)

    def switch_while_valuing():
        reader = Reader(c, body(), s)
        trigger = waiting(reader)
        c.call('profile', {'profile': 'viewer'})
        return reader, trigger, time.perf_counter()
    try:
        reader, trigger, switched = blocked(switch_while_valuing)
        frames = reader.finish()
    finally:
        c.call('profile', {'profile': 'trader'})
        c.bootstrap()
    after = frames[trigger+1:]
    assert [f['type'] for _, f in after] == ['error'], [f['type'] for _, f in after]
    at, error = after[0]
    assert error == {'type': 'error', 'code': 'access_denied', 'message': error['message']} and at > switched, error
    print('PASS blotter stream profile_switch_mid_run_is_error_frame: after a profile switch mid-run the next frame is access_denied and no rows follow', flush=True)

    logged = len(log.read_text())
    def abort_while_valuing():
        aborted = c.stream(STREAM, body(catalog='priced_contracts'))
        assert next(aborted)['type'] == 'start'
        while next(aborted)['valued'] < before:
            pass
        until(lambda: lock_waiters(query, PPA_VALUES) != '', 5, 'the run did not reach the PPA values')
        backend = lock_waiters(query, PPA_VALUES)
        assert ',' not in backend, backend
        aborted.close()
        # The lock is still held: only the abort can end the wait, and the run would otherwise wait out its deadline.
        left = until(lambda: query(f"SELECT count(*) FROM pg_stat_activity WHERE pid={backend} AND (state<>'idle' OR wait_event_type='Lock');") == '0',
                     3, 'the aborted run still holds its connection')
        return left
    left = blocked(abort_while_valuing)
    assert positions(retained) == len(workspace_rows)
    time.sleep(1)
    since = log.read_text()[logged:]
    assert FAILED not in since, since
    print(f'PASS blotter stream abort_releases_connection: {left*1000:.0f} ms after a client abort mid-run its backend left the lock wait and returned idle to the pool, '
          f'the session reads its positions, and the API logged no error ({len(since.splitlines())} log lines meanwhile)', flush=True)

    assert s.errors(), 'no error frame was read'
    assert all(set(f) == {'type', 'code', 'message'} for f in s.errors()), s.errors()
    print('PASS blotter stream error_frame_shape: every error frame states exactly its type, code and message', flush=True)


def run_valuation_status_checks(c, frame):
    """D220 at a close that cannot value every position: the stream sends the same unvalued rows and totals as the workspace."""
    s = Streams(c)
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None, 'priceNumericalPolicy': None}
    current = {'snapshotId': frame['id'], 'convention': CONVENTION, 'knowledgeMode': 'current'}
    start = s.read({'context': current, 'view': view, 'bookingCatalog': 'directory', 'summaries': []})[0]
    request = {'context': {**current, 'knowledgeMode': 'retained', 'contextId': start['context']['contextId']}, 'view': view,
               'bookingCatalog': 'directory', 'summaries': []}
    frames, workspace = s.read(request), c.call('trading/workspace', request)
    grammar(frames)
    streamed = rows(frames)
    unvalued = [r for r in streamed if r['unavailableReason'] is not None]
    assert streamed == workspace['blotter']['holdings']
    assert {r['unavailableReason'] for r in unvalued} == {'index_fixing_missing', 'source_expired'}, {r['unavailableReason'] for r in unvalued}
    assert unvalued == [r for r in workspace['blotter']['holdings'] if r['unavailableReason'] is not None]
    complete = frames[-1]['blotter']
    assert complete['unvaluedPositions'] == workspace['blotter']['unvaluedPositions'] > 0 and complete['totals'] == workspace['blotter']['totals']
    equals_workspace(frames, workspace)
    assert not s.errors()
    print('PASS valuation status: the stream sends the unvalued index_fixing_missing and source_expired rows, unvalued count and totals of the workspace', flush=True)


def run_hosted_checks(session, principal, query, psql, environment, frame):
    """A hosted sign-in session that ends while a stream's read runs is refused by the stream's own authority check.

    Sign-out revokes under the security epoch, so it waits for the read's transaction; a session reaching its end does
    not. Here it ends while the read waits on a lock before start, so the refusal is the check before start, an ordinary
    HTTP 403. The same check runs before every rows frame."""
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None, 'priceNumericalPolicy': None}
    request = {'context': {'snapshotId': frame['id'], 'convention': CONVENTION, 'knowledgeMode': 'current'}, 'view': view,
               'bookingCatalog': 'directory', 'summaries': []}
    digest = query(f"SELECT token_digest FROM security.hosted_sessions WHERE principal_id='{principal}' AND revoked_at IS NULL "
                   "AND expires_at>clock_timestamp() ORDER BY created_at DESC LIMIT 1;")
    outcome = {}
    def read():
        try:
            outcome['refusal'] = session.call('/api/'+STREAM, request, expected=403)[0]
        except Exception as error:
            outcome['error'] = error
    def end_session():
        reading = threading.Thread(target=read, daemon=True)
        reading.start()
        until(lambda: lock_waiters(query, 'trading.books') != '' or not reading.is_alive(), 5,
              'the stream did not reach the books: '+query("SELECT string_agg(query,' | ') FROM pg_stat_activity WHERE wait_event_type='Lock';"))
        assert reading.is_alive(), outcome
        query(f"UPDATE security.hosted_sessions SET expires_at=clock_timestamp() WHERE token_digest='{digest}';")
        return reading
    reading = holding(psql, environment, 'LOCK TABLE trading.books IN ACCESS EXCLUSIVE MODE', end_session)
    reading.join(20)
    assert not reading.is_alive() and 'error' not in outcome, outcome.get('error')
    assert outcome['refusal']['code'] == 'access_denied' and set(outcome['refusal']) == {'code', 'message'}, outcome['refusal']
    print('PASS hosted blotter stream: a sign-in session that ends during the stream read is refused by the stream authority check, before any frame', flush=True)
