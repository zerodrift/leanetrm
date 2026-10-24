-- The mandatory temporal policy calls the same fresh reference-access guard.
-- Keep that restrictive barrier, including under additional permissive policies,
-- and avoid evaluating the identical access predicate twice for every row.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $$BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
END$$;
DO $$
DECLARE name text; revision_column text;
 names constant text[]:=ARRAY[
  'calendar_days','calendars','cash_rounding_conventions','clearing_houses','clock_rules',
  'commodities','commodity_locations','commodity_units','currencies','delivery_profiles','dimensions',
  'exchange_clearing_houses','exchanges','futures_execution_admissions','futures_milestones',
  'futures_products','futures_series','imported_product_keys','imported_series_keys',
  'interest_rate_definitions','location_types','locations','measurement_bases','price_index_definitions',
  'product_evidence','quantity_specifications','quote_specifications','reference_import_receipts',
  'revisions','settlement_targets','source_documents','sources','unit_conversions','units','value_specifications'];
BEGIN
 -- Hold the complete declared policy set stable before validating any of it.
 -- This also fences ordinary writes while their SELECT-policy composition changes.
 FOREACH name IN ARRAY names LOOP
  EXECUTE format('LOCK TABLE ref_data.%I IN SHARE ROW EXCLUSIVE MODE',name);
 END LOOP;
 -- This exact helper checks current reference authority before either its
 -- unbound return or its certified historical lookup. It remains VOLATILE.
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid='security.reference_version_visible(uuid,uuid)'::regprocedure
  AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.provolatile='v' AND p.prosecdef AND NOT p.proisstrict
  AND p.proconfig @> ARRAY['search_path=pg_catalog','plan_cache_mode=force_generic_plan'] AND cardinality(p.proconfig)=2
  AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']::aclitem[]
  AND cardinality(p.proacl)=2
  AND encode(sha256(convert_to(p.prosrc,'UTF8')),'hex')='77b21522ab7348fec339917de6c81c05f82bc11ae0ab374ecddb7a662a078e87')
 THEN RAISE EXCEPTION 'reference_read_authority: expected fresh visibility helper is unavailable' USING ERRCODE='55000';END IF;
 FOREACH name IN ARRAY names LOOP
  revision_column=CASE name WHEN 'revisions' THEN 'id' ELSE 'revision_id' END;
  IF NOT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
   JOIN pg_policy access ON access.polrelid=c.oid AND access.polname='scoped_read'
   JOIN pg_policy temporal ON temporal.polrelid=c.oid AND temporal.polname='temporal_read'
   WHERE n.nspname='ref_data' AND c.relname=name AND c.relrowsecurity AND c.relforcerowsecurity
    AND access.polcmd='r' AND access.polpermissive AND access.polroles=ARRAY['openetrm_runtime'::regrole::oid]
    AND pg_get_expr(access.polqual,c.oid)='security.reference_access(catalog_id, ''ReadMarketData''::text)' AND access.polwithcheck IS NULL
    AND temporal.polcmd='r' AND NOT temporal.polpermissive AND temporal.polroles=access.polroles
    AND pg_get_expr(temporal.polqual,c.oid)=format('security.reference_version_visible(catalog_id, %s)',revision_column)
    AND temporal.polwithcheck IS NULL)
  THEN RAISE EXCEPTION 'reference_read_authority: unexpected policies on ref_data.%',name USING ERRCODE='55000';END IF;
 END LOOP;
 -- Only these permissive SELECT expressions change. The restrictive temporal
 -- and quantity policies, role targets, write rules and grants stay in place.
 FOREACH name IN ARRAY names LOOP
  EXECUTE format('ALTER POLICY scoped_read ON ref_data.%I USING(true)',name);
 END LOOP;
END$$;
COMMIT;
