import test from 'node:test';
import assert from 'node:assert/strict';
import {tools} from '../dist/assistant-events.js';
import {lookingAt} from '../dist/assistant-pages.js';

test('Looking at announces the server digest only when the chosen version enables it',()=>{
  assert.ok(tools.includes('read_page_digest'));
  const report={page:'curves',selection:{view:'curves',curve:null}},context={mode:'current',frameId:'f1000000-0000-4000-8000-000000000001',frameLabel:'Synthetic close'};
  const names={curve:String,table:String};
  assert.match(lookingAt(report,context,names,{digest:true}).shows,/server.*digest/i);
  assert.doesNotMatch(lookingAt(report,context,names).shows,/digest/i);
});
