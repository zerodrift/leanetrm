import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

const html=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
test('the shared context groups the session with its close date and provides distinct knowledge help',()=>{
 const toolbar=html.slice(html.indexOf('id="context-toolbar"'),html.indexOf('id="profile-open"'));
 assert.match(toolbar,/id="market-close-controls"[^>]*aria-label="Market close"/);
 assert.match(toolbar,/id="close-session"[^>]*aria-describedby="close-session-help"/);
 assert.match(toolbar,/id="knowledge-context-summary"[^>]*aria-describedby="knowledge-context-help"/);
 assert.match(toolbar,/id="context-help"/);
 assert.match(toolbar,/id="close-session-help"/);
 assert.match(toolbar,/id="knowledge-context-help"/);
});
