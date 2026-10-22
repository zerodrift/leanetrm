-- Immutable explicit payment continuity. No cash acceptance, claims or obligations.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
INSERT INTO security.permissions VALUES('ProposeSettlementDeterminations');
CREATE TABLE settlement_data.payment_lineages (
 catalog_id uuid NOT NULL,id uuid NOT NULL,trade_id uuid NOT NULL,
 original_revision integer NOT NULL CHECK(original_revision=1),original_binding_id uuid NOT NULL,
 original_instrument_id uuid NOT NULL,original_role text NOT NULL CHECK(original_role='strike_payment'),original_ordinal integer NOT NULL,
 original_side_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 currency_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,payment_rule_id uuid NOT NULL,
 direction ref_data.exact_number NOT NULL CHECK(direction IN(-1,1)),
 contract text NOT NULL CHECK(contract='ppa_monthly_gross_fixed_v1'),evidence text NOT NULL CHECK(evidence='verified_meter_head_v1'),
 rounding text NOT NULL CHECK(rounding='cash_2dp_half_even_per_occurrence_v1'),approval text NOT NULL CHECK(approval='two_principals_exact_action_24h_v1'),
 correction text NOT NULL CHECK(correction='restate_rounded_occurrence_v1'),adjustment_due text NOT NULL CHECK(adjustment_due='original_due_for_adjustment_v1'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,trade_id),
 UNIQUE(catalog_id,trade_id,original_binding_id,original_role,original_ordinal),
 FOREIGN KEY(catalog_id,trade_id,original_revision,original_binding_id) REFERENCES trading.ppa_revision_bindings(catalog_id,trade_id,revision,binding_id),
 FOREIGN KEY(catalog_id,original_instrument_id,original_role,original_ordinal) REFERENCES trading.ppa_memberships(catalog_id,instrument_id,role,ordinal),
 FOREIGN KEY(catalog_id,original_side_id) REFERENCES trading.ppa_sides(catalog_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,payment_rule_id) REFERENCES trading.swap_payment_rules(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE settlement_data.payment_lineage_members (
 catalog_id uuid NOT NULL,lineage_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,origin_revision integer NOT NULL,
 instrument_id uuid NOT NULL,role text NOT NULL CHECK(role='strike_payment'),ordinal integer NOT NULL,side_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,binding_id,role,ordinal),
 FOREIGN KEY(catalog_id,lineage_id,trade_id) REFERENCES settlement_data.payment_lineages(catalog_id,id,trade_id),
 FOREIGN KEY(catalog_id,trade_id,origin_revision,binding_id) REFERENCES trading.ppa_revision_bindings(catalog_id,trade_id,revision,binding_id),
 FOREIGN KEY(catalog_id,instrument_id,role,ordinal) REFERENCES trading.ppa_memberships(catalog_id,instrument_id,role,ordinal),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.ppa_sides(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['payment_lineages','payment_lineage_members'] LOOP
  EXECUTE format('ALTER TABLE settlement_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE settlement_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON settlement_data.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT,INSERT ON settlement_data.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON settlement_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION settlement_data.require_preview_authority(catalog uuid,trade uuid) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE ref uuid;BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.trading_access(catalog,'ProposeSettlementDeterminations') IS NOT TRUE
  OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'settlement_denied' USING ERRCODE='42501';END IF;
 SELECT reference_catalog_id INTO ref FROM trading.ppa_trade_origins WHERE(catalog_id,trade_id)=(catalog,trade);
 IF ref IS NULL OR security.reference_access(ref,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'settlement_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION settlement_data.plan_payment_lineage(catalog uuid,trade uuid,expected integer,context uuid,as_of timestamptz,original jsonb,mappings jsonb,policies jsonb)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE scope jsonb;head trading.ppa_trade_revisions;origin trading.ppa_revision_bindings;member trading.ppa_memberships;side trading.ppa_sides;
 lineage settlement_data.payment_lineages;old settlement_data.payment_lineage_members;binding trading.ppa_revision_bindings;
 m jsonb;publication integer;actor uuid;identity uuid;BEGIN
 PERFORM settlement_data.require_preview_authority(catalog,trade);
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM security.ppa_keys(original,ARRAY['bindingId','originRevision','instrumentId','sideId','role','ordinal']);
 PERFORM security.ppa_keys(policies,ARRAY['contract','evidence','rounding','approval','correction','adjustmentDue']);
 IF policies IS DISTINCT FROM '{"contract":"ppa_monthly_gross_fixed_v1","evidence":"verified_meter_head_v1","rounding":"cash_2dp_half_even_per_occurrence_v1","approval":"two_principals_exact_action_24h_v1","correction":"restate_rounded_occurrence_v1","adjustmentDue":"original_due_for_adjustment_v1"}'::jsonb
  OR jsonb_typeof(mappings) IS DISTINCT FROM 'array' OR jsonb_array_length(mappings) NOT BETWEEN 1 AND 32 OR pg_column_size(mappings)>32768
 THEN RAISE EXCEPTION 'settlement_policy';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM settlement_data.require_preview_authority(catalog,trade);
 scope=security.ppa_read_scope(catalog,'trades',ARRAY[trade],context,as_of,false);
 SELECT * INTO head FROM jsonb_populate_record(NULL::trading.ppa_trade_revisions,scope->'heads'->0);
 IF head.revision IS DISTINCT FROM expected THEN RAISE EXCEPTION 'settlement_trade_head';END IF;
 IF jsonb_array_length(scope->'bindings')<>jsonb_array_length(mappings) OR
  (SELECT count(DISTINCT value->>'bindingId') FROM jsonb_array_elements(mappings))<>jsonb_array_length(mappings)
 THEN RAISE EXCEPTION 'settlement_manifest';END IF;
 SELECT * INTO origin FROM trading.ppa_revision_bindings WHERE(catalog_id,trade_id,revision,binding_id)=(catalog,trade,1,(original->>'bindingId')::uuid);
 SELECT * INTO member FROM trading.ppa_memberships WHERE(catalog_id,instrument_id,role,ordinal)=(catalog,origin.instrument_id,original->>'role',(original->>'ordinal')::integer);
 SELECT * INTO side FROM trading.ppa_sides WHERE(catalog_id,id)=(catalog,member.side_id);
 IF origin.binding_id IS NULL OR (original->>'originRevision')::integer IS DISTINCT FROM 1 OR
  origin.instrument_id IS DISTINCT FROM (original->>'instrumentId')::uuid OR member.side_id IS DISTINCT FROM (original->>'sideId')::uuid
  OR member.role IS DISTINCT FROM 'strike_payment' OR side.kind IS DISTINCT FROM 'fixed' OR side.currency_code IS DISTINCT FROM 'usd' OR side.quantity_kind IS DISTINCT FROM 'slots' OR
  NOT EXISTS(SELECT 1 FROM trading.ppa_memberships pm JOIN trading.ppa_sides ps ON(ps.catalog_id,ps.id)=(pm.catalog_id,pm.side_id) WHERE(pm.catalog_id,pm.instrument_id,pm.role)=(catalog,origin.instrument_id,'energy_delivery') AND ps.quantity_kind='volume')
 THEN RAISE EXCEPTION 'settlement_lineage';END IF;
 SELECT * INTO lineage FROM settlement_data.payment_lineages l WHERE(l.catalog_id,l.trade_id,l.original_binding_id,l.original_role,l.original_ordinal)=
  (catalog,trade,origin.binding_id,member.role,member.ordinal);
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 identity=coalesce(lineage.id,gen_random_uuid());
 -- Administrative continuity is known when committed; its business anchor is the
 -- original immutable trade, never a new cash event or backdated acceptance.
 IF lineage.id IS NULL THEN
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO settlement_data.payment_lineages VALUES(catalog,identity,trade,1,origin.binding_id,origin.instrument_id,member.role,member.ordinal,side.id,
   side.reference_catalog_id,side.reference_revision_id,side.currency_code,side.clock_code,side.payment_rule_id,member.coefficient,
   policies->>'contract',policies->>'evidence',policies->>'rounding',policies->>'approval',policies->>'correction',policies->>'adjustmentDue',actor,publication,origin.effective_at)
   RETURNING * INTO lineage;
  INSERT INTO settlement_data.payment_lineage_members VALUES(catalog,identity,trade,origin.binding_id,1,origin.instrument_id,member.role,member.ordinal,member.side_id,actor,publication,origin.effective_at);
 END IF;
 FOR m IN SELECT value FROM jsonb_array_elements(mappings) LOOP
  PERFORM security.ppa_keys(m,ARRAY['bindingId','originRevision','instrumentId','sideId','role','ordinal']);
  SELECT * INTO binding FROM trading.ppa_revision_bindings b WHERE(b.catalog_id,b.trade_id,b.revision,b.binding_id)=(catalog,trade,head.revision,(m->>'bindingId')::uuid);
  SELECT * INTO member FROM trading.ppa_memberships WHERE(catalog_id,instrument_id,role,ordinal)=(catalog,binding.instrument_id,m->>'role',(m->>'ordinal')::integer);
  SELECT * INTO side FROM trading.ppa_sides WHERE(catalog_id,id)=(catalog,member.side_id);
  IF binding.binding_id=origin.binding_id AND m IS DISTINCT FROM original THEN RAISE EXCEPTION 'settlement_lineage';END IF;
  IF binding.binding_id IS NULL OR side.quantity_kind IS DISTINCT FROM 'slots' OR
   NOT EXISTS(SELECT 1 FROM trading.ppa_memberships pm JOIN trading.ppa_sides ps ON(ps.catalog_id,ps.id)=(pm.catalog_id,pm.side_id) WHERE(pm.catalog_id,pm.instrument_id,pm.role)=(catalog,binding.instrument_id,'energy_delivery') AND ps.quantity_kind='volume') OR binding.instrument_id IS DISTINCT FROM (m->>'instrumentId')::uuid OR member.side_id IS DISTINCT FROM (m->>'sideId')::uuid OR
   member.role IS DISTINCT FROM 'strike_payment' OR side.kind IS DISTINCT FROM 'fixed' OR member.coefficient IS DISTINCT FROM lineage.direction OR
   (side.reference_catalog_id,side.reference_revision_id,side.currency_code,side.clock_code,side.payment_rule_id) IS DISTINCT FROM
   (lineage.reference_catalog_id,lineage.reference_revision_id,lineage.currency_code,lineage.clock_code,lineage.payment_rule_id) OR
   (m->>'originRevision')::integer IS DISTINCT FROM (SELECT min(revision) FROM trading.ppa_revision_bindings b WHERE(b.catalog_id,b.trade_id,b.binding_id)=(catalog,trade,binding.binding_id))
  THEN RAISE EXCEPTION 'settlement_incompatible';END IF;
  SELECT * INTO old FROM settlement_data.payment_lineage_members l WHERE(l.catalog_id,l.trade_id,l.binding_id,l.role,l.ordinal)=(catalog,trade,binding.binding_id,member.role,member.ordinal);
  IF old.lineage_id IS NOT NULL AND (old.lineage_id,old.instrument_id,old.side_id,old.origin_revision) IS DISTINCT FROM(identity,binding.instrument_id,member.side_id,(m->>'originRevision')::integer)
  THEN RAISE EXCEPTION 'settlement_lineage_conflict';END IF;
  IF old.lineage_id IS NULL THEN
   IF publication IS NULL THEN
    SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
    INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
   END IF;
   INSERT INTO settlement_data.payment_lineage_members VALUES(catalog,identity,trade,binding.binding_id,(m->>'originRevision')::integer,binding.instrument_id,member.role,member.ordinal,member.side_id,actor,publication,(SELECT b.effective_at FROM trading.ppa_revision_bindings b WHERE(b.catalog_id,b.trade_id,b.revision,b.binding_id)=(catalog,trade,(m->>'originRevision')::integer,binding.binding_id)));
  END IF;
 END LOOP;
 PERFORM settlement_data.require_preview_authority(catalog,trade);
 RETURN jsonb_build_object('lineage',to_jsonb(lineage)||jsonb_build_object('direction',lineage.direction::text),'tradeHead',head.revision,'mappings',mappings);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_parameter_value THEN RAISE EXCEPTION 'settlement_input';
END$$;
CREATE FUNCTION settlement_data.read_payment_lineage(catalog uuid,identity uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE lineage settlement_data.payment_lineages;result jsonb;BEGIN
 SELECT * INTO lineage FROM settlement_data.payment_lineages WHERE(catalog_id,id)=(catalog,identity);
 PERFORM settlement_data.require_preview_authority(catalog,lineage.trade_id);
 IF lineage.id IS NULL OR NOT security.ppa_visible_at(catalog,lineage.publication_order,lineage.effective_at,context,as_of) THEN RAISE EXCEPTION 'settlement_lineage';END IF;
 result=jsonb_build_object('lineage',to_jsonb(lineage)||jsonb_build_object('direction',lineage.direction::text),'originalBinding',(SELECT to_jsonb(b) FROM trading.ppa_revision_bindings b WHERE(b.catalog_id,b.trade_id,b.revision,b.binding_id)=(catalog,lineage.trade_id,1,lineage.original_binding_id)),'mappings',coalesce((SELECT jsonb_agg(to_jsonb(m) ORDER BY m.origin_revision,m.binding_id) FROM settlement_data.payment_lineage_members m
  WHERE(m.catalog_id,m.lineage_id)=(catalog,identity) AND security.ppa_visible_at(catalog,m.publication_order,m.effective_at,context,as_of)),'[]'::jsonb));
 PERFORM settlement_data.require_preview_authority(catalog,lineage.trade_id);RETURN result;
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='settlement_data' AND p.proname IN('require_preview_authority','plan_payment_lineage','read_payment_lineage') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);
 END LOOP;
END$$;
COMMIT;
