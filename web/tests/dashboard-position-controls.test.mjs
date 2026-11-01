import test from 'node:test';
import assert from 'node:assert/strict';
import {setupPositions} from '../dist/dashboard-positions.js';

class Element {
 constructor(tag){this.tag=tag;this.children=[];this.value='';this.attributes={};this.hidden=false;}
 append(...nodes){for(const node of nodes){node.parentElement=this;this.children.push(node);}}
 replaceChildren(...nodes){this.children=[];this.append(...nodes);}
 add(option){this.append(option);if(this.value==='')this.value=option.value;}
 setAttribute(name,value){this.attributes[name]=value;}
 get options(){return this.children;}
 get selectedOptions(){return this.options.filter(option=>option.selected);}
 descendants(){return this.children.flatMap(node=>[node,...node.descendants()]);}
 querySelectorAll(selector){return this.descendants().filter(node=>node.tag===selector);}
}
function controls(api){
 globalThis.document={createElement:tag=>new Element(tag)};
 globalThis.Option=class extends Element{constructor(text,value){super('option');this.textContent=text;this.value=value;}};
 const control=setupPositions(api).configurator(()=>({snapshotId:'close',navigation:{context:null}}));
 const fields=control.element.querySelectorAll('select'),books=fields[1],preset=fields[2];
 books.replaceChildren(new Option('Book A','a'),new Option('Book B','b'));books.options[0].selected=true;
 return {control,books,preset,read:control.element.querySelectorAll('button')[0],sizes:()=>control.element.querySelectorAll('input').filter(input=>input.attributes['aria-label'])};
}
const target={id:'held',kind:'contract',unit:'USD / MWh',priceUnit:{currency:{code:'usd'},quantity:{code:'mwh'},multiplier:'1'}};
const catalog={scenarioTargets:[target],heldContractTargets:['held'],heldCurveTargets:[]};

test('a delayed price-unit read cannot populate sizes for books the person has since changed',async()=>{
 let complete;const sent=[];
 const f=controls(async(path,body)=>{
  sent.push([path,body]);
  if(path==='trading/directory')return {context:{contextId:'retained'},books:[]};
  if(path==='portfolio/risk/catalog')return new Promise(resolve=>{complete=resolve;});
  throw Error('Unexpected operation '+path);
 });
 const reading=f.read.onclick();await new Promise(resolve=>setImmediate(resolve));
 assert.deepEqual(sent.at(-1)[1].bookIds,['a']);
 f.books.options[0].selected=false;f.books.options[1].selected=true;f.books.onchange();
 complete(catalog);await reading;
 assert.equal(f.sizes().length,0,'old Book A risk units must not be offered for current Book B');
});

test('a delayed price-unit read cannot populate sizes after the preset changes',async()=>{
 let complete;
 const f=controls(async path=>path==='trading/directory'?{context:{contextId:'retained'},books:[]}:new Promise(resolve=>{complete=resolve;}));
 const reading=f.read.onclick();await new Promise(resolve=>setImmediate(resolve));
 f.preset.value='held_curves';f.preset.onchange();complete(catalog);await reading;
 assert.equal(f.sizes().length,0,'the old held-contract result must not fill parallel-shift controls');
 assert.equal(f.control.element.children.at(-1).textContent,'','an old response must not leave an error for the current preset');
});

test('current price units are shown and their exact entered sizes reach the saved definition',async()=>{
 const sent=[];
 const f=controls(async(path,body)=>{
  sent.push([path,body]);
  if(path==='trading/directory')return {context:{contextId:'retained'},books:[]};
  if(path==='portfolio/risk/catalog')return catalog;
  if(path==='risk/definitions')return {definitionId:'saved-definition'};
  throw Error('Unexpected operation '+path);
 });
 f.control.element.querySelectorAll('input')[0].value='Synthetic book exposure';
 await f.read.onclick();assert.equal(f.sizes().length,1);
 f.sizes()[0].value='0.25';
 assert.deepEqual(await f.control.settle(),{kind:'positions',config:{definitionId:'saved-definition'}});
 const [path,body]=sent.at(-1);assert.equal(path,'risk/definitions');
 assert.equal(body.name,'Synthetic book exposure');assert.deepEqual(body.bookIds,['a']);
 assert.equal(body.preset,'held_contracts');assert.deepEqual(body.sizes,[{unit:target.priceUnit,size:'0.25'}]);
 assert.ok(body.requestId,'the saved definition has its idempotent request identity');
});
