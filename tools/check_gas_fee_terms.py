"""Fixed loan fee corrections retain gross cash and recorded bank allocations."""
import json
from decimal import Decimal
from uuid import uuid4
from check_reference_editing import main
from check_gas_owned_holdings import checks as baseline
from preview_fixture import fixture as native_fixture


def fixture(root):
    reference,market,security,manifest=native_fixture(root)
    assert reference.count('COMMIT;')==1 and not reference.rsplit('COMMIT;',1)[1].strip()
    # A valid same-revision currency makes the currency-only seal probe
    # independent of its ordinary foreign-key checks.
    reference=reference.rsplit('COMMIT;',1)[0]+f"INSERT INTO ref_data.currencies VALUES('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'," \
        "'synthetic_fee_currency','SFC','Synthetic fee currency',2);COMMIT;"
    return reference,market,security,{**manifest,'identityIssuer':'https://preview.invalid'}


def checks(query,manifest,after_amendments=None):
    # Keep the shared native/managed financial baseline. Its separate ordinary
    # qualifier also tests a deliberately pending cashout source correction;
    # this consumer needs an uncontested whole-book source for ordinary fee PV.
    baseline(query,manifest,form_source_counterexamples=False)
    catalog,tenant,workspace=(manifest[key] for key in ('catalogId','tenantId','workspaceId'))
    at='2020-12-20T16:00:00Z'
    def literal(body):return "'"+json.dumps(body).replace("'","''")+"'::jsonb"
    def call(statement,rollback=False):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statement}"+('ROLLBACK;' if rollback else 'COMMIT;'))
    def capture():return call(f"SELECT security.resolve_operation_context('{catalog}','2020-12-20',NULL);")
    def read(loan):
        context=capture()
        return json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');"
            f"SELECT security.read_gas_loan_fee_terms('{catalog}','{loan}','{context}','{at}')::text;"),parse_float=Decimal)
    fee=json.loads(query("SELECT to_jsonb(f)||jsonb_build_object('accountId',l.account_id,'lenderId',l.lender_id,'lenderRevision',l.lender_revision) "
        "FROM trading.gas_owned_fees f JOIN trading.gas_owned_loans l ON(l.catalog_id,l.id,l.return_position_id)=(f.catalog_id,f.loan_id,f.position_id) "
        "JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(l.catalog_id,l.account_id) "
        f"WHERE f.catalog_id='{catalog}' AND a.code='synthetic_owned_inventory_1' AND l.quantity=100;"),parse_float=Decimal)
    account=fee['accountId']
    source=read(fee['loan_id'])
    assert source['implementation']=='gas_fixed_loan_fee_terms_source_v1'
    assert source['loanId']==fee['loan_id'] and source['accountId']==account
    assert source['terms']['revision']==1 and Decimal(source['terms']['amount'])==Decimal('-2')
    assert source['terms']['dueAt'].startswith('2020-12-21T12:00:00')
    assert len(source['payments'])==2 and Decimal(source['currentPaid'])==Decimal('-0.5')
    def record(action,body,policy=None,revision=None):
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','{action}','{account}',{literal(body)},'{at}','{capture()}',"
            +(f"'{policy}',{revision}" if policy else 'NULL,NULL')+",false)::text;"))
    when='2020-12-20T15:00:00Z'
    def evidence(key,kind):return record('evidence',dict(sourceKey=key,kind=kind,quantity='1',occurredAt=when,
        evidence='Synthetic independently observed zero-fee loan',expectedRevision=0))['id']
    title=evidence('synthetic_zero_fee_title','title_receipt');agreement=evidence('synthetic_zero_fee_agreement','loan_agreement')
    policy=json.loads(query(f"SELECT to_jsonb(p) FROM trading.gas_owned_action_bindings p WHERE(p.catalog_id,p.account_id,p.action)=('{catalog}','{account}','gas.loan.borrow') ORDER BY revision DESC LIMIT 1;"))
    zero=record('borrow',dict(agreementId=agreement,evidenceId=title,quantity='1',titleAt=when,availableAt=when,returnAt='2020-12-22T12:00:00Z',
        returnMarkCurve='future',returnMarkContractId='50000000-0000-0000-0000-000000000001',carrying='nominal_mark_v1',
        feeAmount='0',feeDueAt=None,lenderId=fee['lenderId'],lenderRevision=fee['lenderRevision']),policy['policy_id'],policy['policy_revision'])
    no_fee=read(zero['id'])
    assert no_fee['terms']['revision']==1 and Decimal(no_fee['terms']['amount'])==0 and no_fee['terms']['dueAt'] is None
    assert no_fee['rootFee'] is None and no_fee['payments']==[] and Decimal(no_fee['currentPaid'])==0
    assert query(f"SELECT count(*) FROM trading.gas_owned_fees WHERE(catalog_id,loan_id)=('{catalog}','{zero['id']}');")=='0'
    print('PASS fixed loan fee source: exact nonzero and explicit zero origins, no invented cash, retained gross fee and bank-allocation revisions',flush=True)
    from gas_fee_terms_cases import amendments
    packet=amendments(query,call,capture,record,read,manifest,fee,zero,at)
    if after_amendments is not None:after_amendments(query,manifest,packet)



if __name__=='__main__':main(checks=checks,fixture_factory=fixture)
