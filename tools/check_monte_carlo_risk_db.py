"""Negative native admission and guarded upgrade checks for full-PV simulation."""
from copy import deepcopy
import json
from uuid import uuid4


def upgrade_checks(query,migration):
    guard=migration[:migration.index('CREATE OR REPLACE FUNCTION risk.axis_commands')]
    definition=query("SELECT pg_get_functiondef('risk.axis_commands(jsonb)'::regprocedure);")
    for mutation in (definition.replace('BEGIN','BEGIN PERFORM 1;',1)+';',"ALTER FUNCTION risk.axis_commands(jsonb) SET search_path=pg_catalog,public;","GRANT EXECUTE ON FUNCTION risk.axis_commands(jsonb) TO PUBLIC;","ALTER FUNCTION risk.axis_commands(jsonb) OWNER TO openetrm_runtime;"):
        try:query('BEGIN;'+mutation+guard+'ROLLBACK;')
        except RuntimeError as error:assert 'monte_carlo_risk_upgrade' in str(error),str(error)
        else:raise AssertionError('Changed helper accepted Monte Carlo upgrade')
    assert query("SELECT pg_get_functiondef('risk.axis_commands(jsonb)'::regprocedure);")==definition
    print('PASS Monte Carlo upgrade: changed helper body, ACL, owner and search path refuse atomically',flush=True)


def admission_checks(query,manifest,job_id):
    row=json.loads(query(f"SELECT jsonb_build_object('request',request,'frame',frame,'context',context_id,'revision',revision_id,'at',valuation_at) FROM risk.jobs WHERE id='{job_id}';"))
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    literal=lambda value:"'"+json.dumps(value).replace("'","''")+"'::jsonb"
    cases=[]
    for field,value in (('seed','01'),('seed','18446744073709551616'),('paths',0),('distribution','lognormal'),('drift',''),('confidence','0.500000000000000001'),('samplingUncertainty','none')):
        bad=deepcopy(row['request']);bad['monteCarlo']['policy'][field]=value;cases.append(bad)
    for field in ('historical','parametric','joint'):
        bad=deepcopy(row['request']);bad[field]={};cases.append(bad)
    for factors in ([],row['request']['monteCarlo']['factors']*2):
        bad=deepcopy(row['request']);bad['monteCarlo']['factors']=factors;cases.append(bad)
    bad=deepcopy(row['request']);bad['detail']='books';cases.append(bad)
    for request in cases:
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        try:query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{row['context']}');SELECT security.use_trading_context('{catalog}','{row['at']}');SELECT risk.submit('{catalog}','{uuid4()}','{row['context']}','{row['revision']}','{row['at']}',{literal(request)},{literal(row['frame'])});COMMIT;")
        except RuntimeError as error:assert 'risk_monte_carlo_input' in str(error) or 'risk_axis_input' in str(error),str(error)
        else:raise AssertionError('Invalid full-PV Monte Carlo admitted')
    assert query(f"SELECT count(*) FROM risk.jobs WHERE id='{job_id}' AND state='completed';")=='1'
    print('PASS Monte Carlo SQL admission: explicit generator/policy/seed, exclusive method and complete native axes refuse invalid inputs',flush=True)
