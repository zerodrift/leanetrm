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
 assert.equal(closeBadge('d',false,cat),'latest');assert.equal(closeBadge('b',false,cat),'newer');assert.equal(closeBadge('b',true,cat),'selected');
 assert.equal(closeBadge('',false,{maxDate:'2026-10-16',dates:[day('2026-10-16','b','c')]}),'choose');
});

test('the newest date is the one the backend names, whatever order the dates arrive in',()=>{
 assert.deepEqual(newestClose({maxDate:'2026-10-16',dates:[day('2026-10-16','b'),day('2026-10-15','a')]}),{kind:'single',closeId:'b',date:'2026-10-16'});
});
test('a catalog that disagrees about its newest date is refused, never read as empty or older',()=>{
 for(const catalog of [
  {maxDate:'2026-10-16',dates:[day('2026-10-15','a')]},
  {maxDate:null,dates:[day('2026-10-15','a')]},
  {maxDate:'2026-10-15',dates:[day('2026-10-15','a'),day('2026-10-16','b')]},
 ])for(const read of [()=>newestClose(catalog),()=>closeBadge('a',true,catalog)])assert.throws(read,/inconsistent/);
});
test('any close on the newest date is the latest close, whoever chose it, even when the date has several',()=>{
 const cat={maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b','c')]};
 for(const chosenByUser of [false,true])for(const id of ['b','c'])assert.equal(closeBadge(id,chosenByUser,cat),'latest');
});
test('a close a person picked from an earlier date stays Selected when a newer date arrives; an app default becomes newer',()=>{
 const before={maxDate:'2026-10-16',dates:[day('2026-10-15','a'),day('2026-10-16','b')]};
 const after={maxDate:'2026-10-19',dates:[...before.dates,day('2026-10-19','d')]};
 assert.equal(closeBadge('b',false,before),'latest');assert.equal(closeBadge('b',false,after),'newer');
 assert.equal(closeBadge('a',true,before),'selected');assert.equal(closeBadge('a',true,after),'selected');
});
test('with nothing selected the badge asks for a choice, even when the newest date has one close',()=>{
 assert.equal(closeBadge('',true,{maxDate:'2026-10-16',dates:[day('2026-10-16','b')]}),'choose');
});
