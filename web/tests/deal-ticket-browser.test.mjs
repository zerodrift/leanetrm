import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

// The ticket draws a synthetic in-delivery schedule view in the real grid dependency: the backend's delivered, remaining and total rows,
// pinned in that order, and the strip's delivered count.
test('a ticket in delivery shows the delivered count and the delivered, remaining and total rows above the pinned total',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:1200px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {renderTicket}=await import('/deal-ticket.js'),{newTicket}=await import('/ticket-model.js');
   const col=(id,label,unit,measure)=>({id,label,unit,measure,input:[]});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month','day'],modes:['new','booked'],tabs:[],
    sides:[{kind:'physical',pill:'Physical',colour:'gas',min:1,initial:1,max:1,fields:[],columns:[col('quantity','Quantity','quantity','quantity')]}],tradeColumns:[col('net_cash','Net cash',null,'money')]};
   const cells=(q,net)=>({'physical-1.quantity':{value:q,sources:['contract']},'trade.net_cash':{value:net,sources:[]}});
   const row=(key,parent,level,c,delivered)=>({key,parent,level,start:'',end:'',operatingDate:null,cells:c,delivered});
   const schedule={levels:['month','day'],valuationAt:'2026-11-02T00:00:00+00:00',deliveredLeaves:1,leaves:2,
    totals:cells('-2000','6500'),delivered:cells('-1000','3250'),remaining:cells('-1000','3250'),
    rows:[row('2026-11',null,'month',cells('-2000','6500'),false),row('2026-11-01','2026-11','day',cells('-1000','3250'),true),row('2026-11-02','2026-11','day',cells('-1000','3250'),false)]};
   const on=new Proxy({},{get:()=>()=>{}});
   window.ticket=renderTicket(document.querySelector('#host'),{state:newTicket(d),mode:'booked',actions:{amend:{allowed:false,reason:'No amendment.'},closeOut:{allowed:false,reason:'No close-out.'},record:{allowed:true,reason:'Recorded.'}},
    schedule,quantityUnit:'MMBtu',instant:at=>'at '+at,previous:null,summaries:[],links:[],entry:null,status:{kind:'current',text:'Up to date'},fieldErrors:{},choices:{},selectedSide:null,tab:'instrument',level:'day',closed:new Set(),on});
   for(let i=0;i<250&&!document.querySelector('.tabulator-calcs-holder .tabulator-cell');i++)await new Promise(r=>setTimeout(r,20));
   await new Promise(r=>setTimeout(r,100));
  })()`);
  assert.equal(await evaluate("document.querySelector('.ticket-progress-text').textContent"),'1 of 2 delivered');
  assert.equal(await evaluate("document.querySelector('.ticket-valuation').textContent"),'Valued at at 2026-11-02T00:00:00+00:00');
  assert.equal(await evaluate("document.querySelector('.ticket-state').textContent"),'Booked · in delivery');
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-calcs-holder .tabulator-cell')].map(c=>[...c.querySelectorAll('.grid-total-line')].map(l=>l.textContent))"),
   [['Delivered','Remaining','Total'],['-1,000','-1,000','-2,000'],['3,250','3,250','6,500']]);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-tableholder .delivered-row')].map(r=>r.dataset.rowKey)"),['2026-11-01']);
  await evaluate('ticket.destroy()');
  assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
 });
});

// The ticket keeps the rows the person opened or closed across a redraw, and draws a side added since the last preview only once a
// preview holds it: until then the instrument grid shows the sides the schedule has and the new side's tab says it is not previewed.
test('a redraw keeps the person\'s open and closed rows and leaves out a side the schedule does not hold yet',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:1200px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {renderTicket}=await import('/deal-ticket.js'),{newTicket,addSide}=await import('/ticket-model.js');
   const col=(id,label,unit,measure)=>({id,label,unit,measure,input:[]});
   const kind=(k,colour,min)=>({kind:k,pill:k,colour,min,initial:min,max:2,fields:[],columns:[col('quantity','Quantity','quantity','quantity')]});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month','day'],modes:['new','booked'],tabs:[],sides:[kind('physical','gas',1),kind('fee','fee',0)],tradeColumns:[]};
   const cells=q=>({'physical-1.quantity':{value:q,sources:[]}});
   const row=(key,parent,level,q)=>({key,parent,level,start:'',end:'',operatingDate:null,cells:cells(q),delivered:false});
   const schedule={levels:['month','day'],valuationAt:'2026-10-30T00:00:00Z',deliveredLeaves:0,leaves:3,delivered:null,remaining:null,totals:cells('3'),
    rows:[row('2026-11',null,'month','2'),row('2026-11-01','2026-11','day','1'),row('2026-11-02','2026-11','day','1'),row('2026-12',null,'month','1'),row('2026-12-01','2026-12','day','1')]};
   window.toggles=[];
   const on=new Proxy({},{get:(_,name)=>name==='toggle'?(key,open)=>toggles.push([key,open]):()=>{}});
   const view=(tab)=>({state:addSide(newTicket(d),'fee'),mode:'new',actions:null,schedule,quantityUnit:'MMBtu',instant:at=>at,previous:null,summaries:[],links:[],entry:null,status:{kind:'stale',text:'Preview refused'},fieldErrors:{},choices:{},
    selectedSide:null,tab,level:'day',closed:new Set(['2026-11']),on});
   window.draw=tab=>{window.ticket?.destroy();window.ticket=renderTicket(document.querySelector('#host'),view(tab));};
   window.draw('instrument');
   for(let i=0;i<250&&!document.querySelector('.tabulator-row[data-row-key]');i++)await new Promise(r=>setTimeout(r,20));
   await new Promise(r=>setTimeout(r,100));
  })()`);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-row[data-row-key]')].map(r=>r.dataset.rowKey)"),['2026-11','2026-12','2026-12-01']);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.grid-group-title')].map(t=>t.textContent)"),['physical · physical-1']);
  await evaluate("document.querySelector('.tabulator-row[data-row-key=\"2026-12\"] .tabulator-data-tree-control').click()");
  assert.deepEqual(await evaluate('toggles'),[['2026-12',false]]);
  await evaluate("draw('fee-1')");
  assert.equal(await evaluate("document.querySelector('.ticket-grid').textContent"),'fee · fee-1 is not in the last preview yet.');
 });
});

// In booked mode a side's tab makes the day cells of a column the description enters in that mode inputs; a change opens that day's
// record. The Quantity links tab draws each link's receipt, delivery and fuel from the backend's exact text.
test('a booked side tab enters day quantities and the links tab draws the backend links',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:1200px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {renderTicket}=await import('/deal-ticket.js'),{newTicket}=await import('/ticket-model.js');
   const col=(id,label,unit,measure,input)=>({id,label,unit,measure,input});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month','day'],modes:['new','booked'],tabs:[{id:'links',label:'Quantity links'}],
    sides:[{kind:'physical',pill:'Physical',colour:'gas',min:1,initial:1,max:1,fields:[],columns:[col('quantity','Quantity','quantity','quantity',['booked']),col('amount','Amount',null,'money',[])]}],tradeColumns:[]};
   const cells=q=>({'physical-1.quantity':{value:q,sources:['contract']},'physical-1.amount':{value:'5',sources:['contract']}});
   const row=(key,parent,level,q)=>({key,parent,level,start:'',end:'',operatingDate:null,cells:cells(q),delivered:false});
   const schedule={levels:['month','day'],valuationAt:'2026-10-30T00:00:00Z',deliveredLeaves:0,leaves:2,delivered:null,remaining:null,totals:cells('-2000'),
    rows:[row('2026-11',null,'month','-2000'),row('2026-11-01','2026-11','day','-1000'),row('2026-11-02','2026-11','day','-1000')]};
   window.entered=[];
   const on=new Proxy({},{get:(_,name)=>name==='enter'?(...args)=>entered.push(args):()=>{}});
   const field=(id,type,choices)=>({id,label:id,meaning:'',type,unit:id==='quantity'?'quantity':null,state:'editable',required:true,band:null,choices,dependsOn:[],lookup:null,soon:null,rule:null});
   const links=[{source:'physical-1',target:'physical-2',formula:'gross_divided_by_one_plus_fraction',fraction:'0.02',receipt:'310000',delivery:'15500000/51',fuel:'310000/51'}];
   window.draw=(tab,mode,entry,record=true)=>{window.ticket?.destroy();window.ticket=renderTicket(document.querySelector('#host'),{state:newTicket(d),mode,actions:{amend:{allowed:false,reason:'No.'},closeOut:{allowed:false,reason:'No.'},record:{allowed:record,reason:'No.'}},
    schedule,quantityUnit:'MMBtu',instant:at=>at,previous:null,summaries:[],links,entry,status:{kind:'current',text:'Up to date'},fieldErrors:{},choices:{},selectedSide:null,tab,level:'day',closed:new Set(),on});};
   window.settled=async()=>{for(let i=0;i<250&&!document.querySelector('.tabulator-row[data-row-key]');i++)await new Promise(r=>setTimeout(r,20));await new Promise(r=>setTimeout(r,100));};
   window.entry={side:'physical-1',day:'2026-11-02',fields:[field('quantity','decimal',null),field('kind','choice','kinds')],values:{quantity:'900'},choices:{kind:[{value:'nominated',label:'Nominated'}]},pending:false,sending:false,text:''};
   draw('physical-1','booked',null);await settled();
  })()`);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-row[data-row-key]')].map(r=>[r.dataset.rowKey,r.querySelectorAll('.ticket-cell-input').length])"),[['2026-11',0],['2026-11-01',1],['2026-11-02',1]]);
  // An entry opens empty, showing the drawn quantity, grouped and signed, only as its placeholder: the record takes a plain unsigned decimal.
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.ticket-cell-input')].map(i=>[i.value,i.placeholder])"),[['','-1,000'],['','-1,000']]);
  await evaluate("(()=>{const input=document.querySelector('.tabulator-row[data-row-key=\"2026-11-02\"] .ticket-cell-input');input.value='900';input.dispatchEvent(new Event('change'));})()");
  assert.deepEqual(await evaluate('entered'),[['physical-1','2026-11-02','900']]);
  await evaluate("(async()=>{draw('physical-1','booked',entry);await settled();})()");
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.ticket-record [data-field] .ticket-control')].map(c=>c.value)"),['900','']);
  assert.equal(await evaluate("document.querySelector('.ticket-record .ticket-unit').textContent"),'MMBtu');
  await evaluate("(async()=>{draw('instrument','booked',null);await settled();})()");
  assert.equal(await evaluate("document.querySelectorAll('.ticket-cell-input').length"),0);
  await evaluate("(async()=>{draw('physical-1','new',null);await settled();})()");
  assert.equal(await evaluate("document.querySelectorAll('.ticket-cell-input').length"),0);
  // A booked trade that records no day quantities takes no entries.
  await evaluate("(async()=>{draw('physical-1','booked',null,false);await settled();})()");
  assert.equal(await evaluate("document.querySelectorAll('.ticket-cell-input').length"),0);
  await evaluate("(async()=>{draw('links','booked',null);await settled();})()");
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-row[data-row-key] .tabulator-cell')].map(c=>c.textContent)"),
   ['physical-1','physical-2','gross_divided_by_one_plus_fraction','0.02','310,000','15,500,000/51','310,000/51']);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-col-title')].map(c=>c.textContent).slice(4)"),['Receipt (MMBtu)','Delivery (MMBtu)','Fuel (MMBtu)']);
 });
});

// A late answer that changes nothing on screen leaves the drawn ticket standing, so the person keeps their place in its schedule.
test('a lookup answer that changes nothing leaves the drawn schedule standing',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:1200px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const f=(id,over)=>({id,label:id,meaning:'',type:'decimal',unit:null,state:'editable',required:true,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null,...over});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month'],modes:['new'],tabs:[],tradeColumns:[],
    sides:[{kind:'leg',pill:'Leg',colour:'gas',min:1,initial:1,max:1,soon:null,fields:[f('q',{band:'quantity'}),f('price',{state:'info',type:'text',required:false,lookup:'market_price'})],
     columns:[{id:'quantity',label:'Quantity',unit:null,measure:'quantity',input:[],soon:null}]}]};
   const cells=q=>({'leg-1.quantity':{value:q,sources:[]}});
   window.held=[];
   const api=async(path,body)=>{
    if(path==='tickets/description')return d;
    if(path==='tickets/choices')return {choices:[]};
    if(path==='tickets/lookup')return await new Promise(answer=>held.push(()=>answer({value:'3.25',source:{kind:'market',label:'Hub · close 2041-01-02'}})));
    if(path==='tickets/preview'){const q=body.draft.sides[0].values.q;return {schedule:{levels:['month'],valuationAt:'',deliveredLeaves:0,leaves:1,delivered:null,remaining:null,totals:cells(q),
     rows:[{key:'2041-02',parent:null,level:'month',start:'',end:'',operatingDate:null,cells:cells(q),delivered:false}]},summaries:[],scheduleRefusal:null,quantityUnit:'MMBtu',links:[]};}
    throw Error('unexpected '+path);
   };
   setupTicket(document.querySelector('#host'),{api,context:()=>({}),product:'synthetic_flow',instant:at=>at,actor:()=>'synthetic',booked:()=>{}});
   const until=async test=>{for(let i=0;i<250&&!test();i++)await new Promise(r=>setTimeout(r,20));if(!test())throw Error('timed out');};
   const edit=async value=>{await until(()=>!!document.querySelector('[data-field=q] input'));const input=document.querySelector('[data-field=q] input');input.value=value;input.onchange();};
   const drawn=q=>document.querySelector('.ticket-status')?.dataset.kind==='current'&&document.querySelector('.tabulator-row[data-row-key="2041-02"]')?.textContent.includes(q);
   await edit('5');await until(()=>drawn('5')&&held.length===1);held.shift()();
   await until(()=>!!document.querySelector('[data-field=price] .ticket-chip'));
   await edit('6');await until(()=>drawn('6')&&held.length===1);
   window.holder=document.querySelector('.ticket-grid .tabulator-tableholder');
   held.shift()();await new Promise(r=>setTimeout(r,200));
  })()`);
  assert.equal(await evaluate("holder.isConnected&&holder===document.querySelector('.ticket-grid .tabulator-tableholder')"),true);
 });
});

// Lookup answers dropped because the draft changed leave that draft unread, so returning to it reads its lookups again.
test('a draft whose lookup answers were dropped reads them again when the person returns to it',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:1200px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const f=(id,over)=>({id,label:id,meaning:'',type:'decimal',unit:null,state:'editable',required:true,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null,...over});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month'],modes:['new'],tabs:[],tradeColumns:[],
    sides:[{kind:'leg',pill:'Leg',colour:'gas',min:1,initial:1,max:1,soon:null,fields:[f('q',{band:'quantity'}),f('price',{state:'info',type:'text',required:false,lookup:'market_price'})],
     columns:[{id:'quantity',label:'Quantity',unit:null,measure:'quantity',input:[],soon:null}]}]};
   window.held=[];
   const api=async(path,body)=>{
    if(path==='tickets/description')return d;
    if(path==='tickets/choices')return {choices:[]};
    if(path==='tickets/lookup')return await new Promise(answer=>held.push(()=>answer({value:'3.'+body.draft.sides[0].values.q,source:{kind:'market',label:'Hub · close 2041-01-02'}})));
    if(path==='tickets/preview')return {schedule:null,summaries:null,scheduleRefusal:{code:'schedule_view',message:'Not drawn here.'},quantityUnit:'MMBtu',links:[]};
    throw Error('unexpected '+path);
   };
   setupTicket(document.querySelector('#host'),{api,context:()=>({}),product:'synthetic_flow',instant:at=>at,actor:()=>'synthetic',booked:()=>{}});
   window.until=async test=>{for(let i=0;i<250&&!test();i++)await new Promise(r=>setTimeout(r,20));if(!test())throw Error('timed out');};
   const edit=async value=>{await until(()=>!!document.querySelector('[data-field=q] input'));const input=document.querySelector('[data-field=q] input');input.value=value;input.onchange();};
   await edit('5');await until(()=>held.length===1);
   // The draft changes, so the answers for 5 are dropped, and changes back before the next preview reads anything.
   await edit('6');held.shift()();await new Promise(r=>setTimeout(r,20));await edit('5');
   await until(()=>held.length===1);held.shift()();
   await until(()=>!!document.querySelector('[data-field=price] .ticket-chip'));
  })()`);
  assert.equal(await evaluate("document.querySelector('[data-field=price] .ticket-value').textContent"),'3.5');
 });
});

// A slow field read must not send every other field into the server's session
// lock, and edits/closing must release obsolete reads.
test('ticket field reads are bounded and superseded or closed reads are cancelled',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const field=(id,choices=null)=>({id,label:id,meaning:'',type:choices?'choice':'text',unit:null,state:'editable',required:false,band:null,choices,dependsOn:[],lookup:null,soon:null,rule:null});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[field('reference'),field('book','books'),field('party','parties'),field('venue','venues')],
    sideLimits:[],levels:['month'],modes:['new'],tabs:[],tradeColumns:[],sides:[]};
   window.calls=[];window.active=0;window.maximum=0;
   const api=async(path,body,signal)=>{
    if(path==='tickets/description')return d;
    if(path==='tickets/choices')return new Promise((_,reject)=>{
     calls.push({body,signal});active++;maximum=Math.max(maximum,active);
     const abort=()=>{active--;reject(Error('response_unknown'));};
     signal?.addEventListener('abort',abort,{once:true});
    });
    throw Error('Unexpected request '+path);
   };
   window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({snapshotId:'synthetic'}),product:'synthetic_flow',instant:at=>at,actor:()=>'bounded-reader',booked:()=>{}});
   window.until=async test=>{for(let i=0;i<250&&!test();i++)await new Promise(r=>setTimeout(r,10));if(!test())throw Error('timed out');};
   await until(()=>calls.length>0);
  })()`);
  assert.equal(await evaluate('maximum'),1);
  await evaluate(`(async()=>{
   const input=document.querySelector('[data-field=reference] input');input.value='latest';input.onchange();
   await until(()=>calls.length===2);
  })()`);
  assert.equal(await evaluate('calls[0].signal.aborted'),true);
  assert.equal(await evaluate('calls[1].body.draft.trade.reference'),'latest');
  assert.equal(await evaluate('maximum'),1);
  await evaluate("ticket.destroy()");
  assert.equal(await evaluate('calls[1].signal.aborted'),true);
  await evaluate('new Promise(r=>setTimeout(r,30))');
  assert.equal(await evaluate('calls.length'),2);
  assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
 });
});

// Cancellation is an optimization as well as a lifetime boundary: a transport
// that still delivers an old answer must not win after A -> B -> A edits.
test('cancelled lookup and preview answers cannot return through the same draft',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const f=(id,over={})=>({id,label:id,meaning:'',type:'text',unit:null,state:'editable',required:false,band:null,choices:null,dependsOn:[],lookup:null,soon:null,rule:null,...over});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[f('reference'),f('price',{state:'info',lookup:'price'})],
    sideLimits:[],levels:['month'],modes:['new'],tabs:[],tradeColumns:[],sides:[]};
   window.lookups=[];window.previews=[];
   const api=async(path,body,signal)=>{
    if(path==='tickets/description')return d;
    if(path==='tickets/lookup')return new Promise(resolve=>lookups.push({resolve,signal}));
    if(path==='tickets/preview')return new Promise(resolve=>previews.push({resolve,signal}));
    throw Error('Unexpected '+path);
   };
   window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({}),product:'synthetic_flow',instant:at=>at,actor:()=>'late-reader',booked:()=>{}});
   window.until=async test=>{for(let i=0;i<250&&!test();i++)await new Promise(r=>setTimeout(r,10));if(!test())throw Error('timed out');};
   window.edit=value=>{const input=document.querySelector('[data-field=reference] input');input.value=value;input.onchange();};
   await until(()=>!!document.querySelector('[data-field=reference] input'));
   edit('A');await until(()=>lookups.length===1&&previews.length===1);
   edit('B');edit('A');await until(()=>previews.length===2);
  })()`);
  assert.equal(await evaluate('lookups[0].signal.aborted&&previews[0].signal.aborted'),true);
  await evaluate(`(async()=>{
   lookups[0].resolve({value:'obsolete',source:{kind:'market',label:'Old answer'}});
   previews[0].resolve({schedule:null,summaries:[],scheduleRefusal:{code:'old',message:'Old preview'},quantityUnit:'MMBtu',links:[]});
   await until(()=>lookups.length===2);
  })()`);
  assert.equal(await evaluate("!!document.querySelector('[data-field=price] .ticket-chip')"),false);
  assert.equal(await evaluate("document.querySelector('.ticket-status').textContent.includes('Old preview')"),false);
  await evaluate(`(async()=>{
   lookups[1].resolve({value:'current',source:{kind:'market',label:'Current answer'}});
   await until(()=>!!document.querySelector('[data-field=price] .ticket-chip'));
  })()`);
  assert.equal(await evaluate("document.querySelector('[data-field=price] .ticket-value').textContent"),'current');
  await evaluate('ticket.destroy()');
  assert.equal(await evaluate('previews[1].signal.aborted'),true);
 });
});
