"""Independent existing-close risk acceptance in an owned synthetic database."""
from copy import deepcopy
from datetime import datetime,timedelta,timezone
from fractions import Fraction
import json
from pathlib import Path
import subprocess
from uuid import uuid4

from benchmark_ppa_http import HttpFixture,terms
from exact_values import fraction,instant
from preview_client import PreviewClient


def run_checks(url,query,manifest,dotnet,owned_root,app_environment,psql,environment):
    root=Path(owned_root).resolve()
    assert (root/'.openetrm-preview').is_file() and (root/'data/postmaster.pid').is_file()
    from check_pilot_close import prepare
    prepare(query,manifest)
    owner=PreviewClient(url);owner.bootstrap();owner.call('profile',{'profile':'approver'})
    fixture=HttpFixture(url,manifest,None)
    frames=fixture.client.call('workspace')['frames']
    live=next(f for f in frames if instant(f['at'])==instant(manifest['closeRisk']['at']) and 'test_existing_close_energy' not in f['curveContracts'] and not f.get('ppaBundle'))
    fixture.heads={s['id']:s['head'] for s in fixture.editor.call('interval-series')['series']}
    fixture.load(60,'2031-11-01','2051-11-01')
    book=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{book}','d0000000-0000-0000-0000-000000000001','Synthetic existing close risk',1,'2020-01-01T00:00:00Z');")
    trade,booking=fixture.book(terms(manifest),'2031-11-02T04:00:00Z','2031-11-03T05:00:00Z',book=book)
    frames=owner.call('workspace')['frames']
    live_owner=next(f for f in frames if instant(f['at'])==instant(manifest['closeRisk']['at']) and 'test_existing_close_energy' not in f['curveContracts'] and not f.get('ppaBundle'))
    policy=owner.call('jobs/pilot-close/catalog')['valuationPolicyChoices'][0]['policy']
    held_book=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{held_book}','d0000000-0000-0000-0000-000000000001','Synthetic retained certificate risk',1,'2020-01-01T00:00:00Z');")
    dates=manifest['closeRisk'];config=manifest['ppaBookings']['workloads']['60']
    fixture.load(60,dates['first'],dates['last'])
    day=dates['day'];first=day+'T17:00:00Z';last=day+'T19:00:00Z'
    fixture.revise(config['forecast'],first,['0.25','0.25']);fixture.revise(config['meter'],first,['50',None])
    from power_collection_fixture import Collector
    from zoneinfo import ZoneInfo
    collector=Collector(query,manifest,{'energy':{'provider':'PJM','sourceId':config['source'],'clock':'synthetic_eastern'}})
    day_start=datetime.fromisoformat(day).replace(tzinfo=ZoneInfo('America/New_York')).astimezone(timezone.utc)
    original_actual=collector.publish('energy',day,day_start,24,60,['40']*24,1)
    held_trade,_=fixture.book(terms(manifest,first=dates['first'],last=dates['last']),first,last,book=held_book)
    # Two distinct long contracts fit the per-scenario bound but exceed the
    # declared row-scenario budget with all native energy/certificate shocks.
    large_book=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{large_book}','d0000000-0000-0000-0000-000000000001','Synthetic admission bound',1,'2020-01-01T00:00:00Z');")
    long_terms=terms(manifest,first='2031-11-01',last='2051-11-01')
    for strike in ('40','41'):
        long_terms['memberships'][1]['side']['fixedPrice']['price']=strike
        fixture.book(long_terms,'2031-11-01T04:00:00Z','2051-11-01T04:00:00Z',book=large_book)
    # Retain a position in the separately published bundle. Scope inspection
    # must find it even though the selected close cannot value that bundle.
    mixed_book=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{mixed_book}','d0000000-0000-0000-0000-000000000001','Synthetic complete-book refusal',1,'2020-01-01T00:00:00Z');")
    fixture.book(terms(manifest),'2031-11-02T04:00:00Z','2031-11-03T05:00:00Z',book=mixed_book)
    from check_ppa_bookings_http import profile_terms
    bundle=manifest['ppaR3']['bundle'];other=profile_terms(manifest);other.update(bundle)
    other_frame=next(f for f in fixture.client.call('ppa/frames') if f.get('ppaBundle')==bundle and instant(f['at'])==instant(manifest['frames'][0]['at']))
    retained_frame=fixture.frame
    fixture.frame=other_frame['id']
    incompatible,_=fixture.book(other,'2031-11-02T04:00:00Z','2031-11-03T05:00:00Z',book=mixed_book)
    fixture.frame=retained_frame
    fixture.revise(manifest['intervalCurves']['shapeSeries']['additive'],'2031-11-01T04:00:00Z',['0']*721)
    unsupported_book=str(uuid4())
    query(f"INSERT INTO trading.books VALUES('{manifest['catalogId']}','{unsupported_book}','d0000000-0000-0000-0000-000000000001','Synthetic unavailable dependency',1,'2020-01-01T00:00:00Z');")
    unsupported,_=fixture.book(profile_terms(manifest),'2031-11-02T04:00:00Z','2031-11-03T05:00:00Z',book=unsupported_book)
    held_frame=next(f for f in frames if instant(f['at'])==instant(dates['at']) and 'test_existing_close_energy' in f['curveContracts'] and not f.get('ppaBundle'))
    import time
    remaining=(instant(manifest['closeRisk']['at'])-datetime.now(timezone.utc)).total_seconds()
    if remaining>0:
        print(f'Waiting {remaining:.1f}s for the explicitly selected close instant; no future knowledge or clock substitution.',flush=True)
        time.sleep(remaining)
    close=owner.call('market/closes/register',dict(requestId=str(uuid4()),snapshotId=live_owner['id'],name='Synthetic existing close'))
    held_close=owner.call('market/closes/register',dict(requestId=str(uuid4()),snapshotId=held_frame['id'],name='Synthetic retained holdings close'))
    now=datetime.now(timezone.utc)
    body={'schedule':{'requestId':str(uuid4()),'name':'Synthetic read-only existing-close risk','operation':'close_book_risk_v1',
        'dueAt':(now+timedelta(minutes=10)).isoformat(),'expiresAt':(now+timedelta(hours=1)).isoformat(),
        'intervalSeconds':None,'timeZone':'UTC','misfire':'run_once','overlap':'forbid','timeoutSeconds':120,'maximumAttempts':3,'profileId':None,'profileVersion':None},
        'definition':{'closeId':close['id'],'bookIds':[book],'knownAtPolicy':'valuation_instant_v1','pendingActuals':'require_actuals_v1',
            'coveragePolicy':'ppa_coverage_v1','revisionPolicy':'exact_bundle_whole_operation_v1','ppaImplementation':'ppa_capabilities_v1','valuationPolicy':policy}}
    # This is an observable operation check, not a schema-name/source assertion.
    created=owner.call('jobs/close-book-risk/create',body)
    assert created==owner.call('jobs/close-book-risk/create',body)
    print('PASS close risk: exact idempotent existing-close admission',flush=True)

    missing=deepcopy(body);missing['schedule']['requestId']=str(uuid4());del missing['definition']['pendingActuals']
    assert owner.call('jobs/close-book-risk/create',missing,expected=422)['code']=='pilot_close_inputs'
    changed=deepcopy(body);changed['definition']['pendingActuals']='estimate_until_actual_v1'
    assert owner.call('jobs/close-book-risk/create',changed,expected=409)['code']=='job_conflict'
    viewer=PreviewClient(url);viewer.bootstrap();viewer.call('profile',{'profile':'viewer'})
    viewer.call('jobs/close-book-risk/create',body,expected=403)
    print('PASS close risk: explicit policies, replay conflict and current submission authority',flush=True)
    before_jobs=query('SELECT count(*) FROM automation.jobs;')
    for selected,code in [(large_book,'risk_size'),(mixed_book,'pilot_close_inputs'),(unsupported_book,'pilot_close_inputs')]:
        refused=deepcopy(body);refused['schedule']['requestId']=str(uuid4());refused['definition']['bookIds']=[selected]
        started=time.monotonic()
        result=owner.call('jobs/close-book-risk/create',refused,expected=422)
        assert result['code']==code and 'totals' not in result,(selected,code,result)
        if selected==mixed_book:
            assert {x['tradeId'] for x in result['affectedBindings']}=={incompatible['tradeId']},result
        if selected==unsupported_book:
            assert {x['tradeId'] for x in result['affectedBindings']}=={unsupported['tradeId']},result
        assert query('SELECT count(*) FROM automation.jobs;')==before_jobs
        print(f'PASS close risk: {code} refusal in {time.monotonic()-started:.3f}s without admission',flush=True)
    print('PASS close risk: actual long-term work bound and incompatible complete book refuse before admission without partial totals',flush=True)

    def due(job):
        query(f"UPDATE automation.jobs SET next_due_at=clock_timestamp()-interval '1 second' WHERE id='{job['id']}';")
    due(created)
    for signature in ('',',true',",ARRAY['worker_check','swarm_job']::text[]"):
        assert query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120{signature}) IS NULL;")=='t'
    print('PASS close risk: legacy and undeclared workers cannot claim existing-close risk',flush=True)
    claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120,ARRAY['close_book_risk_v1']::text[])::text;"))
    assert claim['operation']=='close_book_risk_v1'
    from check_close_risk_authority import run_checks as check_close_risk_authority
    check_close_risk_authority(query,claim)
    old_token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{claim['id']}',{claim['fence']},'{claim['workerId']}');")
    query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{old_token}');DO $$BEGIN PERFORM automation.publish_pilot_close('[]','[]','[]');RAISE EXCEPTION 'Existing-close operation published market prices';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;")
    # Correction is after admission and after the selected valuation cutoff.
    late=fixture.revise(config['forecast'],'2031-11-02T04:00:00Z',['0.9']*25)
    pending_sql=f"SELECT count(*) FROM market_data.interval_series_revisions r LEFT JOIN market_data.interval_series_knowledge_receipts k USING(catalog_id,publication_order) WHERE r.series_id='{config['forecast']}' AND r.revision={late['revision']} AND k.publication_order IS NULL;"
    assert query(pending_sql)=='1','The later correction must start uncertified for the read-only retry probe'
    query(f"UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{claim['id']}';")
    scheduler=root/'close-risk.scheduler.connection';runtime=root/'close-risk.runtime.connection'
    scheduler.write_text(f'Host={root};Port=55439;Database=postgres;Username=pilot_fixture_scheduler;SSL Mode=Disable;Application Name=synthetic-close-risk-worker');scheduler.chmod(0o600)
    runtime.write_text(f'Host={root};Port=55439;Database=postgres;Username=pilot_fixture_runtime;SSL Mode=Disable;Application Name=synthetic-close-risk-worker');runtime.chmod(0o600)
    worker_env={k:v for k,v in app_environment.items() if not k.startswith(('OPENETRM_PREVIEW_','ASPNETCORE_'))}
    worker_env.update(OPENETRM_WORKER_CONNECTION_FILE=str(scheduler),OPENETRM_PILOT_RUNTIME_CONNECTION_FILE=str(runtime),
        OPENETRM_PILOT_MANIFEST_FILE=str(root/'manifest.json'),OPENETRM_WORKER_POLL_SECONDS='1',OPENETRM_WORKER_LEASE_SECONDS='120',ASPNETCORE_URLS='not-an-http-listener')
    repository=Path(__file__).resolve().parents[1];published=root/'close-risk-worker'
    built=subprocess.run([str(dotnet),'publish','src/OpenEtrm.Worker','--no-restore','-o',str(published)],cwd=repository,env=worker_env,capture_output=True,text=True,timeout=120)
    assert built.returncode==0,built.stdout+built.stderr
    command=[str(dotnet),str(published/'OpenEtrm.Worker.dll'),'--once']
    def market_counts():
        names=['market_data.observations','market_data.market_closes','market_data.market_close_pins','market_data.market_close_receipts','market_data.market_knowledge_receipts','market_data.temporal_contexts','market_data.interval_series_knowledge_receipts','power_data.receipts']
        return {name:query(f'SELECT count(*) FROM {name};') for name in names}
    def work(job,pending=None):
        before=market_counts()
        result=subprocess.run(command,cwd=repository,env=worker_env,capture_output=True,text=True,timeout=160)
        assert result.returncode==0,result.stdout+result.stderr
        state=json.loads(query(f"SELECT jsonb_build_object('id',id,'state',state,'error',error_code)::text FROM automation.runs WHERE job_id='{job['id']}';"))
        assert state['state']=='succeeded',state
        assert market_counts()==before,'Read-only risk wrote market facts or publication receipts'
        if pending is not None:
            assert query(pending)=='1','Existing-close retry certified an unrelated later correction'
        report=owner.call('jobs/pilot-close/results?runId='+state['id'])
        assert report['mode']=='existing_close' and report['reportId']==state['id']
        return report
    report=work(created,pending_sql)
    assert report['close']['id']==close['id'] and report['close']['id']!=report['reportId']
    assert fraction(report['totals'][0]['value'])==2000
    assert {p['tradeId'] for p in report['positions']}=={trade['tradeId']}
    assert report['context']['contextId']==created['admission']['contextId']
    assert report['ppaQuantityPins'] and report['ppaShapePins'] and report['ppaAssetPins']
    assert owner.call('jobs/close-book-risk/create',body)==created
    query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;DO $$BEGIN PERFORM security.use_context('{old_token}');PERFORM automation.lock_pilot_run();RAISE EXCEPTION 'Old worker fence revived';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;")
    print('PASS close risk: replacement retries preserve admitted inputs, exact 2000 oracle and receipts without market writes',flush=True)
    held_request=deepcopy(body);held_request['schedule'].update(requestId=str(uuid4()),name='Synthetic held-certificate risk')
    held_request['definition'].update(closeId=held_close['id'],bookIds=[held_book],pendingActuals='estimate_until_actual_v1')
    held_job=owner.call('jobs/close-book-risk/create',held_request);due(held_job)
    held_claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120,ARRAY['close_book_risk_v1']::text[])::text;"))
    assert held_claim['jobId']==held_job['id']
    fixture.revise(config['meter'],first,['60',None])
    later_actual=collector.publish('energy',day,day_start,24,60,['45']*24,2)
    query(f"UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{held_claim['id']}';")
    held=work(held_job)
    assert query(f"SELECT count(*) FROM automation.pilot_report_ppa_actuals WHERE report_id='{held['reportId']}' AND batch_id='{original_actual['batchId']}';")=='1'
    assert query(f"SELECT count(*) FROM automation.pilot_report_ppa_actuals WHERE report_id='{held['reportId']}' AND batch_id='{later_actual['batchId']}';")=='0'
    assert fraction(held['totals'][0]['value'])==200
    assert {p['tradeId'] for p in held['positions']}=={held_trade['tradeId']}
    assert len(held['risk'])==1 and fraction(held['risk'][0]['delta']['value'])==40
    assert fraction(held['risk'][0]['gamma']['value'])==0
    shock=Fraction(held['risk'][0]['factor']['bumpSize'])
    assert fraction(held['risk'][0]['upPnl'])==40*shock and fraction(held['risk'][0]['downPnl'])==-40*shock
    balances=held['ppaBalances'][0]
    assert fraction(balances['overdueKnownBalance'])==-860 and fraction(balances['overdueEstimated'])==-860
    assert held['ppaActualPins'] and held['ppaQuantityPins']
    viewer.call('jobs/pilot-close/results?runId='+held['runId'],expected=403)
    print('PASS close risk: held certificates retain mark sensitivity; known and estimated overdue balances remain separate',flush=True)

    from check_close_risk_discount import run_checks as check_discounts
    check_discounts(owner,fixture,query,manifest,body,due,work)

    cancelled_request=deepcopy(body);cancelled_request['schedule']['requestId']=str(uuid4())
    cancelled=owner.call('jobs/close-book-risk/create',cancelled_request);due(cancelled)
    cancelled_claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120,ARRAY['close_book_risk_v1']::text[])::text;"))
    cancelled_token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{cancelled_claim['id']}',{cancelled_claim['fence']},'{cancelled_claim['workerId']}');")
    owner.call('jobs/control',dict(requestId=str(uuid4()),id=cancelled['id'],action='cancel',expectedVersion=cancelled['version']))
    query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;DO $$BEGIN PERFORM security.use_context('{cancelled_token}');PERFORM automation.lock_pilot_run();RAISE EXCEPTION 'Cancelled worker authorized';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;")
    assert query(f"SELECT count(*) FROM automation.pilot_reports WHERE run_id='{cancelled_claim['id']}';")=='0'
    print('PASS close risk: cancellation fences retained worker authority and report publication',flush=True)

    choices=owner.call('jobs/pilot-close/catalog')
    original_frame=next(f for f in owner.call('workspace')['frames'] if instant(f['at']).isoformat()=='2020-12-18T16:00:00+00:00' and set(f['curveContracts'])=={'local_power','local_power_all_in'} and not f.get('ppaBundle'))
    original_close=owner.call('market/closes/register',dict(requestId=str(uuid4()),snapshotId=original_frame['id'],name='Synthetic legacy worker anchor'))
    root_curve=next(c for c in choices['curves'] if c['code']=='local_power')
    legacy=deepcopy(body);legacy['schedule'].update(requestId=str(uuid4()),operation='generated_pilot_close_v1')
    legacy['definition']={'priorCloseId':original_close['id'],'bookIds':[book],'curveCodes':['local_power'],'sourceCode':'fixture','seed':17,
        'leafMoves':[{'curve':code,'maxTicks':7} for code in root_curve['leafCurves']],
        **{key:values[0]['value'] for key,values in choices['policies'].items()},'valuationPolicy':policy}
    for signature in ('',',true',",ARRAY['generated_pilot_close_v1']::text[]"):
        legacy['schedule']['requestId']=str(uuid4())
        old_job=owner.call('jobs/pilot-close/create',legacy);due(old_job)
        assert query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120{signature}) IS NULL;")=='t'
        state=json.loads(query(f"SELECT jsonb_build_object('operation',operation,'state',state)::text FROM automation.jobs WHERE id='{old_job['id']}';"))
        # The readiness signature deliberately does not admit financial work.
        if signature:
            assert state=={'operation':'generated_pilot_close_v1','state':'blocked'},state
        else:
            assert state=={'operation':'generated_pilot_close_v1','state':'active'},state
            owner.call('jobs/control',dict(requestId=str(uuid4()),id=old_job['id'],action='cancel',expectedVersion=old_job['version']))
        assert query(f"SELECT count(*) FROM automation.runs WHERE job_id='{old_job['id']}';")=='0'
    print('PASS close risk: legacy and declared generated-v1 workers refuse PPA books without relabelling historical operation identities',flush=True)
