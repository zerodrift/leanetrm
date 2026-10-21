import test from 'node:test';
import assert from 'node:assert/strict';
import {riskTotalGroups} from '../dist/risk-report.js';

const amounts={base:'1/3',up:'103/300',down:'103/300',upPnl:'1/100',downPnl:'1/100',delta:'0',gamma:'2'};
const display={base:'0.333333',up:'0.343333',down:'0.343333',upPnl:'0.010000',downPnl:'0.010000',delta:'0.000000',gamma:'2.000000'};
const row={bookId:'synthetic-book',book:'Synthetic book',measure:'futures_execution_pnl',measureLabel:'Futures execution P&L',values:{...amounts,display,currency:'TST',currencyReference:{code:'test',revision:'first'},deltaUnit:'TST / quote',gammaUnit:'TST / quote²'}};

test('different valuation measures and exact currency versions stay separate without combining returned amounts',()=>{
  const swap={...row,measure:'swap_present_value',measureLabel:'Swap PV'};
  const anotherCurrency={...row,values:{...row.values,currencyReference:{code:'test',revision:'second'}}};
  const groups=riskTotalGroups([row,swap,anotherCurrency]);
  assert.deepEqual(groups,[[row],[swap],[anotherCurrency]]);
});

test('a nonlinear returned result retains exact fractions and independently supplied display values',()=>{
  const groups=riskTotalGroups([row,{...row,bookId:'another-book',book:'Another book'}]);
  assert.equal(groups.length,1);
  assert.equal(groups[0][0].values.base,'1/3');
  assert.deepEqual(groups[0][0].values.display,display);
  assert.equal(groups[0][0].values.delta,'0');
  assert.equal(groups[0][0].values.gamma,'2');
});

test('different derivative units never share a table or chart partition',()=>{
  const other={...row,values:{...row.values,deltaUnit:'TST / other quote',gammaUnit:'TST / other quote²'}};
  assert.deepEqual(riskTotalGroups([row,other]),[[row],[other]]);
});
