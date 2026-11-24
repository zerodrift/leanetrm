import test from 'node:test';
import assert from 'node:assert/strict';
import {previewed,refusedField} from '../dist/ticket-preview.js';

const view=q=>({levels:['month'],rows:[],totals:{'leg-1.quantity':{value:q,sources:[]}},delivered:null,remaining:null,deliveredLeaves:0,leaves:1,valuationAt:'2026-10-30T00:00:00Z'});
const summaries=[{side:'leg-1',billedQuantity:'1',amount:'2',againstMarket:null}];
const empty={schedule:null,quantityUnit:null,previous:null,summaries:[],links:[],status:{kind:'current',text:'Not previewed yet.'},errors:{}};
const refusal=(message,details)=>Object.assign(Error(message),{code:'numeric_format',details});
const sideGroups=[{instrumentId:'synthetic-service',bindingId:null,label:'Service instrument',sides:[{slot:0,sideId:'synthetic-side',label:'Rights',roleLabel:'Commercial entitlement',directionLabel:'Signed whole package',overviewSections:[{label:'Terms',values:[{label:'Packages',value:'1'}]}]}]}];

test('a refusal names its field as the ticket keys it, or none',()=>{
 assert.equal(refusedField(refusal('x',{code:'numeric_format',side:'leg-1',field:'quantity'})),'leg-1.quantity');
 assert.equal(refusedField(refusal('x',{code:'ticket_input',side:null,field:'book'})),'book');
 assert.equal(refusedField(refusal('x',{code:'ticket_input'})),null);
 assert.equal(refusedField(Error('offline')),null);
});

test('a preview draws its schedule, and the schedule it replaces marks what changed',()=>{
 const first=previewed(empty,{preview:{schedule:view('1'),summaries,scheduleRefusal:null,quantityUnit:'MMBtu',links:[]}});
 assert.deepEqual(first,{schedule:view('1'),quantityUnit:'MMBtu',previous:null,summaries,links:[],status:{kind:'current',text:'Up to date · previewed after your last change'},errors:{}});
 const second=previewed(first,{preview:{schedule:view('2'),summaries:[],scheduleRefusal:null,quantityUnit:'GJ',links:[]}});
 assert.deepEqual([second.schedule,second.previous,second.quantityUnit],[view('2'),view('1'),'GJ']);
 assert.throws(()=>previewed(first,{preview:{schedule:view('3'),summaries:[],scheduleRefusal:null,links:[]}}),/quantity unit/);
});

test('a preview draws its quantity links, and one without them is refused',()=>{
 const links=[{source:'leg-1',target:'leg-2',formula:'gross_less_fraction',fraction:'0.02',receipt:'310000',delivery:'303800',fuel:'6200'}];
 assert.deepEqual(previewed(empty,{preview:{schedule:view('1'),summaries,scheduleRefusal:null,quantityUnit:'MMBtu',links}}).links,links);
 assert.throws(()=>previewed(empty,{preview:{schedule:view('1'),summaries,scheduleRefusal:null,quantityUnit:'MMBtu'}}),/quantity links/);
});

test('a refused preview keeps the last schedule and summaries, stale, with the reason on the field it names',()=>{
 const good=previewed(empty,{preview:{schedule:view('1'),summaries,scheduleRefusal:null,quantityUnit:'MMBtu',links:[]}});
 const refused=previewed(good,{refused:refusal('Supply a plain decimal.',{side:'leg-1',field:'quantity'})});
 assert.deepEqual(refused,{...good,status:{kind:'stale',text:'Preview refused: Supply a plain decimal.'},errors:{'leg-1.quantity':'Supply a plain decimal.'}});
 assert.deepEqual(previewed(empty,{refused:Error('offline')}),{...empty,status:{kind:'refused',text:'Preview refused: offline'},errors:{}});
});

test('a valuation without a schedule says why and keeps the last schedule',()=>{
 const good=previewed(empty,{preview:{schedule:view('1'),summaries,scheduleRefusal:null,quantityUnit:'MMBtu',links:[]}});
 const kept=previewed(good,{preview:{schedule:null,summaries:null,scheduleRefusal:{code:'schedule_view',message:'The sides keep different gas-day clocks.'},quantityUnit:'MMBtu',links:[]}});
 assert.deepEqual(kept,{...good,status:{kind:'stale',text:'Schedule refused: The sides keep different gas-day clocks.'},errors:{}});
 assert.equal(previewed(empty,{preview:{schedule:null,summaries:null,scheduleRefusal:{code:'schedule_view',message:'No grid.'},quantityUnit:'MMBtu',links:[]}}).status.kind,'refused');
 assert.throws(()=>previewed(good,{preview:{schedule:null,summaries:null,scheduleRefusal:null,quantityUnit:'MMBtu',links:[]}}),/neither/);
});

test('a required Side composition is retained independently from schedule availability and refuses an absent or false retained owner',()=>{
 const result={schedule:null,summaries:null,scheduleRefusal:{code:'unpriced',message:'Select a published mark separately.'},quantityUnit:'package',links:[],sideGroups};
 const shown=previewed(empty,{preview:result},{sideGroupsRequired:true,retained:false});
 assert.deepEqual(shown.sideGroups,sideGroups);assert.equal(shown.schedule,null);
 assert.throws(()=>previewed(empty,{preview:{...result,sideGroups:undefined}},{sideGroupsRequired:true,retained:false}),/Side memberships/);
 assert.throws(()=>previewed(empty,{preview:result},{sideGroupsRequired:true,retained:true}),/Side memberships/);
});

test('a refused new draft withholds the previous Side composition while its old schedule remains explicitly stale',()=>{
 const shown=previewed(empty,{preview:{schedule:view('1'),summaries,quantityUnit:'package',links:[],sideGroups}},{sideGroupsRequired:true,retained:false});
 assert.equal(shown.sideGroups.length,1);
 const refused=previewed(shown,{refused:Error('The revised Side terms are incomplete.')});
 assert.equal(refused.sideGroups,undefined);assert.equal(refused.status.kind,'stale');assert.deepEqual(refused.schedule,shown.schedule);
});

test('explicit absence requires both an empty group list and its backend reason, and never substitutes for missing unpriced structure',()=>{
 const result={schedule:null,summaries:null,scheduleRefusal:{code:'no_financial_instrument',message:'No financial instrument is held by this service.'},quantityUnit:'native units',links:[],sideGroups:[],sideGroupsReason:'Custody has no fee instrument; its performance remains separate.'};
 const shown=previewed(empty,{preview:result},{sideGroupsRequired:true,retained:true});
 assert.deepEqual(shown.sideGroups,[]);assert.equal(shown.sideGroupsReason,result.sideGroupsReason);
 assert.throws(()=>previewed(empty,{preview:{...result,sideGroupsReason:undefined}},{sideGroupsRequired:true,retained:true}),/Side memberships/);
 assert.throws(()=>previewed(empty,{preview:{...result,sideGroups:undefined,scheduleRefusal:{code:'rights_mark_required',message:'A published mark has not been adopted.'}}},{sideGroupsRequired:true,retained:true}),/Side memberships/);
 assert.throws(()=>previewed(empty,{preview:{...result,sideGroupsReason:null}},{sideGroupsRequired:true,retained:true}),/Side memberships/);
});

test('nonempty composition accepts an explicit null absence reason from normal backend serialization',()=>{
 const result={schedule:view('1'),summaries,quantityUnit:'package',links:[],sideGroups,sideGroupsReason:null};
 assert.deepEqual(previewed(empty,{preview:result},{sideGroupsRequired:true,retained:false}).sideGroups,sideGroups);
 assert.throws(()=>previewed(empty,{preview:{...result,sideGroupsReason:'No instrument'}},{sideGroupsRequired:true,retained:false}),/Side memberships/);
});

test('a refused replacement keeps the displayed exercise schedule and its original currency',()=>{
 const schedule={...view('1'),groups:[{right:0,from:'2026-11-01',to:'2026-11-02',state:'exercised',forwardSum:'1',counted:'1',inputs:null}]};
 const first=previewed(empty,{preview:{schedule,summaries,scheduleRefusal:null,quantityUnit:'MMBtu',currency:'USD',links:[]}});
 const refused=previewed(first,{preview:{schedule:null,summaries:null,scheduleRefusal:{code:'schedule_view',message:'This schedule is unavailable.'},
  quantityUnit:'GJ',currency:'EUR',links:[]}});
 assert.equal(refused.schedule,schedule);
 assert.equal(refused.status.kind,'stale');
 assert.equal(refused.currency,'USD');
});

test('a multi-clock ticket retains its priced groups and currency when its combined schedule is unavailable',()=>{
 const groups=[{right:0,from:'2041-01-01',to:'2041-01-02',state:'exercised',forwardSum:'2000',counted:'2000',inputs:null}];
 const preview={schedule:null,summaries:null,scheduleRefusal:{code:'schedule_view',message:'The sides keep different gas-day clocks.'},
  quantityUnit:'MMBtu',currency:'EUR',links:[],groups};
 const priced=previewed(empty,{preview:{...preview,schedule:view('1'),summaries,scheduleRefusal:null}},{retained:true});
 const result=previewed(priced,{preview},{retained:true});
 assert.equal(result.schedule,null);
 assert.deepEqual(result.groups,groups);
 assert.equal(result.currency,'EUR');
 assert.match(result.status.text,/different gas-day clocks/);
 const failed=previewed(priced,{preview:{...preview,groups:null,scheduleRefusal:{code:'exercise_conflict',message:'Conflicting delivery.'}}},{retained:true});
 assert.equal(failed.groups,undefined);
 assert.equal(failed.schedule,null);
 assert.deepEqual(failed.summaries,[]);
});

for(const [label,groups,code] of [['a mixed-clock schedule',[],'schedule_view'],['missing exercise marks',null,'exercise_marks']]){
 test(`an editable draft keeps its last priced grid and summaries after ${label} is refused`,()=>{
  const oldGroups=groups===null?[{right:0,from:'2041-01-01',to:'2041-01-02',state:'exercised',forwardSum:'2',counted:'2',inputs:null}]:[];
  const schedule={...view('1'),groups:oldGroups};
  const first=previewed(empty,{preview:{schedule,summaries,groups:oldGroups,scheduleRefusal:null,quantityUnit:'MMBtu',currency:'USD',links:[]}});
  const result=previewed(first,{preview:{schedule:null,summaries:null,groups,scheduleRefusal:{code,message:label},quantityUnit:'GJ',currency:'EUR',links:[]}},{retained:false});
  assert.equal(result.schedule,schedule);
  assert.deepEqual(result.summaries,summaries);
  assert.deepEqual([result.quantityUnit,result.currency,result.status.kind],['MMBtu','USD','stale']);
  assert.match(result.status.text,new RegExp(label));
  assert.deepEqual(result.groups,[]);
 });
}

test('an initial editable mark refusal retains its resolved unit and clears any gridless prior groups',()=>{
 const missing={schedule:null,summaries:null,groups:null,scheduleRefusal:{code:'exercise_marks',message:'Missing exercise marks.'},quantityUnit:'MMBtu',links:[]};
 const first=previewed(empty,{preview:missing},{retained:false});
 assert.equal(first.quantityUnit,'MMBtu');
 assert.equal(first.schedule,null);
 assert.equal(first.status.kind,'refused');
 const group={right:0,from:'2041-01-01',to:'2041-01-02',state:'exercised',forwardSum:'2',counted:'2',inputs:null};
 const priced=previewed(empty,{preview:{...missing,groups:[group],quantityUnit:'GJ',scheduleRefusal:{code:'schedule_view',message:'Different clocks.'}}},{retained:false});
 const next=previewed(priced,{preview:missing},{retained:false});
 assert.equal(next.quantityUnit,'MMBtu');
 assert.deepEqual(next.groups??[],[]);
});

test('an initial editable grid refusal keeps the unit and quantity links supplied with the reply',()=>{
 const links=[{source:'leg-1',target:'leg-2',formula:'gross_less_fraction',fraction:'0.02',receipt:'100',delivery:'98',fuel:'2'}];
 const reply={schedule:null,summaries:[],groups:[],scheduleRefusal:{code:'schedule_view',message:'Different clocks.'},quantityUnit:'MMBtu',currency:'USD',links};
 const panel=previewed(empty,{preview:reply},{retained:false});
 assert.deepEqual(panel.links,links);
 assert.deepEqual([panel.schedule,panel.quantityUnit,panel.currency,panel.status.kind],[null,'MMBtu','USD','refused']);
});
