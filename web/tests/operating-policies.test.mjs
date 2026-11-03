import test from 'node:test';
import assert from 'node:assert/strict';
import {policyValues} from '../dist/operating-policies.js';
test('policy entry follows the server field contract and submits every explicit value',()=>{
 const fields=[{key:'capacity',label:'Capacity',min:2,max:7},{key:'reserve',label:'Reserve',min:1,max:3}];
 assert.deepEqual(policyValues(fields,{capacity:'6',reserve:'2'}),{capacity:6,reserve:2});
 for(const raw of [{capacity:'',reserve:'2'},{capacity:'8',reserve:'2'},{capacity:'2.5',reserve:'2'},{capacity:'2'}])assert.throws(()=>policyValues(fields,raw),/Supply/);
});
