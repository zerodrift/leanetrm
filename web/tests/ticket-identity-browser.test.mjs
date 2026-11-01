import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

async function fixture(work){
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><div id="host" style="width:1100px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const field={id:'reference',label:'Reference',meaning:'',type:'text',unit:null,state:'editable',required:false,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null};
   const physical={kind:'physical',pill:'Physical',colour:'gas',min:1,initial:1,max:1,soon:null,fields:[],columns:[{id:'quantity',label:'Quantity',unit:'quantity',measure:'quantity',input:['booked']}]};
   const description={product:'synthetic_identity',version:1,rowShape:'flow',tradeFields:[field],sideLimits:[],levels:['month','day'],modes:['new','booked'],tabs:[],tradeColumns:[],sides:[physical]};
   const cells={'physical-1.quantity':{value:'1000',sources:['contract']}};
   const row=(key,parent,level)=>({key,parent,level,start:'',end:'',operatingDate:null,cells,delivered:false});
   const preview={schedule:{levels:['month','day'],valuationAt:'2041-01-01T00:00:00Z',deliveredLeaves:0,leaves:1,delivered:null,remaining:null,totals:cells,rows:[row('2041-02',null,'month'),row('2041-02-01','2041-02','day')]},summaries:[],links:[],scheduleRefusal:null,quantityUnit:'MMBtu'};
   const trade={...preview,draft:{product:description.product,version:1,trade:{reference:'Retained'},sides:[{kind:'physical',code:'physical-1',values:{}}]},quantityPolicy:'synthetic_quantity',actions:{amend:{allowed:false,reason:'No'},closeOut:{allowed:false,reason:'No'},record:{allowed:true,reason:'Record day'}}};
   window.actor='actor-a';window.callbacks=[];window.calls=[];window.bookings=[];window.release=null;window.done=false;
   const api=async(path,body)=>{
    calls.push({path,body});
    if(path==='tickets/description')return description;
    if(path==='tickets/trade')return trade;
    if(path==='tickets/preview')return preview;
    if(path==='tickets/book')return await new Promise((resolve,reject)=>{const receipt={operationId:body.operationId,tradeId:'trade-a',instrumentId:'instrument-a',revision:1};bookings.push({body,receipt,resolve:()=>resolve(receipt),reject:()=>reject(Error('Unknown response'))});window.release=()=>{resolve(receipt);setTimeout(()=>window.done=true,20);};});
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
   window.open=async(booked=false,readOnly=false)=>{
    window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({snapshotId:'frame-a'}),product:description.product,instant:at=>at,actor:()=>window.actor,readOnly,
     ...(booked?{opened:{tradeId:'trade-a',bindingId:'binding-a',revision:1}}:{booked:r=>callbacks.push(r)})});
    await until(()=>!!document.querySelector('.deal-ticket'));
   };
   window.day=async()=>{const level=document.querySelector('.ticket-level');level.value='day';level.onchange();document.querySelector('.ticket-tab[data-tab="physical-1"]').click();await until(()=>!!document.querySelector('.ticket-cell-input'));};
  })()`);
  await work(evaluate);
 });
}

test('a late gas booking receipt leaves its original request recoverable without changing the new actor or navigating',{skip:!browserAvailable,timeout:30000},async()=>{
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

for(const outcome of ['resolve','reject'])test(`an abandoned booking ${outcome} cannot replace the same actor's newer retained request`,{skip:!browserAvailable,timeout:30000},async()=>{
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

test('booked gas day-entry state belongs to its actor when the same trade is reopened',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await open(true);await day();const cell=document.querySelector('.ticket-cell-input');cell.value='990';cell.dispatchEvent(new Event('change'));await until(()=>!!document.querySelector('.ticket-record'));ticket.destroy();actor='actor-b';await open(true);await day();})()");
  assert.equal(await evaluate("document.querySelector('.ticket-record')===null"),true);
  await evaluate('(async()=>{ticket.destroy();actor="actor-a";await open(true);await day();})()');
  assert.equal(await evaluate("document.querySelector('.ticket-record [data-field=quantity] .ticket-control').value"),'990','the original actor keeps their unsubmitted entry');
 });
});

test('read-only gas tickets offer no writable day cells even when the account may record elsewhere',{skip:!browserAvailable,timeout:30000},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await open(true,true);const level=document.querySelector('.ticket-level');level.value='day';level.onchange();document.querySelector('.ticket-tab[data-tab=\"physical-1\"]').click();await new Promise(resolve=>setTimeout(resolve,100));})()");
  assert.equal(await evaluate("document.querySelectorAll('.ticket-cell-input').length"),0);
  assert.equal(await evaluate("document.querySelector('.ticket-record')===null"),true);
 });
});

test('a late day-record reply leaves the reopened ticket able to check its original outcome',{skip:!browserAvailable,timeout:30000},async()=>{
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
