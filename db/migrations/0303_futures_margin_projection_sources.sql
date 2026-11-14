-- Current consumer: the economic worker checks that its accepted past margin
-- prefix has no unaccepted official correction. Financial facts need not be
-- variable graph inputs. No caller supplies observation IDs or source windows.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $required$BEGIN
 IF to_regprocedure('risk.bound_lifecycle_reader()') IS NULL
  OR to_regprocedure('settlement_data.read_futures_margin_state(uuid,uuid,uuid,uuid,timestamptz)') IS NULL
  OR to_regprocedure('security.require_admitted_business_source(uuid,uuid,timestamptz,date,timestamptz)') IS NULL
 THEN RAISE EXCEPTION 'futures_margin_projection_upgrade: native source and worker prerequisites missing';END IF;
END$required$;

-- Private relational producer permits a complete byte-budget check before
-- aggregating JSON. Certified event/publication prefixes are immutable, so the
-- budget and materialization statements read the same source set.
CREATE FUNCTION settlement_data.futures_margin_projection_source_rows(p settlement_data.futures_margin_policies,
 context uuid,opening timestamptz,executed timestamptz)
 RETURNS TABLE(observation market_data.observations,known_at timestamptz)
 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $function$
 SELECT o,k.known_from
 FROM market_data.curves c JOIN market_data.series s
  ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=(c.catalog_id,c.revision_id,p.contract_id,p.quote_code)
 JOIN settlement_data.futures_margin_policy_occurrences occurrence
  ON(occurrence.catalog_id,occurrence.policy_id,occurrence.policy_revision)=(p.catalog_id,p.id,p.revision)
 JOIN market_data.observations o
  ON(o.catalog_id,o.definition_revision_id,o.series_id,o.source_code,o.role,o.observed_at)=
   (p.catalog_id,p.definition_revision_id,s.id,c.direct_source_code,
    CASE occurrence.phase WHEN 'final' THEN 'final_contract_settlement' ELSE 'daily_settlement' END,occurrence.at)
 JOIN market_data.knowledge_events e
  ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
 JOIN market_data.market_knowledge_receipts k
  ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
 JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(p.catalog_id,context)
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(m.catalog_id,m.context_id)
 WHERE(c.catalog_id,c.revision_id,c.code)=(p.catalog_id,p.definition_revision_id,p.settlement_curve_code)
  AND occurrence.at<=opening AND(occurrence.at>executed OR occurrence.at=executed AND p.opening='include_opening_instant')
  AND e.event_order<=m.known_through AND k.known_from<=t.known_at;
$function$;
ALTER FUNCTION settlement_data.futures_margin_projection_source_rows(settlement_data.futures_margin_policies,uuid,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.futures_margin_projection_source_rows(settlement_data.futures_margin_policies,uuid,timestamptz,timestamptz) FROM PUBLIC;

CREATE FUNCTION settlement_data.read_futures_margin_sources(catalog uuid,trade uuid,binding uuid,context uuid,
 as_of timestamptz,business_date date,until_at timestamptz) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $function$
DECLARE native jsonb;p settlement_data.futures_margin_policies;curve market_data.curves;executed timestamptz;
 payload jsonb;occurrences jsonb;bytes bigint;BEGIN
 -- The original admission cannot be renewed by a later grant. The existing
 -- margin reader verifies native adoption, exact held revision/definition and,
 -- for workers, the bound job/attempt and retained trade/binding scope.
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,until_at);
 native=settlement_data.read_futures_margin_state(catalog,trade,binding,context,as_of);
 IF(native->>'available')::boolean IS NOT TRUE THEN RAISE EXCEPTION 'futures_margin_unavailable';END IF;
 p=jsonb_populate_record(NULL::settlement_data.futures_margin_policies,native->'policy');
 executed=(native#>>'{origin,execution_at}')::timestamptz;
 PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,p.definition_revision_id);
 SELECT * INTO STRICT curve FROM market_data.curves c
  WHERE(c.catalog_id,c.revision_id,c.code)=(p.catalog_id,p.definition_revision_id,p.settlement_curve_code);
 IF curve.rule<>'direct' OR curve.direct_role IS DISTINCT FROM 'daily_settlement' OR curve.direct_source_code IS NULL
 THEN RAISE EXCEPTION 'futures_settlement_evidence';END IF;
 SELECT coalesce(sum(octet_length(to_jsonb(r.observation)::text)+octet_length(to_jsonb(r.known_at)::text)+64),0)
 INTO bytes FROM settlement_data.futures_margin_projection_source_rows(p,context,as_of,executed)r;
 -- Every UUID needs at most 40 JSON bytes; the remaining envelope is under
 -- 1 KiB. Count the complete saved policy conservatively, including future
 -- occurrences, before building either response array.
 IF bytes+1024+40*p.occurrence_count>16777216 THEN RAISE EXCEPTION 'futures_margin_source_capacity';END IF;
 SELECT coalesce(jsonb_agg(jsonb_build_object('observation',to_jsonb(r.observation),'knownAt',r.known_at)
  ORDER BY(r.observation).observed_at,(r.observation).role,r.known_at,(r.observation).id),'[]'::jsonb)
 INTO payload FROM settlement_data.futures_margin_projection_source_rows(p,context,as_of,executed)r;
 SELECT coalesce(jsonb_agg(o.id ORDER BY o.at,o.phase,o.id),'[]'::jsonb) INTO occurrences
 FROM settlement_data.futures_margin_policy_occurrences o
 WHERE(o.catalog_id,o.policy_id,o.policy_revision)=(p.catalog_id,p.id,p.revision)
  AND o.at<=as_of AND(o.at>executed OR o.at=executed AND p.opening='include_opening_instant');
 PERFORM settlement_data.require_futures_margin_reference(p);
 PERFORM security.require_admitted_business_source(catalog,context,as_of,business_date,until_at);
 RETURN jsonb_build_object('contextId',context,'asOf',as_of,'businessDate',business_date,
  'knownAt',native->'knownAt','authorityUntil',until_at,'occurrenceIds',occurrences,'observations',payload);
END$function$;
ALTER FUNCTION settlement_data.read_futures_margin_sources(uuid,uuid,uuid,uuid,timestamptz,date,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.read_futures_margin_sources(uuid,uuid,uuid,uuid,timestamptz,date,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.read_futures_margin_sources(uuid,uuid,uuid,uuid,timestamptz,date,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
