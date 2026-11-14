-- Closed actual commands over the typed physical/performance/principal stores.
-- A date does not establish title, dispatch, procurement or cash payment.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

DO $guard$DECLARE item record;f pg_proc;BEGIN
 FOR item IN SELECT * FROM(VALUES
 ('settlement_data.determine_futures_margin(uuid,jsonb,jsonb)','308b7182904cf08f1d5a19a69567052e',true,'jsonb'),
 ('settlement_data.determine_futures_margin_native(uuid,jsonb,jsonb,timestamptz)','35888a5d2f07997df8c99485d0dfbf1e',false,'jsonb'),
 ('settlement_data.finish_futures_margin_run(uuid,uuid,jsonb,timestamptz)','4103f62909fec08dfd1fa0faaf3697bc',true,'void'))v(signature,hash,runtime,result) LOOP
  SELECT * INTO STRICT f FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(f.prosrc)<>item.hash OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.prorettype<>item.result::regtype OR f.proretset
   OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
   OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') IS DISTINCT FROM item.runtime
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))x WHERE x.grantor<>f.proowner OR x.is_grantable
    OR x.privilege_type<>'EXECUTE' OR x.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
  THEN RAISE EXCEPTION 'futures_delivery_principal_upgrade: unexpected helper %',item.signature;END IF;
 END LOOP;
END$guard$;


CREATE FUNCTION trading.gas_futures_delivery_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=trading.gas_owned_fact(body);item jsonb;items jsonb='[]';key text;BEGIN
 FOREACH key IN ARRAY ARRAY['titleEvidenceId','cashId'] LOOP
  IF body->>key IS NOT NULL THEN result=result||jsonb_build_object(key,(body->>key)::uuid);END IF;
 END LOOP;
 IF body->>'performedAt' IS NOT NULL THEN result=result||jsonb_build_object('performedAt',to_char(trading.gas_owned_at(body->>'performedAt') AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));END IF;
 IF jsonb_typeof(body->'expectedVersion') IS DISTINCT FROM 'number' OR(body->>'expectedVersion')::numeric<0 OR(body->>'expectedVersion')::numeric>2147483647
  OR trunc((body->>'expectedVersion')::numeric)<>(body->>'expectedVersion')::numeric THEN RAISE EXCEPTION 'futures_delivery_input';END IF;
 result=result||jsonb_build_object('expectedVersion',(body->>'expectedVersion')::integer);
 IF body?'supply' THEN
  IF jsonb_typeof(body->'supply') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'supply')>256 THEN RAISE EXCEPTION 'futures_delivery_input';END IF;
  SELECT coalesce(jsonb_agg(value ORDER BY value::text),'[]') INTO items FROM(
   SELECT trading.gas_owned_fact(value) value FROM jsonb_array_elements(body->'supply'))s;
  result=result||jsonb_build_object('supply',items);
 END IF;RETURN result;
END$$;
CREATE FUNCTION trading.require_gas_futures_delivery_source(catalog uuid,holding uuid,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE link settlement_data.futures_delivery_obligations;p trading.gas_owned_positions;g trading.gas_futures_delivery_terms;BEGIN
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(catalog,holding);
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(catalog,holding);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(catalog,link.profile_id,link.profile_revision);
 PERFORM settlement_data.require_futures_delivery_head(catalog,holding,context);
 IF trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,context,as_of) IS NOT TRUE THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 IF EXISTS(SELECT 1 FROM(
  SELECT x.publication_order FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.position_id)=(catalog,holding)
  UNION ALL SELECT x.publication_order FROM trading.gas_futures_delivery_principal_runs x WHERE(x.catalog_id,x.position_id)=(catalog,holding)
  UNION ALL SELECT x.publication_order FROM trading.gas_futures_delivery_payments x JOIN trading.gas_futures_delivery_cash c ON(c.catalog_id,c.id)=(x.catalog_id,x.cash_id)
   WHERE(c.catalog_id,c.position_id)=(catalog,holding))h
  WHERE trading.gas_owned_visible(catalog,h.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE)
 THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 PERFORM trading.require_gas_owned_head(catalog,g.account_id,context,as_of);
END$$;

CREATE FUNCTION security.record_gas_futures_delivery(catalog uuid,operation uuid,holding uuid,action text,body jsonb,as_of timestamptz,context uuid,
 policy uuid,policy_revision integer,receipt_only boolean,continuation jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;link settlement_data.futures_delivery_obligations;p trading.gas_owned_positions;g trading.gas_futures_delivery_terms;
 profile settlement_data.futures_delivery_profiles;a trading.gas_owned_accounts;source_account trading.gas_owned_accounts;
 prior trading.gas_owned_commands;same_fact trading.gas_owned_commands;binding trading.gas_owned_action_bindings;canonical jsonb;outcome jsonb;
 performance trading.gas_futures_delivery_performances;cash trading.gas_futures_delivery_cash;payment trading.gas_futures_delivery_payments;
 evidence trading.gas_owned_evidence;stock trading.gas_owned_positions;final record;item jsonb;code text;publication integer;version integer;current_version integer;
 quantity numeric;amount_value numeric;total numeric=0;at_time timestamptz;available timestamptz;id uuid;stock_id uuid;consumption uuid;ordinal integer;BEGIN
 admission=security.gas_owned_admission(catalog,'futures_delivery_'||action,body,false);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR holding IS NULL OR holding='00000000-0000-0000-0000-000000000000'::uuid
  OR action IS NULL OR action NOT IN('performance','payment') OR context IS NULL OR context='00000000-0000-0000-0000-000000000000'::uuid
  OR policy IS NULL OR policy='00000000-0000-0000-0000-000000000000'::uuid OR policy_revision IS NULL OR policy_revision<1
  OR as_of IS NULL OR NOT isfinite(as_of) OR receipt_only IS NULL OR jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>131072
 THEN RAISE EXCEPTION 'futures_delivery_input';END IF;
 body=trading.gas_futures_delivery_fact(body);
 IF continuation='null'::jsonb THEN continuation=NULL;END IF;
 IF(action='payment' OR receipt_only) AND continuation IS NOT NULL THEN RAISE EXCEPTION 'futures_delivery_continuation';END IF;
 IF action='performance' THEN
  PERFORM trading.gas_owned_projection_keys(body,ARRAY['positionId','businessReference','expectedVersion','quantity','performedAt','titleEvidenceId','availableAt','supply'],ARRAY['titleEvidenceId','availableAt']);
 ELSE PERFORM trading.gas_owned_projection_keys(body,ARRAY['positionId','cashId','businessReference','expectedVersion','paidAt','amount','evidence'],ARRAY[]::text[]);END IF;
 IF(body->>'positionId')::uuid IS DISTINCT FROM holding OR body->>'businessReference' IS NULL OR length(body->>'businessReference') NOT BETWEEN 1 AND 128
  OR body->>'businessReference'<>btrim(body->>'businessReference') OR octet_length(body->>'businessReference')>512 THEN RAISE EXCEPTION 'futures_delivery_input';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(catalog,holding);
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(catalog,holding);
 SELECT * INTO STRICT profile FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.revision)=(catalog,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(catalog,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,g.account_id);
 PERFORM settlement_data.require_futures_delivery_reference(profile,g);
 canonical=jsonb_build_object('action','futures_delivery_'||action,'accountId',g.account_id,'body',body,'asOf',as_of,'contextId',context,'policyId',policy,'policyRevision',policy_revision,'automatic',false,'continuation',continuation);
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id IS DISTINCT FROM actor OR(CASE WHEN receipt_only THEN prior.request-'continuation' IS DISTINCT FROM canonical-'continuation' ELSE prior.request IS DISTINCT FROM canonical END) THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
  IF receipt_only THEN RETURN jsonb_build_object('outcome',prior.outcome,'continuation',prior.request->'continuation');END IF;RETURN prior.outcome;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO same_fact FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.account_id)=(catalog,g.account_id) AND c.request->>'action'='futures_delivery_'||action
  AND((c.request->'body')-'expectedVersion')=(body-'expectedVersion') ORDER BY(c.outcome->>'acknowledgedRevision')::integer LIMIT 1;
 IF action='performance' THEN SELECT coalesce(max(r.revision),0) INTO current_version FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,holding);
 ELSE
  SELECT * INTO STRICT cash FROM trading.gas_futures_delivery_cash c WHERE(c.catalog_id,c.id,c.position_id)=(catalog,(body->>'cashId')::uuid,holding);
  SELECT coalesce(max(x.revision),0) INTO current_version FROM trading.gas_futures_delivery_payments x WHERE(x.catalog_id,x.cash_id,x.business_reference)=(catalog,cash.id,body->>'businessReference');
 END IF;
 IF same_fact.request_id IS NOT NULL THEN
  outcome=same_fact.outcome||jsonb_build_object('operationId',operation,'changed',false,'semanticAcknowledgement',true,'currentRevision',current_version);
 ELSE
  IF receipt_only THEN PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);RETURN NULL;END IF;
  PERFORM trading.require_gas_futures_delivery_source(catalog,holding,context,as_of);
  code=CASE action WHEN 'performance' THEN 'gas.futures.delivery' ELSE 'gas.cash.record_payment' END;
  SELECT * INTO binding FROM trading.gas_owned_action_bindings b WHERE(b.catalog_id,b.account_id,b.action)=(catalog,g.account_id,code) ORDER BY b.revision DESC LIMIT 1;
  IF(binding.policy_id,binding.policy_revision) IS DISTINCT FROM(policy,policy_revision) THEN RAISE EXCEPTION 'futures_delivery_policy_changed';END IF;
  PERFORM lifecycle.require_policy(catalog,policy,policy_revision,code,as_of,false);
  IF current_version<>(body->>'expectedVersion')::integer THEN RAISE EXCEPTION 'futures_delivery_revision';END IF;
  PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,a.definition_revision_id);
  -- Admit every supplied account before inserting any piece of this operation.
  -- Later pieces must not mistake an earlier same-publication consumption for
  -- a financial head missing from the immutable opening source certificate.
  IF action='performance' AND p.quantity<0 THEN
   FOR item IN SELECT value FROM jsonb_array_elements(body->'supply') LOOP
    PERFORM trading.gas_owned_projection_keys(item,ARRAY['lotId','evidenceId','reservationId','quantity'],ARRAY['reservationId']);
    SELECT * INTO STRICT stock FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id,x.kind)=(catalog,(item->>'lotId')::uuid,'owned_stock');
    SELECT * INTO STRICT source_account FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,stock.account_id);
    PERFORM trading.require_gas_owned_head(catalog,stock.account_id,context,as_of);
    PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,source_account.definition_revision_id);
   END LOOP;
  END IF;
  SELECT coalesce(max(x.publication_order),0)+1 INTO publication FROM trading.publications x WHERE x.catalog_id=catalog;
  id=operation;INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  IF action='performance' THEN
   quantity=trading.gas_owned_quantity(body->>'quantity');at_time=trading.gas_owned_at(body->>'performedAt');
   IF quantity<=0 OR at_time>as_of OR at_time<g.delivery_from OR at_time>=g.delivery_to OR quantity+coalesce((SELECT sum(x.quantity)
    FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.position_id)=(catalog,holding)),0)>abs(p.quantity)
   THEN RAISE EXCEPTION 'futures_delivery_performance';END IF;
   SELECT * INTO STRICT final FROM settlement_data.read_futures_delivery_final(catalog,holding,context,as_of);
   IF final.final_at>at_time THEN RAISE EXCEPTION 'futures_delivery_performance';END IF;
   IF EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.position_id,x.business_reference)=(catalog,holding,body->>'businessReference'))
   THEN RAISE EXCEPTION 'futures_delivery_reconciliation_required';END IF;
   stock_id=NULL;
   IF p.quantity>0 THEN
    IF body->>'titleEvidenceId' IS NULL OR body->>'availableAt' IS NULL OR jsonb_array_length(body->'supply')<>0 THEN RAISE EXCEPTION 'futures_delivery_title';END IF;
    available=trading.gas_owned_at(body->>'availableAt');
    SELECT * INTO STRICT evidence FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.id,e.account_id)=(catalog,(body->>'titleEvidenceId')::uuid,g.account_id);
    IF evidence.kind<>'title_receipt' OR evidence.quantity<=0 OR evidence.occurred_at<>at_time OR available<at_time
     OR trading.gas_owned_visible(catalog,evidence.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
     OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence e WHERE(e.catalog_id,e.account_id,e.source_key)=(catalog,evidence.account_id,evidence.source_key) AND e.revision>evidence.revision)
    THEN RAISE EXCEPTION 'futures_delivery_evidence';END IF;
    stock_id=gen_random_uuid();
    INSERT INTO trading.gas_owned_positions VALUES(catalog,stock_id,g.account_id,'owned_stock',quantity,evidence.id,'physical_futures_delivery_v1',a.definition_revision_id,a.mark_curve_code,a.mark_contract_id,
     NULL,'nominal_mark_v1','owned_stock_v1',actor,publication,at_time);
    INSERT INTO trading.gas_owned_lots VALUES(catalog,stock_id,g.account_id,at_time,available);
   ELSE
    IF body->>'titleEvidenceId' IS NOT NULL OR body->>'availableAt' IS NOT NULL OR jsonb_array_length(body->'supply')=0 THEN RAISE EXCEPTION 'futures_delivery_supply';END IF;
   END IF;
   INSERT INTO trading.gas_futures_delivery_performances VALUES(catalog,id,holding,body->>'businessReference',quantity,at_time,(body->>'titleEvidenceId')::uuid,stock_id,actor,publication);
   IF p.quantity<0 THEN
    ordinal=0;
    FOR item IN SELECT value FROM jsonb_array_elements(body->'supply') LOOP
     PERFORM trading.gas_owned_projection_keys(item,ARRAY['lotId','evidenceId','reservationId','quantity'],ARRAY['reservationId']);
     SELECT * INTO STRICT stock FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id,x.kind)=(catalog,(item->>'lotId')::uuid,'owned_stock');
     SELECT * INTO STRICT source_account FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,stock.account_id);
     SELECT * INTO STRICT evidence FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(item->>'evidenceId')::uuid,stock.account_id);
     amount_value=trading.gas_owned_quantity(item->>'quantity');
     IF amount_value<=0 OR evidence.kind<>'physical_delivery' OR evidence.occurred_at<>at_time OR evidence.quantity=0
      OR(source_account.book_id,source_account.legal_entity_id,source_account.legal_entity_revision,source_account.reference_catalog_id,source_account.reference_revision_id,source_account.quantity_code,source_account.currency_code,source_account.location_code)
       IS DISTINCT FROM(a.book_id,a.legal_entity_id,a.legal_entity_revision,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.currency_code,a.location_code)
      OR trading.gas_owned_visible(catalog,stock.publication_order,stock.effective_at,context,as_of) IS NOT TRUE
      OR trading.gas_owned_visible(catalog,evidence.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
      OR EXISTS(SELECT 1 FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.account_id,x.source_key)=(catalog,evidence.account_id,evidence.source_key) AND x.revision>evidence.revision)
     THEN RAISE EXCEPTION 'futures_delivery_supply';END IF;
     total=total+amount_value;consumption=gen_random_uuid();ordinal=ordinal+1;
     INSERT INTO trading.gas_owned_consumptions VALUES(catalog,consumption,stock.id,'delivery',amount_value,evidence.id,(item->>'reservationId')::uuid,actor,publication,at_time);
     INSERT INTO trading.gas_futures_delivery_splits VALUES(catalog,id,ordinal,consumption);
    END LOOP;
    IF total<>quantity THEN RAISE EXCEPTION 'futures_delivery_supply';END IF;
   END IF;
   version=trading.apply_futures_delivery_principal(catalog,holding,final.determination_id,context,as_of,actor,publication,continuation);
   outcome=jsonb_build_object('stockId',stock_id,'performedQuantity',trim_scale(quantity)::text,'principalVersion',version);
  ELSE
   amount_value=trading.gas_owned_quantity(body->>'amount');at_time=trading.gas_owned_at(body->>'paidAt');
   IF at_time<cash.effective_at THEN RAISE EXCEPTION 'futures_delivery_payment_origin';END IF;
   IF at_time>as_of OR amount_value=0 OR sign(amount_value)<>sign(cash.amount) OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 2000 THEN RAISE EXCEPTION 'futures_delivery_payment';END IF;
   SELECT * INTO payment FROM trading.gas_futures_delivery_payments x WHERE(x.catalog_id,x.cash_id,x.business_reference)=(catalog,cash.id,body->>'businessReference') ORDER BY x.revision DESC LIMIT 1;
   version=coalesce(payment.revision,0)+1;
   INSERT INTO trading.gas_futures_delivery_payments VALUES(catalog,id,cash.id,body->>'businessReference',version,at_time,amount_value,body->>'evidence',actor,publication);
   outcome=jsonb_build_object('cashId',cash.id,'amount',trim_scale(amount_value)::text,'paidAt',at_time);
  END IF;
  outcome=coalesce(outcome,'{}')||jsonb_build_object('operationId',operation,'positionId',holding,'id',id,'action','futures_delivery_'||action,
   'revision',version,'acknowledgedRevision',version,'currentRevision',version,'changed',true,'semanticAcknowledgement',false);
 END IF;
 INSERT INTO trading.gas_owned_commands VALUES(catalog,actor,operation,g.account_id,canonical,outcome,(admission->>'until')::timestamptz,false,false);
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,false,false);
 IF receipt_only THEN RETURN jsonb_build_object('outcome',outcome,'continuation',continuation);END IF;RETURN outcome;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.gas_futures_delivery_fact(jsonb)'::regprocedure,
  'trading.require_gas_futures_delivery_source(uuid,uuid,uuid,timestamptz)'::regprocedure,
  'security.record_gas_futures_delivery(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
 GRANT EXECUTE ON FUNCTION security.record_gas_futures_delivery(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer,boolean,jsonb) TO openetrm_runtime;
END$$;
-- Complete financial closure is bound to the existing margin command identity.
-- This is its first-outcome receipt, not another financial journal.
CREATE TABLE settlement_data.futures_delivery_command_receipts(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,continuation jsonb,outcome jsonb NOT NULL,authority_until timestamptz NOT NULL CHECK(isfinite(authority_until)),
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,operation_id) REFERENCES settlement_data.futures_margin_run_commands(catalog_id,operation_id),
 CHECK(continuation IS NULL OR pg_column_size(continuation)<=8388608),CHECK(pg_column_size(outcome)<=8388608)
);
ALTER TABLE settlement_data.futures_delivery_command_receipts OWNER TO openetrm_security_owner;
ALTER TABLE settlement_data.futures_delivery_command_receipts ENABLE ROW LEVEL SECURITY;
ALTER TABLE settlement_data.futures_delivery_command_receipts FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON settlement_data.futures_delivery_command_receipts TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON settlement_data.futures_delivery_command_receipts FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

CREATE FUNCTION settlement_data.is_retained_cash_margin_command(catalog uuid,operation uuid) RETURNS boolean
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM settlement_data.futures_margin_run_commands cmd
  JOIN settlement_data.futures_margin_adoptions a ON(a.catalog_id,a.trade_id,a.binding_id)=(cmd.catalog_id,cmd.trade_id,cmd.binding_id)
  JOIN settlement_data.futures_margin_policies p ON(p.catalog_id,p.id,p.revision)=(a.catalog_id,a.policy_id,a.policy_revision)
  JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(p.catalog_id,p.definition_revision_id,p.contract_id)
  JOIN ref_data.futures_series fs ON(fs.catalog_id,fs.revision_id,fs.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)
  JOIN ref_data.futures_products fp ON(fp.catalog_id,fp.revision_id,fp.code)=(fs.catalog_id,fs.revision_id,fs.product_code)
  WHERE(cmd.catalog_id,cmd.operation_id)=(catalog,operation) AND fp.settlement_method='cash' AND p.delivery_profile_id IS NULL);
$$;
ALTER FUNCTION settlement_data.is_retained_cash_margin_command(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.is_retained_cash_margin_command(uuid,uuid) FROM PUBLIC,openetrm_runtime;

CREATE FUNCTION settlement_data.finish_futures_delivery_command(catalog uuid,operation uuid,body jsonb,continuation jsonb,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE receipt settlement_data.futures_delivery_command_receipts;BEGIN
 PERFORM settlement_data.finish_futures_margin_run(catalog,operation,body,until_at);
 SELECT * INTO receipt FROM settlement_data.futures_delivery_command_receipts x WHERE(x.catalog_id,x.operation_id)=(catalog,operation);
 IF receipt.operation_id IS NULL THEN
  IF continuation IS NULL AND settlement_data.is_retained_cash_margin_command(catalog,operation) THEN RETURN;END IF;
  RAISE EXCEPTION 'futures_delivery_command_conflict';
 END IF;
 IF receipt.continuation IS DISTINCT FROM continuation THEN RAISE EXCEPTION 'futures_delivery_command_conflict';END IF;
 PERFORM settlement_data.require_futures_margin_delivery_commit(catalog,(body->>'tradeId')::uuid,(body->>'bindingId')::uuid,until_at);
END$$;
CREATE FUNCTION settlement_data.futures_delivery_run_status(catalog uuid,operation uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;prior jsonb;receipt settlement_data.futures_delivery_command_receipts;BEGIN
 until_at=settlement_data.futures_margin_delivery_authority(catalog,(body->>'tradeId')::uuid,(body->>'bindingId')::uuid);
 -- The existing status function takes the exact same catalog/operation lock,
 -- including a negative lookup, and checks actor, body and retained references.
 prior=settlement_data.futures_margin_run_status(catalog,operation,body);
 IF prior IS NULL THEN
  PERFORM settlement_data.require_futures_margin_delivery_commit(catalog,(body->>'tradeId')::uuid,(body->>'bindingId')::uuid,until_at);RETURN NULL;
 END IF;
 SELECT * INTO receipt FROM settlement_data.futures_delivery_command_receipts x WHERE(x.catalog_id,x.operation_id)=(catalog,operation);
 IF receipt.operation_id IS NULL THEN
  IF settlement_data.is_retained_cash_margin_command(catalog,operation) THEN
   PERFORM settlement_data.finish_futures_delivery_command(catalog,operation,body,NULL,until_at);RETURN jsonb_build_object('outcome',prior,'continuation',NULL);
  END IF;
  RAISE EXCEPTION 'futures_delivery_command_incomplete';
 END IF;
 PERFORM settlement_data.finish_futures_delivery_command(catalog,operation,body,receipt.continuation,until_at);
 RETURN jsonb_build_object('outcome',receipt.outcome,'continuation',receipt.continuation);
END$$;
CREATE FUNCTION settlement_data.check_futures_delivery_command() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE body jsonb;BEGIN
 SELECT c.request INTO STRICT body FROM settlement_data.futures_margin_run_commands c WHERE(c.catalog_id,c.operation_id)=(NEW.catalog_id,NEW.operation_id);
 PERFORM settlement_data.finish_futures_delivery_command(NEW.catalog_id,NEW.operation_id,body,NEW.continuation,NEW.authority_until);RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER original_command_authority AFTER INSERT ON settlement_data.futures_delivery_command_receipts DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_delivery_command();

CREATE FUNCTION settlement_data.determine_futures_margin(catalog uuid,body jsonb,steps jsonb,continuation jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;operation uuid;prior jsonb;result jsonb;trade uuid;binding uuid;link settlement_data.futures_delivery_obligations;
 final settlement_data.futures_margin_determinations;run settlement_data.futures_margin_runs;principal integer=0;current_principal integer=0;basis_price numeric;basis_multiplier numeric;BEGIN
 trade=(body->>'tradeId')::uuid;binding=(body->>'bindingId')::uuid;operation=(body->>'operationId')::uuid;
 until_at=settlement_data.futures_margin_delivery_authority(catalog,trade,binding);
 prior=settlement_data.futures_delivery_run_status(catalog,operation,body);
 IF prior IS NOT NULL THEN
  IF prior->'continuation' IS DISTINCT FROM coalesce(continuation,'null'::jsonb) THEN RAISE EXCEPTION 'futures_delivery_command_conflict';END IF;
  PERFORM settlement_data.finish_futures_delivery_command(catalog,operation,body,continuation,until_at);RETURN prior->'outcome';
 END IF;
 IF continuation='null'::jsonb THEN continuation=NULL;END IF;
 IF continuation IS NOT NULL THEN PERFORM trading.require_futures_delivery_continuation(continuation);END IF;
 result=settlement_data.determine_futures_margin_native(catalog,body,steps,until_at);
 SELECT * INTO link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.trade_id,x.binding_id)=(catalog,trade,binding);
 IF link.position_id IS NOT NULL THEN
  SELECT coalesce(max(r.revision),0) INTO current_principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,link.position_id);
  SELECT d.* INTO final FROM settlement_data.futures_margin_determinations d JOIN trading.publications p ON(p.catalog_id,p.publication_order)=(d.catalog_id,d.publication_order)
   WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=(catalog,trade,binding,link.final_occurrence_id)
   AND(d.run_revision,d.publication_order)=((result->>'version')::integer,(result->>'publicationOrder')::integer) AND p.publication_xid=pg_current_xact_id();
  IF final.id IS NOT NULL THEN
   IF continuation IS NULL THEN RAISE EXCEPTION 'futures_delivery_continuation_required';END IF;
   SELECT * INTO STRICT run FROM settlement_data.futures_margin_runs r WHERE(r.catalog_id,r.trade_id,r.binding_id,r.revision)=(catalog,trade,binding,final.run_revision);
   principal=trading.apply_futures_delivery_principal(catalog,link.position_id,final.id,run.source_context_id,run.as_of,run.actor_id,run.publication_order,continuation);
   current_principal=principal;
  ELSIF continuation IS NULL THEN
   IF EXISTS(SELECT 1 FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=
    (catalog,trade,binding,link.final_occurrence_id) AND d.run_revision<=(result->>'version')::integer) THEN RAISE EXCEPTION 'futures_delivery_continuation_required';END IF;
  ELSE
   -- A semantic acknowledgement may identify an older composite fact. It can
   -- never recompute today's principal or rewind the current accepted basis.
   SELECT d.* INTO final FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=
    (catalog,trade,binding,link.final_occurrence_id) AND d.run_revision<=(result->>'version')::integer ORDER BY d.run_revision DESC LIMIT 1;
   IF final.id IS NULL OR(continuation->>'sourceObservationId')::uuid IS DISTINCT FROM final.source_observation_id
    OR(continuation->>'finalOccurrenceId')::uuid IS DISTINCT FROM link.final_occurrence_id
    OR(continuation->>'positionId')::uuid IS DISTINCT FROM link.position_id AND NOT(continuation->>'positionId' IS NULL AND jsonb_array_length(continuation->'steps')=0) THEN RAISE EXCEPTION 'futures_delivery_acknowledgement';END IF;
   SELECT o.value,p.quote_multiplier INTO STRICT basis_price,basis_multiplier FROM market_data.observations o
    JOIN settlement_data.futures_delivery_profiles p ON(p.catalog_id,p.id,p.revision)=(catalog,link.profile_id,link.profile_revision)
    WHERE(o.catalog_id,o.id)=(catalog,final.source_observation_id);
   SELECT r.revision INTO principal FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id,r.final_determination_id)=(catalog,link.position_id,final.id)
    AND r.performance_count=jsonb_array_length(continuation->'steps')
    AND ARRAY(SELECT(s.value->>'performanceId')::uuid FROM jsonb_array_elements(continuation->'steps') WITH ORDINALITY s(value,n) ORDER BY s.n)=
     ARRAY(SELECT p.id FROM trading.gas_futures_delivery_principal_determinations d JOIN trading.gas_futures_delivery_performances p
      ON(p.catalog_id,p.id)=(d.catalog_id,d.performance_id) WHERE(d.catalog_id,d.position_id,d.run_revision)=(catalog,link.position_id,r.revision)
      ORDER BY p.performed_at,convert_to(p.business_reference,'UTF8'))
    AND NOT EXISTS(
     SELECT 1 FROM jsonb_array_elements(continuation->'steps')s LEFT JOIN trading.gas_futures_delivery_principal_determinations d
     ON(d.catalog_id,d.position_id,d.run_revision,d.performance_id)=(catalog,link.position_id,r.revision,(s->>'performanceId')::uuid)
     WHERE d.id IS NULL OR(d.cumulative_quantity,d.rounded_total,d.contribution) IS DISTINCT FROM
      ((s->>'cumulativeQuantity')::numeric,(s->>'roundedTotal')::numeric,(s->>'contribution')::numeric)
      OR trading.gas_delivery_exact_equal(s->>'exactTotal',-basis_price*basis_multiplier*d.cumulative_quantity) IS NOT TRUE
      OR(((s->>'previousDeterminationId')::uuid IS NOT DISTINCT FROM d.previous_id AND(s->>'adjustment')::numeric=d.adjustment)
       OR((s->>'previousDeterminationId')::uuid=d.id AND(s->>'adjustment')::numeric=0)) IS NOT TRUE) ORDER BY r.revision LIMIT 1;
   IF principal IS NULL THEN
    IF jsonb_array_length(continuation->'steps')=0 AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances p
     WHERE(p.catalog_id,p.position_id)=(catalog,link.position_id) AND p.publication_order<=final.publication_order) THEN principal=0;
    ELSE RAISE EXCEPTION 'futures_delivery_acknowledgement';END IF;
   END IF;
  END IF;
  result=result||jsonb_build_object('deliveryPositionId',link.position_id,'acknowledgedPrincipalVersion',principal,'currentPrincipalVersion',current_principal);
 ELSIF continuation IS NOT NULL THEN RAISE EXCEPTION 'futures_delivery_continuation';END IF;
 INSERT INTO settlement_data.futures_delivery_command_receipts VALUES(catalog,operation,continuation,result,until_at);
 PERFORM settlement_data.finish_futures_delivery_command(catalog,operation,body,continuation,until_at);RETURN result;
END$$;

-- Preserve the original public signature and OID for cash margin and physical
-- daily margin. A new physical final requires its explicit reviewed closure.
CREATE OR REPLACE FUNCTION settlement_data.determine_futures_margin(catalog uuid,body jsonb,steps jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN RETURN settlement_data.determine_futures_margin(catalog,body,steps,NULL);END$$;

CREATE FUNCTION settlement_data.check_futures_delivery_receipt_closure() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM settlement_data.futures_delivery_obligations l WHERE(l.catalog_id,l.trade_id,l.binding_id,l.final_occurrence_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id,NEW.occurrence_id))
 AND NOT EXISTS(SELECT 1 FROM settlement_data.futures_delivery_command_receipts r JOIN settlement_data.futures_margin_run_commands c
 ON(c.catalog_id,c.operation_id)=(r.catalog_id,r.operation_id)
 WHERE(c.catalog_id,c.trade_id,c.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id) AND(c.outcome->>'publicationOrder')::integer=NEW.publication_order
 AND(r.continuation->>'sourceObservationId')::uuid=NEW.source_observation_id AND(r.continuation->>'finalOccurrenceId')::uuid=NEW.occurrence_id)
 THEN RAISE EXCEPTION 'futures_delivery_command_incomplete';END IF;RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER complete_physical_command AFTER INSERT ON settlement_data.futures_margin_determinations DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_delivery_receipt_closure();

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['settlement_data.finish_futures_delivery_command(uuid,uuid,jsonb,jsonb,timestamptz)'::regprocedure,
  'settlement_data.futures_delivery_run_status(uuid,uuid,jsonb)'::regprocedure,'settlement_data.check_futures_delivery_command()'::regprocedure,
  'settlement_data.determine_futures_margin(uuid,jsonb,jsonb,jsonb)'::regprocedure,'settlement_data.check_futures_delivery_receipt_closure()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION settlement_data.finish_futures_delivery_command(uuid,uuid,jsonb,jsonb,timestamptz),
 settlement_data.futures_delivery_run_status(uuid,uuid,jsonb),settlement_data.determine_futures_margin(uuid,jsonb,jsonb,jsonb) TO openetrm_runtime;

CREATE FUNCTION security.gas_futures_delivery_discovery_authority(catalog uuid) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE market_until timestamptz;trades_until timestamptz;gas_until timestamptz;until_at timestamptz;BEGIN
 market_until=security.authority_until('ReadMarketData');trades_until=security.authority_until('ReadTrades');gas_until=security.authority_until('ReadGasOwnedHoldings');
 IF market_until IS NULL OR trades_until IS NULL OR gas_until IS NULL THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 until_at=least(market_until,trades_until,gas_until);PERFORM security.require_gas_owned_preview(catalog,until_at);
 until_at=least(until_at,settlement_data.futures_margin_preview_authority(catalog));
 PERFORM security.require_gas_owned_preview(catalog,until_at);RETURN until_at;
END$$;
ALTER FUNCTION security.gas_futures_delivery_discovery_authority(uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_futures_delivery_discovery_authority(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.gas_futures_delivery_discovery_authority(uuid) TO openetrm_runtime;

CREATE FUNCTION security.read_gas_futures_delivery_actions(catalog uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE state jsonb;held jsonb;versions jsonb;until_at timestamptz;BEGIN
 state=security.read_gas_owned_operations(catalog,context,as_of,NULL);
 held=security.read_gas_owned_holdings(catalog,context,as_of,ARRAY[]::uuid[]);
 until_at=least((state->>'authorityUntil')::timestamptz,(held->>'authorityUntil')::timestamptz);
 SELECT coalesce(jsonb_agg(to_jsonb(r)),'[]') INTO versions FROM(
  SELECT DISTINCT ON(r.position_id)r.position_id,r.revision FROM trading.gas_futures_delivery_principal_runs r
  WHERE r.catalog_id=catalog AND security.trading_visible(r.catalog_id,r.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00') ORDER BY r.position_id,r.revision DESC)r;
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 RETURN state||jsonb_build_object('deliveryRemainders',coalesce((SELECT jsonb_agg(p) FROM jsonb_array_elements(held->'positions')p WHERE p->>'kind'='delivery_obligation'),'[]'),
  'deliveryCash',held->'deliveryCash','deliveryPayments',held->'deliveryPayments','principalVersions',versions,'authorityUntil',until_at);
END$$;
ALTER FUNCTION security.read_gas_futures_delivery_actions(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_futures_delivery_actions(uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_futures_delivery_actions(uuid,uuid,timestamptz) TO openetrm_runtime;

-- The shared lifecycle receipt must acknowledge the same complete native first
-- outcome and exact reviewed request, including its principal continuation.
DO $shared$DECLARE item record;f pg_proc;definition text;before text;after text;BEGIN
 FOR item IN SELECT * FROM(VALUES
 ('lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)','48cbc8038cab6c5d5203bb97cd18c57d','jsonb'),
 ('lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)','4fe744c3a9419c2a4b890e25e28d3395','void'))v(signature,hash,result) LOOP
  SELECT * INTO STRICT f FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(f.prosrc)<>item.hash OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.prorettype<>item.result::regtype OR f.proretset
   OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
   OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[] OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE')
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))x WHERE x.grantor<>f.proowner OR x.is_grantable
    OR x.privilege_type<>'EXECUTE' OR x.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
  THEN RAISE EXCEPTION 'futures_delivery_shared_upgrade: unexpected helper %',item.signature;END IF;
 END LOOP;
 definition=pg_get_functiondef('lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)'::regprocedure);
 before=$old$  SELECT c.outcome INTO native FROM settlement_data.futures_margin_run_commands c WHERE(c.catalog_id,c.operation_id,c.actor_id,c.trade_id,c.binding_id)=
   (catalog,operation,actor,(body#>>'{target,scopeId}')::uuid,(body#>>'{target,bindingId}')::uuid);$old$;
 after=$new$  SELECT coalesce(r.outcome,c.outcome),c.request INTO native,native_request FROM settlement_data.futures_margin_run_commands c
   LEFT JOIN settlement_data.futures_delivery_command_receipts r ON(r.catalog_id,r.operation_id)=(c.catalog_id,c.operation_id)
   WHERE(c.catalog_id,c.operation_id,c.actor_id,c.trade_id,c.binding_id)=
    (catalog,operation,actor,(body#>>'{target,scopeId}')::uuid,(body#>>'{target,bindingId}')::uuid);
  IF EXISTS(SELECT 1 FROM jsonb_object_keys(body->'selection')k WHERE k NOT IN('expectedRevision','expectedMarginRevision','throughOccurrence','correctionDue'))
   OR native_request IS DISTINCT FROM jsonb_build_object('operationId',body->'operationId','tradeId',body#>'{target,scopeId}','bindingId',body#>'{target,bindingId}',
    'expectedRevision',body#>'{selection,expectedRevision}','expectedMarginRevision',body#>'{selection,expectedMarginRevision}',
    'contextId',body#>'{context,contextId}','asOf',body#>'{context,asOf}','throughOccurrence',body#>'{selection,throughOccurrence}',
    'implementation','native_futures_margin_journal_v1','correctionDue',body#>'{selection,correctionDue}','previewHash',body->'previewHash')
  THEN RAISE EXCEPTION 'lifecycle_native_receipt';END IF;$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'futures_delivery_shared_upgrade';END IF;
 EXECUTE replace(definition,before,after);
 definition=pg_get_functiondef('lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)'::regprocedure);
 before=$old$PERFORM settlement_data.finish_futures_margin_run(catalog,operation,(SELECT r.request FROM settlement_data.futures_margin_run_commands r WHERE(r.catalog_id,r.operation_id)=(catalog,operation)),until_at);$old$;
 after=$new$PERFORM settlement_data.finish_futures_delivery_command(catalog,operation,
   (SELECT r.request FROM settlement_data.futures_margin_run_commands r WHERE(r.catalog_id,r.operation_id)=(catalog,operation)),
   (SELECT r.continuation FROM settlement_data.futures_delivery_command_receipts r WHERE(r.catalog_id,r.operation_id)=(catalog,operation)),until_at);$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'futures_delivery_shared_upgrade';END IF;
 EXECUTE replace(definition,before,after);
END$shared$;

COMMIT;
