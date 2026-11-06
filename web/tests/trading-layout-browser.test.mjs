import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

const index=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');
const blotterPage=index.replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi,'').replace('</body>','<div id="host" hidden></div></body>');

// The actual page, table and dock, with synthetic rows and no application/API.
async function blotterFixture(work){
 await withBrowserPage(blotterPage,async(evaluate,command)=>{
  await command('Emulation.setDeviceMetricsOverride',{width:2560,height:1200,deviceScaleFactor:1,mobile:false});
  await evaluate(`(async()=>{
   const {setupDock}=await import('/dock.js'),{defaultBlotterArrangement,blotterDockRules}=await import('/blotter-layout.js');
   const {createTableWorkspace}=await import('/table-workspace.js'),{setupDetailsPane}=await import('/details-pane.js');
   const get=id=>document.getElementById(id);
   for(const section of document.querySelectorAll('main>section'))section.hidden=section.id!=='futures-page';
   document.body.classList.add('blotter-shown');get('page-title').textContent='Blotter';get('desk').hidden=false;
   get('blotter-strip').innerHTML='<li class="strip-item"><span class="strip-label">Positions</span><strong class="strip-value">100</strong></li>';
   get('workspace-footer').textContent='Synthetic local workspace';
   window.workspace=createTableWorkspace(get('blotter-workspace'),{key:'blotter',rowMode:'trades',rowLabel:'Positions',dimensions:[['book','Book']],measures:[['value','Value']],scopes:[['books','Books']],
    actions:[['risk','Run risk'],['refresh','Refresh'],['export','Export'],['new','New trade'],['popout','Pop out']],rowGroups:[['book','Book']],pivotHelp:'Synthetic units',onApply(){},onScope(){},body:get('blotter-dock'),rows:get('blotter-pane-grid')});
   get('blotter-rows').id='desk-positions';
   window.dock=setupDock(get('blotter-dock'),{label:'Blotter panes',rules:blotterDockRules,narrow:()=>false,initial:defaultBlotterArrangement(),maximizable:true,ownCollapse:['details'],
    tabs:['summary','grid','details','charts'].map((id,i)=>({id,label:['Summary','Positions','Details','Charts'][i],body:get('blotter-pane-'+id)})),changed(){}});
   dock.collapse('charts');
   window.pane=setupDetailsPane(get('blotter-pane-details'),{unsaved:()=>false,busy:()=>false,step(){},collapsed:()=>dock.collapse('details'),expand:()=>dock.reveal('details'),changed(){}});
   await pane.show({kind:'new',product:'gas'});pane.shell.form(true);
   pane.shell.element('form').innerHTML='<label>Synthetic edit<input value="keep this edit"></label>'+Array.from({length:40},(_,i)=>'<p>Schedule detail '+i+'</p>').join('');
   window.grid=workspace.update('blotter','synthetic',['Trade','Quantity'],Array.from({length:100},(_,i)=>['TR-'+String(i).padStart(6,'0'),'100']),[1],()=>false,{keys:['trade','quantity'],rowKeys:Array.from({length:100},(_,i)=>'trade-'+i),layout:{order:[],widths:{},sort:[]},onLayout(){}});
   window.settle=()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
   window.box=selector=>{const e=typeof selector==='string'?document.querySelector(selector):selector,r=e.getBoundingClientRect();return {top:r.top,bottom:r.bottom,left:r.left,right:r.right,height:r.height,width:r.width,client:e.clientHeight,scroll:e.scrollHeight};};
   await document.fonts.ready;await settle();
  })()`);
  await work(evaluate,command);
  await evaluate('workspace.clear();dock.destroy()');
 });
}

test('the Blotter fills a wide window and uses the space above its compact footer',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await blotterFixture(async evaluate=>{
  const geometry=await evaluate("({frame:box('#blotter-workspace'),footer:box('#workspace-footer'),width:innerWidth,height:innerHeight,scroll:document.documentElement.scrollHeight})");
  assert.ok(geometry.width-geometry.frame.right<=32,JSON.stringify(geometry));
  assert.ok(geometry.height-geometry.frame.bottom<=48,JSON.stringify(geometry));
  assert.ok(geometry.scroll<=geometry.height+1,'the workbench scrolls within panes instead of growing the page');
  await evaluate("document.getElementById('assistant-launcher').hidden=false;document.getElementById('assistant-hide').hidden=false;settle()");
  const footer=await evaluate("({frame:box('#blotter-workspace'),launcher:box('#assistant-launcher'),minimize:box('#assistant-hide'),height:innerHeight})");
  assert.ok(footer.frame.bottom<=Math.min(footer.launcher.top,footer.minimize.top),JSON.stringify(footer));
  assert.ok(footer.height-footer.frame.bottom<=84,JSON.stringify(footer));
  await evaluate("document.body.classList.add('assistant-open');document.documentElement.style.setProperty('--assistant-width','560px');settle()");
  const beside=await evaluate("({frame:box('#blotter-workspace'),width:innerWidth,height:innerHeight})");
  assert.ok(Math.abs(beside.frame.right-(beside.width-560-32))<=1,JSON.stringify(beside));
  assert.ok(beside.height-beside.frame.bottom<=48,JSON.stringify(beside));
 });
});

test('maximized Positions and Details fill the available dock and retain their own scrolling',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await blotterFixture(async(evaluate,command)=>{
  for(const [width,height] of [[2560,1200],[1440,900]]){
   await command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
   for(const id of ['grid','details']){
    await evaluate(`dock.maximize('${id}');grid.relayout();settle()`);
    const geometry=await evaluate(`(()=>{const panels=document.querySelector('#blotter-pane-${id}').closest('.dock-panels');return {dock:box('#blotter-dock'),panel:box(panels),scroll:document.documentElement.scrollHeight,height:innerHeight,
     content:box(${id==='grid'?"'#desk-positions .tabulator-tableholder'":"panels"})};})()`);
    assert.ok(geometry.panel.height>=geometry.dock.height-80,JSON.stringify({id,...geometry}));
    assert.ok(geometry.panel.bottom<=geometry.dock.bottom,JSON.stringify({id,...geometry}));
    assert.ok(geometry.content.height>=geometry.dock.height-(id==='grid'?160:80),JSON.stringify({id,...geometry}));
    assert.ok(geometry.content.scroll>geometry.content.client,JSON.stringify({id,...geometry}));
    assert.ok(geometry.scroll<=geometry.height+1,JSON.stringify({id,...geometry}));
   }
   await evaluate("dock.restore();settle()");
   assert.equal(await evaluate("pane.shell.element('form').querySelector('input').value"),'keep this edit');
  }
 });
});

test('a narrow desktop risk pane keeps its radio choices and labels inside their borders',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<meta charset="utf-8"><link rel="stylesheet" href="/app.css"><main style="width:1100px;margin:0;padding:0"><div id="host" class="dock-panels" style="width:430px"><div class="details-form"></div></div></main>',async(evaluate,command)=>{
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  await evaluate(`(()=>{const source=new DOMParser().parseFromString(${JSON.stringify(index)},'text/html'),report=source.querySelector('#risk-report');report.hidden=false;document.querySelector('.details-form').append(report);})()`);
  const choices=await evaluate("[...document.querySelectorAll('.risk-choice')].map(label=>({label:label.textContent.trim(),width:label.clientWidth,content:label.scrollWidth,radio:label.querySelector('input').getBoundingClientRect().width}))");
  assert.ok(choices.length>=3,'the actual risk report offers its three presets');
  for(const choice of choices){
   assert.ok(choice.radio<=24,`${choice.label}: a radio must retain its native size, got ${choice.radio}px`);
   assert.ok(choice.content<=choice.width+1,`${choice.label}: content extends ${choice.content-choice.width}px outside the choice border`);
  }
 });
});

test('a narrow position pane stacks its summary even on a wide desktop',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(`<meta charset="utf-8"><link rel="stylesheet" href="/app.css"><main style="width:1100px;margin:0;padding:0"><div id="host" class="dock-panels" style="width:430px"><section class="details-pane"><div id="position-details-head">
  <p class="side-panel-kicker">Synthetic trade</p><h2 class="side-panel-title">Scheduled financial swap</h2><p class="side-panel-meta">Revision 2 · amendment</p>
  <div class="side-panel-value"><div class="side-panel-figure"><span>Present value</span><div class="kpi-value">12,345,678.900 <small>USD</small></div></div><div class="side-panel-delta"><span>Largest risk move</span><b>−9,876,543.210 USD</b></div></div>
  <div class="side-panel-actions"><button type="button">Close editor</button><button type="button" class="secondary">Open curve</button></div>
 </div></section></div></main>`,async(evaluate,command)=>{
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  const position=await evaluate("(()=>{const head=document.querySelector('#position-details-head'),title=head.querySelector('.side-panel-title').getBoundingClientRect(),value=head.querySelector('.side-panel-value').getBoundingClientRect(),number=document.createRange();number.selectNodeContents(head.querySelector('.kpi-value').firstChild);return {columns:getComputedStyle(head).gridTemplateColumns.split(' ').length,titleBottom:title.bottom,valueTop:value.top,headWidth:head.clientWidth,contentWidth:head.scrollWidth,numberLines:number.getClientRects().length};})()");
  assert.equal(position.columns,1,'the position summary responds to its pane width, not the desktop viewport');
  assert.ok(position.valueTop>=position.titleBottom,'the value cards follow the title instead of squeezing it alongside');
  assert.ok(position.contentWidth<=position.headWidth+1,'the summary stays inside its own border');
  assert.equal(position.numberLines,1,'a large value keeps its digits together instead of splitting across squeezed KPI cards');
 });
});

test('a reduced desktop workspace wraps the Blotter search and action buttons inside the toolbar',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<meta charset="utf-8"><link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><main style="width:320px;margin:0;padding:0"><div id="host"></div></main>',async(evaluate,command)=>{
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  await evaluate(`(async()=>{
   const {createTableWorkspace}=await import('/table-workspace.js');
   const host=document.querySelector('#host');host.id='blotter-workspace';
   const body=document.createElement('div'),rows=document.createElement('div');body.style.height='320px';rows.style.height='300px';body.append(rows);
   window.workspace=createTableWorkspace(host,{key:'blotter',rowMode:'trades',rowLabel:'Positions',dimensions:[['book','Book']],measures:[['value','Value']],scopes:[['books','Books']],
    actions:[['risk','Run risk'],['refresh','Refresh'],['export','Export'],['new','New trade'],['popout','Pop out']],rowGroups:[['book','Book'],['market','Market']],pivotHelp:'Synthetic units',onApply(){},onScope(){},body,rows});
   workspace.update('blotter','synthetic',['Trade','Price'],[['TR-000001','12.300']],[1],()=>false,{keys:['trade','price'],rowKeys:['trade-a'],layout:{order:[],widths:{},sort:[]},onLayout(){}});
   await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
  })()`);
  const outside=await evaluate("(()=>{const host=document.querySelector('#blotter-workspace').getBoundingClientRect();return [...document.querySelectorAll('.table-search,.table-workspace-actions button')].filter(n=>n.getClientRects().length).flatMap(n=>{const r=n.getBoundingClientRect();return r.left<host.left-1||r.right>host.right+1?[{label:n.textContent||n.getAttribute('aria-label'),left:r.left-host.left,right:r.right-host.right}]:[];});})()");
  assert.deepEqual(outside,[],'the toolbar keeps every visible search/action border inside the available workspace');
  await evaluate('workspace.clear()');
 });
});
