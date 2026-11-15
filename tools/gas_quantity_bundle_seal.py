"""Independent persisted-receipt and original-authority probes for mixed gas."""
import json
from decimal import Decimal
from uuid import uuid4


def source_check(query,call,capture,catalog,account,ordinary,correction,at,ordinary_policy,physical_policy,continuation,literal):
    selected=json.loads(query("SELECT jsonb_build_object('positionId',p.position_id,'performanceId',p.id,'expectedRevision',q.revision,"
        "'expectedPrincipalVersion',(SELECT max(r.revision) FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(p.catalog_id,p.position_id)),"
        "'quantity',trim_scale(q.quantity)::text,'retracted',q.retracted,'titleEvidenceId',q.title_evidence_id,'supply','[]'::jsonb,"
        "'sourceAllocation','preserve_unallocated_source_quantity_v1','evidence','Synthetic unchanged physical counterpart') "
        "FROM trading.gas_futures_delivery_performances p JOIN trading.gas_owned_positions owner ON(owner.catalog_id,owner.id)=(p.catalog_id,p.position_id) "
        "JOIN LATERAL(SELECT x.* FROM trading.gas_futures_performance_quantities x WHERE(x.catalog_id,x.performance_id)=(p.catalog_id,p.id) ORDER BY x.revision DESC LIMIT 1)q ON true "
        f"WHERE(p.catalog_id,owner.account_id)=('{catalog}','{account}') AND owner.quantity>0 AND q.quantity>0 ORDER BY p.id LIMIT 1;"))
    position=selected['positionId']
    cash=json.loads(query(f"SELECT to_jsonb(c) FROM trading.gas_futures_delivery_cash c WHERE(c.catalog_id,c.position_id)=('{catalog}','{position}') "
        f"AND c.effective_at<='{at}' AND abs(c.amount)>=0.01 AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_delivery_payments p WHERE(p.catalog_id,p.cash_id)=(c.catalog_id,c.id)) ORDER BY c.effective_at,c.id LIMIT 1;"))
    policy=json.loads(query("SELECT to_jsonb(b) FROM trading.gas_owned_action_bindings b "
        f"WHERE(b.catalog_id,b.account_id,b.action)=('{catalog}','{account}','gas.cash.record_payment') ORDER BY b.revision DESC LIMIT 1;"))
    stale=capture()
    payment=dict(positionId=position,cashId=cash['id'],businessReference='synthetic_unseen_counterpart_payment',expectedVersion=0,
        paidAt=at,amount='0.01' if Decimal(str(cash['amount']))>0 else '-0.01',evidence='Synthetic separately recorded bank payment')
    call(f"SELECT security.record_gas_futures_delivery('{catalog}','{uuid4()}','{position}','payment',{literal(payment)},'{at}','{capture()}',"
         f"'{policy['policy_id']}',{policy['policy_revision']},false,NULL);")
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    body=dict(ordinary=ordinary,physical=dict(corrections=[correction,selected]),physicalAccountId=account)
    statement=f"SELECT security.reconcile_gas_quantity_bundle('{catalog}','{uuid4()}','{account}',{literal(body)},'{at}','{stale}',"
    statement+=f"'{ordinary_policy}',1,'{physical_policy}',1,false,{literal(continuation)});SET CONSTRAINTS ALL IMMEDIATE;"
    try:call(statement,rollback=True)
    except RuntimeError as error:assert 'futures_delivery_source' in str(error),str(error)
    else:raise AssertionError('An unchanged counterpart with a bank fact outside captured source K bypassed its original native source fence')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    print('PASS mixed unchanged counterpart: guessed current performance/principal versions cannot bypass the exact source knowledge prefix',flush=True)


def checks(query,call,command,catalog):
    faults={
        'missing_ordinary':"NEW.request=NEW.request#-ARRAY['body','ordinary'];",
        'wrong_policy':"NEW.request=jsonb_set(NEW.request,ARRAY['physicalPolicyRevision'],'2'::jsonb);",
        'wrong_context':"NEW.request=jsonb_set(NEW.request,ARRAY['contextId'],to_jsonb(gen_random_uuid()));",
        'false_ack':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['semanticAcknowledgement'],'true'::jsonb);",
        'wrong_publication':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['publicationOrder'],'1'::jsonb);",
        'wrong_title':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['ordinary','positions','0','currentQuantity'],'\"99\"'::jsonb);",
        'missing_physical':"NEW.outcome=NEW.outcome-'physical';",
        'wrong_cash_basis':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['physical','corrections','0','currentPrincipalVersion'],'1'::jsonb);"}
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    try:
        for name,change in faults.items():
            query("CREATE OR REPLACE FUNCTION trading.fixture_gas_bundle_fault() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN "
                  "IF NEW.request->>'action'='quantity_reconciliation_bundle' THEN "+change+"END IF;RETURN NEW;END$$;")
            query("CREATE TRIGGER fixture_gas_bundle_fault BEFORE INSERT ON trading.gas_owned_commands FOR EACH ROW EXECUTE FUNCTION trading.fixture_gas_bundle_fault();")
            try:
                for immediate in (True,False):
                    try:call(command+("SET CONSTRAINTS ALL IMMEDIATE;" if immediate else ''),rollback=immediate)
                    except RuntimeError as error:
                        expected='gas_owned_input' if name=='missing_ordinary' else 'gas_quantity_bundle_seal'
                        assert expected in str(error),str(error)
                    else:raise AssertionError('Mixed correction accepted malformed persisted receipt '+name+(' at immediate constraints' if immediate else ' at COMMIT'))
            finally:query('DROP TRIGGER fixture_gas_bundle_fault ON trading.gas_owned_commands;')
    finally:query('DROP FUNCTION IF EXISTS trading.fixture_gas_bundle_fault();')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    print('PASS mixed correction seal: both canonical children, original context/policies, publication, title, cash basis and complete outcome refuse stored faults at immediate constraints and COMMIT',flush=True)


def acknowledgement_checks(query,call,command,catalog):
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    query("""CREATE FUNCTION trading.fixture_gas_bundle_ack_fault() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN
      IF NEW.request->>'action'='owned_quantity_reconciliation' THEN
       NEW.request=(NEW.request#-ARRAY['body','positions'])#-ARRAY['body','consumptions'];
       NEW.outcome=jsonb_set(jsonb_set(NEW.outcome,ARRAY['positions'],'[]'::jsonb),ARRAY['consumptions'],'[]'::jsonb);
      ELSIF NEW.request->>'action'='futures_quantity_reconciliation_batch' THEN
       NEW.request=NEW.request#-ARRAY['body','corrections'];
       NEW.outcome=jsonb_set(NEW.outcome,ARRAY['corrections'],'[]'::jsonb);
      ELSIF NEW.request->>'action'='quantity_reconciliation_bundle' THEN
       NEW.request=((NEW.request#-ARRAY['body','ordinary','positions'])#-ARRAY['body','ordinary','consumptions'])#-ARRAY['body','physical','corrections'];
       NEW.outcome=jsonb_set(jsonb_set(jsonb_set(NEW.outcome,ARRAY['ordinary','positions'],'[]'::jsonb),ARRAY['ordinary','consumptions'],'[]'::jsonb),ARRAY['physical','corrections'],'[]'::jsonb);
      END IF;RETURN NEW;END$$;
      CREATE TRIGGER fixture_gas_bundle_ack_fault BEFORE INSERT ON trading.gas_owned_commands FOR EACH ROW EXECUTE FUNCTION trading.fixture_gas_bundle_ack_fault();""")
    try:
        for immediate in (True,False):
            try:call(command+("SET CONSTRAINTS ALL IMMEDIATE;" if immediate else ''),rollback=immediate)
            except RuntimeError as error:assert 'gas_quantity_bundle_seal' in str(error),str(error)
            else:raise AssertionError('Correlated malformed all-acknowledgement bodies and outcomes bypassed the independent mixed native seal')
    finally:
        query('DROP TRIGGER fixture_gas_bundle_ack_fault ON trading.gas_owned_commands;DROP FUNCTION trading.fixture_gas_bundle_ack_fault();')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    print('PASS mixed historical acknowledgement: correlated malformed child and outer bodies/outcomes cannot bypass typed membership at immediate constraints or COMMIT',flush=True)


def upgrade_checks(query,migration):
    from check_gas_quantity_reconciliation import upgrade_checks as prior
    prior(query,migration)
    if migration.name!='0302_gas_quantity_bundles.sql':return
    from assistant_context import migration_body
    body=migration_body(migration.read_text())
    signatures=(
        'security.reconcile_gas_owned_quantities(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)',
        'security.reconcile_gas_futures_delivery_batch(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)',
        'trading.require_gas_owned_quantity_amendment(uuid,uuid,jsonb,uuid,timestamptz,uuid,integer)',
        'trading.require_gas_quantity_batch_funding(uuid,uuid[],jsonb,uuid,timestamptz)',
        'trading.check_gas_owned_quantity_correction()',
        'trading.gas_quantity_batch_reconciles(uuid,integer,uuid,timestamptz,jsonb)',
        'trading.gas_owned_quantity_terms(jsonb)',
        'trading.gas_futures_quantity_batch_fact(jsonb)')
    for signature in signatures:
        changes=[f"ALTER FUNCTION {signature} SECURITY {'DEFINER' if signature in ('trading.gas_owned_quantity_terms(jsonb)','trading.gas_futures_quantity_batch_fact(jsonb)') else 'INVOKER'};",
                 f"ALTER FUNCTION {signature} SET work_mem='64kB';",
                 f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_risk_worker;",
                 f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic source drift */ ');END$$;"]
        for change in changes:
            try:query('BEGIN;'+change+body+'ROLLBACK;')
            except RuntimeError as error:assert 'gas_quantity_bundle_upgrade' in str(error),str(error)
            else:raise AssertionError('Mixed correction upgraded a changed native product producer or seal')
    query('BEGIN;SET LOCAL search_path=security,public;'+body+'ROLLBACK;')
    print('PASS mixed correction upgrade: exact product writers, source closure and native seal metadata/ACLs under any caller search path',flush=True)
