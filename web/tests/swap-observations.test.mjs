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
