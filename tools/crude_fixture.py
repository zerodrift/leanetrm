"""Project-authored synthetic fixed-standard-volume crude inputs, owned preview only."""
from datetime import datetime, timezone
from preview_fixture import CAT, RCAT, RREV, R, CURRENCY

REVISION = '60000000-0000-0000-0000-000000000051'
CONTRACT = '50000000-0000-0000-0000-000000000051'
SERIES = '90000000-0000-0000-0000-000000000051'


def prepare(reference, market, manifest):
    refs = f"""
INSERT INTO ref_data.commodities VALUES ({R},'synthetic_crude','Synthetic crude oil','volume');
INSERT INTO ref_data.commodity_units VALUES ({R},'synthetic_crude','petroleum_barrel','volume');
INSERT INTO ref_data.measurement_bases VALUES ({R},'synthetic_crude_standard_60f','synthetic_crude','volume');
INSERT INTO ref_data.quantity_specifications VALUES ({R},'synthetic_crude_quantity','synthetic_crude','synthetic_crude_standard_60f','petroleum_barrel','volume');
INSERT INTO ref_data.quote_specifications VALUES ({R},'synthetic_crude_outright','synthetic_crude','outright','synthetic_crude_quantity','{CURRENCY}',1,0.01,0.01,-1000,1000);
INSERT INTO ref_data.commodity_locations VALUES ({R},'synthetic_crude','test_facility_a'),({R},'synthetic_crude','test_delivery_point_a'),({R},'synthetic_crude','test_hub_a');
INSERT INTO ref_data.price_index_definitions VALUES ({R},'synthetic_crude_index','synthetic_crude','test_hub_a','{CURRENCY}','petroleum_barrel',1,'test_business');
INSERT INTO ref_data.settlement_targets VALUES ({R},'synthetic_crude_target','index','synthetic_crude_index',NULL);
INSERT INTO ref_data.crude_grades(catalog_id,revision_id,code,commodity_code,name,quantity_code,standard_temperature,temperature_scale)
 VALUES({R},'synthetic_grade_a','synthetic_crude','Synthetic grade A','synthetic_crude_quantity',60,'fahrenheit');
INSERT INTO ref_data.crude_delivery_points VALUES({R},'synthetic_crude','synthetic_grade_a','test_facility_a','test_delivery_point_a');
"""
    market = market.replace('UPDATE ref_data.revisions SET', refs+'\nUPDATE ref_data.revisions SET', 1)
    m = f"'{CAT}','{REVISION}'"
    statements = [f"""BEGIN;
INSERT INTO market_data.revisions VALUES({m},{R},'draft',0);
INSERT INTO market_data.revision_periods VALUES({m},'2020-01-01','2022-01-01');
INSERT INTO market_data.contracts VALUES({m},'{CONTRACT}',{R},'forward','2021-01-01','2021-02-01','2021-02-01T00:00:00Z',NULL);
INSERT INTO market_data.series VALUES({m},'{SERIES}','synthetic_crude_forward',{R},'{CONTRACT}','synthetic_crude_outright',NULL);
INSERT INTO market_data.curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,commodity_code,location_code,quote_code,profile_code,settlement_target_code,rule,direct_source_code,direct_role,name)
 VALUES({m},'synthetic_crude_forward',{R},'synthetic_crude','test_hub_a','synthetic_crude_outright','test_uniform','synthetic_crude_target','direct','fixture','trader_mark','Synthetic crude forward');
INSERT INTO market_data.curve_members VALUES({m},'synthetic_crude_forward','{CONTRACT}',0);
INSERT INTO market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation)
 VALUES({m},'synthetic_crude_index','2021-01-01','2021-02-01',{R},'synthetic_crude_outright','{SERIES}','fixture','2020-12-31T12:00:00Z','published_index_observations_v1');
INSERT INTO market_data.crude_valuation_assignments(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,commodity_code,grade_code,facility_code,custody_point_code,curve_code,clock_code)
 VALUES({m},'synthetic_crude_assignment',{R},'synthetic_crude','synthetic_grade_a','test_facility_a','test_delivery_point_a','synthetic_crude_forward','test_utc');
UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{CAT}' AND id='{REVISION}';
"""]
    frames = []
    for n, frame in enumerate(manifest['frames']):
        oid = f'70000000-0000-0000-0000-{5100+n:012d}'
        statements.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REVISION}','{SERIES}',{R},'fixture','synthetic_crude_mark_{n}','1','trader_mark','final','{frame['at']}','{datetime.now(timezone.utc).isoformat()}',80,NULL);")
        frames.append({'at':frame['at'],'observationIds':[oid],'curveContracts':{'synthetic_crude_forward':[CONTRACT]}})
    statements.append('COMMIT;')
    return reference, market+'\n'+'\n'.join(statements), {**manifest,'crudeMarket':{'revisionId':REVISION,'frames':frames}}
