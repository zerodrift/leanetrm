"""Retired principal can leave a fee-only account without a quantity profile."""
import json
from decimal import Decimal
from uuid import uuid4


def checks(query,call,state,manifest):
    catalog,account=manifest['catalogId'],state['account']
    at='2020-12-21T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-21',NULL);")
    def record(action,body,policy=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',"
            +(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;"))
    # The preceding independent future-activation check creates this exact
    # native loan. Retire it explicitly too; it cannot disappear from the test
    # merely because it was future-effective at the earlier opening.
    future=query(f"SELECT l.id FROM trading.gas_owned_loans l JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.received_lot_id) "
        "JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id) "
        f"WHERE(l.catalog_id,l.account_id,e.source_key)=('{catalog}','{account}','synthetic_future_loan_title');")
    assert future and '\n' not in future
    context=capture()
    source=json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_loan_reconciliation('{catalog}','{future}','{context}','{at}')::text;"))
    evidence={row['id']:row for row in source['evidence']}
    def withdraw(identifier):
        original=evidence[identifier]
        head=max((row for row in source['evidence'] if row['source_key']==original['source_key']),key=lambda row:row['revision'])
        return record('evidence',dict(sourceKey=head['source_key'],kind=head['kind'],quantity='0',
            occurredAt=head['occurred_at'].replace('+00:00','Z'),evidence='Synthetic complete withdrawal of observed future loan',expectedRevision=head['revision']))['id']
    title=withdraw(source['loan']['title_evidence_id']);agreement=withdraw(source['loan']['agreement_evidence_id'])
    body=dict(loanId=future,expectedRevision=source['loan']['expectedRevision'],stockExpectedRevision=source['stock']['financialRevision'],
        returnExpectedRevision=source['return']['financialRevision'],quantity='0',titleEvidenceId=title,agreementEvidenceId=agreement,
        sourceAllocation='withdraw_all_source_allocations_v1',feeDisposition='retain_fixed_fee_memberships_v1',consumptions=[],
        evidence='Synthetic explicit withdrawal before fee-only economic projection')
    result=json.loads(call(f"SELECT security.reconcile_gas_loan_quantities('{catalog}','{uuid4()}','{account}',{literal(body)},'{at}','{capture()}','{state['policy']}',1,false)::text;"))
    assert result['changed'] and Decimal(result['currentQuantity'])==0
    # The separate .25 funding stock was restored by the original repayment
    # retraction. Record its explicit subsequent delivery; do not drop it from
    # the economic scope or invent an offsetting cash receipt.
    delivered=record('evidence',dict(sourceKey='synthetic_fee_only_funding_delivery',kind='physical_delivery',quantity='0.25',
        occurredAt=at,evidence='Synthetic observed delivery of independently owned funding stock',expectedRevision=0))['id']
    record('dispatch',dict(lotId=state['funding'],evidenceId=delivered,quantity='0.25',reservationId=None,effectiveAt=at),state['dispatchPolicy'])
    context=capture()
    held=json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_owned_holdings('{catalog}','{context}','{at}',ARRAY['{state['book']}']::uuid[])::text;"))
    assert not held['positions'] and len(held['fees'])==1 and not held['deliveryCash']
    assert held['fees'][0]['loan_id']==state['loan']['id'] and Decimal(held['fees'][0]['amount'])==Decimal('-.10')
    print('PASS fee-only native source: all known principal retired, independent stock explicitly delivered, original fee and bank retained',flush=True)
    return dict(contextId=context,at=at,bookId=state['book'],expectedPv='-0.05',
        economicSession='synthetic_gas_economic',feeOnly=True)
