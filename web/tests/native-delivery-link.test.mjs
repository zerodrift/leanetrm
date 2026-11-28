import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {transform} from 'esbuild';

// An exact delivery link is a function of the application shell. Its own source runs here against recorded stand-ins.
const source=await readFile(new URL('../src/app.ts',import.meta.url),'utf8');
const parts=[/function deliveriesHeld\([^]*?\n\}/,/function explainDeliveriesHeld\([^]*?\n\}/,/async function openNativeDelivery\([^]*?\n\}/].map(pattern=>source.match(pattern)?.[0]);
async function open(target,{held=null,busy=false,leaves=true}={}){
 assert.ok(parts.every(Boolean),'the delivery link and its pending-request check are named functions of app.ts');
 const calls=[],context={calls,busy,contextDirty:false,select:()=>({value:'synthetic-close'}),
  physicalOperations:{pendingDesk:()=>held,open:async value=>calls.push(['open',value.tradeId])},
  notice:message=>calls.push(['notice',message]),mayLeave:async move=>{calls.push(['leave',move]);return leaves;},act:async work=>work(),
  desks:{want:desk=>calls.push(['desk',desk])},setPage:async page=>calls.push(['page',page])};
 vm.createContext(context);vm.runInContext((await transform(parts.join('\n'),{loader:'ts',target:'es2022'})).code,context);
 await context.openNativeDelivery(target);
 return JSON.parse(JSON.stringify(calls));
}
const crude={tradeId:'synthetic-crude',bindingId:'synthetic-binding',revision:1,instrumentType:'physical_crude',tab:'delivery'};
const gas={...crude,tradeId:'synthetic-gas',instrumentType:'physical_gas'};

test('a delivery link opens its trade on that trade\'s desk',async()=>{
 assert.deepEqual(await open(crude),[['leave','page'],['desk','crude'],['page','physical-operations'],['open','synthetic-crude']]);
 assert.deepEqual(await open(gas,{held:'gas'}),[['leave','page'],['desk','gas'],['page','physical-operations'],['open','synthetic-gas']]);
});
test('a request unconfirmed on another desk is explained before the page is left',async()=>{
 const calls=await open(crude,{held:'gas'});
 assert.equal(calls.length,1);assert.equal(calls[0][0],'notice');assert.match(calls[0][1],/retained physical request/);
});
test('a refused leave or a busy app opens nothing',async()=>{
 assert.deepEqual(await open(crude,{leaves:false}),[['leave','page']]);
 assert.deepEqual(await open(crude,{busy:true}),[]);
});
