-- Ordinary title and accepted futures delivery funding reconcile atomically.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
-- A concrete ordinary-title/resource and physical-performance correction.
-- Native product writers keep their existing typed receipts and seals; this
-- header ties them to one publication and exact prevalidated source closure.
CREATE TABLE trading.gas_quantity_bundles(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,account_id uuid NOT NULL,physical_account_id uuid NOT NULL,
 ordinary_operation_id uuid NOT NULL,physical_operation_id uuid NOT NULL,publication_order integer,read_publication_order integer NOT NULL,
 source_context_id uuid NOT NULL,as_of ref_data.instant NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 ordinary_changed boolean NOT NULL,physical_changed boolean NOT NULL,
 implementation text NOT NULL CHECK(implementation='ordinary_futures_quantity_bundle_v1'),
 PRIMARY KEY(catalog_id,operation_id),UNIQUE(catalog_id,publication_order),
 CHECK((publication_order IS NOT NULL)=(ordinary_changed OR physical_changed)),
 CHECK(operation_id<>ordinary_operation_id AND operation_id<>physical_operation_id AND ordinary_operation_id<>physical_operation_id),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,physical_account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,read_publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_owned_commands(catalog_id,request_id) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,ordinary_operation_id) REFERENCES trading.gas_owned_commands(catalog_id,request_id) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,physical_operation_id) REFERENCES trading.gas_owned_commands(catalog_id,request_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE trading.gas_quantity_bundle_titles(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,position_id uuid NOT NULL,expected_revision integer NOT NULL,
 source_evidence_id uuid NOT NULL,changed boolean NOT NULL,
 PRIMARY KEY(catalog_id,operation_id,position_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_quantity_bundles(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,position_id,expected_revision) REFERENCES trading.gas_owned_position_quantities(catalog_id,position_id,revision),
 FOREIGN KEY(catalog_id,source_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id)
);
CREATE TABLE trading.gas_quantity_bundle_uses(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,consumption_id uuid NOT NULL,expected_revision integer NOT NULL,
 source_evidence_id uuid NOT NULL,changed boolean NOT NULL,
 PRIMARY KEY(catalog_id,operation_id,consumption_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_quantity_bundles(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,consumption_id,expected_revision) REFERENCES trading.gas_owned_consumption_quantities(catalog_id,consumption_id,revision),
 FOREIGN KEY(catalog_id,source_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id)
);
CREATE TABLE trading.gas_quantity_bundle_performances(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,performance_id uuid NOT NULL,expected_revision integer NOT NULL,
 source_evidence_id uuid,changed boolean NOT NULL,
 PRIMARY KEY(catalog_id,operation_id,performance_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_quantity_bundles(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,performance_id,expected_revision) REFERENCES trading.gas_futures_performance_quantities(catalog_id,performance_id,revision),
 FOREIGN KEY(catalog_id,source_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id)
);
CREATE TABLE trading.gas_quantity_bundle_supplies(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,performance_id uuid NOT NULL,consumption_id uuid NOT NULL,
 expected_revision integer NOT NULL,source_evidence_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,operation_id,performance_id,consumption_id),
 FOREIGN KEY(catalog_id,operation_id,performance_id) REFERENCES trading.gas_quantity_bundle_performances(catalog_id,operation_id,performance_id),
 FOREIGN KEY(catalog_id,consumption_id,expected_revision) REFERENCES trading.gas_owned_consumption_quantities(catalog_id,consumption_id,revision),
 FOREIGN KEY(catalog_id,source_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id)
);

CREATE FUNCTION trading.guard_gas_quantity_bundle() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE header trading.gas_quantity_bundles;actor uuid;BEGIN
 SELECT q.actor_id INTO actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF TG_TABLE_NAME='gas_quantity_bundles' THEN header=NEW;
 ELSE SELECT * INTO STRICT header FROM trading.gas_quantity_bundles b WHERE(b.catalog_id,b.operation_id)=(NEW.catalog_id,NEW.operation_id);END IF;
 IF header.actor_id IS DISTINCT FROM actor OR header.publication_order IS NOT NULL AND NOT EXISTS(
  SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(header.catalog_id,header.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'gas_quantity_bundle_sealed';END IF;
 RETURN NEW;
END$$;
DO $tables$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_quantity_bundles','gas_quantity_bundle_titles','gas_quantity_bundle_uses','gas_quantity_bundle_performances','gas_quantity_bundle_supplies'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('REVOKE ALL ON trading.%I FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
  EXECUTE format('CREATE TRIGGER native_bundle BEFORE INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_quantity_bundle()',name);
 END LOOP;
END$tables$;

-- Only this exact native source conflict is covered. Resource capacity,
-- contractual conservation, current revisions and both product seals remain
-- mandatory. An acknowledgement never authorizes a staged source transition.
CREATE FUNCTION trading.gas_quantity_bundle_allows(catalog uuid,account uuid,context uuid,as_of timestamptz,
 body jsonb,conflict jsonb,physical boolean) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE header trading.gas_quantity_bundles;item jsonb;matched boolean;BEGIN
 IF conflict->>'sourceKind' IS DISTINCT FROM 'gas_evidence' THEN RETURN false;END IF;
 FOR header IN SELECT b.* FROM trading.gas_quantity_bundles b JOIN trading.publications p
  ON(p.catalog_id,p.publication_order,p.publication_xid)=(b.catalog_id,b.publication_order,pg_current_xact_id_if_assigned())
  JOIN security.request_contexts q ON q.actor_id=b.actor_id AND q.backend_pid=pg_backend_pid()
   AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp()
  WHERE(b.catalog_id,b.source_context_id,b.as_of)=($1,$3,$4)
   AND(CASE WHEN $7 THEN b.physical_account_id ELSE b.account_id END)=$2 LOOP
  IF physical THEN
   IF NOT EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_performances m
    WHERE(m.catalog_id,m.operation_id,m.performance_id,m.expected_revision)=
     (catalog,header.operation_id,(body->>'performanceId')::uuid,(body->>'expectedRevision')::integer)) THEN CONTINUE;END IF;
  ELSE
   IF jsonb_array_length(body->'positions')<>(SELECT count(*) FROM trading.gas_quantity_bundle_titles m WHERE(m.catalog_id,m.operation_id)=(catalog,header.operation_id))
    OR jsonb_array_length(body->'consumptions')<>(SELECT count(*) FROM trading.gas_quantity_bundle_uses m WHERE(m.catalog_id,m.operation_id)=(catalog,header.operation_id))
   THEN CONTINUE;END IF;
   matched=true;
   FOR item IN SELECT value FROM jsonb_array_elements(body->'positions') LOOP
    matched=matched AND EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_titles m WHERE(m.catalog_id,m.operation_id,m.position_id,m.expected_revision,m.source_evidence_id)=
     (catalog,header.operation_id,(item->>'positionId')::uuid,(item->>'expectedRevision')::integer,(item->>'evidenceId')::uuid));
   END LOOP;
   FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
    matched=matched AND EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_uses m WHERE(m.catalog_id,m.operation_id,m.consumption_id,m.expected_revision,m.source_evidence_id)=
     (catalog,header.operation_id,(item->>'consumptionId')::uuid,(item->>'expectedRevision')::integer,(item->>'evidenceId')::uuid));
   END LOOP;
   IF NOT matched THEN CONTINUE;END IF;
  END IF;
  IF conflict->>'kind'='position' AND(
   EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_titles m WHERE(m.catalog_id,m.operation_id,m.position_id,m.changed)=
    (catalog,header.operation_id,(conflict->>'effectId')::uuid,true) AND conflict->'currentSourceIds'=jsonb_build_array(m.source_evidence_id))
   OR EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_performances m JOIN trading.gas_futures_delivery_performances p
    ON(p.catalog_id,p.id)=(m.catalog_id,m.performance_id) WHERE(m.catalog_id,m.operation_id,p.receipt_lot_id,m.changed)=
     (catalog,header.operation_id,(conflict->>'effectId')::uuid,true) AND conflict->'currentSourceIds'=jsonb_build_array(m.source_evidence_id)))
   OR conflict->>'kind'='consumption' AND(
   EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_uses m WHERE(m.catalog_id,m.operation_id,m.consumption_id,m.changed)=
    (catalog,header.operation_id,(conflict->>'effectId')::uuid,true) AND conflict->'currentSourceIds'=jsonb_build_array(m.source_evidence_id))
   OR EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_supplies s JOIN trading.gas_quantity_bundle_performances m
    ON(m.catalog_id,m.operation_id,m.performance_id)=(s.catalog_id,s.operation_id,s.performance_id)
    WHERE(s.catalog_id,s.operation_id,s.consumption_id,m.changed)=(catalog,header.operation_id,(conflict->>'effectId')::uuid,true)
     AND conflict->'currentSourceIds'=jsonb_build_array(s.source_evidence_id))) THEN RETURN true;END IF;
 END LOOP;
 RETURN false;
END$$;

-- Sibling effects are recognized through both native product journals and this
-- exact shared publication. They never exempt an unproven financial row.
CREATE FUNCTION trading.gas_quantity_bundle_physical_position(catalog uuid,publication integer,ordinary_operation uuid,holding uuid,version integer)
 RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_quantity_bundles b JOIN trading.publications p
  ON(p.catalog_id,p.publication_order,p.publication_xid)=(b.catalog_id,b.publication_order,pg_current_xact_id_if_assigned())
  WHERE(b.catalog_id,b.publication_order,b.ordinary_operation_id,b.physical_changed)=($1,$2,$3,true)
   AND trading.gas_quantity_batch_position($1,$2,$4)
   AND EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances origin
    JOIN trading.gas_futures_quantity_batch_members member ON(member.catalog_id,member.operation_id,member.performance_id,member.changed)=
     (origin.catalog_id,b.physical_operation_id,origin.id,true)
    JOIN trading.gas_futures_performance_quantities quantity ON(quantity.catalog_id,quantity.performance_id,quantity.revision,quantity.publication_order)=
     (member.catalog_id,member.performance_id,member.performance_revision,b.publication_order)
    JOIN trading.gas_owned_position_quantities receipt ON(receipt.catalog_id,receipt.position_id,receipt.revision,receipt.publication_order,receipt.actor_id,receipt.quantity,receipt.evidence_id,receipt.retracted)=
     (origin.catalog_id,origin.receipt_lot_id,$5,b.publication_order,b.actor_id,quantity.quantity,quantity.title_evidence_id,quantity.retracted)
    WHERE(origin.catalog_id,origin.receipt_lot_id)=($1,$4)))
$$;
CREATE FUNCTION trading.gas_quantity_bundle_physical_consumption(catalog uuid,publication integer,ordinary_operation uuid,consumption uuid,version integer)
 RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_quantity_bundles b JOIN trading.publications p
  ON(p.catalog_id,p.publication_order,p.publication_xid)=(b.catalog_id,b.publication_order,pg_current_xact_id_if_assigned())
  WHERE(b.catalog_id,b.publication_order,b.ordinary_operation_id,b.physical_changed)=($1,$2,$3,true)
   AND trading.gas_quantity_batch_consumption($1,$2,$4,false)
   AND EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batch_members member
    JOIN trading.gas_futures_performance_quantities quantity ON(quantity.catalog_id,quantity.performance_id,quantity.revision,quantity.publication_order)=
     (member.catalog_id,member.performance_id,member.performance_revision,b.publication_order)
    JOIN trading.gas_futures_performance_quantity_supplies supply ON(supply.catalog_id,supply.performance_id,supply.performance_revision)=
     (quantity.catalog_id,quantity.performance_id,quantity.revision)
    JOIN trading.gas_owned_consumption_quantities use ON(use.catalog_id,use.consumption_id,use.revision,use.publication_order,use.actor_id)=
     (supply.catalog_id,supply.consumption_id,supply.consumption_revision,b.publication_order,b.actor_id)
    WHERE(member.catalog_id,member.operation_id,member.changed,supply.consumption_id,supply.consumption_revision)=($1,b.physical_operation_id,true,$4,$5)))
$$;
CREATE FUNCTION trading.gas_quantity_bundle_ordinary_reconciles(catalog uuid,publication integer,context uuid,as_of timestamptz,conflict jsonb)
 RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT $5->>'sourceKind'='gas_evidence' AND EXISTS(
  SELECT 1 FROM trading.gas_quantity_bundles b JOIN trading.publications p
   ON(p.catalog_id,p.publication_order,p.publication_xid)=(b.catalog_id,b.publication_order,pg_current_xact_id_if_assigned())
  WHERE(b.catalog_id,b.publication_order,b.source_context_id,b.as_of,b.ordinary_changed)=($1,$2,$3,$4,true) AND(
   $5->>'kind'='position' AND EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_titles m
    JOIN trading.gas_owned_quantity_position_members n ON(n.catalog_id,n.operation_id,n.position_id,n.changed)=
     (m.catalog_id,b.ordinary_operation_id,m.position_id,true)
    JOIN trading.gas_owned_position_quantities q ON(q.catalog_id,q.position_id,q.revision,q.publication_order,q.evidence_id)=
     (n.catalog_id,n.position_id,n.revision,b.publication_order,m.source_evidence_id)
    WHERE(m.catalog_id,m.operation_id,m.position_id,m.changed)=($1,b.operation_id,($5->>'effectId')::uuid,true)
     AND $5->'currentSourceIds'=jsonb_build_array(m.source_evidence_id)
     AND trading.gas_owned_quantity_position_proof(q.catalog_id,q.position_id,q.revision))
   OR $5->>'kind'='consumption' AND EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_uses m
    JOIN trading.gas_owned_quantity_consumption_members n ON(n.catalog_id,n.operation_id,n.consumption_id,n.changed)=
     (m.catalog_id,b.ordinary_operation_id,m.consumption_id,true)
    JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.revision,q.publication_order,q.evidence_id)=
     (n.catalog_id,n.consumption_id,n.revision,b.publication_order,m.source_evidence_id)
    WHERE(m.catalog_id,m.operation_id,m.consumption_id,m.changed)=($1,b.operation_id,($5->>'effectId')::uuid,true)
     AND $5->'currentSourceIds'=jsonb_build_array(m.source_evidence_id)
     AND trading.gas_owned_quantity_consumption_proof(q.catalog_id,q.consumption_id,q.revision))))
$$;

CREATE FUNCTION trading.check_gas_quantity_bundle() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b trading.gas_quantity_bundles;c trading.gas_owned_commands;o trading.gas_owned_commands;f trading.gas_owned_commands;
 item jsonb;supply jsonb;expected jsonb;counted integer;ordinary jsonb;physical jsonb;
 results jsonb;uses jsonb;head trading.gas_owned_position_quantities;used trading.gas_owned_consumption_quantities;
 performance trading.gas_futures_performance_quantities;acknowledged integer;principal integer;BEGIN
 SELECT * INTO STRICT b FROM trading.gas_quantity_bundles x WHERE(x.catalog_id,x.operation_id)=(NEW.catalog_id,NEW.operation_id);
 SELECT * INTO STRICT c FROM trading.gas_owned_commands x WHERE(x.catalog_id,x.request_id)=(b.catalog_id,b.operation_id);
 SELECT * INTO STRICT o FROM trading.gas_owned_commands x WHERE(x.catalog_id,x.request_id)=(b.catalog_id,b.ordinary_operation_id);
 SELECT * INTO STRICT f FROM trading.gas_owned_commands x WHERE(x.catalog_id,x.request_id)=(b.catalog_id,b.physical_operation_id);
 PERFORM trading.gas_owned_projection_keys(c.request,ARRAY['action','accountId','body','asOf','contextId','ordinaryPolicyId','ordinaryPolicyRevision',
  'physicalPolicyId','physicalPolicyRevision','automatic','continuation'],ARRAY['continuation']);
 PERFORM trading.gas_owned_projection_keys(c.request->'body',ARRAY['ordinary','physical','physicalAccountId'],ARRAY[]::text[]);
 ordinary=c.request#>'{body,ordinary}';physical=c.request#>'{body,physical}';
 IF jsonb_typeof(ordinary) IS DISTINCT FROM 'object' OR jsonb_typeof(ordinary->'positions') IS DISTINCT FROM 'array'
  OR jsonb_typeof(ordinary->'consumptions') IS DISTINCT FROM 'array' OR jsonb_typeof(physical) IS DISTINCT FROM 'object'
  OR jsonb_typeof(physical->'corrections') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 IF trading.gas_owned_quantity_terms(ordinary) IS DISTINCT FROM ordinary
  OR trading.gas_futures_quantity_batch_fact(physical) IS DISTINCT FROM physical
 THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 -- Product seals validate the complete normalized bodies. This outer seal
 -- checks their exact canonical linkage without exception subtransactions.
 IF(c.actor_id,c.account_id,c.request->>'action',(c.request->>'accountId')::uuid,(c.request->>'asOf')::timestamptz,
   (c.request->>'contextId')::uuid,(c.request->>'automatic')::boolean,c.configuring,c.requires_jobs)
  IS DISTINCT FROM(b.actor_id,b.account_id,'quantity_reconciliation_bundle',b.account_id,b.as_of,b.source_context_id,false,false,false)
  OR(c.request#>>'{body,physicalAccountId}')::uuid IS DISTINCT FROM b.physical_account_id
  OR(o.actor_id,o.account_id,o.configuring,o.requires_jobs) IS DISTINCT FROM(b.actor_id,b.account_id,false,false)
  OR(f.actor_id,f.account_id,f.configuring,f.requires_jobs) IS DISTINCT FROM(b.actor_id,b.physical_account_id,false,false)
  OR o.request IS DISTINCT FROM jsonb_build_object('action','owned_quantity_reconciliation','accountId',b.account_id,'body',ordinary,
   'asOf',b.as_of,'contextId',b.source_context_id,'policyId',c.request->'ordinaryPolicyId','policyRevision',c.request->'ordinaryPolicyRevision','automatic',false)
  OR f.request IS DISTINCT FROM jsonb_build_object('action','futures_quantity_reconciliation_batch','accountId',b.physical_account_id,'body',physical,
   'asOf',b.as_of,'contextId',b.source_context_id,'policyId',c.request->'physicalPolicyId','policyRevision',c.request->'physicalPolicyRevision','automatic',false,
   'continuation',c.request->'continuation')
  OR(o.outcome->>'changed')::boolean IS DISTINCT FROM b.ordinary_changed
  OR(f.outcome->>'changed')::boolean IS DISTINCT FROM b.physical_changed
  OR(o.outcome->>'publicationOrder')::integer IS DISTINCT FROM(CASE WHEN b.ordinary_changed THEN b.publication_order ELSE NULL END)
  OR(f.outcome->>'publicationOrder')::integer IS DISTINCT FROM(CASE WHEN b.physical_changed THEN b.publication_order ELSE NULL END)
  OR(o.outcome->>'operationId')::uuid IS DISTINCT FROM b.ordinary_operation_id
  OR(f.outcome->>'operationId')::uuid IS DISTINCT FROM b.physical_operation_id
 THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 PERFORM security.require_gas_owned_commit(b.catalog_id,c.authority_until,false,false);
 PERFORM security.require_gas_owned_commit(b.catalog_id,o.authority_until,false,false);
 PERFORM security.require_gas_owned_commit(b.catalog_id,f.authority_until,false,false);
 IF b.publication_order IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.publications p
  WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(b.catalog_id,b.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 IF NOT b.ordinary_changed THEN
  results='[]';uses='[]';
  FOR item IN SELECT value FROM jsonb_array_elements(ordinary->'positions') LOOP
   acknowledged=trading.gas_owned_position_quantity_snapshot(b.catalog_id,item);
   IF acknowledged IS NULL OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities q
    WHERE(q.catalog_id,q.position_id,q.revision)=(b.catalog_id,(item->>'positionId')::uuid,acknowledged)
     AND q.publication_order<=b.read_publication_order) THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
   SELECT * INTO STRICT head FROM trading.gas_owned_position_quantities q
    WHERE(q.catalog_id,q.position_id)=(b.catalog_id,(item->>'positionId')::uuid) AND q.publication_order<=b.read_publication_order ORDER BY q.revision DESC LIMIT 1;
   results=results||jsonb_build_array(jsonb_build_object('positionId',head.position_id,'acknowledgedRevision',acknowledged,'currentRevision',head.revision,
    'currentQuantity',trim_scale(head.quantity)::text,'evidenceId',head.evidence_id,'changed',false));
  END LOOP;
  FOR item IN SELECT value FROM jsonb_array_elements(ordinary->'consumptions') LOOP
   acknowledged=trading.gas_owned_consumption_quantity_snapshot(b.catalog_id,item);
   IF acknowledged IS NULL OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities q
    WHERE(q.catalog_id,q.consumption_id,q.revision)=(b.catalog_id,(item->>'consumptionId')::uuid,acknowledged)
     AND q.publication_order<=b.read_publication_order) THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
   SELECT * INTO STRICT used FROM trading.gas_owned_consumption_quantities q
    WHERE(q.catalog_id,q.consumption_id)=(b.catalog_id,(item->>'consumptionId')::uuid) AND q.publication_order<=b.read_publication_order ORDER BY q.revision DESC LIMIT 1;
   uses=uses||jsonb_build_array(jsonb_build_object('consumptionId',used.consumption_id,'acknowledgedRevision',acknowledged,'currentRevision',used.revision,
    'currentQuantity',trim_scale(used.quantity)::text,'lotId',used.lot_id,'evidenceId',used.evidence_id,'reservationId',used.reservation_id,'changed',false));
  END LOOP;
  expected=jsonb_build_object('operationId',b.ordinary_operation_id,'accountId',b.account_id,'action','owned_quantity_reconciliation','changed',false,
   'semanticAcknowledgement',true,'publicationOrder',NULL,'positions',results,'consumptions',uses,'sourceAllocations',NULL);
  IF o.outcome IS DISTINCT FROM expected THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 END IF;
 IF NOT b.physical_changed THEN
  results='[]';
  FOR item IN SELECT value FROM jsonb_array_elements(physical->'corrections') LOOP
   acknowledged=trading.gas_futures_quantity_snapshot(b.catalog_id,item);
   IF acknowledged IS NULL OR NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantities q
    WHERE(q.catalog_id,q.performance_id,q.revision)=(b.catalog_id,(item->>'performanceId')::uuid,acknowledged)
     AND q.publication_order<=b.read_publication_order) THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
   SELECT * INTO STRICT performance FROM trading.gas_futures_performance_quantities q
    WHERE(q.catalog_id,q.performance_id)=(b.catalog_id,(item->>'performanceId')::uuid) AND q.publication_order<=b.read_publication_order ORDER BY q.revision DESC LIMIT 1;
   SELECT coalesce(max(r.revision),0) INTO principal FROM trading.gas_futures_delivery_principal_runs r
    WHERE(r.catalog_id,r.position_id)=(b.catalog_id,(item->>'positionId')::uuid) AND r.publication_order<=b.read_publication_order;
   results=results||jsonb_build_array(jsonb_build_object('performanceId',performance.performance_id,'positionId',(item->>'positionId')::uuid,
    'acknowledgedRevision',acknowledged,'currentRevision',performance.revision,'currentPrincipalVersion',principal,'currentQuantity',trim_scale(performance.quantity)::text,
    'changed',false,'sourceAllocation',item->>'sourceAllocation','sourceAllocations',NULL));
  END LOOP;
  expected=jsonb_build_object('operationId',b.physical_operation_id,'accountId',b.physical_account_id,'action','futures_quantity_reconciliation_batch',
   'changed',false,'semanticAcknowledgement',true,'publicationOrder',NULL,'corrections',results);
  IF f.outcome IS DISTINCT FROM expected THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 END IF;
 SELECT count(*) INTO counted FROM trading.gas_quantity_bundle_titles m WHERE(m.catalog_id,m.operation_id)=(b.catalog_id,b.operation_id);
 IF counted<>jsonb_array_length(ordinary->'positions') THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(ordinary->'positions') LOOP
  IF NOT EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_titles m WHERE(m.catalog_id,m.operation_id,m.position_id,m.expected_revision,m.source_evidence_id)=
   (b.catalog_id,b.operation_id,(item->>'positionId')::uuid,(item->>'expectedRevision')::integer,(item->>'evidenceId')::uuid)
   AND m.changed=EXISTS(SELECT 1 FROM trading.gas_owned_quantity_position_members n
    WHERE(n.catalog_id,n.operation_id,n.position_id,n.changed)=(b.catalog_id,b.ordinary_operation_id,m.position_id,true)))
  THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 END LOOP;
 SELECT count(*) INTO counted FROM trading.gas_quantity_bundle_uses m WHERE(m.catalog_id,m.operation_id)=(b.catalog_id,b.operation_id);
 IF counted<>jsonb_array_length(ordinary->'consumptions') THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(ordinary->'consumptions') LOOP
  IF NOT EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_uses m WHERE(m.catalog_id,m.operation_id,m.consumption_id,m.expected_revision,m.source_evidence_id)=
   (b.catalog_id,b.operation_id,(item->>'consumptionId')::uuid,(item->>'expectedRevision')::integer,(item->>'evidenceId')::uuid)
   AND m.changed=EXISTS(SELECT 1 FROM trading.gas_owned_quantity_consumption_members n
    WHERE(n.catalog_id,n.operation_id,n.consumption_id,n.changed)=(b.catalog_id,b.ordinary_operation_id,m.consumption_id,true)))
  THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 END LOOP;
 SELECT count(*) INTO counted FROM trading.gas_quantity_bundle_performances m WHERE(m.catalog_id,m.operation_id)=(b.catalog_id,b.operation_id);
 IF counted<>jsonb_array_length(physical->'corrections') THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(physical->'corrections') LOOP
  IF NOT EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_performances m WHERE(m.catalog_id,m.operation_id,m.performance_id,m.expected_revision)=
   (b.catalog_id,b.operation_id,(item->>'performanceId')::uuid,(item->>'expectedRevision')::integer)
   AND m.source_evidence_id IS NOT DISTINCT FROM(item->>'titleEvidenceId')::uuid
   AND m.changed=EXISTS(SELECT 1 FROM trading.gas_futures_quantity_batch_members n
    WHERE(n.catalog_id,n.operation_id,n.performance_id,n.changed)=(b.catalog_id,b.physical_operation_id,m.performance_id,true)))
  THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
  SELECT count(*) INTO counted FROM trading.gas_quantity_bundle_supplies m
   WHERE(m.catalog_id,m.operation_id,m.performance_id)=(b.catalog_id,b.operation_id,(item->>'performanceId')::uuid);
  IF counted<>(SELECT count(*) FROM jsonb_array_elements(item->'supply')s WHERE s->>'consumptionId' IS NOT NULL)
  THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
  FOR supply IN SELECT value FROM jsonb_array_elements(item->'supply') WHERE value->>'consumptionId' IS NOT NULL LOOP
   IF NOT EXISTS(SELECT 1 FROM trading.gas_quantity_bundle_supplies m
    WHERE(m.catalog_id,m.operation_id,m.performance_id,m.consumption_id,m.expected_revision,m.source_evidence_id)=
    (b.catalog_id,b.operation_id,(item->>'performanceId')::uuid,(supply->>'consumptionId')::uuid,(supply->>'expectedRevision')::integer,(supply->>'evidenceId')::uuid))
   THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
  END LOOP;
 END LOOP;
 expected=jsonb_build_object('operationId',b.operation_id,'accountId',b.account_id,'action','quantity_reconciliation_bundle',
  'changed',b.ordinary_changed OR b.physical_changed,'semanticAcknowledgement',NOT(b.ordinary_changed OR b.physical_changed),
  'publicationOrder',b.publication_order,'ordinary',o.outcome,'physical',f.outcome);
 IF c.outcome IS DISTINCT FROM expected THEN RAISE EXCEPTION 'gas_quantity_bundle_seal';END IF;
 PERFORM security.require_gas_owned_commit(b.catalog_id,c.authority_until,false,false);RETURN NULL;
END$$;
DO $tables$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_quantity_bundles','gas_quantity_bundle_titles','gas_quantity_bundle_uses','gas_quantity_bundle_performances','gas_quantity_bundle_supplies'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER complete_bundle AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_quantity_bundle()',name);
 END LOOP;
END$tables$;

DO $upgrade$DECLARE spec record;p pg_proc;definition text;needle text;replacement text;body text;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('security.reconcile_gas_owned_quantities(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)','f7911bd2b89743c2a3d3bc4c9d68c31b','jsonb',true,ARRAY['catalog','operation','account','body','as_of','context','policy','policy_revision','receipt_only'],'v','plpgsql',true),
  ('security.reconcile_gas_futures_delivery_batch(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)','5dcea3c0bc6dee8acf0aab5aa18e4a7b','jsonb',true,ARRAY['catalog','operation','account','body','as_of','context','policy','policy_revision','receipt_only','continuation'],'v','plpgsql',true),
  ('trading.require_gas_owned_quantity_amendment(uuid,uuid,jsonb,uuid,timestamptz,uuid,integer)','95d3a3e0234f19fe148179c69a81f6d6','void',false,ARRAY['catalog','account','body','context','as_of','policy','policy_revision'],'v','plpgsql',true),
  ('trading.require_gas_quantity_batch_funding(uuid,uuid[],jsonb,uuid,timestamptz)','24d9e9942a57fe1e5c3ab1c8b42acc1e','void',false,ARRAY['catalog','amended','body','context','as_of'],'v','plpgsql',true),
  ('trading.check_gas_owned_quantity_correction()','3a533e94fc283fe2fe0c121a71a38f9c','trigger',false,NULL::text[],'v','plpgsql',true),
  ('trading.gas_quantity_batch_reconciles(uuid,integer,uuid,timestamptz,jsonb)','35254ebb05a6a8208883076552ff1bb9','boolean',false,ARRAY['catalog','publication','context','as_of','conflict'],'s','sql',true),
  ('trading.gas_owned_quantity_terms(jsonb)','9421ea07bacaff6f79b68ccb3d55deb9','jsonb',false,ARRAY['body'],'i','plpgsql',false),
  ('trading.gas_futures_quantity_batch_fact(jsonb)','05511be4cbf76a33adde56122bc7db50','jsonb',false,ARRAY['body'],'i','plpgsql',false)
 )s(signature,hash,result_type,runtime_execute,names,volatility,language,definer) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=spec.signature::regprocedure;
  IF md5(p.prosrc)<>spec.hash OR p.proowner<>'openetrm_security_owner'::regrole OR p.prosecdef IS DISTINCT FROM spec.definer OR p.prokind<>'f' OR p.prorettype<>spec.result_type::regtype
   OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.proargnames IS DISTINCT FROM spec.names
   OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.proisstrict OR p.proleakproof OR p.proparallel<>'u' OR p.provolatile::text<>spec.volatility
   OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>spec.language OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR has_function_privilege('openetrm_runtime',p.oid,'EXECUTE') IS DISTINCT FROM spec.runtime_execute OR has_function_privilege('openetrm_risk_worker',p.oid,'EXECUTE')
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner)))a WHERE a.grantor<>p.proowner OR a.is_grantable OR a.privilege_type<>'EXECUTE'
    OR a.grantee NOT IN(p.proowner,CASE WHEN spec.runtime_execute THEN 'openetrm_runtime'::regrole ELSE p.proowner END))
  THEN RAISE EXCEPTION 'gas_quantity_bundle_upgrade: unexpected helper %',spec.signature;END IF;
 END LOOP;
 definition=pg_get_functiondef('trading.require_gas_owned_quantity_amendment(uuid,uuid,jsonb,uuid,timestamptz,uuid,integer)'::regprocedure);
 needle='  RAISE EXCEPTION ''gas_owned_pending_source_reconciliation'';';replacement='  IF trading.gas_quantity_bundle_allows(catalog,account,context,as_of,body,conflict,false) THEN CONTINUE;END IF;
  RAISE EXCEPTION ''gas_owned_pending_source_reconciliation'';';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_bundle_upgrade';END IF;
 EXECUTE replace(definition,needle,replacement);
 definition=pg_get_functiondef('trading.require_gas_quantity_batch_funding(uuid,uuid[],jsonb,uuid,timestamptz)'::regprocedure);
 needle='  RAISE EXCEPTION ''gas_owned_reconciliation'';';replacement='  IF trading.gas_quantity_bundle_allows(catalog,(SELECT p.account_id FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(catalog,(body->>''positionId'')::uuid)),context,as_of,body,conflict,true) THEN CONTINUE;END IF;
  RAISE EXCEPTION ''gas_owned_reconciliation'';';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_bundle_upgrade';END IF;
 EXECUTE replace(definition,needle,replacement);
 definition=pg_get_functiondef('trading.check_gas_owned_quantity_correction()'::regprocedure);
 needle='  AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_position_members member_link WHERE';replacement='  AND NOT trading.gas_quantity_bundle_physical_position(h.catalog_id,h.publication_order,h.operation_id,financial.position_id,financial.revision)
  AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_position_members member_link WHERE';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_bundle_upgrade';END IF;
 EXECUTE replace(definition,needle,replacement);
 definition=pg_get_functiondef('trading.check_gas_owned_quantity_correction()'::regprocedure);
 needle='  AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_consumption_members member_link WHERE';replacement='  AND NOT trading.gas_quantity_bundle_physical_consumption(h.catalog_id,h.publication_order,h.operation_id,financial.consumption_id,financial.revision)
  AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_quantity_consumption_members member_link WHERE';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_quantity_bundle_upgrade';END IF;
 EXECUTE replace(definition,needle,replacement);
 SELECT prosrc INTO STRICT body FROM pg_proc WHERE oid='trading.gas_quantity_batch_reconciles(uuid,integer,uuid,timestamptz,jsonb)'::regprocedure;
 body='SELECT ('||substr(body,strpos(body,'SELECT ')+7)||') OR trading.gas_quantity_bundle_ordinary_reconciles($1,$2,$3,$4,$5)';
 EXECUTE 'CREATE OR REPLACE FUNCTION trading.gas_quantity_batch_reconciles(catalog uuid,publication integer,context uuid,as_of timestamptz,conflict jsonb) RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS '||quote_literal(body);
 EXECUTE 'CREATE FUNCTION trading.reconcile_gas_owned_quantities_in_bundle(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,policy uuid,policy_revision integer,receipt_only boolean,shared_publication integer) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS '||quote_literal('
DECLARE admission jsonb;actor uuid;canonical jsonb;prior trading.gas_owned_commands;item jsonb;state jsonb;position_states jsonb=''[]'';consumption_states jsonb=''[]'';
 position_results jsonb=''[]'';consumption_results jsonb=''[]'';all_ack boolean=true;acknowledged integer;publication integer;
 position_head trading.gas_owned_position_quantities;consumption_head trading.gas_owned_consumption_quantities;outcome jsonb;BEGIN
 admission=security.gas_owned_admission(catalog,''owned_quantity_reconciliation'',body,false);
 IF operation IS NULL OR operation=''00000000-0000-0000-0000-000000000000''::uuid OR account IS NULL OR context IS NULL OR policy IS NULL
  OR policy_revision IS NULL OR policy_revision<1 OR as_of IS NULL OR NOT isfinite(as_of) OR receipt_only IS NULL THEN RAISE EXCEPTION ''gas_quantity_input'';END IF;
 body=trading.gas_owned_quantity_fact(body);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid() AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(hashtextextended(''gas_owned:''||catalog::text||'':''||operation::text,0));
 PERFORM trading.require_gas_owned_quantity_references(catalog,account,body);
 IF NOT EXISTS(SELECT 1 FROM lifecycle.action_policies p WHERE(p.catalog_id,p.id,p.revision,p.action)=(catalog,policy,policy_revision,''gas.owned.reconcile_quantity''))
 THEN RAISE EXCEPTION ''gas_owned_policy'';END IF;
 canonical=jsonb_build_object(''action'',''owned_quantity_reconciliation'',''accountId'',account,''body'',body,''asOf'',as_of,''contextId'',context,''policyId'',policy,''policyRevision'',policy_revision,''automatic'',false);
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION ''gas_owned_conflict'';END IF;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>''until'')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object(''outcome'',prior.outcome);END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR item IN SELECT value FROM jsonb_array_elements(body->''positions'') LOOP
  SELECT * INTO STRICT position_head FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,(item->>''positionId'')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_owned_position_quantity_snapshot(catalog,item);all_ack=all_ack AND acknowledged IS NOT NULL;
  position_states=position_states||jsonb_build_array(jsonb_build_object(''selection'',item,''currentRevision'',position_head.revision,''acknowledgedRevision'',acknowledged));
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(body->''consumptions'') LOOP
  SELECT * INTO STRICT consumption_head FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>''consumptionId'')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_owned_consumption_quantity_snapshot(catalog,item);all_ack=all_ack AND acknowledged IS NOT NULL;
  consumption_states=consumption_states||jsonb_build_array(jsonb_build_object(''selection'',item,''currentRevision'',consumption_head.revision,''acknowledgedRevision'',acknowledged));
 END LOOP;
 IF NOT all_ack AND receipt_only THEN
  PERFORM security.require_gas_owned_commit(catalog,(admission->>''until'')::timestamptz,false,false);RETURN NULL;
 END IF;
 IF NOT all_ack THEN
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(position_states||consumption_states)s WHERE s->>''acknowledgedRevision'' IS NOT NULL AND s->>''acknowledgedRevision''<>s->>''currentRevision'')
  THEN RAISE EXCEPTION ''gas_quantity_batch_history'';END IF;
 IF shared_publication IS NULL THEN
  PERFORM trading.require_gas_owned_quantity_amendment(catalog,account,body,context,as_of,policy,policy_revision);
 END IF;
  IF shared_publication IS NULL THEN
  SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  ELSE
   IF NOT EXISTS(SELECT 1 FROM trading.gas_quantity_bundles b JOIN trading.publications p
    ON(p.catalog_id,p.publication_order,p.publication_xid)=(b.catalog_id,b.publication_order,pg_current_xact_id())
    WHERE(b.catalog_id,b.ordinary_operation_id,b.account_id,b.source_context_id,b.as_of,b.actor_id,b.publication_order,b.ordinary_changed)=
     ($1,$2,$3,$6,$5,actor,shared_publication,true)) THEN RAISE EXCEPTION ''gas_quantity_bundle_sealed'';END IF;
   publication=shared_publication;
  END IF;
  outcome=trading.apply_gas_owned_quantity_amendments(catalog,operation,account,body,context,as_of,actor,publication);
 END IF;
 IF all_ack THEN
 FOR state IN SELECT value FROM jsonb_array_elements(position_states) LOOP
  item=state->''selection'';
  SELECT * INTO STRICT position_head FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,(item->>''positionId'')::uuid) ORDER BY q.revision DESC LIMIT 1;
  position_results=position_results||jsonb_build_array(jsonb_build_object(''positionId'',position_head.position_id,
   ''acknowledgedRevision'',(state->>''acknowledgedRevision'')::integer,''currentRevision'',position_head.revision,
   ''currentQuantity'',trim_scale(position_head.quantity)::text,''evidenceId'',position_head.evidence_id,''changed'',false));
 END LOOP;
 FOR state IN SELECT value FROM jsonb_array_elements(consumption_states) LOOP
  item=state->''selection'';
  SELECT * INTO STRICT consumption_head FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>''consumptionId'')::uuid) ORDER BY q.revision DESC LIMIT 1;
  consumption_results=consumption_results||jsonb_build_array(jsonb_build_object(''consumptionId'',consumption_head.consumption_id,
   ''acknowledgedRevision'',(state->>''acknowledgedRevision'')::integer,''currentRevision'',consumption_head.revision,
   ''currentQuantity'',trim_scale(consumption_head.quantity)::text,''lotId'',consumption_head.lot_id,''evidenceId'',consumption_head.evidence_id,''reservationId'',consumption_head.reservation_id,
   ''changed'',false));
 END LOOP;
 outcome=jsonb_build_object(''operationId'',operation,''accountId'',account,''action'',''owned_quantity_reconciliation'',''changed'',false,''semanticAcknowledgement'',true,
  ''publicationOrder'',NULL,''positions'',position_results,''consumptions'',consumption_results,''sourceAllocations'',NULL);
 END IF;
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,(admission->>''until'')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>''until'')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object(''outcome'',outcome);END IF;RETURN outcome;
END');
 EXECUTE 'CREATE OR REPLACE FUNCTION security.reconcile_gas_owned_quantities(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,policy uuid,policy_revision integer,receipt_only boolean) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS '||quote_literal('
BEGIN
 RETURN trading.reconcile_gas_owned_quantities_in_bundle(catalog,operation,account,body,as_of,context,policy,policy_revision,receipt_only,NULL);
END');
 EXECUTE 'CREATE FUNCTION trading.reconcile_gas_futures_batch_in_bundle(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,policy uuid,policy_revision integer,receipt_only boolean,continuation jsonb,shared_publication integer) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS '||quote_literal('
DECLARE admission jsonb;actor uuid;canonical jsonb;prior trading.gas_owned_commands;item jsonb;state jsonb;states jsonb=''[]'';results jsonb=''[]'';
 a trading.gas_owned_accounts;link settlement_data.futures_delivery_obligations;profile settlement_data.futures_delivery_profiles;terms trading.gas_futures_delivery_terms;
 head trading.gas_futures_performance_quantities;acknowledged integer;principal integer;publication integer;revision integer;all_ack boolean=true;
 amended uuid[];changed_positions uuid[];held_position uuid;final record;proposal jsonb;facts jsonb;source jsonb;outcome jsonb;BEGIN
 admission=security.gas_owned_admission(catalog,''futures_quantity_reconciliation_batch'',body,false);
 IF operation IS NULL OR operation=''00000000-0000-0000-0000-000000000000''::uuid OR account IS NULL OR context IS NULL
  OR policy IS NULL OR policy_revision IS NULL OR policy_revision<1 OR receipt_only IS NULL OR as_of IS NULL OR NOT isfinite(as_of)
 THEN RAISE EXCEPTION ''gas_quantity_input'';END IF;
 body=trading.gas_futures_quantity_batch_fact(body);IF continuation=''null''::jsonb THEN continuation=NULL;END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(hashtextextended(''gas_owned:''||catalog::text||'':''||operation::text,0));
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
 FOR item IN SELECT value FROM jsonb_array_elements(body->''corrections'') LOOP
  SELECT x.* INTO STRICT link FROM settlement_data.futures_delivery_obligations x JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(x.catalog_id,x.position_id)
   WHERE(x.catalog_id,x.position_id,p.account_id)=(catalog,(item->>''positionId'')::uuid,account);
  SELECT * INTO STRICT profile FROM settlement_data.futures_delivery_profiles p WHERE(p.catalog_id,p.id,p.revision)=(catalog,link.profile_id,link.profile_revision);
  SELECT * INTO STRICT terms FROM trading.gas_futures_delivery_terms t WHERE(t.catalog_id,t.profile_id,t.profile_revision)=(catalog,link.profile_id,link.profile_revision);
  PERFORM settlement_data.require_futures_delivery_reference(profile,terms);
  PERFORM trading.require_gas_quantity_references(catalog,account,item);
 END LOOP;
 canonical=jsonb_build_object(''action'',''futures_quantity_reconciliation_batch'',''accountId'',account,''body'',body,''asOf'',as_of,''contextId'',context,
  ''policyId'',policy,''policyRevision'',policy_revision,''automatic'',false,''continuation'',continuation);
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR(CASE WHEN receipt_only THEN prior.request-''continuation'' IS DISTINCT FROM canonical-''continuation'' ELSE prior.request IS DISTINCT FROM canonical END)
  THEN RAISE EXCEPTION ''gas_owned_conflict'';END IF;
  FOR item IN SELECT value FROM jsonb_array_elements(prior.outcome->''corrections'') LOOP
   PERFORM trading.require_gas_quantity_snapshot_references(catalog,(item->>''performanceId'')::uuid,(item->>''acknowledgedRevision'')::integer);
  END LOOP;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>''until'')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object(''outcome'',prior.outcome,''continuation'',prior.request->''continuation'');END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR item IN SELECT value FROM jsonb_array_elements(body->''corrections'') LOOP
  SELECT q.* INTO STRICT head FROM trading.gas_futures_performance_quantities q JOIN trading.gas_futures_delivery_performances p
   ON(p.catalog_id,p.id)=(q.catalog_id,q.performance_id) WHERE(q.catalog_id,q.performance_id,p.position_id)=(catalog,(item->>''performanceId'')::uuid,(item->>''positionId'')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_futures_quantity_snapshot(catalog,item);all_ack=all_ack AND acknowledged IS NOT NULL;
  states=states||jsonb_build_array(jsonb_build_object(''selection'',item,''currentRevision'',head.revision,''acknowledgedRevision'',acknowledged));
 END LOOP;
 IF NOT all_ack AND receipt_only THEN
  PERFORM security.require_gas_owned_commit(catalog,(admission->>''until'')::timestamptz,false,false);RETURN NULL;
 END IF;
 IF NOT all_ack THEN
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(states)s WHERE s->>''acknowledgedRevision'' IS NOT NULL AND s->>''acknowledgedRevision''<>s->>''currentRevision'')
  THEN RAISE EXCEPTION ''gas_quantity_batch_history'';END IF;
  amended=ARRAY(SELECT(s#>>''{selection,performanceId}'')::uuid FROM jsonb_array_elements(states)s WHERE s->>''acknowledgedRevision'' IS NULL);
  changed_positions=ARRAY(SELECT DISTINCT(s#>>''{selection,positionId}'')::uuid FROM jsonb_array_elements(states)s WHERE s->>''acknowledgedRevision'' IS NULL);
  PERFORM trading.gas_owned_projection_keys(continuation,ARRAY[''implementation'',''positions''],ARRAY[]::text[]);
  IF continuation->>''implementation'' IS DISTINCT FROM ''gas_futures_quantity_batch_continuation_v1'' OR jsonb_typeof(continuation->''positions'') IS DISTINCT FROM ''array''
   OR cardinality(changed_positions)<>jsonb_array_length(continuation->''positions'')
   OR jsonb_array_length(continuation->''positions'')<>(SELECT count(DISTINCT(value->>''positionId'')::uuid) FROM jsonb_array_elements(continuation->''positions''))
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(continuation->''positions'')p WHERE NOT((p->>''positionId'')::uuid=ANY(changed_positions)))
  THEN RAISE EXCEPTION ''gas_quantity_input'';END IF;
  FOR proposal IN SELECT value FROM jsonb_array_elements(continuation->''positions'') LOOP PERFORM trading.require_futures_delivery_continuation(proposal);END LOOP;
  -- Validate every source before any financial head is advanced. Unchanged
  -- members still carry explicit current versions and are part of the seal.
  FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
   item=state->''selection'';
   IF state->>''acknowledgedRevision'' IS NULL THEN
 IF shared_publication IS NULL THEN
    PERFORM trading.require_gas_futures_quantity_amendment(catalog,account,item,context,as_of,policy,policy_revision,amended);
 END IF;
   ELSE
 IF shared_publication IS NULL THEN
    PERFORM trading.require_gas_futures_delivery_source(catalog,(item->>''positionId'')::uuid,context,as_of);
 END IF;
    SELECT coalesce(max(r.revision),0) INTO principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,(item->>''positionId'')::uuid);
    IF(state->>''currentRevision'')::integer<>(item->>''expectedRevision'')::integer OR principal<>(item->>''expectedPrincipalVersion'')::integer THEN RAISE EXCEPTION ''gas_quantity_revision'';END IF;
   END IF;
  END LOOP;
  IF shared_publication IS NULL THEN
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  ELSE
   IF NOT EXISTS(SELECT 1 FROM trading.gas_quantity_bundles b JOIN trading.publications p
    ON(p.catalog_id,p.publication_order,p.publication_xid)=(b.catalog_id,b.publication_order,pg_current_xact_id())
    WHERE(b.catalog_id,b.physical_operation_id,b.physical_account_id,b.source_context_id,b.as_of,b.actor_id,b.publication_order,b.physical_changed)=
     ($1,$2,$3,$6,$5,actor,shared_publication,true)) THEN RAISE EXCEPTION ''gas_quantity_bundle_sealed'';END IF;
   publication=shared_publication;
  END IF;
  INSERT INTO trading.gas_futures_quantity_batches VALUES(catalog,operation,account,publication,context,as_of,actor,''gas_futures_quantity_batch_v1'');
  FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
   item=state->''selection'';revision=(state->>''currentRevision'')::integer;
   IF state->>''acknowledgedRevision'' IS NULL THEN revision=trading.apply_gas_futures_quantity_amendment(catalog,item,actor,publication);END IF;
   INSERT INTO trading.gas_futures_quantity_batch_members VALUES(catalog,operation,(item->>''performanceId'')::uuid,revision,state->>''acknowledgedRevision'' IS NULL);
  END LOOP;
  FOR held_position IN SELECT unnest(changed_positions) LOOP
   SELECT * INTO STRICT final FROM settlement_data.read_futures_delivery_final(catalog,held_position,context,as_of);
   SELECT value INTO STRICT proposal FROM jsonb_array_elements(continuation->''positions'') WHERE(value->>''positionId'')::uuid=held_position;
   principal=trading.apply_futures_delivery_principal(catalog,held_position,final.determination_id,context,as_of,actor,publication,proposal);
  END LOOP;
  FOR state IN SELECT value FROM jsonb_array_elements(states) WHERE value->>''acknowledgedRevision'' IS NULL LOOP
   item=state->''selection'';revision=(state->>''currentRevision'')::integer+1;
   facts=trading.gas_futures_allocation_facts(catalog,(item->>''performanceId'')::uuid,revision);
   PERFORM trading.require_gas_source_allocation(item->>''sourceAllocation'',facts);
   FOR source IN SELECT value FROM jsonb_array_elements(facts) LOOP
    INSERT INTO trading.gas_futures_quantity_sources VALUES(catalog,(item->>''performanceId'')::uuid,revision,(source->>''accountId'')::uuid,(source->>''sourceEvidenceId'')::uuid,(source->>''oldEvidenceId'')::uuid);
   END LOOP;
  END LOOP;
 END IF;
 FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
  item=state->''selection'';
  SELECT * INTO STRICT head FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(catalog,(item->>''performanceId'')::uuid) ORDER BY q.revision DESC LIMIT 1;
  SELECT coalesce(max(r.revision),0) INTO principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,(item->>''positionId'')::uuid);
  acknowledged=CASE WHEN all_ack THEN(state->>''acknowledgedRevision'')::integer ELSE head.revision END;
  PERFORM trading.require_gas_quantity_snapshot_references(catalog,(item->>''performanceId'')::uuid,acknowledged);
  results=results||jsonb_build_array(jsonb_build_object(''performanceId'',(item->>''performanceId'')::uuid,''positionId'',(item->>''positionId'')::uuid,
   ''acknowledgedRevision'',acknowledged,''currentRevision'',head.revision,''currentPrincipalVersion'',principal,''currentQuantity'',trim_scale(head.quantity)::text,
   ''changed'',NOT all_ack AND state->>''acknowledgedRevision'' IS NULL,''sourceAllocation'',item->>''sourceAllocation'',
   ''sourceAllocations'',CASE WHEN NOT all_ack AND state->>''acknowledgedRevision'' IS NULL THEN trading.gas_futures_allocation_facts(catalog,(item->>''performanceId'')::uuid,head.revision) ELSE NULL END));
 END LOOP;
 outcome=jsonb_build_object(''operationId'',operation,''accountId'',account,''action'',''futures_quantity_reconciliation_batch'',''changed'',NOT all_ack,
  ''semanticAcknowledgement'',all_ack,''publicationOrder'',publication,''corrections'',results);
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,(admission->>''until'')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>''until'')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object(''outcome'',outcome,''continuation'',continuation);END IF;RETURN outcome;
END');
 EXECUTE 'CREATE OR REPLACE FUNCTION security.reconcile_gas_futures_delivery_batch(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,policy uuid,policy_revision integer,receipt_only boolean,continuation jsonb) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS '||quote_literal('
BEGIN
 RETURN trading.reconcile_gas_futures_batch_in_bundle(catalog,operation,account,body,as_of,context,policy,policy_revision,receipt_only,continuation,NULL);
END');
END$upgrade$;

CREATE FUNCTION security.reconcile_gas_quantity_bundle(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,
 ordinary_policy uuid,ordinary_policy_revision integer,physical_policy uuid,physical_policy_revision integer,
 receipt_only boolean,continuation jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;canonical jsonb;prior trading.gas_owned_commands;item jsonb;supply jsonb;
 ordinary_body jsonb;physical_body jsonb;physical_account uuid;accounts uuid[];acknowledged integer;current_revision integer;current_principal integer;
 ordinary_changed boolean=false;physical_changed boolean=false;publication integer;read_publication integer;ordinary_operation uuid;physical_operation uuid;
 ordinary_result jsonb;physical_result jsonb;outcome jsonb;BEGIN
 admission=security.gas_owned_admission(catalog,'quantity_reconciliation_bundle',body,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR account IS NULL OR context IS NULL
  OR ordinary_policy IS NULL OR ordinary_policy_revision IS NULL OR ordinary_policy_revision<1
  OR physical_policy IS NULL OR physical_policy_revision IS NULL OR physical_policy_revision<1
  OR as_of IS NULL OR NOT isfinite(as_of) OR receipt_only IS NULL
  OR octet_length(convert_to(body::text,'UTF8'))>16777216
 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 PERFORM trading.gas_owned_projection_keys(body,ARRAY['ordinary','physical','physicalAccountId'],ARRAY[]::text[]);
 ordinary_body=trading.gas_owned_quantity_fact(body->'ordinary');physical_body=trading.gas_futures_quantity_batch_fact(body->'physical');
 body=jsonb_build_object('ordinary',ordinary_body,'physical',physical_body,'physicalAccountId',body->'physicalAccountId');
 IF continuation='null'::jsonb THEN continuation=NULL;END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 PERFORM trading.require_gas_owned_quantity_references(catalog,account,ordinary_body);
 SELECT array_agg(DISTINCT p.account_id) INTO accounts FROM jsonb_array_elements(physical_body->'corrections') selected
  JOIN settlement_data.futures_delivery_obligations o ON(o.catalog_id,o.position_id)=(catalog,(selected->>'positionId')::uuid)
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(o.catalog_id,o.position_id);
 IF cardinality(accounts) IS DISTINCT FROM 1 OR jsonb_typeof(body->'physicalAccountId') IS DISTINCT FROM 'string'
  OR(body->>'physicalAccountId')::uuid IS DISTINCT FROM accounts[1] THEN RAISE EXCEPTION 'gas_quantity_input';END IF;physical_account=accounts[1];
 FOR item IN SELECT value FROM jsonb_array_elements(physical_body->'corrections') LOOP
  PERFORM trading.require_gas_quantity_references(catalog,physical_account,item);
 END LOOP;
 canonical=jsonb_build_object('action','quantity_reconciliation_bundle','accountId',account,'body',body,'asOf',as_of,'contextId',context,
  'ordinaryPolicyId',ordinary_policy,'ordinaryPolicyRevision',ordinary_policy_revision,
  'physicalPolicyId',physical_policy,'physicalPolicyRevision',physical_policy_revision,'automatic',false,'continuation',continuation);
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR(CASE WHEN receipt_only THEN prior.request-'continuation' IS DISTINCT FROM canonical-'continuation'
   ELSE prior.request IS DISTINCT FROM canonical END) THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  FOR item IN SELECT value FROM jsonb_array_elements(prior.outcome#>'{physical,corrections}') LOOP
   PERFORM trading.require_gas_quantity_snapshot_references(catalog,(item->>'performanceId')::uuid,(item->>'acknowledgedRevision')::integer);
  END LOOP;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object('outcome',prior.outcome,'continuation',prior.request->'continuation');END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT max(p.publication_order) INTO STRICT read_publication FROM trading.publications p WHERE p.catalog_id=catalog;
 -- Classify both products before either is allowed to advance a head.
 FOR item IN SELECT value FROM jsonb_array_elements(ordinary_body->'positions') LOOP
  acknowledged=trading.gas_owned_position_quantity_snapshot(catalog,item);
  SELECT revision INTO STRICT current_revision FROM trading.gas_owned_position_quantities q
   WHERE(q.catalog_id,q.position_id)=(catalog,(item->>'positionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  ordinary_changed=ordinary_changed OR acknowledged IS NULL;
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(ordinary_body->'consumptions') LOOP
  acknowledged=trading.gas_owned_consumption_quantity_snapshot(catalog,item);
  SELECT revision INTO STRICT current_revision FROM trading.gas_owned_consumption_quantities q
   WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  ordinary_changed=ordinary_changed OR acknowledged IS NULL;
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(physical_body->'corrections') LOOP
  acknowledged=trading.gas_futures_quantity_snapshot(catalog,item);
  SELECT q.revision INTO STRICT current_revision FROM trading.gas_futures_performance_quantities q
   JOIN trading.gas_futures_delivery_performances p ON(p.catalog_id,p.id)=(q.catalog_id,q.performance_id)
   WHERE(q.catalog_id,q.performance_id,p.position_id)=(catalog,(item->>'performanceId')::uuid,(item->>'positionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  physical_changed=physical_changed OR acknowledged IS NULL;
 END LOOP;
 IF(ordinary_changed OR physical_changed) AND receipt_only THEN
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);RETURN NULL;
 END IF;
 IF ordinary_changed OR physical_changed THEN
  -- A mixed new effect may acknowledge only the current native snapshot.
  -- Fully historical acknowledgements are handled below without publication.
  FOR item IN SELECT value FROM jsonb_array_elements(ordinary_body->'positions') LOOP
   SELECT revision INTO STRICT current_revision FROM trading.gas_owned_position_quantities q
    WHERE(q.catalog_id,q.position_id)=(catalog,(item->>'positionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
   acknowledged=trading.gas_owned_position_quantity_snapshot(catalog,item);
   IF current_revision<>(item->>'expectedRevision')::integer OR acknowledged IS NOT NULL AND acknowledged<>current_revision
   THEN RAISE EXCEPTION 'gas_quantity_batch_history';END IF;
  END LOOP;
  FOR item IN SELECT value FROM jsonb_array_elements(ordinary_body->'consumptions') LOOP
   SELECT revision INTO STRICT current_revision FROM trading.gas_owned_consumption_quantities q
    WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
   acknowledged=trading.gas_owned_consumption_quantity_snapshot(catalog,item);
   IF current_revision<>(item->>'expectedRevision')::integer OR acknowledged IS NOT NULL AND acknowledged<>current_revision
   THEN RAISE EXCEPTION 'gas_quantity_batch_history';END IF;
  END LOOP;
  FOR item IN SELECT value FROM jsonb_array_elements(physical_body->'corrections') LOOP
   SELECT q.revision INTO STRICT current_revision FROM trading.gas_futures_performance_quantities q
    WHERE(q.catalog_id,q.performance_id)=(catalog,(item->>'performanceId')::uuid) ORDER BY q.revision DESC LIMIT 1;
   acknowledged=trading.gas_futures_quantity_snapshot(catalog,item);
   IF current_revision<>(item->>'expectedRevision')::integer OR acknowledged IS NOT NULL AND acknowledged<>current_revision
   THEN RAISE EXCEPTION 'gas_quantity_batch_history';END IF;
  END LOOP;
  publication=read_publication+1;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 END IF;
 ordinary_operation=gen_random_uuid();physical_operation=gen_random_uuid();
 INSERT INTO trading.gas_quantity_bundles VALUES(catalog,operation,account,physical_account,ordinary_operation,physical_operation,
  publication,read_publication,context,as_of,actor,ordinary_changed,physical_changed,'ordinary_futures_quantity_bundle_v1');
 FOR item IN SELECT value FROM jsonb_array_elements(ordinary_body->'positions') LOOP
  INSERT INTO trading.gas_quantity_bundle_titles VALUES(catalog,operation,(item->>'positionId')::uuid,(item->>'expectedRevision')::integer,
   (item->>'evidenceId')::uuid,trading.gas_owned_position_quantity_snapshot(catalog,item) IS NULL);
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(ordinary_body->'consumptions') LOOP
  INSERT INTO trading.gas_quantity_bundle_uses VALUES(catalog,operation,(item->>'consumptionId')::uuid,(item->>'expectedRevision')::integer,
   (item->>'evidenceId')::uuid,trading.gas_owned_consumption_quantity_snapshot(catalog,item) IS NULL);
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(physical_body->'corrections') LOOP
  INSERT INTO trading.gas_quantity_bundle_performances VALUES(catalog,operation,(item->>'performanceId')::uuid,(item->>'expectedRevision')::integer,
   (item->>'titleEvidenceId')::uuid,trading.gas_futures_quantity_snapshot(catalog,item) IS NULL);
  FOR supply IN SELECT value FROM jsonb_array_elements(item->'supply') WHERE value->>'consumptionId' IS NOT NULL LOOP
   INSERT INTO trading.gas_quantity_bundle_supplies VALUES(catalog,operation,(item->>'performanceId')::uuid,(supply->>'consumptionId')::uuid,
    (supply->>'expectedRevision')::integer,(supply->>'evidenceId')::uuid);
  END LOOP;
 END LOOP;
 -- Both preflights see the complete named source transition before mutation.
 IF ordinary_changed THEN PERFORM trading.require_gas_owned_quantity_amendment(catalog,account,ordinary_body,context,as_of,ordinary_policy,ordinary_policy_revision);END IF;
 IF ordinary_changed OR physical_changed THEN
  FOR item IN SELECT value FROM jsonb_array_elements(physical_body->'corrections') LOOP
   IF trading.gas_futures_quantity_snapshot(catalog,item) IS NULL THEN
    PERFORM trading.require_gas_futures_quantity_amendment(catalog,physical_account,item,context,as_of,physical_policy,physical_policy_revision,
     ARRAY(SELECT m.performance_id FROM trading.gas_quantity_bundle_performances m WHERE(m.catalog_id,m.operation_id,m.changed)=(catalog,operation,true)));
   ELSE
    PERFORM trading.require_gas_futures_delivery_source(catalog,(item->>'positionId')::uuid,context,as_of);
    SELECT coalesce(max(r.revision),0) INTO current_principal FROM trading.gas_futures_delivery_principal_runs r
     WHERE(r.catalog_id,r.position_id)=(catalog,(item->>'positionId')::uuid);
    IF current_principal<>(item->>'expectedPrincipalVersion')::integer THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
   END IF;
  END LOOP;
 END IF;
 ordinary_result=trading.reconcile_gas_owned_quantities_in_bundle(catalog,ordinary_operation,account,ordinary_body,as_of,context,
  ordinary_policy,ordinary_policy_revision,false,CASE WHEN ordinary_changed THEN publication ELSE NULL END);
 physical_result=trading.reconcile_gas_futures_batch_in_bundle(catalog,physical_operation,physical_account,physical_body,as_of,context,
  physical_policy,physical_policy_revision,false,continuation,CASE WHEN physical_changed THEN publication ELSE NULL END);
 outcome=jsonb_build_object('operationId',operation,'accountId',account,'action','quantity_reconciliation_bundle',
  'changed',ordinary_changed OR physical_changed,'semanticAcknowledgement',NOT(ordinary_changed OR physical_changed),
  'publicationOrder',publication,'ordinary',ordinary_result,'physical',physical_result);
 IF octet_length(convert_to(outcome::text,'UTF8'))>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,(admission->>'until')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object('outcome',outcome,'continuation',continuation);END IF;RETURN outcome;
END$$;

DO $privileges$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.guard_gas_quantity_bundle()'::regprocedure,'trading.gas_quantity_bundle_allows(uuid,uuid,uuid,timestamptz,jsonb,jsonb,boolean)'::regprocedure,'trading.gas_quantity_bundle_physical_position(uuid,integer,uuid,uuid,integer)'::regprocedure,'trading.gas_quantity_bundle_physical_consumption(uuid,integer,uuid,uuid,integer)'::regprocedure,'trading.gas_quantity_bundle_ordinary_reconciles(uuid,integer,uuid,timestamptz,jsonb)'::regprocedure,'trading.check_gas_quantity_bundle()'::regprocedure,'trading.reconcile_gas_owned_quantities_in_bundle(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,integer)'::regprocedure,'trading.reconcile_gas_futures_batch_in_bundle(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb,integer)'::regprocedure,'security.reconcile_gas_quantity_bundle(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,uuid,integer,boolean,jsonb)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$privileges$;
GRANT EXECUTE ON FUNCTION security.reconcile_gas_quantity_bundle(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,uuid,integer,boolean,jsonb) TO openetrm_runtime;
COMMIT;
