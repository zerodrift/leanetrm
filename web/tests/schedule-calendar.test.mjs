import test from 'node:test';
import assert from 'node:assert/strict';
import {monthCells,instantDate} from '../dist/schedule-calendar.js';
test('calendar geometry preserves leap days and Monday-aligned civil dates',()=>{
 const cells=monthCells('2024-02');
 assert.equal(cells.length,35);assert.deepEqual(cells.slice(0,3),[null,null,null]);
 assert.equal(cells.filter(Boolean).length,29);assert.equal(cells[31],'2024-02-29');
 assert.equal(monthCells('2026-03').length,42);
});
test('calendar timestamp fallback uses the explicit display zone',()=>{
 assert.equal(instantDate('2026-02-01T01:30:00Z','America/Chicago'),'2026-01-31');
});
