import test from 'node:test';
import assert from 'node:assert/strict';
import {storageChoices,storageCommand,keepOfferedStorage,ratchetBands} from '../dist/gas-storage-ticket-model.js';

// A synthetic catalog: two gas curves under one quote, and a power curve under another.
const curve=(code,name,quote,quantity,priceUnit,quantityUnit)=>({code,name,quote,quantity,commodity:'synthetic',currency:'usd',priceUnit,quantityUnit,contracts:[{from:'2041-02-01',to:'2041-03-01'}]});
const catalog={definitionRevisionId:'definition-a',referenceCatalogId:'reference-catalog',referenceRevisionId:'reference-a',
 curves:[curve('gas_a','Synthetic gas A','gas_quote','gas_quantity','USD/MMBtu','MMBtu'),curve('gas_b','Synthetic gas B','gas_quote','gas_quantity','USD/MMBtu','MMBtu'),
  curve('power_a','Synthetic power','power_quote','power_quantity','USD/MWh','MWh')],
 clocks:['clock_a'],maximumRatchets:3,maximumDays:1830,payment:{frequency:'monthly',rule:'days_after_period_end',dayBasis:'calendar_days',adjustment:'unadjusted',implementation:'payment_v1',maximumDays:366},
 policies:{ratchetBases:['ratchet_v1'],flowPolicies:['flow_v1'],fuelBases:['fuel_v1'],planSearches:['search_v1']}};
const fields={book:'book-1',legalEntity:JSON.stringify(['entity-1',3]),counterparty:JSON.stringify(['party-1',1]),reason:'Synthetic storage',injectionCurve:'gas_a',withdrawalCurve:'gas_b',
 minimumInventory:'0',maximumInventory:'100000',openingInventory:'29000',terminalMinimum:'0',terminalMaximum:'100000',injectionFuel:'0.01',withdrawalFuel:'0',
 ratchetBasis:'ratchet_v1',flowPolicy:'flow_v1',fuelBasis:'fuel_v1',injectionCharge:'0.02',withdrawalCharge:'0.03',monthlyCapacityCharge:'0.005',
 termFrom:'2041-02-01',termTo:'2041-03-01',clock:'clock_a',gasDayStart:'09:00',paymentDays:'20'};
const ratchets=[{upTo:'30000',injection:'2000',withdrawal:'500'},{upTo:'70000',injection:'1500',withdrawal:'1000'},{upTo:'',injection:'1000',withdrawal:'1500'}];
const complete={fields,ratchets};
const band=(lower,upper,maximumDailyInjection,maximumDailyWithdrawal)=>({lower,upper,maximumDailyInjection,maximumDailyWithdrawal});

test('nothing is chosen for the person: the withdrawal curve and units wait for the injection curve',()=>{
 const shown=storageChoices(catalog,{fields:{},ratchets:[]});
 assert.deepEqual(shown.injectionCurves.map(c=>c.value),['gas_a','gas_b','power_a']);
 assert.deepEqual([shown.withdrawalCurves,shown.unit,shown.priceUnit],[[],null,null]);
});

test('the withdrawal curve is one priced under the injection curve\'s quote, and a choice no longer offered is cleared',()=>{
 const shown=storageChoices(catalog,{fields:{injectionCurve:'gas_a'},ratchets:[]});
 assert.deepEqual(shown.withdrawalCurves.map(c=>c.value),['gas_a','gas_b']);
 assert.deepEqual([shown.unit,shown.priceUnit],['MMBtu','USD/MMBtu']);
 assert.equal(keepOfferedStorage(catalog,{fields:{...fields,injectionCurve:'power_a'},ratchets}).fields.withdrawalCurve,'');
 assert.equal(keepOfferedStorage(catalog,complete),complete);
});

test('ratchet rows become bands that cover the range: each starts where the last ended and the last reaches capacity',()=>{
 assert.deepEqual(ratchetBands(complete),[band('0','30000','2000','500'),band('30000','70000','1500','1000'),band('70000','100000','1000','1500')]);
 assert.deepEqual(ratchetBands({fields:{minimumInventory:'500',maximumInventory:'9000'},ratchets:[{upTo:'ignored',injection:'10',withdrawal:'20'}]}),[band('500','9000','10','20')]);
});

test('a complete ticket becomes the exact booking terms with every physical parameter as entered',()=>{
 assert.deepEqual(storageCommand(catalog,complete),{ok:true,body:{bookId:'book-1',legalEntityId:'entity-1',legalEntityRevision:3,counterpartyId:'party-1',counterpartyRevision:1,reason:'Synthetic storage',
  terms:{definitionRevisionId:'definition-a',referenceCatalogId:'reference-catalog',referenceRevisionId:'reference-a',quantity:'gas_quantity',injectionCurve:'gas_a',withdrawalCurve:'gas_b',
   minimumInventory:'0',maximumInventory:'100000',ratchets:[band('0','30000','2000','500'),band('30000','70000','1500','1000'),band('70000','100000','1000','1500')],
   injectionFuel:'0.01',withdrawalFuel:'0',ratchetBasis:'ratchet_v1',flowPolicy:'flow_v1',fuelBasis:'fuel_v1',openingInventory:'29000',terminalMinimum:'0',terminalMaximum:'100000',
   injectionCharge:'0.02',withdrawalCharge:'0.03',monthlyCapacityCharge:'0.005',termFrom:'2041-02-01',termTo:'2041-03-01',clock:'clock_a',dayStartMinute:540,
   paymentRule:{frequency:'monthly',rule:'days_after_period_end',days:20,dayOfMonth:null,dayBasis:'calendar_days',shortMonth:null,adjustment:'unadjusted',calendar:null,implementation:'payment_v1'}}}});
});

test('a missing value is asked for by name, zero is never assumed, and nothing is sent in its place',()=>{
 const missing=(field,said)=>assert.deepEqual(storageCommand(catalog,{fields:{...fields,[field]:''},ratchets}),{ok:false,reason:said});
 missing('injectionCurve','Choose the injection curve.');missing('maximumInventory','Enter the contracted capacity.');missing('openingInventory','Enter the opening inventory.');
 missing('withdrawalFuel','Enter the withdrawal fuel fraction; zero is stated, not assumed.');missing('monthlyCapacityCharge','Enter the monthly capacity charge; zero is stated, not assumed.');
 missing('ratchetBasis','Choose how ratchets apply.');missing('gasDayStart','Enter when the gas day starts.');missing('reason','Enter the reason for the booking.');
 const rows=change=>storageCommand(catalog,{fields,ratchets:change(structuredClone(ratchets))});
 assert.deepEqual(rows(()=>[]),{ok:false,reason:'Add at least one ratchet band.'});
 assert.deepEqual(rows(r=>[...r,r[0]]),{ok:false,reason:'A contract takes at most 3 ratchet bands.'});
 assert.deepEqual(rows(r=>{r[0].upTo='';return r;}),{ok:false,reason:'Enter the inventory each ratchet band reaches up to.'});
 assert.deepEqual(rows(r=>{r[2].withdrawal='';return r;}),{ok:false,reason:'Enter each ratchet band\'s daily injection and withdrawal limits.'});
 assert.deepEqual(storageCommand(catalog,{fields:{...fields,gasDayStart:'9am'},ratchets}),{ok:false,reason:'Enter when the gas day starts as hours and minutes.'});
 assert.deepEqual(storageCommand(catalog,{fields:{...fields,paymentDays:'1.5'},ratchets}),{ok:false,reason:'Enter the payment days as a whole number.'});
 assert.equal(storageCommand(catalog,{fields:{...fields,maximumInventory:'a lot'},ratchets}).ok,true);
});
