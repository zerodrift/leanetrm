-- Commercial storage entitlements and thermal pricing retain exact source versions.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE desk.facility_profiles(
 catalog_id uuid NOT NULL,id uuid NOT NULL,code ref_data.code NOT NULL,revision integer NOT NULL CHECK(revision>0),
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 160),definition_id uuid NOT NULL,
 storage_version uuid,injection_port uuid,withdrawal_port uuid,plant_id uuid,plant_version int,
 input_assignment ref_data.code NOT NULL,output_gas_assignment ref_data.code,power_assignment ref_data.code,ratchets jsonb,
 actor_id uuid NOT NULL REFERENCES security.principals(id),operation_id uuid NOT NULL,command jsonb NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,code,revision),UNIQUE(catalog_id,actor_id,operation_id),
 CHECK((storage_version IS NOT NULL AND injection_port IS NOT NULL AND withdrawal_port IS NOT NULL AND output_gas_assignment IS NOT NULL AND ratchets IS NOT NULL AND plant_id IS NULL AND plant_version IS NULL AND power_assignment IS NULL)
 OR(storage_version IS NULL AND injection_port IS NULL AND withdrawal_port IS NULL AND output_gas_assignment IS NULL AND ratchets IS NULL AND plant_id IS NOT NULL AND plant_version IS NOT NULL AND power_assignment IS NOT NULL)),
 FOREIGN KEY(catalog_id,storage_version,injection_port) REFERENCES trading.storage_facility_ports(catalog_id,version_id,id),
 FOREIGN KEY(catalog_id,storage_version,withdrawal_port) REFERENCES trading.storage_facility_ports(catalog_id,version_id,id),
 FOREIGN KEY(catalog_id,plant_id,plant_version) REFERENCES physical.power_operating_facility_versions(catalog_id,facility_id,version),
 FOREIGN KEY(catalog_id,definition_id,input_assignment) REFERENCES market_data.gas_valuation_assignments(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_id,output_gas_assignment) REFERENCES market_data.gas_valuation_assignments(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_id,power_assignment) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE desk.facility_trades(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,profile_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id),FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,profile_id) REFERENCES desk.facility_profiles,FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);

CREATE FUNCTION desk.gas_at(catalog uuid,definition uuid,pipeline text,zone text,point text,product text) RETURNS market_data.gas_valuation_assignments
 LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT a FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)=(catalog,definition,pipeline,zone,point,product)
 AND a.setup_id IS NOT DISTINCT FROM security.current_gas_setup(catalog,definition)
 AND (a.setup_id IS NULL OR EXISTS(SELECT FROM market_data.gas_setup_points p
  WHERE(p.catalog_id,p.setup_id,p.pipeline_code,p.zone_code,p.point_code,p.product_code)=(a.catalog_id,a.setup_id,pipeline,zone,point,product) AND p.forecast_source='assigned_native_v1'))
$$;

CREATE FUNCTION desk.save_profile(catalog uuid,operation uuid,body jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid=desk.authority(catalog,'ConfigureFacilityLimits',true);until_at timestamptz=security.authority_until('ConfigureFacilityLimits');
 definition uuid=(body->>'definitionRevisionId')::uuid;profile_code text=body->>'code';expected int=(body->>'expectedRevision')::integer;
 existing desk.facility_profiles;sv trading.storage_facility_versions;thermal physical.power_operating_facility_versions;
 ip trading.storage_facility_ports;op trading.storage_facility_ports;fuel market_data.gas_valuation_assignments;output market_data.gas_valuation_assignments;
 energy market_data.curves;bands jsonb;band jsonb;last_high numeric;lo numeric;hi numeric;ri numeric;rw numeric;
 id uuid=gen_random_uuid();head desk.facility_profiles;receipt desk.requests;result jsonb;
 command_hash bytea=sha256(convert_to('facility-profile-v1 '||body::text,'UTF8')); effective timestamptz=(body->>'effectiveAt')::timestamptz;keys text[];
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 IF operation IS NULL OR expected IS NULL OR expected<0 OR NOT isfinite(effective) OR effective IS NULL OR NOT desk.source_visible(catalog,definition) THEN RAISE EXCEPTION 'desk_facility';END IF;
 PERFORM desk.require_authority(catalog,'ConfigureFacilityLimits',until_at);
 SELECT * INTO receipt FROM desk.requests r WHERE(r.catalog_id,r.actor_id,r.operation_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF receipt.command_hash<>command_hash THEN RAISE EXCEPTION 'desk_request_conflict';END IF;
  RETURN receipt.outcome;
 END IF;
 SELECT * INTO existing FROM desk.facility_profiles p WHERE(p.catalog_id,p.code,p.effective_at)=(catalog,profile_code,effective)
  AND p.command-'expectedRevision'-'effectiveAt'=body-'expectedRevision'-'effectiveAt' ORDER BY p.revision LIMIT 1;
 IF FOUND THEN
  result=jsonb_build_object('id',existing.id,'revision',existing.revision);
  INSERT INTO desk.requests VALUES(catalog,actor,operation,command_hash,result);
  PERFORM desk.require_authority(catalog,'ConfigureFacilityLimits',until_at);
  RETURN result;
 END IF;
 SELECT * INTO head FROM desk.facility_profiles p WHERE p.catalog_id=catalog AND p.code=profile_code ORDER BY p.revision DESC LIMIT 1;
 IF expected<>coalesce(head.revision,0) THEN RAISE EXCEPTION 'desk_stale';END IF;
 keys=ARRAY['kind','code','name','expectedRevision','definitionRevisionId','effectiveAt'];
 IF body->>'kind'='storage' THEN
  IF NOT trading.rec_keys(body,keys||ARRAY['storageVersionId','injectionPortId','withdrawalPortId','ratchets']) THEN RAISE EXCEPTION 'desk_facility';END IF;
  SELECT * INTO sv FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.id)=(catalog,(body->>'storageVersionId')::uuid) AND v.effective_at<=effective;
  SELECT * INTO ip FROM trading.storage_facility_ports p WHERE(p.catalog_id,p.version_id,p.id,p.direction)=(catalog,sv.id,(body->>'injectionPortId')::uuid,'inject');
  SELECT * INTO op FROM trading.storage_facility_ports p WHERE(p.catalog_id,p.version_id,p.id,p.direction)=(catalog,sv.id,(body->>'withdrawalPortId')::uuid,'withdraw');
  fuel=desk.gas_at(catalog,definition,ip.pipeline_code,ip.zone_code,ip.point_code,ip.product_code);
  output=desk.gas_at(catalog,definition,op.pipeline_code,op.zone_code,op.point_code,op.product_code);
  IF fuel.code IS NULL OR output.code IS NULL OR fuel.reference_revision_id<>sv.reference_revision_id OR head.plant_id IS NOT NULL OR
   EXISTS(SELECT FROM trading.storage_facility_versions v WHERE (v.catalog_id,v.id)=(catalog,head.storage_version) AND v.facility_id<>sv.facility_id)
  THEN RAISE EXCEPTION 'desk_facility';END IF;
  IF NOT EXISTS(SELECT FROM ref_data.gas_delivery_conventions ic JOIN ref_data.gas_delivery_conventions oc
    USING(catalog_id,revision_id,clock_code,day_start_minute)
    JOIN ref_data.clock_rules clk ON(clk.catalog_id,clk.revision_id,clk.code,clk.kind)=(ic.catalog_id,ic.revision_id,ic.clock_code,'fixed_offset')
    WHERE(ic.catalog_id,ic.revision_id,ic.clock_code)=(sv.reference_catalog_id,sv.reference_revision_id,sv.clock_code)
    AND (ic.pipeline_code,ic.zone_code,ic.point_code,ic.product_code)=(ip.pipeline_code,ip.zone_code,ip.point_code,ip.product_code)
    AND (oc.pipeline_code,oc.zone_code,oc.point_code,oc.product_code)=(op.pipeline_code,op.zone_code,op.point_code,op.product_code))
  THEN RAISE EXCEPTION 'desk_facility';END IF;
  bands=body->'ratchets';last_high=(bands->0->>'lower')::numeric;
  IF jsonb_typeof(bands)<>'array' OR jsonb_array_length(bands) NOT BETWEEN 1 AND 32 THEN RAISE EXCEPTION 'desk_facility';END IF;
  FOR band IN SELECT value FROM jsonb_array_elements(bands) LOOP
   IF NOT trading.rec_keys(band,ARRAY['lower','upper','maximumDailyInjection','maximumDailyWithdrawal']) THEN RAISE EXCEPTION 'desk_facility';END IF;
   lo=trading.gas_storage_number(band->>'lower');hi=trading.gas_storage_number(band->>'upper');
   ri=trading.gas_storage_number(band->>'maximumDailyInjection');rw=trading.gas_storage_number(band->>'maximumDailyWithdrawal');
   IF lo IS DISTINCT FROM last_high OR hi<=lo OR least(ri,rw)<0 OR greatest(hi,ri,rw)>=1e15 OR ri IS NULL OR rw IS NULL OR hi IS NULL THEN RAISE EXCEPTION 'desk_facility';END IF;
   last_high=hi;
  END LOOP;
  IF last_high-(bands->0->>'lower')::numeric>sv.maximum-sv.minimum OR (bands->0->>'lower')::numeric<0 THEN RAISE EXCEPTION 'desk_facility';END IF;
 ELSIF body->>'kind'='thermal' THEN
  IF NOT trading.rec_keys(body,keys||ARRAY['plantId','plantVersion','fuelPipeline','fuelZone','fuelPoint','fuelProduct','powerLocation','powerAssignment']) THEN RAISE EXCEPTION 'desk_facility';END IF;
  SELECT * INTO thermal FROM physical.power_operating_facility_versions v WHERE(v.catalog_id,v.facility_id,v.version,v.kind,v.definition_revision_id)=(catalog,(body->>'plantId')::uuid,(body->>'plantVersion')::int,'thermal',definition);
  fuel=desk.gas_at(catalog,definition,body->>'fuelPipeline',body->>'fuelZone',body->>'fuelPoint',body->>'fuelProduct');
  SELECT e.* INTO energy FROM market_data.curves e JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(e.reference_catalog_id,e.reference_revision_id,e.quote_code)
   WHERE(e.catalog_id,e.revision_id,e.code,e.location_code,q.quantity_code)=(catalog,definition,body->>'powerAssignment',body->>'powerLocation',thermal.energy_quantity_code);
  IF thermal.facility_id IS NULL OR fuel.code IS NULL OR energy.code IS NULL OR head.storage_version IS NOT NULL OR head.plant_id<>thermal.facility_id THEN RAISE EXCEPTION 'desk_facility';END IF;
 ELSE RAISE EXCEPTION 'desk_facility';END IF;
 INSERT INTO desk.facility_profiles VALUES(catalog,id,profile_code,expected+1,body->>'name',definition,sv.id,ip.id,op.id,thermal.facility_id,thermal.version,
  fuel.code,output.code,energy.code,bands,actor,operation,body,desk.publication(catalog),effective);
 result=jsonb_build_object('id',id,'revision',expected+1);
 INSERT INTO desk.requests VALUES(catalog,actor,operation,command_hash,result);
 PERFORM desk.require_authority(catalog,'ConfigureFacilityLimits',until_at);
 RETURN result;
END$$;

CREATE FUNCTION desk.profiles(catalog uuid,definition uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM desk.authority(catalog,'ReadTrades',false);
 RETURN (SELECT coalesce(jsonb_agg(jsonb_build_object('id',p.id,'revision',p.revision,'code',p.code,'name',p.name,'kind',CASE WHEN s.id IS NULL THEN 'thermal' ELSE 'storage' END,
  'facilityId',coalesce(s.facility_id,p.plant_id),'version',coalesce(s.revision,p.plant_version),'clock',coalesce(s.clock_code,v.clock_code),
  'quantity',coalesce(s.quantity_code,v.energy_quantity_code),'minimum',p.ratchets->0->>'lower','maximum',p.ratchets->-1->>'upper','ratchets',p.ratchets,
  'dayStartMinute',conv.day_start_minute,'inputLocation',a.point_code,'inputAssignment',a.code,'inputCurve',a.curve_code,'inputAdjustment',trim_scale(a.adjustment)::text,
  'outputLocation',coalesce(b.point_code,e.location_code),'outputAssignment',coalesce(b.code,e.code),'outputCurve',coalesce(b.curve_code,e.code),
  'outputAdjustment',trim_scale(coalesce(b.adjustment,0))::text,'hourlyRatchets',CASE WHEN security.trading_access(catalog,'ReadFacilityQuantities') THEN (SELECT jsonb_agg(jsonb_build_object('lower',r.lower_level::text,
     'upper',r.upper_level::text,'injectionPerHour',r.injection_per_hour::text,'withdrawalPerHour',r.withdrawal_per_hour::text) ORDER BY r.ordinal)
     FROM trading.storage_facility_ratchets r WHERE(r.catalog_id,r.version_id)=(catalog,s.id)) END) ORDER BY p.name),'[]')
 FROM (SELECT DISTINCT ON(code) * FROM desk.facility_profiles h WHERE h.catalog_id=catalog AND h.definition_id=definition
  AND desk.visible(catalog,h.publication_order,h.effective_at) AND desk.source_visible(catalog,h.definition_id)
  AND (NOT security.current_write_context() OR h.effective_at<=clock_timestamp()) ORDER BY code,revision DESC)p
 JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(catalog,definition,p.input_assignment)
 JOIN ref_data.gas_delivery_conventions conv ON(conv.catalog_id,conv.revision_id,conv.pipeline_code,conv.zone_code,conv.point_code,conv.product_code)=(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)
 LEFT JOIN market_data.gas_valuation_assignments b ON(b.catalog_id,b.revision_id,b.code)=(catalog,definition,p.output_gas_assignment)
 LEFT JOIN market_data.curves e ON(e.catalog_id,e.revision_id,e.code)=(catalog,definition,p.power_assignment)
 LEFT JOIN trading.storage_facility_versions s ON(s.catalog_id,s.id)=(catalog,p.storage_version)
 LEFT JOIN physical.power_operating_facility_versions v ON(v.catalog_id,v.facility_id,v.version)=(catalog,p.plant_id,p.plant_version));
END$$;

-- Validate economic selections on the native booking path, after exact historical replays.
CREATE FUNCTION desk.attach_booking() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE t jsonb;profile desk.facility_profiles;storage trading.storage_facility_versions;a market_data.gas_valuation_assignments;
 b market_data.gas_valuation_assignments;e market_data.curves;plant physical.power_operating_facility_versions;facility uuid;
 held trading.power_operating_instruments;terms jsonb;price record;product text=TG_ARGV[0];actor uuid;operation uuid;minute integer;starts timestamptz;ends timestamptz;
BEGIN
 SELECT tr.request->'terms',tr.actor_id,tr.operation_id INTO STRICT t,actor,operation FROM trading.trades tr WHERE(tr.catalog_id,tr.id)=(NEW.catalog_id,NEW.trade_id);
 IF product='thermal' THEN
  SELECT v.* INTO plant FROM trading.power_operating_instruments i JOIN physical.power_operating_facility_versions v ON(v.catalog_id,v.facility_id,v.version)=(i.catalog_id,i.facility_id,i.facility_version)
   WHERE(i.catalog_id,i.instrument_id)=(NEW.catalog_id,NEW.instrument_id);
  IF plant.kind<>'thermal' THEN RETURN NEW;END IF;
  SELECT i.* INTO STRICT held FROM trading.power_operating_instruments i WHERE(i.catalog_id,i.instrument_id)=(NEW.catalog_id,NEW.instrument_id);
  SELECT tr.request INTO terms FROM trading.trades tr WHERE(tr.catalog_id,tr.id)=(NEW.catalog_id,NEW.trade_id);
  facility=(terms->>'facilityProfileId')::uuid;
  IF facility IS NULL AND (SELECT count(DISTINCT p.code) FROM desk.facility_profiles p WHERE(p.catalog_id,p.plant_id,p.plant_version)=(NEW.catalog_id,plant.facility_id,plant.version) AND p.effective_at<=clock_timestamp())>1 THEN RAISE EXCEPTION 'desk_facility';END IF;
  SELECT * INTO profile FROM desk.facility_profiles p WHERE (facility IS NULL OR p.id=facility) AND (p.catalog_id,p.plant_id,p.plant_version)=(NEW.catalog_id,plant.facility_id,plant.version)
   AND p.effective_at<=clock_timestamp() ORDER BY p.effective_at DESC,p.publication_order DESC LIMIT 1;
  IF NOT FOUND THEN IF facility IS NOT NULL THEN RAISE EXCEPTION 'desk_facility';END IF;RETURN NEW;END IF;
 ELSE
  facility=(t->>'facilityProfileId')::uuid;
  IF facility IS NULL AND product='heat_rate_call' THEN RETURN NEW;END IF;
  SELECT * INTO profile FROM desk.facility_profiles p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,facility) AND p.effective_at<=clock_timestamp();
  IF NOT FOUND OR profile.definition_id IS DISTINCT FROM (t->>'definitionRevisionId')::uuid THEN RAISE EXCEPTION 'desk_facility';END IF;
 END IF;
 IF NOT desk.source_visible(NEW.catalog_id,profile.definition_id) OR EXISTS(SELECT FROM desk.facility_profiles h
   WHERE(h.catalog_id,h.code)=(profile.catalog_id,profile.code) AND h.effective_at<=clock_timestamp() AND h.revision>profile.revision)
 THEN RAISE EXCEPTION 'desk_facility';END IF;
 SELECT * INTO STRICT a FROM market_data.gas_valuation_assignments x WHERE(x.catalog_id,x.revision_id,x.code)=(NEW.catalog_id,profile.definition_id,profile.input_assignment);
 IF a.setup_id IS DISTINCT FROM security.current_gas_setup(NEW.catalog_id,profile.definition_id) THEN RAISE EXCEPTION 'desk_facility';END IF;
 IF product='gas_storage' THEN
  SELECT * INTO storage FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.id)=(NEW.catalog_id,profile.storage_version);
  SELECT * INTO b FROM market_data.gas_valuation_assignments x WHERE(x.catalog_id,x.revision_id,x.code)=(NEW.catalog_id,profile.definition_id,profile.output_gas_assignment);
  SELECT cv.day_start_minute INTO minute FROM ref_data.gas_delivery_conventions cv WHERE(cv.catalog_id,cv.revision_id,cv.pipeline_code,cv.zone_code,cv.point_code,cv.product_code)
   =(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code);
  SELECT min(d.starts_at),max(d.ends_at) INTO starts,ends FROM trading.gas_storage_days d
   WHERE(d.catalog_id,d.instrument_id)=(NEW.catalog_id,NEW.instrument_id);
  IF starts IS NULL OR b.setup_id IS DISTINCT FROM a.setup_id OR storage.effective_at>starts OR EXISTS(SELECT FROM trading.storage_facility_versions later
    WHERE(later.catalog_id,later.facility_id)=(storage.catalog_id,storage.facility_id) AND later.revision>storage.revision AND later.effective_at<ends) THEN RAISE EXCEPTION 'desk_facility';END IF;
  IF storage.id IS NULL OR (t->>'quantity',t->>'clock',t->>'injectionCurve',t->>'withdrawalCurve',(t->>'dayStartMinute')::int)
    IS DISTINCT FROM(storage.quantity_code,storage.clock_code,a.curve_code,b.curve_code,minute)
   OR (t->>'minimumInventory')::numeric<(profile.ratchets->0->>'lower')::numeric OR (t->>'maximumInventory')::numeric>(profile.ratchets->-1->>'upper')::numeric THEN RAISE EXCEPTION 'desk_facility';END IF;
 ELSE
  SELECT * INTO e FROM market_data.curves x WHERE(x.catalog_id,x.revision_id,x.code)=(NEW.catalog_id,profile.definition_id,profile.power_assignment);
  IF product='thermal' THEN
   FOR price IN SELECT m.name,sp.curve_code,coalesce(held.terms->'composition'->'sides'->m.slot->>'quantitySource',m.name) flow
    FROM trading.operating_memberships m JOIN trading.operating_side_prices sp USING(catalog_id,instrument_id,slot)
    WHERE(m.catalog_id,m.instrument_id,sp.mode)=(NEW.catalog_id,NEW.instrument_id,'selected_contract_flat_v1') LOOP
    IF (price.flow IN('energy_output','energy') AND price.curve_code<>e.code) OR (price.flow IN('fuel_input','guaranteed_fuel','consumption_fuel') AND price.curve_code<>a.curve_code) THEN RAISE EXCEPTION 'desk_facility';END IF;
   END LOOP;
  END IF;
  IF product='heat_rate_call' AND (profile.plant_id IS NULL OR (t->>'powerCurve',t->>'fuelCurve') IS DISTINCT FROM(e.code,a.curve_code)
   OR NOT EXISTS(SELECT FROM physical.power_operating_facility_versions v WHERE(v.catalog_id,v.facility_id,v.version)=(NEW.catalog_id,profile.plant_id,profile.plant_version)
    AND v.effective_from<=(t->>'deliveryFrom')::date AND v.effective_to>=(t->>'deliveryTo')::date)
) THEN RAISE EXCEPTION 'desk_facility';END IF;
 END IF;
 INSERT INTO desk.facility_trades VALUES(NEW.catalog_id,NEW.trade_id,profile.id,actor,NEW.publication_order,NEW.effective_at);
 RETURN NEW;
END$$;
CREATE TRIGGER facility_booking AFTER INSERT ON trading.gas_storage_trade_revisions FOR EACH ROW EXECUTE FUNCTION desk.attach_booking('gas_storage');
CREATE TRIGGER facility_booking AFTER INSERT ON trading.heat_rate_call_trade_revisions FOR EACH ROW EXECUTE FUNCTION desk.attach_booking('heat_rate_call');
CREATE TRIGGER facility_booking AFTER INSERT ON trading.operating_trade_revisions FOR EACH ROW EXECUTE FUNCTION desk.attach_booking('thermal');

DO $$DECLARE f regprocedure;definition text;before text='trading.rec_keys(t,ARRAY[';
BEGIN
 FOREACH f IN ARRAY ARRAY['security.book_gas_storage(uuid,jsonb,jsonb,timestamptz)'::regprocedure,'security.book_heat_rate_call(uuid,jsonb,jsonb,timestamptz)'::regprocedure] LOOP
  definition=pg_get_functiondef(f);
  IF (length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'desk_booking_predecessor';END IF;
  EXECUTE replace(definition,before,'trading.rec_keys(t-''facilityProfileId'',ARRAY[');
 END LOOP;
END$$;

DO $$DECLARE definition text;before text='physical.power_operating_keys(body,ARRAY[';
BEGIN
 definition=pg_get_functiondef('security.book_operating_right(uuid,jsonb,jsonb,timestamptz)'::regprocedure);
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'desk_thermal_predecessor';END IF;
 EXECUTE replace(definition,before,'physical.power_operating_keys(body-''facilityProfileId'',ARRAY[');
END$$;

-- Keep location adjustments on their floating Side. The contract record and price lookup refer to the same assigned version.
DO $$DECLARE definition text;before text='''injection_curve_code'',h.injection_curve_code,';
BEGIN
 definition=pg_get_functiondef('security.read_gas_storage_positions(uuid,uuid[],uuid[],uuid[])'::regprocedure);
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'desk_storage_read_predecessor';END IF;
 EXECUTE replace(definition,before,$new$'facility_profile_id',h.terms->'terms'->>'facilityProfileId',
   'injection_adjustment',coalesce((SELECT trim_scale(a.adjustment)::text FROM desk.facility_trades ft JOIN desk.facility_profiles p ON(p.catalog_id,p.id)=(ft.catalog_id,ft.profile_id)
    JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(p.catalog_id,p.definition_id,p.input_assignment) WHERE(ft.catalog_id,ft.trade_id)=(r.catalog_id,r.trade_id) AND h.terms->'terms'->>'facilityProfileId'=p.id::text),'0'),
   'withdrawal_adjustment',coalesce((SELECT trim_scale(a.adjustment)::text FROM desk.facility_trades ft JOIN desk.facility_profiles p ON(p.catalog_id,p.id)=(ft.catalog_id,ft.profile_id)
    JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(p.catalog_id,p.definition_id,p.output_gas_assignment) WHERE(ft.catalog_id,ft.trade_id)=(r.catalog_id,r.trade_id) AND h.terms->'terms'->>'facilityProfileId'=p.id::text),'0'),
   $new$||before);
END$$;

-- Existing rights can be displayed and attributed without inventing a historical legal or credit decision.
CREATE VIEW desk.facility_revisions AS
 SELECT a.* FROM desk.admissions a
 UNION ALL
 SELECT old.catalog_id,old.trade_id,old.revision,old.book_id,old.product,NULL::uuid,NULL::uuid,old.definition_id,
  b.executed_at,old.effective_at,old.publication_order FROM (
  SELECT r.catalog_id,r.trade_id,r.revision,r.book_id,'physical_gas' product,i.definition_revision_id definition_id,r.effective_at,r.publication_order
   FROM trading.gas_trade_revisions r JOIN trading.gas_instruments i USING(catalog_id,instrument_id)
  UNION ALL SELECT r.catalog_id,r.trade_id,r.revision,r.book_id,'gas_storage',i.definition_revision_id,r.effective_at,r.publication_order
   FROM trading.gas_storage_trade_revisions r JOIN trading.gas_storage_contracts i USING(catalog_id,instrument_id)
  UNION ALL SELECT r.catalog_id,r.trade_id,r.revision,r.book_id,'heat_rate_call',i.definition_revision_id,r.effective_at,r.publication_order
   FROM trading.heat_rate_call_trade_revisions r JOIN trading.heat_rate_call_instruments i USING(catalog_id,instrument_id)
  UNION ALL SELECT r.catalog_id,r.trade_id,r.revision,r.book_id,'thermal_toll',i.definition_revision_id,r.effective_at,r.publication_order
   FROM trading.operating_trade_revisions r JOIN trading.power_operating_instruments i USING(catalog_id,instrument_id)
   JOIN physical.power_operating_facility_versions v ON(v.catalog_id,v.facility_id,v.version,v.kind)=(i.catalog_id,i.facility_id,i.facility_version,'thermal')
  UNION ALL SELECT r.catalog_id,r.trade_id,r.revision,r.book_id,'financial_swap',fs.definition_revision_id,r.effective_at,r.publication_order
   FROM trading.swap_trade_revisions r JOIN desk.swap_sources fs USING(catalog_id,instrument_id)
 )old JOIN trading.booking_facts b USING(catalog_id,trade_id)
 WHERE NOT EXISTS(SELECT FROM desk.admissions a WHERE(a.catalog_id,a.trade_id,a.revision)=(old.catalog_id,old.trade_id,old.revision));
ALTER VIEW desk.facility_revisions OWNER TO openetrm_security_owner;
REVOKE ALL ON desk.facility_revisions FROM PUBLIC;

-- One whole-trade attribution for a physical purchase/sale or a separately booked financial hedge.
CREATE FUNCTION desk.attribute(catalog uuid,trade uuid,profile_id uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid=desk.authority(catalog,'BookTrade',true);until_at timestamptz=security.authority_until('BookTrade');
 p desk.facility_profiles;ad desk.admissions;existing desk.facility_trades;
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO p FROM desk.facility_profiles x WHERE(x.catalog_id,x.id)=(catalog,profile_id);
 SELECT * INTO ad FROM desk.facility_revisions a WHERE(a.catalog_id,a.trade_id)=(catalog,trade) ORDER BY a.revision DESC LIMIT 1;
 IF p.id IS NULL OR ad.trade_id IS NULL OR p.definition_id<>ad.definition_revision_id OR NOT desk.source_visible(catalog,p.definition_id) THEN RAISE EXCEPTION 'desk_facility';END IF;
 PERFORM desk.require_authority(catalog,'BookTrade',until_at);
 SELECT * INTO existing FROM desk.facility_trades f WHERE(f.catalog_id,f.trade_id)=(catalog,trade);
 IF FOUND THEN
  IF existing.profile_id<>p.id THEN RAISE EXCEPTION 'desk_request_conflict';END IF;
  RETURN jsonb_build_object('tradeId',trade,'profileId',p.id);
 END IF;
 IF p.effective_at>clock_timestamp() OR EXISTS(SELECT FROM desk.facility_profiles h WHERE(h.catalog_id,h.code)=(p.catalog_id,p.code)
  AND h.revision>p.revision AND h.effective_at<=clock_timestamp()) THEN RAISE EXCEPTION 'desk_facility';END IF;
 IF ad.product='physical_gas' THEN
  IF NOT EXISTS(SELECT FROM trading.gas_trade_revisions r JOIN trading.gas_component_terms c ON(c.catalog_id,c.instrument_id)=(r.catalog_id,r.instrument_id)
    JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(catalog,p.definition_id,c.assignment_code)
    WHERE(r.catalog_id,r.trade_id,r.revision)=(catalog,trade,ad.revision) AND c.role='physical' AND ((p.plant_id IS NOT NULL AND a.code=p.input_assignment) OR EXISTS(SELECT FROM trading.storage_facility_ports port
     WHERE(port.catalog_id,port.version_id,port.pipeline_code,port.zone_code,port.point_code,port.product_code,port.contract_direction)=(catalog,p.storage_version,a.pipeline_code,a.zone_code,a.point_code,a.product_code,c.direction*coalesce(r.direction,1))))) THEN RAISE EXCEPTION 'desk_facility';END IF;
 ELSIF ad.product='gas_storage' THEN
  IF p.storage_version IS NULL OR ad.agreement_id IS NOT NULL AND NOT EXISTS(SELECT FROM desk.facility_trades ft WHERE(ft.catalog_id,ft.trade_id,ft.profile_id)=(catalog,trade,p.id)) THEN RAISE EXCEPTION 'desk_facility';END IF;
 ELSIF ad.product='heat_rate_call' THEN
  IF NOT EXISTS(SELECT FROM trading.heat_rate_call_trade_revisions r JOIN trading.heat_rate_call_instruments i USING(catalog_id,instrument_id)
   JOIN market_data.gas_valuation_assignments g ON(g.catalog_id,g.revision_id,g.code)=(catalog,p.definition_id,p.input_assignment)
   WHERE(r.catalog_id,r.trade_id,i.power_curve_code,i.fuel_curve_code)=(catalog,trade,p.power_assignment,g.curve_code)) THEN RAISE EXCEPTION 'desk_facility';END IF;
 ELSIF ad.product<>'financial_swap' THEN RAISE EXCEPTION 'desk_facility';END IF;
 INSERT INTO desk.facility_trades VALUES(catalog,trade,p.id,actor,desk.publication(catalog),ad.effective_at);
 PERFORM desk.require_authority(catalog,'BookTrade',until_at);
 RETURN jsonb_build_object('tradeId',trade,'profileId',p.id);
END$$;

DO $$DECLARE t text;f regprocedure;BEGIN
 FOREACH t IN ARRAY ARRAY['facility_profiles','facility_trades'] LOOP
  EXECUTE format('ALTER TABLE desk.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE desk.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE desk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY internal_content ON desk.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON desk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p WHERE p.pronamespace='desk'::regnamespace AND p.proname IN('gas_at','save_profile','profiles','attach_booking','attribute') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION desk.profiles(uuid,uuid),desk.attribute(uuid,uuid,uuid),desk.save_profile(uuid,uuid,jsonb) TO openetrm_runtime;
COMMIT;
