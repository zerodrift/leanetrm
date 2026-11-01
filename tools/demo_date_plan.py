"""Explicit synthetic dates, compiled before any demo instrument is rendered."""
import json
import subprocess
from pathlib import Path
from uuid import UUID, uuid5

NAMESPACE=UUID('e1000000-0000-0000-0000-000000000004')
BOUNDARIES=['2021-01-01','2021-02-01','2021-03-01','2021-04-01','2021-05-01','2021-06-01','2021-07-01']
EXPIRIES=['2020-12-28','2021-01-28','2021-02-28','2021-03-28','2021-04-28','2021-05-28']
PILLARS=['2021-01-18','2021-06-18','2021-12-18']


def compile_plan(dotnet,dll,owned_root):
    root=Path(owned_root)
    schedules=[]
    for order,(symbol,name,dates) in enumerate([
        ('oe.delivery_months','Synthetic monthly delivery boundaries',BOUNDARIES),
        ('oe.gas_expiries','Synthetic gas expiry dates',EXPIRIES),
        ('oe.rate_pillars','Synthetic rate maturities',PILLARS),
        ('oe.listing_dates','Synthetic listing dates',['2020-01-01'])],1):
        schedules.append({'id':str(uuid5(NAMESPACE,symbol)),'version':1,'generation':1,'state':'published',
            'definition':{'name':name,'symbol':symbol,'firstMonth':None,'lastMonth':None,'anchor':None,'day':None,
                'shortMonth':None,'adjustment':None,'calendar':None,'dates':dates},
            'selection':'absolute_index','effectiveFrom':'2020-01-01','effectiveTo':'2022-01-01',
            'publicationOrder':order,'publishedAt':'2020-01-01T00:00:00Z','engineVersion':'date_schedule_v3'})
    context={'effectiveOn':'2020-12-18','knownThrough':len(schedules)}
    mappings=[]
    for code,name,last,cutoff in [('gas_delivery','Synthetic gas futures dates','oe.gas_expiries','15:30'),
                                  ('forward_delivery','Synthetic forward dates','oe.delivery_months','00:00')]:
        rows=[{'label':f'Month {i+1}','start':f'oe.delivery_months[{i}]','end':f'oe.delivery_months[{i+1}]',
               'lastTrading':f'{last}[{i if code=="gas_delivery" else i+1}]','cutoffUtc':cutoff} for i in range(6)]
        mappings.append({'code':code,'name':name,'context':context,'contracts':rows})
    source=root/'date-seed-input.json';target=root/'date-seed.json'
    source.write_text(json.dumps({'schedules':schedules,'mappings':mappings}))
    subprocess.run([str(dotnet),str(dll),'--compile-date-seed',str(source),str(target)],check=True,timeout=30)
    return json.loads(target.read_text())


def schedule_sql(plan,catalog):
    def quoted(value):return "'"+value.replace("'","''")+"'"
    lines=[f"INSERT INTO market_data.catalogs VALUES('{catalog}','synthetic');"]
    for s in plan['schedules']:
        lines.append(f"INSERT INTO market_data.date_schedules VALUES('{catalog}','{s['id']}',{quoted(s['definition']['symbol'])});")
        lines.append("INSERT INTO market_data.date_schedule_versions(catalog_id,schedule_id,version,generation,state,definition,selection,effective_from,effective_to,engine_version,publication_order,published_at,publication_xid) VALUES("+
            f"'{catalog}','{s['id']}',1,1,'published',{quoted(json.dumps(s['definition']))}::jsonb,'absolute_index','2020-01-01','2022-01-01','date_schedule_v3',{s['publicationOrder']},clock_timestamp(),pg_current_xact_id());")
    return '\n'.join(lines)


def verify_instrument_dates(query, plan, catalog, revision):
    """Refuse demo rows whose actual dates did not come from the compiled plan."""
    from datetime import datetime
    mappings={m['code']:m['contracts'] for m in plan['mappings']}
    expected={kind:{(r['start'],r['end'],datetime.fromisoformat(r['lastTradingAt'])) for r in mappings[code]}
              for kind,code in [('future','gas_delivery'),('forward','forward_delivery')]}
    rows=json.loads(query(f"SELECT coalesce(json_agg(row_to_json(c)),'[]') FROM (SELECT id,kind,delivery_from,delivery_to,expiry_at FROM market_data.contracts WHERE catalog_id='{catalog}' AND revision_id='{revision}')c;"))
    if not rows:raise ValueError('The date-first fixture has no contracts.')
    for row in rows:
        dates=(row['delivery_from'],row['delivery_to'],datetime.fromisoformat(row['expiry_at']))
        if dates not in expected[row['kind']]:
            raise ValueError('Demo contract dates have no source mapping: '+row['id'])
    pillars=next(s['definition']['dates'] for s in plan['schedules'] if s['definition']['symbol']=='oe.rate_pillars')
    actual=json.loads(query(f"SELECT coalesce(json_agg(DISTINCT maturity ORDER BY maturity),'[]') FROM market_data.rate_curve_points WHERE catalog_id='{catalog}' AND revision_id='{revision}';"))
    if actual!=pillars:raise ValueError('Demo rate pillars do not match their source schedule.')
    return len(rows)
