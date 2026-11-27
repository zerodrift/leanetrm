import test from 'node:test';
import assert from 'node:assert/strict';
import {navigationGroups,schedulingDesks} from '../dist/navigation-groups.js';
import {availableDesks,deskFor} from '../dist/scheduling-desks.js';

const trades=['ReadMarketData','ReadTrades'];
test('Scheduling lists its entries by desk, in the order the work is done',()=>{
 assert.deepEqual(schedulingDesks.map(desk=>[desk.id,desk.label,[...desk.pages]]),[
  ['gas','Gas',['facilities-position','physical-operations','facilities-transport','facilities']],
  ['power','Power',['rec-inventory']],
  ['crude','Crude',['physical-operations','facilities-crude']],
  ['lng','LNG',['physical-operations']]]);
 const scheduling=navigationGroups.find(group=>group.id==='scheduling');
 assert.deepEqual([...new Set(schedulingDesks.flatMap(desk=>desk.pages))].sort(),[...scheduling.pages].sort());
});
test('a desk is offered only when the account can open one of its entries',()=>{
 assert.deepEqual(availableDesks([]).map(desk=>desk.id),[]);
 assert.deepEqual(availableDesks(['ReadMarketData']).map(desk=>desk.id),[]);
 assert.deepEqual(availableDesks(trades).map(desk=>desk.id),['gas','power','crude','lng']);
});
test('an entry is shown under the current desk when it lists it, else under the first desk that does',()=>{
 assert.equal(deskFor('physical-operations','crude',trades),'crude');
 assert.equal(deskFor('physical-operations','power',trades),'gas');
 assert.equal(deskFor('rec-inventory','gas',trades),'power');
 assert.equal(deskFor('facilities-crude',null,trades),'crude');
 assert.equal(deskFor('market','gas',trades),null);
 assert.equal(deskFor('facilities-position','gas',trades),'gas','the desk stays; the entry itself is refused by its own rule');
});
