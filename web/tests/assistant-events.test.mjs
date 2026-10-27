import {test} from 'node:test';
import assert from 'node:assert/strict';
import {streamFrame,readEvents,newTurnView,applyEvent,streamOrder} from '../dist/assistant-events.js';

const turnId='7e1a0c3e-4b5d-4f6a-8b9c-0d1e2f3a4b5c',agentId='a1000000-0000-4000-8000-000000000001';
const context={mode:'current',contextId:'c1000000-0000-4000-8000-000000000001',frameId:'f1000000-0000-4000-8000-000000000001'};
const target={page:'curves',selection:{view:'curve',curve:'future'},context,kind:'record',label:'Curves & prices: curve future'};
const jobTarget={page:'jobs',selection:{view:'history',job:'a7000000-0000-0000-0000-000000000003',page:null,before:null},context,kind:'record',label:'Jobs & Infrastructure: job history'};
const usage={calls:2,uncachedInput:{count:120,unknownCalls:0},cacheRead:{count:0,unknownCalls:0},cacheWrite:{count:0,unknownCalls:0},
  output:{count:40,unknownCalls:0},reasoning:{count:4,unknownCalls:0},
  cost:[{currency:{catalogId:'e1000000-0000-4000-8000-000000000001',revisionId:'e2000000-0000-4000-8000-000000000002',code:'usd'},amount:'0.000542',unpricedCalls:0}]};
const frame=(name,id,payload,extra={})=>streamFrame(name,id,JSON.stringify({turnId,hidden:null,...extra,payload}));

test('a stored event parses only under its fixed name, with its sequence, turn, hidden marker and exact payload',()=>{
  assert.deepEqual(frame('accepted','4',{ordinal:1,agentId,version:3}),{sequence:4,turnId,kind:'accepted',hidden:null,incomplete:null,payload:{ordinal:1,agentId,version:3}});
  assert.deepEqual(frame('text','5',{text:'Prices rose.'}).payload,{text:'Prices rose.'});
  assert.deepEqual(frame('step','6',{ordinal:1,tool:'read_market_data'}).payload,{ordinal:1,tool:'read_market_data'});
  assert.deepEqual(frame('step','7',{ordinal:2,tool:'navigate',code:'tool_arguments_invalid'}).payload.code,'tool_arguments_invalid');
  assert.deepEqual(frame('step','3',{withheld:1}).payload,{withheld:1});
  assert.deepEqual(frame('navigation','8',{ordinal:3,target}).payload.target,target);
  assert.deepEqual(frame('navigation','9',{ordinal:4,target:jobTarget}).payload.target,jobTarget);
  assert.deepEqual(frame('usage','9',usage).payload,usage);
  assert.deepEqual(frame('completed','10',{},{incomplete:false}),{sequence:10,turnId,kind:'completed',hidden:null,incomplete:false,payload:{}});
  assert.equal(frame('failed','11',{code:'limit_elapsed'},{incomplete:true}).payload.code,'limit_elapsed');
  assert.deepEqual(frame('text','12',{hidden:'access_changed'},{hidden:'access_changed'}).payload,{hidden:'access_changed'});
  assert.deepEqual(frame('navigation','13',{hidden:'access_changed'},{hidden:'access_changed'}).payload,{hidden:'access_changed'});
});

test('replaced and refused carry no id; refused names its code and message',()=>{
  assert.deepEqual(streamFrame('replaced',null,'{}'),{kind:'replaced'});
  assert.deepEqual(streamFrame('refused',null,JSON.stringify({code:'replay_unavailable',message:'The stream cannot resume from there.'})),
    {kind:'refused',code:'replay_unavailable',message:'The stream cannot resume from there.'});
});

test('anything outside the fixed names and payloads is refused, so the panel closes the stream and reads the turn again',()=>{
  const invalid=[
    ()=>frame('card','5',{}),()=>frame('text','05',{text:'x'}),()=>frame('text','',{text:'x'}),()=>frame('text',null,{text:'x'}),
    ()=>frame('text','5',{text:1}),()=>frame('text','5',{text:'x',extra:1}),()=>streamFrame('text','5',JSON.stringify({turnId,hidden:null,payload:{text:'x'},more:1})),
    ()=>streamFrame('text','5',JSON.stringify({turnId:'not-a-turn',hidden:null,payload:{text:'x'}})),()=>frame('text','5',{text:'x'},{hidden:'elsewhere'}),
    ()=>frame('completed','10',{}),()=>frame('text','5',{text:'x'},{incomplete:false}),()=>frame('completed','10',{code:'Bad code'},{incomplete:true}),
    ()=>frame('step','6',{ordinal:0,tool:'read_market_data'}),()=>frame('step','6',{ordinal:1,tool:'delete_trades'}),
    ()=>frame('navigation','8',{ordinal:3,target:{...target,page:'settings_ai'}}),()=>frame('navigation','8',{ordinal:3,target:{...target,kind:'page'}}),
    ()=>frame('navigation','8',{ordinal:3,target:{...target,context:{...context,mode:'elsewhere'}}}),()=>frame('navigation','8',{ordinal:3,target:{...target,href:'/x'}}),
    ()=>frame('usage','9',{...usage,cost:[{...usage.cost[0],amount:5}]}),()=>streamFrame('replaced',null,'{"x":1}'),
    ()=>streamFrame('refused',null,JSON.stringify({code:'replay_unavailable'})),()=>streamFrame('text','5','not json'),
    ()=>streamFrame('replaced','4','{}')];
  for(const parse of invalid)assert.throws(parse,/stream sent an event the panel cannot read/);
});

test('a turn view joins its text in order, drops a repeated sequence, and keeps steps, targets, usage and the ending',()=>{
  let view=newTurnView({id:turnId,ordinal:1,question:'What moved?'});
  for(const event of [frame('accepted','4',{ordinal:1,agentId,version:3}),frame('text','5',{text:'Prices '}),frame('step','6',{ordinal:1,tool:'read_market_data'}),
    frame('navigation','7',{ordinal:1,target}),frame('text','8',{text:'rose.'}),frame('text','8',{text:'rose.'}),frame('text','5',{text:'Prices '}),
    frame('usage','9',usage),frame('completed','10',{},{incomplete:false})])view=applyEvent(view,event);
  assert.equal(view.text,'Prices rose.');
  assert.deepEqual(view.steps,[{ordinal:1,tool:'read_market_data',code:null}]);
  assert.deepEqual(view.targets,[{ordinal:1,sequence:7,target}]);
  assert.deepEqual(view.usage,usage);
  assert.deepEqual(view.ending,{state:'completed',code:null,incomplete:false});
  assert.equal(view.lastSequence,10);
  assert.equal(view.hidden,null);
});

test('once an event says the answer is hidden, its text and targets are hidden from then on, and its steps stay',()=>{
  let view=newTurnView({id:turnId,ordinal:2,question:'And now?'});
  view=applyEvent(view,frame('text','5',{text:'Secret '}));
  view=applyEvent(view,frame('navigation','6',{ordinal:1,target}));
  view=applyEvent(view,frame('step','7',{ordinal:2,tool:'read_trades'},{hidden:'access_changed'}));
  view=applyEvent(view,frame('text','8',{text:'more'}));
  assert.equal(view.hidden,'access_changed');assert.equal(view.text,'');assert.deepEqual(view.targets,[]);assert.equal(view.steps.length,1);
});

test('a conversation read becomes the same events, each turn with its own hidden marker and the terminal its incompleteness',()=>{
  const read={conversation:{id:'b1000000-0000-4000-8000-000000000001',agentId,createdAt:'2026-10-22T10:00:00Z',readThrough:0},
    turns:[{id:turnId,ordinal:1,question:'What moved?',state:'failed',incomplete:true,hidden:null,answer:'Partial'}],
    events:[{sequence:4,turnId,kind:'accepted',payload:{ordinal:1,agentId,version:3},at:'2026-10-22T10:00:00Z'},
      {sequence:5,turnId,kind:'text',payload:{text:'Partial'},at:'2026-10-22T10:00:01Z'},
      {sequence:6,turnId,kind:'failed',payload:{code:'limit_elapsed'},at:'2026-10-22T10:00:02Z'}]};
  const events=readEvents(read);
  assert.deepEqual(events.map(e=>[e.sequence,e.kind,e.hidden,e.incomplete]),[[4,'accepted',null,null],[5,'text',null,null],[6,'failed',null,true]]);
  assert.throws(()=>readEvents({...read,events:[{...read.events[1],turnId:'c2000000-0000-4000-8000-000000000002'}]}),/stream sent an event the panel cannot read/);
});

test('a stream reads its events in sequence: one already read is a repeat, and one past a gap needs a fresh read',()=>{
  assert.equal(streamOrder(5,6),'next');
  assert.equal(streamOrder(5,5),'repeat');assert.equal(streamOrder(5,2),'repeat');
  assert.equal(streamOrder(5,7),'gap');assert.equal(streamOrder(0,1),'next');
});
