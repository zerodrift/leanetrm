"""A year of page-assistant Usage at a stated volume through the launcher's owned synthetic preview.

One workspace holds 100,000 model calls over the 366 local days of a report in New York: 50 users each ask one of three
published agents 980 questions with two calls each, and 2,000 provider tests run beside them. The agents' three models
share one currency reference, and one of them prices no cache write, so its calls that write the cache are unpriced;
every 37th call has unknown usage, and each user's last turn is still running with its second call open. Users, turns
and calls are written by the database owner, as the usage checks write theirs, after the provider, models and agents
are made through Settings › AI. The access administrator then reads the year grouped by user, in SQL and over HTTP,
and exports it; each answer is checked against the counts the workload declares, and a refusal, a timeout or a wrong
count fails the run naming its step. The launcher prints the report as JSON (docs/verification.md, "Usage at volume")."""
import json
import subprocess
from uuid import uuid4

from assistant_context import Contexts
from assistant_fixture import Fixture
from benchmark_interval_series import api_resident_bytes, environment, require, timed
from preview_client import PreviewClient

ZONE, FIRST_DAY, END_DAY, DAYS = 'America/New_York', '2025-01-01', '2026-01-02', 366
# The year's local days in New York start at 05:00Z in winter; every start lies in [2025-01-01T05:00Z, 2026-01-02T05:00Z).
WINDOW_START, WINDOW_SECONDS = '2025-01-01T05:00:00Z', DAYS * 86400
USERS, TURNS_PER_USER, CALLS_PER_TURN, TESTS = 50, 980, 2, 2000
CALLS = USERS * TURNS_PER_USER * CALLS_PER_TURN + TESTS
UNKNOWN_EVERY = 37
# The API's database commands time out after 5 seconds (PreviewDatabase.cs), and the client abandons a request after 20.
COMMAND_TIMEOUT_SECONDS = 5
# The launcher's queries stop after 30 seconds; the workload is written in one transaction allowed ten minutes.
SEED_TIMEOUT_SECONDS = 600
# The shared fixture's rates without a cache-write rate.
NO_CACHE_WRITE = '1.234,0.0123,NULL,9.87'


def seed(client, query, psql, psql_env, manifest, contexts, stamp):
    """Users, agents, turns and calls as the workload declares them; returns what the report must count."""
    fixture = Fixture(client, query, manifest, contexts.administrator, stamp, 'usage benchmark')
    fixture.qualify()
    models = [fixture.model(f'model-usage-benchmark-a-{stamp}'), fixture.model(f'model-usage-benchmark-b-{stamp}'),
              fixture.model(f'model-usage-benchmark-c-{stamp}', NO_CACHE_WRITE)]
    agents = [fixture.publish(fixture.save(fixture.create(f'usage benchmark {name}'), model)) for name, model in zip('abc', models)]
    tenant, workspace = contexts.tenant, contexts.workspace
    generation = query('SELECT generation FROM assistant.worker_generation;')
    provider, credential, terms = query(f"SELECT a.provider_id||','||a.credential_version||','||a.terms_version FROM assistant.models m "
                                        f"CROSS JOIN LATERAL assistant.current_attestation(m.provider_id) a WHERE m.id='{models[0]}';").split(',')
    pinned = [line for line in contexts.runtime('trader', f"SELECT security.capture_date_context('{manifest['catalogId']}','2020-12-18',NULL);").splitlines()
              if line.strip()][-1]
    cutoff = query(f"SELECT known_at FROM market_data.temporal_contexts WHERE id='{pinned}';")
    agent_rows = ','.join(f"({i},'{a['id']}',(SELECT principal_id FROM assistant.agents WHERE id='{a['id']}'),'{m}',"
                          f"(SELECT max(version) FROM assistant.model_versions WHERE model_id='{m}'))" for i, (a, m) in enumerate(zip(agents, models)))
    # Each user asks the agent their number picks; a turn's calls start at evenly spaced instants of the year, its
    # second a minute after its first.
    written = subprocess.run(psql, input=f"""BEGIN;
CREATE TEMPORARY TABLE bench_agents(slot integer,agent uuid,principal uuid,model uuid,model_version integer) ON COMMIT DROP;
INSERT INTO bench_agents VALUES {agent_rows};
CREATE TEMPORARY TABLE bench_users ON COMMIT DROP AS SELECT u,gen_random_uuid() AS principal,gen_random_uuid() AS conversation,
 gen_random_uuid() AS delegation FROM generate_series(0,{USERS - 1}) u;
INSERT INTO security.principals SELECT principal,'human',true FROM bench_users;
INSERT INTO security.workspace_memberships SELECT '{tenant}','{workspace}',principal,'synthetic_benchmark_'||lpad(u::text,2,'0')||'_{stamp}',
 'Synthetic benchmark user '||lpad(u::text,2,'0'),true,1 FROM bench_users;
INSERT INTO security.agent_delegations SELECT delegation,a.principal,b.principal,'{tenant}','{workspace}','2024-12-01','2026-02-01',true
 FROM bench_users b JOIN bench_agents a ON a.slot=b.u%3;
INSERT INTO assistant.conversations SELECT b.conversation,'{tenant}','{workspace}',b.principal,'api',a.agent,'2024-12-01',0
 FROM bench_users b JOIN bench_agents a ON a.slot=b.u%3;
CREATE TEMPORARY TABLE bench_turns ON COMMIT DROP AS SELECT gen_random_uuid() AS id,b.*,t,
 '{WINDOW_START}'::timestamptz+make_interval(secs=>((t*{USERS}+b.u)*{CALLS_PER_TURN})::double precision*{WINDOW_SECONDS}/{CALLS - TESTS}) AS asked,
 CASE WHEN t={TURNS_PER_USER - 1} THEN 'running' WHEN t%100<5 THEN 'failed' WHEN t%100<7 THEN 'cancelled' WHEN t%100=7 THEN 'interrupted' ELSE 'completed' END AS state
 FROM bench_users b CROSS JOIN generate_series(0,{TURNS_PER_USER - 1}) t;
INSERT INTO assistant.turns SELECT x.id,x.conversation,'{tenant}','{workspace}',x.t+1,x.principal,gen_random_uuid(),a.agent,1,false,x.delegation,
 'Synthetic benchmark question','{{"page":"curves"}}','{manifest['catalogId']}','current',NULL,NULL,'{pinned}','{cutoff}',NULL,x.asked,ARRAY[]::uuid[],
 'turns_v1',x.state,CASE WHEN x.state='failed' THEN 'model_provider_overloaded' END,NULL,CASE WHEN x.state='completed' THEN 'Synthetic benchmark answer' ELSE '' END,
 false,{generation},'{'0' * 64}','{'0' * 64}','Synthetic benchmark',x.asked+interval '30 minutes',x.asked,x.asked,
 CASE WHEN x.state='cancelled' THEN x.asked+interval '1 second' END,NULL,CASE WHEN x.state<>'running' THEN x.asked+interval '2 minutes' END
 FROM bench_turns x JOIN bench_agents a ON a.slot=x.u%3;
CREATE TEMPORARY TABLE bench_calls ON COMMIT DROP AS SELECT gen_random_uuid() AS id,x.id AS turn,x.state,a.model,a.model_version,c AS attempt,
 x.asked+make_interval(mins=>c-1) AS started,(x.t*{USERS}+x.u)*{CALLS_PER_TURN}+c AS k
 FROM bench_turns x JOIN bench_agents a ON a.slot=x.u%3 CROSS JOIN generate_series(1,{CALLS_PER_TURN}) c;
INSERT INTO assistant.model_calls(id,tenant_id,workspace_id,generation,provider_id,credential_version,terms_version,model_id,model_version,reasoning,
 max_output_tokens,price_sequence,cost_method,started_at,turn_id,attempt,after_sequence)
 SELECT id,'{tenant}','{workspace}',{generation},'{provider}',{credential},{terms},model,model_version,'low',256,1,'token_cost_v1',started,turn,attempt,1 FROM bench_calls;
INSERT INTO assistant.model_calls(id,tenant_id,workspace_id,generation,provider_id,credential_version,terms_version,model_id,model_version,reasoning,
 max_output_tokens,price_sequence,cost_method,started_at,operation_id,actor_id,token_digest)
 SELECT gen_random_uuid(),'{tenant}','{workspace}',{generation},'{provider}',{credential},{terms},'{models[0]}',
 (SELECT max(version) FROM assistant.model_versions WHERE model_id='{models[0]}'),'low',256,1,'token_cost_v1',
 '{WINDOW_START}'::timestamptz+make_interval(secs=>(n+0.5)*{WINDOW_SECONDS}/{TESTS}),gen_random_uuid(),'{contexts.administrator}','{'0' * 64}'
 FROM generate_series(0,{TESTS - 1}) n;
INSERT INTO assistant.model_call_outcomes SELECT c.id,c.started_at+interval '5 seconds',
 CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN 'model_provider_overloaded' ELSE 'succeeded' END,CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN NULL ELSE m.identifier END,
 CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN 'unknown' ELSE 'recognized' END,
 CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN NULL ELSE 1200 END,CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN 'unknown' ELSE 'reported' END,
 CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN NULL ELSE 300 END,CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN 'unknown' ELSE 'reported' END,
 CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN NULL ELSE 40 END,CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN 'unknown' ELSE 'reported' END,
 CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN NULL ELSE 250 END,CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN 'unknown' ELSE 'reported' END,
 CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN NULL ELSE 50 END,CASE WHEN b.k%{UNKNOWN_EVERY}=0 THEN 'unknown' ELSE 'reported' END,
 CASE WHEN b.k%{UNKNOWN_EVERY}<>0 THEN assistant.token_cost_v1(r,1200,300,40,250) END,
 CASE WHEN b.k%{UNKNOWN_EVERY}<>0 THEN 'end_turn' END
 FROM bench_calls b JOIN assistant.model_calls c ON c.id=b.id JOIN assistant.price_revisions r ON(r.model_id,r.sequence)=(c.model_id,c.price_sequence)
 JOIN assistant.models m ON m.id=c.model_id WHERE NOT(b.state='running' AND b.attempt={CALLS_PER_TURN});
INSERT INTO assistant.model_call_outcomes SELECT c.id,c.started_at+interval '5 seconds','succeeded',m.identifier,'recognized',
 10,'reported',0,'reported',0,'reported',10,'reported',0,'reported',assistant.token_cost_v1(r,10,0,0,10)
 FROM assistant.model_calls c JOIN assistant.price_revisions r ON(r.model_id,r.sequence)=(c.model_id,c.price_sequence) JOIN assistant.models m ON m.id=c.model_id
 WHERE c.turn_id IS NULL AND c.model_id='{models[0]}' AND c.actor_id='{contexts.administrator}' AND c.started_at>='{WINDOW_START}'
  AND NOT EXISTS(SELECT 1 FROM assistant.model_call_outcomes o WHERE o.call_id=c.id);
COMMIT;""", env=psql_env, text=True, capture_output=True, timeout=SEED_TIMEOUT_SECONDS)
    require(written.returncode == 0, f'writing the workload failed: {written.stderr}')
    turn_calls = USERS * TURNS_PER_USER * CALLS_PER_TURN
    keys = range(1, turn_calls + 1)
    unknown = sum(1 for k in keys if k % UNKNOWN_EVERY == 0)
    # A key is ((t*USERS+u)*CALLS_PER_TURN+attempt); the open calls are the running turns' second calls.
    running = {(((TURNS_PER_USER - 1) * USERS + u) * CALLS_PER_TURN + CALLS_PER_TURN) for u in range(USERS)}
    unknown -= sum(1 for k in running if k % UNKNOWN_EVERY == 0)
    unpriced = sum(1 for k in keys if k % UNKNOWN_EVERY and k not in running and ((k - 1) // CALLS_PER_TURN) % USERS % 3 == 2)
    return {'calls': CALLS, 'open': len(running), 'unknown': unknown, 'unpriced': unpriced, 'users': USERS}


def sql_seconds(contexts, statement):
    """The executor's own time for `statement`, as EXPLAIN ANALYZE reports it, in the administrator's request context."""
    output = contexts.runtime('admin', f'EXPLAIN (ANALYZE, TIMING OFF) {statement}')
    line = next(line for line in output.splitlines() if 'Execution Time:' in line)
    return float(line.split('Execution Time:')[1].split()[0]) / 1000


def verify(label, answer, declared):
    """The report holds every declared call on the year's days, grouped by each user with provider tests apart."""
    require(answer['reportFormat'] == 'usage_report_v1' and len(answer['days']) == DAYS, f'{label}: {len(answer["days"])} days')
    calls = [s['total']['calls'] for s in answer['currencies']]
    totals = {key: sum(c[key] for c in calls) for key in ('total', 'open', 'unknown', 'unpriced')}
    require(totals == {'total': declared['calls'], 'open': declared['open'], 'unknown': declared['unknown'], 'unpriced': declared['unpriced']},
            f'{label}: counted {totals}, declared {declared}')
    rows = sum(len(s['rows']) for s in answer['currencies'])
    require(len(answer['currencies']) == 1 and rows == declared['users'] + 1, f'{label}: {len(answer["currencies"])} currencies, {rows} rows')
    return rows


def run_benchmark(url, query, psql, manifest, psql_env, dotnet, api_pid):
    """The report of measurements; api_pid is the launcher-owned API process whose memory is sampled."""
    contexts = Contexts(query, manifest)
    client = PreviewClient(url)
    timed('start session', client.bootstrap)
    timed('select the administrator', lambda: client.call('profile', {'profile': 'admin'}))
    stamp = uuid4().hex[:8]
    seed_seconds, declared = timed('seed the workload', lambda: seed(client, query, psql, psql_env, manifest, contexts, stamp))
    stored_rows = dict(zip(('modelCalls', 'outcomes', 'turns'), map(int, query(
        f"SELECT (SELECT count(*) FROM assistant.model_calls WHERE(tenant_id,workspace_id)=('{contexts.tenant}','{contexts.workspace}'))||'|'||"
        "(SELECT count(*) FROM assistant.model_call_outcomes)||'|'||"
        f"(SELECT count(*) FROM assistant.turns WHERE(tenant_id,workspace_id)=('{contexts.tenant}','{contexts.workspace}'));").split('|'))))
    _, resident_before = timed('measure API memory before the reads', lambda: api_resident_bytes(api_pid))
    statement = f"SELECT assistant.usage_report('{ZONE}','{FIRST_DAY}','{END_DAY}','user');"
    sql = [round(sql_seconds(contexts, statement), 3) for _ in range(3)]
    request = {'zone': ZONE, 'from': FIRST_DAY, 'to': END_DAY, 'by': 'user'}
    http, size = [], None
    for attempt in range(3):
        seconds, answer = timed(f'read the year over HTTP ({attempt + 1})', lambda: client.call('settings/ai/usage/report', request))
        rows = verify('report', answer, declared)
        http.append(round(seconds, 3))
        size = len(json.dumps(answer, separators=(',', ':')).encode())
    operation = str(uuid4())
    export_seconds, exported = timed('export the year', lambda: client.call('settings/ai/usage/export', {'operationId': operation, **request}))
    verify('export', exported, declared)
    stored_text, stored_bytes = query(f"SELECT octet_length(after_state::text)||'|'||pg_column_size(after_state) FROM assistant.administration_audit "
                                      f"WHERE operation_id='{operation}';").split('|')
    tables = query("SELECT sum(pg_total_relation_size(c.oid)) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
                   "WHERE n.nspname='assistant' AND c.relname IN('model_calls','model_call_outcomes','turns');")
    _, resident = timed('measure API memory after the reads', lambda: api_resident_bytes(api_pid))
    _, described = timed('describe the environment', lambda: environment(query, dotnet))
    require(max(sql) < COMMAND_TIMEOUT_SECONDS and max([*http, export_seconds]) < COMMAND_TIMEOUT_SECONDS,
            f'a read took {max([*sql, *http, export_seconds])} seconds, past the {COMMAND_TIMEOUT_SECONDS} second command timeout')
    return {'environment': described,
            'workload': {'zone': ZONE, 'from': FIRST_DAY, 'to': END_DAY, 'days': DAYS, 'groupedBy': 'user', **declared,
                         'turns': USERS * TURNS_PER_USER, 'providerTests': TESTS, 'models': 3, 'agents': 3, 'currencies': 1, 'reportRows': rows},
            'seedSeconds': round(seed_seconds, 3), 'sqlExecutionSeconds': sql, 'httpReportSeconds': http, 'httpExportSeconds': round(export_seconds, 3),
            'reportJsonBytes': size, 'auditRecordTextBytes': int(stored_text), 'auditRecordStoredBytes': int(stored_bytes),
            'storedRows': stored_rows, 'callTurnOutcomeTableBytes': int(tables), 'apiResidentBytesBefore': resident_before,
            'apiResidentBytesAfter': resident, 'commandTimeoutSeconds': COMMAND_TIMEOUT_SECONDS}
