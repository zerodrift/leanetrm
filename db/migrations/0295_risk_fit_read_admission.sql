-- Current consumer: the established volatility-surface fit job. Its human
-- publisher and existing risk-reader service keep distinct permission sets.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE p pg_proc;entry record;definition text;actor_before text;actor_after text;service_before text;service_after text;BEGIN
 FOR entry IN SELECT * FROM(VALUES
  ('risk.bind(uuid,uuid)','5d592beff57a0c342b65be88088c7206','uuid','{job_id,token}',
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'),
  ('security.authority_until_pre_lifecycle_jobs(text)','20c5f2a85dbc9bed7bb03acfe2231660','timestamp with time zone','{permission}',
   '{openetrm_security_owner=X/openetrm_security_owner}')
 )v(signature,body_hash,return_type,argument_names,acl) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(entry.signature);
  IF NOT FOUND OR md5(p.prosrc)<>entry.body_hash OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>entry.return_type::regtype
   OR p.proargnames IS DISTINCT FROM entry.argument_names::text[] OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL
   OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
   OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM entry.acl
  THEN RAISE EXCEPTION 'risk_fit_admission_upgrade: unexpected helper %',entry.signature;END IF;
  definition=pg_get_functiondef(p.oid);
  IF entry.signature='risk.bind(uuid,uuid)' THEN
   actor_before='ARRAY[''ReadMarketData'',''ReadTrades'',''RunScenario'',''ReadGasOwnedHoldings'']';
   actor_after='ARRAY[''ReadMarketData'',''ReadTrades'',CASE WHEN job.request->>''convention''=''vol_surface_fit_v1'' THEN ''PublishMarketData'' ELSE ''RunScenario'' END,''ReadGasOwnedHoldings'']';
   service_before='security.grant_until(worker.principal_id,job.tenant_id,job.workspace_id,required,checked_at)';
   service_after='security.grant_until(worker.principal_id,job.tenant_id,job.workspace_id,CASE WHEN job.request->>''convention''=''vol_surface_fit_v1'' AND required=''PublishMarketData'' THEN ''RunScenario'' ELSE required END,checked_at)';
  ELSE
   actor_before='ARRAY[''ReadMarketData'',''ReadTrades'',''RunScenario'',permission]';
   actor_after='ARRAY[''ReadMarketData'',''ReadTrades'',CASE WHEN j.request->>''convention''=''vol_surface_fit_v1'' THEN ''PublishMarketData'' ELSE ''RunScenario'' END,permission]';
   service_before='security.grant_until(w.principal_id,j.tenant_id,j.workspace_id,required,checked)';
   service_after='security.grant_until(w.principal_id,j.tenant_id,j.workspace_id,CASE WHEN j.request->>''convention''=''vol_surface_fit_v1'' AND required=''PublishMarketData'' THEN ''RunScenario'' ELSE required END,checked)';
  END IF;
  IF length(definition)-length(replace(definition,actor_before,''))<>length(actor_before)
   OR length(definition)-length(replace(definition,service_before,''))<>length(service_before)
  THEN RAISE EXCEPTION 'risk_fit_admission_upgrade: unexpected permission boundary %',entry.signature;END IF;
  EXECUTE replace(replace(definition,actor_before,actor_after),service_before,service_after);
 END LOOP;
END$guard$;
COMMIT;
