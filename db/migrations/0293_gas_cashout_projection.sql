-- Consumer: whole-book economic projection of native operator cashout claims.
-- Projection configuration is immutable; no observation, cash or title is made.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

DO $guard$DECLARE c pg_constraint;BEGIN
 SELECT * INTO STRICT c FROM pg_constraint WHERE conrelid='trading.gas_owned_projection_profiles'::regclass
  AND conname='gas_owned_projection_profiles_implementation_check';
 IF c.contype<>'c' OR NOT c.convalidated OR pg_get_constraintdef(c.oid) IS DISTINCT FROM
  'CHECK ((implementation = ''gas_quantity_lifecycle_projection_v1''::text))'
 THEN RAISE EXCEPTION 'gas_cashout_projection_upgrade: unexpected implementation constraint';END IF;
END$guard$;
ALTER TABLE trading.gas_owned_projection_profiles DROP CONSTRAINT gas_owned_projection_profiles_implementation_check;
ALTER TABLE trading.gas_owned_projection_profiles ADD CONSTRAINT gas_owned_projection_profiles_implementation_check
 CHECK(implementation IN('gas_quantity_lifecycle_projection_v1','gas_quantity_and_cashout_lifecycle_projection_v2'));
CREATE TABLE trading.gas_owned_projection_cashouts(
 catalog_id uuid NOT NULL,profile_id uuid NOT NULL,profile_revision integer NOT NULL,position_id uuid NOT NULL,
 scenario_definition_revision_id uuid NOT NULL,scenario_curve_code ref_data.code NOT NULL,scenario_contract_id uuid NOT NULL,
 price_interpretation text NOT NULL CHECK(price_interpretation='selected_valuation_mark_is_cashout_expectation_v1'),
 cash_carrying text NOT NULL CHECK(cash_carrying IN('explicit_nominal_cashout_cash_v1','captured_currency_curve_cashout_cash_v1')),
 PRIMARY KEY(catalog_id,profile_id,profile_revision,position_id),
 FOREIGN KEY(catalog_id,profile_id,profile_revision) REFERENCES trading.gas_owned_projection_profiles(catalog_id,id,revision),
 FOREIGN KEY(catalog_id,position_id) REFERENCES trading.gas_owned_positions(catalog_id,id),
 FOREIGN KEY(catalog_id,scenario_definition_revision_id,scenario_curve_code,scenario_contract_id)
  REFERENCES market_data.curve_members(catalog_id,revision_id,curve_code,contract_id)
);
ALTER TABLE trading.gas_owned_projection_cashouts OWNER TO openetrm_security_owner;
ALTER TABLE trading.gas_owned_projection_cashouts ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.gas_owned_projection_cashouts FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON trading.gas_owned_projection_cashouts TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.gas_owned_projection_cashouts FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE TRIGGER native_child BEFORE INSERT ON trading.gas_owned_projection_cashouts FOR EACH ROW EXECUTE FUNCTION trading.guard_gas_owned_projection_child();

CREATE FUNCTION trading.require_gas_cashout_projection(catalog uuid,account uuid,body jsonb,context uuid,as_of timestamptz) RETURNS void
 LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE item jsonb;p trading.gas_owned_positions;BEGIN
 IF jsonb_typeof(body->'cashouts') IS DISTINCT FROM 'array' OR jsonb_array_length(body->'cashouts') NOT BETWEEN 1 AND 2048
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(body->'cashouts')r GROUP BY r->>'positionId' HAVING count(*)<>1)
 THEN RAISE EXCEPTION 'gas_owned_projection_policy';END IF;
 FOR item IN SELECT value FROM jsonb_array_elements(body->'cashouts') LOOP
  PERFORM trading.gas_owned_projection_keys(item,ARRAY['positionId','scenarioDefinitionRevisionId','scenarioCurve','scenarioContractId','priceInterpretation','cashCarrying'],ARRAY[]::text[]);
  SELECT * INTO p FROM trading.gas_owned_positions x WHERE(x.catalog_id,x.id,x.account_id)=(catalog,(item->>'positionId')::uuid,account);
  IF p.id IS NULL OR p.kind<>'operator_claim' OR p.settlement_mode<>'cashout_final_price_v1'
   OR NOT EXISTS(SELECT 1 FROM trading.gas_owned_cashout_terms t WHERE(t.catalog_id,t.position_id)=(catalog,p.id))
   OR(p.definition_revision_id,p.mark_curve_code,p.mark_contract_id) IS DISTINCT FROM
    ((item->>'scenarioDefinitionRevisionId')::uuid,item->>'scenarioCurve',(item->>'scenarioContractId')::uuid)
   OR item->>'priceInterpretation'<>'selected_valuation_mark_is_cashout_expectation_v1'
   OR NOT((p.carrying='nominal_mark_v1' AND item->>'cashCarrying'='explicit_nominal_cashout_cash_v1')
    OR(p.carrying='currency_curve_v1' AND item->>'cashCarrying'='captured_currency_curve_cashout_cash_v1'))
   OR EXISTS(SELECT 1 FROM jsonb_array_elements(body->'performances')r WHERE(r->>'positionId')::uuid=p.id)
  THEN RAISE EXCEPTION 'gas_owned_projection_policy';END IF;
  IF trading.gas_owned_visible(catalog,p.publication_order,TIMESTAMPTZ '0001-01-01 00:00:00+00',context,as_of) IS NOT TRUE
   OR security.market_fact_visible(catalog,p.definition_revision_id,NULL) IS NOT TRUE THEN RAISE EXCEPTION 'gas_owned_context';END IF;
 END LOOP;
END$$;
ALTER FUNCTION trading.require_gas_cashout_projection(uuid,uuid,jsonb,uuid,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION trading.require_gas_cashout_projection(uuid,uuid,jsonb,uuid,timestamptz) FROM PUBLIC,openetrm_runtime,openetrm_risk_worker;

-- Preserve the public producer and scoped reader OIDs, authority, receipt-first
-- path and legacy v1 canonical facts. The existing worker wrapper remains intact.
DO $upgrade$DECLARE spec record;p pg_proc;definition text;item record;BEGIN
 FOR spec IN SELECT * FROM(VALUES
  ('trading.gas_owned_projection_fact(jsonb)','89a3dcb49157e28ab24c9448433ec6d9',false,'i',ARRAY['body'],
   '{openetrm_security_owner=X/openetrm_security_owner}'),
  ('security.configure_gas_owned_projection(uuid,uuid,text,uuid,jsonb,timestamptz,uuid,boolean)','a4ff9851d6879862f9cf6951020171b7',true,'v',
   ARRAY['catalog','operation','action','account','body','as_of','context','receipt_only'],
   '{openetrm_security_owner=X/openetrm_security_owner,openetrm_runtime=X/openetrm_security_owner}'),
  ('security.read_gas_owned_projection_profile_pre_risk(uuid,uuid,integer,uuid,timestamptz)','06ae46a3ca4e8c2c47e802f23c881e78',true,'v',
   ARRAY['catalog','requested_profile','requested_revision','context','at_time'],'{openetrm_security_owner=X/openetrm_security_owner}')
 )v(signature,body_hash,definer,volatility,names,acl) LOOP
  SELECT * INTO STRICT p FROM pg_proc WHERE oid=spec.signature::regprocedure;
  IF md5(p.prosrc)<>spec.body_hash OR p.proowner<>'openetrm_security_owner'::regrole OR p.prorettype<>'jsonb'::regtype OR p.proretset
   OR p.provariadic<>0 OR p.proargmodes IS NOT NULL OR p.proallargtypes IS NOT NULL OR p.proargnames IS DISTINCT FROM spec.names
   OR p.pronargdefaults<>0 OR p.proargdefaults IS NOT NULL OR p.prokind<>'f' OR p.proisstrict OR p.proleakproof OR p.proparallel<>'u'
   OR p.provolatile::text<>spec.volatility OR p.prosecdef IS DISTINCT FROM spec.definer OR(SELECT lanname FROM pg_language WHERE oid=p.prolang)<>'plpgsql'
   OR p.proconfig IS DISTINCT FROM ARRAY['search_path=pg_catalog'] OR p.proacl::text IS DISTINCT FROM spec.acl
  THEN RAISE EXCEPTION 'gas_cashout_projection_upgrade: unexpected helper %',spec.signature;END IF;
  definition=pg_get_functiondef(p.oid);
  IF spec.signature LIKE 'trading.gas_owned_projection_fact(%' THEN
   FOR item IN SELECT * FROM(VALUES
    ('''incomingPositionId''] LOOP','''incomingPositionId'',''scenarioDefinitionRevisionId'',''scenarioContractId''] LOOP'),
    ('ARRAY[''performances'',''supply'']','ARRAY[''performances'',''supply'',''cashouts'']'),
    ('END LOOP;RETURN result;',E'END LOOP;\n IF result->>''implementation''=''gas_quantity_lifecycle_projection_v1'' AND result->''cashouts''=''[]''::jsonb THEN result=result-''cashouts'';END IF;RETURN result;')
   )v(before,after) LOOP
    IF(length(definition)-length(replace(definition,item.before,'')))/length(item.before)<>1 THEN RAISE EXCEPTION 'gas_cashout_projection_upgrade: unexpected fact marker';END IF;
    definition=replace(definition,item.before,item.after);
   END LOOP;
  ELSIF spec.signature LIKE 'security.configure_gas_owned_projection(%' THEN
   FOR item IN SELECT * FROM(VALUES
    ('ELSE PERFORM trading.gas_owned_projection_keys(body,ARRAY[''profileId'',''expectedRevision'',''implementation'',''quantityDisposition'',''effectiveAt'',''performances''],ARRAY[]::text[]);END IF;',
     E'ELSE PERFORM trading.gas_owned_projection_keys(body,ARRAY[''profileId'',''expectedRevision'',''implementation'',''quantityDisposition'',''effectiveAt'',''performances'']||CASE WHEN body->>''implementation''=''gas_quantity_and_cashout_lifecycle_projection_v2'' THEN ARRAY[''cashouts''] ELSE ARRAY[]::text[] END,ARRAY[]::text[]);END IF;'),
    ('body->>''implementation''<>''gas_quantity_lifecycle_projection_v1''','body->>''implementation'' NOT IN(''gas_quantity_lifecycle_projection_v1'',''gas_quantity_and_cashout_lifecycle_projection_v2'')'),
    (E'  FOR item IN SELECT value FROM jsonb_array_elements(body->''performances'') LOOP\n   PERFORM trading.gas_owned_projection_keys(item,',
     E'  IF body->>''implementation''=''gas_quantity_and_cashout_lifecycle_projection_v2'' THEN PERFORM trading.require_gas_cashout_projection(catalog,account,body,context,as_of);END IF;\n  FOR item IN SELECT value FROM jsonb_array_elements(body->''performances'') LOOP\n   PERFORM trading.gas_owned_projection_keys(item,'),
    ('   INSERT INTO trading.gas_owned_projection_profiles VALUES(catalog,(body->>''profileId'')::uuid,version,account,body->>''implementation'',body->>''quantityDisposition'',actor,publication,at_time);',
     E'   INSERT INTO trading.gas_owned_projection_profiles VALUES(catalog,(body->>''profileId'')::uuid,version,account,body->>''implementation'',body->>''quantityDisposition'',actor,publication,at_time);\n   IF body->>''implementation''=''gas_quantity_and_cashout_lifecycle_projection_v2'' THEN\n    FOR item IN SELECT value FROM jsonb_array_elements(body->''cashouts'') LOOP\n     INSERT INTO trading.gas_owned_projection_cashouts VALUES(catalog,(body->>''profileId'')::uuid,version,(item->>''positionId'')::uuid,(item->>''scenarioDefinitionRevisionId'')::uuid,item->>''scenarioCurve'',(item->>''scenarioContractId'')::uuid,item->>''priceInterpretation'',item->>''cashCarrying'');\n    END LOOP;\n   END IF;')
   )v(before,after) LOOP
    IF(length(definition)-length(replace(definition,item.before,'')))/length(item.before)<>1 THEN RAISE EXCEPTION 'gas_cashout_projection_upgrade: unexpected producer marker';END IF;
    definition=replace(definition,item.before,item.after);
   END LOOP;
  ELSE
   SELECT * INTO item FROM(VALUES(' SELECT jsonb_build_object(''profile'',to_jsonb(profile),''authorityUntil'',until_at,',
    E' SELECT jsonb_build_object(''profile'',to_jsonb(profile),''authorityUntil'',until_at,\n  ''cashouts'',coalesce((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.position_id) FROM trading.gas_owned_projection_cashouts c WHERE(c.catalog_id,c.profile_id,c.profile_revision)=(catalog,requested_profile,requested_revision)),''[]''::jsonb),'))v(before,after);
   IF(length(definition)-length(replace(definition,item.before,'')))/length(item.before)<>1 THEN RAISE EXCEPTION 'gas_cashout_projection_upgrade: unexpected reader marker';END IF;
   definition=replace(definition,item.before,item.after);
  END IF;
  EXECUTE definition;
 END LOOP;
END$upgrade$;
COMMIT;
