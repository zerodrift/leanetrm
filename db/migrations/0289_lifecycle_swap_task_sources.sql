-- Consumers: exact source-only tasks for native independent swap periods.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$DECLARE item record;f pg_proc;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('automation.create_lifecycle_job(uuid,jsonb)','a03286b355496bf9d497fa39d6556f48','jsonb',true,ARRAY['catalog','request']::text[]),
  ('automation.require_lifecycle_settings(automation.lifecycle_profiles)','bf6bb2ab7ffa8c88378fbcba8f044a48','void',false,ARRAY['p']::text[])
 )v(signature,body_hash,result_type,runtime_execute,arg_names) LOOP
  SELECT * INTO STRICT f FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(f.prosrc)<>item.body_hash OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.proretset
   OR f.prorettype<>item.result_type::regtype OR f.provariadic<>0 OR f.proargmodes IS NOT NULL OR f.proallargtypes IS NOT NULL
   OR f.proargnames IS DISTINCT FROM item.arg_names OR f.pronargdefaults<>0 OR f.proargdefaults IS NOT NULL OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
   OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
   OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') IS DISTINCT FROM item.runtime_execute
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable
    OR a.privilege_type<>'EXECUTE' OR a.grantee NOT IN(f.proowner,CASE WHEN item.runtime_execute THEN 'openetrm_runtime'::regrole ELSE f.proowner END))
  THEN RAISE EXCEPTION 'lifecycle_swap_task_source_upgrade: unexpected helper %',item.signature;END IF;
 END LOOP;
END$upgrade$;

-- Current task settings are validated before the temporary pricing source is
-- bound. The public historical occurrence reader retains its strict binding.
CREATE FUNCTION automation.lifecycle_swap_occurrence(p automation.lifecycle_profiles) RETURNS uuid
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE profile lifecycle.swap_profiles;origin trading.swap_trade_revisions;terms record;BEGIN
 SELECT * INTO profile FROM lifecycle.swap_profiles s WHERE(s.catalog_id,s.id,s.revision,s.trade_id,s.origin_revision,s.action,s.policy_id,s.policy_revision)=
  (p.catalog_id,p.swap_profile_id,p.swap_profile_revision,p.trade_id,p.swap_revision,p.action,p.policy_id,p.policy_revision);
 SELECT * INTO origin FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision,r.binding_id)=
  (p.catalog_id,p.trade_id,p.swap_revision,p.binding_id);
 IF profile.id IS NULL OR origin.trade_id IS NULL THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;
 PERFORM lifecycle.require_swap_reference(p.catalog_id,p.trade_id,p.action,p.swap_profile_id,p.swap_profile_revision);
 SELECT * INTO terms FROM settlement_data.swap_payment_terms(p.catalog_id,origin.instrument_id,origin.quantity,
  profile.membership_ordinal,profile.delivery_from,profile.delivery_to);
 IF terms.child IS NULL THEN RAISE EXCEPTION 'lifecycle_schedule_settings';END IF;RETURN terms.child;
END$$;

-- The sealed executable side bindings are the exact declared source closure.
-- Qualified custom recipes require every node to contribute to their pinned
-- output; the existing native closure includes their published-source links.
CREATE FUNCTION automation.require_lifecycle_swap_sources(p automation.lifecycle_profiles) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE origin trading.swap_trade_revisions;anchor market_data.market_closes;binding record;BEGIN
 SELECT * INTO STRICT origin FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision,r.binding_id)=
  (p.catalog_id,p.trade_id,p.swap_revision,p.binding_id);
 SELECT * INTO anchor FROM market_data.market_closes c WHERE(c.catalog_id,c.id,c.definition_revision_id)=(p.catalog_id,p.close_id,p.definition_revision_id);
 IF anchor.id IS NULL OR NOT security.market_close_visible(p.catalog_id,p.close_id)
  OR(SELECT count(*) FROM trading.instrument_side_schedules m WHERE(m.catalog_id,m.instrument_id)=(p.catalog_id,origin.instrument_id))<>2
 THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 FOR binding IN SELECT DISTINCT b.custom_catalog_id,b.custom_curve_id,b.custom_version,b.output_node
  FROM trading.instrument_side_schedules m JOIN trading.side_schedule_bindings b ON(b.catalog_id,b.schedule_id)=(m.catalog_id,m.schedule_id)
  WHERE(m.catalog_id,m.instrument_id)=(p.catalog_id,origin.instrument_id) AND b.kind='custom_output'
 LOOP
  IF security.custom_curve_visible(binding.custom_catalog_id,binding.custom_curve_id,binding.custom_version) IS NOT TRUE
   OR NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state)=
    (binding.custom_catalog_id,binding.custom_curve_id,binding.custom_version,binding.output_node,'published'))
  THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 END LOOP;
 IF EXISTS(WITH bindings AS MATERIALIZED(
   SELECT b.* FROM trading.instrument_side_schedules m JOIN trading.side_schedule_bindings b ON(b.catalog_id,b.schedule_id)=(m.catalog_id,m.schedule_id)
   WHERE(m.catalog_id,m.instrument_id)=(p.catalog_id,origin.instrument_id)),
  recipes AS MATERIALIZED(SELECT DISTINCT custom_catalog_id,custom_curve_id,custom_version FROM bindings WHERE kind='custom_output'),
  required AS(
   SELECT source_catalog_id,definition_revision_id FROM bindings WHERE source_catalog_id IS NOT NULL AND definition_revision_id IS NOT NULL
   UNION SELECT source.source_catalog_id,source.source_revision_id FROM recipes r
    CROSS JOIN LATERAL security.custom_curve_native_sources(r.custom_catalog_id,r.custom_curve_id,r.custom_version)source)
  SELECT 1 FROM required source LEFT JOIN market_data.revisions r ON(r.catalog_id,r.id)=(source.source_catalog_id,source.definition_revision_id)
  WHERE source.source_catalog_id<>p.catalog_id OR r.id IS NULL OR security.market_fact_visible(r.catalog_id,r.id,NULL) IS NOT TRUE
   OR security.reference_version_visible(r.reference_catalog_id,r.reference_revision_id) IS NOT TRUE
   OR NOT((r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id)=
     (anchor.catalog_id,anchor.definition_revision_id,anchor.reference_catalog_id,anchor.reference_revision_id)
    OR EXISTS(SELECT 1 FROM market_data.market_close_sources s WHERE(s.catalog_id,s.close_id,s.definition_revision_id,s.reference_catalog_id,s.reference_revision_id)=
     (anchor.catalog_id,anchor.id,r.id,r.reference_catalog_id,r.reference_revision_id))))
 THEN RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
END$$;

DO $patch$DECLARE definition text;before text;after text;BEGIN
 definition=pg_get_functiondef('automation.create_lifecycle_job(uuid,jsonb)'::regprocedure);
 before=$old$SELECT r.definition_revision_id INTO p.definition_revision_id FROM lifecycle.swap_profiles selected$old$;
 after=$new$SELECT anchor.definition_revision_id INTO p.definition_revision_id FROM lifecycle.swap_profiles selected$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'lifecycle_swap_task_source_upgrade';END IF;
 definition=replace(definition,before,after);
 before='lifecycle.swap_profile_occurrence(selected.catalog_id,selected.id,selected.revision)';
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'lifecycle_swap_task_source_upgrade';END IF;
 EXECUTE replace(definition,before,'automation.lifecycle_swap_occurrence(p)');
 definition=pg_get_functiondef('automation.require_lifecycle_settings(automation.lifecycle_profiles)'::regprocedure);
 before='lifecycle.swap_profile_occurrence(p.catalog_id,p.swap_profile_id,p.swap_profile_revision)';
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'lifecycle_swap_task_source_upgrade';END IF;
 definition=replace(definition,before,'automation.lifecycle_swap_occurrence(p)');
 before=$old$ ELSE RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
END$old$;
 after=$new$ ELSE RAISE EXCEPTION 'lifecycle_schedule_source';END IF;
 IF p.handler='financial_swap' THEN PERFORM automation.require_lifecycle_swap_sources(p);END IF;
END$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'lifecycle_swap_task_source_upgrade';END IF;
 EXECUTE replace(definition,before,after);
END$patch$;
DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['automation.lifecycle_swap_occurrence(automation.lifecycle_profiles)'::regprocedure,
  'automation.require_lifecycle_swap_sources(automation.lifecycle_profiles)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
COMMIT;
