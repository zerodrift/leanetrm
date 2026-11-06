import test from 'node:test';
import assert from 'node:assert/strict';

import {delivery} from './physical-interval-fixture.mjs';

test('the common delivery grid accepts intraday geometry and explicitly supplied tracks without gas conventions',async()=>{
 const {physicalDeliveryGrid,readPhysicalDelivery}=await import('../dist/physical-delivery.js');
 const grid=physicalDeliveryGrid(readPhysicalDelivery(delivery,{tradeId:'trade',bindingId:'binding',revision:1}),'UTC');
 const value=(row,key)=>grid.rows[row][grid.keys.indexOf(key)];
 assert.equal(grid.rows.length,2);assert.equal(value(0,'contract'),'1/3');assert.equal(value(1,'contract'),'100.000000000000000001');assert.equal(value(0,'metered'),'0');assert.equal(value(0,'accepted'),'');assert.equal(value(0,'direction'),'Deliver');assert.equal(value(0,'unit'),'MWh');assert.equal(value(1,'start'),'2021-01-01T00:15:00Z');assert.ok(!grid.keys.includes('nominated'));assert.equal(grid.headers[0],'Operating day');assert.deepEqual(grid.rowKeys,['0','1']);
});
test('the delivery reader rejects a different binding/revision and incomplete contracts',async()=>{
 const {readPhysicalDelivery}=await import('../dist/physical-delivery.js');
 for(const change of [{bindingId:'other'},{revision:2},{capabilities:undefined},{version:'unknown'},{rows:[delivery.rows[0],delivery.rows[0]]}])
  assert.throws(()=>readPhysicalDelivery({...delivery,...change},{tradeId:'trade',bindingId:'binding',revision:1}));
});
test('delivery bounds preserve distinct supported microsecond instants',async()=>{
 const {physicalDeliveryGrid}=await import('../dist/physical-delivery.js');
 const view={...delivery,rows:[{...delivery.rows[0],start:'2021-01-01T09:00:00.000001Z',end:'2021-01-01T09:00:00.000002Z'}]};
 const grid=physicalDeliveryGrid(view,'UTC');
 const start=grid.displayRows[0][grid.keys.indexOf('start')],end=grid.displayRows[0][grid.keys.indexOf('end')];
 assert.notEqual(start,end);assert.match(start,/000001/);assert.match(end,/000002/);
});
