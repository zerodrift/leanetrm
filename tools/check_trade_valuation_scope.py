"""Priced trading consumes required milestones without inspecting unrelated metadata."""
from preview_client import initial_frame


def run_checks(c, check, query):
    c.call('profile', {'profile': 'trader'})
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
            'measures': [], 'pivotConvention': None}
    for on in [None, '2020-12-17']:
        selected = (c.call('market/context', {'effectiveOn': on, 'mode': 'current',
                    'selection': 'latest_correction'})['context'] if on else None)
        metadata = c.call('workspace/catalog')
        frame = initial_frame(metadata, on or '2020-12-18')
        check('the trading scope fixture includes unrelated milestone metadata',
              any(m['milestone'] not in ('first_trading', 'last_trading')
                  for m in metadata['references']['futures_milestones']))
        context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1',
                   'knowledgeMode': 'retained' if selected else 'current'}
        if selected:
            context['contextId'] = selected['contextId']
        captured = c.call('trading/workspace', {'context': context, 'view': view})
        context.update(knowledgeMode='retained', contextId=captured['catalog']['context']['contextId'])
        request = {'context': context, 'view': view, 'bookingCatalog': 'priced_contracts'}
        expected = c.call('trading/workspace', request)
        query("""CREATE FUNCTION security.synthetic_trade_milestone_guard(kind text) RETURNS boolean
          LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
          BEGIN
            IF kind NOT IN ('first_trading','last_trading') THEN
              RAISE EXCEPTION 'Unrelated synthetic milestone metadata was inspected' USING ERRCODE='42501';
            END IF;
            RETURN true;
          END $$;
          CREATE POLICY synthetic_trade_milestone_guard ON ref_data.futures_milestones
            AS RESTRICTIVE FOR SELECT TO openetrm_runtime
            USING(security.synthetic_trade_milestone_guard(milestone));""")
        try:
            actual = c.call('trading/workspace', request)
            check('priced trading preserves its complete catalog and report without unrelated milestone reads',
                  actual == expected and c.call('trading/workspace', {'context': context, 'view': view}) == expected)
            c.call('workspace/catalog', expected=403)
            c.call('reference/catalog?table=futures_milestones', expected=403)
            c.call('futures/catalog', {**context, 'convention': metadata['futuresConvention']}, expected=403)
            c.call('evaluate', {'snapshotId': frame['id'], 'curve': 'future', 'includeWorkspace': True}, expected=403)
            check('complete metadata and generic financial reads retain their full milestone scope')
        finally:
            query('DROP POLICY synthetic_trade_milestone_guard ON ref_data.futures_milestones;'
                  'DROP FUNCTION security.synthetic_trade_milestone_guard(text);')
        check('full workspace metadata remains exact after the scoped trading read',
              c.call('workspace/catalog') == metadata)
        for required in ['first_trading', 'last_trading']:
            query("CREATE POLICY synthetic_required_milestone_guard ON ref_data.futures_milestones "
                  "AS RESTRICTIVE FOR SELECT TO openetrm_runtime "
                  f"USING(NOT(series_code='test_gas_202101' AND milestone='{required}'));")
            try:
                error = c.call('trading/workspace', request, expected=422)
                check('priced trading still requires its exact visible ' + required + ' fact',
                      error['code'] == 'ambiguous_reference')
            finally:
                query('DROP POLICY synthetic_required_milestone_guard ON ref_data.futures_milestones;')
        check('required milestone checks do not replace the retained context or financial result',
              c.call('trading/workspace', request) == expected)
    c.call('profile', {'profile': 'admin'})
    c.call('trading/workspace', request, expected=403)
    check('the scoped read cannot substitute administrative rights for trading authority')
    c.call('profile', {'profile': 'viewer'})
