-- A revision that changes only internal booking fields carries the commercial terms, legal authority and
-- confirmations of the revision before it. Any other change is commercial and awaits its own confirmation.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

ALTER TABLE desk.admissions ADD COLUMN carried_from int,
 ADD CHECK(carried_from BETWEEN 1 AND revision-1),
 ADD FOREIGN KEY(catalog_id,trade_id,carried_from) REFERENCES desk.admissions;

-- Fails toward a new confirmation: a term this list does not name, or a row with no instrument binding to compare,
-- makes the change commercial. A scheduled swap also states its quantity basis, unit and delivery hours in its request;
-- those stated terms are commercial even when they resolve to the same instrument.
CREATE FUNCTION desk.commercial_change(before_row jsonb,after_row jsonb) RETURNS boolean LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT NOT coalesce(after_row ? 'instrument_id' AND before_row-internal.fields=after_row-internal.fields
  AND before_row#>'{request,body,scheduled}' IS NOT DISTINCT FROM after_row#>'{request,body,scheduled}',false)
 FROM(SELECT ARRAY['revision','previous_revision','event_id','event_kind','book_id','memo','reason','request','operation_id','actor_id',
  'publication_order','effective_at'] fields)internal
$$;

CREATE FUNCTION desk.confirmed_revision(catalog uuid,trade uuid,trade_revision int) RETURNS int LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT coalesce((SELECT a.carried_from FROM desk.admissions a WHERE(a.catalog_id,a.trade_id,a.revision)=(catalog,trade,trade_revision)),trade_revision)
$$;

DO $$DECLARE target regprocedure;definition text;step text[];
BEGIN
 FOREACH step SLICE 1 IN ARRAY ARRAY[
  ['desk.admit_revision()',
   $before$row_data jsonb=to_jsonb(NEW);at_time timestamptz;$before$,
   $after$row_data jsonb=to_jsonb(NEW);at_time timestamptz;previous jsonb;prior desk.admissions;$after$],
  -- A carried revision is admitted under its predecessor's agreement and credit decision; nothing new is extended.
  -- Choosing another agreement for the operation is itself a commercial change.
  ['desk.admit_revision()',
   $before$ PERFORM pg_advisory_xact_lock(730849102);
$before$,
   $after$ PERFORM pg_advisory_xact_lock(730849102);
 IF(row_data->>'revision')::int>1 THEN
  EXECUTE format('SELECT to_jsonb(r) FROM trading.%I r WHERE(r.catalog_id,r.trade_id,r.revision)=($1,$2,$3)',TG_TABLE_NAME)
   INTO previous USING NEW.catalog_id,NEW.trade_id,(row_data->>'revision')::int-1;
  SELECT * INTO prior FROM desk.admissions a WHERE(a.catalog_id,a.trade_id,a.revision)=(NEW.catalog_id,NEW.trade_id,(row_data->>'revision')::int-1);
  IF FOUND AND NOT desk.commercial_change(previous,row_data) AND NOT EXISTS(SELECT FROM desk.booking_choices c
   WHERE(c.catalog_id,c.actor_id,c.operation_id)=(NEW.catalog_id,NEW.actor_id,NEW.operation_id) AND c.agreement_id IS DISTINCT FROM prior.agreement_id) THEN
   INSERT INTO desk.admissions VALUES(NEW.catalog_id,NEW.trade_id,(row_data->>'revision')::int,NEW.book_id,prior.product,prior.agreement_id,prior.clearance_id,
    prior.definition_revision_id,prior.executed_at,NEW.effective_at,NEW.publication_order,coalesce(prior.carried_from,prior.revision));
   RETURN NEW;
  END IF;
 END IF;
$after$],
  ['desk.record(uuid,uuid,jsonb)',
   $before$(a.catalog_id,a.trade_id,a.revision)=(catalog,trade,tr)$before$,
   $after$(a.catalog_id,a.trade_id,a.revision)=(catalog,trade,tr) AND a.carried_from IS NULL$after$],
  ['desk.directory(uuid)',
   $before$SELECT a.trade_id,a.revision,a.book_id,$before$,
   $after$SELECT a.trade_id,a.revision,a.carried_from,a.book_id,$after$],
  -- Carried revisions share their predecessor's execution instant; list a trade's revisions newest first.
  ['desk.directory(uuid)',
   $before$to_jsonb(x) ORDER BY x.executed_at DESC$before$,
   $after$to_jsonb(x) ORDER BY x.executed_at DESC,x.trade_id,x.revision DESC$after$],
  ['desk.directory(uuid)',
   $before$(c.catalog_id,c.trade_id,c.trade_revision)=(a.catalog_id,a.trade_id,a.revision)$before$,
   $after$(c.catalog_id,c.trade_id,c.trade_revision)=(a.catalog_id,a.trade_id,coalesce(a.carried_from,a.revision))$after$],
  ['desk.facility_activity(uuid,uuid,date,date,timestamptz)',
   $before$(c.catalog_id,c.trade_id,c.trade_revision)=(catalog,h.trade_id,h.revision)$before$,
   $after$(c.catalog_id,c.trade_id,c.trade_revision)=(catalog,h.trade_id,desk.confirmed_revision(catalog,h.trade_id,h.revision))$after$]]
 LOOP
  target=step[1]::regprocedure;definition=pg_get_functiondef(target);
  IF(length(definition)-length(replace(definition,step[2],'')))/length(step[2])<>1 THEN RAISE EXCEPTION 'carried_terms_predecessor: %',target;END IF;
  EXECUTE replace(definition,step[2],step[3]);
 END LOOP;
END$$;

ALTER FUNCTION desk.commercial_change(jsonb,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION desk.confirmed_revision(uuid,uuid,int) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION desk.commercial_change(jsonb,jsonb),desk.confirmed_revision(uuid,uuid,int) FROM PUBLIC;
COMMIT;
