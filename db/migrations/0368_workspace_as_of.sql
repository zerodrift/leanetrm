-- A workspace owns its selected coordinates. Business records can still retain
-- those coordinates with the exact identity used by the displayed calculation.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION security.workspace_context_id(catalog uuid,day date,at_time timestamptz) RETURNS uuid
LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE SET search_path=pg_catalog AS $$
 SELECT substr(encode(sha256(uuid_send($1)||date_send($2)||timestamptz_send($3)),'hex'),1,32)::uuid
$$;
REVOKE ALL ON FUNCTION security.workspace_context_id(uuid,date,timestamptz) FROM PUBLIC;
ALTER FUNCTION security.workspace_context_id(uuid,date,timestamptz) OWNER TO openetrm_security_owner;

-- Both an ordinary capture and later durable admission choose the same new
-- identity, even if two users first inspect the identical coordinates. The
-- existing lookup continues to return every previously assigned receipt ID.
DO $split$
DECLARE fn pg_proc; source text; tail text;
BEGIN
 SELECT * INTO STRICT fn FROM pg_proc WHERE oid='security.capture_schedule_context(uuid,date,timestamptz)'::regprocedure;
 source=pg_get_functiondef(fn.oid);
 IF encode(sha256(convert_to(source||fn.proowner::regrole::text||coalesce(fn.proacl::text,''),'UTF8')),'hex')
 <> '0f5174f3a860bbfc3930fb7c02cd0f8ce34d3ea6d4ea6f97866105095354673b' THEN RAISE EXCEPTION 'workspace_context_upgrade: receipt writer';END IF;
 -- Share the released idempotence, capacity and authority checks. A save of a
 -- certified value stores its original watermark without publishing new history.
 tail=split_part(split_part(source,' SELECT id INTO token FROM market_data.temporal_contexts',2),'END $function$',1);
 EXECUTE 'CREATE FUNCTION security.store_schedule_cut(catalog uuid,effective_date date,cutoff timestamptz,head integer,covered timestamptz,captured_at timestamptz)
 RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $receipt$
 DECLARE token uuid;BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT id INTO token FROM market_data.temporal_contexts'||replace(replace(tail,'token=gen_random_uuid();',
 'token=security.workspace_context_id(catalog,effective_date,cutoff);'),'clock.coverage_from','covered')||'END $receipt$';
 EXECUTE replace(source,' SELECT id INTO token FROM market_data.temporal_contexts'||tail,
 ' RETURN security.store_schedule_cut(catalog,effective_date,cutoff,head,clock.coverage_from,captured_at);'||chr(10));
END$split$;
ALTER FUNCTION security.store_schedule_cut(uuid,date,timestamptz,integer,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.store_schedule_cut(uuid,date,timestamptz,integer,timestamptz,timestamptz) FROM PUBLIC;

CREATE FUNCTION security.workspace_read_cut(catalog uuid,day date,requested_at timestamptz,day_end boolean)
RETURNS TABLE(id uuid,known_at timestamptz,known_through integer,coverage_from timestamptz,certified_through timestamptz,system_id text,timeline integer,stored boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; started timestamptz; selected timestamptz;
BEGIN
 IF day IS NULL OR day<DATE '0001-01-01' OR day>=DATE '9999-12-31' OR day_end IS NULL OR (day_end AND requested_at IS NULL)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 SELECT * INTO STRICT c FROM security.calendar_read_cut(catalog);
 SELECT k.coverage_from INTO STRICT started FROM market_data.knowledge_clocks k WHERE k.catalog_id=catalog;
 selected=CASE WHEN day_end THEN least(requested_at,c.known_at) ELSE coalesce(requested_at,c.known_at) END;
 IF selected NOT BETWEEN started AND c.known_at THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 RETURN QUERY SELECT coalesce(t.id,security.workspace_context_id(catalog,day,selected)),selected,
 (SELECT coalesce(max(k.publication_order),0) FROM market_data.knowledge_publications k WHERE k.catalog_id=catalog AND k.known_from<=selected),
 started,c.known_at,c.system_id,c.timeline,t.id IS NOT NULL
 FROM (VALUES(1)) seed(x) LEFT JOIN market_data.temporal_contexts t ON(t.catalog_id,t.effective_on,t.known_at)=(catalog,day,selected);
END$$;
ALTER FUNCTION security.workspace_read_cut(uuid,date,timestamptz,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.workspace_read_cut(uuid,date,timestamptz,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.workspace_read_cut(uuid,date,timestamptz,boolean) TO openetrm_runtime;

ALTER TABLE security.request_contexts ALTER CONSTRAINT request_business_source_trades DEFERRABLE INITIALLY IMMEDIATE,
 ALTER CONSTRAINT request_business_source_market DEFERRABLE INITIALLY IMMEDIATE;
CREATE FUNCTION security.workspace_cut_visible(catalog uuid,context_id uuid,day date,cutoff timestamptz,
 prefix integer,system_id text,timeline integer) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.market_access($1,'ReadMarketData') IS TRUE AND current_setting('track_commit_timestamp')='on' AND NOT pg_is_in_recovery()
 AND (pg_control_system()).system_identifier::text=$6 AND (pg_control_checkpoint()).timeline_id=$7
 AND EXISTS(SELECT 1 FROM market_data.knowledge_clocks k WHERE k.catalog_id=$1 AND k.system_identifier=$6 AND k.timeline_id=$7
 AND $4 BETWEEN k.coverage_from AND k.certified_through)
 AND $2=security.workspace_context_id($1,$3,$4) AND $5=(SELECT coalesce(max(k.publication_order),0)
 FROM market_data.knowledge_publications k WHERE k.catalog_id=$1 AND k.known_from<=$4)
$$;
ALTER FUNCTION security.workspace_cut_visible(uuid,uuid,date,timestamptz,integer,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.workspace_cut_visible(uuid,uuid,date,timestamptz,integer,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.workspace_cut_visible(uuid,uuid,date,timestamptz,integer,text,integer) TO openetrm_runtime;

ALTER FUNCTION security.bind_calendar_read(uuid,uuid,date,timestamptz,integer,text,integer) RENAME TO bind_calendar_coordinates;
REVOKE ALL ON FUNCTION security.bind_calendar_coordinates(uuid,uuid,date,timestamptz,integer,text,integer) FROM openetrm_runtime;

CREATE FUNCTION security.bind_workspace_read(catalog uuid,context_id uuid,day date,cutoff timestamptz,
 prefix integer,system_id text,timeline integer) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.workspace_cut_visible(catalog,context_id,day,cutoff,prefix,system_id,timeline) IS NOT TRUE THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 -- A nested operation may already own this exact authorized binding.
 IF EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
  AND q.runtime_login=session_user AND q.expires_at>clock_timestamp() AND (q.market_catalog_id,q.market_context_id)=(catalog,context_id)
  AND EXISTS(SELECT 1 FROM security.read_temporal_contexts t WHERE (t.catalog_id,t.id,t.effective_on,t.known_at,t.known_through)=(catalog,context_id,day,cutoff,prefix))) THEN RETURN;END IF;
 BEGIN
  PERFORM security.bind_calendar_coordinates(catalog,context_id,day,cutoff,prefix,system_id,timeline);
  SET CONSTRAINTS security.request_business_source_trades,security.request_business_source_market DEFERRED;
 EXCEPTION WHEN SQLSTATE 'P0001' THEN
  IF SQLERRM<>'knowledge_coverage' OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t
    WHERE (t.catalog_id,t.id,t.effective_on,t.known_at,t.known_through)=(catalog,context_id,day,cutoff,prefix)) THEN RAISE;END IF;
  PERFORM security.require_calendar_reader(catalog);
  PERFORM security.use_market_context(catalog,context_id);
 END;
END$$;
ALTER FUNCTION security.bind_workspace_read(uuid,uuid,date,timestamptz,integer,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.bind_workspace_read(uuid,uuid,date,timestamptz,integer,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.bind_workspace_read(uuid,uuid,date,timestamptz,integer,text,integer) TO openetrm_runtime;

-- Calendar callers cannot label a different date or cutoff with a workspace ID.
CREATE FUNCTION security.bind_calendar_read(catalog uuid,context_id uuid,day date,cutoff timestamptz,
 schedule_prefix integer,system_id text,timeline integer) RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.bind_workspace_read($1,$2,$3,$4,$5,$6,$7)
$$;
ALTER FUNCTION security.bind_calendar_read(uuid,uuid,date,timestamptz,integer,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.bind_calendar_read(uuid,uuid,date,timestamptz,integer,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.bind_calendar_read(uuid,uuid,date,timestamptz,integer,text,integer) TO openetrm_runtime;

-- For a read spanning a complete authenticated transaction, finish only after
-- every commit admission. Expire its single-use authorization as we unbind.
CREATE FUNCTION security.finish_workspace_read() RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 UPDATE security.request_contexts q SET calendar_cut=NULL
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.calendar_cut IS NOT NULL
 AND EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE (t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id));
 UPDATE security.request_contexts SET market_catalog_id=NULL,market_context_id=NULL,calendar_cut=NULL,
 trading_as_of=NULL,trading_business_date=NULL,business_source_catalog_id=NULL,business_source_context_id=NULL,
 business_source_as_of=NULL,business_source_on=NULL,business_source_until=NULL,expires_at=least(expires_at,clock_timestamp())
 WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND calendar_cut IS NOT NULL
$$;
ALTER FUNCTION security.finish_workspace_read() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.finish_workspace_read() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.finish_workspace_read() TO openetrm_runtime;

CREATE OR REPLACE VIEW security.bound_calendar_context WITH (security_invoker=true) AS
 SELECT q.market_catalog_id AS catalog_id,q.market_context_id AS id,(q.calendar_cut).*
 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
 AND q.runtime_login=session_user AND q.expires_at>statement_timestamp() AND q.calendar_cut IS NOT NULL
 AND NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE (t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id));

-- Only the already-bound read may disclose its transient market prefix. The
-- domain queries keep their original RLS path for stored receipts and workers.
CREATE FUNCTION security.active_workspace_market(catalog uuid,identifier uuid)
RETURNS TABLE(known_through integer,known_at timestamptz) LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT b.market_prefix,b.known_at FROM security.bound_calendar_context b WHERE (b.catalog_id,b.id)=($1,$2)
 AND security.market_access($1,'ReadMarketData') IS TRUE
$$;
ALTER FUNCTION security.active_workspace_market(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.active_workspace_market(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.active_workspace_market(uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;

CREATE VIEW security.read_trading_contexts WITH (security_invoker=true) AS
 SELECT catalog_id,context_id,known_through FROM trading.contexts
 UNION ALL SELECT b.catalog_id,b.id,(SELECT coalesce(max(k.publication_order),0) FROM trading.knowledge_receipts k
 WHERE k.catalog_id=b.catalog_id AND k.known_from<=b.known_at) FROM security.bound_calendar_context b;
GRANT SELECT ON security.read_trading_contexts TO openetrm_security_owner;


-- Only relation reads in these reviewed predicates change. Retained row writes,
-- leases, exact foreign keys and immutable evidence continue to use base tables.
DO $readers$
DECLARE installed text; signature text; source text;
 targets text[]=ARRAY[
 'assistant.pin_market_turn(uuid,bytea,date,jsonb,integer)',
 'assistant.pin_turn(uuid,bytea,date,jsonb,integer)',
 'assistant.resolve_turn_context(uuid,bytea,date,jsonb)',
 'automation.close_risk_run_definition()',
 'automation.create_close_risk_job(jsonb,uuid,jsonb,bigint,bigint)',
 'automation.lifecycle_job_result(uuid)',
 'automation.read_pilot_report(uuid,uuid)',
 'lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb)',
 'lifecycle.option_proposal_base(uuid,jsonb)',
 'lifecycle.read_option_known_history_pre_risk(uuid,uuid,uuid,integer,uuid,timestamp with time zone,date)',
 'lifecycle.read_swap_payment_history_pre_risk(uuid,uuid,uuid,integer,uuid,timestamp with time zone,date)',
 'lifecycle.read_swap_profile(uuid,uuid,text,uuid,integer,uuid,timestamp with time zone,date)',
 'lifecycle.swap_net_profile_document(uuid,uuid,uuid,integer,uuid,timestamp with time zone,date)',
 'market_data.point_trader_mark_known(uuid,uuid,uuid,uuid)',
 'market_data.price_publication_event_visible(uuid,integer)',
 'market_data.read_price_publications(uuid)',
 'physical.power_operating_context(uuid,uuid,timestamp with time zone,date)',
 'physical.power_operating_visible(physical.power_operating_facility_versions,uuid)',
 'risk.bind(uuid,uuid)',
 'risk.capture_history_clock(uuid,uuid,jsonb,jsonb)',
 'risk.definition_run_history(uuid,uuid,uuid)',
 'risk.lifecycle_scope_policies(risk.jobs)',
 'risk.lifecycle_scope_trades(risk.jobs)',
 'risk.needs_gas_park_custody(risk.jobs,boolean)',
 'risk.needs_gas_pre_gas_park_custody(risk.jobs)',
 'risk.require_cash_accumulation(risk.jobs)',
 'risk.retain_statistical_history(risk.jobs,jsonb)',
 'risk.submit_vol_surface_fit(uuid,uuid,uuid,uuid,timestamp with time zone,jsonb,jsonb)',
 'security.admit_trading_business_source(uuid,uuid,timestamp with time zone,date)',
 'security.crude_position_scope(uuid,uuid[],uuid[],uuid[])',
 'security.crude_setup_visible(uuid,uuid)',
 'security.current_crude_setups(uuid)',
 'security.gas_operations_balance(uuid,uuid,date,uuid,uuid,timestamp with time zone,uuid)',
 'security.gas_operations_context(uuid,uuid,timestamp with time zone,integer,timestamp with time zone)',
 'security.gas_owned_review_hash(uuid,uuid,text,jsonb,timestamp with time zone,uuid,uuid,integer,boolean)',
 'security.gas_physical_journal_source(uuid,uuid,timestamp with time zone,uuid[],uuid[])',
 'security.gas_position_scope(uuid,uuid[],uuid[],uuid[])',
 'security.gas_setup_visible(uuid,uuid)',
 'security.gas_transport_financial_capacity_facts(uuid,uuid,timestamp with time zone,uuid)',
 'security.lifecycle_projection_sessions(uuid,uuid,timestamp with time zone,date)',
 'security.lng_position_scope(uuid,uuid[],uuid[],uuid[])',
 'security.operating_position_scope(uuid,uuid[],uuid[],uuid[])',
 'security.pilot_report_sources_visible(uuid,uuid)',
 'security.pilot_report_visible(uuid,uuid)',
 'security.point_trader_mark_setup(uuid,uuid,timestamp with time zone,uuid,uuid,uuid,jsonb)',
 'security.ppa_authoring_directory(uuid,uuid,timestamp with time zone)',
 'security.ppa_read_scope_pre_portfolio_worker(uuid,text,uuid[],uuid,timestamp with time zone,boolean)',
 'security.ppa_require_market_pre_portfolio_worker(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,uuid[])',
 'security.ppa_tou_versions_pre_portfolio_worker(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,jsonb)',
 'security.ppa_visible_at(uuid,integer,timestamp with time zone,uuid,timestamp with time zone)',
 'security.price_publication_visible(uuid,uuid,integer)',
 'security.read_crude_lifecycle_original(uuid,uuid)',
 'security.read_gas_owned_holdings_original(uuid,uuid,timestamp with time zone,uuid[])',
 'security.read_gas_owned_known_history_pre_risk(uuid,uuid,timestamp with time zone,uuid[])',
 'security.read_gas_owned_operations(uuid,uuid,timestamp with time zone,uuid)',
 'security.read_gas_owned_profile_holdings(uuid,uuid,timestamp with time zone,uuid[])',
 'security.read_gas_owned_projection_choices(uuid,uuid,timestamp with time zone,uuid[])',
 'security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamp with time zone)',
 'security.read_gas_park_custody(uuid,uuid,uuid,timestamp with time zone)',
 'security.read_gas_park_loan_principal_v1(uuid,uuid,uuid,timestamp with time zone)',
 'security.read_gas_physical_known_attachments(uuid,uuid,timestamp with time zone,uuid[],jsonb)',
 'security.read_gas_quantity_rows(uuid,uuid,text)',
 'security.read_gas_transport_capacity(uuid,uuid)',
 'security.read_gas_transport_financial(uuid,uuid,uuid,timestamp with time zone)',
 'security.read_gas_transport_route(uuid,uuid)',
 'security.read_lifecycle_projection_final_pre_final_scope(uuid,uuid,text,text,uuid,timestamp with time zone,uuid,timestamp with time zone,date)',
 'security.read_lifecycle_projection_horizon_pre_risk(uuid,text,integer,uuid,timestamp with time zone,date)',
 'security.read_lifecycle_projection_profiles(uuid,text,uuid,integer,uuid,timestamp with time zone,date,timestamp with time zone)',
 'security.read_rec_inventory(uuid,uuid,uuid,timestamp with time zone)',
 'security.read_storage_facility(uuid,uuid)',
 'security.read_vol_surface(uuid,uuid,jsonb,date,text,uuid,timestamp with time zone,uuid[])',
 'security.rec_forward_position_scope(uuid,uuid[],uuid[],uuid[])',
 'security.record_crude_lifecycle(uuid,text,jsonb,timestamp with time zone,uuid)',
 'security.record_delivery_route_allocation(uuid,uuid,jsonb,timestamp with time zone,uuid)',
 'security.record_gas_balance_fee(uuid,uuid,uuid,jsonb,timestamp with time zone,uuid,timestamp with time zone)',
 'security.record_gas_billing(uuid,jsonb,timestamp with time zone,uuid,jsonb)',
 'security.record_gas_daily_quantity(uuid,jsonb,timestamp with time zone,uuid)',
 'security.record_gas_operations(uuid,jsonb,timestamp with time zone,uuid,jsonb)',
 'security.record_gas_owned(uuid,uuid,text,uuid,jsonb,timestamp with time zone,uuid,uuid,integer,boolean)',
 'security.record_gas_park_custody(uuid,uuid,uuid,text,jsonb,timestamp with time zone,uuid,uuid,integer,boolean)',
 'security.record_gas_park_custody_evidence(uuid,uuid,uuid,jsonb,timestamp with time zone,uuid)',
 'security.record_gas_park_loan(uuid,uuid,uuid,text,jsonb,timestamp with time zone,uuid,boolean)',
 'security.require_admitted_business_source(uuid,uuid,timestamp with time zone,date,timestamp with time zone)',
 'security.require_crude_event_context(uuid,uuid,uuid,timestamp with time zone)',
 'security.require_crude_trade_context(uuid,uuid,integer,uuid,timestamp with time zone)',
 'security.require_frame_inputs(uuid,uuid,jsonb)',
 'security.require_gas_park_custody_read(uuid,uuid,timestamp with time zone,timestamp with time zone,boolean)',
 'security.require_gas_park_loan_presence(uuid,uuid,timestamp with time zone,uuid[],uuid[],timestamp with time zone)',
 'security.require_gas_transport_financial_presence(uuid,uuid,timestamp with time zone,uuid[],uuid[],timestamp with time zone)',
 'security.require_gas_transport_financial_read(uuid,uuid,timestamp with time zone,timestamp with time zone)',
 'security.require_lifecycle_projection_profile_pre_risk(uuid,text,uuid,uuid,integer,uuid,timestamp with time zone,date,timestamp with time zone)',
 'security.require_point_trader_mark_read(uuid,uuid,timestamp with time zone,uuid,uuid,uuid,timestamp with time zone)',
 'security.require_trading_history(uuid,uuid)',
 'security.resolve_crude_delivery(uuid,jsonb,timestamp with time zone,timestamp with time zone,timestamp with time zone)',
 'security.risk_ppa_trade_heads(uuid,uuid[],uuid[])',
 'security.trade_party_versions(uuid,text,timestamp with time zone)',
 'security.trading_context_day_matches(uuid,uuid,timestamp with time zone)',
 'security.trading_visible(uuid,integer,timestamp with time zone)',
 'security.use_trading_context(uuid,timestamp with time zone)',
 'security.use_trading_context(uuid,timestamp with time zone,date)',
 'security.visible_trading_publications()',
 'security.vol_surface_library(uuid,uuid,uuid,uuid,text,text,uuid,timestamp with time zone,uuid[],integer,integer)',
 'settlement_data.adopt_futures_margin(uuid,jsonb)',
 'settlement_data.apply_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamp with time zone,jsonb,numeric,uuid,uuid,text,timestamp with time zone,text,uuid,jsonb,text)',
 'settlement_data.determine_futures_margin_native(uuid,jsonb,jsonb,timestamp with time zone)',
 'settlement_data.determine_swap(uuid,uuid,uuid,integer,integer,uuid,timestamp with time zone,integer,date,date,timestamp with time zone,uuid,uuid,text,integer,timestamp with time zone,text,text,text,numeric,numeric,numeric,jsonb,jsonb,timestamp with time zone)',
 'settlement_data.futures_delivery_profile_document(uuid,uuid,integer,uuid,timestamp with time zone)',
 'settlement_data.futures_known_history_contents(uuid,uuid,uuid,uuid,timestamp with time zone,jsonb)',
 'settlement_data.futures_margin_policy_visible(uuid,integer)',
 'settlement_data.futures_margin_projection_source_rows(settlement_data.futures_margin_policies,uuid,timestamp with time zone,timestamp with time zone)',
 'settlement_data.publish_meter_finality(uuid,uuid,text,text,timestamp with time zone,timestamp with time zone,integer,text,timestamp with time zone,uuid,uuid,text)',
 'settlement_data.read_futures_delivery_final(uuid,uuid,uuid,timestamp with time zone)',
 'settlement_data.read_futures_delivery_obligation_pre_risk(uuid,uuid,uuid,uuid,timestamp with time zone)',
 'settlement_data.read_futures_delivery_principal(uuid,uuid,uuid,uuid,integer,uuid,timestamp with time zone)',
 'settlement_data.read_futures_delivery_settings(uuid,uuid,timestamp with time zone)',
 'settlement_data.read_futures_known_linked_history_pre_risk(uuid,uuid,uuid,uuid,timestamp with time zone,uuid)',
 'settlement_data.read_futures_margin_origin(uuid,uuid,uuid,uuid,timestamp with time zone)',
 'settlement_data.read_futures_margin_state_pre_risk(uuid,uuid,uuid,uuid,timestamp with time zone)',
 'settlement_data.read_meter_finality(uuid,uuid,text,text,timestamp with time zone,timestamp with time zone,uuid,timestamp with time zone)',
 'settlement_data.require_futures_delivery_head(uuid,uuid,uuid)',
 'settlement_data.require_futures_margin_delivery(settlement_data.futures_margin_policies)',
 'settlement_data.require_futures_margin_read_pre_risk(uuid,uuid,timestamp with time zone,timestamp with time zone)',
 'settlement_data.require_futures_margin_source(uuid,uuid,timestamp with time zone,uuid)',
 'settlement_data.save_futures_delivery_profile(uuid,jsonb)',
 'trading.gas_balance_fee_accrual(uuid,uuid,uuid,timestamp with time zone,timestamp with time zone)',
 'trading.gas_operator_reconciliation_rows(uuid,uuid,uuid,timestamp with time zone,boolean)',
 'trading.gas_owned_applied_observation_conflicts(uuid,uuid,timestamp with time zone,uuid[],uuid[])',
 'trading.gas_owned_cashout_price(uuid,uuid,uuid,timestamp with time zone,uuid)',
 'trading.gas_owned_visible(uuid,integer,timestamp with time zone,uuid,timestamp with time zone)',
 'trading.gas_transport_capacity_cut(uuid,uuid)',
 'trading.gas_transport_capacity_known(uuid,integer,uuid)',
 'trading.gas_transport_financial_known(uuid,integer,uuid)',
 'trading.gas_transport_financial_mark_document(trading.gas_transport_financial_marks)',
 'trading.gas_transport_financial_market_known(uuid,uuid,uuid,uuid)',
 'trading.gas_transport_financial_state_document(trading.gas_transport_financial_states)',
 'trading.require_gas_loan_fee_context(uuid,uuid,timestamp with time zone)',
 'trading.require_gas_owned_quantity_amendment(uuid,uuid,jsonb,uuid,timestamp with time zone,uuid,integer)',
 'trading.require_gas_park_custody_receipt(uuid,uuid)',
 'trading.require_gas_transport_financial_source(uuid,jsonb,uuid,timestamp with time zone,timestamp with time zone,uuid)',
 'trading.require_storage_facility_source(uuid,uuid,jsonb,uuid,timestamp with time zone,integer)',
 'trading.validate_gas_balance_fee_determination(uuid,uuid)',
 'trading.verify_delivery_route_allocation(uuid,uuid)'
 ];
BEGIN
 SELECT encode(sha256(convert_to(string_agg(t||'|'||pg_get_functiondef(f.oid)||'|'||f.proowner::regrole::text||'|'||coalesce(f.proacl::text,''),E'\n' ORDER BY t COLLATE "C"),'UTF8')),'hex') INTO installed
 FROM unnest(targets) t JOIN pg_proc f ON f.oid=t::regprocedure;
 IF installed<>'5f2c58ff02af00a55cce4e06fedd843a1acd4673be2734061e38846d80fb3406' THEN RAISE EXCEPTION 'workspace_context_upgrade: unexpected financial reader';END IF;
 FOREACH signature IN ARRAY targets LOOP
  source=pg_get_functiondef(signature::regprocedure);
  source=regexp_replace(source,'\m(FROM|JOIN)([[:space:]]+)market_data\.(temporal_contexts|market_contexts|catalogue_contexts)\M','\1\2security.read_\3','gi');
  source=regexp_replace(source,'\m(FROM|JOIN)([[:space:]]+)trading\.contexts\M','\1\2security.read_trading_contexts','gi');
  EXECUTE source;
 END LOOP;
END$readers$;
-- A saved record can be the first durable consumer of a displayed view. Its
-- insert and receipt share a transaction. The trigger matches only the exact
-- currently bound source referenced by the destination foreign key.
CREATE FUNCTION security.store_workspace_source(catalog uuid,identifier uuid,day date,cutoff timestamptz,prefix integer) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE covered timestamptz; stored uuid;
BEGIN
 SELECT k.coverage_from INTO STRICT covered FROM market_data.knowledge_clocks k WHERE k.catalog_id=catalog AND cutoff BETWEEN k.coverage_from AND k.certified_through;
 stored=security.store_schedule_cut(catalog,day,cutoff,prefix,covered,cutoff);
 IF stored IS DISTINCT FROM identifier THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 INSERT INTO market_data.market_contexts SELECT catalog,stored,coalesce(max(k.event_order),0) FROM market_data.market_knowledge_receipts k WHERE k.catalog_id=catalog AND k.known_from<=cutoff ON CONFLICT DO NOTHING;
 INSERT INTO market_data.catalogue_contexts SELECT catalog,stored,coalesce(max(k.publication_order),0) FROM market_data.catalogue_knowledge_receipts k WHERE k.catalog_id=catalog AND k.known_from<=cutoff ON CONFLICT DO NOTHING;
 INSERT INTO trading.contexts SELECT catalog,stored,coalesce(max(k.publication_order),0) FROM trading.knowledge_receipts k WHERE k.catalog_id=catalog AND k.known_from<=cutoff ON CONFLICT DO NOTHING;
 RETURN stored;
END$$;
ALTER FUNCTION security.store_workspace_source(uuid,uuid,date,timestamptz,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.store_workspace_source(uuid,uuid,date,timestamptz,integer) FROM PUBLIC;

-- A current-write mapping names a historical validation source without switching
-- its write authority. A failed command rolls back both source and artifact.
CREATE FUNCTION security.retain_workspace_write_source(catalog uuid,identifier uuid,day date,cutoff timestamptz,prefix integer,system_id text,timeline integer) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_calendar_reader(catalog);
 IF security.current_write_context() IS NOT TRUE OR security.market_access(catalog,'WriteMarketDefinitions') IS NOT TRUE
 OR security.workspace_cut_visible(catalog,identifier,day,cutoff,prefix,system_id,timeline) IS NOT TRUE THEN RAISE EXCEPTION 'workspace_source_denied' USING ERRCODE='42501';END IF;
 PERFORM security.store_workspace_source(catalog,identifier,day,cutoff,prefix);
END$$;
ALTER FUNCTION security.retain_workspace_write_source(uuid,uuid,date,timestamptz,integer,text,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.retain_workspace_write_source(uuid,uuid,date,timestamptz,integer,text,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.retain_workspace_write_source(uuid,uuid,date,timestamptz,integer,text,integer) TO openetrm_runtime;

CREATE FUNCTION security.retain_referenced_workspace_cut() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected record; fields jsonb; identifier uuid;
BEGIN
 SELECT * INTO selected FROM security.bound_calendar_context;
 IF NOT FOUND THEN RETURN NEW;END IF;
 fields=to_jsonb(NEW);
 IF (fields->>TG_ARGV[0])::uuid=selected.catalog_id AND (fields->>TG_ARGV[1])::uuid=selected.id
 AND NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(selected.catalog_id,selected.id)) THEN
  IF selected.id<>security.workspace_context_id(selected.catalog_id,selected.effective_on,selected.known_at) THEN
   RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';
  END IF;
  identifier=security.store_workspace_source(selected.catalog_id,selected.id,selected.effective_on,selected.known_at,selected.schedule_prefix);
  IF identifier IS DISTINCT FROM selected.id THEN RAISE EXCEPTION 'temporal_context' USING ERRCODE='P0001';END IF;
 END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION security.retain_referenced_workspace_cut() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.retain_referenced_workspace_cut() FROM PUBLIC;

-- An admitted historical assistant turn names its source before the worker
-- sets context_id. Its requested source must survive a closed browser.
CREATE TRIGGER retain_view_requested_context BEFORE INSERT ON assistant.turns FOR EACH ROW
 WHEN (NEW.requested_context IS NOT NULL) EXECUTE FUNCTION security.retain_referenced_workspace_cut('catalog_id','requested_context');

DO $$DECLARE k record;BEGIN
 FOR k IN SELECT DISTINCT c.conrelid::regclass AS relation,a.attname AS catalog_key,b.attname AS context_key
 FROM pg_constraint c JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=c.conkey[1]
 JOIN pg_attribute b ON b.attrelid=c.conrelid AND b.attnum=c.conkey[2]
 WHERE c.contype='f' AND c.confrelid IN('market_data.temporal_contexts'::regclass,'market_data.market_contexts'::regclass,'trading.contexts'::regclass)
 AND c.conrelid NOT IN('security.request_contexts'::regclass,'market_data.market_contexts'::regclass,'market_data.catalogue_contexts'::regclass,'trading.contexts'::regclass)
 AND c.confkey=ARRAY[1,2]::smallint[] AND cardinality(c.conkey)=2 LOOP
  EXECUTE format('CREATE TRIGGER %I BEFORE INSERT OR UPDATE OF %I,%I ON %s FOR EACH ROW WHEN(NEW.%I IS NOT NULL) EXECUTE FUNCTION security.retain_referenced_workspace_cut(%L,%L)',
  'retain_view_'||k.context_key,k.catalog_key,k.context_key,k.relation,k.context_key,k.catalog_key,k.context_key);
 END LOOP;
END$$;
-- A statistical job's calendar and clock require independent issuer
-- attestation. The issuer cannot see an uncommitted workspace receipt in the
-- saving transaction. Attest only a digest to that exact transaction, which
-- then records the ordinary clock receipt with its original foreign keys.
CREATE TABLE risk.pending_clock_attestations(
 backend_pid integer NOT NULL,transaction_id xid8 NOT NULL,proof bytea NOT NULL,
 PRIMARY KEY(backend_pid,transaction_id)
);
ALTER TABLE risk.pending_clock_attestations ENABLE ROW LEVEL SECURITY;
ALTER TABLE risk.pending_clock_attestations FORCE ROW LEVEL SECURITY;
CREATE POLICY attest ON risk.pending_clock_attestations TO openetrm_security_owner USING(true) WITH CHECK(true);
GRANT SELECT,INSERT,DELETE ON risk.pending_clock_attestations TO openetrm_security_owner;
CREATE FUNCTION risk.clock_attestation(catalog uuid,context_id uuid,calendar jsonb,clock jsonb) RETURNS bytea
LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
 SELECT sha256(convert_to(jsonb_build_array($1,$2,$3,$4)::text,'UTF8'))
$$;
ALTER FUNCTION risk.clock_attestation(uuid,uuid,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.clock_attestation(uuid,uuid,jsonb,jsonb) FROM PUBLIC;

CREATE FUNCTION risk.attest_workspace_clock(pid integer,transaction_id xid8,catalog uuid,context_id uuid,calendar jsonb,clock jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT pg_has_role(session_user,'openetrm_authenticator','MEMBER') OR pg_has_role(session_user,'openetrm_runtime','MEMBER')
 OR pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RAISE EXCEPTION 'risk_capture_issuer' USING ERRCODE='42501';END IF;
 IF pg_xact_status($2) IS DISTINCT FROM 'in progress' OR NOT EXISTS(SELECT 1 FROM pg_locks l WHERE l.pid=$1 AND l.granted
  AND l.locktype='transactionid' AND l.mode='ExclusiveLock' AND l.transactionid=$2::xid)
 THEN RAISE EXCEPTION 'risk_capture_input';END IF;
 -- A database rollback also rolls back consumption. Reclaim those signatures
 -- on the next issuance. Only active transactions can receive another one.
 DELETE FROM risk.pending_clock_attestations a WHERE pg_xact_status(a.transaction_id) IS DISTINCT FROM 'in progress';
 INSERT INTO risk.pending_clock_attestations VALUES($1,$2,risk.clock_attestation($3,$4,$5,$6));
END$$;
ALTER FUNCTION risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.attest_workspace_clock(integer,xid8,uuid,uuid,jsonb,jsonb) TO openetrm_authenticator;

-- The released validator above was fingerprinted with its grants. Split out
-- its body so both admissions use the identical evidence and capacity checks.
DO $clock$ DECLARE source text; guard text; body text;BEGIN
 source=pg_get_functiondef('risk.capture_history_clock(uuid,uuid,jsonb,jsonb)'::regprocedure);
 guard=split_part(split_part(source,'BEGIN',2),' IF jsonb_typeof(calendar)',1);
 IF position('risk_capture_issuer' in guard)=0 THEN RAISE EXCEPTION 'workspace_context_upgrade: clock issuer';END IF;
 body=replace(replace(source,'FUNCTION risk.capture_history_clock(','FUNCTION risk.store_history_clock('),guard,chr(10));
 EXECUTE body;
 EXECUTE split_part(source,'BEGIN',1)||'BEGIN'||guard||' RETURN risk.store_history_clock(catalog,context,calendar,clock); END $function$';
END$clock$;
ALTER FUNCTION risk.store_history_clock(uuid,uuid,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.store_history_clock(uuid,uuid,jsonb,jsonb) FROM PUBLIC;

CREATE FUNCTION risk.capture_workspace_clock(catalog uuid,context_id uuid,calendar jsonb,clock jsonb) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source record; result uuid;
BEGIN
 IF security.market_access($1,'ReadMarketData') IS NOT TRUE OR security.trading_access($1,'ReadTrades') IS NOT TRUE
 OR security.has_permission('RunScenario') IS NOT TRUE OR NOT EXISTS(SELECT 1 FROM security.request_contexts q
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
  AND q.expires_at>clock_timestamp() AND (q.market_catalog_id,q.market_context_id)=($1,$2))
 THEN RAISE EXCEPTION 'risk_capture_input' USING ERRCODE='42501';END IF;
 DELETE FROM risk.pending_clock_attestations a WHERE a.backend_pid=pg_backend_pid() AND a.transaction_id=pg_current_xact_id_if_assigned()
 AND a.proof=risk.clock_attestation($1,$2,$3,$4);
 IF NOT FOUND THEN RAISE EXCEPTION 'risk_capture_issuer' USING ERRCODE='42501';END IF;
 SELECT * INTO source FROM security.bound_calendar_context b WHERE (b.catalog_id,b.id)=($1,$2);
 IF FOUND THEN
  PERFORM security.store_workspace_source($1,$2,source.effective_on,source.known_at,source.schedule_prefix);
 END IF;
 result=risk.store_history_clock($1,$2,$3,$4);
 IF security.market_access($1,'ReadMarketData') IS NOT TRUE OR security.trading_access($1,'ReadTrades') IS NOT TRUE
 OR security.has_permission('RunScenario') IS NOT TRUE THEN RAISE EXCEPTION 'risk_capture_input' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION risk.capture_workspace_clock(uuid,uuid,jsonb,jsonb) TO openetrm_runtime;
COMMIT;
