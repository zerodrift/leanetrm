import test from 'node:test';
import assert from 'node:assert/strict';
import {readCrudeView} from '../dist/crude-trade-view.js';
const h={tradeId:'synthetic-trade',bindingId:'synthetic-instrument',revision:'1',tradeReference:'Crude trade',instrumentReference:'Crude instrument',book:'Energy',executedAt:'2030-01-01T12:00:00Z',trader:'Trader',booking:{tradeDate:'2030-01-01'}};
const request={assignmentCode:'grade_delivery',direction:'receive',considerationDirection:'pay',quantity:'1000',fixedPrice:'70',handlingFee:'0.1',handlingFeeDirection:'receive',paymentAt:'2030-03-01T12:00:00Z',deliveryFrom:'2030-02-04T12:00:00Z',deliveryTo:'2030-02-05T12:00:00Z',profile:'physical_crude_standard_volume_v1',tolerance:'0'};
const detail={head:{revision:1,instrument_id:h.bindingId,request},physical:{commodity_name:'Crude',grade_name:'Grade A',facility_name:'Terminal A',custody_point_name:'Custody A',unit:'bbl',currency:'USD',multiplier:'1',standard_temperature:'60',temperature_scale:'fahrenheit',curve_name:'Crude curve'}};
test('common crude details read exact retained selection, preserve independent directions, and require no PV',async()=>{
 let sent;
 const read=await readCrudeView(async(path,body)=>{sent={path,body};return detail;},{contextId:'retained',knowledgeMode:'retained'},h);
 assert.equal(sent.path,'trading/crude');assert.equal(sent.body.context.contextId,'retained');assert.equal(sent.body.context.convention,'trading_workspace_v1');assert.equal(sent.body.bindingId,h.bindingId);
 assert.equal(read.amend.allowed,false);const view=read.view('UTC');const rows=view.terms.flatMap(t=>t.rows);
 assert.ok(rows.some(([label,value])=>label==='Physical direction'&&value==='Receive'));
 assert.ok(rows.some(([label,value])=>label==='Consideration direction'&&value==='Pay'));
 assert.ok(rows.some(([label,value])=>label==='Handling fee direction'&&value==='Receive'));
 assert.ok(rows.some(([label,value])=>label==='Contract volume'&&value==='1000 bbl'));
 assert.ok(rows.some(([label,value])=>label==='Price unit'&&value==='USD / 1 bbl'));
 assert.deepEqual(view.terms.map(s=>s.title),['Physical crude trade','Crude','Fixed price','Handling fee']);assert.equal(view.actions,undefined);
});
test('common crude details reject another retained instrument or revision',async()=>{
 for(const head of [{...detail.head,revision:2},{...detail.head,instrument_id:'other'}])await assert.rejects(readCrudeView(async()=>({...detail,head}),{},h),/changed/);
});
test('retained crude details preserve exact intraday and fractional exclusive delivery boundaries',async()=>{
 const exact={...request,deliveryFrom:'2030-02-04T12:00:01.1234567Z',deliveryTo:'2030-02-04T12:00:02.7654321Z'};
 const read=await readCrudeView(async()=>({...detail,head:{...detail.head,request:exact}}),{},h);
 const delivery=read.view('America/Chicago').terms.flatMap(t=>t.rows).find(([name])=>name==='Delivery')[1];
 assert.ok(delivery.includes('06:00:01.1234567 UTC-06:00'));assert.ok(delivery.includes('06:00:02.7654321 UTC-06:00'));assert.ok(delivery.includes('exclusive'));
});
