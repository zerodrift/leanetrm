-- Selected correction history uses the same indexed identity probes at every
-- request size. Large custom-plan requests exceeded the read deadline in CI;
-- use the passing generic plan and restore the caller's planning mode on return.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $upgrade$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_proc p
  WHERE p.oid='security.read_observation_facts(uuid,uuid,uuid[])'::regprocedure
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='cc37c8fa87c20caf161594c90778c6c500a5dfa2096199b96e25cf356e8de2e0'
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
   AND p.proconfig=ARRAY['search_path=pg_catalog'] AND p.provolatile='v'
   AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'::aclitem[])
 THEN RAISE EXCEPTION 'fact_plan_upgrade: unexpected helper security.read_observation_facts(uuid,uuid,uuid[])';END IF;
END $upgrade$;
ALTER FUNCTION security.read_observation_facts(uuid,uuid,uuid[]) SET plan_cache_mode=force_generic_plan;
COMMIT;
