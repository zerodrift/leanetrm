"""Additional native allocation boundaries; imported by the owned gas fixture."""
import json
from decimal import Decimal
from datetime import datetime
from uuid import uuid4


def upgrade_checks(query, migration):
    if migration.name != '0289_gas_quantity_reconciliation.sql':return
    from assistant_context import check_guard
    private='security.read_gas_owned_holdings_original(uuid,uuid,timestamptz,uuid[])'
    public='security.read_gas_physical_known_attachments(uuid,uuid,timestamptz,uuid[],jsonb)'
    producer='trading.apply_futures_delivery_principal(uuid,uuid,uuid,uuid,timestamptz,uuid,integer,jsonb)'
    changes=[
        (f"UPDATE pg_proc SET proargnames=ARRAY['catalog','context','changed_at','books'] WHERE oid='{private}'::regprocedure;",private),
        (f"UPDATE pg_proc SET proallargtypes=ARRAY['uuid'::regtype::oid,'uuid'::regtype::oid,'timestamptz'::regtype::oid,'uuid[]'::regtype::oid] WHERE oid='{private}'::regprocedure;",private),
        (f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid='{private}'::regprocedure;",private),
        (f"UPDATE pg_proc SET proargmodes=array_fill('i'::\"char\",ARRAY[4]) WHERE oid='{private}'::regprocedure;",private),
        (f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{private}'::regprocedure;",private),
        (f"GRANT EXECUTE ON FUNCTION {private} TO openetrm_runtime;",private),
        (f"REVOKE EXECUTE ON FUNCTION {public} FROM openetrm_risk_worker;",public),
        (f"ALTER FUNCTION {public} SECURITY INVOKER;",public),
        (f"UPDATE pg_proc SET proargdefaults=NULL WHERE oid='{public}'::regprocedure;",public),
        (f"GRANT EXECUTE ON FUNCTION {producer} TO openetrm_runtime;",producer),
        (f"ALTER FUNCTION {producer} SET work_mem='64kB';",producer),
        (f"DO $$DECLARE d text=pg_get_functiondef('{producer}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;",producer)]
    check_guard(query,migration.read_text(),'0289','gas_quantity_upgrade',changes)


def checks(query, manifest):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    at='2026-11-06T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2026-11-06',NULL);")
    def refuse(sql,code):
        try:call(sql)
        except RuntimeError as error:assert code in str(error),str(error)
        else:raise AssertionError('Expected '+code)
    long=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_owned_positions p WHERE p.catalog_id='{catalog}' AND p.kind='delivery_obligation' AND p.quantity>0;"))
    link=json.loads(query(f"SELECT to_jsonb(l) FROM settlement_data.futures_delivery_obligations l WHERE(l.catalog_id,l.position_id)=('{catalog}','{long['id']}');"))
    terms=json.loads(query(f"SELECT to_jsonb(t) FROM trading.gas_futures_delivery_terms t WHERE(t.catalog_id,t.profile_id,t.profile_revision)=('{catalog}','{link['profile_id']}',{link['profile_revision']});"))
    correction_policy=query(f"SELECT policy_id FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=('{catalog}','{long['account_id']}','gas.futures.reconcile_quantity') ORDER BY revision DESC LIMIT 1;")
    def evidence(kind,amount,when,key=None,revision=0,description='Synthetic bounded source document'):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{long['account_id']}',"
            f"{literal(dict(sourceKey=key or str(uuid4()),kind=kind,quantity=amount,occurredAt=when,evidence=description,expectedRevision=revision))},'{at}','{capture()}',NULL,NULL,false)::text;"))['id']
    # Independent cumulative eighth-unit values at the fixture's locked 6.03.
    # The half-even 9.045 case is retained explicitly rather than recomputed by
    # the production rounding helper or a second implementation of its loop.
    exact=['0','0.75375','1.5075','2.26125','3.015','3.76875','4.5225','5.27625','6.03','6.78375','7.5375','8.29125','9.045','9.79875','10.5525','11.30625','12.06']
    rounded=['0','0.75','1.51','2.26','3.02','3.77','4.52','5.28','6.03','6.78','7.54','8.29','9.04','9.80','10.55','11.31','12.06']
    def plan(holding,updates=None,added=None):
        updates={} if updates is None else updates
        rows=json.loads(query(f"SELECT coalesce(jsonb_agg(to_jsonb(p)),'[]') FROM trading.gas_futures_performances_current p WHERE(catalog_id,position_id)=('{catalog}','{holding}');"))
        if added is not None:rows.append(added)
        rows.sort(key=lambda row:(datetime.fromisoformat(row['performed_at'].replace('Z','+00:00')),row['business_reference'].encode('utf-8')))
        owner=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_owned_positions p WHERE(catalog_id,id)=('{catalog}','{holding}');"))
        native_link=json.loads(query(f"SELECT to_jsonb(l) FROM settlement_data.futures_delivery_obligations l WHERE(catalog_id,position_id)=('{catalog}','{holding}');"))
        current=json.loads(query(f"SELECT to_jsonb(r) FROM trading.gas_futures_delivery_principal_runs r WHERE(catalog_id,position_id)=('{catalog}','{holding}') ORDER BY revision DESC LIMIT 1;"))
        version=current['revision'] if current else 0
        previous=json.loads(query(f"SELECT coalesce(jsonb_object_agg(performance_id,to_jsonb(d)),'{{}}') FROM trading.gas_futures_delivery_principal_determinations d WHERE(catalog_id,position_id,run_revision)=('{catalog}','{holding}',{version});"))
        source=query(f"SELECT source_observation_id FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=('{catalog}','{native_link['trade_id']}','{native_link['binding_id']}','{native_link['final_occurrence_id']}') ORDER BY run_revision DESC LIMIT 1;")
        sign=Decimal(-1) if Decimal(str(owner['quantity']))>0 else Decimal(1)
        units=0;before=Decimal(0);steps=[]
        for row in rows:
            quantity=Decimal(str(updates.get(row['id'],row['quantity'])));parts=quantity/Decimal('.125')
            assert parts==int(parts) and quantity>=0
            units+=int(parts);total=sign*Decimal(rounded[units]);contribution=total-before;old=previous.get(row['id'])
            steps.append(dict(performanceId=row['id'],previousDeterminationId=old['id'] if old else None,cumulativeQuantity=str(-sign*Decimal('.125')*units),
                exactTotal=str(sign*Decimal(exact[units])),roundedTotal=str(total),contribution=str(contribution),adjustment=str(contribution-(Decimal(str(old['contribution'])) if old else 0))))
            before=total
        return dict(implementation='gas_futures_delivery_continuation_v1',positionId=holding,expectedVersion=version,finalOccurrenceId=native_link['final_occurrence_id'],sourceObservationId=source,steps=steps)
    def physical(reference,when,doc):
        identity=str(uuid4());proposal=plan(long['id'],added=dict(id=identity,business_reference=reference,performed_at=when,quantity='0.25'))
        body=dict(positionId=long['id'],businessReference=reference,expectedVersion=proposal['expectedVersion'],quantity='0.25',performedAt=when,titleEvidenceId=doc,availableAt=when,supply=[])
        accepted=json.loads(call(f"SELECT security.record_gas_futures_delivery('{catalog}','{identity}','{long['id']}','performance',{literal(body)},'{at}','{capture()}',"
            f"'{terms['action_policy_id']}',{terms['action_policy_revision']},false,{literal(proposal)})::text;"))
        return identity,accepted
    def amendment(performance,doc,allocation):
        proposal=plan(long['id'],updates={performance:'0'})
        body=dict(positionId=long['id'],performanceId=performance,expectedRevision=1,expectedPrincipalVersion=proposal['expectedVersion'],quantity='0',retracted=True,
            titleEvidenceId=doc,supply=[],sourceAllocation=allocation,evidence='Synthetic complete source-allocation amendment')
        sql=f"SELECT security.reconcile_gas_futures_delivery('{catalog}','{uuid4()}','{long['id']}',{literal(body)},'{at}','{capture()}','{correction_policy}',1,false,{literal(proposal)})::text;"
        return sql
    when='2026-11-06T15:00:00Z';key=str(uuid4())
    doc=evidence('title_receipt','1',when,key)
    performance,received=physical('synthetic_unused_capacity_withdrawal',when,doc)
    withdrawn=evidence('title_receipt','0',when,key,1,'Synthetic explicit full source withdrawal')
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    refuse(amendment(performance,withdrawn,'preserve_unallocated_source_quantity_v1'),'gas_source_allocation')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    assert query(f"SELECT max(revision) FROM trading.gas_futures_performance_quantities WHERE(catalog_id,performance_id)=('{catalog}','{performance}');")=='1'
    accepted=json.loads(call(amendment(performance,withdrawn,'withdraw_all_source_allocations_v1')))
    allocation=accepted['sourceAllocations'][0]
    assert allocation['oldCapacity']=='1' and allocation['oldAllocated']=='0.25' and allocation['capacity']=='0' and allocation['allocated']=='0'
    assert query(f"SELECT quantity FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{received['stockId']}');")=='0'
    when='2026-11-06T15:30:00Z';key=str(uuid4())
    performance,received=physical('synthetic_preserved_unused_capacity',when,evidence('title_receipt','1',when,key))
    reduced=evidence('title_receipt','0.75',when,key,1,'Synthetic capacity reduction preserving unused source')
    accepted=json.loads(call(amendment(performance,reduced,'preserve_unallocated_source_quantity_v1')))
    allocation=accepted['sourceAllocations'][0]
    assert allocation['oldCapacity']=='1' and allocation['oldAllocated']=='0.25' and allocation['capacity']=='0.75' and allocation['allocated']=='0'
    assert query(f"SELECT quantity FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{received['stockId']}');")=='0'
    print('PASS partial source allocations: preserve1/.25/.75, explicit full withdrawal, whole-transaction rejection of an invalid unused-capacity change and no unused-stock asset',flush=True)
    # Replace one identified short-delivery source lot. The second, unrelated
    # source piece remains exactly unchanged; its financial revision is reused.
    short=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_owned_positions p WHERE p.catalog_id='{catalog}' AND p.kind='delivery_obligation' AND p.quantity<0;"))
    performance=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_futures_performances_current p WHERE(catalog_id,position_id)=('{catalog}','{short['id']}');"))
    pieces=json.loads(query("SELECT jsonb_agg(to_jsonb(q) ORDER BY q.consumption_id) FROM trading.gas_futures_performance_quantity_supplies s "
        "JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision) "
        f"WHERE(s.catalog_id,s.performance_id,s.performance_revision)=('{catalog}','{performance['id']}',1);"))
    assert len(pieces)==2 and all(Decimal(str(p['quantity']))==Decimal('.25') for p in pieces)
    replaced,unchanged=pieces
    free=query("SELECT p.id FROM trading.gas_owned_positions_current p JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(p.catalog_id,p.id) "
        f"WHERE(p.catalog_id,p.account_id)=('{catalog}','{long['account_id']}') AND p.quantity=0.25 AND l.available_at<='{performance['performed_at']}' "
        "AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_consumptions_current c WHERE(c.catalog_id,c.lot_id)=(p.catalog_id,p.id) AND c.quantity>0) "
        "ORDER BY p.id LIMIT 1;")
    assert free,'The short reallocation needs an explicitly owned quarter available at the original dispatch instant.'
    original_source=json.loads(query(f"SELECT to_jsonb(e) FROM trading.gas_owned_evidence e WHERE(catalog_id,id)=('{catalog}','{replaced['evidence_id']}');"))
    occurred=datetime.fromisoformat(performance['performed_at'].replace('Z','+00:00')).strftime('%Y-%m-%dT%H:%M:%S.%fZ')
    revised=evidence('physical_delivery','0.25',occurred,original_source['source_key'],original_source['revision'],'Synthetic explicit source-lot reallocation')
    short_policy=query(f"SELECT policy_id FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=('{catalog}','{short['account_id']}','gas.futures.reconcile_quantity') ORDER BY revision DESC LIMIT 1;")
    assert short_policy==correction_policy
    principal=plan(short['id'])
    before_short_context=capture()
    selection=dict(positionId=short['id'],performanceId=performance['id'],expectedRevision=1,expectedPrincipalVersion=principal['expectedVersion'],quantity='0.5',retracted=False,titleEvidenceId=None,
        supply=[dict(consumptionId=replaced['consumption_id'],expectedRevision=1,lotId=replaced['lot_id'],evidenceId=revised,reservationId=replaced['reservation_id'],quantity='0',retracted=True),
            dict(consumptionId=unchanged['consumption_id'],expectedRevision=1,lotId=unchanged['lot_id'],evidenceId=unchanged['evidence_id'],reservationId=unchanged['reservation_id'],quantity='0.25',retracted=False),
            dict(consumptionId=None,expectedRevision=0,lotId=free,evidenceId=revised,reservationId=None,quantity='0.25',retracted=False)],
        sourceAllocation='preserve_unallocated_source_quantity_v1',evidence='Synthetic reviewed exact stock replacement')
    def reconcile(body):
        return f"SELECT security.reconcile_gas_futures_delivery('{catalog}','{uuid4()}','{short['id']}',{literal(body)},'{at}','{capture()}','{short_policy}',1,false,{literal(principal)})::text;"
    financial=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    incomplete={**selection,'supply':[selection['supply'][1],selection['supply'][2]]}
    refuse(reconcile(incomplete),'gas_quantity_supply')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==financial
    accepted=json.loads(call(reconcile(selection)))
    assert accepted['currentRevision']==2 and accepted['principalVersion']==2
    assert query(f"SELECT max(revision) FROM trading.gas_owned_consumption_quantities WHERE(catalog_id,consumption_id)=('{catalog}','{unchanged['consumption_id']}');")=='1'
    assert query(f"SELECT trim_scale(sum(quantity)) FROM trading.gas_owned_consumptions_current WHERE(catalog_id,lot_id)=('{catalog}','{replaced['lot_id']}');")=='0'
    assert query(f"SELECT trim_scale(sum(quantity)) FROM trading.gas_owned_consumptions_current WHERE(catalog_id,lot_id)=('{catalog}','{free}');")=='0.25'
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{short['id']}');")=='3.02'
    assert query(f"SELECT count(*) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{short['id']}');")=='1'
    def read_source(context,identity=performance['id']):
        return json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
            f"SELECT security.read_gas_futures_reconciliation('{catalog}','{short['id']}','{identity}','{context}','{at}')::text;"))
    previous_source=read_source(before_short_context);selected_source=read_source(capture())
    assert previous_source['performance']['revision']==1 and len(previous_source['supply'])==2
    assert all(p['financial_revision']==1 and not p['retracted'] for p in previous_source['supply'])
    selected_supplies={p['consumption_id']:p for p in selected_source['supply']}
    assert selected_source['performance']['revision']==2 and len(selected_supplies)==3
    assert selected_supplies[replaced['consumption_id']]['financial_revision']==2 and selected_supplies[replaced['consumption_id']]['retracted']
    assert selected_supplies[unchanged['consumption_id']]['financial_revision']==1
    assert all(p['id']==p['consumption_id'] and p['original_publication_order']<=p['publication_order'] for p in selected_supplies.values())
    try:read_source(capture(),str(uuid4()))
    except RuntimeError as error:assert 'gas_quantity_source' in str(error),str(error)
    else:raise AssertionError('An unknown performance did not produce a structured source refusal.')
    print('PASS short quantity reconciliation: complete explicit stock replacement, unchanged piece reuses its financial version, no acquisition or duplicate principal cash',flush=True)
    missing=[]
    prior=json.loads(query(f"SELECT c.request FROM trading.gas_owned_commands c WHERE c.catalog_id='{catalog}' AND c.request->>'action'='futures_quantity_reconciliation' "
        "AND c.request->'body'->>'retracted'='false' AND c.request->'body'->>'titleEvidenceId' IS NOT NULL AND c.outcome->>'changed'='true' "
        "ORDER BY(c.outcome->>'currentRevision')::integer DESC LIMIT 1;"))
    current_revision=query(f"SELECT max(revision) FROM trading.gas_futures_performance_quantities WHERE(catalog_id,performance_id)=('{catalog}','{prior['body']['performanceId']}');")
    current_principal=query(f"SELECT max(revision) FROM trading.gas_futures_delivery_principal_runs WHERE(catalog_id,position_id)=('{catalog}','{prior['body']['positionId']}');")
    wrong_policy={**prior['body'],'expectedRevision':int(current_revision),'expectedPrincipalVersion':int(current_principal),'sourceAllocation':'withdraw_all_source_allocations_v1'}
    policy_plan=plan(wrong_policy['positionId'])
    try:call(f"SELECT security.reconcile_gas_futures_delivery('{catalog}','{uuid4()}','{wrong_policy['positionId']}',{literal(wrong_policy)},'{at}','{capture()}',"
        f"'{correction_policy}',1,false,{literal(policy_plan)});")
    except RuntimeError as error:assert 'gas_source_allocation' in str(error),str(error)
    else:missing.append('allocation_policy_acknowledgement')
    # Two accepted identical source pieces must consume two matching requested
    # rows. One matching wildcard row cannot cover both and hide another row.
    twin_id=str(uuid4());twin_at='2026-11-06T14:30:00Z';twin_doc=evidence('physical_delivery','0.25',twin_at)
    second_lot=query("SELECT p.id FROM trading.gas_owned_positions_current p JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(p.catalog_id,p.id) "
        f"WHERE(p.catalog_id,p.account_id)=('{catalog}','{long['account_id']}') AND p.id<>'{replaced['lot_id']}' AND l.available_at<='{twin_at}' "
        "AND p.quantity-coalesce((SELECT sum(c.quantity) FROM trading.gas_owned_consumptions_current c WHERE(c.catalog_id,c.lot_id)=(p.catalog_id,p.id)),0)>=0.125 "
        "ORDER BY p.id LIMIT 1;")
    assert second_lot,'The identical-term test requires a second explicitly available original source lot.'
    twin_plan=plan(short['id'],added=dict(id=twin_id,business_reference='synthetic_identical_source_pieces',performed_at=twin_at,quantity='0.25'))
    twin_body=dict(positionId=short['id'],businessReference='synthetic_identical_source_pieces',expectedVersion=twin_plan['expectedVersion'],quantity='0.25',performedAt=twin_at,
        titleEvidenceId=None,availableAt=None,supply=[dict(lotId=lot,evidenceId=twin_doc,reservationId=None,quantity='0.125') for lot in (replaced['lot_id'],second_lot)])
    twin=json.loads(call(f"SELECT security.record_gas_futures_delivery('{catalog}','{twin_id}','{short['id']}','performance',{literal(twin_body)},'{at}','{capture()}',"
        f"'{terms['action_policy_id']}',{terms['action_policy_revision']},false,{literal(twin_plan)})::text;"))
    original_pieces=json.loads(query(f"SELECT jsonb_agg(to_jsonb(q) ORDER BY s.ordinal) FROM trading.gas_futures_performance_quantity_supplies s "
        "JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision) "
        f"WHERE(s.catalog_id,s.performance_id,s.performance_revision)=('{catalog}','{twin_id}',1);"))
    combined=dict(positionId=short['id'],performanceId=twin_id,expectedRevision=1,expectedPrincipalVersion=twin['principalVersion'],quantity='0.25',retracted=False,titleEvidenceId=None,
        supply=[dict(consumptionId=p['consumption_id'],expectedRevision=1,lotId=replaced['lot_id'],evidenceId=twin_doc,reservationId=p['reservation_id'],quantity='0.125',retracted=False) for p in original_pieces],
        sourceAllocation='preserve_unallocated_source_quantity_v1',evidence='Synthetic reviewed allocation from two original lots onto one retained lot')
    combined_plan=plan(short['id'])
    combined_result=json.loads(call(f"SELECT security.reconcile_gas_futures_delivery('{catalog}','{uuid4()}','{short['id']}',{literal(combined)},'{at}','{capture()}',"
        f"'{short_policy}',1,false,{literal(combined_plan)})::text;"))
    assert combined_result['currentRevision']==2
    unmatched=evidence('physical_delivery','0.125',twin_at)
    wildcard=dict(consumptionId=None,expectedRevision=0,lotId=replaced['lot_id'],evidenceId=twin_doc,reservationId=None,quantity='0.125',retracted=False)
    false_ack=dict(positionId=short['id'],performanceId=twin_id,expectedRevision=2,expectedPrincipalVersion=combined_result['principalVersion'],quantity='0.25',retracted=False,titleEvidenceId=None,
        supply=[wildcard,{**wildcard,'evidenceId':unmatched}],sourceAllocation='preserve_unallocated_source_quantity_v1',evidence='Synthetic unmatched wildcard acknowledgement refusal')
    false_ack_plan=plan(short['id'])
    try:call(f"SELECT security.reconcile_gas_futures_delivery('{catalog}','{uuid4()}','{short['id']}',{literal(false_ack)},'{at}','{capture()}','{short_policy}',1,false,{literal(false_ack_plan)});")
    except RuntimeError as error:assert 'gas_quantity_supply' in str(error),str(error)
    else:missing.append('complete_supply_multiset_acknowledgement')
    if missing:print('Observed accepted incomplete acknowledgements: '+', '.join(missing),flush=True)
    from check_gas_quantity_reconciliation_authority import read_check
    if not read_check(query,manifest,short['id'],twin_id,capture(),at,call):missing.append('native_reader_final_authority')
    if not read_check(query,manifest,short['id'],twin_id,capture(),at,call,phase='source'):missing.append('native_reader_source_authority')
    assert not missing,'Accepted incomplete correction acknowledgement/read: '+', '.join(missing)
    print('PASS correction acknowledgements bind the retained allocation policy and complete supply multiset; native reader retains original authority through its final work',flush=True)
