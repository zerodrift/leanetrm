"""Independent behavior oracles for an owned exchange-futures example."""
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient

def run_checks(url,query=None,manifest=None):
    base=PreviewClient(url);base.bootstrap();base.call('profile',{'profile':'trader'})
    original=base.call('workspace');baseline_frame=original['frames'][-1]
    c=PreviewClient(url,'exchange');c.opener=base.opener
    boot=c.bootstrap();c.call('profile',{'profile':'trader'});w=c.call('workspace')
    assert boot['workspaceId']!=base.call('bootstrap')['workspaceId']
    assert boot['catalogId']!=base.call('bootstrap')['catalogId']
    frame=next(f for f in w['frames'] if f['at'].startswith('2026-10-16'))
    context={'snapshotId':frame['id'],'convention':w['futuresConvention'],'knowledgeMode':'current'}
    # Same browser cookies cannot make a token or input frame cross examples.
    c.call('profile',{'profile':'trader'},expected=400,headers={'X-Preview-CSRF':base.token})
    c.call('futures/catalog',{**context,'snapshotId':baseline_frame['id']},expected=422)
    base.call('futures/catalog',{'snapshotId':frame['id'],'convention':'fixed_total_cash_execution_pnl_v1','knowledgeMode':'current'},expected=422)
    catalog=c.call('futures/catalog',context);contracts=catalog['contracts']
    assert len(contracts)==61
    expected_sizes={'ice_dgd':'2500','ice_hxs':'2500','ice_wah':'2500','nymex_ng':'10000','ice_pmi':'320','ice_pjc':'320','ice_ern':'320','ice_spm':'384','ice_npm':'384',
                    'ice_cfb':'2500','ice_fqh':'2500','ice_fqw':'2500'}
    assert len({v['curve'] for v in contracts})==len(expected_sizes)
    assert all(v['firstTrading'] is None for v in contracts), 'Unknown source facts must not contain invented first-trading times'
    assert not any(v['curve'].startswith('test_') for v in contracts), 'Test controls do not belong to the exchange catalog'
    assert not any(v['curve'] in {'ice_css','ice_ucs','ice_was','ice_pda','ice_dpn','ice_sdp','ice_end'} for v in contracts), 'Daily listing dates require an actual exchange contract list'
    nymex=next(v for v in contracts if v['curve']=='nymex_ng')
    assert nymex['settlementMethod']=='physical' and nymex['tick']=='0.001'
    for curve,totals in [('ice_spm',['384','400','384','432','416']),('ice_pmi',['320','320','320','368','352'])]:
        selected=sorted((v for v in contracts if v['curve']==curve),key=lambda v:v['deliveryFrom'])
        assert [v['contractSize'] for v in selected]==totals
        assert all(v['nominalSize']=='1' and v['nominalUnit']=='MW' and v['unit']=='MWh' for v in selected)
    for curve in ('ice_dgd','ice_hxs','ice_wah'):
        selected=next(v for v in contracts if v['curve']==curve)
        assert selected['quoteKind']=='basis' and selected['basisSettlement']['implementation']=='index_minus_future_v1'
        assert selected['basisSettlement']['benchmarkSeries']['code']=='nymex_ng_20261101'
        assert Decimal(selected['mark'])<0
    # Direct quoted basis is itself the live factor: its benchmark is settlement metadata.
    from fractions import Fraction as F
    basis=next(v for v in contracts if v['curve']=='ice_dgd')
    draft={'context':context,'curve':basis['curve'],'contractId':basis['id'],'lots':'-2','executionPrice':basis['mark']}
    price=c.call('futures/price',draft);assert price['executionPnl']=='0' and price['unbooked']
    risk=c.call('futures/draft-risk',{'draft':{**draft,'context':{**context,'knowledgeMode':'retained','contextId':price['context']['contextId']}},'bumpPolicy':price['bumpPolicy'],'numericalPolicy':price['numericalPolicy']})
    assert len(risk['factors'])==1 and risk['factors'][0]['factor']['curves']==['ice_dgd']
    assert F(risk['factors'][0]['delta'])==-2*F(basis['contractSize'])*F(basis['quoteMultiplier'])
    assert F(risk['factors'][0]['gamma'])==0
    assert not c.call('futures/holdings',context)['holdings']
    print('PASS temporary basis futures: signed lot exposure belongs only to its quoted observable, no benchmark exposure or booking',flush=True)
    expected={}
    for n,(curve,size) in enumerate(expected_sizes.items()):
        contract=next(v for v in contracts if v['curve']==curve)
        assert contract['contractSize']==size
        lots=Decimal('-2' if n%2 else '2');tick=Decimal(contract['tick']);execution=Decimal(contract['mark'])-tick
        body={'operationId':str(uuid4()),**context,'bookId':catalog['books'][n%2]['id'],'curve':curve,'contractId':contract['id'],
              'lots':str(lots),'executionPrice':str(execution),'legalEntityId':catalog['legalEntities'][0]['id'],'legalEntityRevision':catalog['legalEntities'][0]['revision'],
              'counterpartyId':catalog['counterparties'][0]['id'],'counterpartyRevision':catalog['counterparties'][0]['revision']}
        saved=c.call('futures/book',body);assert saved==c.call('futures/book',body)
        expected[saved['tradeId']]={'quantity':lots*Decimal(size),'pnl':lots*Decimal(size)*tick,'body':body,'contract':contract}
    holdings=c.call('futures/holdings',context)['holdings'];assert len(holdings)==12
    for h in holdings:
        oracle=expected[h['tradeId']]
        assert Decimal(h['quantity'])==oracle['quantity'],h
        assert Decimal(h['executionPnl'])==oracle['pnl'],h
        assert h['settlement']=='unprocessed'
    print('PASS exchange futures: 61 executable source-listed monthly maturities,12 exchange product families, physical/cash, source-listed monthly maturities, signed lots and exact published power lot totals.',flush=True)
    future=holdings[0];trade=future['tradeId']
    formats=c.call('trade-formats/read',{'context':context,'tradeId':trade})
    selected=next(r for r in formats['roles'] if r['role']=='trade');assert selected['revision']==0
    policy={'money':3,'price':7,'quantity':1,'factor':8}
    request={'tradeId':trade,'role':'trade','expectedRevision':0,'format':policy}
    assert c.call('trade-formats/save',request)['revision']==1
    c.call('trade-formats/save',request,expected=409)
    c.call('trade-formats/save',{**request,'role':'fixed'},expected=422)
    c.call('trade-formats/save',{**request,'format':{**policy,'money':19}},expected=422)
    again=c.call('trade-formats/read',{'context':context,'tradeId':trade});assert next(r for r in again['roles'] if r['role']=='trade')['format']==policy
    blotter=c.call('trading/blotter',{'context':{**context,'convention':'trading_workspace_v1'},'view':{'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}})
    assert next(h for h in blotter['holdings'] if h['tradeId']==trade)['amountFormat']==policy
    assert all(h['amountFormat']!=policy for h in blotter['holdings'] if h['tradeId']!=trade)
    c.call('profile',{'profile':'risk'});c.call('trade-formats/save',{**request,'expectedRevision':1},expected=403)
    print('PASS deal precision: per-deal persistence, independent formats, expected-revision conflicts, role/unit bounds and read-only authority.',flush=True)
    power_catalog=c.call('power/catalog',{'snapshotId':frame['id']})
    # Contract identities here are name-based, so their order says nothing about delivery dates.
    deliveries=[[k['from'] for k in v['contracts']] for v in power_catalog['curves']]
    assert any(len(d)>1 for d in deliveries) and all(d==sorted(d) for d in deliveries)
    print('PASS power catalog: each curve lists its contracts in delivery order.',flush=True)
    peak=next(p for p in power_catalog['presets'] if p['product']=='pjm' and p['block']=='peak' and p['minutes']==60)
    energy=next(q for q in power_catalog['quantityChoices'] if q['basis']=='total_energy')
    power_contract=next(v for v in contracts if v['curve']=='ice_pjc')
    example=c.call('power/preview',{'snapshotId':frame['id'],'curve':'ice_pjc','contractId':power_contract['id'],'product':'pjm','productVersion':1,'block':'peak','minutes':60,'hourLabels':'HE',
        'quantity':{'value':'1','specification':energy['specification']},'quantityBasis':'total_energy','allocation':'flat_rate',
        'display':power_catalog['display'],'priceType':'Synthetic','timing':'Illustrative flat futures price',
        'nonHolidays':peak['nonHolidays'],'holidays':peak['holidays'],'numericalPolicy':power_catalog['numericalPolicy']})
    assert example['summary']['hours']=='320' and example['summary']['energy']=='1' and example['holidays']==['2026-11-26'] and example['holidayCalendar']=='ept_published_peak'
    assert Decimal(example['summary']['amount'])==Decimal(power_contract['mark']).quantize(Decimal('0.01'))
    print('PASS power example: exact indexed futures reference feeds a typed one-MWh peak allocation without inferring official hourly prices.',flush=True)
    risk_catalog=c.call('futures/risk/catalog',{'context': context, 'bookIds': [], 'portfolioIds': []})
    held={v['contract']['id']:v for v in expected.values()}
    factors=[v for v in risk_catalog['factors'] if v['contractId'] in held]
    bumps=[{'seriesId':v['seriesId'],'quote':v['quote'],'size':'0.01','convention':risk_catalog['bumpConvention']} for v in factors]
    checked=set()
    for start in range(0,len(bumps),16):
        risk=c.call('futures/risk',{'context':context,'bookIds':[],'portfolioIds':[],'bumps':bumps[start:start+16],'convention':risk_catalog['convention'],'numericalPolicy':risk_catalog['numericalPolicy']})
        for result in risk['factors']:
            cid=result['factor']['contractId'];oracle=held[cid];checked.add(cid)
            moved=[p for p in result['positions'] if p['contractId']==cid]
            assert len(moved)==1
            assert Decimal(moved[0]['values']['upPnl'])==oracle['quantity']*Decimal('0.01')
            assert Decimal(moved[0]['values']['downPnl'])==-oracle['quantity']*Decimal('0.01')
            assert all(Decimal(p['values']['upPnl'])==0 for p in result['positions'] if p['contractId']!=cid)
    assert len(checked)==12
    c.call('profile',{'profile':'trader'})
    oracle=next(v for v in expected.values() if v['body']['curve']=='ice_wah')
    initial_id=next(key for key,value in expected.items() if value is oracle)
    initial=c.call('futures/editor',{'context':context,'tradeId':initial_id,'revision':None})
    changed={**oracle['body'],'operationId':str(uuid4()),'tradeId':initial_id,'expectedRevision':1,'lots':'3','effectiveAt':frame['at'],'reason':'Synthetic lot correction','amendmentConvention':initial['amendmentConvention']}
    c.call('futures/amend-preview',changed)
    amended=c.call('futures/amend',changed);assert amended['revision']==2
    current=c.call('futures/editor',{'context':context,'tradeId':initial_id,'revision':None})
    assert current['state']['instrumentId']==initial['state']['instrumentId'] and current['state']['lots']=='3'
    reused=c.call('futures/book',{**oracle['body'],'operationId':str(uuid4()),'lots':'-1'})
    assert reused['instrumentId']==initial['state']['instrumentId']
    assert c.call('futures/book',{**oracle['body'],'operationId':str(uuid4()),'lots':'0.5'},expected=422)['code']=='futures_lots'
    assert base.call('workspace')['frames']==original['frames']
    print('PASS exchange shared scenarios: lot normalization scales each physical/cash position once; baseline session and input pins remain independent.',flush=True)
    # Identical local logins in separate verified provider namespaces resolve to
    # different principals. Revoking one cannot revoke the other workspace.
    base.call('profile',{'profile':'admin'});c.call('profile',{'profile':'admin'})
    login='namespace_example'
    body={'operationId':str(uuid4()),'login':login,'name':'Synthetic namespace account','kind':'human','roles':['viewer']}
    first=base.call('admin/create',body)
    second=c.call('admin/create',{**body,'operationId':str(uuid4())})
    assert first['id']!=second['id']
    base.call('profile',{'profile':login});assert base.call('workspace')['definitions']['curves']
    c.call('profile',{'profile':login});assert c.call('workspace')['definitions']['curves']
    c.call('profile',{'profile':'admin'})
    c.call('admin/update',{'operationId':str(uuid4()),'id':second['id'],'expectedVersion':second['version'],'name':second['name'],'enabled':False,'roles':['viewer']})
    c.call('profile',{'profile':login});c.call('workspace',expected=403)
    assert base.call('workspace')['definitions']['curves']
    c.call('profile',{'profile':'admin'})
    c.call('admin/update',{'operationId':str(uuid4()),'id':first['id'],'expectedVersion':first['version'],'name':first['name'],'enabled':False,'roles':['viewer']},expected=409)
    print('PASS namespace administration: same-login accounts are independent, newly created users sign in, revocation is scoped and cross-workspace updates reject.',flush=True)
    # Each example workspace keeps its own page-assistant providers and audit.
    base.call('profile',{'profile':'admin'})
    stored=base.call('settings/ai/providers/key',{'operationId':str(uuid4()),'vendor':'anthropic','expectedVersion':0,'key':'sk-synthetic-example-'+uuid4().hex})
    exchange=c.call('settings/ai/providers')
    assert stored['credential']['version']==1 and exchange['audit']==[] and all(p['credential'] is None and p['version']==0 for p in exchange['providers'])
    print('PASS each example workspace administers its own model providers.',flush=True)
    if query is not None:
        import json
        assert query(f"SELECT count(*) FROM ref_data.futures_products WHERE catalog_id='{manifest['referenceCatalogId']}' AND definition_state='synthetic';")=='0'
        assert query(f"SELECT count(*) FROM ref_data.futures_products WHERE catalog_id='{manifest['referenceCatalogId']}';")=='19'
        assert query(f"SELECT count(*) FROM ref_data.futures_series WHERE catalog_id='{manifest['referenceCatalogId']}';")=='72'
        assert query(f"SELECT count(*) FROM ref_data.futures_milestones WHERE catalog_id='{manifest['referenceCatalogId']}' AND milestone='last_trading' AND value_state='known' AND event_at IS NULL;")=='11'
        assert query(f"SELECT count(*) FROM ref_data.exchanges WHERE catalog_id='{manifest['referenceCatalogId']}';")=='2'
        assert query("SELECT bool_and(relrowsecurity AND relforcerowsecurity) FROM pg_class WHERE oid IN('ref_data.futures_product_terms'::regclass,'ref_data.futures_basis_settlements'::regclass,'ref_data.futures_lot_resolutions'::regclass);")=='t'
        assert query("SELECT NOT has_table_privilege('preview_runtime','security.request_contexts','UPDATE');")=='t'
        oracle=next(v for v in expected.values() if v['body']['curve']=='ice_wah');body=oracle['body']
        booking=json.dumps({key:body[key] for key in ('legalEntityId','legalEntityRevision','counterpartyId','counterpartyRevision')}).replace("'","''")
        for changed in ('incompatible','missing','duplicate_index'):
            token=query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                f"'https://exchange-preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
            change=(f"UPDATE ref_data.futures_product_terms SET profile_code='nymex_ng_profile' WHERE product_code='ice_wah' AND catalog_id='{manifest['referenceCatalogId']}';" if changed=='incompatible' else f"DELETE FROM ref_data.futures_product_terms WHERE product_code='ice_wah' AND catalog_id='{manifest['referenceCatalogId']}';")
            if changed=='duplicate_index':change=f"UPDATE ref_data.futures_product_terms SET index_code='waha_bidweek' WHERE product_code='ice_wah' AND catalog_id='{manifest['referenceCatalogId']}';"
            query(f"""BEGIN;ALTER TABLE ref_data.futures_product_terms DISABLE TRIGGER frozen_definition;
              {change}
              ALTER TABLE ref_data.futures_product_terms ENABLE TRIGGER frozen_definition;
              SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
              DO $$ BEGIN
                PERFORM security.book_futures_with_parties('{manifest['catalogId']}','{uuid4()}','{body['bookId']}','{manifest['revisionId']}',
                  'ice_wah','{body['contractId']}','{frame['at']}',NULL,1,{body['executionPrice']},'contract_lot_execution_pnl_v2',NULL,NULL,NULL,'{booking}'::jsonb);
                RAISE EXCEPTION 'Expected canonical-profile rejection';
              EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'futures_definition' THEN RAISE;END IF;END $$;ROLLBACK;""")
        print('PASS SQL boundary: missing/incompatible canonical profiles reject through raw authorized booking; exact reference tables force RLS and runtime cannot rewrite verified identity.',flush=True)
