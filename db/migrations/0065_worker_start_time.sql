-- A worker start waits for every in-flight test open before it stamps its start, so a test it closes as
-- interrupted is never closed before it started (docs/ai-assistant.md §7.4). In 0062 the stamp could be taken
-- before the start waited for an open's share of the generation row. Nothing else changes; the replaced
-- definition keeps its owner and grants.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the lock helper the start relies on and the definition this migration replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.holds_worker_lock(integer)','b38b1bead5ffe6cd25cb6311d8f335b2d09f0be46bd505beea9584aca7f9da13',false),
  ('assistant.start_worker()','5bc9bce9a8dba838643833eadd8c390a67271d901045e675c19815cb4b45f89c',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'worker_start_time: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- As in 0062, with the generation row locked before the start is stamped.
CREATE OR REPLACE FUNCTION assistant.start_worker() RETURNS bigint
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE w assistant.worker_generation;
BEGIN
 IF NOT assistant.holds_worker_lock(pg_backend_pid()) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 PERFORM 1 FROM assistant.worker_generation x FOR UPDATE;
 UPDATE assistant.worker_generation x SET generation=x.generation+1,worker_pid=pg_backend_pid(),started_at=clock_timestamp() RETURNING x.* INTO w;
 INSERT INTO assistant.model_call_outcomes(call_id,closed_at,outcome,usage_status,uncached_input_source,cache_read_source,cache_write_source,output_source,reasoning_source)
  SELECT c.id,w.started_at,'interrupted','unknown','unknown','unknown','unknown','unknown','unknown' FROM assistant.model_calls c
  WHERE c.generation<w.generation AND NOT EXISTS(SELECT 1 FROM assistant.model_call_outcomes o WHERE o.call_id=c.id)
 ON CONFLICT(call_id) DO NOTHING;
 RETURN w.generation;
END $$;
COMMIT;
