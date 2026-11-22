-- A swap amendment dated before the trade's latest settlement activity needs its own permission (D442). The settlement
-- commands already refuse to record behind later activity; an amendment at an earlier As of would judge which months
-- have started as of that date. The Trade supervisor role holds the permission and is assigned to no one.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

INSERT INTO security.permissions VALUES('AmendBehindSettlement');
INSERT INTO security.roles VALUES(gen_random_uuid(),'trade_supervisor','Trade supervisor');
INSERT INTO security.role_permissions
 SELECT r.id,p FROM security.roles r CROSS JOIN unnest(ARRAY['ReadMarketData','ReadTrades','ReadTradeParties','AmendTrade','AmendBehindSettlement'])p
 WHERE r.code='trade_supervisor';

-- The effective instant of the trade's latest settlement command, for the amendment preview to state. It is deliberately
-- not bounded by the reader's As of: the question is whether anything was recorded after it.
CREATE FUNCTION settlement_data.swap_activity_through(catalog uuid,trade uuid) RETURNS timestamptz
 LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM settlement_data.require_swap_authority(catalog,false);
 IF NOT EXISTS(SELECT 1 FROM trading.swap_trade_revisions r WHERE(r.catalog_id,r.trade_id)=(catalog,trade) AND security.trading_visible(r.catalog_id,r.publication_order,r.effective_at))
 THEN RAISE EXCEPTION 'settlement_trade';END IF;
 PERFORM settlement_data.require_swap_reference(catalog,trade);
 RETURN(SELECT max(c.effective_at) FROM settlement_data.swap_commands c WHERE(c.catalog_id,c.trade_id)=(catalog,trade));
END$$;

CREATE FUNCTION settlement_data.guard_backdated_swap_amendment() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF NEW.previous_revision IS NOT NULL AND EXISTS(SELECT 1 FROM settlement_data.swap_commands c
   WHERE(c.catalog_id,c.trade_id)=(NEW.catalog_id,NEW.trade_id) AND c.effective_at>NEW.effective_at)
  AND security.trading_access(NEW.catalog_id,'AmendBehindSettlement') IS NOT TRUE
 THEN RAISE EXCEPTION 'swap_amendment_backdated';END IF;
 RETURN NEW;
END$$;
CREATE TRIGGER settled_activity_order BEFORE INSERT ON trading.swap_trade_revisions FOR EACH ROW EXECUTE FUNCTION settlement_data.guard_backdated_swap_amendment();

ALTER FUNCTION settlement_data.swap_activity_through(uuid,uuid) OWNER TO openetrm_security_owner;
ALTER FUNCTION settlement_data.guard_backdated_swap_amendment() OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION settlement_data.swap_activity_through(uuid,uuid),settlement_data.guard_backdated_swap_amendment() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION settlement_data.swap_activity_through(uuid,uuid) TO openetrm_runtime;
COMMIT;
