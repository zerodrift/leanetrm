-- Current consumer: TimedCashPayment and the shared native cash-state reader.
-- Date-only premiums and exact-instant settlement obligations remain distinct
-- economic definitions, sharing the existing payment membership/revision ledger.
BEGIN;
SET LOCAL search_path=pg_catalog;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SELECT pg_advisory_xact_lock(730849102);
DO $guard$BEGIN
 IF NOT EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='trading.cash_payment_instruments'::regclass
  AND conname='cash_payment_instruments_check' AND contype='c' AND convalidated
  AND pg_get_constraintdef(oid)='CHECK ((instrument_id = security.cash_payment_instrument_id(reference_catalog_id, reference_revision_id, (currency_code)::text, payment_date)))')
 OR NOT EXISTS(SELECT 1 FROM pg_attribute WHERE attrelid='trading.cash_payment_instruments'::regclass
  AND attname='payment_date' AND attnotnull AND NOT attisdropped)
 THEN RAISE EXCEPTION 'timed_cash_schema_drift';END IF;
END$guard$;

CREATE FUNCTION security.timed_cash_payment_instrument_id(ref_catalog uuid,ref_revision uuid,currency text,payment_at timestamptz)
 RETURNS uuid LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
 SELECT substr(encode(sha256(convert_to('timed_cash_payment_v1:'||ref_catalog::text||':'||ref_revision::text||':'||currency||':'||
  to_char(payment_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US')||'0+00:00','UTF8')),'hex'),1,32)::uuid;
$$;
ALTER FUNCTION security.timed_cash_payment_instrument_id(uuid,uuid,text,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.timed_cash_payment_instrument_id(uuid,uuid,text,timestamptz) FROM PUBLIC;

ALTER TABLE trading.cash_payment_instruments ADD COLUMN payment_at ref_data.instant;
ALTER TABLE trading.cash_payment_instruments ALTER COLUMN payment_date DROP NOT NULL;
ALTER TABLE trading.cash_payment_instruments DROP CONSTRAINT cash_payment_instruments_check;
ALTER TABLE trading.cash_payment_instruments ADD CONSTRAINT cash_payment_due_definition CHECK(
 (payment_date IS NOT NULL AND payment_at IS NULL AND
  instrument_id=security.cash_payment_instrument_id(reference_catalog_id,reference_revision_id,currency_code,payment_date)) OR
 (payment_date IS NULL AND payment_at IS NOT NULL AND payment_at>=TIMESTAMPTZ '1901-01-01 00:00:00+00' AND payment_at<TIMESTAMPTZ '2200-01-01 00:00:00+00' AND
  instrument_id=security.timed_cash_payment_instrument_id(reference_catalog_id,reference_revision_id,currency_code,payment_at)));
COMMIT;
