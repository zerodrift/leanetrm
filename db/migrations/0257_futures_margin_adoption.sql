-- Consumers: saved dated margin policies, explicit inception adoption and its
-- certified valuation-state reader. This records no settlement or paid cash.
-- Stop write admission and drain calls before installing continuity triggers.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE settlement_data.futures_margin_policies(
 catalog_id uuid NOT NULL,id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),
 definition_revision_id uuid NOT NULL,contract_id uuid NOT NULL,settlement_curve_code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,quote_code ref_data.code NOT NULL,
 from_at ref_data.instant NOT NULL,until_at ref_data.instant NOT NULL,opening text NOT NULL CHECK(opening IN('after_opening_instant','include_opening_instant')),
 rounding_increment ref_data.exact_number NOT NULL CHECK(rounding_increment>0),tie_rule text NOT NULL CHECK(tie_rule IN('to_even','away_from_zero')),
 rounding_stage text NOT NULL CHECK(rounding_stage='payment_total'),rounding_scope text NOT NULL CHECK(rounding_scope='held_binding_difference_payment_total_v1'),
 implementation text NOT NULL CHECK(implementation='dated_futures_margin_policy_v1'),occurrence_count integer NOT NULL CHECK(occurrence_count BETWEEN 1 AND 16384),
 terms_hash text NOT NULL CHECK(terms_hash~'^[0-9a-f]{64}$'),source_context_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id,revision),UNIQUE(catalog_id,id,revision,publication_order),UNIQUE(catalog_id,id,from_at,terms_hash),
 CHECK(from_at<until_at),CHECK(id<>'00000000-0000-0000-0000-000000000000'::uuid),
 FOREIGN KEY(catalog_id,definition_revision_id,settlement_curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE settlement_data.futures_margin_policy_occurrences(
 catalog_id uuid NOT NULL,policy_id uuid NOT NULL,policy_revision integer NOT NULL,id uuid NOT NULL,
 at ref_data.instant NOT NULL,due_at ref_data.instant NOT NULL,phase text NOT NULL CHECK(phase IN('daily','final')),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,policy_id,policy_revision,id),UNIQUE(catalog_id,policy_id,policy_revision,at,phase),
 CHECK(due_at>=at),CHECK(id<>'00000000-0000-0000-0000-000000000000'::uuid),
 FOREIGN KEY(catalog_id,policy_id,policy_revision,publication_order) REFERENCES settlement_data.futures_margin_policies(catalog_id,id,revision,publication_order)
);
CREATE TABLE settlement_data.futures_margin_adoptions(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,origin_revision integer NOT NULL,
 book_id uuid NOT NULL,instrument_id uuid NOT NULL,origin_publication integer NOT NULL,policy_id uuid NOT NULL,policy_revision integer NOT NULL,
 implementation text NOT NULL CHECK(implementation='native_execution_inception_v1'),source_context_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,trade_id,origin_revision,book_id,binding_id,instrument_id,origin_publication)
  REFERENCES trading.trade_revisions(catalog_id,trade_id,revision,book_id,binding_id,instrument_id,publication_order),
 FOREIGN KEY(catalog_id,trade_id,origin_revision) REFERENCES trading.futures_revision_markets(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES settlement_data.futures_margin_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE settlement_data.futures_margin_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 kind text NOT NULL CHECK(kind IN('policy_save','adopt')),request jsonb NOT NULL CHECK(pg_column_size(request)<=4194304),
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,trade_id uuid,binding_id uuid,source_context_id uuid NOT NULL,
 authority_until timestamptz NOT NULL CHECK(isfinite(authority_until)),outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),CHECK((kind='adopt')=(trade_id IS NOT NULL AND binding_id IS NOT NULL)),
 CHECK((trade_id IS NULL)=(binding_id IS NULL)),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES settlement_data.futures_margin_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,trade_id,binding_id) REFERENCES settlement_data.futures_margin_adoptions(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
DO $tables$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['futures_margin_policies','futures_margin_policy_occurrences','futures_margin_adoptions','futures_margin_commands'] LOOP
  EXECUTE format('ALTER TABLE settlement_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE settlement_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON settlement_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON settlement_data.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON settlement_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$tables$;
CREATE FUNCTION settlement_data.futures_margin_policy_visible(catalog uuid,publication integer) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT security.market_access(catalog,'ReadMarketData') AND EXISTS(
  SELECT 1 FROM security.request_contexts q JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id)
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON k.catalog_id=t.catalog_id AND k.publication_order=publication
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
   AND q.expires_at>clock_timestamp() AND t.catalog_id=catalog AND publication<=x.known_through AND k.known_from<=t.known_at)
$function$;
CREATE POLICY scoped_read ON settlement_data.futures_margin_policies FOR SELECT TO openetrm_runtime USING(
 settlement_data.futures_margin_policy_visible(catalog_id,publication_order)
 AND security.reference_access(reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(reference_catalog_id,reference_revision_id)
 AND security.market_fact_visible(catalog_id,definition_revision_id,NULL));
CREATE POLICY scoped_read ON settlement_data.futures_margin_policy_occurrences FOR SELECT TO openetrm_runtime USING(EXISTS(
 SELECT 1 FROM settlement_data.futures_margin_policies p WHERE(p.catalog_id,p.id,p.revision)=
  (futures_margin_policy_occurrences.catalog_id,futures_margin_policy_occurrences.policy_id,futures_margin_policy_occurrences.policy_revision)));
CREATE POLICY scoped_read ON settlement_data.futures_margin_adoptions FOR SELECT TO openetrm_runtime USING(
 security.trading_access(catalog_id,'ReadTrades') AND security.trading_visible(catalog_id,publication_order,effective_at) AND EXISTS(
 SELECT 1 FROM settlement_data.futures_margin_policies p WHERE(p.catalog_id,p.id,p.revision)=
  (futures_margin_adoptions.catalog_id,futures_margin_adoptions.policy_id,futures_margin_adoptions.policy_revision)));
GRANT SELECT ON settlement_data.futures_margin_policies,settlement_data.futures_margin_policy_occurrences,settlement_data.futures_margin_adoptions TO openetrm_runtime;

-- A policy is a closed set created in one publication transaction. Neither a
-- later append nor an incomplete initial set is a valid immutable version.
CREATE FUNCTION settlement_data.futures_margin_occurrence_values(items jsonb) RETURNS jsonb
 LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $function$
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',e.id,
  'at',to_char(e.at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'dueAt',to_char(e."dueAt" AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'phase',e.phase)
  ORDER BY e.at,e.phase,e.id),'[]'::jsonb)
 FROM jsonb_to_recordset(items)e(id uuid,at timestamptz,"dueAt" timestamptz,phase text)
$function$;
CREATE FUNCTION settlement_data.futures_margin_policy_value(p settlement_data.futures_margin_policies,items jsonb) RETURNS jsonb
 LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $function$
 SELECT jsonb_build_object('definitionRevisionId',p.definition_revision_id,'contractId',p.contract_id,'settlementCurve',p.settlement_curve_code,
  'referenceCatalogId',p.reference_catalog_id,'referenceRevisionId',p.reference_revision_id,'currency',p.currency_code,'quote',p.quote_code,
  'fromAt',to_char(p.from_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'untilAt',to_char(p.until_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'opening',p.opening,'increment',trim_scale(p.rounding_increment),'tie',p.tie_rule,'stage',p.rounding_stage,
  'roundingScope',p.rounding_scope,'implementation',p.implementation,'occurrences',settlement_data.futures_margin_occurrence_values(items))
$function$;
CREATE FUNCTION settlement_data.guard_futures_margin_occurrence() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM settlement_data.futures_margin_policies p JOIN trading.publications x
  ON(x.catalog_id,x.publication_order)=(p.catalog_id,p.publication_order)
  WHERE(p.catalog_id,p.id,p.revision,p.publication_order)=(NEW.catalog_id,NEW.policy_id,NEW.policy_revision,NEW.publication_order)
  AND x.publication_xid=pg_current_xact_id()) THEN RAISE EXCEPTION 'futures_margin_policy_sealed';END IF;
 RETURN NEW;
END$function$;
CREATE TRIGGER sealed_membership BEFORE INSERT ON settlement_data.futures_margin_policy_occurrences
 FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_futures_margin_occurrence();
CREATE FUNCTION settlement_data.check_futures_margin_policy_seal() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE items jsonb;finals integer;BEGIN
 SELECT jsonb_agg(jsonb_build_object('id',o.id,'at',o.at,'dueAt',o.due_at,'phase',o.phase)),count(*) FILTER(WHERE o.phase='final')
 INTO items,finals FROM settlement_data.futures_margin_policy_occurrences o
 WHERE(o.catalog_id,o.policy_id,o.policy_revision)=(NEW.catalog_id,NEW.id,NEW.revision);
 IF coalesce(jsonb_array_length(items),0)<>NEW.occurrence_count OR finals<>1 OR
  encode(sha256(convert_to(settlement_data.futures_margin_policy_value(NEW,items)::text,'UTF8')),'hex')<>NEW.terms_hash
 THEN RAISE EXCEPTION 'futures_margin_policy_seal';END IF;
 RETURN NULL;
END$function$;
CREATE CONSTRAINT TRIGGER complete_policy AFTER INSERT ON settlement_data.futures_margin_policies DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_margin_policy_seal();

CREATE FUNCTION settlement_data.require_futures_margin_authority(catalog uuid,permission text,until_at timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE actor uuid;bound uuid;hosted text;BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 SELECT q.actor_id,q.market_context_id,q.hosted_session_digest INTO actor,bound,hosted FROM security.request_contexts q
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF until_at IS NULL OR clock_timestamp()>=until_at OR actor IS NULL OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.has_permission(permission) IS NOT TRUE
  OR(permission='AmendTrade' AND security.trading_access(catalog,'ReadTrades') IS NOT TRUE)
 THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
 IF hosted IS NOT NULL THEN PERFORM security.require_hosted_session();END IF;
 IF bound IS NOT NULL OR security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
 RETURN actor;
END$function$;
CREATE FUNCTION settlement_data.capture_futures_margin_authority(catalog uuid,permission text) RETURNS timestamptz
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE until_at timestamptz;hosted text;session_until timestamptz;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until(permission));
 IF permission='AmendTrade' THEN until_at=least(until_at,security.authority_until('ReadTrades'));END IF;
 SELECT q.hosted_session_digest INTO hosted FROM security.request_contexts q
 WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 IF hosted IS NOT NULL THEN
  SELECT s.expires_at INTO session_until FROM security.hosted_session_info(hosted)s;
  IF session_until IS NULL THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
  until_at=least(until_at,session_until);
 END IF;
 PERFORM settlement_data.require_futures_margin_authority(catalog,permission,until_at);
 RETURN until_at;
END$function$;
CREATE FUNCTION settlement_data.require_futures_margin_reference(p settlement_data.futures_margin_policies) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF security.market_access(p.catalog_id,'ReadMarketData') IS NOT TRUE OR security.reference_access(p.reference_catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(p.reference_catalog_id,p.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(p.catalog_id,p.definition_revision_id,NULL) IS NOT TRUE
 THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
END$function$;
CREATE FUNCTION settlement_data.require_futures_margin_source(catalog uuid,context uuid,as_of timestamptz,definition uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN market_data.knowledge_events e ON e.catalog_id=t.catalog_id AND e.revision_id=definition AND e.observation_id IS NULL
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods r ON(r.catalog_id,r.revision_id)=(e.catalog_id,e.revision_id)
  WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date)
   AND e.event_order<=m.known_through AND k.known_from<=t.known_at AND r.effective_from<=t.effective_on AND t.effective_on<r.effective_to)
 THEN RAISE EXCEPTION 'futures_margin_context';END IF;
END$function$;
CREATE FUNCTION settlement_data.futures_margin_utc_instant(value text) RETURNS timestamptz
 LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $function$
BEGIN
 IF value IS NULL OR value!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?(Z|[+]00:00)$'
 THEN RAISE EXCEPTION 'futures_margin_instant';END IF;
 IF value::timestamptz>='9999-12-31 00:00:00+00'::timestamptz THEN RAISE EXCEPTION 'futures_margin_instant';END IF;
 RETURN value::timestamptz;
END$function$;

-- Receipt reconciliation precedes ephemeral source/head resolution. The actor
-- lock precedes the common publication fence in both writers.
CREATE FUNCTION settlement_data.futures_margin_command_status(catalog uuid,operation uuid,command_kind text,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE actor uuid;permission text;until_at timestamptz;prior settlement_data.futures_margin_commands;p settlement_data.futures_margin_policies;BEGIN
 permission=CASE command_kind WHEN 'policy_save' THEN 'WriteMarketDefinitions' WHEN 'adopt' THEN 'AmendTrade' ELSE NULL END;
 IF permission IS NULL OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 until_at=settlement_data.capture_futures_margin_authority(catalog,permission);
 actor=settlement_data.require_futures_margin_authority(catalog,permission,until_at);
 PERFORM pg_advisory_xact_lock(hashtextextended('futures_margin:'||catalog::text||':'||operation::text,0));
 actor=settlement_data.require_futures_margin_authority(catalog,permission,until_at);
 SELECT * INTO prior FROM settlement_data.futures_margin_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF FOUND THEN
  IF(prior.actor_id,prior.kind,prior.request) IS DISTINCT FROM(actor,command_kind,body) THEN RAISE EXCEPTION 'futures_margin_command_conflict';END IF;
  SELECT * INTO STRICT p FROM settlement_data.futures_margin_policies f WHERE(f.catalog_id,f.id,f.revision)=(catalog,prior.policy_id,prior.policy_revision);
  PERFORM settlement_data.require_futures_margin_reference(p);
 END IF;
 PERFORM settlement_data.require_futures_margin_authority(catalog,permission,until_at);
 RETURN prior.outcome;
END$function$;

CREATE FUNCTION settlement_data.save_futures_margin_policy(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE operation uuid;actor uuid;prior jsonb;input jsonb;items jsonb;p settlement_data.futures_margin_policies;fact settlement_data.futures_margin_policies;
 expected integer;head integer;as_of timestamptz;context uuid;until_at timestamptz;contract market_data.contracts;curve market_data.curves;q ref_data.quote_specifications;outcome jsonb;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>4194304 THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 operation=(body->>'operationId')::uuid;
 until_at=settlement_data.capture_futures_margin_authority(catalog,'WriteMarketDefinitions');
 prior=settlement_data.futures_margin_command_status(catalog,operation,'policy_save',body);
 IF prior IS NOT NULL THEN PERFORM settlement_data.require_futures_margin_authority(catalog,'WriteMarketDefinitions',until_at);RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 actor=settlement_data.require_futures_margin_authority(catalog,'WriteMarketDefinitions',until_at);
 IF NOT(body?&ARRAY['operationId','expectedRevision','contextId','asOf','policy']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k
  WHERE k NOT IN('operationId','expectedRevision','contextId','asOf','policy')) OR jsonb_typeof(body->'policy') IS DISTINCT FROM 'object'
 THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 input=body->'policy';
 IF NOT(input?&ARRAY['id','definitionRevisionId','contractId','settlementCurve','referenceCatalogId','referenceRevisionId','currency','fromAt','untilAt',
  'opening','increment','tie','stage','roundingScope','implementation','occurrences']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(input)k
  WHERE k NOT IN('id','definitionRevisionId','contractId','settlementCurve','referenceCatalogId','referenceRevisionId','currency','fromAt','untilAt',
   'opening','increment','tie','stage','roundingScope','implementation','occurrences')) OR jsonb_typeof(input->'occurrences') IS DISTINCT FROM 'array'
 THEN RAISE EXCEPTION 'futures_margin_policy';END IF;
 expected=(body->>'expectedRevision')::integer;context=(body->>'contextId')::uuid;as_of=settlement_data.futures_margin_utc_instant(body->>'asOf');
 p.catalog_id=catalog;p.id=(input->>'id')::uuid;p.revision=expected+1;p.definition_revision_id=(input->>'definitionRevisionId')::uuid;
 p.contract_id=(input->>'contractId')::uuid;p.settlement_curve_code=input->>'settlementCurve';
 p.reference_catalog_id=(input->>'referenceCatalogId')::uuid;p.reference_revision_id=(input->>'referenceRevisionId')::uuid;p.currency_code=input->>'currency';
 p.from_at=settlement_data.futures_margin_utc_instant(input->>'fromAt');p.until_at=settlement_data.futures_margin_utc_instant(input->>'untilAt');p.opening=input->>'opening';
 p.rounding_increment=(input->>'increment')::numeric;p.tie_rule=input->>'tie';p.rounding_stage=input->>'stage';
 p.rounding_scope=input->>'roundingScope';p.implementation=input->>'implementation';p.occurrence_count=jsonb_array_length(input->'occurrences');
 p.source_context_id=context;p.actor_id=actor;
 IF expected IS NULL OR expected<0 OR as_of IS NULL OR NOT isfinite(as_of) OR as_of>='9999-12-31 00:00:00+00'::timestamptz
  OR p.id IS NULL OR p.id='00000000-0000-0000-0000-000000000000'::uuid OR p.occurrence_count NOT BETWEEN 1 AND 16384
  OR p.from_at IS NULL OR p.until_at IS NULL OR NOT isfinite(p.from_at) OR NOT isfinite(p.until_at) OR p.from_at>=p.until_at
  OR p.until_at>='9999-12-31 00:00:00+00'::timestamptz THEN RAISE EXCEPTION 'futures_margin_policy';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,p.definition_revision_id);
 SELECT * INTO contract FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.id)=(catalog,p.definition_revision_id,p.contract_id);
 SELECT * INTO curve FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,p.definition_revision_id,p.settlement_curve_code);
 SELECT * INTO q FROM ref_data.quote_specifications s WHERE(s.catalog_id,s.revision_id,s.code)=(curve.reference_catalog_id,curve.reference_revision_id,curve.quote_code);
 IF contract.kind IS DISTINCT FROM 'future' OR curve.rule IS DISTINCT FROM 'direct' OR curve.direct_role IS DISTINCT FROM 'daily_settlement'
  OR(contract.reference_catalog_id,contract.reference_revision_id) IS DISTINCT FROM(p.reference_catalog_id,p.reference_revision_id)
  OR(q.catalog_id,q.revision_id,q.currency_code) IS DISTINCT FROM(p.reference_catalog_id,p.reference_revision_id,p.currency_code)
  OR NOT EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=
    (catalog,p.definition_revision_id,p.settlement_curve_code,p.contract_id)) THEN RAISE EXCEPTION 'futures_margin_policy';END IF;
 p.quote_code=q.code;
 PERFORM settlement_data.require_futures_margin_reference(p);
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(input->'occurrences')o WHERE jsonb_typeof(o) IS DISTINCT FROM 'object' OR
  NOT(o?&ARRAY['id','at','dueAt','phase']) OR EXISTS(SELECT 1 FROM jsonb_object_keys(o)k WHERE k NOT IN('id','at','dueAt','phase')))
 THEN RAISE EXCEPTION 'futures_margin_policy';END IF;
 PERFORM settlement_data.futures_margin_utc_instant(o->>'at'),settlement_data.futures_margin_utc_instant(o->>'dueAt')
  FROM jsonb_array_elements(input->'occurrences')o;
 items=settlement_data.futures_margin_occurrence_values(input->'occurrences');
 IF EXISTS(SELECT 1 FROM jsonb_to_recordset(items)e(id uuid,at timestamptz,"dueAt" timestamptz,phase text)
  WHERE e.id IS NULL OR e.at IS NULL OR e."dueAt" IS NULL OR e.phase IS NULL OR NOT isfinite(e.at) OR NOT isfinite(e."dueAt")
   OR e.at<p.from_at OR e.at>=p.until_at OR e."dueAt"<e.at OR e."dueAt">='9999-12-31 00:00:00+00'::timestamptz
   OR(e.phase='daily' AND e.at>contract.expiry_at) OR(e.phase='final' AND e.at<contract.expiry_at))
  OR(SELECT count(*) FROM jsonb_array_elements(items)e WHERE e->>'phase'='final')<>1
 THEN RAISE EXCEPTION 'futures_margin_policy';END IF;
 p.terms_hash=encode(sha256(convert_to(settlement_data.futures_margin_policy_value(p,items)::text,'UTF8')),'hex');
 SELECT * INTO fact FROM settlement_data.futures_margin_policies s WHERE(s.catalog_id,s.id,s.from_at,s.terms_hash)=(catalog,p.id,p.from_at,p.terms_hash);
 IF fact.id IS NULL THEN
  PERFORM security.require_catalogue_curve_choice(catalog,p.definition_revision_id,p.settlement_curve_code,as_of);
  SELECT coalesce(max(s.revision),0) INTO head FROM settlement_data.futures_margin_policies s WHERE(s.catalog_id,s.id)=(catalog,p.id);
  IF head<>expected THEN RAISE EXCEPTION 'futures_margin_policy_revision';END IF;
  SELECT coalesce(max(x.publication_order),0)+1 INTO p.publication_order FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,p.publication_order,pg_current_xact_id());
  INSERT INTO settlement_data.futures_margin_policies SELECT(p).*;
  INSERT INTO settlement_data.futures_margin_policy_occurrences
   SELECT catalog,p.id,p.revision,e.id,e.at,e."dueAt",e.phase,p.publication_order
   FROM jsonb_to_recordset(items)e(id uuid,at timestamptz,"dueAt" timestamptz,phase text);
  fact=p;
 END IF;
 outcome=jsonb_build_object('operationId',operation,'policyId',fact.id,'revision',fact.revision,'publicationOrder',fact.publication_order);
 INSERT INTO settlement_data.futures_margin_commands VALUES(catalog,operation,actor,'policy_save',body,fact.id,fact.revision,NULL,NULL,context,until_at,outcome);
 PERFORM settlement_data.require_futures_margin_reference(fact);
 PERFORM settlement_data.require_futures_margin_authority(catalog,'WriteMarketDefinitions',until_at);
 RETURN outcome;
END$function$;

CREATE FUNCTION settlement_data.adopt_futures_margin(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE operation uuid;actor uuid;prior jsonb;trade uuid;binding uuid;expected integer;context uuid;as_of timestamptz;until_at timestamptz;policy uuid;policy_revision integer;
 p settlement_data.futures_margin_policies;head trading.trade_revisions;market trading.futures_revision_markets;origin trading.trade_revisions;
 fact settlement_data.futures_margin_adoptions;publication integer;outcome jsonb;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>4194304 THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 operation=(body->>'operationId')::uuid;
 until_at=settlement_data.capture_futures_margin_authority(catalog,'AmendTrade');
 prior=settlement_data.futures_margin_command_status(catalog,operation,'adopt',body);
 IF prior IS NOT NULL THEN PERFORM settlement_data.require_futures_margin_authority(catalog,'AmendTrade',until_at);RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 actor=settlement_data.require_futures_margin_authority(catalog,'AmendTrade',until_at);
 IF NOT(body?&ARRAY['operationId','tradeId','bindingId','expectedRevision','policyId','policyRevision','contextId','asOf','adoption']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','tradeId','bindingId','expectedRevision','policyId','policyRevision','contextId','asOf','adoption'))
  OR body->>'adoption' IS DISTINCT FROM 'native_execution_inception_v1' THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 trade=(body->>'tradeId')::uuid;binding=(body->>'bindingId')::uuid;expected=(body->>'expectedRevision')::integer;
 policy=(body->>'policyId')::uuid;policy_revision=(body->>'policyRevision')::integer;context=(body->>'contextId')::uuid;
 as_of=settlement_data.futures_margin_utc_instant(body->>'asOf');
 IF trade IS NULL OR binding IS NULL OR expected IS NULL OR expected<1 OR context IS NULL THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 SELECT * INTO p FROM settlement_data.futures_margin_policies s WHERE(s.catalog_id,s.id,s.revision)=(catalog,policy,policy_revision);
 IF p.id IS NULL OR as_of IS NULL OR NOT isfinite(as_of) THEN RAISE EXCEPTION 'futures_margin_policy';END IF;
 PERFORM settlement_data.require_futures_margin_reference(p);
 SELECT * INTO fact FROM settlement_data.futures_margin_adoptions a WHERE(a.catalog_id,a.trade_id,a.binding_id)=(catalog,trade,binding);
 IF fact.trade_id IS NOT NULL THEN
  IF(fact.policy_id,fact.policy_revision,fact.implementation) IS DISTINCT FROM(policy,policy_revision,body->>'adoption')
  THEN RAISE EXCEPTION 'futures_margin_adoption_conflict';END IF;
 ELSE
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,p.definition_revision_id);
 SELECT * INTO head FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) ORDER BY r.revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM expected OR head.binding_id IS DISTINCT FROM binding OR head.effective_at>as_of THEN RAISE EXCEPTION 'futures_revision_conflict';END IF;
 SELECT * INTO market FROM trading.futures_revision_markets m WHERE(m.catalog_id,m.trade_id,m.revision)=(catalog,trade,head.revision);
 IF(market.definition_revision_id,market.contract_id,market.reference_catalog_id,market.reference_revision_id) IS DISTINCT FROM
  (p.definition_revision_id,p.contract_id,p.reference_catalog_id,p.reference_revision_id) OR head.execution_at<p.from_at OR head.execution_at>=p.until_at
  OR NOT EXISTS(SELECT 1 FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code,c.quote_code)=
    (catalog,market.definition_revision_id,market.curve_code,p.quote_code)) THEN RAISE EXCEPTION 'futures_margin_policy';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON k.catalog_id=t.catalog_id AND k.publication_order=p.publication_order
  JOIN trading.knowledge_receipts h ON h.catalog_id=t.catalog_id AND h.publication_order=head.publication_order
  WHERE(t.catalog_id,t.id)=(catalog,context) AND p.publication_order<=x.known_through AND head.publication_order<=x.known_through
   AND k.known_from<=t.known_at AND h.known_from<=t.known_at) THEN RAISE EXCEPTION 'futures_margin_context';END IF;
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO settlement_data.futures_margin_adoptions VALUES(catalog,trade,binding,head.revision,head.book_id,head.instrument_id,head.publication_order,
   policy,policy_revision,body->>'adoption',context,actor,publication,head.effective_at) RETURNING * INTO fact;
 END IF;
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'bindingId',binding,'originRevision',fact.origin_revision,
  'policyId',fact.policy_id,'policyRevision',fact.policy_revision,'publicationOrder',fact.publication_order);
 INSERT INTO settlement_data.futures_margin_commands VALUES(catalog,operation,actor,'adopt',body,policy,policy_revision,trade,binding,context,until_at,outcome);
 PERFORM settlement_data.require_futures_margin_reference(p);
 PERFORM settlement_data.require_futures_margin_authority(catalog,'AmendTrade',until_at);
 RETURN outcome;
END$function$;
CREATE FUNCTION settlement_data.check_futures_margin_command_authority() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE actor uuid;p settlement_data.futures_margin_policies;BEGIN
 actor=settlement_data.require_futures_margin_authority(NEW.catalog_id,
  CASE NEW.kind WHEN 'policy_save' THEN 'WriteMarketDefinitions' ELSE 'AmendTrade' END,NEW.authority_until);
 IF actor<>NEW.actor_id THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT p FROM settlement_data.futures_margin_policies s WHERE(s.catalog_id,s.id,s.revision)=(NEW.catalog_id,NEW.policy_id,NEW.policy_revision);
 PERFORM settlement_data.require_futures_margin_reference(p);
 RETURN NULL;
END$function$;
CREATE CONSTRAINT TRIGGER commit_authority AFTER INSERT ON settlement_data.futures_margin_commands DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_margin_command_authority();

-- The deferred guard is supplemental: SQL callers control constraint timing.
-- The trusted application owns the transaction and invokes this final validator
-- after all managed work, immediately before commit admission. A replay uses its
-- fresh invocation's deadline, never the old receipt's historical grant term.
CREATE FUNCTION settlement_data.futures_margin_command_authority(catalog uuid,command_kind text) RETURNS timestamptz
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE permission text;BEGIN
 permission=CASE command_kind WHEN 'policy_save' THEN 'WriteMarketDefinitions' WHEN 'adopt' THEN 'AmendTrade' ELSE NULL END;
 IF permission IS NULL THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 RETURN settlement_data.capture_futures_margin_authority(catalog,permission);
END$function$;
CREATE FUNCTION settlement_data.finish_futures_margin_command(catalog uuid,operation uuid,command_kind text,body jsonb,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE actor uuid;permission text;r settlement_data.futures_margin_commands;p settlement_data.futures_margin_policies;BEGIN
 permission=CASE command_kind WHEN 'policy_save' THEN 'WriteMarketDefinitions' WHEN 'adopt' THEN 'AmendTrade' ELSE NULL END;
 IF permission IS NULL THEN RAISE EXCEPTION 'futures_margin_input';END IF;
 actor=settlement_data.require_futures_margin_authority(catalog,permission,until_at);
 SELECT * INTO r FROM settlement_data.futures_margin_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF r.operation_id IS NULL OR(r.actor_id,r.kind,r.request) IS DISTINCT FROM(actor,command_kind,body)
 THEN RAISE EXCEPTION 'futures_margin_command_conflict';END IF;
 SELECT * INTO STRICT p FROM settlement_data.futures_margin_policies s WHERE(s.catalog_id,s.id,s.revision)=(catalog,r.policy_id,r.policy_revision);
 PERFORM settlement_data.require_futures_margin_reference(p);
 PERFORM settlement_data.require_futures_margin_authority(catalog,permission,until_at);
END$function$;

CREATE FUNCTION settlement_data.guard_futures_margin_continuity() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE a settlement_data.futures_margin_adoptions;r trading.trade_revisions;m trading.futures_revision_markets;BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 FOR a IN SELECT * FROM settlement_data.futures_margin_adoptions s WHERE(s.catalog_id,s.trade_id)=(NEW.catalog_id,NEW.trade_id) LOOP
  IF TG_TABLE_NAME='trade_revisions' THEN
   SELECT * INTO STRICT r FROM trading.trade_revisions h WHERE(h.catalog_id,h.trade_id,h.revision)=(a.catalog_id,a.trade_id,a.origin_revision);
   IF(NEW.book_id,NEW.binding_id,NEW.instrument_id,NEW.lots,NEW.execution_price,NEW.execution_at,NEW.trade_date,
      NEW.legal_entity_id,NEW.legal_entity_revision,NEW.counterparty_id,NEW.counterparty_revision,NEW.trader_id,NEW.trader_revision)
    IS DISTINCT FROM(r.book_id,r.binding_id,r.instrument_id,r.lots,r.execution_price,r.execution_at,r.trade_date,
      r.legal_entity_id,r.legal_entity_revision,r.counterparty_id,r.counterparty_revision,r.trader_id,r.trader_revision)
   THEN RAISE EXCEPTION 'futures_margin_continuity';END IF;
  ELSE
   SELECT * INTO STRICT m FROM trading.futures_revision_markets h WHERE(h.catalog_id,h.trade_id,h.revision)=(a.catalog_id,a.trade_id,a.origin_revision);
   IF(NEW.instrument_id,NEW.definition_revision_id,NEW.curve_code,NEW.contract_id,NEW.reference_catalog_id,NEW.reference_revision_id,NEW.series_code,NEW.implementation)
    IS DISTINCT FROM(m.instrument_id,m.definition_revision_id,m.curve_code,m.contract_id,m.reference_catalog_id,m.reference_revision_id,m.series_code,m.implementation)
   THEN RAISE EXCEPTION 'futures_margin_continuity';END IF;
  END IF;
 END LOOP;
 RETURN NEW;
END$function$;
CREATE TRIGGER margin_continuity BEFORE INSERT ON trading.trade_revisions FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_futures_margin_continuity();
CREATE TRIGGER margin_continuity BEFORE INSERT ON trading.futures_revision_markets FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_futures_margin_continuity();

CREATE FUNCTION settlement_data.require_futures_margin_read(catalog uuid,context uuid,as_of timestamptz,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
BEGIN
 IF until_at IS NULL OR clock_timestamp()>=until_at OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR security.trading_context_visible(catalog,context) IS NOT TRUE OR NOT EXISTS(
   SELECT 1 FROM security.request_contexts q JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id)
   JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
   WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
    AND(q.market_catalog_id,q.market_context_id,q.trading_as_of)=(catalog,context,as_of) AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'futures_margin_denied' USING ERRCODE='42501';END IF;
END$function$;
-- This producer returns only the explicitly adopted inception profile. A later
-- settlement producer must account for its complete accepted determination
-- prefix; an old inception reader is not a fallback for a margined position.
CREATE FUNCTION settlement_data.read_futures_margin_inception(catalog uuid,trade uuid,binding uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE until_at timestamptz;cut market_data.temporal_contexts;prefix integer;
 a settlement_data.futures_margin_adoptions;p settlement_data.futures_margin_policies;origin trading.trade_revisions;head trading.trade_revisions;
 market trading.futures_revision_markets;current_market trading.futures_revision_markets;known timestamptz;result jsonb;BEGIN
 PERFORM 1 FROM security.epoch FOR SHARE;
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'));
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);
 SELECT * INTO STRICT cut FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 SELECT x.known_through INTO STRICT prefix FROM trading.contexts x WHERE(x.catalog_id,x.context_id)=(catalog,context);
 result=jsonb_build_object('available',false,'contextId',context,'knownAt',cut.known_at,'effectiveOn',cut.effective_on,'authorityUntil',until_at);
 SELECT s.* INTO a FROM settlement_data.futures_margin_adoptions s JOIN trading.knowledge_receipts k
  ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
  JOIN trading.trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(s.catalog_id,s.trade_id,s.origin_revision)
  JOIN trading.knowledge_receipts h ON(h.catalog_id,h.publication_order)=(r.catalog_id,r.publication_order)
  WHERE(s.catalog_id,s.trade_id,s.binding_id)=(catalog,trade,binding) AND s.effective_at<=as_of AND r.effective_at<=as_of
   AND s.publication_order<=prefix AND r.publication_order<=prefix AND k.known_from<=cut.known_at AND h.known_from<=cut.known_at;
 IF a.trade_id IS NOT NULL THEN
  SELECT * INTO STRICT origin FROM trading.trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision)=(catalog,trade,a.origin_revision);
  SELECT r.* INTO STRICT head FROM trading.trade_revisions r JOIN trading.knowledge_receipts k
   ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
   WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND r.effective_at<=as_of AND r.publication_order<=prefix AND k.known_from<=cut.known_at
   ORDER BY r.revision DESC LIMIT 1;
  SELECT * INTO STRICT market FROM trading.futures_revision_markets m WHERE(m.catalog_id,m.trade_id,m.revision)=(catalog,trade,a.origin_revision);
  SELECT * INTO STRICT current_market FROM trading.futures_revision_markets m WHERE(m.catalog_id,m.trade_id,m.revision)=(catalog,trade,head.revision);
  IF(head.book_id,head.binding_id,head.instrument_id,head.lots,head.execution_price,head.execution_at,head.trade_date,
      head.legal_entity_id,head.legal_entity_revision,head.counterparty_id,head.counterparty_revision,head.trader_id,head.trader_revision)
   IS DISTINCT FROM(origin.book_id,origin.binding_id,origin.instrument_id,origin.lots,origin.execution_price,origin.execution_at,origin.trade_date,
      origin.legal_entity_id,origin.legal_entity_revision,origin.counterparty_id,origin.counterparty_revision,origin.trader_id,origin.trader_revision)
   OR(current_market.definition_revision_id,current_market.curve_code,current_market.contract_id,current_market.reference_catalog_id,
       current_market.reference_revision_id,current_market.series_code,current_market.implementation)
    IS DISTINCT FROM(market.definition_revision_id,market.curve_code,market.contract_id,market.reference_catalog_id,
       market.reference_revision_id,market.series_code,market.implementation)
  THEN RAISE EXCEPTION 'futures_margin_continuity';END IF;
  SELECT s.* INTO p FROM settlement_data.futures_margin_policies s JOIN trading.knowledge_receipts k
   ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
   WHERE(s.catalog_id,s.id,s.revision)=(catalog,a.policy_id,a.policy_revision) AND s.publication_order<=prefix AND k.known_from<=cut.known_at;
  IF p.id IS NULL THEN RAISE EXCEPTION 'futures_margin_context';END IF;
  PERFORM settlement_data.require_futures_margin_reference(p);
  SELECT k.known_from INTO STRICT known FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,a.publication_order);
  result=result||jsonb_build_object('available',true,'stateProfile',a.implementation,'adoption',to_jsonb(a),'knownFrom',known,
   'origin',to_jsonb(origin),'head',to_jsonb(head),'market',to_jsonb(market),'policy',to_jsonb(p),
   'occurrences',(SELECT jsonb_agg(to_jsonb(o) ORDER BY o.at,o.phase,o.id) FROM settlement_data.futures_margin_policy_occurrences o
    WHERE(o.catalog_id,o.policy_id,o.policy_revision)=(catalog,p.id,p.revision)));
 END IF;
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);
 RETURN result;
END$function$;

DO $functions$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='settlement_data' AND p.proname IN('futures_margin_occurrence_values','futures_margin_policy_value','guard_futures_margin_occurrence',
   'check_futures_margin_policy_seal','require_futures_margin_authority','require_futures_margin_reference','require_futures_margin_source',
   'futures_margin_command_status','save_futures_margin_policy','adopt_futures_margin','guard_futures_margin_continuity',
   'futures_margin_policy_visible','futures_margin_utc_instant','require_futures_margin_read','read_futures_margin_inception',
   'capture_futures_margin_authority','check_futures_margin_command_authority','futures_margin_command_authority','finish_futures_margin_command') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$functions$;
GRANT EXECUTE ON FUNCTION settlement_data.futures_margin_command_status(uuid,uuid,text,jsonb),
 settlement_data.save_futures_margin_policy(uuid,jsonb),settlement_data.adopt_futures_margin(uuid,jsonb),
 settlement_data.futures_margin_policy_visible(uuid,integer),settlement_data.require_futures_margin_read(uuid,uuid,timestamptz,timestamptz),
 settlement_data.futures_margin_command_authority(uuid,text),settlement_data.finish_futures_margin_command(uuid,uuid,text,jsonb,timestamptz),
 settlement_data.read_futures_margin_inception(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
