-- Current consumers: reviewed option exercise/expiry and financial-swap actions.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
CREATE SCHEMA lifecycle;
GRANT USAGE ON SCHEMA lifecycle TO openetrm_runtime,openetrm_security_owner;
CREATE TABLE lifecycle.action_kinds(code text PRIMARY KEY,automatic boolean NOT NULL,
 configuration_permission text NOT NULL REFERENCES security.permissions(code),execution_permission text NOT NULL REFERENCES security.permissions(code));
INSERT INTO lifecycle.action_kinds VALUES('option.exercise',false,'AmendTrade','BookTrade'),('option.assignment',false,'AmendTrade','BookTrade'),('option.expiry',true,'AmendTrade','BookTrade'),
 ('option.exercise_instruction',false,'AmendTrade','BookTrade'),('option.abandon_instruction',false,'AmendTrade','BookTrade'),
 ('swap.determine',true,'AmendTrade','AmendTrade'),('swap.payment',false,'AmendTrade','AmendTrade'),('futures.margin',true,'AmendTrade','AmendTrade');
CREATE TABLE lifecycle.action_policies(
 catalog_id uuid NOT NULL,id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),action text NOT NULL REFERENCES lifecycle.action_kinds(code),
 mode text NOT NULL CHECK(mode IN('manual','review','automatic')),from_at ref_data.instant NOT NULL,until_at ref_data.instant NOT NULL,
 implementation text NOT NULL CHECK(implementation='explicit_lifecycle_action_policy_v1'),actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,PRIMARY KEY(catalog_id,id,revision),CHECK(from_at<until_at),
 UNIQUE(catalog_id,id,action,mode,from_at,until_at),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order));
CREATE TABLE lifecycle.commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),kind text NOT NULL,
 request jsonb NOT NULL CHECK(pg_column_size(request)<=4194304),outcome jsonb NOT NULL,
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,authority_until timestamptz NOT NULL CHECK(isfinite(authority_until)),
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,policy_id,policy_revision) REFERENCES lifecycle.action_policies(catalog_id,id,revision));
DO $tables$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['action_policies','commands'] LOOP
  EXECUTE format('ALTER TABLE lifecycle.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE lifecycle.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON lifecycle.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON lifecycle.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON lifecycle.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$tables$;
GRANT SELECT ON lifecycle.action_kinds TO openetrm_runtime,openetrm_security_owner;
GRANT SELECT ON lifecycle.action_policies TO openetrm_runtime;
CREATE POLICY scoped_read ON lifecycle.action_policies FOR SELECT TO openetrm_runtime USING(
 security.trading_visible(catalog_id,publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00'));
CREATE FUNCTION lifecycle.require_read(catalog uuid,until_at timestamptz) RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT settlement_data.require_futures_margin_preview(catalog,until_at);
$$;
CREATE FUNCTION lifecycle.capture_authority(catalog uuid,permission text) RETURNS timestamptz LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;BEGIN
 until_at=least(settlement_data.capture_futures_margin_authority(catalog,permission),security.authority_until('ReadTrades'));
 PERFORM lifecycle.require_read(catalog,until_at);RETURN until_at;
END$$;
CREATE FUNCTION lifecycle.require_authority(catalog uuid,permission text,until_at timestamptz) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;BEGIN
 actor=settlement_data.require_futures_margin_authority(catalog,permission,until_at);PERFORM lifecycle.require_read(catalog,until_at);RETURN actor;
END$$;
CREATE FUNCTION lifecycle.action_permission(kind text,body jsonb) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE permission text;BEGIN
 SELECT CASE WHEN kind IN('policy','option_profile','swap_profile') THEN k.configuration_permission ELSE k.execution_permission END INTO permission
 FROM lifecycle.action_kinds k WHERE k.code=body->>'action';
 IF permission IS NULL THEN RAISE EXCEPTION 'lifecycle_action_unsupported';END IF;RETURN permission;
END$$;
CREATE FUNCTION lifecycle.command_status(catalog uuid,operation uuid,kind text,body jsonb,permission text,until_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;c lifecycle.commands;BEGIN
 actor=lifecycle.require_authority(catalog,permission,until_at);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('lifecycle:'||catalog::text||':'||operation::text,0));
 SELECT * INTO c FROM lifecycle.commands s WHERE(s.catalog_id,s.operation_id)=(catalog,operation);
 IF FOUND AND(c.actor_id,c.kind,c.request) IS DISTINCT FROM(actor,kind,body) THEN RAISE EXCEPTION 'lifecycle_command_conflict';END IF;
 IF c.operation_id IS NOT NULL THEN PERFORM lifecycle.finish_command(catalog,operation,kind,body,permission,until_at);END IF;
 PERFORM lifecycle.require_authority(catalog,permission,until_at);RETURN c.outcome;
END$$;
CREATE FUNCTION lifecycle.require_policy(catalog uuid,policy uuid,revision integer,action text,at timestamptz,automatic boolean)
 RETURNS lifecycle.action_policies LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p lifecycle.action_policies;BEGIN
 SELECT * INTO p FROM lifecycle.action_policies s WHERE(s.catalog_id,s.id,s.revision)=(catalog,policy,require_policy.revision);
 IF p.id IS NULL OR p.action IS DISTINCT FROM action THEN RAISE EXCEPTION 'lifecycle_action_policy';END IF;
 IF at IS NULL OR at<p.from_at OR at>=p.until_at THEN RAISE EXCEPTION 'lifecycle_action_window';END IF;
 IF automatic IS NULL THEN RAISE EXCEPTION 'lifecycle_action_policy';END IF;
 IF automatic AND(p.mode<>'automatic' OR NOT EXISTS(SELECT 1 FROM lifecycle.action_kinds k WHERE k.code=action AND k.automatic))
 THEN RAISE EXCEPTION 'lifecycle_automation_policy';END IF;
 RETURN p;
END$$;
CREATE FUNCTION lifecycle.capture_action_authority(catalog uuid,kind text,body jsonb) RETURNS timestamptz
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;run_until timestamptz;permission text;mode text;needs_jobs boolean;BEGIN
 permission=lifecycle.action_permission(kind,body);
 until_at=lifecycle.capture_authority(catalog,permission);run_until=security.authority_until('RunJobs');
 IF kind='policy' THEN needs_jobs=body->>'mode'='automatic';
 ELSIF kind IN('option_profile','swap_profile') THEN
  SELECT p.mode INTO mode FROM lifecycle.action_policies p WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer);
  needs_jobs=mode='automatic';
 ELSE needs_jobs=coalesce((body->>'automatic')::boolean,false);END IF;
 IF needs_jobs THEN
  until_at=least(until_at,run_until);
  IF security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM lifecycle.require_authority(catalog,permission,until_at);RETURN until_at;
END$$;
CREATE FUNCTION lifecycle.save_policy(catalog uuid,body jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE operation uuid;actor uuid;until_at timestamptz;prior jsonb;p lifecycle.action_policies;fact lifecycle.action_policies;expected integer;publication integer;result jsonb;permission text;changed boolean=false;BEGIN
 until_at=lifecycle.capture_action_authority(catalog,'policy',body);operation=(body->>'operationId')::uuid;
 permission=lifecycle.action_permission('policy',body);
 prior=lifecycle.command_status(catalog,operation,'policy',body,permission,until_at);IF prior IS NOT NULL THEN RETURN prior;END IF;
 PERFORM pg_advisory_xact_lock(730849102);actor=lifecycle.require_authority(catalog,permission,until_at);
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['operationId','policyId','expectedRevision','action','mode','fromAt','untilAt','implementation'])
 OR EXISTS(SELECT 1 FROM jsonb_object_keys(body)k WHERE k NOT IN('operationId','policyId','expectedRevision','action','mode','fromAt','untilAt','implementation'))
 OR jsonb_typeof(body->'expectedRevision') IS DISTINCT FROM 'number' OR body->>'expectedRevision'!~'^[0-9]{1,8}$'
 OR body->>'implementation' IS DISTINCT FROM 'explicit_lifecycle_action_policy_v1' THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 expected=(body->>'expectedRevision')::integer;p.catalog_id=catalog;p.id=(body->>'policyId')::uuid;p.revision=expected+1;p.action=body->>'action';p.mode=body->>'mode';
 p.from_at=settlement_data.futures_margin_utc_instant(body->>'fromAt');p.until_at=settlement_data.futures_margin_utc_instant(body->>'untilAt');
 p.implementation=body->>'implementation';p.actor_id=actor;
 IF p.id IS NULL OR p.id='00000000-0000-0000-0000-000000000000'::uuid OR p.mode IS NULL OR p.from_at>=p.until_at THEN RAISE EXCEPTION 'lifecycle_input';END IF;
 IF p.mode='automatic' AND NOT EXISTS(SELECT 1 FROM lifecycle.action_kinds k WHERE k.code=p.action AND k.automatic) THEN RAISE EXCEPTION 'lifecycle_automation_unsupported';END IF;
 IF p.mode='automatic' AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO fact FROM lifecycle.action_policies s WHERE(s.catalog_id,s.id,s.action,s.mode,s.from_at,s.until_at)=(catalog,p.id,p.action,p.mode,p.from_at,p.until_at);
 IF fact.id IS NULL THEN
  IF coalesce((SELECT max(s.revision) FROM lifecycle.action_policies s WHERE(s.catalog_id,s.id)=(catalog,p.id)),0)<>expected THEN RAISE EXCEPTION 'lifecycle_policy_revision';END IF;
  SELECT coalesce(max(x.publication_order),0)+1 INTO p.publication_order FROM trading.publications x WHERE x.catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,p.publication_order,pg_current_xact_id());INSERT INTO lifecycle.action_policies SELECT(p).*;fact=p;changed=true;
 END IF;
 result=jsonb_build_object('operationId',operation,'policyId',fact.id,'revision',fact.revision,'publicationOrder',fact.publication_order,
  'acknowledgedRevision',fact.revision,'currentRevision',(SELECT max(s.revision) FROM lifecycle.action_policies s WHERE(s.catalog_id,s.id)=(catalog,fact.id)),
  'changed',changed,'semanticAcknowledgement',NOT changed);
 INSERT INTO lifecycle.commands VALUES(catalog,operation,actor,'policy',body,result,fact.id,fact.revision,until_at);
 PERFORM lifecycle.require_authority(catalog,permission,until_at);RETURN result;
END$$;
CREATE FUNCTION lifecycle.finish_command(catalog uuid,operation uuid,kind text,body jsonb,permission text,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;c lifecycle.commands;p lifecycle.action_policies;expected_permission text;BEGIN
 expected_permission=lifecycle.action_permission(kind,body);
 IF permission IS DISTINCT FROM expected_permission THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 actor=lifecycle.require_authority(catalog,permission,until_at);
 SELECT * INTO c FROM lifecycle.commands s WHERE(s.catalog_id,s.operation_id)=(catalog,operation);
 IF c.operation_id IS NULL OR(c.actor_id,c.kind,c.request) IS DISTINCT FROM(actor,kind,body) THEN RAISE EXCEPTION 'lifecycle_command_conflict';END IF;
 SELECT * INTO STRICT p FROM lifecycle.action_policies s WHERE(s.catalog_id,s.id,s.revision)=(catalog,c.policy_id,c.policy_revision);
 IF kind LIKE 'option.%' OR kind='option_profile' THEN
  PERFORM lifecycle.require_option_reference(catalog,coalesce((body#>>'{target,scopeId}')::uuid,(body->>'tradeId')::uuid),
   body->>'action',CASE WHEN kind='option_profile' THEN(c.outcome->>'revision')::integer ELSE(body#>>'{selection,profileRevision}')::integer END);
 ELSIF kind='swap_profile' THEN
  PERFORM lifecycle.require_swap_reference(catalog,(body->>'tradeId')::uuid,body->>'action',(c.outcome->>'revision')::integer);
 ELSIF kind='futures.margin' THEN
  PERFORM settlement_data.finish_futures_margin_run(catalog,operation,(SELECT r.request FROM settlement_data.futures_margin_run_commands r WHERE(r.catalog_id,r.operation_id)=(catalog,operation)),until_at);
 ELSIF kind IN('swap.determine','swap.payment') THEN
  PERFORM settlement_data.swap_command_status(catalog,operation,lifecycle.swap_native_request(body));
  PERFORM lifecycle.require_swap_reference(catalog,(body#>>'{target,scopeId}')::uuid,kind,(body#>>'{selection,profileRevision}')::integer);
 END IF;
 IF((kind IN('policy','option_profile','swap_profile') AND p.mode='automatic') OR coalesce((body->>'automatic')::boolean,false))
 AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'lifecycle_denied' USING ERRCODE='42501';END IF;
 PERFORM lifecycle.require_authority(catalog,permission,until_at);
END$$;
DO $functions$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='lifecycle' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$functions$;
GRANT EXECUTE ON FUNCTION lifecycle.save_policy(uuid,jsonb),lifecycle.capture_authority(uuid,text),lifecycle.capture_action_authority(uuid,text,jsonb),lifecycle.require_read(uuid,timestamptz),
 lifecycle.action_permission(text,jsonb),lifecycle.command_status(uuid,uuid,text,jsonb,text,timestamptz),lifecycle.finish_command(uuid,uuid,text,jsonb,text,timestamptz) TO openetrm_runtime;
COMMIT;
