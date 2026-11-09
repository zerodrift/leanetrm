-- Explicit publication-release pricing preserves the legacy scheduled-fixing contract.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SELECT pg_advisory_xact_lock(730849102);
DO $$DECLARE c record;n integer=0;BEGIN
 FOR c IN SELECT conname FROM pg_constraint WHERE conrelid='market_data.custom_curve_price_terms'::regclass
 AND contype='c' AND pg_get_constraintdef(oid) LIKE '%published_index_v1%' LOOP
  EXECUTE format('ALTER TABLE market_data.custom_curve_price_terms DROP CONSTRAINT %I',c.conname);n=n+1;
 END LOOP;
 IF n<>2 THEN RAISE EXCEPTION 'publication_price_upgrade: unexpected settlement constraints';END IF;
END$$;
ALTER TABLE market_data.custom_curve_price_terms ADD CONSTRAINT custom_curve_price_terms_settlement_check
 CHECK(settlement IN('exchange_final_v1','published_index_v1','published_release_v1','derived_inputs_v1'));
ALTER TABLE market_data.custom_curve_price_terms ADD CONSTRAINT custom_curve_price_terms_publication_check CHECK(
 (settlement IN('published_index_v1','published_release_v1') AND publication_id IS NOT NULL AND publication_version>0
  AND reference_catalog_id IS NOT NULL AND reference_revision_id IS NOT NULL AND index_code IS NOT NULL
  AND(projection_policy IS NULL OR projection_policy='forward_curve_no_basis_v1')) OR
 (settlement NOT IN('published_index_v1','published_release_v1') AND publication_id IS NULL AND publication_version IS NULL
  AND reference_catalog_id IS NULL AND reference_revision_id IS NULL AND index_code IS NULL AND projection_policy IS NULL));
DO $$DECLARE item record;body text;needle text=$old$='published_index_v1'$old$;BEGIN
 FOR item IN SELECT * FROM(VALUES
  ('market_data.guard_contract_price_term()','9506d23c3790d1d71861e051531d87b6341e46851390852e068f7aaaf13c6aef'),
  ('market_data.guard_contract_price_publication()','fc2c4ecc88901468babdce7007dd33fe00cbd37d61e5906b41e0f3a002f66f8b')
 )reviewed(signature,hash) LOOP
  SELECT pg_get_functiondef(p.oid) INTO body FROM pg_proc p WHERE p.oid=item.signature::regprocedure
   AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=item.hash
   AND p.prosecdef AND pg_get_userbyid(p.proowner)='openetrm_security_owner'
   AND p.proconfig=ARRAY['search_path=pg_catalog']
   AND p.proacl='{openetrm_security_owner=X/openetrm_security_owner}'::aclitem[];
  IF body IS NULL THEN RAISE EXCEPTION 'publication_price_upgrade: unexpected helper %',item.signature;END IF;
  IF length(body)-length(replace(body,needle,''))<>length(needle)
  THEN RAISE EXCEPTION 'publication_price_upgrade: unexpected settlement guard %',item.signature;END IF;
  EXECUTE replace(body,needle,$new$ IN('published_index_v1','published_release_v1')$new$);
 END LOOP;
END$$;

-- Exact dated targets bound the read. This is pricing evidence, not the editor's
-- 200-row receipt history. Native publication visibility and worker service
-- authority remain mandatory, including before returning the result.
CREATE FUNCTION market_data.read_contract_publication_prices(catalog uuid,market_revision uuid,targets jsonb,
 market_cutoff timestamptz,known_at timestamptz) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog SET TimeZone='UTC' AS $$
DECLARE target jsonb;result jsonb='[]';occurrences jsonb;pub uuid;selected_version integer;index_name text;
 first_day date;last_day date;fixing market_data.index_fixing_observations;worker boolean;
BEGIN
 worker=pg_has_role(session_user,'openetrm_risk_worker','MEMBER');
 IF NOT security.market_access(catalog,'ReadMarketData') OR(worker AND NOT risk.worker_read())
 OR NOT security.custom_curve_source_visible(catalog,market_revision) THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 IF targets IS NULL OR jsonb_typeof(targets)<>'array' OR jsonb_array_length(targets)>1200
 OR pg_column_size(targets)>1048576 OR market_cutoff IS NULL OR known_at IS NULL OR NOT isfinite(market_cutoff) OR NOT isfinite(known_at)
 THEN RAISE EXCEPTION 'publication_command' USING ERRCODE='22023';END IF;
 FOR target IN SELECT value FROM jsonb_array_elements(targets) LOOP
  pub=(target->>'publicationId')::uuid;selected_version=(target->>'version')::integer;index_name=target->>'index';
  first_day=(target->>'from')::date;last_day=(target->>'to')::date;
  IF pub IS NULL OR selected_version IS NULL OR selected_version<1 OR index_name IS NULL OR first_day IS NULL OR last_day IS NULL OR first_day>=last_day
  THEN RAISE EXCEPTION 'publication_command' USING ERRCODE='22023';END IF;
  IF NOT security.price_publication_visible(catalog,pub,selected_version) OR NOT EXISTS(
   SELECT 1 FROM market_data.price_publication_versions v JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(v.catalog_id,v.publication_order)
   WHERE(v.catalog_id,v.publication_id,v.version)=(catalog,pub,selected_version) AND k.known_from<=known_at)
  THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
  SELECT f.* INTO fixing FROM market_data.index_fixing_observations f
   JOIN market_data.price_publication_indices i ON(i.catalog_id,i.publication_id,i.version,i.index_code,i.quote_code,i.reference_catalog_id,i.reference_revision_id)=
    (catalog,pub,selected_version,f.index_code,f.quote_code,f.reference_catalog_id,f.reference_revision_id)
   WHERE(f.catalog_id,f.revision_id,f.index_code,f.observation_from,f.observation_to)=(catalog,market_revision,index_name,first_day,last_day);
  IF NOT FOUND THEN RAISE EXCEPTION 'publication_index' USING ERRCODE='22023';END IF;
  SELECT coalesce(jsonb_agg(market_data.price_publication_occurrence_json(catalog,o.id)||jsonb_build_object('releases',
   (SELECT coalesce(jsonb_agg(jsonb_build_object('id',r.id,'observationId',f.observation_id,'sourceCode',r.source_code,
     'publishedAt',r.published_at,'receivedAt',r.received_at,'knownAt',k.known_from,'vendorRevision',r.source_revision_id,
     'correctsId',r.corrects_release_id,'value',trim_scale(r.value)::text) ORDER BY r.published_at,r.id),'[]')
    FROM market_data.price_publication_releases r
    JOIN market_data.price_publication_fixings f ON(f.catalog_id,f.release_id,f.definition_revision_id,f.index_code,f.delivery_from,f.delivery_to)=
     (r.catalog_id,r.id,market_revision,index_name,first_day,last_day)
    JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
    WHERE(r.catalog_id,r.occurrence_id,r.publication_id,r.publication_version,r.index_code,r.source_code)=
     (catalog,o.id,pub,selected_version,index_name,fixing.source_code)
    AND r.published_at<=market_cutoff AND k.known_from<=known_at AND market_data.price_publication_event_visible(catalog,r.publication_order))) ORDER BY o.id),'[]')
   INTO occurrences FROM market_data.price_publication_occurrences o
   JOIN market_data.price_publication_periods p ON(p.catalog_id,p.occurrence_id,p.delivery_from,p.delivery_to)=(o.catalog_id,o.id,first_day,last_day)
   JOIN trading.knowledge_receipts ok ON(ok.catalog_id,ok.publication_order)=(o.catalog_id,o.publication_order)
   WHERE(o.catalog_id,o.publication_id,o.publication_version)=(catalog,pub,selected_version)
   AND ok.known_from<=known_at AND market_data.price_publication_event_visible(catalog,o.publication_order);
  result=result||jsonb_build_array(jsonb_build_object('target',target,'occurrences',occurrences));
  IF pg_column_size(result)>8388608 THEN RAISE EXCEPTION 'publication_command' USING ERRCODE='22023';END IF;
 END LOOP;
 IF NOT security.market_access(catalog,'ReadMarketData') OR(worker AND NOT risk.worker_read())
 OR NOT security.custom_curve_source_visible(catalog,market_revision) THEN RAISE EXCEPTION 'publication_denied' USING ERRCODE='42501';END IF;
 RETURN result;
END$$;
ALTER FUNCTION market_data.read_contract_publication_prices(uuid,uuid,jsonb,timestamptz,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION market_data.read_contract_publication_prices(uuid,uuid,jsonb,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION market_data.read_contract_publication_prices(uuid,uuid,jsonb,timestamptz,timestamptz) TO openetrm_runtime,openetrm_risk_worker;
COMMIT;
