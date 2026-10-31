import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readBlotterOperation,createBlotterOperations,requireModeColumns} from '../dist/blotter-operations.js';
import {defaultBlotterArrangement} from '../dist/blotter-layout.js';

const pos='71000000-0000-4000-8000-000000000009:72000000-0000-4000-8000-000000000008',book='d1000000-0000-4000-8000-000000000001',portfolio='d2000000-0000-4000-8000-000000000002';
const examples={
  selectView:{kind:'selectView',name:'Trade rows'},
  chooseScope:{kind:'chooseScope',bookIds:[book],portfolioIds:[portfolio]},
  groupRows:{kind:'groupRows',rowGroup:'market'},
  sort:{kind:'sort',sort:[{key:'quantity',direction:'desc'}]},
  filter:{kind:'filter',query:'power',columns:{market:'east'}},
  chooseColumns:{kind:'chooseColumns',columns:['book','market']},
  selectPosition:{kind:'selectPosition',key:pos},
  details:{kind:'details',open:true},
  arrange:{kind:'arrange',panes:defaultBlotterArrangement()},
  collapsePane:{kind:'collapsePane',pane:'charts',collapsed:true},
  resetLayout:{kind:'resetLayout'},
  popOut:{kind:'popOut'},
  bringBack:{kind:'bringBack'},
  draftCloseOut:{kind:'draftCloseOut',key:pos},
  amend:{kind:'amend',key:pos}};

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
    {kind:'selectView',name:''},{kind:'selectView',name:'   '},{kind:'selectView',name:'x'.repeat(61)},{kind:'selectPosition',key:'t1:b1'},{kind:'amend',key:'t1:b1'},{kind:'draftCloseOut',key:pos+':x'},{kind:'selectPosition',key:''},{kind:'selectView',name:3},
    {kind:'arrange',panes:{layout:{kind:'group',tabs:['summary'],active:'summary'},collapsed:[]}},
    {kind:'arrange',panes:{...defaultBlotterArrangement(),collapsed:['grid']}},{kind:'arrange'}];
  for(const value of refused)assert.throws(()=>readBlotterOperation(value),Error,JSON.stringify(value));
});

test('the dispatcher calls exactly the matching handler with that operation',async()=>{
  const calls=[];
  const handlers=Object.fromEntries(Object.keys(examples).map(kind=>[kind,async op=>{calls.push([kind,op]);}]));
  const dispatch=createBlotterOperations(handlers,{busy:()=>false});
  for(const op of Object.values(examples)){calls.length=0;await dispatch(op);assert.deepEqual(calls,[[op.kind,op]]);}
});
test('a handler failure reaches the caller',async()=>{
  const handlers=Object.fromEntries(Object.keys(examples).map(kind=>[kind,async()=>{}]));
  handlers.popOut=async()=>{throw Error('refused');};
  await assert.rejects(createBlotterOperations(handlers,{busy:()=>false})(examples.popOut,{placed:false}),/refused/);
});

test('a view name follows the saved-view rule, trimmed',()=>{
  assert.deepEqual(readBlotterOperation({kind:'selectView',name:'  Trade rows '}),{kind:'selectView',name:'Trade rows'});
  assert.equal(readBlotterOperation({kind:'selectView',name:'x'.repeat(60)}).name.length,60);
});
test('kind must be an own field, not an inherited one',()=>{
  assert.throws(()=>readBlotterOperation(Object.create({kind:'popOut'})),Error);
  assert.throws(()=>readBlotterOperation(JSON.parse('{"__proto__":{"kind":"popOut"}}')),Error);
});
test('the dispatcher validates the operation itself and looks handlers up only by own key',async()=>{
  const calls=[];
  const handlers=Object.fromEntries(Object.keys(examples).map(kind=>[kind,async op=>{calls.push(op);}]));
  const dispatch=createBlotterOperations(handlers,{busy:()=>false});
  for(const kind of ['constructor','toString','__proto__','hasOwnProperty'])await assert.rejects(dispatch({kind}),Error,kind);
  await assert.rejects(dispatch({kind:'popOut',extra:1}),Error);
  await assert.rejects(dispatch({kind:'collapsePane',pane:'grid',collapsed:true}),Error);
  assert.deepEqual(calls,[]);
});

test('the dispatcher answers what the handler answers, a position selection whether the position is shown, and hands it the caller\'s context',async()=>{
  const seen=[];
  const handlers=Object.fromEntries(Object.keys(examples).map(kind=>[kind,async(op,via)=>{seen.push([kind,via]);}]));
  handlers.selectPosition=async(op,via)=>{seen.push(['selectPosition',via]);return via.placed;};
  const dispatch=createBlotterOperations(handlers,{busy:()=>false});
  assert.equal(await dispatch(examples.selectPosition,{placed:true}),true);
  assert.equal(await dispatch(examples.selectPosition,{placed:false}),false);
  assert.equal(await dispatch(examples.resetLayout,{placed:false}),undefined);
  assert.deepEqual(seen,[['selectPosition',{placed:true}],['selectPosition',{placed:false}],['resetLayout',{placed:false}]]);
});

test('chosen columns must belong to the display mode shown, by the saved-view rules',()=>{
  const trades={mode:'trades',groupBy:[],measures:[]},pivot={mode:'pivot',groupBy:['book'],measures:['presentValue']},prices={mode:'pivot',groupBy:['book'],measures:['markMean']};
  for(const [view,columns] of [[trades,['book','market','memo']],[pivot,['book','presentValue','unit','currency']],[pivot,['book','unit','currency','instrumentType']],
    [prices,['book','markMean','unit','currency','priceUnit']]])
    assert.doesNotThrow(()=>requireModeColumns(view,columns),JSON.stringify([view,columns]));
  for(const [view,columns] of [[trades,['book','tradeCount']],[trades,['book','markMean']],[pivot,['book','presentValue','unit','currency','market']],
    [pivot,['book','presentValue','currency']],[pivot,['book','presentValue','unit']],[prices,['book','markMean','unit','currency']]])
    assert.throws(()=>requireModeColumns(view,columns),/The Blotter operation was refused: /,JSON.stringify([view,columns]));
});

test('the dispatcher refuses every operation while the page is busy, before any handler, and says so',async()=>{
  const calls=[];let busy=true;
  const handlers=Object.fromEntries(Object.keys(examples).map(kind=>[kind,async op=>{calls.push(op.kind);}]));
  const dispatch=createBlotterOperations(handlers,{busy:()=>busy});
  for(const op of Object.values(examples))await assert.rejects(dispatch(op,{placed:false}),/The Blotter operation was refused: the Blotter is busy; try again when it is idle\./,op.kind);
  assert.deepEqual(calls,[]);
  // A malformed operation is refused for what it is, busy or not.
  await assert.rejects(dispatch({kind:'popOut',extra:1},{placed:false}),/popOut takes exactly no fields/);
  busy=false;await dispatch(examples.resetLayout,{placed:false});assert.deepEqual(calls,['resetLayout']);
});
