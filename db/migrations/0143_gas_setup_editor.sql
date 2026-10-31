-- Immutable gas setup versions over exact existing location and market masters.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE market_data.gas_setup_versions (
 catalog_id uuid NOT NULL,id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,base_setup_id uuid,
 actor_id uuid NOT NULL REFERENCES security.principals(id),configuration jsonb NOT NULL,
 state text NOT NULL CHECK(state IN('draft','published')),publication_order integer,publication_xid xid8,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,publication_order),UNIQUE(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,base_setup_id) REFERENCES market_data.gas_setup_versions(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 CHECK((state='draft' AND publication_order IS NULL AND publication_xid IS NULL) OR(state='published' AND publication_order>0 AND publication_xid IS NOT NULL))
);
CREATE TABLE market_data.gas_setup_receipts (
 catalog_id uuid NOT NULL,setup_id uuid NOT NULL,publication_xid xid8 NOT NULL,known_from timestamptz NOT NULL,
 PRIMARY KEY(catalog_id,setup_id),FOREIGN KEY(catalog_id,setup_id) REFERENCES market_data.gas_setup_versions(catalog_id,id)
);
CREATE TABLE market_data.gas_setup_commands (
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),setup_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,setup_id) REFERENCES market_data.gas_setup_versions(catalog_id,id)
);
CREATE TABLE market_data.gas_setup_points (
 catalog_id uuid NOT NULL,setup_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,
 clock_code ref_data.code NOT NULL,day_start_minute integer NOT NULL CHECK(day_start_minute BETWEEN 0 AND 1439),
 PRIMARY KEY(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code),UNIQUE(catalog_id,setup_id,point_code,product_code),
 FOREIGN KEY(catalog_id,setup_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.gas_setup_versions(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,zone_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,point_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,pipeline_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code)
);
CREATE TABLE market_data.gas_setup_meters (
 catalog_id uuid NOT NULL,setup_id uuid NOT NULL,code ref_data.code NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,product_code ref_data.code NOT NULL,
 identifier text NOT NULL CHECK(length(identifier) BETWEEN 1 AND 128 AND identifier=btrim(identifier)),quantity_code ref_data.code NOT NULL,
 valid_from date NOT NULL,valid_to date NOT NULL,CHECK(isfinite(valid_from) AND isfinite(valid_to) AND valid_from<valid_to),
 PRIMARY KEY(catalog_id,setup_id,code),
 FOREIGN KEY(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code) REFERENCES market_data.gas_setup_points(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code)
);
ALTER TABLE market_data.gas_valuation_groups ADD COLUMN setup_id uuid,ADD COLUMN logical_code text;
ALTER TABLE market_data.gas_valuation_assignments ADD COLUMN setup_id uuid;
DO $$DECLARE r record;BEGIN
 FOR r IN SELECT c.conname FROM pg_constraint c WHERE c.conrelid='market_data.gas_valuation_assignments'::regclass AND c.contype='u'
 AND(SELECT array_agg(a.attname::text ORDER BY k.ordinality) FROM unnest(c.conkey) WITH ORDINALITY k(n,ordinality) JOIN pg_attribute a ON(a.attrelid,a.attnum)=(c.conrelid,k.n))=ARRAY['catalog_id','revision_id','pipeline_code','zone_code','point_code','product_code'] LOOP
  EXECUTE format('ALTER TABLE market_data.gas_valuation_assignments DROP CONSTRAINT %I',r.conname);
 END LOOP;
END$$;
ALTER TABLE market_data.gas_valuation_assignments ADD UNIQUE NULLS NOT DISTINCT(catalog_id,revision_id,setup_id,pipeline_code,zone_code,point_code,product_code);
ALTER TABLE market_data.gas_usage_tariffs ADD COLUMN setup_id uuid,ADD COLUMN logical_code text;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_valuation_groups','gas_valuation_assignments','gas_usage_tariffs'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ADD FOREIGN KEY(catalog_id,setup_id,revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.gas_setup_versions(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id)',t);
 END LOOP;
END$$;
-- Legacy imported paths keep their original exact FKs; authored versions use their own typed paths.
DO $$DECLARE r record;BEGIN
 FOR r IN SELECT conname FROM pg_constraint WHERE conrelid='market_data.gas_valuation_assignments'::regclass AND confrelid IN('ref_data.pipeline_delivery_points'::regclass,'ref_data.gas_delivery_conventions'::regclass) LOOP
  EXECUTE format('ALTER TABLE market_data.gas_valuation_assignments DROP CONSTRAINT %I',r.conname);
 END LOOP;
END$$;
ALTER TABLE market_data.gas_valuation_assignments
 ADD COLUMN imported_point_code ref_data.code GENERATED ALWAYS AS(CASE WHEN setup_id IS NULL THEN point_code END) STORED,
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,zone_code,imported_point_code,product_code) REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code),
 ADD FOREIGN KEY(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code) REFERENCES market_data.gas_setup_points(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code);
ALTER TABLE trading.gas_instruments ADD COLUMN gas_setup_id uuid,
 ADD FOREIGN KEY(catalog_id,gas_setup_id) REFERENCES market_data.gas_setup_versions(catalog_id,id);
CREATE FUNCTION security.gas_setup_visible(catalog uuid,setup uuid) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT setup IS NULL OR EXISTS(SELECT 1 FROM market_data.gas_setup_versions v
 WHERE(v.catalog_id,v.id)=(catalog,setup) AND security.market_access(catalog,'ReadMarketData')
 AND security.market_fact_visible(catalog,v.definition_revision_id,NULL) AND security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id)
 AND(v.state='published' AND(NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts q JOIN market_data.temporal_contexts c ON(c.catalog_id,c.id)=(q.market_catalog_id,q.market_context_id)
 JOIN market_data.gas_setup_receipts k ON(k.catalog_id,k.setup_id,k.publication_xid)=(v.catalog_id,v.id,v.publication_xid)
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND c.catalog_id=catalog AND k.known_from<=c.known_at))
 OR v.state='draft' AND security.current_write_context() AND(security.market_access(catalog,'WriteMarketDefinitions') OR security.market_access(catalog,'SealMarketDefinitions'))))
$$;
CREATE FUNCTION security.current_gas_setup(catalog uuid,definition uuid) RETURNS uuid
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT id FROM market_data.gas_setup_versions WHERE catalog_id=catalog AND definition_revision_id=definition AND state='published' AND security.gas_setup_visible(catalog,id) ORDER BY publication_order DESC LIMIT 1
$$;
CREATE FUNCTION market_data.guard_gas_setup() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE s text;
BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Gas setup rows are immutable' USING ERRCODE='55000';END IF;
 IF NEW.setup_id IS NULL THEN
  SELECT state INTO s FROM market_data.revisions WHERE(catalog_id,id)=(NEW.catalog_id,NEW.revision_id) FOR UPDATE;
  IF s IS DISTINCT FROM 'draft' THEN RAISE EXCEPTION 'Market revision is frozen' USING ERRCODE='55000';END IF;
  UPDATE market_data.revisions SET generation=generation+1 WHERE(catalog_id,id)=(NEW.catalog_id,NEW.revision_id);
 ELSE
  SELECT state INTO s FROM market_data.gas_setup_versions WHERE(catalog_id,id)=(NEW.catalog_id,NEW.setup_id) FOR SHARE;
  IF s IS DISTINCT FROM 'draft' THEN RAISE EXCEPTION 'Gas setup is frozen' USING ERRCODE='55000';END IF;
 END IF;
 RETURN NEW;
END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_valuation_groups','gas_valuation_assignments','gas_usage_tariffs'] LOOP
  EXECUTE format('DROP TRIGGER guard_definition ON market_data.%I',t);
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_gas_setup()',t);
  EXECUTE format('CREATE POLICY setup_version ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(setup_id IS NULL OR security.gas_setup_visible(catalog_id,setup_id))',t);
  EXECUTE format('GRANT INSERT ON market_data.%I TO openetrm_security_owner',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['gas_setup_points','gas_setup_meters'] LOOP
  EXECUTE format('CREATE TRIGGER guard_definition BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_gas_setup()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['gas_setup_versions','gas_setup_commands','gas_setup_receipts','gas_setup_points','gas_setup_meters'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON market_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON market_data.%I TO openetrm_security_owner',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['gas_setup_versions','gas_setup_points','gas_setup_meters'] LOOP
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime',t);
  EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime USING(security.market_access(catalog_id,''ReadMarketData''))',t);
  EXECUTE format('CREATE POLICY setup_version ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.gas_setup_visible(catalog_id,%s))',t,CASE WHEN t='gas_setup_versions' THEN 'id' ELSE 'setup_id' END);
 END LOOP;
END$$;
GRANT UPDATE ON market_data.gas_setup_versions TO openetrm_security_owner;
CREATE FUNCTION market_data.guard_gas_setup_version() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR OLD.state='published' OR
 (NEW.catalog_id,NEW.id,NEW.definition_revision_id,NEW.reference_catalog_id,NEW.reference_revision_id,NEW.base_setup_id,NEW.actor_id,NEW.configuration)
 IS DISTINCT FROM(OLD.catalog_id,OLD.id,OLD.definition_revision_id,OLD.reference_catalog_id,OLD.reference_revision_id,OLD.base_setup_id,OLD.actor_id,OLD.configuration)
 THEN RAISE EXCEPTION 'Gas setup versions are immutable' USING ERRCODE='55000';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER immutable_version BEFORE UPDATE OR DELETE ON market_data.gas_setup_versions FOR EACH ROW EXECUTE FUNCTION market_data.guard_gas_setup_version();
CREATE FUNCTION security.gas_setup_code(setup uuid,code text) RETURNS ref_data.code LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT ('gs_'||md5(setup::text||'/'||code))::ref_data.code $$;
CREATE FUNCTION security.gas_setup_delivery(a market_data.gas_valuation_assignments) RETURNS ref_data.gas_delivery_conventions
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d ref_data.gas_delivery_conventions;
BEGIN
 IF a.setup_id IS NULL THEN
 SELECT * INTO STRICT d FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)=(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code);
 ELSE
 SELECT reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code,clock_code,day_start_minute INTO STRICT d FROM market_data.gas_setup_points
 WHERE(catalog_id,setup_id,pipeline_code,zone_code,point_code,product_code)=(a.catalog_id,a.setup_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code);
 END IF;
 RETURN d;
END$$;
-- Authorize every command at the SQL boundary; immutable requests support exact replay.
CREATE FUNCTION security.save_gas_setup(catalog uuid,definition uuid,operation uuid,base uuid,configuration jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;r market_data.revisions;prior market_data.gas_setup_versions;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'WriteMarketDefinitions') THEN RAISE EXCEPTION 'No gas setup authoring access' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO STRICT r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition) AND state='sealed';
 IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No reference access' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR jsonb_typeof(configuration)<>'object' OR octet_length(configuration::text)>262144 THEN RAISE EXCEPTION 'gas_setup_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM market_data.gas_setup_versions WHERE(catalog_id,id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>q.actor_id OR prior.configuration IS DISTINCT FROM configuration OR prior.base_setup_id IS DISTINCT FROM base OR prior.definition_revision_id<>definition THEN RAISE EXCEPTION 'gas_setup_conflict';END IF;
  RETURN jsonb_build_object('setupId',operation,'state','draft');
 END IF;
 IF base IS DISTINCT FROM security.current_gas_setup(catalog,definition) THEN RAISE EXCEPTION 'gas_setup_conflict';END IF;
 INSERT INTO market_data.gas_setup_versions VALUES(catalog,operation,definition,r.reference_catalog_id,r.reference_revision_id,base,q.actor_id,configuration,'draft',NULL,NULL);
 RETURN jsonb_build_object('setupId',operation,'state','draft');
END$$;
CREATE FUNCTION security.materialize_gas_setup(catalog uuid,setup uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE v market_data.gas_setup_versions;x jsonb;g market_data.gas_valuation_groups;n integer;
BEGIN
 SELECT * INTO STRICT v FROM market_data.gas_setup_versions WHERE(catalog_id,id)=(catalog,setup) AND state='draft' FOR UPDATE;
 IF EXISTS(SELECT 1 FROM jsonb_object_keys(v.configuration) k WHERE k NOT IN('paths','groups','meters','tariffs')) THEN RAISE EXCEPTION 'gas_setup_fields';END IF;
 FOREACH x IN ARRAY ARRAY[v.configuration->'paths',v.configuration->'groups',v.configuration->'meters',v.configuration->'tariffs'] LOOP
  IF jsonb_typeof(x) IS DISTINCT FROM 'array' OR jsonb_array_length(x)>256 THEN RAISE EXCEPTION 'gas_setup_bounds';END IF;
 END LOOP;
 IF jsonb_array_length(v.configuration->'paths')=0 THEN RAISE EXCEPTION 'gas_setup_paths';END IF;
 FOR x IN SELECT value FROM jsonb_array_elements(v.configuration->'paths') LOOP
  IF jsonb_typeof(x)<>'object' OR NOT(x ?& ARRAY['pipeline','zone','point','product','clock','dayStartMinute','groupCode','adjustment']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(x) k WHERE k NOT IN('pipeline','zone','point','product','clock','dayStartMinute','groupCode','adjustment')) THEN RAISE EXCEPTION 'gas_setup_path_fields';END IF;
  IF NOT EXISTS(SELECT 1 FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code,kind)=(v.reference_catalog_id,v.reference_revision_id,x->>'clock','fixed_offset')) THEN RAISE EXCEPTION 'gas_setup_clock';END IF;
  INSERT INTO market_data.gas_setup_points VALUES(catalog,setup,v.definition_revision_id,v.reference_catalog_id,v.reference_revision_id,x->>'pipeline',x->>'zone',x->>'point',x->>'product',x->>'clock',(x->>'dayStartMinute')::integer);
 END LOOP;
 IF EXISTS(SELECT 1 FROM market_data.gas_setup_points WHERE(catalog_id,setup_id)=(catalog,setup) GROUP BY zone_code HAVING count(DISTINCT pipeline_code)>1)
 OR EXISTS(SELECT 1 FROM market_data.gas_setup_points WHERE(catalog_id,setup_id)=(catalog,setup) GROUP BY point_code HAVING count(DISTINCT(pipeline_code,zone_code))>1) THEN RAISE EXCEPTION 'gas_setup_hierarchy';END IF;
 FOR x IN SELECT value FROM jsonb_array_elements(v.configuration->'groups') LOOP
  IF jsonb_typeof(x)<>'object' OR NOT(x ?& ARRAY['code','pipeline','product','curve','adjustment']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(x) k WHERE k NOT IN('code','pipeline','product','curve','adjustment')) OR (x->>'code')!~'^[a-z][a-z0-9_]{0,63}$' THEN RAISE EXCEPTION 'gas_setup_group_fields';END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
   JOIN ref_data.settlement_targets t ON(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
   WHERE(c.catalog_id,c.revision_id,c.code,c.commodity_code)=(catalog,v.definition_revision_id,x->>'curve',x->>'product') AND q.kind='outright' AND t.kind='index' AND t.index_b_code IS NULL) THEN RAISE EXCEPTION 'gas_setup_group_curve';END IF;
  INSERT INTO market_data.gas_valuation_groups VALUES(catalog,v.definition_revision_id,security.gas_setup_code(setup,x->>'code'),v.reference_catalog_id,v.reference_revision_id,x->>'pipeline',x->>'product',x->>'curve',(x->>'adjustment')::numeric,setup,x->>'code');
 END LOOP;
 FOR x IN SELECT value FROM jsonb_array_elements(v.configuration->'paths') LOOP
  IF x->>'groupCode' IS NOT NULL THEN
   SELECT * INTO STRICT g FROM market_data.gas_valuation_groups WHERE(catalog_id,setup_id,code)=(catalog,setup,security.gas_setup_code(setup,x->>'groupCode'));
   IF (g.pipeline_code,g.product_code) IS DISTINCT FROM(x->>'pipeline',x->>'product') THEN RAISE EXCEPTION 'gas_setup_group_path';END IF;
   INSERT INTO market_data.gas_valuation_assignments(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code,curve_code,group_code,group_adjustment,location_adjustment,setup_id)
   VALUES(catalog,v.definition_revision_id,security.gas_setup_code(setup,(x->>'point')||'/'||(x->>'product')),v.reference_catalog_id,v.reference_revision_id,x->>'pipeline',x->>'zone',x->>'point',x->>'product',g.curve_code,g.code,g.adjustment,(x->>'adjustment')::numeric,setup);
  ELSIF (x->>'adjustment')::numeric IS DISTINCT FROM 0::numeric THEN RAISE EXCEPTION 'gas_setup_unpriced_adjustment';END IF;
 END LOOP;
 FOR x IN SELECT value FROM jsonb_array_elements(v.configuration->'meters') LOOP
  IF jsonb_typeof(x)<>'object' OR NOT(x ?& ARRAY['code','pipeline','zone','point','product','identifier','quantity','validFrom','validTo']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(x) k WHERE k NOT IN('code','pipeline','zone','point','product','identifier','quantity','validFrom','validTo')) OR(x->>'code')!~'^[a-z][a-z0-9_]{0,63}$' THEN RAISE EXCEPTION 'gas_setup_meter_fields';END IF;
  IF NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code,commodity_code)=(v.reference_catalog_id,v.reference_revision_id,x->>'quantity',x->>'product')) THEN RAISE EXCEPTION 'gas_setup_meter_quantity';END IF;
  INSERT INTO market_data.gas_setup_meters VALUES(catalog,setup,x->>'code',v.reference_catalog_id,v.reference_revision_id,x->>'pipeline',x->>'zone',x->>'point',x->>'product',x->>'identifier',x->>'quantity',(x->>'validFrom')::date,(x->>'validTo')::date);
 END LOOP;
 IF EXISTS(SELECT 1 FROM market_data.gas_setup_meters a JOIN market_data.gas_setup_meters b ON(a.catalog_id,a.setup_id,a.pipeline_code)=(b.catalog_id,b.setup_id,b.pipeline_code) AND a.code<b.code
 WHERE(a.catalog_id,a.setup_id)=(catalog,setup) AND daterange(a.valid_from,a.valid_to,'[)')&&daterange(b.valid_from,b.valid_to,'[)') AND ((a.point_code,a.product_code)=(b.point_code,b.product_code) OR a.identifier=b.identifier)) THEN RAISE EXCEPTION 'gas_setup_meter_overlap';END IF;
 FOR x IN SELECT value FROM jsonb_array_elements(v.configuration->'tariffs') LOOP
  IF jsonb_typeof(x)<>'object' OR NOT(x ?& ARRAY['code','pipeline','product','quote','rate','validFrom','validTo']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(x) k WHERE k NOT IN('code','pipeline','product','quote','rate','validFrom','validTo')) OR(x->>'code')!~'^[a-z][a-z0-9_]{0,63}$' THEN RAISE EXCEPTION 'gas_setup_tariff_fields';END IF;
  IF NOT EXISTS(SELECT 1 FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code,commodity_code,kind)=(v.reference_catalog_id,v.reference_revision_id,x->>'quote',x->>'product','outright')) THEN RAISE EXCEPTION 'gas_setup_tariff_quote';END IF;
  INSERT INTO market_data.gas_usage_tariffs VALUES(catalog,v.definition_revision_id,security.gas_setup_code(setup,x->>'code'),v.reference_catalog_id,v.reference_revision_id,x->>'pipeline',x->>'product',x->>'quote',(x->>'rate')::numeric,(x->>'validFrom')::date,(x->>'validTo')::date,setup,x->>'code');
 END LOOP;
END$$;
CREATE FUNCTION security.validate_gas_setup(catalog uuid,setup uuid,first_day date,last_day date) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;v market_data.gas_setup_versions;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'ReadMarketData') OR NOT(security.market_access(catalog,'WriteMarketDefinitions') OR security.market_access(catalog,'SealMarketDefinitions')) THEN RAISE EXCEPTION 'No setup validation access' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT v FROM market_data.gas_setup_versions WHERE(catalog_id,id)=(catalog,setup);
 IF NOT security.reference_access(v.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No reference access' USING ERRCODE='42501';END IF;
 IF first_day IS NULL OR last_day IS NULL OR first_day>=last_day OR last_day-first_day>31 THEN RAISE EXCEPTION 'gas_setup_preview_period';END IF;
 BEGIN
  IF v.state='draft' THEN PERFORM security.materialize_gas_setup(catalog,setup);END IF;
  SELECT jsonb_build_object('valid',true,'points',coalesce(jsonb_agg(jsonb_build_object('pipeline',p.pipeline_code,'zone',p.zone_code,'point',p.point_code,'product',p.product_code,'curve',a.curve_code,'index',t.index_a_code,'group',g.logical_code,'adjustment',a.adjustment::text,'groupAdjustment',a.group_adjustment::text,'pointAdjustment',a.location_adjustment::text,'quote',c.quote_code,
   'missingMeterDays',(SELECT count(*) FROM generate_series(first_day::timestamp,(last_day-1)::timestamp,interval '1 day') d WHERE NOT EXISTS(SELECT 1 FROM market_data.gas_setup_meters m WHERE(m.catalog_id,m.setup_id,m.pipeline_code,m.zone_code,m.point_code,m.product_code)=(p.catalog_id,p.setup_id,p.pipeline_code,p.zone_code,p.point_code,p.product_code) AND m.valid_from<=d::date AND d::date<m.valid_to)))), '[]'::jsonb)) INTO result
  FROM market_data.gas_setup_points p LEFT JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.setup_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)=(p.catalog_id,p.setup_id,p.pipeline_code,p.zone_code,p.point_code,p.product_code)
  LEFT JOIN market_data.gas_valuation_groups g ON(g.catalog_id,g.revision_id,g.code)=(a.catalog_id,a.revision_id,a.group_code)
  LEFT JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
  LEFT JOIN ref_data.settlement_targets t ON(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
  WHERE(p.catalog_id,p.setup_id)=(catalog,setup);
  RAISE EXCEPTION 'gas_setup_validation_rollback';
 EXCEPTION WHEN raise_exception THEN
  IF SQLERRM='gas_setup_validation_rollback' THEN RETURN result;END IF;
  RETURN jsonb_build_object('valid',false,'code',SQLERRM,'message',CASE SQLERRM
   WHEN 'gas_setup_group_curve' THEN 'Choose an outright index curve for the group product.'
   WHEN 'gas_setup_group_path' THEN 'The valuation group must match its point pipeline and product.'
   WHEN 'gas_setup_hierarchy' THEN 'Assign each zone to one pipeline and each point to one zone.'
   WHEN 'gas_setup_meter_overlap' THEN 'Meter assignments overlap or reuse an active identifier on the same pipeline.'
   WHEN 'gas_setup_meter_quantity' THEN 'Choose a meter quantity specification for its product.'
   WHEN 'gas_setup_tariff_quote' THEN 'Choose an outright tariff quote for its product.'
   WHEN 'gas_setup_unpriced_adjustment' THEN 'An unpriced point must have a zero price adjustment.'
   WHEN 'gas_setup_clock' THEN 'Choose a supported fixed-offset gas-day clock.'
   ELSE 'Complete the required setup fields using valid references and explicit rates.' END);
 WHEN integrity_constraint_violation OR data_exception OR no_data_found THEN RETURN jsonb_build_object('valid',false,'message','Choose complete, unique and compatible references, exact rates and valid date ranges.');
 END;
END$$;
CREATE FUNCTION security.publish_gas_setup(catalog uuid,setup uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE v market_data.gas_setup_versions;q security.request_contexts;prior market_data.gas_setup_commands;n integer;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No gas setup publication access' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT q FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'gas_setup_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM market_data.gas_setup_commands WHERE(catalog_id,operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>q.actor_id OR prior.setup_id<>setup THEN RAISE EXCEPTION 'gas_setup_conflict';END IF;
  RETURN jsonb_build_object('setupId',setup,'state','published');
 END IF;
 SELECT * INTO STRICT v FROM market_data.gas_setup_versions WHERE(catalog_id,id)=(catalog,setup) FOR UPDATE;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.reference_access(v.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No source access' USING ERRCODE='42501';END IF;
 IF v.state='draft' THEN
  IF v.base_setup_id IS DISTINCT FROM security.current_gas_setup(catalog,v.definition_revision_id) THEN RAISE EXCEPTION 'gas_setup_conflict';END IF;
  PERFORM security.materialize_gas_setup(catalog,setup);
  SELECT coalesce(max(publication_order),0)+1 INTO n FROM market_data.gas_setup_versions WHERE catalog_id=catalog;
  UPDATE market_data.gas_setup_versions SET state='published',publication_order=n,publication_xid=pg_current_xact_id() WHERE(catalog_id,id)=(catalog,setup);
 END IF;
 INSERT INTO market_data.gas_setup_commands VALUES(catalog,operation,q.actor_id,setup);
 IF NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'Setup access expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('setupId',setup,'state','published');
END$$;
-- Committed setup availability participates in every certified market context.
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_gas_setup_context;
REVOKE ALL ON FUNCTION security.capture_pre_gas_setup_context(uuid,date,timestamptz) FROM openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record;known timestamptz;last_known timestamptz;certified timestamptz;
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RETURN security.capture_pre_gas_setup_context(catalog,effective_date,requested_at);END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No temporal access' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT max(known_from) INTO last_known FROM market_data.gas_setup_receipts WHERE catalog_id=catalog;
 FOR r IN SELECT v.* FROM market_data.gas_setup_versions v LEFT JOIN market_data.gas_setup_receipts k ON(k.catalog_id,k.setup_id)=(v.catalog_id,v.id) WHERE v.catalog_id=catalog AND v.state='published' AND k.setup_id IS NULL ORDER BY v.publication_order LOOP
  known=security.checked_commit_time(r.publication_xid);
  IF known<last_known OR known<=certified THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO market_data.gas_setup_receipts VALUES(catalog,r.id,r.publication_xid,known);last_known=known;
 END LOOP;
 RETURN security.capture_pre_gas_setup_context(catalog,effective_date,requested_at);
END$$;
-- Teach the existing final booking gate about the selected setup, without changing
-- retained-program validation or allowing any raw runtime insert.
DO $$DECLARE source text;BEGIN
 source=pg_get_functiondef('security.book_physical_gas(uuid,jsonb,timestamptz)'::regprocedure);
 source=replace(source,'''physical_gas_fixed_quantity_v3''','''physical_gas_fixed_quantity_v4''');
 source=replace(source,'''snapshotId'',''convention''','''gasSetupId'',''snapshotId'',''convention''');
 source=replace(source,'NOT(body ? ''usageFee'')','NOT(body ? ''gasSetupId'') OR NOT(body ? ''usageFee'')');
 source=replace(source,$old$AND r.state='sealed'$old$,$new$AND a.setup_id IS NOT DISTINCT FROM (body->>'gasSetupId')::uuid AND r.state='sealed'$new$);
 source=replace(source,' SELECT * INTO STRICT delivery FROM ref_data.gas_delivery_conventions WHERE(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)='||chr(10)||'  (assignment.reference_catalog_id,assignment.reference_revision_id,assignment.pipeline_code,assignment.zone_code,assignment.point_code,assignment.product_code);',
  ' SELECT * INTO STRICT delivery FROM security.gas_setup_delivery(assignment);');
 source=replace(source,'AND t.valid_from<=first_day','AND t.setup_id IS NOT DISTINCT FROM assignment.setup_id AND t.valid_from<=first_day');
 source=replace(source,' WHEN ''receive'' THEN 1 END);',' WHEN ''receive'' THEN 1 END,(body->>''gasSetupId'')::uuid);');
 IF strpos(source,'security.gas_setup_delivery(assignment)')=0 OR strpos(source,'a.setup_id IS NOT DISTINCT FROM')=0 OR strpos(source,'physical_gas_fixed_quantity_v4')=0 OR strpos(source,'AND t.setup_id IS NOT DISTINCT FROM assignment.setup_id')=0 THEN RAISE EXCEPTION 'gas_setup_booking_upgrade';END IF;
 EXECUTE source;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('gas_setup_visible','current_gas_setup','gas_setup_code','gas_setup_delivery','save_gas_setup','materialize_gas_setup','validate_gas_setup','publish_gas_setup','capture_date_context') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.gas_setup_visible(uuid,uuid),security.current_gas_setup(uuid,uuid),security.save_gas_setup(uuid,uuid,uuid,uuid,jsonb),security.validate_gas_setup(uuid,uuid,date,date),security.publish_gas_setup(uuid,uuid,uuid),security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_risk_worker;
COMMIT;
