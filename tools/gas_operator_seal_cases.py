"""A native operator receipt proves source and original cash lineage at COMMIT."""
import json
import re
from uuid import uuid4

from gas_operator_source_authority import _state


PRODUCER='security.reconcile_gas_operator_quantities(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'


def _literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"


def _refused(call,sql,expected,label,immediate):
    try:call(sql+('SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;' if immediate else ''))
    except RuntimeError as error:
        assert re.search(r'ERROR:\s+(?:[A-Z0-9]{5}:\s+)?'+re.escape(expected)+r'(?:\n|$)',str(error)),label+': '+str(error)
    else:raise AssertionError('Operator producer mutation '+label+' accepted at '+('IMMEDIATE validation' if immediate else 'actual COMMIT')+'.')


def checks(query,manifest,call,capture,read,record,amend,packet):
    catalog=manifest['catalogId'];account=packet['accountId']
    candidate=json.loads(json.dumps(packet['body']));context=capture()
    for chosen in candidate['claims']:
        source=read(chosen['claimId'],context);chosen['expectedRevision']=source['claim']['expectedRevision']
        steps={row['id']:row for row in source['settlements']}
        for index,step in enumerate(chosen['settlements']):
            step['expectedRevision']=steps[step['settlementId']]['expectedRevision']
            if chosen['claimId']==packet['rootClaimId']:step['quantity']=['0.5','2.5'][index]
    foreign=capture('2020-12-21');original=query(f"SELECT pg_get_functiondef('{PRODUCER}'::regprocedure);")
    header="INSERT INTO trading.gas_operator_quantity_corrections VALUES(catalog,operation,account,(body->>'rootClaimId')::uuid,publication,read_publication,context,as_of,"
    assert original.count(header)==1,'The producer header hook must name one exact native insertion.'
    replacement=f"canonical=jsonb_set(canonical,'{{contextId}}',to_jsonb('{foreign}'::uuid)); "+header.replace('read_publication,context,as_of,',f"read_publication,'{foreign}'::uuid,as_of,")
    before=_state(query,catalog)
    try:
        query(original.replace(header,replacement,1))
        for body,expected,label in [(candidate,'gas_operator_quantity_source','new effect foreign-day source'),
            (packet['body'],'gas_operator_quantity_seal','all-acknowledgement foreign-day source')]:
            for immediate in (False,True):
                operation=str(uuid4());selected=capture()
                sql=f"SELECT security.reconcile_gas_operator_quantities('{catalog}','{operation}','{account}',{_literal(body)},'{packet['at']}','{selected}','{packet['policyId']}',{packet['policyRevision']},false)::text;"
                _refused(call,sql,expected,label,immediate)
                assert _state(query,catalog)==before,'Rejected foreign-day source changed native operator/cash/bank/receipt state.'
    finally:query(original)
    print('PASS operator source proof: new effects refuse at independent allocation proof; all-acknowledgement headers refuse at deferred COMMIT and IMMEDIATE',flush=True)
    _original_cash(query,manifest,call,capture,record,packet)
    _acknowledgement_members(query,manifest,call,capture,amend,packet)


def _acknowledgement_members(query,manifest,call,capture,amend,packet):
    catalog=manifest['catalogId'];account=packet['accountId'];body=packet['body']
    root=next(row for row in body['claims'] if row['claimId']==packet['rootClaimId'])
    evidence=root['settlements'][0]['evidenceId']
    selected=capture();operation=str(uuid4());accepted=amend(body,operation,selected)
    assert not accepted['changed'] and accepted['semanticAcknowledgement'] and accepted['cashChanges']==[]
    assert query(f"SELECT count(*) FROM trading.gas_operator_source_members WHERE(catalog_id,operation_id)=('{catalog}','{operation}');")=='0'
    original=query(f"SELECT pg_get_functiondef('{PRODUCER}'::regprocedure);")
    prior='IF receipt_only THEN RETURN jsonb_build_object(\'outcome\',prior.outcome);END IF;RETURN prior.outcome;'
    assert original.count(prior)==1,'The retained first-outcome hook must identify one exact producer branch.'
    insertion="INSERT INTO trading.gas_operator_source_members VALUES(catalog,operation,"+\
        f"'{account}'::uuid,'{evidence}'::uuid,'{evidence}'::uuid,'preserve_unallocated_source_quantity_v1');"
    before=_state(query,catalog)
    try:
        query(original.replace(prior,insertion+prior,1))
        for immediate in (False,True):
            sql=f"SELECT security.reconcile_gas_operator_quantities('{catalog}','{operation}','{account}',{_literal(body)},'{packet['at']}','{selected}','{packet['policyId']}',{packet['policyRevision']},false)::text;"
            _refused(call,sql,'gas_operator_quantity_seal','late source member on retained all-ACK receipt',immediate)
            assert _state(query,catalog)==before,'A rejected retained ACK member changed financial or first-receipt proof state.'
    finally:query(original)

    # A new ACK header must prove an empty effect-member set as well. The
    # source tuple has genuine same-account evidence FKs, but no financial
    # change authorizes a source-allocation effect under this operation.
    selected=capture();before=_state(query,catalog);name='synthetic_operator_ack_'+uuid4().hex
    try:
        query(f"CREATE FUNCTION trading.{name}() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN "
            f"IF NEW.catalog_id='{catalog}'::uuid AND NEW.request->>'action'='operator_quantity_reconciliation' "
            "AND NEW.outcome->>'changed'='false' THEN "
            "INSERT INTO trading.gas_operator_source_members VALUES(NEW.catalog_id,NEW.request_id,"+
            f"'{account}'::uuid,'{evidence}'::uuid,'{evidence}'::uuid,'preserve_unallocated_source_quantity_v1');END IF;RETURN NEW;END$$;"
            f"ALTER FUNCTION trading.{name}() OWNER TO openetrm_security_owner;REVOKE ALL ON FUNCTION trading.{name}() FROM PUBLIC;"
            f"CREATE TRIGGER zz_{name} BEFORE INSERT ON trading.gas_owned_commands FOR EACH ROW EXECUTE FUNCTION trading.{name}();")
        for immediate in (False,True):
            sql=f"SELECT security.reconcile_gas_operator_quantities('{catalog}','{uuid4()}','{account}',{_literal(body)},'{packet['at']}','{selected}','{packet['policyId']}',{packet['policyRevision']},false)::text;"
            _refused(call,sql,'gas_operator_quantity_seal','new all-ACK command with extra source member',immediate)
            assert _state(query,catalog)==before,'A rejected new ACK member changed financial or first-receipt proof state.'
    finally:query(f"DROP TRIGGER IF EXISTS zz_{name} ON trading.gas_owned_commands;DROP FUNCTION IF EXISTS trading.{name}();")
    print('PASS operator no-effect proof: valid-FK source members refuse on retained receipt replay and new all-ACK commands at actual COMMIT and IMMEDIATE',flush=True)


def _original_cash(query,manifest,call,capture,record,packet):
    catalog=manifest['catalogId'];account=packet['accountId'];terms=packet['cashoutTerms'];point=str(uuid4());tag='synthetic_operator_origin_'+uuid4().hex
    query("INSERT INTO market_data.observations SELECT catalog_id,"+f"'{point}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
        f"source_code,'{tag}','1',role,'final','2020-12-19T15:03:00Z',clock_timestamp(),1.005,NULL "
        f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{packet['originalObservationId']}');")
    def evidence(suffix,kind,quantity,at):return record('evidence',dict(sourceKey=tag+'_'+suffix,kind=kind,quantity=quantity,occurredAt=at,
        expectedRevision=0,evidence='Synthetic independent operator cash origin'))['id']
    statement=evidence('statement','operator_statement','3','2020-12-19T12:00:00Z')
    claim=record('operator_claim',dict(evidenceId=statement,quantity='3',effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-24T12:00:00Z',
        carrying='currency_curve_v1',settlementMode='cashout_final_price_v1',cashout=dict(source=terms['source_code'],role=terms['role'],
            observedAt='2020-12-19T15:03:00Z',cashDueAt='2020-12-24T12:00:00Z',rounding=terms['rounding'])),'gas.operator.record')
    document=evidence('determination','cashout_determination','3','2020-12-20T13:00:00Z')
    selected=capture('2020-12-22')
    policy=json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
        f"WHERE(catalog_id,account_id,action,revision)=('{catalog}','{account}','gas.operator.settle',1);"))
    other=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='approver';")
    assert policy and other
    body=dict(positionId=claim['id'],quantity='1',ownedAccountId=None,lotId=None,reservationId=None,evidenceId=document,
        effectiveAt='2020-12-20T13:00:00Z',availableAt=None,sourceObservationId=point)
    before=_state(query,catalog)
    mutations=[('actor',f"NEW.actor_id='{other}'::uuid;"),('effective time',"NEW.effective_at=NEW.effective_at+interval '1 minute';"),
        ('publication',"NEW.publication_order=NEW.publication_order+1;INSERT INTO trading.publications VALUES(NEW.catalog_id,NEW.publication_order,pg_current_xact_id());")]
    for label,mutation in mutations:
        name='synthetic_operator_origin_'+uuid4().hex
        try:
            query(f"CREATE FUNCTION trading.{name}() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN "
                f"IF(NEW.catalog_id,NEW.position_id)=('{catalog}'::uuid,'{claim['id']}'::uuid) THEN {mutation}END IF;RETURN NEW;END$$;"
                f"ALTER FUNCTION trading.{name}() OWNER TO openetrm_security_owner;REVOKE ALL ON FUNCTION trading.{name}() FROM PUBLIC;"
                f"CREATE TRIGGER zz_{name} BEFORE INSERT ON trading.gas_owned_fees FOR EACH ROW EXECUTE FUNCTION trading.{name}();")
            for immediate in (False,True):
                sql=f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','operator_settle','{account}',{_literal(body)},'2020-12-22T16:00:00Z','{selected}','{policy['id']}',{policy['revision']},false)::text;"
                _refused(call,sql,'gas_operator_quantity_origin','original native settlement cash '+label,immediate)
                assert _state(query,catalog)==before,'Rejected cash '+label+' changed native operator/cash/bank/receipt state.'
        finally:query(f"DROP TRIGGER IF EXISTS zz_{name} ON trading.gas_owned_fees;DROP FUNCTION IF EXISTS trading.{name}();")
    print('PASS operator original cash origin: valid-FK actor, effective-time and same-transaction publication faults refuse at actual COMMIT and IMMEDIATE',flush=True)
