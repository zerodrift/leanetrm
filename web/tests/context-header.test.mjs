import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';

const html=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
test('the shared context groups the session with its close date and provides distinct knowledge help',()=>{
 const toolbar=html.slice(html.indexOf('id="context-toolbar"'),html.indexOf('id="profile-open"'));
 assert.match(toolbar,/id="market-close-controls"[^>]*aria-label="Market close"/);
 assert.match(toolbar,/id="close-session"[^>]*aria-describedby="close-session-help"/);
 assert.match(toolbar,/id="as-of-context-summary"[^>]*aria-describedby="knowledge-context-help"/);
 assert.match(toolbar,/id="context-help"/);
 assert.match(toolbar,/id="close-session-help"/);
 assert.match(toolbar,/id="knowledge-context-help"/);
});
// D384: As of details sits beneath the close or business date it details, and its collapsed line is the close alone.
test('As of details follows the market close and business date inside their own group',()=>{
 const toolbar=html.slice(html.indexOf('id="context-toolbar"'),html.indexOf('id="profile-open"'));
 const group=/<div class="context-as-of">([\s\S]*?)<div id="context-actions"/.exec(toolbar)?.[1];
 assert.ok(group,'the context has no As of group');
 const order=['id="market-close-controls"','id="business-date-field"','class="as-of-widget"'].map(part=>group.indexOf(part));
 assert.ok(order.every(at=>at>=0)&&order[0]<order[1]&&order[1]<order[2],JSON.stringify(order));
 assert.match(group,/<summary id="as-of-context-summary"[^>]*><span id="as-of-close">As of details<\/span><\/summary>/);
});
