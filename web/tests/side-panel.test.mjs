import test from 'node:test';
import assert from 'node:assert/strict';
import {isolate,liveRefreshPlan,modeLabel,panelPlacement,riskRefreshPlan,sameMode,setupSidePanel,tabAfterKey} from '../dist/side-panel.js';

test('the panel docks beside the desk only when the main area is wide and the assistant is closed',()=>{
 assert.equal(panelPlacement(1200,false),'docked');
 assert.equal(panelPlacement(1200,true),'overlay');
 assert.equal(panelPlacement(900,false),'overlay');
 assert.equal(panelPlacement(1180,false),'docked');
 assert.equal(panelPlacement(1179,false),'overlay');
});

test('a contract of a curve is its own mode, the same only for the same curve and contract',()=>{
 const contract={kind:'curve-contract',curve:'gas',contract:'c1'};
 assert.ok(sameMode(contract,{...contract}));
 assert.ok(!sameMode(contract,{...contract,contract:'c2'}));
 assert.ok(!sameMode(contract,{...contract,curve:'power'}));
 assert.ok(!sameMode(contract,{kind:'risk'}));
 assert.ok(!sameMode(contract,{kind:'contract',factorId:'c1'}),'a contract of a curve is not a shocked contract of the delta ladder');
 assert.equal(modeLabel(contract),'Contract');
});

test('a panel finds its parts by its own id, so a page can hold more than one',async()=>{
 const {host,parts}=markup('contract-panel');
 const shell=setupSidePanel(host,{unsaved:()=>false,confirmLeave:async()=>true,docked:()=>true});
 assert.equal(shell.element('body'),parts.body);
 await shell.show({kind:'curve-contract',curve:'gas',contract:'c1'});assert.equal(host.hidden,false);assert.equal(parts.form.hidden,true);
});

test('modes are the same when they name the same thing',()=>{
 const position={kind:'position',tradeId:'t1',bindingId:'b1',revision:2};
 assert.ok(sameMode(null,null));
 assert.ok(sameMode(position,{...position}));
 assert.ok(!sameMode(position,{...position,revision:3}));
 assert.ok(!sameMode(position,{...position,tradeId:'t2'}));
 assert.ok(!sameMode(position,{...position,bindingId:'b2'}),'two bindings of one trade are two positions');
 assert.ok(!sameMode(position,null));
 assert.ok(!sameMode({kind:'new',product:'futures'},{kind:'new',product:'swaps'}));
 assert.ok(sameMode({kind:'new',product:null},{kind:'new',product:null}),'a new trade with no product chosen is one mode');
 assert.ok(!sameMode({kind:'new',product:null},{kind:'new',product:'futures'}),'choosing a product is a switch');
 assert.ok(sameMode({kind:'risk'},{kind:'risk'}));
 assert.ok(!sameMode({kind:'risk'},position));
});

// What the shell touches of its markup, as plain objects: the panel's own element and the parts index.html gives it.
function markup(id='side-panel'){
 const part=()=>({hidden:false,onclick:null,focused:false,children:[],replaceChildren(...nodes){this.children=nodes;}});
 const parts={close:part(),head:part(),tabs:part(),body:part(),view:part(),form:part(),prompt:part()};
 const handlers={};
 const host={id,hidden:true,dataset:{},focused:0,labels:[],focus(){host.focused++;},setAttribute(name,value){if(name==='aria-label')host.labels.push(value);},addEventListener(type,handler){handlers[type]=handler;},querySelector:selector=>selector.startsWith(`#${id}-`)?parts[selector.slice(id.length+2)]:undefined};
 return {host,parts,handlers};
}
const position=(tradeId,revision=1,bindingId='b1')=>({kind:'position',tradeId,bindingId,revision});
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

test('the panel is wide while what it shows needs more room than a docked panel has, whatever its placement, and only while it is open',async()=>{
 const state={docked:true,wide:false},{host}=markup();
 const shell=setupSidePanel(host,{unsaved:()=>false,confirmLeave:async()=>true,docked:()=>state.docked,wide:()=>state.wide});
 await shell.show(position('t1'));assert.equal('wide' in host.dataset,false);
 state.wide=true;shell.place();assert.equal(host.dataset.wide,'');assert.equal(host.dataset.placement,'docked','the page beneath keeps its docked layout');
 state.docked=false;shell.place();assert.equal(host.dataset.wide,'');assert.equal(host.dataset.placement,'overlay');
 state.wide=false;shell.place();assert.equal('wide' in host.dataset,false);
 state.wide=true;shell.place();await shell.show(null);assert.equal('wide' in host.dataset,false,'a closed panel is not wide');
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

test('the panel is named for its mode, and a new trade for its product, and a closed panel has nobody to tell when its close button is pressed',async()=>{
 const state=panel();
 await state.shell.show(position('t1'));await state.shell.show({kind:'new',product:null});await state.shell.show({kind:'new',product:'futures'});await state.shell.show({kind:'new',product:'swaps'});await state.shell.show({kind:'risk'});
 assert.deepEqual(state.host.labels,['Position','New trade','New future','New swap','Risk report']);
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

// A page as plain nodes: each has its parent, its children and what isolating a page reads and sets of them.
function page(){
 const node=(name,children=[],localName='div')=>{const n={name,children,parentElement:null,localName,inert:false};for(const child of children)child.parentElement=n;return n;};
 const host=node('panel'),stage=node('stage',[host]),toolbar=node('toolbar'),workspace=node('workspace',[toolbar,stage]),
  banner=node('banner'),editor=node('editor',[],'dialog'),main=node('main',[banner,workspace]),sidebar=node('sidebar'),root=node('body',[sidebar,main,editor]);
 return {host,stage,toolbar,workspace,banner,editor,main,sidebar,root};
}

test('isolating a panel makes everything beside it and its ancestors inert, except dialogs, and gives it all back',()=>{
 const {host,stage,toolbar,workspace,banner,editor,main,sidebar}=page();
 const release=isolate(host);
 assert.deepEqual([toolbar,banner,sidebar].map(n=>n.inert),[true,true,true]);
 assert.deepEqual([host,stage,workspace,main,editor].map(n=>n.inert),[false,false,false,false,false],'the panel, the path to it and a dialog stay available');
 release();
 assert.deepEqual([toolbar,banner,sidebar].map(n=>n.inert),[false,false,false]);
});

test('isolating leaves alone what was inert already, and gives back only what it made inert',()=>{
 const {host,banner,sidebar}=page();
 sidebar.inert=true;
 const release=isolate(host);release();
 assert.equal(sidebar.inert,true);assert.equal(banner.inert,false);
});

test('a panel that covers the page, a sheet, leaves the page behind it out of reach while it is open',async()=>{
 const tree=page(),state={sheet:true};
 const {host}=markup();Object.assign(host,{parentElement:tree.stage,children:[],inert:false});tree.stage.children=[host];
 const shell=setupSidePanel(host,{unsaved:()=>false,confirmLeave:async()=>true,docked:()=>false,sheet:()=>state.sheet});
 assert.equal(tree.toolbar.inert,false,'a closed panel isolates nothing');
 await shell.show({kind:'risk'});
 assert.deepEqual([tree.toolbar.inert,tree.banner.inert,tree.sidebar.inert],[true,true,true]);
 state.sheet=false;shell.place();
 assert.deepEqual([tree.toolbar.inert,tree.banner.inert,tree.sidebar.inert],[false,false,false],'a panel that no longer covers the page gives it back');
 state.sheet=true;shell.place();assert.equal(tree.toolbar.inert,true);
 shell.reset();
 assert.deepEqual([tree.toolbar.inert,tree.banner.inert,tree.sidebar.inert],[false,false,false],'closing it gives the page back');
});

test('a drawer that leaves the page usable beside it isolates nothing',async()=>{
 const tree=page();
 const {host}=markup();Object.assign(host,{parentElement:tree.stage,children:[],inert:false});tree.stage.children=[host];
 const shell=setupSidePanel(host,{unsaved:()=>false,confirmLeave:async()=>true,docked:()=>false,sheet:()=>false});
 await shell.show({kind:'risk'});
 assert.equal(tree.toolbar.inert,false);
});

test('the panel hands out the parts a mode draws into: the head, the tab strip, the tab panel, its view and its form',async()=>{
 const {shell,parts}=panel();
 assert.equal(shell.element('head'),parts.head);assert.equal(shell.element('tabs'),parts.tabs);assert.equal(shell.element('body'),parts.body);
 assert.equal(shell.element('view'),parts.view);assert.equal(shell.element('form'),parts.form);
});

test('the form is shown in place of the view only when asked, and every mode starts on the view',async()=>{
 const {shell,parts}=panel();
 await shell.show(position('t1'));
 assert.deepEqual([parts.view.hidden,parts.form.hidden],[false,true],'a mode opens on its view');
 shell.form(true);
 assert.deepEqual([parts.view.hidden,parts.form.hidden],[true,false]);
 shell.form(false);
 assert.deepEqual([parts.view.hidden,parts.form.hidden],[false,true]);
 shell.form(true);await shell.show(position('t2'));
 assert.deepEqual([parts.view.hidden,parts.form.hidden],[false,true],'another position does not inherit the last one\'s form');
 shell.form(true);shell.reset();
 assert.deepEqual([parts.view.hidden,parts.form.hidden],[false,true],'nor does a closed panel keep it');
});

test('leaving asks only when the panel holds unsaved edits, and is a question, not a move',async()=>{
 const edits=panel({unsaved:true,answer:false});
 await edits.shell.show(position('t1'));
 assert.equal(await edits.shell.leave(),false);assert.deepEqual(edits.asked,['asked']);
 const clean=panel({unsaved:false});
 await clean.shell.show(position('t1'));
 assert.equal(await clean.shell.leave(),true);assert.deepEqual(clean.asked,[]);
 const yes=panel({unsaved:true,answer:true});
 await yes.shell.show(position('t1'));
 assert.equal(await yes.shell.leave(),true);assert.deepEqual(yes.shell.current(),position('t1'),'the panel stays where it was until the page moves it');
 const closed=panel({unsaved:true});
 assert.equal(await closed.shell.leave(),true);assert.deepEqual(closed.asked,[],'a closed panel has nothing to leave');
});

test('while the person is being asked, leaving is refused too, and a reset answers it no',async()=>{
 const {host}=markup();let release,signal;
 const shell=setupSidePanel(host,{unsaved:()=>true,confirmLeave:abandoned=>{signal=abandoned;return new Promise(resolve=>{release=resolve;});},docked:()=>true});
 await shell.show(position('t1'));
 const asking=shell.leave();
 assert.equal(await shell.leave(),false);assert.equal(await shell.show(position('t2')),false);
 shell.reset();
 assert.equal(signal.aborted,true);release(true);
 assert.equal(await asking,false);
});

test('the panel asks a question of its own as it asks about edits: one at a time, and a reset answers it no',async()=>{
 const {host}=markup();let release,signal,asked=0;
 const shell=setupSidePanel(host,{unsaved:()=>true,confirmLeave:async()=>true,docked:()=>true});
 await shell.show(position('t1'));
 const question=abandoned=>{asked++;signal=abandoned;return new Promise(resolve=>{release=resolve;});};
 const asking=shell.ask(question);
 assert.equal(await shell.ask(async()=>true),false,'a second question is refused while one is open');
 assert.equal(await shell.leave(),false,'and leaving is too');
 release(true);
 assert.equal(await asking,true);assert.equal(asked,1);
 const declined=shell.ask(question);release(false);assert.equal(await declined,false);
 const reset=shell.ask(question);shell.reset();
 assert.equal(signal.aborted,true);release(true);
 assert.equal(await reset,false,'an answer after a reset is no');
});

const cleared='Risk results cleared · live corrections changed the inputs';
const retained='Live corrections changed the inputs · the unconfirmed risk submission keeps the inputs it was captured at';
const positionMode={kind:'position',tradeId:'t',bindingId:'b',revision:1};
const untouched={carry:false,reopen:false,note:null};
test('a live correction reads an open panel again unless it holds edits, and clears a risk run with a note',()=>{
 assert.deepEqual(liveRefreshPlan({panel:positionMode,unsaved:false,riskRun:false,riskRetained:false}),{reloadPanel:true,risk:untouched});
 assert.deepEqual(liveRefreshPlan({panel:positionMode,unsaved:true,riskRun:false,riskRetained:false}),{reloadPanel:false,risk:untouched});
 assert.deepEqual(liveRefreshPlan({panel:positionMode,unsaved:true,riskRun:true,riskRetained:false}),{reloadPanel:false,risk:{carry:false,reopen:false,note:cleared}},
  'results kept while a position is shown are cleared all the same');
 assert.equal(liveRefreshPlan({panel:{kind:'new',product:'swaps'},unsaved:false,riskRun:false,riskRetained:false}).reloadPanel,true,'a new trade reloads too');
});

test('a live correction reads the open risk report again at the corrected inputs, and never keeps a setup at the inputs it was read at',()=>{
 assert.deepEqual(liveRefreshPlan({panel:{kind:'risk'},unsaved:false,riskRun:false,riskRetained:false}),{reloadPanel:false,risk:{carry:false,reopen:true,note:null}});
 assert.deepEqual(liveRefreshPlan({panel:{kind:'risk'},unsaved:true,riskRun:false,riskRetained:false}),{reloadPanel:false,risk:{carry:true,reopen:true,note:null}},
  'a setup holding edits is read again too, with the person\'s choices carried over');
 assert.deepEqual(liveRefreshPlan({panel:{kind:'risk'},unsaved:true,riskRun:true,riskRetained:false}),{reloadPanel:false,risk:{carry:true,reopen:true,note:cleared}},
  'a run is cleared all the same, as its results belong to the inputs that changed');
});

test('a run the report shows again after a correction, an unconfirmed submission, is said to keep the inputs it was captured at',()=>{
 assert.equal(liveRefreshPlan({panel:{kind:'risk'},unsaved:false,riskRun:true,riskRetained:true}).risk.note,retained);
 assert.equal(liveRefreshPlan({panel:null,unsaved:false,riskRun:true,riskRetained:true}).risk.note,retained);
 assert.equal(liveRefreshPlan({panel:null,unsaved:false,riskRun:false,riskRetained:true}).risk.note,null,'nothing is said when no run was on screen');
});

test('a live correction has no panel to read again when none is open, whatever the edits say, and still clears a risk run',()=>{
 assert.deepEqual(liveRefreshPlan({panel:null,unsaved:false,riskRun:false,riskRetained:false}),{reloadPanel:false,risk:untouched});
 assert.deepEqual(liveRefreshPlan({panel:null,unsaved:true,riskRun:true,riskRetained:false}),{reloadPanel:false,risk:{carry:false,reopen:false,note:cleared}});
});

test('a settlement record does to the risk report what a live correction does, in its own words',()=>{
 const settledNote='Risk results cleared · a settlement record changed the values they were run on';
 const settledKept='A settlement record changed the values · the unconfirmed risk submission keeps the inputs it was captured at';
 assert.deepEqual(riskRefreshPlan({open:true,edited:true,run:false,retained:false},'settlement'),{carry:true,reopen:true,note:null});
 assert.deepEqual(riskRefreshPlan({open:true,edited:false,run:true,retained:false},'settlement'),{carry:false,reopen:true,note:settledNote});
 assert.deepEqual(riskRefreshPlan({open:false,edited:false,run:true,retained:false},'settlement'),{carry:false,reopen:false,note:settledNote});
 assert.deepEqual(riskRefreshPlan({open:false,edited:false,run:true,retained:true},'settlement'),{carry:false,reopen:false,note:settledKept});
 assert.deepEqual(riskRefreshPlan({open:false,edited:true,run:false,retained:false},'settlement'),untouched,'a report that is not open holds no setup to carry');
 assert.deepEqual(riskRefreshPlan({open:true,edited:true,run:true,retained:true},'live'),{carry:true,reopen:true,note:retained});
});

test('a mode is named for the person and for assistive technology: a position, a new trade with or without its product, a risk report',()=>{
 assert.equal(modeLabel({kind:'position',tradeId:'t',bindingId:'b',revision:1}),'Position');assert.equal(modeLabel({kind:'risk'}),'Risk report');
 assert.equal(modeLabel({kind:'new',product:null}),'New trade');assert.equal(modeLabel({kind:'new',product:'futures'}),'New future');assert.equal(modeLabel({kind:'new',product:'swaps'}),'New swap');
});
test('a custom curve is one panel mode whose views are tabs',()=>{
 assert.ok(sameMode({kind:'custom-curve'},{kind:'custom-curve'}));
 assert.ok(!sameMode({kind:'custom-curve'},{kind:'risk'}));
 assert.equal(modeLabel({kind:'custom-curve'}),'Custom curve');
});
