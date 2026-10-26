"""Synthetic typed-factor consumer with a separately published unit revision."""
from datetime import date,datetime,timedelta,timezone
from decimal import Decimal
from preview_fixture import CAT,RCAT,RREV,CURRENCY,identity,installed_tzdb

REFERENCE='20000000-0000-0000-0000-000000000220'
REVISION='60000000-0000-0000-0000-000000000220'
SQL=None


def prepare(manifest):
    global SQL
    base=next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18'))
    for at in ['2021-04-01T02:00:00Z','2021-04-01T04:00:00Z','2021-04-05T08:00:00Z']:
        manifest['frames'].append({**base,'at':at})
    for market in manifest['swapMarkets']:
        present={f['at'] for f in market['frames']}
        market['frames'].extend(dict(at=f['at'],observationIds=[],curveContracts={}) for f in manifest['frames'] if f['at'] not in present)
    r=f"'{RCAT}','{REFERENCE}'";m=f"'{CAT}','{REVISION}'"
    rows=["BEGIN;",f"INSERT INTO ref_data.revisions(catalog_id,id,revision_key) VALUES({r},'synthetic_factor_units');",f"""DO $copy$ DECLARE t text;BEGIN
    FOREACH t IN ARRAY security.reference_display_tables() LOOP
      EXECUTE format('INSERT INTO ref_data.%1$I SELECT(jsonb_populate_record(NULL::ref_data.%1$I,to_jsonb(r)||jsonb_build_object(''revision_id'',$3))).* FROM ref_data.%1$I r WHERE(catalog_id,revision_id)=($1,$2)',t)
      USING '{RCAT}'::uuid,'{RREV}'::uuid,'{REFERENCE}'::uuid;
    END LOOP;END $copy$;""",
      f"INSERT INTO ref_data.units VALUES({r},'synthetic_thermal_bundle','TU','energy'),({r},'synthetic_power_slice','PU','energy');",
      f"INSERT INTO ref_data.unit_conversions VALUES({r},'synthetic_thermal_bundle','mmbtu_it','energy',2,1),({r},'synthetic_power_slice','megawatt_hour','energy',1,1000);",
      f"INSERT INTO ref_data.commodity_units VALUES({r},'test_gas','synthetic_thermal_bundle','energy'),({r},'test_power','synthetic_power_slice','energy'),({r},'test_power','megawatt','power');",
      f"INSERT INTO ref_data.measurement_bases VALUES({r},'factor_power_rate_basis','test_power','power');",
      f"INSERT INTO ref_data.quantity_specifications VALUES({r},'factor_gas_bundle','test_gas','test_gas_energy','synthetic_thermal_bundle','energy'),({r},'factor_power_slice','test_power','test_power_energy','synthetic_power_slice','energy'),({r},'factor_power_rate','test_power','factor_power_rate_basis','megawatt','power');",
      f"INSERT INTO ref_data.clock_rules(catalog_id,revision_id,code,kind,iana_zone,tzdb_version) VALUES({r},'synthetic_new_york','iana','America/New_York','{installed_tzdb()}');",
      f"UPDATE ref_data.revisions SET state='sealed' WHERE(catalog_id,id)=({r});",
      f"INSERT INTO market_data.revisions VALUES({m},{r},'draft',0);",f"INSERT INTO market_data.revision_periods VALUES({m},'2020-01-01','2022-01-01');"]
    members={};series=[]
    for commodity,quote,target,index in [('gas','test_gas_outright','test_index_target','test_price'),('power','test_power_outright','test_power_target','test_power_price')]:
        code='factor_daily_'+commodity;members[code]=[]
        rows.append(f"INSERT INTO market_data.curves VALUES({m},'{code}',{r},'test_{commodity}','test_hub_a','{quote}','test_uniform','{target}','direct','fixture','trader_mark','Synthetic factor {commodity}');")
        for ordinal in range(181):
            start=date(2021,1,1)+timedelta(days=ordinal);end=start+timedelta(days=1);cid=identity('5',220000+(1000 if commodity=='power' else 0)+ordinal);sid=identity('9',220000+(1000 if commodity=='power' else 0)+ordinal);members[code].append(cid)
            fixing=start-timedelta(days=1);at=f'{fixing}T12:00:00Z'
            price=Decimal('3')+Decimal(ordinal+1)/100 if commodity=='gas' else Decimal('40')+Decimal(ordinal+1)/10
            actual=price+(Decimal('.31') if commodity=='gas' else Decimal('3.1'))
            rows.extend([f"INSERT INTO market_data.contracts VALUES({m},'{cid}',{r},'forward','{start}','{end}','{end}T00:00:00Z',NULL);",f"INSERT INTO market_data.curve_members VALUES({m},'{code}','{cid}',{ordinal});",f"INSERT INTO market_data.series VALUES({m},'{sid}','factor_{commodity}_{ordinal}',{r},'{cid}','{quote}',NULL);",f"INSERT INTO market_data.index_fixing_observations VALUES({m},'{index}','{start}','{end}',{r},'{quote}','{sid}','fixture','{at}','published_index_observations_v1');"])
            series.append(dict(id=sid,fixing=at,price=price,actual=actual))
    rates=[]
    rows.append(f"INSERT INTO market_data.rate_curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,interest_rate_code,source_code,date_basis,name) VALUES({m},'factor_rates',{r},'test_interest','fixture','valuation_utc_date','Synthetic factor discounting');")
    for n,maturity in enumerate(('2021-01-18','2021-06-18','2021-12-18')):
        sid=identity('9',229000+n);rates.append(sid)
        rows.extend([f"INSERT INTO market_data.series VALUES({m},'{sid}','factor_rate_{n}',{r},NULL,NULL,'test_zero_rate');",f"INSERT INTO market_data.rate_curve_points VALUES({m},'factor_rates','{maturity}','{sid}',{n});"])
    rows.append(f"UPDATE market_data.revisions SET state='sealed' WHERE(catalog_id,id)=({m});")
    now=datetime.now(timezone.utc).isoformat();emitted=set();frames=[]
    for sample,frame in enumerate(manifest['frames']):
        pins=[]
        for n,s in enumerate(series):
            oid=identity('7',22000000+sample*1000+n);pins.append(oid)
            rows.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REVISION}','{s['id']}',{r},'fixture','factor_mark_{oid}','1','trader_mark','final','{frame['at']}','{now}',{s['price']},NULL);")
            if s['fixing']<=frame['at']:
                actual=identity('7',22900000+n);pins.append(actual)
                if actual not in emitted:
                    rows.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{actual}','{REVISION}','{s['id']}',{r},'fixture','factor_actual_{actual}','1','index_fixing','final','{s['fixing']}','{now}',{s['actual']},NULL);");emitted.add(actual)
        for n,sid in enumerate(rates):
            oid=identity('7',22800000+sample*10+n);pins.append(oid)
            rows.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REVISION}','{sid}',{r},'fixture','factor_rate_{oid}','1','parameter','final','{frame['at']}','{now}',0.05,NULL);")
        frames.append(dict(at=frame['at'],observationIds=pins,curveContracts=members))
    rows.append(f"INSERT INTO trading.currency_discount_defaults VALUES('{CAT}','d2000000-0000-0000-0000-000000000220','{REVISION}',{r},'{CURRENCY}','factor_rates','log_discount_geometric_root_to_even_18_v1',1,'2020-01-01T00:00:00Z');")
    rows.append('COMMIT;');SQL='\n'.join(rows);manifest['swapMarkets'].append(dict(revisionId=REVISION,frames=frames))
