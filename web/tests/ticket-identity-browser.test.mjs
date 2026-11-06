import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

async function fixture(work,levels=['month','day'],amend=false){
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><div id="host" style="width:1100px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const field={id:'reference',label:'Reference',meaning:'',type:'text',unit:null,state:'editable',required:false,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null};
   const physical={kind:'physical',pill:'Physical',colour:'gas',min:1,initial:1,max:1,soon:null,fields:[],columns:[{id:'quantity',label:'Quantity',unit:'quantity',measure:'quantity',input:['booked']}]};
   const description={product:'synthetic_identity',version:1,rowShape:'flow',tradeFields:[field],sideLimits:[],levels:${JSON.stringify(levels)},modes:${JSON.stringify(amend?['new','booked','amend']:['new','booked'])},tabs:[],tradeColumns:[],sides:[physical]};
   const cells={'physical-1.quantity':{value:'1000',sources:['contract']}};
   const row=(key,parent,level)=>({key,parent,level,start:'',end:'',operatingDate:null,cells,delivered:false});
   const preview={schedule:{levels:['month','day'],valuationAt:'2041-01-01T00:00:00Z',deliveredLeaves:0,leaves:1,delivered:null,remaining:null,totals:cells,rows:[row('2041-02',null,'month'),row('2041-02-01','2041-02','day')]},summaries:[],links:[],scheduleRefusal:null,quantityUnit:'MMBtu'};
   const trade={...preview,definitionRevisionId:'retained-definition',draft:{product:description.product,version:1,trade:{reference:'Retained'},sides:[{kind:'physical',code:'physical-1',values:{}}]},quantityPolicy:'synthetic_quantity',sideSlots:{'physical-1':9},snapshotId:'frame-a',context:{contextId:'ticket-cutoff-a',effectiveOn:'2041-01-01',knownAt:'2041-01-01T18:00:00Z'},actions:{amend:{allowed:${amend},reason:${amend?'null':"'No'"}},closeOut:{allowed:false,reason:'No'},record:{allowed:true,reason:'Record day'}}};
   window.actor='actor-a';window.callbacks=[];window.calls=[];window.bookings=[];window.release=null;window.done=false;window.nativeSchedules=[];window.useNative=false;window.holdTrade=false;window.releaseTrade=null;window.advanceQuantityContext=()=>{trade.context={...trade.context,contextId:"ticket-cutoff-b",knownAt:"2041-01-01T19:00:00Z"};trade.schedule.rows.find(r=>r.level==="day").cells={"physical-1.quantity":{value:"1050",sources:["nominated"]}};};
   const api=async(path,body)=>{
    calls.push({path,body});
    if(path==='tickets/description'){if(window.holdDescription)return new Promise(resolve=>window.releaseDescription=()=>resolve(description));if(window.refuseDescription)throw Error('Synthetic metadata refusal');return description;}
    if(path==='tickets/trade'){if(holdTrade){const held=structuredClone(trade);return new Promise(resolve=>{releaseTrade=()=>resolve(held);});}return trade;}
    if(path==='tickets/preview'||path==='tickets/amend-preview'){if(window.holdPreview)return new Promise(resolve=>window.releasePreview=()=>resolve(preview));return preview;}
    if(path==='tickets/book'||path==='tickets/amend')return await new Promise((resolve,reject)=>{const receipt={operationId:body.operationId,tradeId:'trade-a',instrumentId:'instrument-a',revision:path==='tickets/amend'?2:1};bookings.push({body,receipt,resolve:()=>resolve(receipt),reject:()=>reject(Error('Unknown response'))});window.release=()=>{resolve(receipt);setTimeout(()=>window.done=true,20);};});
    if(path==='tickets/status')return bookings.find(b=>b.body.operationId===body.operationId).receipt;
    if(path==='gas/quantities/read')return {tradeId:'trade-a',expectedRevision:1,rows:[{sideCode:'physical-1',day:'2041-02-01',obligationId:'obligation-a',heads:[]}]};
    if(path==='gas/quantities/record'){
     window.recordReceipt={operationId:body.operationId,tradeId:body.tradeId,obligationId:body.obligationId,revisionId:'quantity-revision',revision:1,unchanged:false};
     return await new Promise(resolve=>window.releaseRecord=()=>{resolve(recordReceipt);setTimeout(()=>window.recordDone=true,20);});
    }
    if(path==='gas/quantities/status')return recordReceipt;
    throw Error('Unexpected '+path);
   };
   window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Ticket condition not ready');};
   window.open=async(booked=false,readOnly=false,amending=false)=>{
    window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({snapshotId:'frame-a',knowledgeMode:'current'}),product:description.product,instant:at=>at,zone:()=>'UTC',actor:()=>window.actor,readOnly,
     ...(window.useNative?{sideSchedule:(host,side,slot,view,context)=>{nativeSchedules.push({code:side.code,slot,context,quantity:view.schedule.rows.find(r=>r.level==='day').cells[side.code+'.quantity'].value});host.append(document.createElement('table'));return()=>host.replaceChildren();}}:{}),
     ...(booked?{opened:{tradeId:'trade-a',bindingId:'binding-a',revision:1}}:{booked:r=>callbacks.push(r),...(amending?{amending:{tradeId:'trade-a',bindingId:'binding-a',revision:1}}:{})})});
    await until(()=>!!document.querySelector('.deal-ticket'));
   };
   window.day=async()=>{const level=document.querySelector('.ticket-level');level.value='day';level.onchange();document.querySelector('.ticket-tab[data-tab="physical-1"]').click();await until(()=>!!document.querySelector('.ticket-cell-input'));};
  })()`);
  await work(evaluate);
 });
}

test('gas amendment edits retained terms and saves a new revision through the amendment operation',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate('(async()=>{await open(false,false,true);await until(()=>!!document.querySelector("[data-field=reference] input"));})()');
  assert.equal(await evaluate("document.querySelector('[data-field=reference] input').value"),'Retained');
  assert.equal(await evaluate("document.querySelector('.ticket-book').textContent"),'Save amendment');
  await evaluate(`(async()=>{
   const reason=document.querySelector('[data-role=amendment-reason]');reason.value='Synthetic correction';reason.dispatchEvent(new Event('input'));
   const field=document.querySelector('[data-field=reference] input');field.value='Amended terms';field.onchange();
   await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>bookings.length===1);
  })()`);
  const sent=await evaluate("calls.find(c=>c.path==='tickets/amend').body");
  assert.equal(sent.tradeId,'trade-a');assert.equal(sent.bindingId,'binding-a');assert.equal(sent.expectedRevision,1);
  assert.equal(sent.reason,'Synthetic correction');assert.equal(sent.policy,'future_gas_composition_replace_v1');
  assert.equal(sent.definitionRevisionId,'retained-definition');
  assert.equal(sent.draft.trade.reference,'Amended terms');
  assert.equal(await evaluate("calls.some(c=>c.path==='tickets/book')"),false);
  await evaluate('(async()=>{release();await until(()=>callbacks.length===1);})()');
  assert.deepEqual(await evaluate('callbacks'),[{tradeId:'trade-a'}]);
 },['month','day'],true);
});

test('an amendment with a lost reply remains recoverable when ticket metadata cannot load',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   await open(false,false,true);
   const reason=document.querySelector('[data-role=amendment-reason]');reason.value='Synthetic correction';reason.dispatchEvent(new Event('input'));
   await until(()=>calls.some(c=>c.path==='tickets/amend-preview'));
   await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>bookings.length===1);
   ticket.destroy();window.refuseDescription=true;
   // The retained request must be recoverable even though no ticket can be drawn.
   void open(false,false,true).catch(()=>{});await new Promise(resolve=>setTimeout(resolve,100));
  })()`);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.ticket-booking button')].map(b=>b.textContent)"),['Check amendment status','Retry the original amendment']);
  await evaluate(`(async()=>{document.querySelector('.ticket-booking button').click();await until(()=>callbacks.length===1);})()`);
  assert.equal(await evaluate("sessionStorage.getItem('deal-ticket-request:synthetic_identity:actor-a')"),null);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/amend').length"),1,'checking never rebooks');
 },['month','day'],true);
});

test('a one-month ticket selects a level its returned schedule actually contains',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate('(async()=>{await open(true);await until(()=>document.querySelectorAll(".tabulator-row").length>0);})()');
  assert.equal(await evaluate("document.querySelector('.ticket-level').value"),'month');
 },['term','month','day']);
});

test('a late gas booking receipt leaves its original request recoverable without changing the new actor or navigating',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   await open();const field=document.querySelector('[data-field=reference] input');field.value='Original actor';field.onchange();
   await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>window.release!==null);
   ticket.destroy();actor='actor-b';sessionStorage.setItem('deal-ticket-request:synthetic_identity:actor-b','new actor retained request');release();await until(()=>done);
  })()`);
  assert.equal(await evaluate("sessionStorage.getItem('deal-ticket-request:synthetic_identity:actor-b')"),'new actor retained request');
  assert.equal(await evaluate("JSON.parse(sessionStorage.getItem('deal-ticket-request:synthetic_identity:actor-a')).operationId===bookings[0].body.operationId"),true);
  assert.deepEqual(await evaluate('callbacks'),[]);
 });
});

for(const outcome of ['resolve','reject'])test(`an abandoned booking ${outcome} cannot replace the same actor's newer retained request`,{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   const book=async value=>{const field=document.querySelector('[data-field=reference] input');field.value=value;field.onchange();await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();};
   await open();await book('Original X');await until(()=>bookings.length===1);ticket.destroy();
   await open();[...document.querySelectorAll('.ticket-booking button')].find(b=>b.textContent==='Check booking status').click();await until(()=>callbacks.length===1);ticket.destroy();
   await open();await book('New Y');await until(()=>bookings.length===2);bookings[0].${outcome}();await new Promise(resolve=>setTimeout(resolve,60));
  })()`);
  assert.equal(await evaluate("JSON.parse(sessionStorage.getItem('deal-ticket-request:synthetic_identity:actor-a')).operationId===bookings[1].body.operationId"),true);
  assert.equal(await evaluate('callbacks.length'),1,'the abandoned view cannot navigate the current ticket');
 });
});

test('booked gas day-entry state belongs to its actor when the same trade is reopened',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await open(true);await day();const cell=document.querySelector('.ticket-cell-input');cell.value='990';cell.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('.ticket-record'));ticket.destroy();actor='actor-b';await open(true);await day();})()");
  assert.equal(await evaluate("document.querySelector('.ticket-record')===null"),true);
  await evaluate('(async()=>{ticket.destroy();actor="actor-a";await open(true);await day();})()');
  assert.equal(await evaluate("document.querySelector('.ticket-record [data-field=quantity] .ticket-control').value"),'990','the original actor keeps their unsubmitted entry');
 });
});

test('read-only gas tickets offer no writable day cells even when the account may record elsewhere',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await open(true,true);const level=document.querySelector('.ticket-level');level.value='day';level.onchange();document.querySelector('.ticket-tab[data-tab=\"physical-1\"]').click();await new Promise(resolve=>setTimeout(resolve,100));})()");
  assert.equal(await evaluate("document.querySelectorAll('.ticket-cell-input').length"),0);
  assert.equal(await evaluate("document.querySelector('.ticket-record')===null"),true);
 });
});

test('a late day-record reply leaves the reopened ticket able to check its original outcome',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   await open(true);await day();const cell=document.querySelector('.ticket-cell-input');cell.value='990';cell.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('.ticket-record'));
   for(const [name,value]of [['kind','nominated'],['status','provisional'],['evidence','Synthetic nomination']]){const input=document.querySelector('.ticket-record [data-field='+name+'] .ticket-control');input.value=value;input.onchange();}
   document.querySelector('.ticket-record-send').click();await until(()=>!!window.releaseRecord);
   ticket.destroy();await open(true);await day();releaseRecord();await until(()=>window.recordDone);
   document.querySelector('.ticket-record-check').click();await new Promise(resolve=>setTimeout(resolve,60));
  })()`);
  assert.equal(await evaluate("calls.filter(c=>c.path==='gas/quantities/status').length"),1);
  assert.equal(await evaluate("document.querySelector('.ticket-record')===null"),true);
 });
});


test('booked Side tabs use the backend membership slot rather than a Side array position',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{useNative=true;await open(true);await until(()=>nativeSchedules.length===1);})()");
  assert.equal(await evaluate('nativeSchedules[0].slot'),9);
  assert.equal(await evaluate('nativeSchedules[0].context.contextId'),'ticket-cutoff-a');
  assert.equal(await evaluate('nativeSchedules[0].context.knowledgeMode'),'retained');
  assert.equal(await evaluate("document.querySelector('.ticket-tab[aria-selected=true]').dataset.tab"),'physical-1');
  assert.equal(await evaluate("document.querySelectorAll('.ticket-grid table').length"),1);
  assert.equal(await evaluate("document.querySelectorAll('.ticket-grid .tabulator').length"),0);
 });
});


test('an intervening quantity revision cannot mix a held ticket response with a newer native schedule context',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{useNative=true;holdTrade=true;void open(true);await until(()=>!!releaseTrade);advanceQuantityContext();releaseTrade();await until(()=>nativeSchedules.length===1);})()");
  assert.equal(await evaluate('nativeSchedules[0].context.contextId'),'ticket-cutoff-a');
  assert.equal(await evaluate('nativeSchedules[0].context.knowledgeMode'),'retained');
  assert.equal(await evaluate('nativeSchedules[0].quantity'),'1000');
 });
});

test('restored pending terms stay locked before metadata arrives while recovery remains usable',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   await open();const field=document.querySelector('[data-field=reference] input');field.value='Pending original';field.onchange();
   await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>bookings.length===1);
   ticket.destroy();window.holdDescription=true;void open();await until(()=>typeof releaseDescription==='function');
  })()`);
  assert.equal(await evaluate("document.querySelector('#host > div').inert"),true,'uncertain terms are locked before description loading completes');
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.ticket-booking button')].map(b=>[b.textContent,b.disabled])"),[['Check booking status',false],['Retry the original booking',false]]);
  await evaluate("(async()=>{document.querySelector('.ticket-booking button').click();await until(()=>callbacks.length===1);})()");
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/book').length"),1);
 });
});


for(const revert of [false,true])test(revert?'round-trip amendment reason edits cannot reuse an earlier preview while the latest preview is held':'editing only an amendment reason invalidates the displayed preview and preserves one recoverable write',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   await open(false,false,true);
   const reason=document.querySelector('[data-role=amendment-reason]');reason.value='First reviewed reason';reason.dispatchEvent(new Event('input'));
   await until(()=>calls.some(c=>c.path==='tickets/amend-preview')&&document.querySelector('.ticket-status')?.dataset.kind==='current');
   window.holdPreview=true;reason.value='Changed reviewed reason';reason.dispatchEvent(new Event('input'));
   if(${revert}){reason.value='First reviewed reason';reason.dispatchEvent(new Event('input'));}
  })()`);
  assert.equal(await evaluate("document.querySelector('.ticket-status').dataset.kind"),'updating','reason edits must not leave an older preview marked current');
  await evaluate(`(async()=>{await until(()=>typeof releasePreview==='function');document.querySelector('.ticket-book').click();})()`);
  assert.equal(await evaluate('bookings.length'),0,'a held reason preview cannot save a command');
  await evaluate(`(async()=>{releasePreview();await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>bookings.length===1);bookings[0].reject();await until(()=>!!document.querySelector('.ticket-booking button:not([disabled])'));})()`);
  assert.equal(await evaluate('bookings[0].body.reason'),revert?'First reviewed reason':'Changed reviewed reason');
  await evaluate(`(async()=>{document.querySelector('.ticket-booking button').click();await until(()=>callbacks.length===1);})()`);
  assert.equal(await evaluate("calls.filter(c=>c.path==='tickets/amend').length"),1,'status recovery never rebooks');
 },['month','day'],true);
});
