"""Managed-worker qualification with exact gas settlement quotes from the owned synthetic fixture."""
import json
import time
from concurrent.futures import ThreadPoolExecutor
from decimal import Decimal
from fractions import Fraction
from uuid import UUID, uuid4

from check_managed_pool import CLUSTER, NODE, SERVICE, TASK
from check_managed_pool import selected_request


def historical_shifts(manifest, query, factors, day, convention):
    # Only the fixture driver reads the synthetic observation pins. Workers use
    # ordinary authorized, pinned risk requests and never query a history by date.
    frame, = [f for f in manifest['frames'] if f['at'][:10] == day]
    ids = ','.join("'" + str(UUID(x)) + "'" for x in frame['observationIds'])
    observations = json.loads(query(f"""SELECT jsonb_agg(jsonb_build_object(
        'series',series_id,'source',source_code,'value',value::text,'id',id,
        'catalog',catalog_id,'revision',definition_revision_id,'role',role,'at',observed_at))
        FROM market_data.observations WHERE id IN ({ids});"""))
    shifts = []
    for factor in factors:
        revision = factor['factor']['target']['curve']['revision']
        quote, = [o for o in observations if
                  (o['series'], o['source'], o['catalog'], o['revision'], o['role']) ==
                  (factor['seriesId'], factor['source']['code'], revision['catalogId'], revision['revisionId'], 'daily_settlement')]
        assert quote['at'][:10] == day
        change = Decimal(quote['value']) - Decimal(factor['value'])
        if change:
            shifts.append({'factor':factor['factor'], 'size':str(change), 'convention':convention})
    assert shifts, f'Historical fixture produced no changes on {day}'
    return shifts


def run(manifest, query, admin, risk, worker, barrier):
    _, base = selected_request(risk)
    catalog = risk.call('portfolio/risk/catalog', {k:base[k] for k in ('context','bookIds','portfolioIds')})
    selected = [f for f in catalog['factors'] if f['role']=='DailySettlement' and f['quote']=='test_gas_outright']
    assert 0 < len(selected) <= 16, 'Expected native held gas settlement quotes in the fixture'
    original = risk.call('portfolio/risk', base)
    def capacity(desired, tasks):
        generation = admin.call('jobs')['workerPool']['generation']
        receipt = admin.call('infrastructure/pool/scale',{'requestId':str(uuid4()),'clusterId':CLUSTER,
                          'expectedVersion':generation,'replicas':desired})
        # Docker placement/inspection is simulated. Claims and published results are real.
        query(f"""SET SESSION AUTHORIZATION jobs_fixture_worker;SELECT automation.inspect_pool(
            '{CLUSTER}','{SERVICE}',{receipt['generation']},{desired},'{json.dumps(tasks)}',true,'{receipt['generation']}');""")
    tasks = [{'id':TASK,'node':NODE,'slot':1,'state':'running'},
             {'id':'synthetictask000000000002','node':'syntheticnode0000000000002','slot':2,'state':'running'}]
    capacity(2,tasks)
    endpoint='portfolio/risk/jobs'
    jobs=[]
    for day in ('2020-12-15','2020-12-16','2020-12-17'):
        shocks=historical_shifts(manifest,query,selected,day,catalog['stressConvention'])
        request={**base,'stress':shocks}
        direct=risk.call('portfolio/risk',request)
        receipt=risk.call(endpoint,{'requestId':str(uuid4()),'request':request})
        assert receipt['state']=='queued'
        jobs.append((day,receipt['id'],direct))
    assert len({j[2]['scenario']['id'] for j in jobs})==len(jobs)
    with ThreadPoolExecutor(max_workers=2) as threads:
        with barrier():
            futures=[threads.submit(worker,task) for task in tasks]
            deadline=time.monotonic()+4
            while time.monotonic()<deadline:
                claims=json.loads(query(f"""SELECT coalesce(jsonb_agg(jsonb_build_object('task',t.task_id,
                    'job',t.job_id,'attempt',j.attempt,'token',j.lease_token)),'[]') FROM automation.pool_tasks t
                    JOIN risk.jobs j ON j.id=t.job_id WHERE t.cluster_id='{CLUSTER}' AND j.state='running';"""))
                if len(claims)==2:break
                time.sleep(.025)
            else:raise AssertionError('Two concurrent managed historical scenario claims were not observed')
            assert {c['task'] for c in claims}=={t['id'] for t in tasks}
            assert len({c['job'] for c in claims})==len({c['token'] for c in claims})==2
            assert {c['job'] for c in claims}<={j[1] for j in jobs}
        for future in futures:future.result()
    worker(tasks[0])
    def totals(report):
        return {(r['bookId'],r['measure'],tuple(sorted(r['values']['currencyReference']['revision'].items())),r['values']['currencyReference']['code']):
                Fraction(r['values']['base']) for r in report['factors'][0]['totals']}
    baseline=totals(original)
    print(f"HISTORICAL_POOL positions={len(original['inputs']['positions'])} instruments={original['heldInstruments']} settlement_series={len(selected)} concurrent_claims=2",flush=True)
    for day,job_id,direct in jobs:
        receipt=risk.call(f'{endpoint}/{job_id}')
        assert receipt['state']=='completed'
        report=receipt['result']
        for field in ('context','snapshotId','asOf','inputs','scenario','factors','excludedPositions'):
            assert report[field]==direct[field], (day,field)
        assert report['inputs']['positions']==original['inputs']['positions']
        values=totals(report)
        assert values.keys()==baseline.keys()
        pnl={}
        for (book,measure,revision,currency),value in values.items():
            key=(measure,revision,currency)
            pnl[key]=pnl.get(key,Fraction())+value-baseline[(book,measure,revision,currency)]
        print('HISTORICAL_POOL',day,[(m,c,str(x)) for (m,r,c),x in pnl.items()],flush=True)
    capacity(0,[])
    print('PASS three historical quote-level replays, pinned holdings and clock, exact direct/worker results for each scenario and two simultaneous managed claims',flush=True)
