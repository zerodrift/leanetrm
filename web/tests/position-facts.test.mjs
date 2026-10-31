import test from 'node:test';
import assert from 'node:assert/strict';
import {readPositionFacts,requirePricedFacts} from '../dist/position-facts.js';

const context={contextId:'synthetic-context-1',effectiveOn:'2031-11-01',knownAt:'2031-11-01T12:00:00Z'};
const fact=(bindingId,revision=1)=>({tradeId:'synthetic-trade',bindingId,revision,tradeReference:'Synthetic trade',bookId:'synthetic-book',book:'Synthetic book',
  instrumentType:'ppa',marketLabel:null,deliveryFrom:'2031-11-02T05:00:00Z',deliveryTo:'2031-11-03T06:00:00Z',bookedQuantity:'80',bookedUnit:'MW'});
const page=(positions,total,nextOffset)=>({context,asOf:'2031-11-01T00:00:00Z',positions,total,nextOffset});

test('a first page states the total being valued and refuses an inconsistent page',()=>{
  const first=readPositionFacts(page([fact('first')],2,1),1);
  assert.deepEqual([first.total,first.positions],[2,[fact('first')]]);
  assert.equal(readPositionFacts(page([],0,null),64).total,0);
  // A wrong cursor either way, an empty page of a selection that holds positions, a repeated binding, and more than the limit.
  for(const [wrong,limit] of [[page([fact('first')],2,null),64],[page([fact('first')],1,1),64],[page([],2,0),64],[page([fact('first'),fact('first')],2,null),64],[page([fact('first'),fact('second')],3,2),1]])
    assert.throws(()=>readPositionFacts(wrong,limit),/inconsistent page/);
});

test('current priced results may advance knowledge only while preserving all booked bindings',()=>{
  const facts=readPositionFacts(page([fact('first')],2,1),1);
  const priced={context:{...context,contextId:'synthetic-context-2'},asOf:facts.asOf,holdings:[fact('first'),fact('second')]};
  assert.doesNotThrow(()=>requirePricedFacts(facts,priced,false));
  assert.throws(()=>requirePricedFacts(facts,priced,true),/inconsistent page/);
  assert.throws(()=>requirePricedFacts(facts,{...priced,holdings:[fact('first',2),fact('second')]},false),/positions changed/);
  assert.throws(()=>requirePricedFacts(facts,{...priced,holdings:[fact('first')]},false),/positions changed/);
  assert.throws(()=>requirePricedFacts(facts,{...priced,holdings:[fact('first'),fact('first')]},false),/positions changed/);
});

test('a historical complete response retains the exact fact context and as-of instant',()=>{
  const facts=readPositionFacts(page([fact('first')],1,null),64);
  const priced={context,asOf:facts.asOf,holdings:[fact('first')]};
  assert.doesNotThrow(()=>requirePricedFacts(facts,priced,true));
  assert.throws(()=>requirePricedFacts(facts,{...priced,context:{...context,knownAt:'2031-11-01T13:00:00Z'}},true),/inconsistent page/);
  assert.throws(()=>requirePricedFacts(facts,{...priced,asOf:'2031-11-02T00:00:00Z'},false),/inconsistent page/);
});

