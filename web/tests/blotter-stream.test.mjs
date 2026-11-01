import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readBlotterStream,streamBlotter,BlotterStreamError} from '../dist/blotter-stream.js';

const ctx={contextId:'c1',effectiveOn:'2026-10-30',knownAt:'2026-10-30T00:00:00Z'};
const request=(mode='trades',contextId)=>({context:{snapshotId:'s1',convention:'v1',knowledgeMode:'current',...(contextId?{contextId}:{})},view:{mode},summaries:[]});
const start=(positions=3,context=ctx)=>({type:'start',positions,context,asOf:'2026-10-30'});
const rows=(n,valued,positions=3)=>({type:'rows',valued,rows:Array.from({length:n},(_,i)=>({id:`r${valued}-${i}`})),positions});
const complete=(valued=3,positions=3)=>({type:'complete',valued,positions,blotter:{b:1},summaries:[],catalog:{}});
const lines=frames=>frames.map(f=>JSON.stringify(f)+'\n').join('');
const bodyOf=(...chunks)=>new ReadableStream({start(c){const e=new TextEncoder();for(const x of chunks)c.enqueue(e.encode(x));c.close();}});
const recorder=()=>{const calls=[];return {calls,handlers:{
  start:s=>calls.push(['start',s.positions]),rows:(r,v,p)=>calls.push(['rows',r.length,v,p]),complete:c=>calls.push(['complete',c.blotter])}};};
const run=(body,req=request())=>{const r=recorder();return readBlotterStream(body,req,r.handlers,new AbortController().signal).then(()=>r.calls);};
const code=c=>e=>e instanceof BlotterStreamError&&e.code===c;
const full=lines([start(),rows(2,2),rows(1,3),complete()]);

test('parses frames split across chunks and several in one chunk',async()=>{
  const expected=[['start',3],['rows',2,2,3],['rows',1,3,3],['complete',{b:1}]];
  assert.deepEqual(await run(bodyOf(full)),expected);
  assert.deepEqual(await run(bodyOf(...full.match(/[\s\S]{1,7}/g))),expected);
});
test('rejects rows before start',async()=>{
  await assert.rejects(run(bodyOf(lines([rows(1,1),complete()]))),code('stream_order'));
});
test('rejects a second start and frames after the terminal frame',async()=>{
  await assert.rejects(run(bodyOf(lines([start(),start()]))),code('stream_order'));
  await assert.rejects(run(bodyOf(lines([start(0),complete(0,0),rows(0,0,0)]),'')),code('stream_order'));
});
test('rejects valued above positions, decreasing valued, and a row count different from positions in trades mode',async()=>{
  await assert.rejects(run(bodyOf(lines([start(),rows(4,4)]))),code('stream_count'));
  await assert.rejects(run(bodyOf(lines([start(),rows(2,2),rows(1,1)]))),code('stream_count'));
  await assert.rejects(run(bodyOf(lines([start(),rows(2,2),complete(2,3)]))),code('stream_count'));
  await assert.rejects(run(bodyOf(lines([start(),rows(2,2),complete(3,3)]))),code('stream_count'));
});
test('rejects start context different from the requested contextId; none requested accepts any',async()=>{
  await assert.rejects(run(bodyOf(full),request('trades','other')),code('stream_context'));
  assert.equal((await run(bodyOf(full),request('trades','c1'))).length,4);
  assert.equal((await run(bodyOf(full),request('trades'))).length,4);
});
test('rejects a body ending without a terminal frame',async()=>{
  await assert.rejects(run(bodyOf(lines([start(),rows(2,2)]))),code('stream_truncated'));
  await assert.rejects(run(bodyOf(lines([start()]).trimEnd())),code('stream_truncated'));
  await assert.rejects(run(bodyOf('')),code('stream_truncated'));
});
test("rejects with the error frame's code and message",async()=>{
  await assert.rejects(run(bodyOf(lines([start(),{type:'error',code:'missing_observation',message:'no mark'}]))),
    e=>e instanceof BlotterStreamError&&e.code==='missing_observation'&&e.message==='no mark');
});
test('pivot mode accepts empty rows frames',async()=>{
  const calls=await run(bodyOf(lines([start(),rows(0,1),rows(0,3),complete()])),request('pivot'));
  assert.deepEqual(calls.map(c=>c[0]),['start','rows','rows','complete']);
});
test('abort stops without further handler calls and resolves',async()=>{
  const abort=new AbortController(),r=recorder(),e=new TextEncoder();
  let release;const gate=new Promise(res=>release=res);
  const body=new ReadableStream({async start(c){c.enqueue(e.encode(lines([start()])));await gate;c.enqueue(e.encode(lines([rows(2,2),complete()])));c.close();}});
  const done=readBlotterStream(body,request(),{...r.handlers,start:s=>{r.handlers.start(s);abort.abort();}},abort.signal);
  await new Promise(res=>setTimeout(res,10));release();
  await done;
  assert.deepEqual(r.calls,[['start',3]]);
});
test('streamBlotter rejects a non-200 response with its code and streams a 200',async()=>{
  const ac=new AbortController(),r=recorder();
  const refuse=async()=>new Response(JSON.stringify({code:'rights_denied',message:'no'}),{status:403});
  await assert.rejects(streamBlotter(refuse,request(),r.handlers,ac.signal),e=>e instanceof BlotterStreamError&&e.code==='rights_denied'&&e.message==='no');
  let seen;
  const ok=async(path,body,signal)=>{seen=[path,body,signal];return new Response(full,{status:200});};
  await streamBlotter(ok,request(),r.handlers,ac.signal);
  assert.equal(seen[0],'trading/workspace/stream');assert.equal(seen[2],ac.signal);assert.equal(r.calls.length,4);
});
test('rejects an error frame whose code or message is not a string, and an error frame before start',async()=>{
  await assert.rejects(run(bodyOf(lines([start(),{type:'error',code:7,message:'no mark'}]))),code('stream_order'));
  await assert.rejects(run(bodyOf(lines([start(),{type:'error',code:'missing_observation',message:null}]))),code('stream_order'));
  await assert.rejects(run(bodyOf(lines([{type:'error',code:'access_denied',message:'no'}]))),code('stream_order'));
});
test('rejects a start frame without a string asOf',async()=>{
  await assert.rejects(run(bodyOf(lines([{...start(),asOf:undefined},rows(2,2),rows(1,3),complete()]))),code('stream_order'));
  await assert.rejects(run(bodyOf(lines([{...start(),asOf:20261030},rows(2,2),rows(1,3),complete()]))),code('stream_order'));
});
test('rejects rows entries that are not objects',async()=>{
  for(const entry of [null,'r',1,[]])
    await assert.rejects(run(bodyOf(lines([start(1),{type:'rows',valued:1,rows:[entry]},complete(1,1)]))),code('stream_count'));
});
test('pivot mode rejects a rows frame that carries rows',async()=>{
  await assert.rejects(run(bodyOf(lines([start(),rows(1,1),rows(0,3),complete()])),request('pivot')),code('stream_count'));
});
test('rejects a complete frame without an object blotter, an array of summaries and an object catalog',async()=>{
  const body=member=>bodyOf(lines([start(),rows(2,2),rows(1,3),{...complete(),...member}]));
  for(const member of [{blotter:undefined},{blotter:[]},{summaries:undefined},{summaries:{}},{catalog:undefined},{catalog:null},{catalog:'c'}])
    await assert.rejects(run(body(member)),code('stream_order'),JSON.stringify(member));
});
test('abort settles while a read is pending on a body that sends nothing more',async()=>{
  const abort=new AbortController(),r=recorder(),e=new TextEncoder();
  const body=new ReadableStream({start(c){c.enqueue(e.encode(lines([start()])));}});
  const done=readBlotterStream(body,request(),{...r.handlers,start:s=>{r.handlers.start(s);setTimeout(()=>abort.abort(),5);}},abort.signal);
  const settled=await Promise.race([done.then(()=>'settled'),new Promise(res=>setTimeout(()=>res('pending'),500))]);
  assert.equal(settled,'settled');
  assert.deepEqual(r.calls,[['start',3]]);
});

test('awaits each asynchronous row display before subsequent rows or completion',async()=>{
 const seen=[];let release;const held=new Promise(resolve=>release=resolve);
 const done=readBlotterStream(bodyOf(full),request(),{
  start:()=>{},rows:async batch=>{seen.push('begin'+batch.length);if(batch.length===2)await held;seen.push('end'+batch.length);},complete:()=>seen.push('complete')
 },new AbortController().signal);
 await new Promise(resolve=>setTimeout(resolve,0));
 assert.deepEqual(seen,['begin2']);release();await done;
 assert.deepEqual(seen,['begin2','end2','begin1','end1','complete']);
});

test('abort settles while an asynchronous handler is held and starts no later handlers',async()=>{
 for(const phase of ['start','rows']){
  let release,entered;const held=new Promise(r=>release=r),began=new Promise(r=>entered=r),seen=[];
  const controller=new AbortController();
  const done=readBlotterStream(bodyOf(full),request(),{
   start:async()=>{seen.push('start');if(phase==='start'){entered();await held;}},
   rows:async()=>{seen.push('rows');if(phase==='rows'){entered();await held;}},complete:()=>seen.push('complete')
  },controller.signal);
  await began;controller.abort();
  assert.equal(await Promise.race([done.then(()=>true),new Promise(r=>setTimeout(()=>r(false),100))]),true);
  release();await new Promise(r=>setTimeout(r,0));
  assert.deepEqual(seen,phase==='start'?['start']:['start','rows']);
 }
});
