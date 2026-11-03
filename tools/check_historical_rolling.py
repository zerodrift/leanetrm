"""Bounded frozen-holdings forecast/HPL oracles; this is not an operating backtest workflow."""
from copy import deepcopy
from datetime import datetime,timedelta
from fractions import Fraction


def run_checks(client,frames):
    instant=lambda value:datetime.fromisoformat(value.replace('Z','+00:00'))
    scope_context=lambda frame:{'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
    forecasts=[]
    for forecast_day,next_day in (('2020-12-16','2020-12-17'),('2020-12-17','2020-12-18')):
        frame=next(f for f in frames if f['at'].startswith(forecast_day+'T16'))
        following=next(f for f in frames if f['at'].startswith(next_day+'T16'))
        held=client.call('trading/blotter',{'context':scope_context(frame),'view':{'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}})
        trade=next(h for h in held['holdings'] if h['instrumentType']=='future')
        scope={'context':{**scope_context(frame),'knowledgeMode':'retained','contextId':held['context']['contextId']},'bookIds':[trade['bookId']],'portfolioIds':[],'tradeIds':[trade['tradeId']]}
        config=client.call('portfolio/risk/catalog',scope)
        source={'scope':scope,'pricingPolicy':config['pricingPolicy'],'policy':{**config['history']['profile'],'minimumSamples':60},'calendar':next(c['reference'] for c in config['history']['calendars'] if c['reference']['code']=='weekdays_utc'),'from':'2020-01-06','to':next_day,'closeTime':'16:00:00','ambiguousTime':'reject','invalidTime':'reject'}
        history=client.call('portfolio/risk/history',source)
        forecast=client.call('portfolio/risk/historical',{'history':history['request'],'policy':{**config['historical']['profile'],'confidence':'0.99'}})
        assert all(instant(s['to']['at'])<=instant(history['manifest']['forecast']['marketCutoff']) for s in history['manifest']['samples'])
        assert {p['tradeId'] for p in forecast['inputs']['positions']}=={trade['tradeId']}
        future_source=deepcopy(source);future_source['scope']['context']=scope_context(following);future_source['to']=(datetime.fromisoformat(next_day)+timedelta(days=1)).date().isoformat()
        next_history=client.call('portfolio/risk/history',future_source)
        sample=next(s for s in next_history['manifest']['samples'] if s['from']['at'].startswith(forecast_day) and s['to']['at'].startswith(next_day))
        assert sample['id'] not in {s['id'] for s in history['manifest']['samples']}
        shifts=[{'factor':next_history['columns'][i]['factor']['factor'],'size':move,'convention':config['stressConvention']} for i,move in enumerate(sample['changes']) if Fraction(move)]
        request={**scope,'pricingPolicy':config['pricingPolicy'],'convention':config['convention'],'numericalPolicy':config['numericalPolicy'],'detail':'holdings','bumps':[],'stress':[],
            'joint':{'convention':config['joint']['convention'],'scenarios':{'id':'next_session_hpl','version':'v1','convention':config['joint']['scenarioConvention'],'scenarios':[{'id':'next','label':'Next-session market move on forecast holdings','shifts':shifts}]},'derivatives':None}}
        outcome=client.call('portfolio/risk',request)['joint']['samples'][0]
        factor=history['columns'][0]['factor']['factor'];step=deepcopy(request);step.pop('joint');step['bumps']=[{'factor':factor,'size':'0.01','convention':config['bumpConvention']}]
        delta=sum(Fraction(t['values']['delta']) for t in client.call('portfolio/risk',step)['factors'][0]['totals'])
        realized=sum(Fraction(p['values']['loss']) for p in outcome['positions'])
        assert realized==-delta*Fraction(sample['changes'][0])
        forecasts.append((history['manifest']['id'],realized))
    assert len({h for h,_ in forecasts})==2
    print('PASS historical rolling fixtures: two forecast-cutoff histories exclude the next-session row; frozen forecast holdings HPL matches independent linear cash sensitivity',flush=True)
