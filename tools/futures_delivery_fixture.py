"""Explicit saved physical continuation terms for isolated synthetic consumers."""
import json
from uuid import uuid4


def configure(query,manifest,call,capture,*,curve,contract,book,entity,entity_revision,mark_contract,account_code,
              account_effective_at,as_of,delivery_from,delivery_to,cash_due,carrying,policy_from,policy_until):
    catalog=manifest['catalogId']
    def lit(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    source=json.loads(query("SELECT jsonb_build_object('quantity',q.quantity_code,'currency',q.currency_code,'location',m.location_code) "
        "FROM market_data.contracts c JOIN market_data.curve_members cm ON(cm.catalog_id,cm.revision_id,cm.contract_id)=(c.catalog_id,c.revision_id,c.id) "
        "JOIN market_data.curves m ON(m.catalog_id,m.revision_id,m.code)=(cm.catalog_id,cm.revision_id,cm.curve_code) "
        "JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.id,m.code)=('{catalog}','{manifest['revisionId']}','{contract}','{curve}');"))
    account=str(uuid4())
    terms=dict(code=account_code,kind='owned_inventory',bookId=book,legalEntityId=entity,legalEntityRevision=entity_revision,
        counterpartyId=None,counterpartyRevision=None,referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],
        definitionRevisionId=manifest['revisionId'],markCurve=curve,markContractId=mark_contract,quantity=source['quantity'],currency=source['currency'],
        location=source['location'],titlePolicy='explicit_accepted_title_v1',lossPolicy='separate_evidenced_loss_v1',effectiveAt=account_effective_at)
    call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','account','{account}',{lit(terms)},'{as_of}','{capture()}',NULL,NULL,false);")
    policy=dict(operationId=str(uuid4()),policyId=str(uuid4()),expectedRevision=0,action='gas.futures.delivery',mode='manual',
        fromAt=policy_from,untilAt=policy_until,implementation='explicit_lifecycle_action_policy_v1')
    call(f"SELECT lifecycle.save_policy('{catalog}',{lit(policy)});")
    profile=dict(id=str(uuid4()),name='Synthetic gas delivery',implementation='gas_future_physical_delivery_v1',definitionRevisionId=manifest['revisionId'],
        contractId=contract,accountId=account,fromAt=delivery_from,toAt=delivery_to,cashDueAt=cash_due,carrying=carrying,
        principalIncrement='0.01',principalTie='to_even',principalScope='delivery_cumulative_principal_total_v1',
        correctionDuePolicy='contractual_principal_due_v1',actionPolicyId=policy['policyId'],actionPolicyRevision=1)
    request=dict(operationId=str(uuid4()),expectedRevision=0,contextId=capture(),asOf=as_of,profile=profile)
    saved=json.loads(call(f"SELECT settlement_data.save_futures_delivery_profile('{catalog}',{lit(request)})::text;"))
    assert saved['revision']==1
    return dict(accountId=account,accountTerms=terms,actionPolicy=policy,profile=profile,request=request)
