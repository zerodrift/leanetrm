import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const markup=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
const navigation=markup.match(/<nav id="curve-price-navigation"[^>]*>.*?<\/nav>/s)[0];
const html=`<link rel="stylesheet" href="/app.css"><button id="nav-market">Curves &amp; prices</button>${navigation}`;
test('curve views share one entry without reading or leaving before the existing guard accepts',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(`(async()=>{window.opened=[];window.blocked=false;window.allowed=['market','custom-curves','interval-curves'];
   window.workspace=(await import('/curve-workspace.js')).setupCurveWorkspace({open:page=>opened.push(page),available:page=>allowed.includes(page),busy:()=>blocked});workspace.show('dashboard');})()`);
  assert.deepEqual(await evaluate('opened'),[]);
  assert.equal(await evaluate("document.querySelector('#curve-price-navigation').hidden"),true);
  await evaluate("document.querySelector('#nav-market').click();workspace.show('market')");
  assert.deepEqual(await evaluate('opened'),['market']);
  assert.equal(await evaluate("document.querySelector('#curve-price-navigation').hidden"),false);
  await evaluate("document.querySelector('#nav-custom-curves').click()");
  assert.deepEqual(await evaluate('opened'),['market','custom-curves']);
  assert.equal(await evaluate("document.querySelector('#curve-price-contracts').getAttribute('aria-pressed')"),'true','a rejected leave keeps the displayed view selected');
  await evaluate("workspace.show('custom-curves')");
  assert.equal(await evaluate("document.querySelector('#nav-market').classList.contains('active')"),true);
  assert.equal(await evaluate("document.querySelector('#nav-custom-curves').getAttribute('aria-pressed')"),'true');
  await evaluate("document.querySelector('#nav-market').click()");
  assert.deepEqual(await evaluate('opened'),['market','custom-curves'],'the active workspace entry does not reload the editor');
  await evaluate("workspace.show('dashboard');document.querySelector('#nav-market').click()");
  assert.equal(await evaluate('opened.at(-1)'),'custom-curves','return to the last curve view');
  await evaluate("workspace.show('custom-curves');blocked=true;workspace.sync();document.querySelector('#curve-price-contracts').click();document.querySelector('#nav-interval-curves').click()");
  assert.equal(await evaluate('opened.length'),3,'a busy operation admits no move');
  await evaluate("blocked=false;allowed=['market'];workspace.sync()");
  assert.equal(await evaluate("document.querySelector('#nav-custom-curves').hidden"),true);
  await evaluate("workspace.reset();document.querySelector('#nav-market').click()");
  assert.equal(await evaluate('opened.at(-1)'),'market','a new identity starts from its permitted price view');
 });
});

test('same-view navigation delegates pending context to the existing guard without reloading an unchanged view',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(`(async()=>{window.opened=[];window.dirty=true;window.blocked=false;
   window.workspace=(await import('/curve-workspace.js')).setupCurveWorkspace({open:page=>opened.push(page),available:()=>true,busy:()=>blocked,pendingContext:()=>dirty});workspace.show('interval-curves');
   document.querySelector('#nav-interval-curves').click();})()`);
  assert.deepEqual(await evaluate('opened'),['interval-curves']);
  await evaluate("dirty=false;workspace.show('interval-curves');document.querySelector('#nav-interval-curves').click();document.querySelector('#nav-market').click()");
  assert.equal(await evaluate('opened.length'),1);
  await evaluate("dirty=true;document.querySelector('#nav-market').click()");
  assert.deepEqual(await evaluate('opened'),['interval-curves','interval-curves']);
 });
});
