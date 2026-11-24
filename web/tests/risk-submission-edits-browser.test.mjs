import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const markup=(await readFile(new URL('../src/index.html',import.meta.url),'utf8')).replace(/<script\b[^>]*>[\s\S]*?<\/script>/g,'');

const openEditor=`(async()=>{
  const scope={context:{snapshotId:'synthetic-snapshot',contextId:'synthetic-context'},bookIds:[],portfolioIds:[]};
  const target={id:'synthetic-target',label:'Synthetic contract',coordinate:'market_quote',kind:'contract',unit:'USD',priceUnit:{},factor:{coordinate:'market_quote',target:{}}};
  const catalog={context:{contextId:'synthetic-context',effectiveOn:'2026-01-01',knownThrough:1},asOf:'2026-01-01T00:00:00Z',
   limits:{maxHoldingsBumps:16,maxBooksBumps:64,maxStress:4,maxParts:4},scenarioTargets:[target],heldContractTargets:[target.id],heldCurveTargets:[],
   heldContractsWithoutTarget:[],heldCurvesWithoutTarget:[],excludedPositions:[],bumpConvention:'synthetic',stressConvention:'synthetic',convention:'synthetic',numericalPolicy:'synthetic',pricingPolicy:{}};
  window.pending=[];window.failures=[];window.cleared=0;window.actions=0;window.risk=(await import('/risk-view.js')).setupRisk({api:async path=>path==='portfolio/risk/catalog'?catalog:path==='portfolio/risk/jobs'?[]:new Promise((resolve,reject)=>pending.push({resolve,reject})),
   act:async work=>{actions++;await work();},failed:error=>failures.push(error.message),failureCleared:()=>cleared++,busy:()=>false,zone:()=> 'UTC',profile:()=> 'synthetic-reader',canRun:()=>true,scope:()=>scope,onDenied(){},onChange(){}});
  document.querySelector('#risk-report').hidden=false;document.body.append(document.querySelector('#risk-report'));
  await risk.open();document.querySelector('input[name=risk-choice][value=held]').click();
  const size=document.querySelector('#risk-size-0');size.focus();size.select();
 })()`;

test('submitting a focused shock size consumes its edits even when every read is refused',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async(evaluate,command)=>{
 await evaluate(openEditor);
 await command('Input.insertText',{text:'0.09'});
 assert.equal(await evaluate('risk.edited()'),true);
 await evaluate("document.querySelector('#risk-form').requestSubmit();pending[0].reject(Error('Synthetic refused read'));new Promise(resolve=>setTimeout(resolve,30))");
 await evaluate("document.querySelector('#risk-size-0').blur()");
 assert.equal(await evaluate('risk.edited()'),false,'a later native change event must not restore edits already submitted');
 assert.deepEqual(await evaluate('failures'),['Synthetic refused read'],'read refusals must still reach the owning page error display');
 assert.equal(await evaluate('cleared'),1,'a new explicit read clears the previous page error');
 assert.equal(await evaluate('actions'),0,'read error reporting must not acquire application-wide busy state');
 await evaluate("document.querySelector('#risk-form').requestSubmit();const parent=document.createElement('section');document.body.append(parent);parent.append(document.querySelector('#risk-report'));parent.hidden=true;pending[1].reject(Error('A refusal from the previous page'));new Promise(resolve=>setTimeout(resolve,30))");
 assert.deepEqual(await evaluate('failures'),['Synthetic refused read'],'a view left while its read settles must not paint its refusal over another page');
}));

for(const mode of ['immediate','polled']){
 test(`the ${mode} risk result admits legacy work and rejects a partial work receipt before rendering`,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(markup,async evaluate=>{
  await evaluate(openEditor);
  await evaluate(`(async()=>{
   const context={contextId:'synthetic-context',knownAt:'2026-01-01T00:00:00Z',knownThrough:1,effectiveOn:'2026-01-01'};
   const report={context,snapshotId:'synthetic-snapshot',scenario:{isBase:true,depth:0},factors:[],excludedPositions:[],projectionPolicies:[],heldInstruments:1,connectedScenarios:[],
    work:{positionCount:1,uniqueValuations:1,scenarioCount:3,preparationScenarioCount:0,unitPricerCalls:3,rebuiltCurveNodes:0,reusedCurveNodes:0,repeatedInstrumentReferences:0,quantityApplications:3}};
   const job={id:'synthetic-job',state:'running',kind:'sensitivities',run:null,context,asOf:context.knownAt};
   window.rejections=[];window.addEventListener('unhandledrejection',event=>{rejections.push(event.reason.message);event.preventDefault();});
   window.finish=async extra=>{
    const before=pending.length,polled=${JSON.stringify(mode)}==='polled';
    if(polled)await risk.reviewJob(job.id,job);
    else{
     const size=document.querySelector('#risk-size-0');size.value='0.1';size.dispatchEvent(new Event('input',{bubbles:true}));
     document.querySelector('#risk-form').requestSubmit();
    }
    const deadline=performance.now()+5000;
    while(pending.length===before){if(performance.now()>deadline)throw Error('The risk result was never requested.');await new Promise(resolve=>setTimeout(resolve,10));}
    const result={...report,work:{...report.work,...extra}};
    pending[before].resolve(polled?{...job,state:'completed',result}:result);
    await new Promise(resolve=>setTimeout(resolve,0));
   };
  })()`);
  await evaluate('finish({})');
  assert.equal(await evaluate('risk.current().parts[0].report.work.unitPricerCalls'),3);
  assert.match(await evaluate("document.querySelector('#risk-work').textContent"),/3 pricer calls/);
  await evaluate(mode==='polled'?'finish({totalUnitPricerCalls:4})':'finish({preparationUnitPricerCalls:1})');
  if(mode==='immediate'){
   assert.deepEqual(await evaluate('failures'),['The risk work receipt has incomplete pricer call counts.']);
   assert.equal(await evaluate('risk.current()===undefined'),true,'a rejected single-part calculation leaves no current report');
  }else{
   assert.deepEqual(await evaluate('risk.current().parts.map(p=>[p.job.state,!!p.report,p.failure])'),
    [['completed',false,'The risk work receipt has incomplete pricer call counts.']]);
   assert.deepEqual(await evaluate('rejections'),[],'a rejected result must settle the poll normally');
  }
 }));
}
