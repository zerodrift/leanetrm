import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const amounts={base:'100',up:'102',down:'98',upPnl:'2',downPnl:'-2',delta:'20',gamma:'3'};
const values={...amounts,display:amounts,currency:'USD',currencyReference:{code:'usd'},deltaUnit:'USD / gas',gammaUnit:'USD / gas²'};
const report=o=>({context:{contextId:'c1'},snapshotId:'s1',scenario:{id:'base',depth:0,isBase:true},excludedPositions:[],projectionPolicies:[],
 factors:[{factor:{id:'jan',label:'Gas · Jan 2027',coordinate:'market_quote',kind:'contract',unit:'gas',curves:['gas'],deliveryFrom:'2027-01-01',deliveryTo:'2027-02-01'},bump:'0.182',
  totals:[{bookId:'A',book:'A',measure:'pv',measureLabel:'Present value',values}],positions:[]}],...o});
const policy={observationRole:'daily_settlement',numericalPolicy:'american_futures_option_leisen_reimer_v1',scenarioPolicy:'option_implied_volatility_held_v1',calibrationFallback:'option_settlement_at_intrinsic_v1'};
// The heading of the shared report widget, as Risk and the Blotter draw it for one report.
const heading=(evaluate,drawn)=>evaluate(`(async()=>{
 const {setupRiskResults}=await import('/risk-results-view.js');
 const r=${JSON.stringify(drawn)},host=document.querySelector('#host');
 setupRiskResults(host,()=>'UTC').show({key:1,queued:false,running:false,retained:true,setupText:'Held contracts',context:r.context,scope:{bookIds:['A'],portfolioIds:[]},parts:[{report:r,unknown:false,unlisted:false}]});
 return host.querySelector('.risk-result-heading').textContent;
})()`);

test('a report that values options states its option valuation policy in its heading, and one that values none states nothing about options',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(
 '<link rel="stylesheet" href="/app.css"><div id="host" style="width:1100px"></div>',async evaluate=>{
  const valued=await heading(evaluate,report({scenarioPolicy:policy.scenarioPolicy,optionCalibrations:[{mode:'solved'}],inputs:{positions:[],markets:[],pricingPolicy:{futuresOptions:policy}}}));
  assert.match(valued,/Option valuation · numerical policy american_futures_option_leisen_reimer_v1 · scenario policy option_implied_volatility_held_v1 · calibration fallback option_settlement_at_intrinsic_v1 · observation role daily_settlement/);
  assert.match(valued,/Captured scope/,'beside the captured scope');
  const plain=await heading(evaluate,report({scenarioPolicy:null,optionCalibrations:[]}));
  assert.doesNotMatch(plain,/Option valuation/);
 }));
