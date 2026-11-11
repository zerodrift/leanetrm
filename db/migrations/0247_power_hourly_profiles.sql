-- Immutable firm local-hour MW factors; native retained schedules emit existing literals.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $guards$DECLARE item record;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('security.ppa_side_document(uuid,uuid)','81bc322c482b88373de9a2d4d9cd44684a632a95d0f3dae8e830736aa99ecec5'),
  ('security.retain_ppa_terms(uuid,jsonb,integer,timestamptz)','c6e18b2c9abe339678f8112477c6a2a4f83d69de40aff4bf21f2b0540e03a759'),
  ('security.ppa_normalize(jsonb)','30ec8747604b5eb629eb25b5b835669921cd1029df47c5d86c25323b23d91fec'),
  ('security.ppa_require_in_term_power(jsonb)','430523ef80a7a91bfb6cd689b63b4361f55b9de63eaf0a7b7fccf1d2b181fcb9')
 ) d(signature,digest) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(item.signature) AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=item.digest)
  THEN RAISE EXCEPTION 'power_profile_upgrade: changed dependency %',item.signature;END IF;
 END LOOP;
END$guards$;

CREATE TABLE trading.ppa_side_hourly_profiles(
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,implementation text NOT NULL CHECK(implementation='local_hour_contractual_mw_factors_v1'),
 factors numeric[] NOT NULL CHECK(array_ndims(factors)=1 AND array_lower(factors,1)=1 AND array_length(factors,1)=24 AND array_position(factors,NULL) IS NULL
  AND 0<=ALL(factors) AND 0<ANY(factors) AND 1e24>ALL(factors)),
 PRIMARY KEY(catalog_id,side_id),FOREIGN KEY(catalog_id,side_id) REFERENCES trading.ppa_sides(catalog_id,id)
);
ALTER TABLE trading.ppa_side_hourly_profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.ppa_side_hourly_profiles FORCE ROW LEVEL SECURITY;
CREATE POLICY private_service ON trading.ppa_side_hourly_profiles TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT SELECT,INSERT ON trading.ppa_side_hourly_profiles TO openetrm_security_owner;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.ppa_side_hourly_profiles FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

CREATE FUNCTION security.ppa_hourly_profile(value jsonb) RETURNS jsonb
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE item jsonb;factor numeric;factors jsonb:='[]';positive boolean:=false;
BEGIN
 IF jsonb_typeof(value) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'ppa_profile_shape';END IF;
 PERFORM security.ppa_keys(value,ARRAY['implementation','factors']);
 IF value->>'implementation' IS DISTINCT FROM 'local_hour_contractual_mw_factors_v1' OR jsonb_typeof(value->'factors') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'ppa_profile_shape';END IF;
 IF jsonb_array_length(value->'factors')<>24 THEN RAISE EXCEPTION 'ppa_profile_shape';END IF;
 FOR item IN SELECT v FROM jsonb_array_elements(value->'factors') v LOOP
  IF jsonb_typeof(item) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'ppa_profile_shape';END IF;
  factor=security.ppa_number(item#>>'{}');IF factor<0 THEN RAISE EXCEPTION 'ppa_profile_shape';END IF;
  positive=positive OR factor>0;factors=factors||jsonb_build_array(factor::text);
 END LOOP;
 IF NOT positive THEN RAISE EXCEPTION 'ppa_profile_shape';END IF;
 RETURN jsonb_build_object('implementation','local_hour_contractual_mw_factors_v1','factors',factors);
END$$;
ALTER FUNCTION security.ppa_hourly_profile(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.ppa_hourly_profile(jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

CREATE FUNCTION trading.guard_ppa_hourly_profile() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s trading.ppa_sides;f numeric;
BEGIN
 SELECT * INTO s FROM trading.ppa_sides WHERE(catalog_id,id)=(NEW.catalog_id,NEW.side_id);
 IF NOT FOUND OR s.quantity_kind<>'profile' OR s.hours_membership<>'all' OR s.interval_minutes<>60 OR NOT EXISTS(
  SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(s.catalog_id,s.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'ppa_profile_publication';END IF;
 FOREACH f IN ARRAY NEW.factors LOOP PERFORM security.ppa_number(f::text);END LOOP;
 RETURN NEW;
END$$;
ALTER FUNCTION trading.guard_ppa_hourly_profile() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.guard_ppa_hourly_profile() FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE TRIGGER fresh_profile BEFORE INSERT ON trading.ppa_side_hourly_profiles FOR EACH ROW EXECUTE FUNCTION trading.guard_ppa_hourly_profile();

DO $patch$DECLARE body text;needle text:=$needle$   IF k IN('price','rate','multiplier','indexMultiplier','adder','conversion','coefficient')$needle$;BEGIN
 body=pg_get_functiondef('security.ppa_normalize(jsonb)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_profile_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$   IF k='hourlyProfile' THEN v=security.ppa_hourly_profile(v);
   ELSIF k IN('price','rate','multiplier','indexMultiplier','adder','conversion','coefficient')$replacement$);
END$patch$;

DO $patch$DECLARE body text;needle text:=$needle$WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]')),
  'paymentRule'$needle$;BEGIN
 body=pg_get_functiondef('security.ppa_side_document(uuid,uuid)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_profile_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]'))||coalesce((SELECT jsonb_build_object('hourlyProfile',jsonb_build_object('implementation',p.implementation,
   'factors',(SELECT jsonb_agg(f::text ORDER BY n) FROM unnest(p.factors) WITH ORDINALITY x(f,n)))) FROM trading.ppa_side_hourly_profiles p WHERE(p.catalog_id,p.side_id)=(catalog,side)),'{}'),
  'paymentRule'$replacement$);
END$patch$;

DO $patch$DECLARE body text;needle text:=$needle$PERFORM security.ppa_keys(q,ARRAY['kind','sources','stage','capacity','energy','hours','slots','assets']);$needle$;BEGIN
 body=pg_get_functiondef('security.retain_ppa_terms(uuid,jsonb,integer,timestamptz)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_profile_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$PERFORM security.ppa_keys(q,ARRAY['kind','sources','stage','capacity','energy','hours','slots','assets']||CASE WHEN q ? 'hourlyProfile' THEN ARRAY['hourlyProfile'] ELSE ARRAY[]::text[] END);
  IF q ? 'hourlyProfile' AND(q->>'kind' IS DISTINCT FROM 'profile' OR h->>'membership' IS DISTINCT FROM 'all' OR minutes<>60) THEN RAISE EXCEPTION 'ppa_profile_shape';END IF;$replacement$);
END$patch$;

DO $patch$DECLARE body text;needle text:=$needle$   INSERT INTO trading.ppa_side_normalization_assets SELECT catalog,side_id,d,$needle$;BEGIN
 body=pg_get_functiondef('security.retain_ppa_terms(uuid,jsonb,integer,timestamptz)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_profile_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$   IF q ? 'hourlyProfile' THEN INSERT INTO trading.ppa_side_hourly_profiles VALUES(catalog,side_id,'local_hour_contractual_mw_factors_v1',
    ARRAY(SELECT security.ppa_number(v) FROM jsonb_array_elements_text(q#>'{hourlyProfile,factors}') v));END IF;
   INSERT INTO trading.ppa_side_normalization_assets SELECT catalog,side_id,d,$replacement$);
END$patch$;

DO $patch$DECLARE body text;needle text:=$needle$request->>'admission' IS DISTINCT FROM 'in_term_fixed_power_contractual_v1'$needle$;BEGIN
 body=pg_get_functiondef('security.ppa_require_in_term_power(jsonb)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_profile_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$(request->>'admission' IS NULL OR request->>'admission' NOT IN('in_term_fixed_power_contractual_v1','in_term_shaped_power_contractual_v1'))$replacement$);
END$patch$;

DO $patch$DECLARE body text;needle text:=$needle$ first_day=(t#>>'{delivery,from}')::date;$needle$;BEGIN
 body=pg_get_functiondef('security.ppa_require_in_term_power(jsonb)'::regprocedure);
 IF length(body)-length(replace(body,needle,''))<>length(needle) THEN RAISE EXCEPTION 'power_profile_upgrade';END IF;
 EXECUTE replace(body,needle,$replacement$ IF(request->>'admission'='in_term_shaped_power_contractual_v1') IS DISTINCT FROM(physical#>'{quantity,hourlyProfile}' IS NOT NULL)
 THEN RAISE EXCEPTION 'ppa_admission';END IF;
 IF request->>'admission'='in_term_shaped_power_contractual_v1' THEN
  IF physical#>>'{quantity,hours,membership}' IS DISTINCT FROM 'all' THEN RAISE EXCEPTION 'ppa_profile_shape';END IF;
  PERFORM security.ppa_hourly_profile(physical#>'{quantity,hourlyProfile}');
 END IF;
 first_day=(t#>>'{delivery,from}')::date;$replacement$);
END$patch$;
COMMIT;
