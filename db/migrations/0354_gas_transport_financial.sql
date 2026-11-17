-- Whole-package financial Transport, independent from operating plans and cash.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- Prompt identity only; no assistant financial operation is installed.
DO $pages$DECLARE definition text;marker text:='''blotter_new_park_loan''';BEGIN
 SELECT pg_get_functiondef(p.oid) INTO STRICT definition FROM pg_proc p WHERE p.oid='assistant.is_prompt_page(text)'::regprocedure
  AND md5(p.prosrc)='c75b3c2c10fe39bf2f1150d598ae7f24' AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
  AND NOT p.prosecdef AND p.provolatile='i' AND p.prokind='f' AND p.proparallel='u' AND NOT p.proisstrict AND NOT p.proleakproof
  AND p.provariadic=0 AND p.proargmodes IS NULL AND p.proargnames=ARRAY['page'] AND p.prorettype='boolean'::regtype
  AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='sql') AND p.proconfig=ARRAY['search_path=pg_catalog']
  AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[];
 IF(length(definition)-length(replace(definition,marker,'')))/length(marker)<>1 OR definition LIKE '%''blotter_new_transport''%'
 THEN RAISE EXCEPTION 'gas_transport_financial_upgrade: unexpected helper assistant.is_prompt_page(text)';END IF;
 EXECUTE replace(definition,marker,marker||',''blotter_new_transport''');
EXCEPTION WHEN no_data_found THEN RAISE EXCEPTION 'gas_transport_financial_upgrade: unexpected helper assistant.is_prompt_page(text)';END$pages$;

INSERT INTO lifecycle.action_kinds(code,automatic,configuration_permission,execution_permission)
 VALUES('gas.transport_financial.expire',false,'ConfigureGasServices','AmendTrade');

CREATE TABLE trading.gas_transport_financial_instruments(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 valuation_reference_catalog_id uuid NOT NULL,valuation_reference_revision_id uuid NOT NULL,
 package_quantity_code ref_data.code NOT NULL,currency_code ref_data.code NOT NULL,
 profile text NOT NULL CHECK(profile='physical_gas_transport_direct_rights_v1'),
 perspective text NOT NULL CHECK(perspective IN('customer','provider')),
 rights_valuation_policy text NOT NULL CHECK(rights_valuation_policy='signed_current_whole_remaining_rights_pv_excluding_fees_and_owned_gas_v1'),
 fee_profile text NOT NULL CHECK(fee_profile='unconditional_one_off_fixed_amount_at_booking_v1'),
 fixed_fee_amount numeric NOT NULL CHECK(fixed_fee_amount>=0 AND fixed_fee_amount<1e20 AND scale(fixed_fee_amount)<=18),
 fee_direction text NOT NULL CHECK(fee_direction IN('pay','receive')),fee_due_at ref_data.instant,
 expiry_policy text NOT NULL CHECK(expiry_policy='fixed_window_capacity_rights_expire_at_through_v1'),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),
 CHECK((fixed_fee_amount=0)=(fee_due_at IS NULL)),CHECK(fee_due_at IS NULL OR fee_due_at>='1901-01-01Z' AND fee_due_at<'2200-01-01Z'),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,valuation_reference_catalog_id,valuation_reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(valuation_reference_catalog_id,valuation_reference_revision_id,package_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(valuation_reference_catalog_id,valuation_reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_financial_trades(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,instrument_id uuid NOT NULL,business_key text NOT NULL,book_id uuid NOT NULL,
 entitlement_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id),UNIQUE(catalog_id,instrument_id),UNIQUE(catalog_id,entitlement_id),UNIQUE(catalog_id,operation_id),
 UNIQUE(catalog_id,book_id,business_key),UNIQUE(catalog_id,trade_id,entitlement_id),
 CHECK(business_key=trading.gas_transport_capacity_key(business_key)),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_transport_financial_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,entitlement_id) REFERENCES trading.gas_transport_capacity_entitlements(catalog_id,id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_financial_states(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 entitlement_id uuid NOT NULL,capacity_version_id uuid NOT NULL,status text NOT NULL CHECK(status IN('live','closed')),
 policy_id uuid,policy_revision integer,
 evidence text NOT NULL CHECK(evidence=trading.gas_transport_capacity_evidence(evidence)),
 source_context_id uuid NOT NULL,source_as_of ref_data.instant NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,id),UNIQUE(catalog_id,trade_id,revision),UNIQUE(catalog_id,trade_id,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),CHECK((status='closed')=(policy_id IS NOT NULL)),CHECK((policy_id IS NULL)=(policy_revision IS NULL)),
 CHECK(status<>'closed' OR previous_id IS NOT NULL),
 FOREIGN KEY(catalog_id,trade_id,entitlement_id) REFERENCES trading.gas_transport_financial_trades(catalog_id,trade_id,entitlement_id),
 FOREIGN KEY(catalog_id,trade_id,previous_id) REFERENCES trading.gas_transport_financial_states(catalog_id,trade_id,id),
 FOREIGN KEY(catalog_id,entitlement_id,capacity_version_id) REFERENCES trading.gas_transport_capacity_versions(catalog_id,entitlement_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_financial_marks(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,id uuid NOT NULL,financial_state_id uuid NOT NULL,valuation_at ref_data.instant NOT NULL,
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,contract_id uuid NOT NULL,series_id uuid NOT NULL,observation_id uuid NOT NULL,
 valuation_reference_catalog_id uuid NOT NULL,valuation_reference_revision_id uuid NOT NULL,source_code ref_data.code NOT NULL,quote_code ref_data.code NOT NULL,
 evidence text NOT NULL CHECK(evidence=trading.gas_transport_capacity_evidence(evidence)),
 source_context_id uuid NOT NULL,source_as_of ref_data.instant NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,financial_state_id,valuation_at,id),
 UNIQUE(catalog_id,financial_state_id,valuation_at,revision),UNIQUE(catalog_id,financial_state_id,valuation_at,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,trade_id,financial_state_id) REFERENCES trading.gas_transport_financial_states(catalog_id,trade_id,id),
 FOREIGN KEY(catalog_id,trade_id,financial_state_id,valuation_at,previous_id)
  REFERENCES trading.gas_transport_financial_marks(catalog_id,trade_id,financial_state_id,valuation_at,id),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,definition_revision_id,series_id) REFERENCES market_data.series(catalog_id,revision_id,id),
 FOREIGN KEY(catalog_id,observation_id) REFERENCES market_data.observations(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,valuation_reference_catalog_id,valuation_reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(valuation_reference_catalog_id,valuation_reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(valuation_reference_catalog_id,valuation_reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_financial_cash_members(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,instrument_id uuid NOT NULL,
 amount numeric NOT NULL CHECK(amount<>0 AND abs(amount)<1e20 AND scale(amount)<=18),
 PRIMARY KEY(catalog_id,trade_id),UNIQUE(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_transport_financial_trades(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,trade_id,binding_id) REFERENCES trading.cash_payment_memberships(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.cash_payment_instruments(catalog_id,instrument_id)
);
CREATE TABLE trading.gas_transport_financial_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,kind text NOT NULL CHECK(kind IN('book','adopt_capacity_version','adopt_rights_mark','expire')),
 trade_id uuid NOT NULL,instrument_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 request jsonb NOT NULL,fact jsonb NOT NULL,outcome jsonb NOT NULL,source_context_id uuid,authority_until ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_transport_financial_trades(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_transport_financial_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_transport_financial_instruments','gas_transport_financial_trades','gas_transport_financial_states',
  'gas_transport_financial_marks','gas_transport_financial_cash_members','gas_transport_financial_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;

CREATE FUNCTION trading.gas_transport_financial_body(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=body;key text;keys text[];kind text=body->>'kind';BEGIN
 IF body?'profile' THEN
  keys=ARRAY['operationId','snapshotId','contextId','definitionRevisionId','knowledgeMode','profile','businessKey','bookId','legalEntityId','legalEntityRevision',
   'counterpartyId','counterpartyRevision','valuationReferenceCatalogId','valuationReferenceRevisionId','entitlementId','capacityVersionId','perspective','packageQuantity','currency',
   'rightsValuationPolicy','feeProfile','fixedFeeAmount','feeDirection','feeDueAt','expiryPolicy','evidence'];
 ELSIF kind='adopt_capacity_version' THEN keys=ARRAY['kind','tradeId','expectedFinancialStateId','capacityVersionId','evidence'];
 ELSIF kind='adopt_rights_mark' THEN keys=ARRAY['kind','tradeId','financialStateId','expectedPreviousMarkId','valuationAt','marketDefinitionRevisionId','curveCode','contractId','seriesId','observationId','source','quote','evidence'];
 ELSIF kind='expire' THEN keys=ARRAY['kind','tradeId','expectedFinancialStateId','policyId','policyRevision','evidence'];
 ELSE RAISE EXCEPTION 'gas_transport_financial_input';END IF;
 PERFORM trading.gas_transport_capacity_keys(body,keys);
 FOREACH key IN ARRAY keys LOOP
  IF key IN('legalEntityRevision','counterpartyRevision','policyRevision') THEN
   IF jsonb_typeof(body->key) IS DISTINCT FROM 'number' OR(body->>key)!~'^[1-9][0-9]*$' THEN RAISE EXCEPTION 'gas_transport_financial_input';END IF;
   result=jsonb_set(result,ARRAY[key],to_jsonb((body->>key)::integer));
  ELSIF body->key='null'::jsonb AND key IN('expectedPreviousMarkId','feeDueAt') THEN NULL;
  ELSE
   IF jsonb_typeof(body->key) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_transport_financial_input';END IF;
   IF key LIKE '%Id' THEN
    IF(body->>key)::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'gas_transport_financial_input';END IF;
    result=jsonb_set(result,ARRAY[key],to_jsonb((body->>key)::uuid));
   ELSIF key IN('valuationAt','feeDueAt') THEN result=jsonb_set(result,ARRAY[key],to_jsonb(trading.gas_transport_capacity_instant(trading.gas_transport_capacity_at(body->>key))));
   ELSIF key='fixedFeeAmount' THEN result=jsonb_set(result,ARRAY[key],to_jsonb(trading.gas_transport_capacity_quantity(body->>key)::text));
   ELSIF key='businessKey' THEN result=jsonb_set(result,ARRAY[key],to_jsonb(trading.gas_transport_capacity_key(body->>key)));
   ELSIF key='evidence' THEN result=jsonb_set(result,ARRAY[key],to_jsonb(trading.gas_transport_capacity_evidence(body->>key)));
   END IF;
  END IF;
 END LOOP;
 RETURN result;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR raise_exception THEN RAISE EXCEPTION 'gas_transport_financial_input';END$$;
CREATE FUNCTION trading.gas_transport_financial_fact(body jsonb,effective timestamptz) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('body',body-ARRAY['operationId','snapshotId','contextId','expectedFinancialStateId','expectedPreviousMarkId'],
  'effectiveAt',trading.gas_transport_capacity_instant(effective))
$$;

CREATE FUNCTION security.gas_transport_financial_authority(catalog uuid,kind text) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;candidate timestamptz;permission text;permissions text[]=ARRAY['ReadMarketData','ReadTrades'];BEGIN
 IF kind IS NOT NULL AND kind NOT IN('book','adopt_capacity_version','adopt_rights_mark','expire') THEN RAISE EXCEPTION 'gas_transport_financial_input';END IF;
 IF kind='book' THEN permissions=permissions||ARRAY['BookTrade','ReadGasOperations'];
 ELSIF kind IS NOT NULL THEN permissions=permissions||ARRAY['AmendTrade'];
 END IF;
 IF kind='adopt_capacity_version' THEN permissions=permissions||ARRAY['ReadGasOperations'];END IF;
 -- Capture every required bound before a scope access can wait; NULL must not
 -- disappear through PostgreSQL least() when an entry grant is missing.
 FOREACH permission IN ARRAY permissions LOOP
  candidate=security.authority_until(permission);
  IF candidate IS NULL OR clock_timestamp()>=candidate THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
  deadline=least(deadline,candidate);
 END LOOP;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
  OR kind='book' AND security.trading_access(catalog,'BookTrade') IS NOT TRUE
  OR kind IS NOT NULL AND kind<>'book' AND security.trading_access(catalog,'AmendTrade') IS NOT TRUE
  OR kind IN('book','adopt_capacity_version') AND security.trading_access(catalog,'ReadGasOperations') IS NOT TRUE
  OR kind IS NOT NULL AND pg_has_role(session_user,'openetrm_risk_worker','MEMBER')
  OR clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
 RETURN deadline;
END$$;
CREATE FUNCTION trading.require_gas_transport_financial_reference(catalog uuid,trade uuid) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE t trading.gas_transport_financial_trades;i trading.gas_transport_financial_instruments;e trading.gas_transport_capacity_entitlements;BEGIN
 SELECT * INTO STRICT t FROM trading.gas_transport_financial_trades x WHERE(x.catalog_id,x.trade_id)=(catalog,trade);
 SELECT * INTO STRICT i FROM trading.gas_transport_financial_instruments x WHERE(x.catalog_id,x.instrument_id)=(catalog,t.instrument_id);
 SELECT * INTO STRICT e FROM trading.gas_transport_capacity_entitlements x WHERE(x.catalog_id,x.id)=(catalog,t.entitlement_id);
 IF security.reference_access(e.reference_catalog_id,'ReadMarketData') IS NOT TRUE OR security.reference_access(i.valuation_reference_catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(e.reference_catalog_id,e.reference_revision_id) IS NOT TRUE
  OR security.reference_version_visible(i.valuation_reference_catalog_id,i.valuation_reference_revision_id) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.require_gas_transport_financial_commit(catalog uuid,trade uuid,kind text,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND(kind IS NOT NULL OR trade IS NOT NULL)
 THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
 PERFORM security.gas_transport_financial_authority(catalog,kind);
 IF trade IS NOT NULL THEN PERFORM trading.require_gas_transport_financial_reference(catalog,trade);END IF;
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
END$$;

-- Shared lifecycle recovery needs the accepted event instant before consulting
-- a fresh (possibly already closed) source. This is an exact expiry lookup,
-- never a producer, a current-head selection or a replacement policy binding.
CREATE FUNCTION security.gas_transport_financial_accepted_expiry(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;target uuid;candidate trading.gas_transport_financial_commands;
 accepted trading.gas_transport_financial_commands;matches integer=0;BEGIN
 body=trading.gas_transport_financial_body(body);
 IF body->>'kind' IS DISTINCT FROM 'expire' THEN RAISE EXCEPTION 'gas_transport_financial_input';END IF;
 deadline=security.gas_transport_financial_authority(catalog,'expire');target=(body->>'tradeId')::uuid;
 IF NOT EXISTS(SELECT 1 FROM trading.gas_transport_financial_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,target))
 THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
 PERFORM security.require_gas_transport_financial_commit(catalog,target,'expire',deadline);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_transport_financial_commit(catalog,target,'expire',deadline);
 FOR candidate IN SELECT c.* FROM trading.gas_transport_financial_commands c
  JOIN trading.gas_transport_financial_trades t ON(t.catalog_id,t.trade_id,t.instrument_id)=(c.catalog_id,c.trade_id,c.instrument_id)
  JOIN trading.gas_transport_financial_states h ON(h.catalog_id,h.id,h.trade_id)=(c.catalog_id,c.operation_id,c.trade_id)
  WHERE c.catalog_id=catalog AND c.kind='expire' AND c.trade_id=target AND c.request->'body'=body
   AND c.outcome->'changed'='true'::jsonb AND h.status='closed'
   AND(h.previous_id,h.policy_id,h.policy_revision,h.evidence)=
    ((body->>'expectedFinancialStateId')::uuid,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,body->>'evidence')
   AND c.request->>'effectiveAt'=trading.gas_transport_capacity_instant(h.effective_at)
   AND c.fact=trading.gas_transport_financial_fact(body,h.effective_at)
   AND c.outcome=jsonb_build_object('tradeId',target,'instrumentId',c.instrument_id,'kind','expire','changed',true,'financialStateId',h.id,'revision',h.revision)
 LOOP
  matches=matches+1;
  IF matches>1 THEN RAISE EXCEPTION 'gas_transport_financial_conflict';END IF;
  accepted=candidate;
 END LOOP;
 PERFORM security.require_gas_transport_financial_commit(catalog,target,'expire',deadline);
 IF matches=0 THEN RETURN jsonb_build_object('found',false,'authorityUntil',deadline);END IF;
 RETURN jsonb_build_object('found',true,'effectiveAt',accepted.request->'effectiveAt','outcome',accepted.outcome,'authorityUntil',deadline);
END$$;

CREATE FUNCTION trading.gas_transport_financial_known(catalog uuid,publication integer,context uuid) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT trading.gas_owned_visible(catalog,publication,TIMESTAMPTZ '0001-01-01Z',context,(SELECT effective_on::timestamp AT TIME ZONE 'UTC' FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context)))
$$;
CREATE FUNCTION trading.gas_transport_financial_market_known(catalog uuid,definition uuid,observation uuid,context uuid) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  JOIN market_data.knowledge_events e ON e.catalog_id=t.catalog_id AND e.revision_id=definition AND e.observation_id IS NOT DISTINCT FROM observation
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND e.event_order<=m.known_through AND k.known_from<=t.known_at
   AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to)
$$;

CREATE FUNCTION trading.require_gas_transport_financial_package(reference_catalog uuid,reference_revision uuid,quantity text) RETURNS void LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications q JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code,u.dimension_code)=(q.catalog_id,q.revision_id,q.unit_code,q.dimension_code)
  JOIN ref_data.dimensions d ON(d.catalog_id,d.revision_id,d.code)=(u.catalog_id,u.revision_id,u.dimension_code)
  WHERE(q.catalog_id,q.revision_id,q.code)=(reference_catalog,reference_revision,quantity)
   AND(d.mass_power,d.length_power,d.time_power,d.temperature_power,d.amount_power,d.current_power,d.luminous_power)=(0,0,0,0,0,0,0))
 THEN RAISE EXCEPTION 'gas_transport_financial_unit';END IF;
END$$;
CREATE FUNCTION trading.require_gas_transport_financial_mark(catalog uuid,body jsonb,context uuid,as_of timestamptz,terms trading.gas_transport_financial_instruments) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE o market_data.observations;c market_data.curves;s market_data.series;q ref_data.quote_specifications;BEGIN
 PERFORM trading.require_gas_transport_financial_package(terms.valuation_reference_catalog_id,terms.valuation_reference_revision_id,terms.package_quantity_code);
 SELECT * INTO o FROM market_data.observations x WHERE(x.catalog_id,x.id)=(catalog,(body->>'observationId')::uuid);
 SELECT * INTO c FROM market_data.curves x WHERE(x.catalog_id,x.revision_id,x.code)=(catalog,(body->>'marketDefinitionRevisionId')::uuid,body->>'curveCode');
 SELECT * INTO s FROM market_data.series x WHERE(x.catalog_id,x.revision_id,x.id)=(catalog,c.revision_id,(body->>'seriesId')::uuid);
 SELECT * INTO q FROM ref_data.quote_specifications x WHERE(x.catalog_id,x.revision_id,x.code)=(terms.valuation_reference_catalog_id,terms.valuation_reference_revision_id,body->>'quote');
 IF o.id IS NULL OR c.code IS NULL OR s.id IS NULL OR q.code IS NULL
  OR c.revision_id IS DISTINCT FROM terms.definition_revision_id
  OR(o.definition_revision_id,o.series_id,o.reference_catalog_id,o.reference_revision_id,o.source_code,o.role,o.publication_state,o.observed_at)
   IS DISTINCT FROM(c.revision_id,s.id,terms.valuation_reference_catalog_id,terms.valuation_reference_revision_id,body->>'source','trader_mark','final',as_of)
  OR(c.reference_catalog_id,c.reference_revision_id,c.rule,c.direct_source_code,c.direct_role,c.quote_code)
   IS DISTINCT FROM(terms.valuation_reference_catalog_id,terms.valuation_reference_revision_id,'direct',o.source_code,'trader_mark',q.code)
  OR(s.contract_id,s.quote_code) IS DISTINCT FROM((body->>'contractId')::uuid,q.code)
  OR(q.kind,q.quantity_code,q.currency_code,q.multiplier) IS DISTINCT FROM('outright',terms.package_quantity_code,terms.currency_code,1::numeric)
  OR trading.gas_transport_capacity_at(body->>'valuationAt')<>as_of
  OR NOT EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(catalog,c.revision_id,c.code,s.contract_id))
  OR NOT EXISTS(SELECT 1 FROM market_data.contracts k WHERE(k.catalog_id,k.revision_id,k.id)=(catalog,c.revision_id,s.contract_id) AND k.expiry_at>as_of)
  OR NOT trading.gas_transport_financial_market_known(catalog,c.revision_id,o.id,context)
  OR NOT trading.gas_transport_financial_market_known(catalog,c.revision_id,NULL,context)
  OR EXISTS(WITH RECURSIVE corrections AS(SELECT id FROM market_data.observations x WHERE(x.catalog_id,x.corrects_id)=(catalog,o.id)
   UNION ALL SELECT x.id FROM market_data.observations x JOIN corrections p ON x.corrects_id=p.id WHERE x.catalog_id=catalog)
   SELECT 1 FROM corrections)
 THEN RAISE EXCEPTION 'gas_transport_financial_mark';END IF;
END$$;

CREATE FUNCTION trading.require_gas_transport_financial_source(catalog uuid,body jsonb,context uuid,effective timestamptz,as_of timestamptz,excluding uuid DEFAULT NULL) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE kind text=CASE WHEN body?'profile' THEN 'book' ELSE body->>'kind' END;t trading.gas_transport_financial_trades;
 i trading.gas_transport_financial_instruments;e trading.gas_transport_capacity_entitlements;v trading.gas_transport_capacity_versions;
 h trading.gas_transport_financial_states;mark trading.gas_transport_financial_marks;selected uuid;bound uuid;known timestamptz;business_day date;BEGIN
 IF security.current_write_context() IS NOT TRUE OR context IS NULL OR effective IS NULL OR as_of IS NULL OR effective>as_of
  OR NOT isfinite(as_of)
 THEN RAISE EXCEPTION 'gas_transport_financial_context';END IF;
 SELECT x.known_at,x.effective_on INTO known,business_day FROM market_data.temporal_contexts x
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(x.catalog_id,x.id)
  JOIN trading.contexts tc ON(tc.catalog_id,tc.context_id)=(x.catalog_id,x.id) WHERE(x.catalog_id,x.id)=(catalog,context);
 IF NOT FOUND THEN RAISE EXCEPTION 'gas_transport_financial_context';END IF;
 IF effective>known OR as_of>known THEN RAISE EXCEPTION 'gas_transport_financial_context';END IF;
 IF kind='adopt_rights_mark' AND effective<>as_of THEN RAISE EXCEPTION 'gas_transport_financial_mark_time';END IF;
 IF excluding IS NOT NULL AND EXISTS(SELECT 1 FROM(
  SELECT publication_order FROM trading.gas_transport_financial_states x WHERE(x.catalog_id,x.id)=(catalog,excluding)
  UNION ALL SELECT publication_order FROM trading.gas_transport_financial_marks x WHERE(x.catalog_id,x.id)=(catalog,excluding))x
  WHERE trading.gas_transport_financial_known(catalog,x.publication_order,context)) THEN RAISE EXCEPTION 'gas_transport_financial_context';END IF;
 BEGIN
  SELECT market_context_id INTO bound FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
  IF bound IS NULL THEN PERFORM security.use_market_context(catalog,context);PERFORM security.use_trading_context(catalog,as_of,business_day);
  ELSIF bound<>context OR NOT EXISTS(SELECT 1 FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user
   AND market_catalog_id=catalog AND trading_as_of=as_of AND trading_business_date=business_day)
   THEN RAISE EXCEPTION 'gas_transport_financial_context';END IF;
  IF NOT security.trading_context_day_matches(catalog,context,as_of) THEN RAISE EXCEPTION 'gas_transport_financial_context';END IF;
  IF kind='book' THEN
   SELECT * INTO e FROM trading.gas_transport_capacity_entitlements x WHERE(x.catalog_id,x.id)=(catalog,(body->>'entitlementId')::uuid);
   i.valuation_reference_catalog_id=(body->>'valuationReferenceCatalogId')::uuid;i.valuation_reference_revision_id=(body->>'valuationReferenceRevisionId')::uuid;
   i.definition_revision_id=(body->>'definitionRevisionId')::uuid;i.package_quantity_code=body->>'packageQuantity';i.currency_code=body->>'currency';
   IF e.id IS NULL OR(e.book_id,e.legal_entity_id,e.legal_entity_revision,e.counterparty_id,e.counterparty_revision) IS DISTINCT FROM
    ((body->>'bookId')::uuid,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer)
    OR NOT security.trading_visible(catalog,e.publication_order,e.effective_at) OR as_of>=e.through_at
   THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
   selected=(body->>'capacityVersionId')::uuid;
  ELSE
   SELECT * INTO t FROM trading.gas_transport_financial_trades x WHERE(x.catalog_id,x.trade_id)=(catalog,(body->>'tradeId')::uuid);
   SELECT * INTO i FROM trading.gas_transport_financial_instruments x WHERE(x.catalog_id,x.instrument_id)=(catalog,t.instrument_id);
   SELECT * INTO e FROM trading.gas_transport_capacity_entitlements x WHERE(x.catalog_id,x.id)=(catalog,t.entitlement_id);
   IF t.trade_id IS NULL OR NOT security.trading_visible(catalog,t.publication_order,t.effective_at) THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
   SELECT * INTO h FROM trading.gas_transport_financial_states x WHERE(x.catalog_id,x.trade_id)=(catalog,t.trade_id) AND x.id IS DISTINCT FROM excluding ORDER BY revision DESC LIMIT 1;
   IF h.id IS NULL OR NOT trading.gas_transport_financial_known(catalog,h.publication_order,context)
    OR h.id IS DISTINCT FROM coalesce((body->>'expectedFinancialStateId')::uuid,(body->>'financialStateId')::uuid)
   THEN RAISE EXCEPTION 'gas_transport_financial_stale';END IF;
   IF h.effective_at>as_of THEN RAISE EXCEPTION 'gas_transport_financial_future_state';END IF;
   IF h.status='closed' THEN RAISE EXCEPTION 'gas_transport_financial_closed';END IF;
   IF kind='adopt_capacity_version' AND effective<h.effective_at THEN RAISE EXCEPTION 'gas_transport_financial_state_time';END IF;
   IF kind='expire' THEN
    IF effective<>e.through_at OR as_of<e.through_at THEN RAISE EXCEPTION 'gas_transport_financial_expiry';END IF;
    PERFORM lifecycle.require_policy(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,'gas.transport_financial.expire',as_of,false);
   ELSIF as_of>=e.through_at THEN RAISE EXCEPTION 'gas_transport_financial_expiry_required';END IF;
   selected=CASE WHEN kind='adopt_capacity_version' THEN(body->>'capacityVersionId')::uuid ELSE h.capacity_version_id END;
   IF kind='adopt_capacity_version' AND selected=h.capacity_version_id THEN RAISE EXCEPTION 'gas_transport_financial_state';END IF;
  END IF;
  IF security.reference_access(e.reference_catalog_id,'ReadMarketData') IS NOT TRUE OR security.reference_access(i.valuation_reference_catalog_id,'ReadMarketData') IS NOT TRUE
   OR security.reference_version_visible(e.reference_catalog_id,e.reference_revision_id) IS NOT TRUE
   OR security.reference_version_visible(i.valuation_reference_catalog_id,i.valuation_reference_revision_id) IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
  PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,i.definition_revision_id);
  PERFORM trading.require_gas_transport_financial_package(i.valuation_reference_catalog_id,i.valuation_reference_revision_id,i.package_quantity_code);
  IF NOT EXISTS(SELECT 1 FROM market_data.revisions r WHERE(r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id)=
   (catalog,i.definition_revision_id,i.valuation_reference_catalog_id,i.valuation_reference_revision_id))
   OR kind='book' AND NOT EXISTS(SELECT 1 FROM market_data.curves c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code)
    JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=(m.catalog_id,m.revision_id,m.contract_id,c.quote_code)
    JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id)
    JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
    WHERE(c.catalog_id,c.revision_id,c.reference_catalog_id,c.reference_revision_id)=(catalog,i.definition_revision_id,i.valuation_reference_catalog_id,i.valuation_reference_revision_id)
     AND c.rule='direct' AND c.direct_role='trader_mark' AND k.expiry_at>as_of AND q.kind='outright' AND(q.quantity_code,q.currency_code,q.multiplier)=(i.package_quantity_code,i.currency_code,1::numeric))
  THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
    WHERE(b.catalog_id,b.id)=(catalog,e.book_id) AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
   OR NOT EXISTS(SELECT 1 FROM trading.legal_entities p WHERE(p.catalog_id,p.id,p.revision)=(catalog,e.legal_entity_id,e.legal_entity_revision) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
   OR NOT EXISTS(SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,e.counterparty_id,e.counterparty_revision) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
  THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
  IF kind IN('book','adopt_capacity_version') THEN
   SELECT * INTO v FROM trading.gas_transport_capacity_versions x WHERE(x.catalog_id,x.entitlement_id)=(catalog,e.id) ORDER BY revision DESC LIMIT 1;
   IF v.id IS DISTINCT FROM selected OR NOT trading.gas_transport_financial_known(catalog,v.publication_order,context) THEN RAISE EXCEPTION 'gas_transport_financial_stale';END IF;
   IF v.effective_at>as_of THEN RAISE EXCEPTION 'gas_transport_financial_future_state';END IF;
   IF kind='adopt_capacity_version' AND effective<v.effective_at THEN RAISE EXCEPTION 'gas_transport_financial_state_time';END IF;
  ELSIF kind='adopt_rights_mark' THEN
   PERFORM trading.require_gas_transport_financial_mark(catalog,body,context,as_of,i);
   SELECT * INTO mark FROM trading.gas_transport_financial_marks x WHERE(x.catalog_id,x.financial_state_id,x.valuation_at)=(catalog,h.id,as_of)
    AND x.id IS DISTINCT FROM excluding ORDER BY revision DESC LIMIT 1;
   IF mark.id IS DISTINCT FROM(body->>'expectedPreviousMarkId')::uuid OR mark.id IS NOT NULL AND NOT trading.gas_transport_financial_known(catalog,mark.publication_order,context)
   THEN RAISE EXCEPTION 'gas_transport_financial_stale';END IF;
  END IF;
  RAISE EXCEPTION USING ERRCODE='PZ344',MESSAGE='Financial Transport source certified';
 EXCEPTION WHEN SQLSTATE 'PZ344' THEN NULL;END;
END$$;

CREATE FUNCTION security.gas_transport_financial_owner(catalog uuid) RETURNS text LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.gas_transport_financial_authority(catalog,NULL);
 RETURN presentation.preference_owner(security.presentation_actor(),'gas_transport_financial:'||catalog::text);
END$$;
CREATE FUNCTION security.gas_transport_financial_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;book_until timestamptz;amend_until timestamptz;gas_until timestamptz;selected timestamptz;
 actor uuid;prior trading.gas_transport_financial_commands;BEGIN
 book_until=security.authority_until('BookTrade');amend_until=security.authority_until('AmendTrade');gas_until=security.authority_until('ReadGasOperations');
 deadline=security.gas_transport_financial_authority(catalog,NULL);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.gas_transport_financial_commands x WHERE(x.catalog_id,x.operation_id)=(catalog,operation);
 IF prior.operation_id IS NOT NULL THEN
  IF prior.actor_id<>actor THEN RAISE EXCEPTION 'gas_transport_financial_conflict';END IF;
  selected=CASE WHEN prior.kind='book' THEN book_until ELSE amend_until END;
  IF selected IS NULL OR prior.kind IN('book','adopt_capacity_version') AND gas_until IS NULL THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
  deadline=least(deadline,selected,CASE WHEN prior.kind IN('book','adopt_capacity_version') THEN gas_until ELSE selected END);
  PERFORM security.require_gas_transport_financial_commit(catalog,prior.trade_id,prior.kind,deadline);
  RETURN jsonb_build_object('found',true,'authorityUntil',deadline,'kind',prior.kind,'tradeId',prior.trade_id,'instrumentId',prior.instrument_id,'request',prior.request,'outcome',prior.outcome);
 END IF;
 PERFORM security.require_gas_transport_financial_commit(catalog,NULL,NULL,deadline);
 RETURN jsonb_build_object('found',false,'authorityUntil',deadline);
END$$;
CREATE FUNCTION security.gas_transport_financial_booking_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 result=security.gas_transport_financial_status(catalog,operation);
 IF(result->>'found')::boolean AND result->>'kind'<>'book' THEN RAISE EXCEPTION 'gas_transport_financial_conflict';END IF;
 RETURN result;
END$$;
CREATE FUNCTION security.require_gas_transport_financial_booking_status(catalog uuid,operation uuid,until_at timestamptz,receipt_found boolean) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.gas_transport_financial_commands;actor uuid;BEGIN
 IF receipt_found THEN
  SELECT * INTO prior FROM trading.gas_transport_financial_commands x WHERE(x.catalog_id,x.operation_id)=(catalog,operation);
  SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
  IF prior.kind IS DISTINCT FROM 'book' OR prior.actor_id IS DISTINCT FROM actor THEN RAISE EXCEPTION 'gas_transport_financial_conflict';END IF;
  PERFORM security.require_gas_transport_financial_commit(catalog,prior.trade_id,'book',until_at);
 ELSE PERFORM security.require_gas_transport_financial_commit(catalog,NULL,NULL,until_at);END IF;
END$$;


CREATE FUNCTION security.book_gas_transport_financial(catalog uuid,body jsonb,valuation_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;actor uuid;operation uuid;context uuid;prior trading.gas_transport_financial_commands;
 request jsonb;selected_fact jsonb;outcome jsonb;fee numeric;due timestamptz;publication integer;trade uuid=gen_random_uuid();instrument uuid=gen_random_uuid();cash uuid;binding uuid;BEGIN
 deadline=security.gas_transport_financial_authority(catalog,'book');body=trading.gas_transport_financial_body(body);
 operation=(body->>'operationId')::uuid;context=(body->>'contextId')::uuid;
 request=jsonb_build_object('body',body,'effectiveAt',trading.gas_transport_capacity_instant(valuation_at),'asOf',trading.gas_transport_capacity_instant(valuation_at),'sourceContextId',context);
 selected_fact=trading.gas_transport_financial_fact(body,valuation_at);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.gas_transport_financial_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF prior.operation_id IS NOT NULL THEN
  IF(prior.kind,prior.actor_id,prior.request) IS DISTINCT FROM('book',actor,request) THEN RAISE EXCEPTION 'gas_transport_financial_conflict';END IF;
  PERFORM security.require_gas_transport_financial_commit(catalog,prior.trade_id,'book',deadline);RETURN prior.outcome;
 END IF;
 SELECT * INTO prior FROM trading.gas_transport_financial_commands c WHERE c.catalog_id=catalog AND c.kind='book' AND c.fact=selected_fact AND(c.outcome->>'changed')::boolean ORDER BY c.operation_id LIMIT 1;
 IF prior.operation_id IS NOT NULL THEN
  PERFORM security.require_gas_transport_financial_commit(catalog,prior.trade_id,'book',deadline);
  outcome=prior.outcome||jsonb_build_object('changed',false);
  INSERT INTO trading.gas_transport_financial_commands VALUES(catalog,operation,'book',prior.trade_id,prior.instrument_id,actor,request,selected_fact,outcome,context,deadline);
  SET CONSTRAINTS trading.transport_financial_receipt IMMEDIATE;SET CONSTRAINTS trading.transport_financial_receipt DEFERRED;
  RETURN outcome;
 END IF;
 fee=trading.gas_transport_capacity_quantity(body->>'fixedFeeAmount');due=CASE WHEN body->>'feeDueAt' IS NULL THEN NULL ELSE trading.gas_transport_capacity_at(body->>'feeDueAt') END;
 IF body->>'profile'<>'physical_gas_transport_direct_rights_v1' OR body->>'knowledgeMode'<>'current'
  OR body->>'perspective' NOT IN('customer','provider') OR body->>'rightsValuationPolicy'<>'signed_current_whole_remaining_rights_pv_excluding_fees_and_owned_gas_v1'
  OR body->>'feeProfile'<>'unconditional_one_off_fixed_amount_at_booking_v1' OR body->>'feeDirection' NOT IN('pay','receive')
  OR body->>'expiryPolicy'<>'fixed_window_capacity_rights_expire_at_through_v1' OR fee>=1e20 OR(fee=0)<>(due IS NULL)
  OR EXISTS(SELECT 1 FROM trading.gas_transport_financial_trades x WHERE x.catalog_id=catalog AND(x.entitlement_id=(body->>'entitlementId')::uuid OR(x.book_id,x.business_key)=((body->>'bookId')::uuid,body->>'businessKey')))
 THEN RAISE EXCEPTION 'gas_transport_financial_terms';END IF;
 PERFORM trading.require_gas_transport_financial_source(catalog,body,context,valuation_at,valuation_at);
 PERFORM security.require_trade_parties(catalog,jsonb_build_object('tradeDate',(valuation_at AT TIME ZONE 'UTC')::date,
  'legalEntityId',body->'legalEntityId','legalEntityRevision',body->'legalEntityRevision','counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision'),valuation_at);
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,valuation_at);
 INSERT INTO trading.gas_transport_financial_instruments VALUES(catalog,instrument,(body->>'definitionRevisionId')::uuid,
  (body->>'valuationReferenceCatalogId')::uuid,(body->>'valuationReferenceRevisionId')::uuid,body->>'packageQuantity',body->>'currency',body->>'profile',body->>'perspective',
  body->>'rightsValuationPolicy',body->>'feeProfile',fee,body->>'feeDirection',due,body->>'expiryPolicy',publication,valuation_at);
 PERFORM security.stamp_trade_booking(catalog);
 INSERT INTO trading.trades VALUES(catalog,trade,(body->>'bookId')::uuid,operation,actor,body,publication,valuation_at);
 INSERT INTO trading.gas_transport_financial_trades VALUES(catalog,trade,instrument,body->>'businessKey',(body->>'bookId')::uuid,(body->>'entitlementId')::uuid,operation,actor,
  (body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,valuation_at);
 INSERT INTO trading.gas_transport_financial_states VALUES(catalog,trade,operation,1,NULL,(body->>'entitlementId')::uuid,(body->>'capacityVersionId')::uuid,'live',NULL,NULL,
  body->>'evidence',context,valuation_at,actor,publication,valuation_at);
 IF fee<>0 THEN
  cash=security.timed_cash_payment_instrument_id((body->>'valuationReferenceCatalogId')::uuid,(body->>'valuationReferenceRevisionId')::uuid,body->>'currency',due);binding=gen_random_uuid();
  IF NOT EXISTS(SELECT 1 FROM trading.cash_payment_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,cash)) THEN
   INSERT INTO trading.instruments VALUES(catalog,cash,publication,valuation_at);
   INSERT INTO trading.cash_payment_instruments(catalog_id,instrument_id,reference_catalog_id,reference_revision_id,currency_code,payment_date,publication_order,effective_at,payment_at)
    VALUES(catalog,cash,(body->>'valuationReferenceCatalogId')::uuid,(body->>'valuationReferenceRevisionId')::uuid,body->>'currency',NULL,publication,valuation_at,due);
  END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.cash_payment_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,cash) AND i.effective_at<=valuation_at) THEN RAISE EXCEPTION 'gas_transport_financial_cash_origin';END IF;
  PERFORM security.register_cash_payment_membership(catalog,trade,binding,cash,publication,valuation_at);
  INSERT INTO trading.gas_transport_financial_cash_members VALUES(catalog,trade,binding,cash,fee*CASE body->>'feeDirection' WHEN 'pay' THEN -1 ELSE 1 END);
 END IF;
 outcome=jsonb_build_object('tradeId',trade,'instrumentId',instrument,'operationId',operation,'revision',1,'financialStateId',operation,'feeBindingId',binding,'changed',true);
 INSERT INTO trading.gas_transport_financial_commands VALUES(catalog,operation,'book',trade,instrument,actor,request,selected_fact,outcome,context,deadline);
 SET CONSTRAINTS trading.transport_financial_receipt,trading.transport_financial_fact IMMEDIATE;
 SET CONSTRAINTS trading.transport_financial_receipt,trading.transport_financial_fact DEFERRED;
 PERFORM security.require_gas_transport_financial_commit(catalog,trade,'book',deadline);
 RETURN outcome;
END$$;

CREATE FUNCTION security.record_gas_transport_financial(catalog uuid,operation uuid,body jsonb,effective_at timestamptz,source_context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected_kind text;deadline timestamptz;actor uuid;prior trading.gas_transport_financial_commands;t trading.gas_transport_financial_trades;
 h trading.gas_transport_financial_states;m trading.gas_transport_financial_marks;i trading.gas_transport_financial_instruments;
 request jsonb;selected_fact jsonb;outcome jsonb;publication integer;revision integer;BEGIN
 body=trading.gas_transport_financial_body(body);selected_kind=body->>'kind';deadline=security.gas_transport_financial_authority(catalog,selected_kind);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR selected_kind IS NULL THEN RAISE EXCEPTION 'gas_transport_financial_input';END IF;
 request=jsonb_build_object('body',body,'effectiveAt',trading.gas_transport_capacity_instant(effective_at),
  'asOf',trading.gas_transport_capacity_instant(as_of),'sourceContextId',source_context);selected_fact=trading.gas_transport_financial_fact(body,effective_at);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.gas_transport_financial_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF prior.operation_id IS NOT NULL THEN
  IF(prior.actor_id,prior.kind,prior.request) IS DISTINCT FROM(actor,selected_kind,request) THEN RAISE EXCEPTION 'gas_transport_financial_conflict';END IF;
  PERFORM security.require_gas_transport_financial_commit(catalog,prior.trade_id,selected_kind,deadline);RETURN prior.outcome;
 END IF;
 SELECT * INTO prior FROM trading.gas_transport_financial_commands c WHERE c.catalog_id=catalog AND c.kind=selected_kind AND c.fact=selected_fact AND(c.outcome->>'changed')::boolean ORDER BY c.operation_id LIMIT 1;
 IF prior.operation_id IS NOT NULL THEN
  PERFORM security.require_gas_transport_financial_commit(catalog,prior.trade_id,selected_kind,deadline);outcome=prior.outcome||jsonb_build_object('changed',false);
  INSERT INTO trading.gas_transport_financial_commands VALUES(catalog,operation,selected_kind,prior.trade_id,prior.instrument_id,actor,request,selected_fact,outcome,source_context,deadline);
  SET CONSTRAINTS trading.transport_financial_receipt IMMEDIATE;SET CONSTRAINTS trading.transport_financial_receipt DEFERRED;
  RETURN outcome;
 END IF;
 SELECT * INTO t FROM trading.gas_transport_financial_trades x WHERE(x.catalog_id,x.trade_id)=(catalog,(body->>'tradeId')::uuid);
 IF t.trade_id IS NULL THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
 SELECT * INTO STRICT i FROM trading.gas_transport_financial_instruments x WHERE(x.catalog_id,x.instrument_id)=(catalog,t.instrument_id);
 PERFORM trading.require_gas_transport_financial_source(catalog,body,source_context,effective_at,as_of);
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 IF selected_kind='adopt_rights_mark' THEN
  SELECT * INTO m FROM trading.gas_transport_financial_marks x WHERE(x.catalog_id,x.financial_state_id,x.valuation_at)=(catalog,(body->>'financialStateId')::uuid,as_of) ORDER BY revision DESC LIMIT 1;
  revision=coalesce(m.revision,0)+1;
  INSERT INTO trading.gas_transport_financial_marks VALUES(catalog,t.trade_id,operation,(body->>'financialStateId')::uuid,as_of,revision,m.id,
   (body->>'marketDefinitionRevisionId')::uuid,body->>'curveCode',(body->>'contractId')::uuid,(body->>'seriesId')::uuid,(body->>'observationId')::uuid,
   i.valuation_reference_catalog_id,i.valuation_reference_revision_id,body->>'source',body->>'quote',body->>'evidence',source_context,as_of,actor,publication,effective_at);
  outcome=jsonb_build_object('tradeId',t.trade_id,'instrumentId',t.instrument_id,'kind',selected_kind,'changed',true,'markId',operation,
   'financialStateId',(body->>'financialStateId')::uuid,'revision',revision);
 ELSE
  SELECT * INTO STRICT h FROM trading.gas_transport_financial_states x WHERE(x.catalog_id,x.trade_id)=(catalog,t.trade_id) ORDER BY revision DESC LIMIT 1;
  revision=h.revision+1;
  INSERT INTO trading.gas_transport_financial_states VALUES(catalog,t.trade_id,operation,revision,h.id,t.entitlement_id,
   CASE WHEN selected_kind='expire' THEN h.capacity_version_id ELSE(body->>'capacityVersionId')::uuid END,
   CASE WHEN selected_kind='expire' THEN 'closed' ELSE 'live' END,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,
   body->>'evidence',source_context,as_of,actor,publication,effective_at);
  outcome=jsonb_build_object('tradeId',t.trade_id,'instrumentId',t.instrument_id,'kind',selected_kind,'changed',true,'financialStateId',operation,'revision',revision);
 END IF;
 INSERT INTO trading.gas_transport_financial_commands VALUES(catalog,operation,selected_kind,t.trade_id,t.instrument_id,actor,request,selected_fact,outcome,source_context,deadline);
 SET CONSTRAINTS trading.transport_financial_receipt,trading.transport_financial_fact IMMEDIATE;
 SET CONSTRAINTS trading.transport_financial_receipt,trading.transport_financial_fact DEFERRED;
 PERFORM security.require_gas_transport_financial_commit(catalog,t.trade_id,selected_kind,deadline);RETURN outcome;
END$$;

CREATE FUNCTION trading.gas_transport_financial_state_document(s trading.gas_transport_financial_states) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',s.id,'agreementId',s.trade_id,'revision',s.revision,'previousId',s.previous_id,'entitlementId',s.entitlement_id,'capacityVersionId',s.capacity_version_id,'capacityVersionRevision',v.revision,
  'remainingRightsIdentity',s.id::text,'status',s.status,'from',e.from_at,'through',e.through_at,'effectiveAt',s.effective_at,
  'knownAt',k.known_from,'sourceContextId',s.source_context_id,'sourceKnownAt',x.known_at,'sourceAsOf',s.source_as_of,
  'policyId',s.policy_id,'policyRevision',s.policy_revision,'evidence',s.evidence)
 FROM trading.gas_transport_capacity_entitlements e JOIN trading.gas_transport_capacity_versions v ON(v.catalog_id,v.entitlement_id,v.id)=(s.catalog_id,s.entitlement_id,s.capacity_version_id)
 JOIN market_data.temporal_contexts x ON(x.catalog_id,x.id)=(s.catalog_id,s.source_context_id)
 LEFT JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order)
 WHERE(e.catalog_id,e.id)=(s.catalog_id,s.entitlement_id)
$$;
CREATE FUNCTION trading.gas_transport_financial_mark_document(m trading.gas_transport_financial_marks) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',m.id,'revision',m.revision,'previousId',m.previous_id,'financialStateId',m.financial_state_id,'valuationAt',m.valuation_at,
  'marketDefinitionRevisionId',m.definition_revision_id,'curveCode',m.curve_code,'contractId',m.contract_id,'seriesId',m.series_id,'observationId',m.observation_id,
  'source',m.source_code,'quote',m.quote_code,'effectiveAt',m.effective_at,'knownAt',k.known_from,'sourceContextId',m.source_context_id,
  'sourceKnownAt',x.known_at,'sourceAsOf',m.source_as_of,'evidence',m.evidence)
 FROM market_data.temporal_contexts x LEFT JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(m.catalog_id,m.publication_order)
 WHERE(x.catalog_id,x.id)=(m.catalog_id,m.source_context_id)
$$;
CREATE FUNCTION security.require_gas_transport_financial_read(catalog uuid,context uuid,as_of timestamptz,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;day date;BEGIN
 PERFORM security.gas_transport_financial_authority(catalog,NULL);
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT q FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 PERFORM security.require_gas_park_loan_worker_scope(catalog,context,as_of,NULL,NULL,NULL);
 IF(q.market_catalog_id,q.market_context_id,q.trading_as_of) IS NOT DISTINCT FROM(catalog,context,as_of) THEN
  PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);
 ELSIF(q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of) IS NOT DISTINCT FROM(catalog,context,as_of) THEN
  SELECT effective_on INTO STRICT day FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context);
  PERFORM security.require_admitted_business_source(catalog,context,as_of,day,q.business_source_until);
 ELSE RAISE EXCEPTION 'gas_transport_financial_context' USING ERRCODE='42501';END IF;
 PERFORM security.gas_transport_financial_authority(catalog,NULL);
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.read_gas_transport_financial(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;t trading.gas_transport_financial_trades;i trading.gas_transport_financial_instruments;
 e trading.gas_transport_capacity_entitlements;h trading.gas_transport_financial_states;m trading.gas_transport_financial_marks;
 states jsonb;marks jsonb;cash jsonb;policies jsonb;state jsonb;mark jsonb;expiry jsonb;reason text;BEGIN
 deadline=security.gas_transport_financial_authority(catalog,NULL);
 PERFORM security.require_gas_park_loan_worker_scope(catalog,context,as_of,NULL,NULL,trade);
 PERFORM security.require_gas_transport_financial_read(catalog,context,as_of,deadline);
 SELECT * INTO t FROM trading.gas_transport_financial_trades x WHERE(x.catalog_id,x.trade_id)=(catalog,trade) AND trading.gas_owned_visible(catalog,x.publication_order,x.effective_at,context,as_of);
 IF t.trade_id IS NULL THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
 SELECT * INTO STRICT i FROM trading.gas_transport_financial_instruments x WHERE(x.catalog_id,x.instrument_id)=(catalog,t.instrument_id);
 SELECT * INTO STRICT e FROM trading.gas_transport_capacity_entitlements x WHERE(x.catalog_id,x.id)=(catalog,t.entitlement_id);
 PERFORM trading.require_gas_transport_financial_reference(catalog,trade);
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,i.definition_revision_id);
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE(b.catalog_id,b.id)=(catalog,t.book_id) AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
 THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
 SELECT coalesce(jsonb_agg(trading.gas_transport_financial_state_document(x) ORDER BY x.revision),'[]') INTO states FROM trading.gas_transport_financial_states x
  WHERE(x.catalog_id,x.trade_id)=(catalog,trade) AND trading.gas_transport_financial_known(catalog,x.publication_order,context);
 SELECT * INTO h FROM trading.gas_transport_financial_states x WHERE(x.catalog_id,x.trade_id)=(catalog,trade) AND trading.gas_transport_financial_known(catalog,x.publication_order,context) ORDER BY revision DESC LIMIT 1;
 IF h.id IS NULL THEN reason='financial_state_unavailable';
 ELSIF h.effective_at>as_of THEN reason='financial_state_future';
 ELSE
  state=trading.gas_transport_financial_state_document(h);
  IF h.status='closed' THEN expiry=jsonb_build_object('id',h.id,'agreementId',trade,'effectiveAt',h.effective_at,'knownAt',state->'knownAt','policyId',h.policy_id,'policyRevision',h.policy_revision);
  ELSIF as_of>=e.through_at THEN reason='expiry_transition_required';
  ELSE
   SELECT * INTO m FROM trading.gas_transport_financial_marks x WHERE(x.catalog_id,x.trade_id,x.financial_state_id,x.valuation_at)=(catalog,trade,h.id,as_of)
    AND trading.gas_transport_financial_known(catalog,x.publication_order,context) ORDER BY revision DESC LIMIT 1;
   IF m.id IS NULL THEN reason='rights_mark_required';
   ELSIF m.effective_at>as_of THEN reason='rights_mark_future';
   ELSIF EXISTS(WITH RECURSIVE corrections AS(SELECT x.* FROM market_data.observations x WHERE(x.catalog_id,x.corrects_id)=(catalog,m.observation_id)
    UNION ALL SELECT x.* FROM market_data.observations x JOIN corrections p ON x.corrects_id=p.id WHERE x.catalog_id=catalog)
    SELECT 1 FROM corrections x WHERE trading.gas_transport_financial_market_known(catalog,x.definition_revision_id,x.id,context))
    THEN reason='rights_mark_corrected';
   ELSE mark=trading.gas_transport_financial_mark_document(m);END IF;
  END IF;
 END IF;
 SELECT coalesce(jsonb_agg(trading.gas_transport_financial_mark_document(x) ORDER BY x.financial_state_id,x.valuation_at,x.revision),'[]') INTO marks FROM trading.gas_transport_financial_marks x
  WHERE(x.catalog_id,x.trade_id)=(catalog,trade) AND trading.gas_transport_financial_known(catalog,x.publication_order,context);
 SELECT jsonb_build_object('bindingId',c.binding_id,'instrumentId',c.instrument_id,'amount',trim_scale(c.amount)::text,'currency',ci.currency_code,
  'referenceCatalogId',ci.reference_catalog_id,'referenceRevisionId',ci.reference_revision_id,'dueAt',ci.payment_at,'effectiveAt',cm.effective_at,
  'payments',(SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',p.trade_id,'binding_id',p.binding_id,'book_id',cm.book_id,
   'reference_catalog_id',ci.reference_catalog_id,'reference_revision_id',ci.reference_revision_id,'currency_code',ci.currency_code,'allocation_id',p.allocation_id,
   'revision',p.revision,'paid_at',p.paid_at,'amount',trim_scale(p.amount)::text,'known_at',k.known_from) ORDER BY p.allocation_id,p.revision),'[]')
   FROM settlement_data.cash_payment_revisions p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE(p.catalog_id,p.trade_id,p.binding_id)=(catalog,trade,c.binding_id) AND trading.gas_transport_financial_known(catalog,p.publication_order,context))) INTO cash
 FROM trading.gas_transport_financial_cash_members c JOIN trading.cash_payment_memberships cm ON(cm.catalog_id,cm.trade_id,cm.binding_id)=(c.catalog_id,c.trade_id,c.binding_id)
 JOIN trading.cash_payment_instruments ci ON(ci.catalog_id,ci.instrument_id)=(c.catalog_id,c.instrument_id) WHERE(c.catalog_id,c.trade_id)=(catalog,trade);
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.id),'[]') INTO policies FROM(SELECT DISTINCT ON(p.id)p.* FROM lifecycle.action_policies p
  WHERE p.catalog_id=catalog AND p.action='gas.transport_financial.expire' AND trading.gas_transport_financial_known(catalog,p.publication_order,context) ORDER BY p.id,p.revision DESC)x;
 PERFORM security.require_gas_transport_financial_read(catalog,context,as_of,deadline);
 RETURN jsonb_build_object('implementation',i.profile,'tradeId',trade,'instrumentId',t.instrument_id,'revision',1,'bookId',t.book_id,'operationId',t.operation_id,
  'publicationOrder',t.publication_order,'effectiveAt',t.effective_at,'businessKey',t.business_key,
  'evidence',(SELECT request->>'evidence' FROM trading.trades WHERE(catalog_id,id)=(catalog,trade)),
  'legalEntityId',t.legal_entity_id,'legalEntityRevision',t.legal_entity_revision,
  'counterpartyId',t.counterparty_id,'counterpartyRevision',t.counterparty_revision,'booking',(SELECT to_jsonb(f) FROM trading.booking_facts f WHERE(f.catalog_id,f.trade_id)=(catalog,trade)),
  'tradeReference',(SELECT reference_number FROM trading.trade_references WHERE(catalog_id,trade_id)=(catalog,trade)),
  'instrumentReference',(SELECT reference_number FROM trading.instrument_references WHERE(catalog_id,instrument_id)=(catalog,t.instrument_id)),
  'contextId',context,'asOf',as_of,'knownAt',(SELECT known_at FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context)),
  'effectiveOn',(SELECT effective_on FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context)),'authorityUntil',deadline,
  'terms',jsonb_build_object('definitionRevisionId',i.definition_revision_id,'valuationReferenceCatalogId',i.valuation_reference_catalog_id,'valuationReferenceRevisionId',i.valuation_reference_revision_id,
   'packageQuantity',i.package_quantity_code,'currency',i.currency_code,'profile',i.profile,'perspective',i.perspective,'rightsValuationPolicy',i.rights_valuation_policy,
   'feeProfile',i.fee_profile,'fixedFeeAmount',trim_scale(i.fixed_fee_amount)::text,'feeDirection',i.fee_direction,'feeDueAt',i.fee_due_at,'expiryPolicy',i.expiry_policy,
   'operationalReferenceCatalogId',e.reference_catalog_id,'operationalReferenceRevisionId',e.reference_revision_id,'entitlementId',e.id,'entitlementReference',e.business_key,'operationalProduct',e.product_code,
   'operationalQuantity',e.quantity_code,'from',CASE WHEN state IS NOT NULL THEN e.from_at END,'through',CASE WHEN state IS NOT NULL THEN e.through_at END),
  'financialState',state,'stateHistory',states,'mark',mark,'markHistory',marks,'cash',cash,'unpricedReason',reason,'expiry',expiry,'policies',policies);
END$$;

-- Financial authoring reads only the retained entitlement and its current
-- capacity version at the financial instant. Operational plan assessments keep
-- their separate end-of-day admission and are deliberately not invoked here.
CREATE FUNCTION security.require_gas_transport_financial_capacity_read(catalog uuid,context uuid,as_of timestamptz,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE candidate timestamptz;BEGIN
 candidate=security.authority_until('ReadGasOperations');
 IF candidate IS NULL OR clock_timestamp()>=candidate OR until_at IS NULL OR NOT isfinite(until_at)
  OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_transport_financial_read(catalog,context,as_of,until_at);
 IF security.trading_access(catalog,'ReadGasOperations') IS NOT TRUE OR pg_has_role(session_user,'openetrm_risk_worker','MEMBER')
  OR clock_timestamp()>=candidate OR clock_timestamp()>=until_at
 THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.gas_transport_financial_capacity_facts(catalog uuid,context uuid,as_of timestamptz,entitlement uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE operating_until timestamptz;deadline timestamptz;e trading.gas_transport_capacity_entitlements;
 v trading.gas_transport_capacity_versions;rows jsonb='[]';count integer=0;known boolean;BEGIN
 operating_until=security.authority_until('ReadGasOperations');
 IF operating_until IS NULL OR clock_timestamp()>=operating_until THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
 deadline=least(operating_until,security.gas_transport_financial_authority(catalog,NULL));
 PERFORM security.require_gas_transport_financial_capacity_read(catalog,context,as_of,deadline);
 FOR e IN SELECT x.* FROM trading.gas_transport_capacity_entitlements x
  JOIN trading.books b ON(b.catalog_id,b.id)=(x.catalog_id,x.book_id)
  JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  JOIN trading.legal_entities le ON(le.catalog_id,le.id,le.revision)=(x.catalog_id,x.legal_entity_id,x.legal_entity_revision)
  JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(x.catalog_id,x.counterparty_id,x.counterparty_revision)
  WHERE x.catalog_id=catalog AND(entitlement IS NULL OR x.id=entitlement)
   AND trading.gas_owned_visible(catalog,x.publication_order,x.effective_at,context,as_of)
   AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at)
   AND security.trading_visible(catalog,le.publication_order,le.effective_at) AND security.trading_visible(catalog,cp.publication_order,cp.effective_at)
   AND security.reference_access(x.reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(x.reference_catalog_id,x.reference_revision_id)
  ORDER BY x.id LIMIT 257 LOOP
  count=count+1;
  IF count>256 THEN RAISE EXCEPTION 'gas_transport_financial_scope';END IF;
  SELECT * INTO v FROM trading.gas_transport_capacity_versions x WHERE(x.catalog_id,x.entitlement_id)=(catalog,e.id) ORDER BY x.revision DESC LIMIT 1;
  known=v.id IS NOT NULL AND trading.gas_transport_financial_known(catalog,v.publication_order,context);
  rows=rows||jsonb_build_array(jsonb_build_object('label',e.business_key,'asOf',as_of,'entitlement',to_jsonb(e),
   'version',CASE WHEN known AND v.effective_at<=as_of THEN(to_jsonb(e)-'id'-'actor_id'-'publication_order'-'effective_at')||to_jsonb(v) ELSE 'null'::jsonb END,
   'hasFutureHeads',known AND v.effective_at>as_of));
 END LOOP;
 IF entitlement IS NOT NULL AND count=0 THEN RAISE EXCEPTION 'gas_transport_financial_source';END IF;
 PERFORM security.require_gas_transport_financial_capacity_read(catalog,context,as_of,deadline);
 RETURN jsonb_build_object('contextId',context,'asOf',as_of,'knownAt',(SELECT known_at FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context)),
  'effectiveOn',(SELECT effective_on FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context)),
  'authorityUntil',deadline,'capacities',rows);
END$$;

CREATE FUNCTION trading.require_gas_transport_financial_receipt(catalog uuid,operation uuid) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c trading.gas_transport_financial_commands;t trading.gas_transport_financial_trades;i trading.gas_transport_financial_instruments;
 h trading.gas_transport_financial_states;previous trading.gas_transport_financial_states;m trading.gas_transport_financial_marks;member trading.gas_transport_financial_cash_members;
 body jsonb;expected jsonb;outcome jsonb;effective timestamptz;as_of timestamptz;publication integer;selected uuid;actor uuid;BEGIN
 SELECT * INTO c FROM trading.gas_transport_financial_commands x WHERE(x.catalog_id,x.operation_id)=(catalog,operation);
 IF c.operation_id IS NULL THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 PERFORM security.require_gas_transport_financial_commit(catalog,c.trade_id,c.kind,c.authority_until);
 body=trading.gas_transport_financial_body(c.request->'body');effective=trading.gas_transport_capacity_at(c.request->>'effectiveAt');
 as_of=trading.gas_transport_capacity_at(c.request->>'asOf');
 IF c.actor_id<>actor OR body IS DISTINCT FROM c.request->'body' OR c.fact IS DISTINCT FROM trading.gas_transport_financial_fact(body,effective)
  OR(c.request->>'sourceContextId')::uuid IS DISTINCT FROM c.source_context_id
  OR c.kind='book' AND((body->>'contextId')::uuid IS DISTINCT FROM c.source_context_id OR(body->>'operationId')::uuid<>operation)
  OR c.request IS DISTINCT FROM jsonb_build_object('body',body,'effectiveAt',trading.gas_transport_capacity_instant(effective),'asOf',trading.gas_transport_capacity_instant(as_of),'sourceContextId',c.source_context_id)
  OR(CASE WHEN c.kind='book' THEN body->>'profile' IS DISTINCT FROM 'physical_gas_transport_direct_rights_v1' ELSE body->>'kind' IS DISTINCT FROM c.kind END)
 THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
 SELECT * INTO STRICT t FROM trading.gas_transport_financial_trades x WHERE(x.catalog_id,x.trade_id)=(catalog,c.trade_id);
 IF c.instrument_id<>t.instrument_id THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
 IF c.outcome->'changed'='false'::jsonb THEN
  IF NOT EXISTS(SELECT 1 FROM trading.gas_transport_financial_commands p WHERE p.catalog_id=catalog AND p.operation_id<>operation AND p.kind=c.kind
   AND p.fact=c.fact AND(p.outcome->>'changed')::boolean AND p.outcome||jsonb_build_object('changed',false)=c.outcome
   AND(p.trade_id,p.instrument_id)=(c.trade_id,c.instrument_id)) THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
  RETURN;
 END IF;
 IF c.outcome->'changed' IS DISTINCT FROM 'true'::jsonb OR EXISTS(SELECT 1 FROM trading.gas_transport_financial_commands p WHERE p.catalog_id=catalog
  AND p.operation_id<>operation AND p.kind=c.kind AND p.fact=c.fact AND(p.outcome->>'changed')::boolean)
 THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
 SELECT * INTO STRICT i FROM trading.gas_transport_financial_instruments x WHERE(x.catalog_id,x.instrument_id)=(catalog,t.instrument_id);
 IF c.kind='book' THEN
  SELECT * INTO STRICT h FROM trading.gas_transport_financial_states x WHERE(x.catalog_id,x.id)=(catalog,t.operation_id);
  SELECT * INTO member FROM trading.gas_transport_financial_cash_members x WHERE(x.catalog_id,x.trade_id)=(catalog,t.trade_id);
  IF operation<>t.operation_id OR h.revision<>1 OR h.status<>'live' OR t.actor_id<>actor OR h.actor_id<>actor
   OR(t.publication_order,t.effective_at) IS DISTINCT FROM(h.publication_order,h.effective_at)
   OR(i.publication_order,i.effective_at) IS DISTINCT FROM(t.publication_order,t.effective_at)
   OR NOT EXISTS(SELECT 1 FROM trading.trades x WHERE(x.catalog_id,x.id,x.book_id,x.operation_id,x.actor_id,x.request,x.publication_order,x.effective_at)
    IS NOT DISTINCT FROM(catalog,t.trade_id,t.book_id,operation,actor,body,t.publication_order,t.effective_at))
  THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
  expected=body||jsonb_build_object('operationId',t.operation_id,'bookId',t.book_id,'businessKey',t.business_key,'legalEntityId',t.legal_entity_id,
   'legalEntityRevision',t.legal_entity_revision,'counterpartyId',t.counterparty_id,'counterpartyRevision',t.counterparty_revision,'entitlementId',t.entitlement_id,
   'capacityVersionId',h.capacity_version_id,'definitionRevisionId',i.definition_revision_id,'valuationReferenceCatalogId',i.valuation_reference_catalog_id,
   'valuationReferenceRevisionId',i.valuation_reference_revision_id,'profile',i.profile,'perspective',i.perspective,'packageQuantity',i.package_quantity_code,
   'currency',i.currency_code,'rightsValuationPolicy',i.rights_valuation_policy,'feeProfile',i.fee_profile,'fixedFeeAmount',trim_scale(i.fixed_fee_amount)::text,
   'feeDirection',i.fee_direction,'feeDueAt',CASE WHEN i.fee_due_at IS NOT NULL THEN trading.gas_transport_capacity_instant(i.fee_due_at) END,
   'expiryPolicy',i.expiry_policy,'evidence',h.evidence);
  outcome=jsonb_build_object('tradeId',t.trade_id,'instrumentId',t.instrument_id,'operationId',operation,'revision',1,'financialStateId',h.id,'feeBindingId',member.binding_id,'changed',true);
  IF(i.fixed_fee_amount=0)<>(member.binding_id IS NULL) THEN RAISE EXCEPTION 'gas_transport_financial_cash';END IF;
  IF member.binding_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.cash_payment_memberships cm JOIN trading.cash_payment_instruments ci ON(ci.catalog_id,ci.instrument_id)=(cm.catalog_id,cm.instrument_id)
   WHERE(cm.catalog_id,cm.trade_id,cm.binding_id,cm.instrument_id,cm.book_id,cm.publication_order,cm.effective_at) IS NOT DISTINCT FROM(catalog,t.trade_id,member.binding_id,member.instrument_id,t.book_id,t.publication_order,t.effective_at)
    AND(ci.reference_catalog_id,ci.reference_revision_id,ci.currency_code,ci.payment_at) IS NOT DISTINCT FROM(i.valuation_reference_catalog_id,i.valuation_reference_revision_id,i.currency_code,i.fee_due_at)
    AND member.amount=i.fixed_fee_amount*CASE i.fee_direction WHEN 'pay' THEN -1 ELSE 1 END AND ci.effective_at<=t.effective_at)
   THEN RAISE EXCEPTION 'gas_transport_financial_cash';END IF;
  publication=h.publication_order;selected=h.id;
 ELSIF c.kind='adopt_rights_mark' THEN
  SELECT * INTO STRICT m FROM trading.gas_transport_financial_marks x WHERE(x.catalog_id,x.id)=(catalog,operation);
  IF(m.trade_id,m.actor_id,m.source_context_id,m.source_as_of,m.effective_at) IS DISTINCT FROM(t.trade_id,actor,c.source_context_id,as_of,effective)
   OR(m.valuation_reference_catalog_id,m.valuation_reference_revision_id) IS DISTINCT FROM(i.valuation_reference_catalog_id,i.valuation_reference_revision_id)
  THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
  expected=jsonb_build_object('kind',c.kind,'tradeId',t.trade_id,'financialStateId',m.financial_state_id,'expectedPreviousMarkId',m.previous_id,'valuationAt',trading.gas_transport_capacity_instant(m.valuation_at),
   'marketDefinitionRevisionId',m.definition_revision_id,'curveCode',m.curve_code,'contractId',m.contract_id,'seriesId',m.series_id,'observationId',m.observation_id,'source',m.source_code,'quote',m.quote_code,'evidence',m.evidence);
  outcome=jsonb_build_object('tradeId',t.trade_id,'instrumentId',t.instrument_id,'kind',c.kind,'changed',true,'markId',m.id,'financialStateId',m.financial_state_id,'revision',m.revision);
  IF m.previous_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.gas_transport_financial_marks p WHERE(p.catalog_id,p.id)=(catalog,m.previous_id)
   AND p.revision=m.revision-1 AND p.publication_order<m.publication_order AND trading.gas_transport_financial_known(catalog,p.publication_order,c.source_context_id))
   THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
  publication=m.publication_order;selected=m.id;
 ELSE
  SELECT * INTO STRICT h FROM trading.gas_transport_financial_states x WHERE(x.catalog_id,x.id)=(catalog,operation);
  SELECT * INTO STRICT previous FROM trading.gas_transport_financial_states x WHERE(x.catalog_id,x.id)=(catalog,h.previous_id);
  IF(h.trade_id,h.actor_id,h.source_context_id,h.source_as_of,h.effective_at) IS DISTINCT FROM(t.trade_id,actor,c.source_context_id,as_of,effective)
   OR previous.status<>'live' OR previous.revision<>h.revision-1 OR previous.publication_order>=h.publication_order OR previous.effective_at>h.effective_at
   OR NOT trading.gas_transport_financial_known(catalog,previous.publication_order,c.source_context_id)
   OR(c.kind='expire')<>(h.status='closed') OR c.kind='expire' AND h.capacity_version_id<>previous.capacity_version_id
  THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
  expected=jsonb_build_object('kind',c.kind,'tradeId',t.trade_id,'expectedFinancialStateId',previous.id,'evidence',h.evidence);
  IF c.kind='expire' THEN expected=expected||jsonb_build_object('policyId',h.policy_id,'policyRevision',h.policy_revision);
  ELSE expected=expected||jsonb_build_object('capacityVersionId',h.capacity_version_id);END IF;
  outcome=jsonb_build_object('tradeId',t.trade_id,'instrumentId',t.instrument_id,'kind',c.kind,'changed',true,'financialStateId',h.id,'revision',h.revision);
  publication=h.publication_order;selected=h.id;
 END IF;
 IF body IS DISTINCT FROM expected OR c.outcome IS DISTINCT FROM outcome OR effective IS DISTINCT FROM coalesce(h.effective_at,m.effective_at)
  OR c.source_context_id IS DISTINCT FROM coalesce(h.source_context_id,m.source_context_id)
  OR as_of IS DISTINCT FROM coalesce(h.source_as_of,m.source_as_of)
  OR NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(catalog,publication,pg_current_xact_id()))
  OR(SELECT count(*) FROM(SELECT publication_order FROM trading.gas_transport_financial_states x WHERE x.catalog_id=catalog AND x.publication_order=publication
    UNION ALL SELECT publication_order FROM trading.gas_transport_financial_marks x WHERE x.catalog_id=catalog AND x.publication_order=publication)x)<>1
 THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
 PERFORM trading.require_gas_transport_financial_source(catalog,body,c.source_context_id,effective,as_of,selected);
END$$;

CREATE FUNCTION trading.guard_gas_transport_financial_receipt() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN PERFORM trading.require_gas_transport_financial_receipt(NEW.catalog_id,NEW.operation_id);RETURN NEW;END$$;
CREATE CONSTRAINT TRIGGER transport_financial_receipt AFTER INSERT ON trading.gas_transport_financial_commands DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_financial_receipt();
CREATE FUNCTION trading.guard_gas_transport_financial_fact() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE operation uuid;BEGIN
 IF TG_TABLE_NAME IN('gas_transport_financial_states','gas_transport_financial_marks') THEN operation=NEW.id;
 ELSIF TG_TABLE_NAME='gas_transport_financial_instruments' THEN
  SELECT operation_id INTO operation FROM trading.gas_transport_financial_trades t WHERE(t.catalog_id,t.instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 ELSIF TG_TABLE_NAME='gas_transport_financial_trades' THEN operation=NEW.operation_id;
 ELSE SELECT operation_id INTO operation FROM trading.gas_transport_financial_trades t WHERE(t.catalog_id,t.trade_id)=(NEW.catalog_id,NEW.trade_id);END IF;
 IF operation IS NULL THEN RAISE EXCEPTION 'gas_transport_financial_receipt';END IF;
 PERFORM trading.require_gas_transport_financial_receipt(NEW.catalog_id,operation);RETURN NEW;
END$$;
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_transport_financial_instruments','gas_transport_financial_trades','gas_transport_financial_states','gas_transport_financial_marks','gas_transport_financial_cash_members'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER transport_financial_fact AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_financial_fact()',name);
 END LOOP;
END$$;
-- This gate is deliberately never included in the producer's eager validation.
-- The supported native protocol and mandatory managed admission retain the
-- original deadline/source through final transaction admission.
CREATE FUNCTION trading.guard_gas_transport_financial_deadline() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN PERFORM trading.require_gas_transport_financial_receipt(NEW.catalog_id,NEW.operation_id);RETURN NEW;END$$;
CREATE CONSTRAINT TRIGGER transport_financial_deadline AFTER INSERT ON trading.gas_transport_financial_commands DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_financial_deadline();

CREATE FUNCTION settlement_data.guard_gas_transport_financial_cash() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE member trading.gas_transport_financial_cash_members;origin timestamptz;total numeric;BEGIN
 SELECT * INTO member FROM trading.gas_transport_financial_cash_members c WHERE(c.catalog_id,c.trade_id,c.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id);
 IF member.binding_id IS NULL THEN RETURN NEW;END IF;
 SELECT effective_at INTO STRICT origin FROM trading.gas_transport_financial_trades WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id);
 PERFORM security.require_cash_payment_write(NEW.catalog_id,NEW.trade_id,NEW.binding_id,greatest(origin,NEW.paid_at));
 IF NEW.paid_at<origin OR abs(NEW.amount)>abs(member.amount) OR NEW.amount<>0 AND sign(NEW.amount)<>sign(member.amount)
  OR NEW.actor_id IS DISTINCT FROM(SELECT actor_id FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user)
  OR NOT EXISTS(SELECT 1 FROM settlement_data.cash_payment_commands c WHERE(c.catalog_id,c.allocation_id,c.revision,c.actor_id)=(NEW.catalog_id,NEW.allocation_id,NEW.revision,NEW.actor_id)
   AND(c.request->>'tradeId')::uuid=NEW.trade_id AND(c.request->>'bindingId')::uuid=NEW.binding_id AND(c.request->>'amount')::numeric=NEW.amount
   AND((c.request->>'paidAtUtc')::timestamp AT TIME ZONE 'UTC')=NEW.paid_at)
 THEN RAISE EXCEPTION 'gas_transport_financial_cash_payment';END IF;
 SELECT coalesce(sum(p.amount),0) INTO total FROM(SELECT DISTINCT ON(p.allocation_id)p.* FROM settlement_data.cash_payment_revisions p
  WHERE(p.catalog_id,p.trade_id,p.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id) ORDER BY p.allocation_id,p.revision DESC)p;
 IF abs(total)>abs(member.amount) OR total<>0 AND sign(total)<>sign(member.amount) THEN RAISE EXCEPTION 'gas_transport_financial_cash_payment';END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER transport_financial_cash AFTER INSERT ON settlement_data.cash_payment_revisions DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_gas_transport_financial_cash();
CREATE FUNCTION trading.guard_gas_transport_financial_cash_membership() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM trading.gas_transport_financial_trades t WHERE(t.catalog_id,t.trade_id)=(NEW.catalog_id,NEW.trade_id))
  AND NOT EXISTS(SELECT 1 FROM trading.gas_transport_financial_cash_members m JOIN trading.gas_transport_financial_trades t ON(t.catalog_id,t.trade_id)=(m.catalog_id,m.trade_id)
   WHERE(m.catalog_id,m.trade_id,m.binding_id,m.instrument_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id,NEW.instrument_id)
    AND(t.book_id,t.publication_order,t.effective_at) IS NOT DISTINCT FROM(NEW.book_id,NEW.publication_order,NEW.effective_at))
 THEN RAISE EXCEPTION 'gas_transport_financial_cash';END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER transport_financial_cash_membership AFTER INSERT ON trading.cash_payment_memberships DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_financial_cash_membership();

CREATE FUNCTION trading.gas_transport_financial_selectors(books uuid[],trades uuid[]) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF books IS NULL OR cardinality(books) NOT BETWEEN 1 AND 128 OR coalesce(cardinality(trades),0)>4096
  OR EXISTS(SELECT 1 FROM unnest(books)x WHERE x IS NULL OR x='00000000-0000-0000-0000-000000000000'::uuid)
  OR EXISTS(SELECT 1 FROM unnest(trades)x WHERE x IS NULL OR x='00000000-0000-0000-0000-000000000000'::uuid)
  OR(SELECT count(DISTINCT x) FROM unnest(books)x)<>cardinality(books)
  OR(SELECT count(DISTINCT x) FROM unnest(trades)x)<>coalesce(cardinality(trades),0)
 THEN RAISE EXCEPTION 'gas_transport_financial_scope';END IF;
END$$;
CREATE FUNCTION security.require_gas_transport_financial_presence(catalog uuid,context uuid,as_of timestamptz,books uuid[],trades uuid[],until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM trading.gas_transport_financial_selectors(books,trades);
 PERFORM security.gas_transport_financial_authority(catalog,NULL);
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);
 PERFORM security.require_trading_history(catalog,context);
 PERFORM security.require_gas_park_loan_worker_scope(catalog,context,as_of,books,coalesce(trades,ARRAY[]::uuid[]),NULL);
 IF NOT security.trading_context_day_matches(catalog,context,as_of) OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND(t.known_at>=as_of OR NOT EXISTS(
   SELECT 1 FROM trading.gas_transport_financial_trades a WHERE a.catalog_id=catalog AND a.book_id=ANY(books)
    AND(coalesce(cardinality(trades),0)=0 OR a.trade_id=ANY(trades))
    AND trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of))))
 THEN RAISE EXCEPTION 'gas_transport_financial_context';END IF;
 IF until_at IS NULL OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_transport_financial_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.require_gas_transport_financial_scope(catalog uuid,context uuid,as_of timestamptz,books uuid[],trades uuid[]) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;present boolean;BEGIN
 deadline=security.gas_transport_financial_authority(catalog,NULL);
 PERFORM security.require_gas_transport_financial_presence(catalog,context,as_of,books,trades,deadline);
 SELECT EXISTS(SELECT 1 FROM trading.gas_transport_financial_trades t WHERE t.catalog_id=catalog AND t.book_id=ANY(books)
  AND(coalesce(cardinality(trades),0)=0 OR t.trade_id=ANY(trades)) AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of)) INTO present;
 PERFORM security.require_gas_transport_financial_presence(catalog,context,as_of,books,trades,deadline);RETURN present;
END$$;
CREATE FUNCTION security.gas_transport_financial_directory(catalog uuid,context uuid,as_of timestamptz,books uuid[],trades uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;result jsonb;BEGIN
 deadline=security.gas_transport_financial_authority(catalog,NULL);
 PERFORM security.require_gas_transport_financial_presence(catalog,context,as_of,books,trades,deadline);
 PERFORM security.require_gas_transport_financial_read(catalog,context,as_of,deadline);
 IF(SELECT count(*) FROM trading.gas_transport_financial_trades t WHERE t.catalog_id=catalog AND t.book_id=ANY(books)
  AND(coalesce(cardinality(trades),0)=0 OR t.trade_id=ANY(trades)) AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of))>4096
 THEN RAISE EXCEPTION 'gas_transport_financial_scope';END IF;
 SELECT coalesce(jsonb_agg(security.read_gas_transport_financial(catalog,t.trade_id,context,as_of) ORDER BY t.trade_id),'[]') INTO result
 FROM trading.gas_transport_financial_trades t WHERE t.catalog_id=catalog AND t.book_id=ANY(books)
  AND(coalesce(cardinality(trades),0)=0 OR t.trade_id=ANY(trades)) AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of);
 PERFORM security.require_gas_transport_financial_read(catalog,context,as_of,deadline);RETURN result;
END$$;
CREATE FUNCTION security.gas_transport_financial_trade_head(catalog uuid,trade uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;present boolean;job risk.jobs;BEGIN
 deadline=security.gas_transport_financial_authority(catalog,NULL);
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();PERFORM security.require_gas_park_loan_worker_scope(catalog,job.context_id,job.valuation_at,NULL,NULL,trade);
 END IF;
 SELECT EXISTS(SELECT 1 FROM trading.gas_transport_financial_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,trade)
  AND security.trading_visible(catalog,t.publication_order,t.effective_at)) INTO present;
 PERFORM security.require_gas_transport_financial_commit(catalog,NULL,NULL,deadline);RETURN present;
END$$;
CREATE FUNCTION security.risk_gas_transport_financial_trade_heads(catalog uuid,books uuid[],trades uuid[]) RETURNS SETOF jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;q security.request_contexts;BEGIN
 deadline=security.gas_transport_financial_authority(catalog,NULL);PERFORM trading.gas_transport_financial_selectors(books,trades);
 SELECT * INTO STRICT q FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 PERFORM security.require_gas_transport_financial_presence(catalog,q.market_context_id,q.trading_as_of,books,trades,deadline);
 RETURN QUERY SELECT jsonb_build_object('trade_id',t.trade_id,'book_id',t.book_id,'revision',1) FROM trading.gas_transport_financial_trades t
  JOIN trading.books b ON(b.catalog_id,b.id)=(t.catalog_id,t.book_id) JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE t.catalog_id=catalog AND t.book_id=ANY(books) AND(coalesce(cardinality(trades),0)=0 OR t.trade_id=ANY(trades))
   AND security.trading_visible(catalog,t.publication_order,t.effective_at) AND security.trading_visible(catalog,b.publication_order,b.effective_at)
   AND security.trading_visible(catalog,p.publication_order,p.effective_at) ORDER BY t.trade_id;
 PERFORM security.require_gas_transport_financial_presence(catalog,q.market_context_id,q.trading_as_of,books,trades,deadline);
END$$;
CREATE FUNCTION security.gas_transport_financial_projection_fence(catalog uuid,context uuid,as_of timestamptz,horizon timestamptz,books uuid[],trades uuid[]) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;present boolean;BEGIN
 deadline=security.gas_transport_financial_authority(catalog,NULL);
 PERFORM security.require_gas_transport_financial_presence(catalog,context,as_of,books,trades,deadline);
 IF horizon IS NULL OR NOT isfinite(horizon) OR horizon<as_of THEN RAISE EXCEPTION 'gas_transport_financial_context';END IF;
 SELECT EXISTS(SELECT 1 FROM trading.gas_transport_financial_trades t WHERE t.catalog_id=catalog AND t.book_id=ANY(books)
  AND(coalesce(cardinality(trades),0)=0 OR t.trade_id=ANY(trades)) AND t.effective_at<=horizon AND trading.gas_transport_financial_known(catalog,t.publication_order,context)) INTO present;
 PERFORM security.require_gas_transport_financial_presence(catalog,context,as_of,books,trades,deadline);RETURN present;
END$$;

DO $$DECLARE item record;BEGIN
 FOR item IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('trading','security','settlement_data') AND p.proname LIKE '%gas_transport_financial%' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',item.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',item.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.book_gas_transport_financial(uuid,jsonb,timestamptz),security.gas_transport_financial_booking_status(uuid,uuid),
 security.record_gas_transport_financial(uuid,uuid,jsonb,timestamptz,uuid,timestamptz),security.gas_transport_financial_status(uuid,uuid),
 security.gas_transport_financial_owner(uuid),
 security.gas_transport_financial_accepted_expiry(uuid,jsonb),
 security.gas_transport_financial_capacity_facts(uuid,uuid,timestamptz,uuid),security.require_gas_transport_financial_capacity_read(uuid,uuid,timestamptz,timestamptz),
 security.require_gas_transport_financial_commit(uuid,uuid,text,timestamptz),security.require_gas_transport_financial_booking_status(uuid,uuid,timestamptz,boolean)
 TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.gas_transport_financial_authority(uuid,text),security.read_gas_transport_financial(uuid,uuid,uuid,timestamptz),
 security.require_gas_transport_financial_commit(uuid,uuid,text,timestamptz),
 security.require_gas_transport_financial_read(uuid,uuid,timestamptz,timestamptz),
 security.require_gas_transport_financial_presence(uuid,uuid,timestamptz,uuid[],uuid[],timestamptz),
 security.require_gas_transport_financial_scope(uuid,uuid,timestamptz,uuid[],uuid[]),
 security.gas_transport_financial_directory(uuid,uuid,timestamptz,uuid[],uuid[]),security.gas_transport_financial_trade_head(uuid,uuid),
 security.risk_gas_transport_financial_trade_heads(uuid,uuid[],uuid[]),security.gas_transport_financial_projection_fence(uuid,uuid,timestamptz,timestamptz,uuid[],uuid[])
 TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
