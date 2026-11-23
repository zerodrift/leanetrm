-- Operating facilities and operating rights reach people through the installed roles (D444).
-- Migration 0342 created ReadPhysical and ManagePhysicalSetup and granted them to no role, so on an installation no
-- person could set up a facility, or read or book an operating right. Market data editor now sets facilities up, and
-- it and every human role that reads trades read them: once a book holds an operating right, a trade reader without
-- the read capability is refused that book's positions, cannot confirm the trade and loses the plant's report rows.
-- Agent-only roles and the roles that read no trades are unchanged, and no person is given a role.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

DO $$
DECLARE readers text[]=ARRAY['market_data_editor','trader','risk_analyst','facility_operator','facility_limits_admin',
 'gas_operator','gas_service_admin','contracts_officer','credit_officer','trade_confirmations','trade_supervisor'];
 missing text;
BEGIN
 -- An installation without one of these roles stops here instead of receiving part of the grant.
 SELECT string_agg(w.code,', ' ORDER BY w.code) INTO missing FROM unnest(readers) w(code)
 WHERE NOT EXISTS(SELECT 1 FROM security.roles r WHERE r.code=w.code);
 IF missing IS NOT NULL THEN RAISE EXCEPTION 'operating_role_grants' USING DETAIL='Missing installed roles: '||missing;END IF;
 -- A grant an installation already made by hand is kept as it is.
 INSERT INTO security.role_permissions SELECT r.id,'ReadPhysical' FROM security.roles r WHERE r.code=ANY(readers)
 ON CONFLICT DO NOTHING;
 INSERT INTO security.role_permissions SELECT r.id,'ManagePhysicalSetup' FROM security.roles r WHERE r.code='market_data_editor'
 ON CONFLICT DO NOTHING;
END$$;
COMMIT;
