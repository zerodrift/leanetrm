"""Independent retained LNG/futures portfolio and queued risk oracles."""
from fractions import Fraction
from uuid import uuid4
from preview_client import PreviewClient, initial_frame
from preview_fixture import trade_booking


def run_checks(url, receipt, *, run_worker, query, manifest):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=initial_frame(c.call('workspace'),'2020-12-18')
    context={'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
    positions=c.call('trading/positions',dict(context=context,bookIds=[],portfolioIds=[],offset=0,limit=64))
    cargo=next(p for p in positions['positions'] if p['tradeId']==receipt['tradeId'])
    assert cargo['instrumentType']=='physical_lng'
    book=cargo['bookId']
    futures_context={**context,'convention':'contract_lot_execution_pnl_v2'}
    catalog=c.call('futures/catalog',futures_context)
    contract=next(p for p in catalog['contracts'] if p['curve']=='future')
    future=c.call('futures/book',{**futures_context,**trade_booking(),'operationId':str(uuid4()),'bookId':book,'curve':contract['curve'],'contractId':contract['id'],'lots':'1','executionPrice':contract['mark']})
    view=dict(mode='trades',bookIds=[book],portfolioIds=[],groupBy=[],measures=[],pivotConvention=None)
    blotter=c.call('trading/blotter',dict(context=context,view=view))
    row=next(p for p in blotter['holdings'] if p['tradeId']==receipt['tradeId'])
    assert row['measure']=='lng_contractual_present_value' and Fraction(row['presentValue'])==49000
    assert Fraction(row['quantity'])==49000 and row['quantityPolicy']=='gross_discharge_energy_total'
    retained={**context,'knowledgeMode':'retained','contextId':blotter['context']['contextId']}
    c.call('profile',{'profile':'risk'})
    catalog=c.call('portfolio/risk/catalog',dict(context=retained,bookIds=[book],portfolioIds=[]))
    assert not any(p['tradeId']==receipt['tradeId'] for p in catalog['excludedPositions'])
    factor=next(f for f in catalog['factors'] if 'synthetic_lng_forward' in f['curves'])
    assert len({f['id'] for f in catalog['factors']})==len(catalog['factors'])
    request=dict(context=retained,bookIds=[book],portfolioIds=[],bumps=[dict(factor=factor['factor'],size='1',convention=catalog['bumpConvention'])],convention=catalog['convention'],numericalPolicy=catalog['numericalPolicy'],pricingPolicy=catalog['pricingPolicy'],stress=[])
    report=c.call('portfolio/risk',request)
    risk=next(p for p in report['factors'][0]['positions'] if p['tradeId']==receipt['tradeId'])
    assert {k:Fraction(risk['values'][k]) for k in ('base','up','down','delta','gamma')}==dict(base=49000,up=98000,down=0,delta=49000,gamma=0)
    untouched=next(p for p in report['factors'][0]['positions'] if p['tradeId']==future['tradeId'])
    assert Fraction(untouched['values']['upPnl'])==Fraction(untouched['values']['downPnl'])==0
    from check_trade_risk import selected_scope
    selected_scope(c,request,receipt['tradeId'],report,run_worker=run_worker)
    stress=c.call('portfolio/risk',{**request,'stress':[dict(factor=factor['factor'],size='2',convention=catalog['stressConvention'])]})
    assert Fraction(next(p for p in stress['factors'][0]['positions'] if p['tradeId']==receipt['tradeId'])['values']['base'])==147000
    assert c.call('portfolio/risk',request)['factors']==report['factors']
    job=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request));run_worker()
    completed=c.call('portfolio/risk/jobs/'+job['id'])
    assert completed['state']=='completed',completed
    assert completed['result']['factors']==report['factors']
    denied=c.call('portfolio/risk/jobs',dict(requestId=str(uuid4()),request=request))
    reference=manifest['referenceCatalogId']
    scopes=query(f"SELECT jsonb_agg(to_jsonb(s)) FROM security.reference_catalog_scopes s WHERE catalog_id='{reference}';")
    try:
        query(f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{reference}';")
        run_worker()
        assert query(f"SELECT state||'|'||(result IS NULL)::text FROM risk.jobs WHERE id='{denied['id']}';")=='failed|true'
    finally:
        encoded=scopes.replace("'","''")
        query(f"INSERT INTO security.reference_catalog_scopes SELECT * FROM jsonb_populate_recordset(NULL::security.reference_catalog_scopes,'{encoded}'::jsonb);")
    assert c.call('portfolio/risk/jobs/'+denied['id'])['state']=='failed'
    print('PASS retained LNG/futures: common positions and PV, independent symmetric shocks, immutable stress, selected and queued replay parity',flush=True)
    print('PASS queued LNG authority: revoked reference access fails the whole retained job without publishing an omitted-cargo result',flush=True)
