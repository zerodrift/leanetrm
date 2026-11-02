import test from 'node:test';
import assert from 'node:assert/strict';
import {sideOffers,sideStructure} from '../dist/swap-side-composer.js';
const structures=[{type:'fixed_floating',sides:['fixed','floating']},{type:'floating_floating',sides:['floating','floating']}];
test('Add side offers only partial compositions admitted by the backend catalog',()=>{
 assert.deepEqual(sideOffers(structures,[]),['fixed','floating']);
 assert.deepEqual(sideOffers(structures,['fixed']),['floating']);
 assert.deepEqual(sideOffers(structures,['floating']),['fixed','floating']);
 assert.deepEqual(sideOffers(structures,['fixed','floating']),[]);
 assert.deepEqual(sideOffers(structures,['floating','floating']),[]);
});
test('a complete pair selects its explicit native structure and an incomplete pair selects none',()=>{
 assert.equal(sideStructure(structures,[])?.type,undefined);
 assert.equal(sideStructure(structures,['floating'])?.type,undefined);
 assert.equal(sideStructure(structures,['floating','fixed'])?.type,'fixed_floating');
 assert.equal(sideStructure(structures,['floating','floating'])?.type,'floating_floating');
 assert.equal(sideStructure(structures,['fixed','fixed'])?.type,undefined);
 assert.equal(sideStructure(structures,['floating','floating','floating'])?.type,undefined);
 assert.deepEqual(sideOffers([],[]),[]);
});
