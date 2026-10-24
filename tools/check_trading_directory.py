"""Deferred booking prices preserve the exact authorized trading report."""
from uuid import UUID, uuid4


def check_unusable_marks(c, check, query, manifest, workspace, frame, context, view, holdings):
    """Append valid source revisions, without bypassing any immutable-data guard."""
    held = {row['contractId'] for row in holdings}
    future_ids = [row['id'] for row in workspace['definitions']['contracts'] if row['kind'] == 'future']
    unheld = next((identifier for identifier in future_ids if identifier not in held), None)
    if not held or unheld is None:
        raise AssertionError('Directory checks require both held and unheld synthetic futures')
    by_id = {row['id']: row for row in workspace['observations']}
    catalog = str(UUID(manifest['catalogId']))

    def append(predecessor, state):
        identifier = str(uuid4())
        result = query(f"""INSERT INTO market_data.observations
          (catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
           source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
          SELECT catalog_id,'{identifier}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
           source_code,source_event_id,'{identifier}',role,'{state}',observed_at,clock_timestamp(),value,id
          FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{str(UUID(predecessor))}'
          RETURNING id;""")
        if result != identifier:
            raise AssertionError('Owned synthetic source revision was not appended')
        return identifier

    for contract, is_held in [(unheld, False), (sorted(held)[0], True)]:
        series = next(row['id'] for row in workspace['definitions']['series'] if row.get('contract_id') == contract)
        original = next(by_id[identifier] for identifier in frame['observationIds']
                        if by_id[identifier]['series_id'] == series)
        head = original
        while True:
            successors = [row for row in by_id.values() if row['corrects_id'] == head['id']]
            if not successors:
                break
            if len(successors) != 1:
                raise AssertionError('Synthetic source history unexpectedly forks')
            head = successors[0]
        preliminary = append(head['id'], 'preliminary')
        try:
            selected = c.call('select-observation', {'snapshotId': frame['id'],
                                                   'observationId': preliminary})['frame']
            request = {'context': {**context, 'snapshotId': selected['id']}, 'view': view}
            if is_held:
                error = c.call('trading/workspace', {**request, 'bookingCatalog': 'directory'}, expected=422)
                check('directory still rejects an unusable selected mark required by an actual holding',
                      error['code'] == 'selection_mismatch')
            else:
                result = c.call('trading/workspace', {**request, 'bookingCatalog': 'directory'})
                check('an unused nonfinal booking mark cannot block exact held-position valuation',
                      result['blotter']['holdings'] == holdings)
                error = c.call('trading/workspace', {**request, 'bookingCatalog': 'priced_contracts'}, expected=422)
                check('explicit booking prices still require every selected booking mark to be usable',
                      error['code'] == 'selection_mismatch')
        finally:
            # Preserve valid append-only provenance for subsequent fixture checks.
            append(preliminary, 'final')


def run_checks(c, check, query, manifest):
    c.call('profile', {'profile': 'trader'})
    c.call('market/current', {})
    workspace = c.call('workspace')
    frame = next(row for row in workspace['frames'] if row['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1',
               'knowledgeMode': 'current'}
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
            'measures': [], 'pivotConvention': None}
    legacy = c.call('trading/workspace', {'context': context, 'view': view})
    directory = c.call('trading/workspace', {'context': context, 'view': view,
                                           'bookingCatalog': 'directory'})
    check('directory mode explicitly omits booking contract prices while retaining report rows',
          directory['catalog']['kind'] == 'directory' and 'contracts' not in directory['catalog']
          and bool(directory['blotter']['holdings']))
    check('directory and report retain one shared financial context and current booking authority',
          directory['catalog']['context'] == directory['blotter']['context']
          and directory['catalog']['asOf'] == directory['blotter']['asOf'] == frame['at']
          and directory['catalog']['canBook'])

    retained = {**context, 'knowledgeMode': 'retained',
                'contextId': directory['catalog']['context']['contextId']}
    request = {'context': retained, 'view': view}
    full = c.call('trading/workspace', {**request, 'bookingCatalog': 'priced_contracts'})
    default = c.call('trading/workspace', request)
    light = c.call('trading/workspace', {**request, 'bookingCatalog': 'directory'})
    check('omitted booking mode preserves the complete legacy priced catalog contract',
          full == default and full['catalog']['kind'] == 'priced_contracts'
          and len(full['catalog']['contracts']) == len(legacy['catalog']['contracts']))
    check('deferred catalog pricing leaves every actual retained holding and exact amount unchanged',
          full['blotter'] == light['blotter'])
    common = lambda catalog: {key: value for key, value in catalog.items()
                              if key not in ('kind', 'contracts')}
    check('directory and priced catalogs preserve identical metadata under the same retained context',
          common(full['catalog']) == common(light['catalog']))
    for mode in ['', 'unknown', 'Directory', 'priced']:
        error = c.call('trading/workspace', {**request, 'bookingCatalog': mode}, expected=422)
        check('unsupported booking catalog mode fails explicitly: ' + repr(mode),
              error['code'] == 'blotter_view')

    selected = c.call('market/context', {'effectiveOn': '2020-12-18',
                      'knownAt': directory['catalog']['context']['knownAt'],
                      'mode': 'at', 'selection': 'latest_correction'})['context']
    historical_frame = next(row for row in c.call('workspace/catalog')['frames']
                            if row.get('contextId') == selected['contextId'] and row['at'] == frame['at'])
    historical = {**retained, 'snapshotId': historical_frame['id'], 'contextId': selected['contextId']}
    history = c.call('trading/workspace', {'context': historical, 'view': view,
                                         'bookingCatalog': 'directory'})
    check('directory mode keeps selected historical contexts read only',
          history['catalog']['context'] == selected == history['blotter']['context']
          and not history['catalog']['canBook'] and 'contracts' not in history['catalog'])
    c.call('market/current', {})
    check_unusable_marks(c, check, query, manifest, workspace, frame, context, view,
                         directory['blotter']['holdings'])

    # Current scope is checked afresh even when the request reuses a retained context.
    catalog, tenant, scope_workspace = [str(UUID(manifest[key])) for key in
                                       ['referenceCatalogId', 'tenantId', 'workspaceId']]
    scope = f"catalog_id='{catalog}' AND tenant_id='{tenant}' AND workspace_id='{scope_workspace}'"
    access = query(f'SELECT access_mode FROM security.reference_catalog_scopes WHERE {scope};')
    if access not in ('owner', 'read_only'):
        raise AssertionError('Owned reference scope missing before directory revocation check')
    try:
        query(f'DELETE FROM security.reference_catalog_scopes WHERE {scope};')
        for mode in ['directory', 'priced_contracts']:
            error = c.call('trading/workspace', {**request, 'bookingCatalog': mode}, expected=422)
            check('a retained trading read cannot use exact references outside current scope: ' + mode,
                  error['code'] == 'ambiguous_reference' and set(error) == {'code', 'message'})
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{catalog}','{tenant}','{scope_workspace}','{access}');")
    for profile in ['viewer', 'admin']:
        c.call('profile', {'profile': profile})
        c.call('trading/workspace', {**request, 'bookingCatalog': 'directory'}, expected=403)
        c.call('trading/workspace', {**request, 'bookingCatalog': 'priced_contracts'}, expected=403)
        check('booking catalog mode cannot confer missing current trading permissions: ' + profile)
    c.call('profile', {'profile': 'viewer'})
