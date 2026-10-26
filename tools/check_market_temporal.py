"""One certified cutoff for market definitions, exact references, rates and observations."""
from decimal import Decimal
from uuid import uuid4


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
    latest = c.call('market/context', request)
    current = c.call('workspace')
    newer = next(f for f in current['frames'] if f.get('contextId') == latest['context']['contextId'] and f['at'] == seed['at'])
    after = c.call('evaluate', {'snapshotId': newer['id'], 'curve': 'local_power'})
    check('explicit latest-correction policy updates the dependent curve', after['points'][0]['value'] != before['points'][0]['value'])
    history = c.call('history', {'revisionPolicy': 'exact_snapshot_pins_v1', 'snapshotId': newer['id'], 'curve': 'local_power', 'contractId': after['points'][0]['contractId']})
    check('contract history stops at the context date', all(p['at'][:10] <= '2020-12-17' for p in history['points']))
    sma = {'code': 'simple_moving_average_v1', 'parameters': {'window': 2}}
    with_overlay = c.call('history', {'revisionPolicy': 'exact_snapshot_pins_v1', 'snapshotId': newer['id'], 'curve': 'local_power', 'contractId': after['points'][0]['contractId'], 'overlays': [sma]})
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
        # Registers durable closes at the seed's instant, which later browser suites would show as extra samples; run after them (run_preview.py).
        # Current-context history: closes of the seed frame and of the corrected frame add samples at the seed's instant.
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
        def expected_means(samples):
            distinct = list(dict.fromkeys((p['at'], p['value']) for p in samples))
            return [(at, None if i == 0 or value is None or distinct[i-1][1] is None else (Decimal(value) + Decimal(distinct[i-1][1])) / 2)
                    for i, (at, value) in enumerate(distinct)]
        c.call('market/closes/register', {'requestId': str(uuid4()), 'snapshotId': seed_frame['id'], 'name': 'Synthetic duplicate sample'})
        duplicated = c.call('history', {'revisionPolicy': 'exact_snapshot_pins_v1', **query, 'overlays': [sma]})
        instants = [p['at'] for p in duplicated['points']]
        series = duplicated['overlays'][0]['series']['value']
        check('identical samples at one instant count once: points are unchanged and the overlay is aligned by instant',
              len(instants) == len(set(instants)) + 1 and duplicated['points'] == c.call('history', query)['points']
              and [(v['at'], None if v['value'] is None else Decimal(v['value'])) for v in series] == expected_means(duplicated['points']))
        c.call('market/closes/register', {'requestId': str(uuid4()), 'snapshotId': corrected_frame['id'], 'name': 'Synthetic differing sample'})
        conflict = c.call('history', {'revisionPolicy': 'exact_snapshot_pins_v1', **query, 'overlays': [sma]}, expected=422)
        check('different samples at one instant refuse overlays by naming the instant, never choosing a frame',
              conflict['code'] == 'overlay_input' and '2020-12-17' in conflict['message'] and len(c.call('history', query)['points']) > 2)
        c.call('profile', {'profile': 'editor'})
        # Revision policy (D230): the correction was recorded after `context['knownAt']`; the closes above record the original and the corrected frame.
        asknown = 'as_known_at_cutoff_v1'
        def context_history(ctx, **extra):
            frame = next(f for f in c.call('workspace')['frames'] if f.get('contextId') == ctx['contextId'])
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
        check('exact pins keep the original and the corrected sample apart at the seed instant',
              exact_view['revisionPolicy'] == 'exact_snapshot_pins_v1' and len(at_seed_day(exact_view)) == 3)
        resolved = c.call('history', {**base, 'revisionPolicy': asknown, 'overlays': [sma]})
        check('working data under the cutoff policy shows the corrected value once and states that it used current knowledge',
              [Decimal(p['value']) for p in at_seed_day(resolved)] == [Decimal(at_seed_day(new_view)[0]['value'])]
              and resolved['knowledgeCutoff'] is None and resolved['knowledgeBasis'] == 'current_knowledge')
        resolved_means = [None if i == 0 or p['value'] is None or resolved['points'][i-1]['value'] is None
                          else (Decimal(p['value']) + Decimal(resolved['points'][i-1]['value'])) / 2 for i, p in enumerate(resolved['points'])]
        resolved_series = resolved['overlays'][0]['series']['value']
        check('overlays run over the resolved series',
              [v['at'] for v in resolved_series] == [p['at'] for p in resolved['points']]
              and all((v['value'] is None) == (m is None) and (m is None or Decimal(v['value']) == m) for v, m in zip(resolved_series, resolved_means)))
        for bad in ({}, {'revisionPolicy': 'latest'}, {'revisionPolicy': None}):
            refused = c.call('history', {**base, **bad}, expected=422)
            check('a missing or unknown history revision policy is refused, never defaulted', refused['code'] == 'history_revision_policy')
    c.call('market/context', {**request, 'selection': None}, expected=422)
    check('mark revision selection policy is mandatory')
    after_browser.append(history_versions)
