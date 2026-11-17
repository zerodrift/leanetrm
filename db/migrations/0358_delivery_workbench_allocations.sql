-- Explicit chosen-route allocations to existing booked gas delivery occurrences.
-- This bridge owns relationships only: no duplicated movement, title or cash.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- Page identities admit prompts only. Their report/tool capability remains
-- explicitly unsupported; extending this list grants no delivery operation.
DO $pages$DECLARE definition text;marker text:='''transport_route_plans''';BEGIN
 SELECT pg_get_functiondef(p.oid) INTO STRICT definition FROM pg_proc p WHERE p.oid='assistant.is_prompt_page(text)'::regprocedure
  AND md5(p.prosrc)='ae033949b4c7cd60b59de60789684537' AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
  AND NOT p.prosecdef AND p.provolatile='i' AND p.prokind='f' AND p.proparallel='u' AND NOT p.proisstrict AND NOT p.proleakproof AND NOT p.proretset
  AND p.provariadic=0 AND p.proargmodes IS NULL AND p.proargnames=ARRAY['page'] AND p.prorettype='boolean'::regtype
  AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='sql') AND p.proconfig=ARRAY['search_path=pg_catalog']
  AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[];
 IF(length(definition)-length(replace(definition,marker,'')))/length(marker)<>1
  OR definition LIKE '%''delivery_workbench_gas''%' OR definition LIKE '%''delivery_workbench_crude''%'
 THEN RAISE EXCEPTION 'delivery_workbench_upgrade: unexpected helper assistant.is_prompt_page(text)';END IF;
 EXECUTE replace(definition,marker,marker||',''delivery_workbench_gas'',''delivery_workbench_crude''');
EXCEPTION WHEN no_data_found THEN RAISE EXCEPTION 'delivery_workbench_upgrade: unexpected helper assistant.is_prompt_page(text)';END$pages$;

CREATE TABLE trading.delivery_route_allocation_plans(
 catalog_id uuid NOT NULL,id uuid NOT NULL,route_id uuid NOT NULL,route_stream_id uuid NOT NULL,route_plan_id uuid NOT NULL,
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,kind text NOT NULL CHECK(kind IN('allocate','release')),
 policy text NOT NULL CHECK(policy='exact_terminal_native_interval_v1'),evidence text NOT NULL,
 source_context_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,route_id,route_stream_id,id),UNIQUE(catalog_id,route_id,route_stream_id,revision),
 UNIQUE(catalog_id,route_id,route_stream_id,previous_id),CHECK((revision=1)=(previous_id IS NULL)),
 CHECK(kind<>'release' OR previous_id IS NOT NULL),CHECK(evidence=trading.gas_transport_capacity_evidence(evidence)),
 FOREIGN KEY(catalog_id,route_id,route_stream_id,route_plan_id) REFERENCES trading.gas_transport_route_plans(catalog_id,route_id,stream_id,id),
 FOREIGN KEY(catalog_id,route_id,route_stream_id,previous_id) REFERENCES trading.delivery_route_allocation_plans(catalog_id,route_id,route_stream_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES trading.contexts(catalog_id,context_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.delivery_route_allocations(
 catalog_id uuid NOT NULL,plan_id uuid NOT NULL,route_id uuid NOT NULL,node_id uuid NOT NULL,
 obligation_id uuid NOT NULL,trade_id uuid NOT NULL,trade_revision integer NOT NULL,instrument_id uuid NOT NULL,
 day date NOT NULL,quantity numeric NOT NULL CHECK(quantity>0 AND quantity<1e24 AND scale(quantity)<=18),
 direction integer NOT NULL CHECK(direction IN(-1,1)),reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,quantity_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,plan_id,node_id,obligation_id,day),
 FOREIGN KEY(catalog_id,plan_id) REFERENCES trading.delivery_route_allocation_plans(catalog_id,id),
 FOREIGN KEY(catalog_id,route_id,node_id) REFERENCES trading.gas_transport_route_nodes(catalog_id,route_id,id),
 FOREIGN KEY(catalog_id,obligation_id) REFERENCES trading.gas_delivery_obligations(catalog_id,id),
 FOREIGN KEY(catalog_id,trade_id,trade_revision,instrument_id) REFERENCES trading.gas_trade_revisions(catalog_id,trade_id,revision,instrument_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code)
);
CREATE INDEX delivery_route_allocation_targets ON trading.delivery_route_allocations(catalog_id,obligation_id,day,plan_id);
CREATE TABLE trading.delivery_route_allocation_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),route_id uuid NOT NULL,
 plan_id uuid NOT NULL,request jsonb NOT NULL,fact jsonb NOT NULL,outcome jsonb NOT NULL,authority_until ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,route_id) REFERENCES trading.gas_transport_routes(catalog_id,id),
 FOREIGN KEY(catalog_id,plan_id) REFERENCES trading.delivery_route_allocation_plans(catalog_id,id),CHECK(pg_column_size(request)<=1048576)
);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['delivery_route_allocation_plans','delivery_route_allocations','delivery_route_allocation_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;

CREATE FUNCTION trading.delivery_route_allocation_uuid(value text) RETURNS uuid
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF value IS NULL OR value!~*'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' OR value::uuid='00000000-0000-0000-0000-000000000000'
 THEN RAISE EXCEPTION 'delivery_workbench_input';END IF;RETURN value::uuid;
END$$;
CREATE FUNCTION trading.delivery_route_allocation_body(body jsonb) RETURNS jsonb
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb;rows jsonb;item jsonb;BEGIN
 IF pg_column_size(body)>1048576 THEN RAISE EXCEPTION 'delivery_workbench_input';END IF;
 PERFORM trading.gas_transport_capacity_keys(body,ARRAY['kind','scopeId','routePlanId','expectedPreviousId','expectedRouteStateVersion','policy','allocations','evidence']);
 IF body->>'kind' NOT IN('allocate','release') OR body->>'policy' IS DISTINCT FROM 'exact_terminal_native_interval_v1'
  OR jsonb_typeof(body->'kind') IS DISTINCT FROM 'string' OR jsonb_typeof(body->'policy') IS DISTINCT FROM 'string'
  OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string'
  OR jsonb_typeof(body->'expectedRouteStateVersion') IS DISTINCT FROM 'number' OR(body->>'expectedRouteStateVersion')!~'^[1-9][0-9]{0,8}$'
  OR jsonb_typeof(body->'allocations') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'allocations')>1024
 THEN RAISE EXCEPTION 'delivery_workbench_input';END IF;
 result=jsonb_build_object('kind',body->>'kind','scopeId',trading.delivery_route_allocation_uuid(body->>'scopeId'),
  'routePlanId',trading.delivery_route_allocation_uuid(body->>'routePlanId'),
  'expectedPreviousId',CASE WHEN body->'expectedPreviousId'='null'::jsonb THEN NULL ELSE trading.delivery_route_allocation_uuid(body->>'expectedPreviousId') END,
  'expectedRouteStateVersion',(body->>'expectedRouteStateVersion')::integer,'policy',body->>'policy',
  'evidence',trading.gas_transport_capacity_evidence(body->>'evidence'));
 FOR item IN SELECT value FROM jsonb_array_elements(body->'allocations') LOOP
  PERFORM trading.gas_transport_capacity_keys(item,ARRAY['nodeId','obligationId','tradeRevision','day','quantity']);
  IF jsonb_typeof(item->'tradeRevision') IS DISTINCT FROM 'number' OR(item->>'tradeRevision')!~'^[1-9][0-9]{0,8}$'
   OR jsonb_typeof(item->'quantity') IS DISTINCT FROM 'string' OR(item->>'quantity')!~'^(0|[1-9][0-9]*)([.][0-9]{1,18})?$'
   OR(item->>'quantity')::numeric<=0 OR(item->>'quantity')::numeric>=1e24
   OR jsonb_typeof(item->'day') IS DISTINCT FROM 'string' OR(item->>'day')!~'^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
   OR NOT isfinite((item->>'day')::date)
  THEN RAISE EXCEPTION 'delivery_workbench_input';END IF;
  PERFORM trading.delivery_route_allocation_uuid(item->>'nodeId');PERFORM trading.delivery_route_allocation_uuid(item->>'obligationId');
 END LOOP;
 SELECT coalesce(jsonb_agg(jsonb_build_object('nodeId',(r->>'nodeId')::uuid,'obligationId',(r->>'obligationId')::uuid,
  'tradeRevision',(r->>'tradeRevision')::integer,'day',(r->>'day')::date,'quantity',trim_scale((r->>'quantity')::numeric)::text)
  ORDER BY(r->>'nodeId')::uuid,(r->>'obligationId')::uuid,(r->>'day')::date),'[]') INTO rows FROM jsonb_array_elements(body->'allocations')r;
 IF(SELECT count(*)<>count(DISTINCT ROW(r->>'nodeId',r->>'obligationId',r->>'day')) FROM jsonb_array_elements(rows)r)
  OR(body->>'kind'='release' AND(jsonb_array_length(rows)<>0 OR body->'expectedPreviousId'='null'::jsonb))
  OR(body->>'kind'='allocate' AND jsonb_array_length(rows)=0)
 THEN RAISE EXCEPTION 'delivery_workbench_input';END IF;
 RETURN result||jsonb_build_object('allocations',rows);
EXCEPTION WHEN raise_exception THEN
 IF SQLERRM='gas_transport_capacity_input' THEN RAISE EXCEPTION 'delivery_workbench_input';ELSE RAISE;END IF;
END$$;
CREATE FUNCTION trading.delivery_route_allocation_fact(body jsonb,at timestamptz) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT(body-ARRAY['expectedPreviousId','expectedRouteStateVersion'])||jsonb_build_object('effectiveAt',trading.gas_transport_capacity_instant(at))
$$;

-- The same source envelope used by the managed route assessment is checked
-- independently before an allocation may consume it. No second capacity debit.
CREATE FUNCTION trading.require_delivery_route_source(catalog uuid,route uuid,selected uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE facts jsonb;plan jsonb;item jsonb;current_source jsonb;source_plan jsonb;BEGIN
 facts=security.read_gas_transport_route(catalog,route);
 SELECT p INTO plan FROM jsonb_array_elements(facts->'plans')p WHERE(p->>'id')::uuid=selected;
 IF plan IS NULL OR plan->>'kind'<>'plan' OR(facts->>'hasFutureHeads')::boolean
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(facts->'sourceCapacities')c WHERE(c->>'hasFutureHeads')::boolean)
 THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(plan->'sources') LOOP
  SELECT c INTO current_source FROM jsonb_array_elements(facts->'sourceCapacities')c WHERE(c#>>'{entitlement,id}')::uuid=(item->>'entitlement_id')::uuid;
  IF current_source IS NULL OR(current_source#>>'{version,id}')::uuid<>(item->>'capacity_version_id')::uuid
   OR(current_source->>'stateVersion')::integer<>(item->>'capacity_generation')::integer
  THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
 END LOOP;
 FOR item IN SELECT value FROM jsonb_array_elements(plan->'source_plans') LOOP
  SELECT p INTO source_plan FROM jsonb_array_elements(facts->'sourceCapacities')c CROSS JOIN LATERAL jsonb_array_elements(c->'sourcePlans')p
   WHERE(p->>'firstPlanRevisionId')::uuid=(item->>'first_plan_revision_id')::uuid;
  IF source_plan IS NULL OR(source_plan->>'planRevisionId')::uuid<>(item->>'plan_revision_id')::uuid
   OR source_plan->>'membershipState' IS DISTINCT FROM 'owned_by_this_route_v1'
  THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
 END LOOP;
 RETURN facts;
END$$;

-- Fresh admission must agree with global heads after the publication gate as
-- well as the certified input cut. Historical inspections use the cut-only
-- source helper above and retain their original valid evidence.
CREATE FUNCTION trading.require_delivery_route_current_heads(catalog uuid,route uuid,selected uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE facts jsonb;item record;BEGIN
 facts=trading.require_delivery_route_source(catalog,route,selected);
 IF trading.gas_transport_route_head(catalog,route)<>(facts->>'stateVersion')::integer THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
 FOR item IN SELECT * FROM trading.gas_transport_route_sources s WHERE(s.catalog_id,s.route_plan_id)=(catalog,selected) LOOP
  IF trading.gas_transport_capacity_head(catalog,item.entitlement_id)<>item.capacity_generation THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
 END LOOP;
END$$;

CREATE FUNCTION trading.require_delivery_allocation_target(catalog uuid,route uuid,node uuid,source_plan uuid,obligation uuid,flow_day date,revision integer,amount numeric,at timestamptz)
RETURNS trading.facility_gas_target LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE scope trading.gas_transport_routes;n trading.gas_transport_route_nodes;t trading.facility_gas_target;stage record;source_total numeric=0;seen integer=0;BEGIN
 SELECT * INTO STRICT scope FROM trading.gas_transport_routes WHERE(catalog_id,id)=(catalog,route);
 SELECT * INTO n FROM trading.gas_transport_route_nodes WHERE(catalog_id,route_id,id)=(catalog,route,node);
 IF n.id IS NULL OR n.role='no_storage_transit_v1' THEN RAISE EXCEPTION 'delivery_workbench_target';END IF;
 BEGIN SELECT * INTO STRICT t FROM security.facility_gas_target(catalog,obligation,flow_day,at);
 EXCEPTION WHEN no_data_found OR too_many_rows THEN RAISE EXCEPTION 'delivery_workbench_target';END;
 IF t.trade_revision<>revision OR(t.reference_catalog_id,t.reference_revision_id,t.quantity_code,t.product_code,t.pipeline_code,t.zone_code,t.point_code,t.legal_entity_id,t.legal_entity_revision,t.direction)
  IS DISTINCT FROM(scope.reference_catalog_id,scope.reference_revision_id,scope.quantity_code,scope.product_code,n.pipeline_code,n.zone_code,n.point_code,scope.legal_entity_id,scope.legal_entity_revision,
   CASE n.role WHEN 'external_receipt_v1' THEN 1 ELSE -1 END)
  OR(amount IS NOT NULL AND(amount<=0 OR amount*t.quantity_d>t.quantity_n))
 THEN RAISE EXCEPTION 'delivery_workbench_target';END IF;
 FOR stage IN SELECT CASE n.role WHEN 'external_receipt_v1' THEN c.receipt_from ELSE c.delivery_from END f,
   CASE n.role WHEN 'external_receipt_v1' THEN c.receipt_to ELSE c.delivery_to END e,
   CASE n.role WHEN 'external_receipt_v1' THEN c.receipt_quantity ELSE c.delivery_quantity END q
  FROM trading.gas_transport_route_flows f JOIN trading.gas_transport_route_segments s ON(s.catalog_id,s.route_id,s.id)=(f.catalog_id,f.route_id,f.segment_id)
  JOIN trading.gas_transport_capacity_claims c ON(c.catalog_id,c.id)=(f.catalog_id,f.claim_id)
  WHERE(f.catalog_id,f.route_id,f.route_plan_id)=(catalog,route,source_plan)
   AND(n.role='external_receipt_v1' AND s.from_node_id=node OR n.role='external_delivery_v1' AND s.to_node_id=node) LOOP
  IF(stage.f,stage.e) IS DISTINCT FROM(t.native_from,t.native_to) THEN RAISE EXCEPTION 'delivery_workbench_geometry';END IF;
  source_total=source_total+stage.q;seen=seen+1;
 END LOOP;
 IF seen=0 OR amount>source_total THEN RAISE EXCEPTION 'delivery_workbench_conservation';END IF;
 RETURN t;
END$$;

CREATE FUNCTION security.read_delivery_route_allocations(catalog uuid,allocation_plan uuid,require_current boolean) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;p trading.delivery_route_allocation_plans;source trading.gas_transport_route_plans;item record;facts jsonb;bound security.request_contexts;current_plan uuid;BEGIN
 deadline=security.gas_transport_route_authority(catalog,NULL);
 SELECT * INTO p FROM trading.delivery_route_allocation_plans WHERE(catalog_id,id)=(catalog,allocation_plan);
 IF p.id IS NULL THEN RAISE EXCEPTION 'delivery_workbench_source';END IF;
 PERFORM trading.require_gas_transport_route_reference(catalog,p.route_id);
 FOR item IN SELECT DISTINCT a.trade_id,a.reference_catalog_id,a.reference_revision_id FROM trading.delivery_route_allocations a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id) LOOP
  PERFORM security.require_gas_quantity_trade(catalog,item.trade_id,false);
  IF security.reference_access(item.reference_catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'delivery_workbench_denied' USING ERRCODE='42501';END IF;
 END LOOP;
 IF require_current THEN
  bound=trading.gas_transport_capacity_bound(catalog);
  IF NOT trading.gas_transport_capacity_known(catalog,p.publication_order,bound.market_context_id) OR p.effective_at>bound.trading_as_of OR p.kind<>'allocate'
  THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
  SELECT h.id INTO current_plan FROM trading.delivery_route_allocation_plans h WHERE(h.catalog_id,h.route_id,h.route_stream_id)=(catalog,p.route_id,p.route_stream_id)
   AND trading.gas_transport_capacity_known(catalog,h.publication_order,bound.market_context_id) ORDER BY h.revision DESC LIMIT 1;
  IF current_plan IS DISTINCT FROM p.id THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
  facts=trading.require_delivery_route_source(catalog,p.route_id,p.route_plan_id);
  FOR item IN SELECT * FROM trading.delivery_route_allocations a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id) LOOP
   PERFORM trading.require_delivery_allocation_target(catalog,p.route_id,item.node_id,p.route_plan_id,item.obligation_id,item.day,item.trade_revision,item.quantity,bound.trading_as_of);
  END LOOP;
 END IF;
 PERFORM security.require_gas_transport_route_commit(catalog,NULL,deadline);
 RETURN jsonb_build_object('plan',to_jsonb(p),'allocations',(SELECT coalesce(jsonb_agg(to_jsonb(a)||jsonb_build_object('quantity',trim_scale(a.quantity)::text) ORDER BY a.node_id,a.obligation_id,a.day),'[]') FROM trading.delivery_route_allocations a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id)),'source',facts);
END$$;

CREATE FUNCTION trading.verify_delivery_route_allocation(catalog uuid,selected uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE p trading.delivery_route_allocation_plans;prior trading.delivery_route_allocation_plans;source trading.gas_transport_route_plans;item record;native_target trading.facility_gas_target;used numeric;available numeric;command trading.delivery_route_allocation_commands;canonical jsonb;expected jsonb;bound security.request_contexts;BEGIN
 SELECT * INTO STRICT p FROM trading.delivery_route_allocation_plans WHERE(catalog_id,id)=(catalog,selected);
 bound=trading.gas_transport_capacity_bound(catalog);
 IF p.source_context_id IS DISTINCT FROM bound.market_context_id OR NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t
  WHERE(t.catalog_id,t.id,t.effective_on)=(catalog,p.source_context_id,(p.effective_at AT TIME ZONE 'UTC')::date))
 THEN RAISE EXCEPTION 'delivery_workbench_integrity';END IF;
 SELECT * INTO STRICT source FROM trading.gas_transport_route_plans WHERE(catalog_id,route_id,id)=(catalog,p.route_id,p.route_plan_id);
 IF p.effective_at<source.effective_at OR source.stream_id<>p.route_stream_id THEN RAISE EXCEPTION 'delivery_workbench_integrity';END IF;
 SELECT * INTO prior FROM trading.delivery_route_allocation_plans h WHERE(h.catalog_id,h.route_id,h.route_stream_id)=(catalog,p.route_id,p.route_stream_id) AND h.revision<p.revision ORDER BY revision DESC LIMIT 1;
 IF p.previous_id IS DISTINCT FROM prior.id OR p.revision<>coalesce(prior.revision,0)+1 OR(prior.id IS NOT NULL AND p.effective_at<prior.effective_at)
 THEN RAISE EXCEPTION 'delivery_workbench_conflict';END IF;
 IF p.kind='release' THEN
  IF EXISTS(SELECT 1 FROM trading.delivery_route_allocations a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id)) THEN RAISE EXCEPTION 'delivery_workbench_integrity';END IF;
 ELSE
  PERFORM trading.require_delivery_route_current_heads(catalog,p.route_id,p.route_plan_id);
  IF NOT EXISTS(SELECT 1 FROM trading.delivery_route_allocations a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id)) THEN RAISE EXCEPTION 'delivery_workbench_input';END IF;
  FOR item IN SELECT * FROM trading.delivery_route_allocations a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id) LOOP
   native_target=trading.require_delivery_allocation_target(catalog,p.route_id,item.node_id,p.route_plan_id,item.obligation_id,item.day,item.trade_revision,item.quantity,p.effective_at);
   IF native_target.trade_revision IS DISTINCT FROM(SELECT max(r.revision) FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,native_target.trade_id))
   THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
   IF item.route_id<>p.route_id OR(item.trade_id,item.instrument_id,item.direction,item.reference_catalog_id,item.reference_revision_id,item.quantity_code)
    IS DISTINCT FROM(native_target.trade_id,native_target.instrument_id,native_target.direction,native_target.reference_catalog_id,native_target.reference_revision_id,native_target.quantity_code)
   THEN RAISE EXCEPTION 'delivery_workbench_integrity';END IF;
   -- A future replacement/release is not available capacity at an earlier
   -- effective instant. This v1 refuses backdated admission across the complete
   -- target-associated stream lineage instead of inventing interval proration.
   IF EXISTS(SELECT 1 FROM(
      SELECT DISTINCT ON(h.route_id,h.route_stream_id)h.* FROM trading.delivery_route_allocation_plans h
      WHERE h.catalog_id=catalog AND h.publication_order<=p.publication_order ORDER BY h.route_id,h.route_stream_id,h.revision DESC)head
     WHERE head.effective_at>p.effective_at AND EXISTS(
      SELECT 1 FROM trading.delivery_route_allocation_plans history JOIN trading.delivery_route_allocations prior_line ON(prior_line.catalog_id,prior_line.plan_id)=(history.catalog_id,history.id)
      WHERE(history.catalog_id,history.route_id,history.route_stream_id)=(catalog,head.route_id,head.route_stream_id)
       AND history.publication_order<=p.publication_order AND(prior_line.obligation_id,prior_line.day)=(item.obligation_id,item.day)))
   THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
   SELECT coalesce(sum(a.quantity),0) INTO used FROM trading.delivery_route_allocations a JOIN(
    SELECT DISTINCT ON(route_id,route_stream_id)h.* FROM trading.delivery_route_allocation_plans h WHERE h.catalog_id=catalog AND h.publication_order<=p.publication_order
    ORDER BY route_id,route_stream_id,revision DESC)heads ON(heads.catalog_id,heads.id)=(a.catalog_id,a.plan_id)
    WHERE(a.catalog_id,a.obligation_id,a.day)=(catalog,item.obligation_id,item.day);
   IF used*native_target.quantity_d>native_target.quantity_n THEN RAISE EXCEPTION 'delivery_workbench_conservation';END IF;
  END LOOP;
  FOR item IN SELECT a.node_id,sum(a.quantity)quantity FROM trading.delivery_route_allocations a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id) GROUP BY a.node_id LOOP
   SELECT coalesce(sum(CASE n.role WHEN 'external_receipt_v1' THEN c.receipt_quantity ELSE c.delivery_quantity END),0) INTO available
    FROM trading.gas_transport_route_nodes n JOIN trading.gas_transport_route_segments s ON(s.catalog_id,s.route_id)=(n.catalog_id,n.route_id)
    JOIN trading.gas_transport_route_flows f ON(f.catalog_id,f.route_id,f.segment_id,f.route_plan_id)=(s.catalog_id,s.route_id,s.id,p.route_plan_id)
    JOIN trading.gas_transport_capacity_claims c ON(c.catalog_id,c.id)=(f.catalog_id,f.claim_id)
    WHERE(n.catalog_id,n.route_id,n.id)=(catalog,p.route_id,item.node_id)
     AND(n.role='external_receipt_v1' AND s.from_node_id=n.id OR n.role='external_delivery_v1' AND s.to_node_id=n.id);
   IF item.quantity>available THEN RAISE EXCEPTION 'delivery_workbench_conservation';END IF;
  END LOOP;
 END IF;
 SELECT * INTO command FROM trading.delivery_route_allocation_commands c WHERE(c.catalog_id,c.operation_id,c.plan_id,c.actor_id)=(catalog,p.id,p.id,p.actor_id) AND(c.outcome->>'changed')::boolean;
 IF command.operation_id IS NULL OR command.route_id<>p.route_id OR NOT EXISTS(SELECT 1 FROM trading.publications x WHERE(x.catalog_id,x.publication_order,x.publication_xid)=(catalog,p.publication_order,pg_current_xact_id()))
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.actor_id=p.actor_id AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'delivery_workbench_integrity';END IF;
 canonical=trading.delivery_route_allocation_body(command.request->'command');
 expected=jsonb_build_object('kind',p.kind,'scopeId',p.route_id,'routePlanId',p.route_plan_id,'expectedPreviousId',p.previous_id,
  'expectedRouteStateVersion',canonical->'expectedRouteStateVersion','policy',p.policy,'evidence',p.evidence,
  'allocations',(SELECT coalesce(jsonb_agg(jsonb_build_object('nodeId',a.node_id,'obligationId',a.obligation_id,'tradeRevision',a.trade_revision,'day',a.day,'quantity',trim_scale(a.quantity)::text)
    ORDER BY a.node_id,a.obligation_id,a.day),'[]') FROM trading.delivery_route_allocations a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id)));
 IF canonical IS DISTINCT FROM expected OR command.request IS DISTINCT FROM jsonb_build_object('command',canonical,'effectiveAt',trading.gas_transport_capacity_instant(p.effective_at))
  OR command.fact IS DISTINCT FROM trading.delivery_route_allocation_fact(canonical,p.effective_at)
  OR command.outcome IS DISTINCT FROM jsonb_build_object('allocationPlanId',p.id,'scopeId',p.route_id,'revision',p.revision,'changed',true)
 THEN RAISE EXCEPTION 'delivery_workbench_integrity';END IF;
 PERFORM security.require_gas_transport_route_commit(catalog,'plan',command.authority_until);
END$$;
CREATE FUNCTION trading.delivery_route_allocation_guard() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_TABLE_NAME='delivery_route_allocation_plans' THEN PERFORM trading.verify_delivery_route_allocation(NEW.catalog_id,NEW.id);
 ELSE PERFORM trading.verify_delivery_route_allocation(NEW.catalog_id,NEW.plan_id);END IF;RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER delivery_route_allocation_plan_integrity AFTER INSERT ON trading.delivery_route_allocation_plans DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.delivery_route_allocation_guard();
CREATE CONSTRAINT TRIGGER delivery_route_allocation_line_integrity AFTER INSERT ON trading.delivery_route_allocations DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.delivery_route_allocation_guard();

CREATE FUNCTION security.delivery_route_allocation_state(catalog uuid,route uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;bound security.request_contexts;source jsonb;plans jsonb;history jsonb;item record;BEGIN
 deadline=security.gas_transport_route_authority(catalog,NULL);bound=trading.gas_transport_capacity_bound(catalog);
 source=security.read_gas_transport_route(catalog,route);
 SELECT coalesce(jsonb_agg(to_jsonb(p)||jsonb_build_object('allocations',security.read_delivery_route_allocations(catalog,p.id,false)->'allocations') ORDER BY p.route_stream_id),'[]') INTO plans
 FROM(SELECT DISTINCT ON(route_stream_id)p.* FROM trading.delivery_route_allocation_plans p WHERE(p.catalog_id,p.route_id)=(catalog,route)
  AND trading.gas_transport_capacity_known(catalog,p.publication_order,bound.market_context_id) ORDER BY route_stream_id,revision DESC)p;
 SELECT coalesce(jsonb_agg(to_jsonb(p)||jsonb_build_object('allocations',security.read_delivery_route_allocations(catalog,p.id,false)->'allocations') ORDER BY p.publication_order DESC),'[]') INTO history
 FROM(SELECT p.* FROM trading.delivery_route_allocation_plans p WHERE(p.catalog_id,p.route_id)=(catalog,route)
  AND trading.gas_transport_capacity_known(catalog,p.publication_order,bound.market_context_id) ORDER BY publication_order DESC LIMIT 1001)p;
 IF jsonb_array_length(history)>1000 THEN RAISE EXCEPTION 'delivery_workbench_limit';END IF;
 PERFORM security.require_gas_transport_route_commit(catalog,NULL,deadline);
 RETURN jsonb_build_object('source',source,'plans',plans,'history',history);
END$$;

CREATE FUNCTION security.delivery_route_allocation_choices(catalog uuid,route uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;facts jsonb;scope trading.gas_transport_routes;node record;candidate record;t trading.facility_gas_target;selected record;
 choices jsonb='[]';compatible boolean;BEGIN
 deadline=security.gas_transport_route_authority(catalog,NULL);facts=security.read_gas_transport_route(catalog,route);
 SELECT * INTO STRICT scope FROM trading.gas_transport_routes WHERE(catalog_id,id)=(catalog,route);
 FOR node IN SELECT * FROM trading.gas_transport_route_nodes n WHERE(n.catalog_id,n.route_id)=(catalog,route) AND n.role IN('external_receipt_v1','external_delivery_v1') ORDER BY n.node_key LOOP
  FOR candidate IN SELECT DISTINCT o.id obligation_id,(date '0001-01-01'+g.operating_days[1]) AS operating_day
   FROM trading.gas_delivery_obligations o JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(o.catalog_id,o.instrument_id,o.slot)
   JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot)=(m.catalog_id,m.instrument_id,m.slot)
   JOIN trading.side_schedule_roots root ON(root.catalog_id,root.schedule_id,root.measure)=(s.catalog_id,s.schedule_id,'schedule_quantity')
   JOIN trading.side_schedule_rows q ON(q.catalog_id,q.schedule_id,q.ordinal)=(root.catalog_id,root.schedule_id,root.row_ordinal)
   JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(q.catalog_id,q.schedule_id,q.grid_ordinal)
   WHERE o.catalog_id=catalog AND m.role='physical' AND(m.pipeline_code,m.zone_code,m.point_code,m.product_code)=(node.pipeline_code,node.zone_code,node.point_code,scope.product_code)
    AND m.direction=CASE node.role WHEN 'external_receipt_v1' THEN 1 ELSE -1 END AND q.operation='quantity_literal' AND cardinality(g.operating_days)=1
   ORDER BY o.id,operating_day LOOP
   BEGIN SELECT * INTO STRICT t FROM security.facility_gas_target(catalog,candidate.obligation_id,candidate.operating_day,(facts->>'asOf')::timestamptz);
   EXCEPTION WHEN no_data_found OR too_many_rows THEN CONTINUE;END;
   compatible=false;
   FOR selected IN SELECT p FROM jsonb_array_elements(facts->'plans')p WHERE p->>'kind'='plan' LOOP
    BEGIN PERFORM trading.require_delivery_allocation_target(catalog,route,node.id,(selected.p->>'id')::uuid,candidate.obligation_id,candidate.operating_day,t.trade_revision,NULL,(facts->>'asOf')::timestamptz);compatible=true;
    EXCEPTION WHEN raise_exception THEN
     IF SQLERRM NOT IN('delivery_workbench_target','delivery_workbench_geometry','delivery_workbench_conservation') THEN RAISE;END IF;
    END;
   END LOOP;
   IF compatible THEN
    choices=choices||jsonb_build_array(jsonb_build_object('nodeId',node.id,'node',node.node_key,'obligationId',candidate.obligation_id,'tradeId',t.trade_id,'tradeRevision',t.trade_revision,
     'day',candidate.operating_day,'point',node.point_code,'direction',t.direction,'nativeFrom',t.native_from,'nativeTo',t.native_to,
     'contractual',CASE WHEN mod(t.quantity_n,t.quantity_d)=0 THEN trim_scale(t.quantity_n/t.quantity_d)::text ELSE trim_scale(t.quantity_n)::text||'/'||trim_scale(t.quantity_d)::text END,
     'tradeReference',(SELECT 'TR-'||lpad(reference_number::text,6,'0') FROM trading.trade_references WHERE(catalog_id,trade_id)=(catalog,t.trade_id))));
    IF jsonb_array_length(choices)>1000 THEN RAISE EXCEPTION 'delivery_workbench_limit';END IF;
   END IF;
  END LOOP;
 END LOOP;
 PERFORM security.require_gas_transport_route_commit(catalog,NULL,deadline);RETURN choices;
END$$;

CREATE FUNCTION security.record_delivery_route_allocation(catalog uuid,operation uuid,body jsonb,requested_at timestamptz,context uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;bound security.request_contexts;actor uuid;canonical jsonb;canonical_fact jsonb;prior trading.delivery_route_allocation_commands;
 source trading.gas_transport_route_plans;previous trading.delivery_route_allocation_plans;inserted_plan trading.delivery_route_allocation_plans;
 facts jsonb;item jsonb;native_target trading.facility_gas_target;publication integer;result jsonb;BEGIN
 deadline=security.gas_transport_route_authority(catalog,'plan');
 IF NOT security.current_write_context() OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' OR requested_at IS NULL OR NOT isfinite(requested_at)
 THEN RAISE EXCEPTION 'delivery_workbench_input';END IF;
 canonical=trading.delivery_route_allocation_body(body);canonical_fact=trading.delivery_route_allocation_fact(canonical,requested_at);
 PERFORM 1 FROM security.epoch WHERE id=true FOR SHARE;PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_transport_route_commit(catalog,'plan',deadline);
 SELECT q.actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 SELECT * INTO prior FROM trading.delivery_route_allocation_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>actor OR prior.request IS DISTINCT FROM jsonb_build_object('command',canonical,'effectiveAt',trading.gas_transport_capacity_instant(requested_at)) THEN RAISE EXCEPTION 'delivery_workbench_conflict';END IF;
  PERFORM security.read_delivery_route_allocations(catalog,prior.plan_id,false);PERFORM security.require_gas_transport_route_commit(catalog,'plan',deadline);RETURN prior.outcome;
 END IF;
 SELECT * INTO prior FROM trading.delivery_route_allocation_commands c WHERE c.catalog_id=catalog AND c.route_id=(canonical->>'scopeId')::uuid AND c.fact=canonical_fact ORDER BY c.operation_id LIMIT 1;
 IF FOUND THEN
  PERFORM security.read_delivery_route_allocations(catalog,prior.plan_id,false);result=prior.outcome||jsonb_build_object('changed',false);
  INSERT INTO trading.delivery_route_allocation_commands VALUES(catalog,operation,actor,prior.route_id,prior.plan_id,jsonb_build_object('command',canonical,'effectiveAt',trading.gas_transport_capacity_instant(requested_at)),canonical_fact,result,deadline);
  PERFORM security.require_gas_transport_route_commit(catalog,'plan',deadline);RETURN result;
 END IF;
 PERFORM security.use_market_context(catalog,context);
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context) AND t.effective_on=(requested_at AT TIME ZONE 'UTC')::date)
 THEN RAISE EXCEPTION 'delivery_workbench_context';END IF;
 PERFORM security.use_trading_context(catalog,((requested_at AT TIME ZONE 'UTC')::date+INTERVAL '1 day'-INTERVAL '1 microsecond') AT TIME ZONE 'UTC');
 bound=trading.gas_transport_capacity_bound(catalog);facts=security.read_gas_transport_route(catalog,(canonical->>'scopeId')::uuid);
 SELECT * INTO source FROM trading.gas_transport_route_plans p WHERE(p.catalog_id,p.route_id,p.id)=(catalog,(canonical->>'scopeId')::uuid,(canonical->>'routePlanId')::uuid)
  AND trading.gas_transport_capacity_known(catalog,p.publication_order,context);
 IF source.id IS NULL OR source.effective_at>requested_at OR(facts->>'stateVersion')::integer<>(canonical->>'expectedRouteStateVersion')::integer
  OR trading.gas_transport_route_head(catalog,source.route_id)<>(canonical->>'expectedRouteStateVersion')::integer THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
 IF canonical->>'kind'='allocate' THEN PERFORM trading.require_delivery_route_current_heads(catalog,source.route_id,source.id);END IF;
 SELECT * INTO previous FROM trading.delivery_route_allocation_plans h WHERE(h.catalog_id,h.route_id,h.route_stream_id)=(catalog,source.route_id,source.stream_id) ORDER BY revision DESC LIMIT 1;
 IF previous.id IS DISTINCT FROM(canonical->>'expectedPreviousId')::uuid OR(previous.id IS NOT NULL AND previous.effective_at>requested_at) THEN RAISE EXCEPTION 'delivery_workbench_conflict';END IF;
 IF previous.id IS NOT NULL THEN PERFORM security.read_delivery_route_allocations(catalog,previous.id,false);END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 INSERT INTO trading.delivery_route_allocation_plans VALUES(catalog,operation,source.route_id,source.stream_id,source.id,coalesce(previous.revision,0)+1,previous.id,canonical->>'kind',canonical->>'policy',canonical->>'evidence',context,actor,publication,requested_at) RETURNING * INTO inserted_plan;
 FOR item IN SELECT value FROM jsonb_array_elements(canonical->'allocations') LOOP
  native_target=trading.require_delivery_allocation_target(catalog,source.route_id,(item->>'nodeId')::uuid,source.id,(item->>'obligationId')::uuid,(item->>'day')::date,(item->>'tradeRevision')::integer,(item->>'quantity')::numeric,bound.trading_as_of);
  IF native_target.trade_revision IS DISTINCT FROM(SELECT max(r.revision) FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,native_target.trade_id))
  THEN RAISE EXCEPTION 'delivery_workbench_stale';END IF;
  INSERT INTO trading.delivery_route_allocations VALUES(catalog,inserted_plan.id,source.route_id,(item->>'nodeId')::uuid,(item->>'obligationId')::uuid,native_target.trade_id,native_target.trade_revision,native_target.instrument_id,(item->>'day')::date,(item->>'quantity')::numeric,native_target.direction,native_target.reference_catalog_id,native_target.reference_revision_id,native_target.quantity_code);
 END LOOP;
 result=jsonb_build_object('allocationPlanId',inserted_plan.id,'scopeId',inserted_plan.route_id,'revision',inserted_plan.revision,'changed',true);
 INSERT INTO trading.delivery_route_allocation_commands VALUES(catalog,operation,actor,inserted_plan.route_id,inserted_plan.id,jsonb_build_object('command',canonical,'effectiveAt',trading.gas_transport_capacity_instant(requested_at)),canonical_fact,result,deadline);
 PERFORM trading.verify_delivery_route_allocation(catalog,inserted_plan.id);PERFORM security.require_gas_transport_route_commit(catalog,'plan',deadline);RETURN result;
END$$;
CREATE FUNCTION security.delivery_route_allocation_status(catalog uuid,operation uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;prior trading.delivery_route_allocation_commands;actor uuid;BEGIN
 deadline=security.gas_transport_route_authority(catalog,NULL);
 SELECT q.actor_id INTO actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 SELECT * INTO prior FROM trading.delivery_route_allocation_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF NOT FOUND THEN RETURN jsonb_build_object('found',false,'authorityUntil',deadline);END IF;
 IF prior.actor_id IS DISTINCT FROM actor THEN RAISE EXCEPTION 'delivery_workbench_denied' USING ERRCODE='42501';END IF;
 deadline=least(deadline,security.gas_transport_route_authority(catalog,'plan'));
 PERFORM security.read_delivery_route_allocations(catalog,prior.plan_id,false);PERFORM security.require_gas_transport_route_commit(catalog,'plan',deadline);
 RETURN jsonb_build_object('found',true,'scopeId',prior.route_id,'kind',prior.request#>>'{command,kind}','command',prior.request->'command','effectiveAt',prior.request->>'effectiveAt','outcome',prior.outcome,'authorityUntil',deadline);
END$$;
CREATE FUNCTION security.delivery_workbench_crude_owner(catalog uuid) RETURNS text
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF security.trading_access(catalog,'ReadTrades') IS NOT TRUE OR security.market_access(catalog,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'delivery_workbench_denied' USING ERRCODE='42501';END IF;
 RETURN presentation.preference_owner(security.presentation_actor(),'delivery_workbench_crude:'||catalog::text);
END$$;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,n.nspname,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname IN('security','trading') AND(p.proname LIKE 'delivery_route_allocation%' OR p.proname IN('read_delivery_route_allocations','record_delivery_route_allocation','verify_delivery_route_allocation','require_delivery_route_source','require_delivery_route_current_heads','require_delivery_allocation_target','delivery_workbench_crude_owner')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.nspname='security' THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
COMMIT;
