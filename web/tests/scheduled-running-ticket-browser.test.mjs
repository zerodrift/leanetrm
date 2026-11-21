import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

test('the ticket of a running scheduled swap keeps started months as booked and extends the term around them',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 const fixture=await readFile(new URL('./scheduled-running-ticket.fixture.mjs',import.meta.url),'utf8');
 await withBrowserPage(`<meta charset="utf-8"><link rel="stylesheet" href="/app.css"><script type="module">${fixture}\nwindow.runningTicketResults=runningTicketChecks().catch(error=>[{name:String(error?.stack??error),passed:false}]);</script>`,async evaluate=>{
  const results=await evaluate('runningTicketResults');assert.deepEqual(results.filter(result=>!result.passed),[]);assert.equal(results.length,9);
 });
});
