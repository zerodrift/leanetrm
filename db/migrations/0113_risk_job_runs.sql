-- A queued part of a portfolio risk run made in parts states its run in its retained frame (D221): the run's identity, which part
-- it is and how many parts the run has; and every queued job the books and portfolios its run was asked for. The store keeps every part
-- of a run at one retained context and one close (its frame: every close of a date shares a context), for one scope, with one part
-- count, and each part once; every job read states the run its job is a part of, or none, its scope and how many shocks it asked for.
-- No table changes; retained jobs keep their frames.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE INDEX risk_jobs_run ON risk.jobs(tenant_id,workspace_id,actor_id,(frame#>>'{run,id}')) WHERE frame ? 'run';

CREATE FUNCTION risk.guard_run() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE run jsonb=NEW.frame->'run';
BEGIN
 IF NOT NEW.frame ? 'run' THEN RETURN NEW;END IF;
 IF jsonb_typeof(run) IS DISTINCT FROM 'object'
  OR(SELECT array_agg(key ORDER BY key) FROM jsonb_object_keys(run) key) IS DISTINCT FROM ARRAY['id','part','parts']
  OR jsonb_typeof(run->'id') IS DISTINCT FROM 'string' OR run->>'id'!~'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
  OR jsonb_typeof(run->'part') IS DISTINCT FROM 'number' OR jsonb_typeof(run->'parts') IS DISTINCT FROM 'number'
  OR run->>'part'!~'^[1-9][0-9]{0,2}$' OR run->>'parts'!~'^[1-9][0-9]{0,2}$'
  OR(run->>'part')::integer>(run->>'parts')::integer OR(run->>'parts')::integer>256
 THEN RAISE EXCEPTION 'risk_job_run';END IF;
 -- The parts of one run are admitted one at a time, so that two sent at once are checked against each other.
 PERFORM pg_advisory_xact_lock(hashtextextended(NEW.tenant_id::text||NEW.workspace_id::text||NEW.actor_id::text||(run->>'id'),730849137));
 IF EXISTS(SELECT 1 FROM risk.jobs j WHERE(j.tenant_id,j.workspace_id,j.actor_id)=(NEW.tenant_id,NEW.workspace_id,NEW.actor_id)
  AND j.frame ? 'run' AND j.frame#>>'{run,id}'=run->>'id'
  AND(j.catalog_id<>NEW.catalog_id OR j.context_id<>NEW.context_id OR j.frame->>'id' IS DISTINCT FROM NEW.frame->>'id'
   OR j.frame->'scope' IS DISTINCT FROM NEW.frame->'scope'
   OR j.frame#>'{run,parts}'<>run->'parts' OR j.frame#>'{run,part}'=run->'part'))
 THEN RAISE EXCEPTION 'risk_job_run';END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION risk.guard_run() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.guard_run() FROM PUBLIC;
CREATE TRIGGER job_run BEFORE INSERT ON risk.jobs FOR EACH ROW EXECUTE FUNCTION risk.guard_run();

CREATE OR REPLACE FUNCTION risk.summary(job risk.jobs) RETURNS jsonb LANGUAGE sql SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('jobId',job.id,'requestId',job.request_id,'state',job.state,'contextId',job.context_id,
  'valuationAt',job.valuation_at,'createdAt',job.created_at,'startedAt',job.started_at,'finishedAt',job.finished_at,
  'attempt',job.attempt,'errorCode',job.error_code,'engineVersion',job.request->>'engineVersion','convention',job.request->>'convention',
  'run',job.frame->'run','scope',job.frame->'scope','shocks',jsonb_array_length(job.request->'bumps'));
$$;
COMMIT;
