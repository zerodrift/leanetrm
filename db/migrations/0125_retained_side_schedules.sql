-- Immutable Side pricing content is staged privately. Only the small final
-- publication header joins the certified trading clock; children inherit it.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM trading.swap_instruments) OR EXISTS(SELECT 1 FROM trading.ppa_instruments)
  OR EXISTS(SELECT 1 FROM trading.swap_fixed_sides) OR EXISTS(SELECT 1 FROM trading.swap_floating_sides)
  OR EXISTS(SELECT 1 FROM trading.swap_whole_term_sides) OR EXISTS(SELECT 1 FROM trading.ppa_sides)
 THEN RAISE EXCEPTION 'side_schedule_rebooking_required';END IF;
END$$;
ALTER TABLE market_data.index_fixing_observations ADD UNIQUE(catalog_id,revision_id,index_code,observation_from,observation_to,
 reference_catalog_id,reference_revision_id,quote_code,series_id,source_code);

CREATE TABLE trading.sides (
 catalog_id uuid NOT NULL,id uuid NOT NULL,family text NOT NULL,
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 economic_identity text NOT NULL,pricing_binding_identity text NOT NULL,identity_hash bytea NOT NULL CHECK(octet_length(identity_hash)=32),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,family,identity_hash),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
  REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id)
);
CREATE TABLE trading.side_schedule_requests (
 catalog_id uuid NOT NULL,request_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),
 command_hash bytea NOT NULL CHECK(octet_length(command_hash)=32),permission text NOT NULL CHECK(permission IN('BookTrade','AmendTrade')),
 trade_id uuid,expected_revision integer,created_at ref_data.instant NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(catalog_id,request_id),FOREIGN KEY(catalog_id) REFERENCES market_data.catalogs(id),
 CHECK((trade_id IS NULL)=(expected_revision IS NULL)),CHECK(expected_revision IS NULL OR expected_revision BETWEEN 1 AND 256)
);
CREATE TABLE trading.side_schedules (
 catalog_id uuid NOT NULL,id uuid NOT NULL,side_id uuid NOT NULL,request_id uuid NOT NULL,
 schema_version text NOT NULL,generator_version text NOT NULL,content_hash bytea NOT NULL CHECK(octet_length(content_hash)=32),
 row_count integer NOT NULL CHECK(row_count>0),edge_count integer NOT NULL CHECK(edge_count>=0),binding_count integer NOT NULL CHECK(binding_count>=0),
 root_count integer NOT NULL CHECK(root_count>0),port_count integer NOT NULL CHECK(port_count>=0),
 grid_count integer NOT NULL CHECK(grid_count>=0),
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,side_id,schema_version,generator_version),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.sides(catalog_id,id),
 FOREIGN KEY(catalog_id,request_id) REFERENCES trading.side_schedule_requests(catalog_id,request_id)
);
CREATE TABLE trading.side_schedule_request_items (
 catalog_id uuid NOT NULL,request_id uuid NOT NULL,slot integer NOT NULL CHECK(slot BETWEEN 0 AND 7),schedule_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,request_id,slot),
 UNIQUE(catalog_id,request_id,slot,schedule_id),
 FOREIGN KEY(catalog_id,request_id) REFERENCES trading.side_schedule_requests(catalog_id,request_id),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id)
);
-- A vector operation refers to this immutable geometry. These arrays retain
-- selected windows and their local labels, never sampled prices or quantities.
CREATE FUNCTION trading.side_schedule_grid_cells(month date,axis integer,selection integer[],lengths integer[],days integer[],minutes integer[],occurrences integer[])
 RETURNS boolean LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $$
 SELECT axis>0 AND cardinality(selection)>0 AND cardinality(selection)<=35136
  AND cardinality(selection)=cardinality(lengths) AND cardinality(selection)=cardinality(days)
  AND cardinality(selection)=cardinality(minutes) AND cardinality(selection)=cardinality(occurrences)
  AND array_ndims(selection)=1 AND array_ndims(lengths)=1 AND array_ndims(days)=1
  AND array_ndims(minutes)=1 AND array_ndims(occurrences)=1
  AND array_lower(selection,1)=1 AND array_lower(lengths,1)=1 AND array_lower(days,1)=1
  AND array_lower(minutes,1)=1 AND array_lower(occurrences,1)=1
  AND NOT EXISTS(SELECT 1 FROM generate_subscripts(selection,1) n
   WHERE selection[n] IS NULL OR lengths[n] IS NULL OR days[n] IS NULL OR minutes[n] IS NULL OR occurrences[n] IS NULL
    OR selection[n]<0 OR lengths[n]<1 OR selection[n]::bigint+lengths[n]>axis
    OR days[n]<month-date '0001-01-01' OR days[n]>=(month+interval '1 month')::date-date '0001-01-01'
    OR minutes[n] NOT BETWEEN 0 AND 1439 OR occurrences[n] NOT IN(1,2)
    OR n<array_upper(selection,1) AND selection[n]::bigint+lengths[n]>selection[n+1]);
$$;
CREATE TABLE trading.side_schedule_grids (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),local_key text NOT NULL,
 clock_catalog_id uuid NOT NULL,clock_revision_id uuid NOT NULL,clock_code ref_data.code NOT NULL,
 month date NOT NULL CHECK(extract(day FROM month)=1),first_row_ordinal integer CHECK(first_row_ordinal>=0),
 origin ref_data.instant NOT NULL,step_ticks bigint NOT NULL CHECK(step_ticks>0 AND step_ticks%10=0),
 axis_count integer NOT NULL CHECK(axis_count>0),selection integer[] NOT NULL,lengths integer[] NOT NULL,
 operating_days integer[] NOT NULL,local_minutes integer[] NOT NULL,occurrences integer[] NOT NULL,
 PRIMARY KEY(catalog_id,schedule_id,ordinal),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id),
 FOREIGN KEY(clock_catalog_id,clock_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 CHECK(trading.side_schedule_grid_cells(month,axis_count,selection,lengths,operating_days,local_minutes,occurrences))
);
CREATE TABLE trading.side_schedule_rows (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),local_key text NOT NULL,
 role text NOT NULL CHECK(role IN('source','intermediate','contribution','root')),
 operation text NOT NULL,operation_version integer NOT NULL CHECK(operation_version=1),
 output_kind text NOT NULL CHECK(output_kind IN('price','quantity','money','factor')),
 unit_catalog_id uuid,unit_revision_id uuid,unit_code ref_data.code,
 currency_catalog_id uuid,currency_revision_id uuid,currency_code ref_data.code,quote_multiplier ref_data.exact_number,
 delivery_from date,delivery_to date,interval_from ref_data.instant,interval_to ref_data.instant,operating_date date,
 hour_beginning smallint,minute smallint,occurrence smallint,
 observation_from date,observation_to date,value_at ref_data.instant,payment_ordinal integer,
 occurrence_key text,terms_digest text,parameters jsonb NOT NULL CHECK(jsonb_typeof(parameters)='object'),grid_ordinal integer,
 -- Edges address ordinals. Logical names are checked once when sealing rather
 -- than indexed on every row; a second prefix index makes fresh FK plans scan
 -- all local names instead of probing the ordinal primary key.
 PRIMARY KEY(catalog_id,schedule_id,ordinal),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id),
 FOREIGN KEY(catalog_id,schedule_id,grid_ordinal) REFERENCES trading.side_schedule_grids(catalog_id,schedule_id,ordinal),
 FOREIGN KEY(unit_catalog_id,unit_revision_id,unit_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code) MATCH FULL,
 FOREIGN KEY(currency_catalog_id,currency_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code) MATCH FULL,
 CHECK((output_kind IN('price','quantity'))=(unit_code IS NOT NULL)),
 CHECK((output_kind IN('price','money'))=(currency_code IS NOT NULL)),
 CHECK((output_kind='price')=(quote_multiplier IS NOT NULL)),CHECK(quote_multiplier IS NULL OR quote_multiplier>0),
 CHECK((delivery_from IS NULL)=(delivery_to IS NULL)),CHECK(delivery_from IS NULL OR delivery_from<delivery_to),
 CHECK((interval_from IS NULL)=(interval_to IS NULL)),CHECK(interval_from IS NULL OR interval_from<interval_to),
 CHECK((interval_from IS NULL)=(operating_date IS NULL)),
 CHECK((observation_from IS NULL)=(observation_to IS NULL)),CHECK(observation_from IS NULL OR observation_from<observation_to),
 CHECK((occurrence_key IS NULL)=(terms_digest IS NULL)),CHECK(occurrence_key IS NULL OR role IN('contribution','root')),
 CHECK(grid_ordinal IS NULL OR(delivery_from IS NULL AND delivery_to IS NULL AND interval_from IS NULL)),
 CHECK(operation<>'output_guard' OR(grid_ordinal IS NULL AND occurrence_key IS NOT NULL) OR(grid_ordinal IS NOT NULL AND occurrence_key IS NULL)),
 CHECK(payment_ordinal IS NULL OR payment_ordinal>=0)
);
CREATE TABLE trading.side_schedule_edges (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,target integer NOT NULL,port text NOT NULL,ordinal integer NOT NULL CHECK(ordinal>=0),source integer NOT NULL,sequence_ordinal integer NOT NULL CHECK(sequence_ordinal>=0),
 PRIMARY KEY(catalog_id,schedule_id,target,port,ordinal),CHECK(source<target),
 UNIQUE(catalog_id,schedule_id,sequence_ordinal),
 FOREIGN KEY(catalog_id,schedule_id,target) REFERENCES trading.side_schedule_rows(catalog_id,schedule_id,ordinal),
 FOREIGN KEY(catalog_id,schedule_id,source) REFERENCES trading.side_schedule_rows(catalog_id,schedule_id,ordinal)
);
CREATE INDEX side_schedule_consumers ON trading.side_schedule_edges(catalog_id,schedule_id,source,target);
CREATE TABLE trading.side_schedule_bindings (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,row_ordinal integer NOT NULL,port text NOT NULL,kind text NOT NULL,
 reference_catalog_id uuid,reference_revision_id uuid,index_code ref_data.code,
 quantity_code ref_data.code,location_code ref_data.code,quote_code ref_data.code,
 source_catalog_id uuid,definition_revision_id uuid,curve_code ref_data.code,contract_id uuid,shaped_curve_code ref_data.code,
 quantity_source_code ref_data.code,asset_code ref_data.code,
 series_id uuid,custom_catalog_id uuid,custom_curve_id uuid,custom_version integer,output_node text,position integer,coordinate text,source_code ref_data.code,
 fixing_index_code ref_data.code,fixing_from date,fixing_to date,sequence_ordinal integer NOT NULL CHECK(sequence_ordinal>=0),
 PRIMARY KEY(catalog_id,schedule_id,row_ordinal,port),
 UNIQUE(catalog_id,schedule_id,sequence_ordinal),
 FOREIGN KEY(catalog_id,schedule_id,row_ordinal) REFERENCES trading.side_schedule_rows(catalog_id,schedule_id,ordinal),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,quote_code) REFERENCES ref_data.quote_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,source_code) REFERENCES ref_data.sources(catalog_id,revision_id,code),
 FOREIGN KEY(source_catalog_id,definition_revision_id,curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(source_catalog_id,definition_revision_id,curve_code,contract_id) REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id),
 FOREIGN KEY(source_catalog_id,definition_revision_id,shaped_curve_code) REFERENCES market_data.shaped_curves(catalog_id,revision_id,code),
 FOREIGN KEY(source_catalog_id,definition_revision_id,quantity_source_code) REFERENCES market_data.volume_sources(catalog_id,revision_id,code),
 FOREIGN KEY(source_catalog_id,definition_revision_id,asset_code) REFERENCES market_data.generation_assets(catalog_id,revision_id,code),
 FOREIGN KEY(source_catalog_id,definition_revision_id,series_id) REFERENCES market_data.series(catalog_id,revision_id,id),
 FOREIGN KEY(source_catalog_id,definition_revision_id,fixing_index_code,fixing_from,fixing_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code)
  REFERENCES market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code),
 FOREIGN KEY(custom_catalog_id,custom_curve_id,custom_version,output_node) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id)
);
CREATE TABLE trading.side_schedule_roots (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,name text NOT NULL,row_ordinal integer NOT NULL,measure text NOT NULL,sequence_ordinal integer NOT NULL CHECK(sequence_ordinal>=0),
 PRIMARY KEY(catalog_id,schedule_id,name),
 FOREIGN KEY(catalog_id,schedule_id,row_ordinal) REFERENCES trading.side_schedule_rows(catalog_id,schedule_id,ordinal)
);
-- Every permitted sequence is nonnegative. The partial predicate keeps this
-- paging index out of root-name FK plans, which have no sequence condition.
CREATE UNIQUE INDEX side_schedule_root_sequence ON trading.side_schedule_roots(catalog_id,schedule_id,sequence_ordinal)
 WHERE sequence_ordinal>=0;
CREATE TABLE trading.side_schedule_ports (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,name text NOT NULL,slot_role text,slot_ordinal integer,kind text NOT NULL,
 unit_catalog_id uuid NOT NULL,unit_revision_id uuid NOT NULL,unit_code ref_data.code NOT NULL,shape_identity text NOT NULL,sequence_ordinal integer NOT NULL CHECK(sequence_ordinal>=0),
 PRIMARY KEY(catalog_id,schedule_id,name),
 UNIQUE(catalog_id,schedule_id,sequence_ordinal),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id),
 FOREIGN KEY(unit_catalog_id,unit_revision_id,unit_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 CHECK((slot_role IS NULL)=(slot_ordinal IS NULL)),CHECK(slot_ordinal IS NULL OR slot_ordinal BETWEEN 0 AND 7)
);
CREATE TABLE trading.side_schedule_authorities (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,kind text NOT NULL CHECK(kind IN('market','reference')),
 authority_catalog_id uuid NOT NULL,market_catalog_id uuid,reference_catalog_id uuid,
 PRIMARY KEY(catalog_id,schedule_id,kind,authority_catalog_id),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id),
 FOREIGN KEY(market_catalog_id) REFERENCES market_data.catalogs(id),
 FOREIGN KEY(reference_catalog_id) REFERENCES ref_data.catalogs(id),
 CHECK((kind='market' AND market_catalog_id=authority_catalog_id AND reference_catalog_id IS NULL) OR
       (kind='reference' AND reference_catalog_id=authority_catalog_id AND market_catalog_id IS NULL))
);
CREATE TABLE trading.side_schedule_output_sets (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,name text NOT NULL CHECK(name='quantity'),
 unit_catalog_id uuid NOT NULL,unit_revision_id uuid NOT NULL,unit_code ref_data.code NOT NULL,shape_identity text NOT NULL,
 PRIMARY KEY(catalog_id,schedule_id,name),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id),
 FOREIGN KEY(unit_catalog_id,unit_revision_id,unit_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code)
);
CREATE TABLE trading.side_schedule_output_members (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,set_name text NOT NULL,root_name text NOT NULL,
 PRIMARY KEY(catalog_id,schedule_id,set_name,root_name),
 FOREIGN KEY(catalog_id,schedule_id,set_name) REFERENCES trading.side_schedule_output_sets(catalog_id,schedule_id,name),
 FOREIGN KEY(catalog_id,schedule_id,root_name) REFERENCES trading.side_schedule_roots(catalog_id,schedule_id,name)
);
CREATE TABLE trading.side_schedule_request_inputs (
 catalog_id uuid NOT NULL,request_id uuid NOT NULL,target_slot integer NOT NULL,target_schedule uuid NOT NULL,port text NOT NULL,
 source_slot integer NOT NULL,source_schedule uuid NOT NULL,output_name text NOT NULL,
 PRIMARY KEY(catalog_id,request_id,target_slot,port),CHECK(target_slot<>source_slot),
 FOREIGN KEY(catalog_id,request_id,target_slot,target_schedule) REFERENCES trading.side_schedule_request_items(catalog_id,request_id,slot,schedule_id),
 FOREIGN KEY(catalog_id,request_id,source_slot,source_schedule) REFERENCES trading.side_schedule_request_items(catalog_id,request_id,slot,schedule_id),
 FOREIGN KEY(catalog_id,target_schedule,port) REFERENCES trading.side_schedule_ports(catalog_id,schedule_id,name),
 FOREIGN KEY(catalog_id,source_schedule,output_name) REFERENCES trading.side_schedule_output_sets(catalog_id,schedule_id,name)
);
CREATE TABLE trading.side_schedule_chunks (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,part text NOT NULL,
 first_sequence integer NOT NULL CHECK(first_sequence>=0),item_count integer NOT NULL CHECK(item_count BETWEEN 1 AND 2048),
 content_hash bytea NOT NULL CHECK(octet_length(content_hash)=32),
 PRIMARY KEY(catalog_id,schedule_id,part,first_sequence),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id),
 CHECK(part IN('grids','rows','edges','bindings','roots','ports','output_sets','output_members'))
);
CREATE TABLE trading.side_schedule_request_seals (
 catalog_id uuid NOT NULL,request_id uuid NOT NULL,slot_count integer NOT NULL,input_count integer NOT NULL,
 PRIMARY KEY(catalog_id,request_id),FOREIGN KEY(catalog_id,request_id) REFERENCES trading.side_schedule_requests(catalog_id,request_id)
);
CREATE TABLE trading.side_schedule_seals (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,content_hash bytea NOT NULL CHECK(octet_length(content_hash)=32),
 sealed_at ref_data.instant NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(catalog_id,schedule_id),UNIQUE(catalog_id,schedule_id,content_hash),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id)
);
CREATE TABLE trading.side_schedule_publications (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,content_hash bytea NOT NULL,publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,schedule_id),
 FOREIGN KEY(catalog_id,schedule_id,content_hash) REFERENCES trading.side_schedule_seals(catalog_id,schedule_id,content_hash),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.instrument_side_schedules (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,slot integer NOT NULL CHECK(slot BETWEEN 0 AND 7),side_id uuid NOT NULL,schedule_id uuid NOT NULL,
 swap_fixed_side_id uuid,swap_floating_side_id uuid,swap_whole_term_side_id uuid,ppa_side_id uuid,
 PRIMARY KEY(catalog_id,instrument_id,slot),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedule_publications(catalog_id,schedule_id),
 FOREIGN KEY(catalog_id,swap_fixed_side_id) REFERENCES trading.swap_fixed_sides(catalog_id,id),
 FOREIGN KEY(catalog_id,swap_floating_side_id) REFERENCES trading.swap_floating_sides(catalog_id,id),
 FOREIGN KEY(catalog_id,swap_whole_term_side_id) REFERENCES trading.swap_whole_term_sides(catalog_id,id),
 FOREIGN KEY(catalog_id,ppa_side_id) REFERENCES trading.ppa_sides(catalog_id,id),
 CHECK(num_nonnulls(swap_fixed_side_id,swap_floating_side_id,swap_whole_term_side_id,ppa_side_id)=1),
 CHECK(side_id=coalesce(swap_fixed_side_id,swap_floating_side_id,swap_whole_term_side_id,ppa_side_id))
);
CREATE TABLE trading.instrument_schedule_compositions (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,request_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,request_id) REFERENCES trading.side_schedule_request_seals(catalog_id,request_id)
);

CREATE FUNCTION security.side_schedule_visible(catalog uuid,schedule uuid) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM trading.side_schedule_publications p
  JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(p.catalog_id,p.schedule_id)
  JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
  WHERE(p.catalog_id,p.schedule_id)=(catalog,schedule)
   AND EXISTS(SELECT 1 FROM trading.instrument_side_schedules m JOIN trading.instruments i
    ON(i.catalog_id,i.id)=(m.catalog_id,m.instrument_id) WHERE(m.catalog_id,m.schedule_id)=(catalog,schedule)
     AND security.trading_visible(i.catalog_id,i.publication_order,i.effective_at))
   AND security.reference_version_visible(s.reference_catalog_id,s.reference_revision_id)
   AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id)=(catalog,schedule)
    AND CASE a.kind WHEN 'reference' THEN NOT security.reference_access(a.authority_catalog_id,'ReadMarketData') ELSE NOT security.market_access(a.authority_catalog_id,'ReadMarketData') END));
$$;
CREATE FUNCTION trading.guard_side_schedule_content() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP<>'INSERT' OR EXISTS(SELECT 1 FROM trading.side_schedule_seals s
  WHERE(s.catalog_id,s.schedule_id)=(NEW.catalog_id,NEW.schedule_id))
 THEN RAISE EXCEPTION 'side_schedule_immutable';END IF;
 RETURN NEW;
END$$;

-- A reusable side and its payment rule inherit each admitted instrument's
-- business boundary, rather than the unrelated market that used them first.
CREATE FUNCTION security.retained_side_visible(catalog uuid,side uuid) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.trading_access(catalog,'ReadTrades') AND
  (NOT pg_has_role(session_user,'openetrm_risk_worker','MEMBER') OR risk.worker_read()) AND
  EXISTS(SELECT 1 FROM trading.instrument_side_schedules m WHERE(m.catalog_id,m.side_id)=(catalog,side)
   AND security.side_schedule_visible(catalog,m.schedule_id));
$$;
CREATE FUNCTION security.retained_payment_rule_visible(catalog uuid,rule uuid) RETURNS boolean
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT EXISTS(SELECT 1 FROM(
  SELECT id side FROM trading.swap_fixed_sides WHERE(catalog_id,payment_rule_id)=(catalog,rule)
  UNION ALL SELECT id FROM trading.swap_floating_sides WHERE(catalog_id,payment_rule_id)=(catalog,rule)
  UNION ALL SELECT side_id FROM trading.swap_side_periods WHERE(catalog_id,payment_rule_id)=(catalog,rule)) parents
  WHERE security.retained_side_visible(catalog,parents.side));
$$;

CREATE FUNCTION security.begin_side_schedule(catalog uuid,request uuid,slot_number integer,command jsonb,
 permission_name text,trade uuid,expected integer,header jsonb) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;prior trading.side_schedule_requests;item trading.side_schedules;parent trading.sides;
 command_digest bytea;economic_digest bytea;found_side uuid;found_schedule uuid;
BEGIN
 IF request IS NULL OR request='00000000-0000-0000-0000-000000000000'::uuid OR slot_number NOT BETWEEN 0 AND 7
  OR jsonb_typeof(command) IS DISTINCT FROM 'object' OR octet_length(command::text)>65536
  OR permission_name NOT IN('BookTrade','AmendTrade') OR(trade IS NULL)<>(expected IS NULL)
 THEN RAISE EXCEPTION 'side_schedule_request';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission_name)
 THEN RAISE EXCEPTION 'No current schedule write authority' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts
  WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND market_context_id IS NULL;
 PERFORM pg_advisory_xact_lock(730849105,hashtext(request::text));
 command_digest=sha256(convert_to(command::text,'UTF8'));
 SELECT * INTO prior FROM trading.side_schedule_requests WHERE(catalog_id,request_id)=(catalog,request);
 IF FOUND THEN
  IF(prior.actor_id,prior.command_hash,prior.permission,prior.trade_id,prior.expected_revision) IS DISTINCT FROM(actor,command_digest,permission_name,trade,expected)
  THEN RAISE EXCEPTION 'side_schedule_request_conflict';END IF;
 ELSE
  INSERT INTO trading.side_schedule_requests(catalog_id,request_id,actor_id,command_hash,permission,trade_id,expected_revision)
   VALUES(catalog,request,actor,command_digest,permission_name,trade,expected);
 END IF;
 IF jsonb_typeof(header) IS DISTINCT FROM 'object' OR
  EXISTS(SELECT 1 FROM jsonb_object_keys(header) k WHERE k NOT IN('family','definitionRevisionId','referenceCatalogId','referenceRevisionId','economicIdentity','pricingBindingIdentity','schemaVersion','generatorVersion','manifest','rowCount','edgeCount','bindingCount','rootCount','portCount','gridCount')) OR
  header->>'family' IS NULL OR header->>'economicIdentity' IS NULL OR header->>'pricingBindingIdentity' IS NULL OR
  header->>'schemaVersion' IS DISTINCT FROM 'side_schedule_blocks_v2' OR header->>'generatorVersion' IS DISTINCT FROM 'whole_term_side_blocks_v2' OR
  header->>'family' NOT IN('fixed','floating','power','certificate','capacity') OR header->>'manifest' !~ '^[0-9a-f]{64}$' OR
  NOT security.reference_access((header->>'referenceCatalogId')::uuid,'ReadMarketData')
 THEN RAISE EXCEPTION 'side_schedule_header';END IF;
 economic_digest=sha256(convert_to(jsonb_build_array(header->>'family',header->>'economicIdentity',header->>'pricingBindingIdentity')::text,'UTF8'));
 PERFORM pg_advisory_xact_lock(730849106,hashtext(encode(economic_digest,'hex')));
 SELECT * INTO parent FROM trading.sides WHERE catalog_id=catalog AND family=header->>'family' AND identity_hash=economic_digest;
 IF FOUND THEN
  IF(parent.economic_identity,parent.pricing_binding_identity,parent.reference_catalog_id,parent.reference_revision_id) IS DISTINCT FROM
   (header->>'economicIdentity',header->>'pricingBindingIdentity',(header->>'referenceCatalogId')::uuid,(header->>'referenceRevisionId')::uuid)
  THEN RAISE EXCEPTION 'side_schedule_identity_conflict';END IF;
  found_side=parent.id;
 ELSE
  found_side=gen_random_uuid();
  INSERT INTO trading.sides VALUES(catalog,found_side,header->>'family',(header->>'definitionRevisionId')::uuid,
   (header->>'referenceCatalogId')::uuid,(header->>'referenceRevisionId')::uuid,header->>'economicIdentity',header->>'pricingBindingIdentity',economic_digest);
 END IF;
 SELECT * INTO item FROM trading.side_schedules WHERE catalog_id=catalog AND side_id=found_side AND
  (schema_version,generator_version)=(header->>'schemaVersion',header->>'generatorVersion');
 IF FOUND THEN
  IF item.content_hash<>decode(header->>'manifest','hex') THEN RAISE EXCEPTION 'side_schedule_generator_conflict';END IF;
  IF(item.row_count,item.edge_count,item.binding_count,item.root_count,item.port_count,item.grid_count) IS DISTINCT FROM
   ((header->>'rowCount')::integer,(header->>'edgeCount')::integer,(header->>'bindingCount')::integer,(header->>'rootCount')::integer,(header->>'portCount')::integer,(header->>'gridCount')::integer)
  THEN RAISE EXCEPTION 'side_schedule_manifest_conflict';END IF;
  found_schedule=item.id;
 ELSE
  found_schedule=gen_random_uuid();
  INSERT INTO trading.side_schedules VALUES(catalog,found_schedule,found_side,request,header->>'schemaVersion',header->>'generatorVersion',decode(header->>'manifest','hex'),
   (header->>'rowCount')::integer,(header->>'edgeCount')::integer,(header->>'bindingCount')::integer,(header->>'rootCount')::integer,(header->>'portCount')::integer,(header->>'gridCount')::integer);
 END IF;
 SELECT schedule_id INTO found_side FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=(catalog,request,slot_number);
 IF FOUND AND found_side<>found_schedule THEN RAISE EXCEPTION 'side_schedule_request_conflict';END IF;
 IF NOT FOUND THEN
  IF EXISTS(SELECT 1 FROM trading.side_schedule_request_seals WHERE(catalog_id,request_id)=(catalog,request)) THEN RAISE EXCEPTION 'side_schedule_immutable';END IF;
  INSERT INTO trading.side_schedule_request_items VALUES(catalog,request,slot_number,found_schedule);
 END IF;
 IF NOT security.trading_access(catalog,permission_name) THEN RAISE EXCEPTION 'Schedule write authority expired' USING ERRCODE='42501';END IF;
 RETURN found_schedule;
END$$;

CREATE FUNCTION trading.side_schedule_parameters(operation text,parameters jsonb) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $$
DECLARE keys text[];actual text[];n numeric;d numeric;key text;
BEGIN
 keys=CASE operation
  WHEN 'constant_price' THEN ARRAY['denominator','numerator'] WHEN 'price_lookup' THEN ARRAY['selection']
  WHEN 'profile_quantity' THEN ARRAY[]::text[] WHEN 'sum_cells' THEN ARRAY[]::text[] WHEN 'quantity_sum_cells' THEN ARRAY[]::text[]
  WHEN 'quantity_input' THEN ARRAY['stage'] WHEN 'quantity_literal' THEN ARRAY['denominator','numerator']
  WHEN 'asset_capacity' THEN ARRAY['measure'] WHEN 'quantity_normalize' THEN ARRAY[]::text[]
  WHEN 'quantity_add' THEN ARRAY[]::text[] WHEN 'quantity_scale' THEN ARRAY['denominator','numerator']
  WHEN 'quantity_transform' THEN ARRAY['denominator','implementation','numerator']
  WHEN 'price_add' THEN ARRAY[]::text[] WHEN 'price_scale' THEN ARRAY['denominator','numerator']
  WHEN 'weighted_price' THEN ARRAY['totalScaledWeight'] WHEN 'price_application' THEN ARRAY['denominator','mapping','numerator']
  WHEN 'amount' THEN ARRAY[]::text[] WHEN 'discount' THEN ARRAY['policy'] WHEN 'pv' THEN ARRAY[]::text[]
  WHEN 'sum' THEN ARRAY[]::text[] WHEN 'output_guard' THEN ARRAY['averaging','firstFixingAt','lifecycle','measure','paymentRule','pricePolicy','unadjustedPayment'] ELSE NULL END;
 IF keys IS NULL OR jsonb_typeof(parameters) IS DISTINCT FROM 'object' THEN RETURN false;END IF;
 SELECT coalesce(array_agg(k ORDER BY k COLLATE "C"),ARRAY[]::text[]) INTO actual FROM jsonb_object_keys(parameters) k;
 IF actual<>keys THEN RETURN false;END IF;
 FOREACH key IN ARRAY keys LOOP IF jsonb_typeof(parameters->key) IS DISTINCT FROM 'string' THEN RETURN false;END IF;END LOOP;
 IF parameters ? 'numerator' THEN
  IF parameters->>'numerator' !~ '^(0|-[1-9][0-9]*|[1-9][0-9]*)$' OR parameters->>'denominator' !~ '^[1-9][0-9]*$'
   OR length(parameters->>'numerator')>1235 OR length(parameters->>'denominator')>1234 THEN RETURN false;END IF;
  n=(parameters->>'numerator')::numeric;d=(parameters->>'denominator')::numeric;
  IF abs(n)>=power(2::numeric,4096) OR d>=power(2::numeric,4096) OR gcd(abs(n),d)<>1 THEN RETURN false;END IF;
 END IF;
 IF parameters ? 'totalScaledWeight' THEN
  IF parameters->>'totalScaledWeight' !~ '^[1-9][0-9]*$' OR length(parameters->>'totalScaledWeight')>1234 OR
   (parameters->>'totalScaledWeight')::numeric>=power(2::numeric,4096) THEN RETURN false;END IF;
 END IF;
 RETURN true;
END$$;
ALTER TABLE trading.side_schedule_rows ADD CHECK(trading.side_schedule_parameters(operation,parameters));
ALTER TABLE trading.side_schedule_rows ADD CHECK(output_kind=CASE
 WHEN operation IN('constant_price','price_lookup','price_scale','price_add','weighted_price','price_application') THEN 'price'
 WHEN operation IN('quantity_literal','quantity_input','profile_quantity','quantity_sum_cells','asset_capacity','quantity_normalize','quantity_add','quantity_scale','quantity_transform') THEN 'quantity'
 WHEN operation='discount' THEN 'factor' WHEN operation IN('amount','pv','sum','sum_cells','output_guard') THEN 'money' END);

CREATE FUNCTION security.append_side_schedule_grids(catalog uuid,request uuid,schedule uuid,documents jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE candidates trading.side_schedule_grids[];header trading.side_schedules;authority uuid;written bigint=-1;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 SELECT * INTO STRICT header FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF jsonb_typeof(documents) IS DISTINCT FROM 'array' OR jsonb_array_length(documents)>2048 THEN RAISE EXCEPTION 'side_schedule_batch';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) document CROSS JOIN LATERAL jsonb_object_keys(document) k
  WHERE k NOT IN('ordinal','key','clock','month','firstRowOrdinal','origin','stepTicks','axisCount','selection','lengths','operatingDays','localMinutes','occurrences'))
 THEN RAISE EXCEPTION 'side_schedule_grid';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) d JOIN trading.sides s ON(s.catalog_id,s.id)=(catalog,header.side_id)
  WHERE((d#>>'{clock,revision,catalogId}')::uuid,(d#>>'{clock,revision,revisionId}')::uuid)
   IS DISTINCT FROM(s.reference_catalog_id,s.reference_revision_id))
 THEN RAISE EXCEPTION 'side_schedule_grid';END IF;
 FOR authority IN SELECT DISTINCT(d#>>'{clock,revision,catalogId}')::uuid FROM jsonb_array_elements(documents) d LOOP
  IF authority IS NULL OR NOT security.reference_access(authority,'ReadMarketData') THEN RAISE EXCEPTION 'No current grid clock authority' USING ERRCODE='42501';END IF;
  INSERT INTO trading.side_schedule_authorities SELECT catalog,schedule,'reference',authority,NULL,authority
   WHERE NOT EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id,a.kind,a.authority_catalog_id)=(catalog,schedule,'reference',authority));
 END LOOP;
 SELECT array_agg(ROW(catalog,schedule,(d->>'ordinal')::integer,d->>'key',
  (d#>>'{clock,revision,catalogId}')::uuid,(d#>>'{clock,revision,revisionId}')::uuid,d#>>'{clock,code}',
  (d->>'month')::date,(d->>'firstRowOrdinal')::integer,(d->>'origin')::timestamptz,(d->>'stepTicks')::bigint,(d->>'axisCount')::integer,
  ARRAY(SELECT v::integer FROM jsonb_array_elements_text(d->'selection') v),ARRAY(SELECT v::integer FROM jsonb_array_elements_text(d->'lengths') v),
  ARRAY(SELECT v::integer FROM jsonb_array_elements_text(d->'operatingDays') v),ARRAY(SELECT v::integer FROM jsonb_array_elements_text(d->'localMinutes') v),
  ARRAY(SELECT v::integer FROM jsonb_array_elements_text(d->'occurrences') v))::trading.side_schedule_grids) INTO candidates
 FROM jsonb_array_elements(documents) d;
 IF EXISTS(SELECT 1 FROM unnest(candidates) c WHERE c.ordinal<0 OR c.ordinal>=header.grid_count) THEN RAISE EXCEPTION 'side_schedule_grid';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN
  INSERT INTO trading.side_schedule_grids SELECT c.* FROM unnest(candidates) c ON CONFLICT DO NOTHING;
  GET DIAGNOSTICS written=ROW_COUNT;
 END IF;
 IF written<>coalesce(cardinality(candidates),0) AND EXISTS(SELECT 1 FROM unnest(candidates) c LEFT JOIN trading.side_schedule_grids p
  ON(p.catalog_id,p.schedule_id,p.ordinal)=(catalog,schedule,c.ordinal) WHERE p.catalog_id IS NULL OR p IS DISTINCT FROM c)
 THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

CREATE FUNCTION security.append_side_schedule_rows(catalog uuid,request uuid,schedule uuid,documents jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off AS $$
DECLARE candidates trading.side_schedule_rows[];header trading.side_schedules;authority uuid;written bigint=-1;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 SELECT * INTO STRICT header FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF jsonb_typeof(documents) IS DISTINCT FROM 'array' OR jsonb_array_length(documents)>2048 THEN RAISE EXCEPTION 'side_schedule_batch';END IF;
 FOR authority IN SELECT DISTINCT refs.authority_catalog FROM jsonb_array_elements(documents) d
  CROSS JOIN LATERAL unnest(ARRAY[(d#>>'{unit,revision,catalogId}')::uuid,(d#>>'{currency,revision,catalogId}')::uuid]) AS refs(authority_catalog) WHERE refs.authority_catalog IS NOT NULL LOOP
  IF NOT security.reference_access(authority,'ReadMarketData') THEN RAISE EXCEPTION 'No current row unit authority' USING ERRCODE='42501';END IF;
  INSERT INTO trading.side_schedule_authorities SELECT catalog,schedule,'reference',authority,NULL,authority
   WHERE NOT EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id,a.kind,a.authority_catalog_id)=(catalog,schedule,'reference',authority));
 END LOOP;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) document CROSS JOIN LATERAL jsonb_object_keys(document) k
  WHERE k NOT IN('ordinal','key','role','operation','version','outputKind','unit','currency','quoteMultiplier',
   'deliveryFrom','deliveryTo','interval','observationFrom','observationTo','valueAt','paymentOrdinal','occurrenceKey','termsDigest','parameters','grid'))
 THEN RAISE EXCEPTION 'side_schedule_row';END IF;
 SELECT array_agg(ROW(catalog,schedule,(document->>'ordinal')::integer,document->>'key',document->>'role',document->>'operation',(document->>'version')::integer,
   document->>'outputKind',(document#>>'{unit,revision,catalogId}')::uuid,(document#>>'{unit,revision,revisionId}')::uuid,document#>>'{unit,code}',
   (document#>>'{currency,revision,catalogId}')::uuid,(document#>>'{currency,revision,revisionId}')::uuid,document#>>'{currency,code}',(document->>'quoteMultiplier')::numeric,
   (document->>'deliveryFrom')::date,(document->>'deliveryTo')::date,(document#>>'{interval,start}')::timestamptz,(document#>>'{interval,end}')::timestamptz,
   (document#>>'{interval,operatingDate}')::date,(document#>>'{interval,hourBeginning}')::smallint,(document#>>'{interval,minute}')::smallint,(document#>>'{interval,occurrence}')::smallint,
   (document->>'observationFrom')::date,(document->>'observationTo')::date,(document->>'valueAt')::timestamptz,(document->>'paymentOrdinal')::integer,
   document->>'occurrenceKey',document->>'termsDigest',document->'parameters',(document->>'grid')::integer)::trading.side_schedule_rows) INTO candidates
  FROM jsonb_array_elements(documents) document;
 IF EXISTS(SELECT 1 FROM unnest(candidates) c WHERE c.ordinal<0 OR c.ordinal>=header.row_count) THEN RAISE EXCEPTION 'side_schedule_row';END IF;
 -- An entirely new batch needs no reread. Any conflict, duplicate or sealed
 -- replay checks every supplied typed fact before committing the transaction.
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN
  INSERT INTO trading.side_schedule_rows SELECT c.* FROM unnest(candidates) c ON CONFLICT DO NOTHING;
  GET DIAGNOSTICS written=ROW_COUNT;
 END IF;
 IF written<>coalesce(cardinality(candidates),0) AND EXISTS(SELECT 1 FROM unnest(candidates) c LEFT JOIN trading.side_schedule_rows p
  ON(p.catalog_id,p.schedule_id,p.ordinal)=(catalog,schedule,c.ordinal) WHERE p.catalog_id IS NULL OR p IS DISTINCT FROM c)
 THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

CREATE FUNCTION security.append_side_schedule_edges(catalog uuid,request uuid,schedule uuid,documents jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off AS $$
DECLARE candidates trading.side_schedule_edges[];written bigint=-1;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 PERFORM 1 FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF jsonb_typeof(documents) IS DISTINCT FROM 'array' OR jsonb_array_length(documents)>4096 THEN RAISE EXCEPTION 'side_schedule_batch';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) document CROSS JOIN LATERAL jsonb_object_keys(document) k
  WHERE k NOT IN('target','port','ordinal','source','sequence')) THEN RAISE EXCEPTION 'side_schedule_edge';END IF;
 SELECT array_agg(ROW(catalog,schedule,(document->>'target')::integer,document->>'port',(document->>'ordinal')::integer,(document->>'source')::integer,(document->>'sequence')::integer)::trading.side_schedule_edges)
  INTO candidates FROM jsonb_array_elements(documents) document;
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN
  INSERT INTO trading.side_schedule_edges SELECT c.* FROM unnest(candidates) c ON CONFLICT DO NOTHING;
  GET DIAGNOSTICS written=ROW_COUNT;
 END IF;
 IF written<>coalesce(cardinality(candidates),0) AND EXISTS(SELECT 1 FROM unnest(candidates) c LEFT JOIN trading.side_schedule_edges p
  ON(p.catalog_id,p.schedule_id,p.target,p.port,p.ordinal)=(catalog,schedule,c.target,c.port,c.ordinal)
  WHERE p.catalog_id IS NULL OR p IS DISTINCT FROM c)
 THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

ALTER TABLE trading.side_schedule_bindings ADD CHECK(kind IN('contract_price','index_fixing','curve_interval','custom_output','quantity_source',
 'asset','sibling_quantity','location','index','quote'));
ALTER TABLE trading.side_schedule_bindings ADD CHECK(CASE kind
 WHEN 'contract_price' THEN num_nonnulls(source_catalog_id,definition_revision_id,curve_code,contract_id)=4
 WHEN 'index_fixing' THEN num_nonnulls(reference_catalog_id,reference_revision_id,source_code,source_catalog_id,definition_revision_id,series_id,fixing_index_code,fixing_from,fixing_to,quote_code,coordinate)=11
 WHEN 'curve_interval' THEN num_nonnulls(source_catalog_id,definition_revision_id)=2 AND num_nonnulls(curve_code,shaped_curve_code)=1
 WHEN 'custom_output' THEN num_nonnulls(custom_catalog_id,custom_curve_id,custom_version,output_node,source_catalog_id,definition_revision_id,curve_code,contract_id,coordinate)=9
  AND coordinate=output_node AND(position IS NULL OR position>=0)
 WHEN 'quantity_source' THEN num_nonnulls(source_catalog_id,definition_revision_id,quantity_source_code)=3
 WHEN 'asset' THEN num_nonnulls(source_catalog_id,definition_revision_id,asset_code)=3
 WHEN 'sibling_quantity' THEN num_nonnulls(reference_catalog_id,reference_revision_id,quantity_code,coordinate,position)=5 AND position BETWEEN 0 AND 7
 WHEN 'location' THEN num_nonnulls(reference_catalog_id,reference_revision_id,location_code,coordinate)=4
 WHEN 'index' THEN num_nonnulls(reference_catalog_id,reference_revision_id,index_code)=3
 WHEN 'quote' THEN num_nonnulls(reference_catalog_id,reference_revision_id,quote_code)=3 ELSE false END);
ALTER TABLE trading.side_schedule_bindings ADD CHECK(num_nonnulls(reference_catalog_id,reference_revision_id,index_code,quantity_code,location_code,quote_code,
 source_catalog_id,definition_revision_id,curve_code,contract_id,shaped_curve_code,quantity_source_code,asset_code,series_id,
 custom_catalog_id,custom_curve_id,custom_version,output_node,position,coordinate,source_code,fixing_index_code,fixing_from,fixing_to)=
 CASE kind WHEN 'contract_price' THEN 4 WHEN 'index_fixing' THEN 11 WHEN 'curve_interval' THEN 3
  WHEN 'custom_output' THEN 9+num_nonnulls(position) WHEN 'quantity_source' THEN 3+num_nonnulls(coordinate)
  WHEN 'asset' THEN 3+num_nonnulls(coordinate) WHEN 'sibling_quantity' THEN 5 WHEN 'location' THEN 4 WHEN 'index' THEN 3 WHEN 'quote' THEN 3 END);
ALTER TABLE trading.side_schedule_bindings ADD CHECK(kind<>'index_fixing' OR coordinate='published_index_observations_v1');

CREATE FUNCTION security.append_side_schedule_aux(catalog uuid,request uuid,schedule uuid,part text,documents jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off AS $$
DECLARE port_document jsonb;binding_batch trading.side_schedule_bindings[];root_batch trading.side_schedule_roots[];authority uuid;
 port_value trading.side_schedule_ports;prior_port trading.side_schedule_ports;written bigint=-1;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 PERFORM 1 FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF jsonb_typeof(documents) IS DISTINCT FROM 'array' OR jsonb_array_length(documents)>2048 THEN RAISE EXCEPTION 'side_schedule_batch';END IF;
 IF part='bindings' THEN
  FOR authority IN SELECT DISTINCT (d->>'reference_catalog_id')::uuid FROM jsonb_array_elements(documents) d WHERE d->>'reference_catalog_id' IS NOT NULL LOOP
   IF NOT security.reference_access(authority,'ReadMarketData') THEN RAISE EXCEPTION 'No current binding authority' USING ERRCODE='42501';END IF;
   INSERT INTO trading.side_schedule_authorities SELECT catalog,schedule,'reference',authority,NULL,authority
    WHERE NOT EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id,a.kind,a.authority_catalog_id)=(catalog,schedule,'reference',authority));
  END LOOP;
  FOR authority IN SELECT DISTINCT refs.authority_catalog FROM jsonb_array_elements(documents) d
   CROSS JOIN LATERAL unnest(ARRAY[(d->>'source_catalog_id')::uuid,(d->>'custom_catalog_id')::uuid]) AS refs(authority_catalog) WHERE refs.authority_catalog IS NOT NULL LOOP
   IF NOT security.market_access(authority,'ReadMarketData') THEN RAISE EXCEPTION 'No current binding authority' USING ERRCODE='42501';END IF;
   INSERT INTO trading.side_schedule_authorities SELECT catalog,schedule,'market',authority,authority,NULL
    WHERE NOT EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id,a.kind,a.authority_catalog_id)=(catalog,schedule,'market',authority));
  END LOOP;
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) document CROSS JOIN LATERAL jsonb_object_keys(document) k WHERE k NOT IN('row_ordinal','port','kind','reference_catalog_id','reference_revision_id','index_code','quantity_code','location_code','quote_code',
    'source_catalog_id','definition_revision_id','curve_code','contract_id','shaped_curve_code','quantity_source_code','asset_code','series_id','custom_catalog_id','custom_curve_id','custom_version','output_node','position','coordinate','source_code','fixing_index_code','fixing_from','fixing_to','sequence_ordinal'))
  THEN RAISE EXCEPTION 'side_schedule_binding';END IF;
  SELECT array_agg(jsonb_populate_record(NULL::trading.side_schedule_bindings,document||jsonb_build_object('catalog_id',catalog,'schedule_id',schedule)))
   INTO binding_batch FROM jsonb_array_elements(documents) document;
  IF EXISTS(SELECT 1 FROM unnest(binding_batch) b WHERE b.kind='custom_output' AND NOT EXISTS(SELECT 1 FROM market_data.custom_curve_versions v
    WHERE(v.catalog_id,v.curve_id,v.version,v.output_node,v.state)=(b.custom_catalog_id,b.custom_curve_id,b.custom_version,b.output_node,'published'))
   ) THEN RAISE EXCEPTION 'side_schedule_custom_output';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN
   INSERT INTO trading.side_schedule_bindings SELECT b.* FROM unnest(binding_batch) b ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS written=ROW_COUNT;
  END IF;
  IF written<>coalesce(cardinality(binding_batch),0) AND EXISTS(SELECT 1 FROM unnest(binding_batch) b LEFT JOIN trading.side_schedule_bindings p
   ON(p.catalog_id,p.schedule_id,p.row_ordinal,p.port)=(catalog,schedule,b.row_ordinal,b.port)
   WHERE p.catalog_id IS NULL OR p IS DISTINCT FROM b)
  THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
 ELSIF part='roots' THEN
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) document CROSS JOIN LATERAL jsonb_object_keys(document) k
   WHERE k NOT IN('name','row','measure','sequence')) THEN RAISE EXCEPTION 'side_schedule_root';END IF;
  SELECT array_agg(ROW(catalog,schedule,document->>'name',(document->>'row')::integer,document->>'measure',(document->>'sequence')::integer)::trading.side_schedule_roots)
   INTO root_batch FROM jsonb_array_elements(documents) document;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN
   INSERT INTO trading.side_schedule_roots SELECT r.* FROM unnest(root_batch) r ON CONFLICT DO NOTHING;
   GET DIAGNOSTICS written=ROW_COUNT;
  END IF;
  IF written<>coalesce(cardinality(root_batch),0) AND EXISTS(SELECT 1 FROM unnest(root_batch) r LEFT JOIN trading.side_schedule_roots p
   ON(p.catalog_id,p.schedule_id,p.name)=(catalog,schedule,r.name)
   WHERE p.catalog_id IS NULL OR p IS DISTINCT FROM r)
  THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
 ELSIF part='ports' THEN
  FOR port_document IN SELECT value FROM jsonb_array_elements(documents) LOOP
   IF EXISTS(SELECT 1 FROM jsonb_object_keys(port_document) k WHERE k NOT IN('name','slot','kind','unit','shapeIdentity','sequence')) THEN RAISE EXCEPTION 'side_schedule_port';END IF;
   port_value=ROW(catalog,schedule,port_document->>'name',port_document#>>'{slot,role}',(port_document#>>'{slot,ordinal}')::integer,port_document->>'kind',
    (port_document#>>'{unit,revision,catalogId}')::uuid,(port_document#>>'{unit,revision,revisionId}')::uuid,port_document#>>'{unit,code}',port_document->>'shapeIdentity',(port_document->>'sequence')::integer);
   SELECT * INTO prior_port FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id,name)=(catalog,schedule,port_value.name);
   IF FOUND THEN IF prior_port IS DISTINCT FROM port_value THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
   ELSE INSERT INTO trading.side_schedule_ports SELECT port_value.*;END IF;
  END LOOP;
 ELSE RAISE EXCEPTION 'side_schedule_part';END IF;
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

CREATE FUNCTION security.seal_side_schedule(catalog uuid,request uuid,schedule uuid,manifest text) RETURNS void
-- Fresh immutable batches have no per-schedule planner statistics. Nested
-- self-joins otherwise repeatedly scan the whole graph while estimating one
-- row; this validation pass requires hash/merge joins over its exact keys.
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off AS $$
DECLARE h trading.side_schedules;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 SELECT * INTO STRICT h FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF manifest !~ '^[0-9a-f]{64}$' OR decode(manifest,'hex')<>h.content_hash THEN RAISE EXCEPTION 'side_schedule_manifest_conflict';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RETURN;END IF;
 IF (SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.row_count OR
  (SELECT count(*) FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.edge_count OR
  (SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.binding_count OR
  (SELECT count(*) FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.root_count OR
  (SELECT count(*) FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.port_count OR
  (SELECT count(*) FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.grid_count OR
  (SELECT min(ordinal)<>0 OR max(ordinal)<>h.grid_count-1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
  (SELECT min(ordinal)<>0 OR max(ordinal)<>h.row_count-1 FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))
 THEN RAISE EXCEPTION 'side_schedule_incomplete';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule)
  GROUP BY local_key HAVING count(*)<>1) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)
   GROUP BY local_key HAVING count(*)<>1) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(catalog,schedule)
  GROUP BY target,port HAVING min(ordinal)<>0 OR max(ordinal)<>count(*)-1) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_roots o JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(o.catalog_id,o.schedule_id,o.row_ordinal)
   WHERE(o.catalog_id,o.schedule_id)=(catalog,schedule) AND
    (o.measure='schedule_quantity' AND r.output_kind<>'quantity' OR o.measure IN('contractual_amount','contribution_pv','pv') AND r.output_kind<>'money')) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_bindings b LEFT JOIN trading.side_schedule_ports p
   ON(p.catalog_id,p.schedule_id,p.name)=(b.catalog_id,b.schedule_id,b.coordinate)
   WHERE(b.catalog_id,b.schedule_id,b.kind)=(catalog,schedule,'sibling_quantity') AND p.name IS NULL)
 THEN RAISE EXCEPTION 'side_schedule_topology';END IF;
 IF EXISTS(WITH inputs AS MATERIALIZED(SELECT target,count(*) n FROM trading.side_schedule_edges
   WHERE(catalog_id,schedule_id)=(catalog,schedule) GROUP BY target)
  SELECT 1 FROM trading.side_schedule_rows r LEFT JOIN inputs ON inputs.target=r.ordinal
  WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND CASE
   WHEN r.operation IN('constant_price','price_lookup','quantity_input','quantity_literal','profile_quantity','asset_capacity','discount') THEN coalesce(inputs.n,0)<>0
   WHEN r.operation IN('quantity_scale','quantity_transform','price_scale','price_application','sum_cells','quantity_sum_cells') THEN coalesce(inputs.n,0)<>1
   WHEN r.operation IN('quantity_normalize','amount','pv','output_guard','price_add') THEN coalesce(inputs.n,0)<>2
   WHEN r.operation IN('quantity_add','weighted_price','sum') THEN coalesce(inputs.n,0)<1 ELSE true END)
 THEN RAISE EXCEPTION 'side_schedule_arity';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND(r.operation='profile_quantity' AND r.grid_ordinal IS NULL OR r.operation='discount' AND r.value_at IS NULL)) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_edges e
   JOIN trading.side_schedule_rows target ON(target.catalog_id,target.schedule_id,target.ordinal)=(e.catalog_id,e.schedule_id,e.target)
   JOIN trading.side_schedule_rows source ON(source.catalog_id,source.schedule_id,source.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   LEFT JOIN trading.side_schedule_grids target_grid ON(target_grid.catalog_id,target_grid.schedule_id,target_grid.ordinal)=(target.catalog_id,target.schedule_id,target.grid_ordinal)
   WHERE(e.catalog_id,e.schedule_id)=(catalog,schedule) AND CASE
    WHEN target.operation='sum_cells' THEN source.grid_ordinal IS NULL OR target.grid_ordinal IS NOT NULL
    WHEN target.operation='quantity_sum_cells' THEN source.grid_ordinal IS NULL OR target.grid_ordinal IS NULL OR cardinality(target_grid.selection)<>1
    ELSE source.grid_ordinal IS NOT NULL AND source.grid_ordinal IS DISTINCT FROM target.grid_ordinal END)
 THEN RAISE EXCEPTION 'side_schedule_grid';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,name,measure)=(catalog,schedule,'pv','pv')) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_edges e JOIN trading.side_schedule_rows target
   ON(target.catalog_id,target.schedule_id,target.ordinal)=(e.catalog_id,e.schedule_id,e.target)
   JOIN trading.side_schedule_rows source ON(source.catalog_id,source.schedule_id,source.ordinal)=(e.catalog_id,e.schedule_id,e.source)
   WHERE(e.catalog_id,e.schedule_id)=(catalog,schedule) AND(
    e.port<>ALL(CASE target.operation
     WHEN 'sum_cells' THEN ARRAY['input'] WHEN 'quantity_sum_cells' THEN ARRAY['input']
     WHEN 'quantity_normalize' THEN ARRAY['input','basis'] WHEN 'amount' THEN ARRAY['quantity','price']
     WHEN 'pv' THEN ARRAY['amount','discount'] WHEN 'output_guard' THEN ARRAY['amount','discounted']
     WHEN 'quantity_scale' THEN ARRAY['input'] WHEN 'quantity_transform' THEN ARRAY['input']
     WHEN 'price_scale' THEN ARRAY['input'] WHEN 'price_application' THEN ARRAY['input']
     WHEN 'quantity_add' THEN ARRAY['input'] WHEN 'price_add' THEN ARRAY['input'] WHEN 'weighted_price' THEN ARRAY['input']
     WHEN 'sum' THEN ARRAY['input'] ELSE ARRAY[]::text[] END) OR
    e.ordinal<>0 AND target.operation NOT IN('quantity_add','price_add','weighted_price','sum') OR
    source.output_kind<>CASE WHEN target.operation IN('quantity_normalize','quantity_add','quantity_scale','quantity_transform','quantity_sum_cells') THEN 'quantity'
     WHEN target.operation IN('price_scale','price_application','price_add','weighted_price') THEN 'price'
     WHEN target.operation='amount' THEN e.port WHEN target.operation='pv' AND e.port='discount' THEN 'factor' ELSE 'money' END OR
    source.output_kind IN('price','money') AND(source.currency_catalog_id,source.currency_revision_id,source.currency_code)
     IS DISTINCT FROM(target.currency_catalog_id,target.currency_revision_id,target.currency_code) OR
    target.operation IN('quantity_add','quantity_scale','quantity_sum_cells','price_scale','price_application','price_add','weighted_price') AND
     (source.unit_catalog_id,source.unit_revision_id,source.unit_code,source.quote_multiplier)
      IS DISTINCT FROM(target.unit_catalog_id,target.unit_revision_id,target.unit_code,target.quote_multiplier) OR
    target.operation='quantity_normalize' AND e.port='input' AND(source.unit_catalog_id,source.unit_revision_id,source.unit_code)
     IS DISTINCT FROM(target.unit_catalog_id,target.unit_revision_id,target.unit_code))) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_rows amount
   JOIN trading.side_schedule_edges qe ON(qe.catalog_id,qe.schedule_id,qe.target,qe.port)=(amount.catalog_id,amount.schedule_id,amount.ordinal,'quantity')
   JOIN trading.side_schedule_rows q ON(q.catalog_id,q.schedule_id,q.ordinal)=(qe.catalog_id,qe.schedule_id,qe.source)
   JOIN trading.side_schedule_edges pe ON(pe.catalog_id,pe.schedule_id,pe.target,pe.port)=(amount.catalog_id,amount.schedule_id,amount.ordinal,'price')
   JOIN trading.side_schedule_rows p ON(p.catalog_id,p.schedule_id,p.ordinal)=(pe.catalog_id,pe.schedule_id,pe.source)
   WHERE(amount.catalog_id,amount.schedule_id,amount.operation)=(catalog,schedule,'amount') AND
    (q.unit_catalog_id,q.unit_revision_id,q.unit_code) IS DISTINCT FROM(p.unit_catalog_id,p.unit_revision_id,p.unit_code))
 THEN RAISE EXCEPTION 'side_schedule_ports';END IF;
 IF EXISTS(WITH sources AS MATERIALIZED(SELECT row_ordinal,count(*) n,bool_or(kind='contract_price') dated,bool_or(kind='curve_interval') curve,
   bool_or(port='market' AND kind='contract_price') market_port,bool_or(port='index' AND kind='index') index_port,
   bool_or(port='quote' AND kind='quote') quote_port,bool_or(port='curve' AND kind='curve_interval') curve_port,
   bool_and(kind=CASE port WHEN 'market' THEN 'contract_price' WHEN 'index' THEN 'index' WHEN 'quote' THEN 'quote'
    WHEN 'fixing' THEN 'index_fixing' WHEN 'projection' THEN 'custom_output' ELSE '' END) dated_ports,
   bool_and((port,kind) IN(('curve','curve_interval'),('index','index'))) curve_ports,
   bool_and(port='location' AND kind IN('location','quantity_source','asset')) location_ports,
   bool_and((port,kind) IN(('source','quantity_source'),('quantity','sibling_quantity'))) quantity_sources,bool_and(port='asset' AND kind='asset') assets
   FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule) GROUP BY row_ordinal)
  SELECT 1 FROM trading.side_schedule_rows r LEFT JOIN sources s ON s.row_ordinal=r.ordinal
  WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND CASE r.operation
   WHEN 'price_lookup' THEN CASE WHEN s.dated THEN NOT(s.market_port AND s.index_port AND s.quote_port AND s.dated_ports)
    WHEN s.curve THEN NOT(s.curve_port AND s.curve_ports AND CASE WHEN r.parameters->>'selection'='interval_attribute_lookup_v1'
     THEN s.n=1 ELSE s.n=2 AND s.index_port END) ELSE coalesce(s.n,0)<>1 OR NOT s.location_ports END
   WHEN 'quantity_input' THEN coalesce(s.n,0)<>1 OR NOT s.quantity_sources
   WHEN 'asset_capacity' THEN coalesce(s.n,0)<>1 OR NOT s.assets
   WHEN 'discount' THEN coalesce(s.n,0)<>0
   ELSE coalesce(s.n,0)<>0 END)
 THEN RAISE EXCEPTION 'side_schedule_sources';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r
   ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
   WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) AND(
    b.kind IN('quantity_source','asset') AND(b.coordinate IS NOT NULL)<>(r.operation='price_lookup' AND b.port='location') OR
    b.kind='index_fixing' AND((r.observation_from,r.observation_to) IS DISTINCT FROM(b.fixing_from,b.fixing_to) OR
     NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings selected
      WHERE(selected.catalog_id,selected.schedule_id,selected.row_ordinal,selected.port,selected.kind)=(catalog,schedule,b.row_ordinal,'index','index') AND
       (selected.reference_catalog_id,selected.reference_revision_id,selected.index_code)=(b.reference_catalog_id,b.reference_revision_id,b.fixing_index_code)) OR
     NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings quote
      WHERE(quote.catalog_id,quote.schedule_id,quote.row_ordinal,quote.port,quote.kind)=(catalog,schedule,b.row_ordinal,'quote','quote') AND
       (quote.reference_catalog_id,quote.reference_revision_id,quote.quote_code)=(b.reference_catalog_id,b.reference_revision_id,b.quote_code))) OR
    b.kind='custom_output' AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_bindings market
     WHERE(market.catalog_id,market.schedule_id,market.row_ordinal,market.port,market.kind)=(catalog,schedule,b.row_ordinal,'market','contract_price') AND
      (market.source_catalog_id,market.definition_revision_id,market.curve_code,market.contract_id)=
       (b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id)) OR
    b.kind='sibling_quantity' AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_ports p
     WHERE(p.catalog_id,p.schedule_id,p.name,p.slot_ordinal,p.unit_catalog_id,p.unit_revision_id,p.unit_code)=
      (catalog,schedule,b.coordinate,b.position,b.reference_catalog_id,b.reference_revision_id,b.quantity_code))))
 THEN RAISE EXCEPTION 'side_schedule_sources';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,measure)=(catalog,schedule,'schedule_quantity')) THEN
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id,name)=(catalog,schedule,'quantity')) OR
   EXISTS(SELECT name FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id,measure)=(catalog,schedule,'schedule_quantity')
    EXCEPT SELECT root_name FROM trading.side_schedule_output_members WHERE(catalog_id,schedule_id,set_name)=(catalog,schedule,'quantity')) OR
   EXISTS(SELECT 1 FROM trading.side_schedule_output_members m JOIN trading.side_schedule_roots root ON(root.catalog_id,root.schedule_id,root.name)=(m.catalog_id,m.schedule_id,m.root_name)
    JOIN trading.side_schedule_rows row ON(row.catalog_id,row.schedule_id,row.ordinal)=(root.catalog_id,root.schedule_id,root.row_ordinal)
    JOIN trading.side_schedule_output_sets o ON(o.catalog_id,o.schedule_id,o.name)=(m.catalog_id,m.schedule_id,m.set_name)
    WHERE(m.catalog_id,m.schedule_id)=(catalog,schedule) AND(root.measure<>'schedule_quantity' OR
     (row.unit_catalog_id,row.unit_revision_id,row.unit_code) IS DISTINCT FROM(o.unit_catalog_id,o.unit_revision_id,o.unit_code)))
  THEN RAISE EXCEPTION 'side_schedule_output_set';END IF;
 ELSIF EXISTS(SELECT 1 FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id)=(catalog,schedule)) THEN RAISE EXCEPTION 'side_schedule_output_set';END IF;
 INSERT INTO trading.side_schedule_seals(catalog_id,schedule_id,content_hash) VALUES(catalog,schedule,h.content_hash);
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

CREATE FUNCTION security.append_side_schedule_outputs(catalog uuid,request uuid,schedule uuid,part text,documents jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off AS $$
DECLARE document jsonb;o trading.side_schedule_output_sets;prior trading.side_schedule_output_sets;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 PERFORM 1 FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF jsonb_typeof(documents) IS DISTINCT FROM 'array' OR jsonb_array_length(documents)>2048 THEN RAISE EXCEPTION 'side_schedule_batch';END IF;
 IF part='members' THEN
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(documents) member CROSS JOIN LATERAL jsonb_object_keys(member) k
   WHERE k NOT IN('set','root')) THEN RAISE EXCEPTION 'side_schedule_output_set';END IF;
  INSERT INTO trading.side_schedule_output_members
   SELECT catalog,schedule,member->>'set',member->>'root' FROM jsonb_array_elements(documents) member WHERE NOT EXISTS(
    SELECT 1 FROM trading.side_schedule_output_members p WHERE(p.catalog_id,p.schedule_id,p.set_name,p.root_name)=(catalog,schedule,member->>'set',member->>'root')) ON CONFLICT DO NOTHING;
  PERFORM security.side_schedule_stage_authority(catalog,request,schedule);RETURN;
 END IF;
 FOR document IN SELECT value FROM jsonb_array_elements(documents) LOOP
  IF part='sets' THEN
   IF EXISTS(SELECT 1 FROM jsonb_object_keys(document) k WHERE k NOT IN('name','unit','shapeIdentity')) THEN RAISE EXCEPTION 'side_schedule_output_set';END IF;
   o=ROW(catalog,schedule,document->>'name',(document#>>'{unit,revision,catalogId}')::uuid,(document#>>'{unit,revision,revisionId}')::uuid,
    document#>>'{unit,code}',document->>'shapeIdentity');
   SELECT * INTO prior FROM trading.side_schedule_output_sets WHERE(catalog_id,schedule_id,name)=(catalog,schedule,o.name);
   IF FOUND THEN IF prior IS DISTINCT FROM o THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
   ELSE INSERT INTO trading.side_schedule_output_sets SELECT o.*;END IF;
  ELSE RAISE EXCEPTION 'side_schedule_part';END IF;
 END LOOP;
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

CREATE FUNCTION security.seal_side_schedule_request(catalog uuid,request uuid,documents jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off AS $$
DECLARE document jsonb;target uuid;source uuid;prior trading.side_schedule_request_inputs;candidate trading.side_schedule_request_inputs;slot_count integer;input_count integer;
BEGIN
 PERFORM security.side_schedule_request_authority(catalog,request);
 PERFORM pg_advisory_xact_lock(730849105,hashtext(request::text));
 IF jsonb_typeof(documents) IS DISTINCT FROM 'array' OR jsonb_array_length(documents)>64 THEN RAISE EXCEPTION 'side_schedule_ports';END IF;
 FOR target IN SELECT schedule_id FROM trading.side_schedule_request_items WHERE(catalog_id,request_id)=(catalog,request) LOOP
  PERFORM security.side_schedule_stage_authority(catalog,request,target);
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,target)) THEN RAISE EXCEPTION 'side_schedule_unsealed';END IF;
 END LOOP;
 FOR document IN SELECT value FROM jsonb_array_elements(documents) LOOP
  IF EXISTS(SELECT 1 FROM jsonb_object_keys(document) k WHERE k NOT IN('target','port','source','output')) THEN RAISE EXCEPTION 'side_schedule_ports';END IF;
  SELECT schedule_id INTO STRICT target FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=(catalog,request,(document->>'target')::integer);
  SELECT schedule_id INTO STRICT source FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,slot)=(catalog,request,(document->>'source')::integer);
  candidate=ROW(catalog,request,(document->>'target')::integer,target,document->>'port',(document->>'source')::integer,source,document->>'output');
  SELECT * INTO prior FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id,target_slot,port)=(catalog,request,candidate.target_slot,candidate.port);
  IF FOUND THEN IF prior IS DISTINCT FROM candidate THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
  ELSE
   IF EXISTS(SELECT 1 FROM trading.side_schedule_request_seals WHERE(catalog_id,request_id)=(catalog,request)) THEN RAISE EXCEPTION 'side_schedule_immutable';END IF;
   INSERT INTO trading.side_schedule_request_inputs SELECT candidate.*;
  END IF;
 END LOOP;
 SELECT count(*) INTO slot_count FROM trading.side_schedule_request_items WHERE(catalog_id,request_id)=(catalog,request);
 SELECT count(*) INTO input_count FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,request);
 IF slot_count=0 OR input_count<>jsonb_array_length(documents) OR input_count<>(SELECT count(*) FROM trading.side_schedule_request_items i
  JOIN trading.side_schedule_ports p ON(p.catalog_id,p.schedule_id)=(i.catalog_id,i.schedule_id) WHERE(i.catalog_id,i.request_id)=(catalog,request)) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_request_inputs i JOIN trading.side_schedule_ports p
   ON(p.catalog_id,p.schedule_id,p.name)=(i.catalog_id,i.target_schedule,i.port) JOIN trading.side_schedule_output_sets o
   ON(o.catalog_id,o.schedule_id,o.name)=(i.catalog_id,i.source_schedule,i.output_name)
   WHERE(i.catalog_id,i.request_id)=(catalog,request) AND(p.unit_catalog_id,p.unit_revision_id,p.unit_code,p.shape_identity) IS DISTINCT FROM(o.unit_catalog_id,o.unit_revision_id,o.unit_code,o.shape_identity)) OR
  EXISTS(WITH RECURSIVE reach(a,b) AS(SELECT target_slot,source_slot FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,request)
   UNION SELECT r.a,i.source_slot FROM reach r JOIN trading.side_schedule_request_inputs i ON i.target_slot=r.b WHERE(i.catalog_id,i.request_id)=(catalog,request)) SELECT 1 FROM reach WHERE a=b)
 THEN RAISE EXCEPTION 'side_schedule_ports';END IF;
 -- Disjoint blocks with identical complete geometry need no cell expansion.
 -- Ambiguous bounding ranges use the full cell check; otherwise only unmatched
 -- price partitions and their overlapping source blocks need expansion.
 -- Epoch microseconds are integer coordinates, independent of the session zone.
 IF EXISTS(WITH links AS MATERIALIZED(
 SELECT * FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,request)),
required_ids AS(
 SELECT DISTINCT i.target_slot,i.port,r.grid_ordinal,i.target_schedule
 FROM links i JOIN trading.side_schedule_bindings b
 ON(b.catalog_id,b.schedule_id,b.kind,b.coordinate)=(catalog,i.target_schedule,'sibling_quantity',i.port)
 JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)),
required_grids AS MATERIALIZED(
 SELECT i.target_slot,i.port,g.*, (extract(epoch FROM g.origin)*1000000)::bigint+(g.step_ticks/10)*g.selection[1] AS first_us,
 (extract(epoch FROM g.origin)*1000000)::bigint+(g.step_ticks/10)*(g.selection[cardinality(g.selection)]+g.lengths[cardinality(g.lengths)]) AS last_us
 FROM required_ids i JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(catalog,i.target_schedule,i.grid_ordinal)),
supplied_grids AS MATERIALIZED(
 SELECT i.target_slot,i.port,g.*, (extract(epoch FROM g.origin)*1000000)::bigint+(g.step_ticks/10)*g.selection[1] AS first_us,
 (extract(epoch FROM g.origin)*1000000)::bigint+(g.step_ticks/10)*(g.selection[cardinality(g.selection)]+g.lengths[cardinality(g.lengths)]) AS last_us
 FROM links i JOIN trading.side_schedule_output_members m ON(m.catalog_id,m.schedule_id,m.set_name)=(catalog,i.source_schedule,i.output_name)
 JOIN trading.side_schedule_roots o ON(o.catalog_id,o.schedule_id,o.name)=(m.catalog_id,m.schedule_id,m.root_name)
 JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(o.catalog_id,o.schedule_id,o.row_ordinal)
 JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(r.catalog_id,r.schedule_id,r.grid_ordinal)),
complex AS MATERIALIZED(
 SELECT EXISTS(SELECT 1 FROM(
 SELECT first_us,max(last_us) OVER(PARTITION BY kind,target_slot,port ORDER BY first_us,last_us ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS previous_end
 FROM(SELECT 'required' kind,target_slot,port,first_us,last_us FROM required_grids UNION ALL SELECT 'supplied',target_slot,port,first_us,last_us FROM supplied_grids) q
 ) ranges WHERE previous_end>first_us) AS expand_all),
unmatched AS MATERIALIZED(
 SELECT r.* FROM required_grids r WHERE(SELECT expand_all FROM complex) OR NOT EXISTS(
 SELECT 1 FROM supplied_grids s WHERE(s.target_slot,s.port,s.month,s.clock_catalog_id,s.clock_revision_id,s.clock_code,s.origin,s.step_ticks,s.axis_count,s.selection,s.lengths,s.operating_days,s.local_minutes,s.occurrences)=
 (r.target_slot,r.port,r.month,r.clock_catalog_id,r.clock_revision_id,r.clock_code,r.origin,r.step_ticks,r.axis_count,r.selection,r.lengths,r.operating_days,r.local_minutes,r.occurrences))),
required AS MATERIALIZED(
 SELECT g.target_slot,g.port,g.clock_catalog_id,g.clock_revision_id,g.clock_code,
 (extract(epoch FROM g.origin)*1000000)::bigint+(g.step_ticks/10)*c.at first_us,
 (extract(epoch FROM g.origin)*1000000)::bigint+(g.step_ticks/10)*(c.at+c.length) last_us,c.day,c.minute,c.occurrence
 FROM unmatched g CROSS JOIN LATERAL unnest(g.selection,g.lengths,g.operating_days,g.local_minutes,g.occurrences)c(at,length,day,minute,occurrence)),
supplied AS MATERIALIZED(
 SELECT g.target_slot,g.port,g.clock_catalog_id,g.clock_revision_id,g.clock_code,
 (extract(epoch FROM g.origin)*1000000)::bigint+(g.step_ticks/10)*c.at first_us,
 (extract(epoch FROM g.origin)*1000000)::bigint+(g.step_ticks/10)*(c.at+c.length) last_us,c.day,c.minute,c.occurrence
 FROM supplied_grids g CROSS JOIN LATERAL unnest(g.selection,g.lengths,g.operating_days,g.local_minutes,g.occurrences)c(at,length,day,minute,occurrence)
 WHERE(SELECT expand_all FROM complex) OR EXISTS(SELECT 1 FROM unmatched r WHERE(r.target_slot,r.port,r.clock_catalog_id,r.clock_revision_id,r.clock_code)=(g.target_slot,g.port,g.clock_catalog_id,g.clock_revision_id,g.clock_code) AND r.first_us<g.last_us AND g.first_us<r.last_us)),
matched AS(
 SELECT r.target_slot,r.port,r.first_us,r.last_us,r.day,r.minute,r.occurrence,count(s.first_us) n
 FROM required r LEFT JOIN supplied s ON(s.target_slot,s.port,s.clock_catalog_id,s.clock_revision_id,s.clock_code,s.first_us,s.last_us,s.day,s.minute,s.occurrence)=(r.target_slot,r.port,r.clock_catalog_id,r.clock_revision_id,r.clock_code,r.first_us,r.last_us,r.day,r.minute,r.occurrence)
 GROUP BY r.target_slot,r.port,r.first_us,r.last_us,r.day,r.minute,r.occurrence),
ordered AS(SELECT s.*,lag(last_us) OVER(PARTITION BY target_slot,port ORDER BY first_us,last_us) previous_end FROM supplied s)
SELECT 1 FROM matched WHERE n<>1
UNION ALL SELECT 1 FROM ordered WHERE previous_end>first_us
UNION ALL SELECT 1 FROM links i WHERE NOT EXISTS(SELECT 1 FROM required_grids r WHERE(r.target_slot,r.port)=(i.target_slot,i.port)))
 THEN RAISE EXCEPTION 'side_schedule_ports';END IF;
 INSERT INTO trading.side_schedule_request_seals VALUES(catalog,request,slot_count,input_count) ON CONFLICT DO NOTHING;
 PERFORM security.side_schedule_request_authority(catalog,request);
END$$;

CREATE FUNCTION security.append_side_schedule_chunk(catalog uuid,request uuid,schedule uuid,part text,first_sequence integer,payload text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE document jsonb;digest bytea;prior trading.side_schedule_chunks;items integer;part_name text=part;first_item integer=first_sequence;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 PERFORM 1 FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 IF octet_length(payload)>8388608 THEN RAISE EXCEPTION 'side_schedule_batch';END IF;
 document=payload::jsonb;
 IF jsonb_typeof(document) IS DISTINCT FROM 'array' OR jsonb_array_length(document) NOT BETWEEN 1 AND 2048 OR first_sequence<0
 THEN RAISE EXCEPTION 'side_schedule_batch';END IF;
 items=jsonb_array_length(document);digest=sha256(convert_to(payload,'UTF8'));
 SELECT * INTO prior FROM trading.side_schedule_chunks c WHERE(c.catalog_id,c.schedule_id,c.part,c.first_sequence)=(catalog,schedule,part_name,first_item);
 IF FOUND THEN
  IF(prior.item_count,prior.content_hash) IS DISTINCT FROM(items,digest) THEN RAISE EXCEPTION 'side_schedule_chunk_conflict';END IF;
  RETURN;
 END IF;
 IF part='grids' THEN PERFORM security.append_side_schedule_grids(catalog,request,schedule,document);
 ELSIF part='rows' THEN PERFORM security.append_side_schedule_rows(catalog,request,schedule,document);
 ELSIF part='edges' THEN PERFORM security.append_side_schedule_edges(catalog,request,schedule,document);
 ELSIF part IN('bindings','roots','ports') THEN PERFORM security.append_side_schedule_aux(catalog,request,schedule,part,document);
 ELSIF part IN('output_sets','output_members') THEN PERFORM security.append_side_schedule_outputs(catalog,request,schedule,
  CASE part WHEN 'output_sets' THEN 'sets' ELSE 'members' END,document);
 ELSE RAISE EXCEPTION 'side_schedule_part';END IF;
 INSERT INTO trading.side_schedule_chunks VALUES(catalog,schedule,part,first_sequence,items,digest);
END$$;
CREATE FUNCTION security.read_side_schedule_state(catalog uuid,request uuid,schedule uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 RETURN jsonb_build_object('sealedManifest',(SELECT encode(content_hash,'hex') FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule)),
  'chunks',coalesce((SELECT jsonb_agg(jsonb_build_object('part',c.part,'first',c.first_sequence,'count',c.item_count,'digest',encode(c.content_hash,'hex')) ORDER BY c.part,c.first_sequence)
   FROM trading.side_schedule_chunks c WHERE(c.catalog_id,c.schedule_id)=(catalog,schedule)),'[]'::jsonb));
END$$;

CREATE FUNCTION security.lock_side_schedule_request(catalog uuid,operation uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE command trading.side_schedule_requests;item record;head integer;
BEGIN
 command=security.side_schedule_request_authority(catalog,operation);
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_request_seals WHERE(catalog_id,request_id)=(catalog,operation)) THEN RAISE EXCEPTION 'side_schedule_unsealed';END IF;
 FOR item IN SELECT schedule_id FROM trading.side_schedule_request_items
  WHERE(catalog_id,request_id)=(catalog,operation) ORDER BY slot LOOP
  PERFORM security.side_schedule_stage_authority(catalog,operation,item.schedule_id);
  PERFORM 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,item.schedule_id) FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'side_schedule_unsealed';END IF;
 END LOOP;
 IF NOT FOUND THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 IF command.trade_id IS NOT NULL THEN
  SELECT max(revision) INTO head FROM(SELECT revision FROM trading.swap_trade_revisions WHERE(catalog_id,trade_id)=(catalog,command.trade_id)
   UNION ALL SELECT revision FROM trading.ppa_trade_revisions WHERE(catalog_id,trade_id)=(catalog,command.trade_id)) r;
  IF head IS DISTINCT FROM command.expected_revision AND NOT EXISTS(SELECT 1 FROM trading.swap_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation))
   AND NOT EXISTS(SELECT 1 FROM trading.ppa_trade_revisions WHERE(catalog_id,operation_id)=(catalog,operation))
  THEN RAISE EXCEPTION 'side_schedule_revision_conflict';END IF;
 END IF;
END$$;

CREATE FUNCTION security.publish_side_schedules(catalog uuid,operation uuid,instrument uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE command trading.side_schedule_requests;publication_number integer;from_at timestamptz;actor uuid;item record;member record;
 candidate trading.instrument_side_schedules;prior trading.instrument_side_schedules;seal trading.side_schedule_seals;source uuid;current_publication boolean;prior_composition uuid;
BEGIN
 command=security.side_schedule_request_authority(catalog,operation);
 actor=command.actor_id;
 PERFORM pg_advisory_xact_lock(730849102);
 PERFORM 1 FROM trading.side_schedule_request_seals WHERE(catalog_id,request_id)=(catalog,operation) FOR UPDATE;
 IF NOT FOUND THEN RAISE EXCEPTION 'side_schedule_unsealed';END IF;
 SELECT publication_order,effective_at INTO publication_number,from_at FROM(
  SELECT r.publication_order,r.effective_at FROM trading.swap_trade_revisions r
   WHERE(r.catalog_id,r.operation_id,r.instrument_id,r.actor_id)=(catalog,operation,instrument,actor)
  UNION ALL SELECT r.publication_order,r.effective_at FROM trading.ppa_trade_revisions r
   WHERE(r.catalog_id,r.operation_id,r.actor_id)=(catalog,operation,actor)
    AND EXISTS(SELECT 1 FROM trading.ppa_revision_bindings b WHERE(b.catalog_id,b.trade_id,b.revision,b.instrument_id)=(catalog,r.trade_id,r.revision,instrument))
 ) published;
 IF publication_number IS NULL THEN RAISE EXCEPTION 'side_schedule_atomic_publication';END IF;
 SELECT publication_xid=pg_current_xact_id() INTO STRICT current_publication FROM trading.publications
  WHERE(catalog_id,publication_order)=(catalog,publication_number);
 FOR item IN SELECT i.slot,i.schedule_id FROM trading.side_schedule_request_items i
  WHERE(i.catalog_id,i.request_id)=(catalog,operation) ORDER BY i.slot LOOP
  PERFORM security.side_schedule_stage_authority(catalog,operation,item.schedule_id);
  SELECT * INTO STRICT seal FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,item.schedule_id) FOR UPDATE;
  FOR source IN SELECT reference_catalog_id FROM trading.side_schedule_authorities
   WHERE(catalog_id,schedule_id,kind)=(catalog,item.schedule_id,'reference') LOOP
   IF NOT security.reference_access(source,'ReadMarketData') THEN RAISE EXCEPTION 'No current bound reference authority' USING ERRCODE='42501';END IF;
  END LOOP;
  FOR source IN SELECT market_catalog_id FROM trading.side_schedule_authorities
   WHERE(catalog_id,schedule_id,kind)=(catalog,item.schedule_id,'market') LOOP
   IF NOT security.market_access(source,'ReadMarketData') THEN RAISE EXCEPTION 'No current bound market authority' USING ERRCODE='42501';END IF;
  END LOOP;
  candidate.catalog_id=catalog;candidate.instrument_id=instrument;candidate.slot=item.slot;candidate.schedule_id=item.schedule_id;
  candidate.swap_fixed_side_id=NULL;candidate.swap_floating_side_id=NULL;candidate.swap_whole_term_side_id=NULL;candidate.ppa_side_id=NULL;
  SELECT s.id INTO candidate.ppa_side_id FROM trading.ppa_memberships m JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id)
   WHERE(m.catalog_id,m.instrument_id,m.position)=(catalog,instrument,item.slot);
  IF candidate.ppa_side_id IS NULL THEN
   SELECT CASE item.slot WHEN 0 THEN i.fixed_side_id END AS fixed,
    CASE item.slot WHEN 0 THEN i.first_floating_side_id WHEN 1 THEN i.floating_side_id END AS floating,
    CASE item.slot WHEN 0 THEN i.scheduled_first_id WHEN 1 THEN i.scheduled_second_id END AS whole
    INTO STRICT member FROM trading.swap_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument);
   candidate.swap_fixed_side_id=member.fixed;candidate.swap_floating_side_id=member.floating;candidate.swap_whole_term_side_id=member.whole;
  END IF;
  candidate.side_id=coalesce(candidate.ppa_side_id,candidate.swap_fixed_side_id,candidate.swap_floating_side_id,candidate.swap_whole_term_side_id);
  IF candidate.side_id IS NULL THEN RAISE EXCEPTION 'side_schedule_membership';END IF;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
   WHERE(h.catalog_id,h.id)=(catalog,item.schedule_id) AND s.family=CASE
    WHEN candidate.swap_fixed_side_id IS NOT NULL THEN 'fixed' WHEN candidate.swap_floating_side_id IS NOT NULL THEN 'floating'
    WHEN candidate.swap_whole_term_side_id IS NOT NULL THEN(SELECT kind FROM trading.swap_whole_term_sides WHERE(catalog_id,id)=(catalog,candidate.swap_whole_term_side_id))
    ELSE(SELECT CASE kind WHEN 'energy' THEN 'power' WHEN 'indexed' THEN 'floating' ELSE kind END FROM trading.ppa_sides WHERE(catalog_id,id)=(catalog,candidate.ppa_side_id)) END)
  THEN RAISE EXCEPTION 'side_schedule_family';END IF;
  SELECT * INTO prior FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,slot)=(catalog,instrument,item.slot);
  IF FOUND THEN
   IF prior IS DISTINCT FROM candidate THEN RAISE EXCEPTION 'side_schedule_membership_conflict';END IF;
  ELSE
   IF NOT current_publication THEN RAISE EXCEPTION 'side_schedule_atomic_publication';END IF;
   INSERT INTO trading.side_schedule_publications
    SELECT catalog,item.schedule_id,seal.content_hash,publication_number,i.effective_at FROM trading.instruments i WHERE(i.catalog_id,i.id)=(catalog,instrument)
    ON CONFLICT DO NOTHING;
   INSERT INTO trading.instrument_side_schedules SELECT candidate.*;
  END IF;
 END LOOP;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_request_inputs i JOIN trading.side_schedule_ports p ON(p.catalog_id,p.schedule_id,p.name)=(i.catalog_id,i.target_schedule,i.port)
  LEFT JOIN trading.ppa_memberships m ON(m.catalog_id,m.instrument_id,m.position)=(catalog,instrument,i.source_slot)
  WHERE(i.catalog_id,i.request_id)=(catalog,operation) AND(m.role IS DISTINCT FROM CASE p.slot_role
   WHEN '1' THEN 'energy_delivery' WHEN '2' THEN 'certificate_delivery' WHEN '3' THEN 'capacity_delivery' WHEN '4' THEN 'strike_payment'
   WHEN '5' THEN 'indexed_payment' WHEN '6' THEN 'fixed_charge' WHEN '7' THEN 'certificate_payment' WHEN '8' THEN 'capacity_payment' ELSE NULL END
   OR m.ordinal IS DISTINCT FROM p.slot_ordinal)) THEN RAISE EXCEPTION 'side_schedule_port_membership';END IF;
 SELECT request_id INTO prior_composition FROM trading.instrument_schedule_compositions WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF FOUND THEN
  IF EXISTS((SELECT target_slot,target_schedule,port,source_slot,source_schedule,output_name FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,operation)
   EXCEPT SELECT target_slot,target_schedule,port,source_slot,source_schedule,output_name FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,prior_composition))
   UNION ALL(SELECT target_slot,target_schedule,port,source_slot,source_schedule,output_name FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,prior_composition)
   EXCEPT SELECT target_slot,target_schedule,port,source_slot,source_schedule,output_name FROM trading.side_schedule_request_inputs WHERE(catalog_id,request_id)=(catalog,operation)))
  THEN RAISE EXCEPTION 'side_schedule_composition_conflict';END IF;
 ELSE
  IF NOT current_publication THEN RAISE EXCEPTION 'side_schedule_atomic_publication';END IF;
  INSERT INTO trading.instrument_schedule_compositions VALUES(catalog,instrument,operation);
 END IF;
 IF NOT security.trading_access(catalog,command.permission) THEN RAISE EXCEPTION 'Schedule publication authority expired' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION trading.require_instrument_side_schedules() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE expected integer;actual integer;
BEGIN
 IF TG_TABLE_NAME='ppa_instruments' THEN
  SELECT count(*) INTO expected FROM trading.ppa_memberships WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 ELSE expected=2;END IF;
 SELECT count(*) INTO actual FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id);
 IF expected=0 OR actual<>expected OR NOT EXISTS(SELECT 1 FROM trading.instrument_schedule_compositions WHERE(catalog_id,instrument_id)=(NEW.catalog_id,NEW.instrument_id))
 THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 RETURN NEW;
END$$;
CREATE FUNCTION security.publish_operation_side_schedules(catalog uuid,operation uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE instrument uuid;
BEGIN
 PERFORM security.side_schedule_request_authority(catalog,operation);
 SELECT i INTO STRICT instrument FROM(
  SELECT r.instrument_id i FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.operation_id)=(catalog,operation)
  UNION ALL SELECT b.instrument_id FROM trading.ppa_trade_revisions r JOIN trading.ppa_revision_bindings b
   ON(b.catalog_id,b.trade_id,b.revision)=(r.catalog_id,r.trade_id,r.revision)
   WHERE(r.catalog_id,r.operation_id)=(catalog,operation) AND NOT EXISTS(SELECT 1 FROM trading.ppa_revision_bindings old
    WHERE(old.catalog_id,old.trade_id,old.revision,old.binding_id)=(r.catalog_id,r.trade_id,r.previous_revision,b.binding_id))
 ) replacements;
 PERFORM security.publish_side_schedules(catalog,operation,instrument);
END$$;
CREATE CONSTRAINT TRIGGER retained_side_schedules AFTER INSERT ON trading.swap_instruments
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.require_instrument_side_schedules();
CREATE CONSTRAINT TRIGGER retained_side_schedules AFTER INSERT ON trading.ppa_instruments
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.require_instrument_side_schedules();

CREATE FUNCTION security.require_side_schedule_read_scope(catalog uuid,context uuid,as_of timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR(context IS NULL)<>(as_of IS NULL)
 THEN RAISE EXCEPTION 'No current schedule read authority' USING ERRCODE='42501';END IF;
 IF pg_has_role(session_user,'openetrm_risk_worker','MEMBER') AND NOT risk.worker_read()
 THEN RAISE EXCEPTION 'No current retained schedule worker claim' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.require_side_schedule_read(catalog uuid,instrument uuid,schedule uuid,context uuid,as_of timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p trading.side_schedule_publications;reference_catalog uuid;allowed boolean;
BEGIN
 PERFORM security.require_side_schedule_read_scope(catalog,context,as_of);
 IF NOT EXISTS(SELECT 1 FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id,schedule_id)=(catalog,instrument,schedule))
 THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.instrument_schedule_compositions WHERE(catalog_id,instrument_id)=(catalog,instrument)) THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 SELECT * INTO STRICT p FROM trading.side_schedule_publications WHERE(catalog_id,schedule_id)=(catalog,schedule);
 IF context IS NULL THEN
  allowed=EXISTS(SELECT 1 FROM trading.instruments i WHERE(i.catalog_id,i.id)=(catalog,instrument)
   AND security.trading_visible(catalog,i.publication_order,i.effective_at)) AND(
   EXISTS(SELECT 1 FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument)
    AND security.trading_visible(catalog,r.publication_order,r.effective_at)) OR
   EXISTS(SELECT 1 FROM trading.ppa_revision_bindings b JOIN trading.ppa_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(b.catalog_id,b.trade_id,b.revision)
    WHERE(b.catalog_id,b.instrument_id)=(catalog,instrument) AND security.trading_visible(catalog,r.publication_order,r.effective_at)));
 ELSE
  allowed=EXISTS(SELECT 1 FROM trading.instruments i WHERE(i.catalog_id,i.id)=(catalog,instrument)
   AND security.ppa_visible_at(catalog,i.publication_order,i.effective_at,context,as_of)) AND(
   EXISTS(SELECT 1 FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.instrument_id)=(catalog,instrument)
    AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)) OR
   EXISTS(SELECT 1 FROM trading.ppa_revision_bindings b JOIN trading.ppa_trade_revisions r ON(r.catalog_id,r.trade_id,r.revision)=(b.catalog_id,b.trade_id,b.revision)
    WHERE(b.catalog_id,b.instrument_id)=(catalog,instrument) AND security.ppa_visible_at(catalog,r.publication_order,r.effective_at,context,as_of)));
 END IF;
 SELECT s.reference_catalog_id INTO STRICT reference_catalog FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
  WHERE(h.catalog_id,h.id)=(catalog,schedule);
 IF allowed IS NOT TRUE OR NOT security.reference_access(reference_catalog,'ReadMarketData')
  OR EXISTS(SELECT 1 FROM trading.side_schedule_authorities a WHERE(a.catalog_id,a.schedule_id)=(catalog,schedule)
   AND CASE a.kind WHEN 'reference' THEN NOT security.reference_access(a.authority_catalog_id,'ReadMarketData') ELSE NOT security.market_access(a.authority_catalog_id,'ReadMarketData') END)
 THEN RAISE EXCEPTION 'No current retained schedule access' USING ERRCODE='42501';END IF;
END$$;

CREATE FUNCTION security.read_instrument_side_schedule_headers(catalog uuid,instrument uuid,context uuid,as_of timestamptz) RETURNS SETOF jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE item trading.instrument_side_schedules;expected integer;actual integer=0;
BEGIN
 PERFORM security.require_side_schedule_read_scope(catalog,context,as_of);
 SELECT count(*) INTO expected FROM trading.ppa_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF expected=0 AND EXISTS(SELECT 1 FROM trading.swap_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument)) THEN expected=2;END IF;
 FOR item IN SELECT * FROM trading.instrument_side_schedules WHERE(catalog_id,instrument_id)=(catalog,instrument) ORDER BY slot LOOP
  PERFORM security.require_side_schedule_read(catalog,instrument,item.schedule_id,context,as_of);
  actual=actual+1;RETURN NEXT jsonb_build_object('side_id',item.side_id,'schedule_id',item.schedule_id,'slot',item.slot);
 END LOOP;
 IF expected=0 OR actual<>expected THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
END$$;

CREATE FUNCTION security.side_schedule_page_authority(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz,first_row integer,maximum integer)
 RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF first_row IS NULL OR maximum IS NULL OR first_row<0 OR maximum NOT BETWEEN 1 AND 2048 THEN RAISE EXCEPTION 'side_schedule_page';END IF;
 IF request IS NOT NULL THEN
  IF instrument IS NOT NULL OR context IS NOT NULL OR as_of IS NOT NULL THEN RAISE EXCEPTION 'side_schedule_scope';END IF;
  PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 ELSE PERFORM security.require_side_schedule_read(catalog,instrument,schedule,context,as_of);END IF;
END$$;
CREATE FUNCTION security.read_side_schedule_header(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz)
 RETURNS SETOF jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.side_schedule_page_authority(catalog,schedule,request,instrument,context,as_of,0,1);
 RETURN QUERY SELECT to_jsonb(h)||jsonb_build_object('family',s.family,'economic_identity',s.economic_identity,
  'pricing_binding_identity',s.pricing_binding_identity,'manifest',encode(h.content_hash,'hex'))
  FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id) WHERE(h.catalog_id,h.id)=(catalog,schedule);
END$$;
CREATE FUNCTION security.read_side_schedule_grids(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz,first_row integer,maximum integer)
 RETURNS TABLE(ordinal integer,local_key text,clock_catalog_id uuid,clock_revision_id uuid,clock_code text,month date,
  first_row_ordinal integer,origin timestamptz,step_ticks bigint,axis_count integer,selection integer[],lengths integer[],
  operating_days integer[],local_minutes integer[],occurrences integer[]) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.side_schedule_page_authority(catalog,schedule,request,instrument,context,as_of,first_row,maximum);
 RETURN QUERY SELECT g.ordinal,g.local_key,g.clock_catalog_id,g.clock_revision_id,g.clock_code::text,g.month,
  g.first_row_ordinal,g.origin::timestamptz,g.step_ticks,g.axis_count,g.selection,g.lengths,g.operating_days,g.local_minutes,g.occurrences
  FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule)
   AND g.ordinal>=first_row ORDER BY g.ordinal LIMIT maximum;
END$$;
CREATE FUNCTION security.read_side_schedule_rows(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz,first_row integer,maximum integer)
 RETURNS TABLE(ordinal integer,local_key text,role text,operation text,
  operation_version integer,output_kind text,unit_catalog_id uuid,unit_revision_id uuid,
  unit_code text,currency_catalog_id uuid,currency_revision_id uuid,currency_code text,
  quote_multiplier text,delivery_from date,delivery_to date,interval_from timestamptz,
  interval_to timestamptz,operating_date date,hour_beginning smallint,minute smallint,
  occurrence smallint,observation_from date,observation_to date,value_at timestamptz,
  payment_ordinal integer,occurrence_key text,terms_digest text,parameters jsonb,grid_ordinal integer) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.side_schedule_page_authority(catalog,schedule,request,instrument,context,as_of,first_row,maximum);
 RETURN QUERY SELECT r.ordinal,r.local_key,r.role,r.operation,r.operation_version,
  r.output_kind,r.unit_catalog_id,r.unit_revision_id,r.unit_code::text,r.currency_catalog_id,
  r.currency_revision_id,r.currency_code::text,trim_scale(r.quote_multiplier)::text,r.delivery_from,r.delivery_to,
  r.interval_from::timestamptz,r.interval_to::timestamptz,r.operating_date,r.hour_beginning,r.minute,
  r.occurrence,r.observation_from,r.observation_to,r.value_at::timestamptz,r.payment_ordinal,
  r.occurrence_key,r.terms_digest,r.parameters,r.grid_ordinal
  FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND r.ordinal>=0 AND r.ordinal>=first_row ORDER BY r.ordinal LIMIT maximum;
END$$;
CREATE FUNCTION security.read_side_schedule_edges(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz,first_row integer,maximum integer)
 RETURNS TABLE(target integer,port text,ordinal integer,source integer) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.side_schedule_page_authority(catalog,schedule,request,instrument,context,as_of,first_row,maximum);
 RETURN QUERY SELECT r.target,r.port,r.ordinal,r.source
  FROM trading.side_schedule_edges r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND r.sequence_ordinal>=0 AND r.sequence_ordinal>=first_row ORDER BY r.sequence_ordinal LIMIT maximum;
END$$;
CREATE FUNCTION security.read_side_schedule_bindings(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz,first_row integer,maximum integer)
 RETURNS TABLE(row_ordinal integer,port text,kind text,reference_catalog_id uuid,
  reference_revision_id uuid,index_code text,quantity_code text,location_code text,
  quote_code text,source_catalog_id uuid,definition_revision_id uuid,curve_code text,
  contract_id uuid,shaped_curve_code text,quantity_source_code text,asset_code text,
  series_id uuid,custom_catalog_id uuid,custom_curve_id uuid,custom_version integer,
  output_node text,"position" integer,coordinate text,source_code text,
  fixing_index_code text,fixing_from date,fixing_to date) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.side_schedule_page_authority(catalog,schedule,request,instrument,context,as_of,first_row,maximum);
 RETURN QUERY SELECT r.row_ordinal,r.port,r.kind,r.reference_catalog_id,r.reference_revision_id,
  r.index_code::text,r.quantity_code::text,r.location_code::text,r.quote_code::text,r.source_catalog_id,
  r.definition_revision_id,r.curve_code::text,r.contract_id,r.shaped_curve_code::text,r.quantity_source_code::text,
  r.asset_code::text,r.series_id,r.custom_catalog_id,r.custom_curve_id,r.custom_version,
  r.output_node,r.position,r.coordinate,r.source_code::text,r.fixing_index_code::text,
  r.fixing_from,r.fixing_to
  FROM trading.side_schedule_bindings r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND r.sequence_ordinal>=0 AND r.sequence_ordinal>=first_row ORDER BY r.sequence_ordinal LIMIT maximum;
END$$;
CREATE FUNCTION security.read_side_schedule_roots(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz,first_row integer,maximum integer)
 RETURNS TABLE(name text,row_ordinal integer,measure text) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.side_schedule_page_authority(catalog,schedule,request,instrument,context,as_of,first_row,maximum);
 RETURN QUERY SELECT r.name,r.row_ordinal,r.measure
  FROM trading.side_schedule_roots r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND r.sequence_ordinal>=0 AND r.sequence_ordinal>=first_row ORDER BY r.sequence_ordinal LIMIT maximum;
END$$;
CREATE FUNCTION security.read_side_schedule_ports(catalog uuid,schedule uuid,request uuid,instrument uuid,context uuid,as_of timestamptz,first_row integer,maximum integer)
 RETURNS TABLE(name text,slot_role text,slot_ordinal integer,kind text,
  unit_catalog_id uuid,unit_revision_id uuid,unit_code text,shape_identity text) LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.side_schedule_page_authority(catalog,schedule,request,instrument,context,as_of,first_row,maximum);
 RETURN QUERY SELECT r.name,r.slot_role,r.slot_ordinal,r.kind,r.unit_catalog_id,
  r.unit_revision_id,r.unit_code::text,r.shape_identity
  FROM trading.side_schedule_ports r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND r.sequence_ordinal>=0 AND r.sequence_ordinal>=first_row ORDER BY r.sequence_ordinal LIMIT maximum;
END$$;

CREATE FUNCTION security.side_schedule_request_authority(catalog uuid,operation uuid) RETURNS trading.side_schedule_requests
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE command trading.side_schedule_requests;actor uuid;
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades')
 THEN RAISE EXCEPTION 'No current schedule request authority' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
  AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user AND market_context_id IS NULL;
 SELECT * INTO command FROM trading.side_schedule_requests WHERE(catalog_id,request_id,actor_id)=(catalog,operation,actor);
 IF actor IS NULL OR NOT FOUND OR NOT security.trading_access(catalog,command.permission)
 THEN RAISE EXCEPTION 'No current schedule request authority' USING ERRCODE='42501';END IF;
 RETURN command;
END$$;
CREATE FUNCTION security.side_schedule_stage_authority(catalog uuid,request uuid,schedule uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE reference_catalog uuid;
BEGIN
 PERFORM security.side_schedule_request_authority(catalog,request);
 IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_request_items WHERE(catalog_id,request_id,schedule_id)=(catalog,request,schedule))
 THEN RAISE EXCEPTION 'No current schedule stage authority' USING ERRCODE='42501';END IF;
 SELECT s.reference_catalog_id INTO reference_catalog FROM trading.side_schedules h JOIN trading.sides s
  ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id) WHERE(h.catalog_id,h.id)=(catalog,schedule);
 IF NOT security.reference_access(reference_catalog,'ReadMarketData') OR EXISTS(SELECT 1 FROM trading.side_schedule_authorities a
  WHERE(a.catalog_id,a.schedule_id)=(catalog,schedule) AND CASE a.kind WHEN 'reference' THEN NOT security.reference_access(a.authority_catalog_id,'ReadMarketData')
   ELSE NOT security.market_access(a.authority_catalog_id,'ReadMarketData') END)
 THEN RAISE EXCEPTION 'No current schedule reference authority' USING ERRCODE='42501';END IF;
END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['sides','side_schedule_requests','side_schedules','side_schedule_request_items','side_schedule_grids','side_schedule_rows','side_schedule_edges',
  'side_schedule_bindings','side_schedule_roots','side_schedule_ports','side_schedule_authorities','side_schedule_output_sets','side_schedule_output_members',
  'side_schedule_request_inputs','side_schedule_request_seals','side_schedule_chunks','side_schedule_seals','side_schedule_publications','instrument_side_schedules','instrument_schedule_compositions'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT,UPDATE ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE POLICY internal_content ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['side_schedule_grids','side_schedule_rows','side_schedule_edges','side_schedule_bindings','side_schedule_roots','side_schedule_ports','side_schedule_authorities','side_schedule_output_sets','side_schedule_output_members','side_schedule_chunks'] LOOP
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE INSERT OR UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION trading.guard_side_schedule_content()',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime,openetrm_risk_worker',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY temporal_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.side_schedule_visible(catalog_id,schedule_id))',t);
  EXECUTE format('CREATE POLICY worker_read ON trading.%I FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',t);
  EXECUTE format('CREATE POLICY worker_temporal_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(security.side_schedule_visible(catalog_id,schedule_id))',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['sides','side_schedule_requests','side_schedules','side_schedule_request_items','side_schedule_request_inputs','side_schedule_request_seals','side_schedule_seals','side_schedule_publications','instrument_side_schedules','instrument_schedule_compositions'] LOOP
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
 END LOOP;
END$$;

ALTER FUNCTION security.side_schedule_visible(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.retained_side_visible(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.retained_payment_rule_visible(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.begin_side_schedule(uuid,uuid,integer,jsonb,text,uuid,integer,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.side_schedule_stage_authority(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.side_schedule_request_authority(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.require_side_schedule_read_scope(uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.side_schedule_visible(uuid,uuid),trading.guard_side_schedule_content() FROM PUBLIC;
REVOKE ALL ON FUNCTION security.retained_side_visible(uuid,uuid),security.retained_payment_rule_visible(uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.begin_side_schedule(uuid,uuid,integer,jsonb,text,uuid,integer,jsonb),security.side_schedule_stage_authority(uuid,uuid,uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.side_schedule_request_authority(uuid,uuid),security.require_side_schedule_read_scope(uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.side_schedule_visible(uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;
GRANT EXECUTE ON FUNCTION security.retained_side_visible(uuid,uuid),security.retained_payment_rule_visible(uuid,uuid) TO openetrm_runtime,openetrm_risk_worker;
DO $$DECLARE table_name text;BEGIN
 FOREACH table_name IN ARRAY ARRAY['swap_fixed_sides','swap_floating_sides','swap_whole_term_sides'] LOOP
  EXECUTE format('CREATE POLICY retained_instrument_read ON trading.%I FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(security.retained_side_visible(catalog_id,id))',table_name);
 END LOOP;
 FOREACH table_name IN ARRAY ARRAY['swap_side_periods','swap_period_projections'] LOOP
  EXECUTE format('CREATE POLICY retained_instrument_read ON trading.%I FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(security.retained_side_visible(catalog_id,side_id))',table_name);
 END LOOP;
END$$;
CREATE POLICY retained_instrument_read ON trading.swap_payment_rules FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(security.retained_payment_rule_visible(catalog_id,id));
GRANT EXECUTE ON FUNCTION security.begin_side_schedule(uuid,uuid,integer,jsonb,text,uuid,integer,jsonb) TO openetrm_runtime;
DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure AS signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
  WHERE n.nspname='security' AND p.proname IN('append_side_schedule_grids','append_side_schedule_rows','append_side_schedule_edges','append_side_schedule_aux',
   'seal_side_schedule','append_side_schedule_outputs','seal_side_schedule_request','append_side_schedule_chunk','read_side_schedule_state','lock_side_schedule_request','publish_side_schedules','publish_operation_side_schedules','side_schedule_page_authority','read_side_schedule_header','read_side_schedule_grids','read_side_schedule_rows','read_side_schedule_edges','read_side_schedule_bindings','read_side_schedule_roots','read_side_schedule_ports','read_instrument_side_schedule_headers','require_side_schedule_read') LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname NOT IN('require_side_schedule_read','side_schedule_page_authority') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION security.read_instrument_side_schedule_headers(uuid,uuid,uuid,timestamptz),
 security.read_side_schedule_header(uuid,uuid,uuid,uuid,uuid,timestamptz),
 security.read_side_schedule_grids(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer),
 security.read_side_schedule_rows(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer),
 security.read_side_schedule_edges(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer),
 security.read_side_schedule_bindings(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer),
 security.read_side_schedule_roots(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer),
 security.read_side_schedule_ports(uuid,uuid,uuid,uuid,uuid,timestamptz,integer,integer) TO openetrm_risk_worker;
ALTER FUNCTION trading.require_instrument_side_schedules() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_instrument_side_schedules() FROM PUBLIC;
COMMIT;
