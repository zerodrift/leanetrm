import test from 'node:test';
import assert from 'node:assert/strict';

test('projection selection requires an explicit offered choice and transports its exact published reference',async()=>{
 const {projectionReference}=await import('../dist/swap-projections.js');
 const reference={catalogId:'catalog',curveId:'curve',version:7,outputNode:'output',position:3,selection:'single_output_position_v1',implementation:'published_custom_curve_projection_v1'};
 const choices=[{label:'Synthetic published output',reference,samplings:[{resetFrequency:'monthly',averagingMethod:'none'}]}];
 assert.throws(()=>projectionReference('',choices),/Choose/);
 assert.throws(()=>projectionReference(JSON.stringify({...reference,version:8}),choices),/unavailable/);
 assert.equal(projectionReference('ordinary',choices),null);
 const selected=projectionReference(JSON.stringify(reference),choices);
 assert.deepEqual(selected,reference);assert.notEqual(selected,reference);
 selected.version=99;assert.equal(reference.version,7);
});

test('a kept projection is labelled by its curve name, never by its key',async()=>{
 const {projectionKeyLabel}=await import('../dist/swap-projections.js');
 const key=JSON.stringify(['fe2039b9-a57c-5d18-b65e-64bab5061e43','houston_ship_channel_forward','houston_ship_channel_iferc_bidweek']);
 const names={houston_ship_channel_forward:'Houston Ship Channel delivery-month forwards'};
 assert.equal(projectionKeyLabel(key,curve=>names[curve]),'Houston Ship Channel delivery-month forwards');
 for(const bad of ['','not json','[]',JSON.stringify(['revision',7,'index'])])
  assert.throws(()=>projectionKeyLabel(bad,curve=>curve),{message:'The kept projection has no curve to name.'});
});
