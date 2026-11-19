-- Return the original fact when an existing spread mark or gas plan is recorded
-- again after a correction. For a legacy duplicate, the lowest revision is the
-- original. A distinct fact still checks the latest revision before publication.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

DO $migration$
DECLARE
 patch record;
 writer pg_proc;
 definition text;
BEGIN
 FOR patch IN SELECT * FROM (VALUES
  ('security.record_spread_marks(uuid,jsonb,timestamptz)'::regprocedure,
   '1401c34c6425cf4dc9a2faf4943ff5b7',
$before$  SELECT * INTO head FROM trading.spread_marks m WHERE(m.catalog_id,m.definition_revision_id,m.long_curve_code,m.short_curve_code,m.bucket_month)=
   (catalog,definition,body->>'longCurve',body->>'shortCurve',month) ORDER BY m.revision DESC LIMIT 1;
  -- The same value at the same instant is the fact already recorded, whatever the caller believed the head to be.
  IF head.id IS NOT NULL AND(head.long_volatility,head.short_volatility,head.correlation,head.effective_at)=(long_vol,short_vol,rho,effective) THEN
   results=results||jsonb_build_array(jsonb_build_object('month',item->>'month','markId',head.id,'revision',head.revision,'changed',false));CONTINUE;
  END IF;$before$,
$after$  SELECT m.* INTO head FROM trading.spread_marks m
  WHERE (m.catalog_id,m.definition_revision_id,m.long_curve_code,m.short_curve_code,m.bucket_month)=
        (catalog,definition,body->>'longCurve',body->>'shortCurve',month)
    AND (m.long_volatility,m.short_volatility,m.correlation,m.effective_at)=(long_vol,short_vol,rho,effective)
  ORDER BY m.revision LIMIT 1;
  IF FOUND THEN
   results=results||jsonb_build_array(jsonb_build_object('month',item->>'month','markId',head.id,'revision',head.revision,'changed',false));CONTINUE;
  END IF;
  SELECT m.* INTO head FROM trading.spread_marks m
  WHERE (m.catalog_id,m.definition_revision_id,m.long_curve_code,m.short_curve_code,m.bucket_month)=
        (catalog,definition,body->>'longCurve',body->>'shortCurve',month)
  ORDER BY m.revision DESC LIMIT 1;$after$),
  ('security.record_gas_storage_plan(uuid,jsonb,timestamptz)'::regprocedure,
   'c545aabc8486235e4b46d97c13f1d7cb',
$before$ SELECT * INTO head FROM trading.gas_storage_plans p WHERE(p.catalog_id,p.instrument_id)=(catalog,instrument) ORDER BY p.revision DESC LIMIT 1;
 IF head.id IS NOT NULL AND head.flows=flows AND(head.source,head.inventory_step,head.effective_at) IS NOT DISTINCT FROM(source->>'kind',step,effective) THEN
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'planId',head.id,'revision',head.revision,'changed',false);
 ELSE$before$,
$after$ SELECT (candidate.plan).* INTO head
 FROM (SELECT p AS plan FROM trading.gas_storage_plans p
       WHERE (p.catalog_id,p.instrument_id)=(catalog,instrument)) candidate
 WHERE (candidate.plan).flows=flows
   AND ((candidate.plan).source,(candidate.plan).inventory_step,(candidate.plan).effective_at)
       IS NOT DISTINCT FROM (source->>'kind',step,effective)
 ORDER BY (candidate.plan).revision LIMIT 1;
 IF FOUND THEN
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'planId',head.id,'revision',head.revision,'changed',false);
 ELSE
  SELECT p.* INTO head FROM trading.gas_storage_plans p
  WHERE (p.catalog_id,p.instrument_id)=(catalog,instrument) ORDER BY p.revision DESC LIMIT 1;$after$)
 ) AS changes(signature,source_md5,old_body,new_body)
 LOOP
  SELECT * INTO STRICT writer FROM pg_proc WHERE oid=patch.signature;
  IF md5(writer.prosrc)<>patch.source_md5 OR writer.proowner<>'openetrm_security_owner'::regrole
   OR NOT writer.prosecdef OR writer.provolatile<>'v' OR writer.proisstrict OR writer.proleakproof
   OR writer.proparallel<>'u' OR writer.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR (SELECT array_agg(a::text ORDER BY a::text) FROM unnest(coalesce(writer.proacl,acldefault('f',writer.proowner))) a)
      IS DISTINCT FROM ARRAY['openetrm_runtime=X/openetrm_security_owner','openetrm_security_owner=X/openetrm_security_owner']
  THEN RAISE EXCEPTION 'historical_fact_writer_predecessor: %',patch.signature;END IF;
  definition=pg_get_functiondef(writer.oid);
  IF (length(definition)-length(replace(definition,patch.old_body,'')))<>length(patch.old_body)
  THEN RAISE EXCEPTION 'historical_fact_writer_body: %',patch.signature;END IF;
  EXECUTE replace(definition,patch.old_body,patch.new_body);
 END LOOP;
END$migration$;
COMMIT;
