-- Atomic reviewed groups reuse native gas nomination facts and quantity tracks.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;

CREATE TABLE trading.delivery_nomination_batches(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 allocation_plan_id uuid NOT NULL,request jsonb NOT NULL,outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),CHECK(pg_column_size(request)<=1048576),
 FOREIGN KEY(catalog_id,allocation_plan_id) REFERENCES trading.delivery_route_allocation_plans(catalog_id,id)
);
CREATE TABLE trading.delivery_nomination_batch_members(
 catalog_id uuid NOT NULL,batch_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),
 nomination_id uuid NOT NULL,obligation_id uuid NOT NULL,day date NOT NULL,
 PRIMARY KEY(catalog_id,batch_id,ordinal),UNIQUE(catalog_id,batch_id,obligation_id,day),
 FOREIGN KEY(catalog_id,batch_id) REFERENCES trading.delivery_nomination_batches(catalog_id,operation_id),
 FOREIGN KEY(catalog_id,nomination_id) REFERENCES trading.gas_nomination_requests(catalog_id,id),
 FOREIGN KEY(catalog_id,obligation_id) REFERENCES trading.gas_delivery_obligations(catalog_id,id)
);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['delivery_nomination_batches','delivery_nomination_batch_members'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE POLICY private_storage ON trading.%I FOR SELECT TO openetrm_runtime USING(false)',name);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;

CREATE FUNCTION security.delivery_nomination_batch_authority(catalog uuid,writing boolean) RETURNS timestamptz
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;BEGIN
 IF writing IS NULL THEN RAISE EXCEPTION 'delivery_nomination_batch_input';END IF;
 -- Authority is rechecked after the writer has internally bound its source
 -- read. The writer separately admits only an unbound current entry scope.
 PERFORM security.require_gas_operations(catalog,'read');
 IF writing AND(security.trading_access(catalog,'AmendTrade') IS NOT TRUE OR security.trading_access(catalog,'RecordGasOperations') IS NOT TRUE)
 THEN RAISE EXCEPTION 'delivery_nomination_batch_denied' USING ERRCODE='42501';END IF;
 deadline=security.gas_transport_route_authority(catalog,CASE WHEN writing THEN 'plan' ELSE NULL END);
 IF writing THEN deadline=least(deadline,security.authority_until('AmendTrade'),security.authority_until('RecordGasOperations'));END IF;
 IF deadline IS NULL OR deadline<=clock_timestamp() THEN RAISE EXCEPTION 'delivery_nomination_batch_denied' USING ERRCODE='42501';END IF;
 RETURN deadline;
END$$;
CREATE FUNCTION security.require_delivery_nomination_batch(catalog uuid,plan uuid,writing boolean,deadline timestamptz) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF deadline IS NULL OR deadline<=clock_timestamp() THEN RAISE EXCEPTION 'delivery_nomination_batch_denied' USING ERRCODE='42501';END IF;
 PERFORM security.delivery_nomination_batch_authority(catalog,writing);
 IF plan IS NOT NULL AND plan<>'00000000-0000-0000-0000-000000000000'::uuid THEN
  PERFORM security.read_delivery_route_allocations(catalog,plan,false);
 END IF;
 IF deadline<=clock_timestamp() THEN RAISE EXCEPTION 'delivery_nomination_batch_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.delivery_nomination_batch_status(catalog uuid,operation uuid,payload jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE prior trading.delivery_nomination_batches;actor uuid;deadline timestamptz;member record;BEGIN
 deadline=security.delivery_nomination_batch_authority(catalog,false);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO prior FROM trading.delivery_nomination_batches b WHERE(b.catalog_id,b.operation_id)=(catalog,operation);
 IF NOT FOUND THEN RETURN jsonb_build_object('found',false,'operationId',operation);END IF;
 IF prior.actor_id<>actor OR payload IS NOT NULL AND payload<>prior.request THEN RAISE EXCEPTION 'delivery_nomination_batch_conflict';END IF;
 PERFORM security.require_delivery_nomination_batch(catalog,prior.allocation_plan_id,false,deadline);
 FOR member IN SELECT m.nomination_id FROM trading.delivery_nomination_batch_members m WHERE(m.catalog_id,m.batch_id)=(catalog,operation) LOOP
  PERFORM security.require_gas_service(catalog,(SELECT account_id FROM trading.gas_nomination_requests n WHERE(n.catalog_id,n.id)=(catalog,member.nomination_id)));
 END LOOP;
 PERFORM security.require_delivery_nomination_batch(catalog,prior.allocation_plan_id,false,deadline);
 RETURN jsonb_build_object('found',true,'operationId',operation,'allocationPlanId',prior.allocation_plan_id,'request',prior.request,'outcome',prior.outcome);
END$$;

CREATE FUNCTION security.record_delivery_nomination_batch(catalog uuid,operation uuid,payload jsonb,nominations uuid[],source_context uuid) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;deadline timestamptz;plan uuid;source jsonb;entry jsonb;details jsonb;input jsonb=payload->'input';
 target record;n trading.gas_nomination_requests;a trading.gas_service_accounts;bound security.request_contexts;ordinal integer=0;expected integer;prior jsonb;outcome jsonb;rows jsonb='[]';BEGIN
 deadline=security.delivery_nomination_batch_authority(catalog,true);
 IF security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR jsonb_typeof(payload) IS DISTINCT FROM 'object'
  OR(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(payload) k) IS DISTINCT FROM ARRAY['input','previewHash']::text[]
  OR jsonb_typeof(input) IS DISTINCT FROM 'object'
  OR(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(input) k) IS DISTINCT FROM ARRAY['allocationPlanId','context','entries','profile']::text[]
  OR jsonb_typeof(payload->'previewHash') IS DISTINCT FROM 'string' OR payload->>'previewHash' !~ '^[0-9a-f]{64}$'
  OR input->>'profile' IS DISTINCT FROM 'complete_allocation_nomination_batch_v1'
  OR input#>>'{context,knowledgeMode}' IS DISTINCT FROM 'current' OR input#>>'{context,convention}' IS DISTINCT FROM 'physical_gas_fixed_quantity_v4'
  OR input#>'{context,contextId}' IS NOT NULL AND input#>'{context,contextId}'<>'null'::jsonb
  OR jsonb_typeof(input->'entries') IS DISTINCT FROM 'array' OR jsonb_array_length(input->'entries') NOT BETWEEN 1 AND 128
  OR nominations IS NULL OR cardinality(nominations)<>jsonb_array_length(input->'entries') OR array_position(nominations,NULL) IS NOT NULL
 THEN RAISE EXCEPTION 'delivery_nomination_batch_input';END IF;
 plan=(input->>'allocationPlanId')::uuid;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_delivery_nomination_batch(catalog,plan,true,deadline);
 prior=security.delivery_nomination_batch_status(catalog,operation,payload);
 IF(prior->>'found')::boolean THEN RETURN prior;END IF;
 source=security.read_delivery_route_allocations(catalog,plan,false);
 PERFORM security.use_market_context(catalog,source_context);
 PERFORM security.use_trading_context(catalog,(((source#>>'{plan,effective_at}')::timestamptz AT TIME ZONE 'UTC')::date+INTERVAL '1 day'-INTERVAL '1 microsecond') AT TIME ZONE 'UTC');
 source=security.read_delivery_route_allocations(catalog,plan,true);
 IF plan IS DISTINCT FROM(SELECT h.id FROM trading.delivery_route_allocation_plans h
  WHERE(h.catalog_id,h.route_id,h.route_stream_id)=(catalog,(source#>>'{plan,route_id}')::uuid,(source#>>'{plan,route_stream_id}')::uuid)
  ORDER BY h.revision DESC LIMIT 1)
 THEN RAISE EXCEPTION 'delivery_nomination_batch_stale';END IF;
 bound=trading.gas_transport_capacity_bound(catalog);
 IF bound.trading_as_of IS DISTINCT FROM(((source#>>'{plan,effective_at}')::timestamptz AT TIME ZONE 'UTC')::date+INTERVAL '1 day'-INTERVAL '1 microsecond') AT TIME ZONE 'UTC'
 THEN RAISE EXCEPTION 'delivery_nomination_batch_context';END IF;
 PERFORM trading.require_delivery_route_current_heads(catalog,(source#>>'{plan,route_id}')::uuid,(source#>>'{plan,route_plan_id}')::uuid);
 SELECT count(*) INTO expected FROM(SELECT x.obligation_id,x.day FROM jsonb_to_recordset(source->'allocations') x(obligation_id uuid,day date) GROUP BY x.obligation_id,x.day) targets;
 IF expected<>cardinality(nominations) OR EXISTS(SELECT 1 FROM jsonb_array_elements(input->'entries') e GROUP BY e->>'obligationId',e->>'day' HAVING count(*)<>1)
 THEN RAISE EXCEPTION 'delivery_nomination_batch_scope';END IF;
 FOR entry IN SELECT value FROM jsonb_array_elements(input->'entries') LOOP
  IF(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(entry) k) IS DISTINCT FROM ARRAY['day','details','expectedRevision','obligationId','tradeId']::text[] THEN RAISE EXCEPTION 'delivery_nomination_batch_input';END IF;
  details=entry->'details';
  PERFORM security.validate_gas_operations(jsonb_build_object('preview',jsonb_build_object('context',input->'context','tradeId',entry->'tradeId','expectedRevision',entry->'expectedRevision','day',entry->'day','kind','nomination','details',details),'operationId',operation,'previewHash',payload->'previewHash'));
  SELECT x.obligation_id,x.trade_id,x.trade_revision,x.day,sum(x.quantity) quantity INTO target
   FROM jsonb_to_recordset(source->'allocations') x(obligation_id uuid,trade_id uuid,trade_revision integer,day date,quantity numeric)
   WHERE x.obligation_id=(entry->>'obligationId')::uuid AND x.day=(entry->>'day')::date GROUP BY x.obligation_id,x.trade_id,x.trade_revision,x.day;
  IF NOT FOUND OR(target.trade_id,target.trade_revision,target.quantity) IS DISTINCT FROM((entry->>'tradeId')::uuid,(entry->>'expectedRevision')::integer,(details->>'quantity')::numeric)
  THEN RAISE EXCEPTION 'delivery_nomination_batch_scope';END IF;
  IF target.trade_revision IS DISTINCT FROM(SELECT max(r.revision) FROM trading.gas_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,target.trade_id))
  THEN RAISE EXCEPTION 'delivery_nomination_batch_stale';END IF;
  SELECT * INTO n FROM trading.gas_nomination_requests WHERE(catalog_id,id)=(catalog,nominations[ordinal+1]);
  SELECT * INTO a FROM trading.gas_service_accounts WHERE(catalog_id,id)=(catalog,n.account_id);
  IF n.id IS NULL OR(a.origin_obligation_id,a.trade_id,n.account_id,n.service_version_id,n.day,n.external_reference,n.quantity,n.submitted_at,n.channel,n.reason,n.evidence)
   IS DISTINCT FROM(target.obligation_id,target.trade_id,(details->>'accountId')::uuid,(details->>'serviceVersionId')::uuid,target.day,details->>'externalReference',target.quantity,(details->>'submittedAt')::timestamptz,details->>'channel',details->>'reason',details->>'evidence')
  THEN RAISE EXCEPTION 'delivery_nomination_batch_scope';END IF;
  -- A group cannot relabel existing evidence with a fabricated financial
  -- context or changed native request. Require this actor's exact admitted
  -- child command, including its selected frame and explicit predecessor.
  IF NOT EXISTS(SELECT 1 FROM trading.gas_operation_commands child
   WHERE(child.catalog_id,child.actor_id,child.trade_id,child.nomination_id,child.kind)=(catalog,actor,target.trade_id,n.id,'nomination')
    AND child.request->'preview'=jsonb_build_object('context',input->'context','tradeId',entry->'tradeId',
     'expectedRevision',entry->'expectedRevision','day',entry->'day','kind','nomination','details',details))
  THEN RAISE EXCEPTION 'delivery_nomination_batch_scope';END IF;
  PERFORM security.require_gas_service(catalog,n.account_id);
  rows=rows||jsonb_build_array(jsonb_build_object('obligationId',target.obligation_id,'tradeId',target.trade_id,'day',target.day,'quantity',trim_scale(target.quantity)::text,'nominationId',n.id));
  ordinal=ordinal+1;
 END LOOP;
 outcome=jsonb_build_object('allocationPlanId',plan,'entries',rows);
 INSERT INTO trading.delivery_nomination_batches VALUES(catalog,operation,actor,plan,payload,outcome);
 ordinal=0;
 FOR entry IN SELECT value FROM jsonb_array_elements(input->'entries') LOOP
  INSERT INTO trading.delivery_nomination_batch_members VALUES(catalog,operation,ordinal,nominations[ordinal+1],(entry->>'obligationId')::uuid,(entry->>'day')::date);
  ordinal=ordinal+1;
 END LOOP;
 PERFORM security.require_delivery_nomination_batch(catalog,plan,true,deadline);
 RETURN jsonb_build_object('found',true,'operationId',operation,'allocationPlanId',plan,'request',payload,'outcome',outcome);
END$$;
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security'
  AND p.proname IN('delivery_nomination_batch_authority','require_delivery_nomination_batch','delivery_nomination_batch_status','record_delivery_nomination_batch') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',item.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',item.signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',item.signature);
 END LOOP;
END$$;
COMMIT;
