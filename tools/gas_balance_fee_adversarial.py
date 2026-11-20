"""Independent deferred-seal probes through a deliberately incomplete test-only definer."""
from uuid import uuid4

from gas_park_loan_fixture import literal, rejected


def acknowledgement_checks(f, checkpoint):
    query, catalog = f.query, f.catalog
    # The future producer intentionally omits source/actor admission. Table guards
    # must protect the same financial contract independently of the public producer.
    query("CREATE FUNCTION security.synthetic_balance_fee_ack(operation uuid,chosen_actor uuid) RETURNS void "
        "LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN "
        "INSERT INTO trading.gas_park_loan_commands SELECT c.catalog_id,operation,c.trade_id,chosen_actor,c.request,"
        "c.outcome||jsonb_build_object('operationId',operation,'changed',false),security.gas_park_loan_authority(c.catalog_id,true) "
        f"FROM trading.gas_park_loan_commands c WHERE(c.catalog_id,c.outcome->>'id')=('{catalog}','{checkpoint['id']}') "
        "AND c.outcome->'changed'='true'::jsonb;END$$;"
        "ALTER FUNCTION security.synthetic_balance_fee_ack(uuid,uuid) OWNER TO openetrm_security_owner;"
        "REVOKE ALL ON FUNCTION security.synthetic_balance_fee_ack(uuid,uuid) FROM PUBLIC;"
        "GRANT EXECUTE ON FUNCTION security.synthetic_balance_fee_ack(uuid,uuid) TO preview_runtime;")
    trader = query("SELECT principal_id FROM security.identity_bindings WHERE subject='trader';")
    other = query("SELECT principal_id FROM security.identity_bindings WHERE subject='viewer';")
    reference = f.manifest['referenceCatalogId']
    tenant, workspace = f.manifest['tenantId'], f.manifest['workspaceId']
    mode = query(f"SELECT access_mode FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{tenant}','{workspace}');")
    observed = []

    def attempt(actor, expected):
        try:
            f.call(f"SELECT security.synthetic_balance_fee_ack('{uuid4()}','{actor}');")
        except RuntimeError as error:
            assert expected in str(error), str(error)
            observed.append('denied')
        else:
            observed.append('accepted')

    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{tenant}','{workspace}');")
    try:
        attempt(trader, 'gas_owned_denied')
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','{mode}');")
    attempt(other, 'gas_balance_fee_publication')
    assert observed == ['denied', 'denied'], 'Deferred semantic acknowledgement admitted revoked reference / forged actor: ' + repr(observed)
    print('PASS deferred fee acknowledgement independently requires exact reference scope and the current actor.', flush=True)


def uncommitted_source_check(f, trade, selection):
    context = f.capture()
    rejected(lambda: f.call(f"SELECT security.record_gas_park_loan('{f.catalog}','{uuid4()}','{trade}','fee_determine',"
        f"{literal(selection)},'{f.at}','{context}',false);SELECT security.resolve_operation_context('{f.catalog}','{f.day}',NULL);"), 'knowledge_retention')
    print('PASS a source certificate cannot include its uncommitted fee publication.', flush=True)


def typed_fact_probe(f, trade, head, through, effective):
    catalog = f.catalog
    name = 'synthetic_balance_fee_fact_' + head['id'].replace('-', '')[:12]
    f.query(f"CREATE FUNCTION security.{name}(operation uuid,context uuid,source_version integer,mode text) RETURNS void "
        "LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$"
        "DECLARE old trading.gas_park_loan_fee_determinations;fact trading.gas_park_loan_fee_determinations;publication integer;body jsonb;outcome jsonb;BEGIN "
        "PERFORM pg_advisory_xact_lock(730849102);"
        f"SELECT * INTO STRICT old FROM trading.gas_park_loan_fee_determinations d WHERE(d.catalog_id,d.id)=('{catalog}','{head['id']}');"
        "fact=old;fact.id=operation;fact.operation_id=operation;fact.revision=old.revision+1;fact.previous_id=old.id;"
        f"fact.business_key='synthetic_guard_probe';fact.through_at='{through}';fact.effective_at='{effective}';"
        "fact.source_context_id=context;fact.source_head=source_version;fact.contribution=0;fact.fee_id=NULL;"
        "SELECT s.effective_on,s.known_at,s.system_id,s.timeline INTO fact.source_business_on,fact.source_known_at,fact.source_system_id,fact.source_timeline "
        "FROM security.operation_read_inputs s WHERE(s.catalog_id,s.id)=(old.catalog_id,context);"
        "IF mode='amount' THEN fact.exact_n=fact.exact_n+1;END IF;"
        "IF mode='duplicate' THEN fact.business_key=old.business_key;fact.through_at=old.through_at;fact.effective_at=old.effective_at;END IF;"
        "SELECT max(p.publication_order)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=old.catalog_id;"
        "INSERT INTO trading.publications VALUES(old.catalog_id,publication,pg_current_xact_id());fact.publication_order=publication;"
        "INSERT INTO trading.gas_park_loan_fee_determinations SELECT fact.*;"
        "IF mode<>'members' THEN INSERT INTO trading.gas_park_loan_fee_return_inputs SELECT old.catalog_id,operation,r.id "
        f"FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=('{catalog}','{trade}') "
        f"AND trading.gas_owned_visible(old.catalog_id,r.publication_order,r.effective_at,context,'{f.at}');END IF;"
        "body=trading.gas_park_loan_fact(jsonb_build_object('expectedVersion',source_version,'previousDeterminationId',old.id,"
        "'through',to_char(fact.through_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'),"
        "'effectiveAt',to_char(fact.effective_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"'),'businessKey',fact.business_key,'evidence',fact.evidence));"
        "outcome=jsonb_build_object('id',fact.id,'feeId',NULL,'through',fact.through_at,'roundedTotal',trim_scale(fact.rounded_total)::text,"
        "'contribution','0','exactNumerator',trim_scale(fact.exact_n)::text,'exactDenominator',trim_scale(fact.exact_d)::text,"
        "'effectiveAt',fact.effective_at,'operationId',operation,'tradeId',old.trade_id,'action','fee_determine','changed',true);"
        "IF mode<>'receipt' THEN INSERT INTO trading.gas_park_loan_commands VALUES(old.catalog_id,operation,old.trade_id,old.actor_id,"
        f"jsonb_build_object('tradeId',old.trade_id,'action','fee_determine','body',body,'asOf','{f.at}'::timestamptz,'contextId',context),"
        "outcome,security.gas_park_loan_authority(old.catalog_id,true));END IF;END$$;"
        f"ALTER FUNCTION security.{name}(uuid,uuid,integer,text) OWNER TO openetrm_security_owner;"
        f"REVOKE ALL ON FUNCTION security.{name}(uuid,uuid,integer,text) FROM PUBLIC;"
        f"GRANT EXECUTE ON FUNCTION security.{name}(uuid,uuid,integer,text) TO preview_runtime;")

    def attempt(context, mode, code=None):
        source = f.query(f"SELECT greatest((SELECT max(publication_order) FROM trading.gas_park_loan_returns WHERE(catalog_id,trade_id)=('{catalog}','{trade}')),"
            f"(SELECT max(publication_order) FROM trading.gas_park_loan_fee_determinations WHERE(catalog_id,trade_id)=('{catalog}','{trade}')));")
        sql = f"SELECT security.{name}('{uuid4()}','{context}',{source},'{mode}');"
        if code is None:
            f.call(sql + 'SELECT security.finish_operation_read();SET CONSTRAINTS ALL IMMEDIATE;', rollback=True)
        else:
            rejected(lambda: f.call(sql), code)
    return attempt


def typed_fact_checks(f, trade, head):
    attempt = typed_fact_probe(f, trade, head, '2020-12-20T13:00:00Z', '2020-12-20T13:10:00Z')
    context = f.capture()
    attempt(context, 'valid')
    for mode, code in [('amount', 'gas_balance_fee_amount'), ('members', 'gas_balance_fee_source'),
                       ('receipt', 'gas_balance_fee_receipt'), ('duplicate', 'gas_balance_fee_conflict')]:
        attempt(context, mode, code)
    print('PASS independent deferred fee proof refuses wrong rational totals, incomplete accepted-return manifests and unreceipted typed facts.', flush=True)
