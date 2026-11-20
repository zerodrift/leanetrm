-- Derived graphs inherit exact quote units; publication never creates an FX conversion.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION market_data.recipe_price_units(catalog uuid,curve uuid,selected_version integer,selected_node text,
 units jsonb DEFAULT '{}'::jsonb,active text[] DEFAULT '{}'::text[]) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;source market_data.custom_curve_node_sources;
 imported market_data.custom_curve_node_published_sources;edge record;unit jsonb;child jsonb;key text;
 inputs jsonb='{}'::jsonb;stack text[];address text[];ready boolean;cells jsonb;entry record;part jsonb;first_unit jsonb;selected_unit jsonb;position integer=0;
BEGIN
 stack=ARRAY[catalog::text||':'||curve::text||':'||selected_version::text||':'||selected_node];
 -- Explicit DFS stack: valid nested graphs never consume the PostgreSQL call stack.
 WHILE cardinality(stack)>0 LOOP
 key=stack[cardinality(stack)];stack=stack[1:cardinality(stack)-1];
 ready=left(key,1)='~';IF ready THEN key=substring(key FROM 2);END IF;
 IF units ? key THEN CONTINUE;END IF;
 address=string_to_array(key,':');catalog=address[1]::uuid;curve=address[2]::uuid;selected_version=address[3]::integer;selected_node=address[4];
 SELECT * INTO STRICT node FROM market_data.custom_curve_nodes n
  WHERE(n.catalog_id,n.curve_id,n.version,n.node_id)=(catalog,curve,selected_version,selected_node);
 IF NOT ready THEN
  IF key=ANY(active) OR cardinality(active)>=2048 OR (SELECT count(*) FROM jsonb_object_keys(units))>=16384 THEN RAISE EXCEPTION 'recipe_units';END IF;
  active=array_append(active,key);stack=array_append(stack,'~'||key);
  IF node.kind='published_source' THEN
   SELECT * INTO STRICT imported FROM market_data.custom_curve_node_published_sources s
    WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(catalog,curve,selected_version,selected_node);
   stack=array_append(stack,imported.source_catalog_id::text||':'||imported.source_curve_id::text||':'||imported.source_version::text||':'||imported.source_output_node);
  ELSE
   FOR edge IN SELECT source_node FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,selected_node) ORDER BY port LOOP
    stack=array_append(stack,catalog::text||':'||curve::text||':'||selected_version::text||':'||edge.source_node);
   END LOOP;
  END IF;
  CONTINUE;
 END IF;
 IF active[cardinality(active)] IS DISTINCT FROM key THEN RAISE EXCEPTION 'recipe_units';END IF;
 active=active[1:cardinality(active)-1];unit=NULL;inputs='{}'::jsonb;position=0;
 IF node.kind='source' THEN
  SELECT * INTO STRICT source FROM market_data.custom_curve_node_sources s
   WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(catalog,curve,selected_version,selected_node);
  IF NOT security.custom_curve_source_visible(source.source_catalog_id,source.source_revision_id) THEN RAISE EXCEPTION 'No current exact source access' USING ERRCODE='42501';END IF;
  SELECT jsonb_build_array(q.catalog_id,q.revision_id,q.currency_code,q.quantity_code,q.multiplier) INTO STRICT unit
   FROM market_data.curves c JOIN ref_data.quote_specifications q
    ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
   WHERE(c.catalog_id,c.revision_id,c.code)=(source.source_catalog_id,source.source_revision_id,source.source_curve_code);
  unit=jsonb_build_object('uniform',unit);
 ELSIF node.kind='published_source' THEN
  SELECT * INTO STRICT imported FROM market_data.custom_curve_node_published_sources s
   WHERE(s.catalog_id,s.curve_id,s.version,s.node_id)=(catalog,curve,selected_version,selected_node);
  IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state)=
   (imported.source_catalog_id,imported.source_curve_id,imported.source_version,imported.source_output_node,'published')) THEN RAISE EXCEPTION 'recipe_units';END IF;
  unit=units->(imported.source_catalog_id::text||':'||imported.source_curve_id::text||':'||imported.source_version::text||':'||imported.source_output_node);
 ELSIF node.kind='dates' THEN unit='null'::jsonb;
 ELSIF node.kind IN('select','weighted_contracts','sample','mean','lookup','splice','weighted_panels','power_intervals','contract_observations','output') THEN
  FOR edge IN SELECT * FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(catalog,curve,selected_version,selected_node) ORDER BY port LOOP
   child=units->(catalog::text||':'||curve::text||':'||selected_version::text||':'||edge.source_node);
   IF edge.port='dates' AND node.kind IN('sample','lookup') THEN
    IF child IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'recipe_units';END IF;
    CONTINUE;
   END IF;
   IF child IS NULL OR child='null'::jsonb THEN RAISE EXCEPTION 'recipe_units';END IF;
   inputs=inputs||jsonb_build_object(edge.port,child);
  END LOOP;
  IF inputs='{}'::jsonb THEN RAISE EXCEPTION 'recipe_units';END IF;
  IF node.kind='weighted_contracts' THEN
   cells='[]'::jsonb;
   FOR entry IN SELECT w.output_position,min(k.delivery_from) first_day,max(k.delivery_to) last_day,
     jsonb_agg(DISTINCT (units->(catalog::text||':'||curve::text||':'||selected_version::text||':'||w.source_node))->'uniform') kinds
    FROM market_data.custom_curve_contract_weights w JOIN market_data.contracts k
     ON(k.catalog_id,k.revision_id,k.id)=(w.source_catalog_id,w.source_revision_id,w.contract_id)
    WHERE(w.catalog_id,w.curve_id,w.version,w.node_id)=(catalog,curve,selected_version,selected_node)
    GROUP BY w.output_position ORDER BY w.output_position LOOP
    IF jsonb_array_length(entry.kinds)<>1 OR entry.kinds->0='null'::jsonb THEN RAISE EXCEPTION 'recipe_units';END IF;
    cells=cells||jsonb_build_array(jsonb_build_object('position',entry.output_position,'from',entry.first_day,'to',entry.last_day,'unit',entry.kinds->0));
   END LOOP;
   IF cells='[]'::jsonb THEN RAISE EXCEPTION 'recipe_units';END IF;
   unit=jsonb_build_object('cells',cells);
  ELSIF node.kind='weighted_panels' THEN
   -- Uniform sources need no date evaluation to prove type compatibility.
   -- Nonuniform panels retain exact declared delivery cells; never merge their currencies.
   cells='[]'::jsonb;
   FOR entry IN SELECT DISTINCT c->>'from' first_day,c->>'to' last_day
    FROM jsonb_each(inputs) i CROSS JOIN LATERAL jsonb_array_elements(coalesce(i.value->'cells','[]'::jsonb)) c ORDER BY 1,2 LOOP
    first_unit=NULL;
    FOR part IN SELECT value FROM jsonb_each(inputs) LOOP
     IF part ? 'uniform' THEN selected_unit=part->'uniform';
     ELSE
      SELECT jsonb_agg(DISTINCT c->'unit') INTO child FROM jsonb_array_elements(part->'cells') c WHERE(c->>'from',c->>'to')=(entry.first_day,entry.last_day);
      IF child IS NULL OR jsonb_array_length(child)<>1 THEN RAISE EXCEPTION 'recipe_units';END IF;
      selected_unit=child->0;
     END IF;
     IF first_unit IS NOT NULL AND first_unit<>selected_unit THEN RAISE EXCEPTION 'recipe_units';END IF;
     first_unit=selected_unit;
    END LOOP;
    position=position+1;cells=cells||jsonb_build_array(jsonb_build_object('position',position,'from',entry.first_day,'to',entry.last_day,'unit',first_unit));
   END LOOP;
   IF cells<>'[]'::jsonb THEN unit=jsonb_build_object('cells',cells);
   ELSE
    SELECT jsonb_agg(DISTINCT value->'uniform') INTO child FROM jsonb_each(inputs);
    IF jsonb_array_length(child)<>1 THEN RAISE EXCEPTION 'recipe_units';END IF;
    unit=jsonb_build_object('uniform',child->0);
   END IF;
  ELSIF node.kind IN('lookup','splice','power_intervals') OR node.kind='mean' AND node.parameters->>'axis'='contracts' THEN
   SELECT jsonb_agg(DISTINCT u) INTO child FROM jsonb_each(inputs) i
    CROSS JOIN LATERAL (
     SELECT i.value->'uniform' u WHERE i.value ? 'uniform'
     UNION ALL SELECT c->'unit' FROM jsonb_array_elements(coalesce(i.value->'cells','[]'::jsonb)) c
      WHERE node.kind<>'splice' OR (c->>'from')::date<(CASE i.key WHEN 'before' THEN node.parameters->>'cutover' ELSE node.parameters->>'to' END)::date
       AND (c->>'to')::date>(CASE i.key WHEN 'before' THEN node.parameters->>'from' ELSE node.parameters->>'cutover' END)::date
    ) kinds;
   IF child IS NULL OR jsonb_array_length(child)<>1 THEN RAISE EXCEPTION 'recipe_units';END IF;
   unit=jsonb_build_object('uniform',child->0);
  ELSE
   IF(SELECT count(*) FROM jsonb_each(inputs))<>1 THEN RAISE EXCEPTION 'recipe_units';END IF;
   SELECT value INTO unit FROM jsonb_each(inputs);
  END IF;
 ELSE RAISE EXCEPTION 'recipe_units';END IF;
 units=units||jsonb_build_object(key,unit);
 END LOOP;
 RETURN units;
EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'recipe_units';
END$$;
ALTER FUNCTION market_data.recipe_price_units(uuid,uuid,integer,text,jsonb,text[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.recipe_price_units(uuid,uuid,integer,text,jsonb,text[]) FROM PUBLIC;
CREATE FUNCTION market_data.guard_recipe_price_units() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.state='published' AND OLD.state='draft' THEN
  PERFORM market_data.recipe_price_units(NEW.catalog_id,NEW.curve_id,NEW.version,NEW.output_node);
 END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION market_data.guard_recipe_price_units() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.guard_recipe_price_units() FROM PUBLIC;
CREATE TRIGGER recipe_price_units BEFORE UPDATE OF state ON market_data.custom_curve_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_recipe_price_units();
COMMIT;
