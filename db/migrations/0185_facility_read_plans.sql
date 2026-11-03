-- Facility reads decide visibility once per ledger and once per gas target.
-- With partial statistics (row counts without column statistics on some small
-- tables) the planner otherwise combines reference or schedule rows first and
-- repeats the per-row visibility checks for every combination: one ledger's
-- directory read checked visibility 13,284 times.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- Refuse drift in the reviewed predecessors.
DO $upgrade$
DECLARE previous text := $previous$
 SELECT l.*,f.name facility,p.name point,c.name commodity,u.symbol unit,e.name legal_entity,g.day
 FROM trading.facility_quantity_ledgers l JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(l.catalog_id,l.id)
 JOIN ref_data.locations f ON(f.catalog_id,f.revision_id,f.code)=(l.reference_catalog_id,l.reference_revision_id,l.facility_code)
 JOIN ref_data.locations p ON(p.catalog_id,p.revision_id,p.code)=(l.reference_catalog_id,l.reference_revision_id,l.flow_point_code)
 JOIN ref_data.commodities c ON(c.catalog_id,c.revision_id,c.code)=(l.reference_catalog_id,l.reference_revision_id,l.commodity_code)
 JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(l.reference_catalog_id,l.reference_revision_id,l.quantity_code)
 JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
 JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=(l.catalog_id,l.legal_entity_id,l.legal_entity_revision)
$previous$;
BEGIN
 EXECUTE 'CREATE TEMP VIEW openetrm_expected_facility_scope AS '||previous;
 IF pg_get_viewdef('trading.facility_quantity_scope_rows'::regclass,true)
       IS DISTINCT FROM pg_get_viewdef('pg_temp.openetrm_expected_facility_scope'::regclass,true)
    OR NOT EXISTS(SELECT 1 FROM pg_class WHERE oid='trading.facility_quantity_scope_rows'::regclass
       AND relkind='v' AND relowner='openetrm_security_owner'::regrole AND reloptions IS NULL
       AND relacl='{openetrm_security_owner=arwdDxt/openetrm_security_owner}'::aclitem[])
    OR NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure('security.facility_gas_target(uuid,uuid,date,timestamptz)')
       AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
       AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')='879c6b96d6aa719912652fd46e259a52a4263ae753a7437a14c1608df6d49299'
       AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[])
 THEN RAISE EXCEPTION 'facility_read_upgrade_mismatch' USING ERRCODE='55000';END IF;
 DROP VIEW pg_temp.openetrm_expected_facility_scope;
END
$upgrade$;

-- Foreign keys guarantee every label row (the ledger's commodity locations,
-- quantity specification and its unit, and legal entity revision) and the header
-- trigger guarantees the gas scope, so key lookups return the joined rows.
-- CREATE OR REPLACE preserves the owner, column identities and ACL.
CREATE OR REPLACE VIEW trading.facility_quantity_scope_rows AS
 SELECT l.*,
  (SELECT f.name FROM ref_data.locations f WHERE(f.catalog_id,f.revision_id,f.code)=(l.reference_catalog_id,l.reference_revision_id,l.facility_code)) facility,
  (SELECT p.name FROM ref_data.locations p WHERE(p.catalog_id,p.revision_id,p.code)=(l.reference_catalog_id,l.reference_revision_id,l.flow_point_code)) point,
  (SELECT c.name FROM ref_data.commodities c WHERE(c.catalog_id,c.revision_id,c.code)=(l.reference_catalog_id,l.reference_revision_id,l.commodity_code)) commodity,
  (SELECT u.symbol FROM ref_data.quantity_specifications q JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
   WHERE(q.catalog_id,q.revision_id,q.code)=(l.reference_catalog_id,l.reference_revision_id,l.quantity_code)) unit,
  (SELECT e.name FROM trading.legal_entities e WHERE(e.catalog_id,e.id,e.revision)=(l.catalog_id,l.legal_entity_id,l.legal_entity_revision)) legal_entity,
  g.day
 FROM trading.facility_quantity_ledgers l JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(l.catalog_id,l.id);

-- Select the trade's latest visible revision once, then the requested day's
-- schedule rows, and check their publications' visibility last, all in one
-- statement and snapshot. The returned rows, filters and refusals are unchanged;
-- CREATE OR REPLACE keeps the ACL.
CREATE OR REPLACE FUNCTION security.facility_gas_target(catalog uuid,obligation uuid,flow_day date,as_of timestamptz)
RETURNS SETOF trading.facility_gas_target
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE trade uuid;BEGIN
 SELECT o.trade_id INTO trade FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.id)=(catalog,obligation);
 IF trade IS NULL THEN RAISE EXCEPTION 'facility_quantity_target';END IF;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 RETURN QUERY WITH r AS MATERIALIZED(
  SELECT h.* FROM trading.gas_trade_revisions h WHERE(h.catalog_id,h.trade_id)=(catalog,trade)
  AND(security.current_write_context() OR security.trading_visible(h.catalog_id,h.publication_order,h.effective_at))
  ORDER BY h.revision DESC LIMIT 1),
 day_rows AS MATERIALIZED(
  SELECT r.revision,r.instrument_id,r.legal_entity_id,r.legal_entity_revision,r.quantity,
   r.catalog_id revision_catalog_id,r.publication_order revision_order,r.effective_at revision_effective_at,
   m.product_code,m.pipeline_code,m.zone_code,m.point_code,m.direction,m.ratio_n,m.ratio_d,
   q.unit_catalog_id,q.unit_revision_id,q.unit_code,q.parameters,g.origin,g.selection,g.lengths,g.step_ticks,
   p.catalog_id publication_catalog_id,p.publication_order,p.effective_at publication_effective_at
  FROM r JOIN trading.gas_delivery_obligations o ON o.instrument_id=r.instrument_id
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
  JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot)=(m.catalog_id,m.instrument_id,m.slot)
  JOIN trading.side_schedule_roots root ON(root.catalog_id,root.schedule_id,root.measure)=(s.catalog_id,s.schedule_id,'schedule_quantity')
  JOIN trading.side_schedule_rows q ON(q.catalog_id,q.schedule_id,q.ordinal)=(root.catalog_id,root.schedule_id,root.row_ordinal)
  JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(q.catalog_id,q.schedule_id,q.grid_ordinal)
  JOIN trading.side_schedule_publications p ON(p.catalog_id,p.schedule_id)=(s.catalog_id,s.schedule_id)
  WHERE(o.catalog_id,o.id)=(catalog,obligation) AND m.role='physical' AND q.operation='quantity_literal'
  AND g.operating_days=ARRAY[flow_day-date '0001-01-01'] AND cardinality(g.selection)=1 AND g.lengths=ARRAY[1]
  AND r.effective_at<=as_of AND p.effective_at<=as_of)
 SELECT trade,d.revision,d.instrument_id,d.unit_catalog_id,d.unit_revision_id,d.unit_code::text,d.product_code::text,
 d.pipeline_code::text,d.zone_code::text,d.point_code::text,d.legal_entity_id,d.legal_entity_revision,d.direction,
 d.origin+(d.selection[1]*d.step_ticks/10)*interval '1 microsecond',
 d.origin+((d.selection[1]+d.lengths[1])*d.step_ticks/10)*interval '1 microsecond',
 (d.parameters->>'numerator')::numeric*d.quantity*d.ratio_n,(d.parameters->>'denominator')::numeric*d.ratio_d
 FROM day_rows d
 WHERE(security.current_write_context() OR(security.trading_visible(d.revision_catalog_id,d.revision_order,d.revision_effective_at)
  AND security.trading_visible(d.publication_catalog_id,d.publication_order,d.publication_effective_at)))
 AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(d.revision_catalog_id,d.revision_order))
 AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(d.publication_catalog_id,d.publication_order));
END$$;
COMMIT;
