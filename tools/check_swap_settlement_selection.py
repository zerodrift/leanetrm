"""Synthetic exact-boundary checks for explicitly selected settlement indices."""
from copy import deepcopy
from datetime import date, datetime, timedelta, timezone
from fractions import Fraction
from uuid import uuid4

from preview_fixture import CAT, R, REV, identity

INDEX = 'synthetic_override_index'
UNQUALIFIED = 'synthetic_unqualified_index'
CURVE = 'synthetic_override_forward'
FIXING = '2020-12-31T12:00:00Z'
MISSING = '2020-12-31T12:00:01Z'
MONTHLY_ACTUAL = identity('7', 8900000)
MONTHLY_SERIES = identity('9', 89001)
FORWARD_SERIES = identity('9', 89002)
CHOICE = {'mode': 'override', 'index': INDEX, 'projectionPolicy': 'forward_curve_no_basis_v1'}


def _insert(market, definitions, observations):
    marker = 'UPDATE market_data.revisions SET'
    assert marker in market
    market = market.replace(marker, '\n'.join(definitions)+'\n'+marker, 1)
    return market.rsplit('COMMIT;', 1)[0]+'\n'+'\n'.join(observations)+'\nCOMMIT;\n'


def prepare_primary(market, manifest):
    """The target and override indices have distinct final series for one month."""
    references = [
        f"INSERT INTO ref_data.price_index_definitions SELECT catalog_id,revision_id,'{code}',commodity_code,location_code,currency_code,quote_unit_code,quote_multiplier,calendar_code FROM ref_data.price_index_definitions WHERE(catalog_id,revision_id,code)=({R},'test_price');"
        for code in (INDEX, UNQUALIFIED)]
    marker = 'UPDATE ref_data.revisions SET'
    assert marker in market
    market = market.replace(marker, '\n'.join(references)+'\n'+marker, 1)
    definitions = [
        f"INSERT INTO market_data.curves VALUES('{CAT}','{REV}','{CURVE}',{R},'test_gas','test_hub_a','test_gas_outright','test_uniform','test_index_target','direct','fixture','trader_mark','Synthetic override forward');",
        *[f"INSERT INTO market_data.contracts VALUES('{CAT}','{REV}','{identity('5', number)}',{R},'forward','2021-01-01','2021-02-01','2021-02-01T00:00:00Z',NULL);" for number in (89001, 89002)],
        f"INSERT INTO market_data.curve_members VALUES('{CAT}','{REV}','{CURVE}','{identity('5', 89002)}',0);",
        f"INSERT INTO market_data.series VALUES('{CAT}','{REV}','{FORWARD_SERIES}','synthetic_override_forward',{R},'{identity('5', 89002)}','test_gas_outright',NULL);",
        f"INSERT INTO market_data.series VALUES('{CAT}','{REV}','{MONTHLY_SERIES}','synthetic_override_monthly_actual',{R},'{identity('5', 89001)}','test_gas_outright',NULL);",
        f"INSERT INTO market_data.index_fixing_observations VALUES('{CAT}','{REV}','{INDEX}','2021-01-01','2021-02-01',{R},'test_gas_outright','{MONTHLY_SERIES}','fixture','{FIXING}','published_index_observations_v1');"]
    base = next(frame for frame in manifest['frames'] if frame['at'] == '2020-12-18T16:00:00Z')
    manifest['frames'].extend({**base, 'at': at, 'observationIds': list(base['observationIds'])} for at in (FIXING, MISSING))
    now = datetime.now(timezone.utc).isoformat()
    observations = [f"INSERT INTO market_data.observations VALUES('{CAT}','{MONTHLY_ACTUAL}','{REV}','{MONTHLY_SERIES}',{R},'fixture','synthetic_override_monthly_final','1','index_fixing','final','{FIXING}','{now}',5.25,NULL);"]
    for ordinal, frame in enumerate(manifest['frames']):
        forward = identity('7', 8910000+ordinal)
        observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{forward}','{REV}','{FORWARD_SERIES}',{R},'fixture','synthetic_override_mark_{ordinal}','1','trader_mark','final','{frame['at']}','{now}',4,NULL);")
        frame['observationIds'] = [*frame['observationIds'], forward]
        if frame['at'] >= FIXING and frame['at'] != MISSING:
            frame['observationIds'].append(MONTHLY_ACTUAL)
    return _insert(market, definitions, observations)


def prepare_daily(market, manifest):
    """Each daily override fixing has its own immutable series and final fact."""
    revision = manifest['revisionId']
    definitions = []
    actuals = []
    now = datetime.now(timezone.utc).isoformat()
    observations = []
    for day in range(1, 32):
        start = date(2021, 1, day); end = start+timedelta(days=1)
        fixing = f'{start-timedelta(days=1)}T12:00:00Z'
        series = identity('9', 89100+day); actual = identity('7', 8920000+day)
        definitions += [
            f"INSERT INTO market_data.contracts VALUES('{CAT}','{revision}','{identity('5', 89100+day)}',{R},'forward','{start}','{end}','{end}T00:00:00Z',NULL);",
            f"INSERT INTO market_data.series VALUES('{CAT}','{revision}','{series}','synthetic_override_daily_{day}',{R},'{identity('5', 89100+day)}','test_gas_outright',NULL);",
            f"INSERT INTO market_data.index_fixing_observations VALUES('{CAT}','{revision}','{INDEX}','{start}','{end}',{R},'test_gas_outright','{series}','fixture','{fixing}','published_index_observations_v1');"]
        observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{actual}','{revision}','{series}',{R},'fixture','synthetic_override_daily_final_{day}','1','index_fixing','final','{fixing}','{now}',4.{day:02d},NULL);")
        actuals.append((fixing, actual))
    base = next(frame for frame in manifest['frames'] if frame['at'] == '2020-12-18T16:00:00Z')
    manifest['frames'].extend({**base, 'at': at, 'observationIds': list(base['observationIds'])} for at in (FIXING, MISSING))
    for frame in manifest['frames']:
        frame['observationIds'] = [*frame['observationIds'], *(actual for fixing, actual in actuals if fixing <= frame['at'] and frame['at'] != MISSING)]
    return _insert(market, definitions, observations)


def run_checks(client, query, manifest, frames):
    from check_index_fixings import booking, draft, swap_index
    from check_blotter_valuation_status import risk_request

    before = frames['2020-12-18T16:00:00']
    at_fixing = frames[FIXING[:19]]
    missing = frames[MISSING[:19]]
    book = str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic explicit settlement indices',publication_order,effective_at FROM trading.books LIMIT 1;")
    saved = []
    for curve, projected, fixed, actual_count, factor_count in (
            (CURVE, Fraction(100), Fraction(225), 1, 1),
            ('daily_gas', Fraction(16), Fraction(596, 31), 1, 31)):
        index = swap_index(client, before, curve)[0]
        terms = draft(before, index, curve, '3')
        target = client.call('swaps/price', terms)
        terms['floating']['settlement'] = deepcopy(CHOICE)
        priced = client.call('swaps/price', terms)
        assert Fraction(priced['valuation']['presentValue']) == projected == Fraction(target['valuation']['presentValue'])
        request = {'draft': {**terms, 'context': {**terms['context'], 'knowledgeMode': 'retained', 'contextId': priced['context']['contextId']}},
                   'bumpPolicy': priced['bumpPolicy'], 'numericalPolicy': priced['numericalPolicy']}
        risk = client.call('swaps/draft-risk', request)
        evidence = risk['projectionPolicies']
        assert len(evidence) == 1 and evidence[0]['settlement']['selectedIndex']['code'] == INDEX
        assert evidence[0]['settlement']['projectionPolicy'] == 'forward_curve_no_basis_v1'
        assert evidence[0]['label'] == 'Curve risk with unmodelled basis to '+INDEX
        assert len(risk['factors']) == factor_count and risk['scenarioCount'] == 1+2*factor_count
        assert all(Fraction(row['basePv']) == projected and Fraction(row['delta']) == Fraction(100, factor_count) and Fraction(row['gamma']) == 0 for row in risk['factors'])
        command = booking(terms, book)
        receipt = client.call('swaps/book', command)
        assert client.call('swaps/book', command) == receipt
        detail = client.call('swaps/editor', {'context': terms['context'], 'tradeId': receipt['tradeId'], 'revision': None})
        assert detail['state']['terms']['floating']['settlement'] == CHOICE
        assert Fraction(detail['valuation']['presentValue']) == projected
        saved.append((receipt['tradeId'], terms, risk['factors'][0]['factor'], Fraction(100, factor_count)))

        due = {**terms, 'context': {**terms['context'], 'snapshotId': at_fixing['id']}}
        value = client.call('swaps/price', due)
        assert Fraction(value['valuation']['presentValue']) == fixed
        resets = value['valuation']['sides'][1]['payments'][0]['resets']
        assert sum(row['fixingState'] == 'Actual' for row in resets) == actual_count
        if curve == CURVE:
            assert resets[0]['price'] == '5.25' and [row['id'] for row in resets[0]['lineage']] == [MONTHLY_ACTUAL]
        else:
            assert resets[0]['price'] == '4.01' and resets[0]['lineage'][0]['id'] == identity('7', 8920001)
        retained_due = {**due, 'context': {**due['context'], 'knowledgeMode': 'retained', 'contextId': value['context']['contextId']}}
        due_risk = client.call('swaps/draft-risk', {'draft': retained_due, 'bumpPolicy': value['bumpPolicy'], 'numericalPolicy': value['numericalPolicy']})
        assert len(due_risk['factors']) == factor_count-actual_count
        assert all(Fraction(row['basePv']) == fixed and Fraction(row['delta']) == Fraction(100, factor_count) for row in due_risk['factors'])
        if curve == 'daily_gas':
            final = {**terms, 'context': {**terms['context'], 'snapshotId': frames['2021-01-31T08:00:00']['id']}}
            final_price = client.call('swaps/price', final)
            assert Fraction(final_price['valuation']['presentValue']) == 116
            final['context'] = {**final['context'], 'knowledgeMode': 'retained', 'contextId': final_price['context']['contextId']}
            final_risk = client.call('swaps/draft-risk', {'draft': final, 'bumpPolicy': final_price['bumpPolicy'], 'numericalPolicy': final_price['numericalPolicy']})
            assert final_risk['factors'] == [] and final_risk['scenarioCount'] == 1
        missing_terms = {**due, 'context': {**due['context'], 'snapshotId': missing['id']}}
        publications = query('SELECT count(*) FROM trading.publications;')
        for path, body in [('swaps/price', missing_terms), ('swaps/book', booking(missing_terms, book))]:
            assert client.call(path, body, expected=422)['code'] == 'index_fixing_missing'
        assert query('SELECT count(*) FROM trading.publications;') == publications
        for selection, code in (
                ({**CHOICE, 'projectionPolicy': None}, 'price_settlement_selection'),
                ({**CHOICE, 'index': index['index']}, 'price_settlement_selection'),
                ({**CHOICE, 'index': UNQUALIFIED}, 'index_fixing_cadence'),
                ({**CHOICE, 'index': 'test_power_price'}, 'price_settlement_selection')):
            rejected = {**terms, 'floating': {**terms['floating'], 'settlement': selection}}
            assert client.call('swaps/price', rejected, expected=422)['code'] == code
        custom = {'catalogId': manifest['catalogId'], 'curveId': str(uuid4()), 'version': 1, 'outputNode': 'output',
                  'position': 0 if curve == CURVE else None,
                  'selection': 'single_output_position_v1' if curve == CURVE else 'exact_delivery_interval_v1',
                  'implementation': 'published_custom_curve_projection_v1'}
        assert client.call('swaps/price', {**terms, 'floating': {**terms['floating'], 'customProjection': custom}}, expected=422)['code'] == 'price_settlement_selection'
        options = client.call('swaps/observation-options', {'context': terms['context'], 'definitionRevisionId': terms['definitionRevisionId'],
            'commodity': terms['commodity'], 'curve': curve, 'index': index['index'], 'resetFrequency': terms['floating']['resetFrequency'],
            'averagingMethod': terms['floating']['averagingMethod'], 'deliveryFrom': '2021-01-01', 'deliveryTo': '2021-02-01', 'settlement': CHOICE})
        assert options['availability'] is None and len(options['observations']) == factor_count
        assert all(row['fixing']['index']['code'] == INDEX for row in options['observations'])
        assert 'unmodelled' in options['customProjectionUnavailableReason']
        print(f'PASS explicit settlement: {curve} projects its forward, selects its exact final at the fixing instant, freezes realized risk, retains booked choice and rejects missing/custom inputs', flush=True)

    # Book risk uses the same retained selections as the draft checks. Each
    # scenario moves only the position reading that primitive forward quote.
    client.call('profile', {'profile': 'risk'})
    context = {**saved[0][1]['context'], 'convention': 'trading_workspace_v1'}
    catalog = client.call('portfolio/risk/catalog', {'context': context, 'bookIds': [book], 'portfolioIds': []})
    context = {**context, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']}
    for trade, _, displayed_factor, delta in saved:
        factor = next(row['factor'] for row in catalog['factors'] if row['seriesId'] == displayed_factor['seriesId']
                      and row['contractId'] == displayed_factor['contract']['id'])
        request = risk_request(catalog, context, factor)
        request.update(bookIds=[book], portfolioIds=[])
        report = client.call('portfolio/risk', request)
        assert len(report['projectionPolicies']) == 2
        assert all(policy['settlement']['selectedIndex']['code'] == INDEX and
                   policy['label'] == 'Curve risk with unmodelled basis to '+INDEX for policy in report['projectionPolicies'])
        values = {row['tradeId']: row['values'] for row in report['factors'][0]['positions']}
        assert set(values) == {row[0] for row in saved}
        assert Fraction(values[trade]['delta']) == delta
        assert all(Fraction(value['delta']) == 0 for other, value in values.items() if other != trade)
    client.call('profile', {'profile': 'trader'})
    print('PASS explicit settlement: booked mixed monthly/daily quote risk preserves independent scenario dependencies', flush=True)
