-- Exact delivery masters bind to an existing pricing source without copying markets.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE market_data.crude_setup_versions (
 catalog_id uuid NOT NULL,id uuid NOT NULL,code ref_data.code NOT NULL,base_setup_id uuid,
 actor_id uuid NOT NULL REFERENCES security.principals(id),configuration jsonb NOT NULL,
 definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,
 delivery_reference_catalog_id uuid NOT NULL,delivery_reference_revision_id uuid NOT NULL,
 commodity_code ref_data.code NOT NULL,grade_code ref_data.code NOT NULL,facility_code ref_data.code NOT NULL,custody_point_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,
 valid_from date NOT NULL,valid_to date NOT NULL,quantity_policy text NOT NULL CHECK(quantity_policy='same_standard_volume_at_60f_v1'),
 state text NOT NULL CHECK(state IN('draft','published')),publication_order integer,publication_xid xid8,
 PRIMARY KEY(catalog_id,id),UNIQUE NULLS NOT DISTINCT(catalog_id,configuration,base_setup_id),UNIQUE(catalog_id,publication_order),UNIQUE(catalog_id,id,publication_xid),
 FOREIGN KEY(catalog_id,base_setup_id) REFERENCES market_data.crude_setup_versions(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(delivery_reference_catalog_id,delivery_reference_revision_id,commodity_code,grade_code,facility_code,custody_point_code)
  REFERENCES ref_data.crude_delivery_points(catalog_id,revision_id,commodity_code,grade_code,facility_code,custody_point_code),
 FOREIGN KEY(delivery_reference_catalog_id,delivery_reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 CHECK(isfinite(valid_from) AND isfinite(valid_to) AND valid_from<valid_to AND valid_from>=date '2000-01-01' AND valid_to<=date '2100-01-01'),
 CHECK((state='draft' AND publication_order IS NULL AND publication_xid IS NULL) OR(state='published' AND publication_order>0 AND publication_xid IS NOT NULL))
);
CREATE TABLE market_data.crude_setup_commands (
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),operation_id uuid NOT NULL,command text NOT NULL,
 payload jsonb NOT NULL,result jsonb NOT NULL,setup_id uuid NOT NULL,PRIMARY KEY(catalog_id,actor_id,operation_id),
 FOREIGN KEY(catalog_id,setup_id) REFERENCES market_data.crude_setup_versions(catalog_id,id)
);
CREATE TABLE market_data.crude_setup_receipts (
 catalog_id uuid NOT NULL,setup_id uuid NOT NULL,publication_xid xid8 NOT NULL,known_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,setup_id),FOREIGN KEY(catalog_id,setup_id,publication_xid) REFERENCES market_data.crude_setup_versions(catalog_id,id,publication_xid)
);
CREATE FUNCTION security.crude_setup_visible(catalog uuid,setup uuid) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM market_data.crude_setup_versions v JOIN market_data.revisions m ON(m.catalog_id,m.id)=(v.catalog_id,v.definition_revision_id)
 WHERE(v.catalog_id,v.id)=(catalog,setup) AND security.market_access(catalog,'ReadMarketData')
 AND(NOT pg_has_role(session_user,'openetrm_risk_worker','MEMBER') OR risk.worker_read())
 AND security.market_fact_visible(catalog,v.definition_revision_id,NULL)
 AND security.reference_access(m.reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(m.reference_catalog_id,m.reference_revision_id)
 AND security.reference_access(v.delivery_reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(v.delivery_reference_catalog_id,v.delivery_reference_revision_id)
 AND(v.state='draft' AND security.current_write_context() AND(security.market_access(catalog,'WriteMarketDefinitions') OR security.market_access(catalog,'SealMarketDefinitions'))
 OR v.state='published' AND(NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts q JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id)
 JOIN market_data.crude_setup_receipts k ON(k.catalog_id,k.setup_id)=(v.catalog_id,v.id)
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND t.catalog_id=catalog AND k.known_from<=t.known_at))))
$$;
-- Resolve the latest known publication before checking its current authority.
-- Filtering inaccessible rows first would turn a revoked head into an older one.
CREATE FUNCTION security.current_crude_setups(catalog uuid) RETURNS uuid[]
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context uuid;known timestamptz;heads uuid[];head uuid;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read())
 THEN RAISE EXCEPTION 'No crude setup read access' USING ERRCODE='42501';END IF;
 SELECT market_context_id INTO context FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
  AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF context IS NOT NULL THEN
  SELECT known_at INTO STRICT known FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context);
 END IF;
 SELECT coalesce(array_agg(v.id ORDER BY v.code),ARRAY[]::uuid[]) INTO heads FROM(
  SELECT DISTINCT ON(v.code) v.code,v.id FROM market_data.crude_setup_versions v
  LEFT JOIN market_data.crude_setup_receipts k ON(k.catalog_id,k.setup_id)=(v.catalog_id,v.id)
  WHERE v.catalog_id=catalog AND v.state='published' AND(known IS NULL OR k.known_from<=known)
  ORDER BY v.code,v.publication_order DESC)v;
 FOREACH head IN ARRAY heads LOOP
  IF NOT security.crude_setup_visible(catalog,head) THEN RAISE EXCEPTION 'Current crude setup is not authorized' USING ERRCODE='42501';END IF;
 END LOOP;
 RETURN heads;
END$$;
CREATE FUNCTION market_data.guard_crude_setup() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR OLD.state='published' OR (to_jsonb(NEW)-ARRAY['state','publication_order','publication_xid']) IS DISTINCT FROM(to_jsonb(OLD)-ARRAY['state','publication_order','publication_xid'])
 THEN RAISE EXCEPTION 'Immutable crude setup' USING ERRCODE='55000';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER immutable_version BEFORE UPDATE OR DELETE ON market_data.crude_setup_versions FOR EACH ROW EXECUTE FUNCTION market_data.guard_crude_setup();
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['crude_setup_versions','crude_setup_commands','crude_setup_receipts'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON market_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON market_data.%I TO openetrm_security_owner',t);
  IF t<>'crude_setup_versions' THEN EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);END IF;
 END LOOP;
END$$;
GRANT UPDATE ON market_data.crude_setup_versions TO openetrm_security_owner;
GRANT SELECT ON market_data.crude_setup_versions TO openetrm_runtime;
CREATE POLICY visible ON market_data.crude_setup_versions FOR SELECT TO openetrm_runtime USING(security.crude_setup_visible(catalog_id,id));
CREATE FUNCTION security.require_crude_setup(catalog uuid,setup uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE v market_data.crude_setup_versions;m market_data.revisions;c market_data.curves;g ref_data.crude_grades;d ref_data.quantity_specifications;p ref_data.quantity_specifications;q ref_data.quote_specifications;
BEGIN
 IF NOT security.crude_setup_visible(catalog,setup) THEN RAISE EXCEPTION 'No crude setup access' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT v FROM market_data.crude_setup_versions WHERE(catalog_id,id)=(catalog,setup);
 SELECT * INTO STRICT m FROM market_data.revisions WHERE(catalog_id,id)=(catalog,v.definition_revision_id);
 SELECT * INTO STRICT c FROM market_data.curves WHERE(catalog_id,revision_id,code)=(catalog,v.definition_revision_id,v.curve_code);
 SELECT * INTO STRICT g FROM ref_data.crude_grades WHERE(catalog_id,revision_id,code)=(v.delivery_reference_catalog_id,v.delivery_reference_revision_id,v.grade_code);
 SELECT * INTO STRICT d FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(g.catalog_id,g.revision_id,g.quantity_code);
 SELECT * INTO STRICT q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(m.reference_catalog_id,m.reference_revision_id,c.quote_code);
 SELECT * INTO STRICT p FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(q.catalog_id,q.revision_id,q.quantity_code);
 IF m.state<>'sealed' OR q.kind<>'outright' OR c.commodity_code<>p.commodity_code OR
 (d.commodity_code,d.basis_code,d.unit_code,d.dimension_code) IS DISTINCT FROM(p.commodity_code,p.basis_code,p.unit_code,p.dimension_code)
 OR d.dimension_code<>'volume' OR g.standard_temperature<>60 OR g.temperature_scale<>'fahrenheit'
 OR NOT EXISTS(SELECT 1 FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code,kind,fixed_offset_seconds)=(g.catalog_id,g.revision_id,v.clock_code,'fixed_offset',0))
 OR EXISTS(SELECT 1 FROM ref_data.dimensions x WHERE(x.catalog_id,x.revision_id,x.code) IN((d.catalog_id,d.revision_id,d.dimension_code),(p.catalog_id,p.revision_id,p.dimension_code))
 AND(x.mass_power,x.length_power,x.time_power,x.temperature_power,x.amount_power,x.current_power,x.luminous_power)<>(0,3,0,0,0,0,0))
 OR NOT EXISTS(SELECT 1 FROM ref_data.settlement_targets WHERE(catalog_id,revision_id,code,kind)=(q.catalog_id,q.revision_id,c.settlement_target_code,'index') AND index_b_code IS NULL)
 THEN RAISE EXCEPTION 'crude_setup_compatibility';END IF;
 -- Same unit code alone cannot qualify a changed conversion definition.
 IF EXISTS(WITH RECURSIVE edges AS(
  SELECT catalog_id,revision_id,from_unit_code f,to_unit_code t FROM ref_data.unit_conversions
  WHERE(catalog_id,revision_id) IN((d.catalog_id,d.revision_id),(p.catalog_id,p.revision_id))
  UNION ALL SELECT catalog_id,revision_id,to_unit_code,from_unit_code FROM ref_data.unit_conversions
  WHERE(catalog_id,revision_id) IN((d.catalog_id,d.revision_id),(p.catalog_id,p.revision_id))),
 reachable(catalog_id,revision_id,code) AS(
  VALUES(d.catalog_id,d.revision_id,d.unit_code),(p.catalog_id,p.revision_id,p.unit_code)
  UNION SELECT e.catalog_id,e.revision_id,e.t FROM reachable r JOIN edges e ON(e.catalog_id,e.revision_id,e.f)=(r.catalog_id,r.revision_id,r.code)),
 definitions AS(SELECT x.catalog_id,x.revision_id,x.from_unit_code,x.to_unit_code,
   x.factor_numerator*1e18/gcd(x.factor_numerator*1e18,x.factor_denominator*1e18) factor_numerator,
   x.factor_denominator*1e18/gcd(x.factor_numerator*1e18,x.factor_denominator*1e18) factor_denominator
  FROM ref_data.unit_conversions x JOIN reachable r ON(r.catalog_id,r.revision_id,r.code)=(x.catalog_id,x.revision_id,x.from_unit_code)),
 delivery AS(SELECT from_unit_code,to_unit_code,factor_numerator,factor_denominator FROM definitions WHERE(catalog_id,revision_id)=(d.catalog_id,d.revision_id)),
 pricing AS(SELECT from_unit_code,to_unit_code,factor_numerator,factor_denominator FROM definitions WHERE(catalog_id,revision_id)=(p.catalog_id,p.revision_id))
 (SELECT * FROM pricing EXCEPT SELECT * FROM delivery))
 THEN RAISE EXCEPTION 'crude_setup_compatibility';END IF;
 IF EXISTS(SELECT 1 FROM generate_series(date_trunc('month',v.valid_from::timestamp),date_trunc('month',(v.valid_to-1)::timestamp),interval '1 month') month
 WHERE(SELECT count(*) FROM market_data.curve_members b JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(b.catalog_id,b.revision_id,b.contract_id)
 WHERE(b.catalog_id,b.revision_id,b.curve_code)=(catalog,v.definition_revision_id,v.curve_code)
 AND k.kind='forward' AND k.delivery_from=month::date AND k.delivery_to=(month+interval '1 month')::date
 AND NOT EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(k.catalog_id,k.revision_id,k.id))
 AND(SELECT count(*) FROM market_data.index_fixing_observations f JOIN ref_data.settlement_targets t ON(t.catalog_id,t.revision_id,t.code)=(q.catalog_id,q.revision_id,c.settlement_target_code)
 WHERE(f.catalog_id,f.revision_id,f.index_code,f.observation_from,f.observation_to,f.quote_code)=(catalog,v.definition_revision_id,t.index_a_code,k.delivery_from,k.delivery_to,q.code))=1)<>1)
 THEN RAISE EXCEPTION 'crude_setup_coverage';END IF;
 IF NOT security.crude_setup_visible(catalog,setup) THEN RAISE EXCEPTION 'Crude setup authority expired' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.save_crude_setup(catalog uuid,operation uuid,base uuid,configuration jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;prior market_data.crude_setup_commands;v market_data.crude_setup_versions;answer jsonb;payload jsonb;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'WriteMarketDefinitions') THEN RAISE EXCEPTION 'No crude setup authoring access' USING ERRCODE='42501';END IF;
 actor=security.custom_curve_actor(catalog,'WriteMarketDefinitions');
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.market_access(catalog,'WriteMarketDefinitions') THEN RAISE EXCEPTION 'No crude setup authoring access' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR jsonb_typeof(configuration) IS DISTINCT FROM 'object'
 OR (SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(configuration) k) IS DISTINCT FROM ARRAY['clock','code','commodity','curve','custodyPoint','definitionRevisionId','deliveryReferenceCatalogId','deliveryReferenceRevisionId','facility','grade','quantityPolicy','validFrom','validTo']::text[]
 THEN RAISE EXCEPTION 'crude_setup_input';END IF;
 IF coalesce(configuration->>'validFrom','') !~ '^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}$' OR coalesce(configuration->>'validTo','') !~ '^[0-9]{4}-[0-9]{1,2}-[0-9]{1,2}$'
 THEN RAISE EXCEPTION 'crude_setup_input';END IF;
 configuration=configuration||jsonb_build_object(
  'definitionRevisionId',(configuration->>'definitionRevisionId')::uuid::text,
  'deliveryReferenceCatalogId',(configuration->>'deliveryReferenceCatalogId')::uuid::text,
  'deliveryReferenceRevisionId',(configuration->>'deliveryReferenceRevisionId')::uuid::text,
  'validFrom',(configuration->>'validFrom')::date::text,'validTo',(configuration->>'validTo')::date::text);
 payload=jsonb_build_object('base',base,'configuration',configuration);
 SELECT * INTO prior FROM market_data.crude_setup_commands WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.command<>'save' OR prior.payload IS DISTINCT FROM payload THEN RAISE EXCEPTION 'crude_setup_conflict';END IF;
  IF NOT security.crude_setup_visible(catalog,prior.setup_id) THEN RAISE EXCEPTION 'No source access' USING ERRCODE='42501';END IF;
  RETURN prior.result;
 END IF;
 SELECT * INTO v FROM market_data.crude_setup_versions WHERE catalog_id=catalog AND crude_setup_versions.configuration=save_crude_setup.configuration
 AND(base_setup_id IS NOT DISTINCT FROM base OR state='published' AND NOT EXISTS(
  SELECT 1 FROM market_data.crude_setup_versions n WHERE n.catalog_id=catalog AND n.code=crude_setup_versions.code
   AND n.state='published' AND n.publication_order>crude_setup_versions.publication_order)) ORDER BY publication_order DESC NULLS LAST,id LIMIT 1;
 IF NOT FOUND THEN
  INSERT INTO market_data.crude_setup_versions(catalog_id,id,code,base_setup_id,actor_id,configuration,definition_revision_id,curve_code,
   delivery_reference_catalog_id,delivery_reference_revision_id,commodity_code,grade_code,facility_code,custody_point_code,clock_code,valid_from,valid_to,quantity_policy,state)
  VALUES(catalog,gen_random_uuid(),configuration->>'code',base,actor,configuration,(configuration->>'definitionRevisionId')::uuid,configuration->>'curve',
   (configuration->>'deliveryReferenceCatalogId')::uuid,(configuration->>'deliveryReferenceRevisionId')::uuid,configuration->>'commodity',configuration->>'grade',configuration->>'facility',configuration->>'custodyPoint',configuration->>'clock',
   (configuration->>'validFrom')::date,(configuration->>'validTo')::date,configuration->>'quantityPolicy','draft') RETURNING * INTO v;
 END IF;
 IF NOT security.crude_setup_visible(catalog,v.id) THEN RAISE EXCEPTION 'No source access' USING ERRCODE='42501';END IF;
 IF base IS NOT NULL AND NOT EXISTS(SELECT 1 FROM market_data.crude_setup_versions WHERE(catalog_id,id,code,state)=(catalog,base,v.code,'published') AND security.crude_setup_visible(catalog,id)) THEN RAISE EXCEPTION 'crude_setup_conflict';END IF;
 answer=jsonb_build_object('setupId',v.id,'state',v.state,'operationId',operation);
 INSERT INTO market_data.crude_setup_commands VALUES(catalog,actor,operation,'save',payload,answer,v.id);
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'WriteMarketDefinitions') OR NOT security.crude_setup_visible(catalog,v.id) THEN RAISE EXCEPTION 'Crude setup authority expired' USING ERRCODE='42501';END IF;
 RETURN answer;
END$$;
CREATE FUNCTION security.publish_crude_setup(catalog uuid,setup uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;v market_data.crude_setup_versions;prior market_data.crude_setup_commands;head uuid;answer jsonb;payload jsonb=jsonb_build_object('setupId',setup);publication integer;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') THEN RAISE EXCEPTION 'No crude setup publication access' USING ERRCODE='42501';END IF;
 actor=security.custom_curve_actor(catalog,'SealMarketDefinitions');PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.crude_setup_visible(catalog,setup) THEN RAISE EXCEPTION 'No source access' USING ERRCODE='42501';END IF;
 SELECT * INTO prior FROM market_data.crude_setup_commands WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.command<>'publish' OR prior.payload IS DISTINCT FROM payload THEN RAISE EXCEPTION 'crude_setup_conflict';END IF;
  RETURN prior.result;
 END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'crude_setup_input';END IF;
 SELECT * INTO STRICT v FROM market_data.crude_setup_versions WHERE(catalog_id,id)=(catalog,setup);
 PERFORM security.require_crude_setup(catalog,setup);
 SELECT id INTO head FROM market_data.crude_setup_versions WHERE catalog_id=catalog AND code=v.code AND state='published' ORDER BY publication_order DESC LIMIT 1;
 IF v.state='draft' THEN
  IF head IS DISTINCT FROM v.base_setup_id THEN RAISE EXCEPTION 'crude_setup_conflict';END IF;
  -- The current crude ticket has one selected original pricing context. Multiple
  -- grades/curves inside that context are supported; a second context refuses.
  IF EXISTS(SELECT 1 FROM market_data.crude_setup_versions h WHERE h.catalog_id=catalog AND h.code<>v.code AND h.state='published'
   AND h.definition_revision_id<>v.definition_revision_id AND NOT EXISTS(SELECT 1 FROM market_data.crude_setup_versions n
    WHERE n.catalog_id=catalog AND n.code=h.code AND n.state='published' AND n.publication_order>h.publication_order))
  THEN RAISE EXCEPTION 'crude_setup_market_scope';END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM market_data.crude_setup_versions WHERE catalog_id=catalog;
  UPDATE market_data.crude_setup_versions SET state='published',publication_order=publication,publication_xid=pg_current_xact_id() WHERE(catalog_id,id)=(catalog,setup);
 END IF;
 answer=jsonb_build_object('setupId',setup,'state','published','operationId',operation);
 INSERT INTO market_data.crude_setup_commands VALUES(catalog,actor,operation,'publish',payload,answer,setup);
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'SealMarketDefinitions') OR NOT security.crude_setup_visible(catalog,setup) THEN RAISE EXCEPTION 'Crude setup authority expired' USING ERRCODE='42501';END IF;
 RETURN answer;
END$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_crude_setup_context;
REVOKE ALL ON FUNCTION security.capture_pre_crude_setup_context(uuid,date,timestamptz) FROM openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r record;known timestamptz;last_known timestamptz;certified timestamptz;
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RETURN security.capture_pre_crude_setup_context(catalog,effective_date,requested_at);END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No temporal access' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT max(known_from) INTO last_known FROM market_data.crude_setup_receipts WHERE catalog_id=catalog;
 FOR r IN SELECT v.* FROM market_data.crude_setup_versions v LEFT JOIN market_data.crude_setup_receipts k ON(k.catalog_id,k.setup_id)=(v.catalog_id,v.id)
  WHERE v.catalog_id=catalog AND v.state='published' AND k.setup_id IS NULL ORDER BY v.publication_order LOOP
  known=security.checked_commit_time(r.publication_xid);
  IF known<last_known OR known<=certified THEN RAISE EXCEPTION 'knowledge_clock';END IF;
  INSERT INTO market_data.crude_setup_receipts VALUES(catalog,r.id,r.publication_xid,known);last_known=known;
 END LOOP;
 RETURN security.capture_pre_crude_setup_context(catalog,effective_date,requested_at);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('crude_setup_visible','current_crude_setups','require_crude_setup','save_crude_setup','publish_crude_setup','capture_date_context') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.crude_setup_visible(uuid,uuid),security.current_crude_setups(uuid),security.require_crude_setup(uuid,uuid),security.save_crude_setup(uuid,uuid,uuid,jsonb),security.publish_crude_setup(uuid,uuid,uuid),security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_risk_worker;
COMMIT;
