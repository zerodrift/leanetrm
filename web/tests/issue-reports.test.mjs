import test from 'node:test';
import assert from 'node:assert/strict';
import {policyRequest,reportChoices,reportStatus,unavailable} from '../dist/issue-reports.js';

const report=(state,extra={})=>({id:'r',kind:'bug',title:'Synthetic title',repository:'synthetic-owner/synthetic-tracker',state,attempt:1,issueNumber:null,issueUrl:null,errorCode:null,...extra});

test('a policy is sent complete: off with no limit, or on with the limit the administrator entered',()=>{
 assert.deepEqual(policyRequest('disabled',''),{mode:'disabled',dailyLimit:null});
 assert.deepEqual(policyRequest('disabled','7'),{mode:'disabled',dailyLimit:null});
 assert.deepEqual(policyRequest('review_required','7'),{mode:'review_required',dailyLimit:7});
 for(const limit of ['','2.5','seven',' 7','-1'])assert.throws(()=>policyRequest('review_required',limit),/daily limit/);
 for(const mode of ['','automatic'])assert.throws(()=>policyRequest(mode,'7'),/Choose/);
});

test('a report offers only the decisions its state allows',()=>{
 assert.deepEqual(reportChoices(report('uncertain')),['look','resend','abandon']);
 assert.deepEqual(reportChoices(report('pending',{attempt:0})),['abandon']);
 for(const state of ['dispatching','submitted','failed','abandoned'])assert.deepEqual(reportChoices(report(state)),[]);
});

test('a report says plainly what happened to it, and never claims an issue it does not have',()=>{
 assert.equal(reportStatus(report('submitted',{issueNumber:7,issueUrl:'https://github.com/synthetic-owner/synthetic-tracker/issues/7'})),'Issue #7 created');
 assert.equal(reportStatus(report('pending',{attempt:0})),'Not sent');
 assert.equal(reportStatus(report('dispatching')),'Being sent');
 assert.equal(reportStatus(report('abandoned')),'Given up');
 assert.match(reportStatus(report('uncertain',{errorCode:'outcome_unknown'})),/^Not confirmed/);
 assert.equal(reportStatus(report('failed',{errorCode:'tracker_refused'})),'Not created: the tracker refused it');
 assert.equal(reportStatus(report('failed',{errorCode:'tracker_unavailable'})),'Not created: the tracker could not be reached');
 assert.equal(reportStatus(report('failed',{errorCode:'synthetic_new_code'})),'Not created: synthetic_new_code');
});

test('the form is offered only when the installation has a tracker and the workspace turned reporting on',()=>{
 assert.equal(unavailable('review_required','synthetic-owner/synthetic-tracker'),null);
 assert.match(unavailable('review_required',null),/no issue tracker connection/);
 assert.match(unavailable('disabled','synthetic-owner/synthetic-tracker'),/not enabled/);
 assert.match(unavailable(null,'synthetic-owner/synthetic-tracker'),/not enabled/);
});
