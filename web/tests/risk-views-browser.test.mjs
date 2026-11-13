import test from 'node:test';
import assert from 'node:assert/strict';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const html=['risk','risk-pnl','risk-scenarios','risk-var','risk-runs'].map(view=>`<button id="nav-${view}">${view}</button>`).join('');
const setup=`(async()=>{window.shown=[];window.opened=0;window.onRisk=true;window.blocked=false;
 window.views=(await import('/risk-views.js')).setupRiskViews({show:view=>shown.push(view),open:()=>opened++,onPage:()=>onRisk,busy:()=>blocked,available:()=>true});})()`;
const browser={skip:!browserAvailable,timeout:browserTestTimeout};

test('on the Risk page an entry switches the view without opening the page again',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  await evaluate("document.querySelector('#nav-risk-var').click()");
  assert.deepEqual(await evaluate('shown'),['risk-var']);
  assert.equal(await evaluate('opened'),0);
  assert.equal(await evaluate('views.current()'),'risk-var');
  assert.equal(await evaluate("document.querySelector('#nav-risk-var').classList.contains('active')"),false,'no entry is marked away from the page');
 });
});
test('from another page an entry opens Risk and takes effect only when the page has moved',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  await evaluate("onRisk=false;document.querySelector('#nav-risk-runs').click()");
  assert.equal(await evaluate('opened'),1);
  assert.deepEqual(await evaluate('shown'),[],'a refused leave changes nothing');
  await evaluate("onRisk=true;views.page('risk')");
  assert.deepEqual(await evaluate('shown'),['risk-runs']);
  assert.equal(await evaluate("document.querySelector('#nav-risk-runs').classList.contains('active')"),true);
  await evaluate("views.page('market')");
  assert.equal(await evaluate("document.querySelector('#nav-risk-runs').classList.contains('active')"),false);
 });
});
test('arriving at Risk without choosing a view opens Reports',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  await evaluate("views.page('risk')");
  assert.equal(await evaluate('views.current()'),'risk');
  await evaluate("views.select('risk-var');views.page('market');views.page('risk')");
  assert.equal(await evaluate('views.current()'),'risk');
 });
});
test('a busy application admits no switch',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  await evaluate("blocked=true;views.sync();document.querySelector('#nav-risk-var').click()");
  assert.deepEqual(await evaluate('shown'),[]);
  assert.equal(await evaluate("document.querySelector('#nav-risk-var').disabled"),true);
 });
});
