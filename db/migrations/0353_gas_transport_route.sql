-- Retained route assertions over existing capacity; no flow, title or cash.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- Page identity admits prompts, without enabling assistant operations.
DO $pages$ DECLARE definition text;marker text:='''transport_capacity''';BEGIN
 SELECT pg_get_functiondef(p.oid) INTO STRICT definition FROM pg_proc p WHERE p.oid='assistant.is_prompt_page(text)'::regprocedure
  AND md5(p.prosrc)='a66025172980e767ef3cd11e63ffbc27' AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
  AND NOT p.prosecdef AND p.provolatile='i' AND p.prokind='f' AND p.proparallel='u' AND NOT p.proisstrict AND NOT p.proleakproof
  AND p.provariadic=0 AND p.proargmodes IS NULL AND p.proargnames=ARRAY['page'] AND p.prorettype='boolean'::regtype
  AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='sql') AND p.proconfig=ARRAY['search_path=pg_catalog']
  AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[];
 IF(length(definition)-length(replace(definition,marker,'')))/length(marker)<>1 OR definition LIKE '%''transport_route_plans''%'
 THEN RAISE EXCEPTION 'gas_transport_route_upgrade: unexpected helper assistant.is_prompt_page(text)';END IF;
 EXECUTE replace(definition,marker,marker||',''transport_route_plans''');
EXCEPTION WHEN no_data_found THEN RAISE EXCEPTION 'gas_transport_route_upgrade: unexpected helper assistant.is_prompt_page(text)';
END $pages$;

-- The route consumer reuses the proven exact capacity algorithm. Refuse drift
-- rather than extracting a different authority or numerical implementation.
DO $guard$DECLARE helper pg_proc;BEGIN
 SELECT * INTO STRICT helper FROM pg_proc WHERE oid='trading.verify_gas_transport_capacity_plan(uuid,uuid)'::regprocedure;
 IF encode(sha256(convert_to(helper.prosrc,'UTF8')),'hex')<>'b189868cc2c6639c64ba920fbe10b527135b8fd014a53a5d0be20cade0f04f7a'
  OR helper.proowner<>'openetrm_security_owner'::regrole OR helper.prosecdef OR helper.provolatile<>'v' OR helper.prokind<>'f'
  OR helper.proisstrict OR helper.proleakproof OR helper.proparallel<>'u' OR helper.provariadic<>0 OR helper.proargmodes IS NOT NULL
  OR helper.proargnames IS DISTINCT FROM ARRAY['catalog','selected_plan'] OR helper.prorettype<>'void'::regtype
  OR helper.prolang<>(SELECT oid FROM pg_language WHERE lanname='plpgsql') OR helper.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']
  OR helper.proacl IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[]
 THEN RAISE EXCEPTION 'gas_transport_route_upgrade: unexpected helper trading.verify_gas_transport_capacity_plan(uuid,uuid)';END IF;
END$guard$;

CREATE FUNCTION trading.verify_gas_transport_capacity_snapshot(catalog uuid,entitlement uuid,selected_version uuid,head_cut integer,selected_policy text,witness jsonb) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE scope trading.gas_transport_capacity_entitlements;item record;part record;active record;proof jsonb;
 rate numeric[];total numeric[];maximum numeric[];seen integer=0;wanted integer;ids jsonb;BEGIN
 IF selected_policy IS NULL OR selected_policy NOT IN('complete_feasible_replacement_v1','explicit_empty_release_v1') OR head_cut IS NULL OR head_cut<0
  OR NOT EXISTS(SELECT 1 FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id,v.id)=(catalog,entitlement,selected_version) AND v.publication_order<=head_cut)
 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 SELECT * INTO STRICT scope FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id)=(catalog,entitlement);
 PERFORM trading.gas_transport_capacity_keys(witness,ARRAY['implementation','versionId','admissionPolicy','intervals']);
 IF witness->>'implementation' IS DISTINCT FROM 'gas_transport_capacity_witness_v1' OR(witness->>'versionId')::uuid IS DISTINCT FROM selected_version
  OR witness->>'admissionPolicy' IS DISTINCT FROM selected_policy
  OR jsonb_typeof(witness->'intervals') IS DISTINCT FROM 'array' OR pg_column_size(witness)>8388608
 THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
 IF(SELECT count(*) FROM trading.gas_transport_capacity_claims c JOIN(SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p
  WHERE(p.catalog_id,p.entitlement_id)=(catalog,entitlement) AND p.publication_order<=head_cut ORDER BY business_key,revision DESC)heads ON heads.id=c.plan_id AND heads.catalog_id=c.catalog_id)>1024
 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 FOR item IN SELECT DISTINCT axis,point_code,path_id FROM trading.gas_transport_capacity_limits l WHERE(l.catalog_id,l.version_id)=(catalog,selected_version) LOOP
  FOR part IN WITH heads AS(SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p
    WHERE(p.catalog_id,p.entitlement_id)=(catalog,entitlement) AND p.publication_order<=head_cut ORDER BY business_key,revision DESC),
   stages AS(SELECT c.id,CASE WHEN item.axis='delivery' THEN c.delivery_from ELSE c.receipt_from END f,
    CASE WHEN item.axis='delivery' THEN c.delivery_to ELSE c.receipt_to END t FROM trading.gas_transport_capacity_claims c JOIN heads ON(heads.catalog_id,heads.id)=(c.catalog_id,c.plan_id)
    JOIN trading.gas_transport_capacity_paths p ON(p.catalog_id,p.entitlement_id,p.id)=(c.catalog_id,c.entitlement_id,c.path_id)
    WHERE item.axis='agreement' OR item.axis='receipt' AND p.receipt_point_code=item.point_code OR item.axis='delivery' AND p.delivery_point_code=item.point_code OR item.axis='route' AND c.path_id=item.path_id),
   bounds AS(SELECT from_at at FROM trading.gas_transport_capacity_limits l WHERE(l.catalog_id,l.version_id,l.axis,l.point_code,l.path_id) IS NOT DISTINCT FROM(catalog,selected_version,item.axis,item.point_code,item.path_id)
    UNION SELECT through_at FROM trading.gas_transport_capacity_limits l WHERE(l.catalog_id,l.version_id,l.axis,l.point_code,l.path_id) IS NOT DISTINCT FROM(catalog,selected_version,item.axis,item.point_code,item.path_id)
    UNION SELECT f FROM stages UNION SELECT t FROM stages)
   SELECT at f,lead(at) OVER(ORDER BY at)t FROM bounds ORDER BY at LOOP
   IF part.t IS NULL THEN CONTINUE;END IF;seen=seen+1;total=ARRAY[0::numeric,1::numeric];ids='[]';
   FOR active IN SELECT c.id,CASE WHEN item.axis='delivery' THEN c.delivery_from ELSE c.receipt_from END f,
     CASE WHEN item.axis='delivery' THEN c.delivery_to ELSE c.receipt_to END t,CASE WHEN item.axis='delivery' THEN c.delivery_quantity ELSE c.receipt_quantity END q
    FROM trading.gas_transport_capacity_claims c JOIN(SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p
     WHERE(p.catalog_id,p.entitlement_id)=(catalog,entitlement) AND p.publication_order<=head_cut ORDER BY business_key,revision DESC)heads
     ON(heads.catalog_id,heads.id)=(c.catalog_id,c.plan_id) JOIN trading.gas_transport_capacity_paths p ON(p.catalog_id,p.entitlement_id,p.id)=(c.catalog_id,c.entitlement_id,c.path_id)
    WHERE(item.axis='agreement' OR item.axis='receipt' AND p.receipt_point_code=item.point_code OR item.axis='delivery' AND p.delivery_point_code=item.point_code OR item.axis='route' AND c.path_id=item.path_id)
     AND CASE WHEN item.axis='delivery' THEN c.delivery_from ELSE c.receipt_from END<=part.f AND CASE WHEN item.axis='delivery' THEN c.delivery_to ELSE c.receipt_to END>=part.t ORDER BY c.id LOOP
    rate=trading.gas_transport_capacity_rate(active.q,active.f,active.t);
    total=trading.gas_transport_capacity_add(total,rate);ids=ids||to_jsonb(active.id);
   END LOOP;
   total=trading.gas_transport_capacity_ratio(total[1],total[2]);
   SELECT trading.gas_transport_capacity_rate(l.maximum_per_hour,part.f,part.f+INTERVAL '1 hour') INTO maximum FROM trading.gas_transport_capacity_limits l
    WHERE(l.catalog_id,l.version_id,l.axis,l.point_code,l.path_id) IS NOT DISTINCT FROM(catalog,selected_version,item.axis,item.point_code,item.path_id)
     AND l.from_at<=part.f AND l.through_at>=part.t;
   IF maximum IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_limits';END IF;
   SELECT min(ordinality)::integer INTO wanted FROM jsonb_array_elements(witness->'intervals') WITH ORDINALITY x(value,ordinality)
    WHERE value->>'axis'=item.axis AND value->>'point' IS NOT DISTINCT FROM item.point_code AND(value->>'pathId')::uuid IS NOT DISTINCT FROM item.path_id
     AND trading.gas_transport_capacity_at(value->>'from')=part.f AND trading.gas_transport_capacity_at(value->>'through')=part.t;
   -- Count separately: duplicated interval identities are never accepted.
   IF(SELECT count(*) FROM jsonb_array_elements(witness->'intervals')value WHERE value->>'axis'=item.axis
    AND value->>'point' IS NOT DISTINCT FROM item.point_code AND(value->>'pathId')::uuid IS NOT DISTINCT FROM item.path_id
    AND trading.gas_transport_capacity_at(value->>'from')=part.f AND trading.gas_transport_capacity_at(value->>'through')=part.t)<>1
   THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
   proof=(witness->'intervals')->(wanted-1);
   PERFORM trading.gas_transport_capacity_keys(proof,ARRAY['axis','point','pathId','from','through','claims','used','maximum','withinLimit']);
   IF trading.gas_transport_capacity_ratio_read(proof->'used')<>total OR trading.gas_transport_capacity_ratio_read(proof->'maximum')<>maximum
    OR proof->'claims' IS DISTINCT FROM ids OR proof->'withinLimit' IS DISTINCT FROM to_jsonb(total[1]*maximum[2]<=maximum[1]*total[2])
   THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
   IF total[1]*maximum[2]>maximum[1]*total[2] AND selected_policy='complete_feasible_replacement_v1' THEN RAISE EXCEPTION 'gas_transport_capacity_exceeded';END IF;
  END LOOP;
 END LOOP;
 IF jsonb_array_length(witness->'intervals')<>seen THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
END$$;

CREATE OR REPLACE FUNCTION trading.verify_gas_transport_capacity_plan(catalog uuid,selected_plan uuid) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE plan trading.gas_transport_capacity_plans;scope trading.gas_transport_capacity_entitlements;BEGIN
 SELECT * INTO STRICT plan FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.id)=(catalog,selected_plan);
 SELECT * INTO STRICT scope FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id)=(catalog,plan.entitlement_id);
 IF EXISTS(SELECT 1 FROM trading.gas_transport_capacity_claims c WHERE(c.catalog_id,c.plan_id)=(catalog,selected_plan)
  AND(c.receipt_from<scope.from_at OR c.delivery_from<scope.from_at OR c.receipt_to>scope.through_at OR c.delivery_to>scope.through_at))
  OR plan.admission_policy='explicit_empty_release_v1' AND(plan.previous_id IS NULL OR EXISTS(SELECT 1 FROM trading.gas_transport_capacity_claims c WHERE(c.catalog_id,c.plan_id)=(catalog,selected_plan)))
 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 PERFORM trading.verify_gas_transport_capacity_snapshot(catalog,plan.entitlement_id,plan.version_id,plan.publication_order,plan.admission_policy,plan.assessment);
END$$;
ALTER FUNCTION trading.verify_gas_transport_capacity_snapshot(uuid,uuid,uuid,integer,text,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.verify_gas_transport_capacity_snapshot(uuid,uuid,uuid,integer,text,jsonb) FROM PUBLIC;

CREATE FUNCTION trading.gas_transport_route_fraction(n numeric,d numeric) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT n IS NOT NULL AND d IS NOT NULL AND n=trunc(n) AND d=trunc(d) AND n>=0 AND d>0 AND n<=d
  AND n<power(2::numeric,4096) AND d<power(2::numeric,4096) AND gcd(n,d)=1
$$;
CREATE TABLE trading.gas_transport_routes(
 catalog_id uuid NOT NULL,id uuid NOT NULL,definition_id uuid NOT NULL,business_key text NOT NULL,
 book_id uuid NOT NULL,legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,product_code ref_data.code NOT NULL,quantity_code ref_data.code NOT NULL,
 implementation text NOT NULL CHECK(implementation='gas_transport_chosen_uniform_route_v1'),
 fraction_basis text NOT NULL CHECK(fraction_basis='fuel_and_loss_each_fraction_of_gross_receipt_v1'),
 flow_shape text NOT NULL CHECK(flow_shape='one_explicit_uniform_receipt_and_delivery_per_segment_v1'),
 transit_law text NOT NULL CHECK(transit_law='fixed_nonnegative_elapsed_microseconds_v1'),
 node_balance_law text NOT NULL CHECK(node_balance_law='simultaneous_inflow_equals_outflow_no_storage_v1'),
 junction_policy text NOT NULL CHECK(junction_policy='same_exact_pipeline_point_handoff_v1'),
 membership_policy text NOT NULL CHECK(membership_policy='whole_capacity_plan_stream_exclusive_v1'),
 evidence text NOT NULL CHECK(evidence=trading.gas_transport_capacity_evidence(evidence)),source_context_id uuid NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,definition_id),UNIQUE(catalog_id,book_id,business_key),UNIQUE(catalog_id,id,definition_id),
 UNIQUE(catalog_id,id,reference_catalog_id,reference_revision_id,product_code),CHECK(business_key=trading.gas_transport_capacity_key(business_key)),
 CHECK(id=trading.gas_transport_capacity_id('gas_transport_route_v1:'||business_key,book_id)),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code,product_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES trading.contexts(catalog_id,context_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_route_nodes(
 catalog_id uuid NOT NULL,route_id uuid NOT NULL,id uuid NOT NULL,node_key text NOT NULL,
 role text NOT NULL CHECK(role IN('external_receipt_v1','no_storage_transit_v1','external_delivery_v1')),
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,product_code ref_data.code NOT NULL,
 pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,route_id,id),UNIQUE(catalog_id,route_id,node_key),UNIQUE(catalog_id,route_id,point_code),
 CHECK(node_key=trading.gas_transport_capacity_key(node_key)),CHECK(id=trading.gas_transport_capacity_id('gas_transport_route_node_v1:'||node_key,route_id)),
 FOREIGN KEY(catalog_id,route_id,reference_catalog_id,reference_revision_id,product_code) REFERENCES trading.gas_transport_routes(catalog_id,id,reference_catalog_id,reference_revision_id,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code)
  REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)
);
CREATE TABLE trading.gas_transport_route_segments(
 catalog_id uuid NOT NULL,route_id uuid NOT NULL,id uuid NOT NULL,segment_key text NOT NULL,entitlement_id uuid NOT NULL,path_id uuid NOT NULL,
 from_node_id uuid NOT NULL,to_node_id uuid NOT NULL,fuel_n numeric NOT NULL,fuel_d numeric NOT NULL,loss_n numeric NOT NULL,loss_d numeric NOT NULL,
 transit_microseconds bigint NOT NULL CHECK(transit_microseconds>=0),
 PRIMARY KEY(catalog_id,route_id,id),UNIQUE(catalog_id,route_id,segment_key),UNIQUE(catalog_id,route_id,entitlement_id,path_id),
 CHECK(segment_key=trading.gas_transport_capacity_key(segment_key)),CHECK(id=trading.gas_transport_capacity_id('gas_transport_route_segment_v1:'||segment_key,route_id)),
 CHECK(from_node_id<>to_node_id),CHECK(trading.gas_transport_route_fraction(fuel_n,fuel_d) AND trading.gas_transport_route_fraction(loss_n,loss_d)),
 CHECK(fuel_n*loss_d+loss_n*fuel_d<=fuel_d*loss_d),
 FOREIGN KEY(catalog_id,route_id,from_node_id) REFERENCES trading.gas_transport_route_nodes(catalog_id,route_id,id),
 FOREIGN KEY(catalog_id,route_id,to_node_id) REFERENCES trading.gas_transport_route_nodes(catalog_id,route_id,id),
 FOREIGN KEY(catalog_id,entitlement_id,path_id) REFERENCES trading.gas_transport_capacity_paths(catalog_id,entitlement_id,id)
);
CREATE TABLE trading.gas_transport_route_plans(
 catalog_id uuid NOT NULL,route_id uuid NOT NULL,id uuid NOT NULL,stream_id uuid NOT NULL,business_key text NOT NULL,
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,definition_id uuid NOT NULL,kind text NOT NULL CHECK(kind IN('plan','release')),
 admission_policy text NOT NULL,evidence text NOT NULL CHECK(evidence=trading.gas_transport_capacity_evidence(evidence)),
 source_context_id uuid NOT NULL,source_generation integer NOT NULL CHECK(source_generation>0),assessment jsonb,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,route_id,id),UNIQUE(catalog_id,route_id,stream_id,id),UNIQUE(catalog_id,route_id,business_key,revision),UNIQUE(catalog_id,route_id,stream_id,previous_id),
 CHECK(business_key=trading.gas_transport_capacity_key(business_key)),CHECK(stream_id=trading.gas_transport_capacity_id('gas_transport_route_plan_v1:'||business_key,route_id)),
 CHECK((revision=1)=(previous_id IS NULL)),
 CHECK(kind='plan' AND admission_policy='complete_current_sources_and_conserving_route_v1' AND assessment IS NOT NULL
  OR kind='release' AND admission_policy='explicit_route_membership_release_v1' AND previous_id IS NOT NULL AND assessment IS NULL),
 FOREIGN KEY(catalog_id,route_id,definition_id) REFERENCES trading.gas_transport_routes(catalog_id,id,definition_id),
 FOREIGN KEY(catalog_id,route_id,stream_id,previous_id) REFERENCES trading.gas_transport_route_plans(catalog_id,route_id,stream_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES trading.contexts(catalog_id,context_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_route_sources(
 catalog_id uuid NOT NULL,route_plan_id uuid NOT NULL,entitlement_id uuid NOT NULL,capacity_version_id uuid NOT NULL,capacity_generation integer NOT NULL CHECK(capacity_generation>0),assessment jsonb NOT NULL,
 PRIMARY KEY(catalog_id,route_plan_id,entitlement_id),FOREIGN KEY(catalog_id,route_plan_id) REFERENCES trading.gas_transport_route_plans(catalog_id,id),
 FOREIGN KEY(catalog_id,entitlement_id,capacity_version_id) REFERENCES trading.gas_transport_capacity_versions(catalog_id,entitlement_id,id)
);
CREATE TABLE trading.gas_transport_route_source_plans(
 catalog_id uuid NOT NULL,route_plan_id uuid NOT NULL,entitlement_id uuid NOT NULL,capacity_business_key text NOT NULL,
 first_plan_revision_id uuid NOT NULL,plan_revision_id uuid NOT NULL,membership_generation integer NOT NULL CHECK(membership_generation>=0),
 PRIMARY KEY(catalog_id,route_plan_id,first_plan_revision_id),UNIQUE(catalog_id,route_plan_id,plan_revision_id),UNIQUE(catalog_id,route_plan_id,entitlement_id,plan_revision_id),
 FOREIGN KEY(catalog_id,route_plan_id,entitlement_id) REFERENCES trading.gas_transport_route_sources(catalog_id,route_plan_id,entitlement_id),
 FOREIGN KEY(catalog_id,entitlement_id,capacity_business_key,first_plan_revision_id) REFERENCES trading.gas_transport_capacity_plans(catalog_id,entitlement_id,business_key,id),
 FOREIGN KEY(catalog_id,entitlement_id,capacity_business_key,plan_revision_id) REFERENCES trading.gas_transport_capacity_plans(catalog_id,entitlement_id,business_key,id)
);
CREATE TABLE trading.gas_transport_route_flows(
 catalog_id uuid NOT NULL,route_id uuid NOT NULL,route_plan_id uuid NOT NULL,id uuid NOT NULL,segment_id uuid NOT NULL,claim_id uuid NOT NULL,
 entitlement_id uuid NOT NULL,plan_revision_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,route_plan_id,id),UNIQUE(catalog_id,route_plan_id,segment_id),UNIQUE(catalog_id,route_plan_id,claim_id),
 FOREIGN KEY(catalog_id,route_id,route_plan_id) REFERENCES trading.gas_transport_route_plans(catalog_id,route_id,id),
 FOREIGN KEY(catalog_id,route_id,segment_id) REFERENCES trading.gas_transport_route_segments(catalog_id,route_id,id),
 FOREIGN KEY(catalog_id,claim_id) REFERENCES trading.gas_transport_capacity_claims(catalog_id,id),
 FOREIGN KEY(catalog_id,route_plan_id,entitlement_id,plan_revision_id) REFERENCES trading.gas_transport_route_source_plans(catalog_id,route_plan_id,entitlement_id,plan_revision_id)
);
CREATE TABLE trading.gas_transport_route_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),route_id uuid NOT NULL,
 request jsonb NOT NULL,fact jsonb NOT NULL,outcome jsonb NOT NULL,source_context_id uuid NOT NULL,authority_until ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,route_id) REFERENCES trading.gas_transport_routes(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES trading.contexts(catalog_id,context_id)
);
CREATE INDEX gas_transport_route_fact ON trading.gas_transport_route_commands(catalog_id,route_id);
CREATE INDEX gas_transport_route_source_stream ON trading.gas_transport_route_source_plans(catalog_id,first_plan_revision_id,route_plan_id);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_transport_routes','gas_transport_route_nodes','gas_transport_route_segments','gas_transport_route_plans',
  'gas_transport_route_sources','gas_transport_route_source_plans','gas_transport_route_flows','gas_transport_route_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',name);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;
ALTER FUNCTION trading.gas_transport_route_fraction(numeric,numeric) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.gas_transport_route_fraction(numeric,numeric) FROM PUBLIC;

CREATE FUNCTION security.gas_transport_route_authority(catalog uuid,kind text) RETURNS timestamptz LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE write_until timestamptz;deadline timestamptz;BEGIN
 IF kind IS NOT NULL AND kind NOT IN('define','plan','release') THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
 IF kind IS NOT NULL THEN
  write_until=security.authority_until(CASE kind WHEN 'define' THEN 'ConfigureGasServices' ELSE 'RecordGasOperations' END);
  IF write_until IS NULL THEN RAISE EXCEPTION 'gas_transport_route_denied' USING ERRCODE='42501';END IF;
 END IF;
 deadline=security.gas_transport_capacity_authority(catalog,CASE WHEN kind='release' THEN 'plan' ELSE kind END);
 IF kind IS NOT NULL THEN deadline=least(deadline,write_until);END IF;
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'gas_transport_route_denied' USING ERRCODE='42501';END IF;RETURN deadline;
EXCEPTION WHEN insufficient_privilege THEN RAISE EXCEPTION 'gas_transport_route_denied' USING ERRCODE='42501';END$$;
CREATE FUNCTION security.require_gas_transport_route_commit(catalog uuid,kind text,original_until timestamptz) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF original_until IS NULL OR clock_timestamp()>=original_until THEN RAISE EXCEPTION 'gas_transport_route_denied' USING ERRCODE='42501';END IF;
 PERFORM security.gas_transport_route_authority(catalog,kind);
END$$;
CREATE FUNCTION security.gas_transport_route_owner(catalog uuid) RETURNS text LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.gas_transport_route_authority(catalog,NULL);
 RETURN presentation.preference_owner(security.presentation_actor(),'gas_transport_route:'||catalog::text);
END$$;

CREATE FUNCTION trading.gas_transport_route_ratio(n numeric,d numeric,bounded boolean DEFAULT false) RETURNS numeric[] LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE divisor numeric;BEGIN
 IF n IS NULL OR d IS NULL OR n<>trunc(n) OR d<>trunc(d) OR d<=0 THEN RAISE EXCEPTION 'gas_transport_route_witness';END IF;
 divisor=gcd(n,d);n=div(n,divisor);d=div(d,divisor);
 IF bounded AND(abs(n)>=power(2::numeric,4096) OR d>=power(2::numeric,4096)) THEN RAISE EXCEPTION 'gas_transport_route_numeric_range';END IF;
 RETURN ARRAY[n,d];
END$$;
CREATE FUNCTION trading.gas_transport_route_ratio_read(value jsonb,nonnegative boolean DEFAULT false) RETURNS numeric[] LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result numeric[];BEGIN
 IF jsonb_typeof(value) IS DISTINCT FROM 'object' OR NOT value?&ARRAY['n','d'] OR(SELECT count(*) FROM jsonb_object_keys(value))<>2
  OR jsonb_typeof(value->'n') IS DISTINCT FROM 'string' OR jsonb_typeof(value->'d') IS DISTINCT FROM 'string'
  OR value->>'n'!~'^(0|-?[1-9][0-9]*)$' OR value->>'d'!~'^[1-9][0-9]*$' OR length(value->>'n')>1235 OR length(value->>'d')>1234
 THEN RAISE EXCEPTION 'gas_transport_route_witness';END IF;
 IF abs((value->>'n')::numeric)>=power(2::numeric,4096) OR(value->>'d')::numeric>=power(2::numeric,4096) THEN RAISE EXCEPTION 'gas_transport_route_numeric_range';END IF;
 result=trading.gas_transport_route_ratio((value->>'n')::numeric,(value->>'d')::numeric,true);
 IF nonnegative AND result[1]<0 THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;RETURN result;
END$$;
CREATE FUNCTION trading.gas_transport_route_ratio_json(value numeric[]) RETURNS jsonb LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('n',trim_scale(value[1])::text,'d',trim_scale(value[2])::text)
$$;
CREATE FUNCTION trading.gas_transport_route_add(a numeric[],b numeric[]) RETURNS numeric[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trading.gas_transport_route_ratio(a[1]*b[2]+b[1]*a[2],a[2]*b[2])
$$;
CREATE FUNCTION trading.gas_transport_route_multiply(a numeric[],b numeric[]) RETURNS numeric[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trading.gas_transport_route_ratio(a[1]*b[1],a[2]*b[2])
$$;
CREATE FUNCTION trading.gas_transport_route_decimal(q numeric) RETURNS numeric[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trading.gas_transport_route_ratio(q*power(10::numeric,scale(q)),power(10::numeric,scale(q)),true)
$$;
CREATE FUNCTION trading.gas_transport_route_rate(q numeric[],from_at timestamptz,through_at timestamptz) RETURNS numeric[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trading.gas_transport_route_ratio(q[1]*3600000000,q[2]*extract(epoch FROM(through_at-from_at))*1000000,true)
$$;
-- UTC whole days/seconds and the bounded microsecond remainder remain exact;
-- multiplying a large bigint by an interval would first convert it to float.
CREATE FUNCTION trading.gas_transport_route_shift(at timestamptz,micros bigint) RETURNS timestamptz LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE result timestamptz;BEGIN
 IF micros IS NULL OR micros<0 OR micros::numeric>=extract(epoch FROM(TIMESTAMPTZ '2200-01-01Z'-at))*1000000 THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
 result=at+make_interval(days=>div(micros,86400000000)::integer,secs=>div(mod(micros,86400000000),1000000)::double precision)
  +mod(micros,1000000)::integer*INTERVAL '1 microsecond';
 IF result<TIMESTAMPTZ '1901-01-01Z' OR result>=TIMESTAMPTZ '2200-01-01Z' THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;RETURN result;
END$$;

CREATE FUNCTION trading.gas_transport_route_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb='{}';item record;value jsonb;ratio numeric[];BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
 FOR item IN SELECT * FROM jsonb_each(body) LOOP
  value=item.value;
  IF value<>'null'::jsonb THEN
   IF item.key~'Id$' THEN value=to_jsonb((value#>>'{}')::uuid);
   ELSIF item.key IN('businessKey','nodeKey','segmentKey','fromNodeKey','toNodeKey','evidence') THEN
    IF jsonb_typeof(value) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
    value=to_jsonb(CASE WHEN item.key='evidence' THEN trading.gas_transport_capacity_evidence(value#>>'{}') ELSE trading.gas_transport_capacity_key(value#>>'{}') END);
   ELSIF item.key IN('fuelFraction','lossFraction') THEN
    ratio=trading.gas_transport_route_ratio_read(value,true);
    IF ratio[1]>ratio[2] THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
    value=trading.gas_transport_route_ratio_json(ratio);
   ELSIF item.key='transitMicroseconds' THEN
    IF jsonb_typeof(value) IS DISTINCT FROM 'string' OR value#>>'{}'!~'^(0|[1-9][0-9]*)$' OR length(value#>>'{}')>19 OR(value#>>'{}')::numeric>9223372036854775807
    THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
   ELSIF item.key IN('nodes','segments','sources','sourcePlans','flows') THEN
    IF jsonb_typeof(value) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
    SELECT coalesce(jsonb_agg(c ORDER BY CASE item.key WHEN 'nodes' THEN c->>'nodeId' WHEN 'segments' THEN c->>'segmentId' WHEN 'flows' THEN c->>'segmentId' ELSE c->>'entitlementId' END COLLATE "C",c->>'firstPlanRevisionId' COLLATE "C"),'[]')
     INTO value FROM(SELECT trading.gas_transport_route_fact(x)c FROM jsonb_array_elements(value)x)s;
   END IF;
  END IF;
  result=result||jsonb_build_object(item.key,value);
 END LOOP;RETURN result;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR raise_exception THEN RAISE EXCEPTION 'gas_transport_route_input';END$$;
CREATE FUNCTION trading.gas_transport_route_fact_key(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=body-'expectedStateVersion'-'expectedPreviousId';BEGIN
 IF body?'sourcePlans' THEN result=jsonb_set(result,'{sourcePlans}',coalesce((SELECT jsonb_agg(x-'expectedMembershipGeneration' ORDER BY(x->>'entitlementId')::uuid,(x->>'firstPlanRevisionId')::uuid)
  FROM jsonb_array_elements(body->'sourcePlans')x),'[]'));END IF;RETURN result;
END$$;

CREATE FUNCTION trading.gas_transport_route_body(body jsonb) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE item jsonb;fuel numeric[];loss numeric[];kind text=body->>'kind';common text[]=ARRAY['kind','routeId','expectedStateVersion','expectedPreviousId','businessKey','evidence'];BEGIN
 IF kind='define' THEN
  PERFORM trading.gas_transport_capacity_keys(body,common||ARRAY['bookId','legalEntityId','legalEntityRevision','referenceCatalogId','referenceRevisionId','product','quantity','implementation','fractionBasis','flowShape','transitLaw','nodeBalanceLaw','junctionPolicy','membershipPolicy','nodes','segments']);
 ELSIF kind='plan' THEN
  PERFORM trading.gas_transport_capacity_keys(body,common||ARRAY['definitionId','admissionPolicy','sources','sourcePlans','flows']);
 ELSIF kind='release' THEN
  PERFORM trading.gas_transport_capacity_keys(body,common||ARRAY['definitionId','admissionPolicy']);
 ELSE RAISE EXCEPTION 'gas_transport_route_input';END IF;
 IF jsonb_typeof(body->'expectedStateVersion') IS DISTINCT FROM 'number' OR body->>'expectedStateVersion'!~'^(0|[1-9][0-9]*)$' OR(body->>'expectedStateVersion')::numeric>2147483647
  OR jsonb_typeof(body->'routeId') IS DISTINCT FROM 'string' OR(body->>'routeId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  OR(body->'expectedPreviousId'<>'null'::jsonb AND jsonb_typeof(body->'expectedPreviousId') IS DISTINCT FROM 'string')
  OR jsonb_typeof(body->'businessKey') IS DISTINCT FROM 'string' OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string'
 THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
 IF kind='define' THEN
  IF body->>'implementation'<>'gas_transport_chosen_uniform_route_v1' OR body->>'fractionBasis'<>'fuel_and_loss_each_fraction_of_gross_receipt_v1'
   OR body->>'flowShape'<>'one_explicit_uniform_receipt_and_delivery_per_segment_v1' OR body->>'transitLaw'<>'fixed_nonnegative_elapsed_microseconds_v1'
   OR body->>'nodeBalanceLaw'<>'simultaneous_inflow_equals_outflow_no_storage_v1' OR body->>'junctionPolicy'<>'same_exact_pipeline_point_handoff_v1'
   OR body->>'membershipPolicy'<>'whole_capacity_plan_stream_exclusive_v1' OR body->'expectedPreviousId'<>'null'::jsonb OR(body->>'expectedStateVersion')::integer<>0
   OR jsonb_typeof(body->'nodes') IS DISTINCT FROM 'array' OR jsonb_typeof(body->'segments') IS DISTINCT FROM 'array'
   OR jsonb_array_length(body->'nodes') NOT BETWEEN 2 AND 128 OR jsonb_array_length(body->'segments') NOT BETWEEN 1 AND 256
   OR jsonb_typeof(body->'legalEntityRevision') IS DISTINCT FROM 'number' OR body->>'legalEntityRevision'!~'^[1-9][0-9]*$'
   OR(body->>'routeId')::uuid<>trading.gas_transport_capacity_id('gas_transport_route_v1:'||(body->>'businessKey'),(body->>'bookId')::uuid)
  THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
  IF EXISTS(SELECT 1 FROM jsonb_each(body)x WHERE x.key NOT IN('expectedPreviousId') AND x.value='null'::jsonb) THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
  FOR item IN SELECT * FROM jsonb_array_elements(body->'nodes') LOOP
   PERFORM trading.gas_transport_capacity_keys(item,ARRAY['nodeKey','nodeId','role','pipeline','zone','point']);
   IF EXISTS(SELECT 1 FROM jsonb_each(item)x WHERE jsonb_typeof(x.value)<>'string') OR item->>'role' NOT IN('external_receipt_v1','no_storage_transit_v1','external_delivery_v1')
    OR(item->>'nodeId')::uuid<>trading.gas_transport_capacity_id('gas_transport_route_node_v1:'||(item->>'nodeKey'),(body->>'routeId')::uuid)
   THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
  END LOOP;
  FOR item IN SELECT * FROM jsonb_array_elements(body->'segments') LOOP
   PERFORM trading.gas_transport_capacity_keys(item,ARRAY['segmentKey','segmentId','entitlementId','pathId','fromNodeKey','toNodeKey','fromNodeId','toNodeId','fuelFraction','lossFraction','transitMicroseconds']);
   IF EXISTS(SELECT 1 FROM jsonb_each(item)x WHERE x.key NOT IN('fuelFraction','lossFraction') AND jsonb_typeof(x.value)<>'string')
    OR(item->>'segmentId')::uuid<>trading.gas_transport_capacity_id('gas_transport_route_segment_v1:'||(item->>'segmentKey'),(body->>'routeId')::uuid)
    OR(item->>'fromNodeId')::uuid=(item->>'toNodeId')::uuid
    OR NOT EXISTS(SELECT 1 FROM jsonb_array_elements(body->'nodes')n WHERE n->>'nodeKey'=item->>'fromNodeKey' AND n->>'nodeId'=item->>'fromNodeId')
    OR NOT EXISTS(SELECT 1 FROM jsonb_array_elements(body->'nodes')n WHERE n->>'nodeKey'=item->>'toNodeKey' AND n->>'nodeId'=item->>'toNodeId')
   THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
   fuel=trading.gas_transport_route_ratio_read(item->'fuelFraction',true);loss=trading.gas_transport_route_ratio_read(item->'lossFraction',true);
   IF fuel[1]*loss[2]+loss[1]*fuel[2]>fuel[2]*loss[2]
   THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
  END LOOP;
  IF(SELECT count(DISTINCT x->>'nodeId') FROM jsonb_array_elements(body->'nodes')x)<>jsonb_array_length(body->'nodes')
   OR(SELECT count(DISTINCT x->>'point') FROM jsonb_array_elements(body->'nodes')x)<>jsonb_array_length(body->'nodes')
   OR(SELECT count(DISTINCT x->>'segmentId') FROM jsonb_array_elements(body->'segments')x)<>jsonb_array_length(body->'segments')
   OR(SELECT count(DISTINCT(x->>'entitlementId',x->>'pathId')) FROM jsonb_array_elements(body->'segments')x)<>jsonb_array_length(body->'segments')
  THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
 ELSE
  IF jsonb_typeof(body->'definitionId') IS DISTINCT FROM 'string' OR(body->>'expectedStateVersion')::integer=0
   OR body->>'admissionPolicy' IS DISTINCT FROM (CASE kind WHEN 'plan' THEN 'complete_current_sources_and_conserving_route_v1' ELSE 'explicit_route_membership_release_v1' END)
   OR kind='release' AND body->'expectedPreviousId'='null'::jsonb THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
  IF kind='plan' THEN
   IF jsonb_typeof(body->'sources') IS DISTINCT FROM 'array' OR jsonb_typeof(body->'sourcePlans') IS DISTINCT FROM 'array' OR jsonb_typeof(body->'flows') IS DISTINCT FROM 'array'
    OR jsonb_array_length(body->'sources') NOT BETWEEN 1 AND 256 OR jsonb_array_length(body->'sourcePlans') NOT BETWEEN 1 AND 256 OR jsonb_array_length(body->'flows') NOT BETWEEN 1 AND 256
   THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
   FOR item IN SELECT * FROM jsonb_array_elements(body->'sources') LOOP
    PERFORM trading.gas_transport_capacity_keys(item,ARRAY['entitlementId','capacityVersionId','capacityGeneration']);
    IF jsonb_typeof(item->'entitlementId')<>'string' OR jsonb_typeof(item->'capacityVersionId')<>'string' OR jsonb_typeof(item->'capacityGeneration')<>'number' OR item->>'capacityGeneration'!~'^[1-9][0-9]*$' THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
   END LOOP;
   FOR item IN SELECT * FROM jsonb_array_elements(body->'sourcePlans') LOOP
    PERFORM trading.gas_transport_capacity_keys(item,ARRAY['entitlementId','firstPlanRevisionId','planRevisionId','expectedMembershipGeneration']);
    IF jsonb_typeof(item->'entitlementId')<>'string' OR jsonb_typeof(item->'firstPlanRevisionId')<>'string' OR jsonb_typeof(item->'planRevisionId')<>'string'
     OR jsonb_typeof(item->'expectedMembershipGeneration')<>'number' OR item->>'expectedMembershipGeneration'!~'^(0|[1-9][0-9]*)$' THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
   END LOOP;
   FOR item IN SELECT * FROM jsonb_array_elements(body->'flows') LOOP
    PERFORM trading.gas_transport_capacity_keys(item,ARRAY['segmentId','claimId','flowId']);
    IF EXISTS(SELECT 1 FROM jsonb_each(item)x WHERE jsonb_typeof(x.value)<>'string') OR(item->>'flowId')::uuid<>trading.gas_transport_capacity_id('gas_transport_route_flow_v1:'||(item->>'segmentId'),trading.gas_transport_capacity_id('gas_transport_route_plan_v1:'||(body->>'businessKey'),(body->>'routeId')::uuid))
    THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
   END LOOP;
   IF(SELECT count(DISTINCT x->>'entitlementId') FROM jsonb_array_elements(body->'sources')x)<>jsonb_array_length(body->'sources')
    OR(SELECT count(DISTINCT x->>'firstPlanRevisionId') FROM jsonb_array_elements(body->'sourcePlans')x)<>jsonb_array_length(body->'sourcePlans')
    OR(SELECT count(DISTINCT x->>'planRevisionId') FROM jsonb_array_elements(body->'sourcePlans')x)<>jsonb_array_length(body->'sourcePlans')
    OR(SELECT count(DISTINCT x->>'segmentId') FROM jsonb_array_elements(body->'flows')x)<>jsonb_array_length(body->'flows')
    OR(SELECT count(DISTINCT x->>'claimId') FROM jsonb_array_elements(body->'flows')x)<>jsonb_array_length(body->'flows')
   THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
  END IF;
 END IF;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'gas_transport_route_input';END$$;

CREATE FUNCTION trading.gas_transport_route_head(catalog uuid,route uuid,context uuid DEFAULT NULL,through_publication integer DEFAULT 2147483647) RETURNS integer LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT coalesce(max(publication_order),0) FROM(SELECT publication_order FROM trading.gas_transport_routes WHERE(catalog_id,id)=(catalog,route)
  UNION ALL SELECT publication_order FROM trading.gas_transport_route_plans WHERE(catalog_id,route_id)=(catalog,route))h
 WHERE publication_order<=through_publication AND(context IS NULL OR trading.gas_transport_capacity_known(catalog,publication_order,context))
$$;
CREATE FUNCTION trading.gas_transport_route_membership_generation(catalog uuid,first_plan uuid,context uuid DEFAULT NULL,excluding uuid DEFAULT NULL) RETURNS integer LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT coalesce(max(p.publication_order),0) FROM trading.gas_transport_route_plans p
 WHERE p.catalog_id=catalog AND p.id IS DISTINCT FROM excluding AND(context IS NULL OR trading.gas_transport_capacity_known(catalog,p.publication_order,context))
 AND EXISTS(SELECT 1 FROM trading.gas_transport_route_source_plans s WHERE s.catalog_id=catalog AND s.first_plan_revision_id=first_plan AND s.route_plan_id IN(p.id,p.previous_id))
$$;
CREATE FUNCTION trading.gas_transport_route_membership(catalog uuid,first_plan uuid,context uuid DEFAULT NULL,excluding uuid DEFAULT NULL) RETURNS SETOF trading.gas_transport_route_plans LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT h.* FROM(SELECT DISTINCT ON(route_id,stream_id)p.* FROM trading.gas_transport_route_plans p WHERE p.catalog_id=catalog AND p.id IS DISTINCT FROM excluding
  AND(context IS NULL OR trading.gas_transport_capacity_known(catalog,p.publication_order,context)) ORDER BY route_id,stream_id,revision DESC)h
 WHERE h.kind='plan' AND EXISTS(SELECT 1 FROM trading.gas_transport_route_source_plans s WHERE(s.catalog_id,s.route_plan_id,s.first_plan_revision_id)=(catalog,h.id,first_plan))
$$;
CREATE FUNCTION trading.require_gas_transport_route_reference(catalog uuid,route uuid) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE scope trading.gas_transport_routes;item record;BEGIN
 SELECT * INTO scope FROM trading.gas_transport_routes h WHERE(h.catalog_id,h.id)=(catalog,route);
 IF scope.id IS NULL OR security.reference_access(scope.reference_catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_transport_route_denied' USING ERRCODE='42501';END IF;
 FOR item IN SELECT DISTINCT entitlement_id FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id)=(catalog,route) LOOP
  PERFORM trading.require_gas_transport_capacity_reference(catalog,item.entitlement_id);
 END LOOP;
END$$;

CREATE FUNCTION trading.require_gas_transport_route_source(catalog uuid,body jsonb,context uuid,effective timestamptz,excluding uuid DEFAULT NULL) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE route uuid=(body->>'routeId')::uuid;at timestamptz;scope trading.gas_transport_routes;ent trading.gas_transport_capacity_entitlements;
 item jsonb;version trading.gas_transport_capacity_versions;first_head trading.gas_transport_capacity_plans;selected trading.gas_transport_capacity_plans;owner trading.gas_transport_route_plans;generation integer;before_order integer=2147483647;BEGIN
 IF security.current_write_context() IS NOT TRUE OR context IS NULL THEN RAISE EXCEPTION 'gas_transport_route_context';END IF;
 at=trading.gas_transport_capacity_cut(catalog,context);
 IF(effective AT TIME ZONE 'UTC')::date<>(at AT TIME ZONE 'UTC')::date THEN RAISE EXCEPTION 'gas_transport_route_context';END IF;
 SELECT publication_order-1 INTO before_order FROM trading.gas_transport_route_plans p WHERE(p.catalog_id,p.id)=(catalog,excluding);
 before_order=coalesce(before_order,2147483647);
 BEGIN
  PERFORM security.use_market_context(catalog,context);PERFORM security.use_trading_context(catalog,at);
  SELECT * INTO scope FROM trading.gas_transport_routes h WHERE(h.catalog_id,h.id)=(catalog,route);
  IF body->>'kind'='define' THEN
   IF scope.id IS NOT NULL AND scope.definition_id IS DISTINCT FROM excluding THEN RAISE EXCEPTION 'gas_transport_route_immutable';END IF;
   scope.book_id=(body->>'bookId')::uuid;scope.legal_entity_id=(body->>'legalEntityId')::uuid;scope.legal_entity_revision=(body->>'legalEntityRevision')::integer;
   scope.reference_catalog_id=(body->>'referenceCatalogId')::uuid;scope.reference_revision_id=(body->>'referenceRevisionId')::uuid;
   scope.product_code=body->>'product';scope.quantity_code=body->>'quantity';
  ELSE
   IF scope.definition_id IS DISTINCT FROM(body->>'definitionId')::uuid OR NOT trading.gas_transport_capacity_known(catalog,scope.publication_order,context) OR scope.effective_at>at
   THEN RAISE EXCEPTION 'gas_transport_route_source';END IF;
   PERFORM trading.require_gas_transport_route_reference(catalog,route);
   IF trading.gas_transport_route_head(catalog,route,NULL,before_order)<>trading.gas_transport_route_head(catalog,route,context,before_order)
    OR(body->>'expectedStateVersion')::integer<>trading.gas_transport_route_head(catalog,route,NULL,before_order) THEN RAISE EXCEPTION 'gas_transport_route_stale';END IF;
  END IF;
  IF security.reference_access(scope.reference_catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'gas_transport_route_denied' USING ERRCODE='42501';END IF;
  IF security.reference_version_visible(scope.reference_catalog_id,scope.reference_revision_id) IS NOT TRUE
   OR NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE(b.catalog_id,b.id)=(catalog,scope.book_id)
    AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
   OR NOT EXISTS(SELECT 1 FROM trading.legal_entities e WHERE(e.catalog_id,e.id,e.revision)=(catalog,scope.legal_entity_id,scope.legal_entity_revision) AND security.trading_visible(catalog,e.publication_order,e.effective_at))
  THEN RAISE EXCEPTION 'gas_transport_route_source';END IF;
  IF body->>'kind'='define' THEN
   FOR item IN SELECT * FROM jsonb_array_elements(body->'segments') LOOP
    SELECT * INTO ent FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id)=(catalog,(item->>'entitlementId')::uuid);
    IF ent.id IS NULL OR(ent.book_id,ent.legal_entity_id,ent.legal_entity_revision,ent.reference_catalog_id,ent.reference_revision_id,ent.product_code,ent.quantity_code) IS DISTINCT FROM
     (scope.book_id,scope.legal_entity_id,scope.legal_entity_revision,scope.reference_catalog_id,scope.reference_revision_id,scope.product_code,scope.quantity_code)
     OR NOT security.trading_visible(catalog,ent.publication_order,ent.effective_at)
     OR NOT EXISTS(SELECT 1 FROM trading.counterparties c WHERE(c.catalog_id,c.id,c.revision)=(catalog,ent.counterparty_id,ent.counterparty_revision) AND security.trading_visible(catalog,c.publication_order,c.effective_at))
    THEN RAISE EXCEPTION 'gas_transport_route_source';END IF;
    PERFORM trading.require_gas_transport_capacity_reference(catalog,ent.id);
   END LOOP;
  ELSIF body->>'kind'='plan' THEN
   IF EXISTS(SELECT 1 FROM(SELECT DISTINCT ON(stream_id)p.* FROM trading.gas_transport_route_plans p WHERE(p.catalog_id,p.route_id)=(catalog,route) AND p.id IS DISTINCT FROM excluding ORDER BY stream_id,revision DESC)h WHERE effective_at>at)
   THEN RAISE EXCEPTION 'gas_transport_route_future_source';END IF;
   FOR item IN SELECT * FROM jsonb_array_elements(body->'sources') LOOP
    generation=trading.gas_transport_capacity_head(catalog,(item->>'entitlementId')::uuid);
    IF generation<>(item->>'capacityGeneration')::integer OR generation<>trading.gas_transport_capacity_head(catalog,(item->>'entitlementId')::uuid,context) THEN RAISE EXCEPTION 'gas_transport_route_stale';END IF;
    SELECT * INTO version FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id)=(catalog,(item->>'entitlementId')::uuid) ORDER BY revision DESC LIMIT 1;
    IF version.id IS DISTINCT FROM(item->>'capacityVersionId')::uuid THEN RAISE EXCEPTION 'gas_transport_route_stale';END IF;
    IF version.effective_at>at OR EXISTS(SELECT 1 FROM(SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.entitlement_id)=(catalog,version.entitlement_id) ORDER BY business_key,revision DESC)h WHERE effective_at>at)
    THEN RAISE EXCEPTION 'gas_transport_route_future_source';END IF;
   END LOOP;
   FOR item IN SELECT * FROM jsonb_array_elements(body->'sourcePlans') LOOP
    SELECT * INTO first_head FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.id,p.entitlement_id,p.revision)=(catalog,(item->>'firstPlanRevisionId')::uuid,(item->>'entitlementId')::uuid,1);
    SELECT * INTO selected FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.entitlement_id,p.business_key)=(catalog,first_head.entitlement_id,first_head.business_key) ORDER BY revision DESC LIMIT 1;
    IF first_head.id IS NULL OR selected.id IS DISTINCT FROM(item->>'planRevisionId')::uuid THEN RAISE EXCEPTION 'gas_transport_route_stale';END IF;
    generation=trading.gas_transport_route_membership_generation(catalog,first_head.id,NULL,excluding);
    IF generation<>(item->>'expectedMembershipGeneration')::integer OR generation<>trading.gas_transport_route_membership_generation(catalog,first_head.id,context,excluding) THEN RAISE EXCEPTION 'gas_transport_route_stale';END IF;
    IF EXISTS(SELECT 1 FROM trading.gas_transport_route_plans p WHERE p.catalog_id=catalog AND p.id IS DISTINCT FROM excluding AND p.effective_at>at
     AND EXISTS(SELECT 1 FROM trading.gas_transport_route_source_plans s WHERE(s.catalog_id,s.first_plan_revision_id)=(catalog,first_head.id) AND s.route_plan_id IN(p.id,p.previous_id))
     AND p.publication_order=(SELECT max(t.publication_order) FROM trading.gas_transport_route_plans t WHERE t.catalog_id=catalog AND t.id IS DISTINCT FROM excluding
      AND EXISTS(SELECT 1 FROM trading.gas_transport_route_source_plans s WHERE(s.catalog_id,s.first_plan_revision_id)=(catalog,first_head.id) AND s.route_plan_id IN(t.id,t.previous_id))))
    THEN RAISE EXCEPTION 'gas_transport_route_future_source';END IF;
    FOR owner IN SELECT * FROM trading.gas_transport_route_membership(catalog,first_head.id,NULL,excluding) LOOP
     IF(owner.route_id,owner.stream_id)<>(route,trading.gas_transport_capacity_id('gas_transport_route_plan_v1:'||(body->>'businessKey'),route)) THEN RAISE EXCEPTION 'gas_transport_route_membership';END IF;
    END LOOP;
   END LOOP;
  END IF;
  RAISE EXCEPTION USING ERRCODE='PZ343',MESSAGE='Route source certified';
 EXCEPTION WHEN SQLSTATE 'PZ343' THEN NULL;END;
END$$;


CREATE FUNCTION trading.verify_gas_transport_route_graph(catalog uuid,route uuid) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE h trading.gas_transport_routes;BEGIN
 SELECT * INTO STRICT h FROM trading.gas_transport_routes r WHERE(r.catalog_id,r.id)=(catalog,route);
 IF(SELECT count(*) FROM trading.gas_transport_route_nodes n WHERE(n.catalog_id,n.route_id)=(catalog,route)) NOT BETWEEN 2 AND 128
  OR(SELECT count(*) FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id)=(catalog,route)) NOT BETWEEN 1 AND 256
 THEN RAISE EXCEPTION 'gas_transport_route_graph';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_transport_route_nodes n WHERE(n.catalog_id,n.route_id)=(catalog,route) AND
  CASE n.role WHEN 'external_receipt_v1' THEN EXISTS(SELECT 1 FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id,s.to_node_id)=(catalog,route,n.id)) OR NOT EXISTS(SELECT 1 FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id,s.from_node_id)=(catalog,route,n.id))
   WHEN 'external_delivery_v1' THEN EXISTS(SELECT 1 FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id,s.from_node_id)=(catalog,route,n.id)) OR NOT EXISTS(SELECT 1 FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id,s.to_node_id)=(catalog,route,n.id))
   ELSE NOT EXISTS(SELECT 1 FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id,s.from_node_id)=(catalog,route,n.id)) OR NOT EXISTS(SELECT 1 FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id,s.to_node_id)=(catalog,route,n.id)) END)
 THEN RAISE EXCEPTION 'gas_transport_route_graph';END IF;
 IF EXISTS(WITH RECURSIVE reach(a,b) AS(SELECT from_node_id,to_node_id FROM trading.gas_transport_route_segments WHERE(catalog_id,route_id)=(catalog,route)
  UNION SELECT r.a,s.to_node_id FROM reach r JOIN trading.gas_transport_route_segments s ON(s.catalog_id,s.route_id,s.from_node_id)=(catalog,route,r.b))SELECT 1 FROM reach WHERE a=b)
  OR(SELECT count(*) FROM(WITH RECURSIVE connected(id) AS(SELECT min(id::text)::uuid FROM trading.gas_transport_route_nodes WHERE(catalog_id,route_id)=(catalog,route)
   UNION SELECT CASE WHEN s.from_node_id=c.id THEN s.to_node_id ELSE s.from_node_id END FROM connected c JOIN trading.gas_transport_route_segments s ON(s.catalog_id,s.route_id)=(catalog,route) AND c.id IN(s.from_node_id,s.to_node_id))SELECT id FROM connected)reached)
   <>(SELECT count(*) FROM trading.gas_transport_route_nodes WHERE(catalog_id,route_id)=(catalog,route))
 THEN RAISE EXCEPTION 'gas_transport_route_graph';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_transport_route_segments s JOIN trading.gas_transport_capacity_paths p ON(p.catalog_id,p.entitlement_id,p.id)=(s.catalog_id,s.entitlement_id,s.path_id)
  JOIN trading.gas_transport_capacity_entitlements e ON(e.catalog_id,e.id)=(p.catalog_id,p.entitlement_id)
  JOIN trading.gas_transport_route_nodes a ON(a.catalog_id,a.route_id,a.id)=(s.catalog_id,s.route_id,s.from_node_id)
  JOIN trading.gas_transport_route_nodes b ON(b.catalog_id,b.route_id,b.id)=(s.catalog_id,s.route_id,s.to_node_id)
  WHERE(s.catalog_id,s.route_id)=(catalog,route) AND
   ((e.book_id,e.legal_entity_id,e.legal_entity_revision,e.reference_catalog_id,e.reference_revision_id,e.product_code,e.quantity_code) IS DISTINCT FROM
    (h.book_id,h.legal_entity_id,h.legal_entity_revision,h.reference_catalog_id,h.reference_revision_id,h.product_code,h.quantity_code)
    OR(a.pipeline_code,a.zone_code,a.point_code,b.pipeline_code,b.zone_code,b.point_code) IS DISTINCT FROM
     (p.pipeline_code,p.receipt_zone_code,p.receipt_point_code,p.pipeline_code,p.delivery_zone_code,p.delivery_point_code)))
 THEN RAISE EXCEPTION 'gas_transport_route_junction';END IF;
END$$;

CREATE FUNCTION trading.gas_transport_route_recorded_body(catalog uuid,route uuid,selected uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE h trading.gas_transport_routes;p trading.gas_transport_route_plans;result jsonb;BEGIN
 SELECT * INTO STRICT h FROM trading.gas_transport_routes r WHERE(r.catalog_id,r.id)=(catalog,route);
 IF selected=h.definition_id THEN
  result=jsonb_build_object('kind','define','routeId',route,'expectedStateVersion',0,'expectedPreviousId',NULL,'businessKey',h.business_key,'evidence',h.evidence,
   'bookId',h.book_id,'legalEntityId',h.legal_entity_id,'legalEntityRevision',h.legal_entity_revision,'referenceCatalogId',h.reference_catalog_id,'referenceRevisionId',h.reference_revision_id,
   'product',h.product_code,'quantity',h.quantity_code,'implementation',h.implementation,'fractionBasis',h.fraction_basis,'flowShape',h.flow_shape,'transitLaw',h.transit_law,
   'nodeBalanceLaw',h.node_balance_law,'junctionPolicy',h.junction_policy,'membershipPolicy',h.membership_policy,
   'nodes',(SELECT coalesce(jsonb_agg(jsonb_build_object('nodeId',n.id,'nodeKey',n.node_key,'role',n.role,'pipeline',n.pipeline_code,'zone',n.zone_code,'point',n.point_code) ORDER BY n.id),'[]') FROM trading.gas_transport_route_nodes n WHERE(n.catalog_id,n.route_id)=(catalog,route)),
   'segments',(SELECT coalesce(jsonb_agg(jsonb_build_object('segmentId',s.id,'segmentKey',s.segment_key,'entitlementId',s.entitlement_id,'pathId',s.path_id,
    'fromNodeId',s.from_node_id,'toNodeId',s.to_node_id,'fromNodeKey',a.node_key,'toNodeKey',b.node_key,
    'fuelFraction',trading.gas_transport_route_ratio_json(ARRAY[s.fuel_n,s.fuel_d]),'lossFraction',trading.gas_transport_route_ratio_json(ARRAY[s.loss_n,s.loss_d]),'transitMicroseconds',s.transit_microseconds::text) ORDER BY s.id),'[]')
    FROM trading.gas_transport_route_segments s JOIN trading.gas_transport_route_nodes a ON(a.catalog_id,a.route_id,a.id)=(s.catalog_id,s.route_id,s.from_node_id)
    JOIN trading.gas_transport_route_nodes b ON(b.catalog_id,b.route_id,b.id)=(s.catalog_id,s.route_id,s.to_node_id) WHERE(s.catalog_id,s.route_id)=(catalog,route)));
 ELSE
  SELECT * INTO STRICT p FROM trading.gas_transport_route_plans x WHERE(x.catalog_id,x.route_id,x.id)=(catalog,route,selected);
  result=jsonb_build_object('kind',p.kind,'routeId',route,'definitionId',p.definition_id,'expectedStateVersion',p.source_generation,'expectedPreviousId',p.previous_id,'businessKey',p.business_key,'admissionPolicy',p.admission_policy,'evidence',p.evidence);
  IF p.kind='plan' THEN
   result=result||jsonb_build_object('sources',(SELECT coalesce(jsonb_agg(jsonb_build_object('entitlementId',s.entitlement_id,'capacityVersionId',s.capacity_version_id,'capacityGeneration',s.capacity_generation) ORDER BY s.entitlement_id),'[]') FROM trading.gas_transport_route_sources s WHERE(s.catalog_id,s.route_plan_id)=(catalog,selected)),
    'sourcePlans',(SELECT coalesce(jsonb_agg(jsonb_build_object('entitlementId',s.entitlement_id,'firstPlanRevisionId',s.first_plan_revision_id,'planRevisionId',s.plan_revision_id,'expectedMembershipGeneration',s.membership_generation) ORDER BY s.entitlement_id,s.first_plan_revision_id),'[]') FROM trading.gas_transport_route_source_plans s WHERE(s.catalog_id,s.route_plan_id)=(catalog,selected)),
    'flows',(SELECT coalesce(jsonb_agg(jsonb_build_object('segmentId',f.segment_id,'claimId',f.claim_id,'flowId',f.id) ORDER BY f.segment_id),'[]') FROM trading.gas_transport_route_flows f WHERE(f.catalog_id,f.route_plan_id)=(catalog,selected)));
  END IF;
 END IF;RETURN result;
END$$;

CREATE FUNCTION trading.gas_transport_route_capacity_facts(catalog uuid,entitlement uuid,route uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE result jsonb;bound security.request_contexts;rows jsonb;BEGIN
 bound=trading.gas_transport_capacity_bound(catalog);result=security.read_gas_transport_capacity(catalog,entitlement);
 SELECT coalesce(jsonb_agg(jsonb_build_object('firstPlanRevisionId',first.id,'planRevisionId',h.id,
  'membershipGeneration',trading.gas_transport_route_membership_generation(catalog,first.id,bound.market_context_id),
  'membershipState',CASE WHEN EXISTS(SELECT 1 FROM trading.gas_transport_route_membership(catalog,first.id,bound.market_context_id)m WHERE m.route_id IS DISTINCT FROM route)
    OR EXISTS(SELECT 1 FROM trading.gas_transport_route_plans p WHERE p.catalog_id=catalog AND trading.gas_transport_capacity_known(catalog,p.publication_order,bound.market_context_id) AND p.effective_at>bound.trading_as_of
     AND EXISTS(SELECT 1 FROM trading.gas_transport_route_source_plans s WHERE(s.catalog_id,s.first_plan_revision_id)=(catalog,first.id) AND s.route_plan_id IN(p.id,p.previous_id))
     AND p.publication_order=trading.gas_transport_route_membership_generation(catalog,first.id,bound.market_context_id)) THEN 'occupied_v1'
   WHEN EXISTS(SELECT 1 FROM trading.gas_transport_route_membership(catalog,first.id,bound.market_context_id)m WHERE m.route_id=route) THEN 'owned_by_this_route_v1' ELSE 'available_v1' END) ORDER BY first.id),'[]') INTO rows
 FROM jsonb_array_elements(result->'plans')head JOIN trading.gas_transport_capacity_plans h ON(h.catalog_id,h.id)=(catalog,(head->>'id')::uuid)
 JOIN trading.gas_transport_capacity_plans first ON(first.catalog_id,first.entitlement_id,first.business_key,first.revision)=(h.catalog_id,h.entitlement_id,h.business_key,1);
 RETURN result||jsonb_build_object('sourcePlans',rows);
END$$;
CREATE FUNCTION security.gas_transport_route_choices(catalog uuid,route uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;bound security.request_contexts;result jsonb='[]';item jsonb;BEGIN
 deadline=security.gas_transport_route_authority(catalog,NULL);bound=trading.gas_transport_capacity_bound(catalog);
 IF route IS NOT NULL THEN
  PERFORM trading.require_gas_transport_route_reference(catalog,route);
  FOR item IN SELECT to_jsonb(s.entitlement_id) FROM(SELECT DISTINCT entitlement_id FROM trading.gas_transport_route_segments WHERE(catalog_id,route_id)=(catalog,route))s ORDER BY s.entitlement_id LOOP
   result=result||jsonb_build_array(trading.gas_transport_route_capacity_facts(catalog,(item#>>'{}')::uuid,route));
  END LOOP;
 ELSE
  FOR item IN SELECT * FROM jsonb_array_elements(security.gas_transport_capacity_directory(catalog)) LOOP
   result=result||jsonb_build_array(trading.gas_transport_route_capacity_facts(catalog,(item->>'id')::uuid,NULL));
  END LOOP;
 END IF;
 PERFORM security.require_gas_transport_route_commit(catalog,NULL,deadline);RETURN jsonb_build_object('sourceCapacities',result);
END$$;
CREATE FUNCTION security.read_gas_transport_route(catalog uuid,route uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE deadline timestamptz;bound security.request_contexts;h trading.gas_transport_routes;result jsonb;plans jsonb;known timestamptz;BEGIN
 deadline=security.gas_transport_route_authority(catalog,NULL);bound=trading.gas_transport_capacity_bound(catalog);
 SELECT * INTO h FROM trading.gas_transport_routes r WHERE(r.catalog_id,r.id)=(catalog,route) AND trading.gas_transport_capacity_known(catalog,r.publication_order,bound.market_context_id);
 IF h.id IS NULL THEN RAISE EXCEPTION 'gas_transport_route_source';END IF;
 PERFORM trading.require_gas_transport_route_reference(catalog,route);
 IF security.reference_version_visible(h.reference_catalog_id,h.reference_revision_id) IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE(b.catalog_id,b.id)=(catalog,h.book_id) AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
  OR NOT EXISTS(SELECT 1 FROM trading.legal_entities e WHERE(e.catalog_id,e.id,e.revision)=(catalog,h.legal_entity_id,h.legal_entity_revision) AND security.trading_visible(catalog,e.publication_order,e.effective_at))
 THEN RAISE EXCEPTION 'gas_transport_route_source';END IF;
 SELECT coalesce(jsonb_agg(to_jsonb(p)||jsonb_build_object(
  'sources',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY entitlement_id),'[]') FROM trading.gas_transport_route_sources s WHERE(s.catalog_id,s.route_plan_id)=(catalog,p.id)),
  'source_plans',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY first_plan_revision_id),'[]') FROM trading.gas_transport_route_source_plans s WHERE(s.catalog_id,s.route_plan_id)=(catalog,p.id)),
  'flows',(SELECT coalesce(jsonb_agg(to_jsonb(f)||jsonb_build_object('claim',to_jsonb(c)||jsonb_build_object('receipt_quantity',trim_scale(c.receipt_quantity)::text,'delivery_quantity',trim_scale(c.delivery_quantity)::text)) ORDER BY f.segment_id),'[]')
   FROM trading.gas_transport_route_flows f JOIN trading.gas_transport_capacity_claims c ON(c.catalog_id,c.id)=(f.catalog_id,f.claim_id) WHERE(f.catalog_id,f.route_plan_id)=(catalog,p.id))) ORDER BY p.business_key COLLATE "C"),'[]') INTO plans
 FROM(SELECT DISTINCT ON(stream_id)p.* FROM trading.gas_transport_route_plans p WHERE(p.catalog_id,p.route_id)=(catalog,route) AND trading.gas_transport_capacity_known(catalog,p.publication_order,bound.market_context_id) ORDER BY stream_id,revision DESC)p;
 SELECT known_at INTO STRICT known FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,bound.market_context_id);
 result=jsonb_build_object('contextId',bound.market_context_id,'asOf',bound.trading_as_of,'knownAt',known,'stateVersion',trading.gas_transport_route_head(catalog,route,bound.market_context_id),
  'hasFutureHeads',h.effective_at>bound.trading_as_of OR EXISTS(SELECT 1 FROM jsonb_array_elements(plans)p WHERE(p->>'effective_at')::timestamptz>bound.trading_as_of),
  'route',to_jsonb(h),'definition',CASE WHEN h.effective_at<=bound.trading_as_of THEN(to_jsonb(h)-'id')||jsonb_build_object('id',h.definition_id,'route_id',route) ELSE 'null'::jsonb END,
  'nodes',(SELECT jsonb_agg(to_jsonb(n) ORDER BY n.id) FROM trading.gas_transport_route_nodes n WHERE(n.catalog_id,n.route_id)=(catalog,route)),
  'segments',(SELECT jsonb_agg(to_jsonb(s)||jsonb_build_object('fuel_n',s.fuel_n::text,'fuel_d',s.fuel_d::text,'loss_n',s.loss_n::text,'loss_d',s.loss_d::text,'transit_microseconds',s.transit_microseconds::text) ORDER BY s.id) FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id)=(catalog,route)),
  'plans',plans,'sourceCapacities',security.gas_transport_route_choices(catalog,route)->'sourceCapacities',
  'memberships',(SELECT coalesce(jsonb_agg(jsonb_build_object('first_plan_revision_id',s.first_plan_revision_id,'stream_id',p.stream_id,'route_plan_id',p.id) ORDER BY s.first_plan_revision_id),'[]')
   FROM jsonb_array_elements(plans)head JOIN trading.gas_transport_route_plans p ON(p.catalog_id,p.id)=(catalog,(head->>'id')::uuid)
   JOIN trading.gas_transport_route_source_plans s ON(s.catalog_id,s.route_plan_id)=(p.catalog_id,p.id) WHERE p.kind='plan'));
 PERFORM security.require_gas_transport_route_commit(catalog,NULL,deadline);RETURN result;
END$$;
CREATE FUNCTION security.gas_transport_route_directory(catalog uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;bound security.request_contexts;result jsonb;BEGIN
 deadline=security.gas_transport_route_authority(catalog,NULL);bound=trading.gas_transport_capacity_bound(catalog);
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',r.id,'label',r.business_key,'revision',1) ORDER BY r.business_key COLLATE "C",r.id),'[]') INTO result FROM(
  SELECT h.* FROM trading.gas_transport_routes h WHERE h.catalog_id=catalog AND trading.gas_transport_capacity_known(catalog,h.publication_order,bound.market_context_id)
   AND security.reference_access(h.reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(h.reference_catalog_id,h.reference_revision_id)
   AND EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE(b.catalog_id,b.id)=(catalog,h.book_id)
    AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
  ORDER BY h.business_key COLLATE "C",h.id LIMIT 257)r;
 IF jsonb_array_length(result)>256 THEN RAISE EXCEPTION 'gas_transport_route_limit';END IF;
 PERFORM security.require_gas_transport_route_commit(catalog,NULL,deadline);RETURN result;
END$$;

CREATE FUNCTION trading.verify_gas_transport_route_plan(catalog uuid,selected uuid) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE plan trading.gas_transport_route_plans;scope trading.gas_transport_routes;item record;segment record;node record;part record;active record;
 expected jsonb;capacities jsonb='[]';segments jsonb='[]';nodes jsonb='[]';intervals jsonb;incoming jsonb;outgoing jsonb;
 receipt numeric[];fuel numeric[];loss numeric[];remaining numeric[];delivery numeric[];expected_rate numeric[];actual_rate numeric[];residual numeric[];
 expected_from timestamptz;expected_to timestamptz;timing boolean;BEGIN
 SELECT * INTO STRICT plan FROM trading.gas_transport_route_plans p WHERE(p.catalog_id,p.id)=(catalog,selected);
 SELECT * INTO STRICT scope FROM trading.gas_transport_routes r WHERE(r.catalog_id,r.id)=(catalog,plan.route_id);
 IF plan.kind='release' THEN
  IF EXISTS(SELECT 1 FROM trading.gas_transport_route_sources s WHERE(s.catalog_id,s.route_plan_id)=(catalog,selected))
   OR EXISTS(SELECT 1 FROM trading.gas_transport_route_flows f WHERE(f.catalog_id,f.route_plan_id)=(catalog,selected)) OR plan.assessment IS NOT NULL
  THEN RAISE EXCEPTION 'gas_transport_route_witness';END IF;RETURN;
 END IF;
 IF pg_column_size(plan.assessment)>8388608 THEN RAISE EXCEPTION 'gas_transport_route_witness';END IF;
 -- Both directions of each manifest are checked. No unused source or omitted
 -- native claim can disappear behind a feasible route subset.
 IF EXISTS((SELECT DISTINCT entitlement_id FROM trading.gas_transport_route_segments WHERE(catalog_id,route_id)=(catalog,plan.route_id))
   EXCEPT(SELECT entitlement_id FROM trading.gas_transport_route_sources WHERE(catalog_id,route_plan_id)=(catalog,selected)))
  OR EXISTS((SELECT entitlement_id FROM trading.gas_transport_route_sources WHERE(catalog_id,route_plan_id)=(catalog,selected))
   EXCEPT(SELECT DISTINCT entitlement_id FROM trading.gas_transport_route_segments WHERE(catalog_id,route_id)=(catalog,plan.route_id)))
  OR EXISTS(SELECT 1 FROM trading.gas_transport_route_sources s WHERE(s.catalog_id,s.route_plan_id)=(catalog,selected) AND NOT EXISTS(SELECT 1 FROM trading.gas_transport_route_source_plans p WHERE(p.catalog_id,p.route_plan_id,p.entitlement_id)=(catalog,selected,s.entitlement_id)))
  OR(SELECT count(*) FROM trading.gas_transport_route_flows f WHERE(f.catalog_id,f.route_plan_id)=(catalog,selected))<>(SELECT count(*) FROM trading.gas_transport_route_segments s WHERE(s.catalog_id,s.route_id)=(catalog,plan.route_id))
  OR EXISTS(SELECT 1 FROM trading.gas_transport_route_source_plans s JOIN trading.gas_transport_capacity_plans first ON(first.catalog_id,first.id)=(s.catalog_id,s.first_plan_revision_id) WHERE(s.catalog_id,s.route_plan_id)=(catalog,selected) AND first.revision<>1)
  OR EXISTS(SELECT 1 FROM trading.gas_transport_capacity_claims c JOIN trading.gas_transport_route_source_plans s ON(s.catalog_id,s.entitlement_id,s.plan_revision_id)=(c.catalog_id,c.entitlement_id,c.plan_id)
   WHERE(s.catalog_id,s.route_plan_id)=(catalog,selected) AND NOT EXISTS(SELECT 1 FROM trading.gas_transport_route_flows f WHERE(f.catalog_id,f.route_plan_id,f.claim_id)=(catalog,selected,c.id)))
  OR EXISTS(SELECT 1 FROM trading.gas_transport_route_flows f JOIN trading.gas_transport_capacity_claims c ON(c.catalog_id,c.id)=(f.catalog_id,f.claim_id)
   JOIN trading.gas_transport_route_segments s ON(s.catalog_id,s.route_id,s.id)=(f.catalog_id,f.route_id,f.segment_id)
   WHERE(f.catalog_id,f.route_plan_id)=(catalog,selected) AND((c.entitlement_id,c.plan_id,c.path_id) IS DISTINCT FROM(f.entitlement_id,f.plan_revision_id,s.path_id) OR s.entitlement_id<>f.entitlement_id
    OR f.id<>trading.gas_transport_capacity_id('gas_transport_route_flow_v1:'||s.id::text,plan.stream_id)))
 THEN RAISE EXCEPTION 'gas_transport_route_source';END IF;
 FOR item IN SELECT * FROM trading.gas_transport_route_sources s WHERE(s.catalog_id,s.route_plan_id)=(catalog,selected) ORDER BY entitlement_id LOOP
  PERFORM trading.verify_gas_transport_capacity_snapshot(catalog,item.entitlement_id,item.capacity_version_id,item.capacity_generation,'complete_feasible_replacement_v1',item.assessment);
  capacities=capacities||jsonb_build_array(jsonb_build_object('entitlementId',item.entitlement_id,'capacityVersionId',item.capacity_version_id,'capacityGeneration',item.capacity_generation,'assessment',item.assessment));
 END LOOP;
 FOR segment IN SELECT s.*,f.id flow_id,f.claim_id,c.receipt_from,c.receipt_to,c.receipt_quantity,c.delivery_from,c.delivery_to,c.delivery_quantity
  FROM trading.gas_transport_route_segments s JOIN trading.gas_transport_route_flows f ON(f.catalog_id,f.route_id,f.segment_id)=(s.catalog_id,s.route_id,s.id) AND f.route_plan_id=selected
  JOIN trading.gas_transport_capacity_claims c ON(c.catalog_id,c.id)=(f.catalog_id,f.claim_id) WHERE(s.catalog_id,s.route_id)=(catalog,plan.route_id) ORDER BY s.id LOOP
  receipt=trading.gas_transport_route_decimal(segment.receipt_quantity);delivery=trading.gas_transport_route_decimal(segment.delivery_quantity);
  fuel=trading.gas_transport_route_multiply(receipt,ARRAY[segment.fuel_n,segment.fuel_d]);loss=trading.gas_transport_route_multiply(receipt,ARRAY[segment.loss_n,segment.loss_d]);
  remaining=trading.gas_transport_route_add(trading.gas_transport_route_add(receipt,ARRAY[-fuel[1],fuel[2]]),ARRAY[-loss[1],loss[2]]);
  PERFORM trading.gas_transport_route_ratio(fuel[1],fuel[2],true);PERFORM trading.gas_transport_route_ratio(loss[1],loss[2],true);PERFORM trading.gas_transport_route_ratio(remaining[1],remaining[2],true);
  expected_from=trading.gas_transport_route_shift(segment.receipt_from,segment.transit_microseconds);expected_to=trading.gas_transport_route_shift(segment.receipt_to,segment.transit_microseconds);
  timing=(expected_from,expected_to)=(segment.delivery_from,segment.delivery_to);intervals='[]';
  FOR part IN SELECT f,lead(f) OVER(ORDER BY f)t FROM(SELECT DISTINCT unnest(ARRAY[expected_from,expected_to,segment.delivery_from,segment.delivery_to])f)b ORDER BY f LOOP
   IF part.t IS NULL THEN CONTINUE;END IF;
   expected_rate=CASE WHEN expected_from<=part.f AND expected_to>=part.t THEN trading.gas_transport_route_rate(remaining,expected_from,expected_to) ELSE ARRAY[0::numeric,1::numeric] END;
   actual_rate=CASE WHEN segment.delivery_from<=part.f AND segment.delivery_to>=part.t THEN trading.gas_transport_route_rate(delivery,segment.delivery_from,segment.delivery_to) ELSE ARRAY[0::numeric,1::numeric] END;
   residual=trading.gas_transport_route_add(actual_rate,ARRAY[-expected_rate[1],expected_rate[2]]);PERFORM trading.gas_transport_route_ratio(residual[1],residual[2],true);
   intervals=intervals||jsonb_build_array(jsonb_build_object('from',trading.gas_transport_capacity_instant(part.f),'through',trading.gas_transport_capacity_instant(part.t),
    'expectedPerHour',trading.gas_transport_route_ratio_json(expected_rate),'deliveredPerHour',trading.gas_transport_route_ratio_json(actual_rate),'residualPerHour',trading.gas_transport_route_ratio_json(residual),'conserved',residual[1]=0));
  END LOOP;
  IF NOT timing OR EXISTS(SELECT 1 FROM jsonb_array_elements(intervals)x WHERE x->'conserved'<>'true'::jsonb) THEN RAISE EXCEPTION 'gas_transport_route_conservation';END IF;
  segments=segments||jsonb_build_array(jsonb_build_object('segmentId',segment.id,'flowId',segment.flow_id,'claimId',segment.claim_id,
   'receiptQuantity',trading.gas_transport_route_ratio_json(receipt),'fuelQuantity',trading.gas_transport_route_ratio_json(fuel),'lossQuantity',trading.gas_transport_route_ratio_json(loss),
   'expectedDeliveryQuantity',trading.gas_transport_route_ratio_json(remaining),'deliveryQuantity',trading.gas_transport_route_ratio_json(delivery),'timingAligned',timing,'conserved',true,'intervals',intervals));
 END LOOP;
 FOR node IN SELECT * FROM trading.gas_transport_route_nodes n WHERE(n.catalog_id,n.route_id)=(catalog,plan.route_id) AND n.role='no_storage_transit_v1' ORDER BY id LOOP
  intervals='[]';
  FOR part IN WITH stages AS(SELECT CASE WHEN s.to_node_id=node.id THEN c.delivery_from ELSE c.receipt_from END f,CASE WHEN s.to_node_id=node.id THEN c.delivery_to ELSE c.receipt_to END t
   FROM trading.gas_transport_route_segments s JOIN trading.gas_transport_route_flows f ON(f.catalog_id,f.route_id,f.segment_id)=(s.catalog_id,s.route_id,s.id) AND f.route_plan_id=selected
   JOIN trading.gas_transport_capacity_claims c ON(c.catalog_id,c.id)=(f.catalog_id,f.claim_id) WHERE(s.catalog_id,s.route_id)=(catalog,plan.route_id) AND node.id IN(s.from_node_id,s.to_node_id)),
   bounds AS(SELECT f FROM stages UNION SELECT t FROM stages)SELECT f,lead(f) OVER(ORDER BY f)t FROM bounds ORDER BY f LOOP
   IF part.t IS NULL THEN CONTINUE;END IF;expected_rate=ARRAY[0::numeric,1::numeric];actual_rate=expected_rate;incoming='[]';outgoing='[]';
   FOR active IN SELECT s.id segment_id,f.id flow_id,c.id claim_id,s.to_node_id=node.id is_in,
    CASE WHEN s.to_node_id=node.id THEN c.delivery_from ELSE c.receipt_from END f,CASE WHEN s.to_node_id=node.id THEN c.delivery_to ELSE c.receipt_to END t,
    CASE WHEN s.to_node_id=node.id THEN c.delivery_quantity ELSE c.receipt_quantity END q
    FROM trading.gas_transport_route_segments s JOIN trading.gas_transport_route_flows f ON(f.catalog_id,f.route_id,f.segment_id)=(s.catalog_id,s.route_id,s.id) AND f.route_plan_id=selected
    JOIN trading.gas_transport_capacity_claims c ON(c.catalog_id,c.id)=(f.catalog_id,f.claim_id) WHERE(s.catalog_id,s.route_id)=(catalog,plan.route_id) AND node.id IN(s.from_node_id,s.to_node_id)
    AND CASE WHEN s.to_node_id=node.id THEN c.delivery_from ELSE c.receipt_from END<=part.f AND CASE WHEN s.to_node_id=node.id THEN c.delivery_to ELSE c.receipt_to END>=part.t ORDER BY s.id LOOP
    IF active.is_in THEN expected_rate=trading.gas_transport_route_add(expected_rate,trading.gas_transport_route_rate(trading.gas_transport_route_decimal(active.q),active.f,active.t));
     incoming=incoming||jsonb_build_array(jsonb_build_object('segmentId',active.segment_id,'flowId',active.flow_id,'claimId',active.claim_id));
    ELSE actual_rate=trading.gas_transport_route_add(actual_rate,trading.gas_transport_route_rate(trading.gas_transport_route_decimal(active.q),active.f,active.t));
     outgoing=outgoing||jsonb_build_array(jsonb_build_object('segmentId',active.segment_id,'flowId',active.flow_id,'claimId',active.claim_id));END IF;
   END LOOP;
   expected_rate=trading.gas_transport_route_ratio(expected_rate[1],expected_rate[2],true);actual_rate=trading.gas_transport_route_ratio(actual_rate[1],actual_rate[2],true);
   residual=trading.gas_transport_route_add(expected_rate,ARRAY[-actual_rate[1],actual_rate[2]]);PERFORM trading.gas_transport_route_ratio(residual[1],residual[2],true);
   IF residual[1]<>0 THEN RAISE EXCEPTION 'gas_transport_route_conservation';END IF;
   intervals=intervals||jsonb_build_array(jsonb_build_object('from',trading.gas_transport_capacity_instant(part.f),'through',trading.gas_transport_capacity_instant(part.t),'incoming',incoming,'outgoing',outgoing,
    'incomingPerHour',trading.gas_transport_route_ratio_json(expected_rate),'outgoingPerHour',trading.gas_transport_route_ratio_json(actual_rate),'residualPerHour',trading.gas_transport_route_ratio_json(residual),'conserved',true));
  END LOOP;
  nodes=nodes||jsonb_build_array(jsonb_build_object('nodeId',node.id,'conserved',true,'intervals',intervals));
 END LOOP;
 expected=jsonb_build_object('implementation','gas_transport_route_witness_v1','definitionId',scope.definition_id,'routePlanId',plan.stream_id,'revisionId',selected,'feasible',true,'capacities',capacities,'segments',segments,'nodes',nodes);
 IF plan.assessment IS DISTINCT FROM expected THEN RAISE EXCEPTION 'gas_transport_route_witness';END IF;
END$$;

CREATE FUNCTION trading.require_gas_transport_route_record(catalog uuid,route uuid,selected uuid,numerical boolean) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE command trading.gas_transport_route_commands;recorded jsonb;h trading.gas_transport_routes;p trading.gas_transport_route_plans;publication integer;actor uuid;context uuid;effective timestamptz;BEGIN
 SELECT * INTO STRICT h FROM trading.gas_transport_routes r WHERE(r.catalog_id,r.id)=(catalog,route);
 IF selected=h.definition_id THEN publication=h.publication_order;actor=h.actor_id;context=h.source_context_id;effective=h.effective_at;
 ELSE SELECT * INTO STRICT p FROM trading.gas_transport_route_plans x WHERE(x.catalog_id,x.route_id,x.id)=(catalog,route,selected);
  publication=p.publication_order;actor=p.actor_id;context=p.source_context_id;effective=p.effective_at;
 END IF;
 recorded=trading.gas_transport_route_recorded_body(catalog,route,selected);PERFORM trading.gas_transport_route_body(recorded);
 SELECT * INTO command FROM trading.gas_transport_route_commands c WHERE(c.catalog_id,c.operation_id,c.route_id,c.actor_id)=(catalog,selected,route,actor) AND c.outcome->'changed'='true'::jsonb;
 IF command.operation_id IS NULL OR command.request->'body' IS DISTINCT FROM recorded OR command.source_context_id<>context
  OR NOT EXISTS(SELECT 1 FROM trading.publications x WHERE(x.catalog_id,x.publication_order,x.publication_xid)=(catalog,publication,pg_current_xact_id()))
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.actor_id=actor AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'gas_transport_route_integrity';END IF;
 IF numerical THEN
  IF(SELECT count(*) FROM(SELECT definition_id FROM trading.gas_transport_routes WHERE(catalog_id,publication_order)=(catalog,publication)
   UNION ALL SELECT id FROM trading.gas_transport_route_plans WHERE(catalog_id,publication_order)=(catalog,publication))headers)<>1
   OR trading.gas_transport_capacity_known(catalog,publication,context) THEN RAISE EXCEPTION 'gas_transport_route_context';END IF;
  IF p.id IS NOT NULL THEN
   IF p.previous_id IS DISTINCT FROM(SELECT v.id FROM trading.gas_transport_route_plans v WHERE(v.catalog_id,v.route_id,v.stream_id,v.revision)=(catalog,route,p.stream_id,p.revision-1))
    OR p.previous_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.gas_transport_route_plans v WHERE(v.catalog_id,v.id)=(catalog,p.previous_id) AND v.publication_order<publication AND v.effective_at<=effective AND trading.gas_transport_capacity_known(catalog,v.publication_order,context))
   THEN RAISE EXCEPTION 'gas_transport_route_stale';END IF;
   PERFORM trading.verify_gas_transport_route_plan(catalog,selected);
  ELSE PERFORM trading.verify_gas_transport_route_graph(catalog,route);END IF;
  PERFORM trading.require_gas_transport_route_source(catalog,recorded,context,effective,selected);
 END IF;
END$$;
CREATE FUNCTION trading.guard_gas_transport_route() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE row_value jsonb=to_jsonb(NEW);route uuid;selected uuid;BEGIN
 IF TG_TABLE_NAME IN('gas_transport_route_sources','gas_transport_route_source_plans') THEN
  selected=(row_value->>'route_plan_id')::uuid;SELECT p.route_id INTO STRICT route FROM trading.gas_transport_route_plans p WHERE(p.catalog_id,p.id)=(NEW.catalog_id,selected);
 ELSIF TG_TABLE_NAME IN('gas_transport_route_plans','gas_transport_route_flows') THEN
  route=NEW.route_id;selected=(row_value->>CASE TG_TABLE_NAME WHEN 'gas_transport_route_plans' THEN 'id' ELSE 'route_plan_id' END)::uuid;
 ELSE
  route=(row_value->>CASE TG_TABLE_NAME WHEN 'gas_transport_routes' THEN 'id' ELSE 'route_id' END)::uuid;
  SELECT definition_id INTO STRICT selected FROM trading.gas_transport_routes r WHERE(r.catalog_id,r.id)=(NEW.catalog_id,route);
 END IF;
 PERFORM trading.require_gas_transport_route_record(NEW.catalog_id,route,selected,TG_TABLE_NAME IN('gas_transport_routes','gas_transport_route_plans'));RETURN NULL;
END$$;
CREATE FUNCTION trading.guard_gas_transport_route_command() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected uuid;kind text=NEW.request#>>'{body,kind}';h trading.gas_transport_routes;p trading.gas_transport_route_plans;expected jsonb;effective timestamptz;BEGIN
 SELECT * INTO STRICT h FROM trading.gas_transport_routes r WHERE(r.catalog_id,r.id)=(NEW.catalog_id,NEW.route_id);
 IF NEW.request->>'contextId' IS DISTINCT FROM NEW.source_context_id::text OR NEW.request#>>'{body,routeId}' IS DISTINCT FROM NEW.route_id::text
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.actor_id=NEW.actor_id AND q.expires_at>clock_timestamp())
  OR jsonb_typeof(NEW.outcome->'changed') IS DISTINCT FROM 'boolean' THEN RAISE EXCEPTION 'gas_transport_route_integrity';END IF;
 PERFORM trading.gas_transport_route_body(NEW.request->'body');
 IF kind='define' THEN selected=h.definition_id;effective=h.effective_at;
  expected=jsonb_build_object('routeId',NEW.route_id,'kind',kind,'changed',NEW.outcome->'changed','definitionId',h.definition_id,'stateVersion',h.publication_order);
 ELSE
  selected=(NEW.outcome->>'revisionId')::uuid;
  SELECT * INTO STRICT p FROM trading.gas_transport_route_plans x WHERE(x.catalog_id,x.route_id,x.id)=(NEW.catalog_id,NEW.route_id,selected);effective=p.effective_at;
  expected=jsonb_build_object('routeId',NEW.route_id,'kind',p.kind,'changed',NEW.outcome->'changed','definitionId',h.definition_id,'stateVersion',p.publication_order,'routePlanId',p.stream_id,'revisionId',p.id,'revision',p.revision);
 END IF;
 IF NEW.outcome IS DISTINCT FROM expected OR NEW.request->>'effectiveAt' IS DISTINCT FROM trading.gas_transport_capacity_instant(effective)
  OR NEW.fact IS DISTINCT FROM jsonb_build_object('body',trading.gas_transport_route_fact_key(trading.gas_transport_route_fact(NEW.request->'body')),'effectiveAt',NEW.request->'effectiveAt')
  OR NEW.fact->'body' IS DISTINCT FROM trading.gas_transport_route_fact_key(trading.gas_transport_route_recorded_body(NEW.catalog_id,NEW.route_id,selected))
 THEN RAISE EXCEPTION 'gas_transport_route_integrity';END IF;
 IF NEW.outcome->'changed'='true'::jsonb THEN
  IF selected<>NEW.operation_id OR EXISTS(SELECT 1 FROM trading.gas_transport_route_commands c WHERE(c.catalog_id,c.route_id,c.fact)=(NEW.catalog_id,NEW.route_id,NEW.fact) AND c.operation_id<>NEW.operation_id AND c.outcome->'changed'='true'::jsonb)
  THEN RAISE EXCEPTION 'gas_transport_route_integrity';END IF;
  PERFORM trading.require_gas_transport_route_record(NEW.catalog_id,NEW.route_id,selected,true);
 ELSE
  IF NOT EXISTS(SELECT 1 FROM trading.gas_transport_route_commands c WHERE(c.catalog_id,c.route_id,c.fact,c.source_context_id)=(NEW.catalog_id,NEW.route_id,NEW.fact,NEW.source_context_id)
   AND c.operation_id<>NEW.operation_id AND c.outcome->'changed'='true'::jsonb AND c.outcome-'changed'=NEW.outcome-'changed')
  THEN RAISE EXCEPTION 'gas_transport_route_integrity';END IF;
 END IF;
 PERFORM trading.require_gas_transport_route_reference(NEW.catalog_id,NEW.route_id);
 PERFORM security.require_gas_transport_route_commit(NEW.catalog_id,kind,NEW.authority_until);RETURN NULL;
END$$;
CREATE FUNCTION trading.guard_gas_transport_route_deadline() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_transport_route_commit(NEW.catalog_id,NEW.request#>>'{body,kind}',NEW.authority_until);
 PERFORM trading.require_gas_transport_route_reference(NEW.catalog_id,NEW.route_id);
 -- Eager numerical/member validation does not consume this deferred event.
 -- A later sibling write may change capacity or exclusive membership after
 -- the producer returned. Recheck the original certificate, never a fresh cut.
 IF NEW.outcome->'changed'='true'::jsonb THEN
  PERFORM trading.require_gas_transport_route_source(NEW.catalog_id,NEW.request->'body',NEW.source_context_id,
   trading.gas_transport_capacity_at(NEW.request->>'effectiveAt'),(NEW.outcome->>CASE WHEN NEW.request#>>'{body,kind}'='define' THEN 'definitionId' ELSE 'revisionId' END)::uuid);
 END IF;RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER valid_gas_transport_route_command AFTER INSERT ON trading.gas_transport_route_commands DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_route_command();
CREATE CONSTRAINT TRIGGER valid_gas_transport_route_deadline AFTER INSERT ON trading.gas_transport_route_commands DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_route_deadline();
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['gas_transport_routes','gas_transport_route_nodes','gas_transport_route_segments','gas_transport_route_plans','gas_transport_route_sources','gas_transport_route_source_plans','gas_transport_route_flows'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER valid_gas_transport_route AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_route()',name);
 END LOOP;
END$$;

CREATE FUNCTION security.gas_transport_route_status(catalog uuid,operation uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;configure_until timestamptz;record_until timestamptz;selected_until timestamptz;prior trading.gas_transport_route_commands;actor uuid;kind text;BEGIN
 configure_until=security.authority_until('ConfigureGasServices');record_until=security.authority_until('RecordGasOperations');
 deadline=security.gas_transport_route_authority(catalog,NULL);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.gas_transport_route_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF NOT FOUND THEN PERFORM security.require_gas_transport_route_commit(catalog,NULL,deadline);RETURN jsonb_build_object('found',false,'authorityUntil',deadline);END IF;
 IF prior.actor_id<>actor THEN RAISE EXCEPTION 'gas_transport_route_denied' USING ERRCODE='42501';END IF;
 kind=prior.request#>>'{body,kind}';selected_until=CASE kind WHEN 'define' THEN configure_until ELSE record_until END;
 IF selected_until IS NULL THEN RAISE EXCEPTION 'gas_transport_route_denied' USING ERRCODE='42501';END IF;
 PERFORM trading.require_gas_transport_route_reference(catalog,prior.route_id);PERFORM security.require_gas_transport_route_commit(catalog,kind,least(deadline,selected_until));
 RETURN jsonb_build_object('found',true,'authorityUntil',least(deadline,selected_until),'operationId',operation,'routeId',prior.route_id,'kind',kind,'command',prior.request->'body','effectiveAt',prior.request->'effectiveAt','outcome',prior.outcome);
END$$;
CREATE FUNCTION security.gas_transport_route_prior_fact(catalog uuid,body jsonb,effective_at timestamptz) RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;prior trading.gas_transport_route_commands;fact_value jsonb;BEGIN
 deadline=security.gas_transport_route_authority(catalog,body->>'kind');body=trading.gas_transport_route_fact(body);PERFORM trading.gas_transport_route_body(body);
 IF effective_at IS NULL OR NOT isfinite(effective_at) OR effective_at<TIMESTAMPTZ '1901-01-01Z' OR effective_at>=TIMESTAMPTZ '2200-01-01Z' OR pg_column_size(body)>4194304 THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
 fact_value=jsonb_build_object('body',trading.gas_transport_route_fact_key(body),'effectiveAt',trading.gas_transport_capacity_instant(effective_at));
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.gas_transport_route_commands c WHERE(c.catalog_id,c.route_id,c.fact)=(catalog,(body->>'routeId')::uuid,fact_value) AND c.outcome->'changed'='true'::jsonb LIMIT 1;
 IF FOUND THEN PERFORM trading.require_gas_transport_route_reference(catalog,prior.route_id);END IF;
 PERFORM security.require_gas_transport_route_commit(catalog,body->>'kind',deadline);RETURN prior.operation_id IS NOT NULL;
END$$;
CREATE FUNCTION security.record_gas_transport_route(catalog uuid,operation uuid,body jsonb,effective_at timestamptz,witness jsonb,source_context uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;actor uuid;kind text;route uuid;stream uuid;prior trading.gas_transport_route_commands;h trading.gas_transport_routes;p trading.gas_transport_route_plans;
 payload jsonb;fact_value jsonb;result jsonb;item jsonb;capacity jsonb;claim trading.gas_transport_capacity_claims;source_plan trading.gas_transport_capacity_plans;previous uuid;revision integer;publication integer;BEGIN
 kind=body->>'kind';deadline=security.gas_transport_route_authority(catalog,kind);
 IF kind IS NULL OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR effective_at IS NULL OR NOT isfinite(effective_at)
  OR effective_at<TIMESTAMPTZ '1901-01-01Z' OR effective_at>=TIMESTAMPTZ '2200-01-01Z' OR body IS NULL OR pg_column_size(body)>4194304 THEN RAISE EXCEPTION 'gas_transport_route_input';END IF;
 body=trading.gas_transport_route_fact(body);PERFORM trading.gas_transport_route_body(body);route=(body->>'routeId')::uuid;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 payload=jsonb_build_object('body',body,'effectiveAt',trading.gas_transport_capacity_instant(effective_at),'contextId',source_context);
 fact_value=jsonb_build_object('body',trading.gas_transport_route_fact_key(body),'effectiveAt',trading.gas_transport_capacity_instant(effective_at));
 PERFORM pg_advisory_xact_lock(730849102);PERFORM security.require_gas_transport_route_commit(catalog,kind,deadline);
 SELECT * INTO prior FROM trading.gas_transport_route_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>actor OR prior.request-'contextId' IS DISTINCT FROM payload-'contextId' THEN RAISE EXCEPTION 'gas_transport_route_conflict';END IF;
  PERFORM security.gas_transport_route_status(catalog,operation);RETURN prior.outcome;
 END IF;
 SELECT * INTO prior FROM trading.gas_transport_route_commands c WHERE(c.catalog_id,c.route_id,c.fact)=(catalog,route,fact_value) AND c.outcome->'changed'='true'::jsonb LIMIT 1;
 IF FOUND THEN
  PERFORM trading.require_gas_transport_route_reference(catalog,route);source_context=prior.source_context_id;payload=jsonb_set(payload,'{contextId}',to_jsonb(source_context));
  result=prior.outcome||jsonb_build_object('changed',false);
 ELSE
  IF security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
  PERFORM trading.require_gas_transport_route_source(catalog,body,source_context,effective_at);
  SELECT * INTO h FROM trading.gas_transport_routes r WHERE(r.catalog_id,r.id)=(catalog,route) FOR UPDATE;
  IF kind='define' THEN
   IF h.id IS NOT NULL THEN RAISE EXCEPTION 'gas_transport_route_immutable';END IF;
  ELSE
   IF h.id IS NULL THEN RAISE EXCEPTION 'gas_transport_route_source';END IF;
   stream=trading.gas_transport_capacity_id('gas_transport_route_plan_v1:'||(body->>'businessKey'),route);
   SELECT * INTO p FROM trading.gas_transport_route_plans x WHERE(x.catalog_id,x.route_id,x.stream_id)=(catalog,route,stream) ORDER BY x.revision DESC LIMIT 1;
   previous=p.id;revision=coalesce(p.revision,0)+1;
   IF(body->>'expectedPreviousId')::uuid IS DISTINCT FROM previous OR p.effective_at>effective_at THEN RAISE EXCEPTION 'gas_transport_route_stale';END IF;
   IF kind='plan' AND(jsonb_typeof(witness) IS DISTINCT FROM 'object' OR pg_column_size(witness)>8388608) OR kind='release' AND witness IS NOT NULL THEN RAISE EXCEPTION 'gas_transport_route_witness';END IF;
  END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  IF kind='define' THEN
   INSERT INTO trading.gas_transport_routes VALUES(catalog,route,operation,body->>'businessKey',(body->>'bookId')::uuid,(body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,
    (body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'product',body->>'quantity',body->>'implementation',body->>'fractionBasis',body->>'flowShape',body->>'transitLaw',body->>'nodeBalanceLaw',body->>'junctionPolicy',body->>'membershipPolicy',body->>'evidence',source_context,actor,publication,effective_at);
   INSERT INTO trading.gas_transport_route_nodes SELECT catalog,route,(n->>'nodeId')::uuid,n->>'nodeKey',n->>'role',(body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'product',n->>'pipeline',n->>'zone',n->>'point' FROM jsonb_array_elements(body->'nodes')n;
   INSERT INTO trading.gas_transport_route_segments SELECT catalog,route,(s->>'segmentId')::uuid,s->>'segmentKey',(s->>'entitlementId')::uuid,(s->>'pathId')::uuid,(s->>'fromNodeId')::uuid,(s->>'toNodeId')::uuid,
    (s#>>'{fuelFraction,n}')::numeric,(s#>>'{fuelFraction,d}')::numeric,(s#>>'{lossFraction,n}')::numeric,(s#>>'{lossFraction,d}')::numeric,(s->>'transitMicroseconds')::bigint FROM jsonb_array_elements(body->'segments')s;
   result=jsonb_build_object('routeId',route,'kind',kind,'changed',true,'definitionId',operation,'stateVersion',publication);
  ELSE
   INSERT INTO trading.gas_transport_route_plans VALUES(catalog,route,operation,stream,body->>'businessKey',revision,previous,(body->>'definitionId')::uuid,kind,body->>'admissionPolicy',body->>'evidence',source_context,(body->>'expectedStateVersion')::integer,witness,actor,publication,effective_at);
   IF kind='plan' THEN
    FOR item IN SELECT * FROM jsonb_array_elements(body->'sources') LOOP
     SELECT x INTO capacity FROM jsonb_array_elements(witness->'capacities')x WHERE(x->>'entitlementId')::uuid=(item->>'entitlementId')::uuid;
     IF capacity IS NULL THEN RAISE EXCEPTION 'gas_transport_route_witness';END IF;
     INSERT INTO trading.gas_transport_route_sources VALUES(catalog,operation,(item->>'entitlementId')::uuid,(item->>'capacityVersionId')::uuid,(item->>'capacityGeneration')::integer,capacity->'assessment');
    END LOOP;
    FOR item IN SELECT * FROM jsonb_array_elements(body->'sourcePlans') LOOP
     SELECT * INTO STRICT source_plan FROM trading.gas_transport_capacity_plans x WHERE(x.catalog_id,x.id)=(catalog,(item->>'planRevisionId')::uuid);
     INSERT INTO trading.gas_transport_route_source_plans VALUES(catalog,operation,(item->>'entitlementId')::uuid,source_plan.business_key,(item->>'firstPlanRevisionId')::uuid,(item->>'planRevisionId')::uuid,(item->>'expectedMembershipGeneration')::integer);
    END LOOP;
    FOR item IN SELECT * FROM jsonb_array_elements(body->'flows') LOOP
     SELECT * INTO STRICT claim FROM trading.gas_transport_capacity_claims c WHERE(c.catalog_id,c.id)=(catalog,(item->>'claimId')::uuid);
     INSERT INTO trading.gas_transport_route_flows VALUES(catalog,route,operation,(item->>'flowId')::uuid,(item->>'segmentId')::uuid,claim.id,claim.entitlement_id,claim.plan_id);
    END LOOP;
   END IF;
   result=jsonb_build_object('routeId',route,'kind',kind,'changed',true,'definitionId',h.definition_id,'stateVersion',publication,'routePlanId',stream,'revisionId',operation,'revision',revision);
  END IF;
 END IF;
 INSERT INTO trading.gas_transport_route_commands VALUES(catalog,operation,actor,route,payload,fact_value,result,source_context,deadline);
 SET CONSTRAINTS trading.valid_gas_transport_route,trading.valid_gas_transport_route_command IMMEDIATE;
 SET CONSTRAINTS trading.valid_gas_transport_route,trading.valid_gas_transport_route_command DEFERRED;
 PERFORM security.require_gas_transport_route_commit(catalog,kind,deadline);RETURN result;
END$$;

DO $$DECLARE signature regprocedure;BEGIN
 FOR signature IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE
  n.nspname='trading' AND(p.proname LIKE '%gas_transport_route%' OR p.proname='verify_gas_transport_capacity_snapshot')
  OR n.nspname='security' AND p.proname LIKE '%gas_transport_route%' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.gas_transport_route_authority(uuid,text),security.require_gas_transport_route_commit(uuid,text,timestamptz),security.gas_transport_route_owner(uuid),
 security.gas_transport_route_choices(uuid,uuid),security.read_gas_transport_route(uuid,uuid),security.gas_transport_route_directory(uuid),security.gas_transport_route_status(uuid,uuid),
 security.gas_transport_route_prior_fact(uuid,jsonb,timestamptz),security.record_gas_transport_route(uuid,uuid,jsonb,timestamptz,jsonb,uuid) TO openetrm_runtime;
COMMIT;
