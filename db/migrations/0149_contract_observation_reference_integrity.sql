-- Bind every retained contract observation definition to its exact reference
-- bundle, including prompt selection whose optional contract key is NULL.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
SET LOCAL search_path=pg_catalog;
SELECT 1 FROM security.epoch WHERE id=true FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);

ALTER TABLE trading.swap_contract_observation_terms
 ADD CONSTRAINT swap_observation_terms_definition_reference
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id);
ALTER TABLE trading.swap_contract_roll_boundaries
 ADD CONSTRAINT swap_roll_boundaries_definition_reference
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id);
ALTER TABLE trading.swap_contract_observations
 ADD CONSTRAINT swap_observations_definition_reference
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id);

-- Child snapshots must also agree with their exact owning period/terms, not
-- merely name some independently valid definition/reference bundle.
ALTER TABLE trading.swap_side_periods
 ADD CONSTRAINT swap_side_period_snapshot UNIQUE
 (catalog_id,side_id,ordinal,definition_revision_id,reference_catalog_id,reference_revision_id);
ALTER TABLE trading.swap_contract_observation_terms
 ADD CONSTRAINT swap_observation_terms_snapshot UNIQUE
 (catalog_id,side_id,period_ordinal,definition_revision_id,reference_catalog_id,reference_revision_id),
 ADD CONSTRAINT swap_observation_terms_period_snapshot
 FOREIGN KEY(catalog_id,side_id,period_ordinal,definition_revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES trading.swap_side_periods(catalog_id,side_id,ordinal,definition_revision_id,reference_catalog_id,reference_revision_id);
ALTER TABLE trading.swap_contract_roll_boundaries
 ADD CONSTRAINT swap_roll_boundaries_terms_snapshot
 FOREIGN KEY(catalog_id,side_id,period_ordinal,definition_revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES trading.swap_contract_observation_terms(catalog_id,side_id,period_ordinal,definition_revision_id,reference_catalog_id,reference_revision_id);
ALTER TABLE trading.swap_contract_observations
 ADD CONSTRAINT swap_observations_terms_snapshot
 FOREIGN KEY(catalog_id,side_id,period_ordinal,definition_revision_id,reference_catalog_id,reference_revision_id)
 REFERENCES trading.swap_contract_observation_terms(catalog_id,side_id,period_ordinal,definition_revision_id,reference_catalog_id,reference_revision_id);

ALTER TABLE market_data.series
 ADD CONSTRAINT series_contract_identity UNIQUE(catalog_id,revision_id,id,contract_id);
ALTER TABLE trading.swap_contract_observations
 ADD CONSTRAINT swap_observations_series_contract
 FOREIGN KEY(catalog_id,definition_revision_id,series_id,contract_id)
 REFERENCES market_data.series(catalog_id,revision_id,id,contract_id);
COMMIT;
