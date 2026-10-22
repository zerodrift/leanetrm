-- One fresh complete authority decision per existing-close context check.
-- Preserve legacy checks, security ownership/ACLs and the final publication guards.
BEGIN;
CREATE OR REPLACE FUNCTION automation.pilot_service_eligible(j automation.jobs,w automation.workers) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT w.kind='scheduler' AND(j.tenant_id,j.workspace_id)=(w.tenant_id,w.workspace_id)
 AND CASE j.operation WHEN 'close_book_risk_v1' THEN automation.close_risk_authorized(j,w.principal_id) AND EXISTS(SELECT 1 FROM automation.pilot_runtime_logins l JOIN automation.close_risk_profiles p ON p.id=j.close_risk_profile_id WHERE l.worker_login=w.login AND l.catalog_id=p.catalog_id) WHEN 'generated_pilot_close_v1' THEN EXISTS(SELECT 1 FROM automation.pilot_runtime_logins l JOIN automation.pilot_close_profiles p
  ON(p.id,p.version)=(j.pilot_profile_id,j.pilot_profile_version) WHERE l.worker_login=w.login AND l.catalog_id=p.catalog_id)
 AND NOT EXISTS(SELECT 1 FROM unnest(automation.pilot_permissions())p
  WHERE NOT security.principal_has_permission(w.principal_id,w.tenant_id,w.workspace_id,p,clock_timestamp())) ELSE false END;
$$;
CREATE OR REPLACE FUNCTION automation.pilot_context_live(token uuid) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM automation.pilot_run_contexts c JOIN automation.runs r ON r.id=c.run_id JOIN automation.jobs j ON j.id=r.job_id
 JOIN automation.workers w ON w.login=c.worker_login JOIN automation.pilot_runtime_logins l ON l.worker_login=w.login

 JOIN security.request_contexts q ON q.id=c.context_id JOIN security.principals sp ON sp.id=w.principal_id AND sp.enabled AND sp.kind='service'
 WHERE c.context_id=token AND(c.fence,c.worker_id,c.worker_login)=(r.fence,r.worker_id,r.worker_login)
 AND(q.actor_id,q.tenant_id,q.workspace_id,q.runtime_login)=(w.principal_id,w.tenant_id,w.workspace_id,l.runtime_login)
 AND w.kind='scheduler' AND r.state='running' AND j.state IN('active','paused') AND r.lease_until>clock_timestamp() AND r.deadline_at>clock_timestamp() AND j.expires_at>clock_timestamp()
 AND automation.pilot_service_eligible(j,w)
 -- The close-risk eligibility branch already performs the full current authorization.
 AND CASE WHEN j.operation='close_book_risk_v1' THEN true ELSE automation.pilot_authorized(j,w.principal_id) END);
$$;
COMMIT;
