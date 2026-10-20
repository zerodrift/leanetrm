-- Page-assistant models and their prices (docs/ai-assistant.md §5.1, §5.3).
-- No model or price is seeded. A model is a provider's vendor identifier with an audited
-- display name; its capability versions arrive with qualification (§12 step 4), their
-- first consumer. A price revision is immutable and effective-dated: a rate set in an
-- exact currency reference, or a withdrawal that prices nothing. Revisions append, and
-- the highest sequence in force at an instant governs it. Every definer this migration
-- creates or replaces renders instants in UTC, so its stored requests and outcomes never
-- depend on the caller's time zone. Like credential replacement (0045), each command
-- rechecks the actor's current authority after its principal and model row waits, before
-- it replays an earlier outcome, and after its writes.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority helpers these operations rely on and the definition this migration replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.actor()','57932921c06350d28515175f5af6dc561fad562cfcaa564033b94f6af0b538f5',false),
  ('assistant.provider_directory()','374fbbe82aa525c5237d05fced111d167ce287a4fdbaebaa4e2422721cf53450',true),
  ('security.reference_access(uuid,text)','697d1e75ddba8a9a7fbdfa948951d3c6760e445f6f681ba307c1c937568c0828',true),
  ('security.reference_version_visible(uuid,uuid)','28471f04e0812cefddb0c188010db6952ceeae4efd880ddb33e2b2b0d32a3b82',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'model_catalog: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- A vendor's model identifier (§6.1) and an administrator-entered display name.
CREATE FUNCTION assistant.is_model_identifier(identifier text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT identifier~'^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$' $$;
CREATE FUNCTION assistant.is_display_name(name text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT name=btrim(name) AND length(name) BETWEEN 1 AND 80 AND name!~'[[:cntrl:]]' $$;
CREATE TABLE assistant.models(
 id uuid PRIMARY KEY,tenant_id uuid NOT NULL,workspace_id uuid NOT NULL,provider_id uuid NOT NULL,
 identifier text NOT NULL CHECK(assistant.is_model_identifier(identifier)),
 display_name text NOT NULL CHECK(assistant.is_display_name(display_name)),
 -- Advances with each audited change to this row (today, a rename); capability versions are separate.
 version integer NOT NULL CHECK(version>0),
 UNIQUE(provider_id,identifier),UNIQUE(id,provider_id,tenant_id,workspace_id),
 FOREIGN KEY(provider_id,tenant_id,workspace_id) REFERENCES assistant.providers(id,tenant_id,workspace_id)
);
CREATE INDEX models_scope ON assistant.models(tenant_id,workspace_id);
-- A rate per million tokens: exact, below 10^12, at most 12 fractional digits.
CREATE FUNCTION assistant.is_rate(rate numeric) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT rate>=0 AND rate<1e12 AND scale(rate)<=12 $$;
CREATE DOMAIN assistant.token_rate AS numeric CHECK(assistant.is_rate(VALUE));
CREATE TABLE assistant.price_revisions(
 model_id uuid NOT NULL REFERENCES assistant.models(id),sequence integer NOT NULL CHECK(sequence>0),kind text NOT NULL,
 currency_catalog_id uuid,currency_revision_id uuid,currency_code ref_data.code,
 -- In a rate set, NULL is "not offered": tokens in that dimension store no cost.
 uncached_input assistant.token_rate,cache_read assistant.token_rate,cache_write assistant.token_rate,output assistant.token_rate,
 -- A positive token count below 10^12, exact in any JSON reader.
 tier_threshold bigint CHECK(tier_threshold BETWEEN 1 AND 999999999999),
 tier_uncached_input assistant.token_rate,tier_cache_read assistant.token_rate,tier_cache_write assistant.token_rate,tier_output assistant.token_rate,
 effective_from ref_data.instant NOT NULL,effective_to ref_data.instant,
 recorded_by uuid NOT NULL REFERENCES security.principals(id),recorded_at ref_data.instant NOT NULL,
 PRIMARY KEY(model_id,sequence),CHECK(effective_to>effective_from),
 FOREIGN KEY(currency_catalog_id,currency_revision_id,currency_code) REFERENCES ref_data.currencies(catalog_id,revision_id,code),
 -- A rate set offers at least uncached input and output in an exact currency; a withdrawal
 -- names neither, so calls starting where it is in force are unpriced.
 CHECK(kind='rates' AND num_nulls(currency_catalog_id,currency_revision_id,currency_code,uncached_input,output)=0
  OR kind='withdrawal' AND num_nonnulls(currency_catalog_id,currency_revision_id,currency_code,uncached_input,cache_read,cache_write,output,tier_threshold)=0),
 -- A tier is a threshold with a second set offering exactly the dimensions the first offers.
 CHECK(CASE WHEN tier_threshold IS NULL THEN num_nonnulls(tier_uncached_input,tier_cache_read,tier_cache_write,tier_output)=0
  ELSE (uncached_input IS NULL,cache_read IS NULL,cache_write IS NULL,output IS NULL)
   =(tier_uncached_input IS NULL,tier_cache_read IS NULL,tier_cache_write IS NULL,tier_output IS NULL) END)
);
ALTER TABLE assistant.administration_audit ADD COLUMN model_id uuid,
 -- A model change names its model and that model's own provider, and no key; a key
 -- replacement names no model.
 ADD CONSTRAINT administration_audit_model FOREIGN KEY(model_id,provider_id,tenant_id,workspace_id)
  REFERENCES assistant.models(id,provider_id,tenant_id,workspace_id),
 ADD CONSTRAINT administration_audit_model_change CHECK((action IN('add_model','rename_model','add_price','withdraw_price'))=(model_id IS NOT NULL)
  AND(model_id IS NULL OR provider_id IS NOT NULL AND key_digest IS NULL));
CREATE TRIGGER immutable_price BEFORE UPDATE OR DELETE ON assistant.price_revisions FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE FUNCTION assistant.guard_model() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR(NEW.id,NEW.tenant_id,NEW.workspace_id,NEW.provider_id,NEW.identifier) IS DISTINCT FROM(OLD.id,OLD.tenant_id,OLD.workspace_id,OLD.provider_id,OLD.identifier)
  OR NEW.version<>OLD.version+1 THEN
  RAISE EXCEPTION 'A model keeps its identity and advances one version at a time' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER guard_model BEFORE UPDATE OR DELETE ON assistant.models FOR EACH ROW EXECUTE FUNCTION assistant.guard_model();

-- The revision in force for a call starting at `at`: the highest append sequence whose
-- [effective_from, effective_to) contains it. A withdrawal in force prices nothing, so
-- such a call is unpriced. Step 4 opens calls with it.
CREATE FUNCTION assistant.price_in_force(model uuid,at timestamptz) RETURNS assistant.price_revisions
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT * FROM assistant.price_revisions r WHERE r.model_id=model AND r.effective_from<=at AND(r.effective_to IS NULL OR at<r.effective_to)
 ORDER BY r.sequence DESC LIMIT 1;
$$;
-- Pricing from `since` on, as half-open segments. The revision in force changes only at a
-- revision boundary, so evaluating `since` and each later boundary is exact.
CREATE FUNCTION assistant.price_schedule(model uuid,since timestamptz) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 WITH boundaries AS(
  SELECT since AS instant UNION
  SELECT b.instant FROM assistant.price_revisions r,LATERAL(VALUES(r.effective_from),(r.effective_to))b(instant)
  WHERE r.model_id=model AND b.instant>since),
 pins AS(SELECT b.instant,r.sequence,coalesce(r.kind='rates',false) AS priced FROM boundaries b,LATERAL assistant.price_in_force(model,b.instant) r),
 changes AS(SELECT p.*,lag(p.sequence) OVER(ORDER BY p.instant) AS previous,row_number() OVER(ORDER BY p.instant) AS n FROM pins p),
 segments AS(SELECT instant,lead(instant) OVER(ORDER BY instant) AS until,sequence,priced FROM changes WHERE n=1 OR sequence IS DISTINCT FROM previous)
 SELECT jsonb_agg(jsonb_build_object('from',instant,'to',until,'sequence',sequence,'priced',priced) ORDER BY instant) FROM segments;
$$;
CREATE FUNCTION assistant.rate_set(uncached_input numeric,cache_read numeric,cache_write numeric,output numeric) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('uncachedInput',trim_scale(uncached_input)::text,'cacheRead',trim_scale(cache_read)::text,
  'cacheWrite',trim_scale(cache_write)::text,'output',trim_scale(output)::text);
$$;
CREATE FUNCTION assistant.model_state(model uuid) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('id',m.id,'vendor',p.vendor,'identifier',m.identifier,'displayName',m.display_name,'version',m.version,
  'priceSequence',(SELECT count(*) FROM assistant.price_revisions r WHERE r.model_id=m.id))
 FROM assistant.models m JOIN assistant.providers p ON p.id=m.provider_id WHERE m.id=model;
$$;
CREATE FUNCTION assistant.price_state(r assistant.price_revisions) RETURNS jsonb
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('sequence',r.sequence,'kind',r.kind,
  'currency',CASE WHEN r.kind='rates' THEN jsonb_build_object('catalogId',r.currency_catalog_id,'revisionId',r.currency_revision_id,'code',r.currency_code) END,
  'rates',CASE WHEN r.kind='rates' THEN assistant.rate_set(r.uncached_input,r.cache_read,r.cache_write,r.output) END,
  'tier',CASE WHEN r.tier_threshold IS NOT NULL THEN jsonb_build_object('threshold',r.tier_threshold,
   'rates',assistant.rate_set(r.tier_uncached_input,r.tier_cache_read,r.tier_cache_write,r.tier_output)) END,
  'effectiveFrom',r.effective_from,'effectiveTo',r.effective_to,'recordedAt',r.recorded_at,
  'recordedBy',(SELECT w.display_name FROM assistant.models m
   JOIN security.workspace_memberships w ON(w.tenant_id,w.workspace_id,w.principal_id)=(m.tenant_id,m.workspace_id,r.recorded_by) WHERE m.id=r.model_id));
$$;
-- Runs one actor's commands in order, so a retry that overlaps its original waits for it.
-- After that wait and the lookup, which can wait too, it rechecks the actor's authority, then
-- returns the original outcome of an identical request by the same actor, or null for a new
-- operation; any other reuse of the request identity conflicts.
CREATE FUNCTION assistant.replayed(c security.request_contexts,operation uuid,request jsonb) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE previous assistant.administration_audit;
BEGIN
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000' THEN RAISE EXCEPTION 'model_admin_input';END IF;
 PERFORM 1 FROM security.principals WHERE id=c.actor_id FOR NO KEY UPDATE;
 SELECT * INTO previous FROM assistant.administration_audit a WHERE(a.tenant_id,a.workspace_id,a.operation_id)=(c.tenant_id,c.workspace_id,operation);
 -- Assignment preserves FOUND.
 c:=assistant.actor();
 IF NOT FOUND THEN RETURN NULL;END IF;
 IF previous.actor_id<>c.actor_id OR previous.request<>request THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 RETURN previous.after_state;
END $$;
-- Whether the actor currently reads this currency row of a sealed reference revision (D131).
CREATE FUNCTION assistant.currency_readable(currency_catalog uuid,currency_revision uuid,currency_code text) RETURNS boolean
LANGUAGE sql VOLATILE SET search_path=pg_catalog AS $$
 SELECT security.reference_access(currency_catalog,'ReadMarketData') IS TRUE AND security.reference_version_visible(currency_catalog,currency_revision) IS TRUE
  AND EXISTS(SELECT 1 FROM ref_data.revisions v JOIN ref_data.currencies k ON(k.catalog_id,k.revision_id)=(v.catalog_id,v.id)
   WHERE(v.catalog_id,v.id,k.code)=(currency_catalog,currency_revision,currency_code) AND v.state='sealed');
$$;
-- Appends the model's next price revision for the acting administrator: a rate set for Add
-- price, or a withdrawal for Withdraw price.
CREATE FUNCTION assistant.append_price(kind text,operation uuid,model uuid,expected_sequence integer,
 currency_catalog uuid,currency_revision uuid,currency_code text,uncached_input numeric,cache_read numeric,cache_write numeric,output numeric,
 tier_threshold bigint,tier_uncached_input numeric,tier_cache_read numeric,tier_cache_write numeric,tier_output numeric,
 effective_from timestamptz,effective_to timestamptz) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
DECLARE c security.request_contexts:=assistant.actor();m assistant.models;vendor assistant.vendor;r assistant.price_revisions;replay jsonb;before jsonb;after jsonb;
 action text:=CASE kind WHEN 'rates' THEN 'add_price' WHEN 'withdrawal' THEN 'withdraw_price' END;
 request jsonb:=jsonb_build_object('action',action,'model',model,'expectedSequence',expected_sequence,
  'currency',CASE WHEN kind='rates' THEN jsonb_build_object('catalogId',currency_catalog,'revisionId',currency_revision,'code',currency_code) END,
  'rates',CASE WHEN kind='rates' THEN assistant.rate_set(uncached_input,cache_read,cache_write,output) END,
  'tier',CASE WHEN num_nonnulls(tier_threshold,tier_uncached_input,tier_cache_read,tier_cache_write,tier_output)>0 THEN jsonb_build_object('threshold',tier_threshold,
   'rates',assistant.rate_set(tier_uncached_input,tier_cache_read,tier_cache_write,tier_output)) END,
  'effectiveFrom',effective_from,'effectiveTo',effective_to);
BEGIN
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 IF model IS NULL OR expected_sequence IS NULL OR expected_sequence<0 OR effective_from IS NULL OR effective_to<=effective_from
  OR kind='rates' AND(currency_catalog IS NULL OR currency_revision IS NULL OR currency_code IS NULL OR uncached_input IS NULL OR output IS NULL
   OR(SELECT bool_and(assistant.is_rate(v)) FROM unnest(ARRAY[uncached_input,cache_read,cache_write,output,
    tier_uncached_input,tier_cache_read,tier_cache_write,tier_output]) v) IS NOT TRUE
   OR(CASE WHEN tier_threshold IS NULL THEN num_nonnulls(tier_uncached_input,tier_cache_read,tier_cache_write,tier_output)>0
    ELSE tier_threshold NOT BETWEEN 1 AND 999999999999 OR(uncached_input IS NULL,cache_read IS NULL,cache_write IS NULL,output IS NULL)
     IS DISTINCT FROM(tier_uncached_input IS NULL,tier_cache_read IS NULL,tier_cache_write IS NULL,tier_output IS NULL) END))
 THEN RAISE EXCEPTION 'model_admin_input';END IF;
 BEGIN
  PERFORM effective_from::ref_data.instant,effective_to::ref_data.instant;
 EXCEPTION WHEN check_violation THEN RAISE EXCEPTION 'model_admin_input';
 END;
 SELECT * INTO m FROM assistant.models x WHERE x.id=append_price.model AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR UPDATE;
 -- The row wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.actor();
 IF NOT FOUND THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 SELECT p.vendor INTO vendor FROM assistant.providers p WHERE p.id=m.provider_id;
 -- Only the Messages API (§6.1) caches explicitly; on every other protocol cached reads occur on their own and need a rate.
 IF kind='rates' AND vendor<>'anthropic' AND cache_read IS NULL THEN RAISE EXCEPTION 'model_admin_input';END IF;
 IF(SELECT count(*) FROM assistant.price_revisions x WHERE x.model_id=m.id)<>expected_sequence THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 IF kind='rates' AND NOT assistant.currency_readable(currency_catalog,currency_revision,currency_code) THEN RAISE EXCEPTION 'currency_unavailable';END IF;
 before=assistant.model_state(m.id);
 INSERT INTO assistant.price_revisions VALUES(m.id,expected_sequence+1,kind,currency_catalog,currency_revision,currency_code,
  trim_scale(uncached_input),trim_scale(cache_read),trim_scale(cache_write),trim_scale(output),tier_threshold,
  trim_scale(tier_uncached_input),trim_scale(tier_cache_read),trim_scale(tier_cache_write),trim_scale(tier_output),
  effective_from,effective_to,c.actor_id,clock_timestamp()) RETURNING * INTO r;
 after=jsonb_build_object('model',assistant.model_state(m.id),'price',assistant.price_state(r));
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at,model_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,action,m.provider_id,request,before,after,r.recorded_at,m.id);
 -- Storage or foreign-key waits can also outlive the authority and currency read checked above.
 PERFORM assistant.actor();
 IF kind='rates' AND NOT assistant.currency_readable(currency_catalog,currency_revision,currency_code) THEN RAISE EXCEPTION 'currency_unavailable';END IF;
 RETURN after;
END $$;

-- Provider administration lists provider changes only; model changes have their own tab.
CREATE OR REPLACE FUNCTION assistant.provider_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();
BEGIN
 RETURN jsonb_build_object(
  'providers',coalesce((SELECT jsonb_agg(assistant.provider_state(p.id) ORDER BY p.vendor) FROM assistant.providers p WHERE(p.tenant_id,p.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor,'action',a.action,'vendor',a.vendor) ORDER BY a.recorded_at DESC,a.operation_id)
   FROM(SELECT a.operation_id,a.recorded_at,a.action,m.display_name actor,p.vendor FROM assistant.administration_audit a
    JOIN assistant.providers p ON p.id=a.provider_id
    JOIN security.workspace_memberships m ON(m.tenant_id,m.workspace_id,m.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
    WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) AND a.model_id IS NULL ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb));
END $$;
CREATE FUNCTION assistant.model_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();n timestamptz:=clock_timestamp();
BEGIN
 RETURN jsonb_build_object('asOf',n,
  'models',coalesce((SELECT jsonb_agg(assistant.model_state(m.id)||jsonb_build_object(
    'prices',coalesce((SELECT jsonb_agg(assistant.price_state(r) ORDER BY r.sequence) FROM assistant.price_revisions r WHERE r.model_id=m.id),'[]'::jsonb),
    'schedule',assistant.price_schedule(m.id,n)) ORDER BY p.vendor,m.identifier)
   FROM assistant.models m JOIN assistant.providers p ON p.id=m.provider_id WHERE(m.tenant_id,m.workspace_id)=(c.tenant_id,c.workspace_id)),'[]'::jsonb),
  'audit',coalesce((SELECT jsonb_agg(jsonb_build_object('operationId',a.operation_id,'at',a.recorded_at,'actor',a.actor,'action',a.action,
    'vendor',a.vendor,'identifier',a.identifier) ORDER BY a.recorded_at DESC,a.operation_id)
   FROM(SELECT a.operation_id,a.recorded_at,a.action,w.display_name actor,p.vendor,m.identifier FROM assistant.administration_audit a
    JOIN assistant.models m ON m.id=a.model_id JOIN assistant.providers p ON p.id=m.provider_id
    JOIN security.workspace_memberships w ON(w.tenant_id,w.workspace_id,w.principal_id)=(a.tenant_id,a.workspace_id,a.actor_id)
    WHERE(a.tenant_id,a.workspace_id)=(c.tenant_id,c.workspace_id) ORDER BY a.recorded_at DESC,a.operation_id LIMIT 100)a),'[]'::jsonb));
END $$;
CREATE FUNCTION assistant.add_model(operation uuid,vendor assistant.vendor,identifier text,display_name text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();provider uuid;model uuid:=gen_random_uuid();replay jsonb;after jsonb;
 request jsonb:=jsonb_build_object('action','add_model','vendor',vendor,'identifier',identifier,'displayName',display_name);
BEGIN
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 IF vendor IS NULL OR identifier IS NULL THEN RAISE EXCEPTION 'model_admin_input';END IF;
 IF NOT assistant.is_model_identifier(identifier) THEN RAISE EXCEPTION 'model_identifier_invalid';END IF;
 IF assistant.is_display_name(display_name) IS NOT TRUE THEN RAISE EXCEPTION 'model_admin_input';END IF;
 -- A vendor's provider exists once anything is administered for it, here without a credential.
 INSERT INTO assistant.providers(id,tenant_id,workspace_id,vendor,version) VALUES(gen_random_uuid(),c.tenant_id,c.workspace_id,vendor,1) ON CONFLICT DO NOTHING;
 SELECT p.id INTO provider FROM assistant.providers p WHERE(p.tenant_id,p.workspace_id,p.vendor)=(c.tenant_id,c.workspace_id,add_model.vendor);
 IF EXISTS(SELECT 1 FROM assistant.models m WHERE m.provider_id=provider AND m.identifier=add_model.identifier) THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 INSERT INTO assistant.models VALUES(model,c.tenant_id,c.workspace_id,provider,identifier,display_name,1);
 after=assistant.model_state(model);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at,model_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'add_model',provider,request,NULL,after,clock_timestamp(),model);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
CREATE FUNCTION assistant.rename_model(operation uuid,model uuid,expected_version integer,display_name text) RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();m assistant.models;replay jsonb;before jsonb;after jsonb;
 request jsonb:=jsonb_build_object('action','rename_model','model',model,'expectedVersion',expected_version,'displayName',display_name);
BEGIN
 replay=assistant.replayed(c,operation,request);
 IF replay IS NOT NULL THEN RETURN replay;END IF;
 IF model IS NULL OR expected_version IS NULL OR expected_version<1 OR assistant.is_display_name(display_name) IS NOT TRUE THEN
  RAISE EXCEPTION 'model_admin_input';
 END IF;
 SELECT * INTO m FROM assistant.models x WHERE x.id=rename_model.model AND(x.tenant_id,x.workspace_id)=(c.tenant_id,c.workspace_id) FOR UPDATE;
 -- The row wait can outlive the authority; assignment preserves FOUND.
 c:=assistant.actor();
 IF NOT FOUND OR m.version<>expected_version THEN RAISE EXCEPTION 'model_admin_conflict';END IF;
 before=assistant.model_state(m.id);
 UPDATE assistant.models SET display_name=rename_model.display_name,version=m.version+1 WHERE id=m.id;
 after=assistant.model_state(m.id);
 INSERT INTO assistant.administration_audit(tenant_id,workspace_id,operation_id,actor_id,action,provider_id,request,before_state,after_state,recorded_at,model_id)
  VALUES(c.tenant_id,c.workspace_id,operation,c.actor_id,'rename_model',m.provider_id,request,before,after,clock_timestamp(),m.id);
 -- Storage or foreign-key waits can also outlive the authority checked above.
 PERFORM assistant.actor();
 RETURN after;
END $$;
CREATE FUNCTION assistant.add_price(operation uuid,model uuid,expected_sequence integer,currency_catalog uuid,currency_revision uuid,currency_code text,
 uncached_input numeric,cache_read numeric,cache_write numeric,output numeric,
 tier_threshold bigint,tier_uncached_input numeric,tier_cache_read numeric,tier_cache_write numeric,tier_output numeric,
 effective_from timestamptz,effective_to timestamptz) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT assistant.append_price('rates',operation,model,expected_sequence,currency_catalog,currency_revision,currency_code,
  uncached_input,cache_read,cache_write,output,tier_threshold,tier_uncached_input,tier_cache_read,tier_cache_write,tier_output,effective_from,effective_to);
$$;
-- A withdrawal names no currency, so it needs no market-data access.
CREATE FUNCTION assistant.withdraw_price(operation uuid,model uuid,expected_sequence integer,effective_from timestamptz,effective_to timestamptz) RETURNS jsonb
LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
 SELECT assistant.append_price('withdrawal',operation,model,expected_sequence,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
  effective_from,effective_to);
$$;

DO $$DECLARE t record;f record;BEGIN
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='assistant' AND tablename IN('models','price_revisions') LOOP
  EXECUTE format('ALTER TABLE assistant.%I OWNER TO openetrm_security_owner',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I ENABLE ROW LEVEL SECURITY',t.tablename);
  EXECUTE format('ALTER TABLE assistant.%I FORCE ROW LEVEL SECURITY',t.tablename);
 END LOOP;
 FOR f IN SELECT p.oid::regprocedure signature FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='assistant' LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f.signature);
  EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.signature);
 END LOOP;
END $$;
ALTER DOMAIN assistant.token_rate OWNER TO openetrm_security_owner;
GRANT EXECUTE ON FUNCTION assistant.model_directory(),assistant.add_model(uuid,assistant.vendor,text,text),assistant.rename_model(uuid,uuid,integer,text),
 assistant.add_price(uuid,uuid,integer,uuid,uuid,text,numeric,numeric,numeric,numeric,bigint,numeric,numeric,numeric,numeric,timestamptz,timestamptz),
 assistant.withdraw_price(uuid,uuid,integer,timestamptz,timestamptz) TO openetrm_runtime;
COMMIT;
