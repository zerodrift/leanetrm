-- Read/preview admission and first outcomes survive restored read scopes.
BEGIN;
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
CREATE FUNCTION security.require_gas_owned_preview(catalog uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_owned(catalog,false,false);
 IF until_at IS NULL OR NOT isfinite(until_at) OR clock_timestamp()>=until_at THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.gas_owned_review_hash(catalog uuid,account uuid,action text,body jsonb,as_of timestamptz,
 context uuid,policy uuid,policy_version integer,automatic boolean) RETURNS text
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE source jsonb;BEGIN
 PERFORM security.require_gas_owned(catalog,false,false);
 SELECT jsonb_build_object('contextId',t.id,'businessDate',t.effective_on,'knownAt',t.known_at,'tradingThrough',x.known_through,'marketThrough',m.known_through)
  INTO STRICT source FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id) WHERE(t.catalog_id,t.id)=(catalog,context);
 RETURN encode(sha256(convert_to(jsonb_build_object('catalog',catalog,'account',account,'action',action,'selection',trading.gas_owned_fact(body),
  'asOf',as_of,'source',source,'policyId',policy,'policyRevision',policy_version,'automatic',automatic)::text,'UTF8')),'hex');
END$$;
CREATE FUNCTION security.require_gas_owned_commit(catalog uuid,until_at timestamptz,configuring boolean,requires_jobs boolean) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM security.require_gas_owned_write(catalog,configuring,until_at);
 IF requires_jobs AND security.has_permission('RunJobs') IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
END$$;
CREATE FUNCTION security.gas_owned_admission(catalog uuid,action text,body jsonb,automatic boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;configuring boolean=action IN('account','bind_policy');requires_jobs boolean=automatic;BEGIN
 IF automatic IS NULL THEN RAISE EXCEPTION 'gas_owned_input';END IF;
 until_at=least(security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'),security.authority_until('ReadGasOwnedHoldings'),security.authority_until('RecordGasOwnedHoldings'));
 IF configuring THEN until_at=least(until_at,security.authority_until('ConfigureGasOwnedHoldings'));END IF;
 IF automatic OR action='bind_policy' AND EXISTS(SELECT 1 FROM lifecycle.action_policies p
  WHERE(p.catalog_id,p.id,p.revision)=(catalog,(body->>'policyId')::uuid,(body->>'policyRevision')::integer) AND p.mode='automatic')
 THEN requires_jobs=true;until_at=least(until_at,security.authority_until('RunJobs'));END IF;
 PERFORM security.require_gas_owned_commit(catalog,until_at,configuring,requires_jobs);
 RETURN jsonb_build_object('until',until_at,'configuring',configuring,'requiresJobs',requires_jobs);
END$$;
CREATE FUNCTION security.gas_owned_command_receipt(catalog uuid,operation uuid,action text,account uuid,body jsonb,as_of timestamptz,
 context uuid,policy uuid,policy_version integer,automatic boolean) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE admission jsonb;actor uuid;prior trading.gas_owned_commands;a trading.gas_owned_accounts;canonical jsonb;BEGIN
 admission=security.gas_owned_admission(catalog,action,body,automatic);
 IF operation IS NULL OR account IS NULL OR as_of IS NULL OR NOT isfinite(as_of) OR jsonb_typeof(body) IS DISTINCT FROM 'object' OR pg_column_size(body)>16384
 THEN RAISE EXCEPTION 'gas_owned_input';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 canonical=jsonb_build_object('action',action,'accountId',account,'body',trading.gas_owned_fact(body),'asOf',as_of,'contextId',context,'policyId',policy,'policyRevision',policy_version,'automatic',automatic);
 PERFORM pg_advisory_xact_lock(hashtextextended('gas_owned:'||catalog::text||':'||operation::text,0));
 SELECT * INTO prior FROM trading.gas_owned_commands c WHERE(c.catalog_id,c.request_id)=(catalog,operation);
 IF prior.request_id IS NOT NULL THEN
  IF prior.actor_id IS DISTINCT FROM actor OR prior.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'gas_owned_conflict';END IF;
  SELECT * INTO STRICT a FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(catalog,account);
  IF security.reference_version_visible(a.reference_catalog_id,a.reference_revision_id) IS NOT TRUE OR security.market_fact_visible(catalog,a.definition_revision_id,NULL) IS NOT TRUE
  THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 END IF;
 PERFORM security.require_gas_owned_commit(catalog,(admission->>'until')::timestamptz,(admission->>'configuring')::boolean,(admission->>'requiresJobs')::boolean);
 RETURN prior.outcome;
END$$;
DO $$DECLARE f regprocedure;BEGIN
 FOREACH f IN ARRAY ARRAY['security.require_gas_owned_preview(uuid,timestamptz)'::regprocedure,
 'security.gas_owned_review_hash(uuid,uuid,text,jsonb,timestamptz,uuid,uuid,integer,boolean)'::regprocedure,
 'security.require_gas_owned_commit(uuid,timestamptz,boolean,boolean)'::regprocedure,
 'security.gas_owned_admission(uuid,text,jsonb,boolean)'::regprocedure,
 'security.gas_owned_command_receipt(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,uuid,integer,boolean)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',f);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f);
  EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO openetrm_runtime',f);
 END LOOP;
END$$;
COMMIT;
