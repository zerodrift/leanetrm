import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

// The chooser over a stub of the pane's parts: what it draws for each window, and what pressing each entry does.
const setup=`(async()=>{
 const {setupNewTrade,chooserGroups}=await import('/new-trade.js');
 const parts=Object.fromEntries(['head','body','view'].map(name=>{const node=document.createElement('div');document.getElementById('host').append(node);return [name,node];}));
 window.chosen=[];window.linked=[];window.state={busy:false,offered:true,main:true};
 window.entry=setupNewTrade({element:name=>parts[name]},{choose:product=>chosen.push(product),link:page=>linked.push(page),busy:()=>state.busy,offered:()=>state.offered,
  groups:()=>chooserGroups(state.main?{opens:()=>true,links:()=>true,soon:true}:{opens:product=>product==='futures'||product==='swaps',links:()=>false,soon:false})});
 entry.show({kind:'new',product:null},'choose');
})()`;

test('the chooser draws the instruments in their groups, selects none, and says what pressing each does',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="width:1280px"></div>',async evaluate=>{
  await evaluate(setup);
  assert.deepEqual(await evaluate(`[...document.querySelectorAll('.instrument-group-name')].map(n=>n.textContent)`),['Financial','Power','Gas & LNG','Oil & liquids','Structured']);
  assert.deepEqual(await evaluate(`[...document.querySelectorAll('.instrument-group')].map(g=>g.dataset.group)`),['financial','power','gas-lng','oil-liquids','structured']);
  assert.equal(await evaluate(`(n=>n.getAttribute('role')+'|'+n.getAttribute('aria-label')+'|'+n.dataset.state)(document.querySelector('#new-trade-product'))`),'group|Instrument|choose');
  assert.equal(await evaluate(`document.querySelectorAll('#new-trade-product [aria-pressed=true]').length`),0);
  assert.equal(await evaluate(`document.querySelector('#new-trade-futures .instrument-name').textContent+'|'+document.querySelector('#new-trade-futures .instrument-about').textContent`),'Future|An exchange contract with a defined expiry.');
  assert.deepEqual(await evaluate(`(document.querySelector('#new-trade-swaps').click(),document.querySelector('#new-trade-operating-right').click(),[chosen,linked])`),[['swaps'],['physical-power']]);
  assert.equal(await evaluate(`document.querySelector('#new-trade-operating-right').textContent.includes('Thermal toll, battery or solar output right · entered on Operating facilities')`),true);
  assert.equal(await evaluate(`(b=>b.disabled&&b.getAttribute('aria-disabled')==='true'&&b.title.startsWith('Will book a storage contract')&&b.querySelector('.ticket-soon-tag').textContent==='Coming soon'&&!!b.closest('[data-own-controls]'))(document.querySelector('#new-trade-product .ticket-soon'))`),true);
  await evaluate(`entry.show({kind:'new',product:'swaps'},'loading')`);
  assert.deepEqual(await evaluate(`[document.querySelector('#new-trade-product').dataset.state,[...document.querySelectorAll('[aria-pressed=true]')].map(b=>b.id),document.querySelector('#side-panel-title').textContent,document.querySelector('#new-trade-note').textContent,document.querySelector('[role=status]').textContent]`),
   ['chosen',['new-trade-swaps'],'New swap','Fixed or floating prices on independent sides.','Loading the swap choices…']);
 });
});

test('the chooser names what is being read, and its next drawing replaces that',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="width:1280px"></div>',async evaluate=>{
  await evaluate(setup);
  assert.equal(await evaluate(`(entry.loading('futures'),document.querySelector('[role=status]').textContent)`),'Loading the futures contracts…');
  assert.equal(await evaluate(`document.querySelector('#new-trade-product').dataset.state`),'choose');
  assert.equal(await evaluate(`(entry.show({kind:'new',product:null},'choose'),document.querySelector('[role=status]').textContent)`),'Choose an instrument type to enter a new trade.');
 });
});

test('the chooser is disabled while the page is busy, withheld where booking is not offered, and drawn again for another window',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><div id="host" style="width:1280px"></div>',async evaluate=>{
  await evaluate(setup);
  assert.deepEqual(await evaluate(`(state.busy=true,entry.sync(),[document.querySelector('#new-trade-ppa').disabled,document.querySelector('#new-trade-operating-right').disabled,document.querySelector('.ticket-soon').disabled])`),[true,true,true]);
  assert.deepEqual(await evaluate(`(state.busy=false,entry.sync(),[document.querySelector('#new-trade-ppa').disabled,document.querySelector('.ticket-soon').disabled])`),[false,true]);
  assert.equal(await evaluate(`(state.offered=false,entry.sync(),document.querySelector('#new-trade-product').hidden)`),true);
  assert.deepEqual(await evaluate(`(state.offered=true,state.main=false,entry.sync(),[document.querySelector('#new-trade-product').hidden,[...document.querySelectorAll('#new-trade-product button')].map(b=>b.id)])`),[false,['new-trade-futures','new-trade-swaps']]);
 });
});
