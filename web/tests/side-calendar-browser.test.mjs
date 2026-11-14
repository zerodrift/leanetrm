import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

test('one side calendar supplies owned swap and PPA schedules without altering linked quantities',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 const fixture=await readFile(new URL('./side-calendar.fixture.mjs',import.meta.url),'utf8');
 await withBrowserPage(`<meta charset="utf-8"><link rel="stylesheet" href="/app.css"><script type="module">${fixture}\nwindow.calendarResults=sideCalendarChecks();</script>`,async evaluate=>{
  const results=await evaluate('calendarResults');assert.deepEqual(results.filter(result=>!result.passed),[]);
 });
});
