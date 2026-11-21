-- A scheduled swap that has started is amended from a later month forward (D441). Every started delivery month is
-- retained from the booked rows and proved unchanged by its owned period identity and exact quantity; the term does
-- not end earlier. Run settings follow an occurrence to the new revision while that occurrence is unchanged.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- A delivery month has started once its delivery has begun or a fixing is due. Its payment day follows both.
CREATE FUNCTION trading.swap_period_started(delivery_from date,first_fixing_at timestamptz,at timestamptz) RETURNS boolean
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT delivery_from<=(at AT TIME ZONE 'UTC')::date OR coalesce(at>=first_fixing_at,false)
$$;

-- The booked terms of one period in the shape the period preparers return, with the fixing evidence its revision retained.
CREATE FUNCTION trading.retained_swap_period_terms(catalog uuid,period uuid,fixings jsonb,slot_number integer) RETURNS jsonb
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('kind',p.kind,'quote',p.quote_code,'price',trim_scale(p.fixed_price)::text,'deliveryFrom',p.delivery_from,'deliveryTo',p.delivery_to,
  'paymentAt',p.payment_at,'paymentRule',p.payment_rule_id,'definitionRevision',p.definition_revision_id,'curve',p.curve_code,'contract',p.contract_id,
  'index',p.index_code,'settlementMode',p.settlement_mode,'settlementIndex',p.settlement_index_code,'settlementProjectionPolicy',p.settlement_projection_policy,
  'firstFixingAt',p.first_fixing_at,'averaging',p.averaging,'implementation',p.price_implementation,'customProjection',p.custom_projection_id,
  'spread',trim_scale(p.spread)::text,'observationSchedule',p.observation_schedule_id,'referenceCatalog',p.reference_catalog_id,'referenceRevision',p.reference_revision_id,
  'projections',coalesce((SELECT jsonb_agg(jsonb_build_object('ordinal',r.ordinal,'contractId',r.contract_id,'weight',r.weight) ORDER BY r.ordinal)
   FROM trading.swap_period_projections r WHERE(r.catalog_id,r.side_id,r.period_ordinal)=(p.catalog_id,p.side_id,p.ordinal)),'[]'::jsonb),
  'fixingEvidence',(SELECT f->'evidence' FROM jsonb_array_elements(fixings) f WHERE(f->>'slot')::integer=slot_number AND(f->>'ordinal')::integer=p.ordinal))
 FROM trading.swap_side_periods p WHERE(p.catalog_id,p.id)=(catalog,period)
$$;

-- The proof of a running amendment: every booked month remains, and each started month has one period identity,
-- direction and exact quantity in the booked and the amended instrument.
CREATE FUNCTION settlement_data.require_retained_swap_periods(catalog uuid,booked_instrument uuid,booked_quantity numeric,kept_instrument uuid,kept_quantity numeric,months date[]) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
BEGIN
 IF cardinality(months)=0 THEN RETURN;END IF;
 IF EXISTS(SELECT 1 FROM trading.swap_instruments b JOIN trading.swap_instruments k ON(k.catalog_id,k.instrument_id)=(b.catalog_id,kept_instrument)
   JOIN trading.swap_side_periods p ON p.catalog_id=b.catalog_id AND p.side_id IN(b.scheduled_first_id,b.scheduled_second_id)
  WHERE(b.catalog_id,b.instrument_id)=(catalog,booked_instrument) AND NOT EXISTS(SELECT 1 FROM trading.swap_side_periods q WHERE q.catalog_id=p.catalog_id
   AND q.side_id=CASE WHEN p.side_id=b.scheduled_first_id THEN k.scheduled_first_id ELSE k.scheduled_second_id END AND(q.delivery_from,q.delivery_to)=(p.delivery_from,p.delivery_to)))
 THEN RAISE EXCEPTION 'swap_amendment_term';END IF;
 IF EXISTS(SELECT 1 FROM unnest(months) month(delivery_from) CROSS JOIN generate_series(0,1) member(ordinal)
   CROSS JOIN LATERAL settlement_data.swap_payment_terms(catalog,booked_instrument,booked_quantity,member.ordinal,month.delivery_from,(month.delivery_from+INTERVAL '1 month')::date) booked
   LEFT JOIN LATERAL settlement_data.swap_payment_terms(catalog,kept_instrument,kept_quantity,member.ordinal,month.delivery_from,(month.delivery_from+INTERVAL '1 month')::date) kept ON true
  WHERE kept.child IS NULL OR kept.coefficient<>booked.coefficient OR kept.numerator*booked.denominator<>booked.numerator*kept.denominator
   OR kept.child<>booked.child AND(settlement_data.swap_owned_period_identity(catalog,booked.child) IS NULL OR settlement_data.swap_owned_period_identity(catalog,kept.child) IS NULL
    OR settlement_data.swap_owned_period_identity(catalog,kept.child) IS DISTINCT FROM settlement_data.swap_owned_period_identity(catalog,booked.child)))
 THEN RAISE EXCEPTION 'swap_amendment_locked';END IF;
END$$;

DO $$DECLARE target regprocedure='security.save_scheduled_swap(uuid,jsonb,jsonb,numeric,timestamptz,jsonb,uuid[])';definition text;step text[];
BEGIN
 FOREACH step SLICE 1 IN ARRAY ARRAY[
  [$before$parameters jsonb;side_quote text;$before$,
   $after$parameters jsonb;retained jsonb;started date[]='{}';held trading.swap_instruments;side_quote text;$after$],
  -- The head gate no longer refuses a started swap. It refuses the started contracts this rule does not cover.
  [$before$WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND((valuation AT TIME ZONE 'UTC')::date>(p.payment_at AT TIME ZONE 'UTC')::date OR valuation>=p.first_fixing_at)) THEN RAISE EXCEPTION 'swap_amendment';END IF;$before$,
   $after$WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id) AND trading.swap_period_started(p.delivery_from,p.first_fixing_at,valuation)
    AND(i.quantity_factor_id IS NOT NULL OR body#>>'{scheduled,first,power}' IS NOT NULL OR body#>>'{scheduled,second,power}' IS NOT NULL
     OR jsonb_path_exists(body->'scheduled','$.*.periods[*].floating.contractObservations ? (@ != null)')
     OR EXISTS(SELECT 1 FROM trading.swap_contract_observation_terms t WHERE(t.catalog_id,t.side_id)=(p.catalog_id,p.side_id))
     OR EXISTS(SELECT 1 FROM trading.swap_quantity_authoring a WHERE(a.catalog_id,a.trade_id,a.revision)=(catalog,trade,head.revision) AND a.clock_code IS NOT NULL))) THEN RAISE EXCEPTION 'swap_amendment';END IF;$after$],
  [$before$executed=head.execution_at;booking=booking||jsonb_build_object('tradeDate',head.trade_date);$before$,
   $after$executed=head.execution_at;booking=booking||jsonb_build_object('tradeDate',head.trade_date);
  SELECT * INTO STRICT held FROM trading.swap_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,head.instrument_id);
  SELECT coalesce(array_agg(DISTINCT p.delivery_from),'{}') INTO started FROM trading.swap_side_periods p
   WHERE p.catalog_id=catalog AND p.side_id IN(held.scheduled_first_id,held.scheduled_second_id) AND trading.swap_period_started(p.delivery_from,p.first_fixing_at,valuation);
  IF cardinality(started)>0 AND(held.discounting IS DISTINCT FROM discount_enabled OR held.fixed_coefficient IS DISTINCT FROM coefficient) THEN RAISE EXCEPTION 'swap_amendment_locked';END IF;$after$],
  -- A started month takes its booked terms, on the booked reference and market versions. The staged side identity must agree
  -- with those terms (security.require_dated_swap_period), which ties the submitted period to the booked one.
  [$before$IF ((period_body->>'fixed') IS NULL)=((period_body->>'floating') IS NULL) OR kind<>((period_body->>'fixed') IS NOT NULL) THEN RAISE EXCEPTION 'scheduled_swap_input';END IF;$before$,
   $after$IF ((period_body->>'fixed') IS NULL)=((period_body->>'floating') IS NULL) OR kind<>((period_body->>'fixed') IS NOT NULL) THEN RAISE EXCEPTION 'scheduled_swap_input';END IF;
   retained=NULL;
   IF cardinality(started)>0 THEN
    SELECT trading.retained_swap_period_terms(catalog,p.id,head.request->'sideFixings',slot) INTO retained FROM trading.swap_side_periods p
     WHERE(p.catalog_id,p.side_id,p.ordinal)=(catalog,CASE slot WHEN 0 THEN held.scheduled_first_id ELSE held.scheduled_second_id END,n) AND p.delivery_from=ANY(started);
    IF retained IS NOT NULL AND((retained->>'referenceCatalog',retained->>'referenceRevision') IS DISTINCT FROM(ref_catalog::text,ref_revision::text)
     OR NOT kind AND(retained->>'definitionRevision')::uuid IS DISTINCT FROM definition) THEN RAISE EXCEPTION 'swap_amendment_locked';END IF;
    IF retained->>'averaging'='quantity_weighted' THEN RAISE EXCEPTION 'swap_amendment';END IF;
   END IF;$after$],
  -- Every other month of an amendment, added or restated, starts after the effective instant.
  [$before$starts=(parameters->>'deliveryFrom')::date;ends=(parameters->>'deliveryTo')::date;payment=(parameters->>'paymentAt')::timestamptz;$before$,
   $after$starts=(parameters->>'deliveryFrom')::date;ends=(parameters->>'deliveryTo')::date;payment=(parameters->>'paymentAt')::timestamptz;
   IF trade IS NOT NULL AND retained IS NULL AND trading.swap_period_started(starts,(parameters->>'firstFixingAt')::timestamptz,valuation) THEN RAISE EXCEPTION 'swap_amendment_locked';END IF;$after$],
  [$before$parameters=security.prepare_fixed_swap_period(catalog,ref_catalog,ref_revision,quote,period_body->'fixed',valuation,publication_number,from_at);$before$,
   $after$IF retained IS NOT NULL THEN parameters=retained;ELSE parameters=security.prepare_fixed_swap_period(catalog,ref_catalog,ref_revision,quote,period_body->'fixed',valuation,publication_number,from_at);END IF;$after$],
  [$before$IF period_body->'floating'->>'contractObservations' IS NOT NULL THEN parameters=security.prepare_contract_observation_period($before$,
   $after$IF retained IS NOT NULL THEN parameters=retained;ELSIF period_body->'floating'->>'contractObservations' IS NOT NULL THEN parameters=security.prepare_contract_observation_period($after$],
  [$before$IF(body->'scheduled',book,instrument,quantity,body->>'memo',booking) IS NOT DISTINCT FROM($before$,
   $after$PERFORM settlement_data.require_retained_swap_periods(catalog,head.instrument_id,head.quantity,instrument,quantity,started);
  IF(body->'scheduled',book,instrument,quantity,body->>'memo',booking) IS NOT DISTINCT FROM($after$]]
 LOOP
  definition=pg_get_functiondef(target);
  IF(length(definition)-length(replace(definition,step[1],'')))/length(step[1])<>1 THEN RAISE EXCEPTION 'running_swap_predecessor: %',left(step[1],60);END IF;
  EXECUTE replace(definition,step[1],step[2]);
 END LOOP;
END$$;

-- Run settings name one payment occurrence. They follow it to a later revision while its period, direction and quantity are unchanged.
CREATE FUNCTION lifecycle.carry_swap_profiles() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.swap_trade_revisions;BEGIN
 IF NEW.previous_revision IS NULL THEN RETURN NEW;END IF;
 SELECT * INTO STRICT prior FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id,r.revision)=(NEW.catalog_id,NEW.trade_id,NEW.previous_revision);
 INSERT INTO lifecycle.swap_profiles
  SELECT p.catalog_id,p.id,p.trade_id,NEW.revision,p.action,p.revision+1,p.policy_id,p.policy_revision,p.membership_ordinal,p.payment_ordinal,p.delivery_from,p.delivery_to,
   p.reference_catalog_id,p.reference_revision_id,p.currency_code,p.decimal_places,p.rounding_policy,p.due_policy,p.recognition_policy,p.correction_policy,NEW.publication_order
  FROM lifecycle.swap_profiles p
  CROSS JOIN LATERAL settlement_data.swap_payment_terms(p.catalog_id,prior.instrument_id,prior.quantity,p.membership_ordinal,p.delivery_from,p.delivery_to) booked
  CROSS JOIN LATERAL settlement_data.swap_payment_terms(p.catalog_id,NEW.instrument_id,NEW.quantity,p.membership_ordinal,p.delivery_from,p.delivery_to) kept
  WHERE(p.catalog_id,p.trade_id,p.origin_revision)=(NEW.catalog_id,NEW.trade_id,prior.revision)
   AND p.revision=(SELECT max(h.revision) FROM lifecycle.swap_profiles h WHERE(h.catalog_id,h.id)=(p.catalog_id,p.id))
   AND kept.coefficient=booked.coefficient AND kept.numerator*booked.denominator=booked.numerator*kept.denominator
   AND(kept.child=booked.child OR EXISTS(SELECT 1 FROM trading.swap_side_periods k WHERE(k.catalog_id,k.id,k.ordinal)=(p.catalog_id,kept.child,p.payment_ordinal))
    AND settlement_data.swap_owned_period_identity(p.catalog_id,kept.child)=settlement_data.swap_owned_period_identity(p.catalog_id,booked.child));
 RETURN NEW;
END$$;
CREATE TRIGGER carry_lifecycle_profiles AFTER INSERT ON trading.swap_trade_revisions FOR EACH ROW EXECUTE FUNCTION lifecycle.carry_swap_profiles();

ALTER FUNCTION trading.swap_period_started(date,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.retained_swap_period_terms(uuid,uuid,jsonb,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION settlement_data.require_retained_swap_periods(uuid,uuid,numeric,uuid,numeric,date[]) OWNER TO openetrm_security_owner;
ALTER FUNCTION lifecycle.carry_swap_profiles() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.swap_period_started(date,timestamptz,timestamptz),trading.retained_swap_period_terms(uuid,uuid,jsonb,integer),
 settlement_data.require_retained_swap_periods(uuid,uuid,numeric,uuid,numeric,date[]),lifecycle.carry_swap_profiles() FROM PUBLIC;
COMMIT;
