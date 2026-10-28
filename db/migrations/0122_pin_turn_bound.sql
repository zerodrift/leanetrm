-- A turn's pinned As of frame may hold up to 256 KiB of JSON (D256). A close that pins about a thousand prices writes
-- more than the 64 KiB 0072 allowed, and every turn asked over such a frame was refused turn_input before its model was
-- called. Only the bound changes. A database already carrying this exact definition is left as it is.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
-- Fail closed unless the definition is 0072's, or already this one, with its owner and privileges.
DO $$
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure('assistant.pin_turn(uuid,bytea,date,jsonb,integer)')
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex') IN('205d78e0a45ebfe2fbc8f83381fb1b43ebbcacec9a8a8d5c39c51e724544c04f','7b49e5dc87323dbbe699dab03cb8494459e9319037140cb04c5c1e4ecc091429')
   AND p.proacl=ARRAY['openetrm_security_owner=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner']::aclitem[])
 THEN RAISE EXCEPTION 'pin_turn_bound: unexpected helper %','assistant.pin_turn(uuid,bytea,date,jsonb,integer)' USING ERRCODE='55000';END IF;
END$$;

-- As in 0072, with pins of up to 262,144 bytes.
CREATE OR REPLACE FUNCTION assistant.pin_turn(turn uuid,token bytea,effective_date date,pins jsonb,reserve integer) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.grant_actor(true);t assistant.turns;x market_data.temporal_contexts;context uuid;
BEGIN
 t:=assistant.fenced_turn(turn,token);
 -- The wait can outlive the authority.
 c:=assistant.grant_actor(true);
 IF t.owner_id<>c.actor_id OR(t.tenant_id,t.workspace_id)<>(c.tenant_id,c.workspace_id) THEN RAISE EXCEPTION 'agent_runtime_unavailable';END IF;
 IF t.recorded_at IS NOT NULL OR t.state NOT IN('queued','cancel_requested') THEN RAISE EXCEPTION 'turn_state_conflict';END IF;
 IF t.state='cancel_requested' THEN RETURN jsonb_build_object('turn',assistant.owner_turn(assistant.end_turn_as(t,'cancelled',t.cancel_code)));END IF;
 IF(t.frame_id IS NULL)<>(pins IS NULL) OR pins IS NOT NULL AND(jsonb_typeof(pins)<>'object' OR pins->>'frameId' IS DISTINCT FROM t.frame_id::text
   OR(SELECT array_agg(key ORDER BY key COLLATE "C") FROM jsonb_object_keys(pins) key)<>ARRAY['at','curveContracts','frameId','frozenObservationIds','name',
    'observationIds','resolvesLatestCorrection','swapMarketSelections']
   OR jsonb_typeof(pins->'at')<>'string' OR pins->>'at'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}([.][0-9]{1,7})?(Z|[+-][0-9]{2}:[0-9]{2})$'
   OR jsonb_typeof(pins->'name')<>'string' OR btrim(pins->>'name')=''
   OR NOT assistant.is_uuid_array(pins->'observationIds') OR NOT assistant.is_uuid_array(pins->'frozenObservationIds')
   OR jsonb_typeof(pins->'curveContracts')<>'object' OR jsonb_typeof(pins->'swapMarketSelections') NOT IN('object','null')
   OR jsonb_typeof(pins->'resolvesLatestCorrection')<>'boolean' OR octet_length(pins::text)>262144) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 IF pins IS NOT NULL AND(EXISTS(SELECT 1 FROM jsonb_array_elements_text(pins->'frozenObservationIds') f WHERE NOT(pins->'observationIds')?f)
   OR EXISTS(SELECT 1 FROM jsonb_each(pins->'curveContracts') e WHERE e.key='' OR NOT assistant.is_uuid_array(e.value))
   OR jsonb_typeof(pins->'swapMarketSelections')='object' AND EXISTS(SELECT 1 FROM jsonb_each(pins->'swapMarketSelections') e
    WHERE NOT assistant.is_uuid_array(jsonb_build_array(e.key)) OR NOT assistant.is_uuid_array(e.value))) THEN
  RAISE EXCEPTION 'turn_input';
 END IF;
 -- The frame's instant is one this database reads.
 BEGIN
  PERFORM(pins->>'at')::timestamptz;
 EXCEPTION WHEN datetime_field_overflow OR invalid_datetime_format OR invalid_time_zone_displacement_value THEN RAISE EXCEPTION 'turn_input';
 END;
 IF t.knowledge_mode='current' THEN
  IF effective_date IS NULL OR reserve IS NULL OR reserve<0 THEN RAISE EXCEPTION 'turn_input';END IF;
  -- Every capture certifies under this lock, held to commit, so the count cannot move before this one does.
  PERFORM pg_advisory_xact_lock(730849102);
  c:=assistant.grant_actor(true);
  IF(SELECT count(*) FROM market_data.temporal_contexts y WHERE y.catalog_id=t.catalog_id)>=4096-reserve THEN RAISE EXCEPTION 'knowledge_budget_reserved';END IF;
  context:=security.capture_date_context(t.catalog_id,effective_date,NULL);
 ELSE
  IF NOT security.market_access(t.catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current market read authority' USING ERRCODE='42501';END IF;
  context:=t.requested_context;
 END IF;
 SELECT y.* INTO x FROM market_data.temporal_contexts y WHERE(y.catalog_id,y.id)=(t.catalog_id,context);
 IF x.id IS NULL THEN RAISE EXCEPTION 'temporal_context';END IF;
 UPDATE assistant.turns y SET context_id=x.id,cutoff=x.known_at,pins=pin_turn.pins,recorded_at=clock_timestamp() WHERE y.id=t.id RETURNING y.* INTO t;
 RETURN jsonb_build_object('turn',assistant.owner_turn(t));
END $$;

COMMIT;
