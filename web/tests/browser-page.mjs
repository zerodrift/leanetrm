import assert from 'node:assert/strict';
import {createServer} from 'node:http';
import {readFile,mkdtemp,rm,access} from 'node:fs/promises';
import {spawn} from 'node:child_process';
import {tmpdir} from 'node:os';
import {join,resolve,extname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {waitForBrowserPage,stopOwnedBrowserGroup} from '../../tests/browser/owned-browser.mjs';

const browser=process.env.CHROME_BIN??'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
export const browserAvailable=await access(browser).then(()=>true,()=>false);

// A disposable headless browser and a static server of web/dist draw `page`; `body` evaluates expressions in it.
// No application runtime, account or database is attached.
export async function withBrowserPage(page,body){
 const root=fileURLToPath(new URL('../dist/',import.meta.url));
 const server=createServer(async(req,res)=>{
  try{
   if(req.url==='/'){res.setHeader('Content-Type','text/html');res.end(page);return;}
   const file=resolve(root,'.'+new URL(req.url,'http://localhost').pathname);
   if(!file.startsWith(root)){res.writeHead(404).end();return;}
   res.setHeader('Content-Type',extname(file)==='.js'?'text/javascript':extname(file)==='.css'?'text/css':'application/octet-stream');res.end(await readFile(file));
  }catch{res.writeHead(404).end();}
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const directory=await mkdtemp(join(tmpdir(),'openetrm-page-'));
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
  await body(evaluate);
 }finally{ws?.close();await stopOwnedBrowserGroup(chrome);await new Promise(r=>server.close(r));await rm(directory,{recursive:true,force:true});}
}
