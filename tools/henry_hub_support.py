"""Prepare, never execute, a bounded Henry Hub support bundle for an approved operator window.

The original primary bundle, observations and published library versions stay intact. Explicit supplied expiry/fixing
instants belong only to a new six-curve bundle; its caller separately admits prices, closes and exact graph references.
"""
from datetime import date, datetime
import json
import re
from uuid import UUID, uuid5


CURVES = ('hhd_daily', 'hhd_daily_basis', 'hhd_location', 'h_monthly', 'h_monthly_basis', 'h_location')
REFERENCE_TABLES = (
    'dimensions', 'currencies', 'units', 'commodities', 'commodity_units', 'measurement_bases', 'quantity_specifications',
    'unit_conversions', 'clock_rules', 'calendars', 'calendar_days', 'location_types', 'locations', 'commodity_locations',
    'pipeline_zones', 'pipeline_delivery_points', 'gas_delivery_conventions', 'gas_meter_assignments', 'crude_grades',
    'crude_delivery_points', 'exchanges', 'clearing_houses', 'exchange_clearing_houses', 'interest_rate_definitions',
    'price_index_definitions', 'sources', 'delivery_profiles', 'value_specifications', 'quote_specifications',
    'settlement_targets', 'futures_products', 'futures_series', 'futures_milestones', 'futures_product_terms',
    'futures_lot_resolutions', 'futures_basis_settlements', 'futures_execution_policies', 'product_evidence',
    'futures_option_rules', 'futures_option_products', 'trading_close_times', 'futures_option_series', 'lng_products',
    'lng_custody_points', 'lng_mass_energy_conversions', 'incoterms_editions', 'incoterms_rules')
MARKET_TABLES = ('contracts', 'curves', 'series', 'curve_members', 'curve_inputs', 'constituent_mappings',
                 'revision_periods', 'index_fixing_observations')
UUID_FIELDS = ('catalog_id', 'reference_catalog_id', 'source_reference_revision_id', 'source_market_revision_id',
               'target_reference_revision_id', 'target_market_revision_id')
CODE_FIELDS = ('revision_key', 'commodity_code', 'location_code', 'quote_code', 'calendar_code', 'clock_code',
               'source_code', 'daily_index_code', 'monthly_index_code', 'daily_target_code', 'monthly_target_code')


def validate_spec(spec):
    if set(spec) != {*UUID_FIELDS, *CODE_FIELDS, 'fixings'}:
        raise ValueError('Supply the exact Henry Hub support identities, reference codes and fixing rows.')
    try:
        for key in UUID_FIELDS:
            if str(UUID(spec[key])) != spec[key] or UUID(spec[key]).int == 0:
                raise ValueError(key)
        if spec['source_reference_revision_id'] == spec['target_reference_revision_id'] or spec['source_market_revision_id'] == spec['target_market_revision_id']:
            raise ValueError('A support bundle requires new reference and market revisions.')
        for key in CODE_FIELDS:
            if not isinstance(spec[key], str) or not re.fullmatch(r'[a-z][a-z0-9_.-]{0,63}', spec[key]):
                raise ValueError(key)
        if spec['daily_index_code'] == spec['monthly_index_code'] or spec['daily_target_code'] == spec['monthly_target_code']:
            raise ValueError('Daily and monthly identities must be distinct.')
        rows = spec['fixings']
        if not isinstance(rows, list) or not 2 <= len(rows) <= 367:
            raise ValueError('Supply every existing physical contract explicitly.')
        if {row.get('curve') for row in rows} != {'hhd_location', 'h_location'}:
            raise ValueError('Supply daily and monthly physical curve periods.')
        seen = set()
        for row in rows:
            if set(row) != {'curve', 'contract_id', 'from', 'to', 'expiry_at', 'fixing_at'}:
                raise ValueError('Every physical contract needs exact coverage, expiry and fixing instants.')
            contract = str(UUID(row['contract_id']))
            if contract in seen or contract != row['contract_id'] or UUID(contract).int == 0:
                raise ValueError('Physical contract identities must be distinct.')
            seen.add(contract)
            start, end = (date.fromisoformat(row[k]) for k in ('from', 'to'))
            if start >= end:
                raise ValueError('Coverage must be ordered.')
            for key in ('expiry_at', 'fixing_at'):
                if not re.fullmatch(r'\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z', row[key]):
                    raise ValueError('Supply exact UTC instants, never an inferred expiry or publication time.')
                datetime.fromisoformat(row[key])
    except (KeyError, TypeError, AttributeError) as error:
        raise ValueError('Malformed Henry Hub support specification.') from error


def prepare_sql(spec):
    """Return one atomic, replay-checked SQL artifact. The caller must review and explicitly execute it separately."""
    validate_spec(spec)
    literal = lambda value: "'" + str(value).replace("'", "''") + "'"
    array = lambda values: 'ARRAY[' + ','.join(literal(value) for value in values) + ']'
    rows = []
    for fixing in spec['fixings']:
        series = str(uuid5(UUID(spec['catalog_id']), 'henry_publication_series_v1:' + fixing['contract_id'] + ':' + spec['quote_code']))
        rows.append(dict(curve=fixing['curve'], contract_id=fixing['contract_id'], delivery_from=fixing['from'], delivery_to=fixing['to'],
                         expiry_at=fixing['expiry_at'], fixing_at=fixing['fixing_at'], series_id=series, series_code='henry_fix_' + series.replace('-', '')))
    return f"""-- Prepared Henry Hub support only: no observation, close, activation or primary-manifest mutation.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='60s';
SET LOCAL search_path=pg_catalog;
SELECT pg_advisory_xact_lock(730849102);
CREATE TEMP TABLE henry_fixings(curve text,contract_id uuid,delivery_from date,delivery_to date,expiry_at timestamptz,
 fixing_at timestamptz,series_id uuid,series_code text) ON COMMIT DROP;
INSERT INTO henry_fixings SELECT * FROM jsonb_populate_recordset(NULL::henry_fixings,{literal(json.dumps(rows))}::jsonb);
DO $support$
DECLARE s jsonb:={literal(json.dumps(spec))}::jsonb;t text;predicate text;expected jsonb;actual jsonb;ref_exists boolean;market_exists boolean;
 c uuid:=(s->>'catalog_id')::uuid;r uuid:=(s->>'reference_catalog_id')::uuid;
 old_ref uuid:=(s->>'source_reference_revision_id')::uuid;new_ref uuid:=(s->>'target_reference_revision_id')::uuid;
 old_market uuid:=(s->>'source_market_revision_id')::uuid;new_market uuid:=(s->>'target_market_revision_id')::uuid;
 selected_curves text[]={array(CURVES)};refs text[]={array(REFERENCE_TABLES)};markets text[]={array(MARKET_TABLES)};
BEGIN
 IF security.reference_display_tables() IS DISTINCT FROM refs THEN RAISE EXCEPTION 'henry_support_reference_schema';END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.revisions WHERE(catalog_id,id,state)=(r,old_ref,'sealed'))
 OR NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id,reference_catalog_id,reference_revision_id,state)=(c,old_market,r,old_ref,'sealed'))
 THEN RAISE EXCEPTION 'henry_support_source';END IF;
 IF(SELECT count(*) FROM market_data.curves WHERE catalog_id=c AND revision_id=old_market AND code=ANY(selected_curves))<>6
 OR EXISTS(SELECT 1 FROM market_data.curves WHERE catalog_id=c AND revision_id=old_market AND code=ANY(selected_curves)
  AND(definition_id IS NOT NULL OR(reference_catalog_id,reference_revision_id)<>(r,old_ref)))
 OR EXISTS(SELECT 1 FROM market_data.curve_inputs WHERE catalog_id=c AND revision_id=old_market AND curve_code=ANY(selected_curves)
  AND(slot NOT IN('base','spread') OR source_curve_code<>ALL(selected_curves)))
 THEN RAISE EXCEPTION 'henry_support_curve_closure';END IF;
 IF EXISTS(SELECT 1 FROM market_data.curve_blocks WHERE catalog_id=c AND revision_id=old_market AND curve_code=ANY(selected_curves))
 OR EXISTS(SELECT 1 FROM market_data.contract_intervals i JOIN market_data.curve_members m
  ON(m.catalog_id,m.revision_id,m.contract_id)=(i.catalog_id,i.revision_id,i.contract_id)
  WHERE m.catalog_id=c AND m.revision_id=old_market AND m.curve_code=ANY(selected_curves))
 THEN RAISE EXCEPTION 'henry_support_interval_curve';END IF;
 IF EXISTS(SELECT 1 FROM market_data.curve_expiry_rules WHERE catalog_id=c AND revision_id=old_market AND curve_code=ANY(selected_curves))
 THEN RAISE EXCEPTION 'henry_support_existing_expiry_rule';END IF;
 IF EXISTS(SELECT 1 FROM henry_fixings f LEFT JOIN market_data.curve_members m
  ON(m.catalog_id,m.revision_id,m.curve_code,m.contract_id)=(c,old_market,f.curve,f.contract_id)
  LEFT JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
  WHERE m.contract_id IS NULL OR k.kind<>'forward' OR(k.delivery_from,k.delivery_to)<>(f.delivery_from,f.delivery_to))
 OR EXISTS(SELECT 1 FROM market_data.curve_members m WHERE m.catalog_id=c AND m.revision_id=old_market
  AND m.curve_code IN('hhd_location','h_location') AND NOT EXISTS(SELECT 1 FROM henry_fixings f WHERE(f.curve,f.contract_id)=(m.curve_code,m.contract_id)))
 THEN RAISE EXCEPTION 'henry_support_coverage';END IF;
 IF EXISTS(SELECT 1 FROM market_data.curves WHERE catalog_id=c AND revision_id=old_market AND code IN('hhd_location','h_location')
  AND(commodity_code,location_code,quote_code)<>(s->>'commodity_code',s->>'location_code',s->>'quote_code'))
 OR NOT EXISTS(SELECT 1 FROM ref_data.calendars WHERE(catalog_id,revision_id,code)=(r,old_ref,s->>'calendar_code'))
 OR NOT EXISTS(SELECT 1 FROM ref_data.clock_rules WHERE(catalog_id,revision_id,code)=(r,old_ref,s->>'clock_code'))
 OR NOT EXISTS(SELECT 1 FROM ref_data.sources WHERE(catalog_id,revision_id,code)=(r,old_ref,s->>'source_code'))
 THEN RAISE EXCEPTION 'henry_support_exact_reference';END IF;

 -- Build expected rows in temporary tables first; source tables are read-only throughout.
 FOREACH t IN ARRAY refs LOOP
  EXECUTE format('CREATE TEMP TABLE %I(LIKE ref_data.%I) ON COMMIT DROP','henry_ref_'||t,t);
  EXECUTE format('INSERT INTO %I SELECT(jsonb_populate_record(NULL::ref_data.%I,to_jsonb(x)||jsonb_build_object(''revision_id'',$3))).* FROM ref_data.%I x WHERE catalog_id=$1 AND revision_id=$2','henry_ref_'||t,t,t) USING r,old_ref,new_ref;
 END LOOP;
 IF EXISTS(SELECT 1 FROM henry_ref_price_index_definitions WHERE code IN(s->>'daily_index_code',s->>'monthly_index_code'))
 OR EXISTS(SELECT 1 FROM henry_ref_settlement_targets WHERE code IN(s->>'daily_target_code',s->>'monthly_target_code'))
 THEN RAISE EXCEPTION 'henry_support_new_reference_collision';END IF;
 INSERT INTO henry_ref_price_index_definitions
 SELECT r,new_ref,x.index_code,q.commodity_code,s->>'location_code',q.currency_code,u.unit_code,q.multiplier,s->>'calendar_code'
 FROM henry_ref_quote_specifications q JOIN henry_ref_quantity_specifications u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.quantity_code)
 CROSS JOIN(VALUES(s->>'daily_index_code'),(s->>'monthly_index_code'))x(index_code)
 WHERE q.code=s->>'quote_code' AND q.commodity_code=s->>'commodity_code' AND q.kind='outright';
 IF(SELECT count(*) FROM henry_ref_price_index_definitions WHERE code IN(s->>'daily_index_code',s->>'monthly_index_code'))<>2
 THEN RAISE EXCEPTION 'henry_support_quote';END IF;
 INSERT INTO henry_ref_settlement_targets VALUES(r,new_ref,s->>'daily_target_code','index',s->>'daily_index_code',NULL),
  (r,new_ref,s->>'monthly_target_code','index',s->>'monthly_index_code',NULL);
 FOREACH t IN ARRAY markets LOOP
  predicate=CASE t
   WHEN 'contracts' THEN 'x.id IN(SELECT contract_id FROM market_data.curve_members WHERE catalog_id=$1 AND revision_id=$2 AND curve_code=ANY($5))'
   WHEN 'series' THEN 'x.contract_id IN(SELECT contract_id FROM market_data.curve_members WHERE catalog_id=$1 AND revision_id=$2 AND curve_code=ANY($5))'
   -- Only the explicitly supplied publication bindings belong to this bundle.
   WHEN 'index_fixing_observations' THEN 'false'
   WHEN 'revision_periods' THEN 'true'
   WHEN 'curves' THEN 'x.code=ANY($5)'
   ELSE 'x.curve_code=ANY($5)' END;
  EXECUTE format('CREATE TEMP TABLE %I(LIKE market_data.%I) ON COMMIT DROP','henry_market_'||t,t);
  EXECUTE format('INSERT INTO %I SELECT(jsonb_populate_record(NULL::market_data.%I,to_jsonb(x)||jsonb_build_object(''revision_id'',$3)||CASE WHEN to_jsonb(x)->>''reference_revision_id''=$6::text AND to_jsonb(x)->>''reference_catalog_id''=$7::text THEN jsonb_build_object(''reference_revision_id'',$4) ELSE ''{{}}''::jsonb END)).* FROM market_data.%I x WHERE x.catalog_id=$1 AND x.revision_id=$2 AND %s','henry_market_'||t,t,t,predicate)
   USING c,old_market,new_market,new_ref,selected_curves,old_ref,r;
 END LOOP;
 UPDATE henry_market_contracts k SET expiry_at=f.expiry_at FROM henry_fixings f WHERE k.id=f.contract_id;
 UPDATE henry_market_curves SET settlement_target_code=CASE code WHEN 'hhd_location' THEN s->>'daily_target_code' ELSE s->>'monthly_target_code' END
  WHERE code IN('hhd_location','h_location');
 INSERT INTO henry_market_series
 SELECT c,new_market,f.series_id,f.series_code,r,new_ref,f.contract_id,s->>'quote_code',NULL FROM henry_fixings f
 WHERE NOT EXISTS(SELECT 1 FROM henry_market_series e WHERE(e.contract_id,e.quote_code)=(f.contract_id,s->>'quote_code'));
 INSERT INTO henry_market_index_fixing_observations
 SELECT c,new_market,CASE f.curve WHEN 'hhd_location' THEN s->>'daily_index_code' ELSE s->>'monthly_index_code' END,
 f.delivery_from,f.delivery_to,r,new_ref,s->>'quote_code',e.id,s->>'source_code',f.fixing_at,'published_index_observations_v1'
 FROM henry_fixings f JOIN henry_market_series e ON(e.contract_id,e.quote_code)=(f.contract_id,s->>'quote_code');

 SELECT EXISTS(SELECT 1 FROM ref_data.revisions WHERE(catalog_id,id)=(r,new_ref)) INTO ref_exists;
 SELECT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id)=(c,new_market)) INTO market_exists;
 IF ref_exists<>market_exists THEN RAISE EXCEPTION 'henry_support_replay_conflict';END IF;
 IF NOT ref_exists THEN
  INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES(r,new_ref,s->>'revision_key');
  FOREACH t IN ARRAY refs LOOP EXECUTE format('INSERT INTO ref_data.%I SELECT * FROM %I',t,'henry_ref_'||t);END LOOP;
  UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=(r,new_ref);
  INSERT INTO market_data.revisions VALUES(c,new_market,r,new_ref,'draft',0);
  FOREACH t IN ARRAY markets LOOP EXECUTE format('INSERT INTO market_data.%I SELECT * FROM %I',t,'henry_market_'||t);END LOOP;
  UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=(c,new_market);
 END IF;
 IF NOT EXISTS(SELECT 1 FROM ref_data.revisions WHERE(catalog_id,id,revision_key,state)=(r,new_ref,s->>'revision_key','sealed'))
 OR NOT EXISTS(SELECT 1 FROM market_data.revisions WHERE(catalog_id,id,reference_catalog_id,reference_revision_id,state)=(c,new_market,r,new_ref,'sealed'))
 THEN RAISE EXCEPTION 'henry_support_replay_conflict';END IF;
 FOREACH t IN ARRAY refs LOOP
  EXECUTE format('SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),''[]''::jsonb) FROM %I x','henry_ref_'||t) INTO expected;
  EXECUTE format('SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),''[]''::jsonb) FROM ref_data.%I x WHERE catalog_id=$1 AND revision_id=$2',t) INTO actual USING r,new_ref;
  IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'henry_support_replay_conflict: reference %',t;END IF;
 END LOOP;
 FOREACH t IN ARRAY markets LOOP
  EXECUTE format('SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),''[]''::jsonb) FROM %I x','henry_market_'||t) INTO expected;
  EXECUTE format('SELECT coalesce(jsonb_agg(to_jsonb(x) ORDER BY to_jsonb(x)::text),''[]''::jsonb) FROM market_data.%I x WHERE catalog_id=$1 AND revision_id=$2',t) INTO actual USING c,new_market;
  IF actual IS DISTINCT FROM expected THEN RAISE EXCEPTION 'henry_support_replay_conflict: market %',t;END IF;
 END LOOP;
END $support$;
COMMIT;
"""
