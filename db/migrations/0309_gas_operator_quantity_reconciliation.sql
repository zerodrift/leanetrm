-- Source preparation for exact operator entitlement and settlement counterparts.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

-- One fixed row projection serves streamed size admission and the final result.
-- Future-effective accepted facts stay known, in original settlement order.
CREATE FUNCTION trading.gas_operator_reconciliation_rows(catalog uuid,claim uuid,context uuid,as_of timestamptz,statistics boolean) RETURNS jsonb
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE rows_sql constant text=$rows$
 WITH chosen AS MATERIALIZED(
  SELECT p.*,q.revision financial_revision,origin.quantity original_quantity,origin.publication_order original_publication_order,k.known_from
  FROM trading.gas_owned_positions_at($1,$3,$4)p
  JOIN trading.gas_owned_positions origin ON(origin.catalog_id,origin.id)=(p.catalog_id,p.id)
  JOIN trading.gas_owned_position_quantities q ON(q.catalog_id,q.position_id,q.publication_order)=(p.catalog_id,p.id,p.publication_order)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE(p.catalog_id,p.id,p.kind)=($1,$2,'operator_claim')),
 settlements AS MATERIALIZED(SELECT s.*,k.known_from FROM trading.gas_owned_claim_settlements s
  JOIN chosen p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
  WHERE trading.gas_owned_visible($1,s.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)),
 lot_ids AS(SELECT receipt_lot_id id FROM settlements WHERE receipt_lot_id IS NOT NULL
  UNION SELECT c.lot_id FROM trading.gas_owned_consumptions_at($1,$3,$4)c WHERE c.id IN(SELECT consumption_id FROM settlements)
  UNION SELECT c.lot_id FROM trading.gas_owned_consumptions c WHERE c.catalog_id=$1 AND c.id IN(SELECT consumption_id FROM settlements)),
 uses AS MATERIALIZED(SELECT c.* FROM trading.gas_owned_consumptions_at($1,$3,$4)c
  WHERE c.lot_id IN(SELECT id FROM lot_ids) OR c.id IN(SELECT consumption_id FROM settlements)
   OR c.id IN(SELECT o.id FROM trading.gas_owned_consumptions o WHERE o.catalog_id=$1 AND o.lot_id IN(SELECT id FROM lot_ids))),
 positions AS MATERIALIZED(SELECT p.*,q.revision financial_revision,o.publication_order original_publication_order,k.known_from
  FROM trading.gas_owned_positions_at($1,$3,$4)p
  JOIN trading.gas_owned_positions o ON(o.catalog_id,o.id)=(p.catalog_id,p.id)
  JOIN trading.gas_owned_position_quantities q ON(q.catalog_id,q.position_id,q.publication_order)=(p.catalog_id,p.id,p.publication_order)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE p.id IN(SELECT id FROM lot_ids UNION SELECT lot_id FROM uses)),
 reservations AS MATERIALIZED(SELECT r.* FROM trading.gas_owned_reservations r WHERE r.catalog_id=$1 AND r.lot_id IN(SELECT id FROM positions)
  AND trading.gas_owned_visible($1,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)),
 cash AS MATERIALIZED(SELECT f.* FROM trading.gas_owned_fees f JOIN chosen p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id)
  WHERE trading.gas_owned_visible($1,f.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)),
 required_evidence AS(SELECT evidence_id id FROM chosen UNION SELECT evidence_id FROM settlements UNION SELECT evidence_id FROM positions
  UNION SELECT evidence_id FROM uses UNION SELECT o.evidence_id FROM trading.gas_owned_positions o WHERE o.catalog_id=$1 AND o.id IN(SELECT id FROM chosen UNION SELECT id FROM positions)
  UNION SELECT o.evidence_id FROM trading.gas_owned_consumptions o WHERE o.catalog_id=$1 AND o.id IN(SELECT id FROM uses)),
 keys AS(SELECT DISTINCT e.account_id,e.source_key FROM trading.gas_owned_evidence e WHERE e.catalog_id=$1 AND e.id IN(SELECT id FROM required_evidence)),
 allocations AS MATERIALIZED(
  SELECT 'position' allocation_kind,p.id,p.account_id,p.kind,p.quantity,p.evidence_id,q.revision financial_revision
  FROM trading.gas_owned_positions_at($1,$3,$4)p JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.evidence_id)
  JOIN keys s ON(s.account_id,s.source_key)=(e.account_id,e.source_key)
  JOIN trading.gas_owned_position_quantities q ON(q.catalog_id,q.position_id,q.publication_order)=(p.catalog_id,p.id,p.publication_order)
  UNION ALL SELECT 'consumption',c.id,p.account_id,c.kind,c.quantity,c.evidence_id,q.revision
  FROM trading.gas_owned_consumptions_at($1,$3,$4)c JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(c.catalog_id,c.evidence_id)
  JOIN keys s ON(s.account_id,s.source_key)=(e.account_id,e.source_key)
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(c.catalog_id,c.lot_id)
  JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.publication_order)=(c.catalog_id,c.id,c.publication_order)),
 evidence_ids AS(SELECT id FROM required_evidence UNION SELECT evidence_id FROM allocations),
 heads AS(SELECT h.id FROM keys s CROSS JOIN LATERAL(SELECT e.id FROM trading.gas_owned_evidence e
  WHERE(e.catalog_id,e.account_id,e.source_key)=($1,s.account_id,s.source_key)
   AND trading.gas_owned_visible($1,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4) ORDER BY e.revision DESC LIMIT 1)h),
 points AS(SELECT DISTINCT o.definition_revision_id,o.series_id,o.source_code,o.role,o.observed_at
  FROM market_data.observations o WHERE o.catalog_id=$1 AND o.id IN(SELECT source_observation_id FROM settlements)),
 observed AS MATERIALIZED(SELECT o.*,k.known_from FROM market_data.observations o JOIN points p
  ON(p.definition_revision_id,p.series_id,p.source_code,p.role,p.observed_at)=(o.definition_revision_id,o.series_id,o.source_code,o.role,o.observed_at)
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(o.catalog_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.market_contexts m ON m.catalog_id=$1 AND m.context_id=$3
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=($1,$3)
  WHERE o.catalog_id=$1 AND e.event_order<=m.known_through AND k.known_from<=t.known_at)
 SELECT 'claim' kind,to_jsonb(p)||jsonb_build_object('quantity',trim_scale(p.quantity)::text,'evidenceId',p.evidence_id,'originalQuantity',trim_scale(p.original_quantity)::text,
  'expectedRevision',p.financial_revision,'originalPublicationOrder',p.original_publication_order,'knownAt',p.known_from,
  'remainingQuantity',trim_scale(CASE WHEN p.effective_at>$4 THEN 0 ELSE p.quantity-sign(p.quantity)*coalesce((
   SELECT sum(s.quantity) FROM settlements s WHERE s.effective_at<=$4),0) END)::text) value FROM chosen p
 UNION ALL SELECT 'cashoutTerms',to_jsonb(t) FROM trading.gas_owned_cashout_terms t JOIN chosen p ON(p.catalog_id,p.id)=(t.catalog_id,t.position_id)
 UNION ALL SELECT 'settlements',to_jsonb(s)||jsonb_build_object('quantity',trim_scale(s.quantity)::text,'evidenceId',s.evidence_id,'expectedRevision',1,
  'originalPublicationOrder',s.publication_order,'effectiveAt',s.effective_at,'knownAt',s.known_from,
  'receiptLotId',s.receipt_lot_id,'consumptionId',s.consumption_id,'cashId',s.cash_id,'sourceObservationId',s.source_observation_id) FROM settlements s
 UNION ALL SELECT 'positions',to_jsonb(p)||jsonb_build_object('quantity',trim_scale(p.quantity)::text,'evidenceId',p.evidence_id,'expectedRevision',p.financial_revision,
  'originalPublicationOrder',p.original_publication_order,'knownAt',p.known_from) FROM positions p
 UNION ALL SELECT 'consumptions',to_jsonb(c)||jsonb_build_object('quantity',trim_scale(c.quantity)::text,'evidenceId',c.evidence_id,'expectedRevision',q.revision,
  'lotId',c.lot_id,'effectiveAt',c.effective_at,'originalPublicationOrder',o.publication_order,'knownAt',k.known_from) FROM uses c
  JOIN trading.gas_owned_consumptions o ON(o.catalog_id,o.id)=(c.catalog_id,c.id)
  JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.publication_order)=(c.catalog_id,c.id,c.publication_order)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(c.catalog_id,c.publication_order)
 UNION ALL SELECT 'reservations',to_jsonb(r)||jsonb_build_object('quantity',trim_scale(r.quantity)::text) FROM reservations r
 UNION ALL SELECT 'releases',to_jsonb(r) FROM trading.gas_owned_releases r WHERE r.catalog_id=$1 AND r.reservation_id IN(SELECT id FROM reservations)
  AND trading.gas_owned_visible($1,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)
 UNION ALL SELECT 'cash',to_jsonb(f)||jsonb_build_object('amount',trim_scale(f.amount)::text) FROM cash f
 UNION ALL SELECT 'payments',to_jsonb(p)||jsonb_build_object('amount',trim_scale(p.amount)::text) FROM trading.gas_owned_fee_payments p
  WHERE p.catalog_id=$1 AND p.fee_id IN(SELECT id FROM cash)
   AND trading.gas_owned_visible($1,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)
 UNION ALL SELECT 'allocations',to_jsonb(a)||jsonb_build_object('quantity',trim_scale(a.quantity)::text) FROM allocations a
 UNION ALL SELECT 'evidence',to_jsonb(e)||jsonb_build_object('quantity',trim_scale(e.quantity)::text,'knownAt',k.known_from,'capturedHead',e.id IN(SELECT id FROM heads))
  FROM trading.gas_owned_evidence e JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
  WHERE e.catalog_id=$1 AND(e.id IN(SELECT id FROM evidence_ids) OR e.id IN(SELECT id FROM heads))
   AND trading.gas_owned_visible($1,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)
 UNION ALL SELECT 'observations',to_jsonb(o)||jsonb_build_object('value',trim_scale(o.value)::text,'knownAt',o.known_from,
  'capturedHead',NOT EXISTS(SELECT 1 FROM observed n WHERE n.corrects_id=o.id)) FROM observed o
  WHERE o.id IN(SELECT source_observation_id FROM settlements) OR NOT EXISTS(SELECT 1 FROM observed n WHERE n.corrects_id=o.id)
 UNION ALL SELECT 'accounts',to_jsonb(a) FROM trading.gas_owned_accounts a WHERE a.catalog_id=$1
  AND a.id IN(SELECT account_id FROM chosen UNION SELECT account_id FROM positions UNION SELECT account_id FROM allocations)
$rows$;result jsonb;BEGIN
 IF statistics IS NULL THEN RAISE EXCEPTION 'gas_operator_quantity_input';END IF;
 IF statistics THEN
  EXECUTE 'WITH rows AS('||rows_sql||'),sizes AS(SELECT kind,count(*) n,sum(octet_length(convert_to(value::text,''UTF8''))) bytes FROM rows GROUP BY kind)
   SELECT jsonb_build_object(''counts'',coalesce(jsonb_object_agg(kind,n),''{}''),''bytes'',coalesce(sum(bytes),0),''count'',coalesce(sum(n),0)) FROM sizes'
   INTO result USING catalog,claim,context,as_of;
 ELSE
  EXECUTE 'SELECT jsonb_build_object(''claim'',(jsonb_agg(value) FILTER(WHERE kind=''claim''))->0,
   ''cashoutTerms'',(jsonb_agg(value) FILTER(WHERE kind=''cashoutTerms''))->0,
   ''settlements'',coalesce(jsonb_agg(value ORDER BY (value->>''originalPublicationOrder'')::integer) FILTER(WHERE kind=''settlements''),''[]''),
   ''positions'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''positions''),''[]''),
   ''consumptions'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''consumptions''),''[]''),
   ''reservations'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''reservations''),''[]''),
   ''releases'',coalesce(jsonb_agg(value ORDER BY value->>''reservation_id'') FILTER(WHERE kind=''releases''),''[]''),
   ''cash'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''cash''),''[]''),
   ''payments'',coalesce(jsonb_agg(value ORDER BY value->>''fee_id'',value->>''business_key'',(value->>''revision'')::integer) FILTER(WHERE kind=''payments''),''[]''),
   ''allocations'',coalesce(jsonb_agg(value ORDER BY value->>''allocation_kind'',value->>''id'') FILTER(WHERE kind=''allocations''),''[]''),
   ''evidence'',coalesce(jsonb_agg(value ORDER BY value->>''account_id'',value->>''source_key'',(value->>''revision'')::integer) FILTER(WHERE kind=''evidence''),''[]''),
   ''observations'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''observations''),''[]''),
   ''accounts'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''accounts''),''[]'')) FROM('||rows_sql||')rows'
   INTO result USING catalog,claim,context,as_of;
 END IF;RETURN result;
END$$;

CREATE FUNCTION security.read_gas_operator_quantity_reconciliation(catalog uuid,claim uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;selected trading.gas_owned_positions;owner trading.gas_owned_accounts;stats jsonb;entry record;account jsonb;result jsonb;BEGIN
 until_at=security.gas_futures_delivery_discovery_authority(catalog);
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 SELECT * INTO STRICT selected FROM trading.gas_owned_positions_at(catalog,context,as_of)p
  WHERE(p.catalog_id,p.id,p.kind)=(read_gas_operator_quantity_reconciliation.catalog,read_gas_operator_quantity_reconciliation.claim,'operator_claim');
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(read_gas_operator_quantity_reconciliation.catalog,selected.account_id)
  AND trading.gas_owned_visible(a.catalog_id,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of);
 IF security.reference_version_visible(owner.reference_catalog_id,owner.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,owner.definition_revision_id,NULL) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,owner.definition_revision_id);
 PERFORM trading.require_gas_owned_quantity_head(catalog,owner.id,context,as_of);
 stats=trading.gas_operator_reconciliation_rows(catalog,claim,context,as_of,true);
 FOR entry IN SELECT key,value FROM jsonb_each(stats->'counts') LOOP
  IF entry.key NOT IN('claim','cashoutTerms','accounts','settlements','positions','consumptions','reservations','releases','cash','payments','allocations','evidence','observations')
   OR(entry.value#>>'{}')::bigint NOT BETWEEN 0 AND 16384 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 END LOOP;
 IF coalesce((stats#>>'{counts,claim}')::integer,0)<>1
  OR coalesce((stats#>>'{counts,cashoutTerms}')::integer,0)<>(CASE WHEN selected.settlement_mode='cashout_final_price_v1' THEN 1 ELSE 0 END)
  OR(stats->>'bytes')::bigint+4*(stats->>'count')::bigint+4096>16777216
 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 result=jsonb_build_object('implementation','gas_operator_quantity_reconciliation_source_v1','scope','gas_operator_reconciliation_dependency_facts_v1',
  'claimId',claim,'accountId',owner.id,'contextId',context,'asOf',as_of,'authorityUntil',until_at)
  ||trading.gas_operator_reconciliation_rows(catalog,claim,context,as_of,false);
 IF EXISTS(SELECT 1 FROM jsonb_array_elements((result->'positions')||(result->'consumptions')||(result->'settlements')||jsonb_build_array(result->'claim')) s
  WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(result->'evidence') e WHERE e->>'id'=s->>'evidenceId'))
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(result->'settlements') s WHERE s->>'sourceObservationId' IS NOT NULL AND NOT EXISTS(
   SELECT 1 FROM jsonb_array_elements(result->'observations') o WHERE o->>'id'=s->>'sourceObservationId'))
 THEN RAISE EXCEPTION 'gas_operator_quantity_source';END IF;
 FOR account IN SELECT value FROM jsonb_array_elements(result->'accounts') LOOP
  IF security.reference_version_visible((account->>'reference_catalog_id')::uuid,(account->>'reference_revision_id')::uuid) IS NOT TRUE
   OR security.market_fact_visible(catalog,(account->>'definition_revision_id')::uuid,NULL) IS NOT TRUE
   OR NOT trading.gas_owned_visible(catalog,(account->>'publication_order')::integer,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
  THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 END LOOP;
 IF octet_length(convert_to(result::text,'UTF8'))>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 RETURN result;
EXCEPTION WHEN NO_DATA_FOUND THEN
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 RAISE EXCEPTION 'gas_operator_quantity_source';
END$$;

ALTER FUNCTION trading.gas_operator_reconciliation_rows(uuid,uuid,uuid,timestamptz,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_operator_reconciliation_rows(uuid,uuid,uuid,timestamptz,boolean) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
ALTER FUNCTION security.read_gas_operator_quantity_reconciliation(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_operator_quantity_reconciliation(uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.read_gas_operator_quantity_reconciliation(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
