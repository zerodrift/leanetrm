"""First pricing may explicitly hydrate the complete catalog in its bound read."""


def run_checks(c, check):
    c.call('profile', {'profile': 'viewer'})
    workspace = c.call('workspace/catalog')

    def compare(full):
        frame = next(f for f in full['frames'] if f['at'].startswith('2020-12-17'))
        body = {'snapshotId': frame['id'], 'curve': 'local_power'}
        ordinary = c.call('evaluate', body)
        sources = c.call('evaluate', {**body, 'includeSources': True})
        combined = c.call('evaluate', {**body, 'includeSources': True, 'includeWorkspace': True})
        check('combined first pricing explicitly carries a complete workspace', 'workspace' in combined)
        metadata = combined.pop('workspace')
        check('combined catalog preserves every legacy catalog property and exact frame pin', metadata == full)
        check('combined pricing preserves every exact source, result and temporal field', combined == sources)
        check('metadata observation history stays explicitly empty and separate from selected sources',
              metadata['observationScope'] == 'on_demand' and metadata['observations'] == []
              and combined['observationScope'] == 'selected_frame' and combined['observations'])
        without_sources = c.call('evaluate', {**body, 'includeWorkspace': True})
        check('workspace inclusion is independent of explicit source inclusion',
              without_sources.pop('workspace') == full and without_sources == ordinary)
        check('ordinary and repeated evaluation omit the workspace projection by default',
              'workspace' not in ordinary and c.call('evaluate', body) == ordinary
              and c.call('evaluate', {**body, 'includeSources': True, 'includeWorkspace': False}) == sources)
        return frame

    frame = compare(workspace)
    context = c.call('market/context', {'effectiveOn': '2020-12-17', 'mode': 'current',
                                        'selection': 'latest_correction'})['context']
    retained = c.call('workspace/catalog')
    compare(retained)
    check('combined workspace and results retain the exact selected temporal context', retained['context'] == context)
    c.call('market/current', {})
    c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'local_power', 'includeWorkspace': 'true'}, expected=400)
    c.call('profile', {'profile': 'admin'})
    c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'local_power', 'includeWorkspace': True}, expected=403)
    check('combined first pricing cannot substitute administrative authority for market access')
    c.call('profile', {'profile': 'viewer'})
