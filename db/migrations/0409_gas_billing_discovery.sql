-- Bounded discovery for the explicitly enrolled gas billing consumer.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

CREATE FUNCTION security.read_gas_billing_page(catalog uuid,books uuid[],context uuid,as_of timestamptz,
 from_day date,to_day date,cursor jsonb,maximum integer) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;r record;d record;items jsonb='[]';next_cursor jsonb;has_more boolean=false;
 after_trade uuid=(cursor->>'tradeId')::uuid;after_slot integer=(cursor->>'cashSlot')::integer;after_day date=(cursor->>'day')::date;
 members integer=0;counted integer=0;remaining integer;
BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 IF cardinality(books) IS NULL OR cardinality(books) NOT BETWEEN 1 AND 64 OR array_position(books,NULL) IS NOT NULL
  OR cardinality(books)<>(SELECT count(DISTINCT b) FROM unnest(books)b) OR maximum IS NULL OR maximum NOT BETWEEN 1 AND 8
  OR from_day IS NULL OR to_day IS NULL OR from_day>=to_day OR to_day-from_day>366
  OR (cursor IS NOT NULL AND (jsonb_typeof(cursor)<>'object' OR cursor-ARRAY['tradeId','cashSlot','day']<>'{}'::jsonb
   OR after_trade IS NULL OR after_slot IS NULL OR after_slot<0 OR after_day IS NULL OR after_day<from_day OR after_day>=to_day))
 THEN RAISE EXCEPTION 'gas_billing_scope';END IF;
 IF (SELECT count(*) FROM trading.books b WHERE b.catalog_id=catalog AND b.id=ANY(books)
  AND security.ppa_visible_at(b.catalog_id,b.publication_order,b.effective_at,context,as_of))<>cardinality(books)
 THEN RAISE EXCEPTION 'gas_billing_scope_denied' USING ERRCODE='42501';END IF;
 FOR r IN SELECT h.trade_id,h.instrument_id,h.revision,h.book_id,m.slot FROM
  (SELECT DISTINCT ON(trade_id) * FROM trading.gas_trade_revisions t WHERE t.catalog_id=catalog
   AND security.ppa_visible_at(t.catalog_id,t.publication_order,t.effective_at,context,as_of) ORDER BY trade_id,revision DESC)h
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id)=(h.catalog_id,h.instrument_id)
  WHERE h.book_id=ANY(books) AND m.role IN('consideration','fee','indexed_consideration')
   AND m.delivery_to>from_day AND m.delivery_from<to_day
   AND(after_trade IS NULL OR(h.trade_id,m.slot)>(after_trade,after_slot)
    OR(after_day<to_day-1 AND(h.trade_id,m.slot)=(after_trade,after_slot)))
  ORDER BY h.trade_id,m.slot LIMIT maximum+1 LOOP
  members=members+1;
  IF members>maximum OR counted=maximum THEN has_more=true;EXIT;END IF;
  remaining=maximum-counted;
  FOR d IN SELECT * FROM security.read_gas_billing_days(catalog,r.trade_id,r.instrument_id,r.slot,context,as_of,from_day,to_day,
   CASE WHEN(r.trade_id,r.slot)=(after_trade,after_slot) THEN after_day END,remaining+1) LOOP
   IF counted=maximum THEN has_more=true;EXIT;END IF;
   items=items||jsonb_build_array(to_jsonb(r)||to_jsonb(d));counted=counted+1;
   next_cursor=jsonb_build_object('tradeId',r.trade_id,'cashSlot',r.slot,'day',d.native_day);
  END LOOP;
  IF has_more THEN EXIT;END IF;
  next_cursor=jsonb_build_object('tradeId',r.trade_id,'cashSlot',r.slot,'day',to_day-1);
 END LOOP;
 PERFORM lifecycle.require_read(catalog,until_at);
 RETURN jsonb_build_object('rows',items,'hasMore',has_more,'next',CASE WHEN has_more THEN next_cursor END);
END$$;
ALTER FUNCTION security.read_gas_billing_page(uuid,uuid[],uuid,timestamptz,date,date,jsonb,integer) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_billing_page(uuid,uuid[],uuid,timestamptz,date,date,jsonb,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_billing_page(uuid,uuid[],uuid,timestamptz,date,date,jsonb,integer) TO openetrm_runtime;

CREATE FUNCTION security.read_gas_billing_components(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_trade_revisions;until_at timestamptz;result jsonb;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT * INTO h FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.ppa_visible_at(r.catalog_id,r.publication_order,r.effective_at,context,as_of) ORDER BY r.revision DESC LIMIT 1;
 IF h.trade_id IS NULL THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 PERFORM security.read_instrument_side_schedule_headers(catalog,h.instrument_id,context,as_of);
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY x.slot),'[]'::jsonb) INTO result FROM(
            SELECT h.revision,h.instrument_id,m.slot,m.code,m.role,m.stage,m.direction,m.price::text,m.payment_at,o.id obligation_id,
            m.share_n::text,m.share_d::text,m.part_kind,m.part_lower_n::text,m.part_lower_d::text,m.part_upper_n::text,m.part_upper_d::text,
            m.delivery_from,m.delivery_to,source.delivery_from source_from,source.delivery_to source_to,h.quantity::text held_base,
            EXISTS(SELECT 1 FROM trading.gas_exercise_right_entries e WHERE(e.catalog_id,e.instrument_id)=(m.catalog_id,m.instrument_id)
                AND e.slot IN(m.slot,m.source_slot))
            OR EXISTS(SELECT 1 FROM trading.gas_swing_members e WHERE(e.catalog_id,e.instrument_id,e.slot)=(m.catalog_id,m.instrument_id,m.slot))
            OR EXISTS(SELECT 1 FROM trading.gas_swing_rights e WHERE(e.catalog_id,e.instrument_id,e.physical_slot)=(m.catalog_id,m.instrument_id,m.source_slot)) right_dependent,
            q.catalog_id reference_catalog_id,q.revision_id reference_revision_id,q.quantity_code,q.currency_code,q.multiplier::text,cur.display_minor_units
            FROM LATERAL(SELECT * FROM trading.gas_component_terms
                WHERE(catalog_id,instrument_id)=(catalog,h.instrument_id) AND role IN('consideration','fee','indexed_consideration') OFFSET 0)m
            CROSS JOIN LATERAL(SELECT * FROM trading.gas_component_terms
                WHERE(catalog_id,instrument_id,slot)=(m.catalog_id,m.instrument_id,m.source_slot) OFFSET 0)source
            CROSS JOIN LATERAL(SELECT * FROM trading.gas_delivery_obligations
                WHERE(catalog_id,trade_id,instrument_id,slot)=(m.catalog_id,trade,m.instrument_id,m.source_slot) OFFSET 0)o
            CROSS JOIN LATERAL(SELECT * FROM market_data.gas_valuation_assignments
                WHERE(catalog_id,revision_id,code)=(m.catalog_id,m.definition_revision_id,m.assignment_code) OFFSET 0)a
            CROSS JOIN LATERAL(SELECT * FROM market_data.curves
                WHERE(catalog_id,revision_id,code)=(a.catalog_id,a.revision_id,a.curve_code) OFFSET 0)curve
            CROSS JOIN LATERAL(SELECT * FROM ref_data.quote_specifications
                WHERE(catalog_id,revision_id,code)=(a.reference_catalog_id,a.reference_revision_id,curve.quote_code) OFFSET 0)q
            CROSS JOIN LATERAL(SELECT * FROM ref_data.currencies
                WHERE(catalog_id,revision_id,code)=(q.catalog_id,q.revision_id,q.currency_code) OFFSET 0)cur
)x;
 PERFORM lifecycle.require_read(catalog,until_at);RETURN result;
END$$;
ALTER FUNCTION security.read_gas_billing_components(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_billing_components(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_billing_components(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;

-- Exact booked inputs remain readable after their setup's effective period.
-- The existing retained schedule boundary authorizes the binding and references.
CREATE FUNCTION security.read_gas_billing_inputs(catalog uuid,trade uuid,context uuid,as_of timestamptz,
 from_day date,to_day date,policy text) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_trade_revisions;until_at timestamptz;obligations jsonb;manual jsonb;selected jsonb;history jsonb;
BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 IF context IS NULL OR as_of IS NULL OR from_day IS NULL OR to_day IS NULL OR from_day>=to_day OR to_day-from_day>366
  OR policy IS NULL OR policy NOT IN('final_allocated_billing_v1','final_facility_allocated_billing_v1') THEN RAISE EXCEPTION 'gas_billing_scope';END IF;
 SELECT * INTO h FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of) ORDER BY revision DESC LIMIT 1;
 IF h.trade_id IS NULL THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 PERFORM security.read_instrument_side_schedule_headers(catalog,h.instrument_id,context,as_of);
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY slot),'[]'::jsonb) INTO obligations FROM(
  SELECT o.*,m.code,m.ratio_n::text,m.ratio_d::text,m.pipeline_code,m.zone_code,m.point_code,m.quantity_policy
  FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m USING(catalog_id,instrument_id,slot)
  WHERE(o.catalog_id,o.trade_id,o.instrument_id)=(catalog,trade,h.instrument_id))x;
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY obligation_id,kind,day),'[]'::jsonb) INTO manual FROM(
  SELECT DISTINCT ON(s.obligation_id,s.kind,r.day) r.*,s.obligation_id,s.kind,s.stage,s.source
  FROM trading.gas_delivery_obligations o JOIN trading.gas_quantity_series s ON(s.catalog_id,s.obligation_id)=(o.catalog_id,o.id)
  JOIN trading.gas_quantity_revisions r ON(r.catalog_id,r.series_id)=(s.catalog_id,s.id)
  WHERE(o.catalog_id,o.trade_id,o.instrument_id)=(catalog,trade,h.instrument_id) AND s.source='manual_daily_v1'
   AND r.day>=from_day AND r.day<to_day AND security.ppa_visible_at(catalog,s.publication_order,s.effective_at,context,as_of)
   AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)
  ORDER BY s.obligation_id,s.kind,r.day,r.revision DESC)x;
 IF policy='final_allocated_billing_v1' THEN selected=manual;
 ELSE
  -- Keep one head per owned ledger. Multiple heads for one obligation/day
  -- remain ambiguous evidence for the managed selector; never pick a ledger.
  SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY obligation_id,day,ledger_id),'[]'::jsonb) INTO selected FROM(
   SELECT DISTINCT ON(t.obligation_id,g.day,t.ledger_id) t.id,t.obligation_id,g.day,t.ledger_id,'allocated'::text kind,
    'facility_allocation_adoption_v1'::text source,CASE WHEN t.state='final' THEN 'final' ELSE 'withdrawn' END status,
    trim_scale(line.quantity)::text quantity,a.evidence,t.revision,a.effective_at,a.publication_order
   FROM trading.gas_delivery_obligations o JOIN trading.facility_adopted_targets t ON(t.catalog_id,t.obligation_id)=(o.catalog_id,o.id)
   JOIN trading.facility_quantity_adoptions a ON(a.catalog_id,a.id)=(t.catalog_id,t.adoption_id)
   JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(t.catalog_id,t.ledger_id)
   LEFT JOIN trading.facility_gas_allocations line ON(line.catalog_id,line.plan_id,line.obligation_id)=(t.catalog_id,t.plan_id,t.allocation_obligation_id)
   WHERE(o.catalog_id,o.trade_id,o.instrument_id)=(catalog,trade,h.instrument_id) AND g.day>=from_day AND g.day<to_day
    AND security.ppa_visible_at(catalog,a.publication_order,a.effective_at,context,as_of)
   ORDER BY t.obligation_id,g.day,t.ledger_id,t.revision DESC)x;
 END IF;
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY cash_slot,day),'[]'::jsonb) INTO history FROM(
  SELECT DISTINCT ON(d.cash_slot,d.day) d.* FROM trading.gas_cash_determinations d
  WHERE(d.catalog_id,d.trade_id)=(catalog,trade) AND d.day>=from_day AND d.day<to_day
   AND security.ppa_visible_at(catalog,d.publication_order,d.effective_at,context,as_of)
  ORDER BY d.cash_slot,d.day,d.ordinal DESC)x;
 PERFORM lifecycle.require_read(catalog,until_at);
 RETURN jsonb_build_object('header',to_jsonb(h),'obligations',obligations,'heads',selected,'manualHeads',manual,'history',history);
END$$;
ALTER FUNCTION security.read_gas_billing_inputs(uuid,uuid,uuid,timestamptz,date,date,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_billing_inputs(uuid,uuid,uuid,timestamptz,date,date,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_billing_inputs(uuid,uuid,uuid,timestamptz,date,date,text) TO openetrm_runtime;

CREATE FUNCTION security.read_gas_billing_contracts(catalog uuid,trade uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE h trading.gas_trade_revisions;until_at timestamptz;obligations jsonb;history jsonb;
BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 SELECT * INTO h FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade)
  AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of) ORDER BY revision DESC LIMIT 1;
 IF h.trade_id IS NULL THEN RAISE EXCEPTION 'gas_billing_conflict';END IF;
 PERFORM security.read_instrument_side_schedule_headers(catalog,h.instrument_id,context,as_of);
 SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY slot),'[]'::jsonb) INTO obligations FROM(
  SELECT o.*,m.code,m.ratio_n::text,m.ratio_d::text
  FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m USING(catalog_id,instrument_id,slot)
  WHERE(o.catalog_id,o.trade_id,o.instrument_id)=(catalog,trade,h.instrument_id))x;
 SELECT coalesce(jsonb_agg(to_jsonb(d)||jsonb_build_object('rounded_total',trim_scale(d.rounded_total)::text,
   'contribution',trim_scale(d.contribution)::text) ORDER BY d.cash_slot,d.day,d.ordinal),'[]'::jsonb) INTO history
  FROM trading.gas_cash_determinations d WHERE(d.catalog_id,d.trade_id)=(catalog,trade)
   AND security.ppa_visible_at(catalog,d.publication_order,d.effective_at,context,as_of);
 PERFORM lifecycle.require_read(catalog,until_at);
 RETURN jsonb_build_object('header',to_jsonb(h),'obligations',obligations,'history',history);
END$$;
ALTER FUNCTION security.read_gas_billing_contracts(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_billing_contracts(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_gas_billing_contracts(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;

COMMIT;
