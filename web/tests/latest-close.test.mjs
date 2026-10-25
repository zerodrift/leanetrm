import test from 'node:test';
import assert from 'node:assert/strict';
import {newestClose,closeBadge,latestInstant} from '../dist/latest-close.js';

// Closes of one display date. Each close is an id, closed at 21:00 UTC, or [id, time] for another time that day.
const day=(date,...closes)=>({date,frames:closes.map(close=>{const [id,time]=Array.isArray(close)?close:[close,'21:00'];return {id,at:`${date}T${time}:00Z`,name:id,businessDateUTC:date};})});
const at=(date,time='21:00')=>`${date}T${time}:00Z`;

test('the newest date with one close is chosen',()=>{
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]}),{kind:'single',closeId:'b',date:'2026-10-16'});
});
test('a newest date with closes at several times chooses the uniquely latest close (D217)',()=>{
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-16',['b','21:00'],['c','16:00'])]}),{kind:'single',closeId:'b',date:'2026-10-16'});
});
test('several closes at the latest instant, such as versions of one close, ask instead of choosing (D162)',()=>{
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-16','b','c')]}),{kind:'choose',date:'2026-10-16'});
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-16',['a','16:00'],'b','c')]}),{kind:'choose',date:'2026-10-16'});
});
test('no available dates selects nothing',()=>{
 assert.deepEqual(newestClose({maxDate:null,dates:[]}),{kind:'none'});
 assert.equal(latestInstant({maxDate:null,dates:[]}),null);
});
test('the latest instant is the time of the latest close, on the newest date',()=>{
 assert.equal(latestInstant({maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16',['b','16:00'],['c','20:00'])]}),at('2026-10-16','20:00'));
});
test('badges distinguish latest, an earlier choice, a newer arrival and a needed choice',()=>{
 const cat={maxDate:'2026-10-19',dates:[day('2026-10-16','b'),day('2026-10-19','d')]};
 assert.equal(closeBadge('d',at('2026-10-19'),cat),'latest');assert.equal(closeBadge('b',at('2026-10-16'),cat),'newer');assert.equal(closeBadge('b',at('2026-10-19'),cat),'selected');
 assert.equal(closeBadge('',at('2026-10-16'),{maxDate:'2026-10-16',dates:[day('2026-10-16','b','c')]}),'choose');
});

test('the newest date is the one the backend names, whatever order the dates arrive in',()=>{
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-16','b'),day('2026-10-15','a')]}),{kind:'single',closeId:'b',date:'2026-10-16'});
});
test('a catalog that disagrees about its newest date is refused, never read as empty or older',()=>{
 for(const catalog of [
  {maxDate:'2026-10-16',dates:[day('2026-10-15','a')]},
  {maxDate:null,dates:[day('2026-10-15','a')]},
  {maxDate:'2026-10-15',dates:[day('2026-10-15','a'),day('2026-10-16','b')]},
 ])for(const read of [()=>newestClose(catalog),()=>closeBadge('a',at('2026-10-15'),catalog),()=>latestInstant(catalog)])assert.throws(read,/inconsistent/);
});
test('a close on the newest date whose time cannot be read is refused, never compared',()=>{
 const catalog={maxDate:'2026-10-16',dates:[{date:'2026-10-16',frames:[{id:'b',at:'not a time',name:'b',businessDateUTC:'2026-10-16'},{id:'c',at:at('2026-10-16'),name:'c',businessDateUTC:'2026-10-16'}]}]};
 for(const read of [()=>newestClose(catalog),()=>closeBadge('c',at('2026-10-16'),catalog),()=>latestInstant(catalog)])assert.throws(read,/inconsistent/);
});
test('any close at the latest instant is the latest close, even when several share it',()=>{
 const cat={maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b','c')]};
 for(const id of ['b','c'])assert.equal(closeBadge(id,at('2026-10-16'),cat),'latest');
});
test('an earlier close of the newest date is not the latest close: chosen with nothing newer since, it is Selected',()=>{
 const cat={maxDate:'2026-10-16',dates:[day('2026-10-16',['b','16:00'],['c','21:00'])]};
 assert.equal(closeBadge('b',at('2026-10-16'),cat),'selected');
 assert.equal(closeBadge('c',at('2026-10-16'),cat),'latest');
});
test('a later close of the same date is a newer close for one chosen before it appeared',()=>{
 const before={maxDate:'2026-10-16',dates:[day('2026-10-16',['b','16:00'])]};
 const after={maxDate:'2026-10-16',dates:[day('2026-10-16',['b','16:00'],['c','21:00'])]};
 assert.equal(closeBadge('b',at('2026-10-16','16:00'),before),'latest');
 assert.equal(closeBadge('b',at('2026-10-16','16:00'),after),'newer');
});
test('a close at the latest instant is the latest even if that instant was not known when it was selected',()=>{
 assert.equal(closeBadge('d',at('2026-10-16'),{maxDate:'2026-10-19',dates:[day('2026-10-16','b'),day('2026-10-19','d')]}),'latest');
});
test('a newer close is offered whoever selected the close: an earlier close is newer once a later close has appeared since it was selected',()=>{
 const before={maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]};
 const after={maxDate:'2026-10-19',dates:[...before.dates,day('2026-10-19','d')]};
 // The latest instant the calendar had when the close was selected: 10-16 21:00 for either close, whether the app or a person selected it.
 for(const id of ['a','b']){assert.equal(closeBadge(id,at('2026-10-16'),after),'newer');}
 assert.equal(closeBadge('b',at('2026-10-16'),before),'latest');
});
test('an earlier close selected with no newer close since stays Selected',()=>{
 const cat={maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]};
 assert.equal(closeBadge('a',at('2026-10-16'),cat),'selected');
 // Selected knowing a later close than the calendar lists now: nothing newer has appeared.
 assert.equal(closeBadge('a',at('2026-10-17'),cat),'selected');
});
test('a latest instant unknown when the close was selected claims no newer close',()=>{
 assert.equal(closeBadge('a',null,{maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]}),'selected');
});
test('with nothing selected the badge asks for a choice, even when the newest date has one close',()=>{
 assert.equal(closeBadge('',at('2026-10-16'),{maxDate:'2026-10-16',dates:[day('2026-10-16','b')]}),'choose');
});
