"""Exact final-input custody and owned worker reads in disposable PostgreSQL.

This verifies native scope and source lineage. Numerical lifecycle projection
and complete graph replay have separate managed/native qualifiers.
"""
from copy import deepcopy
import json
from uuid import uuid4
from check_reference_editing import main
from check_risk_jobs_db import provision_worker
import check_lifecycle_risk_jobs as economic

_before = {}
SIGNATURES = ('security.require_lifecycle_projection_input(uuid,uuid,text,text,uuid,uuid,timestamptz,date,timestamptz)',
              'security.read_lifecycle_projection_final(uuid,uuid,text,text,uuid,timestamptz,uuid,timestamptz,date)')


def before_migration(query, migration):
    economic.before_migration(query, migration)
    if migration.name != '0281_lifecycle_projection_final_scope.sql':
        return
    for signature in SIGNATURES:
        _before[signature] = query(f"SELECT to_regprocedure('{signature}')::oid;")
    from check_fact_anchor_upgrade import run_checks
    run_checks(query, migration, number=migration.name[:4], guard='lifecycle_final_upgrade', signatures=SIGNATURES)


def final_scope(query, manifest):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    literal = lambda value: "'"+json.dumps(value).replace("'", "''")+"'::jsonb"
    def user(sql):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def worker(sql):
        return query('SET SESSION AUTHORIZATION synthetic_final_scope_worker;'+sql)
    def reject(work, code):
        try:
            work()
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Expected '+code+' for the exact retained final-input scope')
    original = json.loads(query("SELECT jsonb_build_object('request',request,'frame',frame)::text FROM risk.jobs "
        "WHERE request->>'engineVersion'='synthetic_economic_engine_v1' ORDER BY created_at,id LIMIT 1;"))
    request = original['request'];request['engineVersion'] = 'synthetic_final_scope_v1'
    frame = original['frame'];frame['id'] = str(uuid4());frame['at'] = '2020-12-28T14:00:00Z';frame['businessDate'] = '2020-12-28'
    # The native final point is an upstream input, absent from output membership.
    frame['curveContracts'].pop('future', None)
    contract = '50000000-0000-0000-0000-000000000001';other = '50000000-0000-0000-0000-000000000006'
    revision = manifest['revisionId']
    pin = json.loads(query(f"SELECT jsonb_build_object('id',o.id,'series',o.series_id)::text FROM market_data.observations o "
        f"JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
        f"WHERE o.catalog_id='{catalog}' AND s.contract_id='{contract}' AND o.id=ANY(ARRAY[{','.join(repr(v)+'::uuid'for v in frame['observationIds'])}]) "
        "AND o.role='daily_settlement' ORDER BY o.observed_at DESC LIMIT 1;"))
    final_at = query(f"SELECT expiry_at FROM market_data.contracts WHERE(catalog_id,revision_id,id)=('{catalog}','{revision}','{contract}');")
    def capture():
        return user(f"SELECT security.capture_date_context('{catalog}','2020-12-28',NULL);")
    def bind(context, clock=frame['at'], day='2020-12-28'):
        return f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{clock}','{day}'::date);"
    def source(context):
        packet = deepcopy(frame);packet['contextId'] = context
        request['context'].update(snapshotId=frame['id'],contextId=context)
        horizon = json.loads(user(bind(context)+f"SELECT(security.read_lifecycle_projection_horizon('{catalog}','synthetic_lifecycle_risk',1,'{context}','{frame['at']}','2020-12-28')-'authorityUntil')::text;"))
        value = json.loads(user(bind(context)+f"SELECT(security.read_lifecycle_projection_final('{catalog}','{revision}','future','future','{contract}','{final_at}','{context}','{frame['at']}','2020-12-28')-'authorityUntil')::text;"))
        value['openingInputs'] = [dict(snapshotId=frame['id'],at=frame['at'],observationId=pin['id'],seriesId=pin['series'],use='Variable')]
        packet['economicLifecycle'] = dict(horizon=horizon,products=[],optionCalibrations=[],inputFinals=[])
        return deepcopy(request), packet, value
    old_request, old_frame, hypothetical = source(capture())
    assert hypothetical['observation'] is None
    final_id = str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{final_id}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
        f"source_code,'synthetic_future_final','1','final_contract_settlement','final','{final_at}',clock_timestamp(),4.25,NULL "
        f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{pin['id']}');")
    payload, retained, actual = source(capture())
    assert actual['observation']['id'] == final_id
    context = retained['contextId']
    def submit(body=payload, packet=retained):
        return json.loads(user(bind(packet['contextId'],packet['at'],packet['businessDate'])+f"SELECT risk.submit('{catalog}','{uuid4()}','{packet['contextId']}','{revision}',"
            f"'{packet['at']}',{literal(body)},{literal(packet)})::text;"))
    provision_worker(query,manifest,'synthetic_final_scope_worker')
    def claim(job):
        selected = json.loads(worker("SELECT risk.claim(60,'synthetic_final_scope_v1')::text;"))
        assert selected['jobId'] == job['jobId'], selected
        return f"BEGIN;SELECT risk.bind('{job['jobId']}','{selected['leaseToken']}');"
    def read(selected_contract=contract, at=final_at, selected_context=context, clock=frame['at'], day='2020-12-28'):
        return f"SELECT security.read_lifecycle_projection_final('{catalog}','{revision}','future','future','{selected_contract}','{at}','{selected_context}','{clock}','{day}')::text;"
    empty = submit();owned = claim(empty)
    reject(lambda:worker(owned+read()+'COMMIT;'),'risk_job_scope')
    print('PASS economic worker cannot read an undeclared final even when its opening quote is retained',flush=True)
    retained['economicLifecycle']['inputFinals'] = [actual]
    accepted = submit();owned = claim(accepted)
    result = json.loads(worker(owned+read()+'COMMIT;').splitlines()[-1])
    assert result['observation']['id'] == final_id
    assert query(f"SELECT count(*) FROM risk.job_contracts WHERE job_id='{accepted['jobId']}' AND contract_id='{contract}';") == '0'
    for sql in (read(selected_contract=other),read(at='2020-12-28T16:00:00Z'),read(selected_context=old_frame['contextId']),
                read(clock='2020-12-28T14:01:00Z'),read(day='2020-12-29')):
        reject(lambda sql=sql:worker(owned+sql+'COMMIT;'),'risk_job_scope')
    print('PASS declared unheld upstream final retains exact contract, fixing, opening clock, business day and certified knowledge',flush=True)
    old_frame['economicLifecycle']['inputFinals'] = [hypothetical]
    old_job = submit(old_request,old_frame);owned_old = claim(old_job)
    old_value = json.loads(worker(owned_old+read(selected_context=old_frame['contextId'])+'COMMIT;').splitlines()[-1])
    assert old_value['observation'] is None
    for change in ('snapshot','use','observation','extra'):
        malformed=deepcopy(retained);item=malformed['economicLifecycle']['inputFinals'][0]
        if change=='snapshot':item['openingInputs'][0]['snapshotId']=str(uuid4())
        elif change=='use':item['openingInputs'][0]['use']='FrozenFact'
        elif change=='observation':item['observation']=None
        else:malformed['economicLifecycle']['inputFinals'].append(deepcopy(item))
        reject(lambda:submit(packet=malformed),'risk_economic_final_input')
    # An explicit frozen source retains final-settlement role and original
    # use; expiry alone does not turn an ordinary daily quote into a fixed fact.
    def frozen_packet(selected_pin, selected_context):
        packet=deepcopy(retained);packet['id']=str(uuid4());packet['contextId']=selected_context;packet['at']='2020-12-28T16:00:00Z'
        packet['observationIds'].append(selected_pin);packet['frozenObservationIds']=[selected_pin]
        body=deepcopy(payload);body['context'].update(snapshotId=packet['id'],contextId=selected_context)
        at='2020-12-29T15:30:00Z'
        packet['economicLifecycle']['horizon']=json.loads(user(bind(selected_context,packet['at'])+
            f"SELECT(security.read_lifecycle_projection_horizon('{catalog}','synthetic_lifecycle_risk',1,'{selected_context}','{packet['at']}','2020-12-28')-'authorityUntil')::text;"))
        final=json.loads(user(bind(selected_context,packet['at'])+
            f"SELECT(security.read_lifecycle_projection_final('{catalog}','{revision}','future','future','{contract}','{at}','{selected_context}','{packet['at']}','2020-12-28')-'authorityUntil')::text;"))
        final['openingInputs']=[dict(snapshotId=packet['id'],at=packet['at'],observationId=selected_pin,seriesId=pin['series'],use='FrozenFact')]
        packet['economicLifecycle']['inputFinals']=[final]
        return body,packet
    frozen_body,frozen_frame=frozen_packet(final_id,context)
    frozen_job=submit(frozen_body,frozen_frame);frozen_owned=claim(frozen_job)
    frozen_value=json.loads(worker(frozen_owned+read(at='2020-12-29T15:30:00Z',clock=frozen_frame['at'])+'COMMIT;').splitlines()[-1])
    assert frozen_value['observation'] is None
    wrong_role=deepcopy(frozen_frame);wrong_role['observationIds'].remove(final_id);wrong_role['frozenObservationIds']=[pin['id']]
    wrong_role['economicLifecycle']['inputFinals'][0]['openingInputs'][0]['observationId']=pin['id']
    reject(lambda:submit(frozen_body,wrong_role),'risk_economic_final_input')
    early=str(uuid4())
    query(f"INSERT INTO market_data.observations SELECT catalog_id,'{early}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
        f"source_code,'synthetic_early_final','1','final_contract_settlement','final','2020-12-28T14:30:00Z',clock_timestamp(),4.00,NULL "
        f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{pin['id']}');")
    early_body,early_frame=frozen_packet(early,capture())
    reject(lambda:submit(early_body,early_frame),'risk_economic_final_input')
    print('PASS explicit frozen opening sources retain final role and expiry evidence; daily quotes and premature finals cannot substitute',flush=True)
    for signature,oid in _before.items():
        assert query(f"SELECT to_regprocedure('{signature}')::oid;")==oid,'A public helper OID changed'
        private=signature.replace('(', '_pre_final_scope(',1)
        assert query(f"SELECT has_function_privilege('preview_runtime','{private}','EXECUTE') OR has_function_privilege('synthetic_final_scope_worker','{private}','EXECUTE');")=='f'
    print('PASS final-input custody rejects substituted opening lineage, input uses, known finals and duplicate declarations; older absence stays pinned',flush=True)


def checks(query,manifest):
    economic.checks(query,manifest)
    final_scope(query,manifest)


if __name__=='__main__':
    main(checks=checks,before_migration=before_migration)
