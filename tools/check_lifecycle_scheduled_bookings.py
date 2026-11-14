"""Service option bookings keep exact causal task scope and actual actor facts."""
from datetime import datetime,timedelta,timezone
import json
from uuid import uuid4
from check_reference_editing import main
from check_lifecycle_option_scheduling import fixture,checks as option_checks

SIGNATURES=('security.stamp_trade_booking(uuid)','security.record_trade_booking()',
 'lifecycle.execute_option_action(uuid,jsonb)','lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb)')
OIDS={}

def upgrade_checks(query,migration):
    if migration.name!='0288_scheduled_option_booking.sql':return
    from assistant_context import check_guard
    for signature in SIGNATURES:
        OIDS[signature]=query(f"SELECT '{signature}'::regprocedure::oid;")
        changes=[(sql,signature) for sql in (
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",f"ALTER FUNCTION {signature} STRICT;",
            f"ALTER FUNCTION {signature} LEAKPROOF;",f"ALTER FUNCTION {signature} PARALLEL SAFE;",f"ALTER FUNCTION {signature} STABLE;",
            f"ALTER FUNCTION {signature} SECURITY INVOKER;",f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime WITH GRANT OPTION;",f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"DO $$DECLARE definition text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(definition,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;",
            f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proargmodes=ARRAY['i']::\"char\"[] WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proallargtypes=proargtypes::oid[] WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proargnames=ARRAY['synthetic_changed_name'] WHERE oid='{signature}'::regprocedure;")]
        check_guard(query,migration.read_text(),migration.name[:4],'scheduled_option_booking_upgrade',changes)
    check_guard(query,migration.read_text(),migration.name[:4],'scheduled_option_booking_upgrade',[
        ("ALTER TABLE trading.trades DISABLE TRIGGER record_booking;",'booking trigger')])


def checks(query,manifest):
    option_checks(query,manifest,terminal_boundary=True)
    for signature,oid in OIDS.items():assert query(f"SELECT '{signature}'::regprocedure::oid;")==oid
    assert query('SELECT count(*) FROM security.scheduled_option_booking_permits;')=='0'
    assert query("SELECT count(*) FROM trading.scheduled_option_bookings p JOIN trading.booking_facts b ON(b.catalog_id,b.trade_id)=(p.catalog_id,p.trade_id) JOIN security.principals s ON s.id=b.booker_id JOIN automation.jobs j ON j.id=p.job_id WHERE s.kind='service' AND p.service_id=b.booker_id AND p.owner_id=j.owner_id AND b.implementation='scheduled_option_future_booking_v1';")=='1'
    for signature in ('lifecycle.require_option_booking_permit(uuid)','trading.seal_scheduled_option_booking()'):
        assert query(f"SELECT has_function_privilege('pilot_fixture_runtime','{signature}','EXECUTE');")=='f'
    assert query("SELECT has_table_privilege('pilot_fixture_runtime','security.scheduled_option_booking_permits','INSERT');")=='f'
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
        f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    definition=json.loads(query("SELECT automation.lifecycle_definition(p)::text FROM automation.lifecycle_profiles p JOIN automation.jobs j ON j.lifecycle_profile_id=p.id JOIN automation.runs r ON r.job_id=j.id JOIN automation.lifecycle_results result ON result.run_id=r.id WHERE p.handler='listed_option' AND r.state='succeeded';"))
    now=datetime.now(timezone.utc)
    body=dict(schedule=dict(requestId=str(uuid4()),name='Synthetic service booking boundary',operation='lifecycle_action_v1',dueAt=(now-timedelta(seconds=1)).isoformat(),expiresAt=(now+timedelta(hours=1)).isoformat(),intervalSeconds=None,timeZone='UTC',misfire='run_once',overlap='forbid',timeoutSeconds=30,maximumAttempts=1,profileId=None,profileVersion=None),definition=definition)
    created=json.loads(query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT automation.create_lifecycle_job('{catalog}',{literal(body)})::text;COMMIT;"))['outcome']
    worker=str(uuid4());claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_lifecycle_next('{worker}',120)::text;"));assert claim['jobId']==created['id']
    source=json.loads(query("SELECT jsonb_build_object('book',r.book_id,'definition',r.definition_revision_id,'contract',m.contract_id,'curve',m.settlement_curve_code,'at',c.at,'parties',jsonb_build_object('legalEntityId',r.legal_entity_id,'legalEntityRevision',r.legal_entity_revision,'counterpartyId',r.counterparty_id,'counterpartyRevision',r.counterparty_revision))::text FROM automation.lifecycle_profiles p JOIN market_data.market_closes c ON(c.catalog_id,c.id)=(p.catalog_id,p.close_id) JOIN trading.futures_option_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(p.catalog_id,p.trade_id,p.option_revision) JOIN settlement_data.futures_margin_policies m ON(m.catalog_id,m.id,m.revision)=(p.catalog_id,p.margin_policy_id,p.margin_policy_revision) WHERE p.handler='listed_option' LIMIT 1;"))
    before=query(f"SELECT count(*)||'|'||(SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}') FROM trading.trades WHERE catalog_id='{catalog}';")
    for operation in (str(uuid4()),query(f"SELECT security.futures_option_event_operation('{claim['id']}');")):
        service=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_lifecycle_context('{claim['id']}',{claim['fence']},'{worker}');")
        result=query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{service}');SELECT automation.capture_lifecycle_source() IS NOT NULL;SELECT security.has_permission('BookTrade');DO $probe$BEGIN BEGIN "
            f"PERFORM security.book_futures_with_parties('{catalog}','{operation}','{source['book']}','{source['definition']}','{source['curve']}','{source['contract']}','{source['at']}',NULL,1,3,'contract_lot_execution_pnl_v2',NULL,NULL,NULL,{literal(source['parties'])});RAISE EXCEPTION 'Unexpected ordinary service booking';"
            "EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'booking_human_required' THEN RAISE;END IF;END;END$probe$;COMMIT;")
        assert result.splitlines()==['t','t'],result
    assert query(f"SELECT count(*)||'|'||(SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}') FROM trading.trades WHERE catalog_id='{catalog}';")==before
    assert query('SELECT count(*) FROM security.scheduled_option_booking_permits;')=='0'
    print('PASS scheduled option booking boundary: actual service/task/owner causal facts, no leaked permit, ordinary and expected-operation service bookings denied without financial publication',flush=True)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
