"""Published synthetic forecasts remain separate from contractual index actuals."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4


def run(c, context, terms, definition, published, projection, saved):
    # The synthetic two-date mean uses independently specified fixture marks:
    # December17=3.09 and December18=3.10; quantity100 and fixed3 give9.5.
    initial = c.call('swaps/editor', dict(context=context, tradeId=saved['tradeId'], revision=None))
    assert Fraction(initial['valuation']['presentValue']) == Fraction(19, 2)
    successor = deepcopy(definition)
    for node in successor['nodes']:
        if node['kind'] == 'source':
            node['parameters']['curve'] = 'local_power'
    c.call('profile', {'profile': 'editor'})
    draft = c.call('custom-curves/save', dict(requestId=str(uuid4()), snapshotId=context['snapshotId'],
        id=published['id'], version=published['version'], generation=published['generation'], expectedState=published['state'],
        definition=successor, layout={}))
    c.call('profile', {'profile': 'approver'})
    second = c.call('custom-curves/publish', dict(requestId=str(uuid4()), id=draft['id'], version=draft['version'], generation=draft['generation']))
    assert second['version'] == 2
    c.call('profile', {'profile': 'trader'})
    reopened = c.call('swaps/editor', dict(context=context, tradeId=saved['tradeId'], revision=None))
    assert Fraction(reopened['valuation']['presentValue']) == Fraction(19, 2)
    assert reopened['state']['terms']['scheduled']['second']['periods'][0]['floating']['customProjection'] == projection
    incompatible = deepcopy(terms)
    incompatible['scheduled']['second']['periods'][0]['floating']['customProjection'] = {
        **projection, 'position': None, 'selection': 'exact_delivery_interval_v1'}
    assert c.call('swaps/preview', incompatible, expected=422)['code'] == 'custom_projection'
    unavailable = deepcopy(terms)
    unavailable['scheduled']['second']['periods'][0]['floating']['customProjection'] = {**projection, 'version': 64}
    assert c.call('swaps/preview', unavailable, expected=422)['code'] in ('custom_projection', 'custom_curve_definition')
    later = next(frame for frame in c.call('workspace')['frames'] if frame['at'].startswith('2021-01-15T08:00:00'))
    later_context = {**context, 'snapshotId': later['id']}
    actual = c.call('swaps/editor', dict(context=later_context, tradeId=saved['tradeId'], revision=None))
    assert Fraction(actual['valuation']['presentValue']) == 50  # (published index3.5-fixed3)*100
    resets = next(side for side in actual['valuation']['sides'] if side['kind'] == 'floating')['payments'][0]['resets']
    assert len(resets) == 1 and resets[0]['fixingState'] == 'Actual' and Fraction(resets[0]['price']) == Fraction(7, 2)
    floating = terms['scheduled']['second']['periods'][0]['floating']
    options_request = dict(context=later_context,
        definitionRevisionId=terms['definitionRevisionId'], commodity=terms['commodity'],
        curve=floating['curve'], index=floating['index'], settlement=floating['settlement'],
        resetFrequency='monthly', averagingMethod='none', deliveryFrom='2021-01-01', deliveryTo='2021-02-01',
        customProjection=projection)
    options = c.call('swaps/observation-options', options_request)
    assert any(period['deliveryFrom'] == '2021-01-01' for period in options['deliveryPeriods']), options
    assert options['qualifiedCustomProjection'] == projection, options
    absent = c.call('swaps/observation-options', {**options_request, 'customProjection': {**projection, 'version': 64}}, expected=422)
    assert absent['code'] == 'custom_curve_definition', absent
    for invalid in ({**projection, 'version': 2}, {**projection, 'position': 999}):
        malformed = c.call('swaps/observation-options', {**options_request, 'customProjection': invalid})
        assert malformed['deliveryPeriods'] == [] and malformed['availability']['code'] in ('custom_projection', 'custom_curve_definition'), malformed
        assert malformed['qualifiedCustomProjection'] is None, malformed
    print('PASS scheduled synthetic source: independent forecast cash, pinned published version, incompatible mapping refusal and actual-index precedence')
