import test from 'node:test';
import assert from 'node:assert/strict';
import {knownReferenceRejection} from '../dist/reference-editor.js';

test('only refusals that prove nothing was written release a retained reference change',()=>{
  // A value past its field's limit is refused as reference_edit, or past the API's request size limit as request_too_large.
  for(const code of ['reference_edit','reference_conflict','access_denied','request_token','request_format','request_too_large'])assert.equal(knownReferenceRejection(code),true,code);
  for(const code of [undefined,'request_failed','response_unknown','reference_outcome_unknown'])assert.equal(knownReferenceRejection(code),false,String(code));
});
