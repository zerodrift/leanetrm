-- Financial amendments retain native identities and event instants. Corrected
-- evidence alone never rewrites accepted title, dispatch or payment history.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.gas_owned_position_quantities(
 catalog_id uuid NOT NULL,position_id uuid NOT NULL,account_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),
 previous_revision integer,quantity ref_data.exact_number NOT NULL,evidence_id uuid NOT NULL,retracted boolean NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,position_id,revision),UNIQUE(catalog_id,position_id,publication_order),
 FOREIGN KEY(catalog_id,position_id,account_id) REFERENCES trading.gas_owned_positions(catalog_id,id,account_id),
 FOREIGN KEY(catalog_id,account_id,evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,position_id,previous_revision) REFERENCES trading.gas_owned_position_quantities(catalog_id,position_id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(retracted=(quantity=0)),CHECK(revision=1 AND previous_revision IS NULL OR revision>1 AND previous_revision IS NOT NULL AND previous_revision=revision-1)
);
CREATE TABLE trading.gas_owned_consumption_quantities(
 catalog_id uuid NOT NULL,consumption_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_revision integer,
 lot_id uuid NOT NULL,quantity ref_data.exact_number NOT NULL CHECK(quantity>=0),evidence_id uuid NOT NULL,reservation_id uuid,retracted boolean NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,consumption_id,revision),UNIQUE(catalog_id,consumption_id,publication_order),
 FOREIGN KEY(catalog_id,consumption_id) REFERENCES trading.gas_owned_consumptions(catalog_id,id),
 FOREIGN KEY(catalog_id,lot_id) REFERENCES trading.gas_owned_lots(catalog_id,id),
 FOREIGN KEY(catalog_id,evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id),
 FOREIGN KEY(catalog_id,reservation_id) REFERENCES trading.gas_owned_reservations(catalog_id,id),
 FOREIGN KEY(catalog_id,consumption_id,previous_revision) REFERENCES trading.gas_owned_consumption_quantities(catalog_id,consumption_id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(retracted=(quantity=0)),CHECK(revision=1 AND previous_revision IS NULL OR revision>1 AND previous_revision IS NOT NULL AND previous_revision=revision-1)
);
CREATE TABLE trading.gas_futures_performance_quantities(
 catalog_id uuid NOT NULL,performance_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_revision integer,
 quantity ref_data.exact_number NOT NULL CHECK(quantity>=0),title_evidence_id uuid,retracted boolean NOT NULL,source_allocation text NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,performance_id,revision),UNIQUE(catalog_id,performance_id,publication_order),
 FOREIGN KEY(catalog_id,performance_id) REFERENCES trading.gas_futures_delivery_performances(catalog_id,id),
 FOREIGN KEY(catalog_id,title_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id),
 FOREIGN KEY(catalog_id,performance_id,previous_revision) REFERENCES trading.gas_futures_performance_quantities(catalog_id,performance_id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(retracted=(quantity=0)),CHECK(revision=1 AND previous_revision IS NULL OR revision>1 AND previous_revision IS NOT NULL AND previous_revision=revision-1),
 CHECK(revision=1 AND source_allocation='initial_native_source_enrollment_v1' OR revision>1 AND source_allocation IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1'))
);
CREATE TABLE trading.gas_futures_performance_quantity_supplies(
 catalog_id uuid NOT NULL,performance_id uuid NOT NULL,performance_revision integer NOT NULL,ordinal integer NOT NULL CHECK(ordinal>0),
 consumption_id uuid NOT NULL,consumption_revision integer NOT NULL,
 PRIMARY KEY(catalog_id,performance_id,performance_revision,ordinal),UNIQUE(catalog_id,performance_id,performance_revision,consumption_id),
 FOREIGN KEY(catalog_id,performance_id,performance_revision) REFERENCES trading.gas_futures_performance_quantities(catalog_id,performance_id,revision),
 FOREIGN KEY(catalog_id,consumption_id,consumption_revision) REFERENCES trading.gas_owned_consumption_quantities(catalog_id,consumption_id,revision)
);
CREATE TABLE trading.gas_futures_quantity_sources(
 catalog_id uuid NOT NULL,performance_id uuid NOT NULL,performance_revision integer NOT NULL CHECK(performance_revision>1),
 account_id uuid NOT NULL,source_evidence_id uuid NOT NULL,previous_evidence_id uuid,
 PRIMARY KEY(catalog_id,performance_id,performance_revision,source_evidence_id),
 FOREIGN KEY(catalog_id,performance_id,performance_revision) REFERENCES trading.gas_futures_performance_quantities(catalog_id,performance_id,revision),
 FOREIGN KEY(catalog_id,account_id,source_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,account_id,previous_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id)
);
CREATE INDEX gas_owned_position_quantity_source ON trading.gas_owned_position_quantities(catalog_id,evidence_id,position_id,revision);
CREATE INDEX gas_owned_consumption_quantity_source ON trading.gas_owned_consumption_quantities(catalog_id,evidence_id,consumption_id,revision);

-- Version one is the existing accepted fact, with its original publication;
-- future origins create the same exact initial row in their own transaction.
INSERT INTO trading.gas_owned_position_quantities SELECT catalog_id,id,account_id,1,NULL,quantity,evidence_id,false,actor_id,publication_order FROM trading.gas_owned_positions;
INSERT INTO trading.gas_owned_consumption_quantities SELECT catalog_id,id,1,NULL,lot_id,quantity,evidence_id,reservation_id,false,actor_id,publication_order FROM trading.gas_owned_consumptions;
INSERT INTO trading.gas_futures_performance_quantities SELECT catalog_id,id,1,NULL,quantity,title_evidence_id,false,'initial_native_source_enrollment_v1',actor_id,publication_order FROM trading.gas_futures_delivery_performances;
INSERT INTO trading.gas_futures_performance_quantity_supplies SELECT s.catalog_id,s.performance_id,1,s.ordinal,s.consumption_id,1 FROM trading.gas_futures_delivery_splits s;

CREATE TABLE trading.gas_futures_principal_performance_heads(
 catalog_id uuid NOT NULL,determination_id uuid NOT NULL,position_id uuid NOT NULL,performance_id uuid NOT NULL,performance_revision integer NOT NULL,
 receipt_lot_id uuid,receipt_revision integer,
 PRIMARY KEY(catalog_id,determination_id),
 FOREIGN KEY(catalog_id,position_id,performance_id,determination_id)
  REFERENCES trading.gas_futures_delivery_principal_determinations(catalog_id,position_id,performance_id,id),
 FOREIGN KEY(catalog_id,performance_id,performance_revision) REFERENCES trading.gas_futures_performance_quantities(catalog_id,performance_id,revision),
 FOREIGN KEY(catalog_id,receipt_lot_id,receipt_revision) REFERENCES trading.gas_owned_position_quantities(catalog_id,position_id,revision),
 CHECK((receipt_lot_id IS NULL)=(receipt_revision IS NULL))
);
INSERT INTO trading.gas_futures_principal_performance_heads SELECT d.catalog_id,d.id,d.position_id,d.performance_id,1,p.receipt_lot_id,
 CASE WHEN p.receipt_lot_id IS NULL THEN NULL ELSE 1 END FROM trading.gas_futures_delivery_principal_determinations d
 JOIN trading.gas_futures_delivery_performances p ON(p.catalog_id,p.id)=(d.catalog_id,d.performance_id);
ALTER TABLE trading.gas_futures_principal_performance_heads OWNER TO openetrm_security_owner;
ALTER TABLE trading.gas_futures_principal_performance_heads ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_futures_principal_performance_heads FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON trading.gas_futures_principal_performance_heads TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.gas_futures_principal_performance_heads FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION trading.seed_gas_principal_performance() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE publication integer;selected integer;lot uuid;lot_revision integer;BEGIN
 SELECT r.publication_order INTO STRICT publication FROM trading.gas_futures_delivery_principal_runs r
  WHERE(r.catalog_id,r.position_id,r.revision)=(NEW.catalog_id,NEW.position_id,NEW.run_revision);
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(NEW.catalog_id,publication,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'futures_delivery_sealed';END IF;
 SELECT q.revision INTO STRICT selected FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(NEW.catalog_id,NEW.performance_id)
  AND q.publication_order<=publication ORDER BY q.revision DESC LIMIT 1;
 SELECT p.receipt_lot_id INTO STRICT lot FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.performance_id);
 IF lot IS NOT NULL THEN
  SELECT q.revision INTO STRICT lot_revision FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.position_id)=(NEW.catalog_id,lot)
   AND q.publication_order<=publication ORDER BY q.revision DESC LIMIT 1;
 END IF;
 INSERT INTO trading.gas_futures_principal_performance_heads VALUES(NEW.catalog_id,NEW.id,NEW.position_id,NEW.performance_id,selected,lot,lot_revision);RETURN NEW;
END$$;
ALTER FUNCTION trading.seed_gas_principal_performance() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.seed_gas_principal_performance() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE TRIGGER exact_performance_head AFTER INSERT ON trading.gas_futures_delivery_principal_determinations FOR EACH ROW EXECUTE FUNCTION trading.seed_gas_principal_performance();

CREATE FUNCTION trading.seed_gas_quantity_origin() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 CASE TG_TABLE_NAME
 WHEN 'gas_owned_positions' THEN INSERT INTO trading.gas_owned_position_quantities VALUES(NEW.catalog_id,NEW.id,NEW.account_id,1,NULL,NEW.quantity,NEW.evidence_id,false,NEW.actor_id,NEW.publication_order);
 WHEN 'gas_owned_consumptions' THEN INSERT INTO trading.gas_owned_consumption_quantities VALUES(NEW.catalog_id,NEW.id,1,NULL,NEW.lot_id,NEW.quantity,NEW.evidence_id,NEW.reservation_id,false,NEW.actor_id,NEW.publication_order);
 WHEN 'gas_futures_delivery_performances' THEN INSERT INTO trading.gas_futures_performance_quantities VALUES(NEW.catalog_id,NEW.id,1,NULL,NEW.quantity,NEW.title_evidence_id,false,'initial_native_source_enrollment_v1',NEW.actor_id,NEW.publication_order);
 WHEN 'gas_futures_delivery_splits' THEN INSERT INTO trading.gas_futures_performance_quantity_supplies VALUES(NEW.catalog_id,NEW.performance_id,1,NEW.ordinal,NEW.consumption_id,1);
 ELSE RAISE EXCEPTION 'gas_quantity_origin';END CASE;RETURN NEW;
END$$;
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_owned_positions','gas_owned_consumptions','gas_futures_delivery_performances','gas_futures_delivery_splits'] LOOP
  EXECUTE format('CREATE TRIGGER quantity_origin AFTER INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.seed_gas_quantity_origin()',name);
 END LOOP;
 FOREACH name IN ARRAY ARRAY['gas_owned_position_quantities','gas_owned_consumption_quantities','gas_futures_performance_quantities','gas_futures_performance_quantity_supplies','gas_futures_quantity_sources'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
 FOREACH name IN ARRAY ARRAY['gas_owned_position_quantities','gas_owned_consumption_quantities','gas_futures_performance_quantities'] LOOP
  EXECUTE format('CREATE TRIGGER native_publication BEFORE INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication()',name);
 END LOOP;
END$$;
ALTER FUNCTION trading.seed_gas_quantity_origin() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.seed_gas_quantity_origin() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION trading.gas_owned_positions_at(catalog uuid,context uuid,as_of timestamptz) RETURNS SETOF trading.gas_owned_positions
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT (jsonb_populate_record(p,jsonb_build_object('quantity',v.quantity,'evidence_id',v.evidence_id,'publication_order',v.publication_order))).*
 FROM trading.gas_owned_positions p CROSS JOIN LATERAL(SELECT x.* FROM trading.gas_owned_position_quantities x
  WHERE(x.catalog_id,x.position_id)=(p.catalog_id,p.id) AND trading.gas_owned_visible(catalog,x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
  ORDER BY x.revision DESC LIMIT 1)v WHERE p.catalog_id=catalog
$$;
CREATE FUNCTION trading.gas_owned_consumptions_at(catalog uuid,context uuid,as_of timestamptz) RETURNS SETOF trading.gas_owned_consumptions
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT (jsonb_populate_record(c,jsonb_build_object('quantity',v.quantity,'lot_id',v.lot_id,'evidence_id',v.evidence_id,'reservation_id',v.reservation_id,'publication_order',v.publication_order))).*
 FROM trading.gas_owned_consumptions c CROSS JOIN LATERAL(SELECT x.* FROM trading.gas_owned_consumption_quantities x
  WHERE(x.catalog_id,x.consumption_id)=(c.catalog_id,c.id) AND trading.gas_owned_visible(catalog,x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
  ORDER BY x.revision DESC LIMIT 1)v WHERE c.catalog_id=catalog
$$;
CREATE FUNCTION trading.gas_futures_performances_at(catalog uuid,context uuid,as_of timestamptz) RETURNS SETOF trading.gas_futures_delivery_performances
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT (jsonb_populate_record(p,jsonb_build_object('quantity',v.quantity,'title_evidence_id',v.title_evidence_id,'publication_order',v.publication_order))).*
 FROM trading.gas_futures_delivery_performances p CROSS JOIN LATERAL(SELECT x.* FROM trading.gas_futures_performance_quantities x
  WHERE(x.catalog_id,x.performance_id)=(p.catalog_id,p.id) AND trading.gas_owned_visible(catalog,x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
  ORDER BY x.revision DESC LIMIT 1)v WHERE p.catalog_id=catalog
$$;

CREATE FUNCTION security.read_gas_futures_reconciliation(catalog uuid,holding uuid,performance uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source jsonb;selected trading.gas_futures_delivery_performances;head trading.gas_futures_performance_quantities;
 result jsonb;supplies jsonb;all_visible boolean;expected integer;BEGIN
 -- The existing exact owner/profile reader captures the gas admission before
 -- its source waits and retains all adopted policy and reference proofs.
 source=settlement_data.read_futures_delivery_position(catalog,holding,context,as_of);
 PERFORM security.require_gas_owned_read(catalog,context,as_of,(source->>'authorityUntil')::timestamptz);
 SELECT * INTO STRICT selected FROM trading.gas_futures_performances_at(catalog,context,as_of) p WHERE(p.catalog_id,p.position_id,p.id)=(catalog,holding,performance);
 SELECT * INTO STRICT head FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id,q.publication_order)=(catalog,performance,selected.publication_order);
 SELECT count(*) INTO expected FROM trading.gas_futures_performance_quantity_supplies s WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(catalog,performance,head.revision);
 IF expected>16384 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 SELECT coalesce(jsonb_agg(to_jsonb(origin)||jsonb_build_object('consumption_id',q.consumption_id,'ordinal',s.ordinal,
  'financial_revision',q.revision,'retracted',q.retracted,'lot_id',q.lot_id,'evidence_id',q.evidence_id,'reservation_id',q.reservation_id,
  'quantity',trim_scale(q.quantity)::text,'publication_order',q.publication_order,'original_publication_order',origin.publication_order,
  'financial_actor_id',q.actor_id,'known_at',k.known_from) ORDER BY s.ordinal),'[]'),
  coalesce(bool_and(trading.gas_owned_visible(catalog,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)),true)
 INTO supplies,all_visible FROM trading.gas_futures_performance_quantity_supplies s
 JOIN trading.gas_owned_consumption_quantities q ON(q.catalog_id,q.consumption_id,q.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision)
 JOIN trading.gas_owned_consumptions origin ON(origin.catalog_id,origin.id)=(q.catalog_id,q.consumption_id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(q.catalog_id,q.publication_order)
 WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(catalog,performance,head.revision);
 IF NOT all_visible OR jsonb_array_length(supplies)<>expected THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 result=source||jsonb_build_object('reconciliationProfile','gas_futures_quantity_reconciliation_v1',
  'performance',to_jsonb(selected)||jsonb_build_object('quantity',trim_scale(selected.quantity)::text,'revision',head.revision,'retracted',head.retracted,'sourceAllocation',head.source_allocation),'supply',supplies);
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 -- The complete native result retains the delegated original read admission.
 PERFORM security.require_gas_owned_read(catalog,context,as_of,(source->>'authorityUntil')::timestamptz);
 RETURN result;
EXCEPTION WHEN no_data_found THEN
 IF source IS NOT NULL THEN PERFORM security.require_gas_owned_read(catalog,context,as_of,(source->>'authorityUntil')::timestamptz);END IF;
 RAISE EXCEPTION 'gas_quantity_source';
END$$;
ALTER FUNCTION security.read_gas_futures_reconciliation(uuid,uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_futures_reconciliation(uuid,uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_futures_reconciliation(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.gas_owned_positions_at(uuid,uuid,timestamptz)'::regprocedure,'trading.gas_owned_consumptions_at(uuid,uuid,timestamptz)'::regprocedure,
  'trading.gas_futures_performances_at(uuid,uuid,timestamptz)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$$;

INSERT INTO lifecycle.action_kinds(code,automatic,configuration_permission,execution_permission)
 VALUES('gas.futures.reconcile_quantity',false,'ConfigureGasOwnedHoldings','RecordGasOwnedHoldings');


-- Current-head views are confined to native writers and integrity seals.
CREATE VIEW trading.gas_owned_positions_current AS
 SELECT (jsonb_populate_record(p,jsonb_build_object('quantity',q.quantity,'evidence_id',q.evidence_id,'publication_order',q.publication_order))).*
 FROM trading.gas_owned_positions p CROSS JOIN LATERAL(SELECT x.* FROM trading.gas_owned_position_quantities x
  WHERE(x.catalog_id,x.position_id)=(p.catalog_id,p.id) ORDER BY x.revision DESC LIMIT 1)q;
CREATE VIEW trading.gas_owned_consumptions_current AS
 SELECT (jsonb_populate_record(c,jsonb_build_object('lot_id',q.lot_id,'quantity',q.quantity,'evidence_id',q.evidence_id,'reservation_id',q.reservation_id,'publication_order',q.publication_order))).*
 FROM trading.gas_owned_consumptions c CROSS JOIN LATERAL(SELECT x.* FROM trading.gas_owned_consumption_quantities x
  WHERE(x.catalog_id,x.consumption_id)=(c.catalog_id,c.id) ORDER BY x.revision DESC LIMIT 1)q;
CREATE VIEW trading.gas_futures_performances_current AS
 SELECT (jsonb_populate_record(p,jsonb_build_object('quantity',q.quantity,'title_evidence_id',q.title_evidence_id,'publication_order',q.publication_order))).*
 FROM trading.gas_futures_delivery_performances p CROSS JOIN LATERAL(SELECT x.* FROM trading.gas_futures_performance_quantities x
  WHERE(x.catalog_id,x.performance_id)=(p.catalog_id,p.id) ORDER BY x.revision DESC LIMIT 1)q;
CREATE FUNCTION trading.gas_futures_performances_through(catalog uuid,through_publication integer) RETURNS SETOF trading.gas_futures_delivery_performances
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT (jsonb_populate_record(p,jsonb_build_object('quantity',q.quantity,'title_evidence_id',q.title_evidence_id,'publication_order',q.publication_order))).*
 FROM trading.gas_futures_delivery_performances p CROSS JOIN LATERAL(SELECT x.* FROM trading.gas_futures_performance_quantities x
  WHERE(x.catalog_id,x.performance_id)=(p.catalog_id,p.id) AND x.publication_order<=through_publication ORDER BY x.revision DESC LIMIT 1)q WHERE p.catalog_id=catalog
$$;
CREATE VIEW trading.gas_futures_supplies_current AS
 SELECT s.catalog_id,s.performance_id,s.ordinal,s.consumption_id FROM trading.gas_futures_performance_quantity_supplies s
 WHERE s.performance_revision=(SELECT max(q.revision) FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(s.catalog_id,s.performance_id));
CREATE FUNCTION trading.gas_futures_supplies_at(catalog uuid,context uuid,as_of timestamptz) RETURNS SETOF trading.gas_futures_delivery_splits
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT s.catalog_id,s.performance_id,s.ordinal,s.consumption_id FROM trading.gas_futures_performance_quantity_supplies s
 WHERE s.catalog_id=catalog AND s.performance_revision=(SELECT q.revision FROM trading.gas_futures_performance_quantities q
  WHERE(q.catalog_id,q.performance_id)=(s.catalog_id,s.performance_id)
   AND trading.gas_owned_visible(catalog,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) ORDER BY q.revision DESC LIMIT 1)
$$;
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_owned_positions_current','gas_owned_consumptions_current','gas_futures_performances_current','gas_futures_supplies_current'] LOOP
  EXECUTE format('ALTER VIEW trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('REVOKE ALL ON trading.%I FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',name);
 END LOOP;
END$$;
ALTER FUNCTION trading.gas_futures_performances_through(uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_futures_performances_through(uuid,integer) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
ALTER FUNCTION trading.gas_futures_supplies_at(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_futures_supplies_at(uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION trading.gas_futures_quantity_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE item jsonb;supplies jsonb='[]';BEGIN
 PERFORM trading.gas_owned_projection_keys(body,ARRAY['positionId','performanceId','expectedRevision','expectedPrincipalVersion','quantity','retracted','titleEvidenceId','supply','sourceAllocation','evidence'],ARRAY['titleEvidenceId']);
 IF jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[1-9][0-9]{0,8}$'
  OR jsonb_typeof(body->'expectedPrincipalVersion') IS DISTINCT FROM 'number' OR body->>'expectedPrincipalVersion'!~'^[1-9][0-9]{0,8}$'
  OR jsonb_typeof(body->'retracted') IS DISTINCT FROM 'boolean' OR jsonb_typeof(body->'quantity') IS DISTINCT FROM 'string'
  OR jsonb_typeof(body->'supply') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'supply')>16384
  OR body->>'sourceAllocation' IS NULL OR body->>'sourceAllocation' NOT IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1')
  OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 2000
  OR trading.gas_owned_quantity(body->>'quantity')<0 OR(body->>'retracted')::boolean IS DISTINCT FROM(trading.gas_owned_quantity(body->>'quantity')=0)
 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'supply') LOOP
  PERFORM trading.gas_owned_projection_keys(item,ARRAY['consumptionId','expectedRevision','lotId','evidenceId','reservationId','quantity','retracted'],ARRAY['consumptionId','reservationId']);
  IF jsonb_typeof(item->'expectedRevision') IS DISTINCT FROM 'number' OR item->>'expectedRevision'!~'^(0|[1-9][0-9]{0,8})$'
   OR jsonb_typeof(item->'retracted') IS DISTINCT FROM 'boolean' OR jsonb_typeof(item->'quantity') IS DISTINCT FROM 'string'
   OR trading.gas_owned_quantity(item->>'quantity')<0 OR(item->>'retracted')::boolean IS DISTINCT FROM(trading.gas_owned_quantity(item->>'quantity')=0)
   OR(item->>'consumptionId' IS NULL) IS DISTINCT FROM((item->>'expectedRevision')::integer=0)
   OR item->>'consumptionId' IS NULL AND(item->>'retracted')::boolean
  THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
  supplies=supplies||jsonb_build_array(item||jsonb_build_object('quantity',trim_scale(trading.gas_owned_quantity(item->>'quantity'))::text));
 END LOOP;
 IF(SELECT count(DISTINCT(value->>'consumptionId')::uuid) FROM jsonb_array_elements(supplies) WHERE value->>'consumptionId' IS NOT NULL)<>
  (SELECT count(*) FROM jsonb_array_elements(supplies) WHERE value->>'consumptionId' IS NOT NULL) THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 RETURN body||jsonb_build_object('quantity',trim_scale(trading.gas_owned_quantity(body->>'quantity'))::text,'supply',supplies);
END$$;

CREATE FUNCTION trading.gas_futures_quantity_business_fact(body jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT(body-'expectedRevision'-'expectedPrincipalVersion')||jsonb_build_object('supply',coalesce((SELECT jsonb_agg(value-'expectedRevision' ORDER BY ordinal)
  FROM jsonb_array_elements(body->'supply') WITH ORDINALITY s(value,ordinal)),'[]'))
$$;

CREATE FUNCTION trading.require_gas_quantity_references(catalog uuid,account uuid,body jsonb) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE item jsonb;selected_account trading.gas_owned_accounts;BEGIN
 IF body->>'titleEvidenceId' IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id,e.account_id)=(catalog,(body->>'titleEvidenceId')::uuid,account))
 THEN RAISE EXCEPTION 'gas_quantity_evidence';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'supply') LOOP
  SELECT x.* INTO STRICT selected_account FROM trading.gas_owned_accounts x JOIN trading.gas_owned_positions stock ON(stock.catalog_id,stock.account_id)=(x.catalog_id,x.id)
   JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.account_id)=(x.catalog_id,x.id)
   WHERE(stock.catalog_id,stock.id,e.id)=(catalog,(item->>'lotId')::uuid,(item->>'evidenceId')::uuid);
  IF security.reference_version_visible(selected_account.reference_catalog_id,selected_account.reference_revision_id) IS NOT TRUE
   OR security.market_fact_visible(catalog,selected_account.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 END LOOP;
END$$;

CREATE FUNCTION trading.require_gas_quantity_snapshot_references(catalog uuid,performance uuid,version integer) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE account trading.gas_owned_accounts;BEGIN
 FOR account IN SELECT DISTINCT a.* FROM trading.gas_owned_accounts a WHERE a.catalog_id=catalog AND(a.id IN(
  SELECT s.account_id FROM trading.gas_futures_quantity_sources s WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(catalog,performance,version))
  OR a.id IN(SELECT pstock.account_id FROM trading.gas_futures_delivery_performances p JOIN trading.gas_owned_positions pstock ON(pstock.catalog_id,pstock.id)=(p.catalog_id,p.receipt_lot_id)
   WHERE(p.catalog_id,p.id)=(catalog,performance))
  OR a.id IN(SELECT pstock.account_id FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_owned_consumption_quantities q
   ON(q.catalog_id,q.consumption_id,q.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision)
   JOIN trading.gas_owned_positions pstock ON(pstock.catalog_id,pstock.id)=(q.catalog_id,q.lot_id)
   WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(catalog,performance,version))) LOOP
  IF security.reference_version_visible(account.reference_catalog_id,account.reference_revision_id) IS NOT TRUE OR security.market_fact_visible(catalog,account.definition_revision_id,NULL) IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 END LOOP;
END$$;

CREATE FUNCTION trading.require_gas_quantity_funding(catalog uuid,performance uuid,body jsonb,context uuid,as_of timestamptz) RETURNS void
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
  -- This complete amendment explicitly replaces or retires its own accepted
  -- stock uses. Other pending title/funding/dispatch facts remain a refusal.
  IF conflict->>'kind'='consumption' AND EXISTS(SELECT 1 FROM trading.gas_futures_supplies_at(catalog,context,as_of) s
   WHERE(s.catalog_id,s.performance_id,s.consumption_id)=(catalog,performance,(conflict->>'effectId')::uuid)) THEN CONTINUE;END IF;
  RAISE EXCEPTION 'gas_owned_reconciliation';
 END LOOP;
END$$;

-- Match retained native identifiers first. New-row placeholders may identify
-- an existing business fact only through an exact multiset of remaining terms.
CREATE FUNCTION trading.gas_futures_quantity_supplies_match(catalog uuid,performance uuid,version integer,requested jsonb) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 WITH accepted AS(
  SELECT q.consumption_id id,jsonb_build_array(q.lot_id,q.evidence_id,q.reservation_id,q.quantity,q.retracted) terms
  FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_owned_consumption_quantities q
   ON(q.catalog_id,q.consumption_id,q.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision)
  WHERE(s.catalog_id,s.performance_id,s.performance_revision)=($1,$2,$3)
 ),chosen AS(
  SELECT(value->>'consumptionId')::uuid id,jsonb_build_array((value->>'lotId')::uuid,(value->>'evidenceId')::uuid,
   (value->>'reservationId')::uuid,trading.gas_owned_quantity(value->>'quantity'),(value->>'retracted')::boolean) terms
  FROM jsonb_array_elements($4)
 ),remaining AS(
  SELECT a.terms FROM accepted a WHERE NOT EXISTS(SELECT 1 FROM chosen c WHERE c.id=a.id)
 ),placeholders AS(SELECT terms FROM chosen WHERE id IS NULL)
 SELECT(SELECT count(*) FROM accepted)=(SELECT count(*) FROM chosen)
  AND(SELECT count(id)=count(DISTINCT id) FROM chosen)
  AND NOT EXISTS(SELECT 1 FROM chosen c WHERE c.id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM accepted a WHERE(a.id,a.terms)=(c.id,c.terms)))
  AND NOT EXISTS((SELECT terms FROM remaining) EXCEPT ALL(SELECT terms FROM placeholders))
  AND NOT EXISTS((SELECT terms FROM placeholders) EXCEPT ALL(SELECT terms FROM remaining))
$$;
ALTER FUNCTION trading.gas_futures_quantity_supplies_match(uuid,uuid,integer,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_futures_quantity_supplies_match(uuid,uuid,integer,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION security.reconcile_gas_futures_delivery(catalog uuid,operation uuid,holding uuid,body jsonb,as_of timestamptz,context uuid,
 policy uuid,policy_revision integer,receipt_only boolean,continuation jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;canonical jsonb;prior trading.gas_owned_commands;same_fact trading.gas_owned_commands;
 link settlement_data.futures_delivery_obligations;profile settlement_data.futures_delivery_profiles;terms trading.gas_futures_delivery_terms;
 a trading.gas_owned_accounts;p trading.gas_owned_positions;performance trading.gas_futures_delivery_performances;
 current_head trading.gas_futures_performance_quantities;stock_head trading.gas_owned_position_quantities;
 current_use trading.gas_owned_consumption_quantities;selected_stock trading.gas_owned_positions;selected_account trading.gas_owned_accounts;
 evidence trading.gas_owned_evidence;binding trading.gas_owned_action_bindings;principal integer;revision integer;publication integer;outcome jsonb;item jsonb;identifier uuid;ordinal integer=0;
 final record;total numeric=0;acknowledged_revision integer;acknowledged_principal integer;use_revision integer;allocation_facts jsonb;BEGIN
 admission=security.gas_owned_admission(catalog,'futures_quantity_reconciliation',body,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR holding IS NULL OR context IS NULL
  OR policy IS NULL OR policy_revision IS NULL OR policy_revision<1 OR receipt_only IS NULL OR as_of IS NULL OR NOT isfinite(as_of)
  OR jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>8388608 THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 body=trading.gas_futures_quantity_fact(body);
 IF(body->>'positionId')::uuid IS DISTINCT FROM holding OR(body->>'performanceId')::uuid IS NULL THEN RAISE EXCEPTION 'gas_quantity_input';END IF;
 IF continuation='null'::jsonb THEN continuation=NULL;END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(catalog,holding);
 SELECT * INTO STRICT profile FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.revision)=(catalog,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT terms FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(catalog,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,terms.account_id);
 PERFORM settlement_data.require_futures_delivery_reference(profile,terms);
 PERFORM trading.require_gas_quantity_references(catalog,a.id,body);
 canonical=jsonb_build_object('action','futures_quantity_reconciliation','accountId',a.id,'body',body,'asOf',as_of,'contextId',context,'policyId',policy,'policyRevision',policy_revision,'automatic',false,'continuation',continuation);
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR(CASE WHEN receipt_only THEN prior.request-'continuation' IS DISTINCT FROM canonical-'continuation' ELSE prior.request IS DISTINCT FROM canonical END)
  THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  PERFORM trading.require_gas_quantity_snapshot_references(catalog,(body->>'performanceId')::uuid,(prior.outcome->>'acknowledgedRevision')::integer);
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object('outcome',prior.outcome,'continuation',prior.request->'continuation');END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO STRICT performance FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.position_id,x.id)=(catalog,holding,(body->>'performanceId')::uuid);
 SELECT * INTO STRICT current_head FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(catalog,performance.id) ORDER BY q.revision DESC LIMIT 1;
 SELECT coalesce(max(r.revision),0) INTO principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,holding);
 SELECT * INTO same_fact FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.account_id)=(catalog,a.id) AND c.request->>'action'='futures_quantity_reconciliation'
  AND trading.gas_futures_quantity_business_fact(c.request->'body')=trading.gas_futures_quantity_business_fact(body) ORDER BY(c.outcome->>'acknowledgedRevision')::integer LIMIT 1;
 SELECT q.revision INTO acknowledged_revision FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id,q.quantity,q.retracted)=(catalog,performance.id,trading.gas_owned_quantity(body->>'quantity'),(body->>'retracted')::boolean)
  AND q.title_evidence_id IS NOT DISTINCT FROM(body->>'titleEvidenceId')::uuid
  -- Initial enrollment used the same bounded capacity without an amendment;
  -- Preserve acknowledges that initial fact without changing its saved policy.
  AND(q.source_allocation=body->>'sourceAllocation' OR q.revision=1 AND body->>'sourceAllocation'='preserve_unallocated_source_quantity_v1')
  AND trading.gas_futures_quantity_supplies_match(catalog,performance.id,q.revision,body->'supply')
  ORDER BY q.revision LIMIT 1;
 IF same_fact.request_id IS NOT NULL THEN
  outcome=same_fact.outcome||jsonb_build_object('operationId',operation,'changed',false,'semanticAcknowledgement',true,'currentRevision',current_head.revision,'currentPrincipalVersion',principal);
 ELSIF acknowledged_revision IS NOT NULL THEN
  SELECT min(r.revision) INTO acknowledged_principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,holding)
   AND acknowledged_revision=(SELECT q.revision FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id)=(catalog,performance.id)
    AND q.publication_order<=r.publication_order ORDER BY q.revision DESC LIMIT 1);
  outcome=jsonb_build_object('operationId',operation,'positionId',holding,'id',performance.id,'action','futures_quantity_reconciliation','revision',acknowledged_revision,
   'acknowledgedRevision',acknowledged_revision,'currentRevision',current_head.revision,'principalVersion',acknowledged_principal,'currentPrincipalVersion',principal,'changed',false,'semanticAcknowledgement',true);
 ELSE
  IF receipt_only THEN PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);RETURN NULL;END IF;
  PERFORM trading.require_gas_futures_delivery_source(catalog,holding,context,as_of);
  IF trading.gas_owned_visible(catalog,current_head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR current_head.revision<>(body->>'expectedRevision')::integer OR principal<>(body->>'expectedPrincipalVersion')::integer THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
  SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,a.id,'gas.futures.reconcile_quantity') ORDER BY b.revision DESC LIMIT 1;
  IF(binding.policy_id,binding.policy_revision) IS DISTINCT FROM(policy,policy_revision) THEN RAISE EXCEPTION 'gas_owned_policy_changed';END IF;
  PERFORM lifecycle.require_policy(catalog,policy,policy_revision,'gas.futures.reconcile_quantity',as_of,false);
  SELECT * INTO STRICT p FROM trading.gas_owned_positions_current x WHERE(x.catalog_id,x.id)=(catalog,holding);
  SELECT * INTO STRICT final FROM settlement_data.read_futures_delivery_final(catalog,holding,context,as_of);
  IF performance.performed_at>as_of THEN RAISE EXCEPTION 'gas_quantity_time';END IF;
  revision=current_head.revision+1;
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  -- Every supplied stock and source must be admitted before this publication.
  IF p.quantity>0 THEN
   IF body->>'titleEvidenceId' IS NULL OR jsonb_array_length(body->'supply')<>0 THEN RAISE EXCEPTION 'gas_quantity_title';END IF;
   SELECT * INTO STRICT evidence FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(body->>'titleEvidenceId')::uuid,a.id);
   IF evidence.kind<>'title_receipt' OR evidence.occurred_at<>performance.performed_at OR evidence.quantity<trading.gas_owned_quantity(body->>'quantity')
    OR NOT trading.gas_owned_visible(catalog,evidence.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
    OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.account_id,x.source_key)=(catalog,evidence.account_id,evidence.source_key) AND x.revision>evidence.revision)
    OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_evidence original WHERE(original.catalog_id,original.id,original.account_id,original.source_key)=(catalog,performance.title_evidence_id,evidence.account_id,evidence.source_key))
   THEN RAISE EXCEPTION 'gas_quantity_evidence';END IF;
   SELECT * INTO STRICT stock_head FROM trading.gas_owned_position_quantities x WHERE(x.catalog_id,x.position_id)=(catalog,performance.receipt_lot_id) ORDER BY x.revision DESC LIMIT 1;
   IF NOT trading.gas_owned_visible(catalog,stock_head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
  ELSE
   IF body->>'titleEvidenceId' IS NOT NULL OR jsonb_array_length(body->'supply')=0 THEN RAISE EXCEPTION 'gas_quantity_supply';END IF;
   FOR item IN SELECT value FROM jsonb_array_elements(body->'supply') LOOP
    SELECT * INTO STRICT selected_stock FROM trading.gas_owned_positions_current x WHERE(x.catalog_id,x.id,x.kind)=(catalog,(item->>'lotId')::uuid,'owned_stock');
    SELECT * INTO STRICT selected_account FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,selected_stock.account_id);
    PERFORM trading.require_gas_owned_head(catalog,selected_stock.account_id,context,as_of);
    PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,selected_account.definition_revision_id);
    SELECT * INTO STRICT evidence FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(item->>'evidenceId')::uuid,selected_stock.account_id);
    IF evidence.kind<>'physical_delivery' OR evidence.occurred_at<>performance.performed_at OR evidence.quantity<trading.gas_owned_quantity(item->>'quantity')
     OR NOT trading.gas_owned_visible(catalog,evidence.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
     OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.account_id,x.source_key)=(catalog,evidence.account_id,evidence.source_key) AND x.revision>evidence.revision)
     OR(selected_account.book_id,selected_account.legal_entity_id,selected_account.legal_entity_revision,selected_account.reference_catalog_id,selected_account.reference_revision_id,selected_account.quantity_code,selected_account.location_code,selected_account.currency_code)
      IS DISTINCT FROM(a.book_id,a.legal_entity_id,a.legal_entity_revision,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.location_code,a.currency_code)
    THEN RAISE EXCEPTION 'gas_quantity_supply';END IF;
    IF item->>'consumptionId' IS NOT NULL THEN
     SELECT * INTO STRICT current_use FROM trading.gas_owned_consumption_quantities x WHERE(x.catalog_id,x.consumption_id)=(catalog,(item->>'consumptionId')::uuid) ORDER BY x.revision DESC LIMIT 1;
     IF current_use.revision<>(item->>'expectedRevision')::integer OR NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s
       WHERE(s.catalog_id,s.performance_id,s.performance_revision,s.consumption_id,s.consumption_revision)=(catalog,performance.id,current_head.revision,current_use.consumption_id,current_use.revision))
      OR NOT trading.gas_owned_visible(catalog,current_use.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
     THEN RAISE EXCEPTION 'gas_quantity_supply';END IF;
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
    WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(catalog,performance.id,current_head.revision)
     AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(body->'supply') chosen WHERE(chosen->>'consumptionId')::uuid=s.consumption_id))
   THEN RAISE EXCEPTION 'gas_quantity_supply';END IF;
   PERFORM trading.require_gas_quantity_funding(catalog,performance.id,body,context,as_of);
  END IF;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_futures_performance_quantities VALUES(catalog,performance.id,revision,current_head.revision,trading.gas_owned_quantity(body->>'quantity'),(body->>'titleEvidenceId')::uuid,(body->>'retracted')::boolean,body->>'sourceAllocation',actor,publication);
  IF p.quantity>0 THEN
   INSERT INTO trading.gas_owned_position_quantities VALUES(catalog,performance.receipt_lot_id,a.id,stock_head.revision+1,stock_head.revision,
    trading.gas_owned_quantity(body->>'quantity'),(body->>'titleEvidenceId')::uuid,(body->>'retracted')::boolean,actor,publication);
  ELSE
   FOR item IN SELECT value FROM jsonb_array_elements(body->'supply') LOOP
    ordinal=ordinal+1;
    IF item->>'consumptionId' IS NULL THEN
     identifier=gen_random_uuid();
     INSERT INTO trading.gas_owned_consumptions VALUES(catalog,identifier,(item->>'lotId')::uuid,'delivery',trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,actor,publication,performance.performed_at);
     INSERT INTO trading.gas_futures_performance_quantity_supplies VALUES(catalog,performance.id,revision,ordinal,identifier,1);
    ELSE
     identifier=(item->>'consumptionId')::uuid;
     SELECT * INTO STRICT current_use FROM trading.gas_owned_consumption_quantities q WHERE(q.catalog_id,q.consumption_id,q.revision)=(catalog,identifier,(item->>'expectedRevision')::integer);
     use_revision=current_use.revision;
     IF(current_use.lot_id,current_use.quantity,current_use.evidence_id,current_use.reservation_id,current_use.retracted) IS DISTINCT FROM
       ((item->>'lotId')::uuid,trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,(item->>'retracted')::boolean) THEN
      use_revision=use_revision+1;
      INSERT INTO trading.gas_owned_consumption_quantities VALUES(catalog,identifier,use_revision,current_use.revision,
       (item->>'lotId')::uuid,trading.gas_owned_quantity(item->>'quantity'),(item->>'evidenceId')::uuid,(item->>'reservationId')::uuid,(item->>'retracted')::boolean,actor,publication);
     END IF;
     INSERT INTO trading.gas_futures_performance_quantity_supplies VALUES(catalog,performance.id,revision,ordinal,identifier,use_revision);
    END IF;
   END LOOP;
  END IF;
  principal=trading.apply_futures_delivery_principal(catalog,holding,final.determination_id,context,as_of,actor,publication,continuation);
  allocation_facts=trading.gas_futures_allocation_facts(catalog,performance.id,revision);
  PERFORM trading.require_gas_source_allocation(body->>'sourceAllocation',allocation_facts);
  FOR item IN SELECT value FROM jsonb_array_elements(allocation_facts) LOOP
   INSERT INTO trading.gas_futures_quantity_sources VALUES(catalog,performance.id,revision,(item->>'accountId')::uuid,(item->>'sourceEvidenceId')::uuid,(item->>'oldEvidenceId')::uuid);
  END LOOP;
  outcome=jsonb_build_object('operationId',operation,'positionId',holding,'id',performance.id,'action','futures_quantity_reconciliation','revision',revision,
   'acknowledgedRevision',revision,'currentRevision',revision,'principalVersion',principal,'currentPrincipalVersion',principal,'changed',true,'semanticAcknowledgement',false,
   'sourceAllocation',body->>'sourceAllocation','sourceAllocations',allocation_facts);
 END IF;
 PERFORM trading.require_gas_quantity_snapshot_references(catalog,performance.id,(outcome->>'acknowledgedRevision')::integer);
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,a.id,canonical,outcome,(admission->>'until')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object('outcome',outcome,'continuation',continuation);END IF;RETURN outcome;
END$$;
DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.gas_futures_quantity_fact(jsonb)'::regprocedure,'trading.gas_futures_quantity_business_fact(jsonb)'::regprocedure,'trading.require_gas_quantity_references(uuid,uuid,jsonb)'::regprocedure,
  'trading.require_gas_quantity_funding(uuid,uuid,jsonb,uuid,timestamptz)'::regprocedure,'trading.require_gas_quantity_snapshot_references(uuid,uuid,integer)'::regprocedure,
  'security.reconcile_gas_futures_delivery(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.reconcile_gas_futures_delivery(uuid,uuid,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb) TO openetrm_runtime;

CREATE FUNCTION security.read_gas_futures_reconciliations(catalog uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE state jsonb;rows jsonb;BEGIN
 state=security.read_gas_owned_operations(read_gas_futures_reconciliations.catalog,read_gas_futures_reconciliations.context,read_gas_futures_reconciliations.as_of,NULL);
 SELECT coalesce(jsonb_agg(jsonb_build_object('positionId',p.position_id,'performanceId',p.id,'reference',p.business_reference,
  'performedAt',p.performed_at,'quantity',trim_scale(p.quantity)::text,'financialRevision',q.revision,'retracted',q.retracted,
  'principalVersion',principal.revision,'accountId',a.id,'account',a.code,'bookId',a.book_id,'book',book.name,
  'policyId',binding.policy_id,'policyRevision',binding.policy_revision,'policyFrom',policy.from_at,'policyUntil',policy.until_at,'policyMode',policy.mode)
  ORDER BY p.performed_at,convert_to(p.business_reference,'UTF8'),p.id),'[]') INTO rows
 FROM trading.gas_futures_performances_at(read_gas_futures_reconciliations.catalog,read_gas_futures_reconciliations.context,read_gas_futures_reconciliations.as_of) p
 JOIN trading.gas_owned_positions parent ON(parent.catalog_id,parent.id)=(p.catalog_id,p.position_id)
 JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(parent.catalog_id,parent.account_id)
 JOIN trading.books book ON(book.catalog_id,book.id)=(a.catalog_id,a.book_id)
 JOIN trading.gas_futures_performance_quantities q ON(q.catalog_id,q.performance_id,q.publication_order)=(p.catalog_id,p.id,p.publication_order)
 CROSS JOIN LATERAL(SELECT r.revision FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(read_gas_futures_reconciliations.catalog,p.position_id)
  AND trading.gas_owned_visible(read_gas_futures_reconciliations.catalog,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',read_gas_futures_reconciliations.context,read_gas_futures_reconciliations.as_of) ORDER BY r.revision DESC LIMIT 1)principal
 LEFT JOIN LATERAL(SELECT b.* FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(read_gas_futures_reconciliations.catalog,a.id,'gas.futures.reconcile_quantity')
  AND trading.gas_owned_visible(read_gas_futures_reconciliations.catalog,b.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',read_gas_futures_reconciliations.context,read_gas_futures_reconciliations.as_of) ORDER BY b.revision DESC LIMIT 1)binding ON true
 LEFT JOIN lifecycle.action_policies policy ON(policy.catalog_id,policy.id,policy.revision)=(binding.catalog_id,binding.policy_id,binding.policy_revision)
 WHERE p.performed_at<=read_gas_futures_reconciliations.as_of AND EXISTS(SELECT 1 FROM jsonb_array_elements(state->'accounts') selected WHERE(selected->>'id')::uuid=a.id);
 IF jsonb_array_length(rows)>16384 OR pg_column_size(rows)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(read_gas_futures_reconciliations.catalog,read_gas_futures_reconciliations.context,read_gas_futures_reconciliations.as_of,(state->>'authorityUntil')::timestamptz);
 RETURN jsonb_build_object('contextId',read_gas_futures_reconciliations.context,'asOf',read_gas_futures_reconciliations.as_of,'authorityUntil',state->'authorityUntil','corrections',rows);
END$$;
ALTER FUNCTION security.read_gas_futures_reconciliations(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_futures_reconciliations(uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_futures_reconciliations(uuid,uuid,timestamptz) TO openetrm_runtime;

CREATE FUNCTION trading.gas_quantity_source_result(catalog uuid,context uuid,as_of timestamptz,result jsonb) RETURNS jsonb
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE item jsonb;rows jsonb;position_head trading.gas_owned_position_quantities;consumption_head trading.gas_owned_consumption_quantities;origin integer;BEGIN
 rows='[]';
 FOR item IN SELECT value FROM jsonb_array_elements(result->'positions') LOOP
  SELECT * INTO STRICT position_head FROM trading.gas_owned_position_quantities q
   WHERE(q.catalog_id,q.position_id,q.publication_order)=(catalog,(item->>'id')::uuid,(item->>'publication_order')::integer);
  IF NOT trading.gas_owned_visible(catalog,position_head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
   OR position_head.quantity IS DISTINCT FROM(item->>'quantity')::numeric OR position_head.evidence_id IS DISTINCT FROM(item->>'evidence_id')::uuid
  THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  SELECT p.publication_order INTO STRICT origin FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(catalog,position_head.position_id);
  rows=rows||jsonb_build_array(item||jsonb_build_object('financial_revision',position_head.revision,'retracted',position_head.retracted,'original_publication_order',origin));
 END LOOP;
 result=result||jsonb_build_object('positions',rows,'quantityStateProfile','gas_owned_financial_quantities_v1');
 IF result?'consumptions' THEN
  rows='[]';
  FOR item IN SELECT value FROM jsonb_array_elements(result->'consumptions') LOOP
   SELECT * INTO STRICT consumption_head FROM trading.gas_owned_consumption_quantities q
    WHERE(q.catalog_id,q.consumption_id,q.publication_order)=(catalog,(item->>'id')::uuid,(item->>'publication_order')::integer);
   IF NOT trading.gas_owned_visible(catalog,consumption_head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
    OR(consumption_head.quantity,consumption_head.lot_id,consumption_head.evidence_id) IS DISTINCT FROM
     ((item->>'quantity')::numeric,(item->>'lot_id')::uuid,(item->>'evidence_id')::uuid)
    OR consumption_head.reservation_id IS DISTINCT FROM(item->>'reservation_id')::uuid THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
   SELECT c.publication_order INTO STRICT origin FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.id)=(catalog,consumption_head.consumption_id);
   rows=rows||jsonb_build_array(item||jsonb_build_object('financial_revision',consumption_head.revision,'retracted',consumption_head.retracted,'original_publication_order',origin));
  END LOOP;
  result=jsonb_set(result,ARRAY['consumptions'],rows);
 END IF;
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;RETURN result;
END$$;

CREATE FUNCTION trading.gas_futures_quantity_source(catalog uuid,context uuid,as_of timestamptz,rows jsonb) RETURNS jsonb
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE item jsonb;result jsonb='[]';head trading.gas_futures_performance_quantities;BEGIN
 FOR item IN SELECT value FROM jsonb_array_elements(rows) LOOP
  SELECT * INTO STRICT head FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id,q.publication_order)=
   (catalog,(item->>'id')::uuid,(item->>'publication_order')::integer);
  IF NOT trading.gas_owned_visible(catalog,head.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of)
   OR head.quantity IS DISTINCT FROM(item->>'quantity')::numeric OR head.title_evidence_id IS DISTINCT FROM(item->>'title_evidence_id')::uuid THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  result=result||jsonb_build_array(item||jsonb_build_object('financial_revision',head.revision,'retracted',head.retracted,'source_allocation',head.source_allocation));
 END LOOP;RETURN result;
END$$;

CREATE FUNCTION trading.gas_physical_quantity_source_result(catalog uuid,context uuid,as_of timestamptz,result jsonb) RETURNS jsonb
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE item jsonb;rows jsonb='[]';step jsonb;steps jsonb;selected_revision integer;BEGIN
 FOR item IN SELECT value FROM jsonb_array_elements(result->'attachments') LOOP
  steps='[]';
  FOR step IN SELECT value FROM jsonb_array_elements(item->'steps') LOOP
   SELECT h.performance_revision INTO STRICT selected_revision FROM trading.gas_futures_principal_performance_heads h
    WHERE(h.catalog_id,h.determination_id,h.performance_id)=(catalog,(step->>'id')::uuid,(step->>'performance_id')::uuid);
   steps=steps||jsonb_build_array(step||jsonb_build_object('performance_revision',selected_revision));
  END LOOP;
  item=jsonb_set(item,ARRAY['performances'],trading.gas_futures_quantity_source(catalog,context,as_of,item->'performances'));
  rows=rows||jsonb_build_array(jsonb_set(item,ARRAY['steps'],steps));
 END LOOP;
 RETURN result||jsonb_build_object('attachments',rows,'quantityStateProfile','gas_owned_financial_quantities_v1');
END$$;
DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.gas_quantity_source_result(uuid,uuid,timestamptz,jsonb)'::regprocedure,
  'trading.gas_futures_quantity_source(uuid,uuid,timestamptz,jsonb)'::regprocedure,'trading.gas_physical_quantity_source_result(uuid,uuid,timestamptz,jsonb)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',f);
 END LOOP;
END$$;

-- Exact predecessor qualification before replacing only financial read sources.
CREATE FUNCTION trading.require_gas_owned_quantity_head(catalog uuid,account uuid,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM(
  SELECT q.publication_order FROM trading.gas_owned_position_quantities q WHERE(q.catalog_id,q.account_id)=(catalog,account) AND q.revision>1
  UNION ALL SELECT q.publication_order FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(q.catalog_id,q.lot_id)
   WHERE(l.catalog_id,l.account_id)=(catalog,account) AND q.revision>1
  UNION ALL SELECT q.publication_order FROM trading.gas_futures_performance_quantities q JOIN trading.gas_futures_delivery_performances f ON(f.catalog_id,f.id)=(q.catalog_id,q.performance_id)
   JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account) AND q.revision>1
 )q WHERE trading.gas_owned_visible(catalog,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE)
 THEN RAISE EXCEPTION 'gas_quantity_revision';END IF;
END$$;
ALTER FUNCTION trading.require_gas_owned_quantity_head(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_owned_quantity_head(uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $upgrade$DECLARE spec record;p pg_proc;definition text;table_name text;replacement text;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('security.read_gas_owned_holdings_original(uuid,uuid,timestamptz,uuid[])','609c52796c9c74f841a0ca2847dd1012','source','at_time','jsonb','plpgsql',true,'v',false,false,ARRAY['catalog','context','at_time','books'],NULL),
  ('security.read_gas_owned_holdings_pre_risk(uuid,uuid,timestamptz,uuid[])','ecfcf04d8377d077db490a2729b38331','source','at_time','jsonb','plpgsql',true,'v',false,false,ARRAY['catalog','context','at_time','books'],NULL),
  ('security.read_gas_owned_operations(uuid,uuid,timestamptz,uuid)','d69d0be6a98f5723fbd045439efd9ac3','source','as_of','jsonb','plpgsql',true,'v',true,false,ARRAY['catalog','context','as_of','account'],NULL),
  ('security.read_gas_owned_known_history_pre_risk(uuid,uuid,timestamptz,uuid[])','0bd19cf50ad3026865838f7ad251508c','source','at_time','jsonb','plpgsql',true,'v',false,false,ARRAY['catalog','context','at_time','books'],NULL),
  ('security.gas_physical_journal_source(uuid,uuid,timestamptz,uuid[],uuid[])','ffa77950859db19668055899dacaf232','source','at_time','jsonb','plpgsql',true,'v',false,false,ARRAY['catalog','context','at_time','books','selected_positions'],NULL),
  ('security.read_gas_physical_known_attachments(uuid,uuid,timestamptz,uuid[],jsonb)','7f8f6af561734e952e4809e767016a08','source','at_time','jsonb','plpgsql',true,'v',true,true,ARRAY['catalog','context','at_time','books','parents'],'NULL::jsonb'),
  ('trading.gas_owned_applied_source_conflicts(uuid,uuid,timestamptz,uuid[],uuid[])','1335505374e2a870c49bc9808d5e8813','source','at_time','jsonb','sql',true,'v',false,false,ARRAY['catalog','context','at_time','books','positions'],'NULL::uuid[]'),
  ('settlement_data.read_futures_delivery_principal(uuid,uuid,uuid,uuid,integer,uuid,timestamptz)','5f8d98c72fc05ea3f1c339a7c9bf6048','source','as_of','jsonb','plpgsql',true,'v',true,false,ARRAY['catalog','trade','binding','profile','profile_revision','context','as_of'],NULL),
  ('trading.check_gas_owned_integrity()','846dba9ebad28c7934a2f6f541c1ddbf','current',NULL,'trigger','plpgsql',true,'v',false,false,NULL,NULL),
  ('trading.apply_gas_owned_financial(uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer)','ce1faf9b2a5ec75c4a22c3e7f40c8381','current',NULL,'jsonb','plpgsql',false,'v',false,false,ARRAY['catalog','account','action','body','as_of','context','actor','publication'],NULL),
  ('security.record_gas_futures_delivery(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)','87599f1003dfd6fac836a84e8d1bebe9','current',NULL,'jsonb','plpgsql',true,'v',true,false,ARRAY['catalog','operation','holding','action','body','as_of','context','policy','policy_revision','receipt_only','continuation'],NULL),
  ('trading.apply_futures_delivery_principal(uuid,uuid,uuid,uuid,timestamptz,uuid,integer,jsonb)','8e9e99349729750799f489709cae98aa','current',NULL,'integer','plpgsql',false,'v',false,false,ARRAY['catalog','holding','final_determination','context','as_of','actor','publication','proposal'],NULL),
  ('trading.require_gas_owned_financial_sources(uuid,uuid,text,jsonb,uuid,timestamptz,integer)','5b4d02361d0d038838983654d549ded7','current',NULL,'void','plpgsql',true,'v',false,false,ARRAY['catalog','account','action','body','context','as_of','own_publication'],NULL),
  ('trading.require_gas_owned_head(uuid,uuid,uuid,timestamptz)','ac7fb8c4b9f5f9901f795331bd336371','current',NULL,'void','plpgsql',false,'s',false,false,ARRAY['catalog','account','context','as_of'],NULL),
  ('trading.gas_owned_financial_ancestors(uuid,uuid[],uuid,timestamptz)','cdc27dfa95d0b14603597acd9a8c661b','source','as_of','uuid[]','plpgsql',true,'s',false,false,ARRAY['catalog','initial','context','as_of'],NULL),
  ('trading.check_gas_futures_principal_run()','2a05de34c21206150c55e75eb5e4fee3','principal_seal',NULL,'trigger','plpgsql',true,'v',false,false,NULL,NULL),
  ('trading.check_gas_futures_performance()','7e7fbabb4c8030806711244606ca2d45','current',NULL,'trigger','plpgsql',true,'v',false,false,NULL,NULL)
 )s(signature,source_hash,mode,time_name,result_type,language,definer,volatility,runtime_execute,worker_execute,names,default_expression) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=spec.signature::regprocedure;
  IF md5(p.prosrc)<>spec.source_hash OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>spec.result_type::regtype
   OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof
   OR p.proargnames IS DISTINCT FROM spec.names OR p.proallargtypes IS NOT NULL
   OR p.pronargdefaults<>(CASE WHEN spec.default_expression IS NULL THEN 0 ELSE 1 END)
   OR pg_get_expr(p.proargdefaults,0) IS DISTINCT FROM spec.default_expression
   OR p.provolatile::text<>spec.volatility OR p.proparallel<>'u' OR p.prosecdef IS DISTINCT FROM spec.definer
   OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>spec.language OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR has_function_privilege('openetrm_runtime',p.oid,'EXECUTE') IS DISTINCT FROM spec.runtime_execute
   OR has_function_privilege('openetrm_risk_worker',p.oid,'EXECUTE') IS DISTINCT FROM spec.worker_execute
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner)))a WHERE a.grantor<>p.proowner OR a.is_grantable OR a.privilege_type<>'EXECUTE'
    OR a.grantee NOT IN(p.proowner,CASE WHEN spec.runtime_execute THEN 'openetrm_runtime'::regrole ELSE p.proowner END,CASE WHEN spec.worker_execute THEN 'openetrm_risk_worker'::regrole ELSE p.proowner END))
  THEN RAISE EXCEPTION 'gas_quantity_upgrade: unexpected helper %',spec.signature;END IF;
  definition=pg_get_functiondef(p.oid);
  FOREACH table_name IN ARRAY ARRAY['gas_owned_positions','gas_owned_consumptions','gas_futures_delivery_performances','gas_futures_delivery_splits'] LOOP
   replacement=CASE table_name WHEN 'gas_futures_delivery_performances' THEN 'gas_futures_performances' WHEN 'gas_futures_delivery_splits' THEN 'gas_futures_supplies' ELSE table_name END;
   IF spec.mode='source' THEN replacement='trading.'||replacement||'_at(catalog,context,'||spec.time_name||')';
   ELSIF spec.mode='principal_seal' AND table_name='gas_futures_delivery_performances' THEN replacement='trading.gas_futures_performances_through(NEW.catalog_id,NEW.publication_order)';
   ELSE replacement='trading.'||replacement||'_current';END IF;
   definition=replace(replace(definition,'FROM trading.'||table_name||' ','FROM '||replacement||' '),'JOIN trading.'||table_name||' ','JOIN '||replacement||' ');
  END LOOP;
  IF spec.signature='trading.check_gas_owned_integrity()' THEN definition=replace(definition,'lot.quantity<=0','lot.quantity<0');END IF;
  IF spec.signature LIKE 'trading.require_gas_owned_head(%' THEN
   definition=replace(definition,E'BEGIN\n IF EXISTS',E'BEGIN\n PERFORM trading.require_gas_owned_quantity_head(catalog,account,context,as_of);\n IF EXISTS');
  END IF;
  IF spec.signature LIKE 'trading.gas_owned_financial_ancestors(%' THEN definition=replace(definition,'OR c.lot_id=ANY(positions)','OR c.quantity>0 AND c.lot_id=ANY(positions)');END IF;
  IF spec.signature LIKE 'security.read_gas_owned_holdings_original(%' OR spec.signature LIKE 'security.read_gas_owned_known_history_pre_risk(%'
   OR spec.signature LIKE 'security.gas_physical_journal_source(%' OR spec.signature LIKE 'security.read_gas_owned_operations(%' THEN
   definition=replace(definition,'PERFORM security.require_gas_owned_read(catalog,context,'||spec.time_name||',until_at);RETURN result;',
    'result=trading.gas_quantity_source_result(catalog,context,'||spec.time_name||',result); PERFORM security.require_gas_owned_read(catalog,context,'||spec.time_name||',until_at);RETURN result;');
  END IF;
  IF spec.signature LIKE 'settlement_data.read_futures_delivery_principal(%' THEN
   definition=replace(definition,'IF pg_column_size(result)>16777216',
    'result=jsonb_set(result,ARRAY[''performances''],trading.gas_futures_quantity_source(catalog,context,as_of,result->''performances'')); IF pg_column_size(result)>16777216');
  END IF;
  IF spec.signature LIKE 'security.read_gas_physical_known_attachments(%' THEN
   definition=replace(definition,'IF pg_column_size(result)>16777216',
    'result=trading.gas_physical_quantity_source_result(catalog,context,at_time,result); IF pg_column_size(result)>16777216');
  END IF;
  IF spec.signature LIKE 'trading.apply_futures_delivery_principal(%' THEN
   definition=replace(definition,'old_run.final_determination_id=final_determination AND old_run.performance_count=counted',
    'old_run.final_determination_id=final_determination AND old_run.performance_count=counted AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantities changed WHERE changed.catalog_id=catalog AND changed.publication_order=publication AND changed.revision>1 AND EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances origin WHERE(origin.catalog_id,origin.id,origin.position_id)=(changed.catalog_id,changed.performance_id,holding)))');
  END IF;
  IF spec.signature='trading.check_gas_futures_principal_run()' THEN
   definition=replace(definition,'  current_total=trading.round_gas_delivery_principal(',
    E'  IF NOT EXISTS(SELECT 1 FROM trading.gas_futures_principal_performance_heads h JOIN trading.gas_futures_performance_quantities q ON(q.catalog_id,q.performance_id,q.revision)=(h.catalog_id,h.performance_id,h.performance_revision) WHERE(h.catalog_id,h.determination_id,h.position_id,h.performance_id,q.publication_order)=(NEW.catalog_id,d.id,NEW.position_id,performance.id,performance.publication_order)) THEN RAISE EXCEPTION \'gas_quantity_principal_basis\';END IF;\n  current_total=trading.round_gas_delivery_principal(');
  END IF;
  IF definition=pg_get_functiondef(p.oid) THEN RAISE EXCEPTION 'gas_quantity_upgrade: missing source replacement %',spec.signature;END IF;
  EXECUTE definition;
 END LOOP;
END$upgrade$;

-- Zero cumulative principal is valid only when the complete native financial
-- prefix explicitly contains retractions. The existing complete-prefix seal
-- verifies every zero against those selected quantities and prior cash rows.
DO $$DECLARE selected pg_constraint;column_number smallint;BEGIN
 SELECT attnum INTO STRICT column_number FROM pg_attribute WHERE attrelid='trading.gas_futures_delivery_principal_determinations'::regclass AND attname='cumulative_quantity' AND NOT attisdropped;
 SELECT * INTO STRICT selected FROM pg_constraint WHERE conrelid='trading.gas_futures_delivery_principal_determinations'::regclass AND contype='c' AND conkey=ARRAY[column_number];
 IF NOT selected.convalidated OR NOT selected.conislocal OR selected.coninhcount<>0
  OR regexp_replace(replace(pg_get_expr(selected.conbin,selected.conrelid),'::numeric',''),'[[:space:]()]','','g')<>'cumulative_quantity<>0'
 THEN RAISE EXCEPTION 'gas_quantity_upgrade: unexpected cumulative principal constraint';END IF;
 EXECUTE format('ALTER TABLE trading.gas_futures_delivery_principal_determinations DROP CONSTRAINT %I',selected.conname);
END$$;

CREATE FUNCTION trading.guard_gas_quantity_child() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE publication integer;BEGIN
 SELECT q.publication_order INTO STRICT publication FROM trading.gas_futures_performance_quantities q
  WHERE(q.catalog_id,q.performance_id,q.revision)=(NEW.catalog_id,NEW.performance_id,NEW.performance_revision);
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(NEW.catalog_id,publication,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'futures_delivery_sealed';END IF;RETURN NEW;
END$$;
ALTER FUNCTION trading.guard_gas_quantity_child() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.guard_gas_quantity_child() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE TRIGGER sealed_parent BEFORE INSERT ON trading.gas_futures_performance_quantity_supplies FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_quantity_child();
CREATE TRIGGER sealed_parent BEFORE INSERT ON trading.gas_futures_quantity_sources FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_quantity_child();

CREATE FUNCTION trading.check_gas_quantity_revision() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE base_position trading.gas_owned_positions;performance trading.gas_futures_delivery_performances;version trading.gas_futures_performance_quantities;
 obligation trading.gas_owned_positions;account trading.gas_owned_accounts;total numeric;facts jsonb;item jsonb;BEGIN
 IF TG_TABLE_NAME='gas_owned_position_quantities' THEN
  SELECT * INTO STRICT base_position FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.position_id);
  IF NEW.revision=1 THEN
   IF(NEW.quantity,NEW.evidence_id,NEW.publication_order,NEW.retracted,NEW.actor_id,NEW.account_id) IS DISTINCT FROM
    (base_position.quantity,base_position.evidence_id,base_position.publication_order,false,base_position.actor_id,base_position.account_id) THEN RAISE EXCEPTION 'gas_quantity_origin';END IF;
  ELSE
   IF NOT EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances p JOIN trading.gas_futures_performance_quantities q ON(q.catalog_id,q.performance_id)=(p.catalog_id,p.id)
    WHERE(p.catalog_id,p.receipt_lot_id)=(NEW.catalog_id,NEW.position_id) AND(q.publication_order,q.quantity,q.title_evidence_id,q.retracted)=(NEW.publication_order,NEW.quantity,NEW.evidence_id,NEW.retracted))
   THEN RAISE EXCEPTION 'gas_quantity_title_closure';END IF;
  END IF;
 ELSIF TG_TABLE_NAME='gas_owned_consumption_quantities' THEN
  IF NEW.revision=1 THEN
   IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.id,c.lot_id,c.quantity,c.evidence_id,c.publication_order,c.actor_id)=
    (NEW.catalog_id,NEW.consumption_id,NEW.lot_id,NEW.quantity,NEW.evidence_id,NEW.publication_order,NEW.actor_id) AND c.reservation_id IS NOT DISTINCT FROM NEW.reservation_id AND NOT NEW.retracted)
   THEN RAISE EXCEPTION 'gas_quantity_origin';END IF;
  ELSIF NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_futures_performance_quantities q
   ON(q.catalog_id,q.performance_id,q.revision)=(s.catalog_id,s.performance_id,s.performance_revision)
   WHERE(s.catalog_id,s.consumption_id,s.consumption_revision,q.publication_order)=(NEW.catalog_id,NEW.consumption_id,NEW.revision,NEW.publication_order))
  THEN RAISE EXCEPTION 'gas_quantity_supply_closure';END IF;
 ELSE
  SELECT * INTO STRICT performance FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.performance_id);
  IF NEW.revision=1 THEN
   IF(NEW.quantity,NEW.title_evidence_id,NEW.retracted,NEW.actor_id,NEW.publication_order) IS DISTINCT FROM
    (performance.quantity,performance.title_evidence_id,false,performance.actor_id,performance.publication_order) THEN RAISE EXCEPTION 'gas_quantity_origin';END IF;
  END IF;
  SELECT * INTO STRICT obligation FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,performance.position_id);
  SELECT * INTO STRICT account FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(NEW.catalog_id,obligation.account_id);
  SELECT coalesce(sum(p.quantity),0) INTO total FROM trading.gas_futures_performances_through(NEW.catalog_id,NEW.publication_order) p
   WHERE p.position_id=performance.position_id;
  IF total>abs(obligation.quantity) THEN RAISE EXCEPTION 'futures_delivery_performance';END IF;
  IF obligation.quantity>0 THEN
   IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_positions origin ON(origin.catalog_id,origin.id)=(q.catalog_id,q.position_id)
    JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,q.position_id)=(NEW.catalog_id,performance.receipt_lot_id) AND q.revision=(SELECT max(x.revision) FROM trading.gas_owned_position_quantities x
     WHERE(x.catalog_id,x.position_id)=(q.catalog_id,q.position_id) AND x.publication_order<=NEW.publication_order)
     AND(q.quantity,q.evidence_id,q.retracted,q.publication_order,origin.kind,e.kind,e.account_id,e.occurred_at)=
      (NEW.quantity,NEW.title_evidence_id,NEW.retracted,NEW.publication_order,'owned_stock','title_receipt',account.id,performance.performed_at)
     AND e.quantity>=NEW.quantity) OR EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s
      WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(NEW.catalog_id,NEW.performance_id,NEW.revision))
   THEN RAISE EXCEPTION 'gas_quantity_title_closure';END IF;
  ELSE
   SELECT coalesce(sum(c.quantity),0) INTO total FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_owned_consumption_quantities c
    ON(c.catalog_id,c.consumption_id,c.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision)
    WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(NEW.catalog_id,NEW.performance_id,NEW.revision);
   IF total<>NEW.quantity OR NEW.title_evidence_id IS NOT NULL OR EXISTS(
    SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_owned_consumption_quantities c
     ON(c.catalog_id,c.consumption_id,c.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision)
    JOIN trading.gas_owned_consumptions origin ON(origin.catalog_id,origin.id)=(c.catalog_id,c.consumption_id)
    JOIN trading.gas_owned_positions stock ON(stock.catalog_id,stock.id)=(c.catalog_id,c.lot_id)
    JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(stock.catalog_id,stock.account_id)
    JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(c.catalog_id,c.evidence_id)
    WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(NEW.catalog_id,NEW.performance_id,NEW.revision) AND(
     c.revision<>(SELECT max(x.revision) FROM trading.gas_owned_consumption_quantities x WHERE(x.catalog_id,x.consumption_id)=(c.catalog_id,c.consumption_id) AND x.publication_order<=NEW.publication_order)
     OR c.publication_order>NEW.publication_order OR origin.kind<>'delivery' OR origin.effective_at<>performance.performed_at
     OR e.kind<>'physical_delivery' OR e.account_id<>a.id OR e.occurred_at<>performance.performed_at OR e.quantity<c.quantity
     OR(a.book_id,a.legal_entity_id,a.legal_entity_revision,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.location_code,a.currency_code)
      IS DISTINCT FROM(account.book_id,account.legal_entity_id,account.legal_entity_revision,account.reference_catalog_id,account.reference_revision_id,account.quantity_code,account.location_code,account.currency_code)
     OR EXISTS(SELECT 1 FROM trading.gas_owned_repayments r WHERE(r.catalog_id,r.consumption_id)=(c.catalog_id,c.consumption_id))
     OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements t WHERE(t.catalog_id,t.consumption_id)=(c.catalog_id,c.consumption_id))))
   THEN RAISE EXCEPTION 'gas_quantity_supply_closure';END IF;
  END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.gas_futures_principal_performance_heads h JOIN trading.gas_futures_delivery_principal_determinations d ON(d.catalog_id,d.id)=(h.catalog_id,h.determination_id)
   JOIN trading.gas_futures_delivery_principal_runs r ON(r.catalog_id,r.position_id,r.revision)=(d.catalog_id,d.position_id,d.run_revision)
   WHERE(h.catalog_id,h.performance_id,h.performance_revision,r.publication_order)=(NEW.catalog_id,performance.id,NEW.revision,NEW.publication_order)) THEN RAISE EXCEPTION 'gas_quantity_principal_closure';END IF;
  IF NEW.revision>1 THEN
   facts=trading.gas_futures_allocation_facts(NEW.catalog_id,performance.id,NEW.revision);
   PERFORM trading.require_gas_source_allocation(NEW.source_allocation,facts);
   IF(SELECT count(*) FROM trading.gas_futures_quantity_sources s WHERE(s.catalog_id,s.performance_id,s.performance_revision)=(NEW.catalog_id,performance.id,NEW.revision))<>jsonb_array_length(facts)
   THEN RAISE EXCEPTION 'gas_quantity_source_closure';END IF;
   FOR item IN SELECT value FROM jsonb_array_elements(facts) LOOP
    IF NOT EXISTS(SELECT 1 FROM trading.gas_futures_quantity_sources s WHERE(s.catalog_id,s.performance_id,s.performance_revision,s.account_id,s.source_evidence_id)=
     (NEW.catalog_id,performance.id,NEW.revision,(item->>'accountId')::uuid,(item->>'sourceEvidenceId')::uuid)
     AND s.previous_evidence_id IS NOT DISTINCT FROM(item->>'oldEvidenceId')::uuid) THEN RAISE EXCEPTION 'gas_quantity_source_closure';END IF;
   END LOOP;
  END IF;
 END IF;RETURN NULL;
END$$;
ALTER FUNCTION trading.check_gas_quantity_revision() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.check_gas_quantity_revision() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_owned_position_quantities','gas_owned_consumption_quantities','gas_futures_performance_quantities'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER exact_quantity_revision AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_quantity_revision()',name);
  EXECUTE format('CREATE CONSTRAINT TRIGGER owned_integrity AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_owned_integrity()',name);
 END LOOP;
END$$;


CREATE FUNCTION trading.gas_futures_allocation_facts(catalog uuid,performance uuid,version integer) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected trading.gas_futures_performance_quantities;native trading.gas_futures_delivery_performances;
 run trading.gas_futures_delivery_principal_runs;keys jsonb;key jsonb;result jsonb='[]';source_account trading.gas_owned_accounts;
 before_ids uuid[];after_ids uuid[];previous_sources uuid[];new_sources uuid[];before_amount numeric;after_amount numeric;
 previous_source trading.gas_owned_evidence;current_source trading.gas_owned_evidence;BEGIN
 SELECT * INTO STRICT selected FROM trading.gas_futures_performance_quantities q WHERE(q.catalog_id,q.performance_id,q.revision)=(catalog,performance,version);
 IF version<=1 THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
 SELECT * INTO STRICT native FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.id)=(catalog,performance);
 SELECT r.* INTO STRICT run FROM trading.gas_futures_principal_performance_heads h
  JOIN trading.gas_futures_delivery_principal_determinations d ON(d.catalog_id,d.id)=(h.catalog_id,h.determination_id)
  JOIN trading.gas_futures_delivery_principal_runs r ON(r.catalog_id,r.position_id,r.revision)=(d.catalog_id,d.position_id,d.run_revision)
  WHERE(h.catalog_id,h.performance_id,h.performance_revision,r.publication_order)=(catalog,performance,version,selected.publication_order);
 IF native.receipt_lot_id IS NOT NULL THEN
  SELECT coalesce(jsonb_agg(identity ORDER BY identity->>'accountId',convert_to(identity->>'sourceKey','UTF8')),'[]') INTO keys FROM(
   SELECT DISTINCT jsonb_build_object('accountId',e.account_id,'sourceKey',e.source_key,'kind',e.kind) identity
   FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
   WHERE(q.catalog_id,q.position_id)=(catalog,native.receipt_lot_id) AND q.revision IN(
    SELECT max(x.revision) FROM trading.gas_owned_position_quantities x WHERE(x.catalog_id,x.position_id)=(catalog,native.receipt_lot_id) AND x.publication_order<selected.publication_order
    UNION ALL SELECT max(x.revision) FROM trading.gas_owned_position_quantities x WHERE(x.catalog_id,x.position_id)=(catalog,native.receipt_lot_id) AND x.publication_order<=selected.publication_order))s;
 ELSE
  SELECT coalesce(jsonb_agg(identity ORDER BY identity->>'accountId',convert_to(identity->>'sourceKey','UTF8')),'[]') INTO keys FROM(
   SELECT DISTINCT jsonb_build_object('accountId',e.account_id,'sourceKey',e.source_key,'kind',e.kind) identity
   FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_owned_consumption_quantities q
    ON(q.catalog_id,q.consumption_id,q.revision)=(s.catalog_id,s.consumption_id,s.consumption_revision)
   JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
   WHERE(s.catalog_id,s.performance_id)=(catalog,performance) AND s.performance_revision IN(version-1,version))s;
 END IF;
 FOR key IN SELECT value FROM jsonb_array_elements(keys) LOOP
  IF key->>'kind' IS DISTINCT FROM(CASE WHEN native.receipt_lot_id IS NULL THEN 'physical_delivery' ELSE 'title_receipt' END) THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
  SELECT * INTO STRICT source_account FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,(key->>'accountId')::uuid);
  IF security.reference_version_visible(source_account.reference_catalog_id,source_account.reference_revision_id) IS NOT TRUE
   OR security.market_fact_visible(catalog,source_account.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
  PERFORM settlement_data.require_futures_margin_source(catalog,run.source_context_id,run.as_of,source_account.definition_revision_id);
  IF native.receipt_lot_id IS NOT NULL THEN
   IF EXISTS(SELECT 1 FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,source_account.id,key->>'sourceKey') AND q.revision=(SELECT max(x.revision) FROM trading.gas_owned_position_quantities x
     WHERE(x.catalog_id,x.position_id)=(q.catalog_id,q.position_id) AND x.publication_order<selected.publication_order)
     AND trading.gas_owned_visible(catalog,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',run.source_context_id,run.as_of) IS NOT TRUE)
   THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
   SELECT coalesce(array_agg(q.position_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(abs(q.quantity)),0)
    INTO before_ids,previous_sources,before_amount FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,source_account.id,key->>'sourceKey') AND q.revision=(SELECT max(x.revision) FROM trading.gas_owned_position_quantities x
     WHERE(x.catalog_id,x.position_id)=(q.catalog_id,q.position_id) AND x.publication_order<selected.publication_order);
   SELECT coalesce(array_agg(q.position_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(abs(q.quantity)),0)
    INTO after_ids,new_sources,after_amount FROM trading.gas_owned_position_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,source_account.id,key->>'sourceKey') AND q.revision=(SELECT max(x.revision) FROM trading.gas_owned_position_quantities x
     WHERE(x.catalog_id,x.position_id)=(q.catalog_id,q.position_id) AND x.publication_order<=selected.publication_order);
   IF EXISTS(SELECT 1 FROM unnest(before_ids||after_ids) id WHERE id<>native.receipt_lot_id) THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
  ELSE
   IF EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,source_account.id,key->>'sourceKey') AND q.revision=(SELECT max(x.revision) FROM trading.gas_owned_consumption_quantities x
     WHERE(x.catalog_id,x.consumption_id)=(q.catalog_id,q.consumption_id) AND x.publication_order<selected.publication_order)
     AND trading.gas_owned_visible(catalog,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',run.source_context_id,run.as_of) IS NOT TRUE)
   THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
   SELECT coalesce(array_agg(q.consumption_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(q.quantity),0)
    INTO before_ids,previous_sources,before_amount FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,source_account.id,key->>'sourceKey') AND q.revision=(SELECT max(x.revision) FROM trading.gas_owned_consumption_quantities x
     WHERE(x.catalog_id,x.consumption_id)=(q.catalog_id,q.consumption_id) AND x.publication_order<selected.publication_order);
   SELECT coalesce(array_agg(q.consumption_id),'{}'),coalesce(array_agg(DISTINCT q.evidence_id),'{}'),coalesce(sum(q.quantity),0)
    INTO after_ids,new_sources,after_amount FROM trading.gas_owned_consumption_quantities q JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(q.catalog_id,q.evidence_id)
    WHERE(q.catalog_id,e.account_id,e.source_key)=(catalog,source_account.id,key->>'sourceKey') AND q.revision=(SELECT max(x.revision) FROM trading.gas_owned_consumption_quantities x
     WHERE(x.catalog_id,x.consumption_id)=(q.catalog_id,q.consumption_id) AND x.publication_order<=selected.publication_order);
   IF EXISTS(SELECT 1 FROM unnest(before_ids) AS target(id) WHERE NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s
    WHERE(s.catalog_id,s.performance_id,s.performance_revision,s.consumption_id)=(catalog,performance,version-1,target.id)))
    OR EXISTS(SELECT 1 FROM unnest(after_ids) AS target(id) WHERE NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s
     WHERE(s.catalog_id,s.performance_id,s.performance_revision,s.consumption_id)=(catalog,performance,version,target.id))) THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
  END IF;
  IF cardinality(previous_sources)>1 OR cardinality(new_sources)>1 OR cardinality(before_ids)>16384 OR cardinality(after_ids)>16384 THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;
  previous_source=NULL;
  IF cardinality(previous_sources)=1 THEN SELECT * INTO STRICT previous_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id)=(catalog,previous_sources[1]);END IF;
  SELECT * INTO STRICT current_source FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,source_account.id,key->>'sourceKey')
   AND trading.gas_owned_visible(catalog,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',run.source_context_id,run.as_of) ORDER BY e.revision DESC LIMIT 1;
  IF current_source.kind<>key->>'kind' OR current_source.occurred_at<>native.performed_at
   OR previous_source.id IS NOT NULL AND(previous_source.kind<>current_source.kind OR previous_source.occurred_at<>native.performed_at)
   OR cardinality(new_sources)=1 AND new_sources[1]<>current_source.id THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  IF previous_source.id IS NOT NULL AND trading.gas_owned_visible(catalog,previous_source.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',run.source_context_id,run.as_of) IS NOT TRUE
   THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
  result=result||jsonb_build_array(jsonb_build_object('accountId',source_account.id,'sourceKey',current_source.source_key,'kind',current_source.kind,
   'oldEvidenceId',previous_source.id,'sourceEvidenceId',current_source.id,'oldCapacity',CASE WHEN previous_source.id IS NULL THEN NULL ELSE trim_scale(abs(previous_source.quantity))::text END,
   'capacity',trim_scale(abs(current_source.quantity))::text,'oldAllocated',trim_scale(before_amount)::text,'allocated',trim_scale(after_amount)::text,
   'newEnrollment',previous_source.id IS NULL,'referenceCatalogId',source_account.reference_catalog_id,'referenceRevisionId',source_account.reference_revision_id,'quantity',source_account.quantity_code));
 END LOOP;
 IF jsonb_array_length(result)=0 OR pg_column_size(result)>8388608 THEN RAISE EXCEPTION 'gas_quantity_source_scope';END IF;RETURN result;
END$$;
ALTER FUNCTION trading.gas_futures_allocation_facts(uuid,uuid,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_futures_allocation_facts(uuid,uuid,integer) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION trading.require_gas_source_allocation(policy text,facts jsonb) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE item jsonb;previous_capacity numeric;previous_allocated numeric;capacity numeric;allocated numeric;BEGIN
 IF policy NOT IN('preserve_unallocated_source_quantity_v1','withdraw_all_source_allocations_v1') OR jsonb_typeof(facts) IS DISTINCT FROM 'array' OR jsonb_array_length(facts)=0
 THEN RAISE EXCEPTION 'gas_source_allocation';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(facts) LOOP
  PERFORM trading.gas_owned_projection_keys(item,ARRAY['accountId','sourceKey','kind','oldEvidenceId','sourceEvidenceId','oldCapacity','capacity','oldAllocated','allocated','newEnrollment','referenceCatalogId','referenceRevisionId','quantity'],ARRAY['oldEvidenceId','oldCapacity']);
  IF jsonb_typeof(item->'newEnrollment') IS DISTINCT FROM 'boolean' OR item->>'capacity' IS NULL OR item->>'allocated' IS NULL
   OR item->>'oldAllocated' IS NULL OR jsonb_typeof(item->'capacity') IS DISTINCT FROM 'string' OR jsonb_typeof(item->'allocated') IS DISTINCT FROM 'string'
   OR jsonb_typeof(item->'oldAllocated') IS DISTINCT FROM 'string' OR item->>'sourceEvidenceId' IS NULL THEN RAISE EXCEPTION 'gas_source_allocation';END IF;
  capacity=(item->>'capacity')::numeric;allocated=(item->>'allocated')::numeric;
  IF capacity<0 OR allocated<0 OR allocated>capacity THEN RAISE EXCEPTION 'gas_source_allocation';END IF;
  IF(item->>'newEnrollment')::boolean THEN
   IF policy<>'preserve_unallocated_source_quantity_v1' OR allocated=0 OR item->>'oldEvidenceId' IS NOT NULL OR item->>'oldCapacity' IS NOT NULL OR(item->>'oldAllocated')::numeric<>0
   THEN RAISE EXCEPTION 'gas_source_allocation';END IF;
  ELSE
   previous_capacity=(item->>'oldCapacity')::numeric;previous_allocated=(item->>'oldAllocated')::numeric;
   IF previous_capacity IS NULL OR previous_allocated IS NULL OR previous_capacity<0 OR previous_allocated<0 OR previous_allocated>previous_capacity
    OR policy='preserve_unallocated_source_quantity_v1' AND capacity-allocated<>previous_capacity-previous_allocated
    OR policy='withdraw_all_source_allocations_v1' AND(capacity<>0 OR allocated<>0) THEN RAISE EXCEPTION 'gas_source_allocation';END IF;
  END IF;
 END LOOP;
END$$;
ALTER FUNCTION trading.require_gas_source_allocation(text,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_source_allocation(text,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

COMMIT;
