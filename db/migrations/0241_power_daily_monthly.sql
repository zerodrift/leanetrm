-- Explicit daily16/8 front section and whole-month peak/off-peak tail.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
CREATE OR REPLACE FUNCTION market_data.guard_power_interval_publication() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;first_day date;last_day date;cutover date;source_count integer;policy text;expected jsonb;
BEGIN
 IF NEW.state<>'published' OR OLD.state<>'draft' THEN RETURN NEW;END IF;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(NEW.catalog_id,NEW.curve_id,NEW.version)
  AND(n.kind='power_intervals') IS DISTINCT FROM EXISTS(SELECT 1 FROM market_data.custom_curve_node_power_intervals p WHERE(p.catalog_id,p.curve_id,p.version,p.node_id)=(n.catalog_id,n.curve_id,n.version,n.node_id)))
 THEN RAISE EXCEPTION 'power_interval_binding';END IF;
 FOR node IN SELECT * FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.kind)=(NEW.catalog_id,NEW.curve_id,NEW.version,'power_intervals') LOOP
  policy=node.parameters->>'implementation';
  source_count=CASE policy WHEN 'flat_forward_only_v1' THEN 2 WHEN 'daily_then_monthly_forward_v1' THEN 4 ELSE 0 END;
  expected=jsonb_build_object('implementation',policy,'profileMapping','exact_native_grid_terms_v1','market',node.parameters->>'market','from',node.parameters->>'from','to',node.parameters->>'to');
  IF source_count=4 THEN expected=expected||jsonb_build_object('cutover',node.parameters->>'cutover');END IF;
  IF source_count=0 OR node.parameters<>expected
   OR EXISTS(SELECT 1 FROM jsonb_each(node.parameters) p WHERE jsonb_typeof(p.value)<>'string')
   OR node.parameters->>'market' IS NULL OR node.parameters->>'market' NOT IN('DA','RT') OR node.parameters->>'from' IS NULL OR node.parameters->>'to' IS NULL
   OR node.parameters->>'from' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR node.parameters->>'to' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
  THEN RAISE EXCEPTION 'power_interval_policy';END IF;
  first_day=(node.parameters->>'from')::date;last_day=(node.parameters->>'to')::date;
  IF NOT isfinite(first_day) OR NOT isfinite(last_day) OR last_day-first_day NOT BETWEEN 1 AND 1464 THEN RAISE EXCEPTION 'power_interval_policy';END IF;
  IF source_count=4 THEN
   IF node.parameters->>'cutover' IS NULL OR node.parameters->>'cutover' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' THEN RAISE EXCEPTION 'power_interval_policy';END IF;
   cutover=(node.parameters->>'cutover')::date;
   IF NOT isfinite(cutover) OR cutover<=first_day OR cutover>=last_day OR extract(day FROM cutover)<>1 OR extract(day FROM last_day)<>1
   THEN RAISE EXCEPTION 'power_interval_policy';END IF;
  END IF;
  IF (SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(NEW.catalog_id,NEW.curve_id,NEW.version))<>source_count+2
   OR (SELECT count(*) FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes s ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
    WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id) AND (e.port IN('peak','offPeak') OR source_count=4 AND e.port IN('dailyPeak','dailyOffPeak')) AND s.kind='source')<>source_count
   OR (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id))<>source_count
   OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes o ON(o.catalog_id,o.curve_id,o.version,o.node_id)=(e.catalog_id,e.curve_id,e.version,e.node_id)
    WHERE(e.catalog_id,e.curve_id,e.version,e.source_node,e.port,o.node_id,o.kind)=(node.catalog_id,node.curve_id,node.version,node.node_id,'values',NEW.output_node,'output'))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.source_node)=(node.catalog_id,node.curve_id,node.version,node.node_id) AND e.node_id<>NEW.output_node)
  THEN RAISE EXCEPTION 'power_interval_inputs';END IF;
  IF (SELECT count(*)=source_count AND count(DISTINCT s.source_curve_code)=source_count AND count(DISTINCT e.source_node)=source_count
    AND count(DISTINCT (s.source_catalog_id,s.source_revision_id))=1 AND bool_and(s.source_catalog_id=NEW.catalog_id)
    AND count(DISTINCT (c.quote_code,c.location_code,c.commodity_code))=1
    AND bool_and(q.kind='outright' AND energy.unit_code='megawatt_hour' AND q.commodity_code=c.commodity_code AND energy.commodity_code=c.commodity_code)
    FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_node_sources s
     ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
    JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
    JOIN ref_data.quantity_specifications energy ON(energy.catalog_id,energy.revision_id,energy.code)=(q.catalog_id,q.revision_id,q.quantity_code)
    WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id)) IS DISTINCT FROM true
   OR (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,NEW.output_node))<>1
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_sources s JOIN market_data.curve_members m
    ON(m.catalog_id,m.revision_id,m.curve_code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
    JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(s.catalog_id,s.curve_id,s.version)=(node.catalog_id,node.curve_id,node.version) AND(k.kind NOT IN('future','forward') OR
     EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(k.catalog_id,k.revision_id,k.id))))
  THEN RAISE EXCEPTION 'power_interval_units';END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_node_power_intervals p JOIN market_data.tou_product_versions t
    ON(t.catalog_id,t.product_id,t.version)=(p.product_catalog_id,p.product_id,p.product_version)
   JOIN ref_data.delivery_profiles d ON(d.catalog_id,d.revision_id,d.code)=(p.reference_catalog_id,p.reference_revision_id,p.profile_code)
   WHERE(p.catalog_id,p.curve_id,p.version,p.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id) AND t.state='published'
    AND(t.clock_catalog_id,t.clock_revision_id,t.clock_code)=(d.catalog_id,d.revision_id,d.clock_code) AND d.interval_minutes=60
    AND security.reference_version_visible(d.catalog_id,d.revision_id))
   OR EXISTS(SELECT 1 FROM market_data.custom_curve_edges e
    JOIN market_data.custom_curve_node_sources s ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
    JOIN ref_data.delivery_profiles d ON(d.catalog_id,d.revision_id,d.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
    JOIN market_data.custom_curve_node_power_intervals p ON(p.catalog_id,p.curve_id,p.version,p.node_id)=(e.catalog_id,e.curve_id,e.version,e.node_id)
    JOIN ref_data.delivery_profiles target ON(target.catalog_id,target.revision_id,target.code)=(p.reference_catalog_id,p.reference_revision_id,p.profile_code)
    WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id)
     AND(c.rule NOT IN('direct','add') OR(c.reference_catalog_id,c.reference_revision_id)<>(p.reference_catalog_id,p.reference_revision_id)
      OR(d.kind,d.clock_code,d.calendar_code,d.quantity_basis,d.interval_minutes) IS DISTINCT FROM(target.kind,target.clock_code,target.calendar_code,target.quantity_basis,target.interval_minutes)))
  THEN RAISE EXCEPTION 'power_interval_grid';END IF;
  IF source_count=4 AND EXISTS(
   SELECT 1 FROM market_data.custom_curve_edges e
   JOIN market_data.custom_curve_node_sources s ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
   JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
   JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id) AND
    CASE WHEN e.port IN('dailyPeak','dailyOffPeak') THEN
      k.delivery_from<cutover AND first_day<k.delivery_to AND(k.delivery_from<first_day OR k.delivery_to>cutover OR k.delivery_to<>k.delivery_from+1)
     ELSE k.delivery_from<last_day AND cutover<k.delivery_to AND(k.delivery_from<cutover OR k.delivery_to>last_day OR extract(day FROM k.delivery_from)<>1 OR k.delivery_to<>(k.delivery_from+interval '1 month')::date) END)
  THEN RAISE EXCEPTION 'power_interval_period';END IF;
 END LOOP;
 -- Interval outputs cannot be consumed as date panels, including imported ones.
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_node_published_sources s
  JOIN market_data.custom_curve_edges e ON(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)=(s.source_catalog_id,s.source_curve_id,s.source_version,s.source_output_node,'values')
  JOIN market_data.custom_curve_nodes n ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
  WHERE(s.catalog_id,s.curve_id,s.version,n.kind)=(NEW.catalog_id,NEW.curve_id,NEW.version,'power_intervals'))
 THEN RAISE EXCEPTION 'power_interval_panel';END IF;
 RETURN NEW;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format THEN RAISE EXCEPTION 'power_interval_policy';
END$$;
-- Assignment admission follows the same explicit two/four-source contract.
CREATE OR REPLACE FUNCTION market_data.power_output_matches(catalog uuid,curve uuid,selected_version integer,output text,definition uuid,location text,market text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v JOIN market_data.custom_curve_edges e
  ON(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)=(v.catalog_id,v.curve_id,v.version,v.output_node,'values')
  JOIN market_data.custom_curve_nodes n ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
  WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state,n.kind,n.parameters->>'market')=(catalog,curve,selected_version,output,'published','power_intervals',market)
  AND security.custom_curve_uses_revision(catalog,curve,selected_version,definition)
  AND(SELECT count(*)=CASE n.parameters->>'implementation' WHEN 'flat_forward_only_v1' THEN 2 WHEN 'daily_then_monthly_forward_v1' THEN 4 ELSE 0 END
    AND bool_and(c.location_code=location) FROM market_data.custom_curve_node_sources s
   JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
   WHERE(s.catalog_id,s.curve_id,s.version)=(catalog,curve,selected_version)))
$$;
COMMIT;
