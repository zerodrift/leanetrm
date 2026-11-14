-- Current consumer: exact source-known final inputs for queued economic risk.
-- Native point inputs include declared frozen upstream sources, independently
-- of the output/view membership retained in risk.job_contracts.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $required$BEGIN
 IF to_regprocedure('risk.bound_lifecycle_reader()') IS NULL THEN
  RAISE EXCEPTION 'lifecycle_final_upgrade: economic worker scope prerequisite missing';
 END IF;
END$required$;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.require_lifecycle_projection_input(uuid,uuid,text,text,uuid,uuid,timestamptz,date,timestamptz)');
 IF NOT FOUND OR md5(p.prosrc)<>'9a843c5033fd1d6d7c012c239eacc477' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'void'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_final_upgrade: unexpected helper security.require_lifecycle_projection_input(uuid,uuid,text,text,uuid,uuid,timestamptz,date,timestamptz)';END IF;
 EXECUTE replace(pg_get_functiondef(p.oid),'FUNCTION security.require_lifecycle_projection_input(','FUNCTION security.require_lifecycle_projection_input_pre_final_scope(');
END$guard$;
ALTER FUNCTION security.require_lifecycle_projection_input_pre_final_scope(uuid,uuid,text,text,uuid,uuid,timestamptz,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_lifecycle_projection_input_pre_final_scope(uuid,uuid,text,text,uuid,uuid,timestamptz,date,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $guard$DECLARE p pg_proc;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.read_lifecycle_projection_final(uuid,uuid,text,text,uuid,timestamptz,uuid,timestamptz,date)');
 IF NOT FOUND OR md5(p.prosrc)<>'792145b4eb76dfa99b0e17eed9990306' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'jsonb'::regtype OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL
  OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u' OR NOT p.prosecdef
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
  OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'lifecycle_final_upgrade: unexpected helper security.read_lifecycle_projection_final(uuid,uuid,text,text,uuid,timestamptz,uuid,timestamptz,date)';END IF;
 EXECUTE replace(pg_get_functiondef(p.oid),'FUNCTION security.read_lifecycle_projection_final(','FUNCTION security.read_lifecycle_projection_final_pre_final_scope(');
END$guard$;
ALTER FUNCTION security.read_lifecycle_projection_final_pre_final_scope(uuid,uuid,text,text,uuid,timestamptz,uuid,timestamptz,date) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_lifecycle_projection_final_pre_final_scope(uuid,uuid,text,text,uuid,timestamptz,uuid,timestamptz,date) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE TABLE risk.job_lifecycle_final_inputs(
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>0),definition_revision_id uuid NOT NULL,
 projection_curve ref_data.code NOT NULL,official_curve ref_data.code NOT NULL,contract_id uuid NOT NULL,final_at ref_data.instant NOT NULL,
 PRIMARY KEY(job_id,ordinal),UNIQUE(job_id,definition_revision_id,projection_curve,official_curve,contract_id,final_at),
 UNIQUE(catalog_id,job_id,ordinal,definition_revision_id),
 FOREIGN KEY(catalog_id,job_id) REFERENCES risk.jobs(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,projection_curve,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,definition_revision_id,official_curve,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
CREATE TABLE risk.job_lifecycle_opening_inputs(
 catalog_id uuid NOT NULL,job_id uuid NOT NULL,final_ordinal integer NOT NULL,definition_revision_id uuid NOT NULL,
 snapshot_id uuid NOT NULL,at ref_data.instant NOT NULL,observation_id uuid NOT NULL,series_id uuid NOT NULL,
 input_use text NOT NULL CHECK(input_use IN('Variable','FrozenFact')),
 PRIMARY KEY(job_id,final_ordinal,snapshot_id,series_id,input_use),
 FOREIGN KEY(catalog_id,job_id,final_ordinal,definition_revision_id) REFERENCES risk.job_lifecycle_final_inputs(catalog_id,job_id,ordinal,definition_revision_id),
 FOREIGN KEY(catalog_id,observation_id,definition_revision_id,series_id) REFERENCES market_data.observations(catalog_id,id,definition_revision_id,series_id)
);
DO $tables$DECLARE item text;BEGIN
 FOREACH item IN ARRAY ARRAY['job_lifecycle_final_inputs','job_lifecycle_opening_inputs'] LOOP
  EXECUTE format('ALTER TABLE risk.%I OWNER TO openetrm_security_owner',item);
  EXECUTE format('ALTER TABLE risk.%I ENABLE ROW LEVEL SECURITY',item);
  EXECUTE format('ALTER TABLE risk.%I FORCE ROW LEVEL SECURITY',item);
  EXECUTE format('CREATE POLICY owner_access ON risk.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',item);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON risk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',item);
 END LOOP;
END$tables$;

CREATE FUNCTION risk.require_lifecycle_final_scope(catalog uuid,definition uuid,projection text,official text,contract uuid,
 context uuid,as_of timestamptz,business_date date,final_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE job risk.jobs;BEGIN
 IF NOT pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RETURN;END IF;
 job=risk.bound_lifecycle_reader();
 IF(catalog,context,as_of,business_date) IS DISTINCT FROM(job.catalog_id,job.context_id,job.valuation_at,(job.frame->>'businessDate')::date)
  OR NOT EXISTS(SELECT 1 FROM risk.job_lifecycle_final_inputs i WHERE(i.catalog_id,i.job_id,i.definition_revision_id,i.projection_curve,i.official_curve,i.contract_id)=
   (catalog,job.id,definition,projection,official,contract) AND(require_lifecycle_final_scope.final_at IS NULL OR i.final_at=require_lifecycle_final_scope.final_at))
 THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
END$function$;
ALTER FUNCTION risk.require_lifecycle_final_scope(uuid,uuid,text,text,uuid,uuid,timestamptz,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.require_lifecycle_final_scope(uuid,uuid,text,text,uuid,uuid,timestamptz,date,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION security.require_lifecycle_projection_input(catalog uuid,definition uuid,projection text,official text,contract uuid,
 context uuid,as_of timestamptz,business_date date,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 PERFORM risk.require_lifecycle_final_scope(catalog,definition,projection,official,contract,context,as_of,business_date,NULL);
 PERFORM security.require_lifecycle_projection_input_pre_final_scope(catalog,definition,projection,official,contract,context,as_of,business_date,until_at);
END$function$;
CREATE OR REPLACE FUNCTION security.read_lifecycle_projection_final(catalog uuid,definition uuid,projection text,official text,contract uuid,
 final_at timestamptz,context uuid,as_of timestamptz,business_date date) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE result jsonb;BEGIN
 PERFORM risk.require_lifecycle_final_scope(catalog,definition,projection,official,contract,context,as_of,business_date,final_at);
 result=security.read_lifecycle_projection_final_pre_final_scope(catalog,definition,projection,official,contract,final_at,context,as_of,business_date);
 PERFORM risk.require_lifecycle_final_scope(catalog,definition,projection,official,contract,context,as_of,business_date,final_at);
 RETURN result;
END$function$;

CREATE FUNCTION risk.retain_lifecycle_final_inputs() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE finals jsonb;item jsonb;opening jsonb;native jsonb;revision uuid;projection text;official text;contract uuid;at timestamptz;
 ordinal integer;source_frame jsonb;pin uuid;series uuid;use_name text;opening_at timestamptz;snapshot uuid;source market_data.curves;
BEGIN
 IF NULLIF(NEW.request->'economic','null'::jsonb) IS NULL THEN RETURN NEW;END IF;
 finals=NEW.frame#>'{economicLifecycle,inputFinals}';
 IF jsonb_typeof(finals) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'risk_economic_input';END IF;
 IF jsonb_array_length(finals)>100000 THEN RAISE EXCEPTION 'risk_economic_final_input';END IF;
 FOR item,ordinal IN SELECT value,position::integer FROM jsonb_array_elements(finals) WITH ORDINALITY entries(value,position) LOOP
  IF jsonb_typeof(item) IS DISTINCT FROM 'object'
   OR item-ARRAY['contextId','knownAt','projectionCurve','officialCurve','contractId','definitionRevisionId','finalAt','observation','openingInputs']<>'{}'::jsonb
   OR NOT(item ?& ARRAY['contextId','knownAt','projectionCurve','officialCurve','contractId','definitionRevisionId','finalAt','observation','openingInputs'])
   OR jsonb_typeof(item->'openingInputs') IS DISTINCT FROM 'array' OR jsonb_array_length(item->'openingInputs') NOT BETWEEN 1 AND 100000
  THEN RAISE EXCEPTION 'risk_economic_final_input';END IF;
  revision=(item->>'definitionRevisionId')::uuid;projection=item->>'projectionCurve';official=item->>'officialCurve';contract=(item->>'contractId')::uuid;at=(item->>'finalAt')::timestamptz;
  IF revision IS DISTINCT FROM NEW.revision_id AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(NEW.frame->'connectedMarkets')m WHERE(m->>'revisionId')::uuid=revision)
  THEN RAISE EXCEPTION 'risk_economic_final_input';END IF;
  native=security.read_lifecycle_projection_final(NEW.catalog_id,revision,projection,official,contract,at,NEW.context_id,NEW.valuation_at,(NEW.frame->>'businessDate')::date)-'authorityUntil';
  IF item-'openingInputs' IS DISTINCT FROM native THEN RAISE EXCEPTION 'risk_economic_final_input';END IF;
  INSERT INTO risk.job_lifecycle_final_inputs VALUES(NEW.catalog_id,NEW.id,ordinal,revision,projection,official,contract,at);
  SELECT * INTO STRICT source FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(NEW.catalog_id,revision,projection);
  FOR opening IN SELECT value FROM jsonb_array_elements(item->'openingInputs') LOOP
   IF jsonb_typeof(opening) IS DISTINCT FROM 'object' OR opening-ARRAY['snapshotId','at','observationId','seriesId','use']<>'{}'::jsonb
    OR NOT(opening ?& ARRAY['snapshotId','at','observationId','seriesId','use']) THEN RAISE EXCEPTION 'risk_economic_final_input';END IF;
   pin=(opening->>'observationId')::uuid;series=(opening->>'seriesId')::uuid;use_name=opening->>'use';opening_at=(opening->>'at')::timestamptz;snapshot=(opening->>'snapshotId')::uuid;
   IF snapshot IS NULL OR snapshot='00000000-0000-0000-0000-000000000000'::uuid OR opening_at IS NULL OR NOT isfinite(opening_at)
    OR opening->>'at'!~'(Z|[+]00:00)$' OR opening_at<>date_trunc('microseconds',opening_at) OR opening_at>NEW.valuation_at OR use_name IS NULL OR use_name NOT IN('Variable','FrozenFact')
    OR NOT EXISTS(SELECT 1 FROM market_data.observations o JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
     JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(s.catalog_id,s.revision_id,s.contract_id)
     WHERE(o.catalog_id,o.id,o.definition_revision_id,o.series_id,s.contract_id,s.quote_code,o.source_code,o.role)=
      (NEW.catalog_id,pin,revision,series,contract,source.quote_code,source.direct_source_code,CASE WHEN use_name='FrozenFact' THEN 'final_contract_settlement' ELSE source.direct_role END)
      AND o.observed_at<=opening_at AND o.publication_state='final' AND(use_name='Variable' OR c.kind='future' AND o.observed_at>=c.expiry_at))
   THEN RAISE EXCEPTION 'risk_economic_final_input';END IF;
   source_frame=CASE WHEN revision=NEW.revision_id THEN NEW.frame ELSE NEW.frame->'supplementalMarkets'->revision::text END;
   IF(source_frame->>'id')::uuid=snapshot AND(source_frame->>'at')::timestamptz=opening_at
    AND source_frame->'observationIds' @> jsonb_build_array(pin)
    AND(source_frame->'frozenObservationIds' @> jsonb_build_array(pin))=(use_name='FrozenFact') THEN
    NULL;
   ELSIF NOT EXISTS(SELECT 1 FROM jsonb_array_elements(NEW.frame->'connectedMarkets')m
    CROSS JOIN LATERAL jsonb_array_elements(m->'samples')s WHERE(m->>'revisionId')::uuid=revision
     AND(s->>'snapshotId')::uuid=snapshot AND(s->>'at')::timestamptz=opening_at AND s->>'sourceCurve'=projection AND(s->>'contractId')::uuid=contract
     AND s->'observations' @> jsonb_build_array(jsonb_build_object('id',pin,'seriesId',series,'use',use_name)))
   THEN RAISE EXCEPTION 'risk_economic_final_input';END IF;
   INSERT INTO risk.job_lifecycle_opening_inputs VALUES(NEW.catalog_id,NEW.id,ordinal,revision,snapshot,opening_at,pin,series,use_name);
  END LOOP;
 END LOOP;
 RETURN NEW;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_datetime_format OR datetime_field_overflow OR unique_violation OR not_null_violation
 THEN RAISE EXCEPTION 'risk_economic_final_input';
END$function$;
ALTER FUNCTION risk.retain_lifecycle_final_inputs() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.retain_lifecycle_final_inputs() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE TRIGGER retain_lifecycle_final_inputs AFTER INSERT ON risk.jobs FOR EACH ROW EXECUTE FUNCTION risk.retain_lifecycle_final_inputs();
COMMIT;
