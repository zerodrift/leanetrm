import test from 'node:test';
import assert from 'node:assert/strict';
import {noOptionPick,optionChoices,pickOption,keepOffered,lifecycleWrites} from '../dist/futures-option-model.js';

// A synthetic catalog: two exchanges, two commodities on the first, the series in the backend's order.
const named=(code,name)=>({code,name});
const series=(code,exchange,commodity,underlyingSeries,right,strike)=>({series:code,product:`${exchange.code}_product`,name:`Synthetic ${commodity.name} option`,exchange,commodity,
 underlyingSeries,right,strike,expiryAt:'2041-01-15T19:30:00Z',premiumStyle:'premium_paid_up_front',contraryInstructionsAllowed:false,multiplier:'1000',currency:'USD',
 premiumTick:'0.01',minimumPremium:'0',maximumPremium:'1000',settlementPrice:'0.5',exerciseStyle:'american'});
const alpha=named('exchange_a','Exchange A'),beta=named('exchange_b','Exchange B'),gas=named('gas','Synthetic gas'),oil=named('oil','Synthetic oil');
const catalog={
 options:[
  series('a_gas_feb_c10','',gas,'gas_feb','call','10'),
  series('a_gas_feb_c9.5','',gas,'gas_feb','call','9.5'),
  series('a_gas_feb_p10','',gas,'gas_feb','put','10'),
  series('a_gas_mar_c10','',gas,'gas_mar','call','10'),
  series('a_oil_feb_c50','',oil,'oil_feb','call','50'),
  series('b_gas_feb_c10','',gas,'b_gas_feb','call','10'),
 ].map((s,i)=>({...s,exchange:i===5?beta:alpha})),
 underlyings:[{series:'gas_feb',deliveryFrom:'2041-02-01',deliveryTo:'2041-03-01'},{series:'gas_mar',deliveryFrom:'2041-03-01',deliveryTo:'2041-04-01'},
  {series:'oil_feb',deliveryFrom:'2041-02-01',deliveryTo:'2041-03-01'},{series:'b_gas_feb',deliveryFrom:'2041-02-01',deliveryTo:'2041-03-01'}],
};
const values=list=>list.map(c=>c.value);
const pick=(...steps)=>steps.reduce((p,[level,value])=>pickOption(catalog,p,level,value),noOptionPick);

test('nothing is chosen for the person: only exchanges are offered until one is picked, and no series is selected',()=>{
 const shown=optionChoices(catalog,noOptionPick);
 assert.deepEqual(shown.exchange,[{value:'exchange_a',label:'Exchange A'},{value:'exchange_b',label:'Exchange B'}]);
 assert.deepEqual([shown.commodity,shown.underlying,shown.right,shown.strike],[[],[],[],[]]);
 assert.equal(shown.series,null);
});

test('an exchange with one commodity still waits for the person to pick it',()=>{
 const shown=optionChoices(catalog,pick(['exchange','exchange_b']));
 assert.deepEqual(shown.commodity,[{value:'gas',label:'Synthetic gas'}]);
 assert.deepEqual(shown.underlying,[]);
});

test('each list offers only what the selections above it list in the catalog: in the catalog\'s order, strikes ascending',()=>{
 const shown=optionChoices(catalog,pick(['exchange','exchange_a'],['commodity','gas'],['underlying','gas_feb'],['right','call']));
 assert.deepEqual(values(shown.commodity),['gas','oil']);
 assert.deepEqual(shown.underlying,[{value:'gas_feb',label:'Feb 2041 · gas_feb'},{value:'gas_mar',label:'Mar 2041 · gas_mar'}]);
 assert.deepEqual(shown.right,[{value:'call',label:'Call'},{value:'put',label:'Put'}]);
 assert.deepEqual(values(shown.strike),['9.5','10']);
 assert.equal(shown.series,null);
});

test('a full pick names exactly the one series of the catalog it was picked from',()=>{
 const shown=optionChoices(catalog,pick(['exchange','exchange_a'],['commodity','gas'],['underlying','gas_feb'],['right','put'],['strike','10']));
 assert.deepEqual(values(shown.strike),['10']);
 assert.equal(shown.series.series,'a_gas_feb_p10');
});

test('changing a selection clears every selection below it, never re-picking one',()=>{
 const full=pick(['exchange','exchange_a'],['commodity','gas'],['underlying','gas_feb'],['right','call'],['strike','10']);
 assert.deepEqual(pickOption(catalog,full,'underlying','gas_mar'),{exchange:'exchange_a',commodity:'gas',underlying:'gas_mar',right:'',strike:''});
 assert.deepEqual(pickOption(catalog,full,'exchange','exchange_b'),{exchange:'exchange_b',commodity:'',underlying:'',right:'',strike:''});
 assert.deepEqual(pickOption(catalog,full,'right',''),{exchange:'exchange_a',commodity:'gas',underlying:'gas_feb',right:'',strike:''});
});

test('a value the list does not offer is refused, as is a pick below an empty selection',()=>{
 assert.throws(()=>pick(['exchange','exchange_c']),/not offered/);
 assert.throws(()=>pick(['exchange','exchange_b'],['commodity','oil']),/not offered/);
 assert.throws(()=>pick(['exchange','exchange_a'],['underlying','gas_feb']),/not offered/);
});

test('a catalog read again keeps the selections it still offers and clears from the first it no longer does',()=>{
 const full=pick(['exchange','exchange_a'],['commodity','gas'],['underlying','gas_feb'],['right','call'],['strike','9.5']);
 assert.deepEqual(keepOffered(catalog,full),full);
 const fewer={...catalog,options:catalog.options.filter(s=>s.series!=='a_gas_feb_c9.5')};
 assert.deepEqual(keepOffered(fewer,full),{...full,strike:''});
 const later={...catalog,options:catalog.options.filter(s=>s.underlyingSeries!=='gas_feb')};
 assert.deepEqual(keepOffered(later,full),{exchange:'exchange_a',commodity:'gas',underlying:'',right:'',strike:''});
});

test('a catalog that lists two series for one pick, or a series whose underlying it does not describe, is refused rather than guessed',()=>{
 const twice={...catalog,options:[...catalog.options,{...catalog.options[0],series:'a_gas_feb_c10_again'}]};
 assert.throws(()=>optionChoices(twice,pick(['exchange','exchange_a'],['commodity','gas'],['underlying','gas_feb'],['right','call'],['strike','10'])),/more than one series/);
 const undescribed={...catalog,underlyings:catalog.underlyings.slice(1)};
 assert.throws(()=>optionChoices(undescribed,pick(['exchange','exchange_a'],['commodity','gas'])),/does not describe/);
});

test('lifecycle events are offered only from working data at current knowledge, to a person who may book, and not again once one is recorded',()=>{
 assert.deepEqual(lifecycleWrites({view:'working',canBook:true,reason:null,contextDirty:false,recorded:false}),{enabled:true,reason:null});
 for(const view of ['historical','live','read_only'])
  assert.deepEqual(lifecycleWrites({view,canBook:true,reason:null,contextDirty:false,recorded:false}),{enabled:false,reason:'Lifecycle events are recorded from working data at current knowledge. Return to working data to record one.'});
 assert.deepEqual(lifecycleWrites({view:'working',canBook:false,reason:'Your current role does not have Book Trade permission.',contextDirty:false,recorded:false}),{enabled:false,reason:'Your current role does not have Book Trade permission.'});
 assert.equal(lifecycleWrites({view:'working',canBook:false,reason:null,contextDirty:false,recorded:false}).reason,'Booking is not available in this view, and the backend did not say why.');
 assert.deepEqual(lifecycleWrites({view:'working',canBook:true,reason:null,contextDirty:false,recorded:true}),{enabled:false,reason:'An event was recorded here. Open the trade again from the Blotter to read it at current knowledge.'});
});

test('lifecycle events are not offered while a changed historical context is unapplied, as booking is not',()=>{
 assert.deepEqual(lifecycleWrites({view:'working',canBook:true,reason:null,contextDirty:true,recorded:false}),{enabled:false,reason:'Apply the changed historical context first.'});
});
