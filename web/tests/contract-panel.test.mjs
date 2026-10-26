import test from 'node:test';
import assert from 'node:assert/strict';
// The chart library reads the browser's user agent when it loads; the pure functions under test never draw.
globalThis.window??={navigator:{userAgent:'node'}};
const {revisionPolicies,historyBasis,historyChart,historyOverlays,knownAtText}=await import('../dist/contract-panel.js');

const at=n=>`2020-12-${String(n).padStart(2,'0')}T22:00:00+00:00`;
const instant=value=>`shown ${value}`;
const read=(policy,cutoff,basis)=>({mode:'exact_visible_close_history_with_selected_instant_v1',revisionPolicy:policy,knowledgeCutoff:cutoff,knowledgeBasis:basis,points:[]});
const catalog={policy:'exact_rational_half_even_18_v1',overlays:[
  {code:'simple_moving_average_v1',label:'Simple moving average',parameters:[{name:'window',label:'Window',minimum:2,maximum:250}],seriesNames:['value']},
  {code:'rolling_range_v1',label:'Rolling range',parameters:[{name:'window',label:'Window',minimum:2,maximum:250}],seriesNames:['high','low']}]};

test('the revision switch offers exact closes first, then as known at the cutoff, by the policies the read takes',()=>{
  assert.deepEqual(revisionPolicies.map(p=>[p.policy,p.label]),[['exact_snapshot_pins_v1','Exact closes'],['as_known_at_cutoff_v1','As known at cutoff']]);
});

test('under the cutoff policy the panel states the cutoff instant the read used',()=>{
  assert.equal(historyBasis('as_known_at_cutoff_v1',read('as_known_at_cutoff_v1',at(17),'selected_context'),instant),`Latest revisions known at ${instant(at(17))}`);
});

test('under the cutoff policy in working data the panel states that the read used current knowledge',()=>{
  assert.equal(historyBasis('as_known_at_cutoff_v1',read('as_known_at_cutoff_v1',null,'current_knowledge'),instant),'Latest revisions known now · current knowledge in working data');
});

test('under exact closes the panel says each close keeps the prices it was saved with',()=>{
  assert.equal(historyBasis('exact_snapshot_pins_v1',read('exact_snapshot_pins_v1',null,'current_knowledge'),instant),'Each close with the prices it was saved with');
});

test('a read under another policy than the one asked for, or with a basis its cutoff contradicts, is refused',()=>{
  assert.throws(()=>historyBasis('exact_snapshot_pins_v1',read('as_known_at_cutoff_v1',null,'current_knowledge'),instant),/revision policy/);
  assert.throws(()=>historyBasis('as_known_at_cutoff_v1',read('as_known_at_cutoff_v1',null,'selected_context'),instant),/knowledge/);
  assert.throws(()=>historyBasis('as_known_at_cutoff_v1',read('as_known_at_cutoff_v1',at(17),'current_knowledge'),instant),/knowledge/);
  assert.throws(()=>historyBasis('as_known_at_cutoff_v1',read('as_known_at_cutoff_v1',null,undefined),instant),/knowledge/);
});

test('the chart draws exactly the series the backend states, one bar per stated point, gaps included',()=>{
  const chart={points:[{at:at(15),value:'3.1',unavailableReason:null},{at:at(16),value:null,unavailableReason:'missing_observation'},{at:at(17),value:'3.2',unavailableReason:null}]};
  assert.deepEqual(historyChart({chart}),{points:[{at:at(15),value:'3.1'},{at:at(16),value:null},{at:at(17),value:'3.2'}]});
});

test('a conflict the backend states is shown as its message, with no chart drawn',()=>{
  const message='Several closes recorded different prices at 2020-12-17T22:00:00+00:00.';
  assert.deepEqual(historyChart({chart:{conflict:{at:at(17),message}}}),{conflict:message});
});

test('a read with no chart, or with both a chart and a conflict, is refused',()=>{
  assert.throws(()=>historyChart({}),/chart/);
  assert.throws(()=>historyChart({chart:{points:[],conflict:{at:at(17),message:'x'}}}),/chart/);
  assert.throws(()=>historyChart({chart:{conflict:{at:at(17)}}}),/chart/);
});

test('overlays are drawn with the catalog label for each definition the read was asked for, in the order asked',()=>{
  const requests=[{code:'rolling_range_v1',parameters:{window:3}},{code:'simple_moving_average_v1',parameters:{window:2}}];
  const series={value:[{at:at(17),value:'3.15',reason:null}]},range={high:[{at:at(17),value:'3.2',reason:null}],low:[{at:at(17),value:'3.1',reason:null}]};
  const response=[{code:'rolling_range_v1',parameters:{window:3},policy:catalog.policy,series:range},{code:'simple_moving_average_v1',parameters:{window:2},policy:catalog.policy,series}];
  assert.deepEqual(historyOverlays(requests,response,catalog),[
    {code:'rolling_range_v1',label:'Rolling range',parameters:{window:3},policy:catalog.policy,series:range},
    {code:'simple_moving_average_v1',label:'Simple moving average',parameters:{window:2},policy:catalog.policy,series}]);
  assert.deepEqual(historyOverlays([],undefined,catalog),[]);
});

test('over a conflicting series the read returns no overlays, and any it returns are refused',()=>{
  const sma={code:'simple_moving_average_v1',parameters:{window:3}};
  assert.deepEqual(historyOverlays([sma],undefined,catalog,true),[]);
  assert.throws(()=>historyOverlays([sma],[{...sma,policy:catalog.policy,series:{value:[]}}],catalog,true),/overlays/);
});

test('overlays that do not answer the request, or that the catalog does not define, are refused',()=>{
  const sma={code:'simple_moving_average_v1',parameters:{window:3}},answer={...sma,policy:catalog.policy,series:{value:[]}};
  assert.throws(()=>historyOverlays([sma],undefined,catalog),/overlays/);
  assert.throws(()=>historyOverlays([sma],[{...answer,parameters:{window:4}}],catalog),/overlays/);
  assert.throws(()=>historyOverlays([sma],[answer,answer],catalog),/overlays/);
  assert.throws(()=>historyOverlays([sma],[{...answer,policy:'another_policy'}],catalog),/overlays/);
  assert.throws(()=>historyOverlays([sma],[{...answer,series:{other:[]}}],catalog),/overlays/);
  assert.throws(()=>historyOverlays([],[answer],catalog),/overlays/);
  const unknown={code:'unknown_v1',parameters:{window:3}};
  assert.throws(()=>historyOverlays([unknown],[{...unknown,policy:catalog.policy,series:{value:[]}}],catalog),/catalog/);
});

test('each value states when it was known: its instant, not certified yet, or nothing for a close with no price saved',()=>{
  assert.equal(knownAtText({at:at(17),value:'3.2',snapshotId:'c',observationIds:['o'],knownAt:at(18)},instant),instant(at(18)));
  assert.equal(knownAtText({at:at(17),value:'3.2',snapshotId:'c',observationIds:['o'],knownAt:null},instant),'Not certified yet');
  assert.equal(knownAtText({at:at(17),value:null,snapshotId:'c',observationIds:[],knownAt:null,unavailableReason:'missing_observation'},instant),'—');
});
