"""A synthetic monthly 3.20 location mark that stays live through delivery."""
from preview_fixture import CAT,REV,R,M,identity

POINT='synthetic_quantity_point'
CURVE='synthetic_quantity_location'


def add_fixture(reference,market,security,manifest,*,price='3.20',prefix='synthetic_quantity',number=94500,curve_name='Synthetic selected quantity location',
                quote='test_gas_outright', target='test_index_target', start_minute=0, clock='test_utc',
                months=(('2021-01-01','2021-02-01'),)):
    point,curve,group,assignment=(prefix+suffix for suffix in ('_point','_location','_group','_assignment'))
    name=prefix.replace('_',' ').capitalize()
    guarded=lambda table,sql:f"DO $fixture$ BEGIN IF to_regclass('{table}') IS NOT NULL THEN {sql} END IF;END$fixture$;"
    refs=[f"INSERT INTO ref_data.locations VALUES({R},'{point}','{name} delivery point','test_delivery_point','{clock}');",
          f"INSERT INTO ref_data.commodity_locations VALUES({R},'test_gas','{point}');",
          guarded('ref_data.pipeline_delivery_points',f"INSERT INTO ref_data.pipeline_delivery_points VALUES({R},'test_pipeline_a','synthetic_gas_zone_a','{point}');"),
          guarded('ref_data.gas_delivery_conventions',f"INSERT INTO ref_data.gas_delivery_conventions VALUES({R},'test_pipeline_a','synthetic_gas_zone_a','{point}','test_gas','{clock}',{start_minute});")]
    definitions=[f"INSERT INTO market_data.curves VALUES({M},'{curve}',{R},'test_gas','test_hub_a','{quote}','test_uniform','{target}','direct','fixture','trader_mark','{curve_name}');",
       guarded('market_data.gas_valuation_groups',f"INSERT INTO market_data.gas_valuation_groups VALUES({M},'{group}',{R},'test_pipeline_a','test_gas','{curve}',0);"),
       guarded('market_data.gas_valuation_assignments',f"INSERT INTO market_data.gas_valuation_assignments(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code,curve_code,group_code,group_adjustment,location_adjustment) VALUES({M},'{assignment}',{R},'test_pipeline_a','synthetic_gas_zone_a','{point}','test_gas','{curve}','{group}',0,0);")]
    observations=[]
    prices = [price] * len(months) if isinstance(price, str) else price
    for ordinal,((start,end),mark) in enumerate(zip(months, prices, strict=True)):
        cid,sid=identity('5',number+ordinal),identity('9',number+ordinal)
        definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{start}','{end}','{end}T00:00:00Z',NULL);",
                            f"INSERT INTO market_data.curve_members VALUES({M},'{curve}','{cid}',{ordinal});",
                            f"INSERT INTO market_data.series VALUES({M},'{sid}','{prefix}_{ordinal}',{R},'{cid}','{quote}',NULL);"])
        for index,frame in enumerate(manifest['frames']):
            frame['observationIds']=[*frame['observationIds'],identity('7',number*100+index*10+ordinal)]
            frame['curveContracts']={**frame['curveContracts'],curve:[*frame['curveContracts'].get(curve, []),cid]}
            oid=frame['observationIds'][-1]
            observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','{prefix}_{index}_{ordinal}','1','trader_mark','final','{frame['at']}',CURRENT_TIMESTAMP,{mark},NULL);")
    market=market.replace('UPDATE ref_data.revisions SET','\n'.join(refs)+'\nUPDATE ref_data.revisions SET',1)
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+'\n'.join(observations)+'\nCOMMIT;\n'
    return reference,market,security,manifest
