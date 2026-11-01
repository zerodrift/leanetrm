import test from 'node:test';
import assert from 'node:assert/strict';
import {exactNumber} from '../dist/exact-number.js';

test('exact text is a plain decimal or a reduced ratio over a positive denominator, read into its parts',()=>{
 assert.deepEqual(exactNumber('-1234.50'),{sign:'-',integer:'1234',fraction:'50',denominator:null});
 assert.deepEqual(exactNumber('-10/3'),{sign:'-',integer:'10',fraction:null,denominator:'3'});
 assert.deepEqual(exactNumber('0'),{sign:'',integer:'0',fraction:null,denominator:null});
 for(const text of ['','+1','1.','.5','1e3','1/0','1.5/2','1,000',' 1','NaN'])assert.equal(exactNumber(text),null,text);
});
