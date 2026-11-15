-- Consumer: Lifecycle discovery of retained physical-delivery settings.
-- Resolve metadata from each eligible account before joining other catalog rows.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$
DECLARE f pg_proc;definition text;previous text;replacement text;
BEGIN
 SELECT * INTO f FROM pg_proc WHERE oid=to_regprocedure('settlement_data.read_futures_delivery_settings(uuid,uuid,timestamptz)');
 IF NOT FOUND OR md5(f.prosrc) IS DISTINCT FROM 'e70b91dd01549e48d7e10381ca43cdd3'
  OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.proretset
  OR f.prorettype<>'jsonb'::regtype OR f.provariadic<>0 OR f.proargmodes IS NOT NULL OR f.proallargtypes IS NOT NULL
  OR f.proargnames IS DISTINCT FROM ARRAY['catalog','context','as_of'] OR f.pronargs<>3
  OR f.pronargdefaults<>0 OR f.proargdefaults IS NOT NULL OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
  OR f.procost<>100 OR f.prorows<>0 OR f.probin IS NOT NULL OR f.prosqlbody IS NOT NULL OR f.prosupport<>0 OR f.protrftypes IS NOT NULL
  OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE')
  OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable
   OR a.privilege_type<>'EXECUTE' OR a.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
 THEN RAISE EXCEPTION 'delivery_settings_upgrade: unexpected helper settlement_data.read_futures_delivery_settings(uuid,uuid,timestamptz)';END IF;
 previous=$old$  FROM trading.gas_owned_accounts a JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(a.catalog_id,a.publication_order)
  JOIN trading.books b ON(b.catalog_id,b.id)=(a.catalog_id,a.book_id)
  JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=(a.catalog_id,a.legal_entity_id,a.legal_entity_revision)
  JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(a.catalog_id,a.definition_revision_id,a.mark_contract_id)
  JOIN market_data.curves m ON(m.catalog_id,m.revision_id,m.code)=(a.catalog_id,a.definition_revision_id,a.mark_curve_code)
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code)
  WHERE a.catalog_id=catalog AND a.kind='owned_inventory' AND a.publication_order<=prefix AND k.known_from<=cut AND a.effective_at<=as_of$old$;
 replacement=$new$  FROM trading.gas_owned_accounts a
  JOIN LATERAL(SELECT 1 FROM trading.knowledge_receipts k
   WHERE(k.catalog_id,k.publication_order)=(a.catalog_id,a.publication_order) AND k.known_from<=cut OFFSET 0)k ON true
  JOIN LATERAL(SELECT b.name FROM trading.books b WHERE(b.catalog_id,b.id)=(a.catalog_id,a.book_id) OFFSET 0)b ON true
  JOIN LATERAL(SELECT e.name FROM trading.legal_entities e
   WHERE(e.catalog_id,e.id,e.revision)=(a.catalog_id,a.legal_entity_id,a.legal_entity_revision) OFFSET 0)e ON true
  JOIN LATERAL(SELECT c.expiry_at FROM market_data.contracts c
   WHERE(c.catalog_id,c.revision_id,c.id)=(a.catalog_id,a.definition_revision_id,a.mark_contract_id) OFFSET 0)c ON true
  JOIN LATERAL(SELECT m.reference_catalog_id,m.reference_revision_id,m.quote_code FROM market_data.curves m
   WHERE(m.catalog_id,m.revision_id,m.code)=(a.catalog_id,a.definition_revision_id,a.mark_curve_code) OFFSET 0)m ON true
  JOIN LATERAL(SELECT q.multiplier FROM ref_data.quote_specifications q
   WHERE(q.catalog_id,q.revision_id,q.code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code) OFFSET 0)q ON true
  WHERE a.catalog_id=catalog AND a.kind='owned_inventory' AND a.publication_order<=prefix AND a.effective_at<=as_of$new$;
 definition=pg_get_functiondef(f.oid);
 IF(length(definition)-length(replace(definition,previous,'')))/length(previous)<>1
 THEN RAISE EXCEPTION 'delivery_settings_upgrade: unexpected account query';END IF;
 -- Keep the original pre-wait authority capture, every profile revision,
 -- source checks, scope bounds, ordering, exit checks, owner and grants.
 EXECUTE replace(definition,previous,replacement);
END$upgrade$;
COMMIT;
