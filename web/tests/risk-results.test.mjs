import test from 'node:test';
import assert from 'node:assert/strict';
import {reportPresentation,recentCompletedRuns,riskAddress,riskReportAddress} from '../dist/risk-results.js';

const amounts={base:'100',up:'102',down:'98',upPnl:'2',downPnl:'-2',delta:'20/3',gamma:'0'};
const values=(o={})=>({...amounts,display:{...amounts,delta:'6.666666666667'},currency:'USD',currencyReference:{revision:'r1',code:'usd'},deltaUnit:'USD / gas',gammaUnit:'USD / gas²',...o});
const total=(book='A',v=values(),measure='pv')=>({bookId:book,book,measure,measureLabel:measure,values:v});
const factor=(id,totals)=>({factor:{id,label:id,coordinate:'market_quote',kind:'contract',unit:'gas',curves:['gas'],deliveryFrom:'2027-01-01',deliveryTo:'2027-02-01'},bump:'.3',totals,positions:[]});
const report=(factors,o={})=>({context:{contextId:'c1'},snapshotId:'s1',scenario:{id:'base',depth:0,isBase:true},excludedPositions:[],projectionPolicies:[],factors,...o});
const run=reports=>({key:1,queued:false,running:false,retained:false,setupText:'Synthetic scenario',context:{contextId:'c1'},scope:{bookIds:[],portfolioIds:[]},parts:reports.map(report=>({report,unknown:false,unlisted:false}))});

test('report graphics retain exact delta, gamma and P&L with separate units, measures and currencies',()=>{
 const r=run([report([factor('jan',[total(),total('A',values({currency:'EUR',currencyReference:{code:'eur'},deltaUnit:'EUR / gas'})),total('A',values(),'execution_pnl')]),factor('feb',[total('B')])])]);
 const p=reportPresentation(r,'delta');
 assert.equal(p.state,'complete');assert.equal(p.factors.length,2);
 assert.equal(p.charts.length,3,'incompatible measures and currencies have separate axes');
 assert.equal(p.charts[0].spec.series[0].points[0].y,'20/3');
 assert.equal(p.charts[0].spec.series[0].points[0].text,'6.666666666667');
 assert.equal(p.charts[0].spec.unit,'USD / gas');
 assert.equal(reportPresentation(r,'gamma').charts[0].spec.series[0].points[0].y,'0');
 assert.equal(reportPresentation(r,'upPnl').charts[0].spec.unit,'USD');
 assert.equal(reportPresentation(r,'downPnl').charts[0].spec.series[0].points[0].y,'-2');
 assert.equal(p.charts[0].spec.series[0].points[1].y,null,'a missing book total is not zero');
 assert.equal(p.charts[0].spec.series[1].points[1].y,'20/3');
});

test('missing parts and excluded positions remain incomplete coverage beside the results',()=>{
 const excluded=[{tradeId:'t1',bindingId:'b1',bookId:'A',tradeReference:'SYN-1',instrumentType:'future',reason:'source_expired'}];
 const r=run([report([factor('jan',[total()])],{excludedPositions:excluded})]);
 r.parts.push({unlisted:true,unknown:false});
 const p=reportPresentation(r,'delta');
 assert.equal(p.state,'incomplete');assert.deepEqual(p.excluded,excluded);assert.match(p.gaps[0].text,/missing/);
 const failed=reportPresentation({...r,parts:[{failure:'synthetic failure',unknown:false,unlisted:false}]},'delta');
 assert.equal(failed.state,'unavailable');assert.equal(failed.charts.length,0);
 const running=reportPresentation({...r,running:true},'delta');
 assert.equal(running.state,'running');assert.equal(running.charts.length,0);
});

test('reports without exclusion evidence or with conflicting context, snapshot, scenarios or factors refuse display',()=>{
 assert.throws(()=>reportPresentation(run([report([],{excludedPositions:undefined})]),'delta'),/positions.*leaves out|exclusion/i);
 for(const changed of [{context:{contextId:'other'}},{snapshotId:'other'},{scenario:{id:'other',depth:0,isBase:true}}])
  assert.throws(()=>reportPresentation(run([report([factor('jan',[total()])]),report([factor('feb',[total()])],changed)]),'delta'),/context|close|scenario/i);
 assert.throws(()=>reportPresentation(run([report([factor('jan',[total()]),factor('jan',[total()])])]),'delta'),/factor.*twice|factor.*more than once/i);
});

const job=(id,o={})=>({id,state:'completed',asOf:'2026-11-02T00:00:00Z',finishedAt:'2026-11-02T01:00:00Z',context:{contextId:'c1'},scope:{bookIds:['A','B'],portfolioIds:[]},run:null,...o});
test('automatic report selection matches the explicit scope and close and requires all unique completed parts',()=>{
 const jobs=[job('wrong-scope',{scope:{bookIds:['A'],portfolioIds:[]}}),job('wrong-close',{asOf:'2026-11-01T00:00:00Z'}),job('running',{state:'running'}),job('missing',{run:{id:'missing',part:1,parts:2}}),
  job('part1',{run:{id:'complete',part:1,parts:2},finishedAt:'2026-11-02T02:00:00Z'}),job('part2',{run:{id:'complete',part:2,parts:2},scope:{bookIds:['B','A'],portfolioIds:[]}}),job('older')];
 const found=recentCompletedRuns(jobs,{bookIds:['A','B'],portfolioIds:[]},'2026-11-02T00:00:00Z');
 assert.deepEqual(found.map(j=>j.id),['part1','older']);
 assert.deepEqual(recentCompletedRuns([jobs[4],jobs[4]],{bookIds:['A','B'],portfolioIds:[]},jobs[4].asOf),[],'a duplicated part cannot complete a run');
 assert.deepEqual(recentCompletedRuns([jobs[4],{...jobs[5],context:{contextId:'different'}}],{bookIds:['A','B'],portfolioIds:[]},jobs[4].asOf),[]);
});

test('report links accept only exact retained job identities',()=>{
 const id='12345678-1234-4234-8234-123456789012';
 assert.equal(riskAddress('#risk/job/'+id),id);
 for(const value of ['#reference/x','#risk/job/../../x','#risk/job/'+id+'/extra','#risk/job/not-a-job'])assert.equal(riskAddress(value),undefined);
 assert.equal(riskReportAddress(id,{hosted:false,example:'baseline'}),'/#risk/job/'+id);
 assert.equal(riskReportAddress(id,{hosted:false,example:'exchange'}),'/?example=exchange#risk/job/'+id);
 assert.equal(riskReportAddress(id,{hosted:true,example:'exchange'}),'/app?example=exchange#risk/job/'+id);
 assert.equal(riskReportAddress(id,{hosted:true,example:'baseline'}),'/app#risk/job/'+id);
});

test('display windows bound graphics without changing report coverage or treating unrecorded scope as complete',()=>{
 const r=run([report(Array.from({length:130},(_,i)=>factor('f'+i,[total()])))])
 const p=reportPresentation(r,'delta',{offset:64,limit:64});
 assert.equal(p.factors.length,130);assert.equal(p.charts[0].spec.series[0].points.length,64);assert.equal(p.charts[0].spec.series[0].points[0].x,'f64');
 assert.equal(reportPresentation({...r,scope:undefined},'delta').state,'incomplete');
});
