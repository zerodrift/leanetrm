-- Current consumer: selected-frame correction history, including context aliases.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
DO $guard$
DECLARE h record;
BEGIN
 FOR h IN SELECT * FROM(VALUES
  ('security.read_observation_pins(uuid,uuid,uuid[])','593afbd7a78125e6f726bc480412924fc97e55a99a88318683eb7fe4e25ed932','["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner","openetrm_risk_worker=X/openetrm_security_owner"]'::jsonb),
  ('security.authority_until(text)','f8fd8044c9b0bfd09225417917beeb238f8a3a99b7f554bc875a5d6cd26d7b4a','["openetrm_security_owner=X/openetrm_security_owner","openetrm_runtime=X/openetrm_security_owner","openetrm_risk_worker=X/openetrm_security_owner"]'::jsonb),
  ('security.exact_observation_policy()','873b115bf14fa86dbc9087c3ff1dbf59508d49c3e11be4cac6a9480413e60c3e','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb),
  ('security.observation_visibility_scope(uuid,uuid,uuid[])','804b0cf88d3f589ca640363358faeb5f1c94e71ac693a0d4cffef7074c57895d','["openetrm_security_owner=X/openetrm_security_owner"]'::jsonb)
 )reviewed(signature,hash,acl) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=h.signature::regprocedure
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=h.hash
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner' AND p.prosecdef
   AND to_jsonb(p.proacl)=h.acl AND to_jsonb(p.proconfig)='["search_path=pg_catalog"]'::jsonb)
  THEN RAISE EXCEPTION 'observation_facts_upgrade: unexpected helper %',h.signature;END IF;
 END LOOP;
 IF security.exact_observation_policy() IS NOT TRUE
 THEN RAISE EXCEPTION 'observation_facts_upgrade: unexpected SELECT policies';END IF;
END $guard$;

-- The scoped visibility function returns an opaque set to its caller. Keep its
-- final fetch parameterized by each returned identity instead of letting that
-- join select a scan of unrelated observation history.
DO $point$
DECLARE definition text=pg_get_functiondef('security.read_observation_pins(uuid,uuid,uuid[])'::regprocedure);
 before_text text=$before$  JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.id)=(v.catalog,v.revision,v.observation)$before$;
 after_text text=$after$  CROSS JOIN LATERAL(
   SELECT candidate.* FROM market_data.observations candidate
   WHERE(candidate.catalog_id,candidate.definition_revision_id,candidate.id)=(v.catalog,v.revision,v.observation)
   OFFSET 0
  )o$after$;
BEGIN
 IF(length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>1
 THEN RAISE EXCEPTION 'observation_facts_upgrade: unexpected point lookup';END IF;
 EXECUTE replace(definition,before_text,after_text);
END $point$;

CREATE FUNCTION security.read_observation_facts(requested_catalog uuid,requested_revision uuid,requested_pins uuid[])
 RETURNS SETOF market_data.observations LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;
BEGIN
 IF requested_pins IS NULL OR coalesce(array_ndims(requested_pins),1)<>1 OR array_position(requested_pins,NULL) IS NOT NULL
 THEN RAISE EXCEPTION 'A fact history requires a one-dimensional array of non-null ids' USING ERRCODE='22023';END IF;
 until_at=security.authority_until('ReadMarketData');
 -- Reuse the complete point-reader policy, SELECT, scope and worker checks even
 -- for an empty request. Private candidate construction grants no visibility.
 PERFORM * FROM security.read_observation_pins(requested_catalog,requested_revision,ARRAY[]::uuid[]);
 RETURN QUERY
 WITH anchors AS MATERIALIZED(
  SELECT DISTINCT o.series_id,o.source_code,o.source_event_id,o.observed_at,o.role
  FROM(SELECT DISTINCT unnest(requested_pins) id)p
   JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.id)=(requested_catalog,requested_revision,p.id)
 ), candidates AS MATERIALIZED(
  SELECT DISTINCT o.id FROM anchors a CROSS JOIN LATERAL(
   SELECT v.id FROM market_data.observations v
   WHERE(v.catalog_id,v.definition_revision_id,v.series_id,v.source_code,v.source_event_id,v.observed_at,v.role)=
    (requested_catalog,requested_revision,a.series_id,a.source_code,a.source_event_id,a.observed_at,a.role)
   OFFSET 0
  )o
 ), authorized AS MATERIALIZED(
  SELECT * FROM security.read_observation_pins(requested_catalog,requested_revision,ARRAY(SELECT c.id FROM candidates c))
 ), visible_facts AS MATERIALIZED(
  SELECT DISTINCT o.series_id,o.source_code,o.source_event_id,o.observed_at,o.role
  FROM authorized o WHERE o.id=ANY(requested_pins)
 )
 SELECT o.* FROM authorized o JOIN visible_facts a USING(series_id,source_code,source_event_id,observed_at,role)
 ORDER BY o.observed_at,o.received_at,o.id;
 -- The same authorization set decides both anchors and siblings. A newer close
 -- cannot turn a hidden requested anchor into a different visible sibling.
 -- Results stay buffered until the existing reader's final checks and the
 -- invocation's original authority deadline have both passed.
 PERFORM * FROM security.read_observation_pins(requested_catalog,requested_revision,ARRAY[]::uuid[]);
 IF until_at IS NULL OR clock_timestamp()>=until_at
 THEN RAISE EXCEPTION 'Observation history access expired' USING ERRCODE='42501';END IF;
END;
$$;
ALTER FUNCTION security.read_observation_facts(uuid,uuid,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_observation_facts(uuid,uuid,uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_observation_facts(uuid,uuid,uuid[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
