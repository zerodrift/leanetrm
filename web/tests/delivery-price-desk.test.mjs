import test from 'node:test';
import assert from 'node:assert/strict';
import {deliveryChartPoints,summaryTiles,collectionTile} from '../dist/delivery-price-desk.js';

const row=(start,end,formattedPrice)=>({start,end,formattedPrice});

test('the chart has one point per published interval, carrying the exact price string',()=>{
 const rows=[row('2026-11-01T05:00:00Z','2026-11-01T05:15:00Z','31.40'),row('2026-11-01T05:15:00Z','2026-11-01T05:30:00Z','-1.25')];
 assert.deepEqual(deliveryChartPoints(rows),[{at:'2026-11-01T05:00:00Z',value:'31.40'},{at:'2026-11-01T05:15:00Z',value:'-1.25'}]);
 assert.deepEqual(deliveryChartPoints([]),[]);
});

test('an unpublished interval between two published ones is a gap, so no line bridges it',()=>{
 const rows=[row('2026-11-02T05:00:00Z','2026-11-02T06:00:00Z','10'),row('2026-11-02T08:00:00Z','2026-11-02T09:00:00Z','12')];
 assert.deepEqual(deliveryChartPoints(rows),[{at:'2026-11-02T05:00:00Z',value:'10'},{at:'2026-11-02T06:00:00Z',value:null},{at:'2026-11-02T08:00:00Z',value:'12'}]);
 // The same instants written differently are still contiguous.
 assert.deepEqual(deliveryChartPoints([row('2026-11-02T05:00:00Z','2026-11-02T06:00:00+00:00','10'),row('2026-11-02T06:00:00Z','2026-11-02T07:00:00Z','12')]).map(p=>p.value),['10','12']);
});

const summary={intervals:23,expectedIntervals:24,missingIntervals:1,low:{price:'-2.5',start:'2026-11-02T05:00:00Z'},high:{price:'31.125',start:'2026-11-03T01:00:00Z'},
 average:'12.456521739130434783',latestKnownAt:'2026-11-03T12:00:00Z',policy:'exact_rational_half_even_18_v1'};
const labels={start:at=>`start ${at}`,instant:at=>`known ${at}`};

test('summary tiles state the backend\'s counts and prices verbatim, with the instants of the low and the high',()=>{
 const tiles=summaryTiles(summary,'USD / MWh',labels);
 assert.deepEqual(tiles.map(t=>t.label),['Intervals','Low','High','Average','Latest knowledge']);
 assert.deepEqual(tiles[0].rows,[{figure:'23',caption:'of 24'}]);
 assert.equal(tiles[0].note,'1 missing at this cutoff');
 assert.deepEqual(tiles[1],{label:'Low',aside:'start 2026-11-02T05:00:00Z',rows:[{figure:'-2.5',caption:'USD / MWh'}]});
 assert.deepEqual(tiles[2],{label:'High',aside:'start 2026-11-03T01:00:00Z',rows:[{figure:'31.125',caption:'USD / MWh'}]});
 assert.deepEqual(tiles[3],{label:'Average',aside:'Time-weighted',rows:[{figure:'12.456521739130434783',caption:'USD / MWh'}]});
 assert.deepEqual(tiles[4],{label:'Latest knowledge',rows:[{figure:'known 2026-11-03T12:00:00Z'}],words:true});
});

test('a complete period says nothing is missing, and an empty one states no price instead of a zero',()=>{
 assert.equal(summaryTiles({...summary,intervals:24,missingIntervals:0},'USD / MWh',labels)[0].note,undefined);
 const empty=summaryTiles({...summary,intervals:0,missingIntervals:24,low:null,high:null,average:null,latestKnownAt:null},'USD / MWh',labels);
 assert.deepEqual(empty.map(t=>t.rows[0].figure),['0','No price','No price','No price','Nothing published']);
 assert.ok(empty.slice(1).every(t=>t.words&&t.rows[0].caption===undefined&&t.aside===undefined));
 assert.equal(empty[0].note,'24 missing at this cutoff');
});

test('the collection tile states the jobs and the newest attempt\'s outcome as the catalog gives them',()=>{
 const date=d=>`day ${d}`;
 assert.deepEqual(collectionTile({jobs:2,enabledJobs:1,latestAttempt:{state:'failed',finishedAt:'2026-11-03T00:00:00Z',fromDate:'2026-11-01',throughDate:'2026-11-02',errorCode:'provider_unavailable'}},date),
  {label:'Collection',aside:'2 jobs · 1 enabled',rows:[{figure:'failed',caption:'newest attempt · day 2026-11-01 – day 2026-11-02'}],note:'provider_unavailable',words:true});
 assert.deepEqual(collectionTile({jobs:1,enabledJobs:1,latestAttempt:{state:'succeeded',finishedAt:null,fromDate:'2026-11-02',throughDate:'2026-11-02',errorCode:null}},date),
  {label:'Collection',aside:'1 job · 1 enabled',rows:[{figure:'succeeded',caption:'newest attempt · day 2026-11-02'}],words:true});
 assert.deepEqual(collectionTile({jobs:0,enabledJobs:0,latestAttempt:null},date),{label:'Collection',aside:'0 jobs · 0 enabled',rows:[{figure:'No attempts yet'}],words:true});
});
