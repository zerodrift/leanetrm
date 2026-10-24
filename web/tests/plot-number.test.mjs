import test from 'node:test';
import assert from 'node:assert/strict';
import {plotNumber} from '../dist/plot-number.js';

test('exact decimals and rationals become numbers for placing pixels',()=>{
 assert.deepEqual(['0','-0','12.5','-0.125','1000','1/4','-1/3','7/7','0/9'].map(plotNumber).map(n=>Math.round(n*1e6)/1e6||0),[0,0,12.5,-0.125,1000,0.25,-0.333333,1,0]);
});
test('a rational whose terms exceed the range of a number is placed by its ratio',()=>{
 assert.equal(plotNumber(`1${'0'.repeat(400)}/1${'0'.repeat(399)}`),10);
 assert.equal(plotNumber(`-3${'0'.repeat(500)}/6${'0'.repeat(500)}`),-0.5);
});
test('a string that is not an exact decimal or rational, or no number can hold, is refused rather than guessed',()=>{
 for(const bad of ['','1e5','NaN','Infinity','1/0','1/-2','1/','/2','1/2/3','0x10','1,5',' 1','1 ',`1${'0'.repeat(400)}`])assert.throws(()=>plotNumber(bad),/exact/,bad);
});
