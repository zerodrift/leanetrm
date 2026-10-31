"""Owned, synthetic quantity-read workloads; no market-data or pricing substitute."""
import json
import platform
import os
import statistics
import subprocess
import time
from uuid import uuid4
from fractions import Fraction
from preview_client import PreviewClient,initial_frame
from preview_fixture import trade_booking
from check_booked_side_programs import runtime_query
from check_gas_daily_quantities import assert_batch_equivalence


def run(url,query,manifest,api_pid):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
    workspace=client.call('workspace');ctx={'snapshotId':initial_frame(workspace,'2020-12-18')['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    catalog=client.call('gas/catalog',ctx)
    locations=[p for p in catalog['locations'] if p.get('valuation') is not None]
    results=[]
    for side_count,days in [(1,1),(4,31)]:
        depth=30 if side_count==1 else 3
        physical=[]
        for n in range(side_count):
            point=locations[min(n//2,len(locations)-1)]
            basis='period_total_equal_days' if n%2==0 else 'per_operating_day'
            physical.append({'code':f'gas_{n}','pipeline':point['pipeline'],'zone':point['zone'],'deliveryPoint':point['deliveryPoint'],'product':point['product'],
                'direction':'receive','deliveryFrom':'2021-01-01','deliveryTo':'2021-01-02' if days==1 else '2021-02-01','quantityBasis':basis,'quantity':str(1000*days if basis=='period_total_equal_days' else 1000),'link':None})
        terms={**ctx,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':physical and catalog['locations'][0]['definitionRevisionId'],'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1','physicalSides':physical,'cashSides':[]}
        trade=client.call('gas/composition/book',terms)
        request={'context':ctx,'tradeId':trade['tradeId'],'selectionPolicy':'operational_projection_v1','includeHistory':False,'measurePerformance':True}
        grid=client.call('gas/quantities/read',request)
        selected_context=grid['context']['contextId'];seed_start=time.perf_counter()
        # Each bounded batch uses the ordinary authorized write gate. Its command
        # outcomes carry the causal predecessor; no raw INSERT fabricates facts.
        for obligation in sorted({r['obligationId'] for r in grid['rows']}):
            calls=[]
            for row in (r for r in grid['rows'] if r['obligationId']==obligation):
                for k,kind in enumerate(['nominated','confirmed','scheduled','allocated']):
                    calls.append('previous:=NULL;')
                    for revision in range(1,depth+1):
                        command={'context':ctx,'tradeId':trade['tradeId'],'expectedRevision':1,'operationId':str(uuid4()),'obligationId':obligation,'kind':kind,'status':'provisional','day':row['day'],'quantity':str(900+10*k+revision),'evidence':'Synthetic quantity benchmark','expectedPreviousId':None}
                        encoded=json.dumps(command).replace("'","''")
                        calls.append(f"outcome:=security.record_gas_daily_quantity('{manifest['catalogId']}',('{encoded}'::jsonb)||jsonb_build_object('expectedPreviousId',previous),'2020-12-18T16:00:00Z','{selected_context}');previous:=(outcome->>'revisionId')::uuid;")
            runtime_query(query,manifest,'DO $seed$ DECLARE previous uuid;outcome jsonb;BEGIN '+''.join(calls)+'END $seed$;')
        seed_ms=(time.perf_counter()-seed_start)*1000
        measurements=[];stages=[];sizes=[];max_rss=0
        for _ in range(8):
            begin=time.perf_counter();value=client.call('gas/quantities/read',request);measurements.append((time.perf_counter()-begin)*1000)
            assert len(value['rows'])==side_count*days
            assert sum(Fraction(int(r['selected']['numerator']),int(r['selected']['denominator'])) for r in value['rows'])==(930+depth)*side_count*days
            assert value['history']==[] and value['performance']['uniquePrograms']==side_count
            stages.append(value['performance']);sizes.append(len(json.dumps(value).encode()))
            max_rss=max(max_rss,int(subprocess.check_output(['ps','-o','rss=','-p',str(api_pid)]).strip()))
        print(json.dumps({'benchmarkProgress':{'physicalSides':side_count,'days':days,'samplesMs':[round(t,2) for t in measurements],'lastStages':stages[-1]}}),flush=True)
        begin=time.perf_counter()
        history=client.call('gas/quantities/history',{'context':{**ctx,'knowledgeMode':'retained','contextId':value['context']['contextId']},'tradeId':trade['tradeId']})
        history_ms=(time.perf_counter()-begin)*1000
        assert len(history['history'])==min(100,4*depth*side_count*days)
        assert all(h['obligation_id'] in {r['obligationId'] for r in grid['rows']} for h in history['history'])
        assert len({h['id'] for h in history['history']})==len(history['history'])
        assert [h['publication_order'] for h in history['history']]==sorted((h['publication_order'] for h in history['history']),reverse=True)
        oracle_begin=time.perf_counter();assert_batch_equivalence(query,manifest,trade['tradeId'],value['context']['contextId'],'2020-12-18T16:00:00Z',include_history=side_count==1);oracle_ms=(time.perf_counter()-oracle_begin)*1000
        results.append({'physicalSides':side_count,'gasDaysPerSide':days,'quantityCells':side_count*days,'series':4*side_count,'visibleHeads':4*side_count*days,'revisionFacts':4*depth*side_count*days,'revisionsPerSeriesDay':depth,'uniquePrograms':stages[-1]['uniquePrograms'],'scenarios':0,
            'evaluatedQuantityCells':side_count*days,'retainedProgramsReused':side_count,'seedMs':round(seed_ms,2),'firstAfterSeedMs':round(measurements[0],2),'warmMedianMs':round(statistics.median(measurements[1:]),2),'warmMaxMs':round(max(measurements[1:]),2),
            'stageMedianMs':{k:round(statistics.median(s[k] for s in stages[1:]),2) for k in ['metadataMs','headsMs','historyMs','programsMs','selectionMs']},'historyReadMs':round(history_ms,2),'maxResponseBytes':max(sizes),'observedApiRssKiB':max_rss,'rowSecurityOracleMatched':['heads','history'] if side_count==1 else ['heads'],'oracleComparisonMs':round(oracle_ms,2)})
    print(json.dumps({'workload':'gas_quantity_reads_v1','environment':{'os':platform.platform(),'architecture':platform.machine(),'logicalCpus':os.cpu_count(),'cpu':subprocess.check_output(['sysctl','-n','machdep.cpu.brand_string'],text=True).strip() if platform.system()=='Darwin' else platform.processor(),'sdk':'10.0.402','database':'stock PostgreSQL '+query('SHOW server_version;'),'samplesPerCase':8},'cacheState':'API has serviced fixture booking and preparation reads; first sample is not a cold-process claim. No quantity-result cache.','results':results},indent=2),flush=True)
