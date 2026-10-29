"""Synthetic retained-close inputs; no clock or retained definitions are rewritten."""
from copy import deepcopy
from datetime import datetime,timedelta,timezone
from zoneinfo import ZoneInfo

from preview_fixture import CAT,REV,R,M,identity
from interval_curve_fixture import next_month


def prepare(reference,market,security,manifest):
    manifest=deepcopy(manifest);config=manifest['ppaBookings']['workloads']['60']
    now=datetime.now(timezone.utc);current=now.date().replace(day=1)
    prior=(current-timedelta(days=1)).replace(day=1)
    first=(prior-timedelta(days=1)).replace(day=1);last=next_month(first)
    zone=ZoneInfo('America/New_York')
    start=datetime.combine(first,datetime.min.time(),zone).astimezone(timezone.utc).isoformat()
    end=datetime.combine(last,datetime.min.time(),zone).astimezone(timezone.utc).isoformat()
    certificate=manifest['ppaBookings']['certificate'];energy='test_existing_close_energy';shaped='test_existing_close_shaped'
    definitions=[];observations=[];contracts={};pins=[]
    for ordinal,(code,quote,target,price) in enumerate(((energy,'test_power_outright','test_power_target','40'),(certificate,'test_rec_quote','test_rec_target','5'))):
        cid,sid,oid=(identity('d',990000+ordinal*10+k) for k in range(3))
        if code==energy:
            definitions.append(f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_power','test_hub_a','{quote}','test_ppa_long_profile_60','{target}','direct','fixture','trader_mark','Synthetic existing-close energy');")
            definitions.append(f"INSERT INTO market_data.curve_blocks VALUES({M},'{code}','all',NULL,NULL);")
        definitions.extend([
            f"INSERT INTO market_data.contracts VALUES({M},'{cid}',{R},'forward','{first}','{last}','{current.year+2}-01-01T00:00:00Z',NULL);",
            f"INSERT INTO market_data.series VALUES({M},'{sid}','test_existing_close_{ordinal}',{R},'{cid}','{quote}',NULL);",
            f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{cid}',9900);"])
        observations.append(f"INSERT INTO market_data.observations VALUES('{CAT}','{oid}','{REV}','{sid}',{R},'fixture','synthetic_existing_close_{ordinal}','1','trader_mark','final','2020-11-01T16:00:00Z','{now.isoformat()}',{price},NULL);")
        contracts[code]=[cid];pins.append(oid)
    definitions.extend([
        f"INSERT INTO market_data.shaped_curves VALUES({M},'{shaped}','Synthetic existing-close shape','additive','duration_weighted_block_mean_v1','block_shape_v1','{config['shape']}');",
        f"INSERT INTO market_data.shaped_curve_blocks VALUES({M},'{shaped}','{energy}','period');",
        f"INSERT INTO market_data.shaped_curve_actual_rules VALUES({M},'{shaped}',{R},'{config['source']}','interval_end',60,'interval_actual_due_v1');",
        f"INSERT INTO market_data.location_valuation_assignments VALUES({M},'test_existing_close_energy_assignment',{R},'{config['location']}','energy','{start}','{end}',NULL,'{shaped}',0);"])
    market=market.replace(f"INSERT INTO market_data.revision_periods VALUES ({M},'2020-01-01','2022-01-01');",f"INSERT INTO market_data.revision_periods VALUES ({M},'2020-01-01','{current.year+2}-01-01');")
    market=market.replace('UPDATE market_data.revisions SET','\n'.join(definitions)+'\nUPDATE market_data.revisions SET',1)
    market=market.rsplit('COMMIT;',1)[0]+'\n'+'\n'.join(observations)+'\nCOMMIT;\n'
    # API booking now seals retained schedules in resumable transactions. Keep
    # this synthetic close after setup so every deliberately supplied input can
    # be certified before its unchanged valuation-instant cutoff.
    at=(now+timedelta(minutes=5)).isoformat()
    template=manifest['frames'][0]
    live=deepcopy(template);live.update(name='Synthetic existing-close live PPA',at=at)
    live['curveContracts']={key:values for key,values in template['curveContracts'].items() if key in ('test_ppa_long_60_peak','test_ppa_long_60_off','test_ppa_long_15_peak','test_ppa_long_15_off')}
    # Live long-term vintages, plus the retained prior-month vintage.
    live['curveContracts'][certificate]=template['curveContracts'][certificate][1:]+contracts[certificate]
    live['observationIds'].extend(pins)
    held=deepcopy(live);held.update(name='Synthetic existing-close retained holdings')
    held['curveContracts']=contracts
    discount=deepcopy(next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18T16:00:00')))
    discount['name']='Synthetic existing-close discount inputs'
    discount['curveContracts']={code:discount['curveContracts'][code] for code in ('local_power','local_power_all_in')}
    manifest['frames'].extend([live,held,discount])
    manifest['closeRisk']={'at':at,'first':str(first),'last':str(last),'day':str(first.replace(day=15)),
        'vintage':contracts[certificate][0],'source':config['source']}
    from check_close_risk_discount import prepare_discount
    return prepare_discount(reference,market,security,manifest)
