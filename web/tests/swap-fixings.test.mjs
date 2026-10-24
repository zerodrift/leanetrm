import test from 'node:test';
import assert from 'node:assert/strict';
import {parseFixingBoundaries} from '../dist/swap-fixings.js';

test('fixing entry retains literal observation intervals and explicit UTC boundaries',()=>{
 assert.deepEqual(parseFixingBoundaries('2021-01-01, 2021-01-02, 2020-12-17T12:00:00Z\n2021-01-02,2021-01-03,2020-12-31T12:00:00Z'),[
  {from:'2021-01-01',to:'2021-01-02',fixingAt:'2020-12-17T12:00:00Z'},
  {from:'2021-01-02',to:'2021-01-03',fixingAt:'2020-12-31T12:00:00Z'}]);
});
test('fixing entry never invents an omitted boundary or ignores extra fields',()=>{
 for(const text of ['', '2021-01-01,2021-01-02,','2021-01-01,2021-01-02,2020-12-17T12:00:00Z,extra'])assert.throws(()=>parseFixingBoundaries(text));
});
