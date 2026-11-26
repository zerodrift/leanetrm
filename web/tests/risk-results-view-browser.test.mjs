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

const lane=(counted,unassessedDays,operations)=>[{right:0,counted,unassessedDays,
 work:{operations,pricedChoices:7,moves:11,factorSteps:3,maximumStates:4,peakBytes:2048}}];
const holding={tradeId:'T',tradeReference:'Trade 7',instrumentReference:'Instrument 8',instrumentType:'physical_gas',bindingId:'B',bookId:'A',book:'A',
 measure:'pv',measureLabel:'Present value'};
for(const kind of ['factor','named scenario'])
test(`a ${kind} report distinguishes missing and zero swing values and shows v2 operations and day coverage`,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(
 '<div id="host" style="width:1100px"></div>',async evaluate=>{
  const base=lane(null,2,17),partial=lane('0',1,19);
  if(kind==='factor'){
   const r=report({});
   r.factors[0].positions=[{...holding,values,swingRights:{base,up:partial,down:lane('89',0,21)}}];
   await heading(evaluate,r);
  }else{
   const amounts={base:'100',value:'105',pnl:'5',loss:'-5'},values={...amounts,display:amounts,currency:'USD',currencyReference:{code:'usd'}};
   const joint={kind:'scenarios',baseTotals:[{...holding,value:'100',display:'100',currency:'USD'}],
    samples:[{id:'stress',label:'Joint stress',shifts:[],totals:[{...holding,values}],positions:[{...holding,values,swingRights:{base,value:partial}}]}]};
   await evaluate(`(async()=>{const {renderJointResults,newJointView}=await import('/joint-risk-results-view.js');
    renderJointResults(document.querySelector('#host'),${JSON.stringify(joint)},newJointView(),()=>{},[]);})()`);
  }
  const grid=await evaluate("Array.from(document.querySelectorAll('.swing-risk-samples tr'),r=>Array.from(r.cells,c=>c.textContent))");
  assert.deepEqual(grid[0].slice(3),['Remaining-right value','Currency','Unassessed days','Counted operations']);
  assert.deepEqual(grid[1].slice(3),['Unassessed','USD','2','17']);
  assert.deepEqual(grid[2].slice(3),['0','USD','1','19']);
  if(kind==='factor')assert.deepEqual(grid[3].slice(3),['89','USD','0','21']);
 }));
