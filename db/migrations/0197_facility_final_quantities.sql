-- Explicit final-input acceptance. Quantities remain in their exact allocation rows.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TABLE trading.facility_quantity_adoptions(
 catalog_id uuid NOT NULL,ledger_id uuid NOT NULL,id uuid NOT NULL,revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),previous_id uuid,
 plan_id uuid NOT NULL,state text NOT NULL CHECK(state IN('adopted','withdrawn')),evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,ledger_id,id),UNIQUE(catalog_id,id,ledger_id,plan_id),UNIQUE(catalog_id,ledger_id,revision),UNIQUE(catalog_id,ledger_id,plan_id,state,evidence,effective_at),
 CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,ledger_id,previous_id) REFERENCES trading.facility_quantity_adoptions(catalog_id,ledger_id,id),
 FOREIGN KEY(catalog_id,ledger_id,plan_id) REFERENCES trading.facility_quantity_plans(catalog_id,ledger_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.facility_adopted_targets(
 catalog_id uuid NOT NULL,id uuid NOT NULL,adoption_id uuid NOT NULL,ledger_id uuid NOT NULL,plan_id uuid NOT NULL,
 obligation_id uuid NOT NULL,allocation_obligation_id uuid,previous_id uuid,revision integer NOT NULL CHECK(revision BETWEEN 1 AND 1000),
 state text NOT NULL CHECK(state IN('final','withdrawn')),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,ledger_id,obligation_id,id),UNIQUE(catalog_id,ledger_id,obligation_id,revision),
 UNIQUE(catalog_id,adoption_id,obligation_id),CHECK((revision=1)=(previous_id IS NULL)),
 CHECK((state='final' AND allocation_obligation_id IS NOT NULL AND allocation_obligation_id=obligation_id) OR(state='withdrawn' AND allocation_obligation_id IS NULL)),
 FOREIGN KEY(catalog_id,ledger_id,obligation_id,previous_id) REFERENCES trading.facility_adopted_targets(catalog_id,ledger_id,obligation_id,id),
 FOREIGN KEY(catalog_id,adoption_id,ledger_id,plan_id) REFERENCES trading.facility_quantity_adoptions(catalog_id,id,ledger_id,plan_id),
 FOREIGN KEY(catalog_id,ledger_id,obligation_id) REFERENCES trading.facility_gas_targets(catalog_id,ledger_id,obligation_id),
 FOREIGN KEY(catalog_id,plan_id,allocation_obligation_id) REFERENCES trading.facility_gas_allocations(catalog_id,plan_id,obligation_id)
);
ALTER TABLE trading.facility_quantity_commands ADD COLUMN adoption_id uuid;
ALTER TABLE trading.facility_quantity_commands ADD FOREIGN KEY(catalog_id,ledger_id,adoption_id) REFERENCES trading.facility_quantity_adoptions(catalog_id,ledger_id,id);
ALTER TABLE trading.facility_quantity_commands ADD CHECK((request#>>'{body,kind}' IN('adopt','retract_adoption'))=(adoption_id IS NOT NULL));
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['facility_quantity_adoptions','facility_adopted_targets'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

CREATE FUNCTION security.require_facility_adoption_targets(catalog uuid,adoption uuid,writing boolean) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE a trading.facility_quantity_adoptions;previous_plan uuid;target record;BEGIN
 SELECT * INTO STRICT a FROM trading.facility_quantity_adoptions WHERE(catalog_id,id)=(catalog,adoption);
 SELECT plan_id INTO previous_plan FROM trading.facility_quantity_adoptions WHERE(catalog_id,id)=(catalog,a.previous_id);
 FOR target IN SELECT DISTINCT trade_id FROM trading.facility_gas_allocations WHERE catalog_id=catalog AND plan_id IN(a.plan_id,previous_plan) LOOP
  PERFORM security.require_gas_quantity_trade(catalog,target.trade_id,writing);
 END LOOP;
END$$;

CREATE FUNCTION trading.guard_facility_adoption() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE a trading.facility_quantity_adoptions;p trading.facility_quantity_plans;prior trading.facility_quantity_adoptions;
 source trading.facility_quantity_revisions;l trading.facility_quantity_ledgers;t record;h trading.facility_adopted_targets;line trading.facility_gas_allocations;old_line trading.facility_gas_allocations;current_target trading.facility_gas_target;
BEGIN
 IF TG_TABLE_NAME='facility_quantity_adoptions' THEN a=NEW;
 ELSE SELECT * INTO STRICT a FROM trading.facility_quantity_adoptions WHERE(catalog_id,id)=(NEW.catalog_id,NEW.adoption_id);END IF;
 PERFORM trading.require_facility_quantity_writer(a.catalog_id,a.actor_id,'adopt',a.publication_order);
 PERFORM security.require_facility_adoption_targets(a.catalog_id,a.id,true);
 SELECT * INTO STRICT p FROM trading.facility_quantity_plans WHERE(catalog_id,id)=(a.catalog_id,a.plan_id);
 SELECT * INTO STRICT l FROM trading.facility_quantity_ledgers WHERE(catalog_id,id)=(a.catalog_id,a.ledger_id);
 SELECT * INTO prior FROM trading.facility_quantity_adoptions WHERE(catalog_id,ledger_id)=(a.catalog_id,a.ledger_id) AND revision<a.revision ORDER BY revision DESC LIMIT 1;
 IF a.previous_id IS DISTINCT FROM prior.id OR a.revision<>coalesce(prior.revision,0)+1 OR a.effective_at<prior.effective_at OR a.effective_at<l.native_to OR a.effective_at<p.effective_at
 OR prior.id IS NOT NULL AND(prior.publication_order>=a.publication_order OR NOT EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(prior.catalog_id,prior.publication_order)))
 OR p.publication_order>=a.publication_order OR NOT EXISTS(SELECT 1 FROM trading.knowledge_receipts k WHERE(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)) THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
 IF a.state='adopted' THEN
  SELECT * INTO source FROM trading.facility_quantity_revisions WHERE(catalog_id,ledger_id)=(a.catalog_id,a.ledger_id) ORDER BY revision DESC LIMIT 1;
  IF p.id IS DISTINCT FROM(SELECT id FROM trading.facility_quantity_plans WHERE(catalog_id,ledger_id)=(a.catalog_id,a.ledger_id) ORDER BY revision DESC LIMIT 1)
   OR p.source_revision_id IS DISTINCT FROM source.id OR a.effective_at<source.effective_at THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
  IF(SELECT coalesce(sum(quantity),0) FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id)=(a.catalog_id,p.id))>source.quantity THEN RAISE EXCEPTION 'facility_quantity_overallocated';END IF;
  FOR line IN SELECT * FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id)=(a.catalog_id,p.id) LOOP
   current_target=security.facility_gas_target_for_ledger(a.catalog_id,a.ledger_id,line.obligation_id,a.effective_at,line.quantity);
   IF(current_target.trade_id,current_target.trade_revision,current_target.instrument_id) IS DISTINCT FROM(line.trade_id,line.trade_revision,line.instrument_id) THEN RAISE EXCEPTION 'facility_quantity_target';END IF;
  END LOOP;
 ELSE
  IF prior.id IS NULL OR prior.state<>'adopted' OR prior.plan_id<>a.plan_id THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
 END IF;
 -- Validate the complete resulting accepted set, including reused own heads.
 FOR t IN SELECT obligation_id FROM trading.facility_gas_targets WHERE(catalog_id,ledger_id)=(a.catalog_id,a.ledger_id) LOOP
  SELECT x.* INTO h FROM trading.facility_adopted_targets x JOIN trading.facility_quantity_adoptions ah ON(ah.catalog_id,ah.id)=(x.catalog_id,x.adoption_id)
   WHERE(x.catalog_id,x.ledger_id,x.obligation_id)=(a.catalog_id,a.ledger_id,t.obligation_id) AND ah.revision<=a.revision ORDER BY x.revision DESC LIMIT 1;
  SELECT * INTO line FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id,obligation_id)=(a.catalog_id,p.id,t.obligation_id) AND a.state='adopted';
  IF line.obligation_id IS NOT NULL THEN
   SELECT * INTO old_line FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id,obligation_id)=(a.catalog_id,h.plan_id,h.allocation_obligation_id);
   IF h.state IS DISTINCT FROM 'final' OR(old_line.quantity,old_line.trade_id,old_line.trade_revision,old_line.instrument_id) IS DISTINCT FROM(line.quantity,line.trade_id,line.trade_revision,line.instrument_id)
    OR p.source_revision_id IS DISTINCT FROM(SELECT source_revision_id FROM trading.facility_quantity_plans WHERE(catalog_id,id)=(a.catalog_id,h.plan_id)) THEN RAISE EXCEPTION 'facility_quantity_target';END IF;
  ELSIF h.id IS NOT NULL AND h.state<>'withdrawn' THEN RAISE EXCEPTION 'facility_quantity_target';END IF;
 END LOOP;
 IF TG_TABLE_NAME='facility_adopted_targets' THEN
  SELECT x.* INTO h FROM trading.facility_adopted_targets x WHERE(x.catalog_id,x.ledger_id,x.obligation_id)=(NEW.catalog_id,NEW.ledger_id,NEW.obligation_id) AND x.revision<NEW.revision ORDER BY x.revision DESC LIMIT 1;
  IF NEW.previous_id IS DISTINCT FROM h.id OR NEW.revision<>coalesce(h.revision,0)+1 THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
 END IF;
 PERFORM trading.require_facility_quantity_writer(a.catalog_id,a.actor_id,'adopt',a.publication_order);
 PERFORM security.require_facility_adoption_targets(a.catalog_id,a.id,true);
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER valid_facility_adoption AFTER INSERT ON trading.facility_quantity_adoptions DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_facility_adoption();
CREATE CONSTRAINT TRIGGER valid_facility_adopted_target AFTER INSERT ON trading.facility_adopted_targets DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_facility_adoption();

CREATE OR REPLACE FUNCTION security.facility_quantity_status(catalog uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.facility_quantity_commands;actor uuid;trade uuid;l trading.facility_quantity_ledgers;BEGIN
 PERFORM security.require_facility_quantities(catalog,false);
 SELECT c.actor_id INTO actor FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.expires_at>clock_timestamp();
 SELECT * INTO prior FROM trading.facility_quantity_commands c WHERE(c.catalog_id,c.operation_id,c.actor_id)=(catalog,operation,actor);
 IF prior.operation_id IS NULL THEN RETURN jsonb_build_object('found',false);END IF;
 PERFORM security.require_facility_quantity_command(catalog,prior.request#>>'{body,kind}');
 SELECT * INTO STRICT l FROM trading.facility_quantity_ledgers h WHERE(h.catalog_id,h.id)=(catalog,prior.ledger_id);
 IF NOT security.reference_version_visible(l.reference_catalog_id,l.reference_revision_id) THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_facility_plan_targets(catalog,prior.previous_plan_id,prior.result_plan_id);
 IF prior.adoption_id IS NOT NULL THEN PERFORM security.require_facility_adoption_targets(catalog,prior.adoption_id,true);END IF;
 IF prior.create_obligation_id IS NOT NULL THEN
  SELECT o.trade_id INTO trade FROM trading.gas_delivery_obligations o WHERE(o.catalog_id,o.id)=(catalog,prior.create_obligation_id);
  PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 END IF;
 IF NOT security.reference_version_visible(l.reference_catalog_id,l.reference_revision_id) THEN RAISE EXCEPTION 'facility_quantity_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_facility_quantity_command(catalog,prior.request#>>'{body,kind}');
 IF prior.adoption_id IS NOT NULL THEN PERFORM security.require_facility_adoption_targets(catalog,prior.adoption_id,true);END IF;
 PERFORM security.require_facility_quantity_command(catalog,prior.request#>>'{body,kind}');
 RETURN prior.outcome;
END$$;

ALTER FUNCTION security.record_facility_quantity_command(uuid,uuid,jsonb,timestamptz) RENAME TO record_facility_quantity_plan_command;
REVOKE ALL ON FUNCTION security.record_facility_quantity_plan_command(uuid,uuid,jsonb,timestamptz) FROM PUBLIC,openetrm_runtime;
CREATE FUNCTION security.record_facility_quantity_command(catalog uuid,operation uuid,body jsonb,as_of timestamptz) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;payload jsonb;receipt trading.facility_quantity_commands;prior trading.facility_quantity_adoptions;l trading.facility_quantity_ledgers;
 p trading.facility_quantity_plans;source trading.facility_quantity_revisions;identity uuid;publication integer;next_state text;outcome jsonb;
 target record;h trading.facility_adopted_targets;line trading.facility_gas_allocations;old_line trading.facility_gas_allocations;target_state text;
BEGIN
 IF body->>'kind' NOT IN('adopt','retract_adoption') OR body->>'kind' IS NULL THEN RETURN security.record_facility_quantity_plan_command(catalog,operation,body,as_of);END IF;
 PERFORM security.require_facility_quantity_command(catalog,'adopt');PERFORM pg_advisory_xact_lock(730849102);
 SELECT c.actor_id INTO STRICT actor FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.expires_at>clock_timestamp();
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR as_of IS NULL OR NOT isfinite(as_of)
 OR jsonb_typeof(body)<>'object' OR pg_column_size(body)>8192
 OR(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(body)k) IS DISTINCT FROM ARRAY['evidence','expectedPreviousId','kind','ledgerId','planRevisionId','sourceRevisionId']::text[]
 OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string' OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'facility_quantity_input';END IF;
 payload=jsonb_build_object('body',body,'effectiveAt',as_of);
 SELECT * INTO receipt FROM trading.facility_quantity_commands c WHERE(c.catalog_id,c.actor_id,c.operation_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF receipt.request IS DISTINCT FROM payload THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
  RETURN security.facility_quantity_status(catalog,operation);
 END IF;
 SELECT * INTO l FROM trading.facility_quantity_ledgers x WHERE(x.catalog_id,x.id)=(catalog,(body->>'ledgerId')::uuid) FOR UPDATE;
 IF l.id IS NULL OR NOT security.reference_version_visible(l.reference_catalog_id,l.reference_revision_id) THEN RAISE EXCEPTION 'facility_quantity_ledger';END IF;
 IF as_of<l.native_to OR as_of<l.effective_at THEN RAISE EXCEPTION 'facility_quantity_interval';END IF;
 SELECT * INTO prior FROM trading.facility_quantity_adoptions WHERE(catalog_id,ledger_id)=(catalog,l.id) ORDER BY revision DESC LIMIT 1;
 SELECT * INTO p FROM trading.facility_quantity_plans WHERE(catalog_id,ledger_id,id)=(catalog,l.id,(body->>'planRevisionId')::uuid);
 IF p.id IS NULL OR p.source_revision_id IS DISTINCT FROM(body->>'sourceRevisionId')::uuid THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
 next_state=CASE WHEN body->>'kind'='adopt' THEN 'adopted' ELSE 'withdrawn' END;
 -- A past fact replay returns that fact without reinstating its accepted heads.
 SELECT id INTO identity FROM trading.facility_quantity_adoptions WHERE(catalog_id,ledger_id,plan_id,state,evidence,effective_at)=(catalog,l.id,p.id,next_state,btrim(body->>'evidence'),as_of);
 IF identity IS NOT NULL THEN
  PERFORM security.require_facility_adoption_targets(catalog,identity,true);
 ELSE
  -- New adoption authorizes included and removed targets before conflict details.
  FOR target IN SELECT DISTINCT trade_id FROM trading.facility_gas_allocations WHERE catalog_id=catalog AND plan_id IN(p.id,prior.plan_id) LOOP
   PERFORM security.require_gas_quantity_trade(catalog,target.trade_id,true);
  END LOOP;
  IF prior.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid OR as_of<prior.effective_at THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
  IF next_state='withdrawn' AND(prior.id IS NULL OR prior.state<>'adopted' OR prior.plan_id<>p.id) THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
  IF next_state='adopted' THEN
   SELECT * INTO source FROM trading.facility_quantity_revisions WHERE(catalog_id,ledger_id)=(catalog,l.id) ORDER BY revision DESC LIMIT 1;
   IF p.id IS DISTINCT FROM(SELECT id FROM trading.facility_quantity_plans WHERE(catalog_id,ledger_id)=(catalog,l.id) ORDER BY revision DESC LIMIT 1)
    OR p.source_revision_id IS DISTINCT FROM source.id OR as_of<source.effective_at OR as_of<p.effective_at THEN RAISE EXCEPTION 'facility_quantity_conflict';END IF;
  END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());identity=gen_random_uuid();
  INSERT INTO trading.facility_quantity_adoptions VALUES(catalog,l.id,identity,coalesce(prior.revision,0)+1,prior.id,p.id,next_state,btrim(body->>'evidence'),actor,publication,as_of);
  FOR target IN SELECT obligation_id FROM trading.facility_gas_targets WHERE(catalog_id,ledger_id)=(catalog,l.id) LOOP
   SELECT * INTO h FROM trading.facility_adopted_targets WHERE(catalog_id,ledger_id,obligation_id)=(catalog,l.id,target.obligation_id) ORDER BY revision DESC LIMIT 1;
   SELECT * INTO line FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id,obligation_id)=(catalog,p.id,target.obligation_id) AND next_state='adopted';
   target_state=CASE WHEN line.obligation_id IS NULL THEN 'withdrawn' ELSE 'final' END;
   IF target_state='withdrawn' AND(h.id IS NULL OR h.state='withdrawn') THEN CONTINUE;END IF;
   SELECT * INTO old_line FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id,obligation_id)=(catalog,h.plan_id,h.allocation_obligation_id);
   IF target_state='final' AND h.state='final' AND(old_line.quantity,old_line.trade_id,old_line.trade_revision,old_line.instrument_id) IS NOT DISTINCT FROM(line.quantity,line.trade_id,line.trade_revision,line.instrument_id)
    AND p.source_revision_id=(SELECT source_revision_id FROM trading.facility_quantity_plans WHERE(catalog_id,id)=(catalog,h.plan_id)) THEN CONTINUE;END IF;
   INSERT INTO trading.facility_adopted_targets VALUES(catalog,gen_random_uuid(),identity,l.id,p.id,target.obligation_id,line.obligation_id,h.id,coalesce(h.revision,0)+1,target_state);
  END LOOP;
  SET CONSTRAINTS trading.valid_facility_adoption,trading.valid_facility_adopted_target IMMEDIATE;
  SET CONSTRAINTS trading.valid_facility_adoption,trading.valid_facility_adopted_target DEFERRED;
 END IF;
 outcome=jsonb_build_object('ledgerId',l.id,'adoptionId',identity,'state',next_state);
 INSERT INTO trading.facility_quantity_commands(catalog_id,operation_id,actor_id,ledger_id,request,outcome,adoption_id) VALUES(catalog,operation,actor,l.id,payload,outcome,identity);
 RETURN security.facility_quantity_status(catalog,operation);
END$$;

ALTER FUNCTION security.read_facility_quantity_ledger(uuid,uuid) RENAME TO read_facility_quantity_plan_ledger;
REVOKE ALL ON FUNCTION security.read_facility_quantity_plan_ledger(uuid,uuid) FROM PUBLIC,openetrm_runtime;
CREATE FUNCTION security.read_facility_quantity_ledger(catalog uuid,ledger uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;a trading.facility_quantity_adoptions;p trading.facility_quantity_plans;BEGIN
 result=security.read_facility_quantity_plan_ledger(catalog,ledger);
 SELECT * INTO a FROM trading.facility_quantity_adoptions WHERE(catalog_id,ledger_id)=(catalog,ledger) AND security.trading_visible(catalog_id,publication_order,effective_at) ORDER BY revision DESC LIMIT 1;
 IF a.id IS NOT NULL THEN
  PERFORM security.require_facility_adoption_targets(catalog,a.id,false);
  SELECT * INTO STRICT p FROM trading.facility_quantity_plans WHERE(catalog_id,id)=(catalog,a.plan_id);
 END IF;
 PERFORM security.require_facility_quantity_read(catalog);
 RETURN result||jsonb_build_object('adoption',CASE WHEN a.id IS NOT NULL THEN to_jsonb(a)||jsonb_build_object('source_revision_id',p.source_revision_id) END,
  'requiresAdoptionReconciliation',a.state='adopted' AND(p.id::text IS DISTINCT FROM result#>>'{plan,id}' OR p.source_revision_id::text IS DISTINCT FROM result#>>'{source,id}'));
END$$;

CREATE FUNCTION security.read_trade_facility_final_quantities(catalog uuid,trade uuid,mode text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);
 IF mode NOT IN('heads','history') OR mode IS NULL OR NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid()
  AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user AND c.market_catalog_id=catalog AND c.market_context_id IS NOT NULL AND c.trading_as_of IS NOT NULL) THEN RAISE EXCEPTION 'gas_quantity_context';END IF;
 WITH visible AS MATERIALIZED(
  SELECT h.id,h.obligation_id,g.day,'allocated'::text kind,'facility_allocation_adoption_v1'::text source,CASE WHEN h.state='final' THEN 'final' ELSE 'withdrawn' END status,
   trim_scale(line.quantity)::text quantity,a.evidence,h.revision,a.effective_at,a.publication_order,
   row_number() OVER(PARTITION BY h.obligation_id,g.day ORDER BY h.revision DESC)rank
  FROM trading.gas_delivery_obligations o JOIN trading.facility_adopted_targets h ON(h.catalog_id,h.obligation_id)=(o.catalog_id,o.id)
  JOIN trading.facility_quantity_adoptions a ON(a.catalog_id,a.id)=(h.catalog_id,h.adoption_id)
  JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(h.catalog_id,h.ledger_id)
  LEFT JOIN trading.facility_gas_allocations line ON(line.catalog_id,line.plan_id,line.obligation_id)=(h.catalog_id,h.plan_id,h.allocation_obligation_id)
  WHERE(o.catalog_id,o.trade_id)=(catalog,trade) AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at)
 ), selected AS(SELECT id,obligation_id,day,kind,source,status,quantity,evidence,revision,effective_at FROM visible WHERE mode='history' OR rank=1 ORDER BY publication_order DESC LIMIT CASE WHEN mode='history' THEN 100 ELSE NULL END)
 SELECT coalesce(jsonb_agg(to_jsonb(s)),'[]') INTO result FROM selected s;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);RETURN result;
END$$;
ALTER FUNCTION security.read_trade_facility_allocations(uuid,uuid) RENAME TO read_trade_facility_plans;
REVOKE ALL ON FUNCTION security.read_trade_facility_plans(uuid,uuid) FROM PUBLIC,openetrm_runtime;
CREATE FUNCTION security.read_trade_facility_allocations(catalog uuid,trade uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE planned jsonb;accepted jsonb;result jsonb;BEGIN
 planned=security.read_trade_facility_plans(catalog,trade);
 accepted=security.read_trade_facility_final_quantities(catalog,trade,'heads');
 SELECT coalesce(jsonb_agg(p.value||jsonb_build_object('finalQuantity',a.value->'quantity','finalState',coalesce(a.value->>'status','missing')) ORDER BY p.ordinality),'[]') INTO result
 FROM jsonb_array_elements(planned) WITH ORDINALITY p(value,ordinality)
 LEFT JOIN LATERAL(SELECT value FROM jsonb_array_elements(accepted) WHERE value->>'obligation_id'=p.value->>'obligationId' AND value->>'day'=p.value->>'day')a ON true;
 PERFORM security.require_gas_quantity_trade(catalog,trade,false);RETURN result;
END$$;
DO $$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE(n.nspname,p.proname) IN
 (('security','require_facility_adoption_targets'),('trading','guard_facility_adoption'),('security','record_facility_quantity_command'),('security','read_facility_quantity_ledger'),('security','read_trade_facility_final_quantities'),('security','read_trade_facility_allocations')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.record_facility_quantity_command(uuid,uuid,jsonb,timestamptz),security.read_facility_quantity_ledger(uuid,uuid),security.read_trade_facility_final_quantities(uuid,uuid,text),security.read_trade_facility_allocations(uuid,uuid) TO openetrm_runtime;
COMMIT;
