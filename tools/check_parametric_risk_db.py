"""Adversarial statistical input checks in the harness-owned stock database."""
from copy import deepcopy
import json
import hashlib
from uuid import uuid4


def run_checks(query, manifest, job_id, run_worker, method="parametric"):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    row = json.loads(query(f"SELECT jsonb_build_object('request',request,'frame',frame,'context',context_id,'revision',revision_id,'at',valuation_at,'source',jsonb_build_object('id',context_id,'effective_on',input_business_on,'known_at',input_known_at,'system_id',input_system_id,'timeline',input_timeline)) FROM risk.jobs WHERE id='{job_id}';"))
    request, frame, context = row['request'], row['frame'], row['context']
    from assistant_context import Contexts
    from native_read_transport import captured_input,select_owned_inputs
    Contexts(query,manifest).runtime('risk',select_owned_inputs(catalog,row['source']))
    literal = lambda value: "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    def reject(work, expected):
        try:
            work()
        except RuntimeError as error:
            assert expected in str(error), str(error)
        else:
            raise AssertionError('Accepted operation expected to refuse: ' + expected)
    def user(sql):
        token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{row['at']}');{sql};COMMIT;")
    def submit(chosen, payload=None):
        payload = payload or request
        return json.loads(user(f"SELECT risk.submit('{catalog}','{uuid4()}','{context}','{row['revision']}','{row['at']}',{literal(payload)},{literal(chosen)})"))
    plan = frame['factorHistory']
    for field in (('confidence','absolute','relative') if method=='parametric' else ('confidence',)):
        malformed=deepcopy(request)
        if field=='confidence':malformed[method]['policy'][field]=0.99
        else:malformed['parametric']['finiteDifference']['deltaTolerance'][field]=0
        reject(lambda: submit(frame,malformed),'risk_'+('monte_carlo' if method=='monteCarlo' else method)+'_input')
    assert int(query(f"SELECT count(*) FROM risk.job_history_observations WHERE job_id='{job_id}';")) == len(plan['observations'])
    assert len(plan['observations']) >= 250
    for table in ('history_clock_captures', 'job_history', 'job_history_observations'):
        for role in ('openetrm_runtime', 'openetrm_risk_worker'):
            assert query(f"SELECT has_table_privilege('{role}','risk.{table}','SELECT,INSERT,UPDATE,DELETE');") == 'f'
        reject(lambda: query(f"UPDATE risk.{table} SET catalog_id=catalog_id WHERE catalog_id='{catalog}';"), 'immutable')
    for login in ('preview_runtime', 'preview_risk_worker'):
        reject(lambda: query(f"SET SESSION AUTHORIZATION {login};SELECT risk.attest_workspace_clock(pg_backend_pid(),pg_current_xact_id(),'{catalog}','{context}',{literal(plan['calendar'])},{literal(plan['clock'])});"), 'permission denied')
    for edit in (
        lambda p: p['clock']['resolvedCloses'][0].update(offsetSeconds=3600),
        lambda p: p['clock'].update(rules=p['clock']['rules']+' ',fingerprint=hashlib.sha256((p['clock']['rules']+' ').encode()).hexdigest()),
        lambda p: p.update(clockCaptureId=str(uuid4())),
        lambda p: p.pop('clockCaptureId'),
    ):
        invalid = deepcopy(frame);edit(invalid['factorHistory'])
        reject(lambda: submit(invalid), 'risk_history_capture')
    # A receipt for a different certified forecast cannot authorize this frame.
    from check_workspace_clock import capture_fixture_clock
    other = Contexts(query,manifest).runtime('risk',f"SELECT security.resolve_operation_context('{catalog}','2020-12-17',NULL);")
    foreign = capture_fixture_clock(query,manifest,captured_input(query,other),'2020-12-17T16:00:00Z',plan['calendar'],plan['clock'])
    invalid = deepcopy(frame);invalid['factorHistory']['clockCaptureId'] = foreign
    reject(lambda: submit(invalid), 'risk_history_capture')
    def capture(calendar,clock):return capture_fixture_clock(query,manifest,row['source'],row['at'],calendar,clock)
    assert capture(plan['calendar'], plan['clock']) == plan['clockCaptureId']
    # Mint is trusted API work; orphan retries reuse the same UUID, and distinct
    # unpublished captures cannot grow without a per-context bound.
    for i in range(16):
        clock = deepcopy(plan['clock']);clock['tzdbVersion'] = 'capture_bound_' + str(i)
        receipt = capture(plan['calendar'], clock)
        assert capture(plan['calendar'], clock) == receipt
    clock['tzdbVersion'] = 'capture_bound_excess'
    reject(lambda: capture(plan['calendar'], clock), 'risk_capture_capacity')
    print('PASS statistical SQL receipt: immutable exact pins; runtime/worker cannot mint/read; altered clocks, missing and cross-context receipts refuse; retries reuse bounded captures', flush=True)
    # An actual owned worker lease is necessary, and the read still requires its
    # bound actor/tenant/workspace/catalog/context/valuation transaction.
    queued = submit(frame);jid=queued['jobId']
    worker = lambda sql: query('SET SESSION AUTHORIZATION preview_risk_worker;' + sql + ';')
    claim=json.loads(worker(f"SELECT risk.claim(30,'{request['engineVersion']}')"));assert claim['jobId']==jid
    lease=claim['leaseToken']
    read=lambda target=jid,token=lease: f"SELECT count(*) FROM risk.history_inputs('{target}','{token}')"
    reject(lambda: worker(read()), 'risk_job_denied')
    reject(lambda: worker(f"BEGIN;SELECT risk.bind('{jid}','{lease}');"+read(token=str(uuid4()))+';COMMIT'), 'risk_job_fence')
    reject(lambda: worker(f"BEGIN;SELECT risk.bind('{jid}','{lease}');"+read(target=job_id)+';COMMIT'), 'risk_job_fence')
    assert worker(f"BEGIN;SELECT risk.bind('{jid}','{lease}');"+read()+';COMMIT').splitlines()[-1] == str(len(plan['observations']))
    # Historical observation visibility stays confined to the dedicated source
    # read. It does not add old marks to the ordinary current-PV RLS pin set.
    visible = worker(f"BEGIN;SELECT risk.bind('{jid}','{lease}');SELECT coalesce(jsonb_agg(id ORDER BY id),'[]'::jsonb) FROM market_data.observations WHERE catalog_id='{catalog}';COMMIT")
    baseline_ids = visible.splitlines()[-1]
    for mutation in (
        "UPDATE security.request_contexts SET expires_at=clock_timestamp()-interval '1 second' WHERE backend_pid=pg_backend_pid()",
        "UPDATE security.role_grants SET granted_to=clock_timestamp()-interval '1 second' WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='preview_risk_worker')",
    ):
        reject(lambda: query(f"BEGIN;SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.bind('{jid}','{lease}');RESET SESSION AUTHORIZATION;{mutation};SET SESSION AUTHORIZATION preview_risk_worker;"+read()+';ROLLBACK;'), 'risk_job_denied')
    user(f"SELECT risk.cancel('{jid}')")
    reject(lambda: worker(f"BEGIN;SELECT risk.bind('{jid}','{lease}');"+read()+';COMMIT'), 'risk_job_cancelled')
    worker(f"SELECT risk.acknowledge_cancel('{jid}','{lease}')")
    plain_frame=deepcopy(frame);plain_frame.pop('factorHistory');plain_request=deepcopy(request);plain_request.pop(method)
    if method in ('historical','monteCarlo'):
        plain_request['bumps']=[{'factor':request[method]['factors'][0],'size':'0.01','convention':'absolute_curve_coordinate_symmetric_v1'}]
    plain=submit(plain_frame,plain_request);pid=plain['jobId']
    ordinary=json.loads(worker(f"SELECT risk.claim(30,'{request['engineVersion']}')"));assert ordinary['jobId']==pid
    ordinary_visible=worker(f"BEGIN;SELECT risk.bind('{pid}','{ordinary['leaseToken']}');SELECT coalesce(jsonb_agg(id ORDER BY id),'[]'::jsonb) FROM market_data.observations WHERE catalog_id='{catalog}';COMMIT")
    assert ordinary_visible.splitlines()[-1]==baseline_ids, 'Statistical pins must not widen the existing bound observation RLS'
    worker(f"SELECT risk.finish('{pid}','{ordinary['leaseToken']}',NULL,'qualification_complete')")
    print('PASS statistical worker custody: exact active lease and bound context, foreign/unbound/wrong-token/cancel refusal, no expanded current-PV visibility', flush=True)

    missing=deepcopy(frame);missing['factorHistory']['observations'].pop(0)
    incomplete=submit(missing);run_worker()
    outcome=json.loads(query(f"SELECT jsonb_build_object('state',state,'result',result) FROM risk.jobs WHERE id='{incomplete['jobId']}';"))
    assert outcome['state']=='failed' and outcome['result'] is None, 'Missing evidence must never produce a successful partial calibration'
    print('PASS statistical retained evidence: missing historical source pins fail reconstruction without partial risk results',flush=True)
