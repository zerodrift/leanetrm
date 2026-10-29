import test from 'node:test';
import assert from 'node:assert/strict';
import {readSettlementChoice} from '../dist/ppa-components.js';

test('a period price form requires an explicit settlement choice and retains an override without inventing basis',()=>{
 assert.throws(()=>readSettlementChoice('period','',''),/settlement/i);
 assert.deepEqual(readSettlementChoice('period','curve_target',''),{mode:'curve_target',index:null,projectionPolicy:null});
 assert.throws(()=>readSettlementChoice('period','override',''),/index/i);
 assert.deepEqual(readSettlementChoice('period','override','synthetic_index'),{mode:'override',index:'synthetic_index',projectionPolicy:'forward_curve_no_basis_v1'});
});
test('native interval price forms send an explicit null settlement field',()=>{
 assert.equal(readSettlementChoice('interval','',''),null);
 assert.equal(readSettlementChoice('shaped','',''),null);
 assert.throws(()=>readSettlementChoice('unknown','',''),/curve/i);
});
