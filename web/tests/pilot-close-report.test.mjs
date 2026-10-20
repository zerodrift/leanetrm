import test from 'node:test';
import assert from 'node:assert/strict';
import {validatePilotReport} from '../dist/pilot-close-report.js';

test('a saved report must identify the requested occurrence and its exact close',()=>{
 const result={runId:'requested-run',reportId:'requested-run',close:{id:'requested-run'},totals:[],risk:[]};
 assert.equal(validatePilotReport(result,'requested-run'),result);
 for(const malformed of [{...result,reportId:'another-run'},{...result,close:{id:'another-close'}}])
  assert.throws(()=>validatePilotReport(malformed,'requested-run'),/report is incomplete/);
});
