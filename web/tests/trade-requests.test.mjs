import test from 'node:test';
import assert from 'node:assert/strict';
import {knownTradeRejection,releasesRetained,retainedText,retryAllowed} from '../dist/trade-requests.js';

test('only refusals that prove nothing was written release a retained booking or amendment',()=>{
  for(const code of ['futures_input','scheduled_quantity','scheduled_swap_input','scheduled_power','swap_input','trade_operation_conflict','access_denied','request_format','request_too_large','request_token'])assert.equal(knownTradeRejection(code),true,code);
  for(const code of [undefined,'request_failed','response_unknown','futures_outcome_unknown','swap_outcome_unknown'])assert.equal(knownTradeRejection(code),false,String(code));
});

test('a request whose outcome is not known is named by what it is, so it can be told from another, and says what can be done about it',()=>{
  assert.equal(retainedText({label:'Swap amendment of TR-000016'}),'Swap amendment of TR-000016 · its outcome is not known. The identical request is retained: check its committed outcome or retry it before entering another booking.');
  assert.equal(retainedText({label:'New futures trade'}).startsWith('New futures trade · '),true);
  assert.equal(retainedText({}).startsWith('A trade request · '),true,'a request kept by an earlier visit that had no name still says what it is');
});

test('a retained settlement command is retried by an account that may amend trades, as an amendment is, and a booking by one that may book',()=>{
  const catalog=(canBook,canAmendTrades)=>({canBook,canAmendTrades});
  for(const path of ['swaps/settlement/determine','swaps/settlement/payment','swaps/settlement/net','futures/amend','swaps/amend']){
    assert.equal(retryAllowed(path,catalog(false,true)),true,path);assert.equal(retryAllowed(path,catalog(true,false)),false,path);
  }
  for(const path of ['futures/book','swaps/book']){assert.equal(retryAllowed(path,catalog(true,false)),true,path);assert.equal(retryAllowed(path,catalog(false,true)),false,path);}
  assert.equal(retryAllowed('swaps/book',undefined),false,'nothing is retried before a catalog says what the account may do');
});

test('a refusal for good releases a retained request on a first send, and on a retry where the backend replays an earlier outcome before it refuses',()=>{
  for(const path of ['swaps/settlement/determine','swaps/settlement/payment','swaps/settlement/net','swaps/book','swaps/amend']){
    for(const code of ['settlement_version_conflict','settlement_stale_preview','swap_input','request_format']){
      assert.equal(releasesRetained(path,code,false),true,`${path} ${code}`);assert.equal(releasesRetained(path,code,true),true,`${path} ${code} on a retry`);
    }
    // A refusal of the caller rather than of the request says nothing of a retried original.
    for(const code of ['access_denied','request_token','historical_read_only'])assert.equal(releasesRetained(path,code,true),false,`${path} ${code} on a retry`);
  }
  // A futures booking or amendment is admitted before its earlier outcome is looked up, so a refused retry says nothing of the original.
  for(const path of ['futures/book','futures/amend'])for(const code of ['futures_execution_policy','futures_conflict','access_denied']){
    assert.equal(releasesRetained(path,code,false),true,`${path} ${code}`);assert.equal(releasesRetained(path,code,true),false,`${path} ${code} on a retry`);
  }
  for(const code of [undefined,'request_failed','swap_outcome_unknown'])for(const retrying of [false,true])assert.equal(releasesRetained('swaps/book',code,retrying),false,String(code));
});
