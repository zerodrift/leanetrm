-- Calculation artifacts retain selected inputs. They do not post, settle or
-- correct financial obligations, and never update earlier calculation results.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.swap_valuation_receipts (
 catalog_id uuid NOT NULL,id uuid NOT NULL,context_id uuid NOT NULL,snapshot_id uuid NOT NULL,
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 quantity_code ref_data.code NOT NULL,currency_code ref_data.code NOT NULL,quantity ref_data.exact_number NOT NULL CHECK(quantity>0),
 valuation_at ref_data.instant NOT NULL,terms_digest text NOT NULL CHECK(terms_digest ~ '^[0-9a-f]{64}$'),
 input_digest text NOT NULL CHECK(input_digest ~ '^[0-9a-f]{64}$'),numerical_policy text NOT NULL,
 result_schema text NOT NULL CHECK(result_schema IN('fixing_aware_swap_valuation_v1','fixing_aware_swap_risk_v1')),contract_terms jsonb NOT NULL CHECK(jsonb_typeof(contract_terms)='object'),
 result jsonb NOT NULL CHECK(jsonb_typeof(result)='object'),created_at ref_data.instant NOT NULL,actor_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,id),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.market_contexts(catalog_id,context_id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(actor_id) REFERENCES security.principals(id)
);
CREATE TABLE trading.swap_valuation_fixing_pins (
 catalog_id uuid NOT NULL,receipt_id uuid NOT NULL,side_slot integer NOT NULL CHECK(side_slot IN(0,1)),payment_ordinal integer NOT NULL CHECK(payment_ordinal>=0),
 reset_ordinal integer NOT NULL CHECK(reset_ordinal>=0),lineage_revision integer NOT NULL CHECK(lineage_revision>0),fixing_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,receipt_id,side_slot,payment_ordinal,reset_ordinal,lineage_revision),
 FOREIGN KEY(catalog_id,receipt_id) REFERENCES trading.swap_valuation_receipts(catalog_id,id),
 FOREIGN KEY(catalog_id,fixing_id) REFERENCES market_data.index_fixings(catalog_id,id)
);
CREATE TABLE trading.swap_valuation_market_pins (
 catalog_id uuid NOT NULL,receipt_id uuid NOT NULL,definition_revision_id uuid NOT NULL,series_id uuid NOT NULL,input_use text NOT NULL CHECK(input_use IN('Variable','FrozenFact')),observation_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,receipt_id,series_id,input_use),FOREIGN KEY(catalog_id,receipt_id) REFERENCES trading.swap_valuation_receipts(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,series_id) REFERENCES market_data.series(catalog_id,revision_id,id),
 FOREIGN KEY(catalog_id,observation_id,definition_revision_id) REFERENCES market_data.observations(catalog_id,id,definition_revision_id)
);
CREATE FUNCTION security.swap_valuation_access(catalog uuid,receipt uuid) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.trading_access(catalog,'ReadTrades') AND security.market_access(catalog,'ReadMarketData') AND EXISTS(
  SELECT 1 FROM trading.swap_valuation_receipts r WHERE(r.catalog_id,r.id)=(catalog,receipt) AND security.reference_access(r.reference_catalog_id,'ReadMarketData') AND
   NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL AND(c.market_catalog_id,c.market_context_id) IS DISTINCT FROM(r.catalog_id,r.context_id))
 );
$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_valuation_receipts','swap_valuation_fixing_pins','swap_valuation_market_pins'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_result BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(security.swap_valuation_access(catalog_id,%I))',t,CASE WHEN t='swap_valuation_receipts' THEN 'id' ELSE 'receipt_id' END);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END $$;
CREATE FUNCTION security.retain_swap_valuation(catalog uuid,context uuid,snapshot uuid,definition uuid,ref_catalog uuid,ref_revision uuid,quantity_code text,currency_code text,amount numeric,
 valuation timestamptz,terms_digest text,input_digest text,numerical_policy text,terms jsonb,result jsonb,fixings jsonb,market_pins jsonb,schema_name text)
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE token uuid;actor uuid;effective date;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(ref_catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current swap calculation access' USING ERRCODE='42501';END IF;
 SELECT c.actor_id INTO actor FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 SELECT t.effective_on INTO effective FROM market_data.temporal_contexts t JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(catalog,context);
 IF schema_name NOT IN('fixing_aware_swap_valuation_v1','fixing_aware_swap_risk_v1') OR actor IS NULL OR effective IS NULL OR(valuation AT TIME ZONE 'UTC')::date<>effective OR octet_length(result::text)>2097152 OR octet_length(terms::text)>524288 OR
  jsonb_typeof(fixings) IS DISTINCT FROM 'array' OR jsonb_typeof(market_pins) IS DISTINCT FROM 'array' OR jsonb_array_length(fixings)>8192 OR jsonb_array_length(market_pins)>8192 THEN RAISE EXCEPTION 'index_fixing_receipt';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_to_recordset(fixings)p("fixingId" uuid,"revision" integer) WHERE NOT EXISTS(
  SELECT 1 FROM security.read_index_fixings_at(catalog,context)f WHERE f.id=p."fixingId" AND f.revision=p."revision" AND f.published_at<=valuation)) THEN RAISE EXCEPTION 'index_fixing_receipt';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_to_recordset(market_pins)p("seriesId" uuid,"observationId" uuid) WHERE NOT EXISTS(
  SELECT 1 FROM market_data.observations o WHERE(o.catalog_id,o.id,o.definition_revision_id,o.series_id)=(catalog,p."observationId",definition,p."seriesId") AND security.market_fact_visible(catalog,definition,o.id) AND o.observed_at<=valuation)) THEN RAISE EXCEPTION 'index_fixing_receipt';END IF;
 token=gen_random_uuid();
 INSERT INTO trading.swap_valuation_receipts VALUES(catalog,token,context,snapshot,definition,ref_catalog,ref_revision,quantity_code,currency_code,amount,valuation,terms_digest,input_digest,numerical_policy,schema_name,terms,result,clock_timestamp(),actor);
 INSERT INTO trading.swap_valuation_fixing_pins SELECT catalog,token,p."sideSlot",p."paymentOrdinal",p."resetOrdinal",p.revision,p."fixingId" FROM jsonb_to_recordset(fixings)p("sideSlot" integer,"paymentOrdinal" integer,"resetOrdinal" integer,"revision" integer,"fixingId" uuid);
 INSERT INTO trading.swap_valuation_market_pins SELECT catalog,token,definition,p."seriesId",p."inputUse",p."observationId" FROM jsonb_to_recordset(market_pins)p("seriesId" uuid,"inputUse" text,"observationId" uuid);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(ref_catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Swap calculation access expired' USING ERRCODE='42501';END IF;
 RETURN token;
END $$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('swap_valuation_access','retain_swap_valuation') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
 END LOOP;
END $$;
COMMIT;
