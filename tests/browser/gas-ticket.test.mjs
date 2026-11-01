import test from 'node:test';
import assert from 'node:assert/strict';
import {browserPageActions} from './owned-browser.mjs';
import {gasTicket} from './gas-ticket.mjs';

function fixture(readyAt,{selectionError}={}){
 let elapsed=0;const edits=[];
 const options=()=>elapsed>=readyAt?[{value:''},{value:'synthetic_entity@1'}]:[{value:''}];
 const document={
  querySelectorAll(selector){assert.match(selector,/legal_entity.* option$/);return options();},
  querySelector(selector){assert.match(selector,/legal_entity/);return {options:options()};},
 };
 const evaluate=async expression=>expression.startsWith('({status:')?
  {status:'Updating…',errors:['legal_entity: Reference unavailable']}:
  Function('document',`return ${expression}`)(document);
 const {wait}=browserPageActions(evaluate,{pause:async ms=>{elapsed+=ms;},now:()=>elapsed});
 const ticket=gasTicket({evaluate,wait,set:async(selector,value)=>{edits.push({selector,value});if(selectionError)throw selectionError;}});
 return {ticket,edits,elapsed:()=>elapsed};
}

for(const method of ['first','choose']){
 test(`${method} waits for queued reference choices and selects once`,async()=>{
  const f=fixture(12500);
  await f.ticket[method](null,'legal_entity','synthetic_entity@1');
  assert.equal(f.elapsed(),12500);
  assert.equal(f.edits.length,1);
  assert.equal(f.edits[0].value,'synthetic_entity@1');
 });
 test(`${method} keeps a bounded deadline with the visible field refusal`,async()=>{
  const f=fixture(Infinity);
  await assert.rejects(f.ticket[method](null,'legal_entity','synthetic_entity@1'),error=>
   /Timed out after 30000ms/.test(error.message)&&/legal_entity: Reference unavailable/.test(error.message));
  assert.equal(f.elapsed(),30000);assert.deepEqual(f.edits,[]);
 });
 test(`${method} does not retry an uncertain selection`,async()=>{
  const error=Error('Selection response lost'),f=fixture(0,{selectionError:error});
  await assert.rejects(f.ticket[method](null,'legal_entity','synthetic_entity@1'),e=>e===error);
  assert.equal(f.edits.length,1);
 });
}
