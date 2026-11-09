import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const markup=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
const header=markup.match(/<header>.*?<\/header>/s)[0],status=markup.match(/<div class="status-bar[^"]*">.*?<\/div>/s)[0];
const html=`<link rel="stylesheet" href="/app.css"><main>${header}${status}</main>`;
const lines="(()=>{const h=document.querySelector('#page-title');const s=getComputedStyle(h);return Math.round(h.getBoundingClientRect().height/parseFloat(s.lineHeight));})()";
const browser={skip:!browserAvailable,timeout:browserTestTimeout};

test('a long page title stays on one line',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate("document.querySelector('#context-toolbar').hidden=false;document.querySelector('main').style.width='620px';document.querySelector('#page-title').textContent='Schedules & date mappings'");
  assert.equal(await evaluate(lines),1);
 });
});
test('the status line is announced but not shown',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  assert.equal(await evaluate("document.querySelector('#status').getAttribute('role')"),'status');
  assert.ok(await evaluate("document.querySelector('.status-bar').getBoundingClientRect().height")<=1);
 });
});
