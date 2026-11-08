-- Current consumer: the Facilities page's catalog-wide target choices (security.facility_quantity_choices).
-- That read resolved each single-day gas target and re-proved its trade's authority in a separate
-- exception block, so its cost grew with gas days; daily gas programs carry one schedule row per day.
-- A target is now resolved set-based, and each trade's authority is proved once. Answers and refusals
-- are unchanged. The ledger-scoped branch keeps its per-candidate checks: it is bounded by the ledger's day.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE helper record;BEGIN
 FOR helper IN SELECT * FROM(VALUES
  ('security.facility_gas_target(uuid,uuid,date,timestamp with time zone)','08fb5a096a0d57ea7d315dac32da9ea773182d5a311dbacdba0ec31b894d2b89','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('security.facility_gas_target_for_ledger(uuid,uuid,uuid,timestamp with time zone,numeric)','be4b00875b9bf8b8b76c53da10d2426f9b582a5a5206132136dc5606b37be370','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('security.require_gas_quantity_trade(uuid,uuid,boolean)','e6451f77763f963995449650a0a59e8bf1a0d3da72eca423173470a4f1fcfa4e','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb),
  ('security.require_facility_quantity_read(uuid)','9d692c8ecab0408b1f07867d7097d06efbb94199e59a9b6c392c7ce8de5c9e37','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('security.require_facility_quantities(uuid,boolean)','252779ae490b1d13494a5689365adf5267de421d9e79af9b4df840aa7f0ab7ab','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb),
  ('security.facility_quantity_choices(uuid,uuid,timestamp with time zone)','17fdeac3dd4e7ba99d4724ab9edb72115b63f65531ceb30b831aa3d644d5d8b1','["openetrm_security_owner=X/openetrm_security_owner", "openetrm_runtime=X/openetrm_security_owner"]'::jsonb)
 )x(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature) AND p.proowner='openetrm_security_owner'::regrole
   AND to_jsonb(p.proacl)=helper.acl AND to_jsonb(p.proconfig)='["search_path=pg_catalog"]'::jsonb AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash)
  THEN RAISE EXCEPTION 'facility_choices_upgrade: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;

-- The one definition of a facility gas target, for any number of obligation-days in one statement
-- and snapshot. A trade's latest visible revision and a schedule's publication are each decided once,
-- however many gas days they carry. It returns every matching row; callers prove trade authority
-- and treat anything but exactly one row for an obligation-day as no target.
CREATE FUNCTION security.resolve_facility_gas_targets(catalog uuid,obligations uuid[],flow_days date[],as_of timestamptz)
RETURNS TABLE(obligation_id uuid,native_day date,target trading.facility_gas_target)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN
 RETURN QUERY WITH requested AS MATERIALIZED(
  SELECT DISTINCT o.id,u.day,o.trade_id,o.instrument_id,o.slot
  FROM unnest(obligations,flow_days)u(obligation,day) JOIN trading.gas_delivery_obligations o ON(o.catalog_id,o.id)=(catalog,u.obligation)),
 heads AS MATERIALIZED(
  SELECT h.* FROM(SELECT DISTINCT q.trade_id FROM requested q)t CROSS JOIN LATERAL(
   SELECT r.* FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,t.trade_id)
   AND(security.current_write_context() OR security.trading_visible(r.catalog_id,r.publication_order,r.effective_at))
   ORDER BY r.revision DESC LIMIT 1)h
  WHERE h.effective_at<=as_of
  AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(h.catalog_id,h.publication_order))),
 members AS MATERIALIZED(
  SELECT d.id,d.day,h.trade_id,h.revision,h.instrument_id,h.legal_entity_id,h.legal_entity_revision,h.quantity,s.schedule_id,
   m.product_code,m.pipeline_code,m.zone_code,m.point_code,m.direction,m.ratio_n,m.ratio_d
  FROM requested d JOIN heads h ON(h.trade_id,h.instrument_id)=(d.trade_id,d.instrument_id)
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(catalog,d.instrument_id,d.slot)
  JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot)=(m.catalog_id,m.instrument_id,m.slot)
  WHERE m.role='physical'),
 schedules AS MATERIALIZED(
  SELECT p.schedule_id FROM(SELECT DISTINCT v.schedule_id FROM members v)v
  JOIN trading.side_schedule_publications p ON(p.catalog_id,p.schedule_id)=(catalog,v.schedule_id)
  WHERE p.effective_at<=as_of
  AND(security.current_write_context() OR security.trading_visible(p.catalog_id,p.publication_order,p.effective_at))
  AND EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order))),
 quantity_rows AS MATERIALIZED(
  SELECT v.schedule_id,q.grid_ordinal,q.unit_catalog_id,q.unit_revision_id,q.unit_code,q.parameters
  FROM schedules v JOIN trading.side_schedule_roots r ON(r.catalog_id,r.schedule_id,r.measure)=(catalog,v.schedule_id,'schedule_quantity')
  JOIN trading.side_schedule_rows q ON(q.catalog_id,q.schedule_id,q.ordinal)=(r.catalog_id,r.schedule_id,r.row_ordinal)
  WHERE q.operation='quantity_literal'),
 schedule_days AS MATERIALIZED(
  SELECT q.schedule_id,g.operating_days,q.unit_catalog_id,q.unit_revision_id,q.unit_code,q.parameters,g.origin,g.selection,g.lengths,g.step_ticks
  FROM quantity_rows q JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(catalog,q.schedule_id,q.grid_ordinal)
  WHERE cardinality(g.selection)=1 AND g.lengths=ARRAY[1])
 SELECT d.id,d.day,ROW(d.trade_id,d.revision,d.instrument_id,g.unit_catalog_id,g.unit_revision_id,g.unit_code::text,d.product_code::text,
  d.pipeline_code::text,d.zone_code::text,d.point_code::text,d.legal_entity_id,d.legal_entity_revision,d.direction,
  g.origin+(g.selection[1]*g.step_ticks/10)*interval '1 microsecond',
  g.origin+((g.selection[1]+g.lengths[1])*g.step_ticks/10)*interval '1 microsecond',
  (g.parameters->>'numerator')::numeric*d.quantity*d.ratio_n,(g.parameters->>'denominator')::numeric*d.ratio_d)::trading.facility_gas_target
 FROM members d JOIN schedule_days g ON g.schedule_id=d.schedule_id AND g.operating_days=ARRAY[d.day-date '0001-01-01'];
END$$;
ALTER FUNCTION security.resolve_facility_gas_targets(uuid,uuid[],date[],timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.resolve_facility_gas_targets(uuid,uuid[],date[],timestamptz) FROM PUBLIC;

-- The resolver's materialized stages carry no useful row estimates, and a nested loop over them is
-- quadratic in requested obligation-days. A catalog-wide request hashes them instead, with a custom
-- plan because plans cached by single-target calls ignore this setting. Single-target calls keep their index plans.
CREATE FUNCTION security.resolve_facility_gas_targets_in_bulk(catalog uuid,obligations uuid[],flow_days date[],as_of timestamptz)
RETURNS TABLE(obligation_id uuid,native_day date,target trading.facility_gas_target)
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off SET plan_cache_mode=force_custom_plan AS $$
 SELECT * FROM security.resolve_facility_gas_targets(catalog,obligations,flow_days,as_of);
$$;
ALTER FUNCTION security.resolve_facility_gas_targets_in_bulk(uuid,uuid[],date[],timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.resolve_facility_gas_targets_in_bulk(uuid,uuid[],date[],timestamptz) FROM PUBLIC;

DO $patch$ DECLARE change record;definition text;BEGIN
 FOR change IN SELECT * FROM(VALUES
  ('security.facility_gas_target(uuid,uuid,date,timestamp with time zone)',$b$ RETURN QUERY WITH r AS MATERIALIZED(
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
$b$,
   $a$ RETURN QUERY SELECT(x.target).* FROM security.resolve_facility_gas_targets(catalog,ARRAY[obligation],ARRAY[flow_day],as_of)x;
$a$),
  ('security.facility_quantity_choices(uuid,uuid,timestamp with time zone)',$b$DECLARE candidate record;t trading.facility_gas_target;result jsonb='[]';label jsonb;BEGIN
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
$b$,
   $a$DECLARE candidate record;t trading.facility_gas_target;result jsonb='[]';label jsonb;allowed uuid[]='{}';refused uuid[]='{}';BEGIN
 PERFORM security.require_facility_quantity_read(catalog);
 FOR candidate IN WITH requested AS MATERIALIZED(
   SELECT DISTINCT o.id obligation,o.trade_id,g.operating_days[1]+date '0001-01-01' AS native_day,m.code side_code
   FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
   JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot)=(o.catalog_id,o.instrument_id,o.slot)
   JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id)=(s.catalog_id,s.schedule_id)
   WHERE o.catalog_id=catalog AND cardinality(g.operating_days)=1
    AND(ledger IS NULL OR g.operating_days[1]+date '0001-01-01'=(SELECT s.day FROM trading.facility_gas_delivery_scopes s WHERE(s.catalog_id,s.ledger_id)=(catalog,ledger)))),
  resolved AS MATERIALIZED(
   SELECT x.obligation_id,x.native_day,x.target,count(*)OVER(PARTITION BY x.obligation_id,x.native_day) matches
   FROM(SELECT array_agg(r.obligation) obligations,array_agg(r.native_day) flow_days FROM requested r WHERE ledger IS NULL)q
   CROSS JOIN LATERAL security.resolve_facility_gas_targets_in_bulk(catalog,q.obligations,q.flow_days,as_of)x)
  SELECT r.*,x.target,x.matches FROM requested r LEFT JOIN resolved x ON(x.obligation_id,x.native_day)=(r.obligation,r.native_day)
  ORDER BY r.obligation,r.native_day LOOP
  IF ledger IS NULL THEN
   IF candidate.matches IS DISTINCT FROM 1 OR candidate.trade_id=ANY(refused) THEN CONTINUE;END IF;
   IF NOT candidate.trade_id=ANY(allowed) THEN
    BEGIN PERFORM security.require_gas_quantity_trade(catalog,candidate.trade_id,false);allowed=allowed||candidate.trade_id;
    EXCEPTION WHEN insufficient_privilege THEN refused=refused||candidate.trade_id;CONTINUE;
     WHEN raise_exception THEN IF SQLERRM='gas_quantity_trade' THEN refused=refused||candidate.trade_id;CONTINUE;ELSE RAISE;END IF;
    END;
   END IF;
   t=candidate.target;
  ELSE
   BEGIN
    t=security.facility_gas_target_for_ledger(catalog,ledger,candidate.obligation,as_of,NULL);
    IF EXISTS(SELECT 1 FROM trading.facility_gas_targets a WHERE(a.catalog_id,a.obligation_id,a.day)=(catalog,candidate.obligation,candidate.native_day) AND a.ledger_id<>ledger) THEN CONTINUE;END IF;
   EXCEPTION WHEN no_data_found OR too_many_rows OR insufficient_privilege THEN CONTINUE;
    WHEN raise_exception THEN IF SQLERRM IN('facility_quantity_target','gas_quantity_trade') THEN CONTINUE;ELSE RAISE;END IF;
   END;
  END IF;
$a$)
 )c(target,before_text,after_text) LOOP
  definition=pg_get_functiondef(change.target::regprocedure);
  IF(length(definition)-length(replace(definition,change.before_text,'')))/length(change.before_text)<>1 THEN RAISE EXCEPTION 'facility_choices_upgrade: unexpected definition %',change.target USING ERRCODE='55000';END IF;
  EXECUTE replace(definition,change.before_text,change.after_text);
 END LOOP;
END$patch$;
COMMIT;
