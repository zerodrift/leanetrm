"""Separate immutable synthetic daily gas/power market bundle for swap schedules."""
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal
from preview_fixture import CAT, RCAT, RREV, identity

REVISION = '60000000-0000-0000-0000-000000000002'


def fixture(fixing_frames=False, observation_guards=False, scheduled_periods=False, closes=(),effective_from='2020-01-01'):
    """closes: further instants at which the bundle publishes its prices and the fixings due by then."""
    m = f"'{CAT}','{REVISION}'"; r = f"'{RCAT}','{RREV}'"
    rows = ['BEGIN;', f"INSERT INTO market_data.revisions VALUES ({m},{r},'draft',0);"]
    prices = []
    for n, commodity, quote, target in [(0, 'gas', 'test_gas_outright', 'test_index_target'), (1, 'power', 'test_power_outright', 'test_power_target')]:
        curve = 'daily_'+commodity
        rows.append(f"INSERT INTO market_data.curves VALUES ({m},'{curve}',{r},'test_{commodity}','test_hub_a','{quote}','test_uniform','{target}','direct','fixture','trader_mark','Daily {commodity}');")
        for day in (range(0,61) if scheduled_periods else range(0, 33) if observation_guards else range(1, 32)):
            start = date(2021, 1, 1)+timedelta(days=day-1); end = start+timedelta(days=1)
            cid = identity('5', 2000+n*100+day); sid = identity('9', 2000+n*100+day)
            fixing_at=f'{start-timedelta(days=1)}T12:00:00Z'
            index='test_price' if commodity=='gas' else 'test_power_price'
            rows.extend([f"INSERT INTO market_data.contracts VALUES ({m},'{cid}',{r},'forward','{start}','{end}','{end} 00:00:00+00',NULL);",
                         f"INSERT INTO market_data.curve_members VALUES ({m},'{curve}','{cid}',{day if observation_guards else day-1});",
                         f"INSERT INTO market_data.series VALUES ({m},'{sid}','swap_{commodity}_{day}',{r},'{cid}','{quote}',NULL);",
                         f"INSERT INTO market_data.index_fixing_observations (catalog_id,revision_id,index_code,observation_from,observation_to,reference_catalog_id,reference_revision_id,quote_code,series_id,source_code,fixing_at,implementation) VALUES ({m},'{index}','{start}','{end}',{r},'{quote}','{sid}','fixture','{fixing_at}','published_index_observations_v1');"])
            prices.append((2000+n*100+day, sid, Decimal('3')+Decimal(day)/100 if n==0 else Decimal('40')+Decimal(day)/10, 'trader_mark'))
    rows.append(f"INSERT INTO market_data.rate_curves VALUES ({m},'interest_rates',{r},'test_interest','fixture','valuation_utc_date','Interest rates');")
    for n, maturity in enumerate(['2021-01-18', '2021-06-18', '2021-12-18']):
        sid=identity('9', 2800+n)
        rows.extend([f"INSERT INTO market_data.series VALUES ({m},'{sid}','swap_rate_{n}',{r},NULL,NULL,'test_zero_rate');",
                     f"INSERT INTO market_data.rate_curve_points VALUES ({m},'interest_rates','{maturity}','{sid}',{n});"])
        prices.append((2800+n,sid,Decimal('.025')+Decimal(n)/200,'parameter'))
    boundary=date.fromisoformat(effective_from).isoformat()
    rows.extend([f"INSERT INTO market_data.revision_periods VALUES ({m},'{boundary}','2022-01-01');",
                 f"UPDATE market_data.revisions SET state='sealed' WHERE catalog_id='{CAT}' AND id='{REVISION}';"])
    now=datetime.now(timezone.utc).isoformat();frames=[]
    for day in range(15, 19):
        pins=[]
        for number, sid, value, role in prices:
            oid=identity('7', 300000+day*10000+number);pins.append(oid)
            adjusted=value-Decimal(18-day)* (Decimal('.0001') if role=='parameter' else Decimal('.01'))
            rows.append(f"INSERT INTO market_data.observations VALUES ('{CAT}','{oid}','{REVISION}','{sid}',{r},'fixture','swap_{day}_{number}','1','{role}','final','2020-12-{day} 16:00:00+00','{now}',{adjusted},NULL);")
        frames.append({'at':f'2020-12-{day}T16:00:00Z','observationIds':pins,'curveContracts':{}})
    emitted=set()
    # Separate final facts reuse the exact price series but have their own
    # index_fixing role. They cannot become shocked forward marks.
    for sample,at in enumerate([*(['2021-01-01T08:00:00Z','2021-01-15T08:00:00Z','2021-01-31T08:00:00Z'] if fixing_frames else []),*closes]):
        pins=[]
        for number,sid,value,role in prices:
            oid=identity('7',6000000+sample*10000+number);pins.append(oid)
            rows.append(f"INSERT INTO market_data.observations VALUES ('{CAT}','{oid}','{REVISION}','{sid}',{r},'fixture','fixing_frame_{sample}_{number}','1','{role}','final','{at}','{now}',{value},NULL);")
            if role=='parameter':
                continue
            day=number%100
            fixing_at=f'{date(2021,1,1)+timedelta(days=day-2)}T12:00:00Z'
            if fixing_at>at:
                continue
            actual_id=identity('7',7000000+number);pins.append(actual_id)
            if actual_id not in emitted:
                actual=value+Decimal('.31') if number<2100 else value+Decimal('3.1')
                rows.append(f"INSERT INTO market_data.observations VALUES ('{CAT}','{actual_id}','{REVISION}','{sid}',{r},'fixture','synthetic_daily_fixing_{number}','1','index_fixing','final','{fixing_at}','{now}',{actual},NULL);")
                emitted.add(actual_id)
        frames.append({'at':at,'observationIds':pins,'curveContracts':{}})
    if fixing_frames:
        partial=next(frame for frame in frames if frame['at']=='2021-01-15T08:00:00Z')
        frames.append({**partial,'at':'2021-01-15T09:00:00Z',
                       'observationIds':[oid for oid in partial['observationIds'] if oid!=identity('7',7002007)]})
    rows.append('COMMIT;')
    return '\n'.join(rows), {'revisionId':REVISION,'frames':frames}
