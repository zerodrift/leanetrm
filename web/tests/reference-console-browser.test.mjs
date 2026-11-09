import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const markup=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
const shell=markup.match(/<nav id="reference-console"[^>]*>.*?<\/nav>/s)[0];
const html=`<link rel="stylesheet" href="/app.css">${shell}`;
const setup=`(async()=>{window.opened=[];window.searched=[];window.blocked=false;
 window.menu=[{id:'curves',name:'Curves & pricing',entries:[{id:'custom-curves',name:'Curve builder',page:'custom-curves'}]},
  {id:'time',name:'Time',entries:[{id:'calendars',name:'Calendars',page:'reference',table:'calendars'},{id:'power',name:'Power TOUs',page:'power'}]}];
 window.console_=(await import('/reference-console.js')).setupReferenceConsole({host:document.querySelector('#reference-console'),menu:()=>menu,open:entry=>opened.push(entry.id),search:text=>searched.push(text),busy:()=>blocked});
 console_.show('power');})()`;
const group=name=>`[...document.querySelectorAll('#reference-console details')].find(d=>d.querySelector('summary').textContent.includes('${name}'))`;
const current=`document.querySelector('#reference-console [aria-current=page]')?.textContent`;
const browser={skip:!browserAvailable,timeout:browserTestTimeout};

test('an entry asks to open and is not marked until the page has moved',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  await evaluate("document.querySelector('#nav-custom-curves').click()");
  assert.deepEqual(await evaluate('opened'),['custom-curves'],'one request to the existing page controller');
  assert.equal(await evaluate(current),'Power TOUs','a refused leave keeps the shown entry marked');
  await evaluate("console_.show('custom-curves')");
  assert.equal(await evaluate(current),'Curve builder');
 });
});
test('the shown page is not opened again',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  await evaluate("document.querySelector('#nav-power').click()");
  assert.deepEqual(await evaluate('opened'),[]);
 });
});
test('only the shown group is open and another can be opened by hand',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  assert.equal(await evaluate(`${group('Time')}.open`),true);
  assert.equal(await evaluate(`${group('Curves')}.open`),false);
  await evaluate(`${group('Curves')}.open=true`);
  await evaluate('console_.sync()');
  assert.equal(await evaluate(`${group('Curves')}.open`),true,'a refresh leaves a group opened by hand');
 });
});
test('typing narrows the menu and submitting searches reference data',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  const type=value=>`{const t=document.querySelector('#reference-search-all-text');t.value='${value}';t.dispatchEvent(new Event('input',{bubbles:true}))}`;
  await evaluate(type('tou'));
  const listed=`[...document.querySelectorAll('#reference-console li')].filter(n=>!n.hidden).map(n=>n.textContent)`;
  assert.deepEqual(await evaluate(listed),['Power TOUs']);
  assert.equal(await evaluate(`${group('Curves')}.hidden`),true,'a group with no match is hidden');
  await evaluate("document.querySelector('#reference-search-all').requestSubmit()");
  assert.deepEqual(await evaluate('searched'),['tou']);
  await evaluate(type(''));
  assert.equal((await evaluate(listed)).length,3);
 });
});
test('reopening goes to the last entry shown, else the first available',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  assert.equal(await evaluate('console_.destination().id'),'power');
  await evaluate("console_.reset()");
  assert.equal(await evaluate('console_.destination().id'),'custom-curves');
  await evaluate('menu=[];console_.sync()');
  assert.equal(await evaluate('console_.destination()'),null);
 });
});
test('entries are disabled while the application is busy',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  await evaluate('blocked=true;console_.sync()');
  assert.equal(await evaluate("document.querySelector('#nav-custom-curves').disabled"),true);
 });
});
test('the menu folds to a slim strip and unfolds',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  const toggle="document.querySelector('#reference-console-toggle')";
  await evaluate(`${toggle}.click()`);
  assert.equal(await evaluate(`${toggle}.getAttribute('aria-expanded')`),'false');
  assert.equal(await evaluate("document.querySelector('#reference-console-body').hidden"),true);
  await evaluate(`${toggle}.click()`);
  assert.equal(await evaluate(`${toggle}.getAttribute('aria-expanded')`),'true');
 });
});
test('the console shows for its pages and hides elsewhere',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  assert.equal(await evaluate("document.querySelector('#reference-console').hidden"),false);
  await evaluate("console_.show('market')");
  assert.equal(await evaluate("document.querySelector('#reference-console').hidden"),true);
  await evaluate("console_.show('reference')");
  assert.equal(await evaluate("document.querySelector('#reference-console').hidden"),false,'reference shows the console before its table is known');
 });
});
