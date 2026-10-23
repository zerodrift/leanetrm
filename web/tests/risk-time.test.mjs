import test from 'node:test';
import assert from 'node:assert/strict';
import {riskContextLabel} from '../dist/risk-time.js';
test('retained risk timestamps use the user zone and preserve business dates',()=>{
 const label=riskContextLabel({asOf:'2026-10-24T01:00:00Z',context:{effectiveOn:'2026-10-24',knownAt:'2026-10-24T02:00:00Z'}},'America/Chicago');
 assert.match(label,/Oct 23, 2026, 8:00 PM/);assert.match(label,/Oct 23, 2026, 9:00 PM/);assert.match(label,/effective date: 2026-10-24/);assert.match(label,/America\/Chicago/);
});
test('repeated local risk times retain distinguishable offsets',()=>{
 const label=riskContextLabel({asOf:'2026-11-01T06:30:00Z',context:{effectiveOn:'2026-11-01',knownAt:'2026-11-01T07:30:00Z'}},'America/Chicago');
 assert.match(label,/GMT-5/);assert.match(label,/GMT-6/);
});
