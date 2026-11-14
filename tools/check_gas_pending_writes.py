"""New financial facts cannot consume unresolved accepted source revisions."""
import json
from uuid import uuid4

from check_reference_editing import main
from check_gas_applied_sources import checks as applied_checks


def upgrade_checks(query,migration):
    if migration.name!='0277_gas_applied_source_reconciliation.sql':return
    from assistant_context import check_guard
    signatures=[('security.record_gas_owned(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)',True),
        ('security.record_gas_futures_delivery(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)',True),
        ('trading.apply_futures_delivery_principal(uuid,uuid,uuid,uuid,timestamptz,uuid,integer,jsonb)',False)]
    for signature,definer in signatures:
        types=signature.split('(',1)[1][:-1].split(',')
        type_array=','.join("'"+value+"'::regtype::oid" for value in types)
        changes=[(change,signature) for change in (
            f"UPDATE pg_proc SET proargnames=array_fill('changed'::text,ARRAY[{len(types)}]) WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proallargtypes=ARRAY[{type_array}] WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET pronargdefaults=1 WHERE oid='{signature}'::regprocedure;",
            "CREATE FUNCTION security.synthetic_pending_default_guard(value uuid[] DEFAULT NULL) RETURNS uuid[] LANGUAGE sql AS 'SELECT value';"
            f"UPDATE pg_proc SET proargdefaults=(SELECT proargdefaults FROM pg_proc WHERE oid='security.synthetic_pending_default_guard(uuid[])'::regprocedure) WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET proargmodes=array_fill('i'::\"char\",ARRAY[{len(types)}]) WHERE oid='{signature}'::regprocedure;",
            f"UPDATE pg_proc SET provariadic='uuid'::regtype WHERE oid='{signature}'::regprocedure;",
            f"ALTER FUNCTION {signature} SET work_mem='64kB';",f"ALTER FUNCTION {signature} STRICT;",
            f"ALTER FUNCTION {signature} LEAKPROOF;",f"ALTER FUNCTION {signature} PARALLEL SAFE;",
            f"ALTER FUNCTION {signature} STABLE;",f"ALTER FUNCTION {signature} SECURITY {'INVOKER' if definer else 'DEFINER'};",
            f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",
            f"GRANT EXECUTE ON FUNCTION {signature} TO openetrm_runtime WITH GRANT OPTION;",
            f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
            f"DO $$DECLARE d text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(d,'AS $function$','AS $function$ /* synthetic drift */ ');END$$;")]
        check_guard(query,migration.read_text(),migration.name[:4],'gas_pending_source_upgrade',changes)


def checks(query,manifest):
    applied_checks(query,manifest)
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    at='2020-12-20T16:00:00Z'
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql,rollback=False):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}"
            +('SET CONSTRAINTS ALL IMMEDIATE;ROLLBACK;' if rollback else 'COMMIT;'))
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2020-12-20',NULL);")
    account=query(f"SELECT id FROM trading.gas_owned_accounts WHERE catalog_id='{catalog}' AND kind='owned_inventory' ORDER BY publication_order LIMIT 1;")
    def policy(action):
        return json.loads(query(f"SELECT jsonb_build_object('id',policy_id,'revision',policy_revision) FROM trading.gas_owned_action_bindings "
            f"WHERE(catalog_id,account_id,action)=('{catalog}','{account}','{action}') ORDER BY revision DESC LIMIT 1;"))
    def command(action,body,code=None,operation=None,context=None,rollback=False):
        selected=policy(code) if code else None
        return json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{operation or uuid4()}','{action}','{account}',{literal(body)},'{at}','{context or capture()}',"
            +(f"'{selected['id']}',{selected['revision']}" if selected else 'NULL,NULL')+",false)::text;",rollback))
    def evidence(kind,quantity,source=None,revision=0,when='2020-12-20T14:00:00Z'):
        return command('evidence',dict(sourceKey=source or str(uuid4()),kind=kind,quantity=quantity,occurredAt=when,
            evidence='Synthetic current financial source',expectedRevision=revision))['id']
    # Pending facts elsewhere in this account must not turn its independent
    # new economic interests into an implicit account-wide suspension.
    source=str(uuid4());receipt=evidence('title_receipt','10',source)
    title=dict(evidenceId=receipt,quantity='10',titleAt='2020-12-20T14:00:00Z',availableAt='2020-12-20T14:00:00Z',
        originPolicy='opening_owned_stock_v1',carrying='nominal_mark_v1')
    operation=str(uuid4());original_context=capture()
    stock=command('receive_title',title,'gas.owned.receive_title',operation,original_context)
    corrected=evidence('title_receipt','9',source,1)
    publication=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    assert command('receive_title',title,'gas.owned.receive_title',operation,original_context)==stock
    acknowledged=command('receive_title',title,'gas.owned.receive_title')
    assert not acknowledged['changed'] and acknowledged['id']==stock['id']
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==publication
    missing=[]
    def reject(action,body,code,expected=('gas_owned_reconciliation',)):
        try:command(action,body,code,rollback=True)
        except RuntimeError as error:assert any(value in str(error) for value in expected),str(error)
        else:missing.append(action)
    reject('receive_title',{**title,'evidenceId':corrected,'quantity':'9'},'gas.owned.receive_title',('gas_owned_reconciliation','gas_owned_source_already_applied'))
    reject('reserve',dict(lotId=stock['id'],businessKey='synthetic_pending',quantity='1',effectiveAt='2020-12-20T15:00:00Z'),'gas.owned.reserve')
    reject('dispatch',dict(lotId=stock['id'],evidenceId=evidence('physical_delivery','1',when='2020-12-20T15:00:00Z'),quantity='1',reservationId=None,
        effectiveAt='2020-12-20T15:00:00Z'),'gas.owned.dispatch')
    loan=query(f"SELECT id FROM trading.gas_owned_loans WHERE(catalog_id,account_id)=('{catalog}','{account}');")
    reject('repay',dict(loanId=loan,lotId=stock['id'],evidenceId=evidence('loan_return','1',when='2020-12-20T15:00:00Z'),quantity='1',reservationId=None,
        effectiveAt='2020-12-20T15:00:00Z'),'gas.loan.repay')
    agreement=evidence('loan_agreement','1')
    reject('receive_title',{**title,'evidenceId':agreement,'quantity':'1'},'gas.owned.receive_title',('gas_owned_evidence',))
    a=json.loads(query(f"SELECT to_jsonb(a) FROM trading.gas_owned_accounts a WHERE(catalog_id,id)=('{catalog}','{account}');"))
    lender=query(f"SELECT id FROM trading.counterparties WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")
    reject('borrow',dict(agreementId=agreement,evidenceId=agreement,quantity='1',titleAt='2020-12-20T14:00:00Z',availableAt='2020-12-20T14:00:00Z',
        returnAt='2020-12-21T12:00:00Z',returnMarkCurve=a['mark_curve_code'],returnMarkContractId=a['mark_contract_id'],carrying='currency_curve_v1',
        feeAmount='0',feeDueAt=None,lenderId=lender,lenderRevision=1),'gas.loan.borrow',('gas_owned_evidence',))
    original_agreement=json.loads(query(f"SELECT to_jsonb(e) FROM trading.gas_owned_loans l JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.return_position_id) "
        f"JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id) WHERE(l.catalog_id,l.id)=('{catalog}','{loan}');"))
    latest_agreement=evidence('loan_agreement','99',original_agreement['source_key'],original_agreement['revision'])
    reject('borrow',dict(agreementId=latest_agreement,evidenceId=evidence('title_receipt','1'),quantity='1',titleAt='2020-12-20T14:00:00Z',availableAt='2020-12-20T14:00:00Z',
        returnAt='2020-12-21T12:00:00Z',returnMarkCurve=a['mark_curve_code'],returnMarkContractId=a['mark_contract_id'],carrying='currency_curve_v1',
        feeAmount='0',feeDueAt=None,lenderId=lender,lenderRevision=1),'gas.loan.borrow',('gas_owned_reconciliation','gas_owned_source_already_applied'))
    assert not missing,'Accepted new financial facts through pending sources: '+', '.join(missing)
    print('PASS new gas facts: current source keys and stock ancestors gate fresh title/reserve/dispatch/repay; independent sources, original receipts and global acknowledgements remain available',flush=True)


if __name__=='__main__':main(checks=checks,before_migration=upgrade_checks)
