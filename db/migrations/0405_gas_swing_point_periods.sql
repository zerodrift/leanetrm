-- The selected swing source can consume a native day or month, uniquely covering its gas day.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$
DECLARE predecessor record;p pg_proc;
BEGIN
 FOR predecessor IN SELECT * FROM (VALUES
  ('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)','9fdf2dec9d3350bcfc7a0ad0fe68cdfa14f793f4d236ebe8da1ecafa3909936d'),
  ('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric,date[],timestamptz[])','dafd10b08d16657446764c27bd196a6b55099692675af9516bbbd537e6484a7b')
 )v(signature,digest) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(predecessor.signature);
  IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>predecessor.digest
   OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  THEN RAISE EXCEPTION 'gas_swing_period_upgrade: unexpected helper %',predecessor.signature;END IF;
 END LOOP;
END$guard$;

DO $shape$
DECLARE signature text='trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric,date[],timestamptz[]';
 original text=pg_get_functiondef((signature||')')::regprocedure); definition text=original;change record;
BEGIN
 IF to_regprocedure(signature||',text)') IS NOT NULL THEN RAISE EXCEPTION 'gas_swing_period_upgrade: unexpected overload';END IF;
 FOR change IN SELECT * FROM (VALUES
  ('charge_due timestamp with time zone[])','charge_due timestamp with time zone[], swing_source text)'),
  ('SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m',
   'BEGIN SELECT c.* INTO STRICT contract FROM market_data.contracts c JOIN market_data.curve_members m'),
  ($old$AND c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date;$old$,
   $new$AND ((c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date)
      OR (swing_source IS NOT NULL AND c.delivery_to=c.delivery_from+1))
 AND (swing_source IS NULL OR NOT EXISTS(SELECT FROM market_data.contract_intervals i
      WHERE(i.catalog_id,i.revision_id,i.contract_id)=(c.catalog_id,c.revision_id,c.id)));
 EXCEPTION WHEN no_data_found OR too_many_rows THEN
  IF swing_source IS NULL THEN RAISE;END IF;
  RAISE EXCEPTION 'gas_composition_input' USING DETAIL=jsonb_build_object('side',swing_source,'field','delivery_point')::text;
 END;$new$)
 )v(before_text,after_text) LOOP
  IF (length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>1 THEN RAISE EXCEPTION 'gas_swing_period_upgrade: shape patch mismatch';END IF;
  definition=replace(definition,change.before_text,change.after_text);
 END LOOP;
 EXECUTE definition;
 EXECUTE 'ALTER FUNCTION '||signature||',text) OWNER TO openetrm_security_owner';
 EXECUTE 'REVOKE ALL ON FUNCTION '||signature||',text) FROM PUBLIC';
 EXECUTE split_part(original,'AS $function$',1)||$wrapper$AS $function$
BEGIN
 PERFORM trading.require_gas_program_shape(catalog,schedule,slot,first_day,last_day,payment,unit_total,literal,assignment,delivery,start_at,forecast,unit_denominator,charge_ends,charge_due,NULL::text);
END$function$ $wrapper$;
END$shape$;

DO $writer$
DECLARE definition text=pg_get_functiondef('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure);
 original text=$old$PERFORM trading.require_gas_program_shape(catalog,schedule,0,first_day,last_day,end_at,unit_total,NULL,a,d,start_at,item->'forecastCurve');$old$;
BEGIN
 IF (length(definition)-length(replace(definition,original,'')))/length(original)<>1 THEN RAISE EXCEPTION 'gas_swing_period_upgrade: writer patch mismatch';END IF;
 EXECUTE replace(definition,original,$new$IF body->'swingRights'->0->>'side'=item->>'code' THEN
    PERFORM trading.require_gas_program_shape(catalog,schedule,0,first_day,last_day,end_at,unit_total,NULL,a,d,start_at,item->'forecastCurve',1,NULL,NULL,item->>'code');
   ELSE $new$||original||' END IF;');
END$writer$;
COMMIT;
