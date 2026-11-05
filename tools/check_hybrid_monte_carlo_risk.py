"""Owned full-reference comparisons, consent custody and complete measured work."""
from copy import deepcopy
from uuid import uuid4
import json
import os
import platform
import statistics
import time


def upgrade_checks(query,migration):
    guard=migration[:migration.index('CREATE OR REPLACE FUNCTION')]
    for name in ('retain_monte_carlo','summary'):
        signature=f'risk.{name}(risk.jobs)'
        definition=query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
        body=(definition.replace('BEGIN','BEGIN PERFORM 1;',1) if name=='retain_monte_carlo' else definition.replace('SELECT jsonb_build_object','SELECT /*changed*/ jsonb_build_object',1))+';'
        for mutation in (body,f'ALTER FUNCTION {signature} SET search_path=pg_catalog,public;',f'GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;',f'ALTER FUNCTION {signature} OWNER TO openetrm_runtime;'):
            try:query('BEGIN;'+mutation+guard+'ROLLBACK;')
            except RuntimeError as error:assert 'hybrid_monte_carlo_upgrade' in str(error),str(error)
            else:raise AssertionError('Changed hybrid predecessor helper accepted upgrade')
        assert query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")==definition
    print('PASS hybrid upgrade: full retention/summary body, owner, ACL and search-path mutations refuse atomically',flush=True)


def selected(profile,request,*,validation=64,fallback=False):
    h=profile['hybrid']
    return {**request,'policy':{**request['policy'],**h['profile']},
        'analysis':{**profile['analysis']['profile'],**profile['analysis']['defaults'],'retainedComponents':1,
                    'valuationPolicy':h['fittingPolicies']['fullPvFallback' if fallback else 'strict']},
        'hybrid':{**h['executionProfile'],'validationPaths':validation,'validationSeed':h['defaults']['validationSeed']}}


def economic(study):
    result=deepcopy(study)
    result['hybrid'].pop('cost')
    return result


def admission_checks(query,manifest,job_id):
    row=json.loads(query(f"SELECT jsonb_build_object('request',request,'frame',frame,'context',context_id,'revision',revision_id,'at',valuation_at) FROM risk.jobs WHERE id='{job_id}';"))
    cases=[]
    for field,value in [('validationPaths',1),('validationPaths',row['request']['monteCarlo']['policy']['paths']+1),('validationPaths','64'),
                        ('validationSeed','01'),('validationSeed','18446744073709551616'),('validationSeed',None),
                        ('validationPolicy','select_largest_losses'),('fallbackScope','replace_tail_rows'),('unknown',True)]:
        bad=deepcopy(row['request']);bad['monteCarlo']['hybrid'][field]=value;cases.append(bad)
    for field in row['request']['monteCarlo']['hybrid']:
        bad=deepcopy(row['request']);bad['monteCarlo']['hybrid'].pop(field);cases.append(bad)
    for field,value in [('analysis',None),('hybrid',None)]:
        bad=deepcopy(row['request']);bad['monteCarlo'][field]=value;cases.append(bad)
    for field,value in [('convention','dated_factor_full_pv_monte_carlo_v1'),('valuationPolicy','current_instrument_pv_frozen_nonfactor_inputs_v1'),('samplingUncertainty','binomial_order_statistic_binary64_95_v1')]:
        bad=deepcopy(row['request']);bad['monteCarlo']['policy'][field]=value;cases.append(bad)
    bad=deepcopy(row['request']);bad['monteCarlo']['analysis']['valuationPolicy']='full_reference_no_fallback_qualification_v1';cases.append(bad)
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    literal=lambda value:"'"+json.dumps(value).replace("'","''")+"'::jsonb"
    for request in cases:
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        try:query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT security.use_market_context('{catalog}','{row['context']}');SELECT security.use_trading_context('{catalog}','{row['at']}');SELECT risk.submit('{catalog}','{uuid4()}','{row['context']}','{row['revision']}','{row['at']}',{literal(request)},{literal(row['frame'])});COMMIT;")
        except RuntimeError as error:assert 'risk_monte_carlo_input' in str(error),str(error)
        else:raise AssertionError('Hybrid mode silently admitted an incomplete or conflicting policy')
    print('PASS hybrid SQL: exact mode/analysis/panel tuple, explicit count/seed/scope and all missing/unknown/mixed policies refuse',flush=True)


def run_checks(c,profile,request,reference,query,manifest,run_worker):
    spec=selected(profile,request)
    direct=c.call('portfolio/risk/monte-carlo',spec);v=direct['monteCarlo'];x=v['hybrid']
    assert x['appliedMethod']=='pca_delta_gamma' and x['approximate'] and not x['fallbackApplied'] and not x['fallbackReasons']
    assert v['generation']['samples']==reference['generation']['samples'] and v['matrix']==reference['matrix']
    assert v['result']['partitions']==reference['result']['partitions']
    assert all('valueAtRiskInterval' not in d for d in v['result']['diagnostics'])
    assert x['validation']['observedPanelPaths']==64 and x['validation']['unpricedPaths']==request['policy']['paths']-64
    assert [p['phase'] for p in x['cost']['phases']]==['fitting','validation']
    assert direct['work']['scenarioCount']==12+64+2
    assert direct['work']['unitPricerCalls']==sum(p['work']['unitPricerCalls'] for p in x['cost']['phases'])
    print('PASS hybrid HTTP: unchanged full-native shocks, exact linear full-N oracle, observed-only panel, no interval and complete actual work',flush=True)
    command={'requestId':str(uuid4()),'request':spec};job=c.call('portfolio/risk/monte-carlo/jobs',command)
    assert c.call('portfolio/risk/monte-carlo/jobs',command)['jobId']==job['jobId']
    run_worker();stored=c.call('portfolio/risk/jobs/'+job['jobId']);assert stored['state']=='completed',stored
    assert economic(stored['result']['monteCarlo'])==economic(v)
    item=c.call('portfolio/risk/statistical/archive?limit=1')['items'][0]
    assert item['kind']=='hybrid_monte_carlo' and item['appliedMethod']=='pca_delta_gamma' and item['approximate'] and not item['fallbackApplied'],item
    admission_checks(query,manifest,job['jobId'])
    from check_parametric_risk_db import run_checks as check_custody
    check_custody(query,manifest,job['jobId'],run_worker,method='monteCarlo')
    opted=selected(profile,request,fallback=True)
    ov=c.call('portfolio/risk/monte-carlo',opted)['monteCarlo']
    assert not ov['hybrid']['fallbackApplied'] and ov['hybrid']['approximate'] and ov['studyId']!=v['studyId']
    assert ov['matrix']==v['matrix'] and ov['result']['partitions']==v['result']['partitions']
    all_paths=selected(profile,request,validation=request['policy']['paths'])
    complete=c.call('portfolio/risk/monte-carlo',all_paths)['monteCarlo']
    assert complete['hybrid']['validation']['unpricedPaths']==0 and complete['hybrid']['approximate']
    assert complete['matrix']==reference['matrix']
    print('PASS hybrid worker/archive: exact retained economic replay, original consent on retries, explicit applied method and all-panel control',flush=True)
    # Same captured one-future workload, complete request/history preparation and
    # JSON transport included. No production-throughput claim follows from this.
    n=1024;full={**request,'policy':{**request['policy'],'paths':n}};approx=selected(profile,full)
    c.call('portfolio/risk/monte-carlo',full);c.call('portfolio/risk/monte-carlo',approx)
    timings={'full_pv':[],'hybrid':[]};runs={}
    for _ in range(3):
        for name,body in [('full_pv',full),('hybrid',approx)]:
            started=time.perf_counter();runs[name]=c.call('portfolio/risk/monte-carlo',body);timings[name].append(time.perf_counter()-started)
    a,b=(runs[name] for name in ('full_pv','hybrid'))
    assert a['monteCarlo']['matrix']==b['monteCarlo']['matrix'] and a['monteCarlo']['result']['partitions']==b['monteCarlo']['result']['partitions']
    assert b['work']['unitPricerCalls']<a['work']['unitPricerCalls']
    benchmark={'workload':'one captured futures trade; fixed native full-PV risk model; linear complete-reference oracle','paths':n,'validationPaths':64,
        'factorCount':len(v['factors']),'holdingCount':len(v['exposures']),'uniqueValuations':a['work']['uniqueValuations'],
        'uniqueMarketContracts':a['work']['uniqueMarketContracts'],'fullPvWork':a['work'],'hybridWork':b['work'],
        'roundtripSeconds':timings,'medianRoundtripSeconds':{name:statistics.median(values) for name,values in timings.items()},
        'environment':{'platform':platform.platform(),'machine':platform.machine(),'python':platform.python_version(),'measurement':'warm API request plus complete history/preparation, valuation and JSON transport; three alternating repeats','memory':'not separately measured; no memory improvement claimed'}}
    worker_metrics={}
    for name,body in [('full_pv',full),('hybrid',approx)]:
        queued=c.call('portfolio/risk/monte-carlo/jobs',{'requestId':str(uuid4()),'request':body})
        started=time.perf_counter();peak=run_worker(measure=True);elapsed=time.perf_counter()-started
        stored=c.call('portfolio/risk/jobs/'+queued['jobId']);assert stored['state']=='completed',stored
        worker_metrics[name]={'elapsedSeconds':elapsed,'sampledPeakRssKiB':peak,'measurement':'one fresh worker process, 50 ms RSS samples, startup/claim/history/calculation/persistence included'}
    benchmark['worker']=worker_metrics
    benchmark['environment']['memory']='fresh worker process sampled peak RSS recorded separately; not an API heap measurement or a memory savings claim'
    target=os.environ.get('OPENETRM_HYBRID_BENCHMARK_EVIDENCE')
    if target:
        from pathlib import Path
        Path(target).write_text(json.dumps(benchmark,indent=2)+'\n')
    print('PASS hybrid complete-reference workload benchmark: '+json.dumps(benchmark),flush=True)


def option_checks(url,manifest,run_worker):
    """Exact zero-error policy on a nonlinear option must refuse unless opted in."""
    from preview_client import PreviewClient
    from check_futures_option_lifecycle import side_lots
    base=PreviewClient(url);base.bootstrap()
    c=PreviewClient(url,'exchange');c.opener=base.opener;c.bootstrap();c.call('profile',{'profile':'trader'})
    frames=c.call('workspace')['frames']
    first,frame=(next(f for f in frames if f['at'].startswith(day)) for day in ('2026-10-13T16','2026-10-16'))
    current={'snapshotId':frame['id'],'knowledgeMode':'current'}
    catalog=c.call('options/catalog',{**current,'convention':'futures_option_premium_v1'})
    option=next(o for o in catalog['options'] if o['product']=='nymex_on' and o['right']=='call' and o['series']!='nymex_on_20261201_c2.00')
    book=catalog['books'][0]['id']
    trade=c.call('options/book',{'operationId':str(uuid4()),'snapshotId':first['id'],'knowledgeMode':'current',
        'convention':'futures_option_premium_v1','bookId':book,'series':option['series'],**side_lots('1'),'premium':'0.125',
        'premiumPaymentDate':'2026-10-19','premiumStyle':'premium_paid_up_front',
        'legalEntityId':catalog['legalEntities'][0]['id'],'legalEntityRevision':catalog['legalEntities'][0]['revision'],
        'counterpartyId':catalog['counterparties'][0]['id'],'counterpartyRevision':catalog['counterparties'][0]['revision']})
    c.call('profile',{'profile':'risk'})
    blotter=c.call('trading/blotter',{'context':{**current,'convention':'trading_workspace_v1'},'view':{'mode':'trades','bookIds':[book],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}})
    scope={'context':{**current,'convention':'trading_workspace_v1','knowledgeMode':'retained','contextId':blotter['context']['contextId']},'bookIds':[book],'portfolioIds':[],'tradeIds':[trade['tradeId']]}
    risk=c.call('portfolio/risk/catalog',scope);profile=risk['monteCarlo']
    calendar=next(x['reference'] for x in risk['history']['calendars'] if x['reference']['code']=='source_business_days')
    history=c.call('portfolio/risk/history',{'scope':scope,'pricingPolicy':risk['pricingPolicy'],'policy':{**risk['history']['profile'],'minimumSamples':2},
        'calendar':calendar,'from':'2026-10-13','to':'2026-10-17','closeTime':'16:00:00','ambiguousTime':'reject','invalidTime':'reject'})
    request={'history':history['request'],'policy':{**profile['profile'],**profile['defaults'],'paths':128,'seed':'42'}}
    reference=c.call('portfolio/risk/monte-carlo',request)['monteCarlo']
    strict=selected(profile,request,validation=32)
    strict['analysis'].update(lossAbsolute='0',lossRelative='0',stabilityRelative='0')
    refused=c.call('portfolio/risk/monte-carlo',strict,expected=422)
    assert refused['code']=='risk_pca_hybrid_qualification',refused
    queued=c.call('portfolio/risk/monte-carlo/jobs',{'requestId':str(uuid4()),'request':strict});run_worker()
    failed=c.call('portfolio/risk/jobs/'+queued['jobId'])
    assert failed['state']=='failed' and failed.get('result') is None and failed['errorCode']=='risk_pca_hybrid_qualification',failed
    opted=deepcopy(strict);opted['analysis']['valuationPolicy']=profile['hybrid']['fittingPolicies']['fullPvFallback']
    full=c.call('portfolio/risk/monte-carlo',opted)['monteCarlo'];x=full['hybrid']
    assert x['appliedMethod']=='full_pv' and x['fallbackApplied'] and not x['approximate'] and x['fallbackReasons']
    assert all(c['method']=='full_pv' for c in x['classifications']) and x['validation']['unpricedPaths']==0
    assert full['matrix']==reference['matrix'] and full['result']['partitions']==reference['result']['partitions']
    assert all('valueAtRiskInterval' not in d for d in full['result']['diagnostics'])
    assert x['cost']['phases'][-1]['phase']=='consented_full_pv_fallback'
    queued=c.call('portfolio/risk/monte-carlo/jobs',{'requestId':str(uuid4()),'request':opted});run_worker()
    stored=c.call('portfolio/risk/jobs/'+queued['jobId']);assert stored['state']=='completed',stored
    assert economic(stored['result']['monteCarlo'])==economic(full)
    item=c.call('portfolio/risk/statistical/archive?limit=1')['items'][0]
    assert item['kind']=='hybrid_monte_carlo' and item['appliedMethod']=='full_pv' and item['fallbackApplied'] and not item['approximate'],item
    print('PASS hybrid nonlinear option HTTP/worker/archive: strict zero-error policy fails without a result; explicit opt-in reprices every holding/path, matches the independent full-PV run and retains visible reasons',flush=True)
