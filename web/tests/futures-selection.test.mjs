import test from 'node:test';
import assert from 'node:assert/strict';
import {futuresSelection} from '../dist/futures-contract.js';
const rows=[
 {id:'one',curve:'gas_a',commodity:'gas',commodityName:'Gas',exchange:'a',exchangeName:'Exchange A',product:'g',name:'Gas monthly',deliveryFrom:'2026-11-01'},
 {id:'two',curve:'gas_a',commodity:'gas',commodityName:'Gas',exchange:'a',exchangeName:'Exchange A',product:'g',name:'Gas monthly',deliveryFrom:'2026-12-01'},
 {id:'three',curve:'gas_b',commodity:'gas',commodityName:'Gas',exchange:'b',exchangeName:'Exchange B',product:'g2',name:'Gas daily',deliveryFrom:'2026-11-03'},
 {id:'four',curve:'power_a',commodity:'power',commodityName:'Power',exchange:'a',exchangeName:'Exchange A',product:'p',name:'Power',deliveryFrom:'2026-11-01'},
];
test('futures choices cascade only through available catalog rows',()=>{
 const all=futuresSelection(rows,{});assert.deepEqual(all.commodities.map(c=>c.value),['gas','power']);assert.deepEqual(all.contracts,[]);
 const gas=futuresSelection(rows,{commodity:'gas'});assert.deepEqual(gas.exchanges.map(c=>c.value),['a','b']);assert.deepEqual(gas.products,[]);
 const product=futuresSelection(rows,{commodity:'gas',exchange:'a',product:'g'});assert.deepEqual(product.dates,['2026-11-01','2026-12-01']);assert.deepEqual(product.contracts.map(c=>c.id),['one','two']);
 const date=futuresSelection(rows,{commodity:'gas',exchange:'a',product:'g',date:'2026-12-01'});assert.deepEqual(date.contracts.map(c=>c.id),['two']);
});
test('the exact contract can be chosen directly, with delivery date as an optional filter',()=>{
 const product={commodity:'gas',exchange:'a',product:'g'};
 assert.deepEqual(futuresSelection(rows,product).contracts.map(c=>c.id),['one','two']);
 assert.deepEqual(futuresSelection(rows,{...product,date:'2026-12-01'}).contracts.map(c=>c.id),['two']);
 assert.deepEqual(futuresSelection(rows,{...product,date:'2027-01-01'}).contracts,[]);
 assert.deepEqual(futuresSelection(rows,{commodity:'power',exchange:'a',product:'g'}).contracts,[]);
});
test('stale downstream futures filters expose no unrelated contracts',()=>{
 const changed=futuresSelection(rows,{commodity:'power',exchange:'b',product:'g2',date:'2026-11-03'});assert.deepEqual(changed.exchanges.map(c=>c.value),['a']);assert.deepEqual(changed.products,[]);assert.deepEqual(changed.contracts,[]);
 assert.deepEqual(futuresSelection([],{}),{commodities:[],exchanges:[],products:[],dates:[],contracts:[]});
});
test('same dated series on distinct curves remains explicitly selectable',()=>{
 const result=futuresSelection([...rows,{...rows[0],curve:'gas_other'}],{commodity:'gas',exchange:'a',product:'g',date:'2026-11-01'});assert.deepEqual(result.contracts.map(c=>c.curve),['gas_a','gas_other']);
});
