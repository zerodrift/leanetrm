"""Computed parents stay connected through booked risk and retained worker replay."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4


def run_checks(client, manifest, query, run_worker, frame, parent, signed, terms, parent_price, signed_price):
    client.call('profile', {'profile': 'editor'})
    signed = deepcopy(signed)
    signed.update(code='synthetic_signed_parent', name='Synthetic signed parent')
    second = client.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'definition': signed, 'layout': {}})
    client.call('profile', {'profile': 'approver'})
    second = client.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': second['id'], 'version': second['version'], 'generation': second['generation']})
    def reference(version):
        return {'catalogId': manifest['catalogId'], 'curveId': version['id'], 'version': version['version'], 'outputNode': version['definition']['output']}
    composition = {'code': 'synthetic_composed_portfolio', 'name': 'Synthetic composed portfolio',
                   'aggregation': 'normalized_weighted_mean_v1', 'alignment': 'exact_delivery_intervals_v1',
                   'inputs': [{'source': reference(parent), 'weight': '7'}, {'source': reference(second), 'weight': '3'}]}
    prepared = client.call('custom-curves/compose', {'snapshotId': frame['id'], 'composition': composition})
    definition = prepared['definition']
    preview = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': definition})
    expected = (7 * parent_price + 3 * signed_price) / 10
    exact = preview['points'][0]['price']
    assert Fraction(int(exact['numerator']), int(exact['denominator'])) == expected
    client.call('profile', {'profile': 'editor'})
    saved = client.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'], 'definition': definition, 'layout': {}})
    client.call('profile', {'profile': 'approver'})
    published = client.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': saved['id'], 'version': saved['version'], 'generation': saved['generation']})
    check_weights(query, manifest, published['id'])
    client.call('profile', {'profile': 'trader'})
    choices = client.call('swaps/catalog', {key: terms[key] for key in ('snapshotId', 'convention', 'knowledgeMode')})
    index = next(i for i in choices['indices'] if i['curve'] == terms['floating']['curve'] and i['definitionRevisionId'] == terms['definitionRevisionId'])
    selection = next(p['reference'] for p in index['customProjections'] if p['reference']['curveId'] == published['id'])
    booking = {**terms, 'operationId': str(uuid4()), 'floating': {**terms['floating'], 'customProjection': selection}}
    booked = client.call('swaps/book', booking)
    client.call('profile', {'profile': 'risk'})
    context = {key: terms[key] for key in ('snapshotId', 'knowledgeMode')}
    context['convention'] = 'trading_workspace_v1'
    catalog = client.call('portfolio/risk/catalog', {'context': context, 'bookIds': [terms['bookId']], 'portfolioIds': []})
    def axis(code):
        return next(t['factor'] for t in catalog['scenarioTargets'] if t['factor']['coordinate'] == 'computed_output'
                    and t['factor']['target']['curve']['code'] == code and t['factor']['target']['kind'] == 'points'
                    and len(t['factor']['target']['points']) == 1)
    axes = [axis(v['definition']['code']) for v in (parent, second, published)]
    request = {'context': {**context, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']},
               'bookIds': [terms['bookId']], 'portfolioIds': [], 'detail': 'holdings',
               'bumps': [{'factor': factor, 'size': '0.1', 'convention': catalog['bumpConvention']} for factor in axes],
               'stress': [], 'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'], 'pricingPolicy': catalog['pricingPolicy']}
    base = client.call('portfolio/risk', request)
    for factor, delta in zip(base['factors'], (7, 3, 10)):
        value = next(p['values'] for p in factor['positions'] if p['tradeId'] == booked['tradeId'])
        assert Fraction(value['base']) == expected * 10 and Fraction(value['delta']) == delta and Fraction(value['gamma']) == 0, value
    stressed = {**request, 'stress': [{'factor': axes[0], 'size': '1', 'convention': catalog['stressConvention']},
                                    {'factor': axes[2], 'size': '0.2', 'convention': catalog['stressConvention']}]}
    result = client.call('portfolio/risk', stressed)
    value = next(p['values'] for p in result['factors'][0]['positions'] if p['tradeId'] == booked['tradeId'])
    assert Fraction(value['base']) == expected * 10 + 9
    queued = client.call('portfolio/risk/jobs', {'requestId': str(uuid4()), 'request': stressed})
    run_worker()
    finished = client.call('portfolio/risk/jobs/' + queued['id'])
    assert finished['state'] == 'completed', finished
    assert finished['result']['factors'] == result['factors']
    assert finished['result']['inputs']['connectedMarkets'] == result['inputs']['connectedMarkets']
    assert client.call('portfolio/risk', request)['factors'] == base['factors'], 'Nested stresses changed their base context'
    print('PASS composed portfolio: two computed parents, typed persisted weights, booked PV, independent parent/root output sensitivities, nested scenarios and retained-worker parity', flush=True)


def check_weights(query, manifest, curve_id):
    """The database independently rejects invalid relations and post-insert draft drift."""
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    assert query(f"SELECT count(*) FROM market_data.custom_curve_panel_weights WHERE catalog_id='{catalog}' AND curve_id='{curve_id}'") == '2'
    fork = str(uuid4())
    setup = f"""INSERT INTO market_data.custom_curves VALUES('{catalog}','{fork}','synthetic_panel_fk');
      INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout)
       SELECT catalog_id,'{fork}',1,1,'draft','Synthetic panel FK',output_node,engine_version,layout
       FROM market_data.custom_curve_versions WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
      INSERT INTO market_data.custom_curve_nodes SELECT catalog_id,'{fork}',version,node_id,kind,parameters FROM market_data.custom_curve_nodes WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
      INSERT INTO market_data.custom_curve_edges SELECT catalog_id,'{fork}',version,node_id,port,source_node FROM market_data.custom_curve_edges WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
      INSERT INTO market_data.custom_curve_node_published_sources SELECT catalog_id,'{fork}',version,node_id,source_catalog_id,source_curve_id,source_version,source_output_node FROM market_data.custom_curve_node_published_sources WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
      INSERT INTO market_data.custom_curve_panel_weights SELECT catalog_id,'{fork}',version,node_id,source_port,source_node,weight FROM market_data.custom_curve_panel_weights WHERE catalog_id='{catalog}' AND curve_id='{curve_id}' AND version=1;
    """
    for change in ("parameters=parameters-'alignment'", "kind='mean',parameters='{}'::jsonb"):
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        try:
            query(f"BEGIN;INSERT INTO security.role_permissions SELECT id,'WriteMarketDefinitions' FROM security.roles WHERE code='market_data_approver';SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');" + setup +
                  f"UPDATE market_data.custom_curve_nodes SET {change} WHERE catalog_id='{catalog}' AND curve_id='{fork}' AND kind='weighted_panels';SELECT security.publish_custom_curve('{catalog}','{fork}',1,1);ROLLBACK;")
        except RuntimeError as error:
            assert 'recipe_panel_weights' in str(error), error
        else:
            raise AssertionError('A changed panel operator/policy was published with invalid retained weights')
