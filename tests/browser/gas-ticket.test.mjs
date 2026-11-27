import test from 'node:test';
import assert from 'node:assert/strict';
import {browserPageActions} from './owned-browser.mjs';
import {blotterAt,blotterShowsLatest,gasTicket,openBookedPosition,openBookedSchedules} from './gas-ticket.mjs';

function fixture(readyAt,{selectionError}={}){
 let elapsed=0;const edits=[];
 const options=()=>elapsed>=readyAt?[{value:''},{value:'synthetic_entity@1'}]:[{value:''}];
 const document={
  querySelectorAll(selector){assert.match(selector,/legal_entity.* option$/);return options();},
  querySelector(selector){assert.match(selector,/legal_entity/);return {options:options()};},
 };
 const evaluate=async expression=>expression.startsWith('({status:')?
  {status:'Updating…',errors:['legal_entity: Reference unavailable']}:
  Function('document',`return ${expression}`)(document);
 const {wait}=browserPageActions(evaluate,{pause:async ms=>{elapsed+=ms;},now:()=>elapsed});
 const ticket=gasTicket({evaluate,wait,set:async(selector,value)=>{edits.push({selector,value});if(selectionError)throw selectionError;}});
 return {ticket,edits,elapsed:()=>elapsed};
}

for(const method of ['first','choose']){
 test(`${method} waits for queued reference choices and selects once`,async()=>{
  const f=fixture(12500);
  await f.ticket[method](null,'legal_entity','synthetic_entity@1');
  assert.equal(f.elapsed(),12500);
  assert.equal(f.edits.length,1);
  assert.equal(f.edits[0].value,'synthetic_entity@1');
 });
 test(`${method} keeps a bounded deadline with the visible field refusal`,async()=>{
  const f=fixture(Infinity);
  await assert.rejects(f.ticket[method](null,'legal_entity','synthetic_entity@1'),error=>
   /Timed out after 30000ms/.test(error.message)&&/legal_entity: Reference unavailable/.test(error.message));
  assert.equal(f.elapsed(),30000);assert.deepEqual(f.edits,[]);
 });
 test(`${method} does not retry an uncertain selection`,async()=>{
  const error=Error('Selection response lost'),f=fixture(0,{selectionError:error});
  await assert.rejects(f.ticket[method](null,'legal_entity','synthetic_entity@1'),e=>e===error);
  assert.equal(f.edits.length,1);
 });
}

// The convention opens its newest close first. Its prices can still be loading
// when the requested close is offered, and the final close has its own prices.
function blotterFixture({offered=true,finalState='complete',resetOnOpen=false}={}){
 let now=0,selected='intermediate',pricesAt=Infinity,choosing=false;
 const edits=[],frame={session:'test-session',id:'requested-close'};
 const controlsReady=()=>choosing&&now>=500;
 const snapshot=()=>({value:selected,options:controlsReady()&&offered?[{value:frame.id}]:[]});
 const document={
  body:{getAttribute:()=>now<750?'true':'false'},
  querySelector(selector){
   if(selector==='#snapshot')return snapshot();
   if(selector==='#close-session')return {value:controlsReady()?frame.session:''};
   if(selector==='#page-title')return {textContent:'Blotter'};
   if(selector==='#futures-page:not([hidden])'||selector==='#futures-page')return {dataset:{pricingState:now>=pricesAt?finalState:'pending'}};
   return null;
  },
 };
 const evaluate=async expression=>Function('document',`return ${expression}`)(document);
 const {wait}=browserPageActions(evaluate,{now:()=>now,pause:async ms=>{now+=ms;}});
 const set=async(selector,value)=>{
  edits.push([selector,value,now]);
  if(selector==='#close-session')choosing=true;
  if(selector==='#snapshot'){
   assert.ok(now>=750&&snapshot().options.some(option=>option.value===value));
   selected=value;pricesAt=now+12500;
  }
 };
 const click=async selector=>{assert.equal(selector,'#nav-futures');if(resetOnOpen)selected='intermediate';};
 const actions={wait,set,click};
 return {open:()=>blotterAt(actions,frame),edits,elapsed:()=>now};
}

test('the gas Blotter can leave pending intermediate prices and waits for the requested close to finish pricing',async()=>{
 const f=blotterFixture();
 await f.open();
 assert.deepEqual(f.edits.map(([selector,value])=>[selector,value]),[['#close-session','test-session'],['#snapshot','requested-close']]);
 assert.ok(f.elapsed()>=f.edits[1][2]+12500);
});

test('the gas Blotter refuses an unavailable close or a final price that never settles',async()=>{
 const missing=blotterFixture({offered:false});
 await assert.rejects(missing.open(),/Timed out/);
 assert.equal(missing.edits.length,1);
 const pending=blotterFixture({finalState:'pending'});
 await assert.rejects(pending.open(),/Timed out/);
 assert.equal(pending.edits.length,2);
});

test('the gas Blotter refuses a different close selected during navigation',async()=>{
 const f=blotterFixture({resetOnOpen:true});
 await assert.rejects(f.open(),/Timed out/);
});

test('the gas Blotter refuses a failed final price read',async()=>{
 const f=blotterFixture({finalState:'failed'});
 await assert.rejects(f.open(),/Timed out/);
});

function positionFixture({refreshAfterPoll=false,accepted=true,reference='Synthetic B',hidden=false,readMs=12500}={}){
 let now=0,busyUntil=0,selected='trade-a:binding-a',shown='Synthetic A',refresh=refreshAfterPoll;
 const clicks=[],key='trade-b:binding-b';
 const row={getAttribute:()=>selected===key?'true':null};
 const control={textContent:'Synthetic B',checkVisibility:()=>!hidden,closest:()=>row,click(){
  clicks.push(now);
  if(now<busyUntil||!accepted)return;
  selected=key;shown=reference;busyUntil=now+readMs;
 }};
 const document={
  body:{getAttribute:()=>now<busyUntil?'true':'false'},
  querySelector(selector){
   if(selector===`#desk-positions [data-row-key="${key}"] .desk-reference`)return control;
   if(selector==='#position-details')return {hidden:false,checkVisibility:()=>true,querySelector:()=>({textContent:shown})};
   return null;
  },
 };
 const evaluate=async expression=>{
  const result=Function('document',`return ${expression}`)(document);
  // A refresh is free to begin after a readiness evaluation, before a separate click.
  if(refresh&&result===true&&!clicks.length){refresh=false;busyUntil=now+500;}
  return result;
 };
 const actions=browserPageActions(evaluate,{now:()=>now,pause:async ms=>{now+=ms;}});
 return {open:()=>openBookedPosition(actions,key,30000),clicks,selected:()=>selected,shown:()=>shown,elapsed:()=>now};
}

test('opening a booked position cannot lose its click to a refresh between browser evaluations',async()=>{
 const f=positionFixture({refreshAfterPoll:true});
 await f.open();
 assert.equal(f.selected(),'trade-b:binding-b');assert.equal(f.shown(),'Synthetic B');
 assert.deepEqual(f.clicks,[0]);assert.equal(f.elapsed(),12500);
});

test('opening a booked position requires both the exact selected binding and its displayed trade',async()=>{
 for(const options of [{accepted:false},{reference:'Synthetic A'}]){
  const f=positionFixture(options);
  await assert.rejects(f.open(),/Timed out after 30000ms/);
  assert.equal(f.clicks.length,1);assert.equal(f.elapsed(),30000);
 }
});

test('opening a booked position refuses an unavailable row or a read that never settles',async()=>{
 for(const options of [{hidden:true},{readMs:Infinity}]){
  const f=positionFixture(options);
  await assert.rejects(f.open(),/Timed out after 30000ms/);
  assert.equal(f.clicks.length,options.hidden?0:1);assert.equal(f.elapsed(),30000);
 }
});

function scheduleFixture({readyAt=12500,readMs=12500,hidden=false,refreshAfterPoll=false}={}){
 let now=0,busyUntil=readyAt;const clicks=[];
 let refresh=refreshAfterPoll,opened=false;
 const document={
  body:{getAttribute:()=>now<busyUntil?'true':'false'},
  querySelector(selector){
   if(selector==='#position-details-tabs-schedules')return {checkVisibility:()=>!hidden,click(){clicks.push(now);if(now<busyUntil)return;opened=true;busyUntil=now+readMs;}};
   if(selector==='#position-details')return {hidden};
   return null;
  },
 };
 const evaluate=async expression=>{
  const result=Function('document',`return ${expression}`)(document);
  if(refresh&&result===true&&!clicks.length){refresh=false;busyUntil=now+500;}
  return result;
 };
 const actions=browserPageActions(evaluate,{now:()=>now,pause:async ms=>{now+=ms;}});
 return {open:milliseconds=>openBookedSchedules(actions,milliseconds),clicks,elapsed:()=>now,opened:()=>opened};
}

test('opening booked schedules cannot lose its click to a refresh between browser evaluations',async()=>{
 const f=scheduleFixture({readyAt:0,refreshAfterPoll:true});
 await f.open(30000);
 assert.equal(f.opened(),true);assert.deepEqual(f.clicks,[0]);assert.equal(f.elapsed(),12500);
});

test('booked schedules honor the caller wait for both the position and its schedule read without clicking twice',async()=>{
 const f=scheduleFixture();
 await f.open(30000);
 assert.deepEqual(f.clicks,[12500]);
 assert.equal(f.elapsed(),25000);
});

test('booked schedules retain the ordinary wait when the caller does not select a longer one',async()=>{
 const f=scheduleFixture();
 await assert.rejects(f.open(),/Timed out after 10000ms/);
 assert.deepEqual(f.clicks,[]);
 assert.equal(f.elapsed(),10000);
});

test('a caller wait cannot pass a stuck position, hidden panel or stuck schedule read',async()=>{
 for(const options of [{readyAt:Infinity},{readyAt:0,hidden:true}]){
  const f=scheduleFixture(options);
  await assert.rejects(f.open(30000),/Timed out after 30000ms/);
  assert.deepEqual(f.clicks,[]);
  assert.equal(f.elapsed(),30000);
 }
 const f=scheduleFixture({readyAt:0,readMs:Infinity});
 await assert.rejects(f.open(30000),/Timed out after 30000ms/);
 assert.deepEqual(f.clicks,[0]);
 assert.equal(f.elapsed(),30000);
});

// A recorded election leaves the server one version ahead of the Blotter. The page is idle until its update check finds that,
// busy while it values the book again, and asks about the new version only once its update has ended. Its first request after
// an election can still name the version before it, which it had not asked about while the election's form was open.
function versionFixture({updateAt=500,shownAt=12500,status=200,values='complete',closeAt=0}={}){
 let now=0;const requests=[];
 const window={
  get watched(){return {context:{snapshotId:now>=closeAt?'synthetic-close':'earlier-close',convention:'trading_workspace_v1'},
   version:now>=shownAt?'after':now>=250?'before':'earlier'};},
  async workspaceFetch(path,init={}){
   requests.push({at:now,path,method:init.method??'GET',token:init.headers?.['X-Preview-CSRF'],body:init.body});
   if(path==='/api/bootstrap?includeFrames=false')return {ok:true,json:async()=>({csrfToken:'synthetic-token'})};
   return {ok:status===200,status,json:async()=>({version:'after'})};
  },
 };
 const document={body:{getAttribute:()=>now>=updateAt&&now<shownAt-250?'true':'false'},
  querySelector:selector=>selector==='#snapshot'?{value:'synthetic-close'}:selector==='#futures-page'?{dataset:{pricingState:values}}:null};
 const evaluate=async expression=>Function('document','window',`return ${expression}`)(document,window);
 const actions={evaluate,...browserPageActions(evaluate,{now:()=>now,pause:async ms=>{now+=ms;}})};
 return {settle:()=>blotterShowsLatest(actions,'window.watched',30000),requests,elapsed:()=>now};
}

test('a recorded election is not settled while the idle Blotter still shows the version before it',async()=>{
 const f=versionFixture();
 await f.settle();
 assert.equal(f.elapsed(),12500);
 assert.deepEqual(f.requests.at(-1),{at:0,path:'/api/trading/freshness',method:'POST',token:'synthetic-token',
  body:JSON.stringify({snapshotId:'synthetic-close',convention:'trading_workspace_v1'})});
});

test('the version is read for the close on display, once the Blotter watches it',async()=>{
 const f=versionFixture({closeAt:1000});
 await f.settle();
 assert.equal(f.elapsed(),12500);
 assert.deepEqual(f.requests.map(r=>[r.at,r.path]),[[1000,'/api/bootstrap?includeFrames=false'],[1000,'/api/trading/freshness']]);
 assert.equal(f.requests.at(-1).body,JSON.stringify({snapshotId:'synthetic-close',convention:'trading_workspace_v1'}));
});

test('a Blotter that never shows the recorded version, or whose update failed, is refused',async()=>{
 for(const options of [{updateAt:Infinity,shownAt:Infinity},{values:'failed'}]){
  const f=versionFixture(options);
  await assert.rejects(f.settle(),/Timed out after 30000ms/);
 }
});

test('a refused version read is reported and never taken as settled',async()=>{
 const f=versionFixture({status:503});
 await assert.rejects(f.settle(),/Blotter version read returned 503/);
 assert.equal(f.elapsed(),0);
});
