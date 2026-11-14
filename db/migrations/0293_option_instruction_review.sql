-- Consumers: source-visible instruction review and acknowledgement of the
-- existing immutable instruction, including a newly supplied evidence key.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$DECLARE item record;f pg_proc;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('lifecycle.option_proposal_base(uuid,jsonb)','5fd45a523df98bc3ca3b9dd199372719'),
  ('lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb)','d677f7673dadc5a223e1cbaec4989d58')
 )v(signature,body_hash) LOOP
  SELECT * INTO STRICT f FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(f.prosrc)<>item.body_hash OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef
   OR f.prokind<>'f' OR f.proretset OR f.prorettype<>'jsonb'::regtype OR f.provariadic<>0
   OR f.proargmodes IS NOT NULL OR f.proallargtypes IS NOT NULL OR f.proargnames IS DISTINCT FROM ARRAY['catalog','body']::text[]
   OR f.pronargdefaults<>0 OR f.proargdefaults IS NOT NULL OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
   OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
   OR NOT has_function_privilege('openetrm_security_owner',f.oid,'EXECUTE')
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))a WHERE a.grantor<>f.proowner OR a.is_grantable
    OR a.privilege_type<>'EXECUTE' OR a.grantee<>f.proowner)
  THEN RAISE EXCEPTION 'option_instruction_review_upgrade: unexpected helper %',item.signature;END IF;
 END LOOP;
END$upgrade$;
DO $patch$DECLARE definition text;before text;after text;BEGIN
 definition=pg_get_functiondef('lifecycle.option_proposal_base(uuid,jsonb)'::regprocedure);
 before=$old$ IF open_lots<=0 THEN RAISE EXCEPTION 'option_holding_closed';END IF;$old$;
 after=$new$ IF open_lots<=0 THEN RAISE EXCEPTION 'option_holding_closed';END IF;
 IF kind IN('exercise_instruction','abandon_instruction') AND EXISTS(
  SELECT 1 FROM trading.futures_option_events e JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(e.catalog_id,e.publication_order)
  WHERE(e.catalog_id,e.trade_id)=(catalog,r.trade_id) AND e.kind IN('exercise_instruction','abandon_instruction')
  AND(e.kind<>replace(action_code,'option.','') OR e.business_at<>at OR e.effective_at<>at)
  AND e.publication_order<=prefix AND k.known_from<=cut AND e.effective_at<=at)
 THEN RAISE EXCEPTION 'option_instruction_conflict';END IF;$new$;
 IF length(definition)-length(replace(definition,before,''))<>length(before)
 THEN RAISE EXCEPTION 'option_instruction_review_upgrade: unexpected proposal body';END IF;
 EXECUTE replace(definition,before,after);

 definition=pg_get_functiondef('lifecycle.execute_option_action_pre_schedule_scope(uuid,jsonb)'::regprocedure);
 before=$old$  SELECT * INTO STRICT e FROM trading.futures_option_events t WHERE(t.catalog_id,t.request_id)=(catalog,operation);$old$;
 after=$new$  SELECT * INTO STRICT e FROM trading.futures_option_events t
   WHERE(t.catalog_id,t.id,t.trade_id,t.kind)=(catalog,(result->>'eventId')::uuid,profile.trade_id,replace(action_code,'option.',''));
  IF e.request_id<>operation THEN
   IF e.kind NOT IN('exercise_instruction','abandon_instruction') OR e.lots IS NOT NULL OR e.futures_trade_id IS NOT NULL
    OR(e.business_at,e.effective_at) IS DISTINCT FROM
     (settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}'),settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}'))
   THEN RAISE EXCEPTION 'lifecycle_option_receipt';END IF;
   result=result||jsonb_build_object('operationId',operation,'acknowledgedOperationId',e.request_id,'semanticAcknowledgement',true);
  END IF;$new$;
 IF length(definition)-length(replace(definition,before,''))<>length(before)
 THEN RAISE EXCEPTION 'option_instruction_review_upgrade: unexpected execution body';END IF;
 EXECUTE replace(definition,before,after);
END$patch$;
COMMIT;
