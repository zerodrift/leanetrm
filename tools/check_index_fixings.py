"""Synthetic fixing revisions, certified cutoffs and current-authority checks."""
from preview_client import PreviewClient
from copy import deepcopy
import json
from uuid import uuid4


def run_checks(url,query):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    workspace=c.call('workspace');frame=next(f for f in workspace['frames'] if f['at'].startswith('2020-12-18'))
    catalog=c.call('swaps/catalog',{'snapshotId':frame['id'],'knowledgeMode':'current','convention':'independent_side_financial_swap_v2'})
    index=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    ref=index['quantityChoices'][0]['specification']['revision']
    quote=query(f"SELECT quote_code FROM market_data.curves WHERE catalog_id='{workspace['catalogId']}' AND revision_id='{index['definitionRevisionId']}' AND code='daily_gas';") if 'catalogId' in workspace else 'test_gas_outright'
    body={'definitionRevisionId':index['definitionRevisionId'],'index':{'revision':ref,'code':index['index']},'quote':{'revision':ref,'code':quote},'source':{'revision':ref,'code':'fixture'},
          'from':'2021-01-01','to':'2021-01-02','publishedAt':'2020-12-17T10:00:00Z','state':'final','revision':1,'correctsId':None,'value':'10','sourceEventId':'synthetic-'+str(uuid4()),'sourceRevisionId':'one'}
    c.call('profile',{'profile':'editor'})
    def capture():return c.call('date-schedules/context',{'effectiveOn':'2020-12-18','knownAt':None,'mode':'current'})['context']
    for change in ({'value':None},{'sourceEventId':None},{'sourceRevisionId':''},{'index':{'revision':ref,'code':None}},{'definitionRevisionId':str(uuid4())}):
        c.call('index-fixings/import',{**body,**change},expected=422)
    before=capture()
    first=c.call('index-fixings/import',body)
    assert c.call('index-fixings/import',body)==first
    original=capture()
    def read(context):return c.call('index-fixings/read',{'contextId':context['contextId'],'definitionRevisionId':index['definitionRevisionId'],'valuationAt':'2020-12-18T16:00:00Z'})
    assert read(before)['fixings']==[]
    assert len(read(original)['fixings'])==1 and read(original)['fixings'][0]['price']['value']=='10'
    correction={**body,'revision':2,'correctsId':first['id'],'sourceRevisionId':'two','value':'16','publishedAt':'2020-12-17T11:00:00Z'}
    second=c.call('index-fixings/import',correction)
    later=capture();old=read(original);new=read(later)
    assert [f['price']['value'] for f in old['fixings']]==['10']
    assert [(f['revision'],f['price']['value']) for f in new['fixings']]==[(1,'10'),(2,'16')]
    assert new['fixings'][1]['corrects']['id']==first['id'] and second['id']!=first['id']
    conflict=c.call('index-fixings/import',{**correction,'value':'17'},expected=409)
    bad=deepcopy(body);bad['sourceEventId']+='-invalid';bad['quote']['code']='unknown';c.call('index-fixings/import',bad,expected=422)
    print('PASS index fixing storage: exact index identities, idempotent source revisions, correction lineage and original-context replay',flush=True)
    from datetime import date,timedelta
    payment={'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    boundaries=[{'from':str(date(2021,1,1)+timedelta(days=n)),'to':str(date(2021,1,2)+timedelta(days=n)),'fixingAt':'2020-12-17T00:00:00Z' if n==0 else '2020-12-31T00:00:00Z'} for n in range(31)]
    floating={'curve':'daily_gas','index':index['index'],'currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':payment,'firstFixingAt':None,'resetFrequency':'daily','averagingMethod':'arithmetic','spread':'0',
              'fixingTerms':{'implementation':'index_actual_projected_v1','revisionPolicy':'latest_eligible_final_by_context_v1','source':'fixture','boundaries':boundaries}}
    draft={'context':{'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'retained','contextId':original['contextId']},
           'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'quantity':{'value':'31','specification':index['quantityChoices'][0]['specification']},
           'fixedCoefficient':'-1','swapType':'financial_fixed_floating','discounting':False,'fixed':{'price':'3','currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':payment},'floating':floating}
    c.call('profile',{'profile':'trader'})
    c.call('swaps/price',{**draft,'floating':{**floating,'firstFixingAt':'2020-12-16T00:00:00Z'}},expected=422)
    c.call('swaps/price',{**draft,'context':{**draft['context'],'contextId':before['contextId']}},expected=422)
    old_value=c.call('swaps/price',draft)
    new_draft={**draft,'context':{**draft['context'],'contextId':later['contextId']}}
    new_value=c.call('swaps/price',new_draft)
    assert old_value['valuation']['presentValue']=='11.95' and new_value['valuation']['presentValue']=='17.95'
    assert c.call('swaps/price',draft)['valuation']['presentValue']=='11.95'
    retained=c.call('swaps/valuation-receipt',{'id':old_value['valuationReceiptId']})
    assert retained['receipt']['result']['presentValue']=='11.95' and retained['receipt']['context_id']==original['contextId']
    assert [r['fixing_id'] for r in retained['fixingPins']]==[first['id']]
    assert retained['receipt']['result']['sides'][1]['payments'][0]['resets'][0]['fixing']['price']['value']=='10'
    assert retained['receipt']['result']['sides'][1]['payments'][0]['resets'][0]['fixing']['quote']['multiplier']=='1'
    newer=c.call('swaps/valuation-receipt',{'id':new_value['valuationReceiptId']})
    assert [r['fixing_id'] for r in newer['fixingPins']]==[first['id'],second['id']] and len(newer['marketPins'])==65
    assert c.call('swaps/valuation-receipt',{'id':old_value['valuationReceiptId']})==retained

    rows=new_value['valuation']['sides'][1]['payments'][0]['resets']
    assert rows[0]['fixingState']=='Actual' and rows[0]['fixing']['key']['id']==second['id']
    assert [r['revision'] for r in rows[0]['fixingLineage']]==[1,2] and all(r['fixingState']=='Projected' for r in rows[1:])
    risk=c.call('swaps/draft-risk',{'draft':new_draft,'bumpPolicy':new_value['bumpPolicy'],'numericalPolicy':new_value['numericalPolicy']})
    assert len(risk['factors'])==30 and all(r['delta']=='1' for r in risk['factors'])
    print('PASS mixed swap: actual 10/16 plus thirty projections, exact original-context replay, revision lineage and frozen-actual risk',flush=True)
    from preview_fixture import trade_booking
    write={k:v for k,v in draft.items() if k not in ('context','quantity')}
    write.update({'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current','operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'quantity':'31',**trade_booking()})
    preview=c.call('swaps/preview',write);book=c.call('swaps/book',write)
    assert preview['valuation']['presentValue']=='17.95' and c.call('swaps/book',write)==book
    detail=c.call('swaps/editor',{'context':{'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'},'tradeId':book['tradeId'],'revision':None})
    assert detail['state']['terms']['floating']['fixingTerms']['revisionPolicy']=='latest_eligible_final_by_context_v1' and detail['valuation']['presentValue']=='17.95' and detail['canAmend']
    amendment={**write,'operationId':str(uuid4()),'tradeId':book['tradeId'],'expectedRevision':1,'effectiveAt':frame['at'],'reason':'Synthetic notional correction','quantity':'62'}
    c.call('swaps/amend',amendment)
    current=c.call('swaps/editor',{'context':draft['context']|{'knowledgeMode':'current','contextId':None},'tradeId':book['tradeId'],'revision':None})
    assert current['valuation']['presentValue']=='35.9'
    historical=c.call('swaps/editor',{'context':draft['context']|{'knowledgeMode':'current','contextId':None},'tradeId':book['tradeId'],'revision':1})
    assert historical['valuation']['presentValue']=='17.95' and not historical['canAmend']
    changed=deepcopy(amendment);changed.update(operationId=str(uuid4()),expectedRevision=2,quantity='63');changed['floating']['fixingTerms']['boundaries'][0]['fixingAt']='2020-12-16T00:00:00Z'
    c.call('swaps/preview',changed,expected=422)
    print('PASS fixing-aware swap persistence: concrete versioned terms reopen, amend, retain prior revisions and reject due-observation reinterpretation',flush=True)
    pair={**write,'operationId':str(uuid4()),'fixed':None,'firstFloating':deepcopy(floating),'fixedCoefficient':None,'firstCoefficient':'-1','swapType':'financial_floating_floating'}
    both=c.call('swaps/book',pair)
    altered=deepcopy(floating);altered['fixingTerms']['source']='unknown'
    def sql_json(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    cat=query("SELECT catalog_id FROM trading.swap_instruments WHERE instrument_id='"+both['instrumentId']+"';")
    query("DO $review$BEGIN BEGIN PERFORM security.require_swap_fixing_amendment('"+cat+"','"+both['instrumentId']+"',"+sql_json(floating)+","+sql_json(altered)+",'2020-12-18T16:00:00Z');RAISE EXCEPTION 'test_failed_due_membership_was_changed';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'index_fixing_amendment' THEN RAISE;END IF;END;END$review$;")
    print('PASS SQL amendment fence: each occurrence of a reused side preserves its due fixing terms',flush=True)
    contradictory={**floating,'firstFixingAt':'2020-12-16T00:00:00Z'}
    query("DO $review$BEGIN BEGIN PERFORM security.retain_floating_swap_side('"+cat+"','"+index['definitionRevisionId']+"','"+ref['catalogId']+"','"+ref['revisionId']+"','"+index['commodity']+"',"+sql_json(contradictory)+",'2020-12-18T16:00:00Z',1,'2020-12-18T16:00:00Z');RAISE EXCEPTION 'test_failed_contradictory_fixing_accepted';EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'index_fixing_rule' THEN RAISE;END IF;END;END$review$;")
    print('PASS SQL fixing boundary: contradictory first-fixing and observation boundaries fail explicitly',flush=True)

    monthly=next(i for i in catalog['indices'] if i['curve']=='base_forward')
    c.call('profile',{'profile':'editor'})
    monthly_body={**body,'definitionRevisionId':monthly['definitionRevisionId'],'index':{'revision':ref,'code':monthly['index']},'to':'2021-02-01','sourceEventId':'synthetic-monthly-'+str(uuid4())}
    c.call('index-fixings/import',monthly_body)
    c.call('profile',{'profile':'trader'})
    monthly_floating={**floating,'curve':'base_forward','index':monthly['index'],'resetFrequency':'monthly','averagingMethod':'none','fixingTerms':{**floating['fixingTerms'],'boundaries':[{'from':'2021-01-01','to':'2021-02-01','fixingAt':'2020-12-17T00:00:00Z'}]}}
    monthly_draft={**draft,'context':{**draft['context'],'knowledgeMode':'current','contextId':None},'definitionRevisionId':monthly['definitionRevisionId'],'floating':monthly_floating}
    mp=c.call('swaps/price',monthly_draft)
    assert mp['valuation']['presentValue']=='217'
    mr=c.call('swaps/draft-risk',{'draft':{**monthly_draft,'context':{**monthly_draft['context'],'knowledgeMode':'retained','contextId':mp['context']['contextId']}},'bumpPolicy':mp['bumpPolicy'],'numericalPolicy':mp['numericalPolicy']})
    assert mr['factors']==[] and mr['scenarioCount']==1 and mr['basePv']=='217'
    receipt=c.call('swaps/valuation-receipt',{'id':mr['valuationReceiptId']})
    assert receipt['receipt']['result_schema']=='fixing_aware_swap_risk_v1' and receipt['fixingPins']
    print('PASS fully fixed swap risk: exact base cash with zero live commodity factors, frozen actual inputs and a retained risk receipt',flush=True)
    late_frame=next(f for f in workspace['frames'] if f['at'].startswith('2021-01-02'))
    late_before=c.call('date-schedules/context',{'effectiveOn':'2021-01-02','knownAt':None,'mode':'current'})['context']
    c.call('profile',{'profile':'editor'})
    third=c.call('index-fixings/import',{**correction,'revision':3,'correctsId':second['id'],'sourceRevisionId':'three','value':'19','publishedAt':'2021-01-02T10:00:00Z'})
    c.call('profile',{'profile':'trader'})
    late_context={'snapshotId':late_frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    late_catalog=c.call('swaps/catalog',late_context)
    retained_daily=next(i for i in late_catalog['indices'] if i['curve']=='daily_gas')
    assert any(p['deliveryFrom']=='2021-01-01' for p in retained_daily['fixingDeliveryPeriods']) and retained_daily['deliveryPeriods']==[]
    later_boundaries=[{**b,'fixingAt':b['to']+'T00:00:00Z'} for b in boundaries]
    late_floating={**floating,'fixingTerms':{**floating['fixingTerms'],'boundaries':later_boundaries}}
    late_draft={**draft,'context':late_context,'floating':late_floating}
    late_value=c.call('swaps/price',late_draft)
    assert late_value['valuation']['presentValue']=='20.95'
    old_late={**late_draft,'context':{**late_context,'knowledgeMode':'retained','contextId':late_before['contextId']}}
    assert c.call('swaps/price',old_late)['valuation']['presentValue']=='17.95'
    assert c.call('swaps/price',late_draft)['valuation']['sides'][1]['payments'][0]['resets'][0]['fixing']['key']['id']==third['id']
    late_write={**write,'operationId':str(uuid4()),'snapshotId':late_frame['id'],'floating':late_floating}
    late_book=c.call('swaps/book',late_write)
    late_detail=c.call('swaps/editor',{'context':late_context,'tradeId':late_book['tradeId'],'revision':None})
    assert late_detail['valuation']['presentValue']=='20.95' and late_detail['canAmend']
    assert late_detail['state']['terms']['floating']['fixingTerms']['boundaries'][0]['fixingAt'].startswith('2021-01-02')
    assert c.call('swaps/valuation-receipt',{'id':old_value['valuationReceiptId']})==retained
    print('PASS delivery-time fixing context: expired reset remains available as an actual, future resets project, and eligible corrections replay by exact cutoff',flush=True)
    future_floating={**floating,'fixingTerms':{**floating['fixingTerms'],'boundaries':[{**b,'fixingAt':'2020-12-31T12:00:00Z'} for b in boundaries]}}
    future_book=c.call('swaps/book',{**write,'operationId':str(uuid4()),'floating':future_floating})
    legacy_floating={k:v for k,v in floating.items() if k!='fixingTerms'};legacy_floating['firstFixingAt']='2020-12-31T12:00:00Z'
    legacy_book=c.call('swaps/book',{**write,'operationId':str(uuid4()),'floating':legacy_floating})
    assert future_book['instrumentId']!=legacy_book['instrumentId']
    legacy_detail=c.call('swaps/editor',{'context':{**draft['context'],'knowledgeMode':'current','contextId':None},'tradeId':legacy_book['tradeId'],'revision':None})
    assert legacy_detail['state']['terms']['floating'].get('fixingTerms') is None and legacy_detail['valuation']['presentValue']=='4.96'
    print('PASS economic version fence: a legacy projection-only booking never reuses a fixing-aware side with otherwise equal terms',flush=True)
    c.call('profile',{'profile':'viewer'});c.call('index-fixings/import',body,expected=403)
    c.call('swaps/valuation-receipt',{'id':old_value['valuationReceiptId']},expected=403)
    c.call('index-fixings/read',{'contextId':later['contextId'],'definitionRevisionId':index['definitionRevisionId'],'valuationAt':'2020-12-18T16:00:00Z'})
    print('PASS index fixing authority: market reads do not grant fixing import; every historical read rechecks current authority',flush=True)
    assert query("SELECT bool_and(NOT has_table_privilege('openetrm_runtime','market_data.index_fixings',p)) FROM unnest(ARRAY['INSERT','UPDATE','DELETE'])p;")=='t'
    print('PASS index fixing SQL boundary: runtime cannot directly insert or rewrite index fixings',flush=True)
