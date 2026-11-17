-- Full-facility physical storage operations; title and cash remain independent.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

-- Identity-only asks use the same closed page catalog as the workbench. These
-- pages do not grant an assistant any facility operation or navigation target.
DO $pages$
DECLARE definition text;marker text:='''facility_quantities''';
BEGIN
 definition=pg_get_functiondef('assistant.is_prompt_page(text)'::regprocedure);
 IF(length(definition)-length(replace(definition,marker,'')))/length(marker)<>1
 THEN RAISE EXCEPTION 'storage_facility_page_predecessor';END IF;
 EXECUTE replace(definition,marker,marker||',''facility_definitions'',''facility_activity''');
END $pages$;

CREATE TABLE trading.storage_facilities(
 catalog_id uuid NOT NULL,id uuid NOT NULL,reference_catalog_id uuid NOT NULL,facility_code ref_data.code NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,reference_catalog_id,facility_code),UNIQUE(catalog_id,id,reference_catalog_id,facility_code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.storage_facility_versions(
 catalog_id uuid NOT NULL,id uuid NOT NULL,facility_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,facility_code ref_data.code NOT NULL,
 commodity_code ref_data.code NOT NULL,quantity_code ref_data.code NOT NULL,clock_code ref_data.code NOT NULL,
 minimum numeric NOT NULL CHECK(minimum>=0 AND scale(minimum)<=18),maximum numeric NOT NULL CHECK(maximum>minimum AND maximum<1e24 AND scale(maximum)<=18),
 implementation text NOT NULL CHECK(implementation='storage_total_physical_step_ratchets_v1'),
 inventory_basis text NOT NULL CHECK(inventory_basis='total_physical_inventory_v1'),
 rate_basis text NOT NULL CHECK(rate_basis='native_quantity_per_elapsed_hour_v1'),
 simultaneous_flow text NOT NULL CHECK(simultaneous_flow='separate_injection_withdrawal_v1'),loss_treatment text NOT NULL CHECK(loss_treatment='no_loss_v1'),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,facility_id,id),UNIQUE(catalog_id,facility_id,revision),UNIQUE(catalog_id,facility_id,previous_id),
 UNIQUE(catalog_id,id,reference_catalog_id,reference_revision_id,commodity_code),CHECK((revision=1)=(previous_id IS NULL)),
 FOREIGN KEY(catalog_id,facility_id,reference_catalog_id,facility_code) REFERENCES trading.storage_facilities(catalog_id,id,reference_catalog_id,facility_code),
 FOREIGN KEY(catalog_id,facility_id,previous_id) REFERENCES trading.storage_facility_versions(catalog_id,facility_id,id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,commodity_code,facility_code) REFERENCES ref_data.commodity_locations(catalog_id,revision_id,commodity_code,location_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code,commodity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code,commodity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.storage_facility_ports(
 catalog_id uuid NOT NULL,version_id uuid NOT NULL,id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,product_code ref_data.code NOT NULL,
 pipeline_code ref_data.code NOT NULL,zone_code ref_data.code NOT NULL,point_code ref_data.code NOT NULL,
 direction text NOT NULL CHECK(direction IN('inject','withdraw')),contract_direction integer NOT NULL CHECK(contract_direction IN(-1,1)),
 PRIMARY KEY(catalog_id,version_id,id),UNIQUE(catalog_id,id),UNIQUE(catalog_id,version_id,point_code,direction),
 FOREIGN KEY(catalog_id,version_id,reference_catalog_id,reference_revision_id,product_code)
  REFERENCES trading.storage_facility_versions(catalog_id,id,reference_catalog_id,reference_revision_id,commodity_code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code)
  REFERENCES ref_data.gas_delivery_conventions(catalog_id,revision_id,pipeline_code,zone_code,point_code,product_code)
);
CREATE TABLE trading.storage_facility_ratchets(
 catalog_id uuid NOT NULL,version_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 127),
 lower_level numeric NOT NULL CHECK(lower_level>=0 AND scale(lower_level)<=18),upper_level numeric NOT NULL CHECK(upper_level>lower_level AND scale(upper_level)<=18),
 injection_per_hour numeric NOT NULL CHECK(injection_per_hour>=0 AND injection_per_hour<1e24 AND scale(injection_per_hour)<=18),
 withdrawal_per_hour numeric NOT NULL CHECK(withdrawal_per_hour>=0 AND withdrawal_per_hour<1e24 AND scale(withdrawal_per_hour)<=18),
 PRIMARY KEY(catalog_id,version_id,ordinal),FOREIGN KEY(catalog_id,version_id) REFERENCES trading.storage_facility_versions(catalog_id,id)
);
CREATE TABLE trading.storage_facility_openings(
 catalog_id uuid NOT NULL,id uuid NOT NULL,facility_id uuid NOT NULL,business_key text NOT NULL CHECK(length(btrim(business_key)) BETWEEN 1 AND 128),
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,observed_at ref_data.instant NOT NULL,quantity numeric NOT NULL CHECK(abs(quantity)<1e24 AND scale(quantity)<=18),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,facility_id,id),UNIQUE(catalog_id,facility_id,business_key,revision),UNIQUE(catalog_id,facility_id,business_key,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),FOREIGN KEY(catalog_id,facility_id) REFERENCES trading.storage_facilities(catalog_id,id),
 FOREIGN KEY(catalog_id,facility_id,previous_id) REFERENCES trading.storage_facility_openings(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),CHECK(observed_at<=effective_at)
);
CREATE TABLE trading.storage_facility_activities(
 catalog_id uuid NOT NULL,id uuid NOT NULL,facility_id uuid NOT NULL,business_key text NOT NULL CHECK(length(btrim(business_key)) BETWEEN 1 AND 128),
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,version_id uuid NOT NULL,port_id uuid NOT NULL,
 from_at ref_data.instant NOT NULL,through_at ref_data.instant NOT NULL,direction text NOT NULL CHECK(direction IN('inject','withdraw')),
 quantity numeric CHECK(quantity>=0 AND quantity<1e24 AND scale(quantity)<=18),source_revision_id uuid,
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,facility_id,id),UNIQUE(catalog_id,facility_id,business_key,revision),UNIQUE(catalog_id,facility_id,business_key,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),CHECK(num_nonnulls(quantity,source_revision_id)=1),CHECK(from_at<through_at AND through_at<=effective_at),
 FOREIGN KEY(catalog_id,facility_id,version_id) REFERENCES trading.storage_facility_versions(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,version_id,port_id) REFERENCES trading.storage_facility_ports(catalog_id,version_id,id),
 FOREIGN KEY(catalog_id,facility_id,previous_id) REFERENCES trading.storage_facility_activities(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,source_revision_id) REFERENCES trading.facility_quantity_revisions(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
-- A reported source belongs to one stable physical activity, including all of
-- its corrections. Linked activities derive quantity from that original store.
CREATE TABLE trading.storage_facility_flow_sources(
 catalog_id uuid NOT NULL,ledger_id uuid NOT NULL,facility_id uuid NOT NULL,business_key text NOT NULL,
 PRIMARY KEY(catalog_id,ledger_id),UNIQUE(catalog_id,facility_id,business_key),
 FOREIGN KEY(catalog_id,ledger_id) REFERENCES trading.facility_quantity_ledgers(catalog_id,id),
 FOREIGN KEY(catalog_id,facility_id) REFERENCES trading.storage_facilities(catalog_id,id)
);
CREATE TABLE trading.storage_facility_coverages(
 catalog_id uuid NOT NULL,id uuid NOT NULL,facility_id uuid NOT NULL,business_key text NOT NULL CHECK(length(btrim(business_key)) BETWEEN 1 AND 128),
 revision integer NOT NULL CHECK(revision>0),previous_id uuid,version_id uuid NOT NULL,opening_revision_id uuid,from_at ref_data.instant NOT NULL,through_at ref_data.instant NOT NULL,
 statement text NOT NULL CHECK(statement IN('complete_observed_window_v1','incomplete_observed_window_v1')),
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,facility_id,id),UNIQUE(catalog_id,facility_id,business_key,revision),UNIQUE(catalog_id,facility_id,business_key,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),CHECK(from_at<through_at AND through_at<=effective_at),
 FOREIGN KEY(catalog_id,facility_id,version_id) REFERENCES trading.storage_facility_versions(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,facility_id,opening_revision_id) REFERENCES trading.storage_facility_openings(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,facility_id,previous_id) REFERENCES trading.storage_facility_coverages(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.storage_facility_covered_activities(
 catalog_id uuid NOT NULL,coverage_id uuid NOT NULL,facility_id uuid NOT NULL,activity_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,coverage_id,activity_id),
 FOREIGN KEY(catalog_id,facility_id,coverage_id) REFERENCES trading.storage_facility_coverages(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,facility_id,activity_id) REFERENCES trading.storage_facility_activities(catalog_id,facility_id,id)
);
CREATE TABLE trading.storage_facility_plans(
 catalog_id uuid NOT NULL,id uuid NOT NULL,facility_id uuid NOT NULL,revision integer NOT NULL CHECK(revision>0),previous_id uuid,
 version_id uuid NOT NULL,opening_revision_id uuid NOT NULL,from_at ref_data.instant NOT NULL,through_at ref_data.instant NOT NULL,
 evidence text NOT NULL CHECK(length(btrim(evidence)) BETWEEN 1 AND 500),source_head integer NOT NULL,
 actor_id uuid NOT NULL REFERENCES security.principals(id),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,facility_id,id),UNIQUE(catalog_id,facility_id,revision),UNIQUE(catalog_id,facility_id,previous_id),
 CHECK((revision=1)=(previous_id IS NULL)),CHECK(from_at<through_at),
 FOREIGN KEY(catalog_id,facility_id,version_id) REFERENCES trading.storage_facility_versions(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,facility_id,opening_revision_id) REFERENCES trading.storage_facility_openings(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,facility_id,previous_id) REFERENCES trading.storage_facility_plans(catalog_id,facility_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.storage_facility_plan_activities(
 catalog_id uuid NOT NULL,plan_id uuid NOT NULL,id uuid NOT NULL,version_id uuid NOT NULL,port_id uuid NOT NULL,
 from_at ref_data.instant NOT NULL,through_at ref_data.instant NOT NULL,direction text NOT NULL CHECK(direction IN('inject','withdraw')),
 quantity numeric NOT NULL CHECK(quantity>=0 AND quantity<1e24 AND scale(quantity)<=18),PRIMARY KEY(catalog_id,plan_id,id),CHECK(from_at<through_at),
 FOREIGN KEY(catalog_id,plan_id) REFERENCES trading.storage_facility_plans(catalog_id,id),
 FOREIGN KEY(catalog_id,version_id,port_id) REFERENCES trading.storage_facility_ports(catalog_id,version_id,id)
);
CREATE TABLE trading.storage_facility_plan_assessments(
 catalog_id uuid NOT NULL,plan_id uuid NOT NULL,witness jsonb NOT NULL CHECK(pg_column_size(witness)<=4194304),
 PRIMARY KEY(catalog_id,plan_id),FOREIGN KEY(catalog_id,plan_id) REFERENCES trading.storage_facility_plans(catalog_id,id)
);
CREATE TABLE trading.storage_facility_commands(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),operation_id uuid NOT NULL,facility_id uuid NOT NULL,
 request jsonb NOT NULL CHECK(pg_column_size(request)<=4194304),fact jsonb NOT NULL,outcome jsonb NOT NULL,authority_until ref_data.instant NOT NULL,
 source_context_id uuid NOT NULL,PRIMARY KEY(catalog_id,actor_id,operation_id),
 FOREIGN KEY(catalog_id,facility_id) REFERENCES trading.storage_facilities(catalog_id,id),
 FOREIGN KEY(catalog_id,source_context_id) REFERENCES market_data.temporal_contexts(catalog_id,id)
);
CREATE INDEX storage_facility_command_facts ON trading.storage_facility_commands(catalog_id,facility_id);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['storage_facilities','storage_facility_versions','storage_facility_ports','storage_facility_ratchets','storage_facility_openings',
  'storage_facility_activities','storage_facility_flow_sources','storage_facility_coverages','storage_facility_covered_activities',
  'storage_facility_plans','storage_facility_plan_activities','storage_facility_plan_assessments','storage_facility_commands'] LOOP
  EXECUTE format('ALTER TABLE trading.%I OWNER TO openetrm_security_owner',t);
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE POLICY owner_access ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;
CREATE FUNCTION security.require_storage_facility_commit(catalog uuid,kind text,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE permission text;BEGIN
 IF until_at IS NULL OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 PERFORM security.storage_facility_authority(catalog,NULL);
 IF kind IS NOT NULL THEN
  permission=CASE WHEN kind='define' THEN 'ConfigureFacilityLimits' ELSE 'RecordFacilityQuantities' END;
  IF kind NOT IN('define','opening','activity','coverage','plan') OR security.trading_access(catalog,permission) IS NOT TRUE
   OR clock_timestamp()>=security.authority_until(permission) THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 END IF;
END$$;

CREATE FUNCTION trading.storage_facility_keys(body jsonb,keys text[]) RETURNS void LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 IF jsonb_typeof(body) IS DISTINCT FROM 'object' OR(SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(body)k)
  IS DISTINCT FROM(SELECT array_agg(k ORDER BY k) FROM unnest(keys)k) THEN RAISE EXCEPTION 'storage_facility_input';END IF;
END$$;
CREATE FUNCTION trading.storage_facility_at(value text) RETURNS timestamptz LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 RETURN settlement_data.futures_margin_utc_instant(value);
EXCEPTION WHEN raise_exception OR invalid_datetime_format OR datetime_field_overflow THEN RAISE EXCEPTION 'storage_facility_input';
END$$;
CREATE FUNCTION trading.storage_facility_fact(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb='{}';item record;value jsonb;BEGIN
 IF jsonb_typeof(body)<>'object' THEN RAISE EXCEPTION 'storage_facility_input';END IF;
 FOR item IN SELECT * FROM jsonb_each(body) LOOP
  value=item.value;
  IF value<>'null'::jsonb THEN
   IF item.key~'Id$' THEN value=to_jsonb((value#>>'{}')::uuid);
   ELSIF item.key=ANY(ARRAY['evidence','businessKey']) THEN value=to_jsonb(btrim(value#>>'{}'));
   ELSIF item.key=ANY(ARRAY['quantity','minimum','maximum','lower','upper','injectionPerHour','withdrawalPerHour'])
    AND NOT(item.key='quantity' AND body->>'kind'='define') THEN value=to_jsonb(trim_scale(trading.gas_owned_quantity(value#>>'{}'))::text);
   ELSIF item.key=ANY(ARRAY['from','through','observedAt']) THEN value=to_jsonb(to_char(trading.storage_facility_at(value#>>'{}') AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
   ELSIF item.key=ANY(ARRAY['ports','activities','ratchets']) THEN
    IF jsonb_typeof(value)<>'array' THEN RAISE EXCEPTION 'storage_facility_input';END IF;
    SELECT coalesce(jsonb_agg(trading.storage_facility_fact(x.value) ORDER BY CASE WHEN item.key='ratchets' THEN x.ordinality ELSE 0 END,
      CASE WHEN item.key<>'ratchets' THEN(x.value->>'portId')::uuid END,CASE WHEN item.key<>'ratchets' THEN(x.value->>'revisionId')::uuid END),'[]') INTO value
     FROM jsonb_array_elements(value) WITH ORDINALITY x(value,ordinality);
   ELSIF item.key='activityRevisionIds' THEN
    SELECT coalesce(jsonb_agg(to_jsonb((x.value#>>'{}')::uuid) ORDER BY(x.value#>>'{}')::uuid),'[]') INTO value FROM jsonb_array_elements(value)x;
   END IF;
  END IF;
  result=result||jsonb_build_object(item.key,value);
 END LOOP;RETURN result;
END$$;
CREATE FUNCTION security.storage_facility_authority(catalog uuid,kind text) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;BEGIN
 PERFORM security.require_facility_quantities(catalog,false);
 deadline=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadFacilityQuantities'));
 IF kind IS NOT NULL THEN
  IF kind='define' THEN
   IF security.trading_access(catalog,'ConfigureFacilityLimits') IS NOT TRUE THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
   deadline=least(deadline,security.authority_until('ConfigureFacilityLimits'));
  ELSE
   PERFORM security.require_facility_quantities(catalog,true);deadline=least(deadline,security.authority_until('RecordFacilityQuantities'));
  END IF;
  IF security.current_write_context() IS NOT TRUE THEN RAISE EXCEPTION 'historical_read_only';END IF;
 END IF;
 IF security.market_access(catalog,'ReadMarketData') IS NOT TRUE OR deadline IS NULL OR clock_timestamp()>=deadline
 THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;RETURN deadline;
END$$;
CREATE FUNCTION trading.storage_facility_head(catalog uuid,facility uuid,known_only boolean,through_publication integer DEFAULT 2147483647) RETURNS integer
 LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT coalesce(max(p),0) FROM(
  SELECT publication_order p FROM trading.storage_facilities WHERE(catalog_id,id)=(catalog,facility)
  UNION ALL SELECT publication_order FROM trading.storage_facility_versions WHERE(catalog_id,facility_id)=(catalog,facility)
  UNION ALL SELECT publication_order FROM trading.storage_facility_openings WHERE(catalog_id,facility_id)=(catalog,facility)
  UNION ALL SELECT publication_order FROM trading.storage_facility_activities WHERE(catalog_id,facility_id)=(catalog,facility)
  UNION ALL SELECT publication_order FROM trading.storage_facility_coverages WHERE(catalog_id,facility_id)=(catalog,facility)
  UNION ALL SELECT publication_order FROM trading.storage_facility_plans WHERE(catalog_id,facility_id)=(catalog,facility)
  UNION ALL SELECT r.publication_order FROM trading.storage_facility_flow_sources s JOIN trading.facility_quantity_revisions r
   ON(r.catalog_id,r.ledger_id)=(s.catalog_id,s.ledger_id) WHERE(s.catalog_id,s.facility_id)=(catalog,facility)
    AND EXISTS(SELECT 1 FROM trading.storage_facility_activities a JOIN trading.facility_quantity_revisions original
     ON(original.catalog_id,original.id)=(a.catalog_id,a.source_revision_id)
     WHERE(a.catalog_id,a.facility_id,a.business_key,original.ledger_id)=(s.catalog_id,s.facility_id,s.business_key,s.ledger_id)
      AND a.publication_order<=through_publication AND(NOT known_only OR security.trading_visible(catalog,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z')))
 )s WHERE p<=through_publication AND(NOT known_only OR security.trading_visible(catalog,p,TIMESTAMPTZ '0001-01-01 00:00:00Z'));
$$;

CREATE FUNCTION trading.storage_facility_fact_key(body jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb=body-'expectedStateVersion'-'expectedPreviousId'-'planId';BEGIN
 IF body?'ports' THEN result=jsonb_set(result,'{ports}',(SELECT jsonb_agg(value-'portId' ORDER BY(value-'portId')::text) FROM jsonb_array_elements(body->'ports')));END IF;
 IF body?'activities' THEN result=jsonb_set(result,'{activities}',coalesce((SELECT jsonb_agg(value-'revisionId' ORDER BY(value-'revisionId')::text) FROM jsonb_array_elements(body->'activities')),'[]'));END IF;
 RETURN result;
END$$;

CREATE FUNCTION trading.require_storage_facility_reference(catalog uuid,facility uuid) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(catalog,facility)
  AND security.reference_access(v.reference_catalog_id,'ReadMarketData') AND security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id))
 THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.storage_facility_status(catalog uuid,operation uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;prior trading.storage_facility_commands;deadline timestamptz;BEGIN
 deadline=security.storage_facility_authority(catalog,NULL);
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO prior FROM trading.storage_facility_commands c WHERE(c.catalog_id,c.actor_id,c.operation_id)=(catalog,actor,operation);
 IF NOT FOUND THEN RETURN jsonb_build_object('found',false);END IF;
 deadline=least(deadline,security.authority_until(CASE WHEN prior.request#>>'{body,kind}'='define' THEN 'ConfigureFacilityLimits' ELSE 'RecordFacilityQuantities' END));
 PERFORM security.require_storage_facility_commit(catalog,prior.request#>>'{body,kind}',deadline);
 PERFORM trading.require_storage_facility_reference(catalog,prior.facility_id);
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 RETURN jsonb_build_object('found',true,'operationId',operation,'facilityId',prior.facility_id,'kind',prior.request#>>'{body,kind}',
  'command',prior.request->'body','effectiveAt',prior.request->'effectiveAt','outcome',prior.outcome,'authorityUntil',deadline);
END$$;

CREATE FUNCTION trading.storage_facility_visible_activities(catalog uuid,facility uuid) RETURNS SETOF trading.storage_facility_activities
 LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT h.* FROM(SELECT DISTINCT ON(business_key)a.* FROM trading.storage_facility_activities a
  WHERE(a.catalog_id,a.facility_id)=(catalog,facility) AND security.trading_visible(catalog,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z')
  ORDER BY business_key,revision DESC)h WHERE security.trading_visible(catalog,h.publication_order,h.effective_at);
$$;
CREATE FUNCTION security.read_storage_facility(catalog uuid,facility uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;header trading.storage_facilities;version trading.storage_facility_versions;plan trading.storage_facility_plans;result jsonb;linked uuid;BEGIN
 deadline=security.storage_facility_authority(catalog,NULL);PERFORM security.require_facility_quantity_read(catalog);
 SELECT * INTO header FROM trading.storage_facilities f WHERE(f.catalog_id,f.id)=(catalog,facility) AND security.trading_visible(catalog,f.publication_order,f.effective_at);
 IF NOT FOUND THEN RAISE EXCEPTION 'storage_facility_source';END IF;
 SELECT * INTO version FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(catalog,facility)
  AND security.trading_visible(catalog,v.publication_order,v.effective_at) ORDER BY revision DESC LIMIT 1;
 IF version.id IS NULL OR security.reference_version_visible(version.reference_catalog_id,version.reference_revision_id) IS NOT TRUE
  OR security.reference_access(version.reference_catalog_id,'ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 IF(SELECT count(*) FROM trading.storage_facility_activities a WHERE(a.catalog_id,a.facility_id)=(catalog,facility)
   AND security.trading_visible(catalog,a.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z'))>16384
  OR(SELECT count(*) FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(catalog,facility)
   AND security.trading_visible(catalog,v.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z'))>128
 THEN RAISE EXCEPTION 'storage_facility_scope';END IF;
 FOR linked IN SELECT DISTINCT s.ledger_id FROM trading.storage_facility_visible_activities(catalog,facility) a
  JOIN trading.facility_quantity_revisions s ON(s.catalog_id,s.id)=(a.catalog_id,a.source_revision_id) LOOP
  PERFORM security.read_facility_quantity_ledger(catalog,linked);
 END LOOP;
 SELECT * INTO plan FROM trading.storage_facility_plans p WHERE(p.catalog_id,p.facility_id)=(catalog,facility)
  AND security.trading_visible(catalog,p.publication_order,p.effective_at) ORDER BY revision DESC LIMIT 1;
 WITH openings AS(SELECT * FROM(SELECT DISTINCT ON(business_key)o.* FROM trading.storage_facility_openings o
  WHERE(o.catalog_id,o.facility_id)=(catalog,facility) AND security.trading_visible(catalog,o.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z') ORDER BY business_key,revision DESC)h
  WHERE security.trading_visible(catalog,h.publication_order,h.effective_at)),
 activities AS(SELECT h.*,coalesce(h.quantity,s.quantity) resolved_quantity,s.ledger_id FROM trading.storage_facility_visible_activities(catalog,facility)h
  LEFT JOIN trading.facility_quantity_revisions s ON(s.catalog_id,s.id)=(h.catalog_id,h.source_revision_id)),
 coverages AS(SELECT * FROM(SELECT DISTINCT ON(business_key)c.* FROM trading.storage_facility_coverages c
  WHERE(c.catalog_id,c.facility_id)=(catalog,facility) AND security.trading_visible(catalog,c.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z') ORDER BY business_key,revision DESC)h
  WHERE security.trading_visible(catalog,h.publication_order,h.effective_at))
 SELECT jsonb_build_object('facility',to_jsonb(header),'version',to_jsonb(version)||jsonb_build_object('minimum',trim_scale(version.minimum)::text,'maximum',trim_scale(version.maximum)::text),
  'stateVersion',trading.storage_facility_head(catalog,facility,true),
  'knownRuleVersions',coalesce((SELECT jsonb_agg(jsonb_build_object('id',v.id,'effective_at',v.effective_at,'revision',v.revision) ORDER BY v.revision)
   FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(catalog,facility)
    AND security.trading_visible(catalog,v.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z')),'[]'),
  'versionBundles',coalesce((SELECT jsonb_agg(jsonb_build_object('version',to_jsonb(v),
    'ports',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM trading.storage_facility_ports p WHERE(p.catalog_id,p.version_id)=(catalog,v.id)),
    'ratchets',(SELECT jsonb_agg(jsonb_build_object('ordinal',b.ordinal,'lower',trim_scale(b.lower_level)::text,'upper',trim_scale(b.upper_level)::text,
     'injectionPerHour',trim_scale(b.injection_per_hour)::text,'withdrawalPerHour',trim_scale(b.withdrawal_per_hour)::text) ORDER BY ordinal)
     FROM trading.storage_facility_ratchets b WHERE(b.catalog_id,b.version_id)=(catalog,v.id))) ORDER BY v.revision)
   FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(catalog,facility) AND security.trading_visible(catalog,v.publication_order,v.effective_at)
    AND security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id) AND security.reference_access(v.reference_catalog_id,'ReadMarketData')),'[]'),
  'ports',coalesce((SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM trading.storage_facility_ports p WHERE(p.catalog_id,p.version_id)=(catalog,version.id)),'[]'),
  'ratchets',coalesce((SELECT jsonb_agg(jsonb_build_object('ordinal',b.ordinal,'lower',trim_scale(b.lower_level)::text,'upper',trim_scale(b.upper_level)::text,
   'injectionPerHour',trim_scale(b.injection_per_hour)::text,'withdrawalPerHour',trim_scale(b.withdrawal_per_hour)::text) ORDER BY ordinal)
   FROM trading.storage_facility_ratchets b WHERE(b.catalog_id,b.version_id)=(catalog,version.id)),'[]'),
  'opening',(SELECT to_jsonb(o)||jsonb_build_object('quantity',trim_scale(o.quantity)::text) FROM openings o ORDER BY observed_at DESC,publication_order DESC LIMIT 1),
  'openings',coalesce((SELECT jsonb_agg(to_jsonb(o)||jsonb_build_object('quantity',trim_scale(o.quantity)::text) ORDER BY observed_at,business_key) FROM openings o),'[]'),
  'activities',coalesce((SELECT jsonb_agg(to_jsonb(a)||jsonb_build_object('quantity',trim_scale(a.resolved_quantity)::text,
    'sourceCorrected',EXISTS(SELECT 1 FROM trading.facility_quantity_revisions previous JOIN trading.facility_quantity_revisions newer
     ON(newer.catalog_id,newer.ledger_id)=(previous.catalog_id,previous.ledger_id) WHERE(previous.catalog_id,previous.id)=(a.catalog_id,a.source_revision_id)
      AND newer.revision>previous.revision AND security.trading_visible(catalog,newer.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z')))
    ORDER BY from_at,business_key) FROM activities a),'[]'),
  'coverage',(SELECT to_jsonb(c)||jsonb_build_object('activityRevisionIds',(SELECT coalesce(jsonb_agg(activity_id ORDER BY activity_id),'[]') FROM trading.storage_facility_covered_activities x WHERE(x.catalog_id,x.coverage_id)=(catalog,c.id))) FROM coverages c ORDER BY publication_order DESC LIMIT 1),
  'coverages',coalesce((SELECT jsonb_agg(to_jsonb(c)||jsonb_build_object('activityRevisionIds',(SELECT coalesce(jsonb_agg(activity_id ORDER BY activity_id),'[]') FROM trading.storage_facility_covered_activities x WHERE(x.catalog_id,x.coverage_id)=(catalog,c.id))) ORDER BY publication_order) FROM coverages c),'[]'),
  'plan',CASE WHEN plan.id IS NULL THEN NULL ELSE to_jsonb(plan) END,
  'planActivities',coalesce((SELECT jsonb_agg(to_jsonb(a)||jsonb_build_object('quantity',trim_scale(a.quantity)::text) ORDER BY a.id) FROM trading.storage_facility_plan_activities a WHERE(a.catalog_id,a.plan_id)=(catalog,plan.id)),'[]'),
  'planAssessment',(SELECT witness FROM trading.storage_facility_plan_assessments a WHERE(a.catalog_id,a.plan_id)=(catalog,plan.id)),
  'requiresReassessment',plan.id IS NOT NULL AND EXISTS(SELECT 1 FROM(
   SELECT publication_order p FROM trading.storage_facility_versions WHERE(catalog_id,facility_id)=(catalog,facility)
   UNION ALL SELECT publication_order FROM trading.storage_facility_openings WHERE(catalog_id,facility_id)=(catalog,facility)
   UNION ALL SELECT publication_order FROM trading.storage_facility_activities WHERE(catalog_id,facility_id)=(catalog,facility)
   UNION ALL SELECT publication_order FROM trading.storage_facility_coverages WHERE(catalog_id,facility_id)=(catalog,facility)
   UNION ALL SELECT r.publication_order FROM activities a JOIN trading.facility_quantity_revisions source ON(source.catalog_id,source.id)=(a.catalog_id,a.source_revision_id)
    JOIN trading.facility_quantity_revisions r ON(r.catalog_id,r.ledger_id)=(source.catalog_id,source.ledger_id)
   )h WHERE h.p>plan.source_head AND security.trading_visible(catalog,h.p,TIMESTAMPTZ '0001-01-01 00:00:00Z')),
  'linkedSourceCorrection',EXISTS(SELECT 1 FROM activities a JOIN trading.facility_quantity_revisions s ON(s.catalog_id,s.id)=(a.catalog_id,a.source_revision_id)
   JOIN trading.facility_quantity_revisions n ON(n.catalog_id,n.ledger_id)=(s.catalog_id,s.ledger_id) WHERE n.revision>s.revision
    AND security.trading_visible(catalog,n.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z')),
  'contextId',(SELECT market_context_id FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user),
  'knownAt',(SELECT t.known_at FROM security.request_contexts q JOIN market_data.temporal_contexts t
   ON(t.catalog_id,t.id)=(q.market_catalog_id,q.market_context_id) WHERE q.backend_pid=pg_backend_pid()
    AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND t.catalog_id=catalog),
  'asOf',(SELECT trading_as_of FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user)) INTO result;
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 PERFORM security.storage_facility_authority(catalog,NULL);RETURN result;
END$$;
CREATE FUNCTION security.storage_facility_directory(catalog uuid) RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;result jsonb;BEGIN
 deadline=security.storage_facility_authority(catalog,NULL);PERFORM security.require_facility_quantity_read(catalog);
 SELECT coalesce(jsonb_agg(jsonb_build_object('id',id,'label',name,'versionId',version_id,'revision',revision) ORDER BY name,id),'[]') INTO result FROM(
  SELECT f.id,l.name,v.id version_id,v.revision FROM trading.storage_facilities f JOIN LATERAL(
   SELECT * FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(f.catalog_id,f.id)
    AND security.trading_visible(catalog,v.publication_order,v.effective_at) ORDER BY revision DESC LIMIT 1)v ON true
  JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(v.reference_catalog_id,v.reference_revision_id,v.facility_code)
  WHERE f.catalog_id=catalog AND security.trading_visible(catalog,f.publication_order,f.effective_at)
   AND security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id) AND security.reference_access(v.reference_catalog_id,'ReadMarketData')
  ORDER BY l.name,f.id LIMIT 201)s;
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 PERFORM security.storage_facility_authority(catalog,NULL);RETURN result;
END$$;

CREATE FUNCTION security.record_storage_facility(catalog uuid,operation uuid,body jsonb,effective timestamptz,assessment jsonb,source_context uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;actor uuid;facility uuid;kind text;prior trading.storage_facility_commands;payload jsonb;fact_key jsonb;
 version trading.storage_facility_versions;opening trading.storage_facility_openings;activity trading.storage_facility_activities;
 coverage trading.storage_facility_coverages;plan trading.storage_facility_plans;header trading.storage_facilities;port trading.storage_facility_ports;
 source trading.facility_quantity_revisions;ledger trading.facility_quantity_ledgers;gas trading.facility_gas_delivery_scopes;
 item jsonb;publication integer;id uuid;result jsonb;ordinal integer;head integer;effective_text text;BEGIN
 kind=body->>'kind';deadline=security.storage_facility_authority(catalog,kind);
 IF kind IS NULL OR kind NOT IN('define','opening','activity','coverage','plan') OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid
  OR effective IS NULL OR NOT isfinite(effective) OR body IS NULL OR pg_column_size(body)>1048576 THEN RAISE EXCEPTION 'storage_facility_input';END IF;
 body=trading.storage_facility_fact(body);facility=(body->>'facilityId')::uuid;
 IF facility IS NULL OR facility='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'storage_facility_input';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 effective_text=to_char(effective AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"');
 payload=jsonb_build_object('body',body,'effectiveAt',effective_text,'contextId',source_context);fact_key=jsonb_build_object('body',trading.storage_facility_fact_key(body),'effectiveAt',effective_text);
 PERFORM pg_advisory_xact_lock(hashtextextended('storage_facility:'||catalog::text||':'||facility::text,0));
 PERFORM pg_advisory_xact_lock(730849102);
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 PERFORM security.storage_facility_authority(catalog,kind);
 SELECT * INTO prior FROM trading.storage_facility_commands c WHERE(c.catalog_id,c.actor_id,c.operation_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF prior.request-'contextId' IS DISTINCT FROM payload-'contextId' THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
  PERFORM security.storage_facility_status(catalog,operation);
  PERFORM security.require_storage_facility_commit(catalog,kind,deadline);RETURN prior.outcome;
 END IF;
 SELECT * INTO prior FROM trading.storage_facility_commands c WHERE(c.catalog_id,c.facility_id,c.fact)=(catalog,facility,fact_key) LIMIT 1;
 IF FOUND THEN
  PERFORM trading.require_storage_facility_reference(catalog,facility);
  result=prior.outcome||jsonb_build_object('changed',false);
  INSERT INTO trading.storage_facility_commands VALUES(catalog,actor,operation,facility,jsonb_set(payload,'{contextId}',to_jsonb(prior.source_context_id)),fact_key,result,deadline,prior.source_context_id);
  PERFORM security.require_storage_facility_commit(catalog,kind,deadline);RETURN result;
 END IF;
 PERFORM trading.require_storage_facility_source(catalog,facility,body,source_context,effective);
 head=trading.storage_facility_head(catalog,facility,false);
 IF(body->>'expectedStateVersion')::integer IS DISTINCT FROM head
 THEN RAISE EXCEPTION 'storage_facility_stale';END IF;
 SELECT * INTO header FROM trading.storage_facilities f WHERE(f.catalog_id,f.id)=(catalog,facility) FOR UPDATE;
 IF kind<>'define' AND header.id IS NULL THEN RAISE EXCEPTION 'storage_facility_source';END IF;
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());id=gen_random_uuid();
 IF kind='define' THEN
  PERFORM trading.storage_facility_keys(body,ARRAY['kind','facilityId','expectedStateVersion','expectedPreviousId','referenceCatalogId','referenceRevisionId','facilityCode',
   'commodity','quantity','clock','minimum','maximum','implementation','inventoryBasis','rateBasis','simultaneousFlow','lossTreatment','ports','ratchets','evidence']);
  IF security.reference_access((body->>'referenceCatalogId')::uuid,'ReadMarketData') IS NOT TRUE
   OR security.reference_version_visible((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid) IS NOT TRUE
  THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
  IF jsonb_typeof(body->'ports')<>'array' OR jsonb_array_length(body->'ports') NOT BETWEEN 1 AND 64
   OR jsonb_typeof(body->'ratchets')<>'array' OR jsonb_array_length(body->'ratchets') NOT BETWEEN 1 AND 128 THEN RAISE EXCEPTION 'storage_facility_input';END IF;
  SELECT * INTO version FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(catalog,facility) ORDER BY revision DESC LIMIT 1;
  IF version.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid OR effective<version.effective_at THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
  IF header.id IS NULL THEN
   INSERT INTO trading.storage_facilities VALUES(catalog,facility,(body->>'referenceCatalogId')::uuid,body->>'facilityCode',actor,publication,effective);
  ELSIF(header.reference_catalog_id,header.facility_code) IS DISTINCT FROM((body->>'referenceCatalogId')::uuid,body->>'facilityCode')
  THEN RAISE EXCEPTION 'storage_facility_scope';END IF;
  INSERT INTO trading.storage_facility_versions VALUES(catalog,id,facility,coalesce(version.revision,0)+1,version.id,
   (body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,body->>'facilityCode',body->>'commodity',body->>'quantity',body->>'clock',
   (body->>'minimum')::numeric,(body->>'maximum')::numeric,body->>'implementation',body->>'inventoryBasis',body->>'rateBasis',body->>'simultaneousFlow',body->>'lossTreatment',
   btrim(body->>'evidence'),actor,publication,effective);
  FOR item IN SELECT value FROM jsonb_array_elements(body->'ports') LOOP
   PERFORM trading.storage_facility_keys(item,ARRAY['portId','pipeline','zone','point','product','direction','contractDirection']);
   INSERT INTO trading.storage_facility_ports VALUES(catalog,id,(item->>'portId')::uuid,(body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid,
    item->>'product',item->>'pipeline',item->>'zone',item->>'point',item->>'direction',(item->>'contractDirection')::integer);
  END LOOP;
  ordinal=0;
  FOR item IN SELECT value FROM jsonb_array_elements(body->'ratchets') LOOP
   PERFORM trading.storage_facility_keys(item,ARRAY['lower','upper','injectionPerHour','withdrawalPerHour']);
   INSERT INTO trading.storage_facility_ratchets VALUES(catalog,id,ordinal,(item->>'lower')::numeric,(item->>'upper')::numeric,
    (item->>'injectionPerHour')::numeric,(item->>'withdrawalPerHour')::numeric);ordinal=ordinal+1;
  END LOOP;
  result=jsonb_build_object('versionId',id,'revision',coalesce(version.revision,0)+1);
 ELSE
  IF kind='opening' THEN
   PERFORM trading.storage_facility_keys(body,ARRAY['kind','facilityId','expectedStateVersion','businessKey','expectedPreviousId','observedAt','quantity','evidence']);
   SELECT * INTO opening FROM trading.storage_facility_openings o WHERE(o.catalog_id,o.facility_id,o.business_key)=(catalog,facility,body->>'businessKey') ORDER BY revision DESC LIMIT 1;
   IF opening.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
   INSERT INTO trading.storage_facility_openings VALUES(catalog,id,facility,body->>'businessKey',coalesce(opening.revision,0)+1,opening.id,
    trading.storage_facility_at(body->>'observedAt'),(body->>'quantity')::numeric,btrim(body->>'evidence'),actor,publication,effective);
   result=jsonb_build_object('revisionId',id,'revision',coalesce(opening.revision,0)+1);
  ELSIF kind='activity' THEN
   PERFORM trading.storage_facility_keys(body,ARRAY['kind','facilityId','expectedStateVersion','businessKey','expectedPreviousId','versionId','portId','from','through','direction','quantity','sourceRevisionId','evidence']);
   SELECT * INTO activity FROM trading.storage_facility_activities a WHERE(a.catalog_id,a.facility_id,a.business_key)=(catalog,facility,body->>'businessKey') ORDER BY revision DESC LIMIT 1;
   IF activity.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
   SELECT * INTO version FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id,v.id)=(catalog,facility,(body->>'versionId')::uuid)
    AND trading.gas_owned_visible(catalog,v.publication_order,v.effective_at,source_context,effective);
   SELECT * INTO port FROM trading.storage_facility_ports p WHERE(p.catalog_id,p.version_id,p.id)=(catalog,version.id,(body->>'portId')::uuid);
   IF port.id IS NULL OR port.direction IS DISTINCT FROM body->>'direction' THEN RAISE EXCEPTION 'storage_facility_port';END IF;
   IF body->>'sourceRevisionId' IS NOT NULL THEN
    SELECT * INTO source FROM trading.facility_quantity_revisions s WHERE(s.catalog_id,s.id)=(catalog,(body->>'sourceRevisionId')::uuid);
    SELECT * INTO ledger FROM trading.facility_quantity_ledgers l WHERE(l.catalog_id,l.id)=(catalog,source.ledger_id);
    SELECT * INTO gas FROM trading.facility_gas_delivery_scopes g WHERE(g.catalog_id,g.ledger_id)=(catalog,ledger.id);
    IF source.id IS NULL OR body->>'quantity' IS NOT NULL OR NOT trading.gas_owned_visible(catalog,source.publication_order,source.effective_at,source_context,effective)
     OR EXISTS(SELECT 1 FROM trading.facility_quantity_revisions s WHERE(s.catalog_id,s.ledger_id)=(catalog,ledger.id) AND s.revision>source.revision)
     OR(ledger.reference_catalog_id,ledger.reference_revision_id,ledger.quantity_code,ledger.commodity_code,ledger.flow_point_code,ledger.direction,ledger.native_from,ledger.native_to,
       gas.pipeline_code,gas.zone_code) IS DISTINCT FROM(version.reference_catalog_id,version.reference_revision_id,version.quantity_code,version.commodity_code,port.point_code,port.contract_direction,
        trading.storage_facility_at(body->>'from'),trading.storage_facility_at(body->>'through'),port.pipeline_code,port.zone_code)
    THEN RAISE EXCEPTION 'storage_facility_flow_source';END IF;
    INSERT INTO trading.storage_facility_flow_sources VALUES(catalog,ledger.id,facility,body->>'businessKey') ON CONFLICT DO NOTHING;
    IF NOT EXISTS(SELECT 1 FROM trading.storage_facility_flow_sources s WHERE(s.catalog_id,s.ledger_id,s.facility_id,s.business_key)=(catalog,ledger.id,facility,body->>'businessKey'))
    THEN RAISE EXCEPTION 'storage_facility_flow_source';END IF;
   ELSIF EXISTS(SELECT 1 FROM trading.storage_facility_flow_sources s WHERE(s.catalog_id,s.facility_id,s.business_key)=(catalog,facility,body->>'businessKey'))
   THEN RAISE EXCEPTION 'storage_facility_flow_source';END IF;
   INSERT INTO trading.storage_facility_activities VALUES(catalog,id,facility,body->>'businessKey',coalesce(activity.revision,0)+1,activity.id,version.id,port.id,
    trading.storage_facility_at(body->>'from'),trading.storage_facility_at(body->>'through'),body->>'direction',(body->>'quantity')::numeric,(body->>'sourceRevisionId')::uuid,
    btrim(body->>'evidence'),actor,publication,effective);
   result=jsonb_build_object('revisionId',id,'revision',coalesce(activity.revision,0)+1);
  ELSIF kind='coverage' THEN
   PERFORM trading.storage_facility_keys(body,ARRAY['kind','facilityId','expectedStateVersion','businessKey','expectedPreviousId','versionId','openingRevisionId','from','through','statement','activityRevisionIds','evidence']);
   SELECT * INTO coverage FROM trading.storage_facility_coverages c WHERE(c.catalog_id,c.facility_id,c.business_key)=(catalog,facility,body->>'businessKey') ORDER BY revision DESC LIMIT 1;
   IF coverage.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
   IF jsonb_typeof(body->'activityRevisionIds')<>'array' OR jsonb_array_length(body->'activityRevisionIds')>1024 THEN RAISE EXCEPTION 'storage_facility_input';END IF;
   INSERT INTO trading.storage_facility_coverages VALUES(catalog,id,facility,body->>'businessKey',coalesce(coverage.revision,0)+1,coverage.id,(body->>'versionId')::uuid,(body->>'openingRevisionId')::uuid,
    trading.storage_facility_at(body->>'from'),trading.storage_facility_at(body->>'through'),body->>'statement',btrim(body->>'evidence'),actor,publication,effective);
   INSERT INTO trading.storage_facility_covered_activities SELECT catalog,id,facility,value::uuid FROM jsonb_array_elements_text(body->'activityRevisionIds');
   result=jsonb_build_object('revisionId',id,'revision',coalesce(coverage.revision,0)+1);
  ELSE
   PERFORM trading.storage_facility_keys(body,ARRAY['kind','facilityId','expectedStateVersion','planId','expectedPreviousId','versionId','openingRevisionId','from','through','coverageStatement','activities','evidence']);
   IF body->>'coverageStatement' IS DISTINCT FROM 'chosen_plan_idle_between_activities_v1' OR jsonb_typeof(body->'activities')<>'array' OR jsonb_array_length(body->'activities')>1024
   THEN RAISE EXCEPTION 'storage_facility_input';END IF;
   SELECT * INTO plan FROM trading.storage_facility_plans p WHERE(p.catalog_id,p.facility_id)=(catalog,facility) ORDER BY revision DESC LIMIT 1;
   IF plan.id IS DISTINCT FROM(body->>'expectedPreviousId')::uuid THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
   id=(body->>'planId')::uuid;
   INSERT INTO trading.storage_facility_plans VALUES(catalog,id,facility,coalesce(plan.revision,0)+1,plan.id,(body->>'versionId')::uuid,(body->>'openingRevisionId')::uuid,
    trading.storage_facility_at(body->>'from'),trading.storage_facility_at(body->>'through'),btrim(body->>'evidence'),head,actor,publication,effective);
   FOR item IN SELECT value FROM jsonb_array_elements(body->'activities') LOOP
    PERFORM trading.storage_facility_keys(item,ARRAY['revisionId','portId','from','through','direction','quantity']);
    INSERT INTO trading.storage_facility_plan_activities VALUES(catalog,id,(item->>'revisionId')::uuid,(body->>'versionId')::uuid,(item->>'portId')::uuid,
     trading.storage_facility_at(item->>'from'),trading.storage_facility_at(item->>'through'),item->>'direction',(item->>'quantity')::numeric);
   END LOOP;
   IF assessment IS NULL OR assessment='null'::jsonb THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
   INSERT INTO trading.storage_facility_plan_assessments VALUES(catalog,id,assessment);
   PERFORM trading.verify_storage_facility_plan(catalog,id);
   result=jsonb_build_object('revisionId',id,'revision',coalesce(plan.revision,0)+1);
  END IF;
 END IF;
 result=result||jsonb_build_object('facilityId',facility,'kind',kind,'changed',true);
 INSERT INTO trading.storage_facility_commands VALUES(catalog,actor,operation,facility,payload,fact_key,result,deadline,source_context);
 SET CONSTRAINTS trading.valid_storage_facility,trading.valid_storage_facility_commit IMMEDIATE;
 SET CONSTRAINTS trading.valid_storage_facility,trading.valid_storage_facility_commit DEFERRED;
 IF clock_timestamp()>=deadline THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 PERFORM security.storage_facility_authority(catalog,kind);RETURN result;
END$$;

-- The public producer stays in working write context. This rollback boundary
-- certifies its explicit source without turning the write into a historical one.
CREATE FUNCTION trading.require_storage_facility_source(catalog uuid,facility uuid,body jsonb,context uuid,at timestamptz,through_publication integer DEFAULT 2147483647) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE v trading.storage_facility_versions;linked uuid;source_known_at timestamptz;observed_end timestamptz;BEGIN
 IF security.current_write_context() IS NOT TRUE OR context IS NULL THEN RAISE EXCEPTION 'storage_facility_context';END IF;
 SELECT t.known_at INTO source_known_at FROM market_data.temporal_contexts t WHERE(t.catalog_id,t.id)=(catalog,context);
 IF source_known_at IS NULL THEN RAISE EXCEPTION 'storage_facility_context';END IF;
 IF body->>'kind' IN('opening','activity','coverage') THEN
  observed_end=trading.storage_facility_at(body->>CASE WHEN body->>'kind'='opening' THEN 'observedAt' ELSE 'through' END);
  IF observed_end IS NULL OR observed_end>at OR observed_end>source_known_at
  THEN RAISE EXCEPTION 'storage_facility_observation_time';END IF;
 END IF;
 BEGIN
  PERFORM security.use_market_context(catalog,context);PERFORM security.use_trading_context(catalog,at);
  IF trading.storage_facility_head(catalog,facility,true,through_publication)<>trading.storage_facility_head(catalog,facility,false,through_publication)
  THEN RAISE EXCEPTION 'storage_facility_stale';END IF;
  IF body->>'kind'='define' THEN
   IF security.reference_version_visible((body->>'referenceCatalogId')::uuid,(body->>'referenceRevisionId')::uuid) IS NOT TRUE
   THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
  ELSE
   SELECT * INTO v FROM trading.storage_facility_versions x WHERE(x.catalog_id,x.facility_id)=(catalog,facility)
    AND security.trading_visible(catalog,x.publication_order,x.effective_at) AND(body->>'versionId' IS NULL OR x.id=(body->>'versionId')::uuid) ORDER BY revision DESC LIMIT 1;
   IF v.id IS NULL OR security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id) IS NOT TRUE
   THEN RAISE EXCEPTION 'storage_facility_source';END IF;
  END IF;
  IF body->>'sourceRevisionId' IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.facility_quantity_revisions s
   WHERE(s.catalog_id,s.id)=(catalog,(body->>'sourceRevisionId')::uuid) AND trading.gas_owned_visible(catalog,s.publication_order,s.effective_at,context,at))
  THEN RAISE EXCEPTION 'storage_facility_source';END IF;
  FOR linked IN SELECT s.ledger_id FROM trading.storage_facility_visible_activities(catalog,facility) a
   JOIN trading.facility_quantity_revisions s ON(s.catalog_id,s.id)=(a.catalog_id,a.source_revision_id)
   UNION SELECT s.ledger_id FROM trading.facility_quantity_revisions s WHERE(s.catalog_id,s.id)=(catalog,(body->>'sourceRevisionId')::uuid) LOOP
   PERFORM security.read_facility_quantity_ledger(catalog,linked);
  END LOOP;
  RAISE EXCEPTION USING ERRCODE='PZ339',MESSAGE='storage source certified';
 EXCEPTION WHEN SQLSTATE 'PZ339' THEN NULL;END;
END$$;

CREATE FUNCTION security.storage_facility_flow_choices(catalog uuid,selected_facility uuid,search_query text) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE deadline timestamptz;v trading.storage_facility_versions;candidate record;result jsonb='[]';BEGIN
 deadline=security.storage_facility_authority(catalog,NULL);PERFORM security.require_facility_quantity_read(catalog);
 IF search_query IS NULL OR length(search_query)>200 THEN RAISE EXCEPTION 'storage_facility_input';END IF;
 SELECT * INTO v FROM trading.storage_facility_versions x WHERE(x.catalog_id,x.facility_id)=(catalog,selected_facility)
  AND security.trading_visible(catalog,x.publication_order,x.effective_at) ORDER BY revision DESC LIMIT 1;
 IF v.id IS NULL THEN RAISE EXCEPTION 'storage_facility_source';END IF;
 IF security.reference_access(v.reference_catalog_id,'ReadMarketData') IS NOT TRUE
  OR security.reference_version_visible(v.reference_catalog_id,v.reference_revision_id) IS NOT TRUE
 THEN RAISE EXCEPTION 'storage_facility_denied' USING ERRCODE='42501';END IF;
 FOR candidate IN SELECT l.id ledger_id,s.id source_id,s.revision,l.native_from,l.native_to,s.quantity,
   g.day,labels.point,labels.unit,labels.legal_entity FROM trading.facility_quantity_ledgers l JOIN trading.facility_gas_delivery_scopes g ON(g.catalog_id,g.ledger_id)=(l.catalog_id,l.id)
  JOIN trading.facility_quantity_scope_rows labels ON(labels.catalog_id,labels.id)=(l.catalog_id,l.id)
  JOIN LATERAL(SELECT x.* FROM trading.facility_quantity_revisions x WHERE(x.catalog_id,x.ledger_id)=(l.catalog_id,l.id)
   AND security.trading_visible(catalog,x.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00Z') ORDER BY revision DESC LIMIT 1)s ON true
  WHERE(l.catalog_id,l.reference_catalog_id,l.reference_revision_id,l.commodity_code,l.quantity_code)=(catalog,v.reference_catalog_id,v.reference_revision_id,v.commodity_code,v.quantity_code)
   AND security.trading_visible(catalog,l.publication_order,l.effective_at) AND security.trading_visible(catalog,s.publication_order,s.effective_at)
   AND EXISTS(SELECT 1 FROM trading.storage_facility_versions rules JOIN trading.storage_facility_ports p
    ON(p.catalog_id,p.version_id,p.point_code,p.contract_direction,p.pipeline_code,p.zone_code)=
     (l.catalog_id,rules.id,l.flow_point_code,l.direction,g.pipeline_code,g.zone_code)
    WHERE(rules.catalog_id,rules.facility_id,rules.reference_catalog_id,rules.reference_revision_id,rules.quantity_code,rules.commodity_code,rules.clock_code)=
     (catalog,selected_facility,v.reference_catalog_id,v.reference_revision_id,v.quantity_code,v.commodity_code,v.clock_code)
     AND security.trading_visible(catalog,rules.publication_order,rules.effective_at))
   AND NOT EXISTS(SELECT 1 FROM trading.storage_facility_flow_sources linked WHERE(linked.catalog_id,linked.ledger_id)=(catalog,l.id) AND linked.facility_id<>selected_facility
    AND EXISTS(SELECT 1 FROM trading.storage_facility_visible_activities(catalog,linked.facility_id) a
     JOIN trading.facility_quantity_revisions covered ON(covered.catalog_id,covered.id)=(a.catalog_id,a.source_revision_id) WHERE covered.ledger_id=linked.ledger_id))
   AND NOT EXISTS(SELECT 1 FROM regexp_split_to_table(btrim(search_query),'\s+')term WHERE strpos(lower(concat_ws(' ',
    labels.point,l.flow_point_code,g.pipeline_code,g.zone_code,g.day::text,s.id::text,l.id::text)),lower(term))=0)
  ORDER BY l.native_from DESC,l.id LIMIT 201 LOOP
  PERFORM security.read_facility_quantity_ledger(catalog,candidate.ledger_id);
  result=result||jsonb_build_array(jsonb_build_object('ledgerId',candidate.ledger_id,'sourceRevisionId',candidate.source_id,
   'from',candidate.native_from,'through',candidate.native_to,'quantity',trim_scale(candidate.quantity)::text,
   'label',candidate.point||' · '||candidate.legal_entity||' · '||candidate.day::text||' · '||trim_scale(candidate.quantity)::text||' '||candidate.unit||' · revision '||candidate.revision::text));
 END LOOP;
 PERFORM security.require_storage_facility_commit(catalog,NULL,deadline);RETURN result;
END$$;
CREATE FUNCTION security.storage_facility_flow_choices(catalog uuid,selected_facility uuid) RETURNS jsonb
 LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.storage_facility_flow_choices(catalog,selected_facility,'');
$$;
CREATE FUNCTION trading.storage_ratio(n numeric,d numeric) RETURNS numeric[] LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE factor numeric;BEGIN
 IF n IS NULL OR d IS NULL OR n<>trunc(n) OR d<>trunc(d) OR d<=0 OR length(n::text)>4096 OR length(d::text)>4096
 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
 factor=gcd(abs(n),d);RETURN ARRAY[n/factor,d/factor];
END$$;
CREATE FUNCTION trading.storage_decimal(q numeric) RETURNS numeric[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trading.storage_ratio(q*power(10::numeric,scale(q)),power(10::numeric,scale(q)));
$$;
CREATE FUNCTION trading.storage_ratio_read(q jsonb) RETURNS numeric[] LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
BEGIN
 PERFORM trading.storage_facility_keys(q,ARRAY['n','d']);
 IF jsonb_typeof(q->'n')<>'string' OR jsonb_typeof(q->'d')<>'string' OR q->>'n'!~'^-?[0-9]+$' OR q->>'d'!~'^[0-9]+$'
 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
 RETURN trading.storage_ratio((q->>'n')::numeric,(q->>'d')::numeric);
END$$;
CREATE FUNCTION trading.storage_ratio_add(a numeric[],b numeric[]) RETURNS numeric[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trading.storage_ratio(a[1]*b[2]+b[1]*a[2],a[2]*b[2]);
$$;
CREATE FUNCTION trading.storage_ratio_multiply(a numeric[],b numeric[]) RETURNS numeric[] LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT trading.storage_ratio(a[1]*b[1],a[2]*b[2]);
$$;
CREATE FUNCTION trading.storage_ratio_compare(a numeric[],b numeric[]) RETURNS integer LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT sign(a[1]*b[2]-b[1]*a[2])::integer;
$$;

CREATE FUNCTION trading.require_storage_facility_plan_rules(catalog uuid,facility uuid,version uuid,at timestamptz,from_at timestamptz,through_at timestamptz,source_head integer) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id,v.id)=(catalog,facility,version)
  AND v.effective_at<=from_at AND v.publication_order<=source_head)
 THEN RAISE EXCEPTION 'storage_facility_rule_transition';END IF;
 IF version IS DISTINCT FROM(SELECT v.id FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(catalog,facility)
   AND v.publication_order<=source_head AND v.effective_at<=at ORDER BY revision DESC LIMIT 1)
 THEN RAISE EXCEPTION 'storage_facility_rule_transition';END IF;
 IF EXISTS(SELECT 1 FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id)=(catalog,facility)
  AND v.publication_order<=source_head AND v.revision>(SELECT selected.revision FROM trading.storage_facility_versions selected WHERE(selected.catalog_id,selected.id)=(catalog,version))
  AND v.effective_at<through_at)
 THEN RAISE EXCEPTION 'storage_facility_rule_transition';END IF;
END$$;

CREATE FUNCTION trading.verify_storage_facility_plan(catalog uuid,selected_plan uuid) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE p trading.storage_facility_plans;v trading.storage_facility_versions;o trading.storage_facility_openings;
 witness jsonb;segment jsonb;times timestamptz[];i integer;start_at timestamptz;end_at timestamptz;
 level numeric[];closing numeric[];injected numeric[];withdrawn numeric[];rate numeric[];low numeric[];high numeric[];
 active jsonb;bands jsonb;row record;BEGIN
 SELECT * INTO STRICT p FROM trading.storage_facility_plans WHERE(catalog_id,id)=(catalog,selected_plan);
 SELECT * INTO STRICT v FROM trading.storage_facility_versions WHERE(catalog_id,id)=(catalog,p.version_id);
 SELECT * INTO STRICT o FROM trading.storage_facility_openings WHERE(catalog_id,id)=(catalog,p.opening_revision_id);
 IF p.source_head<>trading.storage_facility_head(catalog,p.facility_id,false,p.publication_order-1)
 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
 PERFORM trading.require_storage_facility_plan_rules(catalog,p.facility_id,p.version_id,p.effective_at,p.from_at,p.through_at,p.source_head);
 IF o.observed_at<>p.from_at OR o.publication_order>p.source_head OR o.effective_at>p.effective_at
  OR EXISTS(SELECT 1 FROM trading.storage_facility_openings n WHERE(n.catalog_id,n.facility_id,n.business_key)=(catalog,p.facility_id,o.business_key)
   AND n.revision>o.revision AND n.publication_order<=p.source_head)
  OR EXISTS(SELECT 1 FROM trading.storage_facility_plan_activities a JOIN trading.storage_facility_ports port
   ON(port.catalog_id,port.version_id,port.id)=(a.catalog_id,a.version_id,a.port_id) WHERE(a.catalog_id,a.plan_id)=(catalog,selected_plan)
   AND(a.version_id<>p.version_id OR a.direction<>port.direction OR a.from_at<p.from_at OR a.through_at>p.through_at))
 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
 SELECT a.witness INTO witness FROM trading.storage_facility_plan_assessments a WHERE(a.catalog_id,a.plan_id)=(catalog,selected_plan);
 IF witness IS NULL THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
 PERFORM trading.storage_facility_keys(witness,ARRAY['implementation','rulesVersion','openingRevision','coverageRevision','from','through','constraint','closing','intervals']);
 IF witness->>'implementation' IS DISTINCT FROM 'storage_assessment_witness_v1' OR witness->>'constraint' IS DISTINCT FROM 'within_limit'
  OR(witness->>'rulesVersion')::uuid IS DISTINCT FROM p.version_id OR(witness->>'openingRevision')::uuid IS DISTINCT FROM o.id
  OR(witness->>'coverageRevision')::uuid IS DISTINCT FROM p.id OR trading.storage_facility_at(witness->>'from') IS DISTINCT FROM p.from_at
  OR trading.storage_facility_at(witness->>'through') IS DISTINCT FROM p.through_at OR jsonb_typeof(witness->'intervals')<>'array'
 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
 SELECT array_agg(t ORDER BY t) INTO times FROM(SELECT p.from_at t UNION SELECT p.through_at
  UNION SELECT a.from_at FROM trading.storage_facility_plan_activities a WHERE(a.catalog_id,a.plan_id)=(catalog,selected_plan)
  UNION SELECT a.through_at FROM trading.storage_facility_plan_activities a WHERE(a.catalog_id,a.plan_id)=(catalog,selected_plan))s;
 IF jsonb_array_length(witness->'intervals')<>cardinality(times)-1 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
 level=trading.storage_decimal(o.quantity);
 FOR i IN 2..cardinality(times) LOOP
  start_at=times[i-1];end_at=times[i];segment=witness->'intervals'->(i-2);
  PERFORM trading.storage_facility_keys(segment,ARRAY['from','through','activities','opening','closing','injectionPerHour','withdrawalPerHour','bands']);
  IF trading.storage_facility_at(segment->>'from') IS DISTINCT FROM start_at OR trading.storage_facility_at(segment->>'through') IS DISTINCT FROM end_at
   OR trading.storage_ratio_compare(level,trading.storage_ratio_read(segment->'opening'))<>0 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
  injected=ARRAY[0::numeric,1];withdrawn=ARRAY[0::numeric,1];active='[]';
  FOR row IN SELECT * FROM trading.storage_facility_plan_activities a WHERE(a.catalog_id,a.plan_id)=(catalog,selected_plan)
   AND a.from_at<=start_at AND a.through_at>=end_at ORDER BY a.id LOOP
   active=active||jsonb_build_array(row.id);
   rate=trading.storage_ratio_multiply(trading.storage_decimal(row.quantity),trading.storage_ratio(3600000000,extract(epoch FROM row.through_at-row.from_at)*1000000));
   IF row.direction='inject' THEN injected=trading.storage_ratio_add(injected,rate);ELSE withdrawn=trading.storage_ratio_add(withdrawn,rate);END IF;
  END LOOP;
  IF segment->'activities' IS DISTINCT FROM active OR trading.storage_ratio_compare(injected,trading.storage_ratio_read(segment->'injectionPerHour'))<>0
   OR trading.storage_ratio_compare(withdrawn,trading.storage_ratio_read(segment->'withdrawalPerHour'))<>0 OR injected[1]>0 AND withdrawn[1]>0
  THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
  closing=trading.storage_ratio_add(level,trading.storage_ratio_multiply(
   trading.storage_ratio_add(injected,ARRAY[-withdrawn[1],withdrawn[2]]),trading.storage_ratio(extract(epoch FROM end_at-start_at)*1000000,3600000000)));
  IF trading.storage_ratio_compare(closing,trading.storage_ratio_read(segment->'closing'))<>0
   OR trading.storage_ratio_compare(level,trading.storage_decimal(v.minimum))<0 OR trading.storage_ratio_compare(level,trading.storage_decimal(v.maximum))>0
   OR trading.storage_ratio_compare(closing,trading.storage_decimal(v.minimum))<0 OR trading.storage_ratio_compare(closing,trading.storage_decimal(v.maximum))>0
  THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
  IF trading.storage_ratio_compare(level,closing)<=0 THEN low=level;high=closing;ELSE low=closing;high=level;END IF;
  bands='[]';
  FOR row IN SELECT * FROM trading.storage_facility_ratchets b WHERE(b.catalog_id,b.version_id)=(catalog,v.id) ORDER BY ordinal LOOP
   IF trading.storage_ratio_compare(low,high)<>0 AND trading.storage_ratio_compare(low,trading.storage_decimal(row.upper_level))<0
    AND trading.storage_ratio_compare(high,trading.storage_decimal(row.lower_level))>0
    OR trading.storage_ratio_compare(low,high)=0 AND trading.storage_ratio_compare(low,trading.storage_decimal(row.lower_level))>=0
     AND(trading.storage_ratio_compare(low,trading.storage_decimal(row.upper_level))<0 OR trading.storage_ratio_compare(low,trading.storage_decimal(v.maximum))=0
      AND row.upper_level=v.maximum)
   THEN
    bands=bands||jsonb_build_array(row.ordinal);
    IF trading.storage_ratio_compare(injected,trading.storage_decimal(row.injection_per_hour))>0
     OR trading.storage_ratio_compare(withdrawn,trading.storage_decimal(row.withdrawal_per_hour))>0 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
   END IF;
  END LOOP;
  IF segment->'bands' IS DISTINCT FROM bands THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
  level=closing;
 END LOOP;
 IF trading.storage_ratio_compare(level,trading.storage_ratio_read(witness->'closing'))<>0 THEN RAISE EXCEPTION 'storage_facility_witness';END IF;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR null_value_not_allowed OR division_by_zero THEN
 RAISE EXCEPTION 'storage_facility_witness';
 WHEN raise_exception THEN
  IF SQLERRM='storage_facility_rule_transition' THEN RAISE;END IF;
  RAISE EXCEPTION 'storage_facility_witness';
END$$;
CREATE FUNCTION trading.guard_storage_facility() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE catalog uuid=NEW.catalog_id;facility uuid;fact_id uuid;kind text;actor uuid;publication integer;effective timestamptz;
 v trading.storage_facility_versions;previous trading.storage_facility_versions;p trading.storage_facility_plans;
 c trading.storage_facility_coverages;a trading.storage_facility_activities;h trading.storage_facility_openings;
 s trading.facility_quantity_revisions;l trading.facility_quantity_ledgers;port trading.storage_facility_ports;g trading.facility_gas_delivery_scopes;
 old_id uuid;wanted jsonb;actual jsonb;BEGIN
 CASE TG_TABLE_NAME
 WHEN 'storage_facilities' THEN
  facility=NEW.id;kind='define';actor=NEW.actor_id;publication=NEW.publication_order;effective=NEW.effective_at;
  SELECT x.id INTO fact_id FROM trading.storage_facility_versions x WHERE(x.catalog_id,x.facility_id,x.revision,x.publication_order,x.actor_id,x.effective_at)=(catalog,facility,1,publication,actor,effective);
  IF fact_id IS NULL THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
 WHEN 'storage_facility_versions' THEN
  v=NEW;facility=v.facility_id;fact_id=v.id;kind='define';actor=v.actor_id;publication=v.publication_order;effective=v.effective_at;
 WHEN 'storage_facility_ports','storage_facility_ratchets' THEN
  SELECT * INTO STRICT v FROM trading.storage_facility_versions WHERE(catalog_id,id)=(catalog,NEW.version_id);
  facility=v.facility_id;fact_id=v.id;kind='define';actor=v.actor_id;publication=v.publication_order;effective=v.effective_at;
 WHEN 'storage_facility_openings' THEN
  h=NEW;facility=h.facility_id;fact_id=h.id;kind='opening';actor=h.actor_id;publication=h.publication_order;effective=h.effective_at;
 WHEN 'storage_facility_activities' THEN
  a=NEW;facility=a.facility_id;fact_id=a.id;kind='activity';actor=a.actor_id;publication=a.publication_order;effective=a.effective_at;
 WHEN 'storage_facility_flow_sources' THEN
  SELECT x.* INTO a FROM trading.storage_facility_activities x JOIN trading.facility_quantity_revisions r ON(r.catalog_id,r.id)=(x.catalog_id,x.source_revision_id)
   WHERE(x.catalog_id,x.facility_id,x.business_key,r.ledger_id)=(catalog,NEW.facility_id,NEW.business_key,NEW.ledger_id) ORDER BY x.revision DESC LIMIT 1;
  facility=a.facility_id;fact_id=a.id;kind='activity';actor=a.actor_id;publication=a.publication_order;effective=a.effective_at;
 WHEN 'storage_facility_coverages' THEN
  c=NEW;facility=c.facility_id;fact_id=c.id;kind='coverage';actor=c.actor_id;publication=c.publication_order;effective=c.effective_at;
 WHEN 'storage_facility_covered_activities' THEN
  SELECT * INTO STRICT c FROM trading.storage_facility_coverages WHERE(catalog_id,id)=(catalog,NEW.coverage_id);
  facility=c.facility_id;fact_id=c.id;kind='coverage';actor=c.actor_id;publication=c.publication_order;effective=c.effective_at;
 WHEN 'storage_facility_plans' THEN
  p=NEW;facility=p.facility_id;fact_id=p.id;kind='plan';actor=p.actor_id;publication=p.publication_order;effective=p.effective_at;
 WHEN 'storage_facility_plan_activities','storage_facility_plan_assessments' THEN
  SELECT * INTO STRICT p FROM trading.storage_facility_plans WHERE(catalog_id,id)=(catalog,NEW.plan_id);
  facility=p.facility_id;fact_id=p.id;kind='plan';actor=p.actor_id;publication=p.publication_order;effective=p.effective_at;
 ELSE RAISE EXCEPTION 'storage_facility_integrity';END CASE;
 PERFORM security.storage_facility_authority(catalog,kind);
 IF facility IS NULL OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid()
  AND q.transaction_id=pg_current_xact_id_if_assigned() AND q.runtime_login=session_user AND q.actor_id=actor AND q.expires_at>clock_timestamp())
  OR NOT EXISTS(SELECT 1 FROM trading.publications x WHERE(x.catalog_id,x.publication_order,x.publication_xid)=(catalog,publication,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.storage_facility_commands command WHERE(command.catalog_id,command.facility_id,command.actor_id)=(catalog,facility,actor)
  AND command.request#>>'{body,kind}'=kind AND command.outcome->>'changed'='true'
  AND command.outcome->>CASE WHEN kind='define' THEN 'versionId' ELSE 'revisionId' END=fact_id::text
  AND command.fact=trading.storage_facility_recorded_fact(catalog,facility,kind,fact_id))
 THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
 IF v.id IS NOT NULL THEN
  SELECT * INTO previous FROM trading.storage_facility_versions x WHERE(x.catalog_id,x.facility_id,x.revision)=(catalog,facility,v.revision-1);
  IF v.previous_id IS DISTINCT FROM previous.id OR v.effective_at<previous.effective_at
   OR previous.id IS NOT NULL AND(v.reference_catalog_id,v.reference_revision_id,v.commodity_code,v.quantity_code,v.clock_code)
    IS DISTINCT FROM(previous.reference_catalog_id,previous.reference_revision_id,previous.commodity_code,previous.quantity_code,previous.clock_code)
  THEN RAISE EXCEPTION 'storage_facility_scope_change';END IF;
  IF(SELECT count(*) FROM trading.storage_facility_ports WHERE(catalog_id,version_id)=(catalog,v.id)) NOT BETWEEN 1 AND 64
   OR(SELECT count(*) FROM trading.storage_facility_ratchets WHERE(catalog_id,version_id)=(catalog,v.id)) NOT BETWEEN 1 AND 128
   OR EXISTS(SELECT 1 FROM(SELECT b.*,lag(upper_level,1,v.minimum) OVER(ORDER BY ordinal) expected_lower,
    row_number() OVER(ORDER BY ordinal)-1 expected_ordinal FROM trading.storage_facility_ratchets b WHERE(b.catalog_id,b.version_id)=(catalog,v.id))b
    WHERE b.ordinal<>expected_ordinal OR b.lower_level<>expected_lower OR b.upper_level>v.maximum)
   OR(SELECT max(upper_level) FROM trading.storage_facility_ratchets WHERE(catalog_id,version_id)=(catalog,v.id))<>v.maximum
  THEN RAISE EXCEPTION 'storage_facility_ratchets';END IF;
  IF EXISTS(SELECT 1 FROM trading.storage_facility_ports connection JOIN ref_data.gas_delivery_conventions d
   ON(d.catalog_id,d.revision_id,d.pipeline_code,d.zone_code,d.point_code,d.product_code)=
    (connection.reference_catalog_id,connection.reference_revision_id,connection.pipeline_code,connection.zone_code,connection.point_code,connection.product_code)
   WHERE(connection.catalog_id,connection.version_id)=(catalog,v.id) AND d.clock_code<>v.clock_code)
  THEN RAISE EXCEPTION 'storage_facility_clock';END IF;
 END IF;
 IF h.id IS NOT NULL THEN
  SELECT id INTO old_id FROM trading.storage_facility_openings x WHERE(x.catalog_id,x.facility_id,x.business_key,x.revision)=(catalog,facility,h.business_key,h.revision-1);
  IF h.previous_id IS DISTINCT FROM old_id THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
 END IF;
 IF a.id IS NOT NULL THEN
  SELECT id INTO old_id FROM trading.storage_facility_activities x WHERE(x.catalog_id,x.facility_id,x.business_key,x.revision)=(catalog,facility,a.business_key,a.revision-1);
  IF a.previous_id IS DISTINCT FROM old_id THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
  SELECT * INTO STRICT v FROM trading.storage_facility_versions WHERE(catalog_id,id)=(catalog,a.version_id);
  SELECT * INTO STRICT port FROM trading.storage_facility_ports WHERE(catalog_id,version_id,id)=(catalog,a.version_id,a.port_id);
  IF a.direction<>port.direction OR v.publication_order>a.publication_order OR v.effective_at>a.effective_at
  THEN RAISE EXCEPTION 'storage_facility_port';END IF;
  IF a.source_revision_id IS NOT NULL THEN
   SELECT * INTO STRICT s FROM trading.facility_quantity_revisions WHERE(catalog_id,id)=(catalog,a.source_revision_id);
   SELECT * INTO STRICT l FROM trading.facility_quantity_ledgers WHERE(catalog_id,id)=(catalog,s.ledger_id);
   SELECT * INTO STRICT g FROM trading.facility_gas_delivery_scopes WHERE(catalog_id,ledger_id)=(catalog,l.id);
   IF s.publication_order>a.publication_order OR s.effective_at>a.effective_at
    OR EXISTS(SELECT 1 FROM trading.facility_quantity_revisions n WHERE(n.catalog_id,n.ledger_id)=(catalog,l.id) AND n.revision>s.revision AND n.publication_order<=a.publication_order)
    OR(l.reference_catalog_id,l.reference_revision_id,l.quantity_code,l.commodity_code,l.flow_point_code,l.direction,l.native_from,l.native_to,g.pipeline_code,g.zone_code)
     IS DISTINCT FROM(v.reference_catalog_id,v.reference_revision_id,v.quantity_code,v.commodity_code,port.point_code,port.contract_direction,a.from_at,a.through_at,port.pipeline_code,port.zone_code)
    OR NOT EXISTS(SELECT 1 FROM trading.storage_facility_flow_sources f WHERE(f.catalog_id,f.ledger_id,f.facility_id,f.business_key)=(catalog,l.id,facility,a.business_key))
   THEN RAISE EXCEPTION 'storage_facility_flow_source';END IF;
   PERFORM security.require_facility_plan_targets(catalog,(SELECT id FROM trading.facility_quantity_plans WHERE(catalog_id,ledger_id)=(catalog,l.id) ORDER BY revision DESC LIMIT 1),NULL);
  ELSIF EXISTS(SELECT 1 FROM trading.storage_facility_flow_sources f WHERE(f.catalog_id,f.facility_id,f.business_key)=(catalog,facility,a.business_key))
  THEN RAISE EXCEPTION 'storage_facility_flow_source';END IF;
 END IF;
 IF c.id IS NOT NULL THEN
  SELECT id INTO old_id FROM trading.storage_facility_coverages x WHERE(x.catalog_id,x.facility_id,x.business_key,x.revision)=(catalog,facility,c.business_key,c.revision-1);
  IF c.previous_id IS DISTINCT FROM old_id THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
  IF c.opening_revision_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM trading.storage_facility_openings o WHERE(o.catalog_id,o.facility_id,o.id,o.observed_at)=(catalog,facility,c.opening_revision_id,c.from_at)
   AND o.publication_order<=c.publication_order AND o.effective_at<=c.effective_at)
  THEN RAISE EXCEPTION 'storage_facility_source';END IF;
  SELECT coalesce(jsonb_agg(activity_id ORDER BY activity_id),'[]') INTO actual FROM trading.storage_facility_covered_activities x WHERE(x.catalog_id,x.coverage_id)=(catalog,c.id);
  SELECT coalesce(jsonb_agg(id ORDER BY id),'[]') INTO wanted FROM(SELECT DISTINCT ON(business_key)x.* FROM trading.storage_facility_activities x
   WHERE(x.catalog_id,x.facility_id)=(catalog,facility) AND x.publication_order<=c.publication_order ORDER BY business_key,revision DESC)heads
   WHERE heads.from_at>=c.from_at AND heads.through_at<=c.through_at AND heads.effective_at<=c.effective_at;
  IF actual<>wanted OR EXISTS(SELECT 1 FROM trading.storage_facility_covered_activities x JOIN trading.storage_facility_activities movement ON(movement.catalog_id,movement.id)=(x.catalog_id,x.activity_id)
   WHERE(x.catalog_id,x.coverage_id)=(catalog,c.id) AND(movement.version_id<>c.version_id OR movement.from_at<c.from_at OR movement.through_at>c.through_at OR movement.publication_order>c.publication_order))
  THEN RAISE EXCEPTION 'storage_facility_coverage';END IF;
  IF c.statement='complete_observed_window_v1' AND EXISTS(SELECT 1 FROM(SELECT DISTINCT ON(business_key)movement.* FROM trading.storage_facility_activities movement
   WHERE(movement.catalog_id,movement.facility_id)=(catalog,facility) AND movement.publication_order<=c.publication_order ORDER BY business_key,revision DESC)heads
   WHERE heads.from_at<c.through_at AND heads.through_at>c.from_at AND(heads.from_at<c.from_at OR heads.through_at>c.through_at))
  THEN RAISE EXCEPTION 'storage_facility_coverage';END IF;
 END IF;
 IF p.id IS NOT NULL THEN
  SELECT id INTO old_id FROM trading.storage_facility_plans x WHERE(x.catalog_id,x.facility_id,x.revision)=(catalog,facility,p.revision-1);
  IF p.previous_id IS DISTINCT FROM old_id THEN RAISE EXCEPTION 'storage_facility_conflict';END IF;
  PERFORM trading.verify_storage_facility_plan(catalog,p.id);
 END IF;
 RETURN NULL;
END$$;
-- Receipts attest these typed records and their members. This comparison does
-- not trust a producer-supplied fact or repeat its numerical assessment.
CREATE FUNCTION trading.storage_facility_recorded_fact(catalog uuid,facility uuid,kind text,selected uuid) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE body jsonb;recorded_at timestamptz;BEGIN
 CASE kind
 WHEN 'define' THEN
  SELECT jsonb_build_object('referenceCatalogId',v.reference_catalog_id,'referenceRevisionId',v.reference_revision_id,
   'facilityCode',v.facility_code,'commodity',v.commodity_code,'quantity',v.quantity_code,'clock',v.clock_code,
   'minimum',v.minimum::text,'maximum',v.maximum::text,'implementation',v.implementation,'inventoryBasis',v.inventory_basis,
   'rateBasis',v.rate_basis,'simultaneousFlow',v.simultaneous_flow,'lossTreatment',v.loss_treatment,'evidence',v.evidence,
   'ports',(SELECT jsonb_agg(jsonb_build_object('portId',p.id,'pipeline',p.pipeline_code,'zone',p.zone_code,'point',p.point_code,
    'product',p.product_code,'direction',p.direction,'contractDirection',p.contract_direction) ORDER BY p.id)
     FROM trading.storage_facility_ports p WHERE(p.catalog_id,p.version_id)=(catalog,v.id)),
   'ratchets',(SELECT jsonb_agg(jsonb_build_object('lower',r.lower_level::text,'upper',r.upper_level::text,
    'injectionPerHour',r.injection_per_hour::text,'withdrawalPerHour',r.withdrawal_per_hour::text) ORDER BY r.ordinal)
     FROM trading.storage_facility_ratchets r WHERE(r.catalog_id,r.version_id)=(catalog,v.id))),v.effective_at INTO body,recorded_at
   FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id,v.id)=(catalog,facility,selected);
 WHEN 'opening' THEN
  SELECT jsonb_build_object('businessKey',o.business_key,'observedAt',o.observed_at,'quantity',o.quantity::text,'evidence',o.evidence),o.effective_at
   INTO body,recorded_at FROM trading.storage_facility_openings o WHERE(o.catalog_id,o.facility_id,o.id)=(catalog,facility,selected);
 WHEN 'activity' THEN
  SELECT jsonb_build_object('businessKey',a.business_key,'versionId',a.version_id,'portId',a.port_id,'from',a.from_at,'through',a.through_at,
   'direction',a.direction,'quantity',a.quantity::text,'sourceRevisionId',a.source_revision_id,'evidence',a.evidence),a.effective_at
   INTO body,recorded_at FROM trading.storage_facility_activities a WHERE(a.catalog_id,a.facility_id,a.id)=(catalog,facility,selected);
 WHEN 'coverage' THEN
  SELECT jsonb_build_object('businessKey',c.business_key,'versionId',c.version_id,'openingRevisionId',c.opening_revision_id,'from',c.from_at,'through',c.through_at,
   'statement',c.statement,'evidence',c.evidence,'activityRevisionIds',(SELECT coalesce(jsonb_agg(x.activity_id ORDER BY x.activity_id),'[]')
    FROM trading.storage_facility_covered_activities x WHERE(x.catalog_id,x.coverage_id)=(catalog,c.id))),c.effective_at
   INTO body,recorded_at FROM trading.storage_facility_coverages c WHERE(c.catalog_id,c.facility_id,c.id)=(catalog,facility,selected);
 WHEN 'plan' THEN
  SELECT jsonb_build_object('versionId',p.version_id,'openingRevisionId',p.opening_revision_id,'from',p.from_at,'through',p.through_at,
   'coverageStatement','chosen_plan_idle_between_activities_v1','evidence',p.evidence,
   'activities',(SELECT coalesce(jsonb_agg(jsonb_build_object('revisionId',a.id,'portId',a.port_id,'from',a.from_at,'through',a.through_at,
    'direction',a.direction,'quantity',a.quantity::text) ORDER BY a.id),'[]') FROM trading.storage_facility_plan_activities a WHERE(a.catalog_id,a.plan_id)=(catalog,p.id))),p.effective_at
   INTO body,recorded_at FROM trading.storage_facility_plans p WHERE(p.catalog_id,p.facility_id,p.id)=(catalog,facility,selected);
 ELSE RAISE EXCEPTION 'storage_facility_integrity';END CASE;
 IF body IS NULL THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
 body=trading.storage_facility_fact(body||jsonb_build_object('kind',kind,'facilityId',facility));
 RETURN jsonb_build_object('body',trading.storage_facility_fact_key(body),
  'effectiveAt',to_char(recorded_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'));
END$$;
CREATE FUNCTION trading.guard_storage_facility_commit() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE kind text=NEW.request#>>'{body,kind}';native_publication integer;previous uuid;BEGIN
 IF kind IS NULL OR kind NOT IN('define','opening','activity','coverage','plan')
  OR NEW.request#>>'{body,facilityId}' IS DISTINCT FROM NEW.facility_id::text OR NEW.outcome->>'facilityId' IS DISTINCT FROM NEW.facility_id::text
  OR NEW.outcome->>'kind' IS DISTINCT FROM kind OR jsonb_typeof(NEW.outcome->'changed') IS DISTINCT FROM 'boolean'
  OR NEW.request->>'contextId' IS DISTINCT FROM NEW.source_context_id::text
  OR NOT EXISTS(SELECT 1 FROM security.request_contexts q WHERE q.backend_pid=pg_backend_pid() AND q.transaction_id=pg_current_xact_id_if_assigned()
   AND q.runtime_login=session_user AND q.actor_id=NEW.actor_id AND q.expires_at>clock_timestamp())
 THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
 CASE kind
 WHEN 'define' THEN SELECT v.publication_order,v.previous_id INTO native_publication,previous FROM trading.storage_facility_versions v WHERE(v.catalog_id,v.facility_id,v.id,v.revision)=
  (NEW.catalog_id,NEW.facility_id,(NEW.outcome->>'versionId')::uuid,(NEW.outcome->>'revision')::integer);
 WHEN 'opening' THEN SELECT v.publication_order,v.previous_id INTO native_publication,previous FROM trading.storage_facility_openings v WHERE(v.catalog_id,v.facility_id,v.id,v.revision)=
  (NEW.catalog_id,NEW.facility_id,(NEW.outcome->>'revisionId')::uuid,(NEW.outcome->>'revision')::integer);
 WHEN 'activity' THEN SELECT v.publication_order,v.previous_id INTO native_publication,previous FROM trading.storage_facility_activities v WHERE(v.catalog_id,v.facility_id,v.id,v.revision)=
  (NEW.catalog_id,NEW.facility_id,(NEW.outcome->>'revisionId')::uuid,(NEW.outcome->>'revision')::integer);
 WHEN 'coverage' THEN SELECT v.publication_order,v.previous_id INTO native_publication,previous FROM trading.storage_facility_coverages v WHERE(v.catalog_id,v.facility_id,v.id,v.revision)=
  (NEW.catalog_id,NEW.facility_id,(NEW.outcome->>'revisionId')::uuid,(NEW.outcome->>'revision')::integer);
 ELSE SELECT v.publication_order,v.previous_id INTO native_publication,previous FROM trading.storage_facility_plans v WHERE(v.catalog_id,v.facility_id,v.id,v.revision)=
  (NEW.catalog_id,NEW.facility_id,(NEW.outcome->>'revisionId')::uuid,(NEW.outcome->>'revision')::integer);END CASE;
 IF native_publication IS NULL THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
 IF NEW.fact IS DISTINCT FROM jsonb_build_object('body',trading.storage_facility_fact_key(trading.storage_facility_fact(NEW.request->'body')),
    'effectiveAt',to_char(trading.storage_facility_at(NEW.request->>'effectiveAt') AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"'))
  OR NEW.fact IS DISTINCT FROM trading.storage_facility_recorded_fact(NEW.catalog_id,NEW.facility_id,kind,
   (NEW.outcome->>CASE WHEN kind='define' THEN 'versionId' ELSE 'revisionId' END)::uuid)
 THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
 IF(NEW.outcome->>'changed')::boolean THEN
  IF NOT EXISTS(SELECT 1 FROM trading.publications p WHERE(p.catalog_id,p.publication_order,p.publication_xid)=(NEW.catalog_id,native_publication,pg_current_xact_id()))
   OR(NEW.request#>>'{body,expectedStateVersion}')::integer IS DISTINCT FROM trading.storage_facility_head(NEW.catalog_id,NEW.facility_id,false,native_publication-1)
   OR(NEW.request#>>'{body,expectedPreviousId}')::uuid IS DISTINCT FROM previous
   OR trading.gas_owned_visible(NEW.catalog_id,native_publication,TIMESTAMPTZ '0001-01-01 00:00:00Z',NEW.source_context_id,trading.storage_facility_at(NEW.request->>'effectiveAt'))
  THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
  PERFORM trading.require_storage_facility_source(NEW.catalog_id,NEW.facility_id,NEW.request->'body',NEW.source_context_id,
   trading.storage_facility_at(NEW.request->>'effectiveAt'),native_publication-1);
 ELSIF NOT EXISTS(SELECT 1 FROM trading.storage_facility_commands c WHERE(c.catalog_id,c.facility_id,c.fact,c.source_context_id)=
   (NEW.catalog_id,NEW.facility_id,NEW.fact,NEW.source_context_id) AND(c.actor_id,c.operation_id) IS DISTINCT FROM(NEW.actor_id,NEW.operation_id)
   AND c.outcome->>'changed'='true' AND c.outcome-'changed'=NEW.outcome-'changed')
 THEN RAISE EXCEPTION 'storage_facility_integrity';END IF;
 PERFORM trading.require_storage_facility_reference(NEW.catalog_id,NEW.facility_id);
 PERFORM security.require_storage_facility_commit(NEW.catalog_id,NEW.request#>>'{body,kind}',NEW.authority_until);
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER valid_storage_facility_commit AFTER INSERT ON trading.storage_facility_commands
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_storage_facility_commit();
-- Keep the original deadline pending until the supported caller commits. The
-- API also performs its independent original-admission check before commit.
CREATE FUNCTION trading.guard_storage_facility_deadline() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_storage_facility_commit(NEW.catalog_id,NEW.request#>>'{body,kind}',NEW.authority_until);
 RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER valid_storage_facility_deadline AFTER INSERT ON trading.storage_facility_commands
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_storage_facility_deadline();
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['storage_facilities','storage_facility_versions','storage_facility_ports','storage_facility_ratchets','storage_facility_openings',
  'storage_facility_activities','storage_facility_flow_sources','storage_facility_coverages','storage_facility_covered_activities',
  'storage_facility_plans','storage_facility_plan_activities','storage_facility_plan_assessments'] LOOP
  EXECUTE format('CREATE CONSTRAINT TRIGGER valid_storage_facility AFTER INSERT ON trading.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.guard_storage_facility()',t);
 END LOOP;
END$$;
DO $$DECLARE signature regprocedure;BEGIN
 FOR signature IN SELECT p.oid::regprocedure FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE(n.nspname='security' AND p.proname IN('storage_facility_authority','storage_facility_status','read_storage_facility','storage_facility_directory','record_storage_facility','require_storage_facility_commit','storage_facility_flow_choices'))
   OR(n.nspname='trading' AND(p.proname LIKE 'storage\_%' ESCAPE '\' OR p.proname IN('require_storage_facility_reference','require_storage_facility_plan_rules','require_storage_facility_source','verify_storage_facility_plan','guard_storage_facility','guard_storage_facility_commit','guard_storage_facility_deadline'))) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.storage_facility_authority(uuid,text),security.storage_facility_status(uuid,uuid),
 security.read_storage_facility(uuid,uuid),security.storage_facility_directory(uuid),security.record_storage_facility(uuid,uuid,jsonb,timestamptz,jsonb,uuid),
 security.require_storage_facility_commit(uuid,text,timestamptz),security.storage_facility_flow_choices(uuid,uuid),security.storage_facility_flow_choices(uuid,uuid,text) TO openetrm_runtime;
COMMIT;
