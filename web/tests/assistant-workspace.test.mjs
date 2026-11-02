import test from 'node:test';
import assert from 'node:assert/strict';
import {aiTabs} from '../dist/agent-settings.js';
import {grantPhrase} from '../dist/agent-grants.js';
import {promptRequest} from '../dist/page-prompts.js';
import {newPreferences,parsePreferences} from '../dist/preferences.js';

test('prompt administration is separately delegable',()=>{
  assert.deepEqual(aiTabs(['ManageAssistantPrompts']),['prompts']);
  assert.match(grantPhrase(['ReadMarketData','UseAssistantWorkspace']),/approval/);
  assert.deepEqual(promptRequest('curves',3,'Ask about missing choices.'),{page:'curves',expectedVersion:3,instructions:'Ask about missing choices.'});
  assert.throws(()=>promptRequest('',0,'text'));
  assert.throws(()=>promptRequest('curves',-1,'text'));
});

test('minimizing the assistant keeps it available and remembers its compact layout',()=>{
  const initial=newPreferences('UTC');
  assert.equal(initial.assistantMinimized,false);
  const minimized=parsePreferences(JSON.stringify({...initial,assistantMinimized:true}));
  assert.equal(minimized.assistantMinimized,true);
  assert.equal(minimized.assistantHidden,false);
  assert.throws(()=>parsePreferences(JSON.stringify({...initial,assistantMinimized:'yes'})));
});
