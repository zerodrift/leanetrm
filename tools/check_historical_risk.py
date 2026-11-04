"""Exact empirical/full-PV oracles in run_preview's owned synthetic database."""
from copy import deepcopy
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient,initial_frame


def run_checks(url,manifest,query,run_worker):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'risk'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    current={'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
    holdings=c.call('trading/blotter',{'context':current,'view':{'mode':'trades','bookIds':[],'portfolioIds':[],
        'groupBy':[],'measures':[],'pivotConvention':None}})
    held=next(h for h in holdings['holdings'] if h['instrumentType']=='future')
    scope={'context':{**current,'knowledgeMode':'retained','contextId':holdings['context']['contextId']},
        'bookIds':[held['bookId']],'portfolioIds':[],'tradeIds':[held['tradeId']]}
    catalog=c.call('portfolio/risk/catalog',scope)
    history=c.call('portfolio/risk/history',{'scope':scope,'pricingPolicy':catalog['pricingPolicy'],
        'policy':{**catalog['history']['profile'],'minimumSamples':200},
        'calendar':{'revision':{'catalogId':manifest['referenceCatalogId'],'revisionId':manifest['referenceRevisionId']},'code':'weekdays_utc'},
        'from':'2020-01-06','to':'2020-12-19','closeTime':'16:00:00','ambiguousTime':'reject','invalidTime':'reject'})
    request={'history':history['request'],'policy':{**catalog['historical']['profile'],'confidence':'0.99'}}
    direct=c.call('portfolio/risk/historical',request);study=direct['historical'];result=study['result']
    assert result['available'] and result['samples']==249 and 'factors' not in direct and 'parametric' not in direct and 'joint' not in direct
    assert result['historyId']==history['manifest']['id'] and study['history']==history['manifest']
    exposures={e['id']:e for e in study['exposures']};columns=study['matrix']['columns'];rows=study['matrix']['rows']
    assert len(exposures)==len(columns)==len([h for h in holdings['holdings'] if h['tradeId']==held['tradeId'] and h['bookId']==held['bookId']])
    assert {r['id'] for r in rows}=={r['id'] for r in history['manifest']['samples']} and len(rows)==249
    losses={}
    for row in rows:
        assert len(row['scenarioValues'])==len(row['losses'])==len(columns)
        for i,key in enumerate(columns):
            assert Fraction(row['losses'][i])==Fraction(exposures[key]['baseValue'])-Fraction(row['scenarioValues'][i])
        losses[row['id']]=sum(Fraction(v) for v in row['losses'])
    assert len(result['partitions'])==1
    partition=result['partitions'][0];alpha=Fraction(request['policy']['confidence']);n=len(losses)
    ordered=sorted(losses.values());rank=(n*alpha.numerator+alpha.denominator-1)//alpha.denominator
    mass=n*(1-alpha);remaining=mass;integral=0
    for loss in reversed(ordered):
        take=min(remaining,Fraction(1));integral+=take*loss;remaining-=take
        if remaining==0:break
    assert Fraction(partition['valueAtRisk'])==ordered[rank-1] and Fraction(partition['expectedShortfall'])==integral/mass
    assert sum(Fraction(v['valueAtRisk']) for v in partition['books'])==Fraction(partition['valueAtRisk'])
    assert sum(Fraction(v['expectedShortfall']) for v in partition['trades'])==Fraction(partition['expectedShortfall'])
    assert sum(Fraction(v['esWeight']) for v in partition['samples'])==1
    assert sum(v['count'] for v in partition['histogram'])==n
    print('PASS historical HTTP: all249 current-portfolio full-PV losses, exact empirical quantile/fractional ES and native allocation',flush=True)

    # A separately requested ordinary joint shock is an independent adapter oracle
    # for the mapping from each captured native history column to an actual PV move.
    row=next(r for r in history['manifest']['samples'] if any(Fraction(v)!=0 for v in r['changes']))
    joint={**history['request']['scope'],'bumps':[],'stress':[],'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],
        'pricingPolicy':catalog['pricingPolicy'],'detail':'holdings','joint':{'convention':catalog['joint']['convention'],
        'scenarios':{'id':'historical_oracle','version':'v1','convention':catalog['joint']['scenarioConvention'],
        'scenarios':[{'id':'row','label':'Independent historical row','shifts':[
            {'factor':history['columns'][i]['factor']['factor'],'size':v,'convention':catalog['stressConvention']}
            for i,v in enumerate(row['changes']) if Fraction(v)!=0]}]},'derivatives':None}}
    oracle=c.call('portfolio/risk',joint)['joint']['samples'][0]
    assert sum(Fraction(p['values']['loss']) for p in oracle['positions'])==losses[row['id']]
    print('PASS historical mapping: ordinary joint PV agrees with the captured history vector, quantities and native units',flush=True)

    normal=catalog['parametric'];compared={**request,'comparison':{'policy':{**normal['profile'],'confidence':'0.99','drift':normal['drifts']['sample']},
        'finiteDifference':{'convention':normal['finiteDifference'],'tradingTicks':1,'deltaTolerance':{'absolute':'0','relative':'0'}}}}
    paired=c.call('portfolio/risk/historical',compared)['historical']
    assert paired['result']==result and paired['studyId']==study['studyId']==paired['comparison']['studyId']
    assert paired['comparison']['report']['history']==study['history'] and paired['comparison']['report']['result']['available']
    print('PASS historical comparison: one captured portfolio/history/pricing study binds the full-PV and sample-mean delta-normal reports',flush=True)

    insufficient=deepcopy(request);insufficient['history'].pop('expectedManifestId');insufficient['history']['policy']['minimumSamples']=1000
    insufficient['history']=c.call('portfolio/risk/history',insufficient['history'])['request']
    unavailable=c.call('portfolio/risk/historical',insufficient)['historical']
    assert not unavailable['result']['available'] and unavailable['result']['partitions']==[] and unavailable['matrix']['rows']==[]
    print('PASS historical coverage: insufficient common history cannot publish a fabricated distribution',flush=True)
    check_assistant_reads(c,scope,request,direct,compared,paired,history,insufficient)
    for field,value in [('horizon','ten_days'),('tailPolicy','normal_tail'),('valuationPolicy','recalibrate_volatility')]:
        bad=deepcopy(request);bad['policy'][field]=value;c.call('portfolio/risk/historical',bad,expected=422)

    mixed_source=deepcopy(history['request']);mixed_source.pop('expectedManifestId');mixed_source['scope']={**scope,'bookIds':[],'tradeIds':None};mixed_source['from']='2020-12-15';mixed_source['policy']['minimumSamples']=2
    mixed_history=c.call('portfolio/risk/history',mixed_source)
    mixed=c.call('portfolio/risk/historical',{'history':mixed_history['request'],'policy':request['policy']})['historical']
    assert mixed['result']['available'] and len(mixed['result']['partitions'])>1 and len(mixed['factors'])>1
    by_id={e['id']:e for e in mixed['exposures']}
    for p in mixed['result']['partitions']:
        indices=[i for i,key in enumerate(mixed['matrix']['columns']) if (by_id[key]['measure'],by_id[key]['currency'])==(p['measure'],p['currency'])]
        values=sorted(sum(Fraction(row['losses'][i]) for i in indices) for row in mixed['matrix']['rows'])
        m=len(values)*(1-alpha);left=m;tail_sum=0
        for loss in reversed(values):
            take=min(left,Fraction(1));tail_sum+=take*loss;left-=take
            if left==0:break
        k=(len(values)*alpha.numerator+alpha.denominator-1)//alpha.denominator
        assert Fraction(p['valueAtRisk'])==values[k-1] and Fraction(p['expectedShortfall'])==tail_sum/m
    assert len(mixed['exposures'])==len(mixed['matrix']['columns'])
    print('PASS historical mixed portfolio: complete shared factor union and independent full-PV net-before-quantile oracles for every native measure/currency partition',flush=True)

    from check_historical_rolling import run_checks as check_rolling
    check_rolling(c,c.call('workspace')['frames'])

    operation=str(uuid4());submission={'requestId':operation,'request':request}
    job=c.call('portfolio/risk/historical/jobs',submission)
    assert c.call('portfolio/risk/historical/jobs',submission)['jobId']==job['jobId']
    run_worker()
    stored=c.call('portfolio/risk/jobs/'+job['jobId'])
    assert stored['state']=='completed',stored
    assert stored['result']['historical']['result']==result and stored['result']['historical']['matrix']==study['matrix']
    assert stored['result']['historical']['studyId']==study['studyId']
    print('PASS historical retained worker: exact native values, source history, empirical risk and original request replay match immediate execution',flush=True)
    archive=c.call('portfolio/risk/historical/archive?limit=1')
    assert [j['id'] for j in archive['items']]==[job['jobId']] and archive['items'][0]['kind']=='historical'
    normal_request={'history':history['request'],**compared['comparison']}
    nj=c.call('portfolio/risk/parametric/jobs',{'requestId':str(uuid4()),'request':normal_request});run_worker()
    assert c.call('portfolio/risk/jobs/'+nj['jobId'])['state']=='completed'
    both=c.call('portfolio/risk/statistical/archive?limit=1');assert both['items'][0]['id']==nj['jobId'] and both['next']
    from urllib.parse import urlencode
    tail=c.call('portfolio/risk/statistical/archive?'+urlencode({'limit':1,'before':both['next']['createdAt'],'id':both['next']['id']}))
    assert tail['items'][0]['id']==job['jobId'] and tail['next'] is None
    assert [j['id'] for j in c.call('portfolio/risk/parametric/archive?limit=25')['items']]==[nj['jobId']]
    print('PASS historical archive: method-separated readers and combined cursor pages retain authorized report identities',flush=True)
    from check_historical_risk_db import admission_checks
    admission_checks(query,manifest,job['jobId'])
    from check_parametric_risk_db import run_checks as check_custody
    check_custody(query,manifest,job['jobId'],run_worker,method='historical')
    c.call('profile',{'profile':'viewer'})
    for path in ('portfolio/risk/jobs/'+job['jobId'],'portfolio/risk/historical/archive?limit=1','portfolio/risk/statistical/archive?limit=1'):
        c.call(path,expected=403)
    c.call('portfolio/risk/historical',request,expected=403)
    c.call('profile',{'profile':'risk'})
    print('PASS historical authority: retained capture does not grant viewer calculation, report, export or archive access',flush=True)
    # New corrections are separate source facts. An independently captured new
    # forecast prefix sees a constant column; the old job retains its original pins.
    query(f"""WITH selected AS(
      SELECT o.* FROM market_data.observations o JOIN risk.job_history_observations p ON(p.catalog_id,p.observation_id)=(o.catalog_id,o.id)
      WHERE p.job_id='{job['jobId']}' AND o.observed_at>='2020-01-06' AND o.observed_at<'2020-01-09'
    ) INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
    SELECT catalog_id,md5(id::text||'/constant')::uuid,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id||'/constant',role,publication_state,observed_at,clock_timestamp(),(SELECT value FROM selected ORDER BY observed_at LIMIT 1),id FROM selected;""")
    zero=deepcopy(history['request']);zero.pop('expectedManifestId');zero['from']='2020-01-06';zero['to']='2020-01-09';zero['policy']['minimumSamples']=2
    zero['scope']['context'].pop('contextId');zero['scope']['context']['knowledgeMode']='current'
    zero_history=c.call('portfolio/risk/history',zero)
    assert len(zero_history['manifest']['samples'])==2 and all(all(Fraction(v)==0 for v in s['changes']) for s in zero_history['manifest']['samples'])
    zero_request={**request,'history':zero_history['request']};zj=c.call('portfolio/risk/historical/jobs',{'requestId':str(uuid4()),'request':zero_request});run_worker()
    stored_zero=c.call('portfolio/risk/jobs/'+zj['jobId']);assert stored_zero['state']=='completed',stored_zero
    z=stored_zero['result']['historical'];assert z['result']['samples']==2 and len(z['request']['factors'])==len(z['factors'])==1
    assert all(Fraction(p['valueAtRisk'])==Fraction(p['expectedShortfall'])==0 for p in z['result']['partitions'])
    assert query(f"SELECT count(*) FROM risk.job_scenario_axes WHERE job_id='{zj['jobId']}';")=='1'
    assert c.call('portfolio/risk/jobs/'+job['jobId'])['result']['historical']['result']==result
    print('PASS zero historical column: complete native factor declaration survives SQL retention and actual worker replay; all sample probability mass remains and prior report is unchanged',flush=True)
    return dict(client=c,scope=scope,request=request,direct=direct,job=stored,catalog=catalog)


ASSISTANT_RESULT_BYTES=65536


def check_assistant_reads(c,scope,request,direct,compared,paired,history,insufficient):
    """The assistant's historical summary and one-scenario drill-down are bounded projections of the Risk page's own report."""
    import json
    from check_parametric_risk import exact_amounts
    def size(value):return len(json.dumps(value,separators=(',',':')).encode())
    study=direct['historical'];result=study['result'];manifest=history['manifest']
    session={s['close']:s['date'] for s in manifest['sessions']}
    changes={r['id']:r for r in manifest['samples']}
    setup=c.call('portfolio/risk/statistical/setup',scope)
    assert size(setup)<=ASSISTANT_RESULT_BYTES and setup['historical']==c.call('portfolio/risk/catalog',scope)['historical']
    uncaptured=deepcopy(request);uncaptured['history'].pop('expectedManifestId')
    for body in (request,uncaptured):
        summary=c.call('portfolio/risk/historical/summary',body)
        assert size(summary)<=ASSISTANT_RESULT_BYTES,size(summary)
        summary=exact_amounts(summary)
        assert summary['available'] and summary['studyId']==study['studyId'] and summary['historyId']==manifest['id']
        assert summary['policy']==request['policy'] and summary['samples']==result['samples']
        assert summary['history']=={k:request['history'][k] for k in ('calendar','from','to','policy')}
        assert [(f['id'],f['label'],f['unit']) for f in summary['factors']]==[(f['id'],f['factor']['label'],f['factor']['unit']) for f in study['factors']]
        assert summary['books']==study['books']
        trades={e['tradeId']:{'id':e['tradeId'],'reference':e['tradeReference'],'instrumentType':e['instrumentType'],'bookId':e['bookId']} for e in study['exposures']}
        assert summary['trades']==sorted(trades.values(),key=lambda t:t['reference'])
        assert len(summary['partitions'])==len(result['partitions'])
        for compact,exact,shown in zip(summary['partitions'],result['partitions'],study['partitions']):
            assert (compact['measure'],compact['currency'])==(exact['measure'],shown['currency'])
            for key in ('baseValue','mean','valueAtRisk','expectedShortfall','tailSampleMass','quantileTies'):assert compact[key]==exact[key],key
            assert compact['books']==[{'bookId':b['id'],'valueAtRisk':b['valueAtRisk'],'expectedShortfall':b['expectedShortfall']} for b in exact['books']]
            assert compact['trades']==[{'tradeId':t['id'],'valueAtRisk':t['valueAtRisk'],'expectedShortfall':t['expectedShortfall']} for t in exact['trades']]
            tail=sorted((x for x in exact['samples'] if Fraction(x['varWeight'])!=0 or Fraction(x['esWeight'])!=0),key=lambda x:(-Fraction(x['loss']),x['id']))
            assert compact['tail']==[{'sample':x['id'],'from':session[changes[x['id']]['from']['at']],'to':session[changes[x['id']]['to']['at']],
                'loss':x['loss'],'varWeight':x['varWeight'],'esWeight':x['esWeight']} for x in tail]
            assert tail and Fraction(compact['expectedShortfall'])==sum(Fraction(x['loss'])*Fraction(x['esWeight']) for x in tail)
        assert summary['comparison'] is None
    paired_summary=exact_amounts(c.call('portfolio/risk/historical/summary',compared))
    normal=paired['comparison']['report']['result']
    assert paired_summary['comparison']['studyId']==study['studyId'] and paired_summary['comparison']['available']==normal['available']
    labels={(p['measure'],json.dumps(p['currencyReference'],sort_keys=True)):p['currency'] for p in study['partitions']}
    assert paired_summary['comparison']['partitions']==[{'measure':p['measure'],'currency':labels[(p['measure'],json.dumps(p['currency'],sort_keys=True))],
        **{k:p[k] for k in ('mean','standardDeviation','valueAtRisk','expectedShortfall')}} for p in normal['partitions']]

    worst=paired_summary['partitions'][0]['tail'][0]['sample']
    row=next(r for r in study['matrix']['rows'] if r['id']==worst)
    drill=c.call('portfolio/risk/historical/sample',{'request':request,'sample':worst})
    assert size(drill)<=ASSISTANT_RESULT_BYTES
    drill=exact_amounts(drill)
    assert drill['studyId']==study['studyId'] and drill['sample']==worst
    assert (drill['from'],drill['to'])==(session[changes[worst]['from']['at']],session[changes[worst]['to']['at']])
    assert [(m['factor'],m['from'],m['to'],m['move']) for m in drill['moves']]==[(i,
        changes[worst]['from']['marks'][i]['value'],changes[worst]['to']['marks'][i]['value'],v) for i,v in enumerate(changes[worst]['changes'])]
    day={d['at']:d['choices'] for d in manifest.get('dayPrices') or []}
    assert [(m['fromPriceType'],m['toPriceType']) for m in drill['moves']]==[(day[changes[worst]['from']['at']][i]['selectedType'],day[changes[worst]['to']['at']][i]['selectedType'])
        if day else (None,None) for i in range(len(drill['moves']))]
    assert drill['partitions']==[{'measure':p['measure'],'currency':p['currency'],'loss':next(x['loss'] for x in e['samples'] if x['id']==worst)}
        for p,e in zip(study['partitions'],result['partitions'])]
    exposures={e['id']:e for e in study['exposures']}
    assert [(h['tradeReference'],h['bindingId'],h['measure'],h['baseValue'],h['scenarioValue'],h['loss']) for h in drill['holdings']]==[
        (exposures[k]['tradeReference'],exposures[k]['bindingId'],exposures[k]['measure'],exposures[k]['baseValue'],row['scenarioValues'][i],row['losses'][i])
        for i,k in enumerate(study['matrix']['columns'])]
    assert c.call('portfolio/risk/historical/sample',{'request':request,'sample':'0'*64},expected=422)['code']=='risk_historical_sample'
    assert exact_amounts(c.call('portfolio/risk/historical/sample',{'request':compared,'sample':worst}))==drill
    current=deepcopy(uncaptured);current['history']['scope']['context']['knowledgeMode']='current';current['history']['scope']['context'].pop('contextId')
    assert c.call('portfolio/risk/historical/summary',current,expected=422)['code']=='risk_historical_scope'
    assert c.call('portfolio/risk/historical/sample',{'request':current,'sample':worst},expected=422)['code']=='risk_historical_scope'
    unavailable=exact_amounts(c.call('portfolio/risk/historical/summary',insufficient))
    assert not unavailable['available'] and unavailable['unavailableReasons']==['insufficient_common_history'] and unavailable['partitions']==[]
    sample=c.call('portfolio/risk/historical/sample',{'request':insufficient,'sample':history['manifest']['samples'][0]['id']},expected=422)
    assert sample['code']=='risk_historical_unavailable' and 'insufficient common history' in sample['message']
    c.call('profile',{'profile':'viewer'})
    for path,body in (('portfolio/risk/statistical/setup',scope),('portfolio/risk/historical/summary',request),('portfolio/risk/historical/sample',{'request':request,'sample':worst})):
        c.call(path,body,expected=403)
    c.call('profile',{'profile':'risk'})
    print('PASS historical assistant reads: bounded summary, tail scenarios and one-scenario drill-down repeat the exact full-PV report',flush=True)
