-- A surface is scoped to the exact definition that supplied its observations.
-- Recover existing identities only from the complete retained observation set.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.vol_surface_revisions ADD COLUMN definition_revision_id uuid;
ALTER TABLE trading.vol_surface_revisions DISABLE TRIGGER immutable_record;
WITH sources AS (
 SELECT i.catalog_id,i.revision_id,(array_agg(DISTINCT o.definition_revision_id))[1] definition
 FROM trading.vol_surface_inputs i
 JOIN market_data.observations o ON (o.catalog_id,o.id)=(i.catalog_id,i.observation_id)
 GROUP BY i.catalog_id,i.revision_id
 HAVING count(DISTINCT o.definition_revision_id)=1)
UPDATE trading.vol_surface_revisions r SET definition_revision_id=s.definition
FROM sources s WHERE(r.catalog_id,r.id)=(s.catalog_id,s.revision_id);
ALTER TABLE trading.vol_surface_revisions ENABLE TRIGGER immutable_record;
ALTER TABLE trading.vol_surface_revisions ALTER COLUMN definition_revision_id SET NOT NULL,
 ADD FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 ADD FOREIGN KEY(catalog_id,close_id,definition_revision_id)
 REFERENCES market_data.market_close_sources(catalog_id,close_id,definition_revision_id);
CREATE INDEX vol_surface_exact_source ON trading.vol_surface_revisions
 (catalog_id,definition_revision_id,model_id,product_code,effective_on,valuation_at,close_id);

CREATE FUNCTION security.vol_surface_source_visible(catalog uuid,definition uuid,reference_catalog uuid,reference_revision uuid)
 RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_fact_visible(catalog,definition,NULL)
 AND security.reference_version_visible(reference_catalog,reference_revision)
 AND EXISTS(SELECT 1 FROM market_data.revisions r
 WHERE(r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id)=(catalog,definition,reference_catalog,reference_revision)
 AND r.state='sealed');
$$;
ALTER FUNCTION security.vol_surface_source_visible(uuid,uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.vol_surface_source_visible(uuid,uuid,uuid,uuid) FROM PUBLIC;

-- A recorded location must supply the whole attested source part. Working
-- locations may select a subset, but every pin belongs to the chosen source.
CREATE FUNCTION security.vol_surface_location_visible(catalog uuid,definition uuid,close uuid,
 valuation_at timestamptz,pins uuid[]) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT pins IS NOT NULL AND cardinality(pins) BETWEEN 1 AND 100000
 AND array_position(pins,NULL) IS NULL
 AND (SELECT count(DISTINCT p) FROM unnest(pins) p)=cardinality(pins)
 AND CASE WHEN close IS NOT NULL THEN
  security.market_close_visible(catalog,close)
  AND EXISTS(SELECT 1 FROM market_data.market_closes c WHERE(c.catalog_id,c.id,c.at)=(catalog,close,valuation_at))
  AND ARRAY(SELECT p FROM unnest(pins) p ORDER BY p)=ARRAY(
   SELECT p.observation_id FROM market_data.market_close_pins p
   WHERE(p.catalog_id,p.close_id,p.definition_revision_id)=(catalog,close,definition) ORDER BY p.observation_id)
 ELSE NOT EXISTS(
  SELECT 1 FROM unnest(pins) p WHERE NOT EXISTS(
   SELECT 1 FROM market_data.observations o
   WHERE(o.catalog_id,o.definition_revision_id,o.id)=(catalog,definition,p)
   AND o.observed_at<=valuation_at AND security.market_fact_visible(catalog,definition,p)))
 END;
$$;
ALTER FUNCTION security.vol_surface_location_visible(uuid,uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.vol_surface_location_visible(uuid,uuid,uuid,timestamptz,uuid[]) FROM PUBLIC;

DO $$DECLARE body text;before text;replacement text;BEGIN
 body=pg_get_functiondef('trading.record_vol_surface_revision(uuid,uuid,uuid,jsonb,date,text,text,uuid,uuid,timestamptz,uuid[],jsonb,text)'::regprocedure);
 before='publication integer;revision uuid=gen_random_uuid();outcome jsonb;';
 IF strpos(body,before)=0 THEN RAISE EXCEPTION 'vol_surface_writer_drift';END IF;
 body=replace(body,before,before||'definition uuid;');
 before='SELECT array_agg(o ORDER BY o) INTO pinned FROM unnest(inputs) o;';
 replacement=before||E'\n SELECT o.definition_revision_id INTO definition FROM market_data.observations o WHERE(o.catalog_id,o.id)=(catalog,pinned[1]);';
 IF strpos(body,before)=0 THEN RAISE EXCEPTION 'vol_surface_writer_drift';END IF;
 body=replace(body,before,replacement);
 before='o.catalog_id=catalog AND o.id=ANY(inputs)';
 IF strpos(body,before)=0 THEN RAISE EXCEPTION 'vol_surface_writer_drift';END IF;
 body=replace(body,before,before||' AND(o.definition_revision_id,o.reference_catalog_id,o.reference_revision_id)=(definition,p.catalog_id,p.revision_id)');
 before='AND r.close_id IS NOT DISTINCT FROM close AND r.valuation_at=record_vol_surface_revision.valuation_at';
 IF strpos(body,before)=0 THEN RAISE EXCEPTION 'vol_surface_writer_drift';END IF;
 body=replace(body,before,before||' AND r.definition_revision_id=definition');
 before='publication,actor,request,surface,digest,close);';
 IF strpos(body,before)=0 THEN RAISE EXCEPTION 'vol_surface_writer_drift';END IF;
 EXECUTE replace(body,before,'publication,actor,request,surface,digest,close,definition);');
END$$;

DO $$DECLARE signature regprocedure;body text;before text;restriction text;BEGIN
 FOR signature,before,restriction IN VALUES
 ('security.read_vol_surface(uuid,jsonb,date,text,uuid,timestamptz,uuid[])'::regprocedure,
  'AND r.close_id IS NOT DISTINCT FROM close',
  'AND r.definition_revision_id=definition AND security.vol_surface_source_visible(catalog,definition,p.catalog_id,p.revision_id) AND security.vol_surface_location_visible(catalog,definition,close,read_vol_surface.valuation_at,pins)'),
 ('risk.vol_surface_fit_job(uuid,jsonb,date,text,uuid,timestamptz,uuid[])'::regprocedure,
  'AND j.request->>''convention''=''vol_surface_fit_v1''',
  'AND j.revision_id=definition'),
 ('security.vol_surface_library(uuid,uuid,uuid,text,text,uuid,timestamptz,uuid[],integer,integer)'::regprocedure,
  'AND(product IS NULL OR r.product_code=product)',
  'AND r.definition_revision_id=definition')
 LOOP
  body=pg_get_functiondef(signature);
  IF strpos(body,'catalog uuid,')=0 OR strpos(body,before)=0 THEN RAISE EXCEPTION 'vol_surface_read_drift: %',signature;END IF;
  IF signature='security.vol_surface_library(uuid,uuid,uuid,text,text,uuid,timestamptz,uuid[],integer,integer)'::regprocedure THEN
   IF strpos(body,'AND NOT EXISTS(SELECT 1 FROM trading.vol_surface_inputs i')=0
    OR strpos(body,'DECLARE context uuid;context_date date;page jsonb;')=0
    OR strpos(body,'WITH heads AS(')=0 THEN RAISE EXCEPTION 'vol_surface_library_location_drift';END IF;
   body=replace(body,'DECLARE context uuid;context_date date;page jsonb;',
    'DECLARE context uuid;context_date date;page jsonb;location_visible boolean;');
   body=replace(body,'WITH heads AS(', $library$
 IF security.vol_surface_source_visible(catalog,definition,reference_catalog,reference_revision) IS NOT TRUE
 THEN RAISE EXCEPTION 'vol_surface_product' USING ERRCODE='P0001';END IF;
 location_visible=security.vol_surface_location_visible(catalog,definition,vol_surface_library.close,vol_surface_library.valuation_at,pins);
 WITH heads AS($library$);
   body=replace(body,'AND NOT EXISTS(SELECT 1 FROM trading.vol_surface_inputs i',
    'AND location_visible AND NOT EXISTS(SELECT 1 FROM trading.vol_surface_inputs i');
  ELSIF signature='risk.vol_surface_fit_job(uuid,jsonb,date,text,uuid,timestamptz,uuid[])'::regprocedure THEN
   IF strpos(body,'DECLARE c security.request_contexts;result jsonb;')=0
    OR strpos(body,'SELECT jsonb_build_object(''id'',j.id')=0
    OR strpos(body,'RETURN result;')=0 THEN RAISE EXCEPTION 'vol_surface_job_drift';END IF;
   body=replace(body,'DECLARE c security.request_contexts;result jsonb;',
    'DECLARE c security.request_contexts;result jsonb;p ref_data.futures_option_products;');
   body=replace(body,'SELECT jsonb_build_object(''id'',j.id', $job$
 p=trading.vol_surface_key(product,business_date,model);
 IF security.vol_surface_source_visible(catalog,definition,p.catalog_id,p.revision_id) IS NOT TRUE
  OR security.vol_surface_location_visible(catalog,definition,close,vol_surface_fit_job.valuation_at,pins) IS NOT TRUE
 THEN RETURN NULL;END IF;
 SELECT jsonb_build_object('id',j.id$job$);
   body=replace(body,'RETURN result;', $job$
 IF c.expires_at<=clock_timestamp() OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
  OR security.vol_surface_source_visible(catalog,definition,p.catalog_id,p.revision_id) IS NOT TRUE
  OR (close IS NOT NULL AND security.market_close_visible(catalog,close) IS NOT TRUE)
 THEN RAISE EXCEPTION 'risk_job_denied' USING ERRCODE='42501';END IF;
 RETURN result;$job$);
  END IF;
  EXECUTE replace(regexp_replace(body,'[(]catalog uuid,','(catalog uuid, definition uuid,'),before,restriction||E'\n  '||before);
  EXECUTE format('DROP FUNCTION %s',signature);
 END LOOP;
END$$;
DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY[
  'security.read_vol_surface(uuid,uuid,jsonb,date,text,uuid,timestamptz,uuid[])'::regprocedure,
  'risk.vol_surface_fit_job(uuid,uuid,jsonb,date,text,uuid,timestamptz,uuid[])'::regprocedure,
  'security.vol_surface_library(uuid,uuid,uuid,uuid,text,text,uuid,timestamptz,uuid[],integer,integer)'::regprocedure]
 LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f);
 END LOOP;
END$$;
DO $$DECLARE body text;before text;BEGIN
 body=pg_get_functiondef('security.vol_surface_inputs_revised(uuid,uuid)'::regprocedure);
 before='AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_on::timestamp AT TIME ZONE ''UTC'')';
 IF strpos(body,before)=0 THEN RAISE EXCEPTION 'vol_surface_revision_visibility_drift';END IF;
 EXECUTE replace(body,before,before||
  ' AND security.vol_surface_source_visible(r.catalog_id,r.definition_revision_id,r.reference_catalog_id,r.reference_revision_id)'
  ||' AND(r.close_id IS NULL OR security.market_close_visible(r.catalog_id,r.close_id))');
END$$;
COMMIT;
