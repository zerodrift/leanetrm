export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await persona('viewer');
 await check('market readers can open the shared contract-date library',"!document.querySelector('#nav-schedules').hidden&&!!document.querySelector('#schedule-tab-contracts')");
 await persona('editor');await click('#nav-schedules');await click('#schedule-tab-contracts');
 await set('#dates-import','2021-01-01\n2021-02-01');
 await check('an imported date list disables unrelated monthly generator controls',"['dates-first','dates-last','dates-anchor','dates-day','dates-short','dates-adjustment'].every(id=>document.getElementById(id).disabled)");
 await click('#date-context-latest');
 await check('monthly generator controls stay disabled after reading the imported list context',"document.querySelector('#dates-import').value==='2021-01-01\\n2021-02-01'&&['dates-first','dates-last','dates-anchor','dates-day','dates-short','dates-adjustment'].every(id=>document.getElementById(id).disabled)");
 await set('#dates-import','');await click('#current-view-context');
 await set('#curve-dates-example-kind','daily_power');await click('#curve-dates-example');
 await set('#curve-dates-code','browser_daily_dates');await set('#curve-dates-name','Synthetic shared daily dates');
 await submit('#curve-dates-form');await click('#curve-dates-keep');
 await check('reviewed literal daily dates save as a shared draft',"document.querySelector('#error').hidden&&document.querySelector('#curve-dates-saved').textContent.includes('Synthetic shared daily dates')&&document.querySelector('#curve-dates-version').textContent.includes('draft')","({error:document.querySelector('#error').textContent,state:document.querySelector('#curve-dates-version').textContent,library:document.querySelector('#curve-dates-saved').textContent})");
 await check('mapping fields stay inside the editor beside the calendar',"(()=>{const editor=document.querySelector('.schedule-workspace-editor'),fields=document.querySelector('#curve-dates-fields');return fields.getBoundingClientRect().right<=editor.getBoundingClientRect().right&&editor.scrollWidth<=editor.clientWidth+1;})()");
 await click('#curve-dates-reload');
 await check('the draft survives reloading the library',"document.querySelector('#curve-dates-saved').textContent.includes('Synthetic shared daily dates')");
 await evaluate(`window.contractDateCommands=[];window.corruptContractSave=true;window.failContractLibrary=false;{const original=window.fetch;window.fetch=async(...args)=>{const path=String(args[0]);if(path.endsWith('/contracts/save'))window.contractDateCommands.push(JSON.parse(args[1].body));const response=await original(...args);if(path.endsWith('/contracts/save')&&response.ok&&window.corruptContractSave){window.corruptContractSave=false;return new Response(JSON.stringify({...await response.json(),requestId:'different-request'}),{status:200,headers:{'Content-Type':'application/json'}});}if(path.split('?')[0].endsWith('/date-schedules')&&window.failContractLibrary){window.failContractLibrary=false;return new Response(JSON.stringify({code:'read_unavailable'}),{status:503,headers:{'Content-Type':'application/json'}});}return response;};}`);
 await set('#curve-dates-name','Synthetic shared daily dates revised');await click('#curve-dates-keep');
 await check('a malformed accepted response retains the original command',"!document.querySelector('#curve-dates-pending').hidden&&!document.querySelector('#error').hidden");
 await click('#curve-dates-retry');
 await check('retry reconciles the committed save using the same request identity',"document.querySelector('#curve-dates-pending').hidden&&document.querySelector('#error').hidden&&window.contractDateCommands.length===2&&window.contractDateCommands[0].requestId===window.contractDateCommands[1].requestId&&document.querySelector('#curve-dates-name').value==='Synthetic shared daily dates revised'");
 await evaluate('window.failContractLibrary=true');await set('#curve-dates-name','Synthetic shared daily dates');await click('#curve-dates-keep');
 await check('a failed library refresh after confirmed save does not offer a mutation retry',"document.querySelector('#curve-dates-pending').hidden&&!document.querySelector('#error').hidden&&document.querySelector('#curve-dates-status').textContent.includes('Saved draft')");
 await click('#curve-dates-reload');
 await check('reload opens the actual saved generation after a read failure',"document.querySelector('#error').hidden&&document.querySelector('#curve-dates-name').value==='Synthetic shared daily dates'");
 // The API refuses an expired request token before any write, so the page keeps no request.
 await evaluate("window.contractTokenSaves=[];window.staleContractToken=true;{const original=window.fetch;window.fetch=async(input,init)=>{if(!window.staleContractToken||!String(input).endsWith('/contracts/save'))return original(input,init);window.staleContractToken=false;const response=await original(input,{...init,headers:{...init.headers,'X-Preview-CSRF':'synthetic-stale-token'}});window.contractTokenSaves.push(response.status+' '+(await response.clone().json()).code);return response;};}");
 await set('#curve-dates-name','Synthetic shared daily dates with a stale token');await click('#curve-dates-keep');
 await check('a save refused for an expired request token keeps no request',"window.contractTokenSaves.join()==='400 request_token'&&!document.querySelector('#error').hidden&&document.querySelector('#curve-dates-pending').hidden&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.contract-dates.request.'))");
 await click('#nav-reference');await wait("!document.querySelector('#schedules-page-prompt').hidden&&document.querySelector('#schedules-page-prompt').getClientRects().length>0");await click('#schedules-page-prompt button:last-child');
 await persona('approver');await click('#nav-schedules');await click('#schedule-tab-contracts');
 await evaluate("[...document.querySelectorAll('#curve-dates-saved .shortcut-saved-row')].find(row=>row.textContent.includes('Synthetic shared daily dates')).querySelector('button').click()");await wait(idle);
 await click('#curve-dates-publish');
 await check('a separate publisher publishes the retained draft',"document.querySelector('#error').hidden&&document.querySelector('#curve-dates-version').textContent.includes('published')");
 await persona('viewer');await click('#nav-schedules');await click('#schedule-tab-contracts');
 await check('readers see the published library with authoring disabled',"document.querySelector('#curve-dates-saved').textContent.includes('Synthetic shared daily dates')&&document.querySelector('#curve-dates-fields').disabled&&document.querySelector('#curve-dates-keep').hidden");
 await evaluate("[...document.querySelectorAll('#curve-dates-saved .shortcut-saved-row')].find(row=>row.textContent.includes('Synthetic shared daily dates')).querySelector('[data-resolve]').click()");await wait(idle);
 await check('an exact published version resolves under the selected context',"document.querySelector('#error').hidden&&!document.querySelector('#curve-dates-result').hidden&&document.querySelectorAll('#curve-dates-table tbody tr').length===2&&document.querySelector('#curve-dates-spec').textContent.includes('version 1')","({error:document.querySelector('#error').textContent,hidden:document.querySelector('#curve-dates-result').hidden,rows:document.querySelectorAll('#curve-dates-table tbody tr').length,receipt:document.querySelector('#curve-dates-spec').textContent})");
 // D356: a generated daily schedule and a date-list mapping are authored in Settings and resolved in the selected context.
 await persona('editor');await click('#nav-schedules');await click('#schedule-tab-exchange');await click('#dates-new');
 await set('#dates-daily','calendar');
 await check('daily schedules offer only next-date selection',
  "[...document.querySelectorAll('#dates-selection option')].filter(o=>o.value&&!o.disabled).map(o=>o.value).join()==='on_or_after'&&['dates-import','dates-first','dates-last','dates-anchor','dates-day','dates-short','dates-adjustment'].every(id=>document.getElementById(id).disabled)");
 await set('#dates-daily-first','2021-01-01');await set('#dates-daily-last','2021-12-31');
 await set('#dates-name','Synthetic browser days');await set('#dates-symbol','oe.browser_days');
 await evaluate("(()=>{const n=document.querySelector('#dates-calendar');n.value=[...n.options].find(o=>o.value.endsWith('|test_business')).value;n.dispatchEvent(new Event('change',{bubbles:true}));})()");
 await set('#dates-selection','on_or_after');await set('#dates-effective-from','2021-01-01');await set('#dates-effective-to','2022-01-01');
 await click('#dates-keep');await wait(idle);
 await persona('approver');await click('#nav-schedules');await click('#schedule-tab-exchange');
 await evaluate("[...document.querySelectorAll('#dates-saved .shortcut-saved-row')].find(row=>row.textContent.includes('oe.browser_days')).querySelectorAll('button')[1].click()");await wait(idle);
 await click('#dates-publish');await wait(idle);
 await check('a daily schedule is saved and published from Settings',
  "document.querySelector('#error').hidden&&[...document.querySelectorAll('#dates-saved .shortcut-saved-row')].some(row=>row.textContent.includes('oe.browser_days')&&row.textContent.includes('published')&&row.textContent.includes('daily'))",
  "({error:document.querySelector('#error').textContent,saved:document.querySelector('#dates-saved').textContent})");
 await persona('editor');await click('#nav-schedules');await click('#schedule-tab-contracts');await click('#date-context-latest');await click('#current-view-context');await wait(idle);
 await click('#curve-dates-new');await set('#curve-dates-kind','dates');
 await check('a date-list mapping is authored with a label and one date per row',
  "[...document.querySelectorAll('#curve-dates-head th')].map(th=>th.textContent).filter(Boolean).join('|')==='Label|Date · date or reference'");
 await set('#curve-dates-code','browser_tenors');await set('#curve-dates-name','Synthetic browser tenors');
 const row=(index,label,date)=>evaluate(`(()=>{const r=document.querySelectorAll('#curve-dates-rows tr')[${index}];for(const [f,v] of [['label',${JSON.stringify(label)}],['date',${JSON.stringify(date)}]]){const n=r.querySelector('[data-field="'+f+'"]');n.value=v;n.dispatchEvent(new Event('input',{bubbles:true}));}})()`);
 await click('#curve-dates-add');await click('#curve-dates-add');await wait("document.querySelectorAll('#curve-dates-rows tr').length===2");await row(0,'6M','oe.browser_days[181]');await row(1,'1D','oe.browser_days + 1bd');
 await submit('#curve-dates-form');await wait(idle);
 await check('a date-list order error names both rows',"!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('1D')&&document.querySelector('#error').textContent.includes('6M')","document.querySelector('#error').textContent");
 await row(0,'1D','oe.browser_days + 1bd');await row(1,'6M','oe.browser_days[181]');
 await submit('#curve-dates-form');await wait(idle);
 await check('a date-list preview resolves each label in the selected context',"document.querySelector('#error').hidden&&document.querySelectorAll('#curve-dates-table tbody tr').length===2&&document.querySelector('#curve-dates-table').textContent.includes('6M')",
  "({error:document.querySelector('#error').textContent,table:document.querySelector('#curve-dates-table').textContent})");
 await click('#curve-dates-keep');await wait(idle);
 await check('a date list saves as a shared draft',"document.querySelector('#error').hidden&&document.querySelector('#curve-dates-saved').textContent.includes('Synthetic browser tenors')&&document.querySelector('#curve-dates-saved').textContent.includes('2 dates')");
}
