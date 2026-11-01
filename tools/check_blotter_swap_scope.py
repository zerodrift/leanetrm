"""Blotter projections consume the selected swap heads, before term hydration."""
from uuid import UUID, uuid4


def run_checks(client, query, manifest, trade):
    client.call('profile', {'profile': 'trader'})
    client.call('market/current', {})
    frame = next(f for f in client.call('workspace/catalog')['frames']
                 if f['at'].startswith('2020-12-18T16:00:00'))
    context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    catalog = client.call('swaps/catalog', context)
    editor = client.call('swaps/editor', {'context': context, 'tradeId': trade, 'revision': None})
    original = {**editor['state']['terms'], 'executedAt': None, 'traderId': None, 'traderRevision': None, 'tradeDate': None}
    revision = initial_revision = editor['state']['revision']
    assert original['fixed'] is not None
    old_book = original['bookId']
    new_book = next(b['id'] for b in catalog['books'] if b['id'] != old_book)
    view = {'mode': 'trades', 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}
    shared = {**context, 'convention': 'trading_workspace_v1'}

    def report(book):
        request = {'context': shared, 'view': {**view, 'bookIds': [book]}, 'bookingCatalog': 'directory'}
        captured = client.call('trading/workspace', request)
        request['context'] = {**shared, 'knowledgeMode': 'retained', 'contextId': captured['catalog']['context']['contextId']}
        return request, client.call('trading/workspace', request)

    historical, before = report(old_book)

    def amend(terms):
        nonlocal revision
        result = client.call('swaps/amend', {**terms, **context, 'operationId': str(uuid4()), 'tradeId': trade,
                                           'expectedRevision': revision, 'effectiveAt': frame['at'],
                                           'reason': 'Synthetic projection scope check'})
        revision = result['revision']
        return query(f"SELECT i.fixed_side_id FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i "
                     f"ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) WHERE r.catalog_id='{UUID(manifest['catalogId'])}' "
                     f"AND r.trade_id='{UUID(trade)}' AND r.revision={revision};")

    try:
        obsolete = amend({**original, 'fixed': {**original['fixed'], 'price': '3.011'}})
        selected = amend({**original, 'bookId': new_book, 'fixed': {**original['fixed'], 'price': '3.022'}})
        current, expected = report(new_book)
        excluded, absent = report(old_book)
        assert any(r['tradeId'] == trade and int(r['revision']) == revision for r in expected['blotter']['holdings'])
        assert not any(r['tradeId'] == trade for r in absent['blotter']['holdings'])
        assert client.call('trading/workspace', historical) == before

        def guard(side):
            query(f"""CREATE FUNCTION public.synthetic_swap_side_scope(id uuid) RETURNS boolean
              LANGUAGE plpgsql VOLATILE AS $$BEGIN
              IF id='{UUID(side)}'::uuid THEN RAISE EXCEPTION 'Unselected synthetic swap side inspected' USING ERRCODE='42501';END IF;
              RETURN true;END$$;
              CREATE POLICY synthetic_swap_side_scope ON trading.swap_fixed_sides AS RESTRICTIVE
              FOR SELECT TO openetrm_runtime USING(public.synthetic_swap_side_scope(id));""")

        def unguard():
            query('DROP POLICY synthetic_swap_side_scope ON trading.swap_fixed_sides;'
                  'DROP FUNCTION public.synthetic_swap_side_scope(uuid);')

        guard(obsolete)
        try:
            assert client.call('trading/workspace', current) == expected
            assert client.call('trading/workspace', historical) == before
            client.call('swaps/editor', {'context': {**context, 'knowledgeMode': 'retained',
                        'contextId': current['context']['contextId']}, 'tradeId': trade, 'revision': None}, expected=403)
            print('PASS swap Blotter omits obsolete terms while the editor retains complete history', flush=True)
        finally:
            unguard()
        guard(selected)
        try:
            assert client.call('trading/workspace', excluded) == absent
            assert client.call('trading/workspace', historical) == before
            client.call('trading/workspace', current, expected=403)
            print('PASS swap Blotter selects the latest head before its book scope without resurrecting a moved trade', flush=True)
        finally:
            unguard()
        query("CREATE POLICY synthetic_swap_head_child ON trading.swap_fixed_sides AS RESTRICTIVE "
              f"FOR SELECT TO openetrm_runtime USING(id<>'{UUID(selected)}'::uuid);")
        try:
            assert client.call('trading/workspace', current, expected=422)['code'] == 'payment_rule'
        finally:
            query('DROP POLICY synthetic_swap_head_child ON trading.swap_fixed_sides;')
        instrument = next(r['instrumentId'] for r in expected['blotter']['holdings'] if r['tradeId'] == trade)
        query("CREATE POLICY synthetic_swap_head_child ON trading.swap_instruments AS RESTRICTIVE "
              f"FOR SELECT TO openetrm_runtime USING(instrument_id<>'{UUID(instrument)}'::uuid);")
        try:
            client.call('trading/workspace', current, expected=403)
            print('PASS a visible swap head with a hidden required instrument refuses rather than falling back to an older revision', flush=True)
        finally:
            query('DROP POLICY synthetic_swap_head_child ON trading.swap_instruments;')
    finally:
        if revision != initial_revision:
            amend(original)
