"""Deferred route counterparts reject incomplete authorized-definer writes."""
import json
from uuid import uuid4
from gas_transport_capacity_fixture import literal,identity


def seals(f,refuses,facts):
    q=f.query;route=facts['route']['id'];head=next(p for p in facts['plans'] if p['kind']=='plan');context=f.capture()
    def attack(body,code):
        from native_read_transport import register_captured_input
        inputs=''.join(register_captured_input(q,identifier) for identifier in q.read_sources)
        q("CREATE OR REPLACE FUNCTION security.synthetic_route_seal_attack() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $attack$DECLARE "
            "p trading.gas_transport_route_plans;r jsonb;op uuid;publication integer;payload jsonb;outcome jsonb;fact_value jsonb;BEGIN "+body+" END$attack$;"
            "ALTER FUNCTION security.synthetic_route_seal_attack() OWNER TO openetrm_security_owner;GRANT EXECUTE ON FUNCTION security.synthetic_route_seal_attack() TO openetrm_runtime;")
        try:refuses(lambda:f.call(inputs+'SELECT security.synthetic_route_seal_attack();'),code)
        finally:q('DROP FUNCTION security.synthetic_route_seal_attack();')
    before=q(f"SELECT count(*) FROM trading.gas_transport_route_plans WHERE catalog_id='{f.catalog}';")
    new=str(uuid4())
    attack(f"SELECT * INTO STRICT p FROM trading.gas_transport_route_plans WHERE(catalog_id,id)=('{f.catalog}','{head['id']}');"
        f"SELECT max(publication_order)+1 INTO publication FROM trading.publications WHERE catalog_id='{f.catalog}';"
        f"INSERT INTO trading.publications VALUES('{f.catalog}',publication,pg_current_xact_id());"
        f"INSERT INTO trading.gas_transport_route_plans VALUES('{f.catalog}','{route}','{new}',p.stream_id,p.business_key,p.revision+1,p.id,p.definition_id,'release','explicit_route_membership_release_v1',"
        f"'Synthetic incomplete release','{context}',{facts['stateVersion']},NULL,p.actor_id,publication,'{f.effective}');",'gas_transport_route_integrity')
    # A definition's child set is sealed even after the producer eagerly checks
    # its first complete request. Deferred mode is deliberately restored.
    definition=f.route_definition('synthetic_sealed_graph_members');op=str(uuid4());extra=identity('gas_transport_route_node_v1:synthetic_extra',definition['routeId'])
    attack(f"r=security.record_gas_transport_route('{f.catalog}','{op}',{literal(definition)},'{f.effective}',NULL,'{context}');"
        f"INSERT INTO trading.gas_transport_route_nodes VALUES('{f.catalog}','{definition['routeId']}','{extra}','synthetic_extra','external_delivery_v1',"
        f"'{f.manifest['referenceCatalogId']}','{f.manifest['referenceRevisionId']}','test_gas','test_pipeline_a','synthetic_gas_zone_a','synthetic_gas_point_a');",'gas_transport_route_integrity')
    # Both forged acknowledgements and fresh changed=true duplicate facts must
    # be tied to an authentic original source and a genuinely new semantic fact.
    attack(f"INSERT INTO trading.gas_transport_route_commands SELECT c.catalog_id,'{uuid4()}',c.actor_id,c.route_id,jsonb_set(c.request,'{{contextId}}','\"{context}\"'),c.fact,"
        f"c.outcome||'{{\"changed\":false}}', '{context}',c.authority_until FROM trading.gas_transport_route_commands c WHERE(c.catalog_id,c.operation_id)=('{f.catalog}','{head['id']}');",'gas_transport_route_integrity')
    new=str(uuid4())
    attack(f"SELECT * INTO STRICT p FROM trading.gas_transport_route_plans WHERE(catalog_id,id)=('{f.catalog}','{head['id']}');"
        f"SELECT max(publication_order)+1 INTO publication FROM trading.publications WHERE catalog_id='{f.catalog}';INSERT INTO trading.publications VALUES('{f.catalog}',publication,pg_current_xact_id());"
        f"INSERT INTO trading.gas_transport_route_plans SELECT catalog_id,route_id,'{new}',stream_id,business_key,revision+1,id,definition_id,kind,admission_policy,evidence,'{context}',{facts['stateVersion']},"
        f"jsonb_set(assessment,'{{revisionId}}','\"{new}\"'),actor_id,publication,effective_at FROM trading.gas_transport_route_plans WHERE(catalog_id,id)=('{f.catalog}','{head['id']}');"
        f"INSERT INTO trading.gas_transport_route_sources SELECT catalog_id,'{new}',entitlement_id,capacity_version_id,capacity_generation,assessment FROM trading.gas_transport_route_sources WHERE(catalog_id,route_plan_id)=('{f.catalog}','{head['id']}');"
        f"INSERT INTO trading.gas_transport_route_source_plans SELECT catalog_id,'{new}',entitlement_id,capacity_business_key,first_plan_revision_id,plan_revision_id,trading.gas_transport_route_membership_generation(catalog_id,first_plan_revision_id,NULL,'{new}') FROM trading.gas_transport_route_source_plans WHERE(catalog_id,route_plan_id)=('{f.catalog}','{head['id']}');"
        f"INSERT INTO trading.gas_transport_route_flows SELECT catalog_id,route_id,'{new}',id,segment_id,claim_id,entitlement_id,plan_revision_id FROM trading.gas_transport_route_flows WHERE(catalog_id,route_plan_id)=('{f.catalog}','{head['id']}');"
        f"payload=trading.gas_transport_route_recorded_body('{f.catalog}','{route}','{new}');"
        f"fact_value=jsonb_build_object('body',trading.gas_transport_route_fact_key(payload),'effectiveAt',trading.gas_transport_capacity_instant(p.effective_at));"
        f"outcome=jsonb_build_object('routeId','{route}'::uuid,'kind','plan','changed',true,'definitionId',p.definition_id,'stateVersion',publication,'routePlanId',p.stream_id,'revisionId','{new}'::uuid,'revision',p.revision+1);"
        f"INSERT INTO trading.gas_transport_route_commands VALUES('{f.catalog}','{new}',p.actor_id,'{route}',jsonb_build_object('body',payload,'contextId','{context}','effectiveAt',trading.gas_transport_capacity_instant(p.effective_at)),fact_value,outcome,'{context}',security.gas_transport_route_authority('{f.catalog}','plan'));",'gas_transport_route_integrity')
    # A public route command is eagerly valid, then a valid sibling capacity
    # correction changes its exact source in the same transaction. Complete
    # source proof at deferred admission must still notice the correction.
    command=f.route_plan(facts);command['evidence']='Synthetic deferred source-currency proof'
    operation=str(uuid4());witness=f.route_witness(facts,command,operation)
    capacity=facts['sourceCapacities'][0];native_head=capacity['plans'][0];capacity_operation=str(uuid4())
    claims=[dict(pathId=c['path_id'],receiptFrom=f.instant(c['receipt_from']),receiptTo=f.instant(c['receipt_to']),receiptQuantity=c['receipt_quantity'],
        deliveryFrom=f.instant(c['delivery_from']),deliveryTo=f.instant(c['delivery_to']),deliveryQuantity=c['delivery_quantity']) for c in capacity['claims']]
    claims.sort(key=lambda c:json.dumps(c,separators=(',',':')))
    for ordinal,claim in enumerate(claims):claim['claimId']=identity('gas_transport_claim_v1',capacity_operation,ordinal)
    claims.sort(key=lambda c:(c['pathId'],c['claimId']))
    revised=dict(kind='plan',entitlementId=capacity['entitlement']['id'],expectedStateVersion=capacity['stateVersion'],expectedPreviousId=native_head['id'],businessKey=native_head['business_key'],
        versionId=capacity['version']['id'],admissionPolicy='complete_feasible_replacement_v1',evidence='Synthetic same-transaction capacity correction',claims=claims)
    attack(f"r=security.record_gas_transport_route('{f.catalog}','{operation}',{literal(command)},'{f.effective}',{literal(witness)},'{context}');"
        f"PERFORM security.record_gas_transport_capacity('{f.catalog}','{capacity_operation}',{literal(revised)},'{f.effective}',{literal(f.capacity_witness(capacity,claims))},'{context}');",'gas_transport_route_stale')
    # Flush only the original command's independent deferred gate, before the
    # incomplete sibling's own typed guards run. A later release changes the
    # captured membership prefix even though it leaves more capacity available.
    command['evidence']='Synthetic deferred membership-prefix proof';operation=str(uuid4());witness=f.route_witness(facts,command,operation);sibling=str(uuid4())
    attack(f"r=security.record_gas_transport_route('{f.catalog}','{operation}',{literal(command)},'{f.effective}',{literal(witness)},'{context}');"
        f"SELECT * INTO STRICT p FROM trading.gas_transport_route_plans WHERE(catalog_id,id)=('{f.catalog}','{operation}');"
        f"SELECT max(publication_order)+1 INTO publication FROM trading.publications WHERE catalog_id='{f.catalog}';INSERT INTO trading.publications VALUES('{f.catalog}',publication,pg_current_xact_id());"
        f"INSERT INTO trading.gas_transport_route_plans VALUES('{f.catalog}','{route}','{sibling}',p.stream_id,p.business_key,p.revision+1,p.id,p.definition_id,'release','explicit_route_membership_release_v1',"
        f"'Synthetic later membership touch','{context}',{facts['stateVersion']},NULL,p.actor_id,publication,'{f.effective}');SET CONSTRAINTS trading.valid_gas_transport_route_deadline IMMEDIATE;",'gas_transport_route_stale')
    assert q(f"SELECT count(*) FROM trading.gas_transport_route_plans WHERE catalog_id='{f.catalog}';")==before
    print('PASS independent deferred seals reject receipt-free headers, same-transaction graph append, false source-lineage acknowledgement, duplicate semantic facts and post-eager capacity/membership source changes without head growth.',flush=True)
