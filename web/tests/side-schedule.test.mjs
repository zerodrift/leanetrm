import test from 'node:test';
import assert from 'node:assert/strict';
import {sideScheduleData} from '../dist/side-schedule.js';
test('side schedule preserves exact numeric values, formatted display and distinct payments sharing a date',()=>{
 const rows=[{deliveryFrom:'2030-01-01',deliveryTo:'2030-02-01',paymentDate:'2030-03-01',cashAmount:'1.000000000000000001',discountFactor:'0.99',presentValue:'0.990000000000000001'},
 {deliveryFrom:'2030-02-01',deliveryTo:'2030-03-01',paymentDate:'2030-03-01',cashAmount:'0',discountFactor:'1',presentValue:'0'}];
 const data=sideScheduleData(rows,'scope','USD/MWh',(value)=>'formatted '+value);
 assert.equal(data.rows[0][3],rows[0].cashAmount);assert.equal(data.displayRows[0][3],'formatted '+rows[0].cashAmount);
 assert.deepEqual(data.numeric,[3,4,5]);assert.deepEqual(data.chart.series[0].points.map(p=>p.y),[rows[0].cashAmount,'0']);
 assert.equal(new Set(data.chart.series[0].points.map(p=>p.x)).size,2);
 assert.equal(data.chart.series[1].points[0].y,rows[0].presentValue);
 assert.equal(data.chart.unit,'USD/MWh');
});
