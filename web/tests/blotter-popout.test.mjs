import test from 'node:test';
import assert from 'node:assert/strict';
import {tieMainWindow,tiePopoutWindow,blotterWindowUrl,readBlotterWindowUrl} from '../dist/blotter-popout.js';
import {openBlotterChannel} from '../dist/blotter-channel.js';
import {newBlotterView} from '../dist/blotter-layout.js';

// Both ends talk over the real BroadcastChannel, which delivers to the other channels of its name a moment later.
const settle=()=>new Promise(resolve=>setTimeout(resolve,30));
const SESSION='6f1c2a7e-3b4d-4c5e-8f60-718293a4b5c6';
const context={snapshotId:'snap-1',knowledgeMode:'current',contextId:null,profile:'run:tenant:workspace:trader'};
const handed={...newBlotterView(),rowGroup:'book'},grouped={...newBlotterView(),rowGroup:'market'};
const selection={mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[]};
const report={page:'futures',risk:false,trade:null,entry:null,lens:'values',view:selection,pane:{details:false,charts:true},poppedOut:true};

function mainWindow(over={}){
  const calls={opened:[],returned:[],reported:0,asked:[]},win={closed:false,closes:0,fronted:0,close(){this.closes++;this.closed=true;},focus(){this.fronted++;}};
  let shown=context;
  const tie=tieMainWindow({source:'http://127.0.0.1:5173/?example=exchange',example:'exchange',grace:150,session:()=>SESSION,channel:openBlotterChannel,
    open:(url,target,features)=>{calls.opened.push({url,target,features});return win;},context:()=>shown,
    returned:(view,handedBack)=>calls.returned.push({view,handedBack}),reported:()=>calls.reported++,ask:question=>calls.asked.push(question),...over});
  return {tie,calls,win,show:next=>{shown=next;}};
}
// The other end, as a raw channel of the session: what it hears, and what it says.
function peer(){
  const heard=[],channel=openBlotterChannel(SESSION,m=>heard.push(m));
  return {heard,send:(kind,fields={})=>channel.send({v:1,session:SESSION,kind,...fields}),close:()=>channel.close()};
}

test('a pop-out URL names its session and example in the fragment only, and reads back exactly',()=>{
  const url=new URL(blotterWindowUrl('http://127.0.0.1:5173/?example=exchange#x',{session:SESSION,example:'exchange'}));
  assert.equal(url.pathname,'/blotter.html');assert.equal(url.search,'');
  assert.deepEqual(readBlotterWindowUrl(url.href),{session:SESSION,example:'exchange'});
  for(const bad of ['/blotter.html','/blotter.html?session='+SESSION+'#session='+SESSION+'&example=baseline','/blotter.html#session=nope&example=baseline',
    '/blotter.html#session='+SESSION,'/blotter.html#session='+SESSION+'&example=other','/blotter.html#session='+SESSION+'&example=baseline&extra=1',
    '/blotter.html#session='+SESSION+'&session='+SESSION+'&example=baseline'])
    assert.throws(()=>readBlotterWindowUrl('http://127.0.0.1:5173'+bad),bad);
});

test('popping out opens the Blotter window for a fresh session, and refuses without a context, twice, or when the browser opens nothing',async()=>{
  const {tie,calls}=mainWindow();
  tie.open(handed);
  assert.equal(tie.out(),true);assert.equal(calls.opened.length,1);
  assert.deepEqual(readBlotterWindowUrl(calls.opened[0].url),{session:SESSION,example:'exchange'});assert.equal(calls.opened[0].target,'_blank');
  assert.match(calls.opened[0].features,/popup/);assert.doesNotMatch(calls.opened[0].features,/noopener/);
  assert.throws(()=>tie.open(handed),/already in a window of its own/);
  tie.end('main_closed');
  // A view the other window could not read is refused before any window opens.
  const unreadable=mainWindow();
  assert.throws(()=>unreadable.tie.open({...handed,columns:[]}),/cannot be handed/);assert.equal(unreadable.tie.out(),false);assert.equal(unreadable.calls.opened.length,0);
  const none=mainWindow({context:()=>undefined});
  assert.throws(()=>none.tie.open(handed),/market date/);assert.equal(none.tie.out(),false);assert.equal(none.calls.opened.length,0);
  const blocked=mainWindow({open:()=>null});
  assert.throws(()=>blocked.tie.open(handed),/did not open/);assert.equal(blocked.tie.out(),false);
  // A blocked window leaves no channel listening for its session.
  const other=peer();other.send('opened',{instance:'w1'});await settle();assert.deepEqual(other.heard,[]);other.close();
});

test('the main window adopts the first pop-out that says it opened, hands it the view and context, and hears no other',async()=>{
  const {tie,calls}=mainWindow(),pop=peer();
  tie.open(handed);pop.send('opened',{instance:'w1'});await settle();
  assert.deepEqual(pop.heard.map(m=>m.kind),['view','context']);
  assert.deepEqual(pop.heard[0],{v:1,session:SESSION,kind:'view',instance:'w1',view:handed});
  assert.deepEqual(pop.heard[1].context,context);
  pop.heard.length=0;
  pop.send('opened',{instance:'w2'});pop.send('pageReport',{instance:'w2',state:report,edits:false,requests:false});pop.send('askAbout',{instance:'w2',key:'t:b',bookIds:[],portfolioIds:[]});pop.send('closed',{instance:'w2'});
  // The main window acts on nothing it sends itself.
  pop.send('context',{context});pop.send('ended',{reason:'signed_out'});
  await settle();
  assert.deepEqual(pop.heard,[]);assert.equal(tie.out(),true);assert.equal(calls.reported,0);assert.deepEqual(calls.asked,[]);assert.equal(tie.shown(),undefined);
  tie.end('main_closed');pop.close();
});

test('the context is sent again only when it changes, and only to an adopted pop-out',async()=>{
  const {tie,show}=mainWindow(),pop=peer();
  tie.open(handed);tie.follow();await settle();assert.deepEqual(pop.heard,[]);
  pop.send('opened',{instance:'w1'});await settle();pop.heard.length=0;
  tie.follow();await settle();assert.deepEqual(pop.heard,[]);
  const later={...context,snapshotId:'snap-2'};show(later);tie.follow();tie.follow();await settle();
  assert.deepEqual(pop.heard.map(m=>m.context),[later]);
  // A context the main window cannot state now (an unapplied selection) leaves the pop-out on the last one.
  show(undefined);tie.follow();await settle();assert.equal(pop.heard.length,1);
  tie.end('main_closed');pop.close();
});

test('the adopted pop-out\'s report is relayed only as a popped-out Blotter, and its question is passed on',async()=>{
  const {tie,calls}=mainWindow(),pop=peer();
  tie.open(handed);pop.send('opened',{instance:'w1'});await settle();
  // Until it has reported, the main window cannot say what the pop-out holds.
  assert.equal(tie.held(),undefined);
  pop.send('pageReport',{instance:'w1',state:report,edits:true,requests:false});await settle();
  assert.deepEqual(tie.shown(),report);assert.deepEqual(tie.held(),{edits:true,requests:false});assert.equal(calls.reported,1);
  pop.send('pageReport',{instance:'w1',state:{...report,poppedOut:false},edits:false,requests:false});pop.send('pageReport',{instance:'w1',state:{page:'market',curve:null,tab:'prices'},edits:false,requests:false});await settle();
  assert.deepEqual(tie.shown(),report);assert.deepEqual(tie.held(),{edits:true,requests:false});assert.equal(calls.reported,1);
  pop.send('pageReport',{instance:'w1',state:report,edits:false,requests:true});await settle();assert.deepEqual(tie.held(),{edits:false,requests:true});
  pop.send('askAbout',{instance:'w1',key:'trade:binding',bookIds:['book'],portfolioIds:[]});await settle();
  assert.deepEqual(calls.asked,[{key:'trade:binding',bookIds:['book'],portfolioIds:[]}]);
  tie.end('main_closed');pop.close();
});

test('a pop-out that closes hands back its view, or the main window keeps the one it handed',async()=>{
  const first=mainWindow(),pop=peer();
  first.tie.open(handed);pop.send('opened',{instance:'w1'});await settle();
  pop.send('view',{instance:'w1',view:grouped});pop.send('closed',{instance:'w1'});await settle();
  assert.deepEqual(first.calls.returned,[{view:grouped,handedBack:true}]);assert.equal(first.tie.out(),false);assert.equal(first.tie.shown(),undefined);
  // The tie is over: nothing more is heard.
  pop.send('closed',{instance:'w1'});await settle();assert.equal(first.calls.returned.length,1);
  const second=mainWindow();
  second.tie.open(handed);pop.send('opened',{instance:'w3'});await settle();pop.send('closed',{instance:'w3'});await settle();
  assert.deepEqual(second.calls.returned,[{view:handed,handedBack:false}]);
  pop.close();
});

test('Bring back asks the pop-out to come back and settles once it has handed back, at once when it is already gone, and refuses when it stays open',async()=>{
  const open=mainWindow(),pop=peer();
  open.tie.open(handed);pop.send('opened',{instance:'w1'});await settle();pop.heard.length=0;
  // An adopted pop-out is told why it ends, and closes itself: the main window has asked about its edits, which it never asks again.
  const back=open.tie.bringBack();await settle();
  assert.deepEqual(pop.heard.map(m=>[m.kind,m.reason]),[['ended','brought_back']]);assert.equal(open.win.closes,0);
  pop.send('view',{instance:'w1',view:grouped});pop.send('closed',{instance:'w1'});
  await back;assert.deepEqual(open.calls.returned,[{view:grouped,handedBack:true}]);
  await assert.rejects(open.tie.bringBack(),/not in a window of its own/);
  const gone=mainWindow();gone.tie.open(handed);gone.win.closed=true;
  await gone.tie.bringBack();assert.deepEqual(gone.calls.returned,[{view:handed,handedBack:false}]);assert.equal(gone.tie.out(),false);
  // A window that keeps itself open, as one asking about its edits does, keeps the Blotter.
  const kept=mainWindow();kept.win.close=function(){this.closes++;};kept.tie.open(handed);
  await assert.rejects(kept.tie.bringBack(),/stayed open/);assert.equal(kept.tie.out(),true);assert.deepEqual(kept.calls.returned,[]);
  // A window that closed without a word gives the Blotter back with the view the main window handed.
  const silent=mainWindow();silent.tie.open(handed);
  await silent.tie.bringBack();assert.deepEqual(silent.calls.returned,[{view:handed,handedBack:false}]);
  kept.tie.end('main_closed');pop.close();
});

test('ending tells every pop-out of the session why, without closing its window or taking a view back',async()=>{
  const {tie,calls,win}=mainWindow(),pop=peer();
  tie.open(handed);tie.end('account_changed');await settle();
  assert.deepEqual(pop.heard.map(m=>[m.kind,m.reason]),[['ended','account_changed']]);
  assert.equal(tie.out(),false);assert.equal(win.closes,0);assert.deepEqual(calls.returned,[]);
  tie.end('signed_out');await settle();assert.equal(pop.heard.length,1);
  tie.front();assert.equal(win.fronted,0);
  pop.close();
});

test('the main window brings its pop-out to the front',()=>{
  const {tie,win}=mainWindow();tie.open(handed);tie.front();assert.equal(win.fronted,1);tie.end('main_closed');
});

function popoutWindow(instance='w1'){
  const calls={handed:[],contexts:[],ended:[],back:0};
  const tie=tiePopoutWindow({session:SESSION,instance,channel:openBlotterChannel,handed:view=>calls.handed.push(view),context:c=>calls.contexts.push(c),ended:reason=>calls.ended.push(reason),broughtBack:()=>calls.back++});
  return {tie,calls};
}

test('a pop-out says it opened, takes only its own hand-off once, follows every context and ends when told',async()=>{
  const main=peer(),{tie,calls}=popoutWindow();await settle();
  assert.deepEqual(main.heard,[{v:1,session:SESSION,kind:'opened',instance:'w1'}]);
  main.send('view',{instance:'w2',view:grouped});main.send('view',{instance:'w1',view:handed});main.send('view',{instance:'w1',view:grouped});
  main.send('context',{context});
  // A pop-out acts on nothing a pop-out says.
  main.send('opened',{instance:'w2'});main.send('closed',{instance:'w2'});main.send('pageReport',{instance:'w2',state:report,edits:false,requests:false});main.send('askAbout',{instance:'w2',key:'k',bookIds:[],portfolioIds:[]});
  await settle();
  assert.deepEqual(calls.handed,[handed]);assert.deepEqual(calls.contexts,[context]);assert.deepEqual(calls.ended,[]);
  main.heard.length=0;
  tie.report(report,{edits:false,requests:false});tie.report(report,{edits:false,requests:false});tie.report(report,{edits:true,requests:false});tie.ask('trade:binding',{bookIds:['book'],portfolioIds:[]});await settle();
  assert.deepEqual(main.heard.map(m=>m.kind),['pageReport','pageReport','askAbout']);assert.equal(main.heard[0].instance,'w1');assert.deepEqual(main.heard[0].state,report);
  assert.deepEqual(main.heard.map(m=>m.edits),[false,true,undefined]);assert.deepEqual(main.heard[2].bookIds,['book']);
  main.heard.length=0;
  main.send('ended',{reason:'main_closed'});await settle();
  assert.deepEqual(calls.ended,['main_closed']);
  main.send('context',{context});tie.report({...report,lens:'risk'},{edits:false,requests:false});tie.ask('k',{bookIds:[],portfolioIds:[]});tie.leave(grouped);await settle();
  assert.deepEqual(calls.contexts.length,1);assert.deepEqual(main.heard,[]);
  main.close();
});

test('a pop-out brought back stays tied until it has handed back its view',async()=>{
  const main=peer(),{tie,calls}=popoutWindow();await settle();main.heard.length=0;
  main.send('ended',{reason:'brought_back'});await settle();
  assert.equal(calls.back,1);assert.deepEqual(calls.ended,[]);
  tie.leave(grouped);await settle();
  assert.deepEqual(main.heard.map(m=>m.kind),['view','closed']);
  main.close();
});
test('a pop-out leaving hands back its view and says it closed, once',async()=>{
  const main=peer(),{tie}=popoutWindow();await settle();main.heard.length=0;
  tie.leave(grouped);tie.leave(grouped);await settle();
  assert.deepEqual(main.heard.map(m=>[m.kind,m.instance]),[['view','w1'],['closed','w1']]);assert.deepEqual(main.heard[0].view,grouped);
  const other=popoutWindow('w2');await settle();main.heard.length=0;
  other.tie.leave(undefined);await settle();
  assert.deepEqual(main.heard.map(m=>m.kind),['closed']);
  main.close();
});

test('the two ends tie together: hand-off, a report, and the view handed back',async()=>{
  const main=mainWindow();main.tie.open(handed);
  const pop=popoutWindow();await settle();
  assert.deepEqual(pop.calls.handed,[handed]);assert.deepEqual(pop.calls.contexts,[context]);
  pop.tie.report(report,{edits:false,requests:true});await settle();assert.deepEqual(main.tie.shown(),report);assert.deepEqual(main.tie.held(),{edits:false,requests:true});
  pop.tie.leave(grouped);await settle();
  assert.deepEqual(main.calls.returned,[{view:grouped,handedBack:true}]);
});
