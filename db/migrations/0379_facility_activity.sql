BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- Add entered decimals and proportional deliveries exactly, including equal-days fractions.
CREATE FUNCTION desk.add_quantity(state numeric[],n numeric,d numeric) RETURNS numeric[] LANGUAGE plpgsql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
DECLARE den numeric=d*state[2];num numeric=state[1]*d+n*state[2];factor numeric;divisor numeric;
BEGIN
 factor=power(10::numeric,greatest(scale(num),scale(den)));num=trunc(num*factor);den=trunc(den*factor);divisor=gcd(num,den);
 RETURN ARRAY[div(num,divisor),div(den,divisor)];
END$$;
CREATE AGGREGATE desk.sum_quantity(numeric,numeric)(SFUNC=desk.add_quantity,STYPE=numeric[],INITCOND='{0,1}');

CREATE FUNCTION desk.facility_activity(catalog uuid,profile uuid,first_day date,end_day date,capacity_at timestamptz DEFAULT NULL) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE as_at timestamptz;p desk.facility_profiles;facility uuid;sv trading.storage_facility_versions;fuel market_data.gas_valuation_assignments;result jsonb;
BEGIN
 PERFORM desk.authority(catalog,'ReadTrades',false);
 SELECT coalesce(c.trading_as_of,capacity_at,clock_timestamp()) INTO as_at FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.runtime_login=session_user AND c.transaction_id=pg_current_xact_id_if_assigned();
 SELECT * INTO p FROM desk.facility_profiles x WHERE(x.catalog_id,x.id)=(catalog,profile)
 AND desk.visible(catalog,x.publication_order,x.effective_at) AND desk.source_visible(catalog,x.definition_id);
 IF p.id IS NULL OR first_day IS NULL OR end_day IS NULL OR first_day>=end_day OR end_day-first_day>3660 THEN RAISE EXCEPTION 'desk_facility';END IF;
 SELECT * INTO sv FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.id)=(catalog,p.storage_version);
 facility=coalesce(sv.facility_id,p.plant_id);
 SELECT * INTO STRICT fuel FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id,a.code)=(catalog,p.definition_id,p.input_assignment);
 WITH profiles AS MATERIALIZED(
  SELECT x.* FROM desk.facility_profiles x LEFT JOIN trading.storage_facility_versions v ON(v.catalog_id,v.id)=(x.catalog_id,x.storage_version)
  WHERE x.catalog_id=catalog AND coalesce(v.facility_id,x.plant_id)=facility AND desk.visible(catalog,x.publication_order,x.effective_at) AND desk.source_visible(catalog,x.definition_id)
 ), flows AS MATERIALIZED(
  SELECT DISTINCT ON(a.business_key) a.* FROM trading.storage_facility_activities a WHERE(a.catalog_id,a.facility_id)=(catalog,facility)
   AND security.trading_access(catalog,'ReadFacilityQuantities') AND desk.visible(catalog,a.publication_order,a.effective_at) ORDER BY a.business_key,a.revision DESC
 ), ledgers AS MATERIALIZED(
  SELECT l.*,f.business_key,scope.day FROM trading.facility_quantity_ledgers l JOIN trading.storage_facility_flow_sources f ON(f.catalog_id,f.ledger_id)=(l.catalog_id,l.id)
   JOIN flows a ON a.business_key=f.business_key AND a.source_revision_id IS NOT NULL
   JOIN trading.facility_gas_delivery_scopes scope ON(scope.catalog_id,scope.ledger_id)=(l.catalog_id,l.id)
   WHERE(f.catalog_id,f.facility_id)=(catalog,facility) AND security.trading_access(catalog,'ReadFacilityQuantities') AND desk.visible(catalog,l.publication_order,l.effective_at)
    AND scope.day>=first_day AND scope.day<end_day
 ), sources AS MATERIALIZED(
  SELECT DISTINCT ON(r.ledger_id) r.* FROM trading.facility_quantity_revisions r JOIN ledgers l ON(r.catalog_id,r.ledger_id)=(catalog,l.id)
   WHERE desk.visible(catalog,r.publication_order,r.effective_at) ORDER BY r.ledger_id,r.revision DESC
 ), allocations AS MATERIALIZED(
  SELECT DISTINCT ON(pl.ledger_id) pl.* FROM trading.facility_quantity_plans pl JOIN ledgers l ON(pl.catalog_id,pl.ledger_id)=(catalog,l.id)
   WHERE desk.visible(catalog,pl.publication_order,pl.effective_at) ORDER BY pl.ledger_id,pl.revision DESC
 ), links AS MATERIALIZED(
  SELECT ft.trade_id FROM desk.facility_trades ft JOIN profiles x ON x.id=ft.profile_id WHERE ft.catalog_id=catalog AND desk.visible(catalog,ft.publication_order,ft.effective_at)
  UNION SELECT a.trade_id FROM allocations pl JOIN trading.facility_gas_allocations a ON(a.catalog_id,a.plan_id)=(catalog,pl.id)
  UNION SELECT r.trade_id FROM trading.operating_trade_revisions r JOIN trading.power_operating_instruments i USING(catalog_id,instrument_id)
   WHERE(r.catalog_id,i.facility_id)=(catalog,p.plant_id) AND desk.visible(catalog,r.publication_order,r.effective_at)
 ),heads AS MATERIALIZED(
  SELECT DISTINCT ON(a.trade_id) a.* FROM links l JOIN desk.facility_revisions a USING(trade_id)
   WHERE a.catalog_id=catalog AND desk.visible(catalog,a.publication_order,a.effective_at)
   ORDER BY a.trade_id,a.revision DESC
 ),held AS MATERIALIZED(SELECT * FROM heads h WHERE desk.trade_source(catalog,h.definition_revision_id,h.product)), gas AS MATERIALIZED(
  SELECT h.trade_id,g.quantity,coalesce(g.direction,1) trade_direction,c.*,q.quantity_code unit
   FROM held h JOIN trading.gas_trade_revisions g ON(g.catalog_id,g.trade_id,g.revision)=(catalog,h.trade_id,h.revision)
   JOIN trading.gas_component_terms c ON(c.catalog_id,c.instrument_id,c.role)=(catalog,g.instrument_id,'physical')
   JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(catalog,c.definition_revision_id,c.assignment_code)
   JOIN market_data.curves k ON(k.catalog_id,k.revision_id,k.code)=(catalog,a.revision_id,a.curve_code)
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,k.quote_code)
   WHERE EXISTS(SELECT FROM profiles pv JOIN trading.storage_facility_ports pt ON(pt.catalog_id,pt.version_id)=(catalog,pv.storage_version)
    WHERE(pt.pipeline_code,pt.zone_code,pt.point_code,pt.product_code,pt.contract_direction)=(c.pipeline_code,c.zone_code,c.point_code,c.product_code,c.direction*coalesce(g.direction,1)))
   OR (p.plant_id IS NOT NULL AND c.assignment_code=p.input_assignment)
 ), storage AS MATERIALIZED(
  SELECT h.trade_id,s.* FROM held h JOIN trading.gas_storage_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(catalog,h.trade_id,h.revision)
   JOIN trading.gas_storage_contracts s ON(s.catalog_id,s.instrument_id)=(r.catalog_id,r.instrument_id)
 ), plans AS MATERIALIZED(
  SELECT DISTINCT ON(pl.instrument_id) pl.* FROM trading.gas_storage_plans pl JOIN storage s USING(catalog_id,instrument_id)
   WHERE desk.visible(catalog,pl.publication_order,pl.effective_at) ORDER BY pl.instrument_id,pl.revision DESC
 ), operational AS MATERIALIZED(
  SELECT DISTINCT ON(s.id,r.day) g.trade_id,g.direction*g.trade_direction direction,g.unit,s.kind,r.status,r.quantity FROM gas g JOIN trading.gas_delivery_obligations o
   ON(o.catalog_id,o.trade_id,o.slot,o.instrument_id)=(catalog,g.trade_id,g.slot,g.instrument_id) JOIN trading.gas_quantity_series s ON(s.catalog_id,s.obligation_id)=(catalog,o.id) AND s.kind IN('nominated','confirmed','scheduled')
   JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(catalog,s.id)
   WHERE r.day>=first_day AND r.day<end_day AND security.trading_access(catalog,'ReadGasOperations') AND desk.visible(catalog,r.publication_order,r.effective_at) ORDER BY s.id,r.day,r.revision DESC
 ), amounts(trade_id,stage,unit,n,d) AS(
  SELECT g.trade_id,CASE WHEN g.direction*g.trade_direction=1 THEN 'Booked gas receipts' ELSE 'Booked gas deliveries' END,g.unit::text,
   g.quantity*g.ratio_n*(least(g.delivery_to,end_day)-greatest(g.delivery_from,first_day)),g.ratio_d*(CASE WHEN g.quantity_basis='period_total_equal_days' THEN g.delivery_to-g.delivery_from ELSE 1 END)
   FROM gas g WHERE g.delivery_from<end_day AND g.delivery_to>first_day
  UNION ALL SELECT s.trade_id,'Storage capacity in force',s.quantity_code,s.maximum_inventory-s.minimum_inventory,1 FROM storage s WHERE s.term_from<=(as_at AT TIME ZONE 'UTC')::date AND s.term_to>(as_at AT TIME ZONE 'UTC')::date
  UNION ALL SELECT s.trade_id,q.label,s.quantity_code,q.amount,1 FROM storage s JOIN plans pl USING(catalog_id,instrument_id)
   JOIN trading.gas_storage_plan_days pd USING(catalog_id,instrument_id,revision) JOIN trading.gas_storage_days gd USING(catalog_id,instrument_id,ordinal)
   CROSS JOIN LATERAL(VALUES('Planned injections',pd.injection),('Planned withdrawals',pd.withdrawal))q(label,amount) WHERE gd.gas_day>=first_day AND gd.gas_day<end_day
  UNION ALL SELECT t.trade_id,CASE q.name WHEN 'energy' THEN 'Planned power output' WHEN 'consumption_fuel' THEN 'Planned consumption fuel' WHEN 'guaranteed_fuel' THEN 'Planned guaranteed fuel' ELSE 'Planned fuel input' END,q.quantity_code,q.numerator,q.denominator
   FROM held t JOIN trading.operating_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(catalog,t.trade_id,t.revision)
   JOIN trading.operating_plan_quantities q ON(q.catalog_id,q.instrument_id)=(r.catalog_id,r.instrument_id) WHERE q.name IN('energy','fuel','consumption_fuel','guaranteed_fuel') AND q.interval_from>=first_day::timestamp AT TIME ZONE 'UTC' AND q.interval_to<=end_day::timestamp AT TIME ZONE 'UTC'
  UNION ALL SELECT NULL,'Installed thermal capacity',v.power_quantity_code,(v.parameters->>'maximumOutputMw')::numeric,1
   FROM physical.power_operating_facility_versions v WHERE(v.catalog_id,v.facility_id,v.version)=(catalog,p.plant_id,p.plant_version)
    AND v.effective_from<=as_at AND as_at<v.effective_to AND security.trading_access(catalog,'ReadPhysical')
  UNION ALL SELECT h.trade_id,'Financial call capacity',i.capacity_quantity_code,r.quantity,1 FROM held h JOIN trading.heat_rate_call_trade_revisions r
   ON(r.catalog_id,r.trade_id,r.revision)=(catalog,h.trade_id,h.revision) JOIN trading.heat_rate_call_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
   WHERE i.delivery_from<end_day AND i.delivery_to>first_day
  UNION ALL SELECT h.trade_id,'Swap notional',q.quantity_code,r.quantity,1 FROM held h JOIN trading.swap_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(catalog,h.trade_id,h.revision)
   JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) JOIN trading.swap_floating_sides fs ON(fs.catalog_id,fs.id)=(catalog,i.floating_side_id)
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(fs.reference_catalog_id,fs.reference_revision_id,fs.quote_code)
   WHERE fs.delivery_from<end_day AND fs.delivery_to>first_day
  UNION ALL SELECT h.trade_id,side.label,q.quantity_code,r.quantity*sp.quantity_numerator,sp.quantity_denominator
   FROM held h JOIN trading.swap_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(catalog,h.trade_id,h.revision)
   JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
   CROSS JOIN LATERAL(VALUES(i.scheduled_first_id,'Swap first Side period notional'),(i.scheduled_second_id,'Swap second Side period notional'))side(id,label)
   JOIN trading.swap_side_periods sp ON(sp.catalog_id,sp.side_id)=(catalog,side.id)
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(sp.reference_catalog_id,sp.reference_revision_id,sp.quote_code)
   WHERE sp.delivery_from<end_day AND sp.delivery_to>first_day
  UNION ALL SELECT trade_id,initcap(kind)||CASE WHEN direction=1 THEN ' receipts' ELSE ' deliveries' END,unit,quantity,1 FROM operational WHERE status<>'withdrawn'
  UNION ALL SELECT NULL,'Measured '||f.direction,v.quantity_code,f.quantity,1 FROM flows f
   JOIN trading.storage_facility_versions v ON(v.catalog_id,v.id)=(catalog,f.version_id)
   JOIN trading.storage_facility_ports pt ON(pt.catalog_id,pt.version_id,pt.id)=(catalog,v.id,f.port_id)
   JOIN ref_data.gas_delivery_conventions cv ON(cv.catalog_id,cv.revision_id,cv.pipeline_code,cv.zone_code,cv.point_code,cv.product_code)
    =(v.reference_catalog_id,v.reference_revision_id,pt.pipeline_code,pt.zone_code,pt.point_code,pt.product_code)
   JOIN ref_data.clock_rules clk ON(clk.catalog_id,clk.revision_id,clk.code,clk.kind)=(cv.catalog_id,cv.revision_id,cv.clock_code,'fixed_offset')
   CROSS JOIN LATERAL(SELECT make_interval(mins=>cv.day_start_minute,secs=>-clk.fixed_offset_seconds) day_offset)clock
   WHERE f.quantity IS NOT NULL AND f.from_at>=(first_day::timestamp AT TIME ZONE 'UTC')+clock.day_offset
    AND f.through_at<=(end_day::timestamp AT TIME ZONE 'UTC')+clock.day_offset
  UNION ALL SELECT NULL,'Measured '||f.direction,l.quantity_code,s.quantity,1 FROM sources s JOIN ledgers l ON s.ledger_id=l.id JOIN flows f ON f.business_key=l.business_key AND f.source_revision_id IS NOT NULL
  UNION ALL SELECT a.trade_id,'Allocated '||f.direction,l.quantity_code,a.quantity,1 FROM allocations pl
   JOIN ledgers l ON l.id=pl.ledger_id JOIN flows f ON f.business_key=l.business_key JOIN trading.facility_gas_allocations a ON(a.catalog_id,a.plan_id)=(catalog,pl.id) JOIN held h ON h.trade_id=a.trade_id
 ), totals AS(SELECT stage,unit,desk.sum_quantity(n,d) amount FROM amounts GROUP BY stage,unit)
 SELECT jsonb_build_object('name',p.name,'facilityId',facility,'asOf',as_at,'from',first_day,'to',end_day,'totals',coalesce((SELECT jsonb_agg(jsonb_build_object('stage',stage,'unit',unit,'numerator',amount[1]::text,'denominator',amount[2]::text) ORDER BY stage,unit) FROM totals),'[]'),
  'meters',coalesce((SELECT jsonb_agg(jsonb_build_object('ledgerId',l.id,'day',l.day,'point',l.flow_point_code,'unit',l.quantity_code,
    'sourceRevision',s.revision,'allocationRevision',pl.revision,'measured',s.quantity::text,'allocated',al.quantity::text,
    'unallocated',(s.quantity-al.quantity)::text,'currentSource',pl.source_revision_id=s.id) ORDER BY l.day,l.id)
   FROM ledgers l JOIN sources s ON s.ledger_id=l.id LEFT JOIN allocations pl ON pl.ledger_id=l.id
   LEFT JOIN LATERAL(SELECT sum(a.quantity) quantity FROM trading.facility_gas_allocations a
    WHERE(a.catalog_id,a.plan_id)=(catalog,pl.id) HAVING bool_and(EXISTS(SELECT FROM held h WHERE h.trade_id=a.trade_id)))al ON true),'[]'),
  'trades',coalesce((SELECT jsonb_agg(jsonb_build_object('tradeId',h.trade_id,'reference',r.reference_number,'revision',h.revision,'product',h.product,'book',b.name,'executedAt',h.executed_at,
   'agreement',a.name,'confirmation',coalesce((SELECT c.status FROM desk.confirmations c WHERE(c.catalog_id,c.trade_id,c.trade_revision)=(catalog,h.trade_id,h.revision)
   AND desk.visible(catalog,c.publication_order,c.effective_at) ORDER BY c.revision DESC LIMIT 1),'awaiting')) ORDER BY r.reference_number)
   FROM held h JOIN trading.trade_references r ON(r.catalog_id,r.trade_id)=(catalog,h.trade_id) JOIN trading.books b ON(b.catalog_id,b.id)=(catalog,h.book_id)
   LEFT JOIN desk.agreements a ON(a.catalog_id,a.id)=(catalog,h.agreement_id)),'[]')) INTO result;
 RETURN result;
END$$;

CREATE FUNCTION desk.profile_setup(catalog uuid,definition uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM desk.authority(catalog,'ConfigureFacilityLimits',false);
 IF NOT desk.source_visible(catalog,definition) THEN RAISE EXCEPTION 'desk_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('storage',coalesce((SELECT jsonb_agg(jsonb_build_object('id',v.id,'facilityId',v.facility_id,'code',v.facility_code,'version',v.revision,
  'minimum',v.minimum::text,'maximum',v.maximum::text,'ports',(SELECT jsonb_agg(jsonb_build_object('id',pt.id,'direction',pt.direction,'point',pt.point_code,'pipeline',pt.pipeline_code,'zone',pt.zone_code,'product',pt.product_code) ORDER BY pt.direction,pt.point_code)
   FROM trading.storage_facility_ports pt WHERE(pt.catalog_id,pt.version_id)=(catalog,v.id))) ORDER BY v.facility_code,v.revision DESC)
   FROM trading.storage_facility_versions v JOIN market_data.revisions d ON(d.catalog_id,d.id,d.reference_catalog_id,d.reference_revision_id)=(catalog,definition,v.reference_catalog_id,v.reference_revision_id)
   WHERE v.catalog_id=catalog AND desk.visible(catalog,v.publication_order,v.effective_at)),'[]'),
  'thermal',coalesce((SELECT jsonb_agg(jsonb_build_object('id',v.facility_id,'version',v.version,'code',v.facility_code) ORDER BY v.facility_code,v.version DESC)
   FROM physical.power_operating_facility_versions v WHERE(v.catalog_id,v.definition_revision_id,v.kind)=(catalog,definition,'thermal') AND desk.visible(catalog,v.publication_order,v.effective_from)),'[]'),
  'fuel',coalesce((SELECT jsonb_agg(jsonb_build_object('point',a.point_code,'pipeline',a.pipeline_code,'zone',a.zone_code,'product',a.product_code,'curve',a.curve_code) ORDER BY a.point_code)
   FROM market_data.gas_valuation_assignments a WHERE(a.catalog_id,a.revision_id)=(catalog,definition) AND a.setup_id IS NOT DISTINCT FROM security.current_gas_setup(catalog,definition)),'[]'),
  'power',coalesce((SELECT jsonb_agg(jsonb_build_object('code',e.code,'location',e.location_code,'curve',e.code) ORDER BY e.location_code,e.code)
   FROM market_data.curves e WHERE(e.catalog_id,e.revision_id)=(catalog,definition) AND EXISTS(SELECT FROM physical.power_operating_facility_versions v JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.quantity_code)=(v.reference_catalog_id,v.reference_revision_id,v.energy_quantity_code) WHERE(v.catalog_id,v.definition_revision_id,v.kind)=(catalog,definition,'thermal') AND q.code=e.quote_code)),'[]'));
END$$;
ALTER FUNCTION desk.profile_setup(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION desk.profile_setup(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION desk.profile_setup(uuid,uuid) TO openetrm_runtime;
ALTER FUNCTION desk.add_quantity(numeric[],numeric,numeric) OWNER TO openetrm_security_owner;
ALTER AGGREGATE desk.sum_quantity(numeric,numeric) OWNER TO openetrm_security_owner;
ALTER FUNCTION desk.facility_activity(uuid,uuid,date,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION desk.add_quantity(numeric[],numeric,numeric),desk.sum_quantity(numeric,numeric),desk.facility_activity(uuid,uuid,date,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION desk.facility_activity(uuid,uuid,date,date,timestamptz) TO openetrm_runtime;
COMMIT;
