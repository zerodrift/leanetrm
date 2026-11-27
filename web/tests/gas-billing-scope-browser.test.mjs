import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const choices={canEnroll:true,books:[{id:'book',name:'Synthetic delivery book'}],policies:[{id:'policy',revision:2,mode:'review',from_at:'2026-01-01T00:00:00Z',until_at:'2027-01-01T00:00:00Z'},{id:'second-policy',revision:2,mode:'review',from_at:'2026-01-01T00:00:00Z',until_at:'2026-12-01T00:00:00Z'}],discoveryPolicy:'gas_billing_scope_reassessment_v1',processingConvention:'gas_processing_instant_utc_v1',knowledgePolicy:'current_knowledge_at_execution_v1',scheduledPolicy:'manual_scheduled_quantity_v1',profiles:['gas_final_daily_entry_cash_v1'],selectionPolicies:['final_allocated_billing_v1'],roundingPolicy:'currency_minor_units_to_even_v1',maximumPage:8,maximumTimeoutSeconds:30,maximumAttempts:3};
test('gas scope requires explicit source choices and a fresh preview before scheduling review work',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<section id="host"></section>',async evaluate=>{
 await evaluate(`(async()=>{const {setupGasBillingScope}=await import('/gas-billing-scope.js');window.calls=[];window.sent=[];window.field=k=>document.querySelector('[data-gas-scope="'+k+'"]');window.view=setupGasBillingScope(document.querySelector('#host'),{api:async(path,body)=>{calls.push({path,body});if(path==='time/inspect')return{candidates:[{instant:body.localTime+'Z',offset:'+00:00'}]};if(path==='gas/billing/enrollment-choices')return ${JSON.stringify(choices)};if(path==='gas/billing/enrollment-preview')return {context:{contextId:'context'},hasMore:false,next:null,items:[{selection:{tradeId:'trade',cashSlot:1,day:'2021-01-01'},state:'review_required',amount:'-240',currency:'usd',reconciliation:{contractual:'100',scheduled:null,final:'80'}}]};throw Error(path);},act:work=>window.waiting=work(),busy:()=>false,pending:()=>false,identity:()=>'actor',zone:()=>'UTC',mutate:async(path,body)=>{sent.push({path,body});}});})()`);
 assert.equal(await evaluate("field('once').disabled"),true);
 await evaluate("field('business-date').value='2026-11-25';field('due').value='2026-11-25T12:00:00';field('choices').click();waiting");
 assert.equal(await evaluate("field('policy').value"),'');
 assert.match(await evaluate("field('policy').options[2].textContent"),/second-policy.*2026-12-01/);
 await evaluate(`for(const key of ['policy','final','profile','rounding','discovery','knowledge','scheduled'])field(key).selectedIndex=1;field('books').options[0].selected=true;for(const [key,value]of Object.entries({from:'2021-01-01',to:'2021-01-03',name:'Synthetic reviewed scope',expires:'2026-11-25T16:00:00',delay:'0','page-size':'2',dispatch:'1',timeout:'30',attempts:'2'}))field(key).value=value;field('preview').click();waiting`);
 assert.equal(await evaluate("field('once').disabled"),false);
 assert.match(await evaluate("document.querySelector('#host').textContent"),/review required/);
 await evaluate("field('once').click();waiting");
 const sent=await evaluate('sent');assert.equal(sent.length,1);assert.equal(sent[0].path,'gas/billing/enroll');assert.equal(sent[0].body.source.mode,'review');assert.equal(sent[0].body.source.policyRevision,2);assert.equal(sent[0].body.source.readinessDelaySeconds,0);assert.equal(sent[0].body.schedule.intervalSeconds,null);assert.deepEqual(sent[0].body.source.bookIds,['book']);
 await evaluate("field('preview').click();waiting");
 await evaluate("field('from').value='2021-01-02';field('from').dispatchEvent(new Event('input',{bubbles:true}))");
 assert.equal(await evaluate("field('once').disabled"),true);assert.equal((await evaluate('sent')).length,1);
}));

test('Jobs invalidates a reviewed gas scope on timezone and context changes',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(
 readFileSync(new URL('../src/index.html',import.meta.url),'utf8').replace(/<script\b[^>]*>[\s\S]*?<\/script>/g,''),async evaluate=>{
 await evaluate(`(async()=>{const {setupJobs}=await import('/jobs-view.js');window.zone='UTC';window.field=k=>document.querySelector('[data-gas-scope="'+k+'"]');window.view=setupJobs({api:async(path,body)=>{if(path==='time/inspect')return{candidates:[{instant:body.localTime+'Z',offset:'+00:00'}]};if(path==='gas/billing/enrollment-choices')return ${JSON.stringify(choices)};if(path==='gas/billing/enrollment-preview')return{context:{contextId:'context'},hasMore:false,next:null,items:[]};throw Error(path);},act:work=>window.waiting=work(),busy:()=>false,identity:()=>'actor',zone:()=>zone,instant:v=>v,contextReady:()=>true});})()`);
 await evaluate("field('business-date').value='2026-11-25';field('due').value='2026-11-25T12:00:00';field('choices').click();waiting");
 await evaluate(`for(const key of ['policy','final','profile','rounding','discovery','knowledge','scheduled'])field(key).selectedIndex=1;field('books').options[0].selected=true;for(const [key,value]of Object.entries({from:'2021-01-01',to:'2021-01-03',name:'Synthetic reviewed scope',expires:'2026-11-25T16:00:00',delay:'0','page-size':'2',dispatch:'1',timeout:'30',attempts:'2'}))field(key).value=value;field('preview').click();waiting`);
 assert.equal(await evaluate("field('once').disabled"),false);
 await evaluate("zone='America/Chicago';view.refresh()");
 assert.equal(await evaluate("field('once').disabled"),true);
 assert.equal(await evaluate("field('due').value"),'2026-11-25T06:00');
 await evaluate('view.contextChanged()');
 assert.equal(await evaluate("field('policy').value"),'');
 assert.equal(await evaluate("field('preview').disabled"),true);
}));
