-- Current consumer: ordinary and economic owned-gas risk labels. Enrich only
-- memberships already returned by the qualified holdings reader, using their
-- original applied evidence reference rather than a later correction head.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])');
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_owned_labels_upgrade: missing helper security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])';END IF;
 IF md5(p.prosrc)<>'5cc0ee86f3afd478b0e41adedab80772' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'jsonb'::regtype OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0
  OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'gas_owned_labels_upgrade: unexpected helper security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])';END IF;
 EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION security.read_gas_owned_holdings(','FUNCTION security.read_gas_owned_holdings_before_labels('),
  'read_gas_owned_holdings.','read_gas_owned_holdings_before_labels.');
END$guard$;
ALTER FUNCTION security.read_gas_owned_holdings_before_labels(uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_holdings_before_labels(uuid,uuid,timestamptz,uuid[]) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.read_gas_owned_holdings(catalog uuid,context uuid,at_time timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE result jsonb;part text;rows jsonb;complete boolean;BEGIN
 result=security.read_gas_owned_holdings_before_labels(catalog,context,at_time,books);
 FOREACH part IN ARRAY ARRAY['positions','fees','deliveryCash'] LOOP
  IF jsonb_typeof(result->part) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_owned_labels_source';END IF;
  SELECT coalesce(jsonb_agg(item.value||jsonb_build_object('account_code',a.code,'source_reference',e.source_key,
    'origin_trade_reference',r.reference_number) ORDER BY item.ordinality),'[]'::jsonb),
   coalesce(bool_and(p.id IS NOT NULL AND a.code IS NOT NULL AND e.source_key IS NOT NULL),true) INTO rows,complete
  FROM jsonb_array_elements(result->part) WITH ORDINALITY item(value,ordinality)
  LEFT JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id,p.account_id)=
   (catalog,(CASE part WHEN 'positions' THEN item.value->>'id' ELSE item.value->>'position_id' END)::uuid,(item.value->>'account_id')::uuid)
  LEFT JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  LEFT JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id,e.account_id)=(p.catalog_id,p.evidence_id,p.account_id)
  LEFT JOIN settlement_data.futures_delivery_obligations d ON(d.catalog_id,d.position_id)=(p.catalog_id,p.id)
  LEFT JOIN trading.trade_references r ON(r.catalog_id,r.trade_id)=(d.catalog_id,d.trade_id);
  IF NOT complete THEN RAISE EXCEPTION 'gas_owned_labels_source';END IF;
  result=jsonb_set(result,ARRAY[part],rows);
 END LOOP;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,(result->>'authorityUntil')::timestamptz);
 RETURN result;
END$function$;
COMMIT;
