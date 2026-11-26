import test from 'node:test';
import assert from 'node:assert/strict';
import {requestDeadlines} from '../dist/request-deadline.js';

test('business requests require server deadlines; startup waits out the longest ordinary budget the server can serve',()=>{
 // The API's ordinary ceiling of 29 s plus its largest response grace of 5 s; HostedConfigurationChecks holds the
 // page's startup guard to the API's actual ceilings.
 const deadlines=requestDeadlines(),longestOrdinary=34000;
 assert.ok(deadlines.milliseconds('bootstrap?includeFrames=false')>=longestOrdinary);
 assert.equal(deadlines.milliseconds('examples'),deadlines.milliseconds('bootstrap'));
 assert.throws(()=>deadlines.milliseconds('trading/blotter'),/deadline metadata/);
});
test('all windows use the server ordinary and extended request budgets',()=>{
 const deadlines=requestDeadlines();deadlines.read({normalMilliseconds:7000,extendedMilliseconds:11000});
 assert.equal(deadlines.milliseconds('workspace/navigation'),7000);
 assert.equal(deadlines.milliseconds('ppa/preview'),11000);
 assert.equal(deadlines.milliseconds('trading/position'),11000);
 assert.equal(deadlines.milliseconds('ppa-other'),7000);
 for(const value of [null,{}, {normalMilliseconds:0,extendedMilliseconds:1},
  {normalMilliseconds:3,extendedMilliseconds:2},{normalMilliseconds:1.1,extendedMilliseconds:2}])
  assert.throws(()=>deadlines.read(value),/deadline metadata/);
});
test('the server alone bounds its budgets, so a longer hosted selection is used as served',()=>{
 const deadlines=requestDeadlines();deadlines.read({normalMilliseconds:34000,extendedMilliseconds:40000});
 assert.equal(deadlines.milliseconds('portfolio/risk/history'),34000);
 assert.equal(deadlines.milliseconds('trading/position'),40000);
});
