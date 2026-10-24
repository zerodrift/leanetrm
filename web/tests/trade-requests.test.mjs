import test from 'node:test';
import assert from 'node:assert/strict';
import {knownTradeRejection,retainedText} from '../dist/trade-requests.js';

test('only refusals that prove nothing was written release a retained booking or amendment',()=>{
  for(const code of ['futures_input','scheduled_quantity','scheduled_swap_input','scheduled_power','swap_input','trade_operation_conflict','access_denied','request_format','request_too_large','request_token'])assert.equal(knownTradeRejection(code),true,code);
  for(const code of [undefined,'request_failed','response_unknown','futures_outcome_unknown','swap_outcome_unknown'])assert.equal(knownTradeRejection(code),false,String(code));
});

test('a request whose outcome is not known is named by what it is, so it can be told from another, and says what can be done about it',()=>{
  assert.equal(retainedText({label:'Swap amendment of TR-000016'}),'Swap amendment of TR-000016 · its outcome is not known. The identical request is retained: check its committed outcome or retry it before entering another booking.');
  assert.equal(retainedText({label:'New futures trade'}).startsWith('New futures trade · '),true);
  assert.equal(retainedText({}).startsWith('A trade request · '),true,'a request kept by an earlier visit that had no name still says what it is');
});
