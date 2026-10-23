"""Original synthetic PPA quantity references, composed after the interval-curve fixture."""
from copy import deepcopy
from datetime import datetime, timezone
from preview_fixture import CAT, REV, R, M, TENANT, WORKSPACE, identity
from interval_curve_fixture import SHAPED, MARKED, EDITOR

ASSETS=('test_asset_a','test_asset_b')
FROM, SWITCH, TO='2020-01-01T05:00:00Z','2031-11-15T05:00:00Z','2032-01-01T05:00:00Z'


def prepare(reference, market, security, manifest):
    manifest=deepcopy(manifest)
    refs=[f"INSERT INTO ref_data.value_specifications VALUES({R},'test_ppa_capacity_factor','dimensionless',NULL,NULL,0,1);",
          f"INSERT INTO ref_data.dimensions(catalog_id,revision_id,code,mass_power,length_power,time_power,temperature_power,amount_power,current_power,luminous_power) VALUES({R},'certificate',0,0,0,0,0,0,0);",
          f"INSERT INTO ref_data.units(catalog_id,revision_id,code,symbol,dimension_code) VALUES({R},'test_rec','REC','certificate');",
          f"INSERT INTO ref_data.commodity_units VALUES({R},'test_power','test_rec','certificate');",
          f"INSERT INTO ref_data.measurement_bases VALUES({R},'test_rec_basis','test_power','certificate');",
          f"INSERT INTO ref_data.quantity_specifications VALUES({R},'test_rec_quantity','test_power','test_rec_basis','test_rec','certificate');",
          f"INSERT INTO ref_data.quote_specifications VALUES({R},'test_rec_quote','test_power','outright','test_rec_quantity','usd',1,0.01,0.01,0,1000);",
          f"INSERT INTO ref_data.price_index_definitions VALUES({R},'test_rec_index','test_power','test_hub_a','usd','test_rec',1,'test_business');",
          f"INSERT INTO ref_data.settlement_targets VALUES({R},'test_rec_target','index','test_rec_index',NULL);"]
    definitions=[];sources=[];series={};assignments=[]
    now=datetime.now(timezone.utc).isoformat()
    for n,asset in enumerate(ASSETS):
        definitions.append(f"INSERT INTO market_data.generation_assets VALUES({M},'{asset}','Synthetic generation asset {n+1}',{R},'test_hub_a','test_capacity_quantity','synthetic_eastern');")
        for first,last,basis,accredited in [(FROM,SWITCH,200 if n==0 else 100,160 if n==0 else 80),
                                          (SWITCH,TO,100 if n==0 else 50,80 if n==0 else 40)]:
            definitions.append(f"INSERT INTO market_data.generation_asset_versions VALUES({M},'{asset}','{first}','{last}',{basis},{accredited});")
        for minutes in (60,15):
            source=f'{asset}_{minutes}'
            forecast,meter=(identity('d',810000+n*100+minutes+i) for i in (0,1))
            for sid,kind,spec in [(forecast,'capacity_factor_forecast','test_ppa_capacity_factor'),(meter,'metered_energy','test_power_quantity')]:
                value=f"'{spec}'" if kind=='capacity_factor_forecast' else 'NULL'
                quantity=f"'{spec}'" if kind=='metered_energy' else 'NULL'
                definitions.append(f"INSERT INTO market_data.interval_series VALUES('{CAT}','{sid}','{source}_{kind}','Synthetic {source} {kind}','{kind}',{R},NULL,{value},{quantity},'synthetic_eastern',{minutes},'{EDITOR}','{TENANT}','{WORKSPACE}','{now}');")
            definitions.append(f"INSERT INTO market_data.volume_sources(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,asset_code,clock_code,interval_minutes,forecast_series_id,forecast_stage,metered_series_id,metered_stage,loss,range_rule,availability_mode,availability,curtailment_mode,curtailment,degradation_mode,degradation,degradation_basis,degradation_base_date,meter_due_rule,meter_due_offset_minutes) VALUES({M},'{source}',{R},'{asset}','synthetic_eastern',{minutes},'{forecast}','generated','{meter}','generated',0.02,'generation_nonnegative_v1','apply',0.98,'reflected',NULL,'reflected',NULL,NULL,NULL,'meter_due_v1',60);")
            sources.append(source);series[source]={'forecast':forecast,'meter':meter,'asset':asset,'minutes':minutes}
    rec='test_rec_vintage'
    cid,sid,oid=(identity('d',820000+i) for i in range(3))
    definitions += [f"INSERT INTO market_data.curves VALUES({M},'{rec}',{R},'test_power','test_hub_a','test_rec_quote','test_uniform','test_rec_target','direct','fixture','trader_mark','Synthetic certificate vintage');",
                    f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','2031-01-01','2032-01-01','2032-01-01T00:00:00Z',NULL);",
                    f"INSERT INTO market_data.series VALUES({M},'{sid}','test_rec_vintage_2031',{R},'{cid}','test_rec_quote',NULL);",
                    f"INSERT INTO market_data.curve_members VALUES({M},'{rec}','{cid}',0);"]
    for ordinal,(attribute,first,last,curve,shaped,adjustment) in enumerate([
            ('energy',FROM,SWITCH,None,SHAPED,'1.25'),('energy',SWITCH,TO,MARKED,None,'2.5'),
            ('certificate',FROM,TO,rec,None,'0'),('capacity',FROM,TO,manifest['intervalCurves']['capacity'],None,'0')]):
        assignment=f'test_location_{attribute}_{ordinal}';assignments.append(assignment)
        curve_sql=f"'{curve}'" if curve else 'NULL';shaped_sql=f"'{shaped}'" if shaped else 'NULL'
        definitions.append(f"INSERT INTO market_data.location_valuation_assignments VALUES({M},'{assignment}',{R},'test_hub_a','{attribute}','{first}','{last}',{curve_sql},{shaped_sql},{adjustment});")
    central='test_asset_c';forecast,meter=identity('d',840001),identity('d',840002)
    definitions += [f"INSERT INTO market_data.generation_assets VALUES({M},'{central}','Synthetic Central generation asset',{R},'test_hub_a','test_capacity_quantity','synthetic_central');",
        f"INSERT INTO market_data.generation_asset_versions VALUES({M},'{central}','2020-01-01T06:00:00Z','2032-01-01T06:00:00Z',100,80);"]
    for central_series,kind,value,quantity in [(forecast,'capacity_factor_forecast',"'test_ppa_capacity_factor'",'NULL'),(meter,'metered_energy','NULL',"'test_power_quantity'")]:
        definitions.append(f"INSERT INTO market_data.interval_series VALUES('{CAT}','{central_series}','{central}_15_{kind}','Synthetic Central {kind}','{kind}',{R},NULL,{value},{quantity},'synthetic_central',15,'{EDITOR}','{TENANT}','{WORKSPACE}','{now}');")
    definitions.append(f"INSERT INTO market_data.volume_sources(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,asset_code,clock_code,interval_minutes,forecast_series_id,forecast_stage,metered_series_id,metered_stage,loss,range_rule,availability_mode,availability,curtailment_mode,curtailment,degradation_mode,degradation,degradation_basis,degradation_base_date,meter_due_rule,meter_due_offset_minutes) VALUES({M},'{central}_15',{R},'{central}','synthetic_central',15,'{forecast}','generated','{meter}','generated',0.02,'generation_nonnegative_v1','apply',0.98,'reflected',NULL,'reflected',NULL,NULL,NULL,'meter_due_v1',60);")
    series[central+'_15']={'forecast':forecast,'meter':meter,'asset':central,'minutes':15}
    # Exact native payment indexes have their own sources, independent of location valuation.
    payment_indexes={}
    for minutes in (60,15):
        code=f'test_payment_index_{minutes}';block=code+'_block';profile=code+'_profile'
        shape,contract,series_id,source=(identity('d',830000+minutes*10+i) for i in range(4))
        clock='synthetic_eastern' if minutes==60 else 'synthetic_central'
        provider,dataset,market_type=('PJM','da_hrl_lmps','DA') if minutes==60 else ('ERCOT','np6-905-cd/spp_node_zone_hub','RT')
        refs.append(f"INSERT INTO ref_data.delivery_profiles VALUES({R},'{profile}','uniform','{clock}','synthetic_eastern_holidays','per_delivery_hour',{minutes});")
        definitions += [
            f"INSERT INTO market_data.interval_series VALUES('{CAT}','{shape}','{code}_shape','Synthetic payment index shape','additive_price_shape',{R},'test_power_outright',NULL,NULL,'{clock}',{minutes},'{EDITOR}','{TENANT}','{WORKSPACE}','{now}');",
            f"INSERT INTO market_data.curves VALUES({M},'{block}',{R},'test_power','test_hub_a','test_power_outright','{profile}','test_power_target','direct','fixture','trader_mark','Synthetic native payment index {minutes}');",
            f"INSERT INTO market_data.contracts VALUES({M},'{contract}',{R},'forward','2020-11-01','2020-12-01','2020-12-01T05:00:00Z',NULL);",
            f"INSERT INTO market_data.series VALUES({M},'{series_id}','{code}_november',{R},'{contract}','test_power_outright',NULL);",
            f"INSERT INTO market_data.curve_members VALUES({M},'{block}','{contract}',0);",
            f"INSERT INTO market_data.curve_blocks VALUES({M},'{block}','all',NULL,NULL);",
            f"INSERT INTO market_data.shaped_curves VALUES({M},'{code}','Synthetic payment index {minutes}','additive','duration_weighted_block_mean_v1','block_shape_v1','{shape}');",
            f"INSERT INTO market_data.shaped_curve_blocks VALUES({M},'{code}','{block}','period');",
            f"INSERT INTO power_data.sources(id,catalog_id,name,provider,dataset,market,point,minutes,reference_catalog_id,reference_revision_id,location_code,clock_code,currency_code,quantity_code) VALUES('{source}','{CAT}','Synthetic payment source {minutes}','{provider}','{dataset}','{market_type}','synthetic_payment_{minutes}',{minutes},{R},'test_hub_a','{clock}','usd','test_power_quantity');",
            f"INSERT INTO market_data.shaped_curve_actual_rules VALUES({M},'{code}',{R},'{source}','interval_end',60,'interval_actual_due_v1');"]
        payment_indexes[str(minutes)]={'curve':code,'sourceId':source,'provider':provider,'clock':clock,'shape':shape}
    market=market.replace('UPDATE ref_data.revisions SET','\n'.join(refs)+'\nUPDATE ref_data.revisions SET',1)
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+f"\nINSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','synthetic_rec_2031','1','trader_mark','final','2020-11-01T16:00:00Z','{now}',5,NULL);\nCOMMIT;\n"
    for frame in manifest['frames']:
        frame['observationIds'].append(oid);frame['curveContracts'][rec]=[cid]
    manifest['ppa']={'assets':list(ASSETS),'sources':sources,'series':series,'from':FROM,'switch':SWITCH,'to':TO,
                     'paymentIndexes':payment_indexes,'assignments':assignments,'location':'test_hub_a','certificateCurve':rec,'capacityCurve':manifest['intervalCurves']['capacity']}
    return reference,market,security,manifest
