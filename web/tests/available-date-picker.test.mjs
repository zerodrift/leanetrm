import test from 'node:test';
import assert from 'node:assert/strict';
import {calendarDays,frameDays,framesOnDay} from '../dist/available-date-picker.js';

test('calendar exposes only served dates including gaps, leap days and empty months',()=>{
 const days=calendarDays('2024-02',['2024-02-02','2024-02-29']);
 assert.equal(days.length,29);
 assert.deepEqual(days.filter(d=>d.available).map(d=>d.date),['2024-02-02','2024-02-29']);
 assert.equal(days[0].weekday,4);
 assert.ok(calendarDays('2024-03',[]).every(d=>!d.available));
});
test('market calendar groups exact snapshots by user local day, preserving corrections',()=>{
 const frames=[{id:'a',at:'2024-02-02T00:30:00Z'},{id:'b',at:'2024-02-02T00:30:00Z'},{id:'c',at:'2024-02-02T23:00:00Z'}];
 assert.deepEqual(frameDays(frames,'America/Chicago'),['2024-02-01','2024-02-02']);
 assert.deepEqual(framesOnDay(frames,'2024-02-01','America/Chicago').map(f=>f.id),['a','b']);
 assert.deepEqual(frameDays(frames,'Asia/Tokyo'),['2024-02-02','2024-02-03']);
});
test('a DST repeated clock stays on one calendar day without dropping either instant',()=>{
 const frames=[{id:'a',at:'2026-11-01T06:30:00Z'},{id:'b',at:'2026-11-01T07:30:00Z'}];
 assert.deepEqual(frameDays(frames,'America/Chicago'),['2026-11-01']);
 assert.equal(framesOnDay(frames,'2026-11-01','America/Chicago').length,2);
});
