-- Immutable metadata authoring and explicitly reviewed current display labels.
-- Economic inputs, market bundles, observations and trade keys are unchanged.
BEGIN;
CREATE TABLE ref_data.display_edits(
 catalog_id uuid NOT NULL,revision_id uuid NOT NULL,source_revision_id uuid NOT NULL,base_revision_id uuid NOT NULL,
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 currency_code ref_data.code,commodity_code ref_data.code,location_code ref_data.code,location_type_code ref_data.code,
 unit_code ref_data.code,exchange_code ref_data.code,clearing_house_code ref_data.code,futures_product_code ref_data.code,futures_series_code ref_data.code,
 table_name text GENERATED ALWAYS AS(CASE WHEN currency_code IS NOT NULL THEN 'currencies' WHEN commodity_code IS NOT NULL THEN 'commodities'
  WHEN location_code IS NOT NULL THEN 'locations' WHEN location_type_code IS NOT NULL THEN 'location_types' WHEN unit_code IS NOT NULL THEN 'units'
  WHEN exchange_code IS NOT NULL THEN 'exchanges' WHEN clearing_house_code IS NOT NULL THEN 'clearing_houses'
  WHEN futures_product_code IS NOT NULL THEN 'futures_products' WHEN futures_series_code IS NOT NULL THEN 'futures_series' END) STORED NOT NULL,
 item_code ref_data.code GENERATED ALWAYS AS(coalesce(currency_code,commodity_code,location_code,location_type_code,unit_code,exchange_code,clearing_house_code,futures_product_code,futures_series_code)) STORED NOT NULL,
 request jsonb NOT NULL,recorded_at ref_data.instant NOT NULL,
 CHECK(num_nonnulls(currency_code,commodity_code,location_code,location_type_code,unit_code,exchange_code,clearing_house_code,futures_product_code,futures_series_code)=1),
 PRIMARY KEY(catalog_id,revision_id),UNIQUE(catalog_id,revision_id,source_revision_id,tenant_id,workspace_id),
 FOREIGN KEY(catalog_id,revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,source_revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,base_revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,source_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_revision_id,commodity_code) REFERENCES ref_data.commodities(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,commodity_code) REFERENCES ref_data.commodities(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_revision_id,location_type_code) REFERENCES ref_data.location_types(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,location_type_code) REFERENCES ref_data.location_types(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_revision_id,unit_code) REFERENCES ref_data.units(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,unit_code) REFERENCES ref_data.units(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_revision_id,exchange_code) REFERENCES ref_data.exchanges(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,exchange_code) REFERENCES ref_data.exchanges(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_revision_id,clearing_house_code) REFERENCES ref_data.clearing_houses(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,clearing_house_code) REFERENCES ref_data.clearing_houses(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_revision_id,futures_product_code) REFERENCES ref_data.futures_products(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,futures_product_code) REFERENCES ref_data.futures_products(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_revision_id,futures_series_code) REFERENCES ref_data.futures_series(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,revision_id,futures_series_code) REFERENCES ref_data.futures_series(catalog_id,revision_id,code),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE TABLE presentation.reference_display_versions(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,catalog_id uuid NOT NULL,source_revision_id uuid NOT NULL,
 version integer NOT NULL CHECK(version>0),display_revision_id uuid NOT NULL,operation_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL,response jsonb NOT NULL,recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,catalog_id,source_revision_id,version),UNIQUE(tenant_id,workspace_id,actor_id,operation_id),
 FOREIGN KEY(catalog_id,source_revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,display_revision_id,source_revision_id,tenant_id,workspace_id) REFERENCES ref_data.display_edits(catalog_id,revision_id,source_revision_id,tenant_id,workspace_id),
 FOREIGN KEY(tenant_id,workspace_id) REFERENCES security.workspaces(tenant_id,id)
);
CREATE TRIGGER immutable_display_edit BEFORE UPDATE OR DELETE ON ref_data.display_edits FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER immutable_display_version BEFORE UPDATE OR DELETE ON presentation.reference_display_versions FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- Fixed consumer catalog; no caller-selected SQL identifiers or field names.
CREATE FUNCTION security.reference_display_editor_fields(table_name text) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(jsonb_agg(jsonb_build_object('key',field_key,'label',field_label,'kind',field_kind,'nullable',optional) ORDER BY position),'[]'::jsonb)
 FROM(VALUES
  ('currencies',1,'name','Name','text',false),('commodities',1,'name','Name','text',false),
  ('locations',1,'name','Name','text',false),('locations',2,'latitude','Latitude','decimal',true),('locations',3,'longitude','Longitude','decimal',true),
  ('location_types',1,'description','Description','text',false),('units',1,'symbol','Display symbol','text',false),
  ('exchanges',1,'name','Name','text',false),('clearing_houses',1,'name','Name','text',false),
  ('futures_products',1,'name','Name','text',false),('futures_series',1,'series_label','Contract label','text',false)
 )d(category,position,field_key,field_label,field_kind,optional) WHERE category=table_name;
$$;
CREATE FUNCTION security.reference_display_fields(table_name text) RETURNS text[]
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(array_agg(field->>'key' ORDER BY position),ARRAY[]::text[])
 FROM jsonb_array_elements(security.reference_display_editor_fields(table_name)) WITH ORDINALITY e(field,position);
$$;
CREATE FUNCTION security.reference_display_tables() RETURNS text[]
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT ARRAY['dimensions','currencies','units','commodities','commodity_units','measurement_bases','quantity_specifications',
 'unit_conversions','clock_rules','calendars','calendar_days','location_types','locations','commodity_locations','exchanges',
 'clearing_houses','exchange_clearing_houses','interest_rate_definitions','price_index_definitions','sources','delivery_profiles',
 'value_specifications','quote_specifications','settlement_targets','cash_rounding_conventions','futures_products','futures_series',
 'futures_milestones','futures_product_terms','futures_lot_resolutions','futures_basis_settlements','futures_execution_policies','product_evidence'];
$$;
CREATE FUNCTION security.reference_display_actor(catalog uuid,source uuid,permission text,working boolean)
RETURNS security.request_contexts LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;
BEGIN
 IF NOT security.reference_access(catalog,'ReadMarketData') OR NOT security.reference_version_visible(catalog,source)
 OR(permission IS NOT NULL AND NOT security.reference_access(catalog,permission)) THEN RAISE EXCEPTION 'reference_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF c.id IS NULL THEN RAISE EXCEPTION 'reference_denied' USING ERRCODE='42501';END IF;
 IF working AND c.market_context_id IS NOT NULL THEN RAISE EXCEPTION 'historical_read_only';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.revisions WHERE catalog_id=catalog AND id=source AND state='sealed') THEN RAISE EXCEPTION 'reference_edit';END IF;
 RETURN c;
END $$;

CREATE FUNCTION security.save_reference_display_edit(catalog uuid,source uuid,base uuid,operation uuid,table_name text,item text,changes jsonb)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;t text;fields text[];existing ref_data.display_edits;request jsonb;found_item boolean;rows bigint:=0;part bigint;key text;value text;
BEGIN
 c=security.reference_display_actor(catalog,source,'WriteReferenceData',true);
 fields=security.reference_display_fields(table_name);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR base IS NULL OR item IS NULL OR item!~'^[a-z][a-z0-9_.-]{0,63}$'
 OR cardinality(fields)=0 OR jsonb_typeof(changes) IS DISTINCT FROM 'object' OR changes='{}'::jsonb
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(changes)k WHERE NOT k=ANY(fields)) THEN RAISE EXCEPTION 'reference_edit';END IF;
 FOR key,value IN SELECT k,v#>>'{}' FROM jsonb_each(changes)e(k,v) LOOP
  IF key IN('latitude','longitude') THEN
   IF value IS NOT NULL AND(value!~'^-?(0|[1-9][0-9]*)([.][0-9]+)?$' OR length(value)>48) THEN RAISE EXCEPTION 'reference_edit';END IF;
  ELSIF jsonb_typeof(changes->key) IS DISTINCT FROM 'string' OR value IS NULL OR value<>btrim(value) OR length(value) NOT BETWEEN 1 AND 300 OR value~'[[:cntrl:]]' THEN RAISE EXCEPTION 'reference_edit';END IF;
 END LOOP;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;
 PERFORM pg_advisory_xact_lock(hashtextextended(catalog::text||source::text||c.tenant_id::text||c.workspace_id::text,730849147));
 c=security.reference_display_actor(catalog,source,'WriteReferenceData',true);
 request=jsonb_build_object('source',source,'base',base,'table',table_name,'code',item,'values',changes);
 SELECT * INTO existing FROM ref_data.display_edits WHERE catalog_id=catalog AND revision_id=operation;
 IF FOUND THEN
  IF(existing.tenant_id,existing.workspace_id,existing.actor_id) IS DISTINCT FROM(c.tenant_id,c.workspace_id,c.actor_id) THEN RAISE EXCEPTION 'reference_denied' USING ERRCODE='42501';END IF;
  IF existing.request<>request THEN RAISE EXCEPTION 'reference_conflict';END IF;
  RETURN jsonb_build_object('revisionId',operation,'state','draft');
 END IF;
 -- Each review is a single edit from the currently applied labels. A chain
 -- of unpublished drafts must not smuggle unreviewed ancestor changes.
 IF base IS DISTINCT FROM coalesce((SELECT v.display_revision_id FROM presentation.reference_display_versions v
 WHERE(v.tenant_id,v.workspace_id,v.catalog_id,v.source_revision_id)=(c.tenant_id,c.workspace_id,catalog,source)
 ORDER BY v.version DESC LIMIT 1),source) THEN RAISE EXCEPTION 'reference_conflict';END IF;
 IF(SELECT count(*) FROM ref_data.display_edits WHERE catalog_id=catalog AND source_revision_id=source AND(tenant_id,workspace_id)=(c.tenant_id,c.workspace_id))>=64 THEN RAISE EXCEPTION 'reference_limit';END IF;
 EXECUTE format('SELECT EXISTS(SELECT 1 FROM ref_data.%I WHERE catalog_id=$1 AND revision_id=$2 AND code=$3)',table_name) INTO found_item USING catalog,base,item;
 IF NOT found_item THEN RAISE EXCEPTION 'reference_edit';END IF;
 FOREACH t IN ARRAY security.reference_display_tables() LOOP
  EXECUTE format('SELECT count(*) FROM ref_data.%I WHERE catalog_id=$1 AND revision_id=$2',t) INTO part USING catalog,base;rows=rows+part;
 END LOOP;
 IF rows>100000 THEN RAISE EXCEPTION 'reference_limit';END IF;
 INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES(catalog,operation,'display_'||replace(operation::text,'-',''));
 FOREACH t IN ARRAY security.reference_display_tables() LOOP
  EXECUTE format('INSERT INTO ref_data.%1$I SELECT(jsonb_populate_record(NULL::ref_data.%1$I,to_jsonb(r)||jsonb_build_object(''revision_id'',$3)||CASE WHEN $4 AND to_jsonb(r)->>''code''=$5 THEN $6 ELSE ''{}''::jsonb END)).* FROM ref_data.%1$I r WHERE catalog_id=$1 AND revision_id=$2',t)
   USING catalog,base,operation,t=table_name,item,changes;
 END LOOP;
 INSERT INTO ref_data.display_edits(catalog_id,revision_id,source_revision_id,base_revision_id,tenant_id,workspace_id,actor_id,
  currency_code,commodity_code,location_code,location_type_code,unit_code,exchange_code,clearing_house_code,futures_product_code,futures_series_code,request,recorded_at)
 VALUES(catalog,operation,source,base,c.tenant_id,c.workspace_id,c.actor_id,
  CASE WHEN table_name='currencies' THEN item END,CASE WHEN table_name='commodities' THEN item END,
  CASE WHEN table_name='locations' THEN item END,CASE WHEN table_name='location_types' THEN item END,CASE WHEN table_name='units' THEN item END,
  CASE WHEN table_name='exchanges' THEN item END,CASE WHEN table_name='clearing_houses' THEN item END,
  CASE WHEN table_name='futures_products' THEN item END,CASE WHEN table_name='futures_series' THEN item END,request,clock_timestamp());
 PERFORM security.reference_display_actor(catalog,source,'WriteReferenceData',true);
 RETURN jsonb_build_object('revisionId',operation,'state','draft');
END $$;

CREATE FUNCTION security.apply_reference_display(catalog uuid,source uuid,target uuid,operation uuid,expected integer)
RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;previous presentation.reference_display_versions;t text;left_rows jsonb;right_rows jsonb;version integer;request jsonb;response jsonb;fields text[];
BEGIN
 c=security.reference_display_actor(catalog,source,'SealReferenceData',true);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR target IS NULL OR expected IS NULL OR expected<0 THEN RAISE EXCEPTION 'reference_edit';END IF;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR UPDATE;
 PERFORM pg_advisory_xact_lock(hashtextextended(catalog::text||source::text||c.tenant_id::text||c.workspace_id::text,730849147));
 c=security.reference_display_actor(catalog,source,'SealReferenceData',true);
 request=jsonb_build_object('catalog',catalog,'source',source,'target',target,'expectedVersion',expected);
 SELECT * INTO previous FROM presentation.reference_display_versions WHERE(tenant_id,workspace_id,actor_id,operation_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN IF previous.request<>request THEN RAISE EXCEPTION 'reference_conflict';END IF;RETURN previous.response;END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.display_edits WHERE catalog_id=catalog AND revision_id=target AND source_revision_id=source AND(tenant_id,workspace_id)=(c.tenant_id,c.workspace_id)) THEN RAISE EXCEPTION 'reference_edit';END IF;
 -- Every draft INSERT holds this revision FOR SHARE. Lock it before the
 -- compatibility proof so a waiting insert must observe the completed seal.
 PERFORM 1 FROM ref_data.revisions WHERE catalog_id=catalog AND id=target FOR UPDATE;
 SELECT coalesce(max(v.version),0) INTO version FROM presentation.reference_display_versions v WHERE(v.tenant_id,v.workspace_id,v.catalog_id,v.source_revision_id)=(c.tenant_id,c.workspace_id,catalog,source);
 IF version<>expected THEN RAISE EXCEPTION 'reference_conflict';END IF;
 IF(SELECT e.base_revision_id FROM ref_data.display_edits e WHERE e.catalog_id=catalog AND e.revision_id=target) IS DISTINCT FROM
 coalesce((SELECT v.display_revision_id FROM presentation.reference_display_versions v WHERE(v.tenant_id,v.workspace_id,v.catalog_id,v.source_revision_id)=(c.tenant_id,c.workspace_id,catalog,source) ORDER BY v.version DESC LIMIT 1),source)
 THEN RAISE EXCEPTION 'reference_conflict';END IF;
 -- A storage seal is insufficient: prove every key and non-display value equal.
 FOREACH t IN ARRAY security.reference_display_tables() LOOP
  fields=security.reference_display_fields(t)||ARRAY['revision_id'];
  EXECUTE format('SELECT coalesce(jsonb_agg(value ORDER BY value::text COLLATE "C"),''[]''::jsonb) FROM(SELECT to_jsonb(r)-$3 value FROM ref_data.%I r WHERE catalog_id=$1 AND revision_id=$2)q',t) INTO left_rows USING catalog,source,fields;
  EXECUTE format('SELECT coalesce(jsonb_agg(value ORDER BY value::text COLLATE "C"),''[]''::jsonb) FROM(SELECT to_jsonb(r)-$3 value FROM ref_data.%I r WHERE catalog_id=$1 AND revision_id=$2)q',t) INTO right_rows USING catalog,target,fields;
  IF left_rows IS DISTINCT FROM right_rows THEN RAISE EXCEPTION 'reference_economic_change';END IF;
 END LOOP;
 UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id=catalog AND id=target AND state='draft';
 response=jsonb_build_object('displayRevisionId',target,'version',version+1);
 INSERT INTO presentation.reference_display_versions VALUES(c.tenant_id,c.workspace_id,catalog,source,version+1,target,operation,c.actor_id,request,response,clock_timestamp());
 PERFORM security.reference_display_actor(catalog,source,'SealReferenceData',true);
 RETURN response;
END $$;

CREATE FUNCTION security.reference_display_state(catalog uuid,source uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;active presentation.reference_display_versions;t text;fields text[];rows jsonb;overlay jsonb:='{}';projection text;
BEGIN
 c=security.reference_display_actor(catalog,source,NULL,false);
 IF c.market_context_id IS NOT NULL THEN RETURN 'null'::jsonb;END IF;
 SELECT * INTO active FROM presentation.reference_display_versions v WHERE(v.tenant_id,v.workspace_id,v.catalog_id,v.source_revision_id)=(c.tenant_id,c.workspace_id,catalog,source) ORDER BY v.version DESC LIMIT 1;
 IF FOUND THEN
  FOREACH t IN ARRAY security.reference_display_tables() LOOP
   fields=security.reference_display_fields(t);IF cardinality(fields)=0 THEN CONTINUE;END IF;
   SELECT string_agg(format('%L,to_jsonb(b)->%L',field,field),',' ORDER BY field COLLATE "C") INTO projection FROM unnest(ARRAY['code']||fields)field;
   EXECUTE format('SELECT coalesce(jsonb_agg(jsonb_build_object(%s) ORDER BY b.code COLLATE "C"),''[]''::jsonb) FROM ref_data.%I a JOIN ref_data.%I b ON(a.catalog_id,a.code)=(b.catalog_id,b.code) WHERE a.catalog_id=$1 AND a.revision_id=$2 AND b.revision_id=$3 AND(to_jsonb(a)-ARRAY[''revision_id''])<>(to_jsonb(b)-ARRAY[''revision_id''])',projection,t,t) INTO rows USING catalog,source,active.display_revision_id;
   IF jsonb_array_length(rows)>0 THEN overlay=overlay||jsonb_build_object(t,rows);END IF;
  END LOOP;
 END IF;
 PERFORM security.reference_display_actor(catalog,source,NULL,false);
 RETURN jsonb_build_object('sourceRevision',jsonb_build_object('catalogId',catalog,'revisionId',source),'displayRevision',jsonb_build_object('catalogId',catalog,'revisionId',coalesce(active.display_revision_id,source)),'version',coalesce(active.version,0),'references',overlay);
END $$;

DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY security.reference_display_tables() LOOP EXECUTE format('GRANT INSERT ON ref_data.%I TO openetrm_security_owner',t);END LOOP;
END$$;
GRANT INSERT ON ref_data.revisions TO openetrm_security_owner;
GRANT UPDATE(state) ON ref_data.revisions TO openetrm_security_owner;
ALTER TABLE ref_data.display_edits OWNER TO openetrm_security_owner;
ALTER TABLE presentation.reference_display_versions OWNER TO openetrm_security_owner;
ALTER TABLE ref_data.display_edits ENABLE ROW LEVEL SECURITY;ALTER TABLE ref_data.display_edits FORCE ROW LEVEL SECURITY;
ALTER TABLE presentation.reference_display_versions ENABLE ROW LEVEL SECURITY;ALTER TABLE presentation.reference_display_versions FORCE ROW LEVEL SECURITY;
CREATE FUNCTION security.reference_display_edit_visible(catalog uuid,source uuid,tenant uuid,workspace uuid) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.current_write_context() AND security.reference_access(catalog,'ReadMarketData') AND security.reference_version_visible(catalog,source)
 AND EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
 AND c.runtime_login=session_user AND(c.tenant_id,c.workspace_id)=(tenant,workspace));
$$;
ALTER FUNCTION security.reference_display_edit_visible(uuid,uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.reference_display_edit_visible(uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.reference_display_edit_visible(uuid,uuid,uuid,uuid) TO openetrm_runtime;
CREATE POLICY scoped_read ON ref_data.display_edits FOR SELECT TO openetrm_runtime USING(security.reference_display_edit_visible(catalog_id,source_revision_id,tenant_id,workspace_id));
GRANT SELECT ON ref_data.display_edits TO openetrm_runtime;
ALTER FUNCTION security.reference_display_editor_fields(text) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.reference_display_fields(text) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.reference_display_tables() OWNER TO openetrm_security_owner;
ALTER FUNCTION security.reference_display_actor(uuid,uuid,text,boolean) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.save_reference_display_edit(uuid,uuid,uuid,uuid,text,text,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.apply_reference_display(uuid,uuid,uuid,uuid,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.reference_display_state(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.reference_display_editor_fields(text),security.reference_display_fields(text),security.reference_display_tables(),security.reference_display_actor(uuid,uuid,text,boolean),security.save_reference_display_edit(uuid,uuid,uuid,uuid,text,text,jsonb),security.apply_reference_display(uuid,uuid,uuid,uuid,integer),security.reference_display_state(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.reference_display_editor_fields(text) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.save_reference_display_edit(uuid,uuid,uuid,uuid,text,text,jsonb),security.apply_reference_display(uuid,uuid,uuid,uuid,integer),security.reference_display_state(uuid,uuid) TO openetrm_runtime;
COMMIT;
