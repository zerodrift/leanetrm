"""Negative migration/admission checks in a harness-owned stock database."""
from copy import deepcopy
import json
from uuid import uuid4


def upgrade_checks(query, migration):
    guard=migration[:migration.index('CREATE OR REPLACE FUNCTION risk.axis_commands')]
    definition=query("SELECT pg_get_functiondef('risk.axis_commands(jsonb)'::regprocedure);")
    changed=definition.replace('BEGIN','BEGIN PERFORM 1;',1)
    assert changed!=definition
    for mutation in (changed+';',"ALTER FUNCTION risk.axis_commands(jsonb) SET search_path=pg_catalog,public;",
                     "GRANT EXECUTE ON FUNCTION risk.axis_commands(jsonb) TO PUBLIC;",
                     "ALTER FUNCTION risk.axis_commands(jsonb) OWNER TO openetrm_runtime;"):
        try:query('BEGIN;'+mutation+guard+'ROLLBACK;')
        except RuntimeError as error:assert 'historical_risk_upgrade' in str(error),str(error)
        else:raise AssertionError('Changed privileged helper accepted historical upgrade')
    assert query("SELECT pg_get_functiondef('risk.axis_commands(jsonb)'::regprocedure);")==definition
    print('PASS historical upgrade: altered helper body, ACL, owner and search path refuse atomically',flush=True)


def admission_checks(query,manifest,job_id):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    row=json.loads(query(f"SELECT jsonb_build_object('request',request,'frame',frame,'context',context_id,'revision',revision_id,'at',valuation_at) FROM risk.jobs WHERE id='{job_id}';"))
    literal=lambda value:"'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def submit(request,frame=None):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{row['context']}');SELECT security.use_trading_context('{catalog}','{row['at']}');SELECT risk.submit('{catalog}','{uuid4()}','{row['context']}','{row['revision']}','{row['at']}',{literal(request)},{literal(frame or row['frame'])});COMMIT;")
    cases=[]
    for factors in ([],row['request']['historical']['factors']*2):
        bad=deepcopy(row['request']);bad['historical']['factors']=factors;cases.append((bad,'risk_historical_input'))
    for field in ('joint','parametric'):
        bad=deepcopy(row['request']);bad[field]={};cases.append((bad,'risk_historical_input'))
    bad=deepcopy(row['request']);bad['historical']['policy']['horizon']='ten_days';cases.append((bad,'risk_historical_input'))
    bad=deepcopy(row['request']);bad['detail']='books';cases.append((bad,'risk_historical_input'))
    for extra in (False,True):
        bad=deepcopy(row['request']);replacement=deepcopy(bad['historical']['factors'][0]);replacement['target']['contract']=str(uuid4())
        if extra:bad['historical']['factors'].append(replacement)
        else:bad['historical']['factors'][0]=replacement
        cases.append((bad,'risk_historical_input' if extra else 'risk_axis_input'))
    for request,expected in cases:
        try:submit(request)
        except RuntimeError as error:assert expected in str(error),str(error)
        else:raise AssertionError('Invalid historical admission accepted')
    assert query(f"SELECT count(*) FROM risk.jobs WHERE id='{job_id}' AND state='completed';")=='1'
    print('PASS historical SQL admission: missing/duplicate factors, mixed envelopes, foreign horizon and partial-book detail refuse without changing the completed run',flush=True)
