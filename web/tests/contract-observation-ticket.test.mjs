import test from 'node:test';
import assert from 'node:assert/strict';
import {contractObservationReceipt} from '../dist/contract-observation-ticket.js';

test('receipt transports authoritative terms unchanged and rejects edited inputs or context',()=>{
 const state=contractObservationReceipt();const request={context:{contextId:'a'},curve:'future',selection:{mode:'prompt_offset',promptOffset:1}};
 const terms={curve:'future',index:null,contractObservations:{observations:[{ordinal:0,fixingAt:'2041-01-01T18:00:00Z'}]}};
 assert.throws(()=>state.read(request),/Preview/);
 state.accept(request,terms);assert.deepEqual(state.read(request),terms);
 assert.throws(()=>state.read({...request,context:{contextId:'b'}}),/Preview/);
 assert.throws(()=>state.read({...request,selection:{mode:'prompt_offset',promptOffset:2}}),/Preview/);
 state.invalidate();assert.throws(()=>state.read(request),/Preview/);
});
test('saved concrete source terms reload without selector execution and edits require new preview',()=>{
 const terms={curve:'future',index:null,contractObservations:{observations:[{ordinal:0,fixingAt:'2041-01-01T18:00:00Z'}]}};
 const state=contractObservationReceipt(terms);
 assert.deepEqual(state.retained(),terms);state.invalidate();assert.equal(state.retained(),undefined);
 assert.throws(()=>state.read({}),/Preview/);
});
test('mutating returned or response objects cannot alter an admitted receipt',()=>{
 const state=contractObservationReceipt();const request={context:{contextId:'a'}};const terms={contractObservations:{observations:[{ordinal:0}]}};
 state.accept(request,terms);terms.contractObservations.observations[0].ordinal=99;
 const first=state.read(request);first.contractObservations.observations[0].ordinal=7;
 assert.equal(state.read(request).contractObservations.observations[0].ordinal,0);
});

test('the mounted source editor discards stale responses and invalidates changed payment inputs',async()=>{
 const {mountContractObservationTicket}=await import('../dist/contract-observation-ticket.js');
 const nodes=[];
 class Element{
  constructor(tag){this.tag=tag;this.children=[];this.dataset={};this.isConnected=true;this._value='';nodes.push(this);}
  append(...children){children.forEach(child=>{child.parentElement=this;this.children.push(child);});}
  replaceChildren(...children){this.children=[];this.append(...children);}
  get firstElementChild(){return this.children[0];}
  setAttribute(){}
  set value(value){this._value=this.tag==='select'&&!this.children.some(c=>c.value===value)?'':value;}
  get value(){return this._value;}
 }
 const oldDocument=globalThis.document,oldOption=globalThis.Option;
 globalThis.document={createElement:tag=>new Element(tag)};globalThis.Option=class extends Element{constructor(label,value){super('option');this.textContent=label;this.value=value;}};
 try{
  const ref={revision:{catalogId:'c',revisionId:'r'},code:'utc'},contract={revision:{catalogId:'c',revisionId:'d'},id:'month'};
  const source={definitionRevisionId:'d',curve:'future',name:'Synthetic future',commodity:'gas',currencyCode:'usd',unit:'energy',quoteMultiplier:'1',contracts:[{contract,label:'Jan41',deliveryFrom:'2041-01-01',deliveryTo:'2041-02-01',expiry:'2040-12-28T18:00:00Z'}],selectionModes:['exact','prompt_offset'],rollRules:['contract_expiry_roll_v1'],expiryPolicies:['carry_final_settlement'],historyPolicies:['visible_corrections_at_cutoff_v1'],roles:['daily_settlement'],clocks:[{reference:ref,label:'UTC'}]};
  const terms={curve:'future',index:null,settlement:null,currency:'usd',deliveryFrom:'2041-01-01',deliveryTo:'2041-02-01',spread:'0',resetFrequency:'daily',averagingMethod:'arithmetic',contractObservations:{implementation:'contract_observation_source_v1',observations:[{ordinal:0,from:'2041-01-01',to:'2041-01-02',fixingAt:'2041-01-01T18:00:00Z'}]}};
  let contextId='a',payment={rule:'explicit_test'},resolve,work,request;
  const editor=mountContractObservationTicket({host:new Element('section'),from:terms.deliveryFrom,to:terms.deliveryTo,sources:()=>[source],context:()=>({contextId}),payment:()=>payment,spread:()=> '0',enabled:()=>true,busy:()=>false,
   api:async(_path,body)=>{request=body;return new Promise(done=>{resolve=done;});},run:task=>{work=task();},changed(){},quoteChanged(){}});
  const field=role=>nodes.find(n=>n.dataset.role===role),set=(role,value)=>{const control=field(role);control.value=value;control.onchange?.();};
  set('contract-curve','future');set('contract-selection-mode','exact');set('contract-exact',JSON.stringify(contract));
  set('contract-expiry-policy','carry_final_settlement');set('contract-history-policy','visible_corrections_at_cutoff_v1');set('contract-role','daily_settlement');set('contract-clock',JSON.stringify(ref));set('contract-fixing-time','18:00:00');set('contract-ambiguous-time','reject');set('contract-invalid-time','reject');
  const response=()=>({available:true,floating:terms,observations:[{observation:terms.contractObservations.observations[0],contract:source.contracts[0]}]});
  field('contract-preview').onclick();assert.deepEqual(request.source.selection,{mode:'exact',contract,promptOffset:null,roll:null,boundaries:null});contextId='b';resolve(response());await work;
  assert.throws(()=>editor.read(),/Preview/,'An old context cannot admit a source receipt.');
  field('contract-preview').onclick();resolve(response());await work;assert.deepEqual(editor.read(),terms);
  payment={rule:'changed'};editor.sync();assert.throws(()=>editor.read(),/Preview/);
  field('contract-preview').onclick();editor.dispose();resolve(response());await work;assert.throws(()=>editor.read(),/Preview/,'Disposed editors cannot accept late responses.');
 }finally{globalThis.document=oldDocument;globalThis.Option=oldOption;}
});
