-- Explicit in-term contractual power admission and normal DA release replay.
BEGIN;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $guards$DECLARE item record;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('security.save_ppa_booking(uuid,uuid,jsonb)','0f42a727fcfeb5de56ca1ad248ad0847df281985e03929f7682ab24ecadd2b76'),
  ('market_data.guard_power_settlement_publication()','2f1be5349fdedcd21a127bcff4dcf7f844e8bd67d959b62efa7ee96c15740396')
 ) d(signature,digest) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(item.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=item.digest)
  THEN RAISE EXCEPTION 'power_booking_upgrade: changed dependency %',item.signature;END IF;
 END LOOP;
END$guards$;

CREATE FUNCTION security.ppa_require_in_term_power(request jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE t jsonb;physical jsonb;cash jsonb;first_day date;last_day date;valuation timestamptz;first_at timestamptz;last_at timestamptz;rc uuid;rr uuid;
 payment jsonb:='{"frequency":"monthly","rule":"day_of_following_month","days":null,"dayOfMonth":10,"dayBasis":null,"shortMonth":"reject","adjustment":"unadjusted","calendar":null,"implementation":"monthly_payment_date_rules_v1"}';
BEGIN
 t=request->'terms';valuation=(request->>'valuationAt')::timestamptz;
 IF jsonb_typeof(request->'admission') IS DISTINCT FROM 'string' OR request->>'admission' IS DISTINCT FROM 'in_term_fixed_power_contractual_v1'
  OR request->>'action' IS DISTINCT FROM 'booked' OR jsonb_typeof(t) IS DISTINCT FROM 'object'
  OR t->>'implementation' IS DISTINCT FROM 'ppa_instrument_v2' OR t->'discounting' IS DISTINCT FROM 'false'::jsonb
  OR t->'normalizationAssets' IS DISTINCT FROM '[]'::jsonb OR jsonb_typeof(t->'memberships') IS DISTINCT FROM 'array'
  OR t#>>'{delivery,intervalMinutes}' IS DISTINCT FROM '60'
 THEN RAISE EXCEPTION 'ppa_admission';END IF;
 IF jsonb_array_length(t->'memberships')<>2 OR(SELECT count(*) FROM jsonb_array_elements(t->'memberships') m WHERE m->>'role'='energy_delivery')<>1
  OR(SELECT count(*) FROM jsonb_array_elements(t->'memberships') m WHERE m->>'role'='strike_payment')<>1
 THEN RAISE EXCEPTION 'ppa_admission';END IF;
 SELECT m->'side' INTO physical FROM jsonb_array_elements(t->'memberships') m WHERE m->>'role'='energy_delivery';
 SELECT m->'side' INTO cash FROM jsonb_array_elements(t->'memberships') m WHERE m->>'role'='strike_payment';
 IF physical->>'kind' IS DISTINCT FROM 'energy' OR physical#>>'{quantity,kind}' IS DISTINCT FROM 'profile' OR cash->>'kind' IS DISTINCT FROM 'fixed'
  OR cash#>>'{fixedPrice,kind}' IS DISTINCT FROM 'flat' OR cash#>>'{quantity,kind}' IS DISTINCT FROM 'slots'
  OR physical->'paymentRule' IS DISTINCT FROM payment OR cash->'paymentRule' IS DISTINCT FROM payment
 THEN RAISE EXCEPTION 'ppa_admission';END IF;
 first_day=(t#>>'{delivery,from}')::date;last_day=(t#>>'{delivery,to}')::date;
 IF first_day IS NULL OR last_day IS NULL OR valuation IS NULL OR NOT isfinite(valuation) THEN RAISE EXCEPTION 'ppa_admission';END IF;
 rc=(t->>'referenceCatalogId')::uuid;rr=(t->>'referenceRevisionId')::uuid;
 first_at=security.ppa_day_start(rc,rr,t#>>'{delivery,clock}',first_day);last_at=security.ppa_day_start(rc,rr,t#>>'{delivery,clock}',last_day);
 IF first_day IS NULL OR last_day IS NULL OR valuation IS NULL OR NOT isfinite(valuation) OR extract(day FROM first_day)<>1 OR last_day<>(first_day+interval '1 month')::date
  OR first_at IS DISTINCT FROM(request->>'coverageFrom')::timestamptz OR last_at IS DISTINCT FROM(request->>'coverageTo')::timestamptz
  OR NOT(first_at<valuation AND valuation<last_at)
  OR NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications capacity JOIN ref_data.quantity_specifications energy
    ON(energy.catalog_id,energy.revision_id,energy.commodity_code)=(capacity.catalog_id,capacity.revision_id,capacity.commodity_code)
    WHERE(capacity.catalog_id,capacity.revision_id,capacity.code)=(rc,rr,physical#>>'{quantity,capacity}')
    AND energy.code=physical#>>'{quantity,energy}' AND capacity.unit_code='megawatt' AND capacity.dimension_code='power' AND energy.unit_code='megawatt_hour')
 THEN RAISE EXCEPTION 'ppa_admission';END IF;
EXCEPTION WHEN invalid_text_representation OR datetime_field_overflow THEN RAISE EXCEPTION 'ppa_admission';
END$$;
ALTER FUNCTION security.ppa_require_in_term_power(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.ppa_require_in_term_power(jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

DO $upgrade$DECLARE body text;needle text;BEGIN
 body=pg_get_functiondef('security.save_ppa_booking(uuid,uuid,jsonb)'::regprocedure);
 needle=$old$PERFORM security.ppa_keys(request,ARRAY['snapshotId','action','tradeId','expectedRevision','bookId','valuationAt','effectiveAt','boundaryAt','coverageFrom','coverageTo','quantity','reason','memo','booking','terms']);$old$;
 IF strpos(body,needle)=0 THEN RAISE EXCEPTION 'power_booking_upgrade';END IF;
 body=replace(body,needle,replace(needle,']);',']||CASE WHEN request ? ''admission'' THEN ARRAY[''admission''] ELSE ARRAY[]::text[] END);'));
 needle=$old$first_at=(request->>'coverageFrom')::timestamptz;last_at=(request->>'coverageTo')::timestamptz;terms=request->'terms';booking=request->'booking';$old$;
 IF strpos(body,needle)=0 THEN RAISE EXCEPTION 'power_booking_upgrade';END IF;
 body=replace(body,needle,needle||E'\n IF request ? ''admission'' THEN PERFORM security.ppa_require_in_term_power(request);END IF;');
 needle='IF first_at<valuation OR jsonb_typeof(booking)';
 IF strpos(body,needle)=0 THEN RAISE EXCEPTION 'power_booking_upgrade';END IF;
 body=replace(body,needle,'IF(first_at<valuation AND NOT(request ? ''admission'')) OR jsonb_typeof(booking)');
 EXECUTE body;
 body=pg_get_functiondef('market_data.guard_power_settlement_publication()'::regprocedure);
 needle=$old$node.parameters->>'settlementPolicy' IS DISTINCT FROM 'captured_required_for_ended_v1'$old$;
 IF strpos(body,needle)=0 THEN RAISE EXCEPTION 'power_release_upgrade';END IF;
 body=replace(body,needle,$new$(node.parameters->>'settlementPolicy' IS NULL OR node.parameters->>'settlementPolicy' NOT IN('captured_required_for_ended_v1','pjm_da_normal_release_replay_v1'))$new$);
 needle=$old$AND publication.clock_code=source.clock_code AND source.provider='PJM'$old$;
 IF strpos(body,needle)=0 THEN RAISE EXCEPTION 'power_release_upgrade';END IF;
 body=replace(body,needle,needle||$new$
    AND(node.parameters->>'settlementPolicy'<>'pjm_da_normal_release_replay_v1' OR(source.market='DA' AND EXISTS(
     SELECT 1 FROM ref_data.clock_rules clock WHERE(clock.catalog_id,clock.revision_id,clock.code)=
      (source.reference_catalog_id,source.reference_revision_id,source.clock_code) AND clock.kind='iana' AND clock.iana_zone='America/New_York')))$new$);
 EXECUTE body;
END$upgrade$;
COMMIT;
