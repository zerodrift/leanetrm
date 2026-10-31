// Native double-clicks open independent windows. All values and schedules remain backend reads.
import {mkdtemp,writeFile} from 'node:fs/promises';
import {join} from 'node:path';
import {tmpdir} from 'node:os';
export async function run({command,evaluate,wait,click,set,persona,check,idle,url,tab,errors}){
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-position-popout-'));console.log('POPOUT_VISUAL_ARTIFACTS='+artifacts);
 async function close(popup,id){
  await popup.evaluate("setTimeout(()=>document.querySelector('#position-window-close').click(),0);true");
  let closed=false;for(let n=0;n<100;n++){closed=!(await command('Target.getTargets')).targetInfos.some(t=>t.targetId===id);if(closed)break;await new Promise(resolve=>setTimeout(resolve,50));}
  if(!closed)throw Error('Close window did not close the script-opened trade window.');await popup.close();
 }
 await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('trader');
 await evaluate(`(()=>{const send=window.fetch;window.popoutReads=[];window.fetch=async(...args)=>{if(args[0]==='/api/trading/workspace'&&window.popoutFailWorkspace){window.popoutFailWorkspace=false;return new Response(JSON.stringify({code:'request_cancelled'}),{status:503,headers:{'Content-Type':'application/json'}});}const response=await send(...args);if(['/api/trading/workspace','/api/trading/positions'].includes(args[0])&&response.ok)window.popoutReads.push({path:args[0],body:JSON.parse(args[1].body),value:await response.clone().json()});return response;};})()`);
 const snapshot=await evaluate("(async()=>{const data=await(await fetch('/api/workspace')).json();return data.frames.find(f=>f.at.startsWith('2020-12-18T16:00:00')).id;})()");
 await set('#snapshot',snapshot);await wait(idle,30000);await click('#nav-futures',30000);await wait(idle,30000);
 const report=await evaluate("window.popoutReads.filter(r=>r.path==='/api/trading/workspace').at(-1).value.blotter"),parentWindow=await command('Browser.getWindowForTarget');let last;
 for(const type of ['future','financial_swap','heat_rate','ppa']){
  const selected=report.holdings.find(h=>h.instrumentType===type&&h.unavailableReason===null);if(!selected)throw Error('The owned popup fixture needs a valued '+type);
  if(type==='ppa'){await evaluate("window.popoutFailWorkspace=true;document.querySelector('#futures-refresh').click()");await wait("document.querySelector('#futures-page').dataset.pricingState==='failed'&&"+idle,30000);}
  await set('#blotter-filters .table-search',selected.tradeReference);
  await wait(`(()=>{const rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length);return rows.length===1&&rows[0].textContent.includes(${JSON.stringify(selected.tradeReference)});})()`);
  await evaluate("(()=>{window.popoutGestures=[];if(window.popoutGestureRecorder)return;window.popoutGestureRecorder=true;for(const type of ['pointerdown','mousedown','mouseup','click','dblclick'])document.addEventListener(type,event=>{if(event.target.closest('#desk-positions')){const cell=event.target.closest('.tabulator-row')?.querySelector('[tabulator-field=c0]'),rect=cell?.getBoundingClientRect();window.popoutGestures.push({type,detail:event.detail,row:cell?.textContent,target:event.target.tagName,class:event.target.className,cell:rect?{x:rect.x,y:rect.y,height:rect.height}:null,selected:window.getSelection()?.toString()});}},true);})()");
  const before=(await command('Target.getTargets')).targetInfos.map(t=>t.targetId),cell=await evaluate("(()=>{const r=document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group) [tabulator-field=c0]').getBoundingClientRect();return{x:r.x+30,y:r.y+15};})()");
  for(const clickCount of [1,2]){
   await command('Input.dispatchMouseEvent',{type:'mousePressed',button:'left',clickCount,...cell});
   // A formatter can replace cell contents during a native gesture. Keep the
   // row identity but replace the exact pressed element before the second release.
   if(clickCount===2&&type==='heat_rate')await evaluate(`(()=>{const cell=document.elementFromPoint(${cell.x},${cell.y});cell.replaceWith(cell.cloneNode(true));})()`);
   await command('Input.dispatchMouseEvent',{type:'mouseReleased',button:'left',clickCount,...cell});
  }
  let target;for(let n=0;n<100;n++){target=(await command('Target.getTargets')).targetInfos.find(t=>t.type==='page'&&!before.includes(t.targetId)&&t.url.startsWith(url+'/position.html#'));if(target)break;await new Promise(resolve=>setTimeout(resolve,50));}
  if(!target)throw Error('Native double-click did not open the owned '+type+' trade popup. · '+JSON.stringify(await evaluate("({gestures:window.popoutGestures,busy:document.body.getAttribute('aria-busy'),notice:document.querySelector('#notice').textContent,selected:window.getSelection()?.toString(),focus:document.activeElement?.id,rows:[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].map(r=>r.querySelector('[tabulator-field=c0]')?.textContent)})")));
  if((await command('Target.getTargets')).targetInfos.filter(t=>t.type==='page'&&!before.includes(t.targetId)&&t.url.startsWith(url+'/position.html#')).length!==1)throw Error('One double-click opened multiple trade windows.');
  const popup=await tab(target.targetId),window=await popup.command('Browser.getWindowForTarget');if(window.windowId===parentWindow.windowId)throw Error('The trade opened in the original window instead of a separate window.');
  try{await popup.wait("document.body.getAttribute('aria-busy')==='false'&&!document.querySelector('#position-window-detail').hidden&&!!document.querySelector('#position-window-view .side-panel-section')",30000);}
  catch(error){throw Error(error.message+' · popup '+JSON.stringify(await popup.evaluate("({url:location.href,busy:document.body.getAttribute('aria-busy'),text:document.body.innerText.slice(0,5000),resources:performance.getEntriesByType('resource').map(r=>({url:r.name,status:r.responseStatus}))})"))+' · exceptions '+JSON.stringify(errors));}
  const expected=type==='ppa'?await evaluate("window.popoutReads.filter(r=>r.path==='/api/trading/positions').at(-1).value"):report;
  const state=await popup.evaluate("({opener:window.opener===null,title:document.querySelector('#position-window-title').textContent,context:document.querySelector('#position-window-detail').dataset.context,knownAt:document.querySelector('#position-window-detail').dataset.knownAt,asOf:document.querySelector('#position-window-detail').dataset.asOf,tabs:[...document.querySelectorAll('[role=tab]')].map(t=>t.textContent),amend:document.querySelector('#side-panel-amend')?.hidden,settlement:!!document.querySelector('#side-panel-settlement'),forms:document.querySelectorAll('form').length,value:document.querySelector('.side-panel-figure .kpi-value')?.title,resources:performance.getEntriesByType('resource').map(r=>new URL(r.name).pathname),error:document.querySelector('#position-window-error').hidden})");
  const value=selected.instrumentType==='future'?selected.executionPnl:selected.presentValue;
  const matches=state.opener&&state.title===selected.tradeReference&&state.context===expected.context.contextId&&state.knownAt===expected.context.knownAt&&state.asOf===expected.asOf&&state.tabs.join()==='Terms,History'&&state.amend&&!state.settlement&&!state.forms&&state.value===value+' '+selected.currency&&state.error&&!state.resources.some(path=>/\/(book|amend|market\/context|market\/current|risk)(?:\/|$)/.test(path));
  await evaluate(`window.__popupCheck=${JSON.stringify(matches)}`);await check(type+' double-click opens an independent read-only window at the exact retained context and backend value',"window.__popupCheck",'('+JSON.stringify(state)+')');
  await popup.evaluate("(()=>{const send=window.fetch;window.popupReads=[];window.fetch=async(...args)=>{const r=await send(...args);window.popupReads.push({path:args[0],body:args[1]?.body?JSON.parse(args[1].body):null,status:r.status});return r;};document.querySelector('#position-window-retry').click();})()");
  await popup.wait("document.body.getAttribute('aria-busy')==='false'&&window.popupReads.some(r=>r.path.endsWith('/trading/position'))&&!document.querySelector('#position-window-detail').hidden&&!!document.querySelector('#position-window-view .side-panel-section')",30000);
  const read=await popup.evaluate("window.popupReads.find(r=>r.path.endsWith('/trading/position')).body");
  if(read.context.snapshotId!==snapshot||read.context.knowledgeMode!=='retained'||read.context.contextId!==expected.context.contextId||read.tradeId!==selected.tradeId||read.bindingId!==selected.bindingId||read.revision!==Number(selected.revision))throw Error('The popup replaced its retained selection.');
  if(type==='ppa'){
   await popup.wait("document.querySelector('.position-schedules')?.textContent.includes('under this Blotter')",30000);
   await popup.evaluate("(()=>{const root=document.querySelector('.position-schedules');for(const select of root.querySelectorAll('.schedule-fields select')){select.value=select.options[1].value;select.dispatchEvent(new Event('change',{bubbles:true}));}root.querySelector('.schedule-fields button').click();})()");
   await popup.wait("window.popupReads.some(r=>r.path.endsWith('/ppa/inspect')&&r.status===200)&&!document.querySelector('.position-schedules').textContent.includes('Loading selected month…')",30000);
   const bounded=await popup.evaluate("(()=>{const tables=document.querySelector('.position-schedules').querySelectorAll('table'),rows=tables[tables.length-1].querySelectorAll('tbody tr');return rows.length>0&&rows.length<=48;})()");
   await evaluate(`window.__popupCheck=${bounded}`);await check('the PPA window uses bounded backend schedule pages while the parent total stays unavailable',"window.__popupCheck&&document.querySelector('#futures-page').dataset.pricingState==='failed'");last={popup,id:target.targetId};
  }
  const picture=await popup.command('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(join(artifacts,'position-'+type+'.png'),Buffer.from(picture.data,'base64'));
  if(type!=='ppa'){await close(popup,target.targetId);await check('closing the '+type+' trade window leaves the parent Blotter open',"!!document.querySelector('#futures-page:not([hidden])')");}
 }
 await persona('editor');await last.popup.evaluate("document.querySelector('#position-window-retry').click()");
 await last.popup.wait("document.body.getAttribute('aria-busy')==='false'&&!document.querySelector('#position-window-error').hidden&&document.querySelector('#position-window-detail').hidden",30000);
 const cleared="document.title==='Trade details · LeanETRM'&&document.querySelector('#position-window-title').textContent==='Trade details'&&document.querySelector('#position-window-context').textContent===''&&Object.keys(document.querySelector('#position-window-detail').dataset).length===0&&!document.querySelector('.side-panel-figure .kpi-value')";
 const denied=await last.popup.evaluate("window.popupReads.some(r=>r.path.endsWith('/trading/position')&&r.status===403)&&"+cleared);
 await evaluate(`window.__popupCheck=${denied}`);await check('current authority is checked again and denial clears the previously displayed trade',"window.__popupCheck");
 const beforeInvalid=await last.popup.evaluate('window.popupReads.length');
 await last.popup.evaluate("location.hash='context=missing'");await last.popup.wait("document.body.getAttribute('aria-busy')==='false'&&!document.querySelector('#position-window-error').hidden&&document.querySelector('#position-window-error').textContent.includes('exact position and retained market context')&&document.querySelector('#position-window-detail').hidden");
 const invalid=await last.popup.evaluate("document.querySelector('#position-window-error').textContent.includes('exact position and retained market context')&&"+cleared);
 if(await last.popup.evaluate('window.popupReads.length')!==beforeInvalid)throw Error('A malformed popup context made an input read.');
 await evaluate(`window.__popupCheck=${invalid}`);await check('a malformed popup context refuses without a current-data fallback',"window.__popupCheck");await close(last.popup,last.id);
 await check('the PPA window also closes after a refusal without closing the original workspace',"!!document.querySelector('#profile-open')");
}
