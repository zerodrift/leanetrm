import test from 'node:test';
import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile,mkdtemp,rm,access} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import {tmpdir} from 'node:os';
import {join,resolve,extname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {waitForBrowserPage,stopOwnedBrowserGroup} from '../../tests/browser/owned-browser.mjs';

const browser=process.env.CHROME_BIN??'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
const available=await access(browser).then(()=>true,()=>false);
// A disposable browser and static server draw a synthetic schedule view in the real grid dependency; no application runtime is attached.
test('a schedule draws as a tree with coloured header groups, decorations and the backend totals',{skip:!available,timeout:30000},async()=>{
 const root=fileURLToPath(new URL('../dist/',import.meta.url));
 const server=createServer(async(req,res)=>{
  try{
   if(req.url==='/'){res.setHeader('Content-Type','text/html');res.end('<link rel="stylesheet" href="/vendor/tabulator.css"><link rel="stylesheet" href="/app.css"><div id="host" style="width:1200px"></div>');return;}
   const file=resolve(root,'.'+new URL(req.url,'http://localhost').pathname);
   if(!file.startsWith(root)){res.writeHead(404).end();return;}
   res.setHeader('Content-Type',extname(file)==='.js'?'text/javascript':extname(file)==='.css'?'text/css':'application/octet-stream');res.end(await readFile(file));
  }catch{res.writeHead(404).end();}
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const directory=await mkdtemp(join(tmpdir(),'openetrm-schedule-'));
 const chrome=spawn(browser,['--headless=new','--no-first-run','--no-default-browser-check','--remote-debugging-port=0',`--user-data-dir=${directory}`,'about:blank'],{stdio:'ignore',detached:true});
 let ws;
 try{
  ws=new WebSocket(await waitForBrowserPage(directory,chrome));await new Promise((r,j)=>{ws.onopen=r;ws.onerror=j;});
  let id=0;const pending=new Map();ws.onmessage=e=>{const m=JSON.parse(e.data);if(m.id){pending.get(m.id)(m);pending.delete(m.id);}};
  const command=(method,params={})=>new Promise(r=>{pending.set(++id,r);ws.send(JSON.stringify({id,method,params}));});
  const evaluate=async expression=>{const reply=await command('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});assert.ok(!reply.error&&!reply.result.exceptionDetails,JSON.stringify(reply));return reply.result.result.value;};
  await command('Page.enable');
  await command('Page.navigate',{url:`http://127.0.0.1:${server.address().port}/`});
  for(let i=0;i<100&&!await evaluate('!!document.querySelector("#host")');i++)await new Promise(r=>setTimeout(r,20));
  await evaluate(`(async()=>{
   const {scheduleGridData}=await import('/schedule-grid.js'),{renderDataGrid}=await import('/data-grid.js');
   const col=(id,label,unit,measure)=>({id,label,unit,measure,input:false});
   const d={product:'synthetic_flow',version:1,rowShape:'flow',tradeFields:[],sideLimits:[],levels:['month','day'],modes:['new'],tabs:[],
    sides:[{kind:'physical',pill:'Physical',colour:'gas',min:1,max:1,fields:[],columns:[col('quantity','Quantity','MMBtu','quantity')]}],tradeColumns:[col('net_cash','Net cash',null,'money')]};
   const cells=(q,net)=>({'physical-1.quantity':{value:q,sources:['contract']},'trade.net_cash':{value:net,sources:[]}});
   const row=(key,parent,level,c,delivered)=>({key,parent,level,start:'',end:'',operatingDate:null,cells:c,delivered});
   const before={levels:['month','day'],valuationAt:'',delivered:null,remaining:null,deliveredLeaves:1,leaves:2,totals:cells('-7','99'),
    rows:[row('2026-11',null,'month',cells('-2000','6500'),false),row('2026-11-01','2026-11','day',cells('-1000','3250'),true),row('2026-11-02','2026-11','day',cells('-1000','3250'),false)]};
   const after={...before,rows:before.rows.map(r=>r.key==='2026-11-02'?{...r,cells:cells('-1001','3250')}:r)};
   const g=scheduleGridData(after,d,[{kind:'physical',code:'physical-1',name:'Leg A'}],'instrument','day',before);
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
 }finally{ws?.close();await stopOwnedBrowserGroup(chrome);await new Promise(r=>server.close(r));await rm(directory,{recursive:true,force:true});}
});
