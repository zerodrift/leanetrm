import test from 'node:test';
import assert from 'node:assert/strict';

test('settlement authoring requires an explicit mode, offered override and explicit unmodelled-basis policy',async()=>{
 const {readPriceSettlement}=await import('../dist/price-settlement.js');
 const indices=[{code:'synthetic_other',name:'Synthetic other index'}];
 assert.throws(()=>readPriceSettlement('','','',indices),/Choose/);
 assert.deepEqual(readPriceSettlement('curve_target','','',indices),{mode:'curve_target',index:null,projectionPolicy:null});
 assert.throws(()=>readPriceSettlement('override','synthetic_other','',indices),/policy/);
 assert.throws(()=>readPriceSettlement('override','unoffered','forward_curve_no_basis_v1',indices),/index/);
 assert.deepEqual(readPriceSettlement('override','synthetic_other','forward_curve_no_basis_v1',indices),{mode:'override',index:'synthetic_other',projectionPolicy:'forward_curve_no_basis_v1'});
 assert.throws(()=>readPriceSettlement('curve_target','synthetic_other','',indices),/conflict/);
});
