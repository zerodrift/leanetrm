"""Synthetic selection, temporal visibility and held valuation checks."""
import json
from copy import deepcopy
from uuid import uuid4
from fractions import Fraction
from check_booked_side_programs import runtime_query
from native_read_transport import select_context_inputs
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking
from synthetic_side_calendars import side_calendar_policy


def run_checks(url,query,manifest,run_worker):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    workspace=c.call('workspace')
    before={'snapshotId':initial_frame(workspace,'2020-12-18')['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    after={**before,'snapshotId':next(f['id'] for f in workspace['frames'] if f['at'].startswith('2021-01-15T08:'))}
    catalog=c.call('gas/catalog',before);point=next(p for p in catalog['locations'] if p['deliveryPoint']=='synthetic_quantity_point')
    calendar=next(x['reference'] for x in catalog['sideCalendars'] if x['code']=='test_utc_every_day')
    body={**before,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'definitionRevisionId':point['definitionRevisionId'],
          'gasSetupId':catalog['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
          'physicalSides':[{'code':'delivery','pipeline':point['pipeline'],'zone':point['zone'],'deliveryPoint':point['deliveryPoint'],'product':point['product'],
              'direction':'receive','deliveryFrom':'2021-01-13','deliveryTo':'2021-01-17','quantityBasis':'per_operating_day','quantity':'1000','link':None,
              'quantityPolicy':'delivery_schedule_v1','calendarPolicy':side_calendar_policy(calendar,delivery=True,observation=False,payment=False)}],
          'cashSides':[{'code':'payment','role':'consideration','share':'1','direction':'pay','source':'delivery','stage':'delivery','price':'3','tariffCode':None,
              'paymentAt':'2021-02-02T00:00:00Z','calendarPolicy':side_calendar_policy(calendar,delivery=True,observation=False,payment=True)}]}
    preview=c.call('gas/composition/preview',body)
    booked=c.call('gas/composition/book',body)
    assert c.call('gas/composition/book',body)==booked
    initial_route_checks(c,before,booked,preview)
    cat=manifest['catalogId'];trade=booked['tradeId'];instrument=booked['instrumentId']
    terms=json.loads(query(f"SELECT jsonb_agg(jsonb_build_object('slot',slot,'policy',quantity_policy) ORDER BY slot) FROM trading.gas_component_terms WHERE instrument_id='{instrument}';"))
    assert terms==[{'slot':0,'policy':'delivery_schedule_v1'},{'slot':1,'policy':None}],terms
    old=deepcopy(body);old['operationId']=str(uuid4());del old['physicalSides'][0]['quantityPolicy']
    old_booked=c.call('gas/composition/book',old)
    assert query(f"SELECT count(*) FROM trading.gas_component_terms WHERE instrument_id='{old_booked['instrumentId']}' AND quantity_policy IS NOT NULL;")=='0'
    legacy_ticket_replay(c,query,manifest,before,old,old_booked)
    bad=deepcopy(body);bad['operationId']=str(uuid4());bad['physicalSides'][0]['quantityPolicy']='other_v1'
    assert c.call('gas/composition/book',bad,expected=422)['code']=='gas_quantity_policy'
    linked=deepcopy(body);linked['operationId']=str(uuid4());linked['physicalSides'].append({**linked['physicalSides'][0],'code':'linked','quantity':None,
        'quantityPolicy':None,'link':{'source':'delivery','formula':'gross_less_fraction','fraction':'0.02'}})
    assert c.call('gas/composition/book',linked,expected=422)['code']=='gas_quantity_policy'

    def quantities(context=after):
        return c.call('gas/quantities/read',{'context':context,'tradeId':trade,'selectionPolicy':'operational_projection_v1'})
    original=quantities();obligation=original['rows'][0]['obligationId']
    def read_heads(context):
        bind=select_context_inputs(cat,context)+f"SELECT security.use_market_context('{cat}','{context['contextId']}');SELECT security.use_trading_context('{cat}','2021-01-15T08:00:00Z');"
        return json.loads(runtime_query(query,manifest,bind+f"SELECT security.read_gas_valuation_quantities('{cat}','{trade}','{instrument}');").splitlines()[-1])
    assert read_heads(original['context'])==[]
    def record(day,kind,amount,status='provisional',previous=None):
        request={'context':after,'tradeId':trade,'expectedRevision':1,'operationId':str(uuid4()),'obligationId':obligation,'kind':kind,'status':status,
                 'day':day,'quantity':amount,'evidence':'Synthetic selected delivery statement','expectedPreviousId':previous}
        return c.call('gas/quantities/record',request)
    record('2021-01-13','nominated','990')
    first=record('2021-01-13','allocated','980','final')
    second=record('2021-01-14','scheduled','1050')
    record('2021-01-15','scheduled','900')
    current=quantities();heads=read_heads(current['context'])
    assert sorted((h['day'],h['kind'],h['quantity']) for h in heads)==[
        ('2021-01-13','allocated','980'),('2021-01-14','scheduled','1050'),('2021-01-15','scheduled','900')],heads
    assert read_heads(original['context'])==[]
    valuation_checks(c,query,manifest,run_worker,after,body,booked,old_booked,obligation,first,record,quantities)
    record('2021-01-14','scheduled',None,'withdrawn',second['revisionId'])
    heads=read_heads(quantities()['context'])
    assert next(h for h in heads if h['day']=='2021-01-14' and h['kind']=='scheduled')['status']=='withdrawn'
    print('PASS delivery quantity storage: explicit booking policy, replay, linked refusal, native heads and earlier knowledge cutoff',flush=True)
    usage_fee_checks(c,body,after)
    return dict(tradeId=trade,instrumentId=instrument,snapshotId=after['snapshotId'],tradeReference=query(f"SELECT reference_number FROM trading.trade_references WHERE trade_id='{trade}';"))


def initial_route_checks(c,context,booked,preview):
    from check_portfolio_risk import VIEW
    trade=booked['tradeId']
    read=c.call('gas/composition/editor',dict(context=context,tradeId=trade))['valuation']
    expected=[Fraction(12800),Fraction(-12000)]
    assert [Fraction(s['presentValue']) for s in preview['sides']]==expected
    assert [Fraction(s['presentValue']) for s in read['sides']]==expected
    assert Fraction(preview['presentValue'])==Fraction(read['presentValue'])==800
    shared={**context,'convention':'trading_workspace_v1'}
    blotter=c.call('trading/blotter',dict(context=shared,view=VIEW))
    assert Fraction(next(h for h in blotter['holdings'] if h['tradeId']==trade)['presentValue'])==800
    c.call('profile',{'profile':'risk'})
    scope=dict(context=shared,bookIds=[],portfolioIds=[],tradeIds=[trade])
    catalog=c.call('portfolio/risk/catalog',scope)
    result=c.call('portfolio/risk',{**scope,'context':{**shared,'knowledgeMode':'retained','contextId':catalog['context']['contextId']},
        'detail':'holdings','convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],
        'pricingPolicy':catalog['pricingPolicy'],'stress':[],
        'bumps':[dict(factor=f['factor'],size='0.1',convention=catalog['bumpConvention']) for f in catalog['factors'][:16]]})
    assert result['factors']
    assert all(Fraction(p['values']['base'])==800 for f in result['factors'] for p in f['positions'])
    c.call('profile',{'profile':'trader'})
    print('PASS gas route baseline: preview and retained Side amounts, editor, Blotter and risk agree before operational records',flush=True)


def legacy_ticket_replay(c,query,manifest,context,body,booked):
    draft=c.call('tickets/trade',dict(context=context,tradeId=booked['tradeId'],bindingId=booked['instrumentId'],revision=1))['draft']
    for side in draft['sides']:
        if side['kind']=='physical':del side['values']['quantity_policy']
    request=dict(context=context,draft=draft,definitionRevisionId=body['definitionRevisionId'],operationId=body['operationId'])
    assert c.call('tickets/book',request)==booked, 'A completed legacy ticket must retain its first outcome'
    changed=deepcopy(request);changed['draft']['sides'][0]['values']['quantity']='1100'
    assert c.call('tickets/book',changed,expected=409)['code']=='gas_conflict', 'Replay must still reach native payload comparison'
    fresh={**request,'operationId':str(uuid4())}
    assert c.call('tickets/book',fresh,expected=422)['code']=='ticket_input'
    assert query(f"SELECT count(*) FROM trading.side_schedule_requests WHERE request_id='{fresh['operationId']}';")=='0'
    # An interrupted predecessor command has a retained actor/hash even before it has an outcome.
    pending={**request,'operationId':str(uuid4())};cat=manifest['catalogId']
    query(f"""INSERT INTO trading.side_schedule_requests(catalog_id,request_id,actor_id,command_hash,permission)
      SELECT s.catalog_id,'{pending['operationId']}',s.actor_id,
        sha256(convert_to((t.request||jsonb_build_object('operationId','{pending['operationId']}'))::text,'UTF8')),s.permission
      FROM trading.side_schedule_requests s JOIN trading.trades t ON(t.catalog_id,t.operation_id)=(s.catalog_id,s.request_id)
      WHERE(s.catalog_id,s.request_id)=('{cat}','{body['operationId']}');""")
    resumed=c.call('tickets/book',pending)
    assert c.call('tickets/book',pending)==resumed
    assert query(f"SELECT count(*) FROM trading.gas_trade_revisions WHERE operation_id='{pending['operationId']}';")=='1'
    other={**request,'operationId':str(uuid4())}
    query(f"""INSERT INTO trading.side_schedule_requests(catalog_id,request_id,actor_id,command_hash,permission)
      SELECT catalog_id,'{other['operationId']}',(SELECT id FROM security.principals WHERE id<>s.actor_id ORDER BY id LIMIT 1),command_hash,permission
      FROM trading.side_schedule_requests s WHERE(catalog_id,request_id)=('{cat}','{body['operationId']}');""")
    assert c.call('tickets/book',other,expected=422)['code']=='ticket_input'
    amendment=deepcopy(request);amendment.update(tradeId=resumed['tradeId'],bindingId=resumed['instrumentId'],expectedRevision=1,
        operationId=str(uuid4()),reason='Synthetic retained price correction',policy='future_gas_composition_replace_v1')
    amendment['draft']=c.call('tickets/trade',dict(context=context,tradeId=resumed['tradeId'],bindingId=resumed['instrumentId'],revision=1))['draft']
    amendment['draft']['sides'][0]['values']['quantity_policy']='contract'
    amendment['draft']['sides'][1]['values']['cash_price']='3.1'
    amended=c.call('tickets/amend',amendment)
    del amendment['draft']['sides'][0]['values']['quantity_policy']
    assert c.call('tickets/amend',amendment)==amended
    assert c.call('tickets/amend',{**amendment,'reason':'Synthetic changed retry'},expected=409)['code']=='gas_conflict'
    print('PASS legacy ticket replay: completed booking/amendment and interrupted commands retain exact outcomes; changed terms, other actors and fresh missing policy refuse',flush=True)


def upgrade_checks(query,migration):
    from assistant_context import migration_body
    body=migration_body(migration.read_text());signature='security.write_gas_composition(uuid,jsonb,timestamptz,boolean)'
    for change in (f"GRANT EXECUTE ON FUNCTION {signature} TO PUBLIC;",f"ALTER FUNCTION {signature} SET work_mem='64kB';",
                   f"ALTER FUNCTION {signature} OWNER TO CURRENT_USER;",
                   f"DO $drift$ DECLARE s text=pg_get_functiondef('{signature}'::regprocedure);BEGIN EXECUTE replace(s,'AS $function$','AS $function$ /* synthetic drift */');END$drift$;"):
        try:query(f'BEGIN;{change}{body}ROLLBACK;')
        except RuntimeError as error:assert 'gas_quantity_upgrade: unexpected composition writer' in str(error),str(error)
        else:raise AssertionError('A changed composition writer was accepted')
    query(f'BEGIN;SET LOCAL search_path=security,public;{body}ROLLBACK;')
    print('PASS delivery quantity upgrade: changed writer definition, owner, configuration and grants refuse atomically; caller search path is independent',flush=True)


def valuation_checks(c,query,manifest,run_worker,context,body,booked,legacy,obligation,first,record,quantities):
    from check_portfolio_risk import financial,VIEW
    cat=manifest['catalogId'];trade=booked['tradeId'];binding=booked['instrumentId']
    def editor(selected=context):return c.call('gas/composition/editor',dict(context=selected,tradeId=trade))['valuation']
    def selected():return c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='delivery_schedule_valuation_v1'))
    value=editor();m=value['measures']
    assert [Fraction(m[k]) for k in ('unrealized','realizedUnsettled','total')]==[380,-6090,-5710],m
    ticket=c.call('tickets/trade',dict(context=context,tradeId=trade,bindingId=binding,revision=1))
    assert ticket['measures']==m and ticket['draft']['sides'][0]['values']['quantity_policy']=='delivery_schedule_v1'
    days={r['key']:r for r in ticket['schedule']['rows'] if r['level']=='day'}
    assert [days[d]['cells']['delivery.quantity']['value'] for d in ('2021-01-13','2021-01-14','2021-01-15','2021-01-16')]==['980','1050','900','1000']
    assert 'allocated_final' in days['2021-01-13']['cells']['delivery.quantity']['sources']
    assert days['2021-01-13']['cells']['delivery.quantity']['quantityEvidence'][0]['revisionId']==first['revisionId']
    assert 'contract' in days['2021-01-16']['cells']['delivery.quantity']['sources']
    old=c.call('gas/composition/editor',dict(context=context,tradeId=legacy['tradeId']))['valuation']['measures']
    assert [Fraction(old[k]) for k in ('unrealized','realizedUnsettled','total')]==[400,-6000,-5600],old
    # Blotter and retained instrument evaluations share the same selected held state.
    shared={**context,'convention':'trading_workspace_v1'}
    blotter=c.call('trading/blotter',dict(context=shared,view=VIEW))
    assert Fraction(next(h for h in blotter['holdings'] if h['tradeId']==trade)['presentValue'])==-5710
    c.call('profile',{'profile':'risk'})
    scope=dict(context=shared,bookIds=[],portfolioIds=[],tradeIds=[trade])
    catalog=c.call('portfolio/risk/catalog',scope)
    request={**scope,'context':{**shared,'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
             'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
             'bumps':[dict(factor=f['factor'],size='0.1',convention=catalog['bumpConvention']) for f in catalog['factors'][:16]]}
    result=c.call('portfolio/risk',request)
    assert result['factors'],result
    for f in result['factors']:
        position=f['positions'][0]
        assert Fraction(position['values']['base'])==-5710 and Fraction(position['values']['up'])==-5520 and Fraction(position['values']['delta'])==1900 and Fraction(position['components']['realizedUnsettled']['up'])==-6090,position
    job=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request))
    c.call('profile',{'profile':'trader'})
    correction=record('2021-01-14','allocated','1020','final')
    assert Fraction(editor()['measures']['realizedUnsettled'])==-6000
    historical={**context,'knowledgeMode':'retained','contextId':catalog['context']['contextId']}
    c.select_inputs(catalog['context'])
    assert Fraction(editor(historical)['measures']['realizedUnsettled'])==-6090
    run_worker();c.call('profile',{'profile':'risk'})
    done=c.call('portfolio/risk/jobs/'+job['id'])
    assert done['state']=='completed' and financial(done['result'])==financial(result),done
    c.select_inputs(catalog['context'])
    worker_scope_checks(c,query,manifest,request,booked,legacy)
    c.call('profile',{'profile':'trader'})
    print('PASS delivery quantities: worked example, legacy values, ticket evidence, Blotter parity, immutable risk inputs and queued replay after correction',flush=True)

    # Trade readers need no facility operation privilege to value their own accepted allocations.
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN unnest(ARRAY['ReadFacilityQuantities','RecordFacilityQuantities'])p WHERE code='trader' ON CONFLICT DO NOTHING;")
    def facility(command,at='2021-01-15T08:00:00Z'):
        payload=json.dumps(command).replace("'","''")
        return json.loads(runtime_query(query,manifest,f"SELECT security.resolve_operation_context('{cat}','2021-01-15',NULL);SELECT security.record_facility_quantity_command('{cat}','{uuid4()}','{payload}'::jsonb,'{at}');").splitlines()[-1])
    ledger=facility(dict(kind='create',obligationId=obligation,day='2021-01-13',facility=body['physicalSides'][0]['deliveryPoint']))['ledgerId']
    source=facility(dict(kind='source',ledgerId=ledger,expectedPreviousId=None,quantity='1000',evidence='Synthetic facility total'))['revisionId']
    plan=None;adoption=None
    def adopt(amount,at='2021-01-15T08:00:00Z'):
        nonlocal plan,adoption
        plan=facility(dict(kind='allocate',ledgerId=ledger,expectedPreviousId=plan,sourceRevisionId=source,evidence='Synthetic allocation plan',
                          allocations=[dict(obligationId=obligation,tradeRevision=1,quantity=amount)]),at)['revisionId']
        adoption=facility(dict(kind='adopt',ledgerId=ledger,planRevisionId=plan,sourceRevisionId=source,expectedPreviousId=adoption,evidence='Synthetic accepted allocation'),at)['adoptionId']
    adopt('975')
    assert c.call('gas/composition/editor',dict(context=context,tradeId=trade),expected=422)['code']=='gas_quantity_selection'
    assert c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='delivery_schedule_valuation_v1'),expected=422)['code']=='gas_quantity_selection'
    rows=c.call('trading/blotter',dict(context=shared,view=VIEW))['holdings']
    refused=next(h for h in rows if h['tradeId']==trade)
    assert refused['presentValue'] is None and 'gas_quantity_selection' in json.dumps(refused),refused
    assert next(h for h in rows if h['tradeId']==legacy['tradeId'])['presentValue'] is not None
    conflict_context=quantities()['context']
    c.call('profile',{'profile':'risk'});c.select_inputs(conflict_context)
    assert c.call('portfolio/risk/catalog',scope,expected=422)['code']=='gas_quantity_selection'
    blocked_request={**request,'context':{**shared,'knowledgeMode':'retained','contextId':conflict_context['contextId']}}
    assert c.call('portfolio/risk',blocked_request,expected=422)['code']=='gas_quantity_selection'
    blocked=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=blocked_request),expected=(200,422))
    if c.last_status==200:
        run_worker();failed=c.call('portfolio/risk/jobs/'+blocked['id'])
        assert failed['state']=='failed' and failed['errorCode']=='gas_quantity_selection',failed
    else:assert blocked['code']=='gas_quantity_selection',blocked
    c.call('profile',{'profile':'trader'})
    adopt('980')
    agreeing=selected()['rows'][0]
    assert agreeing['selected']=={'numerator':'980','denominator':'1'} and len(agreeing['provenance'])==2,agreeing
    withdrawn=record('2021-01-13','allocated',None,'withdrawn',first['revisionId'])
    assert selected()['rows'][0]['selected']=={'numerator':'980','denominator':'1'}
    assert Fraction(editor()['measures']['realizedUnsettled'])==-6000
    adoption=facility(dict(kind='retract_adoption',ledgerId=ledger,planRevisionId=plan,sourceRevisionId=source,expectedPreviousId=adoption,evidence='Synthetic acceptance withdrawal'))['adoptionId']
    assert selected()['rows'][0]['selected']=={'numerator':'1000','denominator':'1'},'Withdrawal must not revive the prior manual allocation'
    # Known later-effective adoption is excluded at the earlier financial instant.
    adopt('900','2021-01-15T20:00:00Z')
    current=selected();assert current['rows'][0]['selected']=={'numerator':'1000','denominator':'1'}
    bind=select_context_inputs(cat,current['context'])+f"SELECT security.use_market_context('{cat}','{current['context']['contextId']}');SELECT security.use_trading_context('{cat}','2021-01-15T20:00:00Z');"
    heads=json.loads(runtime_query(query,manifest,bind+f"SELECT security.read_gas_valuation_quantities('{cat}','{trade}','{binding}');").splitlines()[-1])
    assert next(h for h in heads if h['source']=='facility_allocation_adoption_v1')['quantity']=='900'
    print('PASS adopted allocations: conflict refuses the complete valuation, equal ranks retain both references, independent withdrawals and financial business-time admission',flush=True)

    # Payment remains a recorded fact when both ended quantities correct to zero.
    payment=dict(operationId=str(uuid4()),tradeId=trade,expectedRevision=1,slot=1,businessKey='synthetic-selected-remittance',
                 paidAt='2021-01-15T07:00:00Z',currency='usd',amount='-2940',allocations=[dict(occurrence=0,amount='-2940')])
    command=f"SELECT security.record_gas_cash_payment('{cat}','{json.dumps(payment)}'::jsonb,'2021-01-15T08:00:00Z');"
    paid=runtime_query(query,manifest,command)
    assert runtime_query(query,manifest,command)==paid
    record('2021-01-13','allocated','0','final',withdrawn['revisionId'])
    record('2021-01-14','allocated','0','final',correction['revisionId'])
    zero=editor()['measures']
    assert [Fraction(zero[k]) for k in ('unrealized','realizedUnsettled','total')]==[380,2940,3320],zero
    position=dict(context=shared,tradeId=trade,bindingId=binding,revision=1,instrumentType='physical_gas')
    row=c.call('trading/side-schedules',dict(position=position,sideSlot=1,contribution=None,offset=0,limit=128))['page']['rows'][0]
    assert (row['lifecycle']['cash'],row['accrued'],row['paid'],row['outstanding'])==('Overpaid','0','-2940','2940'),row
    c.select_inputs(catalog['context'])
    assert Fraction(editor(historical)['measures']['realizedUnsettled'])==-6090
    print('PASS zero-after-payment: recorded payment retained, full overpaid balance displayed, earlier view unchanged',flush=True)


def worker_scope_checks(c,query,manifest,request,booked,legacy):
    cat=manifest['catalogId'];trade=booked['tradeId'];binding=booked['instrumentId']
    job=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request))
    engine=query(f"SELECT request->>'engineVersion' FROM risk.jobs WHERE id='{job['id']}';")
    claim=json.loads(query(f"SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.claim(120,'{engine}');"))
    assert claim['jobId']==job['id'],claim
    bind=f"SELECT risk.bind('{job['id']}','{claim['leaseToken']}');"
    read=f"SELECT security.read_gas_valuation_quantities('{cat}','{trade}','{binding}');"
    worker=lambda sql:query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;{bind}{sql}ROLLBACK;")
    assert len(json.loads(worker(read).splitlines()[-1]))==3
    assert query("SELECT security.principal_has_permission(w.principal_id,w.tenant_id,w.workspace_id,'ReadGasOperations',clock_timestamp()) FROM risk.workers w WHERE login='preview_risk_worker';")=='f'
    def refuse(sql):
        try:worker(sql)
        except RuntimeError as error:
            assert any(code in str(error) for code in ('gas_quantity_','risk_job_','risk_worker','risk_attempt','context_','No market','No trading','permission')),str(error)
        else:raise AssertionError('Worker quantity read disclosed outside its active scope')
    refuse(read.replace(binding,str(uuid4())))
    refuse(f"SELECT security.read_gas_valuation_quantities('{cat}','{legacy['tradeId']}','{legacy['instrumentId']}');")
    refuse(read.replace(cat,str(uuid4())))
    # Faults live only in rolled-back transactions in the owned disposable database.
    def drift(sql):return 'RESET SESSION AUTHORIZATION;SET LOCAL session_replication_role=replica;'+sql+'SET LOCAL session_replication_role=origin;SET LOCAL SESSION AUTHORIZATION preview_risk_worker;'+read
    refuse(drift("UPDATE security.request_contexts SET trading_as_of=trading_as_of+interval '1 hour' WHERE backend_pid=pg_backend_pid();"))
    refuse(drift(f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{job['id']}';"))
    refuse(drift(f"UPDATE risk.jobs SET request=jsonb_set(request,'{{bookIds}}','[]') WHERE id='{job['id']}';"))
    refuse(drift("UPDATE security.workspace_memberships SET enabled=false WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='preview_risk_worker');"))
    query(f"SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.finish('{job['id']}','{claim['leaseToken']}',NULL,'synthetic_probe_complete');")
    print('PASS ordinary risk quantity reader: active lease without gas-operations permission, exact binding/trade/book/catalog/time scope and expiry/revocation refusal',flush=True)


def usage_fee_checks(c,body,context):
    fee=deepcopy(body);fee['operationId']=str(uuid4());fee['cashSides'].append({**fee['cashSides'][0],'code':'usage','role':'fee','share':'1','price':None,'tariffCode':'synthetic_gas_usage_a'})
    booked=c.call('gas/composition/book',fee);trade=booked['tradeId']
    obligation=c.call('gas/quantities/read',dict(context=context,tradeId=trade,selectionPolicy='operational_projection_v1'))['rows'][0]['obligationId']
    for day,kind,status,quantity in [('2021-01-13','allocated','final','980'),('2021-01-14','scheduled','provisional','1050'),('2021-01-15','scheduled','provisional','900')]:
        c.call('gas/quantities/record',dict(context=context,tradeId=trade,expectedRevision=1,operationId=str(uuid4()),obligationId=obligation,
            day=day,kind=kind,status=status,quantity=quantity,evidence='Synthetic usage basis',expectedPreviousId=None))
    value=c.call('gas/composition/editor',dict(context=context,tradeId=trade))['valuation']
    assert [Fraction(value['measures'][k]) for k in ('unrealized','realizedUnsettled','total')]==[152,Fraction('-6333.6'),Fraction('-6181.6')],value['measures']
    usage=next(s for s in value['sides'] if s['code']=='usage')
    assert Fraction(int(usage['quantity']['numerator']),int(usage['quantity']['denominator']))==3930
    print('PASS configured usage tariff follows the source selected quantity in value and daily accrual',flush=True)
