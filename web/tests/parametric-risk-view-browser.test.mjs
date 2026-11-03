import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
import {pricedReport} from './parametric-fixture.mjs';

// The real VaR report view, drawn from a synthetic report. A stand-in formatter marks which amounts were sent for display and at
// what precision; no application runtime, account or database is attached.
test('VaR amounts show at money display precision and keep their exact values',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(
 '<link rel="stylesheet" href="/app.css"><div id="host" style="width:1100px"></div>',async evaluate=>{
  const shown=await evaluate(`(async()=>{
   const {validateParametric}=await import('/parametric-risk.js'),{newParametricView,renderParametric}=await import('/parametric-risk-view.js'),{prepareAmountDisplay}=await import('/amount-display.js');
   const r=${JSON.stringify(pricedReport())};
   await prepareAmountDisplay(r,async rows=>rows.map(v=>'shown '+v.value+' at '+v.decimalPlaces));
   const host=document.querySelector('#host'),p=r.parametric.result.partitions[0];
   renderParametric(host,validateParametric(r),newParametricView(),()=>{},[]);
   const visible=[...host.querySelectorAll('p')].filter(n=>!n.closest('details:not([open])')).map(n=>n.textContent).join(' ');
   return {cards:[...host.querySelectorAll('.parametric-stat strong')].map(n=>[n.textContent,n.title]),exact:[p.valueAtRisk,p.expectedShortfall,p.standardDeviation],
    variance:p.variance,visible,measures:[...host.querySelectorAll('select[aria-label="Parametric native measure"] option')].map(o=>o.textContent)};
  })()`);
  assert.deepEqual(shown.cards,shown.exact.map(v=>['shown '+v+' at 3 USD',v]));
  assert.match(shown.visible,/Model loss mean: shown 0 at 3 USD\./);
  assert.ok(!shown.visible.includes(shown.variance),'the exact variance stays in the collapsed exact moments');
  assert.deepEqual(shown.measures,['Present value · USD']);
 }));
