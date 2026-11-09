-- Materialized publication schedules and normalized final releases use existing
-- v1 actual targets and the existing immutable observation/correction store.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION market_data.price_publication_utc_text(value text) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(value ~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?Z$',false);
$$;
CREATE TABLE market_data.price_publication_occurrences (
 catalog_id uuid NOT NULL,id uuid NOT NULL,publication_id uuid NOT NULL,publication_version integer NOT NULL,
 assessment_from ref_data.instant NOT NULL,assessment_to ref_data.instant NOT NULL,assessment_date date NOT NULL,
 expected_release_at ref_data.instant NOT NULL,publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),CHECK(assessment_from<assessment_to),
 FOREIGN KEY(catalog_id,publication_id,publication_version) REFERENCES market_data.price_publication_versions(catalog_id,publication_id,version),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE market_data.price_publication_periods (
 catalog_id uuid NOT NULL,occurrence_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 1 AND 366),
 delivery_from date NOT NULL,delivery_to date NOT NULL,PRIMARY KEY(catalog_id,occurrence_id,ordinal),
 UNIQUE(catalog_id,occurrence_id,delivery_from,delivery_to),CHECK(delivery_from<delivery_to),
 FOREIGN KEY(catalog_id,occurrence_id) REFERENCES market_data.price_publication_occurrences(catalog_id,id)
);
CREATE TABLE market_data.price_publication_releases (
 catalog_id uuid NOT NULL,id uuid NOT NULL,occurrence_id uuid NOT NULL,publication_id uuid NOT NULL,publication_version integer NOT NULL,
 index_code ref_data.code NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,source_code ref_data.code NOT NULL,
 source_event_id text NOT NULL CHECK(length(source_event_id) BETWEEN 1 AND 200 AND btrim(source_event_id)=source_event_id),
 source_revision_id text NOT NULL CHECK(length(source_revision_id) BETWEEN 1 AND 200 AND btrim(source_revision_id)=source_revision_id),
 published_at ref_data.instant NOT NULL,received_at ref_data.instant NOT NULL,value ref_data.exact_number NOT NULL,
 corrects_release_id uuid,publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,source_code,source_event_id,source_revision_id,index_code,publication_id),
 UNIQUE(catalog_id,corrects_release_id),CHECK(published_at<=received_at),CHECK(corrects_release_id IS NULL OR corrects_release_id<>id),
 FOREIGN KEY(catalog_id,occurrence_id) REFERENCES market_data.price_publication_occurrences(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_id,publication_version,index_code) REFERENCES market_data.price_publication_indices(catalog_id,publication_id,version,index_code),
 FOREIGN KEY(catalog_id,publication_id,publication_version,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.price_publication_versions(catalog_id,publication_id,version,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,corrects_release_id) REFERENCES market_data.price_publication_releases(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE market_data.price_publication_fixings (
 catalog_id uuid NOT NULL,release_id uuid NOT NULL,definition_revision_id uuid NOT NULL,index_code ref_data.code NOT NULL,
 delivery_from date NOT NULL,delivery_to date NOT NULL,observation_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,release_id,delivery_from,delivery_to),UNIQUE(catalog_id,observation_id),
 FOREIGN KEY(catalog_id,release_id) REFERENCES market_data.price_publication_releases(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,index_code,delivery_from,delivery_to)
 REFERENCES market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to),
 FOREIGN KEY(catalog_id,observation_id) REFERENCES market_data.observations(catalog_id,id)
);
CREATE INDEX price_publication_release_history ON market_data.price_publication_releases(catalog_id,published_at DESC,received_at DESC,id);
CREATE TABLE market_data.price_publication_event_requests (
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 kind text NOT NULL CHECK(kind IN('occurrence','final')),command jsonb NOT NULL CHECK(pg_column_size(command)<=1048576),
 occurrence_id uuid NOT NULL,release_id uuid,PRIMARY KEY(catalog_id,actor_id,request_id),
 CHECK((kind='final')=(release_id IS NOT NULL)),
 FOREIGN KEY(catalog_id,occurrence_id) REFERENCES market_data.price_publication_occurrences(catalog_id,id),
 FOREIGN KEY(catalog_id,release_id) REFERENCES market_data.price_publication_releases(catalog_id,id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['price_publication_occurrences','price_publication_periods','price_publication_releases','price_publication_fixings','price_publication_event_requests'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY private_storage ON market_data.%I FOR SELECT TO openetrm_runtime USING(false)',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime',t);
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.reject_price_publication_mutation()',t);
 END LOOP;
END$$;
CREATE FUNCTION market_data.price_publication_event_visible(catalog uuid,ordinal integer) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'ReadMarketData') AND(NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
 AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)
 OR EXISTS(SELECT 1 FROM security.request_contexts c JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(c.market_catalog_id,c.market_context_id)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(x.catalog_id,x.context_id)
 JOIN trading.knowledge_receipts k ON k.catalog_id=x.catalog_id AND k.publication_order=ordinal
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
 AND x.catalog_id=catalog AND ordinal<=x.known_through AND k.known_from<=t.known_at));
$$;
CREATE FUNCTION market_data.price_publication_occurrence_json(catalog uuid,occurrence uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('id',o.id,'occurrence',jsonb_build_object('publication',jsonb_build_object('catalogId',o.catalog_id,'id',o.publication_id,'version',o.publication_version),
 'assessmentFrom',to_char(o.assessment_from,'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'assessmentTo',to_char(o.assessment_to,'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
 'assessmentDate',o.assessment_date,'expectedReleaseAt',to_char(o.expected_release_at,'YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
 'periods',(SELECT jsonb_agg(jsonb_build_object('from',p.delivery_from,'to',p.delivery_to) ORDER BY p.ordinal) FROM market_data.price_publication_periods p WHERE(p.catalog_id,p.occurrence_id)=(o.catalog_id,o.id))))
 FROM market_data.price_publication_occurrences o WHERE(o.catalog_id,o.id)=(catalog,occurrence);
$$;
CREATE FUNCTION market_data.read_price_publication_occurrences(catalog uuid) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT coalesce(jsonb_agg(market_data.price_publication_occurrence_json(o.catalog_id,o.id) ORDER BY o.expected_release_at,o.id),'[]')
 FROM market_data.price_publication_occurrences o WHERE o.catalog_id=catalog
 AND security.price_publication_visible(o.catalog_id,o.publication_id,o.publication_version)
 AND market_data.price_publication_event_visible(o.catalog_id,o.publication_order);
$$;
CREATE FUNCTION market_data.save_price_publication_occurrence(catalog uuid,request uuid,occurrence uuid,body jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE actor uuid;pub market_data.price_publication_versions;candidate market_data.price_publication_occurrences;
 prior market_data.price_publication_event_requests;existing market_data.price_publication_occurrences;command jsonb;periods jsonb;ordinal integer;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;PERFORM pg_advisory_xact_lock(730849102);
 actor=security.custom_curve_actor(catalog,'WriteMarketDefinitions');
 IF actor IS NULL OR NOT security.current_write_context() THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000' OR occurrence IS NULL OR occurrence='00000000-0000-0000-0000-000000000000'
 OR body IS NULL OR jsonb_typeof(body)<>'object' OR pg_column_size(body)>1048576 OR(body#>>'{publication,catalogId}')::uuid IS DISTINCT FROM catalog
 THEN RAISE EXCEPTION 'publication_occurrence' USING ERRCODE='22023';END IF;
 SELECT * INTO pub FROM market_data.price_publication_versions WHERE(catalog_id,publication_id,version)=(catalog,(body#>>'{publication,id}')::uuid,(body#>>'{publication,version}')::integer);
 IF pub.publication_id IS NULL OR NOT security.price_publication_visible(catalog,pub.publication_id,pub.version) THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 IF pub.implementation NOT IN('gas_daily_explicit_v1','gas_monthly_explicit_v1') THEN RAISE EXCEPTION 'publication_unsupported' USING ERRCODE='22023';END IF;
 command=jsonb_build_object('id',occurrence,'occurrence',body);
 SELECT * INTO prior FROM market_data.price_publication_event_requests r WHERE(r.catalog_id,r.actor_id,r.request_id)=(catalog,actor,request);
 IF FOUND THEN
  IF prior.kind<>'occurrence' OR prior.command<>command THEN RAISE EXCEPTION 'publication_request_conflict' USING ERRCODE='22023';END IF;
  RETURN market_data.price_publication_occurrence_json(catalog,prior.occurrence_id);
 END IF;
 IF pub.version<>(SELECT max(version) FROM market_data.price_publication_versions WHERE(catalog_id,publication_id)=(catalog,pub.publication_id))
 THEN RAISE EXCEPTION 'publication_version_read_only' USING ERRCODE='40001';END IF;
 IF NOT market_data.price_publication_utc_text(body->>'assessmentFrom') OR NOT market_data.price_publication_utc_text(body->>'assessmentTo') OR NOT market_data.price_publication_utc_text(body->>'expectedReleaseAt')
 OR(body->>'assessmentDate') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR jsonb_typeof(body->'periods') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'periods') NOT BETWEEN 1 AND 366
 THEN RAISE EXCEPTION 'publication_occurrence' USING ERRCODE='22023';END IF;
 candidate.catalog_id=catalog;candidate.id=occurrence;candidate.publication_id=pub.publication_id;candidate.publication_version=pub.version;
 candidate.assessment_from=(body->>'assessmentFrom')::timestamptz;candidate.assessment_to=(body->>'assessmentTo')::timestamptz;
 candidate.assessment_date=(body->>'assessmentDate')::date;candidate.expected_release_at=(body->>'expectedReleaseAt')::timestamptz;
 IF candidate.assessment_from>=candidate.assessment_to THEN RAISE EXCEPTION 'publication_time' USING ERRCODE='22023';END IF;
 periods=body->'periods';
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(periods) p WHERE p->>'from' IS NULL OR p->>'to' IS NULL OR(p->>'from') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
 OR(p->>'to') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR(p->>'from')::date>=(p->>'to')::date)
 OR(pub.implementation='gas_monthly_explicit_v1' AND(jsonb_array_length(periods)<>1 OR extract(day FROM(periods#>>'{0,from}')::date)<>1
 OR(periods#>>'{0,to}')::date<>((periods#>>'{0,from}')::date+interval '1 month')::date))
 OR(pub.implementation='gas_daily_explicit_v1' AND EXISTS(SELECT 1 FROM jsonb_array_elements(periods) WITH ORDINALITY p(v,n)
 WHERE(v->>'to')::date-(v->>'from')::date<>1 OR(n>1 AND(periods->(n::integer-2)->>'to')::date<>(v->>'from')::date)))
 THEN RAISE EXCEPTION 'publication_coverage' USING ERRCODE='22023';END IF;
 SELECT o.* INTO existing FROM market_data.price_publication_occurrences o WHERE(o.catalog_id,o.publication_id,o.publication_version,o.assessment_from,o.assessment_to,o.assessment_date,o.expected_release_at)=
 (catalog,pub.publication_id,pub.version,candidate.assessment_from,candidate.assessment_to,candidate.assessment_date,candidate.expected_release_at)
 AND(market_data.price_publication_occurrence_json(catalog,o.id)#>'{occurrence,periods}')=periods;
 IF FOUND THEN candidate.id=existing.id;
 ELSE
  IF EXISTS(SELECT 1 FROM market_data.price_publication_occurrences o WHERE(o.catalog_id,o.id)=(catalog,occurrence)) THEN RAISE EXCEPTION 'publication_occurrence_conflict' USING ERRCODE='40001';END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,ordinal,pg_current_xact_id());candidate.publication_order=ordinal;
  INSERT INTO market_data.price_publication_occurrences SELECT(candidate).*;
  INSERT INTO market_data.price_publication_periods SELECT catalog,candidate.id,n,(p->>'from')::date,(p->>'to')::date FROM jsonb_array_elements(periods) WITH ORDINALITY x(p,n);
 END IF;
 INSERT INTO market_data.price_publication_event_requests VALUES(catalog,actor,request,'occurrence',command,candidate.id,NULL);
 RETURN market_data.price_publication_occurrence_json(catalog,candidate.id);
END$$;
CREATE FUNCTION market_data.price_publication_release_receipt(catalog uuid,release uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('releaseId',r.id,'occurrenceId',r.occurrence_id,'publication',jsonb_build_object('catalogId',r.catalog_id,'id',r.publication_id,'version',r.publication_version),
 'indexCode',r.index_code,'sourceCode',r.source_code,'sourceEventId',r.source_event_id,'sourceRevisionId',r.source_revision_id,'stage','final',
 'publishedAt',r.published_at,'receivedAt',r.received_at,'value',trim_scale(r.value)::text,'correctsReleaseId',r.corrects_release_id,
 'observationIds',(SELECT jsonb_agg(f.observation_id ORDER BY f.delivery_from) FROM market_data.price_publication_fixings f WHERE(f.catalog_id,f.release_id)=(r.catalog_id,r.id)))
 FROM market_data.price_publication_releases r WHERE(r.catalog_id,r.id)=(catalog,release);
$$;
-- The editor shows at most 200 releases under the same market and knowledge view.
-- This receipt history does not select new pricing inputs into an existing snapshot.
CREATE FUNCTION market_data.read_price_publication_releases(catalog uuid,market_cutoff timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 IF market_cutoff IS NULL OR NOT isfinite(market_cutoff) THEN RAISE EXCEPTION 'publication_time' USING ERRCODE='22023';END IF;
 SELECT coalesce(jsonb_agg(market_data.price_publication_release_receipt(catalog,r.id) ORDER BY r.published_at DESC,r.received_at DESC,r.id),'[]') INTO result
 FROM(SELECT p.id,p.published_at,p.received_at FROM market_data.price_publication_releases p
 WHERE p.catalog_id=catalog AND p.published_at<=market_cutoff
 AND security.price_publication_visible(p.catalog_id,p.publication_id,p.publication_version)
 AND market_data.price_publication_event_visible(p.catalog_id,p.publication_order)
 ORDER BY p.published_at DESC,p.received_at DESC,p.id LIMIT 200)r;
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
CREATE FUNCTION market_data.ingest_price_publication_final(catalog uuid,market_revision uuid,request uuid,body jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE actor uuid;occ market_data.price_publication_occurrences;pub market_data.price_publication_versions;member market_data.price_publication_indices;
 previous market_data.price_publication_releases;existing market_data.price_publication_releases;prior market_data.price_publication_event_requests;
 source ref_data.code;source_event text;source_revision text;price ref_data.exact_number;published timestamptz;received timestamptz;corrects uuid;
 release uuid;ordinal integer;period market_data.price_publication_periods;fixing market_data.index_fixing_observations;observation uuid;old_observation market_data.observations;event_id text;command jsonb;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;PERFORM pg_advisory_xact_lock(730849102);
 actor=security.custom_curve_actor(catalog,'ImportObservations');
 IF actor IS NULL OR NOT security.current_write_context() THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000' OR body IS NULL OR jsonb_typeof(body)<>'object' OR pg_column_size(body)>65536
 THEN RAISE EXCEPTION 'publication_command' USING ERRCODE='22023';END IF;
 SELECT * INTO occ FROM market_data.price_publication_occurrences WHERE(catalog_id,id)=(catalog,(body->>'occurrenceId')::uuid);
 IF occ.id IS NULL OR NOT security.price_publication_visible(catalog,occ.publication_id,occ.publication_version)
 OR NOT market_data.price_publication_event_visible(catalog,occ.publication_order) THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO pub FROM market_data.price_publication_versions WHERE(catalog_id,publication_id,version)=(catalog,occ.publication_id,occ.publication_version);
 SELECT * INTO member FROM market_data.price_publication_indices WHERE(catalog_id,publication_id,version,index_code)=(catalog,pub.publication_id,pub.version,body->>'indexCode');
 IF member.index_code IS NULL OR NOT security.custom_curve_source_visible(catalog,market_revision) OR NOT EXISTS(SELECT 1 FROM market_data.revisions
 WHERE(catalog_id,id,reference_catalog_id,reference_revision_id)=(catalog,market_revision,pub.reference_catalog_id,pub.reference_revision_id) AND state='sealed')
 THEN RAISE EXCEPTION 'publication_index' USING ERRCODE='22023';END IF;
 command=jsonb_build_object('marketRevision',market_revision,'release',body);
 SELECT * INTO prior FROM market_data.price_publication_event_requests r WHERE(r.catalog_id,r.actor_id,r.request_id)=(catalog,actor,request);
 IF FOUND THEN
  IF prior.kind<>'final' OR prior.command<>command THEN RAISE EXCEPTION 'publication_request_conflict' USING ERRCODE='22023';END IF;
  RETURN market_data.price_publication_release_receipt(catalog,prior.release_id);
 END IF;
 IF pub.implementation NOT IN('gas_daily_explicit_v1','gas_monthly_explicit_v1') THEN RAISE EXCEPTION 'publication_unsupported' USING ERRCODE='22023';END IF;
 IF NOT market_data.price_publication_utc_text(body->>'publishedAt') OR(body->>'value') !~ '^-?(0|[1-9][0-9]*)(\.[0-9]{1,18})?$'
 OR body->>'sourceCode' IS NULL OR body->>'sourceEventId' IS NULL OR body->>'sourceRevisionId' IS NULL
 THEN RAISE EXCEPTION 'publication_release' USING ERRCODE='22023';END IF;
 source=body->>'sourceCode';source_event=body->>'sourceEventId';source_revision=body->>'sourceRevisionId';price=(body->>'value')::numeric;
 published=(body->>'publishedAt')::timestamptz;received=clock_timestamp();corrects=(body->>'correctsReleaseId')::uuid;
 IF published>received OR published<occ.assessment_to THEN RAISE EXCEPTION 'publication_time' USING ERRCODE='22023';END IF;
 SELECT * INTO existing FROM market_data.price_publication_releases r WHERE(r.catalog_id,r.source_code,r.source_event_id,r.source_revision_id,r.index_code,r.publication_id)=
 (catalog,source,source_event,source_revision,member.index_code,pub.publication_id);
 IF FOUND THEN
  IF(existing.occurrence_id,existing.publication_version,existing.published_at,existing.value,existing.corrects_release_id) IS DISTINCT FROM(occ.id,pub.version,published,price,corrects)
  OR EXISTS(SELECT 1 FROM market_data.price_publication_fixings f WHERE(f.catalog_id,f.release_id)=(catalog,existing.id) AND f.definition_revision_id<>market_revision)
  THEN RAISE EXCEPTION 'publication_revision_conflict' USING ERRCODE='40001';END IF;
  INSERT INTO market_data.price_publication_event_requests VALUES(catalog,actor,request,'final',command,occ.id,existing.id);
  RETURN market_data.price_publication_release_receipt(catalog,existing.id);
 END IF;
 IF corrects IS NOT NULL THEN
  SELECT * INTO previous FROM market_data.price_publication_releases WHERE(catalog_id,id)=(catalog,corrects);
  IF previous.id IS NULL OR(previous.occurrence_id,previous.publication_id,previous.publication_version,previous.index_code,previous.source_code,previous.source_event_id)
   IS DISTINCT FROM(occ.id,pub.publication_id,pub.version,member.index_code,source,source_event) OR previous.source_revision_id=source_revision OR previous.published_at>published
   OR EXISTS(SELECT 1 FROM market_data.price_publication_releases WHERE catalog_id=catalog AND corrects_release_id=corrects)
  THEN RAISE EXCEPTION 'publication_correction' USING ERRCODE='40001';END IF;
 ELSIF EXISTS(SELECT 1 FROM market_data.price_publication_releases r WHERE(r.catalog_id,r.publication_id,r.index_code,r.source_code,r.source_event_id)=(catalog,pub.publication_id,member.index_code,source,source_event))
 THEN RAISE EXCEPTION 'publication_correction' USING ERRCODE='40001';END IF;
 -- Resolve the full coverage before publishing any observation. All rows commit atomically.
 FOR period IN SELECT * FROM market_data.price_publication_periods WHERE(catalog_id,occurrence_id)=(catalog,occ.id) ORDER BY ordinal LOOP
  SELECT * INTO fixing FROM market_data.index_fixing_observations WHERE(catalog_id,revision_id,index_code,observation_from,observation_to)=
   (catalog,market_revision,member.index_code,period.delivery_from,period.delivery_to);
  IF fixing.series_id IS NULL OR(fixing.reference_catalog_id,fixing.reference_revision_id,fixing.quote_code,fixing.source_code,fixing.implementation)
   IS DISTINCT FROM(pub.reference_catalog_id,pub.reference_revision_id,member.quote_code,source,'published_index_observations_v1')
  THEN RAISE EXCEPTION 'publication_target' USING ERRCODE='22023';END IF;
  IF published<fixing.fixing_at THEN RAISE EXCEPTION 'publication_legacy_boundary' USING ERRCODE='22023';END IF;
 END LOOP;
 SELECT coalesce(max(publication_order),0)+1 INTO ordinal FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,ordinal,pg_current_xact_id());release=gen_random_uuid();
 INSERT INTO market_data.price_publication_releases VALUES(catalog,release,occ.id,pub.publication_id,pub.version,member.index_code,pub.reference_catalog_id,pub.reference_revision_id,
  source,source_event,source_revision,published,received,price,corrects,ordinal);
 FOR period IN SELECT * FROM market_data.price_publication_periods WHERE(catalog_id,occurrence_id)=(catalog,occ.id) ORDER BY ordinal LOOP
  SELECT * INTO STRICT fixing FROM market_data.index_fixing_observations WHERE(catalog_id,revision_id,index_code,observation_from,observation_to)=
   (catalog,market_revision,member.index_code,period.delivery_from,period.delivery_to);
  old_observation=NULL;
  IF corrects IS NOT NULL THEN
   SELECT o.* INTO old_observation FROM market_data.price_publication_fixings f JOIN market_data.observations o ON(o.catalog_id,o.id)=(f.catalog_id,f.observation_id)
   WHERE(f.catalog_id,f.release_id,f.delivery_from,f.delivery_to)=(catalog,corrects,period.delivery_from,period.delivery_to);
   IF old_observation.id IS NULL OR old_observation.definition_revision_id<>market_revision THEN RAISE EXCEPTION 'publication_correction' USING ERRCODE='40001';END IF;
  END IF;
  event_id='price-publication:'||pub.publication_id::text||':'||member.index_code||':'||source_event||':'||period.delivery_from::text||':'||period.delivery_to::text;
  observation=gen_random_uuid();
  INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,
   role,publication_state,observed_at,received_at,value,corrects_id)
  VALUES(catalog,observation,market_revision,fixing.series_id,pub.reference_catalog_id,pub.reference_revision_id,source,event_id,source_revision,'index_fixing','final',
   coalesce(old_observation.observed_at,published),received,price,old_observation.id);
  INSERT INTO market_data.price_publication_fixings VALUES(catalog,release,market_revision,member.index_code,period.delivery_from,period.delivery_to,observation);
 END LOOP;
 IF security.custom_curve_actor(catalog,'ImportObservations') IS DISTINCT FROM actor OR NOT security.price_publication_visible(catalog,pub.publication_id,pub.version)
 THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 INSERT INTO market_data.price_publication_event_requests VALUES(catalog,actor,request,'final',command,occ.id,release);
 RETURN market_data.price_publication_release_receipt(catalog,release);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='market_data'
 AND p.proname IN('price_publication_utc_text','price_publication_event_visible','price_publication_occurrence_json','read_price_publication_occurrences','save_price_publication_occurrence','price_publication_release_receipt','read_price_publication_releases','ingest_price_publication_final') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname IN('read_price_publication_occurrences','save_price_publication_occurrence','read_price_publication_releases','ingest_price_publication_final') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
