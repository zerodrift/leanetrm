import test from 'node:test';
import assert from 'node:assert/strict';
import {readPositionFacts,requirePricedFacts,readIndividualPosition} from '../dist/position-facts.js';

const context={contextId:'synthetic-context-1',effectiveOn:'2031-11-01',knownAt:'2031-11-01T12:00:00Z'};
const fact=(bindingId,revision=1)=>({tradeId:'synthetic-trade',bindingId,revision,tradeReference:'Synthetic trade',bookId:'synthetic-book',book:'Synthetic book',
  instrumentType:'ppa',marketLabel:null,deliveryFrom:'2031-11-02T05:00:00Z',deliveryTo:'2031-11-03T06:00:00Z',bookedQuantity:'80',bookedUnit:'MW'});
const page=(positions,total,nextOffset)=>({context,asOf:'2031-11-01T00:00:00Z',positions,total,nextOffset});

test('facts pages retain exact binding coverage and quantities without valuation',()=>{
  const first=readPositionFacts(page([fact('first')],2,1),0,1);
  const complete=readPositionFacts(page([fact('second')],2,null),1,1,first);
  assert.equal(first.positions.length,1);
  assert.deepEqual(complete.positions,[fact('first'),fact('second')]);
  assert.equal(complete.positions[0].deliveryFrom,'2031-11-02T05:00:00Z');
  assert.equal(complete.positions[0].bookedQuantity,'80');
});

test('pages refuse changed knowledge, totals, cursors and repeated bindings',()=>{
  const first=readPositionFacts(page([fact('first')],2,1),0,1);
  for(const later of [
    {...page([fact('second')],2,null),context:{...context,contextId:'synthetic-context-2'}},
    page([fact('second')],3,2),page([fact('second')],2,2),page([fact('first')],2,null),
  ])assert.throws(()=>readPositionFacts(later,1,1,first),/inconsistent page/);
});

test('current priced results may advance knowledge only while preserving all booked bindings',()=>{
  const facts=readPositionFacts(page([fact('first')],2,1),0,1);
  const priced={context:{...context,contextId:'synthetic-context-2'},asOf:facts.asOf,holdings:[fact('first'),fact('second')]};
  assert.doesNotThrow(()=>requirePricedFacts(facts,priced,false));
  assert.throws(()=>requirePricedFacts(facts,priced,true),/inconsistent page/);
  assert.throws(()=>requirePricedFacts(facts,{...priced,holdings:[fact('first',2),fact('second')]},false),/positions changed/);
  assert.throws(()=>requirePricedFacts(facts,{...priced,holdings:[fact('first')]},false),/positions changed/);
  assert.throws(()=>requirePricedFacts(facts,{...priced,holdings:[fact('first'),fact('first')]},false),/positions changed/);
});

test('a historical complete response retains the exact fact context and as-of instant',()=>{
  const facts=readPositionFacts(page([fact('first')],1,null),0,64);
  const priced={context,asOf:facts.asOf,holdings:[fact('first')]};
  assert.doesNotThrow(()=>requirePricedFacts(facts,priced,true));
  assert.throws(()=>requirePricedFacts(facts,{...priced,context:{...context,knownAt:'2031-11-01T13:00:00Z'}},true),/inconsistent page/);
  assert.throws(()=>requirePricedFacts(facts,{...priced,asOf:'2031-11-02T00:00:00Z'},false),/inconsistent page/);
});


test('an individual position must match its certified facts, exact binding and revision',()=>{
 const selected=fact('first'),facts=readPositionFacts(page([selected],1,null),0,64);
 const value={context,asOf:facts.asOf,holding:{...selected,revision:'1',presentValue:'123/7'}};
 assert.equal(readIndividualPosition(value,facts,selected).holding.presentValue,'123/7');
 for(const changed of [
  {...value,context:{...context,contextId:'later'}},
  {...value,asOf:'2031-11-02T00:00:00Z'},
  {...value,holding:{...value.holding,bindingId:'second'}},
  {...value,holding:{...value.holding,revision:'2'}},
  {...value,holding:{...value.holding,bookId:'other'}},
  {...value,holding:{...value.holding,instrumentType:'future'}},
 ])assert.throws(()=>readIndividualPosition(changed,facts,selected),/individual position/);
 assert.throws(()=>readIndividualPosition(value,facts,fact('unlisted')),/individual position/);
});
