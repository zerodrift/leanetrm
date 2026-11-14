-- Observed ordinary gas corrections retain exact native event identities.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

INSERT INTO lifecycle.action_kinds(code,automatic,configuration_permission,execution_permission)
 VALUES('gas.owned.reconcile_quantity',false,'ConfigureGasOwnedHoldings','RecordGasOwnedHoldings');
CREATE TABLE trading.gas_owned_quantity_corrections(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,account_id uuid NOT NULL,publication_order integer NOT NULL,
 source_context_id uuid NOT NULL,as_of ref_data.instant NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 implementation text NOT NULL CHECK(implementation='gas_owned_quantity_reconciliation_v1'),
 PRIMARY KEY(catalog_id,operation_id),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_owned_commands(catalog_id,request_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE trading.gas_owned_quantity_position_members(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,position_id uuid NOT NULL,revision integer NOT NULL,changed boolean NOT NULL,
 source_allocation text NOT NULL CHECK(source_allocation IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')),
 PRIMARY KEY(catalog_id,operation_id,position_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_owned_quantity_corrections(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,position_id,revision) REFERENCES trading.gas_owned_position_quantities(catalog_id,position_id,revision)
);
CREATE TABLE trading.gas_owned_quantity_consumption_members(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,consumption_id uuid NOT NULL,revision integer NOT NULL,changed boolean NOT NULL,
 source_allocation text NOT NULL CHECK(source_allocation IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')),
 PRIMARY KEY(catalog_id,operation_id,consumption_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_owned_quantity_corrections(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,consumption_id,revision) REFERENCES trading.gas_owned_consumption_quantities(catalog_id,consumption_id,revision)
);
CREATE TABLE trading.gas_owned_quantity_source_members(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,account_id uuid NOT NULL,source_evidence_id uuid NOT NULL,previous_evidence_id uuid NOT NULL,
 source_allocation text NOT NULL CHECK(source_allocation IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')),
 PRIMARY KEY(catalog_id,operation_id,source_evidence_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_owned_quantity_corrections(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,account_id,source_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,account_id,previous_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id)
);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_owned_quantity_corrections','gas_owned_quantity_position_members','gas_owned_quantity_consumption_members','gas_owned_quantity_source_members'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;
CREATE TRIGGER native_publication BEFORE INSERT ON trading.gas_owned_quantity_corrections FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication();

CREATE FUNCTION trading.gas_owned_position_quantity_snapshot(catalog uuid,body jsonb) RETURNS integer
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT q.revision FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id,q.quantity,q.evidence_id,q.retracted)=
  ($1,($2->>'positionId')::uuid,trading.gas_owned_quantity($2->>'quantity'),($2->>'evidenceId')::uuid,($2->>'retracted')::boolean)
  AND(q.revision=1 AND $2->>'sourceAllocation'='preserve_unallocated_source_quantity_v1' OR EXISTS(
   SELECT 1 FROM trading.gas_owned_quantity_position_members m WHERE(m.catalog_id,m.position_id,m.revision,m.source_allocation)=
    (q.catalog_id,q.position_id,q.revision,$2->>'sourceAllocation'))) ORDER BY q.revision LIMIT 1
$$;
CREATE FUNCTION trading.gas_owned_consumption_quantity_snapshot(catalog uuid,body jsonb) RETURNS integer
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT q.revision FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id,q.lot_id,q.quantity,q.evidence_id,q.retracted)=
  ($1,($2->>'consumptionId')::uuid,($2->>'lotId')::uuid,trading.gas_owned_quantity($2->>'quantity'),($2->>'evidenceId')::uuid,($2->>'retracted')::boolean)
  AND q.reservation_id IS NOT DISTINCT FROM($2->>'reservationId')::uuid
  AND(q.revision=1 AND $2->>'sourceAllocation'='preserve_unallocated_source_quantity_v1' OR EXISTS(
   SELECT 1 FROM trading.gas_owned_quantity_consumption_members m WHERE(m.catalog_id,m.consumption_id,m.revision,m.source_allocation)=
    (q.catalog_id,q.consumption_id,q.revision,$2->>'sourceAllocation'))) ORDER BY q.revision LIMIT 1
$$;

CREATE FUNCTION trading.gas_owned_quantity_terms(body jsonb) RETURNS jsonb
 LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE item jsonb;position_rows jsonb='[]';consumption_rows jsonb='[]';BEGIN
 PERFORM trading.gas_owned_projection_keys(body,ARRAY['positions','consumptions','evidence'],ARRAY[]::text[]);
 IF jsonb_typeof(body->'positions') IS DISTINCT FROM 'array' OR jsonb_typeof(body->'consumptions') IS DISTINCT FROM 'array'
  OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 2000
  OR pg_column_size(body)>8388608 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 IF jsonb_array_length(body->'positions')+jsonb_array_length(body->'consumptions') NOT BETWEEN 1 AND 16384 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'positions') LOOP
  PERFORM trading.gas_owned_projection_keys(item,ARRAY['positionId','expectedRevision','quantity','retracted','evidenceId','sourceAllocation'],ARRAY[]::text[]);
  IF jsonb_typeof(item->'positionId') IS DISTINCT FROM 'string' OR(item->>'positionId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR jsonb_typeof(item->'evidenceId') IS DISTINCT FROM 'string' OR(item->>'evidenceId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR jsonb_typeof(item->'expectedRevision') IS DISTINCT FROM 'number' OR item->>'expectedRevision'!~'^[1-9][0-9]{0,8}$'
   OR jsonb_typeof(item->'quantity') IS DISTINCT FROM 'string' OR jsonb_typeof(item->'retracted') IS DISTINCT FROM 'boolean'
   OR(item->>'retracted')::boolean IS DISTINCT FROM(trading.gas_owned_quantity(item->>'quantity')=0)
   OR item->>'sourceAllocation' IS NULL OR item->>'sourceAllocation' NOT IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')
  THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
  position_rows=position_rows||jsonb_build_array(item||jsonb_build_object('quantity',trim_scale(trading.gas_owned_quantity(item->>'quantity'))::text));
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  PERFORM trading.gas_owned_projection_keys(item,ARRAY['consumptionId','expectedRevision','lotId','quantity','retracted','evidenceId','reservationId','sourceAllocation'],ARRAY['reservationId']);
  IF jsonb_typeof(item->'consumptionId') IS DISTINCT FROM 'string' OR(item->>'consumptionId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR jsonb_typeof(item->'lotId') IS DISTINCT FROM 'string' OR(item->>'lotId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR jsonb_typeof(item->'evidenceId') IS DISTINCT FROM 'string' OR(item->>'evidenceId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR jsonb_typeof(item->'expectedRevision') IS DISTINCT FROM 'number' OR item->>'expectedRevision'!~'^[1-9][0-9]{0,8}$'
   OR jsonb_typeof(item->'quantity') IS DISTINCT FROM 'string' OR jsonb_typeof(item->'retracted') IS DISTINCT FROM 'boolean'
   OR trading.gas_owned_quantity(item->>'quantity')<0 OR(item->>'retracted')::boolean IS DISTINCT FROM(trading.gas_owned_quantity(item->>'quantity')=0)
   OR item->>'reservationId' IS NOT NULL AND(jsonb_typeof(item->'reservationId') IS DISTINCT FROM 'string'
    OR(item->>'reservationId')::uuid='00000000-0000-0000-0000-000000000000'::uuid)
   OR item->>'sourceAllocation' IS NULL OR item->>'sourceAllocation' NOT IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')
  THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
  consumption_rows=consumption_rows||jsonb_build_array(item||jsonb_build_object('quantity',trim_scale(trading.gas_owned_quantity(item->>'quantity'))::text));
 END LOOP;
 IF jsonb_array_length(position_rows)<>(SELECT count(DISTINCT(value->>'positionId')::uuid) FROM jsonb_array_elements(position_rows))
  OR jsonb_array_length(consumption_rows)<>(SELECT count(DISTINCT(value->>'consumptionId')::uuid) FROM jsonb_array_elements(consumption_rows))
 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 RETURN jsonb_build_object('positions',coalesce((SELECT jsonb_agg(value ORDER BY(value->>'positionId')::uuid) FROM jsonb_array_elements(position_rows)),'[]'),
  'consumptions',coalesce((SELECT jsonb_agg(value ORDER BY(value->>'consumptionId')::uuid) FROM jsonb_array_elements(consumption_rows)),'[]'),'evidence',body->>'evidence');
END$$;
-- Input translation belongs at the runtime boundary. The deferred seal uses
-- the same pure validator without opening an exception subtransaction during
-- COMMIT, including when original authority has just expired.
CREATE FUNCTION trading.gas_owned_quantity_fact(body jsonb) RETURNS jsonb
 LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 RETURN trading.gas_owned_quantity_terms(body);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'gas_quantity_input';
END$$;

-- The same closed projection supplies admission statistics and the result.
-- Only the fixed aggregate changes; all source values remain SQL parameters.
CREATE FUNCTION trading.gas_owned_reconciliation_source_rows(catalog uuid,account uuid,context uuid,as_of timestamptz,
 related uuid[],selected_positions uuid[],selected_consumptions uuid[],proposed_evidence uuid[],statistics boolean) RETURNS jsonb
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE rows_sql constant text=$rows$
 WITH positions AS MATERIALIZED(
  SELECT p.*,q.revision financial_revision,q.retracted,k.known_from,to_jsonb(origin) original,origin.publication_order original_publication
  FROM trading.gas_owned_positions_at($1,$3,$4) p JOIN trading.gas_owned_positions origin ON(origin.catalog_id,origin.id)=(p.catalog_id,p.id)
  JOIN trading.gas_owned_position_quantities q ON(q.catalog_id,q.position_id,q.publication_order)=(p.catalog_id,p.id,p.publication_order)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(q.catalog_id,q.publication_order) WHERE p.id=ANY($5)),
 consumptions AS MATERIALIZED(
  SELECT c.*,q.revision financial_revision,q.retracted,k.known_from,to_jsonb(origin) original,origin.publication_order original_publication
  FROM trading.gas_owned_consumptions_at($1,$3,$4) c JOIN trading.gas_owned_consumptions origin ON(origin.catalog_id,origin.id)=(c.catalog_id,c.id)
  JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.publication_order)=(c.catalog_id,c.id,c.publication_order)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(q.catalog_id,q.publication_order) WHERE c.lot_id=ANY($5) OR c.id=ANY($7)),
 required AS(SELECT evidence_id id FROM positions UNION SELECT evidence_id FROM consumptions UNION SELECT unnest($8::uuid[])),
 source_keys AS(SELECT DISTINCT d.source_key FROM trading.gas_owned_evidence d WHERE(d.catalog_id,d.account_id)=($1,$2) AND d.id IN(SELECT id FROM required)),
 heads AS(SELECT latest.id FROM source_keys k CROSS JOIN LATERAL(SELECT e.id FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=($1,$2,k.source_key)
  AND trading.gas_owned_visible(e.catalog_id,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4) ORDER BY e.revision DESC LIMIT 1)latest),
 evidence AS(
  SELECT e.*,k.known_from FROM trading.gas_owned_evidence e JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
  WHERE(e.catalog_id,e.account_id)=($1,$2) AND(e.id IN(SELECT id FROM required) OR e.id IN(SELECT id FROM heads))
   AND trading.gas_owned_visible(e.catalog_id,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)),
 reservations AS MATERIALIZED(SELECT r.* FROM trading.gas_owned_reservations r WHERE r.catalog_id=$1 AND r.lot_id=ANY($5)
  AND trading.gas_owned_visible(r.catalog_id,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4))
 SELECT 'positions' kind,jsonb_build_object('positionId',p.id,'currentRevision',p.financial_revision,'currentQuantity',trim_scale(p.quantity)::text,
  'retracted',p.retracted,'evidenceId',p.evidence_id,'selected',p.id=ANY($6),'position',p.original,
  'originalPublicationOrder',p.original_publication,'publicationOrder',p.publication_order,'knownAt',p.known_from) value FROM positions p
 UNION ALL SELECT 'consumptions',jsonb_build_object('consumptionId',c.id,'currentRevision',c.financial_revision,'currentQuantity',trim_scale(c.quantity)::text,
  'retracted',c.retracted,'lotId',c.lot_id,'evidenceId',c.evidence_id,'reservationId',c.reservation_id,'selected',c.id=ANY($7),'consumption',c.original,
  'originalPublicationOrder',c.original_publication,'publicationOrder',c.publication_order,'knownAt',c.known_from) FROM consumptions c
 UNION ALL SELECT 'evidence',(to_jsonb(e)-'known_from')||jsonb_build_object('quantity',trim_scale(e.quantity)::text,'known_at',e.known_from)
  FROM evidence e WHERE e.id IN(SELECT id FROM required) OR e.id IN(SELECT id FROM heads)
 UNION ALL SELECT 'reservations',to_jsonb(r) FROM reservations r
 UNION ALL SELECT 'releases',to_jsonb(r) FROM trading.gas_owned_releases r WHERE r.catalog_id=$1
  AND r.reservation_id IN(SELECT id FROM reservations) AND trading.gas_owned_visible(r.catalog_id,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)
$rows$;result jsonb;BEGIN
 IF statistics IS NULL THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 IF statistics THEN
  EXECUTE 'WITH source AS('||rows_sql||'),sizes AS(SELECT kind,count(*) n,sum(pg_column_size(value)) bytes FROM source GROUP BY kind)
   SELECT jsonb_build_object(''counts'',coalesce(jsonb_object_agg(kind,n),''{}''),''bytes'',coalesce(sum(bytes),0),''count'',coalesce(sum(n),0)) FROM sizes'
   INTO result USING catalog,account,context,as_of,related,selected_positions,selected_consumptions,proposed_evidence;
 ELSE
  EXECUTE 'SELECT jsonb_build_object(
   ''positions'',coalesce(jsonb_agg(value ORDER BY value->>''positionId'') FILTER(WHERE kind=''positions''),''[]''),
   ''consumptions'',coalesce(jsonb_agg(value ORDER BY value->>''consumptionId'') FILTER(WHERE kind=''consumptions''),''[]''),
   ''evidence'',coalesce(jsonb_agg(value ORDER BY value->>''source_key'',(value->>''revision'')::integer) FILTER(WHERE kind=''evidence''),''[]''),
   ''reservations'',coalesce(jsonb_agg(value ORDER BY value->>''effective_at'',value->>''id'') FILTER(WHERE kind=''reservations''),''[]''),
   ''releases'',coalesce(jsonb_agg(value ORDER BY value->>''effective_at'',value->>''reservation_id'') FILTER(WHERE kind=''releases''),''[]'')) FROM('||rows_sql||')source'
   INTO result USING catalog,account,context,as_of,related,selected_positions,selected_consumptions,proposed_evidence;
 END IF;RETURN result;
END$$;

CREATE FUNCTION security.read_gas_owned_reconciliation(catalog uuid,account uuid,body jsonb,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;owner trading.gas_owned_accounts;selected_positions uuid[];selected_consumptions uuid[];related_positions uuid[];evidence_ids uuid[];
 source_rows jsonb;statistics jsonb;byte_budget bigint;size_entry record;result jsonb;BEGIN
 until_at=security.gas_futures_delivery_discovery_authority(catalog);
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 body=trading.gas_owned_quantity_fact(body);
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(read_gas_owned_reconciliation.catalog,read_gas_owned_reconciliation.account)
  AND trading.gas_owned_visible(a.catalog_id,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',read_gas_owned_reconciliation.context,read_gas_owned_reconciliation.as_of);
 IF security.reference_version_visible(owner.reference_catalog_id,owner.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,owner.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,owner.definition_revision_id);
 PERFORM trading.require_gas_owned_quantity_head(catalog,account,context,as_of);
 IF EXISTS(SELECT 1 FROM jsonb_array_elements((body->'positions')||(body->'consumptions')) chosen WHERE NOT EXISTS(
  SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.id)=
   (read_gas_owned_reconciliation.catalog,read_gas_owned_reconciliation.account,(chosen->>'evidenceId')::uuid)
   AND trading.gas_owned_visible(e.catalog_id,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',read_gas_owned_reconciliation.context,read_gas_owned_reconciliation.as_of)))
 THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(body->'consumptions') chosen WHERE chosen->>'reservationId' IS NOT NULL AND NOT EXISTS(
  SELECT 1 FROM trading.gas_owned_reservations r WHERE(r.catalog_id,r.id,r.lot_id)=
   (read_gas_owned_reconciliation.catalog,(chosen->>'reservationId')::uuid,(chosen->>'lotId')::uuid)
   AND trading.gas_owned_visible(r.catalog_id,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',read_gas_owned_reconciliation.context,read_gas_owned_reconciliation.as_of)))
 THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 selected_positions=ARRAY(SELECT(value->>'positionId')::uuid FROM jsonb_array_elements(body->'positions'));
 selected_consumptions=ARRAY(SELECT(value->>'consumptionId')::uuid FROM jsonb_array_elements(body->'consumptions'));
 IF EXISTS(SELECT 1 FROM unnest(selected_positions) selected(id) WHERE NOT EXISTS(SELECT 1 FROM trading.gas_owned_positions_at(catalog,context,as_of) p
   WHERE(p.id,p.account_id)=(selected.id,read_gas_owned_reconciliation.account)))
  OR EXISTS(SELECT 1 FROM unnest(selected_consumptions) selected(id) WHERE NOT EXISTS(SELECT 1 FROM trading.gas_owned_consumptions_at(catalog,context,as_of) c
   JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(c.catalog_id,c.lot_id) WHERE(c.id,p.account_id)=(selected.id,read_gas_owned_reconciliation.account)))
 THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(body->'positions') chosen JOIN trading.gas_owned_positions p
  ON(p.catalog_id,p.id)=(read_gas_owned_reconciliation.catalog,(chosen->>'positionId')::uuid)
  WHERE p.kind<>'owned_stock' OR p.origin_policy<>'opening_owned_stock_v1' OR trading.gas_owned_quantity(chosen->>'quantity')<0)
  OR EXISTS(SELECT 1 FROM trading.gas_owned_consumptions c WHERE(c.catalog_id)=(read_gas_owned_reconciliation.catalog) AND c.id=ANY(selected_consumptions) AND c.kind NOT IN('delivery','loss'))
  OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s WHERE s.catalog_id=read_gas_owned_reconciliation.catalog AND s.consumption_id=ANY(selected_consumptions)
   AND trading.gas_owned_visible(s.catalog_id,s.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',read_gas_owned_reconciliation.context,read_gas_owned_reconciliation.as_of))
  OR EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_futures_performance_quantities q
   ON(q.catalog_id,q.performance_id,q.revision)=(s.catalog_id,s.performance_id,s.performance_revision)
   WHERE s.catalog_id=read_gas_owned_reconciliation.catalog AND s.consumption_id=ANY(selected_consumptions)
    AND trading.gas_owned_visible(q.catalog_id,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',read_gas_owned_reconciliation.context,read_gas_owned_reconciliation.as_of))
 THEN RAISE EXCEPTION 'gas_owned_reconciliation_closure';END IF;
 related_positions=ARRAY(SELECT DISTINCT id FROM(
  SELECT unnest(selected_positions) id UNION ALL SELECT(value->>'lotId')::uuid FROM jsonb_array_elements(body->'consumptions')
  UNION ALL SELECT c.lot_id FROM trading.gas_owned_consumptions_at(catalog,context,as_of) c WHERE c.id=ANY(selected_consumptions)) related);
 IF cardinality(related_positions)>16384 OR EXISTS(SELECT 1 FROM unnest(related_positions) selected(id) WHERE NOT EXISTS(
  SELECT 1 FROM trading.gas_owned_positions_at(catalog,context,as_of) p WHERE(p.id,p.account_id)=(selected.id,read_gas_owned_reconciliation.account)))
 THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 evidence_ids=ARRAY(SELECT DISTINCT(value->>'evidenceId')::uuid FROM jsonb_array_elements((body->'positions')||(body->'consumptions')));
 byte_budget=pg_column_size(body)::bigint+pg_column_size(to_jsonb(owner))+2048;
 statistics=trading.gas_owned_reconciliation_source_rows(catalog,account,context,as_of,related_positions,selected_positions,selected_consumptions,evidence_ids,true);
 FOR size_entry IN SELECT key,value FROM jsonb_each(statistics->'counts') LOOP
  IF size_entry.key NOT IN('positions','consumptions','evidence','reservations','releases') OR(size_entry.value#>>'{}')::bigint NOT BETWEEN 0 AND 16384
  THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 END LOOP;
 IF coalesce((statistics#>>'{counts,positions}')::integer,0)<>cardinality(related_positions)
  OR byte_budget+(statistics->>'bytes')::bigint+4*(statistics->>'count')::bigint>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 -- No result array is aggregated until every complete dependency family and
 -- the combined encoded payload fit the source admission.
 source_rows=trading.gas_owned_reconciliation_source_rows(catalog,account,context,as_of,related_positions,selected_positions,selected_consumptions,evidence_ids,false);
 IF EXISTS(SELECT 1 FROM jsonb_array_elements((source_rows->'positions')||(source_rows->'consumptions')||(body->'positions')||(body->'consumptions')) chosen WHERE NOT EXISTS(
  SELECT 1 FROM jsonb_array_elements(source_rows->'evidence') e WHERE(e->>'id')::uuid=(chosen->>'evidenceId')::uuid))
 THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 result=jsonb_build_object('implementation','gas_owned_quantity_reconciliation_source_v1','scope','gas_owned_reconciliation_dependency_facts_v1',
  'capability','ordinary_owned_title_and_dispatch_v1',
  'accountId',account,'account',to_jsonb(owner),'contextId',context,'asOf',as_of,'authorityUntil',until_at,'selection',body)||source_rows;
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);RETURN result;
EXCEPTION WHEN NO_DATA_FOUND THEN
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);RAISE EXCEPTION 'gas_quantity_source';
END$$;
CREATE FUNCTION trading.require_gas_owned_quantity_references(catalog uuid,account uuid,body jsonb) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE owner trading.gas_owned_accounts;item jsonb;lot trading.gas_owned_positions;BEGIN
 SELECT * INTO owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account);
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 IF security.reference_version_visible(owner.reference_catalog_id,owner.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,owner.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'positions') LOOP
  SELECT * INTO lot FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.account_id,p.id,p.kind,p.origin_policy)=
   (catalog,account,(item->>'positionId')::uuid,'owned_stock','opening_owned_stock_v1');
  IF NOT FOUND THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.id)=(catalog,account,(item->>'evidenceId')::uuid))
  THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  SELECT p.* INTO lot FROM trading.gas_owned_consumptions c JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(c.catalog_id,c.lot_id)
   WHERE(c.catalog_id,c.id,p.account_id)=(catalog,(item->>'consumptionId')::uuid,account) AND c.kind IN('delivery','loss');
  IF NOT FOUND THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.account_id,p.id,p.kind)=(catalog,account,(item->>'lotId')::uuid,'owned_stock'))
   OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.id)=(catalog,account,(item->>'evidenceId')::uuid))
  THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 END LOOP;
END$$;

CREATE FUNCTION trading.require_gas_owned_quantity_amendment(catalog uuid,account uuid,body jsonb,context uuid,as_of timestamptz,policy uuid,policy_revision integer) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE owner trading.gas_owned_accounts;binding trading.gas_owned_action_bindings;item jsonb;old_source trading.gas_owned_evidence;current_source trading.gas_owned_evidence;
 position trading.gas_owned_positions;consumption trading.gas_owned_consumptions;position_head trading.gas_owned_position_quantities;
 consumption_head trading.gas_owned_consumption_quantities;positions uuid[];ancestors uuid[];books uuid[];conflict jsonb;BEGIN
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account);
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts c ON(c.catalog_id,c.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND security.trading_context_day_matches(catalog,context,as_of))
  OR trading.gas_owned_visible(catalog,owner.publication_order,owner.effective_at,context,as_of) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_context';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,owner.definition_revision_id);
 PERFORM trading.require_gas_owned_head(catalog,account,context,as_of);PERFORM trading.require_gas_owned_quantity_head(catalog,account,context,as_of);
 SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,'gas.owned.reconcile_quantity') ORDER BY b.revision DESC LIMIT 1;
 IF(binding.policy_id,binding.policy_revision) IS DISTINCT FROM(policy,policy_revision) THEN RAISE EXCEPTION 'gas_owned_policy_changed';END IF;
 PERFORM lifecycle.require_policy(catalog,policy,policy_revision,'gas.owned.reconcile_quantity',as_of,false);
 positions='{}';
 FOR item IN SELECT value FROM jsonb_array_elements(body->'positions') LOOP
  SELECT * INTO STRICT position FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.account_id,p.id,p.kind,p.origin_policy)=
   (catalog,account,(item->>'positionId')::uuid,'owned_stock','opening_owned_stock_v1');
  SELECT * INTO STRICT position_head FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,position.id) ORDER BY q.revision DESC LIMIT 1;
  IF position_head.revision<>(item->>'expectedRevision')::integer OR position.effective_at>as_of OR trading.gas_owned_quantity(item->>'quantity')<0
  THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
  SELECT * INTO STRICT old_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=(catalog,position_head.evidence_id);
  SELECT * INTO STRICT current_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id,e.account_id)=(catalog,(item->>'evidenceId')::uuid,account);
  IF(current_source.kind,current_source.source_key,current_source.occurred_at) IS DISTINCT FROM('title_receipt',old_source.source_key,old_source.occurred_at)
   OR current_source.quantity<trading.gas_owned_quantity(item->>'quantity') OR current_source.occurred_at>position.effective_at
   OR trading.gas_owned_visible(catalog,current_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,account,current_source.source_key) AND e.revision>current_source.revision)
  THEN RAISE EXCEPTION 'gas_quantity_evidence';END IF;
  positions=array_append(positions,position.id);
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  SELECT * INTO STRICT consumption FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.id)=(catalog,(item->>'consumptionId')::uuid) AND c.kind IN('delivery','loss');
  SELECT * INTO STRICT consumption_head FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,consumption.id) ORDER BY q.revision DESC LIMIT 1;
  SELECT * INTO STRICT position FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id,p.account_id,p.kind)=(catalog,(item->>'lotId')::uuid,account,'owned_stock');
  IF consumption_head.revision<>(item->>'expectedRevision')::integer OR consumption.effective_at>as_of
   OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.consumption_id)=(catalog,consumption.id))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s WHERE(s.catalog_id,s.consumption_id)=(catalog,consumption.id))
  THEN RAISE EXCEPTION 'gas_owned_reconciliation_closure';END IF;
  SELECT * INTO STRICT old_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=(catalog,consumption_head.evidence_id);
  SELECT * INTO STRICT current_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id,e.account_id)=(catalog,(item->>'evidenceId')::uuid,account);
  IF(current_source.kind,current_source.source_key,current_source.occurred_at) IS DISTINCT FROM
    ((CASE consumption.kind WHEN 'loss' THEN 'physical_loss' ELSE 'physical_delivery' END),old_source.source_key,consumption.effective_at)
   OR abs(current_source.quantity)<trading.gas_owned_quantity(item->>'quantity')
   OR trading.gas_owned_visible(catalog,current_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,account,current_source.source_key) AND e.revision>current_source.revision)
  THEN RAISE EXCEPTION 'gas_quantity_evidence';END IF;
  positions=array_append(array_append(positions,position.id),consumption_head.lot_id);
 END LOOP;
 ancestors=trading.gas_owned_financial_ancestors(catalog,positions,context,as_of);
 SELECT coalesce(array_agg(DISTINCT a.book_id),'{}') INTO books FROM trading.gas_owned_accounts a JOIN trading.gas_owned_positions p ON(p.catalog_id,p.account_id)=(a.catalog_id,a.id)
  WHERE p.catalog_id=catalog AND p.id=ANY(ancestors);
 FOR conflict IN SELECT value FROM jsonb_array_elements(trading.gas_owned_applied_source_conflicts(catalog,context,as_of,books,ancestors)||
  trading.gas_owned_applied_observation_conflicts(catalog,context,as_of,books,ancestors)) LOOP
  IF conflict->>'sourceKind'='gas_evidence' AND(conflict->>'kind'='position' AND EXISTS(SELECT 1 FROM jsonb_array_elements(body->'positions') chosen
    WHERE(chosen->>'positionId')::uuid=(conflict->>'effectId')::uuid AND conflict->'currentSourceIds'=jsonb_build_array((chosen->>'evidenceId')::uuid))
   OR conflict->>'kind'='consumption' AND EXISTS(SELECT 1 FROM jsonb_array_elements(body->'consumptions') chosen
    WHERE(chosen->>'consumptionId')::uuid=(conflict->>'effectId')::uuid AND conflict->'currentSourceIds'=jsonb_build_array((chosen->>'evidenceId')::uuid))) THEN CONTINUE;END IF;
  RAISE EXCEPTION 'gas_owned_pending_source_reconciliation';
 END LOOP;
END$$;

CREATE FUNCTION trading.gas_owned_quantity_allocation_facts(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE header trading.gas_owned_quantity_corrections;owner trading.gas_owned_accounts;key record;previous_source trading.gas_owned_evidence;
 current_source trading.gas_owned_evidence;before_ids uuid[];after_ids uuid[];previous_sources uuid[];current_sources uuid[];
 previous_amount numeric;current_amount numeric;policies text[];result jsonb='[]';fact jsonb;BEGIN
 SELECT * INTO STRICT header FROM trading.gas_owned_quantity_corrections h WHERE(h.catalog_id,h.operation_id)=(catalog,operation);
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,header.account_id);
 FOR key IN SELECT source_keys.* FROM(
  SELECT DISTINCT e.source_key,e.kind FROM trading.gas_owned_quantity_position_members m JOIN trading.gas_owned_position_quantities q
   ON(q.catalog_id,q.position_id,q.revision)=(m.catalog_id,m.position_id,m.revision) JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
   WHERE(m.catalog_id,m.operation_id)=(catalog,operation)
  UNION SELECT DISTINCT e.source_key,e.kind FROM trading.gas_owned_quantity_consumption_members m JOIN trading.gas_owned_consumption_quantities q
   ON(q.catalog_id,q.consumption_id,q.revision)=(m.catalog_id,m.consumption_id,m.revision) JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
   WHERE(m.catalog_id,m.operation_id)=(catalog,operation))source_keys ORDER BY convert_to(source_keys.source_key,'UTF8'),source_keys.kind
 LOOP
  IF key.kind='title_receipt' THEN
   IF EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key) AND q.publication_order<=header.publication_order
     AND q.revision IN(SELECT max(prior.revision) FROM trading.gas_owned_position_quantities prior WHERE(prior.catalog_id,prior.position_id)=(q.catalog_id,q.position_id)
      AND prior.publication_order<header.publication_order UNION ALL SELECT max(prior.revision) FROM trading.gas_owned_position_quantities prior WHERE(prior.catalog_id,prior.position_id)=(q.catalog_id,q.position_id)
      AND prior.publication_order<=header.publication_order)
     AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_position_members m WHERE(m.catalog_id,m.operation_id,m.position_id)=(catalog,operation,q.position_id)))
   THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
   IF EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key) AND q.revision=(SELECT max(prior.revision) FROM trading.gas_owned_position_quantities prior
     WHERE(prior.catalog_id,prior.position_id)=(q.catalog_id,q.position_id) AND prior.publication_order<header.publication_order)
     AND trading.gas_owned_visible(catalog,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',header.source_context_id,header.as_of) IS NOT TRUE)
   THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
   SELECT coalesce(array_agg(q.position_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(abs(q.quantity)),0)
    INTO before_ids,previous_sources,previous_amount FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key) AND q.revision=(SELECT max(prior.revision) FROM trading.gas_owned_position_quantities prior
     WHERE(prior.catalog_id,prior.position_id)=(q.catalog_id,q.position_id) AND prior.publication_order<header.publication_order);
   SELECT coalesce(array_agg(q.position_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(abs(q.quantity)),0)
    INTO after_ids,current_sources,current_amount FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key) AND q.revision=(SELECT max(prior.revision) FROM trading.gas_owned_position_quantities prior
     WHERE(prior.catalog_id,prior.position_id)=(q.catalog_id,q.position_id) AND prior.publication_order<=header.publication_order);
   IF EXISTS(SELECT 1 FROM unnest(before_ids||after_ids) selected(id) WHERE NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_position_members m
    WHERE(m.catalog_id,m.operation_id,m.position_id)=(catalog,operation,selected.id))) THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
   SELECT array_agg(DISTINCT m.source_allocation) INTO policies FROM trading.gas_owned_quantity_position_members m WHERE(m.catalog_id,m.operation_id)=(catalog,operation) AND m.position_id=ANY(after_ids);
  ELSIF key.kind IN('physical_delivery','physical_loss') THEN
   IF EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key) AND q.publication_order<=header.publication_order
     AND q.revision IN(SELECT max(prior.revision) FROM trading.gas_owned_consumption_quantities prior WHERE(prior.catalog_id,prior.consumption_id)=(q.catalog_id,q.consumption_id)
      AND prior.publication_order<header.publication_order UNION ALL SELECT max(prior.revision) FROM trading.gas_owned_consumption_quantities prior WHERE(prior.catalog_id,prior.consumption_id)=(q.catalog_id,q.consumption_id)
      AND prior.publication_order<=header.publication_order)
     AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_consumption_members m WHERE(m.catalog_id,m.operation_id,m.consumption_id)=(catalog,operation,q.consumption_id)))
   THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
   IF EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key) AND q.revision=(SELECT max(prior.revision) FROM trading.gas_owned_consumption_quantities prior
     WHERE(prior.catalog_id,prior.consumption_id)=(q.catalog_id,q.consumption_id) AND prior.publication_order<header.publication_order)
     AND trading.gas_owned_visible(catalog,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',header.source_context_id,header.as_of) IS NOT TRUE)
   THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
   SELECT coalesce(array_agg(q.consumption_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(q.quantity),0)
    INTO before_ids,previous_sources,previous_amount FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key) AND q.revision=(SELECT max(prior.revision) FROM trading.gas_owned_consumption_quantities prior
     WHERE(prior.catalog_id,prior.consumption_id)=(q.catalog_id,q.consumption_id) AND prior.publication_order<header.publication_order);
   SELECT coalesce(array_agg(q.consumption_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(q.quantity),0)
    INTO after_ids,current_sources,current_amount FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key) AND q.revision=(SELECT max(prior.revision) FROM trading.gas_owned_consumption_quantities prior
     WHERE(prior.catalog_id,prior.consumption_id)=(q.catalog_id,q.consumption_id) AND prior.publication_order<=header.publication_order);
   IF EXISTS(SELECT 1 FROM unnest(before_ids||after_ids) selected(id) WHERE NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_consumption_members m
    WHERE(m.catalog_id,m.operation_id,m.consumption_id)=(catalog,operation,selected.id))) THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
   SELECT array_agg(DISTINCT m.source_allocation) INTO policies FROM trading.gas_owned_quantity_consumption_members m WHERE(m.catalog_id,m.operation_id)=(catalog,operation) AND m.consumption_id=ANY(after_ids);
  ELSE RAISE EXCEPTION 'gas_owned_reconciliation_closure';END IF;
  IF cardinality(previous_sources)<>1 OR cardinality(current_sources)<>1 OR cardinality(policies)<>1 OR cardinality(before_ids)>16384 OR cardinality(after_ids)>16384
  THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
  SELECT * INTO STRICT previous_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=(catalog,previous_sources[1]);
  SELECT * INTO STRICT current_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=(catalog,current_sources[1]);
  IF current_source.account_id<>header.account_id OR(previous_source.account_id,previous_source.source_key,previous_source.kind,previous_source.occurred_at) IS DISTINCT FROM
    (header.account_id,current_source.source_key,current_source.kind,current_source.occurred_at)
   OR trading.gas_owned_visible(catalog,previous_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',header.source_context_id,header.as_of) IS NOT TRUE
   OR trading.gas_owned_visible(catalog,current_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',header.source_context_id,header.as_of) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,header.account_id,key.source_key)
    AND e.revision>current_source.revision AND e.publication_order<=header.publication_order)
  THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  fact=jsonb_build_object('accountId',header.account_id,'sourceKey',key.source_key,'kind',key.kind,'oldEvidenceId',previous_source.id,'sourceEvidenceId',current_source.id,
   'oldCapacity',trim_scale(abs(previous_source.quantity))::text,'capacity',trim_scale(abs(current_source.quantity))::text,
   'oldAllocated',trim_scale(previous_amount)::text,'allocated',trim_scale(current_amount)::text,'newEnrollment',false,
   'referenceCatalogId',owner.reference_catalog_id,'referenceRevisionId',owner.reference_revision_id,'quantity',owner.quantity_code);
  PERFORM trading.require_gas_source_allocation(policies[1],jsonb_build_array(fact));
  result=result||jsonb_build_array(fact||jsonb_build_object('sourceAllocation',policies[1]));
 END LOOP;
 IF jsonb_array_length(result)=0 OR pg_column_size(result)>8388608 THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;RETURN result;
END$$;

CREATE FUNCTION trading.apply_gas_owned_quantity_amendments(catalog uuid,operation uuid,account uuid,body jsonb,context uuid,as_of timestamptz,actor uuid,publication integer) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE item jsonb;state jsonb;position_states jsonb='[]';consumption_states jsonb='[]';position_results jsonb='[]';consumption_results jsonb='[]';
 acknowledged integer;revision integer;changed boolean=false;facts jsonb;fact jsonb;
 position_head trading.gas_owned_position_quantities;consumption_head trading.gas_owned_consumption_quantities;BEGIN
 IF body IS DISTINCT FROM trading.gas_owned_quantity_fact(body) OR NOT EXISTS(SELECT 1 FROM trading.publications p
  WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(catalog,publication,pg_current_xact_id())) THEN RAISE EXCEPTION 'gas_owned_quantity_sealed';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'positions') LOOP
  SELECT * INTO STRICT position_head FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,(item->>'positionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_owned_position_quantity_snapshot(catalog,item);
  IF position_head.revision<>(item->>'expectedRevision')::integer OR acknowledged IS NOT NULL AND acknowledged<>position_head.revision THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
  changed=changed OR acknowledged IS NULL;
  position_states=position_states||jsonb_build_array(jsonb_build_object('selection',item,'currentRevision',position_head.revision,'acknowledgedRevision',acknowledged));
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  SELECT * INTO STRICT consumption_head FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_owned_consumption_quantity_snapshot(catalog,item);
  IF consumption_head.revision<>(item->>'expectedRevision')::integer OR acknowledged IS NOT NULL AND acknowledged<>consumption_head.revision THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
  changed=changed OR acknowledged IS NULL;
  consumption_states=consumption_states||jsonb_build_array(jsonb_build_object('selection',item,'currentRevision',consumption_head.revision,'acknowledgedRevision',acknowledged));
 END LOOP;
 IF NOT changed THEN RAISE EXCEPTION 'gas_owned_quantity_sealed';END IF;
 INSERT INTO trading.gas_owned_quantity_corrections VALUES(catalog,operation,account,publication,context,as_of,actor,'gas_owned_quantity_reconciliation_v1');
 FOR state IN SELECT value FROM jsonb_array_elements(position_states) LOOP
  item=state->'selection';revision=(state->>'currentRevision')::integer;
  IF state->>'acknowledgedRevision' IS NULL THEN
   revision=revision+1;
   INSERT INTO trading.gas_owned_position_quantities VALUES(catalog,(item->>'positionId')::uuid,account,revision,revision-1,trading.gas_owned_quantity(item->>'quantity'),
    (item->>'evidenceId')::uuid,(item->>'retracted')::boolean,actor,publication);
  END IF;
  INSERT INTO trading.gas_owned_quantity_position_members VALUES(catalog,operation,(item->>'positionId')::uuid,revision,state->>'acknowledgedRevision' IS NULL,item->>'sourceAllocation');
  position_results=position_results||jsonb_build_array(jsonb_build_object('positionId',(item->>'positionId')::uuid,'acknowledgedRevision',revision,'currentRevision',revision,
   'currentQuantity',item->>'quantity','evidenceId',(item->>'evidenceId')::uuid,'changed',state->>'acknowledgedRevision' IS NULL));
 END LOOP;
 FOR state IN SELECT value FROM jsonb_array_elements(consumption_states) LOOP
  item=state->'selection';revision=(state->>'currentRevision')::integer;
  IF state->>'acknowledgedRevision' IS NULL THEN
   revision=revision+1;
   INSERT INTO trading.gas_owned_consumption_quantities VALUES(catalog,(item->>'consumptionId')::uuid,revision,revision-1,(item->>'lotId')::uuid,
    trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,(item->>'retracted')::boolean,actor,publication);
  END IF;
  INSERT INTO trading.gas_owned_quantity_consumption_members VALUES(catalog,operation,(item->>'consumptionId')::uuid,revision,state->>'acknowledgedRevision' IS NULL,item->>'sourceAllocation');
  consumption_results=consumption_results||jsonb_build_array(jsonb_build_object('consumptionId',(item->>'consumptionId')::uuid,'acknowledgedRevision',revision,'currentRevision',revision,
   'currentQuantity',item->>'quantity','lotId',(item->>'lotId')::uuid,'evidenceId',(item->>'evidenceId')::uuid,'reservationId',(item->>'reservationId')::uuid,'changed',state->>'acknowledgedRevision' IS NULL));
 END LOOP;
 facts=trading.gas_owned_quantity_allocation_facts(catalog,operation);
 FOR fact IN SELECT value FROM jsonb_array_elements(facts) LOOP
  INSERT INTO trading.gas_owned_quantity_source_members VALUES(catalog,operation,account,(fact->>'sourceEvidenceId')::uuid,(fact->>'oldEvidenceId')::uuid,fact->>'sourceAllocation');
 END LOOP;
 RETURN jsonb_build_object('operationId',operation,'accountId',account,'action','owned_quantity_reconciliation','changed',true,'semanticAcknowledgement',false,
  'publicationOrder',publication,'positions',position_results,'consumptions',consumption_results,'sourceAllocations',facts);
END$$;

CREATE FUNCTION security.reconcile_gas_owned_quantities(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,
 policy uuid,policy_revision integer,receipt_only boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;canonical jsonb;prior trading.gas_owned_commands;item jsonb;state jsonb;position_states jsonb='[]';consumption_states jsonb='[]';
 position_results jsonb='[]';consumption_results jsonb='[]';all_ack boolean=true;acknowledged integer;publication integer;
 position_head trading.gas_owned_position_quantities;consumption_head trading.gas_owned_consumption_quantities;outcome jsonb;BEGIN
 admission=security.gas_owned_admission(catalog,'owned_quantity_reconciliation',body,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR account IS NULL OR context IS NULL OR policy IS NULL
  OR policy_revision IS NULL OR policy_revision<1 OR as_of IS NULL OR NOT isfinite(as_of) OR receipt_only IS NULL THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 body=trading.gas_owned_quantity_fact(body);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 PERFORM trading.require_gas_owned_quantity_references(catalog,account,body);
 IF NOT EXISTS(SELECT 1 FROM lifecycle.action_policies p WHERE(p.catalog_id,p.id,p.revision,p.action)=(catalog,policy,policy_revision,'gas.owned.reconcile_quantity'))
 THEN RAISE EXCEPTION 'gas_owned_policy';END IF;
 canonical=jsonb_build_object('action','owned_quantity_reconciliation','accountId',account,'body',body,'asOf',as_of,'contextId',context,'policyId',policy,'policyRevision',policy_revision,'automatic',false);
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object('outcome',prior.outcome);END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR item IN SELECT value FROM jsonb_array_elements(body->'positions') LOOP
  SELECT * INTO STRICT position_head FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,(item->>'positionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_owned_position_quantity_snapshot(catalog,item);all_ack=all_ack AND acknowledged IS NOT NULL;
  position_states=position_states||jsonb_build_array(jsonb_build_object('selection',item,'currentRevision',position_head.revision,'acknowledgedRevision',acknowledged));
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  SELECT * INTO STRICT consumption_head FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_owned_consumption_quantity_snapshot(catalog,item);all_ack=all_ack AND acknowledged IS NOT NULL;
  consumption_states=consumption_states||jsonb_build_array(jsonb_build_object('selection',item,'currentRevision',consumption_head.revision,'acknowledgedRevision',acknowledged));
 END LOOP;
 IF NOT all_ack AND receipt_only THEN
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);RETURN NULL;
 END IF;
 IF NOT all_ack THEN
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(position_states||consumption_states)s WHERE s->>'acknowledgedRevision' IS NOT NULL AND s->>'acknowledgedRevision'<>s->>'currentRevision')
  THEN RAISE EXCEPTION 'gas_quantity_batch_history';END IF;
  PERFORM trading.require_gas_owned_quantity_amendment(catalog,account,body,context,as_of,policy,policy_revision);
  SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  outcome=trading.apply_gas_owned_quantity_amendments(catalog,operation,account,body,context,as_of,actor,publication);
 END IF;
 IF all_ack THEN
 FOR state IN SELECT value FROM jsonb_array_elements(position_states) LOOP
  item=state->'selection';
  SELECT * INTO STRICT position_head FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,(item->>'positionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  position_results=position_results||jsonb_build_array(jsonb_build_object('positionId',position_head.position_id,
   'acknowledgedRevision',(state->>'acknowledgedRevision')::integer,'currentRevision',position_head.revision,
   'currentQuantity',trim_scale(position_head.quantity)::text,'evidenceId',position_head.evidence_id,'changed',false));
 END LOOP;
 FOR state IN SELECT value FROM jsonb_array_elements(consumption_states) LOOP
  item=state->'selection';
  SELECT * INTO STRICT consumption_head FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  consumption_results=consumption_results||jsonb_build_array(jsonb_build_object('consumptionId',consumption_head.consumption_id,
   'acknowledgedRevision',(state->>'acknowledgedRevision')::integer,'currentRevision',consumption_head.revision,
   'currentQuantity',trim_scale(consumption_head.quantity)::text,'lotId',consumption_head.lot_id,'evidenceId',consumption_head.evidence_id,'reservationId',consumption_head.reservation_id,
   'changed',false));
 END LOOP;
 outcome=jsonb_build_object('operationId',operation,'accountId',account,'action','owned_quantity_reconciliation','changed',false,'semanticAcknowledgement',true,
  'publicationOrder',NULL,'positions',position_results,'consumptions',consumption_results,'sourceAllocations',NULL);
 END IF;
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,(admission->>'until')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object('outcome',outcome);END IF;RETURN outcome;
END$$;

CREATE FUNCTION trading.gas_owned_quantity_position_proof(catalog uuid,position_id uuid,version integer) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_position_members m JOIN trading.gas_owned_quantity_corrections h
  ON(h.catalog_id,h.operation_id)=(m.catalog_id,m.operation_id) JOIN trading.publications pub ON(pub.catalog_id,pub.publication_order)=(h.catalog_id,h.publication_order)
  JOIN trading.gas_owned_position_quantities q ON(q.catalog_id,q.position_id,q.revision)=(m.catalog_id,m.position_id,m.revision)
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id,p.account_id)=(q.catalog_id,q.position_id,h.account_id)
  WHERE(m.catalog_id,m.position_id,m.revision,m.changed)=($1,$2,$3,true) AND(q.publication_order,q.actor_id)=(h.publication_order,h.actor_id)
   AND p.kind='owned_stock' AND p.origin_policy='opening_owned_stock_v1' AND pub.publication_xid=pg_current_xact_id_if_assigned())
$$;
CREATE FUNCTION trading.gas_owned_quantity_consumption_proof(catalog uuid,consumption uuid,version integer) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_consumption_members m JOIN trading.gas_owned_quantity_corrections h
  ON(h.catalog_id,h.operation_id)=(m.catalog_id,m.operation_id) JOIN trading.publications pub ON(pub.catalog_id,pub.publication_order)=(h.catalog_id,h.publication_order)
  JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.revision)=(m.catalog_id,m.consumption_id,m.revision)
  JOIN trading.gas_owned_consumptions c ON(c.catalog_id,c.id)=(q.catalog_id,q.consumption_id)
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id,p.account_id)=(q.catalog_id,q.lot_id,h.account_id)
  WHERE(m.catalog_id,m.consumption_id,m.revision,m.changed)=($1,$2,$3,true) AND(q.publication_order,q.actor_id)=(h.publication_order,h.actor_id)
   AND c.kind IN('delivery','loss') AND pub.publication_xid=pg_current_xact_id_if_assigned())
$$;
DO $guard$DECLARE original pg_proc;spec record;definition text;needle text;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('trading.check_gas_quantity_revision()','ab2b28f0cfc6682c5474a6c1ea17878d'),
  ('trading.check_gas_owned_integrity()','0aef616e6b69e4bce7614ff5d540f506'))s(signature,source_hash) LOOP
 SELECT * INTO STRICT original FROM pg_proc WHERE oid=spec.signature::regprocedure;
 IF md5(original.prosrc)<>spec.source_hash OR original.proowner<>'openetrm_security_owner'::regrole OR NOT original.prosecdef
  OR original.prokind<>'f' OR original.prorettype<>'trigger'::regtype OR original.proretset OR original.provolatile<>'v' OR original.proparallel<>'u'
  OR original.provariadic<>0 OR original.proargmodes IS NOT NULL OR original.proallargtypes IS NOT NULL OR original.proargnames IS NOT NULL
  OR original.pronargdefaults<>0 OR original.proargdefaults IS NOT NULL OR original.proisstrict OR original.proleakproof
  OR original.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR(SELECT lanname FROM pg_language WHERE oid=original.prolang)<>'plpgsql'
  OR EXISTS(SELECT 1 FROM aclexplode(coalesce(original.proacl,acldefault('f',original.proowner)))a WHERE a.grantor<>original.proowner OR a.grantee<>original.proowner OR a.is_grantable OR a.privilege_type<>'EXECUTE')
 THEN RAISE EXCEPTION 'gas_owned_quantity_upgrade: unexpected helper %',spec.signature;END IF;
 definition=pg_get_functiondef(original.oid);
 IF spec.signature='trading.check_gas_owned_integrity()' THEN
  -- A sealed financial retraction has no remaining dispatch. An unchanged
  -- positive use still requires positive-magnitude source capacity.
  needle='OR e.quantity=0';
  IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_owned_quantity_upgrade';END IF;
  definition=replace(definition,needle,'OR(e.quantity=0 AND c.quantity<>0)');
 ELSE
 needle='   IF NOT EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances p JOIN trading.gas_futures_performance_quantities q';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_owned_quantity_upgrade';END IF;
 definition=replace(definition,needle,'   IF NOT trading.gas_owned_quantity_position_proof(NEW.catalog_id,NEW.position_id,NEW.revision) AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances p JOIN trading.gas_futures_performance_quantities q');
 needle='  ELSIF NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_futures_performance_quantities q';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_owned_quantity_upgrade';END IF;
 definition=replace(definition,needle,'  ELSIF NOT trading.gas_owned_quantity_consumption_proof(NEW.catalog_id,NEW.consumption_id,NEW.revision) AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_futures_performance_quantities q');
 END IF;
 EXECUTE definition;
 END LOOP;
END$guard$;
CREATE FUNCTION trading.guard_gas_owned_quantity_member() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_corrections h JOIN trading.publications p ON(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order)
  WHERE(h.catalog_id,h.operation_id,p.publication_xid)=(NEW.catalog_id,NEW.operation_id,pg_current_xact_id())) THEN RAISE EXCEPTION 'gas_owned_quantity_sealed';END IF;
 RETURN NEW;
END$$;
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_owned_quantity_position_members','gas_owned_quantity_consumption_members','gas_owned_quantity_source_members'] LOOP
  EXECUTE format('CREATE TRIGGER sealed_parent BEFORE INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_quantity_member()',name);
 END LOOP;
END$$;

CREATE FUNCTION trading.check_gas_owned_quantity_correction() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_owned_quantity_corrections;c trading.gas_owned_commands;owner trading.gas_owned_accounts;binding trading.gas_owned_action_bindings;
 position_member trading.gas_owned_quantity_position_members;consumption_member trading.gas_owned_quantity_consumption_members;
 position_origin trading.gas_owned_positions;consumption_origin trading.gas_owned_consumptions;
 position_head trading.gas_owned_position_quantities;previous_position trading.gas_owned_position_quantities;
 consumption_head trading.gas_owned_consumption_quantities;previous_consumption trading.gas_owned_consumption_quantities;
 old_source trading.gas_owned_evidence;selected_source trading.gas_owned_evidence;item jsonb;fact jsonb;facts jsonb;
 positions jsonb='[]';consumptions jsonb='[]';expected jsonb;counted integer;BEGIN
 SELECT * INTO STRICT h FROM trading.gas_owned_quantity_corrections x WHERE(x.catalog_id,x.operation_id)=(NEW.catalog_id,NEW.operation_id);
 SELECT * INTO STRICT c FROM trading.gas_owned_commands x WHERE(x.catalog_id,x.request_id)=(h.catalog_id,h.operation_id);
 PERFORM trading.gas_owned_projection_keys(c.request,ARRAY['action','accountId','body','asOf','contextId','policyId','policyRevision','automatic'],ARRAY[]::text[]);
 IF jsonb_typeof(c.request->'body') IS DISTINCT FROM 'object' OR jsonb_typeof(c.request#>'{body,positions}') IS DISTINCT FROM 'array'
  OR jsonb_typeof(c.request#>'{body,consumptions}') IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 IF trading.gas_owned_quantity_terms(c.request->'body') IS DISTINCT FROM c.request->'body'
   OR jsonb_typeof(c.request->'policyId') IS DISTINCT FROM 'string' OR(c.request->>'policyId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR jsonb_typeof(c.request->'policyRevision') IS DISTINCT FROM 'number' OR c.request->>'policyRevision'!~'^[1-9][0-9]{0,8}$'
 THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 IF(c.actor_id,c.account_id,c.request->>'action',(c.request->>'accountId')::uuid,(c.request->>'contextId')::uuid,
   (c.request->>'asOf')::timestamptz,(c.request->>'automatic')::boolean,c.configuring,c.requires_jobs) IS DISTINCT FROM
  (h.actor_id,h.account_id,'owned_quantity_reconciliation',h.account_id,h.source_context_id,h.as_of,false,false,false)
  OR NOT EXISTS(SELECT 1 FROM trading.publications pub WHERE(pub.catalog_id,pub.publication_order,pub.publication_xid)=(h.catalog_id,h.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 PERFORM security.require_gas_owned_commit(h.catalog_id,c.authority_until,false,false);
 PERFORM trading.require_gas_owned_quantity_references(h.catalog_id,h.account_id,c.request->'body');
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(h.catalog_id,h.account_id);
 IF trading.gas_owned_visible(h.catalog_id,owner.publication_order,owner.effective_at,h.source_context_id,h.as_of) IS NOT TRUE
  OR security.trading_context_day_matches(h.catalog_id,h.source_context_id,h.as_of) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 SELECT * INTO STRICT binding FROM trading.gas_owned_action_bindings x WHERE(x.catalog_id,x.account_id,x.action)=(h.catalog_id,h.account_id,'gas.owned.reconcile_quantity')
  AND x.publication_order<=h.publication_order ORDER BY x.revision DESC LIMIT 1;
 IF(binding.policy_id,binding.policy_revision) IS DISTINCT FROM((c.request->>'policyId')::uuid,(c.request->>'policyRevision')::integer)
  OR trading.gas_owned_visible(h.catalog_id,binding.publication_order,binding.effective_at,h.source_context_id,h.as_of) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 PERFORM lifecycle.require_policy(h.catalog_id,binding.policy_id,binding.policy_revision,'gas.owned.reconcile_quantity',h.as_of,false);
 SELECT count(*) INTO counted FROM trading.gas_owned_quantity_position_members x WHERE(x.catalog_id,x.operation_id)=(h.catalog_id,h.operation_id);
 IF counted<>jsonb_array_length(c.request#>'{body,positions}') THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 SELECT count(*) INTO counted FROM trading.gas_owned_quantity_consumption_members x WHERE(x.catalog_id,x.operation_id)=(h.catalog_id,h.operation_id);
 IF counted<>jsonb_array_length(c.request#>'{body,consumptions}') OR NOT(
  EXISTS(SELECT 1 FROM trading.gas_owned_quantity_position_members x WHERE(x.catalog_id,x.operation_id,x.changed)=(h.catalog_id,h.operation_id,true))
  OR EXISTS(SELECT 1 FROM trading.gas_owned_quantity_consumption_members x WHERE(x.catalog_id,x.operation_id,x.changed)=(h.catalog_id,h.operation_id,true)))
 THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(c.request#>'{body,positions}') LOOP
  SELECT * INTO STRICT position_member FROM trading.gas_owned_quantity_position_members x WHERE(x.catalog_id,x.operation_id,x.position_id)=(h.catalog_id,h.operation_id,(item->>'positionId')::uuid);
  SELECT * INTO STRICT position_head FROM trading.gas_owned_position_quantities x WHERE(x.catalog_id,x.position_id,x.revision)=(h.catalog_id,position_member.position_id,position_member.revision);
  SELECT * INTO STRICT previous_position FROM trading.gas_owned_position_quantities x WHERE(x.catalog_id,x.position_id)=(h.catalog_id,position_member.position_id)
   AND x.publication_order<h.publication_order ORDER BY x.revision DESC LIMIT 1;
  SELECT * INTO STRICT position_origin FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(h.catalog_id,position_member.position_id);
  IF(position_origin.account_id,position_origin.kind,position_origin.origin_policy) IS DISTINCT FROM(h.account_id,'owned_stock','opening_owned_stock_v1')
   OR position_origin.effective_at>h.as_of OR position_head.quantity<0
   OR previous_position.revision<>(item->>'expectedRevision')::integer
   OR position_head.revision<>previous_position.revision+(CASE WHEN position_member.changed THEN 1 ELSE 0 END)
   OR position_head.revision<>(SELECT max(x.revision) FROM trading.gas_owned_position_quantities x WHERE(x.catalog_id,x.position_id)=(h.catalog_id,position_head.position_id) AND x.publication_order<=h.publication_order)
   OR(position_head.quantity,position_head.evidence_id,position_head.retracted,position_member.source_allocation) IS DISTINCT FROM
    (trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'retracted')::boolean,item->>'sourceAllocation')
   OR position_member.changed AND(position_head.publication_order,position_head.actor_id) IS DISTINCT FROM(h.publication_order,h.actor_id)
   OR NOT position_member.changed AND position_head.publication_order>=h.publication_order
   OR trading.gas_owned_visible(h.catalog_id,previous_position.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',h.source_context_id,h.as_of) IS NOT TRUE
   OR trading.gas_owned_position_quantity_snapshot(h.catalog_id,item) IS DISTINCT FROM position_head.revision
  THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
  SELECT * INTO STRICT old_source FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id)=(h.catalog_id,previous_position.evidence_id);
  SELECT * INTO STRICT selected_source FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id)=(h.catalog_id,position_head.evidence_id);
  IF(selected_source.account_id,selected_source.kind,selected_source.source_key,selected_source.occurred_at) IS DISTINCT FROM
    (h.account_id,'title_receipt',old_source.source_key,old_source.occurred_at) OR selected_source.occurred_at>position_origin.effective_at
   OR selected_source.quantity<position_head.quantity
  THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
  positions=positions||jsonb_build_array(jsonb_build_object('positionId',position_head.position_id,'acknowledgedRevision',position_head.revision,
   'currentRevision',position_head.revision,'currentQuantity',trim_scale(position_head.quantity)::text,'evidenceId',position_head.evidence_id,'changed',position_member.changed));
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(c.request#>'{body,consumptions}') LOOP
  SELECT * INTO STRICT consumption_member FROM trading.gas_owned_quantity_consumption_members x WHERE(x.catalog_id,x.operation_id,x.consumption_id)=(h.catalog_id,h.operation_id,(item->>'consumptionId')::uuid);
  SELECT * INTO STRICT consumption_head FROM trading.gas_owned_consumption_quantities x WHERE(x.catalog_id,x.consumption_id,x.revision)=(h.catalog_id,consumption_member.consumption_id,consumption_member.revision);
  SELECT * INTO STRICT previous_consumption FROM trading.gas_owned_consumption_quantities x WHERE(x.catalog_id,x.consumption_id)=(h.catalog_id,consumption_member.consumption_id)
   AND x.publication_order<h.publication_order ORDER BY x.revision DESC LIMIT 1;
  SELECT * INTO STRICT consumption_origin FROM trading.gas_owned_consumptions x WHERE(x.catalog_id,x.id)=(h.catalog_id,consumption_member.consumption_id);
  IF consumption_origin.kind NOT IN('delivery','loss') OR consumption_origin.effective_at>h.as_of
   OR previous_consumption.revision<>(item->>'expectedRevision')::integer
   OR consumption_head.revision<>previous_consumption.revision+(CASE WHEN consumption_member.changed THEN 1 ELSE 0 END)
   OR consumption_head.revision<>(SELECT max(x.revision) FROM trading.gas_owned_consumption_quantities x WHERE(x.catalog_id,x.consumption_id)=(h.catalog_id,consumption_head.consumption_id) AND x.publication_order<=h.publication_order)
   OR(consumption_head.lot_id,consumption_head.quantity,consumption_head.evidence_id,consumption_head.retracted,consumption_member.source_allocation) IS DISTINCT FROM
    ((item->>'lotId')::uuid,trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'retracted')::boolean,item->>'sourceAllocation')
   OR consumption_head.reservation_id IS DISTINCT FROM(item->>'reservationId')::uuid
   OR consumption_member.changed AND(consumption_head.publication_order,consumption_head.actor_id) IS DISTINCT FROM(h.publication_order,h.actor_id)
   OR NOT consumption_member.changed AND consumption_head.publication_order>=h.publication_order
   OR trading.gas_owned_visible(h.catalog_id,previous_consumption.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',h.source_context_id,h.as_of) IS NOT TRUE
   OR trading.gas_owned_consumption_quantity_snapshot(h.catalog_id,item) IS DISTINCT FROM consumption_head.revision
   OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements x WHERE(x.catalog_id,x.consumption_id)=(h.catalog_id,consumption_head.consumption_id))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies x WHERE(x.catalog_id,x.consumption_id)=(h.catalog_id,consumption_head.consumption_id))
  THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
  SELECT * INTO STRICT old_source FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id)=(h.catalog_id,previous_consumption.evidence_id);
  SELECT * INTO STRICT selected_source FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id)=(h.catalog_id,consumption_head.evidence_id);
  IF(selected_source.account_id,selected_source.kind,selected_source.source_key,selected_source.occurred_at) IS DISTINCT FROM
    (h.account_id,CASE consumption_origin.kind WHEN 'loss' THEN 'physical_loss' ELSE 'physical_delivery' END,old_source.source_key,consumption_origin.effective_at)
   OR abs(selected_source.quantity)<consumption_head.quantity
  THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
  consumptions=consumptions||jsonb_build_array(jsonb_build_object('consumptionId',consumption_head.consumption_id,'acknowledgedRevision',consumption_head.revision,
   'currentRevision',consumption_head.revision,'currentQuantity',trim_scale(consumption_head.quantity)::text,'lotId',consumption_head.lot_id,
   'evidenceId',consumption_head.evidence_id,'reservationId',consumption_head.reservation_id,'changed',consumption_member.changed));
 END LOOP;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities financial WHERE(financial.catalog_id,financial.publication_order)=(h.catalog_id,h.publication_order)
  AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_position_members member_link WHERE
   (member_link.catalog_id,member_link.operation_id,member_link.position_id,member_link.revision,member_link.changed)=(h.catalog_id,h.operation_id,financial.position_id,financial.revision,true)))
  OR EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities financial WHERE(financial.catalog_id,financial.publication_order)=(h.catalog_id,h.publication_order)
  AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_consumption_members member_link WHERE
   (member_link.catalog_id,member_link.operation_id,member_link.consumption_id,member_link.revision,member_link.changed)=(h.catalog_id,h.operation_id,financial.consumption_id,financial.revision,true)))
 THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 facts=trading.gas_owned_quantity_allocation_facts(h.catalog_id,h.operation_id);
 SELECT count(*) INTO counted FROM trading.gas_owned_quantity_source_members x WHERE(x.catalog_id,x.operation_id)=(h.catalog_id,h.operation_id);
 IF counted<>jsonb_array_length(facts) THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 FOR fact IN SELECT value FROM jsonb_array_elements(facts) LOOP
  IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_source_members x WHERE
   (x.catalog_id,x.operation_id,x.account_id,x.source_evidence_id,x.previous_evidence_id,x.source_allocation)=
   (h.catalog_id,h.operation_id,h.account_id,(fact->>'sourceEvidenceId')::uuid,(fact->>'oldEvidenceId')::uuid,fact->>'sourceAllocation'))
  THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 END LOOP;
 expected=jsonb_build_object('operationId',h.operation_id,'accountId',h.account_id,'action','owned_quantity_reconciliation','changed',true,'semanticAcknowledgement',false,
  'publicationOrder',h.publication_order,'positions',positions,'consumptions',consumptions,'sourceAllocations',facts);
 IF c.outcome IS DISTINCT FROM expected THEN RAISE EXCEPTION 'gas_owned_quantity_seal';END IF;
 PERFORM security.require_gas_owned_commit(h.catalog_id,c.authority_until,false,false);RETURN NULL;
END$$;
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_owned_quantity_corrections','gas_owned_quantity_position_members','gas_owned_quantity_consumption_members','gas_owned_quantity_source_members'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER complete_correction AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_owned_quantity_correction()',name);
 END LOOP;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY[
  'trading.gas_owned_position_quantity_snapshot(uuid,jsonb)'::regprocedure,'trading.gas_owned_consumption_quantity_snapshot(uuid,jsonb)'::regprocedure,
  'trading.require_gas_owned_quantity_references(uuid,uuid,jsonb)'::regprocedure,
  'trading.require_gas_owned_quantity_amendment(uuid,uuid,jsonb,uuid,timestamptz,uuid,integer)'::regprocedure,
  'trading.gas_owned_quantity_allocation_facts(uuid,uuid)'::regprocedure,
  'trading.apply_gas_owned_quantity_amendments(uuid,uuid,uuid,jsonb,uuid,timestamptz,uuid,integer)'::regprocedure,
  'security.reconcile_gas_owned_quantities(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'::regprocedure,
  'trading.gas_owned_quantity_position_proof(uuid,uuid,integer)'::regprocedure,'trading.gas_owned_quantity_consumption_proof(uuid,uuid,integer)'::regprocedure,
  'trading.guard_gas_owned_quantity_member()'::regprocedure,'trading.check_gas_owned_quantity_correction()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.reconcile_gas_owned_quantities(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean) TO openetrm_runtime;

ALTER FUNCTION trading.gas_owned_quantity_fact(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_owned_quantity_fact(jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
ALTER FUNCTION trading.gas_owned_quantity_terms(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_owned_quantity_terms(jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
ALTER FUNCTION trading.gas_owned_reconciliation_source_rows(uuid,uuid,uuid,timestamptz,uuid[],uuid[],uuid[],uuid[],boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_owned_reconciliation_source_rows(uuid,uuid,uuid,timestamptz,uuid[],uuid[],uuid[],uuid[],boolean) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
ALTER FUNCTION security.read_gas_owned_reconciliation(uuid,uuid,jsonb,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_reconciliation(uuid,uuid,jsonb,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_reconciliation(uuid,uuid,jsonb,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
