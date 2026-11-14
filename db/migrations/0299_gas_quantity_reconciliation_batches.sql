-- One explicit source closure may contain changed and unchanged native facts.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.gas_futures_quantity_batches(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,account_id uuid NOT NULL,publication_order integer NOT NULL,
 source_context_id uuid NOT NULL,as_of ref_data.instant NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 implementation text NOT NULL CHECK(implementation='gas_futures_quantity_batch_v1'),
 PRIMARY KEY(catalog_id,operation_id),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_owned_commands(catalog_id,request_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE trading.gas_futures_quantity_batch_members(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,performance_id uuid NOT NULL,performance_revision integer NOT NULL,changed boolean NOT NULL,
 PRIMARY KEY(catalog_id,operation_id,performance_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_futures_quantity_batches(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,performance_id,performance_revision) REFERENCES trading.gas_futures_performance_quantities(catalog_id,performance_id,revision)
);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_futures_quantity_batches','gas_futures_quantity_batch_members'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;

CREATE FUNCTION trading.gas_futures_quantity_batch_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE item jsonb;rows jsonb='[]';BEGIN
 PERFORM trading.gas_owned_projection_keys(body,ARRAY['corrections'],ARRAY[]::text[]);
 IF jsonb_typeof(body->'corrections') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'corrections') NOT BETWEEN 1 AND 16384
  OR pg_column_size(body)>8388608 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'corrections') LOOP
  item=trading.gas_futures_quantity_fact(item);
  IF(item->>'positionId')::uuid IS NULL OR(item->>'performanceId')::uuid IS NULL
   OR(item->>'positionId')::uuid='00000000-0000-0000-0000-000000000000'::uuid OR(item->>'performanceId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
  rows=rows||jsonb_build_array(item);
 END LOOP;
 IF jsonb_array_length(rows)<>(SELECT count(DISTINCT(value->>'performanceId')::uuid) FROM jsonb_array_elements(rows))
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(rows)r GROUP BY(r->>'positionId')::uuid HAVING count(DISTINCT(r->>'expectedPrincipalVersion')::integer)<>1)
 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 RETURN jsonb_build_object('corrections',(SELECT jsonb_agg(value ORDER BY(value->>'positionId')::uuid,(value->>'performanceId')::uuid) FROM jsonb_array_elements(rows)));
END$$;
CREATE FUNCTION trading.gas_futures_quantity_snapshot(catalog uuid,body jsonb) RETURNS integer LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT q.revision FROM trading.gas_futures_performance_quantities q
 WHERE(q.catalog_id,q.performance_id,q.quantity,q.retracted)=($1,($2->>'performanceId')::uuid,trading.gas_owned_quantity($2->>'quantity'),($2->>'retracted')::boolean)
  AND q.title_evidence_id IS NOT DISTINCT FROM($2->>'titleEvidenceId')::uuid
  AND(q.source_allocation=$2->>'sourceAllocation' OR q.revision=1 AND $2->>'sourceAllocation'='preserve_unallocated_source_quantity_v1')
  AND trading.gas_futures_quantity_supplies_match($1,q.performance_id,q.revision,$2->'supply') ORDER BY q.revision LIMIT 1
$$;

CREATE FUNCTION security.read_gas_futures_quantity_batch(catalog uuid,account uuid,body jsonb,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;item jsonb;source jsonb;rows jsonb='[]';result jsonb;head integer;BEGIN
 until_at=security.gas_futures_delivery_discovery_authority(catalog);
 body=trading.gas_futures_quantity_batch_fact(body);
 IF account IS NULL OR account='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'corrections') LOOP
  source=security.read_gas_futures_reconciliation(catalog,(item->>'positionId')::uuid,(item->>'performanceId')::uuid,context,as_of);
  until_at=least(until_at,(source->>'authorityUntil')::timestamptz);
  IF(source#>>'{position,account_id}')::uuid IS DISTINCT FROM account THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  SELECT max(q.revision) INTO head FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(catalog,(item->>'performanceId')::uuid);
  IF head IS DISTINCT FROM(source#>>'{performance,revision}')::integer THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
  rows=rows||jsonb_build_array(jsonb_build_object('selection',item,'source',source,'currentRevision',head,
   'acknowledgedRevision',trading.gas_futures_quantity_snapshot(catalog,item)));
  IF pg_column_size(rows)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 END LOOP;
 result=jsonb_build_object('implementation','gas_futures_quantity_batch_source_v1','accountId',account,'contextId',context,'asOf',as_of,
  'authorityUntil',until_at,'corrections',rows);
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);RETURN result;
EXCEPTION WHEN NO_DATA_FOUND THEN
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);RAISE EXCEPTION 'gas_quantity_source';
END$$;

CREATE FUNCTION trading.require_gas_quantity_batch_funding(catalog uuid,amended uuid[],body jsonb,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE positions uuid[];books uuid[]='{}';account trading.gas_owned_accounts;conflict jsonb;BEGIN
 positions=trading.gas_owned_financial_ancestors(catalog,ARRAY(SELECT(value->>'lotId')::uuid FROM jsonb_array_elements(body->'supply')
  WHERE trading.gas_owned_quantity(value->>'quantity')>0),context,as_of);
 FOR account IN SELECT DISTINCT a.* FROM trading.gas_owned_accounts a JOIN trading.gas_owned_positions p ON(p.catalog_id,p.account_id)=(a.catalog_id,a.id)
  WHERE p.catalog_id=catalog AND p.id=ANY(positions) LOOP
  IF security.reference_version_visible(account.reference_catalog_id,account.reference_revision_id) IS NOT TRUE OR security.market_fact_visible(catalog,account.definition_revision_id,NULL) IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
  PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,account.definition_revision_id);books=array_append(books,account.book_id);
 END LOOP;
 FOR conflict IN SELECT value FROM jsonb_array_elements(trading.gas_owned_applied_source_conflicts(catalog,context,as_of,books,positions)||
  trading.gas_owned_applied_observation_conflicts(catalog,context,as_of,books,positions)) LOOP
  IF conflict->>'sourceKind'='gas_evidence' AND(
   conflict->>'kind'='consumption' AND EXISTS(SELECT 1 FROM trading.gas_futures_supplies_at(catalog,context,as_of) s
    WHERE s.catalog_id=catalog AND s.performance_id=ANY(amended) AND s.consumption_id=(conflict->>'effectId')::uuid)
   OR conflict->>'kind'='position' AND EXISTS(SELECT 1 FROM trading.gas_futures_performances_at(catalog,context,as_of) p
    WHERE p.catalog_id=catalog AND p.id=ANY(amended) AND p.receipt_lot_id=(conflict->>'effectId')::uuid)) THEN CONTINUE;END IF;
  RAISE EXCEPTION 'gas_owned_reconciliation';
 END LOOP;
END$$;
CREATE TRIGGER native_publication BEFORE INSERT ON trading.gas_futures_quantity_batches FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication();

CREATE FUNCTION trading.require_gas_futures_quantity_amendment(catalog uuid,account uuid,body jsonb,context uuid,as_of timestamptz,
 policy uuid,policy_revision integer,amended uuid[]) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE performance trading.gas_futures_delivery_performances;p trading.gas_owned_positions;head trading.gas_futures_performance_quantities;
 a trading.gas_owned_accounts;stock trading.gas_owned_positions;stock_account trading.gas_owned_accounts;e trading.gas_owned_evidence;
 current_use trading.gas_owned_consumption_quantities;binding trading.gas_owned_action_bindings;principal integer;item jsonb;total numeric=0;BEGIN
 SELECT * INTO STRICT performance FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.position_id,x.id)=(catalog,(body->>'positionId')::uuid,(body->>'performanceId')::uuid);
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,performance.position_id,account);
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
 PERFORM trading.require_gas_futures_delivery_source(catalog,p.id,context,as_of);
 SELECT * INTO STRICT head FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(catalog,performance.id) ORDER BY q.revision DESC LIMIT 1;
 SELECT coalesce(max(r.revision),0) INTO principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,p.id);
 IF head.revision<>(body->>'expectedRevision')::integer OR principal<>(body->>'expectedPrincipalVersion')::integer
  OR trading.gas_owned_visible(catalog,head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
 SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,'gas.futures.reconcile_quantity') ORDER BY b.revision DESC LIMIT 1;
 IF(binding.policy_id,binding.policy_revision) IS DISTINCT FROM(policy,policy_revision) THEN RAISE EXCEPTION 'gas_owned_policy_changed';END IF;
 PERFORM lifecycle.require_policy(catalog,policy,policy_revision,'gas.futures.reconcile_quantity',as_of,false);
 IF performance.performed_at>as_of THEN RAISE EXCEPTION 'gas_quantity_time';END IF;
 IF p.quantity>0 THEN
  IF body->>'titleEvidenceId' IS NULL OR jsonb_array_length(body->'supply')<>0 THEN RAISE EXCEPTION 'gas_quantity_title';END IF;
  SELECT * INTO STRICT e FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(body->>'titleEvidenceId')::uuid,account);
  IF e.kind<>'title_receipt' OR e.occurred_at<>performance.performed_at OR e.quantity<trading.gas_owned_quantity(body->>'quantity')
   OR NOT trading.gas_owned_visible(catalog,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.account_id,x.source_key)=(catalog,e.account_id,e.source_key) AND x.revision>e.revision)
   OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_evidence old WHERE(old.catalog_id,old.id,old.account_id,old.source_key)=(catalog,performance.title_evidence_id,e.account_id,e.source_key))
  THEN RAISE EXCEPTION 'gas_quantity_evidence';END IF;
  IF EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,performance.receipt_lot_id)
   AND trading.gas_owned_visible(catalog,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE) THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
 ELSE
  IF body->>'titleEvidenceId' IS NOT NULL OR jsonb_array_length(body->'supply')=0 THEN RAISE EXCEPTION 'gas_quantity_supply';END IF;
  FOR item IN SELECT value FROM jsonb_array_elements(body->'supply') LOOP
   SELECT * INTO STRICT stock FROM trading.gas_owned_positions_current x WHERE(x.catalog_id,x.id,x.kind)=(catalog,(item->>'lotId')::uuid,'owned_stock');
   SELECT * INTO STRICT stock_account FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,stock.account_id);
   PERFORM trading.require_gas_owned_head(catalog,stock.account_id,context,as_of);
   PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,stock_account.definition_revision_id);
   SELECT * INTO STRICT e FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(item->>'evidenceId')::uuid,stock.account_id);
   IF e.kind<>'physical_delivery' OR e.occurred_at<>performance.performed_at OR e.quantity<trading.gas_owned_quantity(item->>'quantity')
    OR NOT trading.gas_owned_visible(catalog,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
    OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.account_id,x.source_key)=(catalog,e.account_id,e.source_key) AND x.revision>e.revision)
    OR(stock_account.book_id,stock_account.legal_entity_id,stock_account.legal_entity_revision,stock_account.reference_catalog_id,stock_account.reference_revision_id,stock_account.quantity_code,stock_account.location_code,stock_account.currency_code)
     IS DISTINCT FROM(a.book_id,a.legal_entity_id,a.legal_entity_revision,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.location_code,a.currency_code)
   THEN RAISE EXCEPTION 'gas_quantity_supply';END IF;
   IF item->>'consumptionId' IS NOT NULL THEN
    SELECT * INTO STRICT current_use FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
    IF current_use.revision<>(item->>'expectedRevision')::integer OR NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s
      WHERE(s.catalog_id,s.performance_id,s.performance_revision,s.consumption_id,s.consumption_revision)=(catalog,performance.id,head.revision,current_use.consumption_id,current_use.revision))
     OR NOT trading.gas_owned_visible(catalog,current_use.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) THEN RAISE EXCEPTION 'gas_quantity_supply';END IF;
    IF(current_use.lot_id,current_use.quantity,current_use.evidence_id,current_use.reservation_id,current_use.retracted) IS DISTINCT FROM
      ((item->>'lotId')::uuid,trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,(item->>'retracted')::boolean)
     AND EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities old WHERE(old.catalog_id,old.consumption_id)=(catalog,current_use.consumption_id)
      AND(old.lot_id,old.quantity,old.evidence_id,old.reservation_id,old.retracted) IS NOT DISTINCT FROM
       ((item->>'lotId')::uuid,trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,(item->>'retracted')::boolean))
    THEN RAISE EXCEPTION 'gas_quantity_source_already_applied';END IF;
   END IF;
   total=total+trading.gas_owned_quantity(item->>'quantity');
  END LOOP;
  IF total<>trading.gas_owned_quantity(body->>'quantity') OR EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s
   WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(catalog,performance.id,head.revision)
    AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(body->'supply') chosen WHERE(chosen->>'consumptionId')::uuid=s.consumption_id)) THEN RAISE EXCEPTION 'gas_quantity_supply';END IF;
  PERFORM trading.require_gas_quantity_batch_funding(catalog,amended,body,context,as_of);
 END IF;
END$$;

CREATE FUNCTION trading.apply_gas_futures_quantity_amendment(catalog uuid,body jsonb,actor uuid,publication integer) RETURNS integer
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE performance trading.gas_futures_delivery_performances;p trading.gas_owned_positions;head trading.gas_futures_performance_quantities;
 stock trading.gas_owned_position_quantities;current_use trading.gas_owned_consumption_quantities;revision integer;use_revision integer;identifier uuid;ordinal integer=0;item jsonb;BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.publications x WHERE(x.catalog_id,x.publication_order,x.publication_xid)=(catalog,publication,pg_current_xact_id())) THEN RAISE EXCEPTION 'gas_owned_publication';END IF;
 SELECT * INTO STRICT performance FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.position_id,x.id)=(catalog,(body->>'positionId')::uuid,(body->>'performanceId')::uuid);
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(catalog,performance.position_id);
 SELECT * INTO STRICT head FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(catalog,performance.id) ORDER BY q.revision DESC LIMIT 1;
 IF head.revision<>(body->>'expectedRevision')::integer THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;revision=head.revision+1;
 INSERT INTO trading.gas_futures_performance_quantities VALUES(catalog,performance.id,revision,head.revision,trading.gas_owned_quantity(body->>'quantity'),(body->>'titleEvidenceId')::uuid,(body->>'retracted')::boolean,body->>'sourceAllocation',actor,publication);
 IF p.quantity>0 THEN
  SELECT * INTO STRICT stock FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,performance.receipt_lot_id) ORDER BY q.revision DESC LIMIT 1;
  INSERT INTO trading.gas_owned_position_quantities VALUES(catalog,performance.receipt_lot_id,p.account_id,stock.revision+1,stock.revision,
   trading.gas_owned_quantity(body->>'quantity'),(body->>'titleEvidenceId')::uuid,(body->>'retracted')::boolean,actor,publication);
 ELSE
  FOR item IN SELECT value FROM jsonb_array_elements(body->'supply') LOOP
   ordinal=ordinal+1;
   IF item->>'consumptionId' IS NULL THEN
    identifier=gen_random_uuid();
    INSERT INTO trading.gas_owned_consumptions VALUES(catalog,identifier,(item->>'lotId')::uuid,'delivery',trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,actor,publication,performance.performed_at);
    use_revision=1;
   ELSE
    identifier=(item->>'consumptionId')::uuid;
    SELECT * INTO STRICT current_use FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,identifier) ORDER BY q.revision DESC LIMIT 1;
    IF current_use.revision<>(item->>'expectedRevision')::integer THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
    use_revision=current_use.revision;
    IF(current_use.lot_id,current_use.quantity,current_use.evidence_id,current_use.reservation_id,current_use.retracted) IS DISTINCT FROM
      ((item->>'lotId')::uuid,trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,(item->>'retracted')::boolean) THEN
     use_revision=use_revision+1;
     INSERT INTO trading.gas_owned_consumption_quantities VALUES(catalog,identifier,use_revision,current_use.revision,
      (item->>'lotId')::uuid,trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,(item->>'retracted')::boolean,actor,publication);
    END IF;
   END IF;
   INSERT INTO trading.gas_futures_performance_quantity_supplies VALUES(catalog,performance.id,revision,ordinal,identifier,use_revision);
  END LOOP;
 END IF;RETURN revision;
END$$;

CREATE FUNCTION security.reconcile_gas_futures_delivery_batch(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,
 policy uuid,policy_revision integer,receipt_only boolean,continuation jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;canonical jsonb;prior trading.gas_owned_commands;item jsonb;state jsonb;states jsonb='[]';results jsonb='[]';
 a trading.gas_owned_accounts;link settlement_data.futures_delivery_obligations;profile settlement_data.futures_delivery_profiles;terms trading.gas_futures_delivery_terms;
 head trading.gas_futures_performance_quantities;acknowledged integer;principal integer;publication integer;revision integer;all_ack boolean=true;
 amended uuid[];changed_positions uuid[];held_position uuid;final record;proposal jsonb;facts jsonb;source jsonb;outcome jsonb;BEGIN
 admission=security.gas_owned_admission(catalog,'futures_quantity_reconciliation_batch',body,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR account IS NULL OR context IS NULL
  OR policy IS NULL OR policy_revision IS NULL OR policy_revision<1 OR receipt_only IS NULL OR as_of IS NULL OR NOT isfinite(as_of)
 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 body=trading.gas_futures_quantity_batch_fact(body);IF continuation='null'::jsonb THEN continuation=NULL;END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
 FOR item IN SELECT value FROM jsonb_array_elements(body->'corrections') LOOP
  SELECT x.* INTO STRICT link FROM settlement_data.futures_delivery_obligations x JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(x.catalog_id,x.position_id)
   WHERE(x.catalog_id,x.position_id,p.account_id)=(catalog,(item->>'positionId')::uuid,account);
  SELECT * INTO STRICT profile FROM settlement_data.futures_delivery_profiles p WHERE(p.catalog_id,p.id,p.revision)=(catalog,link.profile_id,link.profile_revision);
  SELECT * INTO STRICT terms FROM trading.gas_futures_delivery_terms t WHERE(t.catalog_id,t.profile_id,t.profile_revision)=(catalog,link.profile_id,link.profile_revision);
  PERFORM settlement_data.require_futures_delivery_reference(profile,terms);
  PERFORM trading.require_gas_quantity_references(catalog,account,item);
 END LOOP;
 canonical=jsonb_build_object('action','futures_quantity_reconciliation_batch','accountId',account,'body',body,'asOf',as_of,'contextId',context,
  'policyId',policy,'policyRevision',policy_revision,'automatic',false,'continuation',continuation);
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR(CASE WHEN receipt_only THEN prior.request-'continuation' IS DISTINCT FROM canonical-'continuation' ELSE prior.request IS DISTINCT FROM canonical END)
  THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  FOR item IN SELECT value FROM jsonb_array_elements(prior.outcome->'corrections') LOOP
   PERFORM trading.require_gas_quantity_snapshot_references(catalog,(item->>'performanceId')::uuid,(item->>'acknowledgedRevision')::integer);
  END LOOP;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object('outcome',prior.outcome,'continuation',prior.request->'continuation');END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR item IN SELECT value FROM jsonb_array_elements(body->'corrections') LOOP
  SELECT q.* INTO STRICT head FROM trading.gas_futures_performance_quantities q JOIN trading.gas_futures_delivery_performances p
   ON(p.catalog_id,p.id)=(q.catalog_id,q.performance_id) WHERE(q.catalog_id,q.performance_id,p.position_id)=(catalog,(item->>'performanceId')::uuid,(item->>'positionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_futures_quantity_snapshot(catalog,item);all_ack=all_ack AND acknowledged IS NOT NULL;
  states=states||jsonb_build_array(jsonb_build_object('selection',item,'currentRevision',head.revision,'acknowledgedRevision',acknowledged));
 END LOOP;
 IF NOT all_ack AND receipt_only THEN
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);RETURN NULL;
 END IF;
 IF NOT all_ack THEN
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(states)s WHERE s->>'acknowledgedRevision' IS NOT NULL AND s->>'acknowledgedRevision'<>s->>'currentRevision')
  THEN RAISE EXCEPTION 'gas_quantity_batch_history';END IF;
  amended=ARRAY(SELECT(s#>>'{selection,performanceId}')::uuid FROM jsonb_array_elements(states)s WHERE s->>'acknowledgedRevision' IS NULL);
  changed_positions=ARRAY(SELECT DISTINCT(s#>>'{selection,positionId}')::uuid FROM jsonb_array_elements(states)s WHERE s->>'acknowledgedRevision' IS NULL);
  PERFORM trading.gas_owned_projection_keys(continuation,ARRAY['implementation','positions'],ARRAY[]::text[]);
  IF continuation->>'implementation' IS DISTINCT FROM 'gas_futures_quantity_batch_continuation_v1' OR jsonb_typeof(continuation->'positions') IS DISTINCT FROM 'array'
   OR cardinality(changed_positions)<>jsonb_array_length(continuation->'positions')
   OR jsonb_array_length(continuation->'positions')<>(SELECT count(DISTINCT(value->>'positionId')::uuid) FROM jsonb_array_elements(continuation->'positions'))
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(continuation->'positions')p WHERE NOT((p->>'positionId')::uuid=ANY(changed_positions)))
  THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
  FOR proposal IN SELECT value FROM jsonb_array_elements(continuation->'positions') LOOP PERFORM trading.require_futures_delivery_continuation(proposal);END LOOP;
  -- Validate every source before any financial head is advanced. Unchanged
  -- members still carry explicit current versions and are part of the seal.
  FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
   item=state->'selection';
   IF state->>'acknowledgedRevision' IS NULL THEN
    PERFORM trading.require_gas_futures_quantity_amendment(catalog,account,item,context,as_of,policy,policy_revision,amended);
   ELSE
    PERFORM trading.require_gas_futures_delivery_source(catalog,(item->>'positionId')::uuid,context,as_of);
    SELECT coalesce(max(r.revision),0) INTO principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,(item->>'positionId')::uuid);
    IF(state->>'currentRevision')::integer<>(item->>'expectedRevision')::integer OR principal<>(item->>'expectedPrincipalVersion')::integer THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
   END IF;
  END LOOP;
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_futures_quantity_batches VALUES(catalog,operation,account,publication,context,as_of,actor,'gas_futures_quantity_batch_v1');
  FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
   item=state->'selection';revision=(state->>'currentRevision')::integer;
   IF state->>'acknowledgedRevision' IS NULL THEN revision=trading.apply_gas_futures_quantity_amendment(catalog,item,actor,publication);END IF;
   INSERT INTO trading.gas_futures_quantity_batch_members VALUES(catalog,operation,(item->>'performanceId')::uuid,revision,state->>'acknowledgedRevision' IS NULL);
  END LOOP;
  FOR held_position IN SELECT unnest(changed_positions) LOOP
   SELECT * INTO STRICT final FROM settlement_data.read_futures_delivery_final(catalog,held_position,context,as_of);
   SELECT value INTO STRICT proposal FROM jsonb_array_elements(continuation->'positions') WHERE(value->>'positionId')::uuid=held_position;
   principal=trading.apply_futures_delivery_principal(catalog,held_position,final.determination_id,context,as_of,actor,publication,proposal);
  END LOOP;
  FOR state IN SELECT value FROM jsonb_array_elements(states) WHERE value->>'acknowledgedRevision' IS NULL LOOP
   item=state->'selection';revision=(state->>'currentRevision')::integer+1;
   facts=trading.gas_futures_allocation_facts(catalog,(item->>'performanceId')::uuid,revision);
   PERFORM trading.require_gas_source_allocation(item->>'sourceAllocation',facts);
   FOR source IN SELECT value FROM jsonb_array_elements(facts) LOOP
    INSERT INTO trading.gas_futures_quantity_sources VALUES(catalog,(item->>'performanceId')::uuid,revision,(source->>'accountId')::uuid,(source->>'sourceEvidenceId')::uuid,(source->>'oldEvidenceId')::uuid);
   END LOOP;
  END LOOP;
 END IF;
 FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
  item=state->'selection';
  SELECT * INTO STRICT head FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(catalog,(item->>'performanceId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  SELECT coalesce(max(r.revision),0) INTO principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,(item->>'positionId')::uuid);
  acknowledged=CASE WHEN all_ack THEN(state->>'acknowledgedRevision')::integer ELSE head.revision END;
  PERFORM trading.require_gas_quantity_snapshot_references(catalog,(item->>'performanceId')::uuid,acknowledged);
  results=results||jsonb_build_array(jsonb_build_object('performanceId',(item->>'performanceId')::uuid,'positionId',(item->>'positionId')::uuid,
   'acknowledgedRevision',acknowledged,'currentRevision',head.revision,'currentPrincipalVersion',principal,'currentQuantity',trim_scale(head.quantity)::text,
   'changed',NOT all_ack AND state->>'acknowledgedRevision' IS NULL,'sourceAllocation',item->>'sourceAllocation',
   'sourceAllocations',CASE WHEN NOT all_ack AND state->>'acknowledgedRevision' IS NULL THEN trading.gas_futures_allocation_facts(catalog,(item->>'performanceId')::uuid,head.revision) ELSE NULL END));
 END LOOP;
 outcome=jsonb_build_object('operationId',operation,'accountId',account,'action','futures_quantity_reconciliation_batch','changed',NOT all_ack,
  'semanticAcknowledgement',all_ack,'publicationOrder',publication,'corrections',results);
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,(admission->>'until')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object('outcome',outcome,'continuation',continuation);END IF;RETURN outcome;
END$$;

CREATE FUNCTION trading.gas_quantity_batch_position(catalog uuid,publication integer,holding uuid) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batches b JOIN trading.gas_futures_quantity_batch_members m
  ON(m.catalog_id,m.operation_id)=(b.catalog_id,b.operation_id) JOIN trading.gas_futures_delivery_performances p
   ON(p.catalog_id,p.id)=(m.catalog_id,m.performance_id)
  WHERE(b.catalog_id,b.publication_order,p.receipt_lot_id)=($1,$2,$3))
$$;
CREATE FUNCTION trading.gas_quantity_batch_consumption(catalog uuid,publication integer,consumption uuid,before_change boolean) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batches b JOIN trading.gas_futures_quantity_batch_members m
  ON(m.catalog_id,m.operation_id)=(b.catalog_id,b.operation_id) JOIN trading.gas_futures_performance_quantities q
   ON(q.catalog_id,q.performance_id,q.revision)=(m.catalog_id,m.performance_id,m.performance_revision)
  JOIN trading.gas_futures_performance_quantity_supplies s ON(s.catalog_id,s.performance_id)=(q.catalog_id,q.performance_id)
   AND s.performance_revision=CASE WHEN $4 AND m.changed THEN q.previous_revision ELSE q.revision END
  WHERE(b.catalog_id,b.publication_order,s.consumption_id)=($1,$2,$3))
$$;
CREATE FUNCTION trading.gas_quantity_batch_reconciles(catalog uuid,publication integer,context uuid,as_of timestamptz,conflict jsonb) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT $5->>'sourceKind'='gas_evidence' AND EXISTS(
  SELECT 1 FROM trading.gas_futures_quantity_batches b JOIN trading.publications pub ON(pub.catalog_id,pub.publication_order)=(b.catalog_id,b.publication_order)
  JOIN trading.gas_futures_quantity_batch_members m ON(m.catalog_id,m.operation_id)=(b.catalog_id,b.operation_id)
  JOIN trading.gas_futures_performance_quantities q ON(q.catalog_id,q.performance_id,q.revision,q.publication_order,q.actor_id)=
   (m.catalog_id,m.performance_id,m.performance_revision,b.publication_order,b.actor_id)
  JOIN trading.gas_futures_delivery_performances p ON(p.catalog_id,p.id)=(q.catalog_id,q.performance_id)
  WHERE(b.catalog_id,b.publication_order,b.source_context_id,b.as_of,pub.publication_xid)=($1,$2,$3,$4,pg_current_xact_id_if_assigned()) AND m.changed
   AND($5->>'kind'='position' AND p.receipt_lot_id=($5->>'effectId')::uuid OR $5->>'kind'='consumption' AND EXISTS(
    SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s WHERE(s.catalog_id,s.performance_id,s.consumption_id)=(q.catalog_id,q.performance_id,($5->>'effectId')::uuid)
     AND s.performance_revision IN(q.revision,q.previous_revision))))
$$;

-- Preserve the public identities and exact predecessor authority contracts.
DO $guard$DECLARE spec record;p pg_proc;definition text;needle text;replacement text;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('trading.gas_futures_allocation_facts(uuid,uuid,integer)','bbb28ed1d3aa4f1d0715c997d7807442','jsonb',false,ARRAY['catalog','performance','version'],'v'),
  ('trading.require_gas_owned_financial_sources(uuid,uuid,text,jsonb,uuid,timestamptz,integer)','f1c200d5f0b6c09ecb628b90ea8b69e4','void',false,ARRAY['catalog','account','action','body','context','as_of','own_publication'],'v'),
  ('security.reconcile_gas_futures_delivery(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)','1b6c9b72559a24c6865fd1b097c6d626','jsonb',true,
   ARRAY['catalog','operation','holding','body','as_of','context','policy','policy_revision','receipt_only','continuation'],'v'),
  ('trading.gas_futures_quantity_source(uuid,uuid,timestamptz,jsonb)','9bd8a43e74962c1b8f696a96e65153c9','jsonb',false,ARRAY['catalog','context','as_of','rows'],'s')
 )s(signature,hash,result_type,runtime_execute,names,volatility) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=spec.signature::regprocedure;
  IF md5(p.prosrc)<>spec.hash OR p.proowner<>'openetrm_security_owner'::regrole OR NOT p.prosecdef OR p.prokind<>'f' OR p.prorettype<>spec.result_type::regtype
   OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.proargnames IS DISTINCT FROM spec.names
   OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.proisstrict OR p.proleakproof OR p.proparallel<>'u' OR p.provolatile::text<>spec.volatility
   OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR has_function_privilege('openetrm_runtime',p.oid,'EXECUTE') IS DISTINCT FROM spec.runtime_execute OR has_function_privilege('openetrm_risk_worker',p.oid,'EXECUTE')
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner)))a WHERE a.grantor<>p.proowner OR a.is_grantable OR a.privilege_type<>'EXECUTE'
    OR a.grantee NOT IN(p.proowner,CASE WHEN spec.runtime_execute THEN 'openetrm_runtime'::regrole ELSE p.proowner END))
  THEN RAISE EXCEPTION 'gas_quantity_batch_upgrade: unexpected helper %',spec.signature;END IF;
 END LOOP;
 definition=pg_get_functiondef('trading.gas_futures_allocation_facts(uuid,uuid,integer)'::regprocedure);
 needle='IF EXISTS(SELECT 1 FROM unnest(before_ids||after_ids) id WHERE id<>native.receipt_lot_id)';
 replacement='IF EXISTS(SELECT 1 FROM unnest(before_ids||after_ids) AS scoped(id) WHERE scoped.id<>native.receipt_lot_id AND NOT trading.gas_quantity_batch_position(catalog,selected.publication_order,scoped.id))';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_batch_upgrade';END IF;definition=replace(definition,needle,replacement);
 needle='FROM unnest(before_ids) AS target(id) WHERE NOT EXISTS';
 replacement='FROM unnest(before_ids) AS target(id) WHERE NOT trading.gas_quantity_batch_consumption(catalog,selected.publication_order,target.id,true) AND NOT EXISTS';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_batch_upgrade';END IF;definition=replace(definition,needle,replacement);
 needle='FROM unnest(after_ids) AS target(id) WHERE NOT EXISTS';
 replacement='FROM unnest(after_ids) AS target(id) WHERE NOT trading.gas_quantity_batch_consumption(catalog,selected.publication_order,target.id,false) AND NOT EXISTS';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_batch_upgrade';END IF;EXECUTE replace(definition,needle,replacement);
 definition=pg_get_functiondef('trading.require_gas_owned_financial_sources(uuid,uuid,text,jsonb,uuid,timestamptz,integer)'::regprocedure);
 needle=' FOR item IN SELECT value FROM jsonb_array_elements(conflicts) LOOP';
 replacement=needle||E'\n  IF action=\'futures_delivery_principal\' AND own_publication IS NOT NULL AND trading.gas_quantity_batch_reconciles(catalog,own_publication,context,as_of,item) THEN CONTINUE;END IF;';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_batch_upgrade';END IF;EXECUTE replace(definition,needle,replacement);
 definition=pg_get_functiondef('security.reconcile_gas_futures_delivery(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)'::regprocedure);
 -- The first-outcome and older-financial-snapshot paths stay byte-for-byte.
 -- Only admitted new effects share the same validation/mutation producers.
 needle=substring(definition FROM '  PERFORM trading.require_gas_futures_delivery_source\(catalog,holding,context,as_of\);.*?  principal=trading.apply_futures_delivery_principal');
 IF needle IS NULL OR(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_batch_upgrade';END IF;
 replacement=$shared$  PERFORM trading.require_gas_futures_quantity_amendment(catalog,a.id,body,context,as_of,policy,policy_revision,ARRAY[performance.id]);
  SELECT * INTO STRICT final FROM settlement_data.read_futures_delivery_final(catalog,holding,context,as_of);
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  revision=trading.apply_gas_futures_quantity_amendment(catalog,body,actor,publication);
  principal=trading.apply_futures_delivery_principal$shared$;
 definition=replace(definition,needle,replacement);
 definition=replace(definition,'a trading.gas_owned_accounts;p trading.gas_owned_positions;performance trading.gas_futures_delivery_performances;','a trading.gas_owned_accounts;performance trading.gas_futures_delivery_performances;');
 definition=replace(definition,'current_head trading.gas_futures_performance_quantities;stock_head trading.gas_owned_position_quantities;','current_head trading.gas_futures_performance_quantities;');
 definition=replace(definition,E' current_use trading.gas_owned_consumption_quantities;selected_stock trading.gas_owned_positions;selected_account trading.gas_owned_accounts;\n','');
 definition=replace(definition,'evidence trading.gas_owned_evidence;binding trading.gas_owned_action_bindings;principal integer;','principal integer;');
 definition=replace(definition,'outcome jsonb;item jsonb;identifier uuid;ordinal integer=0;','outcome jsonb;item jsonb;');
 definition=replace(definition,'final record;total numeric=0;acknowledged_revision integer;acknowledged_principal integer;use_revision integer;allocation_facts jsonb;','final record;acknowledged_revision integer;acknowledged_principal integer;allocation_facts jsonb;');
 EXECUTE definition;
 definition=pg_get_functiondef('trading.gas_futures_quantity_source(uuid,uuid,timestamptz,jsonb)'::regprocedure);
 needle='result jsonb=''[]'';head trading.gas_futures_performance_quantities;';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_batch_upgrade';END IF;
 definition=replace(definition,needle,needle||'original_publication integer;');
 needle='  result=result||jsonb_build_array(item||jsonb_build_object(';
 replacement=$origin$  SELECT p.publication_order INTO STRICT original_publication FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.id)=(catalog,head.performance_id);
  IF NOT trading.gas_owned_visible(catalog,original_publication,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  item=item||jsonb_build_object('original_publication_order',original_publication);
  result=result||jsonb_build_array(item||jsonb_build_object($origin$;
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_batch_upgrade';END IF;
 EXECUTE replace(definition,needle,replacement);
END$guard$;

CREATE FUNCTION trading.guard_gas_quantity_batch_member() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batches b JOIN trading.publications p ON(p.catalog_id,p.publication_order)=(b.catalog_id,b.publication_order)
  WHERE(b.catalog_id,b.operation_id,p.publication_xid)=(NEW.catalog_id,NEW.operation_id,pg_current_xact_id())) THEN RAISE EXCEPTION 'futures_delivery_sealed';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER sealed_batch BEFORE INSERT ON trading.gas_futures_quantity_batch_members FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_quantity_batch_member();
CREATE FUNCTION trading.require_gas_quantity_batch_continuation(catalog uuid,operation uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE batch trading.gas_futures_quantity_batches;command trading.gas_owned_commands;proposal jsonb;
 principal trading.gas_futures_delivery_principal_runs;link settlement_data.futures_delivery_obligations;
 final settlement_data.futures_margin_determinations;positions integer;BEGIN
 SELECT * INTO STRICT batch FROM trading.gas_futures_quantity_batches b WHERE(b.catalog_id,b.operation_id)=(catalog,operation);
 SELECT * INTO STRICT command FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 PERFORM trading.gas_owned_projection_keys(command.request->'continuation',ARRAY['implementation','positions'],ARRAY[]::text[]);
 IF command.request#>>'{continuation,implementation}' IS DISTINCT FROM 'gas_futures_quantity_batch_continuation_v1'
  OR jsonb_typeof(command.request#>'{continuation,positions}') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
 SELECT count(DISTINCT p.position_id) INTO positions FROM trading.gas_futures_quantity_batch_members m JOIN trading.gas_futures_delivery_performances p
  ON(p.catalog_id,p.id)=(m.catalog_id,m.performance_id) WHERE(m.catalog_id,m.operation_id,m.changed)=(catalog,operation,true);
 IF positions NOT BETWEEN 1 AND 16384 OR positions<>jsonb_array_length(command.request#>'{continuation,positions}')
  OR positions<>(SELECT count(DISTINCT(value->>'positionId')::uuid) FROM jsonb_array_elements(command.request#>'{continuation,positions}'))
 THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
 FOR proposal IN SELECT value FROM jsonb_array_elements(command.request#>'{continuation,positions}') LOOP
  PERFORM trading.require_futures_delivery_continuation(proposal);
  SELECT * INTO STRICT principal FROM trading.gas_futures_delivery_principal_runs r
   WHERE(r.catalog_id,r.position_id,r.publication_order)=(catalog,(proposal->>'positionId')::uuid,batch.publication_order);
  SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations l WHERE(l.catalog_id,l.position_id)=(catalog,principal.position_id);
  SELECT * INTO STRICT final FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.id)=(catalog,principal.final_determination_id);
  IF(proposal->>'expectedVersion')::integer IS DISTINCT FROM principal.revision-1
   OR(proposal->>'finalOccurrenceId')::uuid IS DISTINCT FROM link.final_occurrence_id
   OR(proposal->>'sourceObservationId')::uuid IS DISTINCT FROM final.source_observation_id
   OR principal.performance_count<>jsonb_array_length(proposal->'steps')
   OR ARRAY(SELECT(value->>'performanceId')::uuid FROM jsonb_array_elements(proposal->'steps') WITH ORDINALITY s(value,n) ORDER BY n)
    IS DISTINCT FROM ARRAY(SELECT p.id FROM trading.gas_futures_delivery_principal_determinations d JOIN trading.gas_futures_delivery_performances p
     ON(p.catalog_id,p.id)=(d.catalog_id,d.performance_id) WHERE(d.catalog_id,d.position_id,d.run_revision)=(catalog,principal.position_id,principal.revision)
     ORDER BY p.performed_at,convert_to(p.business_reference,'UTF8'))
   OR NOT EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batch_members m JOIN trading.gas_futures_delivery_performances p
    ON(p.catalog_id,p.id)=(m.catalog_id,m.performance_id) WHERE(m.catalog_id,m.operation_id,m.changed,p.position_id)=(catalog,operation,true,principal.position_id))
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(proposal->'steps') s LEFT JOIN trading.gas_futures_delivery_principal_determinations d
    ON(d.catalog_id,d.position_id,d.run_revision,d.performance_id)=(catalog,principal.position_id,principal.revision,(s->>'performanceId')::uuid)
    WHERE d.id IS NULL OR(d.previous_id,d.cumulative_quantity,d.exact_total,d.rounded_total,d.contribution,d.adjustment)
     IS DISTINCT FROM((s->>'previousDeterminationId')::uuid,trading.gas_owned_quantity(s->>'cumulativeQuantity'),s->>'exactTotal',
      trading.gas_owned_quantity(s->>'roundedTotal'),trading.gas_owned_quantity(s->>'contribution'),trading.gas_owned_quantity(s->>'adjustment')))
  THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
 END LOOP;
EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'gas_quantity_batch_seal';
END$$;
CREATE FUNCTION trading.check_gas_quantity_batch() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b trading.gas_futures_quantity_batches;c trading.gas_owned_commands;m trading.gas_futures_quantity_batch_members;
 q trading.gas_futures_performance_quantities;p trading.gas_futures_delivery_performances;item jsonb;reported jsonb;counted integer;current_principal integer;BEGIN
 SELECT * INTO STRICT b FROM trading.gas_futures_quantity_batches x WHERE(x.catalog_id,x.operation_id)=(NEW.catalog_id,NEW.operation_id);
 SELECT * INTO STRICT c FROM trading.gas_owned_commands x WHERE(x.catalog_id,x.request_id)=(b.catalog_id,b.operation_id);
 -- These checks independently seal the persisted command, including writes
 -- made outside the public producer. Missing JSON values cannot skip a fence.
 BEGIN
  PERFORM trading.gas_owned_projection_keys(c.request,ARRAY['action','accountId','body','asOf','contextId','policyId','policyRevision','automatic','continuation'],ARRAY[]::text[]);
  PERFORM trading.gas_owned_projection_keys(c.outcome,ARRAY['operationId','accountId','action','changed','semanticAcknowledgement','publicationOrder','corrections'],ARRAY[]::text[]);
  IF jsonb_typeof(c.request#>'{body,corrections}') IS DISTINCT FROM 'array' OR jsonb_typeof(c.outcome->'corrections') IS DISTINCT FROM 'array'
   OR trading.gas_futures_quantity_batch_fact(c.request->'body') IS DISTINCT FROM c.request->'body'
  THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
  FOR reported IN SELECT value FROM jsonb_array_elements(c.outcome->'corrections') LOOP
   PERFORM trading.gas_owned_projection_keys(reported,ARRAY['performanceId','positionId','acknowledgedRevision','currentRevision','currentPrincipalVersion','currentQuantity','changed','sourceAllocation','sourceAllocations'],ARRAY['sourceAllocations']);
   IF jsonb_typeof(reported->'performanceId') IS DISTINCT FROM 'string' OR jsonb_typeof(reported->'positionId') IS DISTINCT FROM 'string'
    OR jsonb_typeof(reported->'acknowledgedRevision') IS DISTINCT FROM 'number' OR reported->>'acknowledgedRevision'!~'^[1-9][0-9]{0,8}$'
    OR jsonb_typeof(reported->'currentRevision') IS DISTINCT FROM 'number' OR reported->>'currentRevision'!~'^[1-9][0-9]{0,8}$'
    OR jsonb_typeof(reported->'currentPrincipalVersion') IS DISTINCT FROM 'number' OR reported->>'currentPrincipalVersion'!~'^[1-9][0-9]{0,8}$'
    OR jsonb_typeof(reported->'currentQuantity') IS DISTINCT FROM 'string' OR jsonb_typeof(reported->'changed') IS DISTINCT FROM 'boolean'
    OR jsonb_typeof(reported->'sourceAllocation') IS DISTINCT FROM 'string'
   THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
  END LOOP;
 EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END;
 IF(c.actor_id,c.account_id,c.request->>'action',(c.request->>'accountId')::uuid,(c.request->>'contextId')::uuid,(c.request->>'asOf')::timestamptz,
   (c.request->>'automatic')::boolean,(c.outcome->>'operationId')::uuid,(c.outcome->>'accountId')::uuid,c.outcome->>'action',
   (c.outcome->>'publicationOrder')::integer,(c.outcome->>'changed')::boolean,(c.outcome->>'semanticAcknowledgement')::boolean) IS DISTINCT FROM
  (b.actor_id,b.account_id,'futures_quantity_reconciliation_batch',b.account_id,b.source_context_id,b.as_of,false,b.operation_id,b.account_id,'futures_quantity_reconciliation_batch',b.publication_order,true,false)
  OR NOT EXISTS(SELECT 1 FROM trading.publications pub WHERE(pub.catalog_id,pub.publication_order,pub.publication_xid)=(b.catalog_id,b.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
 SELECT count(*) INTO counted FROM trading.gas_futures_quantity_batch_members x WHERE(x.catalog_id,x.operation_id)=(b.catalog_id,b.operation_id);
 IF counted NOT BETWEEN 1 AND 16384 OR counted<>jsonb_array_length(c.request#>'{body,corrections}') OR counted<>jsonb_array_length(c.outcome->'corrections')
  OR counted<>(SELECT count(DISTINCT(value->>'performanceId')::uuid) FROM jsonb_array_elements(c.outcome->'corrections'))
  OR NOT EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batch_members x WHERE(x.catalog_id,x.operation_id)=(b.catalog_id,b.operation_id) AND x.changed)
 THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(c.request#>'{body,corrections}') LOOP
  SELECT * INTO STRICT m FROM trading.gas_futures_quantity_batch_members x WHERE(x.catalog_id,x.operation_id,x.performance_id)=(b.catalog_id,b.operation_id,(item->>'performanceId')::uuid);
  SELECT * INTO STRICT q FROM trading.gas_futures_performance_quantities x WHERE(x.catalog_id,x.performance_id,x.revision)=(m.catalog_id,m.performance_id,m.performance_revision);
  SELECT * INTO STRICT p FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.id,x.position_id)=(m.catalog_id,m.performance_id,(item->>'positionId')::uuid);
  SELECT coalesce(max(r.revision),0) INTO current_principal FROM trading.gas_futures_delivery_principal_runs r
   WHERE(r.catalog_id,r.position_id)=(b.catalog_id,p.position_id) AND r.publication_order<=b.publication_order;
  SELECT value INTO STRICT reported FROM jsonb_array_elements(c.outcome->'corrections') WHERE(value->>'performanceId')::uuid=p.id;
  IF q.revision<>(item->>'expectedRevision')::integer+(CASE WHEN m.changed THEN 1 ELSE 0 END)
   OR q.revision<>(SELECT max(x.revision) FROM trading.gas_futures_performance_quantities x WHERE(x.catalog_id,x.performance_id)=(q.catalog_id,q.performance_id) AND x.publication_order<=b.publication_order)
   OR m.changed AND(q.publication_order,q.actor_id) IS DISTINCT FROM(b.publication_order,b.actor_id)
   OR NOT m.changed AND q.publication_order>=b.publication_order
   OR(q.quantity,q.retracted) IS DISTINCT FROM(trading.gas_owned_quantity(item->>'quantity'),(item->>'retracted')::boolean)
   OR q.title_evidence_id IS DISTINCT FROM(item->>'titleEvidenceId')::uuid
   OR NOT(q.source_allocation=item->>'sourceAllocation' OR NOT m.changed AND q.revision=1 AND item->>'sourceAllocation'='preserve_unallocated_source_quantity_v1')
   OR NOT trading.gas_futures_quantity_supplies_match(b.catalog_id,p.id,q.revision,item->'supply')
   OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_positions owner WHERE(owner.catalog_id,owner.id,owner.account_id)=(b.catalog_id,p.position_id,b.account_id))
   OR((reported->>'positionId')::uuid,(reported->>'acknowledgedRevision')::integer,(reported->>'currentRevision')::integer,
    (reported->>'currentPrincipalVersion')::integer,trading.gas_owned_quantity(reported->>'currentQuantity'),(reported->>'changed')::boolean)
    IS DISTINCT FROM(p.position_id,q.revision,q.revision,current_principal,q.quantity,m.changed)
   OR reported->>'sourceAllocation' IS DISTINCT FROM item->>'sourceAllocation'
   OR reported->'sourceAllocations' IS DISTINCT FROM(CASE WHEN m.changed THEN trading.gas_futures_allocation_facts(b.catalog_id,p.id,q.revision) ELSE 'null'::jsonb END)
  THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
  IF m.changed AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_delivery_principal_runs r WHERE
   (r.catalog_id,r.position_id,r.publication_order,r.source_context_id,r.as_of,r.actor_id)=(b.catalog_id,p.position_id,b.publication_order,b.source_context_id,b.as_of,b.actor_id)
   AND r.revision=(item->>'expectedPrincipalVersion')::integer+1) THEN RAISE EXCEPTION 'gas_quantity_batch_principal';END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantities financial WHERE(financial.catalog_id,financial.publication_order)=(b.catalog_id,b.publication_order)
  AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batch_members member_link WHERE(member_link.catalog_id,member_link.operation_id,member_link.performance_id,member_link.performance_revision,member_link.changed)=
   (b.catalog_id,b.operation_id,financial.performance_id,financial.revision,true)))
  OR EXISTS(SELECT 1 FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.publication_order)=(b.catalog_id,b.publication_order) AND NOT EXISTS(
   SELECT 1 FROM trading.gas_futures_quantity_batch_members member_link JOIN trading.gas_futures_delivery_performances origin ON(origin.catalog_id,origin.id)=(member_link.catalog_id,member_link.performance_id)
    WHERE(member_link.catalog_id,member_link.operation_id,origin.position_id,member_link.changed)=(b.catalog_id,b.operation_id,r.position_id,true))) THEN RAISE EXCEPTION 'gas_quantity_batch_seal';END IF;
 PERFORM trading.require_gas_quantity_batch_continuation(b.catalog_id,b.operation_id);
 RETURN NULL;
EXCEPTION WHEN NO_DATA_FOUND OR TOO_MANY_ROWS THEN RAISE EXCEPTION 'gas_quantity_batch_seal';
END$$;
CREATE CONSTRAINT TRIGGER complete_batch AFTER INSERT ON trading.gas_futures_quantity_batches DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.check_gas_quantity_batch();
CREATE CONSTRAINT TRIGGER complete_batch AFTER INSERT ON trading.gas_futures_quantity_batch_members DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.check_gas_quantity_batch();

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY[
  'trading.gas_futures_quantity_batch_fact(jsonb)'::regprocedure,
  'trading.gas_futures_quantity_snapshot(uuid,jsonb)'::regprocedure,'trading.require_gas_quantity_batch_funding(uuid,uuid[],jsonb,uuid,timestamptz)'::regprocedure,
  'trading.require_gas_futures_quantity_amendment(uuid,uuid,jsonb,uuid,timestamptz,uuid,integer,uuid[])'::regprocedure,
  'trading.apply_gas_futures_quantity_amendment(uuid,jsonb,uuid,integer)'::regprocedure,
  'security.read_gas_futures_quantity_batch(uuid,uuid,jsonb,uuid,timestamptz)'::regprocedure,
  'security.reconcile_gas_futures_delivery_batch(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)'::regprocedure,
  'trading.gas_quantity_batch_position(uuid,integer,uuid)'::regprocedure,'trading.gas_quantity_batch_consumption(uuid,integer,uuid,boolean)'::regprocedure,
  'trading.gas_quantity_batch_reconciles(uuid,integer,uuid,timestamptz,jsonb)'::regprocedure,
  'trading.guard_gas_quantity_batch_member()'::regprocedure,'trading.require_gas_quantity_batch_continuation(uuid,uuid)'::regprocedure,
  'trading.check_gas_quantity_batch()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.reconcile_gas_futures_delivery_batch(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.read_gas_futures_quantity_batch(uuid,uuid,jsonb,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
