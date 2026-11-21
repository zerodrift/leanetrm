import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {transform} from 'esbuild';
import * as desk from '../dist/bilateral-desk.js';

const source=await readFile(new URL('../src/app.ts',import.meta.url),'utf8');
const entry=source.match(/async function api<T>\([^\n]*?\{[^]*?\n\}/)[0];
const script=(await transform(entry,{loader:'ts',target:'es2022'})).code;
const path='tickets/gas-locations/book',body={operationId:'parent',agreements:'separate_confirmations'};
const choices=[{id:'first',name:'Synthetic framework',revision:1},{id:'second',name:'Synthetic master',revision:2}];
function refuse(confirmation){
 return Object.assign(Error('Choose an agreement'),{code:'desk_agreement_ambiguous',
  details:{confirmation,agreementSelection:{operationId:confirmation+'-child',choices}}});
}
function booking(refusals,onChoice=()=>{}){
 const prompts=[],recorded=[],profiles=[],attempts=[],snapshot={value:'close'};
 const env={...desk,currentProfile:'trader',administrationScope:'desk',exampleId:'sample',sessionEnded:false,asyncIdentityLost:false,contextDirty:false,closeSession:'series',
  select:()=>snapshot,navigation:{context:{mode:'current'}},apiRoot:'/api/',asyncReadsEnabled:false,
  shownAmounts:()=>undefined,AsyncReadError:class extends Error{},
  async selectBookingAgreement(options,confirmation){assert.deepEqual(options,choices);prompts.push(confirmation);onChoice(env,snapshot);return choices[prompts.length-1].id;},
  async transport(route,request){if(route==='desk/choose'){recorded.push([request.operationId,request.agreementId]);profiles.push(request.expectedProfile);return;}
   assert.equal(route,path);assert.equal(request,body);attempts.push(request.operationId);
   const next=refusals.shift();if(next)throw next;return {purchaseTradeId:'buy',saleTradeId:'sell'};}};
 vm.createContext(env);vm.runInContext(script,env);
 return {run:()=>env.api(path,body),prompts,recorded,profiles,attempts};
}
test('one booking retains two explicit child agreement choices and its original operation',async()=>{
 const b=booking([refuse('purchase'),refuse('sale')]);
 assert.deepEqual(await b.run(),{purchaseTradeId:'buy',saleTradeId:'sell'});
 assert.deepEqual(b.prompts,['purchase','sale']);assert.deepEqual(b.recorded,[['purchase-child','first'],['sale-child','second']]);
 assert.deepEqual(b.profiles,['trader','trader']);
 assert.deepEqual(b.attempts,['parent','parent','parent']);
});
test('a sale can require the first choice, and a repeated refusal never repeats a choice',async()=>{
 const b=booking([refuse('sale'),refuse('sale')]);await assert.rejects(b.run(),{code:'desk_agreement_ambiguous'});
 assert.deepEqual(b.prompts,['sale']);assert.deepEqual(b.recorded,[['sale-child','first']]);assert.equal(b.attempts.length,2);
});
test('changing the selected close while an agreement dialog is open prevents the choice and resubmission',async()=>{
 const b=booking([refuse('sale')],(_,snapshot)=>{snapshot.value='another-close';});
 await assert.rejects(b.run(),/account or market context changed/);assert.deepEqual(b.recorded,[]);assert.deepEqual(b.attempts,['parent']);
});
test('an account invalidated by another view cannot post an outstanding agreement choice',async()=>{
 const b=booking([refuse('purchase')],env=>{env.asyncIdentityLost=true;});
 await assert.rejects(b.run(),/account or market context changed/);assert.deepEqual(b.recorded,[]);assert.equal(b.attempts.length,1);
});
test('an unknown outcome on resubmission propagates without another write',async()=>{
 const b=booking([refuse('purchase'),Object.assign(Error('unknown'),{code:'response_unknown'})]);
 await assert.rejects(b.run(),{code:'response_unknown'});assert.equal(b.recorded.length,1);assert.equal(b.attempts.length,2);
});
