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
    assert {('rate.dayCount', 'thirty_360'), ('rate.compounding', 'semi_annual'), ('rate.markUnit', 'basis_points')} <= coming_soon, coming_soon
    assert ('rate.extrapolation', 'flat_forward') in {(c['field'], c['code']) for c in catalog['choices'] if c['supported']}
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

    # D357: curve definitions are saved, published and edited. An interest-rate curve takes every label of a date list.
    calendar = next(c['reference'] for c in editor.call('date-schedules')['calendars'] if c['reference']['code'] == 'test_business')
    days = editor.call('date-schedules/save', {'definition': {'name': 'Synthetic rate days', 'symbol': 'oe.rate_days',
        'daily': {'days': 'calendar', 'firstDate': '2021-01-01', 'lastDate': '2021-12-31'}}, 'calendar': calendar,
        'selection': 'on_or_after', 'effectiveFrom': '2021-01-01', 'effectiveTo': '2022-01-01'})
    approver.call('date-schedules/publish', {k: days[k] for k in ('id', 'version', 'generation')})
    tenors = editor.call('date-schedules/contracts/save', {'requestId': str(uuid4()), 'code': 'rate_tenors', 'name': 'Synthetic rate tenors',
        'context': context('2021-01-15'), 'contracts': None, 'dates': [{'label': '1D', 'date': 'oe.rate_days + 1bd'}, {'label': '3M', 'date': 'oe.rate_days[90]'}]})
    tenors = approver.call('date-schedules/contracts/publish', {'requestId': str(uuid4()), **{k: tenors[k] for k in ('id', 'version', 'generation')}})
    blank = {k: None for k in ('commodity', 'location', 'quote', 'profile', 'futuresProduct', 'settlementTarget', 'priceRule', 'markRole', 'missingPrice')}
    rates = {**blank, 'code': 'usd_zero', 'name': 'Synthetic zero rates', 'contractKind': 'interest_rate', 'markSource': 'fixture',
             'interpolation': 'log_linear_discount', 'currency': 'usd', 'markUnit': 'percent_per_year', 'dayCount': 'actual_365_fixed',
             'compounding': 'continuous', 'extrapolation': 'flat_forward', 'contracts': {'mappingId': tenors['id'], 'mappingVersion': 1, 'count': 2}}
    shown = preview(rates)
    assert [(m['label'], m['maturity'], m['days']) for m in shown['maturities']] == [('1D', '2021-01-18', 3), ('3M', '2021-04-15', 90)], shown
    assert shown['valuationDate'] == '2021-01-15' and shown['terms']['currency'] == 'usd', shown
    assert shown['terms']['conventions'] == {'dayCount': 'actual_365_fixed', 'compounding': 'continuous', 'interpolation': 'log_linear_discount',
                                             'extrapolation': 'flat_forward', 'markUnit': 'percent_per_year'}, shown
    assert preview({**rates, 'compounding': 'semi_annual'}, expected=422)['code'] == 'unsupported_rate_convention'
    assert [m['maturity'] for m in preview(rates, '2021-02-15')['maturities']] == ['2021-02-16', '2021-05-16'], 'The tenors did not roll'
    assert preview({**rates, 'quote': 'test_gas_outright'}, expected=422)['code'] == 'curve_definition'

    def save(body, who=editor, expected=200):
        return who.call('curve-definitions/save', {'snapshotId': close(who, '2021-01-15'), **body}, expected=expected)
    def publish(receipt, who=approver, expected=200, request=None):
        return who.call('curve-definitions/publish', {'requestId': request or str(uuid4()), **{k: receipt[k] for k in ('id', 'version', 'generation')}}, expected=expected)
    def listed(who=viewer):
        return who.call('curve-definitions')['definitions']

    first = {'requestId': str(uuid4()), 'definition': rates}
    save(first, who=viewer, expected=403)
    saved = save(first)
    assert saved['state'] == 'draft' and saved['version'] == 1, saved
    assert save(first) == saved, 'A retried save returned a different receipt'
    assert save({**first, 'definition': {**rates, 'name': 'Changed'}}, expected=409)['code'] == 'curve_definition_conflict'
    assert not any(d['id'] == saved['id'] for d in listed()), 'A reader sees a draft'
    publish(saved, who=editor, expected=403)
    published = publish(saved)
    assert published['state'] == 'published' and any(d['id'] == saved['id'] and d['state'] == 'published' for d in listed())

    # Editing a published version starts a new draft; a stale generation is refused.
    edit = lambda receipt, state, **extra: {'requestId': str(uuid4()), 'id': receipt['id'], 'version': receipt['version'],
                                           'generation': receipt['generation'], 'expectedState': state, **extra}
    save(edit({**published, 'generation': 9}, 'published', definition=rates), expected=409)
    # Inline tenor rows save as a draft of the curve's date list, which the curve draft pins; the published curve keeps version 1.
    rows = [{'label': '1D', 'date': 'oe.rate_days + 1bd'}, {'label': '3M', 'date': 'oe.rate_days[90]'}, {'label': '6M', 'date': 'oe.rate_days[181]'}]
    mapping_edit = {'id': tenors['id'], 'version': 1, 'generation': tenors['generation'], 'expectedState': 'published',
                    'code': 'rate_tenors', 'name': 'Synthetic rate tenors', 'contracts': None, 'dates': rows}
    widened = save(edit(published, 'published', definition={**rates, 'contracts': {**rates['contracts'], 'count': 3}}, mapping=mapping_edit))
    assert widened['version'] == 2 and widened['state'] == 'draft', widened
    versions = {(d['id'], d['version']): d for d in listed(editor)}
    assert versions[(saved['id'], 2)]['definition']['contracts']['mappingVersion'] == 2
    assert versions[(saved['id'], 1)]['definition']['contracts']['mappingVersion'] == 1
    mapping_state = lambda version: next(v['state'] for v in editor.call('date-schedules')['contractDefinitions'] if v['id'] == tenors['id'] and v['version'] == version)
    assert mapping_state(2) == 'draft'
    # A curve publish that fails leaves its mapping draft unpublished, in the same transaction.
    publish({**widened, 'generation': widened['generation'] + 1}, expected=409)
    assert mapping_state(2) == 'draft', 'A failed curve publish published its mapping'
    # A concurrent Settings edit of the mapping draft makes the curve's pinned generation stale.
    settings = editor.call('date-schedules/contracts/save', {'requestId': str(uuid4()), 'code': 'rate_tenors', 'name': 'Synthetic rate tenors edited',
        'context': context('2021-01-15'), 'contracts': None, 'dates': rows, 'id': tenors['id'], 'version': 2,
        'generation': versions[(saved['id'], 2)]['mappingGeneration'], 'expectedState': 'draft'})
    publish(widened, expected=409)
    stale_inline = edit(widened, 'draft', definition={**rates, 'contracts': {**rates['contracts'], 'count': 3}},
                        mapping={**mapping_edit, 'version': 2, 'generation': versions[(saved['id'], 2)]['mappingGeneration'], 'expectedState': 'draft'})
    assert save(stale_inline, expected=409)['code'] == 'contract_date_conflict'
    current = save(edit(widened, 'draft', definition={**rates, 'contracts': {**rates['contracts'], 'count': 3}},
                        mapping={**mapping_edit, 'version': 2, 'generation': settings['generation'], 'expectedState': 'draft'}))
    final = publish(current)
    assert final['state'] == 'published' and mapping_state(2) == 'published', 'Publishing the curve must publish its mapping draft first'
    assert [m['label'] for m in preview({**rates, 'contracts': {**rates['contracts'], 'mappingVersion': 2, 'count': 3}})['maturities']] == ['1D', '3M', '6M']

    # A contract curve round-trips too; another curve pinned to an earlier mapping version is unchanged.
    gas = save({'requestId': str(uuid4()), 'definition': future})
    assert publish(gas)['state'] == 'published'
    assert next(d for d in listed() if d['id'] == saved['id'] and d['version'] == 1)['definition']['contracts']['mappingVersion'] == 1
    # A knowledge view captured before these publications cannot see them.
    assert not any(d['id'] in (saved['id'], gas['id']) for d in historical.call('curve-definitions')['definitions'])
    historical.call('profile', {'profile': 'editor'})
    historical.call('market/context/retained', {'contextId': selected['contextId'], 'selection': 'latest_correction'})
    assert historical.call('curve-definitions/save', {'requestId': str(uuid4()), 'definition': {**rates, 'code': 'usd_zero_historical'},
        'snapshotId': close(historical, '2021-01-15')}, expected=422)['code'] == 'curve_definition_historical'
    # Risk workers keep capturing through the outer function only; the renamed inner capture is not theirs to call.
    assert query("SELECT has_function_privilege('openetrm_risk_worker','security.capture_date_context(uuid,date,timestamptz)','EXECUTE'),"
                 "has_function_privilege('openetrm_risk_worker','security.capture_pre_curve_definitions_context(uuid,date,timestamptz)','EXECUTE');") == 't|f'
    print('PASS curve definitions save, publish and edit with inline tenors, atomically and idempotently')

    # D359: admitting a published rate definition into a draft definition revision creates one series per tenor label.
    def draft_revision():
        revision = str(uuid4())
        query(f"INSERT INTO market_data.revisions VALUES('{cat}','{revision}','{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','draft',0);")
        return revision
    def tenors_of(revision):
        return json.loads(query(f"""SELECT json_agg(json_build_array(t.label,t.series_id,s.value_specification_code) ORDER BY t.ordinal)
            FROM market_data.rate_curve_tenors t JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(t.catalog_id,t.revision_id,t.series_id)
            WHERE t.catalog_id='{cat}' AND t.revision_id='{revision}';"""))
    first_revision = draft_revision()
    admit = {'requestId': str(uuid4()), 'revisionId': first_revision, 'definitionId': saved['id'], 'version': 1, 'valueSpecification': 'test_zero_rate_percent'}
    viewer.call('curve-definitions/admit', admit, expected=403)
    assert editor.call('curve-definitions/admit', {**admit, 'requestId': str(uuid4()), 'valueSpecification': 'test_zero_rate'},
                       expected=422)['code'] == 'rate_admission', 'A decimal-range specification admitted a percent curve'
    admitted = editor.call('curve-definitions/admit', admit)
    assert [t['label'] for t in admitted['tenors']] == ['1D', '3M'], admitted
    assert editor.call('curve-definitions/admit', admit) == admitted, 'A retried admission returned a different receipt'
    assert editor.call('curve-definitions/admit', {**admit, 'version': 2}, expected=409)['code'] == 'rate_admission_conflict'
    first = tenors_of(first_revision)
    assert [(label, spec) for label, _, spec in first] == [('1D', 'test_zero_rate_percent'), ('3M', 'test_zero_rate_percent')], first
    # A later revision reuses each label's series; a new version's added label gets a new series.
    second_revision = draft_revision()
    editor.call('curve-definitions/admit', {**admit, 'requestId': str(uuid4()), 'revisionId': second_revision, 'version': 2})
    second = tenors_of(second_revision)
    assert [label for label, _, _ in second] == ['1D', '3M', '6M'] and second[:2] == first and second[2][1] not in {s for _, s, _ in first}, second
    # A version that changes the mark unit gives every label a new series, so no history mixes percent and decimal marks.
    decimal = publish(save(edit(final, 'published', definition={**rates, 'markUnit': 'decimal_per_year', 'contracts': {**rates['contracts'], 'mappingVersion': 2, 'count': 3}})))
    decimal_revision = draft_revision()
    editor.call('curve-definitions/admit', {**admit, 'requestId': str(uuid4()), 'revisionId': decimal_revision, 'version': decimal['version'],
                                            'valueSpecification': 'test_zero_rate'})
    assert not {s for _, s, _ in tenors_of(decimal_revision)} & {s for _, s, _ in second}, 'A kept label kept its series across a mark unit change'
    # Only a published version is admitted.
    pending = save(edit(decimal, 'published', definition={**rates, 'name': 'Pending', 'contracts': {**rates['contracts'], 'mappingVersion': 2, 'count': 3}}))
    assert editor.call('curve-definitions/admit', {**admit, 'requestId': str(uuid4()), 'revisionId': draft_revision(), 'version': pending['version']},
                       expected=422)['code'] == 'rate_admission'
    print('PASS published rate definitions admit one stable series per tenor, idempotently and with the unit range')

    # D359: an admitted curve rolls with each close's t_0: its tenors keep their series and marks, and their maturities move.
    from decimal import Decimal, getcontext, ROUND_HALF_EVEN
    getcontext().prec = 50
    def factor(mark, days):  # continuous compounding, ACT/365 Fixed, from a percent mark; rounded once to 18 places
        return str((-(Decimal(mark) / 100) * days / Decimal(365)).exp().quantize(Decimal('1e-18'), ROUND_HALF_EVEN))
    for day, maturities, marks in [('2021-01-15', ['2021-02-14', '2022-01-15'], ['4.10', '4.30']),
                                   ('2021-02-15', ['2021-03-17', '2022-02-15'], ['4.20', '4.40'])]:
        shown = viewer.call('evaluate', {'snapshotId': close(viewer, day), 'curve': 'usd_rolling'})
        days = [30, 365]
        assert [(p['label'], p['maturity'], Decimal(p['mark']), p['actualDays']) for p in shown['points']] == list(zip(['1M', '1Y'], maturities, map(Decimal, marks), days)), shown['points']
        assert [p['discountFactor'] for p in shown['points']] == [factor(m, d) for m, d in zip(marks, days)], shown['points']
        assert shown['markUnitLabel'] == '% per year' and shown['definition']['version'] == 1 and shown['currency'] == 'usd', shown
    january = viewer.call('evaluate', {'snapshotId': close(viewer, '2021-01-15'), 'curve': 'usd_rolling'})['points']
    february = viewer.call('evaluate', {'snapshotId': close(viewer, '2021-02-15'), 'curve': 'usd_rolling'})['points']
    assert [p['seriesId'] for p in january] == [p['seriesId'] for p in february], 'A tenor changed series between closes'
    print('PASS a definition-driven rate curve rolls its tenors with each close, in its own unit and conventions')

    # The curve list offers a curve's own configuration only to someone who may change it.
    def offered(who):
        return who.call('workspace/navigation')['curveConfigurations']
    pinned = viewer.call('evaluate', {'snapshotId': close(viewer, '2021-01-15'), 'curve': 'usd_rolling'})['definition']
    assert offered(viewer) == [] and not viewer.call('curve-definitions')['canEdit'], offered(viewer)
    configurations = {c['curve']: c for c in offered(editor)}
    assert configurations['usd_rolling'] == {'curve': 'usd_rolling', 'id': pinned['id'], 'version': pinned['version']}, configurations
    listed_rates = {c['code'] for c in editor.call('workspace/navigation')['curves'] if c['marketType'] == 'rates'}
    assert set(configurations) == listed_rates, (configurations, listed_rates)
    print('PASS the curve list names a curve\'s configuration only for someone who may edit it')
