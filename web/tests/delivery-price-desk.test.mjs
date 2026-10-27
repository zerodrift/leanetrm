import test from 'node:test';
import assert from 'node:assert/strict';
import {periodChart,summaryTiles,collectionTile} from '../dist/delivery-price-desk.js';

const point=(at,value,unavailableReason=value===null?'not_published_at_cutoff':null)=>({at,value,unavailableReason});

test('the chart draws exactly the series the period read states, an unpublished interval as a point without a price',()=>{
 const chart={points:[point('2026-11-02T05:00:00Z',null),point('2026-11-02T06:00:00Z','10'),point('2026-11-02T07:00:00Z',null),point('2026-11-02T08:00:00Z','-1.25'),point('2026-11-02T09:00:00Z',null)]};
 assert.deepEqual(periodChart({chart}),chart.points.map(({at,value})=>({at,value})));
 assert.deepEqual(periodChart({chart:{points:[]}}),[]);
});

test('a period read that does not state its chart series is refused, never rebuilt from its rows',()=>{
 assert.throws(()=>periodChart({}),/chart/);
 assert.throws(()=>periodChart({chart:{}}),/chart/);
 assert.throws(()=>periodChart({chart:{points:[{at:'2026-11-02T05:00:00Z'}]}}),/chart/);
 assert.throws(()=>periodChart({chart:{points:[point('2026-11-02T05:00:00Z',null,null)]}}),/chart/);
 assert.throws(()=>periodChart({chart:{points:[point('2026-11-02T05:00:00Z',12)]}}),/chart/);
});

const summary={intervals:23,expectedIntervals:24,missingIntervals:1,low:{price:'-2.5',start:'2026-11-02T05:00:00Z'},high:{price:'31.125',start:'2026-11-03T01:00:00Z'},
 average:'12.456521739130434783',latestKnownAt:'2026-11-03T12:00:00Z',policy:'exact_rational_half_even_18_v1'};
const labels={start:at=>`start ${at}`,instant:at=>`known ${at}`};

test('summary tiles state the backend\'s counts and prices verbatim, with the instants of the low and the high',()=>{
 const tiles=summaryTiles(summary,'USD / MWh',labels);
 assert.deepEqual(tiles.map(t=>t.label),['Intervals','Low','High','Average','Latest knowledge']);
 assert.deepEqual(tiles.map(t=>t.kind),['intervals','low','high','average','known']);
 assert.deepEqual(tiles[0].rows,[{figure:'23',caption:'of 24'}]);
 assert.equal(tiles[0].note,'1 missing at this cutoff');
 assert.deepEqual(tiles[1],{kind:'low',label:'Low',aside:'start 2026-11-02T05:00:00Z',rows:[{figure:'-2.5',caption:'USD / MWh'}]});
 assert.deepEqual(tiles[2],{kind:'high',label:'High',aside:'start 2026-11-03T01:00:00Z',rows:[{figure:'31.125',caption:'USD / MWh'}]});
 assert.deepEqual(tiles[3],{kind:'average',label:'Average',aside:'Time-weighted',rows:[{figure:'12.456521739130434783',caption:'USD / MWh'}]});
 assert.deepEqual(tiles[4],{kind:'known',label:'Latest knowledge',rows:[{figure:'known 2026-11-03T12:00:00Z'}],words:true});
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
  {kind:'collection',label:'Collection',aside:'2 jobs · 1 enabled',rows:[{figure:'failed',caption:'newest attempt · day 2026-11-01 – day 2026-11-02'}],note:'provider_unavailable',words:true});
 assert.deepEqual(collectionTile({jobs:1,enabledJobs:1,latestAttempt:{state:'succeeded',finishedAt:null,fromDate:'2026-11-02',throughDate:'2026-11-02',errorCode:null}},date),
  {kind:'collection',label:'Collection',aside:'1 job · 1 enabled',rows:[{figure:'succeeded',caption:'newest attempt · day 2026-11-02'}],words:true});
 assert.deepEqual(collectionTile({jobs:0,enabledJobs:0,latestAttempt:null},date),{kind:'collection',label:'Collection',aside:'0 jobs · 0 enabled',rows:[{figure:'No attempts yet'}],words:true});
});
