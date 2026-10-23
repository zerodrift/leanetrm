import test from 'node:test';
import assert from 'node:assert/strict';
import {newestClose,closeBadge} from '../dist/latest-close.js';

const day=(date,...ids)=>({date,frames:ids.map(id=>({id,at:date+'T21:00:00Z',name:id,businessDateUTC:date}))});

test('the newest date with one close is chosen',()=>{
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]}),{kind:'single',closeId:'b',date:'2026-10-16'});
});
test('a newest date with two closes asks instead of choosing',()=>{
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-16','b','c')]}),{kind:'choose',date:'2026-10-16'});
});
test('no available dates selects nothing',()=>assert.deepEqual(newestClose({maxDate:null,dates:[]}),{kind:'none'}));
test('badges distinguish latest, an earlier choice, a newer arrival and a needed choice',()=>{
 const cat={maxDate:'2026-10-19',dates:[day('2026-10-16','b'),day('2026-10-19','d')]};
 assert.equal(closeBadge('d','2026-10-19',cat),'latest');assert.equal(closeBadge('b','2026-10-16',cat),'newer');assert.equal(closeBadge('b','2026-10-19',cat),'selected');
 assert.equal(closeBadge('','2026-10-16',{maxDate:'2026-10-16',dates:[day('2026-10-16','b','c')]}),'choose');
});

test('the newest date is the one the backend names, whatever order the dates arrive in',()=>{
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-16','b'),day('2026-10-15','a')]}),{kind:'single',closeId:'b',date:'2026-10-16'});
});
test('a catalog that disagrees about its newest date is refused, never read as empty or older',()=>{
 for(const catalog of [
  {maxDate:'2026-10-16',dates:[day('2026-10-15','a')]},
  {maxDate:null,dates:[day('2026-10-15','a')]},
  {maxDate:'2026-10-15',dates:[day('2026-10-15','a'),day('2026-10-16','b')]},
 ])for(const read of [()=>newestClose(catalog),()=>closeBadge('a','2026-10-15',catalog)])assert.throws(read,/inconsistent/);
});
test('any close on the newest date is the latest close, even when the date has several',()=>{
 const cat={maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b','c')]};
 for(const id of ['b','c'])assert.equal(closeBadge(id,'2026-10-16',cat),'latest');
});
test('a close on the newest date is the latest even if its date was not known when it was selected',()=>{
 assert.equal(closeBadge('d','2026-10-16',{maxDate:'2026-10-19',dates:[day('2026-10-16','b'),day('2026-10-19','d')]}),'latest');
});
test('a newer date is offered whoever selected the close: an earlier close is newer once a later date has appeared since it was selected',()=>{
 const before={maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]};
 const after={maxDate:'2026-10-19',dates:[...before.dates,day('2026-10-19','d')]};
 // The newest date the calendar had when the close was selected: 10-16 for either close, whether the app or a person selected it.
 for(const id of ['a','b']){assert.equal(closeBadge(id,'2026-10-16',after),'newer');}
 assert.equal(closeBadge('b','2026-10-16',before),'latest');
});
test('an earlier close selected with no newer date since stays Selected',()=>{
 const cat={maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]};
 assert.equal(closeBadge('a','2026-10-16',cat),'selected');
 // Selected knowing a later date than the calendar names now: nothing newer has appeared.
 assert.equal(closeBadge('a','2026-10-17',cat),'selected');
});
test('a newest date unknown when the close was selected claims no newer date', ()=>{
 assert.equal(closeBadge('a',null,{maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]}),'selected');
});
test('with nothing selected the badge asks for a choice, even when the newest date has one close',()=>{
 assert.equal(closeBadge('','2026-10-16',{maxDate:'2026-10-16',dates:[day('2026-10-16','b')]}),'choose');
});
