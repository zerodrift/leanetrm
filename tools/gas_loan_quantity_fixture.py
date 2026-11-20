"""A corrected title-transfer loan retains every surviving native counterpart."""
import json
import os
import subprocess
import sys
from decimal import Decimal
from pathlib import Path
from uuid import uuid4
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'tools'))
from check_gas_owned_holdings import checks as baseline
from preview_fixture import fixture as base_fixture


def fixture(root):
    reference,market,security,manifest=base_fixture(root)
    manifest['identityIssuer']='https://preview.invalid'
    return reference,market,security,manifest


def configure(query,manifest,*,form_source_counterexamples=True):
    baseline(query,manifest,form_source_counterexamples=form_source_counterexamples)
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    at='2020-12-20T16:00:00Z';issuer=manifest['identityIssuer']
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,rollback=False):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}"+('ROLLBACK;' if rollback else 'COMMIT;'))
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-20',NULL);")
    account=str(uuid4())
    def record(action,body,policy=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',"
            +(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;"))
    original=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE catalog_id='{catalog}' AND kind='owned_inventory' ORDER BY code LIMIT 1;"))
    book=str(uuid4())
    query(f"INSERT INTO trading.books SELECT catalog_id,'{book}',portfolio_id,'Synthetic corrected loan',publication_order,effective_at "
        f"FROM trading.books WHERE(catalog_id,id)=('{catalog}','{original['book_id']}');")
    original['book_id']=book
    record('account',dict(code='synthetic_corrected_loan',kind='owned_inventory',bookId=original['book_id'],legalEntityId=original['legal_entity_id'],
        legalEntityRevision=original['legal_entity_revision'],counterpartyId=None,counterpartyRevision=None,referenceCatalogId=original['reference_catalog_id'],
        referenceRevisionId=original['reference_revision_id'],quantity=original['quantity_code'],location=original['location_code'],
        definitionRevisionId=original['definition_revision_id'],markCurve=original['mark_curve_code'],markContractId=original['mark_contract_id'],
        currency=original['currency_code'],titlePolicy=original['title_policy'],lossPolicy=original['loss_policy'],effectiveAt='2020-12-18T00:00:00Z'))
    policies={}
    for action in ('gas.owned.receive_title','gas.owned.dispatch','gas.loan.borrow','gas.loan.repay','gas.cash.record_payment'):
        policy=str(uuid4());policies[action]=policy
        policy_body=dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action=action,mode='manual',
            fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(policy_body)});")
        record('bind_policy',dict(action=action,policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    def evidence(key,kind,quantity,when,revision=0):return record('evidence',dict(sourceKey=key,kind=kind,quantity=quantity,occurredAt=when,
        evidence='Synthetic observed loan counterpart',expectedRevision=revision))['id']
    supply=evidence('synthetic_loan_return_funding','title_receipt','0.25','2020-12-19T08:00:00Z')
    funding=record('receive_title',dict(evidenceId=supply,quantity='0.25',titleAt='2020-12-19T08:00:00Z',availableAt='2020-12-19T08:00:00Z',
        originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'),policies['gas.owned.receive_title'])['id']
    when='2020-12-20T10:00:00Z';title=evidence('synthetic_loan_title','title_receipt','1',when);agreement=evidence('synthetic_loan_agreement','loan_agreement','1',when)
    lender=json.loads(query(f"SELECT jsonb_build_object('id',lender_id,'revision',lender_revision) FROM trading.gas_owned_loans WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;"))
    loan=record('borrow',dict(agreementId=agreement,evidenceId=title,quantity='1',titleAt=when,availableAt=when,returnAt='2020-12-21T12:00:00Z',
        returnMarkCurve=original['mark_curve_code'],returnMarkContractId=original['mark_contract_id'],carrying='nominal_mark_v1',feeAmount='-0.10',
        feeDueAt='2020-12-21T12:00:00Z',lenderId=lender['id'],lenderRevision=lender['revision']),policies['gas.loan.borrow'])
    dispatched=evidence('synthetic_loan_dispatch','physical_delivery','0.25','2020-12-20T11:00:00Z')
    dispatch=record('dispatch',dict(lotId=loan['stockId'],evidenceId=dispatched,quantity='0.25',reservationId=None,effectiveAt='2020-12-20T11:00:00Z'),policies['gas.owned.dispatch'])['id']
    returned=evidence('synthetic_loan_repayment','loan_return','0.25','2020-12-20T12:00:00Z')
    repayment=record('repay',dict(loanId=loan['id'],lotId=funding,evidenceId=returned,quantity='0.25',reservationId=None,effectiveAt='2020-12-20T12:00:00Z'),policies['gas.loan.repay'])['id']
    fee=query(f"SELECT id FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{loan['id']}');")
    payment=record('fee_payment',dict(feeId=fee,businessKey='synthetic_loan_bank',expectedRevision=0,paidAt='2020-12-20T14:00:00Z',amount='-0.05',
        evidence='Synthetic observed bank allocation'),policies['gas.cash.record_payment'])['id']
    before=capture()
    title_head=evidence('synthetic_loan_title','title_receipt','0.75',when,1)
    agreement_head=evidence('synthetic_loan_agreement','loan_agreement','0.75',when,1)
    context=capture();bound=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
    source=json.loads(call(bound+f"SELECT security.read_gas_loan_reconciliation('{catalog}','{loan['id']}','{context}','{at}')::text;"))
    assert source['implementation']=='gas_loan_quantity_reconciliation_source_v1' and source['accountId']==account and source['loanId']==loan['id']
    assert source['loan']['expectedRevision']==1 and Decimal(source['loan']['quantity'])==1
    assert source['stock']['positionId']==loan['stockId'] and source['return']['positionId']==loan['returnId']
    assert Decimal(source['stock']['quantity'])==1 and Decimal(source['return']['quantity'])==-1
    assert {title_head,agreement_head}.issubset({row['id'] for row in source['evidence']})
    assert any(row['id']==dispatch for row in source['consumptions']) and any(row['id']==repayment for row in source['repayments'])
    assert any(row['id']==payment and Decimal(row['amount'])==Decimal('-0.05') for row in source['payments'])
    print('PASS corrected loan source retains exact stock, debt, dispatch, linked repayment and separate fee/bank counterparts without changing accepted quantities',flush=True)
    signatures=['trading.check_gas_owned_integrity()','trading.check_gas_quantity_revision()',
        'security.read_gas_owned_operations(uuid,uuid,timestamptz,uuid)',
        'security.read_gas_owned_known_history_pre_risk(uuid,uuid,timestamptz,uuid[])',
        'security.gas_physical_journal_source(uuid,uuid,timestamptz,uuid[],uuid[])',
        'trading.apply_gas_owned_financial(uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer)']
    digest=query("SELECT jsonb_object_agg(p.oid::regprocedure::text,md5(p.prosrc)) FROM pg_proc p WHERE p.oid=ANY(ARRAY["
        +','.join("'"+name+"'::regprocedure" for name in signatures)+"]);")
    print('loan predecessor source hashes '+digest,flush=True)
    policy=str(uuid4())
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,
        action='gas.loan.reconcile_quantity',mode='manual',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',
        implementation='explicit_lifecycle_action_policy_v1'))});")
    record('bind_policy',dict(action='gas.loan.reconcile_quantity',policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    context=capture();operation=str(uuid4())
    correction=dict(loanId=loan['id'],expectedRevision=1,stockExpectedRevision=1,returnExpectedRevision=1,quantity='0.75',
        titleEvidenceId=title_head,agreementEvidenceId=agreement_head,sourceAllocation='preserve_unallocated_source_quantity_v1',
        feeDisposition='retain_fixed_fee_memberships_v1',consumptions=[],evidence='Synthetic reviewed loan quantity correction')
    cash_before=query(f"""SELECT jsonb_build_object('fees',(SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id) FROM trading.gas_owned_fees f WHERE(catalog_id,loan_id)=('{catalog}','{loan['id']}')),
        'payments',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM trading.gas_owned_fee_payments p WHERE(catalog_id,fee_id)=('{catalog}','{fee}')));""")
    publication_before=int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))
    corrected=json.loads(call(f"SELECT security.reconcile_gas_loan_quantities('{catalog}','{operation}','{account}',{literal(correction)},'{at}','{context}','{policy}',1,false)::text;"))
    assert corrected['changed'] and corrected['currentRevision']==2 and Decimal(corrected['currentQuantity'])==Decimal('.75')
    assert corrected['publicationOrder']==publication_before+1 and len(corrected['sourceAllocations'])==2
    after=capture()
    def state(cutoff):
        bind=f"SELECT security.use_market_context('{catalog}','{cutoff}');SELECT security.use_trading_context('{catalog}','{at}');"
        return json.loads(call(bind+f"SELECT security.read_gas_owned_operations('{catalog}','{cutoff}','{at}','{account}')::text;"))
    old,current=state(before),state(after)
    assert Decimal(next(row['quantity'] for row in old['loans'] if row['id']==loan['id']))==1
    assert Decimal(next(row['quantity'] for row in current['loans'] if row['id']==loan['id']))==Decimal('.75')
    positions={row['id']:row for row in current['positions']}
    assert Decimal(positions[loan['stockId']]['quantity'])==Decimal('.75') and Decimal(positions[loan['returnId']]['quantity'])==Decimal('-.75')
    uses={row['id']:row for row in current['consumptions']}
    assert Decimal(uses[dispatch]['quantity'])==Decimal('.25') and Decimal(uses[next(row['consumption_id'] for row in current['repayments'] if row['id']==repayment)]['quantity'])==Decimal('.25')
    cash_after=query(f"""SELECT jsonb_build_object('fees',(SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id) FROM trading.gas_owned_fees f WHERE(catalog_id,loan_id)=('{catalog}','{loan['id']}')),
        'payments',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM trading.gas_owned_fee_payments p WHERE(catalog_id,fee_id)=('{catalog}','{fee}')));""")
    assert cash_before==cash_after,'loan correction rewrote a fixed fee or bank allocation'
    retry=json.loads(call(f"SELECT security.reconcile_gas_loan_quantities('{catalog}','{operation}','{account}',{literal(correction)},'{at}','{context}','{policy}',1,false)::text;"))
    assert retry==corrected
    acknowledgement=json.loads(call(f"SELECT security.reconcile_gas_loan_quantities('{catalog}','{uuid4()}','{account}',{literal(correction)},'{at}','{context}','{policy}',1,false)::text;"))
    assert acknowledgement['semanticAcknowledgement'] and not acknowledgement['changed'] and acknowledgement['publicationOrder'] is None
    assert acknowledgement['acknowledgedRevision']==acknowledgement['currentRevision']==2
    assert int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))==publication_before+1
    bound=f"SELECT security.use_market_context('{catalog}','{after}');SELECT security.use_trading_context('{catalog}','{at}');"
    holdings=json.loads(call(bound+f"SELECT security.read_gas_owned_holdings('{catalog}','{after}','{at}',ARRAY['{original['book_id']}']::uuid[])::text;"))
    held={row['id']:Decimal(row['remaining_quantity']) for row in holdings['positions']}
    assert held[loan['stockId']]==Decimal('.5') and held[loan['returnId']]==Decimal('-.5')
    known=json.loads(call(bound+f"SELECT security.read_gas_owned_known_history('{catalog}','{after}','{at}',ARRAY['{original['book_id']}']::uuid[])::text;"))
    assert Decimal(next(row['quantity'] for row in known['loans'] if row['id']==loan['id']))==Decimal('.75')
    print('PASS corrected loan 1→.75 retains stock.50 and return debt.50 after distinct .25 dispatch/repayment; fixed fee/bank history, historical heads, one publication and first outcomes remain exact',flush=True)
    from gas_loan_reconciliation_cases import checks as cases
    cases(query,call,record,evidence,capture,dict(account=account,loan=loan,policy=policy,at=at,
        originalTitle=title,originalAgreement=agreement,original=corrected,originalCommand=correction,originalContext=context,originalOperation=operation),manifest)
    from gas_loan_reconciliation_future import checks as future_checks
    future_checks(query,call,catalog,account,original,lender,policies['gas.loan.borrow'])
    title_managed=evidence('synthetic_loan_title','title_receipt','0.375',when,4)
    agreement_managed=evidence('synthetic_loan_agreement','loan_agreement','0.375',when,4)
    selected=correction|dict(expectedRevision=3,stockExpectedRevision=3,returnExpectedRevision=3,quantity='0.375',
        titleEvidenceId=title_managed,agreementEvidenceId=agreement_managed)
    host=query('SHOW unix_socket_directories;');path=Path(host)/'gas-loan-correction.json'
    managed=dict(manifest=manifest,host=host,at=at,contextId=capture(),operationId=str(uuid4()),
        accountId=account,loanId=loan['id'],policyId=policy,selection=selected,expectedQuantity='0.375')
    path.write_text(json.dumps(managed))
    subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--project','tests/OpenEtrm.Api.Checks','--no-build','--no-restore','--',
        '--gas-loan-correction',str(path)],check=True,cwd=Path(__file__).resolve().parents[1])
    from native_read_transport import captured_input
    admitted=captured_input(query,managed['contextId'])
    assert query(f"SELECT count(*)=2 AND bool_and((source_business_on,source_known_at,source_system_id,source_timeline)="
        f"('{admitted['effective_on']}'::date,'{admitted['known_at']}'::timestamptz,'{admitted['system_id']}',{admitted['timeline']})) "
        f"FROM trading.gas_loan_quantity_corrections WHERE(catalog_id,loan_id,source_context_id)=('{catalog}','{loan['id']}','{managed['contextId']}');")=='t', \
        'Only the first managed correction and its proved acknowledgement may own complete original inputs; failed or retried calls add no row.'
    current_context=capture()
    source=json.loads(call(f"SELECT security.use_market_context('{catalog}','{current_context}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_loan_reconciliation('{catalog}','{loan['id']}','{current_context}','{at}')::text;"))
    fee_bank_digest=query(f"SELECT md5(string_agg(value,E'\\n' ORDER BY value)) FROM("
        f"SELECT row_to_json(f)::text value FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.loan_id)=('{catalog}','{loan['id']}') "
        f"UNION ALL SELECT row_to_json(p)::text FROM trading.gas_owned_fee_payments p JOIN trading.gas_owned_fees f "
        f"ON(f.catalog_id,f.id)=(p.catalog_id,p.fee_id) WHERE(f.catalog_id,f.loan_id)=('{catalog}','{loan['id']}'))rows;")
    return dict(account=account,loan=loan,context=context,before=before,dispatch=dispatch,repayment=repayment,fee=fee,payment=payment,
        title=title_head,agreement=agreement_head,at=at,funding=funding,policy=policy,book=book,dispatchPolicy=policies['gas.owned.dispatch'],
        original=original,currentContextId=current_context,source=source,feeBankDigest=fee_bank_digest)
