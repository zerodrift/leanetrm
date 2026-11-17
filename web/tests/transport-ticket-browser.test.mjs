import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

const fixture=`
const {setupTransportTicket,readTransportView}=await import('/transport-trade-view.js');
window.calls=[];window.bookings=[];window.saved=[];window.historical=false;window.actor='synthetic-actor';
const product='physical_gas_transport';
const choices={profile:[{value:'physical_gas_transport_direct_rights_v1',label:'Direct whole-rights value'}],package_quantity:[{value:'whole_transport_package',label:'One whole Transport package'}],fee_direction:[{value:'pay',label:'Pay'},{value:'receive',label:'Receive'}]};
const field=(id,type='choice')=>({id,label:id,meaning:'Explicit synthetic contract term.',type,unit:null,state:'editable',required:true,band:null,choices:type==='choice'?id:null,dependsOn:[],lookup:null,soon:null,rule:null});
const description={product,version:1,rowShape:'flow',tradeFields:[field('profile'),field('package_quantity'),field('fee_direction'),field('fixed_fee_amount','decimal')],sideLimits:[],levels:['term'],modes:['new','booked'],tabs:[],tradeColumns:[],sides:[]};
const preview={source:{definitionRevisionId:'synthetic-definition',referenceCatalogId:'valuation-catalog',referenceRevisionId:'valuation-revision'},schedule:{levels:['term'],valuationAt:'2041-01-01T12:00:00Z',rows:[],totals:{},delivered:null,remaining:null,deliveredLeaves:0,leaves:0},summaries:[],links:[],scheduleRefusal:null,quantityUnit:'package'};
window.trade={...preview,draft:{product,version:1,trade:{profile:'physical_gas_transport_direct_rights_v1',package_quantity:'whole_transport_package',fee_direction:'pay',fixed_fee_amount:'12.50'},sides:[]},snapshotId:'synthetic-snapshot',definitionRevisionId:'synthetic-definition',context:{contextId:'synthetic-cutoff',effectiveOn:'2041-01-01',knownAt:'2041-01-01T19:00:00Z'},
 lifecycleTarget:{handler:'gas_transport_financial',scopeId:'synthetic-trade',bindingId:'synthetic-binding'},measureLabel:'Remaining Transport rights and fee PV',ownershipNote:'Whole rights and fee cash have independent value inputs.',unavailableReason:'rights_mark_required',
 overviewSections:[{label:'Financial state',values:[{label:'Capacity version',value:'Retained operating definition'},{label:'Fee due',value:'2041-01-02T12:00:00Z',kind:'instant'}]}],
 retainedChoices:{profile:[{value:'physical_gas_transport_direct_rights_v1',label:'Retained direct whole-rights contract'}],package_quantity:[{value:'whole_transport_package',label:'One retained whole Transport package'}],fee_direction:[{value:'pay',label:'Pay'}]},
 sideSlots:{},quantityPolicy:null,facilityAllocations:[],actions:{amend:{allowed:false,reason:'Use reviewed financial actions.'},closeOut:{allowed:false,reason:'Use explicit expiry.'},record:{allowed:false,reason:'No gas daily quantity applies.'}}};
window.holding={instrumentType:product,tradeId:'synthetic-trade',bindingId:'synthetic-binding',revision:'1',tradeReference:'Synthetic Transport agreement',instrumentReference:'Synthetic whole package',book:'Synthetic book',executedAt:'2040-12-01T12:00:00Z',trader:'Synthetic trader',booking:{tradeDate:'2040-12-01'}};
const api=async(path,body)=>{calls.push({path,body:structuredClone(body)});
 if(path==='tickets/description')return description;
 if(path==='tickets/choices')return {choices:choices[body.field],starting:null};
 if(path==='tickets/preview')return preview;
 if(path==='tickets/trade')return trade;
 if(path==='tickets/book'){bookings.push(structuredClone(body));throw Error('Synthetic acknowledgement lost');}
 if(path==='tickets/status')return {operationId:bookings[0].operationId,tradeId:'synthetic-trade',instrumentId:'synthetic-binding',revision:1};
 throw Error('Unexpected operation '+path);
};
window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Transport ticket condition not ready');};
window.editor=setupTransportTicket({api,source:()=>({snapshotId:'synthetic-snapshot'}),historical:()=>historical,actor:()=>actor,instant:at=>at,zone:()=>'UTC',onBooked:async id=>saved.push(id)});
window.setTerm=(id,value)=>{const control=document.querySelector('[data-field="'+id+'"] .ticket-control');control.value=value;control.dispatchEvent(new Event('change'));};
`;
const page='<div id="transport-page"></div><div id="terms"></div>';

test('Transport entry requires explicit package and fee choices and recovers the original product request',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}await editor.begin();await until(()=>document.querySelector('[data-field=profile] select')?.options.length===2);})()`);
  assert.deepEqual(await evaluate('[...document.querySelectorAll(".ticket-control")].map(control=>control.value)'),['','','','']);
  assert.equal(await evaluate("document.querySelector('[data-field=package_quantity] select')!==null"),true);
  assert.equal(await evaluate("calls.find(c=>c.path==='tickets/description').body.product"),'physical_gas_transport');
  await evaluate(`(async()=>{for(const[id,value]of Object.entries({profile:'physical_gas_transport_direct_rights_v1',package_quantity:'whole_transport_package',fee_direction:'pay',fixed_fee_amount:'12.50'}))setTerm(id,value);
   await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>bookings.length===1&&editor.pending());
   editor.reset();await editor.begin();await until(()=>document.querySelectorAll('.ticket-booking button').length===2);
   [...document.querySelectorAll('.ticket-booking button')].find(button=>button.textContent==='Check booking status').click();await until(()=>saved.length===1);
  })()`);
  assert.deepEqual(await evaluate('saved'),['synthetic-trade']);
  assert.equal(await evaluate('bookings.length'),1);
  assert.equal(await evaluate('bookings[0].draft.trade.package_quantity'),'whole_transport_package');
  assert.equal(await evaluate("calls.find(c=>c.path==='tickets/status').body.product"),'physical_gas_transport');
  assert.equal(await evaluate("calls.find(c=>c.path==='tickets/status').body.profile"),'physical_gas_transport_direct_rights_v1');
  assert.equal(await evaluate('editor.pending()'),false);
 });
});

test('Transport entry refuses historical input before any reads',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  assert.match(await evaluate(`(async()=>{${fixture}historical=true;return editor.begin().then(()=>'',error=>error.message);})()`),/Return to working data/);
  assert.deepEqual(await evaluate('calls'),[]);
 });
});

test('an unpriced Transport reopens exact retained terms and local-time evidence without a price or quantity fallback',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}window.opened=0;window.detail=await readTransportView(api,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor,lifecycle:()=>opened++});
   const view=detail.view('America/Chicago');window.rows=view.terms[0].rows;view.termsContent(document.querySelector('#terms'));view.actions[0].run();await until(()=>document.querySelector('[data-field=package_quantity] .ticket-value'));
  })()`);
  assert.equal(await evaluate('opened'),1);
  assert.equal(await evaluate("rows.find(([label])=>label==='Value unavailable')[1]"),'No current whole-rights mark has been adopted');
  assert.equal(await evaluate("document.querySelector('[data-field=package_quantity] .ticket-value').textContent"),'One retained whole Transport package');
  assert.match(await evaluate("document.querySelector('[data-field=\"Fee due\"] dd').textContent"),/6:00 AM CST/);
  assert.deepEqual(await evaluate("calls.filter(c=>c.path==='tickets/trade').map(c=>[c.body.product,c.body.tradeId,c.body.bindingId,c.body.context.contextId])"),[
   ['physical_gas_transport','synthetic-trade','synthetic-binding','synthetic-cutoff'],['physical_gas_transport','synthetic-trade','synthetic-binding','synthetic-cutoff']]);
  assert.equal(await evaluate("document.querySelectorAll('#terms input,#terms select,#terms button').length"),0);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/choices').length"),0,'retained financial terms must not perform operating source discovery');
 });
});

for(const [label,change]of [['missing',"delete trade.retainedChoices"],['wrong value',"trade.retainedChoices.package_quantity[0].value='gas_quantity'"],['extra field',"trade.retainedChoices.owned_account=[{value:'unexpected',label:'Unrequested account'}]"]])
test('retained financial choice labels refuse '+label+' metadata without discovery fallback',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}${change};const detail=await readTransportView(api,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor});
   detail.view('UTC').termsContent(document.querySelector('#terms'));await until(()=>document.querySelector('#terms [role=alert]'));
  })()`);
  assert.match(await evaluate("document.querySelector('#terms [role=alert]').textContent"),/retained choice labels/i);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/choices').length"),0);
  assert.equal(await evaluate("document.querySelector('#terms [data-field=package_quantity]')===null"),true);
 });
});

test('a future financial head leaves availability and evidence visible without reconstructing an obsolete ticket',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}trade.draft=null;trade.unavailableReason='financial_state_future';
   window.detail=await readTransportView(api,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor});
   const view=detail.view('UTC');window.rows=view.terms[0].rows;view.termsContent(document.querySelector('#terms'));
  })()`);
  assert.equal(await evaluate("rows.find(([label])=>label==='Value unavailable')[1]"),'Latest financial state is not yet effective in this view');
  assert.equal(await evaluate("document.querySelector('#terms .ticket')===null"),true);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/trade').length"),1);
  assert.equal(await evaluate("document.querySelector('[data-field=\"Capacity version\"] dd').textContent"),'Retained operating definition');
 });
});

test('a Transport detail reply from another agreement is withheld before terms mount',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  assert.match(await evaluate(`(async()=>{${fixture}trade.lifecycleTarget.scopeId='another-trade';return readTransportView(api,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor}).then(()=>'',error=>error.message);})()`),/selected agreement/);
  assert.equal(await evaluate("document.querySelector('#terms').children.length"),0);
 });
});

test('the second retained ticket read cannot replace its exact target under the same context',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}let reads=0;const misrouted=async(path,body,signal)=>{const reply=await api(path,body,signal);return path==='tickets/trade'&&++reads===2?{...reply,lifecycleTarget:{...reply.lifecycleTarget,scopeId:'another-agreement'}}:reply;};
   const detail=await readTransportView(misrouted,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor});detail.view('UTC').termsContent(document.querySelector('#terms'));
   await until(()=>document.querySelector('#terms [role=alert]')||document.querySelector('#terms [data-field=package_quantity]'));
  })()`);
  assert.equal(await evaluate("document.querySelector('#terms [role=alert]')!==null"),true);
  assert.match(await evaluate("document.querySelector('#terms [role=alert]').textContent"),/exact retained target/i);
  assert.equal(await evaluate("document.querySelector('#terms [data-field=package_quantity]')===null"),true);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/choices').length"),0);
 });
});
