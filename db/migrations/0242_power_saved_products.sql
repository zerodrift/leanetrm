-- Reuse exact published identity-map splices as power product inputs.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION market_data.power_product_sources(catalog uuid,curve uuid,selected_version integer,output text,first_day date,last_day date)
RETURNS TABLE(source_catalog_id uuid,source_revision_id uuid,source_curve_code text,daily boolean,cutover date)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE splice market_data.custom_curve_nodes;sample market_data.custom_curve_nodes;mapping market_data.custom_curve_nodes;
 date_node text;sample_date text;selected_source_node text;leg text;begin_day date;end_day date;cursor_day date;k record;binding record;
BEGIN
 IF NOT security.custom_curve_visible(catalog,curve,selected_version) OR NOT EXISTS(
  SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state,v.engine_version)=(catalog,curve,selected_version,output,'published','custom_curve_recipe_v2'))
  OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.node_id,n.kind)=(catalog,curve,selected_version,output,'output'))
  OR(SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version))<>9
  OR(SELECT count(*) FROM market_data.custom_curve_node_sources s WHERE(s.catalog_id,s.curve_id,s.version)=(catalog,curve,selected_version))<>2
  OR(SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version)=(catalog,curve,selected_version))<>9
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version) AND n.kind IN('source','sample','output') AND n.parameters<>'{}'::jsonb)
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_calendars t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version))
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_schedules t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version))
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_node_published_sources t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version))
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version))
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_panel_weights t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version))
  OR EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version) AND n.kind NOT IN('source','weighted_contracts','dates','sample','splice','output'))
 THEN RAISE EXCEPTION 'power_interval_product';END IF;
 SELECT n.* INTO splice FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes n
  ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
  WHERE(e.catalog_id,e.curve_id,e.version,e.node_id,e.port,n.kind)=(catalog,curve,selected_version,output,'values','splice');
 IF NOT FOUND OR splice.parameters<>jsonb_build_object('implementation','whole_delivery_intervals_v1','from',first_day::text,'to',last_day::text,'cutover',splice.parameters->>'cutover')
  OR splice.parameters->>'cutover' IS NULL OR splice.parameters->>'cutover' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
 THEN RAISE EXCEPTION 'power_interval_product';END IF;
 cutover=(splice.parameters->>'cutover')::date;
 IF NOT isfinite(cutover) OR cutover<=first_day OR cutover>=last_day OR extract(day FROM cutover)<>1 OR extract(day FROM last_day)<>1
 THEN RAISE EXCEPTION 'power_interval_product';END IF;
 IF(SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,splice.node_id))<>2
 THEN RAISE EXCEPTION 'power_interval_product';END IF;
 FOREACH leg IN ARRAY ARRAY['before','after'] LOOP
  SELECT n.* INTO sample FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes n
   ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
   WHERE(e.catalog_id,e.curve_id,e.version,e.node_id,e.port,n.kind)=(catalog,curve,selected_version,splice.node_id,leg,'sample');
  IF NOT FOUND OR sample.parameters<>'{}'::jsonb OR(SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,sample.node_id))<>2
  THEN RAISE EXCEPTION 'power_interval_product';END IF;
  SELECT n.node_id INTO sample_date FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes n
   ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
   WHERE(e.catalog_id,e.curve_id,e.version,e.node_id,e.port,n.kind)=(catalog,curve,selected_version,sample.node_id,'dates','dates')
    AND n.parameters='{"mode":"context","sampling":"context_utc_time","corrections":"exact_context_frame_history_latest_visible"}'::jsonb;
  IF NOT FOUND OR date_node IS NOT NULL AND date_node<>sample_date THEN RAISE EXCEPTION 'power_interval_product';END IF;
  date_node=sample_date;
  SELECT n.* INTO mapping FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes n
   ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
   WHERE(e.catalog_id,e.curve_id,e.version,e.node_id,e.port,n.kind)=(catalog,curve,selected_version,sample.node_id,'selection','weighted_contracts');
  IF NOT FOUND OR mapping.parameters<>'{"aggregation":"signed_sum_v1"}'::jsonb OR
   (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,mapping.node_id))<>1 OR
   (SELECT count(*)>0 AND bool_and(w.weight=1) AND count(*)=count(DISTINCT w.contract_id) AND count(*)=count(DISTINCT w.output_position)
      AND min(w.output_position)=1 AND max(w.output_position)=count(*) FROM market_data.custom_curve_contract_weights w
    WHERE(w.catalog_id,w.curve_id,w.version,w.node_id)=(catalog,curve,selected_version,mapping.node_id)) IS DISTINCT FROM true
  THEN RAISE EXCEPTION 'power_interval_product';END IF;
  SELECT s.* INTO binding FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_node_sources s
   ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
   WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,mapping.node_id);
  IF NOT FOUND THEN RAISE EXCEPTION 'power_interval_product';END IF;
  selected_source_node=binding.node_id;daily=leg='before';begin_day=CASE WHEN daily THEN first_day ELSE cutover END;end_day=CASE WHEN daily THEN cutover ELSE last_day END;cursor_day=begin_day;
  FOR k IN SELECT c.* FROM market_data.custom_curve_contract_weights w JOIN market_data.curve_members m
    ON(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(binding.source_catalog_id,binding.source_revision_id,binding.source_curve_code,w.contract_id)
   JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(w.catalog_id,w.curve_id,w.version,w.node_id,w.source_node)=(catalog,curve,selected_version,mapping.node_id,selected_source_node)
    AND c.delivery_from<end_day AND begin_day<c.delivery_to ORDER BY c.delivery_from,c.delivery_to LOOP
   IF k.delivery_from<>cursor_day OR k.delivery_to>end_day OR k.kind NOT IN('future','forward') OR
      EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(k.catalog_id,k.revision_id,k.id)) OR
      (CASE WHEN daily THEN k.delivery_to<>k.delivery_from+1 ELSE extract(day FROM k.delivery_from)<>1 OR k.delivery_to<>(k.delivery_from+interval '1 month')::date END)
   THEN RAISE EXCEPTION 'power_interval_product';END IF;
   cursor_day=k.delivery_to;
  END LOOP;
  IF cursor_day<>end_day THEN RAISE EXCEPTION 'power_interval_product';END IF;
  source_catalog_id=binding.source_catalog_id;source_revision_id=binding.source_revision_id;source_curve_code=binding.source_curve_code;RETURN NEXT;
 END LOOP;
 IF(SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version) AND n.kind='source')<>2 OR
   (SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version) AND n.kind='weighted_contracts')<>2 OR
   (SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version) AND n.kind='sample')<>2 OR
   (SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version) AND n.kind='dates')<>1
 THEN RAISE EXCEPTION 'power_interval_product';END IF;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format THEN RAISE EXCEPTION 'power_interval_product';
END$$;
CREATE FUNCTION market_data.power_saved_sources(catalog uuid,curve uuid,selected_version integer)
RETURNS TABLE(source_catalog_id uuid,source_revision_id uuid,source_curve_code text,membership text,cutover date)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;first_day date;last_day date;prior_cutover date;input record;native record;
BEGIN
 SELECT n.* INTO node FROM market_data.custom_curve_versions v
  JOIN market_data.custom_curve_nodes o ON(o.catalog_id,o.curve_id,o.version,o.node_id,o.kind)=(v.catalog_id,v.curve_id,v.version,v.output_node,'output')
  JOIN market_data.custom_curve_edges e
  ON(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)=(v.catalog_id,v.curve_id,v.version,v.output_node,'values')
  JOIN market_data.custom_curve_nodes n ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
  WHERE(v.catalog_id,v.curve_id,v.version,n.kind)=(catalog,curve,selected_version,'power_intervals');
 IF NOT FOUND OR node.parameters<>jsonb_build_object('implementation','saved_product_forwards_v1','profileMapping','exact_native_grid_terms_v1','market',node.parameters->>'market','from',node.parameters->>'from','to',node.parameters->>'to')
  OR node.parameters->>'market' IS NULL OR node.parameters->>'market' NOT IN('DA','RT')
  OR node.parameters->>'from' IS NULL OR node.parameters->>'to' IS NULL OR node.parameters->>'from' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR node.parameters->>'to' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
 THEN RAISE EXCEPTION 'power_interval_policy';END IF;
 first_day=(node.parameters->>'from')::date;last_day=(node.parameters->>'to')::date;
 IF NOT isfinite(first_day) OR NOT isfinite(last_day) OR last_day-first_day NOT BETWEEN 1 AND 1464 OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_node_sources t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version)) OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_node_calendars t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version)) OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_node_schedules t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version)) OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_contract_weights t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version)) OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_price_terms t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version)) OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_panel_weights t WHERE(t.catalog_id,t.curve_id,t.version)=(catalog,curve,selected_version)) OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version) AND n.kind NOT IN('published_source','power_intervals','output')) OR
  (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version)=(catalog,curve,selected_version))<>3 OR
  EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version) AND(n.kind='output' AND n.parameters<>'{}'::jsonb OR n.kind='published_source' AND n.parameters<>'{"selection":"all_output_points_v1"}'::jsonb)) OR
  (SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.kind)=(catalog,curve,selected_version,'published_source'))<>2 OR
  (SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.kind)=(catalog,curve,selected_version,'output'))<>1 OR
  (SELECT count(*) FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version)=(catalog,curve,selected_version))<>4 OR
  (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,node.node_id))<>2 OR
  (SELECT count(*)=2 AND count(DISTINCT (s.source_curve_id,s.source_version,s.source_output_node))=2 FROM market_data.custom_curve_node_published_sources s
   JOIN market_data.custom_curve_edges e ON(e.catalog_id,e.curve_id,e.version,e.source_node)=(s.catalog_id,s.curve_id,s.version,s.node_id)
   WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,node.node_id) AND e.port IN('peak','offPeak')) IS DISTINCT FROM true
 THEN RAISE EXCEPTION 'power_interval_inputs';END IF;
 FOR input IN SELECT e.port,s.* FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_node_published_sources s
  ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
  WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,node.node_id) LOOP
  FOR native IN SELECT * FROM market_data.power_product_sources(input.source_catalog_id,input.source_curve_id,input.source_version,input.source_output_node,first_day,last_day) LOOP
   IF prior_cutover IS NOT NULL AND prior_cutover<>native.cutover THEN RAISE EXCEPTION 'power_interval_cutover';END IF;
   prior_cutover=native.cutover;source_catalog_id=native.source_catalog_id;source_revision_id=native.source_revision_id;source_curve_code=native.source_curve_code;cutover=native.cutover;
   membership=CASE WHEN native.daily THEN CASE WHEN input.port='peak' THEN 'dailyPeak' ELSE 'dailyOffPeak' END ELSE input.port END;RETURN NEXT;
  END LOOP;
 END LOOP;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow OR invalid_datetime_format THEN RAISE EXCEPTION 'power_interval_policy';
END$$;
CREATE FUNCTION market_data.validate_saved_power(catalog uuid,curve uuid,selected_version integer) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF(SELECT count(*)=4 AND count(DISTINCT s.source_curve_code)=4 AND count(DISTINCT(s.source_catalog_id,s.source_revision_id))=1 AND bool_and(s.source_catalog_id=catalog)
   AND count(DISTINCT(c.quote_code,c.location_code,c.commodity_code))=1
   AND bool_and(c.rule IN('direct','add') AND q.kind='outright' AND energy.unit_code='megawatt_hour' AND q.commodity_code=c.commodity_code AND energy.commodity_code=c.commodity_code)
   AND bool_and((c.reference_catalog_id,c.reference_revision_id)=(p.reference_catalog_id,p.reference_revision_id)
     AND(d.kind,d.clock_code,d.calendar_code,d.quantity_basis,d.interval_minutes) IS NOT DISTINCT FROM(target.kind,target.clock_code,target.calendar_code,target.quantity_basis,target.interval_minutes))
  FROM market_data.power_saved_sources(catalog,curve,selected_version) s
  JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code)
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  JOIN ref_data.quantity_specifications energy ON(energy.catalog_id,energy.revision_id,energy.code)=(q.catalog_id,q.revision_id,q.quantity_code)
  JOIN ref_data.delivery_profiles d ON(d.catalog_id,d.revision_id,d.code)=(c.reference_catalog_id,c.reference_revision_id,c.profile_code)
  JOIN market_data.custom_curve_node_power_intervals p ON(p.catalog_id,p.curve_id,p.version)=(catalog,curve,selected_version)
  JOIN ref_data.delivery_profiles target ON(target.catalog_id,target.revision_id,target.code)=(p.reference_catalog_id,p.reference_revision_id,p.profile_code)
  JOIN market_data.tou_product_versions t ON(t.catalog_id,t.product_id,t.version)=(p.product_catalog_id,p.product_id,p.product_version)
  WHERE t.state='published' AND(t.clock_catalog_id,t.clock_revision_id,t.clock_code)=(target.catalog_id,target.revision_id,target.clock_code)
   AND target.interval_minutes=60 AND security.reference_version_visible(target.catalog_id,target.revision_id)) IS DISTINCT FROM true
 THEN RAISE EXCEPTION 'power_interval_grid';END IF;
END$$;
ALTER FUNCTION market_data.power_product_sources(uuid,uuid,integer,text,date,date) OWNER TO openetrm_security_owner;
ALTER FUNCTION market_data.power_saved_sources(uuid,uuid,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION market_data.validate_saved_power(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.power_product_sources(uuid,uuid,integer,text,date,date),market_data.power_saved_sources(uuid,uuid,integer),market_data.validate_saved_power(uuid,uuid,integer) FROM PUBLIC;
DO $$DECLARE body text;needle text:='  policy=node.parameters->>''implementation'';';BEGIN
 body=pg_get_functiondef('market_data.guard_power_interval_publication()'::regprocedure);
 IF strpos(body,needle)=0 THEN RAISE EXCEPTION 'power_interval_upgrade';END IF;
 body=replace(body,needle,$patch$  IF node.parameters->>'implementation'='saved_product_forwards_v1' THEN
   PERFORM market_data.validate_saved_power(NEW.catalog_id,NEW.curve_id,NEW.version);CONTINUE;
  END IF;
$patch$||needle);EXECUTE body;
END$$;
ALTER FUNCTION market_data.power_output_matches(uuid,uuid,integer,text,uuid,text,text) RENAME TO native_power_output_matches;
CREATE FUNCTION market_data.power_output_matches(catalog uuid,curve uuid,selected_version integer,output text,definition uuid,location text,market text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT CASE WHEN EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n WHERE(n.catalog_id,n.curve_id,n.version,n.kind,n.parameters->>'implementation')=(catalog,curve,selected_version,'power_intervals','saved_product_forwards_v1')) THEN
  EXISTS(SELECT 1 FROM market_data.custom_curve_versions v JOIN market_data.custom_curve_edges e
   ON(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)=(v.catalog_id,v.curve_id,v.version,v.output_node,'values')
   JOIN market_data.custom_curve_nodes n ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
   WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state,n.parameters->>'market')=(catalog,curve,selected_version,output,'published',market))
   AND security.custom_curve_uses_revision(catalog,curve,selected_version,definition)
   AND(SELECT count(*)=4 AND bool_and((s.source_catalog_id,s.source_revision_id)=(catalog,definition) AND c.location_code=location)
    FROM market_data.power_saved_sources(catalog,curve,selected_version) s JOIN market_data.curves c
    ON(c.catalog_id,c.revision_id,c.code)=(s.source_catalog_id,s.source_revision_id,s.source_curve_code))
 ELSE market_data.native_power_output_matches(catalog,curve,selected_version,output,definition,location,market) END
$$;
ALTER FUNCTION market_data.power_output_matches(uuid,uuid,integer,text,uuid,text,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.power_output_matches(uuid,uuid,integer,text,uuid,text,text) FROM PUBLIC;
COMMIT;
