-- One currency discount default per exact key, effective instant and publication.
-- Retained tied mappings stop this migration; they are never merged or deleted.
-- The lookup keeps its owner and grants and also refuses a tied latest mapping.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $$BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure('security.swap_discount_default(uuid,uuid,uuid,text,uuid,timestamp with time zone)')
  AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
  AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='eb80308013e4e61a98f05f392135c88b763704182efbf0d14025ea510d13270a'
  AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']::aclitem[]
  AND cardinality(p.proacl)=2)
 THEN RAISE EXCEPTION 'unambiguous_discount_defaults: unexpected function security.swap_discount_default' USING ERRCODE='55000';END IF;
END$$;
ALTER TABLE trading.currency_discount_defaults ADD CONSTRAINT currency_discount_default_key
 UNIQUE(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id,currency_code,effective_at,publication_order);
CREATE OR REPLACE FUNCTION security.swap_discount_default(catalog uuid,ref_catalog uuid,ref_revision uuid,currency text,definition uuid,valuation timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;candidates jsonb;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501'; END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.market_context_id IS NOT NULL AND c.trading_as_of IS DISTINCT FROM valuation THEN RAISE EXCEPTION 'swap_context' USING ERRCODE='P0001'; END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') OR NOT security.reference_version_visible(ref_catalog,ref_revision) OR NOT security.market_fact_visible(catalog,definition,NULL) THEN RAISE EXCEPTION 'No visible reference or market definition' USING ERRCODE='42501'; END IF;
 -- Group by the selection order so every mapping at the latest instant and publication is seen.
 SELECT jsonb_agg(to_jsonb(d)) INTO candidates FROM trading.currency_discount_defaults d
 WHERE d.catalog_id=catalog AND d.reference_catalog_id=ref_catalog AND d.reference_revision_id=ref_revision AND d.currency_code=currency AND d.definition_revision_id=definition AND d.effective_at<=valuation
 AND(c.market_context_id IS NULL OR security.trading_visible(d.catalog_id,d.publication_order,d.effective_at))
 GROUP BY d.effective_at,d.publication_order ORDER BY d.effective_at DESC,d.publication_order DESC LIMIT 1;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501'; END IF;
 IF NOT security.reference_access(ref_catalog,'ReadMarketData') OR NOT security.reference_version_visible(ref_catalog,ref_revision) OR NOT security.market_fact_visible(catalog,definition,NULL) THEN RAISE EXCEPTION 'No visible reference or market definition' USING ERRCODE='42501'; END IF;
 IF jsonb_array_length(candidates)>1 THEN RAISE EXCEPTION 'swap_discount_ambiguous' USING ERRCODE='P0001'; END IF;
 RETURN candidates->0;
END $$;
COMMIT;
