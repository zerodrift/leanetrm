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

// Slow reference reads must not accumulate a request for every intermediate ticket shape.
test('ticket reference reads are bounded and queued choices follow only the latest sides',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const field=id=>({id,label:id,meaning:'',type:'choice',unit:null,state:'editable',required:false,band:null,choices:id,dependsOn:[],lookup:null,soon:null,rule:null});
   const side=(kind,initial)=>({kind,pill:kind,colour:'gas',min:0,initial,max:3,soon:null,fields:[field('source')],columns:[]});
   const d={product:'synthetic_queue',version:1,rowShape:'flow',tradeFields:[...['book','party','third','fourth','fifth','sixth'].map(field),...Array.from({length:6},(_,i)=>({...field('derived'+i),state:'info',choices:null,lookup:'derived'}))],sideLimits:[],levels:['month'],modes:['new'],tabs:[],tradeColumns:[],sides:[side('physical',1),side('cash',1),side('fee',0)]};
   window.calls=[];window.active=0;window.peak=0;window.held=[];
   window.tick=()=>new Promise(r=>setTimeout(r,30));
   const api=async(path,body)=>{
    if(path==='tickets/description')return d;
    if(path==='tickets/preview')throw Error('Incomplete draft');
    if(path==='tickets/choices'||path==='tickets/lookup'){
     active++;peak=Math.max(peak,active);calls.push({...body,path});
     return new Promise((resolve,reject)=>held.push({body,path,finish:(fail=false)=>{active--;fail||path==='tickets/lookup'?reject(Error('Reference unavailable')):resolve({choices:[{value:body.draft.sides.map(s=>s.code).join(','),label:'Current sides'}]});}}));
    }
    throw Error(path);
   };
   window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({}),product:d.product,instant:at=>at,actor:()=>'synthetic',booked:()=>{}});
   await tick();
   document.querySelector('[data-side="cash-1"] .ticket-remove').click();
   for(const kind of ['physical','fee'])[...document.querySelectorAll('.ticket-add-kind')].find(b=>b.textContent===kind).click();
   await new Promise(r=>setTimeout(r,350));
  })()`);
  assert.ok(await evaluate('peak<=4'),'at most four reference reads may be outstanding across edits');
  await evaluate('(async()=>{window.initial=calls.length;held.splice(0).forEach(h=>h.finish());await tick();})()');
  assert.equal(await evaluate("calls.slice(initial).every(c=>c.draft.sides.map(s=>s.code).join(',')==='physical-1,physical-2,fee-1')"),true,'obsolete queued drafts are never sent');
  await evaluate("(async()=>{for(let i=0;i<15&&held.length;i++){held.splice(0).forEach(h=>h.finish(h.body.field==='party'));await tick();}})()");
  assert.equal(await evaluate("document.querySelector('[data-field=book] select').options[1].value"),'physical-1,physical-2,fee-1');
  assert.equal(await evaluate("document.querySelector('[data-field=party] .ticket-error').textContent"),'Reference unavailable');
  assert.equal(await evaluate("calls.some(c=>c.path==='tickets/lookup')&&peak<=4"),true,'lookups share the bound and refusals release it');
  assert.equal(await evaluate("calls.slice(initial).some(c=>c.side==='cash-1')"),false,'removed sides are never read from the queue');
  // A new edit queues fresh lists. Destroying the ticket stops the queue even when outstanding reads complete.
  await evaluate(`(async()=>{document.querySelector('.ticket-add-kind').click();await tick();window.beforeDestroy=calls.length;ticket.destroy();held.splice(0).forEach(h=>h.finish());await tick();})()`);
  assert.equal(await evaluate('calls.length'),await evaluate('beforeDestroy'));
 });
});

test('returning to an earlier draft cannot apply a partially superseded lookup batch',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const field=(id,lookup=null)=>({id,label:id,meaning:'',type:'text',unit:null,state:lookup?'info':'editable',required:false,band:null,choices:null,dependsOn:[],lookup,soon:null,rule:null});
   const d={product:'synthetic_aba',version:1,rowShape:'flow',tradeFields:[field('input'),...Array.from({length:6},(_,i)=>field('looked'+i,'derived'))],sideLimits:[],levels:['month'],modes:['new'],tabs:[],tradeColumns:[],sides:[]};
   window.held=[];window.requests=[];window.delay=ms=>new Promise(r=>setTimeout(r,ms));
   const api=async(path,body)=>{
    if(path==='tickets/description')return d;
    if(path==='tickets/preview')throw Error('Incomplete draft');
    if(path==='tickets/lookup'){
     requests.push(body);
     if(body.draft.trade.input==='base')return {value:'stable',source:{kind:'reference',label:'Retained lookup'}};
     return new Promise(resolve=>held.push({body,finish:()=>resolve({value:'obsolete',source:{kind:'reference',label:'Old lookup'}})}));
    }
    throw Error(path);
   };
   window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({}),product:d.product,instant:at=>at,actor:()=>'synthetic',booked:()=>{}});
   await delay(30);
   window.edit=value=>{const input=document.querySelector('[data-field=input] input');input.value=value;input.onchange();};
   edit('base');await delay(700);
   if(document.querySelectorAll('[data-field^=looked] .ticket-chip').length!==6)throw Error('Initial lookup batch did not fill all fields');
   edit('A');await delay(350);
   edit('B');await delay(350);
   edit('A');held.splice(0).forEach(h=>h.finish());await delay(30);
  })()`);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-field^=looked] .ticket-chip')].map(e=>e.textContent)"),Array(6).fill('Retained lookup'));
  await evaluate('ticket.destroy();held.splice(0).forEach(h=>h.finish())');
 });
});

test('late booked choice labels preserve an editable day cell and its unsent quantity',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {setupTicket}=await import('/ticket-controller.js');
   const field={id:'book',label:'Book',meaning:'',type:'choice',unit:null,state:'editable',required:true,band:null,choices:'books',dependsOn:[],lookup:null,soon:null,rule:null};
   const d={product:'synthetic_booked',version:1,rowShape:'flow',tradeFields:[field,{...field,id:'party',label:'Party'}],sideLimits:[],levels:['month','day'],modes:['booked'],tabs:[],tradeColumns:[],
    sides:[{kind:'physical',pill:'Physical',colour:'gas',min:1,initial:1,max:1,soon:null,fields:[],columns:[{id:'quantity',label:'Quantity',unit:'quantity',measure:'quantity',input:['booked']}]}]};
   const cells={'physical-1.quantity':{value:'1000',sources:['contract']}};
   const row=(key,parent,level)=>({key,parent,level,start:'',end:'',operatingDate:null,cells,delivered:false});
   const trade={draft:{product:d.product,version:1,trade:{book:'book-1',party:'party-1'},sides:[{kind:'physical',code:'physical-1',values:{}}]},
    schedule:{levels:['month','day'],valuationAt:'2041-01-01T00:00:00Z',deliveredLeaves:0,leaves:1,delivered:null,remaining:null,totals:cells,rows:[row('2041-02',null,'month'),row('2041-02-01','2041-02','day')]},
    summaries:[],links:[],scheduleRefusal:null,quantityUnit:'MMBtu',quantityPolicy:'synthetic_quantity',actions:{amend:{allowed:false,reason:'No'},closeOut:{allowed:false,reason:'No'},record:{allowed:true,reason:'Record day'}}};
   window.held=[];
   const api=async(path,body)=>{
    if(path==='tickets/description')return d;
    if(path==='tickets/trade')return trade;
    if(path==='tickets/choices')return new Promise((resolve,reject)=>{if(body.field==='book')held.push(()=>resolve({choices:[{value:'book-1',label:'Resolved book'}]}));else window.refuseChoice=()=>reject(Error('Reference unavailable'));});
    throw Error(path);
   };
   window.until=async test=>{for(let i=0;i<250&&!test();i++)await new Promise(r=>setTimeout(r,20));if(!test())throw Error('Condition not ready');};
   window.ticket=setupTicket(document.querySelector('#host'),{api,context:()=>({}),product:d.product,instant:at=>at,opened:{tradeId:'synthetic',bindingId:'synthetic',revision:1}});
   await until(()=>held.length===1&&window.refuseChoice);
   const level=document.querySelector('.ticket-level');level.value='day';level.onchange();
   document.querySelector('.ticket-tab[data-tab="physical-1"]').click();
   await until(()=>!!document.querySelector('.ticket-cell-input'));
   window.cell=document.querySelector('.ticket-cell-input');cell.value='990';cell.focus();
   held.shift()();await until(()=>document.querySelector('[data-field=book] .ticket-value')?.textContent==='Resolved book');
  })()`);
  assert.deepEqual(await evaluate("({same:cell.isConnected&&cell===document.querySelector('.ticket-cell-input'),value:cell.value,focused:document.activeElement===cell})"),{same:true,value:'990',focused:true});
  await evaluate('(async()=>{refuseChoice();await new Promise(r=>setTimeout(r,50));})()');
  assert.deepEqual(await evaluate("({same:cell.isConnected&&cell===document.querySelector('.ticket-cell-input'),value:cell.value,focused:document.activeElement===cell,party:document.querySelector('[data-field=party] .ticket-value').textContent})"),{same:true,value:'990',focused:true,party:'party-1'});
  await evaluate("cell.dispatchEvent(new Event('change'))");
  assert.equal(await evaluate("document.querySelector('.ticket-record [data-field=quantity] .ticket-control').value"),'990');
  await evaluate('ticket.destroy()');
 });
});
