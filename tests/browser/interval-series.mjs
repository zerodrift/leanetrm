import {sideSession} from './side-session.mjs';
import {knownAtAssertion,observeCatalogClock} from './known-at.mjs';

// Interval series screen: definition, CSV import, lost-reply recovery, corrections, as-of reads and read-only access.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,fitsWidth,errors,url}){
 const table=id=>`[...document.querySelectorAll('#${id} tbody tr')].map(r=>[...r.cells].map(c=>c.textContent))`;
 const values=table('interval-series-values');
 const pause=milliseconds=>evaluate(`new Promise(accept=>setTimeout(accept,${milliseconds}))`);
 const choose=async code=>{await evaluate(`(()=>{const s=document.querySelector('#interval-series-select');s.value=[...s.options].find(o=>o.textContent.includes(${JSON.stringify(code)})).value;s.dispatchEvent(new Event('change',{bubbles:true}));})()`);await wait(idle);};
 const headIs=(code,head)=>`(async()=>(await(await fetch('/api/interval-series')).json()).series.find(s=>s.code===${JSON.stringify(code)}).head===${head})()`;
 const importCsv=async(reason,csv)=>{await set('#interval-series-reason',reason);await set('#interval-series-csv',csv);await submit('#interval-series-import-form');};
 const selected=text=>`document.querySelector('#interval-series-selected').textContent===${JSON.stringify(text)}`;
 const press=async key=>{for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key,code:key,windowsVirtualKeyCode:{ArrowDown:40,ArrowUp:38,Enter:13}[key]});};
 await command('Runtime.enable');await command('Page.enable');await command('Page.navigate',{url:url+'/'});
 await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 // Every revise exchange is recorded; loseNextRevise drops one committed reply, as a broken connection would.
 await evaluate("window.reviseExchanges=[];window.loseNextRevise=false;{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/interval-series/revise')){window.reviseExchanges.push({body:JSON.parse(args[1].body),status:response.status,payload:await response.clone().json()});if(window.loseNextRevise){window.loseNextRevise=false;throw Error('Synthetic lost committed reply');}}return response;};}");
 await observeCatalogClock(evaluate,'/api/interval-series');
 await persona('editor');await click('#nav-interval-series');
 await check('interval browsing shows a common floating context without requiring a window',"document.querySelector('#interval-series-form').closest('.floating-context')!==null&&document.querySelector('#interval-series-values-note').textContent.length>0&&document.querySelector('#date-context').hidden");
 await check('editors see the page with definition and import controls',"!document.querySelector('#interval-series-page').hidden&&document.querySelector('#page-title').textContent==='Interval data'&&!document.querySelector('#interval-series-define').hidden&&!document.querySelector('#interval-series-revise-card').hidden&&document.querySelector('#error').hidden");

 await check('definition choices start unselected rather than assumed',"['kind','specification','clock','minutes'].every(n=>document.querySelector('#interval-series-'+n).value==='')&&document.querySelectorAll('#interval-series-specification option').length===1&&document.querySelectorAll('#interval-series-clock option').length>1&&[...document.querySelectorAll('#interval-series-minutes option')].map(o=>o.value).join()===',5,15,30,60'");
 await set('#interval-series-kind','additive_price_shape');
 await check('the specification list follows the chosen kind',"[...document.querySelectorAll('#interval-series-specification option')].some(o=>o.value==='test_power_outright')&&![...document.querySelectorAll('#interval-series-specification option')].some(o=>o.value==='test_zero_rate')");
 await set('#interval-series-code','browser_capacity');await set('#interval-series-name','Synthetic browser capacity');await set('#interval-series-kind','capacity_factor_forecast');
 await set('#interval-series-specification','test_zero_rate');await set('#interval-series-clock','synthetic_eastern');await set('#interval-series-minutes','60');await submit('#interval-series-define-form');
 await check('a defined series is selected and described from its pinned terms',"document.querySelector('#interval-series-select').selectedOptions[0]?.textContent.includes('browser_capacity')&&document.querySelector('#interval-series-summary').textContent.includes('60-minute intervals')&&document.querySelector('#interval-series-defined').textContent.includes('browser_capacity')&&document.querySelector('#interval-series-kind').value===''&&document.querySelector('#error').hidden");

 await set('#interval-series-code','browser_capacity');await set('#interval-series-name','Synthetic duplicate');await set('#interval-series-kind','capacity_factor_forecast');
 await set('#interval-series-specification','test_zero_rate');await set('#interval-series-clock','synthetic_eastern');await set('#interval-series-minutes','60');await submit('#interval-series-define-form');
 await check('a refused definition leaves nothing outstanding and keeps its form',"document.querySelector('#error').textContent.includes('unused code')&&document.querySelector('#interval-series-define-pending').hidden&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.interval-series.request.'))&&!document.querySelector('#interval-series-define-fields').disabled&&document.querySelector('#interval-series-code').value==='browser_capacity'");

 await set('#interval-series-from','2027-01-01T05:00');await set('#interval-series-to','2027-01-01T08:00');
 await check('Known at starts at the catalog time and names the profile zone',`${knownAtAssertion("document.querySelector('#interval-series-known')",'window.catalogKnownAt','UTC')}&&document.querySelector('label[for=interval-series-known]').textContent==='Knowledge cutoff · UTC'`);
 await importCsv('Synthetic browser import','start,value\r\n2027-01-01T05:00:00Z,0.25\r\n2027-01-01T06:00:00Z,0.5\r\n2027-01-01T07:00:00Z,0.75\r\n');
 await check('an import saves one revision and shows its three values',`${values}.length===3&&${values}.map(r=>r[2]+'@'+r[3]).join()==='0.25@1,0.5@1,0.75@1'&&${values}[0][0]==='Jan 1, 2027, 5:00 AM'&&document.querySelector('#interval-series-saved').textContent==='Saved revision 1 · 3 intervals.'&&document.querySelector('#interval-series-reason').value===''&&document.querySelector('#interval-series-csv').value===''&&document.querySelector('#error').hidden`);

 await importCsv('Synthetic local time','start,value\n2027-01-01T08:00:00Z,0.9\n2027-01-01T09:00:00,0.8\n');
 await check('a local time in the CSV is refused with its row before anything is sent',"document.querySelector('#error').textContent.startsWith('Row 3:')&&window.reviseExchanges.length===1&&document.querySelector('#interval-series-pending').hidden&&document.querySelector('#interval-series-csv').value.includes('09:00:00,0.8')");

 await importCsv('Synthetic bad value','start,value\n2027-01-01T08:00:00Z,1e3\n');
 await check('a malformed value is refused and leaves nothing outstanding, with the CSV and controls intact',"!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('up to 18 decimal places')&&window.reviseExchanges.length===2&&window.reviseExchanges[1].status===422&&window.reviseExchanges[1].payload.code==='numeric_format'&&document.querySelector('#interval-series-pending').hidden&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.interval-series.request.'))&&document.querySelector('#interval-series-csv').value.includes('1e3')&&!document.querySelector('#interval-series-select').disabled&&!document.querySelector('#interval-series-revise-fields').disabled&&!document.querySelector('#interval-series-csv').disabled");

 await set('#interval-series-to','2027-01-01T09:00');await evaluate('window.reviseExchanges=[];window.loseNextRevise=true');
 await importCsv('Synthetic lost reply','start,value\n2027-01-01T08:00:00Z,0.9\n');
 await check('a lost reply keeps the exact request and offers a retry',`window.reviseExchanges.length===1&&window.reviseExchanges[0].status===200&&!document.querySelector('#interval-series-pending').hidden&&document.querySelector('#interval-series-retry').textContent==='Retry original save'&&document.querySelector('#interval-series-discard').textContent==='Discard request after checking'&&document.querySelector('#interval-series-revise-fields').disabled&&document.querySelector('#interval-series-select').disabled&&JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.interval-series.request.')&&!k.endsWith('.define')))).operationId===window.reviseExchanges[0].body.requestId`);
 await click('#nav-market');await click('#nav-interval-series');
 await check('an outstanding request survives leaving and reopening the page',"!document.querySelector('#interval-series-pending').hidden&&document.querySelector('#interval-series-select').selectedOptions[0]?.textContent.includes('browser_capacity')&&document.querySelector('#interval-series-select').disabled&&document.querySelector('#interval-series-revise-fields').disabled");
 await click('#interval-series-retry');
 await check('retrying yields the same revision and leaves the series head unchanged',`window.reviseExchanges.length===2&&window.reviseExchanges[1].status===200&&window.reviseExchanges[1].body.requestId===window.reviseExchanges[0].body.requestId&&JSON.stringify(window.reviseExchanges[1].payload)===JSON.stringify(window.reviseExchanges[0].payload)&&window.reviseExchanges[1].payload.revision===2&&document.querySelector('#interval-series-pending').hidden&&${values}.length===4&&${values}[3][2]==='0.9'&&document.querySelector('#interval-series-saved').textContent==='Saved revision 2 · 1 interval.'`);
 await check('the committed head is revision 2, not 3',headIs('browser_capacity',2));

 // Known at has whole-second precision, so the chosen instant must fall between two revisions by more than a second.
 await pause(1200);await click('#interval-series-refresh');
 const before=await evaluate("document.querySelector('#interval-series-known').value");
 await check('Correct and Withdraw wait for a chosen interval and the values hold no buttons',`!document.querySelector('#interval-series-selection').hidden&&${selected('Choose a row to correct or withdraw its interval.')}&&document.querySelector('#interval-series-correct').disabled&&document.querySelector('#interval-series-withdraw').disabled&&!document.querySelector('#interval-series-values tbody button')`);
 await click('#interval-series-values tbody tr:nth-child(2)');
 await check('clicking a row chooses its interval and shows its current value',`${selected('Interval starting Jan 1, 2027, 6:00 AM · value 0.5')}&&document.querySelector('#interval-series-values tbody tr:nth-child(2)').getAttribute('aria-current')==='true'&&document.querySelectorAll('#interval-series-values tbody tr[aria-current]').length===1&&!document.querySelector('#interval-series-correct').disabled&&!document.querySelector('#interval-series-withdraw').disabled`);
 await click('#interval-series-correct');
 await check('Correct stages one interval with its current value',"!document.querySelector('#interval-series-change-form').hidden&&document.querySelector('#interval-series-change-value').value==='0.5'&&document.querySelector('#interval-series-change-title').textContent.includes('6:00 AM')");
 await click('#interval-series-values tbody tr:nth-child(1)');
 await check('choosing another interval closes the open change',`document.querySelector('#interval-series-change-form').hidden&&${selected('Interval starting Jan 1, 2027, 5:00 AM · value 0.25')}`);
 await click('#interval-series-values tbody tr:nth-child(2)');await click('#interval-series-correct');
 await set('#interval-series-reason','Synthetic bad correction');await set('#interval-series-change-value','1e3');await submit('#interval-series-change-form');
 await check('a malformed correction is refused, nothing is outstanding and the form stays open',"document.querySelector('#error').textContent.includes('up to 18 decimal places')&&document.querySelector('#interval-series-pending').hidden&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.interval-series.request.'))&&!document.querySelector('#interval-series-change-form').hidden&&document.querySelector('#interval-series-change-value').value==='1e3'&&!document.querySelector('#interval-series-select').disabled&&!document.querySelector('#interval-series-revise-fields').disabled");
 await set('#interval-series-reason','Synthetic correction');await set('#interval-series-change-value','0.4');await submit('#interval-series-change-form');
 await check('a correction is a new revision of that interval',`${values}[1][2]==='0.4'&&${values}[1][3]==='3'&&${values}[0][3]==='1'&&document.querySelector('#interval-series-saved').textContent==='Saved revision 3 · 1 interval.'&&document.querySelector('#interval-series-change-form').hidden&&${selected('Choose a row to correct or withdraw its interval.')}`);
 await set('#interval-series-known',before);await submit('#interval-series-form');
 await check('Known at before the correction shows the old value and offers no changes',`${values}[1][2]==='0.5'&&${values}[1][3]==='1'&&${values}.length===4&&document.querySelector('#interval-series-selection').hidden&&!document.querySelector('#interval-series-values tbody tr[tabindex]')&&!${table('interval-series-revisions')}.some(r=>r[0]==='3')`);
 await click('#interval-series-refresh');
 const editor=(await(await sideSession(url)).call('admin')).members.find(m=>m.login==='editor').name;
 const named=JSON.stringify(editor);
 await check('the change log shows who changed what, why and the previous value',`(()=>{const log=${table('interval-series-changes')}.find(r=>r[2]==='3'),summary=${table('interval-series-revisions')}.find(r=>r[0]==='3');return !!log&&!!summary&&summary[1]===${named}&&summary[2]==='Synthetic correction'&&summary[5]==='1'&&log[3]==='0.5'&&log[4]==='0.4'&&log[5]===${named}&&log[6]==='Synthetic correction';})()`);

 await click('#interval-series-values tbody tr:nth-child(1)');await click('#interval-series-refresh');
 await check('reloading clears the chosen interval',`${selected('Choose a row to correct or withdraw its interval.')}&&!document.querySelector('#interval-series-values tbody tr[aria-current]')&&document.querySelector('#interval-series-correct').disabled`);
 // One row is the tab stop: the arrow keys move it and Enter chooses the row.
 await evaluate(`document.querySelector('#interval-series-values tbody tr[tabindex="0"]').focus()`);
 for(const key of ['ArrowDown','ArrowDown','ArrowDown','ArrowUp','ArrowDown','Enter'])await press(key);
 await check('the keyboard moves between rows and Enter chooses one',`document.activeElement===document.querySelector('#interval-series-values tbody tr:nth-child(4)')&&document.querySelectorAll('#interval-series-values tbody tr[tabindex]').length===1&&${selected('Interval starting Jan 1, 2027, 8:00 AM · value 0.9')}`,
  `({focused:document.activeElement?.outerHTML.slice(0,120),stops:document.querySelectorAll('#interval-series-values tbody tr[tabindex]').length,chosen:document.querySelector('#interval-series-selected').textContent})`);
 await click('#interval-series-withdraw');
 await check('Withdraw asks for no value',"!document.querySelector('#interval-series-change-form').hidden&&document.querySelector('#interval-series-change-field').hidden&&document.querySelector('#interval-series-change-save').textContent==='Withdraw interval'");
 await set('#interval-series-reason','Synthetic withdrawal');await submit('#interval-series-change-form');
 await check('a withdrawn interval leaves the values and shows as withdrawn in the log',`${values}.length===3&&${table('interval-series-changes')}.some(r=>r[2]==='4'&&r[3]==='0.9'&&r[4]==='Withdrawn')`);

 // A colleague saves first: the stale expected revision is refused and nothing is left outstanding.
 const colleague=await sideSession(url,'editor'),seriesId=(await colleague.call('interval-series')).series.find(s=>s.code==='browser_capacity').id;
 await colleague.call('interval-series/revise',{requestId:crypto.randomUUID(),seriesId,expectedRevision:4,reason:'Synthetic colleague',segments:[{start:'2027-01-01T09:00:00Z',values:['0.1']}]});
 await importCsv('Synthetic stale import','start,value\n2027-01-01T10:00:00Z,0.2\n');
 await check('a revision conflict says to reload and leaves the CSV to reapply',"document.querySelector('#error').textContent.includes('Someone else saved this series first')&&document.querySelector('#interval-series-pending').hidden&&document.querySelector('#interval-series-csv').value.includes('10:00:00Z,0.2')");
 await click('#interval-series-refresh');await submit('#interval-series-import-form');
 await check('after reloading the same import saves on top of the colleague revision',`document.querySelector('#error').hidden&&document.querySelector('#interval-series-saved').textContent==='Saved revision 6 · 1 interval.'`);
 await check('the colleague revision and the reapplied import are both committed',headIs('browser_capacity',6));

 await click('#profile-open');await set('#zone','America/New_York');await submit('#preferences-form');
 await check('a time-zone change relabels the shown values in place',`${values}[0][0]==='Jan 1, 2027, 12:00 AM'&&document.querySelector('label[for=interval-series-known]').textContent==='Knowledge cutoff · America/New_York'`);
 const hours=Array.from({length:25},(_,i)=>new Date(Date.UTC(2026,10,1,4+i)).toISOString().replace('.000Z','Z'));
 await set('#interval-series-from','2026-11-01T00:00');await set('#interval-series-to','2026-11-02T00:00');
 const csv=part=>'start,value\n'+part.map(h=>h+',0.5').join('\n')+'\n';
 await importCsv('Synthetic fall-back morning',csv(hours.slice(0,12)));
 // Real commit times are never in a repeated hour, so the change log is served two revisions known at 1:30 AM before and after the fold.
 await evaluate("{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(!String(args[0]).endsWith('/interval-series/changes'))return response;const data=await response.clone().json(),known={7:'2026-11-01T05:30:00+00:00',8:'2026-11-01T06:30:00+00:00'};for(const r of [...data.revisions,...data.rows])r.knownFrom=known[r.revision]??r.knownFrom;return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});};}");
 await importCsv('Synthetic fall-back evening',csv(hours.slice(12)));
 await check('the fall-back day shows both repeated hours with their UTC offsets',`(()=>{const starts=${values}.map(r=>r[0]);return starts.length===25&&starts.filter(s=>s.includes(', 1:00 AM')).length===2&&starts.includes('Nov 1, 2026, 1:00 AM GMT-4')&&starts.includes('Nov 1, 2026, 1:00 AM GMT-5')&&new Set(starts).size===25;})()`,
  `({error:document.querySelector('#error').textContent,saved:document.querySelector('#interval-series-saved').textContent,starts:${values}.map(r=>r[0])})`);

 await check('Known from shows seconds and names the offset in the repeated hour',`${table('interval-series-revisions')}.map(r=>r[0]+'@'+r[3]).join()==='8@Nov 1, 2026, 1:30:00 AM GMT-5,7@Nov 1, 2026, 1:30:00 AM GMT-4'&&new Set(${table('interval-series-changes')}.map(r=>r[2]+'@'+r[7])).size===2&&${table('interval-series-changes')}.every(r=>r[7]===(r[2]==='8'?'Nov 1, 2026, 1:30:00 AM GMT-5':'Nov 1, 2026, 1:30:00 AM GMT-4'))&&/ known at [A-Z][a-z]{2} \\d+, 2026, \\d+:\\d\\d:\\d\\d [AP]M/.test(document.querySelector('#interval-series-values-note').textContent)`,"document.querySelector('#interval-series-values-note').textContent");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate("document.querySelector('#interval-series-define').open=true;document.querySelector('#interval-series-values tbody tr').click();document.querySelector('#interval-series-correct').click();window.scrollTo(0,0)");
 await check('the series page fits a 390 px phone with its forms open',fitsWidth);
 await command('Emulation.clearDeviceMetricsOverride');

 // The largest window: 366 days of quarter-hour values and their change log, timed from Show until both tables are painted.
 const quarterHours=await sideSession(url,'editor');
 const quarter=await quarterHours.call('interval-series/define',{requestId:crypto.randomUUID(),code:'browser_quarter_hour',name:'Synthetic browser quarter-hour',
  kind:'capacity_factor_forecast',specification:'test_zero_rate',clock:'synthetic_eastern',intervalMinutes:15});
 await quarterHours.call('interval-series/revise',{requestId:crypto.randomUUID(),seriesId:quarter.seriesId,expectedRevision:0,reason:'Synthetic full window',
  segments:[{start:'2027-01-01T05:00:00Z',values:Array.from({length:35136},(_,i)=>'0.'+(i%9+1))}]});
 await click('#interval-series-refresh');await choose('browser_quarter_hour');
 await set('#interval-series-from','2027-01-01T00:00');await set('#interval-series-to','2028-01-02T00:00');
 // Painted is the first task after the frame that shows both tables.
 const timing=await evaluate(`new Promise(accept=>{
  const original=window.fetch;let replied;
  window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/interval-series/changes'))replied=performance.now();return response;};
  const started=performance.now();document.querySelector('#interval-series-form').requestSubmit();
  const shown=()=>document.body.getAttribute('aria-busy')==='false'&&document.querySelectorAll('#interval-series-changes tbody tr').length>0;
  const poll=()=>shown()?requestAnimationFrame(()=>setTimeout(()=>{window.fetch=original;accept({total:Math.round(performance.now()-started),afterReply:Math.round(performance.now()-replied)});})):setTimeout(poll,20);
  setTimeout(poll,20);})`);
 console.log(`MEASURE interval series 366-day quarter-hour window: ${timing.total} ms from Show until painted, ${timing.afterReply} ms of it after the change log arrived`);
 await check('the largest window renders every value and change',`${values}.length===35136&&${table('interval-series-changes')}.length===35136&&${table('interval-series-revisions')}.length===1&&${values}[35135][2]==='0.${35135%9+1}'&&document.querySelector('#error').hidden`);
 // A column filter lists every distinct value of its column, so the per-interval columns have none.
 await check('the largest window lists no per-interval column filters',"document.querySelectorAll('#interval-series-page .column-filter-row option').length<100",
  "[...document.querySelectorAll('#interval-series-page .column-filter-row select')].map(s=>s.getAttribute('aria-label')+':'+s.options.length)");
 // What the renderer holds with the largest window shown, then the next actions on it: starting a Correct on its last
 // interval, and opening Profile, which disables and enables every control on the page.
 await command('Performance.enable');
 const heap=Object.fromEntries((await command('Performance.getMetrics')).metrics.map(m=>[m.name,m.value]));
 const timed=action=>evaluate(`new Promise(accept=>{const started=performance.now();${action};
  const settled=()=>document.body.getAttribute('aria-busy')==='false'?requestAnimationFrame(()=>setTimeout(()=>accept(Math.round(performance.now()-started)))):setTimeout(settled,20);setTimeout(settled);})`);
 const correct=await timed("document.querySelector('#interval-series-values tbody tr:last-child').click();document.querySelector('#interval-series-correct').click()");
 await check('a Correct on the last interval of the largest window stages its value',"document.querySelector('#interval-series-change-title').textContent==='Correct the interval starting Jan 1, 2028, 11:45 PM'&&document.querySelector('#interval-series-change-value').value==='0.9'");
 const profile=await timed("document.querySelector('#profile-open').click()");
 console.log(`MEASURE interval series next actions on the largest window: ${correct} ms to choose its last interval and start a Correct, ${profile} ms to open Profile; renderer JS heap ${Math.round(heap.JSHeapUsedSize/2**20)} MiB used of ${Math.round(heap.JSHeapTotalSize/2**20)} MiB, ${heap.Nodes} DOM nodes`);
 await check('each next action on the largest window finishes within 5 s',`${correct}<5000&&${profile}<5000&&document.querySelector('#profile-dialog').open&&document.querySelector('#error').hidden`);

 await set('#profile','viewer');await click('#open');await click('#nav-interval-series');await choose('browser_capacity');
 await set('#interval-series-from','2027-01-01T05:00');await set('#interval-series-to','2027-01-01T09:00');await submit('#interval-series-form');
 await check('viewers read values and the change log but see no Import, Correct or Withdraw',`${values}.length===3&&${table('interval-series-changes')}.length>0&&document.querySelector('#interval-series-revise-card').hidden&&document.querySelector('#interval-series-define').hidden&&document.querySelector('#interval-series-selection').hidden&&!document.querySelector('#interval-series-values tbody tr[tabindex]')&&![...document.querySelectorAll('#interval-series-page button')].some(b=>b.offsetParent!==null&&/^(Import|Correct|Withdraw)/.test(b.textContent))&&document.querySelector('#error').hidden`);
 await check('no uncaught JavaScript exceptions',JSON.stringify(errors.length===0));
}
