import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

test('each exercise links to the power and fuel contracts retained for its own day',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<main></main>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {readHeatRateCallView}=await import('/heat-rate-call-view.js');
  const h={tradeId:'t',bindingId:'b',instrumentType:'heat_rate_call',definitionRevisionId:'def',executedAt:'2030-02-01T12:00:00Z',booking:{}};
  const position={...h,quantity:'2',curve:'power',fuelCurve:'gas',exerciseStyle:'daily_automatic_cash_exercise_v1',model:'spread_call_kirk_lognormal_v1',unavailableReason:null};
  const exercises=['2030-02-28','2030-03-01'].map((day,i)=>({delivery_day:day,exercise_at:day+'T11:00:00Z',payment_at:day+'T16:00:00Z',energy_per_unit:'12',power_contract_id:'p'+i,fuel_contract_id:'g'+i}));
  const opened=[],details=await readHeatRateCallView(async()=>({position,exercises}),{},h,(...exact)=>opened.push(exact));
  const host=document.querySelector('main');details.view('UTC').termsContent(host);
  host.querySelector('select').value='2030-03-01';host.querySelector('select').dispatchEvent(new Event('change'));
  for(const b of host.querySelectorAll('button'))b.click();
  return opened;
 })()`);
 assert.deepEqual(result,[['power','p1','def'],['gas','g1','def']]);
}));
