-- Retire the fixed-only command format; every new capability states its policy.
-- Existing capability versions and audit outcomes remain immutable and readable.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='15s';
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DROP FUNCTION assistant.edit_model(uuid,uuid,integer,integer,text,date,text[],text,date,text,date,text[],text,date,text,date,text,date);
ALTER TABLE assistant.model_versions ALTER COLUMN identifier_policy DROP DEFAULT;
COMMIT;
