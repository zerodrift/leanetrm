-- Consumers: exact gas delivery terms selected by a futures margin policy and
-- its ordinary post-final obligation. Configuration establishes no title.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

DO $upgrade$DECLARE item record;f pg_proc;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('settlement_data.save_futures_margin_policy(uuid,jsonb)','7ce8ef6064505d95689c0146fa0dc5ea','jsonb',true),
  ('settlement_data.require_futures_margin_reference(settlement_data.futures_margin_policies)','206490aafc6c3b95774f7e3ce3a85e43','void',false),
  ('settlement_data.read_futures_margin_origin(uuid,uuid,uuid,uuid,timestamptz)','fe11ee14a62bbc93b0c6c513a4073d42','jsonb',false),
  ('settlement_data.read_futures_known_linked_history(uuid,uuid,uuid,uuid,timestamptz,uuid)','72680f91af5fa729fc22c57ae6a7a709','jsonb',true),
  ('lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)','87bc99ee3ffe168c3e447381b61475b1','jsonb',true)
 )v(signature,body_hash,result_type,runtime_execute) LOOP
  SELECT * INTO STRICT f FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(f.prosrc)<>item.body_hash OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.proretset
  OR f.prorettype<>item.result_type::regtype OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
  OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
  OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') IS DISTINCT FROM item.runtime_execute
  OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))x WHERE x.grantor<>f.proowner OR x.is_grantable
   OR x.privilege_type<>'EXECUTE' OR x.grantee NOT IN(f.proowner,CASE WHEN item.runtime_execute THEN 'openetrm_runtime'::regrole ELSE f.proowner END))
  THEN RAISE EXCEPTION 'futures_delivery_policy_upgrade: unexpected helper %',item.signature;END IF;
 END LOOP;
END$upgrade$;

CREATE TABLE settlement_data.futures_delivery_profiles(
 catalog_id uuid NOT NULL,id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),terms_id uuid NOT NULL,
 name text NOT NULL CHECK(length(btrim(name)) BETWEEN 1 AND 128),implementation text NOT NULL CHECK(implementation='gas_future_physical_delivery_v1'),
 definition_revision_id uuid NOT NULL,contract_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 series_code ref_data.code NOT NULL,quantity_code ref_data.code NOT NULL,currency_code ref_data.code NOT NULL,location_code ref_data.code NOT NULL,
 quote_multiplier ref_data.exact_number NOT NULL CHECK(quote_multiplier>0),terms_hash text NOT NULL CHECK(terms_hash~'^[0-9a-f]{64}$'),
 source_context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id,revision),UNIQUE(catalog_id,terms_id),UNIQUE(catalog_id,id,revision,publication_order),UNIQUE(catalog_id,id,terms_hash),
 UNIQUE(catalog_id,id,revision,definition_revision_id,contract_id,reference_catalog_id,reference_revision_id,currency_code),
 FOREIGN KEY(catalog_id,definition_revision_id,contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,series_code) REFERENCES ref_data.futures_series(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(id<>'00000000-0000-0000-0000-000000000000'::uuid AND terms_id<>'00000000-0000-0000-0000-000000000000'::uuid)
);
CREATE TABLE trading.gas_futures_delivery_terms(
 catalog_id uuid NOT NULL,profile_id uuid NOT NULL,profile_revision integer NOT NULL,publication_order integer NOT NULL,account_id uuid NOT NULL,
 delivery_from ref_data.instant NOT NULL,delivery_to ref_data.instant NOT NULL,cash_due_at ref_data.instant NOT NULL,
 carrying text NOT NULL CHECK(carrying IN('nominal_mark_v1','currency_curve_v1')),
 principal_increment ref_data.exact_number NOT NULL CHECK(principal_increment>0),principal_tie text NOT NULL CHECK(principal_tie IN('to_even','away_from_zero')),
 principal_scope text NOT NULL CHECK(principal_scope='delivery_cumulative_principal_total_v1'),
 correction_due_policy text NOT NULL CHECK(correction_due_policy='contractual_principal_due_v1'),
 action_policy_id uuid NOT NULL,action_policy_revision integer NOT NULL,
 PRIMARY KEY(catalog_id,profile_id,profile_revision),
 FOREIGN KEY(catalog_id,profile_id,profile_revision,publication_order) REFERENCES settlement_data.futures_delivery_profiles(catalog_id,id,revision,publication_order),
 FOREIGN KEY(catalog_id,account_id) REFERENCES trading.gas_owned_accounts(catalog_id,id),
 FOREIGN KEY(catalog_id,action_policy_id,action_policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 CHECK(delivery_from<delivery_to AND cash_due_at>=delivery_from)
);
DO $$DECLARE qualified text;BEGIN
 FOREACH qualified IN ARRAY ARRAY['settlement_data.futures_delivery_profiles','trading.gas_futures_delivery_terms'] LOOP
  EXECUTE 'ALTER TABLE '||qualified||' OWNER TO openetrm_security_owner';
  EXECUTE 'ALTER TABLE '||qualified||' ENABLE ROW LEVEL SECURITY';
  EXECUTE 'ALTER TABLE '||qualified||' FORCE ROW LEVEL SECURITY';
  EXECUTE 'CREATE POLICY owner_all ON '||qualified||' TO openetrm_security_owner USING(true) WITH CHECK(true)';
  EXECUTE 'CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON '||qualified||' FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()';
  EXECUTE 'CREATE TRIGGER native_publication BEFORE INSERT ON '||qualified||' FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication()';
 END LOOP;
END$$;

CREATE FUNCTION settlement_data.futures_delivery_profile_value(p settlement_data.futures_delivery_profiles,g trading.gas_futures_delivery_terms) RETURNS jsonb
 LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('name',p.name,'implementation',p.implementation,'definition',p.definition_revision_id,'contract',p.contract_id,
  'references',p.reference_catalog_id,'referenceRevision',p.reference_revision_id,'series',p.series_code,'quantity',p.quantity_code,'currency',p.currency_code,
  'location',p.location_code,'multiplier',trim_scale(p.quote_multiplier),'account',g.account_id,
  'from',to_char(g.delivery_from AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'to',to_char(g.delivery_to AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'due',to_char(g.cash_due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'carrying',g.carrying,'increment',trim_scale(g.principal_increment),'tie',g.principal_tie,'scope',g.principal_scope,
  'correctionDue',g.correction_due_policy,'actionPolicy',g.action_policy_id,'actionPolicyRevision',g.action_policy_revision)
$$;
CREATE FUNCTION settlement_data.check_futures_delivery_profile() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_delivery_profiles;g trading.gas_futures_delivery_terms;c market_data.contracts;
 a trading.gas_owned_accounts;product ref_data.futures_products;q ref_data.quote_specifications;quantity ref_data.quantity_specifications;mark_contract market_data.contracts;BEGIN
 SELECT * INTO STRICT p FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.revision)=(NEW.catalog_id,NEW.id,NEW.revision);
 SELECT * INTO g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(p.catalog_id,p.id,p.revision);
 IF g.profile_id IS NULL OR encode(sha256(convert_to(settlement_data.futures_delivery_profile_value(p,g)::text,'UTF8')),'hex')<>p.terms_hash
 THEN RAISE EXCEPTION 'futures_delivery_seal';END IF;
 SELECT * INTO STRICT c FROM market_data.contracts x WHERE(x.catalog_id,x.revision_id,x.id)=(p.catalog_id,p.definition_revision_id,p.contract_id);
 SELECT x.* INTO STRICT product FROM ref_data.futures_series s JOIN ref_data.futures_products x
  ON(x.catalog_id,x.revision_id,x.code)=(s.catalog_id,s.revision_id,s.product_code)
  WHERE(s.catalog_id,s.revision_id,s.code)=(p.reference_catalog_id,p.reference_revision_id,p.series_code);
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(g.catalog_id,g.account_id);
 SELECT quote.* INTO STRICT q FROM market_data.curves curve JOIN ref_data.quote_specifications quote
  ON(quote.catalog_id,quote.revision_id,quote.code)=(curve.reference_catalog_id,curve.reference_revision_id,curve.quote_code)
  WHERE(curve.catalog_id,curve.revision_id,curve.code)=(a.catalog_id,a.definition_revision_id,a.mark_curve_code);
 SELECT * INTO STRICT quantity FROM ref_data.quantity_specifications x WHERE(x.catalog_id,x.revision_id,x.code)=(p.reference_catalog_id,p.reference_revision_id,p.quantity_code);
 SELECT * INTO STRICT mark_contract FROM market_data.contracts x WHERE(x.catalog_id,x.revision_id,x.id)=(a.catalog_id,a.definition_revision_id,a.mark_contract_id);
 IF c.kind<>'future' OR c.futures_series_code IS DISTINCT FROM p.series_code OR product.settlement_method IS DISTINCT FROM 'physical'
  OR(c.reference_catalog_id,c.reference_revision_id) IS DISTINCT FROM(p.reference_catalog_id,p.reference_revision_id)
  OR(product.quote_currency_code,product.quote_unit_code,product.location_code,product.quote_multiplier,product.commodity_code) IS DISTINCT FROM
   (p.currency_code,quantity.unit_code,p.location_code,p.quote_multiplier,quantity.commodity_code)
  OR(a.reference_catalog_id,a.reference_revision_id,a.currency_code,a.quantity_code,a.location_code,q.multiplier) IS DISTINCT FROM
   (p.reference_catalog_id,p.reference_revision_id,p.currency_code,p.quantity_code,p.location_code,p.quote_multiplier)
  OR a.kind<>'owned_inventory' OR g.delivery_from<c.expiry_at OR mark_contract.expiry_at<=g.delivery_to
  OR product.lot_basis<>'fixed_total' OR product.lot_unit_code<>product.quote_unit_code
  OR NOT EXISTS(SELECT 1 FROM lifecycle.action_policies x WHERE(x.catalog_id,x.id,x.revision,x.action)=(g.catalog_id,g.action_policy_id,g.action_policy_revision,'gas.futures.delivery'))
 THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER complete_delivery_profile AFTER INSERT ON settlement_data.futures_delivery_profiles DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_delivery_profile();
CREATE FUNCTION settlement_data.guard_futures_delivery_child() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM settlement_data.futures_delivery_profiles p JOIN trading.publications x
  ON(x.catalog_id,x.publication_order)=(p.catalog_id,p.publication_order)
  WHERE(p.catalog_id,p.id,p.revision,p.publication_order)=(NEW.catalog_id,NEW.profile_id,NEW.profile_revision,NEW.publication_order)
  AND x.publication_xid=pg_current_xact_id()) THEN RAISE EXCEPTION 'futures_delivery_sealed';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER sealed_delivery_profile BEFORE INSERT ON trading.gas_futures_delivery_terms FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_futures_delivery_child();

-- This is an existing gas-owned configuration command, not another event log.
DO $admission$DECLARE f pg_proc;definition text;before text=$old$action IN('account','bind_policy','mark_binding','projection_profile')$old$;BEGIN
 SELECT * INTO STRICT f FROM pg_proc WHERE oid='security.gas_owned_admission(uuid,text,jsonb,boolean)'::regprocedure;
 IF md5(f.prosrc)<>'1b0b4bdfd8a02ec7986e99f8bc3423ec' OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef
 OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR f.prorettype<>'jsonb'::regtype OR f.prokind<>'f' OR f.proretset
 OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v' OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql'
 OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE')
 OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable
  OR a.privilege_type<>'EXECUTE' OR a.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
 THEN RAISE EXCEPTION 'futures_delivery_admission_upgrade: unexpected helper security.gas_owned_admission(uuid,text,jsonb,boolean)';END IF;
 definition=pg_get_functiondef(f.oid);
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'futures_delivery_admission_upgrade: unexpected helper security.gas_owned_admission(uuid,text,jsonb,boolean)';END IF;
 EXECUTE replace(definition,before,$new$action IN('account','bind_policy','mark_binding','projection_profile','futures_delivery_profile')$new$);
END$admission$;
CREATE FUNCTION settlement_data.require_futures_delivery_reference(p settlement_data.futures_delivery_profiles,g trading.gas_futures_delivery_terms) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE a trading.gas_owned_accounts;BEGIN
 PERFORM security.require_gas_owned(p.catalog_id,false,false);
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(p.catalog_id,g.account_id);
 IF security.reference_version_visible(p.reference_catalog_id,p.reference_revision_id) IS NOT TRUE
 OR security.market_fact_visible(p.catalog_id,p.definition_revision_id,NULL) IS NOT TRUE
 OR security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE
 OR security.market_fact_visible(a.catalog_id,a.definition_revision_id,NULL) IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.legal_entities e WHERE(e.catalog_id,e.id,e.revision)=(a.catalog_id,a.legal_entity_id,a.legal_entity_revision)
  AND e.effective_at<=a.effective_at AND(security.current_write_context() OR security.trading_visible(e.catalog_id,e.publication_order,e.effective_at)))
 THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION settlement_data.save_futures_delivery_profile(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;until_at timestamptz;input jsonb;operation uuid;context uuid;as_of timestamptz;actor uuid;expected integer;head integer;
 p settlement_data.futures_delivery_profiles;g trading.gas_futures_delivery_terms;fact settlement_data.futures_delivery_profiles;
 a trading.gas_owned_accounts;c market_data.contracts;product ref_data.futures_products;prior jsonb;canonical jsonb;outcome jsonb;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>16384 THEN RAISE EXCEPTION 'futures_delivery_input';END IF;
 PERFORM trading.gas_owned_keys(body,ARRAY['operationId','expectedRevision','contextId','asOf','profile']);
 input=body->'profile';
 PERFORM trading.gas_owned_keys(input,ARRAY['id','name','implementation','definitionRevisionId','contractId','accountId','fromAt','toAt','cashDueAt','carrying',
  'principalIncrement','principalTie','principalScope','correctionDuePolicy','actionPolicyId','actionPolicyRevision']);
 IF jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^(0|[1-9][0-9]{0,8})$'
 OR jsonb_typeof(input->'actionPolicyRevision') IS DISTINCT FROM 'number' OR input->>'actionPolicyRevision'!~'^[1-9][0-9]{0,8}$'
 THEN RAISE EXCEPTION 'futures_delivery_input';END IF;
 operation=(body->>'operationId')::uuid;context=(body->>'contextId')::uuid;as_of=trading.gas_owned_at(body->>'asOf');expected=(body->>'expectedRevision')::integer;
 g.account_id=(input->>'accountId')::uuid;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR context IS NULL OR g.account_id IS NULL
 THEN RAISE EXCEPTION 'futures_delivery_input';END IF;
 admission=security.gas_owned_admission(catalog,'futures_delivery_profile',body,false);until_at=(admission->>'until')::timestamptz;
 prior=security.gas_owned_command_receipt(catalog,operation,'futures_delivery_profile',g.account_id,body,as_of,context,NULL,NULL,false);
 IF prior IS NOT NULL THEN
  SELECT x.* INTO STRICT p FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.revision)=(catalog,(prior->>'profileId')::uuid,(prior->>'revision')::integer);
  SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(catalog,p.id,p.revision);
  PERFORM settlement_data.require_futures_delivery_reference(p,g);PERFORM security.require_gas_owned_commit(catalog,until_at,true,false);RETURN prior;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.require_gas_owned_commit(catalog,until_at,true,false);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,g.account_id);
 p.catalog_id=catalog;p.id=(input->>'id')::uuid;p.revision=expected+1;p.terms_id=gen_random_uuid();p.name=btrim(input->>'name');p.implementation=input->>'implementation';
 p.definition_revision_id=(input->>'definitionRevisionId')::uuid;p.contract_id=(input->>'contractId')::uuid;p.source_context_id=context;p.actor_id=actor;
 SELECT * INTO STRICT c FROM market_data.contracts x WHERE(x.catalog_id,x.revision_id,x.id)=(catalog,p.definition_revision_id,p.contract_id);
 SELECT x.* INTO STRICT product FROM ref_data.futures_series s JOIN ref_data.futures_products x
  ON(x.catalog_id,x.revision_id,x.code)=(s.catalog_id,s.revision_id,s.product_code)
  WHERE(s.catalog_id,s.revision_id,s.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code);
 p.reference_catalog_id=c.reference_catalog_id;p.reference_revision_id=c.reference_revision_id;p.series_code=c.futures_series_code;
 p.quantity_code=a.quantity_code;p.currency_code=product.quote_currency_code;p.location_code=product.location_code;p.quote_multiplier=product.quote_multiplier;
 g.catalog_id=catalog;g.profile_id=p.id;g.profile_revision=p.revision;g.delivery_from=trading.gas_owned_at(input->>'fromAt');g.delivery_to=trading.gas_owned_at(input->>'toAt');
 g.cash_due_at=trading.gas_owned_at(input->>'cashDueAt');g.carrying=input->>'carrying';g.principal_increment=trading.gas_owned_quantity(input->>'principalIncrement');
 g.principal_tie=input->>'principalTie';g.principal_scope=input->>'principalScope';g.correction_due_policy=input->>'correctionDuePolicy';
 g.action_policy_id=(input->>'actionPolicyId')::uuid;g.action_policy_revision=(input->>'actionPolicyRevision')::integer;
 PERFORM settlement_data.require_futures_delivery_reference(p,g);
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,p.definition_revision_id);
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,a.definition_revision_id);
 IF trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of) IS NOT TRUE OR g.delivery_from<c.expiry_at
 OR NOT EXISTS(SELECT 1 FROM lifecycle.action_policies x JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(catalog,context)
  JOIN trading.contexts tc ON(tc.catalog_id,tc.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(x.catalog_id,x.publication_order)
  WHERE(x.catalog_id,x.id,x.revision,x.action)=(catalog,g.action_policy_id,g.action_policy_revision,'gas.futures.delivery')
   AND x.mode IN('manual','review') AND x.publication_order<=tc.known_through AND k.known_from<=t.known_at)
 THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;
 p.terms_hash=encode(sha256(convert_to(settlement_data.futures_delivery_profile_value(p,g)::text,'UTF8')),'hex');
 SELECT coalesce(max(x.revision),0) INTO head FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id)=(catalog,p.id);
 SELECT * INTO fact FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.terms_hash)=(catalog,p.id,p.terms_hash);
 IF fact.id IS NULL THEN
  IF head<>expected THEN RAISE EXCEPTION 'futures_delivery_revision';END IF;
  IF head>0 AND NOT EXISTS(SELECT 1 FROM settlement_data.futures_delivery_profiles x JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(catalog,context)
   JOIN trading.contexts tc ON(tc.catalog_id,tc.context_id)=(t.catalog_id,t.id)
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(x.catalog_id,x.publication_order)
   WHERE(x.catalog_id,x.id,x.revision)=(catalog,p.id,head) AND x.publication_order<=tc.known_through AND k.known_from<=t.known_at)
  THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
  SELECT coalesce(max(x.publication_order),0)+1 INTO p.publication_order FROM trading.publications x WHERE x.catalog_id=catalog;
  g.publication_order=p.publication_order;INSERT INTO trading.publications VALUES(catalog,p.publication_order,pg_current_xact_id());
  INSERT INTO settlement_data.futures_delivery_profiles SELECT(p).*;INSERT INTO trading.gas_futures_delivery_terms SELECT(g).*;fact=p;head=p.revision;
 END IF;
 outcome=jsonb_build_object('operationId',operation,'profileId',fact.id,'revision',fact.revision,'acknowledgedRevision',fact.revision,'currentRevision',head,
  'changed',coalesce(fact.publication_order=p.publication_order,false),'semanticAcknowledgement',fact.publication_order IS DISTINCT FROM p.publication_order,'publicationOrder',fact.publication_order);
 canonical=jsonb_build_object('action','futures_delivery_profile','accountId',g.account_id,'body',trading.gas_owned_fact(body),'asOf',as_of,'contextId',context,'policyId',NULL,'policyRevision',NULL,'automatic',false);
 INSERT INTO trading.gas_owned_commands(catalog_id,actor_id,request_id,account_id,request,outcome,authority_until,configuring,requires_jobs)
  VALUES(catalog,actor,operation,g.account_id,canonical,outcome,until_at,true,false);
 PERFORM settlement_data.require_futures_delivery_reference(p,g);PERFORM security.require_gas_owned_commit(catalog,until_at,true,false);RETURN outcome;
END$$;

DO $$DECLARE signature regprocedure;BEGIN
 FOREACH signature IN ARRAY ARRAY['settlement_data.futures_delivery_profile_value(settlement_data.futures_delivery_profiles,trading.gas_futures_delivery_terms)'::regprocedure,
 'settlement_data.check_futures_delivery_profile()'::regprocedure,'settlement_data.guard_futures_delivery_child()'::regprocedure,
 'settlement_data.require_futures_delivery_reference(settlement_data.futures_delivery_profiles,trading.gas_futures_delivery_terms)'::regprocedure,
 'settlement_data.save_futures_delivery_profile(uuid,jsonb)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION settlement_data.save_futures_delivery_profile(uuid,jsonb) TO openetrm_runtime;

-- Version one remains byte-for-byte the cash-only financial hash contract.
-- Version two explicitly pins an exact delivery profile, or explicit null for
-- a cash-settled contract; a profile is never inferred from commodity/location.
ALTER TABLE settlement_data.futures_margin_policies DROP CONSTRAINT futures_margin_policies_implementation_check;
ALTER TABLE settlement_data.futures_margin_policies ADD CHECK(implementation IN('dated_futures_margin_policy_v1','dated_futures_margin_policy_v2')),
 ADD COLUMN delivery_profile_id uuid,ADD COLUMN delivery_profile_revision integer,
 ADD CHECK((delivery_profile_id IS NULL)=(delivery_profile_revision IS NULL)),
 ADD CHECK(implementation<>'dated_futures_margin_policy_v1' OR delivery_profile_id IS NULL),
 ADD FOREIGN KEY(catalog_id,delivery_profile_id,delivery_profile_revision,definition_revision_id,contract_id,reference_catalog_id,reference_revision_id,currency_code)
  REFERENCES settlement_data.futures_delivery_profiles(catalog_id,id,revision,definition_revision_id,contract_id,reference_catalog_id,reference_revision_id,currency_code);
DO $copy_value$DECLARE f pg_proc;definition text;BEGIN
 SELECT * INTO STRICT f FROM pg_proc WHERE oid='settlement_data.futures_margin_policy_value(settlement_data.futures_margin_policies,jsonb)'::regprocedure;
 IF md5(f.prosrc)<>'77e22ba1b760ba800dc2417c8eb86a44' OR f.proowner<>'openetrm_security_owner'::regrole OR f.prosecdef OR f.provolatile<>'i'
 OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR f.prorettype<>'jsonb'::regtype OR f.prokind<>'f' OR f.proretset
 OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'sql'
 OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE')
 OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable
  OR a.privilege_type<>'EXECUTE' OR a.grantee<>f.proowner) THEN RAISE EXCEPTION 'futures_delivery_hash_upgrade: unexpected helper settlement_data.futures_margin_policy_value(settlement_data.futures_margin_policies,jsonb)';END IF;
 definition=replace(pg_get_functiondef(f.oid),'CREATE OR REPLACE FUNCTION settlement_data.futures_margin_policy_value(',
  'CREATE FUNCTION settlement_data.futures_margin_policy_v1_value(');EXECUTE definition;
 ALTER FUNCTION settlement_data.futures_margin_policy_v1_value(settlement_data.futures_margin_policies,jsonb) OWNER TO openetrm_security_owner;
 REVOKE ALL ON FUNCTION settlement_data.futures_margin_policy_v1_value(settlement_data.futures_margin_policies,jsonb) FROM PUBLIC;
END$copy_value$;
CREATE OR REPLACE FUNCTION settlement_data.futures_margin_policy_value(p settlement_data.futures_margin_policies,items jsonb) RETURNS jsonb
 LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT settlement_data.futures_margin_policy_v1_value(p,items)||CASE WHEN p.implementation='dated_futures_margin_policy_v2'
  THEN jsonb_build_object('delivery',CASE WHEN p.delivery_profile_id IS NULL THEN 'null'::jsonb
   ELSE jsonb_build_object('id',p.delivery_profile_id,'revision',p.delivery_profile_revision) END) ELSE '{}'::jsonb END
$$;
CREATE FUNCTION settlement_data.require_futures_margin_delivery(p settlement_data.futures_margin_policies) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d settlement_data.futures_delivery_profiles;g trading.gas_futures_delivery_terms;product ref_data.futures_products;q ref_data.quote_specifications;BEGIN
 SELECT x.* INTO STRICT product FROM market_data.contracts c JOIN ref_data.futures_series s
  ON(s.catalog_id,s.revision_id,s.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)
  JOIN ref_data.futures_products x ON(x.catalog_id,x.revision_id,x.code)=(s.catalog_id,s.revision_id,s.product_code)
  WHERE(c.catalog_id,c.revision_id,c.id)=(p.catalog_id,p.definition_revision_id,p.contract_id);
 IF p.implementation='dated_futures_margin_policy_v2' AND(product.settlement_method='physical')<>(p.delivery_profile_id IS NOT NULL)
 THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;
 IF p.delivery_profile_id IS NULL THEN RETURN;END IF;
 SELECT * INTO STRICT d FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.revision)=(p.catalog_id,p.delivery_profile_id,p.delivery_profile_revision);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(d.catalog_id,d.id,d.revision);
 SELECT * INTO STRICT q FROM ref_data.quote_specifications x WHERE(x.catalog_id,x.revision_id,x.code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code);
 IF(d.definition_revision_id,d.contract_id,d.reference_catalog_id,d.reference_revision_id,d.quantity_code,d.currency_code,d.quote_multiplier) IS DISTINCT FROM
  (p.definition_revision_id,p.contract_id,p.reference_catalog_id,p.reference_revision_id,q.quantity_code,p.currency_code,q.multiplier)
 OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
  WHERE(t.catalog_id,t.id)=(p.catalog_id,p.source_context_id) AND d.publication_order<=x.known_through AND k.known_from<=t.known_at)
 THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;
 PERFORM settlement_data.require_futures_delivery_reference(d,g);
END$$;
CREATE FUNCTION settlement_data.guard_futures_margin_delivery() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN PERFORM settlement_data.require_futures_margin_delivery(NEW);RETURN NEW;END$$;
CREATE TRIGGER exact_delivery BEFORE INSERT ON settlement_data.futures_margin_policies FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_futures_margin_delivery();
CREATE FUNCTION settlement_data.check_futures_margin_delivery_window() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.delivery_profile_id IS NOT NULL AND EXISTS(SELECT 1 FROM settlement_data.futures_margin_policy_occurrences o
  JOIN trading.gas_futures_delivery_terms g ON(g.catalog_id,g.profile_id,g.profile_revision)=(NEW.catalog_id,NEW.delivery_profile_id,NEW.delivery_profile_revision)
  WHERE(o.catalog_id,o.policy_id,o.policy_revision,o.phase)=(NEW.catalog_id,NEW.id,NEW.revision,'final') AND o.at>g.delivery_from)
 THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER complete_delivery_window AFTER INSERT ON settlement_data.futures_margin_policies DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_margin_delivery_window();
CREATE FUNCTION settlement_data.guard_futures_delivery_adoption() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_margin_policies;a trading.gas_owned_accounts;r trading.trade_revisions;BEGIN
 SELECT * INTO STRICT p FROM settlement_data.futures_margin_policies x WHERE(x.catalog_id,x.id,x.revision)=(NEW.catalog_id,NEW.policy_id,NEW.policy_revision);
 IF p.delivery_profile_id IS NULL THEN RETURN NEW;END IF;
 SELECT account.* INTO STRICT a FROM trading.gas_futures_delivery_terms g JOIN trading.gas_owned_accounts account ON(account.catalog_id,account.id)=(g.catalog_id,g.account_id)
  WHERE(g.catalog_id,g.profile_id,g.profile_revision)=(p.catalog_id,p.delivery_profile_id,p.delivery_profile_revision);
 SELECT * INTO STRICT r FROM trading.trade_revisions x WHERE(x.catalog_id,x.trade_id,x.revision)=(NEW.catalog_id,NEW.trade_id,NEW.origin_revision);
 IF(a.book_id,a.legal_entity_id,a.legal_entity_revision) IS DISTINCT FROM(r.book_id,r.legal_entity_id,r.legal_entity_revision)
 THEN RAISE EXCEPTION 'futures_delivery_owner';END IF;
 PERFORM settlement_data.require_futures_margin_delivery(p);RETURN NEW;
END$$;
CREATE TRIGGER exact_delivery_owner BEFORE INSERT ON settlement_data.futures_margin_adoptions FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_futures_delivery_adoption();

CREATE FUNCTION settlement_data.futures_delivery_profile_document(catalog uuid,profile uuid,requested_revision integer,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_delivery_profiles;g trading.gas_futures_delivery_terms;a trading.gas_owned_accounts;prefix integer;cut timestamptz;BEGIN
 PERFORM security.require_gas_owned(catalog,false,false);
 SELECT x.known_through,t.known_at INTO STRICT prefix,cut FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND security.trading_context_day_matches(catalog,context,as_of);
 SELECT x.* INTO p FROM settlement_data.futures_delivery_profiles x JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(x.catalog_id,x.publication_order)
  WHERE(x.catalog_id,x.id,x.revision)=(catalog,profile,requested_revision) AND x.publication_order<=prefix AND k.known_from<=cut;
 IF p.id IS NULL THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(catalog,p.id,p.revision);
 SELECT x.* INTO a FROM trading.gas_owned_accounts x JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(x.catalog_id,x.publication_order)
  WHERE(x.catalog_id,x.id)=(catalog,g.account_id) AND x.publication_order<=prefix AND k.known_from<=cut;
 IF a.id IS NULL OR encode(sha256(convert_to(settlement_data.futures_delivery_profile_value(p,g)::text,'UTF8')),'hex')<>p.terms_hash THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 PERFORM settlement_data.require_futures_delivery_reference(p,g);
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,p.definition_revision_id);
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,a.definition_revision_id);
 RETURN jsonb_build_object('profile',to_jsonb(p),'gas',to_jsonb(g),'account',to_jsonb(a));
END$$;
CREATE FUNCTION settlement_data.futures_margin_policy_document(p settlement_data.futures_margin_policies,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT to_jsonb(p)||CASE WHEN p.implementation='dated_futures_margin_policy_v2' THEN jsonb_build_object('delivery',CASE WHEN p.delivery_profile_id IS NULL
  THEN 'null'::jsonb ELSE settlement_data.futures_delivery_profile_document(p.catalog_id,p.delivery_profile_id,p.delivery_profile_revision,context,as_of) END) ELSE '{}'::jsonb END
$$;

-- Extend only the exact parsing/serialization sites. Everything else in the
-- previously qualified producers, receipts and current-authority fences stays.
DO $extend$DECLARE f regprocedure;definition text;before text;after text;BEGIN
 f='settlement_data.save_futures_margin_policy(uuid,jsonb)'::regprocedure;definition=pg_get_functiondef(f);
 before=$old$'opening','increment','tie','stage','roundingScope','implementation','occurrences'))$old$;
 after=$new$'opening','increment','tie','stage','roundingScope','implementation','occurrences','delivery'))$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'futures_delivery_policy_upgrade';END IF;definition=replace(definition,before,after);
 before=$old$p.source_context_id=context;p.actor_id=actor;$old$;
 after=$new$p.source_context_id=context;p.actor_id=actor;
 IF p.implementation='dated_futures_margin_policy_v2' THEN
  IF NOT(input?'delivery') THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;
  IF input->'delivery'<>'null'::jsonb THEN
   PERFORM trading.gas_owned_keys(input->'delivery',ARRAY['id','revision']);
   IF jsonb_typeof(input#>'{delivery,revision}') IS DISTINCT FROM 'number' OR input#>>'{delivery,revision}'!~'^[1-9][0-9]{0,8}$' THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;
   p.delivery_profile_id=(input#>>'{delivery,id}')::uuid;p.delivery_profile_revision=(input#>>'{delivery,revision}')::integer;
  END IF;
 ELSIF input?'delivery' THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'futures_delivery_policy_upgrade';END IF;definition=replace(definition,before,after);
 EXECUTE definition;
 f='settlement_data.require_futures_margin_reference(settlement_data.futures_margin_policies)'::regprocedure;definition=pg_get_functiondef(f);
 before=$old$BEGIN$old$;after=$new$BEGIN
 IF p.delivery_profile_id IS NOT NULL THEN PERFORM settlement_data.require_futures_margin_delivery(p);END IF;$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'futures_delivery_policy_upgrade';END IF;EXECUTE replace(definition,before,after);
 FOREACH f IN ARRAY ARRAY['settlement_data.read_futures_margin_origin(uuid,uuid,uuid,uuid,timestamptz)'::regprocedure,
  'settlement_data.read_futures_known_linked_history(uuid,uuid,uuid,uuid,timestamptz,uuid)'::regprocedure,
  'lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date)'::regprocedure] LOOP
  definition=pg_get_functiondef(f);before=$old$'policy',to_jsonb(p)$old$;
  after=$new$'policy',settlement_data.futures_margin_policy_document(p,context,as_of)$new$;
  IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'futures_delivery_reader_upgrade';END IF;
  EXECUTE replace(definition,before,after);
 END LOOP;
END$extend$;
DO $$DECLARE signature regprocedure;BEGIN
 FOREACH signature IN ARRAY ARRAY['settlement_data.futures_margin_policy_value(settlement_data.futures_margin_policies,jsonb)'::regprocedure,
 'settlement_data.require_futures_margin_delivery(settlement_data.futures_margin_policies)'::regprocedure,'settlement_data.guard_futures_margin_delivery()'::regprocedure,
 'settlement_data.check_futures_margin_delivery_window()'::regprocedure,'settlement_data.guard_futures_delivery_adoption()'::regprocedure,
 'settlement_data.futures_delivery_profile_document(uuid,uuid,integer,uuid,timestamptz)'::regprocedure,
 'settlement_data.futures_margin_policy_document(settlement_data.futures_margin_policies,uuid,timestamptz)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
 END LOOP;
END$$;
COMMIT;
