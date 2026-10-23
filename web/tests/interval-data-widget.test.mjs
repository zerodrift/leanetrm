import test from 'node:test';
import assert from 'node:assert/strict';
import {intervalDataPoints} from '../dist/interval-data-widget.js';
test('missing or withdrawn intervals break the plot and zero remains a value',()=>{
 const rows=[{start:'2030-01-01T00:00:00Z',end:'2030-01-01T00:15:00Z',value:'0'},
 {start:'2030-01-01T00:30:00Z',end:'2030-01-01T00:45:00Z',value:'2'}];
 const points=intervalDataPoints(rows,['First','Third']);assert.deepEqual(points.map(p=>p.y),['0',null,'2']);assert.equal(points[1].x,rows[0].end);
});
