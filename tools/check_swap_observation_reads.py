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
    # These tables supply only explicitly retained new-mode schedules. An old
    # side has no such relationship, so its read must not consume those tables.
    query('REVOKE SELECT ON trading.swap_observation_schedules,trading.swap_observation_quantities FROM openetrm_runtime;')
    try:
        after = client.call('swaps/editor', request)
        assert after['state']['terms'] == before['state']['terms']
        assert after['valuation'] == before['valuation'] and after['history'] == before['history']
        denied = client.call('swaps/editor', {**request, 'tradeId': observation_trade}, expected=403)
        assert denied['code'] == 'access_denied'
    finally:
        query('GRANT SELECT ON trading.swap_observation_schedules,trading.swap_observation_quantities TO openetrm_runtime;')
    print('PASS swap read scope: legacy sides do not consume unused observation tables; required exact schedules retain access checks', flush=True)
