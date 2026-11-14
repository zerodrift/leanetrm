-- Current consumer: explicitly selected saved-profile carrying for ordinary
-- and economic native delivery principal. Other cash keeps its selected law.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE FUNCTION security.read_gas_owned_profile_holdings(catalog uuid,context uuid,at_time timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE result jsonb;rows jsonb;complete boolean;until_at timestamptz;BEGIN
 -- The existing source reader establishes actor/service scope, the exact
 -- context and original authority deadline before any profile is inspected.
 result=security.read_gas_owned_holdings(catalog,context,at_time,books);
 until_at=(result->>'authorityUntil')::timestamptz;
 IF jsonb_typeof(result->'deliveryCash') IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_principal_profile_source';END IF;
 SELECT coalesce(jsonb_agg(item.value||jsonb_build_object('principalCarrying',jsonb_build_object(
   'implementation','saved_delivery_profile_principal_carrying_v1','profileId',p.id,'profileRevision',p.revision,'termsId',p.terms_id,
   'positionId',position.id,'accountId',g.account_id,'definitionRevisionId',account.definition_revision_id,
   'markCurve',position.mark_curve_code,'markContractId',position.mark_contract_id,
   'referenceCatalogId',c.reference_catalog_id,'referenceRevisionId',c.reference_revision_id,'currency',c.currency_code,
   'dueAt',g.cash_due_at,'carrying',g.carrying,'publicationOrder',p.publication_order,'knownAt',receipt.known_from))
   ORDER BY item.ordinality),'[]'::jsonb),
   coalesce(bool_and((c.id IS NOT NULL AND p.id IS NOT NULL AND account.id IS NOT NULL AND cut.context_id IS NOT NULL AND
    p.implementation='gas_future_physical_delivery_v1' AND g.carrying IN('nominal_mark_v1','currency_curve_v1') AND
    p.publication_order<=cut.known_through AND receipt.known_from<=(result->>'knownAt')::timestamptz AND
    (c.reference_catalog_id,c.reference_revision_id,c.currency_code)=(p.reference_catalog_id,p.reference_revision_id,p.currency_code) AND
    (account.id,account.book_id,account.definition_revision_id)=(
     (item.value->>'account_id')::uuid,(item.value->>'book_id')::uuid,(item.value->>'definition_revision_id')::uuid) AND
    c.amount=(item.value->>'amount')::numeric AND c.due_at=g.cash_due_at) IS TRUE),true) INTO rows,complete
 FROM jsonb_array_elements(result->'deliveryCash') WITH ORDINALITY item(value,ordinality)
 LEFT JOIN trading.gas_futures_delivery_cash c ON(c.catalog_id,c.id,c.position_id)=
  (catalog,(item.value->>'id')::uuid,(item.value->>'position_id')::uuid)
 LEFT JOIN settlement_data.futures_delivery_obligations d ON(d.catalog_id,d.position_id)=(c.catalog_id,c.position_id)
 LEFT JOIN settlement_data.futures_delivery_profiles p ON(p.catalog_id,p.id,p.revision)=(d.catalog_id,d.profile_id,d.profile_revision)
 LEFT JOIN trading.gas_futures_delivery_terms g ON(g.catalog_id,g.profile_id,g.profile_revision)=(p.catalog_id,p.id,p.revision)
 LEFT JOIN trading.gas_owned_positions position ON(position.catalog_id,position.id,position.account_id)=(c.catalog_id,c.position_id,g.account_id)
 LEFT JOIN trading.gas_owned_accounts account ON(account.catalog_id,account.id)=(position.catalog_id,position.account_id)
 LEFT JOIN trading.contexts cut ON(cut.catalog_id,cut.context_id)=(catalog,context)
 LEFT JOIN trading.knowledge_receipts receipt ON(receipt.catalog_id,receipt.publication_order)=(p.catalog_id,p.publication_order);
 IF NOT complete OR pg_column_size(rows)>16777216 THEN RAISE EXCEPTION 'gas_principal_profile_source';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);
 RETURN result||jsonb_build_object('deliveryCash',rows,'principalCarryingImplementation','saved_delivery_profile_principal_carrying_v1');
END$function$;
ALTER FUNCTION security.read_gas_owned_profile_holdings(uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_profile_holdings(uuid,uuid,timestamptz,uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_profile_holdings(uuid,uuid,timestamptz,uuid[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
