"""Server booking facts are immutable across retries and financial amendments."""
from datetime import datetime, timezone
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking


def run_checks(url,query=None,manifest=None,client=None):
    c=client or PreviewClient(url);c.bootstrap();profile=c.call('profile',{'profile':'trader'})
    w=c.call('workspace');frame=next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context={'snapshotId':frame['id'],'convention':'fixed_total_cash_execution_pnl_v1','knowledgeMode':'current'}
    catalog=c.call('futures/catalog',context);contract=catalog['contracts'][0]
    parties={k:v for k,v in trade_booking().items() if k!='tradeDate'}
    body={**context,**parties,'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'curve':contract['curve'],'contractId':contract['id'],'lots':'2','executionPrice':'3','memo':'Synthetic automatic booking facts'}
    before=datetime.now(timezone.utc);result=c.call('futures/book',body);after=datetime.now(timezone.utc)
    state=c.call('futures/editor',{'context':context,'tradeId':result['tradeId'],'revision':None})['state']
    executed=datetime.fromisoformat(state['executedAt'].replace('Z','+00:00'))
    assert before<=executed<=after
    assert state['booking']['tradeDate']==executed.date().isoformat()
    assert state['traderId']==catalog['bookingActor']['id'] and state['trader']==catalog['bookingActor']['name']
    assert result==c.call('futures/book',body)
    for patch in [{'executedAt':'2020-12-18T15:00:00Z'},{'tradeDate':'2020-12-18'},{'traderId':str(uuid4())},{'traderRevision':1}]:
        assert c.call('futures/book',{**body,'operationId':str(uuid4()),**patch},expected=422)['code']=='trade_booking_read_only'
    amend={**body,'operationId':str(uuid4()),'tradeId':result['tradeId'],'expectedRevision':1,'lots':'3','effectiveAt':catalog['asOf'],'reason':'Synthetic quantity amendment','amendmentConvention':'unprocessed_futures_booking_amendment_v1'}
    c.call('futures/amend-preview',amend);c.call('futures/amend',amend)
    current=c.call('futures/editor',{'context':context,'tradeId':result['tradeId'],'revision':None})['state']
    for field in ['traderId','trader','executedAt']:
        assert current[field]==state[field]
    assert current['booking']['tradeDate']==state['booking']['tradeDate']
    print('PASS server booking facts: authenticated booker, UTC date, persistence timestamp, exact retries, forged-field rejection and immutable facts across amendments',flush=True)

    historical=c.call('futures/holdings',{**context,'knowledgeMode':'retained','contextId':catalog['context']['contextId']})
    assert not any(h['tradeId']==result['tradeId'] for h in historical['holdings'])
    swap_context={**context,'convention':'independent_side_financial_swap_v2'}
    swap_catalog=c.call('swaps/catalog',swap_context);index=next(i for i in swap_catalog['indices'] if i['curve']=='base_forward')
    rule={'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    swap={**swap_context,**parties,'operationId':str(uuid4()),'bookId':body['bookId'],'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'quantity':'100','fixedCoefficient':'-1','discounting':False,
          'fixed':{'price':'3','currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':rule},
          'floating':{'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':rule,'resetFrequency':'monthly','averagingMethod':'none'}}
    c.call('swaps/preview',swap);swap_result=c.call('swaps/book',swap)
    swap_state=c.call('swaps/editor',{'context':swap_context,'tradeId':swap_result['tradeId'],'revision':None})['state']
    assert swap_state['traderId']==state['traderId'] and swap_state['booking']['tradeDate']==datetime.fromisoformat(swap_state['executedAt'].replace('Z','+00:00')).date().isoformat()
    for patch in [{'executedAt':'2020-12-18T15:00:00Z'},{'tradeDate':'2020-12-18'},{'traderId':str(uuid4())}]:
        assert c.call('swaps/book',{**swap,'operationId':str(uuid4()),**patch},expected=422)['code']=='trade_booking_read_only'
    assert swap_result==c.call('swaps/book',swap)

    admin=PreviewClient(url);admin.bootstrap();admin.call('profile',{'profile':'admin'})
    login='booking_'+uuid4().hex[:12]
    other=admin.call('admin/create',{'operationId':str(uuid4()),'login':login,'name':'Synthetic second booker','kind':'human','roles':['trader']})
    admin.call('profile',{'profile':login});other_frame=next(f for f in admin.call('workspace')['frames'] if f['at'].startswith('2020-12-18'))
    other_context={**context,'snapshotId':other_frame['id']}
    other_amend={**amend,'snapshotId':other_frame['id'],'operationId':str(uuid4()),'expectedRevision':2,'lots':'4'}
    admin.call('futures/amend-preview',other_amend);admin.call('futures/amend',other_amend)
    other_swap={**swap,'snapshotId':other_frame['id'],'operationId':str(uuid4()),'tradeId':swap_result['tradeId'],'expectedRevision':1,'quantity':'200','effectiveAt':catalog['asOf'],'reason':'Synthetic quantity change by a different booker'}
    admin.call('swaps/preview',other_swap);admin.call('swaps/amend',other_swap)
    for product,trade,ctx,original in [('futures',result['tradeId'],other_context,state),('swaps',swap_result['tradeId'],{**other_context,'convention':swap_context['convention']},swap_state)]:
        changed=admin.call(product+'/editor',{'context':ctx,'tradeId':trade,'revision':None})['state']
        assert all(changed[k]==original[k] for k in ['traderId','trader','executedAt']) and changed['booking']['tradeDate']==original['booking']['tradeDate']
    admin.call('profile',{'profile':'admin'});member=next(m for m in admin.call('admin')['members'] if m['id']==state['traderId'])
    altered=admin.call('admin/update',{'operationId':str(uuid4()),'id':member['id'],'expectedVersion':member['version'],'name':'Synthetic renamed former booker','enabled':False,'roles':[]})
    try:
        admin.call('profile',{'profile':login})
        retained=admin.call('futures/editor',{'context':other_context,'tradeId':result['tradeId'],'revision':None})['state']
        assert retained['trader']==state['trader'] and retained['traderId']==state['traderId']
    finally:
        admin.call('profile',{'profile':'admin'});admin.call('admin/update',{'operationId':str(uuid4()),'id':member['id'],'expectedVersion':altered['version'],'name':member['name'],'enabled':member['enabled'],'roles':member['roles']})
    print('PASS immutable booker: swap retry, a different human amending both products, renamed/disabled original member, and pre-booking historical exclusion',flush=True)

    if query is not None:
        import json
        def issue():return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        for patch in [{'executed':"'2020-12-18T15:00:00Z'"},{'trader':"'"+str(uuid4())+"'"},{'date':'2020-12-18'}]:
            submitted={**parties,**({'tradeDate':patch['date']} if 'date' in patch else {})};booking=json.dumps(submitted).replace("'","''")
            query(f"""BEGIN;SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issue()}');
             DO $$ BEGIN
              PERFORM security.book_futures_with_parties('{manifest['catalogId']}','{uuid4()}','{body['bookId']}','{manifest['revisionId']}','{body['curve']}','{body['contractId']}','2020-12-18T16:00:00Z',{patch.get('executed','NULL')},1,3,'fixed_total_cash_execution_pnl_v1',{patch.get('trader','NULL')},NULL,NULL,'{booking}'::jsonb);
              RAISE EXCEPTION 'Forged booking facts accepted';
             EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'trade_booking_read_only' THEN RAISE;END IF;END $$;ROLLBACK;""")
        assert query("SELECT NOT has_table_privilege('preview_runtime','trading.booking_facts','UPDATE') AND NOT has_table_privilege('preview_runtime','security.request_contexts','UPDATE');")=='t'
        assert query("SELECT bool_and(NOT has_function_privilege('preview_runtime',oid,'EXECUTE')) FROM pg_proc WHERE pronamespace='security'::regnamespace AND proname IN('stamp_trade_booking','record_trade_booking');")=='t'
        original=query("SELECT pg_get_functiondef('security.stamp_trade_booking(uuid)'::regprocedure);")
        query("CREATE OR REPLACE FUNCTION security.stamp_trade_booking(catalog uuid) RETURNS timestamptz LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$BEGIN RAISE EXCEPTION 'An exact retry must not request another booking clock';END $$;")
        try:
            assert c.call('futures/book',body)==result
            assert c.call('swaps/book',swap)==swap_result
        finally:query(original)
        assert query(f"SELECT actor_id='{other['id']}'::uuid FROM trading.trade_revisions WHERE trade_id='{result['tradeId']}' AND revision=3;")=='t'
        print('PASS SQL booking boundary: forged facts reject, stamp helpers/table writes are private, retries never request a fresh clock and amendment actor stays independently audited',flush=True)
