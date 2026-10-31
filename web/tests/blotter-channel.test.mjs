import test from 'node:test';
import assert from 'node:assert/strict';
import {readBlotterMessage,openBlotterChannel} from '../dist/blotter-channel.js';
import {newBlotterView} from '../dist/blotter-layout.js';

const S='session-a';
const selection={mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[]};
const futures={page:'futures',risk:false,trade:null,entry:null,lens:'values',view:selection,pane:{details:false,charts:true},poppedOut:false};
const valid={
 context:{kind:'context',context:{snapshotId:'snap-1',knowledgeMode:'current',contextId:null,profile:'p1'}},
 view:{kind:'view',instance:'w1',view:newBlotterView()},
 opened:{kind:'opened',instance:'w1'},closed:{kind:'closed',instance:'w1'},
 ended:{kind:'ended',reason:'signed_out'},
 pageReport:{kind:'pageReport',instance:'w1',state:futures,unsaved:false},
 askAbout:{kind:'askAbout',instance:'w1',key:'t1:b1',bookIds:['b1'],portfolioIds:[]},
};
const msg=(kind,extra={})=>({v:1,session:S,...valid[kind],...extra});

test('one valid message per kind is accepted and returned as sent',()=>{
 for(const kind of Object.keys(valid))assert.deepEqual(readBlotterMessage(msg(kind),S),msg(kind),kind);
 for(const reason of ['signed_out','account_changed','main_closed','brought_back'])assert.equal(readBlotterMessage(msg('ended',{reason}),S).reason,reason);
});
test('another session id, another version and an unknown kind are refused',()=>{
 assert.throws(()=>readBlotterMessage(msg('opened',{session:'session-b'}),S));
 assert.throws(()=>readBlotterMessage(msg('opened',{v:2}),S));
 assert.throws(()=>readBlotterMessage(msg('opened',{kind:'shutdown'}),S));
 for(const bad of [null,undefined,'opened',7,[],{}])assert.throws(()=>readBlotterMessage(bad,S));
});
test('every message but context and ended names the pop-out it concerns',()=>{
 for(const kind of ['view','opened','closed','pageReport','askAbout']){
  const {instance,...unnamed}=msg(kind);
  assert.throws(()=>readBlotterMessage(unnamed,S),kind);
  for(const bad of ['',7,null,'x'.repeat(201)])assert.throws(()=>readBlotterMessage(msg(kind,{instance:bad}),S),kind+' '+bad);
 }
 for(const kind of ['context','ended'])assert.throws(()=>readBlotterMessage(msg(kind,{instance:'w1'}),S),kind);
});
test('a report says whether the pop-out holds unsaved edits, and a question names the scope it was asked in',()=>{
 for(const unsaved of [true,false])assert.equal(readBlotterMessage(msg('pageReport',{unsaved}),S).unsaved,unsaved);
 for(const unsaved of [undefined,'yes',1,null])assert.throws(()=>readBlotterMessage(msg('pageReport',{unsaved}),S),String(unsaved));
 const {unsaved,...silent}=msg('pageReport');assert.throws(()=>readBlotterMessage(silent,S));
 assert.deepEqual(readBlotterMessage(msg('askAbout',{bookIds:[],portfolioIds:['p1','p2']}),S).portfolioIds,['p1','p2']);
 for(const scope of [{bookIds:'b1'},{portfolioIds:[1]},{bookIds:undefined}])assert.throws(()=>readBlotterMessage(msg('askAbout',scope),S),JSON.stringify(scope));
});
test('extra fields are refused at every level',()=>{
 for(const kind of Object.keys(valid))assert.throws(()=>readBlotterMessage(msg(kind,{figures:'1'}),S),kind);
 assert.throws(()=>readBlotterMessage(msg('context',{context:{...valid.context.context,pv:'1'}}),S));
 assert.throws(()=>readBlotterMessage(msg('view',{view:{...newBlotterView(),snapshotId:'private'}}),S));
 assert.throws(()=>readBlotterMessage(msg('pageReport',{state:{...futures,figure:'1'}}),S));
 assert.throws(()=>readBlotterMessage(msg('pageReport',{state:{...futures,view:{...selection,pv:'1'}}}),S));
});
test('a view that the saved-view rules reject is refused',()=>{
 for(const patch of [{columns:[]},{mode:'guess'},{version:5},{columns:['unknown']}])
  assert.throws(()=>readBlotterMessage(msg('view',{view:{...newBlotterView(),...patch}}),S),JSON.stringify(patch));
});
test('malformed payloads are refused',()=>{
 const ctx=valid.context.context;
 for(const bad of [{...ctx,snapshotId:1},{...ctx,contextId:7},{...ctx,profile:undefined},{snapshotId:'s'}])assert.throws(()=>readBlotterMessage(msg('context',{context:bad}),S));
 assert.throws(()=>readBlotterMessage(msg('ended',{reason:'bored'}),S));
 assert.throws(()=>readBlotterMessage(msg('askAbout',{key:3}),S));
 assert.throws(()=>readBlotterMessage(msg('view',{view:null}),S));
 for(const state of [{...futures,lens:'greeks'},{...futures,risk:'no'},{...futures,trade:{tradeId:'t'}},{...futures,view:{...selection,mode:'x'}},{...futures,entry:{product:'bonds'}},{...futures,pane:undefined},{...futures,pane:{details:true}},{...futures,pane:{details:true,charts:'no'}},{...futures,poppedOut:1},{page:'nowhere'},{page:'market',curve:null,tab:'x'},{page:'market',curve:null},null])
  assert.throws(()=>readBlotterMessage(msg('pageReport',{state}),S),JSON.stringify(state));
});
test('every page state shape is accepted exactly',()=>{
 const trade={tradeId:'t',instrumentType:'future',revision:1,reference:'F1'};
 for(const state of [{page:'market',curve:'c',tab:'inputs'},{page:'power',frame:true},{page:'configuration'},{page:'schedules'},{page:'reference',table:'t',search:''},{page:'parties',kind:'k'},
  {...futures,risk:true,trade,entry:{product:'swaps'},lens:'risk'},{page:'jobs',job:null},{page:'ppa',trade:{tradeId:'t',revision:2},book:null},{page:'custom-curves',curve:null,unsaved:true},
  ...['delivery','interval-series','interval-curves','admin','ai'].map(page=>({page}))])
  assert.deepEqual(readBlotterMessage(msg('pageReport',{state}),S).state,state,state.page);
});

// A BroadcastChannel in one realm: posts reach every other open channel of the same name.
const open=new Set();
class FakeChannel{
 constructor(name){this.name=name;this.closed=false;this.onmessage=null;this.onmessageerror=null;open.add(this);}
 postMessage(data){if(this.closed)throw new Error('closed');for(const c of open)if(c!==this&&c.name===this.name&&c.onmessage)c.onmessage({data});}
 close(){this.closed=true;open.delete(this);}
}
test('the channel delivers valid messages and drops refused ones',()=>{
 globalThis.BroadcastChannel=FakeChannel;
 const got=[],raw=new FakeChannel('openetrm-blotter'),ch=openBlotterChannel(S,m=>got.push(m));
 raw.postMessage(msg('opened'));
 raw.postMessage(msg('opened',{session:'other'}));raw.postMessage(msg('view',{view:{...newBlotterView(),columns:[]}}));raw.postMessage(msg('opened',{extra:1}));raw.postMessage('junk');
 assert.deepEqual(got,[msg('opened')]);
 ch.close();raw.close();
});
test('a receiver that throws, and a send of an invalid message, do not break the channel',()=>{
 globalThis.BroadcastChannel=FakeChannel;
 let n=0;const raw=new FakeChannel('openetrm-blotter'),ch=openBlotterChannel(S,()=>{n++;throw new Error('boom');});
 raw.postMessage(msg('opened'));raw.postMessage(msg('closed'));
 assert.equal(n,2);
 const peer=openBlotterChannel(S,m=>assert.fail('delivered '+m.kind));
 assert.throws(()=>peer.send(msg('opened',{session:'other'})));
 peer.close();ch.close();raw.close();
});
test('send reaches the other window and close stops delivery',()=>{
 globalThis.BroadcastChannel=FakeChannel;
 const a=[],b=[],ca=openBlotterChannel(S,m=>a.push(m)),cb=openBlotterChannel(S,m=>b.push(m));
 ca.send(msg('askAbout'));
 assert.deepEqual(b,[msg('askAbout')]);assert.deepEqual(a,[]);
 cb.close();ca.send(msg('closed'));
 assert.equal(b.length,1);
 ca.close();
});

// Review round 1
test('a view grid with extra fields is refused (1)',()=>{
 const v=newBlotterView();
 for(const grid of [{...v.grid,pv:'123.45'},{...v.grid,sort:[{key:'book',direction:'asc',pv:'1'}]},{...v.grid,widths:{book:'12'}}])
  assert.throws(()=>readBlotterMessage(msg('view',{view:{...v,grid}}),S),JSON.stringify(grid));
});
test('the message read is a new object of checked values, never the received one (2)',()=>{
 assert.throws(()=>readBlotterMessage(msg('view',{view:{...newBlotterView(),query:new Date(0)}}),S));
 assert.throws(()=>readBlotterMessage(msg('view',{view:{...newBlotterView(),columnFilters:new Map()}}),S));
 const input=msg('pageReport'),out=readBlotterMessage(input,S);
 assert.notEqual(out,input);assert.notEqual(out.state,input.state);assert.notEqual(out.state.view,input.state.view);
 const sent=msg('view'),read=readBlotterMessage(sent,S);
 assert.notEqual(read.view,sent.view);assert.notEqual(read.view.grid,sent.view.grid);
});
test('lists with extra properties or holes are refused (3)',()=>{
 for(const bookIds of [new Array(1),Object.assign(['a'],{pv:'1'}),Object.assign([],{pv:'1'})])assert.throws(()=>readBlotterMessage(msg('pageReport',{state:{...futures,view:{...selection,bookIds}}}),S));
});
test('revisions are positive safe whole numbers (4)',()=>{
 const trade=r=>({tradeId:'t',instrumentType:'future',revision:r,reference:'F'});
 for(const r of [1e300,-1,0,1.5,'1'])assert.throws(()=>readBlotterMessage(msg('pageReport',{state:{...futures,trade:trade(r)}}),S),String(r));
 assert.throws(()=>readBlotterMessage(msg('pageReport',{state:{page:'ppa',trade:{tradeId:'t',revision:0},book:null}}),S));
});
test('text is bounded and the knowledge mode must be known (5)',()=>{
 const long='x'.repeat(201),ctx=valid.context.context;
 assert.throws(()=>readBlotterMessage(msg('askAbout',{key:long}),S));
 assert.throws(()=>readBlotterMessage(msg('context',{context:{...ctx,snapshotId:long}}),S));
 assert.throws(()=>readBlotterMessage(msg('pageReport',{state:{page:'market',curve:long,tab:'prices'}}),S));
 assert.throws(()=>readBlotterMessage(msg('context',{context:{...ctx,knowledgeMode:'latest'}}),S));
 assert.throws(()=>readBlotterMessage(msg('context',{context:{...ctx,knowledgeMode:'retained'}}),S));
 assert.throws(()=>readBlotterMessage(msg('context',{context:{...ctx,contextId:'c1'}}),S));
 assert.equal(readBlotterMessage(msg('context',{context:{...ctx,knowledgeMode:'retained',contextId:'c1'}}),S).context.contextId,'c1');
});
test('a receiver that throws is reported, a refused message is not (6)',()=>{
 globalThis.BroadcastChannel=FakeChannel;
 const logged=[],original=console.error;console.error=(...a)=>logged.push(a);
 try{
  const raw=new FakeChannel('openetrm-blotter'),ch=openBlotterChannel(S,()=>{throw new Error('boom');});
  raw.postMessage(msg('opened',{session:'other'}));assert.equal(logged.length,0);
  raw.postMessage(msg('opened'));assert.equal(logged.length,1);assert.match(String(logged[0][1]),/boom/);
  ch.close();raw.close();
 }finally{console.error=original;}
});
test('only own properties count as fields (7)',()=>{
 const inherited=Object.create({key:'k'});Object.assign(inherited,{v:1,session:S,kind:'askAbout'});
 assert.throws(()=>readBlotterMessage(inherited,S));
});

// Review round 2
test('a saved view with a long valid pivot column key is accepted (R2-1)',()=>{
 const id='a'.repeat(80),key='pivot:'+JSON.stringify([JSON.stringify([id,id,id]),'lots']);
 assert.ok(key.length>200&&key.length<=512);
 const view={...newBlotterView(),grid:{order:[key],widths:{[key]:120},sort:[{key,direction:'asc'}]}};
 assert.deepEqual(readBlotterMessage(msg('view',{view}),S).view.grid,view.grid);
 assert.throws(()=>readBlotterMessage(msg('view',{view:{...view,grid:{...view.grid,order:['bogus']}}}),S));
});
test('v, session and kind count only as own properties (R2-2)',()=>{
 const base=Object.create({v:1,session:S,kind:'opened'});
 assert.throws(()=>readBlotterMessage(base,S));
 const partly=Object.assign(Object.create({kind:'opened'}),{v:1,session:S});
 assert.throws(()=>readBlotterMessage(partly,S));
});
test('a retained context needs a non-empty context id (R2-3)',()=>{
 const ctx={...valid.context.context,knowledgeMode:'retained'};
 assert.throws(()=>readBlotterMessage(msg('context',{context:{...ctx,contextId:''}}),S));
});
