import test from 'node:test';
import assert from 'node:assert/strict';
import {ppaSideName,ppaMeasureLabel,ppaRoundingLabel,ppaPendingActualsLabel,quantityChoices} from '../dist/ppa-side-label.js';
test('a PPA side is named by its terms and numbered from one',()=>{
 assert.equal(ppaSideName('strike_payment',0),'Fixed price 1');assert.equal(ppaSideName('energy_delivery',1),'Power 2');
});
test('break-even measures and rounding are named, and an unknown one is refused rather than shown as its code',()=>{
 assert.equal(ppaMeasureLabel('ValueExcludingOverdue'),'Value excluding overdue cash');assert.equal(ppaMeasureLabel('CompletePv'),'Complete PV');
 assert.equal(ppaRoundingLabel('half_even_v1'),'Round half to even');
 assert.throws(()=>ppaMeasureLabel('Other'),/measure/);assert.throws(()=>ppaRoundingLabel('up'),/rounding/);
});
test('a quantity specification is offered by its unit symbol',()=>{
 const units=[{code:'megawatt',symbol:'MW'},{code:'megawatt_hour',symbol:'MWh'}];
 assert.deepEqual(quantityChoices([{code:'power_mw',unit_code:'megawatt'},{code:'power_mwh',unit_code:'megawatt_hour'}],units),[{value:'power_mw',label:'MW'},{value:'power_mwh',label:'MWh'}]);
 assert.throws(()=>quantityChoices([{code:'unknown',unit_code:'none'}],units),/unit/);
});
test('pending-actuals policies read by what they do, and an unknown policy is refused',()=>{
 assert.equal(ppaPendingActualsLabel('require_actuals_v1'),'Require delivered actuals');
 assert.equal(ppaPendingActualsLabel('estimate_until_actual_v1'),'Estimate until actuals arrive');
 assert.throws(()=>ppaPendingActualsLabel('guess_v1'),/pending-actuals policy/);
});
