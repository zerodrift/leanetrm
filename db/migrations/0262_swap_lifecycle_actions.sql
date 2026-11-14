-- Reusable execution settings bind exact native swap occurrences. Economics,
-- final inputs, rounding and accepted cash remain the existing swap operation.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
-- Command acknowledgements identify an already accepted financial fact. They
-- never create another financial sequence, cash membership or allocation.
ALTER TABLE settlement_data.swap_commands ADD COLUMN acknowledged_operation_id uuid,
 ADD COLUMN recorded_at timestamptz,
 ADD FOREIGN KEY(catalog_id,acknowledged_operation_id) REFERENCES settlement_data.swap_commands(catalog_id,operation_id),
 ADD CHECK(acknowledged_operation_id IS DISTINCT FROM operation_id),
 DROP CONSTRAINT swap_commands_catalog_id_trade_id_sequence_key;
-- Earlier financial records retain their original receipt knowledge time;
-- the migration does not invent a recording time for those rows.
ALTER TABLE settlement_data.swap_commands ALTER COLUMN recorded_at SET DEFAULT clock_timestamp();
CREATE UNIQUE INDEX swap_financial_sequence ON settlement_data.swap_commands(catalog_id,trade_id,sequence)
 WHERE acknowledged_operation_id IS NULL;
CREATE FUNCTION settlement_data.guard_swap_acknowledgement() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE accepted settlement_data.swap_commands;BEGIN
 IF NEW.acknowledged_operation_id IS NULL THEN RETURN NEW;END IF;
 SELECT * INTO accepted FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.operation_id)=(NEW.catalog_id,NEW.acknowledged_operation_id)
 AND c.acknowledged_operation_id IS NULL;
 IF accepted.operation_id IS NULL OR(NEW.trade_id,NEW.sequence,NEW.publication_order,NEW.effective_at) IS DISTINCT FROM
 (accepted.trade_id,accepted.sequence,accepted.publication_order,accepted.effective_at) OR
 (NEW.outcome-ARRAY['operationId','currentVersion','acknowledgedOperationId']) IS DISTINCT FROM(accepted.outcome-ARRAY['operationId','currentVersion','acknowledgedOperationId'])
 OR NEW.outcome->>'operationId' IS DISTINCT FROM NEW.operation_id::text OR NEW.outcome->>'acknowledgedOperationId' IS DISTINCT FROM accepted.operation_id::text
 THEN RAISE EXCEPTION 'settlement_acknowledgement';END IF;RETURN NEW;
END$$;
ALTER FUNCTION settlement_data.guard_swap_acknowledgement() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.guard_swap_acknowledgement() FROM PUBLIC;
CREATE TRIGGER valid_acknowledgement BEFORE INSERT ON settlement_data.swap_commands FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_swap_acknowledgement();
CREATE FUNCTION settlement_data.acknowledge_swap_command(catalog uuid,operation uuid,trade uuid,accepted_operation uuid,payload jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE accepted settlement_data.swap_commands;actor uuid;current_version integer;result jsonb;BEGIN
 PERFORM settlement_data.require_swap_authority(catalog,true);
 SELECT * INTO STRICT accepted FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.operation_id,c.trade_id)=(catalog,accepted_operation,trade)
 AND c.acknowledged_operation_id IS NULL;
 SELECT actor_id INTO actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 SELECT coalesce(max(c.sequence),0) INTO current_version FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade) AND c.acknowledged_operation_id IS NULL;
 result=accepted.outcome||jsonb_build_object('operationId',operation,'currentVersion',current_version,'acknowledgedOperationId',accepted.operation_id);
 INSERT INTO settlement_data.swap_commands(catalog_id,operation_id,trade_id,sequence,actor_id,request,outcome,publication_order,effective_at,acknowledged_operation_id)
 VALUES(catalog,operation,trade,accepted.sequence,actor,payload,result,accepted.publication_order,accepted.effective_at,accepted.operation_id);
 PERFORM settlement_data.require_swap_authority(catalog,true);PERFORM settlement_data.require_swap_reference(catalog,trade);RETURN result;
END$$;
ALTER FUNCTION settlement_data.acknowledge_swap_command(uuid,uuid,uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.acknowledge_swap_command(uuid,uuid,uuid,uuid,jsonb) FROM PUBLIC;

DO $snapshot_ack$
DECLARE f pg_proc;definition text;before text;after text;BEGIN
 SELECT p.* INTO STRICT f FROM pg_proc p WHERE p.oid='settlement_data.determine_swap(uuid,uuid,uuid,integer,integer,uuid,timestamptz,integer,date,date,timestamptz,uuid,uuid,text,integer,timestamptz,text,text,text,numeric,numeric,numeric,jsonb,jsonb,timestamptz)'::regprocedure;
 IF md5(f.prosrc) <>'94b72a3d36a69e72d0d6ddda9a47d5e1' OR NOT f.prosecdef OR f.proowner<>'openetrm_security_owner'::regrole
 OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[] OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') OR has_function_privilege('public',f.oid,'EXECUTE')
 THEN RAISE EXCEPTION 'swap_snapshot_upgrade';END IF;
 before=$before$ SELECT * INTO head FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;$before$;
 after=$after$ -- A selected older financial snapshot remains an existing fact after later
 -- corrections. Matching its complete native cash/source contract acknowledges
 -- that fact and leaves the current economic head untouched.
 IF expected_revision IS NULL OR expected_revision<1 OR expected_version IS NULL OR expected_version<0 THEN RAISE EXCEPTION 'settlement_input';END IF;
 SELECT d.* INTO prior FROM settlement_data.swap_occurrences o JOIN settlement_data.swap_determinations d
 ON(d.catalog_id,d.occurrence_id)=(o.catalog_id,o.id) JOIN settlement_data.swap_commands c
 ON(c.catalog_id,c.operation_id,c.trade_id)=(d.catalog_id,d.operation_id,d.trade_id) AND c.acknowledged_operation_id IS NULL
 WHERE(o.catalog_id,o.trade_id,o.membership_ordinal,o.delivery_from,o.delivery_to,o.contract_hash,o.reference_catalog_id,o.reference_revision_id,o.currency_code,o.decimal_places,o.contractual_due,o.completion_at,o.completion_policy)=
 (catalog,trade,membership,period_from,period_to,determine_swap.contract_hash,ref_catalog,ref_revision,currency,places,determine_swap.contractual_due,determine_swap.completion_at,determine_swap.completion_policy)
 AND(d.trade_revision,d.input_hash,d.rounded_total,d.exact_numerator,d.exact_denominator,d.effective_at,d.due_at)=
 (expected_revision,determine_swap.input_hash,total,numerator,denominator,as_of,coalesce(correction_due,determine_swap.contractual_due))
 AND(d.previous_id IS NULL)=(correction_due IS NULL) ORDER BY d.ordinal LIMIT 1;
 IF prior.id IS NOT NULL THEN RETURN settlement_data.acknowledge_swap_command(catalog,operation,trade,prior.operation_id,payload);END IF;
 SELECT * INTO head FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;$after$;
 IF(length(f.prosrc)-length(replace(f.prosrc,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'swap_snapshot_upgrade';END IF;
 definition=pg_get_functiondef(f.oid);EXECUTE replace(definition,before,after);
END $snapshot_ack$;
CREATE UNIQUE INDEX swap_observed_cash_reference ON settlement_data.swap_cash_payments
 (catalog_id,trade_id,btrim(reference),cash_at,coalesce(reverses_id,'00000000-0000-0000-0000-000000000000'::uuid));
DO $payment_ack$
DECLARE f pg_proc;definition text;before text;after text;BEGIN
 SELECT p.* INTO STRICT f FROM pg_proc p WHERE p.oid='settlement_data.apply_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamptz,jsonb,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb,text)'::regprocedure;
 IF md5(f.prosrc)<>'10ce31008a5c7675ac33db20dae7b97c' OR NOT f.prosecdef OR f.proowner<>'openetrm_security_owner'::regrole
 OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[] OR has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') OR has_function_privilege('public',f.oid,'EXECUTE')
 THEN RAISE EXCEPTION 'swap_payment_snapshot_upgrade';END IF;
 before=$before$ SELECT * INTO current_head FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;$before$;
 after=$after$ IF expected_revision IS NULL OR expected_revision<1 OR expected_version IS NULL OR expected_version<0 THEN RAISE EXCEPTION 'settlement_cash_input';END IF;
 -- The selected reference and cash instant identify this observed payment in
 -- its trade. A distinct reversal remains the existing explicit operation.
 SELECT * INTO original FROM settlement_data.swap_cash_payments p WHERE(p.catalog_id,p.trade_id,btrim(p.reference),p.cash_at)=
 (catalog,trade,btrim(apply_swap_cash.reference),apply_swap_cash.cash_at) AND p.reverses_id IS NOT DISTINCT FROM reverses;
 IF original.id IS NOT NULL THEN
  IF(original.amount,original.reference_catalog_id,original.reference_revision_id,original.currency_code) IS DISTINCT FROM(amount,ref_catalog,ref_revision,currency)
  OR EXISTS((SELECT a.determination_id,a.amount,a.allocation_policy FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(catalog,original.id)
    EXCEPT SELECT x.determination,x.amount,policy FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric))
   UNION ALL(SELECT x.determination,x.amount,policy FROM jsonb_to_recordset(lines)x(determination uuid,amount numeric)
    EXCEPT SELECT a.determination_id,a.amount,a.allocation_policy FROM settlement_data.swap_payment_allocations a WHERE(a.catalog_id,a.payment_id)=(catalog,original.id)))
  THEN RAISE EXCEPTION 'settlement_payment_evidence_conflict';END IF;
  RETURN settlement_data.acknowledge_swap_command(catalog,operation,trade,original.operation_id,payload);
 END IF;
 SELECT * INTO current_head FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;$after$;
 IF(length(f.prosrc)-length(replace(f.prosrc,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'swap_payment_snapshot_upgrade';END IF;
 definition=pg_get_functiondef(f.oid);EXECUTE replace(definition,before,after);
END $payment_ack$;
CREATE TABLE lifecycle.swap_profiles(
 catalog_id uuid NOT NULL,id uuid NOT NULL CHECK(id<>'00000000-0000-0000-0000-000000000000'::uuid),trade_id uuid NOT NULL,origin_revision integer NOT NULL,action text NOT NULL CHECK(action IN('swap.determine','swap.payment')),
 revision integer NOT NULL CHECK(revision>0),policy_id uuid NOT NULL,policy_revision integer NOT NULL,
 membership_ordinal integer NOT NULL CHECK(membership_ordinal IN(0,1)),payment_ordinal integer NOT NULL CHECK(payment_ordinal>=0),
 delivery_from date NOT NULL,delivery_to date NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 decimal_places integer NOT NULL CHECK(decimal_places BETWEEN 0 AND 12),rounding_policy text NOT NULL CHECK(rounding_policy='currency_minor_units_to_even_v1'),
 due_policy text NOT NULL CHECK(due_policy='swap_due_nominal_v1'),recognition_policy text NOT NULL CHECK(recognition_policy='swap_completed_period_trading_pnl_v1'),
 correction_policy text NOT NULL CHECK(correction_policy='swap_explicit_correction_due_v1'),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id,revision),CHECK(delivery_from<delivery_to),
 FOREIGN KEY(catalog_id,trade_id,origin_revision) REFERENCES trading.swap_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order));
ALTER TABLE lifecycle.swap_profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE lifecycle.swap_profiles FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_access ON lifecycle.swap_profiles TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY scoped_read ON lifecycle.swap_profiles FOR SELECT TO openetrm_runtime USING(
 security.trading_visible(catalog_id,publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00') AND
 security.reference_access(reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON lifecycle.swap_profiles FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
GRANT SELECT,INSERT ON lifecycle.swap_profiles TO openetrm_security_owner;
GRANT SELECT ON lifecycle.swap_profiles TO openetrm_runtime;
CREATE FUNCTION lifecycle.require_swap_reference(catalog uuid,trade uuid,action text,profile uuid,revision integer) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE configured lifecycle.swap_profiles;origin trading.swap_trade_revisions;terms record;shape record;definition uuid;BEGIN
 SELECT * INTO configured FROM lifecycle.swap_profiles t WHERE(t.catalog_id,t.trade_id,t.action,t.id,t.revision)=(catalog,trade,require_swap_reference.action,require_swap_reference.profile,require_swap_reference.revision);
 SELECT * INTO origin FROM trading.swap_trade_revisions t WHERE(t.catalog_id,t.trade_id,t.revision)=(catalog,trade,configured.origin_revision);
 IF configured.trade_id IS NULL OR origin.trade_id IS NULL
 OR security.reference_access(configured.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 OR security.reference_version_visible(configured.reference_catalog_id,configured.reference_revision_id) IS NOT TRUE
 THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_swap_metadata_references(catalog,origin.instrument_id);
 FOR definition IN
 SELECT s.definition_revision_id FROM trading.swap_instruments i JOIN trading.swap_floating_sides s
 ON s.catalog_id=i.catalog_id AND s.id IN(i.first_floating_side_id,i.floating_side_id) WHERE(i.catalog_id,i.instrument_id)=(catalog,origin.instrument_id)
 UNION SELECT s.definition_revision_id FROM trading.swap_instruments i JOIN trading.swap_side_periods s
 ON s.catalog_id=i.catalog_id AND s.side_id IN(i.scheduled_first_id,i.scheduled_second_id)
 WHERE(i.catalog_id,i.instrument_id)=(catalog,origin.instrument_id) AND s.definition_revision_id IS NOT NULL
 LOOP
  IF security.market_fact_visible(catalog,definition,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 END LOOP;
 SELECT * INTO terms FROM settlement_data.swap_payment_terms(catalog,origin.instrument_id,origin.quantity,configured.membership_ordinal,configured.delivery_from,configured.delivery_to);
 SELECT v.* INTO shape FROM(
  SELECT child.ordinal,child.reference_catalog_id,child.reference_revision_id,child.quote_code FROM trading.swap_side_periods child WHERE(child.catalog_id,child.id)=(catalog,terms.child)
  UNION ALL SELECT 0,child.reference_catalog_id,child.reference_revision_id,child.quote_code FROM trading.swap_fixed_sides child WHERE(child.catalog_id,child.id)=(catalog,terms.child)
  UNION ALL SELECT 0,child.reference_catalog_id,child.reference_revision_id,child.quote_code FROM trading.swap_floating_sides child WHERE(child.catalog_id,child.id)=(catalog,terms.child))v;
 IF terms.child IS NULL OR shape.ordinal IS DISTINCT FROM configured.payment_ordinal OR NOT EXISTS(
 SELECT 1 FROM ref_data.quote_specifications q JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,q.currency_code)
 WHERE(q.catalog_id,q.revision_id,q.code,c.code,c.display_minor_units)=(shape.reference_catalog_id,shape.reference_revision_id,shape.quote_code,configured.currency_code,configured.decimal_places)
 AND(q.catalog_id,q.revision_id)=(configured.reference_catalog_id,configured.reference_revision_id))
 THEN RAISE EXCEPTION 'lifecycle_swap_occurrence';END IF;
END$$;
CREATE FUNCTION lifecycle.read_swap_profile(catalog uuid,trade uuid,action text,profile uuid,revision integer,context uuid,as_of timestamptz,business_date date) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p lifecycle.swap_profiles;r trading.swap_trade_revisions;until_at timestamptz;prefix integer;cut timestamptz;terms record;BEGIN
 until_at=lifecycle.capture_authority(catalog,'AmendTrade');
 SELECT x.known_through,t.known_at INTO prefix,cut FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,business_date);
 SELECT t.* INTO p FROM lifecycle.swap_profiles t JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(t.catalog_id,t.publication_order)
 WHERE(t.catalog_id,t.trade_id,t.action,t.id,t.revision)=(catalog,trade,read_swap_profile.action,read_swap_profile.profile,read_swap_profile.revision)
 AND t.publication_order<=prefix AND k.known_from<=cut;
 SELECT t.* INTO r FROM trading.swap_trade_revisions t JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(t.catalog_id,t.publication_order)
 WHERE(t.catalog_id,t.trade_id)=(catalog,trade) AND t.publication_order<=prefix AND k.known_from<=cut AND t.effective_at<=as_of ORDER BY t.revision DESC LIMIT 1;
 IF p.id IS NULL OR r.revision IS DISTINCT FROM p.origin_revision THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 PERFORM lifecycle.require_swap_reference(catalog,trade,read_swap_profile.action,p.id,p.revision);
 SELECT * INTO terms FROM settlement_data.swap_payment_terms(catalog,r.instrument_id,r.quantity,p.membership_ordinal,p.delivery_from,p.delivery_to);
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);RETURN to_jsonb(p)||jsonb_build_object('binding_id',r.binding_id,'occurrence_id',terms.child);
END$$;
CREATE FUNCTION lifecycle.check_action_policy(catalog uuid,policy uuid,revision integer,action text,at timestamptz,automatic boolean)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p lifecycle.action_policies;until_at timestamptz;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);p=lifecycle.require_policy(catalog,policy,revision,action,at,automatic);
 IF automatic AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 PERFORM lifecycle.require_read(catalog,until_at);RETURN to_jsonb(p);
END$$;
CREATE FUNCTION lifecycle.swap_profile_occurrence(catalog uuid,profile uuid,revision integer) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE configured lifecycle.swap_profiles;origin trading.swap_trade_revisions;terms record;until_at timestamptz;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT * INTO configured FROM lifecycle.swap_profiles p WHERE(p.catalog_id,p.id,p.revision)=(catalog,profile,swap_profile_occurrence.revision)
 AND security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00');
 SELECT * INTO origin FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision)=(catalog,configured.trade_id,configured.origin_revision)
 AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at);
 IF configured.id IS NULL OR origin.trade_id IS NULL OR security.reference_access(configured.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 OR security.reference_version_visible(configured.reference_catalog_id,configured.reference_revision_id) IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 SELECT * INTO terms FROM settlement_data.swap_payment_terms(catalog,origin.instrument_id,origin.quantity,configured.membership_ordinal,configured.delivery_from,configured.delivery_to);
 IF terms.child IS NULL THEN RAISE EXCEPTION 'lifecycle_swap_occurrence';END IF;
 PERFORM lifecycle.require_read(catalog,until_at);RETURN terms.child;
END$$;
CREATE FUNCTION lifecycle.swap_native_request(body jsonb) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT CASE body->>'action' WHEN 'swap.determine' THEN body#>'{selection,determination}'||jsonb_build_object(
  'operationId',body->'operationId','previewHash',body->'previewHash')
 WHEN 'swap.payment' THEN body#>'{selection,payment}'||jsonb_build_object('operationId',body->'operationId','tradeId',body#>'{target,scopeId}',
  'context',jsonb_build_object('snapshotId',body#>'{selection,snapshotId}','convention','independent_side_financial_swap_v2','knowledgeMode','current','contextId',NULL)) END;
$$;
CREATE FUNCTION lifecycle.record_native_action(catalog uuid,body jsonb,outcome jsonb,until_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;action_code text;permission text;operation uuid;prior jsonb;p lifecycle.action_policies;native jsonb;native_request jsonb;native_ack uuid;BEGIN
 action_code=body->>'action';permission=CASE WHEN action_code IN('swap.determine','swap.payment','futures.margin') THEN 'AmendTrade' ELSE NULL END;
 IF permission IS NULL THEN RAISE EXCEPTION 'lifecycle_action_unsupported';END IF;
 operation=(body->>'operationId')::uuid;actor=lifecycle.require_authority(catalog,permission,until_at);
 prior=lifecycle.command_status(catalog,operation,action_code,body,permission,until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 p=lifecycle.require_policy(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,action_code,
  settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}'),(body->>'automatic')::boolean);
 IF action_code='futures.margin' THEN
  SELECT c.outcome INTO native FROM settlement_data.futures_margin_run_commands c WHERE(c.catalog_id,c.operation_id,c.actor_id,c.trade_id,c.binding_id)=
   (catalog,operation,actor,(body#>>'{target,scopeId}')::uuid,(body#>>'{target,bindingId}')::uuid);
 ELSE
  SELECT c.outcome,c.request,c.acknowledged_operation_id INTO native,native_request,native_ack FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.operation_id,c.actor_id,c.trade_id)=
   (catalog,operation,actor,(body#>>'{target,scopeId}')::uuid);
  IF native_request IS DISTINCT FROM lifecycle.swap_native_request(body) THEN RAISE EXCEPTION 'lifecycle_native_receipt';END IF;
  IF native_ack IS NULL AND(body#>>'{selection,profileRevision}')::integer IS DISTINCT FROM(
   SELECT max(s.revision) FROM lifecycle.swap_profiles s WHERE(s.catalog_id,s.id)=(catalog,(body#>>'{selection,profileId}')::uuid))
  THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
  PERFORM lifecycle.require_swap_reference(catalog,(body#>>'{target,scopeId}')::uuid,action_code,(body#>>'{selection,profileId}')::uuid,(body#>>'{selection,profileRevision}')::integer);
 END IF;
 IF native IS NULL OR native IS DISTINCT FROM outcome THEN RAISE EXCEPTION 'lifecycle_native_receipt';END IF;
 INSERT INTO lifecycle.commands VALUES(catalog,operation,actor,action_code,body,outcome,p.id,p.revision,until_at);
 PERFORM lifecycle.require_authority(catalog,permission,until_at);RETURN outcome;
END$$;
CREATE FUNCTION lifecycle.save_swap_profile(catalog uuid,body jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;actor uuid;operation uuid;prior jsonb;r trading.swap_trade_revisions;p lifecycle.action_policies;
 s lifecycle.swap_profiles;fact lifecycle.swap_profiles;expected integer;publication integer;terms record;shape record;result jsonb;BEGIN
 until_at=lifecycle.capture_action_authority(catalog,'swap_profile',body);operation=(body->>'operationId')::uuid;
 prior=lifecycle.command_status(catalog,operation,'swap_profile',body,'AmendTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);actor=lifecycle.require_authority(catalog,'AmendTrade',until_at);
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','profileId','tradeId','expectedRevision','originRevision','action','policyId','policyRevision',
 'membershipOrdinal','paymentOrdinal','deliveryFrom','deliveryTo','referenceCatalogId','referenceRevisionId','currency','decimalPlaces','roundingPolicy','duePolicy','recognitionPolicy','correctionPolicy'])
 OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[0-9]{1,8}$'
 OR body->>'deliveryFrom' IS NULL OR body->>'deliveryFrom'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
 OR body->>'deliveryTo' IS NULL OR body->>'deliveryTo'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','profileId','tradeId','expectedRevision','originRevision','action','policyId','policyRevision',
 'membershipOrdinal','paymentOrdinal','deliveryFrom','deliveryTo','referenceCatalogId','referenceRevisionId','currency','decimalPlaces','roundingPolicy','duePolicy','recognitionPolicy','correctionPolicy'))
 THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 SELECT * INTO r FROM trading.swap_trade_revisions t WHERE(t.catalog_id,t.trade_id)=(catalog,(body->>'tradeId')::uuid) ORDER BY t.revision DESC LIMIT 1;
 IF r.revision IS DISTINCT FROM(body->>'originRevision')::integer THEN RAISE EXCEPTION 'swap_revision_conflict';END IF;
 SELECT * INTO p FROM lifecycle.action_policies t WHERE(t.catalog_id,t.id,t.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer);
 IF p.action IS DISTINCT FROM body->>'action' THEN RAISE EXCEPTION 'lifecycle_action_policy';END IF;
 IF p.mode='automatic' AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO terms FROM settlement_data.swap_payment_terms(catalog,r.instrument_id,r.quantity,(body->>'membershipOrdinal')::integer,
  (body->>'deliveryFrom')::date,(body->>'deliveryTo')::date);
 SELECT v.* INTO shape FROM(
  SELECT child.ordinal,child.reference_catalog_id,child.reference_revision_id,child.quote_code FROM trading.swap_side_periods child WHERE(child.catalog_id,child.id)=(catalog,terms.child)
  UNION ALL SELECT 0,child.reference_catalog_id,child.reference_revision_id,child.quote_code FROM trading.swap_fixed_sides child WHERE(child.catalog_id,child.id)=(catalog,terms.child)
  UNION ALL SELECT 0,child.reference_catalog_id,child.reference_revision_id,child.quote_code FROM trading.swap_floating_sides child WHERE(child.catalog_id,child.id)=(catalog,terms.child))v;
 IF terms.child IS NULL OR shape.ordinal IS DISTINCT FROM(body->>'paymentOrdinal')::integer OR NOT EXISTS(
 SELECT 1 FROM ref_data.quote_specifications q WHERE(q.catalog_id,q.revision_id,q.code,q.currency_code)=(shape.reference_catalog_id,shape.reference_revision_id,shape.quote_code,body->>'currency')
 AND(q.catalog_id,q.revision_id)=((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid))
 THEN RAISE EXCEPTION 'lifecycle_swap_occurrence';END IF;
 s=jsonb_populate_record(NULL::lifecycle.swap_profiles,jsonb_build_object('catalog_id',catalog,'id',(body->>'profileId')::uuid,'trade_id',r.trade_id,'origin_revision',r.revision,
 'action',p.action,'policy_id',p.id,'policy_revision',p.revision,'membership_ordinal',(body->>'membershipOrdinal')::integer,
 'payment_ordinal',(body->>'paymentOrdinal')::integer,'delivery_from',body->>'deliveryFrom','delivery_to',body->>'deliveryTo',
 'reference_catalog_id',body->>'referenceCatalogId','reference_revision_id',body->>'referenceRevisionId','currency_code',body->>'currency',
 'decimal_places',(body->>'decimalPlaces')::integer,'rounding_policy',body->>'roundingPolicy','due_policy',body->>'duePolicy',
 'recognition_policy',body->>'recognitionPolicy','correction_policy',body->>'correctionPolicy'));
 IF NOT EXISTS(SELECT 1 FROM ref_data.currencies c WHERE(c.catalog_id,c.revision_id,c.code,c.display_minor_units)=
 (s.reference_catalog_id,s.reference_revision_id,s.currency_code,s.decimal_places)) OR security.reference_access(s.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'settlement_rounding';END IF;
 IF s.id IS NULL OR s.id='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 SELECT * INTO fact FROM lifecycle.swap_profiles t WHERE(to_jsonb(t)-ARRAY['id','revision','publication_order'])=(to_jsonb(s)-ARRAY['id','revision','publication_order']) ORDER BY t.revision LIMIT 1;
 IF fact.trade_id IS NULL THEN
  IF EXISTS(SELECT 1 FROM lifecycle.swap_profiles t WHERE(t.catalog_id,t.id)=(catalog,s.id) AND
   (t.trade_id,t.action,t.membership_ordinal,t.payment_ordinal,t.delivery_from,t.delivery_to) IS DISTINCT FROM
   (s.trade_id,s.action,s.membership_ordinal,s.payment_ordinal,s.delivery_from,s.delivery_to)) THEN RAISE EXCEPTION 'lifecycle_profile_identity';END IF;
  IF EXISTS(SELECT 1 FROM lifecycle.swap_profiles t WHERE(t.catalog_id,t.trade_id,t.action,t.membership_ordinal,t.payment_ordinal,t.delivery_from,t.delivery_to)=
   (catalog,s.trade_id,s.action,s.membership_ordinal,s.payment_ordinal,s.delivery_from,s.delivery_to) AND t.id<>s.id) THEN RAISE EXCEPTION 'lifecycle_profile_conflict';END IF;
 expected=(body->>'expectedRevision')::integer;
  IF expected IS NULL OR expected<0 OR coalesce((SELECT max(t.revision) FROM lifecycle.swap_profiles t WHERE(t.catalog_id,t.id)=(catalog,s.id)),0)<>expected THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
  SELECT coalesce(max(t.publication_order),0)+1 INTO publication FROM trading.publications t WHERE t.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());s.publication_order=publication;s.revision=expected+1;
  INSERT INTO lifecycle.swap_profiles SELECT(s).*;fact=s;
 END IF;
 s=fact;
 result=jsonb_build_object('operationId',operation,'tradeId',r.trade_id,'profileId',s.id,'revision',s.revision,'action',s.action);
 INSERT INTO lifecycle.commands VALUES(catalog,operation,actor,'swap_profile',body,result,p.id,p.revision,until_at);
 PERFORM lifecycle.require_swap_reference(catalog,r.trade_id,s.action,s.id,s.revision);
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);RETURN result;
END$$;
DO $profile_scope$
DECLARE f pg_proc;definition text;before text;after text;BEGIN
 SELECT * INTO STRICT f FROM pg_proc WHERE oid='lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz)'::regprocedure;
 IF md5(f.prosrc)<>'6aa1b632af4225a4d9dda8c864608686' OR NOT f.prosecdef OR f.proowner<>'openetrm_security_owner'::regrole
 OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[] OR has_function_privilege('public',f.oid,'EXECUTE')
 OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') THEN RAISE EXCEPTION 'swap_profile_scope_upgrade';END IF;
 definition=pg_get_functiondef(f.oid);
 before=$old$lifecycle.require_swap_reference(catalog,(body->>'tradeId')::uuid,body->>'action',(c.outcome->>'revision')::integer)$old$;
 after=$new$lifecycle.require_swap_reference(catalog,(body->>'tradeId')::uuid,body->>'action',(c.outcome->>'profileId')::uuid,(c.outcome->>'revision')::integer)$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'swap_profile_scope_upgrade';END IF;
 definition=replace(definition,before,after);
 before=$old$lifecycle.require_swap_reference(catalog,(body#>>'{target,scopeId}')::uuid,kind,(body#>>'{selection,profileRevision}')::integer)$old$;
 after=$new$lifecycle.require_swap_reference(catalog,(body#>>'{target,scopeId}')::uuid,kind,(body#>>'{selection,profileId}')::uuid,(body#>>'{selection,profileRevision}')::integer)$new$;
 IF(length(definition)-length(replace(definition,before,'')))/length(before)<>1 THEN RAISE EXCEPTION 'swap_profile_scope_upgrade';END IF;
 EXECUTE replace(definition,before,after);
END $profile_scope$;
CREATE FUNCTION lifecycle.require_swap_action(catalog uuid,body jsonb,action text,until_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE info jsonb;selected lifecycle.action_policies;source_until timestamptz;at timestamptz;context uuid;business_date date;BEGIN
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','context','target','action','policyId','policyRevision','automatic','previewHash','selection'))
 OR body->>'action' IS DISTINCT FROM action OR body#>>'{target,handler}' IS DISTINCT FROM 'financial_swap'
 OR jsonb_typeof(body->'automatic') IS DISTINCT FROM 'boolean' OR body->>'previewHash' IS NULL OR body->>'previewHash'!~'^[0-9a-f]{64}$'
 OR jsonb_typeof(body->'selection') IS DISTINCT FROM 'object'
 OR NOT((body->'selection')?&ARRAY['snapshotId','profileId','profileRevision','determination','payment'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body->'selection')k WHERE k NOT IN('snapshotId','profileId','profileRevision','determination','payment'))
 OR jsonb_typeof(body#>'{selection,snapshotId}') IS DISTINCT FROM 'string' OR jsonb_typeof(body#>'{selection,profileId}') IS DISTINCT FROM 'string'
 OR jsonb_typeof(body#>'{selection,profileRevision}') IS DISTINCT FROM 'number' OR body#>>'{selection,profileRevision}'!~'^[1-9][0-9]{0,7}$'
 THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 at=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');context=(body#>>'{context,contextId}')::uuid;business_date=(body#>>'{context,businessDate}')::date;
 SELECT q.business_source_until INTO source_until FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
 AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user
 AND(q.business_source_catalog_id,q.business_source_context_id,q.business_source_as_of,q.business_source_on)=(catalog,context,at,business_date);
 IF source_until IS NULL THEN RAISE EXCEPTION 'trading_source_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_admitted_business_source(catalog,context,at,business_date,source_until);
 info=lifecycle.read_swap_profile(catalog,(body#>>'{target,scopeId}')::uuid,action,(body#>>'{selection,profileId}')::uuid,
  (body#>>'{selection,profileRevision}')::integer,context,at,business_date);
 IF(info->>'binding_id')::uuid IS DISTINCT FROM(body#>>'{target,bindingId}')::uuid OR
 (info->>'policy_id')::uuid IS DISTINCT FROM(body->>'policyId')::uuid OR(info->>'policy_revision')::integer IS DISTINCT FROM(body->>'policyRevision')::integer
 THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 selected=lifecycle.require_policy(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer,action,at,(body->>'automatic')::boolean);
 PERFORM lifecycle.require_authority(catalog,'AmendTrade',until_at);RETURN info;
END$$;
CREATE FUNCTION lifecycle.determine_swap_action(catalog uuid,body jsonb,original_until timestamptz,calculation jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;prior jsonb;info jsonb;payload jsonb;request jsonb;result jsonb;at timestamptz;BEGIN
 until_at=least(original_until,lifecycle.capture_action_authority(catalog,'swap.determine',body));
 prior=lifecycle.command_status(catalog,(body->>'operationId')::uuid,'swap.determine',body,'AmendTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);info=lifecycle.require_swap_action(catalog,body,'swap.determine',until_at);
 request=body#>'{selection,determination}';payload=lifecycle.swap_native_request(body);at=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');
 IF jsonb_typeof(request) IS DISTINCT FROM 'object' OR NOT(request?&ARRAY['preview','expectedVersion','duePolicy','recognitionPolicy','correctionPolicy','correctionDueAt'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(request)k WHERE k NOT IN('preview','expectedVersion','duePolicy','recognitionPolicy','correctionPolicy','correctionDueAt'))
 OR jsonb_typeof(request->'preview') IS DISTINCT FROM 'object' OR NOT((request->'preview')?&ARRAY['context','tradeId','expectedRevision','membershipOrdinal','paymentOrdinal','currency','decimalPlaces','roundingPolicy'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(request->'preview')k WHERE k NOT IN('context','tradeId','expectedRevision','membershipOrdinal','paymentOrdinal','currency','decimalPlaces','roundingPolicy'))
 OR jsonb_typeof(body#>'{selection,payment}') IS DISTINCT FROM 'null'
 OR jsonb_typeof(request->'expectedVersion') IS DISTINCT FROM 'number' OR request->>'expectedVersion'!~'^[0-9]{1,8}$'
 OR request#>>'{preview,context,snapshotId}' IS DISTINCT FROM body#>>'{selection,snapshotId}'
 OR request#>>'{preview,context,convention}' IS DISTINCT FROM 'independent_side_financial_swap_v2'
 OR request#>>'{preview,context,knowledgeMode}' IS DISTINCT FROM 'current' OR jsonb_typeof(request#>'{preview,context,contextId}') IS DISTINCT FROM 'null'
 OR request#>>'{preview,tradeId}' IS DISTINCT FROM body#>>'{target,scopeId}'
 OR(request#>>'{preview,expectedRevision}')::integer IS DISTINCT FROM(info->>'origin_revision')::integer
 OR(request#>>'{preview,membershipOrdinal}')::integer IS DISTINCT FROM(info->>'membership_ordinal')::integer
 OR(request#>>'{preview,paymentOrdinal}')::integer IS DISTINCT FROM(info->>'payment_ordinal')::integer
 OR request#>>'{preview,roundingPolicy}' IS DISTINCT FROM info->>'rounding_policy'
 OR(request#>>'{preview,decimalPlaces}')::integer IS DISTINCT FROM(info->>'decimal_places')::integer
 OR request->>'duePolicy' IS DISTINCT FROM info->>'due_policy' OR request->>'recognitionPolicy' IS DISTINCT FROM info->>'recognition_policy'
 OR request->>'correctionPolicy' IS DISTINCT FROM info->>'correction_policy'
 OR(request#>>'{preview,currency,revision,catalogId}',request#>>'{preview,currency,revision,revisionId}',request#>>'{preview,currency,code}')
 IS DISTINCT FROM(info->>'reference_catalog_id',info->>'reference_revision_id',info->>'currency_code')
 OR jsonb_typeof(calculation) IS DISTINCT FROM 'object' OR NOT(calculation?&ARRAY['contextId','contractualDue','completionAt','completionPolicy','contractHash','inputHash','total','numerator','denominator','evidence'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(calculation)k WHERE k NOT IN('contextId','contractualDue','completionAt','completionPolicy','contractHash','inputHash','total','numerator','denominator','evidence'))
 OR calculation->>'contextId' IS DISTINCT FROM body#>>'{context,contextId}'
 THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 result=settlement_data.determine_swap(catalog,(body->>'operationId')::uuid,(body#>>'{target,scopeId}')::uuid,(info->>'origin_revision')::integer,(request->>'expectedVersion')::integer,
  (body#>>'{context,contextId}')::uuid,at,(info->>'membership_ordinal')::integer,(info->>'delivery_from')::date,(info->>'delivery_to')::date,
  settlement_data.futures_margin_utc_instant(calculation->>'contractualDue'),(info->>'reference_catalog_id')::uuid,(info->>'reference_revision_id')::uuid,info->>'currency_code',(info->>'decimal_places')::integer,
  settlement_data.futures_margin_utc_instant(calculation->>'completionAt'),calculation->>'completionPolicy',calculation->>'contractHash',calculation->>'inputHash',
  (calculation->>'total')::numeric,(calculation->>'numerator')::numeric,(calculation->>'denominator')::numeric,calculation->'evidence',payload,
  CASE WHEN request->>'correctionDueAt' IS NULL THEN NULL ELSE settlement_data.futures_margin_utc_instant(request->>'correctionDueAt') END);
 RETURN lifecycle.record_native_action(catalog,body,result,until_at);
END$$;
CREATE FUNCTION lifecycle.record_swap_payment_action(catalog uuid,body jsonb,original_until timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;prior jsonb;info jsonb;request jsonb;result jsonb;at timestamptz;BEGIN
 until_at=least(original_until,lifecycle.capture_action_authority(catalog,'swap.payment',body));
 prior=lifecycle.command_status(catalog,(body->>'operationId')::uuid,'swap.payment',body,'AmendTrade',until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);info=lifecycle.require_swap_action(catalog,body,'swap.payment',until_at);
 request=body#>'{selection,payment}';at=settlement_data.futures_margin_utc_instant(body#>>'{context,asOf}');
 IF jsonb_typeof(request) IS DISTINCT FROM 'object' OR NOT(request?&ARRAY['expectedRevision','expectedVersion','determinationId','amount','currency','effectiveAt','reference','reversesId'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(request)k WHERE k NOT IN('expectedRevision','expectedVersion','determinationId','amount','currency','effectiveAt','reference','reversesId'))
 OR jsonb_typeof(body#>'{selection,determination}') IS DISTINCT FROM 'null'
 OR jsonb_typeof(request->'expectedVersion') IS DISTINCT FROM 'number' OR request->>'expectedVersion'!~'^[0-9]{1,8}$'
 OR(request->>'expectedRevision')::integer IS DISTINCT FROM(info->>'origin_revision')::integer
 OR(request#>>'{currency,revision,catalogId}',request#>>'{currency,revision,revisionId}',request#>>'{currency,code}')
 IS DISTINCT FROM(info->>'reference_catalog_id',info->>'reference_revision_id',info->>'currency_code')
 OR NOT EXISTS(SELECT 1 FROM settlement_data.swap_determinations d JOIN settlement_data.swap_occurrences o ON(o.catalog_id,o.id,o.trade_id)=(d.catalog_id,d.occurrence_id,d.trade_id)
  WHERE(d.catalog_id,d.id,d.trade_id,o.membership_ordinal,o.delivery_from,o.delivery_to)=(catalog,(request->>'determinationId')::uuid,(body#>>'{target,scopeId}')::uuid,
   (info->>'membership_ordinal')::integer,(info->>'delivery_from')::date,(info->>'delivery_to')::date))
 THEN RAISE EXCEPTION 'lifecycle_profile_revision';END IF;
 result=settlement_data.record_swap_cash(catalog,(body->>'operationId')::uuid,(body#>>'{target,scopeId}')::uuid,(info->>'origin_revision')::integer,(request->>'expectedVersion')::integer,
  (body#>>'{context,contextId}')::uuid,at,(request->>'determinationId')::uuid,(request->>'amount')::numeric,(info->>'reference_catalog_id')::uuid,(info->>'reference_revision_id')::uuid,
  info->>'currency_code',settlement_data.futures_margin_utc_instant(request->>'effectiveAt'),request->>'reference',(request->>'reversesId')::uuid,lifecycle.swap_native_request(body));
 RETURN lifecycle.record_native_action(catalog,body,result,until_at);
END$$;
REVOKE ALL ON FUNCTION settlement_data.determine_swap(uuid,uuid,uuid,integer,integer,uuid,timestamptz,integer,date,date,timestamptz,uuid,uuid,text,integer,timestamptz,text,text,text,numeric,numeric,numeric,jsonb,jsonb,timestamptz),
 settlement_data.record_swap_cash(uuid,uuid,uuid,integer,integer,uuid,timestamptz,uuid,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb),
 settlement_data.record_swap_net(uuid,uuid,uuid,integer,integer,uuid,timestamptz,jsonb,numeric,uuid,uuid,text,timestamptz,text,uuid,jsonb) FROM openetrm_runtime;
-- A knowledge-only source for economic horizon projection. Economic time is
-- applied by the product projection at each event, never by dropping known
-- future-effective payments from the source capture.
CREATE FUNCTION lifecycle.require_swap_history_occurrence(catalog uuid,head trading.swap_trade_revisions,occ settlement_data.swap_occurrences,prefix integer,cut timestamptz)
 RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE origin trading.swap_trade_revisions;old_terms record;held_terms record;BEGIN
 SELECT r.* INTO origin FROM trading.swap_trade_revisions r JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
 WHERE(r.catalog_id,r.trade_id,r.revision,r.binding_id)=(catalog,head.trade_id,occ.origin_revision,occ.origin_binding_id) AND r.publication_order<=prefix AND k.known_from<=cut;
 IF origin.trade_id IS NULL OR(origin.book_id,origin.legal_entity_id,origin.legal_entity_revision,origin.counterparty_id,origin.counterparty_revision) IS DISTINCT FROM
  (head.book_id,head.legal_entity_id,head.legal_entity_revision,head.counterparty_id,head.counterparty_revision) THEN RAISE EXCEPTION 'lifecycle_swap_continuity';END IF;
 IF security.reference_access(occ.reference_catalog_id,'ReadMarketData') IS NOT TRUE OR security.reference_version_visible(occ.reference_catalog_id,occ.reference_revision_id) IS NOT TRUE
 THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO old_terms FROM settlement_data.swap_payment_terms(catalog,origin.instrument_id,origin.quantity,occ.membership_ordinal,occ.delivery_from,occ.delivery_to);
 SELECT * INTO held_terms FROM settlement_data.swap_payment_terms(catalog,head.instrument_id,head.quantity,occ.membership_ordinal,occ.delivery_from,occ.delivery_to);
 IF old_terms.child IS NULL OR held_terms.child IS NULL OR held_terms.coefficient<>old_terms.coefficient
  OR held_terms.numerator*old_terms.denominator<>old_terms.numerator*held_terms.denominator
  OR held_terms.child<>old_terms.child AND(settlement_data.swap_owned_period_identity(catalog,old_terms.child) IS NULL
   OR settlement_data.swap_owned_period_identity(catalog,held_terms.child) IS DISTINCT FROM settlement_data.swap_owned_period_identity(catalog,old_terms.child))
 THEN RAISE EXCEPTION 'lifecycle_swap_continuity';END IF;
END$$;
CREATE FUNCTION lifecycle.read_swap_payment_history(catalog uuid,trade uuid,binding uuid,revision integer,context uuid,as_of timestamptz,business_date date)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;prefix integer;cut timestamptz;head trading.swap_trade_revisions;payment record;allocation record;
 occurrence settlement_data.swap_occurrences;determination record;determinations jsonb='[]';payments jsonb='[]';allocations jsonb;total numeric;result jsonb;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT x.known_through,t.known_at INTO prefix,cut FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,business_date);
 IF prefix IS NULL OR as_of IS NULL OR NOT isfinite(as_of) THEN RAISE EXCEPTION 'lifecycle_context';END IF;
 SELECT r.* INTO head FROM trading.swap_trade_revisions r JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
 WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND r.publication_order<=prefix AND k.known_from<=cut AND r.effective_at<=as_of ORDER BY r.revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM revision OR head.binding_id IS DISTINCT FROM binding THEN RAISE EXCEPTION 'lifecycle_swap_source';END IF;
 PERFORM security.require_swap_metadata_references(catalog,head.instrument_id);
 FOR determination IN SELECT d.*,k.known_from FROM settlement_data.swap_determinations d JOIN trading.knowledge_receipts k
 ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
 WHERE(d.catalog_id,d.trade_id)=(catalog,trade) AND d.publication_order<=prefix AND k.known_from<=cut ORDER BY d.publication_order,d.id LOOP
  SELECT o.* INTO occurrence FROM settlement_data.swap_occurrences o JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(o.catalog_id,o.publication_order)
  WHERE(o.catalog_id,o.id,o.trade_id)=(catalog,determination.occurrence_id,trade) AND o.publication_order<=prefix AND k.known_from<=cut;
  IF occurrence.id IS NULL OR determination.previous_id IS NOT NULL AND NOT EXISTS(
   SELECT 1 FROM settlement_data.swap_determinations p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE(p.catalog_id,p.id,p.occurrence_id,p.ordinal)=(catalog,determination.previous_id,determination.occurrence_id,determination.ordinal-1)
   AND p.publication_order<=prefix AND k.known_from<=cut) THEN RAISE EXCEPTION 'lifecycle_swap_source';END IF;
  PERFORM lifecycle.require_swap_history_occurrence(catalog,head,occurrence,prefix,cut);
  determinations=determinations||jsonb_build_array(jsonb_build_object('contributionId',determination.id,'previousId',determination.previous_id,
   'membershipOrdinal',occurrence.membership_ordinal,'deliveryFrom',occurrence.delivery_from,'deliveryTo',occurrence.delivery_to,
   'effectiveAt',determination.effective_at,'dueAt',determination.due_at,'knownAt',determination.known_from,'publicationOrder',determination.publication_order,
   'roundedTotal',trim_scale(determination.rounded_total)::text,'contribution',trim_scale(determination.contribution)::text,
   'referenceCatalogId',occurrence.reference_catalog_id,'referenceRevisionId',occurrence.reference_revision_id,'currency',occurrence.currency_code));
 END LOOP;
 FOR payment IN SELECT p.*,k.known_from FROM settlement_data.swap_cash_payments p JOIN trading.knowledge_receipts k
 ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
 WHERE(p.catalog_id,p.trade_id)=(catalog,trade) AND p.publication_order<=prefix AND k.known_from<=cut ORDER BY p.cash_at,p.publication_order,p.id LOOP
  IF security.reference_access(payment.reference_catalog_id,'ReadMarketData') IS NOT TRUE
   OR security.reference_version_visible(payment.reference_catalog_id,payment.reference_revision_id) IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
  allocations='[]';total=0;
  FOR allocation IN SELECT a.*,d.occurrence_id,d.trade_revision,d.publication_order determination_publication,d.effective_at determination_effective,
   dk.known_from determination_known,o.origin_revision,o.origin_binding_id,o.membership_ordinal,o.delivery_from,o.delivery_to,
   o.reference_catalog_id,o.reference_revision_id,o.currency_code,ak.known_from allocation_known,ok.known_from occurrence_known,
   o.publication_order occurrence_publication
   FROM settlement_data.swap_payment_allocations a JOIN settlement_data.swap_determinations d ON(d.catalog_id,d.id,d.trade_id)=(a.catalog_id,a.determination_id,a.trade_id)
   JOIN settlement_data.swap_occurrences o ON(o.catalog_id,o.id,o.trade_id)=(d.catalog_id,d.occurrence_id,d.trade_id)
   LEFT JOIN trading.knowledge_receipts dk ON(dk.catalog_id,dk.publication_order)=(d.catalog_id,d.publication_order)
   LEFT JOIN trading.knowledge_receipts ak ON(ak.catalog_id,ak.publication_order)=(a.catalog_id,a.publication_order)
   LEFT JOIN trading.knowledge_receipts ok ON(ok.catalog_id,ok.publication_order)=(o.catalog_id,o.publication_order)
   WHERE(a.catalog_id,a.payment_id,a.trade_id)=(catalog,payment.id,trade) ORDER BY a.determination_id LOOP
   IF allocation.determination_known IS NULL OR allocation.allocation_known IS NULL OR allocation.occurrence_known IS NULL
    OR greatest(allocation.determination_publication,allocation.publication_order,allocation.occurrence_publication)>prefix
    OR greatest(allocation.determination_known,allocation.allocation_known,allocation.occurrence_known)>cut
    OR(allocation.reference_catalog_id,allocation.reference_revision_id,allocation.currency_code) IS DISTINCT FROM
      (payment.reference_catalog_id,payment.reference_revision_id,payment.currency_code)
   THEN RAISE EXCEPTION 'lifecycle_swap_source';END IF;
   SELECT * INTO STRICT occurrence FROM settlement_data.swap_occurrences o WHERE(o.catalog_id,o.id)=(catalog,allocation.occurrence_id);
   PERFORM lifecycle.require_swap_history_occurrence(catalog,head,occurrence,prefix,cut);
   allocations=allocations||jsonb_build_array(jsonb_build_object('contributionId',allocation.determination_id,'membershipOrdinal',allocation.membership_ordinal,
    'deliveryFrom',allocation.delivery_from,'deliveryTo',allocation.delivery_to,'amount',trim_scale(allocation.amount)::text,
    'determinationEffectiveAt',allocation.determination_effective,'determinationKnownAt',allocation.determination_known,
    'determinationPublicationOrder',allocation.determination_publication));total=total+allocation.amount;
  END LOOP;
  IF jsonb_array_length(allocations)=0 OR total<>payment.amount THEN RAISE EXCEPTION 'lifecycle_swap_source';END IF;
  payments=payments||jsonb_build_array(jsonb_build_object('paymentId',payment.id,'reversesId',payment.reverses_id,'effectiveAt',payment.cash_at,
   'knownAt',payment.known_from,'publicationOrder',payment.publication_order,'amount',trim_scale(payment.amount)::text,
   'referenceCatalogId',payment.reference_catalog_id,'referenceRevisionId',payment.reference_revision_id,'currency',payment.currency_code,'allocations',allocations));
 END LOOP;
 result=jsonb_build_object('contextId',context,'knownAt',cut,'asOf',as_of,'businessDate',business_date,'tradeId',trade,'bindingId',binding,
  'revision',head.revision,'bookId',head.book_id,'authorityUntil',until_at,'payments',payments,'determinations',determinations);
 PERFORM lifecycle.require_read(catalog,until_at);RETURN result;
END$$;
DO $functions$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='lifecycle' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$functions$;
GRANT EXECUTE ON FUNCTION lifecycle.check_action_policy(uuid,uuid,integer,text,timestamptz,boolean),lifecycle.read_swap_profile(uuid,uuid,text,uuid,integer,uuid,timestamptz,date),lifecycle.record_native_action(uuid,jsonb,jsonb,timestamptz),
 lifecycle.save_swap_profile(uuid,jsonb),lifecycle.read_swap_payment_history(uuid,uuid,uuid,integer,uuid,timestamptz,date),lifecycle.swap_profile_occurrence(uuid,uuid,integer),
 lifecycle.determine_swap_action(uuid,jsonb,timestamptz,jsonb),lifecycle.record_swap_payment_action(uuid,jsonb,timestamptz) TO openetrm_runtime;
COMMIT;
