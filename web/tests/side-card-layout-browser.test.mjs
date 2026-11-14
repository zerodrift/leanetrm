import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

test('native sided tickets align fields and respond to their panel width',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 const fixture=await readFile(new URL('./side-card-layout.fixture.mjs',import.meta.url),'utf8');
 await withBrowserPage(`<meta charset="utf-8"><link rel="stylesheet" href="/app.css"><script type="module">${fixture}\nwindow.layoutResults=sideCardLayoutChecks();</script>`,async evaluate=>{
  const results=await evaluate('layoutResults');
  assert.deepEqual(results.filter(result=>!result.passed),[]);
 });
});
