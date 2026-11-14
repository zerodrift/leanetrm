-- Current consumer: retained scheduled-swap portfolio and worker hydration.
-- Exact invoker projections share native side metadata. Worker SELECT policies
-- follow D282: full current authority once per SQL statement and its original
-- minimum end on every row. Explicit readers, writers and finish guards remain
-- current; revocation/cancellation is visible on the next statement/cursor close.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$
DECLARE p pg_proc;v pg_class;previous text:=$expected_projection$
 SELECT r.catalog_id,
    r.trade_id,
    r.revision,
    r.previous_revision,
    r.event_id,
    r.event_schema,
    r.event_kind,
    r.book_id,
    r.binding_id,
    r.instrument_id,
    r.quantity,
    r.execution_at,
    r.effective_at,
    r.operation_id,
    r.actor_id,
    r.reason,
    r.request,
    r.publication_order,
    r.trader_id,
    r.trader_revision,
    r.memo,
    r.trade_date,
    r.legal_entity_id,
    r.legal_entity_revision,
    r.counterparty_id,
    r.counterparty_revision,
    facts.booker_id,
    facts.booker_version,
    facts.booker_name,
    facts.booker_login,
    facts.executed_at AS booked_at,
    facts.trade_date AS booking_date,
    facts.implementation AS booking_implementation,
    facts.label_captured_at AS booker_label_captured_at,
    le.code AS legal_entity_code,
    le.name AS legal_entity_name,
    cp.code AS counterparty_code,
    cp.name AS counterparty_name,
    trdr.code AS trader_code,
    trdr.name AS trader_name,
    tr.reference_number AS trade_reference,
    ir.reference_number AS instrument_reference,
    COALESCE(i.fixed_side_id, i.first_floating_side_id, i.scheduled_first_id) AS fixed_side_id,
    i.first_floating_side_id,
    COALESCE(i.floating_side_id, i.scheduled_second_id) AS floating_side_id,
    i.fixed_coefficient,
    i.discounting,
    i.implementation,
    i.scheduled_first_id,
    i.scheduled_second_id,
    i.second_quantity_numerator,
    i.second_quantity_denominator,
        CASE
            WHEN i.scheduled_first_id IS NULL THEN NULL::jsonb
            ELSE trading.scheduled_swap_terms(r.catalog_id, r.trade_id, r.revision, i.instrument_id)
        END AS scheduled_data,
    f.fixed_price,
    f.delivery_from AS fixed_from,
    f.delivery_to AS fixed_to,
    f.payment_at AS fixed_payment,
    s.reference_catalog_id,
    s.reference_revision_id,
    s.spread AS floating_spread,
    projections.contracts AS projection_contracts,
    projections.weights AS projection_weights,
    q.currency_code AS quote_currency_code,
    q.commodity_code AS quote_commodity_code,
    s.implementation AS floating_implementation,
    s.definition_revision_id,
    s.curve_code,
    s.index_code,
    s.settlement_mode,
    s.settlement_index_code,
    s.settlement_projection_policy,
    s.quote_code,
    s.delivery_from AS floating_from,
    s.delivery_to AS floating_to,
    s.first_fixing_at,
    s.payment_at AS floating_payment,
    s.averaging,
    s.custom_projection_id,
    s.observation_schedule_id,
    NULL::jsonb AS observation_schedule,
    ( SELECT jsonb_build_object('curve', a_1.curve_code, 'index', a_1.index_code, 'settlement', jsonb_build_object('mode', a_1.settlement_mode, 'index',
                CASE
                    WHEN a_1.settlement_mode = 'override'::text THEN a_1.settlement_index_code::text
                    ELSE NULL::text
                END, 'projectionPolicy', a_1.settlement_projection_policy), 'currency', q_1.currency_code, 'deliveryFrom', a_1.delivery_from, 'deliveryTo', a_1.delivery_to, 'firstFixingAt', NULL::unknown, 'paymentAt', NULL::unknown, 'paymentSchedule', ( SELECT jsonb_build_object('frequency', p.frequency, 'rule', p.rule, 'days', p.days, 'dayOfMonth', p.day_of_month, 'dayBasis', p.day_basis, 'shortMonth', p.short_month, 'adjustment', p.adjustment, 'calendar', p.calendar_code, 'implementation', p.implementation) AS jsonb_build_object
                   FROM trading.swap_payment_rules p
                  WHERE p.catalog_id = a_1.catalog_id AND p.id = a_1.payment_rule_id), 'resetFrequency',
                CASE
                    WHEN a_1.averaging = 'monthly_index_price'::text THEN 'monthly'::text
                    ELSE 'daily'::text
                END, 'averagingMethod',
                CASE a_1.averaging
                    WHEN 'monthly_index_price'::text THEN 'none'::text
                    WHEN 'calendar_day_arithmetic'::text THEN 'arithmetic'::text
                    ELSE a_1.averaging
                END, 'spread', a_1.spread::text, 'observationSchedule', NULL::unknown, 'customProjection', ( SELECT jsonb_build_object('catalogId', p.catalog_id, 'curveId', p.curve_id, 'version', p.version, 'outputNode', p.output_node, 'position', p."position", 'selection', p.selection, 'implementation', p.implementation) AS jsonb_build_object
                   FROM trading.custom_curve_projection_bindings p
                  WHERE p.catalog_id = a_1.catalog_id AND p.id = a_1.custom_projection_id)) AS jsonb_build_object
           FROM trading.swap_floating_sides a_1
             JOIN ref_data.quote_specifications q_1 ON q_1.catalog_id = a_1.reference_catalog_id AND q_1.revision_id = a_1.reference_revision_id AND q_1.code::text = a_1.quote_code::text
          WHERE a_1.catalog_id = i.catalog_id AND a_1.id = i.first_floating_side_id) AS first_floating,
    a.first_fixing_at AS first_floating_fixing,
    a.custom_projection_id AS first_custom_projection_id,
    a.observation_schedule_id AS first_observation_schedule_id,
    first_projections.contracts AS first_projection_contracts,
    first_projections.weights AS first_projection_weights,
    ( SELECT jsonb_build_object('catalogId', p.catalog_id, 'curveId', p.curve_id, 'version', p.version, 'outputNode', p.output_node, 'position', p."position", 'selection', p.selection, 'implementation', p.implementation) AS jsonb_build_object
           FROM trading.custom_curve_projection_bindings p
          WHERE p.catalog_id = s.catalog_id AND p.id = s.custom_projection_id) AS custom_projection,
    ( SELECT jsonb_build_object('frequency', p.frequency, 'rule', p.rule, 'days', p.days, 'dayOfMonth', p.day_of_month, 'dayBasis', p.day_basis, 'shortMonth', p.short_month, 'adjustment', p.adjustment, 'calendar', p.calendar_code, 'implementation', p.implementation) AS jsonb_build_object
           FROM trading.swap_payment_rules p
          WHERE p.catalog_id = f.catalog_id AND p.id = f.payment_rule_id) AS fixed_payment_rule,
    ( SELECT jsonb_build_object('frequency', p.frequency, 'rule', p.rule, 'days', p.days, 'dayOfMonth', p.day_of_month, 'dayBasis', p.day_basis, 'shortMonth', p.short_month, 'adjustment', p.adjustment, 'calendar', p.calendar_code, 'implementation', p.implementation) AS jsonb_build_object
           FROM trading.swap_payment_rules p
          WHERE p.catalog_id = s.catalog_id AND p.id = s.payment_rule_id) AS floating_payment_rule
   FROM trading.swap_trade_revisions r
     JOIN LATERAL ( SELECT i_1.catalog_id,
            i_1.instrument_id,
            i_1.fixed_side_id,
            i_1.floating_side_id,
            i_1.fixed_coefficient,
            i_1.discounting,
            i_1.implementation,
            i_1.publication_order,
            i_1.effective_at,
            i_1.first_floating_side_id,
            i_1.scheduled_first_id,
            i_1.scheduled_second_id,
            i_1.second_quantity_numerator,
            i_1.second_quantity_denominator,
            i_1.quantity_factor_id
           FROM trading.swap_instruments i_1
          WHERE i_1.catalog_id = r.catalog_id AND i_1.instrument_id = r.instrument_id
         OFFSET 0) i ON true
     LEFT JOIN LATERAL ( SELECT f_1.fixed_price,
            f_1.delivery_from,
            f_1.delivery_to,
            f_1.payment_at,
            f_1.payment_rule_id,
            f_1.catalog_id,
            f_1.id
           FROM trading.swap_fixed_sides f_1
          WHERE f_1.catalog_id = i.catalog_id AND f_1.id = i.fixed_side_id
        UNION ALL
         SELECT p.fixed_price,
            p.delivery_from,
            p.delivery_to,
            p.payment_at,
            p.payment_rule_id,
            p.catalog_id,
            p.id
           FROM trading.swap_side_periods p
          WHERE p.catalog_id = i.catalog_id AND p.side_id = i.scheduled_first_id AND p.ordinal = 0 AND p.kind = 'fixed'::text) f ON true
     JOIN LATERAL ( SELECT s_1.reference_catalog_id,
            s_1.reference_revision_id,
            s_1.spread,
            s_1.catalog_id,
            s_1.id,
            s_1.implementation,
            s_1.definition_revision_id,
            s_1.curve_code,
            s_1.index_code,
            s_1.settlement_mode,
            s_1.settlement_index_code,
            s_1.settlement_projection_policy,
            s_1.quote_code,
            s_1.delivery_from,
            s_1.delivery_to,
            s_1.first_fixing_at,
            s_1.payment_at,
            s_1.averaging,
            s_1.custom_projection_id,
            s_1.observation_schedule_id,
            s_1.payment_rule_id
           FROM trading.swap_floating_sides s_1
          WHERE s_1.catalog_id = i.catalog_id AND s_1.id = i.floating_side_id
        UNION ALL
         SELECT p.reference_catalog_id,
            p.reference_revision_id,
            p.spread,
            p.catalog_id,
            p.id,
            p.price_implementation,
            p.definition_revision_id,
            p.curve_code,
            p.index_code,
            p.settlement_mode,
            p.settlement_index_code,
            p.settlement_projection_policy,
            p.quote_code,
            p.delivery_from,
            p.delivery_to,
            p.first_fixing_at,
            p.payment_at,
            p.averaging,
            p.custom_projection_id,
            p.observation_schedule_id,
            p.payment_rule_id
           FROM trading.swap_side_periods p
          WHERE p.catalog_id = i.catalog_id AND p.side_id = i.scheduled_second_id AND p.ordinal = 0 AND p.kind = 'floating'::text) s ON true
     CROSS JOIN LATERAL ( SELECT array_agg(p.contract_id ORDER BY p.ordinal) AS contracts,
            array_agg(p.weight ORDER BY p.ordinal) AS weights
           FROM ( SELECT p_1.ordinal,
                    p_1.contract_id,
                    p_1.weight
                   FROM trading.swap_reset_projections p_1
                  WHERE i.scheduled_first_id IS NULL AND p_1.catalog_id = s.catalog_id AND p_1.side_id = s.id
                UNION ALL
                 SELECT p_1.ordinal,
                    p_1.contract_id,
                    p_1.weight
                   FROM trading.swap_period_projections p_1
                  WHERE i.scheduled_first_id IS NOT NULL AND p_1.catalog_id = i.catalog_id AND p_1.side_id = i.scheduled_second_id AND p_1.period_ordinal = 0) p) projections
     CROSS JOIN LATERAL ( SELECT array_agg(p.contract_id ORDER BY p.ordinal) AS contracts,
            array_agg(p.weight ORDER BY p.ordinal) AS weights
           FROM trading.swap_reset_projections p
          WHERE p.catalog_id = i.catalog_id AND p.side_id = i.first_floating_side_id) first_projections
     JOIN ref_data.quote_specifications q ON q.catalog_id = s.reference_catalog_id AND q.revision_id = s.reference_revision_id AND q.code::text = s.quote_code::text
     LEFT JOIN trading.swap_floating_sides a ON a.catalog_id = i.catalog_id AND a.id = i.first_floating_side_id
     LEFT JOIN trading.booking_facts facts ON facts.catalog_id = r.catalog_id AND facts.trade_id = r.trade_id
     LEFT JOIN trading.legal_entities le ON le.catalog_id = r.catalog_id AND le.id = r.legal_entity_id AND le.revision = r.legal_entity_revision
     LEFT JOIN trading.counterparties cp ON cp.catalog_id = r.catalog_id AND cp.id = r.counterparty_id AND cp.revision = r.counterparty_revision
     LEFT JOIN trading.traders trdr ON trdr.catalog_id = r.catalog_id AND trdr.id = r.trader_id AND trdr.revision = r.trader_revision
     JOIN trading.trade_references tr ON tr.catalog_id = r.catalog_id AND tr.trade_id = r.trade_id
     JOIN trading.instrument_references ir ON ir.catalog_id = r.catalog_id AND ir.instrument_id = r.instrument_id;
$expected_projection$;
BEGIN
 SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure('trading.scheduled_swap_terms(uuid,uuid,integer,uuid)');
 IF NOT FOUND OR md5(p.prosrc)<>'5900cc5344e291fb8222fdcda48fe92f' OR p.proowner<>current_user::regrole OR p.prorettype<>'jsonb'::regtype
  OR p.proretset OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.prosecdef OR p.provolatile<>'s' OR p.proparallel<>'u' OR p.prosqlbody IS NOT NULL
  OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL
  OR p.proargnames IS DISTINCT FROM ARRAY['catalog','trade','revision_number','instrument']
  OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'sql' OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR p.proacl IS NULL OR EXISTS(SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantor<>p.proowner OR a.privilege_type<>'EXECUTE' OR a.is_grantable
    OR a.grantee<>ALL(ARRAY[p.proowner,'openetrm_runtime'::regrole,'openetrm_security_owner'::regrole,'openetrm_risk_worker'::regrole]::oid[]))
  OR(SELECT count(*) FROM aclexplode(p.proacl))<>(SELECT count(DISTINCT role_id) FROM unnest(ARRAY[p.proowner,'openetrm_runtime'::regrole,'openetrm_security_owner'::regrole,'openetrm_risk_worker'::regrole]::oid[]) role_id)
 THEN RAISE EXCEPTION 'retained_swap_source_upgrade: unexpected scheduled terms helper';END IF;
 SELECT * INTO v FROM pg_class WHERE oid=to_regclass('trading.swap_revision_projection');
 IF NOT FOUND OR v.relkind<>'v' OR v.relpersistence<>'p' OR v.relowner<>'openetrm_security_owner'::regrole
  OR v.reloptions IS DISTINCT FROM ARRAY['security_invoker=true'] OR v.relhastriggers
  OR v.relacl::text IS DISTINCT FROM '{openetrm_security_owner=arwdDxt/openetrm_security_owner,openetrm_runtime=r/openetrm_security_owner,openetrm_risk_worker=r/openetrm_security_owner}'
  OR EXISTS(SELECT 1 FROM pg_attribute a WHERE a.attrelid=v.oid AND a.attacl IS NOT NULL)
  OR(SELECT count(*) FROM pg_rewrite r WHERE r.ev_class=v.oid)<>1
  OR NOT EXISTS(SELECT 1 FROM pg_rewrite r WHERE r.ev_class=v.oid AND r.rulename='_RETURN' AND r.ev_type='1' AND r.is_instead AND r.ev_enabled='O')
 THEN RAISE EXCEPTION 'retained_swap_source_upgrade: unexpected revision projection';END IF;
 EXECUTE 'CREATE TEMP VIEW openetrm_expected_retained_swap_projection WITH(security_invoker=true) AS '||previous;
 IF pg_get_viewdef(v.oid,true) IS DISTINCT FROM pg_get_viewdef('pg_temp.openetrm_expected_retained_swap_projection'::regclass,true)
 THEN RAISE EXCEPTION 'retained_swap_source_upgrade: changed revision projection';END IF;
END$upgrade$;

DO $period_upgrade$DECLARE p pg_proc;spec record;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('trading.indexed_whole_term_swap_period_input(uuid,uuid,integer)','38b43d1e59e45d58de802979ec7281fd','sql'),
  ('trading.whole_term_swap_period_input(uuid,uuid,integer)','19ae2e7bebfbd78752d8041262003bff','plpgsql'),
  ('trading.contract_observation_evidence(uuid,uuid,integer)','a54d10e1d419adb81efcfa7aaaec25df','sql')
 )x(signature,body_hash,language) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=spec.signature::regprocedure;
  IF md5(p.prosrc)<>spec.body_hash OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset OR p.prosqlbody IS NOT NULL
   OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.prosecdef OR p.provolatile<>'s' OR p.proparallel<>'u' OR p.provariadic<>0
   OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.proargnames IS DISTINCT FROM ARRAY['catalog','selected_side','period']
   OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>spec.language
   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
   OR p.proacl::text IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner,openetrm_risk_worker=X/openetrm_security_owner}'
  THEN RAISE EXCEPTION 'retained_swap_source_upgrade: unexpected period helper %',spec.signature;END IF;
 END LOOP;
END$period_upgrade$;

-- Preserve the former strict required-child fence for a contract-observation
-- period. This helper reads no rows and supplies no financial inputs.
CREATE FUNCTION trading.require_retained_swap_period(document jsonb,available boolean) RETURNS jsonb
 LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$BEGIN
 IF available IS NOT TRUE THEN RAISE EXCEPTION 'retained_swap_period_denied' USING ERRCODE='42501';END IF;
 RETURN document;
END$$;
ALTER FUNCTION trading.require_retained_swap_period(jsonb,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_retained_swap_period(jsonb,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION trading.require_retained_swap_period(jsonb,boolean) TO openetrm_runtime,openetrm_risk_worker;

CREATE VIEW trading.swap_period_input_projection WITH(security_invoker=true) AS
SELECT p.catalog_id,p.side_id,p.ordinal,
 CASE WHEN q.code IS NULL OR payment.terms IS NULL THEN NULL ELSE CASE WHEN p.kind='fixed' THEN jsonb_build_object('fixed',jsonb_build_object('price',trim_scale(p.fixed_price)::text,'currency',q.currency_code,
  'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,'paymentSchedule',payment.terms),'floating',NULL)
 ELSE jsonb_build_object('fixed',NULL,'floating',jsonb_build_object('curve',p.curve_code,'index',p.index_code,'settlement',jsonb_build_object('mode',p.settlement_mode,'index',CASE WHEN p.settlement_mode='override' THEN p.settlement_index_code END,'projectionPolicy',p.settlement_projection_policy),'currency',q.currency_code,
  'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,'paymentSchedule',payment.terms,'spread',trim_scale(p.spread)::text,
  'resetFrequency',CASE WHEN p.averaging='monthly_index_price' THEN 'monthly' ELSE 'daily' END,
  'averagingMethod',CASE p.averaging WHEN 'monthly_index_price' THEN 'none' WHEN 'calendar_day_arithmetic' THEN 'arithmetic' ELSE p.averaging END,
  'observationSchedule',CASE WHEN p.observation_schedule_id IS NULL THEN NULL ELSE jsonb_build_object('implementation',o.implementation,
   'calendar',CASE WHEN o.calendar_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',o.reference_catalog_id,'revisionId',o.reference_revision_id),'code',o.calendar_code) END,
   'quantities',CASE WHEN o.averaging='quantity_weighted' THEN(SELECT jsonb_agg(jsonb_build_object('from',k.delivery_from,'to',k.delivery_to,'quantity',jsonb_build_object('value',trim_scale(w.amount)::text,'specification',jsonb_build_object('revision',jsonb_build_object('catalogId',w.reference_catalog_id,'revisionId',w.reference_revision_id),'code',w.quantity_code))) ORDER BY w.ordinal)
    FROM trading.swap_observation_quantities w JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(w.catalog_id,w.definition_revision_id,w.contract_id) WHERE(w.catalog_id,w.schedule_id)=(o.catalog_id,o.id)) END) END,
  'customProjection',CASE WHEN c.id IS NULL THEN NULL ELSE jsonb_build_object('catalogId',c.catalog_id,'curveId',c.curve_id,'version',c.version,'outputNode',c.output_node,'position',c.position,'selection',c.selection,'implementation',c.implementation) END)) END END AS indexed_input,
 CASE WHEN p.price_implementation IS DISTINCT FROM 'contract_observation_average_v1' THEN
  CASE WHEN q.code IS NULL OR payment.terms IS NULL THEN NULL ELSE CASE WHEN p.kind='fixed' THEN jsonb_build_object('fixed',jsonb_build_object('price',trim_scale(p.fixed_price)::text,'currency',q.currency_code,
  'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,'paymentSchedule',payment.terms),'floating',NULL)
 ELSE jsonb_build_object('fixed',NULL,'floating',jsonb_build_object('curve',p.curve_code,'index',p.index_code,'settlement',jsonb_build_object('mode',p.settlement_mode,'index',CASE WHEN p.settlement_mode='override' THEN p.settlement_index_code END,'projectionPolicy',p.settlement_projection_policy),'currency',q.currency_code,
  'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,'paymentSchedule',payment.terms,'spread',trim_scale(p.spread)::text,
  'resetFrequency',CASE WHEN p.averaging='monthly_index_price' THEN 'monthly' ELSE 'daily' END,
  'averagingMethod',CASE p.averaging WHEN 'monthly_index_price' THEN 'none' WHEN 'calendar_day_arithmetic' THEN 'arithmetic' ELSE p.averaging END,
  'observationSchedule',CASE WHEN p.observation_schedule_id IS NULL THEN NULL ELSE jsonb_build_object('implementation',o.implementation,
   'calendar',CASE WHEN o.calendar_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',o.reference_catalog_id,'revisionId',o.reference_revision_id),'code',o.calendar_code) END,
   'quantities',CASE WHEN o.averaging='quantity_weighted' THEN(SELECT jsonb_agg(jsonb_build_object('from',k.delivery_from,'to',k.delivery_to,'quantity',jsonb_build_object('value',trim_scale(w.amount)::text,'specification',jsonb_build_object('revision',jsonb_build_object('catalogId',w.reference_catalog_id,'revisionId',w.reference_revision_id),'code',w.quantity_code))) ORDER BY w.ordinal)
    FROM trading.swap_observation_quantities w JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(w.catalog_id,w.definition_revision_id,w.contract_id) WHERE(w.catalog_id,w.schedule_id)=(o.catalog_id,o.id)) END) END,
  'customProjection',CASE WHEN c.id IS NULL THEN NULL ELSE jsonb_build_object('catalogId',c.catalog_id,'curveId',c.curve_id,'version',c.version,'outputNode',c.output_node,'position',c.position,'selection',c.selection,'implementation',c.implementation) END)) END END
 ELSE trading.require_retained_swap_period(jsonb_build_object('fixed',NULL,'floating',jsonb_build_object('curve',p.curve_code,'index',NULL,'settlement',NULL,'currency',q.currency_code,'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,'paymentSchedule',payment.terms,'spread',trim_scale(p.spread)::text,'resetFrequency','daily','averagingMethod','arithmetic',
  'contractObservations',jsonb_build_object('implementation','contract_observation_source_v1','historyPolicy',t.history_policy,'role',t.role,'expiryPolicy',t.expiry_policy,
   'selection',jsonb_build_object('mode',t.selection_mode,'contract',CASE WHEN t.selected_contract_id IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',t.catalog_id,'revisionId',t.definition_revision_id),'id',t.selected_contract_id) END,'promptOffset',t.prompt_offset,'roll',t.roll,'boundaries',boundaries.items),'observations',observations.items))),t.catalog_id IS NOT NULL AND q.code IS NOT NULL) END AS input,
 observations.evidence

 FROM trading.swap_side_periods p LEFT JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.reference_catalog_id,p.reference_revision_id,p.quote_code)
 LEFT JOIN LATERAL(SELECT jsonb_build_object('frequency',r.frequency,'rule',r.rule,'days',r.days,'dayOfMonth',r.day_of_month,'dayBasis',r.day_basis,'shortMonth',r.short_month,'adjustment',r.adjustment,'calendar',r.calendar_code,'implementation',r.implementation) terms
  FROM trading.swap_payment_rules r WHERE(r.catalog_id,r.id)=(p.catalog_id,p.payment_rule_id)) payment ON true
 LEFT JOIN trading.swap_observation_schedules o ON(o.catalog_id,o.id)=(p.catalog_id,p.observation_schedule_id)
 LEFT JOIN trading.custom_curve_projection_bindings c ON(c.catalog_id,c.id)=(p.catalog_id,p.custom_projection_id)
 LEFT JOIN trading.swap_contract_observation_terms t ON(t.catalog_id,t.side_id,t.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal)
 LEFT JOIN LATERAL(
  SELECT jsonb_agg(jsonb_build_object('ordinal',a.ordinal,'from',a.observation_from,'to',a.observation_to,'fixingAt',a.fixing_at) ORDER BY a.ordinal) AS items,
   jsonb_agg(to_jsonb(a)||jsonb_build_object('history_policy',t.history_policy,'role',t.role,'expiry_policy',t.expiry_policy,'implementation','contract_observation_source_v1') ORDER BY a.ordinal)
    FILTER(WHERE t.catalog_id IS NOT NULL) AS evidence
  FROM trading.swap_contract_observations a WHERE(a.catalog_id,a.side_id,a.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal)
 ) observations ON true
 LEFT JOIN LATERAL(
  SELECT jsonb_agg(jsonb_build_object('contract',jsonb_build_object('revision',jsonb_build_object('catalogId',b.catalog_id,'revisionId',b.definition_revision_id),'id',b.contract_id),'rollAt',b.roll_at) ORDER BY b.ordinal) AS items
  FROM trading.swap_contract_roll_boundaries b WHERE(b.catalog_id,b.side_id,b.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal)
 ) boundaries ON true;
ALTER VIEW trading.swap_period_input_projection OWNER TO openetrm_security_owner;
REVOKE ALL ON trading.swap_period_input_projection FROM PUBLIC;
GRANT SELECT ON trading.swap_period_input_projection TO openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION trading.indexed_whole_term_swap_period_input(catalog uuid,selected_side uuid,period integer) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT p.indexed_input FROM trading.swap_period_input_projection p WHERE(p.catalog_id,p.side_id,p.ordinal)=(catalog,selected_side,period)
$$;
CREATE OR REPLACE FUNCTION trading.whole_term_swap_period_input(catalog uuid,selected_side uuid,period integer) RETURNS jsonb
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$DECLARE document jsonb;BEGIN
 SELECT p.input INTO document FROM trading.swap_period_input_projection p WHERE(p.catalog_id,p.side_id,p.ordinal)=(catalog,selected_side,period);
 RETURN document;
END$$;
CREATE OR REPLACE FUNCTION trading.contract_observation_evidence(catalog uuid,selected_side uuid,period integer) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT p.evidence FROM trading.swap_period_input_projection p WHERE(p.catalog_id,p.side_id,p.ordinal)=(catalog,selected_side,period)
$$;

CREATE VIEW trading.scheduled_swap_term_projection WITH(security_invoker=true) AS
WITH slots AS(
 SELECT r.catalog_id,r.trade_id,r.revision,r.instrument_id,i.implementation,i.quantity_factor_id,selected.slot,selected.side_id
 FROM trading.swap_trade_revisions r
 JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
 CROSS JOIN LATERAL(VALUES(0,i.scheduled_first_id),(1,i.scheduled_second_id)) selected(slot,side_id)
 WHERE i.scheduled_first_id IS NOT NULL
),side_terms AS(
 SELECT selected.catalog_id,selected.trade_id,selected.revision,selected.instrument_id,selected.implementation,selected.quantity_factor_id,
  selected.slot,selected.side_id,
  jsonb_build_object('basis',a.basis,'quantity',jsonb_build_object('revision',jsonb_build_object('catalogId',a.reference_catalog_id,'revisionId',a.reference_revision_id),'code',a.quantity_code),
   'amount',trim_scale(a.contract_amount)::text,'allocation',a.allocation,'priceApplication',a.price_application,
   'power',CASE WHEN a.clock_code IS NULL THEN NULL ELSE jsonb_build_object('clock',jsonb_build_object('revision',jsonb_build_object('catalogId',a.reference_catalog_id,'revisionId',a.reference_revision_id),'code',a.clock_code),
    'product',a.delivery_product,'startHour',a.start_hour,'endHour',a.end_hour,'saturday',a.saturday,'calendar',CASE WHEN a.calendar_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',a.reference_catalog_id,'revisionId',a.reference_revision_id),'code',a.calendar_code) END) END,
   'periods',jsonb_agg(period_input.input||jsonb_build_object('amount',trim_scale(q.amount)::text) ORDER BY p.ordinal)) terms,
  jsonb_build_object('id',selected.side_id,'periods',jsonb_agg(jsonb_build_object(
   'contract_observations',period_input.evidence,
   'id',p.id,'n',trim_scale(p.quantity_numerator)::text,'d',trim_scale(p.quantity_denominator)::text,
   'projection_contracts',(SELECT jsonb_agg(r.contract_id ORDER BY r.ordinal) FROM trading.swap_period_projections r WHERE(r.catalog_id,r.side_id,r.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal)),
   'projection_weights',(SELECT jsonb_agg(r.weight::text ORDER BY r.ordinal) FROM trading.swap_period_projections r WHERE(r.catalog_id,r.side_id,r.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal))) ORDER BY p.ordinal)) binding
 FROM slots selected
 JOIN trading.swap_quantity_authoring a ON(a.catalog_id,a.trade_id,a.revision,a.slot)=(selected.catalog_id,selected.trade_id,selected.revision,selected.slot)
 JOIN trading.swap_side_periods p ON(p.catalog_id,p.side_id)=(selected.catalog_id,selected.side_id)
 LEFT JOIN trading.swap_period_input_projection period_input ON(period_input.catalog_id,period_input.side_id,period_input.ordinal)=(p.catalog_id,p.side_id,p.ordinal)
 LEFT JOIN trading.swap_quantity_amounts q ON(q.catalog_id,q.trade_id,q.revision,q.slot,q.ordinal)=(selected.catalog_id,selected.trade_id,selected.revision,selected.slot,p.ordinal)
 GROUP BY selected.catalog_id,selected.trade_id,selected.revision,selected.instrument_id,selected.implementation,selected.quantity_factor_id,selected.slot,selected.side_id,
  a.basis,a.reference_catalog_id,a.reference_revision_id,a.quantity_code,a.contract_amount,a.allocation,a.price_application,a.clock_code,a.delivery_product,a.start_hour,a.end_hour,a.saturday,a.calendar_code
)
SELECT catalog_id,trade_id,revision,instrument_id,
 jsonb_build_object('terms',jsonb_build_object('implementation',implementation,'first',jsonb_agg(terms ORDER BY slot)->0,'second',jsonb_agg(terms ORDER BY slot)->1)||
  coalesce((SELECT jsonb_build_object('factor',jsonb_build_object('value',trim_scale(f.value)::text,
   'numeratorQuantity',jsonb_build_object('revision',jsonb_build_object('catalogId',f.reference_catalog_id,'revisionId',f.reference_revision_id),'code',f.numerator_quantity),
   'denominatorQuantity',jsonb_build_object('revision',jsonb_build_object('catalogId',f.reference_catalog_id,'revisionId',f.reference_revision_id),'code',f.denominator_quantity),
   'implementation',f.implementation,'unitConversionImplementation',f.conversion_implementation)) FROM trading.swap_quantity_factors f WHERE(f.catalog_id,f.id)=(side_terms.catalog_id,side_terms.quantity_factor_id)),'{}'::jsonb),
  'bindings',jsonb_agg(binding ORDER BY slot)) AS scheduled_data
 FROM side_terms GROUP BY catalog_id,trade_id,revision,instrument_id,implementation,quantity_factor_id;
ALTER VIEW trading.scheduled_swap_term_projection OWNER TO openetrm_security_owner;
REVOKE ALL ON trading.scheduled_swap_term_projection FROM PUBLIC;
GRANT SELECT ON trading.scheduled_swap_term_projection TO openetrm_runtime,openetrm_risk_worker;

CREATE OR REPLACE FUNCTION trading.scheduled_swap_terms(catalog uuid,trade uuid,revision_number integer,instrument uuid) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $function$
 SELECT p.scheduled_data FROM trading.scheduled_swap_term_projection p
 WHERE(p.catalog_id,p.trade_id,p.revision,p.instrument_id)=(catalog,trade,revision_number,instrument)
$function$;

DO $projection$
DECLARE definition text=pg_get_viewdef('pg_temp.openetrm_expected_retained_swap_projection'::regclass,true);
 needle constant text='trading.scheduled_swap_terms(r.catalog_id, r.trade_id, r.revision, i.instrument_id)';
BEGIN
 IF length(definition)-length(replace(definition,needle,''))<>length(needle)
 THEN RAISE EXCEPTION 'retained_swap_source_upgrade: expected exact schedule projection';END IF;
 definition=replace(definition,needle,'scheduled.scheduled_data');
 -- The exact native parent coordinates remain part of this invoker view. No
 -- source, reference, book, head or current-permission predicate is bypassed.
 definition=regexp_replace(definition,';[[:space:]]*$','');
 EXECUTE 'CREATE OR REPLACE VIEW trading.swap_revision_projection WITH(security_invoker=true) AS '||definition||
  ' LEFT JOIN trading.scheduled_swap_term_projection scheduled ON(scheduled.catalog_id,scheduled.trade_id,scheduled.revision,scheduled.instrument_id)=(r.catalog_id,r.trade_id,r.revision,i.instrument_id)';
END$projection$;
DROP VIEW pg_temp.openetrm_expected_retained_swap_projection;

-- Qualify the complete predecessor worker/PUBLIC read-policy inventory before
-- changing the established worker-only predicates. Role OIDs are installation
-- local; logical role names and exact expressions make this guard reproducible.
DO $read_policies$DECLARE actual text;p record;BEGIN
 SELECT md5(jsonb_agg(jsonb_build_array(n.nspname,c.relname,x.polname,x.polcmd,x.polpermissive,
  (SELECT array_agg(r.rolname ORDER BY r.rolname COLLATE "C") FROM pg_roles r WHERE r.oid=ANY(x.polroles)),
  pg_get_expr(x.polqual,x.polrelid),pg_get_expr(x.polwithcheck,x.polrelid),c.relrowsecurity,c.relforcerowsecurity)
  ORDER BY n.nspname COLLATE "C",c.relname COLLATE "C",x.polname COLLATE "C")::text) INTO actual
 FROM pg_policy x JOIN pg_class c ON c.oid=x.polrelid JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname IN('security','risk','ref_data','market_data','trading','automation','settlement_data','lifecycle','assistant')
  AND x.polcmd IN('r','*') AND('openetrm_risk_worker'::regrole=ANY(x.polroles) OR 0=ANY(x.polroles));
 IF actual IS DISTINCT FROM '8a3a24172b006f5d28a9c140302d4521'
 THEN RAISE EXCEPTION 'retained_swap_source_upgrade: unexpected worker read policies';END IF;
 FOR p IN SELECT n.nspname,c.relname,x.polname,x.polroles,x.polcmd,c.relowner
  FROM pg_policy x JOIN pg_class c ON c.oid=x.polrelid JOIN pg_namespace n ON n.oid=c.relnamespace
  WHERE n.nspname IN('security','risk','ref_data','market_data','trading','automation','settlement_data','lifecycle','assistant')
   AND pg_get_expr(x.polqual,x.polrelid)='risk.worker_read()' AND x.polrelid<>'market_data.observations'::regclass LOOP
  IF p.polroles<>ARRAY['openetrm_risk_worker'::regrole]::oid[] OR p.polcmd<>'r'
   OR p.relowner NOT IN('openetrm_security_owner'::regrole,current_user::regrole)
  THEN RAISE EXCEPTION 'retained_swap_source_upgrade: unexpected worker policy owner or role';END IF;
  -- The scalar subqueries are statement-local initplans, including separate
  -- commands in one simple-query protocol message. No transaction cache exists.
  EXECUTE format('ALTER POLICY %I ON %I.%I USING((SELECT risk.worker_read()) AND clock_timestamp()<(SELECT security.authority_until(''ReadMarketData'')))',p.polname,p.nspname,p.relname);
 END LOOP;
 -- Exact observation readers separately certify their closed native policy
 -- expression; their existing policy and validator are unchanged here.
END$read_policies$;
COMMIT;
