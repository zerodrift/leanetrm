"""Positive original loan principal can mature into only independent unpaid cash."""
import json
from decimal import Decimal
from uuid import uuid4


def checks(query,manifest,state):
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    issuer=manifest['identityIssuer'];account=state['account'];at='2020-12-21T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{issuer}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-21',NULL);")
    def record(action,body,policy=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',"
            +(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;"))
    policy=query(f"SELECT policy_id FROM trading.gas_owned_action_bindings WHERE(catalog_id,account_id,action)=('{catalog}','{account}','gas.loan.repay') ORDER BY revision DESC LIMIT 1;")
    assert policy
    def repay(loan,lot,key,quantity):
        evidence=record('evidence',dict(sourceKey=key,kind='loan_return',quantity=quantity,occurredAt=at,
            evidence='Synthetic explicit full performance of surviving loan principal',expectedRevision=0))['id']
        return record('repay',dict(loanId=loan,lotId=lot,evidenceId=evidence,quantity=quantity,reservationId=None,effectiveAt=at),policy)
    # .375 gross minus the existing .25 external-stock repayment leaves exactly
    # .125 debt and .125 received stock after the separate .25 dispatch.
    repayment=repay(state['loan']['id'],state['loan']['stockId'],'synthetic_matured_loan_return','0.125')
    future=query(f"SELECT l.id FROM trading.gas_owned_loans l JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.received_lot_id) "
        "JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id) "
        f"WHERE(l.catalog_id,l.account_id,e.source_key)=('{catalog}','{account}','synthetic_future_loan_title');")
    assert future and '\n' not in future
    future_stock=query(f"SELECT received_lot_id FROM trading.gas_owned_loans WHERE(catalog_id,id)=('{catalog}','{future}');")
    repay(future,future_stock,'synthetic_matured_future_loan_return','0.4')
    assert query(f"SELECT quantity FROM trading.gas_owned_loans_current WHERE(catalog_id,id)=('{catalog}','{state['loan']['id']}');")=='0.375'
    assert query(f"SELECT quantity FROM trading.gas_owned_loans_current WHERE(catalog_id,id)=('{catalog}','{future}');")=='0.4'
    digest=query(f"SELECT md5(string_agg(value,E'\\n' ORDER BY value)) FROM("
        f"SELECT row_to_json(f)::text value FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.loan_id)=('{catalog}','{state['loan']['id']}') "
        f"UNION ALL SELECT row_to_json(p)::text FROM trading.gas_owned_fee_payments p JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(p.catalog_id,p.fee_id) "
        f"WHERE(f.catalog_id,f.loan_id)=('{catalog}','{state['loan']['id']}'))rows;")
    assert digest==state['feeBankDigest'],'Actual quantity performance changed fixed fee or bank memberships.'
    context=capture()
    held=json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_owned_holdings('{catalog}','{context}','{at}',ARRAY['{state['book']}']::uuid[])::text;"))
    assert not held['positions'] and len(held['fees'])==1 and not held['deliveryCash']
    assert Decimal(held['fees'][0]['amount'])==Decimal('-.10')
    print('PASS fully performed positive-gross loans leave no quantity, while exact original fixed fee and bank rows survive',flush=True)
    return dict(contextId=context,at=at,loanId=state['loan']['id'],stockId=state['loan']['stockId'],returnId=state['loan']['returnId'],
        bookId=state['book'],repaymentId=repayment['id'],expectedPv='-0.05',economicSession='synthetic_gas_economic')
