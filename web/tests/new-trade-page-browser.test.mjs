import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

// New trade's page (D421): the Blotter's section in its entry presentation, with the Details pane filling the dock on the chooser.
const page=width=>`<meta charset="utf-8"><link rel="stylesheet" href="/app.css"><section id="futures-page" data-entry style="width:${width}px"><div id="blotter-dock" class="blotter-dock" style="height:820px"></div></section>`;
const setup=`(async()=>{
 const {setupDock}=await import('/dock.js'),{setupDetailsPane}=await import('/details-pane.js'),{setupNewTrade,chooserGroups}=await import('/new-trade.js');
 const tabs=['summary','grid','details','charts'].map((id,i)=>{const body=document.createElement('div');body.id='blotter-pane-'+id;return {id,label:['Summary','Positions','Details','Charts'][i],body};});
 const group=id=>({kind:'group',tabs:[id],active:id});
 const initial={layout:{kind:'split',direction:'column',ratio:.25,children:[group('summary'),{kind:'split',direction:'row',ratio:.5,children:[group('grid'),{kind:'split',direction:'column',ratio:.5,children:[group('details'),group('charts')]}]}]},collapsed:[]};
 window.dock=setupDock(document.getElementById('blotter-dock'),{tabs,initial,rules:{maxDepth:4,fixed:['grid'],ratioRange:[.05,.95]},narrow:()=>false,maximizable:true,ownCollapse:['details'],changed:()=>{}});
 const chooser={kind:'new',product:null};
 window.pane=setupDetailsPane(document.getElementById('blotter-pane-details'),{unsaved:()=>false,busy:()=>false,step(){},collapsed(){},expand:()=>dock.reveal('details'),changed(){},rest:()=>chooser});
 window.entry=setupNewTrade(pane.shell,{choose(){},link(){},busy:()=>false,offered:()=>true,groups:()=>chooserGroups({opens:()=>true,links:()=>true,soon:true})});
 await pane.show(chooser);entry.show(chooser,'choose');dock.maximize('details');
 await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
})()`;

for(const width of [1280,1440,1920,400])test(`New trade's page shows the chooser alone and unclipped at ${width}px`,{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page(width),async evaluate=>{
  await evaluate(setup);
  assert.equal(await evaluate(`document.querySelectorAll('.instrument-group').length`),5);
  assert.deepEqual(await evaluate(`[...document.querySelectorAll('.dock-head,.dock-bar,.dock-tab,.details-bar,#details-collapse')].filter(n=>n.checkVisibility()).map(n=>n.className||n.id)`),[],'none of the dock\'s or the pane\'s own controls is drawn');
  assert.deepEqual(await evaluate(`['summary','grid','charts'].filter(id=>document.getElementById('blotter-pane-'+id).checkVisibility())`),[]);
  assert.equal(await evaluate(`(()=>{const pane=document.querySelector('#position-details').getBoundingClientRect(),host=document.querySelector('#futures-page').getBoundingClientRect();return pane.width>=host.width-80;})()`),true,'the pane has the page\'s width');
  assert.equal(await evaluate(`(()=>{const panels=document.querySelector('#blotter-pane-details').closest('.dock-panels');return panels.scrollWidth<=panels.clientWidth+1;})()`),true,'nothing scrolls sideways');
  assert.equal(await evaluate(`[...document.querySelectorAll('.instrument:not(.ticket-soon)')].every(b=>b.getBoundingClientRect().height>=44)`),true);
  const rows=await evaluate(`new Set([...document.querySelectorAll('.instrument-group')].map(g=>Math.round(g.getBoundingClientRect().top))).size`);
  assert.equal(rows<5,width>=1280,'the groups share rows where there is room and stack where there is not');
  const tiles=await evaluate(`document.querySelector('#new-trade-product').getBoundingClientRect().height`);
  const strip=await evaluate(`(entry.show({kind:'new',product:'swaps'},'loading'),document.querySelector('#new-trade-product').getBoundingClientRect().height)`);
  assert.ok(strip<(width>=1280?80:tiles/2),`an instrument chosen leaves one compact strip (${strip}px, tiles ${tiles}px)`);
  assert.equal(await evaluate(`document.querySelector('.instrument-about').checkVisibility()`),false);
 });
});

test('the page shows values the Blotter has yet to check again only where the Blotter itself is shown',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page(1280),async evaluate=>{
  await evaluate(setup);
  await evaluate(`document.querySelector('#futures-page').classList.add('checking-cache')`);
  assert.equal(await evaluate(`getComputedStyle(document.querySelector('#blotter-dock')).visibility`),'visible','New trade is not hidden behind the Blotter\'s unchecked values');
  await evaluate(`document.querySelector('#futures-page').removeAttribute('data-entry')`);
  assert.equal(await evaluate(`getComputedStyle(document.querySelector('#blotter-dock')).visibility`),'hidden');
 });
});
