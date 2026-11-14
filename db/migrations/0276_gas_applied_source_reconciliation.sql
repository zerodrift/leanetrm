-- Current consumers: complete economic physical-futures continuation and
-- source-evidence reconciliation for independently held gas effects.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

-- Reuse the qualified knowledge-only journal producer privately. Its public
-- whole-book boundary stays unchanged. The new public attachment entry point
-- proves exact parent scope and removes unrelated facts before returning data.
DO $guard$DECLARE p pg_proc;definition text;needle text=' PERFORM security.require_gas_owned_projection_scope(catalog,context,at_time,books,true,until_at);';
 accounts text='a.book_id=ANY(books)';held text=$old$WHERE security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),$old$;BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])'::regprocedure;
 IF md5(p.prosrc)<>'0bd19cf50ad3026865838f7ad251508c' OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype
  OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
  OR p.proargnames IS DISTINCT FROM ARRAY['catalog','context','at_time','books'] OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR p.provolatile<>'v' OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM true
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
  OR to_regprocedure('security.gas_physical_journal_source(uuid,uuid,timestamptz,uuid[],uuid[])') IS NOT NULL
 THEN RAISE EXCEPTION 'gas_physical_source_upgrade: unexpected helper security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])';END IF;
 definition=pg_get_functiondef(p.oid);
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1
  OR(length(definition)-length(replace(definition,accounts,'')))/length(accounts)<>2
  OR(length(definition)-length(replace(definition,held,'')))/length(held)<>1
  OR(length(definition)-length(replace(definition,'books uuid[])','')))/length('books uuid[])')<>1
 THEN RAISE EXCEPTION 'gas_physical_source_upgrade: unexpected helper security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])';END IF;
 definition=replace(replace(replace(definition,'FUNCTION security.read_gas_owned_known_history(','FUNCTION security.gas_physical_journal_source('),needle,''),
  'books uuid[])','books uuid[], selected_positions uuid[])');
 definition=replace(definition,accounts,accounts||' AND EXISTS(SELECT 1 FROM trading.gas_owned_positions selected WHERE(selected.catalog_id,selected.account_id)=(a.catalog_id,a.id) AND selected.id=ANY(selected_positions))');
 definition=replace(definition,held,$new$WHERE p.id=ANY(selected_positions) AND security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),$new$);
 EXECUTE definition;
END$guard$;
ALTER FUNCTION security.gas_physical_journal_source(uuid,uuid,timestamptz,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_physical_journal_source(uuid,uuid,timestamptz,uuid[],uuid[]) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION security.read_gas_physical_known_attachments(catalog uuid,context uuid,at_time timestamptz,books uuid[],parents jsonb DEFAULT NULL) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;cut timestamptz;prefix integer;market_prefix integer;day date;job risk.jobs;
 journal jsonb;result jsonb;attachments jsonb='[]';item jsonb;key text;link settlement_data.futures_delivery_obligations;
 profile settlement_data.futures_delivery_profiles;terms trading.gas_futures_delivery_terms;account trading.gas_owned_accounts;
 position trading.gas_owned_positions;latest record;finals jsonb;performances jsonb;splits jsonb;runs jsonb;steps jsonb;cash jsonb;payments jsonb;
 position_ids uuid[]='{}';account_ids uuid[]='{}';next_ids uuid[];BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);
 IF books IS NULL OR cardinality(books) NOT BETWEEN 1 AND 1000 OR cardinality(books)<>(SELECT count(DISTINCT b) FROM unnest(books)b)
  OR EXISTS(SELECT 1 FROM unnest(books)b WHERE b IS NULL OR NOT EXISTS(SELECT 1 FROM trading.books x WHERE(x.catalog_id,x.id)=(catalog,b)
   AND security.trading_visible(x.catalog_id,x.publication_order,x.effective_at)))
 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 IF parents IS NOT NULL THEN
  IF jsonb_typeof(parents) IS DISTINCT FROM 'array' OR jsonb_array_length(parents) NOT BETWEEN 1 AND 16384 OR pg_column_size(parents)>2097152
  THEN RAISE EXCEPTION 'gas_physical_parent_scope';END IF;
  FOR item IN SELECT value FROM jsonb_array_elements(parents) LOOP
   PERFORM trading.gas_owned_keys(item,ARRAY['tradeId','bindingId']);
   IF(item->>'tradeId')::uuid IS NULL OR(item->>'bindingId')::uuid IS NULL OR(item->>'tradeId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
    OR(item->>'bindingId')::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'gas_physical_parent_scope';END IF;
   IF NOT EXISTS(SELECT 1 FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id,r.binding_id)=(catalog,(item->>'tradeId')::uuid,(item->>'bindingId')::uuid)
    AND r.book_id=ANY(books) AND security.trading_visible(r.catalog_id,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00'))
   THEN RAISE EXCEPTION 'gas_physical_parent_scope';END IF;
  END LOOP;
  IF jsonb_array_length(parents)<>(SELECT count(DISTINCT(value->>'tradeId',value->>'bindingId')) FROM jsonb_array_elements(parents))
  THEN RAISE EXCEPTION 'gas_physical_parent_scope';END IF;
 END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF(catalog,context,at_time) IS DISTINCT FROM(job.catalog_id,job.context_id,job.valuation_at)
   OR(SELECT array_agg(b ORDER BY b) FROM unnest(books)b) IS DISTINCT FROM(SELECT array_agg(value::uuid ORDER BY value::uuid) FROM jsonb_array_elements_text(job.request->'bookIds'))
   OR parents IS NULL AND NULLIF(job.request->'tradeIds','null'::jsonb) IS NOT NULL
   OR parents IS NOT NULL AND EXISTS(SELECT 1 FROM jsonb_array_elements(parents)p WHERE NOT EXISTS(
    SELECT 1 FROM risk.lifecycle_scope_trades(job)s WHERE(s.trade_id,s.binding_id)=((p->>'tradeId')::uuid,(p->>'bindingId')::uuid)))
  THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 SELECT t.known_at,t.effective_on,x.known_through,m.known_through INTO STRICT cut,day,prefix,market_prefix FROM market_data.temporal_contexts t
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND security.trading_context_day_matches(catalog,context,at_time);
 FOR link IN SELECT l.* FROM settlement_data.futures_delivery_obligations l JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.position_id)
  JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  WHERE l.catalog_id=catalog AND a.book_id=ANY(books)
   AND security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')
   AND(parents IS NULL OR EXISTS(SELECT 1 FROM jsonb_array_elements(parents)r WHERE((r->>'tradeId')::uuid,(r->>'bindingId')::uuid)=(l.trade_id,l.binding_id))) ORDER BY l.position_id LOOP
  SELECT * INTO STRICT position FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(catalog,link.position_id);
  SELECT * INTO STRICT profile FROM settlement_data.futures_delivery_profiles p WHERE(p.catalog_id,p.id,p.revision)=(catalog,link.profile_id,link.profile_revision);
  SELECT * INTO STRICT terms FROM trading.gas_futures_delivery_terms t WHERE(t.catalog_id,t.profile_id,t.profile_revision)=(catalog,link.profile_id,link.profile_revision);
  SELECT * INTO STRICT account FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,terms.account_id);
  IF account.id<>position.account_id OR NOT EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,profile.publication_order)
   AND profile.publication_order<=prefix AND k.known_from<=cut)
   OR NOT EXISTS(SELECT 1 FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(a.catalog_id,a.policy_id,a.policy_revision)
    JOIN trading.knowledge_receipts ak ON(ak.catalog_id,ak.publication_order)=(a.catalog_id,a.publication_order)
    JOIN trading.knowledge_receipts pk ON(pk.catalog_id,pk.publication_order)=(p.catalog_id,p.publication_order)
    WHERE(a.catalog_id,a.trade_id,a.binding_id,a.policy_id,a.policy_revision,a.origin_revision)=(catalog,link.trade_id,link.binding_id,link.policy_id,link.policy_revision,link.origin_revision)
     AND a.publication_order<=prefix AND p.publication_order<=prefix AND ak.known_from<=cut AND pk.known_from<=cut)
   OR encode(sha256(convert_to(settlement_data.futures_delivery_profile_value(profile,terms)::text,'UTF8')),'hex')<>profile.terms_hash
  THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
  PERFORM settlement_data.require_futures_delivery_reference(profile,terms);
  PERFORM settlement_data.require_futures_margin_source(catalog,context,at_time,profile.definition_revision_id);
  PERFORM settlement_data.require_futures_margin_source(catalog,context,at_time,account.definition_revision_id);
  SELECT * INTO STRICT latest FROM settlement_data.read_futures_delivery_final(catalog,link.position_id,context,at_time);
  -- Resolve accepted heads by source knowledge before proving each exact
  -- market observation. A missing proof must never select an older final.
  IF EXISTS(SELECT 1 FROM settlement_data.futures_margin_determinations d JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
   WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=(catalog,link.trade_id,link.binding_id,link.final_occurrence_id) AND d.publication_order<=prefix AND k.known_from<=cut
   AND NOT EXISTS(SELECT 1 FROM market_data.observations o JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(o.catalog_id,o.id)
    JOIN market_data.market_knowledge_receipts r ON(r.catalog_id,r.event_order,r.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
    WHERE(o.catalog_id,o.id)=(catalog,d.source_observation_id) AND e.event_order<=market_prefix AND r.known_from<=cut))
  THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
  SELECT coalesce(jsonb_agg(to_jsonb(d)||jsonb_build_object('known_at',k.known_from,'final_price',trim_scale(o.value)::text) ORDER BY d.run_revision),'[]') INTO finals
   FROM settlement_data.futures_margin_determinations d JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
   JOIN market_data.observations o ON(o.catalog_id,o.id)=(d.catalog_id,d.source_observation_id)
   WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=(catalog,link.trade_id,link.binding_id,link.final_occurrence_id) AND d.publication_order<=prefix AND k.known_from<=cut;
  SELECT coalesce(jsonb_agg(to_jsonb(p)||jsonb_build_object('known_at',k.known_from,'quantity',trim_scale(p.quantity)::text) ORDER BY p.performed_at,convert_to(p.business_reference,'UTF8')),'[]') INTO performances
   FROM trading.gas_futures_delivery_performances p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE(p.catalog_id,p.position_id)=(catalog,link.position_id) AND p.publication_order<=prefix AND k.known_from<=cut;
  SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY s.performance_id,s.ordinal),'[]') INTO splits FROM trading.gas_futures_delivery_splits s
   WHERE s.catalog_id=catalog AND EXISTS(SELECT 1 FROM jsonb_array_elements(performances)p WHERE(p->>'id')::uuid=s.performance_id);
  SELECT coalesce(jsonb_agg(to_jsonb(r)||jsonb_build_object('known_at',k.known_from) ORDER BY r.revision),'[]') INTO runs
   FROM trading.gas_futures_delivery_principal_runs r JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
   WHERE(r.catalog_id,r.position_id)=(catalog,link.position_id) AND r.publication_order<=prefix AND k.known_from<=cut;
  SELECT coalesce(jsonb_agg(to_jsonb(d)||jsonb_build_object('cumulative_quantity',trim_scale(d.cumulative_quantity)::text,'rounded_total',trim_scale(d.rounded_total)::text,
   'contribution',trim_scale(d.contribution)::text,'adjustment',trim_scale(d.adjustment)::text) ORDER BY d.run_revision,p.performed_at,convert_to(p.business_reference,'UTF8')),'[]') INTO steps
   FROM trading.gas_futures_delivery_principal_determinations d JOIN trading.gas_futures_delivery_performances p ON(p.catalog_id,p.id)=(d.catalog_id,d.performance_id)
   WHERE(d.catalog_id,d.position_id)=(catalog,link.position_id) AND EXISTS(SELECT 1 FROM jsonb_array_elements(runs)r WHERE(r->>'revision')::integer=d.run_revision);
  SELECT coalesce(jsonb_agg(to_jsonb(c)||jsonb_build_object('known_at',k.known_from,'amount',trim_scale(c.amount)::text) ORDER BY c.id),'[]') INTO cash
   FROM trading.gas_futures_delivery_cash c JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(c.catalog_id,c.publication_order)
   WHERE(c.catalog_id,c.position_id)=(catalog,link.position_id) AND c.publication_order<=prefix AND k.known_from<=cut;
  SELECT coalesce(jsonb_agg(to_jsonb(p)||jsonb_build_object('known_at',k.known_from,'amount',trim_scale(p.amount)::text) ORDER BY p.cash_id,p.business_reference,p.revision),'[]') INTO payments
   FROM trading.gas_futures_delivery_payments p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE p.catalog_id=catalog AND p.publication_order<=prefix AND k.known_from<=cut AND EXISTS(SELECT 1 FROM jsonb_array_elements(cash)c WHERE(c->>'id')::uuid=p.cash_id);
  position_ids=array_append(position_ids,position.id);
  position_ids=position_ids||ARRAY(SELECT(p->>'receipt_lot_id')::uuid FROM jsonb_array_elements(performances)p WHERE p->>'receipt_lot_id' IS NOT NULL);
  position_ids=position_ids||ARRAY(SELECT c.lot_id FROM jsonb_array_elements(splits)s JOIN trading.gas_owned_consumptions c ON(c.catalog_id,c.id)=(catalog,(s->>'consumption_id')::uuid));
  attachments=attachments||jsonb_build_array(jsonb_build_object('link',to_jsonb(link),'position',to_jsonb(position)||jsonb_build_object('quantity',trim_scale(position.quantity)::text,
   'known_at',(SELECT k.known_from FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,position.publication_order))),
   'profile',to_jsonb(profile),'gas',to_jsonb(terms),'account',to_jsonb(account),'final',to_jsonb(latest)||jsonb_build_object('final_price',trim_scale(latest.final_price)::text),
   'finals',finals,'performances',performances,'splits',splits,'runs',runs,'steps',steps,'cash',cash,'payments',payments,
   'occurrence',(SELECT to_jsonb(o) FROM settlement_data.futures_margin_policy_occurrences o WHERE(o.catalog_id,o.policy_id,o.policy_revision,o.id)=(catalog,link.policy_id,link.policy_revision,link.final_occurrence_id))));
 END LOOP;
 -- The native links alone choose the resource closure. Joining one account
 -- does not pull in every independent economic interest in that account.
 LOOP
  SELECT array_agg(DISTINCT id ORDER BY id) INTO next_ids FROM(
   SELECT unnest(position_ids) id UNION ALL
   SELECT l.return_position_id FROM trading.gas_owned_loans l WHERE l.catalog_id=catalog AND l.received_lot_id=ANY(position_ids) UNION ALL
   SELECT l.received_lot_id FROM trading.gas_owned_loans l WHERE l.catalog_id=catalog AND l.return_position_id=ANY(position_ids) UNION ALL
   SELECT s.position_id FROM trading.gas_owned_claim_settlements s WHERE s.catalog_id=catalog AND s.receipt_lot_id=ANY(position_ids)
    AND security.trading_visible(s.catalog_id,s.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00'))r;
  next_ids=coalesce(next_ids,'{}');
  IF cardinality(next_ids)>16384 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
  IF next_ids=position_ids OR cardinality(next_ids)=0 THEN EXIT;END IF;
  position_ids=next_ids;
 END LOOP;
 SELECT coalesce(array_agg(DISTINCT p.account_id),'{}') INTO account_ids FROM trading.gas_owned_positions p WHERE p.catalog_id=catalog AND p.id=ANY(position_ids);
 journal=security.gas_physical_journal_source(catalog,context,at_time,books,position_ids);
 FOR key IN SELECT jsonb_object_keys(journal) LOOP
  IF key IN('accounts','positions','lots','evidence','reservations','releases','consumptions','loans','repayments','settlements','cashoutTerms','fees','payments','markBindings') THEN
   SELECT coalesce(jsonb_agg(value ORDER BY value::text),'[]') INTO item FROM jsonb_array_elements(journal->key) WHERE CASE key
    WHEN 'accounts' THEN(value->>'id')::uuid=ANY(account_ids)
    WHEN 'positions' THEN(value->>'id')::uuid=ANY(position_ids)
    WHEN 'lots' THEN(value->>'id')::uuid=ANY(position_ids)
    WHEN 'evidence' THEN EXISTS(SELECT 1 FROM trading.gas_owned_evidence original WHERE original.catalog_id=catalog
     AND(original.account_id,original.source_key)=((value->>'account_id')::uuid,value->>'source_key') AND(
      EXISTS(SELECT 1 FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.evidence_id)=(catalog,original.id) AND p.id=ANY(position_ids))
      OR EXISTS(SELECT 1 FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.evidence_id)=(catalog,original.id) AND c.lot_id=ANY(position_ids)
       AND security.trading_visible(c.catalog_id,c.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00'))
      OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.evidence_id)=(catalog,original.id) AND s.position_id=ANY(position_ids)
       AND security.trading_visible(s.catalog_id,s.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00'))))
    WHEN 'reservations' THEN(value->>'lot_id')::uuid=ANY(position_ids)
    WHEN 'releases' THEN EXISTS(SELECT 1 FROM trading.gas_owned_reservations r WHERE(r.catalog_id,r.id)=(catalog,(value->>'reservation_id')::uuid) AND r.lot_id=ANY(position_ids))
    WHEN 'consumptions' THEN(value->>'lot_id')::uuid=ANY(position_ids)
    WHEN 'loans' THEN(value->>'return_position_id')::uuid=ANY(position_ids)
    WHEN 'repayments' THEN EXISTS(SELECT 1 FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id)=(catalog,(value->>'loan_id')::uuid) AND l.return_position_id=ANY(position_ids))
    WHEN 'settlements' THEN(value->>'position_id')::uuid=ANY(position_ids)
    WHEN 'cashoutTerms' THEN(value->>'position_id')::uuid=ANY(position_ids)
    WHEN 'fees' THEN(value->>'position_id')::uuid=ANY(position_ids)
    WHEN 'payments' THEN EXISTS(SELECT 1 FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.id)=(catalog,(value->>'fee_id')::uuid) AND f.position_id=ANY(position_ids))
    WHEN 'markBindings' THEN(value->>'position_id')::uuid=ANY(position_ids) ELSE false END;
   journal=jsonb_set(journal,ARRAY[key],item);
  END IF;
 END LOOP;
 journal=jsonb_set(journal,ARRAY['stateProfile'],to_jsonb('known_gas_physical_dependency_journal_v1'::text));
 journal=journal||jsonb_build_object('sourceConflicts',trading.gas_owned_applied_source_conflicts(catalog,context,at_time,books,position_ids)||
  trading.gas_owned_applied_observation_conflicts(catalog,context,at_time,books,position_ids));
 result=jsonb_build_object('stateProfile','known_gas_physical_attachments_v1','contextId',context,'asOf',at_time,'knownAt',cut,'effectiveOn',day,
  'authorityUntil',until_at,'attachments',attachments,'journal',journal-'authorityUntil');
 IF pg_column_size(result)>16777216 OR jsonb_array_length(attachments)>16384 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);RETURN result;
END$$;
ALTER FUNCTION security.read_gas_physical_known_attachments(uuid,uuid,timestamptz,uuid[],jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_physical_known_attachments(uuid,uuid,timestamptz,uuid[],jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_physical_known_attachments(uuid,uuid,timestamptz,uuid[],jsonb) TO openetrm_runtime,openetrm_risk_worker;

-- A corrected operational source is not a replacement title, dispatch, return
-- or cash flow. Retain the accepted effect and report the exact unresolved
-- relationship, including effects whose original stock no longer survives.
CREATE FUNCTION trading.gas_owned_applied_source_conflicts(catalog uuid,context uuid,at_time timestamptz,books uuid[],positions uuid[] DEFAULT NULL) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 WITH accounts AS(SELECT a.* FROM trading.gas_owned_accounts a WHERE a.catalog_id=catalog AND a.book_id=ANY(books)
  AND security.trading_visible(a.catalog_id,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),
 effects AS(
  SELECT 'position'::text kind,p.id effect_id,p.id position_id,p.account_id,p.evidence_id,p.effective_at,p.publication_order
   FROM trading.gas_owned_positions p JOIN accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
   WHERE(positions IS NULL OR p.id=ANY(positions)) AND security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')
  UNION ALL SELECT 'consumption',c.id,c.lot_id,p.account_id,c.evidence_id,c.effective_at,c.publication_order
   FROM trading.gas_owned_consumptions c JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(c.catalog_id,c.lot_id)
   JOIN accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
   WHERE(positions IS NULL OR p.id=ANY(positions)) AND security.trading_visible(c.catalog_id,c.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')
  UNION ALL SELECT 'settlement',s.id,s.position_id,p.account_id,s.evidence_id,s.effective_at,s.publication_order
   FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)
   JOIN accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
   WHERE(positions IS NULL OR p.id=ANY(positions)) AND security.trading_visible(s.catalog_id,s.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),
 heads AS(SELECT DISTINCT ON(e.account_id,e.source_key)e.* FROM trading.gas_owned_evidence e JOIN accounts a ON(a.catalog_id,a.id)=(e.catalog_id,e.account_id)
  WHERE security.trading_visible(e.catalog_id,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00') ORDER BY e.account_id,e.source_key,e.revision DESC)
 SELECT coalesce(jsonb_agg(jsonb_build_object('kind',f.kind,'effectId',f.effect_id,'positionId',f.position_id,'accountId',f.account_id,
  'sourceKind','gas_evidence','acceptedSourceId',original.id,'currentSourceIds',jsonb_build_array(h.id),
  'effectiveAt',f.effective_at,'publicationOrder',f.publication_order,'knownAt',fk.known_from)
  ORDER BY f.kind,f.effect_id),'[]')
 FROM effects f JOIN trading.gas_owned_evidence original ON(original.catalog_id,original.id)=(catalog,f.evidence_id)
 JOIN heads h ON(h.account_id,h.source_key)=(original.account_id,original.source_key)
 JOIN trading.knowledge_receipts fk ON(fk.catalog_id,fk.publication_order)=(catalog,f.publication_order)
 JOIN trading.knowledge_receipts hk ON(hk.catalog_id,hk.publication_order)=(catalog,h.publication_order)
 WHERE h.revision>original.revision;
$$;
ALTER FUNCTION trading.gas_owned_applied_source_conflicts(uuid,uuid,timestamptz,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_owned_applied_source_conflicts(uuid,uuid,timestamptz,uuid[],uuid[]) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION trading.gas_owned_applied_observation_conflicts(catalog uuid,context uuid,at_time timestamptz,books uuid[],positions uuid[] DEFAULT NULL) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 WITH cut AS(SELECT t.known_at,m.known_through FROM market_data.temporal_contexts t JOIN market_data.market_contexts m
  ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(catalog,context)),
 applied AS(SELECT s.*,p.account_id FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)
  JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  WHERE s.catalog_id=catalog AND a.book_id=ANY(books) AND s.source_observation_id IS NOT NULL AND(positions IS NULL OR p.id=ANY(positions))
   AND security.trading_visible(s.catalog_id,s.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),
 comparisons AS(SELECT s.*,heads.ids FROM applied s JOIN market_data.observations original ON(original.catalog_id,original.id)=(s.catalog_id,s.source_observation_id)
  CROSS JOIN LATERAL(SELECT coalesce(jsonb_agg(o.id ORDER BY o.id),'[]') ids FROM market_data.observations o
   JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(o.catalog_id,o.id)
   JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid) CROSS JOIN cut
   WHERE(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code,o.role,o.observed_at)=
    (catalog,original.definition_revision_id,original.series_id,original.source_code,original.role,original.observed_at)
    AND e.event_order<=cut.known_through AND k.known_from<=cut.known_at AND NOT EXISTS(
     SELECT 1 FROM market_data.observations n JOIN market_data.knowledge_events ne ON(ne.catalog_id,ne.observation_id)=(n.catalog_id,n.id)
     JOIN market_data.market_knowledge_receipts nk ON(nk.catalog_id,nk.event_order,nk.publication_xid)=(ne.catalog_id,ne.event_order,ne.publication_xid)
     WHERE(n.catalog_id,n.corrects_id)=(o.catalog_id,o.id) AND ne.event_order<=cut.known_through AND nk.known_from<=cut.known_at))heads)
 SELECT coalesce(jsonb_agg(jsonb_build_object('kind','settlement','effectId',s.id,'positionId',s.position_id,'accountId',s.account_id,
  'sourceKind','market_observation','acceptedSourceId',s.source_observation_id,'currentSourceIds',s.ids,
  'effectiveAt',s.effective_at,'publicationOrder',s.publication_order,'knownAt',k.known_from) ORDER BY s.id),'[]')
 FROM comparisons s JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
 WHERE s.ids IS DISTINCT FROM jsonb_build_array(s.source_observation_id);
$$;
ALTER FUNCTION trading.gas_owned_applied_observation_conflicts(uuid,uuid,timestamptz,uuid[],uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_owned_applied_observation_conflicts(uuid,uuid,timestamptz,uuid[],uuid[]) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION security.read_gas_owned_applied_sources(catalog uuid,context uuid,at_time timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source jsonb;result jsonb;until_at timestamptz;BEGIN
 -- The existing public source entry point supplies current authority, exact
 -- whole-book job scope and the original deadline before any follow-up read.
 source=security.read_gas_owned_known_history(catalog,context,at_time,books);
 until_at=(source->>'authorityUntil')::timestamptz;
 result=(source-ARRAY['accounts','positions','lots','evidence','reservations','releases','consumptions','loans','repayments','settlements','cashoutTerms','fees','payments','markBindings'])||
  jsonb_build_object('stateProfile','known_gas_applied_source_conflicts_v1','conflicts',trading.gas_owned_applied_source_conflicts(catalog,context,at_time,books)||trading.gas_owned_applied_observation_conflicts(catalog,context,at_time,books));
 IF pg_column_size(result)>8388608 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);RETURN result;
END$$;
ALTER FUNCTION security.read_gas_owned_applied_sources(uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_applied_sources(uuid,uuid,timestamptz,uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_applied_sources(uuid,uuid,timestamptz,uuid[]) TO openetrm_runtime,openetrm_risk_worker;

COMMIT;
