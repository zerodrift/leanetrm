import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {transform} from 'esbuild';
import * as refusals from '../dist/api-refusal.js';
import {AsyncReadError,asyncReadRequest,createAsyncReadClient} from '../dist/async-read-client.js';

const source=await readFile(new URL('../src/app.ts',import.meta.url),'utf8');
const actual=source.match(/async function api<T>\([^]*?\n\}/)?.[0];
assert.ok(actual,'The main-page read adapter must be present.');
const script=(await transform(actual,{loader:'ts',target:'es2022'})).code;
const snapshotId='20000000-0000-4000-8000-000000000001',channelId='10000000-0000-4000-8000-000000000001',protocol='async_reads_v1';
const flush=()=>new Promise(resolve=>setImmediate(resolve));
class Source extends EventTarget{
 closed=false;close(){this.closed=true;}
 emit(type,value){this.dispatchEvent(new MessageEvent(type,{data:JSON.stringify(value)}));}
}
function harness(hosted=false){
 let stream,request,invalidations=0;
 const client=createAsyncReadClient({getIdentity:()=>({profile:'synthetic-reader',context:null}),
  post:async(path,body)=>{
   if(path.endsWith('/channels'))return {protocol,channelId,profile:'synthetic-reader',context:null,admissionWindowMs:230000};
   if(path.endsWith('/cancel'))return {...body,state:'cancellation_requested'};
   request=body;return {channelId,requestId:body.requestId,state:'accepted'};
  },createEventSource:()=>stream=new Source()});
 const context={asyncReadsEnabled:true,asyncIdentityLost:false,page:'market',pageRead:undefined,readClient:client,asyncReadRequest,AsyncReadError,...refusals,
  hostedPilot:hosted,invalidateReadIdentity(){invalidations++;},shownAmounts(){throw Error('A refused read must not format a result.');}};
 vm.createContext(context);vm.runInContext(script,context);
 return {client,api:context.api,invalidations:()=>invalidations,async ready(){await flush();stream.emit('ready',{protocol,channelId});await flush();},
  fail(error){stream.emit('response',{protocol,channelId,requestId:request.requestId,operation:request.operation,snapshotId,state:'failed',error});},
  cancel(){stream.emit('response',{protocol,channelId,requestId:request.requestId,operation:request.operation,snapshotId,state:'cancelled',error:{code:'request_cancelled',message:'Synthetic cancellation'}});},
  corrupt(){stream.emit('response',{protocol:'unexpected'});}};
}

test('the actual main-page adapter gives SSE business refusals the same wording and code as HTTP',async()=>{
 for(const [path,body,code,hosted] of [
  ['evaluate',{snapshotId,curve:'synthetic'},'curve_not_selected',false],
  ['evaluate',{snapshotId,curve:'synthetic'},'missing_curve_view',false],
  ['portfolio/risk',{context:{snapshotId}},'missing_observation',false],
  ['evaluate',{snapshotId,curve:'synthetic'},'access_denied',true]]){
  const h=harness(hosted),failure={code,message:'Synthetic managed refusal.'};
  try{
   const expected=await refusals.apiRefusal(path,Response.json(failure,{status:422}),hosted);
   const pending=h.api(path,body).catch(error=>error);await h.ready();h.fail(failure);const actual=await pending;
   assert.equal(actual.message,expected.message);assert.equal(actual.code,code);
   assert.deepEqual({...actual.details},failure);assert.equal(actual.status,undefined,'SSE does not fabricate an HTTP status');
  }finally{h.client.close();}
 }
});

test('transport reset, protocol and cancellation outcomes keep their original errors',async()=>{
 for(const [action,code,name] of [['reset','async_read_reset','AsyncReadError'],['corrupt','async_read_protocol','AsyncReadError'],['cancel','request_cancelled','AbortError']]){
  const h=harness();
  try{
   const pending=h.api('evaluate',{snapshotId,curve:'synthetic'}).catch(error=>error);await h.ready();
   if(action==='reset')h.client.reset();else h[action]();
   const error=await pending;assert.equal(error.code,code);assert.equal(error.name,name);assert.equal(error.details,undefined);
  }finally{h.client.close();}
 }
});

test('a server refusal cannot impersonate a local identity-change outcome through its code',async()=>{
 const h=harness();try{
  const pending=h.api('evaluate',{snapshotId,curve:'synthetic'}).catch(error=>error);await h.ready();h.fail({code:'async_read_identity',message:'Synthetic server refusal'});
  const error=await pending;assert.equal(error.code,'async_read_identity');assert.equal(h.invalidations(),0);
 }finally{h.client.close();}
});
