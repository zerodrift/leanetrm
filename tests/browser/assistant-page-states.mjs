// Explicit UI representatives, independent of the frontend labels and server admission catalog.
export function assistantPageCases(){
  const blotter={page:'futures',risk:false,trade:null,entry:null,lens:'values',view:{mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[]},pane:{details:false,charts:false},poppedOut:false};
  return [
    ['curves',{page:'market',curve:null,tab:'prices'}],
    ['power_tous',{page:'power',frame:true}],
    ['schedules',{page:'schedules'}],
    ['reference_data',{page:'reference',table:'units',search:''}],
    ['parties',{page:'parties',kind:'counterparty'}],
    ['jobs',{page:'jobs',job:null}],
    ['lifecycle',{page:'lifecycle'}],
    ['ppa',{page:'ppa',trade:null,book:null}],
    ['custom_curves',{page:'custom-curves',curve:null,unsaved:false}],
    ['blotter',blotter],
    ...[['operating_right','blotter_operating_right'],['rec_forward','blotter_rec_forward']]
      .map(([instrumentType,page])=>[page,{...blotter,trade:{tradeId:'71000000-0000-4000-8000-000000000009',instrumentType,revision:1,reference:'TR-000009'}}]),
    ['blotter_risk',{...blotter,risk:true}],
    ['blotter_risk_lens',{...blotter,lens:'risk'}],
    ...[[null,'blotter_new_trade'],['futures','blotter_new_future'],['options','blotter_new_option'],['swaps','blotter_new_swap'],['ppa','blotter_new_ppa'],['gas','blotter_new_gas'],['park-loan','blotter_new_park_loan'],['transport','blotter_new_transport'],['crude','blotter_new_crude'],['lng','blotter_new_lng'],['rec','blotter_new_rec']]
      .map(([product,page])=>[page,{...blotter,entry:{product}}]),
    ['risk_jobs',{page:'risk',view:'risk'}],
    ['physical_power',{page:'physical-power'}],
    ['facility_definitions',{page:'facilities',facilitySection:'definitions'}],
    ['facility_activity',{page:'facilities',facilitySection:'activity'}],
    ['transport_capacity',{page:'facilities',facilitySection:'transport'}],
    ['transport_route_plans',{page:'facilities',facilitySection:'route'}],
    ['rec_inventory',{page:'rec-inventory'}],
    ['options',{page:'options'}],
    ...[['physical-operations','physical_operations'],['facilities','facility_quantities'],['dashboard','dashboard'],['crude-setup','crude_setup'],['gas-setup','gas_setup'],['gas','physical_gas'],['crude','physical_crude'],['delivery','delivery_prices'],['interval-series','interval_series'],['interval-curves','interval_curves'],['vol-surfaces','vol_surfaces'],['admin','users_access'],['ai','settings_ai']]
      .map(([page,report])=>[report,{page}])
  ];
}
