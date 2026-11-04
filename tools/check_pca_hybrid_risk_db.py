"""Owned-database qualification of PCA / hybrid policy custody and atomic upgrades."""
from copy import deepcopy
import json
from uuid import uuid4


def upgrade_checks(query,migration,consent=False):
    guard=migration[:migration.index('CREATE OR REPLACE FUNCTION')]
    signature='risk.retain_monte_carlo(risk.jobs)'
    definition=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    for mutation in (definition.replace('BEGIN','BEGIN PERFORM 1;',1)+';',f'ALTER FUNCTION {signature} SET search_path=pg_catalog,public;',f'GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;',f'ALTER FUNCTION {signature} OWNER TO openetrm_runtime;'):
        try:query('BEGIN;'+mutation+guard+'ROLLBACK;')
        except RuntimeError as error:assert ('pca_hybrid_consent_upgrade' if consent else 'pca_hybrid_risk_upgrade') in str(error),str(error)
        else:raise AssertionError('Changed retained simulation helper accepted PCA upgrade')
    assert query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")==definition
    print('PASS PCA / hybrid upgrade: complete helper body, owner, ACL and search path mutations refuse atomically',flush=True)


def admission_checks(query,manifest,job_id):
    row=json.loads(query(f"SELECT jsonb_build_object('request',request,'frame',frame,'context',context_id,'revision',revision_id,'at',valuation_at) FROM risk.jobs WHERE id='{job_id}';"))
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    literal=lambda value:"'"+json.dumps(value).replace("'","''")+"'::jsonb"
    cases=[]
    for field,value in (('retainedComponents',0),('retainedComponents',9),('retainedComponents',2),('tradingTicks',0),('tradingTicks',1001),('lossRelative',None),('lossRelative','-1'),('lossRelative','000.1'),('lossAbsolute','1e3'),('stabilityRelative','0.0000000000000000001'),('valuationPolicy','panel_validation'),('valuationPolicy','full_reference_qualification_v1'),('residualPolicy','drop_residual'),('unknown',True)):
        bad=deepcopy(row['request']);bad['monteCarlo']['analysis'][field]=value;cases.append(bad)
    for field in row['request']['monteCarlo']['analysis']:
        bad=deepcopy(row['request']);bad['monteCarlo']['analysis'].pop(field);cases.append(bad)
    for request in cases:
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        try:query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{row['context']}');SELECT security.use_trading_context('{catalog}','{row['at']}');SELECT risk.submit('{catalog}','{uuid4()}','{row['context']}','{row['revision']}','{row['at']}',{literal(request)},{literal(row['frame'])});COMMIT;")
        except RuntimeError as error:assert 'risk_monte_carlo_input' in str(error),str(error)
        else:raise AssertionError('Invalid PCA / hybrid policy admitted')
    for field,value in (('valuationPolicy','full_reference_opt_in_full_pv_qualification_v1'),('lossAbsolute','-0.0'),('lossRelative','0.00100'),('stabilityRelative','0.00010')):
        valid=deepcopy(row['request']);valid['monteCarlo']['analysis'][field]=value
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{row['context']}');SELECT security.use_trading_context('{catalog}','{row['at']}');SELECT risk.submit('{catalog}','{uuid4()}','{row['context']}','{row['revision']}','{row['at']}',{literal(valid)},{literal(row['frame'])});ROLLBACK;")
    assert query(f"SELECT count(*) FROM risk.jobs WHERE id='{job_id}' AND state='completed';")=='1'
    print('PASS PCA / hybrid SQL: explicit complete policy, factor-bound components, representable tolerances and full-reference mode required',flush=True)
