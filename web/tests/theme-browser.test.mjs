import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';
import {themeAudit} from './theme-audit.mjs';
import {pricedReport} from './parametric-fixture.mjs';
import {historicalReport} from './historical-fixture.mjs';
import {hybridMonteCarloReport} from './hybrid-monte-carlo-fixture.mjs';

// A disposable browser and static server draw synthetic rows in the real grid dependency, whose own stylesheet is a light one.
const page='<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div class="table-workspace"><div id="grouped" style="width:900px"></div></div><div id="paged" style="width:900px"></div><div id="empty" style="width:900px"></div>';
// A computed colour of the theme: what a probe wearing the token computes to.
const tokenColour=name=>`(()=>{const probe=document.body.appendChild(document.createElement('i'));probe.style.background='var(${name})';const colour=getComputedStyle(probe).backgroundColor;probe.remove();return colour;})()`;
const draw=`(async()=>{
 const {renderDataGrid}=await import('/data-grid.js');
 const headers=['Trade','Market','Quantity','Value'],keys=['trade','market','quantity','value'],layout={order:[],widths:{},sort:[{key:'trade',direction:'asc'}]};
 const rows=Array.from({length:8},(_,i)=>['T-'+i,i%2?'Synthetic gas':'Synthetic power',String(100*i),i%3?'-'+i:String(i)]);
 const built=(host,source,options)=>Promise.race([new Promise((_,fail)=>setTimeout(()=>fail(Error('The grid was not built.')),5000)),
  new Promise(ready=>renderDataGrid(document.querySelector(host),host.slice(1),'synthetic',headers,source,[2,3],()=>false,{keys,layout,onLayout:()=>{},onReady:ready,...options}))]);
 const header=(key,count)=>{const node=document.createElement('span');node.textContent=key+' · '+count+' trades';return node;};
 await built('#grouped',rows,{rowKeys:rows.map(row=>row[0]),onRowOpen:()=>{},group:{keys:rows.map(row=>row[1]),order:['Synthetic gas','Synthetic power'],header},totals:[['Total','','2800','-9']],
  headerGroups:[{title:'Leg A',colour:'gas',columns:[2,3]}],soon:{1:'Not drawn yet.'}});
 await built('#paged',rows,{pageSize:3,totals:[['Total','','2800','-9']]});
 await built('#empty',[],{});
 await new Promise(done=>setTimeout(done,100));
})()`;
test('a grid draws its rows, headers, groups, totals, pages and empty state in the dark theme',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(draw);
  for(const part of ['.tabulator-header .tabulator-col','.tabulator-row.tabulator-group','.tabulator-row.openable-row','.tabulator-table .tabulator-row.tabulator-calcs .tabulator-cell','.tabulator-footer .tabulator-calcs-holder .tabulator-cell','.tabulator-paginator .tabulator-page','.tabulator-placeholder-contents','.grid-soon-tag'])
   assert.ok(await evaluate(`document.querySelectorAll(${JSON.stringify(part)}).length>0`),`the grids draw no ${part}`);
  assert.deepEqual(await evaluate(`${themeAudit}(document.body)`),{grounds:[],text:[]});
  // The open row of a workspace grid is marked on the accent's soft ground and keeps its text readable.
  await evaluate("document.querySelector('#grouped .tabulator-row.openable-row').classList.add('open-row','selected-row')");
  assert.equal(await evaluate("getComputedStyle(document.querySelector('#grouped .open-row')).backgroundColor"),await evaluate(tokenColour('--accent-soft')));
  assert.deepEqual(await evaluate(`${themeAudit}(document.body)`),{grounds:[],text:[]});
 });
});
test('native controls take the dark scheme, and a dialog the theme\'s surface rather than the browser\'s own',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/app.css"><label>Book <select><option>Synthetic</option></select></label><input value="Typed" placeholder="Filter rows…"><textarea>Note</textarea><button>Save</button><button class="secondary">Cancel</button><dialog open><h2>Choose</h2><p>Text</p></dialog>',async evaluate=>{
  assert.equal(await evaluate("getComputedStyle(document.documentElement).colorScheme"),'dark');
  assert.equal(await evaluate("getComputedStyle(document.querySelector('dialog')).backgroundColor"),await evaluate(tokenColour('--surface')));
  assert.deepEqual(await evaluate(`${themeAudit}(document.body)`),{grounds:[],text:[]});
 });
});
// The real VaR report views, drawn from the synthetic reports of their own tests, with every collapsed section opened.
test('parametric, historical and Monte Carlo VaR reports are drawn in the dark theme',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="parametric" style="width:1100px"></div><div id="historical" style="width:1100px"></div><div id="hybrid" style="width:1100px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {validateParametric}=await import('/parametric-risk.js'),{newParametricView,renderParametric}=await import('/parametric-risk-view.js'),
    {validateHistorical}=await import('/historical-risk.js'),{newHistoricalView,renderHistorical}=await import('/historical-risk-view.js'),
    {setupRiskResults}=await import('/risk-results-view.js'),{prepareAmountDisplay}=await import('/amount-display.js');
   const shown=async rows=>rows.map(v=>v.value),host=id=>document.querySelector(id);
   const parametric=${JSON.stringify(pricedReport())};await prepareAmountDisplay(parametric,shown);renderParametric(host('#parametric'),validateParametric(parametric),newParametricView(),()=>{},[]);
   const historical=${JSON.stringify(historicalReport())};await prepareAmountDisplay(historical,shown);renderHistorical(host('#historical'),validateHistorical(historical),newHistoricalView(),()=>{},[]);
   const hybrid=${JSON.stringify(hybridMonteCarloReport())};await prepareAmountDisplay(hybrid,shown);
   setupRiskResults(host('#hybrid'),()=>'UTC').show({key:1,queued:false,running:false,retained:false,setupText:'Hybrid valuation',context:hybrid.context,scope:hybrid.monteCarlo.request.history.scope,parts:[{report:hybrid,unknown:false,unlisted:false}]});
   for(const section of document.querySelectorAll('details'))section.open=true;
   await new Promise(done=>setTimeout(done,300));
  })()`);
  for(const report of ['#parametric','#historical','#hybrid'])assert.ok(await evaluate(`document.querySelector('${report}').innerText.length>200`),`${report} draws no report`);
  assert.deepEqual(await evaluate(`${themeAudit}(document.body)`),{grounds:[],text:[]});
 });
});
