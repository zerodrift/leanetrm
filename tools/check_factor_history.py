"""Aligned history qualification in the harness-owned synthetic database only."""
from copy import deepcopy
from decimal import Decimal
from fractions import Fraction
import json

from preview_client import PreviewClient, initial_frame


def run_checks(url, manifest, query):
    c = PreviewClient(url); c.bootstrap(); c.call('profile', {'profile': 'risk'})
    working = c.call('workspace'); frame = initial_frame(working, '2020-12-18')
    current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    holdings = c.call('trading/blotter', {'context': current, 'view': {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}})
    held = next(h for h in holdings['holdings'] if h['instrumentType'] == 'future')
    context = {**current, 'knowledgeMode': 'retained', 'contextId': holdings['context']['contextId']}
    scope = {'context': context, 'bookIds': [held['bookId']], 'portfolioIds': [], 'tradeIds': [held['tradeId']]}
    catalog = c.call('portfolio/risk/catalog', scope)
    policy = {'convention': 'dated_factor_common_session_changes_v1', 'numericalPolicy': 'exact_18_fractional_abs_lt_1e24_v1', 'basis': 'fixed_current_dated_input_mapping_v1',
              'revisions': 'unique_visible_correction_head_v1', 'alignment': 'exact_session_close_v1', 'transform': 'native_absolute_change_v1',
              'missing': 'common_complete_adjacent_sessions_v1', 'regimes': 'exact_source_role_state_unit_contract_v1', 'minimumSamples': 2}
    request = {'scope': scope, 'pricingPolicy': catalog['pricingPolicy'], 'policy': policy,
               'calendar': {'revision': {'catalogId': manifest['referenceCatalogId'], 'revisionId': manifest['referenceRevisionId']}, 'code': 'test_business'},
               'from': '2020-12-15', 'to': '2020-12-19', 'closeTime': '16:00:00', 'ambiguousTime': 'reject', 'invalidTime': 'reject'}
    result = c.call('portfolio/risk/history', request)
    history = result['manifest']
    assert result['coverage']['available'] and len(history['columns']) == 1 and len(history['samples']) == 3 and not history['rejected']
    assert result['scope']['tradeIds'] == [held['tradeId']] and result['request']['scope']['bookIds'] == [held['bookId']]
    assert c.call('portfolio/risk/history', result['request'])['manifest'] == history
    prefix = int(query(f"SELECT known_through FROM market_data.market_contexts WHERE catalog_id='{manifest['catalogId']}' AND context_id='{context['contextId']}';"))
    assert history['forecast']['marketKnownThrough'] == prefix
    assert prefix != holdings['context']['knownThrough'], 'Fixture must distinguish market and schedule publication prefixes'
    for sample in history['samples']:
        assert Fraction(sample['changes'][0]) == Fraction(sample['to']['marks'][0]['value']) - Fraction(sample['from']['marks'][0]['value'])
    print('PASS aligned history HTTP: held dated-factor scope, exact adjacent changes, separate market prefix and canonical replay', flush=True)
    whole_scope = {**scope, 'bookIds': [], 'tradeIds': None}
    whole_catalog = c.call('portfolio/risk/catalog', whole_scope)
    whole = c.call('portfolio/risk/history', {**request, 'scope': whole_scope})
    held_series = {f['seriesId'] for f in whole_catalog['factors']}
    assert len(held_series) > 1 and len(whole_catalog['availableFactors']) > len(held_series)
    assert {column['mapping']['factor']['input']['series']['id'] for column in whole['manifest']['columns']} == held_series
    assert whole['excludedPositions'] == whole_catalog['excludedPositions']
    assert all(len(endpoint['marks']) == len(held_series)
               for row in whole['manifest']['samples'] + whole['manifest']['rejected'] for endpoint in (row['from'], row['to']))
    print('PASS aligned history universe: mixed held instruments share the exact complete factor union; unused available quotes stay outside it', flush=True)
    long_request = {**request, 'scope': whole_scope, 'from': '2020-01-06',
                    'calendar': {**request['calendar'], 'code': 'weekdays_utc'}, 'policy': {**policy, 'minimumSamples': 200}}
    longer = c.call('portfolio/risk/history', long_request)
    assert longer['coverage']['available'] and len(longer['manifest']['sessions']) == 250 and len(longer['manifest']['samples']) == 249
    assert len(longer['manifest']['columns']) == len(held_series) and not longer['manifest']['rejected']
    count = query('SELECT count(*) FROM market_data.observations;')
    from factor_history_fixture import seed
    seed(query, manifest)
    assert query('SELECT count(*) FROM market_data.observations;') == count
    assert c.call('portfolio/risk/history', {**long_request, 'expectedManifestId': longer['manifest']['id']})['manifest'] == longer['manifest']
    print('PASS aligned history development data: 250 daily closes provide 249 common changes across the mixed held universe', flush=True)
    for change in ('minimum', 'transform', 'clock', 'future', 'forged'):
        bad = deepcopy(request)
        if change == 'minimum': bad['policy']['minimumSamples'] = 0
        elif change == 'transform': bad['policy']['transform'] = 'log'
        elif change == 'clock': bad['closeTime'] = ''
        elif change == 'future': bad['to'] = '2020-12-23'
        else: bad['resolvedCloses'] = []
        c.call('portfolio/risk/history', bad, expected=400 if change == 'forged' else 422)
    off_close = c.call('portfolio/risk/history', {**request, 'closeTime': '15:59:00'})
    assert not off_close['coverage']['available'] and not off_close['manifest']['samples'] and len(off_close['manifest']['rejected']) == 3
    assert {i['reason'] for row in off_close['manifest']['rejected'] for i in row['issues']} == {'asynchronous_close'}
    insufficient = c.call('portfolio/risk/history', {**request, 'policy': {**policy, 'minimumSamples': 4}})
    assert not insufficient['coverage']['available'] and len(insufficient['manifest']['samples']) == 3
    wrong = c.call('portfolio/risk/history', {**request, 'expectedManifestId': '0' * 64}, expected=422)
    assert wrong['code'] == 'risk_history_manifest'
    print('PASS aligned history admission: explicit policies, exact clocks, unavailable coverage, forged schedules and expected-identity refusal', flush=True)
    extra = deepcopy(request); extra['policy']['forwardFill'] = True
    c.call('portfolio/risk/history', extra, expected=400)
    original = history['samples'][0]['from']['marks'][0]
    editor = PreviewClient(url); editor.bootstrap(); editor.call('profile', {'profile': 'editor'})
    old_frame = initial_frame(editor.call('workspace'), '2020-12-15')
    correction = editor.call('corrections', {'snapshotId': old_frame['id'], 'observationId': original['observation']['id'],
                                           'value': format(Decimal(original['value']) + Decimal('0.25'), 'f')})
    assert c.call('portfolio/risk/history', result['request'])['manifest'] == history, 'Future knowledge cannot alter the earlier captured history'
    newer = c.call('trading/blotter', {'context': current, 'view': {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}})
    next_request = {**request, 'scope': {**scope, 'context': {**context, 'contextId': newer['context']['contextId']}}}
    latest = c.call('portfolio/risk/history', next_request)
    latest_mark = latest['manifest']['samples'][0]['from']['marks'][0]
    assert latest['manifest']['id'] != history['id'] and latest_mark['observation']['id'] == correction['observationId']
    assert latest_mark['lineage'] == [original['observation'], latest_mark['observation']]
    assert Fraction(latest['manifest']['samples'][0]['changes'][0]) == Fraction(history['samples'][0]['changes'][0]) - Fraction(1, 4)
    assert newer['context']['knownThrough'] == holdings['context']['knownThrough'], 'An observation correction does not advance the schedule publication stream'
    print('PASS aligned history knowledge: later corrected history uses a new certified market prefix; original manifest and ancestor evidence replay exactly', flush=True)
    anchor = history['columns'][0]['mapping']['factor']['observation']
    anchor_row = next(r for r in history['evidence'] if r['observation']['key'] == anchor)
    today = initial_frame(editor.call('workspace'), '2020-12-18')
    corrected_anchor = editor.call('corrections', {'snapshotId': today['id'], 'observationId': anchor['id'],
                                                  'value': format(Decimal(anchor_row['observation']['value']) + Decimal('0.5'), 'f')})
    selected = c.call('market/context', {'effectiveOn': '2020-12-18', 'mode': 'current', 'selection': 'latest_correction'})
    source = c.call('workspace')
    selected_frame = next(f for f in source['frames'] if f['at'].startswith('2020-12-18') and corrected_anchor['observationId'] in f['observationIds'])
    short = {**request, 'to': '2020-12-17', 'scope': {**scope, 'context': {**context, 'snapshotId': selected_frame['id'], 'contextId': selected['context']['contextId']}}}
    older_window = c.call('portfolio/risk/history', short)
    assert len(older_window['manifest']['samples']) == 1 and not older_window['coverage']['available']
    assert {anchor['id'], corrected_anchor['observationId']} <= {r['observation']['key']['id'] for r in older_window['manifest']['evidence']}
    print('PASS aligned history anchors: a corrected forecast mark outside the lookback retains its full visible ancestry', flush=True)
    c.call('market/current', {})
    query(f"""INSERT INTO market_data.observations
        (catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
        SELECT catalog_id,gen_random_uuid(),definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,
          repeat('unused_source_metadata_',3000),'1',role,publication_state,'2020-12-16 16:01:00+00',clock_timestamp(),value,NULL
        FROM market_data.observations WHERE catalog_id='{manifest['catalogId']}' AND id='{anchor['id']}';""")
    large_context = c.call('trading/blotter', {'context': current, 'view': {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}})['context']
    bounded = c.call('portfolio/risk/history', {**request, 'scope': {**scope, 'context': {**context, 'contextId': large_context['contextId']}}})
    assert bounded['coverage']['available'] and len(json.dumps(bounded)) < 50000
    assert 'unused_source_metadata_' not in json.dumps(bounded)
    print('PASS aligned history evidence: large unused source metadata stays outside the typed manifest', flush=True)
    c.call('profile', {'profile': 'viewer'})
    c.call('portfolio/risk/history', result['request'], expected=403)
    c.call('profile', {'profile': 'risk'})
    assert c.call('portfolio/risk/history', result['request'])['manifest'] == history
    print('PASS aligned history authority: captured identities never grant a viewer scenario access', flush=True)
