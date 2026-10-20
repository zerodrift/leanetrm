"""One certified cutoff for market definitions, exact references, rates and observations."""


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
    c.call('market/context', {**request, 'selection': None}, expected=422)
    check('mark revision selection policy is mandatory')
