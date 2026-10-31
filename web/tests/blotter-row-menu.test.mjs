import {test} from 'node:test';
import assert from 'node:assert/strict';
import {rowMenuItems} from '../dist/blotter-row-menu.js';

const key='71000000-0000-4000-8000-000000000009:72000000-0000-4000-8000-000000000008';
const row=(instrumentType='future')=>({key,instrumentType,reference:'TRD-7'});
const all={book:true,amend:true,ask:true};
const labels=['Close out…','Amend…','Open in its own window','Copy trade ID','Ask the assistant about this position'];
// The menu for a row, and what choosing each item it offers asks for, in order.
function actions(can,type='future'){
  const ran=[];const items=rowMenuItems(row(type),can,action=>ran.push(action));
  for(const item of items)if(item.run&&item.disabled===undefined)item.run();
  return {items,ran};
}

test('the row menu offers the trader’s actions in order, and no hedge',()=>{
  const {items,ran}=actions(all);
  assert.deepEqual(items.map(i=>i.label),labels);
  assert.ok(!items.some(i=>/hedge/i.test(i.label)));
  assert.ok(items.every(i=>i.disabled===undefined));
  assert.deepEqual(ran,[{kind:'draftCloseOut',key},{kind:'amend',key},{kind:'openWindow'},{kind:'copyId'},{kind:'ask'}]);
});

test('an account that cannot book has Close out disabled with the booking reason, and its other actions offered',()=>{
  const why='Return to working data before booking or amending.';
  const {items,ran}=actions({...all,book:false,why:{book:why}});
  assert.deepEqual(items.map(i=>[i.label,i.disabled]),[['Close out…',why],['Amend…',undefined],['Open in its own window',undefined],['Copy trade ID',undefined],['Ask the assistant about this position',undefined]]);
  assert.ok(!ran.some(a=>a.kind==='draftCloseOut'));
});

test('amend and ask are refused with their own reasons',()=>{
  const {items,ran}=actions({...all,amend:false,ask:false,why:{amend:'PPA amendments and terminations are made in PPA bookings.',ask:'Your role cannot use the page assistant.'}},'ppa');
  assert.equal(items[1].disabled,'PPA amendments and terminations are made in PPA bookings.');
  assert.equal(items[4].disabled,'Your role cannot use the page assistant.');
  assert.deepEqual(ran,[{kind:'draftCloseOut',key},{kind:'openWindow'},{kind:'copyId'}]);
});

test('heat rate and PPA rows keep Close out offered: what the backend refuses it says in the form',()=>{
  for(const type of ['heat_rate','ppa']){
    const {items,ran}=actions(all,type);
    assert.equal(items[0].label,'Close out…');assert.equal(items[0].disabled,undefined,type);
    assert.deepEqual(ran[0],{kind:'draftCloseOut',key});
  }
});

test('a row whose values are not current offers nothing, each item saying why',()=>{
  const stale='Positions open once the Blotter has valued them.';
  const {items,ran}=actions({...all,stale});
  assert.deepEqual(items.map(i=>i.label),labels);
  assert.ok(items.every(i=>i.disabled===stale&&i.run===undefined));
  assert.deepEqual(ran,[]);
});

test('a refused action without its reason is an error, never a silent default',()=>{
  for(const can of [{...all,book:false},{...all,amend:false,why:{book:'x'}},{...all,ask:false,why:{}}])
    assert.throws(()=>rowMenuItems(row(),can,()=>{}),/reason/);
});
