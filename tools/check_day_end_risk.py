"""EOD typed-price and DST custody checks in the caller's owned disposable database."""
from copy import deepcopy
from datetime import datetime
from fractions import Fraction
from uuid import uuid4


def run_checks(c, query, scope, catalog, original_history, statistical, run_worker):
    factor = original_history['manifest']['columns'][0]['mapping']['factor']
    series, refs = factor['input']['series'], factor['source']['revision']
    for date, at, value in [('2020-11-01', '2020-11-01T04:30:00Z', '10'),
                            ('2020-11-02', '2020-11-02T22:00:00Z', '20'),
                            ('2020-11-03', '2020-11-03T22:00:00Z', '30')]:
        query(f"""INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
          source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value)
          VALUES('{series['revision']['catalogId']}','{uuid4()}','{series['revision']['revisionId']}','{series['id']}',
          '{refs['catalogId']}','{refs['revisionId']}','{factor['source']['code']}','day_end/{series['id']}/{date}','1',
          'trader_mark','final','{at}',clock_timestamp(),{value});""")
    current = {**scope['context'], 'knowledgeMode': 'current'}
    current.pop('contextId')
    held = c.call('trading/blotter', {'context': current, 'view': {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}})
    scope = {**scope, 'context': {**scope['context'], 'contextId': held['context']['contextId']}}
    guided = catalog['history']['guided']
    source = {'scope': scope, 'pricingPolicy': catalog['pricingPolicy'],
              'policy': {**guided['profile'], 'minimumSamples': 2, 'priceTypes': ['official_close_v1']},
              'calendar': {**original_history['request']['calendar'], 'code': 'calendar_days_new_york'},
              'from': '2020-11-01', 'to': '2020-11-04', 'closeTime': 'end_of_day', 'ambiguousTime': 'reject', 'invalidTime': 'reject'}
    official = c.call('portfolio/risk/history', source)
    assert not official['coverage']['available'] and len(official['manifest']['samples']) == 1 and len(official['manifest']['rejected']) == 1
    missing = official['manifest']['dayPrices'][0]['choices'][0]
    assert missing['selectedType'] is None and missing['officialCloseStatus'] == 'missing_mark'
    assert 'trader_mark_final_v1' in missing['availableTypes']
    allowed = deepcopy(source)
    allowed['policy']['priceTypes'].append('trader_mark_final_v1')
    aligned = c.call('portfolio/risk/history', allowed)
    h = aligned['manifest']
    assert aligned['coverage']['available'] and len(h['samples']) == 2
    window = h['clock']['dayWindows'][0]
    parse = lambda s: datetime.fromisoformat(s.replace('Z', '+00:00'))
    assert (parse(window['end']) - parse(window['start'])).total_seconds() == 25 * 3600
    first = h['samples'][0]['from']['marks'][0]
    original = next(r for r in h['evidence'] if r['observation']['key'] == first['observation'])
    assert parse(original['observation']['observedAt']) == parse('2020-11-01T04:30:00Z'), 'The first hour of a 25-hour first read window was lost'
    assert h['dayPrices'][0]['choices'][0]['selectedType'] == 'trader_mark_final_v1'
    assert all(d['choices'][0]['selectedType'] == 'official_close_v1' for d in h['dayPrices'][1:]), 'Official type priority must precede recency across types'
    assert c.call('portfolio/risk/history', aligned['request'])['manifest'] == h
    body = {**statistical, 'history': aligned['request']}
    direct = c.call('portfolio/risk/parametric', body)['parametric']
    moves = [Fraction(row['changes'][0]) for row in h['samples']]
    average = sum(moves) / len(moves)
    covariance = sum((m - average) ** 2 for m in moves) / (len(moves) - 1)
    assert Fraction(direct['result']['calibration']['covariance'][0][0]) == covariance
    from check_parametric_risk import exact_amounts
    summary = exact_amounts(c.call('portfolio/risk/parametric/summary', body))
    assert [f['sessionPriceTypes'] for f in summary['factors']] == [{'trader_mark_final_v1': 1, 'official_close_v1': len(h['dayPrices']) - 1}]
    assert summary['partitions'][0]['valueAtRisk'] == direct['result']['partitions'][0]['valueAtRisk']
    historical = {'history': aligned['request'], 'policy': {**catalog['historical']['profile'], 'confidence': '0.95'}}
    assert c.call('portfolio/risk/historical/summary', historical)['factors'][0]['sessionPriceTypes'] == summary['factors'][0]['sessionPriceTypes']
    first = exact_amounts(c.call('portfolio/risk/historical/sample', {'request': historical, 'sample': h['samples'][0]['id']}))['moves'][0]
    assert (first['fromPriceType'], first['toPriceType']) == ('trader_mark_final_v1', 'official_close_v1')
    job = c.call('portfolio/risk/parametric/jobs', {'requestId': str(uuid4()), 'request': body})
    run_worker()
    completed = c.call('portfolio/risk/jobs/' + job['id'])
    assert completed['state'] == 'completed', completed.get('error')
    retained = completed['result']['parametric']
    assert retained['history'] == direct['history'] and retained['result'] == direct['result']
    assert retained['request']['history']['policy']['priceTypes'] == ['official_close_v1', 'trader_mark_final_v1']
    print('PASS EOD price history: official-only missing coverage, explicit ordered fallback, first-hour 25-hour local window, exact covariance and actual worker/receipt parity', flush=True)
