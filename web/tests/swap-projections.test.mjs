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
