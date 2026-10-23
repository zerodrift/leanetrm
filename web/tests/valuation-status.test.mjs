import test from 'node:test';
import assert from 'node:assert/strict';
import {valuationReason,valuationNote,excludedPositions,valuationMeasure} from '../dist/valuation-status.js';

test('each position-specific status reads in plain words',()=>{
 assert.equal(valuationReason('source_expired'),'Contract expired · awaiting settlement');
 assert.equal(valuationReason('missing_observation'),'Missing price at this close');
});
test('a status the Blotter does not define is refused, never shown as valued or as another reason',()=>{
 for(const code of ['index_fixing_missing','','toString','__proto__',undefined,null])
  assert.throws(()=>valuationReason(code),{message:'The Blotter returned an unknown valuation status.'});
});
test('a total says how many positions the close leaves out',()=>{
 assert.equal(valuationNote(0),null);
 assert.equal(valuationNote(1),"Excludes 1 position that can't be valued at this close");
 assert.equal(valuationNote(3),"Excludes 3 positions that can't be valued at this close");
});
test('a count that is not a whole number of positions is refused',()=>{
 for(const count of [-1,1.5,Number.NaN,'2',null,undefined])
  assert.throws(()=>valuationNote(count),{message:'The Blotter returned an invalid count of positions it cannot value.'});
});
test('a group states its own count of positions it leaves out, or is refused',()=>{
 assert.equal(excludedPositions(0),0);assert.equal(excludedPositions(4),4);
 for(const count of [-1,1.5,Number.NaN,'2',null,undefined])
  assert.throws(()=>excludedPositions(count),{message:'The Blotter returned an invalid count of positions it cannot value.'});
});
test('only valuation measures leave unvalued positions out; booked facts count every position',()=>{
 for(const measure of ['presentValue','executionPnl','markMin','markMax','markMean'])assert.equal(valuationMeasure(measure),true,measure);
 for(const measure of ['tradeCount','lots','quantity','executionPriceMin','executionPriceMean','fixedPriceMax','mark',''])assert.equal(valuationMeasure(measure),false,measure);
});
