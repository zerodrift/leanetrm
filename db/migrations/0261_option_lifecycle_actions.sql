-- Exact option action settings and atomic exercise-to-margin enrollment.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE trading.futures_option_events ADD UNIQUE(catalog_id,trade_id,id);
CREATE TABLE lifecycle.option_profiles(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,option_revision integer NOT NULL,action text NOT NULL,revision integer NOT NULL CHECK(revision>0),
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,margin_policy_id uuid NOT NULL,margin_policy_revision integer NOT NULL,
 definition_revision_id uuid NOT NULL,underlying_contract_id uuid NOT NULL,future_mark_curve ref_data.code NOT NULL,expiry_curve ref_data.code,
 publication_order integer NOT NULL,PRIMARY KEY(catalog_id,trade_id,action,revision),
 CHECK((action='option.expiry')=(expiry_curve IS NOT NULL)),
 FOREIGN KEY(catalog_id,trade_id,option_revision) REFERENCES trading.futures_option_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,margin_policy_id,margin_policy_revision) REFERENCES settlement_data.futures_margin_policies(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,definition_revision_id,future_mark_curve,underlying_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,definition_revision_id,expiry_curve,underlying_contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order));
CREATE TABLE lifecycle.option_effects(
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,action text NOT NULL,evidence_id uuid NOT NULL,event_id uuid NOT NULL,
 profile_revision integer NOT NULL,request jsonb NOT NULL,PRIMARY KEY(catalog_id,trade_id,action,evidence_id),
 FOREIGN KEY(catalog_id,trade_id,event_id) REFERENCES trading.futures_option_events(catalog_id,trade_id,id),
 FOREIGN KEY(catalog_id,trade_id,action,profile_revision) REFERENCES lifecycle.option_profiles(catalog_id,trade_id,action,revision));
DO $tables$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['option_profiles','option_effects'] LOOP
  EXECUTE format('ALTER TABLE lifecycle.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE lifecycle.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON lifecycle.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON lifecycle.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON lifecycle.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$tables$;
CREATE POLICY scoped_read ON lifecycle.option_profiles FOR SELECT TO openetrm_runtime USING(
 security.trading_visible(catalog_id,publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00') AND EXISTS(
 SELECT 1 FROM trading.futures_option_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision)=(option_profiles.catalog_id,option_profiles.trade_id,option_profiles.option_revision)));
GRANT SELECT ON lifecycle.option_profiles TO openetrm_runtime;
CREATE FUNCTION lifecycle.require_option_future_mark(catalog uuid,definition uuid,contract uuid,curve text) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE f market_data.contracts;mark market_data.curves;s ref_data.futures_series;p ref_data.futures_products;BEGIN
 SELECT * INTO f FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.id,c.kind)=(catalog,definition,contract,'future');
 SELECT * INTO mark FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,definition,curve);
 SELECT * INTO s FROM ref_data.futures_series v WHERE(v.catalog_id,v.revision_id,v.code)=(f.reference_catalog_id,f.reference_revision_id,f.futures_series_code);
 SELECT * INTO p FROM ref_data.futures_products v WHERE(v.catalog_id,v.revision_id,v.code)=(s.catalog_id,s.revision_id,s.product_code);
 IF f.id IS NULL OR mark.code IS NULL OR mark.rule IS DISTINCT FROM 'direct'
 OR(p.commodity_code,p.location_code) IS DISTINCT FROM(mark.commodity_code,mark.location_code)
 OR NOT EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.contract_id,m.curve_code)=(catalog,definition,contract,curve))
 THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
 BEGIN
  PERFORM security.futures_execution_start(f.reference_catalog_id,f.reference_revision_id,f.futures_series_code,'contract_lot_execution_pnl_v2',mark.direct_role,mark.direct_source_code);
 EXCEPTION WHEN SQLSTATE 'P0001' THEN RAISE EXCEPTION 'lifecycle_option_source';END;
END$$;
CREATE FUNCTION lifecycle.option_effect_identity(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE lots numeric;at timestamptz;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'))
 OR jsonb_typeof(body->'automatic') IS DISTINCT FROM 'boolean' OR body->>'previewHash' IS NULL OR body->>'previewHash'!~'^[0-9a-f]{64}$'
 OR jsonb_typeof(body->'policyRevision') IS DISTINCT FROM 'number' OR body->>'policyRevision'!~'^[1-9][0-9]{0,7}$'
 OR jsonb_typeof(body->'context') IS DISTINCT FROM 'object' OR NOT((body->'context')?&ARRAY['contextId','asOf','businessDate'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'context')k WHERE k NOT IN('contextId','asOf','businessDate'))
 OR body#>>'{context,businessDate}' IS NULL OR body#>>'{context,businessDate}'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
 OR jsonb_typeof(body->'target') IS DISTINCT FROM 'object' OR NOT((body->'target')?&ARRAY['handler','scopeId','bindingId'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'target')k WHERE k NOT IN('handler','scopeId','bindingId'))
 OR body#>>'{target,handler}' IS DISTINCT FROM 'listed_option'
 OR jsonb_typeof(body->'selection') IS DISTINCT FROM 'object' OR NOT((body->'selection')?&ARRAY['evidenceId','lots','expectedEventVersion','profileRevision'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'selection')k WHERE k NOT IN('evidenceId','lots','expectedEventVersion','profileRevision'))
 OR jsonb_typeof(body#>'{selection,expectedEventVersion}') IS DISTINCT FROM 'number' OR body#>>'{selection,expectedEventVersion}'!~'^[0-9]{1,8}$'
 OR jsonb_typeof(body#>'{selection,profileRevision}') IS DISTINCT FROM 'number' OR body#>>'{selection,profileRevision}'!~'^[1-9][0-9]{0,7}$'
 THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 IF body->>'action' NOT IN('option.exercise','option.assignment','option.expiry','option.exercise_instruction','option.abandon_instruction')
 THEN RAISE EXCEPTION 'lifecycle_action_unsupported';END IF;
 at=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');
 IF body->>'action' IN('option.exercise','option.assignment') THEN
  IF jsonb_typeof(body#>'{selection,lots}') IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'option_lots';END IF;
  lots=(body#>>'{selection,lots}')::numeric;
  IF lots IS NULL OR lots<=0 OR lots<>trunc(lots) OR lots::text IN('NaN','Infinity','-Infinity') THEN RAISE EXCEPTION 'option_lots';END IF;
 ELSIF jsonb_typeof(body#>'{selection,lots}') IS DISTINCT FROM 'null' THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 RETURN jsonb_build_object('target',body->'target','action',body->'action','policyId',body->'policyId','policyRevision',body->'policyRevision',
  'at',to_char(at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'businessDate',body#>'{context,businessDate}',
  'evidenceId',body#>'{selection,evidenceId}','lots',trim_scale(lots)::text,'profileRevision',body#>'{selection,profileRevision}',
  -- Expiry consumes an official price selection. Preserve its accepted source
  -- and review rather than acknowledging a different fixing as the old expiry.
  'expirySource',CASE WHEN body->>'action'='option.expiry' THEN jsonb_build_object('contextId',body#>'{context,contextId}','review',body->'previewHash') END);
END$$;
CREATE FUNCTION lifecycle.require_option_reference(catalog uuid,trade uuid,action text,revision integer) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r trading.futures_option_trade_revisions;c market_data.contracts;p settlement_data.futures_margin_policies;configured lifecycle.option_profiles;BEGIN
 SELECT * INTO r FROM trading.futures_option_trade_revisions t WHERE(t.catalog_id,t.trade_id)=(catalog,trade);
 SELECT * INTO c FROM market_data.contracts t WHERE(t.catalog_id,t.revision_id,t.id)=(catalog,r.definition_revision_id,r.contract_id);
 SELECT m.* INTO p FROM settlement_data.futures_margin_policies m JOIN lifecycle.option_profiles s
 ON(s.catalog_id,s.margin_policy_id,s.margin_policy_revision)=(m.catalog_id,m.id,m.revision)
 WHERE(s.catalog_id,s.trade_id,s.action,s.revision)=(catalog,trade,require_option_reference.action,require_option_reference.revision);
 SELECT * INTO configured FROM lifecycle.option_profiles t WHERE(t.catalog_id,t.trade_id,t.action,t.revision)=(catalog,trade,require_option_reference.action,require_option_reference.revision);
 IF c.id IS NULL OR p.id IS NULL OR security.reference_access(c.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 OR security.reference_version_visible(c.reference_catalog_id,c.reference_revision_id) IS NOT TRUE OR security.market_fact_visible(catalog,r.definition_revision_id,NULL) IS NOT TRUE
 THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 PERFORM settlement_data.require_futures_margin_reference(p);
 IF NOT EXISTS(SELECT 1 FROM market_data.curve_members m JOIN market_data.curves v ON(v.catalog_id,v.revision_id,v.code)=(m.catalog_id,m.revision_id,m.curve_code)
 WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(catalog,configured.definition_revision_id,configured.future_mark_curve,configured.underlying_contract_id)
 AND(v.reference_catalog_id,v.reference_revision_id,v.quote_code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code)) OR configured.expiry_curve IS NOT NULL AND NOT EXISTS(
 SELECT 1 FROM market_data.curve_members m JOIN market_data.curves v ON(v.catalog_id,v.revision_id,v.code)=(m.catalog_id,m.revision_id,m.curve_code)
 WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(catalog,configured.definition_revision_id,configured.expiry_curve,configured.underlying_contract_id)
 AND v.rule='direct' AND v.direct_role='daily_settlement' AND(v.reference_catalog_id,v.reference_revision_id,v.quote_code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code))
 THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
 PERFORM lifecycle.require_option_future_mark(catalog,configured.definition_revision_id,configured.underlying_contract_id,configured.future_mark_curve);
END$$;
CREATE FUNCTION lifecycle.save_option_profile(catalog uuid,body jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;until_at timestamptz;operation uuid;prior jsonb;p lifecycle.action_policies;r trading.futures_option_trade_revisions;
 m settlement_data.futures_margin_policies;option market_data.contracts;s ref_data.futures_option_series;f market_data.contracts;
 profile lifecycle.option_profiles;expected integer;publication integer;result jsonb;changed boolean=false;BEGIN
 until_at=lifecycle.capture_action_authority(catalog,'option_profile',body);operation=(body->>'operationId')::uuid;
 prior=lifecycle.command_status(catalog,operation,'option_profile',body,'AmendTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);actor=lifecycle.require_authority(catalog,'AmendTrade',until_at);
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','tradeId','action','expectedRevision','policyId','policyRevision','marginPolicyId','marginPolicyRevision'])
 OR NOT(body?&ARRAY['futureMarkCurve','expiryCurve'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','tradeId','action','expectedRevision','policyId','policyRevision','marginPolicyId','marginPolicyRevision','futureMarkCurve','expiryCurve'))
 OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[0-9]{1,8}$'
 OR body->>'action' NOT IN('option.exercise','option.assignment','option.expiry','option.exercise_instruction','option.abandon_instruction') THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 SELECT * INTO r FROM trading.futures_option_trade_revisions t WHERE(t.catalog_id,t.trade_id)=(catalog,(body->>'tradeId')::uuid);
 SELECT * INTO p FROM lifecycle.action_policies t WHERE(t.catalog_id,t.id,t.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer);
 SELECT * INTO m FROM settlement_data.futures_margin_policies t WHERE(t.catalog_id,t.id,t.revision)=(catalog,(body->>'marginPolicyId')::uuid,(body->>'marginPolicyRevision')::integer);
 IF r.trade_id IS NULL OR p.id IS NULL OR p.action IS DISTINCT FROM body->>'action' OR m.id IS NULL THEN RAISE EXCEPTION 'lifecycle_option_profile';END IF;
 SELECT * INTO option FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.id)=(catalog,r.definition_revision_id,r.contract_id);
 SELECT * INTO s FROM ref_data.futures_option_series v WHERE(v.catalog_id,v.revision_id,v.code)=(option.reference_catalog_id,option.reference_revision_id,option.futures_option_series_code);
 SELECT * INTO f FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.kind,c.futures_series_code)=(catalog,r.definition_revision_id,'future',s.underlying_series_code);
 IF f.id IS NULL OR(m.definition_revision_id,m.contract_id,m.reference_catalog_id,m.reference_revision_id) IS DISTINCT FROM
 (r.definition_revision_id,f.id,f.reference_catalog_id,f.reference_revision_id) THEN RAISE EXCEPTION 'lifecycle_option_profile';END IF;
 PERFORM settlement_data.require_futures_margin_reference(m);
 IF NOT EXISTS(SELECT 1 FROM market_data.curve_members cm JOIN market_data.curves cc ON(cc.catalog_id,cc.revision_id,cc.code)=(cm.catalog_id,cm.revision_id,cm.curve_code)
 WHERE(cm.catalog_id,cm.revision_id,cm.contract_id,cm.curve_code)=(catalog,r.definition_revision_id,f.id,body->>'futureMarkCurve') AND(cc.reference_catalog_id,cc.reference_revision_id,cc.quote_code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code))
 OR(p.action='option.expiry') IS DISTINCT FROM(body->>'expiryCurve' IS NOT NULL) OR p.action='option.expiry' AND NOT EXISTS(
 SELECT 1 FROM market_data.curve_members cm JOIN market_data.curves cc ON(cc.catalog_id,cc.revision_id,cc.code)=(cm.catalog_id,cm.revision_id,cm.curve_code)
 WHERE(cm.catalog_id,cm.revision_id,cm.contract_id,cm.curve_code)=(catalog,r.definition_revision_id,f.id,body->>'expiryCurve')
 AND cc.rule='direct' AND cc.direct_role='daily_settlement' AND(cc.reference_catalog_id,cc.reference_revision_id,cc.quote_code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code)) THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
 PERFORM lifecycle.require_option_future_mark(catalog,r.definition_revision_id,f.id,body->>'futureMarkCurve');
 SELECT * INTO profile FROM lifecycle.option_profiles t WHERE(t.catalog_id,t.trade_id,t.action,t.policy_id,t.policy_revision,t.margin_policy_id,t.margin_policy_revision,t.future_mark_curve)=
 (catalog,r.trade_id,p.action,p.id,p.revision,m.id,m.revision,body->>'futureMarkCurve') AND t.expiry_curve IS NOT DISTINCT FROM body->>'expiryCurve' ORDER BY t.revision LIMIT 1;
 IF profile.trade_id IS NULL THEN
  expected=(body->>'expectedRevision')::integer;
  IF coalesce((SELECT max(t.revision) FROM lifecycle.option_profiles t WHERE(t.catalog_id,t.trade_id,t.action)=(catalog,r.trade_id,p.action)),0)<>expected THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
  SELECT coalesce(max(t.publication_order),0)+1 INTO publication FROM trading.publications t WHERE t.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO lifecycle.option_profiles VALUES(catalog,r.trade_id,r.revision,p.action,expected+1,p.id,p.revision,m.id,m.revision,r.definition_revision_id,f.id,body->>'futureMarkCurve',body->>'expiryCurve',publication) RETURNING * INTO profile;changed=true;
 END IF;
 result=jsonb_build_object('operationId',operation,'tradeId',r.trade_id,'action',p.action,'revision',profile.revision,
  'acknowledgedRevision',profile.revision,'currentRevision',(SELECT max(t.revision) FROM lifecycle.option_profiles t WHERE(t.catalog_id,t.trade_id,t.action)=(catalog,profile.trade_id,profile.action)),
  'changed',changed,'semanticAcknowledgement',NOT changed);
 INSERT INTO lifecycle.commands VALUES(catalog,operation,actor,'option_profile',body,result,p.id,p.revision,until_at);
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);RETURN result;
END$$;

-- A linked future exists inside the same transaction as its causal option event.
-- That exact event/publication proof admits enrollment; it supplies no fabricated
-- pre-commit knowledge receipt. Ordinary state reads still require certification.
CREATE FUNCTION lifecycle.enroll_option_future(catalog uuid,event uuid,profile lifecycle.option_profiles,context uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE e trading.futures_option_events;r trading.trade_revisions;m trading.futures_revision_markets;p settlement_data.futures_margin_policies;BEGIN
 SELECT * INTO STRICT e FROM trading.futures_option_events t WHERE(t.catalog_id,t.id)=(catalog,event);
 IF e.futures_trade_id IS NULL THEN RETURN;END IF;
 IF EXISTS(SELECT 1 FROM settlement_data.futures_margin_adoptions a WHERE(a.catalog_id,a.trade_id)=(catalog,e.futures_trade_id)) THEN RETURN;END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.publications t WHERE(t.catalog_id,t.publication_order)=(catalog,e.publication_order) AND t.publication_xid=pg_current_xact_id())
 THEN RAISE EXCEPTION 'lifecycle_option_causality';END IF;
 SELECT * INTO STRICT r FROM trading.trade_revisions t WHERE(t.catalog_id,t.trade_id,t.revision,t.publication_order)=(catalog,e.futures_trade_id,1,e.publication_order);
 SELECT * INTO STRICT m FROM trading.futures_revision_markets t WHERE(t.catalog_id,t.trade_id,t.revision)=(catalog,r.trade_id,1);
 SELECT * INTO STRICT p FROM settlement_data.futures_margin_policies t WHERE(t.catalog_id,t.id,t.revision)=(catalog,profile.margin_policy_id,profile.margin_policy_revision);
 IF(m.definition_revision_id,m.contract_id,m.reference_catalog_id,m.reference_revision_id) IS DISTINCT FROM
 (p.definition_revision_id,p.contract_id,p.reference_catalog_id,p.reference_revision_id) OR r.execution_at<p.from_at OR r.execution_at>=p.until_at
 OR NOT EXISTS(SELECT 1 FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code,c.quote_code)=(catalog,m.definition_revision_id,m.curve_code,p.quote_code))
 THEN RAISE EXCEPTION 'lifecycle_option_margin_policy';END IF;
 PERFORM settlement_data.require_futures_margin_reference(p);
 INSERT INTO settlement_data.futures_margin_adoptions VALUES(catalog,r.trade_id,r.binding_id,1,r.book_id,r.instrument_id,r.publication_order,
 p.id,p.revision,'native_execution_inception_v1',context,e.actor_id,e.publication_order,r.effective_at);
END$$;
CREATE FUNCTION lifecycle.option_proposal(catalog uuid,body jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;r trading.futures_option_trade_revisions;profile lifecycle.option_profiles;p lifecycle.action_policies;
 option market_data.contracts;s ref_data.futures_option_series;product ref_data.futures_option_products;future_product ref_data.futures_products;
 future market_data.contracts;source market_data.observations;rule ref_data.futures_option_rules;context uuid;at timestamptz;action_code text;kind text;
 lots numeric;open_lots numeric;event_version integer;prefix integer;market_prefix integer;cut timestamptz;result jsonb;decision text;
 candidates uuid[];underlying_curve market_data.curves;BEGIN
 until_at=lifecycle.capture_action_authority(catalog,body->>'action',body);action_code=body->>'action';kind=replace(action_code,'option.','');
 PERFORM lifecycle.option_effect_identity(body);
 context=(body#>>'{context,contextId}')::uuid;at=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');
 SELECT x.known_through,t.known_at,m.known_through INTO prefix,cut,market_prefix FROM market_data.temporal_contexts t
 JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(body#>>'{context,businessDate}')::date);
 IF prefix IS NULL THEN RAISE EXCEPTION 'lifecycle_context';END IF;
 SELECT t.* INTO r FROM trading.futures_option_trade_revisions t JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(t.catalog_id,t.publication_order)
 WHERE(t.catalog_id,t.trade_id)=(catalog,(body#>>'{target,scopeId}')::uuid) AND t.publication_order<=prefix AND k.known_from<=cut AND t.effective_at<=at;
 IF r.trade_id IS NULL OR r.option_instrument_id IS DISTINCT FROM(body#>>'{target,bindingId}')::uuid THEN RAISE EXCEPTION 'lifecycle_option_trade';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.knowledge_events e JOIN market_data.market_knowledge_receipts k
 ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
 JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(e.catalog_id,e.revision_id)
 WHERE(e.catalog_id,e.revision_id)=(catalog,r.definition_revision_id) AND e.observation_id IS NULL AND e.event_order<=market_prefix
 AND k.known_from<=cut AND v.effective_from<=(body#>>'{context,businessDate}')::date AND(body#>>'{context,businessDate}')::date<v.effective_to)
 THEN RAISE EXCEPTION 'lifecycle_context';END IF;
 SELECT t.* INTO profile FROM lifecycle.option_profiles t JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(t.catalog_id,t.publication_order)
 WHERE(t.catalog_id,t.trade_id,t.action)=(catalog,r.trade_id,action_code) AND t.publication_order<=prefix AND k.known_from<=cut ORDER BY t.revision DESC LIMIT 1;
 IF profile.revision IS DISTINCT FROM(body#>>'{selection,profileRevision}')::integer OR(profile.policy_id,profile.policy_revision) IS DISTINCT FROM
 ((body->>'policyId')::uuid,(body->>'policyRevision')::integer) THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 p=lifecycle.require_policy(catalog,profile.policy_id,profile.policy_revision,action_code,at,(body->>'automatic')::boolean);
 SELECT * INTO STRICT option FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.id)=(catalog,r.definition_revision_id,r.contract_id);
 SELECT * INTO STRICT s FROM ref_data.futures_option_series t WHERE(t.catalog_id,t.revision_id,t.code)=(option.reference_catalog_id,option.reference_revision_id,option.futures_option_series_code);
 SELECT * INTO STRICT product FROM ref_data.futures_option_products t WHERE(t.catalog_id,t.revision_id,t.code)=(s.catalog_id,s.revision_id,s.product_code);
 SELECT * INTO STRICT future FROM market_data.contracts c WHERE(c.catalog_id,c.revision_id,c.kind,c.futures_series_code)=(catalog,r.definition_revision_id,'future',s.underlying_series_code);
 SELECT fp.* INTO STRICT future_product FROM ref_data.futures_series fs JOIN ref_data.futures_products fp ON(fp.catalog_id,fp.revision_id,fp.code)=(fs.catalog_id,fs.revision_id,fs.product_code)
 WHERE(fs.catalog_id,fs.revision_id,fs.code)=(s.catalog_id,s.revision_id,s.underlying_series_code);
 IF security.reference_access(s.catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 SELECT count(*),abs(r.lots)-coalesce(sum(e.lots) FILTER(WHERE e.kind IN('exercise','assignment','expiry')),0) INTO event_version,open_lots
 FROM trading.futures_option_events e JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
 WHERE(e.catalog_id,e.trade_id)=(catalog,r.trade_id) AND e.publication_order<=prefix AND k.known_from<=cut AND e.effective_at<=at;
 IF event_version IS DISTINCT FROM(body#>>'{selection,expectedEventVersion}')::integer THEN RAISE EXCEPTION 'lifecycle_event_revision';END IF;
 IF open_lots<=0 THEN RAISE EXCEPTION 'option_holding_closed';END IF;
 lots=CASE WHEN kind IN('exercise','assignment') THEN(body#>>'{selection,lots}')::numeric ELSE open_lots END;
 IF lots IS NULL OR lots<=0 OR lots<>trunc(lots) OR lots>open_lots THEN RAISE EXCEPTION 'option_lots_exceeded';END IF;
 IF kind IN('exercise','assignment') AND ((kind='assignment')=(r.lots>0)) THEN RAISE EXCEPTION 'option_event_side';END IF;
 IF kind='expiry' THEN
  IF at<s.expiry_at THEN RAISE EXCEPTION 'option_expiry_not_reached';END IF;
  IF(body#>>'{selection,evidenceId}')::uuid IS DISTINCT FROM r.trade_id THEN RAISE EXCEPTION 'lifecycle_evidence';END IF;
  SELECT cc.* INTO STRICT underlying_curve FROM market_data.curves cc WHERE(cc.catalog_id,cc.revision_id,cc.code)=(catalog,profile.definition_revision_id,profile.expiry_curve);
  SELECT array_agg(o.id) INTO candidates FROM market_data.observations o JOIN market_data.series ss ON(ss.catalog_id,ss.revision_id,ss.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(o.catalog_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  WHERE(o.catalog_id,o.definition_revision_id,ss.contract_id,o.role,o.source_code,ss.quote_code)=(catalog,r.definition_revision_id,future.id,'daily_settlement',underlying_curve.direct_source_code,underlying_curve.quote_code)
  AND security.futures_option_market_date(future_product,o.observed_at)=s.last_trading_date AND o.observed_at<=at AND e.event_order<=market_prefix AND k.known_from<=cut
  AND NOT EXISTS(SELECT 1 FROM market_data.observations n JOIN market_data.knowledge_events ne ON(ne.catalog_id,ne.observation_id)=(n.catalog_id,n.id)
   JOIN market_data.market_knowledge_receipts nk ON(nk.catalog_id,nk.event_order,nk.publication_xid)=(ne.catalog_id,ne.event_order,ne.publication_xid)
   WHERE(n.catalog_id,n.corrects_id)=(o.catalog_id,o.id) AND n.observed_at<=at AND ne.event_order<=market_prefix AND nk.known_from<=cut);
  IF coalesce(cardinality(candidates),0)=0 THEN RAISE EXCEPTION 'option_expiry_settlement_missing';END IF;
  IF cardinality(candidates)<>1 THEN RAISE EXCEPTION 'option_expiry_settlement_ambiguous';END IF;
  SELECT * INTO STRICT source FROM market_data.observations o WHERE(o.catalog_id,o.id)=(catalog,candidates[1]);
  IF source.publication_state<>'final' THEN RAISE EXCEPTION 'option_expiry_settlement_missing';END IF;
  SELECT * INTO STRICT rule FROM ref_data.futures_option_rules t WHERE(t.catalog_id,t.revision_id,t.code,t.kind)=
   (product.catalog_id,product.revision_id,product.automatic_exercise_rule_code,product.automatic_exercise_rule_kind);
  SELECT CASE e.kind WHEN 'exercise_instruction' THEN 'exercised' ELSE 'abandoned' END INTO decision FROM trading.futures_option_events e
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
   WHERE(e.catalog_id,e.trade_id)=(catalog,r.trade_id) AND e.kind IN('exercise_instruction','abandon_instruction')
   AND e.publication_order<=prefix AND k.known_from<=cut AND e.effective_at<=at;
  decision=coalesce(decision,security.futures_option_automatic_outcome(rule,s.option_right,s.strike,source.value));
 ELSE
  IF kind IN('exercise','assignment') THEN
   IF at<r.execution_at OR at>=s.expiry_at OR security.futures_option_market_date(future_product,at)>s.last_trading_date THEN RAISE EXCEPTION 'option_exercise_window';END IF;
   IF NOT security.futures_option_business_day(future_product,security.futures_option_market_date(future_product,at)) THEN RAISE EXCEPTION 'option_calendar';END IF;
  ELSIF at<r.execution_at THEN RAISE EXCEPTION 'option_instruction_window';END IF;
  IF kind IN('exercise_instruction','abandon_instruction') AND(NOT product.contrary_instructions_allowed OR r.lots<0 OR s.instruction_cutoff_at IS NULL OR at>s.instruction_cutoff_at OR clock_timestamp()>s.instruction_cutoff_at)
  THEN RAISE EXCEPTION 'option_instruction_closed';END IF;
 END IF;
 result=jsonb_build_object('tradeId',r.trade_id,'bindingId',r.option_instrument_id,'action',action_code,'profileRevision',profile.revision,
  'policyId',p.id,'policyRevision',p.revision,'marginPolicyId',profile.margin_policy_id,'marginPolicyRevision',profile.margin_policy_revision,
  'futureMarkCurve',profile.future_mark_curve,'expiryCurve',profile.expiry_curve,
  'eventVersion',event_version,'openLots',trim_scale(open_lots)::text,'lots',trim_scale(lots)::text,
  'at',to_char(at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),'expiryAt',to_char(s.expiry_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'right',s.option_right,'strike',trim_scale(s.strike)::text,'decision',decision,'sourceObservationId',source.id,
  'futureContractId',future.id,'futureLots',CASE WHEN kind IN('exercise','assignment') OR decision='exercised' THEN lots*sign(r.lots)*CASE s.option_right WHEN 'call' THEN 1 ELSE -1 END ELSE 0 END);
 PERFORM lifecycle.require_authority(catalog,'BookTrade',until_at);
 RETURN jsonb_build_object('details',result,'hash',encode(sha256(convert_to(result::text,'UTF8')),'hex'),'authorityUntil',until_at);
END$$;
CREATE FUNCTION lifecycle.execute_option_action(catalog uuid,body jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE operation uuid;actor uuid;until_at timestamptz;prior jsonb;proposal jsonb;details jsonb;profile lifecycle.option_profiles;e trading.futures_option_events;
 effect lifecycle.option_effects;result jsonb;action_code text;evidence uuid;BEGIN
 until_at=lifecycle.capture_action_authority(catalog,body->>'action',body);operation=(body->>'operationId')::uuid;action_code=body->>'action';evidence=(body#>>'{selection,evidenceId}')::uuid;
 prior=lifecycle.command_status(catalog,operation,action_code,body,'BookTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);actor=lifecycle.require_authority(catalog,'BookTrade',until_at);
 PERFORM lifecycle.option_effect_identity(body);
 PERFORM lifecycle.require_policy(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,action_code,
  settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}'),(body->>'automatic')::boolean);
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,(body#>>'{context,contextId}')::uuid,(body#>>'{context,businessDate}')::date))
 THEN RAISE EXCEPTION 'lifecycle_context';END IF;
 IF evidence IS NULL OR evidence='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'lifecycle_evidence';END IF;
 SELECT * INTO effect FROM lifecycle.option_effects t WHERE(t.catalog_id,t.trade_id,t.action,t.evidence_id)=(catalog,(body#>>'{target,scopeId}')::uuid,action_code,evidence);
 IF effect.event_id IS NOT NULL THEN
  IF lifecycle.option_effect_identity(effect.request||jsonb_build_object('operationId',operation)) IS DISTINCT FROM lifecycle.option_effect_identity(body)
  THEN RAISE EXCEPTION 'lifecycle_evidence_conflict';END IF;
  SELECT * INTO STRICT e FROM trading.futures_option_events t WHERE(t.catalog_id,t.id)=(catalog,effect.event_id);result=security.futures_option_event_outcome(e);
 ELSE
  proposal=lifecycle.option_proposal(catalog,body);details=proposal->'details';
  IF proposal->>'hash' IS DISTINCT FROM body->>'previewHash' THEN RAISE EXCEPTION 'lifecycle_stale_review';END IF;
  SELECT * INTO STRICT profile FROM lifecycle.option_profiles t WHERE(t.catalog_id,t.trade_id,t.action,t.revision)=
   (catalog,(details->>'tradeId')::uuid,action_code,(details->>'profileRevision')::integer);
  IF profile.revision<>(SELECT max(t.revision) FROM lifecycle.option_profiles t WHERE(t.catalog_id,t.trade_id,t.action)=(catalog,profile.trade_id,action_code))
  THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
  IF(details->>'eventVersion')::integer<>(SELECT count(*) FROM trading.futures_option_events t WHERE(t.catalog_id,t.trade_id)=(catalog,profile.trade_id))
  THEN RAISE EXCEPTION 'lifecycle_event_revision';END IF;
  IF action_code='option.expiry' THEN
   result=security.apply_futures_option_event(catalog,operation,profile.trade_id,'expiry',NULL,NULL,(body#>>'{context,contextId}')::uuid,ARRAY[(details->>'sourceObservationId')::uuid],body);
  ELSE result=security.apply_futures_option_event(catalog,operation,profile.trade_id,replace(action_code,'option.',''),
   CASE WHEN action_code IN('option.exercise','option.assignment') THEN(details->>'lots')::numeric ELSE NULL END,
   settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}'),NULL,NULL,body);END IF;
  SELECT * INTO STRICT e FROM trading.futures_option_events t WHERE(t.catalog_id,t.request_id)=(catalog,operation);
  PERFORM lifecycle.enroll_option_future(catalog,e.id,profile,(body#>>'{context,contextId}')::uuid);
  INSERT INTO lifecycle.option_effects VALUES(catalog,profile.trade_id,action_code,evidence,e.id,profile.revision,body-'operationId');
 END IF;
 INSERT INTO lifecycle.commands VALUES(catalog,operation,actor,action_code,body,result,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,until_at);
 PERFORM lifecycle.require_authority(catalog,'BookTrade',until_at);RETURN result;
END$$;
-- Adapt the existing private executable event operation without duplicating its
-- exercise, assignment, threshold, instruction, lot or publication algorithms.
DO $explicit_mark$
DECLARE f pg_proc;definition text;before text;after text;BEGIN
 SELECT * INTO STRICT f FROM pg_proc WHERE oid='security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)'::regprocedure;
 IF md5(f.prosrc)<>'1c84a4ffdc41d652bb5739d0214d005a' OR f.prosecdef OR f.proowner<>'openetrm_security_owner'::regrole
 OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[] OR has_function_privilege('public',f.oid,'EXECUTE')
 OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') THEN RAISE EXCEPTION 'lifecycle_option_mark_upgrade';END IF;
 definition=pg_get_functiondef(f.oid);
 before=$old$ -- The linked future is valued, and the option expires, on the underlying's one direct daily-settlement curve.
 SELECT min(m.curve_code),count(*) INTO curve,curves FROM market_data.curve_members m JOIN market_data.curves v ON(v.catalog_id,v.revision_id,v.code)=(m.catalog_id,m.revision_id,m.curve_code)
  WHERE(m.catalog_id,m.revision_id,m.contract_id)=(catalog,r.definition_revision_id,future_contract.id) AND v.rule='direct' AND v.direct_role='daily_settlement';
 IF future_contract.id IS NULL OR curves<>1 THEN RAISE EXCEPTION 'option_underlying_market' USING ERRCODE='P0001';END IF;
$old$;
 after=$new$ -- Each action retains an explicit generated-future mark. Expiry retains its
 -- own selected source curve in the profile and passes only that certified fact.
 SELECT p.future_mark_curve INTO curve FROM lifecycle.option_profiles p
 WHERE(p.catalog_id,p.trade_id,p.action,p.revision)=(catalog,trade,request_value->>'action',(request_value#>>'{selection,profileRevision}')::integer);
 IF future_contract.id IS NULL OR curve IS NULL OR NOT EXISTS(SELECT 1 FROM market_data.curve_members m
 WHERE(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(catalog,r.definition_revision_id,curve,future_contract.id))
 THEN RAISE EXCEPTION 'lifecycle_option_source' USING ERRCODE='P0001';END IF;
$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'lifecycle_option_mark_upgrade';END IF;
 EXECUTE replace(definition,before,after);
END $explicit_mark$;
-- Runtime callers use the policy/evidence-bound operation; the primitive remains
-- private for its qualified native wrapper. No legacy event bypass is admitted.
REVOKE ALL ON FUNCTION security.record_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz),
 security.process_futures_option_expiry(uuid,uuid,uuid,uuid,uuid[]) FROM openetrm_runtime;
CREATE FUNCTION lifecycle.require_option_origin(catalog uuid,trade uuid,revision integer,until_at timestamptz)
 RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r trading.futures_option_trade_revisions;c market_data.contracts;BEGIN
 PERFORM lifecycle.require_read(catalog,until_at);
 SELECT * INTO r FROM trading.futures_option_trade_revisions t WHERE(t.catalog_id,t.trade_id,t.revision)=(catalog,trade,require_option_origin.revision);
 SELECT * INTO c FROM market_data.contracts t WHERE(t.catalog_id,t.revision_id,t.id)=(catalog,r.definition_revision_id,r.contract_id);
 IF r.trade_id IS NULL OR c.id IS NULL OR security.reference_access(c.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 OR security.reference_version_visible(c.reference_catalog_id,c.reference_revision_id) IS NOT TRUE
 OR security.market_fact_visible(catalog,r.definition_revision_id,NULL) IS NOT TRUE
 THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_trade_parties(catalog,jsonb_build_object('tradeDate',r.trade_date,'legalEntityId',r.legal_entity_id,
  'legalEntityRevision',r.legal_entity_revision,'counterpartyId',r.counterparty_id,'counterpartyRevision',r.counterparty_revision),r.execution_at);
 PERFORM lifecycle.require_read(catalog,until_at);
END$$;
DO $status_upgrade$DECLARE f pg_proc;BEGIN
 SELECT * INTO STRICT f FROM pg_proc WHERE oid='security.futures_option_operation_status(uuid,uuid)'::regprocedure;
 IF md5(f.prosrc)<>'878131d481fe8a9a062b9f58a75a9e9a' OR NOT f.prosecdef OR f.proowner<>'openetrm_security_owner'::regrole
 OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[] OR has_function_privilege('public',f.oid,'EXECUTE')
 OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') THEN RAISE EXCEPTION 'lifecycle_option_status_upgrade';END IF;
END$status_upgrade$;
CREATE OR REPLACE FUNCTION security.futures_option_operation_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;until_at timestamptz;r trading.futures_option_trade_revisions;e trading.futures_option_events;result jsonb;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 -- Previous producers use this fence. A negative status cannot race an already
 -- executing native event and cause a retained request to be dismissed early.
 PERFORM pg_advisory_xact_lock(730849102);PERFORM lifecycle.require_read(catalog,until_at);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 SELECT * INTO r FROM trading.futures_option_trade_revisions t WHERE(t.catalog_id,t.operation_id,t.actor_id)=(catalog,operation,actor);
 IF r.trade_id IS NOT NULL THEN
  PERFORM lifecycle.require_option_origin(catalog,r.trade_id,r.revision,until_at);
  result=jsonb_build_object('found',true,'operationId',operation,'tradeId',r.trade_id,'revision',r.revision,
   'optionInstrumentId',r.option_instrument_id,'premiumInstrumentId',r.premium_instrument_id);
 ELSE
  SELECT * INTO e FROM trading.futures_option_events t WHERE(t.catalog_id,t.request_id,t.actor_id)=(catalog,operation,actor);
  IF e.id IS NULL THEN result=jsonb_build_object('found',false);
  ELSE
   PERFORM lifecycle.require_option_origin(catalog,e.trade_id,e.trade_revision,until_at);
   IF e.rule_code IS NOT NULL AND(security.reference_access(e.rule_catalog_id,'ReadMarketData') IS NOT TRUE
    OR security.reference_version_visible(e.rule_catalog_id,e.rule_revision_id) IS NOT TRUE)
    OR e.settlement_observation_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM market_data.observations o
     WHERE(o.catalog_id,o.id)=(catalog,e.settlement_observation_id) AND security.market_fact_visible(catalog,o.definition_revision_id,o.id))
   THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
   result=security.futures_option_event_outcome(e)||jsonb_build_object('found',true,'revision',e.trade_revision);
  END IF;
 END IF;
 PERFORM lifecycle.require_read(catalog,until_at);RETURN result;
END$$;
-- Economic projection needs every accepted event known at the source cutoff,
-- including events that have not reached their business time at opening.
CREATE FUNCTION lifecycle.read_option_known_history(catalog uuid,trade uuid,binding uuid,revision integer,context uuid,as_of timestamptz,business_date date)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;source_until timestamptz;prefix integer;market_prefix integer;cut timestamptz;scope security.request_contexts;
 r trading.futures_option_trade_revisions;c market_data.contracts;s ref_data.futures_option_series;underlying market_data.contracts;
 e record;source record;fr trading.trade_revisions;fm trading.futures_revision_markets;a settlement_data.futures_margin_adoptions;
 p settlement_data.futures_margin_policies;profile lifecycle.option_profiles;party record;known_at timestamptz;
 events jsonb='[]';linked jsonb;observation jsonb;terms jsonb;occurrences jsonb;items jsonb;event_value jsonb;
 event_count bigint;occurrence_count bigint;native_bytes bigint;
 remaining numeric;previous_at timestamptz;result jsonb;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT q.business_source_until INTO source_until FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
 AND(q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of,q.business_source_on)=(catalog,context,as_of,business_date);
 IF source_until IS NULL THEN RAISE EXCEPTION 'trading_source_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,source_until);
 SELECT * INTO STRICT scope FROM security.request_contexts t WHERE t.backend_pid=pg_backend_pid()
 AND t.transaction_id=pg_current_xact_id_if_assigned() AND t.runtime_login=session_user;
 IF scope.market_context_id IS NULL THEN PERFORM security.use_market_context(catalog,context);
 ELSIF(scope.market_catalog_id,scope.market_context_id) IS DISTINCT FROM(catalog,context)
 THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
 IF scope.trading_as_of IS NOT NULL AND(scope.trading_as_of,scope.trading_business_date) IS DISTINCT FROM(as_of,business_date)
 THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
 IF scope.trading_as_of IS NULL THEN PERFORM security.use_trading_context(catalog,as_of,business_date);END IF;
 SELECT x.known_through,m.known_through,t.known_at INTO prefix,market_prefix,cut FROM market_data.temporal_contexts t
 JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,business_date);
 SELECT h.* INTO r FROM trading.futures_option_trade_revisions h JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(h.catalog_id,h.publication_order)
 WHERE(h.catalog_id,h.trade_id,h.option_instrument_id,h.revision)=(catalog,trade,binding,read_option_known_history.revision)
 AND h.publication_order<=prefix AND k.known_from<=cut AND h.effective_at<=as_of;
 IF r.trade_id IS NULL THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
 PERFORM lifecycle.require_option_origin(catalog,trade,r.revision,until_at);
 SELECT * INTO STRICT c FROM market_data.contracts t WHERE(t.catalog_id,t.revision_id,t.id)=(catalog,r.definition_revision_id,r.contract_id);
 SELECT * INTO STRICT s FROM ref_data.futures_option_series t WHERE(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_option_series_code);
 SELECT * INTO STRICT underlying FROM market_data.contracts t WHERE(t.catalog_id,t.revision_id,t.kind,t.futures_series_code)=(catalog,r.definition_revision_id,'future',s.underlying_series_code);
 IF NOT EXISTS(SELECT 1 FROM market_data.knowledge_events me JOIN market_data.market_knowledge_receipts mk
 ON(mk.catalog_id,mk.event_order,mk.publication_xid)=(me.catalog_id,me.event_order,me.publication_xid)
 JOIN market_data.revision_periods v ON(v.catalog_id,v.revision_id)=(me.catalog_id,me.revision_id)
 WHERE(me.catalog_id,me.revision_id)=(catalog,r.definition_revision_id) AND me.observation_id IS NULL
 AND me.event_order<=market_prefix AND mk.known_from<=cut AND v.effective_from<=business_date AND business_date<v.effective_to)
 THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
 -- Complete-source work bounds precede schedule/array construction. A large
 -- scope is refused as a whole; neither accepted events nor policy rows vanish.
 SELECT count(DISTINCT budget_event.id),coalesce(sum(budget_policy.occurrence_count),0) INTO event_count,occurrence_count
 FROM trading.futures_option_events budget_event LEFT JOIN settlement_data.futures_margin_adoptions budget_adoption
  ON(budget_adoption.catalog_id,budget_adoption.trade_id)=(budget_event.catalog_id,budget_event.futures_trade_id)
 LEFT JOIN settlement_data.futures_margin_policies budget_policy
  ON(budget_policy.catalog_id,budget_policy.id,budget_policy.revision)=(budget_adoption.catalog_id,budget_adoption.policy_id,budget_adoption.policy_revision)
 WHERE(budget_event.catalog_id,budget_event.trade_id)=(catalog,trade) AND budget_event.publication_order<=prefix;
 IF event_count>4096 OR occurrence_count>65536 THEN RAISE EXCEPTION 'lifecycle_option_source_capacity'
  USING DETAIL='known_futures_option_events_v1 permits at most 4096 events and 65536 repeated policy occurrences.';END IF;
 result=jsonb_build_object('stateProfile','known_futures_option_events_v1','contextId',context,'knownAt',cut,'asOf',as_of,'businessDate',business_date,
  'origin',to_jsonb(r),'referenceCatalogId',c.reference_catalog_id,'referenceRevisionId',c.reference_revision_id,'seriesCode',s.code);
 native_bytes=octet_length(result::text)+16;
 remaining=abs(r.lots);previous_at=r.execution_at;
 FOR e IN SELECT t.*,k.known_from known_at FROM trading.futures_option_events t
 LEFT JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(t.catalog_id,t.publication_order)
 WHERE(t.catalog_id,t.trade_id)=(catalog,trade) AND t.publication_order<=prefix ORDER BY t.publication_order,t.id LOOP
  IF e.known_at IS NULL OR e.known_at>cut OR e.trade_revision<>r.revision OR e.business_at<previous_at OR e.effective_at<>e.business_at
  THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
  IF e.kind IN('exercise','assignment','expiry') THEN remaining=remaining-e.lots;END IF;
  IF remaining<0 OR e.remaining_lots<>remaining OR e.kind='expiry' AND remaining<>0 THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
  previous_at=e.business_at;observation=NULL;linked=NULL;
  IF e.instruction_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.futures_option_events i JOIN trading.knowledge_receipts k
   ON(k.catalog_id,k.publication_order)=(i.catalog_id,i.publication_order) WHERE(i.catalog_id,i.trade_id,i.id)=(catalog,trade,e.instruction_id)
   AND i.kind IN('exercise_instruction','abandon_instruction') AND i.publication_order<e.publication_order AND k.known_from<=e.known_at)
  THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
  IF e.rule_code IS NOT NULL AND(security.reference_access(e.rule_catalog_id,'ReadMarketData') IS NOT TRUE
   OR security.reference_version_visible(e.rule_catalog_id,e.rule_revision_id) IS NOT TRUE) THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
  IF e.settlement_observation_id IS NOT NULL THEN
   SELECT o.*,ss.quote_code,ss.contract_id source_contract,mk.known_from source_known,me.event_order INTO source
   FROM market_data.observations o JOIN market_data.series ss ON(ss.catalog_id,ss.revision_id,ss.id)=(o.catalog_id,o.definition_revision_id,o.series_id)
   JOIN market_data.knowledge_events me ON(me.catalog_id,me.observation_id)=(o.catalog_id,o.id)
   JOIN market_data.market_knowledge_receipts mk ON(mk.catalog_id,mk.event_order,mk.publication_xid)=(me.catalog_id,me.event_order,me.publication_xid)
   WHERE(o.catalog_id,o.id)=(catalog,e.settlement_observation_id);
   IF source.id IS NULL OR(source.definition_revision_id,source.source_contract,source.role,source.publication_state,source.value) IS DISTINCT FROM
    (r.definition_revision_id,underlying.id,'daily_settlement','final',e.settlement_price) OR source.event_order>market_prefix OR source.source_known>least(cut,e.known_at)
   THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
   observation=to_jsonb(source);
  END IF;
  IF e.futures_trade_id IS NOT NULL THEN
   SELECT h.* INTO fr FROM trading.trade_revisions h JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(h.catalog_id,h.publication_order)
   WHERE(h.catalog_id,h.trade_id,h.revision,h.publication_order)=(catalog,e.futures_trade_id,1,e.publication_order) AND k.known_from=e.known_at;
   SELECT * INTO fm FROM trading.futures_revision_markets h WHERE(h.catalog_id,h.trade_id,h.revision,h.instrument_id)=(catalog,fr.trade_id,fr.revision,fr.instrument_id);
   SELECT * INTO a FROM settlement_data.futures_margin_adoptions d WHERE(d.catalog_id,d.trade_id,d.binding_id)=(catalog,fr.trade_id,fr.binding_id);
   SELECT k.known_from INTO known_at FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,a.publication_order);
   SELECT * INTO p FROM settlement_data.futures_margin_policies h WHERE(h.catalog_id,h.id,h.revision)=(catalog,a.policy_id,a.policy_revision);
   SELECT * INTO profile FROM lifecycle.option_profiles h WHERE(h.catalog_id,h.trade_id,h.action,h.revision)=
    (catalog,trade,e.request->>'action',(e.request#>>'{selection,profileRevision}')::integer);
   IF fr.trade_id IS NULL OR fm.contract_id IS NULL OR a.trade_id IS NULL OR p.id IS NULL OR profile.trade_id IS NULL
   OR(fr.book_id,fr.legal_entity_id,fr.legal_entity_revision,fr.counterparty_id,fr.counterparty_revision,fr.execution_at,fr.effective_at,fr.execution_price,fr.lots) IS DISTINCT FROM
    (r.book_id,r.legal_entity_id,r.legal_entity_revision,r.counterparty_id,r.counterparty_revision,e.business_at,e.effective_at,s.strike,e.lots*sign(r.lots)*CASE s.option_right WHEN 'call' THEN 1 ELSE -1 END)
   OR(fm.definition_revision_id,fm.contract_id,fm.curve_code,a.origin_revision,a.origin_publication,a.publication_order,a.effective_at,a.policy_id,a.policy_revision,a.source_context_id) IS DISTINCT FROM
    (r.definition_revision_id,underlying.id,profile.future_mark_curve,1,e.publication_order,e.publication_order,e.effective_at,profile.margin_policy_id,profile.margin_policy_revision,(e.request#>>'{context,contextId}')::uuid)
   OR known_at IS DISTINCT FROM e.known_at OR p.publication_order>e.publication_order
   OR NOT EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(catalog,p.publication_order) AND k.known_from<=e.known_at)
   THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
   PERFORM lifecycle.require_option_reference(catalog,trade,profile.action,profile.revision);
   SELECT jsonb_agg(to_jsonb(o) ORDER BY o.at,o.phase,o.id),jsonb_agg(jsonb_build_object('id',o.id,'at',o.at,'dueAt',o.due_at,'phase',o.phase))
    INTO occurrences,items FROM settlement_data.futures_margin_policy_occurrences o WHERE(o.catalog_id,o.policy_id,o.policy_revision)=(catalog,p.id,p.revision);
   IF coalesce(jsonb_array_length(occurrences),0)<>p.occurrence_count
    OR encode(sha256(convert_to(settlement_data.futures_margin_policy_value(p,items)::text,'UTF8')),'hex')<>p.terms_hash THEN RAISE EXCEPTION 'lifecycle_option_source';END IF;
   SELECT le.code legal_entity_code,le.name legal_entity_name,cp.code counterparty_code,cp.name counterparty_name INTO STRICT party
    FROM trading.legal_entities le CROSS JOIN trading.counterparties cp WHERE(le.catalog_id,le.id,le.revision,cp.catalog_id,cp.id,cp.revision)=
     (catalog,fr.legal_entity_id,fr.legal_entity_revision,catalog,fr.counterparty_id,fr.counterparty_revision);
   terms=to_jsonb(fr)||to_jsonb(fm)||to_jsonb(party)||jsonb_build_object('side_id',fm.market_binding_id,'effective_at',fr.execution_at);
   linked=jsonb_build_object('revision',terms,'adoption',to_jsonb(a),'known_at',known_at,'policy',to_jsonb(p),'occurrences',occurrences);
  END IF;
  event_value=(to_jsonb(e)-'request')||jsonb_build_object('settlement_source',observation,'settlement_known_at',observation->'source_known',
   'settlement_quote_code',observation->'quote_code','future',linked);
  native_bytes=native_bytes+octet_length(event_value::text)+2;
  IF native_bytes>16777216 THEN RAISE EXCEPTION 'lifecycle_option_source_capacity'
   USING DETAIL='known_futures_option_events_v1 permits at most 16 MiB of complete native source JSON.';END IF;
  events=events||jsonb_build_array(event_value);
 END LOOP;
 result=result||jsonb_build_object('events',events);
 PERFORM lifecycle.require_option_origin(catalog,trade,r.revision,until_at);
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,source_until);RETURN result;
END$$;
DO $functions$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='lifecycle' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$functions$;
GRANT EXECUTE ON FUNCTION lifecycle.save_option_profile(uuid,jsonb),lifecycle.option_proposal(uuid,jsonb),lifecycle.execute_option_action(uuid,jsonb),
 lifecycle.require_option_origin(uuid,uuid,integer,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION lifecycle.read_option_known_history(uuid,uuid,uuid,integer,uuid,timestamptz,date) TO openetrm_runtime;
COMMIT;
