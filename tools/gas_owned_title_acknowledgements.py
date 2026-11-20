"""Current quantity acknowledgements preserve original title and fixed-fee terms."""
import json
from decimal import Decimal
from uuid import uuid4
from check_gas_fee_existing_origins import _tables as gas_tables, _state as gas_state


def checks(query,manifest,state):
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];account=state['account'];at=state['at'];failures=[]
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,profile='trader'):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','{at[:10]}',NULL);")
    def record(action,body,policy=None,operation=None,context=None,profile='trader'):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{operation or uuid4()}','{action}','{account}',{literal(body)},'{at}','{context or capture()}',"
            +(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;",profile))
    def saved_policy(action):
        return query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)=('{catalog}','{account}','{action}') ORDER BY revision DESC LIMIT 1;")
    def bind_policy(action):
        policy=str(uuid4())
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action=action,mode='manual',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
        record('bind_policy',dict(action=action,policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
        return policy
    def evidence(key,kind,quantity,when,revision=0):
        return record('evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,expectedRevision=revision,
            evidence='Synthetic retained title and principal snapshot'))['id']
    def financial():
        # Only the new idempotency receipt may be added. Every other native gas
        # membership, mark, allocation, bank fact and publication stays exact.
        return gas_state(query,catalog,[table for table in gas_tables(query) if table!='gas_owned_commands'])
    def commands():return int(query(f"SELECT count(*) FROM trading.gas_owned_commands WHERE catalog_id='{catalog}';"))
    def acknowledged(action,body,policy,identity,revision,tag,current_revision=None,current_quantity=None,operation=None,context=None):
        context=context or capture();before=financial();command_count=commands()
        try:result=record(action,body,policy,operation,context)
        except RuntimeError as error:
            assert any(code in str(error) for code in ('gas_owned_source_already_applied','gas_owned_evidence_capacity')),str(error)
            assert financial()==before,'An original snapshot refusal left financial state behind.'
            assert commands()==command_count,'A refused snapshot left a command receipt behind.'
            failures.append(tag);print('Observed original-command false refusal: '+tag,flush=True);return
        assert not result['changed'] and result['semanticAcknowledgement'] and result['id']==identity,result
        assert result['acknowledgedFinancialRevision']==revision,result
        assert Decimal(result['acknowledgedQuantity'])==Decimal(body['quantity']),result
        assert result['currentFinancialRevision']==(current_revision or revision),result
        assert Decimal(result['currentQuantity'])==Decimal(current_quantity or body['quantity']),result
        assert financial()==before,'An accepted original snapshot created another financial effect.'
        assert commands()==command_count+1,'A fresh acknowledgement must retain exactly one command receipt.'
        return result
    def refused(action,body,policy,codes):
        context=capture();before=financial();command_count=commands()
        try:record(action,body,policy,context=context)
        except RuntimeError as error:assert any(code in str(error) for code in codes),str(error)
        else:raise AssertionError('An original command changed immutable title, loan or fee terms through an acknowledgement.')
        assert financial()==before
        assert commands()==command_count

    # An independently titled lot has no downstream resource use. Its accepted
    # current revision is still the same owned title, never a second holding.
    key='synthetic_current_title_origin';when='2020-12-19T09:00:00Z'
    first=evidence(key,'title_receipt','1',when)
    title_body=dict(evidenceId=first,quantity='1',titleAt=when,availableAt=when,
        originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1')
    title_policy=saved_policy('gas.owned.receive_title')
    stock=record('receive_title',title_body,title_policy)['id']
    corrected=evidence(key,'title_receipt','0.75',when,1)
    ordinary_policy=bind_policy('gas.owned.reconcile_quantity')
    selection=dict(positions=[dict(positionId=stock,expectedRevision=1,quantity='0.75',retracted=False,evidenceId=corrected,
        sourceAllocation='preserve_unallocated_source_quantity_v1')],consumptions=[],evidence='Synthetic exact current title head')
    changed=json.loads(call(f"SELECT security.reconcile_gas_owned_quantities('{catalog}','{uuid4()}','{account}',{literal(selection)},'{at}','{capture()}','{ordinary_policy}',1,false)::text;"))
    assert changed['positions'][0]['currentRevision']==2
    current_title=title_body|dict(quantity='0.75',evidenceId=corrected)
    title_operation=str(uuid4());title_context=capture()
    title_ack=acknowledged('receive_title',current_title,title_policy,stock,2,'current_owned_title',operation=title_operation,context=title_context)
    later_title=evidence(key,'title_receipt','0.5',when,2)
    later_selection=selection|dict(positions=[selection['positions'][0]|dict(expectedRevision=2,quantity='0.5',evidenceId=later_title)])
    later=json.loads(call(f"SELECT security.reconcile_gas_owned_quantities('{catalog}','{uuid4()}','{account}',{literal(later_selection)},'{at}','{capture()}','{ordinary_policy}',1,false)::text;"))
    assert later['positions'][0]['currentRevision']==3
    acknowledged('receive_title',current_title,title_policy,stock,2,'historical_owned_title_no_rewind',3,'0.5',context=title_context)
    if title_ack is not None:
        before=financial();command_count=commands()
        assert record('receive_title',current_title,title_policy,title_operation,title_context)==title_ack
        assert financial()==before and commands()==command_count,'An exact title retry changed the first command outcome or native state.'
        assert Decimal(title_ack['effect']['performedQuantity'])==Decimal('.75')
    refused('receive_title',current_title|dict(availableAt='2020-12-19T09:01:00Z'),title_policy,['gas_owned_evidence','gas_owned_source_already_applied'])

    # The borrow's original fixed fee is independent of later fee amendments.
    # Principal acknowledgement must match original immutable borrow terms and
    # leave the current fee head, gross cash and bank allocations unchanged.
    loan=state['loan']['id'];source=state['source']
    original=json.loads(query(f"SELECT request->'body' FROM trading.gas_owned_commands WHERE(catalog_id,account_id,request->>'action')=('{catalog}','{account}','borrow') AND outcome->>'id'='{loan}' AND outcome->'changed'='true'::jsonb;"))
    assert Decimal(original['feeAmount'])==Decimal('-0.10')
    fee_policy=bind_policy('gas.loan.amend_fee')
    fee_body=dict(loanId=loan,expectedRevision=1,amount='-0.125',dueAt='2020-12-22T12:00:00Z',effectiveAt='2020-12-20T15:30:00Z',
        implementation='gross_timed_fixed_fee_adjustments_v1',evidence='Synthetic independent fixed borrower fee amendment')
    fee=json.loads(call(f"SELECT security.amend_gas_loan_fee_terms('{catalog}','{uuid4()}','{account}',{literal(fee_body)},'{at}','{capture()}','{fee_policy}',1,false)::text;"))
    assert fee['changed'] and fee['currentRevision']==2 and Decimal(fee['currentAmount'])==Decimal('-0.125')
    body=original|dict(quantity=source['loan']['quantity'],evidenceId=source['loan']['title_evidence_id'],agreementId=source['loan']['agreement_evidence_id'])
    borrow_policy=saved_policy('gas.loan.borrow');loan_revision=source['loan']['expectedRevision']
    borrow_operation=str(uuid4());borrow_context=capture()
    loan_ack=acknowledged('borrow',body,borrow_policy,loan,loan_revision,'current_loan_principal_with_original_fee',operation=borrow_operation,context=borrow_context)
    documents={row['id']:row for row in source['evidence']}
    def successor(identity,quantity):
        document=documents[identity]
        return evidence(document['source_key'],document['kind'],quantity,document['occurred_at'].replace('+00:00','Z'),document['revision'])
    title=successor(body['evidenceId'],'0.5');agreement=successor(body['agreementId'],'0.5')
    principal=dict(loanId=loan,expectedRevision=loan_revision,stockExpectedRevision=source['stock']['financialRevision'],returnExpectedRevision=source['return']['financialRevision'],
        quantity='0.5',titleEvidenceId=title,agreementEvidenceId=agreement,sourceAllocation='preserve_unallocated_source_quantity_v1',
        feeDisposition='retain_fixed_fee_memberships_v1',consumptions=[],evidence='Synthetic later accepted loan principal without a fee amendment')
    later=json.loads(call(f"SELECT security.reconcile_gas_loan_quantities('{catalog}','{uuid4()}','{account}',{literal(principal)},'{at}','{capture()}','{state['policy']}',1,false)::text;"))
    assert later['changed'] and later['currentRevision']==loan_revision+1
    acknowledged('borrow',body,borrow_policy,loan,loan_revision,'historical_loan_principal_no_rewind',loan_revision+1,'0.5',context=borrow_context)
    if loan_ack is not None:
        before=financial();command_count=commands()
        assert record('borrow',body,borrow_policy,borrow_operation,borrow_context)==loan_ack
        assert financial()==before and commands()==command_count,'An exact principal retry changed the first command outcome or native state.'
        assert Decimal(loan_ack['effect']['stockQuantity'])==Decimal('.375') and Decimal(loan_ack['effect']['returnQuantity'])==Decimal('-.375')
        assert Decimal(loan_ack['effect']['feeAmount'])==Decimal('-.10')
    refused('borrow',body|dict(feeAmount='-0.125'),borrow_policy,['gas_loan_terms','gas_owned_evidence','gas_owned_source_already_applied'])
    refused('borrow',body|dict(returnAt='2020-12-22T12:00:00Z'),borrow_policy,['gas_loan_terms','gas_owned_evidence','gas_owned_source_already_applied'])
    # Gross principal remains a fact after both native stock and debt are fully
    # discharged. A fresh original command must not recreate either owner.
    returned=evidence('synthetic_origin_mature_repayment','loan_return','0.25','2020-12-20T15:00:00Z')
    record('repay',dict(loanId=loan,lotId=state['loan']['stockId'],evidenceId=returned,quantity='0.25',reservationId=None,
        effectiveAt='2020-12-20T15:00:00Z'),saved_policy('gas.loan.repay'))
    current_body=original|dict(quantity='0.5',evidenceId=title,agreementId=agreement)
    acknowledged('borrow',current_body,borrow_policy,loan,loan_revision+1,'fully_repaid_principal_with_retained_fixed_fee')
    context=capture()
    held=json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_owned_holdings('{catalog}','{context}','{at}',ARRAY['{state['book']}']::uuid[])::text;"))
    assert not {state['loan']['stockId'],state['loan']['returnId']}.intersection(row['id'] for row in held['positions'])
    # A zero original fee has explicit null due time and no cash membership.
    # Inspect this separately at a stated later business time, once its native
    # title is effective; no future-title eligibility or clock rule is waived.
    at='2020-12-22T16:00:00Z'
    zero=json.loads(query(f"SELECT to_jsonb(c) FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.account_id,c.request->>'action')=('{catalog}','{account}','borrow') "
        "AND c.outcome->>'changed'='true' AND c.request#>>'{body,feeAmount}'='0';"))
    zero_id=zero['outcome']['id'];context=capture()
    zero_source=json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_loan_reconciliation('{catalog}','{zero_id}','{context}','{at}')::text;"),parse_float=Decimal)
    documents={row['id']:row for row in zero_source['evidence']}
    zero_title=successor(zero_source['loan']['title_evidence_id'],'0.5')
    zero_agreement=successor(zero_source['loan']['agreement_evidence_id'],'0.5')
    changed=principal|dict(loanId=zero_id,expectedRevision=zero_source['loan']['expectedRevision'],stockExpectedRevision=zero_source['stock']['financialRevision'],
        returnExpectedRevision=zero_source['return']['financialRevision'],titleEvidenceId=zero_title,agreementEvidenceId=zero_agreement)
    accepted=json.loads(call(f"SELECT security.reconcile_gas_loan_quantities('{catalog}','{uuid4()}','{account}',{literal(changed)},'{at}','{capture()}','{state['policy']}',1,false)::text;"))
    assert accepted['changed'] and accepted['currentRevision']==2
    zero_body=zero['request']['body']|dict(quantity='0.5',evidenceId=zero_title,agreementId=zero_agreement)
    zero_ack=acknowledged('borrow',zero_body,borrow_policy,zero_id,2,'zero_original_fee_principal_without_invented_cash')
    if zero_ack is not None:assert Decimal(zero_ack['effect']['feeAmount'])==0 and zero_ack['effect']['feeDueAt'] is None
    assert query(f"SELECT count(*) FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{zero_id}');")=='0'
    if title_ack is not None and loan_ack is not None:
        at=state['at']
        # The shared accepted fact is reusable by a separately authorized actor.
        # Each original operation token remains bound to its first actor/body.
        profile='synthetic_title_principal_operator';principal=str(uuid4())
        actor=query(f"SELECT principal_id FROM security.identity_bindings WHERE(issuer,subject)=('{issuer}','trader');")
        query(f"INSERT INTO security.principals VALUES('{principal}','human',true);"
            f"INSERT INTO security.identity_bindings VALUES('{issuer}','{profile}','{principal}','human_interactive');"
            f"INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{principal}','{profile}','Synthetic title principal operator',true,1);"
            f"INSERT INTO security.role_grants SELECT gen_random_uuid(),'{principal}',role_id,tenant_id,workspace_id,granted_from,granted_to,revoked "
            f"FROM security.role_grants WHERE principal_id='{actor}';")
        for action,selected,policy,identity,revision,current_quantity,operation,context in [
            ('receive_title',current_title,title_policy,stock,3,'0.5',title_operation,title_context),
            ('borrow',body,borrow_policy,loan,loan_revision+1,'0.5',borrow_operation,borrow_context)]:
            fresh_context=capture();before=financial();command_count=commands()
            other=record(action,selected,policy,context=fresh_context,profile=profile)
            assert not other['changed'] and other['semanticAcknowledgement'] and other['id']==identity,other
            assert other['currentFinancialRevision']==revision and Decimal(other['currentQuantity'])==Decimal(current_quantity),other
            for changed,who in [(selected,profile),(selected|dict(quantity='0.03125'),'trader')]:
                try:record(action,changed,policy,operation,context,who)
                except RuntimeError as error:assert 'gas_owned_conflict' in str(error),str(error)
                else:raise AssertionError('A retained title or principal receipt accepted another actor or payload.')
            assert financial()==before
            assert commands()==command_count+1
    # A fresh nonce still declares a certified source. Receipt recovery keeps
    # its original branch, but a new acknowledgement cannot invent that source
    # or attach an accepted fact to another certified business day.
    at=state['at']
    foreign_day=call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-21',NULL);")
    for action,selected,policy in [('receive_title',title_body,title_policy),('borrow',original,borrow_policy)]:
        for label,context in [('unknown_context',str(uuid4())),('foreign_business_day',foreign_day)]:
            before=financial();command_count=commands()
            try:result=record(action,selected,policy,context=context)
            except RuntimeError as error:
                assert 'gas_owned_context' in str(error),str(error)
                assert financial()==before and commands()==command_count
            else:
                assert not result['changed'] and result['semanticAcknowledgement'],result
                assert financial()==before and commands()==command_count+1
                failures.append(label+'_'+action)
                print('Observed fresh original-command acknowledgement with '+label+': '+action,flush=True)
    assert not failures,'Unsupported original title/principal acknowledgement: '+', '.join(failures)
    print('PASS accepted title and loan principal acknowledgements preserve all independent fixed-fee and bank facts',flush=True)
