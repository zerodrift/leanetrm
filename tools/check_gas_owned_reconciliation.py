"""Ordinary gas title and dispatch corrections in an owned native database."""
import json
from decimal import Decimal
from uuid import uuid4

from check_reference_editing import main
from check_gas_owned_holdings import checks as owned_checks


def checks(query,manifest):
    owned_checks(query,manifest)
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    at='2020-12-20T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-20',NULL);")
    account=str(uuid4())
    def record(action,body,policy=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',"
            +(f"'{policy}',1" if policy else 'NULL,NULL')+",false)::text;"))
    original=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE catalog_id='{catalog}' AND kind='owned_inventory' ORDER BY code LIMIT 1;"))
    record('account',dict(code='synthetic_corrected_inventory',kind='owned_inventory',bookId=original['book_id'],
        legalEntityId=original['legal_entity_id'],legalEntityRevision=original['legal_entity_revision'],counterpartyId=None,counterpartyRevision=None,
        referenceCatalogId=original['reference_catalog_id'],referenceRevisionId=original['reference_revision_id'],quantity=original['quantity_code'],
        location=original['location_code'],definitionRevisionId=original['definition_revision_id'],markCurve=original['mark_curve_code'],markContractId=original['mark_contract_id'],
        currency=original['currency_code'],titlePolicy=original['title_policy'],lossPolicy=original['loss_policy'],effectiveAt='2020-12-18T00:00:00Z'))
    policies={}
    for action in ('gas.owned.receive_title','gas.owned.dispatch','gas.owned.loss'):
        policy=str(uuid4());policies[action]=policy
        call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action=action,mode='manual',
            fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
        record('bind_policy',dict(action=action,policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
    title_key=str(uuid4());title_at='2020-12-19T08:00:00Z'
    title=record('evidence',dict(sourceKey=title_key,kind='title_receipt',quantity='1',occurredAt=title_at,evidence='Synthetic observed owned title',expectedRevision=0))['id']
    stock=record('receive_title',dict(evidenceId=title,quantity='1',titleAt=title_at,availableAt=title_at,originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1'),policies['gas.owned.receive_title'])['id']
    dispatch_at='2020-12-20T12:00:00Z'
    dispatch_source=record('evidence',dict(sourceKey=str(uuid4()),kind='physical_delivery',quantity='0.25',occurredAt=dispatch_at,evidence='Synthetic observed dispatch',expectedRevision=0))['id']
    dispatch=record('dispatch',dict(lotId=stock,evidenceId=dispatch_source,quantity='0.25',reservationId=None,effectiveAt=dispatch_at),policies['gas.owned.dispatch'])['id']
    before=capture()
    amended=record('evidence',dict(sourceKey=title_key,kind='title_receipt',quantity='0.75',occurredAt=title_at,evidence='Synthetic corrected title quantity',expectedRevision=1))['id']
    context=capture()
    body=dict(positions=[dict(positionId=stock,expectedRevision=1,quantity='0.75',retracted=False,evidenceId=amended,
        sourceAllocation='preserve_unallocated_source_quantity_v1')],consumptions=[],evidence='Synthetic explicit correction of owned title')
    def read():return call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
        f"SELECT security.read_gas_owned_reconciliation('{catalog}','{account}',{literal(body)},'{context}','{at}')::text;")
    result=json.loads(read())
    assert result['implementation']=='gas_owned_quantity_reconciliation_source_v1' and result['accountId']==account
    assert result['positions'][0]['positionId']==stock and result['positions'][0]['currentRevision']==1
    assert Decimal(result['positions'][0]['currentQuantity'])==1 and result['consumptions'][0]['consumptionId']==dispatch
    assert Decimal('0.25')+Decimal('0.00000001')*16385<Decimal('0.75')
    from gas_reconciliation_bounds import check
    check(query,catalog,stock,read)
    print('PASS ordinary correction source: exact owned title and accepted dispatch, corrected raw evidence, immutable financial head and complete linked resource facts',flush=True)
    return dict(account=account,stock=stock,dispatch=dispatch,before=before,body=body,book=original['book_id'],at=at)


if __name__=='__main__':main(checks=checks)
