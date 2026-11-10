"""Location-derived daily and monthly cash in an owned in-term fixture."""
from copy import deepcopy
from decimal import Decimal
import json
from uuid import uuid4
from preview_client import PreviewClient,initial_frame
from gas_in_term_fixture import ADMINISTRATOR,POINT,LOCATION,trade

def run_checks(url,query,manifest,admin_load):
 c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'editor'})
 frame=initial_frame(c.call('workspace'),'2026-11-06');context={'snapshotId':frame['id'],'convention':'physical_gas_fixed_quantity_v4','knowledgeMode':'current'}
 curves=c.call('custom-curves')['recipes'];curve=next(p for p in curves if p['definition']['code']=='synthetic_hub_location_mixed')
 setup=c.call('gas-setup/catalog');configuration=deepcopy(setup['configuration']);point=next(p for p in configuration['paths'] if p['point']==POINT)
 point['pricing']={'source':'published_custom_v1','curveId':curve['id'],'version':curve['version'],'outputNode':'output','selection':'containing_delivery_interval_v1','implementation':'published_custom_curve_projection_v1'}
 point['settlementPeriods']=[{'from':'2026-09-01','to':'2026-12-01','curve':LOCATION,'index':'test_price','observationConvention':'daily_published_index_v1'},
  {'from':'2026-12-01','to':'2027-01-01','curve':LOCATION,'index':'test_price','observationConvention':'monthly_published_index_v1'}]
 draft=c.call('gas-setup/save',{'operationId':str(uuid4()),'baseSetupId':setup['activeSetupId'],'configuration':configuration})
 c.call('profile',{'profile':'approver'});c.call('gas-setup/publish',{'operationId':str(uuid4()),'setupId':draft['setupId']})
 c.call('profile',{'profile':'trader'});catalog=c.call('gas/catalog',context);location=next(p for p in catalog['locations'] if p['deliveryPoint']==POINT)
 body=trade(context,location,catalog['books'],draft['setupId'],curve)
 del body['physicalSides'][0]['forecastCurve'];body['cashSides'][0]['indexed'].update(index=None,observationConvention='point_publications_v1',forecastCurve=None)
 result=admin_load({'administrator':ADMINISTRATOR[0],'composition':body});assert result.returncode==0,result.stderr
 booked=json.loads(result.stdout);repeat=admin_load({'administrator':ADMINISTRATOR[0],'composition':body});assert repeat.returncode==0 and json.loads(repeat.stdout)==booked,repeat.stderr
 value=c.call('gas/composition/editor',{'context':context,'tradeId':booked['tradeId']})['valuation']
 # Monthly December cash uses one original fixing series over31 daily cash rows.
 facts=json.loads(query(f"SELECT jsonb_build_object('rows',count(*),'days',count(DISTINCT r.payment_ordinal),'fixings',count(DISTINCT b.series_id),'from',min(b.fixing_from),'to',max(b.fixing_to)) FROM trading.side_schedule_bindings b JOIN trading.side_schedule_rows r ON(r.catalog_id,r.schedule_id,r.ordinal)=(b.catalog_id,b.schedule_id,b.row_ordinal) JOIN trading.side_schedules h ON(h.catalog_id,h.id)=(r.catalog_id,r.schedule_id) JOIN trading.sides s ON(s.catalog_id,s.id)=(h.catalog_id,h.side_id) JOIN trading.instrument_side_schedules m ON(m.catalog_id,m.schedule_id)=(h.catalog_id,h.id) WHERE m.instrument_id='{booked['instrumentId']}' AND s.family='floating' AND b.kind='index_fixing' AND b.fixing_from='2026-12-01' AND b.fixing_to='2027-01-01';"))
 assert facts=={'rows':31,'days':31,'fixings':1,'from':'2026-12-01','to':'2027-01-01'},facts
 assert value['measures'] is not None and Decimal(value['measures']['realizedUnsettled'])!=0,value['measures']
 print('PASS location in-term cash: inherited graph/index periods, original monthly fixing on31 daily rows, actual accrued cash and exact loader retry',flush=True)
