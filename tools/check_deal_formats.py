"""Deal/side precision must not amend financial state or personal preferences."""
from preview_client import PreviewClient

def run_checks(url):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'});w=c.call('workspace')
    frame=next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    report=c.call('trading/blotter',{'context':{**context,'convention':'trading_workspace_v1'},'view':view})
    trade=next(h for h in report['holdings'] if h['instrumentType']=='financial_swap')['tradeId']
    assert c.call('trade-formats/read',{'context':None,'tradeId':trade},expected=422)['code']=='amount_format'
    before=c.call('swaps/editor',{'context':context,'tradeId':trade,'revision':None})
    display=c.call('trade-formats/read',{'context':context,'tradeId':trade})
    assert display['scope']=='current_deal_presentation'
    freshness_request={**context,'convention':'trading_workspace_v1'}
    prior_version=c.call('trading/freshness',freshness_request)
    for role,policy in [('trade',{'money':3,'price':4,'quantity':1,'factor':6}),('fixed',{'money':4,'price':7,'quantity':3,'factor':9}),('floating',{'money':2,'price':2,'quantity':3,'factor':5})]:
        saved=c.call('trade-formats/save',{'tradeId':trade,'role':role,'expectedRevision':0,'format':policy})
        assert saved['revision']==1 and saved['format']==policy
    assert prior_version!=c.call('trading/freshness',freshness_request),'Shared deal formats invalidate retained Blotter display'
    print('PASS Blotter freshness: deal display changes invalidate retained rows without amending financial facts.',flush=True)
    after=c.call('swaps/editor',{'context':context,'tradeId':trade,'revision':None})
    assert before['state']==after['state'] and before['history']==after['history']
    assert before['valuation']['presentValue']==after['valuation']['presentValue']
    assert before['valuation']['sides']==after['valuation']['sides']
    report=c.call('trading/blotter',{'context':{**context,'convention':'trading_workspace_v1'},'view':view})
    row=next(h for h in report['holdings'] if h['tradeId']==trade)
    assert row['amountFormat']['quantity']==1 and row['fixedAmountFormat']['price']==7
    c.call('market/context',{'effectiveOn':'2020-12-18','knownAt':after['context']['knownAt'],'mode':'at','selection':'latest_correction'})
    selected_workspace=c.call('workspace')
    historical=next(f for f in selected_workspace['frames'] if f['at'].startswith('2020-12-18'))
    c.call('trade-formats/save',{'tradeId':trade,'role':'trade','expectedRevision':1,'format':row['amountFormat']},expected=422)
    selected=c.call('trade-formats/read',{'context':{**context,'snapshotId':historical['id'],'knowledgeMode':'retained','contextId':selected_workspace['context']['contextId']},'tradeId':trade})
    assert next(r for r in selected['roles'] if r['role']=='fixed')['format']['price']==7
    print('PASS deal/side precision: swap read context, independent fixed/floating formats, unchanged financial history/PV, shared blotter formatting and historical write denial.',flush=True)
