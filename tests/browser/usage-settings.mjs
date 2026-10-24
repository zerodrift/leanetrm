import {sideSession} from './side-session.mjs';
import {qualifyProvider,pricedModel} from './model-fixture.mjs';

// The Usage tab of Settings › AI, after the Agents scenario in the assistant check's preview. An independent session
// prices a model and tests it once against the fake provider, so the report holds a priced provider test of this run in
// a currency reference this run names. The preview's other checks also record calls, so the page is compared with the
// API's own answers rather than with fixed totals; the SQL and HTTP checks own the figures.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,errors,url}){
  await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
  await evaluate(`window.__requests=[];window.__originalFetch=window.fetch;window.fetch=async(...args)=>{window.__requests.push({path:String(args[0]),body:args[1]?.body?JSON.parse(args[1].body):null});return window.__originalFetch(...args);};`
    +"window.__api=async(path,body)=>{const boot=await(await window.__originalFetch('/api/bootstrap')).json();const r=await window.__originalFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(body)});return {status:r.status,data:await r.json()};};"
    // Each CSV the page saves is kept for inspection instead of downloaded.
    +"window.__csv=[];const create=URL.createObjectURL;URL.createObjectURL=b=>{window.__csv.push(b);return create(b);};"
    +"const save=HTMLAnchorElement.prototype.click;HTMLAnchorElement.prototype.click=function(){if(this.download==='leanetrm-usage.csv')return;return save.call(this);};");
  const stamp=Date.now(),reader='browser_usage_reader_'+stamp,manager='browser_usage_admin_'+stamp,labeled='browser_usage_labels_'+stamp,pricer='browser_usage_pricing_'+stamp;
  // A session can sign in only as the accounts it created: the page creates its readers, the side session its pricer.
  await persona('admin');
  for(const [login,display,roles] of [[reader,'Browser usage reader',['synthetic_usage_reader']],[manager,'Browser usage administrator',['access_administrator']],
    [labeled,'Browser usage labels',['access_administrator','viewer']]]){
    const created=await evaluate(`__api('admin/create',${JSON.stringify({operationId:crypto.randomUUID(),login,name:display,kind:'human',roles})})`);
    if(created.status!==200)throw Error('Synthetic persona setup failed: '+JSON.stringify(created));
  }
  const side=await sideSession(url);
  await side.call('admin/create',{operationId:crypto.randomUUID(),login:pricer,name:'Browser usage pricing',kind:'human',roles:['access_administrator','viewer']});
  await side.call('profile',{profile:pricer});
  await qualifyProvider(side,'sk-synthetic-browser-usage-'+stamp,{enable:false});
  const added=await pricedModel(side,{identifier:'browser-usage-model-'+stamp,displayName:'Browser usage model',efforts:['low']}),{usd}=added;
  const tested=await side.call('settings/ai/providers/test',{operationId:crypto.randomUUID(),modelId:added.id,reasoning:'low'});
  if(tested.outcome!=='succeeded'||tested.cost?.status!=='priced')throw Error('The synthetic provider test recorded no priced call: '+JSON.stringify(tested));

  // The test started now, so a UTC range from yesterday up to the day after tomorrow holds it whenever this runs.
  const day=offset=>new Date(Date.now()+offset*86400000).toISOString().slice(0,10),from=day(-1),to=day(2);
  const shown="!document.querySelector('#error').hidden&&document.querySelector('#error').textContent";
  const sent=path=>`window.__requests.filter(r=>r.path==='/api/settings/ai/usage${path}')`;
  const tabs="['usage','models','providers','agents'].filter(t=>!document.querySelector('#ai-tab-'+t).hidden).join()";
  const retained="Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.usage.request.'))";
  const audit="(await(await window.__originalFetch('/api/settings/ai/usage')).json()).audit";
  const auditRows="[...document.querySelectorAll('#ai-usage-audit tbody tr')].map(r=>[...r.cells].map(c=>c.textContent))";
  const sections="[...document.querySelectorAll('#ai-usage-sections .ai-usage-currency')]";
  const csv="(await window.__csv.at(-1).text())";
  // This run's currency reference in an API report, by its exact identity.
  const mine=`r.currencies.findIndex(s=>s.currency.catalogId===${JSON.stringify(usd.catalogId)}&&s.currency.revisionId===${JSON.stringify(usd.revisionId)}&&s.currency.code==='usd')`;
  const request={zone:'UTC',from,to,by:'model'};
  async function fill({zone='UTC',start=from,end=to,by='model'}={}){await set('#ai-usage-zone',zone);await set('#ai-usage-from',start);await set('#ai-usage-to',end);await set('#ai-usage-by',by);}
  const usageTab=async()=>{await click('#nav-ai');await click('#ai-tab-usage');};

  // Usage is offered by ReadModelUsage alone, first among the tabs.
  await evaluate('window.__requests=[]');await persona(reader);
  await check('an identity holding only ReadModelUsage lands on Settings › AI, is offered the Usage tab alone and reads only its directory',
    `!document.querySelector('#ai-page').hidden&&${tabs}==='usage'&&!document.querySelector('#ai-usage').hidden&&document.querySelector('#ai-models').hidden&&document.querySelector('#error').hidden`
    +"&&window.__requests.filter(r=>r.path.startsWith('/api/settings/ai/')).every(r=>r.path==='/api/settings/ai/usage')&&window.__requests.some(r=>r.path==='/api/settings/ai/usage')");
  await check('the tab states the longest range the API passes, and no grouping or day is chosen for the reader',
    "document.querySelector('#ai-usage-range').textContent.startsWith('Up to 366 days')&&document.querySelector('#ai-usage-by').value===''"
    +"&&JSON.stringify([...document.querySelector('#ai-usage-by').options].map(o=>[o.value,o.textContent]))==='[[\"\",\"Choose a grouping…\"],[\"model\",\"Model\"],[\"user\",\"User\"],[\"agent\",\"Agent\"]]'"
    +"&&document.querySelector('#ai-usage-from').value===''&&document.querySelector('#ai-usage-to').value===''&&document.querySelector('#ai-usage-report').hidden");
  await persona(manager);await click('#nav-ai');
  await check('an access administrator is offered all four tabs, Usage first, and still opens on Models & prices',
    `${tabs}==='usage,models,providers,agents'&&document.querySelector('.tabs[aria-label="AI settings"] .ai-tab').id==='ai-tab-usage'&&!document.querySelector('#ai-models').hidden&&document.querySelector('#ai-usage').hidden`);

  // The zone starts as Profile's and stays as chosen.
  await persona(labeled);
  await click('#profile-open');await set('#zone','America/New_York');await submit('#preferences-form');
  await usageTab();
  await check("the reporting zone is prefilled from Profile",`!document.querySelector('#ai-usage').hidden&&document.querySelector('#ai-usage-zone').value==='America/New_York'`);
  await set('#ai-usage-zone','UTC');
  await click('#profile-open');await set('#zone','Asia/Tokyo');await submit('#preferences-form');
  await check('a zone chosen for the report stays chosen when Profile changes',"document.querySelector('#ai-usage-zone').value==='UTC'");

  // Nothing blank is inferred; the server decides whether the rest make a report.
  await fill({by:''});await submit('#ai-usage-form');
  await check('a report without a grouping is refused before any request',`${shown}==='Choose whether to group by model, user or agent.'&&${sent('/report')}.length===0`);
  await fill({start:''});await submit('#ai-usage-form');
  await check('so is one without its first day',`${shown}==='Enter the first day.'&&${sent('/report')}.length===0`);
  await fill({start:to,end:from});await submit('#ai-usage-form');
  await check('a range that ends before it starts is refused by the server',
    `${shown}.startsWith('Enter a From date before the To date')&&${sent('/report')}.length===1&&document.querySelector('#ai-usage-report').hidden`);
  await fill();await evaluate("(()=>{const zone=document.querySelector('#ai-usage-zone');zone.append(new Option('Mars/Olympus','Mars/Olympus'));zone.value='Mars/Olympus';})()");
  await submit('#ai-usage-form');
  await check('a zone the database does not name is refused by the server',
    `${shown}.startsWith('Choose a time zone the preview’s database knows')&&${sent('/report')}.at(-1).body.zone==='Mars/Olympus'&&document.querySelector('#ai-usage-report').hidden`);
  // A report in a format the page does not read is refused, never shown; a stubbed answer changes the format.
  await evaluate("window.__format=true;window.__tracked0=window.fetch;window.fetch=async(...args)=>{const r=await window.__tracked0(...args);"
    +"if(!window.__format||String(args[0])!=='/api/settings/ai/usage/report'||!r.ok)return r;window.__format=false;const d=await r.json();d.reportFormat='usage_report_v2';"
    +"return new Response(JSON.stringify(d),{status:200,headers:{'Content-Type':'application/json'}});}");
  await fill();await submit('#ai-usage-form');
  await check('a report in a format the page does not read is refused by name and not shown',
    `${shown}.startsWith('This usage report is in a format this page does not read')&&document.querySelector('#ai-usage-report').hidden`);

  await fill();await submit('#ai-usage-form');
  await check('a report sends exactly its zone, range and grouping, and states them with its UTC bounds',
    `JSON.stringify(${sent('/report')}.at(-1).body)===${JSON.stringify(JSON.stringify(request))}&&document.querySelector('#error').hidden&&!document.querySelector('#ai-usage-report').hidden`
    +`&&document.querySelector('#ai-usage-bounds').textContent.startsWith('UTC · ${from} up to ${to}, not included · UTC ${from}T00:00:00+00:00 to ${to}T00:00:00+00:00 · by model · generated ')`);
  await check("the report shows the API's currency references in its order, each with its rows, its provider tests and its summary, and this run's test priced",
    `(async()=>{const r=(await window.__api('settings/ai/usage/report',${JSON.stringify(request)})).data,shownSections=${sections},index=${mine};`
    +"if(index<0||shownSections.length!==r.currencies.length)return false;"
    +"return r.currencies.every((s,i)=>{const rows=[...shownSections[i].querySelectorAll('tbody tr')].map(t=>[...t.cells].map(c=>c.textContent)),tests=s.rows.find(x=>x.member.kind==='tests'),"
    +"row=rows.find(x=>x[0]==='Provider tests');return rows.length===s.rows.length&&rows.at(-1)[0]===(tests?'Provider tests':rows.at(-1)[0])"
    +"&&(!tests||!!row&&row[1]===tests.calls.total.toLocaleString('en-US')&&row[2]===(tests.pricedCost?.display??'No priced calls')"
    +"&&row[3]===tests.calls.withoutCost.toLocaleString('en-US'))"
    +"&&shownSections[i].querySelector('li').textContent.startsWith(s.total.calls.total.toLocaleString('en-US')+(s.total.calls.total===1?' call: ':' calls: '));})"
    +"&&r.currencies[index].rows.some(x=>x.member.kind==='tests'&&x.calls.priced>=1)&&!!shownSections[index].querySelector('.chart canvas');})()");
  await check("a reader holding ReadMarketData sees this run's currency named as its reference data names it",
    `(async()=>{const r=(await window.__api('settings/ai/usage/report',${JSON.stringify(request)})).data;`
    +`return ${sections}[${mine}].querySelector('h3').textContent.startsWith(${JSON.stringify(`${usd.displayCode} · ${usd.name}`)});})()`);

  // An export records the report it carries, which the audit lists; the CSV carries the exact reference.
  await persona(reader);
  await fill();await submit('#ai-usage-form');
  await check("a reader without ReadMarketData sees no name for the currency, only its code",
    `(async()=>{const r=(await window.__api('settings/ai/usage/report',${JSON.stringify(request)})).data;return ${sections}[${mine}].querySelector('h3').textContent.startsWith('Currency not readable · usd');})()`);
  const exported=await evaluate(`${sent('/export')}.length`);
  await click('#ai-usage-export');
  const tests=JSON.stringify(`","${usd.catalogId}","${usd.revisionId}","usd","Provider tests","Provider tests","",`);
  await check('Export sends only its identity, zone, range and grouping, saves the CSV of the report it recorded under that identity, lists it first and '
    +'keeps no request',
    `(async()=>{const b=${sent('/export')}.at(-1)?.body,a=${audit},lines=${csv}.split('\\r\\n');`
    +`return ${sent('/export')}.length===${exported+1}&&Object.keys(b).sort().join()==='by,from,operationId,to,zone'&&a[0].operationId===b.operationId`
    +`&&lines.includes('"Generated","'+a[0].at+'"')&&lines.includes('"Export ID","'+b.operationId+'"')&&['"Usage report"','"Report format","usage_report_v1"','"Time zone","UTC"','"From","${from}"','"To, not included","${to}"','"Grouped by","Model"',`
    +`'"Cost method","token_cost_v1"','"Ratio method","token_cost_report_v1"'].every(l=>lines.includes(l))&&lines.some(l=>l.includes(${tests}))`
    +`&&JSON.stringify(${auditRows}[0].slice(1))===${JSON.stringify(JSON.stringify(['Browser usage reader','UTC',from,to,'Model']))}&&!${retained}&&document.querySelector('#error').hidden;})()`);
  // A member name a spreadsheet could read as a formula; a stubbed answer adds one.
  await evaluate("window.__formula=true;window.__tracked1=window.fetch;window.fetch=async(...args)=>{const r=await window.__tracked1(...args);"
    +"if(!window.__formula||String(args[0])!=='/api/settings/ai/usage/export'||!r.ok)return r;window.__formula=false;const d=await r.json(),s=d.currencies[0];"
    +"s.rows.unshift({...s.rows[0],member:{kind:'agent',id:crypto.randomUUID(),name:'=Synthetic formula'}});"
    +"return new Response(JSON.stringify(d),{status:200,headers:{'Content-Type':'application/json'}});}");
  await click('#ai-usage-export');
  await check('a member named like a formula shows as text and exports apostrophe-prefixed',
    `(async()=>${csv}.split('\\r\\n').some(l=>l.includes('","Model","'+"'"+'=Synthetic formula","'))`
    +`&&${sections}[0].querySelector('tbody tr').cells[0].textContent==='=Synthetic formula')()`);

  // An export answered in a format the page does not read is refused by name. The file is built before the request is
  // released, so no CSV is saved and the request stays offered; Retry answers with the report that export recorded.
  await evaluate("window.__exportFormat=true;window.__tracked4=window.fetch;window.fetch=async(...args)=>{const r=await window.__tracked4(...args);"
    +"if(!window.__exportFormat||String(args[0])!=='/api/settings/ai/usage/export'||!r.ok)return r;window.__exportFormat=false;const d=await r.json();"
    +"d.reportFormat='usage_report_v2';return new Response(JSON.stringify(d),{status:200,headers:{'Content-Type':'application/json'}});}");
  const files=await evaluate('window.__csv.length');
  await fill({by:'user'});await click('#ai-usage-export');
  const unread=await evaluate(`JSON.parse(sessionStorage.getItem(${retained}))`);
  await check('an export answered in a format the page does not read is refused by name, saves no CSV and keeps its request for Retry',
    `(async()=>${shown}.startsWith('This usage report is in a format this page does not read')&&!document.querySelector('#ai-usage-pending').hidden`
    +`&&document.querySelector('#ai-usage-retry').checkVisibility()&&window.__csv.length===${files}&&${audit}[0].operationId===${JSON.stringify(unread?.operationId)})()`);
  await click('#ai-usage-retry');
  await check('Retry answers with the report that export recorded and saves its CSV, keeping no request',
    `(async()=>window.__csv.length===${files+1}&&${csv}.split('\\r\\n').includes('"Export ID","'+${JSON.stringify(unread?.operationId)}+'"')`
    +`&&${csv}.split('\\r\\n').includes('"Grouped by","User"')&&document.querySelector('#ai-usage-pending').hidden&&!${retained}&&document.querySelector('#error').hidden)()`);

  // A lost answer after the server recorded the export keeps the request for the identity that sent it; Retry answers
  // with the recorded report and records nothing more.
  await evaluate("window.__dropExport=true;window.__tracked2=window.fetch;window.fetch=async(...args)=>{const r=await window.__tracked2(...args);"
    +"if(window.__dropExport&&String(args[0])==='/api/settings/ai/usage/export'){window.__dropExport=false;throw new TypeError('Synthetic lost export response');}return r;}");
  const saved=await evaluate('window.__csv.length');
  await fill({by:'agent'});await click('#ai-usage-export');
  const kept=await evaluate(`JSON.parse(sessionStorage.getItem(${retained}))`);
  await check('a lost export response keeps the exact request, says what it would export, and the audit shows the export was recorded',
    `(async()=>!document.querySelector('#ai-usage-pending').hidden&&!!(${shown})&&document.querySelector('#ai-usage-retry').checkVisibility()`
    +`&&document.querySelector('#ai-usage-pending-text').textContent.startsWith('An export of usage in UTC from ${from} up to ${to}, by agent, has no confirmed outcome.')`
    +`&&${audit}[0].operationId===${JSON.stringify(kept?.operationId)}&&window.__csv.length===${saved}&&document.querySelector('#ai-usage-export').disabled)()`);
  await persona(manager);await usageTab();
  await check('another identity is not offered the kept request',"!document.querySelector('#ai-usage').hidden&&document.querySelector('#ai-usage-pending').hidden");
  await persona(reader);
  await check('the identity that sent it is offered it again',
    `!document.querySelector('#ai-usage-pending').hidden&&JSON.parse(sessionStorage.getItem(${retained})).operationId===${JSON.stringify(kept?.operationId)}`);
  await click('#ai-usage-retry');
  await check('Retry resends the same request, which answers with the recorded report, listed once',
    `(async()=>{const same=${sent('/export')}.filter(r=>r.body.operationId===${JSON.stringify(kept?.operationId)}),a=${audit};`
    +"return same.length===2&&JSON.stringify(same[0].body)===JSON.stringify(same[1].body)&&a.filter(e=>e.operationId===same[0].body.operationId).length===1"
    +`&&a[0].operationId===same[0].body.operationId&&${csv}.split('\\r\\n').includes('"Generated","'+a[0].at+'"')&&${csv}.split('\\r\\n').includes('"Grouped by","Agent"')`
    +`&&document.querySelector('#ai-usage-pending').hidden&&!${retained}&&document.querySelector('#error').hidden;})()`);
  // A request whose answer never came is kept until Discard, which sends nothing.
  await evaluate("window.__failExport=true;window.__tracked3=window.fetch;window.fetch=async(...args)=>{if(window.__failExport&&String(args[0])==='/api/settings/ai/usage/export'){"
    +"window.__failExport=false;throw new TypeError('Synthetic network failure before sending');}return window.__tracked3(...args);}");
  const attempts=await evaluate(`${sent('/export')}.length`);
  await fill();await click('#ai-usage-export');
  await check('an unanswered export is kept with Retry and Discard',`!document.querySelector('#ai-usage-pending').hidden&&!!${retained}&&${sent('/export')}.length===${attempts}`);
  await click('#ai-usage-discard');
  await check('Discard forgets the kept request without sending it',`document.querySelector('#ai-usage-pending').hidden&&!${retained}&&${sent('/export')}.length===${attempts}`);

  await fill();await submit('#ai-usage-form');
  await evaluate('window.scrollTo(0,0)');await shot('ai-usage-desktop');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate('window.scrollTo(0,0)');
  await check('the Usage tab, its report and its tabs avoid page-wide horizontal overflow on a phone',`!document.querySelector('#ai-usage-report').hidden&&${fitsWidth}`);await shot('ai-usage-mobile');
  await check('the Usage tab exposes no internal GUIDs',"!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(document.body.innerText)");
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});

  // An independent administrator removes access while the page stays open: an export refused because access ended,
  // like a read refused for it, keeps no request and leaves no usage, form or audit data on screen.
  const wiped="document.querySelector('#ai-usage-report').hidden&&document.querySelector('#ai-usage-sections').children.length===0&&document.querySelector('#ai-usage-audit').children.length===0"
    +"&&document.querySelector('#ai-usage-zone').options.length===0&&document.querySelector('#ai-usage-by').options.length===1&&document.querySelector('#ai-usage-from').value===''"
    +"&&document.querySelector('#ai-usage-to').value===''&&document.querySelector('#ai-usage-bounds').textContent===''&&document.querySelector('#ai-usage-range').textContent===''";
  // The pricing session administers access too, so the run opens no third session: a preview holds at most 64.
  const revoke=async login=>side.replaceRoles((await side.call('admin')).members.find(m=>m.login===login),['viewer']);
  await revoke(reader);
  const refused=await evaluate(`${sent('/export')}.length`);
  await click('#ai-usage-export');
  await check('an export refused because access ended keeps no request, and the reader keeps no usage data',
    `${sent('/export')}.length===${refused+1}&&${shown}.includes('make that change')&&!${retained}&&${wiped}`);
  await persona(manager);await usageTab();await fill();await submit('#ai-usage-form');
  await revoke(manager);
  await click('#ai-usage-refresh');
  await check('a read refused because access ended leaves the administrator no usage data',`${shown}.includes('make that change')&&${wiped}`);
  await check('no uncaught JavaScript exceptions',JSON.stringify(errors.length===0));
}
