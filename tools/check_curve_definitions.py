"""D318 curve definition previews over a rolling date mapping in an owned synthetic preview."""
import json
from uuid import uuid4
from preview_client import PreviewClient


def run_checks(url, query, manifest):
    def client(profile):
        value = PreviewClient(url); value.bootstrap(); value.call('profile', {'profile': profile}); return value
    editor, approver, viewer = (client(p) for p in ('editor', 'approver', 'viewer'))
    cat = manifest['catalogId']

    def close(who, day):
        """The one close the fixture records on a day, as the session lists it."""
        frames = [f['id'] for f in who.call('workspace')['frames'] if f['at'].startswith(day + 'T')]
        assert len(frames) == 1, (day, frames)
        return frames[0]

    def context(day):
        return viewer.call('date-schedules/context', {'effectiveOn': day, 'knownAt': None, 'mode': 'current'})['context']

    # Futures positions must last trade when their exchange series does, so the mapping takes the seeded series' last-trading dates.
    # Both schedules select the first date on or after the context: at 15 January the front contract is February, January having expired.
    expiries = json.loads(query(f"""SELECT json_agg(json_build_array(m.business_date,to_char(m.event_at AT TIME ZONE 'UTC','HH24:MI')) ORDER BY m.series_code)
        FROM ref_data.futures_milestones m WHERE (m.catalog_id,m.revision_id)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}')
        AND m.milestone='last_trading' AND m.series_code BETWEEN 'test_gas_202102' AND 'test_gas_202106';"""))
    assert len(expiries) == 5 and len({cutoff for _, cutoff in expiries}) == 1, expiries
    for symbol, dates in [('oe.curve_starts', [f'2021-{m:02d}-01' for m in range(2, 8)]), ('oe.curve_expiries', [d for d, _ in expiries])]:
        draft = editor.call('date-schedules/import', {'name': symbol, 'symbol': symbol, 'dates': dates, 'calendar': None,
                                                      'selection': 'on_or_after', 'effectiveFrom': '2021-01-01', 'effectiveTo': '2021-07-01'})
        approver.call('date-schedules/publish', {k: draft[k] for k in ('id', 'version', 'generation')})
    mapping = editor.call('date-schedules/contracts/save', {
        'requestId': str(uuid4()), 'code': 'curve_months', 'name': 'Synthetic rolling months', 'context': context('2021-01-15'),
        'contracts': [{'label': f'Month {k + 1}', 'start': f'oe.curve_starts[{k}]', 'end': f'oe.curve_starts[{k + 1}]',
                       'lastTrading': f'oe.curve_expiries[{k}]', 'cutoffUtc': expiries[0][1]} for k in range(3)]})
    approver.call('date-schedules/contracts/publish', {'requestId': str(uuid4()), **{k: mapping[k] for k in ('id', 'version', 'generation')}})

    catalog = viewer.call('curve-definitions/catalog')
    coming_soon = {(c['field'], c['code']) for c in catalog['choices'] if not c['supported']}
    assert ('settlement', 'hourly_physical') in coming_soon and ('priceRule', 'formula') in coming_soon, coming_soon
    assert catalog['references']['futuresProducts']['test_gas_future']['commodity'] == 'test_gas'
    assert 'series' not in catalog['references'], 'The catalog returns futures series only through previews'

    future = {'code': 'test_gas_curve', 'name': 'Synthetic gas futures curve', 'commodity': 'test_gas', 'location': 'test_hub_a',
              'quote': 'test_gas_outright', 'profile': 'test_uniform', 'contractKind': 'future', 'futuresProduct': 'test_gas_future',
              'contracts': {'mappingId': mapping['id'], 'mappingVersion': 1, 'count': 3}, 'settlementTarget': 'test_future_target',
              'priceRule': 'direct', 'markRole': 'daily_settlement', 'markSource': 'fixture', 'missingPrice': 'reject', 'interpolation': 'none'}
    forward = {**future, 'code': 'test_gas_forward', 'contractKind': 'forward', 'futuresProduct': None,
               'settlementTarget': 'test_index_target', 'markRole': 'trader_mark'}
    curves_before = query(f"SELECT count(*) FROM market_data.curves WHERE catalog_id='{cat}';")

    # A preview resolves its window at the close the header selects: its date, under the session's knowledge.
    def preview(definition, day='2021-01-15', who=viewer, expected=200):
        return who.call('curve-definitions/preview', {'definition': definition, 'snapshotId': close(who, day)}, expected=expected)

    january = preview(future)
    assert [c['futuresSeries'] for c in january['contracts']] == ['test_gas_202102', 'test_gas_202103', 'test_gas_202104']
    assert [c['position'] for c in january['contracts']] == [1, 2, 3] and january['mapping'] == 'curve_months'
    assert january['terms']['timeZone'] == 'UTC' and january['terms']['currency'] == 'usd'
    assert january['context']['effectiveOn'] == '2021-01-15', january['context']
    february = preview(future, '2021-02-15')
    assert [c['futuresSeries'] for c in february['contracts']] == ['test_gas_202103', 'test_gas_202104', 'test_gas_202105'], 'The window did not roll'
    assert february['contracts'][0]['start'] == '2021-03-01' and february['contracts'][2]['end'] == '2021-06-01'
    ahead = preview(forward)
    assert ahead['terms']['settlementIndex'] == 'test_price' and all(c['futuresSeries'] is None for c in ahead['contracts'])

    for definition, code in [
        ({**forward, 'settlementTarget': 'test_basis_target'}, 'curve_unsupported'),
        ({**forward, 'missingPrice': 'carry_forward'}, 'curve_unsupported'),
        ({**forward, 'location': 'test_hub_b'}, 'curve_terms_mismatch'),
        ({**future, 'settlementTarget': 'test_index_target'}, 'curve_terms_mismatch'),
        ({**future, 'contracts': {**future['contracts'], 'count': 4}}, 'curve_contract_count'),
        ({**forward, 'quote': 'test_power_outright'}, 'curve_terms_mismatch'),
        ({**forward, 'markSource': 'unlisted'}, 'curve_reference'),
        ({**forward, 'interpolation': None}, 'curve_definition'),
    ]:
        assert preview(definition, expected=422)['code'] == code, (definition, code)
    # At a close the published mapping does not cover there is no window; it is refused, not moved to another date.
    early = preview(future, '2020-12-18', expected=422)
    assert early['code'] == 'contract_dates', early
    assert viewer.call('curve-definitions/preview', {'definition': future, 'snapshotId': None}, expected=422)['code'] == 'snapshot_missing'
    assert viewer.call('curve-definitions/preview', {'definition': future, 'snapshotId': str(uuid4())}, expected=422)['code'] == 'snapshot_missing'
    # Under a selected historical context a preview reads at that context's own close; it lists no close on another date, and a
    # close another session lists is refused.
    historical = client('viewer')
    selected = context('2021-01-15')
    historical.call('market/context/retained', {'contextId': selected['contextId'], 'selection': 'latest_correction'})
    terms = lambda preview: [(c['label'], c['start'], c['end'], c['lastTradingAt'], c['futuresSeries']) for c in preview['contracts']]
    retained = preview(future, who=historical)
    assert terms(retained) == terms(january) and retained['context']['contextId'] == selected['contextId'], retained['context']
    assert not any(f['at'].startswith('2021-02-15T') for f in historical.call('workspace')['frames'])
    assert historical.call('curve-definitions/preview', {'definition': future, 'snapshotId': close(viewer, '2021-02-15')},
                           expected=422)['code'] == 'snapshot_missing'
    preview({**future, 'contracts': {**future['contracts'], 'mappingId': str(uuid4())}}, expected=422)
    viewer.call('curve-definitions/preview', {'definition': {**future, 'unexpected': 1}, 'snapshotId': close(viewer, '2021-01-15')}, expected=400)
    assert query(f"SELECT count(*) FROM market_data.curves WHERE catalog_id='{cat}';") == curves_before, 'A preview wrote a curve'
    print('PASS curve definition previews validate terms and roll their contract window with the selected close')
