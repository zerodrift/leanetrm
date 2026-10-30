import test from 'node:test';
import assert from 'node:assert/strict';
import {parseReferenceAddress,formatReferenceAddress} from '../dist/reference-address.js';

test('reference addresses round-trip codes and literal searches',()=>{
  for(const a of [{table:'units'},{table:'units',code:'MWh'},{table:'locations',code:'zone.north-1'},{table:'locations',search:'50%_off /x?y#z'},{table:'currencies',code:'a b/c',search:'%_ /?#'}])assert.deepEqual(parseReferenceAddress(formatReferenceAddress(a)),a);
  assert.equal(formatReferenceAddress({table:'units',code:'MWh'}),'#reference/units/MWh');
});

test('malformed or unknown addresses are not addresses',()=>{
  for(const h of ['','#reference','#reference/','#reference//x','#other/units','#reference/not_a_table','#reference/units/','#reference/units/a/b','#reference/units?q=','#reference/units/%E0%A4%A','#reference/units?x=1'])assert.equal(parseReferenceAddress(h),undefined,h);
});
