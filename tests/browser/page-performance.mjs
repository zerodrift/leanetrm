// Invoked by benchmark_pages.py against its newly owned synthetic installation.
import {readFile} from 'node:fs/promises';
import {setTimeout as delay} from 'node:timers/promises';
import {withOwnedBrowser} from './owned-browser.mjs';

if(process.argv.length!==4)throw Error('Invoke through tools/benchmark_pages.py.');
const target=JSON.parse(await readFile(process.argv[2],'utf8'));
if(!Number.isInteger(target.samples)||target.samples<1||target.samples>20||!Number.isFinite(target.latencyMs)||target.latencyMs<0||!Number.isFinite(target.budgetMs)||target.budgetMs<=0)throw Error('Explicit bounded benchmark parameters are required.');
let report;
await withOwnedBrowser(process.argv[2],process.argv[3],async({command,evaluate,wait,persona,idle,url})=>{
 await command('Runtime.enable');await command('Page.enable');await command('Network.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Network.emulateNetworkConditions',{offline:false,latency:target.latencyMs,downloadThroughput:10*1024*1024,uploadThroughput:5*1024*1024});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`(()=>{
  window.__pageRequests=[];const original=window.fetch;
  window.fetch=async(...args)=>{const start=performance.now();const response=await original(...args);window.__pageRequests.push({path:new URL(String(args[0]),location.href).pathname,status:response.status,headerMs:performance.now()-start});return response;};
 })();`});
 await command('Page.navigate',{url:url+'/'});
 await wait(idle+"&&document.querySelector('#error').hidden&&document.querySelectorAll('.curve-button').length>0&&performance.getEntriesByName('leanetrm:workspace-ready','mark').length===1");
 const initial=await evaluate(`({readyMs:performance.getEntriesByName('leanetrm:workspace-ready','mark')[0].startTime,requests:window.__pageRequests})`);
 const samples=[],pricing=[];
 const pages={market:['market-page',"document.querySelectorAll('.curve-button').length>0"],delivery:['delivery-page',"document.querySelector('#delivery-price-known').textContent.startsWith('Known at')"],jobs:['jobs-page',"document.querySelector('#jobs-infrastructure').textContent.length>0"],power:['power-page',"document.querySelector('#tou-list').textContent.length>0&&document.querySelector('#power-product').options.length>1"],reference:['reference-page',"document.querySelector('#reference-page-status').textContent.startsWith('Rows ')&&!!document.querySelector('#reference .tabulator-row, #reference tbody tr')"],schedules:['schedules-page',"document.querySelector('#dates-saved').textContent.length>0"],configuration:['curve-date-editor',"document.querySelector('#curve-dates-saved').textContent.length>0"],parties:['parties-page',"document.querySelector('#party-list').textContent.length>0"],futures:['futures-page',"document.querySelector('#futures-holdings .tabulator-row')&&!document.querySelector('#blotter-export').disabled"],admin:['admin-page',"document.querySelector('#admin-members').textContent.includes('Viewer')||document.querySelector('#admin-members').textContent.includes('viewer')"]};
 async function completed(){
  const deadline=Date.now()+65000;
  while(Date.now()<deadline){
   const value=await evaluate('window.__pageMeasurement');
   if(value?.done){if(value.error)throw Error(value.error);return value.result;}
   await delay(50);
  }
  throw Error('Page measurement did not complete.');
 }
 async function measure(page){
  const [section,ready]=pages[page];
  await evaluate(`(()=>{
   window.__pageMeasurement={done:false};const accept=result=>{window.__pageMeasurement={done:true,result};},reject=error=>{window.__pageMeasurement={done:true,error:String(error)};};
   const start=performance.now(),first=window.__pageRequests.length;document.querySelector('#nav-${page}').click();
   const poll=()=>{if(performance.now()-start>60000){reject(Error('Page readiness timed out: ${page}'));return;}
    if(${idle}&&!document.querySelector('#${section}').hidden&&(${ready})){
     if(!document.querySelector('#error').hidden){reject(Error(document.querySelector('#error').textContent));return;}
     requestAnimationFrame(()=>requestAnimationFrame(()=>accept({page:${JSON.stringify(page)},readyMs:performance.now()-start,requests:window.__pageRequests.slice(first)})));
    }else requestAnimationFrame(poll);};requestAnimationFrame(poll);
  })()`);
  samples.push(await completed());
 }
 for(const [profile,allowed] of [['editor',['market','delivery','jobs','power','reference','schedules','configuration']],['trader',['parties','futures']],['admin',['admin']]]){
  await persona(profile);
  if(profile!=='admin'){
   await evaluate(`(()=>{const n=document.querySelector('#snapshot');n.value=[...n.options].at(-1).value;n.dispatchEvent(new Event('change',{bubbles:true}));})()`);await wait(idle);
  }
  for(let i=0;i<target.samples;i++)for(const page of allowed)await measure(page);
  if(profile==='editor'){
   await measure('market');
   for(let i=0;i<target.samples;i++){
    await evaluate(`(()=>{
     window.__pageMeasurement={done:false};const accept=result=>{window.__pageMeasurement={done:true,result};},reject=error=>{window.__pageMeasurement={done:true,error:String(error)};};
     const start=performance.now(),first=window.__pageRequests.length;
     [...document.querySelectorAll('.curve-button')].find(n=>n.textContent.startsWith('Local power forwards')).click();
     const poll=()=>{if(performance.now()-start>60000){reject(Error('Curve price timed out'));return;}
      if(${idle}&&!document.querySelector('#error').hidden){reject(Error(document.querySelector('#error').textContent));return;}
      if(${idle}&&document.querySelector('#contracts tbody tr')&&document.querySelector('#curve-chart canvas'))requestAnimationFrame(()=>requestAnimationFrame(()=>accept({readyMs:performance.now()-start,requests:window.__pageRequests.slice(first)})));
      else requestAnimationFrame(poll);};requestAnimationFrame(poll);
    })()`);
    pricing.push(await completed());
   }
  }
 }
 const summary=values=>{const sorted=values.map(v=>v.readyMs).sort((a,b)=>a-b);return {samples:values,p50Ms:sorted[Math.floor(sorted.length/2)],p95Ms:sorted[Math.min(sorted.length-1,Math.floor(sorted.length*.95))],maximumMs:sorted.at(-1)};};
 const navigation=Object.fromEntries(Object.keys(pages).map(page=>[page,summary(samples.filter(s=>s.page===page))]));
 const curvePricing=summary(pricing);
 report={initial,navigation,curvePricing,budgetPassed:initial.readyMs<target.budgetMs&&curvePricing.p95Ms<target.budgetMs&&Object.values(navigation).every(v=>v.p95Ms<target.budgetMs),network:{latencyMs:target.latencyMs,downloadBytesPerSecond:10*1024*1024,uploadBytesPerSecond:5*1024*1024},readiness:'Busy false, expected visible authorized data and two animation frames; populated Blotter grid/export readiness. Initial starts at navigation time origin; subsequent pages start at click. Curve pricing includes returned rows and rendered chart.'};
});
process.stdout.write(JSON.stringify(report)+'\n');
