import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

// Two Sides of `total` rows each: Side 0 has PV 320 and an operating day, Side 1 PV -302. The page's stylesheet has the rows scroll inside their box.
async function fixture(work,{inline=false,windows=[]}={}){
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {mountSideScheduleInspection}=await import('/side-schedule-inspection.js');
   window.reads=[];window.held=[];window.hold=false;window.holdCalculation=false;window.deny=false;window.denied=0;window.patchRow={};window.quantityEntries=[];window.windows=${JSON.stringify(windows)};window.total=59;
   const identity={tradeId:'trade',bindingId:'binding',revision:2,contextId:'retained',snapshotId:'snapshot'};
   const row={key:'10:0',operation:'output_guard',kind:'Payment',deliveryFrom:'2041-01-01',deliveryTo:'2041-01-02',start:'2041-01-01T09:00:00Z',end:'2041-01-02T09:00:00Z',observationFrom:null,observationTo:null,valueAt:'2041-02-10T00:00:00Z',paymentAt:'2041-02-10T00:00:00Z',quantity:'-100',price:'3.02',amount:'-302',discountFactor:'1',presentValue:'-302',quantityState:'Contractual',priceStates:['Projected'],quantitySpecification:{code:'synthetic_mmbtu'},currency:{code:'synthetic_usd'},quoteMultiplier:'1',operatingDate:null,paid:null,outstanding:null,lifecycle:{delivery:'Scheduled',due:'NotDue',cash:'NotTracked',economicClass:null,coverage:'Discounted',unavailableReason:null,fulfillment:null}};
   window.api=async(path,body)=>{
    reads.push({path,body});if(deny)throw Object.assign(new Error('Access revoked'),{code:deny===true?'access_denied':deny});
    if(body.sideSlot===null){if(${inline})await new Promise(r=>setTimeout(r,30));return {...identity,sides:[{slot:0,sideId:'delivery',family:'gas',manifest:'m0',windows:window.windows},{slot:1,sideId:'payment',family:'floating',manifest:'m1',windows:window.windows}]};}
    const selected={...identity,window:body.window,sideSlot:body.sideSlot,sideId:body.sideSlot===0?'delivery':'payment',manifest:body.sideSlot===0?'m0':'m1'};
    if(body.contribution&&holdCalculation)await new Promise(resolve=>held.push(resolve));
    if(body.contribution)return {...selected,page:{side:'payment',manifest:selected.manifest,contribution:body.contribution,valueBasis:'unit_side_v1',tradeQuantityScale:'100',membershipNumerator:'-1',membershipDenominator:'1',offset:body.offset,total:59,rows:[{key:'2:0',role:'source',operation:'price_lookup',kind:'price',value:'3',presence:'Resolved',deliveryFrom:row.deliveryFrom,deliveryTo:row.deliveryTo,start:row.start,end:row.end,observationFrom:row.deliveryFrom,observationTo:row.deliveryTo,valueAt:row.valueAt,unit:null,currency:null,quantityState:null,priceStates:['Projected'],inputs:[],bindings:[],fixingAt:'2041-01-01T12:00:00Z',contractFrom:row.deliveryFrom,contractTo:row.deliveryTo,lineage:[]}]}};
    const rows=Array.from({length:Math.max(0,Math.min(body.limit,total-body.offset))},(_,n)=>({...row,key:'10:'+(body.offset+n),operatingDate:body.sideSlot===0?'2041-01-01':null,presentValue:body.sideSlot===0?'320':'-302',...patchRow}));
    const result={...selected,page:{side:body.sideSlot===0?'delivery':'payment',manifest:selected.manifest,valuationAt:'2040-12-18T16:00:00Z',knownAt:'2040-12-18T17:00:00Z',offset:body.offset,total,rows}};
    if(hold&&body.sideSlot===0)return new Promise(resolve=>held.push(()=>resolve(result)));
    return result;
   };
   window.until=async f=>{for(let n=0;n<200&&!f();n++)await new Promise(r=>setTimeout(r,10));if(!f())throw Error('Inspector did not settle');};
   window.find=selector=>document.querySelector(selector);
   window.rowCount=()=>document.querySelectorAll('.inspection-rows tbody tr:not(.inspection-window-heading):not(.inspection-window-more)').length;
   window.tab=n=>document.querySelectorAll('.inspection-sides [role=tab]')[n];
   window.toEnd=()=>{const box=find('.inspection-rows');box.scrollTop=box.scrollHeight;};
   window.mount=()=>{window.dispose=mountSideScheduleInspection(find('#host'),{api,...${inline?`{side:{slot:0},quantityEntry:{days:new Map([['2041-01-01',{value:'120',sources:['nominated']}]]),disabled:false,enter:(...args)=>quantityEntries.push(args)}}`:'{}'},onDenied:()=>{denied++;},position:{context:{snapshotId:'snapshot',knowledgeMode:'current',convention:'trading_workspace_v1'},tradeId:'trade',bindingId:'binding',revision:2,instrumentType:'physical_gas'},instant:x=>x});};
  })()`);
  await work(evaluate);
 });
}
const shown=text=>`until(()=>find('.inspection-rows').textContent.includes('${text}'))`;

test('the first Side shows its rows at once under a tab per Side, with nothing to expand or choose',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{mount();await ${shown('320')};})()`);
  assert.equal(await evaluate("document.querySelectorAll('details,summary,select').length"),0);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.inspection-sides [role=tab]')].map(t=>[t.textContent,t.getAttribute('aria-selected')])"),[['Gas · Side 1','true'],['Floating · Side 2','false']]);
  assert.equal(await evaluate('reads[0].body.sideSlot'),null);
  assert.equal(await evaluate('reads[1].body.sideSlot'),0);
  assert.equal(await evaluate('reads[1].body.position.context.knowledgeMode'),'retained');
  assert.equal(await evaluate('reads[1].body.position.context.contextId'),'retained');
  assert.match(await evaluate("find('[role=status]').textContent"),/^32 of 59 rows · Valued 2040-12-18T16:00:00Z · Known 2040-12-18T17:00:00Z/);
 });
});

test('a Side tab shows that Side, and calculation evidence loads only on request',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{mount();await ${shown('320')};tab(1).click();await ${shown('-302')};})()`);
  assert.equal(await evaluate("tab(1).getAttribute('aria-selected')"),'true');
  assert.equal(await evaluate("find('.inspection-rows').textContent.includes('320')"),false);
  assert.match(await evaluate("find('.inspection-rows').textContent"),/2041-02-10/);
  assert.match(await evaluate("find('.inspection-rows').textContent"),/2041-01-01T09:00:00Z/);
  assert.match(await evaluate("find('.inspection-rows').textContent"),/Not tracked/);
  assert.equal(await evaluate('reads.some(r=>r.body.contribution!==null)'),false);
  await evaluate("(async()=>{find('.inspection-calculation').click();await until(()=>find('.inspection-steps')?.textContent.includes('Price lookup'));})()");
  assert.equal(await evaluate('reads.at(-1).body.sideSlot'),1);
  assert.match(await evaluate("find('.inspection-evidence').textContent"),/Unit Side/);
  assert.match(await evaluate("find('.inspection-evidence').textContent"),/100/);
  assert.match(await evaluate("find('.inspection-steps').textContent"),/2041-01-01T09:00:00Z/);
 });
});

test('a Side without delivery windows reads on as its rows are scrolled, keeping the rows already shown',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{mount();await ${shown('320')};await until(()=>rowCount()===32);})()`);
  await evaluate("new Promise(r=>setTimeout(r,150))");
  assert.equal(await evaluate('reads.length'),2,'rows below the box are not read until they are reached');
  await evaluate("{window.firstRow=find('.inspection-rows tbody tr');find('.inspection-rows').style.width='250px';find('.inspection-rows').scrollLeft=120;window.left=find('.inspection-rows').scrollLeft;toEnd();}");
  await evaluate('until(()=>rowCount()===59)');
  assert.deepEqual(await evaluate('[reads.at(-1).body.offset,reads.at(-1).body.sideSlot]'),[32,0]);
  assert.equal(await evaluate("firstRow.isConnected&&find('.inspection-rows').scrollLeft===left"),true);
  assert.match(await evaluate("find('[role=status]').textContent"),/^59 rows · /);
  assert.equal(await evaluate("find('.inspection-more').hidden"),true);
 });
});

test('every delivery window of a Side is listed in its one table under its own heading, in order',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{total=3;mount();await until(()=>{toEnd();return document.querySelectorAll('.inspection-window-heading').length===3&&rowCount()===9;});})()`);
  assert.equal(await evaluate("document.querySelectorAll('.inspection-rows table').length"),1);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.inspection-window-heading')].map(h=>[h.querySelector('span').firstChild.data,h.querySelector('small').textContent])"),[['Jan 2041','3 rows'],['Feb 2041','3 rows'],['Mar 2041','3 rows']]);
  assert.deepEqual(await evaluate('reads.slice(1).map(r=>[r.body.sideSlot,r.body.window,r.body.offset])'),[[0,'2041-01-01',0],[0,'2041-02-01',0],[0,'2041-03-01',0]]);
  assert.equal(await evaluate('rowCount()'),9);
  assert.match(await evaluate("find('[role=status]').textContent"),/^3 of 3 delivery windows · /);
  await evaluate("(async()=>{document.querySelectorAll('.inspection-calculation')[4].click();await until(()=>find('.inspection-steps'));})()");
  assert.equal(await evaluate('reads.at(-1).body.window'),'2041-02-01','a row is calculated in its own delivery window');
 },{windows:[{id:'2041-01-01',label:'Jan 2041'},{id:'2041-02-01',label:'Feb 2041'},{id:'2041-03-01',label:'Mar 2041'}]});
});

test('a long delivery window opens with its first rows and reads the rest when asked, in its place',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{mount();await until(()=>rowCount()===32);})()`);
  assert.deepEqual(await evaluate("[find('.inspection-window-heading span').firstChild.data,find('.inspection-window-heading small').textContent]"),['Jan 2041','32 of 59 rows']);
  assert.equal(await evaluate("(()=>{const box=find('.inspection-rows');box.scrollTop=200;const head=find('.inspection-rows thead th').getBoundingClientRect(),heading=find('.inspection-window-heading th').getBoundingClientRect();box.scrollTop=0;return heading.top>=head.bottom-1.5&&heading.top<=head.bottom+0.5;})()"),true,'a window heading stays in view just under the column titles while its rows scroll');
  await evaluate("(async()=>{toEnd();await until(()=>document.querySelectorAll('.inspection-window-heading').length===2);})()");
  assert.equal(await evaluate('rowCount()'),64,'the next window follows the first rows of the one before');
  await evaluate("(async()=>{find('.inspection-window-more button').click();await until(()=>rowCount()===91);})()");
  assert.deepEqual(await evaluate('[reads.at(-1).body.window,reads.at(-1).body.offset]'),['2041-01-01',32]);
  assert.equal(await evaluate("find('.inspection-window-heading small').textContent"),'59 rows');
  assert.equal(await evaluate("find('tbody').querySelectorAll('tr').length"),60,'the rest joins its own window');
  assert.equal(await evaluate("find('tbody').querySelector('.inspection-window-more')===null"),true);
 },{windows:[{id:'2041-01-01',label:'Jan 2041'},{id:'2041-02-01',label:'Feb 2041'}]});
});

test('new product receives the common inspector without a product UI branch and reads its opaque native windows',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   const {withSideSchedules,genericPositionView}=await import('/position-schedules.js');
   const api=async(path,body)=>{reads.push({path,body});const identity={tradeId:'new-trade',bindingId:'new-binding',revision:1,contextId:'retained',snapshotId:'snapshot'};
    if(body.sideSlot===null)return {...identity,sides:[{slot:9,sideId:'new-side',family:'synthetic',manifest:'new-manifest',windows:[{id:'native-a',label:'First period'},{id:'native-b',label:'Second period'}]}]};
    return {...identity,window:body.window,sideSlot:9,sideId:'new-side',manifest:'new-manifest',page:{manifest:'new-manifest',valuationAt:'2040-12-18T00:00:00Z',knownAt:'2040-12-19T00:00:00Z',offset:0,total:0,rows:[]}};
   };
   const holding={tradeId:'new-trade',bindingId:'new-binding',revision:'1',tradeReference:'Synthetic trade',instrumentReference:'Synthetic instrument',instrumentType:'new_product'};
   const view=withSideSchedules(genericPositionView(holding),api,{snapshotId:'snapshot',convention:'independent_side_financial_swap_v2'},holding,()=>{}).view('UTC');
   window.dispose=view.schedules(find('#host'));
   await until(()=>document.querySelectorAll('.inspection-window-heading').length===2);
  })()`);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.inspection-window-heading')].map(h=>[h.querySelector('span').firstChild.data,h.querySelector('small').textContent])"),[['First period','No rows'],['Second period','No rows']]);
  assert.deepEqual(await evaluate('reads.slice(1).map(r=>[r.body.sideSlot,r.body.window])'),[[9,'native-a'],[9,'native-b']]);
  assert.equal(await evaluate('reads.at(-1).body.position.context.convention'),'trading_workspace_v1');
  assert.equal(await evaluate("tab(0).textContent"),'Synthetic · Side 10');
 });
});

test('a position read again opens on the Side last shown for it',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{
   const {withSideSchedules,genericPositionView}=await import('/position-schedules.js');
   const holding={tradeId:'trade',bindingId:'binding',revision:'2',tradeReference:'Synthetic trade',instrumentReference:'Synthetic instrument',instrumentType:'physical_gas'};
   window.open=h=>{window.dispose=withSideSchedules(genericPositionView(h),api,{snapshotId:'snapshot'},h,()=>{}).view('UTC').schedules(find('#host'));};
   open(holding);await ${shown('320')};tab(1).click();await ${shown('-302')};dispose();
   open(holding);await until(()=>!!find('.inspection-calculation'));
  })()`);
  assert.equal(await evaluate("tab(1).getAttribute('aria-selected')"),'true');
  assert.equal(await evaluate("find('.inspection-rows').textContent.includes('-302')&&!find('.inspection-rows').textContent.includes('320')"),true);
  assert.equal(await evaluate('reads.at(-1).body.sideSlot'),1);
 });
});

test('the end of the rows is reached inside their box, wherever the box sits on the page',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("{const pane=document.createElement('div');pane.style.cssText='height:200px;overflow:auto';find('#host').replaceWith(pane);const host=document.createElement('div');host.id='host';pane.append(host);}");
  await evaluate(`(async()=>{mount();await until(()=>rowCount()===32);})()`);
  assert.equal(await evaluate("find('.inspection-more').getBoundingClientRect().top>find('#host').parentElement.getBoundingClientRect().bottom"),true,'the pane hides the foot of the box');
  await evaluate("(async()=>{toEnd();await until(()=>rowCount()===59);})()");
 });
});

test('a view taken down reads nothing more',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{mount();await until(()=>rowCount()===32);})()`);
  const before=await evaluate('reads.length');
  await evaluate("(async()=>{toEnd();dispose();await new Promise(r=>setTimeout(r,200));})()");
  assert.equal(await evaluate('reads.length'),before);
 });
});

test('a late Side response cannot replace the newer selected Side and disposal cancels the view',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{hold=true;mount();await until(()=>held.length===1);tab(1).click();await ${shown('-302')};held.shift()();await new Promise(r=>setTimeout(r,50));})()`);
  assert.equal(await evaluate("find('.inspection-rows').textContent.includes('320')"),false);
  await evaluate('dispose()');assert.equal(await evaluate("find('#host').children.length"),0);
 });
});

for(const refusal of ['access_denied','position_unavailable','position_facts','side_schedule_missing'])test(refusal+' clears previously authorized rows and active controls and notifies the parent',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{mount();await until(()=>find('.inspection-calculation'));deny='${refusal}';find('.inspection-calculation').click();await until(()=>find('[role=status]').textContent.includes('Access revoked'));})()`);
  assert.equal(await evaluate('denied'),1);
  assert.equal(await evaluate("document.querySelectorAll('.inspection-rows tbody').length"),0);
  assert.equal(await evaluate("find('.inspection-evidence').children.length"),0);
  assert.equal(await evaluate("find('.inspection-sides').hidden"),true);
  assert.equal(await evaluate("document.querySelectorAll('.inspection-sides [role=tab]').length"),0);
  assert.equal(await evaluate("find('.inspection-more').hidden"),true);
 });
});

test('unavailable plan rows keep their dates and reason without a calculation or fabricated zero',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{patchRow={quantity:null,price:null,amount:null,discountFactor:null,presentValue:null,lifecycle:{delivery:'InProgress',due:null,cash:null,economicClass:null,coverage:null,unavailableReason:'gas_delivery_lifecycle',fulfillment:'NotTracked'}};mount();await until(()=>find('.inspection-calculation'));})()");
  assert.equal(await evaluate("find('.inspection-calculation').disabled"),true);
  assert.match(await evaluate("find('[role=status]').textContent"),/Valuation unavailable/);
  assert.match(await evaluate("find('.inspection-rows').textContent"),/2041-01-01T09:00:00Z/);
  assert.equal(await evaluate("find('.inspection-rows').textContent.includes('320')"),false);
 });
});

test('ordinary calculation read failure retains the authorized schedule and permits retry',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{mount();await until(()=>find('.inspection-calculation'));deny='temporary_service_failure';find('.inspection-calculation').click();await until(()=>find('.inspection-evidence').textContent.includes('Access revoked'));})()`);
  assert.equal(await evaluate('denied'),0);
  assert.match(await evaluate("find('.inspection-rows').textContent"),/320/);
  assert.equal(await evaluate("find('.inspection-sides').hidden"),false);
  await evaluate("(async()=>{deny=false;find('.inspection-calculation').click();await until(()=>find('.inspection-steps'));})()");
 });
});

test('a failed read of further rows keeps the rows shown, says why and reads again when asked',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate(`(async()=>{mount();await until(()=>rowCount()===32);deny='temporary_service_failure';toEnd();await until(()=>find('[role=status]').textContent.includes('Access revoked'));})()`);
  assert.equal(await evaluate('[denied,rowCount()].join()'),'0,32');
  const failed=await evaluate('reads.length');
  await evaluate("new Promise(r=>setTimeout(r,150))");
  assert.equal(await evaluate('reads.length'),failed,'a failed read is not repeated unasked');
  await evaluate("(async()=>{deny=false;find('.inspection-more').click();await until(()=>rowCount()===59);})()");
  assert.match(await evaluate("find('[role=status]').textContent"),/^59 rows · /);
 });
});

test('a selected ticket Side has one grid, no Side tabs, and retains quantity entry',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{mount();await until(()=>!!find('.inspection-calculation'));})()");
  assert.equal(await evaluate("find('.inspection-sides').hidden"),true);
  assert.equal(await evaluate("document.querySelectorAll('.inspection-sides [role=tab]').length"),0);
  assert.equal(await evaluate("document.querySelectorAll('.inspection-rows table').length"),1);
  assert.equal(await evaluate("reads.filter(r=>r.body.sideSlot===null).length"),1);
  assert.equal(await evaluate("reads.filter(r=>r.body.sideSlot!==null).every(r=>r.body.sideSlot===0)"),true);
  assert.equal(await evaluate("find('.ticket-cell-input').placeholder"),'120');
  await evaluate("{const input=find('.ticket-cell-input');input.value='125';input.onchange();}");
  assert.deepEqual(await evaluate('quantityEntries'),[['2041-01-01','125']]);
 },{inline:true});
});

test('calculation paging retains its table and horizontal place until the replacement is ready',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await fixture(async evaluate=>{
  await evaluate("(async()=>{mount();await until(()=>find('.inspection-calculation'));find('.inspection-calculation').click();await until(()=>find('.inspection-steps'));const style=document.createElement('style');style.textContent='.inspection-steps{width:240px;overflow:auto}';document.head.append(style);const steps=find('.inspection-steps');steps.scrollLeft=120;window.oldSteps=steps;holdCalculation=true;[...document.querySelectorAll('.inspection-evidence button')].find(b=>b.textContent==='Next inputs').click();await until(()=>held.length===1);})()");
  assert.equal(await evaluate("oldSteps.isConnected&&find('.inspection-evidence').inert&&find('.inspection-evidence').getAttribute('aria-busy')==='true'"),true);
  await evaluate("(async()=>{held.shift()();await until(()=>find('.inspection-steps')!==oldSteps);})()");
  assert.equal(await evaluate("find('.inspection-steps').scrollLeft"),120);
  assert.equal(await evaluate("find('.inspection-evidence').inert"),false);
 });
});
