"""Small market navigation stays distinct from executable workspace data."""
from uuid import UUID


def run_checks(c, check, query, manifest):
    c.call('profile', {'profile': 'viewer'})
    original = query("SELECT pg_get_functiondef('automation.pilot_context_live(uuid)'::regprocedure);")
    try:
        query("""CREATE OR REPLACE FUNCTION automation.pilot_context_live(token uuid) RETURNS boolean
        LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
        BEGIN RAISE EXCEPTION 'An ordinary request evaluated a pilot-only authority graph';END;$$;""")
        navigation = c.call('workspace/navigation')
        check('ordinary navigation does not evaluate a registered-worker authority graph')
    finally:
        query(original)
    full = c.call('workspace/catalog')

    def compare(nav, workspace):
        definitions, references = workspace['definitions'], workspace['references']
        contracts = {row['id']: row for row in definitions['contracts']}
        commodities = {row['code']: row['name'] for row in references['commodities']}
        expected = []
        for curve in definitions['curves']:
            kinds = {contracts[row['contract_id']]['kind'] for row in definitions['curve_members']
                     if row['curve_code'] == curve['code']}
            if not kinds or not kinds <= {'future', 'forward'}:
                raise AssertionError('Fixture curve has no explicit supported classification')
            kind = 'mixed' if len(kinds) == 2 else {'future': 'futures', 'forward': 'forwards'}[next(iter(kinds))]
            expected.append({'code': curve['code'], 'name': curve['name'], 'marketType': kind,
                             'category': {'code': curve['commodity_code'], 'name': commodities[curve['commodity_code']]}})
        expected += [{'code': curve['code'], 'name': curve['name'], 'marketType': 'rates',
                      'category': {'code': 'rates', 'name': 'Interest rates'}}
                     for curve in definitions['rate_curves']]
        revision = definitions['revisions'][0]
        return (sorted(nav['curves'], key=lambda r: r['code']) == sorted(expected, key=lambda r: r['code'])
                and nav['definitionRevision'] == {'catalogId': revision['catalog_id'], 'revisionId': revision['id']}
                and nav['referenceRevision'] == {'catalogId': revision['reference_catalog_id'], 'revisionId': revision['reference_revision_id']}
                and nav['frames'] == [{**{key: frame[key] for key in ['id', 'name', 'at']},'originCloseId':frame.get('originCloseId')} for frame in workspace['frames']]
                and all(nav[key] == workspace[key] for key in ['profile', 'context', 'example', 'futuresConvention', 'initialFrameIds','referenceDisplay','frameCatalog'])
                and nav['curveNames'] == c.call('curve-names'))

    check('navigation explicitly contains only its directory and frame-summary contract',
          set(navigation) == {'kind', 'profile', 'context', 'definitionRevision', 'referenceRevision',
                              'example', 'futuresConvention', 'initialFrameIds', 'frames', 'curveNames', 'curves','referenceDisplay','frameCatalog'}
          and navigation['kind'] == 'market_navigation'
          and all(set(frame) == {'id', 'name', 'at','originCloseId'} and frame['originCloseId'] is None for frame in navigation['frames']))
    check('ordinary navigation declares its bounded exact stored-close discovery policy',
          navigation['frameCatalog']=={'durableSelection':'exact_recorded_close_pins_v1',
                                      'selectionPolicy':'preserved_session_plus_latest_visible_closes_v1',
                                      'maximumFrames':128,'durableLimit':128-len(navigation['frames']),
                                      'hasMoreDurableCloses':False})
    check('working navigation supplies only its exact unapplied display-metadata identity',
          navigation['referenceDisplay'] == {'sourceRevision':navigation['referenceRevision'],
                                             'displayRevision':navigation['referenceRevision'],
                                             'version':0,'references':{}})
    check('navigation classifications, names, exact revisions and frames match the authorized workspace', compare(navigation, full))
    check('navigation is independent of the much larger executable workspace payload',
          len(str(navigation)) * 5 < len(str(full)))
    hidden = str(UUID(full['definitions']['curve_members'][0]['contract_id']))
    try:
        query("CREATE POLICY navigation_hidden_contract ON market_data.contracts AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(id<>'" + hidden + "');")
        rejected = c.call('workspace/navigation', expected=422)
        check('an RLS-hidden member cannot become a partial or guessed market classification', rejected['code'] == 'market_navigation')
    finally:
        query('DROP POLICY IF EXISTS navigation_hidden_contract ON market_data.contracts;')
    context = c.call('market/context', {'effectiveOn': '2020-12-17', 'mode': 'current',
                                       'selection': 'latest_correction'})['context']
    retained = c.call('workspace/navigation')
    check('navigation preserves exact retained effective and knowledge coordinates',
          retained['context'] == context and retained['referenceDisplay'] is None
          and compare(retained, c.call('workspace/catalog')))
    c.call('market/current', {})
    c.call('profile', {'profile': 'admin'})
    c.call('workspace/navigation', expected=403)
    check('administration permission cannot authorize a market directory')
    c.call('profile', {'profile': 'viewer'})
    catalog, tenant, workspace = [str(UUID(manifest[key])) for key in
                                  ['referenceCatalogId', 'tenantId', 'workspaceId']]
    scope = f"catalog_id='{catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'"
    mode = query(f'SELECT access_mode FROM security.reference_catalog_scopes WHERE {scope};')
    if mode not in ('owner', 'read_only'):
        raise AssertionError('Owned reference scope is missing before navigation revocation check')
    try:
        query(f'DELETE FROM security.reference_catalog_scopes WHERE {scope};')
        c.call('workspace/navigation', expected=422)
        check('current reference revocation cannot return a stale or empty successful directory')
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{catalog}','{tenant}','{workspace}','{mode}');")
    check('restored reference authority is read afresh', compare(c.call('workspace/navigation'), full))
