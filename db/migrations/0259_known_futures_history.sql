-- Consumer: economic projection of committed, knowledge-visible future margin
-- and its separately owned cash. This read advances no business-effective state.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

-- This paid-cash profile allocates to an existing owned obligation. Deposits
-- before ownership need a separately qualified advance asset/offset contract.
-- Existing command receipts and identical facts keep their first outcomes.
DO $patch$DECLARE f pg_proc;definition text;
 needle constant text='   IF expected<>coalesce(previous.revision,0) THEN RAISE EXCEPTION ''cash_payment_revision'';END IF;';
 replacement constant text='   IF expected<>coalesce(previous.revision,0) THEN RAISE EXCEPTION ''cash_payment_revision'';END IF;
   IF paid<(SELECT m.effective_at FROM trading.cash_payment_memberships m WHERE(m.catalog_id,m.trade_id,m.binding_id)=(catalog,trade,binding))
   THEN RAISE EXCEPTION ''cash_payment_membership_time'';END IF;';
BEGIN
 SELECT p.* INTO f FROM pg_proc p WHERE p.oid=to_regprocedure('security.record_cash_payment(uuid,jsonb,timestamptz)');
 IF NOT FOUND THEN RAISE EXCEPTION 'known_futures_upgrade: unexpected helper security.record_cash_payment(uuid,jsonb,timestamptz)';END IF;
 IF md5(f.prosrc)<>'75b0edfd08eaac2f92b9e41fb57018c6' OR NOT f.prosecdef OR f.proowner<>'openetrm_security_owner'::regrole
  OR f.prorettype<>'jsonb'::regtype OR f.prokind<>'f' OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql'
  OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
  OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
  OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') IS NOT TRUE
  OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a
   WHERE a.grantor<>f.proowner OR a.is_grantable OR a.privilege_type<>'EXECUTE'
    OR a.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
  OR(length(f.prosrc)-length(replace(f.prosrc,needle,'')))/length(needle)<>1
 THEN RAISE EXCEPTION 'known_futures_upgrade: unexpected helper security.record_cash_payment(uuid,jsonb,timestamptz)';END IF;
 definition=pg_get_functiondef(f.oid);EXECUTE replace(definition,needle,replacement);
END$patch$;

CREATE FUNCTION settlement_data.read_futures_known_history(catalog uuid,trade uuid,binding uuid,context uuid,as_of timestamptz)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE origin jsonb;result jsonb;prefix integer;market_prefix integer;cut timestamptz;until_at timestamptz;expected integer;BEGIN
 origin=settlement_data.read_futures_margin_origin(catalog,trade,binding,context,as_of);
 IF(origin->>'available')::boolean IS NOT TRUE THEN RAISE EXCEPTION 'futures_margin_unavailable';END IF;
 until_at=(origin->>'authorityUntil')::timestamptz;
 SELECT x.known_through,m.known_through,t.known_at INTO STRICT prefix,market_prefix,cut
 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
 WHERE(t.catalog_id,t.id)=(catalog,context);
 SELECT count(DISTINCT d.occurrence_id) INTO expected FROM settlement_data.futures_margin_determinations d
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
 WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) AND d.publication_order<=prefix AND k.known_from<=cut;
 IF expected>16384 OR(SELECT count(*) FROM settlement_data.futures_margin_determinations d
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
  WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) AND d.publication_order<=prefix AND k.known_from<=cut AND d.cash_binding_id IS NOT NULL)>20000
 THEN RAISE EXCEPTION 'futures_known_history_capacity';END IF;
 result=origin||jsonb_build_object('stateProfile','known_futures_journal_projection_source_v1','asOf',as_of,
 'heads',coalesce((SELECT jsonb_agg(to_jsonb(v) ORDER BY v.at,v.phase) FROM(
  WITH heads AS MATERIALIZED(
   SELECT DISTINCT ON(o.at,o.phase)d.*,o.at,o.phase,o.due_at,k.known_from
   FROM settlement_data.futures_margin_determinations d JOIN settlement_data.futures_margin_policy_occurrences o
   ON(o.catalog_id,o.policy_id,o.policy_revision,o.id)=(d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id)
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
   WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) AND d.publication_order<=prefix AND k.known_from<=cut
   ORDER BY o.at,o.phase,d.run_revision DESC)
  SELECT h.*,sk.known_from source_known_at,to_jsonb(s) source FROM heads h
  JOIN market_data.observations s ON(s.catalog_id,s.id)=(h.catalog_id,h.source_observation_id)
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(s.catalog_id,s.id)
  JOIN market_data.market_knowledge_receipts sk ON(sk.catalog_id,sk.event_order,sk.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  WHERE e.event_order<=market_prefix AND sk.known_from<=cut)v),'[]'::jsonb),
 'cash',coalesce((SELECT jsonb_agg(to_jsonb(v) ORDER BY v.at,v.publication_order,v.cash_binding_id) FROM(
  SELECT d.id,d.cash_binding_id,d.adjustment,d.publication_order,k.known_from,o.id occurrence_id,o.at,o.phase,o.due_at,
   b.book_id,i.instrument_id,i.reference_catalog_id,i.reference_revision_id,i.currency_code,i.payment_date,i.payment_at,
   coalesce((SELECT jsonb_agg(to_jsonb(payment) ORDER BY payment.allocation_id,payment.revision) FROM(
    SELECT p.allocation_id,p.revision,p.paid_at,trim_scale(p.amount)::text amount,pk.known_from known_at
    FROM settlement_data.cash_payment_revisions p JOIN trading.knowledge_receipts pk
    ON(pk.catalog_id,pk.publication_order)=(p.catalog_id,p.publication_order)
    WHERE(p.catalog_id,p.trade_id,p.binding_id)=(d.catalog_id,d.trade_id,d.cash_binding_id)
    AND p.publication_order<=prefix AND pk.known_from<=cut)payment),'[]'::jsonb) payments
  FROM settlement_data.futures_margin_determinations d JOIN settlement_data.futures_margin_policy_occurrences o
  ON(o.catalog_id,o.policy_id,o.policy_revision,o.id)=(d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
  JOIN trading.cash_payment_memberships b ON(b.catalog_id,b.trade_id,b.binding_id)=(d.catalog_id,d.trade_id,d.cash_binding_id)
  JOIN trading.cash_payment_instruments i ON(i.catalog_id,i.instrument_id)=(b.catalog_id,b.instrument_id)
  WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) AND o.at>as_of AND d.publication_order<=prefix AND k.known_from<=cut
  AND b.book_id=(origin->'adoption'->>'book_id')::uuid AND b.effective_at=o.at)v),'[]'::jsonb));
 -- Select latest accepted heads before source qualification. Missing source
 -- coverage must never turn a later determination into an older one.
 IF jsonb_array_length(result->'heads')<>expected THEN RAISE EXCEPTION 'futures_known_history_context';END IF;
 IF jsonb_array_length(result->'cash')<>(SELECT count(*) FROM settlement_data.futures_margin_determinations d
  JOIN settlement_data.futures_margin_policy_occurrences o ON(o.catalog_id,o.policy_id,o.policy_revision,o.id)=
   (d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
  WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) AND o.at>as_of
  AND d.publication_order<=prefix AND k.known_from<=cut AND d.cash_binding_id IS NOT NULL)
 THEN RAISE EXCEPTION 'futures_known_history_context';END IF;
 IF(SELECT coalesce(sum(jsonb_array_length(c->'payments')),0) FROM jsonb_array_elements(result->'cash')c)>20000
 THEN RAISE EXCEPTION 'futures_known_history_capacity';END IF;
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);
 RETURN result;
END$$;
ALTER FUNCTION settlement_data.read_futures_known_history(uuid,uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_futures_known_history(uuid,uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_known_history(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
CREATE FUNCTION settlement_data.require_futures_known_reference(catalog uuid,trade uuid,binding uuid,policy uuid,revision integer,until_at timestamptz)
 RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_margin_policies;BEGIN
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
 SELECT s.* INTO p FROM settlement_data.futures_margin_policies s JOIN settlement_data.futures_margin_adoptions a
 ON(a.catalog_id,a.policy_id,a.policy_revision)=(s.catalog_id,s.id,s.revision)
 WHERE(a.catalog_id,a.trade_id,a.binding_id,a.policy_id,a.policy_revision)=(catalog,trade,binding,policy,require_futures_known_reference.revision);
 IF p.id IS NULL THEN RAISE EXCEPTION 'futures_known_history_context';END IF;
 PERFORM settlement_data.require_futures_margin_reference(p);
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
END$$;
ALTER FUNCTION settlement_data.require_futures_known_reference(uuid,uuid,uuid,uuid,integer,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.require_futures_known_reference(uuid,uuid,uuid,uuid,integer,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.require_futures_known_reference(uuid,uuid,uuid,uuid,integer,timestamptz) TO openetrm_runtime;
COMMIT;
