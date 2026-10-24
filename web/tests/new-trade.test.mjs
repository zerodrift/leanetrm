import test from 'node:test';
import assert from 'node:assert/strict';
import {bookingAllowed,newTradeControl,retainedBlock} from '../dist/new-trade.js';

const fallback='Choose an As of date to book a trade.';
const allowed={canBook:true,bookingUnavailableReason:null};

test('New trade is offered to an account that may book at the working As of date, and says nothing',()=>{
 assert.deepEqual(newTradeControl({catalog:allowed,retained:false,fallback}),{enabled:true,reason:null});
});

test('New trade follows the backend: a view that cannot book, or an account without the permission, is told why in the backend\'s words',()=>{
 const historical='Historical view is read-only. Return to working data, then select an As of date.';
 assert.deepEqual(newTradeControl({catalog:{canBook:false,bookingUnavailableReason:historical},retained:false,fallback}),{enabled:false,reason:historical});
 const role='Your current role does not have Book Trade permission.';
 assert.deepEqual(newTradeControl({catalog:{canBook:false,bookingUnavailableReason:role},retained:false,fallback}),{enabled:false,reason:role});
});

test('a backend that refuses booking without saying why is reported as having said nothing, never as a blank',()=>{
 const control=newTradeControl({catalog:{canBook:false,bookingUnavailableReason:null},retained:false,fallback});
 assert.equal(control.enabled,false);
 assert.equal(control.reason,'Booking is not available in this view, and the backend did not say why.');
});

test('a catalog the backend returned allows booking, or says why not in its own words, which a form opening from it asks before it opens a field',()=>{
 assert.deepEqual(bookingAllowed(allowed),{enabled:true,reason:null});
 assert.deepEqual(bookingAllowed({canBook:false,bookingUnavailableReason:'Historical view is read-only.'}),{enabled:false,reason:'Historical view is read-only.'});
 assert.equal(bookingAllowed({canBook:false}).reason,'Booking is not available in this view, and the backend did not say why.');
});

test('before the Blotter is read, New trade says what the page needs, and it is not the backend that is asked',()=>{
 assert.deepEqual(newTradeControl({catalog:undefined,retained:false,fallback}),{enabled:false,reason:fallback});
});

test('a retained request holds New trade back, ahead of every other reason, because it must be settled first',()=>{
 assert.deepEqual(newTradeControl({catalog:allowed,retained:true,fallback}),{enabled:false,reason:retainedBlock});
 assert.deepEqual(newTradeControl({catalog:{canBook:false,bookingUnavailableReason:'Historical view is read-only.'},retained:true,fallback}),{enabled:false,reason:retainedBlock});
});
