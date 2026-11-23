"""Which installed roles reach operating facilities and operating rights through the public routes."""
from uuid import uuid4
from exact_values import fraction
from preview_client import PreviewClient, initial_frame

FACILITIES, TRADES = 'physical/power-facilities/', 'physical/power-trades/'
# The preview's built-in people hold one role each; every other role gets a person created for it.
BUILT_IN = dict(viewer='viewer', market_data_editor='editor', market_data_approver='approver', trader='trader',
                risk_analyst='risk')
TRADE_READERS = ('facility_operator', 'facility_limits_admin', 'gas_operator', 'gas_service_admin',
                 'contracts_officer', 'credit_officer', 'trade_confirmations', 'trade_supervisor')
# What a person holding exactly one installed role may do:
#   facilities  read the facility directory and exact versions
#   setup       save a facility version and recover that save
#   rights      read saved operating-right trades, their value and their positions
#   booking     review and book an operating right
#   scenarios   run a supplied-plan preview and the hedge report
ACCESS = {
    'market_data_editor': {'facilities', 'setup'},
    'trader': {'facilities', 'rights', 'booking'},
    'risk_analyst': {'facilities', 'rights', 'scenarios'},
    **{role: {'facilities', 'rights'} for role in TRADE_READERS},
    'viewer': set(),
    'market_data_approver': set(),
}
# The preview gives these two roles RunScenario for its own pages, which an installation does not. Their
# scenario routes are therefore asserted through the roles the preview leaves as installed.
PREVIEW_SCENARIO_ROLES = {'trader', 'market_data_editor'}


def run_checks(url, query, facility_command, plan, operating):
    """`facility_command` saved the battery, `plan` is its supplied plan and `operating` names the booked right."""
    client = PreviewClient(url)
    client.bootstrap()

    def check(label, condition=True):
        assert condition, label

    created = set()

    def become(role):
        if role not in BUILT_IN and role not in created:
            client.call('profile', {'profile': 'admin'})
            client.call('admin/create', dict(operationId=str(uuid4()), login='operating_access_' + role,
                                             name='Synthetic operating access check', kind='human', roles=[role]))
            created.add(role)
        client.call('profile', {'profile': BUILT_IN.get(role, 'operating_access_' + role)})
        return initial_frame(client.call('workspace'), '2020-12-15')['id']

    def refused(path, body):
        return client.call(path, body, expected=403)['code'] == 'access_denied'

    facility, trade, book = facility_command['facility'], operating['tradeId'], operating['bookId']
    retry = {}      # each person's own facility save, recovered by its request identity
    booking = {}    # the booking command a booker prepares from the choices they are offered
    hedge = {}      # the hedge report request a scenario reader prepares

    def facilities(role, frame, may):
        directory = dict(snapshotId=frame, selectionPolicy='all_known_versions_v1')
        exact = dict(snapshotId=frame, facilityId=facility['id'], version=facility['version'])
        if 'facilities' in may:
            catalog = client.call(FACILITIES + 'catalog', {'snapshotId': frame})
            check(role + ' is offered setup only with setup authority', catalog['canSave'] is ('setup' in may))
            check(role + ' lists the saved facility', any((row['id'], row['version']) == (facility['id'], facility['version'])
                                                         for row in client.call(FACILITIES + 'list', directory)))
            check(role + ' reads the exact facility version', client.call(FACILITIES + 'get', exact)['id'] == facility['id'])
        else:
            check(role + ' reads no operating facility', refused(FACILITIES + 'catalog', {'snapshotId': frame})
                  and refused(FACILITIES + 'list', directory) and refused(FACILITIES + 'get', exact))

    def setup(role, frame, may):
        command = dict(facility_command, snapshotId=frame, requestId=retry.setdefault(role, str(uuid4())))
        status = {'requestId': command['requestId']}
        if 'setup' in may:
            receipt = client.call(FACILITIES + 'save', command)
            check(role + ' saves a facility version, here an already recorded fact',
                  not receipt['changed'] and receipt['facility']['id'] == facility['id'])
            check(role + ' recovers its own save', client.call(FACILITIES + 'status', status) == receipt)
        else:
            check(role + ' cannot set up an operating facility',
                  refused(FACILITIES + 'save', command) and refused(FACILITIES + 'status', status))

    def scenarios(role, frame, may):
        preview = dict(plan, snapshotId=frame)
        context = dict(snapshotId=frame, convention='trading_workspace_v1', knowledgeMode='current')
        if 'scenarios' in may:
            check(role + ' previews the supplied plan', fraction(client.call(FACILITIES + 'preview', preview)['valuation']['value']) == 610)
            risk = client.call('portfolio/risk/catalog', dict(context=context, bookIds=[book], portfolioIds=[],
                                                               tradeIds=[trade, operating['hedge']['tradeId']]))
            factor = next(t for t in risk['scenarioTargets'] if t['kind'] != 'delivery_bucket'
                          and t['factor']['target']['curve']['code'] == operating['hedge']['curveCode'])
            hedge.update(context=dict(context, knowledgeMode='retained', contextId=risk['context']['contextId']),
                bookIds=[book], operatingTradeId=trade, hedgeTradeIds=[operating['hedge']['tradeId']],
                bumps=[dict(factor=factor['factor'], size='1', convention=risk['bumpConvention'])],
                convention='operating_right_price_hedge_v1', numericalPolicy=risk['numericalPolicy'],
                pricingPolicy=risk['pricingPolicy'], aggregationPolicy='shared_price_changes_v1')
            check(role + ' reads the hedge report of the operating right',
                  len(client.call(TRADES + 'hedges', hedge)['valuation']['positions']) == 2)
        elif role not in PREVIEW_SCENARIO_ROLES:
            check(role + ' runs no supplied-plan preview or hedge report',
                  refused(FACILITIES + 'preview', preview) and refused(TRADES + 'hedges', hedge))

    def rights(role, frame, may):
        selected = dict(snapshotId=frame, tradeId=trade)
        directory = dict(snapshotId=frame, bookIds=[book], offset=0, limit=50)
        positions = dict(context=dict(snapshotId=frame, convention='trading_workspace_v1', knowledgeMode='current'),
                         bookIds=[book], portfolioIds=[], offset=0, limit=64)
        if 'rights' in may:
            catalog = client.call(TRADES + 'catalog', {'snapshotId': frame})
            check(role + ' is offered booking only with booking authority', catalog['canBook'] is ('booking' in may))
            if 'booking' in may and not booking:
                booking.update(operationId=str(uuid4()), tradeId=None, expectedRevision=0, bookId=book,
                    legalEntityId=catalog['legalEntities'][0]['id'], legalEntityRevision=catalog['legalEntities'][0]['revision'],
                    counterpartyId=catalog['counterparties'][0]['id'], counterpartyRevision=catalog['counterparties'][0]['revision'],
                    reason='Synthetic supplied operating-right contract', convention=catalog['convention'], plan=dict(plan))
            check(role + ' lists the saved operating right',
                  any(t['tradeId'] == trade for t in client.call(TRADES + 'directory', directory)['trades']))
            check(role + ' opens the saved operating right', client.call(TRADES + 'get', selected)['tradeId'] == trade)
            check(role + ' values the saved operating right',
                  fraction(client.call(TRADES + 'valuation', selected)['valuation']['value']) == 610)
            check(role + ' reads the position of the operating right', any(
                p['tradeId'] == trade and p['instrumentType'] == 'operating_right'
                for p in client.call('trading/positions', positions)['positions']))
        else:
            check(role + ' reads no operating right', refused(TRADES + 'catalog', {'snapshotId': frame})
                  and refused(TRADES + 'directory', directory) and refused(TRADES + 'get', selected)
                  and refused(TRADES + 'valuation', selected))

    def bookings(role, frame, may):
        command = dict(booking, plan=dict(booking['plan'], snapshotId=frame))
        status = {'operationId': command['operationId']}
        if 'booking' in may:
            check(role + ' reviews the operating right', fraction(client.call(TRADES + 'preview', command)['valuation']['value']) == 610)
            receipt = client.call(TRADES + 'book', command, timeout=60)
            check(role + ' books the operating right, here an already recorded fact',
                  not receipt['changed'] and receipt['tradeId'] == trade)
            check(role + ' recovers its own booking', client.call(TRADES + 'status', status) == receipt)
        else:
            check(role + ' cannot book an operating right', refused(TRADES + 'preview', command)
                  and refused(TRADES + 'book', command) and refused(TRADES + 'status', status))

    # The people who prepare a booking and a hedge report go first: the others are refused the same requests.
    for role in sorted(ACCESS, key=lambda name: ('booking' not in ACCESS[name], 'scenarios' not in ACCESS[name], name)):
        frame, may = become(role), ACCESS[role]
        for capability in (facilities, setup, rights, bookings, scenarios):
            capability(role, frame, may)
        print(f"PASS operating access HTTP: {role} holds exactly {', '.join(sorted(may)) or 'no operating access'}", flush=True)

    frame = become('trade_confirmations')
    check('the confirmations desk lists the operating right it confirms',
          any(row['trade_id'] == trade for row in client.call('desk/directory', {'snapshotId': frame})['trades']))

    # A trade reader without the read capability is refused every position read over a book that holds an
    # operating right, not only the right itself.
    role = query("SELECT id FROM security.roles WHERE code='gas_service_admin';")
    query(f"DELETE FROM security.role_permissions WHERE(role_id,permission)=('{role}','ReadPhysical');")
    try:
        frame = become('gas_service_admin')
        check('a trade reader without Physical read authority is refused the whole position read of that book',
              refused('trading/positions', dict(context=dict(snapshotId=frame, convention='trading_workspace_v1',
                      knowledgeMode='current'), bookIds=[book], portfolioIds=[], offset=0, limit=64)))
    finally:
        query(f"INSERT INTO security.role_permissions VALUES('{role}','ReadPhysical');")
    print('PASS operating access HTTP: the confirmations desk lists an operating right, and a trade reader without '
          'the read capability is refused the whole position read of its book', flush=True)
