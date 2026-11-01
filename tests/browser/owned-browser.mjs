// Browser checks receive an owned target from run_preview.py, never an arbitrary URL.
import {X509Certificate,createHash} from 'node:crypto';
import {execFile,spawn} from 'node:child_process';
import {mkdtemp,readFile,writeFile,stat} from 'node:fs/promises';
import {dirname,join,resolve} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';
import {promisify} from 'node:util';

const execute=promisify(execFile);

export const idle="document.body?.getAttribute('aria-busy')==='false'&&(!document.querySelector('#futures-page:not([hidden])')||document.querySelector('#futures-page').dataset.pricingState!=='pending')";
// Phone emulation widens window.innerWidth to fit overflowing content, so a page's width is
// compared with the root element's clientWidth: the layout viewport the device width sets.
export const fitsWidth='(document.documentElement.scrollWidth<=document.documentElement.clientWidth+1)';

// A cold first launch on a busy CI runner can take longer than ten seconds.
export async function waitForBrowserPort(directory,chrome,deadline=60000){
  let spawnError;
  const failed=error=>{spawnError=error;};chrome.on('error',failed);
  try{
    for(const until=performance.now()+deadline;performance.now()<until;){
      if(spawnError)throw spawnError;
      if(chrome.exitCode!==null||chrome.signalCode!==null)throw Error('Owned browser exited before readiness.');
      let content='';try{content=await readFile(join(directory,'DevToolsActivePort'),'utf8');}catch{}
      const end=content.indexOf('\n'),line=content.slice(0,end),port=Number(line);
      if(end>0&&/^\d+$/.test(line)&&Number.isInteger(port)&&port>=1&&port<=65535)return port;
      await delay(100);
    }
    throw Error('Owned browser did not become ready.');
  }finally{chrome.off('error',failed);}
}

export async function waitForBrowserPage(directory,chrome,deadline=60000){
  const until=performance.now()+deadline;
  let port;
  try{port=await waitForBrowserPort(directory,chrome,deadline);}
  catch(error){throw Error('Owned browser debugger port readiness failed.',{cause:error});}
  const controller=new AbortController();
  const exited=()=>controller.abort(Error('Owned browser exited during debugger readiness.'));
  const failed=error=>controller.abort(Error('Owned browser failed during debugger readiness.',{cause:error}));
  const timeout=setTimeout(()=>controller.abort(Error('Owned browser debugger did not become ready before the startup deadline.')),Math.max(0,until-performance.now()));
  chrome.once('exit',exited);chrome.once('error',failed);
  let lastFailure;
  try{
    for(;;){
      if(chrome.exitCode!==null||chrome.signalCode!==null)exited();
      controller.signal.throwIfAborted();
      let targets;
      try{
        const response=await fetch(`http://127.0.0.1:${port}/json/list`,{signal:controller.signal});
        if(response.ok)targets=await response.json();
        else{lastFailure=Error('Debugger target list returned HTTP '+response.status);await response.body?.cancel();}
      }catch(error){lastFailure=error;controller.signal.throwIfAborted();}
      if(targets!==undefined){
        if(!Array.isArray(targets))throw Error('Owned browser debugger returned an invalid target list.');
        const page=targets.find(target=>target.type==='page');
        if(page){
          let socketUrl;
          try{socketUrl=new URL(page.webSocketDebuggerUrl);}catch{throw Error('Owned browser debugger returned an invalid page URL.');}
          if(socketUrl.protocol!=='ws:'||socketUrl.hostname!=='127.0.0.1'||socketUrl.port!==String(port)||socketUrl.username||socketUrl.password||socketUrl.search||socketUrl.hash||!/^\/devtools\/page\/[^/]+$/.test(socketUrl.pathname))
            throw Error('Browser debugger page URL does not match the exact owned loopback port.');
          controller.signal.throwIfAborted();
          return socketUrl.href;
        }
        lastFailure=Error('Debugger target list has no page yet.');
      }
      await delay(100,undefined,{signal:controller.signal});
    }
  }catch(error){
    if(controller.signal.aborted)throw Error(controller.signal.reason.message,{cause:lastFailure??controller.signal.reason});
    throw error;
  }finally{clearTimeout(timeout);chrome.off('exit',exited);chrome.off('error',failed);}
}

export async function stopOwnedBrowser(chrome,primaryError,grace=5000){
  if(chrome.exitCode!==null||chrome.signalCode!==null||!chrome.pid)return;
  try{
    await new Promise((accept,reject)=>{
      let timeout,lastFailure;
      const finished=()=>{clearTimeout(timeout);chrome.off('exit',finished);chrome.off('error',failed);accept();};
      const failed=error=>{lastFailure=error;};
      const signal=name=>{try{chrome.kill(name);}catch(error){lastFailure=error;}};
      chrome.once('exit',finished);chrome.on('error',failed);
      timeout=setTimeout(()=>{
        timeout=setTimeout(()=>{
          chrome.off('exit',finished);chrome.off('error',failed);
          reject(Error('Owned browser shutdown could not be confirmed after SIGKILL.',{cause:lastFailure}));
        },grace);
        signal('SIGKILL');
      },grace);
      signal('SIGTERM');
    });
  }catch(error){
    if(primaryError)throw new AggregateError([primaryError,error],'Browser check failed and owned browser cleanup failed.',{cause:primaryError});
    throw error;
  }
}

// For a browser spawned with detached:true, whose process group ID is its own PID: its helpers can outlive it and keep
// writing into the profile, so its caller removes the profile only after the whole group has stopped.
export async function stopOwnedBrowserGroup(chrome,primaryError,grace=5000){
  const group=chrome.pid;
  const members=async()=>{
    const {stdout}=await execute('ps',['-axo','pid=,pgid=,stat='],{timeout:5000});
    return stdout.split('\n').map(line=>line.trim().split(/\s+/)).filter(parts=>parts.length===3&&Number(parts[1])===group&&!parts[2].startsWith('Z'));
  };
  try{
    await stopOwnedBrowser(chrome,undefined,grace);
    for(const signal of ['SIGTERM','SIGKILL']){
      if(!(await members()).length)return;
      // macOS refuses to signal a group whose remaining members are already exiting; the listing below decides.
      try{process.kill(-group,signal);}catch(error){if(!['ESRCH','EPERM'].includes(error.code))throw error;}
      for(const until=performance.now()+grace;performance.now()<until;await delay(50))if(!(await members()).length)return;
    }
    throw Error('Owned browser process group did not stop; retain its profile.');
  }catch(error){
    if(primaryError)throw new AggregateError([primaryError,error],'Browser check failed and owned browser cleanup failed.',{cause:primaryError});
    throw error;
  }
}

// One debugger connection, once open: its commands and the page helpers the checks use. Every connection reports its
// page's uncaught exceptions into the same list.
async function connect(socket,errors){
  await new Promise((accept,reject)=>{const timeout=setTimeout(()=>reject(Error('Browser connection timed out')),5000);socket.onopen=()=>{clearTimeout(timeout);accept();};socket.onerror=error=>{clearTimeout(timeout);reject(error);};});
  let sequence=0;const pending=new Map();
  socket.onmessage=event=>{
    const message=JSON.parse(event.data);
    if(message.method==='Runtime.exceptionThrown')errors.push(message.params.exceptionDetails.text);
    if(message.id){const p=pending.get(message.id);if(!p)return;pending.delete(message.id);clearTimeout(p.timeout);message.error?p.reject(Error(JSON.stringify(message.error))):p.accept(message.result);}
  };
  const abandon=(reason='Browser check finished')=>{for(const p of pending.values()){clearTimeout(p.timeout);p.reject(Error(reason));}pending.clear();};
  socket.onclose=()=>abandon('Browser connection closed.');
  function command(method,params={}){const id=++sequence;return new Promise((accept,reject)=>{const timeout=setTimeout(()=>{pending.delete(id);reject(Error('Browser command timed out: '+method));},20000);pending.set(id,{accept,reject,timeout});socket.send(JSON.stringify({id,method,params}));});}
  async function evaluate(expression){const response=await command('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(response.exceptionDetails)throw Error(JSON.stringify(response.exceptionDetails));return response.result.value;}
  const {wait,click,set,submit}=browserPageActions(evaluate);
  return {command,evaluate,wait,click,set,submit,abandon};
}

// A suite whose fixture makes every action heavy may raise the ordinary wait for one action, at most to a minute.
const actionWait=(()=>{const value=process.env.OPENETRM_BROWSER_ACTION_WAIT_MS;if(value===undefined)return 10000;const ms=Number(value);
  if(!Number.isInteger(ms)||ms<=0||ms>60000)throw Error('OPENETRM_BROWSER_ACTION_WAIT_MS must be a whole number of milliseconds up to 60000.');return ms;})();
// Page actions share the same evaluator for the primary page and owned tabs.
export function browserPageActions(evaluate,{pause=delay,now=()=>performance.now()}={}){
  const budget=(milliseconds=actionWait)=>{if(!Number.isFinite(milliseconds)||milliseconds<=0||milliseconds>60000)throw Error('Page action wait must be finite, positive and at most 60 seconds.');return milliseconds;};
  // A navigation the page makes while a poll runs answers that poll with an error; the condition is read again on the next page.
  const navigated=error=>/"Inspected target navigated or closed"/.test(error.message);
  const poll=async expression=>{try{return await evaluate(expression);}catch(error){if(navigated(error))return false;throw error;}};
  async function until(expression,milliseconds,read){const duration=budget(milliseconds),until=now()+duration;while(now()<until){if(await read(expression))return;await pause(Math.max(0,Math.min(50,until-now())));}throw Error('Timed out after '+duration+'ms: '+expression);}
  const wait=(expression,milliseconds)=>until(expression,milliseconds,poll);
  async function click(selector,milliseconds){const duration=budget(milliseconds);await evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);await wait(idle,duration);}
  // Retry only a missing control, never an edit whose result is uncertain. Find and edit in the same page turn.
  async function set(selector,value){await until(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});if(!n)return false;n.value=${JSON.stringify(value)};n.dispatchEvent(new Event('input',{bubbles:true}));n.dispatchEvent(new Event('change',{bubbles:true}));return true;})()`,undefined,evaluate);}
  async function submit(selector,milliseconds){const duration=budget(milliseconds);await evaluate(`document.querySelector(${JSON.stringify(selector)}).requestSubmit()`);await wait(idle,duration);}
  return {wait,click,set,submit};
}

export async function withOwnedBrowser(targetFile,browserPath,run){
  const root=dirname(resolve(targetFile)),target=JSON.parse(await readFile(targetFile,'utf8'));
  const url=new URL(target.url);
  const hosted=target.suite==='hosted_browser_v1';
  if(!['business_browser_v1','hosted_browser_v1'].includes(target.suite)||url.protocol!==(hosted?'https:':'http:')||url.hostname!=='127.0.0.1'||!url.port||url.pathname!=='/'||url.search||url.hash||url.username||url.password||
    (await readFile(join(root,'.openetrm-preview'),'utf8'))!==target.runId||!(await stat(join(root,'data/postmaster.pid'))).isFile())throw Error('Browser checks require the newly owned preview target.');
  const control=new URL(hosted?target.controlUrl:target.url);
  if(control.protocol!=='http:'||control.hostname!=='127.0.0.1'||!control.port||control.pathname!=='/'||control.search||control.hash||control.username||control.password)throw Error('Invalid owned control target.');
  let health;
  try{health=await(await fetch(new URL('/api/health',control),{signal:AbortSignal.timeout(5000)})).json();}
  catch(error){throw Error('Owned preview health check failed before browser startup.',{cause:error});}
  if(health.mode!=='local-synthetic-preview'||health.runId!==target.runId)throw Error('HTTP target does not match the owned preview.');
  const certificateFlags=[];
  if(hosted){
    const configuration=JSON.parse(await readFile(join(root,'hosted-check/host.json'),'utf8'));
    if(configuration.publicOrigin!==url.origin)throw Error('Hosted target does not match owned configuration.');
    const certificate=new X509Certificate(await readFile(join(root,'hosted-check/tls.pem')));
    certificateFlags.push('--ignore-certificate-errors-spki-list='+createHash('sha256').update(certificate.publicKey.export({type:'spki',format:'der'})).digest('base64'));
  }
  if(!(await stat(browserPath)).isFile())throw Error('Configure an installed Chromium-compatible browser executable.');
  const directory=await mkdtemp(join(root,'browser-'));
  const chrome=spawn(browserPath,[...certificateFlags,'--headless=new','--no-first-run','--disable-background-networking','--disable-component-update','--disable-sync','--disable-extensions','--disable-default-apps','--metrics-recording-only',`--user-data-dir=${directory}`,'--remote-debugging-port=0','about:blank'],{stdio:'ignore'});
  let socket,passed=0,primaryError;
  const errors=[],tabs=[];
  try{
    socket=new WebSocket(await waitForBrowserPage(directory,chrome));
    const {command,evaluate,wait,click,set,submit,abandon}=await connect(socket,errors);
    // Another tab of this browser, sharing its cookies and storage origin, over a debugger connection of its own.
    async function tab(targetId){
      const port=await waitForBrowserPort(directory,chrome),response=await fetch(`http://127.0.0.1:${port}`+(targetId===undefined?'/json/new?about:blank':'/json/list'),{method:targetId===undefined?'PUT':'GET'});
      const found=await response.json(),created=targetId===undefined?found:found.find(item=>item.id===targetId);
      if(!created)throw Error('The owned popup target is unavailable.');
      if(targetId!==undefined){const popup=new URL(created.url);if(popup.origin!==url.origin||!['/position.html','/blotter.html'].includes(popup.pathname))throw Error('Only this owned browser’s trade or Blotter popup can be attached.');}
      const address=new URL(created.webSocketDebuggerUrl);
      if(created.type!=='page'||address.protocol!=='ws:'||address.hostname!=='127.0.0.1'||address.port!==String(port)||!/^\/devtools\/page\/[^/]+$/.test(address.pathname))
        throw Error('Browser debugger tab URL does not match the exact owned loopback port.');
      const other=new WebSocket(address.href),driver=await connect(other,errors);tabs.push({other,driver});
      await driver.command('Runtime.enable');await driver.command('Page.enable');await driver.command('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
      return {...driver,async close(){driver.abandon();other.close();await fetch(`http://127.0.0.1:${port}/json/close/${created.id}`);}};
    }
    // Switching accounts reads the identity and the account's first page again, so it waits longer than a single action.
    async function persona(id){await click('#profile-open');await set('#profile',id);await click('#open',30000);}
    // An optional detail expression reports observed state only when the check fails.
    async function check(label,expression,detail){if(!await evaluate(expression))throw Error('FAIL '+label+(detail===undefined?'':' · observed '+JSON.stringify(await evaluate(detail).catch(error=>'unavailable: '+error.message))));passed++;console.log('PASS '+label);}
    async function shot(name){if(!/^[a-z-]+$/.test(name))throw Error('Invalid screenshot name');const result=await command('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(join(root,name+'.png'),Buffer.from(result.data,'base64'));}
    await command('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
    try{
      await run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,errors,url:url.origin,provider:target.provider,tab,fixture:target.ppa??target.closeOut});
      if(errors.length)throw Error('Uncaught browser exceptions: '+JSON.stringify(errors));
      return passed;
    }finally{abandon();for(const {other,driver} of tabs){driver.abandon();other.close();}}
  }catch(error){primaryError=error;throw error;}
  finally{
    socket?.close();
    await stopOwnedBrowser(chrome,primaryError);
    // run_preview.py removes profiles after stopping the entire owned process group.
  }
}
