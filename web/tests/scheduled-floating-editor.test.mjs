import test from 'node:test';
import assert from 'node:assert/strict';
import {mountScheduledFloatingEditor} from '../dist/scheduled-floating-editor.js';

// Minimal DOM surface used by the side editor; no pricing or compatibility logic.
function dom(){
 const nodes=[];
 class Element{
  constructor(tag){this.tag=tag;this.children=[];this.dataset={};this.value='';this.disabled=false;this.isConnected=true;nodes.push(this);}
  append(...children){for(const child of children){child.parentElement=this;this.children.push(child);}}
  replaceChildren(...children){this.children=[];this.append(...children);}
  querySelector(selector){return nodes.find(n=>selector.startsWith('#')?n.id===selector.slice(1):n.tag===selector);}
  setAttribute(){} after(){} remove(){}
  set value(value){this._value=this.tag==='select'&&!this.children.some(c=>c.value===value)?'':value;}
  get value(){return this._value;}
  get options(){return this.children;}
  get selectedOptions(){return this.children.filter(n=>n.value===this.value);}
 }
 return{nodes,Element,document:{createElement:tag=>new Element(tag),getElementById:id=>nodes.find(n=>n.id===id)},Option:class extends Element{constructor(text,value){super('option');this.text=text;this.value=value;}}};
}
const monthly={resetFrequency:'monthly',averagingMethod:'none',label:'Published monthly',requiresObservationCalendar:false,requiresQuantityWeights:false};
const daily={...monthly,resetFrequency:'daily',averagingMethod:'arithmetic',label:'Daily observations'};
const indices=[{definitionRevisionId:'r',curve:'month',index:'month_index',commodity:'gas',currency:'USD',currencyCode:'usd',unit:'energy',quoteMultiplier:'1',samplings:[monthly],customProjections:[],contracts:[{deliveryFrom:'2030-01-01',deliveryTo:'2030-02-01'}]},
 {definitionRevisionId:'r',curve:'day',index:'day_index',commodity:'gas',currency:'USD',currencyCode:'usd',unit:'energy',quoteMultiplier:'1',samplings:[daily],customProjections:[],contracts:[{deliveryFrom:'2030-01-01',deliveryTo:'2030-02-01'}]}];
const key=i=>JSON.stringify([i.definitionRevisionId,i.curve,i.index]);
test('scheduled side supports convention-first discovery and applies backend index and frequency restrictions',async()=>{
 const fake=dom(),oldDocument=globalThis.document,oldOption=globalThis.Option;globalThis.document=fake.document;globalThis.Option=fake.Option;
 try{
  const host=new fake.Element('section'),requests=[];let pending;
  const response={indices:[indices[1]],samplings:[daily],choices:[],observations:null,observationCalendars:[],availability:null,customProjectionUnavailableReason:null};
  const editor=mountScheduledFloatingEditor({host,from:'2030-01-01',to:'2030-02-01',indices:()=>indices,context:()=>({contextId:'context'}),enabled:()=>true,busy:()=>false,
   api:async(path,body)=>{requests.push(body);return response;},run:work=>{pending=work();},changed(){},quoteChanged(){}});
  await editor.refresh();assert.equal(requests.length,1,'A partial side must request backend choices before an index is selected.');
  const selects=host.children.filter(n=>n.children[1]?.tag==='select').map(n=>n.children[1]);
  const index=selects.find(s=>s.dataset.role==='period-index'),frequency=selects.find(s=>s.options.some(o=>o.value==='daily'));
  assert.deepEqual(index.options.filter(o=>o.value&&!o.disabled).map(o=>o.value),[key(indices[1])]);
  assert.ok(frequency);assert.equal(frequency.options.some(o=>o.value==='monthly'&&!o.disabled),false);
  frequency.value='daily';frequency.onchange();await pending;
  assert.equal(requests.at(-1).resetFrequency,'daily');assert.equal(index.value,'','No index is selected implicitly.');
 }finally{globalThis.document=oldDocument;globalThis.Option=oldOption;}
});

test('bulk source changes preserve the requested index across disjoint compatible option sets',async()=>{
 const fake=dom(),oldDocument=globalThis.document,oldOption=globalThis.Option;globalThis.document=fake.document;globalThis.Option=fake.Option;
 try{
  const host=new fake.Element('section');let requested;
  const editor=mountScheduledFloatingEditor({host,from:'2030-01-01',to:'2030-02-01',indices:()=>indices,context:()=>({contextId:'context'}),enabled:()=>true,busy:()=>false,
   api:async(path,body)=>{requested=body;const i=indices.find(i=>i.curve===body.curve);return{indices:i?[i]:indices,samplings:i?.samplings??[monthly,daily],choices:[],observations:[{}],observationCalendars:[],availability:null,customProjectionUnavailableReason:null};},run:work=>work(),changed(){},quoteChanged(){}});
  for(const i of [indices[1],indices[0],indices[1]]){
   await editor.apply({curve:i.curve,index:i.index,currency:'usd',settlement:{mode:'curve_target',index:null,projectionPolicy:null},deliveryFrom:'2030-01-01',deliveryTo:'2030-02-01',...i.samplings[0],spread:'0',customProjection:null});
   assert.equal(requested.curve,i.curve);assert.equal(editor.read().curve,i.curve);assert.equal(editor.read().resetFrequency,i.samplings[0].resetFrequency);
  }
 }finally{globalThis.document=oldDocument;globalThis.Option=oldOption;}
});

test('a rejected calendar retains the control needed to select a valid replacement',async()=>{
 const fake=dom(),oldDocument=globalThis.document,oldOption=globalThis.Option;globalThis.document=fake.document;globalThis.Option=fake.Option;
 try{
  const mode={...daily,averagingMethod:'observation_arithmetic',requiresObservationCalendar:true};
  const i={...indices[1],samplings:[monthly],settlementIndices:[{code:'daily',name:'Daily'}]},reference=code=>({revision:{catalogId:'c',revisionId:'r'},code});
  const host=new fake.Element('section');let pending;
  const editor=mountScheduledFloatingEditor({host,from:'2030-01-01',to:'2030-02-01',indices:()=>[i],context:()=>({contextId:'context'}),enabled:()=>true,busy:()=>false,
   api:async(path,body)=>{const valid=body.observationCalendar?.code==='covered';return{indices:valid?[i]:[],samplings:valid?[mode]:[],selectedSampling:mode,choices:[],observations:valid?[{}]:null,observationCalendars:['short','covered'].map(code=>({reference:reference(code),name:code,from:'2030-01-01',to:'2030-02-01'})),availability:valid?null:{code:'swap_averaging',message:'Calendar does not cover the period.'},customProjectionUnavailableReason:null};},run:work=>{pending=work();},changed(){},quoteChanged(){}});
  await editor.apply({curve:i.curve,index:i.index,currency:'usd',settlement:{mode:'override',index:'daily',projectionPolicy:'forward_curve_no_basis_v1'},deliveryFrom:'2030-01-01',deliveryTo:'2030-02-01',...mode,spread:'0',customProjection:null,observationSchedule:{implementation:'side_observation_schedule_v1',calendar:reference('short'),quantities:null}});
  editor.sync();const calendar=fake.nodes.find(n=>n.id?.endsWith('-observation-calendar'));
  assert.equal(calendar.parentElement.hidden,false);assert.equal(calendar.disabled,false);assert.throws(()=>editor.read(),/qualified/);
  calendar.value=JSON.stringify(reference('covered'));calendar.onchange();await pending;
  assert.equal(editor.read().observationSchedule.calendar.code,'covered');
 }finally{globalThis.document=oldDocument;globalThis.Option=oldOption;}
});

test('an override-capable curve stays discoverable before its settlement choice is complete',async()=>{
 const fake=dom(),oldDocument=globalThis.document,oldOption=globalThis.Option;globalThis.document=fake.document;globalThis.Option=fake.Option;
 try{
  const candidate={...indices[0],settlementIndices:[{code:'daily',name:'Daily'}]},host=new fake.Element('section');
  const editor=mountScheduledFloatingEditor({host,from:'2030-01-01',to:'2030-02-01',indices:()=>[candidate],context:()=>({}),enabled:()=>true,busy:()=>false,
   api:async()=>({indices:[],samplings:[daily],choices:[],observations:null,observationCalendars:[],availability:null,customProjectionUnavailableReason:null}),run:work=>work(),changed(){},quoteChanged(){}});
  await editor.refresh();const select=fake.nodes.find(n=>n.dataset.role==='period-index');
  assert.ok(select.options.some(o=>o.value===key(candidate)&&!o.disabled));
  assert.throws(()=>editor.read(),/qualified/,'An authoring candidate is not an admitted plan.');
 }finally{globalThis.document=oldDocument;globalThis.Option=oldOption;}
});
