"""Financial-state applicability and future-head controls over exact source cuts."""
from copy import deepcopy


def future_capacity(f, trade, state, rejected):
    previous_at = f.at
    f.at = '2020-12-20T23:59:59.999999Z'
    try:
        capacity = f.read(f.entitlement)
        definition = deepcopy(f.operational_definition)
        definition.update(expectedStateVersion=capacity['stateVersion'], expectedPreviousId=capacity['version']['id'],
            evidence='Synthetic next-date operational capacity applicability')
        definition['agreement'][0]['maximumPerHour'] = '85'
        version = f.record(definition, effective='2020-12-20T00:00:00Z')['versionId']
        body = dict(kind='adopt_capacity_version', tradeId=trade, expectedFinancialStateId=state,
            capacityVersionId=version, evidence='Synthetic explicit future-applicable capacity adoption')
        rejected(lambda: f.record_financial(body, effective=f.valuation_at, at='2020-12-20T00:00:00Z'), 'gas_transport_financial_state_time')
        return body
    finally:
        f.at = previous_at


def accept_future_capacity(f, body):
    from gas_transport_financial_managed import checkpoint, value
    historical = f.capture()
    old = f.read_financial(body['tradeId'], context=historical)
    previous_at = f.at
    f.at = '2020-12-20T23:59:59.999999Z'
    try:
        at = '2020-12-20T00:00:00Z'
        accepted = f.record_financial(body, effective=at, at=at)
        visible = f.read_financial(body['tradeId'], at=at)
        assert visible['financialState']['id'] == accepted['financialStateId']
        assert visible['financialState']['capacityVersionId'] == body['capacityVersionId']
        assert visible['unpricedReason'] == 'rights_mark_required'
        value(f, body['tradeId'], rate_ids=[], at=at, reason='rights_mark_required')
    finally:
        f.at = previous_at
    unavailable = f.read_financial(body['tradeId'])
    assert unavailable['financialState'] is None and unavailable['mark'] is None
    assert unavailable['unpricedReason'] == 'financial_state_future'
    assert unavailable['terms']['from'] is None and unavailable['terms']['through'] is None
    assert unavailable['cash'] == old['cash']
    checkpoint(f, body['tradeId'], 'read', status=None, reason='financial_state_future')
    value(f, body['tradeId'], rate_ids=[], reason='financial_state_future')
    assert f.read_financial(body['tradeId'], context=historical)['financialState'] == old['financialState']
    print('PASS a capacity version cannot apply financially before its own applicability; valid exact activation is accepted, future financial head does not revive the old state, and historical knowledge retains the original state.', flush=True)
    return accepted
