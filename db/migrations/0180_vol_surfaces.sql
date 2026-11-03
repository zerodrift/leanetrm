-- Fitted volatility surfaces: immutable revisions of one option product's surface for one settlement business date
-- and one vol model, published under the trading publication and knowledge protocol. A revision is recorded once per
-- request and once per set of inputs, its valuation instant and pinned observations (D238); a read selects the latest
-- revision known at the request's cutoff.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
-- The business time is the settlement business date. Its effective instant, for security.trading_visible, is the
-- start of that UTC date: the date convention of trading contexts, whose business date is the UTC date of their
-- trading as-of. A revision is therefore visible from contexts on its business date onward.
CREATE TABLE trading.vol_surface_revisions(
 catalog_id uuid NOT NULL,id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,product_code ref_data.code NOT NULL,
 effective_on date NOT NULL CHECK(effective_on BETWEEN DATE '1901-01-01' AND DATE '2199-12-31'),
 model_id text NOT NULL CHECK(model_id='smile_quadratic_log_moneyness_v1'),
 solver_policy text NOT NULL CHECK(solver_policy='american_futures_option_crr_v1'),
 frame_id uuid NOT NULL,valuation_at timestamptz NOT NULL CHECK(isfinite(valuation_at)),publication_order integer NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 content jsonb NOT NULL CHECK(jsonb_typeof(content)='object' AND pg_column_size(content)<=8000000),
 content_sha256 text NOT NULL CHECK(content_sha256 ~ '^[0-9a-f]{64}$'),
 PRIMARY KEY(catalog_id,id),
 UNIQUE(catalog_id,reference_catalog_id,reference_revision_id,product_code,effective_on,model_id,publication_order),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code) REFERENCES ref_data.futures_option_products(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- Every observation revision a revision's calibration consumed: each series' option and underlying settlements and the
-- discount-curve points behind its discount factor to expiry.
CREATE TABLE trading.vol_surface_inputs(
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,observation_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,revision_id,observation_id),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES trading.vol_surface_revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,observation_id) REFERENCES market_data.observations(catalog_id,id)
);
-- Each request's first outcome, for its actor. The revision fixes the request's key, inputs and content; the frame is
-- the request's own, since an identical refit from another frame at the same valuation instant returns the earlier revision.
CREATE TABLE trading.vol_surface_commands(
 catalog_id uuid NOT NULL,request_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),frame_id uuid NOT NULL,
 revision_id uuid NOT NULL,outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,request_id),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES trading.vol_surface_revisions(catalog_id,id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['vol_surface_revisions','vol_surface_inputs','vol_surface_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
 CREATE POLICY current_authority ON trading.vol_surface_revisions AS RESTRICTIVE FOR SELECT TO openetrm_runtime
  USING(security.market_access(catalog_id,'ReadMarketData') AND security.reference_access(reference_catalog_id,'ReadMarketData'));
 CREATE POLICY history_scope ON trading.vol_surface_revisions AS RESTRICTIVE FOR SELECT TO openetrm_runtime
  USING(security.trading_visible(catalog_id,publication_order,effective_on::timestamp AT TIME ZONE 'UTC'));
 CREATE POLICY history_scope ON trading.vol_surface_inputs AS RESTRICTIVE FOR SELECT TO openetrm_runtime
  USING(EXISTS(SELECT 1 FROM trading.vol_surface_revisions r WHERE(r.catalog_id,r.id)=(vol_surface_inputs.catalog_id,vol_surface_inputs.revision_id)));
 CREATE POLICY command_private ON trading.vol_surface_commands AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);
END$$;

-- The exact option product, business date and vol model of a surface key, or a structured error. The product is
-- {referenceCatalogId, referenceRevisionId, code}: one exact product version.
CREATE FUNCTION trading.vol_surface_key(product jsonb,business_date date,model text) RETURNS ref_data.futures_option_products
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE p ref_data.futures_option_products;uuid_text text='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$';
BEGIN
 IF jsonb_typeof(product) IS DISTINCT FROM 'object' OR NOT product?&ARRAY['referenceCatalogId','referenceRevisionId','code']
 OR(SELECT count(*) FROM jsonb_object_keys(product))<>3 OR jsonb_typeof(product->'code') IS DISTINCT FROM 'string'
 OR jsonb_typeof(product->'referenceCatalogId') IS DISTINCT FROM 'string' OR product->>'referenceCatalogId' !~ uuid_text
 OR jsonb_typeof(product->'referenceRevisionId') IS DISTINCT FROM 'string' OR product->>'referenceRevisionId' !~ uuid_text
 THEN RAISE EXCEPTION 'vol_surface_product' USING ERRCODE='P0001';END IF;
 SELECT * INTO p FROM ref_data.futures_option_products
  WHERE(catalog_id,revision_id,code)=((product->>'referenceCatalogId')::uuid,(product->>'referenceRevisionId')::uuid,product->>'code');
 IF NOT FOUND THEN RAISE EXCEPTION 'vol_surface_product' USING ERRCODE='P0001';END IF;
 IF business_date IS NULL OR business_date NOT BETWEEN DATE '1901-01-01' AND DATE '2199-12-31' THEN RAISE EXCEPTION 'vol_surface_input' USING ERRCODE='P0001';END IF;
 IF model IS DISTINCT FROM 'smile_quadratic_log_moneyness_v1' THEN RAISE EXCEPTION 'vol_model_unknown' USING ERRCODE='P0001';END IF;
 RETURN p;
END$$;

-- Records a fitted surface for an actor and request (D238). It grants no authority of its own: no login or runtime
-- role may execute it. The risk worker's finish path, after rechecking the job actor's current authority, calls it in
-- the transaction that completes the job.
-- - A known request returns its first outcome; the same request with a different payload is refused.
-- - Under the trading publication lock, which serializes every recording of the key, the key's latest revision is
--   read. Its inputs are its valuation instant, from which time to expiry runs, and its pinned observations. If both
--   are the same and so is the content, it is returned and nothing new is published; the same inputs with other
--   content are refused as nondeterministic. The digest is the worker's SHA-256 of its canonical serialization, not of
--   the stored jsonb.
-- - Otherwise the surface is published as a new revision under the next publication order. Its knowledge time is
--   the publication's commit, certified into trading.knowledge_receipts by a later context capture, so a newly
--   published revision's knownFrom is null.
CREATE FUNCTION trading.record_vol_surface_revision(catalog uuid,actor uuid,request uuid,product jsonb,business_date date,model text,policy text,
 frame uuid,valuation_at timestamptz,inputs uuid[],surface jsonb,digest text) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p ref_data.futures_option_products;pinned uuid[];prior trading.vol_surface_revisions;command trading.vol_surface_commands;
 publication integer;revision uuid=gen_random_uuid();outcome jsonb;
BEGIN
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001';END IF;
 p=trading.vol_surface_key(product,business_date,model);
 SELECT array_agg(o ORDER BY o) INTO pinned FROM unnest(inputs) o;
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000'::uuid OR frame IS NULL
 OR record_vol_surface_revision.valuation_at IS NULL OR NOT isfinite(record_vol_surface_revision.valuation_at)
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
  IF(command.actor_id,command.frame_id)<>(actor,frame)
  OR(prior.reference_catalog_id,prior.reference_revision_id,prior.product_code,prior.effective_on,prior.model_id,prior.solver_policy,prior.valuation_at,prior.content_sha256)
   <>(p.catalog_id,p.revision_id,p.code,business_date,model,policy,record_vol_surface_revision.valuation_at,digest)
  OR prior.content<>surface
  OR(SELECT array_agg(i.observation_id ORDER BY i.observation_id) FROM trading.vol_surface_inputs i WHERE(i.catalog_id,i.revision_id)=(catalog,prior.id))<>pinned
  THEN RAISE EXCEPTION 'vol_surface_input' USING ERRCODE='P0001';END IF;
  RETURN command.outcome;
 END IF;
 SELECT * INTO prior FROM trading.vol_surface_revisions r
  WHERE(r.catalog_id,r.reference_catalog_id,r.reference_revision_id,r.product_code,r.effective_on,r.model_id)=(catalog,p.catalog_id,p.revision_id,p.code,business_date,model)
  ORDER BY r.publication_order DESC LIMIT 1;
 IF FOUND AND prior.valuation_at=record_vol_surface_revision.valuation_at
 AND(SELECT array_agg(i.observation_id ORDER BY i.observation_id) FROM trading.vol_surface_inputs i WHERE(i.catalog_id,i.revision_id)=(catalog,prior.id))=pinned THEN
  IF prior.content_sha256<>digest OR prior.content<>surface THEN RAISE EXCEPTION 'vol_surface_nondeterministic' USING ERRCODE='P0001';END IF;
  outcome=jsonb_build_object('revision',prior.id,'publicationOrder',prior.publication_order,'created',false,
   'knownFrom',(SELECT k.known_from FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,prior.publication_order)));
  revision=prior.id;
 ELSE
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.vol_surface_revisions VALUES(catalog,revision,p.catalog_id,p.revision_id,p.code,business_date,model,policy,frame,
   record_vol_surface_revision.valuation_at,publication,actor,request,surface,digest);
  INSERT INTO trading.vol_surface_inputs SELECT catalog,revision,o FROM unnest(pinned) o;
  outcome=jsonb_build_object('revision',revision,'publicationOrder',publication,'created',true,'knownFrom',NULL);
 END IF;
 INSERT INTO trading.vol_surface_commands VALUES(catalog,request,actor,frame,revision,outcome);
 RETURN outcome;
END$$;

-- The latest revision of a surface key known at the request's bound trading context, or null when none is. The
-- business date must be the context's own; a product whose reference data the reader cannot read is refused exactly
-- as an unknown one.
CREATE FUNCTION security.read_vol_surface(catalog uuid,product jsonb,business_date date,model text) RETURNS jsonb
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
 IF business_date<>context_date THEN RAISE EXCEPTION 'vol_surface_context' USING ERRCODE='P0001';END IF;
 SELECT * INTO head FROM trading.vol_surface_revisions r
  WHERE(r.catalog_id,r.reference_catalog_id,r.reference_revision_id,r.product_code,r.effective_on,r.model_id)=(catalog,p.catalog_id,p.revision_id,p.code,business_date,model)
  AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_on::timestamp AT TIME ZONE 'UTC')
  ORDER BY r.publication_order DESC LIMIT 1;
 IF FOUND THEN
  result=jsonb_build_object('revision',head.id,'publicationOrder',head.publication_order,
   'knownFrom',(SELECT k.known_from FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,head.publication_order)),
   'actor',head.actor_id,'requestId',head.request_id,
   'product',jsonb_build_object('referenceCatalogId',head.reference_catalog_id,'referenceRevisionId',head.reference_revision_id,'code',head.product_code),
   'effectiveOn',head.effective_on,'model',head.model_id,'solverPolicy',head.solver_policy,'frameId',head.frame_id,'valuationAt',head.valuation_at,
   'inputs',(SELECT jsonb_agg(i.observation_id ORDER BY i.observation_id) FROM trading.vol_surface_inputs i WHERE(i.catalog_id,i.revision_id)=(catalog,head.id)),
   'content',head.content,'contentSha256',head.content_sha256);
 END IF;
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(p.catalog_id,'ReadMarketData')
 THEN RAISE EXCEPTION 'Volatility surface read authority expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
DO $$DECLARE f text;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.vol_surface_key(jsonb,date,text)','trading.record_vol_surface_revision(uuid,uuid,uuid,jsonb,date,text,text,uuid,timestamptz,uuid[],jsonb,text)',
  'security.read_vol_surface(uuid,jsonb,date,text)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.read_vol_surface(uuid,jsonb,date,text) TO openetrm_runtime;
COMMIT;
