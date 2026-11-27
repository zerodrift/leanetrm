-- REC inventory and owned gas reach people through the installed roles (D460).
-- Migration 0344 created the three REC inventory permissions and granted them to no role. Migration 0265 created the
-- three owned gas permissions; two were held only by the agent-only Gas lifecycle operator role (0305), and an agent
-- acts only within what its delegating person also holds. So on an installation no person could read, record or set up
-- either. Every human role that reads trades now reads both: once a book holds a park and loan agreement, a trade
-- reader without the owned gas read is refused that book's Blotter and position reads. Recording and setup go to
-- named operating roles. Agent-only roles and the roles that read no trades are unchanged, and no person is given a role.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT 1 FROM security.epoch WHERE id FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

DO $$
DECLARE readers text[]=ARRAY['trader','risk_analyst','facility_operator','facility_limits_admin','gas_operator',
 'gas_service_admin','contracts_officer','credit_officer','trade_confirmations','trade_supervisor'];
 missing text;
BEGIN
 -- An installation without one of these roles stops here instead of receiving part of the grant. The four roles that
 -- record or set up are among the ten.
 SELECT string_agg(w.code,', ' ORDER BY w.code) INTO missing FROM unnest(readers) w(code)
 WHERE NOT EXISTS(SELECT 1 FROM security.roles r WHERE r.code=w.code);
 IF missing IS NOT NULL THEN RAISE EXCEPTION 'inventory_role_grants' USING DETAIL='Missing installed roles: '||missing;END IF;
 -- A grant an installation already made by hand is kept as it is.
 INSERT INTO security.role_permissions SELECT r.id,p FROM security.roles r
 CROSS JOIN unnest(ARRAY['ReadRecInventory','ReadGasOwnedHoldings'])p WHERE r.code=ANY(readers) ON CONFLICT DO NOTHING;
 INSERT INTO security.role_permissions SELECT r.id,g.permission FROM security.roles r,(VALUES
  ('facility_operator','RecordRecInventory'),('contracts_officer','ConfigureRecInventory'),
  ('gas_operator','RecordGasOwnedHoldings'),('gas_service_admin','RecordGasOwnedHoldings'),
  ('gas_service_admin','ConfigureGasOwnedHoldings'))g(code,permission) WHERE g.code=r.code ON CONFLICT DO NOTHING;
END$$;
COMMIT;
