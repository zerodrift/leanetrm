-- Consumer: explicit portfolio-wide funding of horizon cash, independent of
-- each product's residual carrying. Rate-only markets use existing job input
-- custody; no economic holdings, price graphs or financial facts are created.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$DECLARE p pg_proc;s record;BEGIN
 FOR s IN SELECT * FROM(VALUES
  ('risk.economic_commands(jsonb)','economic_commands','jsonb','2d7a4cfcb276abf1d70e18a78d503880',false,ARRAY['request'],'jsonb'),
  ('risk.retain_economic(risk.jobs)','retain_economic','void','187d8e677ac5ef1632069ce15a1223ff',true,ARRAY['job'],'risk.jobs'),
  ('risk.retain_connected(risk.jobs,jsonb)','retain_connected','void','bd94dc50cefb2d0f5ad09609d8cdfc40',true,ARRAY['job','markets'],'risk.jobs,jsonb')
 )v(signature,name,returns,checksum,definer,names,types) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(s.signature);
  IF NOT FOUND OR md5(p.prosrc)<>s.checksum OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>s.returns::regtype
   OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.prosecdef<>s.definer OR p.provolatile<>'v' OR p.proparallel<>'u'
   OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.prosqlbody IS NOT NULL
   OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.proargnames IS DISTINCT FROM s.names
   OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'
  THEN RAISE EXCEPTION 'cash_accumulation_upgrade: unexpected helper %',s.signature;END IF;
  IF s.name<>'retain_connected' THEN
  EXECUTE replace(replace(pg_get_functiondef(p.oid),'FUNCTION risk.'||s.name||'(','FUNCTION risk.'||s.name||'_pre_explicit_cash('),
   s.name||'.',s.name||'_pre_explicit_cash.');
  EXECUTE format('ALTER FUNCTION risk.%I(%s) OWNER TO openetrm_security_owner',s.name||'_pre_explicit_cash',s.types);
  EXECUTE format('REVOKE ALL ON FUNCTION risk.%I(%s) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',s.name||'_pre_explicit_cash',s.types);
  END IF;
 END LOOP;
END$guard$;

CREATE FUNCTION security.read_projection_cash_curve(catalog uuid,frame jsonb,selection jsonb,until_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE q security.request_contexts;mapping trading.currency_discount_defaults;curve market_data.rate_curves;
 context uuid=(frame->>'contextId')::uuid;at timestamptz=(frame->>'at')::timestamptz;day date=(frame->>'businessDate')::date;
 revision uuid=(selection#>>'{curve,revision,revisionId}')::uuid;part jsonb;result jsonb;
BEGIN
 PERFORM security.require_admitted_business_source(catalog,context,at,day,until_at);
 SELECT * INTO STRICT q FROM security.request_contexts r WHERE r.backend_pid=pg_backend_pid()
  AND r.transaction_id=pg_current_xact_id_if_assigned() AND r.runtime_login=session_user;
 IF(q.market_catalog_id,q.market_context_id,q.trading_as_of,q.trading_business_date) IS DISTINCT FROM(catalog,context,at,day)
 THEN RAISE EXCEPTION 'projection_cash_source' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(selection) IS DISTINCT FROM 'object' OR selection-ARRAY['currency','snapshotId','curve','mappingId','definitionId','definitionVersion']<>'{}'::jsonb
  OR NOT(selection ?& ARRAY['currency','snapshotId','curve','mappingId','definitionId','definitionVersion'])
  OR jsonb_typeof(selection->'currency') IS DISTINCT FROM 'object' OR(selection->'currency')-ARRAY['revision','code']<>'{}'::jsonb
  OR jsonb_typeof(selection#>'{currency,revision}') IS DISTINCT FROM 'object' OR(selection#>'{currency,revision}')-ARRAY['catalogId','revisionId']<>'{}'::jsonb
  OR jsonb_typeof(selection->'curve') IS DISTINCT FROM 'object' OR(selection->'curve')-ARRAY['revision','code']<>'{}'::jsonb
  OR jsonb_typeof(selection#>'{curve,revision}') IS DISTINCT FROM 'object' OR(selection#>'{curve,revision}')-ARRAY['catalogId','revisionId']<>'{}'::jsonb
  OR(selection->>'snapshotId')::uuid IS DISTINCT FROM(frame->>'id')::uuid OR(selection#>>'{curve,revision,catalogId}')::uuid IS DISTINCT FROM catalog
  OR jsonb_typeof(selection->'definitionVersion') IS DISTINCT FROM 'number' OR(selection->>'definitionVersion') !~ '^[1-9][0-9]{0,8}$'
 THEN RAISE EXCEPTION 'projection_cash_source';END IF;
 PERFORM security.require_frame_inputs(catalog,(frame#>>'{source,definitionRevisionId}')::uuid,frame);
 part=frame->'sourceInputs'->revision::text;
 IF part IS NULL AND revision=(frame#>>'{source,definitionRevisionId}')::uuid THEN part=jsonb_build_object('observationIds',frame->'observationIds','frozenObservationIds',frame->'frozenObservationIds');END IF;
 IF part IS NULL THEN RAISE EXCEPTION 'projection_cash_source';END IF;
 SELECT * INTO STRICT mapping FROM trading.currency_discount_defaults d WHERE(d.catalog_id,d.id,d.definition_revision_id,d.curve_code,
  d.reference_catalog_id,d.reference_revision_id,d.currency_code)=(catalog,(selection->>'mappingId')::uuid,revision,selection#>>'{curve,code}',
  (selection#>>'{currency,revision,catalogId}')::uuid,(selection#>>'{currency,revision,revisionId}')::uuid,selection#>>'{currency,code}')
  AND d.effective_at<=at AND security.trading_visible(d.catalog_id,d.publication_order,d.effective_at);
 SELECT * INTO STRICT curve FROM market_data.rate_curves c WHERE(c.catalog_id,c.revision_id,c.code,c.definition_id,c.definition_version,
  c.reference_catalog_id,c.reference_revision_id,c.currency_code)=(catalog,revision,mapping.curve_code,(selection->>'definitionId')::uuid,
  (selection->>'definitionVersion')::integer,mapping.reference_catalog_id,mapping.reference_revision_id,mapping.currency_code);
 IF NOT EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.curve_code)=(catalog,revision,curve.code))
  OR EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.curve_code)=(catalog,revision,curve.code) AND
   (SELECT count(*) FROM market_data.observations o WHERE(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code,o.role,o.publication_state)=
    (catalog,revision,t.series_id,curve.source_code,'parameter','final') AND o.observed_at<=at AND part->'observationIds' @> jsonb_build_array(o.id)
    AND NOT(part->'frozenObservationIds' @> jsonb_build_array(o.id)) AND security.market_fact_visible(catalog,revision,o.id))<>1)
 THEN RAISE EXCEPTION 'projection_cash_source';END IF;
 result=to_jsonb(mapping);
 PERFORM security.require_admitted_business_source(catalog,context,at,day,until_at);RETURN result;
EXCEPTION WHEN no_data_found OR too_many_rows OR data_exception THEN RAISE EXCEPTION 'projection_cash_source';
END$function$;
ALTER FUNCTION security.read_projection_cash_curve(uuid,jsonb,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_projection_cash_curve(uuid,jsonb,jsonb,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_projection_cash_curve(uuid,jsonb,jsonb,timestamptz) TO openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION risk.economic_commands(request jsonb) RETURNS jsonb
 LANGUAGE plpgsql SET search_path=pg_catalog AS $function$
DECLARE p jsonb=request#>'{economic,projection}';choices jsonb=p->'cashAccumulationSelections';normalized jsonb;BEGIN
 IF p->>'implementation' IS DISTINCT FROM 'native_portfolio_economic_lifecycle_v2' THEN RETURN risk.economic_commands_pre_explicit_cash(request);END IF;
 IF jsonb_typeof(choices) IS DISTINCT FROM 'array' OR jsonb_array_length(choices)>256
  OR p#>>'{policy,cashAccumulation}'='nominal_currency_cash_v1' AND choices<>'[]'::jsonb
  OR p#>>'{policy,cashAccumulation}'='captured_currency_curve_exact_cash_accumulation_v1' AND choices='[]'::jsonb
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(choices)s WHERE jsonb_typeof(s) IS DISTINCT FROM 'object'
   OR s-ARRAY['currency','snapshotId','curve','mappingId','definitionId','definitionVersion']<>'{}'::jsonb
   OR NOT(s ?& ARRAY['currency','snapshotId','curve','mappingId','definitionId','definitionVersion'])
   OR(s->>'snapshotId')::uuid IS DISTINCT FROM(request#>>'{context,snapshotId}')::uuid
   OR(s->>'mappingId')::uuid IS NULL OR(s->>'mappingId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR(s->>'definitionId')::uuid IS NULL OR(s->>'definitionId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR jsonb_typeof(s->'definitionVersion') IS DISTINCT FROM 'number' OR(s->>'definitionVersion') !~ '^[1-9][0-9]{0,8}$')
  OR(SELECT count(*)<>count(DISTINCT s->'currency') FROM jsonb_array_elements(choices)s)
 THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 normalized=jsonb_set(request,'{economic,projection}',(p-'cashAccumulationSelections')||jsonb_build_object('implementation','native_portfolio_economic_lifecycle_v1'));
 RETURN risk.economic_commands_pre_explicit_cash(normalized);
EXCEPTION WHEN data_exception THEN RAISE EXCEPTION 'risk_economic_input';
END$function$;

CREATE FUNCTION risk.require_cash_accumulation(job risk.jobs) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE proof jsonb=job.frame#>'{economicLifecycle,cashAccumulation}';p jsonb=job.request#>'{economic,projection}';
 entry jsonb;selection jsonb;source jsonb;definition jsonb;part jsonb;native market_data.rate_curves;until_at timestamptz;BEGIN
 IF p->>'implementation' IS DISTINCT FROM 'native_portfolio_economic_lifecycle_v2' THEN
  RETURN;
 END IF;
 PERFORM risk.economic_commands(job.request);
 IF jsonb_typeof(proof) IS DISTINCT FROM 'object' OR proof-ARRAY['implementation','currencies','selections']<>'{}'::jsonb
  OR proof->>'implementation' IS DISTINCT FROM 'explicit_portfolio_currency_cash_accumulation_v1'
  OR jsonb_typeof(proof->'currencies') IS DISTINCT FROM 'array' OR jsonb_array_length(proof->'currencies') NOT BETWEEN 1 AND 256
  OR(SELECT count(*)<>count(DISTINCT value) FROM jsonb_array_elements(proof->'currencies'))
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(proof->'currencies')c WHERE jsonb_typeof(c) IS DISTINCT FROM 'object'
   OR c-ARRAY['revision','code']<>'{}'::jsonb OR NOT EXISTS(SELECT 1 FROM ref_data.currencies r
    WHERE(r.catalog_id,r.revision_id,r.code)=((c#>>'{revision,catalogId}')::uuid,(c#>>'{revision,revisionId}')::uuid,c->>'code')
     AND security.reference_version_visible(r.catalog_id,r.revision_id)))
  OR jsonb_typeof(proof->'selections') IS DISTINCT FROM 'array'
  OR jsonb_array_length(proof->'selections')<>jsonb_array_length(p->'cashAccumulationSelections')
  OR EXISTS(SELECT supplied.value->'selection' FROM jsonb_array_elements(proof->'selections')supplied(value)
   EXCEPT ALL SELECT value FROM jsonb_array_elements(p->'cashAccumulationSelections'))
  OR EXISTS(SELECT value FROM jsonb_array_elements(p->'cashAccumulationSelections')
   EXCEPT ALL SELECT supplied.value->'selection' FROM jsonb_array_elements(proof->'selections')supplied(value))
  OR p#>>'{policy,cashAccumulation}'='captured_currency_curve_exact_cash_accumulation_v1' AND(
   (SELECT count(*) FROM jsonb_array_elements(proof->'currencies'))<>jsonb_array_length(p->'cashAccumulationSelections') OR EXISTS(
    SELECT value FROM jsonb_array_elements(proof->'currencies') EXCEPT SELECT s->'currency' FROM jsonb_array_elements(p->'cashAccumulationSelections')s))
 THEN RAISE EXCEPTION 'risk_economic_cash';END IF;
 SELECT q.business_source_until INTO until_at FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 FOR entry IN SELECT value FROM jsonb_array_elements(proof->'selections') LOOP
  IF jsonb_typeof(entry) IS DISTINCT FROM 'object' OR entry-ARRAY['selection','source','definition']<>'{}'::jsonb
  THEN RAISE EXCEPTION 'risk_economic_cash';END IF;
  selection=entry->'selection';source=entry->'source';definition=entry->'definition';
  IF jsonb_typeof(source) IS DISTINCT FROM 'object' OR source-ARRAY['id','definitions','valuationAt','marketCutoff','selectionPolicy','selections','knowledge']<>'{}'::jsonb
   OR jsonb_typeof(definition) IS DISTINCT FROM 'object' OR definition-ARRAY['key','currency','source','conventions','tenors','definitionId','definitionVersion']<>'{}'::jsonb
   OR NOT(p->'cashAccumulationSelections' @> jsonb_build_array(selection)) OR
   (source->>'id')::uuid IS DISTINCT FROM(job.frame->>'id')::uuid OR(source#>>'{definitions,catalogId}')::uuid IS DISTINCT FROM job.catalog_id
   OR(source#>>'{definitions,revisionId}')::uuid IS DISTINCT FROM(selection#>>'{curve,revision,revisionId}')::uuid
   OR(source->>'valuationAt')::timestamptz IS DISTINCT FROM job.valuation_at OR(source->>'marketCutoff')::timestamptz IS DISTINCT FROM job.valuation_at
   OR(source#>>'{knowledge,contextId}')::uuid IS DISTINCT FROM job.context_id OR(source#>>'{knowledge,knownAt}')::timestamptz IS DISTINCT FROM
    (SELECT t.known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id))
   OR source->>'selectionPolicy' IS DISTINCT FROM '1' OR definition->'key' IS DISTINCT FROM selection->'curve'
   OR definition->'currency' IS DISTINCT FROM selection->'currency' OR definition->'definitionId' IS DISTINCT FROM selection->'definitionId'
   OR definition->'definitionVersion' IS DISTINCT FROM selection->'definitionVersion'
  THEN RAISE EXCEPTION 'risk_economic_cash';END IF;
  PERFORM security.read_projection_cash_curve(job.catalog_id,job.frame,selection,until_at);
  SELECT * INTO STRICT native FROM market_data.rate_curves c WHERE(c.catalog_id,c.revision_id,c.code)=
   (job.catalog_id,(selection#>>'{curve,revision,revisionId}')::uuid,selection#>>'{curve,code}');
  IF definition->'source' IS DISTINCT FROM jsonb_build_object('revision',jsonb_build_object('catalogId',native.reference_catalog_id,'revisionId',native.reference_revision_id),'code',native.source_code)
   OR definition->'conventions' IS DISTINCT FROM jsonb_build_object('dayCount',native.day_count,'compounding',native.compounding,
    'interpolation',native.interpolation,'extrapolation',native.extrapolation,'markUnit',native.mark_unit)
  THEN RAISE EXCEPTION 'risk_economic_cash';END IF;
  IF source#>>'{definitions,revisionId}'<>job.revision_id::text AND NOT EXISTS(SELECT 1 FROM risk.job_markets m
   WHERE(m.catalog_id,m.job_id,m.revision_id)=(job.catalog_id,job.id,native.revision_id))
   OR jsonb_typeof(definition->'tenors') IS DISTINCT FROM 'array'
   OR jsonb_array_length(definition->'tenors')<>(SELECT count(*) FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.curve_code)=(native.catalog_id,native.revision_id,native.code))
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(definition->'tenors')WITH ORDINALITY x(t,n) LEFT JOIN market_data.rate_curve_tenors actual
    ON(actual.catalog_id,actual.revision_id,actual.curve_code,actual.ordinal)=(native.catalog_id,native.revision_id,native.code,x.n-1)
    WHERE x.t-ARRAY['label','series','maturity']<>'{}'::jsonb OR x.t->>'label' IS DISTINCT FROM actual.label
     OR x.t->'series' IS DISTINCT FROM jsonb_build_object('revision',jsonb_build_object('catalogId',native.catalog_id,'revisionId',native.revision_id),'id',actual.series_id)
     OR(x.t->>'maturity')::date IS NULL OR(x.t->>'maturity')::date<=(job.valuation_at AT TIME ZONE 'UTC')::date)
  THEN RAISE EXCEPTION 'risk_economic_cash';END IF;
  -- Exact native input values and resolved tenor geometry are also reproduced
  -- by the managed replay. The immutable source itself must have native custody.
  part=job.frame->'sourceInputs'->native.revision_id::text;
  IF part IS NULL AND native.revision_id=job.revision_id THEN part=job.frame;END IF;
  IF jsonb_typeof(source->'selections') IS DISTINCT FROM 'array' OR jsonb_array_length(source->'selections')<>jsonb_array_length(part->'observationIds')
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(source->'selections')s WHERE NOT(part->'observationIds' @> jsonb_build_array(s#>'{observation,key,id}')))
   OR(SELECT count(*)<>count(DISTINCT s#>'{observation,key,id}') FROM jsonb_array_elements(source->'selections')s)
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(source->'selections')s LEFT JOIN market_data.observations o
    ON(o.catalog_id,o.definition_revision_id,o.id)=(job.catalog_id,native.revision_id,(s#>>'{observation,key,id}')::uuid)
    WHERE o.id IS NULL OR s-ARRAY['input','observation']<>'{}'::jsonb
     OR s->'input' IS DISTINCT FROM jsonb_build_object('series',jsonb_build_object('revision',jsonb_build_object('catalogId',job.catalog_id,'revisionId',native.revision_id),'id',o.series_id),
      'use',CASE WHEN o.role='index_fixing' OR part->'frozenObservationIds' @> jsonb_build_array(o.id) THEN 2 ELSE 1 END)
     OR s#>'{observation,key}' IS DISTINCT FROM jsonb_build_object('catalogId',job.catalog_id,'id',o.id)
     OR s#>'{observation,series}' IS DISTINCT FROM s#>'{input,series}'
     OR s#>'{observation,source}' IS DISTINCT FROM jsonb_build_object('revision',jsonb_build_object('catalogId',o.reference_catalog_id,'revisionId',o.reference_revision_id),'code',o.source_code)
     OR s#>>'{observation,role}' IS DISTINCT FROM CASE o.role WHEN 'trader_mark' THEN '1' WHEN 'daily_settlement' THEN '2' WHEN 'final_contract_settlement' THEN '3' WHEN 'parameter' THEN '4' WHEN 'index_fixing' THEN '5' END
     OR s#>>'{observation,state}' IS DISTINCT FROM CASE o.publication_state WHEN 'preliminary' THEN '1' WHEN 'final' THEN '2' END
     OR(s#>>'{observation,observedAt}')::timestamptz IS DISTINCT FROM o.observed_at OR(s#>>'{observation,value}')::numeric IS DISTINCT FROM o.value)
  THEN RAISE EXCEPTION 'risk_economic_cash';END IF;
 END LOOP;
EXCEPTION WHEN no_data_found OR too_many_rows OR data_exception THEN RAISE EXCEPTION 'risk_economic_cash';
END$function$;
ALTER FUNCTION risk.require_cash_accumulation(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.require_cash_accumulation(risk.jobs) FROM PUBLIC;

CREATE FUNCTION risk.cash_input_revisions(job risk.jobs) RETURNS uuid[]
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE result uuid[];BEGIN
 IF job.request#>>'{economic,projection,implementation}' IS DISTINCT FROM 'native_portfolio_economic_lifecycle_v2' THEN RETURN '{}'::uuid[];END IF;
 PERFORM risk.require_cash_accumulation(job);
 SELECT coalesce(array_agg(DISTINCT (entry#>>'{selection,curve,revision,revisionId}')::uuid),'{}'::uuid[]) INTO result
 FROM jsonb_array_elements(job.frame#>'{economicLifecycle,cashAccumulation,selections}')entry
 CROSS JOIN LATERAL(SELECT job.frame->'sourceInputs'->(entry#>>'{selection,curve,revision,revisionId}') part)selected
 WHERE part->'rootCurves'='[]'::jsonb AND part->'curveContracts'='{}'::jsonb
  AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(job.frame->'scenarioAxes')axis
   WHERE axis->>'marketRevisionId'=entry#>>'{selection,curve,revision,revisionId}')
  AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements_text(part->'observationIds')pin
   LEFT JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.id)=
    (job.catalog_id,(entry#>>'{selection,curve,revision,revisionId}')::uuid,pin.value::uuid)
   LEFT JOIN market_data.series series ON(series.catalog_id,series.revision_id,series.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
   WHERE o.id IS NULL OR o.role<>'parameter' OR o.publication_state<>'final' OR series.contract_id IS NOT NULL OR series.quote_code IS NOT NULL
    OR NOT EXISTS(SELECT 1 FROM market_data.rate_curve_tenors t WHERE(t.catalog_id,t.revision_id,t.series_id)=(o.catalog_id,o.definition_revision_id,o.series_id)));
 RETURN result;
END$function$;
ALTER FUNCTION risk.cash_input_revisions(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.cash_input_revisions(risk.jobs) FROM PUBLIC;

DO $connected$DECLARE definition text=pg_get_functiondef('risk.retain_connected(risk.jobs,jsonb)'::regprocedure);needle text;BEGIN
 needle='BEGIN'||E'\n';
 IF(length(definition)-length(replace(definition,needle,'')))<>length(needle) THEN RAISE EXCEPTION 'cash_accumulation_upgrade: connected entry';END IF;
 definition=replace(definition,'use_name text;','use_name text;cash_revisions uuid[];');
 definition=replace(definition,needle,needle||E' cash_revisions=risk.cash_input_revisions(job);\n');
 needle='WHERE m.job_id=job.id AND NOT EXISTS(';
 IF(length(definition)-length(replace(definition,needle,'')))<>length(needle) THEN RAISE EXCEPTION 'cash_accumulation_upgrade: connected closure';END IF;
 EXECUTE replace(definition,needle,'WHERE m.job_id=job.id AND NOT(m.revision_id=ANY(cash_revisions)) AND NOT EXISTS(');
END$connected$;

CREATE OR REPLACE FUNCTION risk.retain_economic(job risk.jobs) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE normalized risk.jobs=job;p jsonb=job.request#>'{economic,projection}';BEGIN
 IF p->>'implementation'='native_portfolio_economic_lifecycle_v2' THEN
  PERFORM risk.require_cash_accumulation(job);
  normalized.request=jsonb_set(job.request,'{economic,projection}',(p-'cashAccumulationSelections')||jsonb_build_object('implementation','native_portfolio_economic_lifecycle_v1'));
  normalized.frame=job.frame#-'{economicLifecycle,cashAccumulation}';
 END IF;
 PERFORM risk.retain_economic_pre_explicit_cash(normalized);
END$function$;
COMMIT;
