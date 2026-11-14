-- Current consumers: exact-input and explicit live-output capture. Their first resolution must
-- survive later session configuration and the loss of an ephemeral source frame.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);

CREATE TABLE market_data.market_close_capture_commands(
 catalog_id uuid NOT NULL,actor_id uuid NOT NULL,request_id uuid NOT NULL,
 request jsonb NOT NULL,close_id uuid NOT NULL,outcome jsonb NOT NULL,
 PRIMARY KEY(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,actor_id,request_id) REFERENCES market_data.market_close_commands(catalog_id,actor_id,request_id),
 FOREIGN KEY(catalog_id,close_id) REFERENCES market_data.market_closes(catalog_id,id)
);
ALTER TABLE market_data.market_close_capture_commands OWNER TO openetrm_security_owner;
ALTER TABLE market_data.market_close_capture_commands ENABLE ROW LEVEL SECURITY;
ALTER TABLE market_data.market_close_capture_commands FORCE ROW LEVEL SECURITY;
CREATE POLICY owner_all ON market_data.market_close_capture_commands TO openetrm_security_owner USING(true) WITH CHECK(true);
CREATE TRIGGER immutable_capture_command BEFORE UPDATE OR DELETE ON market_data.market_close_capture_commands
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();

CREATE FUNCTION security.require_close_capture_write(catalog uuid,session text) RETURNS void
 LANGUAGE plpgsql VOLATILE SET search_path=pg_catalog AS $$
BEGIN
 IF security.current_write_context() IS NOT TRUE OR security.market_access(catalog,'PublishMarketData') IS NOT TRUE
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 IF security.can_record_close(catalog,session) IS NOT TRUE
 THEN RAISE EXCEPTION 'close_grant' USING ERRCODE='42501';END IF;
END$$;
ALTER FUNCTION security.require_close_capture_write(uuid,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_close_capture_write(uuid,text) FROM PUBLIC;

CREATE FUNCTION security.market_close_capture_receipt(catalog uuid,operation uuid,snapshot uuid,close_name text,session text,business_date date,policy text)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE actor uuid;canonical jsonb;previous market_data.market_close_capture_commands;
BEGIN
 PERFORM security.require_close_capture_write(catalog,session);
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR
  snapshot IS NULL OR snapshot='00000000-0000-0000-0000-000000000000'::uuid OR close_name IS NULL OR
  length(btrim(close_name)) NOT BETWEEN 1 AND 80 OR business_date IS NULL OR NOT isfinite(business_date) OR
  policy IS NULL OR policy NOT IN('live_contract_outputs_v1','exact_selected_inputs_v1')
 THEN RAISE EXCEPTION 'pilot_close_input';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
  AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 canonical=jsonb_build_object('snapshotId',snapshot,'name',close_name,'session',session,'businessDate',business_date,
  'policy',policy);
 -- Serialize retries of this actor's command before resolving mutable setup.
 -- Hold no global publication lock during managed graph qualification.
 PERFORM pg_advisory_xact_lock(hashtextextended('market_close_capture:'||catalog::text||':'||actor::text||':'||operation::text,0));
 PERFORM security.require_close_capture_write(catalog,session);
 SELECT * INTO previous FROM market_data.market_close_capture_commands c
  WHERE(c.catalog_id,c.actor_id,c.request_id)=(catalog,actor,operation);
 IF FOUND THEN
  IF previous.request IS DISTINCT FROM canonical THEN RAISE EXCEPTION 'pilot_close_conflict';END IF;
  IF security.market_close_visible(catalog,previous.close_id) IS NOT TRUE
  THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
  RETURN previous.outcome;
 END IF;
 IF EXISTS(SELECT 1 FROM market_data.market_close_commands c WHERE(c.catalog_id,c.actor_id,c.request_id)=(catalog,actor,operation))
 THEN RAISE EXCEPTION 'pilot_close_conflict';END IF;
 RETURN NULL;
END$$;
ALTER FUNCTION security.market_close_capture_receipt(uuid,uuid,uuid,text,text,date,text) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.market_close_capture_receipt(uuid,uuid,uuid,text,text,date,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.market_close_capture_receipt(uuid,uuid,uuid,text,text,date,text) TO openetrm_runtime;

CREATE FUNCTION security.capture_market_close(catalog uuid,definition uuid,operation uuid,snapshot uuid,close_name text,
 session text,business_date date,policy text,frame_at timestamptz,pins jsonb,members jsonb)
 RETURNS jsonb LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE outcome jsonb;captured_close uuid;actor uuid;in_force uuid;header market_data.market_closes;
BEGIN
 outcome=security.market_close_capture_receipt(catalog,operation,snapshot,close_name,session,business_date,policy);
 IF outcome IS NOT NULL THEN RETURN outcome;END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 -- An exact-registration command may have won the common publication fence.
 outcome=security.market_close_capture_receipt(catalog,operation,snapshot,close_name,session,business_date,policy);
 IF outcome IS NOT NULL THEN RETURN outcome;END IF;
 captured_close=security.register_market_close(catalog,definition,operation,close_name,session,business_date,frame_at,pins,members);
 SELECT * INTO STRICT header FROM market_data.market_closes c WHERE(c.catalog_id,c.id)=(catalog,captured_close);
 IF policy='live_contract_outputs_v1' AND EXISTS(
  SELECT 1 FROM market_data.market_close_members m
  JOIN market_data.close_session_curves root ON(root.catalog_id,root.code,root.version,root.definition_revision_id,root.curve_code)=
   (m.catalog_id,header.session_code,header.session_version,m.definition_revision_id,m.curve_code)
  JOIN market_data.contracts term ON(term.catalog_id,term.revision_id,term.id)=(m.catalog_id,m.definition_revision_id,m.contract_id)
  WHERE(m.catalog_id,m.close_id)=(catalog,captured_close) AND term.expiry_at<=frame_at)
 THEN RAISE EXCEPTION 'source_expired';END IF;
 SELECT actor_id INTO STRICT actor FROM security.request_contexts WHERE backend_pid=pg_backend_pid()
  AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
 in_force=security.close_in_force(catalog,session,business_date);
 outcome=jsonb_build_object('id',header.id,'name',header.name,'at',header.at,'kind',header.kind,
  'definition',jsonb_build_object('catalogId',header.catalog_id,'revisionId',header.definition_revision_id),
  'reference',jsonb_build_object('catalogId',header.reference_catalog_id,'revisionId',header.reference_revision_id),
  'anchorCloseId',header.anchor_close_id,'runId',header.run_id,'requestId',operation,
  'session',header.session_code,'sessionVersion',header.session_version,'businessDate',header.business_date,
  'replacedBy',CASE WHEN in_force=header.id THEN NULL ELSE in_force END);
 INSERT INTO market_data.market_close_capture_commands VALUES(catalog,actor,operation,
  jsonb_build_object('snapshotId',snapshot,'name',close_name,'session',session,'businessDate',business_date,'policy',policy),captured_close,outcome);
 PERFORM security.require_close_capture_write(catalog,session);
 IF security.market_close_visible(catalog,captured_close) IS NOT TRUE
 THEN RAISE EXCEPTION 'pilot_close_denied' USING ERRCODE='42501';END IF;
 RETURN outcome;
END$$;
ALTER FUNCTION security.capture_market_close(uuid,uuid,uuid,uuid,text,text,date,text,timestamptz,jsonb,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.capture_market_close(uuid,uuid,uuid,uuid,text,text,date,text,timestamptz,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION security.capture_market_close(uuid,uuid,uuid,uuid,text,text,date,text,timestamptz,jsonb,jsonb) TO openetrm_runtime;
COMMIT;
