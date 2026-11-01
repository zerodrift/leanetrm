-- Bound retained swap quotes and ordered projections to their selected sides.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

DO $upgrade$
DECLARE
 previous text := $previous$
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
   FROM trading.swap_trade_revisions r
   -- Keep the instrument lookup dependent on its exact revision, not a catalog scan.
   JOIN LATERAL(SELECT * FROM trading.swap_instruments i
    WHERE(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) OFFSET 0)i ON true
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
   JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(r.catalog_id,r.instrument_id)
$previous$;
 projection text;
BEGIN
 -- Parse the expected predecessor with this PostgreSQL version, instead of
 -- depending on a version-specific pg_get_viewdef text hash. This temporary
 -- view asserts the upgrade boundary; no replacement table or grant is added.
 EXECUTE 'CREATE TEMP VIEW openetrm_expected_swap_projection WITH(security_invoker=true) AS '||previous;
 IF pg_get_viewdef('trading.swap_revision_projection'::regclass,true)
       IS DISTINCT FROM pg_get_viewdef('pg_temp.openetrm_expected_swap_projection'::regclass,true)
    OR NOT EXISTS(SELECT 1 FROM pg_class WHERE oid='trading.swap_revision_projection'::regclass
       AND relkind='v' AND relowner='openetrm_security_owner'::regrole
       AND reloptions=ARRAY['security_invoker=true'])
 THEN RAISE EXCEPTION 'swap_projection_upgrade_mismatch';END IF;
 projection:=previous;
 projection:=replace(projection,$old$JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)$old$,$new$-- Resolve the side before its exact quote, preserving invoker RLS.
   JOIN LATERAL(SELECT q.currency_code,q.commodity_code FROM ref_data.quote_specifications q
    WHERE(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code) OFFSET 0)q ON true$new$);
 projection:=replace(projection,$old$    CASE WHEN i.scheduled_first_id IS NULL THEN(SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE p.catalog_id=s.catalog_id AND p.side_id=s.id) ELSE(SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_period_projections p WHERE(p.catalog_id,p.side_id,p.period_ordinal)=(i.catalog_id,i.scheduled_second_id,0)) END AS projection_contracts,
    CASE WHEN i.scheduled_first_id IS NULL THEN(SELECT array_agg(p.weight ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE p.catalog_id=s.catalog_id AND p.side_id=s.id) ELSE(SELECT array_agg(p.weight ORDER BY p.ordinal) FROM trading.swap_period_projections p WHERE(p.catalog_id,p.side_id,p.period_ordinal)=(i.catalog_id,i.scheduled_second_id,0)) END AS projection_weights,$old$,$new$    projections.contracts AS projection_contracts,projections.weights AS projection_weights,$new$);
 projection:=replace(projection,$old$    (SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(i.catalog_id,i.first_floating_side_id)) AS first_projection_contracts,
    (SELECT array_agg(p.weight ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(i.catalog_id,i.first_floating_side_id)) AS first_projection_weights,$old$,$new$    first_projections.contracts AS first_projection_contracts,first_projections.weights AS first_projection_weights,$new$);
 projection:=replace(projection,$old$   )s ON true
$old$,$new$   )s ON true
   -- Both arrays describe the same ordered facts; authorize and read them once.
   CROSS JOIN LATERAL(
    SELECT array_agg(p.contract_id ORDER BY p.ordinal) AS contracts,array_agg(p.weight ORDER BY p.ordinal) AS weights
    FROM(
     SELECT p.ordinal,p.contract_id,p.weight FROM trading.swap_reset_projections p
      WHERE i.scheduled_first_id IS NULL AND(p.catalog_id,p.side_id)=(s.catalog_id,s.id)
     UNION ALL
     SELECT p.ordinal,p.contract_id,p.weight FROM trading.swap_period_projections p
      WHERE i.scheduled_first_id IS NOT NULL AND(p.catalog_id,p.side_id,p.period_ordinal)=(i.catalog_id,i.scheduled_second_id,0)
    )p
   )projections
   CROSS JOIN LATERAL(
    SELECT array_agg(p.contract_id ORDER BY p.ordinal) AS contracts,array_agg(p.weight ORDER BY p.ordinal) AS weights
    FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(i.catalog_id,i.first_floating_side_id)
   )first_projections
$new$);
 EXECUTE 'CREATE OR REPLACE VIEW trading.swap_revision_projection WITH(security_invoker=true) AS '||projection;
 DROP VIEW pg_temp.openetrm_expected_swap_projection;
 -- CREATE OR REPLACE preserves the original owner, column identities and ACL.
END
$upgrade$;

COMMIT;
