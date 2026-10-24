import test from 'node:test';
import assert from 'node:assert/strict';
import {linkedValue,availableSwapIndices} from '../dist/swap-selection.js';

test('linked choices keep a valid selection and choose only an unambiguous replacement',()=>{
 assert.equal(linkedValue(['gas'],'gas'),'gas');
 assert.equal(linkedValue(['power'],'gas'),'power');
 assert.equal(linkedValue(['north','south'],'old'),'');
 assert.equal(linkedValue(['north','south'],'south'),'south');
 assert.equal(linkedValue([],'old'),'');
});
const usable={commodity:'power',index:'north_hub',curve:'north_daily',definitionRevisionId:'a',contracts:[{}],samplings:[{}],quantityChoices:[{}]};
test('commodity and search filters expose only available data and exact compatible revisions',()=>{
 const south={...usable,index:'south_hub',curve:'south_daily',definitionRevisionId:'b'};
 const rows=[usable,south,{...usable,commodity:'gas'},{...usable,contracts:[]},{...usable,samplings:[]},{...usable,quantityChoices:[]}];
 assert.deepEqual(availableSwapIndices(rows,'power'),[usable,south]);
 assert.deepEqual(availableSwapIndices(rows,'power','NORTH HUB'),[usable]);
 assert.deepEqual(availableSwapIndices(rows,'power','','a'),[usable]);
 assert.deepEqual(availableSwapIndices(rows,'power','missing'),[]);
 assert.deepEqual(availableSwapIndices(rows,''),[]);
});

test('quantity defaults use the selected backend quote specification, never catalog ordering',async()=>{
 const {quantityChoice}=await import('../dist/swap-selection.js');
 const choices=[{specification:{code:'kwh'},unit:'kWh'},{specification:{code:'mwh'},unit:'MWh'}];
 assert.equal(quantityChoice(choices,'mwh'),JSON.stringify({code:'mwh'}));
 assert.equal(quantityChoice(choices,'mwh',JSON.stringify({code:'kwh'})),JSON.stringify({code:'kwh'}));
 assert.equal(quantityChoice(choices,'missing'),'');
 assert.equal(quantityChoice([{specification:{code:'mwh',revision:'a'}},{specification:{code:'mwh',revision:'b'}}],'mwh'),'');
});

test('delivery calendars use only the selected sampling and exact paired period',async()=>{
 const {deliveryChoices}=await import('../dist/swap-selection.js');
 const rows=[{resetFrequency:'monthly',deliveryFrom:'2026-01-01',deliveryTo:'2026-02-01'},{resetFrequency:'daily',deliveryFrom:'2026-03-01',deliveryTo:'2026-04-01'}];
 assert.deepEqual(deliveryChoices(rows,'monthly'),{starts:['2026-01-01'],ends:[]});
 assert.deepEqual(deliveryChoices(rows,'daily','2026-03-01'),{starts:['2026-03-01'],ends:['2026-04-01']});
 assert.deepEqual(deliveryChoices(rows,'daily','2026-01-01').ends,[]);
 assert.deepEqual(deliveryChoices(rows,''),{starts:[],ends:[]});
});

test('an incompatible prior unit stays unset instead of reinterpreting its amount',async()=>{
 const {quantityChoice}=await import('../dist/swap-selection.js');
 assert.equal(quantityChoice([{specification:{code:'mwh'}}],'mwh',JSON.stringify({code:'gas'})),'');
});
