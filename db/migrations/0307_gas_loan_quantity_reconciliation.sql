-- A title-transfer loan keeps its stock, return and cash counterparts separate.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
INSERT INTO lifecycle.action_kinds(code,automatic,configuration_permission,execution_permission)
 VALUES('gas.loan.reconcile_quantity',false,'ConfigureGasOwnedHoldings','RecordGasOwnedHoldings');
ALTER TABLE trading.gas_owned_loans ADD CONSTRAINT gas_loan_exact_counterparts
 UNIQUE(catalog_id,id,account_id,received_lot_id,return_position_id);

CREATE TABLE trading.gas_owned_loan_quantities(
 catalog_id uuid NOT NULL,loan_id uuid NOT NULL,account_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_revision integer,
 quantity ref_data.exact_number NOT NULL CHECK(quantity>=0),title_evidence_id uuid NOT NULL,agreement_evidence_id uuid NOT NULL,
 received_lot_id uuid NOT NULL,stock_revision integer NOT NULL,return_position_id uuid NOT NULL,return_revision integer NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,loan_id,revision),UNIQUE(catalog_id,loan_id,publication_order),
 FOREIGN KEY(catalog_id,loan_id) REFERENCES trading.gas_owned_loans(catalog_id,id),
 FOREIGN KEY(catalog_id,loan_id,account_id,received_lot_id,return_position_id)
  REFERENCES trading.gas_owned_loans(catalog_id,id,account_id,received_lot_id,return_position_id),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,account_id,title_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,account_id,agreement_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,loan_id,previous_revision) REFERENCES trading.gas_owned_loan_quantities(catalog_id,loan_id,revision),
 FOREIGN KEY(catalog_id,received_lot_id,stock_revision) REFERENCES trading.gas_owned_position_quantities(catalog_id,position_id,revision),
 FOREIGN KEY(catalog_id,return_position_id,return_revision) REFERENCES trading.gas_owned_position_quantities(catalog_id,position_id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(revision=1 AND previous_revision IS NULL OR revision>1 AND previous_revision IS NOT NULL AND previous_revision=revision-1)
);
-- The immutable origin is revision one, including for loans accepted after
-- this migration. Later terms must pin both exact native position revisions.
INSERT INTO trading.gas_owned_loan_quantities
 SELECT l.catalog_id,l.id,l.account_id,1,NULL,l.quantity,s.evidence_id,r.evidence_id,l.received_lot_id,1,l.return_position_id,1,s.actor_id,s.publication_order
 FROM trading.gas_owned_loans l JOIN trading.gas_owned_positions s ON(s.catalog_id,s.id)=(l.catalog_id,l.received_lot_id)
 JOIN trading.gas_owned_positions r ON(r.catalog_id,r.id)=(l.catalog_id,l.return_position_id);
ALTER TABLE trading.gas_owned_loan_quantities OWNER TO openetrm_security_owner;
ALTER TABLE trading.gas_owned_loan_quantities ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_owned_loan_quantities FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON trading.gas_owned_loan_quantities TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.gas_owned_loan_quantities FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER native_publication BEFORE INSERT ON trading.gas_owned_loan_quantities FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication();

CREATE FUNCTION trading.seed_gas_loan_quantity() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE stock trading.gas_owned_positions;owed trading.gas_owned_positions;BEGIN
 SELECT * INTO STRICT stock FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.received_lot_id);
 SELECT * INTO STRICT owed FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.return_position_id);
 INSERT INTO trading.gas_owned_loan_quantities VALUES(NEW.catalog_id,NEW.id,NEW.account_id,1,NULL,NEW.quantity,
  stock.evidence_id,owed.evidence_id,NEW.received_lot_id,1,NEW.return_position_id,1,stock.actor_id,stock.publication_order);RETURN NEW;
END$$;
CREATE TRIGGER initial_quantity AFTER INSERT ON trading.gas_owned_loans FOR EACH ROW EXECUTE FUNCTION trading.seed_gas_loan_quantity();

CREATE FUNCTION trading.gas_owned_loans_at(catalog uuid,context uuid,as_of timestamptz) RETURNS SETOF trading.gas_owned_loans
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT (jsonb_populate_record(l,jsonb_build_object('quantity',q.quantity))).*
 FROM trading.gas_owned_loans l CROSS JOIN LATERAL(SELECT v.* FROM trading.gas_owned_loan_quantities v
  WHERE(v.catalog_id,v.loan_id)=($1,l.id) AND trading.gas_owned_visible($1,v.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$2,$3)
  ORDER BY v.revision DESC LIMIT 1)q WHERE l.catalog_id=$1
$$;

-- One fixed projection supplies preflight statistics and the result. It keeps
-- all source-known resource events, including accepted future-effective uses.
CREATE FUNCTION trading.gas_loan_reconciliation_rows(catalog uuid,loan uuid,context uuid,as_of timestamptz,statistics boolean) RETURNS jsonb
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE rows_sql constant text=$rows$
 WITH chosen AS MATERIALIZED(
  SELECT l.*,q.revision financial_revision,q.title_evidence_id,q.agreement_evidence_id,q.stock_revision,q.return_revision,q.publication_order,k.known_from
  FROM trading.gas_owned_loans_at($1,$3,$4) l JOIN LATERAL(SELECT v.* FROM trading.gas_owned_loan_quantities v
   WHERE(v.catalog_id,v.loan_id)=($1,l.id) AND trading.gas_owned_visible($1,v.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)
   ORDER BY v.revision DESC LIMIT 1)q ON true JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=($1,q.publication_order)
  WHERE(l.catalog_id,l.id)=($1,$2)),
 repayments AS MATERIALIZED(SELECT r.* FROM trading.gas_owned_repayments r JOIN chosen l ON(l.catalog_id,l.id)=(r.catalog_id,r.loan_id)
  JOIN trading.gas_owned_consumptions_at($1,$3,$4)c ON(c.catalog_id,c.id)=(r.catalog_id,r.consumption_id)),
 uses AS MATERIALIZED(SELECT c.* FROM trading.gas_owned_consumptions_at($1,$3,$4)c WHERE c.lot_id IN(SELECT received_lot_id FROM chosen)
  OR c.id IN(SELECT original.id FROM trading.gas_owned_consumptions original WHERE original.catalog_id=$1 AND original.lot_id IN(SELECT received_lot_id FROM chosen))
  OR c.id IN(SELECT consumption_id FROM repayments)),
 lot_ids AS(SELECT received_lot_id id FROM chosen UNION SELECT lot_id FROM uses),
 positions AS MATERIALIZED(SELECT p.*,q.revision financial_revision,q.retracted,k.known_from,origin.publication_order original_publication_order
  FROM trading.gas_owned_positions_at($1,$3,$4)p JOIN trading.gas_owned_positions origin ON(origin.catalog_id,origin.id)=(p.catalog_id,p.id)
  JOIN trading.gas_owned_position_quantities q ON(q.catalog_id,q.position_id,q.publication_order)=(p.catalog_id,p.id,p.publication_order)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE p.id IN(SELECT id FROM lot_ids UNION SELECT return_position_id FROM chosen)),
 reservations AS MATERIALIZED(SELECT r.* FROM trading.gas_owned_reservations r WHERE r.catalog_id=$1 AND r.lot_id IN(SELECT id FROM lot_ids)
  AND trading.gas_owned_visible($1,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)),
 fees AS MATERIALIZED(SELECT f.* FROM trading.gas_owned_fees f JOIN chosen l ON(l.catalog_id,l.id)=(f.catalog_id,f.loan_id)
  WHERE trading.gas_owned_visible($1,f.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)),
 required_evidence AS(SELECT title_evidence_id id FROM chosen UNION SELECT agreement_evidence_id FROM chosen
  UNION SELECT evidence_id FROM positions UNION SELECT evidence_id FROM uses),
 keys AS(SELECT DISTINCT e.account_id,e.source_key FROM trading.gas_owned_evidence e WHERE e.catalog_id=$1 AND e.id IN(SELECT id FROM required_evidence)),
 heads AS(SELECT h.id FROM keys k CROSS JOIN LATERAL(SELECT e.id FROM trading.gas_owned_evidence e
  WHERE(e.catalog_id,e.account_id,e.source_key)=($1,k.account_id,k.source_key)
   AND trading.gas_owned_visible($1,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4) ORDER BY e.revision DESC LIMIT 1)h)
 SELECT 'loan' kind,to_jsonb(l)||jsonb_build_object('quantity',trim_scale(l.quantity)::text,'expectedRevision',l.financial_revision) value FROM chosen l
 UNION ALL SELECT CASE WHEN p.id=l.received_lot_id THEN 'stock' WHEN p.id=l.return_position_id THEN 'return' ELSE 'funding' END,
  to_jsonb(p)||jsonb_build_object('positionId',p.id,'quantity',trim_scale(p.quantity)::text,'financialRevision',p.financial_revision)
  FROM positions p CROSS JOIN chosen l
 UNION ALL SELECT 'consumptions',to_jsonb(c)||jsonb_build_object('quantity',trim_scale(c.quantity)::text,'financialRevision',q.revision,
  'originalPublicationOrder',o.publication_order,'knownAt',k.known_from) FROM uses c
  JOIN trading.gas_owned_consumptions o ON(o.catalog_id,o.id)=(c.catalog_id,c.id)
  JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.publication_order)=(c.catalog_id,c.id,c.publication_order)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(c.catalog_id,c.publication_order)
 UNION ALL SELECT 'repayments',to_jsonb(r) FROM repayments r
 UNION ALL SELECT 'reservations',to_jsonb(r)||jsonb_build_object('quantity',trim_scale(r.quantity)::text) FROM reservations r
 UNION ALL SELECT 'releases',to_jsonb(r) FROM trading.gas_owned_releases r WHERE r.catalog_id=$1 AND r.reservation_id IN(SELECT id FROM reservations)
  AND trading.gas_owned_visible($1,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)
 UNION ALL SELECT 'fees',to_jsonb(f)||jsonb_build_object('amount',trim_scale(f.amount)::text) FROM fees f
 UNION ALL SELECT 'payments',to_jsonb(p)||jsonb_build_object('amount',trim_scale(p.amount)::text) FROM trading.gas_owned_fee_payments p
  WHERE p.catalog_id=$1 AND p.fee_id IN(SELECT id FROM fees)
   AND trading.gas_owned_visible($1,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)
 UNION ALL SELECT 'evidence',to_jsonb(e)||jsonb_build_object('quantity',trim_scale(e.quantity)::text,'knownAt',k.known_from)
  FROM trading.gas_owned_evidence e JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
  WHERE e.catalog_id=$1 AND(e.id IN(SELECT id FROM required_evidence) OR e.id IN(SELECT id FROM heads))
   AND trading.gas_owned_visible($1,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$3,$4)
$rows$;result jsonb;BEGIN
 IF statistics IS NULL THEN RAISE EXCEPTION 'gas_loan_quantity_input';END IF;
 IF statistics THEN
  EXECUTE 'WITH rows AS('||rows_sql||'),sizes AS(SELECT kind,count(*) n,sum(octet_length(convert_to(value::text,''UTF8''))) bytes FROM rows GROUP BY kind)
   SELECT jsonb_build_object(''counts'',coalesce(jsonb_object_agg(kind,n),''{}''),''bytes'',coalesce(sum(bytes),0),''count'',coalesce(sum(n),0)) FROM sizes'
   INTO result USING catalog,loan,context,as_of;
 ELSE
  EXECUTE 'SELECT jsonb_build_object(
   ''loan'',(jsonb_agg(value) FILTER(WHERE kind=''loan''))->0,''stock'',(jsonb_agg(value) FILTER(WHERE kind=''stock''))->0,
   ''return'',(jsonb_agg(value) FILTER(WHERE kind=''return''))->0,
   ''funding'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''funding''),''[]''),
   ''consumptions'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''consumptions''),''[]''),
   ''repayments'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''repayments''),''[]''),
   ''reservations'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''reservations''),''[]''),
   ''releases'',coalesce(jsonb_agg(value ORDER BY value->>''reservation_id'') FILTER(WHERE kind=''releases''),''[]''),
   ''fees'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''fees''),''[]''),
   ''payments'',coalesce(jsonb_agg(value ORDER BY value->>''id'') FILTER(WHERE kind=''payments''),''[]''),
   ''evidence'',coalesce(jsonb_agg(value ORDER BY value->>''source_key'',(value->>''revision'')::integer) FILTER(WHERE kind=''evidence''),''[]''))
   FROM('||rows_sql||')rows' INTO result USING catalog,loan,context,as_of;
 END IF;RETURN result;
END$$;

CREATE FUNCTION security.read_gas_loan_reconciliation(catalog uuid,loan uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;owner trading.gas_owned_accounts;selected trading.gas_owned_loans;stats jsonb;entry record;result jsonb;BEGIN
 until_at=security.gas_futures_delivery_discovery_authority(catalog);
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 SELECT * INTO STRICT selected FROM trading.gas_owned_loans_at(catalog,context,as_of) l WHERE(l.catalog_id,l.id)=(catalog,loan);
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,selected.account_id)
  AND trading.gas_owned_visible(catalog,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of);
 IF security.reference_version_visible(owner.reference_catalog_id,owner.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,owner.definition_revision_id,NULL) IS NOT TRUE OR NOT EXISTS(
   SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,selected.lender_id,selected.lender_revision)
    AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of))
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,owner.definition_revision_id);
 PERFORM trading.require_gas_owned_quantity_head(catalog,owner.id,context,as_of);
 stats=trading.gas_loan_reconciliation_rows(catalog,loan,context,as_of,true);
 FOR entry IN SELECT key,value FROM jsonb_each(stats->'counts') LOOP
  IF entry.key NOT IN('loan','stock','return','funding','consumptions','repayments','reservations','releases','fees','payments','evidence')
   OR(entry.value#>>'{}')::bigint NOT BETWEEN 0 AND 16384 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 END LOOP;
 IF coalesce((stats#>>'{counts,loan}')::integer,0)<>1 OR coalesce((stats#>>'{counts,stock}')::integer,0)<>1
  OR coalesce((stats#>>'{counts,return}')::integer,0)<>1 OR(stats->>'bytes')::bigint+4*(stats->>'count')::bigint+2048>16777216
 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 result=jsonb_build_object('implementation','gas_loan_quantity_reconciliation_source_v1','scope','gas_loan_reconciliation_dependency_facts_v1',
  'loanId',loan,'accountId',owner.id,'contextId',context,'asOf',as_of,'authorityUntil',until_at)
  ||trading.gas_loan_reconciliation_rows(catalog,loan,context,as_of,false);
 IF octet_length(convert_to(result::text,'UTF8'))>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);RETURN result;
EXCEPTION WHEN NO_DATA_FOUND THEN
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);RAISE EXCEPTION 'gas_loan_quantity_source';
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.seed_gas_loan_quantity()'::regprocedure,'trading.gas_owned_loans_at(uuid,uuid,timestamptz)'::regprocedure,
  'trading.gas_loan_reconciliation_rows(uuid,uuid,uuid,timestamptz,boolean)'::regprocedure,
  'security.read_gas_loan_reconciliation(uuid,uuid,uuid,timestamptz)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.read_gas_loan_reconciliation(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
CREATE TABLE trading.gas_loan_quantity_corrections(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,account_id uuid NOT NULL,loan_id uuid NOT NULL,loan_revision integer NOT NULL,
 loan_changed boolean NOT NULL,source_allocation text NOT NULL CHECK(source_allocation IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')),
 publication_order integer,read_publication_order integer NOT NULL,source_context_id uuid NOT NULL,as_of ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),implementation text NOT NULL CHECK(implementation='gas_loan_quantity_reconciliation_v1'),
 PRIMARY KEY(catalog_id,operation_id),UNIQUE(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,loan_id,loan_revision) REFERENCES trading.gas_owned_loan_quantities(catalog_id,loan_id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,read_publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_owned_commands(catalog_id,request_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE trading.gas_loan_quantity_consumption_members(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,consumption_id uuid NOT NULL,revision integer NOT NULL,changed boolean NOT NULL,
 source_allocation text NOT NULL CHECK(source_allocation IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')),
 PRIMARY KEY(catalog_id,operation_id,consumption_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_loan_quantity_corrections(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,consumption_id,revision) REFERENCES trading.gas_owned_consumption_quantities(catalog_id,consumption_id,revision)
);
CREATE TABLE trading.gas_loan_quantity_sources(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,account_id uuid NOT NULL,source_evidence_id uuid NOT NULL,previous_evidence_id uuid NOT NULL,
 source_allocation text NOT NULL CHECK(source_allocation IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')),
 PRIMARY KEY(catalog_id,operation_id,source_evidence_id),
 FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_loan_quantity_corrections(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,account_id,source_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,account_id,previous_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id)
);

CREATE FUNCTION trading.gas_loan_quantity_terms(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE uses jsonb;BEGIN
 PERFORM trading.gas_owned_projection_keys(body,ARRAY['loanId','expectedRevision','stockExpectedRevision','returnExpectedRevision','quantity',
  'titleEvidenceId','agreementEvidenceId','sourceAllocation','feeDisposition','consumptions','evidence'],ARRAY[]::text[]);
 IF jsonb_typeof(body->'loanId') IS DISTINCT FROM 'string' OR(body->>'loanId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  OR jsonb_typeof(body->'titleEvidenceId') IS DISTINCT FROM 'string' OR(body->>'titleEvidenceId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  OR jsonb_typeof(body->'agreementEvidenceId') IS DISTINCT FROM 'string' OR(body->>'agreementEvidenceId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[1-9][0-9]{0,8}$'
  OR jsonb_typeof(body->'stockExpectedRevision') IS DISTINCT FROM 'number' OR body->>'stockExpectedRevision'!~'^[1-9][0-9]{0,8}$'
  OR jsonb_typeof(body->'returnExpectedRevision') IS DISTINCT FROM 'number' OR body->>'returnExpectedRevision'!~'^[1-9][0-9]{0,8}$'
  OR jsonb_typeof(body->'quantity') IS DISTINCT FROM 'string' OR trading.gas_owned_quantity(body->>'quantity')<0
  OR body->>'sourceAllocation' IS NULL OR body->>'sourceAllocation' NOT IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')
  OR body->>'feeDisposition' IS DISTINCT FROM 'retain_fixed_fee_memberships_v1'
  OR jsonb_typeof(body->'consumptions') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'consumptions')>16384
  OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 2000
  OR octet_length(convert_to(body::text,'UTF8'))>8388608 THEN RAISE EXCEPTION 'gas_loan_quantity_input';END IF;
 -- The proven ordinary validator normalizes identical nonempty stock-use tuples.
 IF jsonb_array_length(body->'consumptions')=0 THEN uses='[]';
 ELSE
  uses=trading.gas_owned_quantity_terms(jsonb_build_object('positions','[]'::jsonb,'consumptions',body->'consumptions','evidence',body->>'evidence'))->'consumptions';
 END IF;
 RETURN body||jsonb_build_object('quantity',trim_scale(trading.gas_owned_quantity(body->>'quantity'))::text,'consumptions',uses);
END$$;

CREATE FUNCTION trading.gas_loan_quantity_position_proof(catalog uuid,position_id uuid,version integer) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_loan_quantity_corrections h JOIN trading.gas_owned_loans l ON(l.catalog_id,l.id)=(h.catalog_id,h.loan_id)
  JOIN trading.gas_owned_loan_quantities q ON(q.catalog_id,q.loan_id,q.revision)=(h.catalog_id,h.loan_id,h.loan_revision)
  JOIN trading.gas_owned_position_quantities p ON(p.catalog_id,p.position_id,p.revision)=($1,$2,$3)
  JOIN trading.publications pub ON(pub.catalog_id,pub.publication_order,pub.publication_xid)=(h.catalog_id,h.publication_order,pg_current_xact_id_if_assigned())
  WHERE h.catalog_id=$1 AND h.loan_changed AND(p.publication_order,p.actor_id,p.account_id,p.retracted)=
   (h.publication_order,h.actor_id,h.account_id,q.quantity=0) AND(
    (p.position_id,p.revision,p.quantity,p.evidence_id)=(l.received_lot_id,q.stock_revision,q.quantity,q.title_evidence_id)
    OR(p.position_id,p.revision,p.quantity,p.evidence_id)=(l.return_position_id,q.return_revision,-q.quantity,q.agreement_evidence_id)))
$$;
CREATE FUNCTION trading.gas_loan_quantity_consumption_proof(catalog uuid,consumption uuid,version integer) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_loan_quantity_consumption_members m JOIN trading.gas_loan_quantity_corrections h
  ON(h.catalog_id,h.operation_id)=(m.catalog_id,m.operation_id)
  JOIN trading.gas_owned_loans l ON(l.catalog_id,l.id)=(h.catalog_id,h.loan_id)
  JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.revision)=(m.catalog_id,m.consumption_id,m.revision)
  JOIN trading.gas_owned_consumptions c ON(c.catalog_id,c.id)=(q.catalog_id,q.consumption_id)
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id,p.account_id)=(q.catalog_id,q.lot_id,h.account_id)
  JOIN trading.publications pub ON(pub.catalog_id,pub.publication_order,pub.publication_xid)=(h.catalog_id,h.publication_order,pg_current_xact_id_if_assigned())
  WHERE(m.catalog_id,m.consumption_id,m.revision,m.changed)=($1,$2,$3,true)
   AND(q.publication_order,q.actor_id)=(h.publication_order,h.actor_id) AND(
    c.kind IN('delivery','loss') AND c.lot_id=l.received_lot_id OR c.kind='return' AND EXISTS(
     SELECT 1 FROM trading.gas_owned_repayments r WHERE(r.catalog_id,r.loan_id,r.consumption_id)=(l.catalog_id,l.id,c.id))))
$$;

CREATE FUNCTION trading.gas_loan_correction_outcome(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_loan_quantity_corrections;c trading.gas_owned_commands;loan trading.gas_owned_loans;head trading.gas_owned_loan_quantities;
 selected trading.gas_owned_loan_quantities;body jsonb;item jsonb;uses jsonb='[]';m trading.gas_loan_quantity_consumption_members;
 used trading.gas_owned_consumption_quantities;facts jsonb;through_publication integer;BEGIN
 SELECT * INTO STRICT h FROM trading.gas_loan_quantity_corrections r WHERE(r.catalog_id,r.operation_id)=(catalog,operation);
 SELECT * INTO STRICT c FROM trading.gas_owned_commands r WHERE(r.catalog_id,r.request_id)=(catalog,operation);body=c.request->'body';
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id)=(catalog,h.loan_id);
 through_publication=coalesce(h.publication_order,h.read_publication_order);
 SELECT * INTO STRICT head FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id)=(catalog,h.loan_id)
  AND q.publication_order<=through_publication ORDER BY q.revision DESC LIMIT 1;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  SELECT * INTO STRICT m FROM trading.gas_loan_quantity_consumption_members r WHERE(r.catalog_id,r.operation_id,r.consumption_id)=(catalog,operation,(item->>'consumptionId')::uuid);
  SELECT * INTO STRICT used FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,m.consumption_id)
   AND q.publication_order<=through_publication ORDER BY q.revision DESC LIMIT 1;
  uses=uses||jsonb_build_array(jsonb_build_object('consumptionId',m.consumption_id,'acknowledgedRevision',m.revision,
   'currentRevision',used.revision,'currentQuantity',trim_scale(used.quantity)::text,'changed',m.changed));
 END LOOP;
 IF h.publication_order IS NOT NULL THEN facts=trading.gas_loan_allocation_facts(catalog,operation);END IF;
 RETURN jsonb_build_object('operationId',operation,'accountId',h.account_id,'loanId',loan.id,'action','loan_quantity_reconciliation',
  'changed',h.publication_order IS NOT NULL,'semanticAcknowledgement',h.publication_order IS NULL,'publicationOrder',h.publication_order,
  'acknowledgedRevision',h.loan_revision,'currentRevision',head.revision,'currentQuantity',trim_scale(head.quantity)::text,
  'stockId',loan.received_lot_id,'returnId',loan.return_position_id,'feeDisposition',body->>'feeDisposition','consumptions',uses,'sourceAllocations',facts);
END$$;

CREATE FUNCTION security.reconcile_gas_loan_quantities(catalog uuid,operation uuid,account uuid,body jsonb,as_of timestamptz,context uuid,
 policy uuid,policy_revision integer,receipt_only boolean) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;canonical jsonb;prior trading.gas_owned_commands;loan trading.gas_owned_loans;
 head trading.gas_owned_loan_quantities;stock trading.gas_owned_position_quantities;owed trading.gas_owned_position_quantities;used trading.gas_owned_consumption_quantities;
 item jsonb;state jsonb;states jsonb='[]';uses jsonb='[]';facts jsonb;fact jsonb;outcome jsonb;
 loan_ack integer;acknowledged integer;revision integer;stock_revision integer;return_revision integer;loan_changed boolean;all_ack boolean;publication integer;read_publication integer;BEGIN
 admission=security.gas_owned_admission(catalog,'loan_quantity_reconciliation',body,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR account IS NULL OR context IS NULL OR policy IS NULL
  OR policy_revision IS NULL OR policy_revision<1 OR as_of IS NULL OR NOT isfinite(as_of) OR receipt_only IS NULL THEN RAISE EXCEPTION 'gas_loan_quantity_input';END IF;
 body=trading.gas_loan_quantity_fact(body);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid()
  AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 PERFORM trading.require_gas_loan_quantity_references(catalog,account,body);
 IF NOT EXISTS(SELECT 1 FROM lifecycle.action_policies p WHERE(p.catalog_id,p.id,p.revision,p.action)=(catalog,policy,policy_revision,'gas.loan.reconcile_quantity'))
 THEN RAISE EXCEPTION 'gas_owned_policy';END IF;
 canonical=jsonb_build_object('action','loan_quantity_reconciliation','accountId',account,'body',body,'asOf',as_of,'contextId',context,
  'policyId',policy,'policyRevision',policy_revision,'automatic',false);
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object('outcome',prior.outcome);END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT max(p.publication_order) INTO STRICT read_publication FROM trading.publications p WHERE p.catalog_id=catalog;
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.account_id,l.id)=(catalog,account,(body->>'loanId')::uuid);
 SELECT * INTO STRICT head FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id)=(catalog,loan.id) ORDER BY q.revision DESC LIMIT 1;
 SELECT * INTO STRICT stock FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,loan.received_lot_id) ORDER BY q.revision DESC LIMIT 1;
 SELECT * INTO STRICT owed FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,loan.return_position_id) ORDER BY q.revision DESC LIMIT 1;
 loan_ack=trading.gas_loan_quantity_snapshot(catalog,body);all_ack=loan_ack IS NOT NULL;loan_changed=loan_ack IS NULL;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  SELECT * INTO STRICT used FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  acknowledged=trading.gas_loan_consumption_snapshot(catalog,item);all_ack=all_ack AND acknowledged IS NOT NULL;
  states=states||jsonb_build_array(jsonb_build_object('selection',item,'currentRevision',used.revision,'acknowledgedRevision',acknowledged));
 END LOOP;
 IF NOT all_ack AND receipt_only THEN
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);RETURN NULL;
 END IF;
 IF NOT all_ack THEN
  IF loan_ack IS NOT NULL AND loan_ack<>head.revision OR EXISTS(SELECT 1 FROM jsonb_array_elements(states)s
   WHERE s->>'acknowledgedRevision' IS NOT NULL AND s->>'acknowledgedRevision'<>s->>'currentRevision') THEN RAISE EXCEPTION 'gas_quantity_batch_history';END IF;
  PERFORM trading.require_gas_loan_quantity_amendment(catalog,account,body,context,as_of,policy,policy_revision);
  SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  stock_revision=stock.revision;return_revision=owed.revision;
  IF loan_changed THEN
   IF(stock.quantity,stock.evidence_id) IS DISTINCT FROM(trading.gas_owned_quantity(body->>'quantity'),(body->>'titleEvidenceId')::uuid) THEN
    stock_revision=stock.revision+1;
    INSERT INTO trading.gas_owned_position_quantities VALUES(catalog,loan.received_lot_id,account,stock_revision,stock.revision,
     trading.gas_owned_quantity(body->>'quantity'),(body->>'titleEvidenceId')::uuid,trading.gas_owned_quantity(body->>'quantity')=0,actor,publication);
   END IF;
   IF(owed.quantity,owed.evidence_id) IS DISTINCT FROM(-trading.gas_owned_quantity(body->>'quantity'),(body->>'agreementEvidenceId')::uuid) THEN
    return_revision=owed.revision+1;
    INSERT INTO trading.gas_owned_position_quantities VALUES(catalog,loan.return_position_id,account,return_revision,owed.revision,
     -trading.gas_owned_quantity(body->>'quantity'),(body->>'agreementEvidenceId')::uuid,trading.gas_owned_quantity(body->>'quantity')=0,actor,publication);
   END IF;
   INSERT INTO trading.gas_owned_loan_quantities VALUES(catalog,loan.id,account,head.revision+1,head.revision,trading.gas_owned_quantity(body->>'quantity'),
    (body->>'titleEvidenceId')::uuid,(body->>'agreementEvidenceId')::uuid,loan.received_lot_id,stock_revision,loan.return_position_id,return_revision,actor,publication);
   head.revision=head.revision+1;
  END IF;
  INSERT INTO trading.gas_loan_quantity_corrections VALUES(catalog,operation,account,loan.id,head.revision,loan_changed,body->>'sourceAllocation',
   publication,read_publication,context,as_of,actor,'gas_loan_quantity_reconciliation_v1');
  FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
   item=state->'selection';revision=(state->>'currentRevision')::integer;
   IF state->>'acknowledgedRevision' IS NULL THEN
    revision=revision+1;
    INSERT INTO trading.gas_owned_consumption_quantities VALUES(catalog,(item->>'consumptionId')::uuid,revision,revision-1,(item->>'lotId')::uuid,
     trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,(item->>'retracted')::boolean,actor,publication);
   END IF;
   INSERT INTO trading.gas_loan_quantity_consumption_members VALUES(catalog,operation,(item->>'consumptionId')::uuid,revision,state->>'acknowledgedRevision' IS NULL,item->>'sourceAllocation');
  END LOOP;
  facts=trading.gas_loan_allocation_facts(catalog,operation);
  FOR fact IN SELECT value FROM jsonb_array_elements(facts) LOOP
   INSERT INTO trading.gas_loan_quantity_sources VALUES(catalog,operation,account,(fact->>'sourceEvidenceId')::uuid,(fact->>'oldEvidenceId')::uuid,fact->>'sourceAllocation');
  END LOOP;
 END IF;
 IF all_ack THEN
  INSERT INTO trading.gas_loan_quantity_corrections VALUES(catalog,operation,account,loan.id,loan_ack,false,body->>'sourceAllocation',
   NULL,read_publication,context,as_of,actor,'gas_loan_quantity_reconciliation_v1');
  FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
   item=state->'selection';
   INSERT INTO trading.gas_loan_quantity_consumption_members VALUES(catalog,operation,(item->>'consumptionId')::uuid,(state->>'acknowledgedRevision')::integer,false,item->>'sourceAllocation');
  END LOOP;
 END IF;
 SELECT * INTO STRICT head FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id)=(catalog,loan.id) ORDER BY q.revision DESC LIMIT 1;
 FOR state IN SELECT value FROM jsonb_array_elements(states) LOOP
  item=state->'selection';
  SELECT * INTO STRICT used FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY q.revision DESC LIMIT 1;
  uses=uses||jsonb_build_array(jsonb_build_object('consumptionId',(item->>'consumptionId')::uuid,
   'acknowledgedRevision',CASE WHEN all_ack THEN(state->>'acknowledgedRevision')::integer ELSE used.revision END,
   'currentRevision',used.revision,'currentQuantity',trim_scale(used.quantity)::text,'changed',NOT all_ack AND state->>'acknowledgedRevision' IS NULL));
 END LOOP;
 outcome=jsonb_build_object('operationId',operation,'accountId',account,'loanId',loan.id,'action','loan_quantity_reconciliation','changed',NOT all_ack,
  'semanticAcknowledgement',all_ack,'publicationOrder',publication,'acknowledgedRevision',CASE WHEN all_ack THEN loan_ack ELSE head.revision END,
  'currentRevision',head.revision,'currentQuantity',trim_scale(head.quantity)::text,'stockId',loan.received_lot_id,'returnId',loan.return_position_id,
  'feeDisposition',body->>'feeDisposition','consumptions',uses,'sourceAllocations',facts);
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,(admission->>'until')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object('outcome',outcome);END IF;RETURN outcome;
END$$;
CREATE FUNCTION trading.gas_loan_quantity_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN RETURN trading.gas_loan_quantity_terms(body);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'gas_loan_quantity_input';END$$;

CREATE FUNCTION trading.gas_loan_quantity_snapshot(catalog uuid,body jsonb) RETURNS integer LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT q.revision FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id,q.quantity,q.title_evidence_id,q.agreement_evidence_id)=
  ($1,($2->>'loanId')::uuid,trading.gas_owned_quantity($2->>'quantity'),($2->>'titleEvidenceId')::uuid,($2->>'agreementEvidenceId')::uuid)
  AND(q.revision=1 AND $2->>'sourceAllocation'='preserve_unallocated_source_quantity_v1' OR EXISTS(
   SELECT 1 FROM trading.gas_loan_quantity_corrections h JOIN trading.gas_owned_commands c ON(c.catalog_id,c.request_id)=(h.catalog_id,h.operation_id)
   WHERE(h.catalog_id,h.loan_id,h.loan_revision,h.loan_changed)=(q.catalog_id,q.loan_id,q.revision,true)
    AND c.request#>>'{body,sourceAllocation}'=$2->>'sourceAllocation')) ORDER BY q.revision LIMIT 1
$$;
CREATE FUNCTION trading.gas_loan_consumption_snapshot(catalog uuid,body jsonb) RETURNS integer LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT q.revision FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id,q.lot_id,q.quantity,q.evidence_id,q.retracted)=
  ($1,($2->>'consumptionId')::uuid,($2->>'lotId')::uuid,trading.gas_owned_quantity($2->>'quantity'),($2->>'evidenceId')::uuid,($2->>'retracted')::boolean)
  AND q.reservation_id IS NOT DISTINCT FROM($2->>'reservationId')::uuid
  AND(q.revision=1 AND $2->>'sourceAllocation'='preserve_unallocated_source_quantity_v1' OR EXISTS(
   SELECT 1 FROM trading.gas_loan_quantity_consumption_members m WHERE(m.catalog_id,m.consumption_id,m.revision,m.source_allocation)=
    (q.catalog_id,q.consumption_id,q.revision,$2->>'sourceAllocation')) OR q.revision=trading.gas_owned_consumption_quantity_snapshot($1,$2))
 ORDER BY q.revision LIMIT 1
$$;

CREATE FUNCTION trading.require_gas_loan_quantity_references(catalog uuid,account uuid,body jsonb) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE owner trading.gas_owned_accounts;loan trading.gas_owned_loans;item jsonb;BEGIN
 SELECT * INTO owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account);
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_loan_quantity_source';END IF;
 SELECT * INTO loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id,l.account_id)=(catalog,(body->>'loanId')::uuid,account);
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_loan_quantity_source';END IF;
 IF security.reference_version_visible(owner.reference_catalog_id,owner.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,owner.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,loan.lender_id,loan.lender_revision))
 THEN RAISE EXCEPTION 'gas_loan_quantity_source';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.id)=(catalog,account,(body->>'titleEvidenceId')::uuid))
  OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.id)=(catalog,account,(body->>'agreementEvidenceId')::uuid))
 THEN RAISE EXCEPTION 'gas_loan_quantity_source';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.id)=(catalog,(item->>'consumptionId')::uuid))
   OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.account_id,p.id,p.kind)=(catalog,account,(item->>'lotId')::uuid,'owned_stock'))
   OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.id)=(catalog,account,(item->>'evidenceId')::uuid))
  THEN RAISE EXCEPTION 'gas_loan_quantity_source';END IF;
 END LOOP;
END$$;

CREATE FUNCTION trading.gas_loan_allocation_facts(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_loan_quantity_corrections;owner trading.gas_owned_accounts;old_head trading.gas_owned_loan_quantities;new_head trading.gas_owned_loan_quantities;
 item jsonb;source_key record;old_source trading.gas_owned_evidence;new_source trading.gas_owned_evidence;
 before_ids uuid[];after_ids uuid[];before_sources uuid[];after_sources uuid[];policies text[];before_amount numeric;after_amount numeric;fact jsonb;result jsonb='[]';BEGIN
 SELECT * INTO STRICT h FROM trading.gas_loan_quantity_corrections c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,h.account_id);
 SELECT * INTO STRICT new_head FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id,q.revision)=(catalog,h.loan_id,h.loan_revision);
 FOR source_key IN SELECT DISTINCT e.source_key,e.kind FROM(
  SELECT q.title_evidence_id id FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id)=(catalog,h.loan_id)
   AND q.revision IN(h.loan_revision,CASE WHEN h.loan_changed THEN h.loan_revision-1 ELSE h.loan_revision END)
  UNION SELECT q.agreement_evidence_id FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id)=(catalog,h.loan_id)
   AND q.revision IN(h.loan_revision,CASE WHEN h.loan_changed THEN h.loan_revision-1 ELSE h.loan_revision END)
  UNION SELECT q.evidence_id FROM trading.gas_loan_quantity_consumption_members m JOIN trading.gas_owned_consumption_quantities q
   ON(q.catalog_id,q.consumption_id)=(m.catalog_id,m.consumption_id)
   WHERE(m.catalog_id,m.operation_id)=(catalog,operation) AND q.revision IN(m.revision,CASE WHEN m.changed THEN m.revision-1 ELSE m.revision END)
 )ids JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(catalog,ids.id) LOOP
  before_ids='{}';after_ids='{}';before_sources='{}';after_sources='{}';policies='{}';
  IF source_key.kind IN('title_receipt','loan_agreement') THEN
   -- Every allocation of this raw source must be the selected loan. Sources
   -- shared with other owners require the explicit multi-loan closure.
   SELECT coalesce(array_agg(q.position_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(abs(q.quantity)),0)
    INTO before_ids,before_sources,before_amount FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,h.account_id,source_key.source_key) AND q.revision=(SELECT max(p.revision) FROM trading.gas_owned_position_quantities p
     WHERE(p.catalog_id,p.position_id)=(q.catalog_id,q.position_id) AND p.publication_order<h.publication_order);
   SELECT coalesce(array_agg(q.position_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(abs(q.quantity)),0)
    INTO after_ids,after_sources,after_amount FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,h.account_id,source_key.source_key) AND q.revision=(SELECT max(p.revision) FROM trading.gas_owned_position_quantities p
     WHERE(p.catalog_id,p.position_id)=(q.catalog_id,q.position_id) AND p.publication_order<=h.publication_order);
   IF cardinality(before_ids)<>1 OR cardinality(after_ids)<>1 OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_loans l
    WHERE(l.catalog_id,l.id)=(catalog,h.loan_id) AND before_ids[1]=CASE source_key.kind WHEN 'title_receipt' THEN l.received_lot_id ELSE l.return_position_id END
     AND after_ids[1]=before_ids[1]) THEN RAISE EXCEPTION 'gas_loan_quantity_closure';END IF;
   policies=ARRAY[h.source_allocation];
  ELSIF source_key.kind IN('physical_delivery','physical_loss','loan_return') THEN
   SELECT coalesce(array_agg(q.consumption_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(q.quantity),0)
    INTO before_ids,before_sources,before_amount FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,h.account_id,source_key.source_key) AND q.revision=(SELECT max(p.revision) FROM trading.gas_owned_consumption_quantities p
     WHERE(p.catalog_id,p.consumption_id)=(q.catalog_id,q.consumption_id) AND p.publication_order<h.publication_order);
   SELECT coalesce(array_agg(q.consumption_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(q.quantity),0)
    INTO after_ids,after_sources,after_amount FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,h.account_id,source_key.source_key) AND q.revision=(SELECT max(p.revision) FROM trading.gas_owned_consumption_quantities p
     WHERE(p.catalog_id,p.consumption_id)=(q.catalog_id,q.consumption_id) AND p.publication_order<=h.publication_order);
   IF cardinality(before_ids)>16384 OR cardinality(after_ids)>16384 OR EXISTS(SELECT 1 FROM unnest(before_ids||after_ids) selected(id)
    WHERE NOT EXISTS(SELECT 1 FROM trading.gas_loan_quantity_consumption_members m WHERE(m.catalog_id,m.operation_id,m.consumption_id)=(catalog,operation,selected.id)))
   THEN RAISE EXCEPTION 'gas_loan_quantity_closure';END IF;
   SELECT array_agg(DISTINCT m.source_allocation) INTO policies FROM trading.gas_loan_quantity_consumption_members m
    WHERE(m.catalog_id,m.operation_id)=(catalog,operation) AND m.consumption_id=ANY(after_ids);
  ELSE RAISE EXCEPTION 'gas_loan_quantity_closure';END IF;
  IF cardinality(before_sources)<>1 OR cardinality(after_sources)<>1 OR cardinality(policies)<>1 THEN RAISE EXCEPTION 'gas_loan_quantity_closure';END IF;
  SELECT * INTO STRICT old_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=(catalog,before_sources[1]);
  SELECT * INTO STRICT new_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=(catalog,after_sources[1]);
  IF(old_source.account_id,old_source.source_key,old_source.kind,old_source.occurred_at) IS DISTINCT FROM
   (new_source.account_id,new_source.source_key,new_source.kind,new_source.occurred_at) OR new_source.quantity<0 AND source_key.kind IN('title_receipt','loan_agreement')
   OR trading.gas_owned_visible(catalog,old_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',h.source_context_id,h.as_of) IS NOT TRUE
   OR trading.gas_owned_visible(catalog,new_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',h.source_context_id,h.as_of) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,h.account_id,source_key.source_key)
    AND e.publication_order<=h.publication_order AND e.revision>new_source.revision) THEN RAISE EXCEPTION 'gas_loan_quantity_source';END IF;
  fact=jsonb_build_object('accountId',h.account_id,'sourceKey',source_key.source_key,'kind',source_key.kind,'oldEvidenceId',old_source.id,'sourceEvidenceId',new_source.id,
   'oldCapacity',trim_scale(abs(old_source.quantity))::text,'capacity',trim_scale(abs(new_source.quantity))::text,
   'oldAllocated',trim_scale(before_amount)::text,'allocated',trim_scale(after_amount)::text,'newEnrollment',false,
   'referenceCatalogId',owner.reference_catalog_id,'referenceRevisionId',owner.reference_revision_id,'quantity',owner.quantity_code);
  PERFORM trading.require_gas_source_allocation(policies[1],jsonb_build_array(fact));
  result=result||jsonb_build_array(fact||jsonb_build_object('sourceAllocation',policies[1]));
 END LOOP;
 IF jsonb_array_length(result) NOT BETWEEN 2 AND 16384 OR octet_length(convert_to(result::text,'UTF8'))>8388608 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 SELECT jsonb_agg(value ORDER BY value->>'sourceKey',value->>'kind') INTO result FROM jsonb_array_elements(result);
 RETURN result;
END$$;

CREATE FUNCTION trading.require_gas_loan_quantity_amendment(catalog uuid,account uuid,body jsonb,context uuid,as_of timestamptz,policy uuid,policy_revision integer) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE loan trading.gas_owned_loans;owner trading.gas_owned_accounts;head trading.gas_owned_loan_quantities;stock trading.gas_owned_position_quantities;
 owed trading.gas_owned_position_quantities;binding trading.gas_owned_action_bindings;item jsonb;native trading.gas_owned_consumptions;used trading.gas_owned_consumption_quantities;
 old_source trading.gas_owned_evidence;new_source trading.gas_owned_evidence;source_id uuid;positions uuid[];ancestors uuid[];books uuid[];conflict jsonb;source_kind text;BEGIN
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.account_id,l.id)=(catalog,account,(body->>'loanId')::uuid);
 SELECT * INTO STRICT owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account);
 IF NOT security.trading_context_day_matches(catalog,context,as_of)
  OR trading.gas_owned_visible(catalog,owner.publication_order,owner.effective_at,context,as_of) IS NOT TRUE OR NOT EXISTS(
   SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,loan.lender_id,loan.lender_revision)
    AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of)) THEN RAISE EXCEPTION 'gas_owned_context';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,owner.definition_revision_id);
 PERFORM trading.require_gas_owned_head(catalog,account,context,as_of);PERFORM trading.require_gas_owned_quantity_head(catalog,account,context,as_of);
 SELECT * INTO STRICT head FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id)=(catalog,loan.id) ORDER BY q.revision DESC LIMIT 1;
 SELECT * INTO STRICT stock FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,loan.received_lot_id) ORDER BY q.revision DESC LIMIT 1;
 SELECT * INTO STRICT owed FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(catalog,loan.return_position_id) ORDER BY q.revision DESC LIMIT 1;
 IF(head.revision,stock.revision,owed.revision) IS DISTINCT FROM
  ((body->>'expectedRevision')::integer,(body->>'stockExpectedRevision')::integer,(body->>'returnExpectedRevision')::integer)
  OR trading.gas_owned_visible(catalog,head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(catalog,loan.received_lot_id) AND p.effective_at<=as_of
   AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of)) THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
 SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,'gas.loan.reconcile_quantity') ORDER BY b.revision DESC LIMIT 1;
 IF(binding.policy_id,binding.policy_revision) IS DISTINCT FROM(policy,policy_revision) THEN RAISE EXCEPTION 'gas_owned_policy_changed';END IF;
 PERFORM lifecycle.require_policy(catalog,policy,policy_revision,'gas.loan.reconcile_quantity',as_of,false);
 FOR source_kind,source_id IN SELECT 'title_receipt',(body->>'titleEvidenceId')::uuid UNION ALL SELECT 'loan_agreement',(body->>'agreementEvidenceId')::uuid LOOP
  SELECT * INTO STRICT old_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=
   (catalog,CASE source_kind WHEN 'title_receipt' THEN head.title_evidence_id ELSE head.agreement_evidence_id END);
  SELECT * INTO STRICT new_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.id)=(catalog,account,source_id);
  IF(new_source.kind,new_source.source_key,new_source.occurred_at) IS DISTINCT FROM(source_kind,old_source.source_key,old_source.occurred_at)
   OR new_source.quantity<trading.gas_owned_quantity(body->>'quantity')
   OR trading.gas_owned_visible(catalog,new_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,account,new_source.source_key) AND e.revision>new_source.revision)
  THEN RAISE EXCEPTION 'gas_quantity_evidence';END IF;
 END LOOP;
 positions=ARRAY[loan.received_lot_id,loan.return_position_id];
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  SELECT * INTO STRICT native FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.id)=(catalog,(item->>'consumptionId')::uuid);
  SELECT * INTO STRICT used FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(catalog,native.id) ORDER BY q.revision DESC LIMIT 1;
  IF used.revision<>(item->>'expectedRevision')::integer
   OR trading.gas_owned_visible(catalog,used.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR(native.kind='return' AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_repayments r WHERE(r.catalog_id,r.loan_id,r.consumption_id)=(catalog,loan.id,native.id)))
   OR(native.kind IN('delivery','loss') AND native.lot_id<>loan.received_lot_id)
   OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.consumption_id)=(catalog,native.id))
   OR EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s WHERE(s.catalog_id,s.consumption_id)=(catalog,native.id))
   OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.account_id,p.id,p.kind)=(catalog,account,(item->>'lotId')::uuid,'owned_stock')
    AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of))
  THEN RAISE EXCEPTION 'gas_loan_quantity_closure';END IF;
  SELECT * INTO STRICT old_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=(catalog,used.evidence_id);
  SELECT * INTO STRICT new_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.id)=(catalog,account,(item->>'evidenceId')::uuid);
  source_kind=CASE native.kind WHEN 'return' THEN 'loan_return' WHEN 'loss' THEN 'physical_loss' ELSE 'physical_delivery' END;
  IF(new_source.kind,new_source.source_key,new_source.occurred_at) IS DISTINCT FROM(source_kind,old_source.source_key,native.effective_at)
   OR abs(new_source.quantity)<trading.gas_owned_quantity(item->>'quantity') OR native.kind='return' AND new_source.quantity<0
   OR trading.gas_owned_visible(catalog,new_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,account,new_source.source_key) AND e.revision>new_source.revision)
   OR item->>'reservationId' IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_reservations r
    WHERE(r.catalog_id,r.id,r.lot_id)=(catalog,(item->>'reservationId')::uuid,(item->>'lotId')::uuid)
     AND trading.gas_owned_visible(catalog,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of))
  THEN RAISE EXCEPTION 'gas_quantity_evidence';END IF;
  positions=array_append(array_append(positions,used.lot_id),(item->>'lotId')::uuid);
 END LOOP;
 -- Funding dependencies are checked under the same original knowledge prefix.
 -- Only the exact source facts this complete correction replaces are exempt.
 ancestors=trading.gas_owned_financial_ancestors(catalog,positions,context,as_of);
 SELECT coalesce(array_agg(DISTINCT a.book_id),'{}') INTO books FROM trading.gas_owned_positions p JOIN trading.gas_owned_accounts a
  ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id) WHERE p.catalog_id=catalog AND p.id=ANY(ancestors);
 FOR conflict IN SELECT value FROM jsonb_array_elements(trading.gas_owned_applied_source_conflicts(catalog,context,as_of,books,ancestors)||
  trading.gas_owned_applied_observation_conflicts(catalog,context,as_of,books,ancestors)) LOOP
  IF conflict->>'sourceKind'='gas_evidence' AND(conflict->>'kind'='position' AND(
   (conflict->>'effectId')::uuid=loan.received_lot_id AND conflict->'currentSourceIds'=jsonb_build_array((body->>'titleEvidenceId')::uuid)
   OR(conflict->>'effectId')::uuid=loan.return_position_id AND conflict->'currentSourceIds'=jsonb_build_array((body->>'agreementEvidenceId')::uuid))
   OR conflict->>'kind'='consumption' AND EXISTS(SELECT 1 FROM jsonb_array_elements(body->'consumptions') chosen
    WHERE(chosen->>'consumptionId')::uuid=(conflict->>'effectId')::uuid AND conflict->'currentSourceIds'=jsonb_build_array((chosen->>'evidenceId')::uuid))) THEN CONTINUE;END IF;
  RAISE EXCEPTION 'gas_owned_pending_source_reconciliation';
 END LOOP;
END$$;

CREATE FUNCTION trading.guard_gas_loan_quantity_member() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_loan_quantity_corrections;actor uuid;BEGIN
 IF TG_TABLE_NAME='gas_loan_quantity_corrections' THEN h=NEW;
 ELSE SELECT * INTO STRICT h FROM trading.gas_loan_quantity_corrections r WHERE(r.catalog_id,r.operation_id)=(NEW.catalog_id,NEW.operation_id);END IF;
 SELECT r.actor_id INTO actor FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid()
  AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user AND r.expires_at>clock_timestamp();
 IF h.actor_id IS DISTINCT FROM actor OR h.publication_order IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.publications p
  WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(h.catalog_id,h.publication_order,pg_current_xact_id()))
  OR TG_TABLE_NAME='gas_loan_quantity_sources' AND h.publication_order IS NULL
 THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;RETURN NEW;
END$$;

CREATE FUNCTION trading.check_gas_loan_quantity() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE loan trading.gas_owned_loans;stock trading.gas_owned_position_quantities;owed trading.gas_owned_position_quantities;
 origin_stock trading.gas_owned_positions;origin_return trading.gas_owned_positions;BEGIN
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id,l.account_id)=(NEW.catalog_id,NEW.loan_id,NEW.account_id);
 SELECT * INTO STRICT stock FROM trading.gas_owned_position_quantities p WHERE(p.catalog_id,p.position_id,p.revision)=(NEW.catalog_id,loan.received_lot_id,NEW.stock_revision);
 SELECT * INTO STRICT owed FROM trading.gas_owned_position_quantities p WHERE(p.catalog_id,p.position_id,p.revision)=(NEW.catalog_id,loan.return_position_id,NEW.return_revision);
 IF(stock.quantity,stock.evidence_id,stock.retracted,owed.quantity,owed.evidence_id,owed.retracted) IS DISTINCT FROM
  (NEW.quantity,NEW.title_evidence_id,NEW.quantity=0,-NEW.quantity,NEW.agreement_evidence_id,NEW.quantity=0)
  OR stock.publication_order>NEW.publication_order OR owed.publication_order>NEW.publication_order
 THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 IF NEW.revision=1 THEN
  SELECT * INTO STRICT origin_stock FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,loan.received_lot_id);
  SELECT * INTO STRICT origin_return FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,loan.return_position_id);
  IF(NEW.quantity,NEW.title_evidence_id,NEW.agreement_evidence_id,NEW.stock_revision,NEW.return_revision,NEW.publication_order,NEW.actor_id) IS DISTINCT FROM
   (loan.quantity,origin_stock.evidence_id,origin_return.evidence_id,1,1,origin_stock.publication_order,origin_stock.actor_id)
  THEN RAISE EXCEPTION 'gas_loan_quantity_origin';END IF;
 ELSIF NOT EXISTS(SELECT 1 FROM trading.gas_loan_quantity_corrections h JOIN trading.publications p
  ON(p.catalog_id,p.publication_order,p.publication_xid)=(h.catalog_id,h.publication_order,pg_current_xact_id())
  WHERE(h.catalog_id,h.loan_id,h.loan_revision,h.loan_changed,h.publication_order,h.actor_id)=
   (NEW.catalog_id,NEW.loan_id,NEW.revision,true,NEW.publication_order,NEW.actor_id)) THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 RETURN NULL;
END$$;

CREATE FUNCTION trading.check_gas_loan_quantity_correction() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_loan_quantity_corrections;c trading.gas_owned_commands;loan trading.gas_owned_loans;
 head trading.gas_owned_loan_quantities;before_head trading.gas_owned_loan_quantities;stock trading.gas_owned_position_quantities;owed trading.gas_owned_position_quantities;
 body jsonb;item jsonb;member trading.gas_loan_quantity_consumption_members;used trading.gas_owned_consumption_quantities;before_use trading.gas_owned_consumption_quantities;
 source jsonb;facts jsonb;through_publication integer;binding trading.gas_owned_action_bindings;operation uuid;BEGIN
 IF TG_TABLE_NAME='gas_owned_commands' THEN
  IF NEW.request->>'action' IS DISTINCT FROM 'loan_quantity_reconciliation' THEN RETURN NULL;END IF;operation=NEW.request_id;
 ELSE operation=NEW.operation_id;END IF;
 SELECT * INTO STRICT h FROM trading.gas_loan_quantity_corrections r WHERE(r.catalog_id,r.operation_id)=(NEW.catalog_id,operation);
 SELECT * INTO STRICT c FROM trading.gas_owned_commands r WHERE(r.catalog_id,r.request_id)=(h.catalog_id,h.operation_id);
 PERFORM trading.gas_owned_projection_keys(c.request,ARRAY['action','accountId','body','asOf','contextId','policyId','policyRevision','automatic'],ARRAY[]::text[]);
 body=c.request->'body';
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR jsonb_typeof(body->'consumptions') IS DISTINCT FROM 'array'
  OR trading.gas_loan_quantity_terms(body) IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 IF(c.actor_id,c.account_id,c.configuring,c.requires_jobs,c.request->>'action',(c.request->>'accountId')::uuid,(c.request->>'asOf')::timestamptz,
   (c.request->>'contextId')::uuid,(c.request->>'automatic')::boolean,(body->>'loanId')::uuid,body->>'sourceAllocation') IS DISTINCT FROM
  (h.actor_id,h.account_id,false,false,'loan_quantity_reconciliation',h.account_id,h.as_of,h.source_context_id,false,h.loan_id,h.source_allocation)
 THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 PERFORM security.require_gas_owned_commit(h.catalog_id,c.authority_until,false,false);
 through_publication=coalesce(h.publication_order,h.read_publication_order);
 SELECT * INTO STRICT loan FROM trading.gas_owned_loans l WHERE(l.catalog_id,l.id,l.account_id)=(h.catalog_id,h.loan_id,h.account_id);
 SELECT * INTO STRICT head FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id,q.revision)=(h.catalog_id,h.loan_id,h.loan_revision);
 IF(head.quantity,head.title_evidence_id,head.agreement_evidence_id) IS DISTINCT FROM
  (trading.gas_owned_quantity(body->>'quantity'),(body->>'titleEvidenceId')::uuid,(body->>'agreementEvidenceId')::uuid)
  OR head.publication_order>through_publication THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 IF h.publication_order IS NOT NULL THEN
  IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=
   (h.catalog_id,h.publication_order,pg_current_xact_id())) THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
  SELECT * INTO STRICT before_head FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.loan_id)=(h.catalog_id,h.loan_id)
   AND q.publication_order<h.publication_order ORDER BY q.revision DESC LIMIT 1;
  SELECT * INTO STRICT stock FROM trading.gas_owned_position_quantities p WHERE(p.catalog_id,p.position_id)=(h.catalog_id,loan.received_lot_id)
   AND p.publication_order<h.publication_order ORDER BY p.revision DESC LIMIT 1;
  SELECT * INTO STRICT owed FROM trading.gas_owned_position_quantities p WHERE(p.catalog_id,p.position_id)=(h.catalog_id,loan.return_position_id)
   AND p.publication_order<h.publication_order ORDER BY p.revision DESC LIMIT 1;
  IF(before_head.revision,stock.revision,owed.revision) IS DISTINCT FROM
   ((body->>'expectedRevision')::integer,(body->>'stockExpectedRevision')::integer,(body->>'returnExpectedRevision')::integer)
   OR head.revision<>before_head.revision+(CASE WHEN h.loan_changed THEN 1 ELSE 0 END)
   OR h.loan_changed IS DISTINCT FROM(head.publication_order=h.publication_order)
   OR trading.gas_loan_quantity_snapshot(h.catalog_id,body) IS DISTINCT FROM h.loan_revision
   OR trading.gas_owned_visible(h.catalog_id,before_head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',h.source_context_id,h.as_of) IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
  SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=
   (h.catalog_id,h.account_id,'gas.loan.reconcile_quantity') AND b.publication_order<=h.publication_order ORDER BY b.revision DESC LIMIT 1;
  IF(binding.policy_id,binding.policy_revision) IS DISTINCT FROM((c.request->>'policyId')::uuid,(c.request->>'policyRevision')::integer)
  THEN RAISE EXCEPTION 'gas_owned_policy_changed';END IF;
  PERFORM lifecycle.require_policy(h.catalog_id,binding.policy_id,binding.policy_revision,'gas.loan.reconcile_quantity',h.as_of,false);
 ELSE
  IF h.loan_changed OR trading.gas_loan_quantity_snapshot(h.catalog_id,body) IS DISTINCT FROM h.loan_revision
  THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 END IF;
 IF jsonb_array_length(body->'consumptions')<>(SELECT count(*) FROM trading.gas_loan_quantity_consumption_members m
  WHERE(m.catalog_id,m.operation_id)=(h.catalog_id,h.operation_id)) THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'consumptions') LOOP
  SELECT * INTO STRICT member FROM trading.gas_loan_quantity_consumption_members m WHERE(m.catalog_id,m.operation_id,m.consumption_id)=
   (h.catalog_id,h.operation_id,(item->>'consumptionId')::uuid);
  SELECT * INTO STRICT used FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id,q.revision)=
   (h.catalog_id,member.consumption_id,member.revision);
  IF(used.lot_id,used.quantity,used.evidence_id,used.retracted,member.source_allocation) IS DISTINCT FROM
   ((item->>'lotId')::uuid,trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'retracted')::boolean,item->>'sourceAllocation')
   OR used.reservation_id IS DISTINCT FROM(item->>'reservationId')::uuid OR used.publication_order>through_publication
  THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
  IF h.publication_order IS NULL THEN
   IF member.changed OR trading.gas_loan_consumption_snapshot(h.catalog_id,item) IS DISTINCT FROM member.revision THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
  ELSE
   SELECT * INTO STRICT before_use FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id)=(h.catalog_id,member.consumption_id)
    AND q.publication_order<h.publication_order ORDER BY q.revision DESC LIMIT 1;
   IF before_use.revision<>(item->>'expectedRevision')::integer OR used.revision<>before_use.revision+(CASE WHEN member.changed THEN 1 ELSE 0 END)
    OR member.changed IS DISTINCT FROM(used.publication_order=h.publication_order)
    OR trading.gas_loan_consumption_snapshot(h.catalog_id,item) IS DISTINCT FROM member.revision
    OR trading.gas_owned_visible(h.catalog_id,before_use.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',h.source_context_id,h.as_of) IS NOT TRUE
   THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
  END IF;
 END LOOP;
 IF h.publication_order IS NOT NULL THEN
  -- This consumer creates quantity revisions only. Cash memberships and bank
  -- allocations are retained independently; the explicit fixed-fee law is not
  -- permission to rewrite, net or recreate either side of a past payment.
  IF(SELECT count(*) FROM trading.gas_owned_loan_quantities q WHERE(q.catalog_id,q.publication_order)=(h.catalog_id,h.publication_order))<>(CASE WHEN h.loan_changed THEN 1 ELSE 0 END)
   OR EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.publication_order)=(h.catalog_id,h.publication_order)
    AND NOT trading.gas_loan_quantity_position_proof(q.catalog_id,q.position_id,q.revision))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.publication_order)=(h.catalog_id,h.publication_order)
    AND NOT trading.gas_loan_quantity_consumption_proof(q.catalog_id,q.consumption_id,q.revision))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.publication_order)=(h.catalog_id,h.publication_order))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_fee_payments p WHERE(p.catalog_id,p.publication_order)=(h.catalog_id,h.publication_order))
  THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
  facts=trading.gas_loan_allocation_facts(h.catalog_id,h.operation_id);
  IF jsonb_array_length(facts)<>(SELECT count(*) FROM trading.gas_loan_quantity_sources s WHERE(s.catalog_id,s.operation_id)=(h.catalog_id,h.operation_id))
  THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
  FOR source IN SELECT value FROM jsonb_array_elements(facts) LOOP
   IF NOT EXISTS(SELECT 1 FROM trading.gas_loan_quantity_sources s WHERE(s.catalog_id,s.operation_id,s.account_id,s.source_evidence_id,s.previous_evidence_id,s.source_allocation)=
    (h.catalog_id,h.operation_id,h.account_id,(source->>'sourceEvidenceId')::uuid,(source->>'oldEvidenceId')::uuid,source->>'sourceAllocation'))
   THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
  END LOOP;
 ELSIF EXISTS(SELECT 1 FROM trading.gas_loan_quantity_sources s WHERE(s.catalog_id,s.operation_id)=(h.catalog_id,h.operation_id)) THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 IF c.outcome IS DISTINCT FROM trading.gas_loan_correction_outcome(h.catalog_id,h.operation_id) THEN RAISE EXCEPTION 'gas_loan_quantity_seal';END IF;
 RETURN NULL;
END$$;

CREATE VIEW trading.gas_owned_loans_current AS
 SELECT (jsonb_populate_record(l,jsonb_build_object('quantity',q.quantity))).* FROM trading.gas_owned_loans l
 CROSS JOIN LATERAL(SELECT v.* FROM trading.gas_owned_loan_quantities v WHERE(v.catalog_id,v.loan_id)=(l.catalog_id,l.id) ORDER BY v.revision DESC LIMIT 1)q;
ALTER VIEW trading.gas_owned_loans_current OWNER TO openetrm_security_owner;
REVOKE ALL ON trading.gas_owned_loans_current FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

-- Replace only the qualified loan source in existing consumers. Public OIDs,
-- source clocks, native linkage and original authority checks remain intact.
DO $upgrade$DECLARE spec record;p pg_proc;definition text;replacement text;needle text;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('trading.check_gas_owned_integrity()','af80f237808caa671d808533d74e7a66','trigger',true,'v',false,NULL,'current',NULL),
  ('trading.check_gas_quantity_revision()','9c7478bf6beea2a131a561ac77b65512','trigger',true,'v',false,NULL,'proof',NULL),
  ('security.read_gas_owned_operations(uuid,uuid,timestamptz,uuid)','f933f3fb7c04f9991debfa4701d24e8c','jsonb',true,'v',true,ARRAY['catalog','context','as_of','account'],'source','as_of'),
  ('security.read_gas_owned_known_history_pre_risk(uuid,uuid,timestamptz,uuid[])','520b6c44b8919d711d87d9979c3ee541','jsonb',true,'v',false,ARRAY['catalog','context','at_time','books'],'source','at_time'),
  ('security.gas_physical_journal_source(uuid,uuid,timestamptz,uuid[],uuid[])','277439c3a64663999f5dcb4fcfe68ab9','jsonb',true,'v',false,ARRAY['catalog','context','at_time','books','selected_positions'],'source','at_time'),
  ('trading.apply_gas_owned_financial(uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer)','236f0f0248e75c1499e83ce5f3c323b8','jsonb',false,'v',false,ARRAY['catalog','account','action','body','as_of','context','actor','publication'],'current',NULL),
  ('trading.require_gas_owned_quantity_head(uuid,uuid,uuid,timestamptz)','0231301e078eccbbc19d67d54de7d4e5','void',true,'s',false,ARRAY['catalog','account','context','as_of'],'head',NULL)
 )s(signature,source_hash,result_type,definer,volatility,runtime_execute,names,mode,time_name) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=spec.signature::regprocedure;
  IF md5(p.prosrc)<>spec.source_hash OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>spec.result_type::regtype OR p.proretset
   OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
   OR p.proargnames IS DISTINCT FROM spec.names OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
   OR p.provolatile::text<>spec.volatility OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM spec.definer
   OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.prosupport<>0 OR p.probin IS NOT NULL OR p.prosqlbody IS NOT NULL OR p.protrftypes IS NOT NULL
   OR has_function_privilege('openetrm_runtime',p.oid,'EXECUTE') IS DISTINCT FROM spec.runtime_execute
   OR has_function_privilege('openetrm_risk_worker',p.oid,'EXECUTE')
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner)))a WHERE a.grantor<>p.proowner OR a.is_grantable OR a.privilege_type<>'EXECUTE'
    OR a.grantee NOT IN(p.proowner,CASE WHEN spec.runtime_execute THEN 'openetrm_runtime'::regrole ELSE p.proowner END))
  THEN RAISE EXCEPTION 'gas_loan_quantity_upgrade: unexpected helper %',spec.signature;END IF;
  definition=pg_get_functiondef(p.oid);
  IF spec.mode='proof' THEN
   needle='IF NOT trading.gas_owned_quantity_position_proof(NEW.catalog_id,NEW.position_id,NEW.revision) AND NOT EXISTS';
   IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_loan_quantity_upgrade';END IF;
   definition=replace(definition,needle,'IF NOT trading.gas_loan_quantity_position_proof(NEW.catalog_id,NEW.position_id,NEW.revision) AND NOT trading.gas_owned_quantity_position_proof(NEW.catalog_id,NEW.position_id,NEW.revision) AND NOT EXISTS');
   needle='ELSIF NOT trading.gas_owned_quantity_consumption_proof(NEW.catalog_id,NEW.consumption_id,NEW.revision) AND NOT EXISTS';
   IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_loan_quantity_upgrade';END IF;
   definition=replace(definition,needle,'ELSIF NOT trading.gas_loan_quantity_consumption_proof(NEW.catalog_id,NEW.consumption_id,NEW.revision) AND NOT trading.gas_owned_quantity_consumption_proof(NEW.catalog_id,NEW.consumption_id,NEW.revision) AND NOT EXISTS');
  ELSIF spec.mode='head' THEN
   needle='SELECT q.publication_order FROM trading.gas_owned_position_quantities q WHERE';
   IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_loan_quantity_upgrade';END IF;
   definition=replace(definition,needle,'SELECT lq.publication_order FROM trading.gas_owned_loan_quantities lq WHERE(lq.catalog_id,lq.account_id)=(catalog,account) AND lq.revision>1 UNION ALL SELECT q.publication_order FROM trading.gas_owned_position_quantities q WHERE');
  ELSE
   replacement=CASE WHEN spec.mode='current' THEN 'trading.gas_owned_loans_current' ELSE 'trading.gas_owned_loans_at(catalog,context,'||spec.time_name||')' END;
   IF strpos(definition,'FROM trading.gas_owned_loans ')=0 AND strpos(definition,'JOIN trading.gas_owned_loans ')=0 THEN RAISE EXCEPTION 'gas_loan_quantity_upgrade';END IF;
   definition=replace(replace(definition,'FROM trading.gas_owned_loans ','FROM '||replacement||' '),'JOIN trading.gas_owned_loans ','JOIN '||replacement||' ');
  END IF;
  EXECUTE definition;
 END LOOP;
END$upgrade$;

DO $$DECLARE name text;f regprocedure;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_loan_quantity_corrections','gas_loan_quantity_consumption_members','gas_loan_quantity_sources'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('REVOKE ALL ON trading.%I FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
  EXECUTE format('CREATE TRIGGER native_member BEFORE INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_loan_quantity_member()',name);
 END LOOP;
 FOREACH f IN ARRAY ARRAY['trading.gas_loan_quantity_terms(jsonb)'::regprocedure,'trading.gas_loan_quantity_fact(jsonb)'::regprocedure,
  'trading.gas_loan_quantity_snapshot(uuid,jsonb)'::regprocedure,'trading.gas_loan_consumption_snapshot(uuid,jsonb)'::regprocedure,
  'trading.require_gas_loan_quantity_references(uuid,uuid,jsonb)'::regprocedure,'trading.gas_loan_allocation_facts(uuid,uuid)'::regprocedure,
  'trading.require_gas_loan_quantity_amendment(uuid,uuid,jsonb,uuid,timestamptz,uuid,integer)'::regprocedure,
  'trading.gas_loan_quantity_position_proof(uuid,uuid,integer)'::regprocedure,'trading.gas_loan_quantity_consumption_proof(uuid,uuid,integer)'::regprocedure,
  'trading.gas_loan_correction_outcome(uuid,uuid)'::regprocedure,'trading.guard_gas_loan_quantity_member()'::regprocedure,
  'trading.check_gas_loan_quantity()'::regprocedure,'trading.check_gas_loan_quantity_correction()'::regprocedure,
  'security.reconcile_gas_loan_quantities(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.reconcile_gas_loan_quantities(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean) TO openetrm_runtime;
CREATE CONSTRAINT TRIGGER exact_loan_quantity AFTER INSERT ON trading.gas_owned_loan_quantities DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_loan_quantity();
CREATE CONSTRAINT TRIGGER owned_integrity AFTER INSERT ON trading.gas_owned_loan_quantities DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_owned_integrity();
CREATE CONSTRAINT TRIGGER sealed_loan_correction AFTER INSERT ON trading.gas_loan_quantity_corrections DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_loan_quantity_correction();
CREATE CONSTRAINT TRIGGER sealed_loan_correction AFTER INSERT ON trading.gas_owned_commands DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_loan_quantity_correction();


COMMIT;
