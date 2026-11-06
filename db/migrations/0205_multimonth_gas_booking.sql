-- A physical Side retains its whole term and each exact native monthly price period.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE helper record;BEGIN
 FOR helper IN SELECT * FROM(VALUES
  ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)','280d25fb39de483cc16f52c2ee9625793cced511664f423eac52d3413944292e','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb),
  ('security.book_gas_composition(uuid,jsonb,timestamp with time zone)','49b4beeb36d677c912680b7091c7d0f00e5b4c091aa510787fbb3fa7fc738015','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb),
  ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone)','18fa6dcbd332be1d6a8c29dcd511c5762f8ed13e860e205b9f5e03704f2649e3','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog", "jit=off"]'::jsonb)
 )v(signature,hash,acl,config) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND p.proowner='openetrm_security_owner'::regrole AND coalesce(to_jsonb(p.proacl),'null'::jsonb)=helper.acl
   AND coalesce(to_jsonb(p.proconfig),'null'::jsonb)=helper.config
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash)
  THEN RAISE EXCEPTION 'gas_term_upgrade: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='trading.gas_component_terms'::regclass AND conname='gas_component_terms_check1' AND convalidated AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='281254dcbef6695311447f2c73f7a3f8b85837fc8a27e57f5723a0a5630ea302') THEN RAISE EXCEPTION 'gas_term_upgrade: unexpected constraint trading.gas_component_terms' USING ERRCODE='55000';END IF;END$$;
ALTER TABLE trading.gas_component_terms DROP CONSTRAINT gas_component_terms_check1;
ALTER TABLE trading.gas_component_terms ADD CONSTRAINT gas_component_terms_check1 CHECK (((role <> 'physical'::text) OR ((num_nulls(pipeline_code, zone_code, point_code, product_code, delivery_from, delivery_to, quantity_basis) = 0) AND (delivery_from < delivery_to) AND ((delivery_to - delivery_from) <= 1200) AND (quantity_basis = ANY (ARRAY['period_total_equal_days'::text, 'per_operating_day'::text])) AND (stage IS NULL) AND (price IS NULL) AND (tariff_code IS NULL) AND (payment_at IS NULL) AND (((source_slot IS NULL) AND (independent_quantity IS NOT NULL) AND (formula IS NULL) AND (fraction IS NULL)) OR ((source_slot IS NOT NULL) AND (independent_quantity IS NULL) AND (formula IS NOT NULL) AND (fraction IS NOT NULL))))));

-- Preserve the reviewed validator's unit, geometry, lifecycle, edge and root checks.
-- Only its exact native source proof changes from one whole-term contract to one
-- qualified monthly contract for each retained daily price row.
DO $patch$ DECLARE definition text=pg_get_functiondef('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)'::regprocedure);change record;BEGIN
 FOR change IN SELECT * FROM(VALUES
($old$DECLARE days integer=last_day-first_day;$old$,$new$DECLARE market_row trading.side_schedule_rows;days integer=last_day-first_day;$new$),
($old$ forecast=NULLIF(forecast,'null'::jsonb);$old$,$new$ IF days<1 OR days>1200 THEN RAISE EXCEPTION 'gas_schedule_geometry';END IF;
 forecast=NULLIF(forecast,'null'::jsonb);$new$),
($old$ SELECT * INTO STRICT target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(assignment.reference_catalog_id,assignment.reference_revision_id,curve.settlement_target_code);
 IF target.kind<>'index' OR target.index_b_code IS NOT NULL OR quote.kind<>'outright' OR curve.commodity_code IS DISTINCT FROM assignment.product_code THEN RAISE EXCEPTION 'gas_schedule_sources';END IF;
$old$,$new$$new$),
($old$ SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m$old$,$new$ SELECT * INTO STRICT target FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code)=(assignment.reference_catalog_id,assignment.reference_revision_id,curve.settlement_target_code);
 IF target.kind<>'index' OR target.index_b_code IS NOT NULL OR quote.kind<>'outright' OR curve.commodity_code IS DISTINCT FROM assignment.product_code THEN RAISE EXCEPTION 'gas_schedule_sources';END IF;
 IF slot=0 THEN
 FOR market_row IN SELECT * FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule) AND operation IN('price_lookup','curve_price_lookup') LOOP
 SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m$new$),
($old$AND c.delivery_from<=first_day AND c.delivery_to>=last_day$old$,$new$AND c.delivery_from<=first_day+split_part(market_row.local_key,'/',2)::integer AND c.delivery_to>=first_day+split_part(market_row.local_key,'/',2)::integer+1$new$),
($old$WHERE(catalog_id,schedule_id,kind)=(catalog,schedule,'gas_assignment') AND coordinate::jsonb$old$,$new$WHERE(catalog_id,schedule_id,kind)=(catalog,schedule,'gas_assignment') AND row_ordinal=market_row.ordinal AND coordinate::jsonb$new$),
($old$WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND r.operation IN('price_lookup','curve_price_lookup') AND(
   (r.observation_from$old$,$new$WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND r.ordinal=market_row.ordinal AND r.operation IN('price_lookup','curve_price_lookup') AND(
   (r.observation_from$new$),
($old$WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND CASE b.port$old$,$new$WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND b.row_ordinal=market_row.ordinal AND CASE b.port$new$),
($old$ IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND(
  r.output_kind IN('price','quantity')$old$,$new$ END LOOP;
 END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND(
  r.output_kind IN('price','quantity')$new$)
 )v(before,after) LOOP
  IF (length(definition)-length(replace(definition,change.before,'')))/length(change.before)<>1
   THEN RAISE EXCEPTION 'gas_term_upgrade: unexpected source validator' USING ERRCODE='55000';END IF;
  definition=replace(definition,change.before,change.after);
 END LOOP;
 EXECUTE definition;
END$patch$;
-- The private legacy booking ABI retains its original single-month admission.
-- Current composition physical booking supplies the explicit forecast argument.
CREATE OR REPLACE FUNCTION trading.require_gas_program_shape(catalog uuid, schedule uuid, slot integer, first_day date, last_day date, payment timestamp with time zone, unit_total numeric, literal numeric, assignment market_data.gas_valuation_assignments, delivery ref_data.gas_delivery_conventions, start_at timestamp with time zone)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$BEGIN
 IF slot=0 AND date_trunc('month',first_day) IS DISTINCT FROM date_trunc('month',last_day-1) THEN RAISE EXCEPTION 'gas_schedule_geometry';END IF;
 PERFORM trading.require_gas_program_shape(catalog,schedule,slot,first_day,last_day,payment,unit_total,literal,assignment,delivery,start_at,NULL);
END$$;
DO $patch$ DECLARE definition text=pg_get_functiondef('security.book_gas_composition(uuid,jsonb,timestamp with time zone)'::regprocedure);BEGIN
 IF (length(definition)-length(replace(definition,'last_day-first_day>31','')))/length('last_day-first_day>31')<>1 THEN RAISE EXCEPTION 'gas_term_upgrade: unexpected booking bound' USING ERRCODE='55000';END IF;
 EXECUTE replace(definition,'last_day-first_day>31','last_day-first_day>1200');
END$patch$;
COMMIT;
