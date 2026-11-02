import test from 'node:test';
import assert from 'node:assert/strict';
import {localTimeValue} from '../dist/user-time.js';
test('local time entry renders the selected user zone instead of appending UTC',()=>{
 assert.equal(localTimeValue('2020-12-18T16:00:00Z','America/Chicago'),'2020-12-18T10:00');
 assert.equal(localTimeValue('2020-12-18T23:00:30Z','Asia/Tokyo',true),'2020-12-19T08:00:30');
});
test('both repeated clocks render the same wall time for explicit backend disambiguation',()=>{
 assert.equal(localTimeValue('2026-11-01T06:30:00Z','America/Chicago'),'2026-11-01T01:30');
 assert.equal(localTimeValue('2026-11-01T07:30:00Z','America/Chicago'),'2026-11-01T01:30');
});

import {zonedInput} from '../dist/user-time.js';
const field=()=>({value:'',step:'1',listeners:{},addEventListener(name,fn){this.listeners[name]=fn;},after(n){this.offsets=n;}});
const setup=(api)=>{const input=field();let zone='America/Chicago';const old=globalThis.document;globalThis.document={createElement:()=>({value:'',hidden:true,listeners:{},addEventListener(name,fn){this.listeners[name]=fn;},setAttribute(){},replaceChildren(){}})};const control=zonedInput(input,()=>zone,api);globalThis.document=old;return {input,control,zone:value=>zone=value};};
test('a resolved wall time preserves its instant when the user switches display to UTC',async()=>{
 let calls=0;const f=setup(async()=>{calls++;return{candidates:[{instant:'2026-11-02T15:00:00Z',offset:'-06:00'}]};});
 f.input.value='2026-11-02T09:00:00';assert.equal(await f.control.instant(),'2026-11-02T15:00:00Z');
 f.zone('UTC');f.control.refresh();assert.equal(f.input.value,'2026-11-02T15:00:00');assert.equal(await f.control.instant(),'2026-11-02T15:00:00Z');assert.equal(calls,1);
});
test('a late timezone inspection cannot accept an edited value or a different display zone',async()=>{
 let release;const f=setup(()=>new Promise(resolve=>release=resolve));f.input.value='2026-11-02T09:00:00';
 const pending=f.control.instant();f.zone('UTC');release({candidates:[{instant:'2026-11-02T15:00:00Z',offset:'-06:00'}]});
 await assert.rejects(pending,/changed/);f.control.refresh();assert.equal(f.input.value,'');
});

test('changing a repeated-hour offset after a preview resolves the newly selected instant',async()=>{
 const old=globalThis.Option;globalThis.Option=class{constructor(text,value){this.text=text;this.value=value;}};
 try{const candidates=[{instant:'2026-11-01T06:30:00Z',offset:'-05:00'},{instant:'2026-11-01T07:30:00Z',offset:'-06:00'}];
 const f=setup(async()=>({candidates}));f.input.value='2026-11-01T01:30:00';await assert.rejects(f.control.instant(),/twice/);
 f.input.offsets.value=candidates[0].instant;assert.equal(await f.control.instant(),candidates[0].instant);
 f.input.offsets.value=candidates[1].instant;f.input.offsets.listeners.change?.();assert.equal(await f.control.instant(),candidates[1].instant);
 }finally{globalThis.Option=old;}
});
test('a nonexistent local time is refused rather than shifted across a DST gap',async()=>{
 const f=setup(async()=>({candidates:[]}));f.input.value='2026-03-08T02:30:00';await assert.rejects(f.control.instant(),/cannot be resolved/);
});
