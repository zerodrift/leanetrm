-- Curve marks are shared market facts. An ordered, exact-version pair has one correlation.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.volatility_marks(
 catalog_id uuid NOT NULL,id uuid NOT NULL,operation_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 curve_code ref_data.code NOT NULL,
 bucket_month date NOT NULL CHECK(extract(day FROM bucket_month)=1 AND bucket_month>=DATE '1901-01-01' AND bucket_month<DATE '2200-01-01'),
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 volatility ref_data.exact_number NOT NULL CHECK(volatility BETWEEN 0 AND 10 AND scale(volatility)<=12),
 convention text NOT NULL CHECK(convention='curve_marks_log_return_held_v1'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),
 UNIQUE(catalog_id,definition_revision_id,curve_code,bucket_month,revision),
 UNIQUE(catalog_id,definition_revision_id,curve_code,bucket_month,id),
 UNIQUE(catalog_id,definition_revision_id,curve_code,bucket_month,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,curve_code,bucket_month,previous_id) REFERENCES trading.volatility_marks(catalog_id,definition_revision_id,curve_code,bucket_month,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);

CREATE TABLE trading.correlation_marks(
 catalog_id uuid NOT NULL,id uuid NOT NULL,operation_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 first_curve_code ref_data.code NOT NULL,second_curve_code ref_data.code NOT NULL,
 bucket_month date NOT NULL CHECK(extract(day FROM bucket_month)=1 AND bucket_month>=DATE '1901-01-01' AND bucket_month<DATE '2200-01-01'),
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 correlation ref_data.exact_number NOT NULL CHECK(correlation BETWEEN -1 AND 1 AND scale(correlation)<=12),
 convention text NOT NULL CHECK(convention='curve_marks_log_return_held_v1'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),
 UNIQUE(catalog_id,definition_revision_id,first_curve_code,second_curve_code,bucket_month,revision),
 UNIQUE(catalog_id,definition_revision_id,first_curve_code,second_curve_code,bucket_month,id),
 UNIQUE(catalog_id,definition_revision_id,first_curve_code,second_curve_code,bucket_month,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),
 CHECK(first_curve_code COLLATE "C"<second_curve_code COLLATE "C"),
 FOREIGN KEY(catalog_id,definition_revision_id,first_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,second_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,first_curve_code,second_curve_code,bucket_month,previous_id) REFERENCES trading.correlation_marks(catalog_id,definition_revision_id,first_curve_code,second_curve_code,bucket_month,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);

CREATE TABLE trading.curve_mark_requests(
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),actor_id uuid NOT NULL REFERENCES security.principals(id),
 kind text NOT NULL CHECK(kind IN('volatility','correlation')),operation_id uuid NOT NULL,command jsonb NOT NULL,
 effective_at ref_data.instant NOT NULL,outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,kind,operation_id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['volatility_marks','correlation_marks','curve_mark_requests'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY internal_content ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

CREATE VIEW trading.curve_mark_facts WITH(security_invoker=true) AS
 SELECT 'volatility'::text kind,catalog_id,definition_revision_id,curve_code first_curve,NULL::text second_curve,
  bucket_month,id,revision,volatility AS amount,publication_order,effective_at,convention FROM trading.volatility_marks
 UNION ALL
 SELECT 'correlation',catalog_id,definition_revision_id,first_curve_code,second_curve_code,
  bucket_month,id,revision,correlation,publication_order,effective_at,convention FROM trading.correlation_marks;
ALTER VIEW trading.curve_mark_facts OWNER TO openetrm_security_owner;
REVOKE ALL ON trading.curve_mark_facts FROM PUBLIC;

CREATE FUNCTION trading.require_curve_mark_sources(catalog uuid,definition uuid,curves text[],pair boolean) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE visible integer;quote_scopes integer;
BEGIN
 SELECT count(*),count(DISTINCT (c.reference_catalog_id,c.reference_revision_id,q.currency_code))
 INTO visible,quote_scopes FROM market_data.curves c JOIN ref_data.quote_specifications q
  ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
 WHERE(c.catalog_id,c.revision_id)=(catalog,definition) AND c.code=ANY(curves)
  AND security.reference_access(c.reference_catalog_id,'ReadMarketData') IS TRUE
  AND security.reference_version_visible(c.reference_catalog_id,c.reference_revision_id) IS TRUE
  AND q.kind='outright' AND q.multiplier=1;
 IF security.market_fact_visible(catalog,definition,NULL) IS NOT TRUE OR visible<>cardinality(curves)
 THEN RAISE EXCEPTION 'curve_mark_source' USING ERRCODE='42501';END IF;
 IF pair AND quote_scopes<>1 THEN RAISE EXCEPTION 'curve_mark_pair';END IF;
END$$;

CREATE FUNCTION trading.record_curve_marks(catalog uuid,body jsonb,effective timestamptz,mark_kind text) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE actor uuid;operation uuid;definition uuid;first_code text;second_code text;source_keys text[];
 prior trading.curve_mark_requests;head trading.curve_mark_facts;item jsonb;month date;amount_value numeric;
 seen date[]=ARRAY[]::date[];publication integer;mark uuid;results jsonb='[]';outcome jsonb;
BEGIN
 actor=security.spread_mark_scope(catalog,true);
 source_keys=CASE mark_kind WHEN 'volatility' THEN ARRAY['curve'] WHEN 'correlation' THEN ARRAY['firstCurve','secondCurve'] END;
 IF source_keys IS NULL OR effective IS NULL OR NOT isfinite(effective) OR effective>clock_timestamp() OR pg_column_size(body)>65536
  OR trading.rec_keys(body,ARRAY['operationId','convention','definitionRevisionId','reason','marks']||source_keys) IS NOT TRUE
  OR EXISTS(SELECT 1 FROM jsonb_each(body)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE WHEN e.key='marks' THEN 'array' ELSE 'string' END)
  OR body->>'convention' IS DISTINCT FROM 'curve_marks_log_return_held_v1'
  OR nullif(btrim(body->>'reason'),'') IS NULL OR length(body->>'reason')>500 OR jsonb_array_length(body->'marks') NOT BETWEEN 1 AND 240
 THEN RAISE EXCEPTION 'curve_mark_input';END IF;
 operation=(body->>'operationId')::uuid;definition=(body->>'definitionRevisionId')::uuid;
 IF operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'curve_mark_input';END IF;
 IF mark_kind='volatility' THEN first_code=body->>'curve';
 ELSE
  first_code=least((body->>'firstCurve') COLLATE "C",(body->>'secondCurve') COLLATE "C");
  second_code=greatest((body->>'firstCurve') COLLATE "C",(body->>'secondCurve') COLLATE "C");
  IF first_code=second_code THEN RAISE EXCEPTION 'curve_mark_pair';END IF;
  body=body||jsonb_build_object('firstCurve',first_code,'secondCurve',second_code);
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.spread_mark_scope(catalog,true);
 PERFORM trading.require_curve_mark_sources(catalog,definition,array_remove(ARRAY[first_code,second_code],NULL),mark_kind='correlation');
 SELECT * INTO prior FROM trading.curve_mark_requests WHERE(catalog_id,actor_id,kind,operation_id)=(catalog,actor,mark_kind,operation);
 IF FOUND THEN
  IF (prior.command,prior.effective_at) IS DISTINCT FROM (body,effective) THEN RAISE EXCEPTION 'curve_mark_request_conflict';END IF;
  RETURN prior.outcome;
 END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'marks') LOOP
  IF trading.rec_keys(item,ARRAY['month','expectedRevision',mark_kind]) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM jsonb_each(item)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE WHEN e.key='expectedRevision' THEN 'number' ELSE 'string' END)
   OR item->>'month' !~ '^[0-9]{4}-(0[1-9]|1[0-2])$' OR item->>'expectedRevision' !~ '^(0|[1-9][0-9]{0,8})$'
   OR item->>mark_kind !~ '^-?[0-9]+([.][0-9]{1,12})?$'
  THEN RAISE EXCEPTION 'curve_mark_input';END IF;
  month=(item->>'month'||'-01')::date;amount_value=(item->>mark_kind)::numeric;
  IF month=ANY(seen) OR month<DATE '1901-01-01' OR month>=DATE '2200-01-01'
   OR mark_kind='volatility' AND amount_value NOT BETWEEN 0 AND 10
   OR mark_kind='correlation' AND abs(amount_value)>1 THEN RAISE EXCEPTION 'curve_mark_value';END IF;
  seen=seen||month;
  -- A matching historical fact has priority over the head, even after a correction.
  SELECT m.* INTO head FROM trading.curve_mark_facts m
   WHERE(m.catalog_id,m.kind,m.definition_revision_id,m.first_curve,m.bucket_month)=(catalog,mark_kind,definition,first_code,month)
    AND m.second_curve IS NOT DISTINCT FROM second_code
   ORDER BY ((m.amount,m.effective_at)=(amount_value,effective)) DESC,m.revision DESC LIMIT 1;
  IF FOUND AND (head.amount,head.effective_at)=(amount_value,effective) THEN
   results=results||jsonb_build_array(jsonb_build_object('month',item->>'month','markId',head.id,'revision',head.revision,'changed',false));
   CONTINUE;
  END IF;
  IF coalesce(head.revision,0)<>(item->>'expectedRevision')::integer THEN RAISE EXCEPTION 'curve_mark_stale';END IF;
  IF head.effective_at>effective THEN RAISE EXCEPTION 'curve_mark_time';END IF;
  IF publication IS NULL THEN
   SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
   INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  END IF;
  mark=gen_random_uuid();
  IF mark_kind='volatility' THEN
   INSERT INTO trading.volatility_marks VALUES(catalog,mark,operation,definition,first_code,month,coalesce(head.revision,0)+1,head.id,
    amount_value,body->>'convention',actor,publication,effective);
  ELSE
   INSERT INTO trading.correlation_marks VALUES(catalog,mark,operation,definition,first_code,second_code,month,coalesce(head.revision,0)+1,head.id,
    amount_value,body->>'convention',actor,publication,effective);
  END IF;
  results=results||jsonb_build_array(jsonb_build_object('month',item->>'month','markId',mark,'revision',coalesce(head.revision,0)+1,'changed',true));
 END LOOP;
 outcome=jsonb_build_object('operationId',operation,'marks',results);
 INSERT INTO trading.curve_mark_requests VALUES(catalog,actor,mark_kind,operation,body,effective,outcome);
 PERFORM security.spread_mark_scope(catalog,true);
 PERFORM trading.require_curve_mark_sources(catalog,definition,array_remove(ARRAY[first_code,second_code],NULL),mark_kind='correlation');
 RETURN outcome;
EXCEPTION WHEN invalid_text_representation OR invalid_datetime_format OR datetime_field_overflow OR numeric_value_out_of_range
 THEN RAISE EXCEPTION 'curve_mark_input';
END$$;

CREATE FUNCTION security.record_volatility_marks(catalog uuid,body jsonb,effective timestamptz) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT trading.record_curve_marks(catalog,body,effective,'volatility');
$$;
CREATE FUNCTION security.record_correlation_marks(catalog uuid,body jsonb,effective timestamptz) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT trading.record_curve_marks(catalog,body,effective,'correlation');
$$;

CREATE FUNCTION security.read_curve_marks(catalog uuid,definition uuid,curves text[],months date[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q security.request_contexts;job risk.jobs;result jsonb;
BEGIN
 PERFORM security.spread_mark_scope(catalog,false);
 IF curves IS NULL OR cardinality(curves) NOT BETWEEN 1 AND 240 OR array_position(curves,NULL) IS NOT NULL
  OR cardinality(curves)<>(SELECT count(DISTINCT x) FROM unnest(curves)x)
  OR months IS NULL OR cardinality(months) NOT BETWEEN 1 AND 240 OR array_position(months,NULL) IS NOT NULL
  OR cardinality(months)<>(SELECT count(DISTINCT x) FROM unnest(months)x)
  OR EXISTS(SELECT 1 FROM unnest(months)x WHERE extract(day FROM x)<>1 OR x<DATE '1901-01-01' OR x>=DATE '2200-01-01')
 THEN RAISE EXCEPTION 'curve_mark_input';END IF;
 SELECT c.* INTO q FROM security.request_contexts c JOIN security.operation_trading t
  ON(t.catalog_id,t.context_id)=(c.market_catalog_id,c.market_context_id)
  WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND c.expires_at>clock_timestamp() AND c.trading_as_of IS NOT NULL;
 IF q.id IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') THEN
  job=risk.bound_lifecycle_reader();
  IF (catalog,q.market_context_id,q.trading_as_of) IS DISTINCT FROM(job.catalog_id,job.context_id,job.valuation_at)
  THEN RAISE EXCEPTION 'risk_job_scope' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM security.require_trading_history(catalog,q.market_context_id);
 PERFORM trading.require_curve_mark_sources(catalog,definition,curves,false);
 WITH latest AS (
  SELECT DISTINCT ON(m.kind,m.first_curve,m.second_curve,m.bucket_month) m.*
  FROM trading.curve_mark_facts m
  WHERE(m.catalog_id,m.definition_revision_id)=(catalog,definition) AND m.first_curve=ANY(curves)
   AND (m.second_curve IS NULL OR m.second_curve=ANY(curves)) AND m.bucket_month=ANY(months)
   AND m.effective_at<=q.trading_as_of AND security.trading_visible(catalog,m.publication_order,m.effective_at)
  ORDER BY m.kind,m.first_curve,m.second_curve,m.bucket_month,m.revision DESC
 ), formatted AS (
  SELECT kind,first_curve,second_curve,bucket_month,
   jsonb_build_object('month',to_char(bucket_month,'YYYY-MM'),'markId',id,'revision',revision,'effectiveAt',effective_at,'convention',convention,
    kind,trim_scale(amount)::text)||CASE WHEN kind='volatility' THEN jsonb_build_object('curve',first_curve)
    ELSE jsonb_build_object('firstCurve',first_curve,'secondCurve',second_curve) END AS mark FROM latest
 )
 SELECT jsonb_build_object(
  'volatilities',coalesce(jsonb_agg(mark ORDER BY first_curve,bucket_month) FILTER(WHERE kind='volatility'),'[]'),
  'correlations',coalesce(jsonb_agg(mark ORDER BY first_curve,second_curve,bucket_month) FILTER(WHERE kind='correlation'),'[]'))
 INTO result FROM formatted;
 PERFORM security.spread_mark_scope(catalog,false);
 PERFORM trading.require_curve_mark_sources(catalog,definition,curves,false);
 RETURN result;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY[
  'trading.require_curve_mark_sources(uuid,uuid,text[],boolean)'::regprocedure,
  'trading.record_curve_marks(uuid,jsonb,timestamptz,text)'::regprocedure,
  'security.record_volatility_marks(uuid,jsonb,timestamptz)'::regprocedure,
  'security.record_correlation_marks(uuid,jsonb,timestamptz)'::regprocedure,
  'security.read_curve_marks(uuid,uuid,text[],date[])'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.record_volatility_marks(uuid,jsonb,timestamptz),security.record_correlation_marks(uuid,jsonb,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.read_curve_marks(uuid,uuid,text[],date[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
