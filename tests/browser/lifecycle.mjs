import {withOwnedBrowser} from './owned-browser.mjs';

export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture}){
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait("!!document.querySelector('#profile-open')&&"+idle,30000);
 await evaluate(`window.observed=[];const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);const body=args[1]?.body?JSON.parse(args[1].body):null;if(response.headers.get('content-type')?.includes('application/json'))observed.push({path:String(args[0]),body,status:response.status,value:await response.clone().json()});if(window.loseGasReply&&String(args[0])==='/api/lifecycle/execute'&&body?.target?.handler==='gas_owned'&&response.ok){window.loseGasReply=false;throw new TypeError('Synthetic lost response after native gas commit');}return response;};`);
 await persona('trader');await click('#nav-lifecycle');
 await set('#close-session','synthetic_lifecycle_close');await wait(`document.querySelector('#snapshot').value===${JSON.stringify(fixture.closeId)}&&${idle}`,30000);
 await check('Lifecycle reads the exact recorded close through the real API',`observed.some(r=>r.path==='/api/lifecycle/view'&&r.status===200&&r.value.recordedCloseId===${JSON.stringify(fixture.closeId)})`,"({requests:observed.filter(r=>r.path.includes('lifecycle')),error:document.querySelector('#error').textContent,lifecycle:document.querySelector('#lifecycle-page').textContent})");
 await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save',30000);
 await check('Lifecycle uses the saved user time zone',"document.querySelector('[data-user-time-zone]').textContent==='America/Chicago'");
 await evaluate("document.querySelector('[data-lifecycle=inactive]').checked=true;document.querySelector('[data-lifecycle=inactive]').dispatchEvent(new Event('change',{bubbles:true}))");
 const row=`[...document.querySelectorAll('[data-lifecycle=rows] .tabulator-row')].find(r=>r.innerText.includes(${JSON.stringify(fixture.reference)}))`;
 await wait(`!!(${row})`);await evaluate(`(${row}).click()`);await wait("!!document.querySelector('[data-lifecycle=panel] form')");
 await evaluate("(()=>{const s=document.querySelector('[data-lifecycle=panel] form select');s.value=[...s.options].find(o=>o.value&&o.textContent.includes('Automatic')).value;s.dispatchEvent(new Event('change',{bubbles:true}));})()");
 await submit('[data-lifecycle=panel] form',30000);
 await check('A real native final margin review exposes the surviving physical obligation',"document.querySelector('[data-lifecycle-review]')?.textContent.includes('Physical delivery principal')&&document.querySelector('[data-lifecycle-review]')?.textContent.includes('without recording title')","({requests:observed.filter(r=>r.path.includes('lifecycle/preview')),error:document.querySelector('#error').textContent,panel:document.querySelector('[data-lifecycle=panel]').textContent,invalid:[...document.querySelectorAll('[data-lifecycle=panel] :invalid')].map(n=>({tag:n.tagName,type:n.type,value:n.value,message:n.validationMessage}))})");
 await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'})");await shot('lifecycle-physical-review');
 await check('The Lifecycle review fits its desktop viewport',fitsWidth);
 await click('[data-lifecycle-execute]',30000);
 await check('Reviewed execution records its exact request and clears uncertainty',`observed.some(r=>r.path==='/api/lifecycle/execute'&&r.status===200&&r.value.operationId===r.body.operationId)&&document.querySelector('[data-lifecycle=pending]').hidden`);
 await wait(`!!(${row})`);await evaluate(`(${row}).click()`);
 await evaluate("[...document.querySelectorAll('[data-lifecycle=panel] button')].find(b=>b.textContent==='Schedule this occurrence').click()");
 await wait("!!document.querySelector('[data-lifecycle=panel] input[type=datetime-local]')");
 await check('Task controls show local times and require every policy explicitly',"document.querySelector('[data-lifecycle=panel]').textContent.includes('America/Chicago')&&[...document.querySelectorAll('[data-lifecycle=panel] select')].every(s=>s.value==='')");
 await evaluate(`(()=>{const f=document.querySelector('[data-lifecycle=panel] form');f.querySelector('input[type=text]').value='Synthetic browser lifecycle';for(const s of f.querySelectorAll('select')){const selected=[...s.options].find(o=>o.value&&o.textContent.includes('Automatic'))??[...s.options].find(o=>o.value);if(!selected)continue;s.value=selected.value;s.dispatchEvent(new Event('change',{bubbles:true}));}const now=new Date(Date.now()-1000),later=new Date(Date.now()+3600000),local=d=>{const p=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:'America/Chicago',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23'}).formatToParts(d).map(p=>[p.type,p.value]));return p.year+'-'+p.month+'-'+p.day+'T'+p.hour+':'+p.minute+':'+p.second;};const dates=f.querySelectorAll('input[type=datetime-local]');dates[0].value=local(now);dates[1].value=local(later);f.querySelector('input[type=number]').value='30';for(const d of f.querySelectorAll('input'))d.dispatchEvent(new Event('input',{bubbles:true}));})()`);
 await submit('[data-lifecycle=panel] form',30000);
 await check('A task submission retains the nested exact request ID and native setting versions',"observed.some(r=>r.path==='/api/jobs/lifecycle/create'&&r.status===200&&r.value.operationId===r.body.schedule.requestId&&r.body.definition.settings.marginPolicyRevision===1)&&document.querySelector('[data-lifecycle=pending]').hidden");
 await click('#nav-jobs',30000);
 const task="[...document.querySelectorAll('#jobs-table tbody tr')].find(r=>r.textContent.includes('Synthetic browser lifecycle'))";
 await wait(`!!(${task})`,30000);
 // Status remains operational; refresh the directory while the real separate
 // managed scheduler performs the queued shared native operation.
 for(let attempt=0;attempt<20&&!await evaluate(`(${task}).textContent.includes('completed')`);attempt++){
  await new Promise(r=>setTimeout(r,1000));await click('#jobs-refresh',30000);
 }
 await check('The separate managed worker commits the queued lifecycle task',`(${task}).textContent.includes('completed')`);
 await click('#nav-lifecycle',30000);
 const titleRow=`[...document.querySelectorAll('[data-lifecycle=rows] .tabulator-row')].find(r=>r.innerText.includes(${JSON.stringify(fixture.gasAccountCode)})&&r.innerText.includes('Accept title to gas'))`;
 await wait(`!!(${titleRow})`);await evaluate(`(${titleRow}).click()`);await wait("!!document.querySelector('[name=quantity]')",30000);
 await check('Gas observation forms use actual native evidence and policy choices',`observed.some(r=>r.path==='/api/lifecycle/gas/setup'&&r.status===200)&&!![...document.querySelector('[name=evidenceId]').options].find(o=>o.textContent.includes(${JSON.stringify(fixture.titleSourceKey)}))`);
 await evaluate(`(()=>{const f=document.querySelector('[data-lifecycle=panel] form'),pick=(name,test)=>{const s=f.querySelector('[name='+name+']');s.value=[...s.options].find(o=>o.value&&test(o)).value;s.dispatchEvent(new Event('change',{bubbles:true}));};pick('policyId',()=>true);pick('evidenceId',o=>o.textContent.includes(${JSON.stringify(fixture.titleSourceKey)}));pick('originPolicy',()=>true);pick('carrying',o=>o.value==='nominal_mark_v1');f.querySelector('[name=quantity]').value='0.125';const parts=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:'America/Chicago',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23'}).formatToParts(new Date(${JSON.stringify(fixture.titleAt)})).map(p=>[p.type,p.value]));const local=parts.year+'-'+parts.month+'-'+parts.day+'T'+parts.hour+':'+parts.minute+':'+parts.second;for(const n of ['titleAt','availableAt'])f.querySelector('[name='+n+']').value=local;for(const input of f.querySelectorAll('input'))input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
 await submit('[data-lifecycle=panel] form',30000);
 await check('A real gas review shows the exact quantity and native economic effect',"document.querySelector('[data-lifecycle-review]')?.textContent.includes('0.125')&&observed.some(r=>r.path==='/api/lifecycle/preview'&&r.status===200&&r.body.target.handler==='gas_owned'&&r.value.details.effects.performedQuantity==='0.125')","({requests:observed.filter(r=>r.path.includes('lifecycle/preview')),error:document.querySelector('#error').textContent,panel:document.querySelector('[data-lifecycle=panel]').textContent,invalid:[...document.querySelectorAll('[data-lifecycle=panel] :invalid')].map(n=>({name:n.name,value:n.value,message:n.validationMessage}))})");
 await evaluate("document.querySelector('[data-lifecycle-review]').scrollIntoView({block:'center'});window.loseGasReply=true");await shot('lifecycle-gas-review');await click('[data-lifecycle-execute]',30000);
 await check('A lost gas acknowledgement retains the original request after the native commit',"!document.querySelector('[data-lifecycle=pending]').hidden&&observed.some(r=>r.path==='/api/lifecycle/execute'&&r.status===200&&r.body.target.handler==='gas_owned')");
 await click('[data-lifecycle=retry]',30000);
 await check('Retry confirms the first gas outcome without a second title receipt',"(()=>{const calls=observed.filter(r=>r.path==='/api/lifecycle/execute'&&r.body.target.handler==='gas_owned');return calls.length===2&&JSON.stringify(calls[0].body)===JSON.stringify(calls[1].body)&&JSON.stringify(calls[0].value)===JSON.stringify(calls[1].value)&&document.querySelector('[data-lifecycle=pending]').hidden;})()");
 await click('#nav-jobs',30000);await wait(`!!(${task})`,30000);
 await evaluate(`[...(${task}).querySelectorAll('button')].find(b=>b.textContent==='View lifecycle outcome').click()`);await wait(idle);
 await check('Jobs reopens the actual retained outcome and certified source',"document.querySelector('#jobs-page').textContent.includes('Executed under the saved policy')&&document.querySelector('#jobs-page').textContent.includes('Known as of')");
 const outcome="[...document.querySelectorAll('#jobs-page h4')].find(h=>h.textContent==='Executed under the saved policy').closest('section')";
 await evaluate(`${outcome}.scrollIntoView({block:'center'})`);await shot('lifecycle-desktop');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate(`${outcome}.scrollIntoView({block:'center'})`);
 await check('The retained lifecycle outcome fits a narrow screen',fitsWidth);await shot('lifecycle-mobile');
}

if(process.argv.length!==4)throw Error('Use tools/check_lifecycle_browser.py with an owned target.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],run)} full-app lifecycle browser checks passed.`);
