"""Retained admission-schema fixture for an owned policy-migration check only."""
import copy


def prepare(reference,market,manifest):
    r=manifest['referenceCatalogId'];rev=manifest['referenceRevisionId'];source='20000000-0000-0000-0000-000000000091'
    reference=reference.replace("'Synthetic fixed-lot gas future','synthetic'","'Synthetic fixed-lot gas future','candidate'")
    reference=reference.replace("'2021-01','synthetic'","'2021-01','candidate'")
    reference=reference.replace("'first_trading','known',DATE '2019-12-01',TIMESTAMPTZ '2019-12-01 15:30:00+00'","'first_trading','known',DATE '2020-11-30',NULL")
    reference=reference.replace('license_reference,distribution_review)','license_reference,distribution_review,content_sha256)').replace("'redistributable' FROM fixture_context","'redistributable',repeat('a',64) FROM fixture_context").replace('synthetic:reference-fixture-v1','generated:pilot-close-v1/synthetic')
    market='\n'.join(line.replace("'synthetic'","'candidate'") if 'INSERT INTO ref_data.futures_series' in line else line for line in market.splitlines())
    reference_sql=f"""
INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES('{r}','{source}','synthetic_native_evidence');
INSERT INTO ref_data.exchanges VALUES('{r}','{source}','test_exchange','Synthetic venue');
INSERT INTO ref_data.futures_products(catalog_id,revision_id,code,name,exchange_code) VALUES('{r}','{source}','test_gas_future','Synthetic source future','test_exchange');
INSERT INTO ref_data.futures_series(catalog_id,revision_id,code,product_code,series_label) SELECT catalog_id,'{source}',code,product_code,series_label FROM ref_data.futures_series WHERE catalog_id='{r}' AND revision_id='{rev}' AND product_code='test_gas_future';
UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{r}' AND id='{source}';
INSERT INTO ref_data.sources VALUES('{r}','{rev}','qualification','manual_entry','operator:synthetic-qualified-terms',clock_timestamp(),repeat('b',64),NULL,'not_reviewed');
INSERT INTO ref_data.futures_execution_admissions(catalog_id,revision_id,series_code,implementation,usage_policy,execution_venue,qualification_source_code,source_catalog_id,source_revision_id,source_series_code,eligible_from,eligibility_rule,eligibility_offset_seconds,eligibility_tzdb_version)
 SELECT s.catalog_id,s.revision_id,s.code,'contract_lot_execution_pnl_v2','generated_prices_sample_trades_v1','central_limit_order_book','qualification',s.catalog_id,'{source}',s.code,
 CASE WHEN m.event_at IS NULL THEN '2020-12-01T00:00:00Z'::timestamptz ELSE m.event_at END,
 CASE WHEN m.event_at IS NULL THEN 'after_source_first_trade_date_v1' ELSE 'exchange_first_trade_instant_v1' END,CASE WHEN m.event_at IS NULL THEN 0 ELSE NULL END,NULL
 FROM ref_data.futures_series s JOIN ref_data.futures_milestones m ON(m.catalog_id,m.revision_id,m.series_code,m.milestone)=(s.catalog_id,s.revision_id,s.code,'first_trading')
 WHERE s.catalog_id='{r}' AND s.revision_id='{rev}' AND s.product_code='test_gas_future';
"""
    marker=f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{r}' AND id='{rev}';"
    market=market.replace(marker,reference_sql+'\n'+marker)
    market=market.replace("'daily_settlement'","'trader_mark'")
    manifest['futuresConvention']='contract_lot_execution_pnl_v2'
    return reference,market,manifest



def run_checks(query,reference,market,manifest,migration):
    def body(sql):return sql.strip().replace('BEGIN;','',1).removesuffix('COMMIT;')
    # This fixture precedes 0040; index-fixing definitions arrive in 0050 and
    # do not participate in the retained futures admission qualification.
    market='\n'.join(line for line in market.splitlines()
                     if not line.startswith('INSERT INTO market_data.index_fixing_observations '))
    old_ref,old_market,old_manifest=prepare(reference,market,copy.deepcopy(manifest))
    r=old_manifest['referenceCatalogId'];rev=old_manifest['referenceRevisionId'];catalog=old_manifest['catalogId'];mrev=old_manifest['revisionId']
    original="CREATE TEMP TABLE retained_admission_rows AS SELECT to_jsonb(a) AS value FROM ref_data.futures_execution_admissions a;"
    checks=f"""
DO $$BEGIN
 IF(SELECT count(*) FROM ref_data.futures_execution_policies)<>(SELECT count(*) FROM retained_admission_rows)
 OR EXISTS(SELECT value FROM retained_admission_rows EXCEPT SELECT to_jsonb(a) FROM ref_data.futures_execution_admissions a)
 OR NOT EXISTS(SELECT 1 FROM ref_data.futures_execution_policies WHERE catalog_id='{r}' AND revision_id='{rev}' AND series_code='test_gas_202101' AND price_role='trader_mark' AND price_source_code='fixture' AND eligible_from='2020-12-01T00:00:00Z')
 OR NOT EXISTS(SELECT 1 FROM ref_data.futures_milestones WHERE catalog_id='{r}' AND revision_id='{rev}' AND series_code='test_gas_202101' AND milestone='first_trading' AND business_date='2020-11-30' AND event_at IS NULL)
 THEN RAISE EXCEPTION 'Retained execution qualification changed';END IF;
END$$;
"""
    query('BEGIN;'+body(old_ref)+body(old_market)+original+body(migration)+checks+'ROLLBACK;')
    # A retained policy that could select two sources cannot be converted by guess.
    ref_seal=f"UPDATE ref_data.revisions SET state='sealed' WHERE catalog_id='{r}' AND id='{rev}';"
    alt=f"INSERT INTO ref_data.sources VALUES('{r}','{rev}','alternate','synthetic','generated:pilot-close-v1/alternate',clock_timestamp(),repeat('c',64),NULL,'not_reviewed');"
    ambiguous=old_market.replace(ref_seal,alt+ref_seal)
    market_seal=f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{catalog}' AND id='{mrev}';"
    alias=f"""INSERT INTO market_data.curves SELECT x.* FROM market_data.curves c CROSS JOIN LATERAL jsonb_populate_record(NULL::market_data.curves,to_jsonb(c)||'{{"code":"alternate","name":"Alternate gas futures","direct_source_code":"alternate"}}'::jsonb)x WHERE c.catalog_id='{catalog}' AND c.revision_id='{mrev}' AND c.code='future';
INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'alternate',contract_id,ordinal FROM market_data.curve_members WHERE catalog_id='{catalog}' AND revision_id='{mrev}' AND curve_code='future';
"""
    assert market_seal in ambiguous
    ambiguous=ambiguous.replace(market_seal,alias+market_seal)
    try:query('BEGIN;'+body(old_ref)+body(ambiguous)+body(migration)+'ROLLBACK;')
    except RuntimeError as error:
        if 'futures_execution_policy_migration' not in str(error):raise
    else:raise AssertionError('Ambiguous retained price sources were silently selected')
    print('PASS execution-policy upgrade preserves exact retained qualifications/source facts and rejects ambiguous price sources without changing old rows',flush=True)
