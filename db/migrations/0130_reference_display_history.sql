-- The applied display versions of one reference item (D263), newest first: when each was applied, the workspace member who
-- authored its edit (by their current display name) and only the fields that edit changed. A futures product's history
-- includes its own contracts' label edits. Historical contexts keep their original labels and so list none; in working
-- data only people who may write or seal reference data read it. The latest 20 versions are listed; `truncated` says earlier ones exist.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;

CREATE FUNCTION security.reference_display_history(catalog uuid,source uuid,item_table text,item text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;listed jsonb;
BEGIN
 c=security.reference_display_actor(catalog,source,NULL,false);
 IF c.market_context_id IS NOT NULL THEN RETURN jsonb_build_object('versions','[]'::jsonb,'truncated',false);END IF;
 IF NOT(security.reference_access(catalog,'WriteReferenceData') OR security.reference_access(catalog,'SealReferenceData')) THEN
  RAISE EXCEPTION 'reference_denied' USING ERRCODE='42501';END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('version',h.version,'table',h.table_name,'code',h.item_code,'recordedAt',h.recorded_at,
  'author',h.display_name,'changes',h.changes) ORDER BY h.version DESC),'[]'::jsonb) INTO listed
 FROM(SELECT v.version,e.table_name,e.item_code,v.recorded_at,m.display_name,e.request->'values' changes
  FROM presentation.reference_display_versions v
  JOIN ref_data.display_edits e ON(e.catalog_id,e.revision_id)=(v.catalog_id,v.display_revision_id)
  JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(e.tenant_id,e.workspace_id,e.actor_id)
  WHERE(v.tenant_id,v.workspace_id,v.catalog_id,v.source_revision_id)=(c.tenant_id,c.workspace_id,catalog,source)
  AND((e.table_name,e.item_code)=(item_table,item) OR item_table='futures_products' AND e.table_name='futures_series'
   AND EXISTS(SELECT 1 FROM ref_data.futures_series s WHERE(s.catalog_id,s.revision_id,s.code,s.product_code)=(catalog,source,e.item_code,item)))
  ORDER BY v.version DESC LIMIT 21)h;
 PERFORM security.reference_display_actor(catalog,source,NULL,false);
 IF NOT(security.reference_access(catalog,'WriteReferenceData') OR security.reference_access(catalog,'SealReferenceData')) THEN
  RAISE EXCEPTION 'reference_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('versions',(SELECT coalesce(jsonb_agg(v ORDER BY n),'[]'::jsonb) FROM jsonb_array_elements(listed) WITH ORDINALITY e(v,n) WHERE n<=20),
  'truncated',jsonb_array_length(listed)>20);
END $$;
ALTER FUNCTION security.reference_display_history(uuid,uuid,text,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.reference_display_history(uuid,uuid,text,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.reference_display_history(uuid,uuid,text,text) TO openetrm_runtime;
COMMIT;
