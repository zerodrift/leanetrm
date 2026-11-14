-- Exact source-known members for the ordinary correction's typed forms.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE FUNCTION trading.gas_owned_quantity_choice_rows(catalog uuid,context uuid,as_of timestamptz,state jsonb)
 RETURNS TABLE(kind text,value jsonb) LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
 WITH positions AS MATERIALIZED(SELECT p.* FROM jsonb_to_recordset($4->'positions')p(id uuid,account_id uuid,kind text,origin_policy text,effective_at timestamptz,financial_revision integer)),
 consumptions AS MATERIALIZED(SELECT c.* FROM jsonb_to_recordset($4->'consumptions')c(id uuid,lot_id uuid,kind text,effective_at timestamptz,financial_revision integer))
 SELECT 'positions',jsonb_build_object('positionId',p.id,'accountId',p.account_id,'expectedRevision',p.financial_revision)
 FROM positions p WHERE p.kind='owned_stock' AND p.origin_policy='opening_owned_stock_v1' AND p.effective_at<=$3
 UNION ALL
 SELECT 'consumptions',jsonb_build_object('consumptionId',c.id,'accountId',p.account_id,'expectedRevision',c.financial_revision)
 FROM consumptions c JOIN positions p ON p.id=c.lot_id WHERE c.kind IN('delivery','loss') AND c.effective_at<=$3
  AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_claim_settlements s WHERE(s.catalog_id,s.consumption_id)=($1,c.id)
   AND trading.gas_owned_visible($1,s.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$2,$3))
  AND NOT EXISTS(SELECT 1 FROM trading.gas_futures_performance_quantity_supplies s JOIN trading.gas_futures_performance_quantities q
   ON(q.catalog_id,q.performance_id,q.revision)=(s.catalog_id,s.performance_id,s.performance_revision)
   WHERE(s.catalog_id,s.consumption_id)=($1,c.id) AND trading.gas_owned_visible($1,q.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',$2,$3))
$$;

CREATE FUNCTION security.read_gas_owned_quantity_choices(catalog uuid,context uuid,as_of timestamptz,account uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE state jsonb;until_at timestamptz;positions_count bigint;consumptions_count bigint;row_count bigint;row_bytes bigint;result jsonb;BEGIN
 until_at=security.gas_futures_delivery_discovery_authority(catalog);
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);
 state=security.read_gas_owned_operations(catalog,context,as_of,account);
 until_at=least(until_at,(state->>'authorityUntil')::timestamptz);
 IF state->>'quantityStateProfile' IS DISTINCT FROM 'gas_owned_financial_quantities_v1'
  OR(state->>'contextId')::uuid IS DISTINCT FROM context OR(state->>'asOf')::timestamptz IS DISTINCT FROM as_of
  OR account IS NOT NULL AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(state->'accounts')a WHERE(a->>'id')::uuid=account)
 THEN RAISE EXCEPTION 'gas_quantity_source';END IF;
 -- Form choices are a complete subset of this authorized source. Both the
 -- native state and added row tuples must fit before an array is aggregated.
 SELECT count(*) FILTER(WHERE r.kind='positions'),count(*) FILTER(WHERE r.kind='consumptions'),count(*),coalesce(sum(octet_length(convert_to(r.value::text,'UTF8'))),0)
  INTO positions_count,consumptions_count,row_count,row_bytes FROM trading.gas_owned_quantity_choice_rows(catalog,context,as_of,state)r;
 IF positions_count>16384 OR consumptions_count>16384 OR octet_length(convert_to(state::text,'UTF8'))::bigint+row_bytes+4*row_count+1024>16777216
 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 SELECT state||jsonb_build_object('quantityChoiceProfile','ordinary_gas_quantity_choices_v1','authorityUntil',until_at,
  'quantityPositionChoices',coalesce(jsonb_agg(r.value ORDER BY r.value->>'positionId') FILTER(WHERE r.kind='positions'),'[]'),
  'quantityConsumptionChoices',coalesce(jsonb_agg(r.value ORDER BY r.value->>'consumptionId') FILTER(WHERE r.kind='consumptions'),'[]'))
  INTO result FROM trading.gas_owned_quantity_choice_rows(catalog,context,as_of,state)r;
 IF octet_length(convert_to(result::text,'UTF8'))>16777216 THEN RAISE EXCEPTION 'gas_owned_scope';END IF;
 PERFORM security.require_gas_owned_read(catalog,context,as_of,until_at);RETURN result;
END$$;
ALTER FUNCTION trading.gas_owned_quantity_choice_rows(uuid,uuid,timestamptz,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_owned_quantity_choice_rows(uuid,uuid,timestamptz,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
ALTER FUNCTION security.read_gas_owned_quantity_choices(uuid,uuid,timestamptz,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_gas_owned_quantity_choices(uuid,uuid,timestamptz,uuid) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.read_gas_owned_quantity_choices(uuid,uuid,timestamptz,uuid) TO openetrm_runtime;
COMMIT;
