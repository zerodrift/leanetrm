-- Shared manual facility quantities and explicit gas allocation plans.
-- These records do not amend booked terms, establish final billing or post cash.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
INSERT INTO security.permissions VALUES('ReadFacilityQuantities'),('RecordFacilityQuantities'),('ConfigureFacilityLimits');
-- No existing role or person is granted the new capabilities implicitly.
INSERT INTO security.roles VALUES(gen_random_uuid(),'facility_operator','Facility operations'),
 (gen_random_uuid(),'facility_limits_admin','Facility operating limits');
INSERT INTO security.role_permissions SELECT r.id,p FROM security.roles r
 CROSS JOIN unnest(ARRAY['ReadMarketData','ReadTrades','ReadFacilityQuantities','RecordFacilityQuantities'])p WHERE r.code='facility_operator';
INSERT INTO security.role_permissions SELECT r.id,p FROM security.roles r
 CROSS JOIN unnest(ARRAY['ReadMarketData','ReadTrades','ReadFacilityQuantities','ConfigureFacilityLimits'])p WHERE r.code='facility_limits_admin';
-- Existing people receive neither role. Workspace access administration assigns
-- these explicit capabilities using its existing authorized role-grant operation.

CREATE TABLE trading.facility_quantity_ledgers(
 catalog_id uuid NOT NULL,id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 facility_code ref_data.code NOT NULL,flow_point_code ref_data.code NOT NULL,
 commodity_code ref_data.code NOT NULL,quantity_code ref_data.code NOT NULL,
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,
 direction integer NOT NULL CHECK(direction IN(-1,1)),
 native_from ref_data.instant NOT NULL,native_to ref_data.instant NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),CHECK(native_from<native_to),
 -- Metadata/unit revisions do not mint a fresh logical source pool. A changed
 -- exact tuple requires a separately qualified continuity operation.
 UNIQUE(catalog_id,reference_catalog_id,flow_point_code,commodity_code,legal_entity_id,direction,native_from,native_to),
 UNIQUE(catalog_id,id,reference_catalog_id,reference_revision_id,commodity_code,flow_point_code,legal_entity_id,direction),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,commodity_code,facility_code)
  REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,commodity_code,flow_point_code)
  REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code,commodity_code)
  REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- The first typed consumer. Other commodities do not acquire empty detail tables.
CREATE TABLE trading.facility_gas_delivery_scopes(
 catalog_id uuid NOT NULL,ledger_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 product_code ref_data.code NOT NULL,pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,
 legal_entity_id uuid NOT NULL,direction integer NOT NULL,
 day date NOT NULL CHECK(isfinite(day)),origin_obligation_id uuid NOT NULL,PRIMARY KEY(catalog_id,ledger_id),
 FOREIGN KEY(catalog_id,origin_obligation_id) REFERENCES trading.gas_delivery_obligations(catalog_id,id),
 UNIQUE(catalog_id,reference_catalog_id,point_code,product_code,legal_entity_id,direction,day),
 FOREIGN KEY(catalog_id,ledger_id,reference_catalog_id,reference_revision_id,product_code,point_code,legal_entity_id,direction)
  REFERENCES trading.facility_quantity_ledgers(catalog_id,id,reference_catalog_id,reference_revision_id,commodity_code,flow_point_code,legal_entity_id,direction),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code)
  REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)
);
CREATE TABLE trading.facility_quantity_revisions(
 catalog_id uuid NOT NULL,ledger_id uuid NOT NULL,id uuid NOT NULL,
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 quantity numeric NOT NULL CHECK(quantity>=0 AND quantity<1e24 AND scale(quantity)<=18),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,ledger_id,id),UNIQUE(catalog_id,ledger_id,revision),
 UNIQUE(catalog_id,ledger_id,effective_at,quantity,evidence),
 UNIQUE(catalog_id,ledger_id,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,ledger_id) REFERENCES trading.facility_quantity_ledgers(catalog_id,id),
 FOREIGN KEY(catalog_id,ledger_id,previous_id) REFERENCES trading.facility_quantity_revisions(catalog_id,ledger_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.facility_quantity_plans(
 catalog_id uuid NOT NULL,ledger_id uuid NOT NULL,id uuid NOT NULL,
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,source_revision_id uuid NOT NULL,
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,ledger_id,id),UNIQUE(catalog_id,ledger_id,revision),
 UNIQUE(catalog_id,ledger_id,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,ledger_id) REFERENCES trading.facility_quantity_ledgers(catalog_id,id),
 FOREIGN KEY(catalog_id,ledger_id,previous_id) REFERENCES trading.facility_quantity_plans(catalog_id,ledger_id,id),
 FOREIGN KEY(catalog_id,ledger_id,source_revision_id) REFERENCES trading.facility_quantity_revisions(catalog_id,ledger_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- A native obligation cell has one facility allocation scope. The current slice
-- does not replace the existing gas quantity-input/billing selection policy.
CREATE TABLE trading.facility_gas_targets(
 catalog_id uuid NOT NULL,ledger_id uuid NOT NULL,obligation_id uuid NOT NULL,day date NOT NULL,
 PRIMARY KEY(catalog_id,obligation_id,day),UNIQUE(catalog_id,ledger_id,obligation_id),
 FOREIGN KEY(catalog_id,ledger_id) REFERENCES trading.facility_quantity_ledgers(catalog_id,id),
 FOREIGN KEY(catalog_id,obligation_id) REFERENCES trading.gas_delivery_obligations(catalog_id,id)
);
CREATE TABLE trading.facility_gas_allocations(
 catalog_id uuid NOT NULL,ledger_id uuid NOT NULL,plan_id uuid NOT NULL,obligation_id uuid NOT NULL,
 trade_id uuid NOT NULL,trade_revision integer NOT NULL,instrument_id uuid NOT NULL,
 quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),
 PRIMARY KEY(catalog_id,plan_id,obligation_id),
 FOREIGN KEY(catalog_id,ledger_id,plan_id) REFERENCES trading.facility_quantity_plans(catalog_id,ledger_id,id),
 FOREIGN KEY(catalog_id,ledger_id,obligation_id) REFERENCES trading.facility_gas_targets(catalog_id,ledger_id,obligation_id),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id)
);
CREATE TABLE trading.facility_quantity_limits(
 catalog_id uuid NOT NULL,ledger_id uuid NOT NULL,id uuid NOT NULL,
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 quantity numeric CHECK(quantity>=0 AND quantity<1e24 AND scale(quantity)<=18),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),
 implementation text NOT NULL CHECK(implementation='native_interval_maximum_v1'),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,ledger_id,id),UNIQUE(catalog_id,ledger_id,revision),
 UNIQUE(catalog_id,ledger_id,previous_id),
 UNIQUE NULLS NOT DISTINCT(catalog_id,ledger_id,effective_at,quantity,evidence),
 CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,ledger_id) REFERENCES trading.facility_quantity_ledgers(catalog_id,id),
 FOREIGN KEY(catalog_id,ledger_id,previous_id) REFERENCES trading.facility_quantity_limits(catalog_id,ledger_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.facility_quantity_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 ledger_id uuid NOT NULL,request jsonb NOT NULL CHECK(pg_column_size(request)<=65536),outcome jsonb NOT NULL,
 create_obligation_id uuid,previous_plan_id uuid,result_plan_id uuid,
 PRIMARY KEY(catalog_id,actor_id,operation_id),
 FOREIGN KEY(catalog_id,ledger_id) REFERENCES trading.facility_quantity_ledgers(catalog_id,id),
 FOREIGN KEY(catalog_id,create_obligation_id) REFERENCES trading.gas_delivery_obligations(catalog_id,id),
 FOREIGN KEY(catalog_id,ledger_id,previous_plan_id) REFERENCES trading.facility_quantity_plans(catalog_id,ledger_id,id),
 FOREIGN KEY(catalog_id,ledger_id,result_plan_id) REFERENCES trading.facility_quantity_plans(catalog_id,ledger_id,id),
 CHECK((request#>>'{body,kind}'='create')=(create_obligation_id IS NOT NULL)),
 CHECK((request#>>'{body,kind}'='allocate')=(result_plan_id IS NOT NULL)),
 CHECK(previous_plan_id IS NULL OR result_plan_id IS NOT NULL)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['facility_quantity_ledgers','facility_gas_delivery_scopes','facility_quantity_revisions',
  'facility_quantity_plans','facility_gas_targets','facility_gas_allocations','facility_quantity_limits','facility_quantity_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  -- UPDATE is needed for row locks; the immutable trigger refuses every update.
  EXECUTE format('GRANT SELECT,INSERT,UPDATE ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

CREATE VIEW trading.facility_quantity_scope_rows AS
 SELECT l.*,f.name facility,p.name point,c.name commodity,u.symbol unit,e.name legal_entity,g.day
 FROM trading.facility_quantity_ledgers l JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(l.catalog_id,l.id)
 JOIN ref_data.locations f ON(f.catalog_id,f.revision_id,f.code)=(l.reference_catalog_id,l.reference_revision_id,l.facility_code)
 JOIN ref_data.locations p ON(p.catalog_id,p.revision_id,p.code)=(l.reference_catalog_id,l.reference_revision_id,l.flow_point_code)
 JOIN ref_data.commodities c ON(c.catalog_id,c.revision_id,c.code)=(l.reference_catalog_id,l.reference_revision_id,l.commodity_code)
 JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(l.reference_catalog_id,l.reference_revision_id,l.quantity_code)
 JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
 JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=(l.catalog_id,l.legal_entity_id,l.legal_entity_revision);
ALTER VIEW trading.facility_quantity_scope_rows OWNER TO openetrm_security_owner;
REVOKE ALL ON trading.facility_quantity_scope_rows FROM PUBLIC,openetrm_runtime;

CREATE FUNCTION security.require_facility_quantity_read(catalog uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$ BEGIN
 PERFORM security.require_facility_quantities(catalog,false);
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND c.market_catalog_id=catalog AND c.market_context_id IS NOT NULL AND c.trading_as_of IS NOT NULL)
 THEN RAISE EXCEPTION 'facility_quantity_context';END IF;
END$$;

CREATE FUNCTION security.require_facility_quantity_command(catalog uuid,kind text) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$ BEGIN
 IF kind='limit' THEN
  PERFORM security.require_facility_quantities(catalog,false);
  IF security.trading_access(catalog,'ConfigureFacilityLimits') IS NOT TRUE THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
  IF NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
 ELSE PERFORM security.require_facility_quantities(catalog,true);END IF;
END$$;

CREATE FUNCTION security.require_facility_quantities(catalog uuid,writing boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 IF security.trading_access(catalog,'ReadFacilityQuantities') IS NOT TRUE
 OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
 OR(writing AND security.trading_access(catalog,'RecordFacilityQuantities') IS NOT TRUE)
 THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
 IF writing AND NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
END$$;

-- Read exact retained quantities rather than regenerate a gas-day calendar or
-- reimplement fuel/normalization rules. Unsupported roots refuse explicitly.
CREATE TYPE trading.facility_gas_target AS(trade_id uuid,trade_revision integer,instrument_id uuid,reference_catalog_id uuid,reference_revision_id uuid,quantity_code text,product_code text,
 pipeline_code text,zone_code text,point_code text,legal_entity_id uuid,legal_entity_revision integer,direction integer,
 native_from timestamptz,native_to timestamptz,quantity_n numeric,quantity_d numeric);
CREATE FUNCTION security.facility_gas_target(catalog uuid,obligation uuid,flow_day date,as_of timestamptz)
RETURNS SETOF trading.facility_gas_target
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE trade uuid;BEGIN
 SELECT o.trade_id INTO trade FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.id)=(catalog,obligation);
 IF trade IS NULL THEN RAISE EXCEPTION 'facility_quantity_target';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 RETURN QUERY SELECT o.trade_id,r.revision,r.instrument_id,q.unit_catalog_id,q.unit_revision_id,q.unit_code::text,m.product_code::text,
 m.pipeline_code::text,m.zone_code::text,m.point_code::text,r.legal_entity_id,r.legal_entity_revision,m.direction,
 g.origin+(g.selection[1]*g.step_ticks/10)*interval '1 microsecond',
 g.origin+((g.selection[1]+g.lengths[1])*g.step_ticks/10)*interval '1 microsecond',
 (q.parameters->>'numerator')::numeric*r.quantity*m.ratio_n,(q.parameters->>'denominator')::numeric*m.ratio_d
 FROM trading.gas_delivery_obligations o
 JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
 JOIN LATERAL(SELECT h.* FROM trading.gas_trade_revisions h WHERE(h.catalog_id,h.trade_id)=(o.catalog_id,o.trade_id)
  AND(security.current_write_context() OR security.trading_visible(h.catalog_id,h.publication_order,h.effective_at))
  ORDER BY h.revision DESC LIMIT 1)r ON r.instrument_id=o.instrument_id
 JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot)=(m.catalog_id,m.instrument_id,m.slot)
 JOIN trading.side_schedule_roots root ON(root.catalog_id,root.schedule_id,root.measure)=(s.catalog_id,s.schedule_id,'schedule_quantity')
 JOIN trading.side_schedule_rows q ON(q.catalog_id,q.schedule_id,q.ordinal)=(root.catalog_id,root.schedule_id,root.row_ordinal)
 JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(q.catalog_id,q.schedule_id,q.grid_ordinal)
 JOIN trading.side_schedule_publications p ON(p.catalog_id,p.schedule_id)=(s.catalog_id,s.schedule_id)
 WHERE(o.catalog_id,o.id)=(catalog,obligation) AND m.role='physical' AND q.operation='quantity_literal'
 AND g.operating_days=ARRAY[flow_day-date '0001-01-01'] AND cardinality(g.selection)=1 AND g.lengths=ARRAY[1]
 AND r.effective_at<=as_of AND p.effective_at<=as_of
 AND(security.current_write_context() OR(security.trading_visible(r.catalog_id,r.publication_order,r.effective_at)
  AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)))
 AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order))
 AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order));
END$$;

CREATE FUNCTION security.facility_gas_target_for_ledger(catalog uuid,ledger uuid,obligation uuid,as_of timestamptz,amount numeric)
RETURNS trading.facility_gas_target LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE l trading.facility_quantity_ledgers;g trading.facility_gas_delivery_scopes;t trading.facility_gas_target;BEGIN
 SELECT * INTO STRICT l FROM trading.facility_quantity_ledgers h WHERE(h.catalog_id,h.id)=(catalog,ledger);
 SELECT * INTO STRICT g FROM trading.facility_gas_delivery_scopes s WHERE(s.catalog_id,s.ledger_id)=(catalog,ledger);
 BEGIN SELECT * INTO STRICT t FROM security.facility_gas_target(catalog,obligation,g.day,as_of);
 EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'facility_quantity_target';END;
 IF(t.reference_catalog_id,t.reference_revision_id,t.quantity_code,t.product_code,t.pipeline_code,t.zone_code,t.point_code,
  t.legal_entity_id,t.legal_entity_revision,t.direction,t.native_from,t.native_to)
  IS DISTINCT FROM(l.reference_catalog_id,l.reference_revision_id,l.quantity_code,l.commodity_code,g.pipeline_code,g.zone_code,l.flow_point_code,
  l.legal_entity_id,l.legal_entity_revision,l.direction,l.native_from,l.native_to)
 OR t.quantity_d<=0 OR(amount IS NOT NULL AND(amount<=0 OR amount*t.quantity_d>t.quantity_n))
 THEN RAISE EXCEPTION 'facility_quantity_target';END IF;
 RETURN t;
END$$;

CREATE FUNCTION security.require_facility_plan_targets(catalog uuid,first_plan uuid,second_plan uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE target record;BEGIN
 FOR target IN SELECT DISTINCT a.trade_id FROM trading.facility_gas_allocations a
 WHERE a.catalog_id=catalog AND a.plan_id IN(first_plan,second_plan) LOOP
  PERFORM security.require_gas_quantity_trade(catalog,target.trade_id,false);
 END LOOP;
END$$;

-- Constraint triggers protect conservation even if a later writer omits a
-- check. New lines must belong to this transaction's new immutable plan.
CREATE FUNCTION trading.require_facility_quantity_writer(catalog uuid,actor uuid,kind text,publication integer) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$ BEGIN
 PERFORM security.require_facility_quantity_command(catalog,kind);
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.actor_id=actor AND c.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(catalog,publication,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'facility_quantity_immutable';END IF;
END$$;
CREATE FUNCTION trading.guard_facility_quantity_header() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE origin uuid;BEGIN
 PERFORM trading.require_facility_quantity_writer(NEW.catalog_id,NEW.actor_id,'create',NEW.publication_order);
 IF NEW.facility_code<>NEW.flow_point_code THEN RAISE EXCEPTION 'facility_quantity_scope';END IF;
 SELECT g.origin_obligation_id INTO STRICT origin FROM trading.facility_gas_delivery_scopes g WHERE(g.catalog_id,g.ledger_id)=(NEW.catalog_id,NEW.id);
 PERFORM security.facility_gas_target_for_ledger(NEW.catalog_id,NEW.id,origin,NEW.effective_at,NULL);
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER valid_facility_quantity_header AFTER INSERT ON trading.facility_quantity_ledgers
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_facility_quantity_header();

CREATE FUNCTION trading.guard_facility_quantity_revision() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE previous record;l trading.facility_quantity_ledgers;BEGIN
 CASE TG_TABLE_NAME WHEN 'facility_quantity_limits' THEN
  PERFORM trading.require_facility_quantity_writer(NEW.catalog_id,NEW.actor_id,'limit',NEW.publication_order);
 WHEN 'facility_quantity_revisions' THEN
  PERFORM trading.require_facility_quantity_writer(NEW.catalog_id,NEW.actor_id,'source',NEW.publication_order);
 ELSE RAISE EXCEPTION 'facility_quantity_input';END CASE;
 SELECT * INTO STRICT l FROM trading.facility_quantity_ledgers h WHERE(h.catalog_id,h.id)=(NEW.catalog_id,NEW.ledger_id) FOR UPDATE;
 IF l.facility_code<>l.flow_point_code OR NOT EXISTS(SELECT 1 FROM trading.facility_gas_delivery_scopes g WHERE(g.catalog_id,g.ledger_id)=(l.catalog_id,l.id))
 THEN RAISE EXCEPTION 'facility_quantity_scope';END IF;
 IF TG_TABLE_NAME='facility_quantity_limits' THEN
  SELECT * INTO previous FROM trading.facility_quantity_limits r WHERE(r.catalog_id,r.ledger_id,r.revision)=(NEW.catalog_id,NEW.ledger_id,NEW.revision-1);
 ELSE
  SELECT * INTO previous FROM trading.facility_quantity_revisions r WHERE(r.catalog_id,r.ledger_id,r.revision)=(NEW.catalog_id,NEW.ledger_id,NEW.revision-1);
 END IF;
 IF NEW.previous_id IS DISTINCT FROM previous.id OR NEW.effective_at<previous.effective_at
 OR(TG_TABLE_NAME='facility_quantity_revisions' AND NEW.effective_at<l.native_to) OR NEW.effective_at<l.effective_at
 THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER valid_facility_quantity_revision AFTER INSERT ON trading.facility_quantity_revisions
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_facility_quantity_revision();
CREATE CONSTRAINT TRIGGER valid_facility_quantity_limit AFTER INSERT ON trading.facility_quantity_limits
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_facility_quantity_revision();

CREATE FUNCTION trading.guard_facility_quantity_plan() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE plan trading.facility_quantity_plans;source trading.facility_quantity_revisions;head trading.facility_quantity_plans;identity uuid;
 l trading.facility_quantity_ledgers;gas trading.facility_gas_delivery_scopes;line trading.facility_gas_allocations;target record;
BEGIN
 IF TG_TABLE_NAME='facility_gas_allocations' THEN identity=NEW.plan_id;ELSE identity=NEW.id;END IF;
 SELECT * INTO STRICT plan FROM trading.facility_quantity_plans p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,identity);
 PERFORM trading.require_facility_quantity_writer(plan.catalog_id,plan.actor_id,'allocate',plan.publication_order);
 SELECT * INTO STRICT l FROM trading.facility_quantity_ledgers h WHERE(h.catalog_id,h.id)=(plan.catalog_id,plan.ledger_id) FOR UPDATE;
 SELECT * INTO STRICT gas FROM trading.facility_gas_delivery_scopes g WHERE(g.catalog_id,g.ledger_id)=(plan.catalog_id,plan.ledger_id);
 IF l.facility_code<>l.flow_point_code THEN RAISE EXCEPTION 'facility_quantity_scope';END IF;
 PERFORM security.require_facility_plan_targets(plan.catalog_id,plan.previous_id,plan.id);
 SELECT * INTO STRICT source FROM trading.facility_quantity_revisions r WHERE(r.catalog_id,r.ledger_id,r.id)=(plan.catalog_id,plan.ledger_id,plan.source_revision_id);
 IF source.effective_at>plan.effective_at OR source.publication_order>plan.publication_order
 THEN RAISE EXCEPTION 'facility_quantity_interval';END IF;
 IF EXISTS(SELECT 1 FROM trading.facility_quantity_revisions r WHERE(r.catalog_id,r.ledger_id)=(plan.catalog_id,plan.ledger_id)
  AND r.publication_order<=plan.publication_order AND r.revision>source.revision)
 THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
 IF(SELECT coalesce(sum(a.quantity),0) FROM trading.facility_gas_allocations a WHERE(a.catalog_id,a.plan_id)=(plan.catalog_id,plan.id))>source.quantity
 THEN RAISE EXCEPTION 'facility_quantity_overallocated';END IF;
 SELECT * INTO head FROM trading.facility_quantity_plans p WHERE(p.catalog_id,p.ledger_id)=(plan.catalog_id,plan.ledger_id) AND p.revision=plan.revision-1;
 IF plan.previous_id IS DISTINCT FROM head.id OR head.effective_at>plan.effective_at THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
 FOR line IN SELECT a.* FROM trading.facility_gas_allocations a WHERE(a.catalog_id,a.plan_id)=(plan.catalog_id,plan.id) LOOP
  target=security.facility_gas_target_for_ledger(plan.catalog_id,plan.ledger_id,line.obligation_id,plan.effective_at,line.quantity);
  IF(target.trade_id,target.trade_revision,target.instrument_id) IS DISTINCT FROM(line.trade_id,line.trade_revision,line.instrument_id)
  OR NOT EXISTS(SELECT 1 FROM trading.facility_gas_targets t WHERE(t.catalog_id,t.ledger_id,t.obligation_id,t.day)=(plan.catalog_id,plan.ledger_id,line.obligation_id,gas.day))
  THEN RAISE EXCEPTION 'facility_quantity_target';END IF;
 END LOOP;
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER valid_facility_quantity_plan AFTER INSERT ON trading.facility_quantity_plans
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_facility_quantity_plan();
CREATE CONSTRAINT TRIGGER valid_facility_quantity_allocation AFTER INSERT ON trading.facility_gas_allocations
DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_facility_quantity_plan();

CREATE FUNCTION security.record_facility_quantity_command(catalog uuid,operation uuid,body jsonb,as_of timestamptz)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;prior trading.facility_quantity_commands;l trading.facility_quantity_ledgers;
 source trading.facility_quantity_revisions;plan trading.facility_quantity_plans;gas trading.facility_gas_delivery_scopes;
 configured trading.facility_quantity_limits;
 target record;item jsonb;lines jsonb;payload jsonb;result jsonb;identity uuid;publication integer;amount numeric;previous_plan uuid;
BEGIN
 PERFORM security.require_facility_quantity_command(catalog,body->>'kind');
 -- Serialize with existing trade/quantity publication and security changes.
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_facility_quantity_command(catalog,body->>'kind');
 SELECT c.actor_id INTO actor FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 IF body IS NULL OR jsonb_typeof(body)<>'object' OR pg_column_size(body)>65536 OR operation IS NULL
 OR operation='00000000-0000-0000-0000-000000000000'::uuid OR as_of IS NULL OR NOT isfinite(as_of)
 OR body->>'kind' IS NULL OR body->>'kind' NOT IN('create','source','allocate','limit') THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
 -- Array order is presentation, not allocation fact identity.
 IF body->>'kind'='allocate' THEN
  IF jsonb_typeof(body->'allocations') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'allocations')>100 THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
  FOR item IN SELECT value FROM jsonb_array_elements(body->'allocations') LOOP
   IF jsonb_typeof(item)<>'object' OR(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(item)k) IS DISTINCT FROM ARRAY['obligationId','quantity','tradeRevision']::text[]
   OR jsonb_typeof(item->'quantity') IS DISTINCT FROM 'string' OR item->>'quantity' !~ '^[0-9]+(\.[0-9]+)?$' THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
   amount=(item->>'quantity')::numeric;
   IF amount<=0 OR amount>=1e24 OR scale(amount)>18 OR(item->>'obligationId')::uuid IS NULL
   OR(item->>'tradeRevision')::integer IS NULL OR(item->>'tradeRevision')::integer<1 THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
  END LOOP;
  SELECT coalesce(jsonb_agg(jsonb_build_object('obligationId',(value->>'obligationId')::uuid,'quantity',trim_scale((value->>'quantity')::numeric)::text,'tradeRevision',(value->>'tradeRevision')::integer)
   ORDER BY ((value->>'obligationId')::uuid)::text),'[]') INTO lines FROM jsonb_array_elements(body->'allocations');
  IF(SELECT count(*)<>count(DISTINCT value->>'obligationId') FROM jsonb_array_elements(lines)) THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
  body=jsonb_set(body,'{allocations}',lines);
 END IF;
 payload=jsonb_build_object('body',body,'effectiveAt',as_of);
 SELECT * INTO prior FROM trading.facility_quantity_commands c WHERE(c.catalog_id,c.actor_id,c.operation_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM payload THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
  SELECT * INTO STRICT l FROM trading.facility_quantity_ledgers h WHERE(h.catalog_id,h.id)=(catalog,prior.ledger_id);
  IF NOT security.reference_version_visible(l.reference_catalog_id,l.reference_revision_id) THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
  -- Retried commands retain their original pins, but never historical authority.
  PERFORM security.require_facility_plan_targets(catalog,prior.previous_plan_id,prior.result_plan_id);
  IF prior.create_obligation_id IS NOT NULL THEN
   SELECT o.trade_id INTO identity FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.id)=(catalog,prior.create_obligation_id);
   PERFORM security.require_gas_quantity_trade(catalog,identity,false);
  END IF;
  PERFORM security.require_facility_quantity_command(catalog,body->>'kind');
  RETURN prior.outcome;
 END IF;
 IF body->>'kind'='create' THEN
  IF(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(body)k) IS DISTINCT FROM ARRAY['day','facility','kind','obligationId']::text[] THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
  BEGIN SELECT * INTO STRICT target FROM security.facility_gas_target(catalog,(body->>'obligationId')::uuid,(body->>'day')::date,as_of);
  EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'facility_quantity_target';END;
  IF body->>'facility' IS DISTINCT FROM target.point_code THEN RAISE EXCEPTION 'facility_quantity_scope';END IF;
  IF NOT security.reference_version_visible(target.reference_catalog_id,target.reference_revision_id) THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
  SELECT h.* INTO l FROM trading.facility_quantity_ledgers h JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(h.catalog_id,h.id)
   WHERE(g.catalog_id,g.reference_catalog_id,g.point_code,g.product_code,g.legal_entity_id,g.direction,g.day)=
   (catalog,target.reference_catalog_id,target.point_code,target.product_code,target.legal_entity_id,target.direction,(body->>'day')::date);
  IF l.id IS NOT NULL AND(l.reference_revision_id,l.quantity_code,l.legal_entity_revision,l.facility_code,l.native_from,l.native_to)
   IS DISTINCT FROM(target.reference_revision_id,target.quantity_code,target.legal_entity_revision,target.point_code,target.native_from,target.native_to)
  THEN RAISE EXCEPTION 'facility_quantity_scope_change';END IF;
  IF l.id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.facility_gas_delivery_scopes g
   WHERE(g.catalog_id,g.ledger_id,g.pipeline_code,g.zone_code,g.point_code,g.product_code,g.day)=
   (catalog,l.id,target.pipeline_code,target.zone_code,target.point_code,target.product_code,(body->>'day')::date))
  THEN RAISE EXCEPTION 'facility_quantity_scope_change';END IF;
  IF l.id IS NULL THEN
   SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=catalog;
   INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
   identity=gen_random_uuid();
   INSERT INTO trading.facility_quantity_ledgers VALUES(catalog,identity,target.reference_catalog_id,target.reference_revision_id,body->>'facility',target.point_code,target.product_code,target.quantity_code,
    target.legal_entity_id,target.legal_entity_revision,target.direction,target.native_from,target.native_to,publication,as_of,actor) RETURNING * INTO l;
   INSERT INTO trading.facility_gas_delivery_scopes VALUES(catalog,l.id,target.reference_catalog_id,target.reference_revision_id,target.product_code,target.pipeline_code,target.zone_code,target.point_code,
    target.legal_entity_id,target.direction,(body->>'day')::date,(body->>'obligationId')::uuid);
  END IF;
  result=jsonb_build_object('ledgerId',l.id);
 ELSE
  SELECT * INTO l FROM trading.facility_quantity_ledgers h WHERE(h.catalog_id,h.id)=(catalog,(body->>'ledgerId')::uuid) FOR UPDATE;
  IF l.id IS NULL THEN RAISE EXCEPTION 'facility_quantity_ledger';END IF;
  IF NOT security.reference_version_visible(l.reference_catalog_id,l.reference_revision_id) THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
  IF(body->>'kind'<>'limit' AND as_of<l.native_to) OR as_of<l.effective_at THEN RAISE EXCEPTION 'facility_quantity_interval';END IF;
  SELECT * INTO STRICT gas FROM trading.facility_gas_delivery_scopes g WHERE(g.catalog_id,g.ledger_id)=(catalog,l.id);
  IF body->>'kind'='limit' THEN
   IF(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(body)k) IS DISTINCT FROM ARRAY['evidence','expectedPreviousId','kind','ledgerId','quantity']::text[]
   OR jsonb_typeof(body->'quantity') NOT IN('string','null') OR(body->'quantity'<>'null'::jsonb AND body->>'quantity' !~ '^[0-9]+(\.[0-9]+)?$')
   OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 OR body->>'evidence' IS NULL THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
   amount=(body->>'quantity')::numeric;
   IF amount<0 OR amount>=1e24 OR scale(amount)>18 THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
   SELECT * INTO configured FROM trading.facility_quantity_limits r WHERE(r.catalog_id,r.ledger_id)=(catalog,l.id)
    AND(r.quantity,r.evidence,r.effective_at) IS NOT DISTINCT FROM(amount,btrim(body->>'evidence'),as_of);
   IF configured.id IS NULL THEN
    SELECT * INTO configured FROM trading.facility_quantity_limits r WHERE(r.catalog_id,r.ledger_id)=(catalog,l.id) ORDER BY revision DESC LIMIT 1;
    IF configured.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid OR configured.effective_at>as_of THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
    SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=catalog;
    INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
    INSERT INTO trading.facility_quantity_limits VALUES(catalog,l.id,gen_random_uuid(),coalesce(configured.revision,0)+1,configured.id,amount,btrim(body->>'evidence'),publication,as_of,actor,'native_interval_maximum_v1') RETURNING * INTO configured;
   END IF;
   result=jsonb_build_object('ledgerId',l.id,'revisionId',configured.id,'revision',configured.revision);
  ELSIF body->>'kind'='source' THEN
   IF(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(body)k) IS DISTINCT FROM ARRAY['evidence','expectedPreviousId','kind','ledgerId','quantity']::text[]
   OR jsonb_typeof(body->'quantity') IS DISTINCT FROM 'string' OR body->>'quantity' !~ '^[0-9]+(\.[0-9]+)?$' THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
   amount=(body->>'quantity')::numeric;
   IF amount>=1e24 OR scale(amount)>18 OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 OR body->>'evidence' IS NULL THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
   SELECT * INTO source FROM trading.facility_quantity_revisions r WHERE(r.catalog_id,r.ledger_id,r.quantity,r.evidence,r.effective_at)=(catalog,l.id,amount,btrim(body->>'evidence'),as_of);
   IF source.id IS NULL THEN
    SELECT * INTO source FROM trading.facility_quantity_revisions r WHERE(r.catalog_id,r.ledger_id)=(catalog,l.id) ORDER BY revision DESC LIMIT 1;
    IF source.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid OR source.effective_at>as_of THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
    SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=catalog;
    INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
    INSERT INTO trading.facility_quantity_revisions VALUES(catalog,l.id,gen_random_uuid(),coalesce(source.revision,0)+1,source.id,amount,btrim(body->>'evidence'),publication,as_of,actor) RETURNING * INTO source;
   END IF;
   result=jsonb_build_object('ledgerId',l.id,'revisionId',source.id,'revision',source.revision);
  ELSE
   IF(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(body)k) IS DISTINCT FROM ARRAY['allocations','evidence','expectedPreviousId','kind','ledgerId','sourceRevisionId']::text[]
   OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 OR body->>'evidence' IS NULL THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
   SELECT * INTO plan FROM trading.facility_quantity_plans p WHERE(p.catalog_id,p.ledger_id,p.source_revision_id,p.evidence,p.effective_at)=(catalog,l.id,(body->>'sourceRevisionId')::uuid,btrim(body->>'evidence'),as_of)
    AND(SELECT coalesce(jsonb_agg(jsonb_build_object('obligationId',a.obligation_id,'quantity',trim_scale(a.quantity)::text,'tradeRevision',a.trade_revision) ORDER BY a.obligation_id::text),'[]') FROM trading.facility_gas_allocations a WHERE(a.catalog_id,a.plan_id)=(p.catalog_id,p.id))=lines;
   IF plan.id IS NULL THEN
    SELECT * INTO plan FROM trading.facility_quantity_plans p WHERE(p.catalog_id,p.ledger_id)=(catalog,l.id) ORDER BY revision DESC LIMIT 1;
    PERFORM security.require_facility_plan_targets(catalog,plan.id,NULL);
    SELECT * INTO source FROM trading.facility_quantity_revisions r WHERE(r.catalog_id,r.ledger_id)=(catalog,l.id) ORDER BY revision DESC LIMIT 1;
    IF source.id IS NULL OR source.id IS DISTINCT FROM(body->>'sourceRevisionId')::uuid OR source.effective_at>as_of
    OR plan.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid OR plan.effective_at>as_of THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
    IF(SELECT coalesce(sum((value->>'quantity')::numeric),0) FROM jsonb_array_elements(lines))>source.quantity THEN RAISE EXCEPTION 'facility_quantity_overallocated';END IF;
    FOR item IN SELECT value FROM jsonb_array_elements(lines) LOOP
     target=security.facility_gas_target_for_ledger(catalog,l.id,(item->>'obligationId')::uuid,as_of,(item->>'quantity')::numeric);
     IF target.trade_revision IS DISTINCT FROM(item->>'tradeRevision')::integer THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
     IF EXISTS(SELECT 1 FROM trading.facility_gas_targets t WHERE(t.catalog_id,t.obligation_id,t.day)=(catalog,(item->>'obligationId')::uuid,gas.day) AND t.ledger_id<>l.id)
     THEN RAISE EXCEPTION 'facility_quantity_target';END IF;
     INSERT INTO trading.facility_gas_targets VALUES(catalog,l.id,(item->>'obligationId')::uuid,gas.day) ON CONFLICT DO NOTHING;
    END LOOP;
    SELECT coalesce(max(p.publication_order),0)+1 INTO publication FROM trading.publications p WHERE p.catalog_id=catalog;
    INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
    INSERT INTO trading.facility_quantity_plans VALUES(catalog,l.id,gen_random_uuid(),coalesce(plan.revision,0)+1,plan.id,source.id,btrim(body->>'evidence'),publication,as_of,actor) RETURNING * INTO plan;
    INSERT INTO trading.facility_gas_allocations SELECT catalog,l.id,plan.id,(x.value->>'obligationId')::uuid,
     t.trade_id,t.trade_revision,t.instrument_id,(x.value->>'quantity')::numeric FROM jsonb_array_elements(lines)x
     CROSS JOIN LATERAL security.facility_gas_target(catalog,(x.value->>'obligationId')::uuid,gas.day,as_of)t;
   END IF;
   previous_plan=plan.previous_id;
   PERFORM security.require_facility_plan_targets(catalog,previous_plan,plan.id);
   result=jsonb_build_object('ledgerId',l.id,'revisionId',plan.id,'revision',plan.revision);
  END IF;
 END IF;
 -- Force deferred invariant checks before returning a successful outcome.
 SET CONSTRAINTS trading.valid_facility_quantity_header,trading.valid_facility_quantity_revision,trading.valid_facility_quantity_limit,trading.valid_facility_quantity_plan,trading.valid_facility_quantity_allocation IMMEDIATE;
 SET CONSTRAINTS trading.valid_facility_quantity_header,trading.valid_facility_quantity_revision,trading.valid_facility_quantity_limit,trading.valid_facility_quantity_plan,trading.valid_facility_quantity_allocation DEFERRED;
 INSERT INTO trading.facility_quantity_commands VALUES(catalog,operation,actor,l.id,payload,result,
  CASE WHEN body->>'kind'='create' THEN(body->>'obligationId')::uuid END,previous_plan,
  CASE WHEN body->>'kind'='allocate' THEN plan.id END);
 PERFORM security.require_facility_quantity_command(catalog,body->>'kind');
 PERFORM security.require_facility_plan_targets(catalog,previous_plan,plan.id);
 RETURN result;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR datetime_field_overflow OR invalid_datetime_format OR invalid_parameter_value THEN
 RAISE EXCEPTION 'facility_quantity_input';
END$$;

CREATE FUNCTION security.read_facility_quantity_ledger(catalog uuid,ledger uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE l trading.facility_quantity_ledgers;source trading.facility_quantity_revisions;plan trading.facility_quantity_plans;target record;result jsonb;
 configured trading.facility_quantity_limits;
BEGIN
 PERFORM security.require_facility_quantity_read(catalog);
 SELECT * INTO l FROM trading.facility_quantity_ledgers h WHERE(h.catalog_id,h.id)=(catalog,ledger)
 AND security.trading_visible(h.catalog_id,h.publication_order,h.effective_at);
 IF l.id IS NULL THEN RAISE EXCEPTION 'facility_quantity_ledger';END IF;
 IF NOT security.reference_version_visible(l.reference_catalog_id,l.reference_revision_id) THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO source FROM trading.facility_quantity_revisions r WHERE(r.catalog_id,r.ledger_id)=(catalog,ledger)
 AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at) ORDER BY revision DESC LIMIT 1;
 SELECT * INTO plan FROM trading.facility_quantity_plans p WHERE(p.catalog_id,p.ledger_id)=(catalog,ledger)
 AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at) ORDER BY revision DESC LIMIT 1;
 SELECT * INTO configured FROM trading.facility_quantity_limits r WHERE(r.catalog_id,r.ledger_id)=(catalog,ledger)
 AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at) ORDER BY revision DESC LIMIT 1;
 FOR target IN SELECT DISTINCT o.trade_id FROM trading.facility_gas_allocations a JOIN trading.gas_delivery_obligations o ON(o.catalog_id,o.id)=(a.catalog_id,a.obligation_id)
 WHERE(a.catalog_id,a.plan_id)=(catalog,plan.id) LOOP
  PERFORM security.require_gas_quantity_trade(catalog,target.trade_id,false);
 END LOOP;
 result=jsonb_build_object('ledger',to_jsonb(l),'source',CASE WHEN source.id IS NOT NULL THEN to_jsonb(source)||jsonb_build_object('quantity',trim_scale(source.quantity)::text) END,
  'labels',(SELECT jsonb_build_object('facility',v.facility,'point',v.point,'commodity',v.commodity,'unit',v.unit,'legalEntity',v.legal_entity,'day',v.day) FROM trading.facility_quantity_scope_rows v WHERE(v.catalog_id,v.id)=(catalog,ledger)),
  'plan',CASE WHEN plan.id IS NOT NULL THEN to_jsonb(plan) END,
  'limit',CASE WHEN configured.id IS NOT NULL THEN to_jsonb(configured)||jsonb_build_object('quantity',trim_scale(configured.quantity)::text) END,
  'sourceHistory',(SELECT coalesce(jsonb_agg(to_jsonb(x)||jsonb_build_object('quantity',trim_scale(x.quantity)::text) ORDER BY x.revision DESC),'[]') FROM(
   SELECT r.* FROM trading.facility_quantity_revisions r WHERE(r.catalog_id,r.ledger_id)=(catalog,ledger) AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at) ORDER BY revision DESC LIMIT 100)x),
  'planHistory',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.revision DESC),'[]') FROM(
   SELECT p.* FROM trading.facility_quantity_plans p WHERE(p.catalog_id,p.ledger_id)=(catalog,ledger) AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at) ORDER BY revision DESC LIMIT 100)x),
  'limitHistory',(SELECT coalesce(jsonb_agg(to_jsonb(x)||jsonb_build_object('quantity',trim_scale(x.quantity)::text) ORDER BY x.revision DESC),'[]') FROM(
   SELECT r.* FROM trading.facility_quantity_limits r WHERE(r.catalog_id,r.ledger_id)=(catalog,ledger) AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at) ORDER BY revision DESC LIMIT 100)x),
  'allocations',(SELECT coalesce(jsonb_agg(jsonb_build_object('obligationId',a.obligation_id,'tradeId',a.trade_id,'tradeRevision',a.trade_revision,
   'instrumentId',a.instrument_id,'quantity',trim_scale(a.quantity)::text,'counterparty',cp.name,'book',b.name,'portfolio',f.name,'sideCode',m.code) ORDER BY a.obligation_id),'[]')
   FROM trading.facility_gas_allocations a JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision,r.instrument_id)=(a.catalog_id,a.trade_id,a.trade_revision,a.instrument_id)
   JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id) JOIN trading.portfolios f ON(f.catalog_id,f.id)=(b.catalog_id,b.portfolio_id)
   JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
   JOIN trading.gas_delivery_obligations o ON(o.catalog_id,o.id)=(a.catalog_id,a.obligation_id)
   JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
   WHERE(a.catalog_id,a.plan_id)=(catalog,plan.id)),
  'requiresReconciliation',plan.id IS NOT NULL AND(plan.source_revision_id IS DISTINCT FROM source.id OR EXISTS(
   SELECT 1 FROM trading.facility_gas_allocations a JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id)=(a.catalog_id,a.trade_id)
   WHERE(a.catalog_id,a.plan_id)=(catalog,plan.id) AND r.revision>a.trade_revision AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at))),
  'scope','manual_facility_quantities_and_allocation_plans_only','billingEffect','unchanged');
 PERFORM security.require_facility_quantities(catalog,false);
 RETURN result;
END$$;

CREATE FUNCTION security.facility_quantity_status(catalog uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.facility_quantity_commands;actor uuid;trade uuid;l trading.facility_quantity_ledgers;BEGIN
 PERFORM security.require_facility_quantities(catalog,false);
 SELECT c.actor_id INTO actor FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.expires_at>clock_timestamp();
 SELECT * INTO prior FROM trading.facility_quantity_commands c WHERE(c.catalog_id,c.operation_id,c.actor_id)=(catalog,operation,actor);
 IF prior.operation_id IS NULL THEN RETURN jsonb_build_object('found',false);END IF;
 PERFORM security.require_facility_quantity_command(catalog,prior.request#>>'{body,kind}');
 SELECT * INTO STRICT l FROM trading.facility_quantity_ledgers h WHERE(h.catalog_id,h.id)=(catalog,prior.ledger_id);
 IF NOT security.reference_version_visible(l.reference_catalog_id,l.reference_revision_id) THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_facility_plan_targets(catalog,prior.previous_plan_id,prior.result_plan_id);
 IF prior.create_obligation_id IS NOT NULL THEN
  SELECT o.trade_id INTO trade FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.id)=(catalog,prior.create_obligation_id);
  PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 END IF;
 PERFORM security.require_facility_quantity_command(catalog,prior.request#>>'{body,kind}');
 RETURN prior.outcome;
END$$;

CREATE FUNCTION security.facility_quantity_directory(catalog uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$ DECLARE result jsonb;BEGIN
 PERFORM security.require_facility_quantity_read(catalog);
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.native_from DESC,x.facility),'[]') INTO result FROM(
  SELECT l.* FROM trading.facility_quantity_scope_rows l
  WHERE l.catalog_id=catalog AND security.trading_visible(l.catalog_id,l.publication_order,l.effective_at)
  AND security.reference_version_visible(l.reference_catalog_id,l.reference_revision_id)
  ORDER BY l.native_from DESC,l.id LIMIT 201)x;
 PERFORM security.require_facility_quantity_read(catalog);RETURN result;
END$$;

CREATE FUNCTION security.facility_quantity_choices(catalog uuid,ledger uuid,as_of timestamptz) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE candidate record;t trading.facility_gas_target;result jsonb='[]';label jsonb;BEGIN
 PERFORM security.require_facility_quantity_read(catalog);
 FOR candidate IN SELECT DISTINCT o.id obligation,g.operating_days[1]+date '0001-01-01' AS native_day,m.code side_code
  FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
  JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot)=(o.catalog_id,o.instrument_id,o.slot)
  JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id)=(s.catalog_id,s.schedule_id)
  WHERE o.catalog_id=catalog AND cardinality(g.operating_days)=1
   AND(ledger IS NULL OR g.operating_days[1]+date '0001-01-01'=(SELECT s.day FROM trading.facility_gas_delivery_scopes s WHERE(s.catalog_id,s.ledger_id)=(catalog,ledger)))
  ORDER BY o.id,native_day LOOP
  BEGIN
   IF ledger IS NULL THEN
    SELECT * INTO STRICT t FROM security.facility_gas_target(catalog,candidate.obligation,candidate.native_day,as_of);
   ELSE
    t=security.facility_gas_target_for_ledger(catalog,ledger,candidate.obligation,as_of,NULL);
    IF EXISTS(SELECT 1 FROM trading.facility_gas_targets a WHERE(a.catalog_id,a.obligation_id,a.day)=(catalog,candidate.obligation,candidate.native_day) AND a.ledger_id<>ledger) THEN CONTINUE;END IF;
   END IF;
  EXCEPTION WHEN no_data_found OR too_many_rows OR insufficient_privilege THEN CONTINUE;
   WHEN raise_exception THEN IF SQLERRM IN('facility_quantity_target','gas_quantity_trade') THEN CONTINUE;ELSE RAISE;END IF;
  END;
  SELECT jsonb_build_object('point',p.name,'commodity',c.name,'unit',u.symbol,'book',b.name,'portfolio',f.name,'counterparty',cp.name) INTO STRICT label
   FROM ref_data.locations p JOIN ref_data.commodities c ON(c.catalog_id,c.revision_id,c.code)=(p.catalog_id,p.revision_id,t.product_code)
   JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.catalog_id,p.revision_id,t.quantity_code)
   JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
   JOIN trading.gas_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision,r.instrument_id)=(catalog,t.trade_id,t.trade_revision,t.instrument_id)
   JOIN trading.books b ON(b.catalog_id,b.id)=(r.catalog_id,r.book_id) JOIN trading.portfolios f ON(f.catalog_id,f.id)=(b.catalog_id,b.portfolio_id)
   JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
   WHERE(p.catalog_id,p.revision_id,p.code)=(t.reference_catalog_id,t.reference_revision_id,t.point_code);
  result=result||jsonb_build_array(to_jsonb(t)||label||jsonb_build_object('obligationId',candidate.obligation,'day',candidate.native_day,'sideCode',candidate.side_code,
   'quantity_n',trim_scale(t.quantity_n)::text,'quantity_d',trim_scale(t.quantity_d)::text));
  EXIT WHEN jsonb_array_length(result)>=1001;
 END LOOP;
 PERFORM security.require_facility_quantities(catalog,false);RETURN result;
END$$;

-- Reuse the existing opaque account/workspace binding; receipts retain this
-- owner even if another tab changes the authenticated preview profile.
CREATE FUNCTION security.facility_quantity_owner(catalog uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$ BEGIN
 PERFORM security.require_facility_quantities(catalog,false);
 RETURN presentation.preference_owner(security.presentation_actor(),'facility:'||catalog::text);
END$$;

-- An ordinary trade reader sees its own delivery track, never a facility pool,
-- another counterparty, a residual or a fingerprint of other allocations.
CREATE FUNCTION security.read_trade_facility_allocations(catalog uuid,trade uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND c.market_catalog_id=catalog AND c.market_context_id IS NOT NULL AND c.trading_as_of IS NOT NULL)
 THEN RAISE EXCEPTION 'gas_quantity_context';END IF;
 WITH head AS(
  SELECT r.* FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at) ORDER BY r.revision DESC LIMIT 1
 ), enrolled AS(
  SELECT t.*,m.code FROM head r JOIN trading.gas_delivery_obligations o ON(o.catalog_id,o.trade_id,o.instrument_id)=(r.catalog_id,r.trade_id,r.instrument_id)
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
  JOIN trading.facility_gas_targets t ON(t.catalog_id,t.obligation_id)=(o.catalog_id,o.id)
  WHERE EXISTS(SELECT 1 FROM trading.facility_gas_allocations a JOIN trading.facility_quantity_plans p ON(p.catalog_id,p.ledger_id,p.id)=(a.catalog_id,a.ledger_id,a.plan_id)
   WHERE(a.catalog_id,a.obligation_id,a.trade_id,a.instrument_id)=(catalog,o.id,trade,r.instrument_id)
   AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at))
 )
 SELECT coalesce(jsonb_agg(jsonb_build_object('obligationId',e.obligation_id,'sideCode',e.code,'day',e.day,
  'quantity',trim_scale(a.quantity)::text,'unit',v.unit,'state',CASE WHEN a.obligation_id IS NULL THEN 'no_current_allocation' ELSE 'planned' END)
  ORDER BY e.day,e.code),'[]') INTO result FROM enrolled e
 JOIN trading.facility_quantity_scope_rows v ON(v.catalog_id,v.id)=(e.catalog_id,e.ledger_id)
 CROSS JOIN LATERAL(SELECT p.id FROM trading.facility_quantity_plans p WHERE(p.catalog_id,p.ledger_id)=(e.catalog_id,e.ledger_id)
  AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at) ORDER BY p.revision DESC LIMIT 1)p
 LEFT JOIN trading.facility_gas_allocations a ON(a.catalog_id,a.plan_id,a.obligation_id,a.trade_id)=(catalog,p.id,e.obligation_id,trade)
 WHERE security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id)
 AND security.trading_visible(v.catalog_id,v.publication_order,v.effective_at);
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);RETURN result;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE(n.nspname,p.proname) IN(('security','require_facility_quantities'),('security','facility_gas_target'),
 ('security','require_facility_quantity_command'),
 ('security','require_facility_quantity_read'),
 ('security','require_facility_plan_targets'),
 ('security','facility_gas_target_for_ledger'),('security','facility_quantity_choices'),
 ('security','facility_quantity_status'),('security','facility_quantity_directory'),
 ('security','facility_quantity_owner'),('security','read_trade_facility_allocations'),
 ('security','record_facility_quantity_command'),('security','read_facility_quantity_ledger'),
 ('trading','guard_facility_quantity_plan'),('trading','guard_facility_quantity_revision'),
 ('trading','require_facility_quantity_writer'),('trading','guard_facility_quantity_header')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.record_facility_quantity_command(uuid,uuid,jsonb,timestamptz),
 security.require_facility_quantities(uuid,boolean),
 security.facility_quantity_owner(uuid),security.read_trade_facility_allocations(uuid,uuid),
 security.read_facility_quantity_ledger(uuid,uuid),security.facility_quantity_status(uuid,uuid),
 security.facility_quantity_directory(uuid),security.facility_quantity_choices(uuid,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
