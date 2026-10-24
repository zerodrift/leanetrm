import test from 'node:test';
import assert from 'node:assert/strict';
import {knownTradeRejection} from '../dist/trade-requests.js';

test('only refusals that prove nothing was written release a retained booking or amendment',()=>{
  for(const code of ['futures_input','swap_input','trade_operation_conflict','access_denied','request_format','request_too_large','request_token'])assert.equal(knownTradeRejection(code),true,code);
  for(const code of [undefined,'request_failed','response_unknown','futures_outcome_unknown','swap_outcome_unknown'])assert.equal(knownTradeRejection(code),false,String(code));
});
