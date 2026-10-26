"""One certified cutoff for market definitions, exact references, rates and observations."""
from decimal import Decimal


def run_checks(Client, check):
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
    history = c.call('history', {'snapshotId': newer['id'], 'curve': 'local_power', 'contractId': after['points'][0]['contractId']})
    check('contract history stops at the context date', all(p['at'][:10] <= '2020-12-17' for p in history['points']))
    sma = {'code': 'simple_moving_average_v1', 'parameters': {'window': 2}}
    with_overlay = c.call('history', {'snapshotId': newer['id'], 'curve': 'local_power', 'contractId': after['points'][0]['contractId'], 'overlays': [sma]})
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
    refusal = c.call('history', {'snapshotId': newer['id'], 'curve': 'local_power', 'contractId': after['points'][0]['contractId'],
                                 'overlays': [{'code': 'simple_moving_average_v1', 'parameters': {'window': 1}}]}, expected=422)
    check('an out-of-range overlay window is a structured overlay_parameter refusal', refusal['code'] == 'overlay_parameter')
    catalog = c.call('analytics/overlays')
    check('the overlay catalog names the policy, codes and window bounds',
          catalog['policy'] == 'exact_rational_half_even_18_v1' and
          {o['code'] for o in catalog['overlays']} == {'simple_moving_average_v1', 'exponential_moving_average_v1', 'rolling_range_v1'} and
          all(o['parameters'] == [{'name': 'window', 'label': 'Window', 'minimum': 2, 'maximum': 250}] for o in catalog['overlays']))
    c.call('market/context', {**request, 'selection': None}, expected=422)
    check('mark revision selection policy is mandatory')
