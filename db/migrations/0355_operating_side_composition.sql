-- The retained operating family serves complete supplied operating rights and
-- certificate forwards. Original contracts own exact membership and admission;
-- shared storage retains immutable programs and ordinary scenario execution.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);

-- Fail closed on an altered predecessor body, owner, privilege or execution mode.
DO $upgrade$DECLARE expected jsonb;p pg_proc;actual_acl jsonb;expected_acl jsonb;BEGIN
 FOR expected IN SELECT value FROM jsonb_array_elements($contracts$
[{"acl":["openetrm_security_owner=X/openetrm_security_owner"],"hash":"6b66eb02e939edd9363697eb36a9116509a1952b120e255fc4df17f7e6a24a00","owner":"openetrm_security_owner","config":["search_path=pg_catalog"],"strict":false,"definer":true,"parallel":"u","leakproof":false,"signature":"trading.operating_plan_admission(physical.power_operating_facility_versions,jsonb,timestamp with time zone)","volatility":"v"},{"acl":["openetrm_security_owner=X/openetrm_security_owner"],"hash":"f961c5ec38087323df2f847e2c69a7d3460e144c77f8818e2abcc7f66007979f","owner":"openetrm_security_owner","config":["search_path=pg_catalog"],"strict":false,"definer":true,"parallel":"u","leakproof":false,"signature":"trading.require_operating_program_shape(uuid,uuid,integer,uuid)","volatility":"v"},{"acl":["openetrm_runtime=X/openetrm_security_owner","openetrm_security_owner=X/openetrm_security_owner"],"hash":"e708155bc4a7d4c088c6415f903b614488ea0dba33bb389390f619117c081ce8","owner":"openetrm_security_owner","config":["search_path=pg_catalog"],"strict":false,"definer":true,"parallel":"u","leakproof":false,"signature":"security.book_operating_right(uuid,jsonb,jsonb,timestamp with time zone)","volatility":"v"},{"acl":["openetrm_security_owner=X/openetrm_security_owner"],"hash":"4c660b6660afecbb63bafc691d28087b8b2216e74ad16183fba240dc383c7898","owner":"openetrm_security_owner","config":["search_path=pg_catalog"],"strict":false,"definer":false,"parallel":"u","leakproof":false,"signature":"trading.request_side_calendar_policy(jsonb,integer)","volatility":"i"}]
$contracts$::jsonb) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(expected->>'signature');
  SELECT jsonb_agg(x::text ORDER BY x::text) INTO actual_acl FROM unnest(coalesce(p.proacl,acldefault('f',p.proowner))) x;
  SELECT jsonb_agg(value ORDER BY value) INTO expected_acl FROM(SELECT replace(value,'@migration_owner',quote_ident(current_user))::aclitem::text value FROM jsonb_array_elements_text(expected->'acl')) a;
  IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex') IS DISTINCT FROM expected->>'hash' OR
   pg_get_userbyid(p.proowner) IS DISTINCT FROM(CASE expected->>'owner' WHEN '@migration_owner' THEN current_user ELSE expected->>'owner' END) OR p.prosecdef IS DISTINCT FROM(expected->>'definer')::boolean OR
   p.provolatile IS DISTINCT FROM(expected->>'volatility')::"char" OR p.proisstrict IS DISTINCT FROM(expected->>'strict')::boolean OR
   p.proleakproof IS DISTINCT FROM(expected->>'leakproof')::boolean OR p.proparallel IS DISTINCT FROM(expected->>'parallel')::"char" OR
   coalesce(to_jsonb(p.proconfig),'null'::jsonb) IS DISTINCT FROM expected->'config' OR actual_acl IS DISTINCT FROM expected_acl
  THEN RAISE EXCEPTION 'operating_composition_upgrade: unexpected helper %',expected->>'signature';END IF;
 END LOOP;
END$upgrade$;


-- Sources name typed outputs of one admitted physical plan; they do not select prices.
-- Match managed String.Trim/UTF-16 length without rewriting a display label.
CREATE FUNCTION trading.operating_side_name_valid(v text) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(v=btrim(v,(SELECT string_agg(chr(c),'') FROM unnest(ARRAY[9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288]) c))
 AND length(v)+length(regexp_replace(v,U&'[^\+010000-\+10FFFF]','','g')) BETWEEN 1 AND 80,false)
$$;
CREATE FUNCTION trading.operating_source_port(source text) RETURNS text LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE source WHEN 'energy_output' THEN 'energy' WHEN 'discharging_energy' THEN 'energy'
 WHEN 'charging_energy' THEN 'charge' WHEN 'grid_throughput' THEN 'variable' WHEN 'payment_energy' THEN 'payment'
 WHEN 'horizon_right' THEN 'energy' ELSE source END
$$;
CREATE FUNCTION trading.require_operating_composition(v jsonb,kind text) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE side jsonb;p jsonb;r jsonb;counted integer;
BEGIN
 IF NOT physical.power_operating_keys(v,ARRAY['implementation','perspective','measure','currencyCode','rightQuantityCode','discounting','sides'])
 OR v->>'implementation' IS DISTINCT FROM 'operating_side_composition_v2'
 OR v->>'perspective' IS DISTINCT FROM 'resource_right_holder_v1' OR v->>'measure' IS DISTINCT FROM 'remaining_operating_value_v1'
 OR v->'discounting' IS DISTINCT FROM 'false'::jsonb OR jsonb_typeof(v->'currencyCode') IS DISTINCT FROM 'string'
 OR jsonb_typeof(v->'rightQuantityCode') IS DISTINCT FROM 'string' OR jsonb_typeof(v->'sides') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'operating_composition';END IF;
 counted=jsonb_array_length(v->'sides');
 IF counted NOT BETWEEN 1 AND 8 OR
 (SELECT count(DISTINCT value->>'key') FROM jsonb_array_elements(v->'sides'))<>counted OR
 NOT EXISTS(SELECT 1 FROM jsonb_array_elements(v->'sides') WHERE value->>'quantitySource'<>'horizon_right')
 THEN RAISE EXCEPTION 'operating_composition';END IF;
 FOR side IN SELECT value FROM jsonb_array_elements(v->'sides') LOOP
  IF NOT physical.power_operating_keys(side,ARRAY['key','name','quantitySource','coefficient','kind','price','paymentRule'])
   OR jsonb_typeof(side->'key') IS DISTINCT FROM 'string' OR side->>'key' !~ '^[a-z][a-z0-9_]{0,47}$'
   OR jsonb_typeof(side->'name') IS DISTINCT FROM 'string' OR NOT trading.operating_side_name_valid(side->>'name')
   OR jsonb_typeof(side->'coefficient') IS DISTINCT FROM 'string' OR side->>'coefficient' NOT IN('1','-1')
   OR jsonb_typeof(side->'kind') IS DISTINCT FROM 'string' OR side->>'kind' NOT IN('payment','delivery')
   OR(side->>'quantitySource'='horizon_right' AND side->>'kind'<>'payment')
   OR jsonb_typeof(side->'quantitySource') IS DISTINCT FROM 'string' OR NOT(
    side->>'quantitySource'='horizon_right' OR kind='thermal' AND side->>'quantitySource' IN('energy_output','guaranteed_fuel','consumption_fuel')
    OR kind='battery' AND side->>'quantitySource' IN('charging_energy','discharging_energy','grid_throughput')
    OR kind='solar' AND side->>'quantitySource' IN('energy_output','payment_energy'))
  THEN RAISE EXCEPTION 'operating_composition';END IF;
  r=side->'paymentRule';
  IF side->>'kind'='delivery' THEN
   IF r IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'operating_composition';END IF;
  ELSE
   IF NOT physical.power_operating_keys(r,ARRAY['frequency','rule','days','dayOfMonth','dayBasis','shortMonth','adjustment','calendar','implementation'])
    OR r->>'frequency' IS DISTINCT FROM 'monthly' OR r->>'rule' IS DISTINCT FROM 'days_after_period_end'
    OR r->>'dayBasis' IS DISTINCT FROM 'calendar_days' OR r->>'adjustment' IS DISTINCT FROM 'unadjusted'
    OR jsonb_typeof(r->'days') IS DISTINCT FROM 'number' OR (r->>'days')::numeric<>trunc((r->>'days')::numeric) OR (r->>'days')::numeric NOT BETWEEN 0 AND 366
    OR r->>'implementation' IS DISTINCT FROM 'monthly_payment_date_rules_v1'
    OR r->'dayOfMonth' IS DISTINCT FROM 'null'::jsonb OR r->'shortMonth' IS DISTINCT FROM 'null'::jsonb OR r->'calendar' IS DISTINCT FROM 'null'::jsonb
   THEN RAISE EXCEPTION 'operating_composition';END IF;
  END IF;
  p=side->'price';
  IF NOT physical.power_operating_keys(p,ARRAY['mode','quoteCode','fixedPrice','curveCode','contractId'])
   OR jsonb_typeof(p->'mode') IS DISTINCT FROM 'string' OR jsonb_typeof(p->'quoteCode') IS DISTINCT FROM 'string'
  THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  IF p->>'mode'='fixed' THEN
   IF jsonb_typeof(p->'fixedPrice') IS DISTINCT FROM 'string' OR p->'curveCode' IS DISTINCT FROM 'null'::jsonb OR p->'contractId' IS DISTINCT FROM 'null'::jsonb
   THEN RAISE EXCEPTION 'operating_booking_price';END IF;
   PERFORM trading.operating_decimal(p->>'fixedPrice');
  ELSIF p->>'mode'='selected_contract_flat_v1' THEN
   IF p->'fixedPrice' IS DISTINCT FROM 'null'::jsonb OR jsonb_typeof(p->'curveCode') IS DISTINCT FROM 'string' OR jsonb_typeof(p->'contractId') IS DISTINCT FROM 'string'
   THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  ELSE RAISE EXCEPTION 'operating_booking_price';END IF;
 END LOOP;
END$$;
ALTER TABLE trading.operating_memberships
 DROP CONSTRAINT operating_memberships_name_check,
 ADD CONSTRAINT operating_memberships_name_check CHECK(trading.operating_side_name_valid(name)),
 DROP CONSTRAINT operating_memberships_catalog_id_instrument_id_name_key;

CREATE OR REPLACE FUNCTION trading.operating_plan_admission(f physical.power_operating_facility_versions, p jsonb, valuation timestamp with time zone)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE kind text=f.kind;params jsonb=f.parameters;s jsonb;v jsonb=p->'valuation';items jsonb;item jsonb;result jsonb='[]';
 start_at timestamptz;at_from timestamptz;at_to timestamptz;step interval;hours numeric[];i integer=0;counted integer;
 energy numeric[];charge numeric[];discharge numeric[];stored numeric[];throughput numeric[];amount numeric[];fuel numeric[];
 output numeric;available numeric;previous numeric;state text;next_state text;state_from timestamptz;issued timestamptz;starts integer;
 composed boolean=p ? 'composition';consumption numeric[];n numeric;d numeric;zero numeric[]=ARRAY[0::numeric,1::numeric];online boolean;started boolean;stopped boolean;basis text;
BEGIN
 IF composed THEN
  IF p->'valuation' IS DISTINCT FROM 'null'::jsonb OR NOT physical.power_operating_keys(p,ARRAY['snapshotId','facilityId','facilityVersion','grid','batteryState','batteryDispatch','thermalState','thermalDispatch','solarRight','solarDispatch','valuation','composition'])
  THEN RAISE EXCEPTION 'operating_composition';END IF;
  PERFORM trading.require_operating_composition(p->'composition',kind);
  p=(p-'composition')||jsonb_build_object('valuation',jsonb_build_object('fuelQuantityBasis','guaranteed'));v=p->'valuation';
 END IF;
 IF NOT physical.power_operating_keys(p,ARRAY['snapshotId','facilityId','facilityVersion','grid','batteryState','batteryDispatch','thermalState','thermalDispatch','solarRight','solarDispatch','valuation'])
 OR jsonb_typeof(p->'snapshotId') IS DISTINCT FROM 'string' OR(p->>'snapshotId')::uuid='00000000-0000-0000-0000-000000000000'
 OR jsonb_typeof(p->'facilityId') IS DISTINCT FROM 'string' OR jsonb_typeof(p->'facilityVersion') IS DISTINCT FROM 'number' OR(p->>'facilityId')::uuid<>f.facility_id OR(p->>'facilityVersion')::integer<>f.version
 OR NOT physical.power_operating_keys(p->'grid',ARRAY['operatingDate','firstInterval'])
 OR jsonb_typeof(p->'batteryDispatch') IS DISTINCT FROM 'array' OR jsonb_typeof(p->'thermalDispatch') IS DISTINCT FROM 'array'
 OR jsonb_typeof(p->'solarDispatch') IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'operating_booking_input';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.clock_rules clock WHERE(clock.catalog_id,clock.revision_id,clock.code,clock.kind,clock.fixed_offset_seconds)=
 (f.reference_catalog_id,f.reference_revision_id,f.clock_code,'fixed_offset',0)) THEN RAISE EXCEPTION 'operating_booking_clock';END IF;
 IF p->'grid'->>'operatingDate' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR jsonb_typeof(p->'grid'->'firstInterval') IS DISTINCT FROM 'number'
 OR(p->'grid'->>'firstInterval')::integer NOT BETWEEN 1 AND 1440/f.interval_minutes THEN RAISE EXCEPTION 'operating_booking_grid';END IF;
 step=make_interval(mins=>f.interval_minutes);hours=trading.operating_ratio(f.interval_minutes,60);
 start_at=(p->'grid'->>'operatingDate')::date::timestamp AT TIME ZONE 'UTC'+((p->'grid'->>'firstInterval')::integer-1)*step;
 items=p->(kind||'Dispatch');counted=jsonb_array_length(items);
 IF counted NOT BETWEEN 1 AND 1024 OR start_at<=valuation OR start_at<f.effective_from OR start_at+counted*step>f.effective_to
 THEN RAISE EXCEPTION 'operating_delivery_lifecycle';END IF;
 IF kind='battery' THEN
  IF NOT physical.power_operating_keys(p->'batteryState',ARRAY['at','storedMwh','cumulativeGridThroughputMwh','terminalMinimumMwh','terminalMaximumMwh'])
  OR p->'thermalState'<>'null'::jsonb OR p->'solarRight'<>'null'::jsonb OR jsonb_array_length(p->'thermalDispatch')<>0 OR jsonb_array_length(p->'solarDispatch')<>0
  THEN RAISE EXCEPTION 'operating_booking_union';END IF;
  s=p->'batteryState';PERFORM trading.operating_require_decimals(s,ARRAY['storedMwh','cumulativeGridThroughputMwh','terminalMinimumMwh','terminalMaximumMwh']);stored=trading.operating_decimal(s->>'storedMwh');throughput=trading.operating_decimal(s->>'cumulativeGridThroughputMwh');
  IF trading.operating_instant(s->>'at')<>start_at OR stored[1]<0 OR throughput[1]<0 OR
   stored[1]<(params->>'minimumStoredMwh')::numeric*stored[2] OR stored[1]>(params->>'maximumStoredMwh')::numeric*stored[2]
   OR(s->>'terminalMinimumMwh')::numeric<(params->>'minimumStoredMwh')::numeric OR(s->>'terminalMaximumMwh')::numeric>(params->>'maximumStoredMwh')::numeric
   OR(s->>'terminalMinimumMwh')::numeric>(s->>'terminalMaximumMwh')::numeric THEN RAISE EXCEPTION 'operating_booking_state';END IF;
 ELSIF kind='thermal' THEN
  IF NOT physical.power_operating_keys(p->'thermalState',ARRAY['at','commitment','elapsedMinutes','previousOutputMw','startsUsed','guaranteedHeatRate','guaranteedNoLoadFuelPerHour','guaranteedStartupFuel','terminalPolicy','numericalPolicy'])
  OR p->'batteryState'<>'null'::jsonb OR p->'solarRight'<>'null'::jsonb OR jsonb_array_length(p->'batteryDispatch')<>0 OR jsonb_array_length(p->'solarDispatch')<>0 THEN RAISE EXCEPTION 'operating_booking_union';END IF;
  s=p->'thermalState';PERFORM trading.operating_require_decimals(s,ARRAY['previousOutputMw','guaranteedHeatRate','guaranteedNoLoadFuelPerHour','guaranteedStartupFuel']);state=s->>'commitment';previous=(s->>'previousOutputMw')::numeric;starts=(s->>'startsUsed')::integer;
  IF trading.operating_instant(s->>'at')<>start_at OR state NOT IN('online','offline') OR starts<0 OR starts>(params->>'maximumStarts')::integer
   OR jsonb_typeof(s->'elapsedMinutes') IS DISTINCT FROM 'number' OR jsonb_typeof(s->'startsUsed') IS DISTINCT FROM 'number' OR(s->>'elapsedMinutes')::integer NOT BETWEEN 0 AND 525600 OR previous<0 OR previous>(params->>'maximumOutputMw')::numeric
   OR(state='offline' AND previous<>0) OR(state='online' AND previous<(params->>'minimumOutputMw')::numeric)
   OR s->>'terminalPolicy' IS DISTINCT FROM 'require_minimum_times_satisfied' OR s->>'numericalPolicy' IS DISTINCT FROM 'thermal_exact_elapsed_quantity_v1'
   OR(trading.operating_decimal(s->>'guaranteedHeatRate'))[1]<=0 OR(trading.operating_decimal(s->>'guaranteedNoLoadFuelPerHour'))[1]<0 OR(trading.operating_decimal(s->>'guaranteedStartupFuel'))[1]<0
  THEN RAISE EXCEPTION 'operating_booking_state';END IF;
  state_from=start_at-make_interval(mins=>(s->>'elapsedMinutes')::integer);
  basis=v->>'fuelQuantityBasis';IF basis NOT IN('guaranteed','consumption') OR basis IS NULL THEN RAISE EXCEPTION 'operating_booking_policy';END IF;
 ELSE
  IF NOT physical.power_operating_keys(p->'solarRight',ARRAY['forecastAssumption','entitlementNumerator','entitlementDenominator','deliveryBasis','paymentBasis'])
  OR p->'batteryState'<>'null'::jsonb OR p->'thermalState'<>'null'::jsonb OR jsonb_array_length(p->'batteryDispatch')<>0 OR jsonb_array_length(p->'thermalDispatch')<>0 THEN RAISE EXCEPTION 'operating_booking_union';END IF;
  s=p->'solarRight';IF jsonb_typeof(s->'entitlementNumerator') IS DISTINCT FROM 'string' OR jsonb_typeof(s->'entitlementDenominator') IS DISTINCT FROM 'string' OR s->>'entitlementNumerator' !~ '^[0-9]+$' OR s->>'entitlementDenominator' !~ '^[0-9]+$' THEN RAISE EXCEPTION 'operating_booking_number';END IF;n=(s->>'entitlementNumerator')::numeric;d=(s->>'entitlementDenominator')::numeric;
  IF n<=0 OR d<=0 OR n>d OR trunc(n)<>n OR trunc(d)<>d OR length(n::text)>80 OR length(d::text)>80
  OR s->>'forecastAssumption' IS DISTINCT FROM 'hypothetical_supplied_forecast_v1' OR s->>'deliveryBasis' IS DISTINCT FROM 'as_produced'
  OR s->>'paymentBasis' NOT IN('produced_only','delivery_obligation') THEN RAISE EXCEPTION 'operating_booking_policy';END IF;
 END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(items) LOOP
  at_from=start_at+i*step;at_to=at_from+step;
  IF kind='battery' THEN
   IF NOT physical.power_operating_keys(item,ARRAY['chargeMw','dischargeMw','availablePowerFraction']) THEN RAISE EXCEPTION 'operating_booking_input';END IF;
   PERFORM trading.operating_require_decimals(item,ARRAY['chargeMw','dischargeMw','availablePowerFraction']);
   charge=trading.operating_multiply(trading.operating_decimal(item->>'chargeMw'),hours);discharge=trading.operating_multiply(trading.operating_decimal(item->>'dischargeMw'),hours);
   available=(item->>'availablePowerFraction')::numeric;
   IF charge[1]<0 OR discharge[1]<0 OR(charge[1]>0 AND discharge[1]>0) OR available<0 OR available>1
   OR(item->>'chargeMw')::numeric>(params->>'maximumChargeMw')::numeric*available OR(item->>'dischargeMw')::numeric>(params->>'maximumDischargeMw')::numeric*available THEN RAISE EXCEPTION 'operating_battery_limit';END IF;
   amount=trading.operating_multiply(charge,trading.operating_decimal(params->>'chargeEfficiency'));
   stored=trading.operating_add(stored,amount);
   amount=trading.operating_decimal(params->>'dischargeEfficiency');amount=trading.operating_multiply(discharge,ARRAY[-amount[2],amount[1]]);
   stored=trading.operating_add(stored,amount);throughput=trading.operating_add(throughput,trading.operating_add(charge,discharge));
   IF stored[1]<(params->>'minimumStoredMwh')::numeric*stored[2] OR stored[1]>(params->>'maximumStoredMwh')::numeric*stored[2]
   OR(params->>'throughputPolicy'='grid_charge_plus_discharge' AND throughput[1]>(params->>'maximumThroughputMwh')::numeric*throughput[2]) THEN RAISE EXCEPTION 'operating_battery_state';END IF;
   energy=discharge;fuel=NULL;
   result=result||jsonb_build_array(jsonb_build_object('ordinal',i,'name','charge','from',at_from,'to',at_to,'quantity',f.energy_quantity_code,'numerator',charge[1]::text,'denominator',charge[2]::text),
    jsonb_build_object('ordinal',i,'name','variable','from',at_from,'to',at_to,'quantity',f.energy_quantity_code,'numerator',(trading.operating_add(charge,discharge))[1]::text,'denominator',(trading.operating_add(charge,discharge))[2]::text));
  ELSIF kind='thermal' THEN
   IF NOT physical.power_operating_keys(item,ARRAY['commitment','outputMw','availableMw','startupInstructionAt']) THEN RAISE EXCEPTION 'operating_booking_input';END IF;
   PERFORM trading.operating_require_decimals(item,ARRAY['outputMw','availableMw']);
   output=(trading.operating_decimal(item->>'outputMw'))[1]/(trading.operating_decimal(item->>'outputMw'))[2];available=(item->>'availableMw')::numeric;
   next_state=item->>'commitment';online=next_state='online';started=online AND state='offline';stopped=next_state='offline' AND state='online';
   IF next_state IS NULL OR next_state NOT IN('online','offline') OR available<0 OR available>(params->>'maximumOutputMw')::numeric OR output<0
   OR(online AND(output<(params->>'minimumOutputMw')::numeric OR output>available)) OR(NOT online AND output<>0) THEN RAISE EXCEPTION 'operating_thermal_output';END IF;
   IF started THEN
    issued=CASE WHEN item->'startupInstructionAt'='null'::jsonb THEN NULL ELSE trading.operating_instant(item->>'startupInstructionAt') END;
    IF issued IS NULL OR issued>at_from OR issued<state_from OR at_from-issued<make_interval(mins=>(params->>'startupLeadMinutes')::integer)
    OR issued-state_from<make_interval(mins=>(params->>'minimumDownMinutes')::integer) OR starts>=(params->>'maximumStarts')::integer
    OR output>(params->>'startupMaximumOutputMw')::numeric THEN RAISE EXCEPTION 'operating_thermal_start';END IF;starts=starts+1;
   ELSIF item->'startupInstructionAt'<>'null'::jsonb THEN RAISE EXCEPTION 'operating_thermal_start';END IF;
   IF stopped AND(at_from-state_from<make_interval(mins=>(params->>'minimumUpMinutes')::integer) OR previous>(params->>'shutdownMaximumOutputMw')::numeric) THEN RAISE EXCEPTION 'operating_thermal_stop';END IF;
   IF online AND NOT started AND abs(output-previous)*60>(CASE WHEN output>=previous THEN(params->>'rampUpMwPerHour')::numeric ELSE(params->>'rampDownMwPerHour')::numeric END)*f.interval_minutes THEN RAISE EXCEPTION 'operating_thermal_ramp';END IF;
   IF started OR stopped THEN state_from=at_from;END IF;
   energy=trading.operating_multiply(trading.operating_decimal(item->>'outputMw'),hours);fuel=zero;
   IF online THEN
    fuel=trading.operating_add(trading.operating_multiply(energy,trading.operating_decimal(CASE basis WHEN 'guaranteed' THEN s->>'guaranteedHeatRate' ELSE params->>'consumptionHeatRate' END)),
     trading.operating_multiply(hours,trading.operating_decimal(CASE basis WHEN 'guaranteed' THEN s->>'guaranteedNoLoadFuelPerHour' ELSE params->>'consumptionNoLoadFuelPerHour' END)));
    IF started THEN fuel=trading.operating_add(fuel,trading.operating_decimal(CASE basis WHEN 'guaranteed' THEN s->>'guaranteedStartupFuel' ELSE params->>'consumptionStartupFuel' END));END IF;
   END IF;
   result=result||jsonb_build_array(jsonb_build_object('ordinal',i,'name',CASE WHEN composed THEN 'guaranteed_fuel' ELSE 'fuel' END,'from',at_from,'to',at_to,'quantity',f.fuel_quantity_code,'numerator',fuel[1]::text,'denominator',fuel[2]::text));
   IF composed THEN
    consumption=zero;
    IF online THEN
     consumption=trading.operating_add(trading.operating_multiply(energy,trading.operating_decimal(params->>'consumptionHeatRate')),trading.operating_multiply(hours,trading.operating_decimal(params->>'consumptionNoLoadFuelPerHour')));
     IF started THEN consumption=trading.operating_add(consumption,trading.operating_decimal(params->>'consumptionStartupFuel'));END IF;
    END IF;
    result=result||jsonb_build_array(jsonb_build_object('ordinal',i,'name','consumption_fuel','from',at_from,'to',at_to,'quantity',f.fuel_quantity_code,'numerator',consumption[1]::text,'denominator',consumption[2]::text));
   END IF;
   state=next_state;previous=output;
  ELSE
   IF NOT physical.power_operating_keys(item,ARRAY['productionMwh','requiredDeliveryMwh']) OR item->'requiredDeliveryMwh'<>'null'::jsonb THEN RAISE EXCEPTION 'operating_booking_input';END IF;
   PERFORM trading.operating_require_decimals(item,ARRAY['productionMwh']);
   energy=trading.operating_multiply(trading.operating_decimal(item->>'productionMwh'),trading.operating_ratio(n,d));
   IF energy[1]<0 THEN RAISE EXCEPTION 'operating_solar_output';END IF;
   result=result||jsonb_build_array(jsonb_build_object('ordinal',i,'name','payment','from',at_from,'to',at_to,'quantity',f.energy_quantity_code,'numerator',energy[1]::text,'denominator',energy[2]::text));
  END IF;
  result=result||jsonb_build_array(jsonb_build_object('ordinal',i,'name','energy','from',at_from,'to',at_to,'quantity',f.energy_quantity_code,'numerator',energy[1]::text,'denominator',energy[2]::text));
  i=i+1;
 END LOOP;
 IF kind='battery' AND(stored[1]<(s->>'terminalMinimumMwh')::numeric*stored[2] OR stored[1]>(s->>'terminalMaximumMwh')::numeric*stored[2]) THEN RAISE EXCEPTION 'operating_battery_terminal';END IF;
 IF kind='thermal' AND at_to-state_from<make_interval(mins=>(CASE state WHEN 'online' THEN params->>'minimumUpMinutes' ELSE params->>'minimumDownMinutes' END)::integer) THEN RAISE EXCEPTION 'operating_thermal_terminal';END IF;
 RETURN result;
END$function$;
CREATE OR REPLACE FUNCTION trading.require_operating_program_shape(catalog uuid, schedule uuid, member_slot integer, instrument uuid)
 RETURNS void
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE h trading.power_operating_instruments;m trading.operating_memberships;p trading.operating_side_prices;q ref_data.quote_specifications;
 cell record;guard trading.side_schedule_rows;amount trading.side_schedule_rows;quantity trading.side_schedule_rows;price trading.side_schedule_rows;
 pv trading.side_schedule_rows;discount trading.side_schedule_rows;port_name text;expected integer;payment timestamptz;cash integer;root integer;side jsonb;composed boolean;once boolean;delivery boolean;
BEGIN
 SELECT * INTO STRICT h FROM trading.power_operating_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument);
 SELECT * INTO STRICT m FROM trading.operating_memberships WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,member_slot);
 SELECT * INTO STRICT p FROM trading.operating_side_prices WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,member_slot);
 SELECT * INTO STRICT q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code);
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedules s JOIN trading.sides d ON(d.catalog_id,d.id)=(s.catalog_id,s.side_id)
 WHERE(s.catalog_id,s.id,s.side_id,d.family,d.definition_revision_id,d.reference_catalog_id,d.reference_revision_id)=
 (catalog,schedule,m.side_id,'operating',h.definition_revision_id,h.reference_catalog_id,h.reference_revision_id))
 OR EXISTS(SELECT 1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule))
 OR EXISTS(SELECT 1 FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id)=(catalog,schedule))
 OR EXISTS(SELECT 1 FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule) AND operation NOT IN('quantity_literal','constant_price','operating_contract_price','amount','discount','pv','output_guard','sum'))
 THEN RAISE EXCEPTION 'operating_schedule_shape';END IF;
 composed=h.terms->'plan' ? 'composition';
 side=h.terms->'plan'->'composition'->'sides'->member_slot;
 delivery=composed AND side->>'kind'='delivery';
 once=CASE WHEN composed THEN side->>'quantitySource'='horizon_right' ELSE m.name='access_fee' END;
 port_name=CASE WHEN composed THEN trading.operating_source_port(side->>'quantitySource') ELSE CASE m.name WHEN 'charging_energy' THEN 'charge' WHEN 'fuel_input' THEN 'fuel' WHEN 'payment_energy' THEN 'payment'
 WHEN 'variable_fee' THEN CASE WHEN h.terms->>'kind'='battery' THEN 'variable' WHEN h.terms->>'kind'='solar' THEN 'payment' ELSE 'energy' END ELSE 'energy' END END;
 expected=CASE WHEN once THEN 1 ELSE(SELECT count(*) FROM trading.operating_plan_quantities WHERE(catalog_id,instrument_id,name)=(catalog,instrument,port_name))END;
 IF expected<1 OR(SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))<>5*expected+(CASE WHEN p.mode='fixed' THEN 1 ELSE expected END)+1
 OR(SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,'sum'))<>1
 OR(SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,CASE WHEN p.mode='fixed' THEN 'constant_price' ELSE 'operating_contract_price' END))<>(CASE WHEN p.mode='fixed' THEN 1 ELSE expected END)
 OR(SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>(CASE WHEN p.mode='fixed' THEN 0 ELSE 2*expected END)
 OR(SELECT count(*) FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id)=(catalog,schedule))<>3*expected+1
 OR(SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,'output_guard'))<>expected
 OR(SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,'quantity_literal'))<>expected
 OR(SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,'amount'))<>expected
 OR(SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,'pv'))<>expected
 OR(SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation)=(catalog,schedule,'discount'))<>expected
 THEN RAISE EXCEPTION 'operating_schedule_shape';END IF;
 FOR cell IN SELECT * FROM trading.operating_plan_quantities WHERE(catalog_id,instrument_id,name)=(catalog,instrument,port_name) ORDER BY ordinal LIMIT expected LOOP
  payment=CASE WHEN delivery THEN cell.interval_to ELSE trading.operating_payment_at(cell.interval_from,
   (CASE WHEN composed THEN side->'paymentRule' ELSE h.terms->'plan'->'valuation'->'paymentRule' END->>'days')::integer) END;
  SELECT * INTO STRICT guard FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id,operation,payment_ordinal)=(catalog,schedule,'output_guard',cell.ordinal);
  IF(guard.interval_from,guard.interval_to,guard.value_at,guard.parameters->>'measure',guard.parameters->>'lifecycle',guard.parameters->>'pricePolicy')
   IS DISTINCT FROM(cell.interval_from,cell.interval_to,payment,'present_value_v1',CASE WHEN delivery THEN 'future_operating_delivery_v1' ELSE 'undue_payment_v1' END,'selected_context_v1')
   OR(guard.operating_date,guard.hour_beginning,guard.minute,guard.occurrence) IS DISTINCT FROM((cell.interval_from AT TIME ZONE 'UTC')::date,extract(hour FROM cell.interval_from AT TIME ZONE 'UTC')::smallint,extract(minute FROM cell.interval_from AT TIME ZONE 'UTC')::smallint,1::smallint) THEN RAISE EXCEPTION 'operating_schedule_time';END IF;
  SELECT r.* INTO STRICT amount FROM trading.side_schedule_edges e JOIN trading.side_schedule_rows r
   ON(r.catalog_id,r.schedule_id,r.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   WHERE(e.catalog_id,e.schedule_id,e.target,e.port)=(catalog,schedule,guard.ordinal,'amount');
  SELECT r.* INTO STRICT pv FROM trading.side_schedule_edges e JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   WHERE(e.catalog_id,e.schedule_id,e.target,e.port)=(catalog,schedule,guard.ordinal,'discounted');
  SELECT r.* INTO STRICT quantity FROM trading.side_schedule_edges e JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   WHERE(e.catalog_id,e.schedule_id,e.target,e.port)=(catalog,schedule,amount.ordinal,'quantity');
  SELECT r.* INTO STRICT price FROM trading.side_schedule_edges e JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   WHERE(e.catalog_id,e.schedule_id,e.target,e.port)=(catalog,schedule,amount.ordinal,'price');
  SELECT r.* INTO STRICT discount FROM trading.side_schedule_edges e JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   WHERE(e.catalog_id,e.schedule_id,e.target,e.port)=(catalog,schedule,pv.ordinal,'discount');
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,name,measure,row_ordinal)=(catalog,schedule,'operating/'||cell.ordinal||'/quantity','schedule_quantity',quantity.ordinal))
   OR NOT EXISTS(SELECT 1 FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,name,measure,row_ordinal)=(catalog,schedule,'operating/'||cell.ordinal||(CASE WHEN delivery THEN '/delivery-value' ELSE '/cash' END),CASE WHEN delivery THEN 'physical_value' ELSE 'contractual_amount' END,amount.ordinal))
   OR NOT EXISTS(SELECT 1 FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,name,measure,row_ordinal)=(catalog,schedule,'operating/'||cell.ordinal||'/pv','contribution_pv',guard.ordinal))
   THEN RAISE EXCEPTION 'operating_schedule_root';END IF;
  IF (delivery AND guard.parameters->>'paymentRule' IS DISTINCT FROM 'not_applicable_delivery_v1')
   OR discount.parameters->>'policy' IS DISTINCT FROM 'context_required_v1' THEN RAISE EXCEPTION 'operating_schedule_time';END IF;
  IF amount.operation<>'amount' OR pv.operation<>'pv' OR discount.operation<>'discount' OR discount.value_at<>payment
   OR NOT EXISTS(SELECT 1 FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id,target,port,source)=(catalog,schedule,pv.ordinal,'amount',amount.ordinal))
   OR quantity.operation<>'quantity_literal' OR(quantity.unit_catalog_id,quantity.unit_revision_id,quantity.unit_code) IS DISTINCT FROM(h.reference_catalog_id,h.reference_revision_id,q.quantity_code)
   OR(quantity.parameters->>'numerator')::numeric*(CASE WHEN once THEN 1 ELSE cell.denominator END)<>
     (CASE WHEN once THEN 1 ELSE cell.numerator END)*(quantity.parameters->>'denominator')::numeric
   OR(price.unit_catalog_id,price.unit_revision_id,price.unit_code,price.currency_code,price.quote_multiplier) IS DISTINCT FROM(h.reference_catalog_id,h.reference_revision_id,q.quantity_code,q.currency_code,q.multiplier)
  THEN RAISE EXCEPTION 'operating_schedule_quantity';END IF;
  IF p.mode='fixed' THEN
   IF price.operation<>'constant_price' OR(price.parameters->>'numerator')::numeric<>p.fixed_price*(price.parameters->>'denominator')::numeric THEN RAISE EXCEPTION 'operating_schedule_price';END IF;
  ELSE
   IF price.operation<>'operating_contract_price' OR price.parameters->>'selection'<>'selected_contract_flat_v1'
    OR(SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id,row_ordinal)=(catalog,schedule,price.ordinal))<>2
    OR NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id,row_ordinal,port,kind,source_catalog_id,definition_revision_id,curve_code,contract_id)=
     (catalog,schedule,price.ordinal,'market','contract_price',catalog,h.definition_revision_id,p.curve_code,p.contract_id))
    OR NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id,row_ordinal,port,kind,reference_catalog_id,reference_revision_id,quote_code)=
     (catalog,schedule,price.ordinal,'quote','quote',h.reference_catalog_id,h.reference_revision_id,p.quote_code)) THEN RAISE EXCEPTION 'operating_schedule_price';END IF;
  END IF;
 END LOOP;
 SELECT r.ordinal INTO STRICT root FROM trading.side_schedule_roots x JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(x.catalog_id,x.schedule_id,x.row_ordinal)
 WHERE(x.catalog_id,x.schedule_id,x.name,x.measure,r.operation)=(catalog,schedule,'pv','pv','sum');
 IF(SELECT count(*) FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id,target,port)=(catalog,schedule,root,'input'))<>expected
 OR EXISTS(SELECT 1 FROM trading.side_schedule_edges e JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(e.catalog_id,e.schedule_id,e.source)
 WHERE(e.catalog_id,e.schedule_id,e.target)=(catalog,schedule,root) AND r.operation<>'output_guard') OR(SELECT count(DISTINCT source) FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id,target,port)=(catalog,schedule,root,'input'))<>expected THEN RAISE EXCEPTION 'operating_schedule_root';END IF;
EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'operating_schedule_shape';
END$function$;
CREATE OR REPLACE FUNCTION security.book_operating_right(catalog uuid, body jsonb, prepared jsonb, valuation timestamp with time zone)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE actor uuid;operation uuid;prior trading.operating_booking_requests;f physical.power_operating_facility_versions;
 staged trading.side_schedule_requests;plan jsonb;canonical jsonb;facts jsonb;rows jsonb;row jsonb;v jsonb;price jsonb;
 publication integer;trade uuid;instrument uuid;outcome jsonb;book uuid;member record;name text;expected_names text[];slot integer=0;quote ref_data.quote_specifications;
 start_at timestamptz;end_at timestamptz;kind text;quantity ref_data.quantity_specifications;expected_quantity text;rc uuid;rr uuid;composed boolean;side jsonb;
BEGIN
 actor=security.operating_booking_scope(catalog,true);
 IF length(body::text)>1048576 OR NOT physical.power_operating_keys(prepared,ARRAY['instrumentId','members']) OR NOT physical.power_operating_keys(body,ARRAY['operationId','tradeId','expectedRevision','bookId','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','reason','convention','plan'])
 OR body->>'convention' IS DISTINCT FROM 'supplied_operating_right_trade_v1' OR body->'tradeId' IS DISTINCT FROM 'null'::jsonb OR body->'expectedRevision' IS DISTINCT FROM '0'::jsonb
 OR jsonb_typeof(body->'operationId') IS DISTINCT FROM 'string' OR jsonb_typeof(body->'bookId') IS DISTINCT FROM 'string'
 OR jsonb_typeof(body->'legalEntityId') IS DISTINCT FROM 'string' OR jsonb_typeof(body->'counterpartyId') IS DISTINCT FROM 'string'
 OR jsonb_typeof(body->'legalEntityRevision') IS DISTINCT FROM 'number' OR jsonb_typeof(body->'counterpartyRevision') IS DISTINCT FROM 'number'
 OR jsonb_typeof(body->'reason') IS DISTINCT FROM 'string' OR nullif(btrim(body->>'reason'),'') IS NULL OR length(body->>'reason')>500 OR valuation IS NULL OR NOT isfinite(valuation)
 THEN RAISE EXCEPTION 'operating_booking_input';END IF;
 operation=(body->>'operationId')::uuid;book=(body->>'bookId')::uuid;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR book IS NULL THEN RAISE EXCEPTION 'operating_booking_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.operating_booking_scope(catalog,true);
 SELECT * INTO prior FROM trading.operating_booking_requests WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.command IS DISTINCT FROM body THEN RAISE EXCEPTION 'operating_request_conflict';END IF;
  PERFORM security.operating_booking_scope(catalog,true);RETURN prior.outcome;
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE(catalog_id,operation_id)=(catalog,operation)) THEN RAISE EXCEPTION 'operating_request_conflict';END IF;
 plan=body->'plan';composed=plan ? 'composition';v=CASE WHEN composed THEN plan->'composition' ELSE plan->'valuation' END;
 SELECT * INTO f FROM physical.power_operating_facility_versions WHERE(catalog_id,facility_id,version)=(catalog,(plan->>'facilityId')::uuid,(plan->>'facilityVersion')::integer);
 IF NOT FOUND OR NOT security.has_permission('ReadPhysical') OR NOT security.reference_access(f.reference_catalog_id,'ReadMarketData')
 OR NOT security.reference_version_visible(f.reference_catalog_id,f.reference_revision_id) OR NOT security.market_fact_visible(catalog,f.definition_revision_id,NULL)
 THEN RAISE EXCEPTION 'operating_facility_unavailable' USING ERRCODE='42501';END IF;
 rc=f.reference_catalog_id;rr=f.reference_revision_id;kind=f.kind;
 IF composed THEN
  IF plan->'valuation' IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'operating_composition';END IF;
  PERFORM trading.require_operating_composition(v,kind);
 ELSE
 IF NOT physical.power_operating_keys(v,ARRAY['perspective','measure','currencyCode','rightQuantityCode','discounting','paymentRule','energyPrice','chargingPrice','fuelPrice','fuelQuantityBasis','accessQuoteCode','accessPrice','accessFeeBasis','variablePrice','variableQuantityBasis','startupQuoteCode','startupPrice','startupFeeBasis'])
 OR v->>'perspective' IS DISTINCT FROM 'resource_right_holder_v1' OR v->>'measure' IS DISTINCT FROM 'remaining_operating_value_v1' OR v->'discounting' IS DISTINCT FROM 'false'::jsonb
 OR v->'startupQuoteCode' IS DISTINCT FROM 'null'::jsonb OR v->'startupPrice' IS DISTINCT FROM 'null'::jsonb OR v->'startupFeeBasis' IS DISTINCT FROM 'null'::jsonb
 OR NOT physical.power_operating_keys(v->'paymentRule',ARRAY['frequency','rule','days','dayOfMonth','dayBasis','shortMonth','adjustment','calendar','implementation'])
 OR v->'paymentRule'->>'frequency' IS DISTINCT FROM 'monthly' OR v->'paymentRule'->>'rule' IS DISTINCT FROM 'days_after_period_end'
 OR v->'paymentRule'->>'dayBasis' IS DISTINCT FROM 'calendar_days' OR v->'paymentRule'->>'adjustment' IS DISTINCT FROM 'unadjusted'
 OR jsonb_typeof(v->'paymentRule'->'days') IS DISTINCT FROM 'number' OR v->'paymentRule'->>'implementation' IS DISTINCT FROM 'monthly_payment_date_rules_v1' OR(v->'paymentRule'->>'days')::integer NOT BETWEEN 0 AND 366
 OR v->'paymentRule'->'dayOfMonth' IS DISTINCT FROM 'null'::jsonb OR v->'paymentRule'->'shortMonth' IS DISTINCT FROM 'null'::jsonb OR v->'paymentRule'->'calendar' IS DISTINCT FROM 'null'::jsonb
 THEN RAISE EXCEPTION 'operating_booking_policy';END IF;
 -- Native callers use the same closed scalar shapes as the managed command.
 -- Validate before staging or semantic reuse can conceal malformed prices.
 IF jsonb_typeof(v->'currencyCode') IS DISTINCT FROM 'string' OR jsonb_typeof(v->'rightQuantityCode') IS DISTINCT FROM 'string'
 OR jsonb_typeof(v->'energyPrice') IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'operating_booking_price';END IF;
 FOREACH name IN ARRAY ARRAY['accessQuoteCode','startupQuoteCode','fuelQuantityBasis','accessFeeBasis','variableQuantityBasis'] LOOP
  IF jsonb_typeof(v->name) NOT IN('string','null') THEN RAISE EXCEPTION 'operating_booking_price';END IF;
 END LOOP;
 FOREACH name IN ARRAY ARRAY['accessPrice','variablePrice','startupPrice'] LOOP
  IF jsonb_typeof(v->name) NOT IN('string','null') THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  IF jsonb_typeof(v->name)='string' THEN PERFORM trading.operating_decimal(v->>name);END IF;
 END LOOP;
 FOR price IN SELECT value FROM jsonb_array_elements(jsonb_build_array(v->'energyPrice',v->'chargingPrice',v->'fuelPrice')) LOOP
  IF price='null'::jsonb THEN CONTINUE;END IF;
  IF NOT physical.power_operating_keys(price,ARRAY['mode','quoteCode','fixedPrice','curveCode','contractId'])
   OR jsonb_typeof(price->'mode') IS DISTINCT FROM 'string' OR jsonb_typeof(price->'quoteCode') IS DISTINCT FROM 'string'
  THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  IF price->>'mode'='fixed' THEN
   IF jsonb_typeof(price->'fixedPrice') IS DISTINCT FROM 'string' OR price->'curveCode' IS DISTINCT FROM 'null'::jsonb
    OR price->'contractId' IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'operating_booking_price';END IF;
   PERFORM trading.operating_decimal(price->>'fixedPrice');
  ELSIF price->>'mode'='selected_contract_flat_v1' THEN
   IF price->'fixedPrice' IS DISTINCT FROM 'null'::jsonb OR jsonb_typeof(price->'curveCode') IS DISTINCT FROM 'string'
    OR jsonb_typeof(price->'contractId') IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  ELSE RAISE EXCEPTION 'operating_booking_price';END IF;
 END LOOP;
 IF(kind='battery' AND(v->'chargingPrice'='null'::jsonb OR v->'fuelPrice'<>'null'::jsonb OR v->'fuelQuantityBasis'<>'null'::jsonb))
 OR(kind<>'battery' AND v->'chargingPrice'<>'null'::jsonb) OR(kind<>'thermal' AND(v->'fuelPrice'<>'null'::jsonb OR v->'fuelQuantityBasis'<>'null'::jsonb))
 OR(v->'variablePrice'='null'::jsonb AND v->'variableQuantityBasis'<>'null'::jsonb)
 OR(v->'variablePrice'<>'null'::jsonb AND v->>'variableQuantityBasis' IS DISTINCT FROM CASE kind WHEN 'battery' THEN 'grid_charge_plus_discharge' WHEN 'thermal' THEN 'energy_output' ELSE 'payment_energy' END)
 OR(v->'accessPrice'='null'::jsonb AND(v->'accessQuoteCode'<>'null'::jsonb OR v->'accessFeeBasis'<>'null'::jsonb))
 OR(v->'accessPrice'<>'null'::jsonb AND(v->>'accessFeeBasis' IS DISTINCT FROM 'preview_horizon_once_v1' OR v->'accessQuoteCode'='null'::jsonb))
 THEN RAISE EXCEPTION 'operating_booking_policy';END IF;
 END IF;
 SELECT * INTO quantity FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(rc,rr,v->>'rightQuantityCode');
 IF NOT FOUND OR quantity.unit_code<>'resource_right' OR NOT EXISTS(SELECT 1 FROM ref_data.dimensions d
 WHERE(d.catalog_id,d.revision_id,d.code,d.mass_power,d.length_power,d.time_power,d.temperature_power,d.amount_power,d.current_power,d.luminous_power)=
 (rc,rr,quantity.dimension_code,0,0,0,0,0,0,0)) OR quantity.commodity_code<>(SELECT commodity_code FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(rc,rr,f.energy_quantity_code)) THEN RAISE EXCEPTION 'operating_right_quantity';END IF;
 rows=trading.operating_plan_admission(f,plan,valuation);
 IF composed THEN
  FOR side IN SELECT value FROM jsonb_array_elements(v->'sides') LOOP
   IF side->>'kind'='payment' AND EXISTS(SELECT 1 FROM jsonb_array_elements(rows) x
    WHERE (x->>'name'=trading.operating_source_port(side->>'quantitySource') OR side->>'quantitySource'='horizon_right' AND x->>'name'='energy' AND x->>'ordinal'='0')
    AND trading.operating_payment_at((x->>'from')::timestamptz,(side->'paymentRule'->>'days')::integer)<(x->>'to')::timestamptz)
   THEN RAISE EXCEPTION 'operating_booking_policy';END IF;
  END LOOP;
 ELSE
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(rows) x WHERE trading.operating_payment_at((x->>'from')::timestamptz,(v->'paymentRule'->>'days')::integer)<(x->>'to')::timestamptz) THEN RAISE EXCEPTION 'operating_booking_policy';END IF;
 END IF;
 SELECT min((value->>'from')::timestamptz),max((value->>'to')::timestamptz) INTO start_at,end_at FROM jsonb_array_elements(rows);
 canonical=trading.operating_canonical_value(plan-'snapshotId');
 IF kind='solar' THEN
  price=canonical->'solarRight';
  DECLARE fraction numeric[];BEGIN fraction=trading.operating_ratio((price->>'entitlementNumerator')::numeric,(price->>'entitlementDenominator')::numeric);
   canonical=jsonb_set(canonical,'{solarRight,entitlementNumerator}',to_jsonb(fraction[1]::text));
   canonical=jsonb_set(canonical,'{solarRight,entitlementDenominator}',to_jsonb(fraction[2]::text));END;
 END IF;
 PERFORM security.require_trade_parties(catalog,jsonb_build_object('tradeDate',(security.stamp_trade_booking(catalog) AT TIME ZONE 'UTC')::date,'legalEntityId',body->'legalEntityId','legalEntityRevision',body->'legalEntityRevision',
 'counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision'),valuation);
 facts=jsonb_build_object('plan',canonical,'kind',kind,'bookId',book,'legalEntityId',(body->>'legalEntityId')::uuid,'legalEntityRevision',(body->>'legalEntityRevision')::integer,
  'counterpartyId',(body->>'counterpartyId')::uuid,'counterpartyRevision',(body->>'counterpartyRevision')::integer);
 SELECT jsonb_build_object('operationId',operation,'tradeId',r.trade_id,'revision',r.revision,'instrumentId',r.instrument_id,'bindingId',r.instrument_id,'changed',false)
 INTO outcome FROM trading.operating_trade_revisions r JOIN trading.power_operating_instruments h USING(catalog_id,instrument_id)
 WHERE r.catalog_id=catalog AND h.terms=facts;
 IF FOUND THEN
  INSERT INTO trading.operating_booking_requests VALUES(catalog,actor,operation,body,outcome);
  PERFORM security.operating_booking_scope(catalog,true);
  IF NOT security.has_permission('ReadPhysical') OR NOT security.reference_access(rc,'ReadMarketData') OR NOT security.reference_version_visible(rc,rr) OR NOT security.market_fact_visible(catalog,f.definition_revision_id,NULL)
  THEN RAISE EXCEPTION 'operating_booking_denied' USING ERRCODE='42501';END IF;
  RETURN outcome;
 END IF;
 IF EXISTS(SELECT 1 FROM trading.power_operating_instruments h JOIN physical.power_operating_facility_versions old ON(old.catalog_id,old.facility_id,old.version)=(h.catalog_id,h.facility_id,h.facility_version)
 WHERE h.catalog_id=catalog AND old.reference_catalog_id=rc AND old.facility_code=f.facility_code AND h.delivery_from<end_at AND start_at<h.delivery_to)
 THEN RAISE EXCEPTION 'operating_right_overlap';END IF;

 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
 WHERE(b.catalog_id,b.id)=(catalog,book) AND b.effective_at<=valuation AND p.effective_at<=valuation) THEN RAISE EXCEPTION 'operating_booking_book';END IF;
 SELECT * INTO staged FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=(catalog,operation);
 IF NOT FOUND OR staged.actor_id<>actor OR staged.command_hash<>sha256(convert_to(body::text,'UTF8')) OR staged.permission<>'BookTrade' THEN RAISE EXCEPTION 'operating_schedule';END IF;
 PERFORM security.lock_side_schedule_request(catalog,operation);
 IF composed THEN
  SELECT array_agg(value->>'name' ORDER BY ordinal) INTO expected_names FROM jsonb_array_elements(v->'sides') WITH ORDINALITY s(value,ordinal);
 ELSE
 expected_names=ARRAY[CASE kind WHEN 'battery' THEN 'discharging_energy' WHEN 'thermal' THEN 'energy_output' ELSE 'payment_energy' END];
 IF kind='battery' THEN expected_names=array_append(expected_names,'charging_energy');ELSIF kind='thermal' THEN expected_names=array_append(expected_names,'fuel_input');END IF;
 IF v->'variablePrice'<>'null'::jsonb THEN expected_names=array_append(expected_names,'variable_fee');END IF;
 IF v->'accessPrice'<>'null'::jsonb THEN expected_names=array_append(expected_names,'access_fee');END IF;
 END IF;
 IF(SELECT count(*) FROM trading.side_schedule_request_items WHERE(catalog_id,request_id)=(catalog,operation))<>cardinality(expected_names)
 OR jsonb_array_length(prepared->'members')<>cardinality(expected_names) THEN RAISE EXCEPTION 'operating_schedule';END IF;
 instrument=(prepared->>'instrumentId')::uuid;IF instrument IS NULL OR instrument='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'operating_booking_input';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());trade=gen_random_uuid();
 INSERT INTO trading.instruments(catalog_id,id,publication_order,effective_at) VALUES(catalog,instrument,publication,valuation);
 INSERT INTO trading.power_operating_instruments VALUES(catalog,instrument,f.definition_revision_id,rc,rr,f.facility_id,f.version,start_at,end_at,v->>'rightQuantityCode',v->>'currencyCode',facts,publication,valuation);
 INSERT INTO trading.operating_plan_quantities SELECT catalog,instrument,(x->>'ordinal')::integer,x->>'name',(x->>'from')::timestamptz,(x->>'to')::timestamptz,x->>'quantity',rc,rr,(x->>'numerator')::numeric,(x->>'denominator')::numeric FROM jsonb_array_elements(rows)x;
 FOR member IN SELECT i.slot,i.schedule_id,h.side_id FROM trading.side_schedule_request_items i JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(i.catalog_id,i.schedule_id)
 WHERE(i.catalog_id,i.request_id)=(catalog,operation) ORDER BY i.slot LOOP
  name=expected_names[member.slot+1];
  IF(prepared->'members'->member.slot->>'name') IS DISTINCT FROM name THEN RAISE EXCEPTION 'operating_schedule';END IF;
  side=CASE WHEN composed THEN v->'sides'->member.slot ELSE NULL END;
  price=CASE WHEN composed THEN side->'price' ELSE CASE name WHEN 'charging_energy' THEN v->'chargingPrice' WHEN 'fuel_input' THEN v->'fuelPrice'
   WHEN 'variable_fee' THEN jsonb_build_object('mode','fixed','quoteCode',v->'energyPrice'->>'quoteCode','fixedPrice',v->'variablePrice','curveCode',NULL,'contractId',NULL)
   WHEN 'access_fee' THEN jsonb_build_object('mode','fixed','quoteCode',v->>'accessQuoteCode','fixedPrice',v->'accessPrice','curveCode',NULL,'contractId',NULL) ELSE v->'energyPrice' END END;
  IF NOT physical.power_operating_keys(price,ARRAY['mode','quoteCode','fixedPrice','curveCode','contractId']) THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  expected_quantity=CASE WHEN composed THEN CASE
   WHEN side->>'quantitySource' IN('guaranteed_fuel','consumption_fuel') THEN f.fuel_quantity_code
   WHEN side->>'quantitySource'='horizon_right' THEN v->>'rightQuantityCode' ELSE f.energy_quantity_code END
   ELSE CASE name WHEN 'fuel_input' THEN f.fuel_quantity_code WHEN 'access_fee' THEN v->>'rightQuantityCode' ELSE f.energy_quantity_code END END;
  SELECT q.* INTO quote FROM ref_data.quote_specifications q WHERE(q.catalog_id,q.revision_id,q.code,q.kind,q.quantity_code,q.currency_code)=(rc,rr,price->>'quoteCode','outright',expected_quantity,v->>'currencyCode');
  IF NOT FOUND THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  IF price->>'mode'='fixed' THEN
   PERFORM trading.operating_decimal(price->>'fixedPrice');IF price->'curveCode'<>'null'::jsonb OR price->'contractId'<>'null'::jsonb THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  ELSIF price->>'mode'='selected_contract_flat_v1' THEN
   IF price->'fixedPrice'<>'null'::jsonb OR NOT EXISTS(SELECT 1 FROM market_data.curves c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)
    JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(c.catalog_id,c.revision_id,c.code,c.quote_code,k.id)=(catalog,f.definition_revision_id,price->>'curveCode',quote.code,(price->>'contractId')::uuid)
    AND k.delivery_from<=(start_at AT TIME ZONE 'UTC')::date AND k.delivery_to>((end_at-interval '1 microsecond') AT TIME ZONE 'UTC')::date) THEN RAISE EXCEPTION 'operating_booking_price';END IF;
  ELSE RAISE EXCEPTION 'operating_booking_price';END IF;
  INSERT INTO trading.operating_memberships VALUES(catalog,instrument,member.slot,member.side_id,name,CASE WHEN composed THEN (side->>'coefficient')::integer WHEN member.slot=0 THEN 1 ELSE -1 END);
  INSERT INTO trading.operating_side_prices VALUES(catalog,instrument,member.slot,quote.code,rc,rr,price->>'mode',(price->>'fixedPrice')::numeric,price->>'curveCode',(price->>'contractId')::uuid,f.definition_revision_id);
  PERFORM trading.require_operating_program_shape(catalog,member.schedule_id,member.slot,instrument);
 END LOOP;
 INSERT INTO trading.trades(catalog_id,id,book_id,operation_id,actor_id,request,publication_order,effective_at) VALUES(catalog,trade,book,operation,actor,body,publication,valuation);
 INSERT INTO trading.operating_trade_revisions VALUES(catalog,trade,1,instrument,book,operation,actor,publication,valuation,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer);
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'revision',1,'instrumentId',instrument,'bindingId',instrument,'changed',true);
 INSERT INTO trading.operating_booking_requests VALUES(catalog,actor,operation,body,outcome);
 PERFORM security.operating_booking_scope(catalog,true);
 IF NOT security.has_permission('ReadPhysical') OR NOT security.reference_access(rc,'ReadMarketData') OR NOT security.reference_version_visible(rc,rr) OR NOT security.market_fact_visible(catalog,f.definition_revision_id,NULL) THEN RAISE EXCEPTION 'operating_booking_denied' USING ERRCODE='42501';END IF;RETURN outcome;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow OR null_value_not_allowed THEN RAISE EXCEPTION 'operating_booking_input';
END$function$;
CREATE OR REPLACE FUNCTION trading.request_side_calendar_policy(command jsonb, slot_number integer)
 RETURNS jsonb
 LANGUAGE plpgsql
 IMMUTABLE
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE selected jsonb;
BEGIN
 IF command->>'convention'='supplied_operating_right_trade_v1' AND command->'plan' ? 'composition' THEN
  IF command->'plan'->'valuation' IS DISTINCT FROM 'null'::jsonb OR
   command#>>'{plan,composition,implementation}' IS DISTINCT FROM 'operating_side_composition_v2' OR
   slot_number<0 OR slot_number>=jsonb_array_length(command#>'{plan,composition,sides}')
  THEN RAISE EXCEPTION 'side_calendar_policy';END IF;
  selected=command#>'{plan,composition,sides}'->slot_number;
  IF selected->>'kind' NOT IN('payment','delivery') OR selected->>'kind' IS NULL OR
   (selected->>'kind'='delivery' AND selected->'paymentRule' IS DISTINCT FROM 'null'::jsonb) OR
   (selected->>'kind'='payment' AND (selected#>>'{paymentRule,frequency}' IS DISTINCT FROM 'monthly' OR
    selected#>>'{paymentRule,rule}' IS DISTINCT FROM 'days_after_period_end' OR selected#>>'{paymentRule,dayBasis}' IS DISTINCT FROM 'calendar_days' OR
    selected#>>'{paymentRule,adjustment}' IS DISTINCT FROM 'unadjusted' OR selected#>'{paymentRule,calendar}' IS DISTINCT FROM 'null'::jsonb))
  THEN RAISE EXCEPTION 'side_calendar_policy';END IF;
  RETURN jsonb_build_object('implementation','side_calendar_policy_v1',
   'delivery',jsonb_build_object('applicable',false,'calendar',NULL),
   'observation',jsonb_build_object('applicable',false,'calendar',NULL),
   'payment',jsonb_build_object('applicable',false,'calendar',NULL));
 END IF;
 IF command->>'convention'='supplied_operating_right_trade_v1' THEN
  IF jsonb_typeof(command->'plan'->'valuation') IS DISTINCT FROM 'object' OR
   command#>>'{plan,valuation,paymentRule,frequency}' IS DISTINCT FROM 'monthly' OR
   command#>>'{plan,valuation,paymentRule,rule}' IS DISTINCT FROM 'days_after_period_end' OR
   command#>>'{plan,valuation,paymentRule,dayBasis}' IS DISTINCT FROM 'calendar_days' OR
   command#>>'{plan,valuation,paymentRule,adjustment}' IS DISTINCT FROM 'unadjusted' OR
   command#>'{plan,valuation,paymentRule,calendar}' IS DISTINCT FROM 'null'::jsonb
  THEN RAISE EXCEPTION 'side_calendar_policy';END IF;
  RETURN jsonb_build_object('implementation','side_calendar_policy_v1',
   'delivery',jsonb_build_object('applicable',false,'calendar',NULL),
   'observation',jsonb_build_object('applicable',false,'calendar',NULL),
   'payment',jsonb_build_object('applicable',false,'calendar',NULL));
 END IF;
 IF command->'terms' ? 'assignmentCode' AND command->'terms' ? 'vintageFrom' THEN
  RETURN command->'terms'->'calendarPolicy';
 END IF;
 IF command->>'schema'='scheduled_financial_swap_booking_v1' THEN
  IF jsonb_typeof(command->'body') IS DISTINCT FROM 'object' OR NOT command ?& ARRAY['schema','body','valuation','booking']
   OR command-ARRAY['schema','body','valuation','booking']<>'{}'::jsonb THEN RAISE EXCEPTION 'side_calendar_policy';END IF;
  command=command->'body';
 END IF;
 IF (CASE WHEN jsonb_typeof(command->'terms'->'memberships')='array' THEN 1 ELSE 0 END
  +CASE WHEN jsonb_typeof(command->'physicalSides')='array' THEN 1 ELSE 0 END
  +CASE WHEN jsonb_typeof(command->'scheduled')='object' THEN 1 ELSE 0 END
  +CASE WHEN jsonb_typeof(command->'deliveryCalendarPolicy')='object' THEN 1 ELSE 0 END
  +CASE WHEN jsonb_typeof(command->'fixed')='object' OR jsonb_typeof(command->'firstFloating')='object'
    OR jsonb_typeof(command->'floating')='object' THEN 1 ELSE 0 END)<>1
  OR(jsonb_typeof(command->'fixed')='object' AND jsonb_typeof(command->'firstFloating')='object')
 THEN RAISE EXCEPTION 'side_calendar_policy';END IF;
 IF jsonb_typeof(command->'terms'->'memberships')='array' THEN
  selected=command->'terms'->'memberships'->slot_number->'side'->'calendarPolicy';
 ELSIF jsonb_typeof(command->'physicalSides')='array' THEN
  IF slot_number<jsonb_array_length(command->'physicalSides') THEN
   selected=command->'physicalSides'->slot_number->'calendarPolicy';
  ELSE selected=command->'cashSides'->(slot_number-jsonb_array_length(command->'physicalSides'))->'calendarPolicy';END IF;
 ELSIF jsonb_typeof(command->'scheduled')='object' THEN
  selected=command->'scheduled'->(CASE slot_number WHEN 0 THEN 'first' WHEN 1 THEN 'second' END)->'calendarPolicy';
 ELSIF jsonb_typeof(command->'deliveryCalendarPolicy')='object' THEN
  selected=command->(CASE slot_number WHEN 0 THEN 'deliveryCalendarPolicy' WHEN 1 THEN 'considerationCalendarPolicy' WHEN 2 THEN 'feeCalendarPolicy' END);
 ELSE
  selected=CASE slot_number WHEN 0 THEN coalesce(nullif(command->'firstFloating','null'::jsonb),command->'fixed')->'calendarPolicy'
   WHEN 1 THEN command->'floating'->'calendarPolicy' END;
 END IF;
 IF jsonb_typeof(selected) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'side_calendar_policy';END IF;
 RETURN selected;
END$function$;

ALTER FUNCTION trading.operating_source_port(text) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.require_operating_composition(jsonb,text) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.operating_side_name_valid(text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.operating_source_port(text),trading.require_operating_composition(jsonb,text),trading.operating_side_name_valid(text) FROM PUBLIC;
COMMIT;
