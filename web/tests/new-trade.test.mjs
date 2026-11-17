import test from 'node:test';
import assert from 'node:assert/strict';
import {bookingAllowed,bookingFallback,chooserGroups,instrumentGroups,newProducts,newTradeControl,productAbout,productName,retainedBlock} from '../dist/new-trade.js';

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

// What the page says before it has read a catalog to ask the backend: the view the person is in, said as it is.
test('before any read, the page names the view that cannot book: a live view is live and a historical one historical',()=>{
 const state={contextDirty:false,canBook:true};
 assert.equal(bookingFallback({...state,view:'historical'}),'Historical view is read-only. Return to working data to book or amend.');
 assert.equal(bookingFallback({...state,view:'live'}),'Live view is read-only. Return to working data to book or amend.');
});

test('before any read, a view that cannot book is said before a role that cannot, and a changed context before either',()=>{
 assert.equal(bookingFallback({contextDirty:false,view:'live',canBook:false}),'Live view is read-only. Return to working data to book or amend.');
 assert.equal(bookingFallback({contextDirty:true,view:'live',canBook:false}),'Apply the changed historical context first.');
});

test('before any read, working data says what a role without Book Trade lacks, and otherwise what the person has yet to choose',()=>{
 assert.equal(bookingFallback({contextDirty:false,view:'working',canBook:false}),'Your current role does not have Book Trade permission.');
 assert.equal(bookingFallback({contextDirty:false,view:'working',canBook:true}),'Choose an As of date to book a trade.');
});

test('a retained request holds New trade back, ahead of every other reason, because it must be settled first',()=>{
 assert.deepEqual(newTradeControl({catalog:allowed,retained:true,fallback}),{enabled:false,reason:retainedBlock});
 assert.deepEqual(newTradeControl({catalog:{canBook:false,bookingUnavailableReason:'Historical view is read-only.'},retained:true,fallback}),{enabled:false,reason:retainedBlock});
});

const main={opens:()=>true,links:()=>true,soon:true};
const shape=groups=>groups.map(g=>[g.id,g.name,g.entries.map(e=>e.kind==='product'?e.product:e.id)]);

test('the instruments are grouped in the chooser\'s order',()=>{
 assert.deepEqual(shape(chooserGroups(main)),[
  ['financial','Financial',['futures','options','swaps']],
  ['power','Power',['ppa','rec']],
  ['gas-lng','Gas & LNG',['gas','park-loan','lng']],
  ['oil-liquids','Oil & liquids',['crude']],
  ['structured','Structured',['operating-right','transport','storage']]]);
});

test('every product is listed once and has a name',()=>{
 assert.equal(new Set(newProducts).size,newProducts.length);assert.equal(newProducts.length,10);
 assert.equal(productName('park-loan'),'Park and loan');assert.equal(productName('gas'),'Physical gas');
 assert.equal(productAbout('futures'),'An exchange contract with a defined expiry.');
});

test('New trade keeps one public Park and loan family beside physical gas',()=>{
 const gas=chooserGroups(main).find(g=>g.id==='gas-lng').entries.map(e=>e.product);
 assert.deepEqual(gas.slice(0,2),['gas','park-loan']);
 assert.equal(newProducts.includes('park-custody'),false);
});

test('a window that opens only futures and swaps draws one group and no link or coming-soon entry',()=>{
 assert.deepEqual(shape(chooserGroups({opens:p=>p==='futures'||p==='swaps',links:()=>false,soon:false})),[['financial','Financial',['futures','swaps']]]);
});

test('an account that cannot open Operating facilities is not offered the operating right',()=>{
 const asked=[],structured=chooserGroups({...main,links:page=>{asked.push(page);return false;}}).find(g=>g.id==='structured');
 assert.deepEqual(structured.entries.map(e=>e.kind),['product','soon']);
 assert.deepEqual(asked,['physical-power']);
});

test('the operating right names the page it is entered on and nothing else',()=>{
 const link=instrumentGroups.at(-1).entries[0];
 assert.deepEqual(link,{kind:'link',id:'operating-right',name:'Operating right',about:'Thermal toll, battery or solar output right · entered on Operating facilities',page:'physical-power'});
});

test('the Park and loan introduction keeps title and fee terms explicitly selected',()=>{
 assert.match(productAbout('park-loan'),/explicitly selected title and fee terms/);
 assert.doesNotMatch(productAbout('park-loan'),/independent fixed fee/);
 assert.match(productAbout('transport'),/no adopted rights value.*after booking.*Lifecycle/i);
});

test('unsupported Storage remains coming soon, with what it will hold',()=>{
 const storage=instrumentGroups.at(-1).entries.at(-1);
 assert.equal(storage.kind,'soon');assert.equal(storage.name,'Storage');assert.match(storage.about,/storage contract/i);
});
