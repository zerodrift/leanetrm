// D318: Curves & prices defines a curve from the mapping tools/check_curve_definitions.py publishes and previews its rolling contract
// window at the close the header selects.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await persona('viewer');
 const close=at=>evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith(${JSON.stringify(at)})).id;})()`);
 await set('#snapshot',await close('2021-01-15T16:00:00'));await wait(idle,30000);
 await click('#curve-define');await wait("document.querySelectorAll('#curve-definition-mapping option').length>1&&"+idle);
 await check('the editor replaces the board on Curves & prices, and Schedules keeps only schedules and date mappings',
  "!document.querySelector('#curve-definition').hidden&&document.querySelector('#market-board').hidden&&document.querySelector('#curve-detail').hidden&&!document.querySelector('#schedule-tab-curves')");
 await check('Coming soon business cases are listed and cannot be chosen, and readers cannot save',
  "[...document.querySelectorAll('#curve-definition-target option')].some(o=>o.textContent.includes('Hourly physical price')&&o.disabled)&&[...document.querySelectorAll('#curve-definition-rule option')].some(o=>o.textContent.includes('Formula contracts')&&o.disabled)&&document.querySelector('#curve-definition-save').hidden");
 const choose=(id,text)=>`(()=>{const field=document.querySelector('#curve-definition-${id}');field.value=[...field.options].find(o=>o.textContent.includes(${JSON.stringify(text)})&&!o.disabled).value;field.dispatchEvent(new Event('change',{bubbles:true}));})()`;
 await set('#curve-definition-code','browser_gas_curve');await set('#curve-definition-name','Synthetic browser gas curve');
 for(const [id,text] of [['location','test_gas at test_hub_a'],['kind','Exchange futures'],['product','test_gas_future'],['quote','test_gas_outright'],['profile','test_uniform'],
  ['mapping','curve_months'],['target','test_future_target'],['rule','Direct mark'],['role','Daily settlement'],['source','fixture'],['missing','Refuse'],['interpolation','None']])await evaluate(choose(id,text));
 await set('#curve-definition-count','3');
 const series="[...document.querySelectorAll('#curve-definition-result tbody tr')].map(r=>r.cells[5].textContent).join()";
 const observed=`({error:document.querySelector('#error').textContent,rows:${series}})`;
 await submit('.curve-definition-editor form');await wait(idle);
 await check('a futures curve previews its three positions at the selected close',
  `document.querySelector('#error').hidden&&${series}==='test_gas_202102,test_gas_202103,test_gas_202104'`,observed);
 // Another close makes the shown window stale; previewing again rolls it past the expired February contract.
 await set('#snapshot',await close('2021-02-15T16:00:00'));await wait(idle,30000);
 await check('a changed close clears the previous window',"document.querySelector('#curve-definition-result').hidden&&!document.querySelector('#curve-definition').hidden");
 await submit('.curve-definition-editor form');await wait(idle);
 await check('the window rolls with the header close',`document.querySelector('#error').hidden&&${series}==='test_gas_202103,test_gas_202104,test_gas_202105'`,observed);
 await set('#curve-definition-count','5');await submit('.curve-definition-editor form');await wait(idle);
 await check('a refused definition shows the server refusal and no stale window',
  "!document.querySelector('#error').hidden&&document.querySelector('#curve-definition-result').hidden");
 await check('the curve form stays inside the market content',"(()=>{const content=document.querySelector('.market-content');return content.scrollWidth<=content.clientWidth+1;})()");
 await click('#curve-definition-back');await wait(idle);
 await check('All markets returns to the board',"document.querySelector('#curve-definition').hidden&&!document.querySelector('#market-board').hidden");
 // D358: saved definitions are listed and edited here; tools/check_curve_definitions.py published usd_zero over rate_tenors.
 await set('#snapshot',await close('2021-01-15T16:00:00'));await wait(idle,30000);
 await persona('editor');await click('#curve-define');await wait("document.querySelectorAll('#curve-definition-list .shortcut-saved-row').length>1&&"+idle);
 // Changing persona selects the newest available close; the rate curve marked there makes that 15 February, so return to 15 January.
 await set('#snapshot',await close('2021-01-15T16:00:00'));await wait(idle,30000);
 const open=code=>evaluate(`[...document.querySelectorAll('#curve-definition-list .shortcut-saved-row')].find(r=>r.textContent.includes(${JSON.stringify(code)})).querySelector('button').click()`);
 await check('Curve definitions lists saved definitions and opens one',
  "(()=>{const list=document.querySelector('#curve-definition-list').textContent;return list.includes('usd_zero')&&list.includes('test_gas_curve');})()");
 await open('usd_zero');await wait(idle);
 await check('an opened definition fills the editor with its saved terms',"document.querySelector('#curve-definition-code').value==='usd_zero'&&document.querySelector('#curve-definition-code').readOnly");
 await click('#curve-definition-new');await wait(idle);
 await evaluate(choose('kind','Interest rate curve'));await wait(idle);
 const hidden=id=>`document.querySelector('#curve-definition-${id}').closest('div').hidden`;
 await check('choosing Interest rate curve shows only rate terms',
  `${['location','product','quote','profile','target','rule','role','missing','interpolation','count'].map(hidden).join('&&')}&&${['currency','unit','daycount','compounding','rate-interpolation','extrapolation','source','mapping'].map(id=>'!'+hidden(id)).join('&&')}`);
 // D359: a rate curve names its own currency, mark unit and conventions; none is preselected and later conventions are Coming soon.
 await check('a rate curve shows required currency, mark unit, day count, compounding, interpolation and extrapolation selects',
  ['currency','unit','daycount','compounding','rate-interpolation','extrapolation'].map(id=>`document.querySelector('#curve-definition-${id}').required&&document.querySelector('#curve-definition-${id}').value===''`).join('&&'));
 await check('coming-soon conventions are disabled',
  "[...document.querySelector('#curve-definition-compounding').options].some(o=>o.disabled&&o.textContent.includes('Semi-annual'))&&[...document.querySelector('#curve-definition-daycount').options].some(o=>o.disabled&&o.textContent.includes('30/360'))");
 await check('the interest-rate specification select is gone',"!document.querySelector('#curve-definition-rate')");
 await set('#curve-definition-code','browser_zero');await set('#curve-definition-name','Synthetic browser zero rates');
 for(const [id,text] of [['currency','usd'],['unit','Percent per year'],['daycount','ACT/365 Fixed'],['compounding','Continuous'],
  ['rate-interpolation','Log-linear discount factor'],['extrapolation','Flat forward'],['source','fixture'],['mapping','rate_tenors']])await evaluate(choose(id,text));
 await wait("document.querySelectorAll('#curve-definition-rows tr').length===3&&"+idle);
 await click('#curve-definition-add');await wait("document.querySelectorAll('#curve-definition-rows tr').length===4");
 await evaluate(`(()=>{const r=document.querySelectorAll('#curve-definition-rows tr')[3];for(const [f,v] of [['label','9M'],['date','oe.rate_days[273]']]){const n=r.querySelector('[data-field="'+f+'"]');n.value=v;n.dispatchEvent(new Event('input',{bubbles:true}));}})()`);
 const maturities="[...document.querySelectorAll('.curve-definition-editor #curve-definition-result tbody tr')].map(r=>r.cells[2].textContent).join()";
 const seen=`({error:document.querySelector('#error').textContent,rows:${maturities}})`;
 await submit('.curve-definition-editor form');await wait(idle);
 await check('tenors are edited in place and previewed at the header close',`document.querySelector('#error').hidden&&document.querySelectorAll('#curve-definition-result tbody tr').length===4`,seen);
 const january=await evaluate(maturities);
 await set('#snapshot',await close('2021-02-15T16:00:00'));await wait(idle,30000);
 await submit('.curve-definition-editor form');await wait(idle);
 await check('the edited tenors roll with the header close',`document.querySelector('#error').hidden&&${maturities}!==${JSON.stringify(january)}&&document.querySelectorAll('#curve-definition-result tbody tr').length===4`,seen);
 await set('#snapshot',await close('2021-01-15T16:00:00'));await wait(idle,30000);
 await click('#curve-definition-save');await wait(idle);
 await check('an author saves the definition and its tenors as drafts',"document.querySelector('#error').hidden&&document.querySelector('#curve-definition-status').textContent.includes('Saved draft')",
  "({error:document.querySelector('#error').textContent,status:document.querySelector('#curve-definition-status').textContent})");
 await persona('approver');await click('#curve-define');await wait("document.querySelectorAll('#curve-definition-list .shortcut-saved-row').length>1&&"+idle);
 await open('browser_zero');await wait(idle);await click('#curve-definition-publish');await wait(idle);
 await check('save then publish publishes the curve and its tenors',
  "document.querySelector('#error').hidden&&[...document.querySelectorAll('#curve-definition-list .shortcut-saved-row')].some(r=>r.textContent.includes('browser_zero')&&r.textContent.includes('published'))",
  "({error:document.querySelector('#error').textContent,list:document.querySelector('#curve-definition-list').textContent})");
 await persona('editor');await click('#curve-define');await wait("document.querySelectorAll('#curve-definition-list .shortcut-saved-row').length>1&&"+idle);
 await open('usd_zero');await wait(idle);
 await check('a newer published mapping version is offered to the curve',"!document.querySelector('#curve-definition-newer').hidden",
  "document.querySelector('#curve-definition-newer').textContent");
 await click('#curve-definition-newer button');await wait(idle);
 await check('accepting the offer shows the newer version\'s tenors',"document.querySelectorAll('#curve-definition-rows tr').length===4&&document.querySelector('#curve-definition-newer').hidden");
 // A mapping draft changed in Settings after the curve pinned it is shown, and adopting it is an explicit choice.
 await open('browser_zero');await wait(idle);
 await click('#curve-definition-add');await wait("document.querySelectorAll('#curve-definition-rows tr').length===5");
 await evaluate(`(()=>{const r=document.querySelectorAll('#curve-definition-rows tr')[4];for(const [f,v] of [['label','10M'],['date','oe.rate_days[300]']]){const n=r.querySelector('[data-field="'+f+'"]');n.value=v;n.dispatchEvent(new Event('input',{bubbles:true}));}})()`);
 await click('#curve-definition-save');await wait(idle);
 await check('a curve draft pins its edited tenors',"document.querySelector('#error').hidden&&document.querySelector('#curve-definition-status').textContent.includes('Saved draft')");
 await evaluate(`(async()=>{const token=(await(await fetch('/api/bootstrap')).json()).csrfToken;
  const m=(await(await fetch('/api/date-schedules')).json()).contractDefinitions.filter(v=>v.definition.code==='rate_tenors').sort((a,b)=>b.version-a.version)[0];
  const r=await fetch('/api/date-schedules/contracts/save',{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify({requestId:crypto.randomUUID(),
   code:'rate_tenors',name:'Synthetic rate tenors changed in Settings',context:m.definition.context,contracts:null,dates:m.definition.dates,id:m.id,version:m.version,generation:m.generation,expectedState:m.state})});
  if(!r.ok)throw Error(await r.text());})()`);
 await click('#curve-define');await wait("document.querySelectorAll('#curve-definition-list .shortcut-saved-row').length>1&&"+idle);
 await open('browser_zero');await wait(idle);
 await check('a mapping draft changed after the curve was saved is shown',"!document.querySelector('#curve-definition-stale').hidden");
 await click('#curve-definition-save');await wait(idle);
 await check('saving does not adopt the changed rows unreviewed',"!document.querySelector('#error').hidden&&!document.querySelector('#curve-definition-stale').hidden");
 await click('#curve-definition-stale button');await click('#curve-definition-save');await wait(idle);
 await check('reviewed changed rows are adopted on save',"document.querySelector('#error').hidden&&document.querySelector('#curve-definition-stale').hidden&&document.querySelector('#curve-definition-status').textContent.includes('Saved draft')",
  "({error:document.querySelector('#error').textContent,status:document.querySelector('#curve-definition-status').textContent})");
 await click('#curve-definition-back');await wait(idle);
 // Recorded prices keep their own correction workflow; a rendered curve offers no way to change what defines it.
 await check('rendered curves have no definition edit control',
  "!document.querySelector('#market-board').hidden&&!document.querySelector('#market-board [id^=curve-definition-],#curve-detail [id^=curve-definition-]:not(#curve-definition-link)')&&![...document.querySelectorAll('#market-board button,#curve-detail button:not(#curve-definition-link)')].some(b=>/definition|tenor|publish/i.test(b.textContent))");
 // D359: an admitted rate curve is shown by tenor, in its own mark unit, with a link to (not an editor for) its definition.
 await evaluate("document.querySelector('.curve-button[data-curve=\"usd_rolling\"]').click()");await wait("document.querySelectorAll('#contracts tbody tr').length===2&&"+idle,30000);
 const rateTable="({head:[...document.querySelectorAll('#contracts thead th')].map(c=>c.textContent).join('|'),rows:[...document.querySelectorAll('#contracts tbody tr')].map(r=>r.textContent).join('|'),unit:document.querySelector('#unit').textContent})";
 await check('a rate curve page lists tenor, maturity, mark and discount factor',
  "document.querySelector('#contracts thead').textContent.includes('Zero rate (% per year)')&&/^1M · .*4\\.\\d+0\\.99\\d{4}$/.test(document.querySelector('#contracts tbody tr').textContent)",rateTable);
 await check('the unit label comes from the server',"document.querySelector('#unit').textContent.endsWith('% per year')",rateTable);
 await check('the page links to the curve\u2019s definition',"!document.querySelector('#curve-definition-link').hidden");
 await click('#curve-definition-link');await wait("!document.querySelector('#curve-definition').hidden&&"+idle,30000);
 await check('the link opens the curve definition the curve pins',"document.querySelector('#curve-definition-code').value==='usd_rolling'&&document.querySelector('#curve-definition-code').readOnly");
 // The gear on a listed curve opens that curve's own configuration, and only for someone the backend says may change it.
 const gear='#curves [data-curve-configure="usd_rolling"]';
 await click('#curve-definition-back');await wait(idle);
 await check('only a curve with a configuration this person may edit has a gear',
  `document.querySelector(${JSON.stringify(gear)})&&document.querySelectorAll('#curves [data-curve-configure]').length<document.querySelectorAll('#curves .curve-button[data-curve]').length`);
 await check('the gear names the curve it configures',`document.querySelector(${JSON.stringify(gear)}).getAttribute('aria-label').startsWith('Configure ')`);
 await check('the gear is not nested in the button that opens the curve',`!document.querySelector(${JSON.stringify(gear)}).closest('.curve-button')`);
 await click(gear);await wait("!document.querySelector('#curve-definition').hidden&&document.querySelector('#curve-definition-code').value==='usd_rolling'&&"+idle,30000);
 await check('the gear opens that curve’s configuration',"document.querySelector('#curve-definition-code').readOnly&&document.querySelector('#curve-definition-code').value==='usd_rolling'");
 // D362: a futures curve admitted from its definition has a gear too, opening its contract terms.
 const contractGear='#curves [data-curve-configure="rolling_gas"]';
 await click('#curve-definition-back');await wait(idle);
 await check('an admitted futures curve has a gear for someone who may edit it',`!!document.querySelector(${JSON.stringify(contractGear)})`);
 await click(contractGear);await wait("!document.querySelector('#curve-definition').hidden&&document.querySelector('#curve-definition-code').value==='rolling_gas'&&"+idle,30000);
 await check('the gear opens the futures curve\u2019s definition',"document.querySelector('#curve-definition-code').readOnly&&document.querySelector('#curve-definition-kind').value==='future'&&document.querySelectorAll('#curve-definition-rows tr').length===3",
  "({kind:document.querySelector('#curve-definition-kind').value,rows:document.querySelectorAll('#curve-definition-rows tr').length,error:document.querySelector('#error').textContent})");
 await click('#curve-definition-back');await wait(idle);
 await persona('viewer');await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await check('a reader sees no gear',"document.querySelectorAll('#curves [data-curve-configure]').length===0");
 await persona('editor');await wait(`document.querySelector(${JSON.stringify(gear)})&&`+idle);
 await click('#as-of-context>summary');await click('#date-context-latest');await click('#apply-view-context');await wait("document.querySelector('#date-context').dataset.contextMode==='retained'&&"+idle,30000);
 await check('a historical view offers no gear, as it cannot author',"document.querySelectorAll('#curves .curve-button[data-curve]').length>0&&document.querySelectorAll('#curves [data-curve-configure]').length===0",
  "({error:document.querySelector('#error').textContent,buttons:document.querySelectorAll('#curves .curve-button[data-curve]').length,gears:document.querySelectorAll('#curves [data-curve-configure]').length,mode:document.querySelector('#date-context').dataset.contextMode,known:document.querySelector('#date-context-view').value})");
}
