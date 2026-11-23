"""A synthetic monthly 3.20 location mark that stays live through delivery."""
from preview_fixture import CAT,REV,R,M,identity

POINT='synthetic_quantity_point'
CURVE='synthetic_quantity_location'


def add_fixture(reference,market,security,manifest):
    guarded=lambda table,sql:f"DO $fixture$ BEGIN IF to_regclass('{table}') IS NOT NULL THEN {sql} END IF;END$fixture$;"
    refs=[f"INSERT INTO ref_data.locations VALUES({R},'{POINT}','Synthetic quantity delivery point','test_delivery_point','test_utc');",
          f"INSERT INTO ref_data.commodity_locations VALUES({R},'test_gas','{POINT}');",
          guarded('ref_data.pipeline_delivery_points',f"INSERT INTO ref_data.pipeline_delivery_points VALUES({R},'test_pipeline_a','synthetic_gas_zone_a','{POINT}');"),
          guarded('ref_data.gas_delivery_conventions',f"INSERT INTO ref_data.gas_delivery_conventions VALUES({R},'test_pipeline_a','synthetic_gas_zone_a','{POINT}','test_gas','test_utc',0);")]
    definitions=[f"INSERT INTO market_data.curves VALUES({M},'{CURVE}',{R},'test_gas','test_hub_a','test_gas_outright','test_uniform','test_index_target','direct','fixture','trader_mark','Synthetic selected quantity location');",
       guarded('market_data.gas_valuation_groups',f"INSERT INTO market_data.gas_valuation_groups VALUES({M},'synthetic_quantity_group',{R},'test_pipeline_a','test_gas','{CURVE}',0);"),
       guarded('market_data.gas_valuation_assignments',f"INSERT INTO market_data.gas_valuation_assignments(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code,curve_code,group_code,group_adjustment,location_adjustment) VALUES({M},'synthetic_quantity_assignment',{R},'test_pipeline_a','synthetic_gas_zone_a','{POINT}','test_gas','{CURVE}','synthetic_quantity_group',0,0);")]
    cid,sid=identity('5',94500),identity('9',94500)
    definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','2021-01-01','2021-02-01','2021-02-01T00:00:00Z',NULL);",
                        f"INSERT INTO market_data.curve_members VALUES({M},'{CURVE}','{cid}',0);",
                        f"INSERT INTO market_data.series VALUES({M},'{sid}','synthetic_quantity_0',{R},'{cid}','test_gas_outright',NULL);"])
    observations=[]
    for index,frame in enumerate(manifest['frames']):
        frame['observationIds']=list(frame['observationIds']);frame['curveContracts']={**frame['curveContracts'],CURVE:[cid]}
        oid=identity('7',9450000+index*10);frame['observationIds'].append(oid)
        observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','synthetic_quantity_{index}_0','1','trader_mark','final','{frame['at']}',CURRENT_TIMESTAMP,3.20,NULL);")
    market=market.replace('UPDATE ref_data.revisions SET','\n'.join(refs)+'\nUPDATE ref_data.revisions SET',1)
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+'\n'.join(observations)+'\nCOMMIT;\n'
    return reference,market,security,manifest
