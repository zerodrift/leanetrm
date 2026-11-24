BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);

-- A listed futures option product states American or European exercise (D449).
-- A European option is exercised only at expiry, by its product's automatic
-- rule, so its product accepts no contrary instructions. No row is rewritten:
-- every existing product states american.
ALTER TABLE ref_data.futures_option_products
 DROP CONSTRAINT futures_option_products_exercise_style_check,
 ADD CONSTRAINT futures_option_products_exercise_style_check CHECK(exercise_style IN('american','european')),
 ADD CONSTRAINT futures_option_products_european_instructions_check
  CHECK(exercise_style<>'european' OR NOT contrary_instructions_allowed);

-- Exercise and assignment before expiry are refused for a European series, in
-- the lifecycle proposal and in the event primitive. Each anchor is the notice
-- test migration 0382 placed ahead of the exercise window, with the test that
-- follows it, so the style is refused first and under one code whatever the
-- product's notice rule states.
DO $$DECLARE signature regprocedure; definition text; anchor text; refusal text;
BEGIN
 FOR signature,anchor,refusal IN SELECT * FROM (VALUES
  ('lifecycle.option_proposal_base(uuid,jsonb)'::regprocedure,
   'PERFORM ref_data.require_option_notice(product,s,kind,at);'||E'\n  '||'IF kind IN(''exercise'',''assignment'') THEN',
   'IF kind IN(''exercise'',''assignment'') AND product.exercise_style=''european'' THEN RAISE EXCEPTION ''option_exercise_style'';END IF;'),
  ('security.apply_futures_option_event(uuid,uuid,uuid,text,numeric,timestamptz,uuid,uuid[],jsonb)'::regprocedure,
   'PERFORM ref_data.require_option_notice(product,series_row,event_kind,event_at);'||E'\n  '||'IF event_at<r.execution_at OR security.futures_option_market_date',
   'IF product.exercise_style=''european'' THEN RAISE EXCEPTION ''option_exercise_style'' USING ERRCODE=''P0001'';END IF;')
 ) v(fn,anchor,refusal) LOOP
  definition=pg_get_functiondef(signature);
  IF length(definition)-length(replace(definition,anchor,''))<>length(anchor)
  THEN RAISE EXCEPTION 'option_exercise_style_definition_changed'; END IF;
  EXECUTE replace(definition,anchor,refusal||E'\n  '||anchor);
 END LOOP;
END$$;
COMMIT;
