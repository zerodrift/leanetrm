-- Same-point custody retains customer title and uses independent ordinary cash.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

INSERT INTO lifecycle.action_kinds(code,automatic,configuration_permission,execution_permission)
 SELECT code,false,'ConfigureGasServices','RecordGasOperations' FROM unnest(ARRAY['gas.park_custody.accept','gas.park_custody.return'])code;

CREATE TABLE trading.gas_park_custody_instruments(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,point_code ref_data.code NOT NULL,
 pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,commodity_code ref_data.code NOT NULL,
 quantity_code ref_data.code NOT NULL,currency_code ref_data.code NOT NULL,
 service_kind text NOT NULL CHECK(service_kind='park'),perspective text NOT NULL CHECK(perspective IN('customer','provider')),
 park_at ref_data.instant NOT NULL,return_at ref_data.instant NOT NULL,
 title_policy text NOT NULL CHECK(title_policy='customer_title_retained_v1'),
 return_policy text NOT NULL CHECK(return_policy='same_point_same_basis_partial_custody_returns_v1'),
 return_timing text NOT NULL CHECK(return_timing='accepted_custody_then_returns_v1'),
 return_order text NOT NULL CHECK(return_order='nondecreasing_accepted_custody_return_instants_v1'),
 use_policy text NOT NULL CHECK(use_policy='no_use_or_sale_of_custody_v1'),
 loss_policy text NOT NULL CHECK(loss_policy='unqualified_loss_requires_reconciliation_v1'),
 physical_policy text NOT NULL CHECK(physical_policy='custody_only_no_inferred_flow_v1'),
 plan_policy text NOT NULL CHECK(plan_policy='unaccepted_custody_plan_from_later_of_earliest_or_evaluation_v1'),
 fixed_fee_amount numeric NOT NULL CHECK(fixed_fee_amount>=0 AND fixed_fee_amount<1e20 AND scale(fixed_fee_amount)<=18),
 fee_direction text NOT NULL CHECK(fee_direction IN('pay','receive')),fee_due_at ref_data.instant,
 fee_recognition text NOT NULL CHECK(fee_recognition='accepted_custody_v1'),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id) REFERENCES market_data.revisions(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,point_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,commodity_code)
  REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code,commodity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(park_at<return_at),CHECK((fixed_fee_amount=0)=(fee_due_at IS NULL)),
 CHECK(fee_due_at IS NULL OR fee_due_at>=park_at AND fee_due_at>='1901-01-01T00:00:00Z' AND fee_due_at<'2200-01-01T00:00:00Z')
);
CREATE TABLE trading.gas_park_custody_trades(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision=1),instrument_id uuid NOT NULL,
 book_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),owned_account_id uuid,
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,authority_until ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id),UNIQUE(catalog_id,instrument_id),UNIQUE(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_park_custody_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,owned_account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_park_custody_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 action text NOT NULL CHECK(action IN('evidence','accept','return')),request jsonb NOT NULL,outcome jsonb NOT NULL,
 authority_until ref_data.instant NOT NULL,policy_id uuid,policy_revision integer,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_park_custody_trades(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 CHECK((policy_id IS NULL)=(policy_revision IS NULL)),CHECK((action='evidence')=(policy_id IS NULL))
);
CREATE TABLE trading.gas_park_custody_evidence(
 catalog_id uuid NOT NULL,id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,
 source_key text NOT NULL CHECK(source_key=btrim(source_key) AND length(source_key) BETWEEN 1 AND 128 AND source_key!~U&'[\0001-\001F\007F-\009F]'),
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 kind text NOT NULL CHECK(kind IN('custody_agreement','custody_receipt','custody_return')),
 quantity numeric NOT NULL CHECK(quantity>=0 AND quantity<1e24 AND scale(quantity)<=18),observed_at ref_data.instant NOT NULL,
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 2000),source_context_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,operation_id),UNIQUE(catalog_id,trade_id,source_key,revision),UNIQUE(catalog_id,trade_id,source_key,id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_park_custody_trades(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,trade_id,source_key,previous_id) REFERENCES trading.gas_park_custody_evidence(catalog_id,trade_id,source_key,id),
 CONSTRAINT gas_custody_evidence_receipt FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_park_custody_commands(catalog_id,operation_id) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),CHECK((revision=1)=(previous_id IS NULL))
);
CREATE TABLE trading.gas_park_custody_acceptances(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,id uuid NOT NULL,operation_id uuid NOT NULL,
 agreement_source_key text NOT NULL,agreement_evidence_id uuid NOT NULL,receipt_source_key text NOT NULL,receipt_evidence_id uuid NOT NULL,
 source_context_id uuid NOT NULL,source_head integer NOT NULL,policy_id uuid NOT NULL,policy_revision integer NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id),UNIQUE(catalog_id,id),UNIQUE(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_park_custody_trades(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,trade_id,agreement_source_key,agreement_evidence_id) REFERENCES trading.gas_park_custody_evidence(catalog_id,trade_id,source_key,id),
 FOREIGN KEY(catalog_id,trade_id,receipt_source_key,receipt_evidence_id) REFERENCES trading.gas_park_custody_evidence(catalog_id,trade_id,source_key,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CONSTRAINT gas_custody_acceptance_receipt FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_park_custody_commands(catalog_id,operation_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE trading.gas_park_custody_lots(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,lot_id uuid NOT NULL,reservation_id uuid NOT NULL,quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),
 PRIMARY KEY(catalog_id,trade_id,lot_id),UNIQUE(catalog_id,reservation_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_park_custody_acceptances(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,lot_id) REFERENCES trading.gas_owned_lots(catalog_id,id),
 FOREIGN KEY(catalog_id,reservation_id) REFERENCES trading.gas_owned_reservations(catalog_id,id)
);
CREATE TABLE trading.gas_park_custody_returns(
 catalog_id uuid NOT NULL,id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,
 business_key text NOT NULL CHECK(business_key=btrim(business_key) AND length(business_key) BETWEEN 1 AND 128 AND business_key!~U&'[\0001-\001F\007F-\009F]'),
 evidence_source_key text NOT NULL,evidence_id uuid NOT NULL,
 quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),source_context_id uuid NOT NULL,source_head integer NOT NULL,
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,operation_id),UNIQUE(catalog_id,trade_id,business_key),UNIQUE(catalog_id,trade_id,evidence_source_key),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_park_custody_acceptances(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,trade_id,evidence_source_key,evidence_id) REFERENCES trading.gas_park_custody_evidence(catalog_id,trade_id,source_key,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CONSTRAINT gas_custody_return_receipt FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_park_custody_commands(catalog_id,operation_id) DEFERRABLE INITIALLY DEFERRED
);
CREATE TABLE trading.gas_park_custody_return_lots(
 catalog_id uuid NOT NULL,return_id uuid NOT NULL,lot_id uuid NOT NULL,released_reservation_id uuid NOT NULL,remainder_reservation_id uuid,
 quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),
 PRIMARY KEY(catalog_id,return_id,lot_id),UNIQUE(catalog_id,released_reservation_id),UNIQUE(catalog_id,remainder_reservation_id),
 FOREIGN KEY(catalog_id,return_id) REFERENCES trading.gas_park_custody_returns(catalog_id,id),
 FOREIGN KEY(catalog_id,lot_id) REFERENCES trading.gas_owned_lots(catalog_id,id),
 FOREIGN KEY(catalog_id,released_reservation_id) REFERENCES trading.gas_owned_releases(catalog_id,reservation_id),
 FOREIGN KEY(catalog_id,remainder_reservation_id) REFERENCES trading.gas_owned_reservations(catalog_id,id)
);
CREATE TABLE trading.gas_park_custody_cash_members(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,instrument_id uuid NOT NULL,
 amount numeric NOT NULL CHECK(amount<>0 AND abs(amount)<1e20 AND scale(amount)<=18),
 PRIMARY KEY(catalog_id,trade_id),UNIQUE(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_park_custody_acceptances(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,trade_id,binding_id) REFERENCES trading.cash_payment_memberships(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.cash_payment_instruments(catalog_id,instrument_id)
);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_park_custody_instruments','gas_park_custody_trades','gas_park_custody_commands','gas_park_custody_evidence',
  'gas_park_custody_acceptances','gas_park_custody_lots','gas_park_custody_returns','gas_park_custody_return_lots','gas_park_custody_cash_members'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;

CREATE FUNCTION trading.gas_park_custody_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=trading.gas_park_loan_fact(body);key text;BEGIN
 FOREACH key IN ARRAY ARRAY['sourceKey','businessKey'] LOOP
  IF body?key THEN
   IF jsonb_typeof(body->key) IS DISTINCT FROM 'string' OR length(btrim(body->>key)) NOT BETWEEN 1 AND 128 OR(body->>key)~U&'[\0001-\001F\007F-\009F]'
   THEN RAISE EXCEPTION '%',CASE key WHEN 'sourceKey' THEN 'gas_custody_evidence' ELSE 'gas_custody_input' END;END IF;
   result=jsonb_set(result,ARRAY[key],to_jsonb(btrim(body->>key)));
  END IF;
 END LOOP;
 IF body->>'quantity' IS NOT NULL THEN result=result||jsonb_build_object('quantity',trim_scale(trading.gas_owned_quantity(body->>'quantity'))::text);END IF;
 FOREACH key IN ARRAY ARRAY['agreementEvidenceId','receiptEvidenceId'] LOOP
  IF body->>key IS NOT NULL THEN result=result||jsonb_build_object(key,(body->>key)::uuid);END IF;
 END LOOP;
 IF body->>'parkAt' IS NOT NULL THEN result=result||jsonb_build_object('parkAt',to_char(trading.gas_owned_at(body->>'parkAt') AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));END IF;
 IF body?'allocations' THEN
  IF jsonb_typeof(body->'allocations') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'allocations')>128 THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
  result=jsonb_set(result,'{allocations}',(SELECT coalesce(jsonb_agg(trading.gas_owned_fact(a) ORDER BY coalesce((a->>'lotId')::uuid,(a->>'reservationId')::uuid)),'[]') FROM jsonb_array_elements(body->'allocations')a));
 END IF;
 RETURN result;
END$$;

CREATE FUNCTION security.gas_park_custody_authority(catalog uuid,writing boolean,inventory boolean) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;BEGIN
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOperations'));
 IF writing THEN deadline=least(deadline,security.authority_until('RecordGasOperations'));END IF;
 IF inventory THEN
  deadline=least(deadline,security.authority_until('ReadGasOwnedHoldings'));
  IF writing THEN deadline=least(deadline,security.authority_until('RecordGasOwnedHoldings'));END IF;
 END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND writing THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_operations(catalog,'read');
 IF writing AND security.trading_access(catalog,'RecordGasOperations') IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 IF writing AND security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
 IF inventory THEN PERFORM security.require_gas_owned(catalog,writing,false);END IF;
 IF deadline IS NULL OR clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 RETURN deadline;
END$$;

CREATE FUNCTION security.gas_park_custody_booking_authority(catalog uuid) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;BEGIN
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('BookTrade'));
 IF security.trading_access(catalog,'BookTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR deadline IS NULL OR NOT isfinite(deadline) OR clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 RETURN deadline;
END$$;

CREATE FUNCTION security.book_gas_park_custody(catalog uuid,body jsonb,valuation timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;inventory_deadline timestamptz;actor uuid;operation uuid;context uuid;prior trading.trades;owner trading.gas_owned_accounts;
 point ref_data.pipeline_delivery_points;quantity_spec ref_data.quantity_specifications;
 quantity numeric;fee numeric;park_at timestamptz;return_at timestamptz;fee_due timestamptz;
 publication integer;trade uuid=gen_random_uuid();instrument uuid=gen_random_uuid();BEGIN
 inventory_deadline=security.authority_until('ReadGasOwnedHoldings');
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('BookTrade'));
 IF security.trading_access(catalog,'BookTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
  OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.current_write_context() IS NOT TRUE
  OR deadline IS NULL OR clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 PERFORM trading.gas_park_loan_keys(body,ARRAY['operationId','snapshotId','contextId','definitionRevisionId','convention','knowledgeMode','profile','bookId',
  'legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','referenceCatalogId','referenceRevisionId','point','quantitySpecification','currency',
  'ownedAccountId','serviceKind','perspective','quantity','parkAt','returnAt','titlePolicy','returnPolicy','returnTiming','returnOrder','usePolicy','lossPolicy',
  'physicalPolicy','planPolicy','fixedFeeAmount','feeDirection','feeDueAt','feeRecognition'],ARRAY['ownedAccountId','feeDueAt']);
 body=trading.gas_park_custody_fact(body);operation=(body->>'operationId')::uuid;context=(body->>'contextId')::uuid;
 IF body->>'perspective'='customer' THEN
  PERFORM security.require_gas_owned(catalog,false,false);deadline=least(deadline,inventory_deadline);
 END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 IF clock_timestamp()>=deadline OR security.trading_access(catalog,'BookTrade') IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO prior FROM trading.trades t WHERE(t.catalog_id,t.operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>actor OR prior.request IS DISTINCT FROM body OR NOT EXISTS(SELECT 1 FROM trading.gas_park_custody_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,prior.id))
  THEN RAISE EXCEPTION 'gas_custody_conflict';END IF;
  IF security.reference_access((body->>'referenceCatalogId')::uuid,'ReadMarketData') IS NOT TRUE
   OR security.reference_version_visible((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid) IS NOT TRUE
   OR security.market_fact_visible(catalog,(body->>'definitionRevisionId')::uuid,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
  RETURN jsonb_build_object('tradeId',prior.id,'instrumentId',(SELECT instrument_id FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(catalog,prior.id)),
   'operationId',operation,'revision',1);
 END IF;
 quantity=trading.gas_owned_quantity(body->>'quantity');fee=trading.gas_owned_quantity(body->>'fixedFeeAmount');
 park_at=trading.gas_owned_at(body->>'parkAt');return_at=trading.gas_owned_at(body->>'returnAt');
 fee_due=CASE WHEN body->>'feeDueAt' IS NULL THEN NULL ELSE trading.gas_owned_at(body->>'feeDueAt') END;
 IF fee<0 OR fee>=1e20 OR fee_due<'1901-01-01T00:00:00Z' OR fee_due>='2200-01-01T00:00:00Z'
 THEN RAISE EXCEPTION 'gas_custody_cash_capacity';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR context IS NULL OR(body->>'snapshotId')::uuid IS NULL
  OR valuation IS NULL OR NOT isfinite(valuation) OR body->>'profile' IS DISTINCT FROM 'physical_gas_park_custody_v1'
  OR body->>'convention' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4' OR body->>'knowledgeMode' IS DISTINCT FROM 'current'
  OR body->>'serviceKind' IS DISTINCT FROM 'park' OR body->>'perspective' NOT IN('customer','provider') OR quantity<=0 OR valuation>=park_at OR park_at>=return_at
  OR body->>'titlePolicy' IS DISTINCT FROM 'customer_title_retained_v1'
  OR body->>'returnPolicy' IS DISTINCT FROM 'same_point_same_basis_partial_custody_returns_v1'
  OR body->>'returnTiming' IS DISTINCT FROM 'accepted_custody_then_returns_v1'
  OR body->>'returnOrder' IS DISTINCT FROM 'nondecreasing_accepted_custody_return_instants_v1'
  OR body->>'usePolicy' IS DISTINCT FROM 'no_use_or_sale_of_custody_v1'
  OR body->>'lossPolicy' IS DISTINCT FROM 'unqualified_loss_requires_reconciliation_v1'
  OR body->>'physicalPolicy' IS DISTINCT FROM 'custody_only_no_inferred_flow_v1'
  OR body->>'planPolicy' IS DISTINCT FROM 'unaccepted_custody_plan_from_later_of_earliest_or_evaluation_v1'
  OR body->>'feeRecognition' IS DISTINCT FROM 'accepted_custody_v1' OR body->>'feeDirection' NOT IN('pay','receive')
  OR(fee=0)<>(fee_due IS NULL) OR fee_due<park_at OR security.trading_context_day_matches(catalog,context,valuation) IS NOT TRUE
  OR(body->>'perspective'='customer')<>(body->>'ownedAccountId' IS NOT NULL) THEN RAISE EXCEPTION 'gas_custody_input';END IF;
 IF security.reference_access((body->>'referenceCatalogId')::uuid,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid) IS NOT TRUE
  OR security.market_fact_visible(catalog,(body->>'definitionRevisionId')::uuid,NULL) IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM market_data.revisions r WHERE(r.catalog_id,r.id,r.reference_catalog_id,r.reference_revision_id)=
   (catalog,(body->>'definitionRevisionId')::uuid,(body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid))
 THEN RAISE EXCEPTION 'gas_custody_source';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,valuation,(body->>'definitionRevisionId')::uuid);
 SELECT * INTO point FROM ref_data.pipeline_delivery_points p WHERE(p.catalog_id,p.revision_id,p.point_code)=
  ((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'point');
 SELECT * INTO quantity_spec FROM ref_data.quantity_specifications q WHERE(q.catalog_id,q.revision_id,q.code)=
  ((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'quantitySpecification');
 IF point.point_code IS NULL OR quantity_spec.code IS NULL OR NOT EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions d
  WHERE(d.catalog_id,d.revision_id,d.pipeline_code,d.zone_code,d.point_code,d.product_code)=
   (point.catalog_id,point.revision_id,point.pipeline_code,point.zone_code,point.point_code,quantity_spec.commodity_code))
 THEN RAISE EXCEPTION 'gas_custody_point_quantity';END IF;
 IF body->>'perspective'='customer' THEN
  PERFORM security.require_gas_owned(catalog,false,false);deadline=least(deadline,inventory_deadline);
  SELECT * INTO owner FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,(body->>'ownedAccountId')::uuid);
  IF owner.id IS NULL OR owner.kind<>'owned_inventory' OR(owner.book_id,owner.legal_entity_id,owner.legal_entity_revision,owner.reference_catalog_id,owner.reference_revision_id,owner.location_code,owner.quantity_code)
   IS DISTINCT FROM((body->>'bookId')::uuid,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,
    point.catalog_id,point.revision_id,point.point_code,quantity_spec.code)
   OR NOT trading.gas_owned_visible(catalog,owner.publication_order,owner.effective_at,context,valuation) THEN RAISE EXCEPTION 'gas_custody_account';END IF;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.legal_entities p WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer)
   AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,valuation)) OR NOT EXISTS(
  SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer)
   AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,valuation)) THEN RAISE EXCEPTION 'gas_custody_context';END IF;
 PERFORM security.require_trade_parties(catalog,jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date,
  'legalEntityId',body->'legalEntityId','legalEntityRevision',body->'legalEntityRevision','counterpartyId',body->'counterpartyId','counterpartyRevision',body->'counterpartyRevision'),valuation);
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,valuation);
 INSERT INTO trading.gas_park_custody_instruments VALUES(catalog,instrument,(body->>'definitionRevisionId')::uuid,point.catalog_id,point.revision_id,
  point.point_code,point.pipeline_code,point.zone_code,quantity_spec.commodity_code,quantity_spec.code,body->>'currency',body->>'serviceKind',body->>'perspective',park_at,return_at,
  body->>'titlePolicy',body->>'returnPolicy',body->>'returnTiming',body->>'returnOrder',body->>'usePolicy',body->>'lossPolicy',body->>'physicalPolicy',body->>'planPolicy',
  fee,body->>'feeDirection',fee_due,body->>'feeRecognition',publication,valuation);
 PERFORM security.stamp_trade_booking(catalog);
 INSERT INTO trading.trades VALUES(catalog,trade,(body->>'bookId')::uuid,operation,actor,body,publication,valuation);
 INSERT INTO trading.gas_park_custody_trades VALUES(catalog,trade,1,instrument,(body->>'bookId')::uuid,operation,actor,quantity,(body->>'ownedAccountId')::uuid,
  (body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,publication,valuation,deadline);
 SET CONSTRAINTS trading.custody_booking IMMEDIATE;
 SET CONSTRAINTS trading.custody_booking DEFERRED;
 PERFORM settlement_data.require_futures_margin_preview(catalog,deadline);
 IF security.trading_access(catalog,'BookTrade') IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('tradeId',trade,'instrumentId',instrument,'operationId',operation,'revision',1);
END$$;

CREATE FUNCTION trading.gas_park_custody_terms_document(t trading.gas_park_custody_instruments) RETURNS jsonb
 LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('definitionRevisionId',t.definition_revision_id,'referenceCatalogId',t.reference_catalog_id,'referenceRevisionId',t.reference_revision_id,
  'point',t.point_code,'quantity',t.quantity_code,'currency',t.currency_code,'pipeline',t.pipeline_code,'zone',t.zone_code,'commodity',t.commodity_code,
  'serviceKind',t.service_kind,'perspective',t.perspective,'parkAt',t.park_at,'returnAt',t.return_at,'titlePolicy',t.title_policy,
  'returnPolicy',t.return_policy,'returnTiming',t.return_timing,'returnOrder',t.return_order,'usePolicy',t.use_policy,'lossPolicy',t.loss_policy,
  'physicalPolicy',t.physical_policy,'planPolicy',t.plan_policy,'fixedFeeAmount',trim_scale(t.fixed_fee_amount)::text,'feeDirection',t.fee_direction,
  'feeDueAt',t.fee_due_at,'feeRecognition',t.fee_recognition);
$$;

CREATE FUNCTION trading.gas_park_custody_head(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS integer
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT max(h.publication_order) FROM(
  SELECT t.publication_order,t.effective_at FROM trading.gas_park_custody_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,trade)
  UNION ALL SELECT e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z' FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.trade_id)=(catalog,trade)
  UNION ALL SELECT a.publication_order,a.effective_at FROM trading.gas_park_custody_acceptances a WHERE(a.catalog_id,a.trade_id)=(catalog,trade)
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.gas_park_custody_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  UNION ALL SELECT p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z' FROM settlement_data.cash_payment_revisions p
   JOIN trading.gas_park_custody_cash_members c ON(c.catalog_id,c.trade_id,c.binding_id)=(p.catalog_id,p.trade_id,p.binding_id)
   WHERE(c.catalog_id,c.trade_id)=(catalog,trade)
 )h WHERE trading.gas_owned_visible(catalog,h.publication_order,h.effective_at,context,as_of);
$$;

CREATE FUNCTION trading.gas_park_custody_reservations(catalog uuid,trade uuid,context uuid,as_of timestamptz)
 RETURNS TABLE(lot_id uuid,reservation_id uuid,quantity numeric,effective_at timestamptz,publication_order integer)
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT r.lot_id,r.id,r.quantity,r.effective_at,r.publication_order FROM trading.gas_owned_reservations r JOIN(
  SELECT l.reservation_id FROM trading.gas_park_custody_lots l JOIN trading.gas_park_custody_acceptances a ON(a.catalog_id,a.trade_id)=(l.catalog_id,l.trade_id)
   WHERE(l.catalog_id,l.trade_id)=(catalog,trade) AND trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of)
  UNION ALL SELECT l.remainder_reservation_id FROM trading.gas_park_custody_return_lots l JOIN trading.gas_park_custody_returns h ON(h.catalog_id,h.id)=(l.catalog_id,l.return_id)
   WHERE(h.catalog_id,h.trade_id)=(catalog,trade) AND l.remainder_reservation_id IS NOT NULL AND trading.gas_owned_visible(catalog,h.publication_order,h.effective_at,context,as_of)
 )linked ON linked.reservation_id=r.id WHERE r.catalog_id=catalog AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of)
  AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_releases x WHERE(x.catalog_id,x.reservation_id)=(catalog,r.id)
   AND trading.gas_owned_visible(catalog,x.publication_order,x.effective_at,context,as_of));
$$;

CREATE FUNCTION trading.gas_park_custody_evidence_document(e trading.gas_park_custody_evidence) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',e.id,'sourceKey',e.source_key,'revision',e.revision,'previousId',e.previous_id,'kind',e.kind,
  'rawQuantity',trim_scale(e.quantity)::text,'observedAt',e.observed_at,'evidence',e.evidence,'publicationOrder',e.publication_order,
  'knownAt',(SELECT k.known_from FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)));
$$;

CREATE FUNCTION security.read_gas_park_custody(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;inventory_deadline timestamptz;binding trading.gas_park_custody_trades;terms trading.gas_park_custody_instruments;
 acceptance trading.gas_park_custody_acceptances;observations jsonb;heads jsonb;returns jsonb;reserved jsonb;cash jsonb;policies jsonb;
 quantity_returned numeric;held numeric;result jsonb;BEGIN
 inventory_deadline=security.authority_until('ReadGasOwnedHoldings');deadline=security.gas_park_custody_authority(catalog,false,false);
 PERFORM security.require_gas_park_loan_worker_scope(catalog,context,as_of,NULL,NULL,trade);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,deadline);
 SELECT * INTO binding FROM trading.gas_park_custody_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,trade)
  AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of);
 IF binding.trade_id IS NULL THEN RAISE EXCEPTION 'gas_custody_source';END IF;
 SELECT * INTO STRICT terms FROM trading.gas_park_custody_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,binding.instrument_id);
 IF terms.perspective='customer' THEN deadline=least(deadline,inventory_deadline);PERFORM security.require_gas_owned_read(catalog,context,as_of,deadline);END IF;
 IF security.reference_access(terms.reference_catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(terms.reference_catalog_id,terms.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,terms.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,terms.definition_revision_id);
 IF(SELECT count(*) FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.trade_id)=(catalog,trade)
   AND trading.gas_owned_visible(catalog,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of))>16384
  OR(SELECT count(*) FROM trading.gas_park_custody_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
   AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of))>16384 THEN RAISE EXCEPTION 'gas_custody_scope';END IF;
 SELECT coalesce(jsonb_agg(trading.gas_park_custody_evidence_document(e) ORDER BY e.source_key,e.revision),'[]') INTO observations
  FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.trade_id)=(catalog,trade)
   AND trading.gas_owned_visible(catalog,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of);
 SELECT coalesce(jsonb_agg(trading.gas_park_custody_evidence_document(e) ORDER BY e.source_key),'[]') INTO heads FROM(
  SELECT DISTINCT ON(e.source_key)e.* FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.trade_id)=(catalog,trade)
   AND trading.gas_owned_visible(catalog,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of) ORDER BY e.source_key,e.revision DESC)e;
 SELECT * INTO acceptance FROM trading.gas_park_custody_acceptances a WHERE(a.catalog_id,a.trade_id)=(catalog,trade)
  AND trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of);
 SELECT coalesce(sum(r.quantity),0),coalesce(jsonb_agg(jsonb_build_object('id',r.id,'acceptanceId',acceptance.id,'businessKey',r.business_key,
  'quantity',trim_scale(r.quantity)::text,'effectiveAt',r.effective_at,'knownAt',k.known_from,'evidenceId',r.evidence_id,
  'evidenceSourceKey',r.evidence_source_key,'evidenceRevision',e.revision,'policyId',r.policy_id,'policyRevision',r.policy_revision,
  'allocations',(SELECT coalesce(jsonb_agg(jsonb_build_object('lotId',l.lot_id,'reservationId',l.released_reservation_id,'quantity',trim_scale(l.quantity)::text,
   'remainderReservationId',l.remainder_reservation_id,'remainderQuantity',trim_scale(coalesce(n.quantity,0))::text) ORDER BY l.lot_id),'[]')
   FROM trading.gas_park_custody_return_lots l LEFT JOIN trading.gas_owned_reservations n ON(n.catalog_id,n.id)=(l.catalog_id,l.remainder_reservation_id)
   WHERE(l.catalog_id,l.return_id)=(catalog,r.id))) ORDER BY r.effective_at,r.publication_order),'[]') INTO quantity_returned,returns
  FROM trading.gas_park_custody_returns r JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
  JOIN trading.gas_park_custody_evidence e ON(e.catalog_id,e.id)=(r.catalog_id,r.evidence_id)
  WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of);
 held=CASE WHEN acceptance.id IS NULL THEN 0 ELSE binding.quantity-quantity_returned END;
 SELECT coalesce(jsonb_agg(jsonb_build_object('lotId',r.lot_id,'reservationId',r.reservation_id,'quantity',trim_scale(r.quantity)::text,
  'effectiveAt',r.effective_at,'knownAt',k.known_from) ORDER BY r.lot_id),'[]') INTO reserved
  FROM trading.gas_park_custody_reservations(catalog,trade,context,as_of)r JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,r.publication_order);
 SELECT jsonb_build_object('bindingId',c.binding_id,'instrumentId',c.instrument_id,'amount',trim_scale(c.amount)::text,'currency',i.currency_code,
  'referenceCatalogId',i.reference_catalog_id,'referenceRevisionId',i.reference_revision_id,'dueAt',i.payment_at,'effectiveAt',m.effective_at,
  'payments',(SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',p.trade_id,'binding_id',p.binding_id,'book_id',m.book_id,
   'reference_catalog_id',i.reference_catalog_id,'reference_revision_id',i.reference_revision_id,'currency_code',i.currency_code,
   'allocation_id',p.allocation_id,'revision',p.revision,'paid_at',p.paid_at,'amount',trim_scale(p.amount)::text,'known_at',k.known_from)
   ORDER BY p.allocation_id,p.revision),'[]') FROM settlement_data.cash_payment_revisions p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE(p.catalog_id,p.trade_id,p.binding_id)=(catalog,trade,c.binding_id) AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of))) INTO cash
  FROM trading.gas_park_custody_cash_members c JOIN trading.cash_payment_memberships m ON(m.catalog_id,m.trade_id,m.binding_id)=(c.catalog_id,c.trade_id,c.binding_id)
  JOIN trading.cash_payment_instruments i ON(i.catalog_id,i.instrument_id)=(c.catalog_id,c.instrument_id)
  WHERE(c.catalog_id,c.trade_id)=(catalog,trade) AND trading.gas_owned_visible(catalog,m.publication_order,m.effective_at,context,as_of);
 SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.action,p.id),'[]') INTO policies FROM(
  SELECT DISTINCT ON(p.id)p.* FROM lifecycle.action_policies p WHERE p.catalog_id=catalog AND p.action IN('gas.park_custody.accept','gas.park_custody.return')
   AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of) ORDER BY p.id,p.revision DESC)p;
 result=jsonb_build_object('implementation','physical_gas_park_custody_v1','tradeId',trade,'instrumentId',binding.instrument_id,'revision',1,
  'bookId',binding.book_id,'quantity',trim_scale(binding.quantity)::text,'ownedAccountId',binding.owned_account_id,'operationId',binding.operation_id,
  'publicationOrder',binding.publication_order,'effectiveAt',binding.effective_at,
  'legalEntityId',binding.legal_entity_id,'legalEntityRevision',binding.legal_entity_revision,'counterpartyId',binding.counterparty_id,'counterpartyRevision',binding.counterparty_revision,
  'booking',(SELECT to_jsonb(f) FROM trading.booking_facts f WHERE(f.catalog_id,f.trade_id)=(catalog,trade)),
  'tradeReference',(SELECT reference_number FROM trading.trade_references WHERE(catalog_id,trade_id)=(catalog,trade)),
  'instrumentReference',(SELECT reference_number FROM trading.instrument_references WHERE(catalog_id,instrument_id)=(catalog,binding.instrument_id)),
  'contextId',context,'asOf',as_of,'knownAt',(SELECT known_at FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context)),
  'effectiveOn',(SELECT effective_on FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context)),'authorityUntil',deadline,
  'stateVersion',trading.gas_park_custody_head(catalog,trade,context,as_of),'terms',trading.gas_park_custody_terms_document(terms),
  'evidence',observations,'evidenceHeads',heads,'acceptance',CASE WHEN acceptance.id IS NULL THEN 'null'::jsonb ELSE
   jsonb_build_object('id',acceptance.id,'quantity',trim_scale(binding.quantity)::text,'effectiveAt',acceptance.effective_at,
    'knownAt',(SELECT known_from FROM trading.knowledge_receipts WHERE(catalog_id,publication_order)=(catalog,acceptance.publication_order)),
    'agreementEvidenceId',acceptance.agreement_evidence_id,'agreementSourceKey',acceptance.agreement_source_key,
    'agreementRevision',(SELECT revision FROM trading.gas_park_custody_evidence WHERE(catalog_id,id)=(catalog,acceptance.agreement_evidence_id)),
    'receiptEvidenceId',acceptance.receipt_evidence_id,'receiptSourceKey',acceptance.receipt_source_key,
    'receiptRevision',(SELECT revision FROM trading.gas_park_custody_evidence WHERE(catalog_id,id)=(catalog,acceptance.receipt_evidence_id)),
    'policyId',acceptance.policy_id,'policyRevision',acceptance.policy_revision,'allocations',(SELECT coalesce(jsonb_agg(jsonb_build_object('lotId',l.lot_id,
     'reservationId',l.reservation_id,'quantity',trim_scale(l.quantity)::text) ORDER BY l.lot_id),'[]') FROM trading.gas_park_custody_lots l WHERE(l.catalog_id,l.trade_id)=(catalog,trade))) END,
  'returns',returns,'reservations',reserved,'cash',coalesce(cash,'null'::jsonb),
  'balances',jsonb_build_object('inCustody',trim_scale(held)::text,'reserved',CASE WHEN terms.perspective='customer' THEN trim_scale(held)::text ELSE '0' END,
   'overdue',CASE WHEN as_of>=terms.return_at THEN trim_scale(held)::text ELSE '0' END,'ownedQuantityChange','0'),
  'reconciliationRequired',EXISTS(SELECT 1 FROM jsonb_array_elements(heads)h JOIN trading.gas_park_custody_evidence old ON(old.catalog_id,old.trade_id,old.source_key)=(catalog,trade,h->>'sourceKey')
   WHERE(h->>'revision')::integer>old.revision AND(old.id IN(acceptance.agreement_evidence_id,acceptance.receipt_evidence_id)
    OR old.id IN(SELECT r.evidence_id FROM trading.gas_park_custody_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
      AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of)))),
  'policies',policies,'nativePolicies',coalesce((SELECT jsonb_agg(to_jsonb(p) ORDER BY p.action) FROM(
   SELECT DISTINCT ON(p.action)p.* FROM trading.gas_owned_action_bindings p WHERE(p.catalog_id,p.account_id)=(catalog,binding.owned_account_id)
    AND p.action IN('gas.owned.reserve','gas.owned.release') AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of) ORDER BY p.action,p.revision DESC)p),'[]'));
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'gas_custody_scope';END IF;
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,deadline);
 IF terms.perspective='customer' THEN PERFORM security.require_gas_owned_read(catalog,context,as_of,deadline);END IF;
 RETURN result;
END$$;

CREATE FUNCTION trading.require_gas_park_custody_source(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE terms trading.gas_park_custody_instruments;BEGIN
 SELECT i.* INTO terms FROM trading.gas_park_custody_trades t JOIN trading.gas_park_custody_instruments i ON(i.catalog_id,i.instrument_id)=(t.catalog_id,t.instrument_id)
  WHERE(t.catalog_id,t.trade_id)=(catalog,trade) AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of);
 IF terms.instrument_id IS NULL OR security.trading_context_day_matches(catalog,context,as_of) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_context';END IF;
 IF security.reference_access(terms.reference_catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(terms.reference_catalog_id,terms.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,terms.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,terms.definition_revision_id);
END$$;

CREATE FUNCTION security.gas_park_custody_command_receipt(catalog uuid,operation uuid,trade uuid,action text,body jsonb,as_of timestamptz,context uuid,policy uuid,policy_revision integer)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;actor uuid;prior trading.gas_park_custody_commands;canonical jsonb;terms trading.gas_park_custody_instruments;BEGIN
 deadline=security.gas_park_custody_authority(catalog,true,false);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 canonical=jsonb_build_object('tradeId',trade,'action',action,'body',trading.gas_park_custody_fact(body),'asOf',as_of,'contextId',context,'policyId',policy,'policyRevision',policy_revision);
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_park_custody:'||catalog::text||':'||operation::text,0));
 SELECT * INTO prior FROM trading.gas_park_custody_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF prior.operation_id IS NULL THEN RETURN NULL;END IF;
 IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'gas_custody_conflict';END IF;
 SELECT i.* INTO STRICT terms FROM trading.gas_park_custody_trades t JOIN trading.gas_park_custody_instruments i ON(i.catalog_id,i.instrument_id)=(t.catalog_id,t.instrument_id)
  WHERE(t.catalog_id,t.trade_id)=(catalog,trade);
 IF security.reference_access(terms.reference_catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(terms.reference_catalog_id,terms.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,terms.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_park_custody_evidence_commit(catalog,trade,deadline);RETURN prior.outcome;
END$$;

CREATE FUNCTION security.record_gas_park_custody_evidence(catalog uuid,operation uuid,trade uuid,body jsonb,as_of timestamptz,context uuid)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;actor uuid;canonical jsonb;prior trading.gas_park_custody_evidence;same_fact trading.gas_park_custody_evidence;
 raw_quantity numeric;observed timestamptz;known_at timestamptz;publication integer;id uuid=gen_random_uuid();outcome jsonb;BEGIN
 deadline=security.gas_park_custody_authority(catalog,true,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR trade IS NULL OR context IS NULL
  OR as_of IS NULL OR NOT isfinite(as_of) OR jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>16384 THEN RAISE EXCEPTION 'gas_custody_input';END IF;
 PERFORM trading.gas_park_loan_keys(body,ARRAY['sourceKey','expectedRevision','kind','quantity','observedAt','evidence'],ARRAY[]::text[]);
 body=trading.gas_park_custody_fact(body);
 canonical=jsonb_build_object('tradeId',trade,'action','evidence','body',body,'asOf',as_of,'contextId',context,'policyId',NULL,'policyRevision',NULL);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 outcome=security.gas_park_custody_command_receipt(catalog,operation,trade,'evidence',body,as_of,context,NULL,NULL);
 IF outcome IS NOT NULL THEN
  IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
  PERFORM security.gas_park_custody_authority(catalog,true,false);RETURN outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 PERFORM security.gas_park_custody_authority(catalog,true,false);
 PERFORM trading.require_gas_park_custody_source(catalog,trade,context,as_of);
 raw_quantity=trading.gas_owned_quantity(body->>'quantity');observed=trading.gas_owned_at(body->>'observedAt');
 SELECT t.known_at INTO STRICT known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF raw_quantity<0 OR observed>as_of OR observed>known_at OR body->>'kind' NOT IN('custody_agreement','custody_receipt','custody_return')
  OR length(btrim(body->>'sourceKey')) NOT BETWEEN 1 AND 128 OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 2000 THEN RAISE EXCEPTION 'gas_custody_evidence';END IF;
 SELECT * INTO prior FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.trade_id,e.source_key)=(catalog,trade,body->>'sourceKey') ORDER BY e.revision DESC LIMIT 1;
 IF prior.id IS NOT NULL AND prior.kind IS DISTINCT FROM body->>'kind' THEN RAISE EXCEPTION 'gas_custody_evidence';END IF;
 SELECT * INTO same_fact FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.trade_id,e.source_key,e.kind,e.quantity,e.observed_at,e.evidence)
  =(catalog,trade,body->>'sourceKey',body->>'kind',raw_quantity,observed,body->>'evidence') ORDER BY e.revision LIMIT 1;
 IF same_fact.id IS NOT NULL THEN
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'instrumentId',(SELECT instrument_id FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(catalog,trade)),'action','evidence','id',same_fact.id,'revision',same_fact.revision,'changed',false);
 ELSE
  IF coalesce(prior.revision,0) IS DISTINCT FROM(body->>'expectedRevision')::integer THEN RAISE EXCEPTION 'gas_custody_stale';END IF;
  IF prior.id IS NOT NULL AND NOT trading.gas_owned_visible(catalog,prior.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of) THEN RAISE EXCEPTION 'gas_custody_context';END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_park_custody_evidence VALUES(catalog,id,operation,trade,body->>'sourceKey',coalesce(prior.revision,0)+1,prior.id,
   body->>'kind',raw_quantity,observed,body->>'evidence',context,actor,publication);
  outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'instrumentId',(SELECT instrument_id FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(catalog,trade)),'action','evidence','id',id,'revision',coalesce(prior.revision,0)+1,'changed',true);
 END IF;
 INSERT INTO trading.gas_park_custody_commands VALUES(catalog,operation,trade,actor,'evidence',canonical,outcome,deadline,NULL,NULL);
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 SET CONSTRAINTS trading.custody_fact IMMEDIATE;
 SET CONSTRAINTS trading.custody_fact DEFERRED;
 PERFORM security.gas_park_custody_authority(catalog,true,false);RETURN outcome;
END$$;

-- Read admissions retain an immutable source even after the write scope is restored.
CREATE FUNCTION security.require_gas_park_custody_read(catalog uuid,context uuid,as_of timestamptz,until_at timestamptz,inventory boolean) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;day date;BEGIN
 PERFORM security.gas_park_custody_authority(catalog,false,inventory);
 IF inventory IS NULL OR until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT q FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user;
 PERFORM security.require_gas_park_loan_worker_scope(catalog,context,as_of,NULL,NULL,NULL);
 IF(q.market_catalog_id,q.market_context_id,q.trading_as_of) IS NOT DISTINCT FROM(catalog,context,as_of) THEN
  PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);
 ELSIF(q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of) IS NOT DISTINCT FROM(catalog,context,as_of) THEN
  SELECT effective_on INTO STRICT day FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context);
  PERFORM security.require_admitted_business_source(catalog,context,as_of,day,q.business_source_until);
 ELSE RAISE EXCEPTION 'gas_custody_context' USING ERRCODE='42501';END IF;
 PERFORM security.gas_park_custody_authority(catalog,false,inventory);
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.require_gas_park_custody_commit(catalog uuid,trade uuid,base_until timestamptz,inventory_until timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE terms trading.gas_park_custody_instruments;BEGIN
 PERFORM security.gas_park_custody_authority(catalog,true,false);
 SELECT i.* INTO terms FROM trading.gas_park_custody_trades t JOIN trading.gas_park_custody_instruments i ON(i.catalog_id,i.instrument_id)=(t.catalog_id,t.instrument_id)
  WHERE(t.catalog_id,t.trade_id)=(catalog,trade);
 IF terms.instrument_id IS NULL THEN RAISE EXCEPTION 'gas_custody_source';END IF;
 IF terms.perspective='customer' THEN
  PERFORM security.gas_park_custody_authority(catalog,true,true);
  IF inventory_until IS NULL OR NOT isfinite(inventory_until) OR clock_timestamp()>=inventory_until THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 END IF;
 IF base_until IS NULL OR NOT isfinite(base_until) OR clock_timestamp()>=base_until THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.gas_park_custody_booking_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;inventory_deadline timestamptz;actor uuid;t trading.gas_park_custody_trades;i trading.gas_park_custody_instruments;BEGIN
 inventory_deadline=security.authority_until('ReadGasOwnedHoldings');deadline=security.gas_park_custody_booking_authority(catalog);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO t FROM trading.gas_park_custody_trades b WHERE(b.catalog_id,b.operation_id,b.actor_id)=(catalog,operation,actor);
 IF t.trade_id IS NULL THEN RETURN jsonb_build_object('found',false,'authorityUntil',deadline,'inventory',false);END IF;
 SELECT * INTO STRICT i FROM trading.gas_park_custody_instruments WHERE(catalog_id,instrument_id)=(catalog,t.instrument_id);
 IF i.perspective='customer' THEN deadline=least(deadline,inventory_deadline);PERFORM security.require_gas_owned(catalog,false,false);END IF;
 IF security.reference_access(i.reference_catalog_id,'ReadMarketData') IS NOT TRUE OR security.reference_version_visible(i.reference_catalog_id,i.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,i.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 PERFORM security.gas_park_custody_booking_authority(catalog);
 IF i.perspective='customer' THEN PERFORM security.require_gas_owned(catalog,false,false);END IF;
 IF deadline IS NULL OR clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('found',true,'authorityUntil',deadline,'inventory',i.perspective='customer','request',(SELECT request FROM trading.trades WHERE(catalog_id,id)=(catalog,t.trade_id)),
  'outcome',jsonb_build_object('tradeId',t.trade_id,'instrumentId',t.instrument_id,'revision',1,'operationId',operation));
END$$;

CREATE FUNCTION security.require_gas_park_custody_booking_status(catalog uuid,operation uuid,until_at timestamptz,inventory boolean,receipt_found boolean) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;t trading.gas_park_custody_trades;i trading.gas_park_custody_instruments;BEGIN
 PERFORM security.gas_park_custody_booking_authority(catalog);
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 IF inventory IS NULL OR receipt_found IS NULL OR NOT receipt_found AND inventory THEN RAISE EXCEPTION 'gas_custody_input';END IF;
 -- An absence admission never gains the authority of a receipt which appears later.
 IF NOT receipt_found THEN RETURN;END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO t FROM trading.gas_park_custody_trades b WHERE(b.catalog_id,b.operation_id,b.actor_id)=(catalog,operation,actor);
 IF t.trade_id IS NULL THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO STRICT i FROM trading.gas_park_custody_instruments WHERE(catalog_id,instrument_id)=(catalog,t.instrument_id);
 IF inventory IS DISTINCT FROM(i.perspective='customer') THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 IF inventory THEN PERFORM security.require_gas_owned(catalog,false,false);END IF;
 IF security.reference_access(i.reference_catalog_id,'ReadMarketData') IS NOT TRUE OR security.reference_version_visible(i.reference_catalog_id,i.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,i.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.gas_park_custody_directory(catalog uuid,context uuid,as_of timestamptz,books uuid[],portfolios uuid[],trades uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;inventory_deadline timestamptz;inventory boolean;result jsonb;BEGIN
 inventory_deadline=security.authority_until('ReadGasOwnedHoldings');deadline=security.gas_park_custody_authority(catalog,false,false);
 PERFORM security.require_gas_park_loan_worker_scope(catalog,context,as_of,books,trades,NULL);
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND cardinality(portfolios)<>0 THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_park_custody_read(catalog,context,as_of,deadline,false);
 IF books IS NULL OR portfolios IS NULL OR trades IS NULL OR cardinality(books)>128 OR cardinality(portfolios)>128 OR cardinality(trades)>4096
  OR array_position(books,NULL) IS NOT NULL OR array_position(portfolios,NULL) IS NOT NULL OR array_position(trades,NULL) IS NOT NULL
  OR(SELECT count(DISTINCT b) FROM unnest(books)b)<>cardinality(books) OR(SELECT count(DISTINCT p) FROM unnest(portfolios)p)<>cardinality(portfolios)
  OR(SELECT count(DISTINCT t) FROM unnest(trades)t)<>cardinality(trades) THEN RAISE EXCEPTION 'gas_custody_scope';END IF;
 IF(SELECT count(*) FROM trading.gas_park_custody_trades t JOIN trading.books b ON(b.catalog_id,b.id)=(t.catalog_id,t.book_id)
  WHERE t.catalog_id=catalog AND(cardinality(books)=0 OR t.book_id=ANY(books)) AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios))
   AND(cardinality(trades)=0 OR t.trade_id=ANY(trades)) AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of))>256 THEN RAISE EXCEPTION 'gas_custody_scope';END IF;
 SELECT coalesce(jsonb_agg(security.read_gas_park_custody(catalog,t.trade_id,context,as_of) ORDER BY t.trade_id),'[]') INTO result
  FROM trading.gas_park_custody_trades t JOIN trading.books b ON(b.catalog_id,b.id)=(t.catalog_id,t.book_id)
  WHERE t.catalog_id=catalog AND(cardinality(books)=0 OR t.book_id=ANY(books)) AND(cardinality(portfolios)=0 OR b.portfolio_id=ANY(portfolios))
   AND(cardinality(trades)=0 OR t.trade_id=ANY(trades)) AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of);
 inventory=EXISTS(SELECT 1 FROM jsonb_array_elements(result)r WHERE r#>>'{terms,perspective}'='customer');
 IF inventory THEN deadline=least(deadline,inventory_deadline);END IF;
 SELECT coalesce(jsonb_agg(jsonb_set(row,'{authorityUntil}',to_jsonb(least((row->>'authorityUntil')::timestamptz,deadline))) ORDER BY row->>'tradeId'),'[]') INTO result FROM jsonb_array_elements(result)row;
 PERFORM security.require_gas_park_custody_read(catalog,context,as_of,deadline,inventory);RETURN result;
END$$;

CREATE FUNCTION security.require_gas_park_custody_presence(catalog uuid,context uuid,as_of timestamptz,books uuid[],trades uuid[],until_at timestamptz) RETURNS void
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.require_gas_park_loan_presence(catalog,context,as_of,books,trades,until_at);
$$;
CREATE FUNCTION security.require_gas_park_custody_scope(catalog uuid,context uuid,as_of timestamptz,books uuid[],trades uuid[]) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;present boolean;BEGIN
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'));
 PERFORM security.require_gas_park_custody_presence(catalog,context,as_of,books,trades,deadline);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,deadline);
 SELECT EXISTS(SELECT 1 FROM trading.gas_park_custody_trades t WHERE t.catalog_id=catalog AND t.book_id=ANY(books)
  AND(cardinality(trades)=0 OR t.trade_id=ANY(trades)) AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of)) INTO present;
 PERFORM security.require_gas_park_custody_presence(catalog,context,as_of,books,trades,deadline);RETURN present;
END$$;
CREATE FUNCTION security.gas_park_custody_projection_fence(catalog uuid,context uuid,as_of timestamptz,horizon timestamptz,books uuid[],trades uuid[]) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;present boolean;BEGIN
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'));
 PERFORM security.require_gas_park_custody_presence(catalog,context,as_of,books,trades,deadline);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,deadline);
 IF horizon IS NULL OR NOT isfinite(horizon) OR horizon<as_of THEN RAISE EXCEPTION 'gas_custody_scope';END IF;
 SELECT EXISTS(SELECT 1 FROM trading.gas_park_custody_trades t WHERE t.catalog_id=catalog AND t.book_id=ANY(books)
  AND(cardinality(trades)=0 OR t.trade_id=ANY(trades)) AND t.effective_at<=horizon
  AND trading.gas_owned_visible(catalog,t.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of)) INTO present;
 PERFORM security.require_gas_park_custody_presence(catalog,context,as_of,books,trades,deadline);RETURN present;
END$$;

CREATE FUNCTION trading.require_gas_park_custody_native_policy(catalog uuid,account uuid,selected_action text,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE current_binding trading.gas_owned_action_bindings;selected trading.gas_owned_action_bindings;p lifecycle.action_policies;BEGIN
 SELECT * INTO current_binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,selected_action) ORDER BY revision DESC LIMIT 1;
 SELECT * INTO selected FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,selected_action)
  AND trading.gas_owned_visible(catalog,b.publication_order,b.effective_at,context,as_of) ORDER BY revision DESC LIMIT 1;
 IF current_binding.policy_id IS NULL OR(current_binding.revision,current_binding.policy_id,current_binding.policy_revision)
  IS DISTINCT FROM(selected.revision,selected.policy_id,selected.policy_revision) THEN RAISE EXCEPTION 'gas_custody_policy_changed';END IF;
 p=lifecycle.require_policy(catalog,selected.policy_id,selected.policy_revision,selected_action,as_of,false);
 IF p.mode='automatic' OR EXISTS(SELECT 1 FROM lifecycle.action_policies n WHERE(n.catalog_id,n.id)=(catalog,p.id) AND n.revision>p.revision)
  OR NOT trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of) THEN RAISE EXCEPTION 'gas_custody_policy_changed';END IF;
END$$;

CREATE FUNCTION trading.require_gas_park_custody_evidence(catalog uuid,trade uuid,selected_evidence uuid,selected_kind text,expected_quantity numeric,effective timestamptz,context uuid,as_of timestamptz)
 RETURNS trading.gas_park_custody_evidence LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE e trading.gas_park_custody_evidence;BEGIN
 SELECT * INTO e FROM trading.gas_park_custody_evidence x WHERE(x.catalog_id,x.trade_id,x.id,x.kind)=(catalog,trade,selected_evidence,selected_kind);
 IF e.id IS NULL OR e.quantity<>expected_quantity OR(selected_kind='custody_agreement' AND e.observed_at>effective)
  OR(selected_kind<>'custody_agreement' AND e.observed_at<>effective) OR EXISTS(SELECT 1 FROM trading.gas_park_custody_evidence n
   WHERE(n.catalog_id,n.trade_id,n.source_key)=(catalog,trade,e.source_key) AND n.revision>e.revision)
  OR NOT trading.gas_owned_visible(catalog,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of)
 THEN RAISE EXCEPTION 'gas_custody_evidence';END IF;
 RETURN e;
END$$;

CREATE FUNCTION trading.require_gas_park_custody_reconciled(catalog uuid,trade uuid) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM trading.gas_park_custody_evidence old JOIN trading.gas_park_custody_evidence newer
  ON(newer.catalog_id,newer.trade_id,newer.source_key)=(old.catalog_id,old.trade_id,old.source_key) AND newer.revision>old.revision
  WHERE(old.catalog_id,old.trade_id)=(catalog,trade) AND(old.id IN(SELECT agreement_evidence_id FROM trading.gas_park_custody_acceptances WHERE(catalog_id,trade_id)=(catalog,trade))
   OR old.id IN(SELECT receipt_evidence_id FROM trading.gas_park_custody_acceptances WHERE(catalog_id,trade_id)=(catalog,trade))
   OR old.id IN(SELECT evidence_id FROM trading.gas_park_custody_returns WHERE(catalog_id,trade_id)=(catalog,trade)))) THEN RAISE EXCEPTION 'gas_custody_correction_required';END IF;
END$$;

CREATE FUNCTION security.record_gas_park_custody(catalog uuid,operation uuid,trade uuid,selected_action text,body jsonb,as_of timestamptz,context uuid,policy uuid,selected_policy_revision integer,preview boolean)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;inventory_deadline timestamptz;actor uuid;canonical jsonb;outcome jsonb;same_fact trading.gas_park_custody_commands;
 binding trading.gas_park_custody_trades;terms trading.gas_park_custody_instruments;a trading.gas_park_custody_acceptances;p lifecycle.action_policies;
 e trading.gas_park_custody_evidence;agreement trading.gas_park_custody_evidence;effective timestamptz;known_at timestamptz;
 amount numeric;held numeric;returned numeric;applied_quantity numeric;publication integer;id uuid=gen_random_uuid();member uuid;cash uuid;
 entry jsonb;native jsonb;reserve trading.gas_owned_reservations;new_reservation uuid;remaining numeric;allocations jsonb='[]';BEGIN
 inventory_deadline=least(security.authority_until('ReadGasOwnedHoldings'),security.authority_until('RecordGasOwnedHoldings'));
 deadline=security.gas_park_custody_authority(catalog,true,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR trade IS NULL OR context IS NULL OR policy IS NULL OR selected_policy_revision IS NULL
  OR as_of IS NULL OR NOT isfinite(as_of) OR preview IS NULL OR selected_action IS NULL OR selected_action NOT IN('accept','return') OR jsonb_typeof(body) IS DISTINCT FROM 'object'
  OR pg_column_size(body)>65536 THEN RAISE EXCEPTION 'gas_custody_input';END IF;
 IF preview THEN
  BEGIN
   outcome=security.record_gas_park_custody(catalog,operation,trade,selected_action,body,as_of,context,policy,selected_policy_revision,false);
   RAISE EXCEPTION USING ERRCODE='GX001',DETAIL=outcome::text;
  EXCEPTION WHEN SQLSTATE 'GX001' THEN GET STACKED DIAGNOSTICS outcome=PG_EXCEPTION_DETAIL;RETURN outcome::jsonb;END;
 END IF;
 body=trading.gas_park_custody_fact(body);
 PERFORM trading.gas_park_loan_keys(body,CASE selected_action WHEN 'accept' THEN ARRAY['expectedVersion','agreementEvidenceId','receiptEvidenceId','effectiveAt','allocations']
  ELSE ARRAY['expectedVersion','businessKey','evidenceId','quantity','effectiveAt','allocations'] END,ARRAY[]::text[]);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 canonical=jsonb_build_object('tradeId',trade,'action',selected_action,'body',body,'asOf',as_of,'contextId',context,'policyId',policy,'policyRevision',selected_policy_revision);
 outcome=security.gas_park_custody_command_receipt(catalog,operation,trade,selected_action,body,as_of,context,policy,selected_policy_revision);
 IF outcome IS NOT NULL THEN PERFORM security.require_gas_park_custody_commit(catalog,trade,deadline,inventory_deadline);RETURN outcome;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_park_custody_commit(catalog,trade,deadline,inventory_deadline);
 SELECT * INTO STRICT binding FROM trading.gas_park_custody_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,trade);
 SELECT * INTO STRICT terms FROM trading.gas_park_custody_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,binding.instrument_id);
 IF terms.perspective='customer' THEN deadline=least(deadline,inventory_deadline);END IF;
 IF security.reference_access(terms.reference_catalog_id,'ReadMarketData') IS NOT TRUE OR security.reference_version_visible(terms.reference_catalog_id,terms.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,terms.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO same_fact FROM trading.gas_park_custody_commands c WHERE(c.catalog_id,c.trade_id,c.action)=(catalog,trade,selected_action)
  AND((c.request->'body')-'expectedVersion')=(body-'expectedVersion') ORDER BY c.operation_id LIMIT 1;
 IF same_fact.operation_id IS NOT NULL THEN
  PERFORM trading.require_gas_park_custody_source(catalog,trade,context,as_of);
  outcome=same_fact.outcome||jsonb_build_object('operationId',operation,'changed',false);
  INSERT INTO trading.gas_park_custody_commands VALUES(catalog,operation,trade,actor,selected_action,canonical,outcome,deadline,policy,selected_policy_revision);
  PERFORM security.require_gas_park_custody_commit(catalog,trade,deadline,inventory_deadline);RETURN outcome;
 END IF;
 PERFORM trading.require_gas_park_custody_source(catalog,trade,context,as_of);
 SELECT t.known_at INTO STRICT known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF jsonb_typeof(body->'expectedVersion') IS DISTINCT FROM 'number' OR(body->>'expectedVersion')::numeric NOT BETWEEN 1 AND 2147483647
  OR(body->>'expectedVersion')::numeric<>trunc((body->>'expectedVersion')::numeric) THEN RAISE EXCEPTION 'gas_custody_input';END IF;
 IF(body->>'expectedVersion')::integer IS DISTINCT FROM trading.gas_park_custody_head(catalog,trade,context,as_of)
  OR EXISTS(SELECT 1 FROM trading.gas_park_custody_evidence x WHERE(x.catalog_id,x.trade_id)=(catalog,trade)
   AND NOT trading.gas_owned_visible(catalog,x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of))
  OR EXISTS(SELECT 1 FROM trading.gas_park_custody_acceptances x WHERE(x.catalog_id,x.trade_id)=(catalog,trade)
   AND NOT trading.gas_owned_visible(catalog,x.publication_order,x.effective_at,context,as_of))
  OR EXISTS(SELECT 1 FROM trading.gas_park_custody_returns x WHERE(x.catalog_id,x.trade_id)=(catalog,trade)
   AND NOT trading.gas_owned_visible(catalog,x.publication_order,x.effective_at,context,as_of)) THEN RAISE EXCEPTION 'gas_custody_stale';END IF;
 PERFORM trading.require_gas_park_custody_reconciled(catalog,trade);
 p=lifecycle.require_policy(catalog,policy,selected_policy_revision,'gas.park_custody.'||selected_action,as_of,false);
 IF p.mode='automatic' OR EXISTS(SELECT 1 FROM lifecycle.action_policies n WHERE(n.catalog_id,n.id)=(catalog,policy) AND n.revision>selected_policy_revision)
  OR NOT trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of) THEN RAISE EXCEPTION 'gas_custody_policy_changed';END IF;
 effective=trading.gas_owned_at(body->>'effectiveAt');
 IF effective>as_of OR effective>known_at THEN RAISE EXCEPTION 'gas_custody_time';END IF;
 SELECT * INTO a FROM trading.gas_park_custody_acceptances x WHERE(x.catalog_id,x.trade_id)=(catalog,trade);
 SELECT coalesce(sum(r.quantity),0) INTO returned FROM trading.gas_park_custody_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade);
 held=CASE WHEN a.id IS NULL THEN 0 ELSE binding.quantity-returned END;
 IF jsonb_typeof(body->'allocations') IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
 IF terms.perspective='provider' AND body->'allocations'<>'[]'::jsonb THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
 IF terms.perspective='customer' THEN
  PERFORM trading.require_gas_owned_head(catalog,binding.owned_account_id,context,as_of);
  PERFORM trading.require_gas_park_custody_stock_source(catalog,trade,operation,context,as_of);
  IF selected_action='accept' OR EXISTS(SELECT 1 FROM jsonb_array_elements(body->'allocations')x
   JOIN trading.gas_park_custody_reservations(catalog,trade,context,as_of)r ON r.reservation_id=(x->>'reservationId')::uuid
   WHERE r.quantity>trading.gas_owned_quantity(x->>'quantity')) THEN
   PERFORM trading.require_gas_park_custody_native_policy(catalog,binding.owned_account_id,'gas.owned.reserve',context,as_of);
  END IF;
  IF selected_action='return' THEN PERFORM trading.require_gas_park_custody_native_policy(catalog,binding.owned_account_id,'gas.owned.release',context,as_of);END IF;
 END IF;
 IF selected_action='accept' THEN
  IF a.id IS NOT NULL OR effective<terms.park_at OR effective>=terms.return_at THEN RAISE EXCEPTION 'gas_custody_accept';END IF;
  applied_quantity=binding.quantity;
  agreement=trading.require_gas_park_custody_evidence(catalog,trade,(body->>'agreementEvidenceId')::uuid,'custody_agreement',applied_quantity,effective,context,as_of);
  e=trading.require_gas_park_custody_evidence(catalog,trade,(body->>'receiptEvidenceId')::uuid,'custody_receipt',applied_quantity,effective,context,as_of);
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_park_custody_acceptances VALUES(catalog,trade,id,operation,agreement.source_key,agreement.id,e.source_key,e.id,context,
   (body->>'expectedVersion')::integer,policy,selected_policy_revision,actor,publication,effective);
  IF terms.perspective='customer' THEN
   IF(SELECT coalesce(sum(trading.gas_owned_quantity(x->>'quantity')),0) FROM jsonb_array_elements(body->'allocations')x)<>applied_quantity
    OR(SELECT count(DISTINCT (x->>'lotId')::uuid) FROM jsonb_array_elements(body->'allocations')x)<>jsonb_array_length(body->'allocations') THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
   FOR entry IN SELECT value FROM jsonb_array_elements(body->'allocations') LOOP
    PERFORM trading.gas_owned_keys(entry,ARRAY['lotId','quantity']);
    SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
    native=trading.apply_gas_owned_financial(catalog,binding.owned_account_id,'reserve',jsonb_build_object('lotId',entry->'lotId','quantity',entry->'quantity',
     'businessKey','custody:'||trade::text||':'||id::text||':'||(entry->>'lotId'),'effectiveAt',body->'effectiveAt'),as_of,context,actor,publication);
    INSERT INTO trading.gas_park_custody_lots VALUES(catalog,trade,(entry->>'lotId')::uuid,(native->>'id')::uuid,trading.gas_owned_quantity(entry->>'quantity'));
    allocations=allocations||jsonb_build_array(jsonb_build_object('lotId',entry->'lotId','reservationId',native->'id','quantity',entry->'quantity',
     'remainderReservationId',NULL,'remainderQuantity','0'));
   END LOOP;
  END IF;
  amount=terms.fixed_fee_amount*CASE terms.fee_direction WHEN 'pay' THEN -1 ELSE 1 END;
  IF amount<>0 THEN
   cash=security.timed_cash_payment_instrument_id(terms.reference_catalog_id,terms.reference_revision_id,terms.currency_code,terms.fee_due_at);member=gen_random_uuid();
   IF NOT EXISTS(SELECT 1 FROM trading.cash_payment_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,cash)) THEN
    INSERT INTO trading.instruments VALUES(catalog,cash,publication,effective);
    INSERT INTO trading.cash_payment_instruments(catalog_id,instrument_id,reference_catalog_id,reference_revision_id,currency_code,payment_date,publication_order,effective_at,payment_at)
     VALUES(catalog,cash,terms.reference_catalog_id,terms.reference_revision_id,terms.currency_code,NULL,publication,effective,terms.fee_due_at);
   END IF;
   IF NOT EXISTS(SELECT 1 FROM trading.cash_payment_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,cash) AND i.effective_at<=effective) THEN RAISE EXCEPTION 'gas_custody_cash_origin';END IF;
   PERFORM security.register_cash_payment_membership(catalog,trade,member,cash,publication,effective);
   INSERT INTO trading.gas_park_custody_cash_members VALUES(catalog,trade,member,cash,amount);
  END IF;
 ELSE
  applied_quantity=trading.gas_owned_quantity(body->>'quantity');
  IF a.id IS NULL OR applied_quantity<=0 OR applied_quantity>held OR effective<=a.effective_at OR length(btrim(body->>'businessKey')) NOT BETWEEN 1 AND 128 THEN RAISE EXCEPTION 'gas_custody_return';END IF;
  IF EXISTS(SELECT 1 FROM trading.gas_park_custody_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND r.effective_at>effective) THEN RAISE EXCEPTION 'gas_custody_return_reconciliation_required';END IF;
  e=trading.require_gas_park_custody_evidence(catalog,trade,(body->>'evidenceId')::uuid,'custody_return',applied_quantity,effective,context,as_of);
  IF EXISTS(SELECT 1 FROM trading.gas_park_custody_returns r WHERE(r.catalog_id,r.trade_id,r.evidence_source_key)=(catalog,trade,e.source_key)) THEN RAISE EXCEPTION 'gas_custody_evidence_used';END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO trading.gas_park_custody_returns VALUES(catalog,id,operation,trade,body->>'businessKey',e.source_key,e.id,applied_quantity,context,(body->>'expectedVersion')::integer,policy,selected_policy_revision,actor,publication,effective);
  IF terms.perspective='customer' THEN
   IF(SELECT coalesce(sum(trading.gas_owned_quantity(x->>'quantity')),0) FROM jsonb_array_elements(body->'allocations')x)<>applied_quantity
    OR(SELECT count(DISTINCT (x->>'reservationId')::uuid) FROM jsonb_array_elements(body->'allocations')x)<>jsonb_array_length(body->'allocations') THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
   FOR entry IN SELECT value FROM jsonb_array_elements(body->'allocations') LOOP
    PERFORM trading.gas_owned_keys(entry,ARRAY['reservationId','quantity']);
    SELECT r.* INTO reserve FROM trading.gas_owned_reservations r JOIN trading.gas_park_custody_reservations(catalog,trade,context,as_of)s ON s.reservation_id=r.id
     WHERE(r.catalog_id,r.id)=(catalog,(entry->>'reservationId')::uuid);
    remaining=reserve.quantity-trading.gas_owned_quantity(entry->>'quantity');
    IF reserve.id IS NULL OR trading.gas_owned_quantity(entry->>'quantity')<=0 OR remaining<0 THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
    SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
    native=trading.apply_gas_owned_financial(catalog,binding.owned_account_id,'release',jsonb_build_object('reservationId',reserve.id,'effectiveAt',body->'effectiveAt'),as_of,context,actor,publication);
    new_reservation=NULL;
    IF remaining>0 THEN
     SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
     native=trading.apply_gas_owned_financial(catalog,binding.owned_account_id,'reserve',jsonb_build_object('lotId',reserve.lot_id,'quantity',trim_scale(remaining)::text,
      'businessKey','custody:'||trade::text||':'||id::text||':'||reserve.lot_id::text,'effectiveAt',body->'effectiveAt'),as_of,context,actor,publication);
     new_reservation=(native->>'id')::uuid;
    END IF;
    INSERT INTO trading.gas_park_custody_return_lots VALUES(catalog,id,reserve.lot_id,reserve.id,new_reservation,trading.gas_owned_quantity(entry->>'quantity'));
    allocations=allocations||jsonb_build_array(jsonb_build_object('lotId',reserve.lot_id,'reservationId',reserve.id,'quantity',entry->'quantity',
     'remainderReservationId',new_reservation,'remainderQuantity',trim_scale(remaining)::text));
   END LOOP;
  END IF;
  amount=0;
 END IF;
 outcome=jsonb_build_object('operationId',operation,'tradeId',trade,'instrumentId',binding.instrument_id,'id',id,'action',selected_action,'changed',true,'custodyBefore',trim_scale(held)::text,
  'custodyAfter',trim_scale(CASE selected_action WHEN 'accept' THEN applied_quantity ELSE held-applied_quantity END)::text,
  'reservedBefore',CASE terms.perspective WHEN 'customer' THEN trim_scale(held)::text ELSE '0' END,
  'reservedAfter',CASE terms.perspective WHEN 'customer' THEN trim_scale(CASE selected_action WHEN 'accept' THEN applied_quantity ELSE held-applied_quantity END)::text ELSE '0' END,
  'quantity',trim_scale(applied_quantity)::text,'ownedQuantityChange','0','feeAmount',trim_scale(amount)::text,'cashBindingId',member,'returnAt',terms.return_at,'allocations',allocations);
 INSERT INTO trading.gas_park_custody_commands VALUES(catalog,operation,trade,actor,selected_action,canonical,outcome,deadline,policy,selected_policy_revision);
 SET CONSTRAINTS trading.custody_fact,trading.custody_reservation,trading.owned_integrity IMMEDIATE;
 SET CONSTRAINTS trading.custody_fact,trading.custody_reservation,trading.owned_integrity DEFERRED;
 PERFORM security.require_gas_park_custody_commit(catalog,trade,deadline,inventory_deadline);RETURN outcome;
END$$;

CREATE FUNCTION security.require_gas_park_custody_evidence_commit(catalog uuid,trade uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE terms trading.gas_park_custody_instruments;BEGIN
 PERFORM security.gas_park_custody_authority(catalog,true,false);
 SELECT i.* INTO terms FROM trading.gas_park_custody_trades t JOIN trading.gas_park_custody_instruments i ON(i.catalog_id,i.instrument_id)=(t.catalog_id,t.instrument_id)
  WHERE(t.catalog_id,t.trade_id)=(catalog,trade);
 IF terms.instrument_id IS NULL THEN RAISE EXCEPTION 'gas_custody_source';END IF;
 IF security.reference_access(terms.reference_catalog_id,'ReadMarketData') IS NOT TRUE OR security.reference_version_visible(terms.reference_catalog_id,terms.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,terms.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
END$$;

-- Custody reservations never become authority to sell, consume or repay gas.
CREATE FUNCTION trading.require_gas_park_custody_reservation(catalog uuid,reservation uuid) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
BEGIN
 IF reservation IS NULL OR NOT(EXISTS(SELECT 1 FROM trading.gas_park_custody_lots l WHERE(l.catalog_id,l.reservation_id)=(catalog,reservation))
  OR EXISTS(SELECT 1 FROM trading.gas_park_custody_return_lots l WHERE(l.catalog_id,l.remainder_reservation_id)=(catalog,reservation))) THEN RETURN;END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.reservation_id)=(catalog,reservation))
  OR EXISTS(SELECT 1 FROM trading.gas_owned_consumption_quantities c WHERE(c.catalog_id,c.reservation_id)=(catalog,reservation))
  OR EXISTS(SELECT 1 FROM trading.gas_owned_releases x WHERE(x.catalog_id,x.reservation_id)=(catalog,reservation)
   AND NOT EXISTS(SELECT 1 FROM trading.gas_park_custody_return_lots l JOIN trading.gas_park_custody_returns r ON(r.catalog_id,r.id)=(l.catalog_id,l.return_id)
    WHERE(l.catalog_id,l.released_reservation_id)=(catalog,reservation) AND(x.effective_at,x.actor_id)=(r.effective_at,r.actor_id)))
 THEN RAISE EXCEPTION 'gas_custody_reservation_protected';END IF;
END$$;
CREATE FUNCTION trading.guard_gas_park_custody_reservation() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF TG_TABLE_NAME='gas_park_custody_return_lots' THEN
  PERFORM trading.require_gas_park_custody_reservation(NEW.catalog_id,NEW.released_reservation_id);
  PERFORM trading.require_gas_park_custody_reservation(NEW.catalog_id,NEW.remainder_reservation_id);
 ELSE PERFORM trading.require_gas_park_custody_reservation(NEW.catalog_id,NEW.reservation_id);END IF;
 RETURN NEW;
END$$;
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_owned_releases','gas_owned_consumptions','gas_owned_consumption_quantities','gas_park_custody_lots','gas_park_custody_return_lots'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER custody_reservation AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_park_custody_reservation()',name);
 END LOOP;
END$$;

CREATE FUNCTION security.gas_park_custody_trade_head(catalog uuid,trade uuid) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;present boolean;job risk.jobs;BEGIN
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  PERFORM security.require_gas_park_loan_worker_scope(catalog,job.context_id,job.valuation_at,NULL,NULL,trade);
 END IF;
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'));
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR deadline IS NULL OR clock_timestamp()>=deadline
 THEN RAISE EXCEPTION 'gas_park_custody_denied' USING ERRCODE='42501';END IF;
 SELECT EXISTS(SELECT 1 FROM trading.gas_park_custody_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,trade)
  AND security.trading_visible(catalog,t.publication_order,t.effective_at)) INTO present;
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR clock_timestamp()>=deadline
 THEN RAISE EXCEPTION 'gas_park_custody_denied' USING ERRCODE='42501';END IF;RETURN present;
END$$;

-- Risk selection needs only the certified product identity, never service terms
-- or native balances. The worker remains bound to its original exact selector.
CREATE FUNCTION security.risk_gas_park_custody_trade_heads(catalog uuid,books uuid[],trades uuid[]) RETURNS SETOF jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context uuid;as_of timestamptz;deadline timestamptz;result jsonb;BEGIN
 PERFORM risk.require_worker_attempt();
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'));
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR deadline IS NULL OR clock_timestamp()>=deadline
 THEN RAISE EXCEPTION 'gas_park_custody_denied' USING ERRCODE='42501';END IF;
 IF books IS NULL OR cardinality(books) NOT BETWEEN 1 AND 128 OR array_position(books,NULL) IS NOT NULL
  OR(SELECT count(DISTINCT b) FROM unnest(books)b)<>cardinality(books)
  OR trades IS NOT NULL AND(cardinality(trades) NOT BETWEEN 1 AND 256 OR array_position(trades,NULL) IS NOT NULL
   OR '00000000-0000-0000-0000-000000000000'::uuid=ANY(trades) OR(SELECT count(DISTINCT t) FROM unnest(trades)t)<>cardinality(trades))
 THEN RAISE EXCEPTION 'risk_trade_scope';END IF;
 SELECT c.market_context_id,c.trading_as_of INTO context,as_of FROM security.request_contexts c
  WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user;
 IF context IS NULL OR as_of IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 PERFORM security.require_gas_park_loan_worker_scope(catalog,context,as_of,books,coalesce(trades,ARRAY[]::uuid[]),NULL);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,deadline);
 SELECT coalesce(jsonb_agg(jsonb_build_object('trade_id',t.trade_id,'book_id',t.book_id,'revision',t.revision) ORDER BY t.trade_id),'[]'::jsonb)
  INTO result FROM trading.gas_park_custody_trades t JOIN trading.books b ON(b.catalog_id,b.id)=(t.catalog_id,t.book_id)
  JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
  WHERE t.catalog_id=catalog AND t.book_id=ANY(books) AND(trades IS NULL OR t.trade_id=ANY(trades))
   AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of)
   AND trading.gas_owned_visible(catalog,b.publication_order,b.effective_at,context,as_of)
   AND trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,deadline);
 PERFORM risk.require_worker_attempt();RETURN QUERY SELECT value FROM jsonb_array_elements(result);
END$$;


CREATE FUNCTION trading.gas_park_custody_recorded_outcome(catalog uuid,selected_action text,fact_id uuid) RETURNS jsonb
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE trade uuid;t trading.gas_park_custody_trades;i trading.gas_park_custody_instruments;
 e trading.gas_park_custody_evidence;a trading.gas_park_custody_acceptances;r trading.gas_park_custody_returns;
 held numeric;quantity numeric;amount numeric=0;member uuid;allocations jsonb;BEGIN
 IF selected_action='evidence' THEN
  SELECT * INTO STRICT e FROM trading.gas_park_custody_evidence WHERE(catalog_id,id)=(catalog,fact_id);trade=e.trade_id;
 ELSIF selected_action='accept' THEN
  SELECT * INTO STRICT a FROM trading.gas_park_custody_acceptances WHERE(catalog_id,id)=(catalog,fact_id);trade=a.trade_id;held=0;
 ELSE
  SELECT * INTO STRICT r FROM trading.gas_park_custody_returns WHERE(catalog_id,id)=(catalog,fact_id);trade=r.trade_id;
 END IF;
 SELECT * INTO STRICT t FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(catalog,trade);
 SELECT * INTO STRICT i FROM trading.gas_park_custody_instruments WHERE(catalog_id,instrument_id)=(catalog,t.instrument_id);
 IF selected_action='evidence' THEN RETURN jsonb_build_object('tradeId',trade,'instrumentId',t.instrument_id,'action',selected_action,'id',e.id,'revision',e.revision);END IF;
 IF selected_action='accept' THEN
  quantity=t.quantity;amount=i.fixed_fee_amount*CASE i.fee_direction WHEN 'pay' THEN -1 ELSE 1 END;
  SELECT binding_id INTO member FROM trading.gas_park_custody_cash_members WHERE(catalog_id,trade_id)=(catalog,trade);
  SELECT coalesce(jsonb_agg(jsonb_build_object('lotId',l.lot_id,'reservationId',l.reservation_id,'quantity',trim_scale(l.quantity)::text,
   'remainderReservationId',NULL,'remainderQuantity','0') ORDER BY l.lot_id),'[]') INTO allocations FROM trading.gas_park_custody_lots l WHERE(l.catalog_id,l.trade_id)=(catalog,trade);
 ELSE
  quantity=r.quantity;
  SELECT t.quantity-coalesce(sum(x.quantity),0) INTO held FROM trading.gas_park_custody_returns x WHERE(x.catalog_id,x.trade_id)=(catalog,trade) AND x.publication_order<r.publication_order;
  SELECT coalesce(jsonb_agg(jsonb_build_object('lotId',l.lot_id,'reservationId',l.released_reservation_id,'quantity',trim_scale(l.quantity)::text,
   'remainderReservationId',l.remainder_reservation_id,'remainderQuantity',trim_scale(coalesce(n.quantity,0))::text) ORDER BY l.lot_id),'[]') INTO allocations
   FROM trading.gas_park_custody_return_lots l LEFT JOIN trading.gas_owned_reservations n ON(n.catalog_id,n.id)=(l.catalog_id,l.remainder_reservation_id)
   WHERE(l.catalog_id,l.return_id)=(catalog,r.id);
 END IF;
 RETURN jsonb_build_object('tradeId',trade,'instrumentId',t.instrument_id,'action',selected_action,'id',fact_id,
  'custodyBefore',trim_scale(held)::text,'custodyAfter',trim_scale(CASE selected_action WHEN 'accept' THEN quantity ELSE held-quantity END)::text,
  'reservedBefore',CASE i.perspective WHEN 'customer' THEN trim_scale(held)::text ELSE '0' END,
  'reservedAfter',CASE i.perspective WHEN 'customer' THEN trim_scale(CASE selected_action WHEN 'accept' THEN quantity ELSE held-quantity END)::text ELSE '0' END,
  'quantity',trim_scale(quantity)::text,'ownedQuantityChange','0','feeAmount',trim_scale(amount)::text,'cashBindingId',member,'returnAt',i.return_at,'allocations',allocations);
END$$;

-- Exact typed members, not an outcome's JSON claim, establish accepted effects.
CREATE FUNCTION trading.gas_park_custody_recorded_body(catalog uuid,selected_action text,fact_id uuid) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT trading.gas_park_custody_fact(CASE selected_action
 WHEN 'evidence' THEN(SELECT jsonb_build_object('sourceKey',e.source_key,'expectedRevision',e.revision-1,'kind',e.kind,'quantity',trim_scale(e.quantity)::text,
  'observedAt',to_char(e.observed_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'evidence',e.evidence)
  FROM trading.gas_park_custody_evidence e WHERE(e.catalog_id,e.id)=(catalog,fact_id))
 WHEN 'accept' THEN(SELECT jsonb_build_object('expectedVersion',a.source_head,'agreementEvidenceId',a.agreement_evidence_id,'receiptEvidenceId',a.receipt_evidence_id,
  'effectiveAt',to_char(a.effective_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'allocations',(SELECT coalesce(jsonb_agg(jsonb_build_object('lotId',l.lot_id,'quantity',trim_scale(l.quantity)::text)),'[]') FROM trading.gas_park_custody_lots l
   WHERE(l.catalog_id,l.trade_id)=(a.catalog_id,a.trade_id))) FROM trading.gas_park_custody_acceptances a WHERE(a.catalog_id,a.id)=(catalog,fact_id))
 WHEN 'return' THEN(SELECT jsonb_build_object('expectedVersion',r.source_head,'businessKey',r.business_key,'evidenceId',r.evidence_id,'quantity',trim_scale(r.quantity)::text,
  'effectiveAt',to_char(r.effective_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'allocations',(SELECT coalesce(jsonb_agg(jsonb_build_object('reservationId',l.released_reservation_id,'quantity',trim_scale(l.quantity)::text)),'[]') FROM trading.gas_park_custody_return_lots l
   WHERE(l.catalog_id,l.return_id)=(r.catalog_id,r.id))) FROM trading.gas_park_custody_returns r WHERE(r.catalog_id,r.id)=(catalog,fact_id)) END);
$$;

CREATE FUNCTION trading.require_gas_park_custody_stock_source(catalog uuid,trade uuid,operation uuid,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE account uuid;BEGIN
 SELECT owned_account_id INTO STRICT account FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(catalog,trade);
 PERFORM trading.require_gas_owned_quantity_head(catalog,account,context,as_of);
 IF EXISTS(SELECT 1 FROM(
  SELECT a.publication_order,'source'::text kind,NULL::uuid id FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account)
  UNION ALL SELECT e.publication_order,'source',NULL FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id)=(catalog,account)
  UNION ALL SELECT p.publication_order,'source',NULL FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT c.publication_order,'source',NULL FROM trading.gas_owned_consumptions c JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(c.catalog_id,c.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT r.publication_order,'reserve',r.id FROM trading.gas_owned_reservations r JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(r.catalog_id,r.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT x.publication_order,'release',x.reservation_id FROM trading.gas_owned_releases x JOIN trading.gas_owned_reservations r ON(r.catalog_id,r.id)=(x.catalog_id,x.reservation_id)
   JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(r.catalog_id,r.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT s.publication_order,'source',NULL FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT f.publication_order,'source',NULL FROM trading.gas_owned_fees f JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT x.publication_order,'source',NULL FROM trading.gas_owned_fee_payments x JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(x.catalog_id,x.fee_id)
   JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT b.publication_order,'source',NULL FROM trading.gas_owned_mark_bindings b JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(b.catalog_id,b.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT p.publication_order,'source',NULL FROM trading.gas_owned_projection_profiles p WHERE(p.catalog_id,p.account_id)=(catalog,account)
 )h WHERE NOT trading.gas_owned_visible(catalog,h.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of)
  AND NOT(h.kind='reserve' AND(EXISTS(SELECT 1 FROM trading.gas_park_custody_lots l JOIN trading.gas_park_custody_acceptances a ON(a.catalog_id,a.trade_id)=(l.catalog_id,l.trade_id)
    WHERE(l.catalog_id,l.trade_id,l.reservation_id,a.operation_id)=(catalog,trade,h.id,operation))
   OR EXISTS(SELECT 1 FROM trading.gas_park_custody_return_lots l JOIN trading.gas_park_custody_returns r ON(r.catalog_id,r.id)=(l.catalog_id,l.return_id)
    WHERE(r.catalog_id,r.trade_id,r.operation_id,l.remainder_reservation_id)=(catalog,trade,operation,h.id))))
  AND NOT(h.kind='release' AND EXISTS(SELECT 1 FROM trading.gas_park_custody_return_lots l JOIN trading.gas_park_custody_returns r ON(r.catalog_id,r.id)=(l.catalog_id,l.return_id)
   WHERE(r.catalog_id,r.trade_id,r.operation_id,l.released_reservation_id)=(catalog,trade,operation,h.id)))
 ) THEN RAISE EXCEPTION 'gas_owned_context';END IF;
END$$;

CREATE FUNCTION trading.require_gas_park_custody_receipt(catalog uuid,operation uuid) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c trading.gas_park_custody_commands;t trading.gas_park_custody_trades;i trading.gas_park_custody_instruments;
 e trading.gas_park_custody_evidence;a trading.gas_park_custody_acceptances;r trading.gas_park_custody_returns;p lifecycle.action_policies;
 context uuid;as_of timestamptz;publication integer;fact_actor uuid;fact_operation uuid;fact_context uuid;effective timestamptz;recorded jsonb;row record;
 body jsonb;fact_id uuid;total numeric;prior numeric;quantity numeric;member trading.gas_park_custody_cash_members;
 changed boolean;BEGIN
 SELECT * INTO c FROM trading.gas_park_custody_commands q WHERE(q.catalog_id,q.operation_id)=(catalog,operation);
 IF c.operation_id IS NULL THEN RAISE EXCEPTION 'gas_custody_receipt';END IF;
 IF c.actor_id IS DISTINCT FROM(SELECT actor_id FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user)
 THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_park_custody_evidence_commit(catalog,c.trade_id,c.authority_until);
 SELECT * INTO STRICT t FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(catalog,c.trade_id);
 SELECT * INTO STRICT i FROM trading.gas_park_custody_instruments WHERE(catalog_id,instrument_id)=(catalog,t.instrument_id);
 IF c.action<>'evidence' THEN PERFORM security.require_gas_park_custody_commit(catalog,c.trade_id,c.authority_until,c.authority_until);END IF;
 PERFORM trading.gas_park_loan_keys(c.request,ARRAY['tradeId','action','body','asOf','contextId','policyId','policyRevision'],CASE WHEN c.action='evidence' THEN ARRAY['policyId','policyRevision'] ELSE ARRAY[]::text[] END);
 context=(c.request->>'contextId')::uuid;as_of=(c.request->>'asOf')::timestamptz;body=c.request->'body';fact_id=(c.outcome->>'id')::uuid;changed=(c.outcome->>'changed')::boolean;
 IF(c.request->>'tradeId')::uuid IS DISTINCT FROM c.trade_id OR c.request->>'action' IS DISTINCT FROM c.action
  OR(c.request->>'policyId')::uuid IS DISTINCT FROM c.policy_id OR(c.request->>'policyRevision')::integer IS DISTINCT FROM c.policy_revision
  OR(c.outcome->>'tradeId')::uuid IS DISTINCT FROM c.trade_id OR(c.outcome->>'instrumentId')::uuid IS DISTINCT FROM t.instrument_id
  OR(c.outcome->>'operationId')::uuid IS DISTINCT FROM c.operation_id OR c.outcome->>'action' IS DISTINCT FROM c.action OR changed IS NULL
 THEN RAISE EXCEPTION 'gas_custody_receipt';END IF;
 recorded=trading.gas_park_custody_recorded_body(catalog,c.action,fact_id);
 IF(c.outcome-ARRAY['operationId','changed']) IS DISTINCT FROM trading.gas_park_custody_recorded_outcome(catalog,c.action,fact_id) THEN RAISE EXCEPTION 'gas_custody_receipt';END IF;
 IF recorded IS NULL OR(recorded-ARRAY['expectedVersion','expectedRevision']) IS DISTINCT FROM(body-ARRAY['expectedVersion','expectedRevision']) THEN RAISE EXCEPTION 'gas_custody_receipt';END IF;
 IF NOT changed THEN
  PERFORM trading.require_gas_park_custody_source(catalog,c.trade_id,context,as_of);
  IF NOT EXISTS(SELECT 1 FROM trading.gas_park_custody_commands anchor WHERE anchor.catalog_id=catalog AND anchor.trade_id=c.trade_id AND anchor.action=c.action
   AND anchor.operation_id<>operation AND anchor.outcome->>'changed'='true' AND(anchor.outcome-ARRAY['operationId','changed'])=(c.outcome-ARRAY['operationId','changed'])
   AND((anchor.request->'body')-ARRAY['expectedVersion','expectedRevision'])=(body-ARRAY['expectedVersion','expectedRevision'])) THEN RAISE EXCEPTION 'gas_custody_receipt';END IF;
  RETURN;
 END IF;
 IF recorded IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_custody_receipt';END IF;
 IF c.action<>'evidence' THEN PERFORM trading.require_gas_park_custody_reconciled(catalog,c.trade_id);END IF;
 CASE c.action
 WHEN 'evidence' THEN
  SELECT * INTO STRICT e FROM trading.gas_park_custody_evidence WHERE(catalog_id,id)=(catalog,fact_id);
  publication=e.publication_order;fact_actor=e.actor_id;fact_operation=e.operation_id;fact_context=e.source_context_id;effective=e.observed_at;
  IF e.trade_id<>c.trade_id OR e.observed_at>as_of OR e.observed_at>(SELECT known_at FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context))
   OR e.previous_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.gas_park_custody_evidence old WHERE(old.catalog_id,old.id,old.trade_id,old.source_key,old.kind,old.revision)=
    (catalog,e.previous_id,e.trade_id,e.source_key,e.kind,e.revision-1) AND trading.gas_owned_visible(catalog,old.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of))
   OR EXISTS(SELECT 1 FROM trading.gas_park_custody_evidence other WHERE(other.catalog_id,other.trade_id,other.source_key)=(catalog,e.trade_id,e.source_key)
    AND(other.quantity,other.observed_at,other.evidence,other.kind)=(e.quantity,e.observed_at,e.evidence,e.kind) AND other.id<>e.id)
  THEN RAISE EXCEPTION 'gas_custody_evidence';END IF;
 WHEN 'accept' THEN
  SELECT * INTO STRICT a FROM trading.gas_park_custody_acceptances WHERE(catalog_id,id)=(catalog,fact_id);
  publication=a.publication_order;fact_actor=a.actor_id;fact_operation=a.operation_id;fact_context=a.source_context_id;effective=a.effective_at;quantity=t.quantity;
  IF a.trade_id<>c.trade_id OR(a.policy_id,a.policy_revision) IS DISTINCT FROM(c.policy_id,c.policy_revision) OR a.effective_at<i.park_at OR a.effective_at>=i.return_at THEN RAISE EXCEPTION 'gas_custody_accept';END IF;
  PERFORM trading.require_gas_park_custody_evidence(catalog,c.trade_id,a.agreement_evidence_id,'custody_agreement',t.quantity,effective,context,as_of);
  PERFORM trading.require_gas_park_custody_evidence(catalog,c.trade_id,a.receipt_evidence_id,'custody_receipt',t.quantity,effective,context,as_of);
  SELECT coalesce(sum(l.quantity),0) INTO total FROM trading.gas_park_custody_lots l WHERE(l.catalog_id,l.trade_id)=(catalog,c.trade_id);
  IF total<>(CASE i.perspective WHEN 'customer' THEN t.quantity ELSE 0 END) THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
  FOR row IN SELECT l.*,n.quantity native_quantity,n.lot_id native_lot,n.actor_id,n.effective_at,n.publication_order,n.business_key native_key FROM trading.gas_park_custody_lots l
   JOIN trading.gas_owned_reservations n ON(n.catalog_id,n.id)=(l.catalog_id,l.reservation_id) WHERE(l.catalog_id,l.trade_id)=(catalog,c.trade_id) LOOP
   IF(row.lot_id,row.quantity,effective,fact_actor) IS DISTINCT FROM(row.native_lot,row.native_quantity,row.effective_at,row.actor_id)
    OR row.native_key IS DISTINCT FROM 'custody:'||c.trade_id::text||':'||a.id::text||':'||row.lot_id::text
    OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_lots lot JOIN trading.gas_owned_positions pos ON(pos.catalog_id,pos.id)=(lot.catalog_id,lot.id) WHERE(lot.catalog_id,lot.id,lot.account_id)=(catalog,row.lot_id,t.owned_account_id) AND trading.gas_owned_visible(catalog,pos.publication_order,pos.effective_at,context,as_of))
    OR NOT EXISTS(SELECT 1 FROM trading.publications q WHERE(q.catalog_id,q.publication_order,q.publication_xid)=(catalog,row.publication_order,pg_current_xact_id()))
   THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
   PERFORM trading.require_gas_park_custody_reservation(catalog,row.reservation_id);
  END LOOP;
  SELECT * INTO member FROM trading.gas_park_custody_cash_members WHERE(catalog_id,trade_id)=(catalog,c.trade_id);
  IF(i.fixed_fee_amount<>0)<>(member.binding_id IS NOT NULL) THEN RAISE EXCEPTION 'gas_custody_cash';END IF;
  IF member.binding_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.cash_payment_memberships m JOIN trading.cash_payment_instruments ci ON(ci.catalog_id,ci.instrument_id)=(m.catalog_id,m.instrument_id)
   WHERE(m.catalog_id,m.trade_id,m.binding_id,m.instrument_id,m.book_id,m.effective_at)=(catalog,c.trade_id,member.binding_id,member.instrument_id,t.book_id,effective)
    AND(ci.reference_catalog_id,ci.reference_revision_id,ci.currency_code,ci.payment_at)=(i.reference_catalog_id,i.reference_revision_id,i.currency_code,i.fee_due_at)
    AND member.amount=i.fixed_fee_amount*CASE i.fee_direction WHEN 'pay' THEN -1 ELSE 1 END
    AND EXISTS(SELECT 1 FROM trading.publications q WHERE(q.catalog_id,q.publication_order,q.publication_xid)=(catalog,m.publication_order,pg_current_xact_id()))) THEN RAISE EXCEPTION 'gas_custody_cash';END IF;
 WHEN 'return' THEN
  SELECT * INTO STRICT r FROM trading.gas_park_custody_returns WHERE(catalog_id,id)=(catalog,fact_id);
  SELECT * INTO STRICT a FROM trading.gas_park_custody_acceptances WHERE(catalog_id,trade_id)=(catalog,c.trade_id);
  publication=r.publication_order;fact_actor=r.actor_id;fact_operation=r.operation_id;fact_context=r.source_context_id;effective=r.effective_at;quantity=r.quantity;
  SELECT coalesce(sum(x.quantity),0) INTO prior FROM trading.gas_park_custody_returns x WHERE(x.catalog_id,x.trade_id)=(catalog,c.trade_id) AND x.publication_order<publication;
  IF r.trade_id<>c.trade_id OR(r.policy_id,r.policy_revision) IS DISTINCT FROM(c.policy_id,c.policy_revision) OR effective<=a.effective_at OR prior+r.quantity>t.quantity
   OR NOT trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of)
   OR EXISTS(SELECT 1 FROM trading.gas_park_custody_returns x WHERE(x.catalog_id,x.trade_id)=(catalog,c.trade_id) AND x.publication_order<publication
    AND(x.effective_at>effective OR NOT trading.gas_owned_visible(catalog,x.publication_order,x.effective_at,context,as_of))) THEN RAISE EXCEPTION 'gas_custody_return';END IF;
  PERFORM trading.require_gas_park_custody_evidence(catalog,c.trade_id,r.evidence_id,'custody_return',quantity,effective,context,as_of);
  SELECT coalesce(sum(l.quantity),0) INTO total FROM trading.gas_park_custody_return_lots l WHERE(l.catalog_id,l.return_id)=(catalog,r.id);
  IF total<>(CASE i.perspective WHEN 'customer' THEN quantity ELSE 0 END) THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
  FOR row IN SELECT l.*,old.quantity old_quantity,old.lot_id old_lot,rel.effective_at release_at,rel.actor_id release_actor,rel.publication_order release_pub,
   n.quantity remainder_quantity,n.lot_id remainder_lot,n.effective_at remainder_at,n.actor_id remainder_actor,n.publication_order remainder_pub,n.business_key remainder_key
   FROM trading.gas_park_custody_return_lots l JOIN trading.gas_owned_reservations old ON(old.catalog_id,old.id)=(l.catalog_id,l.released_reservation_id)
   JOIN trading.gas_owned_releases rel ON(rel.catalog_id,rel.reservation_id)=(l.catalog_id,l.released_reservation_id)
   LEFT JOIN trading.gas_owned_reservations n ON(n.catalog_id,n.id)=(l.catalog_id,l.remainder_reservation_id)
   WHERE(l.catalog_id,l.return_id)=(catalog,r.id) LOOP
   IF NOT EXISTS(SELECT 1 FROM trading.gas_park_custody_reservations(catalog,c.trade_id,context,as_of)s WHERE(s.lot_id,s.reservation_id)=(row.lot_id,row.released_reservation_id))
    OR(row.old_lot,row.release_at,row.release_actor) IS DISTINCT FROM(row.lot_id,effective,fact_actor)
    OR row.quantity>row.old_quantity OR(row.old_quantity>row.quantity)<>(row.remainder_reservation_id IS NOT NULL)
    OR row.remainder_reservation_id IS NOT NULL AND row.remainder_key IS DISTINCT FROM 'custody:'||c.trade_id::text||':'||r.id::text||':'||row.lot_id::text
    OR row.remainder_reservation_id IS NOT NULL AND(row.remainder_lot,row.remainder_quantity,row.remainder_at,row.remainder_actor)
     IS DISTINCT FROM(row.lot_id,row.old_quantity-row.quantity,effective,fact_actor)
    OR NOT EXISTS(SELECT 1 FROM trading.publications q WHERE(q.catalog_id,q.publication_order,q.publication_xid)=(catalog,row.release_pub,pg_current_xact_id()))
    OR row.remainder_reservation_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.publications q WHERE(q.catalog_id,q.publication_order,q.publication_xid)=(catalog,row.remainder_pub,pg_current_xact_id()))
   THEN RAISE EXCEPTION 'gas_custody_allocation';END IF;
   PERFORM trading.require_gas_park_custody_reservation(catalog,row.released_reservation_id);
   PERFORM trading.require_gas_park_custody_reservation(catalog,row.remainder_reservation_id);
  END LOOP;
 END CASE;
 IF(fact_operation,fact_actor,fact_context) IS DISTINCT FROM(c.operation_id,c.actor_id,context)
  OR NOT EXISTS(SELECT 1 FROM trading.publications q WHERE(q.catalog_id,q.publication_order,q.publication_xid)=(catalog,publication,pg_current_xact_id()))
  OR trading.gas_owned_visible(catalog,publication,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of)
  OR effective>as_of OR effective>(SELECT known_at FROM market_data.temporal_contexts WHERE(catalog_id,id)=(catalog,context))
 THEN RAISE EXCEPTION 'gas_custody_context';END IF;
 PERFORM trading.require_gas_park_custody_source(catalog,c.trade_id,context,as_of);
 IF c.action<>'evidence' THEN
  IF(body->>'expectedVersion')::integer IS DISTINCT FROM trading.gas_park_custody_head(catalog,c.trade_id,context,as_of) THEN RAISE EXCEPTION 'gas_custody_stale';END IF;
  p=lifecycle.require_policy(catalog,c.policy_id,c.policy_revision,'gas.park_custody.'||c.action,as_of,false);
  IF p.mode='automatic' OR EXISTS(SELECT 1 FROM lifecycle.action_policies n WHERE(n.catalog_id,n.id)=(catalog,c.policy_id) AND n.revision>c.policy_revision)
   OR NOT trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of) THEN RAISE EXCEPTION 'gas_custody_policy_changed';END IF;
  IF i.perspective='customer' THEN
   PERFORM trading.require_gas_park_custody_stock_source(catalog,c.trade_id,c.operation_id,context,as_of);
   IF c.action='accept' OR EXISTS(SELECT 1 FROM trading.gas_park_custody_return_lots l
    WHERE(l.catalog_id,l.return_id)=(catalog,fact_id) AND l.remainder_reservation_id IS NOT NULL) THEN
    PERFORM trading.require_gas_park_custody_native_policy(catalog,t.owned_account_id,'gas.owned.reserve',context,as_of);
   END IF;
   IF c.action='return' THEN PERFORM trading.require_gas_park_custody_native_policy(catalog,t.owned_account_id,'gas.owned.release',context,as_of);END IF;
  END IF;
 END IF;
END$$;

CREATE FUNCTION trading.guard_gas_park_custody_fact() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE operation uuid;expected_action text;expected_trade uuid;expected_fact uuid;BEGIN
 CASE TG_TABLE_NAME
 WHEN 'gas_park_custody_commands' THEN operation=NEW.operation_id;
 WHEN 'gas_park_custody_evidence' THEN operation=NEW.operation_id;expected_action='evidence';expected_trade=NEW.trade_id;expected_fact=NEW.id;
 WHEN 'gas_park_custody_acceptances' THEN operation=NEW.operation_id;expected_action='accept';expected_trade=NEW.trade_id;expected_fact=NEW.id;
 WHEN 'gas_park_custody_returns' THEN operation=NEW.operation_id;expected_action='return';expected_trade=NEW.trade_id;expected_fact=NEW.id;
 WHEN 'gas_park_custody_lots' THEN
  SELECT operation_id,id INTO operation,expected_fact FROM trading.gas_park_custody_acceptances WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id);
  expected_action='accept';expected_trade=NEW.trade_id;
 WHEN 'gas_park_custody_cash_members' THEN
  SELECT operation_id,id INTO operation,expected_fact FROM trading.gas_park_custody_acceptances WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id);
  expected_action='accept';expected_trade=NEW.trade_id;
 WHEN 'cash_payment_memberships' THEN
  IF NOT EXISTS(SELECT 1 FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id)) THEN RETURN NEW;END IF;
  SELECT operation_id,id INTO operation,expected_fact FROM trading.gas_park_custody_acceptances WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id);
  expected_action='accept';expected_trade=NEW.trade_id;
  IF NOT EXISTS(SELECT 1 FROM trading.gas_park_custody_cash_members m WHERE(m.catalog_id,m.trade_id,m.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id)) THEN RAISE EXCEPTION 'gas_custody_cash';END IF;
 WHEN 'gas_park_custody_return_lots' THEN
  SELECT operation_id,id,trade_id INTO operation,expected_fact,expected_trade FROM trading.gas_park_custody_returns WHERE(catalog_id,id)=(NEW.catalog_id,NEW.return_id);
  expected_action='return';
 END CASE;
 IF expected_action IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.gas_park_custody_commands c
  WHERE(c.catalog_id,c.operation_id,c.trade_id,c.action)=(NEW.catalog_id,operation,expected_trade,expected_action)
   AND(c.outcome->>'id')::uuid=expected_fact AND c.outcome->>'changed'='true') THEN RAISE EXCEPTION 'gas_custody_receipt';END IF;
 PERFORM trading.require_gas_park_custody_receipt(NEW.catalog_id,operation);RETURN NEW;
END$$;
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_park_custody_commands','gas_park_custody_evidence','gas_park_custody_acceptances','gas_park_custody_lots','gas_park_custody_returns','gas_park_custody_return_lots','gas_park_custody_cash_members','cash_payment_memberships'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER custody_fact AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_park_custody_fact()',name);
 END LOOP;
END$$;

CREATE FUNCTION settlement_data.guard_gas_park_custody_cash() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE member trading.gas_park_custody_cash_members;origin timestamptz;total numeric;BEGIN
 SELECT * INTO member FROM trading.gas_park_custody_cash_members c WHERE(c.catalog_id,c.trade_id,c.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id);
 IF member.binding_id IS NULL THEN RETURN NEW;END IF;
 SELECT effective_at INTO STRICT origin FROM trading.gas_park_custody_acceptances WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id);
 PERFORM security.require_cash_payment_write(NEW.catalog_id,NEW.trade_id,NEW.binding_id,greatest(origin,NEW.paid_at));
 IF NEW.paid_at<origin OR abs(NEW.amount)>abs(member.amount) OR NEW.amount<>0 AND sign(NEW.amount)<>sign(member.amount) OR NEW.actor_id IS DISTINCT FROM(
  SELECT actor_id FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user)
  OR NOT EXISTS(SELECT 1 FROM settlement_data.cash_payment_commands c WHERE(c.catalog_id,c.allocation_id,c.revision,c.actor_id)=(NEW.catalog_id,NEW.allocation_id,NEW.revision,NEW.actor_id)
   AND(c.request->>'tradeId')::uuid=NEW.trade_id AND(c.request->>'bindingId')::uuid=NEW.binding_id AND(c.request->>'amount')::numeric=NEW.amount
   AND((c.request->>'paidAtUtc')::timestamp AT TIME ZONE 'UTC')=NEW.paid_at)
 THEN RAISE EXCEPTION 'gas_custody_cash_payment';END IF;
 SELECT coalesce(sum(p.amount),0) INTO total FROM(SELECT DISTINCT ON(p.allocation_id)p.* FROM settlement_data.cash_payment_revisions p
  WHERE(p.catalog_id,p.trade_id,p.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id) ORDER BY p.allocation_id,p.revision DESC)p;
 IF abs(total)>abs(member.amount) OR total<>0 AND sign(total)<>sign(member.amount) THEN RAISE EXCEPTION 'gas_custody_cash_payment';END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER custody_cash AFTER INSERT ON settlement_data.cash_payment_revisions DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_gas_park_custody_cash();

CREATE FUNCTION trading.guard_gas_park_custody_booking() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE t trading.gas_park_custody_trades;i trading.gas_park_custody_instruments;g trading.trades;body jsonb;expected jsonb;context uuid;owner trading.gas_owned_accounts;BEGIN
 IF TG_TABLE_NAME='gas_park_custody_trades' THEN SELECT * INTO t FROM trading.gas_park_custody_trades WHERE(catalog_id,trade_id)=(NEW.catalog_id,NEW.trade_id);
 ELSE SELECT * INTO t FROM trading.gas_park_custody_trades WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);END IF;
 IF t.trade_id IS NULL THEN RAISE EXCEPTION 'gas_custody_booking_seal';END IF;
 SELECT * INTO STRICT i FROM trading.gas_park_custody_instruments WHERE(catalog_id,instrument_id)=(t.catalog_id,t.instrument_id);
 SELECT * INTO STRICT g FROM trading.trades WHERE(catalog_id,id)=(t.catalog_id,t.trade_id);body=g.request;context=(body->>'contextId')::uuid;
 expected=jsonb_build_object('operationId',t.operation_id,'definitionRevisionId',i.definition_revision_id,'convention','physical_gas_fixed_quantity_v4','knowledgeMode','current',
  'profile','physical_gas_park_custody_v1','bookId',t.book_id,'legalEntityId',t.legal_entity_id,'legalEntityRevision',t.legal_entity_revision,
  'counterpartyId',t.counterparty_id,'counterpartyRevision',t.counterparty_revision,'referenceCatalogId',i.reference_catalog_id,'referenceRevisionId',i.reference_revision_id,
  'point',i.point_code,'quantitySpecification',i.quantity_code,'currency',i.currency_code,'ownedAccountId',t.owned_account_id,'serviceKind',i.service_kind,'perspective',i.perspective,
  'quantity',trim_scale(t.quantity)::text,'parkAt',i.park_at,'returnAt',i.return_at,'titlePolicy',i.title_policy,'returnPolicy',i.return_policy,'returnTiming',i.return_timing,
  'returnOrder',i.return_order,'usePolicy',i.use_policy,'lossPolicy',i.loss_policy,'physicalPolicy',i.physical_policy,'planPolicy',i.plan_policy,
  'fixedFeeAmount',trim_scale(i.fixed_fee_amount)::text,'feeDirection',i.fee_direction,'feeDueAt',i.fee_due_at,'feeRecognition',i.fee_recognition);
 -- Native canonicalizers accept UTC wire strings, while typed timestamps are
 -- compared as values so caller formatting cannot define another agreement.
 IF body-ARRAY['contextId','snapshotId','parkAt','returnAt','feeDueAt'] IS DISTINCT FROM expected-ARRAY['parkAt','returnAt','feeDueAt']
  OR(body->>'parkAt')::timestamptz IS DISTINCT FROM i.park_at OR(body->>'returnAt')::timestamptz IS DISTINCT FROM i.return_at
  OR(body->>'feeDueAt')::timestamptz IS DISTINCT FROM i.fee_due_at OR(body->>'snapshotId')::uuid IS NULL
  OR(g.book_id,g.operation_id,g.actor_id,g.publication_order,g.effective_at) IS DISTINCT FROM(t.book_id,t.operation_id,t.actor_id,t.publication_order,t.effective_at)
  OR(i.publication_order,i.effective_at) IS DISTINCT FROM(t.publication_order,t.effective_at) OR t.effective_at>=i.park_at
  OR(i.perspective='customer')<>(t.owned_account_id IS NOT NULL)
  OR NOT EXISTS(SELECT 1 FROM market_data.revisions d WHERE(d.catalog_id,d.id,d.reference_catalog_id,d.reference_revision_id)=(t.catalog_id,i.definition_revision_id,i.reference_catalog_id,i.reference_revision_id))
  OR t.actor_id IS DISTINCT FROM(SELECT actor_id FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user)
  OR NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(t.catalog_id,t.publication_order,pg_current_xact_id()))
  OR trading.gas_owned_visible(t.catalog_id,t.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,t.effective_at)
 THEN RAISE EXCEPTION 'gas_custody_booking_seal';END IF;
 IF security.trading_access(t.catalog_id,'BookTrade') IS NOT TRUE OR security.trading_access(t.catalog_id,'ReadTrades') IS NOT TRUE OR security.market_access(t.catalog_id,'ReadMarketData') IS NOT TRUE
  OR t.authority_until IS NULL OR clock_timestamp()>=t.authority_until OR security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 IF security.trading_context_day_matches(t.catalog_id,context,t.effective_at) IS NOT TRUE OR security.reference_access(i.reference_catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(i.reference_catalog_id,i.reference_revision_id) IS NOT TRUE OR security.market_fact_visible(t.catalog_id,i.definition_revision_id,NULL) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_custody_context';END IF;
 PERFORM settlement_data.require_futures_margin_source(t.catalog_id,context,t.effective_at,i.definition_revision_id);
 -- The source predates birth, so prove dependencies without pretending the new
 -- trade was already part of its source. Exact parties/account are source facts.
 IF NOT EXISTS(SELECT 1 FROM trading.legal_entities p WHERE(p.catalog_id,p.id,p.revision)=(t.catalog_id,t.legal_entity_id,t.legal_entity_revision)
  AND trading.gas_owned_visible(t.catalog_id,p.publication_order,p.effective_at,context,t.effective_at)) OR NOT EXISTS(
  SELECT 1 FROM trading.counterparties p WHERE(p.catalog_id,p.id,p.revision)=(t.catalog_id,t.counterparty_id,t.counterparty_revision)
  AND trading.gas_owned_visible(t.catalog_id,p.publication_order,p.effective_at,context,t.effective_at)) THEN RAISE EXCEPTION 'gas_custody_context';END IF;
 IF i.perspective='customer' THEN
  PERFORM security.require_gas_owned(t.catalog_id,false,false);
  SELECT * INTO owner FROM trading.gas_owned_accounts WHERE(catalog_id,id)=(t.catalog_id,t.owned_account_id);
  IF owner.id IS NULL OR owner.kind<>'owned_inventory' OR(owner.book_id,owner.legal_entity_id,owner.legal_entity_revision,owner.reference_catalog_id,owner.reference_revision_id,owner.location_code,owner.quantity_code)
   IS DISTINCT FROM(t.book_id,t.legal_entity_id,t.legal_entity_revision,i.reference_catalog_id,i.reference_revision_id,i.point_code,i.quantity_code)
   OR NOT trading.gas_owned_visible(t.catalog_id,owner.publication_order,owner.effective_at,context,t.effective_at) THEN RAISE EXCEPTION 'gas_custody_account';END IF;
 END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER custody_booking AFTER INSERT ON trading.gas_park_custody_instruments DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_park_custody_booking();
CREATE CONSTRAINT TRIGGER custody_booking AFTER INSERT ON trading.gas_park_custody_trades DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_park_custody_booking();

CREATE FUNCTION trading.guard_gas_park_custody_deadline() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF TG_TABLE_NAME='gas_park_custody_trades' THEN
  IF EXISTS(SELECT 1 FROM trading.gas_park_custody_instruments i WHERE(i.catalog_id,i.instrument_id,i.perspective)=(NEW.catalog_id,NEW.instrument_id,'customer')) THEN PERFORM security.require_gas_owned(NEW.catalog_id,false,false);END IF;
  IF clock_timestamp()>=NEW.authority_until OR security.trading_access(NEW.catalog_id,'BookTrade') IS NOT TRUE OR security.market_access(NEW.catalog_id,'ReadMarketData') IS NOT TRUE
   OR security.trading_access(NEW.catalog_id,'ReadTrades') IS NOT TRUE THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
 ELSIF NEW.action='evidence' THEN PERFORM security.require_gas_park_custody_evidence_commit(NEW.catalog_id,NEW.trade_id,NEW.authority_until);
 ELSE PERFORM security.require_gas_park_custody_commit(NEW.catalog_id,NEW.trade_id,NEW.authority_until,NEW.authority_until);END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER custody_deadline AFTER INSERT ON trading.gas_park_custody_trades DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_park_custody_deadline();
CREATE CONSTRAINT TRIGGER custody_deadline AFTER INSERT ON trading.gas_park_custody_commands DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_park_custody_deadline();

-- Optional lifecycle metadata has a separate declared scope from complete PV.
CREATE FUNCTION security.gas_park_custody_discovery_authority(catalog uuid,required boolean) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;BEGIN
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOperations'));
 IF required IS NULL THEN RAISE EXCEPTION 'gas_custody_input';END IF;
 IF security.trading_access(catalog,'ReadGasOperations') IS NOT TRUE OR deadline IS NULL OR NOT isfinite(deadline) OR clock_timestamp()>=deadline THEN
  IF required THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;RETURN NULL;
 END IF;
 PERFORM security.gas_park_custody_authority(catalog,false,false);
 IF clock_timestamp()>=deadline THEN
  IF required THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;RETURN NULL;
 END IF;
 RETURN deadline;
END$$;
CREATE FUNCTION security.require_gas_park_custody_discovery(catalog uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.gas_park_custody_authority(catalog,false,false);
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_custody_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.gas_park_custody_discovery_directory(catalog uuid,context uuid,as_of timestamptz,include_customer boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;inventory_deadline timestamptz;result jsonb;BEGIN
 inventory_deadline=security.authority_until('ReadGasOwnedHoldings');deadline=security.gas_park_custody_authority(catalog,false,false);
 IF include_customer IS NULL THEN RAISE EXCEPTION 'gas_custody_input';END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 IF include_customer THEN deadline=least(deadline,inventory_deadline);END IF;
 PERFORM security.require_gas_park_custody_read(catalog,context,as_of,deadline,include_customer);
 IF(SELECT count(*) FROM trading.gas_park_custody_trades t JOIN trading.gas_park_custody_instruments i ON(i.catalog_id,i.instrument_id)=(t.catalog_id,t.instrument_id)
  WHERE t.catalog_id=catalog AND(include_customer OR i.perspective='provider') AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of))>256 THEN RAISE EXCEPTION 'gas_custody_scope';END IF;
 SELECT coalesce(jsonb_agg(security.read_gas_park_custody(catalog,t.trade_id,context,as_of) ORDER BY t.trade_id),'[]') INTO result
  FROM trading.gas_park_custody_trades t JOIN trading.gas_park_custody_instruments i ON(i.catalog_id,i.instrument_id)=(t.catalog_id,t.instrument_id)
  WHERE t.catalog_id=catalog AND(include_customer OR i.perspective='provider') AND trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of);
 SELECT coalesce(jsonb_agg(jsonb_set(row,'{authorityUntil}',to_jsonb(least((row->>'authorityUntil')::timestamptz,deadline))) ORDER BY row->>'tradeId'),'[]') INTO result FROM jsonb_array_elements(result)row;
 PERFORM security.require_gas_park_custody_read(catalog,context,as_of,deadline,include_customer);RETURN result;
END$$;

CREATE FUNCTION settlement_data.cash_payment_request(body jsonb) RETURNS jsonb
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE operation uuid;allocation uuid;trade uuid;binding uuid;expected integer;paid timestamptz;amount_value numeric;canonical jsonb;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','allocationId','tradeId','bindingId','expectedRevision','paidAt','amount','policy']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('operationId','allocationId','tradeId','bindingId','expectedRevision','paidAt','amount','policy')) OR
  EXISTS(SELECT 1 FROM unnest(ARRAY['operationId','allocationId','tradeId','bindingId','paidAt','amount','policy']) k WHERE jsonb_typeof(body->k) IS DISTINCT FROM 'string') OR
  jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[0-9]{1,8}$' OR
  body->>'policy' IS DISTINCT FROM 'explicit_paid_cash_nominal_due_v1' OR
  body->>'paidAt'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?(Z|[+]00:00)$' OR
  body->>'amount'!~'^-?[0-9]{1,20}([.][0-9]{1,18})?$'
 THEN RAISE EXCEPTION 'cash_payment_input';END IF;
 operation=(body->>'operationId')::uuid;allocation=(body->>'allocationId')::uuid;trade=(body->>'tradeId')::uuid;binding=(body->>'bindingId')::uuid;
 expected=(body->>'expectedRevision')::integer;paid=(body->>'paidAt')::timestamptz;amount_value=(body->>'amount')::numeric;
 IF operation='00000000-0000-0000-0000-000000000000'::uuid OR allocation='00000000-0000-0000-0000-000000000000'::uuid OR NOT isfinite(paid)
 THEN RAISE EXCEPTION 'cash_payment_input';END IF;
 canonical=jsonb_build_object('operationId',operation,'allocationId',allocation,'tradeId',trade,'bindingId',binding,'expectedRevision',expected,
  'paidAtUtc',paid AT TIME ZONE 'UTC','amount',amount_value,'policy',body->>'policy');
 RETURN canonical;
END$$;

DO $upgrade$DECLARE p pg_proc;definition text;BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('security.record_cash_payment(uuid,jsonb,timestamptz)');
 IF NOT FOUND OR md5(p.prosrc)<>'65270da33575fdf53eaf2c6b7157cc27' OR p.proowner<>'openetrm_security_owner'::regrole
  OR p.prorettype<>'jsonb'::regtype OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0
  OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u' OR p.proargnames IS DISTINCT FROM ARRAY['catalog','body','as_of']
  OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
 THEN RAISE EXCEPTION 'gas_custody_upgrade: unexpected cash producer';END IF;
 definition=pg_get_functiondef(p.oid);
 definition=replace(definition,$old$ IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','allocationId','tradeId','bindingId','expectedRevision','paidAt','amount','policy']) OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('operationId','allocationId','tradeId','bindingId','expectedRevision','paidAt','amount','policy')) OR
  EXISTS(SELECT 1 FROM unnest(ARRAY['operationId','allocationId','tradeId','bindingId','paidAt','amount','policy']) k WHERE jsonb_typeof(body->k) IS DISTINCT FROM 'string') OR
  jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[0-9]{1,8}$' OR
  body->>'policy' IS DISTINCT FROM 'explicit_paid_cash_nominal_due_v1' OR
  body->>'paidAt'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6})?(Z|[+]00:00)$' OR
  body->>'amount'!~'^-?[0-9]{1,20}([.][0-9]{1,18})?$' OR as_of IS NULL OR NOT isfinite(as_of)
 THEN RAISE EXCEPTION 'cash_payment_input';END IF;
 operation=(body->>'operationId')::uuid;allocation=(body->>'allocationId')::uuid;trade=(body->>'tradeId')::uuid;binding=(body->>'bindingId')::uuid;
 expected=(body->>'expectedRevision')::integer;paid=(body->>'paidAt')::timestamptz;amount_value=(body->>'amount')::numeric;
 IF operation='00000000-0000-0000-0000-000000000000'::uuid OR allocation='00000000-0000-0000-0000-000000000000'::uuid OR NOT isfinite(paid) OR paid>as_of
 THEN RAISE EXCEPTION 'cash_payment_input';END IF;
 canonical=jsonb_build_object('operationId',operation,'allocationId',allocation,'tradeId',trade,'bindingId',binding,'expectedRevision',expected,
  'paidAtUtc',paid AT TIME ZONE 'UTC','amount',amount_value,'policy',body->>'policy');
$old$,$new$ canonical=settlement_data.cash_payment_request(body);
 operation=(canonical->>'operationId')::uuid;allocation=(canonical->>'allocationId')::uuid;
 trade=(canonical->>'tradeId')::uuid;binding=(canonical->>'bindingId')::uuid;expected=(canonical->>'expectedRevision')::integer;
 paid=(canonical->>'paidAtUtc')::timestamp AT TIME ZONE 'UTC';amount_value=(canonical->>'amount')::numeric;
 IF as_of IS NULL OR NOT isfinite(as_of) OR paid>as_of THEN RAISE EXCEPTION 'cash_payment_input';END IF;
$new$);
 EXECUTE definition;
END$upgrade$;


CREATE FUNCTION security.require_cash_payment_status(catalog uuid,trade uuid,binding uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE member trading.cash_payment_memberships;BEGIN
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'cash_payment_denied' USING ERRCODE='42501';END IF;
 IF security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'cash_payment_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO member FROM trading.cash_payment_memberships m WHERE(m.catalog_id,m.trade_id,m.binding_id)=(catalog,trade,binding);
 IF member.binding_id IS NULL THEN RAISE EXCEPTION 'cash_payment_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_cash_payment_write(catalog,trade,binding,member.effective_at);
 IF clock_timestamp()>=until_at THEN RAISE EXCEPTION 'cash_payment_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.cash_payment_status(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;canonical jsonb;operation uuid;trade uuid;binding uuid;actor uuid;receipt settlement_data.cash_payment_commands;BEGIN
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('AmendTrade'));
 canonical=settlement_data.cash_payment_request(body);operation=(canonical->>'operationId')::uuid;trade=(canonical->>'tradeId')::uuid;binding=(canonical->>'bindingId')::uuid;
 PERFORM security.require_cash_payment_status(catalog,trade,binding,deadline);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_cash_payment_status(catalog,trade,binding,deadline);
 SELECT * INTO receipt FROM settlement_data.cash_payment_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF receipt.operation_id IS NULL THEN RETURN jsonb_build_object('found',false,'authorityUntil',deadline);END IF;
 IF receipt.actor_id IS DISTINCT FROM actor OR receipt.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'cash_payment_conflict';END IF;
 PERFORM security.require_cash_payment_status(catalog,trade,binding,deadline);
 RETURN jsonb_build_object('found',true,'authorityUntil',deadline,'outcome',jsonb_build_object('operationId',operation,'allocationId',receipt.allocation_id,
  'revision',receipt.revision,'tradeId',trade,'bindingId',binding));
END$$;
ALTER FUNCTION settlement_data.cash_payment_request(jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.require_cash_payment_status(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.cash_payment_status(uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.cash_payment_request(jsonb),security.require_cash_payment_status(uuid,uuid,uuid,timestamptz),security.cash_payment_status(uuid,jsonb)
 FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.require_cash_payment_status(uuid,uuid,uuid,timestamptz),security.cash_payment_status(uuid,jsonb) TO openetrm_runtime;

-- Worker admission derives custody permissions from its immutable selected
-- source. Foreign custody is never classified as independently owned gas.
DO $guard$DECLARE spec record;p pg_proc;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('risk.authorized(risk.jobs)','6f268d337823bbc1fb81f6e7e1c31aba','boolean','sql',ARRAY['job'],false),
  ('risk.worker_read()','045cf468c3307f69c90baa61e63cbd92','boolean','sql',NULL::text[],true),
  ('risk.needs_gas(risk.jobs)','96a8eb0dcb23f27320e6dd0764f29b86','boolean','plpgsql',ARRAY['job'],false),
  ('risk.claim(integer,text)','c5ff7e444da451d813504a6cde0eac1a','jsonb','plpgsql',ARRAY['lease_seconds','engine_version'],true),
  ('risk.owned_attempt(uuid,uuid,boolean,boolean)','fc07d9faaf2b16ad5fa69948dd260442','risk.jobs','plpgsql',ARRAY['job_id','token','allow_cancel','lock_row'],false),
  ('risk.bind(uuid,uuid)','6c12bcc0cc5f0a389851f048588ea3aa','uuid','plpgsql',ARRAY['job_id','token'],true)
 )s(signature,digest,result_type,language_name,argument_names,worker_execute) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(spec.signature);
  IF NOT FOUND OR md5(p.prosrc)<>spec.digest OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>to_regtype(spec.result_type)
   OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.provariadic<>0 OR NOT p.prosecdef OR p.provolatile<>'v' OR p.proparallel<>'u'
   OR p.proargnames IS DISTINCT FROM spec.argument_names OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
   OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>spec.language_name OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proacl::text IS DISTINCT FROM(CASE WHEN spec.worker_execute THEN '{openetrm_security_owner=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}' ELSE '{openetrm_security_owner=X/openetrm_security_owner}' END)
  THEN RAISE EXCEPTION 'gas_custody_upgrade: unexpected worker boundary %',spec.signature;END IF;
 END LOOP;
 IF to_regprocedure('risk.authorized_pre_gas_park_custody(risk.jobs)') IS NOT NULL OR to_regprocedure('risk.worker_read_pre_gas_park_custody()') IS NOT NULL
  OR to_regprocedure('risk.needs_gas_pre_gas_park_custody(risk.jobs)') IS NOT NULL THEN RAISE EXCEPTION 'gas_custody_upgrade: custody worker boundary already installed';END IF;
 EXECUTE replace(pg_get_functiondef('risk.authorized(risk.jobs)'::regprocedure),'FUNCTION risk.authorized(','FUNCTION risk.authorized_pre_gas_park_custody(');
 EXECUTE replace(pg_get_functiondef('risk.worker_read()'::regprocedure),'FUNCTION risk.worker_read(','FUNCTION risk.worker_read_pre_gas_park_custody(');
 EXECUTE replace(pg_get_functiondef('risk.needs_gas(risk.jobs)'::regprocedure),'FUNCTION risk.needs_gas(','FUNCTION risk.needs_gas_pre_gas_park_custody(');
END$guard$;
ALTER FUNCTION risk.authorized_pre_gas_park_custody(risk.jobs) OWNER TO openetrm_security_owner;
ALTER FUNCTION risk.worker_read_pre_gas_park_custody() OWNER TO openetrm_security_owner;
ALTER FUNCTION risk.needs_gas_pre_gas_park_custody(risk.jobs) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.authorized_pre_gas_park_custody(risk.jobs),risk.worker_read_pre_gas_park_custody(),risk.needs_gas_pre_gas_park_custody(risk.jobs)
 FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION risk.needs_gas_park_custody(job risk.jobs,customer_only boolean) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(WITH cut AS(SELECT x.known_through,t.known_at FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(job.catalog_id,job.context_id))
  SELECT 1 FROM trading.gas_park_custody_trades s JOIN trading.gas_park_custody_instruments i ON(i.catalog_id,i.instrument_id)=(s.catalog_id,s.instrument_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(s.catalog_id,s.publication_order) CROSS JOIN cut
  WHERE s.catalog_id=job.catalog_id AND s.publication_order<=cut.known_through AND k.known_from<=cut.known_at AND s.effective_at<=job.valuation_at
   AND(NOT customer_only OR i.perspective='customer') AND job.request->'bookIds' @> jsonb_build_array(s.book_id)
   AND(NULLIF(job.request->'tradeIds','null'::jsonb) IS NULL OR job.request->'tradeIds' @> jsonb_build_array(s.trade_id)));
$$;
ALTER FUNCTION risk.needs_gas_park_custody(risk.jobs,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.needs_gas_park_custody(risk.jobs,boolean) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION risk.needs_gas(job risk.jobs) RETURNS boolean
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN RETURN risk.needs_gas_pre_gas_park_custody(job) OR risk.needs_gas_park_custody(job,true);END$$;
CREATE OR REPLACE FUNCTION risk.authorized(job risk.jobs) RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT risk.authorized_pre_gas_park_custody(job) AND(NOT risk.needs_gas_park_custody(job,false)
  OR risk.actor_permission(job.actor_id,job.tenant_id,job.workspace_id,job.delegation_id,'ReadGasOperations'));
$$;
CREATE OR REPLACE FUNCTION risk.worker_read() RETURNS boolean
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT risk.worker_read_pre_gas_park_custody() AND NOT EXISTS(SELECT 1 FROM security.request_contexts q JOIN risk.worker_attempt_contexts b ON b.context_id=q.id
  JOIN risk.jobs j ON(j.id,j.lease_token)=(b.job_id,b.lease_token) JOIN risk.workers w ON w.login=session_user
  WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
   AND risk.needs_gas_park_custody(j,false) AND NOT security.principal_has_permission(w.principal_id,j.tenant_id,j.workspace_id,'ReadGasOperations',clock_timestamp()));
$$;
DO $patch$DECLARE p pg_proc;definition text;needle text;replacement text;BEGIN
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='risk.claim(integer,text)'::regprocedure;definition=pg_get_functiondef(p.oid);
 needle='AND(NOT risk.needs_gas(j) OR security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,''ReadGasOwnedHoldings'',clock_timestamp()))';
 replacement=needle||' AND(NOT risk.needs_gas_park_custody(j,false) OR security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,''ReadGasOperations'',clock_timestamp()))';
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'gas_custody_upgrade: worker claim shape';END IF;
 EXECUTE replace(definition,needle,replacement);
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='risk.owned_attempt(uuid,uuid,boolean,boolean)'::regprocedure;definition=pg_get_functiondef(p.oid);
 needle='OR risk.needs_gas(job) AND NOT security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,''ReadGasOwnedHoldings'',clock_timestamp()) THEN';
 replacement=replace(needle,' THEN',' OR risk.needs_gas_park_custody(job,false) AND NOT security.principal_has_permission(actor.principal_id,actor.tenant_id,actor.workspace_id,''ReadGasOperations'',clock_timestamp()) THEN');
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'gas_custody_upgrade: worker attempt shape';END IF;
 EXECUTE replace(definition,needle,replacement);
 SELECT * INTO STRICT p FROM pg_proc WHERE oid='risk.bind(uuid,uuid)'::regprocedure;definition=pg_get_functiondef(p.oid);
 needle='gas_until timestamptz;';replacement=needle||'custody_until timestamptz;';
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'gas_custody_upgrade: worker bind declaration';END IF;
 definition=replace(definition,needle,replacement);
 needle='ARRAY[''ReadMarketData'',''ReadTrades'',CASE WHEN job.request->>''convention''=''vol_surface_fit_v1'' THEN ''PublishMarketData'' ELSE ''RunScenario'' END,''ReadGasOwnedHoldings''] LOOP';
 replacement='ARRAY[''ReadMarketData'',''ReadTrades'',CASE WHEN job.request->>''convention''=''vol_surface_fit_v1'' THEN ''PublishMarketData'' ELSE ''RunScenario'' END,''ReadGasOwnedHoldings'',''ReadGasOperations''] LOOP';
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'gas_custody_upgrade: worker bind capture';END IF;
 definition=replace(definition,needle,replacement);
 needle='IF required=''ReadGasOwnedHoldings'' THEN gas_until=permission_until;ELSE candidate_until=least(candidate_until,permission_until);END IF;';
 replacement='IF required=''ReadGasOwnedHoldings'' THEN gas_until=permission_until;ELSIF required=''ReadGasOperations'' THEN custody_until=permission_until;ELSE candidate_until=least(candidate_until,permission_until);END IF;';
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'gas_custody_upgrade: worker bind permission';END IF;
 definition=replace(definition,needle,replacement);
 needle='IF risk.needs_gas(job) THEN candidate_until=least(candidate_until,gas_until);END IF;';
 replacement=needle||E'\n IF risk.needs_gas_park_custody(job,false) THEN candidate_until=least(candidate_until,custody_until);END IF;';
 IF length(definition)-length(replace(definition,needle,''))<>length(needle) THEN RAISE EXCEPTION 'gas_custody_upgrade: worker bind deadline';END IF;
 EXECUTE replace(definition,needle,replacement);
END$patch$;

-- Keep typed facts private; grant only each declared operation and read scope.
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('trading','security','settlement_data') AND p.proname LIKE '%gas_park_custody%' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',item.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',item.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.book_gas_park_custody(uuid,jsonb,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.read_gas_park_custody(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.record_gas_park_custody_evidence(uuid,uuid,uuid,jsonb,timestamptz,uuid),
 security.gas_park_custody_command_receipt(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.gas_park_custody_authority(uuid,boolean,boolean),
 security.require_gas_park_custody_commit(uuid,uuid,timestamptz,timestamptz),security.gas_park_custody_booking_status(uuid,uuid) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.require_gas_park_custody_read(uuid,uuid,timestamptz,timestamptz,boolean),
 security.gas_park_custody_directory(uuid,uuid,timestamptz,uuid[],uuid[],uuid[]),security.require_gas_park_custody_scope(uuid,uuid,timestamptz,uuid[],uuid[]),
 security.require_gas_park_custody_presence(uuid,uuid,timestamptz,uuid[],uuid[],timestamptz),
 security.gas_park_custody_projection_fence(uuid,uuid,timestamptz,timestamptz,uuid[],uuid[]) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.record_gas_park_custody(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer,boolean) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.require_gas_park_custody_evidence_commit(uuid,uuid,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.gas_park_custody_trade_head(uuid,uuid),security.risk_gas_park_custody_trade_heads(uuid,uuid[],uuid[]) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.gas_park_custody_discovery_authority(uuid,boolean),security.require_gas_park_custody_discovery(uuid,timestamptz),
 security.gas_park_custody_discovery_directory(uuid,uuid,timestamptz,boolean) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.gas_park_custody_authority(uuid,boolean,boolean),security.read_gas_park_custody(uuid,uuid,uuid,timestamptz),
 security.require_gas_park_custody_read(uuid,uuid,timestamptz,timestamptz,boolean),security.gas_park_custody_directory(uuid,uuid,timestamptz,uuid[],uuid[],uuid[]),
 security.require_gas_park_custody_scope(uuid,uuid,timestamptz,uuid[],uuid[]),security.require_gas_park_custody_presence(uuid,uuid,timestamptz,uuid[],uuid[],timestamptz),
 security.gas_park_custody_projection_fence(uuid,uuid,timestamptz,timestamptz,uuid[],uuid[]),security.gas_park_custody_trade_head(uuid,uuid),
 security.risk_gas_park_custody_trade_heads(uuid,uuid[],uuid[]) TO openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.require_gas_park_custody_booking_status(uuid,uuid,timestamptz,boolean,boolean) TO openetrm_runtime;
COMMIT;
