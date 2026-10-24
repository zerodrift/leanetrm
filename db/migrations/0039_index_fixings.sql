-- Append-only index fixings share the certified market knowledge stream.
-- Forward observations and all earlier financial contracts remain unchanged.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE market_data.index_fixings (
 catalog_id uuid NOT NULL,id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,index_code ref_data.code NOT NULL,
 quote_code ref_data.code NOT NULL,source_code ref_data.code NOT NULL,
 observation_from date NOT NULL,observation_to date NOT NULL,published_at ref_data.instant NOT NULL,
 publication_state text NOT NULL CHECK(publication_state IN('preliminary','final')),
 revision integer NOT NULL CHECK(revision BETWEEN 1 AND 4096),corrects_id uuid,value ref_data.exact_number NOT NULL,
 source_event_id text NOT NULL CHECK(length(btrim(source_event_id)) BETWEEN 1 AND 200),
 source_revision_id text NOT NULL CHECK(length(btrim(source_revision_id)) BETWEEN 1 AND 200),
 received_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,definition_revision_id),UNIQUE(catalog_id,corrects_id),
 UNIQUE(catalog_id,source_code,source_event_id,source_revision_id),
 UNIQUE(catalog_id,reference_catalog_id,reference_revision_id,index_code,quote_code,source_code,observation_from,observation_to,revision),
 CHECK(observation_from<observation_to AND observation_to-observation_from<=366),
 CHECK((revision=1)=(corrects_id IS NULL)),CHECK(published_at<=received_at),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,corrects_id) REFERENCES market_data.index_fixings(catalog_id,id)
);
ALTER TABLE market_data.knowledge_events ADD COLUMN fixing_id uuid,
 ADD FOREIGN KEY(catalog_id,fixing_id,revision_id) REFERENCES market_data.index_fixings(catalog_id,id,definition_revision_id),
 ADD CHECK(observation_id IS NULL OR fixing_id IS NULL),ADD UNIQUE(catalog_id,fixing_id);
DROP INDEX market_data.one_bundle_publication;
CREATE UNIQUE INDEX one_bundle_publication ON market_data.knowledge_events(catalog_id,revision_id) WHERE observation_id IS NULL AND fixing_id IS NULL;
-- Existing publication writers name columns because this stream now admits a
-- second kind of observation. Neither kind is a proxy for the other.
CREATE OR REPLACE FUNCTION security.record_market_event() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE rev uuid;observation uuid;fixing uuid;ordinal integer;
BEGIN
 IF TG_TABLE_NAME='revisions' THEN
  IF NEW.state<>'sealed' THEN RETURN NEW;END IF;
  rev=NEW.id;
  IF NOT EXISTS(SELECT 1 FROM market_data.revision_periods WHERE catalog_id=NEW.catalog_id AND revision_id=rev) THEN RAISE EXCEPTION 'Explicit definition applicability is required before sealing' USING ERRCODE='23514';END IF;
 ELSIF TG_TABLE_NAME='index_fixings' THEN rev=NEW.definition_revision_id;fixing=NEW.id;
 ELSE rev=NEW.definition_revision_id;observation=NEW.id;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT(SELECT rolsuper FROM pg_roles WHERE rolname=session_user) AND NOT security.market_access(NEW.catalog_id,CASE TG_TABLE_NAME WHEN 'revisions' THEN 'SealMarketDefinitions' ELSE 'ImportObservations' END) THEN RAISE EXCEPTION 'Market publication authority expired during admission' USING ERRCODE='42501';END IF;
 IF current_setting('track_commit_timestamp')<>'on' THEN RAISE EXCEPTION 'knowledge_tracking';END IF;
 SELECT coalesce(max(event_order),0)+1 INTO ordinal FROM market_data.knowledge_events WHERE catalog_id=NEW.catalog_id;
 INSERT INTO market_data.knowledge_events(catalog_id,event_order,revision_id,observation_id,publication_xid,fixing_id)
 VALUES(NEW.catalog_id,ordinal,rev,observation,pg_current_xact_id(),fixing);
 RETURN NEW;
END $$;

CREATE FUNCTION security.index_fixing_visible(catalog uuid,fixing uuid) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access(catalog,'ReadMarketData') AND EXISTS(
  SELECT 1 FROM security.request_contexts c JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  JOIN market_data.knowledge_events e ON e.catalog_id=t.catalog_id AND e.fixing_id=fixing
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  JOIN market_data.index_fixings f ON(f.catalog_id,f.id)=(e.catalog_id,e.fixing_id)
  WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND t.catalog_id=catalog
   AND e.event_order<=m.known_through AND k.known_from<=t.known_at AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to
   AND security.reference_access(f.reference_catalog_id,'ReadMarketData')
 );
$$;

-- Explicit retained-context reads are also used while a working booking owns
-- the publication gate. Reading a manifest does not bind a historical write context.
CREATE FUNCTION security.read_index_fixings_at(catalog uuid,requested_context uuid) RETURNS SETOF market_data.index_fixings LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT EXISTS(SELECT 1 FROM market_data.market_contexts m WHERE(m.catalog_id,m.context_id)=(catalog,requested_context)) THEN RAISE EXCEPTION 'No current fixing/context access' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL AND(c.market_catalog_id,c.market_context_id) IS DISTINCT FROM(catalog,requested_context)) THEN RAISE EXCEPTION 'index_fixing_context';END IF;
 RETURN QUERY SELECT f.* FROM market_data.index_fixings f
 JOIN market_data.knowledge_events e ON(e.catalog_id,e.fixing_id)=(f.catalog_id,f.id)
 JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
 JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(catalog,requested_context)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)
 JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
 WHERE f.catalog_id=catalog AND e.event_order<=m.known_through AND k.known_from<=t.known_at AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to AND security.reference_access(f.reference_catalog_id,'ReadMarketData');
 IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'Fixing read authority expired' USING ERRCODE='42501';END IF;
END $$;

CREATE FUNCTION security.guard_index_fixing() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE previous market_data.index_fixings;q ref_data.quote_specifications;i ref_data.price_index_definitions;u ref_data.quantity_specifications;
BEGIN
 IF TG_OP<>'INSERT' THEN RAISE EXCEPTION 'Index fixings are immutable; append a correction' USING ERRCODE='55000';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT(SELECT rolsuper FROM pg_roles WHERE rolname=session_user) AND(
  NOT security.market_access(NEW.catalog_id,'ImportObservations') OR NOT security.reference_access(NEW.reference_catalog_id,'ReadMarketData') OR
  EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL)) THEN RAISE EXCEPTION 'No working index-fixing import authority' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r WHERE(r.catalog_id,r.id)=(NEW.catalog_id,NEW.definition_revision_id) AND r.state='sealed') THEN RAISE EXCEPTION 'index_fixing_definition';END IF;
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(NEW.reference_catalog_id,NEW.reference_revision_id,NEW.quote_code);
 SELECT * INTO i FROM ref_data.price_index_definitions WHERE(catalog_id,revision_id,code)=(NEW.reference_catalog_id,NEW.reference_revision_id,NEW.index_code);
 SELECT * INTO u FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(q.catalog_id,q.revision_id,q.quantity_code);
 IF q.code IS NULL OR i.code IS NULL OR q.kind<>'outright' OR(i.currency_code,i.commodity_code,i.quote_unit_code,i.quote_multiplier) IS DISTINCT FROM(q.currency_code,u.commodity_code,u.unit_code,q.multiplier)
  OR NEW.value<q.minimum OR NEW.value>q.maximum THEN RAISE EXCEPTION 'index_fixing_definition';END IF;
 IF NEW.corrects_id IS NOT NULL THEN
  SELECT * INTO previous FROM market_data.index_fixings WHERE(catalog_id,id)=(NEW.catalog_id,NEW.corrects_id);
  IF previous.id IS NULL OR
   (NEW.definition_revision_id,NEW.reference_catalog_id,NEW.reference_revision_id,NEW.index_code,NEW.quote_code,NEW.source_code,NEW.observation_from,NEW.observation_to,NEW.source_event_id,NEW.revision)
   IS DISTINCT FROM(previous.definition_revision_id,previous.reference_catalog_id,previous.reference_revision_id,previous.index_code,previous.quote_code,previous.source_code,previous.observation_from,previous.observation_to,previous.source_event_id,previous.revision+1)
   OR NEW.published_at<previous.published_at THEN RAISE EXCEPTION 'index_fixing_revision';END IF;
 END IF;
 NEW.received_at=clock_timestamp();
 RETURN NEW;
END $$;
CREATE TRIGGER guard_index_fixing BEFORE INSERT OR UPDATE OR DELETE ON market_data.index_fixings FOR EACH ROW EXECUTE FUNCTION security.guard_index_fixing();
CREATE TRIGGER record_index_fixing AFTER INSERT ON market_data.index_fixings FOR EACH ROW EXECUTE FUNCTION security.record_market_event();
ALTER TABLE market_data.index_fixings ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.index_fixings FORCE ROW LEVEL SECURITY;
CREATE POLICY scoped_read ON market_data.index_fixings FOR SELECT TO openetrm_runtime USING(security.index_fixing_visible(catalog_id,id));
GRANT SELECT ON market_data.index_fixings TO openetrm_runtime;
GRANT SELECT,INSERT ON market_data.index_fixings TO openetrm_security_owner;
CREATE FUNCTION security.import_index_fixing(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,index_name text,quote_name text,source_name text,
 from_date date,to_date date,published timestamptz,state text,revision_number integer,corrects uuid,amount numeric,source_event text,source_revision text)
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior market_data.index_fixings;new_id uuid;
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.market_access(catalog,'ReadMarketData') OR NOT security.market_access(catalog,'ImportObservations') OR NOT security.reference_access(ref_catalog,'ReadMarketData') OR
  EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN RAISE EXCEPTION 'No working index-fixing import authority' USING ERRCODE='42501';END IF;
 SELECT * INTO prior FROM market_data.index_fixings f WHERE f.catalog_id=catalog AND f.source_code=source_name AND f.source_event_id=source_event AND f.source_revision_id=source_revision;
 IF FOUND THEN
  IF(prior.definition_revision_id,prior.reference_catalog_id,prior.reference_revision_id,prior.index_code,prior.quote_code,prior.source_code,prior.observation_from,prior.observation_to,prior.published_at,prior.publication_state,prior.revision,prior.corrects_id,prior.value)
   IS DISTINCT FROM(definition,ref_catalog,ref_revision,index_name,quote_name,source_name,from_date,to_date,published,state,revision_number,corrects,amount) THEN RAISE EXCEPTION 'index_fixing_conflict';END IF;
  RETURN prior.id;
 END IF;
 IF definition IS NULL OR ref_catalog IS NULL OR ref_revision IS NULL OR index_name IS NULL OR quote_name IS NULL OR source_name IS NULL OR
  from_date IS NULL OR to_date IS NULL OR published IS NULL OR state IS NULL OR revision_number IS NULL OR amount IS NULL OR source_event IS NULL OR source_revision IS NULL THEN RAISE EXCEPTION 'index_fixing_input';END IF;
 new_id=gen_random_uuid();
 INSERT INTO market_data.index_fixings(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,index_code,quote_code,source_code,
  observation_from,observation_to,published_at,publication_state,revision,corrects_id,value,source_event_id,source_revision_id,received_at)
 VALUES(catalog,new_id,definition,ref_catalog,ref_revision,index_name,quote_name,source_name,from_date,to_date,published,state,revision_number,corrects,amount,source_event,source_revision,clock_timestamp());
 RETURN new_id;
END $$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname IN('index_fixing_visible','guard_index_fixing','import_index_fixing','read_index_fixings_at') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION security.index_fixing_visible(uuid,uuid),security.read_index_fixings_at(uuid,uuid),security.import_index_fixing(uuid,uuid,uuid,uuid,text,text,text,date,date,timestamptz,text,integer,uuid,numeric,text,text) TO openetrm_runtime;
COMMIT;
