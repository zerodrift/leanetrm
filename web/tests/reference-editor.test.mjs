import test from 'node:test';
import assert from 'node:assert/strict';
import {knownReferenceRejection,restoreReferenceRequest} from '../dist/reference-editor.js';

test('only refusals that prove nothing was written release a retained reference change',()=>{
  // A value past its field's limit is refused as reference_edit, or past the API's request size limit as request_too_large.
  for(const code of ['reference_edit','reference_conflict','reference_economic_change','reference_limit','historical_read_only','access_denied','request_token','request_format','request_too_large'])assert.equal(knownReferenceRejection(code),true,code);
  for(const code of [undefined,'request_failed','response_unknown','reference_outcome_unknown'])assert.equal(knownReferenceRejection(code),false,String(code));
});

test('a reload restores only a retained commit, exactly and as uncertain',()=>{
  const body={table:'units',code:'synthetic_unit',sourceRevisionId:'00000000-0000-0000-0000-00000000000a',baseRevisionId:'00000000-0000-0000-0000-00000000000b',expectedVersion:3,values:{symbol:'Synthetic symbol'}};
  const saved={operationId:'00000000-0000-0000-0000-000000000001',path:'reference/commit',body};
  const restored=restoreReferenceRequest(JSON.parse(JSON.stringify(saved)));
  assert.deepEqual(restored,{...saved,uncertain:true});
  restored.body.values.symbol='changed';
  assert.equal(body.values.symbol,'Synthetic symbol');
  // Requests of the retired separate save and apply steps, and malformed receipts, are discarded.
  for(const value of [null,'text',[],{...saved,path:'reference/save'},{...saved,path:'reference/apply'},{...saved,operationId:''},{...saved,operationId:7},{...saved,body:null},{...saved,body:[]}])
    assert.equal(restoreReferenceRequest(value),undefined,JSON.stringify(value));
});
