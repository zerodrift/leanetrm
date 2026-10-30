import test from 'node:test';
import assert from 'node:assert/strict';
import {calendarMonth,shiftCalendarMonth,closesOnDate,uniqueClose,unavailableClosesMessage} from '../dist/market-calendar.js';

const catalog={timeZone:'America/Chicago',curve:'synthetic_curve',selectionPolicy:'exact_snapshot_no_close_substitution_v1',minDate:'2024-02-03',maxDate:'2024-02-06',dates:[
 {date:'2024-02-03',frames:[{id:'one',at:'2024-02-04T01:00:00Z',name:'First close',businessDateUTC:'2024-02-04'}]},
 {date:'2024-02-06',frames:[{id:'two',at:'2024-02-06T15:00:00Z',name:'Early close',businessDateUTC:'2024-02-06'},{id:'three',at:'2024-02-06T21:00:00Z',name:'Late close',businessDateUTC:'2024-02-06'}]}],unavailable:[]};
test('calendar grid is presentation-only and includes leap day in its correct weekday',()=>{
 const cells=calendarMonth('2024-02');assert.equal(cells.length,42);assert.equal(cells[4],'2024-02-01');assert.equal(cells[32],'2024-02-29');assert.equal(cells.filter(Boolean).length,29);
});
test('month navigation crosses year boundaries without inventing available dates',()=>{
 assert.equal(shiftCalendarMonth('2024-01',-1),'2023-12');assert.equal(shiftCalendarMonth('2024-12',1),'2025-01');
});
test('availability uses server dates including a supplied weekend and preserves exact frame identity',()=>{
 assert.deepEqual(closesOnDate(catalog,'2024-02-03'),catalog.dates[0].frames);assert.equal(uniqueClose(catalog,'2024-02-03'),'one');assert.deepEqual(closesOnDate(catalog,'2024-02-04'),[]);
});
test('multiple closes never implicitly select the first or latest snapshot',()=>{
 assert.equal(uniqueClose(catalog,'2024-02-06'),undefined);assert.equal(uniqueClose(catalog,'2024-02-04'),undefined);assert.equal(closesOnDate(catalog,'2024-02-06')[1].id,'three');
});


test('a calendar with no closes explains its backend refusals instead of giving selection instructions',()=>{
 const empty={...catalog,dates:[],minDate:null,maxDate:null,unavailable:[{date:'2024-02-03',reason:'source_expired'},{date:'2024-02-04',reason:'missing_observation'}]};
 const message=unavailableClosesMessage(empty);
 assert.match(message,/No complete close/);assert.match(message,/expired/);assert.match(message,/missing/);
 assert.doesNotMatch(message,/Select an available date/);
 assert.equal(unavailableClosesMessage(catalog),'');
 assert.match(unavailableClosesMessage({...empty,unavailable:[]}),/No complete close/);
 assert.doesNotMatch(unavailableClosesMessage({...empty,unavailable:[{date:'2024-02-03',reason:'unknown_internal_code'}]}),/unknown_internal_code/);
});
