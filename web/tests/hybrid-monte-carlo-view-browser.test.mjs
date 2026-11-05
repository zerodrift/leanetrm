import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
import {hybridMonteCarloReport} from './hybrid-monte-carlo-fixture.mjs';

test('the shared hybrid report header identifies model-valued paths before its distribution',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(
 '<link rel="stylesheet" href="/app.css"><div id="host" style="width:1100px"></div>',async evaluate=>{
  const text=await evaluate(`(async()=>{
   const {setupRiskResults}=await import('/risk-results-view.js'),{prepareAmountDisplay}=await import('/amount-display.js');
   const r=${JSON.stringify(hybridMonteCarloReport())};await prepareAmountDisplay(r,async rows=>rows.map(v=>v.value));
   const host=document.querySelector('#host');setupRiskResults(host,()=> 'UTC').show({key:1,queued:false,running:false,retained:false,setupText:'Hybrid valuation',context:r.context,scope:r.monteCarlo.request.history.scope,parts:[{report:r,unknown:false,unlisted:false}]});
   return host.querySelector('.risk-coverage')?.textContent??host.textContent;
  })()`);
  assert.match(text,/2 paths valued by the model/);assert.doesNotMatch(text,/full-PV paths/);
 }));
