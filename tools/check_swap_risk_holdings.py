"""Qualify stored swap risk materialization against real isolated bookings."""
import json
from datetime import datetime
import subprocess
import time
from pathlib import Path
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking


def run_checks(url,query,manifest,dotnet,owned_root,environment,psql):
    frozen_id=str(uuid4())
    query(f"""INSERT INTO market_data.observations
     SELECT (jsonb_populate_record(NULL::market_data.observations,to_jsonb(o)||jsonb_build_object(
      'id','{frozen_id}','role','final_contract_settlement','source_event_id','synthetic_risk_final','received_at',clock_timestamp()))).*
     FROM market_data.observations o WHERE catalog_id='{manifest['catalogId']}' AND role='daily_settlement' AND observed_at='2020-12-18T16:00:00Z'
     ORDER BY id LIMIT 1;""")
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
    workspace=client.call('workspace')
    frame=next(frame for frame in workspace['frames'] if frame['at'].startswith('2020-12-18'))
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    catalog=client.call('swaps/catalog',context)
    index=next(index for index in catalog['indices'] if index['curve']=='base_forward' and index['definitionRevisionId']==manifest['revisionId'])
    book,unsupported_book=[book['id'] for book in catalog['books'][:2]]
    delivery=next(delivery for delivery in index['contracts'] if delivery['deliveryFrom']=='2021-01-01')
    def payment(days):return {'frequency':'monthly','rule':'days_after_period_end','days':days,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    body={**context,**trade_booking(),'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'bookId':book,'quantity':'100','fixedCoefficient':'-1','discounting':True,
          'fixed':{'price':'3','currency':index['currencyCode'],**delivery,'paymentSchedule':payment(18)},
          'floating':{'spread': '0', 'settlement': {'mode': 'curve_target', 'index': None, 'projectionPolicy': None}, 'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],**delivery,'paymentSchedule':payment(46),'resetFrequency':'monthly','averagingMethod':'none'}}
    def book_swap(terms):return client.call('swaps/book',{**terms,'operationId':str(uuid4())})
    first=book_swap(body);second=book_swap({**body,'quantity':'250'});undiscounted=book_swap({**body,'quantity':'20','discounting':False})
    earlier=client.call('swaps/editor',{'context':context,'tradeId':first['tradeId'],'revision':None})['context']
    client.call('swaps/amend',{**body,'operationId':str(uuid4()),'tradeId':first['tradeId'],'expectedRevision':1,'effectiveAt':frame['at'],'reason':'Synthetic risk quantity correction','quantity':'200'})
    daily=next(index for index in catalog['indices'] if index['curve']=='daily_gas')
    daily_terms={**body,'bookId':book,'discounting':False,'definitionRevisionId':daily['definitionRevisionId'],
               'floating':{**body['floating'],'curve':daily['curve'],'index':daily['index'],'resetFrequency':'daily','averagingMethod':'arithmetic'}}
    daily_trade=book_swap(daily_terms)
    client.call('swaps/amend',{**daily_terms,'bookId':unsupported_book,'operationId':str(uuid4()),'tradeId':daily_trade['tradeId'],'expectedRevision':1,'effectiveAt':frame['at'],'reason':'Synthetic book transfer'})
    future_context={**context,'convention':'contract_lot_execution_pnl_v2'}
    future=client.call('futures/catalog',future_context)['contracts'][0]
    future_terms={'curve':future['curve'],'contractId':future['id'],'lots':'2','executionPrice':'2.9'}
    future_price=client.call('futures/price',{'context':future_context,**future_terms})['executionPnl']
    future_trade=client.call('futures/book',{**future_context,**trade_booking(),**future_terms,'operationId':str(uuid4()),'bookId':book})
    expected=[]
    for label,trade in [('amended',first),('shared',second),('undiscounted',undiscounted)]:
        editor=client.call('swaps/editor',{'context':context,'tradeId':trade['tradeId'],'revision':None})
        expected.append({'label':label,'tradeId':trade['tradeId'],'instrumentId':editor['state']['instrumentId'],'quantity':editor['state']['quantity'],
                         'revision':str(editor['state']['revision']),'presentValue':editor['valuation']['presentValue'],
                         'projectionIds':[reset['lookup']['contract']['key']['id'] for payment in editor['valuation']['sides'][1]['payments'] for reset in payment['resets']]})
    actual_cases=[]
    for day,count in [('01',1),('15',15),('31',31)]:
        selected=next(f for f in workspace['frames'] if f['at'].startswith(f'2021-01-{day}T08:00:00'))
        selected_context={**context,'snapshotId':selected['id']}
        actual_editor=client.call('swaps/editor',{'context':selected_context,'tradeId':daily_trade['tradeId'],'revision':None})
        supplemental=next(m for m in manifest['swapMarkets'] if m['revisionId']==daily['definitionRevisionId'])
        seed=next(f for f in supplemental['frames'] if datetime.fromisoformat(f['at'])==datetime.fromisoformat(selected['at']))
        actual_cases.append({'frame':{**selected,'observationIds':seed['observationIds'],'curveContracts':seed['curveContracts']},
            'context':actual_editor['context'],'revisionId':daily['definitionRevisionId'],'tradeId':daily_trade['tradeId'],
            'actualCount':count,'expectedValue':str(16+count),'actualIds':[reset['lineage'][0]['id'] for reset in actual_editor['valuation']['sides'][1]['payments'][0]['resets'] if reset['fixingState']=='Actual']})
    fixture=Path(owned_root)/'swap-risk-holdings.json'
    fixture.write_text(json.dumps({'frame':frame,'frozenObservationId':frozen_id,'bookId':book,'unsupportedBookId':unsupported_book,'sharedInstrumentId':first['instrumentId'],
                                 'amendedTradeId':first['tradeId'],'earlierQuantity':body['quantity'],'earlierContext':earlier,'currentContext':editor['context'],'holdings':expected,
                                 'futureTradeId':future_trade['tradeId'],'futureExecutionPnl':future_price,'actualCases':actual_cases}))
    repository=Path(__file__).resolve().parents[1]
    def managed(mode):subprocess.run([str(dotnet),'run',*(['--no-build'] if mode=='--swap-risk-holdings-expired' else []),'--project','tests/OpenEtrm.Api.Checks','--',mode,str(fixture)],cwd=repository,env=environment,check=True,timeout=90)
    managed('--swap-risk-holdings')
    managed('--portfolio-risk-scope')
    _reuse_reference_revocation(query,manifest,managed,fixture)
    _read_trades_expiry(query,manifest,psql,environment,managed,frame,editor['context'])
    scope={key:manifest[key] for key in ['catalogId','tenantId','workspaceId']}
    query(f"DELETE FROM security.market_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{scope['catalogId']}','{scope['tenantId']}','{scope['workspaceId']}');")
    try:managed('--swap-risk-holdings-denied')
    finally:query(f"INSERT INTO security.market_catalog_scopes VALUES('{scope['catalogId']}','{scope['tenantId']}','{scope['workspaceId']}');")


def _reuse_reference_revocation(query,manifest,managed,fixture):
    from concurrent.futures import ThreadPoolExecutor
    ready,release=Path(str(fixture)+'.reuse-ready'),Path(str(fixture)+'.reuse-release')
    reference=manifest['referenceCatalogId'];tenant=manifest['tenantId'];workspace=manifest['workspaceId']
    mode=query(f"SELECT access_mode FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{tenant}','{workspace}');")
    assert mode in ('owner','read_only'),mode
    revoked=False
    with ThreadPoolExecutor(max_workers=1) as pool:
        reading=pool.submit(managed,'--swap-risk-reuse-revoked')
        try:
            deadline=time.monotonic()+30
            while not ready.exists():
                if reading.done():reading.result()
                assert time.monotonic()<deadline,'Cached holdings did not reach the owned fallback boundary'
                time.sleep(.02)
            query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{reference}','{tenant}','{workspace}');")
            revoked=True
            release.write_text('released')
            reading.result(timeout=20)
        finally:
            release.write_text('released')
            if revoked:query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','{mode}');")
            ready.unlink(missing_ok=True);release.unlink(missing_ok=True)


def _read_trades_expiry(query,manifest,psql,environment,managed,frame,context):
    role=query("SELECT id FROM security.roles WHERE code='risk_analyst';")
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk';")
    temporary_role,grant=str(uuid4()),str(uuid4())
    locker=None
    from concurrent.futures import ThreadPoolExecutor
    try:
        query(f"BEGIN;INSERT INTO security.roles(id,code,name) VALUES('{temporary_role}','synthetic_expiring_trade_reader','Synthetic expiring trade reader');"
              f"INSERT INTO security.role_permissions VALUES('{temporary_role}','ReadTrades');"
              f"DELETE FROM security.role_permissions WHERE role_id='{role}' AND permission='ReadTrades';"
              f"INSERT INTO security.role_grants VALUES('{grant}','{principal}','{temporary_role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '3 seconds',false);COMMIT;")
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','risk','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        guard=(f"SELECT security.require_bound_market_context('{manifest['catalogId']}','{context['contextId']}','{frame['at']}');")
        prefix=(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
                f"SELECT security.use_market_context('{manifest['catalogId']}','{context['contextId']}');"
                f"SELECT security.use_trading_context('{manifest['catalogId']}','{frame['at']}');")
        # The same already-bound request loses only ReadTrades. Other calculation
        # rights and its exact market/trading coordinates remain valid.
        checked=query(prefix+guard+"SELECT pg_sleep(3.1);SELECT security.has_permission('ReadMarketData') AND security.has_permission('RunValuation') AND security.has_permission('RunScenario') AND NOT security.has_permission('ReadTrades');"
            "DO $$BEGIN "+guard+"RAISE EXCEPTION 'Expired ReadTrades returned a successful portfolio guard';EXCEPTION WHEN SQLSTATE '42501' THEN NULL;END$$;ROLLBACK;")
        assert checked.splitlines()[-1]=='t',checked
        print('PASS portfolio final guard: independently expiring ReadTrades refuses an otherwise authorized exact bound context',flush=True)
        locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        locker.stdin.write('BEGIN;LOCK TABLE trading.swap_trade_revisions IN ACCESS EXCLUSIVE MODE;\n\\echo LOCKED\n');locker.stdin.flush()
        while locker.stdout.readline().strip()!='LOCKED':
            if locker.poll() is not None:raise AssertionError(locker.stderr.read())
        query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '8 seconds' WHERE id='{grant}';")
        with ThreadPoolExecutor(max_workers=1) as pool:
            calculating=pool.submit(managed,'--swap-risk-holdings-expired')
            try:
                blocked=False
                for _ in range(100):
                    if query("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.usename='preview_runtime' AND NOT l.granted AND l.relation='trading.swap_trade_revisions'::regclass;")=='1':blocked=True;break
                    if calculating.done():calculating.result();break
                    time.sleep(.02)
                assert blocked,'Portfolio calculation did not reach the controlled trading read before expiry'
                for _ in range(200):
                    if query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{grant}';")=='t':break
                    time.sleep(.05)
                else:raise AssertionError('The independent ReadTrades grant did not expire within its bounded window')
            finally:
                locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
                assert locker.returncode==0,errors
            calculating.result(timeout=30)
    finally:
        if locker is not None and locker.poll() is None:
            locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
        query(f"BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{temporary_role}';DELETE FROM security.roles WHERE id='{temporary_role}';INSERT INTO security.role_permissions VALUES('{role}','ReadTrades') ON CONFLICT DO NOTHING;COMMIT;")
