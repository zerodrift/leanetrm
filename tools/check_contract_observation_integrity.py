"""Exact definition/reference foreign keys for retained contract observations."""


def run(query):
    # The ordinary booking just completed with prompt selection and no selected
    # contract. Its definition must still be constrained despite that NULL key.
    assert query("""SELECT EXISTS(SELECT 1 FROM trading.swap_contract_observation_terms
        WHERE selection_mode='prompt_offset' AND selected_contract_id IS NULL);""") == 't'
    check_snapshots(query)
    # Direct stored-row mutations isolate FK enforcement from append-only policy.
    # Only this disposable transaction suspends the immutable trigger; rollback
    # restores it and all rows. Application commands never disable that trigger.
    query("""BEGIN;
ALTER TABLE trading.swap_contract_observation_terms DISABLE TRIGGER immutable_record;
DO $check$
DECLARE selected trading.swap_contract_observation_terms;
 alternate_reference uuid='20000000-0000-0000-0000-000000889136';
 rejected text;
BEGIN
 SELECT * INTO STRICT selected FROM trading.swap_contract_observation_terms
  WHERE selection_mode='prompt_offset' AND selected_contract_id IS NULL LIMIT 1;
 INSERT INTO ref_data.revisions(catalog_id,id,revision_key)
 VALUES(selected.reference_catalog_id,alternate_reference,'synthetic_integrity_probe');
 BEGIN
  UPDATE trading.swap_contract_observation_terms SET definition_revision_id='60000000-0000-0000-0000-000000889136'
   WHERE(catalog_id,side_id,period_ordinal)=(selected.catalog_id,selected.side_id,selected.period_ordinal);
  RAISE EXCEPTION 'A stored prompt selection admitted a nonexistent definition';
 EXCEPTION WHEN foreign_key_violation THEN
  GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
  IF rejected NOT IN('swap_observation_terms_definition_reference','swap_observation_terms_period_snapshot',
   'swap_roll_boundaries_terms_snapshot','swap_observations_terms_snapshot') THEN RAISE;END IF;
 END;
 BEGIN
  UPDATE trading.swap_contract_observation_terms SET reference_revision_id=alternate_reference
   WHERE(catalog_id,side_id,period_ordinal)=(selected.catalog_id,selected.side_id,selected.period_ordinal);
  RAISE EXCEPTION 'A stored prompt selection admitted another reference bundle';
 EXCEPTION WHEN foreign_key_violation THEN
  GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
  IF rejected NOT IN('swap_observation_terms_definition_reference','swap_observation_terms_period_snapshot',
   'swap_roll_boundaries_terms_snapshot','swap_observations_terms_snapshot') THEN RAISE;END IF;
 END;
END $check$;
ROLLBACK;""")
    check_parent_snapshots(query)
    check_series_contract(query)
    print('PASS stored prompt integrity: direct invalid definition/reference mutations rejected and rolled back', flush=True)


def check_snapshots(query):
    query("""BEGIN;
DO $check$
DECLARE item record; definition text; selected market_data.revisions;
 alternate_reference uuid='20000000-0000-0000-0000-000000889136';
 missing uuid='60000000-0000-0000-0000-000000889136';
BEGIN
 SELECT * INTO STRICT selected FROM market_data.revisions ORDER BY catalog_id,id LIMIT 1;
 INSERT INTO ref_data.revisions(catalog_id,id,revision_key)
 VALUES(selected.reference_catalog_id,alternate_reference,'synthetic_integrity_probe');
 FOR item IN SELECT * FROM(VALUES
  ('swap_contract_observation_terms','swap_observation_terms_definition_reference'),
  ('swap_contract_roll_boundaries','swap_roll_boundaries_definition_reference'),
  ('swap_contract_observations','swap_observations_definition_reference')) AS targets(table_name,constraint_name)
 LOOP
  SELECT pg_get_constraintdef(oid) INTO STRICT definition FROM pg_constraint
   WHERE conrelid=('trading.'||item.table_name)::regclass AND conname=item.constraint_name
    AND contype='f' AND convalidated AND confrelid='market_data.revisions'::regclass;
  IF definition IS DISTINCT FROM 'FOREIGN KEY (catalog_id, definition_revision_id, reference_catalog_id, reference_revision_id) REFERENCES market_data.revisions(catalog_id, id, reference_catalog_id, reference_revision_id)'
  THEN RAISE EXCEPTION 'Retained observations require the exact definition/reference foreign key';END IF;
  -- Exercise each actual installed constraint independently of other ownership
  -- constraints. The probe has no selected-contract FK that could hide a gap.
  CREATE TEMP TABLE revision_probe(catalog_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
   reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,selected_contract_id uuid) ON COMMIT DROP;
  EXECUTE 'ALTER TABLE pg_temp.revision_probe ADD CONSTRAINT snapshot_reference '||definition;
  INSERT INTO revision_probe VALUES(selected.catalog_id,selected.id,selected.reference_catalog_id,selected.reference_revision_id,NULL);
  BEGIN
   UPDATE revision_probe SET definition_revision_id=missing;
   RAISE EXCEPTION 'A nonexistent definition revision was admitted';
  EXCEPTION WHEN foreign_key_violation THEN NULL;END;
  BEGIN
   UPDATE revision_probe SET catalog_id=missing;
   RAISE EXCEPTION 'A different definition catalog was admitted';
  EXCEPTION WHEN foreign_key_violation THEN NULL;END;
  BEGIN
   UPDATE revision_probe SET reference_catalog_id=missing;
   RAISE EXCEPTION 'A different reference catalog was admitted';
  EXCEPTION WHEN foreign_key_violation THEN NULL;END;
  BEGIN
   UPDATE revision_probe SET reference_revision_id=alternate_reference;
   RAISE EXCEPTION 'An existing but mismatched reference revision was admitted';
  EXCEPTION WHEN foreign_key_violation THEN NULL;END;
  DROP TABLE revision_probe;
 END LOOP;
END $check$;
ROLLBACK;""")
    print('PASS contract observation integrity: exact snapshot foreign keys, nullable prompt contract, and wrong/missing revision rejection', flush=True)


def check_parent_snapshots(query):
    query("""BEGIN;
ALTER TABLE trading.swap_contract_observation_terms DISABLE TRIGGER immutable_record;
DO $check$
DECLARE item record; definition text; selected trading.swap_contract_observation_terms;
 alternate_definition uuid='60000000-0000-0000-0000-000000889136'; rejected text;
BEGIN
 SELECT * INTO STRICT selected FROM trading.swap_contract_observation_terms
  WHERE selection_mode='prompt_offset' AND selected_contract_id IS NULL LIMIT 1;
 INSERT INTO market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id,state,generation)
 VALUES(selected.catalog_id,alternate_definition,selected.reference_catalog_id,selected.reference_revision_id,'draft',0);
 -- This replacement is a real definition/reference bundle, but not the owning
 -- period's bundle. The exact parent link must reject it on the production row.
 BEGIN
  UPDATE trading.swap_contract_observation_terms SET definition_revision_id=alternate_definition
   WHERE(catalog_id,side_id,period_ordinal)=(selected.catalog_id,selected.side_id,selected.period_ordinal);
  RAISE EXCEPTION 'A prompt selection admitted a different valid parent snapshot';
 EXCEPTION WHEN foreign_key_violation THEN
  GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
  IF rejected NOT IN('swap_observation_terms_period_snapshot','swap_roll_boundaries_terms_snapshot','swap_observations_terms_snapshot') THEN RAISE;END IF;
 END;
 FOR item IN SELECT * FROM(VALUES
  ('swap_contract_observation_terms','swap_observation_terms_period_snapshot','swap_side_periods'),
  ('swap_contract_roll_boundaries','swap_roll_boundaries_terms_snapshot','swap_contract_observation_terms'),
  ('swap_contract_observations','swap_observations_terms_snapshot','swap_contract_observation_terms')) AS targets(table_name,constraint_name,parent_name)
 LOOP
  SELECT pg_get_constraintdef(oid) INTO STRICT definition FROM pg_constraint
   WHERE conrelid=('trading.'||item.table_name)::regclass AND conname=item.constraint_name
    AND contype='f' AND convalidated AND confrelid=('trading.'||item.parent_name)::regclass;
  IF definition IS DISTINCT FROM 'FOREIGN KEY (catalog_id, side_id, period_ordinal, definition_revision_id, reference_catalog_id, reference_revision_id) REFERENCES trading.'||item.parent_name||
   '(catalog_id, side_id, '||CASE WHEN item.parent_name='swap_side_periods' THEN 'ordinal' ELSE 'period_ordinal' END||', definition_revision_id, reference_catalog_id, reference_revision_id)'
  THEN RAISE EXCEPTION 'Parent snapshot relationship must retain every exact coordinate';END IF;
  CREATE TEMP TABLE parent_probe(catalog_id uuid NOT NULL,side_id uuid NOT NULL,period_ordinal integer NOT NULL,
   definition_revision_id uuid NOT NULL,reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL) ON COMMIT DROP;
  EXECUTE 'ALTER TABLE pg_temp.parent_probe ADD CONSTRAINT parent_snapshot '||definition;
  INSERT INTO parent_probe VALUES(selected.catalog_id,selected.side_id,selected.period_ordinal,
   selected.definition_revision_id,selected.reference_catalog_id,selected.reference_revision_id);
  BEGIN
   UPDATE parent_probe SET definition_revision_id=alternate_definition;
   RAISE EXCEPTION 'A child admitted a different valid parent snapshot';
  EXCEPTION WHEN foreign_key_violation THEN NULL;END;
  DROP TABLE parent_probe;
 END LOOP;
END $check$;
ROLLBACK;""")
    print('PASS observation parent integrity: terms match the owning period and child snapshots match their terms', flush=True)


def check_series_contract(query):
    query("""BEGIN;
ALTER TABLE trading.swap_contract_observations DISABLE TRIGGER immutable_record;
DO $check$
DECLARE selected trading.swap_contract_observations; wrong_series uuid; rejected text; definition text;
BEGIN
 SELECT * INTO STRICT selected FROM trading.swap_contract_observations LIMIT 1;
 SELECT id INTO STRICT wrong_series FROM market_data.series
  WHERE(catalog_id,revision_id)=(selected.catalog_id,selected.definition_revision_id)
   AND contract_id IS NOT NULL AND contract_id<>selected.contract_id LIMIT 1;
 SELECT pg_get_constraintdef(oid) INTO STRICT definition FROM pg_constraint
  WHERE conrelid='trading.swap_contract_observations'::regclass AND conname='swap_observations_series_contract'
   AND contype='f' AND convalidated AND confrelid='market_data.series'::regclass;
 IF definition IS DISTINCT FROM 'FOREIGN KEY (catalog_id, definition_revision_id, series_id, contract_id) REFERENCES market_data.series(catalog_id, revision_id, id, contract_id)'
 THEN RAISE EXCEPTION 'Observed series must belong to its exact selected contract';END IF;
 BEGIN
  UPDATE trading.swap_contract_observations SET series_id=wrong_series
   WHERE(catalog_id,side_id,period_ordinal,ordinal)=(selected.catalog_id,selected.side_id,selected.period_ordinal,selected.ordinal);
  RAISE EXCEPTION 'An observation admitted another existing contract series';
 EXCEPTION WHEN foreign_key_violation THEN
  GET STACKED DIAGNOSTICS rejected=CONSTRAINT_NAME;
  IF rejected<>'swap_observations_series_contract' THEN RAISE;END IF;
 END;
END $check$;
ROLLBACK;""")
    print('PASS observation series integrity: another valid contract series is rejected on the stored observation', flush=True)
