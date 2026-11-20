-- A bilateral trade revision retains its legal authority and its independent credit decision.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
CREATE SCHEMA desk AUTHORIZATION openetrm_security_owner;
REVOKE ALL ON SCHEMA desk FROM PUBLIC;
GRANT USAGE ON SCHEMA desk TO openetrm_runtime;

INSERT INTO security.permissions VALUES ('ManageAgreements'),('ReadAgreementDocuments'),('ManageCounterpartyCredit'),('ConfirmBilateralTrades');
INSERT INTO security.roles VALUES (gen_random_uuid(),'contracts_officer','Contracts administration'),
 (gen_random_uuid(),'credit_officer','Counterparty credit'),(gen_random_uuid(),'trade_confirmations','Trade confirmations');
INSERT INTO security.role_permissions
 SELECT r.id,p FROM security.roles r CROSS JOIN LATERAL unnest(CASE r.code
 WHEN 'contracts_officer' THEN ARRAY['ReadMarketData','ReadTrades','ReadTradeParties','ManageAgreements','ReadAgreementDocuments']
 WHEN 'credit_officer' THEN ARRAY['ReadMarketData','ReadTrades','ReadTradeParties','ManageCounterpartyCredit']
 WHEN 'trade_confirmations' THEN ARRAY['ReadMarketData','ReadTrades','ReadTradeParties','ConfirmBilateralTrades'] END)p
 WHERE r.code IN ('contracts_officer','credit_officer','trade_confirmations');

CREATE FUNCTION desk.products() RETURNS text[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT ARRAY['physical_gas','gas_storage','gas_transport','gas_park_loan','gas_park_custody','thermal_toll','battery_right',
 'solar_right','heat_rate_call','financial_swap','ppa','physical_crude','physical_lng','rec_forward']::text[]
$$;
CREATE TABLE desk.documents(
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),digest bytea NOT NULL,content bytea NOT NULL,
 PRIMARY KEY(catalog_id,digest),CHECK(digest=sha256(content)),CHECK(octet_length(content) BETWEEN 10 AND 8388608),
 CHECK(substring(content FROM 1 FOR 5)=convert_to('%PDF-','UTF8'))
);
CREATE TABLE desk.agreements(
 catalog_id uuid NOT NULL,id uuid NOT NULL,code ref_data.code NOT NULL,revision int NOT NULL CHECK(revision>0),
 legal_entity_id uuid NOT NULL,legal_entity_revision int NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision int NOT NULL,
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 160),agreement_type text NOT NULL CHECK(length(btrim(agreement_type)) BETWEEN 1 AND 80),
 products text[] NOT NULL CHECK(cardinality(products)>0 AND products<@desk.products() AND array_position(products,NULL) IS NULL),
 effective_at ref_data.instant NOT NULL,valid_to date,status text NOT NULL CHECK(status IN('draft','executed','terminated')),
 procedure text NOT NULL CHECK(procedure IN('oral','written','signed_contract')),provisions text NOT NULL CHECK(length(provisions)<=4000),
 filename text,digest bytea,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order int NOT NULL,fact_hash bytea NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,code,revision),UNIQUE(catalog_id,code,fact_hash),
 CHECK(valid_to IS NULL OR valid_to>(effective_at AT TIME ZONE 'UTC')::date),
 CHECK((filename IS NULL)=(digest IS NULL) AND (status<>'executed' OR digest IS NOT NULL)),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,digest) REFERENCES desk.documents,
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE desk.clearances(
 catalog_id uuid NOT NULL,id uuid NOT NULL,revision int NOT NULL CHECK(revision>0),
 legal_entity_id uuid NOT NULL,legal_entity_revision int NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision int NOT NULL,
 product text NOT NULL CHECK(product=ANY(desk.products())),effective_at ref_data.instant NOT NULL,valid_to date,
 status text NOT NULL CHECK(status IN('approved','hold','expired')),reason text NOT NULL CHECK(length(btrim(reason)) BETWEEN 1 AND 500),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order int NOT NULL,fact_hash bytea NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,legal_entity_id,counterparty_id,product,revision),
 UNIQUE(catalog_id,legal_entity_id,counterparty_id,product,fact_hash),
 CHECK(valid_to IS NULL OR valid_to>(effective_at AT TIME ZONE 'UTC')::date),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE desk.admissions(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision int NOT NULL,book_id uuid NOT NULL,product text NOT NULL,
 agreement_id uuid NOT NULL,clearance_id uuid NOT NULL,definition_revision_id uuid NOT NULL,executed_at ref_data.instant NOT NULL,effective_at ref_data.instant NOT NULL,publication_order int NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision),FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),FOREIGN KEY(catalog_id,agreement_id) REFERENCES desk.agreements,
 FOREIGN KEY(catalog_id,definition_revision_id) REFERENCES market_data.revisions(catalog_id,id),FOREIGN KEY(catalog_id,clearance_id) REFERENCES desk.clearances,FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE desk.confirmations(
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision int NOT NULL,revision int NOT NULL CHECK(revision>0),
 status text NOT NULL CHECK(status IN('sent','matched','disputed')),reference text NOT NULL CHECK(length(btrim(reference)) BETWEEN 1 AND 160),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 1000),effective_at ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order int NOT NULL,fact_hash bytea NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,trade_id,trade_revision,revision),UNIQUE(catalog_id,trade_id,trade_revision,fact_hash),
 FOREIGN KEY(catalog_id,trade_id,trade_revision) REFERENCES desk.admissions,FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE desk.requests(
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),actor_id uuid NOT NULL REFERENCES security.principals(id),
 operation_id uuid NOT NULL,command_hash bytea NOT NULL,outcome jsonb NOT NULL,PRIMARY KEY(catalog_id,actor_id,operation_id)
);

CREATE TABLE desk.booking_choices(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),operation_id uuid NOT NULL,
 agreement_id uuid,PRIMARY KEY(catalog_id,actor_id,operation_id),FOREIGN KEY(catalog_id,agreement_id) REFERENCES desk.agreements
);

CREATE FUNCTION desk.authority(catalog uuid,permission text,writing boolean) RETURNS uuid
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id FOR SHARE;
 IF security.trading_access(catalog,permission) IS NOT TRUE OR (writing AND security.current_write_context() IS NOT TRUE)
 THEN RAISE EXCEPTION 'desk_denied' USING ERRCODE='42501';END IF;
 SELECT q.actor_id INTO actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.runtime_login=session_user
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.expires_at>clock_timestamp();
 IF actor IS NULL THEN RAISE EXCEPTION 'desk_denied' USING ERRCODE='42501';END IF;
 RETURN actor;
END$$;

CREATE FUNCTION desk.require_authority(catalog uuid,permission text,original_until timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM desk.authority(catalog,permission,false);
 IF original_until IS NULL OR least(original_until,security.authority_until(permission))<=clock_timestamp()
 THEN RAISE EXCEPTION 'desk_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION desk.choose(catalog uuid,operation uuid,agreement uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE permission text=CASE WHEN security.trading_access(catalog,'BookTrade') THEN 'BookTrade' ELSE 'AmendTrade' END;
 actor uuid=desk.authority(catalog,permission,true);until_at timestamptz=security.authority_until(permission);saved uuid;
BEGIN
 IF operation IS NULL OR agreement IS NULL OR NOT EXISTS(SELECT FROM desk.agreements a WHERE (a.catalog_id,a.id)=(catalog,agreement))
 THEN RAISE EXCEPTION 'desk_terms';END IF;
 IF EXISTS(SELECT FROM trading.trades t LEFT JOIN desk.admissions a ON(a.catalog_id,a.trade_id,a.revision)=(t.catalog_id,t.id,1)
  WHERE(t.catalog_id,t.operation_id)=(catalog,operation) AND (t.actor_id<>actor OR a.agreement_id IS DISTINCT FROM agreement)) THEN RAISE EXCEPTION 'desk_request_conflict';END IF;
 INSERT INTO desk.booking_choices VALUES(catalog,actor,operation,agreement) ON CONFLICT DO NOTHING;
 SELECT b.agreement_id INTO saved FROM desk.booking_choices b WHERE(b.catalog_id,b.actor_id,b.operation_id)=(catalog,actor,operation);
 IF saved IS DISTINCT FROM agreement THEN RAISE EXCEPTION 'desk_request_conflict';END IF;
 PERFORM desk.require_authority(catalog,permission,until_at);
 RETURN jsonb_build_object('agreementId',saved);
END$$;

CREATE FUNCTION desk.source_visible(catalog uuid,definition uuid) RETURNS boolean LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT security.market_fact_visible(catalog,definition,NULL) IS TRUE AND EXISTS(SELECT FROM market_data.revisions d
  WHERE (d.catalog_id,d.id)=(catalog,definition) AND security.reference_access(d.reference_catalog_id,'ReadMarketData')
  AND security.reference_version_visible(d.reference_catalog_id,d.reference_revision_id))
$$;

CREATE FUNCTION desk.trade_source(catalog uuid,definition uuid,product text) RETURNS boolean LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT desk.source_visible(catalog,definition) AND
  (product NOT IN('thermal_toll','battery_right','solar_right') OR security.trading_access(catalog,'ReadPhysical'))
$$;

CREATE FUNCTION desk.publication(catalog uuid) RETURNS integer LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE n integer;
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT coalesce(max(publication_order),0)+1 INTO n FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,n,pg_current_xact_id());
 RETURN n;
END$$;

-- Choose a revision before testing its status, scope or expiry. Otherwise an old approval
-- can reappear after a hold, amendment or termination. The current credit decision also
-- applies to a new booking with an earlier execution date.
CREATE FUNCTION desk.resolve(catalog uuid,entity uuid,party uuid,product_code text,executed timestamptz,checked_at timestamptz,selected uuid DEFAULT NULL)
 RETURNS jsonb LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE matches uuid[];agreement desk.agreements;credit desk.clearances;at_time timestamptz;reason text;
BEGIN
 IF executed IS NULL OR executed>checked_at OR NOT isfinite(executed) OR entity IS NULL OR party IS NULL OR product_code IS NULL OR NOT product_code=ANY(desk.products())
 THEN RAISE EXCEPTION 'desk_terms';END IF;
 SELECT array_agg(a.id ORDER BY a.code) INTO matches FROM(
  SELECT DISTINCT ON(code) * FROM desk.agreements WHERE catalog_id=catalog AND legal_entity_id=entity AND counterparty_id=party
   AND effective_at<=executed ORDER BY code,effective_at DESC,revision DESC)a
 WHERE (selected IS NULL OR a.id=selected) AND a.status='executed' AND product_code=ANY(a.products) AND (a.valid_to IS NULL OR (executed AT TIME ZONE 'UTC')::date<a.valid_to);
 IF coalesce(cardinality(matches),0)=0 THEN reason='agreement_required';
 ELSIF cardinality(matches)>1 THEN reason='agreement_ambiguous';
 ELSE
  SELECT * INTO agreement FROM desk.agreements WHERE catalog_id=catalog AND id=matches[1];
  FOREACH at_time IN ARRAY ARRAY[executed,checked_at] LOOP
   SELECT * INTO credit FROM desk.clearances WHERE catalog_id=catalog AND legal_entity_id=entity AND counterparty_id=party
    AND product=product_code AND effective_at<=at_time ORDER BY effective_at DESC,revision DESC LIMIT 1;
   IF NOT FOUND THEN reason='credit_required';
   ELSIF credit.status='hold' THEN reason='credit_hold';
   ELSIF credit.status='expired' OR credit.valid_to<=(at_time AT TIME ZONE 'UTC')::date THEN reason='credit_expired';END IF;
   EXIT WHEN reason IS NOT NULL;
  END LOOP;
 END IF;
 RETURN jsonb_build_object('eligible',reason IS NULL,'reason',reason,'choices',(SELECT coalesce(jsonb_agg(jsonb_build_object('id',a.id,'name',a.name,'revision',a.revision) ORDER BY a.code),'[]') FROM desk.agreements a WHERE a.catalog_id=catalog AND a.id=ANY(matches)),'agreementId',agreement.id,'agreement',agreement.name,
  'agreementRevision',agreement.revision,'clearanceId',credit.id,'creditRevision',credit.revision,'procedure',agreement.procedure);
END$$;

CREATE FUNCTION desk.eligibility(catalog uuid,entity uuid,party uuid,product text,executed timestamptz)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM desk.authority(catalog,'ReadTrades',true);
 RETURN desk.resolve(catalog,entity,party,product,executed,clock_timestamp());
END$$;

-- Both swap representations retain the market definition on the second floating Side.
CREATE VIEW desk.swap_sources AS
 SELECT i.catalog_id,i.instrument_id,f.definition_revision_id FROM trading.swap_instruments i
 JOIN trading.swap_floating_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.floating_side_id)
 UNION ALL
 SELECT i.catalog_id,i.instrument_id,p.definition_revision_id FROM trading.swap_instruments i
 JOIN trading.swap_side_periods p ON(p.catalog_id,p.side_id,p.ordinal,p.kind)=(i.catalog_id,i.scheduled_second_id,0,'floating');
ALTER VIEW desk.swap_sources OWNER TO openetrm_security_owner;
REVOKE ALL ON desk.swap_sources FROM PUBLIC;

CREATE FUNCTION desk.admit_revision() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected uuid;definition uuid;source_table text;product text=TG_ARGV[0];pin jsonb;row_data jsonb=to_jsonb(NEW);at_time timestamptz;
BEGIN
 IF row_data->>'event_kind'='terminated' THEN RETURN NEW;END IF;
 IF product='operating' THEN
  SELECT CASE v.kind WHEN 'thermal' THEN 'thermal_toll' WHEN 'battery' THEN 'battery_right' WHEN 'solar' THEN 'solar_right' END INTO STRICT product
  FROM trading.power_operating_instruments i JOIN physical.power_operating_facility_versions v
   ON(v.catalog_id,v.facility_id,v.version)=(i.catalog_id,i.facility_id,i.facility_version)
  WHERE(i.catalog_id,i.instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF coalesce((row_data->>'revision')::int,1)=1 THEN
  SELECT f.executed_at INTO STRICT at_time FROM trading.booking_facts f WHERE(f.catalog_id,f.trade_id)=(NEW.catalog_id,NEW.trade_id);
 ELSE at_time=clock_timestamp();END IF;
 SELECT c.agreement_id INTO selected FROM desk.booking_choices c WHERE(c.catalog_id,c.actor_id,c.operation_id)=(NEW.catalog_id,NEW.actor_id,NEW.operation_id);
 pin=desk.resolve(NEW.catalog_id,NEW.legal_entity_id,NEW.counterparty_id,product,at_time,at_time,selected);
 IF pin->>'reason'='agreement_ambiguous' THEN RAISE EXCEPTION 'desk_agreement_ambiguous'
  USING HINT=jsonb_build_object('operationId',NEW.operation_id,'choices',pin->'choices')::text;END IF;
 IF NOT (pin->>'eligible')::boolean THEN RAISE EXCEPTION '%', 'desk_'||(pin->>'reason') USING ERRCODE='P0001';END IF;
 IF product='ppa' THEN definition=NEW.definition_revision_id;
 ELSIF product='financial_swap' THEN
  SELECT s.definition_revision_id INTO STRICT definition FROM desk.swap_sources s WHERE(s.catalog_id,s.instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 ELSE
  source_table=CASE product WHEN 'physical_gas' THEN 'gas_instruments' WHEN 'gas_storage' THEN 'gas_storage_contracts'
   WHEN 'gas_transport' THEN 'gas_transport_financial_instruments' WHEN 'gas_park_loan' THEN 'gas_park_loan_instruments'
   WHEN 'gas_park_custody' THEN 'gas_park_custody_instruments' WHEN 'heat_rate_call' THEN 'heat_rate_call_instruments'
   WHEN 'ppa' THEN 'ppa_instruments' WHEN 'physical_crude' THEN 'crude_instruments' WHEN 'physical_lng' THEN 'lng_instruments'
   WHEN 'rec_forward' THEN 'rec_forward_instruments' ELSE 'power_operating_instruments' END;
  EXECUTE format('SELECT definition_revision_id FROM trading.%I WHERE(catalog_id,instrument_id)=($1,$2)',source_table)
   INTO STRICT definition USING NEW.catalog_id,NEW.instrument_id;
 END IF;
 INSERT INTO desk.admissions VALUES(NEW.catalog_id,NEW.trade_id,coalesce((row_data->>'revision')::int,1),NEW.book_id,product,
  (pin->>'agreementId')::uuid,(pin->>'clearanceId')::uuid,definition,at_time,NEW.effective_at,NEW.publication_order);
 RETURN NEW;
END$$;
DO $$DECLARE t record;
BEGIN
 FOR t IN SELECT * FROM (VALUES ('gas_trade_revisions','physical_gas'),('gas_storage_trade_revisions','gas_storage'),
 ('gas_transport_financial_trades','gas_transport'),('gas_park_loan_trades','gas_park_loan'),('gas_park_custody_trades','gas_park_custody'),
 ('operating_trade_revisions','operating'),('heat_rate_call_trade_revisions','heat_rate_call'),('swap_trade_revisions','financial_swap'),
 ('ppa_trade_revisions','ppa'),('crude_trade_revisions','physical_crude'),('lng_trade_revisions','physical_lng'),
 ('rec_forward_trade_revisions','rec_forward'))v(tab,product)
 LOOP EXECUTE format('CREATE TRIGGER bilateral_admission AFTER INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION desk.admit_revision(%L)',t.tab,t.product);END LOOP;
END$$;

CREATE FUNCTION desk.record(catalog uuid,operation uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
<<entry>>
DECLARE kind text=body->>'kind';permission text;actor uuid;original_until timestamptz;expected int;at_time timestamptz;ends date;
 entity uuid;party uuid;er int;pr int;trade uuid;tr int;code text;product text;products text[];document bytea;digest bytea;
 terms jsonb;fingerprint bytea;request_hash bytea;receipt desk.requests;existing uuid;rev int;head int;identifier uuid;result jsonb;
BEGIN
 permission=CASE kind WHEN 'agreement' THEN 'ManageAgreements' WHEN 'credit' THEN 'ManageCounterpartyCredit' WHEN 'confirmation' THEN 'ConfirmBilateralTrades' END;
 IF permission IS NULL OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'desk_terms';END IF;
 actor=desk.authority(catalog,permission,true);original_until=security.authority_until(permission);
 IF pg_column_size(body)>12000000 THEN RAISE EXCEPTION 'desk_document_size';END IF;
 expected=(body->>'expectedRevision')::int;
 at_time=(body->>'effectiveAt')::timestamptz;
 IF expected IS NULL OR expected<0 OR at_time IS NULL OR NOT isfinite(at_time) THEN RAISE EXCEPTION 'desk_terms';END IF;
 terms=body-'expectedRevision'-'pdf';
 IF kind='agreement' THEN
  IF trading.rec_keys(body,ARRAY['kind','code','name','agreementType','products','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','effectiveAt','validTo','status','procedure','provisions','filename','pdf','expectedRevision']) IS NOT TRUE
  THEN RAISE EXCEPTION 'desk_terms';END IF;
  IF body->>'pdf' IS NOT NULL THEN
   document=decode(body->>'pdf','base64');digest=sha256(document);
   IF octet_length(document) NOT BETWEEN 10 AND 8388608 OR substring(document FROM 1 FOR 5)<>convert_to('%PDF-','UTF8') OR
    body->>'filename' IS NULL OR length(body->>'filename')>180 OR body->>'filename' ~ '[/\\[:cntrl:]]' OR lower(body->>'filename') NOT LIKE '%.pdf'
   THEN RAISE EXCEPTION 'desk_document';END IF;
  END IF;
  IF body->>'status'='executed' AND document IS NULL THEN RAISE EXCEPTION 'desk_document_required';END IF;
  code=body->>'code';
  SELECT array_agg(DISTINCT x ORDER BY x) INTO products FROM jsonb_array_elements_text(body->'products')x;
  terms=terms||jsonb_build_object('digest',encode(digest,'hex'),'products',products);
 ELSIF kind='credit' THEN
  IF trading.rec_keys(body,ARRAY['kind','product','legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','effectiveAt','validTo','status','reason','expectedRevision']) IS NOT TRUE
  THEN RAISE EXCEPTION 'desk_terms';END IF;
  product=body->>'product';
 ELSE
  IF trading.rec_keys(body,ARRAY['kind','tradeId','tradeRevision','effectiveAt','reference','evidence','status','expectedRevision']) IS NOT TRUE
   OR at_time>clock_timestamp() THEN RAISE EXCEPTION 'desk_confirmation';END IF;
  trade=(body->>'tradeId')::uuid;tr=(body->>'tradeRevision')::int;
 END IF;
 terms=terms||jsonb_build_object('effectiveAt',at_time AT TIME ZONE 'UTC');
 fingerprint=sha256(convert_to(terms::text,'UTF8'));
 request_hash=sha256(fingerprint||int4send(expected));
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM desk.authority(catalog,permission,true);
 PERFORM desk.require_authority(catalog,permission,original_until);
 IF kind='confirmation' AND NOT EXISTS(SELECT FROM desk.admissions a WHERE(a.catalog_id,a.trade_id,a.revision)=(catalog,trade,tr)
   AND a.executed_at<=at_time AND desk.trade_source(catalog,a.definition_revision_id,a.product) AND security.trading_access(catalog,'ReadTrades'))
 THEN RAISE EXCEPTION 'desk_confirmation';END IF;
 SELECT * INTO receipt FROM desk.requests WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF receipt.command_hash<>request_hash THEN RAISE EXCEPTION 'desk_request_conflict';END IF;
  RETURN receipt.outcome;
 END IF;
 IF kind IN ('agreement','credit') THEN
  entity=(body->>'legalEntityId')::uuid;er=(body->>'legalEntityRevision')::int;
  party=(body->>'counterpartyId')::uuid;pr=(body->>'counterpartyRevision')::int;ends=(body->>'validTo')::date;
  PERFORM security.require_trade_parties(catalog,jsonb_build_object('legalEntityId',entity,'legalEntityRevision',er,
   'counterpartyId',party,'counterpartyRevision',pr,'tradeDate',(at_time AT TIME ZONE 'UTC')::date),at_time);
 END IF;
 IF kind='agreement' THEN
  IF EXISTS(SELECT 1 FROM desk.agreements a WHERE a.catalog_id=catalog AND a.code=entry.code AND (a.legal_entity_id,a.counterparty_id)<>(entity,party)) THEN RAISE EXCEPTION 'desk_party';END IF;
  SELECT a.id,a.revision INTO existing,rev FROM desk.agreements a WHERE a.catalog_id=catalog AND a.code=entry.code AND a.fact_hash=fingerprint;
  SELECT max(a.revision) INTO head FROM desk.agreements a WHERE a.catalog_id=catalog AND a.code=entry.code;
 ELSIF kind='credit' THEN
  SELECT c.id,c.revision INTO existing,rev FROM desk.clearances c WHERE(c.catalog_id,c.legal_entity_id,c.counterparty_id,c.product,c.fact_hash)=(catalog,entity,party,entry.product,fingerprint);
  SELECT max(c.revision) INTO head FROM desk.clearances c WHERE(c.catalog_id,c.legal_entity_id,c.counterparty_id,c.product)=(catalog,entity,party,entry.product);
 ELSE
  SELECT c.id,c.revision INTO existing,rev FROM desk.confirmations c WHERE(c.catalog_id,c.trade_id,c.trade_revision,c.fact_hash)=(catalog,trade,tr,fingerprint);
  SELECT max(c.revision) INTO head FROM desk.confirmations c WHERE(c.catalog_id,c.trade_id,c.trade_revision)=(catalog,trade,tr);
 END IF;
 IF existing IS NULL THEN
  IF expected<>coalesce(head,0) THEN RAISE EXCEPTION 'desk_stale';END IF;
  identifier=gen_random_uuid();rev=expected+1;
  IF kind='agreement' THEN
   IF document IS NOT NULL THEN INSERT INTO desk.documents VALUES(catalog,digest,document) ON CONFLICT DO NOTHING;END IF;
   INSERT INTO desk.agreements VALUES(catalog,identifier,code,rev,entity,er,party,pr,body->>'name',body->>'agreementType',products,at_time,ends,
    body->>'status',body->>'procedure',body->>'provisions',body->>'filename',digest,actor,desk.publication(catalog),fingerprint);
  ELSIF kind='credit' THEN
   INSERT INTO desk.clearances VALUES(catalog,identifier,rev,entity,er,party,pr,product,at_time,ends,body->>'status',body->>'reason',actor,desk.publication(catalog),fingerprint);
  ELSE
   INSERT INTO desk.confirmations VALUES(catalog,identifier,trade,tr,rev,body->>'status',body->>'reference',body->>'evidence',at_time,actor,desk.publication(catalog),fingerprint);
  END IF;
 ELSE identifier=existing;END IF;
 result=jsonb_build_object('id',identifier,'revision',rev);
 INSERT INTO desk.requests VALUES(catalog,actor,operation,request_hash,result);
 PERFORM desk.require_authority(catalog,permission,original_until);
 RETURN result;
END$$;

CREATE FUNCTION desk.visible(catalog uuid,publication integer,at_time timestamptz) RETURNS boolean
 LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT (security.current_write_context() AND security.trading_access(catalog,'ReadTrades'))
   OR security.trading_visible(catalog,publication,at_time)
$$;

CREATE FUNCTION desk.directory(catalog uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;
BEGIN
 PERFORM desk.authority(catalog,'ReadTradeParties',false);
 SELECT jsonb_build_object('products',desk.products(),'working',security.current_write_context(),
 'entities',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.name),'[]') FROM (SELECT DISTINCT ON (id) id,name,revision FROM trading.legal_entities p
  WHERE p.catalog_id=catalog AND desk.visible(catalog,p.publication_order,p.effective_at) AND p.effective_at<=clock_timestamp() ORDER BY id,p.effective_at DESC,revision DESC)x),
 'parties',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.name),'[]') FROM (SELECT DISTINCT ON (id) id,name,revision FROM trading.counterparties p
  WHERE p.catalog_id=catalog AND desk.visible(catalog,p.publication_order,p.effective_at) AND p.effective_at<=clock_timestamp() ORDER BY id,p.effective_at DESC,revision DESC)x),
 'agreements',(SELECT coalesce(jsonb_agg(to_jsonb(a)-ARRAY['fact_hash','catalog_id','publication_order','actor_id','digest','provisions'] ||CASE WHEN security.trading_access(catalog,'ManageAgreements') THEN jsonb_build_object('provisions',a.provisions) ELSE '{}'::jsonb END
  ||jsonb_build_object('sha256',encode(a.digest,'hex')) ORDER BY a.code,a.revision DESC),'[]') FROM desk.agreements a
  WHERE a.catalog_id=catalog AND desk.visible(catalog,a.publication_order,a.effective_at)),
 'clearances',(SELECT coalesce(jsonb_agg(to_jsonb(c)-ARRAY['fact_hash','catalog_id','publication_order','actor_id','reason'] ||CASE WHEN security.trading_access(catalog,'ManageCounterpartyCredit') THEN jsonb_build_object('reason',c.reason) ELSE '{}'::jsonb END ORDER BY c.product,c.revision DESC),'[]') FROM desk.clearances c
  WHERE c.catalog_id=catalog AND desk.visible(catalog,c.publication_order,c.effective_at)),
 'trades',(SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.executed_at DESC),'[]') FROM (
   SELECT a.trade_id,a.revision,a.book_id,a.product,a.agreement_id,a.clearance_id,a.executed_at,coalesce((
    SELECT jsonb_agg(to_jsonb(c)-ARRAY['catalog_id','actor_id','fact_hash','publication_order'] ORDER BY c.revision DESC)
    FROM desk.confirmations c WHERE (c.catalog_id,c.trade_id,c.trade_revision)=(a.catalog_id,a.trade_id,a.revision)
    AND desk.visible(catalog,c.publication_order,c.effective_at)),'[]') confirmations
   FROM desk.admissions a WHERE a.catalog_id=catalog AND security.trading_access(catalog,'ReadTrades') AND desk.trade_source(catalog,a.definition_revision_id,a.product)
    AND desk.visible(catalog,a.publication_order,a.effective_at)
   ORDER BY a.publication_order DESC LIMIT 256)x),
 'canEdit',security.trading_access(catalog,'ManageAgreements'),'canCredit',security.trading_access(catalog,'ManageCounterpartyCredit'),
 'canDownload',security.trading_access(catalog,'ReadAgreementDocuments'),'canConfirm',security.trading_access(catalog,'ConfirmBilateralTrades')) INTO result;
 RETURN result;
END$$;
CREATE FUNCTION desk.document(catalog uuid,agreement uuid) RETURNS TABLE(filename text,content bytea)
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM desk.authority(catalog,'ReadAgreementDocuments',false);
 RETURN QUERY SELECT a.filename,d.content FROM desk.agreements a JOIN desk.documents d USING(catalog_id,digest)
 WHERE(a.catalog_id,a.id)=(catalog,agreement) AND desk.visible(a.catalog_id,a.publication_order,a.effective_at);
END$$;

-- No session has raw access to files or approvals. The approved readers are the only projections.
DO $$DECLARE t text;f regprocedure;BEGIN
 FOREACH t IN ARRAY ARRAY['documents','agreements','clearances','admissions','confirmations','requests','booking_choices'] LOOP
  EXECUTE format('ALTER TABLE desk.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE desk.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE desk.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY internal_content ON desk.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON desk.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p WHERE p.pronamespace='desk'::regnamespace LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION desk.require_authority(uuid,text,timestamptz),desk.choose(uuid,uuid,uuid),desk.record(uuid,uuid,jsonb),desk.directory(uuid),desk.document(uuid,uuid),desk.eligibility(uuid,uuid,uuid,text,timestamptz) TO openetrm_runtime;
COMMIT;
