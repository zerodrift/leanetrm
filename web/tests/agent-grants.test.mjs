import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readsPhrase,grantOutcome,grantItem,inForceRow,historyRow,knownGrantRejection,grantMessage,keptRevoke,keptNote} from '../dist/agent-grants.js';

const vendors=[{code:'anthropic',name:'Anthropic'},{code:'openai',name:'OpenAI'}];
const model={vendor:'anthropic',identifier:'model-one',displayName:'Model one'};
const instant=at=>`at ${at}`;
const grant={id:'g1',agentId:'a1',agentName:'Page guide',version:4,modelId:'m1',modelVersion:2,...model,permissions:['ReadMarketData','ReadTrades'],
  grantedAt:'2026-10-20T12:00:00Z',expiresAt:'2026-10-27T12:00:00Z'};

test('what a grant reads is named from a closed set, in a fixed order; anything else refuses to display',()=>{
  assert.equal(readsPhrase(['ReadMarketData','ReadTrades']),'trades and market data');
  assert.equal(readsPhrase(['ReadMarketData','ReadTradeParties','ReadTrades']),'trades, trade parties and market data');
  assert.equal(readsPhrase(['ReadMarketData']),'market data');
  assert.equal(readsPhrase(['ReadJobs']),'your jobs');
  assert.equal(readsPhrase(['ReadJobs','ReadMarketData']),'market data and your jobs');
  assert.equal(readsPhrase([]),'no workspace data');
  assert.throws(()=>readsPhrase(['ManageAccess']),/agent grants are inconsistent/);
  assert.throws(()=>readsPhrase(['ReadTrades','ReadTrades']),/agent grants are inconsistent/);
});

test("a person's grant names its agent and version, model, reads and term, and its Revoke names the agent",()=>{
  assert.deepEqual(grantItem(grant,vendors,instant),{title:'Page guide · version 4',model:'Model one · model-one (Anthropic)',reads:'Reads trades and market data',
    term:'Allowed at 2026-10-20T12:00:00Z · until at 2026-10-27T12:00:00Z',revoke:'Revoke Page guide'});
  // A vendor the read does not name is an inconsistency, in the grants' own words.
  assert.throws(()=>grantItem({...grant,vendor:'xai'},vendors,instant),/agent grants are inconsistent/);
});

test("a workspace's grants name each grant in force by person, and each change from a closed set",()=>{
  assert.deepEqual(inForceRow({operationId:'o1',delegator:'Synthetic trader',agentName:'Page guide',version:4,...model,permissions:['ReadMarketData'],
    grantedAt:'2026-10-20T12:00:00Z',expiresAt:'2026-10-27T12:00:00Z'},vendors,instant),
  ['Synthetic trader','Page guide · version 4','Model one · model-one (Anthropic)','market data','at 2026-10-20T12:00:00Z','at 2026-10-27T12:00:00Z']);
  const record={operationId:'o2',at:'2026-10-21T08:00:00Z',outcome:'retired',delegator:'Synthetic trader',actor:'Synthetic administrator',agentName:'Page guide',
    version:4,...model,permissions:['ReadMarketData','ReadTrades'],expiresAt:'2026-10-27T12:00:00Z'};
  assert.deepEqual(historyRow(record,vendors,instant),['at 2026-10-21T08:00:00Z','Synthetic trader','Page guide · version 4','Model one · model-one (Anthropic)',
    'trades and market data','at 2026-10-27T12:00:00Z','Ended by Retire','Synthetic administrator']);
  assert.deepEqual(['granted','replaced','revoked','retired'].map(grantOutcome),['Granted','Replaced by a newer grant','Revoked','Ended by Retire']);
  assert.throws(()=>grantOutcome('expired'),/agent grants are inconsistent/);
});

test('only refusals that prove nothing was written release a kept revocation',()=>{
  for(const code of ['grant_input','grant_unknown','grant_conflict','access_denied','request_format','request_too_large','request_token'])
    assert.equal(knownGrantRejection(code),true,code);
  for(const code of [undefined,'grant_outcome_unknown','response_unknown','request_failed','preview_error','request_cancelled'])
    assert.equal(knownGrantRejection(code),false,code);
});

test('a kept revocation is read back only in the form the page stores, and its note names the agent',()=>{
  const stored={operationId:'op',path:'assistant/grants/revoke',body:{grantId:'g1'},uncertain:true,agent:'Page guide'};
  assert.deepEqual(keptRevoke(JSON.stringify(stored)),
    {command:{operationId:'op',path:'assistant/grants/revoke',body:{grantId:'g1'},uncertain:true},agent:'Page guide'});
  // A stored request is always read back as uncertain.
  assert.equal(keptRevoke(JSON.stringify({...stored,uncertain:false})).command.uncertain,true);
  for(const changed of [{path:'assistant/grants/grant'},{body:{grantId:'g1',agentId:'a1'}},{body:{}},{agent:undefined},{operationId:7}])
    assert.equal(keptRevoke(JSON.stringify({...stored,...changed})),undefined,JSON.stringify(changed));
  assert.equal(keptRevoke('not json'),undefined);assert.equal(keptRevoke(null),undefined);
  assert.match(keptNote('Page guide'),/^A revocation of your grant to Page guide has no confirmed outcome\. Retry sends the same request/);
});

test("grant refusals are told in the grants' own words, and other codes keep the page's",()=>{
  assert.match(grantMessage('grant_unknown'),/no such grant/);
  // Only a first attempt is released by a refusal, so no kept request remains to discard.
  assert.match(grantMessage('grant_conflict'),/nothing was revoked/);
  assert.match(grantMessage('access_denied'),/cannot read or revoke agent grants/);
  assert.equal(grantMessage('request_token'),undefined);assert.equal(grantMessage(undefined),undefined);
});
