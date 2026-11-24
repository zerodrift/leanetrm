BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);

-- A stored volatility surface names the numerical policy of the model that
-- calibrated its series: the rebuilt tree for an American product and the
-- Black-76 formula for a European one (D446). The table admits both ids; which
-- of them a product takes is decided where a fit is admitted and where its
-- revision is recorded. No row is rewritten: every stored revision names the tree.
DO $$DECLARE names name[];
BEGIN
 SELECT array_agg(conname) INTO names FROM pg_constraint
 WHERE conrelid='trading.vol_surface_revisions'::regclass AND contype='c'
  AND pg_get_constraintdef(oid) LIKE '%american_futures_option_crr_v1%';
 IF coalesce(cardinality(names),0)<>1 THEN RAISE EXCEPTION 'vol_surface_solver_policy_check_changed'; END IF;
 EXECUTE format('ALTER TABLE trading.vol_surface_revisions DROP CONSTRAINT %I,'
  ||' ADD CONSTRAINT vol_surface_revisions_solver_policy_check'
  ||' CHECK(solver_policy IN(''american_futures_option_crr_v1'',''european_futures_option_black76_v1''))',names[1]);
END$$;

-- Each patch replaces the one occurrence of the text it expects, or stops.
DO $$DECLARE signature regprocedure; definition text; anchor text; replacement text;
 style_policy text='CASE p.exercise_style WHEN ''american'' THEN ''american_futures_option_crr_v1'''
  ||' WHEN ''european'' THEN ''european_futures_option_black76_v1'' END';
BEGIN
 FOR signature,anchor,replacement IN SELECT * FROM (VALUES
  -- A revision is recorded under exactly the policy of its product's exercise style.
  ('trading.record_vol_surface_revision(uuid,uuid,uuid,jsonb,date,text,text,uuid,uuid,timestamptz,uuid[],jsonb,text)'::regprocedure,
   'OR policy IS DISTINCT FROM ''american_futures_option_crr_v1''',
   'OR policy IS DISTINCT FROM '||style_policy),
  -- A fit request names one of the two policies; its product is read after the request's shape is checked.
  ('risk.submit_vol_surface_fit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)'::regprocedure,
   'request->>''solverPolicy'' IS DISTINCT FROM ''american_futures_option_crr_v1''',
   'request->>''solverPolicy'' IS NULL OR request->>''solverPolicy'' NOT IN(''american_futures_option_crr_v1'',''european_futures_option_black76_v1'')'),
  -- Once the caller is known to read the product, the request must name that product's own policy.
  ('risk.submit_vol_surface_fit(uuid,uuid,uuid,uuid,timestamptz,jsonb,jsonb)'::regprocedure,
   'THEN RAISE EXCEPTION ''vol_surface_context'';END IF;',
   'THEN RAISE EXCEPTION ''vol_surface_context'';END IF;'||E'\n IF request->>''solverPolicy'' IS DISTINCT FROM '||style_policy
    ||' THEN RAISE EXCEPTION ''risk_job_input'';END IF;')
 ) v(fn,anchor,replacement) LOOP
  definition=pg_get_functiondef(signature);
  IF length(definition)-length(replace(definition,anchor,''))<>length(anchor)
  THEN RAISE EXCEPTION 'vol_surface_solver_policy_definition_changed: %',signature; END IF;
  EXECUTE replace(definition,anchor,replacement);
 END LOOP;
END$$;
COMMIT;
