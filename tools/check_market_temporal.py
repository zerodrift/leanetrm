"""One certified cutoff for market definitions, exact references, rates and observations."""
from decimal import Decimal
from uuid import uuid4
from close_session_fixture import record


def run_checks(Client, check, after_browser):
    c = Client(); c.bootstrap(); c.call('profile', {'profile': 'editor'})
    source = c.call('workspace')
    seed = next(f for f in source['frames'] if f['at'].startswith('2020-12-17'))
    request = {'effectiveOn': '2020-12-17', 'mode': 'current', 'selection': 'latest_correction'}
    first = c.call('market/context', request)
    context = first['context']
    workspace = c.call('workspace')
    frame = next(f for f in workspace['frames'] if f.get('contextId') == context['contextId'] and f['at'] == seed['at'])
    before = c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'local_power'})
    oid = next(o['id'] for o in workspace['observations'] if o['id'] in frame['observationIds'] and o['series_id'].endswith('000000000001'))
    c.call('corrections', {'snapshotId': frame['id'], 'observationId': oid, 'value': '3.2'}, expected=422)
    check('a retained market context is read-only even for an editor')
    c.call('market/current', {})
    changed = c.call('corrections', {'snapshotId': seed['id'], 'observationId': oid, 'value': '3.2'})
    c.call('market/context', {**request, 'mode': 'at', 'knownAt': context['knownAt']})
    historical = c.call('workspace')
    check('certified aliases omit an entire later manual correction while retaining original seed choices',
          len(historical['frames'])==sum(f['at'][:10]<='2020-12-17' for f in source['frames']) and
          all(changed['observationId'] not in f['observationIds'] for f in historical['frames']))
    retained_boot = c.call('bootstrap')
    compact_boot = c.call('bootstrap?includeFrames=false')
    check('bootstrap omits resolved historical frames while the authorized workspace preserves their exact context',
          compact_boot['frames'] == [] and retained_boot['frames'] == c.call('bootstrap')['frames'] == [] and
          bool(historical['frames']) and all(f['contextId'] == historical['context']['contextId'] for f in historical['frames']) and
          c.call('workspace')['context'] == historical['context'])
    check('historical workspace omits later corrections and later observation dates', changed['observationId'] not in [o['id'] for o in historical['observations']] and all(o['observed_at'][:10] <= '2020-12-17' for o in historical['observations']))
    sources = c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'local_power', 'includeSources': True})
    check('on-demand sources retain the certified cutoff without leaking later corrections',
          sources['context'] == context and changed['observationId'] not in {o['id'] for o in sources['observations']}
          and {o['id'] for o in sources['observations']} <= {o['id'] for o in historical['observations']})
    check('commodity replay retains exact result after a later correction', c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'local_power'}) == before)
    rates = c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'interest_rates'})
    check('interest rates use the identical certified market context', rates['context'] == context)
    c.call('evaluate', {'snapshotId': changed['frame']['id'], 'curve': 'local_power'}, expected=422)
    check('historical view cannot evaluate a different unpinned context')
    # A recorded close stays exact (D172); the correction reaches a close only when it is recorded as that day's close
    # (D334), which every view of that session then reads. A test session keeps the built-in closes untouched.
    c.call('profile', {'profile': 'approver'})
    original = record(c, next(f for f in c.call('workspace')['frames'] if f['id'] == seed['id']), 'Synthetic original close')
    c.call('select-observation', {'snapshotId': seed['id'], 'observationId': changed['observationId']})
    snapshot = next(f for f in c.call('workspace')['frames'] if 'close' not in f and changed['observationId'] in f['observationIds'])
    corrected = record(c, snapshot, 'Synthetic corrected close')
    c.call('profile', {'profile': 'editor'})
    latest = c.call('market/context', request)
    current = c.call('workspace')
    newer = next(f for f in current['frames'] if f.get('originCloseId') == corrected['id'])
    assert original['id'] not in {f.get('originCloseId') for f in current['frames']}, 'A replaced close is still offered'
    after = c.call('evaluate', {'snapshotId': newer['id'], 'curve': 'local_power'})
    check('a recorded correction replaces the close every view reads', after['points'][0]['value'] != before['points'][0]['value'])
    # History is a session's own (D334): read it over the built-in session's closes under the latest context.
    seeded = next(f for f in current['frames'] if f.get('originCloseId') == seed['id'])
    history = c.call('history', {'revisionPolicy': 'exact_snapshot_pins_v1', 'snapshotId': seeded['id'], 'curve': 'local_power', 'contractId': after['points'][0]['contractId']})
    check('contract history stops at the context date', all(p['at'][:10] <= '2020-12-17' for p in history['points']))
    def chart_of(points):
        return [{'at': p['at'], 'value': p['value'], 'unavailableReason': p['unavailableReason']} for p in points]
    check('the history states its chart series: each returned point, one per instant, with its availability',
          len({p['at'] for p in history['points']}) == len(history['points']) and history['chart'] == {'points': chart_of(history['points'])})
    sma = {'code': 'simple_moving_average_v1', 'parameters': {'window': 2}}
    with_overlay = c.call('history', {'revisionPolicy': 'exact_snapshot_pins_v1', 'snapshotId': seeded['id'], 'curve': 'local_power', 'contractId': after['points'][0]['contractId'], 'overlays': [sma]})
    points = with_overlay['points']
    series = with_overlay['overlays'][0]['series']['value']
    means = [None if i == 0 or points[i]['value'] is None or points[i-1]['value'] is None
             else (Decimal(points[i]['value']) + Decimal(points[i-1]['value'])) / 2 for i in range(len(points))]
    check('history overlays are the means of exactly the returned points under the selected context',
          len(points) > 2 and with_overlay['overlays'][0]['policy'] == 'exact_rational_half_even_18_v1'
          and [v['at'] for v in series] == [p['at'] for p in points] and with_overlay['points'] == history['points']
          and all((v['value'] is None) == (m is None) and (m is None or Decimal(v['value']) == m) for v, m in zip(series, means))
          and all(v['reason'] == 'window_incomplete' for v, m in zip(series, means) if m is None))
    check('history carries no overlays field unless requested', 'overlays' not in history)
    refusal = c.call('history', {'revisionPolicy': 'exact_snapshot_pins_v1', 'snapshotId': newer['id'], 'curve': 'local_power', 'contractId': after['points'][0]['contractId'],
                                 'overlays': [{'code': 'simple_moving_average_v1', 'parameters': {'window': 1}}]}, expected=422)
    check('an out-of-range overlay window is a structured overlay_parameter refusal', refusal['code'] == 'overlay_parameter')
    catalog = c.call('analytics/overlays')
    check('the overlay catalog names the policy, codes and window bounds',
          catalog['policy'] == 'exact_rational_half_even_18_v1' and
          {o['code'] for o in catalog['overlays']} == {'simple_moving_average_v1', 'exponential_moving_average_v1', 'rolling_range_v1'} and
          all(o['parameters'] == [{'name': 'window', 'label': 'Window', 'minimum': 2, 'maximum': 250}] for o in catalog['overlays']))
    def history_versions():
        # Registers durable closes at the seed's instant in another session; run after the browser suites (run_preview.py).
        # The deferred run opens its own session: the first one may have expired during the browser phase.
        c = Client(); c.bootstrap(); c.call('profile', {'profile': 'editor'})
        fresh = c.call('workspace')
        seed_frame = next(f for f in fresh['frames'] if f['id'] in fresh['initialFrameIds'] and f['at'] == seed['at'] and not f.get('ppaBundle'))
        corrected_frame = c.call('select-observation', {'snapshotId': seed_frame['id'], 'observationId': changed['observationId']})['frame']
        c.call('profile', {'profile': 'approver'}); c.call('market/current', {})
        current_frames = c.call('workspace')['frames']
        pinned = next(f for f in current_frames if f['at'][:10] == '2020-12-16' and not f.get('ppaBundle'))
        contract = after['points'][0]['contractId']
        query = {'revisionPolicy': 'exact_snapshot_pins_v1', 'snapshotId': pinned['id'], 'curve': 'local_power', 'contractId': contract}
        offered=c.call('history',query)['points']
        # A close's history is its own session's (D334): closes another session records at the same instants are not its
        # samples, so one session's chart has one sample per day.
        record(c, seed_frame, 'Synthetic other-session sample')
        record(c, corrected_frame, 'Synthetic other-session correction')
        c.call('workspace/navigation')
        plain = c.call('history', query)
        check('history keeps only its own session: another session\'s closes at the same instants add no samples',
              plain['points'] == offered and len({p['at'] for p in plain['points']}) == len(plain['points']) and 'conflict' not in plain['chart'])
        malformed = c.call('history', {'revisionPolicy': 'exact_snapshot_pins_v1', **query, 'overlays': [{'code': 'simple_moving_average_v1', 'parameters': {'window': 1}}]}, expected=422)
        check('an overlay request with a bad window is an overlay_parameter refusal', malformed['code'] == 'overlay_parameter')
        c.call('profile', {'profile': 'editor'})
        # Revision policy (D237): the correction was recorded after `context['knownAt']`; the closes above record the original and the corrected frame.
        asknown = 'as_known_at_cutoff_v1'
        def context_history(ctx, **extra):
            frame = next(f for f in c.call('workspace')['frames'] if f.get('contextId') == ctx['contextId'] and f.get('originCloseId') == seed['id'])
            return c.call('history', {'revisionPolicy': asknown, 'snapshotId': frame['id'], 'curve': 'local_power', 'contractId': contract, **extra})
        def at_seed_day(history):
            return [p for p in history['points'] if p['at'][:10] == '2020-12-17']
        old_ctx = c.call('market/context', {**request, 'mode': 'at', 'knownAt': context['knownAt']})['context']
        old_view = context_history(old_ctx)
        check('under a cutoff before the correction the original is shown and the cutoff used is echoed',
              old_view['revisionPolicy'] == asknown and old_view['knowledgeCutoff'] == old_ctx['knownAt']
              and len(at_seed_day(old_view)) == 1 and Decimal(at_seed_day(old_view)[0]['value']) != Decimal('30.43') and at_seed_day(old_view)[0]['knownAt'] <= old_ctx['knownAt'])
        later = c.call('market/context', request)['context']
        new_view = context_history(later)
        check('under a cutoff after the correction the corrected value is shown, with its recorded knowledge instant',
              len(at_seed_day(new_view)) == 1 and Decimal(at_seed_day(new_view)[0]['value']) != Decimal(at_seed_day(old_view)[0]['value'])
              and new_view['knowledgeCutoff'] == later['knownAt'] and at_seed_day(new_view)[0]['knownAt'] > old_ctx['knownAt'])
        c.call('market/current', {})
        working = c.call('workspace')['frames']
        working_pin = next(f for f in working if f['at'][:10] == '2020-12-16' and not f.get('ppaBundle'))
        base = {'snapshotId': working_pin['id'], 'curve': 'local_power', 'contractId': contract}
        exact_view = c.call('history', {**base, 'revisionPolicy': 'exact_snapshot_pins_v1'})
        # One close per session day (D334): exact pins read the built-in session's one close at the seed instant.
        check('exact pins read the session\'s one close at the seed instant',
              exact_view['revisionPolicy'] == 'exact_snapshot_pins_v1' and len(at_seed_day(exact_view)) == 1)
        resolved = c.call('history', {**base, 'revisionPolicy': asknown, 'overlays': [sma]})
        check('working data under the cutoff policy shows the corrected value once and states that it used current knowledge',
              [Decimal(p['value']) for p in at_seed_day(resolved)] == [Decimal(at_seed_day(new_view)[0]['value'])]
              and resolved['knowledgeCutoff'] is None and resolved['knowledgeBasis'] == 'current_knowledge')
        resolved_means = [None if i == 0 or p['value'] is None or resolved['points'][i-1]['value'] is None
                          else (Decimal(p['value']) + Decimal(resolved['points'][i-1]['value'])) / 2 for i, p in enumerate(resolved['points'])]
        resolved_series = resolved['overlays'][0]['series']['value']
        check('under the cutoff policy the chart series is the resolved points', resolved['chart'] == {'points': chart_of(resolved['points'])})
        check('overlays run over the resolved series',
              [v['at'] for v in resolved_series] == [p['at'] for p in resolved['points']]
              and all((v['value'] is None) == (m is None) and (m is None or Decimal(v['value']) == m) for v, m in zip(resolved_series, resolved_means)))
        for bad in ({}, {'revisionPolicy': 'latest'}, {'revisionPolicy': None}):
            refused = c.call('history', {**base, **bad}, expected=422)
            check('a missing or unknown history revision policy is refused, never defaulted', refused['code'] == 'history_revision_policy')
    c.call('market/context', {**request, 'selection': None}, expected=422)
    check('mark revision selection policy is mandatory')
    after_browser.append(history_versions)
