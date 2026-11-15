"""Only exact retained observation schedules are read for a selected swap."""
from copy import deepcopy
from uuid import uuid4


def run_checks(client, query, context, booking, observation_trade):
    legacy = deepcopy(booking)
    legacy['operationId'] = str(uuid4())
    legacy['floating'].pop('observationSchedule')
    legacy['floating']['averagingMethod'] = 'arithmetic'
    saved = client.call('swaps/book', legacy)
    request = {'context': context, 'tradeId': saved['tradeId'], 'revision': None}
    captured = client.call('swaps/editor', request)
    request['context'] = {**context, 'knowledgeMode': 'retained', 'contextId': captured['context']['contextId']}
    before = client.call('swaps/editor', request)
    observation_request = {**request, 'tradeId': observation_trade}
    observation_before = client.call('swaps/editor', observation_request)
    # Neither ordinary side consumes the scheduled-swap projection. Only the
    # explicitly retained observation side consumes the observation relations.
    for relation in ('trading.scheduled_swap_term_projection', 'trading.swap_observation_schedules',
                     'trading.swap_observation_quantities'):
        query(f'REVOKE SELECT ON {relation} FROM openetrm_runtime;')
        try:
            after = client.call('swaps/editor', request)
            assert after['state']['terms'] == before['state']['terms'], relation
            assert after['valuation'] == before['valuation'] and after['history'] == before['history'], relation
            if relation == 'trading.scheduled_swap_term_projection':
                observed = client.call('swaps/editor', observation_request)
                assert observed['state']['terms'] == observation_before['state']['terms']
                assert observed['valuation'] == observation_before['valuation']
            else:
                denied = client.call('swaps/editor', observation_request, expected=403)
                assert denied['code'] == 'access_denied'
        finally:
            query(f'GRANT SELECT ON {relation} TO openetrm_runtime;')
    restored = client.call('swaps/editor', observation_request)
    assert restored['state']['terms'] == observation_before['state']['terms']
    assert restored['valuation'] == observation_before['valuation']
    print('PASS swap read scope: legacy sides do not consume unused observation tables; required exact schedules retain access checks', flush=True)
