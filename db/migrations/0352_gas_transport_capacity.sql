-- Direct-path operational capacity. No financial trade, title, cash or flow.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- Page identity admits prompts only, not assistant operations on entitlements.
DO $pages$ DECLARE definition text;marker text:='''facility_activity''';BEGIN
 SELECT pg_get_functiondef(p.oid) INTO STRICT definition FROM pg_proc p WHERE p.oid='assistant.is_prompt_page(text)'::regprocedure
  AND md5(p.prosrc)='de1737949f2a9acf35442d30c6c10377' AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
  AND NOT p.prosecdef AND p.provolatile='i' AND p.prokind='f' AND p.proparallel='u' AND NOT p.proisstrict AND NOT p.proleakproof
  AND p.provariadic=0 AND p.proargmodes IS NULL AND p.proargnames=ARRAY['page'] AND p.prorettype='boolean'::regtype
  AND p.prolang=(SELECT oid FROM pg_language WHERE lanname='sql') AND p.proconfig=ARRAY['search_path=pg_catalog']
  AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[];
 IF(length(definition)-length(replace(definition,marker,'')))/length(marker)<>1
  OR definition LIKE '%''transport_capacity''%' THEN RAISE EXCEPTION 'gas_transport_capacity_upgrade: unexpected helper assistant.is_prompt_page(text)';END IF;
 EXECUTE replace(definition,marker,marker||',''transport_capacity''');
EXCEPTION WHEN no_data_found THEN RAISE EXCEPTION 'gas_transport_capacity_upgrade: unexpected helper assistant.is_prompt_page(text)';
END $pages$;

CREATE FUNCTION trading.gas_transport_capacity_key(value text) RETURNS text LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result text=btrim(value,' ');BEGIN
 IF result IS NULL OR length(result) NOT BETWEEN 1 AND 128 OR result~'[\x01-\x1f\x7f-\x9f]'
 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;RETURN result;
END$$;
CREATE FUNCTION trading.gas_transport_capacity_evidence(value text) RETURNS text LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result text=btrim(value,' ');BEGIN
 IF result IS NULL OR length(result) NOT BETWEEN 1 AND 2000 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;RETURN result;
END$$;

CREATE FUNCTION trading.gas_transport_capacity_at(value text) RETURNS timestamptz LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result timestamptz;BEGIN
 result=settlement_data.futures_margin_utc_instant(value);
 IF result IS NULL OR result<TIMESTAMPTZ '1901-01-01Z' OR result>=TIMESTAMPTZ '2200-01-01Z' THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 RETURN result;
EXCEPTION WHEN invalid_datetime_format OR datetime_field_overflow OR raise_exception THEN RAISE EXCEPTION 'gas_transport_capacity_input';END$$;
CREATE FUNCTION trading.gas_transport_capacity_instant(value timestamptz) RETURNS text LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT to_char(value AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"')
$$;
CREATE FUNCTION trading.gas_transport_capacity_id(domain text,scope uuid,ordinal integer DEFAULT 0) RETURNS uuid LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT substr(encode(sha256(convert_to(domain||':'||scope::text||':'||ordinal::text,'UTF8')),'hex'),1,32)::uuid
$$;
CREATE FUNCTION trading.gas_transport_capacity_quantity(value text) RETURNS numeric LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result numeric;BEGIN
 result=trading.gas_owned_quantity(value);IF result<0 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;RETURN trim_scale(result);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR raise_exception THEN RAISE EXCEPTION 'gas_transport_capacity_input';END$$;
CREATE FUNCTION trading.gas_transport_capacity_keys(body jsonb,keys text[]) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(body)k)
  IS DISTINCT FROM(SELECT array_agg(k ORDER BY k) FROM unnest(keys)k) THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
END$$;

CREATE TABLE trading.gas_transport_capacity_entitlements(
 catalog_id uuid NOT NULL,id uuid NOT NULL,business_key text NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,book_id uuid NOT NULL,
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,
 product_code ref_data.code NOT NULL,quantity_code ref_data.code NOT NULL,from_at ref_data.instant NOT NULL,through_at ref_data.instant NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,book_id,business_key),
 UNIQUE(catalog_id,id,reference_catalog_id,reference_revision_id,product_code),
 CHECK(business_key=trading.gas_transport_capacity_key(business_key)),CHECK(from_at<through_at),
 CHECK(id=trading.gas_transport_capacity_id('gas_transport_entitlement_v1:'||business_key,book_id)),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code,product_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_capacity_paths(
 catalog_id uuid NOT NULL,entitlement_id uuid NOT NULL,id uuid NOT NULL,path_key text NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,product_code ref_data.code NOT NULL,
 pipeline_code ref_data.code NOT NULL,receipt_zone_code ref_data.code NOT NULL,receipt_point_code ref_data.code NOT NULL,
 delivery_zone_code ref_data.code NOT NULL,delivery_point_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,entitlement_id,id),UNIQUE(catalog_id,entitlement_id,path_key),CHECK(path_key=trading.gas_transport_capacity_key(path_key)),
 CHECK(id=trading.gas_transport_capacity_id('gas_transport_path_v1:'||path_key,entitlement_id)),
 FOREIGN KEY(catalog_id,entitlement_id,reference_catalog_id,reference_revision_id,product_code)
  REFERENCES trading.gas_transport_capacity_entitlements(catalog_id,id,reference_catalog_id,reference_revision_id,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,receipt_zone_code,receipt_point_code,product_code)
  REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,delivery_zone_code,delivery_point_code,product_code)
  REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)
);
CREATE TABLE trading.gas_transport_capacity_points(
 catalog_id uuid NOT NULL,entitlement_id uuid NOT NULL,axis text NOT NULL CHECK(axis IN('receipt','delivery')),point_code ref_data.code NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,product_code ref_data.code NOT NULL,
 PRIMARY KEY(catalog_id,entitlement_id,axis,point_code),
 FOREIGN KEY(catalog_id,entitlement_id,reference_catalog_id,reference_revision_id,product_code)
  REFERENCES trading.gas_transport_capacity_entitlements(catalog_id,id,reference_catalog_id,reference_revision_id,product_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,product_code,point_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code)
);
CREATE TABLE trading.gas_transport_capacity_versions(
 catalog_id uuid NOT NULL,entitlement_id uuid NOT NULL,id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 implementation text NOT NULL CHECK(implementation='gas_transport_gross_uniform_capacity_v1'),
 rate_basis text NOT NULL CHECK(rate_basis='native_quantity_per_elapsed_hour_v1'),
 flow_shape text NOT NULL CHECK(flow_shape='uniform_each_explicit_receipt_delivery_interval_v1'),
 agreement_basis text NOT NULL CHECK(agreement_basis='agreement_receipt_route_gross_receipts_v1'),
 delivery_basis text NOT NULL CHECK(delivery_basis='delivery_point_gross_deliveries_v1'),evidence text NOT NULL CHECK(evidence=trading.gas_transport_capacity_evidence(evidence)),
 source_context_id uuid NOT NULL,source_generation integer NOT NULL CHECK(source_generation>=0),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,entitlement_id,id),UNIQUE(catalog_id,entitlement_id,revision),UNIQUE(catalog_id,entitlement_id,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),FOREIGN KEY(catalog_id,entitlement_id) REFERENCES trading.gas_transport_capacity_entitlements(catalog_id,id),
 FOREIGN KEY(catalog_id,entitlement_id,previous_id) REFERENCES trading.gas_transport_capacity_versions(catalog_id,entitlement_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_capacity_limits(
 catalog_id uuid NOT NULL,entitlement_id uuid NOT NULL,version_id uuid NOT NULL,axis text NOT NULL CHECK(axis IN('agreement','receipt','delivery','route')),
 point_code ref_data.code,path_id uuid,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 255),
 from_at ref_data.instant NOT NULL,through_at ref_data.instant NOT NULL,maximum_per_hour numeric NOT NULL CHECK(maximum_per_hour>=0 AND maximum_per_hour<1e24 AND scale(maximum_per_hour)<=18),
 UNIQUE NULLS NOT DISTINCT(catalog_id,version_id,axis,point_code,path_id,ordinal),CHECK(from_at<through_at),
 CHECK((axis='agreement' AND point_code IS NULL AND path_id IS NULL) OR(axis IN('receipt','delivery') AND point_code IS NOT NULL AND path_id IS NULL) OR(axis='route' AND point_code IS NULL AND path_id IS NOT NULL)),
 FOREIGN KEY(catalog_id,entitlement_id,version_id) REFERENCES trading.gas_transport_capacity_versions(catalog_id,entitlement_id,id),
 FOREIGN KEY(catalog_id,entitlement_id,axis,point_code) REFERENCES trading.gas_transport_capacity_points(catalog_id,entitlement_id,axis,point_code),
 FOREIGN KEY(catalog_id,entitlement_id,path_id) REFERENCES trading.gas_transport_capacity_paths(catalog_id,entitlement_id,id)
);
CREATE TABLE trading.gas_transport_capacity_plans(
 catalog_id uuid NOT NULL,entitlement_id uuid NOT NULL,id uuid NOT NULL,business_key text NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_id uuid,version_id uuid NOT NULL,
 admission_policy text NOT NULL CHECK(admission_policy IN('complete_feasible_replacement_v1','explicit_empty_release_v1')),
 evidence text NOT NULL CHECK(evidence=trading.gas_transport_capacity_evidence(evidence)),source_context_id uuid NOT NULL,source_generation integer NOT NULL CHECK(source_generation>=0),assessment jsonb NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,entitlement_id,id),UNIQUE(catalog_id,entitlement_id,business_key,id),
 UNIQUE(catalog_id,entitlement_id,business_key,revision),UNIQUE(catalog_id,entitlement_id,business_key,previous_id),
 CHECK(business_key=trading.gas_transport_capacity_key(business_key)),CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,entitlement_id,version_id) REFERENCES trading.gas_transport_capacity_versions(catalog_id,entitlement_id,id),
 FOREIGN KEY(catalog_id,entitlement_id,business_key,previous_id) REFERENCES trading.gas_transport_capacity_plans(catalog_id,entitlement_id,business_key,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.gas_transport_capacity_claims(
 catalog_id uuid NOT NULL,entitlement_id uuid NOT NULL,plan_id uuid NOT NULL,id uuid NOT NULL,path_id uuid NOT NULL,
 receipt_from ref_data.instant NOT NULL,receipt_to ref_data.instant NOT NULL,receipt_quantity numeric NOT NULL CHECK(receipt_quantity>=0 AND receipt_quantity<1e24 AND scale(receipt_quantity)<=18),
 delivery_from ref_data.instant NOT NULL,delivery_to ref_data.instant NOT NULL,delivery_quantity numeric NOT NULL CHECK(delivery_quantity>=0 AND delivery_quantity<1e24 AND scale(delivery_quantity)<=18),
 PRIMARY KEY(catalog_id,id),CHECK(receipt_from<receipt_to AND delivery_from<delivery_to),
 FOREIGN KEY(catalog_id,entitlement_id,plan_id) REFERENCES trading.gas_transport_capacity_plans(catalog_id,entitlement_id,id),
 FOREIGN KEY(catalog_id,entitlement_id,path_id) REFERENCES trading.gas_transport_capacity_paths(catalog_id,entitlement_id,id)
);
CREATE TABLE trading.gas_transport_capacity_commands(
 catalog_id uuid NOT NULL,operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),entitlement_id uuid NOT NULL,
 request jsonb NOT NULL,fact jsonb NOT NULL,outcome jsonb NOT NULL,authority_until ref_data.instant NOT NULL,source_context_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,operation_id),FOREIGN KEY(catalog_id,entitlement_id) REFERENCES trading.gas_transport_capacity_entitlements(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE INDEX gas_transport_capacity_fact ON trading.gas_transport_capacity_commands(catalog_id,entitlement_id);
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['entitlements','paths','points','versions','limits','plans','claims','commands'] LOOP
  EXECUTE format('ALTER TABLE trading.gas_transport_capacity_%I OWNER TO openetrm_security_owner',name);
  EXECUTE format('ALTER TABLE trading.gas_transport_capacity_%I ENABLE ROW LEVEL SECURITY',name);
  EXECUTE format('ALTER TABLE trading.gas_transport_capacity_%I FORCE ROW LEVEL SECURITY',name);
  EXECUTE format('CREATE POLICY owner_access ON trading.gas_transport_capacity_%I TO openetrm_security_owner USING(true) WITH CHECK(true)',name);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.gas_transport_capacity_%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',name);
 END LOOP;
END$$;

CREATE FUNCTION security.gas_transport_capacity_authority(catalog uuid,kind text) RETURNS timestamptz LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;permission text;write_until timestamptz;BEGIN
 IF kind IS NOT NULL THEN
  permission=CASE kind WHEN 'define' THEN 'ConfigureGasServices' WHEN 'plan' THEN 'RecordGasOperations' ELSE NULL END;
  IF permission IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_denied' USING ERRCODE='42501';END IF;
  write_until=security.authority_until(permission);
 END IF;
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOperations'));
 IF kind IS NOT NULL THEN
  IF permission IS NULL OR security.trading_access(catalog,permission) IS NOT TRUE THEN RAISE EXCEPTION 'gas_transport_capacity_denied' USING ERRCODE='42501';END IF;
  deadline=least(deadline,write_until);
 END IF;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR security.trading_access(catalog,'ReadTrades') IS NOT TRUE
  OR security.trading_access(catalog,'ReadGasOperations') IS NOT TRUE OR deadline IS NULL OR clock_timestamp()>=deadline
 THEN RAISE EXCEPTION 'gas_transport_capacity_denied' USING ERRCODE='42501';END IF;RETURN deadline;
END$$;
CREATE FUNCTION security.require_gas_transport_capacity_commit(catalog uuid,kind text,original_until timestamptz) RETURNS void LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF original_until IS NULL OR clock_timestamp()>=original_until THEN RAISE EXCEPTION 'gas_transport_capacity_denied' USING ERRCODE='42501';END IF;
 PERFORM security.gas_transport_capacity_authority(catalog,kind);
END$$;
CREATE FUNCTION security.gas_transport_capacity_owner(catalog uuid) RETURNS text LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.gas_transport_capacity_authority(catalog,NULL);
 RETURN presentation.preference_owner(security.presentation_actor(),'gas_transport_capacity:'||catalog::text);
END$$;

-- Canonical notation is technical only; no missing commercial term is supplied.
CREATE FUNCTION trading.gas_transport_capacity_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb='{}';item record;value jsonb;BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 FOR item IN SELECT * FROM jsonb_each(body) LOOP
  value=item.value;
  IF item.key IN('businessKey','pathKey','evidence','from','through','receiptFrom','receiptTo','deliveryFrom','deliveryTo','maximumPerHour','receiptQuantity','deliveryQuantity')
   AND jsonb_typeof(value) IS DISTINCT FROM 'string' THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
  IF value<>'null'::jsonb THEN
   IF item.key~'Id$' THEN value=to_jsonb((value#>>'{}')::uuid);
   ELSIF item.key IN('businessKey','pathKey') THEN value=to_jsonb(trading.gas_transport_capacity_key(value#>>'{}'));
   ELSIF item.key='evidence' THEN value=to_jsonb(trading.gas_transport_capacity_evidence(value#>>'{}'));
   ELSIF item.key IN('from','through','receiptFrom','receiptTo','deliveryFrom','deliveryTo') THEN value=to_jsonb(trading.gas_transport_capacity_instant(trading.gas_transport_capacity_at(value#>>'{}')));
   ELSIF item.key IN('maximumPerHour','receiptQuantity','deliveryQuantity') THEN value=to_jsonb(trading.gas_transport_capacity_quantity(value#>>'{}')::text);
   ELSIF item.key IN('agreement','paths','receipts','deliveries','limits','claims') THEN
    IF jsonb_typeof(value) IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
    SELECT coalesce(jsonb_agg(c ORDER BY CASE WHEN item.key='paths' THEN c->>'pathId' WHEN item.key IN('receipts','deliveries') THEN c->>'point'
     WHEN item.key='claims' THEN c->>'pathId' ELSE c->>'from' END COLLATE "C",c->>'claimId' COLLATE "C"),'[]') INTO value
     FROM(SELECT trading.gas_transport_capacity_fact(x) c FROM jsonb_array_elements(value)x)s;
   END IF;
  END IF;
  result=result||jsonb_build_object(item.key,value);
 END LOOP;RETURN result;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'gas_transport_capacity_input';END$$;
CREATE FUNCTION trading.gas_transport_capacity_fact_key(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=body-'expectedStateVersion'-'expectedPreviousId';BEGIN
 IF body?'claims' THEN result=jsonb_set(result,'{claims}',coalesce((SELECT jsonb_agg(c-'claimId' ORDER BY(c-'claimId')::text COLLATE "C") FROM jsonb_array_elements(body->'claims')c),'[]'));END IF;
 RETURN result;
END$$;
CREATE FUNCTION trading.gas_transport_capacity_claim_sort(body jsonb) RETURNS text LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT '{"pathId":'||(body->'pathId')::text||',"receiptFrom":'||(body->'receiptFrom')::text||',"receiptTo":'||(body->'receiptTo')::text||
 ',"receiptQuantity":'||(body->'receiptQuantity')::text||',"deliveryFrom":'||(body->'deliveryFrom')::text||',"deliveryTo":'||(body->'deliveryTo')::text||
 ',"deliveryQuantity":'||(body->'deliveryQuantity')::text||'}'
$$;

CREATE FUNCTION trading.gas_transport_capacity_body(body jsonb,operation uuid) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE kind text=body->>'kind';item jsonb;member jsonb;scope text;wanted jsonb;actual jsonb;field text;BEGIN
 IF kind='define' THEN
  PERFORM trading.gas_transport_capacity_keys(body,ARRAY['kind','entitlementId','expectedStateVersion','expectedPreviousId','evidence','businessKey','referenceCatalogId','referenceRevisionId','bookId',
   'legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision','product','quantity','from','through','implementation','rateBasis','flowShape','agreementBasis','deliveryBasis','agreement','paths','receipts','deliveries']);
  IF body->>'implementation' IS DISTINCT FROM 'gas_transport_gross_uniform_capacity_v1' OR body->>'rateBasis' IS DISTINCT FROM 'native_quantity_per_elapsed_hour_v1'
   OR body->>'flowShape' IS DISTINCT FROM 'uniform_each_explicit_receipt_delivery_interval_v1' OR body->>'agreementBasis' IS DISTINCT FROM 'agreement_receipt_route_gross_receipts_v1'
   OR body->>'deliveryBasis' IS DISTINCT FROM 'delivery_point_gross_deliveries_v1'
   OR(body->>'entitlementId')::uuid IS DISTINCT FROM trading.gas_transport_capacity_id('gas_transport_entitlement_v1:'||(body->>'businessKey'),(body->>'bookId')::uuid)
   OR trading.gas_transport_capacity_at(body->>'from')>=trading.gas_transport_capacity_at(body->>'through')
  THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
  IF jsonb_array_length(body->'paths') NOT BETWEEN 1 AND 128 OR jsonb_array_length(body->'receipts') NOT BETWEEN 1 AND 128 OR jsonb_array_length(body->'deliveries') NOT BETWEEN 1 AND 128
   OR(SELECT count(DISTINCT x->>'pathId') FROM jsonb_array_elements(body->'paths')x)<>jsonb_array_length(body->'paths')
  THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
  FOR item IN SELECT * FROM jsonb_array_elements(body->'paths') LOOP
   PERFORM trading.gas_transport_capacity_keys(item,ARRAY['pathId','pathKey','pipeline','receiptZone','receiptPoint','deliveryZone','deliveryPoint','product','limits']);
   IF(item->>'pathId')::uuid IS DISTINCT FROM trading.gas_transport_capacity_id('gas_transport_path_v1:'||(item->>'pathKey'),(body->>'entitlementId')::uuid)
    OR item->>'product' IS DISTINCT FROM body->>'product' THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
  END LOOP;
  FOREACH scope IN ARRAY ARRAY['receipts','deliveries'] LOOP
   IF(SELECT count(DISTINCT x->>'point') FROM jsonb_array_elements(body->scope)x)<>jsonb_array_length(body->scope)
   THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
   SELECT jsonb_agg(point ORDER BY point COLLATE "C") INTO actual FROM(SELECT x->>'point' point FROM jsonb_array_elements(body->scope)x)s;
   SELECT jsonb_agg(point ORDER BY point COLLATE "C") INTO wanted FROM(SELECT DISTINCT x->>CASE WHEN scope='receipts' THEN 'receiptPoint' ELSE 'deliveryPoint' END point FROM jsonb_array_elements(body->'paths')x)s;
   IF wanted IS DISTINCT FROM actual THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
   FOR item IN SELECT * FROM jsonb_array_elements(body->scope) LOOP PERFORM trading.gas_transport_capacity_keys(item,ARRAY['point','limits']);END LOOP;
  END LOOP;
  FOR item IN SELECT body->'agreement' UNION ALL SELECT x->'limits' FROM jsonb_array_elements(body->'paths')x
    UNION ALL SELECT x->'limits' FROM jsonb_array_elements(body->'receipts')x UNION ALL SELECT x->'limits' FROM jsonb_array_elements(body->'deliveries')x LOOP
   IF jsonb_typeof(item) IS DISTINCT FROM 'array' OR jsonb_array_length(item) NOT BETWEEN 1 AND 256 THEN RAISE EXCEPTION 'gas_transport_capacity_limits';END IF;
   FOR member IN SELECT * FROM jsonb_array_elements(item) LOOP PERFORM trading.gas_transport_capacity_keys(member,ARRAY['from','through','maximumPerHour']);END LOOP;
   IF EXISTS(SELECT 1 FROM(SELECT trading.gas_transport_capacity_at(x->>'from') f,trading.gas_transport_capacity_at(x->>'through') t,
     lag(trading.gas_transport_capacity_at(x->>'through'),1,trading.gas_transport_capacity_at(body->>'from')) OVER(ORDER BY trading.gas_transport_capacity_at(x->>'from')) prior FROM jsonb_array_elements(item)x)s
     WHERE f<>prior OR f>=t OR t>trading.gas_transport_capacity_at(body->>'through'))
    OR(SELECT max(trading.gas_transport_capacity_at(x->>'through')) FROM jsonb_array_elements(item)x)<>trading.gas_transport_capacity_at(body->>'through')
   THEN RAISE EXCEPTION 'gas_transport_capacity_limits';END IF;
  END LOOP;
 ELSIF kind='plan' THEN
  PERFORM trading.gas_transport_capacity_keys(body,ARRAY['kind','entitlementId','expectedStateVersion','expectedPreviousId','evidence','businessKey','versionId','admissionPolicy','claims']);
  IF body->>'admissionPolicy' IS NULL OR body->>'admissionPolicy' NOT IN('complete_feasible_replacement_v1','explicit_empty_release_v1')
   OR body->>'admissionPolicy'='explicit_empty_release_v1' AND(body->>'expectedPreviousId' IS NULL OR jsonb_array_length(body->'claims')<>0)
  THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
  IF jsonb_typeof(body->'claims') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'claims')>1024
   OR(SELECT count(DISTINCT x->>'claimId') FROM jsonb_array_elements(body->'claims')x)<>jsonb_array_length(body->'claims')
  THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
  FOR item IN SELECT * FROM jsonb_array_elements(body->'claims') LOOP
   PERFORM trading.gas_transport_capacity_keys(item,ARRAY['claimId','pathId','receiptFrom','receiptTo','receiptQuantity','deliveryFrom','deliveryTo','deliveryQuantity']);
   IF trading.gas_transport_capacity_at(item->>'receiptFrom')>=trading.gas_transport_capacity_at(item->>'receiptTo')
    OR trading.gas_transport_capacity_at(item->>'deliveryFrom')>=trading.gas_transport_capacity_at(item->>'deliveryTo')
   THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
  END LOOP;
  IF EXISTS(SELECT 1 FROM(
   SELECT trading.gas_transport_capacity_claim_sort(x) semantic,array_agg((x->>'claimId')::uuid ORDER BY(x->>'claimId')::uuid) actual,
    array_agg(trading.gas_transport_capacity_id('gas_transport_claim_v1',operation,ordinal::integer) ORDER BY trading.gas_transport_capacity_id('gas_transport_claim_v1',operation,ordinal::integer)) wanted
   FROM(SELECT x,row_number() OVER(ORDER BY trading.gas_transport_capacity_claim_sort(x) COLLATE "C",(x->>'claimId')::uuid)-1 ordinal FROM jsonb_array_elements(body->'claims')x)s
   GROUP BY trading.gas_transport_capacity_claim_sort(x))groups WHERE groups.actual<>groups.wanted)
  THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 ELSE RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 IF body->>'entitlementId' IS NULL OR(body->>'entitlementId')::uuid='00000000-0000-0000-0000-000000000000'::uuid
  OR jsonb_typeof(body->'expectedStateVersion') IS DISTINCT FROM 'number' OR body->>'expectedStateVersion'!~'^(0|[1-9][0-9]*)$'
  OR body->>'evidence' IS NULL OR length(body->>'evidence') NOT BETWEEN 1 AND 2000 OR jsonb_typeof(body->'evidence') IS DISTINCT FROM 'string'
 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 IF kind='define' THEN
  FOREACH field IN ARRAY ARRAY['product','quantity'] LOOP
   IF jsonb_typeof(body->field) IS DISTINCT FROM 'string' OR body->>field!~'^[a-z][a-z0-9_.-]{0,63}$' THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
  END LOOP;
  FOR item IN SELECT * FROM jsonb_array_elements(body->'paths') LOOP
   FOREACH field IN ARRAY ARRAY['pipeline','receiptZone','receiptPoint','deliveryZone','deliveryPoint','product'] LOOP
    IF jsonb_typeof(item->field) IS DISTINCT FROM 'string' OR item->>field!~'^[a-z][a-z0-9_.-]{0,63}$' THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
   END LOOP;
  END LOOP;
 END IF;
END$$;

-- Explicit certificate reads keep knowledge selection separate from business time.
CREATE FUNCTION trading.gas_transport_capacity_known(catalog uuid,publication integer,context uuid) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,publication)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND publication<=x.known_through AND k.known_from<=t.known_at)
$$;
CREATE FUNCTION trading.gas_transport_capacity_head(catalog uuid,entitlement uuid,context uuid DEFAULT NULL,through_publication integer DEFAULT 2147483647) RETURNS integer
 LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT coalesce(max(publication_order),0) FROM(
  SELECT publication_order FROM trading.gas_transport_capacity_versions WHERE(catalog_id,entitlement_id)=(catalog,entitlement)
  UNION ALL SELECT publication_order FROM trading.gas_transport_capacity_plans WHERE(catalog_id,entitlement_id)=(catalog,entitlement)
 )s WHERE publication_order<=through_publication AND(context IS NULL OR trading.gas_transport_capacity_known(catalog,publication_order,context))
$$;
CREATE FUNCTION trading.gas_transport_capacity_cut(catalog uuid,context uuid) RETURNS timestamptz LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE result timestamptz;BEGIN
 SELECT((t.effective_on+1)::timestamp AT TIME ZONE 'UTC')-INTERVAL '1 microsecond' INTO result FROM market_data.temporal_contexts t
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(catalog,context);
 IF result IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_context';END IF;RETURN result;
END$$;
CREATE FUNCTION trading.gas_transport_capacity_bound(catalog uuid) RETURNS security.request_contexts LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE result security.request_contexts;BEGIN
 SELECT * INTO result FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
  AND q.runtime_login=session_user AND q.expires_at>clock_timestamp() AND q.market_catalog_id=catalog;
 IF result.market_context_id IS NULL OR result.trading_as_of IS DISTINCT FROM trading.gas_transport_capacity_cut(catalog,result.market_context_id)
 THEN RAISE EXCEPTION 'gas_transport_capacity_context';END IF;
 PERFORM security.require_trading_history(catalog,result.market_context_id);RETURN result;
END$$;
CREATE FUNCTION trading.require_gas_transport_capacity_reference(catalog uuid,entitlement uuid) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE scope trading.gas_transport_capacity_entitlements;BEGIN
 SELECT * INTO scope FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id)=(catalog,entitlement);
 IF scope.id IS NULL OR security.reference_access(scope.reference_catalog_id,'ReadMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'gas_transport_capacity_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION trading.require_gas_transport_capacity_source(catalog uuid,body jsonb,context uuid,effective timestamptz,through_publication integer DEFAULT 2147483647) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE entitlement uuid=(body->>'entitlementId')::uuid;at timestamptz;scope trading.gas_transport_capacity_entitlements;selected trading.gas_transport_capacity_versions;BEGIN
 IF security.current_write_context() IS NOT TRUE OR context IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_context';END IF;
 at=trading.gas_transport_capacity_cut(catalog,context);
 IF (effective AT TIME ZONE 'UTC')::date<>(at AT TIME ZONE 'UTC')::date THEN RAISE EXCEPTION 'gas_transport_capacity_context';END IF;
 BEGIN
  PERFORM security.use_market_context(catalog,context);PERFORM security.use_trading_context(catalog,at);
  IF body->>'kind'='define' THEN
   scope.reference_catalog_id=(body->>'referenceCatalogId')::uuid;scope.reference_revision_id=(body->>'referenceRevisionId')::uuid;
   scope.book_id=(body->>'bookId')::uuid;scope.legal_entity_id=(body->>'legalEntityId')::uuid;scope.legal_entity_revision=(body->>'legalEntityRevision')::integer;
   scope.counterparty_id=(body->>'counterpartyId')::uuid;scope.counterparty_revision=(body->>'counterpartyRevision')::integer;
  ELSE SELECT * INTO scope FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id)=(catalog,entitlement);END IF;
  IF scope.book_id IS NULL OR security.reference_version_visible(scope.reference_catalog_id,scope.reference_revision_id) IS NOT TRUE
   OR NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
    WHERE(b.catalog_id,b.id)=(catalog,scope.book_id) AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
   OR NOT EXISTS(SELECT 1 FROM trading.legal_entities e WHERE(e.catalog_id,e.id,e.revision)=(catalog,scope.legal_entity_id,scope.legal_entity_revision) AND security.trading_visible(catalog,e.publication_order,e.effective_at))
   OR NOT EXISTS(SELECT 1 FROM trading.counterparties e WHERE(e.catalog_id,e.id,e.revision)=(catalog,scope.counterparty_id,scope.counterparty_revision) AND security.trading_visible(catalog,e.publication_order,e.effective_at))
  THEN RAISE EXCEPTION 'gas_transport_capacity_source';END IF;
  IF trading.gas_transport_capacity_head(catalog,entitlement,NULL,through_publication)<>trading.gas_transport_capacity_head(catalog,entitlement,context,through_publication)
   OR(body->>'expectedStateVersion')::integer<>trading.gas_transport_capacity_head(catalog,entitlement,NULL,through_publication)
  THEN RAISE EXCEPTION 'gas_transport_capacity_stale';END IF;
  SELECT * INTO selected FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id)=(catalog,entitlement)
   AND v.publication_order<=through_publication ORDER BY revision DESC LIMIT 1;
  IF selected.effective_at>at OR EXISTS(SELECT 1 FROM(SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p
    WHERE(p.catalog_id,p.entitlement_id)=(catalog,entitlement) AND p.publication_order<=through_publication ORDER BY business_key,revision DESC)heads WHERE effective_at>at)
  THEN RAISE EXCEPTION 'gas_transport_capacity_future_source';END IF;
  IF body->>'kind'='plan' THEN
   IF selected.id IS DISTINCT FROM(body->>'versionId')::uuid THEN RAISE EXCEPTION 'gas_transport_capacity_stale';END IF;
  END IF;
  RAISE EXCEPTION USING ERRCODE='PZ342',MESSAGE='Transport source certified';
 EXCEPTION WHEN SQLSTATE 'PZ342' THEN NULL;END;
END$$;

-- Integer fractions are reduced before comparison. This uses the same explicit
-- 4096-bit result envelope as the managed ExactQuantity consumer, never rounding.
CREATE FUNCTION trading.gas_transport_capacity_ratio(n numeric,d numeric) RETURNS numeric[] LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE divisor numeric;BEGIN
 IF n IS NULL OR d IS NULL OR n<>trunc(n) OR d<>trunc(d) OR d<=0 OR n<0 THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
 divisor=gcd(n,d);n=div(n,divisor);d=div(d,divisor);
 IF n>=power(2::numeric,4096) OR d>=power(2::numeric,4096) THEN RAISE EXCEPTION 'gas_transport_capacity_numeric_range';END IF;RETURN ARRAY[n,d];
END$$;
CREATE FUNCTION trading.gas_transport_capacity_rate(q numeric,from_at timestamptz,through_at timestamptz) RETURNS numeric[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trading.gas_transport_capacity_ratio(q*power(10::numeric,scale(q))*3600000000,extract(epoch FROM(through_at-from_at))*1000000*power(10::numeric,scale(q)))
$$;
CREATE FUNCTION trading.gas_transport_capacity_add(a numeric[],b numeric[]) RETURNS numeric[] LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE n numeric;d numeric;divisor numeric;BEGIN
 n=a[1]*b[2]+b[1]*a[2];d=a[2]*b[2];
 IF n IS NULL OR d IS NULL OR n<>trunc(n) OR d<>trunc(d) OR n<0 OR d<=0 THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
 divisor=gcd(n,d);RETURN ARRAY[div(n,divisor),div(d,divisor)];
END$$;
CREATE FUNCTION trading.gas_transport_capacity_ratio_read(value jsonb) RETURNS numeric[] LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 PERFORM trading.gas_transport_capacity_keys(value,ARRAY['n','d']);
 IF jsonb_typeof(value->'n') IS DISTINCT FROM 'string' OR jsonb_typeof(value->'d') IS DISTINCT FROM 'string'
  OR value->>'n'!~'^(0|[1-9][0-9]*)$' OR value->>'d'!~'^[1-9][0-9]*$' OR length(value->>'n')>1234 OR length(value->>'d')>1234
 THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
 RETURN trading.gas_transport_capacity_ratio((value->>'n')::numeric,(value->>'d')::numeric);
END$$;

-- Rebuild complete active membership from typed plan heads, not a caller list.
CREATE FUNCTION trading.verify_gas_transport_capacity_plan(catalog uuid,selected_plan uuid) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE plan trading.gas_transport_capacity_plans;scope trading.gas_transport_capacity_entitlements;item record;part record;active record;proof jsonb;
 rate numeric[];total numeric[];maximum numeric[];seen integer=0;wanted integer;ids jsonb;BEGIN
 SELECT * INTO STRICT plan FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.id)=(catalog,selected_plan);
 SELECT * INTO STRICT scope FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id)=(catalog,plan.entitlement_id);
 PERFORM trading.gas_transport_capacity_keys(plan.assessment,ARRAY['implementation','versionId','admissionPolicy','intervals']);
 IF plan.assessment->>'implementation' IS DISTINCT FROM 'gas_transport_capacity_witness_v1' OR(plan.assessment->>'versionId')::uuid IS DISTINCT FROM plan.version_id
  OR plan.assessment->>'admissionPolicy' IS DISTINCT FROM plan.admission_policy
  OR jsonb_typeof(plan.assessment->'intervals') IS DISTINCT FROM 'array' OR pg_column_size(plan.assessment)>8388608
 THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
 IF(SELECT count(*) FROM trading.gas_transport_capacity_claims c JOIN(SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p
  WHERE(p.catalog_id,p.entitlement_id)=(catalog,plan.entitlement_id) AND p.publication_order<=plan.publication_order ORDER BY business_key,revision DESC)heads ON heads.id=c.plan_id AND heads.catalog_id=c.catalog_id)>1024
 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 IF EXISTS(SELECT 1 FROM trading.gas_transport_capacity_claims c WHERE(c.catalog_id,c.plan_id)=(catalog,selected_plan)
  AND(c.receipt_from<scope.from_at OR c.delivery_from<scope.from_at OR c.receipt_to>scope.through_at OR c.delivery_to>scope.through_at))
 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 FOR item IN SELECT DISTINCT axis,point_code,path_id FROM trading.gas_transport_capacity_limits l WHERE(l.catalog_id,l.version_id)=(catalog,plan.version_id) LOOP
  FOR part IN WITH heads AS(SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p
    WHERE(p.catalog_id,p.entitlement_id)=(catalog,plan.entitlement_id) AND p.publication_order<=plan.publication_order ORDER BY business_key,revision DESC),
   stages AS(SELECT c.id,CASE WHEN item.axis='delivery' THEN c.delivery_from ELSE c.receipt_from END f,
    CASE WHEN item.axis='delivery' THEN c.delivery_to ELSE c.receipt_to END t FROM trading.gas_transport_capacity_claims c JOIN heads ON(heads.catalog_id,heads.id)=(c.catalog_id,c.plan_id)
    JOIN trading.gas_transport_capacity_paths p ON(p.catalog_id,p.entitlement_id,p.id)=(c.catalog_id,c.entitlement_id,c.path_id)
    WHERE item.axis='agreement' OR item.axis='receipt' AND p.receipt_point_code=item.point_code OR item.axis='delivery' AND p.delivery_point_code=item.point_code OR item.axis='route' AND c.path_id=item.path_id),
   bounds AS(SELECT from_at at FROM trading.gas_transport_capacity_limits l WHERE(l.catalog_id,l.version_id,l.axis,l.point_code,l.path_id) IS NOT DISTINCT FROM(catalog,plan.version_id,item.axis,item.point_code,item.path_id)
    UNION SELECT through_at FROM trading.gas_transport_capacity_limits l WHERE(l.catalog_id,l.version_id,l.axis,l.point_code,l.path_id) IS NOT DISTINCT FROM(catalog,plan.version_id,item.axis,item.point_code,item.path_id)
    UNION SELECT f FROM stages UNION SELECT t FROM stages)
   SELECT at f,lead(at) OVER(ORDER BY at)t FROM bounds ORDER BY at LOOP
   IF part.t IS NULL THEN CONTINUE;END IF;seen=seen+1;total=ARRAY[0::numeric,1::numeric];ids='[]';
   FOR active IN SELECT c.id,CASE WHEN item.axis='delivery' THEN c.delivery_from ELSE c.receipt_from END f,
     CASE WHEN item.axis='delivery' THEN c.delivery_to ELSE c.receipt_to END t,CASE WHEN item.axis='delivery' THEN c.delivery_quantity ELSE c.receipt_quantity END q
    FROM trading.gas_transport_capacity_claims c JOIN(SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p
     WHERE(p.catalog_id,p.entitlement_id)=(catalog,plan.entitlement_id) AND p.publication_order<=plan.publication_order ORDER BY business_key,revision DESC)heads
     ON(heads.catalog_id,heads.id)=(c.catalog_id,c.plan_id) JOIN trading.gas_transport_capacity_paths p ON(p.catalog_id,p.entitlement_id,p.id)=(c.catalog_id,c.entitlement_id,c.path_id)
    WHERE(item.axis='agreement' OR item.axis='receipt' AND p.receipt_point_code=item.point_code OR item.axis='delivery' AND p.delivery_point_code=item.point_code OR item.axis='route' AND c.path_id=item.path_id)
     AND CASE WHEN item.axis='delivery' THEN c.delivery_from ELSE c.receipt_from END<=part.f AND CASE WHEN item.axis='delivery' THEN c.delivery_to ELSE c.receipt_to END>=part.t ORDER BY c.id LOOP
    rate=trading.gas_transport_capacity_rate(active.q,active.f,active.t);
    total=trading.gas_transport_capacity_add(total,rate);ids=ids||to_jsonb(active.id);
   END LOOP;
   total=trading.gas_transport_capacity_ratio(total[1],total[2]);
   SELECT trading.gas_transport_capacity_rate(l.maximum_per_hour,part.f,part.f+INTERVAL '1 hour') INTO maximum FROM trading.gas_transport_capacity_limits l
    WHERE(l.catalog_id,l.version_id,l.axis,l.point_code,l.path_id) IS NOT DISTINCT FROM(catalog,plan.version_id,item.axis,item.point_code,item.path_id)
     AND l.from_at<=part.f AND l.through_at>=part.t;
   IF maximum IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_limits';END IF;
   SELECT min(ordinality)::integer INTO wanted FROM jsonb_array_elements(plan.assessment->'intervals') WITH ORDINALITY x(value,ordinality)
    WHERE value->>'axis'=item.axis AND value->>'point' IS NOT DISTINCT FROM item.point_code AND(value->>'pathId')::uuid IS NOT DISTINCT FROM item.path_id
     AND trading.gas_transport_capacity_at(value->>'from')=part.f AND trading.gas_transport_capacity_at(value->>'through')=part.t;
   -- Count separately: duplicated interval identities are never accepted.
   IF(SELECT count(*) FROM jsonb_array_elements(plan.assessment->'intervals')value WHERE value->>'axis'=item.axis
    AND value->>'point' IS NOT DISTINCT FROM item.point_code AND(value->>'pathId')::uuid IS NOT DISTINCT FROM item.path_id
    AND trading.gas_transport_capacity_at(value->>'from')=part.f AND trading.gas_transport_capacity_at(value->>'through')=part.t)<>1
   THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
   proof=(plan.assessment->'intervals')->(wanted-1);
   PERFORM trading.gas_transport_capacity_keys(proof,ARRAY['axis','point','pathId','from','through','claims','used','maximum','withinLimit']);
   IF trading.gas_transport_capacity_ratio_read(proof->'used')<>total OR trading.gas_transport_capacity_ratio_read(proof->'maximum')<>maximum
    OR proof->'claims' IS DISTINCT FROM ids OR proof->'withinLimit' IS DISTINCT FROM to_jsonb(total[1]*maximum[2]<=maximum[1]*total[2])
   THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
   IF total[1]*maximum[2]>maximum[1]*total[2] AND plan.admission_policy='complete_feasible_replacement_v1' THEN RAISE EXCEPTION 'gas_transport_capacity_exceeded';END IF;
  END LOOP;
 END LOOP;
 IF jsonb_array_length(plan.assessment->'intervals')<>seen THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
END$$;

CREATE FUNCTION trading.gas_transport_capacity_limit_rows(catalog uuid,version uuid,selected_axis text,point text DEFAULT NULL,path uuid DEFAULT NULL) RETURNS jsonb LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT coalesce(jsonb_agg(jsonb_build_object('from',trading.gas_transport_capacity_instant(from_at),'through',trading.gas_transport_capacity_instant(through_at),
  'maximumPerHour',trim_scale(maximum_per_hour)::text) ORDER BY ordinal),'[]') FROM trading.gas_transport_capacity_limits l
 WHERE(l.catalog_id,l.version_id,l.axis,l.point_code,l.path_id) IS NOT DISTINCT FROM(catalog,version,selected_axis,point,path)
$$;
CREATE FUNCTION trading.gas_transport_capacity_recorded_body(catalog uuid,entitlement uuid,kind text,selected uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 IF kind='define' THEN
  SELECT jsonb_build_object('kind','define','entitlementId',h.id,'businessKey',h.business_key,'expectedStateVersion',v.source_generation,'expectedPreviousId',v.previous_id,
   'referenceCatalogId',h.reference_catalog_id,'referenceRevisionId',h.reference_revision_id,'bookId',h.book_id,
   'legalEntityId',h.legal_entity_id,'legalEntityRevision',h.legal_entity_revision,'counterpartyId',h.counterparty_id,'counterpartyRevision',h.counterparty_revision,
   'product',h.product_code,'quantity',h.quantity_code,'from',trading.gas_transport_capacity_instant(h.from_at),'through',trading.gas_transport_capacity_instant(h.through_at),
   'implementation',v.implementation,'rateBasis',v.rate_basis,'flowShape',v.flow_shape,'agreementBasis',v.agreement_basis,'deliveryBasis',v.delivery_basis,'evidence',v.evidence,
   'agreement',trading.gas_transport_capacity_limit_rows(catalog,v.id,'agreement'),
   'paths',(SELECT coalesce(jsonb_agg(jsonb_build_object('pathId',p.id,'pathKey',p.path_key,'pipeline',p.pipeline_code,'receiptZone',p.receipt_zone_code,'receiptPoint',p.receipt_point_code,
    'deliveryZone',p.delivery_zone_code,'deliveryPoint',p.delivery_point_code,'product',p.product_code,'limits',trading.gas_transport_capacity_limit_rows(catalog,v.id,'route',NULL,p.id)) ORDER BY p.id),'[]')
    FROM trading.gas_transport_capacity_paths p WHERE(p.catalog_id,p.entitlement_id)=(catalog,entitlement)),
   'receipts',(SELECT coalesce(jsonb_agg(jsonb_build_object('point',p.point_code,'limits',trading.gas_transport_capacity_limit_rows(catalog,v.id,'receipt',p.point_code)) ORDER BY p.point_code COLLATE "C"),'[]')
    FROM trading.gas_transport_capacity_points p WHERE(p.catalog_id,p.entitlement_id,p.axis)=(catalog,entitlement,'receipt')),
   'deliveries',(SELECT coalesce(jsonb_agg(jsonb_build_object('point',p.point_code,'limits',trading.gas_transport_capacity_limit_rows(catalog,v.id,'delivery',p.point_code)) ORDER BY p.point_code COLLATE "C"),'[]')
    FROM trading.gas_transport_capacity_points p WHERE(p.catalog_id,p.entitlement_id,p.axis)=(catalog,entitlement,'delivery')))
  INTO result FROM trading.gas_transport_capacity_entitlements h JOIN trading.gas_transport_capacity_versions v ON(v.catalog_id,v.entitlement_id)=(h.catalog_id,h.id)
   WHERE(h.catalog_id,h.id,v.id)=(catalog,entitlement,selected);
 ELSIF kind='plan' THEN
  SELECT jsonb_build_object('kind','plan','entitlementId',p.entitlement_id,'businessKey',p.business_key,'expectedStateVersion',p.source_generation,'expectedPreviousId',p.previous_id,
   'versionId',p.version_id,'admissionPolicy',p.admission_policy,'evidence',p.evidence,'claims',(SELECT coalesce(jsonb_agg(jsonb_build_object('claimId',c.id,'pathId',c.path_id,
    'receiptFrom',trading.gas_transport_capacity_instant(c.receipt_from),'receiptTo',trading.gas_transport_capacity_instant(c.receipt_to),'receiptQuantity',trim_scale(c.receipt_quantity)::text,
    'deliveryFrom',trading.gas_transport_capacity_instant(c.delivery_from),'deliveryTo',trading.gas_transport_capacity_instant(c.delivery_to),'deliveryQuantity',trim_scale(c.delivery_quantity)::text)
    ORDER BY c.path_id,c.id),'[]') FROM trading.gas_transport_capacity_claims c WHERE(c.catalog_id,c.plan_id)=(catalog,selected))) INTO result
   FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.entitlement_id,p.id)=(catalog,entitlement,selected);
 END IF;
 IF result IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;RETURN result;
END$$;

CREATE FUNCTION security.gas_transport_capacity_status(catalog uuid,operation uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE deadline timestamptz;configure_until timestamptz;record_until timestamptz;prior trading.gas_transport_capacity_commands;actor uuid;selected_until timestamptz;BEGIN
 configure_until=security.authority_until('ConfigureGasServices');record_until=security.authority_until('RecordGasOperations');
 deadline=security.gas_transport_capacity_authority(catalog,NULL);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF prior.operation_id IS NULL THEN PERFORM security.require_gas_transport_capacity_commit(catalog,NULL,deadline);RETURN jsonb_build_object('found',false,'authorityUntil',deadline);END IF;
 IF prior.actor_id<>actor THEN RAISE EXCEPTION 'gas_transport_capacity_denied' USING ERRCODE='42501';END IF;
 PERFORM security.gas_transport_capacity_authority(catalog,prior.request#>>'{body,kind}');
 PERFORM trading.require_gas_transport_capacity_reference(catalog,prior.entitlement_id);
 selected_until=CASE WHEN prior.request#>>'{body,kind}'='define' THEN configure_until ELSE record_until END;
 IF selected_until IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_denied' USING ERRCODE='42501';END IF;
 PERFORM security.require_gas_transport_capacity_commit(catalog,prior.request#>>'{body,kind}',least(deadline,selected_until));
 RETURN jsonb_build_object('found',true,'authorityUntil',least(deadline,selected_until),'operationId',operation,'entitlementId',prior.entitlement_id,'kind',prior.request#>>'{body,kind}',
  'command',prior.request->'body','effectiveAt',prior.request->'effectiveAt','outcome',prior.outcome);
END$$;

CREATE FUNCTION security.read_gas_transport_capacity(catalog uuid,entitlement uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE deadline timestamptz;bound security.request_contexts;scope trading.gas_transport_capacity_entitlements;version trading.gas_transport_capacity_versions;
 result jsonb;body jsonb;plans jsonb;claims jsonb;future boolean;known timestamptz;BEGIN
 deadline=security.gas_transport_capacity_authority(catalog,NULL);bound=trading.gas_transport_capacity_bound(catalog);
 SELECT * INTO scope FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id)=(catalog,entitlement)
  AND trading.gas_owned_visible(catalog,h.publication_order,TIMESTAMPTZ '0001-01-01Z',bound.market_context_id,bound.trading_as_of);
 IF scope.id IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_source';END IF;
 PERFORM trading.require_gas_transport_capacity_reference(catalog,entitlement);
 IF security.reference_version_visible(scope.reference_catalog_id,scope.reference_revision_id) IS NOT TRUE
  OR NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id)
   WHERE(b.catalog_id,b.id)=(catalog,scope.book_id) AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
  OR NOT EXISTS(SELECT 1 FROM trading.legal_entities e WHERE(e.catalog_id,e.id,e.revision)=(catalog,scope.legal_entity_id,scope.legal_entity_revision) AND security.trading_visible(catalog,e.publication_order,e.effective_at))
  OR NOT EXISTS(SELECT 1 FROM trading.counterparties e WHERE(e.catalog_id,e.id,e.revision)=(catalog,scope.counterparty_id,scope.counterparty_revision) AND security.trading_visible(catalog,e.publication_order,e.effective_at))
 THEN RAISE EXCEPTION 'gas_transport_capacity_source';END IF;
 SELECT * INTO version FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id)=(catalog,entitlement)
  AND trading.gas_owned_visible(catalog,v.publication_order,TIMESTAMPTZ '0001-01-01Z',bound.market_context_id,bound.trading_as_of) ORDER BY revision DESC LIMIT 1;
 IF version.id IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_source';END IF;
 SELECT coalesce(jsonb_agg(to_jsonb(heads) ORDER BY business_key COLLATE "C"),'[]'),coalesce(bool_or(effective_at>bound.trading_as_of),false) INTO plans,future FROM(
  SELECT DISTINCT ON(business_key)p.* FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.entitlement_id)=(catalog,entitlement)
   AND trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01Z',bound.market_context_id,bound.trading_as_of) ORDER BY business_key,revision DESC)heads;
 SELECT coalesce(jsonb_agg(to_jsonb(c)||jsonb_build_object('receipt_quantity',trim_scale(c.receipt_quantity)::text,'delivery_quantity',trim_scale(c.delivery_quantity)::text) ORDER BY c.plan_id,c.path_id,c.id),'[]') INTO claims
  FROM trading.gas_transport_capacity_claims c WHERE c.catalog_id=catalog AND EXISTS(SELECT 1 FROM jsonb_array_elements(plans)p
   WHERE(p->>'id')::uuid=c.plan_id AND(p->>'effective_at')::timestamptz<=bound.trading_as_of);
 body=trading.gas_transport_capacity_recorded_body(catalog,entitlement,'define',version.id);
 SELECT known_at INTO known FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,bound.market_context_id);
 result=jsonb_build_object('entitlement',to_jsonb(scope),'version',CASE WHEN version.effective_at<=bound.trading_as_of THEN
  (to_jsonb(scope)-'id'-'actor_id'-'publication_order'-'effective_at')||to_jsonb(version) ELSE 'null'::jsonb END,
  'paths',(SELECT jsonb_agg(to_jsonb(p)||jsonb_build_object('limits',(SELECT jsonb_agg(jsonb_build_object('from',x->'from','through',x->'through','maximum_per_hour',x->'maximumPerHour')) FROM jsonb_array_elements(path->'limits')x)) ORDER BY p.id)
   FROM trading.gas_transport_capacity_paths p JOIN jsonb_array_elements(body->'paths')path ON(path->>'pathId')::uuid=p.id WHERE(p.catalog_id,p.entitlement_id)=(catalog,entitlement)),
  'agreement',(SELECT jsonb_agg(jsonb_build_object('from',x->'from','through',x->'through','maximum_per_hour',x->'maximumPerHour')) FROM jsonb_array_elements(body->'agreement')x),
  'receipts',(SELECT jsonb_agg(jsonb_build_object('point_code',p->'point','limits',(SELECT jsonb_agg(jsonb_build_object('from',x->'from','through',x->'through','maximum_per_hour',x->'maximumPerHour')) FROM jsonb_array_elements(p->'limits')x))) FROM jsonb_array_elements(body->'receipts')p),
  'deliveries',(SELECT jsonb_agg(jsonb_build_object('point_code',p->'point','limits',(SELECT jsonb_agg(jsonb_build_object('from',x->'from','through',x->'through','maximum_per_hour',x->'maximumPerHour')) FROM jsonb_array_elements(p->'limits')x))) FROM jsonb_array_elements(body->'deliveries')p),
  'plans',plans,'claims',claims,'stateVersion',trading.gas_transport_capacity_head(catalog,entitlement,bound.market_context_id),
  'contextId',bound.market_context_id,'asOf',bound.trading_as_of,'knownAt',known,'hasFutureHeads',future OR version.effective_at>bound.trading_as_of);
 PERFORM security.require_gas_transport_capacity_commit(catalog,NULL,deadline);PERFORM trading.require_gas_transport_capacity_reference(catalog,entitlement);RETURN result;
END$$;
CREATE FUNCTION security.gas_transport_capacity_directory(catalog uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;bound security.request_contexts;result jsonb;BEGIN
 deadline=security.gas_transport_capacity_authority(catalog,NULL);bound=trading.gas_transport_capacity_bound(catalog);
 SELECT coalesce(jsonb_agg(to_jsonb(listing) ORDER BY label COLLATE "C",id),'[]') INTO result FROM(
  SELECT h.id,h.business_key label,v.revision FROM trading.gas_transport_capacity_entitlements h CROSS JOIN LATERAL(
   SELECT revision FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id)=(h.catalog_id,h.id)
    AND security.trading_visible(catalog,v.publication_order,TIMESTAMPTZ '0001-01-01Z') ORDER BY revision DESC LIMIT 1)v
  WHERE h.catalog_id=catalog AND security.trading_visible(catalog,h.publication_order,TIMESTAMPTZ '0001-01-01Z')
   AND security.reference_access(h.reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(h.reference_catalog_id,h.reference_revision_id)
   AND EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE(b.catalog_id,b.id)=(catalog,h.book_id)
    AND security.trading_visible(catalog,b.publication_order,b.effective_at) AND security.trading_visible(catalog,p.publication_order,p.effective_at))
  ORDER BY h.business_key COLLATE "C",h.id LIMIT 257)listing;
 IF jsonb_array_length(result)>256 THEN RAISE EXCEPTION 'gas_transport_capacity_limit';END IF;
 PERFORM security.require_gas_transport_capacity_commit(catalog,NULL,deadline);RETURN result;
END$$;

-- Every typed parent and member is sealed against one exact command. A member
-- appended after eager validation must still match that immutable request.
CREATE FUNCTION trading.require_gas_transport_capacity_record(catalog uuid,entitlement uuid,kind text,selected uuid,numerical boolean) RETURNS void LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE command trading.gas_transport_capacity_commands;recorded jsonb;publication integer;actor uuid;effective timestamptz;previous uuid;record_revision integer;context uuid;generation integer;expected jsonb;BEGIN
 recorded=trading.gas_transport_capacity_recorded_body(catalog,entitlement,kind,selected);PERFORM trading.gas_transport_capacity_body(recorded,selected);
 IF kind='define' THEN
  SELECT v.publication_order,v.actor_id,v.effective_at,v.previous_id,v.revision,v.source_context_id,v.source_generation INTO publication,actor,effective,previous,record_revision,context,generation
   FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id,v.id)=(catalog,entitlement,selected);
 ELSE
  SELECT v.publication_order,v.actor_id,v.effective_at,v.previous_id,v.revision,v.source_context_id,v.source_generation INTO publication,actor,effective,previous,record_revision,context,generation
   FROM trading.gas_transport_capacity_plans v WHERE(v.catalog_id,v.entitlement_id,v.id)=(catalog,entitlement,selected);
 END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(catalog,publication,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
 SELECT * INTO command FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.operation_id,c.entitlement_id,c.actor_id)=(catalog,selected,entitlement,actor) AND c.outcome->>'changed'='true';
 IF command.operation_id IS NULL OR command.request->'body' IS DISTINCT FROM recorded OR command.source_context_id IS DISTINCT FROM context
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.actor_id=actor AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
 IF kind='define' THEN
  IF previous IS DISTINCT FROM(SELECT v.id FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id,v.revision)=(catalog,entitlement,record_revision-1))
   OR EXISTS(SELECT 1 FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id,v.revision)=(catalog,entitlement,record_revision-1) AND v.effective_at>effective)
  THEN RAISE EXCEPTION 'gas_transport_capacity_stale';END IF;
  IF record_revision=1 AND NOT EXISTS(SELECT 1 FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id,h.actor_id,h.publication_order,h.effective_at)=(catalog,entitlement,actor,publication,effective))
  THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
 ELSE
  IF previous IS DISTINCT FROM(SELECT v.id FROM trading.gas_transport_capacity_plans v WHERE(v.catalog_id,v.entitlement_id,v.business_key,v.revision)=(catalog,entitlement,recorded->>'businessKey',record_revision-1))
   OR EXISTS(SELECT 1 FROM trading.gas_transport_capacity_plans v WHERE(v.catalog_id,v.entitlement_id,v.business_key,v.revision)=(catalog,entitlement,recorded->>'businessKey',record_revision-1) AND v.effective_at>effective)
  THEN RAISE EXCEPTION 'gas_transport_capacity_stale';END IF;
  IF numerical THEN PERFORM trading.verify_gas_transport_capacity_plan(catalog,selected);END IF;
 END IF;
 IF numerical THEN
  IF(SELECT count(*) FROM(
   SELECT id FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.publication_order)=(catalog,publication)
   UNION ALL SELECT id FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.publication_order)=(catalog,publication))headers)<>1
  THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
  IF previous IS NOT NULL AND NOT EXISTS(SELECT 1 FROM(
   SELECT publication_order FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.id)=(catalog,previous) AND kind='define'
   UNION ALL SELECT publication_order FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.id)=(catalog,previous) AND kind='plan')predecessor
   WHERE predecessor.publication_order<publication AND trading.gas_transport_capacity_known(catalog,predecessor.publication_order,context))
  THEN RAISE EXCEPTION 'gas_transport_capacity_context';END IF;
  IF trading.gas_transport_capacity_known(catalog,publication,context) THEN RAISE EXCEPTION 'gas_transport_capacity_context';END IF;
  PERFORM trading.require_gas_transport_capacity_source(catalog,recorded,context,effective,publication-1);
 END IF;
END$$;
CREATE FUNCTION trading.guard_gas_transport_capacity() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE kind text;selected uuid;entitlement uuid;row_value jsonb=to_jsonb(NEW);BEGIN
 entitlement=(row_value->>CASE WHEN TG_TABLE_NAME='gas_transport_capacity_entitlements' THEN 'id' ELSE 'entitlement_id' END)::uuid;
 IF TG_TABLE_NAME IN('gas_transport_capacity_plans','gas_transport_capacity_claims') THEN
  kind='plan';selected=(row_value->>CASE WHEN TG_TABLE_NAME='gas_transport_capacity_plans' THEN 'id' ELSE 'plan_id' END)::uuid;
 ELSE
  kind='define';
  IF TG_TABLE_NAME='gas_transport_capacity_versions' THEN selected=(row_value->>'id')::uuid;
  ELSIF TG_TABLE_NAME='gas_transport_capacity_limits' THEN selected=(row_value->>'version_id')::uuid;
  ELSE SELECT id INTO selected FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id,v.revision)=(NEW.catalog_id,entitlement,1);END IF;
 END IF;
 PERFORM trading.require_gas_transport_capacity_record(NEW.catalog_id,entitlement,kind,selected,TG_TABLE_NAME IN('gas_transport_capacity_versions','gas_transport_capacity_plans'));
 RETURN NULL;
END$$;
CREATE FUNCTION trading.guard_gas_transport_capacity_command() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE kind text=NEW.request#>>'{body,kind}';selected uuid;publication integer;revision integer;expected jsonb;BEGIN
 IF NEW.request->>'contextId' IS DISTINCT FROM NEW.source_context_id::text OR NEW.request#>>'{body,entitlementId}' IS DISTINCT FROM NEW.entitlement_id::text
  OR NEW.outcome->>'entitlementId' IS DISTINCT FROM NEW.entitlement_id::text OR NEW.outcome->>'kind' IS DISTINCT FROM kind
  OR jsonb_typeof(NEW.outcome->'changed') IS DISTINCT FROM 'boolean'
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.actor_id=NEW.actor_id AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
 PERFORM trading.gas_transport_capacity_body(NEW.request->'body',NEW.operation_id);
 selected=(NEW.outcome->>CASE WHEN kind='define' THEN 'versionId' ELSE 'revisionId' END)::uuid;
 IF kind='define' THEN SELECT p.publication_order,p.revision INTO publication,revision FROM trading.gas_transport_capacity_versions p WHERE(p.catalog_id,p.entitlement_id,p.id)=(NEW.catalog_id,NEW.entitlement_id,selected);
 ELSE SELECT p.publication_order,p.revision INTO publication,revision FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.entitlement_id,p.id)=(NEW.catalog_id,NEW.entitlement_id,selected);END IF;
 IF publication IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
 expected=jsonb_build_object('entitlementId',NEW.entitlement_id,'kind',kind,'changed',(NEW.outcome->>'changed')::boolean,
  'versionId',CASE WHEN kind='define' THEN selected ELSE(NEW.request#>>'{body,versionId}')::uuid END,'revision',revision,'stateVersion',publication);
 IF kind='plan' THEN expected=expected||jsonb_build_object('revisionId',selected);END IF;
 IF NEW.outcome IS DISTINCT FROM expected OR NEW.fact IS DISTINCT FROM jsonb_build_object('body',trading.gas_transport_capacity_fact_key(trading.gas_transport_capacity_fact(NEW.request->'body')),'effectiveAt',NEW.request->'effectiveAt')
  OR NEW.fact->'body' IS DISTINCT FROM trading.gas_transport_capacity_fact_key(trading.gas_transport_capacity_recorded_body(NEW.catalog_id,NEW.entitlement_id,kind,selected))
  OR NEW.request->>'effectiveAt' IS DISTINCT FROM(SELECT trading.gas_transport_capacity_instant(p.effective_at) FROM(
   SELECT effective_at FROM trading.gas_transport_capacity_versions WHERE(catalog_id,id)=(NEW.catalog_id,selected) AND kind='define'
   UNION ALL SELECT effective_at FROM trading.gas_transport_capacity_plans WHERE(catalog_id,id)=(NEW.catalog_id,selected) AND kind='plan')p)
 THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
 IF(NEW.outcome->>'changed')::boolean THEN
  IF selected<>NEW.operation_id THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
  IF EXISTS(SELECT 1 FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.entitlement_id,c.fact)=(NEW.catalog_id,NEW.entitlement_id,NEW.fact)
   AND c.operation_id<>NEW.operation_id AND c.outcome->>'changed'='true') THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
  PERFORM trading.require_gas_transport_capacity_record(NEW.catalog_id,NEW.entitlement_id,kind,selected,true);
 ELSE
  -- Semantic acknowledgement preserves the authentic first effect and cannot
  -- revise a head. Its own selected context must still be a valid certificate.
  IF NOT EXISTS(SELECT 1 FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.entitlement_id,c.fact)=(NEW.catalog_id,NEW.entitlement_id,NEW.fact)
   AND c.outcome->>'changed'='true' AND c.outcome-'changed'=NEW.outcome-'changed' AND c.operation_id<>NEW.operation_id)
  THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.entitlement_id,c.fact,c.source_context_id)=
   (NEW.catalog_id,NEW.entitlement_id,NEW.fact,NEW.source_context_id) AND c.outcome->>'changed'='true' AND c.outcome-'changed'=NEW.outcome-'changed')
  THEN RAISE EXCEPTION 'gas_transport_capacity_integrity';END IF;
 END IF;
 PERFORM trading.require_gas_transport_capacity_reference(NEW.catalog_id,NEW.entitlement_id);
 PERFORM security.require_gas_transport_capacity_commit(NEW.catalog_id,kind,NEW.authority_until);RETURN NULL;
END$$;
CREATE FUNCTION trading.guard_gas_transport_capacity_deadline() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_transport_capacity_commit(NEW.catalog_id,NEW.request#>>'{body,kind}',NEW.authority_until);
 PERFORM trading.require_gas_transport_capacity_reference(NEW.catalog_id,NEW.entitlement_id);RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER valid_gas_transport_capacity_command AFTER INSERT ON trading.gas_transport_capacity_commands DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_capacity_command();
CREATE CONSTRAINT TRIGGER valid_gas_transport_capacity_deadline AFTER INSERT ON trading.gas_transport_capacity_commands DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_capacity_deadline();
DO $$DECLARE name text;BEGIN
 FOREACH name IN ARRAY ARRAY['entitlements','paths','points','versions','limits','plans','claims'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER valid_gas_transport_capacity AFTER INSERT ON trading.gas_transport_capacity_%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_transport_capacity()',name);
 END LOOP;
END$$;


CREATE FUNCTION security.record_gas_transport_capacity(catalog uuid,operation uuid,body jsonb,effective_at timestamptz,witness jsonb,source_context uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;actor uuid;entitlement uuid;kind text;prior trading.gas_transport_capacity_commands;scope trading.gas_transport_capacity_entitlements;
 version trading.gas_transport_capacity_versions;plan trading.gas_transport_capacity_plans;payload jsonb;fact_value jsonb;result jsonb;item jsonb;limits jsonb;axis text;point text;path uuid;publication integer;revision integer;previous uuid;BEGIN
 kind=body->>'kind';deadline=security.gas_transport_capacity_authority(catalog,kind);
 IF kind IS NULL OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR effective_at IS NULL OR NOT isfinite(effective_at)
  OR effective_at<TIMESTAMPTZ '1901-01-01Z' OR effective_at>=TIMESTAMPTZ '2200-01-01Z' OR body IS NULL OR pg_column_size(body)>4194304
 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 body=trading.gas_transport_capacity_fact(body);PERFORM trading.gas_transport_capacity_body(body,operation);entitlement=(body->>'entitlementId')::uuid;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user;
 payload=jsonb_build_object('body',body,'effectiveAt',trading.gas_transport_capacity_instant(effective_at),'contextId',source_context);
 fact_value=jsonb_build_object('body',trading.gas_transport_capacity_fact_key(body),'effectiveAt',trading.gas_transport_capacity_instant(effective_at));
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM security.require_gas_transport_capacity_commit(catalog,kind,deadline);
 SELECT * INTO prior FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.operation_id)=(catalog,operation);
 IF FOUND THEN
  IF prior.actor_id<>actor OR prior.request-'contextId' IS DISTINCT FROM payload-'contextId' THEN RAISE EXCEPTION 'gas_transport_capacity_conflict';END IF;
  PERFORM security.gas_transport_capacity_status(catalog,operation);RETURN prior.outcome;
 END IF;
 SELECT * INTO prior FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.entitlement_id,c.fact)=(catalog,entitlement,fact_value) AND c.outcome->>'changed'='true' LIMIT 1;
 IF FOUND THEN
  PERFORM trading.require_gas_transport_capacity_reference(catalog,entitlement);
  source_context=prior.source_context_id;payload=jsonb_set(payload,'{contextId}',to_jsonb(source_context));
  result=prior.outcome||jsonb_build_object('changed',false);
  INSERT INTO trading.gas_transport_capacity_commands VALUES(catalog,operation,actor,entitlement,payload,fact_value,result,deadline,source_context);
 ELSE
  IF security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
  PERFORM trading.require_gas_transport_capacity_source(catalog,body,source_context,effective_at);
  SELECT * INTO scope FROM trading.gas_transport_capacity_entitlements h WHERE(h.catalog_id,h.id)=(catalog,entitlement) FOR UPDATE;
  IF kind='define' THEN
   SELECT * INTO version FROM trading.gas_transport_capacity_versions v WHERE(v.catalog_id,v.entitlement_id)=(catalog,entitlement) ORDER BY v.revision DESC LIMIT 1;
   previous=version.id;revision=coalesce(version.revision,0)+1;
  ELSE
   IF scope.id IS NULL THEN RAISE EXCEPTION 'gas_transport_capacity_source';END IF;
   IF witness IS NULL OR jsonb_typeof(witness) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'gas_transport_capacity_witness';END IF;
   SELECT * INTO plan FROM trading.gas_transport_capacity_plans p WHERE(p.catalog_id,p.entitlement_id,p.business_key)=(catalog,entitlement,body->>'businessKey') ORDER BY p.revision DESC LIMIT 1;
   previous=plan.id;revision=coalesce(plan.revision,0)+1;
  END IF;
  IF(body->>'expectedPreviousId')::uuid IS DISTINCT FROM previous THEN RAISE EXCEPTION 'gas_transport_capacity_stale';END IF;
  SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
  INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
  IF kind='define' THEN
   IF EXISTS(SELECT 1 FROM jsonb_array_elements(body->'paths')p WHERE NOT EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions d
    WHERE(d.catalog_id,d.revision_id,d.pipeline_code,d.zone_code,d.point_code,d.product_code)=((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,
     p->>'pipeline',p->>'receiptZone',p->>'receiptPoint',p->>'product')) OR NOT EXISTS(SELECT 1 FROM ref_data.gas_delivery_conventions d
    WHERE(d.catalog_id,d.revision_id,d.pipeline_code,d.zone_code,d.point_code,d.product_code)=((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,
     p->>'pipeline',p->>'deliveryZone',p->>'deliveryPoint',p->>'product')))
   THEN RAISE EXCEPTION 'gas_transport_capacity_path';END IF;
   IF scope.id IS NULL THEN
    INSERT INTO trading.gas_transport_capacity_entitlements VALUES(catalog,entitlement,body->>'businessKey',(body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,(body->>'bookId')::uuid,
     (body->>'legalEntityId')::uuid,(body->>'legalEntityRevision')::integer,(body->>'counterpartyId')::uuid,(body->>'counterpartyRevision')::integer,
     body->>'product',body->>'quantity',trading.gas_transport_capacity_at(body->>'from'),trading.gas_transport_capacity_at(body->>'through'),actor,publication,effective_at);
    FOR item IN SELECT * FROM jsonb_array_elements(body->'paths') LOOP
     INSERT INTO trading.gas_transport_capacity_paths VALUES(catalog,entitlement,(item->>'pathId')::uuid,item->>'pathKey',(body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'product',
      item->>'pipeline',item->>'receiptZone',item->>'receiptPoint',item->>'deliveryZone',item->>'deliveryPoint');
    END LOOP;
    FOREACH axis IN ARRAY ARRAY['receipt','delivery'] LOOP
     FOR item IN SELECT * FROM jsonb_array_elements(body->CASE WHEN axis='receipt' THEN 'receipts' ELSE 'deliveries' END) LOOP
      INSERT INTO trading.gas_transport_capacity_points VALUES(catalog,entitlement,axis,item->>'point',(body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'product');
     END LOOP;
    END LOOP;
   END IF;
   INSERT INTO trading.gas_transport_capacity_versions VALUES(catalog,entitlement,operation,revision,previous,body->>'implementation',body->>'rateBasis',body->>'flowShape',body->>'agreementBasis',body->>'deliveryBasis',
    body->>'evidence',source_context,(body->>'expectedStateVersion')::integer,actor,publication,effective_at);
   FOR axis,point,path,limits IN SELECT 'agreement',NULL::text,NULL::uuid,body->'agreement'
    UNION ALL SELECT 'route',NULL,(x->>'pathId')::uuid,x->'limits' FROM jsonb_array_elements(body->'paths')x
    UNION ALL SELECT 'receipt',x->>'point',NULL,x->'limits' FROM jsonb_array_elements(body->'receipts')x
    UNION ALL SELECT 'delivery',x->>'point',NULL,x->'limits' FROM jsonb_array_elements(body->'deliveries')x LOOP
    INSERT INTO trading.gas_transport_capacity_limits SELECT catalog,entitlement,operation,axis,point,path,(ordinality-1)::integer,
     trading.gas_transport_capacity_at(value->>'from'),trading.gas_transport_capacity_at(value->>'through'),trading.gas_transport_capacity_quantity(value->>'maximumPerHour') FROM jsonb_array_elements(limits) WITH ORDINALITY;
   END LOOP;
   -- Scope changes cannot be hidden behind new typed definition headers.
   IF trading.gas_transport_capacity_recorded_body(catalog,entitlement,'define',operation) IS DISTINCT FROM body THEN RAISE EXCEPTION 'gas_transport_capacity_scope_change';END IF;
   result=jsonb_build_object('entitlementId',entitlement,'kind',kind,'changed',true,'versionId',operation,'revision',revision,'stateVersion',publication);
  ELSE
   INSERT INTO trading.gas_transport_capacity_plans VALUES(catalog,entitlement,operation,body->>'businessKey',revision,previous,(body->>'versionId')::uuid,body->>'admissionPolicy',body->>'evidence',source_context,
    (body->>'expectedStateVersion')::integer,witness,actor,publication,effective_at);
   INSERT INTO trading.gas_transport_capacity_claims SELECT catalog,entitlement,operation,(x->>'claimId')::uuid,(x->>'pathId')::uuid,
    trading.gas_transport_capacity_at(x->>'receiptFrom'),trading.gas_transport_capacity_at(x->>'receiptTo'),trading.gas_transport_capacity_quantity(x->>'receiptQuantity'),
    trading.gas_transport_capacity_at(x->>'deliveryFrom'),trading.gas_transport_capacity_at(x->>'deliveryTo'),trading.gas_transport_capacity_quantity(x->>'deliveryQuantity') FROM jsonb_array_elements(body->'claims')x;
   PERFORM trading.verify_gas_transport_capacity_plan(catalog,operation);
   result=jsonb_build_object('entitlementId',entitlement,'kind',kind,'changed',true,'revisionId',operation,'versionId',(body->>'versionId')::uuid,'revision',revision,'stateVersion',publication);
  END IF;
  INSERT INTO trading.gas_transport_capacity_commands VALUES(catalog,operation,actor,entitlement,payload,fact_value,result,deadline,source_context);
 END IF;
 SET CONSTRAINTS trading.valid_gas_transport_capacity,trading.valid_gas_transport_capacity_command IMMEDIATE;
 SET CONSTRAINTS trading.valid_gas_transport_capacity,trading.valid_gas_transport_capacity_command DEFERRED;
 PERFORM security.require_gas_transport_capacity_commit(catalog,kind,deadline);RETURN result;
END$$;

CREATE FUNCTION security.gas_transport_capacity_prior_fact(catalog uuid,body jsonb,effective_at timestamptz) RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;prior trading.gas_transport_capacity_commands;fact_value jsonb;BEGIN
 deadline=security.gas_transport_capacity_authority(catalog,body->>'kind');body=trading.gas_transport_capacity_fact(body);
 IF body->>'kind' IS NULL OR body->>'kind' NOT IN('define','plan') OR effective_at IS NULL OR NOT isfinite(effective_at)
  OR pg_column_size(body)>4194304 THEN RAISE EXCEPTION 'gas_transport_capacity_input';END IF;
 fact_value=jsonb_build_object('body',trading.gas_transport_capacity_fact_key(body),'effectiveAt',trading.gas_transport_capacity_instant(effective_at));
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO prior FROM trading.gas_transport_capacity_commands c WHERE(c.catalog_id,c.entitlement_id,c.fact)=(catalog,(body->>'entitlementId')::uuid,fact_value) AND c.outcome->>'changed'='true' LIMIT 1;
 IF FOUND THEN PERFORM trading.require_gas_transport_capacity_reference(catalog,prior.entitlement_id);END IF;
 PERFORM security.require_gas_transport_capacity_commit(catalog,body->>'kind',deadline);RETURN prior.operation_id IS NOT NULL;
END$$;
DO $$DECLARE signature regprocedure;BEGIN
 FOR signature IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE(n.nspname='security' AND p.proname IN('gas_transport_capacity_authority','require_gas_transport_capacity_commit','gas_transport_capacity_owner','gas_transport_capacity_status','gas_transport_capacity_directory','read_gas_transport_capacity','record_gas_transport_capacity','gas_transport_capacity_prior_fact'))
   OR(n.nspname='trading' AND(p.proname LIKE 'gas\_transport\_capacity\_%' ESCAPE '\' OR p.proname LIKE 'require\_gas\_transport\_capacity\_%' ESCAPE '\' OR p.proname LIKE 'guard\_gas\_transport\_capacity%' ESCAPE '\' OR p.proname='verify_gas_transport_capacity_plan')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.gas_transport_capacity_authority(uuid,text),security.require_gas_transport_capacity_commit(uuid,text,timestamptz),
 security.gas_transport_capacity_owner(uuid),security.gas_transport_capacity_status(uuid,uuid),security.gas_transport_capacity_directory(uuid),
 security.read_gas_transport_capacity(uuid,uuid),security.record_gas_transport_capacity(uuid,uuid,jsonb,timestamptz,jsonb,uuid),security.gas_transport_capacity_prior_fact(uuid,jsonb,timestamptz) TO openetrm_runtime;
COMMIT;
