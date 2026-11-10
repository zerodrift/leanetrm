-- A context selects an explicit published catalogue, never an arbitrary visible
-- definition revision. Its members are exact immutable source relationships.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
CREATE TABLE market_data.catalogue_publications(
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),id uuid NOT NULL,
 previous_id uuid,effective_from date NOT NULL,effective_to date NOT NULL,
 publication_order integer NOT NULL CHECK(publication_order>0),publication_xid xid8 NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,publication_order),UNIQUE(catalog_id,publication_order,publication_xid),
 FOREIGN KEY(catalog_id,previous_id) REFERENCES market_data.catalogue_publications(catalog_id,id),
 CHECK(isfinite(effective_from) AND isfinite(effective_to) AND effective_from<effective_to)
);
CREATE TABLE market_data.catalogue_curves(
 catalog_id uuid NOT NULL,publication_id uuid NOT NULL,code text NOT NULL,
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 curve_code ref_data.code,rate_code ref_data.code,
 PRIMARY KEY(catalog_id,publication_id,code),
 FOREIGN KEY(catalog_id,publication_id) REFERENCES market_data.catalogue_publications(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,rate_code) REFERENCES market_data.rate_curves(catalog_id,revision_id,code),
 CHECK(num_nonnulls(curve_code,rate_code)=1 AND code=coalesce(curve_code,rate_code))
);
CREATE TABLE market_data.catalogue_roles(
 catalog_id uuid NOT NULL,publication_id uuid NOT NULL,role text NOT NULL
  CHECK(role IN('primary','units','gas','crude','lng','ppa') OR role ~ '^reference:[a-z][a-z0-9_]*$'),
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,publication_id,role),
 FOREIGN KEY(catalog_id,publication_id) REFERENCES market_data.catalogue_publications(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id)
);
CREATE TABLE market_data.catalogue_commands(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 request jsonb NOT NULL,publication_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,publication_id) REFERENCES market_data.catalogue_publications(catalog_id,id)
);
CREATE TABLE market_data.catalogue_knowledge_receipts(
 catalog_id uuid NOT NULL,publication_order integer NOT NULL,publication_xid xid8 NOT NULL,known_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid)
  REFERENCES market_data.catalogue_publications(catalog_id,publication_order,publication_xid)
);
CREATE TABLE market_data.catalogue_contexts(
 catalog_id uuid NOT NULL,context_id uuid NOT NULL,known_through integer NOT NULL CHECK(known_through>=0),
 PRIMARY KEY(catalog_id,context_id),
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE FUNCTION market_data.guard_catalogue_publication() RETURNS trigger
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'catalogue_immutable' USING ERRCODE='55000';END IF;
 IF TG_TABLE_NAME='catalogue_publications' THEN
  IF NEW.publication_xid<>pg_current_xact_id() THEN RAISE EXCEPTION 'catalogue_publication' USING ERRCODE='23514';END IF;
 END IF;
 RETURN NEW;
END;$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['catalogue_publications','catalogue_curves','catalogue_roles','catalogue_commands','catalogue_knowledge_receipts','catalogue_contexts'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_all ON market_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_catalogue BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_catalogue_publication()',t);
 END LOOP;
END$$;
CREATE FUNCTION security.catalogue_json(catalog uuid,publication uuid) RETURNS jsonb
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',p.id,'previousId',p.previous_id,'effectiveFrom',p.effective_from,'effectiveTo',p.effective_to,
  'publicationOrder',p.publication_order,
  'curves',coalesce((SELECT jsonb_agg(jsonb_build_object('code',m.code,'kind',CASE WHEN m.rate_code IS NULL THEN 'curve' ELSE 'rate' END,
   'definitionRevisionId',m.definition_revision_id,'referenceCatalogId',m.reference_catalog_id,'referenceRevisionId',m.reference_revision_id) ORDER BY m.code COLLATE "C")
   FROM market_data.catalogue_curves m WHERE(m.catalog_id,m.publication_id)=(p.catalog_id,p.id)),'[]'::jsonb),
  'roles',coalesce((SELECT jsonb_object_agg(m.role,jsonb_build_object('definitionRevisionId',m.definition_revision_id,
   'referenceCatalogId',m.reference_catalog_id,'referenceRevisionId',m.reference_revision_id)) FROM market_data.catalogue_roles m
   WHERE(m.catalog_id,m.publication_id)=(p.catalog_id,p.id)),'{}'::jsonb))
 FROM market_data.catalogue_publications p WHERE(p.catalog_id,p.id)=(catalog,publication);
$$;
CREATE FUNCTION security.require_catalogue_sources(catalog uuid,publication uuid) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source record;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
 FOR source IN
  SELECT definition_revision_id,reference_catalog_id,reference_revision_id FROM market_data.catalogue_curves WHERE(catalog_id,publication_id)=(catalog,publication)
  UNION SELECT definition_revision_id,reference_catalog_id,reference_revision_id FROM market_data.catalogue_roles WHERE(catalog_id,publication_id)=(catalog,publication)
 LOOP
  IF security.reference_access(source.reference_catalog_id,'ReadMarketData') IS NOT TRUE
  THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
  IF security.market_fact_visible(catalog,source.definition_revision_id,NULL) IS NOT TRUE
  OR security.reference_version_visible(source.reference_catalog_id,source.reference_revision_id) IS NOT TRUE
  THEN RAISE EXCEPTION 'catalogue_source' USING ERRCODE='P0001';END IF;
 END LOOP;
END;$$;
CREATE FUNCTION security.catalogue_publish_outcome(catalog uuid,publication uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_catalogue_sources(catalog,publication);
 IF security.market_access(catalog,'SealMarketDefinitions') IS NOT TRUE THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
 RETURN security.catalogue_json(catalog,publication);
END;$$;
CREATE FUNCTION security.publish_catalogue(catalog uuid,operation uuid,expected uuid,definition jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;prior market_data.catalogue_commands;head uuid;identity uuid;ordinal integer;first_day date;last_day date;
 normalized jsonb;item jsonb;source record;entry text;kind text;
BEGIN
 actor=security.custom_curve_actor(catalog,'SealMarketDefinitions');
 IF actor IS NULL THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR security.current_write_context() IS NOT TRUE
 THEN RAISE EXCEPTION 'catalogue_publication' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF jsonb_typeof(definition) IS DISTINCT FROM 'object' OR jsonb_typeof(definition->'curves') IS DISTINCT FROM 'array'
 OR jsonb_typeof(definition->'roles') IS DISTINCT FROM 'object' OR jsonb_array_length(definition->'curves')>512
 OR NOT(definition->'roles' ? 'primary') OR (SELECT count(*) FROM jsonb_object_keys(definition->'roles'))>256
 OR (SELECT count(*) FROM jsonb_object_keys(definition))<>4
 OR NOT(definition ?& ARRAY['effectiveFrom','effectiveTo','curves','roles'])
 OR (SELECT count(DISTINCT value->>'code') FROM jsonb_array_elements(definition->'curves'))<>jsonb_array_length(definition->'curves')
 THEN RAISE EXCEPTION 'catalogue_publication' USING ERRCODE='P0001';END IF;
 first_day=(definition->>'effectiveFrom')::date;last_day=(definition->>'effectiveTo')::date;
 IF first_day IS NULL OR last_day IS NULL OR first_day>=last_day OR NOT isfinite(first_day) OR NOT isfinite(last_day)
 THEN RAISE EXCEPTION 'catalogue_publication' USING ERRCODE='P0001';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(definition->'curves') LOOP
  entry=item->>'code';kind=item->>'kind';
  IF entry IS NULL OR kind IS NULL OR kind NOT IN('curve','rate') OR jsonb_typeof(item) IS DISTINCT FROM 'object'
  OR (SELECT count(*) FROM jsonb_object_keys(item))<>5
  OR NOT(item ?& ARRAY['code','kind','definitionRevisionId','referenceCatalogId','referenceRevisionId'])
  THEN RAISE EXCEPTION 'catalogue_publication' USING ERRCODE='P0001';END IF;
 END LOOP;
 FOR entry,item IN SELECT key,value FROM jsonb_each(definition->'roles') LOOP
  IF entry NOT IN('primary','units','gas','crude','lng','ppa') AND NOT(entry LIKE 'reference:%' AND EXISTS(
   SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='ref_data' AND c.relname=substring(entry FROM 11)
   AND c.relkind='r' AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='catalog_id' AND NOT a.attisdropped)
   AND EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attname='revision_id' AND NOT a.attisdropped)))
  THEN RAISE EXCEPTION 'catalogue_publication' USING ERRCODE='P0001';END IF;
  IF jsonb_typeof(item) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(item))<>3
  OR NOT(item ?& ARRAY['definitionRevisionId','referenceCatalogId','referenceRevisionId']) THEN RAISE EXCEPTION 'catalogue_publication' USING ERRCODE='P0001';END IF;
 END LOOP;
 -- Normalize native coordinates, not their input spelling, before comparing facts.
 SELECT jsonb_build_object('effectiveFrom',first_day,'effectiveTo',last_day,
  'curves',coalesce((SELECT jsonb_agg(jsonb_build_object('code',value->>'code','kind',value->>'kind',
   'definitionRevisionId',(value->>'definitionRevisionId')::uuid,'referenceCatalogId',(value->>'referenceCatalogId')::uuid,
   'referenceRevisionId',(value->>'referenceRevisionId')::uuid) ORDER BY value->>'code' COLLATE "C") FROM jsonb_array_elements(definition->'curves')),'[]'::jsonb),
  'roles',(SELECT jsonb_object_agg(key,jsonb_build_object('definitionRevisionId',(value->>'definitionRevisionId')::uuid,
   'referenceCatalogId',(value->>'referenceCatalogId')::uuid,'referenceRevisionId',(value->>'referenceRevisionId')::uuid)) FROM jsonb_each(definition->'roles')))
 INTO normalized;
 normalized=jsonb_build_object('expected',expected,'definition',normalized);
 SELECT * INTO prior FROM market_data.catalogue_commands WHERE(catalog_id,actor_id,request_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.request<>normalized THEN RAISE EXCEPTION 'catalogue_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.catalogue_publish_outcome(catalog,prior.publication_id);
 END IF;
 -- A repeated fact on the same predecessor returns its first publication, even
 -- when a later command has since advanced the head. A->B->A is a new occurrence.
 SELECT c.publication_id INTO identity FROM market_data.catalogue_commands c
 WHERE c.catalog_id=catalog AND c.request=normalized LIMIT 1;
 IF identity IS NOT NULL THEN
  INSERT INTO market_data.catalogue_commands VALUES(catalog,actor,operation,normalized,identity);
  RETURN security.catalogue_publish_outcome(catalog,identity);
 END IF;
 SELECT p.id INTO head FROM market_data.catalogue_publications p WHERE p.catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 IF head IS DISTINCT FROM expected THEN RAISE EXCEPTION 'catalogue_conflict' USING ERRCODE='P0001';END IF;
 IF head IS NOT NULL AND (security.catalogue_json(catalog,head)-ARRAY['id','previousId','publicationOrder'])=normalized->'definition' THEN
  INSERT INTO market_data.catalogue_commands VALUES(catalog,actor,operation,normalized,head);
  RETURN security.catalogue_publish_outcome(catalog,head);
 END IF;
 identity=gen_random_uuid();SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.catalogue_publications WHERE catalog_id=catalog;
 INSERT INTO market_data.catalogue_publications VALUES(catalog,identity,head,first_day,last_day,ordinal,pg_current_xact_id());
 FOR item IN SELECT value FROM jsonb_array_elements(definition->'curves') LOOP
  entry=item->>'code';kind=item->>'kind';
  INSERT INTO market_data.catalogue_curves VALUES(catalog,identity,entry,(item->>'definitionRevisionId')::uuid,
   (item->>'referenceCatalogId')::uuid,(item->>'referenceRevisionId')::uuid,
   CASE WHEN kind='curve' THEN entry END,CASE WHEN kind='rate' THEN entry END);
 END LOOP;
 FOR entry,item IN SELECT key,value FROM jsonb_each(definition->'roles') LOOP
  INSERT INTO market_data.catalogue_roles VALUES(catalog,identity,entry,(item->>'definitionRevisionId')::uuid,
   (item->>'referenceCatalogId')::uuid,(item->>'referenceRevisionId')::uuid);
 END LOOP;
 FOR source IN
  SELECT definition_revision_id,reference_catalog_id,reference_revision_id FROM market_data.catalogue_curves WHERE(catalog_id,publication_id)=(catalog,identity)
  UNION SELECT definition_revision_id,reference_catalog_id,reference_revision_id FROM market_data.catalogue_roles WHERE(catalog_id,publication_id)=(catalog,identity)
 LOOP
  IF NOT EXISTS(SELECT 1 FROM market_data.revisions r JOIN market_data.revision_periods d ON(d.catalog_id,d.revision_id)=(r.catalog_id,r.id)
   WHERE(r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id,r.state)=(catalog,source.definition_revision_id,source.reference_catalog_id,source.reference_revision_id,'sealed')
   AND d.effective_from<=first_day AND last_day<=d.effective_to)
   OR security.market_fact_visible(catalog,source.definition_revision_id,NULL) IS NOT TRUE
   OR security.reference_version_visible(source.reference_catalog_id,source.reference_revision_id) IS NOT TRUE
  THEN RAISE EXCEPTION 'catalogue_source' USING ERRCODE='P0001';END IF;
 END LOOP;
 INSERT INTO market_data.catalogue_commands VALUES(catalog,actor,operation,normalized,identity);
 RETURN security.catalogue_publish_outcome(catalog,identity);
END;$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_catalogue_context;
REVOKE ALL ON FUNCTION security.capture_pre_catalogue_context(uuid,date,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;token uuid;prefix integer;
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RETURN security.capture_pre_catalogue_context(catalog,effective_date,requested_at);END IF;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
 SELECT known_from INTO last_commit FROM market_data.catalogue_knowledge_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
 FOR row IN SELECT p.* FROM market_data.catalogue_publications p LEFT JOIN market_data.catalogue_knowledge_receipts k USING(catalog_id,publication_order)
 WHERE p.catalog_id=catalog AND k.publication_order IS NULL ORDER BY p.publication_order LOOP
  commit_at=security.checked_commit_time(row.publication_xid);
  IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO market_data.catalogue_knowledge_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);last_commit=commit_at;
 END LOOP;
 token=security.capture_pre_catalogue_context(catalog,effective_date,requested_at);
 SELECT coalesce(max(k.publication_order),0) INTO prefix FROM market_data.catalogue_knowledge_receipts k JOIN market_data.temporal_contexts t
 ON(t.catalog_id,t.id)=(catalog,token) WHERE k.catalog_id=catalog AND k.known_from<=t.known_at;
 INSERT INTO market_data.catalogue_contexts VALUES(catalog,token,prefix) ON CONFLICT DO NOTHING;
 IF NOT EXISTS(SELECT 1 FROM market_data.catalogue_contexts WHERE(catalog_id,context_id,known_through)=(catalog,token,prefix))
 THEN RAISE EXCEPTION 'catalogue_context' USING ERRCODE='P0001';END IF;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
 RETURN token;
END;$$;
-- Current metadata has an explicit published-head policy. It is not a dated
-- pricing read; contextual consumers must use read_catalogue with their token.
CREATE FUNCTION security.read_current_catalogue(catalog uuid,effective_on date) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected uuid;
BEGIN
 IF security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'catalogue_context' USING ERRCODE='P0001';END IF;
 SELECT id INTO selected FROM market_data.catalogue_publications p WHERE p.catalog_id=catalog
 AND(read_current_catalogue.effective_on IS NULL OR p.effective_from<=read_current_catalogue.effective_on AND read_current_catalogue.effective_on<p.effective_to)
 ORDER BY publication_order DESC LIMIT 1;
 IF selected IS NULL THEN RAISE EXCEPTION 'catalogue_missing' USING ERRCODE='P0001';END IF;
 PERFORM security.require_catalogue_sources(catalog,selected);
 RETURN security.catalogue_json(catalog,selected);
END;$$;
CREATE FUNCTION security.read_catalogue(catalog uuid,context uuid) RETURNS jsonb
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected uuid;c security.request_contexts;source record;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS DISTINCT FROM context OR c.market_catalog_id IS DISTINCT FROM catalog
 OR NOT EXISTS(SELECT 1 FROM market_data.catalogue_contexts WHERE(catalog_id,context_id)=(catalog,context))
 THEN RAISE EXCEPTION 'catalogue_context' USING ERRCODE='P0001';END IF;
 SELECT p.id INTO selected FROM market_data.catalogue_publications p JOIN market_data.catalogue_knowledge_receipts k USING(catalog_id,publication_order,publication_xid)
 JOIN market_data.catalogue_contexts m ON(m.catalog_id,m.context_id)=(catalog,context)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)
 WHERE p.catalog_id=catalog AND p.publication_order<=m.known_through AND k.known_from<=t.known_at
 AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to ORDER BY p.publication_order DESC LIMIT 1;
 IF selected IS NULL THEN RAISE EXCEPTION 'catalogue_missing' USING ERRCODE='P0001';END IF;
 -- Decide the head before checking its sources' current authority. An unavailable
 -- head must never revive an older catalogue publication.
 PERFORM security.require_catalogue_sources(catalog,selected);
 RETURN security.catalogue_json(catalog,selected);
END;$$;
CREATE FUNCTION security.read_request_catalogue(catalog uuid,effective_on date) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected uuid;bound_catalog uuid;
BEGIN
 SELECT market_context_id,market_catalog_id INTO selected,bound_catalog FROM security.request_contexts
 WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF NOT FOUND THEN RAISE EXCEPTION 'catalogue_denied' USING ERRCODE='42501';END IF;
 IF selected IS NULL THEN RETURN security.read_current_catalogue(catalog,effective_on);END IF;
 IF bound_catalog IS DISTINCT FROM catalog OR effective_on IS NOT NULL AND NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,selected,read_request_catalogue.effective_on)) THEN RAISE EXCEPTION 'catalogue_context' USING ERRCODE='P0001';END IF;
 RETURN security.read_catalogue(catalog,selected);
END;$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
 AND p.proname IN('catalogue_json','catalogue_publish_outcome','require_catalogue_sources','publish_catalogue','capture_date_context','read_catalogue','read_current_catalogue','read_request_catalogue') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.read_request_catalogue(uuid,date),security.read_current_catalogue(uuid,date),security.publish_catalogue(uuid,uuid,uuid,jsonb),security.read_catalogue(uuid,uuid),security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_risk_worker;
COMMIT;
