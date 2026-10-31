-- Explicit dated futures observations are a separate price source contract.
-- Existing scheduled swap authorization, operation identity and publication own the write.
BEGIN;
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE c record;BEGIN
 FOR c IN SELECT conname,pg_get_constraintdef(oid) definition FROM pg_constraint WHERE conrelid='trading.swap_side_periods'::regclass AND contype='c' LOOP
  IF c.definition LIKE '%index_observation_fixings_v1%' THEN
   EXECUTE format('ALTER TABLE trading.swap_side_periods DROP CONSTRAINT %I',c.conname);
   EXECUTE 'ALTER TABLE trading.swap_side_periods ADD CHECK('||substr(c.definition,8,length(c.definition)-8)||' OR (kind=''floating'' AND price_implementation=''contract_observation_average_v1'' AND fixed_price IS NULL AND index_code IS NULL AND settlement_mode IS NULL AND settlement_index_code IS NULL AND settlement_projection_policy IS NULL AND contract_id IS NULL AND custom_projection_id IS NULL AND observation_schedule_id IS NULL AND num_nonnulls(definition_revision_id,curve_code,first_fixing_at,averaging,spread)=5 AND averaging=''calendar_day_arithmetic'' AND first_fixing_at<payment_at))';
  END IF;
 END LOOP;
END$$;
CREATE TABLE trading.swap_contract_observation_terms (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,period_ordinal integer NOT NULL,
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 selection_mode text NOT NULL CHECK(selection_mode IN('exact','prompt_offset')),selected_contract_id uuid,prompt_offset integer,roll text,
 history_policy text NOT NULL CHECK(history_policy='visible_corrections_at_cutoff_v1'),
 role text NOT NULL CHECK(role='daily_settlement'),expiry_policy text NOT NULL CHECK(expiry_policy IN('require_live','carry_final_settlement')),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,side_id,period_ordinal),
 CHECK((selection_mode='exact' AND selected_contract_id IS NOT NULL AND prompt_offset IS NULL AND roll IS NULL) OR
  (selection_mode='prompt_offset' AND selected_contract_id IS NULL AND prompt_offset BETWEEN 0 AND 9999 AND roll IN('contract_expiry_roll_v1','explicit_contract_roll_v1'))),
 FOREIGN KEY(catalog_id,side_id,period_ordinal) REFERENCES trading.swap_side_periods(catalog_id,side_id,ordinal),
 FOREIGN KEY(catalog_id,definition_revision_id,selected_contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.swap_contract_roll_boundaries (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,period_ordinal integer NOT NULL,ordinal integer NOT NULL,
 definition_revision_id uuid NOT NULL,contract_id uuid NOT NULL,roll_at ref_data.instant NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,side_id,period_ordinal,ordinal),UNIQUE(catalog_id,side_id,period_ordinal,contract_id),
 FOREIGN KEY(catalog_id,side_id,period_ordinal) REFERENCES trading.swap_contract_observation_terms(catalog_id,side_id,period_ordinal),
 FOREIGN KEY(catalog_id,definition_revision_id,contract_id) REFERENCES market_data.contracts(catalog_id,revision_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id) REFERENCES ref_data.revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.swap_contract_observations (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,period_ordinal integer NOT NULL,ordinal integer NOT NULL,
 definition_revision_id uuid NOT NULL,curve_code ref_data.code NOT NULL,contract_id uuid NOT NULL,series_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,source_code ref_data.code NOT NULL,
 observation_from date NOT NULL,observation_to date NOT NULL,fixing_at ref_data.instant NOT NULL,selection_at ref_data.instant NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,side_id,period_ordinal,ordinal),CHECK(ordinal>=0 AND observation_to=observation_from+1 AND selection_at=fixing_at),
 FOREIGN KEY(catalog_id,side_id,period_ordinal) REFERENCES trading.swap_contract_observation_terms(catalog_id,side_id,period_ordinal),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(catalog_id,definition_revision_id,series_id) REFERENCES market_data.series(catalog_id,revision_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['swap_contract_observation_terms','swap_contract_roll_boundaries','swap_contract_observations'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('CREATE POLICY retained_instrument_read ON trading.%I FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(security.retained_side_visible(catalog_id,side_id))',t);
  EXECUTE format('CREATE POLICY reference_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(security.reference_version_visible(reference_catalog_id,reference_revision_id))',t);
  EXECUTE format('CREATE POLICY worker_service ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime,openetrm_risk_worker',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;

CREATE FUNCTION security.prepare_contract_observation_period(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,body jsonb,valuation timestamptz,publication_number integer,from_at timestamptz,compiled jsonb,input_pins uuid[])
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source jsonb=body->'contractObservations';selection jsonb=source->'selection';cv market_data.curves;q ref_data.quote_specifications;
 starts date=(body->>'deliveryFrom')::date;ends date=(body->>'deliveryTo')::date;payment timestamptz;rule uuid;spread_value numeric;
 row jsonb;authored jsonb;n integer;anchor timestamptz;selected uuid;expected uuid;series uuid;expiry timestamptz;eligible timestamptz;boundary jsonb;prior_roll timestamptz;roll_at timestamptz;
BEGIN
 IF jsonb_typeof(source) IS DISTINCT FROM 'object' OR NOT source ?& ARRAY['implementation','historyPolicy','role','expiryPolicy','selection','observations'] OR (SELECT count(*) FROM jsonb_object_keys(source))<>6 OR
  source->>'implementation' IS DISTINCT FROM 'contract_observation_source_v1' OR source->>'historyPolicy' IS DISTINCT FROM 'visible_corrections_at_cutoff_v1' OR source->>'role' IS DISTINCT FROM 'daily_settlement' OR source->>'expiryPolicy' IS NULL OR source->>'expiryPolicy' NOT IN('require_live','carry_final_settlement') OR
  body->>'index' IS NOT NULL OR body->>'settlement' IS NOT NULL OR body->>'customProjection' IS NOT NULL OR body->>'observationSchedule' IS NOT NULL OR body->>'firstFixingAt' IS NOT NULL OR body->>'paymentAt' IS NOT NULL OR body->>'averaging' IS NOT NULL OR body->>'resetFrequency' IS DISTINCT FROM 'daily' OR body->>'averagingMethod' IS DISTINCT FROM 'arithmetic' OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(body) key WHERE key NOT IN('curve','index','currency','deliveryFrom','deliveryTo','firstFixingAt','paymentAt','averaging','paymentSchedule','resetFrequency','averagingMethod','spread','customProjection','observationSchedule','settlement','contractObservations'))
 THEN RAISE EXCEPTION 'contract_observation_input';END IF;
 IF jsonb_typeof(selection) IS DISTINCT FROM 'object' OR NOT selection ?& ARRAY['mode','contract','promptOffset','roll','boundaries'] OR (SELECT count(*) FROM jsonb_object_keys(selection))<>5 THEN RAISE EXCEPTION 'contract_selection';END IF;
 IF selection->>'mode'='exact' THEN
  IF jsonb_typeof(selection->'contract') IS DISTINCT FROM 'object' OR NOT (selection->'contract') ?& ARRAY['revision','id'] OR (SELECT count(*) FROM jsonb_object_keys(selection->'contract'))<>2 OR
   jsonb_typeof(selection->'contract'->'revision') IS DISTINCT FROM 'object' OR NOT (selection->'contract'->'revision') ?& ARRAY['catalogId','revisionId'] OR (SELECT count(*) FROM jsonb_object_keys(selection->'contract'->'revision'))<>2 THEN RAISE EXCEPTION 'contract_selection';END IF;
  IF selection->>'promptOffset' IS NOT NULL OR selection->>'roll' IS NOT NULL OR selection->>'boundaries' IS NOT NULL OR
   (selection->'contract'->'revision'->>'catalogId')::uuid IS DISTINCT FROM catalog OR (selection->'contract'->'revision'->>'revisionId')::uuid IS DISTINCT FROM definition OR (selection->'contract'->>'id')::uuid IS NULL THEN RAISE EXCEPTION 'contract_selection';END IF;
 ELSIF selection->>'mode'='prompt_offset' THEN
  IF selection->>'contract' IS NOT NULL OR (selection->>'promptOffset')::integer IS NULL OR (selection->>'promptOffset')::integer NOT BETWEEN 0 AND 9999 OR selection->>'roll' IS NULL OR selection->>'roll' NOT IN('contract_expiry_roll_v1','explicit_contract_roll_v1') OR
   (selection->>'roll'='explicit_contract_roll_v1') IS DISTINCT FROM(selection->>'boundaries' IS NOT NULL) THEN RAISE EXCEPTION 'contract_selection';END IF;
 ELSE RAISE EXCEPTION 'contract_selection';END IF;
 SELECT * INTO cv FROM market_data.curves WHERE(catalog_id,revision_id,code)=(catalog,definition,body->>'curve');
 SELECT * INTO q FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(ref_catalog,ref_revision,cv.quote_code);
 IF cv.commodity_code IS DISTINCT FROM commodity OR cv.rule IS DISTINCT FROM 'direct' OR cv.direct_role IS DISTINCT FROM 'daily_settlement' OR q.kind IS DISTINCT FROM 'outright' OR q.currency_code IS DISTINCT FROM body->>'currency' THEN RAISE EXCEPTION 'contract_observation_source';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,definition,cv.code)) OR EXISTS(
  SELECT 1 FROM(SELECT m.ordinal,c.kind,c.expiry_at,c.futures_series_code,
    row_number() OVER(ORDER BY m.ordinal)-1 AS expected_ordinal,lag(c.expiry_at) OVER(ORDER BY m.ordinal) AS previous_expiry
   FROM market_data.curve_members m JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
   WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,definition,cv.code)) sequence
  WHERE sequence.ordinal<>sequence.expected_ordinal OR sequence.kind<>'future' OR sequence.futures_series_code IS NULL OR sequence.previous_expiry>=sequence.expiry_at)
 THEN RAISE EXCEPTION 'sequence_definition';END IF;
 IF source->>'expiryPolicy'='carry_final_settlement' AND NOT EXISTS(SELECT 1 FROM market_data.curve_expiry_rules WHERE(catalog_id,revision_id,curve_code,rule_version)=(catalog,definition,cv.code,'freeze_final_settlement_v1')) THEN RAISE EXCEPTION 'contract_observation_source';END IF;
 payment=security.swap_payment_date(ref_catalog,ref_revision,starts,ends,body->'paymentSchedule')::timestamp AT TIME ZONE 'UTC';spread_value=(body->>'spread')::numeric;
 IF starts IS NULL OR ends IS NULL OR NOT isfinite(starts) OR NOT isfinite(ends) OR extract(day FROM starts)<>1 OR ends IS DISTINCT FROM(starts+INTERVAL '1 month')::date OR payment IS NULL OR valuation>=payment OR spread_value IS NULL OR abs(spread_value)>=1e24 OR scale(spread_value)>18 OR
  jsonb_typeof(source->'observations') IS DISTINCT FROM 'array' OR jsonb_array_length(source->'observations')<>ends-starts OR jsonb_typeof(compiled) IS DISTINCT FROM 'array' OR jsonb_array_length(compiled)<>ends-starts THEN RAISE EXCEPTION 'contract_observation_input';END IF;
 IF selection->>'roll'='explicit_contract_roll_v1' THEN
  IF jsonb_array_length(selection->'boundaries')<>(SELECT count(*) FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=(catalog,definition,cv.code)) THEN RAISE EXCEPTION 'contract_roll';END IF;
  FOR n IN 0..jsonb_array_length(selection->'boundaries')-1 LOOP
   boundary=selection->'boundaries'->n;
   IF jsonb_typeof(boundary) IS DISTINCT FROM 'object' OR NOT boundary ?& ARRAY['contract','rollAt'] OR (SELECT count(*) FROM jsonb_object_keys(boundary))<>2 THEN RAISE EXCEPTION 'contract_roll';END IF;
   IF jsonb_typeof(boundary->'contract') IS DISTINCT FROM 'object' OR NOT (boundary->'contract') ?& ARRAY['revision','id'] OR (SELECT count(*) FROM jsonb_object_keys(boundary->'contract'))<>2 OR
    jsonb_typeof(boundary->'contract'->'revision') IS DISTINCT FROM 'object' OR NOT (boundary->'contract'->'revision') ?& ARRAY['catalogId','revisionId'] OR (SELECT count(*) FROM jsonb_object_keys(boundary->'contract'->'revision'))<>2 THEN RAISE EXCEPTION 'contract_roll';END IF;
   roll_at=(boundary->>'rollAt')::timestamptz;
   SELECT c.id,c.expiry_at,f.event_at INTO expected,expiry,eligible FROM market_data.curve_members m JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
    JOIN ref_data.futures_milestones f ON(f.catalog_id,f.revision_id,f.series_code,f.milestone)=(ref_catalog,ref_revision,c.futures_series_code,'first_trading') WHERE(m.catalog_id,m.revision_id,m.curve_code,m.ordinal)=(catalog,definition,cv.code,n);
   IF (boundary->'contract'->>'id')::uuid IS DISTINCT FROM expected OR (boundary->'contract'->'revision'->>'catalogId')::uuid IS DISTINCT FROM catalog OR (boundary->'contract'->'revision'->>'revisionId')::uuid IS DISTINCT FROM definition OR roll_at IS NULL OR eligible IS NULL OR roll_at<=eligible OR roll_at>expiry OR prior_roll>=roll_at THEN RAISE EXCEPTION 'contract_roll';END IF;
   prior_roll=roll_at;
  END LOOP;
 END IF;
 FOR n IN 0..ends-starts-1 LOOP
  row=compiled->n;authored=source->'observations'->n;
  IF jsonb_typeof(row) IS DISTINCT FROM 'object' OR NOT row ?& ARRAY['observationId','ordinal','observationFrom','observationTo','fixingAt','selectionAt','contractId','seriesId','sourceCode'] OR (SELECT count(*) FROM jsonb_object_keys(row))<>9 OR
   jsonb_typeof(authored) IS DISTINCT FROM 'object' OR NOT authored ?& ARRAY['ordinal','from','to','fixingAt'] OR (SELECT count(*) FROM jsonb_object_keys(authored))<>4 THEN RAISE EXCEPTION 'contract_observation_input';END IF;
  anchor=(authored->>'fixingAt')::timestamptz;selected=(row->>'contractId')::uuid;
  IF (authored->>'ordinal')::integer IS DISTINCT FROM n OR (authored->>'from')::date IS DISTINCT FROM starts+n OR (authored->>'to')::date IS DISTINCT FROM starts+n+1 OR anchor IS NULL OR anchor>=payment OR
   (row->>'ordinal')::integer IS DISTINCT FROM n OR (row->>'observationFrom')::date IS DISTINCT FROM starts+n OR (row->>'observationTo')::date IS DISTINCT FROM starts+n+1 OR (row->>'fixingAt')::timestamptz IS DISTINCT FROM anchor OR (row->>'selectionAt')::timestamptz IS DISTINCT FROM anchor THEN RAISE EXCEPTION 'contract_observation_input';END IF;
  IF selection->>'mode'='exact' THEN expected=(selection->'contract'->>'id')::uuid;
  ELSE
   SELECT c.id INTO expected FROM market_data.curve_members m JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,definition,cv.code) AND
     CASE WHEN selection->>'roll'='contract_expiry_roll_v1' THEN c.expiry_at ELSE (selection->'boundaries'->m.ordinal->>'rollAt')::timestamptz END>anchor
    ORDER BY m.ordinal OFFSET(selection->>'promptOffset')::integer LIMIT 1;
  END IF;
  IF selection->>'mode'='prompt_offset' THEN
   IF EXISTS(SELECT 1 FROM(
    SELECT c.futures_series_code FROM market_data.curve_members m JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
    WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,definition,cv.code) AND
     CASE WHEN selection->>'roll'='contract_expiry_roll_v1' THEN c.expiry_at ELSE (selection->'boundaries'->m.ordinal->>'rollAt')::timestamptz END>anchor
    ORDER BY m.ordinal LIMIT ((selection->>'promptOffset')::integer+1)) prefix
    LEFT JOIN ref_data.futures_milestones f ON(f.catalog_id,f.revision_id,f.series_code,f.milestone)=(ref_catalog,ref_revision,prefix.futures_series_code,'first_trading')
    WHERE f.value_state IS DISTINCT FROM 'known' OR f.event_at IS NULL OR f.event_at>anchor)
   THEN RAISE EXCEPTION 'sequence_ineligible';END IF;
  END IF;
  SELECT c.expiry_at,f.event_at,s.id INTO expiry,eligible,series FROM market_data.contracts c JOIN market_data.curve_members m ON(m.catalog_id,m.revision_id,m.contract_id)=(c.catalog_id,c.revision_id,c.id)
   JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=(c.catalog_id,c.revision_id,c.id,q.code)
   JOIN ref_data.futures_milestones f ON(f.catalog_id,f.revision_id,f.series_code,f.milestone)=(ref_catalog,ref_revision,c.futures_series_code,'first_trading')
   JOIN ref_data.futures_milestones l ON(l.catalog_id,l.revision_id,l.series_code,l.milestone)=(ref_catalog,ref_revision,c.futures_series_code,'last_trading')
   WHERE(c.catalog_id,c.revision_id,c.id,m.curve_code)=(catalog,definition,selected,cv.code) AND c.kind='future' AND f.value_state='known' AND l.value_state='known' AND l.event_at=c.expiry_at;
  IF expected IS NULL OR selected IS DISTINCT FROM expected OR eligible IS NULL OR anchor<eligible OR series IS DISTINCT FROM(row->>'seriesId')::uuid OR row->>'sourceCode' IS DISTINCT FROM cv.direct_source_code OR
   source->>'expiryPolicy'='require_live' AND anchor>=expiry THEN RAISE EXCEPTION 'contract_observation_source';END IF;
  IF (SELECT count(*) FROM market_data.observations o WHERE o.catalog_id=catalog AND o.definition_revision_id=definition AND o.series_id=series AND o.source_code=cv.direct_source_code
    AND o.id=(row->>'observationId')::uuid AND o.id=ANY(input_pins) AND o.publication_state='final'
    AND ((anchor>valuation AND least(anchor,valuation)<expiry) OR NOT EXISTS(
      SELECT 1 FROM market_data.observations successor WHERE successor.catalog_id=o.catalog_id AND successor.corrects_id=o.id
       AND security.market_fact_visible(successor.catalog_id,successor.definition_revision_id,successor.id))) AND
    CASE WHEN least(anchor,valuation)>=expiry THEN o.role='final_contract_settlement' AND o.observed_at>=expiry AND o.observed_at<=least(anchor,valuation)
     WHEN anchor<=valuation THEN o.role='daily_settlement' AND o.observed_at=anchor
     ELSE o.role='daily_settlement' AND o.observed_at<=valuation END)<>1 THEN RAISE EXCEPTION 'missing_observation';END IF;

 END LOOP;
 rule=security.retain_swap_payment_rule(catalog,ref_catalog,ref_revision,body->'paymentSchedule',publication_number,from_at);
 RETURN jsonb_build_object('kind','floating','definitionRevision',definition,'curve',cv.code,'contract',NULL,'index',NULL,'settlementMode',NULL,'settlementIndex',NULL,'settlementProjectionPolicy',NULL,
  'quote',q.code,'deliveryFrom',starts,'deliveryTo',ends,'firstFixingAt',source->'observations'->0->>'fixingAt','paymentAt',payment,'averaging','calendar_day_arithmetic','implementation','contract_observation_average_v1','paymentRule',rule,'customProjection',NULL,'spread',trim_scale(spread_value)::text,'observationSchedule',NULL,'projections','[]'::jsonb,'fixingEvidence',jsonb_build_object('observationPins',to_jsonb(input_pins)),
  'contractObservations',(SELECT jsonb_agg(value-'observationId' ORDER BY ordinality) FROM jsonb_array_elements(compiled) WITH ORDINALITY),'contractObservationTerms',source-'observations');
END$$;
DO $$DECLARE signature regprocedure='security.save_scheduled_swap(uuid,jsonb,jsonb,numeric,timestamptz,jsonb,uuid[])'::regprocedure;definition text;before text;after text;BEGIN
 definition=pg_get_functiondef(signature);
 definition=replace(definition,'DECLARE c security.request_contexts;','<<contract_observation_booking>> DECLARE c security.request_contexts;');
 before='parameters=security.prepare_floating_swap_period(catalog,definition,ref_catalog,ref_revision,side_commodity,period_body->''floating'',valuation,publication_number,from_at,period_pins);';
 after='IF period_body->''floating''->>''contractObservations'' IS NOT NULL THEN parameters=security.prepare_contract_observation_period(catalog,definition,ref_catalog,ref_revision,side_commodity,period_body->''floating'',valuation,publication_number,from_at,shape->''periods''->n->''contractObservations'',input_pins); ELSE '||before||' END IF;';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'contract_observation_prepare_patch';END IF;
 definition=replace(definition,before,after);
 before='  SELECT side_fixings||coalesce(jsonb_agg';
 after=$patch$
  INSERT INTO trading.swap_contract_observation_terms
   SELECT catalog,side_id,(x.ordinality-1)::integer,definition,ref_catalog,ref_revision,
    p->'contractObservationTerms'->'selection'->>'mode',(p->'contractObservationTerms'->'selection'->'contract'->>'id')::uuid,
    (p->'contractObservationTerms'->'selection'->>'promptOffset')::integer,p->'contractObservationTerms'->'selection'->>'roll',
    p->'contractObservationTerms'->>'historyPolicy',p->'contractObservationTerms'->>'role',p->'contractObservationTerms'->>'expiryPolicy',publication_number,from_at
   FROM jsonb_array_elements(children) WITH ORDINALITY x CROSS JOIN LATERAL(SELECT x.value->'terms' p) terms
   WHERE p->>'implementation'='contract_observation_average_v1' AND NOT EXISTS(SELECT 1 FROM trading.swap_contract_observation_terms t WHERE(t.catalog_id,t.side_id,t.period_ordinal)=(catalog,contract_observation_booking.side_id,(x.ordinality-1)::integer));
  INSERT INTO trading.swap_contract_roll_boundaries
   SELECT catalog,side_id,(x.ordinality-1)::integer,(b.ordinality-1)::integer,definition,(b.value->'contract'->>'id')::uuid,(b.value->>'rollAt')::timestamptz,
    ref_catalog,ref_revision,publication_number,from_at
   FROM jsonb_array_elements(children) WITH ORDINALITY x CROSS JOIN LATERAL(SELECT x.value->'terms' p) terms
   CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN p->'contractObservationTerms'->'selection'->>'boundaries' IS NULL THEN '[]'::jsonb ELSE p->'contractObservationTerms'->'selection'->'boundaries' END) WITH ORDINALITY b
   WHERE NOT EXISTS(SELECT 1 FROM trading.swap_contract_roll_boundaries t WHERE(t.catalog_id,t.side_id,t.period_ordinal,t.ordinal)=(catalog,contract_observation_booking.side_id,(x.ordinality-1)::integer,(b.ordinality-1)::integer));
  INSERT INTO trading.swap_contract_observations
   SELECT catalog,side_id,(x.ordinality-1)::integer,(r.value->>'ordinal')::integer,definition,p->>'curve',(r.value->>'contractId')::uuid,(r.value->>'seriesId')::uuid,
    ref_catalog,ref_revision,r.value->>'sourceCode',(r.value->>'observationFrom')::date,(r.value->>'observationTo')::date,(r.value->>'fixingAt')::timestamptz,(r.value->>'selectionAt')::timestamptz,publication_number,from_at
   FROM jsonb_array_elements(children) WITH ORDINALITY x CROSS JOIN LATERAL(SELECT x.value->'terms' p) terms
   CROSS JOIN LATERAL jsonb_array_elements(coalesce(p->'contractObservations','[]'::jsonb)) r
   WHERE NOT EXISTS(SELECT 1 FROM trading.swap_contract_observations t WHERE(t.catalog_id,t.side_id,t.period_ordinal,t.ordinal)=(catalog,contract_observation_booking.side_id,(x.ordinality-1)::integer,(r.value->>'ordinal')::integer));
  SELECT side_fixings||coalesce(jsonb_agg
$patch$;
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'contract_observation_retain_patch';END IF;
 EXECUTE replace(definition,before,after);
END$$;

ALTER FUNCTION trading.whole_term_swap_period_input(uuid,uuid,integer) RENAME TO indexed_whole_term_swap_period_input;
CREATE FUNCTION trading.whole_term_swap_period_input(catalog uuid,selected_side uuid,period integer) RETURNS jsonb
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE p trading.swap_side_periods;t trading.swap_contract_observation_terms;payment jsonb;quote ref_data.quote_specifications;observations jsonb;boundaries jsonb;BEGIN
 SELECT * INTO p FROM trading.swap_side_periods WHERE(catalog_id,side_id,ordinal)=(catalog,selected_side,period);
 IF p.price_implementation IS DISTINCT FROM 'contract_observation_average_v1' THEN RETURN trading.indexed_whole_term_swap_period_input(catalog,selected_side,period);END IF;
 SELECT * INTO STRICT t FROM trading.swap_contract_observation_terms WHERE(catalog_id,side_id,period_ordinal)=(catalog,selected_side,period);
 SELECT * INTO STRICT quote FROM ref_data.quote_specifications WHERE(catalog_id,revision_id,code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code);
 SELECT jsonb_build_object('frequency',r.frequency,'rule',r.rule,'days',r.days,'dayOfMonth',r.day_of_month,'dayBasis',r.day_basis,'shortMonth',r.short_month,'adjustment',r.adjustment,'calendar',r.calendar_code,'implementation',r.implementation) INTO payment FROM trading.swap_payment_rules r WHERE(r.catalog_id,r.id)=(p.catalog_id,p.payment_rule_id);
 SELECT jsonb_agg(jsonb_build_object('ordinal',o.ordinal,'from',o.observation_from,'to',o.observation_to,'fixingAt',o.fixing_at) ORDER BY o.ordinal) INTO observations FROM trading.swap_contract_observations o WHERE(o.catalog_id,o.side_id,o.period_ordinal)=(catalog,selected_side,period);
 SELECT jsonb_agg(jsonb_build_object('contract',jsonb_build_object('revision',jsonb_build_object('catalogId',b.catalog_id,'revisionId',b.definition_revision_id),'id',b.contract_id),'rollAt',b.roll_at) ORDER BY b.ordinal) INTO boundaries FROM trading.swap_contract_roll_boundaries b WHERE(b.catalog_id,b.side_id,b.period_ordinal)=(catalog,selected_side,period);
 RETURN jsonb_build_object('fixed',NULL,'floating',jsonb_build_object('curve',p.curve_code,'index',NULL,'settlement',NULL,'currency',quote.currency_code,'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,'paymentSchedule',payment,'spread',trim_scale(p.spread)::text,'resetFrequency','daily','averagingMethod','arithmetic',
  'contractObservations',jsonb_build_object('implementation','contract_observation_source_v1','historyPolicy',t.history_policy,'role',t.role,'expiryPolicy',t.expiry_policy,
   'selection',jsonb_build_object('mode',t.selection_mode,'contract',CASE WHEN t.selected_contract_id IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',t.catalog_id,'revisionId',t.definition_revision_id),'id',t.selected_contract_id) END,'promptOffset',t.prompt_offset,'roll',t.roll,'boundaries',boundaries),'observations',observations)));
END$$;
CREATE FUNCTION trading.contract_observation_evidence(catalog uuid,selected_side uuid,period integer) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_agg(to_jsonb(o)||jsonb_build_object('history_policy',t.history_policy,'role',t.role,'expiry_policy',t.expiry_policy,'implementation','contract_observation_source_v1') ORDER BY o.ordinal)
 FROM trading.swap_contract_observations o JOIN trading.swap_contract_observation_terms t USING(catalog_id,side_id,period_ordinal)
 WHERE(o.catalog_id,o.side_id,o.period_ordinal)=(catalog,selected_side,period)
$$;
DO $$DECLARE signature regprocedure='trading.scheduled_swap_terms(uuid,uuid,integer,uuid)'::regprocedure;definition text;before text;BEGIN
 definition=pg_get_functiondef(signature);before='''id'',p.id,''n'',trim_scale(p.quantity_numerator)::text';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'contract_observation_evidence_patch';END IF;
 EXECUTE replace(definition,before,'''contract_observations'',trading.contract_observation_evidence(catalog,p.side_id,p.ordinal),'||before);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,n.nspname,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE(n.nspname='security' AND p.proname='prepare_contract_observation_period') OR(n.nspname='trading' AND p.proname IN('whole_term_swap_period_input','indexed_whole_term_swap_period_input','contract_observation_evidence')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.nspname='trading' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime,openetrm_risk_worker',f.signature);END IF;
 END LOOP;
END$$;
-- A separately declared final-settlement rule also has a concrete dated-futures consumer.
DO $$DECLARE definition text;before text;after text;BEGIN
 definition=pg_get_functiondef('market_data.validate_interval_price_sources()'::regprocedure);
 before='IF EXISTS(SELECT 1 FROM market_data.curve_expiry_rules r
   WHERE(r.catalog_id,r.revision_id)=(NEW.catalog_id,NEW.id) AND NOT EXISTS(';
 after='IF EXISTS(SELECT 1 FROM market_data.curve_expiry_rules r
   WHERE(r.catalog_id,r.revision_id)=(NEW.catalog_id,NEW.id) AND NOT EXISTS(
    SELECT 1 FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(r.catalog_id,r.revision_id,r.curve_code)
     AND c.rule=''direct'' AND c.direct_role=''daily_settlement''
     AND EXISTS(SELECT 1 FROM market_data.curve_members m WHERE(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code))
     AND NOT EXISTS(SELECT 1 FROM market_data.curve_members m JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
      WHERE(m.catalog_id,m.revision_id,m.curve_code)=(c.catalog_id,c.revision_id,c.code) AND(k.kind<>''future'' OR EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(k.catalog_id,k.revision_id,k.id))))) AND NOT EXISTS(';
 IF strpos(definition,before)=0 THEN RAISE EXCEPTION 'contract_observation_expiry_qualification_patch';END IF;
 EXECUTE replace(replace(definition,before,after),'An expiry rule binds only a direct date-only block consumed by a shaped curve.','An expiry rule requires a qualified shaped block or a direct dated futures settlement source.');
END$$;
COMMIT;
