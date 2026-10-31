import test from 'node:test';
import assert from 'node:assert/strict';
import {readBlotterMessage,openBlotterChannel} from '../dist/blotter-channel.js';
import {newBlotterView} from '../dist/blotter-layout.js';

const S='session-a';
const selection={mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[]};
const futures={page:'futures',risk:false,trade:null,entry:null,lens:'values',view:selection};
const valid={
 context:{kind:'context',context:{snapshotId:'snap-1',knowledgeMode:'current',contextId:null,profile:'p1'}},
 view:{kind:'view',view:newBlotterView()},
 opened:{kind:'opened'},closed:{kind:'closed'},
 ended:{kind:'ended',reason:'signed_out'},
 pageReport:{kind:'pageReport',state:futures},
 askAbout:{kind:'askAbout',key:'t1:b1'},
};
const msg=(kind,extra={})=>({v:1,session:S,...valid[kind],...extra});

test('one valid message per kind is accepted and returned as sent',()=>{
 for(const kind of Object.keys(valid))assert.deepEqual(readBlotterMessage(msg(kind),S),msg(kind),kind);
 for(const reason of ['signed_out','account_changed','main_closed'])assert.equal(readBlotterMessage(msg('ended',{reason}),S).reason,reason);
});
test('another session id, another version and an unknown kind are refused',()=>{
 assert.throws(()=>readBlotterMessage(msg('opened',{session:'session-b'}),S));
 assert.throws(()=>readBlotterMessage(msg('opened',{v:2}),S));
 assert.throws(()=>readBlotterMessage(msg('opened',{kind:'shutdown'}),S));
 for(const bad of [null,undefined,'opened',7,[],{}])assert.throws(()=>readBlotterMessage(bad,S));
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
 for(const state of [{...futures,lens:'greeks'},{...futures,risk:'no'},{...futures,trade:{tradeId:'t'}},{...futures,view:{...selection,mode:'x'}},{...futures,entry:{product:'bonds'}},{page:'nowhere'},{page:'market',curve:null,tab:'x'},{page:'market',curve:null},null])
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
