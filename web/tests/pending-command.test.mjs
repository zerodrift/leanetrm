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
