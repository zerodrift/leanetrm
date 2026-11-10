-- New bookings admit the dated published choice. Original commands reconcile
-- before this eligibility check, so an accepted trade is never reinterpreted.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION security.require_catalogue_curve_choice(catalog uuid,revision uuid,curve text,valuation timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE catalogue jsonb;source market_data.revisions;
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 catalogue=security.read_request_catalogue(catalog,(valuation AT TIME ZONE 'UTC')::date);
 SELECT * INTO source FROM market_data.revisions r WHERE(r.catalog_id,r.id)=(catalog,revision) AND r.state='sealed';
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM jsonb_array_elements(catalogue->'curves')c
  WHERE(c->>'code',c->>'kind',(c->>'definitionRevisionId')::uuid,(c->>'referenceCatalogId')::uuid,(c->>'referenceRevisionId')::uuid)=
   (curve,'curve',revision,source.reference_catalog_id,source.reference_revision_id))
 THEN RAISE EXCEPTION 'catalogue_choice_changed' USING ERRCODE='P0001';END IF;
END;$$;
CREATE FUNCTION security.require_catalogue_option_choice(catalog uuid,revision uuid,series text,valuation timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE catalogue jsonb;curves text[];
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 catalogue=security.read_request_catalogue(catalog,(valuation AT TIME ZONE 'UTC')::date);
 SELECT array_agg(DISTINCT m.curve_code) INTO curves FROM market_data.contracts c
 JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
 WHERE(c.catalog_id,c.revision_id,c.kind,c.futures_option_series_code)=(catalog,revision,'future_option',series)
 AND EXISTS(SELECT 1 FROM jsonb_array_elements(catalogue->'curves')published
  WHERE(published->>'code',(published->>'definitionRevisionId')::uuid)=(m.curve_code,revision));
 -- The option profile requires one settlement curve per listed contract.
 IF cardinality(curves) IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'catalogue_choice_changed' USING ERRCODE='P0001';END IF;
 PERFORM security.require_catalogue_curve_choice(catalog,revision,curves[1],valuation);
END;$$;
ALTER FUNCTION security.require_catalogue_curve_choice(uuid,uuid,text,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.require_catalogue_option_choice(uuid,uuid,text,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_catalogue_curve_choice(uuid,uuid,text,timestamptz),security.require_catalogue_option_choice(uuid,uuid,text,timestamptz) FROM PUBLIC;
DO $$DECLARE signature regprocedure;definition text;before text;after text;BEGIN
 signature='security.book_futures_with_parties(uuid,uuid,uuid,uuid,text,uuid,timestamptz,timestamptz,numeric,numeric,text,uuid,integer,text,jsonb)'::regprocedure;
 definition=pg_get_functiondef(signature);before=E'\n PERFORM security.record_futures_trade(';
 after=E'\n PERFORM security.require_catalogue_curve_choice(catalog,revision,curve,valuation);\n PERFORM security.record_futures_trade(';
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'futures_choice_admission_drift';END IF;
 EXECUTE replace(definition,before,after);
 signature='security.book_futures_option(uuid,uuid,uuid,uuid,text,numeric,numeric,date,text,timestamptz,timestamptz,text,uuid,integer,text,jsonb)'::regprocedure;
 definition=pg_get_functiondef(signature);before=E'\n INSERT INTO trading.publications VALUES(catalog,ordinal,pg_current_xact_id());';
 after=E'\n PERFORM security.require_catalogue_option_choice(catalog,revision,series,valuation);'||before;
 IF length(definition)-length(replace(definition,before,''))<>length(before) THEN RAISE EXCEPTION 'option_choice_admission_drift';END IF;
 EXECUTE replace(definition,before,after);
END$$;
COMMIT;
