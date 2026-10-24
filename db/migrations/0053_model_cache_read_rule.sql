-- Page-assistant pricing states its cache-read rule (docs/ai-assistant.md §5.3). One
-- helper now decides whether a rate set must price cache reads: Add price applies it, and
-- the Models & prices directory states it for each model, so no reader restates the rule.
-- Nothing else changes; the replaced definitions keep 0051's checks, owners and grants.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
-- The pinned definitions print the same under any caller's search path.
SET LOCAL search_path=pg_catalog;
-- Pin the authority helpers these operations rely on and the definitions this migration replaces.
DO $$
DECLARE helper record;
BEGIN
 PERFORM 1 FROM security.epoch WHERE id=true FOR UPDATE;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR helper IN SELECT * FROM(VALUES
  ('assistant.actor()','57932921c06350d28515175f5af6dc561fad562cfcaa564033b94f6af0b538f5',false),
  ('assistant.replayed(security.request_contexts,uuid,jsonb)','848b43fd8f72b0787471d91379f5b9f570daa271fcc669459a04d73f0fb54efd',false),
  ('assistant.currency_readable(uuid,uuid,text)','d97917f7ad43589896dfe2761f48f7887ad47ee1613218e41621097b04956c5a',false),
  ('assistant.append_price(text,uuid,uuid,integer,uuid,uuid,text,numeric,numeric,numeric,numeric,bigint,numeric,numeric,numeric,numeric,timestamp with time zone,timestamp with time zone)','c2dabd0753e87a38a654d4dc57ce37498bb3532299e7404cfe0389e82bbdbb1a',false),
  ('assistant.model_directory()','3a2512d3b9f0c04bc5323c62c421979fe0dada1446c2701669cb9cee326a8fbe',true)
 )v(signature,hash,runtime_execute) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p WHERE p.oid=to_regprocedure(helper.signature)
   AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash
   AND p.proacl @> ARRAY['openetrm_security_owner=X/openetrm_security_owner']::aclitem[]
   AND cardinality(p.proacl)=CASE WHEN helper.runtime_execute THEN 2 ELSE 1 END
   AND(NOT helper.runtime_execute OR p.proacl @> ARRAY['openetrm_runtime=X/openetrm_security_owner']::aclitem[]))
  THEN RAISE EXCEPTION 'model_cache_read: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
END$$;
-- Only the Messages API (§6.1) caches explicitly; on every other protocol cached reads
-- occur on their own, so a rate set must price them.
CREATE FUNCTION assistant.cache_read_required(vendor assistant.vendor) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path=pg_catalog AS $$ SELECT vendor<>'anthropic' $$;
ALTER FUNCTION assistant.cache_read_required(assistant.vendor) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION assistant.cache_read_required(assistant.vendor) FROM PUBLIC;
-- Appends the model's next price revision for the acting administrator: a rate set for Add
-- price, or a withdrawal for Withdraw price.
CREATE OR REPLACE FUNCTION assistant.append_price(kind text,operation uuid,model uuid,expected_sequence integer,
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
 IF kind='rates' AND assistant.cache_read_required(vendor) AND cache_read IS NULL THEN RAISE EXCEPTION 'model_admin_input';END IF;
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
CREATE OR REPLACE FUNCTION assistant.model_directory() RETURNS jsonb
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE c security.request_contexts:=assistant.actor();n timestamptz:=clock_timestamp();
BEGIN
 RETURN jsonb_build_object('asOf',n,
  'models',coalesce((SELECT jsonb_agg(assistant.model_state(m.id)||jsonb_build_object('cacheReadRequired',assistant.cache_read_required(p.vendor),
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
COMMIT;
