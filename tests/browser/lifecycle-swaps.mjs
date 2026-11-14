import {blotterAt} from './gas-ticket.mjs';
import {setupWorkspaceProbe} from './workspace-probe.mjs';

const q=JSON.stringify,root='#lifecycle-page',panel=root+' [data-lifecycle="panel"]';

// The setup books original synthetic terms and saves explicit product policies.
// Every financial action below goes through the actual shared browser form.
export async function swapLifecycleFixture(driver,name,{net=false,shared=false}={}){
 const {command,evaluate,wait,click,set,submit,persona,check,idle,url}=driver;
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('admin');
 await evaluate(`(async()=>{const b=await(await fetch('/api/bootstrap')).json();const r=await fetch('/api/admin/create',{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':b.csrfToken},body:JSON.stringify({operationId:crypto.randomUUID(),login:${q(name)},name:${q(name)},kind:'human',roles:['trader','risk_analyst']})});if(!r.ok)throw Error(await r.text());})()`);
 await persona(name);await setupWorkspaceProbe(evaluate);
 const dates=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return ['2020-12-18','2021-01-15'].map(day=>{const f=w.frames.find(f=>w.initialFrameIds.includes(f.id)&&f.at.startsWith(day));if(!f)throw Error('Missing explicit synthetic close '+day);return {id:f.id,session:f.close?.session??'demo_close_1600',at:f.at};});})()`);
 await blotterAt({set,wait,click},dates[0]);
 const selected=await evaluate(`(async()=>{
  const token=(await(await fetch('/api/bootstrap')).json()).csrfToken;
  window.__swapPost=async(path,body)=>{const r=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});if(!r.ok)throw Error(path+': '+await r.text());return r.json();};
  const context={snapshotId:${q(dates[0].id)},convention:'independent_side_financial_swap_v2',knowledgeMode:'current'},c=await window.__swapPost('swaps/catalog',context),index=c.indices.find(i=>i.curve==='base_forward');
  const entity=c.legalEntities[0],party=c.counterparties[0],payment={frequency:'monthly',rule:'days_after_period_end',days:18,dayOfMonth:null,dayBasis:'calendar_days',shortMonth:null,adjustment:'unadjusted',calendar:null,implementation:'monthly_payment_date_rules_v1'};
  if(!index||!entity||!party)throw Error('Missing explicit synthetic booking choices');
  const terms={currency:index.currencyCode,deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',paymentSchedule:payment};
  const floating={...terms,curve:index.curve,index:index.index,resetFrequency:'monthly',averagingMethod:'none',spread:'0',settlement:{mode:'curve_target',index:null,projectionPolicy:null}},
   side={basis:'period_total',quantity:index.quantityChoices[0].specification,amount:null,allocation:null,priceApplication:'period_average_times_quantity_v1',power:null,periods:[{amount:'100',fixed:null,floating}]};
  const booked=await window.__swapPost('swaps/book',{...context,operationId:crypto.randomUUID(),bookId:c.books[0].id,executedAt:null,quantity:${shared?'null':q('100')},fixedCoefficient:${shared?'null':q('-1')},discounting:false,definitionRevisionId:index.definitionRevisionId,commodity:index.commodity,
   fixed:${shared?'null':"{...terms,price:'3'}"},floating:${shared?'null':'floating'},${shared?"swapType:'scheduled_financial_swap',firstCoefficient:'-1',scheduled:{implementation:'whole_term_scheduled_financial_swap_v1',first:side,second:structuredClone(side)},":''}legalEntityId:entity.id,legalEntityRevision:entity.revision,counterpartyId:party.id,counterpartyRevision:party.revision});
  window.__swapFixture={tradeId:booked.tradeId,frame:${q(dates[1].id)},context:{...context,snapshotId:${q(dates[1].id)}}};return window.__swapFixture;
 })()`);
 await blotterAt({set,wait,click},dates[1]);
 await evaluate(`(async()=>{const f=window.__swapFixture,post=window.__swapPost;let d=await post('lifecycle/view',{...f.context,convention:'trading_workspace_v1'});
  const rows=d.swapOccurrences.filter(r=>r.trade_id===f.tradeId);if(rows.length!==2)throw Error('Expected two exact synthetic payment occurrences');
  f.occurrences=rows;f.profiles=[];f.currency={revision:{catalogId:rows[0].reference_catalog_id,revisionId:rows[0].reference_revision_id},code:rows[0].currency_code};
  f.policies={};for(const action of ['swap.determine','swap.payment',...${net?'["swap.net"]':'[]'}]){const policyId=crypto.randomUUID();f.policies[action]=policyId;
   await post('lifecycle/policies/save',{operationId:crypto.randomUUID(),policyId,expectedRevision:0,action,mode:'review',fromAt:'2020-01-01T00:00:00Z',untilAt:'2035-01-01T00:00:00Z',implementation:'explicit_lifecycle_action_policy_v1'});
   if(action==='swap.net')await post('lifecycle/swaps/net/configure',{operationId:crypto.randomUUID(),profileId:crypto.randomUUID(),tradeId:f.tradeId,expectedRevision:0,originRevision:1,action,policyId,policyRevision:1,referenceCatalogId:rows[0].reference_catalog_id,referenceRevisionId:rows[0].reference_revision_id,currency:rows[0].currency_code,decimalPlaces:2,roundingPolicy:'currency_minor_units_to_even_v1',duePolicy:'swap_due_nominal_v1',recognitionPolicy:'swap_completed_period_trading_pnl_v1',correctionPolicy:'swap_explicit_correction_due_v1',netPolicy:'explicit_same_due_swap_net_v1'});
   else for(const row of rows){const profileId=crypto.randomUUID();await post('lifecycle/swaps/configure',{operationId:crypto.randomUUID(),profileId,tradeId:f.tradeId,expectedRevision:0,originRevision:1,action,policyId,policyRevision:1,membershipOrdinal:row.membership_ordinal,paymentOrdinal:row.payment_ordinal,deliveryFrom:row.delivery_from,deliveryTo:row.delivery_to,referenceCatalogId:row.reference_catalog_id,referenceRevisionId:row.reference_revision_id,currency:row.currency_code,decimalPlaces:2,roundingPolicy:'currency_minor_units_to_even_v1',duePolicy:'swap_due_nominal_v1',recognitionPolicy:'swap_completed_period_trading_pnl_v1',correctionPolicy:'swap_explicit_correction_due_v1'});f.profiles.push({id:profileId,action,membership:row.membership_ordinal});}
  }
  const report=await post('trading/blotter',{context:{...f.context,convention:'trading_workspace_v1'},view:{mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[],pivotConvention:null}});f.reference=report.holdings.find(h=>h.tradeId===f.tradeId).tradeReference;
  window.__swapInspect=()=>post('swaps/settlement/inspect',{context:f.context,tradeId:f.tradeId,revision:null,duePolicy:'swap_due_nominal_v1',recognitionPolicy:'swap_completed_period_trading_pnl_v1'});
  window.__swapWrites=[];const send=window.fetch;window.fetch=async(input,init)=>{const path=String(input),body=init?.body?JSON.parse(init.body):null,response=await send(input,init);if(path==='/api/lifecycle/execute'){window.__swapWrites.push({body,status:response.status,outcome:response.ok?await response.clone().json():null});if(window.__loseSwapReply&&response.ok){window.__loseSwapReply=false;throw new TypeError('Synthetic reply lost after the committed lifecycle action');}}return response;};
 })()`);
 Object.assign(selected,await evaluate('window.__swapFixture'));
 async function legacy(){await click('#nav-futures');await wait(idle);await set('#blotter-filters .table-search',selected.reference);
  await wait(`[...document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]')].filter(r=>r.getClientRects().length&&r.dataset.rowKey.startsWith(${q(selected.tradeId+':')})).length===1`);
  await evaluate(`[...document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]')].find(r=>r.dataset.rowKey.startsWith(${q(selected.tradeId+':')})).click()`);await wait('!document.querySelector("#position-details").hidden&&'+idle);
  await click('#position-details-tabs-terms');await wait('!!document.querySelector("#side-panel-settlement")&&'+idle);await click('#side-panel-settlement');await wait('!document.querySelector("#swap-settlement").hidden&&'+idle);
 }
 async function lifecycle(){await click('#nav-lifecycle');await wait(`document.querySelector(${q(root)})?.hidden===false&&document.querySelector(${q(root+' [data-lifecycle="rows"] .table-search')})&&${idle}`,60000);
  await set(root+' [data-lifecycle="rows"] .table-search',selected.reference);await wait(idle);
 }
 async function action(code,membership=0){await lifecycle();const occurrence=code==='swap.net'?null:await evaluate(`window.__swapFixture.occurrences.find(o=>o.membership_ordinal===${membership}).occurrence_id`),profile=code==='swap.net'?null:await evaluate(`window.__swapFixture.profiles.find(p=>p.action===${q(code)}&&p.membership===${membership}).id`);
  const expression=`[...document.querySelectorAll(${q(root+' .tabulator-row[data-row-key]')})].find(r=>{const k=JSON.parse(r.dataset.rowKey);return k[1]===${q(selected.tradeId)}&&k[3]===${q(code)}&&(${q(occurrence)}===null||k[4]===${q(occurrence)}&&k[5]===${q(profile)});})`;
  await wait(`!!(${expression})&&${idle}`,60000);await evaluate(`(${expression}).click()`);await wait(`!!document.querySelector(${q(panel+' form.lifecycle-form')})&&${idle}`);
 }
 async function field(label,value){await evaluate(`(()=>{const labels=[...document.querySelectorAll(${q(panel+' label')})],wrapper=labels.find(l=>l.firstChild?.textContent===${q(label)}||l.firstChild?.textContent?.startsWith(${q(label)})),input=wrapper?.querySelector('input,select');if(!input)throw Error('Missing lifecycle field '+${q(label)});input.value=${q(value)};input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));})()`);}
 async function observedTime(){const local=await evaluate(`(()=>{const input=document.querySelector(${q(panel+' input[type=datetime-local]')}),zone=input.parentElement.querySelector('small').textContent,p=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:zone,hourCycle:'h23',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit'}).formatToParts(new Date(${q(dates[1].at)})).map(x=>[x.type,x.value]));return p.year+'-'+p.month+'-'+p.day+'T'+p.hour+':'+p.minute+':'+p.second;})()`);await field('Observed payment time',local);}
 async function review(){await submit(panel+' form.lifecycle-form',60000);await wait(`!!document.querySelector(${q(panel+' [data-lifecycle-review]')})&&${idle}`,60000);}
 async function record(){await click(panel+' [data-lifecycle-execute]',60000);await wait(idle,60000);}
 async function determine(membership){await action('swap.determine',membership);await review();await record();}
 async function inspect(){return evaluate('window.__swapInspect()');}
 async function lostReply(){await evaluate('window.__loseSwapReply=true');await record();await wait(`!document.querySelector(${q(root+' [data-lifecycle="pending"]')}).hidden&&${idle}`);
  const pending=await evaluate(`Object.entries(sessionStorage).find(([k])=>k.startsWith('lifecycle-request:'))`);if(!pending)throw Error('The uncertain command was not retained');
  const version=(await inspect()).settlement.version,origin=await evaluate('performance.timeOrigin');await command('Page.reload');await wait(`performance.timeOrigin!==${origin}&&${idle}`,60000);
  // A reload starts with an unselected workspace. Reselect the same actor and
  // read-only header; the retained command itself remains byte-for-byte intact.
  await persona(name);await blotterAt({set,wait,click},dates[1]);await lifecycle();await check('reload retains the exact uncertain lifecycle request before receipt-first retry',`sessionStorage.getItem(${q(pending[0])})===${q(pending[1])}&&!document.querySelector(${q(root+' [data-lifecycle="pending"]')}).hidden`);
  // The helper functions are intentionally page-local; restore only read access
  // after reload, never the retained financial body.
  await evaluate(`(async()=>{const token=(await(await fetch('/api/bootstrap')).json()).csrfToken;window.__swapPost=async(path,body)=>{const r=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});if(!r.ok)throw Error(await r.text());return r.json();};window.__swapFixture=${q(selected)};window.__swapInspect=()=>window.__swapPost('swaps/settlement/inspect',{context:window.__swapFixture.context,tradeId:window.__swapFixture.tradeId,revision:null,duePolicy:'swap_due_nominal_v1',recognitionPolicy:'swap_completed_period_trading_pnl_v1'});})()`);
  await click(root+' [data-lifecycle="retry"]',60000);await wait(`document.querySelector(${q(root+' [data-lifecycle="pending"]')}).hidden&&${idle}`,60000);
  await check('receipt-first retry preserves the accepted financial version and records no second cash movement',q((await inspect()).settlement.version===version));
 }
 return{...selected,openingFrame:dates[0].id,action,field,observedTime,review,record,determine,inspect,lifecycle,legacy,lostReply,root,panel};
}
