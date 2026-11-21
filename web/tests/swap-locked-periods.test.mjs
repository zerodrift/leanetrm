import test from 'node:test';
import assert from 'node:assert/strict';
import {droppedLockedPeriod,lifecyclePeriodRows,lockedNote} from '../dist/swap-locked-periods.js';

const january={deliveryFrom:'2031-01-01',deliveryTo:'2031-02-01'},february={deliveryFrom:'2031-02-01',deliveryTo:'2031-03-01'};

test('a started month states why it cannot change, and any other month states nothing',()=>{
 const locked=[{...january,reason:'settled'},{...february,reason:'in_progress'}];
 assert.equal(lockedNote(locked,'2031-01-01','2031-02-01'),'Settled · cannot change');
 assert.equal(lockedNote(locked,'2031-02-01','2031-03-01'),'In progress · cannot change');
 assert.equal(lockedNote(locked,'2031-03-01','2031-04-01'),undefined);
 assert.equal(lockedNote(undefined,'2031-01-01','2031-02-01'),undefined);
 assert.equal(lockedNote([{...january,reason:'delivered'}],'2031-01-01','2031-02-01'),'Delivered · cannot change');
 assert.equal(lockedNote([{...january,reason:'fixing'}],'2031-01-01','2031-02-01'),'Fixing has started · cannot change');
});

test('a reason this page does not know still locks its month',()=>{
 assert.equal(lockedNote([{...january,reason:'a_later_reason'}],'2031-01-01','2031-02-01'),'Started · cannot change');
});

test('a proposed term names the started month it would drop',()=>{
 const locked=[{...january,reason:'settled'}];
 assert.equal(droppedLockedPeriod(locked,[january,february]),undefined);
 assert.deepEqual(droppedLockedPeriod(locked,[february]),locked[0]);
 assert.deepEqual(droppedLockedPeriod(locked,[{deliveryFrom:'2031-01-01',deliveryTo:'2031-03-01'}]),locked[0]);
 assert.equal(droppedLockedPeriod(undefined,[]),undefined);
});

test('lifecycle months keep the backend order and name each side',()=>{
 assert.deepEqual(lifecyclePeriodRows({payments:[
  {membershipOrdinal:1,...february,state:'Projected',remainingValue:'275'},
  {membershipOrdinal:0,...january,state:'Paid',remainingValue:'0'}]}),
  [['Second','2031-02-01 to 2031-03-01','Projected','275'],['First','2031-01-01 to 2031-02-01','Paid','0']]);
});
