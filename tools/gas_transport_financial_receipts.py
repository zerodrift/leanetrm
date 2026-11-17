"""Independent original-request/source consistency for fresh financial facts."""
from gas_transport_financial_fixture import literal


def booking_context(f, sample_trade, rejected):
    # A future owner-definer can omit an application check. The private typed
    # counterpart must still tie every duplicate request source pin together.
    f.query("""CREATE OR REPLACE FUNCTION trading.synthetic_financial_bad_book_context(catalog uuid,body jsonb,source uuid,at timestamptz,sample uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
 DECLARE i trading.gas_transport_financial_instruments;t trading.gas_transport_financial_trades;actor uuid;deadline timestamptz;
  publication integer;trade uuid=gen_random_uuid();instrument uuid=gen_random_uuid();operation uuid=(body->>'operationId')::uuid;request jsonb;outcome jsonb;
 BEGIN
  deadline=security.gas_transport_financial_authority(catalog,'book');
  SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
  SELECT * INTO STRICT t FROM trading.gas_transport_financial_trades x WHERE(x.catalog_id,x.trade_id)=(catalog,sample);
  SELECT * INTO STRICT i FROM trading.gas_transport_financial_instruments x WHERE(x.catalog_id,x.instrument_id)=(catalog,t.instrument_id);
  IF i.fixed_fee_amount<>0 THEN RAISE EXCEPTION 'Synthetic receipt control requires a real zero-fee agreement';END IF;
  body=jsonb_set(body,'{contextId}',to_jsonb(gen_random_uuid()));
  PERFORM pg_advisory_xact_lock(730849102);
  SELECT max(publication_order)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.instruments VALUES(catalog,instrument,publication,at);
  INSERT INTO trading.gas_transport_financial_instruments SELECT(jsonb_populate_record(NULL::trading.gas_transport_financial_instruments,
   to_jsonb(i)||jsonb_build_object('instrument_id',instrument,'publication_order',publication,'effective_at',at))).*;
  PERFORM security.stamp_trade_booking(catalog);
  INSERT INTO trading.trades VALUES(catalog,trade,(body->>'bookId')::uuid,operation,actor,body,publication,at);
  INSERT INTO trading.gas_transport_financial_trades VALUES(catalog,trade,instrument,body->>'businessKey',(body->>'bookId')::uuid,(body->>'entitlementId')::uuid,operation,actor,
   (body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,at);
  INSERT INTO trading.gas_transport_financial_states VALUES(catalog,trade,operation,1,NULL,(body->>'entitlementId')::uuid,(body->>'capacityVersionId')::uuid,'live',NULL,NULL,
   body->>'evidence',source,at,actor,publication,at);
  request=jsonb_build_object('body',body,'effectiveAt',trading.gas_transport_capacity_instant(at),'asOf',trading.gas_transport_capacity_instant(at),'sourceContextId',source);
  outcome=jsonb_build_object('tradeId',trade,'instrumentId',instrument,'operationId',operation,'revision',1,'financialStateId',operation,'feeBindingId',NULL,'changed',true);
  INSERT INTO trading.gas_transport_financial_commands VALUES(catalog,operation,'book',trade,instrument,actor,request,trading.gas_transport_financial_fact(body,at),outcome,source,deadline);
 END$$;
 ALTER FUNCTION trading.synthetic_financial_bad_book_context(uuid,jsonb,uuid,timestamptz,uuid) OWNER TO openetrm_security_owner;
 GRANT EXECUTE ON FUNCTION trading.synthetic_financial_bad_book_context(uuid,jsonb,uuid,timestamptz,uuid) TO openetrm_runtime;""")
    old_book = f.book
    f.book = f.query(f"SELECT book_id FROM trading.gas_transport_financial_trades WHERE(catalog_id,trade_id)=('{f.catalog}','{sample_trade}');")
    try:
        definition = f.definition('synthetic_original_source_receipt')
        version = f.record(definition)['versionId']
        body = {**f.booking(key='synthetic_original_source_receipt', perspective='provider', fee='0', direction='receive'),
            'entitlementId': definition['entitlementId'], 'capacityVersionId': version}
        rejected(lambda: f.call(f"SELECT trading.synthetic_financial_bad_book_context('{f.catalog}',{literal(body)},"
            f"'{body['contextId']}','{f.valuation_at}','{sample_trade}');SET CONSTRAINTS ALL IMMEDIATE;", commit=False), 'gas_transport_financial_receipt')
    finally:
        f.book = old_book
    print('PASS independent fresh-booking seal rejects inconsistent original body context and actual certified financial source, without changing semantic retry coordinates.', flush=True)
