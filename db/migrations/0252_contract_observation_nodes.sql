-- Contract observation nodes: typed observation terms on a saved recipe node. No new price facts.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
-- Pin the reviewed predecessor definitions and their installation ownership. These trigger helpers have no
-- explicit ACL and use the catalog-only search path.
DO $$DECLARE helper record;BEGIN
 FOR helper IN SELECT * FROM(VALUES
  ('market_data.guard_panel_weight()','19a136703847c9997ea1d024047c584e61633853df211ad5c02b8ce75e656699'),
  ('market_data.guard_panel_publication()','967658630ec72e0469e2a721023394c3d1074f84ce37c7527985dd495b4e9b77')
 )v(signature,hash) LOOP
  IF NOT EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
   WHERE p.oid=to_regprocedure(helper.signature) AND p.proowner=n.nspowner AND p.proacl IS NULL
    AND p.proconfig=ARRAY['search_path=pg_catalog']
    AND encode(sha256(convert_to(pg_get_functiondef(p.oid),'UTF8')),'hex')=helper.hash)
  THEN RAISE EXCEPTION 'contract_observation_upgrade: unexpected helper %',helper.signature USING ERRCODE='55000';END IF;
 END LOOP;
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='market_data.custom_curve_nodes'::regclass
  AND conname='custom_curve_nodes_kind_check' AND convalidated
  AND encode(sha256(convert_to(pg_get_constraintdef(oid),'UTF8')),'hex')='4aaae783a6dc1d300d38dfbe32cce15b3f52da57d1a742f7e2a682aacbfe99e8')
 THEN RAISE EXCEPTION 'contract_observation_upgrade: unexpected node kinds' USING ERRCODE='55000';END IF;
END$$;
ALTER TABLE market_data.custom_curve_nodes DROP CONSTRAINT custom_curve_nodes_kind_check;
ALTER TABLE market_data.custom_curve_nodes ADD CHECK(kind IN('source','published_source','select','weighted_contracts','weighted_panels','dates','sample','mean','lookup','splice','power_intervals','contract_observations','output'));

-- A contract observation panel is a computed-curve combination input, as other panels are. Its other consumers
-- (mean, lookup, splice) remain refused by the recipe engine.
DO $$
DECLARE signature text;body text;
 old_types constant text:='''sample'',''mean'',''published_source'',''weighted_panels'',''lookup'',''splice''';
 new_types constant text:='''sample'',''mean'',''published_source'',''weighted_panels'',''lookup'',''splice'',''contract_observations''';
BEGIN
 FOREACH signature IN ARRAY ARRAY['market_data.guard_panel_weight()','market_data.guard_panel_publication()'] LOOP
  body:=pg_get_functiondef(signature::regprocedure);
  IF (length(body)-length(replace(body,old_types,'')))/length(old_types)<>1
  THEN RAISE EXCEPTION 'Contract observation upgrade requires exactly one expected panel type check in %',signature;END IF;
  EXECUTE replace(body,old_types,new_types);
 END LOOP;
END$$;

-- One row per term: a positive weight within a named group, a contract position, an optional second position whose
-- value is subtracted, and the period whose days are observed.
CREATE TABLE market_data.custom_curve_observation_terms (
 catalog_id uuid NOT NULL,curve_id uuid NOT NULL,version integer NOT NULL,node_id text NOT NULL,
 ordinal integer NOT NULL CHECK(ordinal>=0),
 term_group text NOT NULL CHECK(term_group ~ '^[a-z][a-z0-9_]{0,31}$'),
 weight numeric NOT NULL CHECK(weight NOT IN('NaN'::numeric,'Infinity'::numeric,'-Infinity'::numeric) AND weight>0 AND weight<1e24 AND scale(weight)<=18),
 position integer NOT NULL CHECK(position BETWEEN 1 AND 120),
 less_position integer CHECK(less_position BETWEEN 1 AND 120 AND less_position<>position),
 period text NOT NULL CHECK(period IN('calendar_month_of_delivery_v1','trade_month_of_delivery_contract_v1')),
 PRIMARY KEY(catalog_id,curve_id,version,node_id,ordinal),
 FOREIGN KEY(catalog_id,curve_id,version,node_id) REFERENCES market_data.custom_curve_nodes(catalog_id,curve_id,version,node_id)
);
DO $$DECLARE t text;BEGIN
 FOREACH t IN ARRAY ARRAY['custom_curve_observation_terms'] LOOP
  EXECUTE format('ALTER TABLE market_data.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE market_data.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('CREATE TRIGGER guard_custom_curve BEFORE INSERT OR UPDATE OR DELETE ON market_data.%I FOR EACH ROW EXECUTE FUNCTION market_data.guard_custom_curve()',t);
  EXECUTE format('CREATE CONSTRAINT TRIGGER agent_draft_write AFTER INSERT OR UPDATE OR DELETE ON market_data.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION security.check_custom_curve_agent_write()',t);
  EXECUTE format('CREATE POLICY scoped_read ON market_data.%I FOR SELECT TO openetrm_runtime,openetrm_risk_worker USING(security.custom_curve_visible(catalog_id,curve_id,version))',t);
  EXECUTE format('CREATE POLICY risk_worker_service ON market_data.%I AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read())',t);
  EXECUTE format('CREATE POLICY current_insert ON market_data.%I AS RESTRICTIVE FOR INSERT TO openetrm_runtime WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_update ON market_data.%I AS RESTRICTIVE FOR UPDATE TO openetrm_runtime USING(security.current_write_context()) WITH CHECK(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY current_delete ON market_data.%I AS RESTRICTIVE FOR DELETE TO openetrm_runtime USING(security.current_write_context())',t);
  EXECUTE format('CREATE POLICY scoped_insert ON market_data.%I FOR INSERT TO openetrm_runtime WITH CHECK((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version))',t);
  EXECUTE format('CREATE POLICY scoped_update ON market_data.%I FOR UPDATE TO openetrm_runtime USING((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version)) WITH CHECK((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version))',t);
  EXECUTE format('CREATE POLICY scoped_delete ON market_data.%I FOR DELETE TO openetrm_runtime USING((security.market_access(catalog_id,''WriteMarketDefinitions'') OR security.custom_curve_agent_draft(catalog_id,curve_id,version)) AND security.custom_curve_visible(catalog_id,curve_id,version))',t);
  EXECUTE format('GRANT SELECT ON market_data.%I TO openetrm_runtime,openetrm_security_owner,openetrm_risk_worker',t);
  EXECUTE format('GRANT INSERT,UPDATE,DELETE ON market_data.%I TO openetrm_runtime',t);
 END LOOP;
END$$;

CREATE FUNCTION market_data.guard_observation_term() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM market_data.custom_curve_nodes n
  WHERE(n.catalog_id,n.curve_id,n.version,n.node_id,n.kind)=(NEW.catalog_id,NEW.curve_id,NEW.version,NEW.node_id,'contract_observations'))
 THEN RAISE EXCEPTION 'recipe_observation_terms' USING ERRCODE='P0001';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER selected_observation_operator BEFORE INSERT OR UPDATE ON market_data.custom_curve_observation_terms
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_observation_term();

-- Publication revalidates the node after any later draft edit: its exact parameters, one to 16 terms with
-- contiguous ordinals from 0, and a business-day calendar exactly when it observes business days.
CREATE FUNCTION market_data.guard_observation_publication() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE node market_data.custom_curve_nodes;target date;
 required constant text[]:=ARRAY['implementation','targetFrom','targetCount','days','roll','sampling','corrections'];
BEGIN
 IF NEW.state<>'published' OR OLD.state<>'draft' THEN RETURN NEW;END IF;
 IF EXISTS(SELECT 1 FROM market_data.custom_curve_observation_terms t JOIN market_data.custom_curve_nodes n
   ON(n.catalog_id,n.curve_id,n.version,n.node_id)=(t.catalog_id,t.curve_id,t.version,t.node_id)
  WHERE(t.catalog_id,t.curve_id,t.version)=(NEW.catalog_id,NEW.curve_id,NEW.version) AND n.kind<>'contract_observations')
 THEN RAISE EXCEPTION 'recipe_observation_terms' USING ERRCODE='P0001';END IF;
 FOR node IN SELECT * FROM market_data.custom_curve_nodes n
  WHERE(n.catalog_id,n.curve_id,n.version,n.kind)=(NEW.catalog_id,NEW.curve_id,NEW.version,'contract_observations') LOOP
  IF NOT node.parameters ?& required OR node.parameters-required<>'{}'::jsonb
   OR EXISTS(SELECT 1 FROM jsonb_each(node.parameters) p WHERE jsonb_typeof(p.value)<>'string')
   OR node.parameters->>'implementation'<>'contract_observation_terms_v1'
   OR node.parameters->>'roll'<>'contract_expiry_roll_v1'
   OR node.parameters->>'sampling'<>'context_utc_time'
   OR node.parameters->>'corrections'<>'exact_context_frame_history_latest_visible'
   OR node.parameters->>'days' NOT IN('calendar_days_v1','business_days_v1')
   OR node.parameters->>'targetFrom' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
   OR(CASE WHEN node.parameters->>'targetCount' ~ '^[0-9]{1,9}$' THEN(node.parameters->>'targetCount')::integer NOT BETWEEN 1 AND 120 ELSE true END)
  THEN RAISE EXCEPTION 'recipe_observation_policy' USING ERRCODE='P0001';END IF;
  BEGIN target:=(node.parameters->>'targetFrom')::date;
  EXCEPTION WHEN datetime_field_overflow OR invalid_datetime_format THEN RAISE EXCEPTION 'recipe_observation_policy' USING ERRCODE='P0001';
  END;
  IF extract(day FROM target)<>1 OR target NOT BETWEEN '1901-01-01' AND '2199-12-01'
  THEN RAISE EXCEPTION 'recipe_observation_policy' USING ERRCODE='P0001';END IF;
  IF (SELECT count(*) NOT BETWEEN 1 AND 16 OR min(t.ordinal)<>0 OR max(t.ordinal)<>count(*)-1 FROM market_data.custom_curve_observation_terms t
   WHERE(t.catalog_id,t.curve_id,t.version,t.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id))
  THEN RAISE EXCEPTION 'recipe_observation_terms' USING ERRCODE='P0001';END IF;
  IF (node.parameters->>'days'='business_days_v1') IS DISTINCT FROM EXISTS(SELECT 1 FROM market_data.custom_curve_node_calendars c
   WHERE(c.catalog_id,c.curve_id,c.version,c.node_id)=(node.catalog_id,node.curve_id,node.version,node.node_id))
  THEN RAISE EXCEPTION 'recipe_observation_calendar' USING ERRCODE='P0001';END IF;
 END LOOP;
 RETURN NEW;
END$$;
CREATE TRIGGER observation_publication BEFORE UPDATE OF state ON market_data.custom_curve_versions
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_observation_publication();
COMMIT;
