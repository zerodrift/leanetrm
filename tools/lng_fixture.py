"""Project-authored LNG cargo qualification inputs for a newly owned preview only."""
from datetime import datetime, timezone
from preview_fixture import CAT, RCAT, RREV, R, CURRENCY

REVISION = '60000000-0000-0000-0000-000000000176'
CONTRACT = '50000000-0000-0000-0000-000000000176'
SERIES = '90000000-0000-0000-0000-000000000176'


def prepare(reference, market, manifest):
    refs = f"""
INSERT INTO ref_data.commodities VALUES({R},'synthetic_lng','Synthetic LNG','energy');
INSERT INTO ref_data.commodity_units VALUES({R},'synthetic_lng','mmbtu_it','energy'),({R},'synthetic_lng','metric_tonne','mass');
INSERT INTO ref_data.measurement_bases VALUES({R},'gross_energy','synthetic_lng','energy'),({R},'discharge_mass','synthetic_lng','mass');
INSERT INTO ref_data.quantity_specifications VALUES
 ({R},'synthetic_lng_energy','synthetic_lng','gross_energy','mmbtu_it','energy'),
 ({R},'synthetic_lng_mass','synthetic_lng','discharge_mass','metric_tonne','mass');
INSERT INTO ref_data.quote_specifications VALUES({R},'synthetic_lng_quote','synthetic_lng','outright','synthetic_lng_energy','{CURRENCY}',1,0.001,0.001,-1000,1000);
"""
    for code, name in [('synthetic_lng_load_terminal', 'Synthetic LNG loading terminal'),
                       ('synthetic_lng_load_custody', 'Synthetic LNG loading custody point'),
                       ('synthetic_lng_discharge_terminal', 'Synthetic LNG discharge terminal'),
                       ('synthetic_lng_discharge_custody', 'Synthetic LNG discharge custody point'),
                       ('synthetic_lng_named_port', 'Synthetic named contractual port')]:
        refs += f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({R},'{code}','{name}','test_physical_site','test_utc');\n"
        refs += f"INSERT INTO ref_data.commodity_locations VALUES({R},'synthetic_lng','{code}');\n"
    refs += f"""
INSERT INTO ref_data.lng_products VALUES({R},'synthetic_lng_product','synthetic_lng','Synthetic gross-energy LNG','synthetic_lng_energy','gross_calorific_v1'),
 ({R},'synthetic_lng_unpriced','synthetic_lng','Unpriced LNG product','synthetic_lng_energy','gross_calorific_v1');
INSERT INTO ref_data.lng_custody_points VALUES
 ({R},'synthetic_lng_product','synthetic_lng','load','synthetic_lng_load_terminal','synthetic_lng_load_custody'),
 ({R},'synthetic_lng_product','synthetic_lng','discharge','synthetic_lng_discharge_terminal','synthetic_lng_discharge_custody'),
 ({R},'synthetic_lng_unpriced','synthetic_lng','load','synthetic_lng_load_terminal','synthetic_lng_load_custody'),
 ({R},'synthetic_lng_unpriced','synthetic_lng','discharge','synthetic_lng_discharge_terminal','synthetic_lng_discharge_custody');
INSERT INTO ref_data.lng_mass_energy_conversions VALUES({R},'synthetic_lng_factor','synthetic_lng_product','synthetic_lng',
 'synthetic_lng_mass','synthetic_lng_energy',50,'gross_discharge_energy_per_mass_v1');
INSERT INTO ref_data.incoterms_editions VALUES({R},'incoterms_2020','Incoterms 2020',2020),({R},'incoterms_2010','Incoterms 2010',2010);
INSERT INTO ref_data.incoterms_rules VALUES
 ({R},'incoterms_2020_cif','incoterms_2020','CIF','Cost, Insurance and Freight'),
 ({R},'incoterms_2020_fob','incoterms_2020','FOB','Free On Board'),
 ({R},'incoterms_2010_cif','incoterms_2010','CIF','Cost, Insurance and Freight');
INSERT INTO ref_data.price_index_definitions VALUES({R},'synthetic_lng_index','synthetic_lng','synthetic_lng_discharge_terminal','{CURRENCY}','mmbtu_it',1,'test_business');
INSERT INTO ref_data.settlement_targets VALUES({R},'synthetic_lng_target','index','synthetic_lng_index',NULL);
"""
    assert market.count('UPDATE ref_data.revisions SET') >= 1
    market = market.replace('UPDATE ref_data.revisions SET', refs + '\nUPDATE ref_data.revisions SET', 1)
    m = f"'{CAT}','{REVISION}'"
    statements = [f"""BEGIN;
INSERT INTO market_data.revisions VALUES({m},{R},'draft',0);
INSERT INTO market_data.revision_periods VALUES({m},'2020-01-01','2022-01-01');
INSERT INTO market_data.contracts VALUES({m},'{CONTRACT}',{R},'forward','2021-01-01','2021-02-01','2021-02-01T00:00:00Z',NULL);
INSERT INTO market_data.series VALUES({m},'{SERIES}','synthetic_lng_forward',{R},'{CONTRACT}','synthetic_lng_quote',NULL);
INSERT INTO market_data.curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,commodity_code,location_code,quote_code,profile_code,settlement_target_code,rule,direct_source_code,direct_role,name)
 VALUES({m},'synthetic_lng_forward',{R},'synthetic_lng','synthetic_lng_discharge_terminal','synthetic_lng_quote','test_uniform','synthetic_lng_target','direct','fixture','trader_mark','Synthetic LNG monthly forward');
INSERT INTO market_data.curve_members VALUES({m},'synthetic_lng_forward','{CONTRACT}',0);
INSERT INTO market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation)
 VALUES({m},'synthetic_lng_index','2021-01-01','2021-02-01',{R},'synthetic_lng_quote','{SERIES}','fixture','2021-02-01T00:00:00Z','published_index_observations_v1');
INSERT INTO market_data.lng_valuation_assignments VALUES({m},'synthetic_lng_assignment',{R},'synthetic_lng_product','discharge',
 'synthetic_lng_discharge_terminal','synthetic_lng_discharge_custody','synthetic_lng_energy','test_utc','synthetic_lng_forward');
UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=({m});
"""]
    frames = []
    for n, frame in enumerate(manifest['frames']):
        oid = f'70000000-0000-0000-0000-{17600+n:012d}'
        statements.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REVISION}','{SERIES}',{R},'fixture','synthetic_lng_mark_{n}','1','trader_mark','final','{frame['at']}','{datetime.now(timezone.utc).isoformat()}',11,NULL);")
        frames.append({'at':frame['at'], 'observationIds':[oid], 'curveContracts':{'synthetic_lng_forward':[CONTRACT]}})
    statements.append('COMMIT;')
    return reference, market + '\n' + '\n'.join(statements), {**manifest,
        'lngMarket':{'revisionId':REVISION, 'frames':frames},
        'lngReferenceCatalog':{'referenceRevisionId':RREV, 'definitionRevisionId':REVISION}}
