import test from 'node:test';
import assert from 'node:assert/strict';
import {calendarDays} from '../dist/available-date-picker.js';

test('calendar exposes only served dates including gaps, leap days and empty months',()=>{
 const days=calendarDays('2024-02',['2024-02-02','2024-02-29']);
 assert.equal(days.length,29);
 assert.deepEqual(days.filter(d=>d.available).map(d=>d.date),['2024-02-02','2024-02-29']);
 assert.equal(days[0].weekday,4);
 assert.ok(calendarDays('2024-03',[]).every(d=>!d.available));
});
