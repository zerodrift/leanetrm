import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage} from './browser-page.mjs';

// A disposable browser and static server draw a synthetic schedule view in the real grid dependency; no application runtime is attached.
test('a schedule draws as a tree with coloured header groups, decorations and the backend totals',{skip:!browserAvailable,timeout:30000},async()=>{
 await withBrowserPage('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:1200px"></div>',async evaluate=>{
  await evaluate(`(async()=>{
   const {scheduleGridData}=await import('/schedule-grid.js'),{renderDataGrid}=await import('/data-grid.js');
   const col=(id,label,unit,measure)=>({id,label,unit,measure,input:false});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month','day'],modes:['new'],tabs:[],
    sides:[{kind:'physical',pill:'Physical',colour:'gas',min:1,initial:1,max:1,fields:[],columns:[col('quantity','Quantity','quantity','quantity')]}],tradeColumns:[col('net_cash','Net cash',null,'money')]};
   const cells=(q,net)=>({'physical-1.quantity':{value:q,sources:['contract']},'trade.net_cash':{value:net,sources:[]}});
   const row=(key,parent,level,c,delivered)=>({key,parent,level,start:'',end:'',operatingDate:null,cells:c,delivered});
   const before={levels:['month','day'],valuationAt:'',delivered:null,remaining:null,deliveredLeaves:1,leaves:2,totals:cells('-7','99'),
    rows:[row('2026-11',null,'month',cells('-2000','6500'),false),row('2026-11-01','2026-11','day',cells('-1000','3250'),true),row('2026-11-02','2026-11','day',cells('-1000','3250'),false)]};
   const after={...before,rows:before.rows.map(r=>r.key==='2026-11-02'?{...r,cells:cells('-1001','3250')}:r)};
   const g=scheduleGridData(after,d,[{kind:'physical',code:'physical-1',name:'Leg A'}],'instrument','day',{quantityUnit:'MMBtu',instant:at=>at},before);
   window.toggles=[];
   await Promise.race([new Promise((_,fail)=>setTimeout(()=>fail(Error('The grid was not built.')),5000)),new Promise(ready=>renderDataGrid(document.querySelector('#host'),'schedule','synthetic',g.headers,g.rows,g.numeric,()=>false,{keys:g.columns,displayRows:g.displayRows,rowKeys:g.keys,
    layout:{order:[],widths:{},sort:[]},onLayout:()=>{},onReady:ready,headerGroups:g.headerGroups,totals:g.totals,decorate:(r,c)=>g.decorations[r][c],
    tree:{parents:g.parents,open:new Set(['2026-11']),onToggle:(key,open)=>toggles.push([key,open])}}))]);
   await new Promise(r=>setTimeout(r,100));
  })()`);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.grid-group-title')].map(n=>[n.textContent,n.style.getPropertyValue('--group-colour')])"),[['Leg A','var(--side-gas)'],['Trade','var(--side-neutral)']]);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-tableholder .tabulator-row')].map(r=>r.dataset.rowKey)"),['2026-11','2026-11-01','2026-11-02']);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-tableholder .delivered-row')].map(r=>r.dataset.rowKey)"),['2026-11-01']);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-tableholder .grid-changed')].map(n=>n.closest('.tabulator-row').dataset.rowKey)"),['2026-11-02']);
  assert.equal(await evaluate("document.querySelectorAll('.tabulator-tableholder .grid-source').length"),3);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-calcs-holder .tabulator-cell')].map(c=>c.textContent)"),['Total','-7','99'],'the backend totals, not the sum of the rows');
  await evaluate("document.querySelector('.tabulator-data-tree-control').click()");
  assert.deepEqual(await evaluate('toggles'),[['2026-11',false]]);
  assert.deepEqual(await evaluate("[...document.querySelectorAll('.tabulator-tableholder .tabulator-row')].map(r=>r.dataset.rowKey)"),['2026-11']);
 });
});
