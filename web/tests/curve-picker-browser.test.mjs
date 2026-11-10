import test from 'node:test';
import assert from 'node:assert/strict';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const html=`<button id="before">Before</button><div id="holder"><button id="title" type="button">NG prompt</button><div id="panel" hidden><button id="first" type="button">First</button><button id="last" type="button">Last</button></div></div><button id="after">After</button>`;
const setup=`(async()=>{window.picker=(await import('/curve-picker.js')).setupCurvePicker({title:document.querySelector('#title'),panel:document.querySelector('#panel')});document.querySelector('#title').focus();})()`;
const press=key=>`document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'${key}',bubbles:true,cancelable:true}))`;
const open="!document.querySelector('#panel').hidden";
const focused='document.activeElement.id';
const browser={skip:!browserAvailable,timeout:browserTestTimeout};

test('clicking the title opens the panel and moves focus inside',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);
  assert.equal(await evaluate("document.querySelector('#title').getAttribute('aria-expanded')"),'false');
  await evaluate("document.querySelector('#title').click()");
  assert.equal(await evaluate(open),true);
  assert.equal(await evaluate("document.querySelector('#title').getAttribute('aria-expanded')"),'true');
  assert.equal(await evaluate(focused),'first');
 });
});
test('Down opens the panel',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);await evaluate(press('ArrowDown'));
  assert.equal(await evaluate(open),true);
 });
});
test('Escape closes and returns focus to the title',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);await evaluate("document.querySelector('#title').click()");await evaluate(press('Escape'));
  assert.equal(await evaluate(open),false);
  assert.equal(await evaluate(focused),'title');
 });
});
test('Tab stays inside the panel in both directions',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);await evaluate("document.querySelector('#title').click();document.querySelector('#last').focus()");
  await evaluate(press('Tab'));
  assert.equal(await evaluate(focused),'first');
  await evaluate("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Tab',shiftKey:true,bubbles:true,cancelable:true}))");
  assert.equal(await evaluate(focused),'last');
 });
});
test('a click elsewhere closes the panel; close does the same from code',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);await evaluate("document.querySelector('#title').click()");
  await evaluate("document.querySelector('#after').dispatchEvent(new MouseEvent('pointerdown',{bubbles:true}))");
  assert.equal(await evaluate(open),false);
  await evaluate("document.querySelector('#title').click();picker.close()");
  assert.equal(await evaluate(open),false);
 });
});
test('closing from code returns focus to the title when it was inside the panel',browser,async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(setup);await evaluate("document.querySelector('#title').click();picker.close()");
  assert.equal(await evaluate(focused),'title');
  await evaluate("document.querySelector('#title').click();document.querySelector('#after').focus();picker.close()");
  assert.equal(await evaluate(focused),'after','focus elsewhere stays where it is');
 });
});
