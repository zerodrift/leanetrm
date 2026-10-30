"""Exact-version reference projection and bounded inspector checks."""
from urllib.parse import urlencode
from uuid import UUID

SEARCH_TABLES = {
    'futures_products', 'futures_series', 'price_index_definitions', 'exchanges', 'clearing_houses',
    'interest_rate_definitions', 'sources', 'commodities', 'units', 'currencies', 'measurement_bases',
    'quantity_specifications', 'value_specifications', 'locations', 'location_types', 'calendars',
    'clock_rules', 'quote_specifications', 'settlement_targets', 'delivery_profiles'}


def run_checks(c, check):
    c.call('profile', {'profile': 'viewer'})
    full = c.call('workspace')
    catalog = c.call('workspace/catalog')
    references = full['references']
    selected = {r['futures_series_code'] for r in full['definitions']['contracts']
                if r.get('futures_series_code') is not None}
    while True:
        expanded = selected | {r['benchmark_series_code']
                               for r in references['futures_basis_settlements']
                               if r['series_code'] in selected}
        if expanded == selected:
            break
        selected = expanded
    products = {r['product_code'] for r in references['futures_series']
                if r['code'] in selected}
    expected = {
        'futures_series': [r for r in references['futures_series'] if r['code'] in selected],
        'futures_products': [r for r in references['futures_products'] if r['code'] in products],
    }
    for table in ['futures_milestones', 'futures_lot_resolutions', 'futures_basis_settlements']:
        expected[table] = [r for r in references[table] if r['series_code'] in selected]
    for table in ['futures_product_terms', 'product_evidence']:
        expected[table] = [r for r in references[table] if r['product_code'] in products]
    def canonical(rows):
        return sorted((tuple(sorted(r.items())) for r in rows), key=repr)
    check('business references include only exact contract and benchmark futures rows',
          all(canonical(catalog['references'][table]) == canonical(rows)
              for table, rows in expected.items()))
    check('business reference projection explicitly identifies its partial scope',
          catalog['referenceScope'] == 'definition_closure' and full['referenceScope'] == 'all_visible')
    check('navigation does not read calendar exceptions needed only by financial consumers',
          catalog['references']['calendar_days'] == [])
    check('reference projection preserves exact financial definitions and unit choices',
          catalog['definitions'] == full['definitions']
          and catalog['unitCatalog'] == full['unitCatalog'])
    locations = {r.get('location_code') for r in full['definitions']['curves']
                 + expected['futures_products'] + references['price_index_definitions']}
    check('business locations follow the retained curve, product and index references',
          canonical(catalog['references']['locations']) ==
          canonical([r for r in references['locations'] if r['code'] in locations]))

    keys = {'unit_conversions': ['from_unit_code', 'to_unit_code'],
            'commodity_units': ['commodity_code', 'unit_code'],
            'calendar_days': ['calendar_code', 'business_date'],
            'commodity_locations': ['commodity_code', 'location_code'],
            'exchange_clearing_houses': ['exchange_code', 'clearing_house_code'],
            'product_evidence': ['product_code', 'source_code', 'fact_group'],
            'futures_milestones': ['series_code', 'milestone'],
            'futures_execution_policies': ['series_code', 'implementation'],
            'futures_lot_resolutions': ['series_code'],
            'futures_product_terms': ['product_code'],
            'futures_basis_settlements': ['series_code']}
    label_fields = {
        'commodities': ['commodity_code', 'fuel_commodity_code', 'output_commodity_code'],
        'currencies': ['currency_code', 'quote_currency_code'],
        'units': ['unit_code', 'lot_unit_code', 'from_unit_code', 'to_unit_code',
                  'quote_unit_code', 'fuel_unit_code', 'output_unit_code'],
        'locations': ['location_code'], 'location_types': ['type_code'],
        'exchanges': ['exchange_code'], 'clearing_houses': ['clearing_house_code'],
        'sources': ['source_code'], 'futures_products': ['product_code'],
        'futures_series': ['series_code'], 'clock_rules': ['clock_code', 'market_clock_code'],
        'calendars': ['calendar_code', 'trading_calendar_code'],
        'quantity_specifications': ['quantity_code', 'numerator_quantity_code', 'denominator_quantity_code'],
        'measurement_bases': ['basis_code'], 'price_index_definitions': ['index_a_code', 'index_b_code']}
    for table in references:
        response = c.call('reference/catalog?' + urlencode({'table': table, 'offset': 0, 'search': ''}))
        source = full['unitCatalog']['references'].get(table, references[table])
        fields = keys.get(table, ['code'])
        ordered = sorted(source, key=lambda r: tuple(r[k] for k in fields))
        check('reference page has exact bounded primary-key order: ' + table,
              response['rows'] == ordered[:200] and response['hasMore'] == (len(ordered) > 200)
              and response['offset'] == 0 and response['pageSize'] == 200
              and response['maximumOffset'] == 100000
              and response['referenceScope'] == 'selected_table'
              and response['table'] == table and set(response['tables']) == set(references))
        label_source = {**references, **full['unitCatalog']['references']} if table in full['unitCatalog']['references'] else references
        for target, columns in label_fields.items():
            wanted = {r.get(column) for r in response['rows'] for column in columns} - {None}
            if wanted:
                actual = {r['code']: r for r in response['references'].get(target, [])}
                check('reference labels have exact authorized row versions: ' + table + '/' + target,
                      all(actual.get(r['code']) == r for r in label_source[target] if r['code'] in wanted)
                      and wanted <= actual.keys())
        revision = (full['unitCatalog']['revision'] if table in full['unitCatalog']['references'] else
                    {'catalogId': full['definitions']['revisions'][0]['reference_catalog_id'],
                     'revisionId': full['definitions']['revisions'][0]['reference_revision_id']})
        check('reference page declares its exact source revision: ' + table,
              response['referenceRevision'] == revision)
        next_page = c.call('reference/catalog?' + urlencode({'table': table, 'offset': 1}))
        check('reference offset advances the exact ordered selection: ' + table,
              next_page['rows'] == ordered[1:201]
              and next_page['hasMore'] == (len(ordered) > 201)
              and next_page['offset'] == 1)
        if ordered and 'code' in fields:
            code = ordered[0]['code']
            found = c.call('reference/catalog?' + urlencode({'table': table, 'offset': 0, 'search': code}))
            check('reference search retains the exact requested record: ' + table,
                  ordered[0] in found['rows'] and found['search'] == code)
    literal = c.call('reference/catalog?' + urlencode({'table': 'currencies', 'search': '%'}))
    check('reference search treats SQL wildcard characters literally', literal['rows'] == [])
    for query in [{}, {'table': 'unknown'}, {'table': 'currencies', 'offset': -1},
                  {'table': 'currencies', 'offset': 100001},
                  {'table': 'currencies', 'offset': ''},
                  {'table': 'currencies', 'offset': '1.5'},
                  {'table': 'currencies', 'offset': 'foo'},
                  {'table': 'currencies', 'search': 'x' * 161}]:
        c.call('reference/catalog?' + urlencode(query), expected=422)
    check('reference page rejects unsupported table and unbounded requests')

    product = references['futures_products'][0]
    found = c.call('reference/search?' + urlencode({'q': product['name'].upper()}))
    check('reference search finds a futures product by case-insensitive name',
          {'table': 'futures_products', 'code': product['code'], 'label': product['name'],
           'productCode': product['code']} in found['hits'])
    series = next(r for r in references['futures_series'] if r['product_code'] == product['code'])
    hit = next(h for h in c.call('reference/search?' + urlencode({'q': series['series_label']}))['hits']
               if h['table'] == 'futures_series')
    check('reference search links a futures series to its product', hit['productCode'] == series['product_code'])
    check('reference search treats SQL wildcard characters literally across categories',
          c.call('reference/search?' + urlencode({'q': '%_'}))['hits'] == [])
    check('reference search refuses blank and unbounded text',
          all(c.call('reference/search?' + urlencode({'q': bad}), expected=422)['code'] == 'reference_search'
              for bad in ['', ' ' * 3, 'x' * 161]))
    check('reference search covers only code-keyed categories and futures series',
          all(h['table'] in SEARCH_TABLES for h in c.call('reference/search?q=a')['hits']))
    c.call('profile', {'profile': 'admin'})
    c.call('reference/catalog?table=currencies', expected=403)
    check('reference page rechecks current read authority')


def run_scope_checks(c, check, query, manifest):
    """Revoke only the explicitly owned fixture's reference mapping, then restore it."""
    catalog, tenant, workspace = [str(UUID(manifest[k])) for k in
                                  ['referenceCatalogId', 'tenantId', 'workspaceId']]
    scope = f"catalog_id='{catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'"
    mode = query(f'SELECT access_mode FROM security.reference_catalog_scopes WHERE {scope};')
    if mode not in ('owner', 'read_only'):
        raise AssertionError('The owned reference scope is missing before the revocation check')
    c.call('profile', {'profile': 'viewer'})
    c.call('reference/catalog?table=currencies')
    try:
        query(f'DELETE FROM security.reference_catalog_scopes WHERE {scope};')
        c.call('reference/catalog?table=currencies', expected=403)
        check('revoked reference scope cannot masquerade as an empty reference page')
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{catalog}','{tenant}','{workspace}','{mode}');")
    check('the restored exact reference scope is read under a fresh capability',
          bool(c.call('reference/catalog?table=currencies')['rows']))
