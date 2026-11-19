-- Trader marks for a two-curve spread option (D430): each curve's volatility and their correlation per delivery month.
-- A mark is market data shared by every option on the curve pair; it is revised, never overwritten.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.spread_marks(
 catalog_id uuid NOT NULL,id uuid NOT NULL,operation_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 long_curve_code ref_data.code NOT NULL,short_curve_code ref_data.code NOT NULL,
 bucket_month date NOT NULL CHECK(extract(day FROM bucket_month)=1 AND bucket_month>=DATE '1901-01-01' AND bucket_month<DATE '2200-01-01'),
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 long_volatility ref_data.exact_number NOT NULL CHECK(long_volatility>=0 AND long_volatility<=10 AND scale(long_volatility)<=12),
 short_volatility ref_data.exact_number NOT NULL CHECK(short_volatility>=0 AND short_volatility<=10 AND scale(short_volatility)<=12),
 correlation ref_data.exact_number NOT NULL CHECK(correlation BETWEEN -1 AND 1 AND scale(correlation)<=12),
 convention text NOT NULL CHECK(convention='spread_marks_log_return_held_v1'),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),
 UNIQUE(catalog_id,definition_revision_id,long_curve_code,short_curve_code,bucket_month,revision),
 UNIQUE(catalog_id,definition_revision_id,long_curve_code,short_curve_code,bucket_month,id),
 UNIQUE(catalog_id,definition_revision_id,long_curve_code,short_curve_code,bucket_month,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),CHECK(long_curve_code<>short_curve_code),
 FOREIGN KEY(catalog_id,definition_revision_id,long_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,short_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,long_curve_code,short_curve_code,bucket_month,previous_id)
  REFERENCES trading.spread_marks(catalog_id,definition_revision_id,long_curve_code,short_curve_code,bucket_month,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.spread_mark_requests(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),operation_id uuid NOT NULL,
 command jsonb NOT NULL,outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,operation_id),FOREIGN KEY(catalog_id) REFERENCES market_data.catalogs(id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['spread_marks','spread_mark_requests'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY internal_content ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

CREATE FUNCTION security.spread_mark_scope(catalog uuid,writing boolean) RETURNS uuid
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE
  OR(writing AND(security.market_access(catalog,'ImportObservations') IS NOT TRUE OR security.current_write_context() IS NOT TRUE))
  OR(pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND(writing OR risk.worker_read() IS NOT TRUE))
 THEN RAISE EXCEPTION 'spread_mark_denied' USING ERRCODE='42501';END IF;
 SELECT q.actor_id INTO actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.expires_at>clock_timestamp();
 IF actor IS NULL THEN RAISE EXCEPTION 'spread_mark_denied' USING ERRCODE='42501';END IF;
 RETURN actor;
END$$;
CREATE FUNCTION trading.require_spread_mark_curves(catalog uuid,definition uuid,long_curve text,short_curve text) RETURNS void
 LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE long_quote ref_data.quote_specifications;short_quote ref_data.quote_specifications;rc uuid;rr uuid;BEGIN
 SELECT c.reference_catalog_id,c.reference_revision_id INTO rc,rr FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,definition,long_curve);
 IF rc IS NULL OR security.market_fact_visible(catalog,definition,NULL) IS NOT TRUE OR security.reference_access(rc,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(rc,rr) IS NOT TRUE THEN RAISE EXCEPTION 'spread_mark_source' USING ERRCODE='42501';END IF;
 SELECT q.* INTO long_quote FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,definition,long_curve);
 SELECT q.* INTO short_quote FROM market_data.curves c JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(c.reference_catalog_id,c.reference_revision_id,c.quote_code)
  WHERE(c.catalog_id,c.revision_id,c.code,c.reference_catalog_id,c.reference_revision_id)=(catalog,definition,short_curve,rc,rr);
 IF short_quote.code IS NULL OR(long_quote.kind,long_quote.multiplier,short_quote.kind,short_quote.multiplier) IS DISTINCT FROM('outright',1::numeric,'outright',1::numeric)
  OR long_quote.currency_code<>short_quote.currency_code OR long_quote.commodity_code=short_quote.commodity_code
 THEN RAISE EXCEPTION 'spread_mark_curves';END IF;
END$$;

CREATE FUNCTION security.record_spread_marks(catalog uuid,body jsonb,effective timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;operation uuid;definition uuid;item jsonb;prior trading.spread_mark_requests;head trading.spread_marks;
 month date;long_vol numeric;short_vol numeric;rho numeric;publication integer;mark uuid;results jsonb='[]';outcome jsonb;seen date[]=ARRAY[]::date[];
BEGIN
 actor=security.spread_mark_scope(catalog,true);
 IF effective IS NULL OR NOT isfinite(effective) OR effective>clock_timestamp() OR pg_column_size(body)>65536
  OR trading.rec_keys(body,ARRAY['operationId','convention','definitionRevisionId','longCurve','shortCurve','reason','marks']) IS NOT TRUE
  OR EXISTS(SELECT 1 FROM jsonb_each(body)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE WHEN e.key='marks' THEN 'array' ELSE 'string' END)
  OR body->>'convention' IS DISTINCT FROM 'spread_marks_log_return_held_v1'
  OR nullif(btrim(body->>'reason'),'') IS NULL OR length(body->>'reason')>500 OR jsonb_array_length(body->'marks') NOT BETWEEN 1 AND 240
 THEN RAISE EXCEPTION 'spread_mark_input';END IF;
 operation=(body->>'operationId')::uuid;definition=(body->>'definitionRevisionId')::uuid;
 IF operation='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'spread_mark_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.spread_mark_scope(catalog,true);
 SELECT * INTO prior FROM trading.spread_mark_requests WHERE(catalog_id,actor_id,operation_id)=(catalog,actor,operation);
 IF FOUND THEN IF prior.command IS DISTINCT FROM body THEN RAISE EXCEPTION 'spread_mark_request_conflict';END IF;RETURN prior.outcome;END IF;
 PERFORM trading.require_spread_mark_curves(catalog,definition,body->>'longCurve',body->>'shortCurve');
 FOR item IN SELECT value FROM jsonb_array_elements(body->'marks') LOOP
  IF trading.rec_keys(item,ARRAY['month','expectedRevision','longVolatility','shortVolatility','correlation']) IS NOT TRUE
   OR EXISTS(SELECT 1 FROM jsonb_each(item)e WHERE jsonb_typeof(e.value) IS DISTINCT FROM CASE WHEN e.key='expectedRevision' THEN 'number' ELSE 'string' END)
   OR item->>'month' !~ '^[0-9]{4}-(0[1-9]|1[0-2])$' OR item->>'expectedRevision' !~ '^(0|[1-9][0-9]{0,8})$'
   OR item->>'longVolatility' !~ '^[0-9]+(\.[0-9]{1,12})?$' OR item->>'shortVolatility' !~ '^[0-9]+(\.[0-9]{1,12})?$'
   OR item->>'correlation' !~ '^-?[0-9]+(\.[0-9]{1,12})?$'
  THEN RAISE EXCEPTION 'spread_mark_input';END IF;
  month=(item->>'month'||'-01')::date;long_vol=(item->>'longVolatility')::numeric;short_vol=(item->>'shortVolatility')::numeric;rho=(item->>'correlation')::numeric;
  IF month=ANY(seen) OR month<DATE '1901-01-01' OR month>=DATE '2200-01-01' OR long_vol>10 OR short_vol>10 OR abs(rho)>1 THEN RAISE EXCEPTION 'spread_mark_value';END IF;
  seen=seen||month;
  SELECT * INTO head FROM trading.spread_marks m WHERE(m.catalog_id,m.definition_revision_id,m.long_curve_code,m.short_curve_code,m.bucket_month)=
   (catalog,definition,body->>'longCurve',body->>'shortCurve',month) ORDER BY m.revision DESC LIMIT 1;
  -- The same value at the same instant is the fact already recorded, whatever the caller believed the head to be.
  IF head.id IS NOT NULL AND(head.long_volatility,head.short_volatility,head.correlation,head.effective_at)=(long_vol,short_vol,rho,effective) THEN
   results=results||jsonb_build_array(jsonb_build_object('month',item->>'month','markId',head.id,'revision',head.revision,'changed',false));CONTINUE;
  END IF;
  IF coalesce(head.revision,0)<>(item->>'expectedRevision')::integer THEN RAISE EXCEPTION 'spread_mark_stale';END IF;
  IF head.effective_at>effective THEN RAISE EXCEPTION 'spread_mark_time';END IF;
  IF publication IS NULL THEN
   SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
   INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  END IF;
  mark=gen_random_uuid();
  INSERT INTO trading.spread_marks VALUES(catalog,mark,operation,definition,body->>'longCurve',body->>'shortCurve',month,coalesce(head.revision,0)+1,head.id,
   long_vol,short_vol,rho,body->>'convention',actor,publication,effective);
  results=results||jsonb_build_array(jsonb_build_object('month',item->>'month','markId',mark,'revision',coalesce(head.revision,0)+1,'changed',true));
 END LOOP;
 outcome=jsonb_build_object('operationId',operation,'marks',results);
 INSERT INTO trading.spread_mark_requests VALUES(catalog,actor,operation,body,outcome);
 PERFORM security.spread_mark_scope(catalog,true);
 PERFORM trading.require_spread_mark_curves(catalog,definition,body->>'longCurve',body->>'shortCurve');
 RETURN outcome;
EXCEPTION WHEN invalid_text_representation OR invalid_datetime_format OR datetime_field_overflow OR numeric_value_out_of_range OR null_value_not_allowed
 THEN RAISE EXCEPTION 'spread_mark_input';
END$$;
CREATE FUNCTION security.spread_mark_request_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;result jsonb;BEGIN
 actor=security.spread_mark_scope(catalog,false);
 SELECT r.outcome INTO result FROM trading.spread_mark_requests r WHERE(r.catalog_id,r.actor_id,r.operation_id)=(catalog,actor,operation);
 PERFORM security.spread_mark_scope(catalog,false);RETURN result;
END$$;

-- The mark in force for a month is its latest revision that is known and effective under the selected context.
-- A month with no such revision is absent from the result; the caller decides that the option is unpriced.
CREATE FUNCTION security.read_spread_marks(catalog uuid,definition uuid,long_curve text,short_curve text,months date[]) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context uuid;result jsonb;BEGIN
 PERFORM security.spread_mark_scope(catalog,false);
 IF months IS NULL OR cardinality(months) NOT BETWEEN 1 AND 240 OR array_position(months,NULL) IS NOT NULL
  OR(SELECT count(DISTINCT x) FROM unnest(months)x)<>cardinality(months) OR EXISTS(SELECT 1 FROM unnest(months)x WHERE extract(day FROM x)<>1)
 THEN RAISE EXCEPTION 'spread_mark_input';END IF;
 SELECT c.market_context_id INTO context FROM security.request_contexts c JOIN trading.contexts t ON(t.catalog_id,t.context_id)=(c.market_catalog_id,c.market_context_id)
  WHERE c.market_catalog_id=catalog AND c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
   AND c.runtime_login=session_user AND c.trading_as_of IS NOT NULL;
 IF context IS NULL THEN RAISE EXCEPTION 'knowledge_coverage';END IF;
 PERFORM security.require_trading_history(catalog,context);
 PERFORM trading.require_spread_mark_curves(catalog,definition,long_curve,short_curve);
 SELECT coalesce(jsonb_agg(jsonb_build_object('month',to_char(x.bucket_month,'YYYY-MM'),'markId',x.id,'revision',x.revision,
   'longVolatility',trim_scale(x.long_volatility)::text,'shortVolatility',trim_scale(x.short_volatility)::text,'correlation',trim_scale(x.correlation)::text,
   'convention',x.convention,'effectiveAt',x.effective_at) ORDER BY x.bucket_month),'[]') INTO result
 FROM(SELECT DISTINCT ON(m.bucket_month)m.* FROM trading.spread_marks m
  WHERE(m.catalog_id,m.definition_revision_id,m.long_curve_code,m.short_curve_code)=(catalog,definition,long_curve,short_curve) AND m.bucket_month=ANY(months)
   AND security.trading_visible(m.catalog_id,m.publication_order,m.effective_at) ORDER BY m.bucket_month,m.revision DESC)x;
 PERFORM security.spread_mark_scope(catalog,false);RETURN result;
END$$;

DO $$DECLARE f regprocedure;BEGIN
 FOR f IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('security','trading') AND p.proname LIKE '%spread_mark%' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.record_spread_marks(uuid,jsonb,timestamptz),security.spread_mark_request_status(uuid,uuid) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.read_spread_marks(uuid,uuid,text,text,date[]) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
