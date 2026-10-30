-- When a definition's run became known (D265). A run's creation, and each of its parts reaching completed, failed or cancelled, is
-- published by its own transaction: at commit, under the knowledge publication lock, it takes the next publication order and
-- records its transaction id. Capturing a context certifies each publication's commit time as an immutable receipt, as pilot
-- reports are (0057), so a read at a knowledge cutoff sees a run, and the state of each part, only from receipts known by then.
-- Risk jobs keep their behavior; only a definition run's jobs publish, and only their terminal state.
BEGIN;
CREATE TABLE risk.definition_run_publications (
 catalog_id uuid NOT NULL, publication_order integer NOT NULL CHECK(publication_order>=1), publication_xid xid8 NOT NULL,
 run_id uuid NOT NULL REFERENCES risk.definition_runs(run_id), part integer,
 state text CHECK(state IN('completed','failed','cancelled')),
 PRIMARY KEY(catalog_id,publication_order), UNIQUE(catalog_id,publication_order,publication_xid), UNIQUE(run_id,part),
 FOREIGN KEY(run_id,part) REFERENCES risk.definition_run_jobs(run_id,part),
 -- A publication without a part is the run's creation; one with a part is that part's terminal state.
 CHECK((part IS NULL)=(state IS NULL))
);
CREATE UNIQUE INDEX risk_definition_run_created ON risk.definition_run_publications(run_id) WHERE part IS NULL;
CREATE TABLE risk.definition_run_receipts (
 catalog_id uuid NOT NULL, publication_order integer NOT NULL, publication_xid xid8 NOT NULL, known_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,publication_order,publication_xid) REFERENCES risk.definition_run_publications(catalog_id,publication_order,publication_xid)
);

CREATE FUNCTION risk.publish_definition_run(catalog uuid,run uuid,run_part integer,part_state text) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 INSERT INTO risk.definition_run_publications
 SELECT catalog,coalesce(max(publication_order),0)+1,pg_current_xact_id(),run,run_part,part_state FROM risk.definition_run_publications WHERE catalog_id=catalog;
END$$;
CREATE FUNCTION risk.publish_run_created() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN PERFORM risk.publish_definition_run(NEW.catalog_id,NEW.run_id,NULL,NULL);RETURN NULL;END$$;
CREATE FUNCTION risk.publish_part_finished() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r risk.definition_run_jobs;
BEGIN
 SELECT * INTO r FROM risk.definition_run_jobs WHERE job_id=NEW.id;
 IF FOUND THEN PERFORM risk.publish_definition_run(NEW.catalog_id,r.run_id,r.part,NEW.state);END IF;
 RETURN NULL;
END$$;
-- Deferred to commit, so a publishing transaction holds the publication lock only while it commits.
CREATE CONSTRAINT TRIGGER publish_run_created AFTER INSERT ON risk.definition_runs DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION risk.publish_run_created();
CREATE CONSTRAINT TRIGGER publish_part_finished AFTER UPDATE OF state ON risk.jobs DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW WHEN(NEW.state IN('completed','failed','cancelled') AND OLD.state IS DISTINCT FROM NEW.state)
 EXECUTE FUNCTION risk.publish_part_finished();

-- The definition's runs known at a context: each run whose creation is known, its valuation time, its part count and each part's
-- terminal state known by the context's cutoff, with when each became known. Only the definition's owner reads it.
CREATE FUNCTION risk.definition_run_history(catalog uuid,definition uuid,context uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cutoff timestamptz;result jsonb;
BEGIN
 SELECT t.known_at INTO cutoff FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF cutoff IS NULL OR NOT security.market_access(catalog,'ReadMarketData') OR NOT EXISTS(SELECT 1 FROM risk.run_definitions d
  JOIN risk.definition_identity() i ON(i.tenant_id,i.workspace_id,i.principal_id)=(d.tenant_id,d.workspace_id,d.owner_id)
  WHERE(d.catalog_id,d.id)=(catalog,definition)) THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('runId',r.run_id,'version',r.version,'createdKnownFrom',k.known_from,
  'valuationAt',(SELECT j.valuation_at FROM risk.definition_run_jobs p JOIN risk.jobs j ON j.id=p.job_id WHERE p.run_id=r.run_id AND p.part=1),
  'parts',(SELECT count(*) FROM risk.definition_run_jobs p WHERE p.run_id=r.run_id),
  'finished',(SELECT coalesce(jsonb_agg(jsonb_build_object('part',f.part,'state',f.state,'knownFrom',fk.known_from) ORDER BY f.part),'[]')
   FROM risk.definition_run_publications f JOIN risk.definition_run_receipts fk USING(catalog_id,publication_order,publication_xid)
   WHERE f.run_id=r.run_id AND f.part IS NOT NULL AND fk.known_from<=cutoff)) ORDER BY r.run_id),'[]') INTO result
 FROM risk.definition_runs r
 JOIN risk.definition_run_publications c ON c.run_id=r.run_id AND c.part IS NULL
 JOIN risk.definition_run_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(c.catalog_id,c.publication_order,c.publication_xid)
 WHERE(r.catalog_id,r.definition_id)=(catalog,definition) AND k.known_from<=cutoff;
 RETURN result;
END$$;

-- Certify a run's publications whenever a context is captured, as every other kind of knowledge is. A risk worker never certifies.
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO capture_pre_definition_run_context;
REVOKE ALL ON FUNCTION security.capture_pre_definition_run_context(uuid,date,timestamptz) FROM openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row record;last_commit timestamptz;commit_at timestamptz;certified timestamptz;
BEGIN
 IF NOT pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  IF NOT security.market_access(catalog,'ReadMarketData') THEN RAISE EXCEPTION 'No current temporal read authority' USING ERRCODE='42501';END IF;
  PERFORM pg_advisory_xact_lock(730849102);
  SELECT certified_through INTO certified FROM market_data.knowledge_clocks WHERE catalog_id=catalog;
  SELECT known_from INTO last_commit FROM risk.definition_run_receipts WHERE catalog_id=catalog ORDER BY publication_order DESC LIMIT 1;
  FOR row IN SELECT p.* FROM risk.definition_run_publications p LEFT JOIN risk.definition_run_receipts r USING(catalog_id,publication_order)
  WHERE p.catalog_id=catalog AND r.publication_order IS NULL ORDER BY p.publication_order LOOP
   commit_at=security.checked_commit_time(row.publication_xid);
   IF commit_at<last_commit OR commit_at<=certified THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
   INSERT INTO risk.definition_run_receipts VALUES(catalog,row.publication_order,row.publication_xid,commit_at);last_commit=commit_at;
  END LOOP;
 END IF;
 RETURN security.capture_pre_definition_run_context(catalog,effective_date,requested_at);
END$$;

DO $$ DECLARE f text; BEGIN
 FOREACH f IN ARRAY ARRAY['risk.publish_definition_run(uuid,uuid,integer,text)','risk.publish_run_created()','risk.publish_part_finished()',
  'risk.definition_run_history(uuid,uuid,uuid)','security.capture_date_context(uuid,date,timestamptz)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION risk.definition_run_history(uuid,uuid,uuid) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
GRANT SELECT,INSERT ON risk.definition_run_publications,risk.definition_run_receipts TO openetrm_security_owner;
GRANT SELECT ON risk.definition_runs,risk.definition_run_jobs,risk.run_definitions TO openetrm_security_owner;
DO $$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['definition_run_publications','definition_run_receipts'] LOOP
  EXECUTE format('ALTER TABLE risk.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE risk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON risk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END $$;
COMMIT;
