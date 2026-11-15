"""All-acknowledgement receipts still have a complete independent native seal."""
from uuid import uuid4


def checks(query,call,command,catalog):
    before=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    faults={
        'missing_resource_array':"NEW.request=NEW.request#-ARRAY['body','consumptions'];",
        'null_resource_array':"NEW.request=jsonb_set(NEW.request,ARRAY['body','consumptions'],'null'::jsonb);",
        'wrong_current_quantity':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['currentQuantity'],'\"100\"'::jsonb);",
        'wrong_acknowledged_revision':"NEW.outcome=jsonb_set(NEW.outcome,ARRAY['acknowledgedRevision'],'100'::jsonb);"}
    try:
        for name,change in faults.items():
            query("CREATE OR REPLACE FUNCTION trading.fixture_loan_ack_fault() RETURNS trigger LANGUAGE plpgsql AS $$BEGIN "
                "IF NEW.request->>'action'='loan_quantity_reconciliation' THEN "+change+"END IF;RETURN NEW;END$$;")
            query('CREATE TRIGGER fixture_loan_ack_fault BEFORE INSERT ON trading.gas_owned_commands FOR EACH ROW EXECUTE FUNCTION trading.fixture_loan_ack_fault();')
            try:
                for immediate in (True,False):
                    try:call(command(str(uuid4()))+('SET CONSTRAINTS ALL IMMEDIATE;' if immediate else ''),rollback=immediate)
                    except RuntimeError as error:assert 'gas_loan_quantity_seal' in str(error) or 'gas_owned_input' in str(error),str(error)
                    else:raise AssertionError('All-acknowledgement loan receipt omitted or forged its complete native input/outcome: '+name)
            finally:query('DROP TRIGGER fixture_loan_ack_fault ON trading.gas_owned_commands;')
    finally:query('DROP FUNCTION IF EXISTS trading.fixture_loan_ack_fault();')
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before
    print('PASS loan all-acknowledgement seal: missing/null resource arrays and forged current/acknowledged outcomes refuse at immediate constraints and actual COMMIT',flush=True)
