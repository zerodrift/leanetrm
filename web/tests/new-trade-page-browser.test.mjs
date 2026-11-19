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
 window.entry=setupNewTrade(pane.shell,{choose(){},link(){},busy:()=>false,offered:()=>true,groups:()=>chooserGroups({opens:()=>true,links:()=>true})});
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
  assert.equal(await evaluate(`[...document.querySelectorAll('.instrument')].every(b=>b.getBoundingClientRect().height>=30)`),true);
  assert.equal(await evaluate(`(b=>b.scrollWidth<=b.clientWidth+1)(document.querySelector('#new-trade-product'))`),true,'the bar wraps and never scrolls sideways');
  const bar=`document.querySelector('#new-trade-product').getBoundingClientRect().height`,unchosen=await evaluate(bar);
  if(width>=1280)assert.ok(unchosen<=150,`the bar is at most two rows where there is room (${unchosen}px)`);
  assert.equal(await evaluate(`(entry.show({kind:'new',product:'swaps'},'loading'),${bar})`),unchosen,'choosing an instrument does not redraw the bar');
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

// A ticket without sides keeps one field per row at a readable width; a new future's price preview sits beside its fields where there
// is room and below them where there is not, and the Book button follows both, as the order a person tabs through them.
const futureTicket=width=>`<meta charset="utf-8"><link rel="stylesheet" href="/app.css"><section id="futures-page" data-entry><div class="dock-panels" style="width:${width}px"><div id="host" class="entry-form"></div></div></section>`;
const mountFuture=`(async()=>{
 const source=new DOMParser().parseFromString(await (await fetch('/index.html')).text(),'text/html'),ticket=source.querySelector('#futures-new-trade');
 document.getElementById('host').append(ticket);ticket.hidden=false;
 for(const id of ['futures-form','futures-draft-valuation'])document.getElementById(id).hidden=false;
 document.getElementById('futures-fields').disabled=false;
 window.box=id=>{const b=document.getElementById(id).getBoundingClientRect();return {left:b.left,right:b.right,top:b.top,bottom:b.bottom,width:b.width};};
})()`;
test('a new future keeps a readable field column with its price preview beside it on a wide page',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(futureTicket(1440),async evaluate=>{
  await evaluate(mountFuture);
  const [terms,preview,book]=await evaluate(`[box('futures-trade-terms'),box('futures-draft-valuation'),box('futures-book-submit')]`);
  assert.ok(terms.width<=560,`the fields are at most 560px wide (${terms.width})`);
  assert.ok(preview.left>=terms.right&&preview.top<terms.bottom,'the preview is beside the fields');
  assert.ok(book.top>=terms.bottom&&book.left<terms.right,'Book follows the fields');
  assert.equal(await evaluate(`[...document.querySelectorAll('#futures-trade-terms select,#futures-trade-terms input:not([type=hidden])')].filter(n=>n.checkVisibility()).every((n,i,all)=>i===0||n.getBoundingClientRect().top>=all[i-1].getBoundingClientRect().bottom-1)`),true,'one field per row');
 });
});
test('a new future\'s price preview goes below its fields where the page is narrow',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(futureTicket(700),async evaluate=>{
  await evaluate(mountFuture);
  const [terms,preview,book]=await evaluate(`[box('futures-trade-terms'),box('futures-draft-valuation'),box('futures-book-submit')]`);
  assert.ok(preview.top>=terms.bottom,'the preview is below the fields');
  assert.ok(book.top>=preview.bottom,'Book follows the preview');
  assert.equal(await evaluate(`(p=>p.scrollWidth<=p.clientWidth+1)(document.querySelector('.dock-panels'))`),true);
 });
});
