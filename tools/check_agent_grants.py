"""Grants (docs/ai-assistant.md §5.1, §5.4, §7.1, §8): a user's grant to an agent version, derived from the stored
version; one current grant per user and agent; coverage; Revoke; Retire revoking open grants; the grant audit; through
the API; owned synthetic preview only."""
import json
import re
import subprocess
import threading
from datetime import datetime, timedelta
from uuid import uuid4
from assistant_context import ISSUER, WINDOW, Contexts, check_guard, expect, holding, migration_body, refused, waiting
from check_agent_administration import RATES
from check_model_qualification import command as provider_command, terms
from check_model_versions import capabilities, command as model_command
from check_provider_tests import KEY
from preview_client import PreviewClient

AGENTS, MODELS, PROVIDERS = 'settings/ai/agents', 'settings/ai/models', 'settings/ai/providers'
USABLE, GRANTS = 'assistant/agents', 'assistant/grants'
EMPTY = '00000000-0000-0000-0000-000000000000'
# Every row a grant command writes, and the generation every admitted request pins.
RECORDS = ("SELECT md5(string_agg(value,E'\\n' ORDER BY value)) FROM("
           "SELECT row_to_json(d)::text value FROM security.agent_delegations d UNION ALL "
           "SELECT row_to_json(p)::text FROM security.delegation_permissions p UNION ALL "
           "SELECT row_to_json(a)::text FROM assistant.grant_audit a)s;")
EPOCH = 'SELECT generation FROM security.epoch WHERE id=true;'


def check_upgrade(query, migration):
    """Before 0068 applies: it refuses to start while any delegation is unrevoked, since a grant is a delegation with
    a grant record, and refuses changed helpers."""
    body = migration_body(migration)
    tenant, workspace, agent, user = uuid4(), uuid4(), uuid4(), uuid4()
    setup = (f"INSERT INTO security.tenants VALUES('{tenant}','synthetic_grant_upgrade');"
             f"INSERT INTO security.workspaces VALUES('{tenant}','{workspace}','synthetic_grant_upgrade');"
             f"INSERT INTO security.principals VALUES('{agent}','agent',true),('{user}','human',true);")

    def delegation(revoked):
        return (f"INSERT INTO security.agent_delegations VALUES('{uuid4()}','{agent}','{user}','{tenant}','{workspace}',"
                f"clock_timestamp()-interval '2 days',clock_timestamp()-interval '1 day',{revoked});")
    # An expired delegation still counts until revoked; a revoked one is history.
    try:
        query(f'BEGIN;{setup}{delegation("false")}{body}ROLLBACK;')
    except RuntimeError as error:
        assert f'agent_grants: {user} holds an unrevoked delegation to agent {agent}' in str(error), error
    else:
        raise AssertionError('Migration 0068 applied while a delegation was unrevoked')
    query(f'BEGIN;{setup}{delegation("true")}{body}ROLLBACK;')
    print('PASS migration 0068 refuses to start while any delegation, even an expired one, is unrevoked, and never revokes one',
          flush=True)
    check_guard(query, migration, '0068', 'agent_grants', tuple(
        (f"ALTER FUNCTION {helper} SET work_mem='64kB';", helper) for helper in (
            'security.has_permission(text)', 'security.has_pre_pilot_permission(text)',
            'security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)', 'security.use_context(uuid)',
            'security.guard_control_change()', 'security.validate_delegation()', 'security.member_description(uuid,uuid,uuid)',
            'assistant.agent_tools()', 'assistant.agent_actor()', 'assistant.agent_replay(security.request_contexts,uuid,jsonb)',
            'assistant.agent_for_change(security.request_contexts,uuid,integer)', 'assistant.agent_state(uuid)',
            'assistant.agent_version_state(uuid,integer)', 'assistant.current_model_version(uuid)',
            'assistant.retire_agent(uuid,uuid,integer)')))


def run_checks(url, query, psql, manifest, environment):
    contexts = Contexts(query, manifest)
    passed = 0

    def check(name, condition=True, observed=None):
        nonlocal passed
        assert condition, name if observed is None else f'{name}: observed {observed}'
        passed += 1
        print('PASS ' + name, flush=True)

    def client(profile):
        session = PreviewClient(url)
        session.bootstrap(); session.call('profile', {'profile': profile})
        return session

    def principal(subject):
        return query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{ISSUER}' AND subject='{subject}';")

    stamp = uuid4().hex[:8]

    def persona(name, roles):
        # A session can sign in only as the accounts it created.
        session = client('admin')
        session.call('admin/create', {'operationId': str(uuid4()), 'login': f'synthetic_grant_{name}_{stamp}', 'name': f'Synthetic grant {name}',
                                      'kind': 'human', 'roles': roles})
        session.call('profile', {'profile': f'synthetic_grant_{name}_{stamp}'})
        return session
    admin, trader, risk, editor, viewer = (client(profile) for profile in ('admin', 'trader', 'risk', 'editor', 'viewer'))
    # Managing agents lets a user use drafts; the curator lacks ReadTrades, which the guide's tools need.
    manager, curator = persona('manager', ['trader', 'access_administrator']), persona('curator', ['market_data_editor', 'access_administrator'])

    def refusal(session, path, body, status, code):
        answer = session.call(path, body, expected=status)
        assert answer['code'] == code, (path, answer)
        return answer

    # A qualified, enabled provider and two priced, enabled model identities.
    def provider():
        return next(p for p in admin.call(PROVIDERS)['providers'] if p['vendor'] == 'anthropic')

    def provider_change(action):
        return admin.call(f'{PROVIDERS}/{action}', provider_command('anthropic', provider()['version']))
    admin.call(f'{PROVIDERS}/key', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'expectedVersion': provider()['version'], 'key': KEY})
    admin.call(f'{PROVIDERS}/terms', terms('anthropic', provider()['version']))
    provider_change('attest'); provider_change('enable')
    usd = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','usd'"

    def row_version(model_id):
        return int(query(f"SELECT version FROM assistant.models WHERE id='{model_id}';"))

    def version_of(model_id):
        return int(query(f"SELECT max(version) FROM assistant.model_versions WHERE model_id='{model_id}';"))

    def edit(model_id):
        admin.call(f'{MODELS}/edit', capabilities(model_id, row_version(model_id), outputLimit=4096, reasoningEfforts=['low', 'high']))

    def model(identifier):
        added = admin.call(f'{MODELS}/add', {'operationId': str(uuid4()), 'vendor': 'anthropic', 'identifier': identifier,
                                             'displayName': f'Synthetic grant model {identifier[-12:]}'})
        edit(added['id'])
        query(f"INSERT INTO assistant.price_revisions VALUES('{added['id']}',1,'rates',{usd},{RATES},NULL,NULL,NULL,NULL,NULL,'2020-01-01',NULL,"
              f"'{contexts.administrator}',clock_timestamp());")
        admin.call(f'{MODELS}/enable', model_command(added['id'], row_version(added['id'])))
        return added['id']
    identifier = f'model-grant-usable-{stamp}'
    model_name = f'Synthetic grant model {identifier[-12:]}'
    usable, other = model(identifier), model(f'model-grant-other-{stamp}')

    def save(agent_state, model_id=None, tools=('read_current_page', 'read_market_data', 'read_trades'), days=28):
        model_id = model_id or usable
        return admin.call(f'{AGENTS}/save', {
            'operationId': str(uuid4()), 'agentId': agent_state['id'], 'expectedVersion': agent_state['version'],
            'description': 'Answers questions about the page it is opened on.', 'instructions': 'Quote only values you read.',
            'modelId': model_id, 'modelVersion': version_of(model_id), 'reasoning': 'low', 'caching': False, 'tools': list(tools),
            'allowedRoles': ['trader', 'risk_analyst'], 'limits': {'toolSteps': 8, 'inputTokens': 200000, 'outputTokens': 2048,
                                                                  'elapsedSeconds': 120, 'retries': 2}, 'grantDays': days})

    def publish(agent_state):
        return admin.call(f'{AGENTS}/publish', {'operationId': str(uuid4()), 'agentId': agent_state['id'], 'expectedVersion': agent_state['version']})

    def create(name):
        return admin.call(f'{AGENTS}/create', {'operationId': str(uuid4()), 'name': f'Synthetic {name} {stamp}'})
    guide = publish(save(create('grant guide')))
    agent_principal = query(f"SELECT principal_id FROM assistant.agents WHERE id='{guide['id']}';")
    trader_principal = principal('trader')

    def offers(session, agent_id=None):
        return [offer for offer in session.call(USABLE)['agents'] if offer['id'] == (agent_id or guide['id'])]

    def covered(agent_id=None):
        return {offer['version']: offer['grant'] for offer in offers(trader, agent_id)}

    # The agents a user may use (§7.1).
    check('a user who holds an allowed role is offered the published version, with its model, the permissions its tools need, '
          'its grant duration and no grant yet',
          offers(trader) == [{'id': guide['id'], 'name': f'Synthetic grant guide {stamp}', 'version': 1, 'draft': False,
                              'model': {'id': usable, 'version': version_of(usable), 'vendor': 'anthropic', 'identifier': identifier,
                                        'displayName': model_name},
                              'permissions': ['ReadMarketData', 'ReadTrades'], 'grantDays': 28, 'grant': None}])
    check('a user whose roles the published version does not allow is not offered it', offers(editor) == [])
    for session in (viewer, admin):
        refusal(session, USABLE, None, 403, 'access_denied')
    check('the agents a user may use are offered only to users who use agents')
    draft = save(guide)
    check('a draft is offered, beside the published version, only to a user who manages agents',
          [offer['version'] for offer in offers(trader)] == [1]
          and [(offer['version'], offer['draft']) for offer in offers(manager)] == [(1, False), (2, True)])
    layered = save(save(publish(save(create('layered guide')))))
    check('only the newest draft is offered, not an older one',
          [(offer['version'], offer['draft']) for offer in offers(manager, layered['id'])] == [(1, False), (3, True)])

    # Grant (§7.1): the request names only the agent version; refusals write nothing and take no exclusive admission.
    def grant(session, version=1, agent_id=None, operation=None):
        return session.call(f'{GRANTS}/grant', {'operationId': operation or str(uuid4()), 'agentId': agent_id or guide['id'], 'version': version})

    def body(version=1, agent_id=None, operation=None, **extra):
        return {'operationId': operation or str(uuid4()), 'agentId': agent_id or guide['id'], 'version': version, **extra}
    records, generation = query(RECORDS), query(EPOCH)
    for request in (body(operation=EMPTY), body(agent_id=EMPTY), body(version=None), body(version=0), body(version=-1)):
        refusal(trader, f'{GRANTS}/grant', request, 422, 'grant_input')
    contexts.runtime('trader', refused(f"PERFORM assistant.grant_agent('{uuid4()}','{guide['id']}',0,90);", 'grant_input'))
    for extra in ({'expiresAt': '9999-12-30T00:00:00Z'}, {'permissions': ['ReadTradeParties']}, {'delegator': principal('risk')}):
        refusal(trader, f'{GRANTS}/grant', body(**extra), 400, 'request_format')
    check('a grant names a request identity and an agent version, and nothing else', query(RECORDS) == records)
    refusal(viewer, f'{GRANTS}/grant', body(), 403, 'access_denied')
    refusal(editor, f'{GRANTS}/grant', body(), 403, 'agent_not_allowed')
    for request in (body(version=2), body(version=99), body(agent_id=str(uuid4()))):
        refusal(trader, f'{GRANTS}/grant', request, 403, 'agent_not_allowed')
    refusal(manager, f'{GRANTS}/grant', body(version=2, agent_id=layered['id']), 403, 'agent_not_allowed')
    check("a grant needs current UseAgents and a version the user may use: the published one, by an allowed role, or the newest "
          "draft, by managing agents", query(RECORDS) == records)
    lacking = refusal(curator, f'{GRANTS}/grant', body(version=2), 403, 'grant_permission_missing')
    check('a grant needs every permission the version needs, and the refusal names the one missing',
          'ReadTrades' in lacking['message'] and query(RECORDS) == records)
    contexts.runtime('trader', refused(f"PERFORM assistant.grant_agent('{uuid4()}','{guide['id']}',1,27);", 'grant_duration_exceeded'))
    check("a version whose grant would outlast the runtime set's bound is refused", query(RECORDS) == records)
    check('no refusal changes the epoch', query(EPOCH) == generation)
    # The database refuses on its own, whatever the API checks first: without UseAgents (the viewer lacks it and
    # CreateDelegations, the administrator only UseAgents), without CreateDelegations, and in any context but a person's own.
    grant_sql = f"PERFORM assistant.grant_agent('{uuid4()}','{guide['id']}',1,90);"
    for profile in ('viewer', 'admin'):
        contexts.runtime(profile, expect(grant_sql, '42501') + expect('PERFORM assistant.usable_agents();', '42501'))
    contexts.rolled_back("DELETE FROM security.role_permissions WHERE permission='CreateDelegations' "
                         "AND role_id=(SELECT id FROM security.roles WHERE code='trader');",
                         'trader', 'human_interactive', contexts.workspace, 'NULL', expect(grant_sql, '42501'))
    every = ''.join(expect(sql, '42501') for sql in ('PERFORM assistant.usable_agents();', 'PERFORM assistant.grants();', grant_sql,
                                                     f"PERFORM assistant.revoke_grant('{uuid4()}','{uuid4()}');"))
    contexts.delegated_agent(every)
    service = f'synthetic_grant_service_{stamp}'
    admin.call('admin/create', {'operationId': str(uuid4()), 'login': service, 'name': 'Synthetic grant service', 'kind': 'service',
                                'roles': ['trader']})
    contexts.rolled_back('', service, 'service_credential', contexts.workspace, 'NULL', every)
    lent, delegation = uuid4(), uuid4()
    contexts.rolled_back(f"INSERT INTO security.principals VALUES('{lent}','agent',true);"
                         f"INSERT INTO security.agent_delegations VALUES('{delegation}','{lent}','{trader_principal}','{contexts.tenant}',"
                         f"'{contexts.workspace}',{WINDOW},false);",
                         'trader', 'human_interactive', contexts.workspace, f"'{delegation}'", every)
    check("the database itself refuses a grant, or the offers, without UseAgents or CreateDelegations, and every grant operation "
          "in the context of an agent, a service or a person acting under a delegation")

    operation = str(uuid4())
    granted = grant(trader, operation=operation)
    issued, expiry = (datetime.fromisoformat(granted[key]) for key in ('grantedAt', 'expiresAt'))
    check('a grant is derived from the stored version: its model, the permissions its tools need and its duration',
          granted == {'id': granted['id'], 'agentId': guide['id'], 'version': 1, 'modelId': usable, 'modelVersion': version_of(usable),
                      'permissions': ['ReadMarketData', 'ReadTrades'], 'grantedAt': granted['grantedAt'], 'expiresAt': granted['expiresAt']}
          and expiry - issued == timedelta(days=28))
    check('the grant is a delegation from the user to the agent principal for exactly those permissions, until the expiry',
          query(f"SELECT agent_id||' '||delegator_id||' '||revoked||' '||(granted_to-granted_from) FROM security.agent_delegations "
                f"WHERE id='{granted['id']}';") == f'{agent_principal} {trader_principal} false 28 days'
          and query(f"SELECT string_agg(permission,',' ORDER BY permission) FROM security.delegation_permissions WHERE delegation_id='{granted['id']}';")
          == 'ReadMarketData,ReadTrades')
    check('the grant audit records it once, naming the version, the model and the permissions it was given for',
          query("SELECT string_agg(outcome||' '||delegator_id||' '||actor_id||' '||agent_id||' '||agent_version||' '||model_id||' '||model_version"
                f"||' '||array_to_string(permissions,','),';') FROM assistant.grant_audit WHERE operation_id='{operation}';")
          == f"granted {trader_principal} {trader_principal} {guide['id']} 1 {usable} {version_of(usable)} ReadMarketData,ReadTrades")
    check('a grant advances the epoch, so it fences what an earlier authority admitted', int(query(EPOCH)) > int(generation))
    check('the offer names the grant that covers it', covered() == {1: {'id': granted['id'], 'expiresAt': granted['expiresAt']}})
    mine = [g for g in trader.call(GRANTS)['grants'] if g['agentId'] == guide['id']]
    check("the user's grants list it, with the agent and model it was given for",
          mine == [{**granted, 'agentName': f'Synthetic grant guide {stamp}', 'vendor': 'anthropic', 'identifier': identifier,
                    'displayName': model_name}])
    check("another user's grants never list it", all(g['id'] != granted['id'] for g in risk.call(GRANTS)['grants']))

    records = query(RECORDS)
    check('repeating the identical grant answers as it did and writes nothing',
          grant(trader, operation=operation) == granted and query(RECORDS) == records)
    refusal(trader, f'{GRANTS}/grant', body(version=2, operation=operation), 409, 'grant_conflict')
    refusal(risk, f'{GRANTS}/grant', body(operation=operation), 409, 'grant_conflict')
    check("a request identity used for another grant, or by another user, is refused", query(RECORDS) == records)

    # One current grant per user and agent: a new grant replaces the earlier one.
    replacing = str(uuid4())
    current = grant(trader, operation=replacing)
    check('a new grant revokes the earlier one, recording the replacement with the grant',
          query(f"SELECT revoked FROM security.agent_delegations WHERE id='{granted['id']}';") == 't'
          and query(f"SELECT count(*) FROM security.agent_delegations WHERE delegator_id='{trader_principal}' AND agent_id='{agent_principal}' "
                    "AND NOT revoked;") == '1'
          and query(f"SELECT string_agg(outcome||':'||delegation_id,',' ORDER BY outcome) FROM assistant.grant_audit WHERE operation_id='{replacing}';")
          == f"granted:{current['id']},replaced:{granted['id']}"
          and [g['id'] for g in trader.call(GRANTS)['grants'] if g['agentId'] == guide['id']] == [current['id']])
    second = (f"INSERT INTO security.agent_delegations VALUES(gen_random_uuid(),'{agent_principal}','{trader_principal}','{contexts.tenant}',"
              f"'{contexts.workspace}',clock_timestamp(),clock_timestamp()+interval '1 day',false);")
    query(f"BEGIN;{expect(second, '23505')}ROLLBACK;")
    check('even for the owner, one user holds at most one unrevoked delegation to one agent')

    # Coverage: the same model identity and permissions are covered whatever the version; anything else asks again.
    current_cover = {'id': current['id'], 'expiresAt': current['expiresAt']}
    guide = publish(draft)
    check('a grant covers a newer version with the same model identity and permissions, keeping its own expiry',
          covered() == {2: current_cover}, covered())
    guide = publish(save(guide, model_id=other))
    check('a version naming another model identity is not covered', covered() == {3: None}, covered())
    guide = publish(save(guide, tools=('read_current_page', 'read_market_data', 'read_trades', 'read_trade_parties')))
    check('a version needing a permission the grant does not list is not covered', covered() == {4: None}, covered())
    guide = publish(save(guide, tools=('read_market_data',)))
    check('a version needing fewer permissions is covered', covered() == {5: current_cover}, covered())
    edit(usable)
    guide = publish(save(guide))
    check("a version naming a newer capability version of the grant's model identity is covered", covered() == {6: current_cover}, covered())

    # Delegated authority (§13): an agent context reads under a current grant only.
    def agent_reads(delegation):
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'{ISSUER}','{agent_principal}','agent_credential','{contexts.tenant}','{contexts.workspace}',{delegation},'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; DO $$ BEGIN PERFORM security.use_context('{token}'); END $$;"
                     f"SELECT security.market_access('{manifest['catalogId']}','ReadMarketData'); COMMIT;")
    check("an agent created through ManageAgents reads market data with the user's current grant, and not without it",
          agent_reads('NULL') == 'f' and agent_reads(f"'{current['id']}'") == 't' and agent_reads(f"'{granted['id']}'") == 'f')

    # An expired grant covers nothing, reads nothing and is not listed, though it stays unrevoked until replaced or revoked.
    query(f"UPDATE security.agent_delegations SET granted_to=clock_timestamp() WHERE id='{current['id']}';")
    check('an expired grant covers nothing, reads nothing and is no longer listed, yet stays unrevoked',
          covered() == {6: None} and agent_reads(f"'{current['id']}'") == 'f'
          and all(g['id'] != current['id'] for g in trader.call(GRANTS)['grants'])
          and query(f"SELECT revoked FROM security.agent_delegations WHERE id='{current['id']}';") == 'f', covered())
    query(f"UPDATE security.agent_delegations SET granted_to='{current['expiresAt']}' WHERE id='{current['id']}';")
    lapsed = grant(risk, version=6)
    query(f"UPDATE security.agent_delegations SET granted_to=clock_timestamp() WHERE id='{lapsed['id']}';")
    renewing = str(uuid4())
    renewed = grant(risk, version=6, operation=renewing)
    check('the next grant replaces an expired one, recording the replacement',
          query(f"SELECT string_agg(outcome||':'||delegation_id,',' ORDER BY outcome) FROM assistant.grant_audit WHERE operation_id='{renewing}';")
          == f"granted:{renewed['id']},replaced:{lapsed['id']}"
          and query(f"SELECT revoked FROM security.agent_delegations WHERE id='{lapsed['id']}';") == 't')

    # A request context that has expired lists and revokes nothing, even under exclusive admission.
    token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                  f"'{ISSUER}','trader','human_interactive','{contexts.tenant}','{contexts.workspace}',NULL,'preview_runtime',2);")
    query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT assistant.use_grant_admission('{token}');SELECT pg_sleep(2.5);"
          + expect('PERFORM assistant.grants();', '42501') + expect(f"PERFORM assistant.revoke_grant('{uuid4()}','{current['id']}');", '42501')
          + 'ROLLBACK;')
    check('an expired request context lists and revokes nothing, even under exclusive admission')

    # A person in another workspace is offered, granted and revokes none of this workspace's agents and grants.
    elsewhere = str(uuid4())
    isolated = contexts.rolled_back(
        f"INSERT INTO security.workspaces VALUES('{contexts.tenant}','{elsewhere}','grant_isolation');"
        f"INSERT INTO security.workspace_memberships VALUES('{contexts.tenant}','{elsewhere}','{trader_principal}','trader','Synthetic trader',true,1);"
        f"INSERT INTO security.role_grants SELECT gen_random_uuid(),'{trader_principal}',id,'{contexts.tenant}','{elsewhere}',{WINDOW},false "
        "FROM security.roles WHERE code='trader';",
        'trader', 'human_interactive', elsewhere, 'NULL',
        refused(f"PERFORM assistant.grant_agent('{uuid4()}','{guide['id']}',6,90);", 'agent_not_allowed')
        + refused(f"PERFORM assistant.revoke_grant('{uuid4()}','{current['id']}');", 'grant_unknown')
        + '\nSELECT assistant.usable_agents()::text;\nSELECT assistant.grants()::text;').splitlines()
    check("a person in another workspace is offered, granted and revokes none of this workspace's agents and grants",
          json.loads(isolated[-2]) == {'agents': []} and json.loads(isolated[-1]) == {'grants': []})

    # A command whose authority lapses while it waits is refused at its last recheck: a grant waiting to replace the
    # earlier one once the person's roles expire, and a Revoke waiting for its grant once its request context expires.
    lapsing = persona('lapsing', ['trader'])
    earlier = grant(lapsing, version=6)
    hold = f"SELECT 1 FROM security.agent_delegations WHERE id='{earlier['id']}'"
    records = query(RECORDS)
    lapse = {'subject': f'synthetic_grant_lapsing_{stamp}', 'admission': 'assistant.use_grant_admission'}
    regranted = contexts.expiring(psql, environment, 'grant', f'{hold} FOR NO KEY UPDATE',
                                  f"SELECT assistant.grant_agent('{uuid4()}','{guide['id']}',6,90);",
                                  held=('assistant.grant_audit', 'RowExclusiveLock'), **lapse)
    revoked_late = contexts.expiring(psql, environment, 'context', f'{hold} FOR NO KEY UPDATE',
                                     f"SELECT assistant.revoke_grant('{uuid4()}','{earlier['id']}');", held=('security.principals', 'RowShareLock'),
                                     **lapse)
    check('a grant or Revoke whose authority lapses while it waits is refused at its last recheck, writing nothing',
          all(failure and '42501' in failure and re.search(rf'function assistant\.{name}\(.*\) line \d+ at PERFORM', failure)
              for name, failure in (('grant_agent', regranted), ('revoke_grant', revoked_late)))
          and query(RECORDS) == records, (regranted, revoked_late))
    ended = holding(psql, environment, f'{hold} FOR KEY SHARE',
                    lambda: lapsing.call(f'{GRANTS}/revoke', {'operationId': str(uuid4()), 'grantId': earlier['id']}))
    check('a Revoke never waits for a request that only refers to its grant', ended['revokedAt'])

    # Exclusive admission: while another request is admitted, refusals and no-ops answer at once and a grant that
    # writes waits; outside it a command that passes every check is refused.
    def admitted(work):
        return holding(psql, environment, 'SELECT 1 FROM security.epoch WHERE id=true FOR SHARE', work)

    def blocked_grant():
        answers, racer = {}, client('risk')
        thread = threading.Thread(target=lambda: answers.update(granted=grant(racer, version=6)))
        thread.start()
        waiting(query, 1, 'use_grant_admission')
        answers['waited'] = 'granted' not in answers
        return thread, answers
    quick = admitted(lambda: (refusal(editor, f'{GRANTS}/grant', body(version=6), 403, 'agent_not_allowed'), grant(trader, operation=replacing)))
    thread, answers = admitted(blocked_grant)
    thread.join(timeout=15)
    check('a refused or repeated grant answers while another request is admitted, and a grant that writes waits for exclusive admission',
          quick[1] == current and answers['waited'] and answers['granted']['version'] == 6)
    contexts.runtime('trader', refused(f"PERFORM assistant.grant_agent('{uuid4()}','{guide['id']}',6,90);", 'admission_required'))
    contexts.runtime('trader', refused(f"PERFORM assistant.revoke_grant('{uuid4()}','{current['id']}');", 'admission_required'))
    check('outside exclusive admission a grant or Revoke that passes every check is refused with admission_required')

    # A grant during a delivery-price attempt fences it: the attempt pinned the epoch generation the grant advances.
    catalog, reference, revision = (manifest[key] for key in ('catalogId', 'referenceCatalogId', 'referenceRevisionId'))
    collector, actor, source, job = f'grant_collector_{stamp}', str(uuid4()), str(uuid4()), str(uuid4())
    query(f"""CREATE ROLE {collector} LOGIN IN ROLE openetrm_collector;
    INSERT INTO security.principals VALUES('{actor}','service',true);
    INSERT INTO security.workspace_memberships VALUES('{contexts.tenant}','{contexts.workspace}','{actor}','{collector}','Synthetic grant collector',true,1);
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{actor}',id,'{contexts.tenant}','{contexts.workspace}',clock_timestamp(),'9999-12-31',false
     FROM security.roles WHERE code='market_data_editor';
    INSERT INTO power_data.collectors VALUES('{collector}','{actor}','{contexts.tenant}','{contexts.workspace}','{catalog}');
    INSERT INTO power_data.sources VALUES('{source}','{catalog}','Synthetic grant delivery prices','PJM','da_hrl_lmps','DA','51289',60,'{reference}',
     '{revision}','test_hub_a','synthetic_eastern','usd','test_power_quantity');
    INSERT INTO power_data.jobs VALUES('{job}',gen_random_uuid(),'Synthetic grant schedule','{collector}','{catalog}','PJM',true,false,'0 8 * * *','UTC',-7,-1);
    INSERT INTO power_data.job_sources VALUES('{job}','{source}','{catalog}');
    INSERT INTO power_data.operating_days VALUES('{reference}','{revision}','synthetic_eastern','2026-11-09','2026-11-09T05:00:00Z','2026-11-10T05:00:00Z','{'b' * 64}');""")
    worker = subprocess.Popen(psql, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        worker.stdin.write(f"SET SESSION AUTHORIZATION {collector};SELECT power_data.worker_lock();"
                           f"SELECT power_data.begin_attempt('{job}','2026-11-09','2026-11-09') AS token \\gset\n\\echo BEGUN\n")
        worker.stdin.flush()
        while worker.stdout.readline().strip() != 'BEGUN':
            assert worker.poll() is None, worker.stderr.read()
        fenced = grant(manager, version=6)
        worker.stdin.write("SELECT power_data.finish_attempt(:'token',NULL,'synthetic_failure');\n")
        worker.stdin.close()
        failure = worker.stderr.read()
    finally:
        if worker.poll() is None:
            worker.kill()
        worker.wait(timeout=10)
    check('a grant during a delivery-price attempt fences it', fenced['version'] == 6 and 'collection_fence' in failure)

    # Revoke (§7.1): only its delegator, answering another user's grant like an unknown one; needs no UseAgents.
    def revoke(session, grant_id, operation=None, expected=200):
        return session.call(f'{GRANTS}/revoke', {'operationId': operation or str(uuid4()), 'grantId': grant_id}, expected=expected)
    records, generation = query(RECORDS), query(EPOCH)
    stranger, unknown = revoke(risk, current['id'], expected=404), revoke(risk, str(uuid4()), expected=404)
    check("another user's grant is answered exactly like an unknown one, and nothing changes",
          stranger == unknown and stranger['code'] == 'grant_unknown' and query(RECORDS) == records and query(EPOCH) == generation)
    for request in ({'operationId': EMPTY, 'grantId': current['id']}, {'operationId': str(uuid4()), 'grantId': EMPTY}):
        refusal(trader, f'{GRANTS}/revoke', request, 422, 'grant_input')
    refusal(trader, f'{GRANTS}/revoke', {'operationId': str(uuid4()), 'grantId': current['id'], 'agentId': guide['id']}, 400, 'request_format')
    check('a Revoke names a request identity and a grant, and nothing else', query(RECORDS) == records)
    revoking = str(uuid4())
    revoked = revoke(trader, current['id'], operation=revoking)
    check('Revoke ends the grant: the delegation is revoked, the revocation recorded once, and the epoch advances',
          revoked == {**current, 'revokedAt': revoked['revokedAt']} and revoked['revokedAt']
          and query(f"SELECT revoked FROM security.agent_delegations WHERE id='{current['id']}';") == 't'
          and query(f"SELECT string_agg(outcome||' '||delegation_id||' '||actor_id,';') FROM assistant.grant_audit WHERE operation_id='{revoking}';")
          == f"revoked {current['id']} {trader_principal}"
          and int(query(EPOCH)) > int(generation))
    check('a revoked grant reads nothing, is no longer listed, and leaves the offer uncovered',
          agent_reads(f"'{current['id']}'") == 'f' and all(g['id'] != current['id'] for g in trader.call(GRANTS)['grants'])
          and covered() == {6: None})
    records, generation = query(RECORDS), query(EPOCH)
    check('repeating the Revoke, or revoking the revoked grant again, answers the revocation and writes nothing',
          revoke(trader, current['id'], operation=revoking) == revoked and revoke(trader, current['id']) == revoked
          and query(RECORDS) == records and query(EPOCH) == generation)
    check('revoking a grant that its replacement ended answers when it ended, and writes nothing',
          revoke(trader, granted['id']) == {**granted, 'revokedAt': current['grantedAt']}
          and query(RECORDS) == records and query(EPOCH) == generation)
    refusal(trader, f'{GRANTS}/grant', body(version=6, operation=revoking), 409, 'grant_conflict')
    refusal(trader, f'{GRANTS}/revoke', {'operationId': replacing, 'grantId': current['id']}, 409, 'grant_conflict')
    check("a grant under a Revoke's request identity, or a Revoke under a grant's, is refused", query(RECORDS) == records)
    fading = persona('fading', ['trader'])
    kept = grant(fading, version=6)
    member = next(m for m in admin.call('admin')['members'] if m['login'] == f'synthetic_grant_fading_{stamp}')
    admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': member['version'], 'name': member['name'],
                                'enabled': member['enabled'], 'roles': ['viewer']})
    refusal(fading, USABLE, None, 403, 'access_denied')
    check('a user who no longer uses agents still lists and revokes their grants',
          [g['id'] for g in fading.call(GRANTS)['grants']] == [kept['id']] and revoke(fading, kept['id'])['revokedAt']
          and fading.call(GRANTS)['grants'] == [])
    suspended = persona('suspended', ['trader'])
    suspended_grant = grant(suspended, version=6)
    member = next(m for m in admin.call('admin')['members'] if m['login'] == f'synthetic_grant_suspended_{stamp}')
    admin.call('admin/update', {'operationId': str(uuid4()), 'id': member['id'], 'expectedVersion': member['version'], 'name': member['name'],
                                'enabled': False, 'roles': ['trader']})
    records = query(RECORDS)
    refusal(suspended, GRANTS, None, 403, 'access_denied')
    refusal(suspended, f'{GRANTS}/revoke', {'operationId': str(uuid4()), 'grantId': suspended_grant['id']}, 403, 'access_denied')
    check('a person whose membership is disabled lists and revokes nothing, even holding their roles', query(RECORDS) == records)

    # An outcome the API cannot confirm is unknown; repeating the identical request then grants exactly once.
    pending = str(uuid4())
    unknown = admitted(lambda: trader.call(f'{GRANTS}/grant', body(version=6, operation=pending), expected=503))
    kept_guide = grant(trader, version=6, operation=pending)
    check("a grant held past the API's command timeout reports an unknown outcome, and repeating it grants exactly once",
          unknown['code'] == 'grant_outcome_unknown'
          and query(f"SELECT count(*) FROM assistant.grant_audit WHERE operation_id='{pending}' AND outcome='granted';") == '1')

    def unrevoked(*delegations):
        return query("SELECT count(*) FROM security.agent_delegations WHERE NOT revoked AND id IN("
                     + ','.join(f"'{d}'" for d in delegations) + ');') == str(len(delegations))

    # Retire (§5.4) revokes the agent's open grants, recording each against the administrator who retired it.
    retiring = publish(save(create('retiring guide')))
    trader_retiring = grant(trader, agent_id=retiring['id'])
    open_grants = sorted((trader_retiring['id'], grant(risk, agent_id=retiring['id'])['id']))
    check("a grant replaces only the user's earlier grant to the same agent", unrevoked(kept_guide['id'], fenced['id']))
    records = query(RECORDS)
    for used in (replacing, revoking):
        refusal(admin, f'{AGENTS}/retire', {'operationId': used, 'agentId': retiring['id'], 'expectedVersion': retiring['version']},
                409, 'model_admin_conflict')
    check("Retire under a grant's or a Revoke's request identity is refused, writing nothing, and that grant still repeats as it did",
          query(RECORDS) == records and grant(trader, operation=replacing) == current)
    retire = str(uuid4())
    admin.call(f'{AGENTS}/retire', {'operationId': retire, 'agentId': retiring['id'], 'expectedVersion': retiring['version']})
    check("Retire revokes the agent's open grants, recording each against the administrator, and no other grant",
          query(f"SELECT bool_and(revoked) FROM security.agent_delegations WHERE id IN('{open_grants[0]}','{open_grants[1]}');") == 't'
          and query(f"SELECT string_agg(outcome||':'||delegation_id||':'||actor_id,',' ORDER BY delegation_id) FROM assistant.grant_audit "
                    f"WHERE operation_id='{retire}';")
          == ','.join(f'retired:{delegation}:{contexts.administrator}' for delegation in open_grants)
          and unrevoked(kept_guide['id'], fenced['id']))
    records, generation = query(RECORDS), query(EPOCH)
    ended = revoke(trader, trader_retiring['id'])
    check('revoking a grant that Retire ended answers when it ended, and writes nothing',
          ended == {**trader_retiring, 'revokedAt': ended['revokedAt']}
          and query(f"SELECT recorded_at='{ended['revokedAt']}'::timestamptz FROM assistant.grant_audit "
                    f"WHERE delegation_id='{trader_retiring['id']}' AND outcome='retired';") == 't'
          and query(RECORDS) == records and query(EPOCH) == generation)
    refusal(trader, f'{GRANTS}/grant', body(agent_id=retiring['id']), 409, 'agent_retired')
    check("a retired agent is neither offered nor granted, and its grants are no longer listed",
          offers(trader, retiring['id']) == [] and all(g['agentId'] != retiring['id'] for g in trader.call(GRANTS)['grants']))

    # The grant audit keeps history for good.
    for statement in ("UPDATE assistant.grant_audit SET expires_at=expires_at+interval '1 day';", 'DELETE FROM assistant.grant_audit;'):
        query(f'BEGIN;{expect(statement, "55000")}ROLLBACK;')
    check('even the owner cannot change or delete the grant audit')
    print(f'{passed} grant checks passed.', flush=True)
