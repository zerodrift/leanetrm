"""Actual future-effective loan facts stay distinct from opening ownership."""
import json
from decimal import Decimal
from uuid import uuid4


def checks(query,call,catalog,account,original,lender,policy):
    at='2020-12-21T16:00:00Z';when='2020-12-21T10:00:00Z'
    def literal(body):return "'"+json.dumps(body).replace("'","''")+"'::jsonb"
    def capture(day):return call(f"SELECT security.resolve_operation_context('{catalog}','{day}',NULL);")
    def record(action,body,selected_policy):
        context=capture('2020-12-21')
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{context}',"
            +(f"'{selected_policy}',1" if selected_policy else 'NULL,NULL')+",false)::text;"))
    title=record('evidence',dict(sourceKey='synthetic_future_loan_title',kind='title_receipt',quantity='0.4',occurredAt=when,
        evidence='Synthetic future-effective accepted title',expectedRevision=0),None)['id']
    agreement=record('evidence',dict(sourceKey='synthetic_future_loan_agreement',kind='loan_agreement',quantity='0.4',occurredAt=when,
        evidence='Synthetic future-effective accepted agreement',expectedRevision=0),None)['id']
    loan=record('borrow',dict(agreementId=agreement,evidenceId=title,quantity='0.4',titleAt=when,availableAt=when,returnAt='2020-12-22T12:00:00Z',
        returnMarkCurve=original['mark_curve_code'],returnMarkContractId=original['mark_contract_id'],carrying='nominal_mark_v1',
        feeAmount='0',feeDueAt=None,lenderId=lender['id'],lenderRevision=lender['revision']),policy)
    def read(kind,day,as_of):
        context=capture(day)
        bind=f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{as_of}');"
        statement=(f"SELECT security.read_gas_owned_operations('{catalog}','{context}','{as_of}','{account}')::text;" if kind=='operations' else
            f"SELECT security.read_gas_owned_known_history('{catalog}','{context}','{as_of}',ARRAY['{original['book_id']}']::uuid[])::text;")
        return json.loads(call(bind+statement),parse_float=Decimal)
    opening=read('operations','2020-12-20','2020-12-20T16:00:00Z')
    known=read('known','2020-12-20','2020-12-20T16:00:00Z')
    actual=read('operations','2020-12-21',at)
    assert not any(row['id']==loan['id'] for row in opening['loans'])
    assert not any(row['id'] in (loan['stockId'],loan['returnId']) for row in opening['positions'])
    assert any(row['id']==loan['id'] and Decimal(row['quantity'])==Decimal('.4') for row in known['loans'])
    assert any(row['id']==loan['stockId'] and row['effective_at'].startswith('2020-12-21T10:00:00') for row in known['positions'])
    assert any(row['id']==loan['id'] and Decimal(row['quantity'])==Decimal('.4') for row in actual['loans'])
    print('PASS native future loan: original known receipt retained before effective time, no opening stock/debt, actual ownership activates only at original title time',flush=True)
