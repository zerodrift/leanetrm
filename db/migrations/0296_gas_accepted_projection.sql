-- Consumer: explicitly selected source-known native gas quantity projection.
-- The version preserves accepted events before same-instant hypothetical use.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

DO $guard$DECLARE c pg_constraint;BEGIN
 SELECT * INTO STRICT c FROM pg_constraint WHERE conrelid='trading.gas_owned_projection_profiles'::regclass
  AND conname='gas_owned_projection_profiles_implementation_check';
 IF c.contype<>'c' OR NOT c.convalidated OR pg_get_constraintdef(c.oid) IS DISTINCT FROM
  'CHECK ((implementation = ANY (ARRAY[''gas_quantity_lifecycle_projection_v1''::text, ''gas_quantity_and_cashout_lifecycle_projection_v2''::text])))'
 THEN RAISE EXCEPTION 'gas_accepted_projection_upgrade: unexpected implementation constraint';END IF;
END$guard$;
ALTER TABLE trading.gas_owned_projection_profiles DROP CONSTRAINT gas_owned_projection_profiles_implementation_check;
ALTER TABLE trading.gas_owned_projection_profiles ADD CONSTRAINT gas_owned_projection_profiles_implementation_check
 CHECK(implementation IN('gas_quantity_lifecycle_projection_v1','gas_quantity_and_cashout_lifecycle_projection_v2','gas_accepted_quantity_lifecycle_projection_v3'));

DO $upgrade$DECLARE spec record;p pg_proc;definition text;before_text text;after_text text;expected integer;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('security.configure_gas_owned_projection(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,boolean)',
   'e01ce6469cf3795a773001d67bcee470','jsonb',ARRAY['catalog','operation','action','account','body','as_of','context','receipt_only'],
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'),
  ('trading.require_gas_cashout_projection(uuid,uuid,jsonb,uuid,timestamptz)',
   '12c184e40b5e939cda54ad6d40e05443','void',ARRAY['catalog','account','body','context','as_of'],
   '{openetrm_security_owner=X/openetrm_security_owner}')
 )v(signature,body_hash,result_type,names,acl) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=spec.signature::regprocedure;
  IF md5(p.prosrc)<>spec.body_hash OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>spec.result_type::regtype OR p.proretset
   OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.proargnames IS DISTINCT FROM spec.names
   OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.proparallel<>'u'
   OR p.provolatile<>'v' OR NOT p.prosecdef OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM spec.acl
  THEN RAISE EXCEPTION 'gas_accepted_projection_upgrade: unexpected helper %',spec.signature;END IF;
  definition=pg_get_functiondef(p.oid);
  IF spec.signature LIKE 'security.configure_gas_owned_projection(%' THEN
   before_text='body->>''implementation''=''gas_quantity_and_cashout_lifecycle_projection_v2''';
   after_text='body->>''implementation'' IN(''gas_quantity_and_cashout_lifecycle_projection_v2'',''gas_accepted_quantity_lifecycle_projection_v3'')';expected=3;
   IF(length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>expected
    THEN RAISE EXCEPTION 'gas_accepted_projection_upgrade: unexpected cashout branches';END IF;
   definition=replace(definition,before_text,after_text);
   before_text='body->>''implementation'' NOT IN(''gas_quantity_lifecycle_projection_v1'',''gas_quantity_and_cashout_lifecycle_projection_v2'')';
   after_text='body->>''implementation'' NOT IN(''gas_quantity_lifecycle_projection_v1'',''gas_quantity_and_cashout_lifecycle_projection_v2'',''gas_accepted_quantity_lifecycle_projection_v3'')';
  ELSE
   before_text='jsonb_array_length(body->''cashouts'') NOT BETWEEN 1 AND 2048';
   after_text='(jsonb_array_length(body->''cashouts'')>2048 OR (jsonb_array_length(body->''cashouts'')=0 AND body->>''implementation''<>''gas_accepted_quantity_lifecycle_projection_v3''))';
  END IF;
  IF(length(definition)-length(replace(definition,before_text,'')))/length(before_text)<>1
   THEN RAISE EXCEPTION 'gas_accepted_projection_upgrade: unexpected version marker';END IF;
  EXECUTE replace(definition,before_text,after_text);
 END LOOP;
END$upgrade$;
COMMIT;
