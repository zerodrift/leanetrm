import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const browser={skip:!browserAvailable,timeout:browserTestTimeout};
const setup=`(async()=>{
 window.calls=[];window.pendingDescriptions=[];window.pendingTimes=[];window.books=[];window.saved=[];window.holdDescriptions=false;window.holdTimes=false;window.extraCutoff=false;window.loseBookReply=false;window.wrongTradeContext=false;window.badDescription='';
 window.actor='synthetic-actor';window.context={snapshotId:'synthetic-snapshot',knowledgeMode:'current',convention:'synthetic-ticket-context'};
 const product='synthetic-profile-ticket',field=(id,type='decimal',extra={})=>({id,label:id,meaning:'Explicit synthetic term.',type,unit:null,state:'editable',required:true,band:null,choices:type==='choice'?id:null,dependsOn:[],lookup:null,soon:null,rule:null,...extra});
 const common=[field('profile','choice',{refreshDescription:true}),field('counterparty','text')];
 const shell={product,version:1,rowShape:'flow',sides:[],sideLimits:[],tradeColumns:[],levels:['term'],tabs:[],modes:['new','booked']};
 const describe=draft=>({...shell,selection:draft?.trade.profile?{profile:draft.trade.profile}:{},tradeFields:[...common,...draft?.trade.profile==='fixed_profile'?[field('fixed_fee_amount'),field('fee_due_at','datetime',{allowNotApplicable:true})]:draft?.trade.profile==='balance_profile'?[field('fee_rate_amount'),field('fee_due_at','datetime'),...extraCutoff?[field('fee_accrual_through_at','datetime')]:[]]:[]]});
 const preview={source:{definitionRevisionId:'synthetic-definition',referenceCatalogId:'synthetic-catalog',referenceRevisionId:'synthetic-reference'},schedule:{levels:['term'],valuationAt:'2041-01-01T12:00:00Z',rows:[],totals:{},delivered:null,remaining:null,deliveredLeaves:0,leaves:0},summaries:[],links:[],scheduleRefusal:null,quantityUnit:'synthetic quantity'};
 window.savedDraft={product,version:1,trade:{profile:'balance_profile',counterparty:'Synthetic retained party',fee_rate_amount:'0.1234567890123456789',fee_due_at:'2041-01-02T12:00:00Z'},sides:[]};
 const api=async(path,body,signal)=>{
  calls.push({path,body:structuredClone(body)});
  if(path==='tickets/description'){const answer=describe(badDescription==='wrong_profile'&&body.draft?{...body.draft,trade:{...body.draft.trade,profile:'fixed_profile'}}:body.draft);if(badDescription==='missing')delete answer.selection;if(badDescription==='extra')answer.selection.irrelevant='synthetic-value';if(badDescription==='wrong_product')answer.product='another-synthetic-product';if(holdDescriptions&&body.draft)return new Promise(resolve=>pendingDescriptions.push({body,resolve:()=>resolve(answer)}));return answer;}
  if(path==='tickets/choices')return{choices:[{value:'fixed_profile',label:'Fixed profile'},{value:'balance_profile',label:'Balance profile'}]};
  if(path==='tickets/preview'){
   const value=body.draft.trade,missing=describe(body.draft).tradeFields.find(field=>!value[field.id]);
   if(missing)throw Object.assign(Error('Explicit '+missing.id+' required'),{details:{field:missing.id}});
   if(value.profile==='balance_profile'&&value.fee_due_at==='none')throw Object.assign(Error('Explicit fee due time required'),{details:{field:'fee_due_at'}});
   return preview;
  }
  if(path==='tickets/book'){books.push(body);if(loseBookReply)throw Error('Synthetic acknowledgement lost');return{operationId:body.operationId,tradeId:'synthetic-trade',instrumentId:'synthetic-binding',revision:1};}
  if(path==='tickets/status')return{operationId:books[0].operationId,tradeId:'synthetic-trade',instrumentId:'synthetic-binding',revision:1};
  if(path==='tickets/trade')return{...preview,draft:savedDraft,snapshotId:context.snapshotId,context:{contextId:wrongTradeContext?'another-synthetic-cut':'synthetic-cut',effectiveOn:'2041-01-01',knownAt:'2041-01-01T12:00:00Z'},sideSlots:{},actions:{amend:{allowed:false,reason:'Read only'},closeOut:{allowed:false,reason:'Read only'},record:{allowed:false,reason:'Read only'}},quantityPolicy:null,facilityAllocations:[]};
  if(path==='time/inspect'){if(holdTimes)return new Promise(resolve=>pendingTimes.push({body,resolve:()=>resolve({candidates:[{instant:'2041-01-02T12:00:00Z'}]})}));return{candidates:[{instant:'2041-01-02T12:00:00Z'}]};}
  throw Error('Unexpected '+path);
 };
 window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Profile condition not ready: '+JSON.stringify(calls.map(call=>({path:call.path,profile:call.body?.draft?.trade?.profile}))));};
 window.setTerm=(id,value)=>{const control=document.querySelector('[data-field="'+id+'"] .ticket-control');if(!control)throw Error('Missing '+id);control.value=value;control.dispatchEvent(new Event('change'));};
 window.begin=async opened=>{window.ticket?.destroy();ticket=(await import('/ticket-controller.js')).setupTicket(document.querySelector('#host'),{api,product,context:()=>context,actor:()=>actor,instant:at=>at,zone:()=> 'UTC',...opened?{opened,readOnly:true,presentation:'terms'}:{booked:r=>saved.push(r.tradeId)}});await until(()=>!!document.querySelector('[data-field=profile]')||!!document.querySelector('[role=alert]'));};
 await begin();await until(()=>document.querySelector('[data-field=profile] select').options.length===3);
})()`;

test('backend-selected descriptions show only operative fields and retain explicit common entries',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 assert.equal(await evaluate("document.querySelector('[data-field=fixed_fee_amount],[data-field=fee_rate_amount]')===null"),true);
 await evaluate("(async()=>{setTerm('counterparty','Synthetic chosen party');setTerm('profile','fixed_profile');await until(()=>!!document.querySelector('[data-field=fixed_fee_amount]'));setTerm('fixed_fee_amount','12.34567890123456789');setTerm('profile','balance_profile');await until(()=>!!document.querySelector('[data-field=fee_rate_amount]'));})()");
 assert.equal(await evaluate("document.querySelector('[data-field=fixed_fee_amount]')===null"),true);
 assert.equal(await evaluate("document.querySelector('[data-field=counterparty] input').value"),'Synthetic chosen party');
 assert.equal(await evaluate("document.querySelector('[data-field=fee_rate_amount] input').value"),'');
 await evaluate("(async()=>{setTerm('fee_rate_amount','0.1234567890123456789');setTerm('fee_due_at','2041-01-02T12:00');await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>books.length===1);})()");
 assert.deepEqual(await evaluate('books[0].draft.trade'),{profile:'balance_profile',counterparty:'Synthetic chosen party',fee_rate_amount:'0.1234567890123456789',fee_due_at:'2041-01-02T12:00:00Z'});
}));

test('an old profile description cannot replace a later explicit selection',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{holdDescriptions=true;setTerm('profile','fixed_profile');await until(()=>pendingDescriptions.length===1);setTerm('profile','balance_profile');await until(()=>pendingDescriptions.length===2);pendingDescriptions[1].resolve();await until(()=>!!document.querySelector('[data-field=fee_rate_amount]'));pendingDescriptions[0].resolve();})()");
 assert.equal(await evaluate("document.querySelector('[data-field=profile] select').value"),'balance_profile');
 assert.equal(await evaluate("document.querySelector('[data-field=fixed_fee_amount]')===null"),true);
 assert.equal(await evaluate("!!document.querySelector('[data-field=fee_rate_amount]')"),true);
}));

for(const invalid of ['wrong_profile','missing','extra'])test('a description with '+invalid+' selector attestation withholds its economic fields',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate(`(async()=>{badDescription=${JSON.stringify(invalid)};setTerm('profile','balance_profile');await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='refused');})()`);
 assert.equal(await evaluate("document.querySelector('[data-field=fixed_fee_amount],[data-field=fee_rate_amount]')===null"),true);
 assert.equal(await evaluate("document.querySelector('.ticket-book').disabled"),true);
 assert.match(await evaluate("document.querySelector('.ticket-status').textContent"),/selection/i);
}));

test('an initial refreshing description without selector attestation is refused before entry appears',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("badDescription='missing';begin()");
 assert.equal(await evaluate("document.querySelector('[data-field=profile]')===null"),true);
 assert.match(await evaluate("document.querySelector('[role=alert]').textContent"),/selection/i);
}));

test('an initial description cannot replace the explicitly requested product',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("badDescription='wrong_product';begin()");
 assert.equal(await evaluate("document.querySelector('[data-field=profile]')===null"),true);
 assert.match(await evaluate("document.querySelector('[role=alert]').textContent"),/product/i);
}));

for(const changed of ['context','actor'])test('a pending profile description is fenced when '+changed+' changes',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{holdDescriptions=true;setTerm('profile','balance_profile');await until(()=>pendingDescriptions.length===1);"+(changed==='context'?"context={...context,snapshotId:'another-synthetic-snapshot'};":"actor='another-synthetic-actor';")+"pendingDescriptions[0].resolve();})()");
 assert.equal(await evaluate("document.querySelector('[data-field=fee_rate_amount]')===null"),true);
 assert.equal(await evaluate("document.querySelector('.ticket-book').disabled"),true);
}));

test('a reopened trade requests the description selected by its saved profile',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{await begin({tradeId:'synthetic-trade',bindingId:'synthetic-binding',revision:1});await until(()=>!!document.querySelector('[data-field=fee_rate_amount] .ticket-value'));})()");
 assert.equal(await evaluate("calls.filter(call=>call.path==='tickets/description').at(-1).body.draft.trade.profile"),'balance_profile');
 assert.equal(await evaluate("document.querySelector('[data-field=fee_rate_amount] .ticket-value').textContent"),'0.1234567890123456789');
 assert.equal(await evaluate("document.querySelector('[data-field=fixed_fee_amount]')===null"),true);
}));

test('a mismatched retained trade is rejected before using its profile to read another description',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{context={...context,knowledgeMode:'retained',contextId:'synthetic-cut'};wrongTradeContext=true;await begin({tradeId:'synthetic-trade',bindingId:'synthetic-binding',revision:1});await until(()=>!!document.querySelector('[role=alert]'));})()");
 assert.equal(await evaluate("calls.filter(call=>call.path==='tickets/description'&&call.body.draft).length"),0);
 assert.equal(await evaluate("document.querySelector('[data-field=fee_rate_amount]')===null"),true);
 assert.match(await evaluate("document.querySelector('[role=alert]').textContent"),/certified snapshot and context/);
}));

test('a retained explicit no-date value cannot become a valid datetime under another profile',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{setTerm('counterparty','Synthetic chosen party');setTerm('profile','fixed_profile');await until(()=>!!document.querySelector('[data-field=fixed_fee_amount]'));setTerm('fixed_fee_amount','0');document.querySelector('[data-field=fee_due_at] [data-role=not-applicable]').click();setTerm('profile','balance_profile');await until(()=>!!document.querySelector('[data-field=fee_rate_amount]'));setTerm('fee_rate_amount','0');await until(()=>document.querySelector('[data-field=fee_due_at] [role=alert]'));})()");
 assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] [data-role=not-applicable]')===null"),true);
 assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] input').value"),'');
 assert.equal(await evaluate("document.querySelector('.ticket-book').disabled"),true);
}));

test('an unresolved clock belonging to an inactive profile cannot prevent booking the chosen profile',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{extraCutoff=true;setTerm('counterparty','Synthetic chosen party');setTerm('profile','balance_profile');await until(()=>!!document.querySelector('[data-field=fee_accrual_through_at]'));holdTimes=true;setTerm('fee_accrual_through_at','2041-01-02T13:00');await until(()=>pendingTimes.length===1);setTerm('profile','fixed_profile');await until(()=>!!document.querySelector('[data-field=fixed_fee_amount]'));setTerm('fixed_fee_amount','0');document.querySelector('[data-field=fee_due_at] [data-role=not-applicable]').click();await until(()=>document.querySelector('.ticket-status')?.dataset.kind==='current');document.querySelector('.ticket-book').click();await until(()=>books.length===1);pendingTimes[0].resolve();})()");
 assert.deepEqual(await evaluate('books[0].draft.trade'),{profile:'fixed_profile',counterparty:'Synthetic chosen party',fixed_fee_amount:'0',fee_due_at:'none'});
}));

test('a local clock resolving after a profile change requires explicit re-entry under the current terms',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{setTerm('counterparty','Synthetic chosen party');setTerm('profile','fixed_profile');await until(()=>!!document.querySelector('[data-field=fee_due_at]'));holdTimes=true;setTerm('fee_due_at','2041-01-02T12:00');await until(()=>pendingTimes.length===1);setTerm('profile','balance_profile');await until(()=>!!document.querySelector('[data-field=fee_rate_amount]'));setTerm('fee_rate_amount','0');pendingTimes[0].resolve();await new Promise(resolve=>setTimeout(resolve,30));})()");
 assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] input').value"),'');
 assert.equal(await evaluate("document.querySelector('.ticket-book').disabled"),true);
 await evaluate("(async()=>{holdTimes=false;setTerm('fee_due_at','2041-01-02T12:00');await until(()=>!document.querySelector('.ticket-book').disabled);})()");
 assert.equal(await evaluate("document.querySelector('[data-field=fee_due_at] [role=alert]')===null"),true);
}));

test('retained booking recovery loads its saved profile without changing the original request',browser,()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(setup);
 await evaluate("(async()=>{setTerm('counterparty','Synthetic chosen party');setTerm('profile','balance_profile');await until(()=>!!document.querySelector('[data-field=fee_rate_amount]'));setTerm('fee_rate_amount','0.1234567890123456789');setTerm('fee_due_at','2041-01-02T12:00');await until(()=>!document.querySelector('.ticket-book').disabled);loseBookReply=true;document.querySelector('.ticket-book').click();await until(()=>ticket.pending());await begin();await until(()=>!!document.querySelector('[data-field=fee_rate_amount]'));})()");
 assert.equal(await evaluate("document.querySelector('[data-field=fee_rate_amount] input').value"),'0.1234567890123456789');
 assert.equal(await evaluate("document.querySelector('[data-field=fixed_fee_amount]')===null"),true);
 await evaluate("(async()=>{[...document.querySelectorAll('.ticket-booking button')].find(button=>button.textContent==='Check booking status').click();await until(()=>saved.length===1);})()");
 assert.equal(await evaluate('books.length'),1);
 assert.equal(await evaluate("calls.filter(call=>call.path==='tickets/description').at(-1).body.draft.trade.profile"),'balance_profile');
}));
