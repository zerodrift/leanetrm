// D334: the header opens on the person's remembered close session, offers only that session's closes as closes and lists a
// day's replaced closes as history, never as a choice.
export async function run({command,evaluate,wait,persona,check,idle,url,click,set}){
 await command('Page.navigate',{url:url+'/'});await wait(idle,30000);
 await persona('viewer');await wait(idle,30000);
 await check('the header names the remembered close session',
  "(()=>{const s=document.querySelector('#close-session');return s&&s.value==='demo_close_1600'&&s.selectedOptions[0].textContent.includes('16:00')&&document.querySelector('#close-session-status').hidden;})()");
 await check('Market close is labelled separately and convention and knowledge are collapsed initially',
  "document.querySelector('label[for=market-date-open]').textContent==='Market close'&&!document.querySelector('#as-of-context').open&&!document.querySelector('#close-session').checkVisibility()&&!document.querySelector('#date-context-view').checkVisibility()&&document.querySelector('#as-of-context>summary').textContent.trim()==='As of details'");
 const original=await evaluate("document.querySelector('#close-session').value");
 let other=await evaluate("[...document.querySelector('#close-session').options].find(o=>o.value&&o.value!==document.querySelector('#close-session').value)?.value");
 if(!other){
  const saved=await evaluate("(async()=>{const boot=await(await fetch('/api/bootstrap')).json();window.__conventionCsrf=boot.csrfToken;return (await(await fetch('/api/close-sessions/catalog')).json()).sessions.find(s=>s.state==='published');})()");
  await persona('editor');
  const draft=await evaluate(`(async()=>{const boot=await(await fetch('/api/bootstrap')).json(),original=${JSON.stringify(saved)};const response=await fetch('/api/close-sessions/save',{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify({requestId:crypto.randomUUID(),draft:{code:'synthetic_profile_'+crypto.randomUUID().slice(0,8),name:'Synthetic profile convention 17:00 UTC',calendarCode:original.calendarCode,cutoff:'17:00',curves:original.curves,effectiveFrom:original.effectiveFrom,effectiveTo:original.effectiveTo}})});if(!response.ok)throw Error(await response.text());return response.json();})()`);
  await persona('approver');
  await evaluate(`(async()=>{const boot=await(await fetch('/api/bootstrap')).json(),draft=${JSON.stringify(draft)};const response=await fetch('/api/close-sessions/publish',{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify({requestId:crypto.randomUUID(),code:draft.code,version:draft.version,generation:draft.generation})});if(!response.ok)throw Error(await response.text());})()`);
  await persona('viewer');other=draft.code;
 }
 const selected=await evaluate("document.querySelector('#snapshot').value");
 await click('#profile-open');await set('#default-close-session',other);await click('#profile-save');await wait(idle);
 await check('saving a profile default keeps this window on its explicitly selected close',
  `document.querySelector('#close-session').value===${JSON.stringify(original)}&&document.querySelector('#snapshot').value===${JSON.stringify(selected)}&&!document.querySelector('#profile-dialog').open`);
 await command('Page.navigate',{url:url+'/'});await wait(idle,30000);
 await check('a new page uses the saved profile convention without expanding As of',
  `document.querySelector('#close-session').value===${JSON.stringify(other)}&&!document.querySelector('#as-of-context').open`);
 await click('#as-of-context>summary');await set('#close-session',original);await wait(idle);
 await check('a view override keeps the profile default unchanged',
  `(async()=>{const choice=await(await fetch('/api/close-sessions/choice')).json();return document.querySelector('#close-session').value===${JSON.stringify(original)}&&choice.session===${JSON.stringify(other)};})()`);
 await click('#profile-open');await set('#default-close-session',original);await click('#profile-save');await wait(idle);
 await command('Page.navigate',{url:url+'/'});await wait(idle,30000);
 const offered=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return {closes:w.frames.filter(f=>f.close?.session==='demo_close_1600').map(f=>f.id),working:w.frames.filter(f=>!f.close).map(f=>f.id)};})()`);
 await check('the As-of choice offers the session closes, and working snapshots only as working data',
  `(()=>{const options=[...document.querySelectorAll('#snapshot option')].filter(o=>o.value);const closes=${JSON.stringify(offered.closes)};
   return options.length>0&&options.every(o=>closes.includes(o.value)||o.textContent.startsWith('Working snapshot · '));})()`);
 await check('a close with no replaced history shows no history list',"document.querySelector('#close-history').hidden");
 // The header wraps below 1190px; the session control keeps its compact width there too.
 await command('Emulation.setDeviceMetricsOverride',{width:1000,height:900,deviceScaleFactor:1,mobile:false});await wait(idle);
 await click('#as-of-context>summary');
 await check('the session control fits the header',"(()=>{const s=document.querySelector('#close-session');const r=s.getBoundingClientRect();return r.width>0&&r.right<=document.documentElement.clientWidth;})()",
  "(()=>{const r=document.querySelector('#close-session').getBoundingClientRect();return {left:r.left,right:r.right,width:r.width,viewport:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth};})()");

 // An absent default is explained without opening the disclosure or selecting an alternative.
 await command('Page.enable');
 const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
  const original=window.fetch;window.fetch=async(...args)=>{const path=new URL(typeof args[0]==='string'?args[0]:args[0].url,location.href).pathname;
   if(path.endsWith('/close-sessions/choice')&&!(args[1]&&args[1].method==='POST'))return new Response(JSON.stringify({session:null}),{status:200,headers:{'Content-Type':'application/json'}});return original(...args);};
 `});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelector('#close-session')?.options.length>1&&"+idle,30000);
 await check('an absent default keeps the convention and knowledge collapsed without choosing a replacement',"!document.querySelector('#as-of-context').open&&!document.querySelector('#close-session').checkVisibility()&&document.querySelector('#close-session').value===''&&document.querySelector('#snapshot').value===''");
 await click('#as-of-context>summary');
 await check('expanding As of reveals the correctly labelled convention and information cutoff',"document.querySelector('#close-session').checkVisibility()&&document.querySelector('label[for=close-session]').textContent==='Close convention'&&document.querySelector('#date-context-view').checkVisibility()");
 await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});
}
