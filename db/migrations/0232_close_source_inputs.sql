-- Exact source membership is part of every recorded input selection. Existing
-- single-source facts are normalized from their own header, without new history.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE market_data.market_close_sources(
 catalog_id uuid NOT NULL,close_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,close_id,definition_revision_id),
 UNIQUE(catalog_id,close_id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,close_id) REFERENCES market_data.market_closes(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id)
);
INSERT INTO market_data.market_close_sources SELECT catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id
 FROM market_data.market_closes;
ALTER TABLE market_data.market_closes ADD CONSTRAINT close_primary_source
 FOREIGN KEY(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.market_close_sources(catalog_id,close_id,definition_revision_id,reference_catalog_id,reference_revision_id)
 DEFERRABLE INITIALLY DEFERRED;
-- Use catalog introspection for generated constraint names, with exact structural
-- assertions. No unrelated relationship can be dropped by this migration.
DO $$DECLARE item record;removed integer:=0;BEGIN
 FOR item IN SELECT conrelid::regclass relation,conname FROM pg_constraint
 WHERE contype='f' AND conrelid IN('market_data.market_close_pins'::regclass,'market_data.market_close_members'::regclass)
 AND confrelid='market_data.market_closes'::regclass LOOP
  EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I',item.relation,item.conname);removed=removed+1;
 END LOOP;
 IF removed<>2 THEN RAISE EXCEPTION 'close_source_schema_drift';END IF;
END$$;
ALTER TABLE market_data.market_close_pins DROP CONSTRAINT market_close_pins_pkey,
 ADD PRIMARY KEY(catalog_id,close_id,definition_revision_id,series_id,input_use),
 ADD FOREIGN KEY(catalog_id,close_id,definition_revision_id) REFERENCES market_data.market_close_sources(catalog_id,close_id,definition_revision_id);
ALTER TABLE market_data.market_close_members DROP CONSTRAINT market_close_members_pkey,
 DROP CONSTRAINT market_close_members_catalog_id_close_id_curve_code_ordinal_key,
 ADD PRIMARY KEY(catalog_id,close_id,definition_revision_id,curve_code,contract_id),
 ADD UNIQUE(catalog_id,close_id,definition_revision_id,curve_code,ordinal),
 ADD FOREIGN KEY(catalog_id,close_id,definition_revision_id) REFERENCES market_data.market_close_sources(catalog_id,close_id,definition_revision_id);
CREATE TABLE market_data.market_close_commands(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 payload jsonb NOT NULL,close_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,close_id) REFERENCES market_data.market_closes(catalog_id,id)
);
INSERT INTO market_data.market_close_commands SELECT catalog_id,actor_id,request_id,request,id FROM market_data.market_closes;
ALTER TABLE market_data.market_close_commands OWNER TO openetrm_security_owner;
ALTER TABLE market_data.market_close_commands ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.market_close_commands FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON market_data.market_close_commands TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_close_command BEFORE UPDATE OR DELETE ON market_data.market_close_commands
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION security.insert_close_command() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 INSERT INTO market_data.market_close_commands VALUES(NEW.catalog_id,NEW.actor_id,NEW.request_id,NEW.request,NEW.id);
 RETURN NEW;
END;$$;
CREATE TRIGGER close_command AFTER INSERT ON market_data.market_closes FOR EACH ROW EXECUTE FUNCTION security.insert_close_command();
CREATE FUNCTION security.insert_close_primary_source() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 INSERT INTO market_data.market_close_sources VALUES(NEW.catalog_id,NEW.id,NEW.definition_revision_id,NEW.reference_catalog_id,NEW.reference_revision_id);
 RETURN NEW;
END;$$;
CREATE TRIGGER close_primary_source AFTER INSERT ON market_data.market_closes
 FOR EACH ROW EXECUTE FUNCTION security.insert_close_primary_source();
CREATE FUNCTION security.guard_close_source() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP<>'INSERT' OR NOT EXISTS(SELECT 1 FROM market_data.market_closes c
  WHERE(c.catalog_id,c.id,c.publication_xid)=(NEW.catalog_id,NEW.close_id,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'immutable_close_source' USING ERRCODE='55000';END IF;
 RETURN NEW;
END;$$;
CREATE TRIGGER immutable_close_source BEFORE INSERT OR UPDATE OR DELETE ON market_data.market_close_sources
 FOR EACH ROW EXECUTE FUNCTION security.guard_close_source();
ALTER TABLE market_data.market_close_sources OWNER TO openetrm_security_owner;
ALTER TABLE market_data.market_close_sources ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.market_close_sources FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON market_data.market_close_sources TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY runtime_read ON market_data.market_close_sources FOR SELECT TO openetrm_runtime USING(security.market_close_visible(catalog_id,close_id));
GRANT SELECT ON market_data.market_close_sources TO openetrm_runtime;
CREATE OR REPLACE FUNCTION security.market_close_visible(catalog uuid,close_id uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected market_data.market_closes;request security.request_contexts;
 temporal market_data.temporal_contexts;known integer;
BEGIN
 SELECT * INTO request FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF request.id IS NULL OR request.expires_at<=clock_timestamp() THEN RETURN false;END IF;
 SELECT * INTO selected FROM market_data.market_closes s WHERE(s.catalog_id,s.id)=(market_close_visible.catalog,market_close_visible.close_id);
 IF NOT FOUND THEN RETURN false;END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.market_close_sources x WHERE(x.catalog_id,x.close_id)=(market_close_visible.catalog,market_close_visible.close_id))
 OR EXISTS(SELECT 1 FROM market_data.market_close_sources x WHERE(x.catalog_id,x.close_id)=(market_close_visible.catalog,market_close_visible.close_id)
 AND(security.market_fact_visible(catalog,x.definition_revision_id,NULL) IS NOT TRUE
 OR security.reference_version_visible(x.reference_catalog_id,x.reference_revision_id) IS NOT TRUE)) THEN RETURN false;END IF;
 IF request.market_context_id IS NOT NULL THEN
  SELECT * INTO temporal FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(request.market_catalog_id,request.market_context_id);
  IF NOT FOUND OR temporal.catalog_id IS DISTINCT FROM catalog OR selected.business_date>temporal.effective_on THEN RETURN false;END IF;
  SELECT m.known_through INTO known FROM market_data.market_contexts m WHERE(m.catalog_id,m.context_id)=(temporal.catalog_id,temporal.id);
  IF NOT FOUND THEN RETURN false;END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.market_close_receipts r
   WHERE(r.catalog_id,r.publication_order,r.publication_xid)=(selected.catalog_id,selected.publication_order,selected.publication_xid)
   AND r.known_from<=temporal.known_at) THEN RETURN false;END IF;
  -- An absent or ineligible pin fails the entire close. Match the certified
  -- observation publication exactly, including its publication transaction.
  IF EXISTS(SELECT 1 FROM market_data.market_close_pins p WHERE(p.catalog_id,p.close_id)=(market_close_visible.catalog,market_close_visible.close_id)
   AND NOT EXISTS(SELECT 1 FROM market_data.knowledge_events e
    JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
    JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(e.catalog_id,e.revision_id)
    JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
    WHERE(e.catalog_id,e.revision_id,e.observation_id)=(p.catalog_id,p.definition_revision_id,p.observation_id)
    AND e.event_order<=known AND k.known_from<=temporal.known_at
    AND v.effective_from<=temporal.effective_on AND temporal.effective_on<v.effective_to
    AND o.observed_at<((temporal.effective_on+1)::timestamp AT TIME ZONE 'UTC')))
  THEN RETURN false;END IF;
 END IF;
 -- Current mode deliberately has no historical receipt requirement. Both modes
 -- retain fresh authority and the identical request/context through completion.
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id AND q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
  AND(q.market_catalog_id,q.market_context_id) IS NOT DISTINCT FROM(request.market_catalog_id,request.market_context_id))
 OR EXISTS(SELECT 1 FROM market_data.market_close_sources x WHERE(x.catalog_id,x.close_id)=(market_close_visible.catalog,market_close_visible.close_id)
 AND(security.market_fact_visible(catalog,x.definition_revision_id,NULL) IS NOT TRUE
 OR security.reference_version_visible(x.reference_catalog_id,x.reference_revision_id) IS NOT TRUE)) THEN RETURN false;END IF;
 RETURN true;
END;$$;
CREATE OR REPLACE FUNCTION security.read_market_close_parts(catalog uuid,close_ids uuid[],expected_context uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE request security.request_contexts;payload jsonb;
BEGIN
 SELECT * INTO request FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF request.id IS NULL OR request.expires_at<=clock_timestamp() OR request.market_context_id IS DISTINCT FROM expected_context
 OR(expected_context IS NOT NULL AND request.market_catalog_id IS DISTINCT FROM catalog) OR catalog IS NULL OR close_ids IS NULL
 OR cardinality(close_ids) NOT BETWEEN 1 AND 128 OR array_position(close_ids,NULL) IS NOT NULL
 OR(SELECT count(DISTINCT requested.id) FROM unnest(close_ids)requested(id))<>cardinality(close_ids)
 OR EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) LEFT JOIN market_data.market_closes s ON(s.catalog_id,s.id)=(catalog,requested.id)
 WHERE s.id IS NULL OR NOT security.market_close_visible(catalog,s.id))
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 SELECT jsonb_agg(jsonb_build_object('close_id',c.id,'primary_definition_revision_id',c.definition_revision_id,
  'sources',(SELECT jsonb_agg(jsonb_build_object('definition_revision_id',s.definition_revision_id,
   'reference_catalog_id',s.reference_catalog_id,'reference_revision_id',s.reference_revision_id,
   'pins',coalesce((SELECT jsonb_agg(jsonb_build_object('observation_id',p.observation_id,'input_use',p.input_use) ORDER BY p.series_id,p.input_use COLLATE "C")
    FROM market_data.market_close_pins p WHERE(p.catalog_id,p.close_id,p.definition_revision_id)=(s.catalog_id,s.close_id,s.definition_revision_id)),'[]'::jsonb),
   'members',coalesce((SELECT jsonb_agg(jsonb_build_object('curve_code',m.curve_code,'contract_id',m.contract_id,'scope',CASE WHEN EXISTS(SELECT 1 FROM market_data.close_session_curves k WHERE(k.catalog_id,k.code,k.version,k.definition_revision_id,k.curve_code)=(c.catalog_id,c.session_code,c.session_version,m.definition_revision_id,m.curve_code)) THEN 'root' ELSE 'retained' END) ORDER BY m.curve_code COLLATE "C",m.ordinal)
    FROM market_data.market_close_members m WHERE(m.catalog_id,m.close_id,m.definition_revision_id)=(s.catalog_id,s.close_id,s.definition_revision_id)),'[]'::jsonb)) ORDER BY s.definition_revision_id)
   FROM market_data.market_close_sources s WHERE(s.catalog_id,s.close_id)=(c.catalog_id,c.id))) ORDER BY c.id)
 INTO payload FROM market_data.market_closes c WHERE c.catalog_id=catalog AND c.id=ANY(close_ids);
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.id=request.id AND q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
 AND(q.market_catalog_id,q.market_context_id) IS NOT DISTINCT FROM(request.market_catalog_id,request.market_context_id))
 OR EXISTS(SELECT 1 FROM unnest(close_ids)requested(id) WHERE NOT security.market_close_visible(catalog,requested.id))
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 RETURN payload;
END;$$;
DO $$DECLARE item record;removed integer:=0;BEGIN
 FOR item IN SELECT conname FROM pg_constraint WHERE contype='f'
  AND conrelid='market_data.close_session_curves'::regclass AND confrelid='market_data.close_session_versions'::regclass LOOP
  EXECUTE format('ALTER TABLE market_data.close_session_curves DROP CONSTRAINT %I',item.conname);removed=removed+1;
 END LOOP;
 IF removed<>1 THEN RAISE EXCEPTION 'close_session_source_schema_drift';END IF;
END$$;
ALTER TABLE market_data.close_session_curves ADD FOREIGN KEY(catalog_id,code,version)
 REFERENCES market_data.close_session_versions(catalog_id,code,version);
DO $$DECLARE item record;removed integer:=0;BEGIN
 FOR item IN SELECT conname FROM pg_constraint WHERE contype='f'
  AND conrelid='market_data.close_session_versions'::regclass AND confrelid='market_data.revisions'::regclass LOOP
  EXECUTE format('ALTER TABLE market_data.close_session_versions DROP CONSTRAINT %I',item.conname);removed=removed+1;
 END LOOP;
 IF removed<>1 THEN RAISE EXCEPTION 'close_session_reference_schema_drift';END IF;
END$$;
ALTER TABLE market_data.close_session_versions ADD FOREIGN KEY(catalog_id,definition_revision_id)
 REFERENCES market_data.revisions(catalog_id,id);
CREATE OR REPLACE FUNCTION security.save_close_session(catalog uuid,definition uuid,operation uuid,draft jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;r market_data.revisions;previous market_data.close_session_versions;next_version integer;
 session text;cutoff time;curves text[];catalogue jsonb;calendar_source jsonb;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'WriteMarketDefinitions') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 SELECT * INTO previous FROM market_data.close_session_versions WHERE catalog_id=catalog AND actor_id=c.actor_id AND request_id=operation;
 IF FOUND THEN
  IF previous.request<>draft OR previous.definition_revision_id<>definition THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.close_session_json(catalog,previous.code,previous.version);
 END IF;
 session=draft->>'code';
 IF jsonb_typeof(draft) IS DISTINCT FROM 'object' OR session IS NULL OR session!~'^[a-z][a-z0-9_]{0,63}$'
 OR jsonb_typeof(draft->'name') IS DISTINCT FROM 'string' OR length(btrim(draft->>'name')) NOT BETWEEN 1 AND 80
 OR coalesce(draft->>'cutoff','')!~'^([01][0-9]|2[0-3]):[0-5][0-9]$' OR jsonb_typeof(draft->'curves') IS DISTINCT FROM 'array'
 OR jsonb_array_length(draft->'curves') NOT BETWEEN 1 AND 512
 OR coalesce(draft->>'effectiveFrom','')!~'^\d{4}-\d{2}-\d{2}$' OR coalesce(draft->>'effectiveTo','')!~'^\d{4}-\d{2}-\d{2}$'
 OR(draft->>'effectiveFrom')::date>=(draft->>'effectiveTo')::date THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 SELECT array_agg(DISTINCT x) INTO curves FROM jsonb_array_elements_text(draft->'curves')x;
 catalogue=security.read_request_catalogue(catalog,NULL);
 calendar_source=catalogue#>'{roles,reference:calendars}';
 IF(catalogue#>>'{roles,primary,definitionRevisionId}')::uuid IS DISTINCT FROM definition
 OR calendar_source IS NULL THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 SELECT * INTO r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition) AND state='sealed';
 IF NOT FOUND OR cardinality(curves)<>jsonb_array_length(draft->'curves')
 OR EXISTS(SELECT 1 FROM unnest(curves)k WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(catalogue->'curves')m WHERE m->>'code'=k AND m->>'kind'='curve'))
 OR NOT EXISTS(SELECT 1 FROM ref_data.calendars cal WHERE(cal.catalog_id,cal.revision_id,cal.code)=((calendar_source->>'referenceCatalogId')::uuid,(calendar_source->>'referenceRevisionId')::uuid,draft->>'calendarCode'))
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM market_data.close_sessions s WHERE(s.catalog_id,s.code)=(catalog,session) AND s.name<>draft->>'name')
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 INSERT INTO market_data.close_sessions VALUES(catalog,session,draft->>'name') ON CONFLICT DO NOTHING;
 SELECT coalesce(max(version),0)+1 INTO next_version FROM market_data.close_session_versions WHERE(catalog_id,code)=(catalog,session);
 INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,reference_catalog_id,reference_revision_id,
  calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request)
 VALUES(catalog,session,next_version,'draft',1,definition,(calendar_source->>'referenceCatalogId')::uuid,(calendar_source->>'referenceRevisionId')::uuid,draft->>'calendarCode',(draft->>'cutoff')::time,
  (draft->>'effectiveFrom')::date,(draft->>'effectiveTo')::date,c.actor_id,operation,draft);
 INSERT INTO market_data.close_session_curves SELECT catalog,session,next_version,(m->>'definitionRevisionId')::uuid,m->>'code'
 FROM jsonb_array_elements(catalogue->'curves')m WHERE m->>'code'=ANY(curves);
 IF NOT security.market_access(catalog,'WriteMarketDefinitions') THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN security.close_session_json(catalog,session,next_version);
END;$$;

CREATE FUNCTION security.require_close_source_parts() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM market_data.market_close_sources s WHERE(s.catalog_id,s.close_id)=(NEW.catalog_id,NEW.close_id)
  AND NOT EXISTS(SELECT 1 FROM market_data.market_close_pins p WHERE(p.catalog_id,p.close_id,p.definition_revision_id)=(s.catalog_id,s.close_id,s.definition_revision_id))
  AND NOT EXISTS(SELECT 1 FROM market_data.market_close_members m WHERE(m.catalog_id,m.close_id,m.definition_revision_id)=(s.catalog_id,s.close_id,s.definition_revision_id)))
 OR(SELECT count(*) FROM market_data.market_close_sources s WHERE(s.catalog_id,s.close_id)=(NEW.catalog_id,NEW.close_id))>256
 THEN RAISE EXCEPTION 'pilot_close_inputs' USING ERRCODE='P0001';END IF;
 RETURN NULL;
END;$$;
CREATE CONSTRAINT TRIGGER close_source_parts AFTER INSERT ON market_data.market_close_sources
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION security.require_close_source_parts();
CREATE OR REPLACE FUNCTION security.register_market_close(catalog uuid,definition uuid,operation uuid,close_name text,session text,business_date date,
 frame_at timestamptz,pins jsonb,members jsonb) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;r market_data.revisions;v market_data.close_session_versions;previous market_data.market_closes;
 payload jsonb;identity uuid;ordinal integer;close_at timestamptz;command market_data.market_close_commands;missing text;extra text;
BEGIN
 IF NOT security.current_write_context() OR NOT security.market_access(catalog,'PublishMarketData') THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.can_record_close(catalog,session) THEN RAISE EXCEPTION 'close_grant' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO r FROM market_data.revisions WHERE(catalog_id,id)=(catalog,definition);
 IF NOT security.market_fact_visible(catalog,definition,NULL) OR NOT security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id)
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR close_name IS NULL OR length(btrim(close_name)) NOT BETWEEN 1 AND 80
 OR frame_at IS NULL OR NOT isfinite(frame_at) OR jsonb_typeof(pins) IS DISTINCT FROM 'array'
 OR jsonb_array_length(pins) NOT BETWEEN 1 AND 20000 OR jsonb_typeof(members) IS DISTINCT FROM 'array' OR jsonb_array_length(members) NOT BETWEEN 1 AND 20000
 THEN RAISE EXCEPTION 'pilot_close_input' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(pins)x WHERE jsonb_typeof(x)<>'object'
  OR(SELECT count(*) FROM jsonb_object_keys(x))<>4 OR NOT x ?& ARRAY['definitionRevisionId','seriesId','observationId','inputUse'])
 OR EXISTS(SELECT 1 FROM jsonb_array_elements(members)x WHERE jsonb_typeof(x)<>'object'
  OR(SELECT count(*) FROM jsonb_object_keys(x))<>5 OR NOT x ?& ARRAY['definitionRevisionId','curve','contractId','ordinal','scope'])
 THEN RAISE EXCEPTION 'pilot_close_inputs' USING ERRCODE='P0001';END IF;
 SELECT jsonb_agg(to_jsonb(x) ORDER BY x."definitionRevisionId",x."seriesId",x."inputUse" COLLATE "C") INTO pins
  FROM jsonb_to_recordset(pins)x("definitionRevisionId" uuid,"seriesId" uuid,"observationId" uuid,"inputUse" text);
 SELECT jsonb_agg(to_jsonb(x) ORDER BY x."definitionRevisionId",x.curve COLLATE "C",x.ordinal) INTO members
  FROM jsonb_to_recordset(members)x("definitionRevisionId" uuid,curve text,"contractId" uuid,ordinal integer,scope text);
 payload=jsonb_build_object('definition',definition,'name',close_name,'session',session,'businessDate',business_date,'at',frame_at,'pins',pins,'members',members);
 SELECT * INTO command FROM market_data.market_close_commands x WHERE(x.catalog_id,x.actor_id,x.request_id)=(catalog,c.actor_id,operation);
 IF FOUND THEN
  IF command.payload<>payload THEN RAISE EXCEPTION 'pilot_close_conflict' USING ERRCODE='P0001';END IF;
  IF NOT security.market_close_visible(catalog,command.close_id) THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
  RETURN command.close_id;
 END IF;
 -- Reaffirming the current fact has no new financial publication. Record the
 -- command outcome separately so its retries retain their first result.
 SELECT * INTO previous FROM market_data.market_closes x WHERE(x.catalog_id,x.session_code,x.business_date)=(register_market_close.catalog,register_market_close.session,register_market_close.business_date)
 ORDER BY x.publication_order DESC LIMIT 1;
 IF FOUND AND previous.request=payload THEN
  IF NOT security.market_close_visible(catalog,previous.id) THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
  INSERT INTO market_data.market_close_commands VALUES(catalog,c.actor_id,operation,payload,previous.id);
  RETURN previous.id;
 END IF;
 SELECT * INTO v FROM market_data.close_session_versions s
  WHERE(s.catalog_id,s.code,s.version)=(catalog,session,security.close_session_version_on(catalog,session,business_date));
 IF NOT FOUND OR v.definition_revision_id<>definition THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 close_at=security.close_session_instant(catalog,session,v.version,business_date);
 IF frame_at<>close_at OR close_at>clock_timestamp() THEN RAISE EXCEPTION 'close_session_instant' USING ERRCODE='P0001';END IF;
 -- Root membership is exact to the signed session version. Retained inputs
 -- are declared separately and cannot satisfy or expand that root set.
 SELECT string_agg(k.curve_code,', ' ORDER BY k.curve_code COLLATE "C") INTO missing
 FROM market_data.close_session_curves k WHERE(k.catalog_id,k.code,k.version)=(catalog,session,v.version)
 AND NOT EXISTS(SELECT 1 FROM jsonb_to_recordset(members)x("definitionRevisionId" uuid,curve text,scope text)
  WHERE(x."definitionRevisionId",x.curve,x.scope)=(k.definition_revision_id,k.curve_code,'root'));
 IF missing IS NOT NULL THEN RAISE EXCEPTION 'close_incomplete: missing curves %',missing USING ERRCODE='P0001';END IF;
 SELECT string_agg(DISTINCT x.curve,', ' ORDER BY x.curve) INTO extra
 FROM jsonb_to_recordset(members)x("definitionRevisionId" uuid,curve text,scope text)
 WHERE x.scope IS NULL OR x.scope NOT IN('root','retained') OR(x.scope='root') IS DISTINCT FROM EXISTS(
   SELECT 1 FROM market_data.close_session_curves k WHERE(k.catalog_id,k.code,k.version,k.definition_revision_id,k.curve_code)=
    (catalog,session,v.version,x."definitionRevisionId",x.curve));
 IF extra IS NOT NULL THEN RAISE EXCEPTION 'close_outside_session: curves %',extra USING ERRCODE='P0001';END IF;
 IF r.state IS DISTINCT FROM 'sealed' OR NOT EXISTS(SELECT 1 FROM market_data.revision_periods p WHERE(p.catalog_id,p.revision_id)=(catalog,definition)
 AND p.effective_from<=business_date AND business_date<p.effective_to)
 OR EXISTS(SELECT 1 FROM jsonb_to_recordset(pins) x("definitionRevisionId" uuid,"seriesId" uuid,"observationId" uuid,"inputUse" text)
 LEFT JOIN market_data.observations o ON(o.catalog_id,o.id,o.definition_revision_id,o.series_id)=(catalog,x."observationId",x."definitionRevisionId",x."seriesId")
 WHERE o.id IS NULL OR o.observed_at>close_at OR NOT security.market_fact_visible(catalog,x."definitionRevisionId",o.id)
 OR x."inputUse" IS NULL OR x."inputUse" NOT IN('variable','frozen') OR(x."inputUse"='frozen')<>(o.role IN('final_contract_settlement','index_fixing'))
 OR(x."inputUse"='frozen' AND o.publication_state<>'final'))
 THEN RAISE EXCEPTION 'pilot_close_inputs' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM(
  SELECT x."definitionRevisionId" id FROM jsonb_to_recordset(pins)x("definitionRevisionId" uuid)
  UNION SELECT x."definitionRevisionId" FROM jsonb_to_recordset(members)x("definitionRevisionId" uuid))sources
  LEFT JOIN market_data.revisions selected ON(selected.catalog_id,selected.id)=(catalog,sources.id)
  WHERE selected.state IS DISTINCT FROM 'sealed' OR security.market_fact_visible(catalog,sources.id,NULL) IS NOT TRUE
  OR security.reference_version_visible(selected.reference_catalog_id,selected.reference_revision_id) IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM market_data.revision_periods p WHERE(p.catalog_id,p.revision_id)=(catalog,sources.id)
   AND p.effective_from<=business_date AND business_date<p.effective_to))
 THEN RAISE EXCEPTION 'pilot_close_inputs' USING ERRCODE='P0001';END IF;
 identity=gen_random_uuid();SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM market_data.market_closes WHERE catalog_id=catalog;
 INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,name,at,kind,actor_id,request_id,request,
  publication_order,publication_xid,session_code,session_version,business_date)
 VALUES(catalog,identity,definition,r.reference_catalog_id,r.reference_revision_id,close_name,close_at,'retained_input',c.actor_id,operation,payload,
  ordinal,pg_current_xact_id(),session,v.version,business_date);
 INSERT INTO market_data.market_close_sources
 SELECT catalog,identity,selected.id,selected.reference_catalog_id,selected.reference_revision_id FROM market_data.revisions selected
 WHERE selected.catalog_id=catalog AND selected.id<>definition AND selected.id IN(
  SELECT x."definitionRevisionId" FROM jsonb_to_recordset(pins)x("definitionRevisionId" uuid)
  UNION SELECT x."definitionRevisionId" FROM jsonb_to_recordset(members)x("definitionRevisionId" uuid));
 INSERT INTO market_data.market_close_pins SELECT catalog,identity,x."definitionRevisionId",x."seriesId",x."observationId",x."inputUse"
  FROM jsonb_to_recordset(pins)x("definitionRevisionId" uuid,"seriesId" uuid,"observationId" uuid,"inputUse" text);
 INSERT INTO market_data.market_close_members SELECT catalog,identity,x."definitionRevisionId",x.curve,x."contractId",x.ordinal
  FROM jsonb_to_recordset(members)x("definitionRevisionId" uuid,curve text,"contractId" uuid,ordinal integer);
 IF NOT security.market_close_visible(catalog,identity) THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 IF NOT security.market_access(catalog,'PublishMarketData') OR NOT security.can_record_close(catalog,session) THEN RAISE EXCEPTION 'close_grant' USING ERRCODE='42501';END IF;
 RETURN identity;
END;$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['security.insert_close_primary_source()'::regprocedure,'security.insert_close_command()'::regprocedure,'security.guard_close_source()'::regprocedure,'security.require_close_source_parts()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
COMMIT;
