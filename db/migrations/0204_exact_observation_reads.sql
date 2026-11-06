-- Current consumer: native exact frame reads, including swap ticket entry.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $guard$
DECLARE h record;
BEGIN
 FOR h IN SELECT * FROM(VALUES
  ('security.visible_observations()','92bb537bfe346b19fb52c07eb45cdefc5717756f17174edeb3bfb02d074ae193','["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner","openetrm_risk_worker=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb),
  ('security.market_close_visible(uuid,uuid)','711082c55b83650ae7fafef7f29765e51e47125c89bf664a284182a22f92423b','["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb),
  ('security.market_fact_visible(uuid,uuid,uuid)','01a8faf06607193c1880e7b9cc4fde4d0e8db8c69a7bbc14393eb73a1ad4466f','["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner","openetrm_risk_worker=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog","plan_cache_mode=force_generic_plan"]'::jsonb),
  ('security.authority_until(text)','f8fd8044c9b0bfd09225417917beeb238f8a3a99b7f554bc875a5d6cd26d7b4a','["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner","openetrm_risk_worker=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb),
  ('risk.worker_read()','bc016fa24450147a31f07cbe7cf926f0c9c1990299b1f942943ca75455fabe19','["openetrm_security_owner=X/openetrm_security_owner","openetrm_risk_worker=X/openetrm_security_owner"]'::jsonb,'["search_path=pg_catalog"]'::jsonb)
 ) reviewed(signature,hash,acl,config) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=h.signature::regprocedure
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=h.hash
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
   AND to_jsonb(p.proacl)=h.acl AND to_jsonb(p.proconfig)=h.config)
  THEN RAISE EXCEPTION 'exact_observation_upgrade: unexpected helper %',h.signature;END IF;
 END LOOP;
END $guard$;

-- A definer point read must not bypass later policy or RLS changes. This is
-- checked at installation and at each point read, including publication.
CREATE FUNCTION security.exact_observation_policy() RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT encode(sha256(convert_to(jsonb_build_object('enabled',c.relrowsecurity,'forced',c.relforcerowsecurity,
  'policies',(SELECT jsonb_agg(jsonb_build_object('name',p.polname,'command',p.polcmd::text,'permissive',p.polpermissive,
   'roles',(SELECT jsonb_agg(coalesce(r.rolname::text,'public') ORDER BY coalesce(r.rolname::text,'public'))
    FROM unnest(p.polroles)x LEFT JOIN pg_roles r ON r.oid=x),
   'using',pg_get_expr(p.polqual,p.polrelid),'check',pg_get_expr(p.polwithcheck,p.polrelid)) ORDER BY p.polname)
   FROM pg_policy p WHERE p.polrelid=c.oid AND p.polcmd IN('r','*')))::text,'UTF8')),'hex')
  ='c6785ed3e38f216feaa8865384b9901609face942d57d07c77cc8a2b01eee853'
 FROM pg_class c WHERE c.oid='market_data.observations'::regclass;
$$;
ALTER FUNCTION security.exact_observation_policy() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.exact_observation_policy() FROM PUBLIC;
DO $$BEGIN IF security.exact_observation_policy() IS NOT TRUE THEN RAISE EXCEPTION 'exact_observation_upgrade: unexpected SELECT policies';END IF;END$$;

CREATE FUNCTION security.observation_visibility_scope(requested_catalog uuid,requested_revision uuid,requested_pins uuid[])
 RETURNS TABLE(catalog uuid,revision uuid,observation uuid)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cut record;
BEGIN
 IF requested_pins IS NULL AND(requested_catalog IS NOT NULL OR requested_revision IS NOT NULL)
 OR requested_pins IS NOT NULL AND(requested_catalog IS NULL OR requested_revision IS NULL)
 THEN RAISE EXCEPTION 'An observation read names all exact inputs or the bulk scope' USING ERRCODE='22023';END IF;
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_context_id IS NOT NULL) THEN
  RETURN QUERY
   SELECT o.catalog_id,o.definition_revision_id,o.id FROM market_data.observations o
    WHERE requested_pins IS NULL AND o.catalog_id IN(SELECT security.market_catalogs('ReadMarketData'))
   UNION ALL
   SELECT o.catalog_id,o.definition_revision_id,o.id FROM(SELECT DISTINCT unnest(requested_pins) id)p
    JOIN market_data.observations o ON o.id=p.id AND o.catalog_id=requested_catalog AND o.definition_revision_id=requested_revision
    WHERE requested_pins IS NOT NULL AND o.catalog_id IN(SELECT security.market_catalogs('ReadMarketData'));
  RETURN;
 END IF;
 SELECT * INTO cut FROM security.request_market_cut();
 IF cut.catalog IS NULL OR cut.catalog NOT IN(SELECT security.market_catalogs('ReadMarketData'))
 OR requested_catalog IS NOT NULL AND requested_catalog<>cut.catalog THEN RETURN;END IF;
 -- Keep the original filtered-knowledge join for bulk/history reads. A union
 -- of broad and point candidate drivers can force whole-catalog reads even
 -- when the retained cutoff knows only a small prefix.
 IF requested_pins IS NULL THEN
  RETURN QUERY
  WITH newest AS MATERIALIZED(
   SELECT s.* FROM market_data.market_closes s WHERE s.catalog_id=cut.catalog AND s.business_date=cut.effective_on
    AND s.session_code=(SELECT c.close_session FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
     AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user)
   ORDER BY s.publication_order DESC LIMIT 1
  ), readable_close AS MATERIALIZED(
   SELECT s.* FROM newest s JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(s.catalog_id,s.definition_revision_id)
   WHERE v.effective_from<=cut.effective_on AND cut.effective_on<v.effective_to
    AND security.market_close_visible(s.catalog_id,s.id)
  )
  SELECT DISTINCT e.catalog_id,e.revision_id,e.observation_id FROM market_data.knowledge_events e
   JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
   JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
   JOIN market_data.observations o ON(o.catalog_id,o.id)=(e.catalog_id,e.observation_id)
   WHERE e.catalog_id=cut.catalog AND e.observation_id IS NOT NULL
    AND e.event_order<=cut.known_through AND k.known_from<=cut.known_at
    AND p.effective_from<=cut.effective_on AND cut.effective_on<p.effective_to
    AND o.observed_at<((cut.effective_on+1)::timestamp AT TIME ZONE 'UTC')
  UNION
  SELECT s.catalog_id,s.definition_revision_id,p.observation_id FROM readable_close s
   JOIN market_data.market_close_pins p ON(p.catalog_id,p.close_id)=(s.catalog_id,s.id)
   JOIN market_data.observations o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
   WHERE o.observed_at<=s.at;
  RETURN;
 END IF;
 RETURN QUERY
 WITH candidates AS MATERIALIZED(
  SELECT o.catalog_id,o.definition_revision_id,o.id,o.observed_at FROM(SELECT DISTINCT unnest(requested_pins) id)p
   JOIN market_data.observations o ON o.id=p.id AND o.catalog_id=requested_catalog AND o.definition_revision_id=requested_revision
 ), newest AS MATERIALIZED(
  SELECT s.* FROM market_data.market_closes s WHERE s.catalog_id=cut.catalog AND s.business_date=cut.effective_on
   AND s.session_code=(SELECT c.close_session FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
    AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user)
  ORDER BY s.publication_order DESC LIMIT 1
 ), readable_close AS MATERIALIZED(
  SELECT s.* FROM newest s JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(s.catalog_id,s.definition_revision_id)
  WHERE(requested_revision IS NULL OR s.definition_revision_id=requested_revision)
   AND v.effective_from<=cut.effective_on AND cut.effective_on<v.effective_to
   AND security.market_close_visible(s.catalog_id,s.id)
 )
 SELECT DISTINCT o.catalog_id,o.definition_revision_id,o.id FROM candidates o
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE e.event_order<=cut.known_through AND k.known_from<=cut.known_at
   AND p.effective_from<=cut.effective_on AND cut.effective_on<p.effective_to
   AND o.observed_at<((cut.effective_on+1)::timestamp AT TIME ZONE 'UTC')
 UNION
 SELECT s.catalog_id,s.definition_revision_id,p.observation_id FROM readable_close s
  JOIN market_data.market_close_pins p ON(p.catalog_id,p.close_id)=(s.catalog_id,s.id)
  JOIN candidates o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_id)
  WHERE o.observed_at<=s.at;
END;
$$;
ALTER FUNCTION security.observation_visibility_scope(uuid,uuid,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.observation_visibility_scope(uuid,uuid,uuid[]) FROM PUBLIC;

CREATE OR REPLACE FUNCTION security.visible_observations() RETURNS TABLE(catalog uuid,revision uuid,observation uuid)
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT * FROM security.observation_visibility_scope(NULL,NULL,NULL);
$$;

CREATE FUNCTION security.read_observation_pins(requested_catalog uuid,requested_revision uuid,requested_pins uuid[])
 RETURNS SETOF market_data.observations LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;worker boolean;
BEGIN
 IF requested_catalog IS NULL OR requested_revision IS NULL OR requested_pins IS NULL
 OR coalesce(array_ndims(requested_pins),1)<>1 OR array_position(requested_pins,NULL) IS NOT NULL
 THEN RAISE EXCEPTION 'An exact observation read requires catalog, revision and ids' USING ERRCODE='22023';END IF;
 IF security.exact_observation_policy() IS NOT TRUE
 OR NOT has_table_privilege(session_user,'market_data.observations','SELECT')
 THEN RAISE EXCEPTION 'The exact observation reader cannot enforce this SELECT policy' USING ERRCODE='42501';END IF;
 until_at=security.authority_until('ReadMarketData');worker=pg_has_role(session_user,'openetrm_risk_worker','MEMBER');
 IF until_at IS NULL OR clock_timestamp()>=until_at OR security.market_access(requested_catalog,'ReadMarketData') IS NOT TRUE
 OR(worker AND risk.worker_read() IS NOT TRUE) THEN RAISE EXCEPTION 'Observation read access denied' USING ERRCODE='42501';END IF;
 IF cardinality(requested_pins)=0 THEN RETURN;END IF;
 RETURN QUERY SELECT o.* FROM security.observation_visibility_scope(requested_catalog,requested_revision,requested_pins)v
  JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.id)=(v.catalog,v.revision,v.observation)
  WHERE clock_timestamp()<until_at ORDER BY o.observed_at,o.received_at,o.id;
 IF clock_timestamp()>=until_at OR security.market_access(requested_catalog,'ReadMarketData') IS NOT TRUE
 OR(worker AND risk.worker_read() IS NOT TRUE) OR security.exact_observation_policy() IS NOT TRUE
 OR NOT has_table_privilege(session_user,'market_data.observations','SELECT')
 THEN RAISE EXCEPTION 'Observation read access expired' USING ERRCODE='42501';END IF;
END;
$$;
ALTER FUNCTION security.read_observation_pins(uuid,uuid,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_observation_pins(uuid,uuid,uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_observation_pins(uuid,uuid,uuid[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
