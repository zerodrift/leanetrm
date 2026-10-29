import test from 'node:test';
import assert from 'node:assert/strict';
import {setupPositionExpansion} from '../dist/position-expansion.js';

class Element{
 children=[];hidden=false;id='';handlers={};
 append(...nodes){this.children.push(...nodes);}replaceChildren(...nodes){this.children=nodes;}
 attributes={};setAttribute(name,value){this.attributes[name]=value;}getAttribute(name){return this.attributes[name];}addEventListener(type,handler){this.handlers[type]=handler;}
 focus(){if(this.isConnected)this.focused=true;}scrollIntoView(){}click(){this.onclick?.();}
 querySelector(selector){const all=this.children.flatMap(c=>[c,...c.descendants()]);return all.find(c=>selector===`#${c.id}`);}
 descendants(){return this.children.flatMap(c=>[c,...c.descendants()]);}
 contains(node){return node===this||this.descendants().includes(node);}
}
function fixture(){
 const frames=[];globalThis.requestAnimationFrame=callback=>frames.push(callback);
 globalThis.document={createElement:()=>new Element()};globalThis.ResizeObserver=class{observe(){}};
 let mode=null,unsaved=false,busy=false;const sideParts={},closed=[],resized=[];
 const side={current:()=>mode,show:async next=>{mode=next;return true;},reset:()=>{mode=null;},leave:async()=>true,ask:ask=>ask(new AbortController().signal),
  element:name=>sideParts[name]??=new Element(),form(){},reveal(){},focusForm(){},place(){}};
 const expansion=setupPositionExpansion(side,{unsaved:()=>unsaved,busy:()=>busy,changed(){},closed:()=>closed.push(true),resized:keepVisible=>resized.push(keepVisible)});
 return{...expansion,side,closed,resized,paint:()=>frames.splice(0).forEach(callback=>callback()),edit:()=>{unsaved=true;},busy:value=>{busy=value;}};
}
const position={kind:'position',tradeId:'trade',bindingId:'binding',revision:2};
test('the inline close control names only the position view it closes',()=>{
 const f=fixture();assert.equal(f.host.children[0].textContent,'Close position view');
});
test('opening a position uses a row expansion and leaves the side panel available to New trade and Risk',async()=>{
 const f=fixture();await f.shell.show(position);
 assert.equal(f.host.hidden,false);assert.equal(f.side.current(),null);assert.deepEqual(f.shell.current(),position);
 assert.equal(f.shell.element('form'),f.position.element('form'));
 const retained=new Element();f.position.element('form').append(retained);await f.shell.show({...position});
 assert.equal(f.position.element('form').children[0],retained,'opening the same position retains its form');
 await f.shell.show({kind:'risk'});assert.equal(f.host.hidden,true);assert.deepEqual(f.side.current(),{kind:'risk'});
 assert.equal(f.shell.element('form'),f.side.element('form'));
 await f.shell.show({kind:'new',product:'swaps'});assert.deepEqual(f.side.current(),{kind:'new',product:'swaps'});
});
test('unsaved edits keep the expanded position until an explicit answer permits switching',async()=>{
 const f=fixture();await f.shell.show(position);f.edit();const moving=f.shell.show({kind:'risk'});
 await Promise.resolve();assert.deepEqual(f.shell.current(),position);assert.equal(f.host.hidden,false);
 assert.equal(await f.shell.show({...position}),false,'opening the current row must not bypass a pending question');
 const prompt=f.host.querySelector('#position-details-prompt'),keep=prompt.children[1].children[0];keep.click();
 assert.equal(await moving,false);assert.deepEqual(f.shell.current(),position);assert.equal(f.side.current(),null);
});
test('a reset cancels an outstanding position question and never reopens an obsolete position',async()=>{
 const f=fixture();await f.shell.show(position);let finish;
 const asking=f.shell.ask(signal=>new Promise(resolve=>{finish=()=>resolve(!signal.aborted);}));f.shell.reset();finish();
 assert.equal(await asking,false);assert.equal(f.shell.current(),null);assert.equal(f.host.hidden,true);
});

test('opening focuses only after the row attaches the detail, and a replaced position cannot take focus',async()=>{
 const f=fixture();await f.shell.show(position);assert.equal(f.host.focused,undefined);
 f.host.isConnected=true;f.paint();assert.equal(f.host.focused,true);
 f.host.focused=false;await f.shell.show({...position,revision:3});await f.shell.show({kind:'risk'});f.paint();
 assert.equal(f.host.focused,false,'a closed detail must not take focus from Risk');
});

test('position questions retain an accessible alert-dialog name and focus skips hidden form controls',()=>{
 const f=fixture(),prompt=f.host.querySelector('#position-details-prompt');
 assert.equal(prompt.getAttribute('role'),'alertdialog');assert.equal(prompt.getAttribute('aria-label'),'Unsaved changes');
 const hidden=new Element(),visible=new Element();hidden.isConnected=visible.isConnected=true;
 hidden.getClientRects=()=>[];visible.getClientRects=()=>[{}];
 const form=f.position.element('form');form.querySelector=()=>hidden;form.querySelectorAll=()=>[hidden,visible];
 f.position.focusForm();assert.equal(hidden.focused,undefined);assert.equal(visible.focused,true);
});

test('detail keyboard navigation stays inside the detail instead of reaching grid shortcuts',()=>{
 const f=fixture();let stopped=0;
 for(const key of ['Home','End','ArrowDown','Tab'])f.host.handlers.keydown({key,defaultPrevented:false,stopPropagation(){stopped++;},preventDefault(){throw Error('Native control behavior must remain available.');}});
 assert.equal(stopped,4);
});

test('amendment focus waits for fields enabled after the current action completes',async()=>{
 const f=fixture();await f.shell.show(position);f.host.isConnected=true;f.position.form(true);
 const input=new Element();input.isConnected=true;input.getClientRects=()=>[{}];let enabled=false;
 f.position.element('form').querySelectorAll=()=>enabled?[input]:[];
 f.position.focusForm();assert.equal(input.focused,undefined);enabled=true;f.paint();assert.equal(input.focused,true);
});

test('a close control detached during a refresh is enabled again when the page becomes idle',async()=>{
 const f=fixture();await f.shell.show(position);const close=f.host.children[0];
 f.busy(true);f.shell.place();assert.equal(close.disabled,true);
 f.shell.reset();f.host.isConnected=false;f.busy(false);f.shell.place();
 await f.shell.show({...position,revision:3});f.host.isConnected=true;
 assert.equal(close.disabled,false,'document-wide control updates cannot reach a detached detail');
 close.click();await new Promise(resolve=>setImmediate(resolve));
 assert.equal(f.host.hidden,true);assert.equal(f.closed.length,1);
});

test('opening an editor reports its new row height before a following prompt can focus below it',async()=>{
 const f=fixture();await f.shell.show(position);
 f.position.form(true);assert.deepEqual(f.resized,[false]);
});

test('returning a focused editor to its read keeps the current position visible, without moving another focus or a reset position',async()=>{
 const f=fixture();await f.shell.show(position);f.position.form(true);f.resized.length=0;document.activeElement=f.host;
 f.position.form(false);assert.deepEqual(f.resized,[true]);
 f.resized.length=0;f.position.form(false);assert.deepEqual(f.resized,[],'drawing the existing read must not keep scrolling it back');
 f.position.form(true);f.resized.length=0;document.activeElement=new Element();f.position.form(false);
 assert.deepEqual(f.resized,[false],'the layout changes without taking focus from another control');
 f.position.form(true);f.resized.length=0;f.shell.reset();document.activeElement=f.host;f.position.form(false);
 assert.deepEqual(f.resized,[],'a reset must not bring back its old position');
});
