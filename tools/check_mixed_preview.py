"""HTTP qualification of the newly owned mixed-period synthetic preview."""
import http.cookiejar
import json
from decimal import Decimal
from urllib.error import HTTPError
from urllib.request import Request, build_opener, HTTPCookieProcessor


def run_checks(url,export):
    passed=0
    def check(label,condition):
        nonlocal passed
        if not condition:raise AssertionError(label)
        passed+=1;print('PASS mixed preview: '+label,flush=True)
    class Client:
        def __init__(self):
            self.http=build_opener(HTTPCookieProcessor(http.cookiejar.CookieJar()))
            self.token=self.call('bootstrap')['csrfToken']
        def call(self,path,body=None,status=200,headers=None):
            h={'Origin':url,**(headers or {})}
            if body is not None:h.update({'Content-Type':'application/json','X-Preview-CSRF':self.token})
            h.update(headers or {})
            req=Request(url+'/api/'+path,headers=h,data=None if body is None else json.dumps(body).encode())
            try:response=self.http.open(req,timeout=20)
            except HTTPError as error:response=error
            payload=response.read()
            data=json.loads(payload) if payload else None
            if response.code!=status:raise AssertionError((path,response.code,status,data))
            return data
        def evaluate(self,frame,curve):return self.call('evaluate',{'snapshotId':frame['id'],'curve':curve})
    c=Client()
    c.call('workspace',status=403)
    c.call('profile',{'profile':'viewer'},status=400,headers={'X-Preview-CSRF':'invalid'})
    c.call('profile',{'profile':'viewer'},status=403,headers={'Origin':'https://foreign.invalid'})
    c.call('profile',{'profile':'viewer'})
    workspace=c.call('workspace');frames=workspace['frames']
    check('eight retained frames and seven commodity curves use the exact exported catalogs',len(frames)==8 and
          len(workspace['definitions']['curves'])==7 and workspace['definitions']['revisions'][0]['catalog_id']==export['catalogId'])
    assert len(export['frames'])==len(frames)
    for seed,frame in zip(export['frames'],frames):
        for code,members in seed['curveContracts'].items():
            result=c.evaluate(frame,code)
            assert [p['contractId'] for p in result['points']]==members,(code,frame['at'])
            if not members:assert result['availability']=='no_remaining_contracts' and result['trace']==[]
            assert len(result['trace'])==result['evaluated']
        expected={(p['curve'],p['contractId']):p['value'] for p in seed['expected']}
        for code in ['location_delivery','location_balmo']:
            for point in c.evaluate(frame,code)['points']:
                assert point['value']==expected[(code,point['contractId'])]
        check('sealed database and managed adapter agree on '+frame['at'][:10],True)
    first=frames[0]
    daily=c.evaluate(first,'location_delivery')['points'][0]
    balmo=c.evaluate(first,'location_balmo')['points'][0]
    check('daily and BALMO prices use their separately marked matching periods',daily['value']=='3.25' and balmo['value']=='3.4')
    daily_series=next(s for s in workspace['definitions']['series'] if s['contract_id']==
                      next(n['contractId'] for n in c.evaluate(first,'location_delivery')['trace']
                           if n['curve']=='benchmark_delivery' and n['deliveryFrom']=='2026-01-16'))
    obs=next(o for o in workspace['observations'] if o['series_id']==daily_series['id'] and o['id'] in first['observationIds'])
    c.call('corrections',{'snapshotId':first['id'],'observationId':obs['id'],'value':'3.2'},status=403)
    check('viewer cannot alter source marks',True)
    c.call('profile',{'profile':'editor'})
    changed=c.call('corrections',{'snapshotId':first['id'],'observationId':obs['id'],'value':'3.2'})['frame']
    check('edited frame preserves exact contract selections',changed['curveContracts']==first['curveContracts'])
    check('daily edit changes only its explicit dependents',c.evaluate(changed,'location_delivery')['points'][0]['value']=='3.35' and
          c.evaluate(changed,'location_balmo')['points'][0]['value']=='3.4' and
          c.evaluate(changed,'location_all_in')['points'][0]['value']=='4.5' and
          c.evaluate(first,'location_delivery')['points'][0]['value']=='3.25')
    balmo_source=next(n for n in c.evaluate(first,'location_balmo')['trace'] if n['curve']=='benchmark_balmo')
    balmo_obs=next(o for o in workspace['observations'] if o['id']==balmo_source['lineage'][0])
    edited_balmo=c.call('corrections',{'snapshotId':first['id'],'observationId':balmo_obs['id'],'value':'3.3'})['frame']
    check('BALMO edit does not create daily exposure',c.evaluate(edited_balmo,'location_balmo')['points'][0]['value']=='3.5' and
          c.evaluate(edited_balmo,'location_delivery')['points'][0]['value']=='3.25')
    revised=c.call('select-observation',{'snapshotId':first['id'],'observationId':changed['observationIds'][first['observationIds'].index(obs['id'])]})['frame']
    check('explicit revision selection preserves period view',revised['curveContracts']==first['curveContracts'] and
          c.evaluate(revised,'location_delivery')['points'][0]['value']=='3.35')
    jan30=next(p for p in c.evaluate(first,'location_delivery')['points'] if p['deliveryFrom']=='2026-01-30')
    history=c.call('history',{'revisionPolicy':'exact_snapshot_pins_v1','snapshotId':first['id'],'curve':'location_delivery','contractId':jan30['contractId']})['points']
    check('dated daily history retains exact prices and explicit expired gaps',
          [p['value'] for p in history[:4]]==['3.404','3.252','3.281','3.41'] and
          all(p['value'] is None and p['unavailableReason']=='source_expired' for p in history[4:]))
    march=next(p for p in c.evaluate(first,'location_delivery')['points'] if p['deliveryFrom']=='2026-03-01')
    history=c.call('history',{'revisionPolicy':'exact_snapshot_pins_v1','snapshotId':first['id'],'curve':'location_delivery','contractId':march['contractId']})['points']
    check('same monthly contract has eight retained prices across the rollover',len(history)==8 and
          history[0]['value']=='3.85' and history[-1]['value']=='4' and all(p['unavailableReason'] is None for p in history))
    check('BALMO is explicitly empty on both month-end frames',all(c.evaluate(frames[i],'location_balmo')['availability']=='no_remaining_contracts' for i in [4,7]))
    feb=c.evaluate(frames[5],'location_delivery')['points']
    check('February view has 27 daily contracts plus two monthly tails',len(feb)==29 and feb[0]['value']=='3.55' and
          c.evaluate(frames[5],'location_balmo')['points'][0]['value']=='3.65')
    for code,members in first['curveContracts'].items():
        summary=c.evaluate(first,code)['summary']
        assert summary['kind']=='contracts' and summary['contractCount']==len(members) and summary['availability'] is None,(code,summary)
        assert (summary['front'] or {}).get('contractId')==(members[0] if members else None),(code,summary)
    check('every evaluated curve states its contract count and front contract from the backend',True)
    front=c.evaluate(first,'location_delivery')['summary']['front']
    seeded={(p['curve'],p['contractId']):p['value'] for p in export['frames'][0]['expected']}
    check('the daily curve fronts its first seeded contract at its seeded price',front['price']==seeded[('location_delivery',front['contractId'])]=='3.25')
    empty=c.evaluate(frames[4],'location_balmo')['summary']
    check('a curve at a close past its last expiry states no contracts and why',empty=={'kind':'contracts','contractCount':0,'front':None,'availability':'no_remaining_contracts'})
    emptied=c.call('market/curve-shapes',{'snapshotId':frames[4]['id'],'curves':['location_balmo']})['curves'][0]
    check('a curve shape with no remaining contracts states a zero count, no front and why',
          (emptied['contractCount'],emptied['front'],emptied['availability'])==(0,None,'no_remaining_contracts'))
    shapes={s['curve']:s for s in c.call('market/curve-shapes',{'snapshotId':first['id'],'curves':['location_delivery','location_balmo']})['curves']}
    check('curve shapes state the same count and front as the evaluation',all(
          shapes[code]['contractCount']==c.evaluate(first,code)['summary']['contractCount'] and shapes[code]['front']==c.evaluate(first,code)['summary']['front'] and shapes[code]['availability'] is None
          for code in shapes))
    rates=c.evaluate(first,'interest_rates')
    check('basic interest-rate example still evaluates',rates['kind']=='rates' and len(rates['points'])==3)
    check('a rate curve states its maturity count, first maturity and zero rate, and day count',rates['summary']=={'kind':'rates','maturityCount':3,
          'firstMaturity':rates['points'][0]['maturity'],'firstZeroRate':rates['points'][0]['value'],'dayCount':rates['convention']['dayCount']})
    check('a rate curve is named by its stored market type, not a rule',{r['code']:r['kind'] for r in c.call('workspace/navigation')['curves']}['interest_rates']=='interest_rate')
    check('unknown curve remains an error',c.call('evaluate',{'snapshotId':first['id'],'curve':'absent'},status=422)['code']=='missing_curve')
    check('arbitrary dated contract cannot be substituted in history',c.call('history',{'revisionPolicy':'exact_snapshot_pins_v1','snapshotId':first['id'],'curve':'location_delivery',
          'contractId':'00000000-0000-0000-0000-000000000001'},status=422)['code']=='request_member')
    other=Client();other.call('profile',{'profile':'viewer'})
    check('another browser session cannot use an edited frame',other.call('evaluate',{'snapshotId':changed['id'],'curve':'location_delivery'},status=422)['code']=='snapshot_missing')
    # Exact transport is independently checked without binary floating point.
    check('USD per MMBtu labels follow persisted reference links',workspace['references']['currencies'][0]['display_code']=='USD' and
          workspace['references']['units'][0]['symbol']=='MMBtu' and Decimal(daily['value'])==Decimal('3.10')+Decimal('0.15'))
    print(f'{passed} mixed preview HTTP checks passed.',flush=True)
    return workspace
