import test from 'node:test';
import assert from 'node:assert/strict';
import {setCurveNames} from '../dist/market-view.js';
import {futuresInstrumentEditor} from '../dist/futures-instrument-editor.js';
class Element{
 constructor(tag){this.tagName=tag.toUpperCase();this.children=[];this.style={setProperty(){}};this.dataset={};this.attributes={};this.listeners={};this.value='';this.classList={add(){}};}
 append(...nodes){for(const n of nodes){n.parentElement=this;this.children.push(n);}}
 replaceChildren(...nodes){this.children=[];this.append(...nodes);}
 set textContent(text){this.text=text;this.children=[];} get textContent(){return (this.text??'')+this.children.map(n=>n.textContent).join('');}
 setAttribute(k,v){this.attributes[k]=v;} addEventListener(k,f){(this.listeners[k]??=[]).push(f);} dispatchEvent(e){for(const f of this.listeners[e.type]??[])f(e);this['on'+e.type]?.(e);}
 get options(){return this.children;} focus(){}
}
const descendants=n=>[n,...n.children.flatMap(descendants)];
test('catalog refresh drops an unavailable delivery filter and immediately offers remaining dated contracts',()=>{
 const prior={document:globalThis.document,Option:globalThis.Option};
 globalThis.document={createElement:tag=>new Element(tag)};globalThis.Option=class extends Element{constructor(text,value){super('option');this.textContent=text;this.value=value;}};
 try{
  setCurveNames([{code:'synthetic',name:'Synthetic curve'}]);
  const host=new Element('section'),editor=futuresInstrumentEditor(host,{contract:'contract',lots:'lots',price:'price',terms:'terms'});
  const row={id:'front',curve:'synthetic',commodity:'gas',commodityName:'Synthetic gas',exchange:'synthetic_exchange',exchangeName:'Synthetic exchange',product:'synthetic_product',name:'Synthetic future',deliveryFrom:'2031-01-01',deliveryTo:'2031-02-01'};
  const later={...row,id:'later',deliveryFrom:'2031-02-01',deliveryTo:'2031-03-01'};
  const byId=id=>descendants(host).find(n=>n.id===id);
  editor.populate([row,later],'');
  for(const [key,value] of [['commodity',row.commodity],['exchange',row.exchange],['product',row.product]]){const n=byId('contract-'+key);n.value=value;n.dispatchEvent(new Event('change'));}
  descendants(host).find(n=>n.dataset.date===row.deliveryFrom).onclick();
  assert.equal(byId('contract').options.length,2,'front month filter is active');
  editor.populate([later],'');
  assert.equal(byId('contract-date-clear').disabled,true,'unavailable filter is cleared');
  assert.deepEqual(byId('contract').options.map(o=>o.value),['',JSON.stringify([later.curve,later.id])]);
 }finally{Object.assign(globalThis,prior);}
});
