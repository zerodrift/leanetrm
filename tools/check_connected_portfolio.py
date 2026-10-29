"""Published custom projections through booked PV and retained risk jobs."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4

from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, manifest, query, run_worker):
    assert query("SELECT to_regclass('risk.job_custom_curves') IS NOT NULL") == 't', 'Connected job retention is missing'
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'editor'})
    frame = initial_frame(client.call('workspace'), '2020-12-18')
    template = next(t for t in client.call('custom-curves')['templates'] if t['code'] == 'ng_average')
    definition = deepcopy(template['definition'])
    definition.update(code='synthetic_connected_mean', name='Synthetic connected mean')
    for node in definition['nodes']:
        if node['kind'] == 'source': node['parameters']['curve'] = 'base_forward'
        if node['kind'] == 'select': node['parameters']['count'] = '1'
        if node['kind'] == 'dates': node['parameters'].update(count='2', includeContext='true')
    saved = client.call('custom-curves/save', {'requestId': str(uuid4()), 'snapshotId': frame['id'],
        'definition': definition, 'layout': {}})
    client.call('profile', {'profile': 'approver'})
    client.call('custom-curves/publish', {'requestId': str(uuid4()), 'id': saved['id'],
        'version': saved['version'], 'generation': saved['generation']})
    reference = {'catalogId': manifest['catalogId'], 'curveId': saved['id'], 'version': saved['version'],
        'outputNode': definition['output'], 'position': 1, 'selection': 'single_output_position_v1',
        'implementation': 'published_custom_curve_projection_v1'}
    panel = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': definition,
        'id': saved['id'], 'version': saved['version']})
    expected_price = sum(Fraction(s['price']['value']) for s in panel['inputs']) / len(panel['inputs'])
    assert len(panel['inputs']) == 2 and len(panel['dependencies']) == 2
    client.call('profile', {'profile': 'trader'})
    context = {'snapshotId': frame['id'], 'convention': 'independent_side_financial_swap_v2', 'knowledgeMode': 'current'}
    catalog = client.call('swaps/catalog', context)
    index = next(i for i in catalog['indices'] if i['curve'] == 'base_forward' and i['definitionRevisionId'] == manifest['revisionId'])
    delivery = next(c for c in index['contracts'] if c['deliveryFrom'] == '2021-01-01')
    payment = {'frequency': 'monthly', 'rule': 'days_after_period_end', 'days': 0,
        'dayOfMonth': None, 'dayBasis': 'calendar_days', 'shortMonth': None,
        'adjustment': 'unadjusted', 'calendar': None, 'implementation': 'monthly_payment_date_rules_v1'}
    terms = {**context, **trade_booking(), 'operationId': str(uuid4()), 'bookId': catalog['books'][0]['id'],
        'definitionRevisionId': index['definitionRevisionId'], 'commodity': index['commodity'], 'quantity': '10',
        'fixedCoefficient': '-1', 'discounting': False,
        'fixed': {'price': '0', 'currency': index['currencyCode'], **delivery, 'paymentSchedule': payment},
        'floating': {'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve': index['curve'], 'index': index['index'], 'currency': index['currencyCode'],
            **delivery, 'paymentSchedule': payment, 'resetFrequency': 'monthly', 'averagingMethod': 'none',
            'customProjection': reference}}
    draft = {'context': context, **{key: terms[key] for key in
        ('definitionRevisionId', 'commodity', 'fixedCoefficient', 'discounting', 'fixed', 'floating')},
        'quantity': {'value': terms['quantity'], 'specification': index['quantityChoices'][0]['specification']}}
    period_query = {'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'context': context, 'definitionRevisionId': index['definitionRevisionId'], 'commodity': index['commodity'],
                    'curve': index['curve'], 'index': index['index'], 'resetFrequency': 'monthly', 'averagingMethod': 'none',
                    'customProjection': reference}
    periods = client.call('swaps/observation-options', period_query)['deliveryPeriods']
    assert any(p['deliveryFrom'] == '2021-01-01' for p in periods), 'Qualified custom monthly output disappeared from delivery choices.'
    unavailable = client.call('swaps/observation-options', {**period_query, 'customProjection': {**reference, 'version': reference['version']+1}})
    assert unavailable['deliveryPeriods'] == [], 'Unpublished custom projection produced selectable delivery dates.'
    before = query("SELECT count(*)::text||':'||(SELECT count(*) FROM trading.publications)::text FROM trading.trades;")
    priced = client.call('swaps/price', draft)
    assert priced['unbooked'] and Fraction(priced['valuation']['presentValue']) == expected_price * 10
    draft_request = {'draft': {**draft, 'context': {**context, 'knowledgeMode': 'retained',
        'contextId': priced['context']['contextId']}}, 'bumpPolicy': priced['bumpPolicy'],
        'numericalPolicy': priced['numericalPolicy']}
    draft_risk = client.call('swaps/draft-risk', draft_request)
    assert draft_risk['context']['contextId'] == priced['context']['contextId']
    assert draft_risk['scenarioCount'] == 5 and len(draft_risk['factors']) == 2
    for factor in draft_risk['factors']:
        bump = Fraction(factor['bump'])
        assert Fraction(factor['basePv']) == expected_price * 10
        assert Fraction(factor['delta']) == 5 and Fraction(factor['gamma']) == 0
        assert Fraction(factor['upPv']) == expected_price * 10 + 5 * bump
        assert Fraction(factor['downPv']) == expected_price * 10 - 5 * bump
    assert client.call('swaps/draft-risk', draft_request)['factors'] == draft_risk['factors']
    assert query("SELECT count(*)::text||':'||(SELECT count(*) FROM trading.publications)::text FROM trading.trades;") == before
    print('PASS connected draft: exact custom Price/Risk parity, retained replay and no trading writes', flush=True)
    booked = client.call('swaps/book', terms)
    assert client.call('swaps/book', terms) == booked
    from check_custom_projection_db import run_booking_checks
    run_booking_checks(query, manifest, booked['tradeId'])
    client.call('swaps/book', {**terms, 'quantity': '11'}, expected=409)
    editor = client.call('swaps/editor', {'context': context, 'tradeId': booked['tradeId'], 'revision': None})
    assert Fraction(editor['valuation']['presentValue']) == expected_price * 10
    assert editor['state']['terms']['floating']['customProjection'] == reference
    blotter = client.call('trading/blotter', {'context': {**context, 'convention': 'trading_workspace_v1'},
        'view': {'mode': 'trades', 'bookIds': [terms['bookId']], 'portfolioIds': [], 'groupBy': [],
            'measures': [], 'pivotConvention': None}})
    holding = next(row for row in blotter['holdings'] if row['tradeId'] == booked['tradeId'])
    assert Fraction(holding['mark']) == expected_price, 'The custom blotter mark must be an exact numeric value'
    assert Fraction(holding['presentValue']) == expected_price * 10
    for invalid in ({**reference, 'version': reference['version'] + 1}, {**reference, 'outputNode': 'missing_output'}):
        error = client.call('swaps/book', {**terms, 'operationId': str(uuid4()),
            'floating': {**terms['floating'], 'customProjection': invalid}}, expected=422)
        assert error['code'] in ('custom_projection', 'custom_curve_definition'), error
    print('PASS connected booking: idempotent receipt, exact editor/blotter values and rejected missing version/output', flush=True)
    client.call('profile', {'profile': 'risk'})
    selected = {**context, 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    risk_catalog = client.call('portfolio/risk/catalog', {'context': selected, 'bookIds': [terms['bookId']], 'portfolioIds': []})
    retained = {**selected, 'knowledgeMode': 'retained', 'contextId': risk_catalog['context']['contextId']}
    bumps = [{'factor': f['factor'], 'size': '0.1', 'convention': risk_catalog['bumpConvention']}
             for f in risk_catalog['factors']]
    request = {'context': retained, 'bookIds': [terms['bookId']], 'portfolioIds': [], 'detail': 'holdings',
        'bumps': bumps, 'stress': [], 'convention': risk_catalog['convention'],
        'numericalPolicy': risk_catalog['numericalPolicy'], 'pricingPolicy': risk_catalog['pricingPolicy']}
    result = client.call('portfolio/risk', request)
    assert len(result['factors']) == 2
    draft_factors = {f['factor']['seriesId']: f for f in draft_risk['factors']}
    for factor in result['factors']:
        value = next(position['values'] for position in factor['positions'] if position['tradeId'] == booked['tradeId'])
        assert Fraction(value['base']) == 10 * expected_price
        assert Fraction(value['delta']) == 5 and Fraction(value['gamma']) == 0
        assert Fraction(value['upPnl']) == Fraction(1, 2)
        assert Fraction(draft_factors[factor['factor']['seriesId']]['delta']) == Fraction(value['delta'])
    assert result['inputs']['connectedMarkets'][0]['manifestId']
    # The risk report's presets shock what a position is priced from. This swap is priced from the published curve's output for
    # its month, which is no contract: Shock every held contract names it instead of shocking a contract in its place, and
    # Parallel shift moves the whole output curve, which moves the swap one for one.
    outputs = [t for t in risk_catalog['scenarioTargets'] if t['coordinate'] == 'computed_output' and t['kind'] == 'points'
               and t['factor']['target']['curve']['code'] != 'base_forward' and '2021-01-01 to 2021-02-01' in t['label']]
    assert len(outputs) == 1 and risk_catalog['heldContractTargets'] == [] and risk_catalog['heldContractsWithoutTarget'] == [outputs[0]['label']], risk_catalog['heldContractsWithoutTarget']
    shifted = [t for t in risk_catalog['scenarioTargets'] if t['id'] in risk_catalog['heldCurveTargets']]
    assert len(shifted) == 1 == len(risk_catalog['heldCurveTargets']) and shifted[0]['kind'] == 'parallel' and shifted[0]['coordinate'] == 'computed_output' \
        and shifted[0]['factor']['target']['curve'] == outputs[0]['factor']['target']['curve'] and risk_catalog['heldCurvesWithoutTarget'] == [], shifted
    parallel = client.call('portfolio/risk', {**request, 'bumps': [{'factor': shifted[0]['factor'], 'size': '0.1', 'convention': risk_catalog['bumpConvention']}]})
    moved = next(p['values'] for p in parallel['factors'][0]['positions'] if p['tradeId'] == booked['tradeId'])
    assert Fraction(moved['delta']) == 10 and Fraction(moved['upPnl']) == 1, moved
    print('PASS connected held targets: a published output is named, not shocked as a contract, and its whole output curve moves the swap one for one', flush=True)
    operation = {'requestId': str(uuid4()), 'request': request}
    queued = client.call('portfolio/risk/jobs', operation)
    assert queued['state'] == 'queued'
    assert client.call('portfolio/risk/jobs', operation)['id'] == queued['id']
    client.call('portfolio/risk/jobs', {**operation, 'request': {**request, 'detail': 'books'}}, expected=409)
    # Correct a historical constituent after admission. A fresh calculation must
    # see the new price, while this worker must rebuild the original exact pins.
    sample = next(s for s in result['inputs']['connectedMarkets'][0]['samples'] if s['freezeHistorical'])
    original = sample['observations'][0]['id']
    correction = str(uuid4())
    query(f"""INSERT INTO market_data.observations
        SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
         source_code,source_event_id,'{correction}',role,publication_state,observed_at,clock_timestamp(),value+1,id
        FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}' AND id='{original}';""")
    client.call('market/current', {})
    corrected = client.call('custom-curves/preview', {'snapshotId': frame['id'], 'definition': definition,
        'id': saved['id'], 'version': saved['version']})
    corrected_price = sum(Fraction(s['price']['value']) for s in corrected['inputs']) / len(corrected['inputs'])
    assert corrected_price == expected_price + Fraction(1, 2)
    assert client.call('portfolio/risk', request)['factors'] == result['factors']
    assert client.call('portfolio/risk/jobs', operation)['id'] == queued['id']
    run_worker()
    finished = client.call('portfolio/risk/jobs/' + queued['id'])
    assert finished['state'] == 'completed', finished
    assert finished['result']['factors'] == result['factors']
    assert finished['result']['inputs']['connectedMarkets'] == result['inputs']['connectedMarkets']
    assert client.call('portfolio/risk/jobs', operation)['id'] == queued['id']
    assert query(f"SELECT count(*) FROM risk.jobs WHERE request_id='{operation['requestId']}';") == '1'
    assert query(f"SELECT count(*) FROM risk.job_custom_curves WHERE job_id='{queued['id']}';") == '1'
    from check_connected_risk_db import run_checks as check_retention
    check_retention(query,manifest,queued['id'])
    client.call('profile', {'profile': 'trader'})
    amendment = {**terms, 'operationId': str(uuid4()), 'tradeId': booked['tradeId'],
        'expectedRevision': 1, 'effectiveAt': frame['at'], 'reason': 'Synthetic quantity correction', 'quantity': '20'}
    preview = client.call('swaps/preview', amendment)
    assert Fraction(preview['valuation']['presentValue']) == corrected_price * 20
    amended = client.call('swaps/amend', amendment)
    assert client.call('swaps/amend', amendment) == amended
    updated = client.call('swaps/editor', {'context': context, 'tradeId': booked['tradeId'], 'revision': None})
    assert updated['state']['revision'] == 2
    assert updated['state']['terms']['floating']['customProjection'] == reference
    assert Fraction(updated['valuation']['presentValue']) == corrected_price * 20
    client.call('profile', {'profile': 'risk'})
    assert client.call('portfolio/risk', request)['factors'] == result['factors']
    assert client.call('portfolio/risk/jobs/' + queued['id'])['result']['factors'] == result['factors']
    print('PASS connected amendment: explicit projection survives quantity change and retained prior risk stays unchanged', flush=True)
    print('PASS connected portfolio: live source shocks, idempotent queue and retained worker parity after a historical correction', flush=True)
    from check_weighted_portfolio import run_checks as check_weighted
    check_weighted(url, manifest, query, run_worker)
