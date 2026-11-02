-- Physical selections determine pricing; source identities are checked outputs.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE FUNCTION security.resolve_crude_delivery(catalog uuid,physical jsonb,start_at timestamptz,end_at timestamptz,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE reference_catalog uuid;reference_revision uuid;grade ref_data.crude_grades;heads uuid[];known timestamptz;
 source_catalog uuid;source_revision uuid;selected_setup uuid;
 candidate record;result jsonb;tuples integer=0;overlap_count integer=0;covered integer=0;intersects boolean;complete boolean;
BEGIN
 IF NOT security.market_access(catalog,'ReadMarketData') OR pg_has_role(session_user,'openetrm_risk_worker','MEMBER')
 THEN RAISE EXCEPTION 'No current physical pricing resolution access' USING ERRCODE='42501';END IF;
 IF jsonb_typeof(physical) IS DISTINCT FROM 'object'
 OR NOT physical ?& ARRAY['referenceCatalogId','referenceRevisionId','commodity','grade','facility','custodyPoint','quantityCode','clock']
 OR physical-ARRAY['referenceCatalogId','referenceRevisionId','commodity','grade','facility','custodyPoint','quantityCode','clock']<>'{}'::jsonb
 OR EXISTS(SELECT 1 FROM jsonb_each(physical) p WHERE jsonb_typeof(p.value)<>'string' OR nullif(btrim(p.value#>>'{}'),'') IS NULL)
 OR start_at IS NULL OR end_at IS NULL OR valuation IS NULL OR NOT isfinite(start_at) OR NOT isfinite(end_at) OR NOT isfinite(valuation)
 OR start_at>=end_at THEN RAISE EXCEPTION 'crude_reference_input';END IF;
 reference_catalog=(physical->>'referenceCatalogId')::uuid;reference_revision=(physical->>'referenceRevisionId')::uuid;
 IF NOT security.reference_access(reference_catalog,'ReadMarketData') OR NOT security.reference_version_visible(reference_catalog,reference_revision)
 THEN RAISE EXCEPTION 'No physical delivery master access' USING ERRCODE='42501';END IF;
 SELECT * INTO grade FROM ref_data.crude_grades g WHERE(g.catalog_id,g.revision_id,g.code,g.commodity_code,g.quantity_code)=
 (reference_catalog,reference_revision,physical->>'grade',physical->>'commodity',physical->>'quantityCode');
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM ref_data.clock_rules k WHERE(k.catalog_id,k.revision_id,k.code)=
 (reference_catalog,reference_revision,physical->>'clock')) THEN RAISE EXCEPTION 'crude_reference_input';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.crude_delivery_points p WHERE(p.catalog_id,p.revision_id,p.commodity_code,p.grade_code,p.facility_code,p.custody_point_code)=
 (reference_catalog,reference_revision,physical->>'commodity',physical->>'grade',physical->>'facility',physical->>'custodyPoint'))
 THEN RAISE EXCEPTION 'crude_delivery_relationship_missing';END IF;

 -- Heads are selected by knowledge first; an inaccessible successor cannot
 -- disappear and make an older or competing mapping look unique.
 SELECT t.known_at INTO known FROM security.request_contexts q JOIN market_data.temporal_contexts t
  ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id)
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 SELECT coalesce(array_agg(v.id),ARRAY[]::uuid[]) INTO heads FROM(
  SELECT DISTINCT ON(v.code) v.id FROM market_data.crude_setup_versions v
  LEFT JOIN market_data.crude_setup_receipts k ON(k.catalog_id,k.setup_id)=(v.catalog_id,v.id)
  WHERE v.catalog_id=catalog AND v.state='published' AND(known IS NULL OR k.known_from<=known)
  ORDER BY v.code,v.publication_order DESC)v;
 FOR candidate IN
  SELECT a.*,v.valid_from,v.valid_to FROM market_data.crude_valuation_assignments a
  JOIN market_data.revisions r ON(r.catalog_id,r.id)=(a.catalog_id,a.revision_id)
  LEFT JOIN market_data.crude_setup_versions v ON(v.catalog_id,v.id)=(a.catalog_id,a.setup_id)
  WHERE a.catalog_id=catalog AND r.state='sealed'
  AND(a.delivery_catalog_id,a.delivery_revision_id,a.commodity_code,a.grade_code,a.facility_code,a.custody_point_code,a.clock_code)=
   (reference_catalog,reference_revision,physical->>'commodity',physical->>'grade',physical->>'facility',physical->>'custodyPoint',physical->>'clock')
  AND(a.setup_id IS NULL OR a.setup_id=ANY(heads))
  AND EXISTS(SELECT 1 FROM market_data.revision_periods p WHERE(p.catalog_id,p.revision_id)=(a.catalog_id,a.revision_id)
   AND p.effective_from<=(valuation AT TIME ZONE 'UTC')::date AND(valuation AT TIME ZONE 'UTC')::date<p.effective_to)
 LOOP
  IF NOT security.reference_access(candidate.reference_catalog_id,'ReadMarketData')
   OR(candidate.setup_id IS NOT NULL AND NOT security.crude_setup_visible(catalog,candidate.setup_id))
  THEN RAISE EXCEPTION 'No original physical pricing reference access' USING ERRCODE='42501';END IF;
  IF NOT security.market_fact_visible(catalog,candidate.revision_id,NULL)
   OR NOT security.reference_version_visible(candidate.reference_catalog_id,candidate.reference_revision_id) THEN CONTINUE;END IF;
  tuples=tuples+1;
  IF candidate.setup_id IS NOT NULL THEN
   intersects=candidate.valid_from::timestamp AT TIME ZONE 'UTC'<end_at AND start_at<candidate.valid_to::timestamp AT TIME ZONE 'UTC';
   complete=candidate.valid_from::timestamp AT TIME ZONE 'UTC'<=start_at AND end_at<=candidate.valid_to::timestamp AT TIME ZONE 'UTC';
  ELSE
   SELECT coalesce(bool_or(c.delivery_from::timestamp AT TIME ZONE 'UTC'<end_at AND start_at<c.delivery_to::timestamp AT TIME ZONE 'UTC'),false),
    coalesce(bool_or(c.delivery_from::timestamp AT TIME ZONE 'UTC'<=start_at AND end_at<=c.delivery_to::timestamp AT TIME ZONE 'UTC'),false)
    INTO intersects,complete FROM market_data.curve_members m JOIN market_data.contracts c
    ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(m.catalog_id,m.revision_id,m.curve_code)=(candidate.catalog_id,candidate.revision_id,candidate.curve_code)
    AND c.kind='forward' AND c.delivery_from=date_trunc('month',c.delivery_from)::date AND c.delivery_to=(c.delivery_from+interval '1 month')::date
    AND NOT EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(c.catalog_id,c.revision_id,c.id));
  END IF;
  IF intersects THEN overlap_count=overlap_count+1;END IF;
  IF complete THEN
   covered=covered+1;
   source_catalog=candidate.reference_catalog_id;source_revision=candidate.reference_revision_id;selected_setup=candidate.setup_id;
   result=jsonb_build_object('definitionRevisionId',candidate.revision_id,'assignmentCode',candidate.code,'crudeSetupId',candidate.setup_id,'curve',candidate.curve_code);
  END IF;
 END LOOP;
 IF overlap_count>1 OR covered>1 THEN RAISE EXCEPTION 'crude_price_mapping_ambiguous';END IF;
 IF covered<>1 THEN
  IF tuples=0 THEN RAISE EXCEPTION 'crude_price_mapping_missing';ELSE RAISE EXCEPTION 'crude_price_mapping_coverage';END IF;
 END IF;
 IF NOT security.market_access(catalog,'ReadMarketData')
  OR NOT security.reference_access(reference_catalog,'ReadMarketData') OR NOT security.reference_version_visible(reference_catalog,reference_revision)
  OR NOT security.reference_access(source_catalog,'ReadMarketData') OR NOT security.reference_version_visible(source_catalog,source_revision)
  OR NOT security.market_fact_visible(catalog,(result->>'definitionRevisionId')::uuid,NULL)
  OR(selected_setup IS NOT NULL AND NOT security.crude_setup_visible(catalog,selected_setup))
 THEN RAISE EXCEPTION 'Physical pricing resolution authority expired' USING ERRCODE='42501';END IF;
 RETURN result;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'crude_reference_input';
END$$;
ALTER FUNCTION security.resolve_crude_delivery(uuid,jsonb,timestamptz,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.resolve_crude_delivery(uuid,jsonb,timestamptz,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.resolve_crude_delivery(uuid,jsonb,timestamptz,timestamptz,timestamptz) TO openetrm_runtime;

-- Preserve completed-command replay before current resolution. Initial writes
-- already hold the same publication lock as setup publication. Supplied source
-- pins never narrow the resolver's candidate set.
DO $patch$ DECLARE source text;needle text=' expected=CASE WHEN fee IS NULL THEN 2 ELSE 3 END;';BEGIN
 source=pg_get_functiondef('security.book_physical_crude(uuid,jsonb,timestamptz)'::regprocedure);
 IF length(source)-length(replace(source,needle,''))<>length(needle)
  OR strpos(source,'publication integer;trade uuid;instrument uuid;booking jsonb;')=0
  OR strpos(source,'''crudeSetupId'',''snapshotId''')=0
 THEN RAISE EXCEPTION 'crude_reference_resolution_upgrade';END IF;
 source=replace(source,'publication integer;trade uuid;instrument uuid;booking jsonb;',
  'publication integer;trade uuid;instrument uuid;booking jsonb;physical jsonb;resolved jsonb;');
 source=replace(source,'''crudeSetupId'',''snapshotId''','''physical'',''crudeSetupId'',''snapshotId''');
 source=replace(source,needle,$body$
 SELECT coalesce(NULLIF(body->'physical','null'::jsonb),jsonb_build_object(
  'referenceCatalogId',assignment.delivery_catalog_id,'referenceRevisionId',assignment.delivery_revision_id,
  'commodity',assignment.commodity_code,'grade',assignment.grade_code,'facility',assignment.facility_code,
  'custodyPoint',assignment.custody_point_code,'quantityCode',g.quantity_code,'clock',assignment.clock_code))
 INTO physical FROM ref_data.crude_grades g WHERE(g.catalog_id,g.revision_id,g.code)=
  (assignment.delivery_catalog_id,assignment.delivery_revision_id,assignment.grade_code);
 resolved=security.resolve_crude_delivery(catalog,physical,start_at,end_at,valuation);
 IF(resolved->>'definitionRevisionId')::uuid IS DISTINCT FROM definition OR resolved->>'assignmentCode' IS DISTINCT FROM body->>'assignmentCode'
  OR(resolved->>'crudeSetupId')::uuid IS DISTINCT FROM(body->>'crudeSetupId')::uuid THEN RAISE EXCEPTION 'crude_setup_conflict';END IF;
 expected=CASE WHEN fee IS NULL THEN 2 ELSE 3 END;
 $body$);
 EXECUTE source;
END $patch$;
COMMIT;
