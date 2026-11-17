import test from 'node:test';
import assert from 'node:assert/strict';
import {requireTransportFinancialReceipt} from '../dist/gas-transport-financial-lifecycle.js';

const body={operationId:'financial-operation',ownerId:'original-owner',context:{contextId:'original-certified-source'},effectiveAt:'2031-02-03T12:00:00.123456Z',
 command:{kind:'adopt_rights_mark',tradeId:'transport-agreement',financialStateId:'financial-state',observationId:'published-observation',quote:'whole_rights_quote',evidence:'Synthetic source adoption'}};
const request={path:'gas/transport/financial/record',operationId:body.operationId,body,uncertain:true};
const receipt={...body,found:true,tradeId:body.command.tradeId,kind:body.command.kind,outcome:{tradeId:body.command.tradeId,kind:body.command.kind,changed:true}};

test('financial receipt identity preserves exact source and microseconds through replay and semantic acknowledgement',()=>{
 const reordered={...receipt,command:Object.fromEntries(Object.entries(receipt.command).reverse()),effectiveAt:'2031-02-03T12:00:00.1234560+00:00'};
 assert.doesNotThrow(()=>requireTransportFinancialReceipt(request,reordered));
 assert.doesNotThrow(()=>requireTransportFinancialReceipt(request,{...reordered,outcome:{...reordered.outcome,changed:false}}));
 assert.equal(request.uncertain,true);assert.deepEqual(request.body,body);
});

test('a financial reply for another command, owner or exact source cannot release the retained request',()=>{
 for(const change of [value=>{value.operationId='other-operation';},value=>{value.ownerId='other-owner';},value=>{value.tradeId='other-agreement';},
  value=>{value.kind='adopt_capacity_version';},value=>{value.command.observationId='other-observation';},value=>{value.command.financialStateId='other-state';},
  value=>{value.command.quote='other_quote';},value=>{value.effectiveAt='2031-02-03T12:00:00.123457Z';},value=>{value.outcome.tradeId='other-agreement';},
  value=>{value.found=false;},value=>{delete value.command;}]){
  const changed=structuredClone(receipt);change(changed);assert.throws(()=>requireTransportFinancialReceipt(request,changed),/exact retained Transport command/);
 }
 assert.equal(request.uncertain,true);assert.deepEqual(request.body,body);
});
