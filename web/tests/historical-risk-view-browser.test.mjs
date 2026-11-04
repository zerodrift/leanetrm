import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
import {historicalReport} from './historical-fixture.mjs';

test('historical VaR, ES and mean cards use money display precision and retain exact amounts',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(
 '<link rel="stylesheet" href="/app.css"><div id="host" style="width:1100px"></div>',async evaluate=>{
  const report=historicalReport();report.historical.result.partitions[0].expectedShortfall='29/3';report.historical.result.partitions[0].mean='-2/13';
  const shown=await evaluate(`(async()=>{
   const {validateHistorical}=await import('/historical-risk.js'),{newHistoricalView,renderHistorical}=await import('/historical-risk-view.js'),{prepareAmountDisplay}=await import('/amount-display.js');
   const r=${JSON.stringify(report)};await prepareAmountDisplay(r,async rows=>rows.map(v=>'shown '+v.value+' at '+v.decimalPlaces));
   const host=document.querySelector('#host');renderHistorical(host,validateHistorical(r),newHistoricalView(),()=>{},[]);
   return [...host.querySelectorAll('.parametric-stat strong')].map(n=>[n.textContent,n.title]);
  })()`);
  assert.deepEqual(shown,['10','29/3','-2/13'].map(v=>['shown '+v+' at 3 USD',v]));
 }));
