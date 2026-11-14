-- Consumer: status-only recovery of earlier swap settlement requests. A
-- negative result serializes with financial publication before dismissal.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$DECLARE f pg_proc;BEGIN
 SELECT * INTO STRICT f FROM pg_proc WHERE oid='settlement_data.swap_operation_status(uuid,uuid)'::regprocedure;
 IF md5(f.prosrc)<>'f7dd5a5bff1e1cb26f9169d03c6b36ad' OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef
 OR f.provariadic<>0 OR f.proargmodes IS NOT NULL OR f.prokind<>'f' OR f.prorettype<>'jsonb'::regtype OR f.proretset OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
 OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
 OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE')
 OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable
  OR a.privilege_type<>'EXECUTE' OR a.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
 THEN RAISE EXCEPTION 'retained_swap_status_upgrade: unexpected helper settlement_data.swap_operation_status(uuid,uuid)';END IF;
END$upgrade$;
CREATE FUNCTION settlement_data.require_swap_operation_read(catalog uuid,operation uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;trade uuid;BEGIN
 PERFORM lifecycle.require_read(catalog,until_at);PERFORM settlement_data.require_swap_authority(catalog,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'settlement_input';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT c.trade_id INTO trade FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.operation_id,c.actor_id)=(catalog,operation,actor);
 IF trade IS NOT NULL THEN PERFORM settlement_data.require_swap_reference(catalog,trade);END IF;
 PERFORM lifecycle.require_read(catalog,until_at);
END$$;
CREATE OR REPLACE FUNCTION settlement_data.swap_operation_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;actor uuid;until_at timestamptz;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 PERFORM settlement_data.require_swap_operation_read(catalog,operation,until_at);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM settlement_data.require_swap_operation_read(catalog,operation,until_at);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT c.outcome INTO result FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.operation_id,c.actor_id)=(catalog,operation,actor);
 PERFORM settlement_data.require_swap_operation_read(catalog,operation,until_at);
 RETURN coalesce(result||jsonb_build_object('found',true),jsonb_build_object('found',false));
END$$;
ALTER FUNCTION settlement_data.require_swap_operation_read(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.require_swap_operation_read(uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.require_swap_operation_read(uuid,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
