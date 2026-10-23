"""Reference compatibility and structural observation entry under an exact view."""
from copy import deepcopy
from preview_client import PreviewClient


def run_checks(client, context, catalog, manifest):
    endpoint = 'swaps/observation-options'
    before = client.call('trading/blotter', {'context': {**context, 'convention': 'trading_workspace_v1'},
                         'view': {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
                                  'measures': [], 'pivotConvention': None}})
    available = client.call(endpoint, {'context': context})
    curve_first = client.call(endpoint, {'context': context, 'curve': 'base_forward'})
    assert {(mode['resetFrequency'], mode['averagingMethod']) for mode in curve_first['samplings']} == {('monthly', 'none')}
    daily = client.call(endpoint, {'context': context, 'resetFrequency': 'daily', 'averagingMethod': 'arithmetic'})
    assert any(index['curve'] == 'daily_gas' for index in daily['indices'])
    assert not any(index['curve'] == 'base_forward' for index in daily['indices'])
    monthly_daily = next(choice for choice in available['choices'] if choice['curve'] == 'base_forward' and choice['averagingMethod'] == 'arithmetic')
    assert not monthly_daily['available'] and monthly_daily['reasons'][0]['code'] == 'index_fixing_cadence'
    print('PASS observation options: source-first and convention-first use the same exact fixing-period capabilities', flush=True)

    source = next(index for index in catalog['indices'] if index['curve'] == 'daily_gas')
    query = {'context': context, 'definitionRevisionId': source['definitionRevisionId'], 'commodity': source['commodity'],
             'curve': source['curve'], 'index': source['index'], 'resetFrequency': 'daily',
             'averagingMethod': 'quantity_weighted', 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01'}
    quantities = client.call(endpoint, query)
    assert quantities['availability'] is None and len(quantities['observations']) == 31
    assert {'resetFrequency': 'daily', 'averagingMethod': 'quantity_weighted', 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01'} in quantities['deliveryPeriods']
    assert all('weight' not in row and 'quantity' not in row for row in quantities['observations'])
    assert all(row['quantitySpecification']['code'] == source['quantityCode'] and row['fixing']['index']['code'] == source['index']
               for row in quantities['observations'])
    assert [row['from'] for row in quantities['observations']] == [f'2021-01-{day:02d}' for day in range(1, 32)]
    print('PASS observation options: quantity entry receives required typed intervals without placeholder financial weights or prices', flush=True)

    calendar = next(row['reference'] for row in quantities['observationCalendars'] if row['reference']['code'] == 'test_business')
    observed = client.call(endpoint, {**query, 'averagingMethod': 'observation_arithmetic', 'observationCalendar': calendar})
    assert observed['availability'] is None and len(observed['observations']) == 20
    assert observed['observations'][0]['from'] == '2021-01-04' and observed['observations'][-1]['from'] == '2021-01-29'
    absent = client.call(endpoint, {**query, 'averagingMethod': 'observation_arithmetic'})
    assert absent['observations'] is None and absent['availability']['code'] == 'swap_observation_calendar'
    assert absent['deliveryPeriods'] == []
    assert observed['deliveryPeriods'] and all(period['averagingMethod'] == 'observation_arithmetic' for period in observed['deliveryPeriods'])
    stale = client.call(endpoint, {**query, 'deliveryFrom': '2098-01-01', 'deliveryTo': '2098-02-01'})
    assert stale['deliveryPeriods'] == quantities['deliveryPeriods'], 'Unavailable entered dates must not remove available replacement dates.'
    wrong = deepcopy(calendar)
    wrong['revision']['revisionId'] = manifest['revisionId']
    rejected = client.call(endpoint, {**query, 'averagingMethod': 'observation_arithmetic', 'observationCalendar': wrong})
    assert rejected['observations'] is None and rejected['availability'] is not None
    replay_context = {**context, 'knowledgeMode': 'retained', 'contextId': observed['context']['contextId']}
    replay = client.call(endpoint, {**query, 'context': replay_context, 'averagingMethod': 'observation_arithmetic', 'observationCalendar': calendar})
    assert replay['observations'] == observed['observations']
    print('PASS observation options: exact holiday calendar, missing and cross-version calendar refusal, and retained-context replay', flush=True)
    after = client.call('trading/blotter', {'context': {**context, 'convention': 'trading_workspace_v1'},
                        'view': {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
                                 'measures': [], 'pivotConvention': None}})
    assert before['holdings'] == after['holdings'], 'An authoring query must not persist a trade.'
    denied = PreviewClient(client.url)
    denied.bootstrap()
    denied.call('profile', {'profile': 'editor'})
    denied.call(endpoint, {'context': context}, expected=403)
    client.call(endpoint, {**query, 'resetFrequency': 'hourly', 'averagingMethod': 'arithmetic'}, expected=422)
    client.call(endpoint, {**query, 'deliveryTo': '2021-01-31'}, expected=422)
    client.call(endpoint, {**query, 'inferredPublication': True}, expected=400)
    print('PASS observation options: current trade authority, explicit supported cadence, whole-month scope and unknown-field rejection', flush=True)
