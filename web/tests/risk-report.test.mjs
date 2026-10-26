import test from 'node:test';
import assert from 'node:assert/strict';
import {riskWorkDetails} from '../dist/risk-report.js';

test('connected graph receipts stay separate from materialization and from each other',()=>{
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
