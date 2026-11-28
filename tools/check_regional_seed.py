"""Independent numerical and temporal checks of the regional synthetic preset."""
from datetime import date, datetime
from decimal import Decimal, localcontext, ROUND_HALF_EVEN
from fractions import Fraction
import json
import time
from uuid import uuid4
from concurrent.futures import ThreadPoolExecutor
from authority_wait import lock_holder
from preview_client import PreviewClient, initial_frame


def usd_values(client, snapshot):
    result=client.call('trading/blotter',dict(context=dict(snapshotId=snapshot,convention='trading_workspace_v1',knowledgeMode='current'),
        view=dict(mode='trades',bookIds=['d1000000-0000-0000-0000-000000000001','d1000000-0000-0000-0000-000000000002'],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)))
    return {r['tradeId']:tuple(r.get(k) for k in ('instrumentId','pricingBindingId','presentValue','executionPnl','currencyReference')) for r in result['holdings']}


def usd_baseline(url):
    client=PreviewClient(url);client.bootstrap();client.call('profile',{'profile':'trader'})
    frame=initial_frame(client.call('workspace'),'2020-12-18')
    values=usd_values(client,frame['id'])
    assert len(values)==24
    return values


def startup_checks(url):
    client=PreviewClient(url);client.bootstrap()
    client.call('profile',{'profile':'viewer'})
    assert client.call('close-sessions/choice')['session']=='nyc_close'
    assert {row['code'] for row in client.call('reference/catalog?table=currencies')['rows']}=={'usd','gbp','eur','cad'}
    library=client.call('date-schedules')['contractDefinitions']
    assert {'gas_delivery','forward_delivery'} <= {v['definition']['code'] for v in library if v['state']=='published'}
    sessions={session['code'] for session in client.call('close-sessions/catalog')['sessions']}
    assert sessions=={'nyc_close','uk_close'},sessions
    print('PASS interactive regional startup: compiled dates, current authoring contexts, four currencies and two selected regional close conventions',flush=True)


def prepare(query, manifest):
    """Interrupt after a real committed mapping, then race two resumptions."""
    from regional_seed import install
    interrupted=False
    class Interrupted(Exception):
        pass
    def fail_after_mapping(sql):
        nonlocal interrupted
        result=query(sql)
        if not interrupted and 'CREATE FUNCTION pg_temp.seed_library_phase()' in sql and 'INSERT INTO market_data.contract_date_definitions' in sql:
            interrupted=True
            raise Interrupted()
        return result
    try:
        install(fail_after_mapping,manifest)
        raise AssertionError('The mapping interruption was not exercised')
    except Interrupted:
        pass
    assert interrupted
    with ThreadPoolExecutor(max_workers=2) as workers:
        pending=[workers.submit(install,query,manifest) for _ in range(2)]
        results=[result.result(timeout=120) for result in pending]
    assert results[0]==results[1],'Concurrent resumes selected different facts'
    print('PASS regional seed: committed mapping interruption and two concurrent resumes produce one exact source',flush=True)
    return results[0]


def authorization_checks(query, manifest):
    """Real hosted admission on disposable synthetic accounts, including expiry."""
    from regional_seed import install
    from seed_exact_rows import hosted_write, rows_sql
    cat,ref,tenant,workspace=(manifest[k] for k in ('catalogId','referenceCatalogId','tenantId','workspaceId'))
    issuer='https://regional-seed.synthetic.invalid/local'
    def account(full):
        principal=str(uuid4());login='seed_'+uuid4().hex[:12];digest=uuid4().hex+uuid4().hex
        roles="code IN('viewer','market_data_editor','market_data_approver','access_administrator','trader')" if full else "code='viewer'"
        query(f"""INSERT INTO security.principals VALUES('{principal}','human',true);
INSERT INTO security.identity_bindings VALUES('{issuer}','{principal}','{principal}','human_interactive');
INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{principal}','{login}','Synthetic seed qualification',true,1);
INSERT INTO security.hosted_accounts(principal_id,issuer,tenant_id,workspace_id,login,password_hash,credential_version) VALUES('{principal}','{issuer}','{tenant}','{workspace}','{login}','synthetic_qualification_only',1);
INSERT INTO security.role_grants SELECT gen_random_uuid(),'{principal}',id,'{tenant}','{workspace}',clock_timestamp()-interval '1 minute',clock_timestamp()+interval '1 hour',false FROM security.roles WHERE {roles};
INSERT INTO security.hosted_sessions(token_digest,principal_id,credential_version,expires_at) VALUES('{digest}','{principal}',1,clock_timestamp()+interval '10 minutes');""")
        return principal,dict(authenticator='preview_authenticator',runtime='preview_runtime',sessionDigest=digest)
    reader,reader_identity=account(False)
    owner,owner_identity=account(True)
    def denied(action):
        try:
            action()
        except RuntimeError as error:
            assert any(code in str(error) for code in ('regional_seed_denied','42501','session is unavailable','current publishing authority','Publishing authority','No current')) ,str(error)
        else:
            raise AssertionError('Unauthorized source write was admitted')
    before=query('SELECT count(*) FROM ref_data.revisions;')
    denied(lambda:install(query,manifest,identity=reader_identity))
    marker=str(uuid4())
    statement=rows_sql('ref_data.revisions',[dict(catalog_id=ref,id=marker,revision_key='regional_auth_'+marker.replace('-',''))],('catalog_id','id'))
    hosted_write(query,statement,cat,ref,owner_identity)
    assert query(f"SELECT count(*) FROM ref_data.revisions WHERE id='{marker}';")=='1'
    marker=str(uuid4())
    statement=rows_sql('ref_data.revisions',[dict(catalog_id=ref,id=marker,revision_key='regional_auth_'+marker.replace('-',''))],('catalog_id','id'))
    query(f"UPDATE security.reference_catalog_scopes SET access_mode='read_only' WHERE(catalog_id,tenant_id,workspace_id)=('{ref}','{tenant}','{workspace}');")
    try:
        denied(lambda:hosted_write(query,statement,cat,ref,owner_identity))
    finally:
        query(f"UPDATE security.reference_catalog_scopes SET access_mode='owner' WHERE(catalog_id,tenant_id,workspace_id)=('{ref}','{tenant}','{workspace}');")
    # A phase admitted before its sign-in or role grants end must still roll back at exit. After its write the phase
    # waits for a private lock, kept until it has waited there with that authority live and the authority has ended.
    # A phase that started late never waits there, which fails the lock holder.
    def expires_in_flight(table,term):
        gate=uuid4().int%(1<<63);application='regional_seed_expiry_'+uuid4().hex
        with ThreadPoolExecutor(max_workers=1) as pool:
            held=pool.submit(query,lock_holder(application,f'SELECT pg_advisory_xact_lock({gate})',
                f"(SELECT bool_and({term}<=clock_timestamp()) FROM security.{table} WHERE principal_id='{owner}')"))
            deadline=time.monotonic()+5
            while query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{application}' AND wait_event='PgSleep';")!='1':
                if held.done():held.result()
                assert time.monotonic()<deadline,'The owned expiry lock was not taken'
                time.sleep(.02)
            try:
                query(f"UPDATE security.{table} SET {term}=clock_timestamp()+interval '2 seconds' WHERE principal_id='{owner}';")
                denied(lambda:hosted_write(query,statement+f'SELECT pg_advisory_xact_lock({gate});',cat,ref,owner_identity))
            finally:held.result()
        assert query(f"SELECT count(*) FROM ref_data.revisions WHERE id='{marker}';")=='0'
    expires_in_flight('hosted_sessions','expires_at')
    denied(lambda:hosted_write(query,statement,cat,ref,owner_identity))
    query(f"UPDATE security.hosted_sessions SET expires_at=clock_timestamp()+interval '10 minutes' WHERE principal_id='{owner}';")
    expires_in_flight('role_grants','granted_to')
    query(f"UPDATE security.role_grants SET revoked=true WHERE principal_id='{owner}';")
    denied(lambda:install(query,manifest,identity=owner_identity))
    assert int(query('SELECT count(*) FROM ref_data.revisions;'))==int(before)+1
    assert query("SELECT count(*) FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) a JOIN pg_roles r ON r.oid=a.grantee WHERE n.nspname LIKE 'pg_temp_%' AND r.rolname='preview_runtime' AND a.privilege_type='USAGE';")=='0'
    print('PASS regional seed: read-only, expired sign-in, mid-phase sign-in/role expiry and revoked replay refuse without source writes',flush=True)


def factor(rate, maturity, effective):
    days=(date.fromisoformat(maturity)-date.fromisoformat(effective)).days
    with localcontext() as arithmetic:
        arithmetic.prec=80
        return (Decimal(365)/(Decimal(365)+Decimal(rate)*days)).quantize(Decimal('1e-18'),rounding=ROUND_HALF_EVEN)


def run_checks(url, manifest, regional, query, run_worker):
    from check_currency_integrity import run_checks as check_currency_integrity
    check_currency_integrity(query)
    authorization_checks(query,manifest)
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'trader'})
    currencies = client.call('reference/catalog?table=currencies')
    codes = {row['code'] for row in currencies['rows']}
    assert codes == {'usd', 'gbp', 'eur', 'cad'}, f'Regional seed currency directory is incomplete: {sorted(codes)}'
    assert currencies['referenceRevision']['revisionId']==regional['referenceRevisionId']
    assert client.call('close-sessions/choice')['session']=='nyc_close'
    expected_days=['2020-07-15','2020-12-15','2020-12-16','2020-12-17','2020-12-18']
    frames={}
    for session,zone,winter,summer in [('nyc_close','America/New_York','21:00','20:00'),('uk_close','Europe/London','16:30','15:30')]:
        available=client.call('market/dates',dict(curve=None,timeZone=zone,session=session))
        assert [d['date'] for d in available['dates']]==expected_days,available
        assert not available['unavailable'],available
        for day in available['dates']:
            assert len(day['frames'])==1,day
            selected=day['frames'][0];frames[session,day['date']]=selected
            expected=day['date']+'T'+(summer if day['date']=='2020-07-15' else winter)+':00+00:00'
            assert datetime.fromisoformat(selected['at'])==datetime.fromisoformat(expected),selected
    print('PASS regional seed: NYC16:00 and UK16:30 native closes on five weekdays, including both DST offsets',flush=True)
    navigation=client.call('workspace/navigation')
    rates={c['code'] for c in navigation['curves'] if c['marketType']=='rates'}
    assert {'interest_rates','gbp_interest_rates','eur_interest_rates','cad_interest_rates'} <= rates,rates
    for day in ('2020-07-15','2020-12-18'):
        for currency,marks in [('gbp',('0.025','0.030','0.035')),('eur',('0.015','0.020','0.025')),('cad',('0.035','0.040','0.045'))]:
            shown=client.call('evaluate',{'snapshotId':frames['uk_close',day]['id'],'curve':currency+'_interest_rates'})
            assert shown['currency']==currency
            assert [(p['maturity'],Fraction(p['mark'])) for p in shown['points']]==list(zip(
                ['2020-12-19','2021-02-03','2022-02-03'],map(Fraction,marks)))
            assert [Fraction(p['discountFactor']) for p in shown['points']]==[
                Fraction(factor(mark,maturity,day)) for mark,maturity in zip(marks,['2020-12-19','2021-02-03','2022-02-03'])]
    print('PASS regional seed: three independent currency curves match exact simple ACT/365 discount oracles in summer and winter',flush=True)
    frame=frames['nyc_close','2020-12-18']
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    indices=client.call('swaps/catalog',context)['indices']
    source_indices={i['currencyCode']:i for i in indices if i['definitionRevisionId']==regional['definitionRevisionId']}
    assert set(source_indices)=={'gbp','eur','cad'}
    for currency,rate in [('gbp','0.030'),('eur','0.020'),('cad','0.040')]:
        index=source_indices[currency]
        assert index['defaultDiscountCurve']==currency+'_interest_rates' and index['primitiveEntrySupported']
        command=next(c for c in regional['plan']['swaps'] if c['fixed']['currency']==currency)
        status=client.call('swaps/status',{'operationId':command['operationId']})
        reopened=client.call('swaps/editor',{'context':context,'tradeId':status['tradeId'],'revision':None})
        expected=Fraction(factor(rate,'2021-02-03','2020-12-18')*100)
        assert Fraction(reopened['valuation']['presentValue'])==expected,(currency,reopened['valuation']['presentValue'],str(expected))
        assert reopened['state']['terms']['fixed']['paymentSchedule']['days']==3
        if currency=='gbp':
            from check_side_schedule_inspection import check_swap
            binding=query(f"SELECT binding_id FROM trading.swap_trade_revisions WHERE trade_id='{status['tradeId']}' AND revision=1;")
            check_swap(client,frame,dict(tradeId=status['tradeId'],bindingId=binding,revision=1))

        for day in ('2020-07-15','2020-12-18'):
            uk_context={**context,'snapshotId':frames['uk_close',day]['id']}
            uk=client.call('swaps/editor',{'context':uk_context,'tradeId':status['tradeId'],'revision':None})
            assert Fraction(uk['valuation']['presentValue'])==Fraction(factor(rate,'2021-02-03',day)*100)
        assert query(f"SELECT s.reference_revision_id FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id) JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id) WHERE r.trade_id='{status['tradeId']}';")==regional['referenceRevisionId']
    assert len(regional['usdBaseline'])==24 and regional['usdBaseline']==usd_values(client,frame['id'])
    print('PASS regional seed: three booked discounted swaps reopen with their own exact currencies; all24 USD holdings retain their values',flush=True)
    client.call('profile',{'profile':'risk'})
    scope={'context':{**context,'convention':'trading_workspace_v1'},'bookIds':[regional['plan']['book']['id']],'portfolioIds':[]}
    catalog=client.call('portfolio/risk/catalog',scope)
    assert len(catalog['factors'])==3,catalog
    request={**scope,'context':{**scope['context'],'knowledgeMode':'retained','contextId':catalog['context']['contextId']},'detail':'holdings',
        'convention':catalog['convention'],'numericalPolicy':catalog['numericalPolicy'],'pricingPolicy':catalog['pricingPolicy'],'stress':[],
        'bumps':[{'factor':item['factor'],'size':'0.1','convention':catalog['bumpConvention']} for item in catalog['factors']]}
    report=client.call('portfolio/risk',request)
    currency_keys={json.dumps(row['values']['currencyReference'],sort_keys=True) for item in report['factors'] for row in item['totals']}
    assert len(currency_keys)==3,'Independent exact currencies were merged in portfolio results'
    job=client.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':request})
    run_worker();completed=client.call('portfolio/risk/jobs/'+job['id'])
    from check_portfolio_risk import financial
    assert completed['state']=='completed' and financial(completed['result'])==financial(report)
    def counts():
        return query("SELECT json_build_array((SELECT count(*) FROM ref_data.revisions),(SELECT count(*) FROM market_data.revisions),"
            "(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM market_data.contract_date_versions),"
            "(SELECT count(*) FROM market_data.curve_definition_versions),(SELECT count(*) FROM trading.currency_discount_defaults),"
            "(SELECT count(*) FROM trading.trades),(SELECT count(*) FROM market_data.market_closes),(SELECT count(*) FROM presentation.close_session_listings));")
    client.call('close-sessions/choice',{'session':'uk_close'})
    before=counts();regional['replay']();assert counts()==before,'Repeated seed appended business facts'
    assert client.call('close-sessions/choice')['session']=='uk_close','A repeated seed changed the user\'s selected close'
    assert query("SELECT count(*) FROM security.request_contexts WHERE read_sources IS NOT NULL OR calendar_cut IS NOT NULL OR market_context_id IS NOT NULL OR business_source_context_id IS NOT NULL;")=='0'
    assert query("SELECT count(*) FROM pg_proc WHERE pronamespace='public'::regnamespace AND proname LIKE 'synthetic_library_%';")=='0'
    from seed_exact_rows import rows_sql
    existing=dict(catalog_id=regional['referenceCatalogId'],revision_id=regional['referenceRevisionId'],code='gbp',name='Conflicting synthetic terms')
    try:
        query(rows_sql('ref_data.currencies',[existing],('catalog_id','revision_id','code')))
    except RuntimeError as error:
        assert 'regional_seed_conflict' in str(error)
    else:
        raise AssertionError('A conflicting same-version source fact was accepted')
    assert counts()==before
    print('PASS regional seed: retained worker parity; full replay adds no business facts; no financial contexts or privileged public helpers retained',flush=True)
    from check_currency_graphs import run_checks as check_currency_graphs
    check_currency_graphs(url,query,manifest,regional)
