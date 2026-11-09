import test from 'node:test';
import assert from 'node:assert/strict';
import {nativeCurveConfiguration} from '../dist/native-curve-inspector.js';
test('native inspection follows exact saved members and preserves source identities and absent terms',()=>{
 const workspace={definitions:{curves:[{code:'gas',rule:'add',direct_source_code:null},{code:'other'}],
  curve_members:[{curve_code:'other',contract_id:'ignored',ordinal:0},{curve_code:'gas',contract_id:'march',ordinal:1},{curve_code:'gas',contract_id:'feb',ordinal:0}],
  contracts:[{id:'feb',delivery_from:'2021-02-01'},{id:'march',delivery_from:'2021-03-01'}],
  curve_inputs:[{curve_code:'gas',source_curve_code:'future',slot:'base'},{curve_code:'other',source_curve_code:'elsewhere'}]}};
 const before=JSON.stringify(workspace),shown=nativeCurveConfiguration(workspace,'gas');
 assert.deepEqual(shown.contracts.map(c=>c.id),['feb','march']);assert.equal(shown.definition.direct_source_code,null);
 assert.deepEqual(shown.inputs.map(i=>i.source_curve_code),['future']);assert.equal(JSON.stringify(workspace),before);
 workspace.definitions.contracts.pop();assert.throws(()=>nativeCurveConfiguration(workspace,'gas'),/missing or inconsistent/);
});
