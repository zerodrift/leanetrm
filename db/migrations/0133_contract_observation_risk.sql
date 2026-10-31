-- Extend the existing connected sample evidence with its explicit consumer and
-- current-observation freeze identity. No market value is copied into a new table.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
ALTER TABLE risk.job_curve_samples ADD COLUMN kind text NOT NULL DEFAULT 'recipe',ADD COLUMN freeze_observation boolean NOT NULL DEFAULT false;
ALTER TABLE risk.job_curve_sample_observations ADD COLUMN kind text NOT NULL DEFAULT 'recipe',ADD COLUMN freeze_observation boolean NOT NULL DEFAULT false;
DO $$DECLARE item record;BEGIN
 FOR item IN SELECT conname FROM pg_constraint WHERE conrelid='risk.job_curve_sample_observations'::regclass AND confrelid='risk.job_curve_samples'::regclass LOOP
  EXECUTE format('ALTER TABLE risk.job_curve_sample_observations DROP CONSTRAINT %I',item.conname);
 END LOOP;
 FOR item IN SELECT conname FROM pg_constraint WHERE conrelid='risk.job_curve_sample_observations'::regclass AND contype='p' LOOP
  EXECUTE format('ALTER TABLE risk.job_curve_sample_observations DROP CONSTRAINT %I',item.conname);
 END LOOP;
 FOR item IN SELECT conname FROM pg_constraint WHERE conrelid='risk.job_curve_samples'::regclass AND contype IN('p','u') LOOP
  EXECUTE format('ALTER TABLE risk.job_curve_samples DROP CONSTRAINT %I',item.conname);
 END LOOP;
END$$;
ALTER TABLE risk.job_curve_samples
 ALTER COLUMN kind DROP DEFAULT,ALTER COLUMN freeze_observation DROP DEFAULT,
 ADD CHECK(kind IN('recipe','contract_observation') AND (NOT freeze_observation OR kind='contract_observation' AND NOT freeze_historical)),
 ADD PRIMARY KEY(job_id,revision_id,snapshot_id,source_curve,contract_id,kind,freeze_observation),
 ADD UNIQUE(catalog_id,job_id,revision_id,snapshot_id,source_curve,contract_id,kind,freeze_observation);
ALTER TABLE risk.job_curve_sample_observations
 ALTER COLUMN kind DROP DEFAULT,ALTER COLUMN freeze_observation DROP DEFAULT,
 ADD PRIMARY KEY(job_id,revision_id,snapshot_id,source_curve,contract_id,kind,freeze_observation,series_id,input_use),
 ADD FOREIGN KEY(catalog_id,job_id,revision_id,snapshot_id,source_curve,contract_id,kind,freeze_observation)
  REFERENCES risk.job_curve_samples(catalog_id,job_id,revision_id,snapshot_id,source_curve,contract_id,kind,freeze_observation);

CREATE FUNCTION risk.require_contract_observation_sample(job risk.jobs,selected_revision uuid,sample jsonb) RETURNS void
LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE candidate record;at timestamptz=(sample->>'at')::timestamptz;frozen boolean=(sample->>'freezeObservation')::boolean;
 observation jsonb;pin uuid;series uuid;use_name text;
BEGIN
 IF jsonb_array_length(sample->'observations')<>1 THEN RAISE EXCEPTION 'risk_graph_input';END IF;
 observation=sample->'observations'->0;pin=(observation->>'id')::uuid;series=(observation->>'seriesId')::uuid;use_name=observation->>'use';
 FOR candidate IN
  WITH heads AS(SELECT DISTINCT ON(r.trade_id) r.* FROM trading.swap_trade_revisions r
   WHERE r.catalog_id=job.catalog_id AND security.ppa_visible_at(r.catalog_id,r.publication_order,r.effective_at,job.context_id,job.valuation_at)
   ORDER BY r.trade_id,r.revision DESC)
  SELECT DISTINCT h.instrument_id,a.schedule_id FROM heads h
  JOIN risk.job_books book ON(book.catalog_id,book.job_id,book.book_id)=(h.catalog_id,job.id,h.book_id)
  JOIN trading.instrument_side_schedules a ON(a.catalog_id,a.instrument_id)=(h.catalog_id,h.instrument_id)
  JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.operation)=(a.catalog_id,a.schedule_id,'contract_observation_price')
  JOIN trading.side_schedule_bindings m ON(m.catalog_id,m.schedule_id,m.row_ordinal,m.port,m.kind)=(r.catalog_id,r.schedule_id,r.ordinal,'market','contract_price')
  JOIN trading.side_schedule_bindings s ON(s.catalog_id,s.schedule_id,s.row_ordinal,s.port,s.kind)=(r.catalog_id,r.schedule_id,r.ordinal,'settlement','settlement_source')
  JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.source_catalog_id,m.definition_revision_id,m.contract_id)
  JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.id,o.series_id,o.source_code)=(s.source_catalog_id,s.definition_revision_id,pin,series,s.source_code)
  WHERE(m.source_catalog_id,m.definition_revision_id,m.curve_code,m.contract_id)=(job.catalog_id,selected_revision,sample->>'sourceCurve',(sample->>'contractId')::uuid)
   AND c.kind='future' AND s.coordinate='DailySettlement' AND s.series_id=series AND at=least((r.parameters->>'sampleAt')::timestamptz,job.valuation_at)
   AND frozen=((r.parameters->>'sampleAt')::timestamptz=job.valuation_at AND at<c.expiry_at)
   AND use_name=CASE WHEN at>=c.expiry_at THEN 'FrozenFact' ELSE 'Variable' END
   AND o.publication_state='final' AND o.observed_at<=at AND security.market_fact_visible(job.catalog_id,selected_revision,pin)
   AND CASE WHEN at>=c.expiry_at THEN
     r.parameters->>'expiryPolicy'='CarryFinalSettlement' AND o.role='final_contract_settlement' AND o.observed_at>=c.expiry_at
    WHEN (r.parameters->>'sampleAt')::timestamptz<=job.valuation_at THEN
     o.role='daily_settlement' AND o.observed_at=(r.parameters->>'sampleAt')::timestamptz
    ELSE o.role='daily_settlement' AND(
     selected_revision=job.revision_id AND EXISTS(SELECT 1 FROM risk.job_observations p WHERE(p.catalog_id,p.job_id,p.observation_id)=(job.catalog_id,job.id,pin)) OR
     EXISTS(SELECT 1 FROM risk.job_market_observations p WHERE(p.catalog_id,p.job_id,p.revision_id,p.observation_id)=(job.catalog_id,job.id,selected_revision,pin))) END
   AND ((r.parameters->>'sampleAt')::timestamptz>job.valuation_at AND at<c.expiry_at OR NOT EXISTS(
    SELECT 1 FROM market_data.observations successor WHERE successor.catalog_id=o.catalog_id AND successor.corrects_id=o.id
     AND security.market_fact_visible(successor.catalog_id,successor.definition_revision_id,successor.id)))
 LOOP
  PERFORM security.require_side_schedule_read(job.catalog_id,candidate.instrument_id,candidate.schedule_id,job.context_id,job.valuation_at);
  RETURN;
 END LOOP;
 RAISE EXCEPTION 'risk_graph_input';
END$$;
ALTER FUNCTION risk.require_contract_observation_sample(risk.jobs,uuid,jsonb) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION risk.require_contract_observation_sample(risk.jobs,uuid,jsonb) FROM PUBLIC;

DO $migration$
DECLARE definition text;changed text;before text;after text;
BEGIN
 SELECT pg_get_functiondef('risk.retain_connected(risk.jobs,jsonb)'::regprocedure) INTO definition;
 before=$old$OR jsonb_typeof(sample->'freezeHistorical') IS DISTINCT FROM 'boolean'$old$;
 after=$new$OR jsonb_typeof(sample->'freezeHistorical') IS DISTINCT FROM 'boolean'
    OR sample->>'kind' IS NULL OR sample->>'kind' NOT IN('recipe','contract_observation')
    OR jsonb_typeof(sample->'freezeObservation') IS DISTINCT FROM 'boolean'
    OR sample->>'kind'='recipe' AND (sample->>'freezeObservation')::boolean$new$;
 changed=replace(definition,before,after);IF changed=definition THEN RAISE EXCEPTION 'contract_risk_migration_shape';END IF;definition=changed;
 before=$old$OR NOT EXISTS(SELECT 1 FROM risk.job_custom_curves jc CROSS JOIN LATERAL security.custom_curve_native_sources(jc.catalog_id,jc.curve_id,jc.version) cs
     WHERE jc.job_id=job.id AND jc.revision_id=revision AND(cs.source_catalog_id,cs.source_revision_id,cs.source_curve_code)=(job.catalog_id,revision,source))$old$;
 after=$new$OR sample->>'kind'='recipe' AND NOT EXISTS(SELECT 1 FROM risk.job_custom_curves jc CROSS JOIN LATERAL security.custom_curve_native_sources(jc.catalog_id,jc.curve_id,jc.version) cs
     WHERE jc.job_id=job.id AND jc.revision_id=revision AND(cs.source_catalog_id,cs.source_revision_id,cs.source_curve_code)=(job.catalog_id,revision,source))$new$;
 changed=replace(definition,before,after);IF changed=definition THEN RAISE EXCEPTION 'contract_risk_migration_shape';END IF;definition=changed;
 before=$old$INSERT INTO risk.job_curve_samples VALUES(job.catalog_id,job.id,revision,sample_id,source,contract,selected_sample_at,(sample->>'freezeHistorical')::boolean);$old$;
 after=$new$IF sample->>'kind'='contract_observation' THEN PERFORM risk.require_contract_observation_sample(job,revision,sample);END IF;
   INSERT INTO risk.job_curve_samples VALUES(job.catalog_id,job.id,revision,sample_id,source,contract,selected_sample_at,(sample->>'freezeHistorical')::boolean,sample->>'kind',(sample->>'freezeObservation')::boolean);$new$;
 changed=replace(definition,before,after);IF changed=definition THEN RAISE EXCEPTION 'contract_risk_migration_shape';END IF;definition=changed;
 before=$old$INSERT INTO risk.job_curve_sample_observations VALUES(job.catalog_id,job.id,revision,sample_id,source,contract,pin,series,use_name);$old$;
 after=$new$INSERT INTO risk.job_curve_sample_observations VALUES(job.catalog_id,job.id,revision,sample_id,source,contract,pin,series,use_name,sample->>'kind',(sample->>'freezeObservation')::boolean);$new$;
 changed=replace(definition,before,after);IF changed=definition THEN RAISE EXCEPTION 'contract_risk_migration_shape';END IF;
 EXECUTE changed;
END$migration$;
COMMIT;
