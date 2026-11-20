"""Exact native operator receipt, origin and original-authority proofs."""
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
import json
import re
import time
from uuid import uuid4

from gas_operator_seal_cases import PRODUCER,_literal
from gas_operator_source_authority import _state

APPLY='trading.apply_gas_owned_financial(uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer)'
SEAL='trading.check_gas_operator_settlement_quantity()'


class AcceptedDefect(AssertionError):pass


def _denied(call,sql,code):
    try:call(sql)
    except RuntimeError as error:
        assert re.search(r'ERROR:\s+(?:[A-Z0-9]{5}:\s+)?'+re.escape(code)+r'(?:\n|$)',str(error)),str(error)
        return True
    return False


def checks(query,manifest,call,capture,read,record,amend,packet):
    failures=[]
    for label,probe in [('read publication',_read_publication),('complete original settlement',_origin_complete),
                        ('last original authority',_origin_authority),('financial conflict receipt',_conflict_receipt)]:
        try:probe(query,manifest,call,capture,read,record,amend,packet)
        except AcceptedDefect as error:
            failures.append(label);print('ACTUAL RED '+label+': '+str(error),flush=True)
    assert not failures,'Accepted operator proof defects: '+', '.join(failures)


def _read_publication(query,manifest,call,capture,read,record,amend,packet):
    catalog=manifest['catalogId'];account=packet['accountId'];body=json.loads(json.dumps(packet['body']))
    root=next(row for row in body['claims'] if row['claimId']==packet['rootClaimId']);root['expectedRevision']=1
    for index,step in enumerate(root['settlements']):step.update(expectedRevision=1,quantity=['1','2'][index])
    context=capture();head=read(packet['rootClaimId'],context)
    current_versions={row['id']:row['expectedRevision'] for row in head['settlements']}
    assert len(current_versions)==2 and all(version>=2 for version in current_versions.values())
    control=amend(body,str(uuid4()),context);member=next(row for row in control['claims'] if row['claimId']==packet['rootClaimId'])
    assert control['semanticAcknowledgement'] and not control['changed']
    assert all(row['acknowledgedRevision']==1 and row['currentRevision']==current_versions[row['settlementId']] for row in member['settlements'])
    original=query(f"SELECT pg_get_functiondef('{PRODUCER}'::regprocedure);")
    needle='SELECT max(p.publication_order) INTO STRICT read_publication FROM trading.publications p WHERE p.catalog_id=catalog;'
    assert original.count(needle)==1
    forged=int(packet['outcome']['publicationOrder'])-1;assert forged>0
    context=capture();before=_state(query,catalog)
    try:
        query(original.replace(needle,needle+f' read_publication={forged};',1))
        for immediate in (False,True):
            sql=f"SELECT security.reconcile_gas_operator_quantities('{catalog}','{uuid4()}','{account}',{_literal(body)},'{packet['at']}','{context}','{packet['policyId']}',{packet['policyRevision']},false)::text;"
            if not _denied(call,sql+('SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;' if immediate else ''),'gas_operator_quantity_seal'):
                raise AcceptedDefect('A new receipt reported old current head1 while the actual newer financial head remained present at actual COMMIT.')
            assert _state(query,catalog)==before
    finally:query(original)
    print('PASS exact current financial read publication: originalA retains its exact acknowledged/current heads; forged old current head refuses at COMMIT and IMMEDIATE',flush=True)


def _cash_claim(query,manifest,call,capture,record,packet,minute,quantity='10'):
    catalog=manifest['catalogId'];point=str(uuid4());tag='synthetic_operator_proof_'+uuid4().hex;terms=packet['cashoutTerms']
    when=f'2020-12-19T15:{minute}:00Z'
    query("INSERT INTO market_data.observations SELECT catalog_id,"+f"'{point}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
        f"source_code,'{tag}','1',role,'final','{when}',clock_timestamp(),0,NULL FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{packet['originalObservationId']}');")
    def claim(suffix):
        evidence=record('evidence',dict(sourceKey=tag+suffix,kind='operator_statement',quantity=quantity,occurredAt='2020-12-19T12:00:00Z',
            expectedRevision=0,evidence='Synthetic independent original operator proof'))['id']
        return record('operator_claim',dict(evidenceId=evidence,quantity=quantity,effectiveAt='2020-12-19T12:00:00Z',dueAt='2020-12-24T12:00:00Z',
            carrying='currency_curve_v1',settlementMode='cashout_final_price_v1',cashout=dict(source=terms['source_code'],role=terms['role'],
                observedAt=when,cashDueAt='2020-12-24T12:00:00Z',rounding=terms['rounding'])),'gas.operator.record')['id']
    return point,tag,claim


def _origin_complete(query,manifest,call,capture,read,record,amend,packet):
    catalog=manifest['catalogId'];account=packet['accountId'];point,tag,claim=_cash_claim(query,manifest,call,capture,record,packet,'05')
    primary,extra=claim('_primary'),claim('_extra')
    document=record('evidence',dict(sourceKey=tag+'_determination',kind='cashout_determination',quantity='1.25',occurredAt='2020-12-20T13:00:00Z',
        expectedRevision=0,evidence='Synthetic complete native operator publication'))['id']
    body=dict(positionId=primary,quantity='1',ownedAccountId=None,lotId=None,reservationId=None,evidenceId=document,effectiveAt='2020-12-20T13:00:00Z',availableAt=None,sourceObservationId=point)
    policy=json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
        f"WHERE(catalog_id,account_id,action,revision)=('{catalog}','{account}','gas.operator.settle',1);"))
    original=query(f"SELECT pg_get_functiondef('{APPLY}'::regprocedure);")
    needle="INSERT INTO trading.gas_owned_claim_settlements VALUES(catalog,id,claim.id,qty,NULL,NULL,other,e.id,(body->>'sourceObservationId')::uuid,actor,publication,at_time);"
    assert original.count(needle)==1
    replacement=needle+f" IF claim.id='{primary}'::uuid THEN INSERT INTO trading.gas_owned_claim_settlements VALUES(catalog,gen_random_uuid(),'{extra}'::uuid,0.25,NULL,NULL,NULL,e.id,(body->>'sourceObservationId')::uuid,actor,publication,at_time);END IF;"
    context=capture('2020-12-22');before=_state(query,catalog)
    try:
        query(original.replace(needle,replacement,1))
        for immediate in (False,True):
            sql=f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','operator_settle','{account}',{_literal(body)},'2020-12-22T16:00:00Z','{context}','{policy['id']}',{policy['revision']},false)::text;"
            # The cashout integrity proof now requires its sealed birth member.
            # Force the independent reverse-origin layer before other deferred
            # validators as well as proving the earliest actual COMMIT refusal.
            refusal='gas_operator_quantity_origin' if immediate else 'gas_operator_cashout'
            if not _denied(call,sql+('SET CONSTRAINTS trading.sealed_operator_origin IMMEDIATE;' if immediate else ''),refusal):
                assert query("SELECT count(*) FROM trading.gas_owned_claim_settlements s LEFT JOIN trading.gas_operator_settlement_quantities q "
                    "ON(q.catalog_id,q.settlement_id,q.revision)=(s.catalog_id,s.id,1) "
                    f"WHERE(s.catalog_id,s.position_id)=('{catalog}','{extra}') AND q.settlement_id IS NULL;")=='1'
                raise AcceptedDefect('A valid-FK zero-cash raw settlement in the same native publication committed without its exact q1 origin.')
            assert _state(query,catalog)==before
    finally:query(original)
    print('PASS complete original native settlement: extra valid-FK zero-cash member without q1 refuses at COMMIT and IMMEDIATE',flush=True)


def _origin_authority(query,manifest,call,capture,read,record,amend,packet):
    catalog,tenant,workspace=(manifest[k] for k in ['catalogId','tenantId','workspaceId']);account=packet['accountId']
    point,tag,claim=_cash_claim(query,manifest,call,capture,record,packet,'06','3');selected_claim=claim('_authority')
    document=record('evidence',dict(sourceKey=tag+'_determination',kind='cashout_determination',quantity='3',occurredAt='2020-12-20T13:00:00Z',
        expectedRevision=0,evidence='Synthetic original operator final authority'))['id']
    body=dict(positionId=selected_claim,quantity='1',ownedAccountId=None,lotId=None,reservationId=None,evidenceId=document,effectiveAt='2020-12-20T13:00:00Z',availableAt=None,sourceObservationId=point)
    policy=json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
        f"WHERE(catalog_id,account_id,action,revision)=('{catalog}','{account}','gas.operator.settle',1);"))
    actor=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    saved=json.loads(query("SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE p.permission='ReadGasOwnedHoldings' "
        f"AND p.role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{actor}');"));assert saved
    role,a,b=(str(uuid4()) for _ in range(3));sequence='synthetic_operator_until_'+uuid4().hex
    original=query(f"SELECT pg_get_functiondef('{SEAL}'::regprocedure);")
    needle=" THEN RAISE EXCEPTION 'gas_operator_capacity';END IF;"
    assert original.count(needle)==1 and original.count(' RETURN NULL;')==1
    tail=original.split(needle)[1].split(' RETURN NULL;')[0]
    final_guard=" IF NEW.revision=1 THEN\n  PERFORM security.require_gas_owned_commit(NEW.catalog_id,command.authority_until,command.configuring,command.requires_jobs);\n  IF command.authority_until IS NULL OR NOT isfinite(command.authority_until) OR clock_timestamp()>=command.authority_until\n  THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;\n END IF;\n"
    assert tail.strip() in('',final_guard.strip()),'The proof delay must precede final admission with no later business work.'
    delay=f" IF NEW.revision=1 AND NEW.claim_id='{selected_claim}'::uuid THEN PERFORM nextval('security.{sequence}');PERFORM pg_sleep(greatest(0,extract(epoch FROM(granted_from-clock_timestamp())))+0.05) FROM security.role_grants WHERE id='{b}';END IF;"
    context=capture('2020-12-22');before=_state(query,catalog)
    try:
        query(f"CREATE SEQUENCE security.{sequence};ALTER SEQUENCE security.{sequence} OWNER TO openetrm_security_owner;")
        query(original.replace(needle,needle+delay,1))
        query(f"BEGIN;INSERT INTO security.roles VALUES('{role}','{sequence}','Synthetic original operator authority');"
            "DELETE FROM security.role_permissions WHERE permission='ReadGasOwnedHoldings' "
            f"AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{actor}');INSERT INTO security.role_permissions VALUES('{role}','ReadGasOwnedHoldings');"
            f"INSERT INTO security.role_grants VALUES('{a}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '4 seconds',false),"
            f"('{b}','{actor}','{role}','{tenant}','{workspace}',clock_timestamp()+interval '5 seconds','9999-12-31',false);COMMIT;")
        sql=f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','operator_settle','{account}',{_literal(body)},'2020-12-22T16:00:00Z','{context}','{policy['id']}',{policy['revision']},false)::text;"
        with ThreadPoolExecutor(max_workers=1) as pool:
            worker=pool.submit(_denied,call,sql,'gas_owned_denied');deadline=time.monotonic()+3
            while query(f"SELECT is_called AND last_value=1 FROM security.{sequence};")!='t':
                if worker.done():worker.result();raise AssertionError('The original q1 seal did not reach its last proof under A.')
                assert time.monotonic()<deadline,'The native q1 proof did not reach its last callback before original authority.'
                time.sleep(.01)
            assert query(f"SELECT clock_timestamp()<x.granted_to AND clock_timestamp()<y.granted_from FROM security.role_grants x,security.role_grants y WHERE x.id='{a}' AND y.id='{b}';")=='t'
            refused=worker.result(timeout=10)
        assert call("SELECT security.has_permission('ReadGasOwnedHoldings');")=='t'
    finally:
        query(original)
        query(f"BEGIN;DELETE FROM security.role_grants WHERE id IN('{a}','{b}');DELETE FROM security.role_permissions WHERE role_id='{role}';DELETE FROM security.roles WHERE id='{role}';"
            f"INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,{_literal(saved)}) ON CONFLICT DO NOTHING;COMMIT;")
        query(f"DROP SEQUENCE IF EXISTS security.{sequence};")
    if not refused:raise AcceptedDefect('A native original q1 proof returned after original A expired at its final callback while successor B was active.')
    assert _state(query,catalog)==before
    print('PASS original q1 final authority: observed A before last native proof, original expiry denies despite active prepublished B',flush=True)


def _conflict_receipt(query,manifest,call,capture,read,record,amend,packet):
    catalog=manifest['catalogId'];case=packet['cashoutCases'][0];claim=case['claimId'];point=case['currentObservationId']
    head=read(claim,capture());book=next(row['book_id'] for row in head['accounts'] if row['id']==head['accountId'])
    expected={row['id']:dict(revision=row['expectedRevision'],known=row['knownAt']) for row in head['settlements']}
    assert {row['revision'] for row in expected.values()}=={5};pending=str(uuid4())
    query("INSERT INTO market_data.observations SELECT catalog_id,"+f"'{pending}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,"
        "source_code,source_event_id,'6',role,'final',observed_at,clock_timestamp(),-0.995,id "
        f"FROM market_data.observations WHERE(catalog_id,id)=('{catalog}','{point}');")
    context=capture();at=packet['at']
    value=json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}','2020-12-20'::date);"
        f"SELECT security.read_gas_owned_applied_sources('{catalog}','{context}','{at}',ARRAY['{book}']::uuid[])::text;"))
    found=[row for row in value['conflicts'] if row['effectId'] in expected and row['sourceKind']=='market_observation'];assert len(found)==2
    wrong=False
    for row in found:
        q=json.loads(query("SELECT jsonb_build_object('publication',publication_order) FROM trading.gas_operator_settlement_quantities "
            f"WHERE(catalog_id,settlement_id,revision)=('{catalog}','{row['effectId']}',{expected[row['effectId']]['revision']});"))
        assert row['acceptedSourceId']==point and row['currentSourceIds']==[pending]
        wrong=wrong or row['publicationOrder']!=q['publication'] or datetime.fromisoformat(row['knownAt'])!=datetime.fromisoformat(expected[row['effectId']]['known'])
    # Complete the explicit observed correction to leave unrelated continuation
    # consumers a reconciled source. The independent amount is +.04, not a price
    # or quantity inferred from the preceding assertion.
    from gas_operator_cashout_cases import _body
    result=amend(_body(head,pending),str(uuid4()),context)
    assert result['changed'] and [row['amount'] for row in result['cashChanges']]==['0.04']
    case.update(currentObservationId=pending,source=read(claim,capture()))
    if wrong:raise AcceptedDefect('Applied conflict receipt used original birth publication/knowledge instead of selected financial head5.')
    print('PASS applied observation conflict: exact selected financial-head publication/knowledge and retained original fixing coordinates',flush=True)
