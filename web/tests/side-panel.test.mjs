import test from 'node:test';
import assert from 'node:assert/strict';
import {panelPlacement,sameMode,setupSidePanel,tabAfterKey} from '../dist/side-panel.js';

test('the panel docks beside the desk only when the main area is wide and the assistant is closed',()=>{
 assert.equal(panelPlacement(1200,false),'docked');
 assert.equal(panelPlacement(1200,true),'overlay');
 assert.equal(panelPlacement(900,false),'overlay');
 assert.equal(panelPlacement(1180,false),'docked');
 assert.equal(panelPlacement(1179,false),'overlay');
});

test('modes are the same when they name the same thing',()=>{
 const position={kind:'position',tradeId:'t1',revision:2};
 assert.ok(sameMode(null,null));
 assert.ok(sameMode(position,{...position}));
 assert.ok(!sameMode(position,{...position,revision:3}));
 assert.ok(!sameMode(position,{...position,tradeId:'t2'}));
 assert.ok(!sameMode(position,null));
 assert.ok(!sameMode({kind:'new',product:'futures'},{kind:'new',product:'swaps'}));
 assert.ok(sameMode({kind:'risk'},{kind:'risk'}));
 assert.ok(!sameMode({kind:'risk'},position));
});

// What the shell touches of its markup, as plain objects: the panel's own element and the parts index.html gives it.
function markup(){
 const part=()=>({hidden:false,onclick:null,focused:false,children:[],replaceChildren(...nodes){this.children=nodes;}});
 const parts={close:part(),head:part(),tabs:part(),body:part(),prompt:part()};
 const handlers={};
 const host={hidden:true,dataset:{},focused:0,labels:[],focus(){host.focused++;},setAttribute(name,value){if(name==='aria-label')host.labels.push(value);},addEventListener(type,handler){handlers[type]=handler;},querySelector:selector=>parts[selector.replace('#side-panel-','')]};
 return {host,parts,handlers};
}
const position=(tradeId,revision=1)=>({kind:'position',tradeId,revision});
function panel({unsaved=false,answer=true,docked=true}={}){
 const {host,parts,handlers}=markup(),asked=[],closed=[];
 const shell=setupSidePanel(host,{unsaved:()=>unsaved,confirmLeave:async()=>{asked.push('asked');return answer;},docked:()=>docked,closed:()=>closed.push('closed')});
 return {shell,host,parts,handlers,asked,closed};
}

test('showing a position opens the panel, places it and moves focus into it',async()=>{
 const {shell,host}=panel();
 assert.equal(shell.current(),null);assert.equal(host.hidden,true);
 assert.equal(await shell.show(position('t1')),true);
 assert.deepEqual(shell.current(),position('t1'));
 assert.equal(host.hidden,false);assert.equal(host.dataset.mode,'position');assert.equal(host.dataset.placement,'docked');assert.equal(host.focused,1);
});

test('the panel is an overlay when the page says it cannot dock, and the placement follows the page',async()=>{
 const state={docked:false},{host,parts}=markup();
 const shell=setupSidePanel(host,{unsaved:()=>false,confirmLeave:async()=>true,docked:()=>state.docked});
 await shell.show({kind:'risk'});assert.equal(host.dataset.placement,'overlay');
 state.docked=true;shell.place();assert.equal(host.dataset.placement,'docked');
 assert.equal(shell.element('body'),parts.body);assert.equal(shell.element('tabs'),parts.tabs);assert.equal(shell.element('head'),parts.head);
});

test('showing the mode it already shows is not a switch: nothing is asked, and focus comes back into the panel',async()=>{
 const {shell,host,asked}=panel({unsaved:true});
 await shell.show(position('t1'));
 assert.equal(await shell.show(position('t1')),true);
 assert.deepEqual(asked,[]);assert.equal(host.focused,2);
});

test('switching modes with unsaved edits asks first, and keeps the current mode when the answer is no',async()=>{
 const state=panel({unsaved:true,answer:false});
 await state.shell.show(position('t1'));
 assert.equal(await state.shell.show(position('t2')),false);
 assert.deepEqual(state.shell.current(),position('t1'));
 assert.deepEqual(state.asked,['asked']);assert.equal(state.host.hidden,false);assert.deepEqual(state.closed,[]);
 assert.equal(await state.shell.show({kind:'new',product:'futures'}),false);
 assert.deepEqual(state.shell.current(),position('t1'));assert.deepEqual(state.asked,['asked','asked']);
});

test('switching modes with unsaved edits switches when the answer is yes',async()=>{
 const state=panel({unsaved:true,answer:true});
 await state.shell.show(position('t1'));
 assert.equal(await state.shell.show(position('t2')),true);
 assert.deepEqual(state.shell.current(),position('t2'));assert.deepEqual(state.asked,['asked']);
});

test('switching without unsaved edits never asks, and opening a closed panel has nothing to leave',async()=>{
 const state=panel({unsaved:false});
 await state.shell.show(position('t1'));await state.shell.show(position('t2'));
 assert.deepEqual(state.asked,[]);
 const other=panel({unsaved:true});
 assert.equal(await other.shell.show(position('t1')),true);
 assert.deepEqual(other.asked,[]);
});

test('closing is leaving: the person is asked, and a no keeps the panel open',async()=>{
 const state=panel({unsaved:true,answer:false});
 await state.shell.show(position('t1'));
 state.parts.close.onclick();
 await new Promise(resolve=>setImmediate(resolve));
 assert.deepEqual(state.asked,['asked']);assert.deepEqual(state.shell.current(),position('t1'));assert.deepEqual(state.closed,[]);
});

test('the close button and Escape close the panel and tell the page the person closed it',async()=>{
 const state=panel({unsaved:false});
 await state.shell.show(position('t1'));
 state.parts.close.onclick();
 await new Promise(resolve=>setImmediate(resolve));
 assert.equal(state.shell.current(),null);assert.equal(state.host.hidden,true);assert.deepEqual(state.closed,['closed']);
 await state.shell.show(position('t2'));
 let prevented=0;
 state.handlers.keydown({key:'Escape',defaultPrevented:false,preventDefault(){prevented++;}});
 await new Promise(resolve=>setImmediate(resolve));
 assert.equal(state.shell.current(),null);assert.equal(prevented,1);assert.deepEqual(state.closed,['closed','closed']);
});

test('the panel is named for its mode, and a closed panel has nobody to tell when its close button is pressed',async()=>{
 const state=panel();
 await state.shell.show(position('t1'));await state.shell.show({kind:'new',product:'swaps'});await state.shell.show({kind:'risk'});
 assert.deepEqual(state.host.labels,['Position','New trade','Risk report']);
 state.shell.reset();state.parts.close.onclick();
 await new Promise(resolve=>setImmediate(resolve));
 assert.deepEqual(state.closed,[]);
});

test('other keys, and an Escape another control already handled, leave the panel open',async()=>{
 const state=panel();
 await state.shell.show(position('t1'));
 state.handlers.keydown({key:'Enter',defaultPrevented:false,preventDefault(){}});
 state.handlers.keydown({key:'Escape',defaultPrevented:true,preventDefault(){}});
 await new Promise(resolve=>setImmediate(resolve));
 assert.deepEqual(state.shell.current(),position('t1'));
});

test('while the person is being asked, a second request is refused instead of asking again',async()=>{
 const {host}=markup();let release,asked=0;
 const shell=setupSidePanel(host,{unsaved:()=>true,confirmLeave:()=>{asked++;return new Promise(resolve=>{release=resolve;});},docked:()=>true});
 await shell.show(position('t1'));
 const first=shell.show(position('t2'));
 assert.equal(await shell.show(position('t3')),false);
 release(true);
 assert.equal(await first,true);
 assert.equal(asked,1);assert.deepEqual(shell.current(),position('t2'));
});

test('the page can reset the panel without asking, and an answer that arrives afterwards changes nothing',async()=>{
 const {host}=markup();let release,signal;
 const closed=[];
 const shell=setupSidePanel(host,{unsaved:()=>true,confirmLeave:abandoned=>{signal=abandoned;return new Promise(resolve=>{release=resolve;});},docked:()=>true,closed:()=>closed.push(1)});
 await shell.show(position('t1'));
 const pending=shell.show(position('t2'));
 shell.reset();
 assert.equal(signal.aborted,true);assert.equal(shell.current(),null);assert.equal(host.hidden,true);
 release(true);
 assert.equal(await pending,false);
 assert.equal(shell.current(),null);assert.equal(host.hidden,true);assert.deepEqual(closed,[]);
 shell.reset();assert.equal(shell.current(),null);
});

test('arrow keys move between tabs and wrap, Home and End go to the ends, other keys move nothing',()=>{
 const ids=['curve','terms','risk','history'];
 assert.equal(tabAfterKey(ids,'curve','ArrowRight'),'terms');
 assert.equal(tabAfterKey(ids,'history','ArrowRight'),'curve');
 assert.equal(tabAfterKey(ids,'curve','ArrowLeft'),'history');
 assert.equal(tabAfterKey(ids,'terms','ArrowLeft'),'curve');
 assert.equal(tabAfterKey(ids,'terms','Home'),'curve');
 assert.equal(tabAfterKey(ids,'terms','End'),'history');
 assert.equal(tabAfterKey(ids,'terms','Enter'),null);
 assert.equal(tabAfterKey(ids,'missing','ArrowRight'),null);
});
