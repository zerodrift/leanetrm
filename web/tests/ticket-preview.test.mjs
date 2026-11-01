import test from 'node:test';
import assert from 'node:assert/strict';
import {previewed,refusedField} from '../dist/ticket-preview.js';

const view=q=>({levels:['month'],rows:[],totals:{'leg-1.quantity':{value:q,sources:[]}},delivered:null,remaining:null,deliveredLeaves:0,leaves:1,valuationAt:'2026-10-30T00:00:00Z'});
const summaries=[{side:'leg-1',billedQuantity:'1',amount:'2',againstMarket:null}];
const empty={schedule:null,previous:null,summaries:[],status:{kind:'current',text:'Not previewed yet.'},errors:{}};
const refusal=(message,details)=>Object.assign(Error(message),{code:'numeric_format',details});

test('a refusal names its field as the ticket keys it, or none',()=>{
 assert.equal(refusedField(refusal('x',{code:'numeric_format',side:'leg-1',field:'quantity'})),'leg-1.quantity');
 assert.equal(refusedField(refusal('x',{code:'ticket_input',side:null,field:'book'})),'book');
 assert.equal(refusedField(refusal('x',{code:'ticket_input'})),null);
 assert.equal(refusedField(Error('offline')),null);
});

test('a preview draws its schedule, and the schedule it replaces marks what changed',()=>{
 const first=previewed(empty,{preview:{schedule:view('1'),summaries,scheduleRefusal:null}});
 assert.deepEqual(first,{schedule:view('1'),previous:null,summaries,status:{kind:'current',text:'Up to date · previewed after your last change'},errors:{}});
 const second=previewed(first,{preview:{schedule:view('2'),summaries:[],scheduleRefusal:null}});
 assert.deepEqual([second.schedule,second.previous],[view('2'),view('1')]);
});

test('a refused preview keeps the last schedule and summaries, stale, with the reason on the field it names',()=>{
 const good=previewed(empty,{preview:{schedule:view('1'),summaries,scheduleRefusal:null}});
 const refused=previewed(good,{refused:refusal('Supply a plain decimal.',{side:'leg-1',field:'quantity'})});
 assert.deepEqual(refused,{...good,status:{kind:'stale',text:'Preview refused: Supply a plain decimal.'},errors:{'leg-1.quantity':'Supply a plain decimal.'}});
 assert.deepEqual(previewed(empty,{refused:Error('offline')}),{...empty,status:{kind:'refused',text:'Preview refused: offline'},errors:{}});
});

test('a valuation without a schedule says why and keeps the last schedule',()=>{
 const good=previewed(empty,{preview:{schedule:view('1'),summaries,scheduleRefusal:null}});
 const kept=previewed(good,{preview:{schedule:null,summaries:null,scheduleRefusal:{code:'schedule_view',message:'The sides keep different gas-day clocks.'}}});
 assert.deepEqual(kept,{...good,status:{kind:'stale',text:'Schedule refused: The sides keep different gas-day clocks.'},errors:{}});
 assert.equal(previewed(empty,{preview:{schedule:null,summaries:null,scheduleRefusal:{code:'schedule_view',message:'No grid.'}}}).status.kind,'refused');
 assert.throws(()=>previewed(good,{preview:{schedule:null,summaries:null,scheduleRefusal:null}}),/neither/);
});
