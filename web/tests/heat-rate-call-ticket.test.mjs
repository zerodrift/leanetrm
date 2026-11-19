import test from 'node:test';
import assert from 'node:assert/strict';
import {tollChoices,tollCommand,keepOfferedEntry} from '../dist/heat-rate-call-ticket-model.js';

// A synthetic catalog: two power curves in different currencies, two fuel curves, and capacity quantities of two commodities.
const choice=(value,label)=>({value,label});
const catalog={
 definitionRevisionId:'definition-a',referenceCatalogId:'reference-catalog',referenceRevisionId:'reference-a',
 curves:[
  {code:'power_a',name:'Synthetic power A',commodity:'power',currency:'usd',priceUnit:'USD/MWh',quantityUnit:'MWh'},
  {code:'power_b',name:'Synthetic power B',commodity:'power',currency:'eur',priceUnit:'EUR/MWh',quantityUnit:'MWh'},
  {code:'gas_a',name:'Synthetic gas A',commodity:'gas',currency:'usd',priceUnit:'USD/MMBtu',quantityUnit:'MMBtu'},
  {code:'gas_b',name:'Synthetic gas B',commodity:'gas',currency:'eur',priceUnit:'EUR/MMBtu',quantityUnit:'MMBtu'}],
 capacityQuantities:[{code:'power_mw',commodity:'power',unit:'MW'},{code:'other_mw',commodity:'other',unit:'MW'}],
 blocks:[{id:'block-1',version:2,name:'Weekday peak',clock:'clock_a'},{id:'block-2',version:1,name:'Other peak',clock:'clock_b'}],
 clocks:['clock_a','clock_b'],calendars:['calendar_a'],intervalMinutes:[15,60],memberships:['all','peak','off'],
 paymentOptions:{frequencies:[choice('monthly','Monthly')],rules:[choice('days_after_period_end','Days after last delivery day'),choice('day_of_following_month','Day of the following month')],
  dayBases:[choice('calendar_days','Calendar days'),choice('business_days','Business days')],shortMonths:[choice('reject','Reject a missing day'),choice('clamp','Use last day of month')],
  adjustments:[choice('unadjusted','Unadjusted'),choice('following','Following')],implementation:'payment_rule_v1'},
 policies:{exerciseStyles:['style_v1'],models:['model_v1'],dayCounts:['day_count_v1'],exerciseRules:['rule_v1']},
};
const party=(id,revision)=>JSON.stringify([id,revision]);
const complete={book:'book-1',legalEntity:party('entity-1',3),counterparty:party('party-1',1),reason:'Synthetic toll',direction:'buy',capacity:'50',capacityQuantity:'power_mw',
 powerCurve:'power_a',fuelCurve:'gas_a',heatRate:'7.5',strike:'3',deliveryFrom:'2041-02-01',deliveryTo:'2041-03-01',clock:'clock_a',intervalMinutes:'60',
 membership:'peak',block:JSON.stringify(['block-1',2]),holidayCalendar:'calendar_a',exerciseStyle:'style_v1',model:'model_v1',dayCount:'day_count_v1',exerciseRule:'rule_v1',
 leadBusinessDays:'1',localCutoff:'10:00',exerciseCalendar:'calendar_a',paymentFrequency:'monthly',paymentRule:'days_after_period_end',paymentNumber:'20',
 paymentDayBasis:'calendar_days',paymentShortMonth:'',paymentAdjustment:'unadjusted',paymentCalendar:''};
const values=list=>list.map(c=>c.value);

test('nothing is chosen for the person: fuel curves and capacity units wait for the power curve',()=>{
 const shown=tollChoices(catalog,{});
 assert.deepEqual(values(shown.powerCurves),['power_a','power_b']);
 assert.deepEqual([shown.fuelCurves,shown.capacityQuantities,shown.blocks],[[],[],[]]);
 assert.deepEqual([shown.heatRateUnit,shown.strikeUnit,shown.capacityUnit],[null,null,null]);
});

test('a power curve offers only capacity of its commodity and fuel of another commodity in its currency',()=>{
 const shown=tollChoices(catalog,{powerCurve:'power_a'});
 assert.deepEqual(values(shown.fuelCurves),['gas_a']);
 assert.deepEqual(values(shown.capacityQuantities),['power_mw']);
 assert.equal(shown.strikeUnit,'USD/MWh');
 assert.equal(shown.heatRateUnit,null);
 assert.equal(tollChoices(catalog,{powerCurve:'power_a',fuelCurve:'gas_a',capacityQuantity:'power_mw'}).heatRateUnit,'MMBtu/MWh');
 assert.equal(tollChoices(catalog,{powerCurve:'power_a',capacityQuantity:'power_mw'}).capacityUnit,'MW');
});

test('a curve with no capacity quantity of its commodity is not offered as the power curve',()=>{
 assert.deepEqual(values(tollChoices(catalog,{}).powerCurves).includes('gas_a'),false);
});

test('block products are offered for the chosen clock, and only when the hours are a block',()=>{
 assert.deepEqual(values(tollChoices(catalog,{clock:'clock_a',membership:'peak'}).blocks),[JSON.stringify(['block-1',2])]);
 assert.deepEqual(tollChoices(catalog,{clock:'clock_a',membership:'all'}).blocks,[]);
 assert.deepEqual(tollChoices(catalog,{membership:'peak'}).blocks,[]);
});

test('a choice no longer offered after a change is cleared, never replaced',()=>{
 const kept=keepOfferedEntry(catalog,{...complete,powerCurve:'power_b'});
 assert.equal(kept.fuelCurve,'');assert.equal(kept.capacityQuantity,'power_mw');
 assert.equal(keepOfferedEntry(catalog,{...complete,clock:'clock_b'}).block,'');
 assert.equal(keepOfferedEntry(catalog,{...complete,membership:'all'}).block,'');
 assert.deepEqual(keepOfferedEntry(catalog,complete),complete);
});

test('a complete ticket becomes the exact booking terms, with a bought option as positive capacity',()=>{
 const command=tollCommand(catalog,complete);
 assert.deepEqual(command,{ok:true,body:{bookId:'book-1',legalEntityId:'entity-1',legalEntityRevision:3,counterpartyId:'party-1',counterpartyRevision:1,reason:'Synthetic toll',
  terms:{definitionRevisionId:'definition-a',referenceCatalogId:'reference-catalog',referenceRevisionId:'reference-a',quantity:'50',capacityQuantity:'power_mw',
   powerCurve:'power_a',fuelCurve:'gas_a',heatRate:'7.5',strike:'3',exerciseStyle:'style_v1',model:'model_v1',dayCount:'day_count_v1',
   deliveryFrom:'2041-02-01',deliveryTo:'2041-03-01',clock:'clock_a',intervalMinutes:60,
   hours:{membership:'peak',touProduct:'block-1',touVersion:2,calendar:'calendar_a'},
   exerciseRule:{implementation:'rule_v1',leadBusinessDays:1,localCutoff:'10:00',calendar:'calendar_a'},
   paymentRule:{frequency:'monthly',rule:'days_after_period_end',days:20,dayOfMonth:null,dayBasis:'calendar_days',shortMonth:null,adjustment:'unadjusted',calendar:null,implementation:'payment_rule_v1'}}}});
});

test('a sold option is negative capacity, all hours names no block, and a day-of-month rule states its own terms',()=>{
 const command=tollCommand(catalog,{...complete,direction:'sell',membership:'all',block:'',paymentRule:'day_of_following_month',paymentNumber:'25',paymentDayBasis:'',paymentShortMonth:'clamp',paymentCalendar:'calendar_a'});
 assert.equal(command.ok,true);
 assert.equal(command.body.terms.quantity,'-50');
 assert.deepEqual(command.body.terms.hours,{membership:'all',touProduct:null,touVersion:null,calendar:'calendar_a'});
 assert.deepEqual(command.body.terms.paymentRule,{frequency:'monthly',rule:'day_of_following_month',days:null,dayOfMonth:25,dayBasis:null,shortMonth:'clamp',adjustment:'unadjusted',calendar:'calendar_a',implementation:'payment_rule_v1'});
});

test('a missing value is asked for by name and nothing is sent in its place',()=>{
 const missing=(field,said)=>assert.deepEqual(tollCommand(catalog,{...complete,[field]:''}),{ok:false,reason:said});
 missing('book','Choose the book.');missing('counterparty','Choose the counterparty.');missing('direction','Choose buy or sell.');
 missing('fuelCurve','Choose the fuel curve.');missing('block','Choose the block product.');missing('exerciseStyle','Choose the exercise style.');
 missing('model','Choose the valuation model.');missing('paymentDayBasis','Choose calendar or business days for the payment.');
 missing('localCutoff','Enter the exercise cutoff.');missing('reason','Enter the reason for the booking.');
 assert.deepEqual(tollCommand(catalog,{}),{ok:false,reason:'Choose the book.'});
});

test('capacity is entered unsigned and counts are whole numbers; other numbers are the backend\'s to judge',()=>{
 const said=(change,reason)=>assert.deepEqual(tollCommand(catalog,{...complete,...change}),{ok:false,reason});
 said({capacity:'-50'},'Enter the capacity as a positive number; buy or sell gives its direction.');
 said({leadBusinessDays:'1.5'},'Enter the exercise lead as a whole number of business days.');
 said({paymentNumber:'x'},'Enter the payment days as a whole number.');
 assert.equal(tollCommand(catalog,{...complete,heatRate:'seven'}).ok,true);
});
