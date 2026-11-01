import test from 'node:test';
import assert from 'node:assert/strict';
import {readPositionFacts,readPositionFactsPage,requirePricedFacts} from '../dist/position-facts.js';

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

test('a later page continues from its offset, and its cursor is checked from there',()=>{
  const later=readPositionFactsPage(page([fact('third')],4,3),1,2);
  assert.deepEqual([later.total,later.nextOffset,later.positions],[4,3,[fact('third')]]);
  assert.equal(readPositionFactsPage(page([fact('last')],4,null),64,3).nextOffset,null);
  for(const [wrong,offset] of [[page([fact('third')],4,1),2],[page([fact('third')],4,null),2],[page([fact('a'),fact('b')],3,null),2],[page([],4,null),2]])
    assert.throws(()=>readPositionFactsPage(wrong,64,offset),/inconsistent page/,JSON.stringify(wrong));
  assert.throws(()=>readPositionFactsPage(page([fact('a')],4,3),64,-1),/inconsistent page/);
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

test('physical gas shares mixed fact pages',()=>{
 const gas={...fact('gas-instrument'),tradeId:'gas-trade',instrumentType:'physical_gas',bookedQuantity:null,bookedUnit:null};
 assert.deepEqual(readPositionFacts(page([fact('ppa-binding'),gas],2,null),2).positions,[fact('ppa-binding'),gas]);
});

test('physical crude keeps its exact total-volume facts in mixed position pages',()=>{
 const crude={...fact('crude-instrument'),tradeId:'crude-trade',instrumentType:'physical_crude',bookedQuantity:'1000',bookedUnit:'bbl'};
 assert.deepEqual(readPositionFacts(page([fact('ppa-binding'),crude],2,null),2).positions,[fact('ppa-binding'),crude]);
});
test('additional booked fact pages preserve one exact context and reject overlaps or changed totals',async()=>{
 const {appendPositionFacts}=await import('../dist/position-facts.js');
 const p=id=>({tradeId:id,bindingId:id,tradeReference:id,bookId:'book',book:'Book',revision:1,instrumentType:'physical_crude',marketLabel:null,deliveryFrom:null,deliveryTo:null,bookedQuantity:'1',bookedUnit:'bbl'});
 const first={context:{contextId:'retained',effectiveOn:'2030-01-01',knownAt:'2030-01-02T00:00:00Z'},asOf:'2030-01-01T00:00:00Z',positions:[p('a'),p('b')],total:3,nextOffset:2};
 const next={...first,positions:[p('c')],nextOffset:null};
 const all=appendPositionFacts(first,next,2);assert.deepEqual(all.positions.map(p=>p.tradeId),['a','b','c']);assert.equal(all.nextOffset,null);
 for(const changed of [{...next,total:4,nextOffset:3},{...next,context:{...next.context,contextId:'other'}},{...next,positions:[p('a')]},{...next,asOf:'2030-01-02T00:00:00Z'}])assert.throws(()=>appendPositionFacts(first,changed,2));
 assert.throws(()=>appendPositionFacts(all,next,2));
});
