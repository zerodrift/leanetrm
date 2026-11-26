-- Keep owning cash on its exact binding and require a representable first gas-day start.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
SELECT 1 FROM security.epoch FOR UPDATE;

DO $guard$
DECLARE helper record;p pg_proc;
BEGIN
 FOR helper IN SELECT * FROM(VALUES
 ('security.gas_amendment_refusal(uuid,uuid,integer,uuid,timestamptz)','1c76b49f54e625dad960a24c13bfcd01a265549adaf6e7ae87005dc22ff64a2f',true),
 ('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)','b936d7d29c4624643f4478d15d930c1f87b7aed998c2c6d8e7aa36df8305c741',false),
 ('trading.save_gas_charge(uuid,uuid,uuid,integer,numeric,jsonb)','c1bd84f6316b7d4a7b451e7cc73b1d4176b5292969a42a3b526de99619b3c730',false)
 )v(signature,digest,runtime_grant) LOOP
  SELECT * INTO p FROM pg_proc WHERE oid=to_regprocedure(helper.signature);
  IF p.oid IS NULL OR encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')<>helper.digest OR
   p.proowner<>'openetrm_security_owner'::regrole OR p.proacl::text IS DISTINCT FROM (CASE WHEN helper.runtime_grant THEN
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}' ELSE '{openetrm_security_owner=X/openetrm_security_owner}' END) OR
   p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog','jit=off'] OR NOT p.prosecdef OR p.proisstrict OR p.proleakproof OR p.provolatile<>'v' OR p.proparallel<>'u'
  THEN RAISE EXCEPTION 'gas_charge_boundary_upgrade: unexpected helper %',helper.signature;END IF;
 END LOOP;
END$guard$;

-- The action and the locked writer share the original-binding rule, after the
-- predecessor has checked source authority, exact head and business date.
DO $original$
DECLARE definition text=pg_get_functiondef('security.gas_amendment_refusal(uuid,uuid,integer,uuid,timestamptz)'::regprocedure);
 marker text=' IF EXISTS(SELECT 1 FROM trading.gas_quantity_commands';
BEGIN
 IF strpos(definition,marker)=0 THEN RAISE EXCEPTION 'gas_charge_boundary_upgrade: amendment marker';END IF;
 EXECUTE replace(definition,marker,$new$
 IF EXISTS(SELECT 1 FROM trading.gas_component_terms m
  JOIN trading.gas_component_terms w ON(w.catalog_id,w.instrument_id,w.slot)=(m.catalog_id,m.instrument_id,m.clock_slot)
  JOIN trading.instrument_side_schedules s ON(s.catalog_id,s.instrument_id,s.slot)=(w.catalog_id,w.instrument_id,w.slot)
  JOIN trading.side_schedule_grids g ON(g.catalog_id,g.schedule_id,g.ordinal)=(s.catalog_id,s.schedule_id,0)
  WHERE(m.catalog_id,m.instrument_id,m.role)=(catalog,binding,'charge')
   AND g.origin+(m.delivery_from-w.delivery_from)*interval '24 hours'<=valuation)
 THEN RETURN 'gas_amendment_started';END IF;
 -- A receipt can be known before its paid instant. The writer sees every recorded
 -- fact under the shared payment lock. A pinned read observes its knowledge cutoff.
 IF EXISTS(SELECT 1 FROM trading.gas_cash_payments p
  JOIN trading.gas_component_terms m ON(m.catalog_id,m.instrument_id,m.slot)=(p.catalog_id,p.instrument_id,p.slot)
  WHERE(p.catalog_id,p.trade_id,p.instrument_id,m.role)=(catalog,trade,binding,'charge')
   AND (security.current_write_context() OR security.trading_visible(catalog,p.publication_order,timestamptz '0001-01-01 UTC')))
 THEN RETURN 'gas_amendment_payment';END IF;
$new$||marker);
END$original$;

-- Both charge bases resolve their first local day and the fixed offset before
-- the staged graph is accepted. Fresh booking may have an already earned charge.
DO $charge$
DECLARE definition text=pg_get_functiondef('trading.save_gas_charge(uuid,uuid,uuid,integer,numeric,jsonb)'::regprocedure);
 marker text=' END CASE;';
BEGIN
 IF strpos(definition,'item jsonb)')=0 OR strpos(definition,marker)=0 THEN RAISE EXCEPTION 'gas_charge_boundary_upgrade: charge marker';END IF;
 definition=replace(definition,'CREATE OR REPLACE FUNCTION','CREATE FUNCTION');
 definition=replace(definition,'item jsonb)','item jsonb, amendment_at timestamptz)');
 definition=replace(definition,'offset_interval interval;','offset_interval interval;first_start timestamptz;');
 EXECUTE replace(definition,marker,marker||$new$
 first_start=(first_day::timestamp AT TIME ZONE 'UTC')+offset_interval;
 IF first_start<timestamptz '0001-01-01 UTC' OR first_start>=timestamptz '9999-12-31 UTC' THEN RAISE EXCEPTION 'gas_day_boundary';END IF;
 IF first_start<=amendment_at THEN RAISE EXCEPTION 'gas_amendment_started';END IF;$new$);
END$charge$;
ALTER FUNCTION trading.save_gas_charge(uuid,uuid,uuid,integer,numeric,jsonb,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.save_gas_charge(uuid,uuid,uuid,integer,numeric,jsonb,timestamptz) FROM PUBLIC;

DO $writer$
DECLARE definition text=pg_get_functiondef('security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'::regprocedure);
 marker text='PERFORM trading.save_gas_charge(catalog,instrument,operation,position,base,item);';
BEGIN
 IF strpos(definition,marker)=0 THEN RAISE EXCEPTION 'gas_charge_boundary_upgrade: writer marker';END IF;
 EXECUTE replace(definition,marker,'PERFORM trading.save_gas_charge(catalog,instrument,operation,position,base,item,CASE WHEN amending THEN valuation END);');
END$writer$;
DROP FUNCTION trading.save_gas_charge(uuid,uuid,uuid,integer,numeric,jsonb);
COMMIT;
