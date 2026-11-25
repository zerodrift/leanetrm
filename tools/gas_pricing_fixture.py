"""Independent synthetic location, daily index and monthly index for exact entries."""
from copy import deepcopy
from datetime import date,timedelta
from preview_fixture import CAT,REV,R,M,TENANT,WORKSPACE,identity
from delivery_quantity_fixture import add_fixture as location_fixture


ADMINISTRATOR=('pricing_administrator',identity('c',94690))

def add_fixture(reference,market,security,manifest,*,exercise_months=False,imbalance_days=False):
    if imbalance_days:
        template=next(f for f in manifest['frames'] if f['at']=='2021-01-01T08:00:00Z')
        for at in ('2021-01-02T08:00:00Z','2021-01-05T08:00:00Z'):
            manifest['frames'].append({**deepcopy(template),'at':at})
    if exercise_months:
        # The deadline/fixing and the paid-cap close each have exact synthetic input selections.
        january=next(f for f in manifest['frames'] if f['at']=='2021-01-15T08:00:00Z')
        for at in ('2021-01-17T16:00:00Z','2021-01-24T08:00:00Z'):
            manifest['frames'].append({**deepcopy(january),'at':at})
    reference,market,security,manifest=location_fixture(reference,market,security,manifest,price='3.10')
    if exercise_months:
        reference,market,security,manifest=location_fixture(reference,market,security,manifest,price='3.20',
            prefix='synthetic_exercise',number=94890,curve_name='Synthetic exercise location')
        reference,market,security,manifest=location_fixture(reference,market,security,manifest,price='3.20',
            prefix='synthetic_worked',number=94892,curve_name='Synthetic worked point')
        reference,market,security,manifest=location_fixture(reference,market,security,manifest,price='0',
            prefix='synthetic_cap',number=94891,curve_name='Synthetic zero location')
    refs=[];definitions=[];observations=[]
    series=[('daily',94600),('monthly',94610)]
    if exercise_months:series.append(('worked_daily',94620))
    if imbalance_days:series.append(('delayed',94640))
    for kind,first in series:
        code='synthetic_entry_'+kind
        refs.extend([f"INSERT INTO ref_data.price_index_definitions SELECT catalog_id,revision_id,'{code}',commodity_code,location_code,currency_code,quote_unit_code,quote_multiplier,calendar_code FROM ref_data.price_index_definitions WHERE(catalog_id,revision_id,code)=({R},'test_price');",
                     f"INSERT INTO ref_data.settlement_targets VALUES({R},'{code}_target','index','{code}',NULL);"])
        definitions.append(f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_gas','test_hub_a','test_gas_outright','test_uniform','{code}_target','direct','fixture','trader_mark','Synthetic {kind} entry index');")
        if kind=='worked_daily':
            periods=[(date(2021,1,day),date(2021,1,day+1),forecast,actual,f'2021-01-{day-1}T16:00:00Z')
                     for day,forecast,actual in [(16,'3.10','3.10'),(17,'2.95','2.95'),(18,'3.20','3.80')]]
        elif kind=='monthly':
            periods=[(date(2021,1,1),date(2021,2,1),'3.00','3.00','2021-01-15T06:00:00Z')]
            if exercise_months:periods.append((date(2021,2,1),date(2021,3,1),'3.00','3.00','2021-02-01T06:00:00Z'))
        else:
            periods=[(date(2021,1,13)+timedelta(days=n),date(2021,1,14)+timedelta(days=n),'3.20',['3.30','3.10','3.20'][n],
                      f'2021-01-{13+n}T12:00:00Z') for n in range(3)]
            if imbalance_days:
                periods=[(date(2021,1,n+1),date(2021,1,n+2),'3.20',price,
                          f'2021-01-{n+1:02d}T12:00:00Z') for n,price in enumerate(['3.00','3.10','3.20','3.30'])]
            if exercise_months:
                for day in [date(2021,1,31),date(2021,2,1),date(2021,2,2)]:
                    periods.append((day,day+timedelta(days=1),'3.20','3.20',f'{day}T12:00:00Z'))
        if kind=='delayed':
            periods=[(start,end,forecast,actual,'2021-01-06T12:00:00Z' if start.day==3 else at) for start,end,forecast,actual,at in periods]
        for ordinal,(start,end,forecast,actual,at) in enumerate(periods):
            number=first+ordinal
            cid,sid=identity('5',number),identity('9',number)
            definitions.extend([f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{start}','{end}','2021-02-01T00:00:00Z',NULL);",
                                f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',{ordinal});",
                                f"INSERT INTO market_data.series VALUES({M},'{sid}','synthetic_entry_{number}',{R},'{cid}','test_gas_outright',NULL);",
                                f"INSERT INTO market_data.index_fixing_observations(catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation) VALUES({M},'{code}','{start}','{end}',{R},'test_gas_outright','{sid}','fixture','{at}','published_index_observations_v1');"])
            actual_id=identity('7',number*100+90)
            observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{actual_id}','{REV}','{sid}',{R},'fixture','synthetic_entry_actual_{number}','1','index_fixing','final','{at}',CURRENT_TIMESTAMP,{actual},NULL);")
            for n,frame in enumerate(manifest['frames']):
                oid=identity('7',number*100+n);frame['observationIds'].append(oid)
                frame['curveContracts']={**frame['curveContracts'],code:[identity('5',first+i) for i in range(len(periods))]}
                observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','synthetic_entry_mark_{number}_{n}','1','trader_mark','final','{frame['at']}',CURRENT_TIMESTAMP,{forecast},NULL);")
                if frame['at']>=at:frame['observationIds'].append(actual_id)
    market=market.replace('UPDATE ref_data.revisions SET','\n'.join(refs)+'\nUPDATE ref_data.revisions SET',1)
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+'\n'.join(observations)+'\nCOMMIT;\n'
    login,principal=ADMINISTRATOR
    security+=f"""
INSERT INTO security.principals VALUES('{principal}','human',true);
INSERT INTO security.identity_bindings VALUES('https://preview.invalid','{login}','{principal}','human_interactive');
INSERT INTO security.workspace_memberships VALUES('{TENANT}','{WORKSPACE}','{principal}','{login}','Synthetic pricing administrator',true,1);
INSERT INTO security.role_grants SELECT gen_random_uuid(),'{principal}',id,'{TENANT}','{WORKSPACE}',CURRENT_TIMESTAMP-INTERVAL '1 day',CURRENT_TIMESTAMP+INTERVAL '1 day',false
 FROM security.roles WHERE code IN('trader','access_administrator');"""
    return reference,market,security,manifest
