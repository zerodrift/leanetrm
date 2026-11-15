"""Independent zero-origin, overdue and due-only fixed-fee examples."""
from decimal import Decimal
import json
from uuid import uuid4

from gas_fee_adversarial_cases import _literal, _refused, _state


def checks(query,call,capture,record,read,manifest,fee,policy,at):
    catalog=manifest['catalogId'];account=fee['accountId'];title_at='2020-12-20T15:00:00Z'
    def evidence(key,kind):
        return record('evidence',dict(sourceKey=key,kind=kind,quantity='1',occurredAt=title_at,
            evidence='Synthetic independently observed timed-fee example',expectedRevision=0))['id']
    title=evidence('synthetic_timed_fee_title','title_receipt')
    agreement=evidence('synthetic_timed_fee_agreement','loan_agreement')
    borrowing=json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) "
        f"FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)=('{catalog}','{account}','gas.loan.borrow') ORDER BY revision DESC LIMIT 1;"))
    loan=record('borrow',dict(agreementId=agreement,evidenceId=title,quantity='1',titleAt=title_at,availableAt=title_at,
        returnAt='2020-12-22T12:00:00Z',returnMarkCurve='future',returnMarkContractId='50000000-0000-0000-0000-000000000001',
        carrying='nominal_mark_v1',feeAmount='0',feeDueAt=None,lenderId=fee['lenderId'],lenderRevision=fee['lenderRevision']),
        borrowing['id'],borrowing['revision'])['id']
    assert read(loan)['rootFee'] is None
    def command(body,context):
        return f"SELECT security.amend_gas_loan_fee_terms('{catalog}','{uuid4()}','{account}',{_literal(body)},'{at}','{context}','{policy}',1,false)::text;"
    body=dict(loanId=loan,expectedRevision=1,amount='-1.25',dueAt='2020-12-19T12:00:00Z',effectiveAt='2020-12-20T15:51:00Z',
        implementation='gross_timed_fixed_fee_adjustments_v1',evidence='Synthetic explicitly overdue contractual fee')
    first=json.loads(call(command(body,capture())),parse_float=Decimal)
    assert first['currentRevision']==2 and [(p['kind'],Decimal(p['amount']),p['dueAt'][:10]) for p in first['cashChanges']]==[
        ('revised_fee',Decimal('-1.25'),'2020-12-19')]
    due_only={**body,'expectedRevision':2,'dueAt':'2020-12-25T12:00:00Z','effectiveAt':'2020-12-20T15:52:00Z'}
    moved=json.loads(call(command(due_only,capture())),parse_float=Decimal)
    assert moved['currentRevision']==3 and Decimal(moved['currentAmount'])==Decimal('-1.25')
    assert [(p['kind'],Decimal(p['amount']),p['dueAt'][:10]) for p in moved['cashChanges']]==[
        ('reverse_prior_fee',Decimal('1.25'),'2020-12-19'),('revised_fee',Decimal('-1.25'),'2020-12-25')]
    assert sum(Decimal(p['amount']) for p in moved['cashChanges'])==0
    corrected={**due_only,'expectedRevision':3,'amount':'-1.375'}
    context=capture();before=_state(query,catalog)
    for invalid in ({**corrected,'amount':'0.125'},{**corrected,'dueAt':None},
        {**corrected,'amount':'0'},{**corrected,'implementation':'synthetic_unknown_adjustment'}):
        _refused(call,command(invalid,context),'gas_fee_terms_input')
        assert _state(query,catalog)==before
    _refused(call,command({**corrected,'effectiveAt':'2020-12-20T15:51:00Z'},context),'gas_fee_terms_revision')
    assert _state(query,catalog)==before
    later=json.loads(call(command(corrected,capture())),parse_float=Decimal)
    assert later['currentRevision']==4 and Decimal(later['currentAmount'])==Decimal('-1.375')
    initial={**body,'amount':'0','dueAt':None,'effectiveAt':title_at}
    ack=json.loads(call(command(initial,capture())),parse_float=Decimal)
    assert not ack['changed'] and ack['acknowledgedRevision']==1 and ack['currentRevision']==4 and ack['cashChanges']==[]
    final=json.loads(call(command({**corrected,'expectedRevision':4,'amount':'0','dueAt':None,
        'effectiveAt':'2020-12-20T15:53:00Z'},capture())),parse_float=Decimal)
    assert final['currentRevision']==5 and [(p['kind'],Decimal(p['amount']),p['dueAt'][:10]) for p in final['cashChanges']]==[
        ('reverse_prior_fee',Decimal('1.375'),'2020-12-25')]
    source=read(loan)
    assert source['rootFee'] is None and source['terms']['revision']==5 and len(source['cash'])==6
    assert sum(Decimal(p['amount']) for p in source['cash'])==0 and source['payments']==[] and Decimal(source['currentPaid'])==0
    print('PASS timed fixed fee: zero origin, explicitly overdue charge, same-amount due change retains both dated gross legs, same-clock revision, old zero acknowledgement without rewind and explicit zero without bank effects',flush=True)
