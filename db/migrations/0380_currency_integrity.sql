-- Exact currency/discount relationships and explicit currency-bound payment rounding.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE market_data.rate_curves ADD CONSTRAINT rate_curve_currency_identity
 UNIQUE(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,currency_code);
-- Validation refuses inconsistent populated installations without rewriting their facts.
ALTER TABLE trading.currency_discount_defaults ADD CONSTRAINT currency_discount_curve_currency
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,reference_catalog_id,reference_revision_id,currency_code)
 REFERENCES market_data.rate_curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,currency_code);
ALTER TABLE settlement_data.payment_lineages DROP CONSTRAINT payment_lineages_rounding_check,
 ADD COLUMN rounding_increment ref_data.exact_number,
 ADD COLUMN rounding_tie_rule text,
 ADD COLUMN rounding_stage text,
 ADD CONSTRAINT payment_lineage_rounding_terms CHECK(
  (rounding='cash_2dp_half_even_per_occurrence_v1' AND num_nonnulls(rounding_increment,rounding_tie_rule,rounding_stage)=0) OR
  (rounding='cash_explicit_increment_per_occurrence_v1' AND num_nulls(rounding_increment,rounding_tie_rule,rounding_stage)=0
   AND rounding_increment>0 AND rounding_tie_rule IN('to_even','away_from_zero') AND rounding_stage='payment_total'));
DROP FUNCTION settlement_data.plan_payment_lineage(uuid,uuid,integer,uuid,timestamptz,jsonb,jsonb,jsonb);
CREATE OR REPLACE FUNCTION settlement_data.plan_payment_lineage(catalog uuid,trade uuid,expected integer,context uuid,as_of timestamptz,original jsonb,mappings jsonb,policies jsonb,rounding_terms jsonb)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE scope jsonb;head trading.ppa_trade_revisions;origin trading.ppa_revision_bindings;member trading.ppa_memberships;side trading.ppa_sides;
 lineage settlement_data.payment_lineages;old settlement_data.payment_lineage_members;binding trading.ppa_revision_bindings;
 m jsonb;increment numeric;tie text;stage text;publication integer;actor uuid;identity uuid;BEGIN
 PERFORM settlement_data.require_preview_authority(catalog,trade);
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only';END IF;
 PERFORM security.ppa_keys(original,ARRAY['bindingId','originRevision','instrumentId','sideId','role','ordinal']);
 PERFORM security.ppa_keys(policies,ARRAY['contract','evidence','rounding','approval','correction','adjustmentDue']);
 IF (policies->>'contract' IS NULL OR policies->>'contract' NOT IN('ppa_monthly_gross_fixed_v1','ppa_monthly_gross_indexed_v1')) OR
  policies-ARRAY['contract','rounding'] IS DISTINCT FROM '{"contract":"ppa_monthly_gross_fixed_v1","evidence":"verified_meter_head_v1","rounding":"cash_2dp_half_even_per_occurrence_v1","approval":"two_principals_exact_action_24h_v1","correction":"restate_rounded_occurrence_v1","adjustmentDue":"original_due_for_adjustment_v1"}'::jsonb-ARRAY['contract','rounding']
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
  OR (member.role,side.kind) IS DISTINCT FROM
   (CASE policies->>'contract' WHEN 'ppa_monthly_gross_fixed_v1' THEN 'strike_payment' ELSE 'indexed_payment' END,
    CASE policies->>'contract' WHEN 'ppa_monthly_gross_fixed_v1' THEN 'fixed' ELSE 'indexed' END) OR side.quantity_kind IS DISTINCT FROM 'slots' OR
  NOT EXISTS(SELECT 1 FROM trading.ppa_memberships pm JOIN trading.ppa_sides ps ON(ps.catalog_id,ps.id)=(pm.catalog_id,pm.side_id) WHERE(pm.catalog_id,pm.instrument_id,pm.role)=(catalog,origin.instrument_id,'energy_delivery') AND ps.quantity_kind='volume')
 THEN RAISE EXCEPTION 'settlement_lineage';END IF;
 PERFORM security.ppa_keys(rounding_terms,ARRAY['currency','increment','tieRule','stage']);
 PERFORM security.ppa_keys(rounding_terms->'currency',ARRAY['revision','code']);
 PERFORM security.ppa_keys(rounding_terms->'currency'->'revision',ARRAY['catalogId','revisionId']);
 increment=(rounding_terms->>'increment')::ref_data.exact_number;tie=rounding_terms->>'tieRule';stage=rounding_terms->>'stage';
 IF increment IS NULL OR increment<=0 OR tie IS NULL OR tie NOT IN('to_even','away_from_zero') OR stage IS DISTINCT FROM 'payment_total' OR
  (side.reference_catalog_id,side.reference_revision_id,side.currency_code::text) IS DISTINCT FROM
  ((rounding_terms->'currency'->'revision'->>'catalogId')::uuid,(rounding_terms->'currency'->'revision'->>'revisionId')::uuid,rounding_terms->'currency'->>'code')
 THEN RAISE EXCEPTION 'settlement_rounding';END IF;
 SELECT * INTO lineage FROM settlement_data.payment_lineages l WHERE(l.catalog_id,l.trade_id,l.original_binding_id,l.original_role,l.original_ordinal)=
  (catalog,trade,origin.binding_id,member.role,member.ordinal);
 IF lineage.id IS NULL THEN
  IF policies->>'rounding' IS DISTINCT FROM 'cash_explicit_increment_per_occurrence_v1' THEN RAISE EXCEPTION 'settlement_rounding';END IF;
 ELSE
  IF (lineage.contract,lineage.evidence,lineage.rounding,lineage.approval,lineage.correction,lineage.adjustment_due) IS DISTINCT FROM
   (policies->>'contract',policies->>'evidence',policies->>'rounding',policies->>'approval',policies->>'correction',policies->>'adjustmentDue')
  THEN RAISE EXCEPTION 'settlement_policy';END IF;
  IF lineage.rounding='cash_2dp_half_even_per_occurrence_v1' THEN
   IF(increment,tie,stage) IS DISTINCT FROM(0.01::numeric,'to_even'::text,'payment_total'::text) THEN RAISE EXCEPTION 'settlement_rounding';END IF;
  ELSIF(lineage.rounding_increment,lineage.rounding_tie_rule,lineage.rounding_stage) IS DISTINCT FROM(increment,tie,stage)
  THEN RAISE EXCEPTION 'settlement_rounding';END IF;
 END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 identity=coalesce(lineage.id,gen_random_uuid());
 -- Administrative continuity is known when committed; its business anchor is the
 -- original immutable trade, never a new cash event or backdated acceptance.
 IF lineage.id IS NULL THEN
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  INSERT INTO settlement_data.payment_lineages(catalog_id,id,trade_id,original_revision,original_binding_id,original_instrument_id,original_role,original_ordinal,original_side_id,
   reference_catalog_id,reference_revision_id,currency_code,clock_code,payment_rule_id,direction,contract,evidence,rounding,approval,correction,adjustment_due,actor_id,publication_order,effective_at,
   rounding_increment,rounding_tie_rule,rounding_stage) VALUES(catalog,identity,trade,1,origin.binding_id,origin.instrument_id,member.role,member.ordinal,side.id,
   side.reference_catalog_id,side.reference_revision_id,side.currency_code,side.clock_code,side.payment_rule_id,member.coefficient,
   policies->>'contract',policies->>'evidence',policies->>'rounding',policies->>'approval',policies->>'correction',policies->>'adjustmentDue',actor,publication,origin.effective_at,increment,tie,stage)
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
   member.role IS DISTINCT FROM lineage.original_role OR side.kind IS DISTINCT FROM
    (CASE lineage.contract WHEN 'ppa_monthly_gross_fixed_v1' THEN 'fixed' ELSE 'indexed' END) OR member.coefficient IS DISTINCT FROM lineage.direction OR
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
 RETURN jsonb_build_object('lineage',to_jsonb(lineage)||jsonb_build_object('direction',lineage.direction::text,'rounding_increment',lineage.rounding_increment::text),'tradeHead',head.revision,'mappings',mappings);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR invalid_parameter_value THEN RAISE EXCEPTION 'settlement_input';
END$$;
ALTER FUNCTION settlement_data.plan_payment_lineage(uuid,uuid,integer,uuid,timestamptz,jsonb,jsonb,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.plan_payment_lineage(uuid,uuid,integer,uuid,timestamptz,jsonb,jsonb,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.plan_payment_lineage(uuid,uuid,integer,uuid,timestamptz,jsonb,jsonb,jsonb,jsonb) TO openetrm_runtime;
CREATE FUNCTION settlement_data.read_payment_lineages(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;
BEGIN
 PERFORM settlement_data.require_preview_authority(catalog,trade);
 SELECT coalesce(jsonb_agg(settlement_data.read_payment_lineage(catalog,l.id,context,as_of)->'lineage'
  ||jsonb_build_object('rounding_increment',l.rounding_increment::text) ORDER BY l.id),'[]'::jsonb) INTO result
 FROM settlement_data.payment_lineages l WHERE(l.catalog_id,l.trade_id)=(catalog,trade)
  AND security.ppa_visible_at(catalog,l.publication_order,l.effective_at,context,as_of);
 PERFORM settlement_data.require_preview_authority(catalog,trade);
 RETURN result;
END$$;
ALTER FUNCTION settlement_data.read_payment_lineages(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_payment_lineages(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.read_payment_lineages(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
