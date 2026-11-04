-- Independent assistant conversations, approvals and presentation preferences for
-- the explicitly enabled synthetic energy dataset. Preserve each function's
-- definition and authority apart from the exact accepted namespace list.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$
DECLARE item record; routine regprocedure; body text; definition text; actual_owner text; actual_acl text;
BEGIN
 FOR item IN SELECT * FROM (VALUES
  ('assistant.ask_turn(uuid,text,uuid,uuid,integer,text,jsonb,uuid,text,uuid,uuid,text,text,timestamp with time zone,text,bigint,text,integer,integer,integer)','0eef20f48cf10b6f744d80a477a23910281ecc7b5c9d48fd85a035614307f529'),
  ('assistant.mark_read(text,uuid,bigint)','1b7bd7cf82c1d792c77c82fb6d08d12c6e79f06aec646828a9b54ffb3993764d'),
  ('assistant.conversation(text,uuid,bigint)','662cd96ba121a0e2da7d269f6974e927f99e9b2f6e6938f488dd36fe233bb7db'),
  ('assistant.unfinished_turn(text)','7b6ed8ab5b68f3c8eb8639c6bfa79622ff85ed03b1668eb4fc8400ad4e7ca139'),
  ('assistant.conversations(text)','294f177c384dec425ecebd4ea50d05475744b9150fdb8a04fc1a5c0d51995e2d'),
  ('assistant.stop_turn(text,uuid)','1a5b4b6dbf003a4de3ca3e20f4a8153118c14729d5ae1dde1eb50a54ce25391c'),
  ('assistant.turn_stream(text,uuid,uuid,bigint,integer)','11d58682b11047cd3b125d33fa32d56ff79156f04a209cc60ad8f2264d5dc922'),
  ('assistant.curve_proposal(text,uuid,integer)','507f1a3b7d00698da83556369268f13d2f17509fc5a877aea002b455550020b2'),
  ('assistant.action_proposal(text,uuid,integer)','90164c7da60ad82bf405e7c52a139425a7ae5d8a7e902ea8490f49fa148c97ba'),
  ('presentation.save_user_preferences(text,uuid,integer,jsonb,text)','3bc6741734606ee8e18d1514ff476288222682defadf12fbccb1a3f43cc05fc2'),
  ('presentation.user_preferences(text)','5646390aa0c996324c4b5c0842541c247b7307a01c9bd45b686242ab51ba3204')
 ) AS expected(signature,fingerprint) LOOP
  routine:=to_regprocedure(item.signature);
  SELECT p.prosrc,pg_get_functiondef(p.oid),r.rolname,p.proacl::text
   INTO body,definition,actual_owner,actual_acl
   FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner WHERE p.oid=routine;
  IF routine IS NULL OR encode(sha256(convert_to(definition,'UTF8')),'hex') IS DISTINCT FROM item.fingerprint
   OR actual_owner IS DISTINCT FROM 'openetrm_security_owner'
   OR actual_acl IS DISTINCT FROM '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'
   OR position('IN(''api'',''exchange'')' IN body)=0
  THEN RAISE EXCEPTION 'synthetic_namespace_upgrade_mismatch: %',item.signature USING ERRCODE='55000',DETAIL=format('body=%s definition=%s owner=%s acl=%s',encode(sha256(convert_to(body,'UTF8')),'hex'),encode(sha256(convert_to(definition,'UTF8')),'hex'),actual_owner,actual_acl);END IF;
  EXECUTE replace(definition,'IN(''api'',''exchange'')','IN(''api'',''exchange'',''synthetic'')');
 END LOOP;
 FOR item IN SELECT * FROM (VALUES
  ('assistant.conversations','conversations_scope_check','scope'),
  ('presentation.user_preference_versions','user_preference_versions_namespace_check','namespace')
 ) AS expected(relation,name,attribute) LOOP
  SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
   WHERE conrelid=item.relation::regclass AND conname=item.name AND contype='c';
  IF definition IS DISTINCT FROM format('CHECK ((%s = ANY (ARRAY[''api''::text, ''exchange''::text])))',item.attribute)
  THEN RAISE EXCEPTION 'synthetic_namespace_constraint_mismatch: %',item.relation USING ERRCODE='55000';END IF;
  EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I, ADD CONSTRAINT %I CHECK (%I IN (''api'',''exchange'',''synthetic''))',item.relation,item.name,item.name,item.attribute);
 END LOOP;
END $upgrade$;
COMMIT;
