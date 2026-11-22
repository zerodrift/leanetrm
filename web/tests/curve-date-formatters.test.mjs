import test from 'node:test';
import assert from 'node:assert/strict';
import {displayDate,deliveryMonth,deliveryLabel} from '../dist/market-view.js';

test('large curve label sets reuse date formatters without changing contractual date text',t=>{
  const formatters=t.mock.method(Intl,'DateTimeFormat');
  for(let i=0;i<250;i++){
    assert.equal(displayDate('2026-01-16'),'Jan 16, 2026');
    assert.equal(deliveryMonth('2026-02-01'),'Feb 2026');
    assert.equal(deliveryLabel('2026-01-16','2026-01-17'),'Jan 16, 2026');
    assert.equal(deliveryLabel('2026-02-01','2026-03-01'),'Feb 2026');
    assert.equal(deliveryLabel('2026-01-16','2026-02-01'),'Jan 16, 2026 – Jan 31, 2026');
  }
  const count=formatters.mock.callCount();
  assert.ok(count<=2,`Labelling a curve built ${count} formatters for two date formats`);
});
