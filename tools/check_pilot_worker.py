"""Run the actual worker only against the caller's new owned synthetic cluster."""
from datetime import datetime,timedelta,timezone
from fractions import Fraction
from decimal import Decimal,localcontext
from hashlib import sha256
import json
from pathlib import Path
import subprocess
import time
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking


def run_checks(url,query,manifest,dotnet,owned_root,environment,psql):
    root=Path(owned_root).resolve();repository=Path(__file__).resolve().parents[1]
    if not(root/'.openetrm-preview').is_file() or not(root/'data/postmaster.pid').is_file():
        raise AssertionError('The worker qualifier requires the matching live owned preview')
    trader=PreviewClient(url);trader.bootstrap();trader.call('profile',{'profile':'trader'})
    frame=next(f for f in trader.call('workspace')['frames'] if f['at'].startswith('2020-12-18T16:00:00'))
    swap_context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    catalog=trader.call('swaps/catalog',swap_context);books=[b['id'] for b in catalog['books'][:2]]
    def payment(days):return {'frequency':'monthly','rule':'days_after_period_end','days':days,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    for curve,strike,book,quantities,discounting in [('base_forward','3',books[0],['100','250'],True),('local_power','40',books[1],['3'],False)]:
        index=next(i for i in catalog['indices'] if i['curve']==curve and i['definitionRevisionId']==manifest['revisionId'])
        delivery=next(c for c in index['contracts'] if c['deliveryFrom']=='2021-01-01')
        for quantity in quantities:
            trader.call('swaps/book',{**swap_context,**trade_booking(),'operationId':str(uuid4()),'bookId':book,
                'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'quantity':quantity,'fixedCoefficient':'-1','discounting':discounting,
                'fixed':{'price':strike,'currency':index['currencyCode'],**delivery,'paymentSchedule':payment(18)},
                'floating':{'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve':curve,'index':index['index'],'currency':index['currencyCode'],**delivery,'paymentSchedule':payment(46),'resetFrequency':'monthly','averagingMethod':'none'}})
    index=next(i for i in catalog['indices'] if i['curve']=='base_forward' and i['definitionRevisionId']==manifest['revisionId'])
    floating={'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':payment(18),'resetFrequency':'monthly','averagingMethod':'none','spread':'0.1'}
    pair=trader.call('swaps/book',{**swap_context,**trade_booking(),'operationId':str(uuid4()),'bookId':books[0],
        'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'quantity':'10','firstCoefficient':'-1','swapType':'financial_floating_floating',
        'firstFloating':floating,'floating':{**floating,'spread':'0.2'},'discounting':False})
    future_context={**swap_context,'convention':'contract_lot_execution_pnl_v2'}
    future=next(c for c in trader.call('futures/catalog',future_context)['contracts'] if c['curve']=='future' and c['deliveryFrom']=='2021-01-01')
    future_trade=trader.call('futures/book',{**future_context,**trade_booking(),'operationId':str(uuid4()),'bookId':books[0],
        'curve':'future','contractId':future['id'],'lots':'2','executionPrice':'2.9'})
    from pilot_report_fixture import prepare_alias
    prepare_alias(query,manifest,future_trade['tradeId'])
    owner=PreviewClient(url);owner.bootstrap();owner.call('profile',{'profile':'approver'})
    selected=next(f for f in owner.call('workspace')['frames'] if f['at']==frame['at'])
    anchor=owner.call('market/closes/register',{'requestId':str(uuid4()),'snapshotId':selected['id'],'name':'Synthetic worker anchor'})
    choices=owner.call('jobs/pilot-close/catalog')
    root_curve=next(c for c in choices['curves'] if c['code']=='local_power')
    source=next(s['code'] for s in choices['sources'] if s['code']=='fixture')
    policies={key:values[0]['value'] for key,values in choices['policies'].items()}
    policy=choices['valuationPolicyChoices'][0]['policy']
    now=datetime.now(timezone.utc)
    body={'schedule':{'requestId':str(uuid4()),'name':'Synthetic mixed-book pilot close','operation':choices['operation'],
        'dueAt':(now+timedelta(minutes=10)).isoformat(),'expiresAt':(now+timedelta(hours=1)).isoformat(),'intervalSeconds':None,
        **choices['schedulePolicy'],'timeoutSeconds':120,'maximumAttempts':3,'profileId':None,'profileVersion':None},
        'definition':{'priorCloseId':anchor['id'],'bookIds':books,'curveCodes':['local_power'],'sourceCode':source,'seed':17,
            'leafMoves':[{'curve':code,'maxTicks':7} for code in root_curve['leafCurves']],**policies,'valuationPolicy':policy}}
    owner.call('jobs/pilot-close/create',{**body,'definition':{k:v for k,v in body['definition'].items() if k!='seed'}},expected=400)
    created=owner.call('jobs/pilot-close/create',body)
    assert created==owner.call('jobs/pilot-close/create',body) and created['requestId']==body['schedule']['requestId']
    owner.call('jobs/pilot-close/create',{**body,'definition':{**body['definition'],'seed':18}},expected=409)
    from check_pilot_definition_expiry import run_checks as check_definition_expiry
    check_definition_expiry(query,manifest,psql,environment,created['id'])
    # Model a retained, late scheduled occurrence at the fixture's financial
    # date. Only an operational due field is adjusted by the fixture owner;
    # no sealed definitions, native prices, trade rows or server clock change.
    due='2020-12-19T16:00:00+00:00'
    query(f"UPDATE automation.jobs SET next_due_at='{due}' WHERE id='{created['id']}';")
    assert query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120) IS NULL;")=='t', 'Readiness-only claim stole a financial occurrence'
    claim=json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.claim_next('{uuid4()}',120,true)::text;"))
    query(f"""SET SESSION AUTHORIZATION pilot_fixture_scheduler;
    DO $$BEGIN PERFORM automation.finish_run('{claim['id']}',{claim['fence']},'{claim['workerId']}',true,NULL,NULL);
    RAISE EXCEPTION 'A financial occurrence completed without a report';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;""")
    assert query(f"SELECT state FROM automation.runs WHERE id='{claim['id']}';")=='running'
    old_token=query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler;SELECT automation.issue_pilot_context('{claim['id']}',{claim['fence']},'{claim['workerId']}');")
    from check_pilot_definition_expiry import run_context_checks as check_run_definition_expiry
    check_run_definition_expiry(query,manifest,psql,environment,claim,old_token)
    actor=query(f"SELECT q.actor_id=w.principal_id AND q.actor_id<>j.owner_id FROM security.request_contexts q JOIN automation.pilot_run_contexts p ON p.context_id=q.id JOIN automation.runs r ON r.id=p.run_id JOIN automation.jobs j ON j.id=r.job_id JOIN automation.workers w ON w.login=p.worker_login WHERE q.id='{old_token}';")
    assert actor=='t','The run context impersonated the human job owner'
    query(f"""SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{old_token}');
    DO $$BEGIN INSERT INTO market_data.observations
     SELECT(jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object('id',gen_random_uuid(),
      'source_event_id','synthetic-pilot-bypass-probe','source_revision_id','probe','received_at',clock_timestamp(),'corrects_id',NULL))).*
     FROM market_data.observations o WHERE catalog_id='{manifest['catalogId']}' ORDER BY observed_at,id LIMIT 1;
     RAISE EXCEPTION 'Pilot run context bypassed its fenced publication operation';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;""")
    payload=json.loads(query(f"""SELECT jsonb_build_object(
     'marks',(SELECT jsonb_agg(jsonb_build_object('seriesId',o.series_id,'anchorObservationId',o.id,'moveTicks',0,'value',o.value::text,'increment',q.clearing_increment::text))
      FROM market_data.market_close_pins a JOIN market_data.observations o ON(o.catalog_id,o.id)=(a.catalog_id,a.observation_id)
      JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(a.catalog_id,a.definition_revision_id,a.series_id)
      JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id)
      JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
      WHERE a.close_id='{anchor['id']}' AND a.input_use='variable' AND o.role IN('trader_mark','daily_settlement') AND k.expiry_at>'{due}'
      AND EXISTS(SELECT 1 FROM automation.jobs j JOIN automation.pilot_close_moves m ON(m.profile_id,m.profile_version)=(j.pilot_profile_id,j.pilot_profile_version)
       JOIN market_data.curves cv ON(cv.catalog_id,cv.revision_id,cv.code)=(m.catalog_id,m.definition_revision_id,m.curve_code)
       JOIN market_data.curve_members cm ON(cm.catalog_id,cm.revision_id,cm.curve_code)=(cv.catalog_id,cv.revision_id,cv.code)
       WHERE j.id='{created['id']}' AND cm.contract_id=s.contract_id AND cv.quote_code=s.quote_code)),
     'carried',coalesce((SELECT jsonb_agg(jsonb_build_object('seriesId',a.series_id,'observationId',a.observation_id,'inputUse',a.input_use))
       FROM market_data.market_close_pins a JOIN market_data.observations o ON(o.catalog_id,o.id)=(a.catalog_id,a.observation_id)
       WHERE a.close_id='{anchor['id']}' AND(o.role='parameter' OR a.input_use='frozen')),'[]'::jsonb),
     'members',(SELECT jsonb_agg(jsonb_build_object('curve',curve_code,'contractId',contract_id,'ordinal',ordinal)) FROM(
      SELECT a.curve_code,a.contract_id,(row_number() OVER(PARTITION BY a.curve_code ORDER BY a.ordinal)-1)::integer ordinal
      FROM market_data.market_close_members a JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(a.catalog_id,a.definition_revision_id,a.contract_id)
      WHERE a.close_id='{anchor['id']}' AND a.curve_code='local_power' AND k.expiry_at>'{due}')x));"""))
    def publish_probe(candidate,expected_error):
        parts={**candidate,"marks":[{key:value for key,value in mark.items() if key!='increment'} for mark in candidate["marks"]]}
        args=','.join("'"+json.dumps(parts[key]).replace("'","''")+"'::jsonb" for key in ['marks','carried','members'])
        statement=f"PERFORM automation.publish_pilot_close({args});"
        if expected_error:
            statement=f"BEGIN {statement} RAISE EXCEPTION 'Tampered pilot payload was accepted';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'{expected_error}' THEN RAISE;END IF;END;"
        query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{old_token}');SELECT security.use_market_context('{manifest['catalogId']}',security.capture_date_context('{manifest['catalogId']}','2020-12-19',NULL));SELECT security.use_trading_context('{manifest['catalogId']}','{due}');DO $$BEGIN {statement} END$$;ROLLBACK;")
    publish_probe(payload,None)
    wrong_member=json.loads(json.dumps(payload));wrong_member['members'][0]['ordinal']+=1
    publish_probe(wrong_member,'pilot_close_inputs')
    wrong_root=json.loads(json.dumps(payload));wrong_root['members'].append({**wrong_root['members'][0],'curve':'base_forward'})
    publish_probe(wrong_root,'pilot_close_inputs')
    wrong_move=json.loads(json.dumps(payload));wrong_move['marks'][0]['moveTicks']=8
    value=Fraction(wrong_move['marks'][0]['value'])+8*Fraction(wrong_move['marks'][0]['increment'])
    with localcontext() as precision:
        precision.prec=80
        wrong_move['marks'][0]['value']=str(Decimal(value.numerator)/Decimal(value.denominator))
    publish_probe(wrong_move,'pilot_close_inputs')
    assert query(f"SELECT count(*) FROM market_data.market_closes WHERE run_id='{claim['id']}';")=='0'
    def run_can_read():
        return query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{old_token}');SELECT security.has_permission('ReadMarketData');ROLLBACK;").splitlines()[-1]
    assert run_can_read()=='t'
    for principal in [created['ownerId'] if 'ownerId' in created else query(f"SELECT owner_id FROM automation.jobs WHERE id='{created['id']}';"),query("SELECT principal_id FROM automation.workers WHERE login='pilot_fixture_scheduler';")]:
        grant=query(f"SELECT g.id FROM security.role_grants g JOIN security.roles r ON r.id=g.role_id WHERE g.principal_id='{principal}' AND r.code='synthetic_pilot_operator';")
        try:
            query(f"UPDATE security.role_grants SET revoked=true WHERE id='{grant}';")
            assert run_can_read()=='f','A run retained authority after its owner/service grant was revoked'
        finally:query(f"UPDATE security.role_grants SET revoked=false WHERE id='{grant}';")
        assert run_can_read()=='t','Restored current authority was not checked afresh'
    for table,column,value in [('market_catalog_scopes','catalog_id',manifest['catalogId']),('reference_catalog_scopes','catalog_id',manifest['referenceCatalogId'])]:
        predicate=f"{column}='{value}' AND tenant_id='{manifest['tenantId']}' AND workspace_id='{manifest['workspaceId']}'"
        retained_scope=query(f"SELECT row_to_json(s)::text FROM security.{table} s WHERE {predicate};")
        try:
            query(f"DELETE FROM security.{table} WHERE {predicate};")
            assert run_can_read()=='f','A run retained authority after its exact catalog scope was removed'
        finally:
            escaped=retained_scope.replace("'","''")
            query(f"INSERT INTO security.{table} SELECT(jsonb_populate_record(NULL::security.{table},'{escaped}'::jsonb)).*;")
        assert run_can_read()=='t'
    from check_pilot_claim_expiry import run_checks as check_claim_expiry
    check_claim_expiry(query,manifest,psql,environment,claim)
    query(f"UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{claim['id']}';")
    denied=query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime;BEGIN;SELECT security.use_context('{old_token}');SELECT NOT security.has_permission('ReadMarketData');ROLLBACK;")
    assert denied.splitlines()[-1]=='t','The run context survived expiry of its actual lease'
    scheduler=root/'pilot.scheduler.connection';runtime=root/'pilot.runtime.connection'
    scheduler.write_text(f'Host={root};Port=55439;Database=postgres;Username=pilot_fixture_scheduler;SSL Mode=Disable;Application Name=synthetic-pilot-worker');scheduler.chmod(0o600)
    runtime.write_text(f'Host={root};Port=55439;Database=postgres;Username=pilot_fixture_runtime;SSL Mode=Disable;Application Name=synthetic-pilot-worker');runtime.chmod(0o600)
    worker_environment={k:v for k,v in environment.items() if not k.startswith(('OPENETRM_PREVIEW_','ASPNETCORE_'))}
    worker_environment.update(OPENETRM_WORKER_CONNECTION_FILE=str(scheduler),OPENETRM_PILOT_RUNTIME_CONNECTION_FILE=str(runtime),
        OPENETRM_PILOT_MANIFEST_FILE=str(root/'manifest.json'),OPENETRM_WORKER_POLL_SECONDS='1',OPENETRM_WORKER_LEASE_SECONDS='120',
        ASPNETCORE_URLS='not-an-http-listener')
    # Build/publish qualification stays in this tree; output is private to the
    # owned cluster and removed by its existing owner-fenced cleanup.
    published=root/'pilot-worker'
    subprocess.run([str(dotnet),'publish','src/OpenEtrm.Worker','--no-restore','-o',str(published)],cwd=repository,env=worker_environment,check=True,timeout=90,capture_output=True,text=True)
    from check_pilot_worker_configuration import run_checks as check_worker_configuration
    check_worker_configuration(query,manifest,claim,[str(dotnet),str(published/'OpenEtrm.Worker.dll'),'--once'],worker_environment,root,repository)
    def once(extra=None):
        result=subprocess.run([str(dotnet),str(published/'OpenEtrm.Worker.dll'),'--once'],cwd=repository,env={**worker_environment,**(extra or {})},capture_output=True,text=True,timeout=90)
        assert result.returncode==0,result.stderr+'\n'+result.stdout
        assert 'Now listening on' not in result.stdout+result.stderr,'The standalone worker started an HTTP host'
    # Kill an actual worker while its second transaction waits at report
    # publication. The first marks transaction is already committed. This
    # exercises the truthful partial state and a replacement worker's reuse.
    locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    process=None
    try:
        locker.stdin.write('BEGIN;LOCK TABLE automation.pilot_report_totals IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
        while locker.stdout.readline().strip()!='LOCKED':
            if locker.poll() is not None:raise AssertionError(locker.stderr.read())
        process=subprocess.Popen([str(dotnet),str(published/'OpenEtrm.Worker.dll'),'--once'],cwd=repository,env=worker_environment,
                                 stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,start_new_session=True)
        blocked=False;deadline=time.monotonic()+45
        while time.monotonic()<deadline:
            if query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='synthetic-pilot-worker' AND NOT l.granted AND l.relation='automation.pilot_report_totals'::regclass;")=='1':blocked=True;break
            if process.poll() is not None:raise AssertionError('Worker exited before report publication: '+''.join(process.communicate())+' '+query(f"SELECT row_to_json(r)::text FROM(SELECT state,error_code FROM automation.runs WHERE id='{claim['id']}')r;"))
            time.sleep(.03)
        if not blocked:
            activity=query("SELECT coalesce(jsonb_agg(jsonb_build_object('state',state,'waitType',wait_event_type,'wait',wait_event,'query',left(query,300))),'[]'::jsonb)::text FROM pg_stat_activity WHERE application_name='synthetic-pilot-worker';")
            raise AssertionError('The actual worker did not reach the controlled report publication boundary: '+activity)
        assert query(f"SELECT count(*) FROM market_data.market_closes WHERE run_id='{claim['id']}';")=='1'
        assert query(f"SELECT count(*) FROM automation.pilot_reports WHERE run_id='{claim['id']}';")=='0'
        from owned_process import stop_owned_group
        stop_owned_group(process)
    finally:
        if process is not None and process.poll() is None:
            from owned_process import stop_owned_group
            stop_owned_group(process)
        locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
        assert locker.returncode==0,errors
    for _ in range(100):
        if query("SELECT count(*) FROM pg_stat_activity WHERE application_name='synthetic-pilot-worker';")=='0':break
        time.sleep(.03)
    else:raise AssertionError('The owned worker backend did not drain after termination')
    assert query(f"SELECT count(*) FROM automation.pilot_reports WHERE run_id='{claim['id']}';")=='0'
    assert query(f"SELECT state FROM automation.runs WHERE id='{claim['id']}';")=='running'
    retained=query(f"SELECT request::text FROM market_data.market_closes WHERE run_id='{claim['id']}';")
    query(f"UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{claim['id']}';")
    once()
    assert query(f"SELECT request::text FROM market_data.market_closes WHERE run_id='{claim['id']}';")==retained,'A replacement worker changed the published close'
    rows=json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('id',r.id,'state',r.state,'error',r.error_code,'jobState',j.state)) FROM automation.runs r JOIN automation.jobs j ON j.id=r.job_id WHERE j.id='{created['id']}';"))
    assert len(rows)==1 and rows[0]['state']=='succeeded' and rows[0]['jobState']=='completed',rows
    run=rows[0]['id'];catalog_id=manifest['catalogId']
    assert query(f"SELECT name FROM market_data.market_closes WHERE run_id='{run}';")=='Generated pilot close','A close label embedded a UTC clock instead of leaving the instant to user-zone formatting'
    marks=json.loads(query(f"""SELECT jsonb_agg(jsonb_build_object('series',n.series_id,'anchor',a.id,'before',a.value::text,'after',n.value::text,'increment',q.clearing_increment::text))
      FROM market_data.market_closes c JOIN market_data.market_close_pins np ON(np.catalog_id,np.close_id)=(c.catalog_id,c.id)
      JOIN market_data.observations n ON(n.catalog_id,n.id)=(np.catalog_id,np.observation_id)
      JOIN market_data.market_close_pins ap ON(ap.catalog_id,ap.close_id,ap.series_id,ap.input_use)=(c.catalog_id,c.anchor_close_id,np.series_id,np.input_use)
      JOIN market_data.observations a ON(a.catalog_id,a.id)=(ap.catalog_id,ap.observation_id)
      JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(n.catalog_id,n.definition_revision_id,n.series_id)
      JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
      WHERE c.id='{run}' AND n.id<>a.id;"""))
    assert len(marks)==30,marks
    for mark in marks:
        encoded='\n'.join(['seeded_clearing_tick_move_v1','17','2020-12-19T16:00:00.0000000+00:00',catalog_id,manifest['revisionId'],mark['series'],mark['anchor']])
        move=int.from_bytes(sha256(encoded.encode()).digest()[:8],'big')%15-7
        assert Fraction(mark['after'])==Fraction(mark['before'])+move*Fraction(mark['increment']),mark
    before=query(f"SELECT count(*) FROM market_data.observations WHERE source_event_id LIKE 'pilot:{run}:%';")
    once()
    assert query(f"SELECT count(*) FROM market_data.observations WHERE source_event_id LIKE 'pilot:{run}:%';")==before
    report=owner.call('jobs/pilot-close/results?runId='+run)
    assert report['state']=='report_published' and report['runId']==run
    assert {t['measure'] for t in report['totals']}=={'futures_execution_pnl','swap_present_value'} and len(report['positions'])==6
    pair_position=next(p for p in report['positions'] if p['tradeId']==pair['tradeId'])
    pair_discounts=[d for d in report['discountInputs'] if d['instrumentId']==pair_position['instrumentId']]
    assert len(pair_discounts)==2 and all(not d['input']['enabled'] and d['input']['factor']=='1' for d in pair_discounts)
    assert query(f"SELECT count(*) FROM automation.pilot_report_discounts WHERE report_id='{run}' AND first_floating_side_id IS NOT NULL;")=='1'
    assert len(report['risk'])>0 and all(row['gamma']['value']['numerator']=='0' for row in report['risk'])
    from check_market_close_frames import check_generated_history
    check_generated_history(url,query,manifest,run,'future',future['id'])
    from check_pilot_report_resume import run_checks as check_report_resume
    check_report_resume(owner,body,query,psql,environment,[str(dotnet),str(published/'OpenEtrm.Worker.dll'),'--once'],worker_environment,repository)
    from check_pilot_alias_bounds import run_checks as check_alias_bounds
    check_alias_bounds(owner,body,query,manifest)
    print('PASS actual pilot worker: whole mixed books, 30 generated native marks checked by independent Fraction/SHA oracle, exact rational report, no HTTP host and idempotent completed occurrence',flush=True)
    return run
