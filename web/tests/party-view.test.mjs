import test from 'node:test';
import assert from 'node:assert/strict';
import {knownPartyRejection} from '../dist/party-view.js';

test('only refusals that prove nothing was written release a retained party save',()=>{
  // A name past its 80 characters is refused as trade_party, or past the API's request size limit as request_too_large.
  for(const code of ['party_conflict','party_input','trade_party','access_denied','request_format','request_too_large','request_token'])assert.equal(knownPartyRejection(code),true,code);
  for(const code of [undefined,'request_failed','response_unknown','party_outcome_unknown'])assert.equal(knownPartyRejection(code),false,String(code));
});
