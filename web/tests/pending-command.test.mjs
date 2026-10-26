import test from 'node:test';
import assert from 'node:assert/strict';
import {PendingCommand} from '../dist/pending-command.js';

test('a confirmed first rejection releases a caller-declared invalid command',()=>{
  const pending=new PendingCommand(code=>code==='access_denied');
  pending.begin('synthetic/save',{name:'Synthetic record'},()=> 'synthetic-operation');
  const previous=pending.sending();
  pending.failed('access_denied',previous);
  assert.equal(pending.command,undefined);
});

test('an uncertain commit survives a denied retry and replays the original payload once',()=>{
  const pending=new PendingCommand(code=>code==='access_denied');
  const original=structuredClone(pending.begin('synthetic/save',{name:'Synthetic record'},()=> 'synthetic-operation'));
  const receipts=new Map();let writes=0;
  function persist(){const id=pending.command.operationId;if(!receipts.has(id))receipts.set(id,++writes);return receipts.get(id);}
  const first=pending.sending();
  assert.equal(persist(),1); // Persisted, then the reply is lost.
  pending.failed(undefined,first);
  const restored=new PendingCommand(code=>code==='access_denied');restored.command=JSON.parse(JSON.stringify(pending.command));
  restored.failed('access_denied',restored.sending());
  assert.deepEqual({...restored.command,uncertain:undefined},{...original,uncertain:undefined});
  pending.command=restored.command;
  assert.equal(persist(),1);
  assert.equal(writes,1);
});

test('an undeclared rejection cannot establish that a dispatched command did not commit',()=>{
  const pending=new PendingCommand(code=>code==='access_denied');
  pending.begin('synthetic/save',{name:'Synthetic record'},()=> 'synthetic-operation');
  pending.failed('unexpected_failure',pending.sending());
  assert.equal(pending.command.operationId,'synthetic-operation');
  assert.equal(pending.command.uncertain,true);
});

test('reload restores every settlement mutation with the exact original identity and payload',async()=>{
  const {restoreTradeRequest}=await import('../dist/pending-command.js');
  for(const path of ['futures/book','futures/amend','swaps/book','swaps/amend','swaps/settlement/determine','swaps/settlement/payment','swaps/settlement/net']){
    const command={operationId:'00000000-0000-0000-0000-000000000001',path,body:{previewHash:'synthetic-hash',amount:'50.00',allocations:[{determinationId:'synthetic-contribution',amount:'-300.00'}]}};
    const saved=JSON.parse(JSON.stringify(command));
    assert.deepEqual(restoreTradeRequest(saved),command);
    const restored=restoreTradeRequest(saved);restored.body.allocations[0].amount='changed';
    assert.equal(saved.body.allocations[0].amount,'-300.00');
  }
  for(const value of [null,'text',{},[],{path:'swaps/settlement/status',operationId:'request',body:{}},{path:'swaps/settlement/preview',operationId:'request',body:{}},{path:'admin/create',operationId:'request',body:{}},
    {path:'swaps/settlement/net',operationId:'request',body:[]},{path:'swaps/book',operationId:7,body:{}},{path:'swaps/book',operationId:'request',body:null},{path:'swaps/book',operationId:'request',body:'text'}])
    assert.equal(restoreTradeRequest(value),undefined,JSON.stringify(value));
});

test('a restored request keeps the label that names it for the person, and only a label that is text',async()=>{
  const {restoreTradeRequest}=await import('../dist/pending-command.js');
  const saved={operationId:'00000000-0000-0000-0000-000000000002',path:'swaps/settlement/net',body:{previewHash:'synthetic-hash'},uncertain:true,label:'Net settlement of TR-000001'};
  assert.deepEqual(restoreTradeRequest(JSON.parse(JSON.stringify(saved))),saved);
  assert.equal('label' in restoreTradeRequest({...saved,label:7}),false);
});
