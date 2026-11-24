-- Exact payment entry terms and named index admission; retained commands are unchanged.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

ALTER TABLE trading.gas_component_terms
 ADD COLUMN share_n numeric, ADD COLUMN share_d numeric,
 ADD COLUMN part_kind text, ADD COLUMN part_lower_n numeric, ADD COLUMN part_lower_d numeric,
 ADD COLUMN part_upper_n numeric, ADD COLUMN part_upper_d numeric;
ALTER TABLE trading.gas_component_terms DROP CONSTRAINT gas_component_terms_check2;
DO $guard$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='trading.gas_component_terms'::regclass AND tgname='immutable_component'
  AND tgenabled='O' AND tgfoid='trading.guard_gas_component()'::regprocedure AND NOT tgisinternal AND
  pg_get_triggerdef(oid)='CREATE TRIGGER immutable_component BEFORE INSERT OR DELETE OR UPDATE ON trading.gas_component_terms FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_component()')
  OR encode(sha256(convert_to(pg_get_functiondef('trading.guard_gas_component()'::regprocedure),'UTF8')),'hex')<>'0da1d55a6a190fb503768b86b07bcb4d5b6af933354094c74abab93304c4787b'
 THEN RAISE EXCEPTION 'gas_pricing_upgrade: unexpected component guard';END IF;
END$guard$;
ALTER TABLE trading.gas_component_terms DISABLE TRIGGER immutable_component;
UPDATE trading.gas_component_terms p SET share_n=1,share_d=1,delivery_from=s.delivery_from,delivery_to=s.delivery_to
 FROM trading.gas_component_terms s
 WHERE(p.catalog_id,p.instrument_id,p.source_slot)=(s.catalog_id,s.instrument_id,s.slot) AND p.role<>'physical';
ALTER TABLE trading.gas_component_terms ENABLE TRIGGER immutable_component;
ALTER TABLE trading.gas_component_terms ADD CONSTRAINT gas_component_terms_check2 CHECK((role='physical' OR
 (source_slot IS NOT NULL AND stage IS NOT NULL AND formula IS NULL AND fraction IS NULL AND
 num_nonnulls(pipeline_code,zone_code,point_code,product_code,quantity_basis,independent_quantity)=0 AND
 isfinite(delivery_from) AND isfinite(delivery_to) AND delivery_from<delivery_to AND
 CASE WHEN role='indexed_consideration' THEN price IS NULL AND tariff_code IS NULL AND payment_at IS NULL ELSE price IS NOT NULL AND isfinite(payment_at) END)) IS TRUE);
ALTER TABLE trading.gas_component_terms ADD CONSTRAINT gas_payment_entry_terms CHECK((
 CASE WHEN role='physical' THEN num_nonnulls(share_n,share_d,part_kind,part_lower_n,part_lower_d,part_upper_n,part_upper_d)=0
 ELSE share_n>0 AND share_d>0 AND share_n<1e120 AND share_d<1e120 AND scale(share_n)=0 AND scale(share_d)=0 AND gcd(share_n,share_d)=1 AND
 CASE WHEN part_kind IS NULL THEN num_nonnulls(part_lower_n,part_lower_d,part_upper_n,part_upper_d)=0
 ELSE part_kind IN('band','shortfall') AND part_lower_n>=0 AND part_lower_n<1e120 AND part_lower_d>0 AND part_lower_d<1e120
 AND scale(part_lower_n)=0 AND scale(part_lower_d)=0 AND gcd(part_lower_n,part_lower_d)=1 AND
 CASE WHEN part_kind='shortfall' THEN part_upper_n IS NULL AND part_upper_d IS NULL
 WHEN part_upper_n IS NULL THEN part_upper_d IS NULL
 ELSE part_upper_d>0 AND part_upper_n<1e120 AND part_upper_d<1e120 AND scale(part_upper_n)=0 AND scale(part_upper_d)=0 AND gcd(part_upper_n,part_upper_d)=1
 AND part_upper_n*part_lower_d>part_lower_n*part_upper_d END END END) IS TRUE);
ALTER TABLE trading.gas_index_cash_terms DROP CONSTRAINT gas_index_cash_terms_observation_convention_check;
ALTER TABLE trading.gas_index_cash_terms ADD CONSTRAINT gas_index_cash_terms_observation_convention_check CHECK(
 (observation_convention IN('daily_published_index_v1','named_index_daily_v1','named_index_monthly_v1') AND index_code IS NOT NULL) OR
 (observation_convention='point_publications_v1' AND index_code IS NULL));

DO $exact$
DECLARE p pg_proc;definition text;original text;old_text text;new_text text;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamp with time zone,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamp with time zone,jsonb)'::regprocedure;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition,'UTF8')),'hex')<>'d11ba9426a1f08e5b083d108b05bd4616be8f1a8c32a11e7bf64fc4a86a19d49'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_pricing_upgrade: unexpected predecessor %',p.oid::regprocedure;END IF;
 IF to_regprocedure('trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric)') IS NOT NULL THEN RAISE EXCEPTION 'gas_pricing_upgrade: unexpected exact core';END IF;
 original=definition;
 FOR old_text,new_text IN SELECT * FROM(VALUES
  ($old$forecast jsonb)$old$,$new$forecast jsonb, unit_denominator numeric)$new$),
  ($old$IF days<1 OR days>1200$old$,$new$IF unit_denominator IS NULL OR unit_denominator<=0 OR unit_denominator>=1e120 OR scale(unit_denominator)<>0 OR days<1 OR days>1200$new$),
  ($old$(row.parameters->>'numerator')::numeric*(CASE WHEN slot=0 THEN days ELSE 1 END)$old$,$new$(row.parameters->>'numerator')::numeric*unit_denominator*(CASE WHEN slot=0 THEN days ELSE 1 END)$new$))changes(a,b) LOOP
  IF (length(definition)-length(replace(definition,old_text,'')))/length(old_text)<>1 THEN RAISE EXCEPTION 'gas_pricing_upgrade: exact core patch mismatch';END IF;
  definition=replace(definition,old_text,new_text);
 END LOOP;
 EXECUTE definition;
 ALTER FUNCTION trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric) OWNER TO openetrm_security_owner;
 REVOKE ALL ON FUNCTION trading.require_gas_program_shape(uuid,uuid,integer,date,date,timestamptz,numeric,numeric,market_data.gas_valuation_assignments,ref_data.gas_delivery_conventions,timestamptz,jsonb,numeric) FROM PUBLIC;
 EXECUTE split_part(original,'AS $function$',1)||$wrapper$AS $function$
BEGIN
 PERFORM trading.require_gas_program_shape(catalog,schedule,slot,first_day,last_day,payment,unit_total,literal,assignment,delivery,start_at,forecast,1);
END$function$ $wrapper$;
END$exact$;

DO $patch$
DECLARE p pg_proc;definition text;old_text text;new_text text;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='security.write_gas_composition(uuid,jsonb,timestamp with time zone,boolean)'::regprocedure;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition,'UTF8')),'hex')<>'663641ba3c5f35f02570f2823e84c7e57ef184388fb2335c16cdd35ca13ed12e'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_pricing_upgrade: unexpected predecessor %',p.oid::regprocedure;END IF;
 FOR old_text,new_text IN SELECT * FROM(VALUES
  ($old$BEGIN
 permission=$old$,
   $new$share numeric;sn numeric;sd numeric;part text;ln numeric;ld numeric;un numeric;ud numeric;level numeric;unit_den numeric;
BEGIN
 permission=$new$),
  ($old$jsonb_array_length(cash)>4$old$,
   $new$jsonb_array_length(cash)>7 OR jsonb_array_length(physical)+jsonb_array_length(cash)>8$new$),
  ($old$'paymentAt','indexed','calendarPolicy'))$old$,
   $new$'paymentAt','indexed','calendarPolicy','share','part','deliveryFrom','deliveryTo'))$new$),
  ($old$first_day=source_term.delivery_from;last_day=source_term.delivery_to;$old$,
   $new$first_day=coalesce((item->>'deliveryFrom')::date,source_term.delivery_from);last_day=coalesce((item->>'deliveryTo')::date,source_term.delivery_to);
  IF NOT isfinite(first_day) OR NOT isfinite(last_day) OR first_day<source_term.delivery_from OR last_day>source_term.delivery_to OR first_day>=last_day
   OR item ? 'deliveryFrom' AND jsonb_typeof(item->'deliveryFrom') IS DISTINCT FROM 'string'
   OR item ? 'deliveryTo' AND jsonb_typeof(item->'deliveryTo') IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  IF jsonb_typeof(item->'share') IS DISTINCT FROM 'string' OR item->>'share' !~ '^(0|[1-9][0-9]*)([.][0-9]{1,18})?$' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  share=(item->>'share')::numeric;
  IF share<=0 OR share>=1e24 OR scale(share)>18 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
  sn=share*1e18;sd=1e18;g=gcd(sn,sd);sn=trim_scale(div(sn,g));sd=trim_scale(div(sd,g));
  part=NULL;ln=NULL;ld=NULL;un=NULL;ud=NULL;
  IF item ? 'part' THEN
   IF jsonb_typeof(item->'part') IS DISTINCT FROM 'object' OR NOT(item->'part' ?& ARRAY['kind','lower','upper']) OR
    EXISTS(SELECT 1 FROM jsonb_object_keys(item->'part') k WHERE k NOT IN('kind','lower','upper')) OR
    item#>>'{part,kind}' IS NULL OR item#>>'{part,kind}' NOT IN('band','shortfall') OR source_term.source_slot IS NOT NULL OR
    EXISTS(SELECT 1 FROM jsonb_array_elements(physical) x WHERE x->'link' IS DISTINCT FROM 'null'::jsonb) THEN RAISE EXCEPTION 'gas_composition_input';END IF;
   part=item#>>'{part,kind}';
   IF jsonb_typeof(item#>'{part,lower}') IS DISTINCT FROM 'string' OR item#>>'{part,lower}' !~ '^(0|[1-9][0-9]*)([.][0-9]{1,18})?$' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
   level=(item#>>'{part,lower}')::numeric;
   IF level<0 OR level>=1e24 OR scale(level)>18 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
   ln=level*1e18;ld=base*1e18;g=gcd(ln,ld);ln=trim_scale(div(ln,g));ld=trim_scale(div(ld,g));
   IF item#>'{part,upper}' IS DISTINCT FROM 'null'::jsonb THEN
    IF part<>'band' OR jsonb_typeof(item#>'{part,upper}') IS DISTINCT FROM 'string' OR item#>>'{part,upper}' !~ '^(0|[1-9][0-9]*)([.][0-9]{1,18})?$' THEN RAISE EXCEPTION 'gas_composition_input';END IF;
    level=(item#>>'{part,upper}')::numeric;
    IF level<0 OR level>=1e24 OR scale(level)>18 THEN RAISE EXCEPTION 'gas_composition_input';END IF;
    un=level*1e18;ud=base*1e18;g=gcd(un,ud);un=trim_scale(div(un,g));ud=trim_scale(div(ud,g));
    IF un*ld<=ln*ud THEN RAISE EXCEPTION 'gas_composition_input';END IF;
   END IF;
  END IF;$new$),
  ($old$unit_total=CASE source_term.quantity_basis WHEN 'per_operating_day' THEN last_day-first_day ELSE 1 END;
  IF item->>'role'='indexed_consideration' THEN PERFORM trading.require_gas_index_cash_shape(catalog,schedule,source_term,a,item->'indexed');
  ELSE PERFORM trading.require_gas_program_shape(catalog,schedule,1,first_day,last_day,payment,unit_total,price,a,d,start_at);END IF;$old$,
   $new$rn=rn*sn;rd=rd*sd;g=gcd(rn,rd);rn=trim_scale(div(rn,g));rd=trim_scale(div(rd,g));
  unit_total=last_day-first_day;unit_den=CASE source_term.quantity_basis WHEN 'per_operating_day' THEN 1 ELSE source_term.delivery_to-source_term.delivery_from END;
  IF item->>'role'='indexed_consideration' THEN
   IF EXISTS(SELECT 1 FROM jsonb_object_keys(item->'indexed') k WHERE k NOT IN('index','observationConvention','paymentConvention','paymentDay','spread','forecastCurve')) THEN RAISE EXCEPTION 'gas_index_terms';END IF;
   PERFORM trading.require_gas_index_cash_shape(catalog,schedule,source_term,a,item->'indexed'||jsonb_build_object('deliveryFrom',first_day,'deliveryTo',last_day));
  ELSE PERFORM trading.require_gas_program_shape(catalog,schedule,1,first_day,last_day,payment,unit_total,price,a,d,start_at,NULL,unit_den);END IF;$new$),
  ($old$definition_revision_id,price,tariff_code,payment_at)$old$,
   $new$definition_revision_id,price,tariff_code,payment_at,delivery_from,delivery_to,share_n,share_d,part_kind,part_lower_n,part_lower_d,part_upper_n,part_upper_d)$new$),
  ($old$a.code,definition,price,item->>'tariffCode',payment);$old$,
   $new$a.code,definition,price,item->>'tariffCode',payment,first_day,last_day,sn,sd,part,ln,ld,un,ud);$new$)
 )changes(a,b) LOOP
  IF (length(definition)-length(replace(definition,old_text,'')))/length(old_text)<>1 THEN RAISE EXCEPTION 'gas_pricing_upgrade: patch mismatch';END IF;
  definition=replace(definition,old_text,new_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

DO $patch$
DECLARE p pg_proc;definition text;old_text text;new_text text;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='trading.require_gas_index_cash_shape(uuid,uuid,trading.gas_component_terms,market_data.gas_valuation_assignments,jsonb)'::regprocedure;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition,'UTF8')),'hex')<>'e2e83e4b4afa3cf4764356d34d39a64cb4109842da80fafe8211726e8b320763'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_pricing_upgrade: unexpected predecessor %',p.oid::regprocedure;END IF;
 FOR old_text,new_text IN SELECT * FROM(VALUES
  ($old$days integer=source.delivery_to-source.delivery_from;$old$,
   $new$first_day date=coalesce((terms->>'deliveryFrom')::date,source.delivery_from);last_day date=coalesce((terms->>'deliveryTo')::date,source.delivery_to);days integer=last_day-first_day;$new$),
  ($old$'paymentDay','spread','forecastCurve'))$old$,
   $new$'paymentDay','spread','forecastCurve','deliveryFrom','deliveryTo'))$new$),
  ($old$NOT IN('daily_published_index_v1','point_publications_v1')$old$,
   $new$NOT IN('named_index_daily_v1','named_index_monthly_v1','point_publications_v1')$new$),
  ($old$OR days NOT BETWEEN 1 AND 1200$old$,
   $new$OR days NOT BETWEEN 1 AND 1200 OR first_day<source.delivery_from OR last_day>source.delivery_to$new$),
  ($old$false,source.delivery_from,source.delivery_to,a,delivery$old$,
   $new$false,first_day,last_day,a,delivery$new$),
  ($old$ ELSE
  IF a.setup_id IS NOT NULL AND EXISTS(SELECT 1 FROM market_data.gas_point_index_periods ip WHERE(ip.catalog_id,ip.setup_id,ip.pipeline_code,ip.zone_code,ip.point_code,ip.product_code)=(catalog,a.setup_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)) THEN RAISE EXCEPTION 'gas_location_index_conflict';END IF;
  IF target.kind<>'index' OR terms->>'index' IS DISTINCT FROM target.index_a_code::text THEN RAISE EXCEPTION 'gas_index_terms';END IF;
  custom=CASE WHEN terms->'forecastCurve'='null'::jsonb THEN NULL ELSE security.validate_custom_projection(catalog,terms->'forecastCurve') END;
 END IF;
 mapping.curve_code=a.curve_code;mapping.index_code=target.index_a_code;mapping.observation_convention='daily_published_index_v1';$old$,
   $new$ ELSE
  IF terms->>'index' IS NULL THEN RAISE EXCEPTION 'gas_index_terms';END IF;
  BEGIN
   SELECT c.* INTO STRICT curve FROM market_data.curves c JOIN ref_data.settlement_targets t
    ON(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
    WHERE(c.catalog_id,c.revision_id,c.commodity_code,c.quote_code,c.reference_catalog_id,c.reference_revision_id,t.kind,t.index_a_code)=
     (catalog,a.revision_id,a.product_code,quote.code,quote.catalog_id,quote.revision_id,'index',terms->>'index') AND t.index_b_code IS NULL;
  EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'gas_index_coverage';END;
  target.index_a_code=terms->>'index';
  custom=CASE WHEN terms->'forecastCurve'='null'::jsonb THEN NULL ELSE security.validate_custom_projection(catalog,terms->'forecastCurve') END;
 END IF;
 mapping.curve_code=curve.code;mapping.index_code=target.index_a_code;
 mapping.observation_convention=CASE WHEN terms->>'observationConvention'='named_index_monthly_v1' THEN 'monthly_published_index_v1' ELSE 'daily_published_index_v1' END;$new$),
  ($old$unit_total=CASE source.quantity_basis WHEN 'per_operating_day' THEN days ELSE 1 END;$old$,
   $new$unit_total=CASE source.quantity_basis WHEN 'per_operating_day' THEN 1 ELSE source.delivery_to-source.delivery_from END;$new$),
  ($old$from_day=source.delivery_from+day_number$old$,
   $new$from_day=first_day+day_number$new$),
  ($old$(q.parameters->>'numerator')::numeric*days IS DISTINCT FROM unit_total*(q.parameters->>'denominator')::numeric$old$,
   $new$(q.parameters->>'numerator')::numeric*unit_total IS DISTINCT FROM(q.parameters->>'denominator')::numeric$new$),
  ($old$  SELECT c.* INTO STRICT contract$old$,
   $new$  BEGIN
  SELECT c.* INTO STRICT contract$new$),
  ($old$'published_index_observations_v1');
  IF (fixing.fixing_at$old$,
   $new$'published_index_observations_v1');
  EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'gas_index_coverage';END;
  IF (fixing.fixing_at$new$),
  ($old$CASE WHEN terms->>'observationConvention'='point_publications_v1' THEN 'delivery_index_fixings_v1' ELSE 'calendar_day_arithmetic_v1' END$old$,
   $new$'delivery_index_fixings_v1'$new$)
 )changes(a,b) LOOP
  IF (length(definition)-length(replace(definition,old_text,'')))/length(old_text)<>1 THEN RAISE EXCEPTION 'gas_pricing_upgrade: patch mismatch';END IF;
  definition=replace(definition,old_text,new_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

DO $patch$
DECLARE p pg_proc;definition text;old_text text;new_text text;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='security.record_gas_billing(uuid,jsonb,timestamp with time zone,uuid,jsonb)'::regprocedure;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition,'UTF8')),'hex')<>'a680bf81bedca40f06f5b886483f7b35979e41c5270d860cac6eee00b3040397'
  OR p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
 THEN RAISE EXCEPTION 'gas_pricing_upgrade: unexpected predecessor %',p.oid::regprocedure;END IF;
 FOR old_text,new_text IN SELECT * FROM(VALUES
  ($old$IF m.role NOT IN('consideration','fee')$old$,
   $new$IF m.share_n IS DISTINCT FROM m.share_d OR m.part_kind IS NOT NULL OR
  NOT EXISTS(SELECT 1 FROM trading.gas_component_terms s WHERE(s.catalog_id,s.instrument_id,s.slot)=(m.catalog_id,m.instrument_id,m.source_slot)
   AND(s.delivery_from,s.delivery_to)=(m.delivery_from,m.delivery_to)) THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 IF m.role NOT IN('consideration','fee')$new$)
 )changes(a,b) LOOP
  IF (length(definition)-length(replace(definition,old_text,'')))/length(old_text)<>1 THEN RAISE EXCEPTION 'gas_pricing_upgrade: patch mismatch';END IF;
  definition=replace(definition,old_text,new_text);
 END LOOP;
 EXECUTE definition;
END$patch$;

-- The ticket may show predecessor whole/full-term terms explicitly while replaying
-- their unchanged original command. Only the originating actor with current write
-- authority may read it; an owned staging request can exist without an outcome.
CREATE FUNCTION security.gas_ticket_retained_command(catalog uuid,operation uuid,permission_name text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $function$
DECLARE actor uuid;body jsonb;
BEGIN
 IF security.gas_ticket_retained_request(catalog,operation,permission_name) IS NOT TRUE THEN RETURN NULL;END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts
  WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND market_context_id IS NULL;
 IF permission_name='AmendTrade' THEN
  SELECT request INTO body FROM trading.gas_amendment_commands WHERE(catalog_id,operation_id,actor_id)=(catalog,operation,actor);
 ELSE
  SELECT request INTO body FROM trading.trades WHERE(catalog_id,operation_id,actor_id)=(catalog,operation,actor);
 END IF;
 IF security.gas_ticket_retained_request(catalog,operation,permission_name) IS NOT TRUE
 THEN RAISE EXCEPTION 'Gas ticket write authority expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('request',body);
END$function$;
ALTER FUNCTION security.gas_ticket_retained_command(uuid,uuid,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_ticket_retained_command(uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_ticket_retained_command(uuid,uuid,text) TO openetrm_runtime;

COMMIT;
