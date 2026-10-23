import test from 'node:test';
import assert from 'node:assert/strict';
import {linkedValue,quantityChoice,deliveryChoices,filterSwapIndices} from '../dist/swap-selection.js';
test('linked selectors preserve an offered choice and fill only an unambiguous replacement',()=>{
 assert.equal(linkedValue(['a','b'],'b'),'b');assert.equal(linkedValue(['a','b'],'old'),'');assert.equal(linkedValue(['a'],'old'),'a');assert.equal(linkedValue([],'old'),'');
});
test('quantity selection uses the exact offered canonical specification, not list order',()=>{
 const choices=[{specification:{code:'kwh',revision:'a'}},{specification:{code:'mwh',revision:'a'}}];
 assert.equal(quantityChoice(choices,'mwh'),JSON.stringify(choices[1].specification));assert.equal(quantityChoice(choices,'mwh',JSON.stringify(choices[0].specification)),JSON.stringify(choices[0].specification));assert.equal(quantityChoice([...choices,{specification:{code:'mwh',revision:'b'}}],'mwh'),'');
});
test('delivery choices preserve the backend sampling, averaging and exact period pairing',()=>{
 const periods=[{resetFrequency:'daily',averagingMethod:'arithmetic',deliveryFrom:'2026-01-01',deliveryTo:'2026-02-01'},{resetFrequency:'daily',averagingMethod:'observation_arithmetic',deliveryFrom:'2026-03-01',deliveryTo:'2026-04-01'}];
 assert.deepEqual(deliveryChoices(periods,'daily','arithmetic'),{starts:['2026-01-01'],ends:[]});assert.deepEqual(deliveryChoices(periods,'daily','observation_arithmetic','2026-03-01'),{starts:['2026-03-01'],ends:['2026-04-01']});assert.deepEqual(deliveryChoices(periods,'daily','arithmetic','2026-03-01').ends,[]);
});
test('index text filtering stays within commodity and exact revision',()=>{
 const rows=[{commodity:'power',index:'north_hub',curve:'daily_north',definitionRevisionId:'a'},{commodity:'gas',index:'north_hub',curve:'daily_north',definitionRevisionId:'b'}];
 assert.deepEqual(filterSwapIndices(rows,'power','NORTH HUB','a'),[rows[0]]);assert.deepEqual(filterSwapIndices(rows,'power','','b'),[]);
});
