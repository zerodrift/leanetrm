// Browser checks receive an owned target from run_preview.py, never an arbitrary URL.
import {X509Certificate,createHash} from 'node:crypto';
import {spawn} from 'node:child_process';
import {mkdtemp,readFile,writeFile,stat} from 'node:fs/promises';
import {dirname,join,resolve} from 'node:path';
import {setTimeout as delay} from 'node:timers/promises';

export async function withOwnedBrowser(targetFile,browserPath,run){
  const root=dirname(resolve(targetFile)),target=JSON.parse(await readFile(targetFile,'utf8'));
  const url=new URL(target.url);
  const hosted=target.suite==='hosted_browser_v1';
  if(!['business_browser_v1','hosted_browser_v1'].includes(target.suite)||url.protocol!==(hosted?'https:':'http:')||url.hostname!=='127.0.0.1'||!url.port||url.pathname!=='/'||url.search||url.hash||url.username||url.password||
    (await readFile(join(root,'.openetrm-preview'),'utf8'))!==target.runId||!(await stat(join(root,'data/postmaster.pid'))).isFile())throw Error('Browser checks require the newly owned preview target.');
  const control=new URL(hosted?target.controlUrl:target.url);
  if(control.protocol!=='http:'||control.hostname!=='127.0.0.1'||!control.port||control.pathname!=='/'||control.search||control.hash||control.username||control.password)throw Error('Invalid owned control target.');
  const health=await(await fetch(new URL('/api/health',control),{signal:AbortSignal.timeout(5000)})).json();
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
  let socket,sequence=0,passed=0,spawnError;
  const pending=new Map(),errors=[];
  chrome.on('error',error=>{spawnError=error;});
  try{
    let port;
    for(let i=0;i<100;i++){
      if(spawnError)throw spawnError;
      if(chrome.exitCode!==null)throw Error('Owned browser exited before readiness.');
      try{port=Number((await readFile(join(directory,'DevToolsActivePort'),'utf8')).split('\n')[0]);break;}catch{await delay(100);}
    }
    if(!port)throw Error('Owned browser did not become ready.');
    const targets=await(await fetch(`http://127.0.0.1:${port}/json/list`,{signal:AbortSignal.timeout(5000)})).json();
    socket=new WebSocket(targets.find(t=>t.type==='page').webSocketDebuggerUrl);
    await new Promise((accept,reject)=>{const timeout=setTimeout(()=>reject(Error('Browser connection timed out')),5000);socket.onopen=()=>{clearTimeout(timeout);accept();};socket.onerror=error=>{clearTimeout(timeout);reject(error);};});
    socket.onmessage=event=>{
      const message=JSON.parse(event.data);
      if(message.method==='Runtime.exceptionThrown')errors.push(message.params.exceptionDetails.text);
      if(message.id){const p=pending.get(message.id);if(!p)return;pending.delete(message.id);clearTimeout(p.timeout);message.error?p.reject(Error(JSON.stringify(message.error))):p.accept(message.result);}
    };
    socket.onclose=()=>{for(const p of pending.values()){clearTimeout(p.timeout);p.reject(Error('Browser connection closed.'));}pending.clear();};
    function command(method,params={}){const id=++sequence;return new Promise((accept,reject)=>{const timeout=setTimeout(()=>{pending.delete(id);reject(Error('Browser command timed out: '+method));},20000);pending.set(id,{accept,reject,timeout});socket.send(JSON.stringify({id,method,params}));});}
    async function evaluate(expression){const response=await command('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(response.exceptionDetails)throw Error(JSON.stringify(response.exceptionDetails));return response.result.value;}
    async function wait(expression){for(let i=0;i<200;i++){if(await evaluate(expression))return;await delay(50);}throw Error('Timed out: '+expression);}
    const idle="document.body.getAttribute('aria-busy')==='false'";
    async function click(selector){await evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);await wait(idle);}
    async function set(selector,value){await evaluate(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});n.value=${JSON.stringify(value)};n.dispatchEvent(new Event('input',{bubbles:true}));n.dispatchEvent(new Event('change',{bubbles:true}));})()`);}
    async function submit(selector){await evaluate(`document.querySelector(${JSON.stringify(selector)}).requestSubmit()`);await wait(idle);}
    async function persona(id){await click('#profile-open');await set('#profile',id);await click('#open');}
    async function check(label,expression){if(!await evaluate(expression))throw Error('FAIL '+label);passed++;console.log('PASS '+label);}
    async function shot(name){if(!/^[a-z-]+$/.test(name))throw Error('Invalid screenshot name');const result=await command('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(join(root,name+'.png'),Buffer.from(result.data,'base64'));}
    await command('Emulation.setTimezoneOverride',{timezoneId:'UTC'});
    await run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,errors,url:url.origin});
    if(errors.length)throw Error('Uncaught browser exceptions: '+JSON.stringify(errors));
    return passed;
  }finally{
    socket?.close();
    for(const p of pending.values()){clearTimeout(p.timeout);p.reject(Error('Browser check finished'));}pending.clear();
    if(chrome.exitCode===null&&chrome.pid){chrome.kill('SIGTERM');await new Promise((accept,reject)=>{const timeout=setTimeout(()=>{chrome.kill('SIGKILL');reject(Error('Browser shutdown timed out'));},5000);chrome.once('exit',()=>{clearTimeout(timeout);accept();});});}
    // run_preview.py removes profiles after stopping the entire owned process group.
  }
}
