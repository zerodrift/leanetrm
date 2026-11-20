"""Original-command acknowledgements resolve accepted quantity snapshots, never duplicate effects."""
import json
from decimal import Decimal
from uuid import uuid4

def checks(query,manifest,state):
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];account=state['account'];at=state['at'];loan=state['loan'];failures=[]
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-20',NULL);")
    def source():
        context=capture()
        return json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
            f"SELECT security.read_gas_loan_reconciliation('{catalog}','{loan['id']}','{context}','{at}')::text;"))
    def record(action,body,policy=None,operation=None,profile='trader',context=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{operation or uuid4()}','{action}','{account}',{literal(body)},'{at}','{context or capture()}',"
            +(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;",profile))
    def evidence(key,kind,quantity,when,revision):
        return record('evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,
            evidence='Synthetic accepted quantity source snapshot',expectedRevision=revision))['id']
    def revise_uses(changes):
        current=source();uses={row['id']:row for row in current['consumptions']}
        body=dict(loanId=loan['id'],expectedRevision=current['loan']['expectedRevision'],stockExpectedRevision=current['stock']['financialRevision'],
            returnExpectedRevision=current['return']['financialRevision'],quantity=current['loan']['quantity'],
            titleEvidenceId=current['loan']['title_evidence_id'],agreementEvidenceId=current['loan']['agreement_evidence_id'],
            sourceAllocation='preserve_unallocated_source_quantity_v1',feeDisposition='retain_fixed_fee_memberships_v1',
            consumptions=[dict(consumptionId=identity,expectedRevision=uses[identity]['financialRevision'],lotId=uses[identity]['lot_id'],quantity=quantity,
                evidenceId=selected_evidence,reservationId=uses[identity]['reservation_id'],retracted=False,sourceAllocation='preserve_unallocated_source_quantity_v1')
                for identity,quantity,selected_evidence in changes],
            evidence='Synthetic complete current financial use correction')
        result=json.loads(call(f"SELECT security.reconcile_gas_loan_quantities('{catalog}','{uuid4()}','{account}',{literal(body)},'{at}','{capture()}','{state['policy']}',1,false)::text;"))
        quantities={row['consumptionId']:Decimal(row['currentQuantity']) for row in result['consumptions']}
        assert result['changed'] and quantities=={identity:Decimal(quantity) for identity,quantity,_ in changes}
        return {row['id']:row for row in source()['consumptions'] if row['id'] in quantities}
    def revise(identity,quantity,selected_evidence):
        return revise_uses([(identity,quantity,selected_evidence)])[identity]
    def original(action,use,loan_id=None):
        value=dict(lotId=use['lot_id'],evidenceId=use['evidence_id'],quantity=use['quantity'],reservationId=use['reservation_id'],effectiveAt=use['effective_at'].replace('+00:00','Z'))
        if loan_id is not None:value['loanId']=loan_id
        return value
    def counts():
        return query(f"SELECT jsonb_build_array((SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}'),"
            f"(SELECT count(*) FROM trading.gas_owned_consumptions WHERE catalog_id='{catalog}'))::text;")
    def acknowledged(action,body,policy,expected_id,revision,current_revision,current_quantity,tag,operation=None,context=None):
        before=counts()
        try:result=record(action,body,policy,operation=operation,context=context)
        except RuntimeError as error:
            assert 'gas_owned_evidence_capacity' in str(error) or 'gas_owned_evidence' in str(error),str(error)
            failures.append(tag);print('Observed original-command false refusal: '+tag,flush=True);return None
        assert result['id']==expected_id and not result['changed'] and result['semanticAcknowledgement'],(tag,result)
        assert result['acknowledgedFinancialRevision']==revision and result['currentFinancialRevision']==current_revision
        assert Decimal(result['currentQuantity'])==Decimal(current_quantity) and Decimal(result['acknowledgedQuantity'])==Decimal(body['quantity'])
        assert counts()==before,'An existing accepted quantity snapshot created another effect/publication.'
        return result
    # Dispatch B is an accepted q2 fact; C later becomes current q3. A fresh
    # nonce for B must acknowledge it while reporting C, never reactivating B.
    current=source();use=next(row for row in current['consumptions'] if row['id']==state['dispatch'])
    old=next(row for row in current['evidence'] if row['id']==use['evidence_id'])
    b=evidence(old['source_key'],old['kind'],'0.125',old['occurred_at'].replace('+00:00','Z'),old['revision'])
    use_b=revise(use['id'],'0.125',b);body_b=original('dispatch',use_b);operation=str(uuid4());operation_context=capture()
    first=acknowledged('dispatch',body_b,state['dispatchPolicy'],use['id'],2,2,'0.125','current_dispatch',operation,operation_context)
    c=evidence(old['source_key'],old['kind'],'0.0625',old['occurred_at'].replace('+00:00','Z'),old['revision']+1)
    revise(use['id'],'0.0625',c)
    acknowledged('dispatch',body_b,state['dispatchPolicy'],use['id'],2,3,'0.0625','historical_dispatch_no_rewind')
    if first is not None:
        # The same original command receives its first, not a refreshed, outcome.
        assert record('dispatch',body_b,state['dispatchPolicy'],operation=operation,context=operation_context)==first
    # A signed loss source uses magnitude for capacity; its accepted quantity
    # snapshot still has a positive consumed amount.
    policy=str(uuid4());policy_body=dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action='gas.owned.loss',mode='manual',
        fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(policy_body)});")
    record('bind_policy',dict(action='gas.owned.loss',policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    loss_at='2020-12-20T13:00:00Z';loss_a=evidence('synthetic_origin_loss','physical_loss','-0.125',loss_at,0)
    loss=record('loss',dict(lotId=loan['stockId'],evidenceId=loss_a,quantity='0.125',reservationId=None,effectiveAt=loss_at),policy)['id']
    loss_b=evidence('synthetic_origin_loss','physical_loss','-0.0625',loss_at,1)
    loss_use=revise(loss,'0.0625',loss_b)
    acknowledged('loss',original('loss',loss_use),policy,loss,2,2,'0.0625','current_signed_loss')
    # Repayment owns a distinct native link ID; matching only the consumption ID
    # would return the wrong business record or discharge the loan twice.
    current=source();repayment=next(row for row in current['repayments'] if row['id']==state['repayment'])
    repaid=next(row for row in current['consumptions'] if row['id']==repayment['consumption_id'])
    document=next(row for row in current['evidence'] if row['id']==repaid['evidence_id'])
    returned=evidence(document['source_key'],'loan_return','0.125',document['occurred_at'].replace('+00:00','Z'),document['revision'])
    repaid=revise(repaid['id'],'0.125',returned)
    repay_policy=query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)=('{catalog}','{account}','gas.loan.repay') ORDER BY revision DESC LIMIT 1;")
    acknowledged('repay',original('repay',repaid,loan['id']),repay_policy,state['repayment'],2,2,'0.125','current_linked_repayment')
    # Distinct accepted partial uses can converge to equal financial snapshots
    # through explicit corrections, while retaining every capacity/source check.
    common_at='2020-12-20T14:00:00Z'
    common_a=evidence('synthetic_ambiguous_origin','physical_delivery','0.25',common_at,0)
    common_body=dict(lotId=loan['stockId'],evidenceId=common_a,quantity='0.125',reservationId=None,effectiveAt=common_at)
    common_first=record('dispatch',common_body,state['dispatchPolicy'])['id']
    common_b=record('evidence',dict(sourceKey='synthetic_ambiguous_origin',kind='physical_delivery',quantity='0.25',occurredAt=common_at,
        evidence='Synthetic successor evidence for exact retained partial allocations',expectedRevision=1))['id']
    revise(common_first,'0.125',common_b)
    second=record('dispatch',common_body|dict(quantity='0.0625',evidenceId=common_b),state['dispatchPolicy'])['id']
    assert second!=common_first,'Distinct partial allocations must retain independent native identities.'
    # Keep the original .0625 unallocated while both accepted uses become .125.
    common_c=evidence('synthetic_ambiguous_origin','physical_delivery','0.3125',common_at,2)
    assert len({common_a,common_b,common_c})==3,'The fixture requires three distinct accepted source revisions.'
    common_uses=revise_uses([(common_first,'0.125',common_c),(second,'0.125',common_c)])
    ambiguous_body=original('dispatch',common_uses[common_first])
    before=counts()
    try:record('dispatch',ambiguous_body,state['dispatchPolicy'])
    except RuntimeError as error:assert 'gas_owned_fact_ambiguous' in str(error),str(error)
    else:failures.append('ambiguous_equal_financial_snapshots')
    assert counts()==before
    if first is not None:
        # Existing shared facts may be acknowledged by another authorized actor;
        # an existing operation ID remains tied to its original actor/payload.
        profile='synthetic_origin_fact_operator';principal=str(uuid4())
        actor=query(f"SELECT principal_id FROM security.identity_bindings WHERE(issuer,subject)=('{issuer}','trader');")
        query(f"INSERT INTO security.principals VALUES('{principal}','human',true);"
            f"INSERT INTO security.identity_bindings VALUES('{issuer}','{profile}','{principal}','human_interactive');"
            f"INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{principal}','{profile}','Synthetic origin fact operator',true,1);"
            f"INSERT INTO security.role_grants SELECT gen_random_uuid(),'{principal}',role_id,tenant_id,workspace_id,granted_from,granted_to,revoked "
            f"FROM security.role_grants WHERE principal_id='{actor}';")
        other=record('dispatch',body_b,state['dispatchPolicy'],profile=profile)
        assert other['id']==use['id'] and not other['changed'] and other['currentFinancialRevision']==3
        for changed,who in [(body_b,profile),(body_b|dict(quantity='0.03125'),'trader')]:
            try:record('dispatch',changed,state['dispatchPolicy'],operation=operation,profile=who,context=operation_context)
            except RuntimeError as error:assert 'gas_owned_conflict' in str(error),str(error)
            else:raise AssertionError('A financial acknowledgement accepted another actor or changed exact operation payload.')
        assert counts()==before
    assert not failures,'Existing accepted financial snapshots were falsely refused: '+', '.join(failures)
    print('PASS exact original dispatch/loss/repayment snapshot acknowledgement, historical no-rewind and first outcomes; capacity unchanged',flush=True)
