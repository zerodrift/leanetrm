-- Actual title/performance and its separately owned principal survive the
-- futures margin component. Source-known future performances remain in every
-- complete chronological principal prefix when a final price is corrected.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.gas_futures_delivery_performances(
 catalog_id uuid NOT NULL,id uuid NOT NULL,position_id uuid NOT NULL,business_reference text COLLATE "C" NOT NULL,
 quantity ref_data.exact_number NOT NULL CHECK(quantity>0),performed_at ref_data.instant NOT NULL,
 title_evidence_id uuid,receipt_lot_id uuid,actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,position_id,id),UNIQUE(catalog_id,position_id,business_reference),UNIQUE(catalog_id,receipt_lot_id),
 FOREIGN KEY(catalog_id,position_id) REFERENCES settlement_data.futures_delivery_obligations(catalog_id,position_id),
 FOREIGN KEY(catalog_id,title_evidence_id) REFERENCES trading.gas_owned_evidence(catalog_id,id),
 FOREIGN KEY(catalog_id,receipt_lot_id) REFERENCES trading.gas_owned_lots(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(length(business_reference) BETWEEN 1 AND 128 AND business_reference=btrim(business_reference) AND octet_length(business_reference)<=512),
 CHECK((title_evidence_id IS NULL)=(receipt_lot_id IS NULL))
);
CREATE TABLE trading.gas_futures_delivery_splits(
 catalog_id uuid NOT NULL,performance_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>0),consumption_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,performance_id,ordinal),UNIQUE(catalog_id,consumption_id),
 FOREIGN KEY(catalog_id,performance_id) REFERENCES trading.gas_futures_delivery_performances(catalog_id,id),
 FOREIGN KEY(catalog_id,consumption_id) REFERENCES trading.gas_owned_consumptions(catalog_id,id)
);
CREATE TABLE trading.gas_futures_delivery_principal_runs(
 catalog_id uuid NOT NULL,position_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),final_determination_id uuid NOT NULL,
 source_context_id uuid NOT NULL,as_of ref_data.instant NOT NULL,performance_count integer NOT NULL CHECK(performance_count BETWEEN 1 AND 16384),
 implementation text NOT NULL CHECK(implementation='delivery_cumulative_principal_total_v1'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,position_id,revision),UNIQUE(catalog_id,position_id,revision,publication_order),UNIQUE(catalog_id,position_id,publication_order),
 FOREIGN KEY(catalog_id,position_id) REFERENCES settlement_data.futures_delivery_obligations(catalog_id,position_id),
 FOREIGN KEY(catalog_id,final_determination_id) REFERENCES settlement_data.futures_margin_determinations(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_futures_delivery_principal_determinations(
 catalog_id uuid NOT NULL,id uuid NOT NULL,position_id uuid NOT NULL,run_revision integer NOT NULL,performance_id uuid NOT NULL,
 previous_id uuid,basis_id uuid,cumulative_quantity ref_data.exact_number NOT NULL CHECK(cumulative_quantity<>0),
 exact_total text NOT NULL CHECK(length(exact_total)<=4096),rounded_total ref_data.exact_number NOT NULL,contribution ref_data.exact_number NOT NULL,adjustment ref_data.exact_number NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,position_id,run_revision,performance_id),UNIQUE(catalog_id,position_id,performance_id,id),
 UNIQUE(catalog_id,position_id,run_revision,id),UNIQUE(catalog_id,position_id,id),
 FOREIGN KEY(catalog_id,position_id,run_revision) REFERENCES trading.gas_futures_delivery_principal_runs(catalog_id,position_id,revision),
 FOREIGN KEY(catalog_id,position_id,performance_id) REFERENCES trading.gas_futures_delivery_performances(catalog_id,position_id,id),
 FOREIGN KEY(catalog_id,position_id,performance_id,previous_id) REFERENCES trading.gas_futures_delivery_principal_determinations(catalog_id,position_id,performance_id,id),
 FOREIGN KEY(catalog_id,position_id,run_revision,basis_id) REFERENCES trading.gas_futures_delivery_principal_determinations(catalog_id,position_id,run_revision,id)
);
CREATE TABLE trading.gas_futures_delivery_cash(
 catalog_id uuid NOT NULL,id uuid NOT NULL,position_id uuid NOT NULL,determination_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,currency_code ref_data.code NOT NULL,
 amount ref_data.exact_number NOT NULL CHECK(amount<>0),due_at ref_data.instant NOT NULL,effective_at ref_data.instant NOT NULL,publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,determination_id),UNIQUE(catalog_id,position_id,id),
 FOREIGN KEY(catalog_id,position_id,determination_id) REFERENCES trading.gas_futures_delivery_principal_determinations(catalog_id,position_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_futures_delivery_payments(
 catalog_id uuid NOT NULL,id uuid NOT NULL,cash_id uuid NOT NULL,business_reference text COLLATE "C" NOT NULL,revision integer NOT NULL CHECK(revision>0),
 paid_at ref_data.instant NOT NULL,amount ref_data.exact_number NOT NULL CHECK(amount<>0),evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 2000),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,cash_id,business_reference,revision),
 FOREIGN KEY(catalog_id,cash_id) REFERENCES trading.gas_futures_delivery_cash(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(length(business_reference) BETWEEN 1 AND 128 AND business_reference=btrim(business_reference) AND octet_length(business_reference)<=512)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['gas_futures_delivery_performances','gas_futures_delivery_splits','gas_futures_delivery_principal_runs',
  'gas_futures_delivery_principal_determinations','gas_futures_delivery_cash','gas_futures_delivery_payments'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['gas_futures_delivery_performances','gas_futures_delivery_principal_runs','gas_futures_delivery_cash','gas_futures_delivery_payments'] LOOP
  EXECUTE format('CREATE TRIGGER native_publication BEFORE INSERT ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_publication()',t);
 END LOOP;
END$$;

CREATE FUNCTION trading.round_gas_delivery_principal(amount numeric,increment numeric,tie text) RETURNS numeric
 LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE remainder numeric;whole numeric;BEGIN
 IF amount IS NULL OR increment IS NULL OR increment<=0 OR tie NOT IN('to_even','away_from_zero') THEN RAISE EXCEPTION 'futures_delivery_rounding';END IF;
 remainder=mod(abs(amount),increment);whole=abs(amount)-remainder;
 IF remainder*2>increment OR remainder*2=increment AND(tie='away_from_zero' OR mod(whole/increment,2)=1) THEN whole=whole+increment;END IF;
 RETURN sign(amount)*whole;
END$$;
CREATE FUNCTION trading.guard_gas_futures_delivery_child() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE publication integer;BEGIN
 IF TG_TABLE_NAME='gas_futures_delivery_splits' THEN
  SELECT p.publication_order INTO STRICT publication FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,NEW.performance_id);
 ELSE
  SELECT r.publication_order INTO STRICT publication FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id,r.revision)=(NEW.catalog_id,NEW.position_id,NEW.run_revision);
 END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(NEW.catalog_id,publication,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'futures_delivery_sealed';END IF;RETURN NEW;
END$$;
CREATE TRIGGER sealed_parent BEFORE INSERT ON trading.gas_futures_delivery_splits FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_futures_delivery_child();
CREATE TRIGGER sealed_parent BEFORE INSERT ON trading.gas_futures_delivery_principal_determinations FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_futures_delivery_child();

CREATE FUNCTION trading.require_futures_delivery_continuation(proposal jsonb) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE item jsonb;key text;BEGIN
 PERFORM trading.gas_owned_projection_keys(proposal,ARRAY['implementation','positionId','expectedVersion','finalOccurrenceId','sourceObservationId','steps'],ARRAY['positionId']);
 IF proposal->>'implementation' IS DISTINCT FROM 'gas_futures_delivery_continuation_v1' OR pg_column_size(proposal)>8388608
 OR jsonb_typeof(proposal->'expectedVersion') IS DISTINCT FROM 'number' OR proposal->>'expectedVersion'!~'^(0|[1-9][0-9]{0,8})$'
 OR(proposal->>'finalOccurrenceId')::uuid IS NULL OR(proposal->>'finalOccurrenceId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
 OR(proposal->>'sourceObservationId')::uuid IS NULL OR(proposal->>'sourceObservationId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
 OR jsonb_typeof(proposal->'steps') IS DISTINCT FROM 'array' OR jsonb_array_length(proposal->'steps')>16384
 THEN RAISE EXCEPTION 'futures_delivery_continuation';END IF;
 IF(SELECT count(DISTINCT(value->>'performanceId')::uuid) FROM jsonb_array_elements(proposal->'steps'))<>jsonb_array_length(proposal->'steps') THEN RAISE EXCEPTION 'futures_delivery_continuation';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(proposal->'steps') LOOP
  PERFORM trading.gas_owned_projection_keys(item,ARRAY['performanceId','previousDeterminationId','cumulativeQuantity','exactTotal','roundedTotal','contribution','adjustment'],ARRAY['previousDeterminationId']);
  IF(item->>'performanceId')::uuid IS NULL OR(item->>'performanceId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
   OR jsonb_typeof(item->'exactTotal') IS DISTINCT FROM 'string' OR length(item->>'exactTotal')>4096
   OR(item->>'exactTotal')!~'^-?[0-9]+(\.[0-9]+|/[1-9][0-9]*)?$' THEN RAISE EXCEPTION 'futures_delivery_continuation';END IF;
  FOREACH key IN ARRAY ARRAY['cumulativeQuantity','roundedTotal','contribution','adjustment'] LOOP
   IF jsonb_typeof(item->key) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'futures_delivery_continuation';END IF;
   PERFORM trading.gas_owned_quantity(item->>key);
  END LOOP;
 END LOOP;
END$$;

-- The managed pure planner supplies every financial operand. This internal
-- writer only seals that closed proposal; the deferred complete-prefix check
-- below independently validates it against native facts and contractual terms.
CREATE FUNCTION trading.apply_futures_delivery_principal(catalog uuid,holding uuid,final_determination uuid,context uuid,as_of timestamptz,actor uuid,publication integer,proposal jsonb) RETURNS integer
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE link settlement_data.futures_delivery_obligations;profile settlement_data.futures_delivery_profiles;terms trading.gas_futures_delivery_terms;
 final settlement_data.futures_margin_determinations;old_run trading.gas_futures_delivery_principal_runs;performance trading.gas_futures_delivery_performances;
 old trading.gas_futures_delivery_principal_determinations;item jsonb;version integer;counted integer;id uuid;predecessor uuid=NULL;BEGIN
 PERFORM trading.require_futures_delivery_continuation(proposal);
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(catalog,publication,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'futures_delivery_publication';END IF;
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(catalog,holding);
 SELECT * INTO STRICT profile FROM settlement_data.futures_delivery_profiles p WHERE(p.catalog_id,p.id,p.revision)=(catalog,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT terms FROM trading.gas_futures_delivery_terms t WHERE(t.catalog_id,t.profile_id,t.profile_revision)=(catalog,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT final FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.id,d.trade_id,d.binding_id,d.occurrence_id)=
  (catalog,final_determination,link.trade_id,link.binding_id,link.final_occurrence_id);
 PERFORM trading.gas_owned_projection_keys(proposal,ARRAY['implementation','positionId','expectedVersion','finalOccurrenceId','sourceObservationId','steps'],ARRAY['positionId']);
 IF proposal->>'implementation' IS DISTINCT FROM 'gas_futures_delivery_continuation_v1' OR pg_column_size(proposal)>8388608
 OR jsonb_typeof(proposal->'expectedVersion') IS DISTINCT FROM 'number' OR proposal->>'expectedVersion'!~'^(0|[1-9][0-9]{0,8})$'
 OR(proposal->>'finalOccurrenceId')::uuid IS DISTINCT FROM link.final_occurrence_id OR(proposal->>'sourceObservationId')::uuid IS DISTINCT FROM final.source_observation_id
 OR(proposal->>'positionId')::uuid IS DISTINCT FROM(CASE WHEN EXISTS(SELECT 1 FROM trading.publications x JOIN trading.gas_owned_positions p
  ON(p.catalog_id,p.publication_order)=(x.catalog_id,x.publication_order) WHERE(p.catalog_id,p.id)=(catalog,holding) AND x.publication_xid=pg_current_xact_id()) THEN NULL::uuid ELSE holding END)
 OR jsonb_typeof(proposal->'steps') IS DISTINCT FROM 'array' OR jsonb_array_length(proposal->'steps')>16384
 THEN RAISE EXCEPTION 'futures_delivery_continuation';END IF;
 IF final.id IS DISTINCT FROM(SELECT d.id FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=
  (catalog,link.trade_id,link.binding_id,link.final_occurrence_id) ORDER BY d.run_revision DESC LIMIT 1) THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 IF final.publication_order<>publication THEN PERFORM settlement_data.require_futures_delivery_head(catalog,holding,context);END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.position_id)=(catalog,holding) AND p.publication_order<>publication
  AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE)
 THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 SELECT * INTO old_run FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id)=(catalog,holding) ORDER BY r.revision DESC LIMIT 1;
 IF old_run.revision IS NOT NULL AND old_run.publication_order<>publication AND trading.gas_owned_visible(catalog,old_run.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
 THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 IF(proposal->>'expectedVersion')::integer<>coalesce(old_run.revision,0) THEN RAISE EXCEPTION 'futures_delivery_revision';END IF;
 SELECT count(*) INTO counted FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.position_id)=(catalog,holding);
 IF counted<>jsonb_array_length(proposal->'steps') OR counted>16384 THEN RAISE EXCEPTION 'futures_delivery_principal_coverage';END IF;
 IF counted=0 THEN RETURN coalesce(old_run.revision,0);END IF;
 IF old_run.final_determination_id=final_determination AND old_run.performance_count=counted THEN RAISE EXCEPTION 'futures_delivery_unchanged_plan';END IF;
 version=coalesce(old_run.revision,0)+1;
 INSERT INTO trading.gas_futures_delivery_principal_runs VALUES(catalog,holding,version,final_determination,context,as_of,counted,'delivery_cumulative_principal_total_v1',actor,publication);
 FOR item IN SELECT value FROM jsonb_array_elements(proposal->'steps') LOOP
  PERFORM trading.gas_owned_projection_keys(item,ARRAY['performanceId','previousDeterminationId','cumulativeQuantity','exactTotal','roundedTotal','contribution','adjustment'],ARRAY['previousDeterminationId']);
  SELECT * INTO STRICT performance FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.id,p.position_id)=(catalog,(item->>'performanceId')::uuid,holding);
  SELECT * INTO old FROM trading.gas_futures_delivery_principal_determinations d WHERE(d.catalog_id,d.position_id,d.performance_id)=(catalog,holding,performance.id) ORDER BY d.run_revision DESC LIMIT 1;
  IF(item->>'previousDeterminationId')::uuid IS DISTINCT FROM old.id OR jsonb_typeof(item->'exactTotal') IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'futures_delivery_principal_lineage';END IF;
  id=gen_random_uuid();
  INSERT INTO trading.gas_futures_delivery_principal_determinations VALUES(catalog,id,holding,version,performance.id,old.id,predecessor,
   trading.gas_owned_quantity(item->>'cumulativeQuantity'),item->>'exactTotal',trading.gas_owned_quantity(item->>'roundedTotal'),
   trading.gas_owned_quantity(item->>'contribution'),trading.gas_owned_quantity(item->>'adjustment'));
  IF(item->>'adjustment')::numeric<>0 THEN INSERT INTO trading.gas_futures_delivery_cash VALUES(catalog,gen_random_uuid(),holding,id,
   profile.reference_catalog_id,profile.reference_revision_id,profile.currency_code,(item->>'adjustment')::numeric,terms.cash_due_at,performance.performed_at,publication);END IF;
  predecessor=id;
 END LOOP;RETURN version;
END$$;

-- A new accepted final may only commit after the reviewed full principal
-- continuation has been sealed in that same native publication.
CREATE FUNCTION trading.check_gas_futures_final_closure() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE link settlement_data.futures_delivery_obligations;BEGIN
 SELECT * INTO link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.trade_id,x.binding_id,x.final_occurrence_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id,NEW.occurrence_id);
 IF link.position_id IS NOT NULL AND EXISTS(SELECT 1 FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.position_id)=(NEW.catalog_id,link.position_id) AND p.publication_order<=NEW.publication_order)
 AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id,r.final_determination_id,r.publication_order)=(NEW.catalog_id,link.position_id,NEW.id,NEW.publication_order))
 THEN RAISE EXCEPTION 'futures_delivery_continuation_required';END IF;RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER complete_physical_principal AFTER INSERT ON settlement_data.futures_margin_determinations DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.check_gas_futures_final_closure();

CREATE FUNCTION trading.gas_delivery_exact_equal(value text,expected numeric) RETURNS boolean LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF value IS NULL OR length(value)>4096 THEN RETURN false;END IF;
 IF value~'^-?[0-9]+/[1-9][0-9]*$' THEN RETURN split_part(value,'/',1)::numeric=expected*split_part(value,'/',2)::numeric;END IF;
 IF value~'^-?[0-9]+(\.[0-9]+)?$' THEN RETURN value::numeric=expected;END IF;RETURN false;
END$$;

CREATE FUNCTION trading.check_gas_futures_performance() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE link settlement_data.futures_delivery_obligations;p trading.gas_owned_positions;g trading.gas_futures_delivery_terms;a trading.gas_owned_accounts;
 stock trading.gas_owned_positions;e trading.gas_owned_evidence;total numeric;BEGIN
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(NEW.catalog_id,NEW.position_id);
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.position_id);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(NEW.catalog_id,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,g.account_id);
 IF NEW.performed_at<p.effective_at OR NEW.performed_at<g.delivery_from OR NEW.performed_at>=g.delivery_to
  OR(SELECT sum(x.quantity) FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.position_id)=(NEW.catalog_id,NEW.position_id))>abs(p.quantity)
  OR NOT EXISTS(SELECT 1 FROM trading.gas_futures_delivery_principal_runs r WHERE(r.catalog_id,r.position_id,r.publication_order)=(NEW.catalog_id,NEW.position_id,NEW.publication_order))
 THEN RAISE EXCEPTION 'futures_delivery_performance';END IF;
 IF p.quantity>0 THEN
  SELECT * INTO STRICT stock FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.receipt_lot_id);
  SELECT * INTO STRICT e FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.title_evidence_id);
  IF(stock.account_id,stock.kind,stock.quantity,stock.effective_at,stock.evidence_id,stock.origin_policy,stock.publication_order) IS DISTINCT FROM
   (g.account_id,'owned_stock',NEW.quantity,NEW.performed_at,e.id,'physical_futures_delivery_v1',NEW.publication_order)
   OR e.kind<>'title_receipt' OR e.occurred_at<>NEW.performed_at OR e.account_id<>g.account_id OR e.quantity<=0
   OR EXISTS(SELECT 1 FROM trading.gas_futures_delivery_splits s WHERE(s.catalog_id,s.performance_id)=(NEW.catalog_id,NEW.id))
   OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.receipt_lot_id)=(NEW.catalog_id,NEW.receipt_lot_id))
  THEN RAISE EXCEPTION 'futures_delivery_title';END IF;
 ELSE
  IF NEW.receipt_lot_id IS NOT NULL OR NEW.title_evidence_id IS NOT NULL THEN RAISE EXCEPTION 'futures_delivery_supply';END IF;
  SELECT coalesce(sum(c.quantity),0) INTO total FROM trading.gas_futures_delivery_splits s JOIN trading.gas_owned_consumptions c ON(c.catalog_id,c.id)=(s.catalog_id,s.consumption_id)
   WHERE(s.catalog_id,s.performance_id)=(NEW.catalog_id,NEW.id);
  IF total<>NEW.quantity OR EXISTS(SELECT 1 FROM trading.gas_futures_delivery_splits s
   JOIN trading.gas_owned_consumptions c ON(c.catalog_id,c.id)=(s.catalog_id,s.consumption_id)
   JOIN trading.gas_owned_positions supplied_stock ON(supplied_stock.catalog_id,supplied_stock.id)=(c.catalog_id,c.lot_id)
   JOIN trading.gas_owned_accounts sa ON(sa.catalog_id,sa.id)=(supplied_stock.catalog_id,supplied_stock.account_id)
   WHERE(s.catalog_id,s.performance_id)=(NEW.catalog_id,NEW.id) AND(
    (c.kind,c.effective_at,c.publication_order) IS DISTINCT FROM('delivery',NEW.performed_at,NEW.publication_order)
    OR(sa.book_id,sa.legal_entity_id,sa.legal_entity_revision,sa.reference_catalog_id,sa.reference_revision_id,sa.quantity_code,sa.currency_code,sa.location_code)
     IS DISTINCT FROM(a.book_id,a.legal_entity_id,a.legal_entity_revision,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.currency_code,a.location_code)
    OR EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements x WHERE(x.catalog_id,x.consumption_id)=(c.catalog_id,c.id))
    OR EXISTS(SELECT 1 FROM trading.gas_owned_repayments x WHERE(x.catalog_id,x.consumption_id)=(c.catalog_id,c.id))))
  THEN RAISE EXCEPTION 'futures_delivery_supply';END IF;
 END IF;RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER complete_physical_performance AFTER INSERT ON trading.gas_futures_delivery_performances DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.check_gas_futures_performance();

CREATE FUNCTION trading.check_gas_futures_principal_run() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE link settlement_data.futures_delivery_obligations;p trading.gas_owned_positions;profile settlement_data.futures_delivery_profiles;g trading.gas_futures_delivery_terms;
 final settlement_data.futures_margin_determinations;price numeric;quantity numeric=0;rounded numeric=0;current_total numeric;
 performance trading.gas_futures_delivery_performances;d trading.gas_futures_delivery_principal_determinations;old trading.gas_futures_delivery_principal_determinations;
 predecessor uuid=NULL;seen integer=0;BEGIN
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(NEW.catalog_id,NEW.position_id);
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.position_id);
 SELECT * INTO STRICT profile FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.revision)=(NEW.catalog_id,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(NEW.catalog_id,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT final FROM settlement_data.futures_margin_determinations x WHERE(x.catalog_id,x.id,x.trade_id,x.binding_id,x.occurrence_id)=
  (NEW.catalog_id,NEW.final_determination_id,link.trade_id,link.binding_id,link.final_occurrence_id);
 SELECT s.value INTO STRICT price FROM market_data.observations s WHERE(s.catalog_id,s.id)=(NEW.catalog_id,final.source_observation_id);
 IF final.id IS DISTINCT FROM(SELECT x.id FROM settlement_data.futures_margin_determinations x WHERE(x.catalog_id,x.trade_id,x.binding_id,x.occurrence_id)=
  (NEW.catalog_id,link.trade_id,link.binding_id,link.final_occurrence_id) AND x.publication_order<=NEW.publication_order ORDER BY x.run_revision DESC LIMIT 1)
  OR NEW.revision<>1+coalesce((SELECT max(x.revision) FROM trading.gas_futures_delivery_principal_runs x WHERE(x.catalog_id,x.position_id)=(NEW.catalog_id,NEW.position_id) AND x.revision<NEW.revision),0)
 THEN RAISE EXCEPTION 'futures_delivery_principal_lineage';END IF;
 FOR performance IN SELECT x.* FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.position_id)=(NEW.catalog_id,NEW.position_id)
  AND x.publication_order<=NEW.publication_order ORDER BY x.performed_at,convert_to(x.business_reference,'UTF8') LOOP
  seen=seen+1;quantity=quantity+sign(p.quantity)*performance.quantity;
  SELECT * INTO STRICT d FROM trading.gas_futures_delivery_principal_determinations x WHERE(x.catalog_id,x.position_id,x.run_revision,x.performance_id)=
   (NEW.catalog_id,NEW.position_id,NEW.revision,performance.id);
  SELECT * INTO old FROM trading.gas_futures_delivery_principal_determinations x WHERE(x.catalog_id,x.position_id,x.performance_id)=(NEW.catalog_id,NEW.position_id,performance.id)
   AND x.run_revision<NEW.revision ORDER BY x.run_revision DESC LIMIT 1;
  current_total=trading.round_gas_delivery_principal(-price*profile.quote_multiplier*quantity,g.principal_increment,g.principal_tie);
  IF abs(quantity)>abs(p.quantity) OR(d.previous_id,d.basis_id,d.cumulative_quantity,d.rounded_total,d.contribution,d.adjustment) IS DISTINCT FROM
   (old.id,predecessor,quantity,current_total,current_total-rounded,current_total-rounded-coalesce(old.contribution,0))
   OR trading.gas_delivery_exact_equal(d.exact_total,-price*profile.quote_multiplier*quantity) IS NOT TRUE
   OR(SELECT count(*) FROM trading.gas_futures_delivery_cash c WHERE(c.catalog_id,c.determination_id)=(d.catalog_id,d.id))<>(CASE WHEN d.adjustment=0 THEN 0 ELSE 1 END)
  THEN RAISE EXCEPTION 'futures_delivery_principal_total';END IF;
  rounded=current_total;predecessor=d.id;
 END LOOP;
 IF seen<>NEW.performance_count OR(SELECT count(*) FROM trading.gas_futures_delivery_principal_determinations x WHERE(x.catalog_id,x.position_id,x.run_revision)=(NEW.catalog_id,NEW.position_id,NEW.revision))<>seen
 THEN RAISE EXCEPTION 'futures_delivery_principal_coverage';END IF;RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER complete_principal_prefix AFTER INSERT ON trading.gas_futures_delivery_principal_runs DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.check_gas_futures_principal_run();

CREATE FUNCTION trading.guard_gas_futures_cash() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d trading.gas_futures_delivery_principal_determinations;r trading.gas_futures_delivery_principal_runs;performance trading.gas_futures_delivery_performances;
 link settlement_data.futures_delivery_obligations;p settlement_data.futures_delivery_profiles;g trading.gas_futures_delivery_terms;BEGIN
 SELECT * INTO STRICT d FROM trading.gas_futures_delivery_principal_determinations x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.determination_id);
 SELECT * INTO STRICT r FROM trading.gas_futures_delivery_principal_runs x WHERE(x.catalog_id,x.position_id,x.revision)=(d.catalog_id,d.position_id,d.run_revision);
 SELECT * INTO STRICT performance FROM trading.gas_futures_delivery_performances x WHERE(x.catalog_id,x.id)=(d.catalog_id,d.performance_id);
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(d.catalog_id,d.position_id);
 SELECT * INTO STRICT p FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.revision)=(link.catalog_id,link.profile_id,link.profile_revision);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(link.catalog_id,link.profile_id,link.profile_revision);
 IF(NEW.position_id,NEW.publication_order,NEW.amount,NEW.reference_catalog_id,NEW.reference_revision_id,NEW.currency_code,NEW.effective_at,NEW.due_at) IS DISTINCT FROM
  (d.position_id,r.publication_order,d.adjustment,p.reference_catalog_id,p.reference_revision_id,p.currency_code,performance.performed_at,g.cash_due_at)
 THEN RAISE EXCEPTION 'futures_delivery_cash';END IF;RETURN NEW;
END$$;
CREATE TRIGGER exact_owned_cash BEFORE INSERT ON trading.gas_futures_delivery_cash FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_futures_cash();

CREATE FUNCTION trading.check_gas_futures_payment() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE cash trading.gas_futures_delivery_cash;paid numeric;BEGIN
 SELECT * INTO STRICT cash FROM trading.gas_futures_delivery_cash x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.cash_id);
 IF NEW.paid_at<cash.effective_at OR sign(NEW.amount)<>sign(cash.amount) THEN RAISE EXCEPTION 'futures_delivery_payment_origin';END IF;
 SELECT coalesce(sum(x.amount),0) INTO paid FROM(SELECT DISTINCT ON(p.business_reference)p.amount FROM trading.gas_futures_delivery_payments p
  WHERE(p.catalog_id,p.cash_id)=(NEW.catalog_id,NEW.cash_id) ORDER BY p.business_reference,p.revision DESC)x;
 IF abs(paid)>abs(cash.amount) THEN RAISE EXCEPTION 'futures_delivery_payment_capacity';END IF;RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER bounded_owned_payment AFTER INSERT ON trading.gas_futures_delivery_payments DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION trading.check_gas_futures_payment();

CREATE FUNCTION settlement_data.read_futures_delivery_principal(catalog uuid,trade uuid,binding uuid,profile uuid,profile_revision integer,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;gas_until timestamptz;link settlement_data.futures_delivery_obligations;holding trading.gas_owned_positions;
 run trading.gas_futures_delivery_principal_runs;result jsonb;performances jsonb;previous jsonb;prefix integer;cut timestamptz;BEGIN
 gas_until=security.authority_until('ReadGasOwnedHoldings');
 IF gas_until IS NULL OR NOT isfinite(gas_until) OR clock_timestamp()>=gas_until THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 until_at=least(gas_until,settlement_data.futures_margin_preview_authority(catalog));
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);PERFORM security.require_gas_owned_preview(catalog,until_at);
 result=settlement_data.futures_delivery_profile_document(catalog,profile,profile_revision,context,as_of);
 SELECT tc.known_through,t.known_at INTO STRICT prefix,cut FROM market_data.temporal_contexts t JOIN trading.contexts tc ON(tc.catalog_id,tc.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(catalog,context);
 IF NOT EXISTS(SELECT 1 FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policies p
 ON(p.catalog_id,p.id,p.revision)=(a.catalog_id,a.policy_id,a.policy_revision)
 JOIN trading.knowledge_receipts ak ON(ak.catalog_id,ak.publication_order)=(a.catalog_id,a.publication_order)
 JOIN trading.knowledge_receipts pk ON(pk.catalog_id,pk.publication_order)=(p.catalog_id,p.publication_order)
 WHERE(a.catalog_id,a.trade_id,a.binding_id)=(catalog,trade,binding) AND(p.delivery_profile_id,p.delivery_profile_revision)=(read_futures_delivery_principal.profile,read_futures_delivery_principal.profile_revision)
 AND a.publication_order<=prefix AND ak.known_from<=cut AND p.publication_order<=prefix AND pk.known_from<=cut)
 THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 SELECT x.* INTO link FROM settlement_data.futures_delivery_obligations x JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(x.catalog_id,x.position_id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
 WHERE(x.catalog_id,x.trade_id,x.binding_id)=(catalog,trade,binding) AND p.publication_order<=prefix AND k.known_from<=cut;
 IF link.position_id IS NOT NULL THEN
  IF(link.profile_id,link.profile_revision) IS DISTINCT FROM(read_futures_delivery_principal.profile,read_futures_delivery_principal.profile_revision) THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
  SELECT * INTO STRICT holding FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(catalog,link.position_id);
  SELECT r.* INTO run FROM trading.gas_futures_delivery_principal_runs r JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
   WHERE(r.catalog_id,r.position_id)=(catalog,link.position_id) AND r.publication_order<=prefix AND k.known_from<=cut ORDER BY r.revision DESC LIMIT 1;
  IF(SELECT count(*) FROM trading.gas_futures_delivery_performances p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE(p.catalog_id,p.position_id)=(catalog,link.position_id) AND p.publication_order<=prefix AND k.known_from<=cut)>16384 THEN RAISE EXCEPTION 'futures_delivery_capacity';END IF;
  SELECT coalesce(jsonb_agg(to_jsonb(p) ORDER BY p.performed_at,convert_to(p.business_reference,'UTF8')),'[]') INTO performances
   FROM trading.gas_futures_delivery_performances p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE(p.catalog_id,p.position_id)=(catalog,link.position_id) AND p.publication_order<=prefix AND k.known_from<=cut;
  SELECT coalesce(jsonb_agg(to_jsonb(d) ORDER BY d.performance_id),'[]') INTO previous FROM trading.gas_futures_delivery_principal_determinations d
   WHERE(d.catalog_id,d.position_id,d.run_revision)=(catalog,link.position_id,run.revision);
 END IF;
 result=result||jsonb_build_object('link',CASE WHEN link.position_id IS NULL THEN NULL ELSE to_jsonb(link) END,
  'position',CASE WHEN holding.id IS NULL THEN NULL ELSE to_jsonb(holding) END,
  'run',CASE WHEN run.revision IS NULL THEN NULL ELSE to_jsonb(run) END,'performances',coalesce(performances,'[]'),'previous',coalesce(previous,'[]'),'authorityUntil',until_at);
 IF pg_column_size(result)>16777216 THEN RAISE EXCEPTION 'futures_delivery_capacity';END IF;
 PERFORM settlement_data.require_futures_delivery_profile_read(catalog,profile,profile_revision,until_at);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);RETURN result;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['trading.round_gas_delivery_principal(numeric,numeric,text)'::regprocedure,
  'trading.require_futures_delivery_continuation(jsonb)'::regprocedure,'trading.guard_gas_futures_delivery_child()'::regprocedure,'trading.apply_futures_delivery_principal(uuid,uuid,uuid,uuid,timestamptz,uuid,integer,jsonb)'::regprocedure,
  'trading.check_gas_futures_final_closure()'::regprocedure,'trading.gas_delivery_exact_equal(text,numeric)'::regprocedure,'trading.check_gas_futures_performance()'::regprocedure,
  'settlement_data.read_futures_delivery_principal(uuid,uuid,uuid,uuid,integer,uuid,timestamptz)'::regprocedure,
  'trading.check_gas_futures_principal_run()'::regprocedure,'trading.guard_gas_futures_cash()'::regprocedure,'trading.check_gas_futures_payment()'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_delivery_principal(uuid,uuid,uuid,uuid,integer,uuid,timestamptz) TO openetrm_runtime;
-- Extend the exact native reader without changing its public identity or its
-- original source/authority contract. Principal is a distinct cash membership.
DO $upgrade$DECLARE f pg_proc;definition text;needle text='security.read_gas_owned_holdings(';BEGIN
 SELECT * INTO STRICT f FROM pg_proc WHERE oid='security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])'::regprocedure;
 IF md5(f.prosrc)<>'4e229bd3f85252f62a34917135f49053' OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.provariadic<>0 OR f.proargmodes IS NOT NULL OR f.prorettype<>'jsonb'::regtype OR f.proretset
  OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
  OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))x
   WHERE x.grantor<>f.proowner OR x.is_grantable OR x.privilege_type<>'EXECUTE' OR x.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
  OR to_regprocedure('security.read_gas_owned_holdings_before_delivery(uuid,uuid,timestamptz,uuid[])') IS NOT NULL
 THEN RAISE EXCEPTION 'futures_delivery_holdings_upgrade: unexpected helper security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])';END IF;
 definition=pg_get_functiondef(f.oid);
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'futures_delivery_holdings_upgrade: unexpected helper security.read_gas_owned_holdings(uuid,uuid,timestamptz,uuid[])';END IF;
 EXECUTE replace(definition,needle,'security.read_gas_owned_holdings_before_delivery(');
 ALTER FUNCTION security.read_gas_owned_holdings_before_delivery(uuid,uuid,timestamptz,uuid[]) OWNER TO openetrm_security_owner;
 REVOKE ALL ON FUNCTION security.read_gas_owned_holdings_before_delivery(uuid,uuid,timestamptz,uuid[]) FROM PUBLIC,openetrm_runtime;
END$upgrade$;
CREATE OR REPLACE FUNCTION security.read_gas_owned_holdings(catalog uuid,context uuid,at_time timestamptz,books uuid[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;positions jsonb='[]';item jsonb;remaining numeric;cash jsonb;payments jsonb;BEGIN
 result=security.read_gas_owned_holdings_before_delivery(catalog,context,at_time,books);
 FOR item IN SELECT value FROM jsonb_array_elements(result->'positions') LOOP
  IF item->>'kind'='delivery_obligation' THEN
   SELECT(item->>'quantity')::numeric-sign((item->>'quantity')::numeric)*coalesce(sum(p.quantity),0) INTO remaining
    FROM trading.gas_futures_delivery_performances p WHERE(p.catalog_id,p.position_id)=(catalog,(item->>'id')::uuid)
     AND security.trading_visible(p.catalog_id,p.publication_order,p.performed_at);
   IF remaining=0 THEN CONTINUE;END IF;
   item=item||jsonb_build_object('remaining_quantity',trim_scale(remaining)::text);
  END IF;
  positions=positions||jsonb_build_array(item);
 END LOOP;
 WITH selected AS(SELECT c.*,p.account_id,a.book_id,a.definition_revision_id FROM trading.gas_futures_delivery_cash c
  JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(c.catalog_id,c.position_id)
  JOIN trading.gas_owned_accounts a ON(a.catalog_id,a.id)=(p.catalog_id,p.account_id)
  WHERE c.catalog_id=catalog AND(cardinality(books)=0 OR a.book_id=ANY(books)) AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at)
   AND security.trading_visible(p.catalog_id,p.publication_order,p.effective_at) AND security.trading_visible(c.catalog_id,c.publication_order,c.effective_at)),
 allocations AS(SELECT p.*,k.known_from known_at,first_value(p.id) OVER(PARTITION BY p.catalog_id,p.cash_id,p.business_reference ORDER BY p.revision) allocation_id
  FROM trading.gas_futures_delivery_payments p JOIN selected c ON(c.catalog_id,c.id)=(p.catalog_id,p.cash_id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE security.trading_visible(p.catalog_id,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00'))
 SELECT coalesce((SELECT jsonb_agg(to_jsonb(c)||jsonb_build_object('amount',trim_scale(c.amount)::text) ORDER BY c.id) FROM selected c),'[]'),
 coalesce((SELECT jsonb_agg(to_jsonb(p)||jsonb_build_object('amount',trim_scale(p.amount)::text) ORDER BY p.cash_id,p.business_reference,p.revision) FROM allocations p),'[]') INTO cash,payments;
 IF pg_column_size(cash)+pg_column_size(payments)>16777216 THEN RAISE EXCEPTION 'futures_delivery_capacity';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,at_time,(result->>'authorityUntil')::timestamptz);
 RETURN result||jsonb_build_object('positions',positions,'deliveryCash',cash,'deliveryPayments',payments);
END$$;

COMMIT;
