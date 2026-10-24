-- Couple delivery facts to the same exact catalog from job through price source.
-- Stop runtime admission and drain calls before this additive schema upgrade.
BEGIN;
-- Collector admission takes its session fence before the publication fence.
SELECT pg_advisory_xact_lock(730849110);
SELECT pg_advisory_xact_lock(730849102);
LOCK TABLE power_data.jobs,power_data.sources,power_data.attempts,power_data.batches,power_data.prices IN ACCESS EXCLUSIVE MODE;
DO $$
BEGIN
 IF EXISTS(SELECT 1 FROM power_data.batches b JOIN power_data.attempts a ON a.id=b.id
  JOIN power_data.jobs j ON j.id=a.job_id WHERE b.catalog_id<>j.catalog_id)
 OR EXISTS(SELECT 1 FROM power_data.prices p JOIN power_data.batches b ON b.id=p.batch_id
  JOIN power_data.sources s ON s.id=p.source_id WHERE b.catalog_id<>s.catalog_id)
 THEN RAISE EXCEPTION 'delivery_catalog_integrity: retained catalogs disagree; explicit operator review is required';END IF;
 IF NOT EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='power_data.prices'::regclass AND tgname='immutable_fact'
  AND tgfoid='market_data.guard_knowledge_receipt()'::regprocedure AND tgtype=27 AND tgenabled='O' AND NOT tgisinternal)
 THEN RAISE EXCEPTION 'delivery_catalog_integrity: unexpected immutable price guard';END IF;
END$$;
ALTER TABLE power_data.attempts ADD COLUMN catalog_id uuid;
UPDATE power_data.attempts a SET catalog_id=j.catalog_id FROM power_data.jobs j WHERE j.id=a.job_id;
ALTER TABLE power_data.attempts ALTER COLUMN catalog_id SET NOT NULL;
ALTER TABLE power_data.attempts ADD UNIQUE(id,catalog_id);
ALTER TABLE power_data.attempts DROP CONSTRAINT attempts_job_id_fkey;
ALTER TABLE power_data.attempts ADD FOREIGN KEY(job_id,catalog_id) REFERENCES power_data.jobs(id,catalog_id);
ALTER TABLE power_data.batches ADD UNIQUE(id,catalog_id);
ALTER TABLE power_data.batches DROP CONSTRAINT batches_id_fkey;
ALTER TABLE power_data.batches ADD FOREIGN KEY(id,catalog_id) REFERENCES power_data.attempts(id,catalog_id);
ALTER TABLE power_data.prices ADD COLUMN catalog_id uuid;
-- Only the new key is populated. The exact immutable guard is restored in this
-- transaction while exclusive table locks prevent concurrent reads or writes.
ALTER TABLE power_data.prices DISABLE TRIGGER immutable_fact;
UPDATE power_data.prices p SET catalog_id=b.catalog_id FROM power_data.batches b WHERE b.id=p.batch_id;
ALTER TABLE power_data.prices ENABLE TRIGGER immutable_fact;
ALTER TABLE power_data.prices ALTER COLUMN catalog_id SET NOT NULL;
ALTER TABLE power_data.prices DROP CONSTRAINT prices_batch_id_fkey;
ALTER TABLE power_data.prices DROP CONSTRAINT prices_source_id_fkey;
ALTER TABLE power_data.prices ADD FOREIGN KEY(batch_id,catalog_id) REFERENCES power_data.batches(id,catalog_id);
ALTER TABLE power_data.prices ADD FOREIGN KEY(source_id,catalog_id) REFERENCES power_data.sources(id,catalog_id);

-- Keep the reviewed worker implementation, signatures, grants and authority
-- checks intact. Change only its explicit inserts for the new exact keys.
DO $$
DECLARE signature regprocedure;definition text;original text;replacement text;
BEGIN
 FOR signature,original,replacement IN VALUES
 ('power_data.begin_attempt(uuid,date,date)'::regprocedure,
  'INSERT INTO power_data.attempts VALUES(token,job,selected.generation,pg_backend_pid(),(SELECT generation FROM security.epoch),first_day,last_day,clock_timestamp(),''running'',NULL,NULL);',
  'INSERT INTO power_data.attempts(id,job_id,generation,worker_pid,security_generation,from_date,through_date,started_at,state,finished_at,error_code,catalog_id) VALUES(token,job,selected.generation,pg_backend_pid(),(SELECT generation FROM security.epoch),first_day,last_day,clock_timestamp(),''running'',NULL,NULL,catalog);'),
 ('power_data.finish_attempt(uuid,jsonb,text)'::regprocedure,
  'INSERT INTO power_data.prices VALUES(token,source.id,(row->>''start'')::timestamptz,(row->>''end'')::timestamptz,(row->>''price'')::numeric,(row->>''revision'')::integer);',
  'INSERT INTO power_data.prices(batch_id,source_id,starts_at,ends_at,price,provider_revision,catalog_id) VALUES(token,source.id,(row->>''start'')::timestamptz,(row->>''end'')::timestamptz,(row->>''price'')::numeric,(row->>''revision'')::integer,catalog);')
 LOOP
  definition=pg_get_functiondef(signature);
  IF length(definition)-length(replace(definition,original,''))<>length(original)
  THEN RAISE EXCEPTION 'delivery_catalog_integrity: unexpected worker insert contract for %',signature;END IF;
  EXECUTE replace(definition,original,replacement);
 END LOOP;
END$$;
COMMIT;
