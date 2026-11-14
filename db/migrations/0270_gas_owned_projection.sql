-- Explicit surviving-inventory marks and saved quantity-performance assumptions.
-- The retained journal below is a knowledge-only source, never a future state.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

-- Keep public function identities and accept only the reviewed predecessor
-- definitions, owners, settings and privileges before extending their behavior.
DO $upgrade$DECLARE item record;f pg_proc;definition text;needle text;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])','609c52796c9c74f841a0ca2847dd1012',true,'jsonb','v',true),
  ('security.gas_owned_admission(uuid,text,jsonb,boolean)','5456200c25c8774ab87937d4187ff0a5',true,'jsonb','v',true),
  ('trading.require_gas_owned_head(uuid,uuid,uuid,timestamptz)','2e1ee59a91304e8a17d1ca21959e5e2b',false,'void','s',false))
  v(signature,body_hash,definer,result_type,volatility,runtime_execute) LOOP
  SELECT p.* INTO f FROM pg_proc p WHERE p.oid=to_regprocedure(item.signature);
  IF NOT FOUND OR md5(f.prosrc)<>item.body_hash OR f.prosecdef<>item.definer OR f.proowner<>'openetrm_security_owner'::regrole
   OR f.prorettype<>item.result_type::regtype OR f.prokind<>'f' OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql'
   OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile::text<>item.volatility
   OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
   OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') IS DISTINCT FROM item.runtime_execute
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable OR a.privilege_type<>'EXECUTE'
    OR a.grantee NOT IN(f.proowner,CASE WHEN item.runtime_execute THEN 'openetrm_runtime'::regrole ELSE f.proowner END))
  THEN RAISE EXCEPTION 'gas_projection_upgrade: unexpected helper %',item.signature;END IF;
  IF item.signature LIKE 'security.read_gas_owned_holdings(%' THEN
   IF to_regprocedure('security.read_gas_owned_holdings_original(uuid,uuid,timestamptz,uuid[])') IS NOT NULL THEN RAISE EXCEPTION 'gas_projection_upgrade: unexpected private helper';END IF;
   definition=pg_get_functiondef(f.oid);needle='security.read_gas_owned_holdings(';
   IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_projection_upgrade: unexpected helper %',item.signature;END IF;
   EXECUTE replace(definition,needle,'security.read_gas_owned_holdings_original(');
   ALTER FUNCTION security.read_gas_owned_holdings_original(uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
   REVOKE ALL ON FUNCTION security.read_gas_owned_holdings_original(uuid,uuid,timestamptz,uuid[]) FROM PUBLIC,openetrm_runtime;
  END IF;
 END LOOP;
END$upgrade$;

CREATE TABLE trading.gas_owned_mark_bindings(
 catalog_id uuid NOT NULL,position_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),
 definition_revision_id uuid NOT NULL,mark_curve_code ref_data.code NOT NULL,mark_contract_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,position_id,revision),
 FOREIGN KEY(catalog_id,position_id) REFERENCES trading.gas_owned_positions(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,mark_curve_code,mark_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_owned_projection_profiles(
 catalog_id uuid NOT NULL,id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),account_id uuid NOT NULL,
 implementation text NOT NULL CHECK(implementation='gas_quantity_lifecycle_projection_v1'),
 quantity_disposition text NOT NULL CHECK(quantity_disposition IN('retain_quantity_obligations_v1','perform_full_quantity_at_contractual_due_v1')),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id,revision),FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_owned_projection_performances(
 catalog_id uuid NOT NULL,profile_id uuid NOT NULL,profile_revision integer NOT NULL,position_id uuid NOT NULL,
 quantity ref_data.exact_number NOT NULL CHECK(quantity>0),destination_account_id uuid,
 inventory_definition_revision_id uuid,inventory_curve_code ref_data.code,inventory_contract_id uuid,availability text,
 PRIMARY KEY(catalog_id,profile_id,profile_revision,position_id),
 FOREIGN KEY(catalog_id,profile_id,profile_revision) REFERENCES trading.gas_owned_projection_profiles(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,position_id) REFERENCES trading.gas_owned_positions(catalog_id,id),
 FOREIGN KEY(catalog_id,destination_account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,inventory_definition_revision_id,inventory_curve_code,inventory_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 CHECK(num_nonnulls(destination_account_id,inventory_definition_revision_id,inventory_curve_code,inventory_contract_id,availability) IN(0,5)),
 CHECK(availability IS NULL OR availability='available_at_projected_title_v1')
);
CREATE TABLE trading.gas_owned_projection_supplies(
 catalog_id uuid NOT NULL,profile_id uuid NOT NULL,profile_revision integer NOT NULL,position_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>0),
 lot_id uuid,incoming_position_id uuid,reservation_id uuid,quantity ref_data.exact_number NOT NULL CHECK(quantity>0),
 PRIMARY KEY(catalog_id,profile_id,profile_revision,position_id,ordinal),
 FOREIGN KEY(catalog_id,profile_id,profile_revision,position_id) REFERENCES trading.gas_owned_projection_performances(catalog_id,profile_id,profile_revision,position_id),
 FOREIGN KEY(catalog_id,lot_id) REFERENCES trading.gas_owned_lots(catalog_id,id),
 FOREIGN KEY(catalog_id,incoming_position_id) REFERENCES trading.gas_owned_positions(catalog_id,id),
 FOREIGN KEY(catalog_id,reservation_id) REFERENCES trading.gas_owned_reservations(catalog_id,id),
 CHECK(num_nonnulls(lot_id,incoming_position_id)=1),CHECK(incoming_position_id IS NULL OR reservation_id IS NULL)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_owned_mark_bindings','gas_owned_projection_profiles','gas_owned_projection_performances','gas_owned_projection_supplies'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['gas_owned_mark_bindings','gas_owned_projection_profiles'] LOOP
  EXECUTE format('CREATE TRIGGER native_publication BEFORE INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication()',t);
 END LOOP;
END$$;

CREATE OR REPLACE FUNCTION trading.require_gas_owned_head(catalog uuid,account uuid,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM(
  SELECT a.publication_order,a.effective_at FROM trading.gas_owned_accounts a WHERE(a.catalog_id,a.id)=(catalog,account)
  UNION ALL SELECT e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00' FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id)=(catalog,account)
  UNION ALL SELECT p.publication_order,p.effective_at FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT c.publication_order,c.effective_at FROM trading.gas_owned_consumptions c JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(c.catalog_id,c.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.gas_owned_reservations r JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(r.catalog_id,r.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT x.publication_order,x.effective_at FROM trading.gas_owned_releases x JOIN trading.gas_owned_reservations r ON(r.catalog_id,r.id)=(x.catalog_id,x.reservation_id)
   JOIN trading.gas_owned_lots l ON(l.catalog_id,l.id)=(r.catalog_id,r.lot_id) WHERE(l.catalog_id,l.account_id)=(catalog,account)
  UNION ALL SELECT s.publication_order,s.effective_at FROM trading.gas_owned_claim_settlements s JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT f.publication_order,f.effective_at FROM trading.gas_owned_fees f JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00' FROM trading.gas_owned_fee_payments x JOIN trading.gas_owned_fees f ON(f.catalog_id,f.id)=(x.catalog_id,x.fee_id)
   JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT b.publication_order,b.effective_at FROM trading.gas_owned_mark_bindings b JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(b.catalog_id,b.position_id) WHERE(p.catalog_id,p.account_id)=(catalog,account)
  UNION ALL SELECT f.publication_order,f.effective_at FROM trading.gas_owned_projection_profiles f WHERE(f.catalog_id,f.account_id)=(catalog,account)
 )h WHERE h.effective_at<=as_of AND trading.gas_owned_visible(catalog,h.publication_order,h.effective_at,context,as_of) IS NOT TRUE)
 THEN RAISE EXCEPTION 'gas_owned_context';END IF;
END$$;

CREATE FUNCTION trading.require_gas_owned_mark(catalog uuid,account uuid,definition uuid,curve text,contract uuid) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE a trading.gas_owned_accounts;r market_data.revisions;q ref_data.quote_specifications;location text;BEGIN
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
 SELECT * INTO STRICT r FROM market_data.revisions x WHERE(x.catalog_id,x.id)=(catalog,definition);
 SELECT c.location_code INTO STRICT location FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,definition,curve);
 SELECT quote.* INTO STRICT q FROM market_data.curves c JOIN ref_data.quote_specifications quote
  ON(quote.catalog_id,quote.revision_id,quote.code)=(r.reference_catalog_id,r.reference_revision_id,c.quote_code)
  WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,definition,curve);
 IF(a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.currency_code,a.location_code) IS DISTINCT FROM
  (r.reference_catalog_id,r.reference_revision_id,q.quantity_code,q.currency_code,location) OR q.kind IS DISTINCT FROM 'outright'
  OR NOT EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(catalog,definition,curve,contract))
 THEN RAISE EXCEPTION 'gas_owned_mark';END IF;
END$$;
CREATE FUNCTION trading.guard_gas_owned_mark_binding() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p trading.gas_owned_positions;BEGIN
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.position_id);
 IF p.kind<>'owned_stock' OR NEW.effective_at<p.effective_at THEN RAISE EXCEPTION 'gas_owned_mark';END IF;
 PERFORM trading.require_gas_owned_mark(NEW.catalog_id,p.account_id,NEW.definition_revision_id,NEW.mark_curve_code,NEW.mark_contract_id);RETURN NEW;
END$$;
CREATE TRIGGER exact_owned_mark BEFORE INSERT ON trading.gas_owned_mark_bindings FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_mark_binding();
CREATE FUNCTION trading.guard_gas_owned_projection_child() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.gas_owned_projection_profiles f JOIN trading.publications p
  ON(p.catalog_id,p.publication_order)=(f.catalog_id,f.publication_order)
  WHERE(f.catalog_id,f.id,f.revision)=(NEW.catalog_id,NEW.profile_id,NEW.profile_revision) AND p.publication_xid=pg_current_xact_id())
 THEN RAISE EXCEPTION 'gas_owned_publication';END IF;RETURN NEW;
END$$;

CREATE FUNCTION trading.gas_owned_projection_keys(body jsonb,keys text[],nullable text[]) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&keys) OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE NOT(k=ANY(keys)))
  OR EXISTS(SELECT 1 FROM jsonb_each(body)e WHERE e.value='null'::jsonb AND NOT(e.key=ANY(nullable)))
 THEN RAISE EXCEPTION 'gas_owned_input';END IF;
END$$;
CREATE FUNCTION trading.gas_owned_projection_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=trading.gas_owned_fact(body);key text;items jsonb;BEGIN
 FOREACH key IN ARRAY ARRAY['profileId','positionId','destinationAccountId','inventoryDefinitionRevisionId','inventoryContractId','incomingPositionId'] LOOP
  IF body->>key IS NOT NULL THEN result=result||jsonb_build_object(key,(body->>key)::uuid);END IF;
 END LOOP;
 FOREACH key IN ARRAY ARRAY['performances','supply'] LOOP
  IF body?key THEN
   IF jsonb_typeof(body->key) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_owned_input';END IF;
   SELECT coalesce(jsonb_agg(normalized ORDER BY normalized::text),'[]') INTO items FROM(
    SELECT trading.gas_owned_projection_fact(value) normalized FROM jsonb_array_elements(body->key))v;
   result=result||jsonb_build_object(key,items);
  END IF;
 END LOOP;RETURN result;
END$$;
CREATE OR REPLACE FUNCTION security.gas_owned_admission(catalog uuid,action text,body jsonb,automatic boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;configuring boolean=action IN('account','bind_policy','mark_binding','projection_profile');requires_jobs boolean=automatic;BEGIN
 IF automatic IS NULL THEN RAISE EXCEPTION 'gas_owned_input';END IF;
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'),security.authority_until('RecordGasOwnedHoldings'));
 IF configuring THEN until_at=least(until_at,security.authority_until('ConfigureGasOwnedHoldings'));END IF;
 IF automatic OR action='bind_policy' AND EXISTS(SELECT 1 FROM lifecycle.action_policies p
  WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer) AND p.mode='automatic')
 THEN requires_jobs=true;until_at=least(until_at,security.authority_until('RunJobs'));END IF;
 PERFORM security.require_gas_owned_commit(catalog,until_at,configuring,requires_jobs);
 RETURN jsonb_build_object('until',until_at,'configuring',configuring,'requiresJobs',requires_jobs);
END$$;
CREATE FUNCTION security.configure_gas_owned_projection(catalog uuid,operation uuid,action text,account uuid,body jsonb,as_of timestamptz,context uuid,receipt_only boolean)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;a trading.gas_owned_accounts;p trading.gas_owned_positions;destination trading.gas_owned_accounts;
 prior trading.gas_owned_commands;same_fact trading.gas_owned_commands;canonical jsonb;outcome jsonb;publication integer;version integer;current_version integer;
 binding trading.gas_owned_mark_bindings;profile trading.gas_owned_projection_profiles;at_time timestamptz;item jsonb;supply jsonb;ordinal integer;number numeric;BEGIN
 admission=security.gas_owned_admission(catalog,action,body,false);
 IF action IS NULL OR action NOT IN('mark_binding','projection_profile') OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid
  OR account IS NULL OR account='00000000-0000-0000-0000-000000000000'::uuid OR context IS NULL OR as_of IS NULL OR NOT isfinite(as_of)
  OR receipt_only IS NULL OR jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>2097152
 THEN RAISE EXCEPTION 'gas_owned_input';END IF;
 body=trading.gas_owned_projection_fact(body);
 IF action='mark_binding' THEN PERFORM trading.gas_owned_projection_keys(body,ARRAY['positionId','definitionRevisionId','markCurve','markContractId','expectedRevision','effectiveAt'],ARRAY[]::text[]);
 ELSE PERFORM trading.gas_owned_projection_keys(body,ARRAY['profileId','expectedRevision','implementation','quantityDisposition','effectiveAt','performances'],ARRAY[]::text[]);END IF;
 IF NOT(body?'expectedRevision') THEN RAISE EXCEPTION 'gas_owned_input';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 canonical=jsonb_build_object('action',action,'accountId',account,'body',body,'asOf',as_of,'contextId',context,'policyId',NULL,'policyRevision',NULL,'automatic',false);
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
 IF security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE OR security.market_fact_visible(catalog,a.definition_revision_id,NULL) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 -- Exact references remain currently authorized on both command replay and
 -- older business-fact acknowledgement; neither branch rereads mutable heads.
 IF action='mark_binding' THEN
  SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(body->>'positionId')::uuid,account);
  IF p.kind<>'owned_stock' THEN RAISE EXCEPTION 'gas_owned_mark';END IF;
  PERFORM trading.require_gas_owned_mark(catalog,account,(body->>'definitionRevisionId')::uuid,body->>'markCurve',(body->>'markContractId')::uuid);
  IF security.market_fact_visible(catalog,(body->>'definitionRevisionId')::uuid,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 ELSE
  IF body->>'implementation'<>'gas_quantity_lifecycle_projection_v1' OR body->>'quantityDisposition' NOT IN('retain_quantity_obligations_v1','perform_full_quantity_at_contractual_due_v1')
   OR jsonb_array_length(body->'performances')>2048 OR(body->>'profileId')::uuid='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'gas_owned_projection_policy';END IF;
  FOR item IN SELECT value FROM jsonb_array_elements(body->'performances') LOOP
   PERFORM trading.gas_owned_projection_keys(item,ARRAY['positionId','quantity','destinationAccountId','inventoryDefinitionRevisionId','inventoryCurve','inventoryContractId','availability','supply'],
    ARRAY['destinationAccountId','inventoryDefinitionRevisionId','inventoryCurve','inventoryContractId','availability']);
   SELECT x.* INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(item->>'positionId')::uuid,account);
   IF p.kind NOT IN('operator_claim','loan_return') OR p.settlement_mode<>'return_quantity_v1' OR p.due_at IS NULL OR trading.gas_owned_quantity(item->>'quantity')<=0
    OR jsonb_array_length(item->'supply')>4096 THEN RAISE EXCEPTION 'gas_owned_projection_policy';END IF;
   IF p.quantity>0 THEN
    SELECT * INTO STRICT destination FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,(item->>'destinationAccountId')::uuid);
    IF(destination.book_id,destination.legal_entity_id,destination.legal_entity_revision,destination.quantity_code,destination.currency_code,destination.location_code,destination.reference_catalog_id,destination.reference_revision_id)
     IS DISTINCT FROM(a.book_id,a.legal_entity_id,a.legal_entity_revision,a.quantity_code,a.currency_code,a.location_code,a.reference_catalog_id,a.reference_revision_id)
     OR destination.kind<>'owned_inventory' OR item->>'availability'<>'available_at_projected_title_v1' OR jsonb_array_length(item->'supply')<>0 THEN RAISE EXCEPTION 'gas_owned_projection_policy';END IF;
    PERFORM trading.require_gas_owned_mark(catalog,destination.id,(item->>'inventoryDefinitionRevisionId')::uuid,item->>'inventoryCurve',(item->>'inventoryContractId')::uuid);
    IF security.market_fact_visible(catalog,(item->>'inventoryDefinitionRevisionId')::uuid,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
   ELSE
    IF item->>'destinationAccountId' IS NOT NULL OR item->>'inventoryDefinitionRevisionId' IS NOT NULL OR item->>'inventoryCurve' IS NOT NULL OR item->>'inventoryContractId' IS NOT NULL OR item->>'availability' IS NOT NULL
     OR jsonb_array_length(item->'supply')=0 THEN RAISE EXCEPTION 'gas_owned_projection_policy';END IF;
    number=0;
    FOR supply IN SELECT value FROM jsonb_array_elements(item->'supply') LOOP
     PERFORM trading.gas_owned_projection_keys(supply,ARRAY['lotId','incomingPositionId','reservationId','quantity'],ARRAY['lotId','incomingPositionId','reservationId']);
     IF num_nonnulls(supply->>'lotId',supply->>'incomingPositionId')<>1 OR supply->>'incomingPositionId' IS NOT NULL AND supply->>'reservationId' IS NOT NULL
      OR trading.gas_owned_quantity(supply->>'quantity')<=0 THEN RAISE EXCEPTION 'gas_projection_supply';END IF;
     number=number+trading.gas_owned_quantity(supply->>'quantity');
    END LOOP;
    IF number<>trading.gas_owned_quantity(item->>'quantity') THEN RAISE EXCEPTION 'gas_projection_supply';END IF;
   END IF;
  END LOOP;
 END IF;
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,true,false);RETURN prior.outcome;
 END IF;
 IF receipt_only THEN PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,true,false);RETURN NULL;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO same_fact FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.account_id)=(catalog,account) AND c.request->>'action'=action
  AND((c.request->'body')-'expectedRevision')=(body-'expectedRevision') ORDER BY(c.outcome->>'acknowledgedRevision')::integer LIMIT 1;
 IF action='mark_binding' THEN SELECT * INTO binding FROM trading.gas_owned_mark_bindings b WHERE(b.catalog_id,b.position_id)=(catalog,(body->>'positionId')::uuid) ORDER BY b.revision DESC LIMIT 1;current_version=coalesce(binding.revision,0);
 ELSE SELECT * INTO profile FROM trading.gas_owned_projection_profiles f WHERE(f.catalog_id,f.id)=(catalog,(body->>'profileId')::uuid) ORDER BY f.revision DESC LIMIT 1;
  IF profile.account_id IS NOT NULL AND profile.account_id<>account THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;current_version=coalesce(profile.revision,0);END IF;
 IF same_fact.request_id IS NOT NULL THEN
  outcome=same_fact.outcome||jsonb_build_object('operationId',operation,'changed',false,'semanticAcknowledgement',true,'currentRevision',current_version);
 ELSE
  at_time=trading.gas_owned_at(body->>'effectiveAt');
  IF action='mark_binding' AND binding.revision IS NOT NULL AND trading.gas_owned_visible(catalog,binding.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR action='projection_profile' AND profile.revision IS NOT NULL AND trading.gas_owned_visible(catalog,profile.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_owned_context';END IF;
  IF current_version<>(body->>'expectedRevision')::integer OR trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of) IS NOT TRUE
   THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,a.definition_revision_id);
  PERFORM trading.require_gas_owned_head(catalog,account,context,as_of);
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  version=current_version+1;INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  IF action='mark_binding' THEN
   PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,(body->>'definitionRevisionId')::uuid);
   IF trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_context';END IF;
   INSERT INTO trading.gas_owned_mark_bindings VALUES(catalog,p.id,version,(body->>'definitionRevisionId')::uuid,body->>'markCurve',(body->>'markContractId')::uuid,actor,publication,at_time);
  ELSE
   IF at_time>as_of OR body->>'quantityDisposition'='retain_quantity_obligations_v1' AND jsonb_array_length(body->'performances')<>0 THEN RAISE EXCEPTION 'gas_owned_projection_policy';END IF;
   INSERT INTO trading.gas_owned_projection_profiles VALUES(catalog,(body->>'profileId')::uuid,version,account,body->>'implementation',body->>'quantityDisposition',actor,publication,at_time);
   FOR item IN SELECT value FROM jsonb_array_elements(body->'performances') LOOP
    SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(catalog,(item->>'positionId')::uuid);
    IF trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_context';END IF;
    IF item->>'inventoryDefinitionRevisionId' IS NOT NULL THEN PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,(item->>'inventoryDefinitionRevisionId')::uuid);END IF;
    INSERT INTO trading.gas_owned_projection_performances VALUES(catalog,(body->>'profileId')::uuid,version,p.id,trading.gas_owned_quantity(item->>'quantity'),
     (item->>'destinationAccountId')::uuid,(item->>'inventoryDefinitionRevisionId')::uuid,item->>'inventoryCurve',(item->>'inventoryContractId')::uuid,item->>'availability');
   END LOOP;
   FOR item IN SELECT value FROM jsonb_array_elements(body->'performances') LOOP
    ordinal=0;
    FOR supply IN SELECT value FROM jsonb_array_elements(item->'supply') LOOP
     ordinal=ordinal+1;INSERT INTO trading.gas_owned_projection_supplies VALUES(catalog,(body->>'profileId')::uuid,version,(item->>'positionId')::uuid,ordinal,
      (supply->>'lotId')::uuid,(supply->>'incomingPositionId')::uuid,(supply->>'reservationId')::uuid,trading.gas_owned_quantity(supply->>'quantity'));
    END LOOP;
   END LOOP;
  END IF;
  outcome=jsonb_build_object('operationId',operation,'accountId',account,'id',coalesce((body->>'positionId')::uuid,(body->>'profileId')::uuid),'action',action,
   'changed',true,'revision',version,'acknowledgedRevision',version,'currentRevision',version,'semanticAcknowledgement',false);
 END IF;
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,account,canonical,outcome,(admission->>'until')::timestamptz,true,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,true,false);RETURN outcome;
END$$;
CREATE TRIGGER native_child BEFORE INSERT ON trading.gas_owned_projection_performances FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_projection_child();
CREATE TRIGGER native_child BEFORE INSERT ON trading.gas_owned_projection_supplies FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_projection_child();

-- Fresh marks change the instrument's economic definition, not native owner or
-- title. Exact prior contexts retain the previous selected mark unchanged.
CREATE OR REPLACE FUNCTION security.read_gas_owned_holdings(catalog uuid,context uuid,at_time timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;positions jsonb;item jsonb;binding trading.gas_owned_mark_bindings;BEGIN
 result=security.read_gas_owned_holdings_original(catalog,context,at_time,books);positions='[]';
 FOR item IN SELECT value FROM jsonb_array_elements(result->'positions') LOOP
  SELECT * INTO binding FROM trading.gas_owned_mark_bindings b WHERE(b.catalog_id,b.position_id)=(catalog,(item->>'id')::uuid)
   AND security.trading_visible(b.catalog_id,b.publication_order,b.effective_at) ORDER BY b.revision DESC LIMIT 1;
  IF FOUND THEN
   IF security.market_fact_visible(catalog,binding.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
   item=item||jsonb_build_object('definition_revision_id',binding.definition_revision_id,'mark_curve_code',binding.mark_curve_code,
    'mark_contract_id',binding.mark_contract_id,'mark_revision',binding.revision,'mark_publication_order',binding.publication_order);
  ELSE item=item||jsonb_build_object('mark_revision',0);END IF;
  positions=positions||jsonb_build_array(item);
 END LOOP;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,(result->>'authorityUntil')::timestamptz);
 RETURN result||jsonb_build_object('positions',positions);
END$$;

CREATE FUNCTION security.require_gas_owned_projection_scope(catalog uuid,context uuid,at_time timestamptz,books uuid[],configured boolean,until_at timestamptz)
 RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE present boolean;BEGIN
 PERFORM settlement_data.require_futures_margin_read(catalog,context,at_time,until_at);
 IF configured IS NULL OR books IS NULL OR cardinality(books) NOT BETWEEN 1 AND 128 OR EXISTS(SELECT 1 FROM unnest(books)b WHERE b IS NULL OR NOT EXISTS(
  SELECT 1 FROM trading.books k WHERE(k.catalog_id,k.id)=(catalog,b) AND security.trading_visible(k.catalog_id,k.publication_order,k.effective_at)))
 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 IF configured THEN PERFORM security.require_gas_owned(catalog,false,false);END IF;
 SELECT EXISTS(SELECT 1 FROM trading.gas_owned_positions p JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  WHERE a.catalog_id=catalog AND a.book_id=ANY(books)
   AND security.trading_visible(a.catalog_id,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')
   AND security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')) INTO present;
 IF present AND NOT configured THEN RAISE EXCEPTION 'gas_owned_projection_policy_required';END IF;
 PERFORM settlement_data.require_futures_margin_read(catalog,context,at_time,until_at);RETURN present;
END$$;

CREATE FUNCTION security.read_gas_owned_known_history(catalog uuid,context uuid,at_time timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;result jsonb;key text;items jsonb;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);
 PERFORM security.require_gas_owned_projection_scope(catalog,context,at_time,books,true,until_at);
 IF EXISTS(SELECT 1 FROM trading.gas_owned_accounts a WHERE a.catalog_id=catalog AND a.book_id=ANY(books)
  AND security.trading_visible(a.catalog_id,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00') AND(
   security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE OR security.market_fact_visible(catalog,a.definition_revision_id,NULL) IS NOT TRUE))
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 WITH accounts AS(SELECT a.* FROM trading.gas_owned_accounts a WHERE a.catalog_id=catalog AND a.book_id=ANY(books)
  AND security.trading_visible(a.catalog_id,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),
 positions AS(SELECT p.*,k.known_from known_at FROM trading.gas_owned_positions p JOIN accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),
 consumptions AS(SELECT c.*,k.known_from known_at FROM trading.gas_owned_consumptions c JOIN positions p ON(p.catalog_id,p.id)=(c.catalog_id,c.lot_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(c.catalog_id,c.publication_order)
  WHERE security.trading_visible(c.catalog_id,c.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),
 fees AS(SELECT f.*,k.known_from known_at FROM trading.gas_owned_fees f JOIN positions p ON(p.catalog_id,p.id)=(f.catalog_id,f.position_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(f.catalog_id,f.publication_order)
  WHERE security.trading_visible(f.catalog_id,f.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00'))
 SELECT jsonb_build_object('stateProfile','known_gas_owned_journal_projection_source_v1','contextId',context,'asOf',at_time,'knownAt',t.known_at,'effectiveOn',t.effective_on,'authorityUntil',until_at,
  'accounts',coalesce((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id) FROM accounts a),'[]'),
  'positions',coalesce((SELECT jsonb_agg(to_jsonb(p)||jsonb_build_object('quantity',trim_scale(p.quantity)::text) ORDER BY p.id) FROM positions p),'[]'),
  'lots',coalesce((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.id) FROM trading.gas_owned_lots l JOIN positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.id)),'[]'),
  'evidence',coalesce((SELECT jsonb_agg(to_jsonb(e)||jsonb_build_object('known_at',k.known_from,'quantity',trim_scale(e.quantity)::text) ORDER BY e.account_id,e.source_key,e.revision)
   FROM trading.gas_owned_evidence e JOIN accounts a ON(a.catalog_id,a.id)=(e.catalog_id,e.account_id) JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
   WHERE security.trading_visible(e.catalog_id,e.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),'[]'),
  'reservations',coalesce((SELECT jsonb_agg(to_jsonb(r)||jsonb_build_object('quantity',trim_scale(r.quantity)::text) ORDER BY r.id) FROM trading.gas_owned_reservations r JOIN positions p ON(p.catalog_id,p.id)=(r.catalog_id,r.lot_id)
   WHERE security.trading_visible(r.catalog_id,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),'[]'),
  'releases',coalesce((SELECT jsonb_agg(to_jsonb(x) ORDER BY x.reservation_id) FROM trading.gas_owned_releases x JOIN trading.gas_owned_reservations r ON(r.catalog_id,r.id)=(x.catalog_id,x.reservation_id)
   JOIN positions p ON(p.catalog_id,p.id)=(r.catalog_id,r.lot_id) WHERE security.trading_visible(x.catalog_id,x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),'[]'),
  'consumptions',coalesce((SELECT jsonb_agg(to_jsonb(c)||jsonb_build_object('quantity',trim_scale(c.quantity)::text) ORDER BY c.id) FROM consumptions c),'[]'),
  'loans',coalesce((SELECT jsonb_agg(to_jsonb(l)||jsonb_build_object('quantity',trim_scale(l.quantity)::text) ORDER BY l.id) FROM trading.gas_owned_loans l JOIN positions p ON(p.catalog_id,p.id)=(l.catalog_id,l.return_position_id)),'[]'),
  'repayments',coalesce((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM trading.gas_owned_repayments r JOIN consumptions c ON(c.catalog_id,c.id)=(r.catalog_id,r.consumption_id)),'[]'),
  'settlements',coalesce((SELECT jsonb_agg(to_jsonb(s)||jsonb_build_object('quantity',trim_scale(s.quantity)::text) ORDER BY s.id) FROM trading.gas_owned_claim_settlements s JOIN positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)
   WHERE security.trading_visible(s.catalog_id,s.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),'[]'),
  'cashoutTerms',coalesce((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.position_id) FROM trading.gas_owned_cashout_terms s JOIN positions p ON(p.catalog_id,p.id)=(s.catalog_id,s.position_id)),'[]'),
  'fees',coalesce((SELECT jsonb_agg(to_jsonb(f)||jsonb_build_object('amount',trim_scale(f.amount)::text) ORDER BY f.id) FROM fees f),'[]'),
  'payments',coalesce((SELECT jsonb_agg(to_jsonb(x)||jsonb_build_object('known_at',k.known_from,'amount',trim_scale(x.amount)::text) ORDER BY x.fee_id,x.business_key,x.revision)
   FROM trading.gas_owned_fee_payments x JOIN fees f ON(f.catalog_id,f.id)=(x.catalog_id,x.fee_id) JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(x.catalog_id,x.publication_order)
   WHERE security.trading_visible(x.catalog_id,x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),'[]'),
  'markBindings',coalesce((SELECT jsonb_agg(to_jsonb(b) ORDER BY b.position_id,b.revision) FROM trading.gas_owned_mark_bindings b JOIN positions p ON(p.catalog_id,p.id)=(b.catalog_id,b.position_id)
   WHERE security.trading_visible(b.catalog_id,b.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),'[]')) INTO result
 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 FOREACH key IN ARRAY ARRAY['accounts','reservations','releases','settlements','markBindings'] LOOP
  SELECT coalesce(jsonb_agg(value||jsonb_build_object('known_at',k.known_from) ORDER BY value::text),'[]') INTO items
   FROM jsonb_array_elements(result->key) JOIN trading.knowledge_receipts k
    ON(k.catalog_id,k.publication_order)=(catalog,(value->>'publication_order')::integer);
  result=result||jsonb_build_object(key,items);
 END LOOP;
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(result->'markBindings')b WHERE security.market_fact_visible(catalog,(b->>'definition_revision_id')::uuid,NULL) IS NOT TRUE)
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);RETURN result;
END$$;

CREATE FUNCTION security.read_gas_owned_projection_profile(catalog uuid,requested_profile uuid,requested_revision integer,context uuid,at_time timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;profile trading.gas_owned_projection_profiles;result jsonb;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);
 SELECT * INTO profile FROM trading.gas_owned_projection_profiles f WHERE(f.catalog_id,f.id,f.revision)=(catalog,requested_profile,requested_revision)
  AND security.trading_visible(f.catalog_id,f.publication_order,f.effective_at);
 IF profile.id IS NULL THEN RAISE EXCEPTION 'gas_owned_projection_policy';END IF;
 SELECT jsonb_build_object('profile',to_jsonb(profile),'authorityUntil',until_at,
  'performances',coalesce((SELECT jsonb_agg(to_jsonb(p)||jsonb_build_object('quantity',trim_scale(p.quantity)::text) ORDER BY p.position_id)
   FROM trading.gas_owned_projection_performances p WHERE(p.catalog_id,p.profile_id,p.profile_revision)=(catalog,requested_profile,requested_revision)),'[]'),
  'supply',coalesce((SELECT jsonb_agg(to_jsonb(s)||jsonb_build_object('quantity',trim_scale(s.quantity)::text) ORDER BY s.position_id,s.ordinal)
   FROM trading.gas_owned_projection_supplies s WHERE(s.catalog_id,s.profile_id,s.profile_revision)=(catalog,requested_profile,requested_revision)),'[]')) INTO result;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);RETURN result;
END$$;

CREATE FUNCTION security.read_gas_owned_projection_choices(catalog uuid,context uuid,at_time timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;result jsonb;BEGIN
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);
 PERFORM security.require_gas_owned_projection_scope(catalog,context,at_time,books,true,until_at);
 IF EXISTS(SELECT 1 FROM trading.gas_owned_accounts a WHERE a.catalog_id=catalog AND a.book_id=ANY(books)
  AND security.trading_visible(a.catalog_id,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00') AND(
   security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE OR security.market_fact_visible(catalog,a.definition_revision_id,NULL) IS NOT TRUE))
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 SELECT jsonb_build_object('contextId',context,'asOf',at_time,'effectiveOn',t.effective_on,'authorityUntil',until_at,'accounts',coalesce((
  SELECT jsonb_agg(jsonb_build_object('accountId',a.id,'code',a.code,'bookId',a.book_id,'book',b.name,'effectiveAt',a.effective_at,
   'profiles',coalesce((SELECT jsonb_agg(jsonb_build_object('profileId',p.id,'revision',p.revision,'implementation',p.implementation,
    'quantityDisposition',p.quantity_disposition,'effectiveAt',p.effective_at) ORDER BY p.id,p.revision)
    FROM trading.gas_owned_projection_profiles p WHERE(p.catalog_id,p.account_id)=(a.catalog_id,a.id)
     AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at)),'[]')) ORDER BY a.book_id,a.code)
   FROM trading.gas_owned_accounts a JOIN trading.books b ON(b.catalog_id,b.id)=(a.catalog_id,a.book_id)
   WHERE a.catalog_id=catalog AND a.book_id=ANY(books) AND security.trading_visible(a.catalog_id,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00')),'[]')) INTO result
 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,until_at);RETURN result;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.require_gas_owned_mark(uuid,uuid,uuid,text,uuid)'::regprocedure,
  'trading.guard_gas_owned_mark_binding()'::regprocedure,'trading.guard_gas_owned_projection_child()'::regprocedure,
  'trading.gas_owned_projection_keys(jsonb,text[],text[])'::regprocedure,'trading.gas_owned_projection_fact(jsonb)'::regprocedure,
  'security.configure_gas_owned_projection(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,boolean)'::regprocedure,
  'security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamptz)'::regprocedure,
  'security.read_gas_owned_projection_choices(uuid,uuid,timestamptz,uuid[])'::regprocedure,
  'security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])'::regprocedure,
  'security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)'::regprocedure,
  'security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
 FOREACH f IN ARRAY ARRAY['security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])'::regprocedure,
  'security.configure_gas_owned_projection(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,boolean)'::regprocedure,
  'security.read_gas_owned_projection_profile(uuid,uuid,integer,uuid,timestamptz)'::regprocedure,
  'security.read_gas_owned_projection_choices(uuid,uuid,timestamptz,uuid[])'::regprocedure,
  'security.require_gas_owned_projection_scope(uuid,uuid,timestamptz,uuid[],boolean,timestamptz)'::regprocedure,
  'security.read_gas_owned_known_history(uuid,uuid,timestamptz,uuid[])'::regprocedure] LOOP EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f);END LOOP;
END$$;
COMMIT;
