-- Consumer: product-owned futures margin trajectories and ordinary cash PV.
-- Accepted determinations, due obligations and recorded payments are distinct.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE settlement_data.futures_margin_adoptions ADD UNIQUE(catalog_id,trade_id,binding_id,policy_id,policy_revision);

CREATE TABLE settlement_data.futures_margin_runs(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>=2),
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,source_context_id uuid NOT NULL,as_of ref_data.instant NOT NULL,
 through_occurrence uuid NOT NULL,determination_count integer NOT NULL CHECK(determination_count BETWEEN 1 AND 16384),
 implementation text NOT NULL CHECK(implementation='native_futures_margin_journal_v1'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,binding_id,revision),UNIQUE(catalog_id,trade_id,binding_id,revision,publication_order),
 FOREIGN KEY(catalog_id,trade_id,binding_id,policy_id,policy_revision)
  REFERENCES settlement_data.futures_margin_adoptions(catalog_id,trade_id,binding_id,policy_id,policy_revision),
 FOREIGN KEY(catalog_id,policy_id,policy_revision,through_occurrence)
  REFERENCES settlement_data.futures_margin_policy_occurrences(catalog_id,policy_id,policy_revision,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE settlement_data.futures_margin_determinations(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,run_revision integer NOT NULL,
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,occurrence_id uuid NOT NULL,
 previous_id uuid,basis_id uuid,source_observation_id uuid NOT NULL,previous_basis ref_data.exact_number NOT NULL,
 exact_variation text NOT NULL CHECK(length(exact_variation)<=4096),rounded_total ref_data.exact_number NOT NULL,
 adjustment ref_data.exact_number NOT NULL,adjustment_due ref_data.instant,cash_binding_id uuid,publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,binding_id,id),UNIQUE(catalog_id,trade_id,binding_id,run_revision,occurrence_id),
 FOREIGN KEY(catalog_id,trade_id,binding_id,run_revision,publication_order)
  REFERENCES settlement_data.futures_margin_runs(catalog_id,trade_id,binding_id,revision,publication_order),
 FOREIGN KEY(catalog_id,policy_id,policy_revision,occurrence_id)
  REFERENCES settlement_data.futures_margin_policy_occurrences(catalog_id,policy_id,policy_revision,id),
 FOREIGN KEY(catalog_id,trade_id,binding_id,previous_id) REFERENCES settlement_data.futures_margin_determinations(catalog_id,trade_id,binding_id,id),
 FOREIGN KEY(catalog_id,trade_id,binding_id,basis_id) REFERENCES settlement_data.futures_margin_determinations(catalog_id,trade_id,binding_id,id),
 FOREIGN KEY(catalog_id,source_observation_id) REFERENCES market_data.observations(catalog_id,id),
 FOREIGN KEY(catalog_id,trade_id,cash_binding_id) REFERENCES trading.cash_payment_memberships(catalog_id,trade_id,binding_id),
 CHECK((adjustment<>0)=(adjustment_due IS NOT NULL AND cash_binding_id IS NOT NULL)),
 CHECK((adjustment_due IS NULL)=(cash_binding_id IS NULL))
);
CREATE INDEX futures_margin_occurrence_head ON settlement_data.futures_margin_determinations(catalog_id,trade_id,binding_id,occurrence_id,run_revision DESC);
CREATE TABLE settlement_data.futures_margin_run_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),request jsonb NOT NULL CHECK(pg_column_size(request)<=4194304),
 source_context_id uuid NOT NULL,authority_until timestamptz NOT NULL CHECK(isfinite(authority_until)),outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,trade_id,binding_id) REFERENCES settlement_data.futures_margin_adoptions(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
DO $tables$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['futures_margin_runs','futures_margin_determinations','futures_margin_run_commands'] LOOP
  EXECUTE format('ALTER TABLE settlement_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE settlement_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON settlement_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON settlement_data.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON settlement_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$tables$;
CREATE POLICY scoped_read ON settlement_data.futures_margin_runs FOR SELECT TO openetrm_runtime USING(
 security.trading_visible(catalog_id,publication_order,as_of) AND EXISTS(SELECT 1 FROM settlement_data.futures_margin_adoptions a
 WHERE(a.catalog_id,a.trade_id,a.binding_id)=(futures_margin_runs.catalog_id,futures_margin_runs.trade_id,futures_margin_runs.binding_id)));
CREATE POLICY scoped_read ON settlement_data.futures_margin_determinations FOR SELECT TO openetrm_runtime USING(
 EXISTS(SELECT 1 FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policy_occurrences o
 ON(o.catalog_id,o.policy_id,o.policy_revision)=(a.catalog_id,a.policy_id,a.policy_revision)
 WHERE(a.catalog_id,a.trade_id,a.binding_id,o.id)=(futures_margin_determinations.catalog_id,futures_margin_determinations.trade_id,
 futures_margin_determinations.binding_id,futures_margin_determinations.occurrence_id)
 AND security.trading_visible(a.catalog_id,futures_margin_determinations.publication_order,o.at)));
GRANT SELECT ON settlement_data.futures_margin_runs,settlement_data.futures_margin_determinations TO openetrm_runtime;

CREATE FUNCTION settlement_data.guard_futures_margin_determination() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM settlement_data.futures_margin_runs r JOIN trading.publications p
 ON(p.catalog_id,p.publication_order)=(r.catalog_id,r.publication_order)
 WHERE(r.catalog_id,r.trade_id,r.binding_id,r.revision,r.policy_id,r.policy_revision,r.publication_order)=
 (NEW.catalog_id,NEW.trade_id,NEW.binding_id,NEW.run_revision,NEW.policy_id,NEW.policy_revision,NEW.publication_order)
 AND p.publication_xid=pg_current_xact_id()) THEN RAISE EXCEPTION 'futures_margin_run_sealed';END IF;
 RETURN NEW;
END$function$;
CREATE TRIGGER sealed_run BEFORE INSERT ON settlement_data.futures_margin_determinations
 FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_futures_margin_determination();
CREATE FUNCTION settlement_data.check_futures_margin_run() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF(SELECT count(*) FROM settlement_data.futures_margin_determinations d
 WHERE(d.catalog_id,d.trade_id,d.binding_id,d.run_revision)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id,NEW.revision))<>NEW.determination_count
 THEN RAISE EXCEPTION 'futures_margin_run_incomplete';END IF;
 RETURN NULL;
END$function$;
CREATE CONSTRAINT TRIGGER complete_run AFTER INSERT ON settlement_data.futures_margin_runs DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_margin_run();

-- Retain the exact origin reader internally. Old inception callers must refuse
-- when actual accepted margin is visible, rather than erase its history.
ALTER FUNCTION settlement_data.read_futures_margin_inception(uuid,uuid,uuid,uuid,timestamptz) RENAME TO read_futures_margin_origin;
REVOKE ALL ON FUNCTION settlement_data.read_futures_margin_origin(uuid,uuid,uuid,uuid,timestamptz) FROM openetrm_runtime;
CREATE FUNCTION settlement_data.read_futures_margin_state(catalog uuid,trade uuid,binding uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE result jsonb;prefix integer;cut timestamptz;version integer;BEGIN
 result=settlement_data.read_futures_margin_origin(catalog,trade,binding,context,as_of);
 IF(result->>'available')::boolean IS NOT TRUE THEN RETURN result;END IF;
 SELECT x.known_through,t.known_at INTO STRICT prefix,cut FROM trading.contexts x JOIN market_data.temporal_contexts t
 ON(t.catalog_id,t.id)=(x.catalog_id,x.context_id) WHERE(x.catalog_id,x.context_id)=(catalog,context);
 SELECT coalesce(max(r.revision),1) INTO version FROM settlement_data.futures_margin_runs r JOIN trading.knowledge_receipts k
 ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
 WHERE(r.catalog_id,r.trade_id,r.binding_id)=(catalog,trade,binding) AND r.publication_order<=prefix AND k.known_from<=cut;
 result=result||jsonb_build_object('stateProfile','native_futures_margin_journal_v1','version',version,'determinations',coalesce((SELECT jsonb_agg(to_jsonb(v) ORDER BY v.at,v.phase) FROM(
 WITH heads AS MATERIALIZED(
 SELECT DISTINCT ON(o.at,o.phase) d.*,o.at,o.phase,k.known_from
 FROM settlement_data.futures_margin_determinations d JOIN settlement_data.futures_margin_policy_occurrences o
 ON(o.catalog_id,o.policy_id,o.policy_revision,o.id)=(d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
 WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) AND d.publication_order<=prefix AND k.known_from<=cut AND o.at<=as_of
 ORDER BY o.at,o.phase,d.run_revision DESC)
 SELECT h.*,sk.known_from AS source_known_at,to_jsonb(s) AS source FROM heads h
 JOIN market_data.observations s ON(s.catalog_id,s.id)=(h.catalog_id,h.source_observation_id)
 JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(s.catalog_id,s.id)
 JOIN market_data.market_knowledge_receipts sk ON(sk.catalog_id,sk.event_order,sk.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
 JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(catalog,context)
 WHERE e.event_order<=m.known_through AND sk.known_from<=cut)v),'[]'::jsonb),
 'obligations',coalesce((SELECT jsonb_agg(to_jsonb(v) ORDER BY v.publication_order,v.cash_binding_id) FROM(
 SELECT d.id,d.cash_binding_id,d.adjustment,d.publication_order,o.at,i.instrument_id,
 i.reference_catalog_id,i.reference_revision_id,i.currency_code,i.payment_date,i.payment_at
 FROM settlement_data.futures_margin_determinations d
 JOIN settlement_data.futures_margin_policy_occurrences o ON(o.catalog_id,o.policy_id,o.policy_revision,o.id)=
 (d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
 JOIN trading.cash_payment_memberships b ON(b.catalog_id,b.trade_id,b.binding_id)=(d.catalog_id,d.trade_id,d.cash_binding_id)
 JOIN trading.cash_payment_instruments i ON(i.catalog_id,i.instrument_id)=(b.catalog_id,b.instrument_id)
 WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) AND d.publication_order<=prefix AND k.known_from<=cut AND o.at<=as_of)v),'[]'::jsonb));
 IF jsonb_array_length(result->'determinations')<>(SELECT count(DISTINCT d.occurrence_id)
 FROM settlement_data.futures_margin_determinations d JOIN settlement_data.futures_margin_policy_occurrences o
 ON(o.catalog_id,o.policy_id,o.policy_revision,o.id)=(d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
 WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) AND d.publication_order<=prefix AND k.known_from<=cut AND o.at<=as_of)
 THEN RAISE EXCEPTION 'futures_margin_context';END IF;
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,(result->>'authorityUntil')::timestamptz);
 RETURN result;
END$function$;
CREATE FUNCTION settlement_data.read_futures_margin_inception(catalog uuid,trade uuid,binding uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE result jsonb;BEGIN
 result=settlement_data.read_futures_margin_state(catalog,trade,binding,context,as_of);
 IF jsonb_array_length(result->'determinations')>0 THEN RAISE EXCEPTION 'futures_margin_history_required';END IF;
 RETURN result||jsonb_build_object('stateProfile','native_execution_inception_v1');
END$function$;
CREATE FUNCTION settlement_data.finish_futures_margin_run(catalog uuid,operation uuid,body jsonb,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE actor uuid;c settlement_data.futures_margin_run_commands;p settlement_data.futures_margin_policies;BEGIN
 actor=settlement_data.require_futures_margin_authority(catalog,'AmendTrade',until_at);
 SELECT * INTO c FROM settlement_data.futures_margin_run_commands r WHERE(r.catalog_id,r.operation_id)=(catalog,operation);
 IF c.operation_id IS NULL OR(c.actor_id,c.request) IS DISTINCT FROM(actor,body) THEN RAISE EXCEPTION 'futures_margin_command_conflict';END IF;
 SELECT s.* INTO STRICT p FROM settlement_data.futures_margin_policies s JOIN settlement_data.futures_margin_adoptions a
 ON(a.catalog_id,a.policy_id,a.policy_revision)=(s.catalog_id,s.id,s.revision)
 WHERE(a.catalog_id,a.trade_id,a.binding_id)=(catalog,c.trade_id,c.binding_id);
 PERFORM settlement_data.require_futures_margin_reference(p);
 PERFORM settlement_data.require_futures_margin_authority(catalog,'AmendTrade',until_at);
END$function$;
CREATE FUNCTION settlement_data.futures_margin_run_status(catalog uuid,operation uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE until_at timestamptz;c settlement_data.futures_margin_run_commands;BEGIN
 until_at=settlement_data.capture_futures_margin_authority(catalog,'AmendTrade');
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('futures_margin_run:'||catalog::text||':'||operation::text,0));
 SELECT * INTO c FROM settlement_data.futures_margin_run_commands r WHERE(r.catalog_id,r.operation_id)=(catalog,operation);
 IF c.operation_id IS NOT NULL THEN PERFORM settlement_data.finish_futures_margin_run(catalog,operation,body,until_at);END IF;
 PERFORM settlement_data.require_futures_margin_authority(catalog,'AmendTrade',until_at);
 RETURN c.outcome;
END$function$;
CREATE FUNCTION settlement_data.check_futures_margin_run_authority() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 PERFORM settlement_data.finish_futures_margin_run(NEW.catalog_id,NEW.operation_id,NEW.request,NEW.authority_until);
 RETURN NULL;
END$function$;
CREATE CONSTRAINT TRIGGER commit_authority AFTER INSERT ON settlement_data.futures_margin_run_commands DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_margin_run_authority();

CREATE FUNCTION settlement_data.require_futures_margin_preview(catalog uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE hosted text;BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 IF until_at IS NULL OR clock_timestamp()>=until_at OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
 SELECT q.hosted_session_digest INTO hosted FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF hosted IS NOT NULL THEN PERFORM security.require_hosted_session();END IF;
END$function$;
CREATE FUNCTION settlement_data.futures_margin_preview_authority(catalog uuid) RETURNS timestamptz
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE until_at timestamptz;hosted text;session_until timestamptz;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'));
 SELECT q.hosted_session_digest INTO hosted FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF hosted IS NOT NULL THEN
  SELECT s.expires_at INTO session_until FROM security.hosted_session_info(hosted)s;
  IF session_until IS NULL THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
  until_at=least(until_at,session_until);
 END IF;
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
 RETURN until_at;
END$function$;

-- Calculated operands come only from the managed product planner. Native
-- admission owns exact source/owner/closure relationships, heads and atomic cash
-- membership. It does not implement another pricing or rounding algorithm.
CREATE FUNCTION settlement_data.determine_futures_margin(catalog uuid,body jsonb,steps jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE operation uuid;actor uuid;until_at timestamptz;prior jsonb;trade uuid;binding uuid;context uuid;as_of timestamptz;
 a settlement_data.futures_margin_adoptions;p settlement_data.futures_margin_policies;origin trading.trade_revisions;head trading.trade_revisions;
 target settlement_data.futures_margin_policy_occurrences;o settlement_data.futures_margin_policy_occurrences;
 proposed_target settlement_data.futures_margin_policy_occurrences;existing_run settlement_data.futures_margin_runs;
 old settlement_data.futures_margin_determinations;source market_data.observations;curve market_data.curves;
 item jsonb;required uuid[];version integer;changed integer;publication integer;prefix integer;cut timestamptz;market_prefix integer;
 previous_price numeric;predecessor uuid;identity uuid;cash_binding uuid;cash_instrument uuid;total numeric;delta numeric;due timestamptz;
 unchanged boolean;position integer=0;corrections integer=0;outcome jsonb;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>4194304 THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 operation=(body->>'operationId')::uuid;until_at=settlement_data.capture_futures_margin_authority(catalog,'AmendTrade');
 prior=settlement_data.futures_margin_run_status(catalog,operation,body);
 IF prior IS NOT NULL THEN PERFORM settlement_data.finish_futures_margin_run(catalog,operation,body,until_at);RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 actor=settlement_data.require_futures_margin_authority(catalog,'AmendTrade',until_at);
 IF NOT(body?&ARRAY['operationId','tradeId','bindingId','expectedRevision','expectedMarginRevision','contextId','asOf','throughOccurrence','implementation','correctionDue','previewHash'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','tradeId','bindingId','expectedRevision','expectedMarginRevision','contextId','asOf','throughOccurrence','implementation','correctionDue','previewHash'))
 OR body->>'previewHash' IS NULL OR body->>'previewHash'!~'^[0-9a-f]{64}$'
 OR body->>'implementation' IS DISTINCT FROM 'native_futures_margin_journal_v1' OR jsonb_typeof(body->'correctionDue') IS DISTINCT FROM 'array'
 OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[1-9][0-9]{0,8}$'
 OR jsonb_typeof(body->'expectedMarginRevision') IS DISTINCT FROM 'number' OR body->>'expectedMarginRevision'!~'^[1-9][0-9]{0,8}$'
 OR jsonb_typeof(steps) IS DISTINCT FROM 'array' OR jsonb_array_length(steps) NOT BETWEEN 1 AND 16384 OR pg_column_size(steps)>4194304
 THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 trade=(body->>'tradeId')::uuid;binding=(body->>'bindingId')::uuid;context=(body->>'contextId')::uuid;
 as_of=settlement_data.futures_margin_utc_instant(body->>'asOf');
 SELECT * INTO a FROM settlement_data.futures_margin_adoptions s WHERE(s.catalog_id,s.trade_id,s.binding_id)=(catalog,trade,binding);
 IF a.trade_id IS NULL THEN RAISE EXCEPTION 'futures_margin_unavailable';END IF;
 SELECT * INTO STRICT p FROM settlement_data.futures_margin_policies s WHERE(s.catalog_id,s.id,s.revision)=(catalog,a.policy_id,a.policy_revision);
 PERFORM settlement_data.require_futures_margin_reference(p);
 SELECT * INTO STRICT origin FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision)=(catalog,trade,a.origin_revision);
 SELECT * INTO target FROM settlement_data.futures_margin_policy_occurrences s
 WHERE(s.catalog_id,s.policy_id,s.policy_revision,s.id)=(catalog,p.id,p.revision,(body->>'throughOccurrence')::uuid);
 IF target.id IS NULL OR target.at>as_of THEN RAISE EXCEPTION 'futures_margin_coverage';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(steps)s WHERE jsonb_typeof(s) IS DISTINCT FROM 'object' OR
 NOT(s?&ARRAY['occurrenceId','previousDeterminationId','sourceObservationId','previousBasis','exactVariation','roundedTotal','adjustment','adjustmentDue','noChange'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(s)k WHERE k NOT IN('occurrenceId','previousDeterminationId','sourceObservationId','previousBasis','exactVariation','roundedTotal','adjustment','adjustmentDue','noChange'))
 OR jsonb_typeof(s->'noChange') IS DISTINCT FROM 'boolean') THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 SELECT * INTO proposed_target FROM settlement_data.futures_margin_policy_occurrences s
 WHERE(s.catalog_id,s.policy_id,s.policy_revision,s.id)=(catalog,p.id,p.revision,(steps->-1->>'occurrenceId')::uuid);
 IF proposed_target.id IS NULL OR(proposed_target.at,proposed_target.phase)<(target.at,target.phase) OR proposed_target.at>as_of
 THEN RAISE EXCEPTION 'futures_margin_coverage';END IF;
 SELECT array_agg(s.id ORDER BY s.at,s.phase) INTO required FROM settlement_data.futures_margin_policy_occurrences s
 WHERE(s.catalog_id,s.policy_id,s.policy_revision)=(catalog,p.id,p.revision) AND(s.at,s.phase)<=(proposed_target.at,proposed_target.phase)
 AND(s.at>origin.execution_at OR(s.at=origin.execution_at AND p.opening='include_opening_instant'));
 IF required IS DISTINCT FROM ARRAY(SELECT(s->>'occurrenceId')::uuid FROM jsonb_array_elements(steps)s)
 THEN RAISE EXCEPTION 'futures_margin_coverage';END IF;
 IF EXISTS(SELECT 1 FROM settlement_data.futures_margin_policy_occurrences s WHERE s.catalog_id=catalog
 AND(s.policy_id,s.policy_revision)=(p.id,p.revision) AND s.id=ANY(required) AND s.at<origin.effective_at)
 THEN RAISE EXCEPTION 'futures_margin_continuity';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(body->'correctionDue')c WHERE jsonb_typeof(c) IS DISTINCT FROM 'object'
 OR NOT(c?&ARRAY['occurrenceId','dueAt']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(c)k WHERE k NOT IN('occurrenceId','dueAt')))
 OR jsonb_array_length(body->'correctionDue')<>(SELECT count(DISTINCT(c->>'occurrenceId')::uuid) FROM jsonb_array_elements(body->'correctionDue')c)
 THEN RAISE EXCEPTION 'futures_margin_correction_due';END IF;
 -- A previously recorded full financial snapshot is acknowledged before live
 -- heads/source frames are recomputed. This never reinstates an old basis.
 SELECT r.* INTO existing_run FROM settlement_data.futures_margin_runs r
 WHERE(r.catalog_id,r.trade_id,r.binding_id,r.through_occurrence)=(catalog,trade,binding,proposed_target.id) AND NOT EXISTS(
  SELECT 1 FROM jsonb_array_elements(steps)s LEFT JOIN LATERAL(
   SELECT d.* FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=
    (catalog,trade,binding,(s->>'occurrenceId')::uuid) AND d.run_revision<=r.revision ORDER BY d.run_revision DESC LIMIT 1)d ON true
  WHERE d.id IS NULL OR(d.source_observation_id,d.previous_basis,d.rounded_total) IS DISTINCT FROM
   ((s->>'sourceObservationId')::uuid,(s->>'previousBasis')::numeric,(s->>'roundedTotal')::numeric)
  OR CASE WHEN(s->>'noChange')::boolean THEN d.id IS DISTINCT FROM(s->>'previousDeterminationId')::uuid
    OR(s->>'adjustment')::numeric IS DISTINCT FROM 0 OR s->>'adjustmentDue' IS NOT NULL
   ELSE d.previous_id IS DISTINCT FROM(s->>'previousDeterminationId')::uuid OR d.adjustment IS DISTINCT FROM(s->>'adjustment')::numeric
    OR d.adjustment_due IS DISTINCT FROM(CASE WHEN s->>'adjustmentDue' IS NOT NULL
      THEN settlement_data.futures_margin_utc_instant(s->>'adjustmentDue') ELSE NULL END) END)
 ORDER BY r.revision LIMIT 1;
 IF existing_run.revision IS NOT NULL THEN
  IF jsonb_array_length(body->'correctionDue')<>(SELECT count(*) FROM jsonb_array_elements(steps)s
   WHERE(s->>'noChange')::boolean IS FALSE AND s->>'previousDeterminationId' IS NOT NULL AND(s->>'adjustment')::numeric<>0)
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(body->'correctionDue')c WHERE NOT EXISTS(SELECT 1 FROM jsonb_array_elements(steps)s
   WHERE(s->>'noChange')::boolean IS FALSE AND(s->>'occurrenceId')::uuid=(c->>'occurrenceId')::uuid
   AND(s->>'adjustment')::numeric<>0 AND settlement_data.futures_margin_utc_instant(s->>'adjustmentDue')=settlement_data.futures_margin_utc_instant(c->>'dueAt')))
  THEN RAISE EXCEPTION 'futures_margin_correction_due';END IF;
  SELECT coalesce(max(r.revision),1) INTO version FROM settlement_data.futures_margin_runs r WHERE(r.catalog_id,r.trade_id,r.binding_id)=(catalog,trade,binding);
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'bindingId',binding,'version',existing_run.revision,
   'currentVersion',version,'publicationOrder',existing_run.publication_order,'changed',0);
  INSERT INTO settlement_data.futures_margin_run_commands VALUES(catalog,operation,trade,binding,actor,body,context,until_at,outcome);
  PERFORM settlement_data.finish_futures_margin_run(catalog,operation,body,until_at);RETURN outcome;
 END IF;
 -- A correction must include every accepted successor, even when its cash delta is zero.
 SELECT s.* INTO o FROM settlement_data.futures_margin_policy_occurrences s JOIN settlement_data.futures_margin_determinations d
 ON(d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id)=(s.catalog_id,s.policy_id,s.policy_revision,s.id)
 WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,trade,binding) ORDER BY s.at DESC,s.phase DESC LIMIT 1;
 IF o.id IS NOT NULL AND(o.at,o.phase)>(target.at,target.phase) THEN target=o;END IF;
 SELECT array_agg(s.id ORDER BY s.at,s.phase) INTO required FROM settlement_data.futures_margin_policy_occurrences s
 WHERE(s.catalog_id,s.policy_id,s.policy_revision)=(catalog,p.id,p.revision) AND(s.at,s.phase)<=(target.at,target.phase)
 AND(s.at>origin.execution_at OR(s.at=origin.execution_at AND p.opening='include_opening_instant'));
 IF cardinality(required) IS DISTINCT FROM jsonb_array_length(steps) OR target.at>as_of THEN RAISE EXCEPTION 'futures_margin_coverage';END IF;
 SELECT count(*) FILTER(WHERE(s->>'noChange')::boolean IS FALSE) INTO changed FROM jsonb_array_elements(steps)s;
 SELECT coalesce(max(r.revision),1),coalesce(max(r.publication_order),a.publication_order) INTO version,publication
 FROM settlement_data.futures_margin_runs r WHERE(r.catalog_id,r.trade_id,r.binding_id)=(catalog,trade,binding);
 IF changed>0 THEN
  IF version IS DISTINCT FROM(body->>'expectedMarginRevision')::integer THEN RAISE EXCEPTION 'futures_margin_revision';END IF;
  SELECT * INTO head FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) ORDER BY r.revision DESC LIMIT 1;
  IF head.revision IS DISTINCT FROM(body->>'expectedRevision')::integer OR head.effective_at>as_of THEN RAISE EXCEPTION 'futures_revision_conflict';END IF;
  PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,p.definition_revision_id);
  SELECT x.known_through,t.known_at,m.known_through INTO STRICT prefix,cut,market_prefix FROM market_data.temporal_contexts t
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(catalog,context);
  IF greatest(a.publication_order,head.publication_order,publication)>prefix OR EXISTS(
   SELECT 1 FROM trading.knowledge_receipts k WHERE k.catalog_id=catalog AND k.publication_order IN(a.publication_order,head.publication_order,publication)
   AND k.known_from>cut) THEN RAISE EXCEPTION 'futures_margin_context';END IF;
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());version=version+1;
  INSERT INTO settlement_data.futures_margin_runs VALUES(catalog,trade,binding,version,p.id,p.revision,context,as_of,target.id,changed,body->>'implementation',actor,publication);
 END IF;
 SELECT * INTO STRICT curve FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,p.definition_revision_id,p.settlement_curve_code);
 previous_price=origin.execution_price;
 FOR item IN SELECT * FROM jsonb_array_elements(steps) LOOP
  position=position+1;
  IF(item->>'occurrenceId')::uuid IS DISTINCT FROM required[position] THEN RAISE EXCEPTION 'futures_margin_coverage';END IF;
  SELECT * INTO STRICT o FROM settlement_data.futures_margin_policy_occurrences s WHERE(s.catalog_id,s.policy_id,s.policy_revision,s.id)=(catalog,p.id,p.revision,required[position]);
  IF o.at<origin.effective_at THEN RAISE EXCEPTION 'futures_margin_continuity';END IF;
  SELECT * INTO old FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=
   (catalog,trade,binding,o.id) ORDER BY d.run_revision DESC LIMIT 1;
  IF(item->>'previousDeterminationId')::uuid IS DISTINCT FROM old.id OR(item->>'previousBasis')::numeric IS DISTINCT FROM previous_price
  THEN RAISE EXCEPTION 'futures_margin_revision';END IF;
  SELECT * INTO source FROM market_data.observations s WHERE(s.catalog_id,s.id)=(catalog,(item->>'sourceObservationId')::uuid);
  IF source.id IS NULL OR source.definition_revision_id<>p.definition_revision_id OR source.source_code<>curve.direct_source_code
  OR source.publication_state<>'final' OR source.observed_at<>o.at OR source.role<>(CASE o.phase WHEN 'final' THEN 'final_contract_settlement' ELSE 'daily_settlement' END)
  OR NOT EXISTS(SELECT 1 FROM market_data.series s WHERE(s.catalog_id,s.revision_id,s.id,s.contract_id,s.quote_code)=
   (catalog,p.definition_revision_id,source.series_id,p.contract_id,p.quote_code)) THEN RAISE EXCEPTION 'futures_settlement_evidence';END IF;
  IF changed>0 AND(SELECT count(*) FROM(
   SELECT s.id FROM market_data.observations s JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(s.catalog_id,s.id)
   JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
   WHERE(s.catalog_id,s.definition_revision_id,s.series_id,s.source_code,s.role,s.observed_at)=
    (catalog,p.definition_revision_id,source.series_id,source.source_code,source.role,source.observed_at)
   AND e.event_order<=market_prefix AND k.known_from<=cut AND NOT EXISTS(
    SELECT 1 FROM market_data.observations n JOIN market_data.knowledge_events ne ON(ne.catalog_id,ne.observation_id)=(n.catalog_id,n.id)
    JOIN market_data.market_knowledge_receipts nk ON(nk.catalog_id,nk.event_order,nk.publication_xid)=(ne.catalog_id,ne.event_order,ne.publication_xid)
    WHERE(n.catalog_id,n.corrects_id)=(s.catalog_id,s.id) AND ne.event_order<=market_prefix AND nk.known_from<=cut))terminal
   HAVING count(*)=1 AND bool_and(terminal.id=source.id)) IS DISTINCT FROM 1 THEN RAISE EXCEPTION 'futures_settlement_evidence';END IF;
  total=(item->>'roundedTotal')::numeric;delta=(item->>'adjustment')::numeric;
  IF total IS NULL OR delta IS DISTINCT FROM(total-coalesce(old.rounded_total,0)) OR mod(total,p.rounding_increment)<>0
  THEN RAISE EXCEPTION 'futures_margin_contribution';END IF;
  -- IS NOT DISTINCT FROM handles the first occurrence's explicit null predecessor.
  unchanged=old.id IS NOT NULL AND(old.source_observation_id,old.previous_basis,old.rounded_total,old.basis_id)
   IS NOT DISTINCT FROM(source.id,previous_price,total,predecessor);
  IF unchanged IS DISTINCT FROM(item->>'noChange')::boolean THEN RAISE EXCEPTION 'futures_margin_contribution';END IF;
  due=NULL;cash_binding=NULL;
  IF delta<>0 THEN
   due=settlement_data.futures_margin_utc_instant(item->>'adjustmentDue');
   IF old.id IS NULL THEN
    IF due IS DISTINCT FROM o.due_at THEN RAISE EXCEPTION 'futures_margin_correction_due';END IF;
   ELSE
    IF(SELECT count(*) FROM jsonb_array_elements(body->'correctionDue')c WHERE(c->>'occurrenceId')::uuid=o.id
     AND settlement_data.futures_margin_utc_instant(c->>'dueAt')=due)<>1 THEN RAISE EXCEPTION 'futures_margin_correction_due';END IF;
    corrections=corrections+1;
   END IF;
   IF due<o.at OR due IS NULL THEN RAISE EXCEPTION 'futures_margin_correction_due';END IF;
  ELSIF item->>'adjustmentDue' IS NOT NULL THEN RAISE EXCEPTION 'futures_margin_correction_due';END IF;
  IF unchanged THEN predecessor=old.id;
  ELSE
   identity=gen_random_uuid();
   IF delta<>0 THEN
    cash_binding=gen_random_uuid();cash_instrument=security.timed_cash_payment_instrument_id(p.reference_catalog_id,p.reference_revision_id,p.currency_code,due);
    IF NOT EXISTS(SELECT 1 FROM trading.cash_payment_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,cash_instrument)) THEN
     -- Currency/due identifies a reusable definition with no business start.
     -- The native beginning-of-time marker is distinct from the independently
     -- owned membership's actual effective instant and certified publication.
     INSERT INTO trading.instruments VALUES(catalog,cash_instrument,publication,TIMESTAMPTZ '0001-01-01 00:00:00+00');
     INSERT INTO trading.cash_payment_instruments(catalog_id,instrument_id,reference_catalog_id,reference_revision_id,currency_code,payment_date,payment_at,publication_order,effective_at)
     VALUES(catalog,cash_instrument,p.reference_catalog_id,p.reference_revision_id,p.currency_code,NULL,due,publication,TIMESTAMPTZ '0001-01-01 00:00:00+00');
    END IF;
    IF EXISTS(SELECT 1 FROM trading.cash_payment_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,cash_instrument) AND i.effective_at>o.at)
    THEN RAISE EXCEPTION 'futures_margin_cash_definition';END IF;
    INSERT INTO trading.cash_payment_memberships VALUES(catalog,trade,cash_binding,cash_instrument,a.book_id,publication,o.at);
   END IF;
   INSERT INTO settlement_data.futures_margin_determinations VALUES(catalog,identity,trade,binding,version,p.id,p.revision,o.id,old.id,predecessor,
    source.id,previous_price,item->>'exactVariation',total,delta,due,cash_binding,publication);
   predecessor=identity;
  END IF;
  previous_price=source.value;
 END LOOP;
 IF corrections<>jsonb_array_length(body->'correctionDue') THEN RAISE EXCEPTION 'futures_margin_correction_due';END IF;
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'bindingId',binding,'version',version,'currentVersion',version,'publicationOrder',publication,'changed',changed);
 INSERT INTO settlement_data.futures_margin_run_commands VALUES(catalog,operation,trade,binding,actor,body,context,until_at,outcome);
 PERFORM settlement_data.finish_futures_margin_run(catalog,operation,body,until_at);
 RETURN outcome;
END$function$;

DO $functions$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='settlement_data' AND p.proname IN('guard_futures_margin_determination','check_futures_margin_run',
 'read_futures_margin_state','read_futures_margin_inception','finish_futures_margin_run','futures_margin_run_status',
 'check_futures_margin_run_authority','determine_futures_margin','require_futures_margin_preview','futures_margin_preview_authority') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$functions$;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_margin_state(uuid,uuid,uuid,uuid,timestamptz),
 settlement_data.read_futures_margin_inception(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION settlement_data.finish_futures_margin_run(uuid,uuid,jsonb,timestamptz),
 settlement_data.futures_margin_run_status(uuid,uuid,jsonb),settlement_data.determine_futures_margin(uuid,jsonb,jsonb) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION settlement_data.require_futures_margin_preview(uuid,timestamptz),settlement_data.futures_margin_preview_authority(uuid) TO openetrm_runtime;
COMMIT;
