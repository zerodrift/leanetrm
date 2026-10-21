import test from 'node:test';
import assert from 'node:assert/strict';
import {aggregateTable,describeAggregate,describePoint,intervalChart,intervalTable} from '../dist/interval-curves-view.js';
import {chartOptions} from '../dist/chart-view.js';

const zone='America/New_York';
const price=display=>({numerator:'1',denominator:'1',display});
// The fall-back hour: 01:00 EDT and 01:00 EST start one hour apart.
const repeated=[['2031-11-02T05:00:00+00:00','2031-11-02T06:00:00+00:00',1],['2031-11-02T06:00:00+00:00','2031-11-02T07:00:00+00:00',2]]
  .map(([start,end,occurrence])=>({start,end,operatingDate:'2031-11-02',hourLabel:1,minute:0,occurrence}));
const marked={curve:'test_power_hourly_marked',kind:'marked',points:repeated.map((interval,i)=>({interval,price:price(['40.250000','40.500000'][i]),observationIds:['x']}))};
const shaped={curve:'test_power_hourly_shaped',kind:'shaped',knownAt:'2031-11-01T12:00:00+00:00',points:repeated.map((interval,i)=>({interval,price:price('40.000000'),
  block:{curve:'test_power_offpeak_monthly',contractId:'x',observationIds:['x']},shapeRevision:i+1}))};
const resolved={curve:'test_power_hourly_marked',kind:'marked',resolved:true,points:[
  {interval:repeated[0],price:price('30.000000'),state:'actual',dueAt:'2031-11-02T07:00:00+00:00',actual:{batchId:'x',providerRevision:2,knownAt:'2031-11-02T09:00:00+00:00'},forward:null},
  {interval:repeated[1],price:null,state:'unresolved',dueAt:'2031-11-02T08:00:00+00:00',actual:null,forward:null}],
  aggregates:[{start:repeated[0].start,end:repeated[1].end,mean:null,constituents:repeated.map(i=>i.start)}]};

test('a table names each repeated hour by its offset and adds the columns its read carries',()=>{
  const table=intervalTable(marked,zone);
  assert.deepEqual(table.headers,['Start','End','Price']);
  assert.deepEqual(table.rows.map(r=>r[0]),['Nov 2, 2031, 1:00 AM GMT-4','Nov 2, 2031, 1:00 AM GMT-5']);
  assert.deepEqual(table.rows.map(r=>r[2]),['40.250000','40.500000']);
  assert.deepEqual(intervalTable(shaped,zone).headers,['Start','End','Price','Block','Shape revision']);
  assert.deepEqual(intervalTable(shaped,zone).rows[1].slice(3),['Power Offpeak Monthly','2']);
  assert.deepEqual(intervalTable(resolved,zone).headers,['Start','End','Price','State','Due']);
  assert.deepEqual(intervalTable(resolved,zone).rows[1].slice(2),['Unresolved','Unresolved','Nov 2, 2031, 3:00 AM']);
});

test('choosing a row explains where its price came from, never an internal identifier',()=>{
  const table=intervalTable(resolved,zone);
  assert.equal(describePoint(marked,0,intervalTable(marked,zone).rows[0][0],zone),'Nov 2, 2031, 1:00 AM GMT-4 · 40.250000 · the pinned mark of this interval');
  assert.equal(describePoint(shaped,1,'Start',zone),'Start · 40.000000 · the Power Offpeak Monthly block mark, shaped by shape revision 2');
  assert.equal(describePoint(resolved,0,table.rows[0][0],zone),'Nov 2, 2031, 1:00 AM GMT-4 · 30.000000 · the collected actual, provider revision 2, known at Nov 2, 2031, 4:00:00 AM');
  assert.equal(describePoint(resolved,1,table.rows[1][0],zone),'Nov 2, 2031, 1:00 AM GMT-5 · no price · no eligible actual yet; it is due Nov 2, 2031, 3:00 AM');
  for(const values of [marked,shaped,resolved])for(const i of [0,1])assert.doesNotMatch(describePoint(values,i,'Start',zone),/\bx\b/);
});

test('an aggregate keeps its native intervals and has no mean while one is unresolved',()=>{
  assert.deepEqual(aggregateTable(resolved,zone).rows,[['Nov 2, 2031, 1:00 AM','Nov 2, 2031, 2:00 AM','None · an interval is unresolved','2']]);
  assert.equal(describeAggregate(resolved,0,zone),'Nov 2, 2031, 1:00 AM to Nov 2, 2031, 2:00 AM · 2 intervals: Nov 2, 2031, 1:00 AM GMT-4 30.000000, Nov 2, 2031, 1:00 AM GMT-5 unresolved');
});

test('the chart plots each native interval by its exact start and server display value',()=>{
  const spec=intervalChart(marked,'Synthetic hourly','USD/MWh',zone),option=chartOptions(spec);
  assert.deepEqual(option.xAxis.data,repeated.map(i=>i.start));
  assert.deepEqual(option.series[0].data,[40.25,40.5]);
  assert.equal(option.xAxis.axisLabel.formatter(repeated[1].start),'Nov 2, 2031, 1:00 AM GMT-5');
  assert.equal(option.series[0].showSymbol,false);
  assert.equal(option.series[0].sampling,'lttb');
  assert.equal(chartOptions({...spec,dense:false}).series[0].showSymbol,true);
});
