"""Qualify actual native loan origins across migration 0308.

The current fee source, managed actions and browser qualify separately on the
complete fresh schema; this historical probe does not cross the reset boundary.
"""
from decimal import Decimal
import json
from pathlib import Path
import re
from uuid import uuid4

import check_gas_fee_terms as fees
from check_reference_editing import main as native_main
from seed_fixture_market import load_fixture
from preview_fixture import trading_fixture,catalogue_fixture,trade_booking
from assistant_context import Contexts


def _literal(value):
    return "'"+json.dumps(value).replace("'","''")+"'::jsonb"


def _tables(query):
    names=json.loads(query("SELECT coalesce(jsonb_agg(c.relname ORDER BY c.relname),'[]') FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
        "WHERE n.nspname='trading' AND c.relkind='r' AND c.relname LIKE 'gas\\_%' ESCAPE '\\' "
        "AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped);"))
    assert names and all(re.fullmatch(r'gas_[a-z0-9_]+',name) for name in names)
    return [*names,'publications','knowledge_receipts']


def _state(query,catalog,tables):
    return query('SELECT jsonb_object_agg(name,digest ORDER BY name) FROM('+' UNION ALL '.join(
        "SELECT '"+table+"'::text name,(SELECT md5(coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),'[]')::text) "
        f"FROM trading.{table} x WHERE x.catalog_id='{catalog}') digest" for table in tables)+')states;')


def _record(query,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    at='2020-12-20T16:00:00Z'
    def call(statement):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','{at[:10]}',NULL);")
    def record(account,action,body,policy=None,revision=None):
        operation=str(uuid4());context=capture()
        result=json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{operation}','{action}','{account}',{_literal(body)},'{at}','{context}',"
            +(f"'{policy}',{revision}" if policy else 'NULL,NULL')+",false)::text;"))
        return operation,result
    return record



def _seed_nonzero(query,manifest):
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    query(trading_fixture(catalog))
    query(f"SELECT security.initialize_operating_policies('{tenant}','{workspace}');")
    query(catalogue_fixture(manifest,manifest['identityIssuer']))
    query("INSERT INTO security.role_permissions SELECT role_id,p FROM security.role_permissions CROSS JOIN "
        "unnest(ARRAY['ReadGasOwnedHoldings','RecordGasOwnedHoldings','ConfigureGasOwnedHoldings'])p WHERE permission='AmendTrade' ON CONFLICT DO NOTHING;")
    parties=trade_booking();entity=parties['legalEntityId'];party=parties['counterpartyId']
    terms=json.loads(query("SELECT jsonb_build_object('quantity',q.quantity_code,'currency',q.currency_code,'location',c.location_code) "
        "FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)="
        "(c.reference_catalog_id,c.reference_revision_id,c.quote_code) "
        f"WHERE(c.catalog_id,c.revision_id,c.code)=('{catalog}','{manifest['revisionId']}','future');"))
    record=_record(query,manifest);account=str(uuid4());contract='50000000-0000-0000-0000-000000000001'
    record(account,'account',dict(code='synthetic_owned_inventory_1',kind='owned_inventory',bookId='d1000000-0000-0000-0000-000000000001',
        legalEntityId=entity,legalEntityRevision=parties['legalEntityRevision'],counterpartyId=None,counterpartyRevision=None,
        referenceCatalogId=manifest['referenceCatalogId'],referenceRevisionId=manifest['referenceRevisionId'],definitionRevisionId=manifest['revisionId'],
        markCurve='future',markContractId=contract,titlePolicy='explicit_accepted_title_v1',lossPolicy='separate_evidenced_loss_v1',effectiveAt='2020-12-18T00:00:00Z',**terms))
    policies={}
    for action in ('gas.loan.borrow','gas.cash.record_payment'):
        policy=str(uuid4())
        request=dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action=action,mode='manual',fromAt='2020-01-01T00:00:00Z',untilAt='2022-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1')
        Contexts(query,manifest).runtime('trader',f"SELECT lifecycle.save_policy('{catalog}',{_literal(request)});")
        record(account,'bind_policy',dict(action=action,policyId=policy,policyRevision=1,effectiveAt='2020-12-18T00:00:00Z'))
        policies[action]=policy
    evidence={}
    for kind in ('loan_agreement','title_receipt'):
        _,created=record(account,'evidence',dict(sourceKey='synthetic_origin_'+kind,kind=kind,quantity='100',occurredAt='2020-12-20T10:00:00Z',evidence='Synthetic accepted loan origin',expectedRevision=0))
        evidence[kind]=created['id']
    body=dict(agreementId=evidence['loan_agreement'],evidenceId=evidence['title_receipt'],quantity='100',titleAt='2020-12-20T10:00:00Z',availableAt='2020-12-20T10:00:00Z',
        returnAt='2020-12-21T12:00:00Z',returnMarkCurve='future',returnMarkContractId=contract,carrying='nominal_mark_v1',feeAmount='-2',feeDueAt='2020-12-21T12:00:00Z',lenderId=party,lenderRevision=parties['counterpartyRevision'])
    _,loan=record(account,'borrow',body,policies['gas.loan.borrow'],1)
    fee=query(f"SELECT id FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{loan['id']}');")
    payment=dict(feeId=fee,businessKey='synthetic_origin_payment',expectedRevision=0,paidAt='2020-12-20T14:00:00Z',amount='-1',evidence='Synthetic bank receipt')
    record(account,'fee_payment',payment,policies['gas.cash.record_payment'],1)
    record(account,'fee_payment',{**payment,'expectedRevision':1,'amount':'-0.5'},policies['gas.cash.record_payment'],1)


def _seed_zero(query,manifest):
    catalog=manifest['catalogId'];record=_record(query,manifest)
    source=json.loads(query("SELECT jsonb_build_object('account',l.account_id,'lender',l.lender_id,'revision',l.lender_revision) "
        "FROM trading.gas_owned_loans l JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(l.catalog_id,l.account_id) "
        f"WHERE l.catalog_id='{catalog}' AND a.code='synthetic_owned_inventory_1' AND l.quantity=100;"))
    account=source['account'];when='2020-12-20T15:00:00Z'
    def evidence(key,kind):
        _,outcome=record(account,'evidence',dict(sourceKey=key,kind=kind,quantity='1',occurredAt=when,
            evidence='Synthetic independently observed zero-fee loan',expectedRevision=0))
        return outcome['id']
    title=evidence('synthetic_zero_fee_title','title_receipt');agreement=evidence('synthetic_zero_fee_agreement','loan_agreement')
    policy=json.loads(query("SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
        f"WHERE(catalog_id,account_id,action)=('{catalog}','{account}','gas.loan.borrow') ORDER BY revision DESC LIMIT 1;"))
    body=dict(agreementId=agreement,evidenceId=title,quantity='1',titleAt=when,availableAt=when,returnAt='2020-12-22T12:00:00Z',
        returnMarkCurve='future',returnMarkContractId='50000000-0000-0000-0000-000000000001',carrying='nominal_mark_v1',
        feeAmount='0',feeDueAt=None,lenderId=source['lender'],lenderRevision=source['revision'])
    operation,created=record(account,'borrow',body,policy['id'],policy['revision'])
    acknowledgement,ack=record(account,'borrow',body,policy['id'],policy['revision'])
    assert created['changed'] and not created['semanticAcknowledgement']
    assert not ack['changed'] and ack['semanticAcknowledgement'] and ack['id']==created['id']
    assert query(f"SELECT count(*) FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{created['id']}');")=='0'
    return dict(loan=created['id'],operation=operation,acknowledgement=acknowledgement,account=account,body=body,policy=policy)


def _origins(query,manifest,zero):
    catalog=manifest['catalogId']
    rows=json.loads(query("SELECT jsonb_agg(jsonb_build_object('loan',l.id,'quantity',l.quantity::text,'terms',to_jsonb(t),'command',to_jsonb(c),"
        "'position',to_jsonb(p),'originalFee',to_jsonb(f)) ORDER BY l.id) FROM trading.gas_owned_loans l "
        "LEFT JOIN trading.gas_loan_fee_terms t ON(t.catalog_id,t.loan_id,t.revision)=(l.catalog_id,l.id,1) "
        "LEFT JOIN trading.gas_owned_commands c ON(c.catalog_id,c.request_id)=(t.catalog_id,t.origin_request_id) "
        "LEFT JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.received_lot_id) "
        "LEFT JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(t.catalog_id,t.original_fee_id) "
        f"WHERE l.catalog_id='{catalog}';"),parse_float=Decimal)
    assert rows and query(f"SELECT count(*) FROM trading.gas_loan_fee_terms WHERE catalog_id='{catalog}';")==str(len(rows))
    for row in rows:
        terms,command,position=row['terms'],row['command'],row['position']
        assert terms['revision']==1 and terms['previous_revision'] is None and terms['implementation']=='native_fixed_loan_fee_origin_v1'
        assert command['request']['action']=='borrow' and command['outcome']['id']==row['loan']
        assert command['outcome']['changed'] and not command['outcome']['semanticAcknowledgement']
        assert terms['actor_id']==command['actor_id']==position['actor_id']
        assert terms['publication_order']==position['publication_order'] and terms['effective_at']==position['effective_at']
        assert Decimal(str(terms['amount']))==Decimal(command['request']['body']['feeAmount'])
        if row['loan']==zero['loan']:
            assert terms['origin_request_id']==zero['operation'] and terms['origin_request_id']!=zero['acknowledgement']
            assert Decimal(str(terms['amount']))==0 and terms['due_at'] is None and terms['original_fee_id'] is None and row['originalFee'] is None
        else:
            assert Decimal(row['quantity'])==100 and Decimal(str(terms['amount']))==Decimal('-2')
            assert terms['due_at'].startswith('2020-12-21T12:00:00')
            assert terms['original_fee_id']==row['originalFee']['id'] and Decimal(str(row['originalFee']['amount']))==Decimal('-2')
            assert terms['due_at']==row['originalFee']['due_at'] and terms['effective_at']==row['originalFee']['effective_at']
    assert len(rows)==2,'The upgrade fixture requires exactly its nonzero and zero-fee native loans.'


def main():
    prepared={};root=Path(__file__).resolve().parents[1]
    def before_migration(query,migration):
        if prepared and not prepared.get('verified') and migration.name>'0308_gas_fixed_fee_terms.sql':verify(query)
        if migration.name!='0308_gas_fixed_fee_terms.sql':return
        assert not prepared and query("SELECT to_regclass('trading.gas_loan_fee_terms') IS NULL;")=='t'
        reference,market,security,manifest=fees.fixture(root)
        load_fixture(query,reference,market,security,manifest)
        _seed_nonzero(query,manifest)
        zero=_seed_zero(query,manifest);tables=_tables(query)
        prepared.update(manifest=manifest,zero=zero,tables=tables,state=_state(query,manifest['catalogId'],tables))
        print('PASS predecessor fixture: actual nonzero-fee and explicit zero-fee native borrow operations, retained bank revisions and a distinct zero-loan acknowledgement before 0308',flush=True)

    def verify(query):
        manifest=prepared['manifest']
        assert _state(query,manifest['catalogId'],prepared['tables'])==prepared['state'],'Fee migration changed preexisting gas facts, bank, publications or first receipts.'
        _origins(query,manifest,prepared['zero']);prepared['verified']=True
        print('PASS existing-origin fee upgrade: all predecessor gas financial rows and receipts unchanged; one exact actual-borrow origin per loan, no acknowledgement origin or invented zero-fee cash',flush=True)

    def loaded_fixture(_):
        assert prepared,'The existing-origin fixture must be seeded before applying 0308.'
        return '','','',prepared['manifest']

    def checks(query,manifest):
        assert prepared and manifest is prepared['manifest']
        if not prepared.get('verified'):verify(query)
        _origins(query,manifest,prepared['zero'])
        zero=prepared['zero'];catalog=manifest['catalogId']
        _,ack=_record(query,manifest)(zero['account'],'borrow',zero['body'],zero['policy']['id'],zero['policy']['revision'])
        assert not ack['changed'] and ack['semanticAcknowledgement'] and ack['id']==zero['loan']
        assert query(f"SELECT count(*) FROM trading.gas_loan_fee_terms WHERE(catalog_id,loan_id,revision,origin_request_id)=('{catalog}','{zero['loan']}',1,'{zero['operation']}');")=='1'
        assert query(f"SELECT count(*) FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{zero['loan']}');")=='0'
        assert query(f"SELECT count(*) FROM trading.gas_owned_commands WHERE catalog_id='{catalog}' AND request->>'action'='borrow' AND outcome->>'id'='{zero['loan']}' AND outcome->>'changed'='false' AND outcome->>'semanticAcknowledgement'='true';")=='2'
        _origins(query,manifest,zero)
        print('PASS native fee-origin upgrade: a post-upgrade acknowledgement retains the original zero-loan origin without inventing fee cash',flush=True)

    native_main(checks=checks,before_migration=before_migration,fixture_factory=loaded_fixture,through_migration='0308')


if __name__=='__main__':main()
