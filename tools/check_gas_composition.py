"""Independent multi-side gas booking oracles in an owned synthetic database."""
from copy import deepcopy
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url,query=None,manifest=None,run_worker=None):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
    cat=c.call('gas/catalog',context)
    def physical(code,point,direction,quantity,link):
        p=next(p for p in cat['locations'] if p['deliveryPoint']==point)
        return {'code':code,'pipeline':p['pipeline'],'zone':p['zone'],'deliveryPoint':p['deliveryPoint'],'product':p['product'],
                'direction':direction,'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','quantityBasis':'period_total_equal_days','quantity':quantity,'link':link}
    body={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':cat['books'][0]['id'],'definitionRevisionId':cat['locations'][0]['definitionRevisionId'],
          'gasSetupId':cat['gasSetupId'],'discounting':False,'profile':'fixed_gas_composition_v1',
          'physicalSides':[physical('receipt','synthetic_gas_point_a','receive','1000',None),physical('delivery','synthetic_gas_point_b','deliver',None,{'source':'receipt','formula':'gross_less_fraction','fraction':'0.02'})],
          'cashSides':[{'code':'usage','role':'fee','direction':'pay','source':'delivery','stage':'delivery','price':'0.1','tariffCode':None,'paymentAt':'2021-02-02T00:00:00Z'}]}
    value=c.call('gas/composition/preview',body)
    assert Decimal(value['presentValue'])==64
    assert value['sides'][0]['deliveryRows'][0]['meter']['identifier']=='SYN-METER-A-JAN'
    assert value['sides'][1]['deliveryRows'][0]['meter']['identifier']=='SYN-METER-B'
    assert [Decimal(s['presentValue']) for s in value['sides']]==[3200,-3038,-98]
    assert {k:value['links'][0][k] for k in ['receipt','delivery','fuel']}=={'receipt':'1000','delivery':'980','fuel':'20'},value['links']
    booked=c.call('gas/composition/book',body)
    assert c.call('gas/composition/book',body)==booked
    reopened=c.call('gas/composition/editor',{'context':context,'tradeId':booked['tradeId']})
    expected={k:value[k] for k in reopened['valuation']}
    assert reopened['valuation']==expected
    assert sum(len(s['deliveryRows']) for s in reopened['valuation']['sides'])==62
    assert reopened['terms']['physicalSides']==body['physicalSides'], (reopened['terms']['physicalSides'],body['physicalSides'])
    assert c.call('gas/composition/status',{'operationId':body['operationId']})==booked
    shared={**context,'convention':'trading_workspace_v1'}
    facts=c.call('trading/positions',{'context':shared,'bookIds':[],'portfolioIds':[],'offset':0,'limit':64})
    fact=next(p for p in facts['positions'] if p['tradeId']==booked['tradeId'])
    assert fact['instrumentType']=='physical_gas' and fact['bindingId']==booked['instrumentId']
    assert fact['bookedQuantity'] is None and fact['bookedUnit'] is None
    view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    report=c.call('trading/blotter',{'context':shared,'view':view})
    holding=next(h for h in report['holdings'] if h['tradeId']==booked['tradeId'])
    assert Decimal(holding['presentValue'])==64 and holding['quantity'] is None
    assert holding['measure']=='gas_contractual_present_value'
    retained={**shared,'knowledgeMode':'retained','contextId':report['context']['contextId']}
    selected={'context':retained,'tradeId':fact['tradeId'],'bindingId':fact['bindingId'],'revision':fact['revision'],'instrumentType':'physical_gas'}
    assert c.call('trading/position',selected)['holding']==holding
    terms=c.call('trading/gas',selected)
    assert len(terms['physical'])==2 and len(terms['components'])==3
    # The gas terms say why the trade is not amended, as the deal ticket's booked read does.
    assert terms['amend']['allowed'] is False and terms['amend']['reason'].startswith('Gas amendments'),terms['amend']
    c.call('trading/position',{**selected,'revision':2},expected=422)
    c.call('trading/position',{**selected,'bindingId':str(uuid4())},expected=422)
    print('PASS gas shares blotter, exact individual selection and retained side terms; no receipt/delivery quantity double counting',flush=True)
    changed=deepcopy(body);changed['cashSides'][0]['stage']='receipt'
    assert Decimal(c.call('gas/composition/preview',changed)['presentValue'])==62
    changed['cashSides'][0]['direction']='receive'
    assert Decimal(c.call('gas/composition/preview',changed)['presentValue'])==262
    for mutation in ['cycle','unknown','duplicate','fraction','geometry']:
        bad=deepcopy(body)
        if mutation=='cycle':bad['physicalSides'][0].update(quantity=None,link={'source':'delivery','formula':'none','fraction':'0'})
        if mutation=='unknown':bad['cashSides'][0]['source']='unknown'
        if mutation=='duplicate':bad['physicalSides'][1]['code']='receipt'
        if mutation=='fraction':bad['physicalSides'][1]['link']['fraction']='1'
        if mutation=='geometry':bad['physicalSides'][1]['deliveryFrom']='2021-01-02'
        c.call('gas/composition/preview',bad,expected=422)
    alternate=deepcopy(body);alternate['operationId']=str(uuid4());alternate['physicalSides'][1]['link']['formula']='gross_divided_by_one_plus_fraction'
    assert c.call('gas/composition/preview',alternate)['presentValue']=='3200/51'
    alternate_booked=c.call('gas/composition/book',alternate)
    assert c.call('gas/composition/editor',{'context':context,'tradeId':alternate_booked['tradeId']})['valuation']['presentValue']=='3200/51'
    independent=deepcopy(body);independent['operationId']=str(uuid4());independent['cashSides']=[]
    independent['physicalSides'][1]=physical('later','synthetic_gas_point_a','deliver','500',None)
    independent['physicalSides'][1].update(deliveryFrom='2021-02-01',deliveryTo='2021-03-01',quantityBasis='per_operating_day')
    assert Decimal(c.call('gas/composition/preview',independent)['presentValue'])==-42510
    independent_booked=c.call('gas/composition/book',independent)
    independent_loaded=c.call('gas/composition/editor',{'context':context,'tradeId':independent_booked['tradeId']})['valuation']
    assert Decimal(independent_loaded['presentValue'])==-42510 and independent_loaded['sides'][1]['quantity']=={'numerator':'14000','denominator':'1'}
    tariff=deepcopy(body);tariff['cashSides'][0].update(price=None,tariffCode=next(t for t in cat['tariffs'] if t['name']=='synthetic_gas_usage_a')['code'])
    assert Decimal(c.call('gas/composition/preview',tariff)['presentValue'])==Decimal('44.4')
    if query is not None:
        import json
        from check_booked_side_programs import runtime_query
        assert runtime_query(query,manifest,f"SELECT security.gas_component_sources_visible('{manifest['catalogId']}','{booked['instrumentId']}');",profile='viewer').splitlines()[-1]=='f'
        invalid=deepcopy(body);invalid['operationId']=str(uuid4());invalid['definitionRevisionId']=str(uuid4())
        encoded=json.dumps(invalid).replace("'","''")
        runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
          PERFORM security.book_gas_composition('{manifest['catalogId']}','{encoded}'::jsonb,'2020-12-18T16:00:00Z');
          RAISE EXCEPTION 'composition_missing_definition_accepted';
          EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'gas_definition' THEN RAISE;END IF;END;END $test$;""")
    c.call('gas/composition/book',{**body,'cashSides':[]},expected=409)
    future_context={**context,'convention':'contract_lot_execution_pnl_v2'}
    future_catalog=c.call('futures/catalog',future_context)
    contract=next(k for k in future_catalog['contracts'] if k['curve']=='future')
    future=c.call('futures/book',{**future_context,**trade_booking(),'operationId':str(uuid4()),'bookId':body['bookId'],'curve':contract['curve'],'contractId':contract['id'],'lots':'1','executionPrice':contract['mark']})
    mixed=c.call('trading/blotter',{'context':shared,'view':view})
    assert {'future','physical_gas'}<={r['instrumentType'] for r in mixed['holdings']}
    assert next(r for r in mixed['holdings'] if r['tradeId']==future['tradeId'])['executionPnl']=='0'
    if query is not None:
        catalog_id=manifest['catalogId']
        bind=f"SELECT security.use_market_context('{catalog_id}','{mixed['context']['contextId']}');SELECT security.use_trading_context('{catalog_id}','2020-12-18T16:00:00Z');"
        scope=f"SELECT security.gas_position_scope('{catalog_id}','{{}}'::uuid[],'{{}}'::uuid[]);"
        assert booked['tradeId'] in runtime_query(query,manifest,bind+scope)
        runtime_query(query,manifest,f"""DO $test$ BEGIN BEGIN
          PERFORM security.gas_position_scope('{catalog_id}','{{}}'::uuid[],'{{}}'::uuid[]);
          RAISE EXCEPTION 'unbound_gas_scope_accepted';
          EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'knowledge_coverage' THEN RAISE;END IF;END;END $test$;""")
    c.call('profile',{'profile':'risk'})
    risk=c.call('portfolio/risk/catalog',{'context':shared,'bookIds':[],'portfolioIds':[]})
    assert any(e['tradeId']==booked['tradeId'] and e['bindingId']==booked['instrumentId'] and e['reason']=='physical_gas_risk_unavailable' for e in risk['excludedPositions'])
    if run_worker is not None:
        factor=risk['factors'][0]
        request={'context':{**shared,'knowledgeMode':'retained','contextId':mixed['context']['contextId']},'bookIds':[],'portfolioIds':[],'bumps':[{'factor':factor['factor'],'size':'0.1','convention':risk['bumpConvention']}],
                 'convention':risk['convention'],'numericalPolicy':risk['numericalPolicy'],'pricingPolicy':risk['pricingPolicy'],'stress':[]}
        interactive=c.call('portfolio/risk',request)
        job=c.call('portfolio/risk/jobs',{'requestId':str(uuid4()),'request':request});run_worker()
        result=c.call('portfolio/risk/jobs/'+job['id'])
        assert result['state']=='completed',result
        assert result['result']['factors']==interactive['factors'] and result['result']['excludedPositions']==interactive['excludedPositions']
        # A worker must reject missing admitted exclusions, not repair its manifest.
        stored=json.loads(query(f"SELECT row_to_json(j) FROM risk.jobs j WHERE id='{job['id']}';"))
        altered=deepcopy(stored['frame']);altered['excludedPositions']=[]
        def literal(v):return "'"+json.dumps(v).replace("'","''")+"'::jsonb"
        raw=runtime_query(query,manifest,f"SELECT security.use_market_context('{manifest['catalogId']}','{stored['context_id']}');SELECT security.use_trading_context('{manifest['catalogId']}','{stored['valuation_at']}');"+
            f"SELECT risk.submit('{manifest['catalogId']}','{uuid4()}','{stored['context_id']}','{stored['revision_id']}','{stored['valuation_at']}',{literal(stored['request'])},{literal(altered)});",profile='risk')
        malformed=json.loads(raw.splitlines()[-1]);run_worker()
        failed=c.call('portfolio/risk/jobs/'+malformed['jobId'])
        assert failed['state']=='failed' and failed['error']['code']=='risk_contract',failed
        print('PASS mixed queued risk reproduces financial values and exact gas exclusions under the leased worker role',flush=True)
    c.call('gas/composition/book',{**body,'operationId':str(uuid4())},expected=403)
    print('PASS gas composition: independent directions, 1000/980/20 conservation, receipt/delivery fee bases, retained book/reopen, replay and invalid links',flush=True)
