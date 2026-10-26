-- D229: a collection that repeats recorded delivery prices publishes nothing new.
-- A successful attempt still covers every operating-day interval; only intervals
-- whose price or provider revision differs from the latest published revision
-- become a new batch, so unchanged prices keep their original knowledge time.
BEGIN;
SET LOCAL lock_timeout='5s';
-- Collector admission takes its session fence before the publication fence.
SELECT pg_advisory_xact_lock(730849110);
SELECT pg_advisory_xact_lock(730849102);
-- The offered rows of a validated payload, one per interval.
CREATE FUNCTION power_data.offered_prices(payload jsonb)
RETURNS TABLE(source_id uuid,starts_at timestamptz,ends_at timestamptz,price numeric,provider_revision integer)
LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT (item->>'sourceId')::uuid,(r->>'start')::timestamptz,(r->>'end')::timestamptz,(r->>'price')::numeric,(r->>'revision')::integer
 FROM jsonb_array_elements(payload) item CROSS JOIN LATERAL jsonb_array_elements(item->'rows') r
$$;
ALTER FUNCTION power_data.offered_prices(jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION power_data.offered_prices(jsonb) FROM PUBLIC;
CREATE OR REPLACE FUNCTION power_data.finish_attempt(token uuid,payload jsonb,failure text) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE attempt power_data.attempts;job power_data.jobs;catalog uuid;item jsonb;row jsonb;source power_data.sources;
BEGIN
 catalog=power_data.worker_catalog();PERFORM power_data.require_lock();
 SELECT * INTO attempt FROM power_data.attempts WHERE id=token AND state='running' AND worker_pid=pg_backend_pid() AND security_generation=(SELECT generation FROM security.epoch) FOR UPDATE;
 SELECT * INTO job FROM power_data.jobs WHERE id=attempt.job_id AND catalog_id=catalog AND collector_login=session_user AND enabled AND generation=attempt.generation FOR SHARE;
 IF job.id IS NULL THEN RAISE EXCEPTION 'collection_fence' USING ERRCODE='42501';END IF;
 PERFORM power_data.validate_sources(job.id);
 IF failure IS NOT NULL THEN
  IF failure !~ '^[a-z_]{1,64}$' OR payload IS NOT NULL THEN RAISE EXCEPTION 'collection_input';END IF;
  UPDATE power_data.attempts SET state='failed',finished_at=clock_timestamp(),error_code=failure WHERE id=token;RETURN;
 END IF;
 IF payload IS NULL OR jsonb_typeof(payload)<>'array' OR jsonb_array_length(payload)<>(SELECT count(*) FROM power_data.job_sources WHERE job_id=job.id) OR pg_column_size(payload)>32000000 THEN RAISE EXCEPTION 'collection_input';END IF;
 PERFORM pg_advisory_xact_lock(730849102);
 FOR item IN SELECT value FROM jsonb_array_elements(payload) LOOP
  SELECT s.* INTO source FROM power_data.sources s JOIN power_data.job_sources j ON j.source_id=s.id WHERE j.job_id=job.id AND s.id=(item->>'sourceId')::uuid AND s.catalog_id=catalog AND s.provider=job.provider;
  IF source.id IS NULL OR jsonb_typeof(item->'rows')<>'array' OR jsonb_array_length(item->'rows') NOT BETWEEN 1 AND 100000 THEN RAISE EXCEPTION 'collection_input';END IF;
  FOR row IN SELECT value FROM jsonb_array_elements(item->'rows') LOOP
   IF (row->>'end')::timestamptz-(row->>'start')::timestamptz<>make_interval(mins=>source.minutes) OR (row->>'price') IS NULL THEN RAISE EXCEPTION 'collection_input';END IF;
   IF (source.provider='PJM' AND ((row->>'revision') IS NULL OR (row->>'revision')::integer<1)) OR (source.provider='ERCOT' AND (row->>'revision') IS NOT NULL) THEN RAISE EXCEPTION 'collection_input';END IF;
  END LOOP;
 END LOOP;
 -- Each source once, each interval once.
 IF (SELECT count(DISTINCT source_id) FROM power_data.offered_prices(payload))<>jsonb_array_length(payload)
 OR EXISTS(SELECT 1 FROM power_data.offered_prices(payload) GROUP BY source_id,starts_at HAVING count(*)>1) THEN RAISE EXCEPTION 'collection_input';END IF;
 IF EXISTS(
  WITH expected AS(SELECT s.id source_id,d.starts_at+n*make_interval(mins=>s.minutes) starts_at,d.starts_at+(n+1)*make_interval(mins=>s.minutes) ends_at
   FROM power_data.job_sources m JOIN power_data.sources s ON s.id=m.source_id
   JOIN power_data.operating_days d ON(d.reference_catalog_id,d.reference_revision_id,d.clock_code)=(s.reference_catalog_id,s.reference_revision_id,s.clock_code) AND d.operating_date BETWEEN attempt.from_date AND attempt.through_date
   CROSS JOIN LATERAL generate_series(0,(extract(epoch FROM(d.ends_at-d.starts_at))/60/s.minutes)::integer-1)n WHERE m.job_id=job.id),
  actual AS(SELECT * FROM power_data.offered_prices(payload))
  SELECT 1 FROM expected e FULL JOIN actual a ON(a.source_id,a.starts_at,a.ends_at)=(e.source_id,e.starts_at,e.ends_at) WHERE e.source_id IS NULL OR a.source_id IS NULL
 ) OR (SELECT count(*) FROM power_data.offered_prices(payload))>100000 THEN RAISE EXCEPTION 'collection_coverage';END IF;
 -- An interval is new when no revision is published or the latest one differs.
 -- The batch exists only when something changed.
 WITH changed AS(
  SELECT o.* FROM power_data.offered_prices(payload) o
  WHERE NOT EXISTS(SELECT 1 FROM(SELECT p.ends_at,p.price,p.provider_revision FROM power_data.prices p JOIN power_data.batches b ON b.id=p.batch_id
    WHERE p.source_id=o.source_id AND p.starts_at=o.starts_at ORDER BY b.sequence DESC LIMIT 1)latest
   WHERE(latest.ends_at,latest.price,latest.provider_revision) IS NOT DISTINCT FROM(o.ends_at,o.price,o.provider_revision))),
 batch AS(INSERT INTO power_data.batches(id,catalog_id,publication_xid) SELECT token,catalog,pg_current_xact_id() WHERE EXISTS(SELECT 1 FROM changed) RETURNING id)
 INSERT INTO power_data.prices(batch_id,source_id,starts_at,ends_at,price,provider_revision,catalog_id)
 SELECT batch.id,c.source_id,c.starts_at,c.ends_at,c.price,c.provider_revision,catalog FROM changed c CROSS JOIN batch;
 -- Grant expiry is a clock boundary as well as a control-plane mutation.
 PERFORM power_data.validate_sources(job.id);
 UPDATE power_data.attempts SET state='succeeded',finished_at=clock_timestamp() WHERE id=token;
END$$;
COMMIT;
