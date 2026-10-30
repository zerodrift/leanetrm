-- Dated indexed schedules require their qualified observation sources.
-- Stored obsolete programs are not rewritten or assigned publication policy.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $migration$
DECLARE definition text;changed text;
BEGIN
 SELECT pg_get_functiondef('security.seal_side_schedule(uuid,uuid,uuid,text)'::regprocedure) INTO definition;
 changed=replace(definition,$old$bool_or(port='quote' AND kind='quote') quote_port,$old$,
  $new$bool_or(port='quote' AND kind='quote') quote_port,bool_or(port='fixing' AND kind='index_fixing') fixing_port,$new$);
 IF changed=definition THEN RAISE EXCEPTION 'published_fixing_migration_shape';END IF;definition=changed;
 changed=replace(definition,$old$s.market_port AND s.index_port AND s.quote_port AND s.dated_ports$old$,
  $new$s.market_port AND s.index_port AND s.quote_port AND s.fixing_port AND s.dated_ports$new$);
 IF changed=definition THEN RAISE EXCEPTION 'published_fixing_migration_shape';END IF;definition=changed;
 changed=replace(definition,$old$ INSERT INTO trading.side_schedule_seals$old$,
  $new$ IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r
  WHERE(r.catalog_id,r.schedule_id,r.operation)=(catalog,schedule,'output_guard') AND r.grid_ordinal IS NULL
   AND r.parameters->>'firstFixingAt' IS DISTINCT FROM 'none')
 THEN RAISE EXCEPTION 'side_schedule_sources';END IF;
 INSERT INTO trading.side_schedule_seals$new$);
 IF changed=definition THEN RAISE EXCEPTION 'published_fixing_migration_shape';END IF;
 EXECUTE changed;
END $migration$;
COMMIT;
