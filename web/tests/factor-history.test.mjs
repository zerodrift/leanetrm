import test from 'node:test';
import assert from 'node:assert/strict';
import {validateFactorHistory,factorHistoryChart} from '../dist/factor-history.js';

const scope={context:{snapshotId:'s',contextId:'c',knowledgeMode:'retained',convention:'trading_workspace_v1'},bookIds:['b'],portfolioIds:[],tradeIds:['t']};
const mark=value=>({observation:{id:'o'},value,knowledgeOrder:3,knownAt:'2026-01-03T17:00:00Z',lineage:[{id:'o'}]});
function report(){
 const from={at:'2026-01-01T16:00:00Z',marks:[mark('10')]},to={at:'2026-01-02T16:00:00Z',marks:[mark('7')]};
 return {manifest:{id:'a'.repeat(64),forecast:{contextId:'c',valuationAt:'2026-01-03T16:00:00Z',marketCutoff:'2026-01-03T16:00:00Z',knownAt:'2026-01-03T17:00:00Z',marketKnownThrough:3},
  policy:{convention:'dated_factor_common_session_changes_v1',minimumSamples:2},columns:[{id:'x',mapping:{}}],sessions:[{date:'2026-01-01',close:from.at},{date:'2026-01-02',close:to.at}],evidence:[],
  samples:[{id:'one',from,to,changes:['-3']}],rejected:[],available:false,unavailableReason:'insufficient_common_history'},
 selection:structuredClone(scope),request:{scope,expectedManifestId:'a'.repeat(64)},context:{contextId:'c',knownAt:'2026-01-03T17:00:00Z'},asOf:'2026-01-03T16:00:00Z',snapshotId:'s',engineVersion:'risk_job_v1',scope,
 excludedPositions:[],columns:[{id:'x',factor:{id:'f',label:'Gas futures · January 2026',unit:'USD / energy'},state:'Final'}],
 coverage:{available:false,historyAvailable:false,portfolioComplete:true,factors:1,sessions:2,samples:1,rejected:0,minimumSamples:2,unavailableReason:'insufficient_common_history'}};
}
test('history graphics preserve exact backend changes and native units even with insufficient calibration history',()=>{
 const r=report();validateFactorHistory(r);const chart=factorHistoryChart(r,'x');
 assert.equal(chart.unit,'USD / energy');assert.equal(chart.series[0].points[0].y,'-3');
 assert.equal(chart.series[0].points[0].x,r.manifest.samples[0].to.at);assert.equal(r.coverage.available,false);
});
test('missing or duplicated intervals, columns and endpoint coverage refuse display',()=>{
 for(const edit of [r=>r.manifest.samples=[],r=>r.manifest.samples.push(structuredClone(r.manifest.samples[0])),r=>r.columns=[],r=>r.manifest.samples[0].to.marks=[]]){
  const r=report();edit(r);assert.throws(()=>validateFactorHistory(r),/coverage|consistent/i);
 }
});
test('rejected sessions remain explicit, and another context cannot populate this result',()=>{
 const r=report(),sample=r.manifest.samples.pop();r.manifest.rejected=[{id:sample.id,from:sample.from,to:{...sample.to,marks:[null]},issues:[{factorId:'x',endpoint:'to',reason:'missing_mark'}]}];
 r.coverage.samples=0;r.coverage.rejected=1;validateFactorHistory(r);
 assert.equal(factorHistoryChart(r,'x').series[0].points.length,0);
 r.manifest.forecast.contextId='another';assert.throws(()=>validateFactorHistory(r),/context|consistent/i);
});
test('a self-consistent response for another submitted trade or snapshot is refused',()=>{
 const expected=structuredClone(report().request),wrongTrade=report();wrongTrade.scope=structuredClone(scope);wrongTrade.request.scope=structuredClone(scope);
 wrongTrade.scope.tradeIds=['other'];wrongTrade.request.scope.tradeIds=['other'];wrongTrade.selection=structuredClone(wrongTrade.scope);
 assert.throws(()=>validateFactorHistory(wrongTrade,expected),/scope|trade|consistent/i);
 const wrongClose=report();wrongClose.snapshotId='other';wrongClose.selection.context.snapshotId='other';wrongClose.request.scope={...structuredClone(scope),context:{...scope.context,snapshotId:'other'}};
 assert.throws(()=>validateFactorHistory(wrongClose,expected),/scope|snapshot|consistent/i);
});
