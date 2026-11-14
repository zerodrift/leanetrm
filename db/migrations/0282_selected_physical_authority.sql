-- Consumers: interactive option actions and selected futures settlement setup.
-- An enrolled physical continuation needs gas authority before it owns stock.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$DECLARE item record;f pg_proc;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('lifecycle.capture_action_authority(uuid,text,jsonb)','7e68c2e0ca1bf95f75ef5c2d16c54cc6','timestamptz',true),
  ('lifecycle.option_proposal(uuid,jsonb)','5fd45a523df98bc3ca3b9dd199372719','jsonb',true),
  ('settlement_data.futures_margin_command_status(uuid,uuid,text,jsonb)','c7ac195c663fc5771b7bfbf0bc1c063a','jsonb',true),
  ('settlement_data.save_futures_margin_policy(uuid,jsonb)','d64003b19ceaeae8427e5e3b2dbf4d3d','jsonb',true),
  ('settlement_data.adopt_futures_margin(uuid,jsonb)','ed0ecffc8244667e5b59a2670f0d3676','jsonb',true),
  ('settlement_data.finish_futures_margin_command(uuid,uuid,text,jsonb,timestamptz)','05e30e52f892aff52448d1c2348f8810','void',true),
  ('settlement_data.futures_delivery_profile_document(uuid,uuid,integer,uuid,timestamptz)','66782d0f704c0795c4d5b2b5c10c82b6','jsonb',false),
  ('settlement_data.futures_margin_delivery_authority(uuid,uuid,uuid)','8f65d91223fb35a17910f4bbe7adb881','timestamptz',true),
  ('settlement_data.futures_margin_delivery_read_authority_pre_risk(uuid,uuid,uuid,uuid,timestamptz)','7390b34a46df0bb1a3a804d130af8a4e','timestamptz',false),
  ('settlement_data.read_futures_delivery_obligation_pre_risk(uuid,uuid,uuid,uuid,timestamptz)','c8d4767ba6a9e13395c51f39b00ede18','jsonb',false),
  ('settlement_data.read_futures_delivery_position_pre_risk(uuid,uuid,uuid,timestamptz)','3269aed6ae637282f7251fe370a4a98e','jsonb',false),
  ('settlement_data.read_futures_delivery_settings(uuid,uuid,timestamptz)','4b1752d348620c4091c9c48f3d2cdd80','jsonb',true),
  ('settlement_data.read_futures_delivery_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz)','fca7d1bf94c010eb63a562d18e58a301','jsonb',false)
 )v(signature,body_hash,result_type,runtime_execute) LOOP
  SELECT * INTO STRICT f FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(f.prosrc)<>item.body_hash OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.proretset
   OR f.prorettype<>item.result_type::regtype OR f.provariadic<>0 OR f.proargmodes IS NOT NULL OR f.pronargdefaults<>0
   OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
   OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
   OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') IS DISTINCT FROM item.runtime_execute
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable
    OR a.privilege_type<>'EXECUTE' OR a.grantee NOT IN(f.proowner,CASE WHEN item.runtime_execute THEN 'openetrm_runtime'::regrole ELSE f.proowner END))
  THEN RAISE EXCEPTION 'selected_physical_authority_upgrade: unexpected helper %',item.signature;END IF;
 END LOOP;
END$upgrade$;

-- Pin candidate gas authority before the base admission's epoch wait. Keep the
-- same function identities and every existing source/reference check.
DO $read_capture$DECLARE signature text;definition text;needle text;replacement text;BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'settlement_data.futures_delivery_profile_document(uuid,uuid,integer,uuid,timestamptz)',
  'settlement_data.futures_margin_delivery_read_authority_pre_risk(uuid,uuid,uuid,uuid,timestamptz)',
  'settlement_data.read_futures_delivery_obligation_pre_risk(uuid,uuid,uuid,uuid,timestamptz)',
  'settlement_data.read_futures_delivery_position_pre_risk(uuid,uuid,uuid,timestamptz)',
  'settlement_data.read_futures_delivery_settings(uuid,uuid,timestamptz)',
  'settlement_data.read_futures_delivery_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz)'] LOOP
  definition=pg_get_functiondef(signature::regprocedure);
  IF signature LIKE 'settlement_data.futures_margin_delivery_read_authority_pre_risk(%' THEN
   needle='until_at=settlement_data.futures_margin_preview_authority(catalog);';
   replacement='gas_until=security.authority_until(''ReadGasOwnedHoldings''); '||needle;
   definition=replace(definition,'security.authority_until(''ReadGasOwnedHoldings'')','gas_until');
  ELSE
   needle='until_at=least(settlement_data.futures_margin_preview_authority(catalog),security.authority_until(''ReadGasOwnedHoldings''));';
   replacement='gas_until=security.authority_until(''ReadGasOwnedHoldings''); until_at=least(settlement_data.futures_margin_preview_authority(catalog),gas_until);';
  END IF;
  IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1
   THEN RAISE EXCEPTION 'selected_physical_authority_upgrade: unexpected helper %',signature;END IF;
  definition=replace(definition,'until_at timestamptz;','until_at timestamptz;gas_until timestamptz;');
  EXECUTE replace(definition,needle,replacement);
 END LOOP;
 definition=pg_get_functiondef('settlement_data.futures_margin_delivery_authority(uuid,uuid,uuid)'::regprocedure);
 needle='until_at=settlement_data.capture_futures_margin_authority(catalog,''AmendTrade'');';
 replacement='gas_until=least(security.authority_until(''ReadGasOwnedHoldings''),security.authority_until(''RecordGasOwnedHoldings'')); '||needle;
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1
  THEN RAISE EXCEPTION 'selected_physical_authority_upgrade: unexpected helper settlement_data.futures_margin_delivery_authority(uuid,uuid,uuid)';END IF;
 definition=replace(definition,'until_at timestamptz;','until_at timestamptz;gas_until timestamptz;');
 definition=replace(definition,'RETURN least(until_at,(admission->>''until'')::timestamptz);',
  'PERFORM security.require_gas_owned_commit(catalog,least(until_at,gas_until),false,false); RETURN least(until_at,gas_until,(admission->>''until'')::timestamptz);');
 EXECUTE replace(definition,needle,replacement);
END$read_capture$;

-- Closed operation coordinates select an existing exact policy. A later head,
-- a commodity name and the presence of inventory are not selectors.
CREATE FUNCTION lifecycle.action_has_delivery(catalog uuid,kind text,body jsonb) RETURNS boolean
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected boolean=false;BEGIN
 IF kind='option_profile' THEN
  SELECT p.delivery_profile_id IS NOT NULL INTO selected FROM settlement_data.futures_margin_policies p
   WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'marginPolicyId')::uuid,(body->>'marginPolicyRevision')::integer);
 ELSIF kind IN('option.exercise','option.assignment','option.expiry','option.exercise_instruction','option.abandon_instruction') THEN
  SELECT p.delivery_profile_id IS NOT NULL INTO selected FROM lifecycle.option_profiles s JOIN settlement_data.futures_margin_policies p
   ON(p.catalog_id,p.id,p.revision)=(s.catalog_id,s.margin_policy_id,s.margin_policy_revision)
   WHERE(s.catalog_id,s.trade_id,s.action,s.revision)=(catalog,(body#>>'{target,scopeId}')::uuid,kind,(body#>>'{selection,profileRevision}')::integer);
 ELSIF kind='futures.margin' THEN
  SELECT p.delivery_profile_id IS NOT NULL INTO selected FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policies p
   ON(p.catalog_id,p.id,p.revision)=(a.catalog_id,a.policy_id,a.policy_revision)
   WHERE(a.catalog_id,a.trade_id,a.binding_id)=(catalog,(body#>>'{target,scopeId}')::uuid,(body#>>'{target,bindingId}')::uuid);
 END IF;
 RETURN coalesce(selected,false);
END$$;
CREATE FUNCTION lifecycle.require_action_read(catalog uuid,kind text,body jsonb,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM lifecycle.require_read(catalog,until_at);
 IF lifecycle.action_has_delivery(catalog,kind,body) THEN PERFORM security.require_gas_owned_preview(catalog,until_at);END IF;
END$$;
DO $copy$DECLARE signature text;definition text;BEGIN
 FOREACH signature IN ARRAY ARRAY['lifecycle.capture_action_authority(uuid,text,jsonb)','lifecycle.option_proposal(uuid,jsonb)'] LOOP
  definition=pg_get_functiondef(signature::regprocedure);
  IF signature LIKE 'lifecycle.capture_action_authority(%' THEN
   definition=replace(definition,'CREATE OR REPLACE FUNCTION lifecycle.capture_action_authority(','CREATE FUNCTION lifecycle.capture_action_authority_base(');
   EXECUTE definition;ALTER FUNCTION lifecycle.capture_action_authority_base(uuid,text,jsonb) OWNER TO openetrm_security_owner;
   REVOKE ALL ON FUNCTION lifecycle.capture_action_authority_base(uuid,text,jsonb) FROM PUBLIC;
  ELSE
   definition=replace(definition,'CREATE OR REPLACE FUNCTION lifecycle.option_proposal(','CREATE FUNCTION lifecycle.option_proposal_base(');
   EXECUTE definition;ALTER FUNCTION lifecycle.option_proposal_base(uuid,jsonb) OWNER TO openetrm_security_owner;
   REVOKE ALL ON FUNCTION lifecycle.option_proposal_base(uuid,jsonb) FROM PUBLIC;
  END IF;
 END LOOP;
END$copy$;
CREATE OR REPLACE FUNCTION lifecycle.capture_action_authority(catalog uuid,kind text,body jsonb) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;gas_until timestamptz;BEGIN
 gas_until=security.authority_until('ReadGasOwnedHoldings');
 until_at=lifecycle.capture_action_authority_base(catalog,kind,body);
 IF lifecycle.action_has_delivery(catalog,kind,body) THEN until_at=least(until_at,gas_until);END IF;
 PERFORM lifecycle.require_action_read(catalog,kind,body,until_at);RETURN until_at;
END$$;
CREATE OR REPLACE FUNCTION lifecycle.option_proposal(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;result jsonb;BEGIN
 until_at=lifecycle.capture_action_authority(catalog,body->>'action',body);
 result=lifecycle.option_proposal_base(catalog,body);
 PERFORM lifecycle.require_action_read(catalog,body->>'action',body,until_at);RETURN result;
END$$;

CREATE FUNCTION settlement_data.margin_command_has_delivery(catalog uuid,kind text,body jsonb) RETURNS boolean
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected boolean=false;BEGIN
 IF kind='policy_save' THEN RETURN jsonb_typeof(body#>'{policy,delivery}')='object';END IF;
 IF kind='adopt' THEN
  SELECT p.delivery_profile_id IS NOT NULL INTO selected FROM settlement_data.futures_margin_policies p
   WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer);
 ELSE RAISE EXCEPTION 'futures_margin_input';END IF;
 RETURN coalesce(selected,false);
END$$;
CREATE FUNCTION settlement_data.require_futures_margin_selected(catalog uuid,kind text,body jsonb,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE permission text;BEGIN
 permission=CASE kind WHEN 'policy_save' THEN 'WriteMarketDefinitions' WHEN 'adopt' THEN 'AmendTrade' ELSE NULL END;
 IF permission IS NULL THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 PERFORM settlement_data.require_futures_margin_authority(catalog,permission,until_at);
 IF settlement_data.margin_command_has_delivery(catalog,kind,body) THEN PERFORM security.require_gas_owned_preview(catalog,until_at);END IF;
END$$;
CREATE FUNCTION settlement_data.futures_margin_selected_authority(catalog uuid,kind text,body jsonb) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;gas_until timestamptz;BEGIN
 gas_until=security.authority_until('ReadGasOwnedHoldings');
 until_at=settlement_data.futures_margin_command_authority(catalog,kind);
 IF settlement_data.margin_command_has_delivery(catalog,kind,body) THEN until_at=least(until_at,gas_until);END IF;
 PERFORM settlement_data.require_futures_margin_selected(catalog,kind,body,until_at);RETURN until_at;
END$$;
DO $patch$DECLARE item record;definition text;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('settlement_data.futures_margin_command_status(uuid,uuid,text,jsonb)','until_at=settlement_data.capture_futures_margin_authority(catalog,permission);',
   'until_at=settlement_data.futures_margin_selected_authority(catalog,command_kind,body);'),
  ('settlement_data.save_futures_margin_policy(uuid,jsonb)','until_at=settlement_data.capture_futures_margin_authority(catalog,''WriteMarketDefinitions'');',
   'until_at=settlement_data.futures_margin_selected_authority(catalog,''policy_save'',body);'),
  ('settlement_data.adopt_futures_margin(uuid,jsonb)','until_at=settlement_data.capture_futures_margin_authority(catalog,''AmendTrade'');',
   'until_at=settlement_data.futures_margin_selected_authority(catalog,''adopt'',body);'),
  ('settlement_data.finish_futures_margin_command(uuid,uuid,text,jsonb,timestamptz)','actor=settlement_data.require_futures_margin_authority(catalog,permission,until_at);',
   'PERFORM settlement_data.require_futures_margin_selected(catalog,command_kind,body,until_at); actor=settlement_data.require_futures_margin_authority(catalog,permission,until_at);')
 )v(signature,needle,replacement) LOOP
  definition=pg_get_functiondef(item.signature::regprocedure);
  IF(length(definition)-length(replace(definition,item.needle,'')))/length(item.needle)<>1
   THEN RAISE EXCEPTION 'selected_physical_authority_upgrade: unexpected helper %',item.signature;END IF;
  EXECUTE replace(definition,item.needle,item.replacement);
 END LOOP;
END$patch$;

CREATE FUNCTION settlement_data.require_futures_margin_selected_preview(catalog uuid,trade uuid,binding uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
 IF lifecycle.action_has_delivery(catalog,'futures.margin',jsonb_build_object('target',jsonb_build_object('scopeId',trade,'bindingId',binding)))
  THEN PERFORM security.require_gas_owned_preview(catalog,until_at);END IF;
END$$;
CREATE FUNCTION settlement_data.futures_margin_selected_preview_authority(catalog uuid,trade uuid,binding uuid) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;gas_until timestamptz;BEGIN
 gas_until=security.authority_until('ReadGasOwnedHoldings');until_at=settlement_data.futures_margin_preview_authority(catalog);
 IF lifecycle.action_has_delivery(catalog,'futures.margin',jsonb_build_object('target',jsonb_build_object('scopeId',trade,'bindingId',binding)))
  THEN until_at=least(until_at,gas_until);END IF;
 PERFORM settlement_data.require_futures_margin_selected_preview(catalog,trade,binding,until_at);RETURN until_at;
END$$;
-- Optional discovery uses the permissions present at its entry. Acquiring a
-- new grant during source resolution does not add data to this same read.
CREATE FUNCTION lifecycle.discovery_authority(catalog uuid,requires_gas boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;gas_until timestamptz;included boolean;BEGIN
 gas_until=security.authority_until('ReadGasOwnedHoldings');
 included=security.has_permission('ReadGasOwnedHoldings') IS TRUE AND gas_until>clock_timestamp();
 IF requires_gas IS NULL THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 IF requires_gas THEN PERFORM security.require_gas_owned_preview(catalog,gas_until);END IF;
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 IF included THEN PERFORM security.require_gas_owned_preview(catalog,least(until_at,gas_until));END IF;
 RETURN jsonb_build_object('authorityUntil',until_at,'gasUntil',CASE WHEN included THEN least(until_at,gas_until) ELSE NULL END);
END$$;
DO $acl$DECLARE signature text;BEGIN
 FOREACH signature IN ARRAY ARRAY['lifecycle.discovery_authority(uuid,boolean)','lifecycle.action_has_delivery(uuid,text,jsonb)','lifecycle.require_action_read(uuid,text,jsonb,timestamptz)',
  'settlement_data.margin_command_has_delivery(uuid,text,jsonb)','settlement_data.require_futures_margin_selected(uuid,text,jsonb,timestamptz)',
  'settlement_data.futures_margin_selected_authority(uuid,text,jsonb)',
  'settlement_data.require_futures_margin_selected_preview(uuid,uuid,uuid,timestamptz)','settlement_data.futures_margin_selected_preview_authority(uuid,uuid,uuid)'] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
 END LOOP;
END$acl$;
GRANT EXECUTE ON FUNCTION lifecycle.discovery_authority(uuid,boolean),lifecycle.require_action_read(uuid,text,jsonb,timestamptz),settlement_data.futures_margin_selected_authority(uuid,text,jsonb),
 settlement_data.require_futures_margin_selected_preview(uuid,uuid,uuid,timestamptz),settlement_data.futures_margin_selected_preview_authority(uuid,uuid,uuid) TO openetrm_runtime;
COMMIT;
