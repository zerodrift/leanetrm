-- Cumulative, rounded outstanding-principal time fees; principal and cash remain independent owners.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- These are the exact guarded producers extended below. Drift is an upgrade refusal.
DO $$DECLARE item record;p pg_proc;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('security.book_gas_park_loan(uuid,jsonb,timestamptz)','449ea5dc980c769b6a1bac1cc05b0591'),
  ('security.read_gas_park_loan(uuid,uuid,uuid,timestamptz)','eb6555eeb630af56515b195cb09a6d39'),
  ('security.record_gas_park_loan(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean)','ee6dd1f478c7a38d3d4f5cf85d2033c3'),
  ('trading.gas_park_loan_fact(jsonb)','48eb367233f6d58ddafd3e873ed33617'),
  ('security.gas_park_loan_projection_fence(uuid,uuid,timestamptz,timestamptz,uuid[],uuid[])','87213b159c8876c88ab16dc7016962ba'))v(signature,fingerprint) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=to_regprocedure(item.signature);
  IF md5(p.prosrc)<>item.fingerprint OR pg_get_userbyid(p.proowner)<>'openetrm_security_owner'
   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.prolang<>(SELECT oid FROM pg_language WHERE lanname='plpgsql')
   OR p.prosecdef IS DISTINCT FROM(item.signature LIKE 'security.%') OR p.proleakproof OR p.proisstrict OR p.proparallel<>'u'
   OR p.provolatile IS DISTINCT FROM(CASE WHEN item.signature LIKE 'trading.%' THEN 'i'::"char" ELSE 'v'::"char" END)
   OR p.proretset OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.prokind<>'f'
   OR p.prorettype<>(CASE WHEN item.signature LIKE 'security.gas_park_loan_projection_fence%' THEN 'boolean'::regtype ELSE 'jsonb'::regtype END)
   OR p.proargnames IS DISTINCT FROM(CASE
    WHEN item.signature LIKE 'security.book_%' THEN ARRAY['catalog','body','valuation']
    WHEN item.signature LIKE 'security.read_%' THEN ARRAY['catalog','trade','context','as_of']
    WHEN item.signature LIKE 'security.record_%' THEN ARRAY['catalog','operation','trade','action','body','as_of','context','preview']
    WHEN item.signature LIKE 'security.gas_park_loan_projection_fence%' THEN ARRAY['catalog','context','opening_at','horizon_at','books','trades']
    ELSE ARRAY['body'] END)
   OR has_function_privilege('openetrm_runtime',p.oid,'EXECUTE') IS DISTINCT FROM(item.signature LIKE 'security.%')
   OR has_function_privilege('openetrm_risk_worker',p.oid,'EXECUTE') IS DISTINCT FROM(item.signature LIKE 'security.read_%' OR item.signature LIKE 'security.gas_park_loan_projection_fence%')
   OR EXISTS(SELECT 1 FROM aclexplode(coalesce(p.proacl,acldefault('f',p.proowner)))a WHERE a.grantor<>p.proowner OR a.privilege_type<>'EXECUTE' OR a.is_grantable
    OR a.grantee NOT IN(p.proowner,CASE WHEN item.signature LIKE 'security.%' THEN 'openetrm_runtime'::regrole ELSE p.proowner END,
      CASE WHEN item.signature LIKE 'security.read_%' OR item.signature LIKE 'security.gas_park_loan_projection_fence%' THEN 'openetrm_risk_worker'::regrole ELSE p.proowner END))
  THEN RAISE EXCEPTION 'gas_balance_fee_upgrade: unexpected predecessor %',item.signature;END IF;
 END LOOP;
 IF to_regprocedure('security.record_gas_park_loan_principal_v1(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean)') IS NOT NULL
  OR to_regprocedure('security.read_gas_park_loan_principal_v1(uuid,uuid,uuid,timestamptz)') IS NOT NULL
 THEN RAISE EXCEPTION 'gas_balance_fee_upgrade: unexpected private predecessor';END IF;
END$$;

INSERT INTO lifecycle.action_kinds VALUES('gas.park_loan.fee_determine',false,'ConfigureGasOwnedHoldings','RecordGasOwnedHoldings');

CREATE TABLE trading.gas_park_loan_fee_terms(
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,
 rate_amount ref_data.exact_number NOT NULL CHECK(rate_amount>=0),accrual_through_at ref_data.instant NOT NULL,due_at ref_data.instant NOT NULL,
 basis text NOT NULL CHECK(basis='accepted_outstanding_principal_v1'),time_basis text NOT NULL CHECK(time_basis='elapsed_utc_hour_v1'),
 start_policy text NOT NULL CHECK(start_policy='accepted_advance_v1'),end_policy text NOT NULL CHECK(end_policy='contractual_accrual_cutoff_v1'),
 overdue_policy text NOT NULL CHECK(overdue_policy='accrue_outstanding_until_cutoff_v1'),
 projection_policy text NOT NULL CHECK(projection_policy='remaining_return_at_fee_cutoff_v1'),
 unadvanced_policy text NOT NULL CHECK(unadvanced_policy='unadvanced_plan_from_later_of_earliest_or_evaluation_v1'),
 determination_policy text NOT NULL CHECK(determination_policy='rounded_cumulative_difference_v1'),
 rounding_increment ref_data.exact_number NOT NULL CHECK(rounding_increment>0),rounding_tie_rule text NOT NULL CHECK(rounding_tie_rule IN('to_even','away_from_zero')),
 rounding_stage text NOT NULL CHECK(rounding_stage='payment_total'),
 PRIMARY KEY(catalog_id,instrument_id),FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.gas_park_loan_instruments(catalog_id,instrument_id),
 CHECK(due_at>=accrual_through_at)
);

CREATE TABLE trading.gas_park_loan_fee_determinations(
 catalog_id uuid NOT NULL,id uuid NOT NULL,operation_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 business_key text NOT NULL CHECK(length(btrim(business_key)) BETWEEN 1 AND 128),through_at ref_data.instant NOT NULL,
 exact_n numeric NOT NULL CHECK(exact_n=trunc(exact_n)),exact_d numeric NOT NULL CHECK(exact_d=trunc(exact_d) AND exact_d>0),
 rounded_total ref_data.exact_number NOT NULL,contribution ref_data.exact_number NOT NULL,fee_id uuid,
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 2000),
 source_context_id uuid NOT NULL,source_head integer NOT NULL CHECK(source_head>0),
 account_id uuid NOT NULL,action text NOT NULL CHECK(action='gas.park_loan.fee_determine'),binding_revision integer NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,operation_id),UNIQUE(catalog_id,trade_id,id),UNIQUE(catalog_id,trade_id,revision),UNIQUE(catalog_id,fee_id),
 CONSTRAINT gas_balance_fee_receipt FOREIGN KEY(catalog_id,operation_id) REFERENCES trading.gas_park_loan_commands(catalog_id,request_id) DEFERRABLE INITIALLY DEFERRED,
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.gas_park_loan_advances(catalog_id,trade_id),
 FOREIGN KEY(catalog_id,trade_id,previous_id) REFERENCES trading.gas_park_loan_fee_determinations(catalog_id,trade_id,id),
 FOREIGN KEY(catalog_id,fee_id) REFERENCES trading.gas_owned_fees(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,account_id,action,binding_revision) REFERENCES trading.gas_owned_action_bindings(catalog_id,account_id,action,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK((revision=1)=(previous_id IS NULL)),CHECK((contribution=0)=(fee_id IS NULL)),CHECK(through_at<=effective_at)
);
CREATE TABLE trading.gas_park_loan_fee_return_inputs(
 catalog_id uuid NOT NULL,determination_id uuid NOT NULL,return_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,determination_id,return_id),
 FOREIGN KEY(catalog_id,determination_id) REFERENCES trading.gas_park_loan_fee_determinations(catalog_id,id),
 FOREIGN KEY(catalog_id,return_id) REFERENCES trading.gas_park_loan_returns(catalog_id,id)
);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_park_loan_fee_terms','gas_park_loan_fee_determinations','gas_park_loan_fee_return_inputs'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_all ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;

-- Finite decimal operands become one reduced rational. No rounded hourly rate or segment division enters the calculation.
CREATE FUNCTION trading.gas_balance_fee_exact(quantity numeric,rate numeric,microseconds numeric,direction integer)
 RETURNS TABLE(n numeric,d numeric) LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE value numeric;factor numeric;common numeric;BEGIN
 IF quantity<0 OR rate<0 OR microseconds<0 OR direction NOT IN(-1,1) OR num_nonnulls(quantity,rate,microseconds,direction)<>4
 THEN RAISE EXCEPTION 'gas_balance_fee_terms';END IF;
 value=quantity*rate*microseconds*direction;factor=power(10::numeric,scale(value));
 n=value*factor;d=3600000000::numeric*factor;common=gcd(abs(n),d);n=div(n,common);d=div(d,common);RETURN NEXT;
END$$;

CREATE FUNCTION trading.gas_balance_fee_round(n numeric,d numeric,increment numeric,tie_rule text) RETURNS numeric
 LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE numerator numeric;denominator numeric;whole numeric;remainder numeric;factor numeric;BEGIN
 IF num_nonnulls(n,d,increment,tie_rule)<>4 OR n<>trunc(n) OR d<=0 OR d<>trunc(d) OR increment<=0 OR tie_rule NOT IN('to_even','away_from_zero')
 THEN RAISE EXCEPTION 'gas_balance_fee_terms';END IF;
 factor=power(10::numeric,scale(increment));numerator=abs(n)*factor;denominator=d*increment*factor;
 whole=div(numerator,denominator);remainder=mod(numerator,denominator);
 IF remainder*2>denominator OR remainder*2=denominator AND(tie_rule='away_from_zero' OR mod(whole,2)=1) THEN whole=whole+1;END IF;
 RETURN sign(n)*whole*increment;
END$$;

CREATE FUNCTION trading.gas_balance_fee_terms_document(t trading.gas_park_loan_fee_terms) RETURNS jsonb
 LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('rateAmount',trim_scale(t.rate_amount)::text,'basis',t.basis,'timeBasis',t.time_basis,'startPolicy',t.start_policy,
  'endPolicy',t.end_policy,'overduePolicy',t.overdue_policy,'projectionPolicy',t.projection_policy,'unadvancedPolicy',t.unadvanced_policy,
  'determinationPolicy',t.determination_policy,'accrualThroughAt',to_char(t.accrual_through_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'),
  'roundingIncrement',trim_scale(t.rounding_increment)::text,'roundingTieRule',t.rounding_tie_rule,'roundingStage',t.rounding_stage);
$$;

-- Extend only this profile's transport canonicalizer; existing generic fact contracts stay unchanged.
DO $$DECLARE definition text;BEGIN
 definition=pg_get_functiondef('trading.gas_park_loan_fact(jsonb)'::regprocedure);
 definition=replace(definition,' RETURN result;',$code$
 IF body->>'previousDeterminationId' IS NOT NULL THEN result=result||jsonb_build_object('previousDeterminationId',(body->>'previousDeterminationId')::uuid);END IF;
 IF body->>'through' IS NOT NULL THEN result=result||jsonb_build_object('through',to_char(trading.gas_owned_at(body->>'through') AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));END IF;
 IF body?'balanceFee' THEN
  result=result||jsonb_build_object('balanceFee',body->'balanceFee'||jsonb_build_object(
   'rateAmount',trim_scale(trading.gas_owned_quantity(body->'balanceFee'->>'rateAmount'))::text,
   'roundingIncrement',trim_scale(trading.gas_owned_quantity(body->'balanceFee'->>'roundingIncrement'))::text,
   'accrualThroughAt',to_char(trading.gas_owned_at(body->'balanceFee'->>'accrualThroughAt') AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"')));
 END IF;
 RETURN result;$code$);EXECUTE definition;
END$$;

CREATE FUNCTION trading.install_gas_balance_fee_terms(catalog uuid,instrument uuid,body jsonb) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE value jsonb=body->'balanceFee';terms trading.gas_park_loan_fee_terms;n numeric;d numeric;maximum numeric;BEGIN
 PERFORM trading.gas_park_loan_keys(value,ARRAY['rateAmount','basis','timeBasis','startPolicy','endPolicy','overduePolicy','projectionPolicy',
  'unadvancedPolicy','determinationPolicy','accrualThroughAt','roundingIncrement','roundingTieRule','roundingStage'],ARRAY[]::text[]);
 terms.catalog_id=catalog;terms.instrument_id=instrument;terms.rate_amount=trading.gas_owned_quantity(value->>'rateAmount');
 terms.accrual_through_at=trading.gas_owned_at(value->>'accrualThroughAt');terms.due_at=trading.gas_owned_at(body->>'feeDueAt');
 terms.basis=value->>'basis';terms.time_basis=value->>'timeBasis';terms.start_policy=value->>'startPolicy';terms.end_policy=value->>'endPolicy';
 terms.overdue_policy=value->>'overduePolicy';terms.projection_policy=value->>'projectionPolicy';terms.unadvanced_policy=value->>'unadvancedPolicy';
 terms.determination_policy=value->>'determinationPolicy';terms.rounding_increment=trading.gas_owned_quantity(value->>'roundingIncrement');
 terms.rounding_tie_rule=value->>'roundingTieRule';terms.rounding_stage=value->>'roundingStage';
 IF body->>'fixedFeeAmount'<>'0' OR terms.rate_amount<0 OR terms.rounding_increment<=0 OR terms.accrual_through_at<trading.gas_owned_at(body->>'returnAt')
  OR terms.due_at<terms.accrual_through_at OR terms.rounding_tie_rule NOT IN('to_even','away_from_zero')
  OR(terms.basis,terms.time_basis,terms.start_policy,terms.end_policy,terms.overdue_policy,terms.projection_policy,terms.unadvanced_policy,terms.determination_policy,terms.rounding_stage)
   IS DISTINCT FROM('accepted_outstanding_principal_v1','elapsed_utc_hour_v1','accepted_advance_v1','contractual_accrual_cutoff_v1',
    'accrue_outstanding_until_cutoff_v1','remaining_return_at_fee_cutoff_v1','unadvanced_plan_from_later_of_earliest_or_evaluation_v1',
    'rounded_cumulative_difference_v1','payment_total') THEN RAISE EXCEPTION 'gas_balance_fee_terms';END IF;
 SELECT x.n,x.d INTO n,d FROM trading.gas_balance_fee_exact(trading.gas_owned_quantity(body->>'quantity'),terms.rate_amount,
  extract(epoch FROM(terms.accrual_through_at-trading.gas_owned_at(body->>'advanceAt')))*1000000,1)x;
 maximum=trading.gas_balance_fee_round(n,d,terms.rounding_increment,terms.rounding_tie_rule);
 IF abs(maximum)>=1e24 OR scale(trim_scale(maximum))>18 THEN RAISE EXCEPTION 'gas_balance_fee_cash_range';END IF;
 INSERT INTO trading.gas_park_loan_fee_terms SELECT terms.*;
END$$;

-- The original booking producer retains the original typed request and native principal pins.
-- Only the new version stores zero/no fixed-fee parent and its independently typed time-fee terms.
DO $$DECLARE definition text;BEGIN
 definition=pg_get_functiondef('security.book_gas_park_loan(uuid,jsonb,timestamptz)'::regprocedure);
 definition=replace(definition,'''carrying''],ARRAY[''claimAccountId'',''feeDueAt'']);',
  '''carrying'']||CASE WHEN body->>''profile''=''physical_gas_park_loan_balance_time_v1'' THEN ARRAY[''balanceFee''] ELSE ARRAY[]::text[] END,ARRAY[''claimAccountId'',''feeDueAt'']);');
 definition=replace(definition,'fee_due=CASE WHEN body->>''feeDueAt'' IS NULL THEN NULL',
  'fee_due=CASE WHEN body->>''feeDueAt'' IS NULL OR body->>''profile''=''physical_gas_park_loan_balance_time_v1'' THEN NULL');
 definition=replace(definition,'body->>''profile'' IS DISTINCT FROM ''physical_gas_park_loan_v1''',
  'body->>''profile'' NOT IN(''physical_gas_park_loan_v1'',''physical_gas_park_loan_balance_time_v1'')');
 definition=replace(definition,' PERFORM security.require_gas_owned_preview(catalog,deadline);',
  ' IF body->>''profile''=''physical_gas_park_loan_balance_time_v1'' THEN PERFORM trading.install_gas_balance_fee_terms(catalog,instrument,body);END IF;'
  ||E'\n PERFORM security.require_gas_owned_preview(catalog,deadline);');EXECUTE definition;
END$$;

CREATE FUNCTION trading.gas_balance_fee_accrual(catalog uuid,trade uuid,context uuid,as_of timestamptz,through_at timestamptz)
 RETURNS TABLE(n numeric,d numeric) LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE binding trading.gas_park_loan_trades;principal trading.gas_park_loan_instruments;terms trading.gas_park_loan_fee_terms;
 advance trading.gas_park_loan_advances;known_at timestamptz;weighted numeric;BEGIN
 SELECT * INTO STRICT binding FROM trading.gas_park_loan_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,trade);
 SELECT * INTO STRICT principal FROM trading.gas_park_loan_instruments t WHERE(t.catalog_id,t.instrument_id)=(catalog,binding.instrument_id);
 SELECT * INTO terms FROM trading.gas_park_loan_fee_terms t WHERE(t.catalog_id,t.instrument_id)=(catalog,binding.instrument_id);
 SELECT * INTO advance FROM trading.gas_park_loan_advances a WHERE(a.catalog_id,a.trade_id)=(catalog,trade)
  AND trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of);
 SELECT t.known_at INTO known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF terms.instrument_id IS NULL OR advance.trade_id IS NULL OR through_at IS NULL OR through_at<advance.effective_at OR through_at>as_of
  OR through_at>known_at OR through_at>terms.accrual_through_at OR advance.effective_at>known_at
  OR EXISTS(SELECT 1 FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
   AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of) AND r.effective_at>known_at)
 THEN RAISE EXCEPTION 'gas_balance_fee_window';END IF;
 -- Each accepted return removes its tail area. This is independent of the managed event-segment evaluator.
 SELECT binding.quantity*extract(epoch FROM(through_at-advance.effective_at))*1000000-
  coalesce(sum(r.quantity*extract(epoch FROM(through_at-r.effective_at))*1000000),0) INTO weighted
  FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND r.effective_at<through_at
   AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of);
 IF weighted<0 THEN RAISE EXCEPTION 'gas_balance_fee_source';END IF;
 RETURN QUERY SELECT x.n,x.d FROM trading.gas_balance_fee_exact(weighted,terms.rate_amount,1,CASE principal.fee_direction WHEN 'pay' THEN -1 ELSE 1 END)x;
END$$;

CREATE FUNCTION trading.gas_balance_fee_paid(catalog uuid,fee uuid,context uuid,as_of timestamptz) RETURNS numeric
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT coalesce(sum(p.amount) FILTER(WHERE p.paid_at<=as_of),0) FROM(
  SELECT DISTINCT ON(p.business_key)p.amount,p.paid_at FROM trading.gas_owned_fee_payments p
   WHERE(p.catalog_id,p.fee_id)=(catalog,fee)
    AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of)
   ORDER BY p.business_key,p.revision DESC)p;
$$;

CREATE FUNCTION trading.gas_park_loan_reconciliation_required(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS boolean
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.gas_owned_evidence e JOIN trading.gas_owned_evidence n
  ON(n.catalog_id,n.account_id,n.source_key)=(e.catalog_id,e.account_id,e.source_key)
  WHERE e.catalog_id=catalog AND n.revision>e.revision
   AND(EXISTS(SELECT 1 FROM trading.gas_park_loan_advances a WHERE(a.catalog_id,a.trade_id)=(catalog,trade)
    AND e.id IN(a.agreement_evidence_id,a.evidence_id,a.claim_evidence_id) AND trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of))
    OR EXISTS(SELECT 1 FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id,r.evidence_id)=(catalog,trade,e.id)
     AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of)))
   AND trading.gas_owned_visible(catalog,n.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of));
$$;

CREATE FUNCTION trading.require_gas_balance_fee_accepted_source(catalog uuid,trade uuid,context uuid,as_of timestamptz,publication_limit integer) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
BEGIN
 IF trading.gas_park_loan_reconciliation_required(catalog,trade,context,as_of) THEN RAISE EXCEPTION 'gas_park_loan_correction_required';END IF;
 -- The retained cut must also include every correction already accepted before
 -- this determination. An old source cannot bypass the reconciliation gate.
 IF EXISTS(SELECT 1 FROM trading.gas_owned_evidence e JOIN trading.gas_owned_evidence n
  ON(n.catalog_id,n.account_id,n.source_key)=(e.catalog_id,e.account_id,e.source_key)
  WHERE e.catalog_id=catalog AND n.revision>e.revision AND n.publication_order<publication_limit
   AND(e.id IN(SELECT pin.id FROM trading.gas_park_loan_advances a CROSS JOIN LATERAL unnest(ARRAY[a.agreement_evidence_id,a.evidence_id,a.claim_evidence_id])pin(id)
    WHERE(a.catalog_id,a.trade_id)=(catalog,trade)) OR e.id IN(SELECT r.evidence_id FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
     AND r.publication_order<publication_limit))
   AND NOT trading.gas_owned_visible(catalog,n.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of))
 THEN RAISE EXCEPTION 'gas_balance_fee_context';END IF;
END$$;

DO $$DECLARE definition text;needle text;BEGIN
 definition=pg_get_functiondef('security.read_gas_park_loan(uuid,uuid,uuid,timestamptz)'::regprocedure);
 needle=$code$EXISTS(SELECT 1 FROM trading.gas_owned_evidence e JOIN trading.gas_owned_evidence n
   ON(n.catalog_id,n.account_id,n.source_key)=(e.catalog_id,e.account_id,e.source_key)
   WHERE e.catalog_id=catalog AND n.revision>e.revision AND(e.id IN(advance.agreement_evidence_id,advance.evidence_id,advance.claim_evidence_id)
    OR e.id IN(SELECT r.evidence_id FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
     AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of)))
    AND trading.gas_owned_visible(catalog,n.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of))$code$;
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_balance_fee_upgrade: unexpected reconciliation predicate';END IF;
 EXECUTE replace(definition,needle,'trading.gas_park_loan_reconciliation_required(catalog,trade,context,as_of)');
END$$;

ALTER FUNCTION security.read_gas_park_loan(uuid,uuid,uuid,timestamptz) RENAME TO read_gas_park_loan_principal_v1;
REVOKE ALL ON FUNCTION security.read_gas_park_loan_principal_v1(uuid,uuid,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.read_gas_park_loan(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;terms trading.gas_park_loan_fee_terms;head trading.gas_park_loan_fee_determinations;rows jsonb;total numeric;paid numeric;BEGIN
 result=security.read_gas_park_loan_principal_v1(catalog,trade,context,as_of);
 SELECT * INTO terms FROM trading.gas_park_loan_fee_terms t WHERE(t.catalog_id,t.instrument_id)=(catalog,(result->>'instrumentId')::uuid);
 IF terms.instrument_id IS NULL THEN RETURN result;END IF;
 -- Resolve the stream head by knowledge first. A known but business-future checkpoint cannot reinstate an earlier head.
 SELECT * INTO head FROM trading.gas_park_loan_fee_determinations d WHERE(d.catalog_id,d.trade_id)=(catalog,trade)
  AND trading.gas_owned_visible(catalog,d.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of) ORDER BY d.revision DESC LIMIT 1;
 IF head.id IS NOT NULL AND NOT trading.gas_owned_visible(catalog,head.publication_order,head.effective_at,context,as_of)
 THEN RAISE EXCEPTION 'gas_balance_fee_context';END IF;
 IF(SELECT count(*) FROM trading.gas_park_loan_fee_determinations d WHERE(d.catalog_id,d.trade_id)=(catalog,trade)
  AND trading.gas_owned_visible(catalog,d.publication_order,d.effective_at,context,as_of))>16384 THEN RAISE EXCEPTION 'gas_balance_fee_scope';END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',d.id,'operationId',d.operation_id,'revision',d.revision,'previousDeterminationId',d.previous_id,'businessKey',d.business_key,
  'through',d.through_at,'roundedTotal',trim_scale(d.rounded_total)::text,'contribution',trim_scale(d.contribution)::text,'feeId',d.fee_id,
  'exactNumerator',trim_scale(d.exact_n)::text,'exactDenominator',trim_scale(d.exact_d)::text,'evidence',d.evidence,
  'effectiveAt',d.effective_at,'knownAt',k.known_from,'sourceContextId',d.source_context_id,'sourceHead',d.source_head,
  'accountId',d.account_id,'bindingRevision',d.binding_revision,'policyId',b.policy_id,'policyRevision',b.policy_revision,
  'paid',trim_scale(trading.gas_balance_fee_paid(catalog,d.fee_id,context,as_of))::text,
  'unpaid',trim_scale(d.contribution-trading.gas_balance_fee_paid(catalog,d.fee_id,context,as_of))::text) ORDER BY d.revision),'[]'),
  coalesce(sum(trading.gas_balance_fee_paid(catalog,d.fee_id,context,as_of)),0) INTO rows,paid
  FROM trading.gas_park_loan_fee_determinations d JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
  JOIN trading.gas_owned_action_bindings b ON(b.catalog_id,b.account_id,b.action,b.revision)=(d.catalog_id,d.account_id,d.action,d.binding_revision)
  WHERE(d.catalog_id,d.trade_id)=(catalog,trade) AND trading.gas_owned_visible(catalog,d.publication_order,d.effective_at,context,as_of);
 total=coalesce(head.rounded_total,0);
 IF total IS DISTINCT FROM(SELECT coalesce(sum((r->>'contribution')::numeric),0) FROM jsonb_array_elements(rows)r)
 THEN RAISE EXCEPTION 'gas_balance_fee_source';END IF;
 result=result||jsonb_build_object('implementation','physical_gas_park_loan_balance_time_v1',
  'stateVersion',greatest((result->>'stateVersion')::integer,coalesce(head.publication_order,0)),
  'terms',result->'terms'||jsonb_build_object('balanceFee',trading.gas_balance_fee_terms_document(terms),'feeDueAt',terms.due_at,
   'feeRecognition','rounded_cumulative_difference_v1'),
  'fee',jsonb_build_object('determinations',rows,'cumulativeDeterminedTotal',trim_scale(total)::text,'amount',trim_scale(total)::text,
   'paid',trim_scale(paid)::text,'unpaid',trim_scale(total-paid)::text,'dueAt',terms.due_at));
 IF result->'advance'<>'null'::jsonb THEN
  result=jsonb_set(result,'{advance}',result->'advance'||(SELECT jsonb_build_object('id',coalesce(a.loan_id,a.claim_id),'knownAt',k.known_from)
   FROM trading.gas_park_loan_advances a JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(a.catalog_id,a.publication_order)
   WHERE(a.catalog_id,a.trade_id)=(catalog,trade)));
 END IF;
 result=jsonb_set(result,'{returns}',(SELECT coalesce(jsonb_agg(r||jsonb_build_object('knownAt',k.known_from) ORDER BY r->>'effectiveAt',r->>'id'),'[]')
  FROM jsonb_array_elements(result->'returns')r JOIN trading.gas_park_loan_returns v ON(v.catalog_id,v.id)=(catalog,(r->>'id')::uuid)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(v.catalog_id,v.publication_order)));
 PERFORM security.require_gas_owned_read(catalog,context,as_of,(result->>'authorityUntil')::timestamptz);RETURN result;
END$$;

CREATE FUNCTION trading.require_gas_balance_fee_policy(catalog uuid,account uuid,context uuid,as_of timestamptz)
 RETURNS trading.gas_owned_action_bindings LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE current_binding trading.gas_owned_action_bindings;selected trading.gas_owned_action_bindings;p lifecycle.action_policies;BEGIN
 SELECT * INTO current_binding FROM trading.gas_owned_action_bindings b
  WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,'gas.park_loan.fee_determine') ORDER BY b.revision DESC LIMIT 1;
 SELECT * INTO selected FROM trading.gas_owned_action_bindings b
  WHERE(b.catalog_id,b.account_id,b.action)=(catalog,account,'gas.park_loan.fee_determine')
   AND trading.gas_owned_visible(catalog,b.publication_order,b.effective_at,context,as_of) ORDER BY b.revision DESC LIMIT 1;
 IF current_binding.policy_id IS NULL THEN RAISE EXCEPTION 'gas_balance_fee_policy';END IF;
 IF(current_binding.revision,current_binding.policy_id,current_binding.policy_revision)
  IS DISTINCT FROM(selected.revision,selected.policy_id,selected.policy_revision) THEN RAISE EXCEPTION 'gas_balance_fee_policy_changed';END IF;
 p=lifecycle.require_policy(catalog,selected.policy_id,selected.policy_revision,'gas.park_loan.fee_determine',as_of,false);
 IF p.mode NOT IN('manual','review') OR NOT trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,as_of)
 THEN RAISE EXCEPTION 'gas_balance_fee_policy';END IF;
 RETURN selected;
END$$;

CREATE FUNCTION trading.gas_balance_fee_head(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS integer
 LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT max(h.publication_order) FROM(
  SELECT t.publication_order,t.effective_at FROM trading.gas_park_loan_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,trade)
  UNION ALL SELECT a.publication_order,a.effective_at FROM trading.gas_park_loan_advances a WHERE(a.catalog_id,a.trade_id)=(catalog,trade)
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  UNION ALL SELECT d.publication_order,d.effective_at FROM trading.gas_park_loan_fee_determinations d WHERE(d.catalog_id,d.trade_id)=(catalog,trade)
 )h WHERE trading.gas_owned_visible(catalog,h.publication_order,h.effective_at,context,as_of);
$$;

CREATE FUNCTION security.record_gas_balance_fee(catalog uuid,operation uuid,trade uuid,body jsonb,as_of timestamptz,context uuid,deadline timestamptz)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;canonical jsonb;binding trading.gas_park_loan_trades;principal trading.gas_park_loan_instruments;terms trading.gas_park_loan_fee_terms;
 advance trading.gas_park_loan_advances;previous trading.gas_park_loan_fee_determinations;duplicate trading.gas_park_loan_fee_determinations;
 policy trading.gas_owned_action_bindings;selected_through timestamptz;effective timestamptz;known_at timestamptz;account uuid;head integer;
 n numeric;d numeric;rounded numeric;contribution numeric;publication integer;determination uuid=gen_random_uuid();fee uuid;outcome jsonb;BEGIN
 PERFORM trading.gas_park_loan_keys(body,ARRAY['expectedVersion','previousDeterminationId','through','effectiveAt','businessKey','evidence'],ARRAY['previousDeterminationId']);
 body=trading.gas_park_loan_fact(body);
 canonical=jsonb_build_object('tradeId',trade,'action','fee_determine','body',body,'asOf',as_of,'contextId',context);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_owned_write(catalog,false,deadline);
 SELECT * INTO binding FROM trading.gas_park_loan_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,trade);
 IF binding.trade_id IS NULL THEN RAISE EXCEPTION 'gas_balance_fee_source';END IF;
 SELECT * INTO STRICT principal FROM trading.gas_park_loan_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,binding.instrument_id);
 SELECT * INTO terms FROM trading.gas_park_loan_fee_terms t WHERE(t.catalog_id,t.instrument_id)=(catalog,binding.instrument_id);
 IF terms.instrument_id IS NULL THEN RAISE EXCEPTION 'gas_balance_fee_profile';END IF;
 IF security.reference_version_visible(principal.reference_catalog_id,principal.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,principal.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 IF security.trading_context_day_matches(catalog,context,as_of) IS NOT TRUE
  OR NOT trading.gas_owned_visible(catalog,binding.publication_order,binding.effective_at,context,as_of) THEN RAISE EXCEPTION 'gas_balance_fee_context';END IF;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,principal.definition_revision_id);
 selected_through=trading.gas_owned_at(body->>'through');effective=trading.gas_owned_at(body->>'effectiveAt');
 SELECT t.known_at INTO STRICT known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF effective>as_of OR effective>known_at OR selected_through>effective THEN RAISE EXCEPTION 'gas_balance_fee_window';END IF;
 SELECT x.n,x.d INTO n,d FROM trading.gas_balance_fee_accrual(catalog,trade,context,as_of,selected_through)x;
 rounded=trading.gas_balance_fee_round(n,d,terms.rounding_increment,terms.rounding_tie_rule);
 -- A semantic retry acknowledges its original immutable fact, without moving the current stream head backwards.
 SELECT t.* INTO duplicate FROM trading.gas_park_loan_fee_determinations t WHERE(t.catalog_id,t.trade_id,t.business_key,t.through_at,t.effective_at,t.evidence)
  =(catalog,trade,body->>'businessKey',selected_through,effective,body->>'evidence') AND(t.exact_n,t.exact_d,t.rounded_total)=(n,d,rounded) ORDER BY t.revision LIMIT 1;
 IF duplicate.id IS NOT NULL THEN
  SELECT c.outcome||jsonb_build_object('operationId',operation,'changed',false) INTO STRICT outcome FROM trading.gas_park_loan_commands c
   WHERE(c.catalog_id,c.request_id)=(catalog,duplicate.operation_id);
  INSERT INTO trading.gas_park_loan_commands VALUES(catalog,operation,trade,actor,canonical,outcome,deadline);
  PERFORM security.require_gas_owned_write(catalog,false,deadline);RETURN outcome;
 END IF;
 PERFORM trading.require_gas_balance_fee_accepted_source(catalog,trade,context,as_of,2147483647);
 IF EXISTS(SELECT 1 FROM trading.gas_park_loan_advances a WHERE(a.catalog_id,a.trade_id)=(catalog,trade)
   AND NOT trading.gas_owned_visible(catalog,a.publication_order,a.effective_at,context,as_of))
  OR EXISTS(SELECT 1 FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
   AND NOT trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of))
  OR EXISTS(SELECT 1 FROM trading.gas_park_loan_fee_determinations t WHERE(t.catalog_id,t.trade_id)=(catalog,trade)
   AND NOT trading.gas_owned_visible(catalog,t.publication_order,t.effective_at,context,as_of)) THEN RAISE EXCEPTION 'gas_balance_fee_context';END IF;
 head=trading.gas_balance_fee_head(catalog,trade,context,as_of);
 IF jsonb_typeof(body->'expectedVersion') IS DISTINCT FROM 'number' OR(body->>'expectedVersion')::numeric<>trunc((body->>'expectedVersion')::numeric)
  OR(body->>'expectedVersion')::numeric NOT BETWEEN 1 AND 2147483647 THEN RAISE EXCEPTION 'gas_balance_fee_input';END IF;
 IF(body->>'expectedVersion')::integer IS DISTINCT FROM head THEN RAISE EXCEPTION 'gas_balance_fee_stale';END IF;
 SELECT * INTO previous FROM trading.gas_park_loan_fee_determinations t WHERE(t.catalog_id,t.trade_id)=(catalog,trade) ORDER BY t.revision DESC LIMIT 1;
 IF previous.id IS DISTINCT FROM(body->>'previousDeterminationId')::uuid THEN RAISE EXCEPTION 'gas_balance_fee_stale';END IF;
 IF selected_through<previous.through_at THEN RAISE EXCEPTION 'gas_balance_fee_window';END IF;
 SELECT * INTO STRICT advance FROM trading.gas_park_loan_advances a WHERE(a.catalog_id,a.trade_id)=(catalog,trade);
 account=CASE principal.principal_direction WHEN 1 THEN binding.owned_account_id ELSE binding.claim_account_id END;
 PERFORM trading.require_gas_owned_head(catalog,binding.owned_account_id,context,as_of);
 IF binding.claim_account_id IS NOT NULL THEN PERFORM trading.require_gas_owned_head(catalog,binding.claim_account_id,context,as_of);END IF;
 policy=trading.require_gas_balance_fee_policy(catalog,account,context,as_of);
 contribution=rounded-coalesce(previous.rounded_total,0);
 IF abs(rounded)>=1e24 OR abs(contribution)>=1e24 OR scale(trim_scale(rounded))>18 OR scale(trim_scale(contribution))>18
 THEN RAISE EXCEPTION 'gas_balance_fee_cash_range';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 IF contribution<>0 THEN
  fee=gen_random_uuid();INSERT INTO trading.gas_owned_fees VALUES(catalog,fee,coalesce(advance.return_id,advance.claim_id),NULL,contribution,
   principal.reference_catalog_id,principal.reference_revision_id,principal.currency_code,terms.due_at,actor,publication,effective);
 END IF;
 INSERT INTO trading.gas_park_loan_fee_determinations VALUES(catalog,determination,operation,trade,coalesce(previous.revision,0)+1,previous.id,body->>'businessKey',selected_through,
  n,d,rounded,contribution,fee,body->>'evidence',context,head,account,'gas.park_loan.fee_determine',policy.revision,actor,publication,effective);
 INSERT INTO trading.gas_park_loan_fee_return_inputs SELECT catalog,determination,r.id FROM trading.gas_park_loan_returns r
  WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,context,as_of);
 outcome=jsonb_build_object('id',determination,'feeId',fee,'through',selected_through,'roundedTotal',trim_scale(rounded)::text,'contribution',trim_scale(contribution)::text,
  'exactNumerator',trim_scale(n)::text,'exactDenominator',trim_scale(d)::text,'effectiveAt',effective,
  'operationId',operation,'tradeId',trade,'action','fee_determine','changed',true);
 INSERT INTO trading.gas_park_loan_commands VALUES(catalog,operation,trade,actor,canonical,outcome,deadline);
 SET CONSTRAINTS trading.gas_balance_fee_integrity IMMEDIATE;
 SET CONSTRAINTS trading.gas_balance_fee_integrity DEFERRED;
 PERFORM security.require_gas_owned_write(catalog,false,deadline);RETURN outcome;
END$$;

-- Retain the original accepted-principal producer and only extend its service head and exact fee-owner membership.
DO $$DECLARE definition text;BEGIN
 definition=pg_get_functiondef('security.record_gas_park_loan(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean)'::regprocedure);
 definition=replace(definition,'FUNCTION security.record_gas_park_loan(','FUNCTION security.record_gas_park_loan_principal_v1(');
 definition=replace(definition,' IF jsonb_typeof(body->''expectedVersion'')',
  ' source=source||jsonb_build_object(''stateVersion'',trading.gas_balance_fee_head(catalog,trade,context,as_of));'||E'\n IF jsonb_typeof(body->''expectedVersion'')');
 definition=replace(definition,'IF advance.fee_id IS NULL OR(body->>''feeId'')::uuid IS DISTINCT FROM advance.fee_id THEN',
  'IF (body->>''feeId'')::uuid IS DISTINCT FROM advance.fee_id AND NOT EXISTS(SELECT 1 FROM trading.gas_park_loan_fee_determinations d WHERE(d.catalog_id,d.trade_id,d.fee_id)=(catalog,trade,(body->>''feeId'')::uuid)) THEN');
 EXECUTE definition;
END$$;
ALTER FUNCTION security.record_gas_park_loan_principal_v1(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.record_gas_park_loan_principal_v1(uuid,uuid,uuid,text,jsonb,timestamptz,uuid,boolean) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE OR REPLACE FUNCTION security.record_gas_park_loan(catalog uuid,operation uuid,trade uuid,action text,body jsonb,as_of timestamptz,context uuid,preview boolean)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;outcome jsonb;known_at timestamptz;actual_at timestamptz;balance_profile boolean;BEGIN
 deadline=security.gas_park_loan_authority(catalog,true);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR trade IS NULL OR context IS NULL OR preview IS NULL
  OR as_of IS NULL OR NOT isfinite(as_of) OR jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>16384
  OR action IS NULL OR action NOT IN('advance','return','fee_determine','fee_payment') THEN RAISE EXCEPTION 'gas_balance_fee_input';END IF;
 IF preview THEN
  BEGIN
   outcome=security.record_gas_park_loan(catalog,operation,trade,action,body,as_of,context,false);
   RAISE EXCEPTION USING ERRCODE='GX001',DETAIL=outcome::text;
  EXCEPTION WHEN SQLSTATE 'GX001' THEN GET STACKED DIAGNOSTICS outcome=PG_EXCEPTION_DETAIL;RETURN outcome::jsonb;END;
 END IF;
 outcome=security.gas_park_loan_command_receipt(catalog,operation,trade,action,body,as_of,context);
 IF outcome IS NOT NULL THEN PERFORM security.require_gas_owned_write(catalog,false,deadline);RETURN outcome;END IF;
 IF action='fee_determine' THEN RETURN security.record_gas_balance_fee(catalog,operation,trade,body,as_of,context,deadline);END IF;
 SELECT EXISTS(SELECT 1 FROM trading.gas_park_loan_trades t JOIN trading.gas_park_loan_fee_terms f ON(f.catalog_id,f.instrument_id)=(t.catalog_id,t.instrument_id)
  WHERE(t.catalog_id,t.trade_id)=(catalog,trade)) INTO balance_profile;
 IF balance_profile THEN
  SELECT t.known_at INTO known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
  actual_at=trading.gas_owned_at(CASE action WHEN 'fee_payment' THEN body->>'paidAt' ELSE body->>'effectiveAt' END);
  IF known_at IS NULL OR actual_at>known_at THEN RAISE EXCEPTION 'gas_balance_fee_window';END IF;
 END IF;
 RETURN security.record_gas_park_loan_principal_v1(catalog,operation,trade,action,body,as_of,context,preview);
END$$;

CREATE FUNCTION trading.validate_gas_balance_fee_determination(catalog uuid,determination uuid) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE fact trading.gas_park_loan_fee_determinations;previous trading.gas_park_loan_fee_determinations;
 binding trading.gas_park_loan_trades;principal trading.gas_park_loan_instruments;terms trading.gas_park_loan_fee_terms;
 advance trading.gas_park_loan_advances;receipt trading.gas_park_loan_commands;fee trading.gas_owned_fees;
 actor uuid;n numeric;d numeric;rounded numeric;as_of timestamptz;known_at timestamptz;head integer;members uuid[];expected uuid[];policy trading.gas_owned_action_bindings;BEGIN
 SELECT * INTO STRICT fact FROM trading.gas_park_loan_fee_determinations x WHERE(x.catalog_id,x.id)=(catalog,determination);
 SELECT * INTO STRICT binding FROM trading.gas_park_loan_trades t WHERE(t.catalog_id,t.trade_id)=(catalog,fact.trade_id);
 SELECT * INTO STRICT principal FROM trading.gas_park_loan_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,binding.instrument_id);
 SELECT * INTO STRICT terms FROM trading.gas_park_loan_fee_terms t WHERE(t.catalog_id,t.instrument_id)=(catalog,binding.instrument_id);
 SELECT * INTO STRICT advance FROM trading.gas_park_loan_advances a WHERE(a.catalog_id,a.trade_id)=(catalog,fact.trade_id);
 SELECT * INTO receipt FROM trading.gas_park_loan_commands c WHERE(c.catalog_id,c.request_id)=(catalog,fact.operation_id);
 IF receipt.request_id IS NULL THEN RAISE EXCEPTION 'gas_balance_fee_counterpart';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_park_loan_fee_determinations p WHERE(p.catalog_id,p.trade_id,p.business_key,p.through_at,p.effective_at,p.evidence,p.exact_n,p.exact_d,p.rounded_total)
  =(catalog,fact.trade_id,fact.business_key,fact.through_at,fact.effective_at,fact.evidence,fact.exact_n,fact.exact_d,fact.rounded_total) AND p.id<>fact.id)
 THEN RAISE EXCEPTION 'gas_balance_fee_conflict';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF actor IS DISTINCT FROM fact.actor_id OR receipt.actor_id IS DISTINCT FROM fact.actor_id OR NOT EXISTS(SELECT 1 FROM trading.publications p
  WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(catalog,fact.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'gas_balance_fee_publication';END IF;
 IF security.reference_version_visible(principal.reference_catalog_id,principal.reference_revision_id) IS NOT TRUE
  OR security.market_fact_visible(catalog,principal.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 as_of=(receipt.request->>'asOf')::timestamptz;
 SELECT t.known_at INTO STRICT known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,fact.source_context_id);
 IF receipt.trade_id IS DISTINCT FROM fact.trade_id OR receipt.outcome->'changed' IS DISTINCT FROM 'true'::jsonb
  OR receipt.request->>'action'<>'fee_determine' OR(receipt.outcome->>'id')::uuid IS DISTINCT FROM determination
  OR(receipt.request->>'contextId')::uuid IS DISTINCT FROM fact.source_context_id
  OR(receipt.request->'body'->>'expectedVersion')::integer IS DISTINCT FROM fact.source_head
  OR(receipt.request->'body'->>'previousDeterminationId')::uuid IS DISTINCT FROM fact.previous_id
  OR(receipt.request->'body'->>'businessKey',receipt.request->'body'->>'evidence') IS DISTINCT FROM(fact.business_key,fact.evidence)
  OR(receipt.request->'body'->>'through')::timestamptz IS DISTINCT FROM fact.through_at
  OR(receipt.request->'body'->>'effectiveAt')::timestamptz IS DISTINCT FROM fact.effective_at
  OR security.trading_context_day_matches(catalog,fact.source_context_id,as_of) IS NOT TRUE
  OR fact.through_at>fact.effective_at OR fact.effective_at>as_of OR fact.effective_at>known_at
  OR EXISTS(SELECT 1 FROM trading.contexts x WHERE(x.catalog_id,x.context_id)=(catalog,fact.source_context_id) AND x.known_through>=fact.publication_order)
  OR NOT trading.gas_owned_visible(catalog,binding.publication_order,binding.effective_at,fact.source_context_id,as_of)
 THEN RAISE EXCEPTION 'gas_balance_fee_source';END IF;
 PERFORM trading.gas_park_loan_keys(receipt.request->'body',ARRAY['expectedVersion','previousDeterminationId','through','effectiveAt','businessKey','evidence'],ARRAY['previousDeterminationId']);
 PERFORM settlement_data.require_futures_margin_source(catalog,fact.source_context_id,as_of,principal.definition_revision_id);
 PERFORM trading.require_gas_balance_fee_accepted_source(catalog,fact.trade_id,fact.source_context_id,as_of,fact.publication_order);
 SELECT * INTO previous FROM trading.gas_park_loan_fee_determinations p WHERE(p.catalog_id,p.trade_id,p.revision)=(catalog,fact.trade_id,fact.revision-1);
 IF fact.previous_id IS DISTINCT FROM previous.id OR fact.through_at<previous.through_at OR fact.publication_order<=coalesce(previous.publication_order,0)
  OR EXISTS(SELECT 1 FROM trading.gas_park_loan_fee_determinations p WHERE(p.catalog_id,p.trade_id)=(catalog,fact.trade_id)
   AND p.revision<fact.revision AND NOT trading.gas_owned_visible(catalog,p.publication_order,p.effective_at,fact.source_context_id,as_of))
 THEN RAISE EXCEPTION 'gas_balance_fee_source';END IF;
 SELECT max(h.publication_order) INTO head FROM(
  SELECT binding.publication_order,binding.effective_at
  UNION ALL SELECT advance.publication_order,advance.effective_at
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,fact.trade_id) AND r.publication_order<fact.publication_order
  UNION ALL SELECT p.publication_order,p.effective_at FROM trading.gas_park_loan_fee_determinations p WHERE(p.catalog_id,p.trade_id)=(catalog,fact.trade_id) AND p.revision<fact.revision
 )h WHERE trading.gas_owned_visible(catalog,h.publication_order,h.effective_at,fact.source_context_id,as_of);
 IF head IS DISTINCT FROM fact.source_head OR NOT trading.gas_owned_visible(catalog,advance.publication_order,advance.effective_at,fact.source_context_id,as_of)
  OR EXISTS(SELECT 1 FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,fact.trade_id) AND r.publication_order<fact.publication_order
   AND NOT trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,fact.source_context_id,as_of)) THEN RAISE EXCEPTION 'gas_balance_fee_source';END IF;
 SELECT array_agg(r.return_id ORDER BY r.return_id) INTO members FROM trading.gas_park_loan_fee_return_inputs r WHERE(r.catalog_id,r.determination_id)=(catalog,determination);
 SELECT array_agg(r.id ORDER BY r.id) INTO expected FROM trading.gas_park_loan_returns r WHERE(r.catalog_id,r.trade_id)=(catalog,fact.trade_id)
  AND trading.gas_owned_visible(catalog,r.publication_order,r.effective_at,fact.source_context_id,as_of);
 IF members IS DISTINCT FROM expected THEN RAISE EXCEPTION 'gas_balance_fee_source';END IF;
 SELECT x.n,x.d INTO n,d FROM trading.gas_balance_fee_accrual(catalog,fact.trade_id,fact.source_context_id,as_of,fact.through_at)x;
 rounded=trading.gas_balance_fee_round(n,d,terms.rounding_increment,terms.rounding_tie_rule);
 policy=trading.require_gas_balance_fee_policy(catalog,fact.account_id,fact.source_context_id,as_of);
 IF fact.account_id IS DISTINCT FROM(CASE principal.principal_direction WHEN 1 THEN binding.owned_account_id ELSE binding.claim_account_id END)
  OR fact.binding_revision IS DISTINCT FROM policy.revision OR(fact.exact_n,fact.exact_d,fact.rounded_total,fact.contribution)
   IS DISTINCT FROM(n,d,rounded,rounded-coalesce(previous.rounded_total,0))
  OR abs(fact.rounded_total)>=1e24 OR abs(fact.contribution)>=1e24 OR scale(trim_scale(fact.rounded_total))>18 OR scale(trim_scale(fact.contribution))>18
 THEN RAISE EXCEPTION 'gas_balance_fee_amount';END IF;
 SELECT * INTO fee FROM trading.gas_owned_fees f WHERE(f.catalog_id,f.id)=(catalog,fact.fee_id);
 IF(fact.contribution=0)<>(fee.id IS NULL) OR fee.id IS NOT NULL AND
  (fee.position_id,fee.loan_id,fee.amount,fee.reference_catalog_id,fee.reference_revision_id,fee.currency_code,fee.due_at,fee.actor_id,fee.publication_order,fee.effective_at)
  IS DISTINCT FROM(coalesce(advance.return_id,advance.claim_id),NULL::uuid,fact.contribution,principal.reference_catalog_id,principal.reference_revision_id,
   principal.currency_code,terms.due_at,fact.actor_id,fact.publication_order,fact.effective_at) THEN RAISE EXCEPTION 'gas_balance_fee_counterpart';END IF;
 IF(receipt.outcome->>'feeId')::uuid IS DISTINCT FROM fact.fee_id OR(receipt.outcome->>'through')::timestamptz IS DISTINCT FROM fact.through_at
  OR(receipt.outcome->>'roundedTotal')::numeric IS DISTINCT FROM fact.rounded_total OR(receipt.outcome->>'contribution')::numeric IS DISTINCT FROM fact.contribution
  OR(receipt.outcome->>'exactNumerator')::numeric IS DISTINCT FROM fact.exact_n OR(receipt.outcome->>'exactDenominator')::numeric IS DISTINCT FROM fact.exact_d
  OR(receipt.outcome->>'effectiveAt')::timestamptz IS DISTINCT FROM fact.effective_at OR(receipt.outcome->>'operationId')::uuid IS DISTINCT FROM receipt.request_id
  OR(receipt.outcome->>'tradeId')::uuid IS DISTINCT FROM fact.trade_id OR receipt.outcome->>'action'<>'fee_determine'
  OR receipt.request IS DISTINCT FROM jsonb_build_object('tradeId',fact.trade_id,'action','fee_determine','body',trading.gas_park_loan_fact(receipt.request->'body'),
   'asOf',as_of,'contextId',fact.source_context_id)
 THEN RAISE EXCEPTION 'gas_balance_fee_counterpart';END IF;
 PERFORM trading.gas_park_loan_keys(receipt.outcome,ARRAY['id','feeId','through','roundedTotal','contribution','exactNumerator','exactDenominator',
  'effectiveAt','operationId','tradeId','action','changed'],ARRAY['feeId']);
 PERFORM security.require_gas_owned_write(catalog,false,receipt.authority_until);
END$$;

CREATE FUNCTION trading.check_gas_balance_fee_integrity() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE catalog uuid=NEW.catalog_id;instrument uuid;id uuid;terms trading.gas_park_loan_fee_terms;principal trading.gas_park_loan_instruments;
 binding trading.gas_park_loan_trades;parent trading.trades;receipt trading.gas_park_loan_commands;fact trading.gas_park_loan_fee_determinations;
 n numeric;d numeric;maximum numeric;through_at timestamptz;as_of timestamptz;actor uuid;BEGIN
 IF TG_TABLE_NAME IN('gas_park_loan_trades','gas_park_loan_fee_terms') THEN
  instrument=NEW.instrument_id;
  SELECT * INTO STRICT principal FROM trading.gas_park_loan_instruments p WHERE(p.catalog_id,p.instrument_id)=(catalog,instrument);
  SELECT * INTO STRICT binding FROM trading.gas_park_loan_trades t WHERE(t.catalog_id,t.instrument_id)=(catalog,instrument);
  SELECT * INTO STRICT parent FROM trading.trades t WHERE(t.catalog_id,t.id)=(catalog,binding.trade_id);
  SELECT * INTO terms FROM trading.gas_park_loan_fee_terms t WHERE(t.catalog_id,t.instrument_id)=(catalog,instrument);
  IF(parent.request->>'profile'='physical_gas_park_loan_balance_time_v1')<>(terms.instrument_id IS NOT NULL) THEN RAISE EXCEPTION 'gas_balance_fee_profile';END IF;
  IF terms.instrument_id IS NULL THEN RETURN NULL;END IF;
  IF principal.fixed_fee_amount<>0 OR principal.fee_due_at IS NOT NULL OR terms.accrual_through_at<principal.return_at
   OR parent.request->'balanceFee' IS DISTINCT FROM trading.gas_balance_fee_terms_document(terms)
   OR(parent.request->>'feeDueAt')::timestamptz IS DISTINCT FROM terms.due_at THEN RAISE EXCEPTION 'gas_balance_fee_terms';END IF;
  PERFORM trading.gas_owned_quantity(trim_scale(terms.rate_amount)::text);PERFORM trading.gas_owned_quantity(trim_scale(terms.rounding_increment)::text);
  SELECT x.n,x.d INTO n,d FROM trading.gas_balance_fee_exact(binding.quantity,terms.rate_amount,
   extract(epoch FROM(terms.accrual_through_at-principal.advance_at))*1000000,1)x;
  maximum=trading.gas_balance_fee_round(n,d,terms.rounding_increment,terms.rounding_tie_rule);
  IF abs(maximum)>=1e24 OR scale(trim_scale(maximum))>18 THEN RAISE EXCEPTION 'gas_balance_fee_cash_range';END IF;
  RETURN NULL;
 END IF;
 CASE TG_TABLE_NAME
 WHEN 'gas_park_loan_fee_determinations' THEN id=NEW.id;
 WHEN 'gas_park_loan_fee_return_inputs' THEN id=NEW.determination_id;
 WHEN 'gas_owned_fees' THEN
  SELECT f.id INTO id FROM trading.gas_park_loan_fee_determinations f WHERE(f.catalog_id,f.fee_id)=(catalog,NEW.id);
  IF id IS NULL AND EXISTS(SELECT 1 FROM trading.gas_park_loan_advances a JOIN trading.gas_park_loan_trades t ON(t.catalog_id,t.trade_id)=(a.catalog_id,a.trade_id)
   JOIN trading.gas_park_loan_fee_terms f ON(f.catalog_id,f.instrument_id)=(t.catalog_id,t.instrument_id)
   WHERE a.catalog_id=catalog AND NEW.position_id IN(a.return_id,a.claim_id)) THEN RAISE EXCEPTION 'gas_balance_fee_counterpart';END IF;
 WHEN 'gas_park_loan_commands' THEN
  IF NEW.request->>'action'<>'fee_determine' THEN RETURN NULL;END IF;
  SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
  IF NEW.actor_id IS DISTINCT FROM actor THEN RAISE EXCEPTION 'gas_balance_fee_publication';END IF;
  SELECT i.* INTO STRICT principal FROM trading.gas_park_loan_trades t JOIN trading.gas_park_loan_instruments i
   ON(i.catalog_id,i.instrument_id)=(t.catalog_id,t.instrument_id) WHERE(t.catalog_id,t.trade_id)=(catalog,NEW.trade_id);
  IF security.reference_version_visible(principal.reference_catalog_id,principal.reference_revision_id) IS NOT TRUE
   OR security.market_fact_visible(catalog,principal.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
  as_of=(NEW.request->>'asOf')::timestamptz;
  IF NEW.request IS DISTINCT FROM jsonb_build_object('tradeId',NEW.trade_id,'action','fee_determine','body',trading.gas_park_loan_fact(NEW.request->'body'),
    'asOf',as_of,'contextId',(NEW.request->>'contextId')::uuid)
   OR(NEW.outcome->>'operationId')::uuid IS DISTINCT FROM NEW.request_id THEN RAISE EXCEPTION 'gas_balance_fee_counterpart';END IF;
  PERFORM settlement_data.require_futures_margin_source(catalog,(NEW.request->>'contextId')::uuid,as_of,principal.definition_revision_id);
  IF NEW.outcome->'changed'='true'::jsonb THEN
   id=(NEW.outcome->>'id')::uuid;
   IF NEW.request_id IS DISTINCT FROM(SELECT d.operation_id FROM trading.gas_park_loan_fee_determinations d WHERE(d.catalog_id,d.id)=(catalog,(NEW.outcome->>'id')::uuid))
   THEN RAISE EXCEPTION 'gas_balance_fee_counterpart';END IF;
  ELSIF NEW.outcome->'changed'='false'::jsonb THEN
   SELECT * INTO STRICT fact FROM trading.gas_park_loan_fee_determinations f WHERE(f.catalog_id,f.id)=(catalog,(NEW.outcome->>'id')::uuid);
   SELECT * INTO STRICT receipt FROM trading.gas_park_loan_commands c WHERE(c.catalog_id,c.request_id)=(catalog,fact.operation_id);
   IF(NEW.outcome-'operationId'-'changed') IS DISTINCT FROM(receipt.outcome-'operationId'-'changed') OR NEW.trade_id<>fact.trade_id
    OR((NEW.request->'body')-'expectedVersion'-'previousDeterminationId') IS DISTINCT FROM((receipt.request->'body')-'expectedVersion'-'previousDeterminationId')
   THEN RAISE EXCEPTION 'gas_balance_fee_counterpart';END IF;
   as_of=(NEW.request->>'asOf')::timestamptz;through_at=(NEW.request->'body'->>'through')::timestamptz;
   SELECT x.n,x.d INTO n,d FROM trading.gas_balance_fee_accrual(catalog,fact.trade_id,(NEW.request->>'contextId')::uuid,as_of,through_at)x;
   IF(n,d) IS DISTINCT FROM(fact.exact_n,fact.exact_d) THEN RAISE EXCEPTION 'gas_balance_fee_source';END IF;
   PERFORM security.require_gas_owned_write(catalog,false,NEW.authority_until);RETURN NULL;
  ELSE RAISE EXCEPTION 'gas_balance_fee_counterpart';END IF;
 END CASE;
 IF id IS NOT NULL THEN PERFORM trading.validate_gas_balance_fee_determination(catalog,id);END IF;
 RETURN NULL;
END$$;

DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_park_loan_trades','gas_park_loan_fee_terms','gas_park_loan_fee_determinations','gas_park_loan_fee_return_inputs','gas_park_loan_commands','gas_owned_fees'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER gas_balance_fee_integrity AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.check_gas_balance_fee_integrity()',name);
 END LOOP;
END$$;

-- Time-fee event continuation is deliberately unqualified in this version,
-- including zero/rounded-zero fees and fully determined, principal-complete books.
-- Preserve the certified knowledge prefix, exact worker selectors and all fixed-v1 continuation behavior.
DO $$DECLARE definition text;needle text;BEGIN
 definition=pg_get_functiondef('security.gas_park_loan_projection_fence(uuid,uuid,timestamptz,timestamptz,uuid[],uuid[])'::regprocedure);
 needle=$code$  AND NOT EXISTS(SELECT 1 FROM trading.gas_park_loan_advances a WHERE(a.catalog_id,a.trade_id)=(t.catalog_id,t.trade_id) AND a.effective_at<=opening_at
   AND trading.gas_owned_visible(catalog,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,opening_at))) INTO present;$code$;
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'gas_balance_fee_upgrade: unexpected projection predicate';END IF;
 definition=replace(definition,needle,$code$  AND(EXISTS(SELECT 1 FROM trading.gas_park_loan_fee_terms f WHERE(f.catalog_id,f.instrument_id)=(t.catalog_id,t.instrument_id))
   OR NOT EXISTS(SELECT 1 FROM trading.gas_park_loan_advances a WHERE(a.catalog_id,a.trade_id)=(t.catalog_id,t.trade_id) AND a.effective_at<=opening_at
    AND trading.gas_owned_visible(catalog,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z',context,opening_at)))) INTO present;$code$);
 EXECUTE definition;
END$$;

-- The installed native entry points and table consumers below receive no raw runtime table grants.
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='trading' AND(p.proname LIKE '%gas_balance_fee%' OR p.proname='gas_park_loan_reconciliation_required')
   OR n.nspname='security' AND p.proname IN('read_gas_park_loan','record_gas_balance_fee') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',item.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC,openetrm_runtime,openetrm_risk_worker',item.signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.read_gas_park_loan(uuid,uuid,uuid,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
