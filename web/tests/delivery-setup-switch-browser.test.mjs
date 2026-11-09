import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {withBrowserPage,browserAvailable,browserTestTimeout} from './browser-page.mjs';

const markup=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
const switcher=markup.match(/<nav id="physical-commodity"[^>]*>.*?<\/nav>/s)[0];
const html=`<link rel="stylesheet" href="/app.css">${switcher}`;

test('the commodity switch shows on Delivery setup and leaves opening to the page controller',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(html,async evaluate=>{
  await evaluate(`(async()=>{window.opened=[];window.allowed=['gas-setup','crude-setup'];window.blocked=false;
   window.delivery=(await import('/delivery-setup-switch.js')).setupDeliverySetupSwitch({open:page=>opened.push(page),available:page=>allowed.includes(page),busy:()=>blocked});
   delivery.show('market');})()`);
  assert.deepEqual(await evaluate('opened'),[],'constructing or hiding the switch reads nothing');
  assert.equal(await evaluate("document.querySelector('#physical-commodity').hidden"),true);
  await evaluate("delivery.show('gas-setup')");
  assert.equal(await evaluate("document.querySelector('#physical-commodity').hidden"),false);
  await evaluate("document.querySelector('#nav-crude-setup').click()");
  assert.deepEqual(await evaluate('opened'),['crude-setup']);
  assert.equal(await evaluate("document.querySelector('#nav-gas-setup').getAttribute('aria-current')"),'page','a refused leave keeps the shown commodity marked');
  await evaluate("delivery.show('crude-setup')");
  assert.equal(await evaluate("document.querySelector('#nav-crude-setup').getAttribute('aria-current')"),'page');
  await evaluate("allowed=['gas-setup'];delivery.sync()");
  assert.equal(await evaluate("document.querySelector('#nav-crude-setup').hidden"),true);
  await evaluate("blocked=true;delivery.sync()");
  assert.equal(await evaluate("document.querySelector('#nav-gas-setup').disabled"),true);
 });
});
