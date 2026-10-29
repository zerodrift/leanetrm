import test from 'node:test';
import assert from 'node:assert/strict';
import {ppaCutoff,readPpaView} from '../dist/ppa-trade-view.js';
test('PPA inspection retains database microseconds when adapting a common context cutoff',()=>{
 assert.equal(ppaCutoff('2026-10-26T12:34:56.1234560+00:00'),'2026-10-26T12:34:56.123456+00:00');
 assert.equal(ppaCutoff('2026-10-26T12:34:56.123456Z'),'2026-10-26T12:34:56.123456Z');
});
test('a PPA position offers its backend schedule under the same pinned read without fetching interval detail eagerly',async()=>{
 const calls=[],terms={definitionRevisionId:'definition',referenceCatalogId:'reference',referenceRevisionId:'revision',currency:'usd',delivery:{from:'2026-03-01',to:'2026-04-01'},memberships:[]};
 const read=await readPpaView(async(path,body)=>{calls.push({path,body});return {head:{revision:2},bindings:[{binding_id:'binding',instrument_id:'instrument',coverage_from:'2026-03-01T05:00:00Z',coverage_to:'2026-04-01T04:00:00Z'}],instruments:[{instrumentId:'instrument',terms}],history:[]};},
  {snapshotId:'close'}, {tradeId:'trade',bindingId:'binding',revision:'2',pendingActuals:'require_actuals_v1'},'2026-02-28T12:00:00.1234560Z');
 assert.equal(typeof read.view('UTC').schedules,'function');
 assert.deepEqual(calls.map(c=>c.path),['ppa/editor']);
 assert.equal(calls[0].body.knownAt,'2026-02-28T12:00:00.123456Z');
});
