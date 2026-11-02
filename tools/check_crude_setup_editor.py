"""Delivery binding authoring against an owned preview, with original market pins."""
from uuid import uuid4
from decimal import Decimal
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest):
    catalog_id, tenant, workspace = (manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    def issue(profile='viewer'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    def runtime(sql, context=None):
        bind = f"SELECT security.use_market_context('{catalog_id}','{context}');" if context else ''
        return f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issue()}');" + bind + sql + 'COMMIT;'
    assert query("SELECT NOT has_function_privilege('preview_runtime','security.capture_pre_crude_setup_context(uuid,date,timestamptz)','EXECUTE') AND NOT has_function_privilege('openetrm_risk_worker','security.capture_pre_crude_setup_context(uuid,date,timestamptz)','EXECUTE');") == 't'
    original_prices = query("SELECT count(*)||'/'||md5(string_agg(row_to_json(o)::text,',' ORDER BY id)) FROM market_data.observations o;")
    before = query(runtime(f"SELECT security.capture_date_context('{catalog_id}','2020-12-18',NULL);"))
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'editor'})
    catalog = c.call('crude-setup/catalog')
    delivery = next(p for p in catalog['deliveryPoints'] if p['grade_code'] == 'synthetic_delivery_grade')
    assert delivery['revision_id'] == manifest['referenceCatalog']['referenceRevisionId']
    source = next(s for s in catalog['sources'] if s['curve'] == 'synthetic_crude_forward')
    assert delivery['revision_id'] != source['referenceRevisionId']
    config = {'code': 'synthetic_crude_binding', 'deliveryReferenceCatalogId': delivery['catalog_id'],
              'deliveryReferenceRevisionId': delivery['revision_id'], 'commodity': delivery['commodity_code'],
              'grade': delivery['grade_code'], 'facility': delivery['facility_code'], 'custodyPoint': delivery['custody_point_code'],
              'clock': 'test_utc', 'definitionRevisionId': source['definitionRevisionId'], 'curve': source['curve'],
              'validFrom': '2021-01-01', 'validTo': '2021-02-01', 'quantityPolicy': 'same_standard_volume_at_60f_v1'}
    body = {'operationId': str(uuid4()), 'baseSetupId': None, 'configuration': config}
    saved = c.call('crude-setup/save', body)
    assert c.call('crude-setup/save', body) == saved
    assert c.call('crude-setup/save', {**body, 'operationId': str(uuid4())})['setupId'] == saved['setupId'], 'Equivalent facts must reuse one version'
    equivalent = {**config, 'validFrom': '2021-1-1', 'validTo': '2021-2-1'}
    assert c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'configuration': equivalent})['setupId'] == saved['setupId']
    c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'configuration': {**config, 'validFrom': '01/02/2021'}}, expected=422)
    c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'configuration': {**config, 'validFrom': '2021-99-01'}}, expected=422)
    missing_month = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'configuration': {**config, 'code': 'synthetic_missing_month', 'validTo': '2021-03-01'}})
    c.call('crude-setup/validate', {'setupId': missing_month['setupId']}, expected=422)
    invalid = {**body, 'configuration': {**config, 'curve': 'missing'}}
    c.call('crude-setup/save', invalid, expected=409)
    result = c.call('crude-setup/validate', {'setupId': saved['setupId']})
    assert result['valid'] and result['pricingQuantity']['specification']['revision']['revisionId'] == source['referenceRevisionId']
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': saved['setupId']}, expected=403)
    c.call('profile', {'profile': 'approver'})
    command = {'operationId': str(uuid4()), 'setupId': saved['setupId']}
    published = c.call('crude-setup/publish', command)
    assert c.call('crude-setup/publish', command) == published
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': missing_month['setupId']}, expected=422)
    # A draft based on a superseded head can be resaved under the new explicit base.
    c.call('profile', {'profile': 'editor'})
    b_config = {**config, 'validFrom': '2021-01-02'}
    b = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'baseSetupId': saved['setupId'], 'configuration': b_config})
    other = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'baseSetupId': saved['setupId'], 'configuration': {**config, 'validFrom': '2021-01-03'}})
    c.call('profile', {'profile': 'approver'})
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': other['setupId']})
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': b['setupId']}, expected=409)
    c.call('profile', {'profile': 'editor'})
    rebased = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'baseSetupId': other['setupId'], 'configuration': b_config})
    assert rebased['setupId'] != b['setupId']
    c.call('profile', {'profile': 'approver'})
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': rebased['setupId']})
    # Returning to earlier terms from a later explicit base is a new revision.
    c.call('profile', {'profile': 'editor'})
    reverted = c.call('crude-setup/save', {**body, 'operationId': str(uuid4()), 'baseSetupId': rebased['setupId']})
    assert reverted['state'] == 'draft' and reverted['setupId'] != saved['setupId']
    c.call('profile', {'profile': 'approver'})
    c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': reverted['setupId']})
    # An earlier head remains authorized in its original reference catalog while
    # a newer head moves to the maintained delivery catalog.
    original = {**config, 'code': 'synthetic_head_visibility', 'deliveryReferenceCatalogId': source['referenceCatalogId'],
        'deliveryReferenceRevisionId': source['referenceRevisionId'], 'grade': 'synthetic_grade_a'}
    c.call('profile', {'profile': 'editor'})
    original_head = c.call('crude-setup/save', {'operationId': str(uuid4()), 'baseSetupId': None, 'configuration': original})
    c.call('profile', {'profile': 'approver'}); c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': original_head['setupId']})
    c.call('profile', {'profile': 'editor'})
    maintained_head = c.call('crude-setup/save', {'operationId': str(uuid4()), 'baseSetupId': original_head['setupId'],
        'configuration': {**config, 'code': 'synthetic_head_visibility'}})
    c.call('profile', {'profile': 'approver'}); c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': maintained_head['setupId']})
    c.call('profile', {'profile': 'trader'})
    frame = initial_frame(c.call('workspace'), '2020-12-18')
    quoted = c.call('crude-setup/preview', {'snapshotId': frame['id'], 'setupId': saved['setupId'], 'knowledgeMode': 'current',
                      'deliveryFrom': '2021-01-01T00:00:00Z', 'deliveryTo': '2021-02-01T00:00:00Z', 'quantity': '1000'})
    assert Decimal(quoted['presentValue']) == 80000
    assert quoted['deliveryQuantity']['revision']['revisionId'] == delivery['revision_id']
    assert quoted['pricingQuantity']['revision']['revisionId'] == source['referenceRevisionId']
    c.call('crude-setup/preview', {'snapshotId': frame['id'], 'setupId': saved['setupId'], 'knowledgeMode': 'current',
        'deliveryFrom': '2020-12-18T00:00:00Z', 'deliveryTo': '2021-01-01T00:00:00Z', 'quantity': '1000'}, expected=422)
    assert quoted['curve'] == source['curve'] and quoted['definitionRevisionId'] == source['definitionRevisionId']
    for context, expected in ((before,'0'),(quoted['contextId'],'1')):
        assert query(runtime(f"SELECT count(*) FROM market_data.crude_setup_versions WHERE(catalog_id,id)=('{catalog_id}','{saved['setupId']}');",context)) == expected
    for reference in (delivery['catalog_id'], source['referenceCatalogId']):
        query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{tenant}','{workspace}');")
        try:
            c.call('profile', {'profile': 'trader'})
            c.call('crude/catalog', {'snapshotId':frame['id'],'convention':'physical_crude_fixed_volume_v1','knowledgeMode':'current'}, expected=403)
            if reference == delivery['catalog_id']:
                explicit = c.call('crude-setup/preview', {'snapshotId':frame['id'],'setupId':original_head['setupId'],'knowledgeMode':'current',
                    'deliveryFrom':'2021-01-01T00:00:00Z','deliveryTo':'2021-02-01T00:00:00Z','quantity':'1000'})
                assert Decimal(explicit['presentValue']) == 80000
            c.call('profile', {'profile': 'editor'}); c.call('crude-setup/save', body, expected=403)
            c.call('profile', {'profile': 'approver'}); c.call('crude-setup/publish', command, expected=403)
            assert query(runtime(f"SELECT count(*) FROM market_data.crude_setup_versions WHERE(catalog_id,id)=('{catalog_id}','{saved['setupId']}');",quoted['contextId'])) == '0'
        finally:
            query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','owner');")
    assert query("SELECT count(*)||'/'||md5(string_agg(row_to_json(o)::text,',' ORDER BY id)) FROM market_data.observations o;") == original_prices
    c.call('profile', {'profile': 'viewer'})
    c.call('crude-setup/save', body, expected=403)
    print('PASS crude setup: immutable exact delivery/pricing bindings, semantic replay, role separation and original market valuation', flush=True)


def run_booking_checks(url, query, manifest, *, run_worker):
    from preview_fixture import trade_booking
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'trader'})
    frame = initial_frame(c.call('workspace'), '2020-12-18')
    context = {'snapshotId': frame['id'], 'convention': 'physical_crude_fixed_volume_v1', 'knowledgeMode': 'current'}
    catalog = c.call('crude/catalog', context)
    assignment = next(a for a in catalog['assignments'] if a['crudeSetupId'] and a['grade'] == 'synthetic_delivery_grade')
    body = {**context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': catalog['books'][0]['id'],
            'definitionRevisionId': assignment['definitionRevisionId'], 'assignmentCode': assignment['code'], 'crudeSetupId': assignment['crudeSetupId'],
            'profile': catalog['profiles'][0]['code'], 'quantityBasis': 'standard_volume_total', 'tolerance': '0', 'discounting': False,
            'deliveryFrom': '2021-01-10T00:00:00Z', 'deliveryTo': '2021-01-11T00:00:00Z', 'paymentAt': '2021-02-01T00:00:00Z',
            'quantity': '1000', 'direction': 'receive', 'considerationDirection': 'pay', 'fixedPrice': '78', 'handlingFee': None, 'handlingFeeDirection': None}
    before = c.call('crude/preview', body)
    assert Decimal(before['presentValue']) == 2000
    c.call('crude/book', {**body, 'crudeSetupId': None}, expected=422)
    booked = c.call('crude/book', body)
    assert c.call('crude/book', body) == booked
    reopened = c.call('crude/editor', {'context': context, 'tradeId': booked['tradeId']})
    assert reopened['terms']['crudeSetupId'] == assignment['crudeSetupId'] and Decimal(reopened['valuation']['presentValue']) == 2000
    shared = {**context, 'convention': 'trading_workspace_v1'}
    positions = c.call('trading/positions', {'context': shared, 'bookIds': [], 'portfolioIds': [], 'offset': 0, 'limit': 64})
    position = next(p for p in positions['positions'] if p['tradeId'] == booked['tradeId'])
    assert position['instrumentType'] == 'physical_crude'
    blotter = c.call('trading/blotter', {'context': shared, 'view': {'mode': 'trades', 'bookIds': [body['bookId']], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}})
    assert next(p for p in blotter['holdings'] if p['tradeId'] == booked['tradeId'])['grade'] == 'Synthetic delivery grade'
    assert query(f"SELECT s.economic_identity::jsonb->>'contract' FROM trading.crude_memberships m JOIN trading.sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id) WHERE m.catalog_id='{manifest['catalogId']}' AND m.instrument_id='{booked['instrumentId']}' AND m.slot=0;") == 'physical_crude_side_v2'
    # Native and queued risk must use one original factor for the old and mapped holdings.
    from fractions import Fraction
    retained = {**shared, 'knowledgeMode': 'retained', 'contextId': blotter['context']['contextId']}
    c.call('profile', {'profile': 'risk'})
    risk = c.call('portfolio/risk/catalog', {'context': retained, 'bookIds': [body['bookId']], 'portfolioIds': []})
    factors = [f for f in risk['factors'] if assignment['curve'] in f['curves']]
    assert len(factors) == 1
    request = {'context': retained, 'bookIds': [body['bookId']], 'portfolioIds': [],
        'bumps': [{'factor': factors[0]['factor'], 'size': '1', 'convention': risk['bumpConvention']}],
        'convention': risk['convention'], 'numericalPolicy': risk['numericalPolicy'], 'pricingPolicy': risk['pricingPolicy'], 'stress': []}
    report = c.call('portfolio/risk', request)
    held = next(p for p in report['factors'][0]['positions'] if p['tradeId'] == booked['tradeId'])
    assert {k: Fraction(held['values'][k]) for k in ('base','up','down','delta','gamma')} == {'base':2000,'up':3000,'down':1000,'delta':1000,'gamma':0}
    selected_request = {**request, 'tradeIds': [booked['tradeId']]}
    selected_report = c.call('portfolio/risk', selected_request)
    assert {p['tradeId'] for factor in selected_report['factors'] for p in factor['positions']} == {booked['tradeId']}
    job = c.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': selected_request})
    run_worker(); completed = c.call('portfolio/risk/jobs/' + job['id'])
    assert completed['state'] == 'completed', completed
    assert completed['result']['factors'] == selected_report['factors']
    # A later setup publication cannot change retained economics or program identity.
    c.call('profile', {'profile': 'editor'})
    saved = next(v for v in c.call('crude-setup/catalog')['versions'] if v['id'] == assignment['crudeSetupId'])
    change = {**saved['configuration'], 'validFrom': '2021-01-04'}
    draft = c.call('crude-setup/save', {'operationId': str(uuid4()), 'baseSetupId': assignment['crudeSetupId'], 'configuration': change})
    c.call('profile', {'profile': 'approver'}); c.call('crude-setup/publish', {'operationId': str(uuid4()), 'setupId': draft['setupId']})
    c.call('profile', {'profile': 'trader'})
    after = c.call('crude/editor', {'context': context, 'tradeId': booked['tradeId']})
    assert after['valuation'] == reopened['valuation']
    c.call('crude/book', {**body, 'operationId': str(uuid4())}, expected=409)
    assert c.call('crude/book', body) == booked, 'An admitted booking retry retains its first outcome after a new setup publication'
    # Explicit supplemental-source pins must be identical in setup preview, ticket and booking.
    c.call('profile', {'profile': 'editor'})
    source = next(m for m in manifest['swapMarkets'] if m['revisionId'] == assignment['definitionRevisionId'])
    source_frame = next(f for f in source['frames'] if f['at'][:10] == frame['at'][:10])
    changed = c.call('corrections', {'snapshotId': frame['id'], 'observationId': source_frame['observationIds'][0], 'value': '81'})
    c.call('profile', {'profile': 'trader'})
    selected = changed['frame']['id']
    current = next(a for a in c.call('crude/catalog', {**context, 'snapshotId': selected})['assignments'] if a['crudeSetupId'] == draft['setupId'])
    b_body = {**body, 'operationId': str(uuid4()), 'snapshotId': selected, 'assignmentCode': current['code'], 'crudeSetupId': current['crudeSetupId']}
    mark = c.call('crude-setup/preview', {'snapshotId': selected, 'setupId': current['crudeSetupId'], 'knowledgeMode': 'current',
        'deliveryFrom': body['deliveryFrom'], 'deliveryTo': body['deliveryTo'], 'quantity': '1000'})
    assert Decimal(mark['presentValue']) == 81000
    assert Decimal(c.call('crude/preview', b_body)['presentValue']) == 3000
    b_trade = c.call('crude/book', b_body)
    assert Decimal(c.call('crude/editor', {'context': {**context, 'snapshotId': selected}, 'tradeId': b_trade['tradeId']})['valuation']['presentValue']) == 3000
    assert Decimal(c.call('crude/preview', {**b_body, 'snapshotId': frame['id']})['presentValue']) == 2000
    refused = c.call('crude/operations/read', {'context': context, 'tradeId': booked['tradeId']}, expected=422)
    assert refused['code'] == 'crude_mapped_lifecycle'
    b_context = {**shared, 'snapshotId': selected}
    b_blotter = c.call('trading/blotter', {'context': b_context, 'view': {'mode':'trades','bookIds':[body['bookId']],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}})
    b_retained = {**b_context, 'knowledgeMode': 'retained', 'contextId': b_blotter['context']['contextId']}
    c.call('profile', {'profile': 'risk'})
    b_catalog = c.call('portfolio/risk/catalog', {'context': b_retained, 'bookIds': [body['bookId']], 'portfolioIds': []})
    b_factor = next(f for f in b_catalog['factors'] if assignment['curve'] in f['curves'])
    b_request = {**request, 'context': b_retained, 'bumps': [{'factor': b_factor['factor'], 'size':'1','convention':b_catalog['bumpConvention']}]}
    b_report = c.call('portfolio/risk', b_request)
    b_value = next(p for p in b_report['factors'][0]['positions'] if p['tradeId'] == b_trade['tradeId'])
    assert {k: Fraction(b_value['values'][k]) for k in ('base','up','down','delta','gamma')} == {'base':3000,'up':4000,'down':2000,'delta':1000,'gamma':0}
    queued = c.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': b_request})
    # Retained knowledge does not retain a revoked delivery-catalog permission.
    delivery_catalog = saved['configuration']['deliveryReferenceCatalogId']
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{delivery_catalog}','{tenant}','{workspace}');")
    try:
        import subprocess
        try: run_worker()
        except subprocess.CalledProcessError: pass
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{delivery_catalog}','{tenant}','{workspace}','owner');")
    denied = c.call('portfolio/risk/jobs/' + queued['id'])
    assert denied['state'] != 'completed' and not denied.get('result'), denied
    if denied['state'] != 'queued':
        queued = c.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': b_request})
    run_worker()
    recovered = c.call('portfolio/risk/jobs/' + queued['id'])
    assert recovered['state'] == 'completed' and recovered['result']['factors'] == b_report['factors'], recovered
    print('PASS mapped crude booking: V2 admission, exact source/setup pins, retained reopen, corrected-source parity, worker risk and stale-head refusal', flush=True)
