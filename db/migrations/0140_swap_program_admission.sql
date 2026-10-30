-- Share retained swap term projection between history reads and booking admission.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM trading.side_schedule_request_seals) THEN RAISE EXCEPTION 'swap_program_rebooking_required';END IF;
END$$;
-- Expose the relational projection to the caller's planner. A per-row SQL
-- function with configuration settings is opaque and repeats the protected joins.
CREATE VIEW trading.swap_revision_projection WITH(security_invoker=true) AS
   SELECT r.*,facts.booker_id,facts.booker_version,facts.booker_name,facts.booker_login,facts.executed_at AS booked_at,facts.trade_date AS booking_date,facts.implementation AS booking_implementation,facts.label_captured_at AS booker_label_captured_at,le.code AS legal_entity_code,le.name AS legal_entity_name,cp.code AS counterparty_code,cp.name AS counterparty_name,trdr.code AS trader_code,trdr.name AS trader_name,tr.reference_number AS trade_reference,ir.reference_number AS instrument_reference,
    coalesce(i.fixed_side_id,i.first_floating_side_id,i.scheduled_first_id) AS fixed_side_id,i.first_floating_side_id,coalesce(i.floating_side_id,i.scheduled_second_id) AS floating_side_id,i.fixed_coefficient,i.discounting,i.implementation,
    i.scheduled_first_id,i.scheduled_second_id,i.second_quantity_numerator,i.second_quantity_denominator,
    CASE WHEN i.scheduled_first_id IS NULL THEN NULL ELSE trading.scheduled_swap_terms(r.catalog_id,r.trade_id,r.revision,i.instrument_id) END AS scheduled_data,
    f.fixed_price,f.delivery_from AS fixed_from,f.delivery_to AS fixed_to,f.payment_at AS fixed_payment,s.reference_catalog_id,s.reference_revision_id,s.spread AS floating_spread,
    CASE WHEN i.scheduled_first_id IS NULL THEN(SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE p.catalog_id=s.catalog_id AND p.side_id=s.id) ELSE(SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_period_projections p WHERE(p.catalog_id,p.side_id,p.period_ordinal)=(i.catalog_id,i.scheduled_second_id,0)) END AS projection_contracts,
    CASE WHEN i.scheduled_first_id IS NULL THEN(SELECT array_agg(p.weight ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE p.catalog_id=s.catalog_id AND p.side_id=s.id) ELSE(SELECT array_agg(p.weight ORDER BY p.ordinal) FROM trading.swap_period_projections p WHERE(p.catalog_id,p.side_id,p.period_ordinal)=(i.catalog_id,i.scheduled_second_id,0)) END AS projection_weights,
    q.currency_code AS quote_currency_code,q.commodity_code AS quote_commodity_code,
    s.implementation AS floating_implementation,s.definition_revision_id,s.curve_code,s.index_code,s.settlement_mode,s.settlement_index_code,s.settlement_projection_policy,s.quote_code,s.delivery_from AS floating_from,s.delivery_to AS floating_to,s.first_fixing_at,s.payment_at AS floating_payment,s.averaging,s.custom_projection_id,s.observation_schedule_id,
    NULL::jsonb AS observation_schedule,
    (SELECT jsonb_build_object('curve',a.curve_code,'index',a.index_code,'settlement',jsonb_build_object('mode',a.settlement_mode,'index',CASE WHEN a.settlement_mode='override' THEN a.settlement_index_code END,'projectionPolicy',a.settlement_projection_policy),'currency',q.currency_code,'deliveryFrom',a.delivery_from,'deliveryTo',a.delivery_to,
     'firstFixingAt',NULL,'paymentAt',NULL,
     'paymentSchedule',(SELECT jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) FROM trading.swap_payment_rules p WHERE(p.catalog_id,p.id)=(a.catalog_id,a.payment_rule_id)),
     'resetFrequency',CASE WHEN a.averaging='monthly_index_price' THEN 'monthly' ELSE 'daily' END,'averagingMethod',CASE a.averaging WHEN 'monthly_index_price' THEN 'none' WHEN 'calendar_day_arithmetic' THEN 'arithmetic' ELSE a.averaging END,'spread',a.spread::text,'observationSchedule',NULL,
     'customProjection',(SELECT jsonb_build_object('catalogId',p.catalog_id,'curveId',p.curve_id,'version',p.version,'outputNode',p.output_node,'position',p.position,'selection',p.selection,'implementation',p.implementation) FROM trading.custom_curve_projection_bindings p WHERE(p.catalog_id,p.id)=(a.catalog_id,a.custom_projection_id)))
     FROM trading.swap_floating_sides a JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,a.quote_code) WHERE(a.catalog_id,a.id)=(i.catalog_id,i.first_floating_side_id)) AS first_floating,
    a.first_fixing_at AS first_floating_fixing,a.custom_projection_id AS first_custom_projection_id,a.observation_schedule_id AS first_observation_schedule_id,
    (SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(i.catalog_id,i.first_floating_side_id)) AS first_projection_contracts,
    (SELECT array_agg(p.weight ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(i.catalog_id,i.first_floating_side_id)) AS first_projection_weights,
    (SELECT jsonb_build_object('catalogId',p.catalog_id,'curveId',p.curve_id,'version',p.version,'outputNode',p.output_node,'position',p.position,'selection',p.selection,'implementation',p.implementation) FROM trading.custom_curve_projection_bindings p WHERE(p.catalog_id,p.id)=(s.catalog_id,s.custom_projection_id)) AS custom_projection,
    (SELECT jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) FROM trading.swap_payment_rules p WHERE(p.catalog_id,p.id)=(f.catalog_id,f.payment_rule_id)) AS fixed_payment_rule,
    (SELECT jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) FROM trading.swap_payment_rules p WHERE(p.catalog_id,p.id)=(s.catalog_id,s.payment_rule_id)) AS floating_payment_rule
   FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
   LEFT JOIN LATERAL(
    SELECT f.fixed_price,f.delivery_from,f.delivery_to,f.payment_at,f.payment_rule_id,f.catalog_id,f.id FROM trading.swap_fixed_sides f WHERE(f.catalog_id,f.id)=(i.catalog_id,i.fixed_side_id)
    UNION ALL
    SELECT p.fixed_price,p.delivery_from,p.delivery_to,p.payment_at,p.payment_rule_id,p.catalog_id,p.id FROM trading.swap_side_periods p WHERE(p.catalog_id,p.side_id,p.ordinal)=(i.catalog_id,i.scheduled_first_id,0) AND p.kind='fixed'
   )f ON true
   JOIN LATERAL(
    SELECT s.reference_catalog_id,s.reference_revision_id,s.spread,s.catalog_id,s.id,s.implementation,s.definition_revision_id,s.curve_code,s.index_code,s.settlement_mode,s.settlement_index_code,s.settlement_projection_policy,s.quote_code,
     s.delivery_from,s.delivery_to,s.first_fixing_at,s.payment_at,s.averaging,s.custom_projection_id,s.observation_schedule_id,s.payment_rule_id FROM trading.swap_floating_sides s WHERE(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
    UNION ALL
    SELECT p.reference_catalog_id,p.reference_revision_id,p.spread,p.catalog_id,p.id,p.price_implementation,p.definition_revision_id,p.curve_code,p.index_code,p.settlement_mode,p.settlement_index_code,p.settlement_projection_policy,p.quote_code,
     p.delivery_from,p.delivery_to,p.first_fixing_at,p.payment_at,p.averaging,p.custom_projection_id,p.observation_schedule_id,p.payment_rule_id FROM trading.swap_side_periods p WHERE(p.catalog_id,p.side_id,p.ordinal)=(i.catalog_id,i.scheduled_second_id,0) AND p.kind='floating'
   )s ON true
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
   LEFT JOIN trading.swap_floating_sides a ON(a.catalog_id,a.id)=(i.catalog_id,i.first_floating_side_id)
   LEFT JOIN trading.booking_facts facts ON(facts.catalog_id,facts.trade_id)=(r.catalog_id,r.trade_id)
   LEFT JOIN trading.legal_entities le ON(le.catalog_id,le.id,le.revision)=(r.catalog_id,r.legal_entity_id,r.legal_entity_revision)
   LEFT JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
   LEFT JOIN trading.traders trdr ON(trdr.catalog_id,trdr.id,trdr.revision)=(r.catalog_id,r.trader_id,r.trader_revision)
   JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
   JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(r.catalog_id,r.instrument_id);
ALTER VIEW trading.swap_revision_projection OWNER TO openetrm_security_owner;
REVOKE ALL ON trading.swap_revision_projection FROM PUBLIC;
GRANT SELECT ON trading.swap_revision_projection TO openetrm_runtime,openetrm_risk_worker,openetrm_security_owner;
CREATE FUNCTION trading.swap_revision_terms(catalog uuid,trade uuid,selected_revision integer DEFAULT NULL) RETURNS SETOF jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT to_jsonb(v) FROM trading.swap_revision_projection v
 WHERE v.catalog_id=catalog AND(trade IS NULL OR v.trade_id=trade) AND(selected_revision IS NULL OR v.revision=selected_revision)
 ORDER BY v.trade_id,v.revision
$$;
CREATE FUNCTION trading.swap_revision_rows(catalog uuid,trade uuid,selected_revision integer DEFAULT NULL) RETURNS SETOF jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT x||jsonb_build_object('recorded_at',k.known_from)
 FROM trading.swap_revision_terms(catalog,trade,selected_revision) x
 JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,(x->>'publication_order')::bigint)
 ORDER BY (x->>'trade_id')::uuid,(x->>'revision')::integer
$$;
CREATE FUNCTION trading.swap_observation_terms(catalog uuid,ids uuid[]) RETURNS SETOF jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
   SELECT jsonb_build_object('id',o.id,'terms',jsonb_build_object('implementation',o.implementation,
    'calendar',CASE WHEN o.calendar_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',o.reference_catalog_id,'revisionId',o.reference_revision_id),'code',o.calendar_code) END,
    'quantities',CASE WHEN o.averaging='quantity_weighted' THEN(SELECT jsonb_agg(jsonb_build_object('from',k.delivery_from,'to',k.delivery_to,'quantity',jsonb_build_object('value',trim_scale(q.amount)::text,'specification',jsonb_build_object('revision',jsonb_build_object('catalogId',q.reference_catalog_id,'revisionId',q.reference_revision_id),'code',q.quantity_code))) ORDER BY q.ordinal)
     FROM trading.swap_observation_quantities q JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(q.catalog_id,q.definition_revision_id,q.contract_id) WHERE(q.catalog_id,q.schedule_id)=(o.catalog_id,o.id)) ELSE NULL END))
   FROM trading.swap_observation_schedules o WHERE o.catalog_id=catalog AND o.id=ANY(ids)
$$;
ALTER FUNCTION trading.swap_revision_terms(uuid,uuid,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.swap_revision_rows(uuid,uuid,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.swap_observation_terms(uuid,uuid[]) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.swap_revision_terms(uuid,uuid,integer),trading.swap_revision_rows(uuid,uuid,integer),trading.swap_observation_terms(uuid,uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.swap_revision_terms(uuid,uuid,integer),trading.swap_revision_rows(uuid,uuid,integer),trading.swap_observation_terms(uuid,uuid[]) TO openetrm_runtime,openetrm_risk_worker;

-- The accepted booking and staging use the same typed command normalization.
CREATE FUNCTION trading.composed_swap_command(trade uuid,expected integer,book uuid,definition uuid,ref_catalog uuid,ref_revision uuid,commodity text,fixed_body jsonb,first_body jsonb,float_body jsonb,quantity numeric,coefficient numeric,discounting boolean,valuation timestamptz,effective timestamptz,reason text,implementation text,memo text,booking jsonb) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('trade',trade,'expected',expected,'book',book,'definition',definition,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,'commodity',commodity,
  'fixed',fixed_body||jsonb_build_object('price',(fixed_body->>'price')::numeric),'firstFloating',first_body||jsonb_build_object('spread',(first_body->>'spread')::numeric),'floating',float_body||jsonb_build_object('spread',(float_body->>'spread')::numeric),'quantity',quantity,'coefficient',coefficient,'discounting',discounting,'valuation',valuation,'effective',effective,'reason',reason,'implementation',implementation,'memo',memo,'booking',booking,'schema','independent_swap_sides_v1');
$$;
CREATE FUNCTION trading.scheduled_swap_command(body jsonb,valuation timestamptz,booking jsonb) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT jsonb_build_object('schema','scheduled_financial_swap_booking_v1','body',body,'valuation',valuation,'booking',booking);
$$;
DO $migration$
DECLARE signature regprocedure;definition text;before text;
BEGIN
 SELECT p.oid::regprocedure INTO STRICT signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='security' AND p.proname='save_composed_swap';
 definition=pg_get_functiondef(signature);before=$before$ request_value=jsonb_build_object('trade',trade,'expected',expected,'book',book,'definition',definition,'referenceCatalog',ref_catalog,'referenceRevision',ref_revision,'commodity',commodity,
  'fixed',fixed_body||jsonb_build_object('price',(fixed_body->>'price')::numeric),'firstFloating',first_body||jsonb_build_object('spread',(first_body->>'spread')::numeric),'floating',float_body||jsonb_build_object('spread',(float_body->>'spread')::numeric),'quantity',quantity,'coefficient',coefficient,'discounting',discounting,'valuation',valuation,'effective',effective,'reason',reason,'implementation',implementation,'memo',memo,'booking',booking,'schema','independent_swap_sides_v1');$before$;
 IF position(before IN definition)=0 THEN RAISE EXCEPTION 'swap_command_upgrade_contract';END IF;
 EXECUTE replace(definition,before,' request_value=trading.composed_swap_command(trade,expected,book,definition,ref_catalog,ref_revision,commodity,fixed_body,first_body,float_body,quantity,coefficient,discounting,valuation,effective,reason,implementation,memo,booking);');
 SELECT p.oid::regprocedure INTO STRICT signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
 WHERE n.nspname='security' AND p.proname='save_scheduled_swap';
 definition=pg_get_functiondef(signature);before=$before$ request_value=jsonb_build_object('schema','scheduled_financial_swap_booking_v1','body',body,'valuation',valuation,'booking',booking);$before$;
 IF position(before IN definition)=0 THEN RAISE EXCEPTION 'swap_command_upgrade_contract';END IF;
 EXECUTE replace(definition,before,' request_value=trading.scheduled_swap_command(body,valuation,booking);');
END$migration$;
CREATE FUNCTION security.swap_schedule_command(body jsonb,ref_catalog uuid,ref_revision uuid,valuation timestamptz) RETURNS jsonb
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE booking jsonb;
BEGIN
 booking=jsonb_build_object('legalEntityId',(body->>'legalEntityId')::uuid,'legalEntityRevision',(body->>'legalEntityRevision')::integer,
  'counterpartyId',(body->>'counterpartyId')::uuid,'counterpartyRevision',(body->>'counterpartyRevision')::integer);
 IF body->'scheduled' IS NOT NULL AND body->'scheduled'<>'null'::jsonb
 THEN RETURN trading.scheduled_swap_command(body,valuation,booking);END IF;
 RETURN trading.composed_swap_command((body->>'tradeId')::uuid,(body->>'expectedRevision')::integer,(body->>'bookId')::uuid,
  (body->>'definitionRevisionId')::uuid,ref_catalog,ref_revision,body->>'commodity',nullif(body->'fixed','null'::jsonb),nullif(body->'firstFloating','null'::jsonb),body->'floating',
  trim_scale((body->>'quantity')::numeric),trim_scale(coalesce(body->>'fixedCoefficient',body->>'firstCoefficient')::numeric),(body->>'discounting')::boolean,
  valuation,CASE WHEN body->>'tradeId' IS NULL THEN valuation ELSE (body->>'effectiveAt')::timestamptz END,
  coalesce(body->>'reason','Initial booking'),body->>'convention',body->>'memo',booking);
END$$;
ALTER FUNCTION trading.composed_swap_command(uuid,integer,uuid,uuid,uuid,uuid,text,jsonb,jsonb,jsonb,numeric,numeric,boolean,timestamptz,timestamptz,text,text,text,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.scheduled_swap_command(jsonb,timestamptz,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.swap_schedule_command(jsonb,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.composed_swap_command(uuid,integer,uuid,uuid,uuid,uuid,text,jsonb,jsonb,jsonb,numeric,numeric,boolean,timestamptz,timestamptz,text,text,text,jsonb),trading.scheduled_swap_command(jsonb,timestamptz,jsonb),security.swap_schedule_command(jsonb,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.swap_schedule_command(jsonb,uuid,uuid,timestamptz) TO openetrm_runtime;
CREATE OR REPLACE FUNCTION security.publish_side_schedules(catalog uuid,operation uuid,instrument uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE staged trading.side_schedule_requests;accepted jsonb;
BEGIN
 staged=security.side_schedule_request_authority(catalog,operation);
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT request INTO accepted FROM(
  SELECT r.request FROM trading.ppa_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.actor_id)=(catalog,operation,staged.actor_id)
  UNION ALL SELECT r.request-'sideFixings' FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.operation_id,r.actor_id)=(catalog,operation,staged.actor_id)
 ) booking;
 IF accepted IS NULL OR sha256(convert_to(accepted::text,'UTF8')) IS DISTINCT FROM staged.command_hash
 THEN RAISE EXCEPTION 'side_schedule_command';END IF;
 PERFORM security.publish_admitted_side_schedules(catalog,operation,instrument);
END$$;
CREATE FUNCTION security.swap_publication_authority(catalog uuid,operation uuid,instrument uuid) RETURNS trading.swap_trade_revisions
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE command trading.side_schedule_requests;accepted trading.swap_trade_revisions;selected uuid;
BEGIN
 command=security.side_schedule_request_authority(catalog,operation);
 SELECT * INTO accepted FROM trading.swap_trade_revisions r
  WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,command.actor_id);
 IF NOT FOUND OR sha256(convert_to((accepted.request-'sideFixings')::text,'UTF8')) IS DISTINCT FROM command.command_hash
 THEN RAISE EXCEPTION 'side_schedule_command';END IF;
 FOR selected IN SELECT DISTINCT i.schedule_id FROM trading.side_schedule_request_items i WHERE(i.catalog_id,i.request_id)=(catalog,operation) LOOP
  PERFORM security.side_schedule_stage_authority(catalog,operation,selected);
 END LOOP;
 PERFORM security.require_swap_metadata_references(catalog,instrument);
 PERFORM security.side_schedule_request_authority(catalog,operation);
 RETURN accepted;
END$$;
CREATE FUNCTION security.read_swap_publication(catalog uuid,operation uuid,instrument uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE accepted trading.swap_trade_revisions;result jsonb;identities jsonb;observations jsonb;
BEGIN
 accepted=security.swap_publication_authority(catalog,operation,instrument);
 SELECT x INTO STRICT result FROM trading.swap_revision_terms(catalog,accepted.trade_id,accepted.revision) x;
 SELECT coalesce(jsonb_object_agg(x->>'id',x->'terms'),'{}'::jsonb) INTO observations
  FROM trading.swap_observation_terms(catalog,ARRAY[(result->>'observation_schedule_id')::uuid,(result->>'first_observation_schedule_id')::uuid]) x;
 result=jsonb_set(result,'{observation_schedule}',coalesce(observations->(result->>'observation_schedule_id'),'null'::jsonb));
 IF result->'first_floating'<>'null'::jsonb THEN
  result=jsonb_set(result,'{first_floating,observationSchedule}',coalesce(observations->(result->>'first_observation_schedule_id'),'null'::jsonb));
 END IF;
 SELECT jsonb_agg(jsonb_build_object('slot',p.slot,'sideId',p.side_id,'economicIdentity',s.economic_identity,'pricingBindingIdentity',s.pricing_binding_identity) ORDER BY p.slot)
 INTO identities FROM trading.instrument_side_schedules p
 JOIN trading.side_schedule_request_items i ON(i.catalog_id,i.request_id,i.slot,i.schedule_id)=(p.catalog_id,operation,p.slot,p.schedule_id)
 JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(p.catalog_id,p.schedule_id)
 JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
 JOIN trading.side_schedule_seals seal ON(seal.catalog_id,seal.schedule_id)=(p.catalog_id,p.schedule_id)
 JOIN trading.side_schedule_programs program ON(program.catalog_id,program.schedule_id)=(p.catalog_id,p.schedule_id)
 WHERE(p.catalog_id,p.instrument_id)=(catalog,instrument);
 IF jsonb_array_length(identities) IS DISTINCT FROM 2 THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 PERFORM security.swap_publication_authority(catalog,operation,instrument);
 RETURN jsonb_build_object('terms',result,'identities',identities);
END$$;
ALTER FUNCTION security.swap_publication_authority(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.read_swap_publication(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.swap_publication_authority(uuid,uuid,uuid),security.read_swap_publication(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_swap_publication(uuid,uuid,uuid) TO openetrm_runtime;
COMMIT;
