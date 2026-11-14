-- Consumer: reviewed same-trade gross/net settlement and exact reversals.
-- Cash, allocations and financial sequence remain in the existing swap store.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
INSERT INTO lifecycle.action_kinds(code,automatic,configuration_permission,execution_permission)
 VALUES('swap.net',false,'AmendTrade','AmendTrade');
CREATE TABLE lifecycle.swap_net_profiles(
 catalog_id uuid NOT NULL,id uuid NOT NULL CHECK(id<>'00000000-0000-0000-0000-000000000000'::uuid),revision integer NOT NULL CHECK(revision>0),
 trade_id uuid NOT NULL,origin_revision integer NOT NULL,policy_id uuid NOT NULL,policy_revision integer NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 decimal_places integer NOT NULL CHECK(decimal_places BETWEEN 0 AND 12),rounding_policy text NOT NULL CHECK(rounding_policy='currency_minor_units_to_even_v1'),
 due_policy text NOT NULL CHECK(due_policy='swap_due_nominal_v1'),recognition_policy text NOT NULL CHECK(recognition_policy='swap_completed_period_trading_pnl_v1'),
 correction_policy text NOT NULL CHECK(correction_policy='swap_explicit_correction_due_v1'),net_policy text NOT NULL CHECK(net_policy='explicit_same_due_swap_net_v1'),
 publication_order integer NOT NULL,PRIMARY KEY(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,trade_id,origin_revision) REFERENCES trading.swap_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order));
ALTER TABLE lifecycle.swap_net_profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE lifecycle.swap_net_profiles FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON lifecycle.swap_net_profiles TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY scoped_read ON lifecycle.swap_net_profiles FOR SELECT TO openetrm_runtime USING(
 security.trading_visible(catalog_id,publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00') AND
 security.reference_access(reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON lifecycle.swap_net_profiles FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
GRANT SELECT,INSERT ON lifecycle.swap_net_profiles TO openetrm_security_owner;
GRANT SELECT ON lifecycle.swap_net_profiles TO openetrm_runtime;

-- Keep the identities used by deferred guards and later callers. Private copies
-- are admitted only when the complete previously qualified contract matches.
DO $upgrade$DECLARE item record;f pg_proc;definition text;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)','d761d03b9c629509216470487a99c7b4','void','finish_command'),
  ('lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz)','440102c3ed3a443c84213add96d031e9','jsonb','record_native_action')
 )v(signature,body_hash,result_type,name) LOOP
  SELECT * INTO STRICT f FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(f.prosrc)<>item.body_hash OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.proretset
   OR f.prorettype<>item.result_type::regtype OR f.provariadic<>0 OR f.proargmodes IS NOT NULL OR f.pronargdefaults<>0
   OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
   OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
   OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE')
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable
    OR a.privilege_type<>'EXECUTE' OR a.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
  THEN RAISE EXCEPTION 'swap_net_lifecycle_upgrade: unexpected helper %',item.signature;END IF;
  definition=pg_get_functiondef(f.oid);
  definition=replace(definition,'CREATE OR REPLACE FUNCTION lifecycle.'||item.name||'(', 'CREATE FUNCTION lifecycle.'||item.name||'_pre_net(');
  -- PL/pgSQL's implicit outer label follows its own function name.
  definition=replace(definition,item.name||'.',item.name||'_pre_net.');EXECUTE definition;
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',replace(item.signature,item.name||'(',item.name||'_pre_net('));
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime',replace(item.signature,item.name||'(',item.name||'_pre_net('));
 END LOOP;
END$upgrade$;
CREATE FUNCTION lifecycle.require_swap_net_reference(catalog uuid,trade uuid,profile uuid,revision integer) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p lifecycle.swap_net_profiles;r trading.swap_trade_revisions;BEGIN
 SELECT * INTO p FROM lifecycle.swap_net_profiles t WHERE(t.catalog_id,t.trade_id,t.id,t.revision)=(catalog,trade,profile,require_swap_net_reference.revision);
 SELECT * INTO r FROM trading.swap_trade_revisions t WHERE(t.catalog_id,t.trade_id,t.revision)=(catalog,trade,p.origin_revision);
 IF p.id IS NULL OR r.trade_id IS NULL OR security.reference_access(p.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 OR security.reference_version_visible(p.reference_catalog_id,p.reference_revision_id) IS NOT TRUE
 OR NOT EXISTS(SELECT 1 FROM ref_data.currencies c WHERE(c.catalog_id,c.revision_id,c.code,c.display_minor_units)=
 (p.reference_catalog_id,p.reference_revision_id,p.currency_code,p.decimal_places))
 THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 PERFORM settlement_data.require_swap_reference(catalog,trade);
 PERFORM security.require_swap_metadata_references(catalog,r.instrument_id);
 IF NOT EXISTS(SELECT 1 FROM trading.swap_instruments i CROSS JOIN LATERAL(
  SELECT s.reference_catalog_id,s.reference_revision_id,s.quote_code FROM trading.swap_fixed_sides s WHERE(s.catalog_id,s.id)=(i.catalog_id,i.fixed_side_id)
  UNION ALL SELECT s.reference_catalog_id,s.reference_revision_id,s.quote_code FROM trading.swap_floating_sides s WHERE s.catalog_id=i.catalog_id AND s.id IN(i.first_floating_side_id,i.floating_side_id)
  UNION ALL SELECT s.reference_catalog_id,s.reference_revision_id,s.quote_code FROM trading.swap_side_periods s WHERE s.catalog_id=i.catalog_id AND s.side_id IN(i.scheduled_first_id,i.scheduled_second_id))s
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
  WHERE(i.catalog_id,i.instrument_id)=(catalog,r.instrument_id) AND(q.catalog_id,q.revision_id,q.currency_code)=(p.reference_catalog_id,p.reference_revision_id,p.currency_code)
  HAVING count(*)>=2) THEN RAISE EXCEPTION 'lifecycle_net_currency';END IF;
END$$;
CREATE FUNCTION lifecycle.swap_net_profile_document(catalog uuid,trade uuid,profile uuid,revision integer,context uuid,as_of timestamptz,business_date date) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p lifecycle.swap_net_profiles;r trading.swap_trade_revisions;until_at timestamptz;prefix integer;cut timestamptz;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT x.known_through,t.known_at INTO prefix,cut FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,business_date);
 SELECT t.* INTO p FROM lifecycle.swap_net_profiles t JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(t.catalog_id,t.publication_order)
 WHERE(t.catalog_id,t.trade_id,t.id,t.revision)=(catalog,trade,profile,swap_net_profile_document.revision) AND t.publication_order<=prefix AND k.known_from<=cut;
 SELECT t.* INTO r FROM trading.swap_trade_revisions t JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(t.catalog_id,t.publication_order)
 WHERE(t.catalog_id,t.trade_id)=(catalog,trade) AND t.publication_order<=prefix AND k.known_from<=cut AND t.effective_at<=as_of ORDER BY t.revision DESC LIMIT 1;
 IF p.id IS NULL OR r.revision IS DISTINCT FROM p.origin_revision THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 PERFORM lifecycle.require_swap_net_reference(catalog,trade,p.id,p.revision);
 PERFORM lifecycle.require_read(catalog,until_at);RETURN to_jsonb(p)||jsonb_build_object('binding_id',r.binding_id);
END$$;
-- Public source reads require the exact selected bound frame. The writer's
-- private document path is separately admitted by the immutable source pin.
CREATE FUNCTION lifecycle.read_swap_net_profile(catalog uuid,trade uuid,profile uuid,revision integer,context uuid,as_of timestamptz,business_date date) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;result jsonb;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);
 result=lifecycle.swap_net_profile_document(catalog,trade,profile,revision,context,as_of,business_date);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);RETURN result;
END$$;
CREATE FUNCTION lifecycle.save_swap_net_profile(catalog uuid,body jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;actor uuid;operation uuid;prior jsonb;r trading.swap_trade_revisions;p lifecycle.action_policies;
 s lifecycle.swap_net_profiles;fact lifecycle.swap_net_profiles;expected integer;publication integer;result jsonb;changed boolean=false;BEGIN
 until_at=lifecycle.capture_action_authority(catalog,'swap_net_profile',body);operation=(body->>'operationId')::uuid;
 prior=lifecycle.command_status(catalog,operation,'swap_net_profile',body,'AmendTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);actor=lifecycle.require_authority(catalog,'AmendTrade',until_at);
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','profileId','tradeId','expectedRevision','originRevision','action','policyId','policyRevision',
 'referenceCatalogId','referenceRevisionId','currency','decimalPlaces','roundingPolicy','duePolicy','recognitionPolicy','correctionPolicy','netPolicy'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','profileId','tradeId','expectedRevision','originRevision','action','policyId','policyRevision',
 'referenceCatalogId','referenceRevisionId','currency','decimalPlaces','roundingPolicy','duePolicy','recognitionPolicy','correctionPolicy','netPolicy'))
 OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[0-9]{1,8}$'
 OR jsonb_typeof(body->'originRevision') IS DISTINCT FROM 'number' OR body->>'originRevision'!~'^[1-9][0-9]{0,7}$'
 OR body->>'action' IS DISTINCT FROM 'swap.net' THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 SELECT * INTO r FROM trading.swap_trade_revisions t WHERE(t.catalog_id,t.trade_id,t.revision)=(catalog,(body->>'tradeId')::uuid,(body->>'originRevision')::integer);
 IF r.trade_id IS NULL THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
 SELECT * INTO p FROM lifecycle.action_policies t WHERE(t.catalog_id,t.id,t.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer);
 IF p.action IS DISTINCT FROM 'swap.net' OR p.mode NOT IN('manual','review') THEN RAISE EXCEPTION 'lifecycle_action_policy';END IF;
 s=jsonb_populate_record(NULL::lifecycle.swap_net_profiles,jsonb_build_object('catalog_id',catalog,'id',(body->>'profileId')::uuid,'trade_id',r.trade_id,'origin_revision',r.revision,
 'policy_id',p.id,'policy_revision',p.revision,'reference_catalog_id',body->>'referenceCatalogId','reference_revision_id',body->>'referenceRevisionId','currency_code',body->>'currency',
 'decimal_places',(body->>'decimalPlaces')::integer,'rounding_policy',body->>'roundingPolicy','due_policy',body->>'duePolicy',
 'recognition_policy',body->>'recognitionPolicy','correction_policy',body->>'correctionPolicy','net_policy',body->>'netPolicy'));
 IF s.id IS NULL OR s.id='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 SELECT * INTO fact FROM lifecycle.swap_net_profiles t WHERE(to_jsonb(t)-ARRAY['id','revision','publication_order'])=(to_jsonb(s)-ARRAY['id','revision','publication_order']) ORDER BY t.revision LIMIT 1;
 IF fact.id IS NULL THEN
  IF r.revision IS DISTINCT FROM(SELECT max(t.revision) FROM trading.swap_trade_revisions t WHERE(t.catalog_id,t.trade_id)=(catalog,r.trade_id)) THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
  IF EXISTS(SELECT 1 FROM lifecycle.swap_net_profiles t WHERE(t.catalog_id,t.id)=(catalog,s.id) AND t.trade_id<>s.trade_id)
   OR EXISTS(SELECT 1 FROM lifecycle.swap_net_profiles t WHERE(t.catalog_id,t.trade_id)=(catalog,s.trade_id) AND t.id<>s.id) THEN RAISE EXCEPTION 'lifecycle_profile_identity';END IF;
  expected=(body->>'expectedRevision')::integer;
  IF coalesce((SELECT max(t.revision) FROM lifecycle.swap_net_profiles t WHERE(t.catalog_id,t.id)=(catalog,s.id)),0)<>expected THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
  SELECT coalesce(max(t.publication_order),0)+1 INTO publication FROM trading.publications t WHERE t.catalog_id=catalog;
  INSERT INTO trading.publications(catalog_id,publication_order,publication_xid) VALUES(catalog,publication,pg_current_xact_id());
  s.publication_order=publication;s.revision=expected+1;INSERT INTO lifecycle.swap_net_profiles SELECT(s).*;fact=s;changed=true;
 END IF;
 PERFORM lifecycle.require_swap_net_reference(catalog,r.trade_id,fact.id,fact.revision);
 result=jsonb_build_object('operationId',operation,'tradeId',r.trade_id,'profileId',fact.id,'revision',fact.revision,
  'acknowledgedRevision',fact.revision,'currentRevision',(SELECT max(t.revision) FROM lifecycle.swap_net_profiles t WHERE(t.catalog_id,t.id)=(catalog,fact.id)),
  'changed',changed,'semanticAcknowledgement',NOT changed);
 INSERT INTO lifecycle.commands(catalog_id,operation_id,actor_id,kind,request,outcome,policy_id,policy_revision,authority_until)
 VALUES(catalog,operation,actor,'swap_net_profile',body,result,fact.policy_id,fact.policy_revision,until_at);
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);RETURN result;
END$$;
CREATE FUNCTION lifecycle.swap_net_native_request(body jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT body#>'{selection,net}'||jsonb_build_object('operationId',body->'operationId','previewHash',body->'previewHash');
$$;
CREATE OR REPLACE FUNCTION lifecycle.finish_command(catalog uuid,operation uuid,kind text,body jsonb,permission text,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;c lifecycle.commands;p lifecycle.action_policies;BEGIN
 IF kind NOT IN('swap.net','swap_net_profile') THEN PERFORM lifecycle.finish_command_pre_net(catalog,operation,kind,body,permission,until_at);RETURN;END IF;
 IF permission IS DISTINCT FROM 'AmendTrade' THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 actor=lifecycle.require_authority(catalog,permission,until_at);
 SELECT * INTO c FROM lifecycle.commands t WHERE(t.catalog_id,t.operation_id)=(catalog,operation);
 IF c.operation_id IS NULL OR(c.actor_id,c.kind,c.request) IS DISTINCT FROM(actor,kind,body) THEN RAISE EXCEPTION 'lifecycle_command_conflict';END IF;
 SELECT * INTO p FROM lifecycle.action_policies t WHERE(t.catalog_id,t.id,t.revision)=(catalog,c.policy_id,c.policy_revision);
 IF p.id IS NULL OR p.action IS DISTINCT FROM 'swap.net' OR p.mode NOT IN('manual','review') THEN RAISE EXCEPTION 'lifecycle_action_policy';END IF;
 IF kind='swap_net_profile' THEN
  PERFORM lifecycle.require_swap_net_reference(catalog,(body->>'tradeId')::uuid,(c.outcome->>'profileId')::uuid,(c.outcome->>'revision')::integer);
 ELSE
  PERFORM settlement_data.swap_command_status(catalog,operation,lifecycle.swap_net_native_request(body));
  PERFORM lifecycle.require_swap_net_reference(catalog,(body#>>'{target,scopeId}')::uuid,(body#>>'{selection,profileId}')::uuid,(body#>>'{selection,profileRevision}')::integer);
 END IF;
 PERFORM lifecycle.require_authority(catalog,permission,until_at);
END$$;
CREATE OR REPLACE FUNCTION lifecycle.record_native_action(catalog uuid,body jsonb,outcome jsonb,until_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;operation uuid;prior jsonb;native jsonb;request jsonb;ack uuid;p lifecycle.action_policies;BEGIN
 IF body->>'action' IS DISTINCT FROM 'swap.net' THEN RETURN lifecycle.record_native_action_pre_net(catalog,body,outcome,until_at);END IF;
 actor=lifecycle.require_authority(catalog,'AmendTrade',until_at);operation=(body->>'operationId')::uuid;
 prior=lifecycle.command_status(catalog,operation,'swap.net',body,'AmendTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 p=lifecycle.require_policy(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,'swap.net',
  settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}'),(body->>'automatic')::boolean);
 SELECT c.outcome,c.request,c.acknowledged_operation_id INTO native,request,ack FROM settlement_data.swap_commands c
 WHERE(c.catalog_id,c.operation_id,c.actor_id,c.trade_id)=(catalog,operation,actor,(body#>>'{target,scopeId}')::uuid);
 IF native IS NULL OR native IS DISTINCT FROM outcome OR request IS DISTINCT FROM lifecycle.swap_net_native_request(body) THEN RAISE EXCEPTION 'lifecycle_native_receipt';END IF;
 IF ack IS NULL AND(body#>>'{selection,profileRevision}')::integer IS DISTINCT FROM(SELECT max(t.revision) FROM lifecycle.swap_net_profiles t
 WHERE(t.catalog_id,t.id)=(catalog,(body#>>'{selection,profileId}')::uuid)) THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 PERFORM lifecycle.require_swap_net_reference(catalog,(body#>>'{target,scopeId}')::uuid,(body#>>'{selection,profileId}')::uuid,(body#>>'{selection,profileRevision}')::integer);
 INSERT INTO lifecycle.commands(catalog_id,operation_id,actor_id,kind,request,outcome,policy_id,policy_revision,authority_until)
 VALUES(catalog,operation,actor,'swap.net',body,outcome,p.id,p.revision,until_at);
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);RETURN outcome;
END$$;
CREATE FUNCTION lifecycle.record_swap_net_action(catalog uuid,body jsonb,original_until timestamptz,calculation jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;prior jsonb;info jsonb;request jsonb;preview jsonb;result jsonb;at timestamptz;source_until timestamptz;context uuid;business_date date;
 lines jsonb;cash numeric;original settlement_data.swap_cash_payments;BEGIN
 until_at=least(original_until,lifecycle.capture_action_authority(catalog,'swap.net',body));
 prior=lifecycle.command_status(catalog,(body->>'operationId')::uuid,'swap.net',body,'AmendTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'))
 OR jsonb_typeof(body->'context') IS DISTINCT FROM 'object' OR NOT((body->'context')?&ARRAY['contextId','asOf','businessDate'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'context')k WHERE k NOT IN('contextId','asOf','businessDate'))
 OR jsonb_typeof(body->'target') IS DISTINCT FROM 'object' OR NOT((body->'target')?&ARRAY['handler','scopeId','bindingId'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'target')k WHERE k NOT IN('handler','scopeId','bindingId'))
 OR body->>'action' IS DISTINCT FROM 'swap.net' OR body#>>'{target,handler}' IS DISTINCT FROM 'financial_swap_net'
 OR jsonb_typeof(body->'automatic') IS DISTINCT FROM 'boolean' OR body->>'automatic' IS DISTINCT FROM 'false'
 OR jsonb_typeof(body->'policyRevision') IS DISTINCT FROM 'number' OR body->>'policyRevision'!~'^[1-9][0-9]{0,7}$'
 OR body->>'previewHash' IS NULL OR body->>'previewHash'!~'^[0-9a-f]{64}$'
 OR jsonb_typeof(body->'selection') IS DISTINCT FROM 'object' OR NOT((body->'selection')?&ARRAY['snapshotId','profileId','profileRevision','net'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'selection')k WHERE k NOT IN('snapshotId','profileId','profileRevision','net'))
 OR jsonb_typeof(body#>'{selection,profileRevision}') IS DISTINCT FROM 'number' OR body#>>'{selection,profileRevision}'!~'^[1-9][0-9]{0,7}$'
 THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 at=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');context=(body#>>'{context,contextId}')::uuid;business_date=(body#>>'{context,businessDate}')::date;
 SELECT q.business_source_until INTO source_until FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
 AND(q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of,q.business_source_on)=(catalog,context,at,business_date);
 IF source_until IS NULL THEN RAISE EXCEPTION 'trading_source_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_admitted_business_source(catalog,context,at,business_date,source_until);
 info=lifecycle.swap_net_profile_document(catalog,(body#>>'{target,scopeId}')::uuid,(body#>>'{selection,profileId}')::uuid,(body#>>'{selection,profileRevision}')::integer,context,at,business_date);
 IF(info->>'binding_id')::uuid IS DISTINCT FROM(body#>>'{target,bindingId}')::uuid OR(info->>'policy_id')::uuid IS DISTINCT FROM(body->>'policyId')::uuid
 OR(info->>'policy_revision')::integer IS DISTINCT FROM(body->>'policyRevision')::integer THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 PERFORM lifecycle.require_policy(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,'swap.net',at,false);
 request=body#>'{selection,net}';preview=request->'preview';
 IF jsonb_typeof(request) IS DISTINCT FROM 'object' OR NOT(request?&ARRAY['preview','effectiveAt','reference'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(request)k WHERE k NOT IN('preview','effectiveAt','reference'))
 OR jsonb_typeof(preview) IS DISTINCT FROM 'object' OR NOT(preview?&ARRAY['context','tradeId','expectedRevision','expectedVersion','currency','amount','allocations','policy','reversesId'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(preview)k WHERE k NOT IN('context','tradeId','expectedRevision','expectedVersion','currency','amount','allocations','policy','reversesId'))
 OR jsonb_typeof(preview->'expectedRevision') IS DISTINCT FROM 'number' OR preview->>'expectedRevision'!~'^[1-9][0-9]{0,7}$'
 OR jsonb_typeof(preview->'expectedVersion') IS DISTINCT FROM 'number' OR preview->>'expectedVersion'!~'^[0-9]{1,8}$'
 OR(preview->>'expectedRevision')::integer IS DISTINCT FROM(info->>'origin_revision')::integer
 OR preview#>>'{context,snapshotId}' IS DISTINCT FROM body#>>'{selection,snapshotId}' OR preview#>>'{context,convention}' IS DISTINCT FROM 'independent_side_financial_swap_v2'
 OR preview#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR jsonb_typeof(preview#>'{context,contextId}') IS DISTINCT FROM 'null'
 OR jsonb_typeof(preview->'context') IS DISTINCT FROM 'object' OR NOT((preview->'context')?&ARRAY['snapshotId','convention','knowledgeMode','contextId'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(preview->'context')k WHERE k NOT IN('snapshotId','convention','knowledgeMode','contextId'))
 OR jsonb_typeof(preview->'currency') IS DISTINCT FROM 'object' OR NOT((preview->'currency')?&ARRAY['revision','code'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(preview->'currency')k WHERE k NOT IN('revision','code'))
 OR jsonb_typeof(preview#>'{currency,revision}') IS DISTINCT FROM 'object' OR NOT((preview#>'{currency,revision}')?&ARRAY['catalogId','revisionId'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(preview#>'{currency,revision}')k WHERE k NOT IN('catalogId','revisionId'))
 OR preview->>'tradeId' IS DISTINCT FROM body#>>'{target,scopeId}' OR preview->>'policy' IS DISTINCT FROM info->>'net_policy'
 OR(preview#>>'{currency,revision,catalogId}',preview#>>'{currency,revision,revisionId}',preview#>>'{currency,code}')
 IS DISTINCT FROM(info->>'reference_catalog_id',info->>'reference_revision_id',info->>'currency_code')
 OR jsonb_typeof(calculation) IS DISTINCT FROM 'object' OR NOT(calculation?&ARRAY['contextId','amount','allocations'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(calculation)k WHERE k NOT IN('contextId','amount','allocations'))
 OR calculation->>'contextId' IS DISTINCT FROM context::text OR jsonb_typeof(calculation->'allocations') IS DISTINCT FROM 'array'
 OR jsonb_array_length(calculation->'allocations') NOT BETWEEN 2 AND 256 THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 lines=calculation->'allocations';cash=(calculation->>'amount')::numeric;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(lines)x WHERE jsonb_typeof(x) IS DISTINCT FROM 'object' OR NOT(x?&ARRAY['determination','amount'])
  OR EXISTS(SELECT 1 FROM jsonb_object_keys(x)k WHERE k NOT IN('determination','amount'))) THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 IF preview->>'reversesId' IS NULL THEN
  IF jsonb_typeof(preview->'allocations') IS DISTINCT FROM 'array' OR jsonb_array_length(preview->'allocations') NOT BETWEEN 2 AND 256
  OR cash IS DISTINCT FROM(preview->>'amount')::numeric OR EXISTS((SELECT x.determination,x.amount FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric)
   EXCEPT SELECT x."determinationId",x.amount FROM jsonb_to_recordset(preview->'allocations')x("determinationId" uuid,amount numeric))
   UNION ALL(SELECT x."determinationId",x.amount FROM jsonb_to_recordset(preview->'allocations')x("determinationId" uuid,amount numeric)
   EXCEPT SELECT x.determination,x.amount FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric))) THEN RAISE EXCEPTION 'lifecycle_native_calculation';END IF;
 ELSE
  IF jsonb_typeof(preview->'amount') IS DISTINCT FROM 'null' OR preview->'allocations' IS DISTINCT FROM '[]'::jsonb THEN RAISE EXCEPTION 'settlement_reversal';END IF;
  SELECT * INTO original FROM settlement_data.swap_cash_payments p WHERE(p.catalog_id,p.trade_id,p.id)=(catalog,(body#>>'{target,scopeId}')::uuid,(preview->>'reversesId')::uuid);
  IF original.id IS NULL OR cash IS DISTINCT FROM -original.amount OR EXISTS((SELECT x.determination,x.amount FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric)
    EXCEPT SELECT a.determination_id,-a.amount FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(catalog,original.id))
    UNION ALL(SELECT a.determination_id,-a.amount FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(catalog,original.id)
    EXCEPT SELECT x.determination,x.amount FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric))) THEN RAISE EXCEPTION 'settlement_reversal';END IF;
 END IF;
 IF EXISTS(SELECT 1 FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric) JOIN settlement_data.swap_determinations d ON(d.catalog_id,d.id)=(catalog,x.determination)
  JOIN settlement_data.swap_occurrences o ON(o.catalog_id,o.id)=(d.catalog_id,d.occurrence_id) WHERE(o.decimal_places,o.rounding_policy,o.due_policy,o.recognition_policy,o.correction_policy)
  IS DISTINCT FROM((info->>'decimal_places')::integer,info->>'rounding_policy',info->>'due_policy',info->>'recognition_policy',info->>'correction_policy'))
 THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 result=settlement_data.record_swap_net(catalog,(body->>'operationId')::uuid,(body#>>'{target,scopeId}')::uuid,(info->>'origin_revision')::integer,(preview->>'expectedVersion')::integer,
  context,at,lines,cash,(info->>'reference_catalog_id')::uuid,(info->>'reference_revision_id')::uuid,info->>'currency_code',
  settlement_data.futures_margin_utc_instant(request->>'effectiveAt'),request->>'reference',(preview->>'reversesId')::uuid,lifecycle.swap_net_native_request(body));
 PERFORM security.require_admitted_business_source(catalog,context,at,business_date,source_until);
 RETURN lifecycle.record_native_action(catalog,body,result,until_at);
END$$;
DO $functions$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='lifecycle' AND
  p.proname IN('require_swap_net_reference','swap_net_profile_document','read_swap_net_profile','save_swap_net_profile','swap_net_native_request','record_swap_net_action') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$functions$;
GRANT EXECUTE ON FUNCTION lifecycle.save_swap_net_profile(uuid,jsonb),lifecycle.read_swap_net_profile(uuid,uuid,uuid,integer,uuid,timestamptz,date),
 lifecycle.record_swap_net_action(uuid,jsonb,timestamptz,jsonb) TO openetrm_runtime;
COMMIT;
