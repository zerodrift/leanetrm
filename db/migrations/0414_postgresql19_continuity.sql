-- D468: explicit stock PostgreSQL 18 -> 19 continuity. Preparation and
-- admission are private installation-owner operations under a writer fence.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$DECLARE helper pg_proc;BEGIN
 SELECT * INTO STRICT helper FROM pg_proc WHERE oid='security.checked_commit_time(xid8)'::regprocedure;
 IF md5(helper.prosrc)<>'99bd0d6feacc88f5ea1a3458f5c2a24c'
 OR helper.proowner<>'openetrm_security_owner'::regrole OR NOT helper.prosecdef
 OR helper.provolatile<>'v' OR helper.proisstrict OR helper.proleakproof OR helper.proparallel<>'u'
 OR helper.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
 OR(SELECT array_agg(x::text ORDER BY x::text) FROM unnest(coalesce(helper.proacl,acldefault('f',helper.proowner))) x)
   IS DISTINCT FROM ARRAY['openetrm_security_owner=X/openetrm_security_owner']
 THEN RAISE EXCEPTION 'postgresql_upgrade_helper: security.checked_commit_time(xid8)';END IF;
END$guard$;

CREATE TABLE security.postgresql_upgrade_source (
 id uuid PRIMARY KEY,
 source_system_id text NOT NULL CHECK(source_system_id ~ '^[0-9]+$'),
 source_timeline integer NOT NULL CHECK(source_timeline>0),
 source_version integer NOT NULL CHECK(source_version>=180000 AND source_version<190000),
 seal_xid xid8 NOT NULL, sealed_at timestamptz NOT NULL CHECK(isfinite(sealed_at)),
 source_schema_digest text NOT NULL CHECK(source_schema_digest ~ '^[0-9a-f]{64}$'),
 schema_digest text NOT NULL CHECK(schema_digest ~ '^[0-9a-f]{64}$'),
 data_digest text NOT NULL CHECK(data_digest ~ '^[0-9a-f]{64}$'),
 times_digest text NOT NULL CHECK(times_digest ~ '^[0-9a-f]{64}$'),
 times_count bigint NOT NULL CHECK(times_count>=0)
);
CREATE TABLE security.postgresql_upgrade_times (
 source_id uuid NOT NULL REFERENCES security.postgresql_upgrade_source(id),
 full_xid xid8 NOT NULL, committed_at timestamptz NOT NULL
 CHECK(isfinite(committed_at) AND committed_at<TIMESTAMPTZ '9999-12-31 00:00:00+00'),
 PRIMARY KEY(source_id,full_xid)
);
CREATE TABLE security.postgresql_upgrade_admission (
 singleton boolean PRIMARY KEY CHECK(singleton),
 source_id uuid NOT NULL UNIQUE REFERENCES security.postgresql_upgrade_source(id),
 target_system_id text NOT NULL CHECK(target_system_id ~ '^[0-9]+$'),
 target_timeline integer NOT NULL CHECK(target_timeline>0),
 target_version integer NOT NULL CHECK(target_version>=190000 AND target_version<200000),
 admitted_at timestamptz NOT NULL CHECK(isfinite(admitted_at))
);
DO $$DECLARE relation text;BEGIN
 FOREACH relation IN ARRAY ARRAY['postgresql_upgrade_source','postgresql_upgrade_times','postgresql_upgrade_admission'] LOOP
  EXECUTE format('CREATE TRIGGER immutable_upgrade_proof BEFORE UPDATE OR DELETE ON security.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',relation);
  EXECUTE format('REVOKE ALL ON security.%I FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',relation);
  EXECUTE format('GRANT SELECT ON security.%I TO openetrm_security_owner',relation);
 END LOOP;
END$$;

-- Source identity is the catalog's original lineage, not a claim that the
-- current physical cluster kept its old identifier. Only this exact admitted
-- successor may continue that lineage. New catalogs still use native identity.
CREATE FUNCTION security.knowledge_source_current(system_id text,timeline integer) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT current_setting('track_commit_timestamp')='on' AND NOT pg_is_in_recovery()
 AND (($1,$2)=((pg_control_system()).system_identifier::text,(pg_control_checkpoint()).timeline_id)
 OR EXISTS(SELECT 1 FROM security.postgresql_upgrade_source s
 JOIN security.postgresql_upgrade_admission a ON a.source_id=s.id
 WHERE(s.source_system_id,s.source_timeline)=($1,$2)
 AND(a.target_system_id,a.target_timeline)=((pg_control_system()).system_identifier::text,(pg_control_checkpoint()).timeline_id)
 AND current_setting('server_version_num')::integer/10000=a.target_version/10000))
$$;
ALTER FUNCTION security.knowledge_source_current(text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.knowledge_source_current(text,integer) FROM PUBLIC;

CREATE OR REPLACE FUNCTION security.checked_commit_time(full_xid xid8) RETURNS timestamptz
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE distance numeric;result timestamptz;
BEGIN
 IF current_setting('track_commit_timestamp')<>'on' THEN RAISE EXCEPTION 'knowledge_tracking' USING ERRCODE='P0001';END IF;
 SELECT t.committed_at INTO result FROM security.postgresql_upgrade_times t
 JOIN security.postgresql_upgrade_source s ON s.id=t.source_id
 JOIN security.postgresql_upgrade_admission a ON a.source_id=s.id
 WHERE t.full_xid=checked_commit_time.full_xid AND t.full_xid<s.seal_xid
 AND security.knowledge_source_current(s.source_system_id,s.source_timeline);
 IF NOT FOUND THEN
  distance=pg_snapshot_xmax(pg_current_snapshot())::text::numeric-full_xid::text::numeric;
  IF distance<=0 OR distance>=2147483648 THEN RAISE EXCEPTION 'knowledge_retention' USING ERRCODE='P0001';END IF;
  result=pg_xact_commit_timestamp(((full_xid::text::numeric % 4294967296)::bigint::text)::xid);
 END IF;
 IF result IS NULL THEN RAISE EXCEPTION 'knowledge_retention' USING ERRCODE='P0001';END IF;
 IF NOT isfinite(result) OR result>=TIMESTAMPTZ '9999-12-31 00:00:00+00' THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
 RETURN result;
END$$;

-- All persistent user-table contents, including immutable owner inputs,
-- authority, clocks and uncertified publications. Fixed UTC/ISO formatting and
-- bytewise ordering keep the witness independent of row order and collation.
-- The three upgrade proof tables are checked separately, never self-hashed.
CREATE FUNCTION security.postgresql_upgrade_data_digest() RETURNS text
LANGUAGE plpgsql SET search_path=pg_catalog SET timezone='UTC' SET datestyle='ISO, YMD' SET extra_float_digits=3 AS $$
DECLARE relation record;rows bigint;fingerprint text;manifest text='';sequence_state jsonb;authority jsonb;
BEGIN
 IF NOT(SELECT rolsuper FROM pg_roles WHERE rolname=session_user)
 THEN RAISE EXCEPTION 'postgresql_upgrade_denied' USING ERRCODE='42501';END IF;
 IF current_setting('transaction_isolation')<>'read committed'
 THEN RAISE EXCEPTION 'postgresql_upgrade_snapshot' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
  WHERE left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema'
  AND(c.relkind IN('p','f','m') OR c.relkind IN('r','S') AND c.relpersistence<>'p'))
 THEN RAISE EXCEPTION 'postgresql_upgrade_relation' USING ERRCODE='P0001';END IF;
 FOR relation IN SELECT n.nspname,c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE c.relkind='r' AND left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema'
 AND NOT(n.nspname='security' AND c.relname IN('postgresql_upgrade_source','postgresql_upgrade_times','postgresql_upgrade_admission'))
 ORDER BY n.nspname COLLATE "C",c.relname COLLATE "C" LOOP
  EXECUTE format('LOCK TABLE %I.%I IN SHARE MODE',relation.nspname,relation.relname);
 END LOOP;
 FOR relation IN SELECT n.nspname,c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE c.relkind='r' AND left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema'
 AND NOT(n.nspname='security' AND c.relname IN('postgresql_upgrade_source','postgresql_upgrade_times','postgresql_upgrade_admission'))
 ORDER BY n.nspname COLLATE "C",c.relname COLLATE "C" LOOP
  EXECUTE format('SELECT count(*),encode(sha256(convert_to(coalesce(string_agg(h,'''' ORDER BY h COLLATE "C"),''''),''UTF8'')),''hex'') FROM (SELECT encode(sha256(convert_to(to_jsonb(t)::text,''UTF8'')),''hex'') h FROM %I.%I t) hashes',relation.nspname,relation.relname) INTO rows,fingerprint;
  manifest=manifest||jsonb_build_array(relation.nspname,relation.relname,rows,fingerprint)::text||E'\n';
 END LOOP;
 FOR relation IN SELECT n.nspname,c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE c.relkind='S' AND left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema'
 ORDER BY n.nspname COLLATE "C",c.relname COLLATE "C" LOOP
  EXECUTE format('SELECT jsonb_build_array(last_value,is_called) FROM %I.%I',relation.nspname,relation.relname) INTO sequence_state;
  manifest=manifest||jsonb_build_array(relation.nspname,relation.relname,sequence_state)::text||E'\n';
 END LOOP;
 -- Physical OIDs and newly added built-in roles are not application authority.
 -- Password material stays inside the digest, never in an operation result.
 SELECT coalesce(jsonb_agg(jsonb_build_array(rolname,rolsuper,rolinherit,rolcreaterole,rolcreatedb,
  rolcanlogin,rolreplication,rolbypassrls,rolconnlimit,rolpassword,rolvaliduntil)
  ORDER BY rolname COLLATE "C"),'[]'::jsonb) INTO authority FROM pg_authid WHERE left(rolname,3)<>'pg_';
 manifest=manifest||authority::text||E'\n';
 SELECT coalesce(jsonb_agg(jsonb_build_array(r.rolname,m.rolname,g.rolname,a.admin_option,a.inherit_option,a.set_option)
  ORDER BY r.rolname COLLATE "C",m.rolname COLLATE "C",g.rolname COLLATE "C"),'[]'::jsonb) INTO authority
 FROM pg_auth_members a JOIN pg_authid r ON r.oid=a.roleid JOIN pg_authid m ON m.oid=a.member JOIN pg_authid g ON g.oid=a.grantor
 WHERE left(m.rolname,3)<>'pg_' OR left(r.rolname,3)<>'pg_';
 manifest=manifest||authority::text||E'\n';
 SELECT jsonb_build_array(d.datname,r.rolname,d.datallowconn,d.datconnlimit,d.datacl::text,d.encoding,d.datcollate,d.datctype,
  d.datlocprovider,d.datlocale,d.daticurules,d.datcollversion,d.datistemplate)
 INTO authority FROM pg_database d JOIN pg_authid r ON r.oid=d.datdba WHERE d.datname=current_database();
 manifest=manifest||authority::text||E'\n';
 SELECT coalesce(jsonb_agg(jsonb_build_array(d.datname,r.rolname,s.setconfig)
  ORDER BY d.datname COLLATE "C" NULLS FIRST,r.rolname COLLATE "C" NULLS FIRST),'[]'::jsonb)
 INTO authority FROM pg_db_role_setting s LEFT JOIN pg_database d ON d.oid=s.setdatabase LEFT JOIN pg_authid r ON r.oid=s.setrole
 WHERE s.setdatabase=0 OR d.datname=current_database();
 manifest=manifest||authority::text||E'\n';
 RETURN encode(sha256(convert_to(manifest,'UTF8')),'hex');
END$$;
REVOKE ALL ON FUNCTION security.postgresql_upgrade_data_digest() FROM PUBLIC;

CREATE FUNCTION security.prepare_postgresql_upgrade(request_id uuid,source_schema_digest text,expected_schema_digest text) RETURNS security.postgresql_upgrade_source
LANGUAGE plpgsql SET search_path=pg_catalog SET timezone='UTC' AS $$
DECLARE proof security.postgresql_upgrade_source;field record;relation text;item record;
 native_time timestamptz;retained_time timestamptz;digest text;prior_digest text;prior_count bigint;
BEGIN
 IF NOT(SELECT rolsuper FROM pg_roles WHERE rolname=session_user)
 THEN RAISE EXCEPTION 'postgresql_upgrade_denied' USING ERRCODE='42501';END IF;
 IF current_setting('transaction_isolation')<>'read committed'
 THEN RAISE EXCEPTION 'postgresql_upgrade_snapshot' USING ERRCODE='P0001';END IF;
 IF request_id IS NULL OR source_schema_digest IS NULL OR source_schema_digest !~ '^[0-9a-f]{64}$'
 OR expected_schema_digest IS NULL OR expected_schema_digest !~ '^[0-9a-f]{64}$'
 THEN RAISE EXCEPTION 'postgresql_upgrade_proof' USING ERRCODE='P0001';END IF;
 IF current_setting('server_version_num')::integer NOT BETWEEN 180000 AND 189999
 OR current_setting('track_commit_timestamp')<>'on' OR pg_is_in_recovery()
 THEN RAISE EXCEPTION 'postgresql_upgrade_source' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO proof FROM security.postgresql_upgrade_source WHERE id=request_id;
 IF FOUND THEN
  IF proof.schema_digest<>expected_schema_digest OR proof.source_schema_digest<>source_schema_digest OR(proof.source_system_id,proof.source_timeline)
    IS DISTINCT FROM((pg_control_system()).system_identifier::text,(pg_control_checkpoint()).timeline_id)
  THEN RAISE EXCEPTION 'postgresql_upgrade_proof' USING ERRCODE='P0001';END IF;
  RETURN proof;
 END IF;
 digest=security.postgresql_upgrade_data_digest();
 IF EXISTS(SELECT 1 FROM market_data.knowledge_clocks k WHERE(k.system_identifier,k.timeline_id)
  IS DISTINCT FROM((pg_control_system()).system_identifier::text,(pg_control_checkpoint()).timeline_id))
 THEN RAISE EXCEPTION 'postgresql_upgrade_source' USING ERRCODE='P0001';END IF;
 CREATE TEMP TABLE upgrade_required_xids(full_xid xid8 PRIMARY KEY) ON COMMIT DROP;
 CREATE TEMP TABLE upgrade_known_times(full_xid xid8 NOT NULL,committed_at timestamptz NOT NULL) ON COMMIT DROP;
 -- Every persistent publication/origin xid8 is retained. The only unrelated
 -- xid8 fields are ephemeral request/attempt transaction bindings; a new
 -- unclassified field must be reviewed before this inventory can run.
 FOR field IN SELECT n.nspname,c.relname,a.attname FROM pg_attribute a
 JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE c.relkind='r' AND a.atttypid='xid8'::regtype AND a.attnum>0 AND NOT a.attisdropped
 AND left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema'
 AND NOT(n.nspname='security' AND c.relname IN('postgresql_upgrade_source','postgresql_upgrade_times')) LOOP
  IF(field.nspname||'.'||field.relname||'.'||field.attname)<>ALL(ARRAY[
   'automation.pilot_report_receipts.publication_xid','automation.pilot_reports.publication_xid',
   'market_data.catalogue_knowledge_receipts.publication_xid','market_data.catalogue_publications.publication_xid',
   'market_data.contract_date_admission_receipts.publication_xid','market_data.contract_date_admissions.publication_xid',
   'market_data.contract_date_knowledge_receipts.publication_xid','market_data.contract_date_versions.publication_xid',
   'market_data.crude_setup_receipts.publication_xid','market_data.crude_setup_versions.publication_xid',
   'market_data.curve_definition_knowledge_receipts.publication_xid','market_data.curve_definition_versions.publication_xid',
   'market_data.custom_curve_activation_plans.publication_xid','market_data.custom_curve_activation_receipts.publication_xid',
   'market_data.custom_curve_knowledge_receipts.publication_xid','market_data.custom_curve_versions.publication_xid',
   'market_data.date_schedule_versions.publication_xid','market_data.gas_setup_receipts.publication_xid',
   'market_data.gas_setup_versions.publication_xid','market_data.interval_series_knowledge_receipts.publication_xid',
   'market_data.interval_series_revisions.publication_xid','market_data.knowledge_clocks.origin_xid',
   'market_data.knowledge_events.publication_xid','market_data.knowledge_publications.publication_xid',
   'market_data.market_close_receipts.publication_xid','market_data.market_closes.publication_xid',
   'market_data.market_knowledge_receipts.publication_xid','power_data.batches.publication_xid',
   'presentation.close_session_listing_receipts.publication_xid','presentation.close_session_listings.publication_xid',
   'risk.definition_run_publications.publication_xid','risk.definition_run_receipts.publication_xid',
   'settlement_data.meter_finality_heads.publication_xid','settlement_data.meter_finality_receipts.publication_xid',
   'trading.knowledge_receipts.publication_xid','trading.publications.publication_xid','trading.rec_inventory_facts.publication_xid']) THEN
   IF(field.nspname,field.relname,field.attname) IN(('security','request_contexts','transaction_id'),('risk','pending_clock_attestations','transaction_id')) THEN CONTINUE;END IF;
   RAISE EXCEPTION 'postgresql_upgrade_inventory' USING ERRCODE='P0001';
  END IF;
  EXECUTE format('INSERT INTO pg_temp.upgrade_required_xids SELECT DISTINCT %I FROM %I.%I WHERE %I IS NOT NULL ON CONFLICT DO NOTHING',field.attname,field.nspname,field.relname,field.attname);
 END LOOP;
 -- These existing immutable receipts reference the exact publication xid.
 -- They can prove a timestamp already pruned from PostgreSQL's native store.
 FOREACH relation IN ARRAY ARRAY[
  'market_data.knowledge_publications','market_data.market_knowledge_receipts','market_data.catalogue_knowledge_receipts',
  'market_data.curve_definition_knowledge_receipts','market_data.crude_setup_receipts','market_data.contract_date_admission_receipts',
  'market_data.gas_setup_receipts','market_data.custom_curve_activation_receipts','market_data.interval_series_knowledge_receipts',
  'market_data.market_close_receipts','market_data.contract_date_knowledge_receipts','market_data.custom_curve_knowledge_receipts',
  'risk.definition_run_receipts','settlement_data.meter_finality_receipts','automation.pilot_report_receipts',
  'trading.knowledge_receipts','presentation.close_session_listing_receipts'] LOOP
  EXECUTE format('INSERT INTO pg_temp.upgrade_known_times SELECT publication_xid,known_from FROM %s',relation::regclass);
 END LOOP;
 INSERT INTO pg_temp.upgrade_known_times SELECT b.publication_xid,r.known_at FROM power_data.batches b JOIN power_data.receipts r USING(sequence);
 INSERT INTO pg_temp.upgrade_known_times SELECT origin_xid,coverage_from FROM market_data.knowledge_clocks WHERE coverage_from IS NOT NULL;
 -- An aborted preparation remains immutable evidence. A later explicit
 -- attempt may preserve its exact times even after native metadata is pruned.
 FOR item IN SELECT * FROM security.postgresql_upgrade_source s
 WHERE(s.source_system_id,s.source_timeline)=((pg_control_system()).system_identifier::text,(pg_control_checkpoint()).timeline_id) LOOP
  SELECT count(*),encode(sha256(convert_to(coalesce(string_agg(full_xid::text||':'||encode(timestamptz_send(committed_at),'hex'),E'\n' ORDER BY full_xid),''),'UTF8')),'hex')
  INTO prior_count,prior_digest FROM security.postgresql_upgrade_times WHERE source_id=item.id;
  IF(prior_count,prior_digest) IS DISTINCT FROM(item.times_count,item.times_digest)
  OR EXISTS(SELECT 1 FROM security.postgresql_upgrade_times WHERE source_id=item.id AND(full_xid>=item.seal_xid OR committed_at>item.sealed_at))
  THEN RAISE EXCEPTION 'postgresql_upgrade_proof' USING ERRCODE='P0001';END IF;
 END LOOP;
 INSERT INTO pg_temp.upgrade_known_times SELECT t.full_xid,t.committed_at
 FROM security.postgresql_upgrade_times t JOIN security.postgresql_upgrade_source s ON s.id=t.source_id
 WHERE(s.source_system_id,s.source_timeline)=((pg_control_system()).system_identifier::text,(pg_control_checkpoint()).timeline_id);
 IF EXISTS(SELECT 1 FROM pg_temp.upgrade_known_times GROUP BY full_xid HAVING count(DISTINCT committed_at)<>1)
 THEN RAISE EXCEPTION 'postgresql_upgrade_time_conflict' USING ERRCODE='P0001';END IF;
 proof.id=request_id;proof.source_system_id=(pg_control_system()).system_identifier::text;
 proof.source_timeline=(pg_control_checkpoint()).timeline_id;proof.source_version=current_setting('server_version_num')::integer;
 proof.seal_xid=pg_current_xact_id();proof.sealed_at=clock_timestamp();proof.data_digest=digest;proof.schema_digest=expected_schema_digest;
 proof.source_schema_digest=source_schema_digest;
 CREATE TEMP TABLE upgrade_exact_times(full_xid xid8 PRIMARY KEY,committed_at timestamptz NOT NULL) ON COMMIT DROP;
 FOR item IN SELECT full_xid FROM pg_temp.upgrade_required_xids ORDER BY full_xid LOOP
  IF item.full_xid>=proof.seal_xid THEN RAISE EXCEPTION 'postgresql_upgrade_uncommitted' USING ERRCODE='P0001';END IF;
  SELECT min(committed_at) INTO retained_time FROM pg_temp.upgrade_known_times WHERE full_xid=item.full_xid;
  native_time=NULL;
  BEGIN native_time=security.checked_commit_time(item.full_xid);
  EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'knowledge_retention' THEN RAISE;END IF;END;
  IF native_time IS NOT NULL AND retained_time IS NOT NULL AND native_time<>retained_time
  THEN RAISE EXCEPTION 'postgresql_upgrade_time_conflict' USING ERRCODE='P0001';END IF;
  IF coalesce(native_time,retained_time) IS NULL THEN RAISE EXCEPTION 'knowledge_retention' USING ERRCODE='P0001';END IF;
  IF NOT isfinite(coalesce(native_time,retained_time)) OR coalesce(native_time,retained_time)>proof.sealed_at
  THEN RAISE EXCEPTION 'knowledge_clock' USING ERRCODE='P0001';END IF;
  INSERT INTO pg_temp.upgrade_exact_times VALUES(item.full_xid,coalesce(native_time,retained_time));
 END LOOP;
 SELECT count(*),encode(sha256(convert_to(coalesce(string_agg(full_xid::text||':'||encode(timestamptz_send(committed_at),'hex'),E'\n' ORDER BY full_xid),''),'UTF8')),'hex')
 INTO proof.times_count,proof.times_digest FROM pg_temp.upgrade_exact_times;
 INSERT INTO security.postgresql_upgrade_source SELECT(proof).*;
 INSERT INTO security.postgresql_upgrade_times SELECT proof.id,t.* FROM pg_temp.upgrade_exact_times t;
 RETURN proof;
END$$;
REVOKE ALL ON FUNCTION security.prepare_postgresql_upgrade(uuid,text,text) FROM PUBLIC;

CREATE FUNCTION security.admit_postgresql_upgrade(request_id uuid,expected_data_digest text,actual_schema_digest text) RETURNS security.postgresql_upgrade_admission
LANGUAGE plpgsql SET search_path=pg_catalog SET timezone='UTC' AS $$
DECLARE proof security.postgresql_upgrade_source;admission security.postgresql_upgrade_admission;digest text;counted bigint;
BEGIN
 IF NOT(SELECT rolsuper FROM pg_roles WHERE rolname=session_user)
 THEN RAISE EXCEPTION 'postgresql_upgrade_denied' USING ERRCODE='42501';END IF;
 IF current_setting('transaction_isolation')<>'read committed'
 THEN RAISE EXCEPTION 'postgresql_upgrade_snapshot' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO proof FROM security.postgresql_upgrade_source WHERE id=request_id;
 IF NOT FOUND OR expected_data_digest IS DISTINCT FROM proof.data_digest
 OR actual_schema_digest IS DISTINCT FROM proof.schema_digest
 OR current_setting('server_version_num')::integer NOT BETWEEN 190000 AND 199999
 OR current_setting('track_commit_timestamp')<>'on' OR pg_is_in_recovery()
 OR proof.source_system_id=(pg_control_system()).system_identifier::text
 THEN RAISE EXCEPTION 'postgresql_upgrade_proof' USING ERRCODE='P0001';END IF;
 SELECT * INTO admission FROM security.postgresql_upgrade_admission;
 IF FOUND THEN
  IF admission.source_id IS DISTINCT FROM proof.id OR(admission.target_system_id,admission.target_timeline) IS DISTINCT FROM((pg_control_system()).system_identifier::text,(pg_control_checkpoint()).timeline_id)
  THEN RAISE EXCEPTION 'postgresql_upgrade_proof' USING ERRCODE='P0001';END IF;
  RETURN admission;
 END IF;
 IF pg_snapshot_xmax(pg_current_snapshot())<=proof.seal_xid OR pg_xact_status(proof.seal_xid) IS DISTINCT FROM 'committed'
 THEN RAISE EXCEPTION 'postgresql_upgrade_proof' USING ERRCODE='P0001';END IF;
 IF security.postgresql_upgrade_data_digest()<>proof.data_digest
 THEN RAISE EXCEPTION 'postgresql_upgrade_proof' USING ERRCODE='P0001';END IF;
 SELECT count(*),encode(sha256(convert_to(coalesce(string_agg(full_xid::text||':'||encode(timestamptz_send(committed_at),'hex'),E'\n' ORDER BY full_xid),''),'UTF8')),'hex')
 INTO counted,digest FROM security.postgresql_upgrade_times WHERE source_id=proof.id;
 IF(counted,digest) IS DISTINCT FROM(proof.times_count,proof.times_digest)
 OR EXISTS(SELECT 1 FROM security.postgresql_upgrade_times WHERE source_id=proof.id AND(full_xid>=proof.seal_xid OR committed_at>proof.sealed_at))
 THEN RAISE EXCEPTION 'postgresql_upgrade_proof' USING ERRCODE='P0001';END IF;
 INSERT INTO security.postgresql_upgrade_admission VALUES(true,proof.id,(pg_control_system()).system_identifier::text,
  (pg_control_checkpoint()).timeline_id,current_setting('server_version_num')::integer,clock_timestamp()) RETURNING * INTO admission;
 RETURN admission;
END$$;
REVOKE ALL ON FUNCTION security.admit_postgresql_upgrade(uuid,text,text) FROM PUBLIC;

-- Preserve every authorization, epoch, coverage and prefix check. Only replace
-- the physical equality with the explicit native-or-admitted lineage proof.
DO $upgrade$DECLARE item record;helper pg_proc;definition text;old_fragment text;new_fragment text;BEGIN
 FOR item IN SELECT * FROM(VALUES
 ('security.coordinates_from_inputs(uuid,date,timestamptz,text,integer)','0c2a6c9c1f714ddd32bed28047a294d5'),
 ('security.workspace_cut_visible(uuid,uuid,date,timestamptz,integer,text,integer)','263c3a649a1a1f356ecd8f070e9c73c4'),
 ('security.certify_knowledge(uuid)','90ecece26f6733d0a907630ed818b0ac'),
 ('security.certify_market_publications(uuid)','31b326c0324be9db613d9d57c038d570'),
 ('power_data.certify(uuid)','baa96cfdf0ed9216e38692b102c99da0')) v(signature,body_hash) LOOP
  SELECT * INTO STRICT helper FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(helper.prosrc)<>item.body_hash OR helper.proowner<>'openetrm_security_owner'::regrole
   OR NOT helper.prosecdef OR helper.provolatile<>'v' OR helper.proisstrict OR helper.proleakproof OR helper.proparallel<>'u'
   OR helper.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR(SELECT array_agg(x::text ORDER BY x::text) FROM unnest(coalesce(helper.proacl,acldefault('f',helper.proowner))) x)
    IS DISTINCT FROM(CASE WHEN item.signature='security.workspace_cut_visible(uuid,uuid,date,timestamptz,integer,text,integer)'
    THEN ARRAY['openetrm_risk_worker=X/openetrm_security_owner','openetrm_runtime=X/openetrm_security_owner','openetrm_security_owner=X/openetrm_security_owner']
    ELSE ARRAY['openetrm_security_owner=X/openetrm_security_owner'] END)
  THEN RAISE EXCEPTION 'postgresql_upgrade_helper: %',item.signature;END IF;
  definition=pg_get_functiondef(helper.oid);
  CASE item.signature
  WHEN 'security.coordinates_from_inputs(uuid,date,timestamptz,text,integer)' THEN
   old_fragment=E'OR system_id IS DISTINCT FROM (pg_control_system()).system_identifier::text\n OR timeline IS DISTINCT FROM (pg_control_checkpoint()).timeline_id';
   new_fragment='OR security.knowledge_source_current(system_id,timeline) IS NOT TRUE';
  WHEN 'security.workspace_cut_visible(uuid,uuid,date,timestamptz,integer,text,integer)' THEN
   old_fragment='AND (pg_control_system()).system_identifier::text=$6 AND (pg_control_checkpoint()).timeline_id=$7';
   new_fragment='AND security.knowledge_source_current($6,$7)';
  WHEN 'security.certify_knowledge(uuid)' THEN
   old_fragment='pg_is_in_recovery() OR schedule_clock.system_identifier IS DISTINCT FROM (pg_control_system()).system_identifier::text OR schedule_clock.timeline_id IS DISTINCT FROM (pg_control_checkpoint()).timeline_id';
   new_fragment='security.knowledge_source_current(schedule_clock.system_identifier,schedule_clock.timeline_id) IS NOT TRUE';
  WHEN 'security.certify_market_publications(uuid)' THEN
   old_fragment=E'pg_is_in_recovery() OR clock.system_identifier IS DISTINCT FROM(pg_control_system()).system_identifier::text OR\n  clock.timeline_id IS DISTINCT FROM(pg_control_checkpoint()).timeline_id';
   new_fragment='security.knowledge_source_current(clock.system_identifier,clock.timeline_id) IS NOT TRUE';
  WHEN 'power_data.certify(uuid)' THEN
   old_fragment='pg_is_in_recovery() OR clock.system_identifier IS DISTINCT FROM (pg_control_system()).system_identifier::text OR clock.timeline_id IS DISTINCT FROM(pg_control_checkpoint()).timeline_id';
   new_fragment='security.knowledge_source_current(clock.system_identifier,clock.timeline_id) IS NOT TRUE';
  END CASE;
  IF(length(definition)-length(replace(definition,old_fragment,'')))/length(old_fragment)<>1
  THEN RAISE EXCEPTION 'postgresql_upgrade_helper: missing unique fragment %',item.signature;END IF;
  EXECUTE replace(definition,old_fragment,new_fragment);
 END LOOP;
END$upgrade$;
COMMIT;
