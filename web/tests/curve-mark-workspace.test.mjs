import test from 'node:test';
import assert from 'node:assert/strict';
import {cellPresentation,moveCell,markFence,currentMarkFence,requireDraft,requireReceipt,requireStatus} from '../dist/curve-mark-model.js';
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

const id=n=>`00000000-0000-4000-8000-${String(n).padStart(12,'0')}`;
const source={definitionRevisionId:id(1),referenceCatalogId:id(2),referenceRevisionId:id(3)};
const batch={policy:'curve_mark_batch_v1',ownerId:'synthetic-owner',operationId:id(4),kind:'volatility',source,
 context:{contextId:id(5),asOf:'2026-01-01T00:00:00.000002Z',businessDate:'2026-01-01'},reason:'Synthetic test',
 changes:[{...key('2026-01'),expectedRevision:0,value:'0.5'}]};
const receiptFor=(command,cells)=>({
 policy:command.policy,ownerId:command.ownerId,operationId:command.operationId,kind:command.kind,source:command.source,
 effectiveAt:command.context.asOf,reason:command.reason,cells,
});
const recorded={key:key('2026-01'),markId:id(6),revision:1,value:'0.5',changed:true,
 headAtPublication:{markId:id(6),revision:1,value:'0.5',effectiveAt:'2026-01-01T00:00:00.000002+00:00'}};
const acknowledged={...recorded,changed:false,
 headAtPublication:{markId:id(7),revision:2,value:'0.6',effectiveAt:'2026-01-01T00:00:00.000003Z'}};
const verifiers=[requireReceipt,(value,command)=>requireStatus({status:'committed',receipt:value},command)];

test('Record and status refuse a changed fact that skips the reviewed predecessor',()=>{
 const wrong={...recorded,revision:7,headAtPublication:{...recorded.headAtPublication,revision:7}};
 for(const verify of verifiers){
  assert.throws(()=>verify(receiptFor(batch,[wrong]),batch));
  assert.deepEqual(verify(receiptFor(batch,[recorded]),batch).cells,[recorded]);
 }
});

test('Record and status keep a valid older acknowledgement and reject a head that predates it by one microsecond',()=>{
 const command={...batch,changes:[{...batch.changes[0],expectedRevision:2}]};
 const wrong={...acknowledged,headAtPublication:{...acknowledged.headAtPublication,effectiveAt:'2026-01-01T00:00:00.000001Z'}};
 for(const verify of verifiers){
  assert.throws(()=>verify(receiptFor(command,[wrong]),command));
  assert.deepEqual(verify(receiptFor(command,[acknowledged]),command).cells,[acknowledged]);
 }
});

test('Publication evidence uses canonical values, supported clocks, and backend key order',()=>{
 for(const invalidHead of [{value:'0.60'},{value:'10.1'},{effectiveAt:'2026-02-30T00:00:00Z'},{effectiveAt:'2026-01-01T00:00:00.0000031Z'}]){
  const invalid={...acknowledged,headAtPublication:{...acknowledged.headAtPublication,...invalidHead}};
  assert.throws(()=>requireReceipt(receiptFor(batch,[invalid]),batch));
 }
 const later={...recorded,key:key('2026-02'),markId:id(8),headAtPublication:{...recorded.headAtPublication,markId:id(8)}};
 const reverseRequest={...batch,changes:[{...batch.changes[0],month:'2026-02'},batch.changes[0]]};
 assert.deepEqual(requireReceipt(receiptFor(reverseRequest,[recorded,later]),reverseRequest).cells,[recorded,later]);
 assert.throws(()=>requireReceipt(receiptFor(reverseRequest,[later,recorded]),reverseRequest));
});
