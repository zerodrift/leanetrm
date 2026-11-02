import test from 'node:test';
import assert from 'node:assert/strict';
import {setupPilotCloseReport,validatePilotReport} from '../dist/pilot-close-report.js';

test('a saved report must identify the requested occurrence and its exact close',()=>{
 const result={runId:'requested-run',reportId:'requested-run',close:{id:'requested-run'},totals:[],risk:[]};
 assert.equal(validatePilotReport(result,'requested-run'),result);
 for(const malformed of [{...result,reportId:'another-run'},{...result,close:{id:'another-close'}}])
  assert.throws(()=>validatePilotReport(malformed,'requested-run'),/report is incomplete/);
});

test('unapplied date edits block a saved report read and hide its previous result',async()=>{
 const previousDocument=globalThis.document,report={hidden:false};
 globalThis.document={getElementById:id=>{assert.equal(id,'pilot-report');return report;}};
 let reads=0;
 try{
  const view=setupPilotCloseReport({api:async()=>{reads++;throw Error('Unexpected report read.');},identity:()=> 'synthetic-reader',instant:at=>at,busy:()=>false,contextReady:()=>false});
  await assert.rejects(view.load('synthetic-run'),/Apply the changed date context/);
  assert.equal(reads,0);
  assert.equal(report.hidden,true);
 }finally{globalThis.document=previousDocument;}
});
