import test from 'node:test';
import assert from 'node:assert/strict';
import {validateParametric,parametricCharts} from '../dist/parametric-risk.js';
import {chartOptions} from '../dist/chart-view.js';

const id='a'.repeat(64),column='b'.repeat(64),currency={revision:{catalogId:'reference',revisionId:'version'},code:'usd'},unit={currency,quantity:{revision:currency.revision,code:'energy'},multiplier:'1'};
function report(){
 const policy={convention:'dated_factor_delta_normal_v1',numericalPolicy:'exact_moments_binary64_normal_v1',estimator:'equal_weight_sample_n_minus_one_v1',centering:'sample_mean_centered_v1',
  drift:'zero_drift_v1',horizon:'frozen_portfolio_one_adjacent_session_v1',tail:'signed_upper_loss_tail_v1',confidence:'0.99'};
 const finiteDifference={convention:'native_tick_central_half_nominal_double_v1',tradingTicks:1,deltaTolerance:{absolute:'0',relative:'0'}};
 const contribution={id:'book',mean:'0',varianceCross:'4000000000000',valueAtRisk:'4652695.7480816816',expectedShortfall:'5330428.44069161'};
 const history={id,forecast:{contextId:'context'},policy:{minimumSamples:2},columns:[{id:column,mapping:{factor:{unit}}}],samples:[{id:'a',changes:['1']},{id:'b',changes:['0']},{id:'c',changes:['-1']}],rejected:[],available:true};
 const request={history:{scope:{context:{contextId:'context',snapshotId:'snapshot'},bookIds:['book'],portfolioIds:[],tradeIds:['trade']},expectedManifestId:id},policy,finiteDifference};
 return {context:{contextId:'context'},snapshotId:'snapshot',tradeIds:['trade'],excludedPositions:[],parametric:{request,history,
  result:{policy,available:true,unavailableReasons:[],calibration:{historyId:id,samples:3,columns:[column],means:['0'],covariance:[['4']],correlation:[['1']],rank:1,constantColumns:[],conditioning:'full_rank',correlationCondition:'1'},
   partitions:[{measure:'present_value',currency,mean:'0',variance:'4000000000000',standardDeviation:'2000000',valueAtRisk:contribution.valueAtRisk,expectedShortfall:contribution.expectedShortfall,
    books:[contribution],factors:[{...contribution,id:column}],density:[{score:'-4',loss:'-8000000',density:'0.00000000006691511288244269'},{score:'0',loss:'0',density:'0.00000019947114020071635'}],
    linearizedLosses:[{sample:'a',loss:'-2000000'},{sample:'b',loss:'0'},{sample:'c',loss:'2000000'}],varReconciliation:'0',esReconciliation:'0'}]},
  exposures:[{bookId:'book',measure:'present_value',currency}],factors:[{id:column,factor:{id:'axis',label:'Gas futures · January 2026',unit:'USD / MMBtu',coordinate:'market_quote',kind:'point',curves:['gas']}}],books:[{id:'book',name:'Gas trading'}],
  partitions:[{measure:'present_value',measureLabel:'Present value',currencyReference:currency,currency:'USD',display:{standardDeviation:'2000000',valueAtRisk:'4652695.7480816816',expectedShortfall:'5330428.44069161'}}],
  finiteDifference:{...finiteDifference,evaluated:true,estimates:[{columnId:column,bookId:'book',measure:'present_value',currency,half:'1000000',nominal:'1000000',double:'1000000',stable:true,gamma:'0',bump:'0.01',unit}]},passes:[{}, {}, {}]}};
}
test('parametric graphics transport backend distribution/contribution/correlation values in native currency',()=>{
 const r=report(),view=validateParametric(r,r.parametric.request),charts=parametricCharts(view,0);
 assert.ok(charts.some(c=>c.kind==='heatmap'));assert.ok(charts.some(c=>c.title.includes('contribution')));
 for(const spec of charts)assert.doesNotThrow(()=>chartOptions(spec));
 assert.equal(charts[0].series[0].points[0].y,r.parametric.result.partitions[0].density[0].density);
});
test('missing covariance/FD coverage, unstable successful results and wrong captured policy refuse display',()=>{
 for(const edit of [r=>r.parametric.result.calibration.covariance=[],r=>r.parametric.finiteDifference.estimates=[],r=>r.parametric.finiteDifference.estimates[0].stable=false,
  r=>r.parametric.history.id='c'.repeat(64),r=>r.parametric.result.partitions[0].valueAtRisk='4E6',r=>r.parametric.result.partitions=[]]){
  const r=report();edit(r);assert.throws(()=>validateParametric(r),/parametric|coverage|captured|consistent/i);
 }
 const expected=report().parametric.request,r=report();r.parametric.request.policy={...expected.policy,confidence:'0.95'};r.parametric.result.policy=r.parametric.request.policy;
 assert.throws(()=>validateParametric(r,expected),/parametric|captured|consistent/i);
});
test('unavailable calibration never displays successful risk cards; zero variance has no density',()=>{
 const r=report();r.parametric.result.available=false;r.parametric.result.partitions=[];r.parametric.result.unavailableReasons=['incomplete_portfolio'];r.parametric.partitions=[];
 validateParametric(r);assert.deepEqual(parametricCharts(r.parametric,0),[]);
 const constant=report();constant.parametric.result.partitions[0].density=[];constant.parametric.result.partitions[0].variance='0';constant.parametric.result.partitions[0].standardDeviation='0';
 validateParametric(constant);assert.ok(!parametricCharts(constant.parametric,0).some(c=>c.title.includes('density')));
});

test('one complete statistical report enters the shared report presentation and refuses sensitivity multipart composition',async()=>{
 const {reportPresentation}=await import('../dist/risk-results.js');const r=report();r.scenario={id:'base',depth:0,isBase:true};
 const run={key:1,queued:false,running:false,retained:false,setupText:'Parametric risk',context:r.context,scope:r.parametric.request.history.scope,parts:[{report:r,unknown:false,unlisted:false}]};
 const p=reportPresentation(run,'delta');assert.equal(p.state,'complete');assert.equal(p.parametric,r.parametric);assert.equal(p.factors.length,0);
 assert.throws(()=>reportPresentation({...run,parts:[...run.parts,...run.parts]},'delta'),/one complete|parts/);
});
test('a successful report cannot omit a native measure or book present in its finite differences',()=>{
 const r=report();r.parametric.finiteDifference.estimates.push({...r.parametric.finiteDifference.estimates[0],measure:'other_pv'});
 assert.throws(()=>validateParametric(r),/parametric|coverage|captured|consistent/i);
 const book=report();book.parametric.books.push({id:'hedge',name:'Hedge book'});book.parametric.finiteDifference.estimates.push({...book.parametric.finiteDifference.estimates[0],bookId:'hedge'});
 assert.throws(()=>validateParametric(book),/parametric|coverage|captured|consistent/i);
});

test('deleting a whole native partition or book cannot turn missing FD exposure into complete coverage',()=>{
 const partition=report(),v=partition.parametric;
 v.exposures.push({bookId:'book',measure:'other_pv',currency});v.finiteDifference.estimates.push({...v.finiteDifference.estimates[0],measure:'other_pv'});
 v.result.partitions.push({...structuredClone(v.result.partitions[0]),measure:'other_pv'});v.partitions.push({...structuredClone(v.partitions[0]),measure:'other_pv'});validateParametric(partition);
 v.finiteDifference.estimates.pop();v.result.partitions.pop();v.partitions.pop();assert.throws(()=>validateParametric(partition),/parametric|coverage|consistent/i);
 const book=report(),w=book.parametric;w.books.push({id:'hedge',name:'Hedge book'});w.exposures.push({bookId:'hedge',measure:'present_value',currency});
 w.finiteDifference.estimates.push({...w.finiteDifference.estimates[0],bookId:'hedge'});w.result.partitions[0].books.push({...w.result.partitions[0].books[0],id:'hedge'});validateParametric(book);
 w.finiteDifference.estimates.pop();w.result.partitions[0].books.pop();assert.throws(()=>validateParametric(book),/parametric|coverage|consistent/i);
});

test('covariance heatmap carries exact backend cells and both native units',()=>{
 const sample=report();const view=validateParametric(sample);const chart=parametricCharts(view,0,'valueAtRisk',0,0,'covariance').at(-1);
 assert.equal(chart.title,'Factor covariance');assert.equal(chart.series[0].points[0].y,view.result.calibration.covariance[0][0]);
 assert.ok(chart.series[0].points[0].text.includes(view.factors[0].factor.unit+' × '+view.factors[0].factor.unit));
});

test('retained parametric reports refuse incomplete EOD price provenance',()=>{
 const make=()=>{
  const r=report(),h=r.parametric.history;h.policy={...h.policy,alignment:'daily_latest_visible_session_end_v1',regimes:'ordered_price_types_fixed_series_source_unit_contract_v1',priceTypes:['official_close_v1']};
  r.parametric.request.history.policy=structuredClone(h.policy);
  const marks=['9','10','10','9'].map((value,i)=>({observation:{id:'mark'+i},value}));
  h.sessions=marks.map((_,i)=>({date:'day'+i,close:'close'+i}));
  h.evidence=marks.map((m,i)=>({observation:{key:m.observation,source:{code:'close'},value:m.value,role:2,state:2,observedAt:'2026-01-0'+(i+1)+'T16:00:00Z'}}));
  for(const [i,s] of h.samples.entries()){s.from={at:h.sessions[i].close,marks:[marks[i]]};s.to={at:h.sessions[i+1].close,marks:[marks[i+1]]};}
  h.dayPrices=h.sessions.map(s=>({at:s.close,choices:[{selectedType:'official_close_v1',officialCloseStatus:'available',availableTypes:['official_close_v1']}]}));return r;
 };
 validateParametric(make());
 for(const edit of [r=>{delete r.parametric.history.dayPrices;r.parametric.history.policy={...r.parametric.history.policy,alignment:'exact_session_close_v1',regimes:'exact_source_role_state_unit_contract_v1'};delete r.parametric.history.policy.priceTypes;},r=>delete r.parametric.history.evidence[0].observation.observedAt,r=>delete r.parametric.history.evidence[0].observation.source,r=>delete r.parametric.history.dayPrices,r=>r.parametric.history.dayPrices[0].choices=[],r=>r.parametric.history.dayPrices[0].choices[0].availableTypes=[],r=>r.parametric.history.policy.priceTypes=['trader_mark_final_v1']]){
  const r=make();edit(r);assert.throws(()=>validateParametric(r),/price|provenance/i);
 }
});
