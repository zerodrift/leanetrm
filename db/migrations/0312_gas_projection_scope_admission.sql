-- Current consumer: whole-book native gas projection scope, including workers.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
DO $upgrade$DECLARE item record;p pg_proc;BEGIN
 IF to_regprocedure('security.read_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean)') IS NOT NULL
  OR to_regprocedure('security.require_gas_owned_projection_scope_admission(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)') IS NOT NULL
 THEN RAISE EXCEPTION 'gas_projection_scope_upgrade: existing entry point';END IF;
 FOR item IN SELECT * FROM(VALUES
  ('settlement_data.futures_margin_preview_authority(uuid)','566649c0adc2b9900bbfb43318f137a8','timestamptz',ARRAY['catalog']),
  ('security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)','d50a2b8efc89ee74e3f030e0069e7c9f','boolean',
   ARRAY['catalog','context','at_time','books','configured','until_at'])
 )v(signature,body_hash,result_type,names) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(item.signature);
  IF NOT FOUND OR md5(p.prosrc) IS DISTINCT FROM item.body_hash OR p.proowner<>'openetrm_security_owner'::regrole
   OR p.prorettype<>item.result_type::regtype OR p.prokind<>'f' OR p.proretset OR p.proisstrict OR p.proleakproof OR NOT p.prosecdef
   OR p.provolatile<>'v' OR p.proparallel<>'u' OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL
   OR p.proargnames IS DISTINCT FROM item.names OR p.pronargs<>cardinality(item.names) OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
   OR p.procost<>100 OR p.prorows<>0 OR p.probin IS NOT NULL OR p.prosqlbody IS NOT NULL OR p.prosupport<>0 OR p.protrftypes IS NOT NULL
   OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR NOT has_function_privilege('openetrm_runtime',p.oid,'EXECUTE') OR NOT has_function_privilege('openetrm_risk_worker',p.oid,'EXECUTE')
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner)))a WHERE a.grantor<>p.proowner OR a.is_grantable
    OR a.privilege_type<>'EXECUTE' OR a.grantee NOT IN(p.proowner,'openetrm_runtime'::regrole,'openetrm_risk_worker'::regrole))
  THEN RAISE EXCEPTION 'gas_projection_scope_upgrade: %',item.signature;END IF;
 END LOOP;
END$upgrade$;

CREATE FUNCTION security.require_gas_owned_projection_scope_admission(catalog uuid,context uuid,as_of timestamptz,books uuid[],configured boolean,until_at timestamptz)
 RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE present boolean;BEGIN
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 present=security.require_gas_owned_projection_scope(catalog,context,as_of,books,configured,until_at);
 -- The predecessor's final clock test precedes permission/source work. Keep
 -- this check after that complete return, including in commit callbacks.
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 RETURN present;
END$$;

CREATE FUNCTION security.read_gas_owned_projection_scope(catalog uuid,context uuid,as_of timestamptz,books uuid[],configured boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE market_until timestamptz;trade_until timestamptz;gas_until timestamptz;original_until timestamptz;base_until timestamptz;until_at timestamptz;present boolean;BEGIN
 -- All selected permission terms precede the base authority helper's epoch,
 -- hosted-session and job lookups. PostgreSQL least ignores NULL, so missing
 -- selected authority must be refused explicitly before forming the minimum.
 market_until=security.authority_until('ReadMarketData');
 trade_until=security.authority_until('ReadTrades');
 IF configured IS NULL THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 IF configured THEN gas_until=security.authority_until('ReadGasOwnedHoldings');END IF;
 IF market_until IS NULL OR trade_until IS NULL OR NOT isfinite(market_until) OR NOT isfinite(trade_until)
  OR configured AND(gas_until IS NULL OR NOT isfinite(gas_until))
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 original_until=least(market_until,trade_until,gas_until);
 base_until=settlement_data.futures_margin_preview_authority(catalog);
 IF base_until IS NULL OR NOT isfinite(base_until) THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 until_at=least(original_until,base_until);
 present=security.require_gas_owned_projection_scope_admission(catalog,context,as_of,books,configured,until_at);
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('implementation','gas_owned_projection_scope_admission_v1','present',present,'authorityUntil',until_at);
END$$;
ALTER FUNCTION security.read_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.require_gas_owned_projection_scope_admission(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean),
 security.require_gas_owned_projection_scope_admission(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean),
 security.require_gas_owned_projection_scope_admission(uuid,uuid,timestamptz,uuid[],boolean,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
