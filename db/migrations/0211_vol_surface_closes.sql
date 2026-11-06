-- A volatility surface belongs to the exact close it was fitted at (D354). A frame identity is session-scoped, so the stored
-- key is the recorded close (which names its session and business day) or, for a working snapshot, none, with the valuation
-- instant in both cases. A read, the fit-job status and the recording's no-op test all name that close and instant, and a
-- read or fit-job status also the frame's pins: a surface fitted at one session's close is not shown at another session's
-- close on the same business date, nor at the close that replaced it, nor at a working snapshot of another instant or of
-- other pins at the same instant. The library read lists the surfaces visible at the bound context
-- with the close each belongs to, so the page can offer them instead of being searched product by product.
-- Current consumer: the Vol surfaces page.
-- No stored revision is attributed to a close after the fact (no backwards compatibility): the migration refuses to run while
-- any revision exists. Deployments clear and reseed that data before the schema release, then refit.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM trading.vol_surface_revisions) THEN RAISE EXCEPTION 'vol_surface_close_upgrade: stored surfaces must be cleared and refitted';END IF;
END$$;

ALTER TABLE trading.vol_surface_revisions ADD COLUMN close_id uuid,
 ADD FOREIGN KEY(catalog_id,close_id) REFERENCES market_data.market_closes(catalog_id,id);
CREATE INDEX vol_surface_revisions_location ON trading.vol_surface_revisions(catalog_id,product_code,effective_on,valuation_at,close_id);

-- Records a fitted surface for an actor and request (D238). It grants no authority of its own: no login or runtime
-- role may execute it. The risk worker's finish path, after rechecking the job actor's current authority, calls it in
-- the transaction that completes the job.
-- - The surface is located by its recorded close, which must be a close at the valuation instant whose pins include every
--   input, or by none for a working snapshot. The frame is the request's session-scoped identity, kept as provenance.
-- - A known request returns its first outcome; the same request with a different payload is refused.
-- - Under the trading publication lock, which serializes every recording of the key, the latest revision of the key at the
--   same close and valuation instant is read. Its inputs are its valuation instant, from which time to expiry runs, and its pinned observations. If
--   both are the same and so is the content, it is returned and nothing new is published; the same inputs with other content
--   are refused as nondeterministic. The digest is the worker's SHA-256 of its canonical serialization, not of the stored jsonb.
-- - Otherwise the surface is published as a new revision under the next publication order. Its knowledge time is
--   the publication's commit, certified into trading.knowledge_receipts by a later context capture, so a newly
--   published revision's knownFrom is null.
CREATE FUNCTION trading.record_vol_surface_revision(catalog uuid,actor uuid,request uuid,product jsonb,business_date date,model text,policy text,
 frame uuid,close uuid,valuation_at timestamptz,inputs uuid[],surface jsonb,digest text) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p ref_data.futures_option_products;pinned uuid[];prior trading.vol_surface_revisions;command trading.vol_surface_commands;
 publication integer;revision uuid=gen_random_uuid();outcome jsonb;
BEGIN
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001';END IF;
 p=trading.vol_surface_key(product,business_date,model);
 SELECT array_agg(o ORDER BY o) INTO pinned FROM unnest(inputs) o;
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000'::uuid OR frame IS NULL
 OR record_vol_surface_revision.valuation_at IS NULL OR NOT isfinite(record_vol_surface_revision.valuation_at)
 OR close IS NOT NULL AND(NOT EXISTS(SELECT 1 FROM market_data.market_closes s WHERE(s.catalog_id,s.id)=(catalog,close) AND s.at=record_vol_surface_revision.valuation_at)
  OR EXISTS(SELECT 1 FROM unnest(inputs) o WHERE NOT EXISTS(SELECT 1 FROM market_data.market_close_pins x WHERE(x.catalog_id,x.close_id,x.observation_id)=(catalog,close,o))))
 OR NOT EXISTS(SELECT 1 FROM market_data.catalogs c WHERE c.id=catalog) OR NOT EXISTS(SELECT 1 FROM security.principals x WHERE x.id=actor)
 OR policy IS DISTINCT FROM 'american_futures_option_crr_v1'
 OR inputs IS NULL OR cardinality(inputs) NOT BETWEEN 1 AND 100000 OR array_position(inputs,NULL) IS NOT NULL
 OR(SELECT count(DISTINCT o) FROM unnest(inputs) o)<>cardinality(inputs)
 OR(SELECT count(*) FROM market_data.observations o WHERE o.catalog_id=catalog AND o.id=ANY(inputs))<>cardinality(inputs)
 OR jsonb_typeof(surface) IS DISTINCT FROM 'object' OR pg_column_size(surface)>8000000 OR digest IS NULL OR digest !~ '^[0-9a-f]{64}$'
 THEN RAISE EXCEPTION 'vol_surface_input' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO command FROM trading.vol_surface_commands c WHERE(c.catalog_id,c.request_id)=(catalog,request);
 IF FOUND THEN
  SELECT * INTO prior FROM trading.vol_surface_revisions r WHERE(r.catalog_id,r.id)=(catalog,command.revision_id);
  IF(command.actor_id,command.frame_id)<>(actor,frame) OR prior.close_id IS DISTINCT FROM close
  OR(prior.reference_catalog_id,prior.reference_revision_id,prior.product_code,prior.effective_on,prior.model_id,prior.solver_policy,prior.valuation_at,prior.content_sha256)
   <>(p.catalog_id,p.revision_id,p.code,business_date,model,policy,record_vol_surface_revision.valuation_at,digest)
  OR prior.content<>surface
  OR(SELECT array_agg(i.observation_id ORDER BY i.observation_id) FROM trading.vol_surface_inputs i WHERE(i.catalog_id,i.revision_id)=(catalog,prior.id))<>pinned
  THEN RAISE EXCEPTION 'vol_surface_input' USING ERRCODE='P0001';END IF;
  RETURN command.outcome;
 END IF;
 SELECT * INTO prior FROM trading.vol_surface_revisions r
  WHERE(r.catalog_id,r.reference_catalog_id,r.reference_revision_id,r.product_code,r.effective_on,r.model_id)=(catalog,p.catalog_id,p.revision_id,p.code,business_date,model)
  AND r.close_id IS NOT DISTINCT FROM close AND r.valuation_at=record_vol_surface_revision.valuation_at
  ORDER BY r.publication_order DESC LIMIT 1;
 IF FOUND
 AND(SELECT array_agg(i.observation_id ORDER BY i.observation_id) FROM trading.vol_surface_inputs i WHERE(i.catalog_id,i.revision_id)=(catalog,prior.id))=pinned THEN
  IF prior.content_sha256<>digest OR prior.content<>surface THEN RAISE EXCEPTION 'vol_surface_nondeterministic' USING ERRCODE='P0001';END IF;
  outcome=jsonb_build_object('revision',prior.id,'publicationOrder',prior.publication_order,'created',false,
   'knownFrom',(SELECT k.known_from FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,prior.publication_order)));
  revision=prior.id;
 ELSE
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.vol_surface_revisions VALUES(catalog,revision,p.catalog_id,p.revision_id,p.code,business_date,model,policy,frame,
   record_vol_surface_revision.valuation_at,publication,actor,request,surface,digest,close);
  INSERT INTO trading.vol_surface_inputs SELECT catalog,revision,o FROM unnest(pinned) o;
  outcome=jsonb_build_object('revision',revision,'publicationOrder',publication,'created',true,'knownFrom',NULL);
 END IF;
 INSERT INTO trading.vol_surface_commands VALUES(catalog,request,actor,frame,revision,outcome);
 RETURN outcome;
END$$;
DROP FUNCTION trading.record_vol_surface_revision(uuid,uuid,uuid,jsonb,date,text,text,uuid,timestamptz,uuid[],jsonb,text);

-- The completing fit records its surface at the close its retained frame was pinned from, or at none for a working snapshot.
DO $upgrade$
DECLARE definition text;before text='job.request->>''model'',job.request->>''solverPolicy'',(job.frame->>''id'')::uuid,job.valuation_at,';
BEGIN
 definition=pg_get_functiondef('risk.finish_vol_surface_fit(uuid,uuid,uuid[],jsonb,text,jsonb)'::regprocedure);
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'vol_surface_close_finish_guard';END IF;
 EXECUTE replace(definition,before,replace(before,'(job.frame->>''id'')::uuid,','(job.frame->>''id'')::uuid,(job.frame->>''originCloseId'')::uuid,'));
END $upgrade$;

-- The latest revision at a close (or, with none, at a working snapshot) and valuation instant whose every input is one of the
-- frame's pins, known at the request's bound trading context, or null when none is. The business date must be the context's
-- own; a product whose reference data the reader cannot read is refused exactly as an unknown one.
CREATE FUNCTION security.read_vol_surface(catalog uuid,product jsonb,business_date date,model text,close uuid,valuation_at timestamptz,pins uuid[]) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p ref_data.futures_option_products;context uuid;context_date date;head trading.vol_surface_revisions;result jsonb;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
 THEN RAISE EXCEPTION 'No volatility surface read access' USING ERRCODE='42501';END IF;
 SELECT t.id,t.effective_on INTO context,context_date FROM security.request_contexts c
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN trading.contexts h ON(h.catalog_id,h.context_id)=(t.catalog_id,t.id)
  WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
  AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 PERFORM security.require_trading_history(catalog,context);
 p=trading.vol_surface_key(product,business_date,model);
 IF NOT security.reference_access(p.catalog_id,'ReadMarketData') OR NOT security.reference_version_visible(p.catalog_id,p.revision_id)
 THEN RAISE EXCEPTION 'vol_surface_product' USING ERRCODE='P0001';END IF;
 IF business_date<>context_date OR read_vol_surface.valuation_at IS NULL OR NOT isfinite(read_vol_surface.valuation_at) OR pins IS NULL
 THEN RAISE EXCEPTION 'vol_surface_context' USING ERRCODE='P0001';END IF;
 SELECT * INTO head FROM trading.vol_surface_revisions r
  WHERE(r.catalog_id,r.reference_catalog_id,r.reference_revision_id,r.product_code,r.effective_on,r.model_id,r.valuation_at)
   =(catalog,p.catalog_id,p.revision_id,p.code,business_date,model,read_vol_surface.valuation_at)
  AND r.close_id IS NOT DISTINCT FROM close
  AND NOT EXISTS(SELECT 1 FROM trading.vol_surface_inputs i WHERE(i.catalog_id,i.revision_id)=(r.catalog_id,r.id) AND i.observation_id<>ALL(pins))
  AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_on::timestamp AT TIME ZONE 'UTC')
  ORDER BY r.publication_order DESC LIMIT 1;
 IF FOUND THEN
  result=jsonb_build_object('revision',head.id,'publicationOrder',head.publication_order,
   'knownFrom',(SELECT k.known_from FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,head.publication_order)),
   'actor',head.actor_id,'requestId',head.request_id,
   'product',jsonb_build_object('referenceCatalogId',head.reference_catalog_id,'referenceRevisionId',head.reference_revision_id,'code',head.product_code),
   'effectiveOn',head.effective_on,'model',head.model_id,'solverPolicy',head.solver_policy,'frameId',head.frame_id,'closeId',head.close_id,
   'valuationAt',head.valuation_at,
   'inputs',(SELECT jsonb_agg(i.observation_id ORDER BY i.observation_id) FROM trading.vol_surface_inputs i WHERE(i.catalog_id,i.revision_id)=(catalog,head.id)),
   'content',head.content,'contentSha256',head.content_sha256);
 END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(p.catalog_id,'ReadMarketData')
 THEN RAISE EXCEPTION 'Volatility surface read authority expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
DROP FUNCTION security.read_vol_surface(uuid,jsonb,date,text);

-- The caller's newest fit job for a key at a close (or a working snapshot), valuation instant and set of pins, with its state
-- and error code, or null. Jobs are operational state: the read shows each as it is now.
CREATE FUNCTION risk.vol_surface_fit_job(catalog uuid,product jsonb,business_date date,model text,close uuid,valuation_at timestamptz,pins uuid[]) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;result jsonb;
BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned()
  AND runtime_login=session_user AND expires_at>clock_timestamp();
 IF c.id IS NULL OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT jsonb_build_object('id',j.id,'state',j.state,'errorCode',j.error_code) INTO result FROM risk.jobs j
  WHERE j.catalog_id=catalog AND(j.tenant_id,j.workspace_id,j.actor_id)=(c.tenant_id,c.workspace_id,c.actor_id)
  AND j.request->>'convention'='vol_surface_fit_v1' AND j.request->'product'=product
  AND j.request->>'businessDate'=to_char(business_date,'YYYY-MM-DD') AND j.request->>'model'=model
  AND j.valuation_at=vol_surface_fit_job.valuation_at AND(j.frame->>'originCloseId')::uuid IS NOT DISTINCT FROM close
  AND(SELECT array_agg(x::uuid ORDER BY x::uuid) FROM jsonb_array_elements_text(j.frame->'observationIds') x)=(SELECT array_agg(p ORDER BY p) FROM unnest(pins) p)
  ORDER BY j.created_at DESC,j.id DESC LIMIT 1;
 RETURN result;
END$$;
DROP FUNCTION risk.vol_surface_fit_job(uuid,jsonb,date,text);

-- The surface library at the bound trading context: for each product of one reference revision, each close (or working
-- snapshot) and valuation instant it was fitted at, the latest revision known at the context, on or before the context's
-- business date, newest business date first. Each names its close (session, business day and whether it is still in force) or,
-- for a working snapshot, none. `here` says whether it is the surface a read at the context's own close, instant and pins
-- returns; those are listed first. A surface whose close the reader cannot see is left out, as the close itself is. A product
-- code narrows it to that product. Paged by `skip` and `take`; `hasMore` says whether more follow.
CREATE FUNCTION security.vol_surface_library(catalog uuid,reference_catalog uuid,reference_revision uuid,product text,model text,
 close uuid,valuation_at timestamptz,pins uuid[],skip integer,take integer) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context uuid;context_date date;page jsonb;
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
 THEN RAISE EXCEPTION 'No volatility surface read access' USING ERRCODE='42501';END IF;
 SELECT t.id,t.effective_on INTO context,context_date FROM security.request_contexts c
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN trading.contexts h ON(h.catalog_id,h.context_id)=(t.catalog_id,t.id)
  WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
  AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 PERFORM security.require_trading_history(catalog,context);
 IF model IS DISTINCT FROM 'smile_quadratic_log_moneyness_v1' THEN RAISE EXCEPTION 'vol_model_unknown' USING ERRCODE='P0001';END IF;
 IF skip IS NULL OR take IS NULL OR skip NOT BETWEEN 0 AND 100000 OR take NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'vol_surface_input' USING ERRCODE='P0001';END IF;
 IF vol_surface_library.valuation_at IS NULL OR NOT isfinite(vol_surface_library.valuation_at) OR pins IS NULL THEN RAISE EXCEPTION 'vol_surface_context' USING ERRCODE='P0001';END IF;
 IF reference_catalog IS NULL OR reference_revision IS NULL OR NOT security.reference_access(reference_catalog,'ReadMarketData')
  OR NOT security.reference_version_visible(reference_catalog,reference_revision)
 THEN RAISE EXCEPTION 'vol_surface_product' USING ERRCODE='P0001';END IF;
 WITH heads AS(
  SELECT DISTINCT ON(r.product_code,r.close_id,r.valuation_at) r.*,
   r.close_id IS NOT DISTINCT FROM vol_surface_library.close AND r.valuation_at=vol_surface_library.valuation_at
   AND NOT EXISTS(SELECT 1 FROM trading.vol_surface_inputs i WHERE(i.catalog_id,i.revision_id)=(r.catalog_id,r.id) AND i.observation_id<>ALL(pins)) here
  FROM trading.vol_surface_revisions r
  WHERE(r.catalog_id,r.reference_catalog_id,r.reference_revision_id,r.model_id)=(catalog,reference_catalog,reference_revision,model)
   AND(product IS NULL OR r.product_code=product) AND r.effective_on<=context_date
   AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_on::timestamp AT TIME ZONE 'UTC')
  ORDER BY r.product_code,r.close_id,r.valuation_at,here DESC,r.publication_order DESC),
 listed AS(
  SELECT h.*,p.name,s.session_code,s.business_date close_date,
   CASE WHEN s.id IS NOT NULL THEN security.close_in_force(s.catalog_id,s.session_code,s.business_date)=s.id END in_force
  FROM heads h JOIN ref_data.futures_option_products p ON(p.catalog_id,p.revision_id,p.code)=(h.reference_catalog_id,h.reference_revision_id,h.product_code)
  LEFT JOIN market_data.market_closes s ON(s.catalog_id,s.id)=(h.catalog_id,h.close_id)
  WHERE h.close_id IS NULL OR security.market_close_visible(s.catalog_id,s.id)
  ORDER BY h.here DESC,h.effective_on DESC,h.valuation_at DESC,h.product_code,h.close_id NULLS LAST
  OFFSET skip LIMIT take+1)
 SELECT coalesce(jsonb_agg(jsonb_build_object('revision',l.id,'product',l.product_code,'productName',l.name,'closeId',l.close_id,
   'businessDate',l.effective_on,'valuationAt',l.valuation_at,'here',l.here,
   'knownFrom',(SELECT k.known_from FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,l.publication_order)),
   'close',CASE WHEN l.close_id IS NULL THEN NULL ELSE jsonb_build_object('session',l.session_code,'businessDate',l.close_date,'inForce',l.in_force) END,
   'expiries',jsonb_array_length(l.content->'expiries'),
   'fittedExpiries',(SELECT count(*) FROM jsonb_array_elements(l.content->'expiries') e WHERE e->>'status'='fitted'),
   'series',jsonb_array_length(l.content->'series'))
   ORDER BY l.here DESC,l.effective_on DESC,l.valuation_at DESC,l.product_code,l.close_id NULLS LAST),'[]') INTO page FROM listed l;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(reference_catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'Volatility surface read authority expired' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('surfaces',(SELECT coalesce(jsonb_agg(e ORDER BY n),'[]') FROM jsonb_array_elements(page) WITH ORDINALITY x(e,n) WHERE n<=take),
  'hasMore',jsonb_array_length(page)>take);
END$$;

DO $$DECLARE f text;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.record_vol_surface_revision(uuid,uuid,uuid,jsonb,date,text,text,uuid,uuid,timestamptz,uuid[],jsonb,text)',
  'security.read_vol_surface(uuid,jsonb,date,text,uuid,timestamptz,uuid[])','risk.vol_surface_fit_job(uuid,jsonb,date,text,uuid,timestamptz,uuid[])',
  'security.vol_surface_library(uuid,uuid,uuid,text,text,uuid,timestamptz,uuid[],integer,integer)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.read_vol_surface(uuid,jsonb,date,text,uuid,timestamptz,uuid[]),risk.vol_surface_fit_job(uuid,jsonb,date,text,uuid,timestamptz,uuid[]),
 security.vol_surface_library(uuid,uuid,uuid,text,text,uuid,timestamptz,uuid[],integer,integer) TO openetrm_runtime;
COMMIT;
