import test from 'node:test';
import assert from 'node:assert/strict';

const key={revision:{catalogId:'catalog',revisionId:'revision'},code:'synthetic_quantity'};
const calendar={revision:{catalogId:'catalog',revisionId:'revision'},code:'synthetic_calendar'};
const rows=[{from:'2030-01-02',to:'2030-01-03',quantitySpecification:key},{from:'2030-01-03',to:'2030-01-04',quantitySpecification:key}];

test('observation controls transport backend planned intervals and typed quantities without deriving weights',async()=>{
 const {observationScheduleInput}=await import('../dist/swap-observations.js');
 const weighted={requiresObservationCalendar:false,requiresQuantityWeights:true};
 const result=observationScheduleInput(weighted,null,rows,['2.500','7']);
 assert.deepEqual(result,{implementation:'side_observation_schedule_v1',calendar:null,quantities:[
  {from:rows[0].from,to:rows[0].to,quantity:{value:'2.500',specification:key}},
  {from:rows[1].from,to:rows[1].to,quantity:{value:'7',specification:key}},
 ]});
 result.quantities[0].quantity.specification.code='changed';
 assert.equal(key.code,'synthetic_quantity');
 assert.throws(()=>observationScheduleInput(weighted,null,null,[]),/Resolve/);
 assert.throws(()=>observationScheduleInput(weighted,null,rows,['2','']),/quantity/);
});

test('required observation calendars stay exact and old sampling pairs omit the new contract',async()=>{
 const {observationScheduleInput}=await import('../dist/swap-observations.js');
 const needsCalendar={requiresObservationCalendar:true,requiresQuantityWeights:false};
 assert.throws(()=>observationScheduleInput(needsCalendar,null,null,[]),/calendar/);
 const selected=observationScheduleInput(needsCalendar,calendar,null,[]);
 assert.deepEqual(selected,{implementation:'side_observation_schedule_v1',calendar,quantities:null});
 selected.calendar.code='changed';assert.equal(calendar.code,'synthetic_calendar');
 assert.equal(observationScheduleInput({requiresObservationCalendar:false,requiresQuantityWeights:false},null,null,[]),undefined);
});

test('each floating slot owns its observation calendar and clears only its own inapplicable choice',async()=>{
 const {setupSwapObservationControls}=await import('../dist/swap-observations-view.js');
 const previous=globalThis.document;
 const nodes=Object.fromEntries(['swap','swap-first'].flatMap(prefix=>['calendar','calendar-field','quantities','status'].map(suffix=>[`${prefix}-observation-${suffix}`,{value:'',hidden:false,required:false,disabled:false,replaceChildren(){}}])));
 globalThis.document={getElementById:id=>nodes[id]};
 try{
  let firstMode={resetFrequency:'daily',averagingMethod:'observation_arithmetic',requiresObservationCalendar:true,requiresQuantityWeights:false};
  const secondMode={...firstMode};
  const setup=(prefix,choice)=>setupSwapObservationControls({prefix,enabled:()=>true,api:()=>assert.fail('No API read expected'),request:()=>({}),choice,unit:()=>'',busy:()=>false,apply:()=>{}});
  const first=setup('swap-first',()=>firstMode),second=setup('swap',()=>secondMode);
  nodes['swap-first-observation-calendar'].value=JSON.stringify(calendar);
  nodes['swap-observation-calendar'].value=JSON.stringify({...calendar,code:'another_calendar'});
  first.sync();second.sync();
  assert.equal(first.calendar().code,calendar.code);
  assert.equal(second.calendar().code,'another_calendar');
  firstMode={resetFrequency:'daily',averagingMethod:'quantity_weighted',requiresObservationCalendar:false,requiresQuantityWeights:true};first.sync();
  assert.equal(nodes['swap-first-observation-calendar'].value,'');
  assert.equal(second.calendar().code,'another_calendar');
 }finally{globalThis.document=previous;}
});

test('an inactive floating slot cannot leave required observation controls enabled',async()=>{
 const {setupSwapObservationControls}=await import('../dist/swap-observations-view.js');
 const previous=globalThis.document;
 const nodes=Object.fromEntries(['calendar','calendar-field','quantities','status'].map(suffix=>[`swap-observation-${suffix}`,{value:'',hidden:false,required:false,disabled:false,replaceChildren(){}}]));
 globalThis.document={getElementById:id=>nodes[id]};
 try{
  const controls=setupSwapObservationControls({prefix:'swap',enabled:()=>false,api:()=>assert.fail('No API read expected'),request:()=>({}),choice:()=>({resetFrequency:'daily',averagingMethod:'observation_arithmetic',requiresObservationCalendar:true,requiresQuantityWeights:false}),unit:()=>'',busy:()=>false,apply:()=>{}});
  controls.sync();assert.equal(nodes['swap-observation-calendar'].disabled,true);assert.equal(nodes['swap-observation-calendar'].required,false);
 }finally{globalThis.document=previous;}
});
