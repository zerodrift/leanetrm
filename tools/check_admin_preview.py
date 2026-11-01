"""Workspace administration behavior; only run in the owned preview check suite."""
from uuid import uuid4


def run_checks(Client, check):
    viewer = Client(); viewer.bootstrap(); viewer.call('profile', {'profile': 'viewer'})
    viewer.call('admin', expected=403)
    check('business readers cannot enumerate identities or permissions')
    admin = Client(); admin.bootstrap(); admin.call('profile', {'profile': 'admin'})
    directory = admin.call('admin')
    check('identity kinds are served with the directory, and only human and service accounts are created here',
          [(k['code'], k['creatable']) for k in directory['kinds']] == [('human', True), ('service', True), ('agent', False)] and all(k['name'] for k in directory['kinds']))
    admin.call('workspace', expected=403)
    check('access administration does not imply market-data access')
    check('the seven human roles are assignable, and both agent-only roles are listed only to name what agents hold',
          sorted(r['code'] for r in directory['roles'] if r['assignable']) == ['access_administrator', 'assistant_prompt_editor', 'market_data_approver', 'market_data_editor', 'risk_analyst', 'trader', 'viewer']
          and sorted(r['code'] for r in directory['roles'] if not r['assignable']) == ['agent_reader', 'swap_settlement_operator'])
    check('role choices carry stored display names', all(isinstance(r.get('name'), str) and r['name'].strip() for r in directory['roles']) and len({r['name'] for r in directory['roles']}) == len(directory['roles']) == 9)
    request = {'operationId': str(uuid4()), 'login': 'synthetic_reader', 'name': 'Synthetic reader',
               'kind': 'human', 'roles': ['viewer']}
    created = admin.call('admin/create', request)
    check('admin creates a named workspace member with explicit roles', created['name'] == request['name'] and created['roles'] == ['viewer'])
    check('retrying the exact administrative command is idempotent', admin.call('admin/create', request) == created)
    admin.call('admin/create', {**request, 'name': 'Different payload'}, expected=409)
    check('operation identity cannot be reused for another payload')
    admin.call('profile', {'profile': request['login']})
    inputs = admin.call('workspace'); frame = inputs['frames'][0]
    check('new human preview identity uses persisted permissions', inputs['profile'] == request['login'])
    admin.call('admin', expected=403)
    admin.call('profile', {'profile': 'admin'})
    update = {'operationId': str(uuid4()), 'id': created['id'], 'expectedVersion': created['version'],
              'name': 'Synthetic reader renamed', 'enabled': False, 'roles': ['viewer']}
    disabled = admin.call('admin/update', update)
    check('workspace access can be suspended without deleting history', not disabled['enabled'] and disabled['version'] == created['version'] + 1)
    admin.call('admin/update', {**update, 'operationId': str(uuid4())}, expected=409)
    admin.call('profile', {'profile': request['login']})
    admin.call('evaluate', {'snapshotId': frame['id'], 'curve': 'local_power'}, expected=403)
    check('suspension blocks replay of retained market inputs')
    admin.call('profile', {'profile': 'admin'})
    restored = admin.call('admin/update', {**update, 'operationId': str(uuid4()), 'expectedVersion': disabled['version'], 'enabled': True})
    admin.call('profile', {'profile': request['login']})
    check('restoration uses unchanged historical market pins', bool(admin.call('evaluate', {'snapshotId': frame['id'], 'curve': 'local_power'})['points']))
    admin.call('profile', {'profile': 'admin'})
    own = next(m for m in admin.call('admin')['members'] if m['login'] == 'admin')
    error = admin.call('admin/update', {'operationId': str(uuid4()), 'id': own['id'], 'expectedVersion': own['version'],
        'name': own['name'], 'enabled': False, 'roles': own['roles']}, expected=422)
    check('last current workspace administrator cannot be removed', error['code'] == 'last_administrator')
    # Agents are created and retired through agent administration (docs/ai-assistant.md §5.4); Users & access lists them and
    # changes none, and grants no one an agent-only role.
    refusals = [admin.call('admin/create', {**request, 'operationId': str(uuid4()), 'login': 'synthetic_agent', 'name': 'Synthetic agent', 'kind': 'agent'}, expected=422),
                admin.call('admin/create', {**request, 'operationId': str(uuid4()), 'login': 'synthetic_second_reader', 'roles': ['agent_reader', 'viewer']}, expected=422)]
    for role in ('agent_reader', 'swap_settlement_operator'):
        refusals.append(admin.call('admin/create', {**request, 'operationId': str(uuid4()), 'login': 'synthetic_restricted_role', 'roles': [role]}, expected=422))
        refusals.append(admin.call('admin/update', {**update, 'operationId': str(uuid4()), 'expectedVersion': restored['version'], 'roles': [role]}, expected=422))
    listed = admin.call('settings/ai/agents/create', {'operationId': str(uuid4()), 'name': 'Synthetic listed agent'})
    agent = next(m for m in admin.call('admin')['members'] if m['kind'] == 'agent' and m['name'] == listed['name'])
    # Naming no role leaves only the rule that Users & access changes no agent to refuse the second change.
    for change in ({'enabled': False, 'roles': ['agent_reader']}, {'enabled': True, 'roles': []}):
        refusals.append(admin.call('admin/update', {'operationId': str(uuid4()), 'id': agent['id'], 'expectedVersion': agent['version'],
                                                    'name': agent['name'], **change}, expected=422))
    unchanged = next(m for m in admin.call('admin')['members'] if m['id'] == agent['id'])
    check('Users & access creates and changes no agent, grants no one an agent-only role, and lists the agents agent administration creates',
          all(r['code'] == 'agent_managed' for r in refusals) and unchanged == agent and agent['roles'] == ['agent_reader'] and agent['enabled'])
    audit = admin.call('admin')['audit']
    check('committed administration has actor and before-after evidence', any(a['operationId'] == update['operationId'] and a['before']['enabled'] and not a['after']['enabled'] for a in audit))
    check('administrative retry produces one audit entry', sum(a['operationId'] == request['operationId'] for a in audit) == 1)
