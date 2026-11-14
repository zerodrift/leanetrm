"""A receipt and every identified dispatch are amended in one native boundary."""
import json
from datetime import datetime, timezone
from decimal import Decimal
from uuid import uuid4

from check_reference_editing import main
from check_gas_quantity_reconciliation import checks as quantity_checks, upgrade_checks, fixture
from gas_quantity_fixture import principal_plan


def checks(query,manifest):
    quantity_checks(query,manifest)
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    at='2026-11-06T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2026-11-06',NULL);")
    receipt=json.loads(query("SELECT to_jsonb(p)||jsonb_build_object('account_id',stock.account_id,'source_key',e.source_key,'source_revision',e.revision) "
        "FROM trading.gas_futures_performances_current p JOIN trading.gas_owned_positions_current stock ON(stock.catalog_id,stock.id)=(p.catalog_id,p.receipt_lot_id) "
        "JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.title_evidence_id) "
        f"WHERE p.catalog_id='{catalog}' AND p.quantity=0.25 AND EXISTS(SELECT 1 FROM trading.gas_owned_consumptions_current c "
        "WHERE(c.catalog_id,c.lot_id)=(p.catalog_id,p.receipt_lot_id) AND c.quantity>0) ORDER BY p.id LIMIT 1;"))
    assert receipt,'The batch baseline requires an accepted receipt already used by a real dispatch.'
    long=receipt['position_id'];account=receipt['account_id']
    dispatch=json.loads(query("SELECT to_jsonb(p) FROM trading.gas_futures_performances_current p JOIN trading.gas_futures_supplies_current s "
        "ON(s.catalog_id,s.performance_id)=(p.catalog_id,p.id) JOIN trading.gas_owned_consumptions_current c ON(c.catalog_id,c.id)=(s.catalog_id,s.consumption_id) "
        f"WHERE(c.catalog_id,c.lot_id)=('{catalog}','{receipt['receipt_lot_id']}') AND c.quantity>0 ORDER BY p.id LIMIT 1;"))
    short=dispatch['position_id']
    assert dispatch['performed_at']>='2026-11-06T14:00:00'
    terms=json.loads(query("SELECT to_jsonb(t) FROM settlement_data.futures_delivery_obligations l JOIN trading.gas_futures_delivery_terms t "
        "ON(t.catalog_id,t.profile_id,t.profile_revision)=(l.catalog_id,l.profile_id,l.profile_revision) "
        f"WHERE(l.catalog_id,l.position_id)=('{catalog}','{long}');"))
    policy=query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)=('{catalog}','{account}','gas.futures.reconcile_quantity') ORDER BY revision DESC LIMIT 1;")
    def evidence(key,quantity,when,revision,description):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{account}',"
            f"{literal(dict(sourceKey=key,kind='title_receipt',quantity=quantity,occurredAt=when,expectedRevision=revision,evidence=description))},'{at}','{capture()}',NULL,NULL,false)::text;"))['id']
    # Replacement stock is actually and explicitly received before dispatch.
    # The correction never invents procurement to cover a withdrawn receipt.
    replacement_id=str(uuid4());replacement_at='2026-11-06T13:45:00Z'
    source=evidence(str(uuid4()),'0.25',replacement_at,0,'Synthetic independently observed replacement title')
    proposed=principal_plan(query,catalog,long,added=dict(id=replacement_id,business_reference='synthetic_batch_replacement_receipt',performed_at=replacement_at,quantity='0.25'))
    performance=dict(positionId=long,businessReference='synthetic_batch_replacement_receipt',expectedVersion=proposed['expectedVersion'],quantity='0.25',performedAt=replacement_at,
        titleEvidenceId=source,availableAt=replacement_at,supply=[])
    replacement=json.loads(call(f"SELECT security.record_gas_futures_delivery('{catalog}','{replacement_id}','{long}','performance',{literal(performance)},'{at}','{capture()}',"
        f"'{terms['action_policy_id']}',{terms['action_policy_revision']},false,{literal(proposed)})::text;"))
    occurred=datetime.fromisoformat(receipt['performed_at']).astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%fZ')
    withdrawn=evidence(receipt['source_key'],'0',occurred,receipt['source_revision'],'Synthetic explicit withdrawal of an already used receipt')
    revision=int(query(f"SELECT max(revision) FROM trading.gas_futures_performance_quantities WHERE(catalog_id,performance_id)=('{catalog}','{receipt['id']}');"))
    short_revision=int(query(f"SELECT max(revision) FROM trading.gas_futures_performance_quantities WHERE(catalog_id,performance_id)=('{catalog}','{dispatch['id']}');"))
    long_plan=principal_plan(query,catalog,long,updates={receipt['id']:'0'})
    short_plan=principal_plan(query,catalog,short)
    uses=json.loads(query("SELECT jsonb_agg(to_jsonb(c) ORDER BY s.ordinal) FROM trading.gas_futures_performance_quantity_supplies s "
        "JOIN trading.gas_owned_consumption_quantities c ON(c.catalog_id,c.consumption_id,c.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision) "
        f"WHERE(s.catalog_id,s.performance_id,s.performance_revision)=('{catalog}','{dispatch['id']}',{short_revision});"))
    moved=sum(Decimal(str(c['quantity'])) for c in uses if c['lot_id']==receipt['receipt_lot_id'])
    assert moved==Decimal('.25')
    corrections=[dict(positionId=long,performanceId=receipt['id'],expectedRevision=revision,expectedPrincipalVersion=long_plan['expectedVersion'],quantity='0',retracted=True,
        titleEvidenceId=withdrawn,supply=[],sourceAllocation='withdraw_all_source_allocations_v1',evidence='Synthetic complete withdrawal of accepted title'),
        dict(positionId=short,performanceId=dispatch['id'],expectedRevision=short_revision,expectedPrincipalVersion=short_plan['expectedVersion'],quantity=str(dispatch['quantity']),retracted=False,titleEvidenceId=None,
            supply=[dict(consumptionId=c['consumption_id'],expectedRevision=c['revision'],lotId=replacement['stockId'] if c['lot_id']==receipt['receipt_lot_id'] and Decimal(str(c['quantity']))>0 else c['lot_id'],
                evidenceId=c['evidence_id'],reservationId=c['reservation_id'],quantity=str(c['quantity']),retracted=c['retracted']) for c in uses],
            sourceAllocation='preserve_unallocated_source_quantity_v1',evidence='Synthetic explicit complete dispatch funding replacement')]
    body=dict(corrections=corrections)
    continuation=dict(implementation='gas_futures_quantity_batch_continuation_v1',positions=[long_plan,short_plan])
    operation=str(uuid4());context=capture()
    before_payments=query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_futures_delivery_payments p WHERE catalog_id='{catalog}';")
    before_cash=Decimal(query(f"SELECT sum(amount) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{long}');"))
    missing=[]
    faults={
        'missing_request_array':"NEW.request=jsonb_set(NEW.request,ARRAY['body'],(NEW.request->'body')-'corrections');",
        'missing_both_arrays':"NEW.request=jsonb_set(NEW.request,ARRAY['body'],(NEW.request->'body')-'corrections');NEW.outcome=NEW.outcome-'corrections';",
        'principal_version':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['corrections'],(SELECT jsonb_agg(value||jsonb_build_object('currentPrincipalVersion',99999999)) FROM jsonb_array_elements(NEW.outcome->'corrections')));",
        'reported_quantity':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['corrections'],(SELECT jsonb_agg(value||jsonb_build_object('currentQuantity','999')) FROM jsonb_array_elements(NEW.outcome->'corrections')));"}
    for label,fault in faults.items():
        query("CREATE FUNCTION trading.synthetic_batch_command_fault() RETURNS trigger LANGUAGE plpgsql AS $fault$BEGIN "
            f"IF NEW.request_id='{operation}'::uuid THEN {fault}END IF;RETURN NEW;END$fault$;"
            "CREATE TRIGGER synthetic_batch_command_fault BEFORE INSERT ON trading.gas_owned_commands FOR EACH ROW EXECUTE FUNCTION trading.synthetic_batch_command_fault();")
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        try:
            try:query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
                f"SELECT security.reconcile_gas_futures_delivery_batch('{catalog}','{operation}','{account}',{literal(body)},'{at}','{context}','{policy}',1,false,{literal(continuation)});"
                "SET CONSTRAINTS ALL IMMEDIATE;ROLLBACK;")
            except RuntimeError as error:assert 'gas_quantity_batch_seal' in str(error),str(error)
            else:missing.append(label)
        finally:query('DROP TRIGGER synthetic_batch_command_fault ON trading.gas_owned_commands;DROP FUNCTION trading.synthetic_batch_command_fault();')
    assert not missing,'The native batch seal accepted malformed command facts: '+', '.join(missing)
    sql=f"SELECT security.reconcile_gas_futures_delivery_batch('{catalog}','{operation}','{account}',{literal(body)},'{at}','{context}','{policy}',1,false,{literal(continuation)})::text;"
    accepted=json.loads(call(sql))
    assert accepted['changed'] and len(accepted['corrections'])==2
    assert json.loads(call(sql))==accepted
    assert query(f"SELECT trim_scale(quantity) FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{receipt['receipt_lot_id']}');")=='0'
    assert query(f"SELECT coalesce(sum(quantity),0) FROM trading.gas_owned_consumptions_current WHERE(catalog_id,lot_id)=('{catalog}','{receipt['receipt_lot_id']}');")=='0'
    assert Decimal(query(f"SELECT sum(quantity) FROM trading.gas_owned_consumptions_current WHERE(catalog_id,lot_id)=('{catalog}','{replacement['stockId']}');"))==Decimal('.25')
    assert Decimal(query(f"SELECT sum(amount) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{long}');"))-before_cash==Decimal('1.50')
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{short}');")=='4.52'
    assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_futures_delivery_payments p WHERE catalog_id='{catalog}';")==before_payments
    print('PASS linked physical correction batch: exact title withdrawal and identified replacement stock, complete principal conservation, unchanged bank payments and atomic first outcome',flush=True)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
