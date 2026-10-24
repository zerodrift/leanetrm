"""Bounded original synthetic NG panels for custom-curve previews; no exchange claims."""
from copy import deepcopy
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal
from preview_fixture import CAT, REV, R, M


def prepare(reference,market,manifest,*,daily_lifecycle=False):
    manifest=deepcopy(manifest)
    refs=[f"INSERT INTO ref_data.futures_products SELECT (jsonb_populate_record(NULL::ref_data.futures_products,to_jsonb(p)||'{{\"code\":\"custom_ng_future\",\"name\":\"Synthetic NG recipe future\"}}'::jsonb)).* FROM ref_data.futures_products p WHERE(p.catalog_id,p.revision_id,p.code)=({R},'test_gas_future');",
          f"INSERT INTO ref_data.futures_product_terms SELECT catalog_id,revision_id,'custom_ng_future',profile_code,index_code FROM ref_data.futures_product_terms WHERE(catalog_id,revision_id,product_code)=({R},'test_gas_future');"]
    definitions=[];observations=[]
    now=datetime.now(timezone.utc).isoformat()
    def key(prefix,n):return f'{prefix}0000000-0000-0000-0000-{n:012d}'
    definitions.append(f"INSERT INTO market_data.curves VALUES({M},'ng_monthly',{R},'test_gas','test_hub_a','test_gas_outright','test_uniform','test_future_target','direct','fixture','daily_settlement','Synthetic NG monthly futures');")
    definitions.append(f"INSERT INTO market_data.curves VALUES({M},'ng_daily',{R},'test_gas','test_hub_a','test_gas_outright','test_uniform','test_index_target','direct','fixture','trader_mark','Synthetic NG daily forwards');")
    monthly=[];daily=[]
    for ordinal in range(19):
        absolute=2020*12+10+ordinal
        year,month=divmod(absolute,12);month+=1
        start=date(year,month,1)
        ny,nm=divmod(absolute+1,12);end=date(ny,nm+1,1)
        py,pm=divmod(absolute-1,12);expiry=date(py,pm+1,28)
        series=f'custom_ng_{year}{month:02d}'
        cid=key('e',100+ordinal);sid=key('e',1000+ordinal)
        refs.extend([f"INSERT INTO ref_data.futures_series VALUES({R},'{series}','custom_ng_future','{year}-{month:02d}','synthetic','{start}','{end}');",
                     f"INSERT INTO ref_data.futures_milestones VALUES({R},'{series}','first_trading','known','2020-01-01','2020-01-01 00:00:00+00','test_utc','fixture');",
                     f"INSERT INTO ref_data.futures_milestones VALUES({R},'{series}','last_trading','known','{expiry}','{expiry} 15:30:00+00','test_utc','fixture');"])
        definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'future','{start}','{end}','{expiry} 15:30:00+00','{series}');",
                            f"INSERT INTO market_data.series VALUES({M},'{sid}','custom_ng_monthly_{ordinal}',{R},'{cid}','test_gas_outright',NULL);",
                            f"INSERT INTO market_data.curve_members VALUES({M},'ng_monthly','{cid}',{ordinal});"])
        monthly.append((cid,sid,ordinal,datetime.combine(expiry,datetime.min.time(),timezone.utc)+timedelta(hours=15,minutes=30)))
    for ordinal in range(90 if daily_lifecycle else 62):
        start=date(2020,12,1)+timedelta(days=ordinal);end=start+timedelta(days=1)
        cid=key('e',200+ordinal);sid=key('e',2000+ordinal)
        definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{start}','{end}','{end} 00:00:00+00',NULL);",
                            f"INSERT INTO market_data.series VALUES({M},'{sid}','custom_ng_daily_{ordinal}',{R},'{cid}','test_gas_outright',NULL);",
                            f"INSERT INTO market_data.curve_members VALUES({M},'ng_daily','{cid}',{ordinal});"])
        daily.append((cid,sid,ordinal,datetime.combine(end,datetime.min.time(),timezone.utc)))
        if start>=date(2021,1,1):
            fixing_day=start-timedelta(days=1)
            definitions.append(f"INSERT INTO market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation) SELECT {M},t.index_a_code,'{start}','{end}',{R},'test_gas_outright','{sid}','fixture','{fixing_day} 12:00:00+00','published_index_observations_v1' FROM ref_data.settlement_targets t WHERE(t.catalog_id,t.revision_id,t.code)=({R},'test_index_target');")
    # A supported two-level basis chain has an explicitly frozen, expired
    # futures input and independent live spreads over the same delivery month.
    fixed_future=monthly[1]
    for ordinal,code in enumerate(('ng_fixed_spread','ng_fixed_basis')):
        cid=key('e',300+ordinal);sid=key('e',3000+ordinal)
        definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','2020-12-01','2021-01-01','2021-01-01 00:00:00+00',NULL);",
                            f"INSERT INTO market_data.series VALUES({M},'{sid}','custom_fixed_basis_{ordinal}',{R},'{cid}','test_gas_basis',NULL);",
                            f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_gas','test_hub_a','test_gas_basis','test_uniform','test_basis_target','direct','fixture','trader_mark','Synthetic frozen-source spread {ordinal+1}');",
                            f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',0);"])
    for code,target,base,base_contract,spread,policy in [
            ('ng_fixed_bridge',key('e',300),'ng_monthly',fixed_future[0],'ng_fixed_spread','freeze_final_future'),
            ('ng_fixed_location',key('e',301),'ng_fixed_bridge',key('e',300),'ng_fixed_basis','require_live')]:
        definitions.extend([f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_gas','test_hub_a','test_gas_outright','test_uniform','test_index_target','add',NULL,NULL,'Synthetic frozen-source {code}');",
                            f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{target}',0);"])
        for slot,source,source_contract,expiry_rule in [('base',base,base_contract,policy),('spread',spread,target,'require_live')]:
            definitions.extend([f"INSERT INTO market_data.curve_inputs(catalog_id,revision_id,curve_code,slot,source_curve_code,expiry_rule) VALUES({M},'{code}','{slot}','{source}','{expiry_rule}');",
                                f"INSERT INTO market_data.constituent_mappings VALUES({M},'{code}','{slot}','{target}','{source}','{source_contract}');"])
    final_id=key('f',900000)
    observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{final_id}','{REV}','{fixed_future[1]}',{R},'fixture','custom_frozen_final','1','final_contract_settlement','final','{fixed_future[3].isoformat()}','{now}',3.25,NULL);")
    frames={frame['at']:frame for frame in manifest['frames']}
    actuals=[]
    if daily_lifecycle:
        for ordinal in range(3):
            fixing_at=datetime(2020,12,31,12,tzinfo=timezone.utc)+timedelta(days=ordinal)
            oid=key('f',920000+ordinal);sid=daily[31+ordinal][1]
            observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','custom_daily_actual_{ordinal}','1','index_fixing','final','{fixing_at.isoformat()}','{now}',{10*(ordinal+1)},NULL);")
            actuals.append((oid,fixing_at))
    first=date(2020,11,18);last=date(2021,1,2) if daily_lifecycle else date(2020,12,31)
    for day_index in range((last-first).days+1):
        day=first+timedelta(days=day_index);instant=f'{day}T16:00:00Z'
        frame=frames.setdefault(instant,{'at':instant,'observationIds':[],'curveContracts':{}})
        if day>=date(2020,11,28) and day!=date(2020,12,17):
            frame['observationIds'].append(final_id)
        for ordinal,code in enumerate(('ng_fixed_spread','ng_fixed_basis') if day<date(2021,1,1) else ()):
            oid=key('f',910000+day_index*10+ordinal)
            value=Decimal('.5')+Decimal(day_index-28)*Decimal('.1') if ordinal==0 else Decimal('.2')
            observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{key('e',3000+ordinal)}',{R},'fixture','custom_fixed_spread_{ordinal}_{day_index}','1','trader_mark','final','{instant}','{now}',{value},NULL);")
            frame['observationIds'].append(oid)
            frame['curveContracts'][code]=[key('e',300+ordinal)]
        if day<date(2021,1,1):
            frame['curveContracts']['ng_fixed_bridge']=[key('e',300)]
            frame['curveContracts']['ng_fixed_location']=[key('e',301)]
        frame['observationIds'].extend(oid for oid,at in actuals if at<=datetime.combine(day,datetime.min.time(),timezone.utc)+timedelta(hours=16))
        for code,contracts,role in [('ng_monthly',monthly,'daily_settlement'),('ng_daily',daily,'trader_mark')]:
            frame['curveContracts'][code]=[]
            for cid,sid,ordinal,expiry in contracts:
                at=datetime.combine(day,datetime.min.time(),timezone.utc)+timedelta(hours=16)
                if at>=expiry:continue
                value=Decimal('3')+Decimal(ordinal)*Decimal('.025')+Decimal(day_index)*Decimal('.001')
                oid=key('f',day_index*1000+(100 if code=='ng_monthly' else 200)+ordinal)
                event=f'custom_curve_{code}_{day_index}_{ordinal}'
                observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','{event}','1','{role}','final','{instant}','{now}',{value},NULL);")
                frame['observationIds'].append(oid);frame['curveContracts'][code].append(cid)
    market=market.replace('UPDATE ref_data.revisions SET','\n'.join(refs)+'\nUPDATE ref_data.revisions SET',1)
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+'\n'+'\n'.join(observations)+'\nCOMMIT;\n'
    manifest['frames']=sorted(frames.values(),key=lambda f:f['at'])
    manifest['schedules'].append({'curve':'ng_monthly','from':'2020-11-01T00:00:00Z','to':'2022-04-28T15:30:00Z','exampleAt':'2020-12-18T16:00:00Z'})
    return reference,market,manifest
