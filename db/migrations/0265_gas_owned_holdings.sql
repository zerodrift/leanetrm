-- Explicit gas title, operator balances and fungible commodity loans. Every
-- relationship is native; operational evidence alone creates no economic holding.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
INSERT INTO security.permissions VALUES('ReadGasOwnedHoldings'),('RecordGasOwnedHoldings'),('ConfigureGasOwnedHoldings');

CREATE TABLE trading.gas_owned_accounts(
 catalog_id uuid NOT NULL,id uuid NOT NULL,code ref_data.code NOT NULL,kind text NOT NULL CHECK(kind IN('owned_inventory','operator_balance')),
 book_id uuid NOT NULL,legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid,counterparty_revision integer,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,quantity_code ref_data.code NOT NULL,location_code ref_data.code NOT NULL,
 definition_revision_id uuid NOT NULL,mark_curve_code ref_data.code NOT NULL,mark_contract_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 title_policy text NOT NULL CHECK(title_policy='explicit_accepted_title_v1'),loss_policy text NOT NULL CHECK(loss_policy='separate_evidenced_loss_v1'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,code),UNIQUE(catalog_id,id,book_id),UNIQUE(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,mark_curve_code,mark_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK((counterparty_id IS NULL)=(counterparty_revision IS NULL)),CHECK(kind<>'operator_balance' OR counterparty_id IS NOT NULL)
);
CREATE TABLE trading.gas_owned_evidence(
 catalog_id uuid NOT NULL,id uuid NOT NULL,account_id uuid NOT NULL,source_key text NOT NULL CHECK(length(btrim(source_key)) BETWEEN 1 AND 128),
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 kind text NOT NULL CHECK(kind IN('title_receipt','physical_delivery','physical_loss','operator_statement','cashout_determination','loan_agreement','loan_return','futures_final')),
 quantity ref_data.exact_number NOT NULL,occurred_at ref_data.instant NOT NULL,evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 2000),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,account_id,id),UNIQUE(catalog_id,account_id,source_key,revision),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,previous_id) REFERENCES trading.gas_owned_evidence(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_owned_positions(
 catalog_id uuid NOT NULL,id uuid NOT NULL,account_id uuid NOT NULL,kind text NOT NULL CHECK(kind IN('owned_stock','operator_claim','loan_return','delivery_obligation')),
 quantity ref_data.exact_number NOT NULL CHECK(quantity<>0),evidence_id uuid NOT NULL,
 origin_policy text NOT NULL CHECK(origin_policy IN('opening_owned_stock_v1','operator_balance_claim_v1','operator_balance_transfer_v1','title_transfer_loan_v1','physical_futures_delivery_v1')),
 definition_revision_id uuid NOT NULL,mark_curve_code ref_data.code NOT NULL,mark_contract_id uuid NOT NULL,due_at ref_data.instant,
 carrying text NOT NULL CHECK(carrying IN('nominal_mark_v1','currency_curve_v1')),
 settlement_mode text NOT NULL CHECK(settlement_mode IN('owned_stock_v1','return_quantity_v1','cashout_final_price_v1','physical_delivery_v1')),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,account_id),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,account_id,definition_revision_id) REFERENCES trading.gas_owned_accounts(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,mark_curve_code,mark_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,account_id,evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,account_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(carrying<>'currency_curve_v1' OR due_at IS NOT NULL)
);
CREATE TABLE trading.gas_owned_lots(
 catalog_id uuid NOT NULL,id uuid NOT NULL,account_id uuid NOT NULL,title_at ref_data.instant NOT NULL,available_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),FOREIGN KEY(catalog_id,id,account_id) REFERENCES trading.gas_owned_positions(catalog_id,id,account_id),
 CHECK(title_at<=available_at)
);
CREATE TABLE trading.gas_owned_reservations(
 catalog_id uuid NOT NULL,id uuid NOT NULL,lot_id uuid NOT NULL,business_key text NOT NULL CHECK(length(btrim(business_key)) BETWEEN 1 AND 128),
 quantity ref_data.exact_number NOT NULL CHECK(quantity>0),actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,lot_id,business_key),FOREIGN KEY(catalog_id,lot_id) REFERENCES trading.gas_owned_lots(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_owned_releases(
 catalog_id uuid NOT NULL,reservation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,reservation_id),FOREIGN KEY(catalog_id,reservation_id) REFERENCES trading.gas_owned_reservations(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_owned_consumptions(
 catalog_id uuid NOT NULL,id uuid NOT NULL,lot_id uuid NOT NULL,kind text NOT NULL CHECK(kind IN('delivery','return','loss')),
 quantity ref_data.exact_number NOT NULL CHECK(quantity>0),evidence_id uuid NOT NULL,reservation_id uuid,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,evidence_id,lot_id),FOREIGN KEY(catalog_id,lot_id) REFERENCES trading.gas_owned_lots(catalog_id,id),
 FOREIGN KEY(catalog_id,evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id),
 FOREIGN KEY(catalog_id,reservation_id) REFERENCES trading.gas_owned_reservations(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_owned_loans(
 catalog_id uuid NOT NULL,id uuid NOT NULL,account_id uuid NOT NULL,received_lot_id uuid NOT NULL,return_position_id uuid NOT NULL,
 quantity ref_data.exact_number NOT NULL CHECK(quantity>0),return_at ref_data.instant NOT NULL,
 title_policy text NOT NULL CHECK(title_policy='fungible_gas_title_transfer_return_v1'),lender_id uuid NOT NULL,lender_revision integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,received_lot_id),UNIQUE(catalog_id,return_position_id),
 FOREIGN KEY(catalog_id,received_lot_id,account_id) REFERENCES trading.gas_owned_positions(catalog_id,id,account_id),
 FOREIGN KEY(catalog_id,return_position_id,account_id) REFERENCES trading.gas_owned_positions(catalog_id,id,account_id),
 FOREIGN KEY(catalog_id,lender_id,lender_revision) REFERENCES trading.counterparties(catalog_id,id,revision)
);
CREATE TABLE trading.gas_owned_repayments(
 catalog_id uuid NOT NULL,id uuid NOT NULL,loan_id uuid NOT NULL,consumption_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,consumption_id),FOREIGN KEY(catalog_id,loan_id) REFERENCES trading.gas_owned_loans(catalog_id,id),
 FOREIGN KEY(catalog_id,consumption_id) REFERENCES trading.gas_owned_consumptions(catalog_id,id)
);
CREATE TABLE trading.gas_owned_claim_settlements(
 catalog_id uuid NOT NULL,id uuid NOT NULL,position_id uuid NOT NULL,quantity ref_data.exact_number NOT NULL CHECK(quantity>0),
 receipt_lot_id uuid,consumption_id uuid,cash_id uuid,evidence_id uuid NOT NULL,source_observation_id uuid,
 actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,receipt_lot_id),UNIQUE(catalog_id,consumption_id),UNIQUE(catalog_id,cash_id),
 UNIQUE(catalog_id,position_id,publication_order),FOREIGN KEY(catalog_id,position_id) REFERENCES trading.gas_owned_positions(catalog_id,id),
 FOREIGN KEY(catalog_id,receipt_lot_id) REFERENCES trading.gas_owned_lots(catalog_id,id),
 FOREIGN KEY(catalog_id,consumption_id) REFERENCES trading.gas_owned_consumptions(catalog_id,id),
 FOREIGN KEY(catalog_id,evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id),
 FOREIGN KEY(catalog_id,source_observation_id) REFERENCES market_data.observations(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(num_nonnulls(receipt_lot_id,consumption_id)=1 AND cash_id IS NULL AND source_observation_id IS NULL OR
  num_nonnulls(receipt_lot_id,consumption_id)=0 AND source_observation_id IS NOT NULL)
);
CREATE TABLE trading.gas_owned_cashout_terms(
 catalog_id uuid NOT NULL,position_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,source_code ref_data.code NOT NULL,role text NOT NULL,
 observed_at ref_data.instant NOT NULL,cash_due_at ref_data.instant NOT NULL,rounding text NOT NULL CHECK(rounding='payment_total_half_even_2dp_v1'),
 PRIMARY KEY(catalog_id,position_id),FOREIGN KEY(catalog_id,position_id) REFERENCES trading.gas_owned_positions(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 CHECK(role IN('daily_settlement','final_contract_settlement')),CHECK(cash_due_at>=observed_at)
);
CREATE TABLE trading.gas_owned_fees(
 catalog_id uuid NOT NULL,id uuid NOT NULL,position_id uuid NOT NULL,loan_id uuid,amount ref_data.exact_number NOT NULL CHECK(amount<>0),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,due_at ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),FOREIGN KEY(catalog_id,position_id) REFERENCES trading.gas_owned_positions(catalog_id,id),
 FOREIGN KEY(catalog_id,loan_id) REFERENCES trading.gas_owned_loans(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_owned_fee_payments(
 catalog_id uuid NOT NULL,id uuid NOT NULL,fee_id uuid NOT NULL,business_key text NOT NULL CHECK(length(btrim(business_key)) BETWEEN 1 AND 128),
 revision integer NOT NULL CHECK(revision>0),paid_at ref_data.instant NOT NULL,amount ref_data.exact_number NOT NULL CHECK(amount<>0),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 2000),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,fee_id,business_key,revision),
 FOREIGN KEY(catalog_id,fee_id) REFERENCES trading.gas_owned_fees(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.gas_owned_claim_settlements ADD FOREIGN KEY(catalog_id,cash_id) REFERENCES trading.gas_owned_fees(catalog_id,id);
CREATE TABLE trading.gas_owned_commands(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),request_id uuid NOT NULL,
 account_id uuid NOT NULL,request jsonb NOT NULL,outcome jsonb NOT NULL,
 authority_until ref_data.instant NOT NULL,configuring boolean NOT NULL,requires_jobs boolean NOT NULL,
 PRIMARY KEY(catalog_id,request_id),FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id)
);

CREATE FUNCTION security.require_gas_owned(catalog uuid,writing boolean,configuring boolean DEFAULT false) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 OR security.trading_access(catalog,'ReadGasOwnedHoldings') IS NOT TRUE
 OR(writing AND security.trading_access(catalog,'RecordGasOwnedHoldings') IS NOT TRUE)
 OR(configuring AND security.trading_access(catalog,'ConfigureGasOwnedHoldings') IS NOT TRUE)
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 IF writing AND security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
END$$;
ALTER FUNCTION security.require_gas_owned(uuid,boolean,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_gas_owned(uuid,boolean,boolean) FROM PUBLIC;

DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_owned_accounts','gas_owned_evidence','gas_owned_positions','gas_owned_lots','gas_owned_reservations',
 'gas_owned_releases','gas_owned_consumptions','gas_owned_loans','gas_owned_repayments','gas_owned_claim_settlements','gas_owned_cashout_terms','gas_owned_fees','gas_owned_fee_payments','gas_owned_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
-- Runtime receives functions, never a direct write grant. Guarded definer
-- producers and deferred conservation checks are installed by 0265.
COMMIT;
