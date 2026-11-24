import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const markup=(await readFile(new URL('../src/index.html',import.meta.url),'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g,'');
test('Risk scope and retained history read independently and reset cancels both',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  window.pending=[];window.failures=[];
  window.retainedKey='openetrm.risk.workspace.submission.synthetic-reader';
  window.retained=JSON.stringify({version:1,queued:true,parts:[{request:{},job:null,failure:null,command:{path:'portfolio/risk/jobs',operationId:'00000000-0000-4000-8000-000000000001',body:{request:{}},uncertain:true}}]});
  sessionStorage.setItem(retainedKey,retained);
  window.risk=(await import('/risk-workspace.js')).setupRiskWorkspace({
   api:(path,body,signal)=>new Promise((resolve,reject)=>pending.push({path,body,signal,resolve,reject})),
   act:async work=>{try{await work();}catch(error){failures.push(error.message);}},busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-reader',canRun:()=>true,
   instant:value=>value,source:()=>({snapshotId:'synthetic-snapshot'}),convention:()=>null,frames:()=>[],changed(){},view(){}});
  window.loading=risk.load().catch(error=>failures.push(error.message));await new Promise(resolve=>setTimeout(resolve,25));
 })()`);
 assert.deepEqual(await evaluate('pending.map(item=>item.path).sort()'),['portfolio/risk/jobs','trading/directory'],
  'a slow retained-report list must not delay the selected scope directory');
 await evaluate('risk.sync()');
 assert.equal(await evaluate('sessionStorage.getItem(retainedKey)===retained'),true,'rendering local loading must preserve an unconfirmed command before recovery adopts it');
 await evaluate("risk.reset();for(const item of pending)item.reject(Object.assign(Error('Old read denied'),{code:'access_denied'}));loading");
 assert.equal(await evaluate('pending.every(item=>item.signal?.aborted)'),true,'reset must cancel both independent transports');
 assert.deepEqual(await evaluate('failures'),[],'late refusals must not replace the reset account state');
 assert.equal(await evaluate("document.querySelector('#risk-workspace-note').textContent"),'');
}));

test('reload recovery opens an already-completed original job as a report',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  const context={contextId:'synthetic-context',knownAt:'2026-01-01T00:00:00Z',knownThrough:1,effectiveOn:'2026-01-01'},request={context:{...context,snapshotId:'synthetic-snapshot'},bookIds:[],portfolioIds:[],tradeIds:null,bumps:[],stress:[]};
  const operationId='00000000-0000-4000-8000-000000000001',id='00000000-0000-4000-8000-000000000002';
  const report={context,snapshotId:'synthetic-snapshot',asOf:'2026-01-01T00:00:00Z',scenario:{id:'base',depth:0,isBase:true},factors:[],excludedPositions:[],projectionPolicies:[],
   heldInstruments:0,work:{positionCount:0,uniqueValuations:0,scenarioCount:1,preparationScenarioCount:0,unitPricerCalls:0,rebuiltCurveNodes:0,reusedCurveNodes:0,repeatedInstrumentReferences:0,quantityApplications:0},connectedScenarios:[],graph:{},preparation:{}};
  const job={id,requestId:operationId,state:'completed',kind:'sensitivities',run:null,context,asOf:report.asOf,result:report};
  window.recovery=JSON.stringify({version:1,queued:true,context,asOf:report.asOf,scope:{bookIds:[],portfolioIds:[],tradeIds:null},parts:[{request,job:null,failure:null,command:{path:'portfolio/risk/jobs',operationId,body:{request},uncertain:true}}]});
  sessionStorage.setItem('openetrm.risk.workspace.submission.synthetic-reader',recovery);
  window.views=[];document.querySelector('#risk-page').hidden=false;
  window.risk=(await import('/risk-workspace.js')).setupRiskWorkspace({api:async path=>path==='portfolio/risk/jobs'?(window.holdJobs?new Promise(resolve=>{window.holdJobs=false;window.releaseJobs=()=>resolve([job]);}):[job]):path.includes('/archive?')?{items:[],next:null}:path==='portfolio/risk/jobs/'+id?job:[],
   act:async work=>work(),busy:()=>false,zone:()=> 'UTC',identity:()=> 'synthetic-reader',canRun:()=>true,instant:value=>value,source:()=>{throw Error('No current close selected');},convention:()=>null,frames:()=>[],changed(){},view:next=>views.push(next)});
  await risk.load();
 })()`);
 assert.match(await evaluate("document.querySelector('#workspace-risk-job-status').textContent"),/Completed/);
 assert.equal(await evaluate('views.at(-1)'),'risk','an already-completed recovered job belongs in Reports, where its charts can draw');
 assert.equal(await evaluate("sessionStorage.getItem('openetrm.risk.workspace.submission.synthetic-reader')"),null,'the acknowledged original job settles recovery custody');
 await evaluate("sessionStorage.setItem('openetrm.risk.workspace.submission.synthetic-reader',recovery);window.holdJobs=true;window.reload=risk.load();risk.showView('risk-runs');views.push('risk-runs');releaseJobs();reload");
 assert.equal(await evaluate('views.at(-1)'),'risk-runs','recovery finishing later must preserve a report view explicitly selected while it loaded');
}));
