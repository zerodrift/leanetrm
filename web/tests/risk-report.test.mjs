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

test('connected graph receipts stay separate from materialization and from each other',async()=>{
  const {riskWorkDetails}=await import('../dist/risk-report.js');
  const report={heldInstruments:4,work:{positionCount:7,uniqueValuations:4,scenarioCount:3,preparationScenarioCount:1,unitPricerCalls:9,rebuiltCurveNodes:8,reusedCurveNodes:2,repeatedInstrumentReferences:3,quantityApplications:21},connectedScenarios:[
    {definitions:{revisionId:'market-a'},work:{evaluatedNodes:31,reusedReads:19,affectedNodes:11,traversedAffectedEdges:23},preparation:{sourceCompilations:2,declaredSourceNodes:17}},
    {definitions:{revisionId:'market-b'},work:{evaluatedNodes:5,reusedReads:4,affectedNodes:1,traversedAffectedEdges:3},preparation:{sourceCompilations:1,declaredSourceNodes:5}}
  ]};
  const lines=riskWorkDetails(report);
  assert.equal(lines.length,3,'One materialization line plus one unchanged receipt per backend market');
  assert.match(lines[0],/Materialization.*8.*2/);
  assert.match(lines[1],/market-a.*31.*19.*11.*23.*2.*17/);
  assert.match(lines[2],/market-b.*5.*4.*1.*3.*1.*5/);
  assert.ok(lines.every(line=>!line.includes('all scenario preparation')));
});
