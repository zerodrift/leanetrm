-- The restrictive temporal policy already checks fresh market authority.
-- Remove only its duplicate permissive check on these exact table contracts.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $$BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
END$$;
DO $$
DECLARE name text; expected text; actual text; helper record;
 names constant text[]:=ARRAY['constituent_mappings','contracts','curve_inputs','curve_members',
  'curves','observations','rate_curve_points','rate_curves','revision_periods','revisions','series'];
BEGIN
 -- Lock the complete set in a fixed order before inspecting or altering any policy.
 FOREACH name IN ARRAY names LOOP
  EXECUTE format('LOCK TABLE market_data.%I IN SHARE ROW EXCLUSIVE MODE',name);
 END LOOP;
 -- Exact definitions include volatility, security mode and function-local settings.
 -- Pin the unchanged visibility helper and its current-authority dependencies.
 FOR helper IN SELECT * FROM(VALUES
  ('security.market_fact_visible(uuid,uuid,uuid)','2681ef478a2a067b6859826f2cc1a93a3faea813d0180a7b4a12e6f6471e96ae',true),
  ('security.market_access(uuid,text)','804fc62e6057b1b2d528e3493d761aa52d613a766a9087c90fcd6313b5832383',true),
  ('security.has_permission(text)','729a77c372f2c78e2898bde6a1316078de423f9762bb9836ba7dfdb4d49d4856',true),
  ('security.principal_has_permission(uuid,uuid,uuid,text,timestamp with time zone)','43e35b62e3ad9ea86286250b208edf56f3dd7ddce12801f4288d75b98d7506bb',false),
  ('security.current_write_context()','34ebc54d89ea1046f9e239d760731fcfce5f9d7acb6c6844cc24b6b9a068e4b9',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'market_read_authority: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
 FOREACH name IN ARRAY names LOOP
  -- Hash every policy, including write/current restrictions and role targets.
  -- This rejects missing, additional or modified policies before the first ALTER.
  expected=CASE
   WHEN name IN('contracts','curve_inputs','curves','series') THEN '57724281da4a33175b25665381b565ce0691f4ea7b105022970ddbc62a5f0ba1'
   WHEN name IN('constituent_mappings','curve_members','rate_curve_points','revision_periods') THEN '2169a05e9a6524c817476bf2abced8a12ec6e7bada12155fba3d1cb56de5bd71'
   WHEN name='revisions' THEN '9a40dee4bc132f82041f05acfef961d60087a27be8e702dbfe03a79dbc7165c8'
   WHEN name='rate_curves' THEN '22ee13baa64f511b3fb3fc7f8aa3fe8a362ddc96a0a3b785884085ae6de63d5f'
   WHEN name='observations' THEN 'd9e47909703e759194bfaa7ff07904ffadbc8e969810cbbd5459cf5693d0cf30'
  END;
  SELECT encode(sha256(convert_to(jsonb_agg(jsonb_build_object('name',p.polname,'command',p.polcmd,
    'permissive',p.polpermissive,'roles',(SELECT jsonb_agg(rolname ORDER BY rolname) FROM pg_roles WHERE oid=ANY(p.polroles)),
    'using',pg_get_expr(p.polqual,p.polrelid),'check',pg_get_expr(p.polwithcheck,p.polrelid)) ORDER BY p.polname)::text,'UTF8')),'hex')
   INTO actual FROM pg_policy p WHERE p.polrelid=to_regclass(format('market_data.%I',name));
  IF actual IS DISTINCT FROM expected OR NOT EXISTS(SELECT 1 FROM pg_class c
   WHERE c.oid=to_regclass(format('market_data.%I',name)) AND c.relkind='r' AND c.relrowsecurity AND c.relforcerowsecurity)
  THEN RAISE EXCEPTION 'market_read_authority: unexpected policies on market_data.%',name USING ERRCODE='55000';END IF;
 END LOOP;
 FOREACH name IN ARRAY names LOOP
  EXECUTE format('ALTER POLICY scoped_read ON market_data.%I USING(true)',name);
 END LOOP;
END$$;
COMMIT;
