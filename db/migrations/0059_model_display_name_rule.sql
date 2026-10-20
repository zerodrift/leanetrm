-- Page-assistant model display names follow the character rule of vendor terms text (0058;
-- docs/ai-assistant.md §6.3): 1 to 80 characters with no space at either end and no C0 or C1
-- control, line or paragraph separator, bidirectional embedding, override or isolate, or
-- zero-width no-break space. 0051 trimmed only ASCII spaces and refused only `[[:cntrl:]]`, the
-- C0 and C1 controls, so a name could begin with a no-break space or carry a right-to-left
-- override or a line separator. One helper now lists those code points for both rules rather
-- than naming a locale's character class, and terms text keeps its meaning. The replaced
-- definitions keep their signatures, owners and grants, and the migration first verifies them.
-- A stored display name that breaks the rule stops the migration, which never renames a model.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the definitions this migration replaces. The stored-name check below must see every name
-- committed while this waits for the epoch, which a snapshot taken under a stricter isolation
-- level would miss.
DO $$
DECLARE helper record;
BEGIN
 IF current_setting('transaction_isolation')<>'read committed' THEN
  RAISE EXCEPTION 'model_display_name: requires read committed isolation' USING ERRCODE='25000';
 END IF;
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.is_display_name(text)','36315aaf07a69748758067f5095a1c2e996029925870851e5a51c7f161018595'),
  ('assistant.is_terms_text(text)','8be389ee47a2f5f73f342ae9fe5a83ce42f746d146bc1d4211b16c2e245579f2')
 )v(signature,hash) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[] AND cardinality(p.proacl)=1)
  THEN RAISE EXCEPTION 'model_display_name: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- Text an administrator enters: no C0 or C1 control, line or paragraph separator, bidirectional
-- embedding, override or isolate, or zero-width no-break space, and no space at either end. The
-- classes list code points rather than naming a locale's character classes, the escapes read the
-- same whatever the caller's standard_conforming_strings, and each rule using it bounds the length.
CREATE FUNCTION assistant.is_plain_text(t text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog SET standard_conforming_strings=on AS $$ SELECT t!~'[\x01-\x1f\x7f-\x9f\u2028\u2029\u202a-\u202e\u2066-\u2069\ufeff]'
 AND t!~'^[ \u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]|[ \u00a0\u1680\u2000-\u200a\u202f\u205f\u3000]$' $$;
ALTER FUNCTION assistant.is_plain_text(text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.is_plain_text(text) FROM PUBLIC;
-- A model's display name, and text recorded from a vendor's terms.
CREATE OR REPLACE FUNCTION assistant.is_display_name(name text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT length(name) BETWEEN 1 AND 80 AND assistant.is_plain_text(name) $$;
CREATE OR REPLACE FUNCTION assistant.is_terms_text(t text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT length(t) BETWEEN 1 AND 1000 AND assistant.is_plain_text(t) $$;
-- A replaced function does not recheck the rows its constraint admitted, so this does. A stored
-- display name that breaks the rule stops the migration: renaming a model is an administrator's
-- audited change, never a migration's. With row security off, a role subject to its policies
-- fails here rather than miss a row.
SET LOCAL row_security=off;
DO $$
DECLARE refused text;
BEGIN
 -- Each model by its identity, vendor and identifier, which are ASCII; never by the name itself.
 SELECT string_agg(format('%s (%s %s)',m.id,p.vendor,m.identifier),', ' ORDER BY m.id) INTO refused
  FROM assistant.models m JOIN assistant.providers p ON p.id=m.provider_id WHERE assistant.is_display_name(m.display_name) IS NOT TRUE;
 IF refused IS NOT NULL THEN
  RAISE EXCEPTION 'model_display_name: stored display names break the rule' USING ERRCODE='23514',DETAIL='Models '||refused||'.',
   HINT='Rename each through Models & prices to 1 to 80 characters with no space at either end and no control, separator or '
    ||'bidirectional formatting character, then apply this migration again.';
 END IF;
END$$;
COMMIT;
