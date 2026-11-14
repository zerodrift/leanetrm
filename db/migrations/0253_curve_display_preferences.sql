-- Current consumer: the saved Curves & prices contract-display preference.
-- This changes display selection only; pricing dependencies and held positions retain their contracts.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $upgrade$DECLARE definition text;BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_proc WHERE oid='presentation.valid_user_preferences(jsonb)'::regprocedure
  AND prosrc=$expected$
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
END$expected$ AND NOT prosecdef AND provolatile='s'
  AND proconfig=ARRAY['search_path=pg_catalog'] AND pg_get_userbyid(proowner)='openetrm_security_owner')
 THEN RAISE EXCEPTION 'curve_display_preferences_upgrade: unexpected preference validator';END IF;
 definition=pg_get_functiondef('presentation.valid_user_preferences(jsonb)'::regprocedure);
 EXECUTE replace(definition,$before$
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
END$before$,$after$
DECLARE key text;item jsonb;
BEGIN
 IF settings?'contractDisplay' THEN
  IF jsonb_typeof(settings->'contractDisplay') IS DISTINCT FROM 'object'
   OR(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(settings->'contractDisplay')k) IS DISTINCT FROM ARRAY['contracts','version']::text[]
   OR settings#>>'{contractDisplay,version}' IS DISTINCT FROM 'curve_display_v1'
   OR coalesce(settings#>>'{contractDisplay,contracts}','') NOT IN('captured_contracts_with_status_v1','eligible_contracts_v1')
  THEN RETURN false;END IF;
  RETURN presentation.valid_user_preferences(settings-'contractDisplay');
 END IF;

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
END$after$);
END $upgrade$;
COMMIT;
