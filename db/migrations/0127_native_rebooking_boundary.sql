-- Preserve display-reference allocation and refuse archived trading contexts.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
LOCK TABLE trading.trades,trading.instruments IN SHARE ROW EXCLUSIVE MODE;

-- References name committed entities; allocation also retains numbers whose
-- entities were explicitly archived by an operator's schema/rebooking release.
-- A transaction rollback rolls back its allocation as well as its new anchor.
CREATE TABLE trading.reference_counters (
 catalog_id uuid NOT NULL REFERENCES market_data.catalogs(id),
 kind text NOT NULL CHECK(kind IN('trade','instrument')),
 high_water integer NOT NULL CHECK(high_water>=0),
 PRIMARY KEY(catalog_id,kind)
);
ALTER TABLE trading.reference_counters ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.reference_counters FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT,UPDATE ON trading.reference_counters TO openetrm_security_owner;

INSERT INTO trading.reference_counters
 SELECT catalog_id,'trade',max(reference_number) FROM trading.trade_references GROUP BY catalog_id
 UNION ALL
 SELECT catalog_id,'instrument',max(reference_number) FROM trading.instrument_references GROUP BY catalog_id;

CREATE FUNCTION security.guard_reference_counter() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='DELETE' OR (NEW.catalog_id,NEW.kind) IS DISTINCT FROM(OLD.catalog_id,OLD.kind)
  OR NEW.high_water<OLD.high_water THEN
  RAISE EXCEPTION 'Immutable reference allocation history' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION security.guard_reference_counter() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.guard_reference_counter() FROM PUBLIC;
CREATE TRIGGER retained_high_water BEFORE UPDATE OR DELETE ON trading.reference_counters
 FOR EACH ROW EXECUTE FUNCTION security.guard_reference_counter();

CREATE OR REPLACE FUNCTION security.assign_trading_reference() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE reference_kind text;allocated integer;
BEGIN
 IF TG_TABLE_SCHEMA<>'trading' THEN RAISE EXCEPTION 'Invalid reference target' USING ERRCODE='55000';END IF;
 IF TG_TABLE_NAME='trades' THEN reference_kind='trade';
 ELSIF TG_TABLE_NAME='instruments' THEN reference_kind='instrument';
 ELSE RAISE EXCEPTION 'Invalid reference target' USING ERRCODE='55000';END IF;
 INSERT INTO trading.reference_counters AS retained(catalog_id,kind,high_water)
  VALUES(NEW.catalog_id,reference_kind,1)
  ON CONFLICT(catalog_id,kind) DO UPDATE SET high_water=retained.high_water+1
  RETURNING high_water INTO allocated;
 IF reference_kind='trade' THEN
  INSERT INTO trading.trade_references VALUES(NEW.catalog_id,NEW.id,allocated);
 ELSE
  INSERT INTO trading.instrument_references VALUES(NEW.catalog_id,NEW.id,allocated);
 END IF;
 RETURN NEW;
END$$;
ALTER FUNCTION security.assign_trading_reference() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.assign_trading_reference() FROM PUBLIC;

-- A destructive definition cutover archives the earlier trading state. Its
-- certified knowledge boundary does not restrict market/reference history.
CREATE TABLE trading.history_boundaries (
 catalog_id uuid PRIMARY KEY REFERENCES market_data.catalogs(id),
 operation_id uuid NOT NULL CHECK(operation_id<>'00000000-0000-0000-0000-000000000000'::uuid),
 context_id uuid NOT NULL,
 FOREIGN KEY(catalog_id,context_id) REFERENCES market_data.temporal_contexts(catalog_id,id),
 FOREIGN KEY(catalog_id,context_id) REFERENCES trading.contexts(catalog_id,context_id)
);
ALTER TABLE trading.history_boundaries ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.history_boundaries FORCE ROW LEVEL SECURITY;
CREATE TRIGGER immutable_history_boundary BEFORE UPDATE OR DELETE ON trading.history_boundaries
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
GRANT SELECT ON trading.history_boundaries TO openetrm_security_owner;

-- Only the schema owner can install a release boundary, after complete native
-- rebooking. No application role receives write or execute authority.
CREATE FUNCTION trading.record_history_boundary(catalog uuid,operation uuid,context uuid) RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE retained trading.history_boundaries;BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 SELECT * INTO retained FROM trading.history_boundaries WHERE catalog_id=catalog;
 IF FOUND THEN
  IF(retained.operation_id,retained.context_id) IS DISTINCT FROM(operation,context) THEN
   RAISE EXCEPTION 'trading_history_boundary_conflict' USING ERRCODE='P0001';END IF;
  RETURN;
 END IF;
 IF operation IS NULL OR operation='00000000-0000-0000-0000-000000000000'::uuid OR NOT EXISTS(
  SELECT 1 FROM trading.contexts c JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.catalog_id,c.context_id)
  WHERE(c.catalog_id,c.context_id)=(catalog,context)
  AND c.known_through=(SELECT coalesce(max(publication_order),0) FROM trading.publications WHERE catalog_id=catalog))
 THEN RAISE EXCEPTION 'trading_history_boundary_input' USING ERRCODE='P0001';END IF;
 INSERT INTO trading.history_boundaries VALUES(catalog,operation,context);
END$$;
REVOKE ALL ON FUNCTION trading.record_history_boundary(uuid,uuid,uuid) FROM PUBLIC;

CREATE FUNCTION security.require_trading_history(catalog uuid,context uuid) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 IF EXISTS(SELECT 1 FROM trading.history_boundaries b
  JOIN market_data.temporal_contexts floor ON(floor.catalog_id,floor.id)=(b.catalog_id,b.context_id)
  JOIN trading.contexts floor_trades ON(floor_trades.catalog_id,floor_trades.context_id)=(b.catalog_id,b.context_id)
  JOIN market_data.temporal_contexts requested ON(requested.catalog_id,requested.id)=(catalog,context)
  JOIN trading.contexts requested_trades ON(requested_trades.catalog_id,requested_trades.context_id)=(catalog,context)
  WHERE b.catalog_id=catalog AND(requested.known_at<floor.known_at OR requested_trades.known_through<floor_trades.known_through))
 THEN RAISE EXCEPTION 'trading_history_archived' USING ERRCODE='P0001';END IF;
END$$;
ALTER FUNCTION security.require_trading_history(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.require_trading_history(uuid,uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION security.use_trading_context(catalog uuid,as_of timestamptz) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE context uuid;BEGIN
 IF NOT security.trading_access(catalog,'ReadTrades') THEN RAISE EXCEPTION 'No trading access' USING ERRCODE='42501';END IF;
 SELECT t.id INTO context FROM security.request_contexts c
 JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(c.market_catalog_id,c.market_context_id)
 JOIN trading.contexts x ON(x.catalog_id,x.context_id)=(t.catalog_id,t.id)
 WHERE c.backend_pid=pg_backend_pid() AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user
  AND t.catalog_id=catalog AND t.effective_on=(as_of AT TIME ZONE 'UTC')::date AND c.trading_as_of IS NULL;
 IF NOT FOUND THEN RAISE EXCEPTION 'knowledge_coverage' USING ERRCODE='P0001';END IF;
 PERFORM security.require_trading_history(catalog,context);
 UPDATE security.request_contexts SET trading_as_of=as_of WHERE backend_pid=pg_backend_pid()
  AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user;
END$$;

-- The PPA reader accepts an explicit context without rebinding the connection.
-- Check its scope before any rows, including an empty book, can be returned.
CREATE OR REPLACE FUNCTION security.ppa_read_scope(catalog uuid,scope text,ids uuid[],context uuid,as_of timestamptz,include_history boolean)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE result jsonb;BEGIN
 PERFORM risk.require_worker_attempt();
 PERFORM security.require_trading_history(catalog,context);
 result=security.ppa_read_scope_pre_portfolio_worker(catalog,scope,ids,context,as_of,include_history);
 PERFORM risk.require_worker_attempt();RETURN result;
END$$;

COMMIT;
