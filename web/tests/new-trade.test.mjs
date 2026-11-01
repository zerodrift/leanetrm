import test from 'node:test';
import assert from 'node:assert/strict';
import {bookingAllowed,bookingFallback,newTradeControl,retainedBlock} from '../dist/new-trade.js';

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

test('one New trade product choice includes futures, swaps, PPAs and physical gas',async()=>{
 const {newTradeProducts}=await import('../dist/new-trade.js');
 assert.deepEqual(newTradeProducts.map(([key])=>key),['futures','swaps','ppa','gas']);
});

test('Transport, Park and loan and Storage are listed as coming soon: greyed, disabled, tagged and with their hover text',async()=>{
 const old=globalThis.document;
 class Node{constructor(tag){this.tagName=tag.toUpperCase();this.children=[];this.attributes={};this.className='';this.title='';this.disabled=false;this.text='';}
  append(...c){this.children.push(...c);}setAttribute(n,v){this.attributes[n]=String(v);}getAttribute(n){return this.attributes[n]??null;}
  set textContent(v){this.text=String(v);}get textContent(){return this.text+this.children.map(c=>c.textContent).join('');}}
 globalThis.document={createElement:tag=>new Node(tag)};
 try{
  const {soonInstruments}=await import('../dist/new-trade.js');
  const group=soonInstruments();
  assert.equal(group.getAttribute('aria-label'),'Coming soon');
  assert.deepEqual(group.children.map(b=>[b.tagName,b.textContent,b.disabled,b.getAttribute('aria-disabled'),b.className]),
   [['BUTTON','TransportComing soon',true,'true','ticket-soon'],['BUTTON','Park and loanComing soon',true,'true','ticket-soon'],['BUTTON','StorageComing soon',true,'true','ticket-soon']]);
  assert.match(group.children[0].title,/receipt and delivery sides.*shipper contract.*capacity.*fuel/i);
  for(const b of group.children)assert.ok(b.title.length>0&&b.onclick===undefined);
 }finally{globalThis.document=old;}
});
