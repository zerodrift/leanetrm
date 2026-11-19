"""Exercise the pool UI's HTTP operations and actual worker on disposable PostgreSQL.
The controller inventory and service observations in this fixture are synthetic.
"""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4
from preview_client import PreviewClient
import select
import subprocess
from owned_process import stop_owned_group


class ReadBarrier:
    def __init__(self, psql, environment): self.psql, self.environment = psql, environment
    def __enter__(self):
        self.process = subprocess.Popen(self.psql, env=self.environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, start_new_session=True)
        self.process.stdin.write("BEGIN;SET LOCAL lock_timeout='2s';LOCK TABLE trading.trade_revisions IN ACCESS EXCLUSIVE MODE;SELECT 'READY';\n")
        self.process.stdin.flush()
        if not select.select([self.process.stdout], [], [], 5)[0] or self.process.stdout.readline().strip() != 'READY':
            stop_owned_group(self.process); raise AssertionError('Owned read barrier did not acquire its exact fixture lock.')
        return self
    def __exit__(self, *_):
        out, error = self.process.communicate('ROLLBACK;\n\\q\n', timeout=10)
        if self.process.returncode: raise AssertionError('Owned read barrier failed: ' + out + error)


def selected_request(client):
    workspace = client.call('workspace')
    frame = next(f for f in workspace['frames'] if f['at'].startswith('2020-12-18'))
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    catalog = client.call('portfolio/risk/catalog', {'context': context, 'bookIds': [], 'portfolioIds': []})
    context = {**context, 'knowledgeMode': 'retained', 'contextId': catalog['context']['contextId']}
    request = {'context': context, 'bookIds': [], 'portfolioIds': [], 'detail': 'holdings',
               'convention': catalog['convention'], 'numericalPolicy': catalog['numericalPolicy'], 'pricingPolicy': catalog['pricingPolicy'],
               'bumps': [{'factor': f['factor'], 'size': '0.1', 'convention': catalog['bumpConvention']} for f in catalog['factors'][:2]],
               'stress': []}
    return frame, request


CLUSTER='syntheticcluster00000000001'
SERVICE='syntheticservice00000000001'
TASK='synthetictask000000000001'
NODE='syntheticnode0000000000001'

def check_scoped_access(query):
    # Bind a disposable runtime context to a finished run. Its actor retains
    # operator registration, while the run grants no current scale authority.
    token, run = uuid4(), uuid4()
    query(f"""BEGIN;
        INSERT INTO automation.runs(id,job_id,tenant_id,workspace_id,due_at,job_version,state,fence,attempt,
                                    worker_id,worker_login,lease_until,started_at,deadline_at)
        SELECT '{run}',id,tenant_id,workspace_id,next_due_at,version,'succeeded',1,1,gen_random_uuid(),
               'jobs_fixture_worker',now(),now(),now() FROM automation.jobs WHERE operation='worker_check' LIMIT 1;
        INSERT INTO security.request_contexts(id,actor_id,tenant_id,workspace_id,runtime_login,expires_at,backend_pid,transaction_id)
        SELECT '{token}',principal_id,tenant_id,workspace_id,'preview_runtime',now()+interval '1 minute',
               pg_backend_pid(),pg_current_xact_id() FROM security.workspace_memberships WHERE login_handle='admin';
        INSERT INTO automation.pilot_run_contexts SELECT '{token}',id,fence,worker_id,worker_login FROM automation.runs WHERE id='{run}';
        SET LOCAL SESSION AUTHORIZATION preview_runtime;
        DO $check$ BEGIN
          IF security.has_permission('ScaleWorkerPool') OR (automation.pool_directory()->>'canScale')::boolean THEN
            RAISE EXCEPTION 'An inactive run offered pool controls';
          END IF;
          BEGIN
            PERFORM automation.scale_pool('{CLUSTER}',gen_random_uuid(),0,1);
            RAISE EXCEPTION 'An inactive run was allowed to scale';
          EXCEPTION WHEN insufficient_privilege THEN NULL;
          END;
        END $check$;
        ROLLBACK;""")


def check(url, query, worker, controller, read_barrier):
    query(f"""INSERT INTO automation.risk_pools(cluster_id,worker_login,service_id,approved_spec,maximum)
        VALUES('{CLUSTER}','preview_risk_worker','{SERVICE}','{{"Labels":{{}},"Mode":{{"Replicated":{{}}}}}}',2);
        INSERT INTO security.role_permissions SELECT id,permission FROM security.roles
        CROSS JOIN (VALUES('ScaleWorkerPool'),('ViewWorkerDiagnostics')) x(permission) WHERE code='access_administrator';""")
    admin=PreviewClient(url);admin.bootstrap();admin.call('profile',{'profile':'admin'})
    viewer=PreviewClient(url);viewer.bootstrap();viewer.call('profile',{'profile':'viewer'})
    risk=PreviewClient(url);risk.bootstrap();risk.call('profile',{'profile':'risk'})
    check_scoped_access(query)
    def directory(client=admin):return client.call('jobs')['workerPool']
    def request(replicas):return {'clusterId':CLUSTER,'replicas':replicas,'expectedVersion':directory()['generation'],'requestId':str(uuid4())}
    def inspect(desired, generation, tasks):
        rows=json.dumps([{'id':TASK,'node':NODE,'slot':1,'state':'running'}] if tasks else [])
        return query(f"SET SESSION AUTHORIZATION jobs_fixture_worker;SELECT automation.inspect_pool('{CLUSTER}','{SERVICE}',{directory()['generation']},{desired},'{rows}',true,'{generation}');")
    path='infrastructure/pool/scale'
    start=request(1)
    viewer.call(path,start,expected=403)
    viewer.call('infrastructure/logs/worker',expected=403)
    assert directory(viewer)['canScale'] is False
    accepted=admin.call(path,start)
    assert admin.call(path,start)==accepted and inspect(0,'',False)=='1'
    controller()
    assert inspect(1,1,True)==''
    worker()
    assert directory()['tasks'][0]['state']=='idle'
    _,inputs=selected_request(risk)
    def wait_busy():
        end=time.monotonic()+8
        while directory()['tasks'][0]['state']!='busy':
            if time.monotonic()>end:raise AssertionError('Worker never became busy')
            time.sleep(.025)

    cancelled=risk.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':inputs})
    resource='portfolio/risk/jobs/'+cancelled['id']
    with ThreadPoolExecutor(max_workers=1) as executor:
        with read_barrier():
            running=executor.submit(worker)
            wait_busy()
            assert risk.call(resource+'/cancel',{})['state']=='cancellation_requested'
            query(f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{cancelled['id']}';")
        running.result(timeout=60)
    assert risk.call(resource)['state']=='cancelled', 'Stopped exact attempt must acknowledge its late cancellation'
    job=risk.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':inputs})
    queued=risk.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':inputs})
    with ThreadPoolExecutor(max_workers=1) as executor:
        with read_barrier():
            running=executor.submit(worker)
            wait_busy()
            admin.call(path,request(0))
            assert inspect(1,1,False)=='' and directory()['diagnostic']=='draining'
            assert directory()['tasks'][0]['state']=='busy'
            assert inspect(1,1,True)=='' and directory()['diagnostic']=='draining'
        running.result(timeout=60)
    assert risk.call('portfolio/risk/jobs/'+job['id'])['state']=='completed'
    logs=admin.call('infrastructure/logs/worker')
    assert any('completed' in event['message'] for event in logs)
    assert admin.call('infrastructure/logs/node')
    worker()
    assert risk.call('portfolio/risk/jobs/'+queued['id'])['state']=='queued'
    assert inspect(1,1,True)=='0'
    inspect(0,2,False)
    assert directory()['observed']==0 and not directory()['tasks']
    risk.call('portfolio/risk/jobs/'+queued['id']+'/cancel',{})
    print('PASS managed pool: scoped capacity and diagnostics, start and retry, real idle heartbeat and completed valuation, drain preserves queued work, observed stop',flush=True)
    return admin,risk
