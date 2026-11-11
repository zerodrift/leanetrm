-- Execution envelopes retain the same explicit business date and native source
-- selections as interactive reads. No earlier envelope format is interpreted.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION security.canonical_source_inputs(part jsonb) RETURNS jsonb
 LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE pins jsonb;frozen jsonb;roots jsonb;members jsonb;
BEGIN
 IF jsonb_typeof(part) IS DISTINCT FROM 'object'
 OR(SELECT array_agg(key ORDER BY key COLLATE "C") FROM jsonb_object_keys(part)key)
  IS DISTINCT FROM ARRAY['curveContracts','frozenObservationIds','observationIds','rootCurves','source']
 OR jsonb_typeof(part->'source') IS DISTINCT FROM 'object'
 OR(SELECT array_agg(key ORDER BY key COLLATE "C") FROM jsonb_object_keys(part->'source')key)
  IS DISTINCT FROM ARRAY['definitionRevisionId','referenceCatalogId','referenceRevisionId']
 OR jsonb_typeof(part->'observationIds') IS DISTINCT FROM 'array'
 OR jsonb_typeof(part->'frozenObservationIds') IS DISTINCT FROM 'array'
 OR jsonb_typeof(part->'rootCurves') IS DISTINCT FROM 'array'
 OR jsonb_typeof(part->'curveContracts') IS DISTINCT FROM 'object'
 THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 IF jsonb_array_length(part->'observationIds')>100000 OR jsonb_array_length(part->'rootCurves')>512
 OR(SELECT count(*)<>count(DISTINCT value::uuid) FROM jsonb_array_elements_text(part->'observationIds'))
 OR(SELECT count(*)<>count(DISTINCT value::uuid) FROM jsonb_array_elements_text(part->'frozenObservationIds'))
 OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements_text(part->'rootCurves'))
 OR EXISTS(SELECT 1 FROM jsonb_array_elements_text(part->'rootCurves')x WHERE NOT(part->'curveContracts')?x)
 OR EXISTS(SELECT 1 FROM jsonb_each(part->'curveContracts')x WHERE x.key='' OR jsonb_typeof(x.value)<>'array')
 THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 SELECT coalesce(jsonb_agg(value::uuid ORDER BY value::uuid),'[]'::jsonb) INTO pins FROM jsonb_array_elements_text(part->'observationIds');
 SELECT coalesce(jsonb_agg(value::uuid ORDER BY value::uuid),'[]'::jsonb) INTO frozen FROM jsonb_array_elements_text(part->'frozenObservationIds');
 SELECT coalesce(jsonb_agg(value ORDER BY value COLLATE "C"),'[]'::jsonb) INTO roots FROM jsonb_array_elements_text(part->'rootCurves');
 IF NOT pins @> frozen THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_each(part->'curveContracts')x WHERE jsonb_array_length(x.value)>100000
  OR(SELECT count(*)<>count(DISTINCT v.value::uuid) FROM jsonb_array_elements_text(x.value)v))
 THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 SELECT coalesce(jsonb_object_agg(x.key,(SELECT coalesce(jsonb_agg(v.value::uuid ORDER BY v.ordinality),'[]'::jsonb)
  FROM jsonb_array_elements_text(x.value) WITH ORDINALITY v)), '{}'::jsonb) INTO members FROM jsonb_each(part->'curveContracts')x;
 RETURN jsonb_build_object('source',jsonb_build_object('definitionRevisionId',(part#>>'{source,definitionRevisionId}')::uuid,
  'referenceCatalogId',(part#>>'{source,referenceCatalogId}')::uuid,'referenceRevisionId',(part#>>'{source,referenceRevisionId}')::uuid),
  'observationIds',pins,'frozenObservationIds',frozen,'curveContracts',members,'rootCurves',roots);
END;$$;
CREATE FUNCTION security.require_frame_inputs(catalog uuid,revision uuid,frame jsonb) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;selected market_data.revisions;day date;instant timestamptz;close uuid;
 sources jsonb;part record;normalized jsonb;expected jsonb;native jsonb;primary_part jsonb;native_close market_data.market_closes;
BEGIN
 SELECT * INTO c FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF c.id IS NULL THEN RAISE EXCEPTION 'temporal_inputs_denied' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(frame) IS DISTINCT FROM 'object' OR pg_column_size(frame)>8000000
 OR NOT frame ?& ARRAY['at','businessDate','source','sourceInputs','resolvesLatestCorrection']
 OR jsonb_typeof(frame->'at') IS DISTINCT FROM 'string' OR frame->>'at'!~'(Z|[+]00:00)$'
 OR jsonb_typeof(frame->'businessDate') IS DISTINCT FROM 'string' OR frame->>'businessDate'!~'^\d{4}-\d{2}-\d{2}$'
 OR jsonb_typeof(frame->'resolvesLatestCorrection') IS DISTINCT FROM 'boolean'
 THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 day=(frame->>'businessDate')::date;instant=(frame->>'at')::timestamptz;
 IF day IS NULL OR instant IS NULL OR NOT isfinite(day) OR NOT isfinite(instant) OR to_char(day,'YYYY-MM-DD')<>frame->>'businessDate'
 OR c.market_context_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t
  WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,c.market_context_id,day))
 THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 SELECT * INTO selected FROM market_data.revisions r WHERE(r.catalog_id,r.id)=(catalog,revision) AND r.state='sealed';
 IF NOT FOUND OR security.market_fact_visible(catalog,revision,NULL) IS NOT TRUE
 OR security.reference_version_visible(selected.reference_catalog_id,selected.reference_revision_id) IS NOT TRUE
 THEN RAISE EXCEPTION 'temporal_inputs_denied' USING ERRCODE='42501';END IF;
 sources=frame->'sourceInputs';
 IF sources='null'::jsonb THEN
  IF frame->>'originCloseId' IS NOT NULL THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
  sources=jsonb_build_object(revision::text,jsonb_build_object('source',jsonb_build_object('definitionRevisionId',revision,
   'referenceCatalogId',selected.reference_catalog_id,'referenceRevisionId',selected.reference_revision_id),
   'observationIds',frame->'observationIds','frozenObservationIds',frame->'frozenObservationIds','curveContracts',frame->'curveContracts','rootCurves','[]'::jsonb));
 END IF;
 IF jsonb_typeof(sources) IS DISTINCT FROM 'object' OR(SELECT count(*) FROM jsonb_each(sources)) NOT BETWEEN 1 AND 256
 THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 FOR part IN SELECT key,value FROM jsonb_each(sources) LOOP
  normalized=security.canonical_source_inputs(part.value);
  IF part.key::uuid IS DISTINCT FROM(normalized#>>'{source,definitionRevisionId}')::uuid
  THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
  IF NOT EXISTS(SELECT 1 FROM market_data.revisions r WHERE(r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id)=
    (catalog,part.key::uuid,(normalized#>>'{source,referenceCatalogId}')::uuid,(normalized#>>'{source,referenceRevisionId}')::uuid)
    AND r.state='sealed' AND security.market_fact_visible(catalog,r.id,NULL)
    AND security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id))
  OR EXISTS(SELECT 1 FROM jsonb_array_elements_text(normalized->'observationIds')p WHERE NOT EXISTS(
    SELECT 1 FROM market_data.observations o WHERE(o.catalog_id,o.definition_revision_id,o.id)=(catalog,part.key::uuid,p.value::uuid)
    AND o.observed_at<=instant AND security.market_fact_visible(catalog,part.key::uuid,o.id)))
  THEN RAISE EXCEPTION 'temporal_inputs_denied' USING ERRCODE='42501';END IF;
  IF EXISTS(SELECT 1 FROM jsonb_each(normalized->'curveContracts')m CROSS JOIN LATERAL jsonb_array_elements_text(m.value)p
   WHERE NOT EXISTS(SELECT 1 FROM market_data.curve_members k WHERE(k.catalog_id,k.revision_id,k.curve_code,k.contract_id)=(catalog,part.key::uuid,m.key,p.value::uuid)))
  THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 END LOOP;
 primary_part=sources->revision::text;
 IF primary_part IS NULL THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 expected=security.canonical_source_inputs(primary_part);
 normalized=security.canonical_source_inputs(jsonb_build_object('source',coalesce(nullif(frame->'source','null'::jsonb),primary_part->'source'),
  'observationIds',frame->'observationIds','frozenObservationIds',frame->'frozenObservationIds','curveContracts',frame->'curveContracts','rootCurves',primary_part->'rootCurves'));
 IF normalized IS DISTINCT FROM expected THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 close=(frame->>'originCloseId')::uuid;
 IF close IS NULL THEN RETURN;END IF;
 SELECT * INTO native_close FROM market_data.market_closes x WHERE(x.catalog_id,x.id,x.at,x.business_date)=(catalog,close,instant,day);
 IF NOT FOUND OR frame#>>'{close,session}' IS DISTINCT FROM native_close.session_code
 OR(frame#>>'{close,sessionVersion}')::integer IS DISTINCT FROM native_close.session_version
 OR(frame#>>'{close,businessDate}')::date IS DISTINCT FROM day
 THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 native=(security.read_market_close_parts(catalog,ARRAY[close],c.market_context_id)->0)->'sources';
 IF jsonb_array_length(native)<>(SELECT count(*) FROM jsonb_each(sources)) THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 FOR part IN SELECT value FROM jsonb_array_elements(native) LOOP
  SELECT jsonb_build_object('source',jsonb_build_object('definitionRevisionId',part.value->'definition_revision_id',
   'referenceCatalogId',part.value->'reference_catalog_id','referenceRevisionId',part.value->'reference_revision_id'),
   'observationIds',(SELECT coalesce(jsonb_agg(p->'observation_id'),'[]'::jsonb) FROM jsonb_array_elements(part.value->'pins')p),
   'frozenObservationIds',(SELECT coalesce(jsonb_agg(p->'observation_id'),'[]'::jsonb) FROM jsonb_array_elements(part.value->'pins')p WHERE p->>'input_use'='frozen'),
   'curveContracts',(SELECT coalesce(jsonb_object_agg(m.code,m.ids),'{}'::jsonb) FROM(
    SELECT p->>'curve_code' code,jsonb_agg(p->'contract_id' ORDER BY n) ids FROM jsonb_array_elements(part.value->'members') WITH ORDINALITY e(p,n) GROUP BY p->>'curve_code')m),
   'rootCurves',(SELECT coalesce(jsonb_agg(code),'[]'::jsonb) FROM(SELECT DISTINCT p->>'curve_code' code FROM jsonb_array_elements(part.value->'members')p WHERE p->>'scope'='root')r)) INTO expected;
  IF security.canonical_source_inputs(sources->(part.value->>'definition_revision_id')) IS DISTINCT FROM security.canonical_source_inputs(expected)
  THEN RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';END IF;
 END LOOP;
EXCEPTION WHEN data_exception THEN
 RAISE EXCEPTION 'temporal_inputs' USING ERRCODE='P0001';
END;$$;
ALTER FUNCTION security.canonical_source_inputs(jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.require_frame_inputs(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.canonical_source_inputs(jsonb),security.require_frame_inputs(uuid,uuid,jsonb) FROM PUBLIC;
-- Patch the admitted entry points in place: existing business rules and grants
-- remain, and every execution format uses the same additional validation.
DO $$DECLARE signature regprocedure;definition text;before text;after text;BEGIN
 FOREACH signature IN ARRAY ARRAY['risk.submit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)'::regprocedure,
  'risk.submit_vol_surface_fit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)'::regprocedure] LOOP
  definition=pg_get_functiondef(signature);before=E'BEGIN\n PERFORM 1 FROM security.epoch FOR SHARE;';
  after=E'BEGIN\n PERFORM security.require_frame_inputs(catalog,revision,frame);\n PERFORM 1 FROM security.epoch FOR SHARE;';
  IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'execution_input_entry_drift: %',signature;END IF;
  EXECUTE replace(definition,before,after);
 END LOOP;
 signature='risk.retain_market(risk.jobs,uuid,jsonb)'::regprocedure;definition=pg_get_functiondef(signature);
 before=E'BEGIN\n IF revision IS NULL';after=E'BEGIN\n PERFORM security.require_frame_inputs(job.catalog_id,revision,frame);\n IF revision IS NULL';
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'execution_input_part_drift';END IF;
 EXECUTE replace(definition,before,after);
END$$;
-- Attest the stored inputs inside the exact leased request context. Workers
-- need no grant to discover or read arbitrary close headers or source matrices.
DO $$DECLARE definition text;before text;after text;BEGIN
 definition=pg_get_functiondef('risk.bind(uuid,uuid)'::regprocedure);
 before='INSERT INTO risk.worker_attempt_contexts VALUES(context,job_id,token);RETURN context;';
 after=E'INSERT INTO risk.worker_attempt_contexts VALUES(context,job_id,token);\n PERFORM security.require_frame_inputs(j.catalog_id,j.revision_id,j.frame) FROM risk.jobs j WHERE j.id=job_id;\n PERFORM risk.owned_attempt(job_id,token,false,false);RETURN context;';
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'execution_input_bind_drift';END IF;
 EXECUTE replace(definition,before,after);
END$$;
DO $$DECLARE definition text;before text;after text;BEGIN
 definition=pg_get_functiondef('assistant.pin_market_turn(uuid,bytea,date,jsonb,integer)'::regprocedure);
 before=E'ARRAY[''at'',''curveContracts'',''frameId'',''frozenObservationIds'',''name'',\n    ''observationIds'',''resolvesLatestCorrection'',''swapMarketSelections'']';
 after=E'ARRAY[''at'',''businessDate'',''close'',''curveContracts'',''frameId'',''frozenObservationIds'',''name'',\n    ''observationIds'',''originCloseId'',''ppaBundle'',''resolvesLatestCorrection'',''source'',''sourceInputs'',''swapMarketSelections'']';
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'assistant_input_keys_drift';END IF;
 definition=replace(definition,before,after);
 before='IF x.id IS NULL THEN RAISE EXCEPTION ''temporal_context'';END IF;';
 after=E'IF x.id IS NULL THEN RAISE EXCEPTION ''temporal_context'';END IF;\n IF pins IS NOT NULL AND(jsonb_typeof(pins->''businessDate'') IS DISTINCT FROM ''string''\n OR pins->>''businessDate''!~''^[0-9]{4}-[0-9]{2}-[0-9]{2}$''\n OR(pins->>''businessDate'')::date IS DISTINCT FROM x.effective_on) THEN RAISE EXCEPTION ''turn_input'';END IF;';
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'assistant_input_date_drift';END IF;
 EXECUTE replace(definition,before,after);
END$$;
COMMIT;
