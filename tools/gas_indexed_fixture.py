"""Synthetic monthly physical marks and varying daily index projections for checks."""
from datetime import date, timedelta
from decimal import Decimal
from preview_fixture import CAT, REV, R, M, identity


def add_fixture(reference, market, security, manifest):
    # Some baseline closes intentionally share their original pin list. New
    # observations belong to their own close, without mutating another frame.
    for frame in manifest['frames']:
        frame['observationIds']=list(frame['observationIds'])
    # An independent outright curve makes the monthly asset and daily payment
    # exposures numerically observable, without changing any baseline fixture.
    point='synthetic_gas_point_indexed'; curve='synthetic_index_gas'
    refs=[f"INSERT INTO ref_data.locations(catalog_id,revision_id,code,name,type_code,clock_code) VALUES({R},'{point}','Synthetic indexed delivery point','test_delivery_point','test_utc');",
          f"INSERT INTO ref_data.commodity_locations VALUES({R},'test_gas','{point}');",
          f"INSERT INTO ref_data.pipeline_delivery_points VALUES({R},'test_pipeline_a','synthetic_gas_zone_a','{point}');",
          f"INSERT INTO ref_data.gas_delivery_conventions VALUES({R},'test_pipeline_a','synthetic_gas_zone_a','{point}','test_gas','test_utc',0);"]
    definitions=[f"INSERT INTO market_data.curves VALUES({M},'{curve}',{R},'test_gas','test_hub_a','test_gas_outright','test_uniform','test_index_target','direct','fixture','trader_mark','Synthetic daily and monthly index');",
                 f"INSERT INTO market_data.gas_valuation_groups VALUES({M},'synthetic_index_group',{R},'test_pipeline_a','test_gas','{curve}',0);",
                 f"INSERT INTO market_data.gas_valuation_assignments(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,pipeline_code,zone_code,point_code,product_code,curve_code,group_code,group_adjustment,location_adjustment) VALUES({M},'synthetic_index_assignment',{R},'test_pipeline_a','synthetic_gas_zone_a','{point}','test_gas','{curve}','synthetic_index_group',0,0);"]
    # Upgrade probes load this fixture before physical setup tables exist.
    def guarded(table,sql):
        return f"DO $fixture$ BEGIN IF to_regclass('{table}') IS NOT NULL THEN {sql} END IF;END$fixture$;"
    refs[2]=guarded('ref_data.pipeline_delivery_points',refs[2])
    refs[3]=guarded('ref_data.gas_delivery_conventions',refs[3])
    definitions[1]=guarded('market_data.gas_valuation_groups',definitions[1])
    definitions[2]=guarded('market_data.gas_valuation_assignments',definitions[2])
    observations=[]
    periods=[(date(2021,1,1),date(2021,2,1),Decimal('3.2')),(date(2021,2,1),date(2021,3,1),Decimal('3.3'))]
    periods.extend((date(2021,1,1)+timedelta(days=n),date(2021,1,2)+timedelta(days=n),Decimal(3)+Decimal(n)/1000) for n in range(59))
    for ordinal,(start,end,price) in enumerate(periods):
        cid=identity('5',80000+ordinal);sid=identity('9',80000+ordinal)
        definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{start}','{end}','{end}T00:00:00Z',NULL);",
                            f"INSERT INTO market_data.curve_members VALUES({M},'{curve}','{cid}',{ordinal});",
                            f"INSERT INTO market_data.series VALUES({M},'{sid}','synthetic_index_{ordinal}',{R},'{cid}','test_gas_outright',NULL);"])
        if ordinal>=2:
            definitions.append(f"INSERT INTO market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation) VALUES({M},'test_price','{start}','{end}',{R},'test_gas_outright','{sid}','fixture','{start}T12:00:00Z','published_index_observations_v1');")
        for sample,frame in enumerate(manifest['frames']):
            oid=identity('7',8000000+sample*1000+ordinal)
            frame['observationIds'].append(oid)
            observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','synthetic_index_{sample}_{ordinal}','1','trader_mark','final','{frame['at']}',CURRENT_TIMESTAMP,{price},NULL);")
    market=market.replace('UPDATE ref_data.revisions SET','\n'.join(refs)+'\nUPDATE ref_data.revisions SET',1)
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+'\n'.join(observations)+'\nCOMMIT;\n'
    return reference,market,security,manifest
