-- Explicit saved asset lookup policies reuse the connected recipe graph.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;
ALTER TABLE market_data.custom_curve_nodes ADD CHECK(kind IN('source','published_source','select','weighted_contracts','weighted_panels','dates','sample','mean','lookup','output'));
-- Retain existing panel weight guards while admitting the new typed panel.
DO $$
DECLARE signature text;body text;updated text;
BEGIN
 FOREACH signature IN ARRAY ARRAY['market_data.guard_panel_weight()','market_data.guard_panel_publication()'] LOOP
  body:=pg_get_functiondef(signature::regprocedure);
  updated:=replace(body,'''sample'',''mean'',''published_source'',''weighted_panels''','''sample'',''mean'',''published_source'',''weighted_panels'',''lookup''');
  IF updated=body THEN RAISE EXCEPTION 'Panel guard upgrade requires its expected source-kind check';END IF;
  EXECUTE updated;
 END LOOP;
END$$;
CREATE FUNCTION market_data.guard_lookup_publication() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;first_day date;last_day date;
BEGIN
 IF NEW.state<>'published' OR OLD.state<>'draft' THEN RETURN NEW;END IF;
 FOR node IN SELECT * FROM market_data.custom_curve_nodes n
  WHERE(n.catalog_id,n.curve_id,n.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) LOOP
  IF node.kind='lookup' THEN
   IF NOT node.parameters ?& ARRAY['axis','method','maxGapDays','leftBoundary','rightBoundary','implementation','output']
    OR node.parameters-ARRAY['axis','method','maxGapDays','leftBoundary','rightBoundary','implementation','output']<>'{}'::jsonb
    OR node.parameters->>'axis' NOT IN('delivery_start','delivery_end')
    OR node.parameters->>'method' NOT IN('exact','containing_delivery','previous','next','linear_price_actual_days_v1')
    OR node.parameters->>'maxGapDays' !~ '^[0-9]{1,4}$'
    OR node.parameters->>'leftBoundary'<>'reject' OR node.parameters->>'rightBoundary'<>'reject'
    OR node.parameters->>'implementation'<>'asset_curve_lookup_v1' OR node.parameters->>'output'<>'daily_delivery_v1'
    OR EXISTS(SELECT 1 FROM jsonb_each(node.parameters) p WHERE jsonb_typeof(p.value)<>'string')
   THEN RAISE EXCEPTION 'recipe_lookup_policy' USING ERRCODE='P0001';END IF;
   IF (node.parameters->>'maxGapDays')::integer NOT BETWEEN 1 AND 3660
    OR (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id))<>2
    OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes s
      ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
     WHERE(e.catalog_id,e.curve_id,e.version,e.node_id,e.port)=(node.catalog_id,node.curve_id,node.version,node.node_id,'values')
      AND s.kind IN('sample','mean','published_source','weighted_panels','lookup'))
    OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes s
      ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
     WHERE(e.catalog_id,e.curve_id,e.version,e.node_id,e.port,s.kind)=(node.catalog_id,node.curve_id,node.version,node.node_id,'dates','dates'))
   THEN RAISE EXCEPTION 'recipe_lookup_inputs' USING ERRCODE='P0001';END IF;
  END IF;
  IF node.kind='dates' AND node.parameters->>'mode'='delivery_range' THEN
   IF NOT node.parameters ?& ARRAY['mode','from','to','sampling','corrections']
    OR node.parameters-ARRAY['mode','from','to','sampling','corrections']<>'{}'::jsonb
    OR EXISTS(SELECT 1 FROM jsonb_each(node.parameters) p WHERE jsonb_typeof(p.value)<>'string')
    OR node.parameters->>'from' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR node.parameters->>'to' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
    OR node.parameters->>'sampling'<>'context_utc_time' OR node.parameters->>'corrections'<>'exact_context_frame_history_latest_visible'
   THEN RAISE EXCEPTION 'recipe_delivery_range' USING ERRCODE='P0001';END IF;
   first_day:=(node.parameters->>'from')::date;last_day:=(node.parameters->>'to')::date;
   IF first_day<'1901-01-01' OR last_day>'2199-12-31' OR last_day-first_day NOT BETWEEN 1 AND 366
   THEN RAISE EXCEPTION 'recipe_delivery_range' USING ERRCODE='P0001';END IF;
  END IF;
 END LOOP;
 RETURN NEW;
END$$;
CREATE TRIGGER lookup_publication BEFORE UPDATE OF state ON market_data.custom_curve_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_lookup_publication();
COMMIT;
