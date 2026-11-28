-- Final daily billing of gas payments priced on a named daily or monthly index or on point publications (D463).
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$ DECLARE f record;p pg_proc;BEGIN
 FOR f IN SELECT * FROM(VALUES
 ('security.validate_gas_billing_request(jsonb)','db13bc2589ccd0d37dae659918c8140309bb8ae7f2fe19028dac960287db871e',false),
 ('security.replay_gas_billing(uuid,uuid,jsonb)','f873b67e2141d2d5b80c17b19b3b87668ceb9abcf4b48284a04d8cdaf1adb5d6',true),
 ('security.record_gas_billing(uuid,jsonb,timestamp with time zone,uuid,jsonb)','8866f678752844845c13d47b2c904e43e9b847bc74e5331e96b6bf55e96733f4',true),
 ('security.read_gas_billing_components(uuid,uuid,uuid,timestamp with time zone)','90333cfb5b658a0973debd1a2bafd07f2afd1aedadc198b9e2d3546d388fdcbb',true))v(signature,digest,secured) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=f.signature::regprocedure;
  IF encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>f.digest
   OR p.prosecdef IS DISTINCT FROM f.secured OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proowner<>(CASE WHEN f.secured THEN 'openetrm_security_owner'::regrole ELSE current_user::regrole END)
   OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
   OR EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.privilege_type<>'EXECUTE' OR a.is_grantable OR a.grantor<>p.proowner
    OR a.grantee NOT IN(p.proowner,'openetrm_security_owner'::regrole,CASE WHEN f.secured THEN 'openetrm_runtime'::regrole ELSE p.proowner END))
   OR (SELECT count(*) FROM aclexplode(p.proacl))<>2
  THEN RAISE EXCEPTION 'gas_indexed_billing_upgrade: unexpected helper %',p.oid::regprocedure;END IF;
 END LOOP;
 IF (SELECT array_agg(conname||' '||pg_get_constraintdef(oid) ORDER BY conname) FROM pg_constraint
  WHERE conrelid='trading.gas_cash_determinations'::regclass AND conname IN('gas_cash_determinations_profile_check','gas_cash_determinations_reason_check',
   'gas_cash_determinations_catalog_id_trade_id_cash_slot_day_m_key','gas_cash_determinations_catalog_id_trade_id_cash_slot_day_f_key'))
  IS DISTINCT FROM ARRAY[
   'gas_cash_determinations_catalog_id_trade_id_cash_slot_day_f_key UNIQUE (catalog_id, trade_id, cash_slot, day, facility_target_id)',
   'gas_cash_determinations_catalog_id_trade_id_cash_slot_day_m_key UNIQUE (catalog_id, trade_id, cash_slot, day, manual_revision_id)',
   'gas_cash_determinations_profile_check CHECK ((profile = ANY (ARRAY[''gas_final_daily_fixed_cash_v1''::text, ''gas_final_daily_entry_cash_v1''::text])))',
   'gas_cash_determinations_reason_check CHECK ((reason = ANY (ARRAY[''late_actuals''::text, ''quantity_correction''::text, ''allocation_correction''::text])))']
 THEN RAISE EXCEPTION 'gas_indexed_billing_upgrade: unexpected determination constraints';END IF;
END$guard$;

-- An indexed determination retains the published observation that priced it.
ALTER TABLE trading.gas_cash_determinations ADD COLUMN fixing_observation_id uuid;
ALTER TABLE trading.gas_cash_determinations
 ADD FOREIGN KEY(catalog_id,fixing_observation_id) REFERENCES market_data.observations(catalog_id,id),
 DROP CONSTRAINT gas_cash_determinations_profile_check,
 ADD CONSTRAINT gas_cash_determinations_profile_check
  CHECK(profile IN('gas_final_daily_fixed_cash_v1','gas_final_daily_entry_cash_v1','gas_final_daily_indexed_cash_v1')),
 DROP CONSTRAINT gas_cash_determinations_reason_check,
 ADD CONSTRAINT gas_cash_determinations_reason_check
  CHECK(reason IN('late_actuals','quantity_correction','allocation_correction','index_fixing_correction')),
 ADD CONSTRAINT gas_cash_determinations_fixing_profile
  CHECK((profile='gas_final_daily_indexed_cash_v1')=(fixing_observation_id IS NOT NULL)),
 -- One determination for each accepted quantity source and, for an indexed payment, each fixing. A fixed payment has no
 -- fixing, so its rule is unchanged: one determination for each quantity source.
 DROP CONSTRAINT gas_cash_determinations_catalog_id_trade_id_cash_slot_day_m_key,
 DROP CONSTRAINT gas_cash_determinations_catalog_id_trade_id_cash_slot_day_f_key,
 ADD CONSTRAINT gas_cash_determinations_source_fixing UNIQUE NULLS NOT DISTINCT
  (catalog_id,trade_id,cash_slot,day,manual_revision_id,facility_target_id,fixing_observation_id);

-- final_fixing_head_v1. One payment day's retained fixing binding selects the published observations of its exact series
-- and source. Of those known within the operation's market prefix and knowledge cutoff, and observed between the fixing
-- instant and the billing instant, the one with no such successor is the head. It must be alone and final.
CREATE FUNCTION security.gas_billing_fixing(catalog uuid,instrument uuid,cash integer,flow_day date,context uuid,as_of timestamptz,
 OUT observation_id uuid,OUT value numeric,OUT observed_at timestamptz,OUT corrects_id uuid,OUT payment_at timestamptz,OUT fixing_at timestamptz,
 OUT index_code text,OUT fixing_from date,OUT fixing_to date,OUT publication_state text)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE b trading.side_schedule_bindings;market_prefix integer;cut timestamptz;candidates uuid[];head market_data.observations;BEGIN
 -- A day outside the payment's retained rows has no fixing binding, or the lookup is not this payment's.
 IF(SELECT count(*) FROM trading.instrument_side_schedules s
  JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id)=(s.catalog_id,s.schedule_id)
  JOIN trading.side_schedule_bindings x ON(x.catalog_id,x.schedule_id,x.row_ordinal,x.port)=(r.catalog_id,r.schedule_id,r.ordinal,'fixing')
  WHERE(s.catalog_id,s.instrument_id,s.slot)=(catalog,instrument,cash) AND(r.delivery_from,r.delivery_to)=(flow_day,flow_day+1)
   AND x.kind='index_fixing')<>1 THEN RAISE EXCEPTION 'gas_billing_interval';END IF;
 SELECT x.* INTO STRICT b FROM trading.instrument_side_schedules s
  JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id)=(s.catalog_id,s.schedule_id)
  JOIN trading.side_schedule_bindings x ON(x.catalog_id,x.schedule_id,x.row_ordinal,x.port)=(r.catalog_id,r.schedule_id,r.ordinal,'fixing')
  WHERE(s.catalog_id,s.instrument_id,s.slot)=(catalog,instrument,cash) AND(r.delivery_from,r.delivery_to)=(flow_day,flow_day+1)
   AND x.kind='index_fixing';
 SELECT r.value_at INTO STRICT payment_at FROM trading.side_schedule_rows r
  WHERE(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal);
 -- Booking proved the fixing's quote is the payment's own. The billed price relies on it, so it is required here too.
 SELECT f.fixing_at INTO STRICT fixing_at FROM market_data.index_fixing_observations f
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(catalog,instrument,cash)
  JOIN market_data.gas_valuation_assignments a ON(a.catalog_id,a.revision_id,a.code)=(m.catalog_id,m.definition_revision_id,m.assignment_code)
  JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
  WHERE(f.catalog_id,f.revision_id,f.index_code,f.observation_from,f.observation_to,f.series_id,f.source_code)=
   (catalog,b.definition_revision_id,b.fixing_index_code,b.fixing_from,b.fixing_to,b.series_id,b.source_code)
   AND(f.reference_catalog_id,f.reference_revision_id,f.quote_code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code);
 SELECT m.known_through,t.known_at INTO market_prefix,cut FROM security.operation_knowledge t
  JOIN security.operation_markets m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date);
 IF payment_at IS NULL OR market_prefix IS NULL OR cut IS NULL THEN RAISE EXCEPTION 'gas_billing_context';END IF;
 SELECT array_agg(o.id) INTO candidates FROM market_data.observations o
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(o.catalog_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  WHERE(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code,o.role)=(catalog,b.definition_revision_id,b.series_id,b.source_code,'index_fixing')
   AND o.observed_at>=fixing_at AND o.observed_at<=as_of AND e.event_order<=market_prefix AND k.known_from<=cut
   AND NOT EXISTS(SELECT 1 FROM market_data.observations n
    JOIN market_data.knowledge_events ne ON(ne.catalog_id,ne.observation_id)=(n.catalog_id,n.id)
    JOIN market_data.market_knowledge_receipts nk ON(nk.catalog_id,nk.event_order,nk.publication_xid)=(ne.catalog_id,ne.event_order,ne.publication_xid)
    WHERE(n.catalog_id,n.corrects_id)=(o.catalog_id,o.id) AND n.observed_at<=as_of AND ne.event_order<=market_prefix AND nk.known_from<=cut);
 IF coalesce(cardinality(candidates),0)<>1 THEN RAISE EXCEPTION 'gas_billing_fixing';END IF;
 SELECT * INTO STRICT head FROM market_data.observations o WHERE(o.catalog_id,o.id)=(catalog,candidates[1]);
 IF head.publication_state<>'final' THEN RAISE EXCEPTION 'gas_billing_fixing';END IF;
 observation_id=head.id;value=head.value;observed_at=head.observed_at;corrects_id=head.corrects_id;
 publication_state=head.publication_state;index_code=b.fixing_index_code;fixing_from=b.fixing_from;fixing_to=b.fixing_to;
END$$;
ALTER FUNCTION security.gas_billing_fixing(uuid,uuid,integer,date,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.gas_billing_fixing(uuid,uuid,integer,date,uuid,timestamptz) FROM PUBLIC;

-- The reviewed fixing of one payment day, under the same admission as the other billing reads plus market read authority.
CREATE FUNCTION security.read_gas_billing_fixing(catalog uuid,trade uuid,cash integer,flow_day date,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_trade_revisions;until_at timestamptz;fx record;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_billing_denied' USING ERRCODE='42501';END IF;
 SELECT * INTO h FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.ppa_visible_at(r.catalog_id,r.publication_order,r.effective_at,context,as_of) ORDER BY r.revision DESC LIMIT 1;
 IF h.trade_id IS NULL THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 PERFORM security.read_instrument_side_schedule_headers(catalog,h.instrument_id,context,as_of);
 IF NOT EXISTS(SELECT 1 FROM trading.gas_index_cash_terms t JOIN trading.gas_component_terms m USING(catalog_id,instrument_id,slot)
  WHERE(t.catalog_id,t.instrument_id,t.slot)=(catalog,h.instrument_id,cash) AND(m.role,m.stage)=('indexed_consideration','delivery')
  AND t.observation_convention IN('named_index_daily_v1','named_index_monthly_v1','point_publications_v1')) THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 SELECT * INTO fx FROM security.gas_billing_fixing(catalog,h.instrument_id,cash,flow_day,context,as_of);
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_billing_denied' USING ERRCODE='42501';END IF;
 PERFORM lifecycle.require_read(catalog,until_at);
 RETURN jsonb_build_object('observationId',fx.observation_id,'value',trim_scale(fx.value)::text,'observedAt',fx.observed_at,'correctsId',fx.corrects_id,
  'publicationState',fx.publication_state,'paymentAt',fx.payment_at,'fixingAt',fx.fixing_at,'indexCode',fx.index_code,'fixingFrom',fx.fixing_from,'fixingTo',fx.fixing_to);
END$$;
ALTER FUNCTION security.read_gas_billing_fixing(uuid,uuid,integer,date,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_billing_fixing(uuid,uuid,integer,date,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_billing_fixing(uuid,uuid,integer,date,uuid,timestamptz) TO openetrm_runtime;

CREATE OR REPLACE FUNCTION security.validate_gas_billing_request(body jsonb)
 RETURNS void
 LANGUAGE plpgsql
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE p jsonb=body->'preview';c jsonb=p->'context';due text=body->>'correctionDueAt';key text;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR NOT(body?&ARRAY['preview','operationId','previewHash','sourceId','expectedPreviousId','correctionDueAt','reason','evidence']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(body) k WHERE k NOT IN('preview','operationId','previewHash','sourceId','expectedPreviousId','correctionDueAt','reason','evidence','fixingObservationId')) OR
 jsonb_typeof(p) IS DISTINCT FROM 'object' OR NOT(p?&ARRAY['context','tradeId','expectedRevision','cashSlot','day','selectionPolicy','profile','decimalPlaces','roundingPolicy']) OR
 EXISTS(SELECT 1 FROM jsonb_object_keys(p) k WHERE k NOT IN('context','tradeId','expectedRevision','cashSlot','day','selectionPolicy','profile','decimalPlaces','roundingPolicy')) OR
 jsonb_typeof(c) IS DISTINCT FROM 'object' OR c->>'knowledgeMode' IS DISTINCT FROM 'current' OR NOT(
  c->>'convention'='physical_gas_fixed_quantity_v4' AND c?&ARRAY['snapshotId','convention','knowledgeMode']
   AND NOT EXISTS(SELECT 1 FROM jsonb_object_keys(c) k WHERE k NOT IN('snapshotId','convention','knowledgeMode','contextId')) AND c->>'contextId' IS NULL
  OR c->>'convention'='gas_processing_instant_utc_v1' AND c?&ARRAY['convention','businessDate','evaluationAt','knowledgeMode','retained']
   AND NOT EXISTS(SELECT 1 FROM jsonb_object_keys(c) k WHERE k NOT IN('convention','businessDate','evaluationAt','knowledgeMode','retained')) AND c->'retained'='null'::jsonb
   AND c->>'businessDate'~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' AND(c->>'businessDate')::date::text=c->>'businessDate'
   AND c->>'evaluationAt'~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6}|[.][0-9]{6}0)?(Z|[+]00:00)$'
   AND(c->>'evaluationAt')::timestamptz<timestamptz '9999-12-31T00:00:00Z'
   AND((c->>'evaluationAt')::timestamptz AT TIME ZONE 'UTC')::date=(c->>'businessDate')::date
 ) IS TRUE OR
 jsonb_typeof(p->'day') IS DISTINCT FROM 'string' OR p->>'day'!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR
 (p->>'day')::date::text IS DISTINCT FROM p->>'day' OR
 due IS NOT NULL AND(due!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]([.][0-9]{1,6}|[.][0-9]{6}0)?(Z|[+]00:00)$' OR
 NOT isfinite(due::timestamptz) OR due::timestamptz>=timestamptz '9999-12-31T00:00:00Z') THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 -- The reviewed fixing is named exactly when the profile prices from one.
 IF(p->>'profile'='gas_final_daily_indexed_cash_v1') IS DISTINCT FROM(body?'fixingObservationId') OR body?'fixingObservationId' AND(
  jsonb_typeof(body->'fixingObservationId') IS DISTINCT FROM 'string' OR
  body->>'fixingObservationId'!~'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 FOREACH key IN ARRAY ARRAY['operationId','previewHash','sourceId','evidence'] LOOP
  IF jsonb_typeof(body->key) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 END LOOP;
 FOREACH key IN ARRAY ARRAY['expectedPreviousId','correctionDueAt','reason'] LOOP
  IF jsonb_typeof(body->key) NOT IN('string','null') THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 END LOOP;
 FOREACH key IN ARRAY ARRAY['cashSlot','expectedRevision','decimalPlaces'] LOOP
  IF jsonb_typeof(p->key) IS DISTINCT FROM 'number' THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 END LOOP;
END$function$;
CREATE OR REPLACE FUNCTION security.replay_gas_billing(catalog uuid, operation uuid, payload jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE old jsonb;prior trading.gas_billing_commands;fact trading.gas_cash_determinations;actor uuid;p jsonb=payload->'preview';BEGIN
 PERFORM security.validate_gas_billing_request(payload);
 PERFORM security.require_gas_quantity_trade(catalog,(payload#>>'{preview,tradeId}')::uuid,true);
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_quantity_trade(catalog,(payload#>>'{preview,tradeId}')::uuid,true);
 old=security.gas_billing_status(catalog,operation,payload);IF old IS NOT NULL THEN RETURN old;END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR operation IS DISTINCT FROM(payload->>'operationId')::uuid THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO prior FROM trading.gas_billing_commands c WHERE c.catalog_id=catalog AND c.actor_id=actor AND c.request-'operationId'=payload-'operationId' LIMIT 1;
 IF FOUND THEN
  old=prior.outcome||jsonb_build_object('operationId',operation);
 ELSE
  SELECT * INTO fact FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.trade_id,d.cash_slot,d.day)=(catalog,(p->>'tradeId')::uuid,(p->>'cashSlot')::integer,(p->>'day')::date)
   AND coalesce(d.manual_revision_id,d.facility_target_id)=(payload->>'sourceId')::uuid
   AND d.fixing_observation_id IS NOT DISTINCT FROM(payload->>'fixingObservationId')::uuid;
  IF NOT FOUND THEN RETURN NULL;END IF;
  IF(fact.trade_revision,fact.profile,fact.selection_policy,fact.rounding_policy,fact.decimal_places,fact.reason,fact.evidence) IS DISTINCT FROM
   ((p->>'expectedRevision')::integer,p->>'profile',p->>'selectionPolicy',p->>'roundingPolicy',(p->>'decimalPlaces')::integer,payload->>'reason',btrim(payload->>'evidence'))
   OR(fact.previous_id IS NULL AND payload->>'correctionDueAt' IS NOT NULL)
   OR(fact.previous_id IS NOT NULL AND fact.due_at IS DISTINCT FROM(payload->>'correctionDueAt')::timestamptz)
   THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
  old=jsonb_build_object('operationId',operation,'tradeId',fact.trade_id,'determinationId',fact.id,'previousId',fact.previous_id,'amount',trim_scale(fact.rounded_total)::text,'contribution',trim_scale(fact.contribution)::text);
  prior.trade_id=fact.trade_id;prior.determination_id=fact.id;
 END IF;
 IF(SELECT count(*) FROM trading.gas_billing_commands WHERE(catalog_id,trade_id)=(catalog,prior.trade_id))>=10000 THEN RAISE EXCEPTION 'gas_billing_limit';END IF;
 INSERT INTO trading.gas_billing_commands VALUES(catalog,operation,prior.trade_id,prior.determination_id,actor,payload,old);
 PERFORM security.require_gas_quantity_trade(catalog,prior.trade_id,true);RETURN old;
END$function$;
CREATE OR REPLACE FUNCTION security.record_gas_billing(catalog uuid, body jsonb, as_of timestamp with time zone, context uuid, calculation jsonb)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE p jsonb=body->'preview';trade uuid=(p->>'tradeId')::uuid;operation uuid=(body->>'operationId')::uuid;
 cash integer;flow_day date;policy text=p->>'selectionPolicy';places integer=(p->>'decimalPlaces')::integer;
 source uuid=(calculation->>'sourceId')::uuid;head trading.gas_trade_revisions;m trading.gas_component_terms;o trading.gas_delivery_obligations;
 a market_data.gas_valuation_assignments;quote ref_data.quote_specifications;prior trading.gas_cash_determinations;manual trading.gas_quantity_revisions;
 target trading.facility_adopted_targets;adoption trading.facility_quantity_adoptions;qty numeric;source_publication integer;source_effective timestamptz;
 finish timestamptz;valid_day boolean;due timestamptz;total numeric=(calculation->>'amount')::numeric;num numeric=(calculation->>'numerator')::numeric;den numeric=(calculation->>'denominator')::numeric;
 scaled numeric;whole numeric;expected numeric;quantity_n numeric;quantity_d numeric;cap_n numeric;cap_d numeric;remainder numeric;actor uuid;publication integer;identity uuid=gen_random_uuid();old jsonb;result jsonb;
 indexed boolean;fixing uuid;index_terms trading.gas_index_cash_terms;fx record;price numeric;
BEGIN
 PERFORM security.validate_gas_billing_request(body);
 indexed=(p->>'profile'='gas_final_daily_indexed_cash_v1');fixing=(body->>'fixingObservationId')::uuid;
 cash=(p->>'cashSlot')::integer;flow_day=(p->>'day')::date;
 old=security.replay_gas_billing(catalog,operation,body);IF old IS NOT NULL THEN RETURN old;END IF;
 IF as_of IS NULL OR NOT isfinite(as_of) OR context IS NULL OR
 (p->>'profile' IN('gas_final_daily_fixed_cash_v1','gas_final_daily_entry_cash_v1','gas_final_daily_indexed_cash_v1')) IS NOT TRUE OR
 indexed IS DISTINCT FROM(fixing IS NOT NULL) OR fixing IS DISTINCT FROM(calculation->>'fixingObservationId')::uuid OR p->>'roundingPolicy' IS DISTINCT FROM 'currency_minor_units_to_even_v1' OR
 policy IS NULL OR policy NOT IN('final_allocated_billing_v1','final_facility_allocated_billing_v1') OR
 p#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR
 (p#>>'{context,convention}'='gas_processing_instant_utc_v1' AND(
  (p#>>'{context,evaluationAt}')::timestamptz IS DISTINCT FROM as_of OR(p#>>'{context,businessDate}')::date IS DISTINCT FROM(as_of AT TIME ZONE 'UTC')::date)) OR
 places IS NULL OR places NOT BETWEEN 0 AND 12 OR length(btrim(body->>'evidence')) NOT BETWEEN 1 AND 500 OR body->>'evidence' IS NULL OR
 source IS DISTINCT FROM(body->>'sourceId')::uuid OR source IS NULL OR num IS NULL OR den IS NULL OR den<=0 OR total IS NULL THEN RAISE EXCEPTION 'gas_billing_input';END IF;
 SELECT * INTO head FROM trading.gas_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
 IF head.revision IS DISTINCT FROM(p->>'expectedRevision')::integer OR head.effective_at>as_of THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 SELECT * INTO m FROM trading.gas_component_terms WHERE(catalog_id,instrument_id,slot)=(catalog,head.instrument_id,cash);
 SELECT * INTO o FROM trading.gas_delivery_obligations WHERE(catalog_id,trade_id,instrument_id,slot)=(catalog,trade,head.instrument_id,m.source_slot);
 IF EXISTS(SELECT 1 FROM trading.gas_exercise_right_entries e
  WHERE(e.catalog_id,e.instrument_id)=(catalog,head.instrument_id) AND e.slot IN(m.slot,m.source_slot))
  OR EXISTS(SELECT 1 FROM trading.gas_swing_members e WHERE(e.catalog_id,e.instrument_id,e.slot)=(catalog,head.instrument_id,m.slot))
  OR EXISTS(SELECT 1 FROM trading.gas_swing_rights e WHERE(e.catalog_id,e.instrument_id,e.physical_slot)=(catalog,head.instrument_id,m.source_slot))
  THEN RAISE EXCEPTION 'gas_billing_rights';END IF;
 IF p->>'profile'='gas_final_daily_fixed_cash_v1' AND(m.share_n IS DISTINCT FROM m.share_d OR m.part_kind IS NOT NULL OR
  NOT EXISTS(SELECT 1 FROM trading.gas_component_terms s WHERE(s.catalog_id,s.instrument_id,s.slot)=(m.catalog_id,m.instrument_id,m.source_slot)
   AND(s.delivery_from,s.delivery_to)=(m.delivery_from,m.delivery_to))) THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 IF flow_day<m.delivery_from OR flow_day>=m.delivery_to THEN RAISE EXCEPTION 'gas_billing_interval';END IF;
 IF m.role IS NULL OR m.role NOT IN('consideration','fee','indexed_consideration') OR(m.role='indexed_consideration') IS DISTINCT FROM indexed
  OR m.stage IS DISTINCT FROM 'delivery' OR o.id IS NULL THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
 SELECT true,g.origin+((g.selection[1]+g.lengths[1])*g.step_ticks/10)*interval '1 microsecond' INTO valid_day,finish
 FROM trading.instrument_side_schedules s JOIN trading.side_schedule_grids g USING(catalog_id,schedule_id)
 WHERE(s.catalog_id,s.instrument_id,s.slot)=(catalog,head.instrument_id,o.slot) AND g.operating_days=ARRAY[flow_day-date '0001-01-01'] AND cardinality(g.selection)=1;
 IF valid_day IS DISTINCT FROM true OR finish>as_of THEN RAISE EXCEPTION 'gas_billing_interval';END IF;
 IF policy='final_allocated_billing_v1' THEN
  SELECT r.* INTO manual FROM trading.gas_quantity_series s JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id)
  WHERE(s.catalog_id,s.obligation_id,s.kind,s.source,s.stage,r.day)=(catalog,o.id,'allocated','manual_daily_v1','delivery',flow_day) ORDER BY r.revision DESC LIMIT 1;
  IF manual.id IS DISTINCT FROM source OR manual.status IS DISTINCT FROM 'final' THEN RAISE EXCEPTION 'gas_billing_source';END IF;
  qty=manual.quantity;source_publication=manual.publication_order;source_effective=manual.effective_at;
 ELSE
  SELECT h.* INTO target FROM trading.facility_adopted_targets h JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(h.catalog_id,h.ledger_id)
  WHERE(h.catalog_id,h.obligation_id,g.day)=(catalog,o.id,flow_day) ORDER BY h.revision DESC LIMIT 1;
  IF target.id IS DISTINCT FROM source OR target.state IS DISTINCT FROM 'final' THEN RAISE EXCEPTION 'gas_billing_source';END IF;
  SELECT * INTO STRICT adoption FROM trading.facility_quantity_adoptions WHERE(catalog_id,id)=(catalog,target.adoption_id);
  SELECT quantity INTO STRICT qty FROM trading.facility_gas_allocations WHERE(catalog_id,plan_id,obligation_id)=(catalog,target.plan_id,o.id);
  source_publication=adoption.publication_order;source_effective=adoption.effective_at;
 END IF;
 IF source_effective>as_of OR NOT EXISTS(SELECT 1 FROM security.operation_knowledge t JOIN security.operation_trading x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,source_publication)
 WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,context,(as_of AT TIME ZONE 'UTC')::date) AND source_publication<=x.known_through AND k.known_from<=t.known_at) THEN RAISE EXCEPTION 'gas_billing_context';END IF;
 SELECT * INTO STRICT a FROM market_data.gas_valuation_assignments WHERE(catalog_id,revision_id,code)=(catalog,m.definition_revision_id,m.assignment_code);
 SELECT q.* INTO STRICT quote FROM ref_data.quote_specifications q JOIN market_data.curves c ON(c.reference_catalog_id,c.reference_revision_id,c.quote_code)=(q.catalog_id,q.revision_id,q.code)
 WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,m.definition_revision_id,a.curve_code);
 IF NOT EXISTS(SELECT 1 FROM ref_data.currencies c WHERE(c.catalog_id,c.revision_id,c.code,c.display_minor_units)=(quote.catalog_id,quote.revision_id,quote.currency_code,places)) THEN RAISE EXCEPTION 'gas_billing_rounding';END IF;
 price=m.price;due=m.payment_at;
 IF indexed THEN
  -- The database selects the fixing itself; the reviewed identity must be that head.
  SELECT * INTO index_terms FROM trading.gas_index_cash_terms t WHERE(t.catalog_id,t.instrument_id,t.slot)=(catalog,head.instrument_id,cash);
  IF index_terms.observation_convention IS NULL OR index_terms.observation_convention NOT IN('named_index_daily_v1','named_index_monthly_v1','point_publications_v1')
   THEN RAISE EXCEPTION 'gas_billing_profile';END IF;
  IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_billing_denied' USING ERRCODE='42501';END IF;
  SELECT * INTO fx FROM security.gas_billing_fixing(catalog,head.instrument_id,cash,flow_day,context,as_of);
  IF fx.observation_id IS DISTINCT FROM fixing THEN RAISE EXCEPTION 'gas_billing_fixing';END IF;
  -- A new determination needs the fixing nothing has corrected, as its quantity is the head nothing has revised. A
  -- context that does not yet know a correction selects the superseded fixing and is refused here.
  IF EXISTS(SELECT 1 FROM market_data.observations n WHERE(n.catalog_id,n.corrects_id)=(catalog,fx.observation_id)) THEN RAISE EXCEPTION 'gas_billing_context';END IF;
  IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_billing_denied' USING ERRCODE='42501';END IF;
  price=fx.value+index_terms.spread;due=fx.payment_at;
 END IF;
 -- Independent rational integrity oracle. Levels use the held trade basis;
 -- final quantity already includes the physical membership and must not be rescaled.
 quantity_n=qty;quantity_d=1;
 IF p->>'profile' IN('gas_final_daily_entry_cash_v1','gas_final_daily_indexed_cash_v1') THEN
  IF m.part_kind IS NOT NULL THEN
   quantity_n=greatest(0,CASE m.part_kind WHEN 'band' THEN qty*m.part_lower_d-m.part_lower_n*head.quantity
    WHEN 'shortfall' THEN m.part_lower_n*head.quantity-qty*m.part_lower_d END);
   quantity_d=m.part_lower_d;
   IF m.part_upper_n IS NOT NULL THEN
    cap_n=(m.part_upper_n*m.part_lower_d-m.part_lower_n*m.part_upper_d)*head.quantity;
    cap_d=m.part_upper_d*m.part_lower_d;
    IF quantity_n*cap_d>cap_n*quantity_d THEN quantity_n=cap_n;quantity_d=cap_d;END IF;
   END IF;
  END IF;
  quantity_n=quantity_n*m.share_n;quantity_d=quantity_d*m.share_d;
 END IF;
 IF num*quantity_d<>quantity_n*price*quote.multiplier*m.direction*den THEN RAISE EXCEPTION 'gas_billing_amount';END IF;
 -- Divide only after cross-product equality, retaining the exact remainder for ties.
 scaled=abs(num)*power(10::numeric,places);whole=div(scaled,den);remainder=mod(scaled,den);
 expected=sign(num)*(whole+CASE WHEN remainder*2>den OR(remainder*2=den AND mod(whole,2)=1) THEN 1 ELSE 0 END)/power(10::numeric,places);
 IF total<>expected THEN RAISE EXCEPTION 'gas_billing_amount';END IF;
 SELECT * INTO prior FROM trading.gas_cash_determinations WHERE(catalog_id,trade_id,cash_slot,day)=(catalog,trade,cash,flow_day) ORDER BY ordinal DESC LIMIT 1;
 IF prior.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid OR prior.effective_at>as_of THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 IF prior.id IS NULL THEN
  IF body->>'reason' IS NOT NULL OR body->>'correctionDueAt' IS NOT NULL THEN RAISE EXCEPTION 'gas_billing_correction';END IF;
 ELSE
  IF (prior.selection_policy,prior.rounding_policy,prior.decimal_places,prior.instrument_id) IS DISTINCT FROM(policy,p->>'roundingPolicy',places,head.instrument_id) OR
   body->>'reason' IS NULL OR body->>'correctionDueAt' IS NULL OR
   -- A changed quantity source takes a quantity cause; the same source with a newer fixing takes the fixing cause.
   (CASE WHEN indexed AND coalesce(prior.manual_revision_id,prior.facility_target_id)=source
     THEN body->>'reason'='index_fixing_correction' AND prior.fixing_observation_id IS DISTINCT FROM fixing
     ELSE body->>'reason' IN('late_actuals','quantity_correction','allocation_correction') END) IS NOT TRUE THEN RAISE EXCEPTION 'gas_billing_correction';END IF;
  due=(body->>'correctionDueAt')::timestamptz;
  IF NOT isfinite(due) THEN RAISE EXCEPTION 'gas_billing_correction';END IF;
 END IF;
 IF(SELECT count(*) FROM trading.gas_billing_commands WHERE(catalog_id,trade_id)=(catalog,trade))>=10000 THEN RAISE EXCEPTION 'gas_billing_limit';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 INSERT INTO trading.gas_cash_determinations(catalog_id,id,trade_id,trade_revision,instrument_id,cash_slot,obligation_id,day,ordinal,previous_id,
 manual_revision_id,facility_target_id,profile,selection_policy,rounding_policy,decimal_places,reference_catalog_id,reference_revision_id,currency_code,
 exact_numerator,exact_denominator,rounded_total,contribution,due_at,reason,evidence,actor_id,context_id,publication_order,effective_at,fixing_observation_id)
 VALUES(catalog,identity,trade,head.revision,head.instrument_id,cash,o.id,flow_day,coalesce(prior.ordinal,0)+1,prior.id,
 CASE WHEN policy='final_allocated_billing_v1' THEN source END,CASE WHEN policy='final_facility_allocated_billing_v1' THEN source END,
 p->>'profile',policy,p->>'roundingPolicy',places,quote.catalog_id,quote.revision_id,quote.currency_code,num,den,total,total-coalesce(prior.rounded_total,0),due,body->>'reason',btrim(body->>'evidence'),actor,context,publication,as_of,fixing);
 result=jsonb_build_object('operationId',operation,'tradeId',trade,'determinationId',identity,'previousId',prior.id,'amount',trim_scale(total)::text,'contribution',trim_scale(total-coalesce(prior.rounded_total,0))::text);
 INSERT INTO trading.gas_billing_commands VALUES(catalog,operation,trade,identity,actor,body,result);
 PERFORM security.require_gas_quantity_trade(catalog,trade,true);RETURN result;
END$function$;
CREATE OR REPLACE FUNCTION security.read_gas_billing_components(catalog uuid, trade uuid, context uuid, as_of timestamp with time zone)
 RETURNS jsonb
 LANGUAGE plpgsql
 SECURITY DEFINER
 SET search_path TO 'pg_catalog'
AS $function$
DECLARE h trading.gas_trade_revisions;until_at timestamptz;result jsonb;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT * INTO h FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.ppa_visible_at(r.catalog_id,r.publication_order,r.effective_at,context,as_of) ORDER BY r.revision DESC LIMIT 1;
 IF h.trade_id IS NULL THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 PERFORM security.read_instrument_side_schedule_headers(catalog,h.instrument_id,context,as_of);
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.slot),'[]'::jsonb) INTO result FROM(
            SELECT h.revision,h.instrument_id,m.slot,m.code,m.role,m.stage,m.direction,m.price::text,m.payment_at,o.id obligation_id,
            m.share_n::text,m.share_d::text,m.part_kind,m.part_lower_n::text,m.part_lower_d::text,m.part_upper_n::text,m.part_upper_d::text,
            m.delivery_from,m.delivery_to,source.delivery_from source_from,source.delivery_to source_to,h.quantity::text held_base,
            EXISTS(SELECT 1 FROM trading.gas_exercise_right_entries e WHERE(e.catalog_id,e.instrument_id)=(m.catalog_id,m.instrument_id)
                AND e.slot IN(m.slot,m.source_slot))
            OR EXISTS(SELECT 1 FROM trading.gas_swing_members e WHERE(e.catalog_id,e.instrument_id,e.slot)=(m.catalog_id,m.instrument_id,m.slot))
            OR EXISTS(SELECT 1 FROM trading.gas_swing_rights e WHERE(e.catalog_id,e.instrument_id,e.physical_slot)=(m.catalog_id,m.instrument_id,m.source_slot)) right_dependent,
            ix.index_code,ix.observation_convention,ix.spread::text,ix.payment_day,
            q.catalog_id reference_catalog_id,q.revision_id reference_revision_id,q.quantity_code,q.currency_code,q.multiplier::text,cur.display_minor_units
            FROM LATERAL(SELECT * FROM trading.gas_component_terms
                WHERE(catalog_id,instrument_id)=(catalog,h.instrument_id) AND role IN('consideration','fee','indexed_consideration') OFFSET 0)m
            CROSS JOIN LATERAL(SELECT * FROM trading.gas_component_terms
                WHERE(catalog_id,instrument_id,slot)=(m.catalog_id,m.instrument_id,m.source_slot) OFFSET 0)source
            CROSS JOIN LATERAL(SELECT * FROM trading.gas_delivery_obligations
                WHERE(catalog_id,trade_id,instrument_id,slot)=(m.catalog_id,trade,m.instrument_id,m.source_slot) OFFSET 0)o
            CROSS JOIN LATERAL(SELECT * FROM market_data.gas_valuation_assignments
                WHERE(catalog_id,revision_id,code)=(m.catalog_id,m.definition_revision_id,m.assignment_code) OFFSET 0)a
            CROSS JOIN LATERAL(SELECT * FROM market_data.curves
                WHERE(catalog_id,revision_id,code)=(a.catalog_id,a.revision_id,a.curve_code) OFFSET 0)curve
            CROSS JOIN LATERAL(SELECT * FROM ref_data.quote_specifications
                WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,curve.quote_code) OFFSET 0)q
            LEFT JOIN trading.gas_index_cash_terms ix ON(ix.catalog_id,ix.instrument_id,ix.slot)=(m.catalog_id,m.instrument_id,m.slot)
            CROSS JOIN LATERAL(SELECT * FROM ref_data.currencies
                WHERE(catalog_id,revision_id,code)=(q.catalog_id,q.revision_id,q.currency_code) OFFSET 0)cur
)x;
 PERFORM lifecycle.require_read(catalog,until_at);RETURN result;
END$function$;
COMMIT;
