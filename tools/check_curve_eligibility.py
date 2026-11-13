"""Owned synthetic live outputs with an expired, explicitly frozen upstream future."""
from copy import deepcopy
from datetime import datetime, timezone
from decimal import Decimal
from uuid import uuid4
from close_session_fixture import draft, grant, publish
from preview_client import PreviewClient
from preview_fixture import CAT, REV, R, identity

EXPIRED=identity('5',6)
FINAL=identity('7',8800001)


def prepare(reference,market,manifest):
    old="'test_gas_202102','last_trading','known','2021-01-28','2021-01-28 15:30:00+00'"
    new="'test_gas_202102','last_trading','known','2020-12-16','2020-12-16 15:30:00+00'"
    assert market.count(old)==1, 'The check requires its exact synthetic February milestone'
    market=market.replace(old,new)
    old=f"'{EXPIRED}',{R},'future','2021-02-01','2021-03-01','2021-01-28 15:30:00+00'"
    assert market.count(old)==1
    market=market.replace(old,old.replace('2021-01-28 15:30:00+00','2020-12-16 15:30:00+00'))
    edge="'base_forward','base','future','require_live'"
    assert market.count(edge)==1
    market=market.replace(edge,edge.replace('require_live','freeze_final_future'))
    observation=(f"INSERT INTO market_data.observations VALUES ('{CAT}','{FINAL}','{REV}','{identity('9',101)}',{R},"
                 f"'fixture','synthetic_expired_future_final','1','final_contract_settlement','final',"
                 f"'2020-12-16T15:30:00Z','{datetime.now(timezone.utc).isoformat()}',3.25,NULL);")
    market=market.rsplit('COMMIT;',1)[0]+observation+'\nCOMMIT;\n'
    manifest=deepcopy(manifest)
    for frame in manifest['frames']:
        if frame['at']>='2020-12-16T15:30:00Z':
            frame['observationIds'].append(FINAL)
            frame['frozenObservationIds']=[*frame.get('frozenObservationIds',[]),FINAL]
    source=next(f for f in manifest['frames'] if f['at']=='2020-12-18T16:00:00Z')
    missing=deepcopy(source);missing['at']='2020-12-18T17:00:00Z'
    missing['observationIds'].remove(FINAL);missing['frozenObservationIds'].remove(FINAL)
    empty=deepcopy(source);empty['at']='2020-12-18T18:00:00Z';empty['curveContracts']['future']=[EXPIRED]
    manifest['frames'].extend([missing,empty])
    return reference,market,manifest


def run_checks(url,query,manifest):
    def client(profile):
        value=PreviewClient(url);value.bootstrap();value.call('profile',{'profile':profile});return value
    editor,approver,admin,trader=(client(p) for p in ('editor','approver','admin','trader'))
    frames=approver.call('workspace')['frames']
    at=lambda hour:next(f for f in frames if f['at'].startswith(f'2020-12-18T{hour}:00:00'))
    source,missing,empty=(at(h) for h in ('16','17','18'))
    dates=approver.call('market/dates',{'curve':'future','timeZone':'America/Chicago','session':source['close']['session']})
    assert source['id'] in {f['id'] for day in dates['dates'] for f in day['frames']}
    trade_frame=next(f for f in trader.call('workspace')['frames'] if f['at'].startswith('2020-12-18T16:00:00'))
    choices=trader.call('futures/catalog',{'snapshotId':trade_frame['id'],'knowledgeMode':'current','convention':manifest['futuresConvention']})
    assert EXPIRED not in {c['id'] for c in choices['contracts']}
    for code,hour,curves in [('test_live_capture','16',sorted(source['curveContracts'])),
                             ('test_missing_capture','17',['future','base_forward']),('test_empty_capture','18',['future'])]:
        publish(editor,approver,draft(code,'test_business',hour+':00',curves))
        grant(admin,query,manifest,'approver',code)
    body={'requestId':str(uuid4()),'snapshotId':source['id'],'name':'Synthetic live output capture',
          'session':'test_live_capture','businessDate':'2020-12-18'}
    assert approver.call('market/closes/register',body,expected=422)['code']=='source_expired'
    result=approver.call('market/closes/capture-live',body)
    assert result==approver.call('market/closes/capture-live',body)
    catalog=manifest['catalogId'];close=result['id']
    assert query(f"SELECT count(*) FROM market_data.market_close_members WHERE catalog_id='{catalog}' AND close_id='{close}' AND curve_code='future';")=='5'
    assert query(f"SELECT count(*) FROM market_data.market_close_members WHERE catalog_id='{catalog}' AND close_id='{close}' AND curve_code='future' AND contract_id='{EXPIRED}';")=='0'
    assert query(f"SELECT count(*) FROM market_data.market_close_pins WHERE catalog_id='{catalog}' AND close_id='{close}' AND observation_id='{FINAL}' AND input_use='frozen';")=='1'
    saved=approver.call('workspace')['frames']
    assert next(f for f in saved if f['id']==source['id'])['curveContracts']==source['curveContracts']
    live=next(f for f in saved if f['id']==close)
    assert set(live['observationIds'])==set(source['observationIds'])
    built=approver.call('evaluate',{'snapshotId':close,'curve':'base_forward'})
    february=next(p for p in built['points'] if p['contractId']==identity('5',7))
    # Independently: the selected final 3.25 plus the synthetic February spread .105.
    assert Decimal(str(february['value']))==Decimal('3.355') and FINAL in february['lineage'],february
    print('PASS live capture removes only the expired output, retains every source pin, prices the dependent forward from its declared final, and preserves the original close',flush=True)
    count=query(f"SELECT count(*) FROM market_data.market_closes WHERE catalog_id='{catalog}';")
    for frame,session,code in [(missing,'test_missing_capture','missing_observation'),(empty,'test_empty_capture','empty_live_curve')]:
        failed={**body,'requestId':str(uuid4()),'snapshotId':frame['id'],'session':session}
        assert approver.call('market/closes/capture-live',failed,expected=422)['code']==code
        assert query(f"SELECT count(*) FROM market_data.market_closes WHERE catalog_id='{catalog}';")==count
    print('PASS live capture refuses missing required final evidence and an empty explicit root window without recording a partial close or rolling contracts',flush=True)
