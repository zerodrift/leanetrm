import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

const index=readFileSync(new URL('../src/index.html',import.meta.url),'utf8');

test('a narrow desktop risk pane keeps its radio choices and labels inside their borders',{skip:!browserAvailable,timeout:30000},async()=>{
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

test('a narrow position pane stacks its summary even on a wide desktop',{skip:!browserAvailable,timeout:30000},async()=>{
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

test('a reduced desktop workspace wraps the Blotter search and action buttons inside the toolbar',{skip:!browserAvailable,timeout:30000},async()=>{
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
