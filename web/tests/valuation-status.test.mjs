import test from 'node:test';
import assert from 'node:assert/strict';
import {valuationReason,valuationNote,excludedPositions,valuationMeasure} from '../dist/valuation-status.js';

test('a month-to-date cash-out with a transfer has a product-specific refusal label',()=>{
 assert.equal(valuationReason('gas_imbalance_transfer','physical_gas'),'Month-to-date cash-out cannot include accepted transfers');
 assert.throws(()=>valuationReason('gas_imbalance_transfer','financial_swap'),/unknown valuation status/);
});

test('a future without its exact close source has an explicit status, without widening other product statuses',()=>{
 assert.equal(valuationReason('market_inputs_missing','future'),'Booked market inputs are unavailable at this close');
 for(const instrumentType of ['ppa','financial_swap','heat_rate'])
  assert.throws(()=>valuationReason('market_inputs_missing',instrumentType),{message:'The Blotter returned an unknown valuation status.'});
});

test('each position-specific status reads in plain words for the instrument it concerns',()=>{
 assert.match(valuationReason('exercise_conflict','physical_gas'),/delivery or payment/);
 assert.equal(valuationReason('source_expired','future'),'Contract expired · awaiting settlement');
 assert.equal(valuationReason('source_expired','financial_swap'),'Past marking cutoff');
 assert.equal(valuationReason('source_expired','heat_rate'),'Past marking cutoff');
 for(const instrumentType of ['future','financial_swap','heat_rate'])assert.equal(valuationReason('missing_observation',instrumentType),'Missing price at this close');
});
test('a missing due index fixing is explicit for indexed swap sides, with no status fallback',()=>{
 for(const instrumentType of ['financial_swap','heat_rate'])
  assert.equal(valuationReason('index_fixing_missing',instrumentType),'Missing published index fixing');
 for(const instrumentType of ['future','ppa'])
  assert.throws(()=>valuationReason('index_fixing_missing',instrumentType),{message:'The Blotter returned an unknown valuation status.'});
});
test('a PPA booked under other reference or definition versions than the close reads in plain words, and only for a PPA',()=>{
 assert.equal(valuationReason('other_definition_versions','ppa'),'Booked market inputs are unavailable at this close');
 for(const instrumentType of ['future','financial_swap','heat_rate'])
  assert.throws(()=>valuationReason('other_definition_versions',instrumentType),{message:'The Blotter returned an unknown valuation status.'});
 assert.throws(()=>valuationReason('source_expired','ppa'),{message:'The Blotter returned an unknown valuation status.'});
});
test('a PPA whose delivery the close cannot price reads in plain words, and only for a PPA',()=>{
 assert.equal(valuationReason('unpriced_delivery','ppa'),'No price for part of its delivery at this close');
 for(const instrumentType of ['future','financial_swap','heat_rate'])
  assert.throws(()=>valuationReason('unpriced_delivery',instrumentType),{message:'The Blotter returned an unknown valuation status.'});
});
test('a PPA with an unavailable retained schedule explicitly needs rebooking, without a pricing fallback',()=>{
 assert.equal(valuationReason('needs_rebooking','ppa'),'Needs rebooking');
 for(const instrumentType of ['future','financial_swap','heat_rate'])
  assert.throws(()=>valuationReason('needs_rebooking',instrumentType),{message:'The Blotter returned an unknown valuation status.'});
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
test('an undelivered gas day whose contract expired without its final settlement says so',()=>{
 assert.equal(valuationReason('delivery_mark_final_settlement','physical_gas'),'Missing final settlement of its expired delivery contract');
});

test('Park and loan unavailable values name the native reconciliation or selected market problem',()=>{
 assert.match(valuationReason('gas_park_loan_reconciliation','physical_gas_park_loan'),/Reconciliation required/);
 assert.match(valuationReason('discount_curve','physical_gas_park_loan'),/fee discount/);
 assert.match(valuationReason('missing_observation','physical_gas_park_loan'),/Missing principal mark/);
 assert.throws(()=>valuationReason('gas_park_loan_reconciliation','physical_gas'),/unknown valuation status/);
});

test('a closed balance-time advance window has its own product-specific unavailable label',()=>{
 assert.equal(valuationReason('gas_balance_fee_advance_window','physical_gas_park_loan'),'Advance window closed without accepted performance; remaining plan value unavailable');
 assert.throws(()=>valuationReason('gas_balance_fee_advance_window','physical_gas'),/unknown valuation status/);
});

test('custody unavailable rows state the acceptance or source issue without implying commodity pricing',()=>{
 assert.match(valuationReason('gas_custody_acceptance_window','physical_gas_park_custody'),/acceptance window.*unavailable/i);
 assert.match(valuationReason('gas_custody_reconciliation','physical_gas_park_custody'),/reconciliation.*observation/i);
 assert.match(valuationReason('discount_curve','physical_gas_park_custody'),/fee discount/i);
 assert.throws(()=>valuationReason('missing_observation','physical_gas_park_custody'),/unknown valuation status/);
});
