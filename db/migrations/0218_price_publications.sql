-- Independently versioned price publications reference sealed commodity/index data.
-- Their operational release calendars do not alter v1 fixing boundaries.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE market_data.price_publishers (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),publisher_id uuid NOT NULL,
 version integer NOT NULL CHECK(version BETWEEN 1 AND 1000),name text NOT NULL CHECK(length(name) BETWEEN 1 AND 200 AND btrim(name)=name),
 publication_order integer NOT NULL,PRIMARY KEY(catalog_id,publisher_id,version),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE market_data.price_publication_versions (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),publication_id uuid NOT NULL,version integer NOT NULL CHECK(version BETWEEN 1 AND 1000),
 publisher_id uuid NOT NULL,publisher_version integer NOT NULL,name text NOT NULL CHECK(length(name) BETWEEN 1 AND 200 AND btrim(name)=name),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,calendar_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,
 implementation text NOT NULL CHECK(implementation IN('gas_daily_explicit_v1','gas_monthly_explicit_v1','crude_placeholder_v1','ngl_placeholder_v1','lng_placeholder_v1','power_placeholder_v1')),
 publication_order integer NOT NULL,PRIMARY KEY(catalog_id,publication_id,version),
 UNIQUE(catalog_id,publication_id,version,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,publisher_id,publisher_version) REFERENCES market_data.price_publishers(catalog_id,publisher_id,version),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE market_data.price_publication_commodities (
 catalog_id uuid NOT NULL,publication_id uuid NOT NULL,version integer NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 commodity_code ref_data.code NOT NULL,PRIMARY KEY(catalog_id,publication_id,version,commodity_code),
 FOREIGN KEY(catalog_id,publication_id,version,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.price_publication_versions(catalog_id,publication_id,version,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,commodity_code) REFERENCES ref_data.commodities(catalog_id,revision_id,code)
);
CREATE TABLE market_data.price_publication_indices (
 catalog_id uuid NOT NULL,publication_id uuid NOT NULL,version integer NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 index_code ref_data.code NOT NULL,quote_code ref_data.code NOT NULL,commodity_code ref_data.code NOT NULL,
 symbol text NOT NULL CHECK(length(symbol) BETWEEN 1 AND 200 AND btrim(symbol)=symbol),
 statistic text NOT NULL CHECK(length(statistic) BETWEEN 1 AND 200 AND btrim(statistic)=statistic),
 PRIMARY KEY(catalog_id,publication_id,version,index_code),UNIQUE(catalog_id,publication_id,version,symbol),
 FOREIGN KEY(catalog_id,publication_id,version,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.price_publication_versions(catalog_id,publication_id,version,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,publication_id,version,commodity_code) REFERENCES market_data.price_publication_commodities(catalog_id,publication_id,version,commodity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code)
);
CREATE TABLE market_data.price_publication_requests (
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 command jsonb NOT NULL CHECK(pg_column_size(command)<=1048576),publication_id uuid NOT NULL,version integer NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,publication_id,version) REFERENCES market_data.price_publication_versions(catalog_id,publication_id,version)
);
CREATE FUNCTION market_data.reject_price_publication_mutation() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN RAISE EXCEPTION 'publication_immutable' USING ERRCODE='55000';END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['price_publishers','price_publication_versions','price_publication_commodities','price_publication_indices','price_publication_requests'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY private_storage ON market_data.%I FOR SELECT TO openetrm_runtime USING(false)',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime',t);
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.reject_price_publication_mutation()',t);
 END LOOP;
END$$;
CREATE FUNCTION market_data.price_publication_definition(catalog uuid,publication uuid,selected_version integer) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('key',jsonb_build_object('catalogId',v.catalog_id,'id',v.publication_id,'version',v.version),
 'references',jsonb_build_object('catalogId',v.reference_catalog_id,'revisionId',v.reference_revision_id),
 'publisher',jsonb_build_object('key',jsonb_build_object('catalogId',p.catalog_id,'id',p.publisher_id,'version',p.version),'name',p.name),'name',v.name,
 'calendar',jsonb_build_object('revision',jsonb_build_object('catalogId',v.reference_catalog_id,'revisionId',v.reference_revision_id),'code',v.calendar_code),
 'clock',jsonb_build_object('revision',jsonb_build_object('catalogId',v.reference_catalog_id,'revisionId',v.reference_revision_id),'code',v.clock_code),'implementation',v.implementation,
 'commodities',coalesce((SELECT jsonb_agg(jsonb_build_object('revision',jsonb_build_object('catalogId',m.reference_catalog_id,'revisionId',m.reference_revision_id),'code',m.commodity_code) ORDER BY m.commodity_code)
 FROM market_data.price_publication_commodities m WHERE(m.catalog_id,m.publication_id,m.version)=(v.catalog_id,v.publication_id,v.version)),'[]'),
 'indices',coalesce((SELECT jsonb_agg(jsonb_build_object(
 'key',jsonb_build_object('revision',jsonb_build_object('catalogId',m.reference_catalog_id,'revisionId',m.reference_revision_id),'code',m.index_code),
 'commodity',jsonb_build_object('revision',jsonb_build_object('catalogId',m.reference_catalog_id,'revisionId',m.reference_revision_id),'code',i.commodity_code),
 'location',jsonb_build_object('revision',jsonb_build_object('catalogId',m.reference_catalog_id,'revisionId',m.reference_revision_id),'code',i.location_code),
 'quote',jsonb_build_object('revision',jsonb_build_object('catalogId',m.reference_catalog_id,'revisionId',m.reference_revision_id),'code',m.quote_code),
 'unit',jsonb_build_object('currency',jsonb_build_object('revision',jsonb_build_object('catalogId',m.reference_catalog_id,'revisionId',m.reference_revision_id),'code',q.currency_code),
 'quantity',jsonb_build_object('revision',jsonb_build_object('catalogId',m.reference_catalog_id,'revisionId',m.reference_revision_id),'code',q.quantity_code),'multiplier',trim_scale(q.multiplier)::text),
 'symbol',m.symbol,'statistic',m.statistic) ORDER BY m.index_code)
 FROM market_data.price_publication_indices m JOIN ref_data.price_index_definitions i ON(i.catalog_id,i.revision_id,i.code)=(m.reference_catalog_id,m.reference_revision_id,m.index_code)
 JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code)
 WHERE(m.catalog_id,m.publication_id,m.version)=(v.catalog_id,v.publication_id,v.version)),'[]'))
 FROM market_data.price_publication_versions v JOIN market_data.price_publishers p ON(p.catalog_id,p.publisher_id,p.version)=(v.catalog_id,v.publisher_id,v.publisher_version)
 WHERE(v.catalog_id,v.publication_id,v.version)=(catalog,publication,selected_version);
$$;
CREATE FUNCTION security.price_publication_visible(catalog uuid,publication uuid,selected_version integer) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT (NOT pg_has_role(session_user,'openetrm_risk_worker','MEMBER') OR risk.worker_read()) AND security.market_access(catalog,'ReadMarketData') AND EXISTS(SELECT 1 FROM market_data.price_publication_versions v
 WHERE(v.catalog_id,v.publication_id,v.version)=(catalog,publication,selected_version)
 AND security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id)
 AND(NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
 AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts c JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(c.market_catalog_id,c.market_context_id)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(x.catalog_id,x.context_id)
 JOIN trading.knowledge_receipts k ON k.catalog_id=x.catalog_id AND k.publication_order=v.publication_order
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
 AND x.catalog_id=catalog AND v.publication_order<=x.known_through AND k.known_from<=t.known_at)));
$$;
ALTER FUNCTION security.price_publication_visible(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.price_publication_visible(uuid,uuid,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.price_publication_visible(uuid,uuid,integer) TO openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION market_data.read_price_publications(catalog uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected_context uuid;result jsonb;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read()) THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 SELECT market_context_id INTO selected_context FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
 AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 SELECT coalesce(jsonb_agg(market_data.price_publication_definition(v.catalog_id,v.publication_id,v.version) ORDER BY v.name,v.publication_id,v.version),'[]') INTO result
 FROM market_data.price_publication_versions v WHERE v.catalog_id=catalog
 AND security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id)
 AND(selected_context IS NULL OR EXISTS(SELECT 1 FROM trading.contexts x JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(x.catalog_id,x.context_id)
 JOIN trading.knowledge_receipts k ON k.catalog_id=x.catalog_id AND k.publication_order=v.publication_order
 WHERE(x.catalog_id,x.context_id)=(catalog,selected_context) AND v.publication_order<=x.known_through AND k.known_from<=t.known_at));
 IF NOT security.market_access(catalog,'ReadMarketData') OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read()) THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
CREATE FUNCTION market_data.save_price_publication(catalog uuid,request uuid,expected integer,body jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;publication uuid;selected_version integer;reference_catalog uuid;reference_revision uuid;publisher uuid;publisher_version integer;
 prior market_data.price_publication_requests;command jsonb;existing jsonb;head integer;ordinal integer;member jsonb;index_row ref_data.price_index_definitions;
 quote_row ref_data.quote_specifications;quantity_row ref_data.quantity_specifications;reference_key jsonb;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'publication_historical_write' USING ERRCODE='42501';END IF;
 actor=security.custom_curve_actor(catalog,'WriteMarketDefinitions');
 IF actor IS NULL OR NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF security.custom_curve_actor(catalog,'WriteMarketDefinitions') IS DISTINCT FROM actor THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000' OR expected IS NULL OR expected<0 OR body IS NULL OR jsonb_typeof(body)<>'object' OR pg_column_size(body)>1048576
 THEN RAISE EXCEPTION 'publication_command' USING ERRCODE='22023';END IF;
 publication=(body#>>'{key,id}')::uuid;selected_version=(body#>>'{key,version}')::integer;
 reference_catalog=(body#>>'{references,catalogId}')::uuid;reference_revision=(body#>>'{references,revisionId}')::uuid;
 publisher=(body#>>'{publisher,key,id}')::uuid;publisher_version=(body#>>'{publisher,key,version}')::integer;
 IF (body#>>'{key,catalogId}')::uuid IS DISTINCT FROM catalog OR(body#>>'{publisher,key,catalogId}')::uuid IS DISTINCT FROM catalog
 OR publication IS NULL OR publication='00000000-0000-0000-0000-000000000000' OR publisher IS NULL OR publisher='00000000-0000-0000-0000-000000000000'
 OR selected_version IS DISTINCT FROM expected+1 OR jsonb_typeof(body->'commodities') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'commodities') NOT BETWEEN 1 AND 32
 OR jsonb_typeof(body->'indices') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'indices')>1000
 THEN RAISE EXCEPTION 'publication_definition' USING ERRCODE='22023';END IF;
 IF NOT security.reference_version_visible(reference_catalog,reference_revision) THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 command=jsonb_build_object('expectedVersion',expected,'definition',body);
 SELECT * INTO prior FROM market_data.price_publication_requests r WHERE(r.catalog_id,r.actor_id,r.request_id)=(catalog,actor,request);
 IF FOUND THEN
  IF prior.command<>command THEN RAISE EXCEPTION 'publication_request_conflict' USING ERRCODE='22023';END IF;
  RETURN market_data.price_publication_definition(catalog,prior.publication_id,prior.version);
 END IF;
 reference_key=jsonb_build_object('catalogId',reference_catalog,'revisionId',reference_revision);
 IF body#>'{calendar,revision}' IS DISTINCT FROM reference_key OR body#>'{clock,revision}' IS DISTINCT FROM reference_key
 OR EXISTS(SELECT 1 FROM jsonb_array_elements(body->'commodities') m WHERE m->'revision' IS DISTINCT FROM reference_key)
 THEN RAISE EXCEPTION 'publication_definition' USING ERRCODE='22023';END IF;
 FOR member IN SELECT value FROM jsonb_array_elements(body->'indices') LOOP
  IF member#>'{key,revision}' IS DISTINCT FROM reference_key OR member#>'{commodity,revision}' IS DISTINCT FROM reference_key OR member#>'{location,revision}' IS DISTINCT FROM reference_key
   OR member#>'{quote,revision}' IS DISTINCT FROM reference_key OR member#>'{unit,currency,revision}' IS DISTINCT FROM reference_key OR member#>'{unit,quantity,revision}' IS DISTINCT FROM reference_key
  THEN RAISE EXCEPTION 'publication_definition' USING ERRCODE='22023';END IF;
  SELECT * INTO index_row FROM ref_data.price_index_definitions WHERE(catalog_id,revision_id,code)=(reference_catalog,reference_revision,member#>>'{key,code}');
  SELECT * INTO quote_row FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(reference_catalog,reference_revision,member#>>'{quote,code}');
  SELECT * INTO quantity_row FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(reference_catalog,reference_revision,quote_row.quantity_code);
  IF index_row.code IS NULL OR quote_row.code IS NULL OR quote_row.kind<>'outright' OR index_row.commodity_code IS DISTINCT FROM member#>>'{commodity,code}'
   OR index_row.location_code IS DISTINCT FROM member#>>'{location,code}' OR quantity_row.commodity_code IS DISTINCT FROM index_row.commodity_code
   OR index_row.currency_code IS DISTINCT FROM quote_row.currency_code OR index_row.quote_unit_code IS DISTINCT FROM quantity_row.unit_code OR index_row.quote_multiplier IS DISTINCT FROM quote_row.multiplier
   OR member#>>'{unit,currency,code}' IS DISTINCT FROM quote_row.currency_code OR member#>>'{unit,quantity,code}' IS DISTINCT FROM quote_row.quantity_code
   OR(member#>>'{unit,multiplier}')::numeric IS DISTINCT FROM quote_row.multiplier
  THEN RAISE EXCEPTION 'publication_index' USING ERRCODE='22023';END IF;
 END LOOP;
 SELECT coalesce(max(version),0) INTO head FROM market_data.price_publication_versions WHERE(catalog_id,publication_id)=(catalog,publication);
 IF head>0 THEN
  existing=market_data.price_publication_definition(catalog,publication,head);
  IF(existing-'key')=(body-'key') THEN
   INSERT INTO market_data.price_publication_requests VALUES(catalog,actor,request,command,publication,head);RETURN existing;
  END IF;
 END IF;
 IF head<>expected THEN RAISE EXCEPTION 'publication_revision_conflict' USING ERRCODE='40001';END IF;
 IF jsonb_array_length(body->'indices')=0 AND(body->>'implementation') IN('gas_daily_explicit_v1','gas_monthly_explicit_v1')
 THEN RAISE EXCEPTION 'publication_definition' USING ERRCODE='22023';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,ordinal,pg_current_xact_id());
 SELECT jsonb_build_object('name',p.name) INTO existing FROM market_data.price_publishers p WHERE(p.catalog_id,p.publisher_id,p.version)=(catalog,publisher,publisher_version);
 IF FOUND THEN
  IF existing->>'name' IS DISTINCT FROM body#>>'{publisher,name}' THEN RAISE EXCEPTION 'publication_publisher_conflict' USING ERRCODE='40001';END IF;
 ELSE
  SELECT coalesce(max(version),0) INTO head FROM market_data.price_publishers WHERE(catalog_id,publisher_id)=(catalog,publisher);
  IF publisher_version IS DISTINCT FROM head+1 THEN RAISE EXCEPTION 'publication_publisher_conflict' USING ERRCODE='40001';END IF;
  INSERT INTO market_data.price_publishers VALUES(catalog,publisher,publisher_version,body#>>'{publisher,name}',ordinal);
 END IF;
 INSERT INTO market_data.price_publication_versions VALUES(catalog,publication,selected_version,publisher,publisher_version,body->>'name',reference_catalog,reference_revision,
  body#>>'{calendar,code}',body#>>'{clock,code}',body->>'implementation',ordinal);
 INSERT INTO market_data.price_publication_commodities SELECT catalog,publication,selected_version,reference_catalog,reference_revision,m->>'code' FROM jsonb_array_elements(body->'commodities') m;
 INSERT INTO market_data.price_publication_indices SELECT catalog,publication,selected_version,reference_catalog,reference_revision,m#>>'{key,code}',m#>>'{quote,code}',m#>>'{commodity,code}',m->>'symbol',m->>'statistic' FROM jsonb_array_elements(body->'indices') m;
 IF NOT security.reference_version_visible(reference_catalog,reference_revision) OR security.custom_curve_actor(catalog,'WriteMarketDefinitions') IS DISTINCT FROM actor
 THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 INSERT INTO market_data.price_publication_requests VALUES(catalog,actor,request,command,publication,selected_version);
 RETURN market_data.price_publication_definition(catalog,publication,selected_version);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='market_data'
 AND p.proname IN('reject_price_publication_mutation','price_publication_definition','read_price_publications','save_price_publication') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname IN('read_price_publications','save_price_publication') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION market_data.read_price_publications(uuid) TO openetrm_risk_worker;
COMMIT;
