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

test('every historical amount the report shows uses money display precision',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(
 '<link rel="stylesheet" href="/app.css"><div id="host" style="width:1100px"></div>',async evaluate=>{
  const report=historicalReport(),h=report.historical,p=h.result.partitions[0];
  p.books[0].valueAtRisk=p.trades[0].valueAtRisk='29/3';p.samples[1].loss='29/3';h.matrix.rows[1].losses=['29/3'];h.matrix.rows[1].scenarioValues=['271/3'];
  const shown=await evaluate(`(async()=>{
   const {validateHistorical}=await import('/historical-risk.js'),{newHistoricalView,renderHistorical}=await import('/historical-risk-view.js'),{prepareAmountDisplay}=await import('/amount-display.js');
   const r=${JSON.stringify(report)};await prepareAmountDisplay(r,async rows=>rows.map(v=>'shown '+v.value+' at '+v.decimalPlaces));
   const host=document.querySelector('#host'),state=newHistoricalView();state.sample='b';renderHistorical(host,validateHistorical(r),state,()=>{},[]);
   const money=['Allocated VaR','Allocated ES','Base PV','Scenario PV','Loss','Portfolio loss'];
   const cells=[...host.querySelectorAll('table')].flatMap(t=>{const heads=[...t.querySelectorAll('th')].map(th=>th.textContent);
    return [...t.querySelectorAll('tbody tr')].flatMap(tr=>[...tr.children].filter((_,i)=>money.includes(heads[i])).map(td=>td.textContent));});
   return {cells,options:[...host.querySelector('select[aria-label="Historical sample"]').options].map(o=>o.text),
    line:[...host.querySelectorAll('p')].find(n=>n.textContent.startsWith('Portfolio loss')).textContent};
  })()`);
  assert.ok(shown.cells.length>=8&&shown.cells.every(c=>/^shown -?[0-9/]+ at 3$/.test(c)),JSON.stringify(shown.cells));
  assert.ok(shown.cells.includes('shown 29/3 at 3')&&shown.cells.includes('shown 271/3 at 3'));
  assert.ok(shown.options.every(o=>/ · shown [0-9/]+ at 3 USD/.test(o)),JSON.stringify(shown.options));
  assert.match(shown.line,/^Portfolio loss shown 29\/3 at 3 USD/);
 }));
