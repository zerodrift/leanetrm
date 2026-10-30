-- Complete executable schedules are constructed at booking. This is a format
-- cutover: retained content must be explicitly rebooked, never rebuilt on read.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM trading.side_schedules)
 THEN RAISE EXCEPTION 'side_schedule_program_rebooking_required';END IF;
END$$;

CREATE TABLE trading.side_schedule_programs (
 catalog_id uuid NOT NULL,schedule_id uuid NOT NULL,version text NOT NULL,
 content bytea NOT NULL,content_hash bytea NOT NULL,
 PRIMARY KEY(catalog_id,schedule_id),
 FOREIGN KEY(catalog_id,schedule_id) REFERENCES trading.side_schedules(catalog_id,id),
 CHECK(version='booked_side_program_v1'),CHECK(octet_length(content)>0),
 CHECK(content_hash=sha256(content))
);
ALTER TABLE trading.side_schedule_programs ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.side_schedule_programs FORCE ROW LEVEL SECURITY;
CREATE POLICY internal_content ON trading.side_schedule_programs TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE POLICY scoped_read ON trading.side_schedule_programs FOR SELECT TO openetrm_runtime USING(true);
CREATE POLICY temporal_read ON trading.side_schedule_programs AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.side_schedule_visible(catalog_id,schedule_id));
CREATE POLICY worker_read ON trading.side_schedule_programs FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
CREATE POLICY worker_temporal_read ON trading.side_schedule_programs AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker
 USING(security.side_schedule_visible(catalog_id,schedule_id));
CREATE TRIGGER immutable_record BEFORE INSERT OR UPDATE OR DELETE ON trading.side_schedule_programs
 FOR EACH ROW EXECUTE FUNCTION trading.guard_side_schedule_content();
GRANT SELECT,INSERT ON trading.side_schedule_programs TO openetrm_security_owner;
GRANT SELECT ON trading.side_schedule_programs TO openetrm_runtime,openetrm_risk_worker;

-- The byte codec contains integer JSON numbers only; amounts are exact strings.
-- Check the original tokens before jsonb could normalize 1.0 or 1e0 into 1.
CREATE FUNCTION trading.side_program_integer_tokens(value json,depth integer DEFAULT 0) RETURNS boolean
LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE child json;item record;
BEGIN
 IF depth>=64 THEN RETURN false;END IF;
 IF json_typeof(value)='number' THEN RETURN value::text ~ '^-?(0|[1-9][0-9]*)$';END IF;
 IF json_typeof(value)='object' THEN
  FOR item IN SELECT k,v FROM json_each(value) x(k,v) LOOP
   IF item.k IN('catalogId','revisionId','id','entity','entityCatalog') AND
    (item.v#>>'{}')='00000000-0000-0000-0000-000000000000' THEN RETURN false;END IF;
   IF NOT trading.side_program_integer_tokens(item.v,depth+1) THEN RETURN false;END IF;
  END LOOP;
 ELSIF json_typeof(value)='array' THEN
  FOR child IN SELECT json_array_elements(value) LOOP
   IF NOT trading.side_program_integer_tokens(child,depth+1) THEN RETURN false;END IF;
  END LOOP;
 END IF;
 RETURN true;
END$$;
CREATE FUNCTION trading.side_program_reference(catalog uuid,revision uuid,code text) RETURNS jsonb
LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('revision',jsonb_build_object('catalogId',catalog,'revisionId',revision),'code',code);
$$;
CREATE FUNCTION trading.side_program_instant(value timestamptz) RETURNS text
LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
 SELECT rtrim(rtrim(to_char(value AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US'),'0'),'.')||'+00:00';
$$;
CREATE FUNCTION trading.side_program_nonblank(value text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT value IS NOT NULL AND btrim(value,U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000')<>'';
$$;

-- The earlier typed seal validates source/FK and operator contracts. Executable
-- publication additionally needs a complete, codec-readable ordered program.
CREATE FUNCTION trading.require_side_program_structure(catalog uuid,schedule uuid,document jsonb) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog SET enable_nestloop=off SET jit=off AS $$
DECLARE h trading.side_schedules;
BEGIN
 SELECT * INTO STRICT h FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule);
 IF jsonb_typeof(document->'instructions') IS DISTINCT FROM 'array' OR
  (SELECT count(*) FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.row_count OR
  (SELECT min(ordinal)<>0 OR max(ordinal)<>h.row_count-1 FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
  (SELECT count(*) FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.edge_count OR
  (SELECT count(*) FROM trading.side_schedule_bindings WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.binding_count OR
  (SELECT count(*) FROM trading.side_schedule_roots WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.root_count OR
  (SELECT count(*) FROM trading.side_schedule_ports WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.port_count OR
  (SELECT count(*) FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule))<>h.grid_count OR
  (SELECT min(ordinal)<>0 OR max(ordinal)<>h.grid_count-1 FROM trading.side_schedule_grids WHERE(catalog_id,schedule_id)=(catalog,schedule)) OR
  NOT trading.side_program_nonblank(document->>'economicIdentity') OR
  EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule) AND(
   r.local_key='' OR r.interval_from IS NOT NULL AND(r.delivery_from IS NOT NULL OR
    r.hour_beginning IS NULL OR r.hour_beginning NOT BETWEEN 0 AND 23 OR r.minute IS NULL OR r.minute NOT BETWEEN 0 AND 59 OR
    r.occurrence IS NULL OR r.occurrence NOT IN(1,2)) OR
   r.interval_from IS NULL AND num_nonnulls(r.interval_to,r.operating_date,r.hour_beginning,r.minute,r.occurrence)>0 OR
   EXISTS(SELECT 1 FROM unnest(ARRAY[r.delivery_from,r.delivery_to,r.observation_from,r.observation_to,r.operating_date]) d
    WHERE d<DATE '0001-01-01' OR d>DATE '9999-12-31'))) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule) AND(
   NOT trading.side_program_nonblank(g.local_key) OR g.month<DATE '2000-01-01' OR g.month>=DATE '2100-01-01' OR
   g.step_ticks::numeric*g.axis_count>extract(epoch FROM(TIMESTAMPTZ '2100-01-02 00:00:00+00'-g.origin))*10000000 OR
   NOT EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id,r.grid_ordinal)=(catalog,schedule,g.ordinal)))) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
   AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_edges e WHERE(e.catalog_id,e.schedule_id,e.source)=(catalog,schedule,r.ordinal))
   AND NOT EXISTS(SELECT 1 FROM trading.side_schedule_roots o WHERE(o.catalog_id,o.schedule_id,o.row_ordinal)=(catalog,schedule,r.ordinal))) OR
  EXISTS(SELECT 1 FROM jsonb_array_elements(document->'instructions') i
   WHERE i#>>'{output,operation}' IN('price_lookup','quantity_input','asset_capacity','discount') AND i->'consumers'='[]'::jsonb) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_roots o WHERE(o.catalog_id,o.schedule_id)=(catalog,schedule)
   AND(NOT trading.side_program_nonblank(o.name) OR NOT trading.side_program_nonblank(o.measure))) OR
  (SELECT count(*) FROM trading.side_schedule_roots o WHERE(o.catalog_id,o.schedule_id,o.measure)=(catalog,schedule,'pv'))<>1 OR
  NOT EXISTS(SELECT 1 FROM trading.side_schedule_roots o JOIN trading.side_schedule_rows r
   ON(r.catalog_id,r.schedule_id,r.ordinal)=(o.catalog_id,o.schedule_id,o.row_ordinal)
   WHERE(o.catalog_id,o.schedule_id,o.name,o.measure)=(catalog,schedule,'pv','pv') AND r.output_kind='money' AND r.grid_ordinal IS NULL) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_ports p WHERE(p.catalog_id,p.schedule_id)=(catalog,schedule)
   AND(p.slot_role IS NULL OR p.slot_role NOT IN('1','2','3','4','5','6','7','8') OR p.kind<>'quantity' OR
    NOT trading.side_program_nonblank(p.name) OR NOT trading.side_program_nonblank(p.shape_identity))) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r
   ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
   JOIN trading.side_schedule_ports p ON(p.catalog_id,p.schedule_id,p.name)=(b.catalog_id,b.schedule_id,b.coordinate)
   WHERE(b.catalog_id,b.schedule_id,b.kind)=(catalog,schedule,'sibling_quantity') AND(
    (p.unit_catalog_id,p.unit_revision_id,p.unit_code) IS DISTINCT FROM(r.unit_catalog_id,r.unit_revision_id,r.unit_code) OR
    p.slot_ordinal IS DISTINCT FROM b.position)) OR
  EXISTS(SELECT 1 FROM trading.side_schedule_bindings b JOIN trading.side_schedule_bindings m
   ON(m.catalog_id,m.schedule_id,m.row_ordinal,m.port)=(b.catalog_id,b.schedule_id,b.row_ordinal,'market')
   WHERE(b.catalog_id,b.schedule_id,b.kind)=(catalog,schedule,'custom_output') AND(
    (b.source_catalog_id,b.definition_revision_id,b.curve_code,b.contract_id)
     IS DISTINCT FROM(m.source_catalog_id,m.definition_revision_id,m.curve_code,m.contract_id) OR
    b.custom_version<=0 OR NOT trading.side_program_nonblank(b.coordinate)))
 THEN RAISE EXCEPTION 'side_schedule_program';END IF;
END$$;

-- Booking/sealing validation only. This independent projection proves complete
-- correspondence to the relational FK authority; valuation never calls it.
CREATE FUNCTION trading.side_program_document(catalog uuid,schedule uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog SET enable_nestloop=off SET jit=off AS $$
 WITH RECURSIVE
 rows AS MATERIALIZED(SELECT * FROM trading.side_schedule_rows WHERE(catalog_id,schedule_id)=(catalog,schedule)),
 edges AS MATERIALIZED(SELECT * FROM trading.side_schedule_edges WHERE(catalog_id,schedule_id)=(catalog,schedule)),
 walk(source,node) AS(
  SELECT r.ordinal,e.target FROM rows r JOIN edges e ON e.source=r.ordinal
   WHERE r.operation IN('price_lookup','quantity_input','asset_capacity','discount')
  UNION
  SELECT w.source,e.target FROM walk w JOIN rows r ON r.ordinal=w.node
   JOIN edges e ON e.source=w.node WHERE r.operation<>'output_guard'),
 consumers AS(SELECT w.source,jsonb_agg(w.node ORDER BY w.node) addresses
  FROM walk w JOIN rows r ON r.ordinal=w.node WHERE r.operation='output_guard' GROUP BY w.source),
 dependents AS(SELECT source,jsonb_agg(target ORDER BY target) addresses FROM(SELECT DISTINCT source,target FROM edges) e GROUP BY source),
 operands AS(SELECT target,jsonb_object_agg(port,addresses ORDER BY port) ports
  FROM(SELECT target,port,jsonb_agg(source ORDER BY ordinal) addresses FROM edges GROUP BY target,port) p GROUP BY target),
 bindings AS(SELECT b.row_ordinal,jsonb_agg(jsonb_build_object(
  'row',b.row_ordinal,'port',b.port,'kind',b.kind,
  'reference',trading.side_program_reference(b.reference_catalog_id,b.reference_revision_id,CASE b.kind
   WHEN 'index_fixing' THEN b.source_code WHEN 'sibling_quantity' THEN b.quantity_code WHEN 'location' THEN b.location_code
   WHEN 'index' THEN b.index_code WHEN 'quote' THEN b.quote_code END),
  'market',CASE WHEN b.contract_id IS NOT NULL THEN jsonb_build_object(
   'curve',trading.side_program_reference(b.source_catalog_id,b.definition_revision_id,b.curve_code),
   'contract',jsonb_build_object('revision',jsonb_build_object('catalogId',b.source_catalog_id,'revisionId',b.definition_revision_id),'id',b.contract_id)) END,
  'series',CASE WHEN b.series_id IS NOT NULL THEN jsonb_build_object(
   'revision',jsonb_build_object('catalogId',b.source_catalog_id,'revisionId',b.definition_revision_id),'id',b.series_id) END,
  'entity',b.custom_curve_id,'version',b.custom_version,'coordinate',b.coordinate,'position',b.position,
  'definitions',CASE WHEN b.kind IN('curve_interval','quantity_source','asset') THEN
   jsonb_build_object('catalogId',b.source_catalog_id,'revisionId',b.definition_revision_id) END,
  'code',CASE b.kind WHEN 'curve_interval' THEN coalesce(b.shaped_curve_code,b.curve_code)
   WHEN 'quantity_source' THEN b.quantity_source_code WHEN 'asset' THEN b.asset_code END,
  'entityCatalog',b.custom_catalog_id) ORDER BY b.port COLLATE "C") items
  FROM trading.side_schedule_bindings b WHERE(b.catalog_id,b.schedule_id)=(catalog,schedule) GROUP BY b.row_ordinal),
 instructions AS(SELECT jsonb_agg(jsonb_build_object('output',jsonb_build_object(
  'ordinal',r.ordinal,'key',r.local_key,'role',r.role,'operation',r.operation,'version',r.operation_version,'outputKind',r.output_kind,
  'unit',trading.side_program_reference(r.unit_catalog_id,r.unit_revision_id,r.unit_code),
  'currency',trading.side_program_reference(r.currency_catalog_id,r.currency_revision_id,r.currency_code),
  'quoteMultiplier',trim_scale(r.quote_multiplier)::text,'deliveryFrom',r.delivery_from,'deliveryTo',r.delivery_to,
  'interval',CASE WHEN r.interval_from IS NOT NULL THEN jsonb_build_object(
   'start',trading.side_program_instant(r.interval_from),'end',trading.side_program_instant(r.interval_to),'operatingDate',r.operating_date,
   'hourBeginning',r.hour_beginning,'minute',r.minute,'occurrence',r.occurrence) END,
  'observationFrom',r.observation_from,'observationTo',r.observation_to,'valueAt',trading.side_program_instant(r.value_at),
  'paymentOrdinal',r.payment_ordinal,'occurrenceKey',r.occurrence_key,'termsDigest',r.terms_digest,'parameters',r.parameters,'grid',r.grid_ordinal),
  'inputs',coalesce(o.ports,'{}'::jsonb),'bindings',coalesce(b.items,'[]'::jsonb),
  'dependents',coalesce(d.addresses,'[]'::jsonb),'consumers',coalesce(c.addresses,'[]'::jsonb)) ORDER BY r.ordinal) items
  FROM rows r LEFT JOIN operands o ON o.target=r.ordinal LEFT JOIN bindings b ON b.row_ordinal=r.ordinal
   LEFT JOIN dependents d ON d.source=r.ordinal LEFT JOIN consumers c ON c.source=r.ordinal)
 SELECT jsonb_build_object('version','booked_side_program_v1','scheduleManifest',encode(h.content_hash,'hex'),
  'family',s.family,'economicIdentity',s.economic_identity,'pricingBindingIdentity',s.pricing_binding_identity,
  'instructions',(SELECT items FROM instructions),
  'grids',coalesce((SELECT jsonb_agg(jsonb_build_object(
   'ordinal',g.ordinal,'key',g.local_key,'clock',trading.side_program_reference(g.clock_catalog_id,g.clock_revision_id,g.clock_code),
   'month',g.month,'firstRowOrdinal',g.first_row_ordinal,'origin',trading.side_program_instant(g.origin),
   'stepTicks',g.step_ticks,'axisCount',g.axis_count,'selection',g.selection,'lengths',g.lengths,'operatingDays',g.operating_days,
   'localMinutes',g.local_minutes,'occurrences',g.occurrences) ORDER BY g.ordinal)
   FROM trading.side_schedule_grids g WHERE(g.catalog_id,g.schedule_id)=(catalog,schedule)),'[]'::jsonb),
  'roots',coalesce((SELECT jsonb_agg(jsonb_build_object('name',r.name,'row',r.row_ordinal,'measure',r.measure) ORDER BY r.sequence_ordinal)
   FROM trading.side_schedule_roots r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)),'[]'::jsonb),
  'ports',coalesce((SELECT jsonb_agg(jsonb_build_object('name',p.name,'kind',p.kind,
   'slot',CASE WHEN p.slot_role IS NOT NULL THEN jsonb_build_object('role',p.slot_role::integer,'ordinal',p.slot_ordinal) END,
   'unit',trading.side_program_reference(p.unit_catalog_id,p.unit_revision_id,p.unit_code),'shapeIdentity',p.shape_identity) ORDER BY p.sequence_ordinal)
   FROM trading.side_schedule_ports p WHERE(p.catalog_id,p.schedule_id)=(catalog,schedule)),'[]'::jsonb))
 FROM trading.side_schedules h JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
 WHERE(h.catalog_id,h.id)=(catalog,schedule);
$$;

CREATE FUNCTION trading.require_side_program_content(catalog uuid,schedule uuid,content bytea) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog SET jit=off AS $$
DECLARE original text;document jsonb;
BEGIN
 original=convert_from(content,'UTF8');
 IF original IS NULL OR NOT(original IS JSON OBJECT WITH UNIQUE KEYS)
  OR NOT trading.side_program_integer_tokens(original::json)
 THEN RAISE EXCEPTION 'side_schedule_program';END IF;
 document=original::jsonb;
 IF document->>'version' IS DISTINCT FROM 'booked_side_program_v1'
 THEN RAISE EXCEPTION 'side_schedule_unsupported';END IF;
 IF EXISTS(SELECT 1 FROM trading.side_schedule_rows r WHERE(r.catalog_id,r.schedule_id)=(catalog,schedule)
  AND r.operation='output_guard' AND(
   r.value_at IS NULL OR r.currency_code IS NULL OR
   r.parameters->>'lifecycle' NOT IN('undue_payment_v1','interval_payment_v1','interval_delivery_v1','certificate_holding_v1') OR
   (r.parameters->>'lifecycle'='undue_payment_v1') IS DISTINCT FROM(r.grid_ordinal IS NULL) OR
   r.parameters->>'measure' IS DISTINCT FROM CASE WHEN r.grid_ordinal IS NULL THEN 'present_value_v1' ELSE 'value_excluding_overdue_v1' END))
 THEN RAISE EXCEPTION 'side_schedule_program';END IF;
 IF document IS DISTINCT FROM trading.side_program_document(catalog,schedule)
 THEN RAISE EXCEPTION 'side_schedule_program';END IF;
 PERFORM trading.require_side_program_structure(catalog,schedule,document);
EXCEPTION WHEN data_exception OR program_limit_exceeded THEN RAISE EXCEPTION 'side_schedule_program';
END$$;

CREATE FUNCTION security.seal_side_schedule_program(catalog uuid,request uuid,schedule uuid,content bytea) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE prior trading.side_schedule_programs;
BEGIN
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
 PERFORM 1 FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule) FOR UPDATE;
 SELECT * INTO prior FROM trading.side_schedule_programs WHERE(catalog_id,schedule_id)=(catalog,schedule);
 IF FOUND THEN
  IF content IS DISTINCT FROM prior.content THEN RAISE EXCEPTION 'side_schedule_program_conflict';END IF;
 ELSE
  IF EXISTS(SELECT 1 FROM trading.side_schedule_seals WHERE(catalog_id,schedule_id)=(catalog,schedule))
  THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
  PERFORM trading.require_side_program_content(catalog,schedule,content);
  INSERT INTO trading.side_schedule_programs VALUES(catalog,schedule,'booked_side_program_v1',content,sha256(content));
 END IF;
 -- Any typed source/shape/arity failure must also roll back the immutable
 -- executable bytes. Both seals share this lock and this transaction.
 PERFORM security.seal_side_schedule(catalog,request,schedule,
  (SELECT encode(content_hash,'hex') FROM trading.side_schedules WHERE(catalog_id,id)=(catalog,schedule)));
 PERFORM security.side_schedule_stage_authority(catalog,request,schedule);
END$$;

CREATE FUNCTION trading.require_side_schedule_program() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET jit=off AS $$
DECLARE program trading.side_schedule_programs;
BEGIN
 SELECT * INTO program FROM trading.side_schedule_programs WHERE(catalog_id,schedule_id)=(NEW.catalog_id,NEW.schedule_id);
 IF NOT FOUND THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 PERFORM trading.require_side_program_content(NEW.catalog_id,NEW.schedule_id,program.content);
 RETURN NEW;
END$$;
CREATE TRIGGER require_executable_program BEFORE INSERT ON trading.side_schedule_seals
 FOR EACH ROW EXECUTE FUNCTION trading.require_side_schedule_program();

-- The complete authorized header and executable bytes cross one read boundary.
-- No typed calculation pages or booking validator are invoked by this read.
CREATE FUNCTION security.read_instrument_side_programs(catalog uuid,instrument uuid,context uuid,as_of timestamptz)
 RETURNS TABLE(side_id uuid,schedule_id uuid,slot integer,version text,content bytea,content_digest text,
  schedule_manifest text,family text,economic_identity text,pricing_binding_identity text,schema_version text,generator_version text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE item jsonb;selected uuid;
BEGIN
 PERFORM security.require_side_schedule_read_scope(catalog,context,as_of);
 IF context IS NOT NULL THEN PERFORM security.require_trading_history(catalog,context);END IF;
 FOR item IN SELECT security.read_instrument_side_schedule_headers(catalog,instrument,context,as_of) LOOP
  selected=(item->>'schedule_id')::uuid;
  IF NOT EXISTS(SELECT 1 FROM trading.side_schedule_programs p WHERE(p.catalog_id,p.schedule_id)=(catalog,selected))
  THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
  IF EXISTS(SELECT 1 FROM trading.side_schedule_programs p WHERE(p.catalog_id,p.schedule_id)=(catalog,selected) AND p.version<>'booked_side_program_v1')
  THEN RAISE EXCEPTION 'side_schedule_unsupported';END IF;
  RETURN QUERY SELECT (item->>'side_id')::uuid,selected,(item->>'slot')::integer,p.version,p.content,encode(p.content_hash,'hex'),
   encode(h.content_hash,'hex'),s.family,s.economic_identity,s.pricing_binding_identity,h.schema_version,h.generator_version
   FROM trading.side_schedule_programs p JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(p.catalog_id,p.schedule_id)
   JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id) WHERE(p.catalog_id,p.schedule_id)=(catalog,selected);
 END LOOP;
END$$;

ALTER FUNCTION trading.side_program_integer_tokens(json,integer) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.side_program_reference(uuid,uuid,text) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.side_program_instant(timestamptz) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.side_program_nonblank(text) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.require_side_program_structure(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.side_program_document(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.require_side_program_content(uuid,uuid,bytea) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.require_side_schedule_program() OWNER TO openetrm_security_owner;
ALTER FUNCTION security.seal_side_schedule_program(uuid,uuid,uuid,bytea) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.read_instrument_side_programs(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.side_program_integer_tokens(json,integer),trading.side_program_reference(uuid,uuid,text),
 trading.side_program_instant(timestamptz),trading.side_program_nonblank(text),trading.require_side_program_structure(uuid,uuid,jsonb),
 trading.side_program_document(uuid,uuid),trading.require_side_program_content(uuid,uuid,bytea),
 trading.require_side_schedule_program(),security.seal_side_schedule_program(uuid,uuid,uuid,bytea),
 security.read_instrument_side_programs(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.seal_side_schedule_program(uuid,uuid,uuid,bytea) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.read_instrument_side_programs(uuid,uuid,uuid,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
