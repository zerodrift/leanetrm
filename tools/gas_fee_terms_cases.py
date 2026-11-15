"""Independent signed cash examples for the fixed borrower-fee amendment law."""
import json
import os
import subprocess
from decimal import Decimal
from pathlib import Path
from uuid import uuid4


def amendments(query,call,capture,record,read,manifest,fee,zero,at):
    catalog=manifest['catalogId'];account=fee['accountId'];loan=fee['loan_id']
    assert query("SELECT count(*) FROM lifecycle.action_kinds WHERE code='gas.loan.amend_fee' AND NOT automatic "
        "AND configuration_permission='ConfigureGasOwnedHoldings' AND execution_permission='RecordGasOwnedHoldings';")=='1', 'The explicit manual loan-fee amendment is not registered.'
    def literal(body):return "'"+json.dumps(body).replace("'","''")+"'::jsonb"
    policy=str(uuid4())
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action='gas.loan.amend_fee',mode='manual',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
    record('bind_policy',dict(action='gas.loan.amend_fee',policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    context=capture();operation=str(uuid4())
    body=dict(loanId=loan,expectedRevision=1,amount='-2.4',dueAt='2020-12-22T12:00:00Z',effectiveAt='2020-12-20T15:30:00Z',
        implementation='gross_timed_fixed_fee_adjustments_v1',evidence='Synthetic revised fixed borrower fee')
    def command(selection,identity=operation,selected_context=context):
        return f"SELECT security.amend_gas_loan_fee_terms('{catalog}','{identity}','{account}',{literal(selection)},'{at}','{selected_context}','{policy}',1,false)::text;"
    original=query(f"SELECT to_jsonb(f) FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.id)=('{catalog}','{fee['id']}');")
    bank=query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE p.catalog_id='{catalog}';")
    before=int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))
    accepted=json.loads(call(command(body)),parse_float=Decimal)
    assert accepted['changed'] and accepted['currentRevision']==2 and Decimal(accepted['currentAmount'])==Decimal('-2.4')
    assert accepted['publicationOrder']==before+1
    changes=accepted['cashChanges']
    assert [(c['kind'],Decimal(c['amount']),c['dueAt'][:10]) for c in changes]==[
        ('reverse_prior_fee',Decimal('2'),'2020-12-21'),('revised_fee',Decimal('-2.4'),'2020-12-22')]
    assert query(f"SELECT to_jsonb(f) FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.id)=('{catalog}','{fee['id']}');")==original
    assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE p.catalog_id='{catalog}';")==bank
    assert Decimal(query(f"SELECT sum(amount) FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{loan}');"))==Decimal('-2.4')
    assert json.loads(call(command(body)),parse_float=Decimal)==accepted
    ack=json.loads(call(command(body,str(uuid4()),capture())))
    assert not ack['changed'] and ack['semanticAcknowledgement'] and ack['acknowledgedRevision']==2
    assert int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))==before+1
    source=read(loan)
    assert source['terms']['revision']==2 and len(source['cash'])==3 and len(source['payments'])==2
    assert Decimal(source['currentPaid'])==Decimal('-.5')
    print('PASS fixed loan fee amendment: explicit gross reversal and revised due cash, unchanged original and bank, one publication and first outcomes',flush=True)
    zero_charge=json.loads(call(command({**body,'expectedRevision':2,'amount':'0','dueAt':None,
        'effectiveAt':'2020-12-20T15:40:00Z'},str(uuid4()),capture())+'SET CONSTRAINTS ALL IMMEDIATE;',rollback=True))
    assert zero_charge['changed'] and zero_charge['currentRevision']==3 and Decimal(zero_charge['currentAmount'])==0
    assert [(c['kind'],Decimal(c['amount']),c['dueAt'][:10]) for c in zero_charge['cashChanges']]==[
        ('reverse_prior_fee',Decimal('2.4'),'2020-12-22')]
    assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE p.catalog_id='{catalog}';")==bank
    print('PASS zero fee amendment passes immediate native validation with only the explicit dated contractual credit',flush=True)
    from gas_fee_seal_cases import checks as seals
    seals(query,call,command,body,capture)
    from gas_fee_adversarial_cases import checks as adversarial
    adversarial(query,call,command,body,capture,manifest,fee,at)
    from gas_fee_read_authority import checks as read_authority
    read_authority(query,call,capture,manifest,loan,at)
    host=Path(query('SHOW unix_socket_directories;'));path=host/'gas-fixed-fee-managed.json'
    path.write_text(json.dumps(dict(manifest=manifest,host=str(host),at=at,accountId=account,loanId=loan,contextId=capture(),markId='70000000-0000-0000-0000-000000000001',
        policyId=policy,operationId=str(uuid4()),selection={**body,'expectedRevision':2,'amount':'-3',
            'dueAt':'2020-12-23T12:00:00Z','effectiveAt':'2020-12-20T15:45:00Z'})))
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
        '--','--gas-loan-fee',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=120)
    print(result.stdout,flush=True)
    if result.returncode:raise RuntimeError(result.stderr)
    removed=json.loads(call(command({**body,'expectedRevision':3,'amount':'0','dueAt':None,
        'effectiveAt':'2020-12-20T15:50:00Z'},str(uuid4()),capture())))
    assert removed['changed'] and removed['currentRevision']==4 and Decimal(removed['currentAmount'])==0
    assert [(c['kind'],Decimal(c['amount']),c['dueAt'][:10]) for c in removed['cashChanges']]==[
        ('reverse_prior_fee',Decimal('3'),'2020-12-23')]
    assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_owned_fee_payments p WHERE p.catalog_id='{catalog}';")==bank
    assert Decimal(query(f"SELECT sum(amount) FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{loan}');"))==0
    retired=read(loan)
    assert retired['terms']['revision']==4 and retired['terms']['dueAt'] is None and Decimal(retired['currentPaid'])==Decimal('-.5')
    assert len(retired['payments'])==2 and Decimal(retired['terms']['amount'])-Decimal(retired['currentPaid'])==Decimal('.5')
    print('PASS committed zero fee retains every original bank revision and an unpaid refund receivable; no refund payment is inferred',flush=True)
    result=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks',
        '--','--gas-loan-fee-zero-pv',str(path)],cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=120)
    print(result.stdout,flush=True)
    if result.returncode:raise RuntimeError(result.stderr)
    from gas_fee_timed_cases import checks as timed_cases
    timed_cases(query,call,capture,record,read,manifest,fee,policy,at)
    reference=query(f"SELECT e.source_key FROM trading.gas_owned_loans l JOIN trading.gas_owned_positions_current p ON(p.catalog_id,p.id)=(l.catalog_id,l.return_position_id) "
        f"JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id) WHERE(l.catalog_id,l.id)=('{catalog}','{loan}');")
    return dict(accountId=account,loanId=loan,policyId=policy,policyRevision=1,at=at,contextId=capture(),currentRevision=4,
        originalFeeId=fee['id'],originalFeeJson=original,bankHash=bank,reference=reference)
