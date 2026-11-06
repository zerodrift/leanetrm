-- Explicit source selection over whole delivery intervals. No new price facts.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
-- Pin reviewed predecessor definitions and their installation ownership. These
-- trigger helpers have no explicit ACL and use the catalog-only search path.
DO $$DECLARE helper record;BEGIN
 FOR helper IN SELECT * FROM(VALUES
  ('market_data.guard_panel_weight()','0db06fea2aa6fc2600068135e03e3d144947e5bdecb95e63017e59e5d3e31990'),
  ('market_data.guard_panel_publication()','2bd4f4fbe2dc24b10c7ecf523d5c2c7163754d951ff9f2729d253bbc7a879811'),
  ('market_data.guard_lookup_publication()','f2edabbbf3b90c17e609eb0311dc837bbc0289392b7dd6705cf892f601f7314f')
 )v(signature,hash) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
   WHERE p.oid=to_regprocedure(helper.signature) AND p.proowner=n.nspowner AND p.proacl IS NULL
    AND p.proconfig=ARRAY['search_path=pg_catalog']
    AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash)
  THEN RAISE EXCEPTION 'curve_splice_upgrade: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='market_data.custom_curve_nodes'::regclass
  AND conname='custom_curve_nodes_kind_check' AND convalidated
  AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='2b11e8ac45ded16f46968e1c108d0ddb8f8e23056785e5172b7e48f246efe567')
 THEN RAISE EXCEPTION 'curve_splice_upgrade: unexpected node kinds' USING ERRCODE='55000';END IF;
END$$;
ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;
ALTER TABLE market_data.custom_curve_nodes ADD CHECK(kind IN('source','published_source','select','weighted_contracts','weighted_panels','dates','sample','mean','lookup','splice','output'));

-- Admit the new panel in the existing typed consumers, retaining their other
-- checks, permissions and trigger contracts. Refuse an unexpected predecessor.
DO $$
DECLARE signature text;body text;updated text;
 old_types constant text:='''sample'',''mean'',''published_source'',''weighted_panels'',''lookup''';
 new_types constant text:='''sample'',''mean'',''published_source'',''weighted_panels'',''lookup'',''splice''';
BEGIN
 FOREACH signature IN ARRAY ARRAY['market_data.guard_panel_weight()','market_data.guard_panel_publication()','market_data.guard_lookup_publication()'] LOOP
  body:=pg_get_functiondef(signature::regprocedure);
  IF (length(body)-length(replace(body,old_types,'')))/length(old_types)<>1
  THEN RAISE EXCEPTION 'Splice upgrade requires exactly one expected panel type check in %',signature;END IF;
  updated:=replace(body,old_types,new_types);EXECUTE updated;
 END LOOP;
END$$;

CREATE FUNCTION market_data.guard_splice_publication() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;first_day date;switch_day date;last_day date;
BEGIN
 IF NEW.state<>'published' OR OLD.state<>'draft' THEN RETURN NEW;END IF;
 FOR node IN SELECT * FROM market_data.custom_curve_nodes n
  WHERE(n.catalog_id,n.curve_id,n.version,n.kind)=(NEW.catalog_id,NEW.curve_id,NEW.version,'splice') LOOP
  IF NOT node.parameters ?& ARRAY['from','cutover','to','implementation']
   OR node.parameters-ARRAY['from','cutover','to','implementation']<>'{}'::jsonb
   OR EXISTS(SELECT 1 FROM jsonb_each(node.parameters) p WHERE jsonb_typeof(p.value)<>'string')
   OR node.parameters->>'implementation'<>'whole_delivery_intervals_v1'
   OR node.parameters->>'from' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
   OR node.parameters->>'cutover' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
   OR node.parameters->>'to' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
  THEN RAISE EXCEPTION 'recipe_splice_policy' USING ERRCODE='P0001';END IF;
  BEGIN
   first_day:=(node.parameters->>'from')::date;switch_day:=(node.parameters->>'cutover')::date;last_day:=(node.parameters->>'to')::date;
  EXCEPTION WHEN datetime_field_overflow OR invalid_datetime_format THEN
   RAISE EXCEPTION 'recipe_splice_policy' USING ERRCODE='P0001';
  END;
  IF first_day<'1901-01-01' OR last_day>'2199-12-31' OR first_day>=switch_day OR switch_day>=last_day
  THEN RAISE EXCEPTION 'recipe_splice_policy' USING ERRCODE='P0001';END IF;
  IF (SELECT count(*) FROM market_data.custom_curve_edges e WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id))<>2
   OR (SELECT count(*) FROM market_data.custom_curve_edges e JOIN market_data.custom_curve_nodes s
     ON(s.catalog_id,s.curve_id,s.version,s.node_id)=(e.catalog_id,e.curve_id,e.version,e.source_node)
    WHERE(e.catalog_id,e.curve_id,e.version,e.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id)
     AND e.port IN('before','after') AND s.kind IN('sample','mean','published_source','weighted_panels','lookup','splice'))<>2
  THEN RAISE EXCEPTION 'recipe_splice_inputs' USING ERRCODE='P0001';END IF;
 END LOOP;
 RETURN NEW;
END$$;
CREATE TRIGGER splice_publication BEFORE UPDATE OF state ON market_data.custom_curve_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_splice_publication();
COMMIT;
