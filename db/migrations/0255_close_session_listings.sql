-- Current consumer: the close-convention picker and eligibility for new close
-- definitions/publications. Listings never rewrite a recorded close or its pins.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE presentation.close_session_listings(
 catalog_id uuid NOT NULL,code ref_data.code NOT NULL,version integer NOT NULL CHECK(version>0),
 session_version integer NOT NULL,name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 80),listed boolean NOT NULL,
 publication_xid xid8 NOT NULL,
 PRIMARY KEY(catalog_id,code,version),UNIQUE(catalog_id,code,version,publication_xid),
 FOREIGN KEY(catalog_id,code,session_version) REFERENCES market_data.close_session_versions(catalog_id,code,version)
);
CREATE TABLE presentation.close_session_listing_receipts(
 catalog_id uuid NOT NULL,code ref_data.code NOT NULL,version integer NOT NULL,publication_xid xid8 NOT NULL,
 known_from ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,code,version),
 FOREIGN KEY(catalog_id,code,version,publication_xid)
  REFERENCES presentation.close_session_listings(catalog_id,code,version,publication_xid)
);
CREATE TABLE presentation.close_session_listing_commands(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 request jsonb NOT NULL,code ref_data.code NOT NULL,version integer NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,code,version) REFERENCES presentation.close_session_listings(catalog_id,code,version)
);
ALTER TABLE presentation.close_session_listings OWNER TO openetrm_security_owner;
ALTER TABLE presentation.close_session_listing_commands OWNER TO openetrm_security_owner;
ALTER TABLE presentation.close_session_listing_receipts OWNER TO openetrm_security_owner;
ALTER TABLE presentation.close_session_listings ENABLE ROW LEVEL SECURITY;
ALTER TABLE presentation.close_session_listings FORCE ROW LEVEL SECURITY;
ALTER TABLE presentation.close_session_listing_commands ENABLE ROW LEVEL SECURITY;
ALTER TABLE presentation.close_session_listing_commands FORCE ROW LEVEL SECURITY;
ALTER TABLE presentation.close_session_listing_receipts ENABLE ROW LEVEL SECURITY;
ALTER TABLE presentation.close_session_listing_receipts FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON presentation.close_session_listings TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY owner_all ON presentation.close_session_listing_commands TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY owner_all ON presentation.close_session_listing_receipts TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON presentation.close_session_listings
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON presentation.close_session_listing_commands
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON presentation.close_session_listing_receipts
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

-- Explicit current-presentation policy: newest committed listing knowledge.
-- A writer can see its own one change; conflicting changes in one transaction
-- are refused rather than resolved by insertion order.
CREATE FUNCTION security.current_close_session_listing(catalog uuid,session text)
 RETURNS presentation.close_session_listings LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE frontier timestamptz;chosen presentation.close_session_listings;pending record;last_commit timestamptz;commit_at timestamptz;
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT known_from INTO last_commit FROM presentation.close_session_listing_receipts
 WHERE(catalog_id,code)=(catalog,session) ORDER BY version DESC LIMIT 1;
 FOR pending IN SELECT l.* FROM presentation.close_session_listings l
 LEFT JOIN presentation.close_session_listing_receipts k USING(catalog_id,code,version)
 WHERE(l.catalog_id,l.code)=(catalog,session) AND k.version IS NULL
 AND l.publication_xid IS DISTINCT FROM pg_current_xact_id_if_assigned() ORDER BY l.version LOOP
  commit_at=security.checked_commit_time(pending.publication_xid);
  IF commit_at<=last_commit THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO presentation.close_session_listing_receipts VALUES(catalog,session,pending.version,pending.publication_xid,commit_at);
  last_commit=commit_at;
 END LOOP;
 SELECT max(CASE WHEN publication_xid=pg_current_xact_id_if_assigned() THEN 'infinity'::timestamptz
  ELSE k.known_from END) INTO frontier
 FROM presentation.close_session_listings l LEFT JOIN presentation.close_session_listing_receipts k USING(catalog_id,code,version,publication_xid)
 WHERE(catalog_id,code)=(catalog,session);
 IF frontier IS NULL THEN RETURN NULL;END IF;
 IF(SELECT count(*) FROM presentation.close_session_listings l LEFT JOIN presentation.close_session_listing_receipts k USING(catalog_id,code,version,publication_xid)
  WHERE(catalog_id,code)=(catalog,session)
  AND CASE WHEN publication_xid=pg_current_xact_id_if_assigned() THEN 'infinity'::timestamptz
      ELSE k.known_from END=frontier)<>1
 THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
 SELECT l.* INTO chosen FROM presentation.close_session_listings l LEFT JOIN presentation.close_session_listing_receipts k USING(catalog_id,code,version,publication_xid)
  WHERE(catalog_id,code)=(catalog,session)
  AND CASE WHEN publication_xid=pg_current_xact_id_if_assigned() THEN 'infinity'::timestamptz
      ELSE k.known_from END=frontier;
 RETURN chosen;
END;$$;

CREATE FUNCTION security.close_session_is_listed(catalog uuid,session text) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN coalesce((security.current_close_session_listing(catalog,session)).listed,true);
END;
$$;

CREATE FUNCTION security.publish_close_session_listing(catalog uuid,operation uuid,session text,session_version integer,
 expected_version integer,display_name text,is_listed boolean) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;body jsonb;prior presentation.close_session_listing_commands;
 current_listing presentation.close_session_listings;outcome presentation.close_session_listings;next_version integer;
BEGIN
 actor=security.custom_curve_actor(catalog,'SealMarketDefinitions');
 IF actor IS NULL OR security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR session IS NULL OR session_version IS NULL
 OR expected_version IS NULL OR expected_version<0 OR display_name IS NULL OR length(btrim(display_name)) NOT BETWEEN 1 AND 80 OR is_listed IS NULL
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 body=jsonb_build_array(session,session_version,expected_version,display_name,is_listed);
 SELECT * INTO prior FROM presentation.close_session_listing_commands WHERE(catalog_id,actor_id,request_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.request<>body THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
  SELECT * INTO outcome FROM presentation.close_session_listings WHERE(catalog_id,code,version)=(catalog,prior.code,prior.version);
 ELSE
  IF session_version IS DISTINCT FROM(SELECT max(version) FROM market_data.close_session_versions
   WHERE(catalog_id,code,state)=(catalog,session,'published'))
  THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
  current_listing=security.current_close_session_listing(catalog,session);
  IF current_listing.version IS NOT NULL AND(current_listing.session_version,current_listing.name,current_listing.listed)=(session_version,display_name,is_listed)
  THEN outcome=current_listing;
  ELSE
   IF coalesce(current_listing.version,0)<>expected_version OR current_listing.publication_xid=pg_current_xact_id_if_assigned()
   THEN RAISE EXCEPTION 'close_conflict' USING ERRCODE='P0001';END IF;
   next_version=coalesce(current_listing.version,0)+1;
   INSERT INTO presentation.close_session_listings VALUES(catalog,session,next_version,session_version,display_name,is_listed,pg_current_xact_id()) RETURNING * INTO outcome;
  END IF;
  INSERT INTO presentation.close_session_listing_commands VALUES(catalog,actor,operation,body,session,outcome.version);
 END IF;
 IF security.market_access(catalog,'SealMarketDefinitions') IS NOT TRUE THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('code',outcome.code,'version',outcome.version,'sessionVersion',outcome.session_version,'name',outcome.name,'listed',outcome.listed);
END;$$;

CREATE OR REPLACE FUNCTION security.close_session_catalog(catalog uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'close_denied' USING ERRCODE='42501';END IF;
 RETURN coalesce((SELECT jsonb_agg(security.close_session_json(catalog,v.code,v.version)||jsonb_build_object('name',
  CASE WHEN v.version=(security.current_close_session_listing(catalog,v.code)).session_version
   THEN(security.current_close_session_listing(catalog,v.code)).name ELSE s.name END) ORDER BY v.code COLLATE "C",v.version)
 FROM market_data.close_session_versions v JOIN market_data.close_sessions s USING(catalog_id,code)
 WHERE v.catalog_id=catalog AND security.close_session_is_listed(catalog,v.code)),'[]'::jsonb);
END;$$;

CREATE OR REPLACE FUNCTION security.close_session_choice(catalog uuid) RETURNS text
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT x.session_code FROM security.request_contexts c JOIN presentation.close_session_choices x
 ON(x.tenant_id,x.workspace_id,x.principal_id,x.catalog_id)=(c.tenant_id,c.workspace_id,c.actor_id,catalog)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
 AND security.market_access(catalog,'ReadMarketData') AND security.close_session_is_listed(catalog,x.session_code);
$$;

CREATE FUNCTION market_data.require_listed_close_session() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE session text;
BEGIN
 IF TG_TABLE_SCHEMA='market_data' AND TG_TABLE_NAME='close_session_versions' THEN session=NEW.code;
 ELSE session=NEW.session_code;END IF;
 IF NOT coalesce((security.current_close_session_listing(NEW.catalog_id,session)).listed,true)
 THEN RAISE EXCEPTION 'close_session' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END;$$;
-- Existing command retries return before these new-fact guards. Binding an
-- exact retained close remains independent of current listing eligibility.
CREATE TRIGGER listed_session BEFORE INSERT OR UPDATE ON market_data.close_session_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.require_listed_close_session();
CREATE TRIGGER listed_session BEFORE INSERT ON market_data.market_closes
 FOR EACH ROW EXECUTE FUNCTION market_data.require_listed_close_session();
CREATE TRIGGER listed_session BEFORE INSERT OR UPDATE ON presentation.close_session_choices
 FOR EACH ROW EXECUTE FUNCTION market_data.require_listed_close_session();

ALTER FUNCTION market_data.require_listed_close_session() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.require_listed_close_session() FROM PUBLIC;
ALTER FUNCTION security.current_close_session_listing(uuid,text) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.close_session_is_listed(uuid,text) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.publish_close_session_listing(uuid,uuid,text,integer,integer,text,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.current_close_session_listing(uuid,text),security.close_session_is_listed(uuid,text),
 security.publish_close_session_listing(uuid,uuid,text,integer,integer,text,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.close_session_is_listed(uuid,text),
 security.publish_close_session_listing(uuid,uuid,text,integer,integer,text,boolean) TO openetrm_runtime;
COMMIT;
