-- Installation capacity applies to each catalog. Existing cutoffs keep their identities and evidence.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE security.retained_context_capacity_versions (
 revision bigint PRIMARY KEY CHECK (revision > 0),
 maximum_per_catalog bigint NOT NULL CHECK (maximum_per_catalog > 0),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 recorded_by name NOT NULL DEFAULT session_user
);
INSERT INTO security.retained_context_capacity_versions(revision,maximum_per_catalog) VALUES (1,4096);
CREATE TRIGGER immutable BEFORE UPDATE OR DELETE ON security.retained_context_capacity_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
GRANT SELECT ON security.retained_context_capacity_versions TO openetrm_security_owner;

-- The migration owner runs this directly. An expected revision and value identify a transition,
-- including an unchanged-value request and a retry after a subsequent operator change.
CREATE FUNCTION security.configure_retained_context_capacity(expected_revision bigint, maximum_per_catalog bigint)
RETURNS bigint LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
DECLARE prior security.retained_context_capacity_versions; successor security.retained_context_capacity_versions; recorded timestamptz;
BEGIN
 IF current_setting('transaction_isolation') <> 'read committed' THEN
  RAISE EXCEPTION 'Context capacity changes require READ COMMITTED' USING ERRCODE='25000';
 END IF;
 IF expected_revision IS NULL OR expected_revision < 1 OR maximum_per_catalog IS NULL OR maximum_per_catalog < 1 THEN
  RAISE EXCEPTION 'A positive capacity and expected revision are required' USING ERRCODE='22023';
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT v.* INTO prior FROM security.retained_context_capacity_versions v WHERE v.revision=expected_revision;
 IF NOT FOUND THEN RAISE EXCEPTION 'Unknown context capacity revision' USING ERRCODE='40001'; END IF;
 IF prior.maximum_per_catalog=maximum_per_catalog THEN RETURN prior.revision; END IF;
 SELECT v.* INTO successor FROM security.retained_context_capacity_versions v WHERE v.revision=prior.revision+1;
 IF FOUND THEN
  IF successor.maximum_per_catalog=maximum_per_catalog THEN RETURN successor.revision; END IF;
  RAISE EXCEPTION 'Context capacity revision conflict' USING ERRCODE='40001';
 END IF;
 recorded:=clock_timestamp();
 IF recorded<=prior.recorded_at THEN RAISE EXCEPTION 'Context capacity revision clock did not advance' USING ERRCODE='22008'; END IF;
 INSERT INTO security.retained_context_capacity_versions(revision,maximum_per_catalog,recorded_at)
 VALUES (prior.revision+1,maximum_per_catalog,recorded);
 RETURN prior.revision+1;
END $$;
REVOKE ALL ON FUNCTION security.configure_retained_context_capacity(bigint,bigint) FROM PUBLIC;

CREATE FUNCTION security.current_retained_context_capacity() RETURNS security.retained_context_capacity_versions
LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog AS $$
 SELECT v FROM security.retained_context_capacity_versions v ORDER BY v.revision DESC LIMIT 1
$$;
REVOKE ALL ON FUNCTION security.current_retained_context_capacity() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.current_retained_context_capacity() TO openetrm_security_owner;

-- Every receipt before this migration used revision 1. Each new receipt records the active revision.
ALTER TABLE market_data.temporal_contexts ADD COLUMN capacity_revision bigint NOT NULL DEFAULT 1
 REFERENCES security.retained_context_capacity_versions(revision);
ALTER TABLE market_data.temporal_contexts ALTER COLUMN capacity_revision SET DEFAULT (security.current_retained_context_capacity()).revision;

-- Keep the existing authorization, epoch/clock certification, exact replay and reserve decisions in place.
-- pg_get_functiondef includes the body, argument and result contracts, volatility, security and settings.
DO $upgrade$
DECLARE target record; proc pg_proc; definition text;
BEGIN
 FOR target IN SELECT * FROM (VALUES
  ('security.capture_schedule_context(uuid,date,timestamptz)','402727139acee39a8ea9fd3d6c851ecb8c37df20faf3fa5f49a60748c9f74f47',false),
  ('assistant.pin_market_turn(uuid,bytea,date,jsonb,integer)','d0135b35aa1ac303c4e214eb7d74e8f1b08653389340d22fa8c5ec38e5377ca0',false),
  ('assistant.resolve_turn_context(uuid,bytea,date,jsonb)','e14cd7501b540294686f9958b0e63752c1e6da3095a52c86d2610cee306d1826',true)
 ) approved(signature,digest,runtime_execute)
 LOOP
  SELECT p.* INTO proc FROM pg_proc p WHERE p.oid=to_regprocedure(target.signature);
  definition:=pg_get_functiondef(proc.oid);
  IF encode(sha256(convert_to(definition,'UTF8')),'hex') IS DISTINCT FROM target.digest
   OR proc.proowner IS DISTINCT FROM 'openetrm_security_owner'::regrole
   OR proc.proacl::text IS DISTINCT FROM (CASE WHEN target.runtime_execute
    THEN '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
    ELSE '{openetrm_security_owner=X/openetrm_security_owner}' END)
  THEN RAISE EXCEPTION 'context_capacity_upgrade: unexpected helper %',target.signature; END IF;
  -- All three predicates compare against the same ceiling; the assistant subtracts its turn's reserve.
  -- Policy changes and capture hold the same transaction lock, including the new receipt's revision default.
  EXECUTE replace(definition,'>=4096','>=(security.current_retained_context_capacity()).maximum_per_catalog');
 END LOOP;
END $upgrade$;
COMMIT;
