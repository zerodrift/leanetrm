"""A physical point supplies one exact published forecast; bookings retain that selection."""
from copy import deepcopy
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient,initial_frame
from preview_fixture import gas_purchase

def run_checks(url,query,manifest):
 c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'editor'})
 setup=c.call('gas-setup/catalog');original=deepcopy(setup['configuration'])
 sources=c.call('custom-curves')['sources'];source=next(s for s in sources if s['code']=='local_gas')
 graph={'code':'point_pricing_curve','name':'Configured point pricing curve','output':'output','nodes':[
  {'id':'source','kind':'source','parameters':{'curve':'local_gas'},'inputs':{}},
  {'id':'contracts','kind':'weighted_contracts','parameters':{'aggregation':'signed_sum_v1'},'inputs':{'price':'source'},'contractWeights':[{'outputPosition':n+1,'sourcePort':'price','contractId':row['id'],'weight':'2'} for n,row in enumerate(source['contracts'])]},
  {'id':'dates','kind':'dates','parameters':{'mode':'context','sampling':'context_utc_time','corrections':'exact_context_frame_history_latest_visible'},'inputs':{}},
  {'id':'sample','kind':'sample','parameters':{},'inputs':{'selection':'contracts','dates':'dates'}},
  {'id':'output','kind':'output','parameters':{},'inputs':{'values':'sample'}}]}
 frame=initial_frame(c.call('workspace'),'2020-12-18');context={'snapshotId':frame['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
 saved=c.call('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':frame['id'],'definition':graph,'layout':{}})
 c.call('profile',{'profile':'approver'});curve=c.call('custom-curves/publish',{'requestId':str(uuid4()),'id':saved['id'],'version':saved['version'],'generation':saved['generation']})
 pricing={'source':'published_custom_v1','curveId':curve['id'],'version':curve['version'],'outputNode':'output','selection':'containing_delivery_interval_v1','implementation':'published_custom_curve_projection_v1'}
 payload=deepcopy(original);point=next(p for p in payload['paths'] if p['point']=='synthetic_gas_point_a')
 group=next(g for g in payload['groups'] if g['code']==point['groupCode']);assert group['curve']=='local_gas'
 adjustment=Decimal(group['adjustment'])+Decimal(point['adjustment'])
 expected_physical=Decimal('1000')*(Decimal('2')*Decimal('3.30')+adjustment)
 expected_native=Decimal('1000')*(Decimal('3.30')+adjustment)
 point['pricing']=pricing;point['settlementPeriods']=[{'from':'2021-01-01','to':'2021-02-01','curve':'base_forward','index':'test_price','observationConvention':'monthly_published_index_v1'}]
 c.call('profile',{'profile':'editor'});command={'operationId':str(uuid4()),'baseSetupId':setup['activeSetupId'],'configuration':payload}
 draft=c.call('gas-setup/save',command);assert c.call('gas-setup/save',command)==draft
 validation=c.call('gas-setup/validate',{'setupId':draft['setupId'],'from':'2021-01-01','to':'2021-02-01'});assert validation['valid'],validation
 c.call('profile',{'profile':'approver'});c.call('gas-setup/publish',{'operationId':str(uuid4()),'setupId':draft['setupId']})
 c.call('profile',{'profile':'trader'});catalog=c.call('gas/catalog',context);body=gas_purchase(context,catalog)
 assert all('forecastCurve' not in p for p in body['physicalSides'])
 draft_terms={'product':'physical_gas','version':1,'trade':{'gas_setup':catalog['gasSetupId']},'sides':[{'kind':'physical','code':'physical','values':{'pipeline':'test_pipeline_a','zone':'synthetic_gas_zone_a','delivery_point':'synthetic_gas_point_a','product':'test_gas','delivery_from':'2021-01-01','delivery_to':'2021-02-01'}}]}
 choices=c.call('tickets/choices',{'context':context,'draft':draft_terms,'side':'physical','field':'forecast_curve'})
 assert len(choices['choices'])==1 and choices['choices'][0]['value']!='assigned',choices
 assert __import__('json').loads(choices['choices'][0]['value'])['curveId']==curve['id']
 # The fixture source is3.30. The preceding browser check may publish another
 # explicit group adjustment; retain exactly that selected setup in the oracle.
 value=c.call('gas/composition/preview',body);assert Decimal(value['sides'][0]['presentValue'])==expected_physical,value['sides'][0]['presentValue']
 booked=c.call('gas/composition/book',body);assert c.call('gas/composition/book',body)==booked
 indexed=deepcopy(body);indexed['operationId']=str(uuid4());indexed['profile']='indexed_gas_composition_v1'
 indexed['cashSides']=[{'code':'payment','role':'indexed_consideration','direction':'pay','source':'gas','stage':'delivery','price':None,'tariffCode':None,'paymentAt':None,
  'indexed':{'index':None,'observationConvention':'point_publications_v1','paymentConvention':'next_month_day_utc_v1','paymentDay':5,'spread':'0','forecastCurve':None}}]
 projected=c.call('gas/composition/preview',indexed)
 assert Decimal(projected['sides'][1]['presentValue'])==Decimal('-6600'),projected['sides'][1]
 indexed_book=c.call('gas/composition/book',indexed);assert c.call('gas/composition/book',indexed)==indexed_book
 indexed_held=c.call('gas/composition/editor',{'context':context,'tradeId':indexed_book['tradeId']})['valuation']
 assert Decimal(indexed_held['sides'][1]['presentValue'])==Decimal('-6600'),indexed_held['sides'][1]
 bad_index=deepcopy(indexed);bad_index['operationId']=str(uuid4());bad_index['cashSides'][0]['indexed']['index']='test_price'
 assert c.call('gas/composition/preview',bad_index,expected=422)['code']=='gas_point_index_terms'

 conflict=deepcopy(body);conflict['operationId']=str(uuid4());conflict['physicalSides'][0]['forecastCurve']={'catalogId':manifest['catalogId'],**{k:v for k,v in pricing.items() if k!='source'},'version':curve['version']+1,'position':None}
 assert c.call('gas/composition/preview',conflict,expected=422)['code']=='gas_location_curve_conflict'
 # Publishing a native setup changes new bookings only. The old contract keeps its exact graph.
 c.call('profile',{'profile':'editor'});native=c.call('gas-setup/save',{'operationId':str(uuid4()),'baseSetupId':draft['setupId'],'configuration':original})
 c.call('profile',{'profile':'approver'});c.call('gas-setup/publish',{'operationId':str(uuid4()),'setupId':native['setupId']})
 c.call('profile',{'profile':'trader'});held=c.call('gas/composition/editor',{'context':context,'tradeId':booked['tradeId']})['valuation'];assert Decimal(held['sides'][0]['presentValue'])==expected_physical,held['sides'][0]['presentValue']
 assert c.call('gas/composition/book',body)==booked
 assert c.call('gas/composition/book',indexed)==indexed_book
 retained_index=c.call('gas/composition/editor',{'context':context,'tradeId':indexed_book['tradeId']})['valuation']
 assert Decimal(retained_index['sides'][1]['presentValue'])==Decimal('-6600'),retained_index['sides'][1]
 fresh=gas_purchase(context,c.call('gas/catalog',context));assert Decimal(c.call('gas/composition/preview',fresh)['sides'][0]['presentValue'])==expected_native
 print('PASS point pricing: setup-derived exact graph, no trade override, contradiction refused, immutable booked pin, monthly location cash, native restoration and exact retries',flush=True)
