-- Working custom-curve calendars resolve one publication cutoff, then borrow a
-- different effective day under that same cutoff for each close they inspect.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);

-- Refuse drift in any definer we amend, including its declared search path,
-- signature and execute grants. Temporary migration helper only.
CREATE FUNCTION pg_temp.checked_read_function(signature regprocedure,expected text) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE p pg_proc;definition text;
BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid=signature;
 definition=pg_get_functiondef(p.oid);
 IF encode(sha256(convert_to(definition||p.proowner::regrole::text||coalesce(p.proacl::text,''),'UTF8')),'hex')<>expected THEN
  RAISE EXCEPTION 'ephemeral_context_upgrade: unexpected helper %',signature;
 END IF;
 RETURN definition;
END$$;

-- The internal chain certifies every publication family before closing the
-- shared watermark. A null effective day requests certification alone. Only
-- the four receipt producers change. The externally callable capture still
-- requires a day and retains its original worker restrictions.
DO $split$
DECLARE definition text;
BEGIN
 definition=pg_temp.checked_read_function('security.capture_schedule_context(uuid,date,timestamp with time zone)','95e743fb850fb90ba411aa2de62a9cabaeb5b94e3fd511837393eb1fc613e3aa');
 EXECUTE replace(definition,$old$ SELECT coalesce(max(publication_order),0) INTO head$old$,$new$ IF effective_date IS NULL THEN RETURN NULL;END IF;
 SELECT coalesce(max(publication_order),0) INTO head$new$);
 definition=pg_temp.checked_read_function('security.capture_market_and_schedule_context(uuid,date,timestamp with time zone)','7da83c787b0cf9fe313813c918f7de151978bd7e9926baba2d99700dda206255');
 EXECUTE replace(definition,$old$ SELECT coalesce(max(event_order),0) INTO head$old$,$new$ IF effective_date IS NULL THEN RETURN NULL;END IF;
 SELECT coalesce(max(event_order),0) INTO head$new$);
 definition=pg_temp.checked_read_function('security.capture_noncollection_date_context(uuid,date,timestamp with time zone)','11470d2fe63da905abff5b78255f5505d71e9166988c2bc32844a699ee3548ea');
 EXECUTE replace(definition,$old$ SELECT coalesce(max(publication_order),0) INTO head$old$,$new$ IF effective_date IS NULL THEN RETURN NULL;END IF;
 SELECT coalesce(max(publication_order),0) INTO head$new$);
 definition=pg_temp.checked_read_function('security.capture_date_context(uuid,date,timestamp with time zone)','c9b5c7d16462390cdcf14055a94bad272eed1dda60c4595f53ad8c6bb29537de');
 EXECUTE replace(definition,$old$ SELECT coalesce(max(k.publication_order),0) INTO prefix$old$,$new$ IF effective_date IS NULL THEN RETURN NULL;END IF;
 SELECT coalesce(max(k.publication_order),0) INTO prefix$new$);
END$split$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) RENAME TO certify_or_capture_date_context;
REVOKE ALL ON FUNCTION security.certify_or_capture_date_context(uuid,date,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.capture_date_context(catalog uuid,effective_date date,requested_at timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF effective_date IS NULL THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 RETURN security.certify_or_capture_date_context(catalog,effective_date,requested_at);
END$$;
ALTER FUNCTION security.capture_date_context(uuid,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.capture_date_context(uuid,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.capture_date_context(uuid,date,timestamptz) TO openetrm_runtime,openetrm_risk_worker;

-- Automation and leased worker identities use their own retained input
-- fences. An ordinary calendar may only bind a live interactive/agent read.
CREATE FUNCTION security.require_calendar_reader(catalog uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 OR NOT EXISTS(SELECT 1 FROM security.request_contexts q
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
   AND q.market_context_id IS NULL AND q.expires_at>clock_timestamp()
   AND NOT EXISTS(SELECT 1 FROM automation.pilot_run_contexts p WHERE p.context_id=q.id)
   AND NOT EXISTS(SELECT 1 FROM risk.worker_attempt_contexts w WHERE w.context_id=q.id))
 THEN RAISE EXCEPTION 'calendar_denied' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.require_calendar_reader(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_calendar_reader(uuid) FROM PUBLIC;

-- A server-owned value can outlive its execution transaction. On reuse, prove
-- the cutoff again against the original timeline before binding its inputs.
CREATE FUNCTION security.calendar_read_cut(catalog uuid)
RETURNS TABLE(known_at timestamptz,known_through integer,system_id text,timeline integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_calendar_reader(catalog);
 PERFORM security.certify_or_capture_date_context(catalog,NULL,NULL);
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'calendar_denied' USING ERRCODE='42501';END IF;
 RETURN QUERY SELECT k.certified_through,k.certified_head,k.system_identifier,k.timeline_id
 FROM market_data.knowledge_clocks k WHERE k.catalog_id=catalog AND k.certified_through IS NOT NULL;
END$$;

-- Scope data belongs to the existing authenticated transaction binding.
-- Its original FK is still enforced at commit. A transient binding must be
-- rolled back to its savepoint before this transaction can commit.
DO $binding$
DECLARE fk pg_constraint;
BEGIN
 SELECT * INTO STRICT fk FROM pg_constraint WHERE conrelid='security.request_contexts'::regclass
  AND conname='request_contexts_market_catalog_id_market_context_id_fkey';
 IF NOT fk.convalidated OR NOT fk.conenforced OR fk.condeferrable OR fk.condeferred OR
    pg_get_constraintdef(fk.oid)<>'FOREIGN KEY (market_catalog_id, market_context_id) REFERENCES market_data.market_contexts(catalog_id, context_id)' OR
    EXISTS(SELECT 1 FROM pg_trigger WHERE tgconstraint=fk.oid AND tgenabled NOT IN ('O','A'))
 THEN RAISE EXCEPTION 'ephemeral_context_upgrade: unexpected request context foreign key';END IF;
END$binding$;
CREATE TYPE security.calendar_read_coordinates AS (
 effective_on date,known_at timestamptz,schedule_prefix integer,coverage_from timestamptz,
 market_prefix integer,catalogue_prefix integer
);
ALTER TABLE security.request_contexts
 ALTER CONSTRAINT request_contexts_market_catalog_id_market_context_id_fkey DEFERRABLE INITIALLY IMMEDIATE,
 ADD COLUMN calendar_cut security.calendar_read_coordinates;

CREATE FUNCTION security.bind_calendar_read(catalog uuid,context_id uuid,day date,cutoff timestamptz,
 schedule_prefix integer,system_id text,timeline integer) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE clock market_data.knowledge_clocks;market_head integer;catalogue_head integer;
BEGIN
 PERFORM security.require_calendar_reader(catalog);
 SELECT * INTO STRICT clock FROM market_data.knowledge_clocks k WHERE k.catalog_id=catalog;
 IF current_setting('track_commit_timestamp')<>'on' OR pg_is_in_recovery()
 OR clock.system_identifier IS DISTINCT FROM system_id OR clock.timeline_id IS DISTINCT FROM timeline
 OR (pg_control_system()).system_identifier::text IS DISTINCT FROM system_id OR (pg_control_checkpoint()).timeline_id IS DISTINCT FROM timeline
 THEN RAISE EXCEPTION 'knowledge_timeline' USING ERRCODE='P0001';END IF;
 IF context_id IS NULL OR day IS NULL OR day<DATE '0001-01-01' OR day>=DATE '9999-12-31'
 OR cutoff IS NULL OR clock.coverage_from IS NULL OR cutoff NOT BETWEEN clock.coverage_from AND clock.certified_through
 OR EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE (t.catalog_id,t.id)=(catalog,context_id))
 OR schedule_prefix IS DISTINCT FROM (SELECT coalesce(max(p.publication_order),0) FROM market_data.knowledge_publications p
     WHERE p.catalog_id=catalog AND p.known_from<=cutoff)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 SELECT coalesce(max(k.event_order),0) INTO market_head FROM market_data.market_knowledge_receipts k WHERE k.catalog_id=catalog AND k.known_from<=cutoff;
 SELECT coalesce(max(k.publication_order),0) INTO catalogue_head FROM market_data.catalogue_knowledge_receipts k WHERE k.catalog_id=catalog AND k.known_from<=cutoff;
 SET CONSTRAINTS security.request_contexts_market_catalog_id_market_context_id_fkey DEFERRED;
 UPDATE security.request_contexts q SET market_catalog_id=catalog,market_context_id=context_id,
 calendar_cut=ROW(day,cutoff,schedule_prefix,clock.coverage_from,market_head,catalogue_head)::security.calendar_read_coordinates
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
 AND q.runtime_login=session_user AND q.market_context_id IS NULL AND q.expires_at>clock_timestamp();
 IF NOT FOUND OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'calendar_denied' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.calendar_read_cut(uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.bind_calendar_read(uuid,uuid,date,timestamptz,integer,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.calendar_read_cut(uuid),security.bind_calendar_read(uuid,uuid,date,timestamptz,integer,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.calendar_read_cut(uuid),security.bind_calendar_read(uuid,uuid,date,timestamptz,integer,text,integer) TO openetrm_runtime;

-- Private read projections. The durable arm retains exactly the caller's old
-- visibility. The transient arm contains at most the calling transaction's row.
CREATE VIEW security.bound_calendar_context WITH (security_invoker=true) AS
 SELECT q.market_catalog_id AS catalog_id,q.market_context_id AS id,(q.calendar_cut).*
 FROM security.request_contexts q
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
 AND q.expires_at>statement_timestamp() AND q.calendar_cut IS NOT NULL;
CREATE VIEW security.read_temporal_contexts WITH (security_invoker=true) AS
 SELECT catalog_id,id,effective_on,known_at,known_through,coverage_from,certified_through FROM market_data.temporal_contexts
 UNION ALL SELECT catalog_id,id,effective_on,known_at,schedule_prefix,coverage_from,known_at FROM security.bound_calendar_context;
CREATE VIEW security.read_market_contexts WITH (security_invoker=true) AS
 SELECT catalog_id,context_id,known_through FROM market_data.market_contexts
 UNION ALL SELECT catalog_id,id,market_prefix FROM security.bound_calendar_context;
CREATE VIEW security.read_catalogue_contexts WITH (security_invoker=true) AS
 SELECT catalog_id,context_id,known_through FROM market_data.catalogue_contexts
 UNION ALL SELECT catalog_id,id,catalogue_prefix FROM security.bound_calendar_context;
GRANT SELECT ON security.bound_calendar_context,security.read_temporal_contexts,security.read_market_contexts,security.read_catalogue_contexts TO openetrm_security_owner;

-- The calendar's financial readers use the same predicates for both bindings.
-- Only their read relations change. Durable commands and their FKs always
-- refer to the original tables. Check the exact released helpers first.
DO $readers$
DECLARE helper record;definition text;
BEGIN
 FOR helper IN SELECT * FROM (VALUES
  ('market_data.frozen_observation_knowledge_pre_portfolio_worker(uuid,uuid,uuid[],uuid,timestamp with time zone)','b75222b0086f41b6994733dd7bf653be46ddd57a785d7926223c51ffd777cc19'),
  ('market_data.open_interval_series_read(market_data.interval_series,timestamp with time zone,timestamp with time zone,timestamp with time zone)','92f0948f199a203b484b113ee6c3b5f2ffdee1ee73fcaa554d5bb6e72502caef'),
  ('market_data.price_publication_event_visible(uuid,integer)','5d361921cfc82ad41c6d4d39fb6567286babcdf7c3d2e58941e54d87f3bed3a6'),
  ('market_data.read_price_publications(uuid)','4bd2e9d434ea107638e316ad8faff0715644d1aa5bb6b6a6bb9eb8c87923ad5d'),
  ('power_data.read_prices_pre_portfolio_worker(uuid,timestamp with time zone,timestamp with time zone,timestamp with time zone,timestamp with time zone)','3a22835130a7319fd0b978a4734910e4a56028024ba62dbad12310c563aa8de1'),
  ('security.close_in_force(uuid,text,date)','c886a337d09a53cd640d43250441bf34f93b0ae757e125b11255379c2da0b4b6'),
  ('security.contract_date_admission_visible(uuid,uuid)','2d4fc34204b7647650f79dad1558a537e0cf40b13d3a6c1365a570dcfeffee00'),
  ('security.contract_date_schedule_visible(uuid,uuid,integer)','06f1d8495d438d627a3d60bbf03249c5a6f71565a58368f644c4d66a28844aea'),
  ('security.contract_date_visible(uuid,uuid,integer)','ee9a12efe0c70d757e9a8ff69a05a01ec4df03482d34ade4b87a69ad85e50fc4'),
  ('security.curve_definition_visible(uuid,uuid,integer)','c4f98b9f6a8f58ad088be5e045b8c74a3ea3c10fcb8284926d2202f037c78454'),
  ('security.custom_curve_visible(uuid,uuid,integer)','87279c71eceebd419c0318a0be0ac6f656d39d6e4ffb01b52ceded2fb8b667a6'),
  ('security.market_close_batch_visible(uuid,uuid[])','e5d77ed9de3a474b897bbe356577d22378917ffe6499f270dfa772edf7107a20'),
  ('security.market_close_visible(uuid,uuid)','cd4556061667a69ff7cfa614be965d92a33e33ab23dbc92828b4fe706c55825d'),
  ('security.market_fact_visible(uuid,uuid,uuid)','3749f9b1d8ee7d59813230e6bca269b2bae843d1b5aece22a4717cd643b81807'),
  ('security.price_publication_visible(uuid,uuid,integer)','07ac76754c0602c3618b2b995aa0830d48d9669a30c947b435602f816de8503e'),
  ('security.read_catalogue(uuid,uuid)','d2739de89a61d8889d8ff6ee78d753a9152a54be6ca8e990815c5cf474203d5f'),
  ('security.read_custom_curve_activation(uuid,uuid,integer)','e1b4d2e422883d6e0e8b6e90642aff22f2957256ca2684810e742bffa314dcbd'),
  ('security.read_power_locations(uuid,uuid,uuid,jsonb)','b4987c5d057ea56b29b7f4e4e8dfc0d1f2890edeab95e8cac13d287c0812d6aa'),
  ('security.read_request_catalogue(uuid,date)','710775e252652fd90abcd92b4cacddbd85845ce988dcc9d438a2861c76bf62a7'),
  ('security.reference_version_visible(uuid,uuid)','c1541a37bb056ca2a5534714778c0f5705c7d33321e4952f2f127ff41241d355'),
  ('security.request_market_cut()','10515d47fbd3e2d4892a36a0a719ac07662cbb7f784f14ba24ffd66ff40de8d6'),
  ('security.side_calendar_cutoff(uuid,uuid)','ebd189398891c01d79e694def6a36149dd5f281c979ee890b880babb422f56be')
 ) AS h(signature,digest) LOOP
  definition=pg_temp.checked_read_function(helper.signature::regprocedure,helper.digest);
  definition=regexp_replace(definition,'\m(FROM|JOIN) market_data\.(temporal_contexts|market_contexts|catalogue_contexts)\M','\1 security.read_\2','gi');
  IF helper.signature='security.market_close_batch_visible(uuid,uuid[])' THEN
   -- Keep the scalar fallback when the reviewed close predicate is changed again.
   definition=replace(definition,'9b6cc5c36494a42edef9d616fc4709544f1f427ba281eb547f854443cde1115d','6fbdedd7d9613d695872ddd7c19eaecc704c331a0eecf5c33b0640c133b4b382');
  END IF;
  EXECUTE definition;
 END LOOP;
END$readers$;
DROP FUNCTION pg_temp.checked_read_function(regprocedure,text);
COMMIT;
