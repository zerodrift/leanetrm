import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

// The ticket draws a synthetic in-delivery schedule view in the real grid dependency: the backend's delivered, remaining and total rows,
// pinned in that order, and the strip's delivered count.
test('a ticket in delivery shows the delivered count and the delivered, remaining and total rows above the pinned total',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:1200px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {renderTicket}=await import('/deal-ticket.js'),{newTicket}=await import('/ticket-model.js');
   const col=(id,label,unit,measure)=>({id,label,unit,measure,input:false});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month','day'],modes:['new','booked'],tabs:[],
    sides:[{kind:'physical',pill:'Physical',colour:'gas',min:1,max:1,fields:[],columns:[col('quantity','Quantity','MMBtu','quantity')]}],tradeColumns:[col('net_cash','Net cash',null,'money')]};
   const cells=(q,net)=>({'physical-1.quantity':{value:q,sources:['contract']},'trade.net_cash':{value:net,sources:[]}});
   const row=(key,parent,level,c,delivered)=>({key,parent,level,start:'',end:'',operatingDate:null,cells:c,delivered});
   const schedule={levels:['month','day'],valuationAt:'2026-11-02T00:00:00+00:00',deliveredLeaves:1,leaves:2,
    totals:cells('-2000','6500'),delivered:cells('-1000','3250'),remaining:cells('-1000','3250'),
    rows:[row('2026-11',null,'month',cells('-2000','6500'),false),row('2026-11-01','2026-11','day',cells('-1000','3250'),true),row('2026-11-02','2026-11','day',cells('-1000','3250'),false)]};
   const on=new Proxy({},{get:()=>()=>{}});
   window.ticket=renderTicket(document.querySelector('#host'),{state:newTicket(d),mode:'booked',actions:{amend:{allowed:false,reason:'No amendment.'},closeOut:{allowed:false,reason:'No close-out.'}},
    schedule,previous:null,summaries:[],status:{kind:'current',text:'Up to date'},fieldErrors:{},choices:{},selectedSide:null,tab:'instrument',level:'day',on});
   for(let i=0;i<250&&!document.querySelector('.tabulator-calcs-holder .tabulator-cell');i++)await new Promise(r=>setTimeout(r,20));
   await new Promise(r=>setTimeout(r,100));
  })()`);
  assert.equal(await evaluate("document.querySelector('.ticket-progress-text').textContent"),'1 of 2 delivered');
  assert.equal(await evaluate("document.querySelector('.ticket-state').textContent"),'Booked · in delivery');
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-calcs-holder .tabulator-cell')].map(c=>[...c.querySelectorAll('.grid-total-line')].map(l=>l.textContent))"),
   [['Delivered','Remaining','Total'],['-1,000','-1,000','-2,000'],['3,250','3,250','6,500']]);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-tableholder .delivered-row')].map(r=>r.dataset.rowKey)"),['2026-11-01']);
  await evaluate('ticket.destroy()');
  assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
 });
});
