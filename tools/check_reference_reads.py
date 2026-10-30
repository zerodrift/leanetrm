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
        'calendars': ['calendar_code', 'trading_calendar_code', 'delivery_calendar_code', 'settlement_calendar_code'],
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
    loc = references['locations'][0]
    one = c.call('reference/catalog?' + urlencode({'table': 'locations', 'code': loc['code']}))
    check('reference item read returns exactly the requested record',
          [r['code'] for r in one['rows']] == [loc['code']] and not one['hasMore'])
    check('reference item read reports an absent code as not found',
          c.call('reference/catalog?' + urlencode({'table': 'locations', 'code': 'absent_code'}), expected=404)['code'] == 'reference_item')
    check('reference item read requires a code-keyed table and no search or offset',
          all(c.call('reference/catalog?' + urlencode(query), expected=422)['code'] == 'reference_page'
              for query in [{'table': 'calendar_days', 'code': 'x'},
                            {'table': 'locations', 'code': loc['code'], 'search': 'a'},
                            {'table': 'locations', 'code': loc['code'], 'offset': 1}]))

    # A product whose contracts have dates, so the milestone equality below compares real rows. This fixture has no
    # execution policies; the execution check's fixture compares those (check_futures_execution.py).
    product = next(p for p in references['futures_products']
                   if any(m['series_code'] in {s['code'] for s in references['futures_series'] if s['product_code'] == p['code']}
                          for m in references['futures_milestones']))
    named = lambda table: {r['code']: r for r in references[table]}
    exchanges, locations, units, currencies = named('exchanges'), named('locations'), named('units'), named('currencies')
    def summary(p):
        basis = {'fixed_total': '', 'per_delivery_day': ' per delivery day', 'per_delivery_hour': ' per delivery hour'}
        lot = None if p['contract_size'] is None or p['lot_unit_code'] is None or p['lot_basis'] not in basis else (
            f"{p['contract_size']} {units[p['lot_unit_code']]['symbol']}{basis[p['lot_basis']]}")
        quote = None if p['quote_currency_code'] is None or p['quote_unit_code'] is None else (
            f"{currencies[p['quote_currency_code']]['display_code']}/{units[p['quote_unit_code']]['symbol']}")
        return [{'label': 'Exchange', 'value': exchanges[p['exchange_code']]['name']},
                {'label': 'Location', 'value': locations[p['location_code']]['name'] if p['location_code'] else None},
                {'label': 'Lot size', 'value': lot}, {'label': 'Quote unit', 'value': quote}]
    found = c.call('reference/search?' + urlencode({'q': product['name'].upper()}))
    check('reference search finds a futures product by case-insensitive name, with its one-line summary',
          {'table': 'futures_products', 'code': product['code'], 'label': product['name'],
           'productCode': product['code'], 'summary': summary(product)} in found['hits'])
    trimmed = c.call('reference/search?' + urlencode({'q': '  ' + product['name'] + ' '}))
    check('reference search trims surrounding spaces and echoes the trimmed text',
          trimmed['query'] == product['name'] and trimmed['hits'] == c.call('reference/search?' + urlencode({'q': product['name']}))['hits'])
    def matches(hit, text):
        return text.lower() in hit['code'].lower() or text.lower() in hit['label'].lower()
    calendar = c.call('reference/search?q=calendar')['hits']
    check('reference search matches item codes and labels only, never column names, identifiers or JSON text',
          all(matches(h, 'calendar') for h in calendar)
          and all(c.call('reference/search?' + urlencode({'q': q}))['hits'] == [] for q in ['"', '\\', 'null', 'exchange_code', full['definitions']['revisions'][0]['reference_revision_id']]))
    # The broad query lists at most 10 hits per category and 60 in total, and says that more exist.
    broad_text = 'e'
    expected_counts = {t: sum(1 for r in references.get(t, []) if 'code' in r and any(broad_text in str(r.get(k) or '').lower() for k in ('code', 'name', 'symbol', 'description', 'series_label')))
                       for t in SEARCH_TABLES}
    assert any(n > 10 for n in expected_counts.values()), 'The fixture needs a category with more than ten matches for the truncation check'
    broad = c.call('reference/search?q=' + broad_text)
    per_table = {t: sum(1 for h in broad['hits'] if h['table'] == t) for t in SEARCH_TABLES}
    check('reference search caps each category at 10 hits and the total at 60, and reports the truncation',
          broad['truncated'] is True and max(per_table.values()) == 10 and len(broad['hits']) <= 60
          and all(matches(h, broad_text) for h in broad['hits']))
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
    read = c.call('reference/futures-product?' + urlencode({'code': product['code']}))
    check('futures product read returns the exact product row', read['product'] == product)
    check('futures product read returns its contracts in delivery order',
          canonical(read['series']) == canonical([r for r in references['futures_series'] if r['product_code'] == product['code']])
          and [r['code'] for r in read['series']] == [r['code'] for r in sorted(
              read['series'], key=lambda r: (r['delivery_from'] is None, r['delivery_from'] or '', r['code']))])
    codes = {r['code'] for r in read['series']}
    check('futures product read returns its contracts\' dates and execution eligibility',
          read['milestones'] and canonical(read['milestones']) == canonical([r for r in references['futures_milestones'] if r['series_code'] in codes])
          and canonical(read['executionPolicies']) ==
          canonical([r for r in references['futures_execution_policies'] if r['series_code'] in codes]))
    linked = {'exchanges': ['exchange_code'], 'clearing_houses': ['clearing_house_code'],
              'commodities': ['commodity_code'], 'locations': ['location_code'], 'currencies': ['quote_currency_code'],
              'units': ['lot_unit_code', 'quote_unit_code'], 'clock_rules': ['market_clock_code'],
              'calendars': ['trading_calendar_code', 'delivery_calendar_code', 'settlement_calendar_code']}
    check('futures product read links exact-revision rows for every code the product names',
          read['references']['exchanges'][0]['code'] == product['exchange_code']
          and all(canonical(read['references'].get(table, [])) ==
                  canonical([r for r in references[table] if r['code'] in {product.get(k) for k in columns}])
                  for table, columns in linked.items()))
    check('futures product read reports an absent product as not found',
          c.call('reference/futures-product?code=absent_code', expected=404)['code'] == 'reference_item')
    check('futures product read requires a code of 1 to 160 characters',
          all(c.call('reference/futures-product' + query, expected=422)['code'] == 'reference_page'
              for query in ['', '?code=', '?' + urlencode({'code': 'x' * 161})]))
    c.call('profile', {'profile': 'admin'})
    for path in ['reference/catalog?table=currencies', 'reference/search?q=a',
                 'reference/futures-product?' + urlencode({'code': product['code']})]:
        c.call(path, expected=403)
    check('reference page, search and product read recheck current read authority')


def run_scope_checks(c, check, query, manifest):
    """Revoke only the explicitly owned fixture's reference mapping, then restore it."""
    catalog, tenant, workspace = [str(UUID(manifest[k])) for k in
                                  ['referenceCatalogId', 'tenantId', 'workspaceId']]
    scope = f"catalog_id='{catalog}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'"
    mode = query(f'SELECT access_mode FROM security.reference_catalog_scopes WHERE {scope};')
    if mode not in ('owner', 'read_only'):
        raise AssertionError('The owned reference scope is missing before the revocation check')
    c.call('profile', {'profile': 'viewer'})
    product = c.call('reference/catalog?table=futures_products')['rows'][0]['code']
    try:
        query(f'DELETE FROM security.reference_catalog_scopes WHERE {scope};')
        for path in ['reference/catalog?table=currencies', 'reference/search?q=a',
                     'reference/futures-product?' + urlencode({'code': product})]:
            c.call(path, expected=403)
        check('revoked reference scope cannot masquerade as an empty reference page, search or product')
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{catalog}','{tenant}','{workspace}','{mode}');")
    check('the restored exact reference scope is read under a fresh capability',
          bool(c.call('reference/catalog?table=currencies')['rows']))
    c.call('market/context', {'effectiveOn': '2020-12-18', 'mode': 'current', 'selection': 'latest_correction'})
    try:
        pinned = c.call('workspace/navigation')['referenceRevision']
        page = c.call('reference/catalog?table=futures_products')
        read = c.call('reference/futures-product?' + urlencode({'code': page['rows'][0]['code']}))
        check('historical futures product read uses the context\'s pinned reference revision',
              read['referenceRevision'] == pinned == page['referenceRevision']
              and read['context'] == page['context'] and read['context'] is not None)
    finally:
        c.call('market/current', {})
