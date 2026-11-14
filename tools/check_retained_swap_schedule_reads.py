"""Guard the source-preserving retained scheduled-swap read upgrade on stock PostgreSQL."""
from check_reference_editing import main
from assistant_context import migration_body
import json
import os
from pathlib import Path
import subprocess
from copy import deepcopy
from uuid import uuid4

PERIODS=('indexed_whole_term_swap_period_input','whole_term_swap_period_input','contract_observation_evidence')


def predecessor_readers(query):
    names=(*PERIODS,'scheduled_swap_terms')
    for name in names:
        arguments='uuid,uuid,integer,uuid'if name=='scheduled_swap_terms'else'uuid,uuid,integer'
        definition=query(f"SELECT pg_get_functiondef('trading.{name}({arguments})'::regprocedure);")
        for called in names:
            definition=definition.replace('trading.'+called+'(', 'public.synthetic_before_'+called+'(')
            definition=definition.replace(called+'.','synthetic_before_'+called+'.')
        query(definition.rstrip().removesuffix(';')+';'+f"ALTER FUNCTION public.synthetic_before_{name}({arguments}) OWNER TO openetrm_security_owner;"
            f"REVOKE ALL ON FUNCTION public.synthetic_before_{name}({arguments}) FROM PUBLIC;"
            f"GRANT EXECUTE ON FUNCTION public.synthetic_before_{name}({arguments}) TO openetrm_runtime,openetrm_risk_worker;")
    definition=query("SELECT pg_get_viewdef('trading.swap_revision_projection'::regclass,true);")
    definition=definition.replace('trading.scheduled_swap_terms(', 'public.synthetic_before_scheduled_swap_terms(')
    query('CREATE VIEW public.synthetic_before_swap_projection WITH(security_invoker=true) AS '+definition.rstrip().removesuffix(';')+';'+
        'ALTER VIEW public.synthetic_before_swap_projection OWNER TO openetrm_security_owner;'
        'REVOKE ALL ON public.synthetic_before_swap_projection FROM PUBLIC;'
        'GRANT SELECT ON public.synthetic_before_swap_projection TO openetrm_runtime,openetrm_risk_worker;')


SIGNATURE='trading.scheduled_swap_terms(uuid,uuid,integer,uuid)'


def upgrade_checks(query,migration):
    from check_lifecycle_risk_jobs import before_migration as risk_upgrade_checks
    risk_upgrade_checks(query,migration)
    if migration.name!='0296_retained_swap_schedule_reads.sql':return
    body=migration_body(migration.read_text())
    function=[
        f"ALTER FUNCTION {SIGNATURE} SET work_mem='64kB';",
        f"ALTER FUNCTION {SIGNATURE} STRICT;",
        f"ALTER FUNCTION {SIGNATURE} LEAKPROOF;",
        f"ALTER FUNCTION {SIGNATURE} SECURITY DEFINER;",
        f"ALTER FUNCTION {SIGNATURE} VOLATILE;",
        f"ALTER FUNCTION {SIGNATURE} PARALLEL SAFE;",
        f"GRANT EXECUTE ON FUNCTION {SIGNATURE} TO PUBLIC;",
        f"GRANT EXECUTE ON FUNCTION {SIGNATURE} TO openetrm_runtime WITH GRANT OPTION;",
        f"ALTER FUNCTION {SIGNATURE} OWNER TO openetrm_security_owner;",
        f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{SIGNATURE}'::regprocedure;",
        f"UPDATE pg_proc SET proargmodes=ARRAY['i','i','i','i']::\"char\"[] WHERE oid='{SIGNATURE}'::regprocedure;",
        f"UPDATE pg_proc SET proargnames=ARRAY['changed','trade','revision_number','instrument'] WHERE oid='{SIGNATURE}'::regprocedure;",
        f"UPDATE pg_proc SET prorettype='text'::regtype WHERE oid='{SIGNATURE}'::regprocedure;",
        f"UPDATE pg_proc SET proretset=true WHERE oid='{SIGNATURE}'::regprocedure;",
        f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid='{SIGNATURE}'::regprocedure;",
        f"DO $$DECLARE d text=pg_get_functiondef('{SIGNATURE}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;",
    ]
    view=[
        'ALTER VIEW trading.swap_revision_projection SET(security_invoker=false);',
        'ALTER VIEW trading.swap_revision_projection SET(security_barrier=true);',
        'ALTER VIEW trading.swap_revision_projection OWNER TO CURRENT_USER;',
        'GRANT SELECT ON trading.swap_revision_projection TO PUBLIC;',
        'GRANT SELECT ON trading.swap_revision_projection TO openetrm_runtime WITH GRANT OPTION;',
        'REVOKE SELECT ON trading.swap_revision_projection FROM openetrm_risk_worker;',
        'GRANT SELECT(quantity) ON trading.swap_revision_projection TO PUBLIC;',
        'CREATE RULE synthetic_extra_rule AS ON UPDATE TO trading.swap_revision_projection DO INSTEAD NOTHING;',
        "UPDATE pg_rewrite SET ev_enabled='D' WHERE ev_class='trading.swap_revision_projection'::regclass;",
        "DO $$DECLARE d text=pg_get_viewdef('trading.swap_revision_projection'::regclass,true);BEGIN EXECUTE 'CREATE OR REPLACE VIEW trading.swap_revision_projection WITH(security_invoker=true) AS '||replace(d,'NULL::jsonb','''{}''::jsonb');END$$;",
    ]
    for change in function+view:
        try:query('BEGIN;'+change+body+'ROLLBACK;')
        except RuntimeError as error:
            assert 'retained_swap_source_upgrade:' in str(error),str(error)
        else:raise AssertionError('A changed retained source helper or projection was admitted: '+change)
    policy=query("SELECT quote_ident(polname) FROM pg_policy WHERE polrelid='trading.swap_side_periods'::regclass "
        "AND pg_get_expr(polqual,polrelid)='risk.worker_read()' ORDER BY polname LIMIT 1;")
    assert policy
    for change in (f'ALTER POLICY {policy} ON trading.swap_side_periods TO PUBLIC;',
                   f'ALTER POLICY {policy} ON trading.swap_side_periods USING(false);',
                   'ALTER TABLE trading.swap_side_periods DISABLE ROW LEVEL SECURITY;'):
        try:query('BEGIN;'+change+body+'ROLLBACK;')
        except RuntimeError as error:assert 'retained_swap_source_upgrade:'in str(error),str(error)
        else:raise AssertionError('Changed predecessor read policy was accepted: '+change)
    query('CREATE TABLE public.synthetic_unrelated_worker_policy(id integer);'
        'ALTER TABLE public.synthetic_unrelated_worker_policy ENABLE ROW LEVEL SECURITY;'
        'CREATE POLICY synthetic_worker ON public.synthetic_unrelated_worker_policy FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());')
    query('BEGIN;'+body+"DO $$BEGIN IF(SELECT pg_get_expr(polqual,polrelid) FROM pg_policy "
        "WHERE polrelid='public.synthetic_unrelated_worker_policy'::regclass)<>'risk.worker_read()' THEN RAISE EXCEPTION 'synthetic_outside_policy_changed';END IF;END$$;ROLLBACK;")
    query(f"CREATE TABLE public.synthetic_retained_swap_reader_identity AS SELECT '{SIGNATURE}'::regprocedure::oid AS helper,'trading.swap_revision_projection'::regclass::oid AS projection;")
    query('BEGIN;SET LOCAL search_path=security,assistant,public;'+body+'ROLLBACK;')
    predecessor_readers(query)
    print('PASS retained-swap source upgrade: full helper and invoker-view metadata, body, privileges and caller search path are qualified',flush=True)


def checks(query,manifest):
    assert query(f"SELECT helper='{SIGNATURE}'::regprocedure::oid AND projection='trading.swap_revision_projection'::regclass::oid FROM public.synthetic_retained_swap_reader_identity;")=='t'
    assert query("SELECT reloptions=ARRAY['security_invoker=true'] AND NOT has_table_privilege('public',oid,'SELECT') AND has_table_privilege('openetrm_runtime',oid,'SELECT') AND has_table_privilege('openetrm_risk_worker',oid,'SELECT') FROM pg_class WHERE oid='trading.scheduled_swap_term_projection'::regclass;")=='t'
    print('PASS retained-swap source upgrade preserves existing helper/view OIDs and admits only the established invoker readers',flush=True)
    from check_lifecycle_risk_jobs import checks as risk_checks
    risk_checks(query,manifest,read_policy_checks=worker_policy_checks)
    child_visibility_checks(query,manifest,prepared=True)


def child_visibility_checks(query,manifest,prepared=False):
    from preview_fixture import catalogue_fixture,trading_fixture,trade_booking
    from check_index_fixings import RULE
    catalog,tenant,workspace=(manifest[k]for k in('catalogId','tenantId','workspaceId'))
    if not prepared:
        query(trading_fixture(catalog))
        query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
        query(catalogue_fixture(manifest,'https://preview.invalid'))
    quantity=query(f"SELECT quantity_code FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)="
        f"('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','test_gas_outright');")
    specification=dict(revision=dict(catalogId=manifest['referenceCatalogId'],revisionId=manifest['referenceRevisionId']),code=quantity)
    periods=[('2021-01-01','2021-02-01','100','3'),('2021-02-01','2021-03-01','200','4')]
    def side(fixed):
        return dict(basis='period_total',quantity=specification,amount=None,allocation=None,priceApplication='period_average_times_quantity_v1',power=None,
            periods=[dict(amount=amount,fixed=dict(price=price,currency='usd',deliveryFrom=start,deliveryTo=end,paymentSchedule=RULE)if fixed else None,
                floating=None if fixed else dict(settlement=dict(mode='curve_target',index=None,projectionPolicy=None),curve='base_forward',index='test_price',currency='usd',
                    deliveryFrom=start,deliveryTo=end,paymentSchedule=RULE,resetFrequency='monthly',averagingMethod='none',spread='0'))for start,end,amount,price in periods])
    snapshot=str(uuid4())
    booking=dict(operationId=str(uuid4()),snapshotId=snapshot,knowledgeMode='current',convention='independent_side_financial_swap_v2',
        bookId='d1000000-0000-0000-0000-000000000001',definitionRevisionId=manifest['revisionId'],commodity='test_gas',swapType='scheduled_financial_swap',
        quantity=None,fixed=None,floating=None,firstFloating=None,fixedCoefficient=None,firstCoefficient='-1',discounting=False,
        scheduled=dict(implementation='whole_term_scheduled_financial_swap_v1',first=side(True),second=side(False)),**trade_booking())
    host=query('SHOW unix_socket_directories;');packet=Path(host)/'synthetic-retained-swap.json'
    packet.write_text(json.dumps(dict(host=host,manifest=manifest,snapshotId=snapshot,booking=booking)));packet.chmod(0o600)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--lifecycle-swap-native',str(packet)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=90)
    assert result.returncode==0,result.stdout+result.stderr
    booked=json.loads(next(line.split(' ',1)[1]for line in result.stdout.splitlines()if line.startswith('LIFECYCLE_SWAP_BOOKED ')))
    actual=projection_parity(query,manifest,'2020-12-18T16:00:00Z',require_observations=False)
    assert booked['tradeId']in actual


def projection_parity(query,manifest,as_of,require_observations=True):
    catalog,tenant,workspace=(manifest[k]for k in('catalogId','tenantId','workspaceId'))
    def runtime(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    context=runtime(f"SELECT security.capture_date_context('{catalog}','{as_of[:10]}',NULL);")
    bind=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{as_of}');"
    def read(view):
        try:return ('value',runtime(bind+f"SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY r.trade_id,r.revision),'[]'::jsonb) FROM {view} r;"))
        except RuntimeError as error:
            # Preserve refusal rather than comparing incidental function-stack text.
            return ('refused',str(error).splitlines()[0])
    old='public.synthetic_before_swap_projection';new='trading.swap_revision_projection'
    assert read(old)==read(new) and read(new)[0]=='value'
    for table in ('trading.swap_quantity_authoring','trading.swap_payment_rules','trading.swap_observation_schedules','ref_data.quote_specifications'):
        if table=='trading.swap_observation_schedules'and query(f'SELECT count(*)FROM {table};')=='0':
            assert not require_observations,'An observation omission check requires actual retained observations.'
            continue
        # All data and guards remain intact. Only this runtime's SELECT visibility
        # changes, reproducing a separately authorized hidden required child.
        query(f'CREATE POLICY synthetic_hidden_child ON {table} AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);')
        try:
            before,after=read(old),read(new)
            assert before==after if before[0]=='value'else after[0]=='refused',(table,before,after)
        finally:query(f'DROP POLICY synthetic_hidden_child ON {table};')
    assert read(old)==read(new)
    print('PASS retained-swap invoker parity: exact scheduled values and required-child visibility/refusal match the qualified predecessor',flush=True)
    return read(new)[1]


def worker_policy_checks(query,manifest,request,frame,submit):
    from check_risk_jobs_db import provision_worker
    catalog,tenant,workspace=(manifest[k]for k in('catalogId','tenantId','workspaceId'))
    login='synthetic_statement_worker';provision_worker(query,manifest,login)
    def work(sql):return query(f'SET SESSION AUTHORIZATION {login};'+sql)
    def start(label):
        selected=deepcopy(request);selected['engineVersion']='synthetic_read_policy_'+label
        job=submit(selected)
        claimed=json.loads(work(f"SELECT risk.claim(30,'{selected['engineVersion']}');"))
        assert claimed['jobId']==job['jobId'],claimed
        return job['jobId'],claimed['leaseToken']
    read=f"SELECT count(*)FROM trading.books WHERE catalog_id='{catalog}';"
    for label,change in(
        ('cancel',lambda job:f"UPDATE risk.jobs SET state='cancellation_requested' WHERE id='{job}';"),
        ('lease',lambda job:f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second'WHERE id='{job}';"),
        ('epoch',lambda job:'UPDATE security.epoch SET generation=generation+1;'),
        ('revoked',lambda job:f"UPDATE security.role_grants SET revoked=true WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='{login}');")):
        job,lease=start(label)
        # This is one protocol message with several independent SQL commands.
        # A statement_timestamp cache could incorrectly reuse the first result.
        lines=work(f"BEGIN;SELECT risk.bind('{job}','{lease}');"+read+'RESET SESSION AUTHORIZATION;'+change(job)+
            f'SET SESSION AUTHORIZATION {login};'+read+
            f"DO $$DECLARE state text;BEGIN BEGIN state=risk.finish('{job}','{lease}','{{}}',NULL);"
            "IF state='cancellation_requested'THEN RETURN;END IF;EXCEPTION WHEN insufficient_privilege THEN "
            "IF SQLERRM IN('risk_job_denied','risk_job_fence','risk_job_authority_changed','risk_job_cancelled')THEN RETURN;END IF;RAISE;END;"
            "RAISE EXCEPTION 'synthetic_invalid_result_published';END$$;ROLLBACK;").splitlines()
        assert int(lines[-2])>0 and lines[-1]=='0',(label,lines)
    print('PASS worker SELECT initplans recheck cancellation, lease, epoch and revocation on the next SQL command, including one protocol message; no result is published',flush=True)

    query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds'WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='{login}');"
        f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,role_id,tenant_id,workspace_id,granted_to+interval '0.2 seconds','9999-12-31',false "
        f"FROM security.role_grants WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='{login}');")
    job,lease=start('original_deadline')
    query('CREATE FUNCTION public.synthetic_read_pause(text)RETURNS boolean LANGUAGE plpgsql VOLATILE AS $$BEGIN PERFORM pg_sleep(0.35);RETURN true;END$$;')
    lines=work(f"BEGIN;SELECT risk.bind('{job}','{lease}');SELECT count(*)FROM market_data.curves WHERE catalog_id='{catalog}';"
        f"SELECT count(*)FROM market_data.curves WHERE catalog_id='{catalog}'AND public.synthetic_read_pause(code::text);"
        'SELECT pg_sleep(0.6);'+read+
        'RESET SESSION AUTHORIZATION;'+f"SELECT security.principal_has_permission(principal_id,'{tenant}','{workspace}','ReadMarketData',clock_timestamp())FROM risk.workers WHERE login='{login}';ROLLBACK;").splitlines()
    assert 0<int(lines[2])<int(lines[1]) and lines[-2:]==['0','t'],lines
    query('DROP FUNCTION public.synthetic_read_pause(text);')
    fresh,token=start('fresh_successor')
    assert int(work(f"BEGIN;SELECT risk.bind('{fresh}','{token}');"+read+'ROLLBACK;').splitlines()[-1])>0
    print('PASS original minimum authority expires during a SELECT and remains expired under a prepublished successor; a separate fresh request works',flush=True)


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks)
