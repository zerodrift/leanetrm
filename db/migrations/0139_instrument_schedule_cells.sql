-- Resolve instrument-owned port geometry once at booking, never during PV.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM trading.side_schedule_request_seals)
 THEN RAISE EXCEPTION 'instrument_schedule_rebooking_required' USING ERRCODE='P0001';END IF;
END$$;

CREATE FUNCTION trading.instrument_cell_addresses(cells integer[]) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $$
 SELECT cardinality(cells)>0 AND array_ndims(cells)=1 AND array_lower(cells,1)=1
  AND NOT EXISTS(SELECT 1 FROM generate_subscripts(cells,1) n
   WHERE cells[n] IS NULL OR cells[n]<0 OR n>1 AND cells[n]<=cells[n-1]);
$$;
CREATE TABLE trading.instrument_schedule_cells (
 catalog_id uuid NOT NULL,request_id uuid NOT NULL,target_slot integer NOT NULL,target_schedule uuid NOT NULL,port text NOT NULL,
 input_row integer NOT NULL,source_slot integer NOT NULL,source_schedule uuid NOT NULL,output_root text NOT NULL,
 input_cells integer[] NOT NULL,output_cells integer[] NOT NULL,
 PRIMARY KEY(catalog_id,request_id,target_slot,input_row,source_slot,output_root),
 FOREIGN KEY(catalog_id,request_id,target_slot,port) REFERENCES trading.side_schedule_request_inputs(catalog_id,request_id,target_slot,port),
 FOREIGN KEY(catalog_id,request_id,target_slot,target_schedule) REFERENCES trading.side_schedule_request_items(catalog_id,request_id,slot,schedule_id),
 FOREIGN KEY(catalog_id,request_id,source_slot,source_schedule) REFERENCES trading.side_schedule_request_items(catalog_id,request_id,slot,schedule_id),
 FOREIGN KEY(catalog_id,target_schedule,input_row) REFERENCES trading.side_schedule_rows(catalog_id,schedule_id,ordinal),
 FOREIGN KEY(catalog_id,source_schedule,output_root) REFERENCES trading.side_schedule_roots(catalog_id,schedule_id,name),
 CHECK(trading.instrument_cell_addresses(input_cells) AND trading.instrument_cell_addresses(output_cells)),
 CHECK(cardinality(input_cells)=cardinality(output_cells))
);
CREATE TABLE trading.instrument_schedule_cell_seals (
 catalog_id uuid NOT NULL,request_id uuid NOT NULL,version text NOT NULL CHECK(version='instrument_quantity_cells_v1'),
 mapping_count integer NOT NULL CHECK(mapping_count>=0),
 PRIMARY KEY(catalog_id,request_id),
 FOREIGN KEY(catalog_id,request_id) REFERENCES trading.side_schedule_request_seals(catalog_id,request_id)
);
CREATE FUNCTION trading.guard_instrument_schedule_cells() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP<>'INSERT' OR EXISTS(SELECT 1 FROM trading.instrument_schedule_cell_seals s WHERE(s.catalog_id,s.request_id)=(NEW.catalog_id,NEW.request_id))
 THEN RAISE EXCEPTION 'instrument_schedule_immutable';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER immutable_record BEFORE INSERT OR UPDATE OR DELETE ON trading.instrument_schedule_cells
 FOR EACH ROW EXECUTE FUNCTION trading.guard_instrument_schedule_cells();
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.instrument_schedule_cell_seals
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['instrument_schedule_cells','instrument_schedule_cell_seals'] LOOP
  EXECUTE format('ALTER TABLE trading.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE trading.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT ON trading.%I TO openetrm_security_owner',t);
  EXECUTE format('CREATE POLICY internal_content ON trading.%I TO openetrm_security_owner USING(true) WITH CHECK(true)',t);
 END LOOP;
END$$;

ALTER FUNCTION security.seal_side_schedule_request(uuid,uuid,jsonb) RENAME TO seal_side_schedule_ports;
REVOKE ALL ON FUNCTION security.seal_side_schedule_ports(uuid,uuid,jsonb) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.seal_side_schedule_request(catalog uuid,request uuid,documents jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET enable_nestloop=off SET jit=off AS $$
DECLARE written integer;selected uuid;
BEGIN
 -- The original admission retains the request lock and validates exact port links.
 PERFORM security.seal_side_schedule_ports(catalog,request,documents);
 IF NOT EXISTS(SELECT 1 FROM trading.instrument_schedule_cell_seals s WHERE(s.catalog_id,s.request_id)=(catalog,request)) THEN
 WITH targets AS MATERIALIZED (
  SELECT l.*,r.ordinal input_row,g.clock_catalog_id,g.clock_revision_id,g.clock_code,
   r.unit_catalog_id,r.unit_revision_id,r.unit_code,n-1 cell,
   g.origin+interval '1 microsecond'*(g.step_ticks/10*g.selection[n]) first_at,
   g.origin+interval '1 microsecond'*(g.step_ticks/10*(g.selection[n]::bigint+g.lengths[n])) last_at,
   g.operating_days[n] operating_day,g.local_minutes[n] local_minute,g.occurrences[n] occurrence
  FROM trading.side_schedule_request_inputs l
  JOIN trading.side_schedule_bindings b ON(b.catalog_id,b.schedule_id,b.coordinate)=(l.catalog_id,l.target_schedule,l.port) AND b.kind='sibling_quantity'
  JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
  JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(r.catalog_id,r.schedule_id,r.grid_ordinal)
  CROSS JOIN LATERAL generate_subscripts(g.selection,1) n
  WHERE(l.catalog_id,l.request_id)=(catalog,request) AND r.operation='quantity_input'
 ), sources AS MATERIALIZED (
  SELECT l.target_slot,l.port,l.source_slot,l.source_schedule,o.root_name,
   g.clock_catalog_id,g.clock_revision_id,g.clock_code,r.unit_catalog_id,r.unit_revision_id,r.unit_code,n-1 cell,
   g.origin+interval '1 microsecond'*(g.step_ticks/10*g.selection[n]) first_at,
   g.origin+interval '1 microsecond'*(g.step_ticks/10*(g.selection[n]::bigint+g.lengths[n])) last_at,
   g.operating_days[n] operating_day,g.local_minutes[n] local_minute,g.occurrences[n] occurrence
  FROM trading.side_schedule_request_inputs l
  JOIN trading.side_schedule_output_members o ON(o.catalog_id,o.schedule_id,o.set_name)=(l.catalog_id,l.source_schedule,l.output_name)
  JOIN trading.side_schedule_roots root ON(root.catalog_id,root.schedule_id,root.name)=(o.catalog_id,o.schedule_id,o.root_name) AND root.measure='schedule_quantity'
  JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(root.catalog_id,root.schedule_id,root.row_ordinal) AND r.output_kind='quantity'
  JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(r.catalog_id,r.schedule_id,r.grid_ordinal)
  CROSS JOIN LATERAL generate_subscripts(g.selection,1) n
  WHERE(l.catalog_id,l.request_id)=(catalog,request)
 )
 INSERT INTO trading.instrument_schedule_cells
 SELECT catalog,request,t.target_slot,t.target_schedule,t.port,t.input_row,s.source_slot,s.source_schedule,s.root_name,
  array_agg(t.cell ORDER BY t.cell),array_agg(s.cell ORDER BY t.cell)
 FROM targets t JOIN sources s ON(s.target_slot,s.port,s.source_slot,s.source_schedule)=(t.target_slot,t.port,t.source_slot,t.source_schedule)
  AND(s.clock_catalog_id,s.clock_revision_id,s.clock_code,s.unit_catalog_id,s.unit_revision_id,s.unit_code,s.first_at,s.last_at,s.operating_day,s.local_minute,s.occurrence)=
     (t.clock_catalog_id,t.clock_revision_id,t.clock_code,t.unit_catalog_id,t.unit_revision_id,t.unit_code,t.first_at,t.last_at,t.operating_day,t.local_minute,t.occurrence)
 GROUP BY t.target_slot,t.target_schedule,t.port,t.input_row,s.source_slot,s.source_schedule,s.root_name;
 GET DIAGNOSTICS written=ROW_COUNT;
 -- Every input cell must occur exactly once, even across split output blocks.
 IF EXISTS(
  SELECT 1 FROM trading.side_schedule_request_inputs l
  JOIN trading.side_schedule_bindings b ON(b.catalog_id,b.schedule_id,b.coordinate)=(l.catalog_id,l.target_schedule,l.port) AND b.kind='sibling_quantity'
  JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal)
  JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(r.catalog_id,r.schedule_id,r.grid_ordinal)
  WHERE(l.catalog_id,l.request_id)=(catalog,request) AND
   ARRAY(SELECT cell FROM trading.instrument_schedule_cells m CROSS JOIN LATERAL unnest(m.input_cells) cell
    WHERE(m.catalog_id,m.request_id,m.target_slot,m.input_row)=(catalog,request,l.target_slot,r.ordinal) ORDER BY cell)
    IS DISTINCT FROM ARRAY(SELECT generate_series(0,cardinality(g.selection)-1)))
 THEN RAISE EXCEPTION 'instrument_schedule_cells';END IF;
 INSERT INTO trading.instrument_schedule_cell_seals VALUES(catalog,request,'instrument_quantity_cells_v1',written);
 END IF;
 FOR selected IN SELECT DISTINCT i.schedule_id FROM trading.side_schedule_request_items i WHERE(i.catalog_id,i.request_id)=(catalog,request) LOOP
  PERFORM security.side_schedule_stage_authority(catalog,request,selected);
 END LOOP;
 PERFORM security.side_schedule_request_authority(catalog,request);
END$$;

CREATE FUNCTION trading.require_instrument_schedule_cells() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM trading.instrument_schedule_cell_seals s WHERE(s.catalog_id,s.request_id)=(NEW.catalog_id,NEW.request_id))
 THEN RAISE EXCEPTION 'instrument_schedule_missing';END IF;
 RETURN NEW;
END$$;
CREATE CONSTRAINT TRIGGER retained_instrument_cells AFTER INSERT ON trading.instrument_schedule_compositions
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION trading.require_instrument_schedule_cells();

CREATE FUNCTION security.read_instrument_schedule_cells(catalog uuid,instrument uuid,context uuid,as_of timestamptz)
 RETURNS TABLE(input_side uuid,input_row integer,output_side uuid,output_root text,input_cells integer[],output_cells integer[])
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE request uuid;members bigint;expected integer;actual integer;
BEGIN
 PERFORM security.require_side_schedule_read_scope(catalog,context,as_of);
 IF context IS NOT NULL THEN PERFORM security.require_trading_history(catalog,context);END IF;
 SELECT count(*) INTO members FROM security.read_instrument_side_schedule_headers(catalog,instrument,context,as_of);
 SELECT c.request_id,s.mapping_count INTO request,expected FROM trading.instrument_schedule_compositions c
  JOIN trading.instrument_schedule_cell_seals s ON(s.catalog_id,s.request_id)=(c.catalog_id,c.request_id)
  WHERE(c.catalog_id,c.instrument_id)=(catalog,instrument);
 IF request IS NULL OR members=0 THEN RAISE EXCEPTION 'instrument_schedule_missing';END IF;
 RETURN QUERY SELECT t.side_id,m.input_row,s.side_id,m.output_root,m.input_cells,m.output_cells
  FROM trading.instrument_schedule_cells m
  JOIN trading.instrument_side_schedules t ON(t.catalog_id,t.instrument_id,t.slot,t.schedule_id)=(m.catalog_id,instrument,m.target_slot,m.target_schedule)
  JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot,s.schedule_id)=(m.catalog_id,instrument,m.source_slot,m.source_schedule)
  WHERE(m.catalog_id,m.request_id)=(catalog,request) ORDER BY m.target_slot,m.input_row,m.source_slot,m.output_root;
 GET DIAGNOSTICS actual=ROW_COUNT;
 IF actual<>expected THEN RAISE EXCEPTION 'instrument_schedule_cells';END IF;
 IF members<>(SELECT count(*) FROM security.read_instrument_side_schedule_headers(catalog,instrument,context,as_of))
 THEN RAISE EXCEPTION 'instrument_schedule_cells';END IF;
END$$;

ALTER FUNCTION trading.instrument_cell_addresses(integer[]) OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.guard_instrument_schedule_cells() OWNER TO openetrm_security_owner;
ALTER FUNCTION trading.require_instrument_schedule_cells() OWNER TO openetrm_security_owner;
ALTER FUNCTION security.seal_side_schedule_request(uuid,uuid,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.read_instrument_schedule_cells(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.instrument_cell_addresses(integer[]),trading.guard_instrument_schedule_cells(),trading.require_instrument_schedule_cells(),
 security.seal_side_schedule_request(uuid,uuid,jsonb),security.read_instrument_schedule_cells(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.seal_side_schedule_request(uuid,uuid,jsonb) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION security.read_instrument_schedule_cells(uuid,uuid,uuid,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
-- This projection deliberately excludes Side terms, calendars and schedule rows.
CREATE FUNCTION security.read_ppa_program_metadata(catalog uuid,instrument uuid,context uuid,as_of timestamptz)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;expected bigint;
BEGIN
 PERFORM security.require_side_schedule_read_scope(catalog,context,as_of);
 IF context IS NOT NULL THEN PERFORM security.require_trading_history(catalog,context);END IF;
 SELECT count(*) INTO expected FROM security.read_instrument_side_schedule_headers(catalog,instrument,context,as_of);
 SELECT jsonb_build_object('implementation',i.implementation,'definitionRevision',i.definition_revision_id,
  'referenceCatalog',i.reference_catalog_id,'referenceRevision',i.reference_revision_id,
  'clock',i.clock_code,'intervalMinutes',i.interval_minutes,'deliveryFrom',i.delivery_from,'deliveryTo',i.delivery_to,
  'currency',i.currency_code,'unitQuantity',i.unit_quantity_code,'discounting',i.discounting,
  'members',(SELECT jsonb_agg(jsonb_build_object('sideId',m.side_id,'position',m.position,'role',m.role,'ordinal',m.ordinal,
   'coefficient',m.coefficient,'referenceCatalog',s.reference_catalog_id,'referenceRevision',s.reference_revision_id,
   'definitionRevision',s.definition_revision_id,'normalizationBasis',s.normalization_basis_code,'normalizationPolicy',s.normalization_policy,
   'assets',coalesce((SELECT jsonb_agg(a.asset_code ORDER BY a.ordinal) FROM trading.ppa_side_normalization_assets a
     WHERE(a.catalog_id,a.side_id)=(catalog,m.side_id)),'[]'::jsonb)) ORDER BY m.position)
   FROM trading.ppa_memberships m JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id)
   JOIN trading.instrument_side_schedules p ON(p.catalog_id,p.instrument_id,p.slot,p.side_id)=(m.catalog_id,m.instrument_id,m.position,m.side_id)
   WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument))) INTO result
 FROM trading.ppa_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument);
 IF result IS NULL OR result->>'implementation'<>'ppa_instrument_v2'
 THEN RAISE EXCEPTION 'instrument_program_unsupported';END IF;
 IF NOT security.ppa_side_normalization_matches(catalog,instrument) THEN RAISE EXCEPTION 'ppa_normalization';END IF;
 IF jsonb_array_length(result->'members') IS DISTINCT FROM expected OR expected=0
 THEN RAISE EXCEPTION 'instrument_schedule_missing';END IF;
 -- Published membership and source access are checked again after projection.
 PERFORM security.read_instrument_side_schedule_headers(catalog,instrument,context,as_of);
 RETURN result;
END$$;
ALTER FUNCTION security.read_ppa_program_metadata(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_ppa_program_metadata(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_ppa_program_metadata(uuid,uuid,uuid,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
-- The compiler's staging command and accepted PPA booking share one envelope.
-- This prevents attaching a valid artifact prepared for a different command.
ALTER FUNCTION security.publish_side_schedules(uuid,uuid,uuid) RENAME TO publish_admitted_side_schedules;
REVOKE ALL ON FUNCTION security.publish_admitted_side_schedules(uuid,uuid,uuid) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;
CREATE FUNCTION security.publish_side_schedules(catalog uuid,operation uuid,instrument uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE staged trading.side_schedule_requests;accepted jsonb;
BEGIN
 staged=security.side_schedule_request_authority(catalog,operation);
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT r.request INTO accepted FROM trading.ppa_trade_revisions r
  WHERE(r.catalog_id,r.operation_id,r.actor_id)=(catalog,operation,staged.actor_id);
 IF FOUND AND sha256(convert_to(accepted::text,'UTF8')) IS DISTINCT FROM staged.command_hash
 THEN RAISE EXCEPTION 'side_schedule_command';END IF;
 PERFORM security.publish_admitted_side_schedules(catalog,operation,instrument);
END$$;
ALTER FUNCTION security.publish_side_schedules(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.publish_side_schedules(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.publish_side_schedules(uuid,uuid,uuid) TO openetrm_runtime;
DROP FUNCTION security.publish_operation_side_schedules(uuid,uuid);
CREATE FUNCTION security.publish_operation_side_schedules(catalog uuid,operation uuid) RETURNS uuid
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
 RETURN instrument;
END$$;
ALTER FUNCTION security.publish_operation_side_schedules(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.publish_operation_side_schedules(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.publish_operation_side_schedules(uuid,uuid) TO openetrm_runtime;
-- Booking verification has operation authority, not a committed history context.
-- Read only exact sealed identities already attached by this accepted operation.
CREATE FUNCTION security.read_ppa_publication_identities(catalog uuid,operation uuid,instrument uuid)
 RETURNS TABLE(slot integer,side_id uuid,economic_identity text,pricing_binding_identity text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE command trading.side_schedule_requests;item record;expected integer;actual integer=0;
BEGIN
 command=security.side_schedule_request_authority(catalog,operation);
 IF NOT EXISTS(SELECT 1 FROM trading.ppa_trade_revisions r JOIN trading.ppa_revision_bindings b
   ON(b.catalog_id,b.trade_id,b.revision)=(r.catalog_id,r.trade_id,r.revision)
   WHERE(r.catalog_id,r.operation_id,r.actor_id,b.instrument_id)=(catalog,operation,command.actor_id,instrument)
    AND sha256(convert_to(r.request::text,'UTF8'))=command.command_hash)
 THEN RAISE EXCEPTION 'side_schedule_command';END IF;
 SELECT count(*) INTO expected FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument);
 FOR item IN SELECT p.slot,p.side_id,p.schedule_id,s.economic_identity,s.pricing_binding_identity
  FROM trading.instrument_side_schedules p
  JOIN trading.side_schedule_request_items i ON(i.catalog_id,i.request_id,i.slot,i.schedule_id)=(p.catalog_id,operation,p.slot,p.schedule_id)
  JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(p.catalog_id,p.schedule_id)
  JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id)
  JOIN trading.side_schedule_seals seal ON(seal.catalog_id,seal.schedule_id)=(p.catalog_id,p.schedule_id)
  JOIN trading.side_schedule_programs program ON(program.catalog_id,program.schedule_id)=(p.catalog_id,p.schedule_id)
  WHERE(p.catalog_id,p.instrument_id)=(catalog,instrument) ORDER BY p.slot LOOP
  PERFORM security.side_schedule_stage_authority(catalog,operation,item.schedule_id);
  actual=actual+1;
  RETURN QUERY SELECT item.slot,item.side_id,item.economic_identity,item.pricing_binding_identity;
 END LOOP;
 IF expected=0 OR actual<>expected THEN RAISE EXCEPTION 'side_schedule_missing';END IF;
 FOR item IN SELECT DISTINCT i.schedule_id FROM trading.side_schedule_request_items i
  WHERE(i.catalog_id,i.request_id)=(catalog,operation) LOOP
  PERFORM security.side_schedule_stage_authority(catalog,operation,item.schedule_id);
 END LOOP;
 PERFORM security.side_schedule_request_authority(catalog,operation);
END$$;
ALTER FUNCTION security.read_ppa_publication_identities(uuid,uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_ppa_publication_identities(uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_ppa_publication_identities(uuid,uuid,uuid) TO openetrm_runtime;
-- Actual quote/factor references need their own authority until publication
-- proves they equal the staged program's exact reference bindings.
CREATE FUNCTION security.require_swap_metadata_references(catalog uuid,instrument uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE selected uuid;
BEGIN
 FOR selected IN
  SELECT s.reference_catalog_id FROM trading.instrument_side_schedules m JOIN trading.swap_fixed_sides s
   ON(s.catalog_id,s.id)=(m.catalog_id,m.swap_fixed_side_id) WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)
  UNION SELECT s.reference_catalog_id FROM trading.instrument_side_schedules m JOIN trading.swap_floating_sides s
   ON(s.catalog_id,s.id)=(m.catalog_id,m.swap_floating_side_id) WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)
  UNION SELECT s.reference_catalog_id FROM trading.instrument_side_schedules m JOIN trading.swap_side_periods s
   ON(s.catalog_id,s.side_id)=(m.catalog_id,m.swap_whole_term_side_id) WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)
  UNION SELECT f.reference_catalog_id FROM trading.swap_instruments i JOIN trading.swap_quantity_factors f
   ON(f.catalog_id,f.id)=(i.catalog_id,i.quantity_factor_id) WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument)
 LOOP
  IF NOT security.reference_access(selected,'ReadMarketData') THEN RAISE EXCEPTION 'No current swap metadata reference access' USING ERRCODE='42501';END IF;
 END LOOP;
END$$;
ALTER FUNCTION security.require_swap_metadata_references(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_swap_metadata_references(uuid,uuid) FROM PUBLIC;

CREATE FUNCTION security.check_swap_program_metadata_scope(catalog uuid,instrument uuid,context uuid,as_of timestamptz)
 RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE members bigint;
BEGIN
 PERFORM security.require_side_schedule_read_scope(catalog,context,as_of);
 IF context IS NOT NULL THEN PERFORM security.require_trading_history(catalog,context);END IF;
 SELECT count(*) INTO members FROM security.read_instrument_side_schedule_headers(catalog,instrument,context,as_of);
 PERFORM security.require_swap_metadata_references(catalog,instrument);
 RETURN members;
END$$;
ALTER FUNCTION security.check_swap_program_metadata_scope(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.check_swap_program_metadata_scope(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.check_swap_program_metadata_scope(uuid,uuid,uuid,timestamptz) TO openetrm_runtime,openetrm_risk_worker;

-- Swap execution needs membership sizing and quote units, never payment terms.
CREATE FUNCTION security.read_swap_program_metadata(catalog uuid,instrument uuid,context uuid,as_of timestamptz)
 RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;expected bigint;
BEGIN
 expected=security.check_swap_program_metadata_scope(catalog,instrument,context,as_of);
 SELECT jsonb_build_object('implementation',i.implementation,'discounting',i.discounting,
  'factor',CASE WHEN i.quantity_factor_id IS NULL THEN NULL ELSE
   (SELECT jsonb_build_object('id',f.id,'referenceCatalog',f.reference_catalog_id,'referenceRevision',f.reference_revision_id,
    'value',f.value::text,'numeratorQuantity',f.numerator_quantity,'denominatorQuantity',f.denominator_quantity,
    'implementation',f.implementation,'conversionImplementation',f.conversion_implementation)
    FROM trading.swap_quantity_factors f WHERE(f.catalog_id,f.id)=(catalog,i.quantity_factor_id)) END,
  'members',(SELECT jsonb_agg(jsonb_build_object('slot',m.slot,'sideId',m.side_id,
   'coefficient',CASE m.slot WHEN 0 THEN i.fixed_coefficient ELSE -i.fixed_coefficient END,
   'numerator',CASE m.slot WHEN 0 THEN '1' ELSE i.second_quantity_numerator::text END,
   'denominator',CASE m.slot WHEN 0 THEN '1' ELSE i.second_quantity_denominator::text END,
   'referenceCatalog',q.catalog_id,'referenceRevision',q.revision_id,'quote',q.code,'currency',q.currency_code,'unitQuantity',q.quantity_code,
   'periods',(SELECT jsonb_agg(jsonb_build_object('ordinal',p.ordinal,'from',p.delivery_from,'to',p.delivery_to,
     'numerator',p.n::text,'denominator',p.d::text) ORDER BY p.ordinal) FROM(
      SELECT 0 ordinal,s.delivery_from,s.delivery_to,1::numeric n,1::numeric d FROM trading.swap_fixed_sides s WHERE(s.catalog_id,s.id)=(catalog,m.swap_fixed_side_id)
      UNION ALL SELECT 0,s.delivery_from,s.delivery_to,1,1 FROM trading.swap_floating_sides s WHERE(s.catalog_id,s.id)=(catalog,m.swap_floating_side_id)
      UNION ALL SELECT p.ordinal,p.delivery_from,p.delivery_to,p.quantity_numerator,p.quantity_denominator FROM trading.swap_side_periods p WHERE(p.catalog_id,p.side_id)=(catalog,m.swap_whole_term_side_id)
   )p)) ORDER BY m.slot)
   FROM trading.instrument_side_schedules m
   CROSS JOIN LATERAL(
    SELECT s.reference_catalog_id,s.reference_revision_id,s.quote_code FROM trading.swap_fixed_sides s WHERE(s.catalog_id,s.id)=(catalog,m.swap_fixed_side_id)
    UNION SELECT s.reference_catalog_id,s.reference_revision_id,s.quote_code FROM trading.swap_floating_sides s WHERE(s.catalog_id,s.id)=(catalog,m.swap_floating_side_id)
    UNION SELECT p.reference_catalog_id,p.reference_revision_id,p.quote_code FROM trading.swap_side_periods p WHERE(p.catalog_id,p.side_id)=(catalog,m.swap_whole_term_side_id)
   ) basis
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(basis.reference_catalog_id,basis.reference_revision_id,basis.quote_code)
   WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument))) INTO result
 FROM trading.swap_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument);
 IF result IS NULL OR result->>'implementation' NOT IN('independent_side_financial_swap_v2','whole_term_scheduled_financial_swap_v1','factor_scheduled_financial_swap_v1')
 THEN RAISE EXCEPTION 'instrument_program_unsupported';END IF;
 IF expected<>2 OR jsonb_array_length(result->'members') IS DISTINCT FROM expected
 THEN RAISE EXCEPTION 'instrument_program_metadata';END IF;
 IF (result->>'implementation'='factor_scheduled_financial_swap_v1') IS DISTINCT FROM (result->'factor'<>'null'::jsonb)
 THEN RAISE EXCEPTION 'instrument_program_metadata';END IF;
 IF result->'factor'<>'null'::jsonb THEN
  IF EXISTS(SELECT 1 FROM jsonb_array_elements(result->'members') m WHERE
   (m->>'referenceCatalog',m->>'referenceRevision') IS DISTINCT FROM
   (result#>>'{factor,referenceCatalog}',result#>>'{factor,referenceRevision}'))
  THEN RAISE EXCEPTION 'instrument_program_metadata';END IF;
  result=result||jsonb_build_object('units',
   (SELECT jsonb_agg(to_jsonb(u) ORDER BY u.code) FROM ref_data.units u WHERE(u.catalog_id,u.revision_id)=((result#>>'{factor,referenceCatalog}')::uuid,(result#>>'{factor,referenceRevision}')::uuid)),
   'unit_conversions',(SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY c.from_unit_code,c.to_unit_code),'[]'::jsonb) FROM ref_data.unit_conversions c
    WHERE(c.catalog_id,c.revision_id)=((result#>>'{factor,referenceCatalog}')::uuid,(result#>>'{factor,referenceRevision}')::uuid)),
   'quantity_specifications',(SELECT jsonb_agg(to_jsonb(q) ORDER BY q.code) FROM ref_data.quantity_specifications q
    WHERE(q.catalog_id,q.revision_id)=((result#>>'{factor,referenceCatalog}')::uuid,(result#>>'{factor,referenceRevision}')::uuid)));
 END IF;
 PERFORM security.check_swap_program_metadata_scope(catalog,instrument,context,as_of);
 RETURN result;
END$$;
ALTER FUNCTION security.read_swap_program_metadata(uuid,uuid,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.read_swap_program_metadata(uuid,uuid,uuid,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.read_swap_program_metadata(uuid,uuid,uuid,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
