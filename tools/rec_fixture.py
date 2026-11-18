"""Project-authored certificate reference rows for the named inventory checks."""


def extend(reference, market, security, manifest):
    catalog = manifest['referenceCatalogId']
    revision = manifest['referenceRevisionId']
    key = f"'{catalog}','{revision}'"
    additions = f"""
DO $fixture$DECLARE d ref_data.code;BEGIN
 SELECT code INTO STRICT d FROM ref_data.dimensions WHERE(catalog_id,revision_id)=({key})
  AND(mass_power,length_power,time_power,temperature_power,amount_power,current_power,luminous_power)=(0,0,0,0,0,0,0);
 INSERT INTO ref_data.commodities VALUES({key},'synthetic_rec','Synthetic certificate',d,'leaf');
 INSERT INTO ref_data.units(catalog_id,revision_id,code,symbol,dimension_code) VALUES({key},'rec','REC',d);
 INSERT INTO ref_data.commodity_units VALUES({key},'synthetic_rec','rec',d);
 INSERT INTO ref_data.measurement_bases VALUES({key},'synthetic_issued_rec','synthetic_rec',d);
 INSERT INTO ref_data.quantity_specifications VALUES({key},'synthetic_rec_quantity','synthetic_rec','synthetic_issued_rec','rec',d);
END$fixture$;
INSERT INTO ref_data.rec_programs VALUES({key},'synthetic_program','Synthetic program','fixture');
INSERT INTO ref_data.rec_classes VALUES({key},'synthetic_class','Synthetic eligible class','synthetic_program');
INSERT INTO ref_data.rec_technologies VALUES({key},'synthetic_solar','Synthetic solar');
INSERT INTO ref_data.rec_retirement_purposes VALUES({key},'synthetic_voluntary','Synthetic voluntary retirement');
INSERT INTO ref_data.rec_products VALUES({key},'synthetic_rec','Synthetic program solar certificate','synthetic_program','synthetic_class','synthetic_solar','test_facility_a','synthetic_rec_quantity');
"""
    additions += f"""
INSERT INTO ref_data.commodity_locations VALUES({key},'synthetic_rec','test_hub_a');
INSERT INTO ref_data.quote_specifications VALUES({key},'synthetic_rec_quote','synthetic_rec','outright','synthetic_rec_quantity','usd',1,0.01,0.01,-1000000,1000000);
INSERT INTO ref_data.price_index_definitions VALUES({key},'synthetic_rec_index','synthetic_rec','test_hub_a','usd','rec',1,'test_business');
INSERT INTO ref_data.settlement_targets VALUES({key},'synthetic_rec_target','index','synthetic_rec_index',NULL);
"""
    market_catalog, market_revision = manifest['catalogId'], manifest['revisionId']
    mk = f"'{market_catalog}','{market_revision}'"
    contract = 'fa000000-0000-0000-0000-000000000001'
    series = 'fa000000-0000-0000-0000-000000000002'
    definitions = f"""
INSERT INTO market_data.contracts VALUES({mk},'{contract}',{key},'forward','2021-01-01','2021-02-01','2021-02-01T00:00:00Z',NULL);
INSERT INTO market_data.curves VALUES({mk},'synthetic_rec_mark',{key},'synthetic_rec','test_hub_a','synthetic_rec_quote','test_uniform','synthetic_rec_target','direct','fixture','trader_mark','Synthetic REC mark');
INSERT INTO market_data.curve_members VALUES({mk},'synthetic_rec_mark','{contract}',0);
INSERT INTO market_data.series VALUES({mk},'{series}','synthetic_rec_mark',{key},'{contract}','synthetic_rec_quote',NULL);
INSERT INTO market_data.rec_valuation_assignments VALUES({mk},'synthetic_rec_2019',{key},'synthetic_rec','2019-01-01','2020-01-01','synthetic_rec_mark','{contract}','exact_product_vintage_flat_v1');
"""
    market = market.replace('UPDATE market_data.revisions SET', definitions + '\nUPDATE market_data.revisions SET', 1)
    from datetime import datetime, timezone
    observed = datetime.now(timezone.utc).isoformat()
    observations = []
    for number, frame in enumerate(manifest['frames']):
        identity = f'fa000000-0000-0000-0000-{100 + number:012d}'
        observations.append(f"INSERT INTO market_data.observations VALUES('{market_catalog}','{identity}','{market_revision}','{series}',{key},'fixture','synthetic_rec_mark_{number}','1','trader_mark','final','{frame['at']}','{observed}',50,NULL);")
        frame['observationIds'].append(identity)
        frame['curveContracts']['synthetic_rec_mark'] = [contract]
    market = market.rsplit('COMMIT;', 1)[0] + '\n' + '\n'.join(observations) + '\nCOMMIT;\n'
    marker = 'UPDATE ref_data.revisions SET state='
    if marker not in market:
        raise ValueError('The synthetic fixture must retain its draft-to-sealed reference boundary.')
    return reference, market.replace(marker, additions + '\n' + marker, 1), security, manifest
