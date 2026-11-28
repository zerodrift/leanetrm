import test from 'node:test';
import assert from 'node:assert/strict';
import {neverRetained,offered,policyRequest,reportStatus,unavailable} from '../dist/issue-reports.js';
import {publicationNotice} from '../dist/assistant-actions.js';

const report=(state,extra={})=>({id:'r',kind:'bug',title:'Synthetic title',repository:'synthetic-owner/synthetic-tracker',state,attempt:1,issueNumber:null,issueUrl:null,errorCode:null,decisions:[],...extra});

test('a policy is sent complete: off with no limit, or on with the limit the administrator entered',()=>{
 assert.deepEqual(policyRequest('disabled',''),{mode:'disabled',dailyLimit:null});
 assert.deepEqual(policyRequest('disabled','7'),{mode:'disabled',dailyLimit:null});
 assert.deepEqual(policyRequest('review_required','7'),{mode:'review_required',dailyLimit:7});
 assert.deepEqual(policyRequest('review_required','700'),{mode:'review_required',dailyLimit:700},'the bound is the server\'s to refuse');
 for(const limit of ['','2.5','seven',' 7','-1'])assert.throws(()=>policyRequest('review_required',limit),/daily limit/);
 for(const mode of ['','automatic'])assert.throws(()=>policyRequest(mode,'7'),/Choose/);
});

test('a report offers the decisions the server lists for it, and no others',()=>{
 assert.deepEqual(offered(report('uncertain',{decisions:['look','resend','abandon']})),['look','resend','abandon']);
 assert.deepEqual(offered(report('pending',{decisions:['abandon']})),['abandon']);
 assert.deepEqual(offered(report('uncertain',{decisions:[]})),[],'a state alone offers nothing');
 assert.deepEqual(offered(report('uncertain',{decisions:['abandon','escalate']})),['abandon'],'a decision this screen does not know is not offered');
 assert.deepEqual(offered(report('submitted')),[]);
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

test('a kept request is released only by an answer that proves the server never retained it',()=>{
 for(const code of ['issue_report_input','issue_report_limit','issue_reporting_disabled'])assert.equal(neverRetained(code),true,code);
 for(const code of [undefined,'issue_report_outcome_unknown','issue_report_conflict','issue_tracker_unconfigured','access_denied','request_cancelled'])assert.equal(neverRetained(code),false,String(code));
});

test('authorizing a drafted issue report says where it will be public; other reviewed commands say nothing',()=>{
 assert.equal(publicationNotice({}),null);
 assert.match(publicationNotice({publishesTo:'synthetic-owner/synthetic-tracker'}),/posts this report to synthetic-owner\/synthetic-tracker, where anyone can read it/);
 assert.match(publicationNotice({publishesTo:null}),/no issue tracker connection/);
});
