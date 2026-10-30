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
// A disposable browser and static server exercise the actual chart dependency with synthetic server answers.
// No application runtime, account or database is attached.
test('time chart and overlay controls in the real chart library',{skip:!available,timeout:30000},async()=>{
 const root=fileURLToPath(new URL('../dist/',import.meta.url));
 const server=createServer(async(req,res)=>{
  try{
   if(req.url==='/'){res.setHeader('Content-Type','text/html');res.end('<link rel="stylesheet" href="/app.css"><div id="menu"></div><div id="host" style="width:900px"></div>');return;}
   const file=resolve(root,'.'+new URL(req.url,'http://localhost').pathname);
   if(!file.startsWith(root)){res.writeHead(404).end();return;}
   res.setHeader('Content-Type',extname(file)==='.js'?'text/javascript':extname(file)==='.css'?'text/css':'application/octet-stream');res.end(await readFile(file));
  }catch{res.writeHead(404).end();}
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const directory=await mkdtemp(join(tmpdir(),'openetrm-chart-'));
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
   window.m=await import('/time-chart.js');window.k=await import('/vendor/klinecharts.js');
   window.catalog={policy:'synthetic',overlays:[{code:'simple_moving_average_v1',label:'Simple moving average',parameters:[{name:'window',label:'Window',minimum:2,maximum:250}],seriesNames:['value']}]};
   window.applied=[];window.menu=m.overlayMenu(document.querySelector('#menu'),catalog,r=>applied.push(r));
   window.points=Array.from({length:80},(_,i)=>({at:new Date(Date.UTC(2026,0,i+1)).toISOString(),value:String(i+10)}));
   window.spec={title:'Synthetic prices',unit:'u',zone:'UTC',points,overlays:[{code:'simple_moving_average_v1',label:'Simple moving average',parameters:{window:2},policy:'synthetic',series:{value:points.map((p,i)=>({...p,value:i?'17.25':null,reason:i?null:'window_incomplete'}))}},{code:'rolling_range_v1',label:'Rolling range',parameters:{window:2},policy:'synthetic',series:{high:points,low:points}}]};
   window.view=m.renderTimeChart(document.querySelector('#host'),spec);window.chart=k.init(document.querySelector('.time-chart-canvas'));
   await new Promise(r=>setTimeout(r,200));
  })()`);
  assert.deepEqual(await evaluate("chart.getIndicators().map(i=>i.result.length)"),[80,80,80]);
  assert.equal(await evaluate("chart.getIndicators()[1].result[1].value"),17.25,'plots the supplied answer, without calculating an average');
  assert.equal(await evaluate("chart.getIndicators()[1].result[0].value===undefined"),true,'preserves the server gap');
  await evaluate(`(()=>{document.querySelector('#menu input[type=checkbox]').click();const input=document.querySelector('#menu input:not([type=checkbox])');input.value='3';input.dispatchEvent(new Event('input'));document.querySelector('#menu form').requestSubmit();[...document.querySelectorAll('#menu button')].find(b=>b.textContent==='Remove overlays').click();})()`);
  await evaluate("document.querySelectorAll('#menu button,#menu input').forEach(n=>n.disabled=false);menu.sync(false)");
  assert.equal(await evaluate("document.querySelector('#menu input:not([type=checkbox])').disabled"),true,'unchecked parameters stay disabled after the page busy reset');
  assert.deepEqual(await evaluate('applied'),[[{code:'simple_moving_average_v1',parameters:{window:3}}],[]]);
  assert.equal(await evaluate("document.querySelector('#menu input:not([type=checkbox])').value"),'','removing overlays clears their old parameters');
  assert.equal(await evaluate("document.querySelector('#menu button[type=submit]').disabled"),true);
  await evaluate(`(()=>{const toggle=document.querySelector('#menu input[type=checkbox]'),input=document.querySelector('#menu input:not([type=checkbox])');toggle.click();input.value='2';input.dispatchEvent(new Event('input'));document.querySelector('#menu form').requestSubmit();toggle.click();})()`);
  assert.equal(await evaluate("document.querySelector('#menu button[type=submit]').disabled"),false,'the final overlay can be unchecked and applied');
  await evaluate("document.querySelector('#menu form').requestSubmit()");
  assert.deepEqual(await evaluate('applied.at(-1)'),[]);
  await evaluate("chart.setBarSpace(20);document.querySelector('#host').style.width='850px'");
  await evaluate('new Promise(r=>setTimeout(r,100))');
  assert.equal(await evaluate('chart.getBarSpace().bar'),20,'resizing preserves user zoom');
  await evaluate(`(()=>{window.strokes=[];const proto=CanvasRenderingContext2D.prototype;for(const method of ['beginPath','moveTo','lineTo','stroke']){const original=proto[method];proto[method]=function(...args){if(method==='beginPath')this.testPath=[];else if(method==='stroke')strokes.push({color:this.strokeStyle,path:this.testPath});else this.testPath?.push([method,...args]);return original.apply(this,args);};}view.update({...spec,points:points.slice(0,3),overlays:[{...spec.overlays[0],series:{value:spec.overlays[0].series.value.slice(0,3)}}]});})()`);
  await evaluate('new Promise(r=>setTimeout(r,100))');
  assert.equal(await evaluate("strokes.some(s=>s.color==='#2a78d6'&&s.path?.length===2&&s.path[0][0]==='moveTo'&&s.path[1][0]==='lineTo'&&s.path[0][1]<s.path[1][1]&&s.path[0][2]===s.path[1][2])"),true,'an overlay with just two available values draws its line');
  await evaluate('view.dispose()');
  assert.equal(await evaluate("document.querySelector('#host').children.length"),0);
 }finally{ws?.close();await stopOwnedBrowserGroup(chrome);await new Promise(r=>server.close(r));await rm(directory,{recursive:true,force:true});}
});
