import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const mount=read=>`(async()=>{const {renderIndexPublications}=await import('/reference-detail.js');window.opened=[];renderIndexPublications(document.querySelector('#item'),${JSON.stringify(read)},id=>opened.push(id));})()`;
const browser={skip:!browserAvailable,timeout:browserTestTimeout};

test('a price index lists the publications that publish it, and each opens that publication',browser,async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="item" class="reference-item"><h2>Synthetic gas hub</h2></div>',async evaluate=>{
  await evaluate(mount({publications:[{id:'daily',version:2,name:'Synthetic daily report',publisher:'Synthetic publisher',symbol:'NEW',statistic:'final'},{id:'other',version:1,name:'Synthetic second report',publisher:'Synthetic second publisher',symbol:'SECOND',statistic:'final'}]}));
  assert.equal(await evaluate("document.querySelector('.item-publications h3').textContent"),'Published by');
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.item-publications tbody tr')].map(r=>[...r.cells].map(c=>c.textContent))"),
   [['Synthetic publisher','Synthetic daily report','NEW','final'],['Synthetic second publisher','Synthetic second report','SECOND','final']]);
  await evaluate("document.querySelector('.item-publications [data-publication=other]').click()");
  assert.deepEqual(await evaluate('opened'),['other']);
  await evaluate(mount({publications:[]}));
  assert.equal(await evaluate("document.querySelectorAll('.item-publications').length"),1,'drawn again, it replaces what it drew');
  assert.equal(await evaluate("document.querySelector('.item-publications p').textContent"),'No saved publication publishes this index.');
  assert.equal(await evaluate("document.querySelector('#item h2').textContent"),'Synthetic gas hub','the index\'s own page stays');
 });
});
test('publications that could not be read say why and are not shown as none',browser,async()=>{
 await withBrowserPage('<div id="item"></div>',async evaluate=>{
  await evaluate(mount({unavailable:'Its publications could not be read. Synthetic refusal.'}));
  assert.equal(await evaluate("document.querySelector('.item-publications').textContent"),'Published byIts publications could not be read. Synthetic refusal.');
 });
});
