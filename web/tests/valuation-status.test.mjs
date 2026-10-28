import test from 'node:test';
import assert from 'node:assert/strict';
import {valuationReason,valuationNote,excludedPositions,valuationMeasure} from '../dist/valuation-status.js';

test('each position-specific status reads in plain words for the instrument it concerns',()=>{
 assert.equal(valuationReason('source_expired','future'),'Contract expired · awaiting settlement');
 assert.equal(valuationReason('source_expired','financial_swap'),'Past marking cutoff');
 assert.equal(valuationReason('source_expired','heat_rate'),'Past marking cutoff');
 for(const instrumentType of ['future','financial_swap','heat_rate'])assert.equal(valuationReason('missing_observation',instrumentType),'Missing price at this close');
});
test('a PPA booked under other reference or definition versions than the close reads in plain words, and only for a PPA',()=>{
 assert.equal(valuationReason('other_definition_versions','ppa'),'Booked under other reference data versions');
 for(const instrumentType of ['future','financial_swap','heat_rate'])
  assert.throws(()=>valuationReason('other_definition_versions',instrumentType),{message:'The Blotter returned an unknown valuation status.'});
 assert.throws(()=>valuationReason('source_expired','ppa'),{message:'The Blotter returned an unknown valuation status.'});
});
test('a PPA whose delivery the close cannot price reads in plain words, and only for a PPA',()=>{
 assert.equal(valuationReason('unpriced_delivery','ppa'),'No price for part of its delivery at this close');
 for(const instrumentType of ['future','financial_swap','heat_rate'])
  assert.throws(()=>valuationReason('unpriced_delivery',instrumentType),{message:'The Blotter returned an unknown valuation status.'});
});
test('a status or instrument the Blotter does not define is refused, never shown as valued or as another reason',()=>{
 for(const code of ['index_fixing_missing','','toString','__proto__',undefined,null])
  assert.throws(()=>valuationReason(code,'future'),{message:'The Blotter returned an unknown valuation status.'});
 for(const instrumentType of ['option','','toString',undefined])
  assert.throws(()=>valuationReason('source_expired',instrumentType),{message:'The Blotter returned an unknown instrument type.'});
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
