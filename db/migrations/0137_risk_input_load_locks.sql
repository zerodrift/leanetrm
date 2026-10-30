-- A bound input-loading transaction inserts a worker_attempt_contexts row,
-- retaining the job FK's KEY SHARE lock until loading commits. Control updates
-- do not change the job key: serialize them without blocking that FK lock.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';

CREATE OR REPLACE FUNCTION risk.owned_attempt(job_id uuid,token uuid,allow_cancel boolean,lock_row boolean) RETURNS risk.jobs LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor risk.workers;job risk.jobs;
BEGIN
 actor=risk.worker();
 IF lock_row THEN
  SELECT * INTO job FROM risk.jobs WHERE id=job_id AND(catalog_id,tenant_id,workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id) FOR NO KEY UPDATE;
 ELSE
  SELECT * INTO job FROM risk.jobs WHERE id=job_id AND(catalog_id,tenant_id,workspace_id)=(actor.catalog_id,actor.tenant_id,actor.workspace_id);
 END IF;
 IF job.id IS NULL OR job.worker_login IS DISTINCT FROM session_user OR job.lease_token IS DISTINCT FROM token OR token IS NULL
  OR job.lease_expires_at<=clock_timestamp() OR job.state NOT IN('running','cancellation_requested') THEN RAISE EXCEPTION 'risk_job_fence' USING ERRCODE='42501';END IF;
 IF job.security_generation<>(SELECT generation FROM security.epoch) OR job.attempt_security_generation<>(SELECT generation FROM security.epoch)
  OR NOT risk.authorized(job) THEN RAISE EXCEPTION 'risk_job_authority_changed' USING ERRCODE='42501';END IF;
 IF NOT allow_cancel AND job.state='cancellation_requested' THEN RAISE EXCEPTION 'risk_job_cancelled';END IF;
 RETURN job;
END$$;

CREATE OR REPLACE FUNCTION risk.cancel(job_id uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE job risk.jobs;
BEGIN
 job=risk.owner(job_id);SELECT * INTO job FROM risk.jobs WHERE id=job_id FOR NO KEY UPDATE;
 PERFORM risk.owner(job_id);
 IF job.state='queued' THEN UPDATE risk.jobs SET state='cancelled',finished_at=clock_timestamp() WHERE id=job_id RETURNING * INTO job;
 ELSIF job.state='running' THEN UPDATE risk.jobs SET state='cancellation_requested' WHERE id=job_id RETURNING * INTO job;END IF;
 RETURN risk.summary(job);
END$$;
ALTER FUNCTION risk.owned_attempt(uuid,uuid,boolean,boolean) OWNER TO openetrm_security_owner;
ALTER FUNCTION risk.cancel(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.owned_attempt(uuid,uuid,boolean,boolean),risk.cancel(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.cancel(uuid) TO openetrm_runtime;
COMMIT;
