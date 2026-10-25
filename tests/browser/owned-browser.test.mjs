import test from 'node:test';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {EventEmitter,once} from 'node:events';
import {createServer} from 'node:http';
import {mkdtemp,rm,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {waitForBrowserPort,waitForBrowserPage,stopOwnedBrowser,browserPageActions,idle} from './owned-browser.mjs';

async function fixture(t,args=['-e','setInterval(()=>{},1000)']){
  const directory=await mkdtemp(join(tmpdir(),'openetrm-browser-port-'));
  const child=spawn(process.execPath,args,{stdio:'ignore'});
  t.after(async()=>{
    if(child.exitCode===null&&child.signalCode===null){const closed=once(child,'exit');child.kill('SIGTERM');await closed;}
    await rm(directory,{recursive:true,force:true});
  });
  return {directory,child,file:join(directory,'DevToolsActivePort')};
}

test('browser startup waits through missing, empty, partial and invalid port files',async t=>{
  const {directory,child,file}=await fixture(t);
  const outcome=waitForBrowserPort(directory,child).then(port=>({port}),error=>({error}));
  for(const content of [null,'','314','nonsense\n','0\n','65536\n','3.14\n','1e3\n']){
    if(content!==null)await writeFile(file,content);
    assert.equal(await Promise.race([outcome,delay(120).then(()=>null)]),null,
      'readiness must remain pending until a complete valid port line: '+JSON.stringify(content));
  }
  await writeFile(file,'31415\n/devtools/browser/synthetic');
  assert.deepEqual(await outcome,{port:31415});
});

test('browser startup accepts both valid port boundaries after a complete line',async t=>{
  for(const port of [1,65535]){
    const {directory,child,file}=await fixture(t);await writeFile(file,port+'\n');
    assert.equal(await waitForBrowserPort(directory,child),port);
  }
});

test('browser startup reports an owned process exit before readiness',async t=>{
  const {directory,child}=await fixture(t,['-e','process.exit(9)']);
  await assert.rejects(waitForBrowserPort(directory,child),/exited before readiness/);
});

test('browser startup reports an owned process signal before readiness',async t=>{
  const {directory,child}=await fixture(t);
  const outcome=waitForBrowserPort(directory,child);
  child.kill('SIGTERM');
  await assert.rejects(outcome,/exited before readiness/);
});

test('browser startup preserves a spawn error',async t=>{
  const directory=await mkdtemp(join(tmpdir(),'openetrm-browser-port-'));
  t.after(()=>rm(directory,{recursive:true,force:true}));
  const child=spawn(join(directory,'missing-browser'),[],{stdio:'ignore'});
  await assert.rejects(waitForBrowserPort(directory,child),{code:'ENOENT'});
});

test('browser startup retains its bounded readiness deadline for an invalid file',async t=>{
  const {directory,child,file}=await fixture(t);await writeFile(file,'invalid\n');
  const start=performance.now();
  await assert.rejects(waitForBrowserPort(directory,child,1000),/did not become ready/);
  assert.ok(performance.now()-start>=1000,'invalid content must not skip the readiness wait');
});

async function debuggerFixture(t,respond){
  const owned=await fixture(t),server=createServer(respond);
  await new Promise(accept=>server.listen(0,'127.0.0.1',accept));
  const port=server.address().port,page=`ws://127.0.0.1:${port}/devtools/page/synthetic`;
  t.after(async()=>{server.closeAllConnections();await new Promise(accept=>server.close(accept));});
  await writeFile(owned.file,port+'\n/devtools/browser/synthetic\n');
  return {...owned,server,port,page};
}

test('complete port metadata still waits for the debugger HTTP listener',async t=>{
  let page;
  const owned=await debuggerFixture(t,(_request,response)=>response.end(JSON.stringify([{type:'page',webSocketDebuggerUrl:page}])));
  page=owned.page;await new Promise(accept=>owned.server.close(accept));
  const result=waitForBrowserPage(owned.directory,owned.child,1000).then(value=>({value}),error=>({error}));
  await delay(150);await new Promise(accept=>owned.server.listen(owned.port,'127.0.0.1',accept));
  assert.deepEqual(await result,{value:page});
});

test('debugger readiness waits for a page after empty and nonpage target lists',async t=>{
  let page,requests=0;
  const owned=await debuggerFixture(t,(_request,response)=>response.end(JSON.stringify(++requests===1?[]:requests===2?[{type:'service_worker'}]:[{type:'page',webSocketDebuggerUrl:page}])));
  page=owned.page;
  assert.equal(await waitForBrowserPage(owned.directory,owned.child,1000),page);
  assert.equal(requests,3);
});

test('debugger readiness treats an unavailable HTTP response as not ready',async t=>{
  let page,requests=0;
  const owned=await debuggerFixture(t,(_request,response)=>{if(++requests===1){response.writeHead(503);response.end('Starting');}else response.end(JSON.stringify([{type:'page',webSocketDebuggerUrl:page}]));});
  page=owned.page;
  assert.equal(await waitForBrowserPage(owned.directory,owned.child,1000),page);
  assert.equal(requests,2);
});

test('debugger HTTP readiness remains inside the total startup deadline',{timeout:2500},async t=>{
  const closed=Promise.withResolvers();
  const owned=await debuggerFixture(t,(_request,response)=>response.once('close',closed.resolve));
  await assert.rejects(waitForBrowserPage(owned.directory,owned.child,150),/debugger.*ready|debugger.*timed out/i);
  await closed.promise;
});

test('debugger readiness timeout identifies the stage and retains its last HTTP failure',async t=>{
  const owned=await debuggerFixture(t,(_request,response)=>{response.writeHead(503);response.end('Starting');});
  await assert.rejects(waitForBrowserPage(owned.directory,owned.child,250),error=>/debugger.*startup deadline/i.test(error.message)&&/HTTP 503/.test(error.cause?.message));
});

test('debugger readiness notices an owned process exit during an HTTP wait',async t=>{
  let page;
  const owned=await debuggerFixture(t,(_request,response)=>{const timer=setTimeout(()=>response.end(JSON.stringify([{type:'page',webSocketDebuggerUrl:page}])),400);t.after(()=>clearTimeout(timer));});
  page=owned.page;
  const result=waitForBrowserPage(owned.directory,owned.child,1000);setTimeout(()=>owned.child.kill('SIGTERM'),75);
  await assert.rejects(result,/exited.*debugger|debugger.*exited/i);
});

test('debugger attachment rejects a websocket outside the exact owned loopback port',async t=>{
  const owned=await debuggerFixture(t,(_request,response)=>response.end(JSON.stringify([{type:'page',webSocketDebuggerUrl:'ws://example.invalid:1234/devtools/page/synthetic'}])));
  await assert.rejects(waitForBrowserPage(owned.directory,owned.child,1000),/owned.*debugger|debugger.*owned/i);
});

test('browser teardown awaits successful SIGKILL escalation',async t=>{
  // The handler must be installed before exercising the graceful-stop deadline.
  const ready=spawn(process.execPath,['-e',"process.on('SIGTERM',()=>{});process.stdout.write('ready');setInterval(()=>{},1000)"],{stdio:['ignore','pipe','ignore']});
  t.after(async()=>{if(ready.exitCode===null&&ready.signalCode===null){const stopped=once(ready,'exit');ready.kill('SIGKILL');await stopped;}});
  await once(ready.stdout,'data');
  await stopOwnedBrowser(ready,undefined,75);
  assert.equal(ready.signalCode,'SIGKILL');
  assert.equal(ready.exitCode,null);
});

test('failed browser cleanup retains the original failure and cleanup detail',async()=>{
  const primary=Error('Synthetic debugger startup failure'),child=new EventEmitter(),signals=[];
  Object.assign(child,{exitCode:null,signalCode:null,pid:42,kill:signal=>{signals.push(signal);return true;}});
  await assert.rejects(stopOwnedBrowser(child,primary,20),error=>error instanceof AggregateError&&error.cause===primary&&error.errors[0]===primary&&/cleanup|stop|shutdown/i.test(error.errors[1].message));
  assert.deepEqual(signals,['SIGTERM','SIGKILL']);
});


test('a booking action may explicitly await its write and holdings refresh beyond the ordinary readiness budget',async()=>{
  let elapsed=0,clicks=0;
  const actions=browserPageActions(async expression=>{
    if(expression===idle)return elapsed>=11000;
    assert.match(expression,/synthetic-book/);clicks++;return undefined;
  },{pause:async ms=>{elapsed+=ms;},now:()=>elapsed});
  await actions.click('#synthetic-book',35000);
  assert.equal(clicks,1,'a longer wait must never resubmit a financial command');
  assert.equal(elapsed,11000);
});

test('ordinary page readiness keeps its short bound when the page never becomes idle',async()=>{
  let elapsed=0,polls=0;
  const actions=browserPageActions(async expression=>{assert.equal(expression,idle);polls++;return false;},
    {pause:async ms=>{elapsed+=ms;},now:()=>elapsed});
  await assert.rejects(actions.wait(idle),/Timed out/);
  assert.equal(elapsed,10000);assert.equal(polls,200);
});


test('page action wait overrides stay finite and reject invalid budgets before clicking',async()=>{
  let calls=0;
  const actions=browserPageActions(async()=>{calls++;return true;});
  for(const invalid of [0,-1,Infinity,NaN,60001]){
    await assert.rejects(actions.click('#synthetic-book',invalid),/finite.*positive/);
    await assert.rejects(actions.wait(idle,invalid),/finite.*positive/);
  }
  assert.equal(calls,0);
});


test('settlement form waits for write, holdings and inspection without submitting twice',async()=>{
  let elapsed=0,submissions=0;
  const actions=browserPageActions(async expression=>{
    if(expression===idle)return elapsed>=46000;
    assert.match(expression,/synthetic-payment-form/);assert.match(expression,/requestSubmit/);submissions++;
  },{pause:async ms=>{elapsed+=ms;},now:()=>elapsed});
  await actions.submit('#synthetic-payment-form',60000);
  assert.equal(submissions,1,'refreshing financial state must never repeat the payment');
  assert.equal(elapsed,46000);
});
