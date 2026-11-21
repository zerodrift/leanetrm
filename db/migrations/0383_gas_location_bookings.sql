-- One booking instruction owns one bilateral trade or two independent confirmations.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.gas_location_bookings(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,agreements text NOT NULL CHECK(agreements IN('one_agreement','separate_confirmations')),
 request_hash bytea NOT NULL CHECK(octet_length(request_hash)=32),
 purchase_operation uuid GENERATED ALWAYS AS
   (substr(encode(sha256(decode('gas_location_booking_child_v1:'||operation_id::text||':0','escape')),'hex'),1,32)::uuid) STORED,
 sale_operation uuid GENERATED ALWAYS AS
   (substr(encode(sha256(decode('gas_location_booking_child_v1:'||operation_id::text||CASE agreements WHEN 'one_agreement' THEN ':0' ELSE ':1' END,'escape')),'hex'),1,32)::uuid) STORED,
 PRIMARY KEY(catalog_id,operation_id),UNIQUE(catalog_id,purchase_operation),UNIQUE(catalog_id,sale_operation),
 FOREIGN KEY(catalog_id,purchase_operation) REFERENCES trading.gas_trade_revisions(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,sale_operation) REFERENCES trading.gas_trade_revisions(catalog_id,operation_id)
);
ALTER TABLE trading.gas_location_bookings OWNER TO openetrm_security_owner;
ALTER TABLE trading.gas_location_bookings ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_location_bookings FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON trading.gas_location_bookings TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY read_members ON trading.gas_location_bookings FOR SELECT TO openetrm_runtime USING(
 EXISTS(SELECT FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.operation_id)=(gas_location_bookings.catalog_id,purchase_operation))
 AND EXISTS(SELECT FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.operation_id)=(gas_location_bookings.catalog_id,sale_operation)));
GRANT SELECT(catalog_id,agreements,purchase_operation,sale_operation) ON trading.gas_location_bookings TO openetrm_runtime;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.gas_location_bookings FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- A command receipt must also be readable in the unpinned working write transaction.
-- Native status admits only the original actor. Historical business reads use the table's member policy.
CREATE FUNCTION security.gas_location_receipt(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b trading.gas_location_bookings;purchase jsonb;sale jsonb;result jsonb;
BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_location_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO b FROM trading.gas_location_bookings WHERE(catalog_id,operation_id)=(catalog,operation);
 IF NOT FOUND THEN RETURN NULL;END IF;
 purchase=security.gas_operation_status(catalog,b.purchase_operation);sale=security.gas_operation_status(catalog,b.sale_operation);
 IF NOT(purchase?'tradeId' AND sale?'tradeId') THEN RETURN NULL;END IF;
 IF payload IS NOT NULL AND b.request_hash<>sha256(convert_to(payload::text,'UTF8')) THEN RAISE EXCEPTION 'gas_location_conflict';END IF;
 SELECT jsonb_build_object('operationId',operation,'agreements',b.agreements,'purchaseTradeId',p.trade_id,'saleTradeId',s.trade_id,
    'purchaseReference',p.reference_number,'saleReference',s.reference_number) INTO STRICT result
 FROM trading.trade_references p,trading.trade_references s
 WHERE(p.catalog_id,p.trade_id)=(catalog,(purchase->>'tradeId')::uuid) AND(s.catalog_id,s.trade_id)=(catalog,(sale->>'tradeId')::uuid);
 RETURN result;
END$$;
ALTER FUNCTION security.gas_location_receipt(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_location_receipt(uuid,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_location_receipt(uuid,uuid,jsonb) TO openetrm_runtime;
-- The link may be created only by the same transaction that publishes both native trades.
CREATE FUNCTION security.record_gas_location_booking(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE mode text=payload->>'agreements';terms jsonb=payload->'terms';leg jsonb;endpoint text;physical jsonb;cash jsonb;
 child uuid;receipt jsonb;endpoints text[];locations jsonb[]='{}';i integer;common_at timestamptz;leg_at timestamptz;
BEGIN
 IF security.trading_access(catalog,'BookTrade') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR NOT security.current_write_context()
 THEN RAISE EXCEPTION 'gas_location_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'gas_location_input';END IF;
 receipt=security.gas_location_receipt(catalog,operation,payload);IF receipt IS NOT NULL THEN RETURN receipt;END IF;
 IF jsonb_typeof(payload) IS DISTINCT FROM 'object' OR (payload-'agreements'-'terms')<>'{}'::jsonb OR mode IS NULL OR mode NOT IN('one_agreement','separate_confirmations') OR jsonb_typeof(terms) IS DISTINCT FROM 'array'
   OR jsonb_array_length(terms)<>(CASE WHEN mode='one_agreement' THEN 1 ELSE 2 END) THEN RAISE EXCEPTION 'gas_location_input';END IF;
 FOR i IN 0..jsonb_array_length(terms)-1 LOOP
  leg=terms->i;child=substr(encode(sha256(decode('gas_location_booking_child_v1:'||operation::text||':'||i::text,'escape')),'hex'),1,32)::uuid;
  IF jsonb_typeof(leg->'snapshotId') IS DISTINCT FROM 'string' OR NOT pg_input_is_valid(leg->>'snapshotId','uuid')
    OR (leg->>'snapshotId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
    OR jsonb_typeof(leg->'definitionRevisionId') IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_location_input';END IF;
  SELECT r.effective_at INTO leg_at FROM trading.gas_trade_revisions r JOIN trading.publications pub USING(catalog_id,publication_order)
    JOIN trading.side_schedule_requests q ON(q.catalog_id,q.request_id,q.actor_id)=(r.catalog_id,r.operation_id,r.actor_id)
    WHERE(r.catalog_id,r.operation_id,r.revision)=(catalog,child,1) AND pub.publication_xid=pg_current_xact_id() AND q.permission='BookTrade'
      AND q.command_hash=sha256(convert_to(leg::text,'UTF8')) AND security.gas_operation_status(catalog,child)?'tradeId';
  IF leg_at IS NULL OR (leg->>'operationId')::uuid IS DISTINCT FROM child
    OR leg->>'snapshotId' IS DISTINCT FROM terms->0->>'snapshotId' OR leg->>'definitionRevisionId' IS DISTINCT FROM terms->0->>'definitionRevisionId'
    OR (i>0 AND leg_at IS DISTINCT FROM common_at) THEN RAISE EXCEPTION 'gas_location_conflict';END IF;
  common_at=leg_at;
  endpoints=CASE WHEN mode='one_agreement' THEN ARRAY['purchase','sale'] WHEN i=0 THEN ARRAY['purchase'] ELSE ARRAY['sale'] END;
  IF jsonb_array_length(leg->'physicalSides')<>cardinality(endpoints) OR jsonb_array_length(leg->'cashSides')<>cardinality(endpoints)
    THEN RAISE EXCEPTION 'gas_location_input';END IF;
  FOREACH endpoint IN ARRAY endpoints LOOP
   SELECT value INTO physical FROM jsonb_array_elements(leg->'physicalSides') WHERE value->>'code'=endpoint;
   SELECT value INTO cash FROM jsonb_array_elements(leg->'cashSides') WHERE value->>'code'=endpoint||'_payment';
   IF physical IS NULL OR cash IS NULL OR physical->>'direction' IS DISTINCT FROM (CASE endpoint WHEN 'purchase' THEN 'receive' ELSE 'deliver' END)
     OR cash->>'direction' IS DISTINCT FROM (CASE endpoint WHEN 'purchase' THEN 'pay' ELSE 'receive' END)
     OR cash->>'source' IS DISTINCT FROM endpoint OR cash->>'role' NOT IN('consideration','indexed_consideration') THEN RAISE EXCEPTION 'gas_location_input';END IF;
   locations=array_append(locations,jsonb_build_array(physical->>'pipeline',physical->>'zone',physical->>'deliveryPoint'));
  END LOOP;
 END LOOP;
 IF locations[1]=locations[2] THEN RAISE EXCEPTION 'gas_location_input';END IF;
 INSERT INTO trading.gas_location_bookings(catalog_id,operation_id,agreements,request_hash)
 VALUES(catalog,operation,mode,sha256(convert_to(payload::text,'UTF8')));
 RETURN security.gas_location_receipt(catalog,operation,payload);
END$$;
ALTER FUNCTION security.record_gas_location_booking(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.record_gas_location_booking(uuid,uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.record_gas_location_booking(uuid,uuid,jsonb) TO openetrm_runtime;

COMMIT;
