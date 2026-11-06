import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

async function fixture(work,inline=false){
 await withBrowserPage('<div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {mountSideScheduleInspection}=await import('/side-schedule-inspection.js');
   window.reads=[];window.held=[];window.hold=false;window.deny=false;window.denied=0;window.patchRow={};window.quantityEntries=[];window.windows=[];
   const identity={tradeId:'trade',bindingId:'binding',revision:2,contextId:'retained',snapshotId:'snapshot'};
   const row={key:'10:0',operation:'output_guard',kind:'Payment',deliveryFrom:'2041-01-01',deliveryTo:'2041-01-02',start:'2041-01-01T09:00:00Z',end:'2041-01-02T09:00:00Z',observationFrom:null,observationTo:null,valueAt:'2041-02-10T00:00:00Z',paymentAt:'2041-02-10T00:00:00Z',quantity:'-100',price:'3.02',amount:'-302',discountFactor:'1',presentValue:'-302',quantityState:'Contractual',priceStates:['Projected'],quantitySpecification:{code:'synthetic_mmbtu'},currency:{code:'synthetic_usd'},quoteMultiplier:'1',operatingDate:null,paid:null,outstanding:null,lifecycle:{delivery:'Scheduled',due:'NotDue',cash:'NotTracked',economicClass:null,coverage:'Discounted',unavailableReason:null,fulfillment:null}};
   const api=async(path,body)=>{
    reads.push({path,body});if(deny)throw Object.assign(new Error('Access revoked'),{code:deny===true?'access_denied':deny});
    if(body.sideSlot===null){if(${inline})await new Promise(r=>setTimeout(r,30));return {...identity,sides:[{slot:0,sideId:'delivery',family:'gas',manifest:'m0',windows:window.windows},{slot:1,sideId:'payment',family:'floating',manifest:'m1',windows:window.windows}]};}
    const selected={...identity,window:body.window,sideSlot:body.sideSlot,sideId:body.sideSlot===0?'delivery':'payment',manifest:body.sideSlot===0?'m0':'m1'};
    if(body.contribution)return {...selected,page:{side:'payment',manifest:selected.manifest,contribution:body.contribution,valueBasis:'unit_side_v1',tradeQuantityScale:'100',membershipNumerator:'-1',membershipDenominator:'1',offset:0,total:1,rows:[{key:'2:0',role:'source',operation:'price_lookup',kind:'price',value:'3',presence:'Resolved',deliveryFrom:row.deliveryFrom,deliveryTo:row.deliveryTo,start:row.start,end:row.end,observationFrom:row.deliveryFrom,observationTo:row.deliveryTo,valueAt:row.valueAt,unit:null,currency:null,quantityState:null,priceStates:['Projected'],inputs:[],bindings:[],fixingAt:'2041-01-01T12:00:00Z',contractFrom:row.deliveryFrom,contractTo:row.deliveryTo,lineage:[]}]}};
    const result={...selected,page:{side:body.sideSlot===0?'delivery':'payment',manifest:selected.manifest,valuationAt:'2040-12-18T16:00:00Z',knownAt:'2040-12-18T17:00:00Z',offset:body.offset,total:59,rows:[{...row,operatingDate:body.sideSlot===0?'2041-01-01':null,presentValue:body.sideSlot===0?'320':'-302',...patchRow}]}};
    if(hold&&body.sideSlot===0)return new Promise(resolve=>held.push(()=>resolve(result)));
    return result;
   };
   window.until=async f=>{for(let n=0;n<200&&!f();n++)await new Promise(r=>setTimeout(r,10));if(!f())throw Error('Inspector did not settle');};
   window.dispose=mountSideScheduleInspection(document.querySelector('#host'),{api,...${inline?`{side:{slot:0},quantityEntry:{days:new Map([['2041-01-01',{value:'120',sources:['nominated']}]]),disabled:false,enter:(...args)=>quantityEntries.push(args)}}`:'{}'},onDenied:()=>{denied++;},position:{context:{snapshotId:'snapshot',knowledgeMode:'current',convention:'trading_workspace_v1'},tradeId:'trade',bindingId:'binding',revision:2,instrumentType:'physical_gas'},instant:x=>x});
   window.openInspector=async()=>{document.querySelector('.side-schedule-inspection').open=true;await until(()=>document.querySelector('.inspection-side')?.options.length===3);};
   window.choose=n=>{const s=document.querySelector('.inspection-side');s.value=String(n);s.dispatchEvent(new Event('change'));};
  })()`);
  await work(evaluate);
 });
}

test('schedule inspection is lazy, pins its context and loads calculation evidence only on request',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  assert.equal(await evaluate('reads.length'),0);
  await evaluate("(async()=>{await openInspector();choose(1);await until(()=>document.querySelector('.inspection-rows')?.textContent.includes('-302'));})()");
  assert.equal(await evaluate("reads[1].body.position.context.knowledgeMode"),'retained');
  assert.equal(await evaluate("reads[1].body.position.context.contextId"),'retained');
  assert.match(await evaluate("document.querySelector('.inspection-rows').textContent"),/2041-02-10/);
  assert.match(await evaluate("document.querySelector('.inspection-rows').textContent"),/2041-01-01T09:00:00Z/);
  assert.match(await evaluate("document.querySelector('.inspection-rows').textContent"),/Not tracked/);
  assert.equal(await evaluate('reads.some(r=>r.body.contribution!==null)'),false);
  await evaluate("(async()=>{document.querySelector('.inspection-calculation').click();await until(()=>document.querySelector('.inspection-steps')?.textContent.includes('Price lookup'));})()");
  assert.match(await evaluate("document.querySelector('.inspection-evidence').textContent"),/Unit Side/);
  assert.match(await evaluate("document.querySelector('.inspection-evidence').textContent"),/100/);
  assert.match(await evaluate("document.querySelector('.inspection-steps').textContent"),/2041-01-01T09:00:00Z/);
 });
});

test('new product receives the common inspector without a product UI branch and selects opaque native windows',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   dispose();const {withSideSchedules,genericPositionView}=await import('/position-schedules.js');
   const api=async(path,body)=>{reads.push({path,body});const identity={tradeId:'new-trade',bindingId:'new-binding',revision:1,contextId:'retained',snapshotId:'snapshot'};
    if(body.sideSlot===null)return {...identity,sides:[{slot:9,sideId:'new-side',family:'synthetic',manifest:'new-manifest',windows:[{id:'native-a',label:'First period'},{id:'native-b',label:'Second period'}]}]};
    return {...identity,window:body.window,sideSlot:9,sideId:'new-side',manifest:'new-manifest',page:{manifest:'new-manifest',valuationAt:'2040-12-18T00:00:00Z',knownAt:'2040-12-19T00:00:00Z',offset:0,total:0,rows:[]}};
   };
   const holding={tradeId:'new-trade',bindingId:'new-binding',revision:'1',tradeReference:'Synthetic trade',instrumentReference:'Synthetic instrument',instrumentType:'new_product'};
   const view=withSideSchedules(genericPositionView(holding),api,{snapshotId:'snapshot',convention:'independent_side_financial_swap_v2'},holding,()=>{}).view('UTC');
   dispose=view.schedules(document.querySelector('#host'));
   document.querySelector('details').open=true;await until(()=>document.querySelector('.inspection-side').options.length===2);choose(9);
   await until(()=>document.querySelector('.inspection-window').options.length===3);
  })()`);
  assert.equal(await evaluate("reads.filter(r=>r.body.position.instrumentType==='new_product'&&r.body.sideSlot!==null).length"),0);
  await evaluate("(async()=>{const w=document.querySelector('.inspection-window');w.value='native-b';w.onchange();await until(()=>document.querySelector('.inspection-rows table'));})()");
  assert.equal(await evaluate('reads.at(-1).body.window'),'native-b');
  assert.equal(await evaluate('reads.at(-1).body.sideSlot'),9);
  assert.equal(await evaluate('reads.at(-1).body.position.context.convention'),'trading_workspace_v1');
 });
});

test('a late Side response cannot replace the newer selected Side and disposal cancels the view',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await openInspector();hold=true;choose(0);await until(()=>held.length===1);choose(1);await until(()=>document.querySelector('.inspection-rows')?.textContent.includes('-302'));held.shift()();await new Promise(r=>setTimeout(r,50));})()");
  assert.equal(await evaluate("document.querySelector('.inspection-rows').textContent.includes('320')"),false);
  await evaluate('dispose()');assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
 });
});


for(const refusal of ['access_denied','position_unavailable','position_facts','side_schedule_missing'])test(refusal+' clears previously authorized rows and active controls and notifies the parent',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await openInspector();choose(1);await until(()=>document.querySelector('.inspection-calculation'));deny='"+refusal+"';document.querySelector('.inspection-calculation').click();await until(()=>document.querySelector('[role=status]').textContent.includes('Access revoked'));})()");
  assert.equal(await evaluate('denied'),1);
  assert.equal(await evaluate("document.querySelector('.inspection-rows').children.length"),0);
  assert.equal(await evaluate("document.querySelector('.inspection-evidence').children.length"),0);
  assert.equal(await evaluate("document.querySelector('.inspection-controls').hidden"),true);
  assert.equal(await evaluate("document.querySelector('.inspection-side').options.length"),0);
  assert.equal(await evaluate("document.querySelector('.inspection-pager').hidden"),true);
 });
});


test('unavailable plan rows keep their dates and reason without a calculation or fabricated zero',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await openInspector();patchRow={quantity:null,price:null,amount:null,discountFactor:null,presentValue:null,lifecycle:{delivery:'InProgress',due:null,cash:null,economicClass:null,coverage:null,unavailableReason:'gas_delivery_lifecycle',fulfillment:'NotTracked'}};choose(0);await until(()=>document.querySelector('.inspection-calculation'));})()");
  assert.equal(await evaluate("document.querySelector('.inspection-calculation').disabled"),true);
  assert.match(await evaluate("document.querySelector('[role=status]').textContent"),/Valuation unavailable/);
  assert.match(await evaluate("document.querySelector('.inspection-rows').textContent"),/2041-01-01T09:00:00Z/);
  assert.equal(await evaluate("document.querySelector('.inspection-rows').textContent.includes('320')"),false);
 });
});

test('ordinary calculation read failure retains the authorized schedule and permits retry',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{await openInspector();choose(1);await until(()=>document.querySelector('.inspection-calculation'));deny='temporary_service_failure';document.querySelector('.inspection-calculation').click();await until(()=>document.querySelector('.inspection-evidence').textContent.includes('Access revoked'));})()");
  assert.equal(await evaluate('denied'),0);
  assert.match(await evaluate("document.querySelector('.inspection-rows').textContent"),/-302/);
  assert.equal(await evaluate("document.querySelector('.inspection-controls').hidden"),false);
 });
});


test('a selected ticket Side has one expanded grid, no duplicate selector, and retains quantity entry',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("until(()=>!!document.querySelector('.inspection-calculation'))");
  assert.equal(await evaluate("document.querySelector('.side-schedule-inspection').open"),true);
  assert.equal(await evaluate("document.querySelector('.side-schedule-inspection > summary').hidden"),true);
  assert.equal(await evaluate("document.querySelector('.inspection-controls').hidden"),true);
  assert.equal(await evaluate("document.querySelectorAll('.inspection-rows table').length"),1);
  assert.equal(await evaluate("reads.filter(r=>r.body.sideSlot===null).length"),1);
  assert.equal(await evaluate("reads.filter(r=>r.body.sideSlot!==null).every(r=>r.body.sideSlot===0)"),true);
  assert.equal(await evaluate("document.querySelector('.ticket-cell-input').placeholder"),'120');
  await evaluate("{const input=document.querySelector('.ticket-cell-input');input.value='125';input.onchange();}");
  assert.deepEqual(await evaluate('quantityEntries'),[['2041-01-01','125']]);
 },true);
});
