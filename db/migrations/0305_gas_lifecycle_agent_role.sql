-- Opt-in delegated gas operations use the existing manual business handlers.
-- No principal, role assignment, delegation or default grant is created.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

DO $upgrade$
DECLARE spec record;p record;source text;changed text;expected_source text;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('security.guard_agent_roles()','638c55d0d903553fde917b5a63cdbc2c','trigger',false,NULL::text[],
   '{openetrm_security_owner=X/openetrm_security_owner}',
   'NEW.role_id IN(''a0000000-0000-0000-0000-000000000007'',''a0000000-0000-0000-0000-000000000008'')',
   'NEW.role_id IN(''a0000000-0000-0000-0000-000000000007'',''a0000000-0000-0000-0000-000000000008'',''a0000000-0000-0000-0000-000000000010'')'),
  ('security.administration_directory()','53ceb34d41698712dd57c3b4aecbcce8','jsonb',true,NULL::text[],
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}',
   'r.id NOT IN(''a0000000-0000-0000-0000-000000000007'',''a0000000-0000-0000-0000-000000000008'')',
   'r.id NOT IN(''a0000000-0000-0000-0000-000000000007'',''a0000000-0000-0000-0000-000000000008'',''a0000000-0000-0000-0000-000000000010'')'),
  ('security.apply_administration(jsonb)','117a5896265a59529a181d0138a1eccf','jsonb',true,ARRAY['request'],
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}',
   'ARRAY[''agent_reader'',''swap_settlement_operator'']::text[]',
   'ARRAY[''agent_reader'',''swap_settlement_operator'',''gas_lifecycle_operator'']::text[]')
 )v(signature,source_hash,result_type,definer,arg_names,acl,needle,replacement) LOOP
  SELECT f.*,l.lanname INTO p FROM pg_proc f JOIN pg_language l ON l.oid=f.prolang WHERE f.oid=to_regprocedure(spec.signature);
  IF NOT FOUND OR md5(p.prosrc) IS DISTINCT FROM spec.source_hash OR p.lanname<>'plpgsql'
   OR p.proowner<>'openetrm_security_owner'::regrole OR p.prosecdef IS DISTINCT FROM spec.definer
   OR p.prorettype<>spec.result_type::regtype OR p.prokind<>'f' OR p.proretset OR p.proisstrict OR p.proleakproof
   OR p.provolatile<>'v' OR p.proparallel<>'u' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proargnames IS DISTINCT FROM spec.arg_names OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.provariadic<>0
   OR p.pronargs<>coalesce(cardinality(spec.arg_names),0)
   OR p.proargtypes IS DISTINCT FROM(CASE WHEN spec.arg_names IS NULL THEN ''::oidvector ELSE('jsonb'::regtype::oid::text)::oidvector END)
   OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.probin IS NOT NULL OR p.prosqlbody IS NOT NULL
   OR p.procost<>100 OR p.prorows<>0 OR p.prosupport<>0 OR p.protrftypes IS NOT NULL
   OR p.proacl IS DISTINCT FROM spec.acl::aclitem[]
  THEN RAISE EXCEPTION 'gas_agent_role_upgrade: %',spec.signature;END IF;
  source=pg_get_functiondef(p.oid);changed=replace(source,spec.needle,spec.replacement);
  IF length(source)-length(replace(source,spec.needle,''))<>length(spec.needle)
  THEN RAISE EXCEPTION 'gas_agent_role_upgrade: %',spec.signature;END IF;
  expected_source=replace(p.prosrc,spec.needle,spec.replacement);EXECUTE changed;
  IF(SELECT f.prosrc FROM pg_proc f WHERE f.oid=p.oid) IS DISTINCT FROM expected_source
  THEN RAISE EXCEPTION 'gas_agent_role_upgrade: %',spec.signature;END IF;
 END LOOP;
END$upgrade$;

INSERT INTO security.roles(id,code,name) VALUES
 ('a0000000-0000-0000-0000-000000000010','gas_lifecycle_operator','Gas lifecycle operator');
INSERT INTO security.role_permissions VALUES
 ('a0000000-0000-0000-0000-000000000010','ReadMarketData'),
 ('a0000000-0000-0000-0000-000000000010','ReadTrades'),
 ('a0000000-0000-0000-0000-000000000010','ReadGasOwnedHoldings'),
 ('a0000000-0000-0000-0000-000000000010','RecordGasOwnedHoldings');
COMMIT;
