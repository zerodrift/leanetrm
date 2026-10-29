-- These bounded metadata guards discourage nested-loop joins on fresh content.
-- Their mandatory lateral JSON loops can consequently have a high estimated
-- cost even for a small booking. Avoid optional LLVM compilation of the guards;
-- pricing remains in the managed engine and command deadlines remain unchanged.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

ALTER FUNCTION security.append_side_schedule_rows(uuid,uuid,uuid,jsonb) SET jit=off;
ALTER FUNCTION security.append_side_schedule_edges(uuid,uuid,uuid,jsonb) SET jit=off;
ALTER FUNCTION security.append_side_schedule_aux(uuid,uuid,uuid,text,jsonb) SET jit=off;
ALTER FUNCTION security.append_side_schedule_outputs(uuid,uuid,uuid,text,jsonb) SET jit=off;
ALTER FUNCTION security.seal_side_schedule(uuid,uuid,uuid,text) SET jit=off;
ALTER FUNCTION security.seal_side_schedule_request(uuid,uuid,jsonb) SET jit=off;

COMMIT;
