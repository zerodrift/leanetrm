import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readBlotterOperation,createBlotterOperations} from '../dist/blotter-operations.js';
import {defaultBlotterArrangement} from '../dist/blotter-layout.js';

const book='d1000000-0000-4000-8000-000000000001',portfolio='d2000000-0000-4000-8000-000000000002';
const examples={
  selectView:{kind:'selectView',name:'Trade rows'},
  chooseScope:{kind:'chooseScope',bookIds:[book],portfolioIds:[portfolio]},
  groupRows:{kind:'groupRows',rowGroup:'market'},
  sort:{kind:'sort',sort:[{key:'quantity',direction:'desc'}]},
  filter:{kind:'filter',query:'power',columns:{market:'east'}},
  chooseColumns:{kind:'chooseColumns',columns:['book','market']},
  selectPosition:{kind:'selectPosition',key:'t1:b1'},
  details:{kind:'details',open:true},
  arrange:{kind:'arrange',panes:defaultBlotterArrangement()},
  collapsePane:{kind:'collapsePane',pane:'charts',collapsed:true},
  resetLayout:{kind:'resetLayout'},
  popOut:{kind:'popOut'},
  bringBack:{kind:'bringBack'},
  draftCloseOut:{kind:'draftCloseOut',key:'t1:b1'},
  amend:{kind:'amend',key:'t1:b1'}};

test('every operation kind reads back as given',()=>{
  for(const [kind,op] of Object.entries(examples)){assert.equal(op.kind,kind);assert.deepEqual(readBlotterOperation(structuredClone(op)),op);}
});
test('selectPosition accepts no position and groupRows accepts no grouping',()=>{
  assert.deepEqual(readBlotterOperation({kind:'selectPosition',key:null}),{kind:'selectPosition',key:null});
  assert.deepEqual(readBlotterOperation({kind:'groupRows',rowGroup:null}),{kind:'groupRows',rowGroup:null});
});

test('an operation that is not exactly one of the kinds is refused',()=>{
  const refused=[null,'popOut',[],{},{kind:'nope'},{kind:7},{kind:'popOut',extra:1},{kind:'details',open:'yes'},{kind:'details'},
    {kind:'selectPosition',key:3},{kind:'selectPosition'},{kind:'draftCloseOut',key:null},{kind:'amend',key:5},
    {kind:'collapsePane',pane:'grid',collapsed:true},{kind:'collapsePane',pane:'charts',collapsed:'true'},{kind:'collapsePane',pane:'sidebar',collapsed:true},
    {kind:'groupRows',rowGroup:'trader'},{kind:'groupRows'},{kind:'sort',sort:[{key:'quantity',direction:'up'}]},
    {kind:'sort',sort:[{key:'quantity',direction:'asc'},{key:'quantity',direction:'desc'}]},{kind:'sort',sort:[{key:'quantity',direction:'asc',x:1}]},
    {kind:'filter',query:1,columns:{}},{kind:'filter',query:'',columns:{market:1}},{kind:'filter',query:'x'.repeat(201),columns:{}},{kind:'filter',query:'',columns:[]},
    {kind:'chooseColumns',columns:['nonsense']},{kind:'chooseColumns',columns:['book','book']},{kind:'chooseColumns',columns:'book'},
    {kind:'chooseScope',bookIds:['x'],portfolioIds:[]},{kind:'chooseScope',bookIds:[book,book],portfolioIds:[]},{kind:'chooseScope',bookIds:[book]},
    {kind:'selectView',name:''},{kind:'selectView',name:3},
    {kind:'arrange',panes:{layout:{kind:'group',tabs:['summary'],active:'summary'},collapsed:[]}},
    {kind:'arrange',panes:{...defaultBlotterArrangement(),collapsed:['grid']}},{kind:'arrange'}];
  for(const value of refused)assert.throws(()=>readBlotterOperation(value),Error,JSON.stringify(value));
});

test('the dispatcher calls exactly the matching handler with that operation',async()=>{
  const calls=[];
  const handlers=Object.fromEntries(Object.keys(examples).map(kind=>[kind,async op=>{calls.push([kind,op]);}]));
  const dispatch=createBlotterOperations(handlers);
  for(const op of Object.values(examples)){calls.length=0;await dispatch(op);assert.deepEqual(calls,[[op.kind,op]]);}
});
test('a handler failure reaches the caller',async()=>{
  const handlers=Object.fromEntries(Object.keys(examples).map(kind=>[kind,async()=>{}]));
  handlers.popOut=async()=>{throw Error('refused');};
  await assert.rejects(createBlotterOperations(handlers)(examples.popOut),/refused/);
});
