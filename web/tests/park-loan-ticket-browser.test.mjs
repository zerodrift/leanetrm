import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

const fixture=`
const {setupParkLoanTicket,readParkLoanView}=await import('/park-loan-trade-view.js');
window.calls=[];window.bookings=[];window.saved=[];window.inspections=[];window.historical=false;window.wrongReadContext=false;window.actor='synthetic-actor';
const product='physical_gas_park_loan';
const choices={profile:[{value:'physical_gas_park_loan_v1',label:'Title-transfer, same-point fixed-fee profile'}],perspective:[{value:'customer',label:'Customer'},{value:'provider',label:'Provider'}],return_policy:[{value:'equal_quantity_same_point_basis_v1',label:'Equal quantity at the same point and basis'}],fee_direction:[{value:'pay',label:'Pay'},{value:'receive',label:'Receive'}]};
const field=id=>({id,label:id,meaning:'Explicit synthetic contract term.',type:id==='fixed_fee_amount'?'decimal':'choice',unit:null,state:'editable',required:true,band:null,choices:id==='fixed_fee_amount'?null:id,dependsOn:[],lookup:null,soon:null,rule:null});
const description={product,version:1,rowShape:'flow',tradeFields:['profile','perspective','return_policy','fee_direction','fixed_fee_amount'].map(field),sideLimits:[],levels:['term'],modes:['new','booked'],tabs:[],tradeColumns:[],sides:[]};
const schedule={levels:['term'],valuationAt:'2041-01-01T12:00:00Z',rows:[{key:'term',parent:null,level:'term',start:'2041-02-01T12:00:00Z',end:'2041-02-08T12:00:00Z',operatingDate:null,cells:{},delivered:false}],totals:{},delivered:null,remaining:null,deliveredLeaves:0,leaves:1};
const preview={source:{definitionRevisionId:'synthetic-definition',referenceCatalogId:'synthetic-catalog',referenceRevisionId:'synthetic-reference'},schedule,summaries:[],links:[],scheduleRefusal:null,quantityUnit:'MMBtu'};
const draft={product,version:1,trade:{profile:'physical_gas_park_loan_v1',perspective:'provider',return_policy:'equal_quantity_same_point_basis_v1',fee_direction:'receive',fixed_fee_amount:'23.456789012345678901'},sides:[]};
const trade={...preview,draft,definitionRevisionId:'synthetic-definition',snapshotId:'synthetic-snapshot',context:{contextId:'synthetic-cutoff',effectiveOn:'2041-01-01',knownAt:'2041-01-01T19:00:00Z'},sideSlots:{},actions:{amend:{allowed:false,reason:'Amendment is not available for this profile.'},closeOut:{allowed:false,reason:'Use native lifecycle actions.'},record:{allowed:false,reason:'Use native lifecycle actions.'}},quantityPolicy:null,facilityAllocations:[],measureLabel:'Remaining scheduled plan PV',ownershipNote:'Accepted principal and fee cash have independent native owners.',overviewSections:[{label:'Performance',values:[{label:'Accepted advance',value:'720 MMBtu'},{label:'Accepted returns',value:'270 MMBtu'},{label:'Outstanding return',value:'450 MMBtu'}]},{label:'Fixed fee',values:[{label:'Unpaid fee',value:'23.456789012345678901 USD'}]}]};
const api=async(path,body)=>{
 calls.push({path,body:structuredClone(body)});
 if(path==='tickets/description')return description;
 if(path==='tickets/choices')return {choices:choices[body.field],starting:null};
 if(path==='tickets/preview')return preview;
 if(path==='time/inspect')return new Promise(resolve=>inspections.push({body,resolve}));
 if(path==='tickets/trade')return {...trade,context:{...trade.context,contextId:wrongReadContext?'other-cutoff':trade.context.contextId}};
 if(path==='tickets/book'){bookings.push(structuredClone(body));throw Error('Synthetic response lost');}
 if(path==='tickets/status')return {operationId:bookings[0].operationId,tradeId:'synthetic-trade',instrumentId:'synthetic-binding',revision:1};
 throw Error('Unexpected operation '+path);
};
window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Park and loan condition not ready');};
window.editor=setupParkLoanTicket({api,source:()=>({snapshotId:'synthetic-snapshot'}),historical:()=>historical,actor:()=>actor,instant:at=>at,zone:()=>'UTC',onBooked:async id=>saved.push(id)});
window.setTerm=(id,value)=>{const control=document.querySelector('[data-field="'+id+'"] .ticket-control');control.value=value;control.dispatchEvent(new Event('change'));};
window.holding={instrumentType:product,tradeId:'synthetic-trade',bindingId:'synthetic-binding',revision:'1',tradeReference:'Synthetic service',instrumentReference:'Synthetic agreement',book:'Synthetic book',executedAt:'2040-12-01T12:00:00Z',trader:'Synthetic trader',booking:{tradeDate:'2040-12-01'}};
`;
const page='<div id="park-loan-page"></div><div id="terms"></div>';

test('Park and loan entry keeps financial choices empty and recovers its original shared booking request',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}await editor.begin();await until(()=>document.querySelector('[data-field=profile] select')?.options.length===2);})()`);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.ticket-control')].map(control=>control.value)"),['','','','','']);
  assert.equal(await evaluate("calls.find(c=>c.path==='tickets/description').body.product"),'physical_gas_park_loan');
  assert.equal(await evaluate("calls[0].body.context.convention"),'physical_gas_fixed_quantity_v4');
  await evaluate(`(async()=>{
   for(const[id,value]of Object.entries({profile:'physical_gas_park_loan_v1',perspective:'provider',return_policy:'equal_quantity_same_point_basis_v1',fee_direction:'receive',fixed_fee_amount:'23.456789012345678901'}))setTerm(id,value);
   await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>bookings.length===1&&editor.pending());
   editor.reset();await editor.begin();await until(()=>document.querySelectorAll('.ticket-booking button').length===2);
  })()`);
  assert.equal(await evaluate('editor.pending()'),true);
  assert.equal(await evaluate('bookings[0].draft.trade.fixed_fee_amount'),'23.456789012345678901');
  assert.equal(await evaluate('bookings[0].draft.trade.fee_direction'),'receive');
  await evaluate("(async()=>{[...document.querySelectorAll('.ticket-booking button')].find(button=>button.textContent==='Check booking status').click();await until(()=>saved.length===1);})()");
  assert.deepEqual(await evaluate('saved'),['synthetic-trade']);
  assert.equal(await evaluate('bookings.length'),1);
  assert.equal(await evaluate("calls.find(c=>c.path==='tickets/status').body.product"),'physical_gas_park_loan');
  assert.equal(await evaluate('editor.pending()'),false);
 });
});

test('historical entry refuses before reading or sending contract terms',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  const message=await evaluate(`(async()=>{${fixture}historical=true;return editor.begin().then(()=>'',error=>error.message);})()`);
  assert.match(message,/Return to working data/);
  assert.deepEqual(await evaluate('calls'),[]);
 });
});

test('reopened Park and loan terms keep their certified context and link to the native lifecycle workspace',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}
   window.lifecycleOpened=0;window.detail=await readParkLoanView(api,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor,lifecycle:()=>lifecycleOpened++});
   document.querySelector('#terms').innerHTML='<p id="position-summary">Position summary</p>';
   const view=detail.view('UTC');view.termsContent(document.querySelector('#terms'));view.actions[0].run();
   await until(()=>document.querySelector('#terms [data-field=perspective] .ticket-value')?.textContent==='Provider');
  })()`);
  assert.equal(await evaluate('lifecycleOpened'),1);
  assert.equal(await evaluate("document.querySelector('#position-summary')?.textContent"),'Position summary');
  assert.equal(await evaluate("document.querySelector('#terms .side-term[data-field=\"Outstanding return\"] dd')?.textContent"),'450 MMBtu');
  assert.equal(await evaluate("document.querySelector('#terms .side-term[data-field=\"Unpaid fee\"] dd')?.textContent"),'23.456789012345678901 USD');
  assert.equal(await evaluate("document.querySelector('#terms [data-field=fixed_fee_amount] .ticket-value').textContent"),'23.456789012345678901');
  assert.deepEqual(await evaluate("calls.filter(c=>c.path==='tickets/trade').map(c=>[c.body.product,c.body.tradeId,c.body.bindingId,c.body.revision,c.body.context.contextId])"),[
   ['physical_gas_park_loan','synthetic-trade','synthetic-binding',1,'synthetic-cutoff'],['physical_gas_park_loan','synthetic-trade','synthetic-binding',1,'synthetic-cutoff']]);
  assert.equal(await evaluate("document.querySelectorAll('#terms input,#terms select,#terms button').length"),0);
 });
});

test('a reopened terms response cannot replace the selected knowledge context',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}
   const detail=await readParkLoanView(api,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor});
   wrongReadContext=true;detail.view('UTC').termsContent(document.querySelector('#terms'));await until(()=>!!document.querySelector('#terms [role=alert]'));
  })()`);
  assert.match(await evaluate("document.querySelector('#terms [role=alert]').textContent"),/certified snapshot and context/);
  assert.equal(await evaluate("document.querySelector('#terms [data-field=perspective]')===null"),true);
 });
});

test('an explicitly selected Not applicable fee date survives an earlier local-time response',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}
   description.tradeFields.push({...field('fee_due_at'),label:'Fixed fee due',type:'datetime',choices:null,allowNotApplicable:true});
   await editor.begin();await until(()=>!!document.querySelector('[data-field=fee_due_at]'));
  })()`);
  assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] [data-role=not-applicable]')?.checked"),false);
  assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] .ticket-control').value"),'');
  await evaluate(`(async()=>{
   const input=document.querySelector('[data-field=fee_due_at] .ticket-control');input.value='2041-02-10T12:00';input.dispatchEvent(new Event('change'));await until(()=>inspections.length===1);
   document.querySelector('[data-field=fee_due_at] [data-role=not-applicable]').click();
   inspections[0].resolve({candidates:[{instant:'2041-02-10T12:00:00Z'}]});
   await until(()=>calls.filter(c=>c.path==='tickets/preview').at(-1)?.body.draft.trade.fee_due_at==='none');
  })()`);
  assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] .ticket-control').disabled"),true);
  assert.equal(await evaluate("calls.filter(c=>c.path==='time/inspect').length"),1);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/preview').at(-1).body.draft.trade.fee_due_at"),'none');
  await evaluate("document.querySelector('[data-field=fee_due_at] [data-role=not-applicable]').click()");
  assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] .ticket-control').disabled"),false);
  assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] .ticket-control').value"),'');
 });
});

test('retained Not applicable dates use the explicit label without invoking a date formatter',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}
   description.tradeFields.push({...field('fee_due_at'),label:'Fixed fee due',type:'datetime',choices:null,allowNotApplicable:true});draft.trade.fee_due_at='none';
   const detail=await readParkLoanView(api,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor});detail.view('UTC').termsContent(document.querySelector('#terms'));
   await until(()=>!!document.querySelector('#terms [data-field=fee_due_at]'));
  })()`);
  assert.equal(await evaluate("document.querySelector('#terms [data-field=fee_due_at] .ticket-value').textContent"),'Not applicable');
  assert.equal(await evaluate("calls.some(c=>c.path==='time/inspect')"),false);
 });
});

test('a zone change during time resolution requires visible explicit re-entry without canceling another field',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const field=id=>({id,label:id,meaning:'Explicit synthetic agreement time.',type:'datetime',unit:null,state:'editable',required:true,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null});
   const description={product:'physical_gas_park_loan',version:1,rowShape:'flow',tradeFields:[field('advance_at'),field('return_at')],sideLimits:[],levels:['term'],modes:['new'],tabs:[],tradeColumns:[],sides:[]};
   window.reviewZone='UTC';window.inspections=[];window.previews=[];
   const api=async(path,body)=>{
    if(path==='tickets/description')return description;
    if(path==='time/inspect')return new Promise(resolve=>inspections.push({body,resolve}));
    if(path==='tickets/preview'){previews.push(body);return {source:{definitionRevisionId:'synthetic-definition',referenceCatalogId:'synthetic-catalog',referenceRevisionId:'synthetic-reference'},schedule:null,summaries:[],links:[],quantityUnit:'synthetic_energy',scheduleRefusal:{code:'schedule_view',message:'Synthetic service has no Side schedule.'}};}
    throw Error('Unexpected '+path);
   };
   window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({snapshotId:'synthetic-snapshot'}),product:description.product,instant:at=>at,zone:()=>reviewZone,actor:()=>'synthetic-clock-owner',booked:()=>{}});
   window.until=async check=>{for(let i=0;i<300&&!check();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!check())throw Error('Synthetic clock condition not ready');};
   window.enterTime=(field,value)=>{const input=document.querySelector('[data-field='+field+'] input');input.value=value;input.onchange();};
   await until(()=>!!document.querySelector('[data-field=advance_at] input'));
   enterTime('advance_at','2041-02-10T12:00');enterTime('return_at','2041-02-15T12:00');await until(()=>inspections.length===2);
   inspections[1].resolve({candidates:[{instant:'2041-02-15T12:00:00Z'}]});inspections[0].resolve({candidates:[{instant:'2041-02-10T12:00:00Z'}]});
   await until(()=>document.querySelector('.ticket-book')?.disabled===false);
  })()`);
  assert.deepEqual(await evaluate('previews.at(-1).draft.trade'),{advance_at:'2041-02-10T12:00:00Z',return_at:'2041-02-15T12:00:00Z'},'independent fields retain both answers when they resolve in reverse order');
  await evaluate(`(async()=>{
   enterTime('advance_at','2041-02-11T12:00');await until(()=>inspections.length===3);
   reviewZone='America/Chicago';inspections[2].resolve({candidates:[{instant:'2041-02-11T12:00:00Z'}]});await new Promise(resolve=>setTimeout(resolve,400));
  })()`);
  assert.equal(await evaluate("document.querySelector('.ticket-book').disabled"),true);
  const message=await evaluate("document.querySelector('[data-field=advance_at] [role=alert]')?.textContent??''");
  assert.match(message,/zone.*changed/i,'a dropped old-zone answer must explain why explicit re-entry is required');
  assert.match(message,/re-enter|enter.*again/i);
  assert.equal(await evaluate('previews.at(-1).draft.trade.advance_at'),'2041-02-10T12:00:00Z','the older accepted instant is not silently replaced');
  await evaluate(`(async()=>{
   enterTime('advance_at','2041-02-12T13:00');await until(()=>inspections.length===4);
   inspections[3].resolve({candidates:[{instant:'2041-02-12T19:00:00Z'}]});await until(()=>document.querySelector('.ticket-book')?.disabled===false);
  })()`);
  assert.equal(await evaluate('inspections[3].body.timeZone'),'America/Chicago');
  assert.deepEqual(await evaluate('previews.at(-1).draft.trade'),{advance_at:'2041-02-12T19:00:00Z',return_at:'2041-02-15T12:00:00Z'});
  assert.equal(await evaluate("document.querySelector('[data-field=advance_at] [role=alert]')===null"),true);
  await evaluate('ticket.destroy()');
 });
});
