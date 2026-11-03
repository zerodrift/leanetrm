import test from 'node:test';
import assert from 'node:assert/strict';
import {reportPresentation} from '../dist/risk-results.js';

const identity={tradeId:'t1',bindingId:'b1',bookId:'A',book:'Book A',tradeReference:'TR-1',instrumentReference:'I-1',instrumentType:'future',measure:'pv',measureLabel:'Present value'};
const currencyReference={code:'usd',revision:'r1'};
const values={base:'100',value:'302/3',pnl:'2/3',loss:'-2/3',currency:'USD',currencyReference,display:{base:'100',value:'100.666666666667',pnl:'0.666666666667',loss:'-0.666666666667'}};
const sample={id:'up',label:'Joint upward stress',shifts:[],totals:[{...identity,values}],positions:[{...identity,values}]};
const joint={kind:'scenarios',request:{convention:'portfolio_joint_risk_v1',scenarios:{id:'named',version:'1',convention:'independent_joint_absolute_curve_scenarios_v1',scenarios:[{id:'up',label:'Joint upward stress',shifts:[{}]}]},derivatives:null},
 baseTotals:[{...identity,value:'100',display:'100',currency:'USD',currencyReference}],axes:[],samples:[sample],derivatives:[],partitions:[{definitions:{revisionId:'m1'},applicableAxes:[],work:{scenarioCount:2},samples:[{id:'up',unchanged:false,scenario:{id:'child'}}]}]};
const report=(extra={})=>({detail:'Holdings',context:{contextId:'c1'},snapshotId:'s1',scenario:{id:'base',depth:0,isBase:true},excludedPositions:[],projectionPolicies:[],inputs:{positions:[identity],markets:[{definitions:{revisionId:'m1'}}]},joint:structuredClone(joint),...extra});
const run=(...reports)=>({key:1,queued:false,running:false,retained:false,setupText:'Named stresses',context:{contextId:'c1'},scope:{bookIds:['A'],portfolioIds:[]},parts:reports.map(report=>({report,unknown:false,unlisted:false}))});

test('joint graphics preserve backend exact loss and display strings without aggregating scenarios',()=>{
 const view=reportPresentation(run(report()),'delta');
 assert.equal(view.joint.kind,'scenarios');assert.equal(view.state,'complete');assert.equal(view.charts.length,1);
 assert.equal(view.charts[0].spec.unit,'USD');
 assert.equal(view.charts[0].spec.series[0].points[0].y,'-2/3');
 assert.equal(view.charts[0].spec.series[0].points[0].text,'-0.666666666667');
});
test('joint reports cannot be combined as sensitivity parts or lose an expected sample/binding',()=>{
 assert.throws(()=>reportPresentation(run(report(),report({joint:undefined})),'delta'),/joint|one complete/i);
 const missing=report();missing.joint.samples=[];
 assert.throws(()=>reportPresentation(run(missing),'delta'),/sample|scenario|coverage/i);
 const position=report();position.joint.samples[0].positions=[];
 assert.throws(()=>reportPresentation(run(position),'delta'),/binding|position|coverage/i);
});
test('a derivative chart displays backend step agreement and keeps native derivative units',()=>{
 const r=report();r.joint.kind='derivatives';r.joint.request.scenarios=null;r.joint.request.derivatives={convention:'exact_central_half_nominal_double_v1',axes:[{id:'a'}],mixed:[]};r.joint.samples=[];
 r.joint.partitions=[{definitions:{revisionId:'m1'},applicableAxes:['a'],work:{scenarioCount:7},samples:Array.from({length:6},(_,i)=>({id:'stencil/'+i,unchanged:false,scenario:{id:'child/'+i}}))}];
 r.joint.axes=[{id:'a',factor:{label:'Synthetic factor',unit:'USD / unit'}}];
 const estimate={half:'1',nominal:'2',double:'3',stable:false,currency:'USD',currencyReference,unit:'USD / (USD / unit)',tolerance:{absolute:'0',relative:'0'},display:{half:'1',nominal:'2',double:'3'}};
 r.joint.derivatives=['central_first_difference_v1','central_diagonal_second_difference_v1'].map(formula=>({id:'a',formula,axes:['a'],factorUnits:[],totals:[{...identity,values:estimate}],positions:[{...identity,values:estimate}]}));
 const p=reportPresentation(run(r),'delta');assert.equal(p.joint.derivatives[0].totals[0].values.stable,false);
 assert.equal(p.charts[0].spec.series[0].points[0].y,'2');assert.equal(p.charts[0].spec.unit,estimate.unit);
});

test('joint reports refuse incomplete or duplicate retained partition sample receipts',()=>{
 const missing=report();missing.joint.partitions[0].work.scenarioCount=3;
 assert.throws(()=>reportPresentation(run(missing),'delta'),/coverage|scenario/i);
 const duplicate=report();duplicate.joint.partitions[0].samples.push(structuredClone(duplicate.joint.partitions[0].samples[0]));duplicate.joint.partitions[0].work.scenarioCount=3;
 assert.throws(()=>reportPresentation(run(duplicate),'delta'),/coverage|scenario/i);
 const old=report({factors:[]});assert.throws(()=>reportPresentation(run(old),'delta'),/coverage|scenario/i);
});
