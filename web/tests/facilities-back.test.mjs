import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {transform} from 'esbuild';

const source=await readFile(new URL('../src/app.ts',import.meta.url),'utf8');
const restore=source.match(/async function restorePlace\(origin:Place\)\{[^]*?\n\}/)?.[0];
async function run(report){
 const calls=[],context={calls,selectContext:async value=>calls.push(['context',value]),setPage:async page=>calls.push(['page',page]),facilities:{load:async(...args)=>calls.push(['ledger',...args]),open:async section=>calls.push(['section',section])},readPages:[],showTarget:async()=>assert.fail('An unsupported operations page cannot become an assistant target.'),showPage:async()=>assert.fail('The explicit operations section must be restored.')};
 vm.createContext(context);vm.runInContext((await transform(restore,{loader:'ts',target:'es2022'})).code,context);
 await context.restorePlace({page:'facilities',report,context:{mode:'current',frameId:null,businessDate:'2032-03-14'}});
 return JSON.parse(JSON.stringify(calls));
}

test('Back restores Transport capacity under its original operating date without opening a quantity ledger',async()=>{
 const calls=await run({page:'transport_capacity',selection:{}});
 assert.deepEqual(calls,[['context',{mode:'current',contextId:null,frameId:null,businessDate:'2032-03-14'}],['page','facilities'],['section','transport']]);
});

test('Back restores Route plans under their operating date without selecting capacity or a quantity ledger',async()=>{
 const calls=await run({page:'transport_route_plans',selection:{}});
 assert.deepEqual(calls,[['context',{mode:'current',contextId:null,frameId:null,businessDate:'2032-03-14'}],['page','facilities'],['section','route']]);
});

test('Back retains each explicit facility operations section and the exact quantity ledger selection',async()=>{
 for(const [page,section]of [['facility_definitions','definitions'],['facility_activity','activity']])assert.deepEqual((await run({page,selection:{}})).at(-1),['section',section]);
 assert.deepEqual((await run({page:'facility_quantities',selection:{ledgerId:'synthetic-original-ledger'}})).at(-1),['ledger','synthetic-original-ledger',false]);
});

test('Back refuses an unknown operating report instead of selecting a quantity or capacity source',async()=>{
 await assert.rejects(run({page:'unsupported_synthetic_operations',selection:{}}),/not supported/);
});
