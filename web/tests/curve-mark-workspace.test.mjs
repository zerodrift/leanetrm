import test from 'node:test';
import assert from 'node:assert/strict';
import {cellPresentation,moveCell,markFence,currentMarkFence,requireDraft} from '../dist/curve-mark-model.js';
import {termOptions,correlationScale} from '../dist/curve-mark-charts.js';

const key=month=>({firstCurve:'synthetic',secondCurve:null,month});
const mark=value=>({id:'12345678-0000-4000-8000-123456789abc',revision:1,value,effectiveAt:'2026-01-01T00:00:00Z',knownAt:'2026-01-02T00:00:00Z'});
const cell=(month,value=null)=>({state:{key:key(month),selected:value===null?null:mark(value),writeHead:null,expectedRevision:0,editBlock:null},enteredValue:null,proposedValue:null,difference:null,isChanged:false,isNewMark:false,error:null});
const zero=cell('2026-01','0');
const missing=cell('2026-02');
const exact=cell('2026-03','0.123456789012');
const invalid={...cell('2026-04','0.4'),enteredValue:'',error:{code:'curve_mark_input',message:'Supply a decimal value.'}};

test('Published zero, missing, unvalidated clearing and backend refusal have distinct cell states',()=>{
 assert.deepEqual(cellPresentation(zero,undefined),{state:'published',value:'0',label:'Published'});
 assert.deepEqual(cellPresentation(missing,undefined),{state:'missing',value:'—',label:'Missing'});
 assert.deepEqual(cellPresentation(zero,''),{state:'unvalidated',value:'',label:'Unvalidated'});
 assert.deepEqual(cellPresentation(invalid,''),{state:'invalid',value:'',label:'Invalid'});
 assert.deepEqual(cellPresentation(invalid,'0'),{state:'unvalidated',value:'0',label:'Unvalidated'});
 assert.deepEqual(cellPresentation(exact,undefined,'0.123456789012'),{state:'pending',value:'0.123456789012',label:'Publication pending'});
 assert.deepEqual(cellPresentation(exact,undefined),{state:'published',value:'0.123456789012',label:'Published'});
});

test('Draft ingress keeps exact server strings and rejects numbers or unrelated keys',()=>{
 const validated={...exact,enteredValue:'0.000000000001',proposedValue:'0.000000000001',difference:'-0.123456789011',isChanged:true};
 const draft={cells:[validated],coverage:{marked:1,missing:0,changed:1,invalid:0}};
 assert.deepEqual(requireDraft(draft,[exact.state.key]),draft);
 assert.throws(()=>requireDraft({...draft,cells:[{...validated,difference:-0.123456789011}]},[exact.state.key]));
 assert.throws(()=>requireDraft(draft,[missing.state.key]));
});

test('Tab traverses visible cells, Enter moves down one month in a strip and skips diagonal cells',()=>{
 const addresses=[{row:0,column:1},{row:0,column:2},{row:1,column:0},{row:1,column:2},{row:2,column:0},{row:2,column:1}];
 assert.deepEqual(moveCell(addresses,addresses[1],'down'),addresses[3]);
 assert.deepEqual(moveCell(addresses,addresses[3],'next'),addresses[4]);
 assert.deepEqual(moveCell(addresses,addresses[4],'previous'),addresses[3]);
 const strip=Array.from({length:240},(_,row)=>({row,column:0}));
 assert.deepEqual(moveCell(strip,strip[238],'down'),strip[239]);
 assert.equal(moveCell(strip,strip[239],'next'),undefined);
});

test('A reply must still match actor, app context, exact source, and editor generation',()=>{
 const args=['actor','context','source',2];
 const token=markFence(...args);
 assert.equal(currentMarkFence(token,markFence(...args)),true);
 for(const [i,replacement] of ['alternate actor','historical context','another source',3].entries()){
  const different=[...args];different[i]=replacement;
  assert.equal(currentMarkFence(token,markFence(...different)),false);
 }
});

test('Charts preserve missing months and separate solid published from strictly validated dashed draft',()=>{
 const approved={...exact,enteredValue:'0.123456789013',proposedValue:'0.123456789013',difference:'0.000000000001',isChanged:true};
 const options=termOptions([zero,missing,approved,invalid],new Map(), 'Synthetic', 'curve_marks_log_return_held_v1','volatility');
 assert.deepEqual(options.legend.data,['Published','Validated draft']);
 assert.deepEqual(options.series.map(s=>[s.lineStyle.type,s.connectNulls]),[['solid',false],['dashed',false]]);
 assert.equal(options.series[0].data[0].value,0);
 assert.equal(options.series[0].data[1],null);
 assert.equal(options.series[1].data[3],null);
 assert.match(options.tooltip.formatter([{data:options.series[1].data[2]}]),/0\.123456789013/);
 const stale=termOptions([approved],new Map([[JSON.stringify(Object.values(approved.state.key)),'0.2']]),'Synthetic','curve_marks_log_return_held_v1','volatility');
 assert.equal(stale.series[1].data[0],null);
 assert.deepEqual(correlationScale,{min:-1,max:1,labels:['−1','0','+1']});
});
