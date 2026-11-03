-- Presentation only: no financial context, precision, permission or result data.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
CREATE FUNCTION presentation.valid_user_preferences(settings jsonb) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE key text;item jsonb;
BEGIN
 IF jsonb_typeof(settings) IS DISTINCT FROM 'object' OR(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(settings)k) IS DISTINCT FROM
 ARRAY['assistantHidden','assistantMinimized','browseBy','collapsedCurveGroups','collapsedMenus','libraryCollapsed','market','marketType','sidebarCollapsed','tab','timeZone','version']::text[]
 OR settings->'version' IS DISTINCT FROM '1'::jsonb OR jsonb_typeof(settings->'timeZone') IS DISTINCT FROM 'string'
 OR (settings->>'timeZone')!~'^(UTC|(Africa|America|Antarctica|Arctic|Asia|Atlantic|Australia|Europe|Indian|Pacific|Etc)/[A-Za-z0-9_+/-]+)$'
 OR NOT EXISTS(SELECT 1 FROM pg_timezone_names z WHERE z.name=settings->>'timeZone')
 OR coalesce(settings->>'browseBy','') NOT IN('commodity','marketType') OR coalesce(settings->>'marketType','') NOT IN('all','futures','forwards','rates')
 OR coalesce(settings->>'tab','') NOT IN('prices','inputs','dependencies')
 OR NOT(settings->'market'='null'::jsonb OR jsonb_typeof(settings->'market')='string' AND length(settings->>'market') BETWEEN 1 AND 128)
 THEN RETURN false;END IF;
 FOREACH key IN ARRAY ARRAY['assistantHidden','assistantMinimized','libraryCollapsed','sidebarCollapsed'] LOOP
  IF jsonb_typeof(settings->key) IS DISTINCT FROM 'boolean' THEN RETURN false;END IF;
 END LOOP;
 FOREACH key IN ARRAY ARRAY['collapsedCurveGroups','collapsedMenus'] LOOP
  IF jsonb_typeof(settings->key) IS DISTINCT FROM 'array' OR jsonb_array_length(settings->key)>64 THEN RETURN false;END IF;
  FOR item IN SELECT * FROM jsonb_array_elements(settings->key) LOOP
   IF jsonb_typeof(item) IS DISTINCT FROM 'string' OR length(item#>>'{}')>128 THEN RETURN false;END IF;
  END LOOP;
  IF(SELECT count(*) FROM jsonb_array_elements(settings->key))<>(SELECT count(DISTINCT v) FROM jsonb_array_elements(settings->key)v) THEN RETURN false;END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements_text(settings->'collapsedMenus')m WHERE m NOT IN('overview','trading','markets','operations','settings','admin')) THEN RETURN false;END IF;
 RETURN true;
END$$;
CREATE TABLE presentation.user_preference_versions(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,principal_id uuid NOT NULL,namespace text NOT NULL CHECK(namespace IN('api','exchange')),
 version integer NOT NULL CHECK(version>0),revision_id uuid NOT NULL,preferences jsonb NOT NULL CHECK(presentation.valid_user_preferences(preferences)),recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,principal_id,namespace,version),UNIQUE(revision_id),UNIQUE(tenant_id,workspace_id,principal_id,namespace,recorded_at),
 FOREIGN KEY(tenant_id,workspace_id,principal_id) REFERENCES security.workspace_memberships(tenant_id,workspace_id,principal_id)
);
CREATE TABLE presentation.user_preference_commands(
 tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,principal_id uuid NOT NULL,operation_id uuid NOT NULL,namespace text NOT NULL,
 expected_version integer NOT NULL,preferences jsonb NOT NULL,version integer NOT NULL,response jsonb NOT NULL,
 PRIMARY KEY(tenant_id,workspace_id,principal_id,operation_id),
 FOREIGN KEY(tenant_id,workspace_id,principal_id,namespace,version) REFERENCES presentation.user_preference_versions
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['user_preference_versions','user_preference_commands'] LOOP
  EXECUTE format('ALTER TABLE presentation.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE presentation.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE presentation.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON presentation.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON presentation.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION presentation.user_preference_state(value presentation.user_preference_versions) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN value.revision_id IS NULL THEN 'null'::jsonb ELSE jsonb_build_object('version',value.version,'revisionId',value.revision_id,'preferences',value.preferences,'recordedAt',value.recorded_at) END;
$$;
CREATE FUNCTION presentation.preference_owner(c security.request_contexts,scope text) RETURNS text
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT encode(sha256(convert_to(jsonb_build_array(c.tenant_id,c.workspace_id,c.actor_id,scope)::text,'UTF8')),'hex');
$$;
CREATE FUNCTION presentation.user_preferences(scope text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=security.presentation_actor();value presentation.user_preference_versions;
BEGIN
 IF scope IS NULL OR scope NOT IN('api','exchange') THEN RAISE EXCEPTION 'user_preferences_input';END IF;
 SELECT * INTO value FROM presentation.user_preference_versions v WHERE(v.tenant_id,v.workspace_id,v.principal_id,v.namespace)=(c.tenant_id,c.workspace_id,c.actor_id,scope)
 ORDER BY recorded_at DESC LIMIT 1;
 RETURN jsonb_build_object('ownerId',presentation.preference_owner(c,scope),'current',presentation.user_preference_state(value));
END$$;
CREATE FUNCTION presentation.save_user_preferences(scope text,operation uuid,expected integer,preferences jsonb,expected_owner text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=security.presentation_actor();previous presentation.user_preference_commands;
 head presentation.user_preference_versions;next presentation.user_preference_versions;response jsonb;n timestamptz;
BEGIN
 IF expected_owner IS NULL OR expected_owner<>presentation.preference_owner(c,scope) THEN RAISE EXCEPTION 'user_preferences_owner_changed';END IF;
 IF scope IS NULL OR scope NOT IN('api','exchange') OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'
 OR expected IS NULL OR expected<0 OR preferences IS NULL OR octet_length(preferences::text)>24000 OR NOT presentation.valid_user_preferences(preferences)
 THEN RAISE EXCEPTION 'user_preferences_input';END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(c.tenant_id::text||c.workspace_id::text||c.actor_id::text||':preferences:'||scope,0));
 c:=security.presentation_actor();
 SELECT * INTO previous FROM presentation.user_preference_commands x WHERE(x.tenant_id,x.workspace_id,x.principal_id,x.operation_id)=(c.tenant_id,c.workspace_id,c.actor_id,operation);
 IF FOUND THEN
  IF(previous.namespace,previous.expected_version,previous.preferences) IS DISTINCT FROM(scope,expected,preferences) THEN RAISE EXCEPTION 'user_preferences_conflict';END IF;
  PERFORM security.presentation_actor();
  RETURN previous.response;
 END IF;
 SELECT * INTO head FROM presentation.user_preference_versions v WHERE(v.tenant_id,v.workspace_id,v.principal_id,v.namespace)=(c.tenant_id,c.workspace_id,c.actor_id,scope)
 ORDER BY recorded_at DESC LIMIT 1;
 IF coalesce(head.version,0)<>expected THEN RAISE EXCEPTION 'user_preferences_conflict';END IF;
 next:=head;
 IF head.preferences IS DISTINCT FROM preferences THEN
  n:=clock_timestamp();IF head.recorded_at IS NOT NULL AND n<=head.recorded_at THEN RAISE EXCEPTION 'user_preferences_clock';END IF;
  INSERT INTO presentation.user_preference_versions VALUES(c.tenant_id,c.workspace_id,c.actor_id,scope,expected+1,gen_random_uuid(),preferences,n) RETURNING * INTO next;
 END IF;
 response:=presentation.user_preference_state(next);
 INSERT INTO presentation.user_preference_commands VALUES(c.tenant_id,c.workspace_id,c.actor_id,operation,scope,expected,preferences,next.version,response);
 PERFORM security.presentation_actor();
 RETURN response;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='presentation'
 AND p.proname IN('valid_user_preferences','preference_owner','user_preference_state','user_preferences','save_user_preferences') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION presentation.user_preferences(text),presentation.save_user_preferences(text,uuid,integer,jsonb,text) TO openetrm_runtime;
COMMIT;
