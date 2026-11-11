"""Native physical operations through an explicitly delegated assistant, owned synthetic fixture only."""
from copy import deepcopy
from datetime import date,timedelta
import json
import time
from uuid import uuid4
from assistant_context import Contexts
from assistant_fixture import Fixture
from fake_model_provider import recording, stream, tool_use
from preview_client import PreviewClient, initial_frame


def run_checks(url, query, manifest, fake, demo, dotnet, environment, owned_root):
    def client(profile):
        c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':profile});return c
    admin=client('admin');writer=client('trader');stamp=uuid4().hex[:8]
    login='synthetic_physical_assistant_'+stamp
    owner=client('admin')
    member=owner.call('admin/create',{'operationId':str(uuid4()),'login':login,'name':'Synthetic physical operator','kind':'human',
                              'roles':['trader','market_data_editor','market_data_approver']})
    owner.call('profile',{'profile':login})
    contexts=Contexts(query,manifest)
    fixture=Fixture(admin,query,manifest,contexts.administrator,stamp,'physical')
    fixture.qualify();identifier='synthetic-physical-'+stamp
    model=fixture.model(identifier)
    agent=fixture.publish(fixture.save(fixture.create('physical operations'),model,
        tools=['read_workspace_page','discover_page_operations','read_page_operation','propose_page_action','navigate','present_walkthrough','read_trades'],roles=['trader']))
    owner.call('assistant/grants/grant',{'operationId':str(uuid4()),'agentId':agent['id'],'version':1})
    frame=initial_frame(owner.call('workspace/navigation'),'2020-12-18')['id']
    facility_report={'page':'facility_quantities','businessDate':demo['operationsOn'],'selection':{'ledgerId':demo['ledgerId']}}
    current={'mode':'current','contextId':None,'frameId':None}
    passed=0
    def check(name,condition):
        nonlocal passed
        assert condition,name
        passed+=1;print('PASS '+name,flush=True)
    def operation(code,body=None,parameters=None):
        return {'operation':code,'inputJson':json.dumps({'body':body,'parameters':parameters or {}})}
    def last_results(handler):
        return [json.loads(p['content']) for p in json.loads(handler.body)['messages'][-1]['content'] if p['type']=='tool_result']
    def ask(steps,report=facility_report,knowledge=current,before_read=None,refused=None):
        start=len(fake.received)
        for n,step in enumerate(steps):
            def reply(handler,n=n,step=step):
                if n==0 and before_read:before_read()
                tool,data=step(last_results(handler)) if callable(step) else step
                stream(tool_use(identifier,[(str(n),tool,data)]))(handler)
            fake.respond(reply)
        fake.respond(stream(recording(identifier,text='Synthetic reviewed physical result.')))
        body={'operationId':str(uuid4()),'conversationId':None,'agentId':agent['id'],'version':1,
              'question':'Inspect the selected physical operation using its explicit context. Prepare changes for review only.',
              'pageReport':report,'knowledge':knowledge}
        turn=owner.call('assistant/turns/ask',body)['turn']
        deadline=time.monotonic()+30
        while True:
            turn=owner.call(f"assistant/conversations/{turn['conversationId']}?after=0")['turns'][0]
            if turn['state'] in ('completed','failed','cancelled','interrupted'):break
            assert time.monotonic()<deadline,turn
            time.sleep(.05)
        if refused:
            assert turn['state']=='failed' and turn['outcome']==refused,turn
            assert len(fake.script)==1,'Only the unused final synthetic response may remain.'
            fake.script.pop()
        else:assert turn['state']=='completed',turn
        calls=[json.loads(r['body']) for r in fake.received[start:]]
        results=[json.loads(p['content']) for call in calls[1:] for p in call['messages'][-1]['content'] if p['type']=='tool_result']
        return turn,results,body
    def facility_source(amount,previous):
        directory=writer.call('facilities/quantities/directory',{'context':demo['context']})
        return writer.call('facilities/quantities/record',{'context':demo['context'],'operationId':str(uuid4()),
            'ownerId':directory['ownerId'],'effectiveAt':'2021-01-15T09:00:00Z','command':{'kind':'source',
            'ledgerId':demo['ledgerId'],'expectedPreviousId':previous,'quantity':amount,'evidence':'Synthetic late pipeline statement'}})
    correction=[]
    turn,results,original=ask([('read_workspace_page',{'page':'facility_quantities'}),('navigate',{'page':'gas_setup','target':None})],
                             before_read=lambda:correction.append(facility_source('90',demo['sourceId'])))
    view=results[0]['directory']['data']
    check('an admitted facility question keeps the prior quantity when a late correction arrives before its read',view['facts']['source']['quantity']=='100')
    check('facility questions use the explicit operating date without a pricing snapshot',
          turn['context']['frameId'] is None and view['context']['effectiveOn']==demo['operationsOn'])
    check('dated facility questions cannot issue navigation that drops their operating date','target' not in results[0] and results[1]['code']=='page_selection_unavailable')
    check('database navigation publication also refuses a dated target',query(f"SELECT assistant.is_navigation_target(t,jsonb_build_object('page','gas_setup','selection',NULL,'kind','page','label','Gas delivery setup','context',jsonb_build_object('mode',t.knowledge_mode,'contextId',t.context_id,'frameId',t.frame_id))) FROM assistant.turns t WHERE t.id='{turn['id']}';")=='f')
    changed=deepcopy(original);changed['pageReport']['businessDate']='2021-01-16'
    check('retrying a question with a different operating date refuses instead of reusing its answer',owner.call('assistant/turns/ask',changed,expected=409)['code']=='turn_conflict')

    prepared=[]
    def propose(rows):
        data=rows[0]['directory']['data'];source=data['facts']['source']
        request={'context':demo['context'],'operationId':str(uuid4()),'ownerId':data['ownerId'],'effectiveAt':'2021-01-15T10:00:00Z',
                 'command':{'kind':'source','ledgerId':demo['ledgerId'],'expectedPreviousId':source['id'],'quantity':'95','evidence':'Synthetic reviewed correction'}}
        prepared.append(request)
        return 'propose_page_action',operation('facilities.quantities.record_v1',request)
    proposal,results,_=ask([('read_workspace_page',{'page':'facility_quantities'}),propose])
    check('the next question sees the corrected quantity',results[0]['directory']['data']['facts']['source']['quantity']=='90')
    reference={'turnId':proposal['id'],'step':2}
    review=owner.call('assistant/actions/review',reference)
    check('proposal and review do not change the facility quantity',results[1]['status']=='awaiting_user_approval' and
          writer.call('facilities/quantities/read',{'context':demo['context'],'ledgerId':demo['ledgerId']})['facts']['source']['quantity']=='90')
    accepted=owner.call('assistant/actions/authorize',reference)
    check('human approval records the native correction exactly once',owner.call('assistant/actions/authorize',reference)==accepted and
          writer.call('facilities/quantities/read',{'context':demo['context'],'ledgerId':demo['ledgerId']})['facts']['source']['quantity']=='95')
    stale_request=deepcopy(prepared[0]);stale_request['command']['quantity']='96'
    stale,_,_=ask([('propose_page_action',operation('facilities.quantities.record_v1',stale_request))])
    check('a stale expected source head refuses through native approval',owner.call('assistant/actions/authorize',{'turnId':stale['id'],'step':1},expected=409)['code']=='facility_quantity_conflict')

    def preview_allocations(rows):
        data=rows[0]['directory']['data'];facts=data['facts']
        return 'read_page_operation',operation('facilities.quantities.preview_v1',{'ownerId':data['ownerId'],
            'context':{**demo['context'],'knowledgeMode':'retained','retained':data['context']},'ledgerId':demo['ledgerId'],
            'sourceRevisionId':facts['source']['id'],'expectedPreviousId':facts['plan']['id'],
            'allocations':[{'obligationId':a['obligationId'],'tradeRevision':a['tradeRevision'],'quantity':q}
                           for a,q in zip(facts['allocations'],['50','25','10'])]})
    _,results,_=ask([('read_workspace_page',{'page':'facility_quantities'}),preview_allocations])
    check('allocation preview uses the pinned source and the native conserved-volume calculation',
          results[1]['data']['balance']['allocated']=='85' and results[1]['data']['balance']['available']=='10')

    def wrong_date(rows):
        certificate=rows[0]['directory']['data']['context']
        return 'read_page_operation',operation('facilities.quantities.read_v1',{'ledgerId':demo['ledgerId'],'context':{
            'effectiveOn':'2021-01-16','knowledgeMode':'retained','retained':certificate,'convention':demo['context']['convention']}})
    _,results,_=ask([('read_workspace_page',{'page':'facility_quantities'}),wrong_date])
    check('the model cannot redirect a facility read to another business date',results[1]['code']=='operation_context_mismatch')
    owner.call('market/context/retained',{'contextId':turn['context']['contextId'],'selection':'latest_correction'})
    try:
        _,results,_=ask([('read_workspace_page',{'page':'facility_quantities'}),preview_allocations,('propose_page_action',operation('facilities.quantities.record_v1',prepared[0]))],
                        knowledge={'mode':'retained','contextId':turn['context']['contextId'],'frameId':None})
        check('historical facility questions preserve the earlier source and refuse proposed writes',results[0]['directory']['data']['facts']['source']['quantity']=='100' and results[2]['code']=='power_historical')
        check('a historical question cannot preview an allocation under the working-read exception',results[1]['code']=='historical_read_only')
        owner.call('assistant/actions/authorize',reference,expected=422)
    finally:owner.call('market/current',{})
    frame=initial_frame(owner.call('workspace/navigation'),'2020-12-18')['id']

    # Authoring history is available to the native editor; it is not financial context for an agent read.
    native=owner.call('gas-setup/catalog',None)
    private=owner.call('gas-setup/save',{'operationId':str(uuid4()),'baseSetupId':native['activeSetupId'],'configuration':native['configuration']})
    for page in ['gas_setup','crude_setup']:
        _,results,_=ask([('read_workspace_page',{'page':page}),('navigate',{'page':page,'target':None})],report={'page':page,'selection':{}},knowledge={**current,'frameId':frame})
        catalog=results[0]['directory']['data']
        check(page+' returns native field definitions and only contextual published terms',bool(catalog['fieldDescriptions']) and catalog['versions']==[] and catalog['proposals']==[] and catalog['proposal'] is None)
        check(page+' admits native fixed-page navigation',results[1]['target']['page']==page)
        if page=='gas_setup':check('unpublished gas proposals remain private authoring state',private['setupId'] not in json.dumps(catalog))
    crude=owner.call('crude-setup/catalog')
    delivery=next(p for p in crude['deliveryPoints'] if p['grade_code']=='synthetic_grade_a')
    source=next(s for s in crude['sources'] if s['curve']=='synthetic_crude_forward')
    config={'code':'synthetic_agent_binding','deliveryReferenceCatalogId':delivery['catalog_id'],'deliveryReferenceRevisionId':delivery['revision_id'],
        'commodity':delivery['commodity_code'],'grade':delivery['grade_code'],'facility':delivery['facility_code'],'custodyPoint':delivery['custody_point_code'],
        'clock':'test_utc','definitionRevisionId':source['definitionRevisionId'],'curve':source['curve'],'validFrom':'2021-01-01','validTo':'2021-02-01',
        'quantityPolicy':'same_standard_volume_at_60f_v1'}
    command={'operationId':str(uuid4()),'baseSetupId':None,'configuration':config}
    setup_turn,results,_=ask([('propose_page_action',operation('crude-setup.save_v1',command))],
        report={'page':'crude_setup','selection':{}},knowledge={**current,'frameId':frame})
    check('a proposed crude binding does not create a draft',results[0]['status']=='awaiting_user_approval' and
          not any(v['configuration']['code']==config['code'] for v in owner.call('crude-setup/catalog')['proposals']))
    reference={'turnId':setup_turn['id'],'step':1}
    saved=owner.call('assistant/actions/authorize',reference)
    check('approved delivery authoring reuses native exact-reference persistence and retry receipts',owner.call('assistant/actions/authorize',reference)==saved)
    publish_turn,_,_=ask([('propose_page_action',operation('crude-setup.publish_v1',{'operationId':str(uuid4()),'setupId':saved['setupId']}))],
        report={'page':'crude_setup','selection':{}},knowledge={**current,'frameId':frame})
    owner.call('assistant/actions/authorize',{'turnId':publish_turn['id'],'step':1})
    def crude_preview(rows):
        certificate=rows[0]['directory']['data']['context']
        return 'read_page_operation',operation('crude-setup.preview_v1',{'snapshotId':frame,'setupId':saved['setupId'],
            'knowledgeMode':'retained','contextId':certificate['contextId'],'deliveryFrom':'2021-01-01T00:00:00Z','deliveryTo':'2021-02-01T00:00:00Z','quantity':'10'})
    _,results,_=ask([('read_workspace_page',{'page':'crude_setup'}),crude_preview],report={'page':'crude_setup','selection':{}},knowledge={**current,'frameId':frame})
    native_value=owner.call('crude-setup/preview',{'snapshotId':frame,'setupId':saved['setupId'],'knowledgeMode':'context',
        'deliveryFrom':'2021-01-01T00:00:00Z','deliveryTo':'2021-02-01T00:00:00Z','quantity':'10'})
    check('crude delivery preview uses the native exact-context valuation',results[1]['data']['presentValue']==native_value['presentValue'])
    check('current crude preview refuses a conflicting retained-context identity',owner.call('crude-setup/preview',{'snapshotId':frame,'setupId':saved['setupId'],'knowledgeMode':'current','contextId':str(uuid4()),'deliveryFrom':'2021-01-01T00:00:00Z','deliveryTo':'2021-02-01T00:00:00Z','quantity':'10'},expected=422)['code']=='crude_setup_input')

    # Native validators admit a publisher even without the separate draft-writing capability.
    updated=admin.call('admin/update',{'operationId':str(uuid4()),'id':member['id'],'expectedVersion':member['version'],
        'name':'Synthetic physical operator','enabled':True,'roles':['trader','market_data_approver']})
    try:
        for page,body in [('gas_setup',{'setupId':private['setupId'],'from':'2021-01-01','to':'2021-02-01'}),('crude_setup',{'setupId':saved['setupId']})]:
            code=page.replace('_','-')+'.validate_v1'
            _,results,_=ask([('read_page_operation',operation(code,body))],report={'page':page,'selection':{}},knowledge={**current,'frameId':frame})
            check(page+' keeps native approver-only validation authority',results[0]['data']['valid'])
    finally:
        admin.call('admin/update',{'operationId':str(uuid4()),'id':member['id'],'expectedVersion':updated['version'],
            'name':'Synthetic physical operator','enabled':True,'roles':['trader','market_data_editor','market_data_approver']})

    # A framed financial question discovers and runs the exact native daily read.
    after=next(f['id'] for f in owner.call('workspace/navigation')['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    def daily(rows):
        certificate=rows[0]['context']
        return 'read_page_operation',operation('gas.quantities.read_v1',{'context':{**demo['quantityContext'],'snapshotId':after,'knowledgeMode':'retained','contextId':certificate['contextId']},
            'tradeId':demo['trades'][0],'selectionPolicy':'operational_projection_v1','includeHistory':False})
    _,results,_=ask([('discover_page_operations',{'page':'blotter','operation':'gas.quantities.read_v1'}),daily],
                    report={'page':'gas_setup','selection':{}},knowledge={**current,'frameId':after})
    check('daily gas quantities reuse the native deterministic selection policy',results[1].get('operation')=='gas.quantities.read_v1' and 'data' in results[1])
    daily_current={**demo['quantityContext'],'snapshotId':after}
    daily_rows=owner.call('gas/quantities/read',{'context':daily_current,'tradeId':demo['trades'][0],'selectionPolicy':'operational_projection_v1'})['rows']
    row=daily_rows[0];head=next(h for h in row['heads'] if h['kind']=='nominated')
    daily_write={'context':daily_current,'tradeId':demo['trades'][0],'expectedRevision':1,'operationId':str(uuid4()),'obligationId':row['obligationId'],
        'kind':'nominated','status':'provisional','day':row['day'],'quantity':'88','evidence':'Synthetic reviewed nomination correction','expectedPreviousId':head['id']}
    daily_turn,_,_=ask([('propose_page_action',operation('gas.quantities.record_v1',daily_write))],report={'page':'gas_setup','selection':{}},knowledge={**current,'frameId':after})
    daily_reference={'turnId':daily_turn['id'],'step':1}
    daily_receipt=owner.call('assistant/actions/authorize',daily_reference)
    check('approved daily gas corrections use native immutable revisions and replay the original outcome',owner.call('assistant/actions/authorize',daily_reference)==daily_receipt and
        next(h for h in owner.call('gas/quantities/read',{'context':daily_current,'tradeId':demo['trades'][0],'selectionPolicy':'operational_projection_v1'})['rows'][0]['heads'] if h['kind']=='nominated')['quantity']=='88')

    # Gas commercial cash uses the same pinned read and separately approved native write.
    allocated=next((h for h in row['heads'] if h['kind']=='allocated'),None)
    final_request={**daily_write,'operationId':str(uuid4()),'kind':'allocated','status':'final','quantity':'100',
        'evidence':'Synthetic accepted manual final quantity','expectedPreviousId':allocated['id'] if allocated else None}
    owner.call('gas/quantities/record',final_request)
    billing_current=dict(context=daily_current,tradeId=demo['trades'][0],expectedRevision=1,cashSlot=1,day=row['day'],
        selectionPolicy='final_allocated_billing_v1',profile='gas_final_daily_fixed_cash_v1',decimalPlaces=2,roundingPolicy='currency_minor_units_to_even_v1')
    def billing_read(rows):
        certificate=rows[0]['context']
        return 'read_page_operation',operation('gas.billing.preview_v1',{**billing_current,'context':{**daily_current,'knowledgeMode':'retained','contextId':certificate['contextId']}})
    _,cash_results,_=ask([('discover_page_operations',{'page':'blotter','operation':'gas.billing.preview_v1'}),billing_read],
        report={'page':'gas_setup','selection':{}},knowledge={**current,'frameId':after})
    cash_preview=cash_results[1]['data']
    check('gas billing agent preview retains native final source and signed cash',cash_preview['amount']=='-300' and cash_preview['contribution']=='-300')
    cash_write=dict(preview=billing_current,operationId=str(uuid4()),previewHash=cash_preview['previewHash'],sourceId=cash_preview['sourceId'],
        expectedPreviousId=None,correctionDueAt=None,reason=None,evidence='Synthetic approved agent cash review')
    cash_turn,_,_=ask([('propose_page_action',operation('gas.billing.determine_v1',cash_write))],report={'page':'gas_setup','selection':{}},knowledge={**current,'frameId':after})
    check('gas billing proposals leave cash history unchanged',owner.call('gas/billing/read',dict(context=daily_current,tradeId=demo['trades'][0]))['determinations']==[])
    cash_reference={'turnId':cash_turn['id'],'step':1}
    cash_receipt=owner.call('assistant/actions/authorize',cash_reference)
    from check_gas_billing_reads import run_checks as check_billing_reads
    check_billing_reads(query,manifest,owner,login,dict(context=daily_current,tradeId=demo['trades'][0]),dotnet,environment,owned_root)
    check('authorized gas cash uses the native handler and retains its first outcome',owner.call('assistant/actions/authorize',cash_reference)==cash_receipt and
        owner.call('gas/billing/read',dict(context=daily_current,tradeId=demo['trades'][0]))['determinations'][0]['rounded_total']=='-300')

    # The operational page discovers metadata without running contractual valuation.
    def directory_read(rows):
        certificate=rows[0]['context']
        return 'read_page_operation',operation('physical.operations.directory_v1',{
            'context':{'snapshotId':after,'convention':'trading_workspace_v1','knowledgeMode':'retained','contextId':certificate['contextId']},
            'bookIds':[],'portfolioIds':[],'offset':0,'limit':64})
    _,directory_results,_=ask([('discover_page_operations',{'page':'physical_operations','operation':'physical.operations.directory_v1'}),directory_read],
        report={'page':'physical_operations','selection':{}},knowledge={**current,'frameId':after})
    physical_facts=directory_results[1]['data']
    check('physical operations agent lists exact booked metadata under the captured header context without PV',
        any(p['tradeId']==demo['trades'][0] for p in physical_facts['positions']) and
        all('presentValue' not in p for p in physical_facts['positions']) and physical_facts['context']['contextId']==directory_results[0]['context']['contextId'])

    # Explicit fixture-only capabilities; no installed production role is changed by D339.
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN unnest(ARRAY['ReadGasOperations','ConfigureGasServices','RecordGasOperations'])p WHERE code='trader' ON CONFLICT DO NOTHING;")
    service_details=dict(obligationId=row['obligationId'],holderRole='legal_entity',externalReference='synthetic-agent-service',expectedPreviousId=None,validFrom='2021-01-01',validTo='2021-02-01',active=True,statementPolicy='final_statement_balance_v1',termsReference='Synthetic reviewed agent service',evidence='Synthetic agent service review')
    service_preview=dict(context=daily_current,tradeId=demo['trades'][0],expectedRevision=1,day=row['day'],kind='service',details=service_details)
    def operations_preview(rows):
        certificate=rows[0]['context']
        return 'read_page_operation',operation('gas.operations.preview_v1',{**service_preview,'context':{**daily_current,'knowledgeMode':'retained','contextId':certificate['contextId']}})
    _,operations_results,_=ask([('discover_page_operations',{'page':'physical_operations','operation':'gas.operations.preview_v1'}),operations_preview],report={'page':'physical_operations','selection':{}},knowledge={**current,'frameId':after})
    operations_review=operations_results[1]['data']
    native_review=owner.call('gas/operations/preview',service_preview)
    check('manual service preview shares deterministic pins across captured assistant and current UI context',native_review['previewHash']==operations_review['previewHash'])
    service_write=dict(preview=service_preview,operationId=str(uuid4()),previewHash=operations_review['previewHash'])
    service_turn,_,_=ask([('propose_page_action',operation('gas.operations.configure_v1',service_write))],report={'page':'physical_operations','selection':{}},knowledge={**current,'frameId':after})
    check('manual service proposals make no operational writes before approval',owner.call('gas/operations/read',dict(context=daily_current,tradeId=demo['trades'][0],day=row['day']))['state']['accounts']==[])
    service_reference={'turnId':service_turn['id'],'step':1};service_receipt=owner.call('assistant/actions/authorize',service_reference)
    check('approved service setup uses the native handler and retained outcome',owner.call('assistant/actions/authorize',service_reference)==service_receipt and len(owner.call('gas/operations/read',dict(context=daily_current,tradeId=demo['trades'][0],day=row['day']))['state']['accounts'])==1)

    # The page hands the assistant its selected instrument, gas day, delivery side and stage, and the agent then records a
    # nomination, its acknowledgments and the schedule they confirm, each prepared as a proposal and applied only on approval.
    position=next(p for p in physical_facts['positions'] if p['tradeId']==demo['trades'][0])
    scheduled_day=row['day'];before_day=(date.fromisoformat(scheduled_day)-timedelta(days=1)).isoformat()
    selection={'tradeId':demo['trades'][0],'bindingId':position['bindingId'],'revision':position['revision'],'instrumentType':'physical_gas',
        'tab':'delivery','day':scheduled_day,'obligationId':row['obligationId'],'stage':1}
    physical_report={'page':'physical_operations','selection':selection}
    _,page_view,_=ask([('read_workspace_page',{'page':'physical_operations'})],report=physical_report,knowledge={**current,'frameId':after})
    check('physical operations hands the assistant its selected instrument, gas day, delivery side and stage',page_view[0]['selection']==selection)
    def day_state():
        return owner.call('gas/operations/read',dict(context=daily_current,tradeId=demo['trades'][0],day=scheduled_day))['state']
    selected_read=page_view[0]['selectedView']
    check('the selected gas day reaches the assistant as its operations read under the pinned frame',
        selected_read['operation']=='gas.operations.read_v1' and selected_read['data']['state']==day_state())
    stated={}
    def repeated(rows):
        stated['input']=rows[0]['selectedView']['inputJson']
        return 'read_page_operation',{'operation':'gas.operations.read_v1','inputJson':stated['input']}
    def unstated(_):
        read=json.loads(stated['input']);del read['body']['context']['convention']
        return 'read_page_operation',{'operation':'gas.operations.read_v1','inputJson':json.dumps(read)}
    _,reused,_=ask([('read_workspace_page',{'page':'physical_operations'}),repeated,unstated],report=physical_report,knowledge={**current,'frameId':after})
    check('the selected read states the exact input the assistant repeats for the day in its turn',reused[1]['data']['state']==reused[0]['selectedView']['data']['state'])
    check('a gas read without its valuation convention is refused as such, not as an unavailable swap market',reused[2]['code']=='valuation_convention')
    _,trade_read,_=ask([('read_trades',{'view':'trade','mode':None,'bookIds':None,'portfolioIds':None,'groupBy':None,'measures':None,
        'tradeId':demo['trades'][0],'instrumentType':'physical_gas','revision':position['revision']})],report=physical_report,knowledge={**current,'frameId':after})
    check('the assistant reads a booked physical gas trade',trade_read[0]['result']['instrumentType']=='physical_gas' and trade_read[0]['result']['head']['trade_id']==demo['trades'][0])
    def agent_records(kind,details):
        preview=dict(context=daily_current,tradeId=demo['trades'][0],expectedRevision=1,day=scheduled_day,kind=kind,details=details)
        def reviewed(rows):
            certificate=rows[0]['context']
            return 'read_page_operation',operation('gas.operations.preview_v1',{**preview,'context':{**daily_current,'knowledgeMode':'retained','contextId':certificate['contextId']}})
        _,results,_=ask([('discover_page_operations',{'page':'physical_operations','operation':'gas.operations.preview_v1'}),reviewed],report=physical_report,knowledge={**current,'frameId':after})
        before=day_state()
        turn,_,_=ask([('propose_page_action',operation('gas.operations.record_v1',dict(preview=preview,operationId=str(uuid4()),previewHash=results[1]['data']['previewHash'])))],
            report=physical_report,knowledge={**current,'frameId':after})
        check(f'a proposed {kind} records nothing before approval',day_state()==before)
        reference={'turnId':turn['id'],'step':1};receipt=owner.call('assistant/actions/authorize',reference)
        check(f'an approved {kind} uses the native handler and replays its first outcome',owner.call('assistant/actions/authorize',reference)==receipt)
        return day_state()
    scheduled=lambda state:[h for h in state['heads'] if h['kind']=='scheduled']
    account=next(a for a in day_state()['accounts'] if a['origin_obligation_id']==row['obligationId']);schedule_before=scheduled(day_state())
    recorded=agent_records('nomination',dict(accountId=account['id'],serviceVersionId=account['version_id'],externalReference='synthetic-agent-nomination',expectedPreviousId=None,
        quantity='100',submittedAt=before_day+'T12:00:00Z',channel='portal',reason='Synthetic agent scheduling request',evidence='Synthetic reviewed agent nomination'))
    nomination=next(n for n in recorded['nominations'] if n['external_reference']=='synthetic-agent-nomination')
    for side,quantity in [('counterparty','95'),('pipeline','90')]:
        recorded=agent_records('response',dict(nominationId=nomination['id'],imbalanceId=None,side=side,decision='approved',quantity=quantity,
            externalReference='synthetic-agent-'+side,respondedAt=before_day+'T18:00:00Z',expectedPreviousId=None,evidence='Synthetic '+side+' acknowledgment'))
    check('recorded acknowledgments leave the schedule to an explicit application',scheduled(recorded)==schedule_before)
    responses={r['side']:r['id'] for r in recorded['responses'] if r['nomination_id']==nomination['id']}
    recorded=agent_records('nomination_apply',dict(nominationId=nomination['id'],counterpartyResponseId=responses['counterparty'],pipelineResponseId=responses['pipeline'],
        expectedPreviousId=None,profile='manual_confirmed_ceiling_v1',evidence='Synthetic reviewed agent scheduling adoption'))
    check('the approved application schedules the lower confirmed quantity',scheduled(recorded)!=schedule_before and [h['quantity'] for h in scheduled(recorded) if h not in schedule_before]==['90'])

    query("DELETE FROM security.role_permissions WHERE role_id=(SELECT id FROM security.roles WHERE code='trader') AND permission='RecordFacilityQuantities';")
    try:
        denied,_,_=ask([('read_workspace_page',{'page':'facility_quantities'}),preview_allocations],refused='access_changed')
        check('a facility reader without record authority cannot publish an allocation preview',denied['hidden']=='access_changed')
    finally:
        query("INSERT INTO security.role_permissions SELECT id,'RecordFacilityQuantities' FROM security.roles WHERE code='trader' ON CONFLICT DO NOTHING;")
    for page in ['facility_quantities','crude_setup']:
        saved=admin.call('settings/ai/page-prompts/save',{'operationId':str(uuid4()),'page':page,'expectedVersion':0,'instructions':'Synthetic reviewed delivery guidance. Require explicit terms and report missing inputs.'})
        check(page+' reuses immutable reviewed page guidance',saved['version']==1)
    print(f'{passed} physical assistant business scenarios passed.',flush=True)
    return {'agent':agent,**demo}
