import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const markup=(await readFile(new URL('../src/index.html',import.meta.url),'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g,'');

for(const [page,prefix] of [['Blotter',''],['Risk','workspace-']]){
test(`the ${page} editor stays usable while recent jobs load or need a retry`,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
 await evaluate(`(async()=>{
  const prefix=${JSON.stringify(prefix)};
  if(prefix)(await import('/risk-results-view.js')).copyRiskEditor(document.getElementById('risk-workspace-editor-body'),prefix);
  window.control=id=>document.getElementById(prefix+id);
  const report=control('risk-report');document.body.append(report);report.hidden=false;
  const scope={context:{snapshotId:'synthetic-snapshot',contextId:'synthetic-context'},bookIds:[],portfolioIds:[]};
  const catalog={context:{contextId:'synthetic-context',effectiveOn:'2026-01-01',knownThrough:1},asOf:'2026-01-01T00:00:00Z',
   limits:{maxHoldingsBumps:16,maxBooksBumps:64,maxStress:4,maxParts:4},scenarioTargets:[],heldContractTargets:[],heldCurveTargets:[],
   heldContractsWithoutTarget:[],heldCurvesWithoutTarget:[],excludedPositions:[]};
  window.reads=[];window.receipts=[];window.catalogReads=0;window.denials=0;window.failures=[];window.opened=false;window.actor='first';
  window.settle=()=>new Promise(resolve=>setTimeout(resolve,0));
  window.job=id=>({id,run:null,state:'completed'});
  window.risk=(await import('/risk-view.js')).setupRisk({api:(path,body,signal)=>{
   if(path==='portfolio/risk/catalog'){catalogReads++;return Promise.resolve(catalog);}
   if(path==='portfolio/risk/jobs')return new Promise((resolve,reject)=>reads.push({resolve,reject,signal}));
   if(path.startsWith('portfolio/risk/jobs/'))return new Promise(resolve=>receipts.push({path,resolve}));
   throw Error('Unexpected read '+path);
  },prefix,act:work=>work(),busy:()=>false,zone:()=>'UTC',profile:()=>actor,canRun:()=>true,scope:()=>scope,
   onDenied:()=>denials++,failed:error=>failures.push(error.code),onChange(){}});
  window.fields=control('risk-fields');window.jobs=control('risk-job-history');
  risk.open().then(()=>opened=true);await settle();
 })()`);
 assert.deepEqual(await evaluate('[opened,fields.disabled,jobs.disabled,reads.length]'),[true,false,true,1]);

 await evaluate("control('risk-bump').dispatchEvent(new Event('input'));reads[0].resolve([job('first')]);settle()");
 assert.equal(await evaluate("Array.from(jobs.options).some(o=>o.value==='first')"),true);
 await evaluate("risk.open();risk.open();settle()");
 assert.equal(await evaluate('reads[1].signal.aborted'),true);
 await evaluate("reads[2].resolve([job('latest')]);reads[1].resolve([job('earlier')]);settle()");
 assert.deepEqual(await evaluate('Array.from(jobs.options,o=>o.value)'),['','latest']);

 await evaluate("risk.open();reads[3].reject(Error('Temporary read failure'));settle()");
 assert.equal(await evaluate('fields.disabled'),false);
 assert.equal(await evaluate("control('risk-history-retry').hidden"),false);
 await evaluate("control('risk-history-retry').click();reads[4].resolve([]);settle()");
 assert.deepEqual(await evaluate("[catalogReads,jobs.disabled,control('risk-history-retry').hidden]"),[1,false,true]);

 await evaluate("risk.open();risk.reset();actor='second';risk.open();settle()");
 assert.equal(await evaluate('reads[5].signal.aborted'),true);
 await evaluate("reads[6].resolve([job('second')]);reads[5].reject(Object.assign(Error('Old account'),{code:'access_denied'}));settle()");
 assert.deepEqual(await evaluate('[denials,Array.from(jobs.options,o=>o.value)]'),[0,['','second']]);
 await evaluate("risk.open();reads[7].reject(Object.assign(Error('Current account'),{code:'access_denied'}));settle()");
 assert.deepEqual(await evaluate('[denials,failures,fields.disabled,Array.from(jobs.options,o=>o.value)]'),[1,['access_denied'],true,['']]);

 await evaluate(`(async()=>{
  const uuid=n=>'00000000-0000-4000-8000-'+String(n).padStart(12,'0');
  const context={contextId:uuid(1),knownAt:'2026-01-01T00:00:00Z',knownThrough:1,effectiveOn:'2026-01-01'};
  const {attachReadReferences}=await import('/read-references.js');
  const runId=uuid(9);
  window.matched=[1,2].map(part=>({id:uuid(10+part),state:'failed',error:{code:'synthetic',message:'Synthetic worker failure'},
   context:attachReadReferences({...context},{[context.contextId]:'synthetic-reference'}),
   run:{id:runId,part,parts:2},requestId:uuid(20+part)}));
  risk.adopt({version:1,queued:true,id:runId,context,parts:matched.map(job=>({request:{context:{...context}},job:null,failure:null,
   command:{path:'portfolio/risk/jobs',operationId:job.requestId,body:{request:{context:{...context}},run:job.run},uncertain:true}}))});
  window.outcomes=[];
  for(let opening=0;opening<2;opening++)risk.open().then(()=>outcomes.push('ready'),error=>outcomes.push(error.message));
  await settle();
 })()`);
 assert.deepEqual(await evaluate('[outcomes.length,catalogReads,reads.length,fields.disabled]'),[0,2,9,true]);
 await evaluate('reads[8].resolve(matched);settle()');
 assert.deepEqual(await evaluate('receipts.map(receipt=>receipt.path)'),[await evaluate("'portfolio/risk/jobs/'+matched[0].id")]);
 await evaluate('receipts[0].resolve(matched[0]);settle()');
 assert.deepEqual(await evaluate('receipts.map(receipt=>receipt.path)'),await evaluate("matched.map(job=>'portfolio/risk/jobs/'+job.id)"));
 await evaluate('receipts[1].resolve(matched[1]);settle()');
 assert.deepEqual(await evaluate('[outcomes,risk.unconfirmed()]'),[['ready','ready'],false]);
}));
}
