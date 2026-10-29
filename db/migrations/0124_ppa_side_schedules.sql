-- Shared whole-term PPA sides retain their own concrete schedule parameters.
-- Existing definitions must be rebooked; retained histories are never rewritten.
BEGIN;
SELECT 1 FROM security.epoch FOR UPDATE;
SELECT pg_advisory_xact_lock(730849102);
DO $$BEGIN
 IF EXISTS(SELECT 1 FROM trading.ppa_sides) OR EXISTS(SELECT 1 FROM trading.ppa_instruments)
  OR EXISTS(SELECT 1 FROM trading.ppa_trade_revisions)
 THEN RAISE EXCEPTION 'whole_term_ppa_rebooking_required' USING ERRCODE='P0001';END IF;
END$$;

CREATE TABLE trading.ppa_side_schedules (
 catalog_id uuid NOT NULL,id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 reference_catalog_id uuid NOT NULL,reference_revision_id uuid NOT NULL,
 clock_code ref_data.code NOT NULL,interval_minutes integer NOT NULL CHECK(interval_minutes IN(15,60)),
 delivery_from date NOT NULL,delivery_to date NOT NULL,cadence text NOT NULL CHECK(cadence IN('native_intervals','operating_months')),
 hours_membership text CHECK(hours_membership IN('all','peak','off')),tou_product_id uuid,tou_version integer,holiday_calendar_code ref_data.code,
 implementation text NOT NULL CHECK(implementation='interval_side_schedule_parameters_v1'),
 economic_hash text NOT NULL,publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,id),
 UNIQUE(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes,delivery_from,delivery_to),
 FOREIGN KEY(catalog_id,definition_revision_id,reference_catalog_id,reference_revision_id) REFERENCES market_data.revisions(catalog_id,id,reference_catalog_id,reference_revision_id),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,clock_code) REFERENCES ref_data.clock_rules(catalog_id,revision_id,code),
 FOREIGN KEY(reference_catalog_id,reference_revision_id,holiday_calendar_code) REFERENCES ref_data.calendars(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,tou_product_id,tou_version) REFERENCES market_data.tou_product_versions(catalog_id,product_id,version),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order),
 CHECK(delivery_from<delivery_to AND extract(day FROM delivery_from)=1 AND extract(day FROM delivery_to)=1),
 CHECK((cadence='operating_months' AND num_nonnulls(hours_membership,tou_product_id,tou_version,holiday_calendar_code)=0) OR
       (cadence='native_intervals' AND hours_membership IS NOT NULL AND
        ((hours_membership='all' AND tou_product_id IS NULL AND tou_version IS NULL) OR
         (hours_membership IN('peak','off') AND tou_product_id IS NOT NULL AND tou_version IS NOT NULL AND holiday_calendar_code IS NOT NULL))))
);
CREATE INDEX ppa_schedule_economics ON trading.ppa_side_schedules(catalog_id,economic_hash);
ALTER TABLE trading.ppa_side_schedules ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.ppa_side_schedules FORCE ROW LEVEL SECURITY;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.ppa_side_schedules
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE POLICY scoped_read ON trading.ppa_side_schedules FOR SELECT TO openetrm_runtime
 USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY temporal_read ON trading.ppa_side_schedules AS RESTRICTIVE FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY reference_read ON trading.ppa_side_schedules AS RESTRICTIVE FOR SELECT TO openetrm_runtime
 USING(security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY risk_worker_scoped_read ON trading.ppa_side_schedules FOR SELECT TO openetrm_risk_worker
 USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY risk_worker_reference_read ON trading.ppa_side_schedules AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker
 USING(security.reference_version_visible(reference_catalog_id,reference_revision_id));
CREATE POLICY risk_worker_service ON trading.ppa_side_schedules AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
GRANT SELECT ON trading.ppa_side_schedules TO openetrm_runtime,openetrm_risk_worker;
GRANT SELECT,INSERT ON trading.ppa_side_schedules TO openetrm_security_owner;

ALTER TABLE trading.ppa_sides ADD COLUMN schedule_id uuid NOT NULL,
 ADD COLUMN definition_version text NOT NULL,
 ADD CHECK(definition_version=CASE kind WHEN 'fixed' THEN 'FixedSide_v4' WHEN 'indexed' THEN 'FloatingSide_v8'
  WHEN 'energy' THEN 'power_physical_side_v2' WHEN 'certificate' THEN 'ppa_certificate_delivery_v2' WHEN 'capacity' THEN 'ppa_capacity_delivery_v2' END),
 ADD FOREIGN KEY(catalog_id,schedule_id,definition_revision_id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes,delivery_from,delivery_to)
 REFERENCES trading.ppa_side_schedules(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes,delivery_from,delivery_to);
-- Normalization belongs to every reusable side, including slot payments.
ALTER TABLE trading.ppa_sides
 ADD COLUMN normalization_basis_code ref_data.code NOT NULL,
 ADD COLUMN normalization_policy text NOT NULL CHECK(normalization_policy IN('contract_unit_v1','sum_capacity_in_force_v1')),
 ADD COLUMN settlement_mode text CHECK(settlement_mode IN('curve_target','override')),
 ADD COLUMN settlement_target_index_code ref_data.code,
 ADD COLUMN settlement_index_code ref_data.code,
 ADD COLUMN settlement_projection_policy text CHECK(settlement_projection_policy='forward_curve_no_basis_v1'),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,normalization_basis_code) REFERENCES ref_data.quantity_specifications(catalog_id,revision_id,code),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,settlement_target_index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 ADD FOREIGN KEY(reference_catalog_id,reference_revision_id,settlement_index_code) REFERENCES ref_data.price_index_definitions(catalog_id,revision_id,code),
 ADD CHECK((settlement_mode IS NULL AND num_nonnulls(settlement_target_index_code,settlement_index_code,settlement_projection_policy)=0) OR
  (kind='indexed' AND settlement_mode='curve_target' AND settlement_target_index_code IS NOT NULL AND settlement_index_code IS NOT NULL AND settlement_target_index_code=settlement_index_code AND settlement_projection_policy IS NULL) OR
  (kind='indexed' AND settlement_mode='override' AND settlement_target_index_code IS NOT NULL AND settlement_index_code IS NOT NULL AND settlement_target_index_code<>settlement_index_code AND settlement_projection_policy IS NOT NULL));
CREATE TABLE trading.ppa_side_normalization_assets (
 catalog_id uuid NOT NULL,side_id uuid NOT NULL,definition_revision_id uuid NOT NULL,
 ordinal integer NOT NULL CHECK(ordinal BETWEEN 0 AND 99),asset_code ref_data.code NOT NULL,
 publication_order integer NOT NULL,effective_at ref_data.instant NOT NULL,
 PRIMARY KEY(catalog_id,side_id,ordinal),UNIQUE(catalog_id,side_id,asset_code),
 FOREIGN KEY(catalog_id,side_id,definition_revision_id) REFERENCES trading.ppa_sides(catalog_id,id,definition_revision_id),
 FOREIGN KEY(catalog_id,definition_revision_id,asset_code) REFERENCES market_data.generation_assets(catalog_id,revision_id,code),
 FOREIGN KEY(catalog_id,publication_order) REFERENCES trading.publications(catalog_id,publication_order)
);
ALTER TABLE trading.ppa_side_normalization_assets ENABLE ROW LEVEL SECURITY;
ALTER TABLE trading.ppa_side_normalization_assets FORCE ROW LEVEL SECURITY;
CREATE TRIGGER immutable_record BEFORE UPDATE OR DELETE ON trading.ppa_side_normalization_assets
 FOR EACH ROW EXECUTE FUNCTION market_data.guard_knowledge_receipt();
CREATE POLICY scoped_read ON trading.ppa_side_normalization_assets FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY temporal_read ON trading.ppa_side_normalization_assets AS RESTRICTIVE FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(security.trading_visible(catalog_id,publication_order,effective_at));
CREATE POLICY parent_read ON trading.ppa_side_normalization_assets AS RESTRICTIVE FOR SELECT TO openetrm_runtime,openetrm_risk_worker
 USING(EXISTS(SELECT 1 FROM trading.ppa_sides s WHERE(s.catalog_id,s.id)=(ppa_side_normalization_assets.catalog_id,ppa_side_normalization_assets.side_id)));
CREATE POLICY risk_worker_service ON trading.ppa_side_normalization_assets AS RESTRICTIVE FOR SELECT TO openetrm_risk_worker USING(risk.worker_read());
GRANT SELECT ON trading.ppa_side_normalization_assets TO openetrm_runtime,openetrm_risk_worker;
GRANT SELECT,INSERT ON trading.ppa_side_normalization_assets TO openetrm_security_owner;
ALTER TABLE trading.ppa_instruments DROP CONSTRAINT ppa_instruments_implementation_check;
ALTER TABLE trading.ppa_instruments ADD CHECK(implementation='ppa_instrument_v2');

-- Pure structural resolution from the submitted exact term references. A slot
-- binds a delivery shape; it never selects a price, meter value or current data.
CREATE FUNCTION security.ppa_schedule_document(spec jsonb,side jsonb) RETURNS jsonb
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE SET search_path=pg_catalog AS $$
DECLARE q jsonb;h jsonb;slot jsonb;target jsonb;selected jsonb;shape jsonb;count_targets integer;cadence text;
BEGIN
 q=side->'quantity';
 IF side->>'kind' IS NULL OR side->>'kind' NOT IN('fixed','indexed','energy','certificate','capacity') OR jsonb_typeof(spec->'delivery') IS DISTINCT FROM 'object' OR spec#>>'{delivery,clock}' IS NULL OR
  spec#>>'{delivery,intervalMinutes}' IS NULL OR spec#>>'{delivery,from}' IS NULL OR spec#>>'{delivery,to}' IS NULL OR
  spec->>'definitionRevisionId' IS NULL OR spec->>'referenceCatalogId' IS NULL OR spec->>'referenceRevisionId' IS NULL OR
  jsonb_typeof(q) IS DISTINCT FROM 'object' THEN RAISE EXCEPTION 'ppa_side_schedule' USING ERRCODE='P0001';END IF;
 IF q->>'kind'='slots' THEN
  IF side->>'kind' NOT IN('fixed','indexed') OR jsonb_typeof(q->'slots') IS DISTINCT FROM 'array' OR jsonb_array_length(q->'slots')=0 OR
   jsonb_typeof(spec->'memberships') IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'ppa_side_schedule' USING ERRCODE='P0001';END IF;
  FOR slot IN SELECT value FROM jsonb_array_elements(q->'slots') LOOP
   SELECT count(*),jsonb_agg(m->'side')->0 INTO count_targets,target FROM jsonb_array_elements(spec->'memberships') m
    WHERE(m->>'role',m->>'ordinal')=(slot->>'role',slot->>'ordinal');
   IF count_targets<>1 OR target->>'kind' IS NULL OR target->>'kind' NOT IN('energy','certificate','capacity') OR target#>>'{quantity,kind}'='slots' OR
    NOT((slot->>'role'='energy_delivery' AND target->>'kind'='energy') OR
        (slot->>'role'='certificate_delivery' AND target->>'kind'='certificate') OR
        (slot->>'role'='capacity_delivery' AND target->>'kind'='capacity'))
   THEN RAISE EXCEPTION 'ppa_side_schedule' USING ERRCODE='P0001';END IF;
   selected=security.ppa_schedule_document(spec,target);
   IF shape IS NOT NULL AND shape<>selected THEN RAISE EXCEPTION 'ppa_side_schedule' USING ERRCODE='P0001';END IF;
   shape=selected;
  END LOOP;
  RETURN shape;
 ELSIF q->>'kind'='profile' THEN
  h=q->'hours';cadence='native_intervals';
  IF jsonb_typeof(h) IS DISTINCT FROM 'object' OR h->>'membership' IS NULL OR h->>'membership' NOT IN('all','peak','off') OR h->>'calendar' IS NULL OR
   (h->>'membership'='all')<>(h->>'touProduct' IS NULL) OR(h->>'touProduct' IS NULL)<>(h->>'touVersion' IS NULL)
  THEN RAISE EXCEPTION 'ppa_side_schedule' USING ERRCODE='P0001';END IF;
 ELSIF q->>'kind'='volume' THEN
  cadence='native_intervals';h=jsonb_build_object('membership','all','touProduct',NULL,'touVersion',NULL,'calendar',NULL);
 ELSIF q->>'kind'='accredited' AND side->>'kind'='capacity' THEN
  cadence='operating_months';h='null'::jsonb;
 ELSE RAISE EXCEPTION 'ppa_side_schedule' USING ERRCODE='P0001';END IF;
 RETURN jsonb_build_object('definitionRevisionId',spec->'definitionRevisionId','referenceCatalogId',spec->'referenceCatalogId',
  'referenceRevisionId',spec->'referenceRevisionId','clock',spec#>'{delivery,clock}','intervalMinutes',spec#>'{delivery,intervalMinutes}',
  'from',spec#>'{delivery,from}','to',spec#>'{delivery,to}','cadence',cadence,'membership',h->'membership',
  'touProduct',h->'touProduct','touVersion',h->'touVersion','calendar',h->'calendar','implementation','interval_side_schedule_parameters_v1');
END$$;

CREATE FUNCTION security.retain_ppa_schedule(catalog uuid,document jsonb,publication integer,from_at timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE found uuid;d uuid;rc uuid;rr uuid;tou uuid;touv integer;cal text;clock text;starts date;ends date;minutes integer;selected_cadence text;selected_membership text;
BEGIN
 PERFORM pg_advisory_xact_lock(730849102);
 d=(document->>'definitionRevisionId')::uuid;rc=(document->>'referenceCatalogId')::uuid;rr=(document->>'referenceRevisionId')::uuid;
 clock=document->>'clock';starts=(document->>'from')::date;ends=(document->>'to')::date;minutes=(document->>'intervalMinutes')::integer;
 selected_cadence=document->>'cadence';selected_membership=document->>'membership';tou=(document->>'touProduct')::uuid;touv=(document->>'touVersion')::integer;cal=document->>'calendar';
 IF NOT security.reference_access(rc,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 IF document->>'implementation' IS DISTINCT FROM 'interval_side_schedule_parameters_v1' OR
  (tou IS NOT NULL AND NOT EXISTS(SELECT 1 FROM market_data.tou_product_versions t WHERE(t.catalog_id,t.product_id,t.version)=(catalog,tou,touv)
   AND t.state='published' AND(t.clock_catalog_id,t.clock_revision_id,t.clock_code)=(rc,rr,clock)))
 THEN RAISE EXCEPTION 'ppa_side_schedule' USING ERRCODE='P0001';END IF;
 SELECT s.id INTO found FROM trading.ppa_side_schedules s
 WHERE s.catalog_id=catalog AND s.economic_hash=md5(document::text) AND
  (s.definition_revision_id,s.reference_catalog_id,s.reference_revision_id,s.clock_code,s.interval_minutes,s.delivery_from,s.delivery_to,s.cadence,s.implementation)=
  (d,rc,rr,clock,minutes,starts,ends,selected_cadence,'interval_side_schedule_parameters_v1') AND
  (s.hours_membership,s.tou_product_id,s.tou_version,s.holiday_calendar_code) IS NOT DISTINCT FROM(selected_membership,tou,touv,cal);
 IF found IS NOT NULL THEN RETURN found;END IF;
 found=gen_random_uuid();
 INSERT INTO trading.ppa_side_schedules VALUES(catalog,found,d,rc,rr,clock,minutes,starts,ends,selected_cadence,selected_membership,tou,touv,cal,
  'interval_side_schedule_parameters_v1',md5(document::text),publication,from_at);
 RETURN found;
END$$;
CREATE OR REPLACE FUNCTION security.retain_ppa_terms(catalog uuid,spec jsonb,publication integer,from_at timestamptz) RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d uuid;rc uuid;rr uuid;delivery jsonb;member jsonb;side jsonb;q jsonb;h jsonb;f jsonb;ix jsonb;a jsonb;item jsonb;
 candidate uuid;instrument uuid;side_id uuid;resolved_schedule uuid;schedule_document jsonb;rule uuid;position integer;common jsonb;key text;month_count integer;payment_month date;
 selected_curve text;shaped_code text;settlement jsonb;settlement_mode text;settlement_target text;settlement_index text;settlement_policy text;normalization_choice text;term_from date;term_to date;minutes integer;normal text[];derived text[];
 energy_count integer;strike_count integer;index_count integer;capacity_count integer;capacity_payment_count integer;
 certificate_count integer;certificate_payment_count integer;charge_count integer;indexes jsonb[];
BEGIN
 spec=security.ppa_normalize(spec);
 PERFORM security.ppa_keys(spec,ARRAY['definitionRevisionId','referenceCatalogId','referenceRevisionId','delivery','currency','unitQuantity','discounting','implementation','normalizationAssets','memberships']);
 d=(spec->>'definitionRevisionId')::uuid;rc=(spec->>'referenceCatalogId')::uuid;rr=(spec->>'referenceRevisionId')::uuid;delivery=spec->'delivery';
 PERFORM security.ppa_keys(delivery,ARRAY['clock','intervalMinutes','from','to']);
 term_from=(delivery->>'from')::date;term_to=(delivery->>'to')::date;minutes=(delivery->>'intervalMinutes')::integer;
 month_count=(extract(year FROM term_to)::integer-extract(year FROM term_from)::integer)*12+extract(month FROM term_to)::integer-extract(month FROM term_from)::integer;
 IF spec->>'implementation' IS DISTINCT FROM 'ppa_instrument_v2' OR jsonb_typeof(spec->'discounting')<>'boolean' OR minutes NOT IN(15,60) OR minutes IS NULL OR
  term_from IS NULL OR term_to IS NULL OR term_from<DATE '2000-01-01' OR term_to>DATE '2100-01-01' OR extract(day FROM term_from)<>1 OR extract(day FROM term_to)<>1 OR month_count<1 OR month_count>(CASE WHEN minutes=60 THEN 360 ELSE 120 END) OR
  jsonb_typeof(spec->'normalizationAssets')<>'array' OR jsonb_array_length(spec->'normalizationAssets')>100 OR jsonb_typeof(spec->'memberships')<>'array' OR jsonb_array_length(spec->'memberships') NOT BETWEEN 2 AND 8
 THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
 IF NOT security.reference_access(rc,'ReadMarketData') THEN RAISE EXCEPTION 'No current reference access' USING ERRCODE='42501';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id)=(catalog,d) AND state='sealed') THEN RAISE EXCEPTION 'ppa_definition_revision' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id,reference_catalog_id,reference_revision_id)=(catalog,d,rc,rr)) OR
  NOT EXISTS(SELECT 1 FROM ref_data.revisions WHERE(catalog_id,id)=(rc,rr) AND state='sealed') THEN RAISE EXCEPTION 'ppa_reference_revision' USING ERRCODE='P0001';END IF;
 -- Reusable definitions exist from their exact bundle's effective boundary,
 -- independently of which trade happens to retain them first.
 SELECT p.effective_from::timestamp AT TIME ZONE 'UTC' INTO from_at FROM market_data.revision_periods p
  WHERE(p.catalog_id,p.revision_id)=(catalog,d) AND p.effective_from<=(from_at AT TIME ZONE 'UTC')::date AND(from_at AT TIME ZONE 'UTC')::date<p.effective_to;
 IF from_at IS NULL THEN RAISE EXCEPTION 'ppa_definition_revision' USING ERRCODE='P0001';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.quantity_specifications WHERE(catalog_id,revision_id,code)=(rc,rr,spec->>'unitQuantity') AND unit_code='megawatt' AND dimension_code='power') THEN RAISE EXCEPTION 'ppa_quantity_unit' USING ERRCODE='P0001';END IF;
 PERFORM security.ppa_day_start(rc,rr,delivery->>'clock',term_from);PERFORM security.ppa_day_start(rc,rr,delivery->>'clock',term_to);
 IF EXISTS(SELECT 1 FROM jsonb_array_elements(spec->'normalizationAssets') v WHERE jsonb_typeof(v)<>'string') OR
  (SELECT count(DISTINCT v) FROM jsonb_array_elements(spec->'normalizationAssets') v)<>jsonb_array_length(spec->'normalizationAssets') THEN RAISE EXCEPTION 'ppa_normalization' USING ERRCODE='P0001';END IF;
 SELECT coalesce(array_agg(v ORDER BY v),ARRAY[]::text[]) INTO normal FROM jsonb_array_elements_text(spec->'normalizationAssets') v;
 SELECT coalesce(array_agg(DISTINCT asset ORDER BY asset),ARRAY[]::text[]) INTO derived FROM(
  SELECT s.asset_code::text asset FROM jsonb_array_elements(spec->'memberships') m CROSS JOIN LATERAL jsonb_array_elements_text(m#>'{side,quantity,sources}') source(code)
   JOIN market_data.volume_sources s ON(s.catalog_id,s.revision_id,s.code)=(catalog,d,source.code)
  UNION SELECT asset.code FROM jsonb_array_elements(spec->'memberships') m CROSS JOIN LATERAL jsonb_array_elements_text(m#>'{side,quantity,assets}') asset(code)
 ) assets;
 IF normal<>derived THEN RAISE EXCEPTION 'ppa_normalization' USING ERRCODE='P0001';END IF;
 IF EXISTS(SELECT 1 FROM unnest(normal) asset(code) LEFT JOIN market_data.generation_assets ga ON(ga.catalog_id,ga.revision_id,ga.code)=(catalog,d,asset.code) WHERE ga.code IS NULL OR ga.clock_code<>delivery->>'clock') THEN RAISE EXCEPTION 'ppa_reference_revision' USING ERRCODE='P0001';END IF;
 -- Exact reconstructed equality, not the hash, establishes reuse. The transaction lock serializes retention.
 FOR candidate IN SELECT instrument_id FROM trading.ppa_instruments WHERE catalog_id=catalog AND economic_hash=md5(spec::text) LOOP
  IF security.ppa_terms_document(catalog,candidate)=spec THEN RETURN candidate;END IF;
 END LOOP;
 IF (SELECT count(DISTINCT(m->>'role',(m->>'ordinal')::integer)) FROM jsonb_array_elements(spec->'memberships') m)<>jsonb_array_length(spec->'memberships') THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
 common=spec-ARRAY['discounting','implementation','memberships'];
 normalization_choice=CASE WHEN cardinality(normal)=0 THEN 'contract_unit_v1' ELSE 'sum_capacity_in_force_v1' END;
 instrument=gen_random_uuid();
 INSERT INTO trading.instruments VALUES(catalog,instrument,publication,from_at);
 INSERT INTO trading.ppa_instruments VALUES(catalog,instrument,d,rc,rr,delivery->>'clock',minutes,term_from,term_to,spec->>'currency',spec->>'unitQuantity',(spec->>'discounting')::boolean,'ppa_instrument_v2',md5(spec::text),publication,from_at);
 INSERT INTO trading.ppa_instrument_assets SELECT catalog,instrument,d,n::integer-1,v,publication,from_at FROM jsonb_array_elements_text(spec->'normalizationAssets') WITH ORDINALITY x(v,n);
 position=0;
 FOR member IN SELECT v FROM jsonb_array_elements(spec->'memberships') v LOOP
  PERFORM security.ppa_keys(member,ARRAY['role','ordinal','coefficient','side']);side=member->'side';
  PERFORM security.ppa_keys(side,ARRAY['kind','quantity','paymentRule','location','fixedPrice','indexPrice','attribute']);
  IF security.ppa_number(member->>'coefficient') NOT IN(-1,1) OR(member->>'ordinal')::integer NOT BETWEEN 0 AND 7 THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
  IF NOT((member->>'role'='energy_delivery' AND side->>'kind'='energy') OR(member->>'role'='certificate_delivery' AND side->>'kind'='certificate') OR
    (member->>'role'='capacity_delivery' AND side->>'kind'='capacity') OR(member->>'role' IN('strike_payment','fixed_charge') AND side->>'kind'='fixed') OR
    (member->>'role'='indexed_payment' AND side->>'kind'='indexed') OR(member->>'role' IN('certificate_payment','capacity_payment') AND side->>'kind' IN('fixed','indexed')))
  THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
  q=side->'quantity';h=q->'hours';f=side->'fixedPrice';ix=side->'indexPrice';a=side->'attribute';
  PERFORM security.ppa_keys(q,ARRAY['kind','sources','stage','capacity','energy','hours','slots','assets']);
  IF jsonb_typeof(q->'sources')<>'array' OR jsonb_typeof(q->'slots')<>'array' OR jsonb_typeof(q->'assets')<>'array' OR jsonb_array_length(q->'sources')>100 OR jsonb_array_length(q->'assets')>100 OR jsonb_array_length(q->'slots')>8 OR
   (q->>'kind'='volume')<>(jsonb_array_length(q->'sources')>0) OR(q->>'kind'='accredited')<>(jsonb_array_length(q->'assets')>0) OR(q->>'kind'='slots')<>(jsonb_array_length(q->'slots')>0) OR
   (q->>'kind'='profile')<>(h<>'null'::jsonb) OR(q->>'kind'='volume')<>(q->>'stage' IS NOT NULL) OR
   (q->>'kind'='profile')<>(q->>'capacity' IS NOT NULL) OR(q->>'kind'='profile')<>(q->>'energy' IS NOT NULL) OR
   q->>'kind' NOT IN('volume','profile','slots','accredited') OR
   (q->>'kind'='slots' AND side->>'kind' NOT IN('fixed','indexed')) OR(side->>'kind'='certificate' AND q->>'kind'<>'volume') OR(side->>'kind'='capacity' AND q->>'kind'<>'accredited')
  THEN RAISE EXCEPTION 'ppa_quantity_source' USING ERRCODE='P0001';END IF;
  IF q->>'kind'='volume' THEN
   IF q->>'stage' NOT IN('generated','delivered') OR (SELECT count(DISTINCT v) FROM jsonb_array_elements(q->'sources') v)<>jsonb_array_length(q->'sources') OR
    EXISTS(SELECT 1 FROM jsonb_array_elements_text(q->'sources') source(code) LEFT JOIN market_data.volume_sources s ON(s.catalog_id,s.revision_id,s.code)=(catalog,d,source.code)
     WHERE s.code IS NULL OR s.clock_code<>delivery->>'clock' OR s.interval_minutes<>minutes) THEN RAISE EXCEPTION 'ppa_quantity_source' USING ERRCODE='P0001';END IF;
  END IF;
  IF q->>'kind'='profile' THEN
   PERFORM security.ppa_keys(h,ARRAY['membership','touProduct','touVersion','calendar']);
   IF h->>'membership' NOT IN('all','peak','off') OR h->>'calendar' IS NULL OR(h->>'membership'='all')<>(h->>'touProduct' IS NULL) OR(h->>'touProduct' IS NULL)<>(h->>'touVersion' IS NULL) OR
    (h->>'touProduct' IS NOT NULL AND NOT EXISTS(SELECT 1 FROM market_data.tou_product_versions t WHERE(t.catalog_id,t.product_id,t.version)=(catalog,(h->>'touProduct')::uuid,(h->>'touVersion')::integer)
      AND t.state='published' AND(t.clock_catalog_id,t.clock_revision_id,t.clock_code)=(rc,rr,delivery->>'clock')))
   THEN RAISE EXCEPTION 'ppa_delivery_hours' USING ERRCODE='P0001';END IF;
  END IF;
  IF member->>'role' IN('certificate_payment','capacity_payment','fixed_charge') AND q->>'kind'<>'slots'
  THEN RAISE EXCEPTION 'ppa_quantity_reference' USING ERRCODE='P0001';END IF;
  IF (side->>'kind'='energy')<>(side->>'location' IS NOT NULL) OR(side->>'kind'='fixed')<>(f<>'null'::jsonb) OR(side->>'kind'='indexed')<>(ix<>'null'::jsonb) OR(side->>'kind' IN('certificate','capacity'))<>(a<>'null'::jsonb)
  THEN RAISE EXCEPTION 'ppa_input' USING ERRCODE='P0001';END IF;
  IF side->>'kind'='fixed' THEN
   PERFORM security.ppa_keys(f,ARRAY['kind','quantity','multiplier','price','rate','baseDate','basis','steps']);
   IF f->>'kind' NOT IN('flat','escalated','stepped') OR security.ppa_number(f->>'multiplier')<=0 OR jsonb_typeof(f->'steps')<>'array' OR jsonb_array_length(f->'steps')>360 OR
    (f->>'kind'='stepped')<>(jsonb_array_length(f->'steps')>0) OR(f->>'kind'<>'stepped')<>(f->>'price' IS NOT NULL) OR
    (f->>'kind'='escalated')<>(f->>'rate' IS NOT NULL) OR(f->>'kind'='escalated')<>(f->>'baseDate' IS NOT NULL) OR(f->>'kind'='escalated')<>(f->>'basis' IS NOT NULL)
   THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
   IF f->>'kind'='escalated' AND(f->>'basis'<>'anniversary_step_v1' OR security.ppa_number(f->>'rate')<=-1 OR to_char((f->>'baseDate')::date,'MM-DD')='02-29') THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
   IF f->>'kind'='stepped' THEN
    IF (SELECT min((v->>'from')::date) FROM jsonb_array_elements(f->'steps') v)>term_from OR
     (SELECT count(DISTINCT v->>'from') FROM jsonb_array_elements(f->'steps') v)<>jsonb_array_length(f->'steps') THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
    FOR item IN SELECT v FROM jsonb_array_elements(f->'steps') v LOOP PERFORM security.ppa_keys(item,ARRAY['from','price']);PERFORM security.ppa_number(item->>'price');END LOOP;
   END IF;
  END IF;
  selected_curve=NULL;shaped_code=NULL;settlement_mode=NULL;settlement_target=NULL;settlement_index=NULL;settlement_policy=NULL;
  IF side->>'kind'='indexed' THEN
   PERFORM security.ppa_keys(ix,ARRAY['curve','quantity','multiplier','indexMultiplier','adder','settlement']);
   IF ix->>'quantity' IS NULL OR ix->>'adder' IS NULL OR security.ppa_number(ix->>'multiplier')<=0 OR security.ppa_number(ix->>'indexMultiplier')<=0 THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
   SELECT code INTO selected_curve FROM market_data.curves WHERE(catalog_id,revision_id,code)=(catalog,d,ix->>'curve');
   SELECT code INTO shaped_code FROM market_data.shaped_curves WHERE(catalog_id,revision_id,code)=(catalog,d,ix->>'curve');
   IF(selected_curve IS NULL)=(shaped_code IS NULL) THEN RAISE EXCEPTION 'ppa_price_rule' USING ERRCODE='P0001';END IF;
   settlement=ix->'settlement';
   IF shaped_code IS NOT NULL OR EXISTS(SELECT 1 FROM market_data.curve_members m JOIN market_data.contract_intervals i
      ON(i.catalog_id,i.revision_id,i.contract_id)=(m.catalog_id,m.revision_id,m.contract_id)
      WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,d,selected_curve)) THEN
    IF settlement IS DISTINCT FROM 'null'::jsonb THEN RAISE EXCEPTION 'price_settlement_selection' USING ERRCODE='P0001';END IF;
   ELSE
    PERFORM security.ppa_keys(settlement,ARRAY['mode','index','projectionPolicy']);
    SELECT t.index_a_code INTO settlement_target FROM market_data.curves c JOIN ref_data.settlement_targets t
     ON(t.catalog_id,t.revision_id,t.code)=(c.reference_catalog_id,c.reference_revision_id,c.settlement_target_code)
     WHERE(c.catalog_id,c.revision_id,c.code)=(catalog,d,selected_curve) AND t.kind='index';
    settlement_mode=settlement->>'mode';settlement_policy=settlement->>'projectionPolicy';
    IF settlement_target IS NULL OR settlement_mode IS NULL OR settlement_mode NOT IN('curve_target','override') THEN RAISE EXCEPTION 'price_settlement_selection' USING ERRCODE='P0001';END IF;
    IF settlement_mode='curve_target' THEN
     IF settlement->>'index' IS NOT NULL OR settlement_policy IS NOT NULL THEN RAISE EXCEPTION 'price_settlement_selection' USING ERRCODE='P0001';END IF;
     settlement_index=settlement_target;
    ELSE
     settlement_index=settlement->>'index';
     IF settlement_index IS NULL OR settlement_index=settlement_target OR settlement_policy IS DISTINCT FROM 'forward_curve_no_basis_v1' OR
      NOT EXISTS(SELECT 1 FROM ref_data.price_index_definitions i JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(catalog,d,selected_curve)
       JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(rc,rr,c.quote_code)
       JOIN ref_data.quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(rc,rr,q.quantity_code)
       WHERE(i.catalog_id,i.revision_id,i.code)=(rc,rr,settlement_index) AND(i.currency_code,i.commodity_code,i.quote_unit_code,i.quote_multiplier)=(q.currency_code,c.commodity_code,u.unit_code,q.multiplier)) OR
      EXISTS(SELECT 1 FROM market_data.curve_members m JOIN market_data.contracts c ON(c.catalog_id,c.revision_id,c.id)=(m.catalog_id,m.revision_id,m.contract_id)
       JOIN market_data.curves cv ON(cv.catalog_id,cv.revision_id,cv.code)=(m.catalog_id,m.revision_id,m.curve_code)
       WHERE(m.catalog_id,m.revision_id,m.curve_code)=(catalog,d,selected_curve) AND c.delivery_from<term_to AND c.delivery_to>term_from AND
        NOT EXISTS(SELECT 1 FROM market_data.index_fixing_observations f WHERE(f.catalog_id,f.revision_id,f.index_code,f.quote_code,f.observation_from,f.observation_to)=
         (catalog,d,settlement_index,cv.quote_code,c.delivery_from,c.delivery_to)))
     THEN RAISE EXCEPTION 'price_settlement_selection' USING ERRCODE='P0001';END IF;
    END IF;
   END IF;
  END IF;
  IF side->>'kind' IN('certificate','capacity') THEN
   PERFORM security.ppa_keys(a,ARRAY['quantity','conversion','eligibility','holding','disposition','rule']);
   IF(side->>'kind'='certificate' AND(a->>'eligibility' IS DISTINCT FROM 'asset_generation_at_stage_v1' OR a->>'holding' IS DISTINCT FROM 'held_at_current_vintage_mark_v1' OR a->>'disposition' IS DISTINCT FROM 'none_recorded_v1' OR a->>'rule' IS NOT NULL OR security.ppa_number(a->>'conversion')<=0)) OR
    (side->>'kind'='capacity' AND(a->>'rule' IS DISTINCT FROM 'mw_day_per_operating_day_v1' OR a->>'conversion' IS NOT NULL OR a->>'eligibility' IS NOT NULL OR a->>'holding' IS NOT NULL OR a->>'disposition' IS NOT NULL)) THEN RAISE EXCEPTION 'ppa_term_unsupported' USING ERRCODE='P0001';END IF;
  END IF;
  -- The existing managed-compatible payment rule validator also proves calendar references.
  payment_month=term_from;
  WHILE payment_month<term_to LOOP
   PERFORM security.swap_payment_date(rc,rr,payment_month,(payment_month+INTERVAL '1 month')::date,side->'paymentRule');
   payment_month=(payment_month+INTERVAL '1 month')::date;
  END LOOP;
  schedule_document=security.ppa_schedule_document(spec,side);
  resolved_schedule=security.retain_ppa_schedule(catalog,schedule_document,publication,from_at);
  key=md5((common||jsonb_build_object('side',side,'schedule',schedule_document))::text);side_id=NULL;
  FOR candidate IN SELECT id FROM trading.ppa_sides WHERE catalog_id=catalog AND economic_hash=key AND ppa_sides.schedule_id=resolved_schedule AND(definition_revision_id,reference_catalog_id,reference_revision_id,clock_code,interval_minutes,delivery_from,delivery_to,currency_code,unit_quantity_code)=(d,rc,rr,delivery->>'clock',minutes,term_from,term_to,spec->>'currency',spec->>'unitQuantity') LOOP
   IF NOT EXISTS(SELECT 1 FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id,m.side_id)=(catalog,instrument,candidate)) AND security.ppa_side_document(catalog,candidate)=side AND
     EXISTS(SELECT 1 FROM trading.ppa_sides n WHERE(n.catalog_id,n.id,n.normalization_basis_code,n.normalization_policy)=(catalog,candidate,spec->>'unitQuantity',normalization_choice)) AND
     coalesce((SELECT jsonb_agg(n.asset_code ORDER BY n.ordinal) FROM trading.ppa_side_normalization_assets n WHERE(n.catalog_id,n.side_id)=(catalog,candidate)),'[]'::jsonb)=spec->'normalizationAssets' THEN side_id=candidate;EXIT;END IF;
  END LOOP;
  IF side_id IS NULL THEN
   rule=security.retain_swap_payment_rule(catalog,rc,rr,side->'paymentRule',publication,from_at);side_id=gen_random_uuid();
   INSERT INTO trading.ppa_sides VALUES(catalog,side_id,d,rc,rr,side->>'kind',delivery->>'clock',minutes,term_from,term_to,spec->>'currency',spec->>'unitQuantity',rule,q->>'kind',q->>'stage',q->>'capacity',q->>'energy',
    h->>'membership',(h->>'touProduct')::uuid,(h->>'touVersion')::integer,h->>'calendar',side->>'location',f->>'kind',coalesce(f->>'quantity',ix->>'quantity'),
    coalesce((f->>'multiplier')::numeric,(ix->>'multiplier')::numeric),(f->>'price')::numeric,(f->>'rate')::numeric,(f->>'baseDate')::date,f->>'basis',selected_curve,shaped_code,(ix->>'indexMultiplier')::numeric,(ix->>'adder')::numeric,
    a->>'quantity',(a->>'conversion')::numeric,a->>'eligibility',a->>'holding',a->>'disposition',a->>'rule',key,publication,from_at,resolved_schedule,
    CASE side->>'kind' WHEN 'fixed' THEN 'FixedSide_v4' WHEN 'indexed' THEN 'FloatingSide_v8' WHEN 'energy' THEN 'power_physical_side_v2' WHEN 'certificate' THEN 'ppa_certificate_delivery_v2' WHEN 'capacity' THEN 'ppa_capacity_delivery_v2' END,
    spec->>'unitQuantity',normalization_choice,settlement_mode,settlement_target,settlement_index,settlement_policy);
   INSERT INTO trading.ppa_side_normalization_assets SELECT catalog,side_id,d,n::integer-1,v,publication,from_at FROM jsonb_array_elements_text(spec->'normalizationAssets') WITH ORDINALITY x(v,n);
   INSERT INTO trading.ppa_side_sources SELECT catalog,side_id,d,n::integer-1,v,publication,from_at FROM jsonb_array_elements_text(q->'sources') WITH ORDINALITY x(v,n);
   INSERT INTO trading.ppa_side_assets SELECT catalog,side_id,d,n::integer-1,v,publication,from_at FROM jsonb_array_elements_text(q->'assets') WITH ORDINALITY x(v,n);
   FOR item IN SELECT v FROM jsonb_array_elements(q->'slots') v LOOP PERFORM security.ppa_keys(item,ARRAY['role','ordinal']);END LOOP;
   INSERT INTO trading.ppa_side_slots SELECT catalog,side_id,n::integer-1,v->>'role',(v->>'ordinal')::integer,publication,from_at FROM jsonb_array_elements(q->'slots') WITH ORDINALITY x(v,n);
   IF f->>'kind'='stepped' THEN INSERT INTO trading.ppa_price_steps SELECT catalog,side_id,n::integer-1,(v->>'from')::date,(v->>'price')::numeric,publication,from_at FROM jsonb_array_elements(f->'steps') WITH ORDINALITY x(v,n);END IF;
  END IF;
  INSERT INTO trading.ppa_memberships VALUES(catalog,instrument,position,member->>'role',(member->>'ordinal')::integer,side_id,(member->>'coefficient')::numeric,publication,from_at);position=position+1;
 END LOOP;
 -- Bind abstract reusable quantity slots to exact memberships in this instrument.
 IF EXISTS(SELECT 1 FROM trading.ppa_memberships m JOIN trading.ppa_side_slots l ON(l.catalog_id,l.side_id)=(m.catalog_id,m.side_id)
  LEFT JOIN trading.ppa_memberships target ON(target.catalog_id,target.instrument_id,target.role,target.ordinal)=(catalog,instrument,l.role,l.ordinal)
  WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND(target.side_id IS NULL OR
   (m.role IN('strike_payment','indexed_payment','fixed_charge') AND l.role<>'energy_delivery') OR(m.role='certificate_payment' AND l.role<>'certificate_delivery') OR(m.role='capacity_payment' AND l.role<>'capacity_delivery')))
 THEN RAISE EXCEPTION 'ppa_quantity_reference' USING ERRCODE='P0001';END IF;
 PERFORM security.ppa_validate_units(catalog,instrument);
 SELECT count(*) FILTER(WHERE role='energy_delivery'),count(*) FILTER(WHERE role='strike_payment'),count(*) FILTER(WHERE role='indexed_payment'),
  count(*) FILTER(WHERE role='capacity_delivery'),count(*) FILTER(WHERE role='capacity_payment'),count(*) FILTER(WHERE role='certificate_delivery'),
  count(*) FILTER(WHERE role='certificate_payment'),count(*) FILTER(WHERE role='fixed_charge')
 INTO energy_count,strike_count,index_count,capacity_count,capacity_payment_count,certificate_count,certificate_payment_count,charge_count
 FROM trading.ppa_memberships WHERE(catalog_id,instrument_id)=(catalog,instrument);
 IF certificate_count>1 OR certificate_payment_count>certificate_count OR charge_count>1 THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
 IF capacity_count=1 AND energy_count=0 AND strike_count=0 AND index_count=0 AND capacity_payment_count=1 AND certificate_count=0 AND charge_count=0 THEN RETURN instrument;END IF;
 IF capacity_count<>0 OR capacity_payment_count<>0 THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
 IF energy_count=1 AND((strike_count=1 AND index_count=0) OR(strike_count=0 AND index_count=1)) THEN
  IF EXISTS(SELECT 1 FROM trading.ppa_memberships m JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id)
   WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND m.role IN('strike_payment','indexed_payment') AND
    (s.quantity_kind<>'slots' OR(SELECT count(*) FROM trading.ppa_side_slots l WHERE(l.catalog_id,l.side_id)=(catalog,s.id))<>1))
  THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
  RETURN instrument;
 END IF;
 IF energy_count=0 AND strike_count=1 AND index_count=1 THEN RETURN instrument;END IF;
 IF energy_count=0 AND strike_count=0 AND index_count=2 AND certificate_count=0 AND charge_count=0 THEN
  SELECT array_agg(security.ppa_side_document(catalog,m.side_id) ORDER BY m.position) INTO indexes
   FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND m.role='indexed_payment';
  IF indexes[1]#>'{indexPrice,curve}'=indexes[2]#>'{indexPrice,curve}' OR indexes[1]->'quantity'<>indexes[2]->'quantity'
  THEN RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';END IF;
  RETURN instrument;
 END IF;
 RAISE EXCEPTION 'ppa_composition' USING ERRCODE='P0001';
END$$;

CREATE FUNCTION security.ppa_side_normalization_matches(catalog uuid,instrument uuid) RETURNS boolean
LANGUAGE plpgsql STABLE SET search_path=pg_catalog AS $$
DECLARE header trading.ppa_instruments;assets jsonb;expected_policy text;
BEGIN
 SELECT * INTO header FROM trading.ppa_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument);
 IF NOT FOUND THEN RETURN false;END IF;
 SELECT coalesce(jsonb_agg(a.asset_code ORDER BY a.ordinal),'[]'::jsonb) INTO assets FROM trading.ppa_instrument_assets a WHERE(a.catalog_id,a.instrument_id)=(catalog,instrument);
 expected_policy=CASE WHEN jsonb_array_length(assets)=0 THEN 'contract_unit_v1' ELSE 'sum_capacity_in_force_v1' END;
 IF NOT EXISTS(SELECT 1 FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)) OR
  EXISTS(SELECT 1 FROM trading.ppa_memberships m JOIN trading.ppa_sides s ON(s.catalog_id,s.id)=(m.catalog_id,m.side_id)
   WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument) AND(s.normalization_basis_code<>header.unit_quantity_code OR s.normalization_policy<>expected_policy OR
    coalesce((SELECT jsonb_agg(a.asset_code ORDER BY a.ordinal) FROM trading.ppa_side_normalization_assets a WHERE(a.catalog_id,a.side_id)=(catalog,s.id)),'[]'::jsonb)<>assets))
 THEN RAISE EXCEPTION 'ppa_normalization' USING ERRCODE='P0001';END IF;
 RETURN true;
END$$;
ALTER FUNCTION security.ppa_side_normalization_matches(uuid,uuid) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.ppa_side_normalization_matches(uuid,uuid) FROM PUBLIC;

CREATE OR REPLACE FUNCTION security.ppa_side_document(catalog uuid,side uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('kind',s.kind,'quantity',jsonb_build_object('kind',s.quantity_kind,
  'sources',coalesce((SELECT jsonb_agg(x.source_code ORDER BY x.ordinal) FROM trading.ppa_side_sources x WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]'),
  'stage',s.stage,'capacity',s.profile_capacity_code,'energy',s.profile_energy_code,
  'hours',CASE WHEN s.hours_membership IS NULL THEN NULL ELSE jsonb_build_object('membership',s.hours_membership,'touProduct',s.tou_product_id,'touVersion',s.tou_version,'calendar',s.holiday_calendar_code) END,
  'slots',coalesce((SELECT jsonb_agg(jsonb_build_object('role',x.role,'ordinal',x.ordinal) ORDER BY x.position) FROM trading.ppa_side_slots x WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]'),
  'assets',coalesce((SELECT jsonb_agg(x.asset_code ORDER BY x.ordinal) FROM trading.ppa_side_assets x WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]')),
  'paymentRule',jsonb_build_object('frequency',r.frequency,'rule',r.rule,'days',r.days,'dayOfMonth',r.day_of_month,'dayBasis',r.day_basis,'shortMonth',r.short_month,'adjustment',r.adjustment,'calendar',r.calendar_code,'implementation',r.implementation),
  'location',s.location_code,
  'fixedPrice',CASE WHEN s.fixed_kind IS NULL THEN NULL ELSE jsonb_build_object('kind',s.fixed_kind,'quantity',s.price_quantity_code,'multiplier',s.price_multiplier::text,'price',s.price::text,
   'rate',s.escalation_rate::text,'baseDate',s.escalation_base_date,'basis',s.escalation_basis,
   'steps',coalesce((SELECT jsonb_agg(jsonb_build_object('from',x.from_date,'price',x.price::text) ORDER BY x.ordinal) FROM trading.ppa_price_steps x WHERE(x.catalog_id,x.side_id)=(catalog,side)),'[]')) END,
  'indexPrice',CASE WHEN s.kind<>'indexed' THEN NULL ELSE jsonb_build_object('curve',coalesce(s.index_curve_code,s.index_shaped_curve_code),'quantity',s.price_quantity_code,'multiplier',s.price_multiplier::text,'indexMultiplier',s.index_multiplier::text,'adder',s.adder::text,
   'settlement',CASE WHEN s.settlement_mode IS NULL THEN NULL ELSE jsonb_build_object('mode',s.settlement_mode,'index',CASE WHEN s.settlement_mode='override' THEN s.settlement_index_code ELSE NULL END,'projectionPolicy',s.settlement_projection_policy) END) END,
  'attribute',CASE WHEN s.attribute_quantity_code IS NULL THEN NULL ELSE jsonb_build_object('quantity',s.attribute_quantity_code,'conversion',s.conversion::text,'eligibility',s.eligibility,'holding',s.holding,'disposition',s.disposition,'rule',s.capacity_rule) END)
 FROM trading.ppa_sides s JOIN trading.swap_payment_rules r ON(r.catalog_id,r.id)=(s.catalog_id,s.payment_rule_id) WHERE(s.catalog_id,s.id)=(catalog,side)
$$;

CREATE OR REPLACE FUNCTION security.ppa_terms_document(catalog uuid,instrument uuid) RETURNS jsonb LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('definitionRevisionId',i.definition_revision_id,'referenceCatalogId',i.reference_catalog_id,'referenceRevisionId',i.reference_revision_id,
  'delivery',jsonb_build_object('clock',i.clock_code,'intervalMinutes',i.interval_minutes,'from',i.delivery_from,'to',i.delivery_to),
  'currency',i.currency_code,'unitQuantity',i.unit_quantity_code,'discounting',i.discounting,'implementation',i.implementation,
  'normalizationAssets',coalesce((SELECT jsonb_agg(a.asset_code ORDER BY a.ordinal) FROM trading.ppa_instrument_assets a WHERE(a.catalog_id,a.instrument_id)=(catalog,instrument)),'[]'),
  'memberships',(SELECT jsonb_agg(jsonb_build_object('role',m.role,'ordinal',m.ordinal,'coefficient',m.coefficient::text,'side',security.ppa_side_document(catalog,m.side_id)) ORDER BY m.position)
   FROM trading.ppa_memberships m WHERE(m.catalog_id,m.instrument_id)=(catalog,instrument)))
 FROM trading.ppa_instruments i WHERE(i.catalog_id,i.instrument_id)=(catalog,instrument) AND security.ppa_side_normalization_matches(catalog,instrument)
$$;

ALTER FUNCTION security.ppa_schedule_document(jsonb,jsonb) OWNER TO openetrm_security_owner;
ALTER FUNCTION security.retain_ppa_schedule(uuid,jsonb,integer,timestamptz) OWNER TO openetrm_security_owner;
REVOKE ALL ON FUNCTION security.ppa_schedule_document(jsonb,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION security.retain_ppa_schedule(uuid,jsonb,integer,timestamptz) FROM PUBLIC;
COMMIT;
