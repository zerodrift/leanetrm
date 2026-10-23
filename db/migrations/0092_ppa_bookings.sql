-- Exact reusable PPA terms and immutable originating-delivery binding revisions.
-- JSON is a bounded command/receipt transport; economic relationships are typed foreign keys.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE trading.ppa_sides (
 catalog_id uuid NOT NULL,id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 kind text NOT NULL CHECK(kind IN('energy','certificate','capacity','fixed','indexed')),
 clock_code ref_data.code NOT NULL,interval_minutes integer NOT NULL CHECK(interval_minutes IN(15,60)),
 delivery_from date NOT NULL,delivery_to date NOT NULL,currency_code ref_data.code NOT NULL,unit_quantity_code ref_data.code NOT NULL,
 payment_rule_id uuid NOT NULL,quantity_kind text NOT NULL CHECK(quantity_kind IN('volume','profile','slots','accredited')),
 stage text CHECK(stage IN('generated','delivered')),profile_capacity_code ref_data.code,profile_energy_code ref_data.code,
 hours_membership text CHECK(hours_membership IN('all','peak','off')),tou_product_id uuid,tou_version integer,holiday_calendar_code ref_data.code,
 location_code ref_data.code,fixed_kind text CHECK(fixed_kind IN('flat','escalated','stepped')),
 price_quantity_code ref_data.code,price_multiplier ref_data.exact_number CHECK(price_multiplier>0),price ref_data.exact_number,
 escalation_rate ref_data.exact_number CHECK(escalation_rate>-1),escalation_base_date date,escalation_basis text CHECK(escalation_basis='anniversary_step_v1'),
 index_curve_code ref_data.code,index_shaped_curve_code ref_data.code,index_multiplier ref_data.exact_number CHECK(index_multiplier>0),adder ref_data.exact_number,
 attribute_quantity_code ref_data.code,conversion ref_data.exact_number CHECK(conversion>0),eligibility text,holding text,disposition text,capacity_rule text,
 economic_hash text NOT NULL,publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),UNIQUE(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,unit_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,profile_capacity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,profile_energy_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,holiday_calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,location_code) REFERENCES ref_data.locations(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,price_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,attribute_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,payment_rule_id) REFERENCES trading.swap_payment_rules(catalog_id,id),
 FOREIGN KEY(catalog_id,tou_product_id,tou_version) REFERENCES market_data.tou_product_versions(catalog_id,product_id,version),
 FOREIGN KEY(catalog_id,definition_revision_id,index_curve_code) REFERENCES market_data.curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,definition_revision_id,index_shaped_curve_code) REFERENCES market_data.shaped_curves(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(delivery_from<delivery_to AND extract(day FROM delivery_from)=1 AND extract(day FROM delivery_to)=1),
 CHECK((quantity_kind='volume')=(stage IS NOT NULL)),CHECK((quantity_kind='profile')=(hours_membership IS NOT NULL)),
 CHECK((hours_membership IS NULL)=(holiday_calendar_code IS NULL)),CHECK((hours_membership IN('peak','off'))=(tou_product_id IS NOT NULL) OR hours_membership IS NULL),
 CHECK((tou_product_id IS NULL)=(tou_version IS NULL)),CHECK((kind='fixed')=(fixed_kind IS NOT NULL)),
 CHECK((kind='indexed')=(index_multiplier IS NOT NULL)),CHECK(NOT(index_curve_code IS NOT NULL AND index_shaped_curve_code IS NOT NULL))
);
CREATE INDEX ppa_side_economics ON trading.ppa_sides(catalog_id,economic_hash);
CREATE TABLE trading.ppa_side_sources (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,definition_revision_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 99),source_code ref_data.code NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,PRIMARY KEY(catalog_id,side_id,ordinal),UNIQUE(catalog_id,side_id,source_code),
 FOREIGN KEY(catalog_id,side_id,definition_revision_id) REFERENCES trading.ppa_sides(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,source_code) REFERENCES market_data.volume_sources(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.ppa_side_assets (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,definition_revision_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 99),asset_code ref_data.code NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,PRIMARY KEY(catalog_id,side_id,ordinal),UNIQUE(catalog_id,side_id,asset_code),
 FOREIGN KEY(catalog_id,side_id,definition_revision_id) REFERENCES trading.ppa_sides(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,asset_code) REFERENCES market_data.generation_assets(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.ppa_side_slots (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,position integer NOT NULL CHECK(position BETWEEN 0 AND 7),role text NOT NULL CHECK(role IN('energy_delivery','certificate_delivery','capacity_delivery')),
 ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 7),publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,side_id,position),UNIQUE(catalog_id,side_id,role,ordinal),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.ppa_sides(catalog_id,id),FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.ppa_price_steps (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 359),from_date date NOT NULL,price ref_data.exact_number NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,PRIMARY KEY(catalog_id,side_id,ordinal),UNIQUE(catalog_id,side_id,from_date),
 FOREIGN KEY(catalog_id,side_id) REFERENCES trading.ppa_sides(catalog_id,id),FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.ppa_instruments (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 clock_code ref_data.code NOT NULL,interval_minutes integer NOT NULL CHECK(interval_minutes IN(15,60)),delivery_from date NOT NULL,delivery_to date NOT NULL,
 currency_code ref_data.code NOT NULL,unit_quantity_code ref_data.code NOT NULL,discounting boolean NOT NULL,implementation text NOT NULL CHECK(implementation='ppa_instrument_v1'),
 economic_hash text NOT NULL,publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id),UNIQUE(catalog_id,instrument_id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.instruments(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,unit_quantity_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE INDEX ppa_instrument_economics ON trading.ppa_instruments(catalog_id,economic_hash);
CREATE TABLE trading.ppa_instrument_assets (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,definition_revision_id uuid NOT NULL,ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 99),asset_code ref_data.code NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,PRIMARY KEY(catalog_id,instrument_id,ordinal),UNIQUE(catalog_id,instrument_id,asset_code),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.ppa_instruments(catalog_id,instrument_id),
 FOREIGN KEY(catalog_id,definition_revision_id,asset_code) REFERENCES market_data.generation_assets(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.ppa_memberships (
 catalog_id uuid NOT NULL,instrument_id uuid NOT NULL,position integer NOT NULL CHECK(position BETWEEN 0 AND 7),role text NOT NULL CHECK(role IN('energy_delivery','certificate_delivery','capacity_delivery','strike_payment','indexed_payment','fixed_charge','certificate_payment','capacity_payment')),
 ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 7),side_id uuid NOT NULL,coefficient ref_data.exact_number NOT NULL CHECK(coefficient IN(-1,1)),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,instrument_id,role,ordinal),UNIQUE(catalog_id,instrument_id,position),UNIQUE(catalog_id,instrument_id,side_id),
 FOREIGN KEY(catalog_id,instrument_id) REFERENCES trading.ppa_instruments(catalog_id,instrument_id),FOREIGN KEY(catalog_id,side_id) REFERENCES trading.ppa_sides(catalog_id,id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.ppa_trade_origins (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id),UNIQUE(catalog_id,trade_id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,trade_id) REFERENCES trading.trades(catalog_id,id),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
CREATE TABLE trading.ppa_trade_revisions (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL CHECK(revision BETWEEN 1 AND 256),previous_revision integer,
 event_id uuid NOT NULL,event_schema text NOT NULL CHECK(event_schema='ppa_booking_v1'),event_kind text NOT NULL CHECK(event_kind IN('booked','amended','terminated')),
 book_id uuid NOT NULL,definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 boundary_at ref_data.instant,execution_at ref_data.instant NOT NULL,effective_at ref_data.instant NOT NULL,
 operation_id uuid NOT NULL,actor_id uuid NOT NULL REFERENCES security.principals(id),reason text NOT NULL CHECK(length(btrim(reason)) BETWEEN 1 AND 500),memo text NOT NULL CHECK(length(memo)<=2000),
 legal_entity_id uuid NOT NULL,legal_entity_revision integer NOT NULL,counterparty_id uuid NOT NULL,counterparty_revision integer NOT NULL,
 request jsonb NOT NULL,publication_order integer NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision),UNIQUE(catalog_id,operation_id),UNIQUE(catalog_id,event_id),
 UNIQUE(catalog_id,trade_id,revision,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,trade_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES trading.ppa_trade_origins(catalog_id,trade_id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,trade_id,previous_revision) REFERENCES trading.ppa_trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,book_id) REFERENCES trading.books(catalog_id,id),
 FOREIGN KEY(catalog_id,legal_entity_id,legal_entity_revision) REFERENCES trading.legal_entities(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,counterparty_id,counterparty_revision) REFERENCES trading.counterparties(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK((revision=1 AND previous_revision IS NULL AND event_kind='booked' AND boundary_at IS NULL) OR(revision>1 AND previous_revision=revision-1 AND event_kind<>'booked' AND boundary_at IS NOT NULL))
);
CREATE TABLE trading.ppa_revision_bindings (
 catalog_id uuid NOT NULL,trade_id uuid NOT NULL,revision integer NOT NULL,binding_id uuid NOT NULL,instrument_id uuid NOT NULL,
 definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 coverage_from ref_data.instant NOT NULL,coverage_to ref_data.instant NOT NULL,quantity ref_data.exact_number NOT NULL CHECK(quantity>0),
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,trade_id,revision,binding_id),CHECK(coverage_from<coverage_to),
 FOREIGN KEY(catalog_id,trade_id,revision,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES trading.ppa_trade_revisions(catalog_id,trade_id,revision,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,instrument_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES trading.ppa_instruments(catalog_id,instrument_id,definition_revision_id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['ppa_sides','ppa_side_sources','ppa_side_assets','ppa_side_slots','ppa_price_steps','ppa_instruments','ppa_instrument_assets','ppa_memberships','ppa_trade_origins','ppa_trade_revisions','ppa_revision_bindings'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt()',t);
  EXECUTE format('CREATE POLICY scoped_read ON trading.%I FOR SELECT TO openetrm_runtime USING(true)',t);
  EXECUTE format('CREATE POLICY temporal_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.trading_visible(catalog_id,publication_order,effective_at))',t);
  EXECUTE format('GRANT SELECT ON trading.%I TO openetrm_runtime',t);EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
 END LOOP;
END$$;
GRANT SELECT ON market_data.tou_product_versions,market_data.volume_sources,market_data.generation_assets,market_data.shaped_curves,ref_data.clock_rules,ref_data.calendars TO openetrm_security_owner;

CREATE FUNCTION security.ppa_keys(value jsonb,keys text[]) RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF value IS NULL OR jsonb_typeof(value)<>'object' OR NOT value ?& keys OR (SELECT count(*) FROM jsonb_object_keys(value))<>cardinality(keys)
 THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
END$$;
CREATE FUNCTION security.ppa_number(value text) RETURNS numeric LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE n numeric;BEGIN
 IF value IS NULL OR value !~ '^-?[0-9]+(\.[0-9]+)?$' OR length(value)>64 THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
 n=value::numeric;IF abs(n)>=1e24 OR scale(n)>18 THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;RETURN trim_scale(n);
END$$;
CREATE FUNCTION security.ppa_normalize(value jsonb) RETURNS jsonb LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE result jsonb;k text;v jsonb;BEGIN
 IF jsonb_typeof(value)='object' THEN
  result='{}';FOR k,v IN SELECT * FROM jsonb_each(value) LOOP
   IF k IN('price','rate','multiplier','indexMultiplier','adder','conversion','coefficient') AND v<>'null'::jsonb THEN
    IF jsonb_typeof(v)<>'string' THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
    v=to_jsonb(security.ppa_number(v#>>'{}')::text);
   ELSE
    v=security.ppa_normalize(v);
    -- The managed SteppedPrice contract orders dated strikes by their start.
    -- Other arrays retain their authored identity order.
    IF k='steps' AND jsonb_typeof(v)='array' THEN
     SELECT coalesce(jsonb_agg(x.step ORDER BY x.step->>'from'),'[]') INTO v FROM jsonb_array_elements(v) x(step);
    END IF;
   END IF;
   result=result||jsonb_build_object(k,v);
  END LOOP;RETURN result;
 ELSIF jsonb_typeof(value)='array' THEN SELECT coalesce(jsonb_agg(security.ppa_normalize(a.v) ORDER BY a.n),'[]') INTO result FROM jsonb_array_elements(value) WITH ORDINALITY a(v,n);RETURN result;
 ELSE RETURN value;END IF;
END$$;
CREATE FUNCTION security.ppa_day_start(rc uuid,rr uuid,clock_name text,day date) RETURNS timestamptz LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE c ref_data.clock_rules;at timestamptz;BEGIN
 SELECT * INTO c FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(rc,rr,clock_name);
 IF NOT FOUND THEN RAISE EXCEPTION 'ppa_reference_revision' USING ERRCODE='P0001';END IF;
 at=CASE WHEN c.kind='fixed_offset' THEN (day::timestamp AT TIME ZONE 'UTC')-make_interval(secs=>c.fixed_offset_seconds) ELSE day::timestamp AT TIME ZONE c.iana_zone END;
 IF NOT market_data.ppa_day_boundary(at,rc,rr,clock_name::ref_data.code) THEN RAISE EXCEPTION 'ppa_segment_boundary' USING ERRCODE='P0001';END IF;RETURN at;
END$$;
CREATE FUNCTION security.ppa_boundary(rc uuid,rr uuid,clock_name text,minutes integer,at timestamptz,whole_month boolean) RETURNS boolean LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE c ref_data.clock_rules;local_at timestamp;BEGIN
 SELECT * INTO c FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(rc,rr,clock_name);
 IF NOT FOUND OR at IS NULL OR NOT isfinite(at) THEN RETURN false;END IF;
 local_at=CASE WHEN c.kind='fixed_offset' THEN(at AT TIME ZONE 'UTC')+make_interval(secs=>c.fixed_offset_seconds) ELSE at AT TIME ZONE c.iana_zone END;
 RETURN extract(second FROM local_at)=0 AND mod(extract(minute FROM local_at)::integer,minutes)=0 AND
  (NOT whole_month OR(local_at=date_trunc('month',local_at) AND market_data.ppa_day_boundary(at,rc,rr,clock_name::ref_data.code)));
END$$;
CREATE FUNCTION security.ppa_side_document(catalog uuid,side uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('kind',s.kind,'quantity',jsonb_build_object('kind',s.quantity_kind,
  'sources',coalesce((SELECT jsonb_agg(x.source_code ORDER BY x.ordinal) FROM trading.ppa_side_sources x WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]'),
  'stage',s.stage,'capacity',s.profile_capacity_code,'energy',s.profile_energy_code,
  'hours',CASE WHEN s.hours_membership IS NULL THEN NULL ELSE jsonb_build_object('membership',s.hours_membership,'touProduct',s.tou_product_id,'touVersion',s.tou_version,'calendar',s.holiday_calendar_code) END,
  'slots',coalesce((SELECT jsonb_agg(jsonb_build_object('role',x.role,'ordinal',x.ordinal) ORDER BY x.position) FROM trading.ppa_side_slots x WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]'),
  'assets',coalesce((SELECT jsonb_agg(x.asset_code ORDER BY x.ordinal) FROM trading.ppa_side_assets x WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]')),
  'paymentRule',jsonb_build_object('frequency',r.frequency,'rule',r.rule,'days',r.days,'dayOfMonth',r.day_of_month,'dayBasis',r.day_basis,'shortMonth',r.short_month,'adjustment',r.adjustment,'calendar',r.calendar_code,'implementation',r.implementation),
  'location',s.location_code,
  'fixedPrice',CASE WHEN s.fixed_kind IS NULL THEN NULL ELSE jsonb_build_object('kind',s.fixed_kind,'quantity',s.price_quantity_code,'multiplier',s.price_multiplier::text,'price',s.price::text,
   'rate',s.escalation_rate::text,'baseDate',s.escalation_base_date,'basis',s.escalation_basis,
   'steps',coalesce((SELECT jsonb_agg(jsonb_build_object('from',x.from_date,'price',x.price::text) ORDER BY x.ordinal) FROM trading.ppa_price_steps x WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]')) END,
  'indexPrice',CASE WHEN s.kind<>'indexed' THEN NULL ELSE jsonb_build_object('curve',coalesce(s.index_curve_code,s.index_shaped_curve_code),'quantity',s.price_quantity_code,'multiplier',s.price_multiplier::text,'indexMultiplier',s.index_multiplier::text,'adder',s.adder::text) END,
  'attribute',CASE WHEN s.attribute_quantity_code IS NULL THEN NULL ELSE jsonb_build_object('quantity',s.attribute_quantity_code,'conversion',s.conversion::text,'eligibility',s.eligibility,'holding',s.holding,'disposition',s.disposition,'rule',s.capacity_rule) END)
 FROM trading.ppa_sides s JOIN trading.swap_payment_rules r ON(r.catalog_id,r.id)=(s.catalog_id,s.payment_rule_id) WHERE(s.catalog_id,s.id)=(catalog,side)
$$;
CREATE FUNCTION security.ppa_terms_document(catalog uuid,instrument uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('definitionRevisionId',i.definition_revision_id,'referenceCatalogId',i.reference_catalog_id,'referenceRevisionId',i.reference_revision_id,
  'delivery',jsonb_build_object('clock',i.clock_code,'intervalMinutes',i.interval_minutes,'from',i.delivery_from,'to',i.delivery_to),
  'currency',i.currency_code,'unitQuantity',i.unit_quantity_code,'discounting',i.discounting,'implementation',i.implementation,
  'normalizationAssets',coalesce((SELECT jsonb_agg(a.asset_code ORDER BY a.ordinal) FROM trading.ppa_instrument_assets a WHERE(a.catalog_id,a.instrument_id)=(catalog,instrument)),'[]'),
  'memberships',(SELECT jsonb_agg(jsonb_build_object('role',m.role,'ordinal',m.ordinal,'coefficient',m.coefficient::text,'side',security.ppa_side_document(catalog,m.side_id)) ORDER BY m.position)
   FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)))
 FROM trading.ppa_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument)
$$;
CREATE FUNCTION security.read_ppa_terms(catalog uuid,instrument uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE i trading.ppa_instruments;result jsonb;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 SELECT * INTO i FROM trading.ppa_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF NOT security.reference_access(i.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() AND NOT security.trading_visible(catalog,i.publication_order,i.effective_at) THEN RETURN NULL;END IF;
 result=jsonb_build_object('instrumentId',instrument,'terms',security.ppa_terms_document(catalog,instrument),
  'sideIds',(SELECT jsonb_agg(jsonb_build_object('role',m.role,'ordinal',m.ordinal,'sideId',m.side_id) ORDER BY m.position) FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)));
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(i.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
CREATE FUNCTION security.ppa_operation_status(catalog uuid,operation uuid) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE r trading.ppa_trade_revisions;actor uuid;result jsonb;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 SELECT actor_id INTO actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 SELECT * INTO r FROM trading.ppa_trade_revisions x WHERE x.catalog_id=catalog AND x.operation_id=operation AND x.actor_id=actor;
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() AND NOT security.trading_visible(catalog,r.publication_order,r.effective_at) THEN RETURN NULL;END IF;
 result=jsonb_build_object('operationId',operation,'tradeId',r.trade_id,'bookId',r.book_id,'controlState','current','revision',r.revision,'eventId',r.event_id,'eventSchema',r.event_schema,'kind',r.event_kind,
  'effectiveAt',r.effective_at,'boundaryAt',r.boundary_at,'definitionRevisionId',r.definition_revision_id,'referenceCatalogId',r.reference_catalog_id,'referenceRevisionId',r.reference_revision_id,
  'tradeReference',(SELECT reference_number FROM trading.trade_references WHERE(catalog_id,trade_id)=(catalog,r.trade_id)),
  'bindings',coalesce((SELECT jsonb_agg(jsonb_build_object('bindingId',b.binding_id,'instrumentId',b.instrument_id,'coverageFrom',b.coverage_from,'coverageTo',b.coverage_to,'quantity',b.quantity::text)
    ORDER BY b.coverage_from,b.binding_id) FROM trading.ppa_revision_bindings b WHERE(b.catalog_id,b.trade_id,b.revision)=(catalog,r.trade_id,r.revision)),'[]'));
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
CREATE FUNCTION security.ppa_current_head(catalog uuid,trade uuid) RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result integer;rc uuid;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT max(revision) INTO result FROM trading.ppa_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade);
 SELECT reference_catalog_id INTO rc FROM trading.ppa_trade_origins WHERE(catalog_id,trade_id)=(catalog,trade);
 IF NOT security.trading_access(catalog,'ReadTrades') OR(rc IS NOT NULL AND NOT security.reference_access(rc,'ReadMarketData')) THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
-- Scope admission must see hidden reference pins before returning any positions.
-- Runtime RLS remains useful for individual rows; it cannot establish completeness.
CREATE FUNCTION security.ppa_scope_heads(catalog uuid,scope text,ids uuid[]) RETURNS SETOF trading.ppa_trade_revisions
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE heads trading.ppa_trade_revisions[];r trading.ppa_trade_revisions;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 IF scope IS NULL OR scope NOT IN('books','trades') OR ids IS NULL OR cardinality(ids) NOT BETWEEN 1 AND 256 OR
  EXISTS(SELECT 1 FROM unnest(ids) x WHERE x IS NULL OR x='00000000-0000-0000-0000-000000000000'::uuid) OR
  (SELECT count(DISTINCT x) FROM unnest(ids) x)<>cardinality(ids)
 THEN RAISE EXCEPTION 'ppa_scope' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM security.request_contexts c WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned()
  AND c.runtime_login=session_user AND c.market_catalog_id=catalog AND c.market_context_id IS NOT NULL AND c.trading_as_of IS NOT NULL)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 IF scope='books' AND(SELECT count(*) FROM trading.books b WHERE b.catalog_id=catalog AND b.id=ANY(ids) AND security.trading_visible(catalog,b.publication_order,b.effective_at))<>cardinality(ids)
 THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 SELECT coalesce(array_agg(h::trading.ppa_trade_revisions ORDER BY h.trade_id),ARRAY[]::trading.ppa_trade_revisions[]) INTO heads FROM(
  SELECT DISTINCT ON(t.trade_id) t.* FROM trading.ppa_trade_revisions t WHERE t.catalog_id=catalog
   AND security.trading_visible(catalog,t.publication_order,t.effective_at) ORDER BY t.trade_id,t.revision DESC
 )h WHERE CASE WHEN scope='books' THEN h.book_id=ANY(ids) ELSE h.trade_id=ANY(ids) END;
 IF cardinality(heads)>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 IF scope='trades' AND cardinality(heads)<>cardinality(ids) THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 FOREACH r IN ARRAY heads LOOP
  IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 END LOOP;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501';END IF;
 RETURN QUERY SELECT h.* FROM unnest(heads) h ORDER BY h.trade_id;
END$$;
-- The connected-risk consumer needs complete-scope refusal, not PPA head rows.
CREATE FUNCTION security.ppa_scope_has_positions(catalog uuid,ids uuid[]) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE present boolean;worker boolean=pg_has_role(session_user,'openetrm_risk_worker','MEMBER');BEGIN
 IF worker AND NOT risk.worker_read() THEN RAISE EXCEPTION 'No current risk worker authority' USING ERRCODE='42501';END IF;
 SELECT EXISTS(SELECT 1 FROM security.ppa_scope_heads(catalog,'books',ids)) INTO present;
 IF worker AND NOT risk.worker_read() THEN RAISE EXCEPTION 'No current risk worker authority' USING ERRCODE='42501';END IF;
 RETURN present;
END$$;
CREATE FUNCTION security.ppa_side_output(catalog uuid,side uuid) RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s trading.ppa_sides;codes text[];BEGIN
 SELECT * INTO STRICT s FROM trading.ppa_sides WHERE(catalog_id,id)=(catalog,side);
 IF s.kind IN('certificate','capacity') THEN RETURN s.attribute_quantity_code;END IF;
 IF s.quantity_kind='profile' THEN RETURN s.profile_energy_code;END IF;
 IF s.quantity_kind='volume' THEN
  SELECT array_agg(DISTINCT i.quantity_code::text) INTO codes FROM trading.ppa_side_sources p JOIN market_data.volume_sources v
   ON(v.catalog_id,v.revision_id,v.code)=(p.catalog_id,p.definition_revision_id,p.source_code)
   JOIN market_data.interval_series i ON i.id=v.metered_series_id WHERE(p.catalog_id,p.side_id)=(catalog,side);
  IF cardinality(codes) IS DISTINCT FROM 1 OR codes[1] IS NULL THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
  RETURN codes[1];
 END IF;
 RAISE EXCEPTION 'ppa_quantity_reference' USING ERRCODE='P0001';
END$$;
CREATE FUNCTION security.ppa_validate_units(catalog uuid,instrument uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s trading.ppa_sides;c ref_data.quantity_specifications;e ref_data.quantity_specifications;target ref_data.quantity_specifications;codes text[];source_assets text[];normal text[];BEGIN
 SELECT array_agg(asset_code::text ORDER BY asset_code) INTO normal FROM trading.ppa_instrument_assets WHERE(catalog_id,instrument_id)=(catalog,instrument);
 FOR s IN SELECT p.* FROM trading.ppa_sides p JOIN trading.ppa_memberships m ON(m.catalog_id,m.side_id)=(p.catalog_id,p.id) WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) LOOP
  IF s.quantity_kind='profile' THEN
   SELECT * INTO STRICT c FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(s.reference_catalog_id,s.reference_revision_id,s.profile_capacity_code);
   SELECT * INTO STRICT e FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(s.reference_catalog_id,s.reference_revision_id,s.profile_energy_code);
   IF c.unit_code<>'megawatt' OR c.dimension_code<>'power' OR e.unit_code<>'megawatt_hour' OR e.dimension_code<>'energy' OR c.commodity_code<>e.commodity_code
   THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
  ELSIF s.quantity_kind='volume' THEN
   SELECT array_agg(v.asset_code::text ORDER BY v.asset_code),array_agg(DISTINCT i.quantity_code::text) INTO source_assets,codes FROM trading.ppa_side_sources p JOIN market_data.volume_sources v
    ON(v.catalog_id,v.revision_id,v.code)=(p.catalog_id,p.definition_revision_id,p.source_code) JOIN market_data.interval_series i ON i.id=v.metered_series_id WHERE(p.catalog_id,p.side_id)=(catalog,s.id);
   IF source_assets IS DISTINCT FROM normal THEN RAISE EXCEPTION 'ppa_normalization' USING ERRCODE='P0001';END IF;
   IF cardinality(codes) IS DISTINCT FROM 1 OR codes[1] IS NULL THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
   SELECT * INTO STRICT e FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(s.reference_catalog_id,s.reference_revision_id,codes[1]);
   IF e.unit_code<>'megawatt_hour' OR e.dimension_code<>'energy' OR EXISTS(SELECT 1 FROM market_data.generation_assets a JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,a.capacity_quantity_code)
    WHERE(a.catalog_id,a.revision_id)=(catalog,s.definition_revision_id) AND a.code=ANY(normal) AND(q.commodity_code<>e.commodity_code OR(s.kind='energy' AND a.location_code<>s.location_code)))
   THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
  END IF;
  IF s.kind IN('certificate','capacity') THEN
   SELECT * INTO STRICT target FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(s.reference_catalog_id,s.reference_revision_id,s.attribute_quantity_code);
   IF(s.kind='certificate' AND(target.dimension_code<>'certificate' OR target.commodity_code<>e.commodity_code)) OR
    (s.kind='capacity' AND(target.unit_code<>'megawatt_day' OR target.dimension_code<>'energy' OR EXISTS(SELECT 1 FROM market_data.generation_assets a JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,a.capacity_quantity_code)
      WHERE(a.catalog_id,a.revision_id)=(catalog,s.definition_revision_id) AND a.code=ANY(normal) AND q.commodity_code<>target.commodity_code)))
   THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
  END IF;
  IF s.kind IN('fixed','indexed') THEN
   IF s.quantity_kind='slots' THEN
    SELECT array_agg(DISTINCT security.ppa_side_output(catalog,m.side_id)) INTO codes FROM trading.ppa_side_slots l JOIN trading.ppa_memberships m
     ON(m.catalog_id,m.instrument_id,m.role,m.ordinal)=(catalog,instrument,l.role,l.ordinal) WHERE(l.catalog_id,l.side_id)=(catalog,s.id);
   ELSE codes=ARRAY[security.ppa_side_output(catalog,s.id)];END IF;
   IF cardinality(codes) IS DISTINCT FROM 1 OR codes[1] IS DISTINCT FROM s.price_quantity_code::text THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
   IF s.kind='indexed' AND EXISTS(SELECT 1 FROM market_data.curves curve JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(curve.reference_catalog_id,curve.reference_revision_id,curve.quote_code)
    WHERE(curve.catalog_id,curve.revision_id)=(catalog,s.definition_revision_id) AND(curve.code=s.index_curve_code OR curve.code IN(SELECT b.block_curve_code FROM market_data.shaped_curve_blocks b WHERE(b.catalog_id,b.revision_id,b.shaped_curve_code)=(catalog,s.definition_revision_id,s.index_shaped_curve_code)))
     AND(q.currency_code<>s.currency_code OR q.quantity_code<>s.price_quantity_code OR q.multiplier<>s.price_multiplier))
   THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
  END IF;
 END LOOP;
END$$;
CREATE FUNCTION security.retain_ppa_terms(catalog uuid,spec jsonb,publication integer,from_at timestamptz) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d uuid;rc uuid;rr uuid;delivery jsonb;member jsonb;side jsonb;q jsonb;h jsonb;f jsonb;ix jsonb;a jsonb;item jsonb;
 candidate uuid;instrument uuid;side_id uuid;rule uuid;position integer;common jsonb;key text;month_count integer;payment_month date;
 curve_code text;shaped_code text;term_from date;term_to date;minutes integer;normal text[];derived text[];
 energy_count integer;strike_count integer;index_count integer;capacity_count integer;capacity_payment_count integer;
 certificate_count integer;certificate_payment_count integer;charge_count integer;indexes jsonb[];
BEGIN
 spec=security.ppa_normalize(spec);
 PERFORM security.ppa_keys(spec,ARRAY['definitionRevisionId','referenceCatalogId','referenceRevisionId','delivery','currency','unitQuantity','discounting','implementation','normalizationAssets','memberships']);
 d=(spec->>'definitionRevisionId')::uuid;rc=(spec->>'referenceCatalogId')::uuid;rr=(spec->>'referenceRevisionId')::uuid;delivery=spec->'delivery';
 PERFORM security.ppa_keys(delivery,ARRAY['clock','intervalMinutes','from','to']);
 term_from=(delivery->>'from')::date;term_to=(delivery->>'to')::date;minutes=(delivery->>'intervalMinutes')::integer;
 month_count=(extract(year FROM term_to)::integer-extract(year FROM term_from)::integer)*12+extract(month FROM term_to)::integer-extract(month FROM term_from)::integer;
 IF spec->>'implementation' IS DISTINCT FROM 'ppa_instrument_v1' OR jsonb_typeof(spec->'discounting')<>'boolean' OR minutes NOT IN(15,60) OR minutes IS NULL OR
  term_from IS NULL OR term_to IS NULL OR term_from<DATE '2000-01-01' OR term_to>DATE '2100-01-01' OR extract(day FROM term_from)<>1 OR extract(day FROM term_to)<>1 OR month_count<1 OR month_count>(CASE WHEN minutes=60 THEN 360 ELSE 120 END) OR
  jsonb_typeof(spec->'normalizationAssets')<>'array' OR jsonb_array_length(spec->'normalizationAssets')>100 OR jsonb_typeof(spec->'memberships')<>'array' OR jsonb_array_length(spec->'memberships') NOT BETWEEN 2 AND 8
 THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
 IF NOT security.reference_access(rc,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id)=(catalog,d) AND state='sealed') THEN RAISE EXCEPTION 'ppa_definition_revision' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id,reference_catalog_id,reference_revision_id)=(catalog,d,rc,rr)) OR
  NOT EXISTS(SELECT 1 FROM ref_data.revisions WHERE(catalog_id,id)=(rc,rr) AND state='sealed') THEN RAISE EXCEPTION 'ppa_reference_revision' USING ERRCODE='P0001';END IF;
 -- Reusable definitions exist from their exact bundle's effective boundary,
 -- independently of which trade happens to retain them first.
 SELECT p.effective_from::timestamp AT TIME ZONE 'UTC' INTO from_at FROM market_data.revision_periods p
  WHERE(p.catalog_id,p.revision_id)=(catalog,d) AND p.effective_from<=(from_at AT TIME ZONE 'UTC')::date AND(from_at AT TIME ZONE 'UTC')::date<p.effective_to;
 IF from_at IS NULL THEN RAISE EXCEPTION 'ppa_definition_revision' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(rc,rr,spec->>'unitQuantity') AND unit_code='megawatt' AND dimension_code='power') THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
 PERFORM security.ppa_day_start(rc,rr,delivery->>'clock',term_from);PERFORM security.ppa_day_start(rc,rr,delivery->>'clock',term_to);
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(spec->'normalizationAssets') v WHERE jsonb_typeof(v)<>'string') OR
  (SELECT count(DISTINCT v) FROM jsonb_array_elements(spec->'normalizationAssets') v)<>jsonb_array_length(spec->'normalizationAssets') THEN RAISE EXCEPTION 'ppa_normalization' USING ERRCODE='P0001';END IF;
 SELECT coalesce(array_agg(v ORDER BY v),ARRAY[]::text[]) INTO normal FROM jsonb_array_elements_text(spec->'normalizationAssets') v;
 SELECT coalesce(array_agg(DISTINCT asset ORDER BY asset),ARRAY[]::text[]) INTO derived FROM(
  SELECT s.asset_code::text asset FROM jsonb_array_elements(spec->'memberships') m CROSS JOIN LATERAL jsonb_array_elements_text(m#>'{side,quantity,sources}') source(code)
   JOIN market_data.volume_sources s ON(s.catalog_id,s.revision_id,s.code)=(catalog,d,source.code)
  UNION SELECT asset.code FROM jsonb_array_elements(spec->'memberships') m CROSS JOIN LATERAL jsonb_array_elements_text(m#>'{side,quantity,assets}') asset(code)
 ) assets;
 IF normal<>derived THEN RAISE EXCEPTION 'ppa_normalization' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM unnest(normal) asset(code) LEFT JOIN market_data.generation_assets ga ON(ga.catalog_id,ga.revision_id,ga.code)=(catalog,d,asset.code) WHERE ga.code IS NULL OR ga.clock_code<>delivery->>'clock') THEN RAISE EXCEPTION 'ppa_reference_revision' USING ERRCODE='P0001';END IF;
 -- Exact reconstructed equality, not the hash, establishes reuse. The transaction lock serializes retention.
 FOR candidate IN SELECT instrument_id FROM trading.ppa_instruments WHERE catalog_id=catalog AND economic_hash=md5(spec::text) LOOP
  IF security.ppa_terms_document(catalog,candidate)=spec THEN RETURN candidate;END IF;
 END LOOP;
 IF (SELECT count(DISTINCT(m->>'role',(m->>'ordinal')::integer)) FROM jsonb_array_elements(spec->'memberships') m)<>jsonb_array_length(spec->'memberships') THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
 common=spec-ARRAY['discounting','implementation','normalizationAssets','memberships'];
 instrument=gen_random_uuid();
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,from_at);
 INSERT INTO trading.ppa_instruments VALUES(catalog,instrument,d,rc,rr,delivery->>'clock',minutes,term_from,term_to,spec->>'currency',spec->>'unitQuantity',(spec->>'discounting')::boolean,'ppa_instrument_v1',md5(spec::text),publication,from_at);
 INSERT INTO trading.ppa_instrument_assets SELECT catalog,instrument,d,n::integer-1,v,publication,from_at FROM jsonb_array_elements_text(spec->'normalizationAssets') WITH ORDINALITY x(v,n);
 position=0;
 FOR member IN SELECT v FROM jsonb_array_elements(spec->'memberships') v LOOP
  PERFORM security.ppa_keys(member,ARRAY['role','ordinal','coefficient','side']);side=member->'side';
  PERFORM security.ppa_keys(side,ARRAY['kind','quantity','paymentRule','location','fixedPrice','indexPrice','attribute']);
  IF security.ppa_number(member->>'coefficient') NOT IN(-1,1) OR(member->>'ordinal')::integer NOT BETWEEN 0 AND 7 THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
  IF NOT((member->>'role'='energy_delivery' AND side->>'kind'='energy') OR(member->>'role'='certificate_delivery' AND side->>'kind'='certificate') OR
    (member->>'role'='capacity_delivery' AND side->>'kind'='capacity') OR(member->>'role' IN('strike_payment','fixed_charge') AND side->>'kind'='fixed') OR
    (member->>'role'='indexed_payment' AND side->>'kind'='indexed') OR(member->>'role' IN('certificate_payment','capacity_payment') AND side->>'kind' IN('fixed','indexed')))
  THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
  q=side->'quantity';h=q->'hours';f=side->'fixedPrice';ix=side->'indexPrice';a=side->'attribute';
  PERFORM security.ppa_keys(q,ARRAY['kind','sources','stage','capacity','energy','hours','slots','assets']);
  IF jsonb_typeof(q->'sources')<>'array' OR jsonb_typeof(q->'slots')<>'array' OR jsonb_typeof(q->'assets')<>'array' OR jsonb_array_length(q->'sources')>100 OR jsonb_array_length(q->'assets')>100 OR jsonb_array_length(q->'slots')>8 OR
   (q->>'kind'='volume')<>(jsonb_array_length(q->'sources')>0) OR(q->>'kind'='accredited')<>(jsonb_array_length(q->'assets')>0) OR(q->>'kind'='slots')<>(jsonb_array_length(q->'slots')>0) OR
   (q->>'kind'='profile')<>(h<>'null'::jsonb) OR(q->>'kind'='volume')<>(q->>'stage' IS NOT NULL) OR
   (q->>'kind'='profile')<>(q->>'capacity' IS NOT NULL) OR(q->>'kind'='profile')<>(q->>'energy' IS NOT NULL) OR
   q->>'kind' NOT IN('volume','profile','slots','accredited') OR
   (q->>'kind'='slots' AND side->>'kind' NOT IN('fixed','indexed')) OR(side->>'kind'='certificate' AND q->>'kind'<>'volume') OR(side->>'kind'='capacity' AND q->>'kind'<>'accredited')
  THEN RAISE EXCEPTION 'ppa_quantity_source' USING ERRCODE='P0001';END IF;
  IF q->>'kind'='volume' THEN
   IF q->>'stage' NOT IN('generated','delivered') OR (SELECT count(DISTINCT v) FROM jsonb_array_elements(q->'sources') v)<>jsonb_array_length(q->'sources') OR
    EXISTS(SELECT 1 FROM jsonb_array_elements_text(q->'sources') source(code) LEFT JOIN market_data.volume_sources s ON(s.catalog_id,s.revision_id,s.code)=(catalog,d,source.code)
     WHERE s.code IS NULL OR s.clock_code<>delivery->>'clock' OR s.interval_minutes<>minutes) THEN RAISE EXCEPTION 'ppa_quantity_source' USING ERRCODE='P0001';END IF;
  END IF;
  IF q->>'kind'='profile' THEN
   PERFORM security.ppa_keys(h,ARRAY['membership','touProduct','touVersion','calendar']);
   IF h->>'membership' NOT IN('all','peak','off') OR h->>'calendar' IS NULL OR(h->>'membership'='all')<>(h->>'touProduct' IS NULL) OR(h->>'touProduct' IS NULL)<>(h->>'touVersion' IS NULL) OR
    (h->>'touProduct' IS NOT NULL AND NOT EXISTS(SELECT 1 FROM market_data.tou_product_versions t WHERE(t.catalog_id,t.product_id,t.version)=(catalog,(h->>'touProduct')::uuid,(h->>'touVersion')::integer)
      AND t.state='published' AND(t.clock_catalog_id,t.clock_revision_id,t.clock_code)=(rc,rr,delivery->>'clock')))
   THEN RAISE EXCEPTION 'ppa_delivery_hours' USING ERRCODE='P0001';END IF;
  END IF;
  IF member->>'role' IN('certificate_payment','capacity_payment','fixed_charge') AND q->>'kind'<>'slots'
  THEN RAISE EXCEPTION 'ppa_quantity_reference' USING ERRCODE='P0001';END IF;
  IF (side->>'kind'='energy')<>(side->>'location' IS NOT NULL) OR(side->>'kind'='fixed')<>(f<>'null'::jsonb) OR(side->>'kind'='indexed')<>(ix<>'null'::jsonb) OR(side->>'kind' IN('certificate','capacity'))<>(a<>'null'::jsonb)
  THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
  IF side->>'kind'='fixed' THEN
   PERFORM security.ppa_keys(f,ARRAY['kind','quantity','multiplier','price','rate','baseDate','basis','steps']);
   IF f->>'kind' NOT IN('flat','escalated','stepped') OR security.ppa_number(f->>'multiplier')<=0 OR jsonb_typeof(f->'steps')<>'array' OR jsonb_array_length(f->'steps')>360 OR
    (f->>'kind'='stepped')<>(jsonb_array_length(f->'steps')>0) OR(f->>'kind'<>'stepped')<>(f->>'price' IS NOT NULL) OR
    (f->>'kind'='escalated')<>(f->>'rate' IS NOT NULL) OR(f->>'kind'='escalated')<>(f->>'baseDate' IS NOT NULL) OR(f->>'kind'='escalated')<>(f->>'basis' IS NOT NULL)
   THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
   IF f->>'kind'='escalated' AND(f->>'basis'<>'anniversary_step_v1' OR security.ppa_number(f->>'rate')<=-1 OR to_char((f->>'baseDate')::date,'MM-DD')='02-29') THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
   IF f->>'kind'='stepped' THEN
    IF (SELECT min((v->>'from')::date) FROM jsonb_array_elements(f->'steps') v)>term_from OR
     (SELECT count(DISTINCT v->>'from') FROM jsonb_array_elements(f->'steps') v)<>jsonb_array_length(f->'steps') THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
    FOR item IN SELECT v FROM jsonb_array_elements(f->'steps') v LOOP PERFORM security.ppa_keys(item,ARRAY['from','price']);PERFORM security.ppa_number(item->>'price');END LOOP;
   END IF;
  END IF;
  curve_code=NULL;shaped_code=NULL;
  IF side->>'kind'='indexed' THEN
   PERFORM security.ppa_keys(ix,ARRAY['curve','quantity','multiplier','indexMultiplier','adder']);
   IF ix->>'quantity' IS NULL OR ix->>'adder' IS NULL OR security.ppa_number(ix->>'multiplier')<=0 OR security.ppa_number(ix->>'indexMultiplier')<=0 THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
   SELECT code INTO curve_code FROM market_data.curves WHERE(catalog_id,revision_id,code)=(catalog,d,ix->>'curve');
   SELECT code INTO shaped_code FROM market_data.shaped_curves WHERE(catalog_id,revision_id,code)=(catalog,d,ix->>'curve');
   IF(curve_code IS NULL)=(shaped_code IS NULL) THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
  END IF;
  IF side->>'kind' IN('certificate','capacity') THEN
   PERFORM security.ppa_keys(a,ARRAY['quantity','conversion','eligibility','holding','disposition','rule']);
   IF(side->>'kind'='certificate' AND(a->>'eligibility' IS DISTINCT FROM 'asset_generation_at_stage_v1' OR a->>'holding' IS DISTINCT FROM 'held_at_current_vintage_mark_v1' OR a->>'disposition' IS DISTINCT FROM 'none_recorded_v1' OR a->>'rule' IS NOT NULL OR security.ppa_number(a->>'conversion')<=0)) OR
    (side->>'kind'='capacity' AND(a->>'rule' IS DISTINCT FROM 'mw_day_per_operating_day_v1' OR a->>'conversion' IS NOT NULL OR a->>'eligibility' IS NOT NULL OR a->>'holding' IS NOT NULL OR a->>'disposition' IS NOT NULL)) THEN RAISE EXCEPTION 'ppa_term_unsupported' USING ERRCODE='P0001';END IF;
  END IF;
  -- The existing managed-compatible payment rule validator also proves calendar references.
  payment_month=term_from;
  WHILE payment_month<term_to LOOP
   PERFORM security.swap_payment_date(rc,rr,payment_month,(payment_month+INTERVAL '1 month')::date,side->'paymentRule');
   payment_month=(payment_month+INTERVAL '1 month')::date;
  END LOOP;
  key=md5((common||jsonb_build_object('side',side))::text);side_id=NULL;
  FOR candidate IN SELECT id FROM trading.ppa_sides WHERE catalog_id=catalog AND economic_hash=key AND(definition_revision_id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes,delivery_from,delivery_to,currency_code,unit_quantity_code)=(d,rc,rr,delivery->>'clock',minutes,term_from,term_to,spec->>'currency',spec->>'unitQuantity') LOOP
   IF NOT EXISTS(SELECT 1 FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id,m.side_id)=(catalog,instrument,candidate)) AND security.ppa_side_document(catalog,candidate)=side THEN side_id=candidate;EXIT;END IF;
  END LOOP;
  IF side_id IS NULL THEN
   rule=security.retain_swap_payment_rule(catalog,rc,rr,side->'paymentRule',publication,from_at);side_id=gen_random_uuid();
   INSERT INTO trading.ppa_sides VALUES(catalog,side_id,d,rc,rr,side->>'kind',delivery->>'clock',minutes,term_from,term_to,spec->>'currency',spec->>'unitQuantity',rule,q->>'kind',q->>'stage',q->>'capacity',q->>'energy',
    h->>'membership',(h->>'touProduct')::uuid,(h->>'touVersion')::integer,h->>'calendar',side->>'location',f->>'kind',coalesce(f->>'quantity',ix->>'quantity'),
    coalesce((f->>'multiplier')::numeric,(ix->>'multiplier')::numeric),(f->>'price')::numeric,(f->>'rate')::numeric,(f->>'baseDate')::date,f->>'basis',curve_code,shaped_code,(ix->>'indexMultiplier')::numeric,(ix->>'adder')::numeric,
    a->>'quantity',(a->>'conversion')::numeric,a->>'eligibility',a->>'holding',a->>'disposition',a->>'rule',key,publication,from_at);
   INSERT INTO trading.ppa_side_sources SELECT catalog,side_id,d,n::integer-1,v,publication,from_at FROM jsonb_array_elements_text(q->'sources') WITH ORDINALITY x(v,n);
   INSERT INTO trading.ppa_side_assets SELECT catalog,side_id,d,n::integer-1,v,publication,from_at FROM jsonb_array_elements_text(q->'assets') WITH ORDINALITY x(v,n);
   FOR item IN SELECT v FROM jsonb_array_elements(q->'slots') v LOOP PERFORM security.ppa_keys(item,ARRAY['role','ordinal']);END LOOP;
   INSERT INTO trading.ppa_side_slots SELECT catalog,side_id,n::integer-1,v->>'role',(v->>'ordinal')::integer,publication,from_at FROM jsonb_array_elements(q->'slots') WITH ORDINALITY x(v,n);
   IF f->>'kind'='stepped' THEN INSERT INTO trading.ppa_price_steps SELECT catalog,side_id,n::integer-1,(v->>'from')::date,(v->>'price')::numeric,publication,from_at FROM jsonb_array_elements(f->'steps') WITH ORDINALITY x(v,n);END IF;
  END IF;
  INSERT INTO trading.ppa_memberships VALUES(catalog,instrument,position,member->>'role',(member->>'ordinal')::integer,side_id,(member->>'coefficient')::numeric,publication,from_at);position=position+1;
 END LOOP;
 -- Bind abstract reusable quantity slots to exact memberships in this instrument.
 IF EXISTS(SELECT 1 FROM trading.ppa_memberships m JOIN trading.ppa_side_slots l ON(l.catalog_id,l.side_id)=(m.catalog_id,m.side_id)
  LEFT JOIN trading.ppa_memberships target ON(target.catalog_id,target.instrument_id,target.role,target.ordinal)=(catalog,instrument,l.role,l.ordinal)
  WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND(target.side_id IS NULL OR
   (m.role IN('strike_payment','indexed_payment','fixed_charge') AND l.role<>'energy_delivery') OR(m.role='certificate_payment' AND l.role<>'certificate_delivery') OR(m.role='capacity_payment' AND l.role<>'capacity_delivery')))
 THEN RAISE EXCEPTION 'ppa_quantity_reference' USING ERRCODE='P0001';END IF;
 PERFORM security.ppa_validate_units(catalog,instrument);
 SELECT count(*) FILTER(WHERE role='energy_delivery'),count(*) FILTER(WHERE role='strike_payment'),count(*) FILTER(WHERE role='indexed_payment'),
  count(*) FILTER(WHERE role='capacity_delivery'),count(*) FILTER(WHERE role='capacity_payment'),count(*) FILTER(WHERE role='certificate_delivery'),
  count(*) FILTER(WHERE role='certificate_payment'),count(*) FILTER(WHERE role='fixed_charge')
 INTO energy_count,strike_count,index_count,capacity_count,capacity_payment_count,certificate_count,certificate_payment_count,charge_count
 FROM trading.ppa_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF certificate_count>1 OR certificate_payment_count>certificate_count OR charge_count>1 THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
 IF capacity_count=1 AND energy_count=0 AND strike_count=0 AND index_count=0 AND capacity_payment_count=1 AND certificate_count=0 AND charge_count=0 THEN RETURN instrument;END IF;
 IF capacity_count<>0 OR capacity_payment_count<>0 THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
 IF energy_count=1 AND((strike_count=1 AND index_count=0) OR(strike_count=0 AND index_count=1)) THEN
  IF EXISTS(SELECT 1 FROM trading.ppa_memberships m JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id)
   WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND m.role IN('strike_payment','indexed_payment') AND
    (s.quantity_kind<>'slots' OR(SELECT count(*) FROM trading.ppa_side_slots l WHERE(l.catalog_id,l.side_id)=(catalog,s.id))<>1))
  THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
  RETURN instrument;
 END IF;
 IF energy_count=0 AND strike_count=1 AND index_count=1 THEN RETURN instrument;END IF;
 IF energy_count=0 AND strike_count=0 AND index_count=2 AND certificate_count=0 AND charge_count=0 THEN
  SELECT array_agg(security.ppa_side_document(catalog,m.side_id) ORDER BY m.position) INTO indexes
   FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND m.role='indexed_payment';
  IF indexes[1]#>'{indexPrice,curve}'=indexes[2]#>'{indexPrice,curve}' OR indexes[1]->'quantity'<>indexes[2]->'quantity'
  THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
  RETURN instrument;
 END IF;
 RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';
END$$;
CREATE FUNCTION security.save_ppa_booking(catalog uuid,operation uuid,request jsonb) RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts;prior trading.ppa_trade_revisions;head trading.ppa_trade_revisions;origin trading.ppa_trade_origins;
 trade uuid;book uuid;expected integer;action text;permission text;valuation timestamptz;effective timestamptz;boundary timestamptz;first_at timestamptz;last_at timestamptz;
 amount numeric;terms jsonb;booking jsonb;d uuid;rc uuid;rr uuid;instrument uuid;binding uuid;publication integer;rev integer;executed timestamptz;
 retained_end timestamptz;retained_start timestamptz;old record;whole_month boolean;term_from timestamptz;term_to timestamptz;
BEGIN
 action=request->>'action';permission=CASE WHEN action='booked' THEN 'BookTrade' ELSE 'AmendTrade' END;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'No trade write access' USING ERRCODE='42501';END IF;
 SELECT * INTO c FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'historical_read_only' USING ERRCODE='P0001';END IF;
 PERFORM security.ppa_keys(request,ARRAY['snapshotId','action','tradeId','expectedRevision','bookId','valuationAt','effectiveAt','boundaryAt','coverageFrom','coverageTo','quantity','reason','memo','booking','terms']);
 IF pg_column_size(request)>262144 OR operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR
  (request->>'snapshotId')::uuid IS NULL OR action IS NULL OR action NOT IN('booked','amended','terminated') OR request->>'reason' IS NULL OR length(btrim(request->>'reason')) NOT BETWEEN 1 AND 500 OR request->>'memo' IS NULL OR length(request->>'memo')>2000
 THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
 trade=(request->>'tradeId')::uuid;book=(request->>'bookId')::uuid;expected=(request->>'expectedRevision')::integer;
 valuation=(request->>'valuationAt')::timestamptz;effective=(request->>'effectiveAt')::timestamptz;boundary=(request->>'boundaryAt')::timestamptz;
 first_at=(request->>'coverageFrom')::timestamptz;last_at=(request->>'coverageTo')::timestamptz;terms=request->'terms';booking=request->'booking';
 IF book IS NULL OR valuation IS NULL OR NOT isfinite(valuation) OR effective IS DISTINCT FROM valuation OR
  (action='booked')<>(trade IS NULL) OR(action='booked')<>(expected IS NULL) OR(action='booked')<>(boundary IS NULL) OR
  (action='terminated')<>(terms='null'::jsonb) OR(action='terminated')<>(request->>'quantity' IS NULL) OR
  (action='terminated')<>(first_at IS NULL) OR(action='terminated')<>(last_at IS NULL)
 THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
 IF action<>'terminated' THEN
  amount=security.ppa_number(request->>'quantity');d=(terms->>'definitionRevisionId')::uuid;rc=(terms->>'referenceCatalogId')::uuid;rr=(terms->>'referenceRevisionId')::uuid;
  IF amount<=0 OR first_at IS NULL OR last_at IS NULL OR NOT isfinite(first_at) OR NOT isfinite(last_at) OR first_at>=last_at THEN RAISE EXCEPTION 'ppa_segment_coverage' USING ERRCODE='P0001';END IF;
 END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501';END IF;
 -- Current admission precedes idempotent replay; stale grants never inherit an old success.
 IF rc IS NOT NULL AND NOT security.reference_access(rc,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 IF trade IS NOT NULL THEN
  SELECT * INTO origin FROM trading.ppa_trade_origins WHERE(catalog_id,trade_id)=(catalog,trade);
  IF NOT FOUND THEN RAISE EXCEPTION 'ppa_trade' USING ERRCODE='P0001';END IF;
  IF NOT security.reference_access(origin.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 END IF;
 SELECT * INTO prior FROM trading.ppa_trade_revisions WHERE catalog_id=catalog AND operation_id=operation;
 IF FOUND THEN
  IF prior.actor_id<>c.actor_id OR prior.request<>request THEN RAISE EXCEPTION 'ppa_conflict' USING ERRCODE='P0001';END IF;
  RETURN security.ppa_operation_status(catalog,operation);
 END IF;
 IF EXISTS(SELECT 1 FROM trading.trades WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.trade_revisions WHERE catalog_id=catalog AND operation_id=operation) OR EXISTS(SELECT 1 FROM trading.swap_trade_revisions WHERE catalog_id=catalog AND operation_id=operation)
 THEN RAISE EXCEPTION 'ppa_conflict' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM trading.books b JOIN trading.portfolios p ON(p.catalog_id,p.id)=(b.catalog_id,b.portfolio_id) WHERE(b.catalog_id,b.id)=(catalog,book) AND b.effective_at<=valuation AND p.effective_at<=valuation)
 THEN RAISE EXCEPTION 'ppa_book' USING ERRCODE='P0001';END IF;
 IF trade IS NOT NULL THEN
  SELECT * INTO head FROM trading.ppa_trade_revisions WHERE(catalog_id,trade_id)=(catalog,trade) ORDER BY revision DESC LIMIT 1;
  IF head.revision IS DISTINCT FROM expected THEN RAISE EXCEPTION 'ppa_revision_conflict' USING ERRCODE='P0001';END IF;
  IF head.revision>=256 OR head.event_kind='terminated' OR head.book_id<>book OR effective<head.effective_at OR boundary IS NULL OR NOT isfinite(boundary) OR boundary<valuation
  THEN RAISE EXCEPTION 'ppa_amendment' USING ERRCODE='P0001';END IF;
  -- Definition mismatch takes precedence over reference mismatch, before any candidate term lookup.
  IF action='amended' AND d IS DISTINCT FROM origin.definition_revision_id THEN RAISE EXCEPTION 'ppa_definition_revision' USING ERRCODE='P0001';END IF;
  IF action='amended' AND(rc,rr) IS DISTINCT FROM(origin.reference_catalog_id,origin.reference_revision_id) THEN RAISE EXCEPTION 'ppa_reference_revision' USING ERRCODE='P0001';END IF;
  d=origin.definition_revision_id;rc=origin.reference_catalog_id;rr=origin.reference_revision_id;executed=head.execution_at;
  SELECT min(coverage_from),max(coverage_to) INTO retained_start,retained_end FROM trading.ppa_revision_bindings WHERE(catalog_id,trade_id,revision)=(catalog,trade,head.revision);
  IF retained_start IS NULL OR boundary<retained_start OR boundary>=retained_end OR(action='amended' AND(first_at<>boundary OR last_at<>retained_end)) THEN RAISE EXCEPTION 'ppa_segment_coverage' USING ERRCODE='P0001';END IF;
  IF booking<>'null'::jsonb AND booking<>jsonb_build_object('legalEntityId',head.legal_entity_id,'legalEntityRevision',head.legal_entity_revision,'counterpartyId',head.counterparty_id,'counterpartyRevision',head.counterparty_revision)
  THEN RAISE EXCEPTION 'ppa_amendment' USING ERRCODE='P0001';END IF;
  booking=jsonb_build_object('legalEntityId',head.legal_entity_id,'legalEntityRevision',head.legal_entity_revision,'counterpartyId',head.counterparty_id,'counterpartyRevision',head.counterparty_revision,
   'tradeDate',(SELECT trade_date FROM trading.booking_facts WHERE(catalog_id,trade_id)=(catalog,trade)));
  FOR old IN SELECT b.*,i.clock_code,i.interval_minutes FROM trading.ppa_revision_bindings b JOIN trading.ppa_instruments i ON(i.catalog_id,i.instrument_id)=(b.catalog_id,b.instrument_id)
    WHERE(b.catalog_id,b.trade_id,b.revision)=(catalog,trade,head.revision) AND b.coverage_from<=boundary AND boundary<b.coverage_to LOOP
   SELECT EXISTS(SELECT 1 FROM trading.ppa_memberships WHERE(catalog_id,instrument_id)=(catalog,old.instrument_id) AND role='capacity_delivery') INTO whole_month;
   IF NOT security.ppa_boundary(rc,rr,old.clock_code,old.interval_minutes,boundary,whole_month) THEN RAISE EXCEPTION 'ppa_segment_boundary' USING ERRCODE='P0001';END IF;
  END LOOP;
 ELSE
  IF first_at<valuation OR jsonb_typeof(booking)<>'object' OR(SELECT count(*) FROM trading.trades WHERE catalog_id=catalog)>=256 THEN RAISE EXCEPTION 'ppa_booking' USING ERRCODE='P0001';END IF;
  PERFORM security.ppa_keys(booking,ARRAY['legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision']);
  executed=valuation;booking=booking||jsonb_build_object('tradeDate',(clock_timestamp() AT TIME ZONE 'UTC')::date);
 END IF;
 PERFORM security.require_trade_parties(catalog,booking,executed);
 SELECT coalesce(max(publication_order),0)+1 INTO publication FROM trading.publications WHERE catalog_id=catalog;
 INSERT INTO trading.publications VALUES(catalog,publication,pg_current_xact_id());
 IF action<>'terminated' THEN
  instrument=security.retain_ppa_terms(catalog,terms,publication,executed);
  SELECT * INTO old FROM trading.ppa_instruments WHERE(catalog_id,instrument_id)=(catalog,instrument);
  term_from=security.ppa_day_start(rc,rr,old.clock_code,old.delivery_from);term_to=security.ppa_day_start(rc,rr,old.clock_code,old.delivery_to);
  SELECT EXISTS(SELECT 1 FROM trading.ppa_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument) AND role='capacity_delivery') INTO whole_month;
  IF first_at<term_from OR last_at>term_to OR NOT security.ppa_boundary(rc,rr,old.clock_code,old.interval_minutes,first_at,whole_month) OR NOT security.ppa_boundary(rc,rr,old.clock_code,old.interval_minutes,last_at,whole_month)
  THEN RAISE EXCEPTION 'ppa_segment_boundary' USING ERRCODE='P0001';END IF;
 END IF;
 IF action='booked' THEN
  trade=gen_random_uuid();PERFORM security.stamp_trade_booking(catalog);
  INSERT INTO trading.trades VALUES(catalog,trade,book,operation,c.actor_id,request,publication,executed);
  INSERT INTO trading.ppa_trade_origins VALUES(catalog,trade,d,rc,rr,publication,executed);
 END IF;
 rev=coalesce(head.revision,0)+1;
 INSERT INTO trading.ppa_trade_revisions VALUES(catalog,trade,rev,head.revision,gen_random_uuid(),'ppa_booking_v1',action,book,d,rc,rr,boundary,executed,effective,
  operation,c.actor_id,btrim(request->>'reason'),request->>'memo',(booking->>'legalEntityId')::uuid,(booking->>'legalEntityRevision')::integer,(booking->>'counterpartyId')::uuid,(booking->>'counterpartyRevision')::integer,request,publication);
 IF head.revision IS NOT NULL THEN
  INSERT INTO trading.ppa_revision_bindings SELECT catalog,trade,rev,b.binding_id,b.instrument_id,b.definition_revision_id,b.reference_catalog_id,b.reference_revision_id,
   b.coverage_from,least(b.coverage_to,boundary),b.quantity,publication,effective FROM trading.ppa_revision_bindings b
   WHERE(b.catalog_id,b.trade_id,b.revision)=(catalog,trade,head.revision) AND b.coverage_from<boundary;
 END IF;
 IF action<>'terminated' THEN
  binding=gen_random_uuid();INSERT INTO trading.ppa_revision_bindings VALUES(catalog,trade,rev,binding,instrument,d,rc,rr,first_at,last_at,amount,publication,effective);
 END IF;
 IF(SELECT count(*) FROM trading.ppa_revision_bindings WHERE(catalog_id,trade_id,revision)=(catalog,trade,rev))>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.trading_access(catalog,permission) OR NOT security.reference_access(rc,'ReadMarketData') THEN RAISE EXCEPTION 'Trade write access expired' USING ERRCODE='42501';END IF;
 RETURN security.ppa_operation_status(catalog,operation);
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range OR datetime_field_overflow OR invalid_parameter_value THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';
END$$;
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['ppa_sides','ppa_instruments','ppa_trade_origins','ppa_trade_revisions','ppa_revision_bindings'] LOOP
  EXECUTE format('CREATE POLICY reference_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(security.reference_access(reference_catalog_id,''ReadMarketData''))',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['ppa_side_sources','ppa_side_assets','ppa_side_slots','ppa_price_steps'] LOOP
  EXECUTE format('CREATE POLICY parent_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.ppa_sides s WHERE(s.catalog_id,s.id)=(%I.catalog_id,%I.side_id)))',t,t,t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['ppa_instrument_assets','ppa_memberships'] LOOP
  EXECUTE format('CREATE POLICY parent_read ON trading.%I AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(EXISTS(SELECT 1 FROM trading.ppa_instruments i WHERE(i.catalog_id,i.instrument_id)=(%I.catalog_id,%I.instrument_id)))',t,t,t);
 END LOOP;
END$$;
-- Current PPA readers retain an explicit certified trading cutoff without changing
-- the connection into a historical market session. Interval inputs have their own
-- audited cutoff readers and deliberately require current-session admission.
CREATE FUNCTION security.ppa_visible_at(catalog uuid,ordinal integer,effective timestamptz,context uuid,as_of timestamptz)
RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT security.trading_access(catalog,'ReadTrades') AND EXISTS(
  SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON k.catalog_id=catalog AND k.publication_order=ordinal
  WHERE t.catalog_id=catalog AND t.id=context AND t.effective_on=(as_of AT TIME ZONE 'UTC')::date
    AND ordinal<=x.known_through AND k.known_from<=t.known_at AND effective<=as_of);
$$;
CREATE FUNCTION security.ppa_read_scope(catalog uuid,scope text,ids uuid[],context uuid,as_of timestamptz,include_history boolean)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE heads trading.ppa_trade_revisions[];r trading.ppa_trade_revisions;result jsonb;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'ppa_historical' USING ERRCODE='P0001';END IF;
 IF scope IS NULL OR scope NOT IN('books','trades') OR ids IS NULL OR cardinality(ids) NOT BETWEEN 1 AND 256 OR include_history IS NULL OR
  EXISTS(SELECT 1 FROM unnest(ids) x WHERE x IS NULL OR x='00000000-0000-0000-0000-000000000000'::uuid) OR
  (SELECT count(DISTINCT x) FROM unnest(ids) x)<>cardinality(ids) OR (include_history AND(scope<>'trades' OR cardinality(ids)<>1))
 THEN RAISE EXCEPTION 'ppa_scope' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE t.catalog_id=catalog AND t.id=context AND t.effective_on=(as_of AT TIME ZONE 'UTC')::date)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 IF scope='books' AND(SELECT count(*) FROM trading.books b WHERE b.catalog_id=catalog AND b.id=ANY(ids) AND security.ppa_visible_at(catalog,b.publication_order,b.effective_at,context,as_of))<>cardinality(ids)
 THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 SELECT coalesce(array_agg(h::trading.ppa_trade_revisions ORDER BY h.trade_id),ARRAY[]::trading.ppa_trade_revisions[]) INTO heads FROM(
  SELECT DISTINCT ON(t.trade_id) t.* FROM trading.ppa_trade_revisions t WHERE t.catalog_id=catalog
   AND security.ppa_visible_at(catalog,t.publication_order,t.effective_at,context,as_of) ORDER BY t.trade_id,t.revision DESC
 )h WHERE CASE WHEN scope='books' THEN h.book_id=ANY(ids) ELSE h.trade_id=ANY(ids) END;
 IF cardinality(heads)>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 IF scope='trades' AND cardinality(heads)<>cardinality(ids) THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 FOREACH r IN ARRAY heads LOOP
  IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 END LOOP;
 IF(SELECT count(*) FROM trading.ppa_revision_bindings b JOIN unnest(heads) h ON(h.catalog_id,h.trade_id,h.revision)=(b.catalog_id,b.trade_id,b.revision))>256 THEN RAISE EXCEPTION 'ppa_limit' USING ERRCODE='P0001';END IF;
 SELECT jsonb_build_object('heads',to_jsonb(heads),'bindings',coalesce((SELECT jsonb_agg(to_jsonb(b) ORDER BY b.trade_id,b.coverage_from,b.binding_id)
  FROM trading.ppa_revision_bindings b JOIN unnest(heads) h ON(h.catalog_id,h.trade_id,h.revision)=(b.catalog_id,b.trade_id,b.revision)),'[]'::jsonb),
  'history',CASE WHEN include_history THEN coalesce((SELECT jsonb_agg(to_jsonb(h) ORDER BY h.revision) FROM trading.ppa_trade_revisions h
   WHERE h.catalog_id=catalog AND h.trade_id=ids[1] AND security.ppa_visible_at(catalog,h.publication_order,h.effective_at,context,as_of)),'[]'::jsonb) ELSE '[]'::jsonb END) INTO result;
 FOREACH r IN ARRAY heads LOOP
  IF NOT security.reference_access(r.reference_catalog_id,'ReadMarketData') THEN RAISE EXCEPTION 'Current reference access expired' USING ERRCODE='42501';END IF;
 END LOOP;
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'Trading access expired' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
-- PPA current-request readers retain an explicit certified historical cutoff.
-- These functions never bind or clear the request's market context.
CREATE FUNCTION security.ppa_require_market(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,context uuid,valuation timestamptz,pins uuid[])
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE t market_data.temporal_contexts;prefix integer;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'No current PPA market access' USING ERRCODE='42501';END IF;
 IF NOT security.current_write_context() THEN RAISE EXCEPTION 'ppa_historical' USING ERRCODE='P0001';END IF;
 SELECT c.* INTO t FROM market_data.temporal_contexts c
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(c.catalog_id,c.id)
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(c.catalog_id,c.id)
  WHERE c.catalog_id=catalog AND c.id=context AND c.effective_on=(valuation AT TIME ZONE 'UTC')::date;
 IF NOT FOUND OR valuation IS NULL OR NOT isfinite(valuation) OR pins IS NULL OR
  EXISTS(SELECT 1 FROM unnest(pins) p WHERE p IS NULL) OR(SELECT count(DISTINCT p) FROM unnest(pins) p)<>cardinality(pins)
 THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 SELECT m.known_through INTO prefix FROM market_data.market_contexts m WHERE(m.catalog_id,m.context_id)=(catalog,context);
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions r JOIN ref_data.revisions q ON(q.catalog_id,q.id)=(r.reference_catalog_id,r.reference_revision_id)
  WHERE(r.catalog_id,r.id)=(catalog,definition) AND(r.reference_catalog_id,r.reference_revision_id)=(ref_catalog,ref_revision) AND r.state='sealed' AND q.state='sealed')
 THEN RAISE EXCEPTION 'ppa_reference_revision' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.knowledge_events e
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN market_data.revision_periods p ON(p.catalog_id,p.revision_id)=(e.catalog_id,e.revision_id)
  WHERE e.catalog_id=catalog AND e.revision_id=definition AND e.observation_id IS NULL AND e.event_order<=prefix AND k.known_from<=t.known_at
   AND p.effective_from<=t.effective_on AND t.effective_on<p.effective_to)
 THEN RAISE EXCEPTION 'ppa_definition_revision' USING ERRCODE='P0001';END IF;
 IF(SELECT count(*) FROM market_data.observations o
  JOIN market_data.knowledge_events e ON(e.catalog_id,e.revision_id,e.observation_id)=(o.catalog_id,o.definition_revision_id,o.id)
  JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  WHERE o.catalog_id=catalog AND o.definition_revision_id=definition AND o.id=ANY(pins)
   AND e.event_order<=prefix AND k.known_from<=t.known_at AND o.observed_at<=valuation)<>cardinality(pins)
 THEN RAISE EXCEPTION 'ppa_observation_knowledge' USING ERRCODE='P0001';END IF;
 IF NOT security.trading_access(catalog,'ReadTrades') OR NOT security.reference_access(ref_catalog,'ReadMarketData')
 THEN RAISE EXCEPTION 'Current PPA market access expired' USING ERRCODE='42501';END IF;
END$$;

-- A committed, eligible sealed bundle proves prior availability of its exact
-- published TOU references. Standalone TOU audit timestamps do not prove commit
-- knowledge; profiles selecting an unproven version are explicitly unsupported.
CREATE FUNCTION security.ppa_tou_versions(catalog uuid,definition uuid,ref_catalog uuid,ref_revision uuid,context uuid,valuation timestamptz,requested jsonb)
RETURNS SETOF jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb[];BEGIN
 PERFORM security.ppa_require_market(catalog,definition,ref_catalog,ref_revision,context,valuation,ARRAY[]::uuid[]);
 IF requested IS NULL OR jsonb_typeof(requested)<>'array' THEN RAISE EXCEPTION 'ppa_tou_knowledge' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(requested) q WHERE jsonb_typeof(q)<>'object' OR NOT q ?& ARRAY['id','version'] OR
  (SELECT count(*) FROM jsonb_object_keys(q))<>2 OR q->>'id' IS NULL OR q->>'version' IS NULL)
 THEN RAISE EXCEPTION 'ppa_tou_knowledge' USING ERRCODE='P0001';END IF;
 SELECT coalesce(array_agg(to_jsonb(v)||jsonb_build_object('code',p.code) ORDER BY p.code,v.version),ARRAY[]::jsonb[]) INTO result
 FROM market_data.tou_product_versions v JOIN market_data.tou_products p ON(p.catalog_id,p.id)=(v.catalog_id,v.product_id)
 WHERE v.catalog_id=catalog AND v.state='published' AND(v.clock_catalog_id,v.clock_revision_id)=(ref_catalog,ref_revision)
  AND EXISTS(SELECT 1 FROM market_data.curve_blocks b WHERE(b.catalog_id,b.revision_id,b.tou_product_id,b.tou_version)=(catalog,definition,v.product_id,v.version));
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(requested) q WHERE NOT EXISTS(
  SELECT 1 FROM unnest(result) r WHERE(r->>'product_id')::uuid=(q->>'id')::uuid AND(r->>'version')::integer=(q->>'version')::integer))
 THEN RAISE EXCEPTION 'ppa_tou_knowledge' USING ERRCODE='P0001';END IF;
 PERFORM security.ppa_require_market(catalog,definition,ref_catalog,ref_revision,context,valuation,ARRAY[]::uuid[]);
 RETURN QUERY SELECT r FROM unnest(result) r;
EXCEPTION WHEN invalid_text_representation OR numeric_value_out_of_range THEN RAISE EXCEPTION 'ppa_tou_knowledge' USING ERRCODE='P0001';
END$$;

CREATE FUNCTION security.ppa_discount_default(catalog uuid,ref_catalog uuid,ref_revision uuid,currency text,definition uuid,valuation timestamptz,context uuid)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE candidates jsonb;BEGIN
 PERFORM security.ppa_require_market(catalog,definition,ref_catalog,ref_revision,context,valuation,ARRAY[]::uuid[]);
 SELECT jsonb_agg(to_jsonb(d)) INTO candidates FROM trading.currency_discount_defaults d
  WHERE d.catalog_id=catalog AND d.definition_revision_id=definition AND d.reference_catalog_id=ref_catalog AND d.reference_revision_id=ref_revision
   AND d.currency_code=currency AND security.ppa_visible_at(catalog,d.publication_order,d.effective_at,context,valuation)
  GROUP BY d.effective_at,d.publication_order ORDER BY d.effective_at DESC,d.publication_order DESC LIMIT 1;
 PERFORM security.ppa_require_market(catalog,definition,ref_catalog,ref_revision,context,valuation,ARRAY[]::uuid[]);
 IF jsonb_array_length(candidates)>1 THEN RAISE EXCEPTION 'swap_discount_ambiguous' USING ERRCODE='P0001';END IF;
 RETURN candidates->0;
END$$;

DO $$DECLARE signature regprocedure;BEGIN
 FOREACH signature IN ARRAY ARRAY[
  'security.ppa_require_market(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,uuid[])'::regprocedure,
  'security.ppa_tou_versions(uuid,uuid,uuid,uuid,uuid,timestamp with time zone,jsonb)'::regprocedure,
  'security.ppa_discount_default(uuid,uuid,uuid,text,uuid,timestamp with time zone,uuid)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',signature);
 END LOOP;
END$$;

DO $$DECLARE f record;BEGIN
 FOR f IN SELECT p.oid::regprocedure signature,p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND(p.proname LIKE 'ppa_%' OR p.proname IN('read_ppa_terms','retain_ppa_terms','save_ppa_booking')) LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
  IF f.proname IN('save_ppa_booking','read_ppa_terms','ppa_operation_status','ppa_current_head','ppa_scope_heads','ppa_scope_has_positions','ppa_read_scope','ppa_require_market','ppa_tou_versions','ppa_discount_default') THEN EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f.signature);END IF;
 END LOOP;
END$$;
-- Read-only complete-scope refusal for the dedicated connected-risk consumer.
-- This grants no PPA terms, booking, meter or source-series access.
GRANT EXECUTE ON FUNCTION security.ppa_scope_has_positions(uuid,uuid[]) TO openetrm_risk_worker;
COMMIT;
