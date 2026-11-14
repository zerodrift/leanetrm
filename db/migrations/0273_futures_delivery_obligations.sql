-- Consumer: accepted physical final margin leaves an ordinary, independently
-- owned delivery obligation. Operational title/performance has a separate store.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $upgrade$DECLARE item record;f pg_proc;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('settlement_data.determine_futures_margin(uuid,jsonb,jsonb)','1377715ddadf00b16dda0a0891d0c2a4','jsonb'),
  ('settlement_data.finish_futures_margin_run(uuid,uuid,jsonb,timestamptz)','623e25534dcd8df214abbbbd6b258140','void')
 )v(signature,body_hash,result_type) LOOP
  SELECT * INTO STRICT f FROM pg_proc WHERE oid=item.signature::regprocedure;
  IF md5(f.prosrc)<>item.body_hash OR f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.proretset
  OR f.prorettype<>item.result_type::regtype OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v'
  OR(SELECT lanname FROM pg_language WHERE oid=f.prolang)<>'plpgsql' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
  OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE')
  OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))x WHERE x.grantor<>f.proowner OR x.is_grantable
   OR x.privilege_type<>'EXECUTE' OR x.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
  THEN RAISE EXCEPTION 'futures_delivery_writer_upgrade: unexpected helper %',item.signature;END IF;
 END LOOP;
 IF to_regprocedure('settlement_data.determine_futures_margin_native(uuid,jsonb,jsonb,timestamptz)') IS NOT NULL
 THEN RAISE EXCEPTION 'futures_delivery_writer_upgrade: unexpected private producer';END IF;
END$upgrade$;
ALTER TABLE settlement_data.futures_margin_policies ADD UNIQUE(catalog_id,id,revision,delivery_profile_id,delivery_profile_revision);
CREATE TABLE settlement_data.futures_delivery_obligations(
 catalog_id uuid NOT NULL,position_id uuid NOT NULL,trade_id uuid NOT NULL,binding_id uuid NOT NULL,origin_revision integer NOT NULL,
 policy_id uuid NOT NULL,policy_revision integer NOT NULL,profile_id uuid NOT NULL,profile_revision integer NOT NULL,
 final_occurrence_id uuid NOT NULL,initial_determination_id uuid NOT NULL,
 PRIMARY KEY(catalog_id,position_id),UNIQUE(catalog_id,trade_id,binding_id),
 FOREIGN KEY(catalog_id,position_id) REFERENCES trading.gas_owned_positions(catalog_id,id),
 FOREIGN KEY(catalog_id,trade_id,binding_id,policy_id,policy_revision) REFERENCES settlement_data.futures_margin_adoptions(catalog_id,trade_id,binding_id,policy_id,policy_revision),
 FOREIGN KEY(catalog_id,trade_id,origin_revision) REFERENCES trading.trade_revisions(catalog_id,trade_id,revision),
 FOREIGN KEY(catalog_id,policy_id,policy_revision,profile_id,profile_revision)
  REFERENCES settlement_data.futures_margin_policies(catalog_id,id,revision,delivery_profile_id,delivery_profile_revision),
 FOREIGN KEY(catalog_id,policy_id,policy_revision,final_occurrence_id) REFERENCES settlement_data.futures_margin_policy_occurrences(catalog_id,policy_id,policy_revision,id),
 FOREIGN KEY(catalog_id,trade_id,binding_id,initial_determination_id) REFERENCES settlement_data.futures_margin_determinations(catalog_id,trade_id,binding_id,id)
);
ALTER TABLE settlement_data.futures_delivery_obligations OWNER TO openetrm_security_owner;
ALTER TABLE settlement_data.futures_delivery_obligations ENABLE ROW LEVEL SECURITY;
ALTER TABLE settlement_data.futures_delivery_obligations FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON settlement_data.futures_delivery_obligations TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON settlement_data.futures_delivery_obligations FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

CREATE FUNCTION settlement_data.futures_margin_delivery_authority(catalog uuid,trade uuid,binding uuid) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_margin_policies;admission jsonb;until_at timestamptz;BEGIN
 until_at=settlement_data.capture_futures_margin_authority(catalog,'AmendTrade');
 SELECT x.* INTO p FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policies x
  ON(x.catalog_id,x.id,x.revision)=(a.catalog_id,a.policy_id,a.policy_revision)
  WHERE(a.catalog_id,a.trade_id,a.binding_id)=(catalog,trade,binding);
 IF p.delivery_profile_id IS NULL THEN RETURN until_at;END IF;
 admission=security.gas_owned_admission(catalog,'physical_delivery','{}'::jsonb,false);
 PERFORM settlement_data.require_futures_margin_delivery(p);
 RETURN least(until_at,(admission->>'until')::timestamptz);
END$$;
CREATE FUNCTION settlement_data.require_futures_margin_delivery_commit(catalog uuid,trade uuid,binding uuid,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_margin_policies;BEGIN
 SELECT x.* INTO p FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policies x
  ON(x.catalog_id,x.id,x.revision)=(a.catalog_id,a.policy_id,a.policy_revision) WHERE(a.catalog_id,a.trade_id,a.binding_id)=(catalog,trade,binding);
 IF p.delivery_profile_id IS NOT NULL THEN
  PERFORM security.require_gas_owned_commit(catalog,until_at,false,false);PERFORM settlement_data.require_futures_margin_delivery(p);
 END IF;
END$$;
CREATE FUNCTION settlement_data.futures_margin_delivery_read_authority(catalog uuid,trade uuid,binding uuid,context uuid,as_of timestamptz) RETURNS timestamptz
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;p settlement_data.futures_margin_policies;BEGIN
 until_at=settlement_data.futures_margin_preview_authority(catalog);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);
 SELECT x.* INTO p FROM settlement_data.futures_margin_adoptions a JOIN settlement_data.futures_margin_policies x ON(x.catalog_id,x.id,x.revision)=(a.catalog_id,a.policy_id,a.policy_revision)
  WHERE(a.catalog_id,a.trade_id,a.binding_id)=(catalog,trade,binding) AND security.trading_visible(a.catalog_id,a.publication_order,a.effective_at);
 IF p.delivery_profile_id IS NOT NULL THEN
  until_at=least(until_at,security.authority_until('ReadGasOwnedHoldings'));PERFORM security.require_gas_owned_preview(catalog,until_at);
 END IF;RETURN until_at;
END$$;

-- Internal knowledge-only selector. Its actual/performance caller separately
-- admits the source and verifies the business instant. Select the head before
-- joining its source proof: an incomplete context must never reveal an older D.
CREATE FUNCTION settlement_data.read_futures_delivery_final(catalog uuid,holding uuid,context uuid,as_of timestamptz)
 RETURNS TABLE(determination_id uuid,source_observation_id uuid,final_at timestamptz,final_price numeric,publication_order integer,known_at timestamptz)
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE link settlement_data.futures_delivery_obligations;head settlement_data.futures_margin_determinations;
 prefix integer;market_prefix integer;cut timestamptz;BEGIN
 PERFORM security.require_gas_owned(catalog,false,false);
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(catalog,holding);
 SELECT x.known_through,m.known_through,t.known_at INTO STRICT prefix,market_prefix,cut FROM market_data.temporal_contexts t
  JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id) JOIN market_data.market_contexts m ON(m.catalog_id,m.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND security.trading_context_day_matches(catalog,context,as_of);
 SELECT d.* INTO head FROM settlement_data.futures_margin_determinations d JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(d.catalog_id,d.publication_order)
  WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=(catalog,link.trade_id,link.binding_id,link.final_occurrence_id)
   AND d.publication_order<=prefix AND k.known_from<=cut ORDER BY d.run_revision DESC LIMIT 1;
 IF head.id IS NULL THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 RETURN QUERY SELECT head.id,s.id,o.at::timestamptz,s.value::numeric,head.publication_order,k.known_from::timestamptz
  FROM market_data.observations s JOIN market_data.knowledge_events e ON(e.catalog_id,e.observation_id)=(s.catalog_id,s.id)
  JOIN market_data.market_knowledge_receipts sk ON(sk.catalog_id,sk.event_order,sk.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(head.catalog_id,head.publication_order)
  JOIN settlement_data.futures_margin_policy_occurrences o ON(o.catalog_id,o.policy_id,o.policy_revision,o.id)=(head.catalog_id,head.policy_id,head.policy_revision,head.occurrence_id)
  WHERE(s.catalog_id,s.id)=(catalog,head.source_observation_id) AND e.event_order<=market_prefix AND sk.known_from<=cut
   AND o.phase='final' AND s.role='final_contract_settlement' AND s.publication_state='final' AND s.observed_at=o.at;
 IF NOT FOUND THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
END$$;
CREATE FUNCTION settlement_data.require_futures_delivery_head(catalog uuid,holding uuid,context uuid) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE link settlement_data.futures_delivery_obligations;head settlement_data.futures_margin_determinations;BEGIN
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(catalog,holding);
 SELECT * INTO STRICT head FROM settlement_data.futures_margin_determinations d WHERE(d.catalog_id,d.trade_id,d.binding_id,d.occurrence_id)=
  (catalog,link.trade_id,link.binding_id,link.final_occurrence_id) ORDER BY d.run_revision DESC LIMIT 1;
 IF NOT EXISTS(SELECT 1 FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(catalog,head.publication_order)
  WHERE(t.catalog_id,t.id)=(catalog,context) AND head.publication_order<=x.known_through AND k.known_from<=t.known_at)
 THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
END$$;

CREATE FUNCTION settlement_data.require_futures_delivery_profile_read(catalog uuid,profile uuid,requested_revision integer,until_at timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_delivery_profiles;g trading.gas_futures_delivery_terms;BEGIN
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);PERFORM security.require_gas_owned_preview(catalog,until_at);
 SELECT * INTO STRICT p FROM settlement_data.futures_delivery_profiles x WHERE(x.catalog_id,x.id,x.revision)=(catalog,profile,requested_revision);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(catalog,profile,requested_revision);
 PERFORM settlement_data.require_futures_delivery_reference(p,g);
 PERFORM settlement_data.require_futures_margin_preview(catalog,until_at);PERFORM security.require_gas_owned_preview(catalog,until_at);
END$$;
CREATE FUNCTION settlement_data.read_futures_delivery_profile(catalog uuid,profile uuid,requested_revision integer,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;result jsonb;BEGIN
 until_at=least(settlement_data.futures_margin_preview_authority(catalog),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);PERFORM security.require_gas_owned_preview(catalog,until_at);
 result=settlement_data.futures_delivery_profile_document(catalog,profile,requested_revision,context,as_of)||jsonb_build_object('authorityUntil',until_at);
 PERFORM settlement_data.require_futures_delivery_profile_read(catalog,profile,requested_revision,until_at);RETURN result;
END$$;
CREATE FUNCTION settlement_data.read_futures_delivery_obligation(catalog uuid,trade uuid,binding uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;link settlement_data.futures_delivery_obligations;holding trading.gas_owned_positions;final_value record;result jsonb;BEGIN
 until_at=least(settlement_data.futures_margin_preview_authority(catalog),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);PERFORM security.require_gas_owned_preview(catalog,until_at);
 SELECT x.* INTO link FROM settlement_data.futures_delivery_obligations x JOIN trading.gas_owned_positions p ON(p.catalog_id,p.id)=(x.catalog_id,x.position_id)
  JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(catalog,context) JOIN trading.contexts tc ON(tc.catalog_id,tc.context_id)=(t.catalog_id,t.id)
  JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE(x.catalog_id,x.trade_id,x.binding_id)=(catalog,trade,binding) AND p.publication_order<=tc.known_through AND k.known_from<=t.known_at;
 IF link.position_id IS NULL THEN RAISE EXCEPTION 'futures_delivery_source';END IF;
 SELECT * INTO STRICT holding FROM trading.gas_owned_positions p WHERE(p.catalog_id,p.id)=(catalog,link.position_id);
 SELECT * INTO STRICT final_value FROM settlement_data.read_futures_delivery_final(catalog,link.position_id,context,as_of);
 result=settlement_data.futures_delivery_profile_document(catalog,link.profile_id,link.profile_revision,context,as_of)||
  jsonb_build_object('link',to_jsonb(link),'position',to_jsonb(holding),'final',to_jsonb(final_value),'authorityUntil',until_at);
 PERFORM settlement_data.require_futures_delivery_profile_read(catalog,link.profile_id,link.profile_revision,until_at);
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);RETURN result;
END$$;
CREATE FUNCTION settlement_data.read_futures_delivery_settings(catalog uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;prefix integer;cut timestamptz;profiles jsonb;accounts jsonb;retained_account trading.gas_owned_accounts;BEGIN
 until_at=least(settlement_data.futures_margin_preview_authority(catalog),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);PERFORM security.require_gas_owned_preview(catalog,until_at);
 SELECT x.known_through,t.known_at INTO STRICT prefix,cut FROM market_data.temporal_contexts t JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
  WHERE(t.catalog_id,t.id)=(catalog,context);
 IF(SELECT count(*) FROM settlement_data.futures_delivery_profiles p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
   WHERE p.catalog_id=catalog AND p.publication_order<=prefix AND k.known_from<=cut)>4096
 OR(SELECT count(*) FROM trading.gas_owned_accounts a JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(a.catalog_id,a.publication_order)
   WHERE a.catalog_id=catalog AND a.kind='owned_inventory' AND a.publication_order<=prefix AND k.known_from<=cut AND a.effective_at<=as_of)>4096
 THEN RAISE EXCEPTION 'futures_delivery_scope';END IF;
 SELECT coalesce(jsonb_agg(settlement_data.futures_delivery_profile_document(catalog,p.id,p.revision,context,as_of) ORDER BY p.name,p.id,p.revision),'[]'::jsonb)
  INTO profiles FROM settlement_data.futures_delivery_profiles p JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(p.catalog_id,p.publication_order)
  WHERE p.catalog_id=catalog AND p.publication_order<=prefix AND k.known_from<=cut;
 FOR retained_account IN SELECT x.* FROM trading.gas_owned_accounts x JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(x.catalog_id,x.publication_order)
  WHERE x.catalog_id=catalog AND x.kind='owned_inventory' AND x.publication_order<=prefix AND k.known_from<=cut AND x.effective_at<=as_of LOOP
  PERFORM settlement_data.require_futures_margin_source(catalog,context,as_of,retained_account.definition_revision_id);
  IF security.reference_version_visible(retained_account.reference_catalog_id,retained_account.reference_revision_id) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_denied' USING ERRCODE='42501';END IF;
 END LOOP;
 SELECT coalesce(jsonb_agg(to_jsonb(v) ORDER BY v.code,v.id),'[]'::jsonb) INTO accounts FROM(
  SELECT a.*,b.name book_name,e.name legal_entity_name,c.expiry_at mark_expiry_at,q.multiplier quote_multiplier
  FROM trading.gas_owned_accounts a JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(a.catalog_id,a.publication_order)
  JOIN trading.books b ON(b.catalog_id,b.id)=(a.catalog_id,a.book_id)
  JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=(a.catalog_id,a.legal_entity_id,a.legal_entity_revision)
  JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(a.catalog_id,a.definition_revision_id,a.mark_contract_id)
  JOIN market_data.curves m ON(m.catalog_id,m.revision_id,m.code)=(a.catalog_id,a.definition_revision_id,a.mark_curve_code)
  JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(m.reference_catalog_id,m.reference_revision_id,m.quote_code)
  WHERE a.catalog_id=catalog AND a.kind='owned_inventory' AND a.publication_order<=prefix AND k.known_from<=cut AND a.effective_at<=as_of)v;
 IF octet_length(convert_to(jsonb_build_object('profiles',profiles,'accounts',accounts)::text,'UTF8'))>16777216 THEN RAISE EXCEPTION 'futures_delivery_scope';END IF;
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);PERFORM security.require_gas_owned_preview(catalog,until_at);
 RETURN jsonb_build_object('profiles',profiles,'accounts',accounts,'authorityUntil',until_at);
END$$;
CREATE FUNCTION settlement_data.read_futures_delivery_position(catalog uuid,holding uuid,context uuid,as_of timestamptz) RETURNS jsonb
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;link settlement_data.futures_delivery_obligations;result jsonb;BEGIN
 until_at=least(settlement_data.futures_margin_preview_authority(catalog),security.authority_until('ReadGasOwnedHoldings'));
 PERFORM settlement_data.require_futures_margin_read(catalog,context,as_of,until_at);PERFORM security.require_gas_owned_preview(catalog,until_at);
 SELECT * INTO STRICT link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(catalog,holding);
 result=settlement_data.read_futures_delivery_obligation(catalog,link.trade_id,link.binding_id,context,as_of);
 PERFORM settlement_data.require_futures_delivery_profile_read(catalog,link.profile_id,link.profile_revision,until_at);
 RETURN result||jsonb_build_object('authorityUntil',least(until_at,(result->>'authorityUntil')::timestamptz));
END$$;

CREATE FUNCTION settlement_data.apply_futures_final_delivery() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p settlement_data.futures_margin_policies;o settlement_data.futures_margin_policy_occurrences;r settlement_data.futures_margin_runs;
 a settlement_data.futures_margin_adoptions;origin trading.trade_revisions;g trading.gas_futures_delivery_terms;account trading.gas_owned_accounts;
 product ref_data.futures_products;link settlement_data.futures_delivery_obligations;holding uuid;ev uuid;qty numeric;BEGIN
 SELECT * INTO STRICT o FROM settlement_data.futures_margin_policy_occurrences x WHERE(x.catalog_id,x.policy_id,x.policy_revision,x.id)=(NEW.catalog_id,NEW.policy_id,NEW.policy_revision,NEW.occurrence_id);
 IF o.phase<>'final' THEN RETURN NEW;END IF;
 SELECT * INTO STRICT p FROM settlement_data.futures_margin_policies x WHERE(x.catalog_id,x.id,x.revision)=(NEW.catalog_id,NEW.policy_id,NEW.policy_revision);
 SELECT x.* INTO STRICT product FROM market_data.contracts c JOIN ref_data.futures_series s ON(s.catalog_id,s.revision_id,s.code)=(c.reference_catalog_id,c.reference_revision_id,c.futures_series_code)
  JOIN ref_data.futures_products x ON(x.catalog_id,x.revision_id,x.code)=(s.catalog_id,s.revision_id,s.product_code)
  WHERE(c.catalog_id,c.revision_id,c.id)=(p.catalog_id,p.definition_revision_id,p.contract_id);
 IF product.settlement_method='cash' THEN RETURN NEW;END IF;
 IF p.implementation<>'dated_futures_margin_policy_v2' OR p.delivery_profile_id IS NULL THEN RAISE EXCEPTION 'futures_delivery_transition';END IF;
 SELECT * INTO STRICT r FROM settlement_data.futures_margin_runs x WHERE(x.catalog_id,x.trade_id,x.binding_id,x.revision)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id,NEW.run_revision);
 SELECT * INTO STRICT a FROM settlement_data.futures_margin_adoptions x WHERE(x.catalog_id,x.trade_id,x.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id);
 SELECT * INTO STRICT origin FROM trading.trade_revisions x WHERE(x.catalog_id,x.trade_id,x.revision)=(NEW.catalog_id,NEW.trade_id,a.origin_revision);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(p.catalog_id,p.delivery_profile_id,p.delivery_profile_revision);
 SELECT * INTO STRICT account FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(g.catalog_id,g.account_id);
 IF product.lot_basis<>'fixed_total' OR product.lot_unit_code<>product.quote_unit_code OR o.at>g.delivery_from
 OR(account.book_id,account.legal_entity_id,account.legal_entity_revision) IS DISTINCT FROM(origin.book_id,origin.legal_entity_id,origin.legal_entity_revision)
 OR NOT EXISTS(SELECT 1 FROM trading.publications x WHERE(x.catalog_id,x.publication_order,x.publication_xid)=(NEW.catalog_id,NEW.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'futures_delivery_terms';END IF;
 PERFORM security.require_gas_owned(NEW.catalog_id,true,false);
 PERFORM trading.require_gas_owned_head(NEW.catalog_id,g.account_id,r.source_context_id,r.as_of);
 SELECT * INTO link FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.trade_id,x.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id);
 IF link.position_id IS NULL THEN
  qty=origin.lots*product.contract_size;holding=gen_random_uuid();ev=gen_random_uuid();
  INSERT INTO trading.gas_owned_evidence(catalog_id,id,account_id,source_key,revision,previous_id,kind,quantity,occurred_at,evidence,actor_id,publication_order)
   VALUES(NEW.catalog_id,ev,g.account_id,'future-final-determination:'||NEW.id::text,1,NULL,'futures_final',qty,o.at,'Accepted final determination '||NEW.id::text,r.actor_id,NEW.publication_order);
  INSERT INTO trading.gas_owned_positions(catalog_id,id,account_id,kind,quantity,evidence_id,origin_policy,definition_revision_id,mark_curve_code,mark_contract_id,due_at,carrying,settlement_mode,actor_id,publication_order,effective_at)
   VALUES(NEW.catalog_id,holding,g.account_id,'delivery_obligation',qty,ev,'physical_futures_delivery_v1',account.definition_revision_id,account.mark_curve_code,account.mark_contract_id,
    g.delivery_from,g.carrying,'physical_delivery_v1',r.actor_id,NEW.publication_order,o.at);
  INSERT INTO settlement_data.futures_delivery_obligations(catalog_id,position_id,trade_id,binding_id,origin_revision,policy_id,policy_revision,profile_id,profile_revision,final_occurrence_id,initial_determination_id)
   VALUES(NEW.catalog_id,holding,NEW.trade_id,NEW.binding_id,a.origin_revision,p.id,p.revision,p.delivery_profile_id,p.delivery_profile_revision,o.id,NEW.id) RETURNING * INTO link;
 END IF;
 -- Evidence-backed performance and its principal reconciler are separate
 -- synchronous consumers of this accepted final. This transition never titles
 -- stock or guesses a performance/payment from the delivery calendar.
 RETURN NEW;
END$$;
CREATE TRIGGER physical_final AFTER INSERT ON settlement_data.futures_margin_determinations FOR EACH ROW EXECUTE FUNCTION settlement_data.apply_futures_final_delivery();

CREATE FUNCTION settlement_data.guard_futures_delivery_obligation() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d settlement_data.futures_margin_determinations;p trading.gas_owned_positions;g trading.gas_futures_delivery_terms;a settlement_data.futures_margin_adoptions;
 evidence trading.gas_owned_evidence;account trading.gas_owned_accounts;origin trading.trade_revisions;product ref_data.futures_products;o settlement_data.futures_margin_policy_occurrences;BEGIN
 SELECT * INTO STRICT d FROM settlement_data.futures_margin_determinations x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.initial_determination_id);
 SELECT * INTO STRICT p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id)=(NEW.catalog_id,NEW.position_id);
 SELECT * INTO STRICT g FROM trading.gas_futures_delivery_terms x WHERE(x.catalog_id,x.profile_id,x.profile_revision)=(NEW.catalog_id,NEW.profile_id,NEW.profile_revision);
 SELECT * INTO STRICT a FROM settlement_data.futures_margin_adoptions x WHERE(x.catalog_id,x.trade_id,x.binding_id)=(NEW.catalog_id,NEW.trade_id,NEW.binding_id);
 SELECT * INTO STRICT evidence FROM trading.gas_owned_evidence x WHERE(x.catalog_id,x.id)=(p.catalog_id,p.evidence_id);
 SELECT * INTO STRICT account FROM trading.gas_owned_accounts x WHERE(x.catalog_id,x.id)=(g.catalog_id,g.account_id);
 SELECT * INTO STRICT origin FROM trading.trade_revisions x WHERE(x.catalog_id,x.trade_id,x.revision)=(NEW.catalog_id,NEW.trade_id,a.origin_revision);
 SELECT * INTO STRICT o FROM settlement_data.futures_margin_policy_occurrences x WHERE(x.catalog_id,x.policy_id,x.policy_revision,x.id)=(d.catalog_id,d.policy_id,d.policy_revision,d.occurrence_id);
 SELECT x.* INTO STRICT product FROM settlement_data.futures_delivery_profiles profile JOIN ref_data.futures_series s
  ON(s.catalog_id,s.revision_id,s.code)=(profile.reference_catalog_id,profile.reference_revision_id,profile.series_code)
  JOIN ref_data.futures_products x ON(x.catalog_id,x.revision_id,x.code)=(s.catalog_id,s.revision_id,s.product_code)
  WHERE(profile.catalog_id,profile.id,profile.revision)=(NEW.catalog_id,NEW.profile_id,NEW.profile_revision);
 IF(d.trade_id,d.binding_id,d.policy_id,d.policy_revision,d.occurrence_id,d.publication_order) IS DISTINCT FROM
  (NEW.trade_id,NEW.binding_id,NEW.policy_id,NEW.policy_revision,NEW.final_occurrence_id,p.publication_order)
 OR a.origin_revision<>NEW.origin_revision OR p.kind<>'delivery_obligation' OR p.origin_policy<>'physical_futures_delivery_v1'
 OR p.settlement_mode<>'physical_delivery_v1' OR p.account_id<>g.account_id OR d.previous_id IS NOT NULL
 OR(evidence.kind,evidence.account_id,evidence.quantity,evidence.occurred_at,evidence.publication_order) IS DISTINCT FROM
  ('futures_final',g.account_id,p.quantity,o.at,d.publication_order)
 OR(p.quantity,p.effective_at,p.definition_revision_id,p.mark_curve_code,p.mark_contract_id,p.due_at,p.carrying) IS DISTINCT FROM
  (origin.lots*product.contract_size,o.at,account.definition_revision_id,account.mark_curve_code,account.mark_contract_id,g.delivery_from,g.carrying)
 OR NOT EXISTS(SELECT 1 FROM trading.publications x WHERE(x.catalog_id,x.publication_order,x.publication_xid)=(p.catalog_id,p.publication_order,pg_current_xact_id()))
 THEN RAISE EXCEPTION 'futures_delivery_origin';END IF;RETURN NEW;
END$$;
CREATE TRIGGER exact_delivery_origin BEFORE INSERT ON settlement_data.futures_delivery_obligations FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_futures_delivery_obligation();
CREATE FUNCTION settlement_data.check_futures_delivery_position() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.kind='delivery_obligation' AND NOT EXISTS(SELECT 1 FROM settlement_data.futures_delivery_obligations x WHERE(x.catalog_id,x.position_id)=(NEW.catalog_id,NEW.id))
 THEN RAISE EXCEPTION 'futures_delivery_origin';END IF;RETURN NULL;
END$$;
CREATE CONSTRAINT TRIGGER complete_delivery_origin AFTER INSERT ON trading.gas_owned_positions DEFERRABLE INITIALLY DEFERRED
 FOR EACH ROW EXECUTE FUNCTION settlement_data.check_futures_delivery_position();

-- Preserve every public OID. The private native calculation remains the sole
-- producer; the owned wrapper captures gas authority before its first wait.
DO $extend$DECLARE f pg_proc;definition text;needle text;BEGIN
 SELECT * INTO STRICT f FROM pg_proc WHERE oid='settlement_data.determine_futures_margin(uuid,jsonb,jsonb)'::regprocedure;
 IF f.proowner<>'openetrm_security_owner'::regrole OR NOT f.prosecdef OR f.prokind<>'f' OR f.prorettype<>'jsonb'::regtype OR f.proretset
 OR f.proisstrict OR f.proleakproof OR f.proparallel<>'u' OR f.provolatile<>'v' OR f.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog']::text[]
 OR NOT has_function_privilege('openetrm_runtime',f.oid,'EXECUTE') OR EXISTS(SELECT 1 FROM aclexplode(coalesce(f.proacl,acldefault('f',f.proowner)))x
  WHERE x.grantor<>f.proowner OR x.is_grantable OR x.privilege_type<>'EXECUTE' OR x.grantee NOT IN(f.proowner,'openetrm_runtime'::regrole))
 THEN RAISE EXCEPTION 'futures_delivery_writer_upgrade';END IF;
 definition=pg_get_functiondef(f.oid);needle='settlement_data.determine_futures_margin(catalog uuid, body jsonb, steps jsonb)';
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'futures_delivery_writer_upgrade';END IF;
 definition=replace(definition,needle,'settlement_data.determine_futures_margin_native(catalog uuid, body jsonb, steps jsonb, delivery_until timestamp with time zone)');
 needle=$old$operation=(body->>'operationId')::uuid;until_at=settlement_data.capture_futures_margin_authority(catalog,'AmendTrade');$old$;
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'futures_delivery_writer_upgrade';END IF;
 definition=replace(definition,needle,$new$operation=(body->>'operationId')::uuid;until_at=least(settlement_data.capture_futures_margin_authority(catalog,'AmendTrade'),delivery_until);$new$);
 EXECUTE definition;
 ALTER FUNCTION settlement_data.determine_futures_margin_native(uuid,jsonb,jsonb,timestamptz) OWNER TO openetrm_security_owner;
 REVOKE ALL ON FUNCTION settlement_data.determine_futures_margin_native(uuid,jsonb,jsonb,timestamptz) FROM PUBLIC,openetrm_runtime;
END$extend$;
CREATE OR REPLACE FUNCTION settlement_data.determine_futures_margin(catalog uuid,body jsonb,steps jsonb) RETURNS jsonb
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE until_at timestamptz;result jsonb;trade uuid;binding uuid;BEGIN
 until_at=settlement_data.capture_futures_margin_authority(catalog,'AmendTrade');
 trade=(body->>'tradeId')::uuid;binding=(body->>'bindingId')::uuid;
 until_at=least(until_at,settlement_data.futures_margin_delivery_authority(catalog,trade,binding));
 result=settlement_data.determine_futures_margin_native(catalog,body,steps,until_at);
 PERFORM settlement_data.require_futures_margin_delivery_commit(catalog,trade,binding,until_at);
 RETURN result;
END$$;
DO $finish$DECLARE definition text;needle text;BEGIN
 definition=pg_get_functiondef('settlement_data.finish_futures_margin_run(uuid,uuid,jsonb,timestamptz)'::regprocedure);
 needle=$old$PERFORM settlement_data.require_futures_margin_reference(p);$old$;
 IF(length(definition)-length(replace(definition,needle,'')))/length(needle)<>1 THEN RAISE EXCEPTION 'futures_delivery_writer_upgrade';END IF;
 EXECUTE replace(definition,needle,needle||E'\n PERFORM settlement_data.require_futures_margin_delivery_commit(catalog,c.trade_id,c.binding_id,until_at);');
END$finish$;

DO $$DECLARE signature regprocedure;BEGIN
 FOREACH signature IN ARRAY ARRAY['settlement_data.futures_margin_delivery_authority(uuid,uuid,uuid)'::regprocedure,
 'settlement_data.require_futures_margin_delivery_commit(uuid,uuid,uuid,timestamptz)'::regprocedure,
 'settlement_data.futures_margin_delivery_read_authority(uuid,uuid,uuid,uuid,timestamptz)'::regprocedure,
 'settlement_data.read_futures_delivery_final(uuid,uuid,uuid,timestamptz)'::regprocedure,
 'settlement_data.require_futures_delivery_head(uuid,uuid,uuid)'::regprocedure,'settlement_data.apply_futures_final_delivery()'::regprocedure,
 'settlement_data.guard_futures_delivery_obligation()'::regprocedure,'settlement_data.check_futures_delivery_position()'::regprocedure,
 'settlement_data.require_futures_delivery_profile_read(uuid,uuid,integer,timestamptz)'::regprocedure,
 'settlement_data.read_futures_delivery_profile(uuid,uuid,integer,uuid,timestamptz)'::regprocedure,
 'settlement_data.read_futures_delivery_obligation(uuid,uuid,uuid,uuid,timestamptz)'::regprocedure,
 'settlement_data.read_futures_delivery_settings(uuid,uuid,timestamptz)'::regprocedure,
 'settlement_data.read_futures_delivery_position(uuid,uuid,uuid,timestamptz)'::regprocedure] LOOP
  EXECUTE format('ALTER FUNCTION %s OWNER TO openetrm_security_owner',signature);EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',signature);
 END LOOP;
END$$;
GRANT EXECUTE ON FUNCTION settlement_data.futures_margin_delivery_authority(uuid,uuid,uuid) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION settlement_data.futures_margin_delivery_read_authority(uuid,uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
GRANT EXECUTE ON FUNCTION settlement_data.require_futures_delivery_profile_read(uuid,uuid,integer,timestamptz),
 settlement_data.read_futures_delivery_profile(uuid,uuid,integer,uuid,timestamptz),
 settlement_data.read_futures_delivery_obligation(uuid,uuid,uuid,uuid,timestamptz),
 settlement_data.read_futures_delivery_settings(uuid,uuid,timestamptz),
 settlement_data.read_futures_delivery_position(uuid,uuid,uuid,timestamptz) TO openetrm_runtime;
COMMIT;
