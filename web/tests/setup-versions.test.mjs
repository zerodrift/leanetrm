import test from 'node:test';
import assert from 'node:assert/strict';
import {setupVersionLabels} from '../dist/setup-versions.js';

// Saved delivery setups are named by their publication, never by their keys.
const published=(id,order)=>({id,state:'published',base_setup_id:null,publication_order:order});
const draft=(id,base)=>({id,state:'draft',base_setup_id:base,publication_order:null});
test('a published setup reads by its publication order and a draft by the setup it revises, each distinct and without its key',()=>{
 const rows=[published('a1b2c3d4-0000-4000-8000-000000000002',2),published('a1b2c3d4-0000-4000-8000-000000000001',1),
  draft('a1b2c3d4-0000-4000-8000-000000000003','a1b2c3d4-0000-4000-8000-000000000002'),draft('a1b2c3d4-0000-4000-8000-000000000004','a1b2c3d4-0000-4000-8000-000000000002'),
  draft('a1b2c3d4-0000-4000-8000-000000000005',null)];
 const labels=setupVersionLabels(rows);
 assert.deepEqual(rows.map(r=>labels.get(r.id)),['Published setup 2','Published setup 1','Draft from published setup 2 · 1','Draft from published setup 2 · 2','New draft']);
});
test('a draft whose base is not listed says so, and a state the editor does not know is refused rather than shown by its key',()=>{
 assert.equal(setupVersionLabels([draft('d','missing')]).get('d'),'Draft from an earlier setup');
 assert.throws(()=>setupVersionLabels([{id:'x',state:'retired',base_setup_id:null,publication_order:3}]),/state/);
});
