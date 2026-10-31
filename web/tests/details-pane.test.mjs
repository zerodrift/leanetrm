import test from 'node:test';
import assert from 'node:assert/strict';
import {setupDetailsPane} from '../dist/details-pane.js';

class Element{
 children=[];hidden=false;id='';handlers={};dataset={};textContent='';
 append(...nodes){this.children.push(...nodes);}prepend(...nodes){this.children.unshift(...nodes);}replaceChildren(...nodes){this.children=nodes;}
 attributes={};setAttribute(name,value){this.attributes[name]=value;}getAttribute(name){return this.attributes[name];}removeAttribute(name){delete this.attributes[name];}
 addEventListener(type,handler){this.handlers[type]=handler;}
 focus(){this.focused=(this.focused??0)+1;}click(){this.onclick?.();}
 descendants(){return this.children.flatMap(c=>[c,...c.descendants()]);}
 querySelector(selector){return this.descendants().find(c=>selector===`#${c.id}`);}
 querySelectorAll(){return [];}
 contains(node){return node===this||this.descendants().includes(node);}
}
function fixture({unsaved=false}={}){
 globalThis.requestAnimationFrame=()=>0;
 globalThis.document={createElement:()=>new Element()};
 const steps=[],collapsed=[],expanded=[],changed=[];let edits=unsaved;
 const host=new Element();host.id='blotter-pane-details';
 const pane=setupDetailsPane(host,{unsaved:()=>edits,busy:()=>false,step:direction=>steps.push(direction),collapsed:()=>collapsed.push(true),expand:()=>expanded.push(true),changed:()=>changed.push(true)});
 const all=()=>host.descendants();
 const labelled=label=>all().find(node=>node.attributes['aria-label']===label);
 const prompt=()=>all().find(node=>node.attributes.role==='alertdialog');
 const answer=label=>prompt().descendants().find(node=>node.textContent===label).click();
 const escape=()=>{let prevented=0;all().find(node=>node.handlers.keydown).handlers.keydown({key:'Escape',defaultPrevented:false,preventDefault(){prevented++;}});return prevented;};
 return {...pane,host,steps,collapsed,expanded,changed,labelled,prompt,answer,escape,edit:value=>{edits=value;}};
}
const settle=()=>new Promise(resolve=>setImmediate(resolve));
const position=(tradeId,revision=1)=>({kind:'position',tradeId,bindingId:'binding',revision});

test('the header steps to the previous and next position, names itself and collapses, and offers no Close position view',async()=>{
 const f=fixture();await f.show(position('t1'));
 f.labelled('Previous position').click();f.labelled('Next position').click();
 assert.deepEqual(f.steps,[-1,1]);
 assert.ok(f.labelled('Collapse details'),'the header collapses the details');
 assert.equal(f.host.descendants().some(node=>node.textContent==='Close position view'),false);
 f.title('Synthetic trade 7 · Synthetic market');
 assert.ok(f.host.descendants().some(node=>node.textContent==='Synthetic trade 7 · Synthetic market'),'the header carries the title it is given');
});

test('showing a mode expands the pane and tells the page the mode changed; the shell hands out its parts',async()=>{
 const f=fixture();
 assert.equal(await f.show(position('t1')),true);
 assert.deepEqual(f.shell.current(),position('t1'));assert.equal(f.expanded.length,1);assert.equal(f.changed.length,1);
 for(const name of ['head','tabs','body','view','form'])assert.ok(f.shell.element(name),name);
 f.shell.form(true);assert.equal(f.shell.element('form').hidden,false);assert.equal(f.shell.element('view').hidden,true);
 assert.equal(await f.show({kind:'risk'}),true);assert.equal(f.shell.element('form').hidden,true,'every mode starts on its view');
});

test('with unsaved edits, showing another position asks first and resolves false on Keep editing',async()=>{
 const f=fixture();await f.show(position('t1'));f.edit(true);
 const moving=f.show(position('t2'));await settle();
 assert.ok(f.prompt()&&!f.prompt().hidden,'the unsaved question is shown');
 assert.equal(await f.show(position('t3')),false,'a second request waits for no answer and changes nothing');
 f.answer('Keep editing');
 assert.equal(await moving,false);assert.deepEqual(f.shell.current(),position('t1'));
});

test('with unsaved edits, Discard changes moves to the other position',async()=>{
 const f=fixture();await f.show(position('t1'));f.edit(true);
 const moving=f.show(position('t2'));await settle();f.answer('Discard changes');
 assert.equal(await moving,true);assert.deepEqual(f.shell.current(),position('t2'));
});

test('Escape collapses the details, and a question still pending resolves as aborted',async()=>{
 const f=fixture();await f.show(position('t1'));let finish;
 const asking=f.shell.ask(signal=>new Promise(resolve=>{finish=()=>resolve(!signal.aborted);signal.addEventListener('abort',()=>resolve(false));}));
 assert.equal(f.escape(),1,'Escape is handled');
 assert.equal(await asking,false,'the pending question is answered no');finish();
 await settle();
 assert.equal(f.collapsed.length,1);assert.equal(f.shell.current(),null,'collapsed details hold nothing');
});

test('collapsing with unsaved edits asks first, and Keep editing keeps the details open',async()=>{
 const f=fixture();await f.show(position('t1'));f.edit(true);
 f.labelled('Collapse details').click();await settle();
 f.answer('Keep editing');await settle();
 assert.deepEqual(f.shell.current(),position('t1'));assert.equal(f.collapsed.length,0);
 f.labelled('Collapse details').click();await settle();f.answer('Discard changes');await settle();
 assert.equal(f.shell.current(),null);assert.equal(f.collapsed.length,1);
});

test('a reset closes the details without asking and answers a pending question no',async()=>{
 const f=fixture();await f.show(position('t1'));f.edit(true);
 const moving=f.show(position('t2'));await settle();f.shell.reset();
 assert.equal(await moving,false);assert.equal(f.shell.current(),null);assert.equal(f.collapsed.length,0,'a reset is the page closing it, not the person');
});

test('the steps are offered only while a position is shown',async()=>{
 const f=fixture();await f.show({kind:'risk'});
 assert.equal(f.labelled('Previous position').disabled,true);assert.equal(f.labelled('Next position').disabled,true);
 await f.show(position('t1'));
 assert.equal(f.labelled('Previous position').disabled,false);assert.equal(f.labelled('Next position').disabled,false);
});
