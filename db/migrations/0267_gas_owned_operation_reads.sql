-- Operational evidence remains distinct from accepted financial application.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION security.read_gas_owned_operations(catalog uuid,context uuid,as_of timestamptz,account uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;result jsonb;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 IF account IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account)
  AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at)) THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_accounts a WHERE a.catalog_id=catalog AND(account IS NULL OR a.id=account)
  AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at) AND(
   security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE OR
   security.market_fact_visible(catalog,a.definition_revision_id,NULL) IS NOT TRUE))
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 WITH accounts AS(SELECT a.*,b.name book_name FROM trading.gas_owned_accounts a JOIN trading.books b ON(b.catalog_id,b.id)=(a.catalog_id,a.book_id)
  WHERE a.catalog_id=catalog AND(account IS NULL OR a.id=account) AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at)),
 evidence AS(SELECT e.*,k.known_from known_at FROM trading.gas_owned_evidence e JOIN accounts a ON(a.catalog_id,a.id)=(e.catalog_id,e.account_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
  WHERE security.trading_visible(e.catalog_id,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),
 positions AS(SELECT p.*,k.known_from known_at FROM trading.gas_owned_positions p JOIN accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)),
 reservations AS(SELECT r.*,k.known_from known_at FROM trading.gas_owned_reservations r JOIN positions p ON(p.catalog_id,p.id)=(r.catalog_id,r.lot_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
  WHERE security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)),
 consumptions AS(SELECT c.*,k.known_from known_at FROM trading.gas_owned_consumptions c JOIN positions p ON(p.catalog_id,p.id)=(c.catalog_id,c.lot_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(c.catalog_id,c.publication_order)
  WHERE security.trading_visible(c.catalog_id,c.publication_order,c.effective_at)),
 fees AS(SELECT f.* FROM trading.gas_owned_fees f JOIN positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id)
  WHERE security.trading_visible(f.catalog_id,f.publication_order,f.effective_at))
 SELECT jsonb_build_object('contextId',context,'asOf',as_of,'knownAt',t.known_at,'effectiveOn',t.effective_on,'authorityUntil',until_at,
  'accounts',coalesce((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.code) FROM accounts a),'[]'),
  'positions',coalesce((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.id) FROM positions p),'[]'),
  'evidence',coalesce((SELECT jsonb_agg(to_jsonb(e) ORDER BY e.account_id,e.source_key,e.revision) FROM evidence e),'[]'),
  'lots',coalesce((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.id) FROM trading.gas_owned_lots l JOIN positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.id)),'[]'),
  'reservations',coalesce((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM reservations r),'[]'),
  'releases',coalesce((SELECT jsonb_agg(to_jsonb(x)||jsonb_build_object('known_at',k.known_from) ORDER BY x.reservation_id) FROM trading.gas_owned_releases x
   JOIN reservations r ON(r.catalog_id,r.id)=(x.catalog_id,x.reservation_id) JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(x.catalog_id,x.publication_order)
   WHERE security.trading_visible(x.catalog_id,x.publication_order,x.effective_at)),'[]'),
  'consumptions',coalesce((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.id) FROM consumptions c),'[]'),
  'loans',coalesce((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.id) FROM trading.gas_owned_loans l JOIN positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.return_position_id)),'[]'),
  'repayments',coalesce((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM trading.gas_owned_repayments r JOIN consumptions c ON(c.catalog_id,c.id)=(r.catalog_id,r.consumption_id)),'[]'),
  'settlements',coalesce((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.id) FROM trading.gas_owned_claim_settlements s JOIN positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)
   WHERE security.trading_visible(s.catalog_id,s.publication_order,s.effective_at)),'[]'),
  'cashoutTerms',coalesce((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.position_id) FROM trading.gas_owned_cashout_terms s JOIN positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)),'[]'),
  'fees',coalesce((SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id) FROM fees f),'[]'),
  'payments',coalesce((SELECT jsonb_agg(to_jsonb(x) ORDER BY x.fee_id,x.business_key,x.revision) FROM trading.gas_owned_fee_payments x JOIN fees f ON(f.catalog_id,f.id)=(x.catalog_id,x.fee_id)
   WHERE security.trading_visible(x.catalog_id,x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),'[]'),
  'policies',coalesce((SELECT jsonb_agg(to_jsonb(b)||jsonb_build_object('mode',p.mode,'from_at',p.from_at,'until_at',p.until_at) ORDER BY b.account_id,b.action,b.revision)
   FROM trading.gas_owned_action_bindings b JOIN accounts a ON(a.catalog_id,a.id)=(b.catalog_id,b.account_id)
   JOIN lifecycle.action_policies p ON(p.catalog_id,p.id,p.revision)=(b.catalog_id,b.policy_id,b.policy_revision)
   WHERE security.trading_visible(b.catalog_id,b.publication_order,b.effective_at)),'[]')) INTO result
 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);RETURN result;
END$$;
ALTER FUNCTION security.read_gas_owned_operations(uuid,uuid,timestamptz,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_operations(uuid,uuid,timestamptz,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_operations(uuid,uuid,timestamptz,uuid) TO openetrm_runtime;
COMMIT;
