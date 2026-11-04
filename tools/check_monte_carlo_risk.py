"""Full-PV simulation, exact sample reduction and retained replay in an owned database."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient,initial_frame


def run_checks(url,manifest,query,run_worker):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'risk'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    current={'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
    holdings=c.call('trading/blotter',{'context':current,'view':{'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}})
    held=next(h for h in holdings['holdings'] if h['instrumentType']=='future')
    scope={'context':{**current,'knowledgeMode':'retained','contextId':holdings['context']['contextId']},'bookIds':[held['bookId']],'portfolioIds':[],'tradeIds':[held['tradeId']]}
    catalog=c.call('portfolio/risk/catalog',scope)
    history=c.call('portfolio/risk/history',{'scope':scope,'pricingPolicy':catalog['pricingPolicy'],'policy':{**catalog['history']['profile'],'minimumSamples':60},
        'calendar':{'revision':{'catalogId':manifest['referenceCatalogId'],'revisionId':manifest['referenceRevisionId']},'code':'weekdays_utc'},
        'from':'2020-01-06','to':'2020-12-19','closeTime':'16:00:00','ambiguousTime':'reject','invalidTime':'reject'})
    profile=catalog['monteCarlo'];request={'history':history['request'],'policy':{**profile['profile'],**profile['defaults'],'paths':256,'seed':'42'}}
    direct=c.call('portfolio/risk/monte-carlo',request);v=direct['monteCarlo'];r=v['result'];g=v['generation']
    assert r['available'] and r['samples']==256 and len(g['samples'])==256 and 'historical' not in direct and 'parametric' not in direct and 'factors' not in direct
    assert v['history']==history['manifest'] and g['calibration']['historyId']==history['manifest']['id']
    assert [s['index'] for s in g['samples']]==list(range(256)) and len({s['id'] for s in g['samples']})==256
    assert c.call('portfolio/risk/monte-carlo',request)['monteCarlo']==v
    exposures={e['id']:e for e in v['exposures']}
    def oracle(study):
        result=study['result'];alpha=Fraction(study['request']['policy']['confidence']);by_id={e['id']:e for e in study['exposures']}
        for p in result['partitions']:
            indices=[i for i,key in enumerate(study['matrix']['columns']) if (by_id[key]['measure'],by_id[key]['currency'])==(p['measure'],p['currency'])]
            values=[]
            for row in study['matrix']['rows']:
                for i,key in enumerate(study['matrix']['columns']):assert Fraction(row['losses'][i])==Fraction(by_id[key]['baseValue'])-Fraction(row['scenarioValues'][i])
                values.append(sum(Fraction(row['losses'][i]) for i in indices))
            ordered=sorted(values);n=len(values);rank=(n*alpha.numerator+alpha.denominator-1)//alpha.denominator;mass=n*(1-alpha);left=mass;integral=0
            for loss in reversed(ordered):
                take=min(left,Fraction(1));integral+=take*loss;left-=take
                if left==0:break
            assert Fraction(p['valueAtRisk'])==ordered[rank-1] and Fraction(p['expectedShortfall'])==integral/mass
            assert sum(Fraction(b['valueAtRisk']) for b in p['books'])==Fraction(p['valueAtRisk'])
            assert sum(Fraction(t['expectedShortfall']) for t in p['trades'])==Fraction(p['expectedShortfall'])
            assert sum(b['count'] for b in p['histogram'])==n
    oracle(v)
    print('PASS Monte Carlo HTTP: retained seeded shocks, every full-PV loss, exact fractional ES and native allocations',flush=True)
    sample=g['samples'][0]
    joint={**scope,'pricingPolicy':catalog['pricingPolicy'],'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'bumps':[],'stress':[],'detail':'holdings',
        'joint':{'convention':catalog['joint']['convention'],'scenarios':{'id':'simulation_oracle','version':'v1','convention':catalog['joint']['scenarioConvention'],
            'scenarios':[{'id':'first','label':'Independent simulation replay','shifts':[{'factor':history['columns'][i]['factor']['factor'],'size':x,'convention':catalog['stressConvention']} for i,x in enumerate(sample['changes']) if Fraction(x)!=0]}]},'derivatives':None}}
    priced=c.call('portfolio/risk',joint)['joint']['samples'][0]
    row=next(row for row in v['matrix']['rows'] if row['id']==sample['id'])
    assert sum(Fraction(x['values']['loss']) for x in priced['positions'])==sum(Fraction(x) for x in row['losses'])
    print('PASS Monte Carlo mapping: separately requested ordinary joint PV reproduces the exact generated native shock and loss',flush=True)
    changed=c.call('portfolio/risk/monte-carlo',{**request,'policy':{**request['policy'],'seed':'43'}})['monteCarlo']
    assert changed['generation']['samples']!=g['samples'] and changed['studyId']!=v['studyId']
    mixed_request=deepcopy(history['request']);mixed_request.pop('expectedManifestId');mixed_request['scope']={**scope,'bookIds':[],'tradeIds':None};mixed_request['from']='2020-12-15';mixed_request['policy']['minimumSamples']=2
    mixed_history=c.call('portfolio/risk/history',mixed_request)
    mixed_spec={**request,'history':mixed_history['request'],'policy':{**request['policy'],'paths':32}}
    mixed=c.call('portfolio/risk/monte-carlo',mixed_spec)['monteCarlo'];oracle(mixed)
    assert mixed['result']['available'] and len(mixed['factors'])>1 and len(mixed['result']['partitions'])>1
    mixed_job=c.call('portfolio/risk/monte-carlo/jobs',{'requestId':str(uuid4()),'request':mixed_spec});run_worker()
    mixed_stored=c.call('portfolio/risk/jobs/'+mixed_job['jobId']);assert mixed_stored['state']=='completed',mixed_stored
    assert mixed_stored['result']['monteCarlo']==mixed
    print('PASS Monte Carlo mixed portfolio: complete multi-factor native partitions, net-before-quantile oracles and exact direct/worker parity',flush=True)
    operation=str(uuid4());submission={'requestId':operation,'request':request};job=c.call('portfolio/risk/monte-carlo/jobs',submission)
    assert c.call('portfolio/risk/monte-carlo/jobs',submission)['jobId']==job['jobId']
    run_worker();stored=c.call('portfolio/risk/jobs/'+job['jobId']);assert stored['state']=='completed',stored
    assert stored['result']['monteCarlo']==v
    assert [x['id'] for x in c.call('portfolio/risk/monte-carlo/archive?limit=1')['items']]==[job['jobId']]
    assert c.call('portfolio/risk/statistical/archive?limit=1')['items'][0]['kind']=='monte_carlo'
    assert not c.call('portfolio/risk/historical/archive?limit=1')['items'] and not c.call('portfolio/risk/parametric/archive?limit=1')['items']
    print('PASS Monte Carlo worker/archive: same generation, full-PV matrix, diagnostics and original request replay under current authority',flush=True)
    from check_monte_carlo_risk_db import admission_checks
    admission_checks(query,manifest,job['jobId'])
    from check_parametric_risk_db import run_checks as check_custody
    check_custody(query,manifest,job['jobId'],run_worker,method='monteCarlo')
    c.call('profile',{'profile':'viewer'})
    for path in ('portfolio/risk/jobs/'+job['jobId'],'portfolio/risk/monte-carlo/archive?limit=1','portfolio/risk/statistical/archive?limit=1'):c.call(path,expected=403)
    c.call('portfolio/risk/monte-carlo',request,expected=403)
    c.call('profile',{'profile':'risk'})
    print('PASS Monte Carlo authority: calculation, report, export and archive require current permission',flush=True)
    missing=deepcopy(history['request']);missing.pop('expectedManifestId');missing['from']='2020-12-17';missing['policy']['minimumSamples']=60
    short=c.call('portfolio/risk/history',missing)
    unavailable=c.call('portfolio/risk/monte-carlo',{**request,'history':short['request']})['monteCarlo']
    assert not unavailable['result']['available'] and unavailable['result']['samples']==0 and not unavailable['matrix']['rows'] and not unavailable['result']['partitions']
    print('PASS Monte Carlo incomplete history: no fabricated simulation distribution or partial successful report',flush=True)
