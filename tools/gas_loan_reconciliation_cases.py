"""Independent native loan counterexamples; no calculations mirror the writer."""
import json
from decimal import Decimal
from uuid import uuid4


def checks(query,call,record,evidence,capture,state,manifest):
    catalog,account,loan,at=(manifest['catalogId'],state['account'],state['loan'],state['at'])
    when='2020-12-20T10:00:00Z'
    title=evidence('synthetic_loan_title','title_receipt','0.125',when,2)
    agreement=evidence('synthetic_loan_agreement','loan_agreement','0.125',when,2)
    context=capture();operation=str(uuid4());policy=state['policy']
    body=dict(loanId=loan['id'],expectedRevision=2,stockExpectedRevision=2,returnExpectedRevision=2,quantity='0.125',
        titleEvidenceId=title,agreementEvidenceId=agreement,sourceAllocation='preserve_unallocated_source_quantity_v1',
        feeDisposition='retain_fixed_fee_memberships_v1',consumptions=[],evidence='Synthetic complete loan source correction')
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def command(selection,op=operation):
        return f"SELECT security.reconcile_gas_loan_quantities('{catalog}','{op}','{account}',{literal(selection)},'{at}','{context}','{policy}',1,false)::text;"
    def refused(statement,codes):
        try:call(statement+'SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;',rollback=True)
        except RuntimeError as error:assert any(code in str(error) for code in codes),str(error)
        else:raise AssertionError('Loan correction accepted a malformed or stale financial proposal')
    refused(command(body),['gas_stock_capacity','gas_loan_return'])
    assert query(f"SELECT max(revision) FROM trading.gas_owned_loan_quantities WHERE(catalog_id,loan_id)=('{catalog}','{loan['id']}');")=='2'
    title=evidence('synthetic_loan_title','title_receipt','0.5',when,3)
    agreement=evidence('synthetic_loan_agreement','loan_agreement','0.5',when,3)
    context=capture();body=body|dict(quantity='0.5',titleEvidenceId=title,agreementEvidenceId=agreement)
    # Establish that every negative uses an otherwise executable same producer.
    assert json.loads(call(command(body)+'SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;',rollback=True))['changed']
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    faults={
        'missing_resource_array':"NEW.request=NEW.request#-ARRAY['body','consumptions'];",
        'forged_current_quantity':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['currentQuantity'],'\"100\"'::jsonb);",
        'forged_current_revision':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['currentRevision'],'100'::jsonb);",
        'forged_acknowledged_revision':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['acknowledgedRevision'],'1'::jsonb);",
        'forged_source_allocation':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['sourceAllocations','0','allocated'],'\"0\"'::jsonb);",
        'missing_source_closure':"NEW.outcome=NEW.outcome-'sourceAllocations';",
        'false_acknowledgement':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['semanticAcknowledgement'],'true'::jsonb);"}
    try:
        for name,change in faults.items():
            query("CREATE OR REPLACE FUNCTION trading.fixture_loan_correction_fault() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN "
                "IF NEW.request->>'action'='loan_quantity_reconciliation' THEN "+change+"END IF;RETURN NEW;END$$;")
            query('CREATE TRIGGER fixture_loan_correction_fault BEFORE INSERT ON trading.gas_owned_commands FOR EACH ROW EXECUTE FUNCTION trading.fixture_loan_correction_fault();')
            try:
                refused(command(body)+'SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;',['gas_loan_quantity_seal','gas_owned_input'])
                # This must cross the actual deferred COMMIT boundary too.
                try:call(command(body))
                except RuntimeError as error:assert 'gas_loan_quantity_seal' in str(error) or 'gas_owned_input' in str(error),str(error)
                else:raise AssertionError('Malformed loan receipt committed: '+name)
            finally:query('DROP TRIGGER fixture_loan_correction_fault ON trading.gas_owned_commands;')
    finally:query('DROP FUNCTION IF EXISTS trading.fixture_loan_correction_fault();')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    from gas_owned_reconciliation_authority import checks as authority_checks
    authority_checks(query,call,command(body),manifest)
    from gas_loan_final_authority import checks as final_authority_checks
    final_authority_checks(query,call,command(body),manifest)
    # Global acknowledgement retains the old existing A; it must never rewind B.
    old=body|dict(expectedRevision=1,stockExpectedRevision=1,returnExpectedRevision=1,quantity='1',
        titleEvidenceId=state['originalTitle'],agreementEvidenceId=state['originalAgreement'])
    acknowledged=json.loads(call(command(old,str(uuid4()))))
    assert acknowledged['semanticAcknowledgement'] and not acknowledged['changed']
    assert acknowledged['acknowledgedRevision']==1 and acknowledged['currentRevision']==2 and Decimal(acknowledged['currentQuantity'])==Decimal('.75')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    corrected=json.loads(call(command(body)))
    assert corrected['currentRevision']==3 and Decimal(corrected['currentQuantity'])==Decimal('.5')
    assert int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))==int(before)+1
    foreign=query(f"SELECT id FROM trading.gas_owned_accounts WHERE catalog_id='{catalog}' AND id<>'{account}' ORDER BY id LIMIT 1;")
    foreign_command=command(body,str(uuid4())).replace(f"'{account}'",f"'{foreign}'")
    refused(foreign_command,['gas_loan_quantity_source'])
    # A fresh value must require the named source even when current head numbers
    # can be guessed. Use a new exact evidence proposal, not an acknowledged fact.
    unseen_body=body|dict(quantity='0.375',expectedRevision=3,stockExpectedRevision=3,returnExpectedRevision=3)
    unseen=command(unseen_body,str(uuid4())).replace(f"'{context}'",f"'{uuid4()}'")
    refused(unseen,['gas_owned_context'])
    principal=query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{manifest['identityIssuer']}' AND subject='trader';")
    saved=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE p.permission='RecordGasOwnedHoldings' AND p.role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
    assert saved not in ('','null')
    try:
        query(f"DELETE FROM security.role_permissions WHERE permission='RecordGasOwnedHoldings' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
        refused(command(body),['gas_owned_denied'])
    finally:
        query("INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'"+saved.replace("'","''")+"'::jsonb) ON CONFLICT DO NOTHING;")
    assert json.loads(call(command(body)))==corrected
    assert int(query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';"))==int(before)+1
    from gas_loan_reconciliation_acknowledgements import checks as acknowledgement_checks
    acknowledgement_checks(query,call,lambda identity:command(body,identity),catalog)
    print('PASS loan source closure: all persisted request/outcome faults refuse at immediate/COMMIT; original grant waits/commit, old acknowledgement and exact correction preserve financial history',flush=True)
