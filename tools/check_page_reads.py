"""Bounded page-read checks for an owned synthetic preview."""
from check_direct_reads import PROFILE, page_view, trade_view
from preview_client import initial_frame

HISTORICAL = {'effectiveOn': '2020-12-17', 'mode': 'current', 'selection': 'latest_correction'}


def run_checks(c, check, direct_reads):
    c.call('profile', {'profile': PROFILE})
    views = [page_view(c, 'working'),
             page_view(c, 'historical', c.call('market/context', HISTORICAL)['context'])]
    check('a historical view refuses current-only TOU products and power hours',
          views[1]['touProducts'] == views[1]['powerCatalog'] == 'power_historical')
    direct_reads(views, trade_view(c))
    c.call('profile', {'profile': 'viewer'})
    catalog = c.call('workspace/catalog')
    full = c.call('workspace')
    check('navigation carries the exact authorized cross-bundle curve-name directory',
          catalog.get('curveNames') == c.call('curve-names'))
    context = c.call('market/context', HISTORICAL)['context']
    historical = c.call('workspace/catalog')
    check('navigation directory preserves the selected effective and knowledge context',
          historical.get('curveNames') == c.call('curve-names')
          and historical['context'] == context)
    c.call('market/current', {})
    check('navigation catalog omits observation history explicitly',
          catalog['observationScope'] == 'on_demand' and catalog['observations'] == [])
    check('navigation preserves exact definitions and input frames',
          all(catalog[k] == full[k] for k in ['definitions', 'frames', 'initialFrameIds']))
    unit_tables = {'dimensions', 'units', 'unit_conversions', 'commodities',
                   'measurement_bases', 'quantity_specifications'}
    check('unit conversion catalog contains only its consumed reference tables',
          set(catalog['unitCatalog']['references']) == unit_tables)

    frame = initial_frame(full, '2020-12-18')
    by_id = {o['id']: o for o in full['observations']}
    fact_fields = ('series_id', 'source_code', 'source_event_id', 'observed_at', 'role')

    def fact(row):
        return tuple(row[k] for k in fact_fields)

    facts = {fact(by_id[i]) for i in frame['observationIds']}
    expected = {o['id'] for o in full['observations'] if fact(o) in facts}
    check('read fixture distinguishes selected facts from unrelated history',
          expected and len(expected) < len(full['observations']))
    for curve in ['local_power', 'interest_rates']:
        body = {'snapshotId': frame['id'], 'curve': curve}
        ordinary = c.call('evaluate', body)
        sources = c.call('evaluate', {**body, 'includeSources': True})
        observations = sources.pop('observations')
        check('price sources have the explicit selected-frame scope: ' + curve,
              sources.pop('observationScope') == 'selected_frame')
        check('source loading preserves exact prices and provenance: ' + curve,
              sources == ordinary)
        ids = [o['id'] for o in observations]
        check('price sources contain all same-fact versions without unrelated rows: ' + curve,
              set(ids) == expected and len(ids) == len(set(ids))
              and all(by_id[o['id']] == o for o in observations))
        check('every selected price lineage has its authorized source: ' + curve,
              all(set(p['lineage']) <= set(ids) for p in sources['points']))

    # A different context must not retain the preceding response's rows.
    earlier = initial_frame(full, min(f['at'][:10] for f in full['frames'] if f['id'] in full['initialFrameIds']))
    other = c.call('evaluate', {'snapshotId': earlier['id'], 'curve': 'local_power',
                              'includeSources': True})
    earlier_facts = {fact(by_id[i]) for i in earlier['observationIds']}
    check('price sources are replaced for the requested historical input frame',
          {o['id'] for o in other['observations']} ==
          {o['id'] for o in full['observations'] if fact(o) in earlier_facts})
    c.call('profile', {'profile': 'trader'})
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1',
               'knowledgeMode': 'current'}
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
            'measures': [], 'pivotConvention': None}
    combined = c.call('trading/workspace', {'context': context, 'view': view})
    check('one workspace read binds its catalog and blotter to the same context and As of',
          combined['catalog']['context'] == combined['blotter']['context']
          and combined['catalog']['asOf'] == combined['blotter']['asOf'] == frame['at'])
    check('working workspace preserves current booking authority', combined['catalog']['canBook'])
    retained = {**context, 'knowledgeMode': 'retained',
                'contextId': combined['catalog']['context']['contextId']}
    repeated = c.call('trading/workspace', {'context': retained, 'view': view})
    old_catalog = c.call('futures/catalog', {**retained, 'convention': full['futuresConvention']})
    old_report = c.call('trading/blotter', {'context': retained, 'view': view})
    check('combined workspace preserves the exact retained catalog and report contracts',
          repeated == {'catalog': old_catalog, 'blotter': old_report})
    pivot = {**view, 'mode': 'pivot', 'groupBy': ['book', 'market', 'contract'],
             'measures': ['quantity'], 'pivotConvention': 'trading_blotter_totals_v1'}
    pivoted = c.call('trading/workspace', {'context': retained, 'view': pivot})
    check('combined workspace preserves explicit managed pivot scopes',
          pivoted['blotter'] == c.call('trading/blotter', {'context': retained, 'view': pivot}))
    c.call('trading/workspace', {'context': {**context, 'convention': 'unknown'}, 'view': view}, expected=422)
    c.call('profile', {'profile': 'viewer'})
    c.call('trading/workspace', {'context': retained, 'view': view}, expected=403)
    check('combined workspace rejects unsupported conventions and missing current trading authority')
    c.call('profile', {'profile': 'admin'})
    c.call('workspace/catalog', expected=403)
    c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'local_power',
                        'includeSources': True}, expected=403)
    check('navigation and on-demand sources recheck current business authority')
