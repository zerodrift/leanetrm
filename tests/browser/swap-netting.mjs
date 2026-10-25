import {mkdtemp,copyFile} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {tmpdir} from 'node:os';
// Intra-trade net settlement and the closing-context P&L bridge (D216) in a financial swap's settlement view, which the Blotter's side panel
// hosts (D219): explicit selection and allocations, gross/net review, record, history and exact reversal. What is typed into the net form is
// an edit the panel asks about; a correction's due instant typed in its calculation is kept through what the net form and the bridge do, and
// a net settlement, which reads the view again, asks before giving it up; one that goes through clears risk results run before it; and one
// whose outcome is not known is kept, named for it, across a reload.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url}){
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 // An account that may both settle and run risk, so that a net settlement made while risk results show is seen to clear them.
 await persona('admin');
 await evaluate(`(async()=>{const b=await(await fetch('/api/bootstrap')).json();const r=await fetch('/api/admin/create',{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':b.csrfToken},body:JSON.stringify({operationId:crypto.randomUUID(),login:'netting_risk_browser',name:'Synthetic netting and risk account',kind:'human',roles:['trader','risk_analyst']})});if(!r.ok)throw Error(await r.text());})()`);
 await persona('netting_risk_browser');
 const typeInto=async(selector,text)=>{await evaluate(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});n.focus();n.select();})()`);await command('Input.insertText',{text});};
 const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2021-01-15T08:00:00')).id;})()");
 await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait(idle);
 const selected=await evaluate(`(async()=>{const token=(await(await fetch('/api/bootstrap')).json()).csrfToken;const post=async(path,body)=>{const r=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});if(!r.ok)throw Error(await r.text());return r.json();};const context={snapshotId:${JSON.stringify(frame)},convention:'independent_side_financial_swap_v2',knowledgeMode:'current'};const report=await post('trading/blotter',{context:{...context,convention:'trading_workspace_v1'},view:{mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[],pivotConvention:null}});for(const h of report.holdings.filter(h=>h.instrumentType==='financial_swap'&&h.settlement==='Active')){const d=await post('swaps/settlement/inspect',{context,tradeId:h.tradeId,revision:null,duePolicy:'swap_due_nominal_v1',recognitionPolicy:'swap_completed_period_trading_pnl_v1'});if(d.cashMovements.some(p=>p.reference==='Synthetic zero discharge reversal'))return {reference:h.tradeReference,otherReference:report.holdings.find(other=>other.instrumentType==='financial_swap'&&other.settlement==='Active'&&other.tradeId!==h.tradeId).tradeReference};}throw Error('Missing synthetic netting fixture');})()`);
 // The settlement view is in the panel's form part, open; it is opened from the Terms of the position's row.
 const open="(()=>{const view=document.querySelector('#swap-settlement'),form=document.querySelector('#side-panel-form');return !!view&&!view.hidden&&form.contains(view)&&!form.hidden;})()";
 let deskWidth=0;
 async function openView(reference,measure=false){
  if(await evaluate("!document.querySelector('#side-panel').hidden")){await click('#side-panel-close');await wait("document.querySelector('#side-panel').hidden&&"+idle);}
  await set('#blotter-filters .table-search',reference);await wait(idle);
  await wait(`(()=>{const rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length);return rows.length===1&&rows[0].textContent.includes(${JSON.stringify(reference)});})()`);
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#side-panel').dataset.mode==='position'&&"+idle);
  await click('#side-panel-tabs-terms');await wait("!!document.querySelector('#side-panel-settlement')&&"+idle);
  if(measure)deskWidth=await evaluate("document.querySelector('#desk-positions').getBoundingClientRect().width");
  await click('#side-panel-settlement');await wait(open);await wait(idle);
 }
 const prompt="document.querySelector('#side-panel-prompt')",due="document.querySelector('#swap-settlement-correction-due')?.value",typed='2021-03-20T00:00:00Z';
 const card=label=>`(()=>{const card=document.querySelector('#futures-pending');return !card.hidden&&card.getClientRects().length>0&&card.textContent.includes(${JSON.stringify(label)});})()`;
 // Each figure and action of a settlement table is read without scrolling sideways: it ends inside its table's scroll area.
 const figuresOutside=containers=>`(()=>{const out=[];for(const id of ${JSON.stringify(containers)})for(const scroll of document.querySelectorAll(id+' .regular-table-scroll')){const box=scroll.getBoundingClientRect(),edge=box.left+scroll.clientLeft+scroll.clientWidth;for(const n of scroll.querySelectorAll('td.numeric,button'))if(n.getClientRects().length&&n.getBoundingClientRect().right>edge+0.5)out.push(id+': '+n.textContent.trim().slice(0,24));}return out;})()`;
 // Settlement content opens the panel wide over the desk on a desktop; on a phone it is the sheet.
 const panelIs=state=>state==='wide'?"(()=>{const p=document.querySelector('#side-panel');return p.hasAttribute('data-wide')&&Math.abs(p.getBoundingClientRect().width-720)<=2;})()":"(()=>{const p=document.querySelector('#side-panel');return p.dataset.placement==='overlay'&&!p.hasAttribute('data-wide');})()";
 const tableWidths="[...document.querySelectorAll('#swap-settlement .regular-table-scroll')].map(s=>[s.parentElement.closest('[id]').id,s.scrollWidth,s.clientWidth])";
 const figuresShown=(containers,label)=>check(label,`${panelIs('wide')}&&${JSON.stringify(containers)}.every(id=>document.querySelector(id+' td.numeric'))&&!!document.querySelector('#swap-settlement-payments button')&&${figuresOutside(containers)}.length===0`,figuresOutside(containers));
 const tablesFit=label=>check(label,`${panelIs('wide')}&&[...document.querySelectorAll('#swap-settlement .regular-table-scroll')].every(s=>s.scrollWidth<=s.clientWidth+1)`,tableWidths);
 // On a phone a table may scroll sideways, and each figure and action can be scrolled to in full.
 const unreachable="(()=>{const out=[];for(const scroll of document.querySelectorAll('#swap-settlement .regular-table-scroll')){for(const n of scroll.querySelectorAll('td.numeric,button')){if(!n.getClientRects().length)continue;n.scrollIntoView({block:'nearest',inline:'nearest'});const box=scroll.getBoundingClientRect(),edge=box.left+scroll.clientLeft+scroll.clientWidth,r=n.getBoundingClientRect();if(r.left<box.left+scroll.clientLeft-0.5||r.right>edge+0.5)out.push(scroll.parentElement.closest('[id]').id+': '+n.textContent.trim().slice(0,24));}scroll.scrollLeft=0;}return out;})()";
 const figuresReachable=label=>check(label,`${panelIs('sheet')}&&!!document.querySelector('#swap-settlement td.numeric')&&${unreachable}.length===0`,unreachable);
 const unloadAsks="(()=>{const event=new Event('beforeunload',{cancelable:true});window.dispatchEvent(event);return event.defaultPrevented;})()";
 const candidates="[...document.querySelectorAll('#swap-net-selection tbody tr')].map(r=>[r.querySelector('input[type=checkbox]')?.checked,r.querySelector('input[type=text]')?.value])";
 async function correction(){await click('#swap-settlement button[data-membership="0"][data-payment="0"]');await wait("!!document.querySelector('#swap-settlement-correction-due')&&"+idle);await typeInto('#swap-settlement-correction-due',typed);}
 await openView(selected.reference,true);
 await check('the settlement view opens the panel wide over the desk, which is as it was beneath it',`${panelIs('wide')}&&Math.abs(document.querySelector('#desk-positions').getBoundingClientRect().width-${deskWidth})<=1`,"[document.querySelector('#side-panel').getBoundingClientRect().width,document.querySelector('#side-panel').hasAttribute('data-wide'),document.querySelector('#desk-positions').getBoundingClientRect().width]");
 // A column that stacks several facts has no column filter; a column of one fact keeps its own.
 await check('the stacked columns have no column filter, and the figure columns keep theirs',"[['#swap-settlement-payments',0],['#swap-settlement-balances',0],['#swap-settlement-cash',0],['#swap-net-selection',1]].every(([id,i])=>{const cells=document.querySelectorAll(id+' .column-filter-row th');return !cells[i].querySelector('select')&&!!cells[i+1].querySelector('select');})");
 await check('net settlement offers explicit gross selection and signed allocation review',"!!document.querySelector('#swap-net-form')&&document.querySelectorAll('#swap-net-selection input[type=checkbox]').length===2");
 // A correction's due instant typed in its calculation is kept through what the net form and the P&L bridge do.
 await correction();
 await typeInto('#swap-net-amount','5');
 await check('typing a net entry keeps a correction due instant typed in its calculation',`${due}===${JSON.stringify(typed)}`,due);
 await set('#swap-pnl-contract-policy','reject_contract_changes_v1');
 await check('choosing a contractual-change policy keeps it',`${due}===${JSON.stringify(typed)}`,due);
 await click('#swap-pnl-capture-opening');
 await check('an opening capture refused for want of a snapshot keeps it, and says why',`${due}===${JSON.stringify(typed)}&&document.querySelector('#swap-settlement [role=alert]').textContent.includes('opening market snapshot')`,"[document.querySelector('#swap-settlement [role=alert]').textContent,"+due+"]");
 await click('#swap-settlement-opening');
 await check('using the view as the opening keeps it',`${due}===${JSON.stringify(typed)}&&!document.querySelector('#swap-settlement-reconcile').disabled`,due);
 await set('#swap-pnl-contract-policy','');await click('#swap-settlement-reconcile');
 await check('a reconciliation refused for want of a contractual-change policy keeps it, and says why',`${due}===${JSON.stringify(typed)}&&document.querySelector('#swap-settlement [role=alert]').textContent.includes('contractual-change valuation policy')`,"[document.querySelector('#swap-settlement [role=alert]').textContent,"+due+"]");
 // What is typed into the net form is an edit: once the calculation is given up, the panel still asks before the view is closed.
 await click('#swap-settlement-refresh');await click('#side-panel-prompt button:not(.secondary)');await wait(`${prompt}.hidden&&`+idle);
 await click('#side-panel-amend');
 await check('a net entry typed in the settlement view makes the panel ask before the view is closed',`!document.querySelector('#swap-settlement-correction-due')&&!${prompt}.hidden&&${prompt}.textContent.includes('unsaved changes')`,`[${prompt}.hidden,${prompt}.textContent]`);
 await click('#side-panel-prompt button.secondary');
 await check('keeping it leaves the view open with the net entry as it was typed',`${open}&&${prompt}.hidden&&document.querySelector('#swap-net-amount').value==='5'`);
 await click('#side-panel-amend');await click('#side-panel-prompt button:not(.secondary)');await wait(`${prompt}.hidden&&!${open}&&!!document.querySelector('#side-panel-settlement')&&`+idle);
 await check('closed, the view gives the panel back its docked width',"(()=>{const p=document.querySelector('#side-panel');return !p.hasAttribute('data-wide')&&p.dataset.placement==='docked'&&Math.abs(p.getBoundingClientRect().width-340)<=2;})()","[document.querySelector('#side-panel').getBoundingClientRect().width,document.querySelector('#side-panel').dataset.placement]");
 await click('#side-panel-settlement');await wait(open);await wait(idle);
 await check('discarding it closes the view, which opens again with no net entry',`${open}&&document.querySelector('#swap-net-amount').value===''`);
 await evaluate("document.querySelectorAll('#swap-net-selection tr').forEach(row=>{const selected=row.querySelector('input[type=checkbox]'),amount=row.querySelector('input[type=text]');if(!selected)return;selected.checked=true;selected.dispatchEvent(new Event('change',{bubbles:true}));amount.value=row.textContent.includes('-200')?'-100':'120';amount.dispatchEvent(new Event('input',{bubbles:true}));})");
 // Selections and typed allocations are kept when the view is read again.
 await click('#swap-settlement-refresh');await wait(idle);
 await check('reading the view again keeps the selected contributions and their typed allocations',"(()=>{const rows=[...document.querySelectorAll('#swap-net-selection tbody tr')];return rows.length===2&&rows.every(r=>r.querySelector('input[type=checkbox]').checked&&r.querySelector('input[type=text]').value===(r.textContent.includes('-200')?'-100':'120'));})()",candidates);
 await set('#swap-net-amount','20');await set('#swap-net-effective','2021-01-15T08:00:00Z');await set('#swap-net-reference','Synthetic browser net settlement');await submit('#swap-net-form');await wait(idle);
 await check('net preview shows backend gross and net amounts before recording',"document.querySelector('#swap-net-review').textContent.includes('-100')&&document.querySelector('#swap-net-review').textContent.includes('120')&&!!document.querySelector('#swap-net-record')");
 await figuresShown(['#swap-net-review','#swap-settlement-payments','#swap-settlement-balances'],'in the wide panel the net review\'s, the payments\' and the balances\' figures, and the payments\' actions, are read without scrolling sideways');
 await tablesFit('and no settlement table there scrolls sideways');
 // A filter of the candidates only reads what the table holds: it is no entry, and the prepared review stays.
 await typeInto('#swap-net-selection .table-search','Side');
 await check('filtering the net candidates leaves the prepared review and holds no edit',`!!document.querySelector('#swap-net-record')&&!${unloadAsks}`,`[!!document.querySelector('#swap-net-record'),${unloadAsks}]`);
 await set('#swap-net-selection .table-search','');
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-netting-visual-'));console.log('NETTING_VISUAL_ARTIFACTS='+artifacts);
 await evaluate("document.querySelector('#swap-settlement-payments').scrollIntoView({block:'start'})");await shot('netting-payments-desktop');await copyFile(join(dirname(process.argv[2]),'netting-payments-desktop.png'),join(artifacts,'payments-desktop.png'));
 await evaluate("document.querySelector('#swap-net-review').scrollIntoView({block:'center'})");await shot('netting-desktop');await copyFile(join(dirname(process.argv[2]),'netting-desktop.png'),join(artifacts,'desktop.png'));
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait("document.querySelector('#side-panel').dataset.placement==='overlay'");
 await evaluate("document.querySelector('#swap-net-review').scrollIntoView({block:'center'})");await shot('netting-narrow');await copyFile(join(dirname(process.argv[2]),'netting-narrow.png'),join(artifacts,'narrow.png'));
 await figuresReachable('in the panel\'s sheet each figure and action can be scrolled to in full');
 await check('net selection and review fit a narrow viewport in the panel\'s sheet',"document.querySelector('#swap-settlement').scrollWidth<=document.querySelector('#swap-settlement').clientWidth+1");
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await set('#swap-net-amount','21');await check('editing net amounts invalidates the prepared allocation review',"!document.querySelector('#swap-net-record')");await set('#swap-net-amount','20');await submit('#swap-net-form');await wait(idle);
 // Recording reads the view again, so over a typed correction due instant it asks first, and keeping it records nothing.
 await correction();await click('#swap-net-record');
 await check('recording a reviewed net settlement over a typed correction due instant asks first',`!${prompt}.hidden&&${prompt}.textContent.includes('correction due instant')`,`[${prompt}.hidden,${prompt}.textContent]`);
 await click('#side-panel-prompt button.secondary');
 await check('keeping it records nothing and leaves the review and the due instant',`${prompt}.hidden&&!!document.querySelector('#swap-net-record')&&${due}===${JSON.stringify(typed)}&&document.querySelector('#futures-pending').hidden&&!document.querySelector('#swap-settlement-cash').textContent.includes('Synthetic browser net settlement')`);
 // An entry changed while the question is open removes the review the record was asked from, so going on records nothing.
 await click('#swap-net-record');await set('#swap-net-amount','21');
 await check('a net entry changed while the question is open removes the review',`!${prompt}.hidden&&!document.querySelector('#swap-net-record')`);
 await click('#side-panel-prompt button:not(.secondary)');await wait(`${prompt}.hidden&&`+idle);
 await check('going on records nothing, and asks for the review again',`document.querySelector('#swap-settlement [role=alert]').textContent.includes('preview again')&&document.querySelector('#futures-pending').hidden&&!document.querySelector('#swap-settlement-cash').textContent.includes('Synthetic browser net settlement')`,"[document.querySelector('#swap-settlement [role=alert]').textContent,document.querySelector('#swap-settlement-cash').textContent.slice(0,400)]");
 await set('#swap-net-amount','20');await submit('#swap-net-form');await wait(idle);await correction();
 await evaluate(`(()=>{const send=window.fetch;let once=true;window.fetch=async(input,init)=>{const response=await send(input,init);if(once&&String(input).endsWith('/api/swaps/settlement/net')&&response.ok){once=false;window.syntheticLostNetCommand=JSON.parse(init.body);throw new TypeError('Synthetic lost reply after committed settlement');}return response;};})()`);
 await click('#swap-net-record');await click('#side-panel-prompt button:not(.secondary)',60000);await wait(idle);
 const retained=await evaluate("(()=>{const key=Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.futures.request.server_booking_utc_v1.'));return {key,value:sessionStorage.getItem(key),command:window.syntheticLostNetCommand};})()");
 await check('a lost net-settlement reply retains its exact operation and allocation payload, in the panel\'s card named for it',`${card('Net settlement of '+selected.reference)}&&!!window.syntheticLostNetCommand`,"document.querySelector('#futures-pending').textContent");
 const profile=await evaluate("document.querySelector('#profile-label')?.textContent");
 const replaced=await evaluate('performance.timeOrigin');await command('Page.reload');await wait(`performance.timeOrigin!==${replaced}&&document.querySelector('#profile-label')?.textContent===${JSON.stringify(profile)}&&${idle}`);await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait(idle);
 await check('reload restores the original uncertain settlement without changing its operation or body, still named for it',`${card('Net settlement of '+selected.reference)}&&sessionStorage.getItem(${JSON.stringify(retained.key)})===${JSON.stringify(retained.value)}&&document.querySelector('#futures-new').disabled`);
 // Check status settles the record from its card in the panel, which asks nothing, while the risk report there holds the person's sizes:
 // the report is read again at the inputs on screen, and keeps the person's choice and sizes.
 await evaluate(`(()=>{const send=window.fetch;window.__riskCatalogs=[];window.__workspaces=[];window.fetch=async(input,init)=>{const response=await send(input,init),path=String(input);if(response.ok&&(path==='/api/portfolio/risk/catalog'||path==='/api/trading/workspace'))(path==='/api/trading/workspace'?window.__workspaces:window.__riskCatalogs).push(await response.clone().json());return response;};})()`);
 await click('#desk-risk-run');await wait("document.querySelector('#side-panel').dataset.mode==='risk'&&!document.querySelector('#risk-fields').disabled&&"+idle);
 await click('input[name=risk-choice][value=held]');
 await evaluate("(()=>{const n=document.querySelector('#risk-size-0');n.focus();n.select();})()");await command('Input.insertText',{text:'0.35'});
 const catalogsBefore=await evaluate('window.__riskCatalogs.length');
 await click('#futures-reconcile',60000);await wait("document.querySelector('#futures-pending').hidden&&"+idle);
 await check('a retained settlement settled from its card keeps the risk report\'s choice and sizes, read again at the inputs on screen, and asks nothing',
  `document.querySelector('#side-panel').dataset.mode==='risk'&&document.querySelector('#side-panel-prompt').hidden&&document.querySelector('input[name=risk-choice][value=held]').checked&&document.querySelector('#risk-size-0').value==='0.35'&&document.querySelector('#risk-size-0-note').hidden&&!document.querySelector('#risk-carried').hidden&&window.__riskCatalogs.length===${catalogsBefore}+1&&window.__riskCatalogs.at(-1).context.contextId===window.__workspaces.at(-1).blotter.context.contextId`,
  "[document.querySelector('#side-panel').dataset.mode,document.querySelector('input[name=risk-choice][value=held]')?.checked,document.querySelector('#risk-size-0')?.value,window.__riskCatalogs.length]");
 await check('operation-status recovery clears the retained settlement only after its committed outcome is known',"document.querySelector('#futures-pending').hidden&&document.querySelector('#error').hidden");
 // A risk report run at this close, whose results a net settlement made afterwards no longer holds for.
 await click('#desk-risk-run');await wait("[...document.querySelector('#risk-factor').options].some(o=>o.value)&&"+idle);await click('input[name=risk-choice][value=custom]');
 await set('#risk-factor',await evaluate("[...document.querySelector('#risk-factor').options].find(o=>o.value).value"));await set('#risk-bump','0.1');await submit('#risk-form');
 await wait("!!document.querySelector('#risk-results .tabulator-row')");
 await openView(selected.reference);
 await check('recorded net settlement retains its gross allocations in history',"!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement-cash').textContent.includes('Synthetic browser net settlement')&&document.querySelector('#swap-settlement-cash').textContent.includes('-100')&&document.querySelector('#swap-settlement-cash').textContent.includes('120')");
 // A cash amount being entered for another record, which the reversal does not use.
 await set('#swap-settlement-amount','7');
 const id=await evaluate("[...document.querySelector('#swap-settlement-reversal').options].find(o=>o.textContent.includes('Synthetic browser net settlement')).value");await set('#swap-settlement-reversal',id);await set('#swap-settlement-effective','2021-01-15T08:00:00Z');await set('#swap-settlement-reference','Synthetic browser exact net reversal');await click('#swap-settlement-reverse',60000);await wait(idle);
 await check('net reversal requires review of the exact original gross allocations',"!!document.querySelector('#swap-net-record')&&document.querySelector('#swap-net-review').textContent.includes('-120')&&document.querySelector('#swap-net-review').textContent.includes('100')");await click('#swap-net-record',60000);await wait(idle);
 await check('net reversal restores gross balances without another earning',"!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement-balances').textContent.includes('-200')&&document.querySelector('#swap-settlement-balances').textContent.includes('230')");
 const riskCleared='Risk results cleared · a settlement record changed the values they were run on';
 await check('a net settlement that goes through clears the risk results run before it, and the page says so',`${open}&&!document.querySelector('#risk-results .tabulator-row')&&document.querySelector('#desk-tile-risk .desk-risk-state').textContent==='Not run at this close'&&!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes(${JSON.stringify(riskCleared)})`,"[document.querySelector('#notice').textContent,document.querySelector('#desk-tile-risk').textContent,!!document.querySelector('#risk-results .tabulator-row')]");
 await check('a net reversal clears the instant and reference it was recorded with, and keeps a cash amount it did not use',"document.querySelector('#swap-settlement-amount').value==='7'&&document.querySelector('#swap-settlement-effective').value===''&&document.querySelector('#swap-settlement-reference').value===''","[document.querySelector('#swap-settlement-amount').value,document.querySelector('#swap-settlement-effective').value,document.querySelector('#swap-settlement-reference').value]");
 // A net settlement whose reply is lost keeps its entries while its outcome is not known; once Check status finds it went through, what it
 // consumed is cleared from the net form, and nothing else.
 await evaluate("document.querySelectorAll('#swap-net-selection tbody tr').forEach(row=>{const selected=row.querySelector('input[type=checkbox]'),amount=row.querySelector('input[type=text]');selected.checked=true;selected.dispatchEvent(new Event('change',{bubbles:true}));amount.value=row.textContent.includes('-200')?'-10':'15';amount.dispatchEvent(new Event('input',{bubbles:true}));})");
 await set('#swap-net-amount','5');await set('#swap-net-effective','2021-01-15T08:00:00Z');await set('#swap-net-reference','Synthetic browser net settlement checked later');await submit('#swap-net-form');await wait(idle);
 await evaluate(`(()=>{const send=window.fetch;let once=true;window.fetch=async(input,init)=>{const response=await send(input,init);if(once&&String(input).endsWith('/api/swaps/settlement/net')&&response.ok){once=false;throw new TypeError('Synthetic lost reply after committed settlement');}return response;};})()`);
 await click('#swap-net-record',60000);await wait(idle);
 await check('a net settlement whose reply is lost keeps its entries while its outcome is not known',`${card('Net settlement of '+selected.reference)}&&document.querySelector('#swap-net-amount').value==='5'&&[...document.querySelectorAll('#swap-net-selection tbody input[type=checkbox]')].every(n=>n.checked)`,candidates);
 await click('#futures-reconcile',60000);await wait(idle);
 await check('once Check status finds it went through, the entries it consumed are cleared and the view shows it',`document.querySelector('#futures-pending').hidden&&${open}&&document.querySelector('#swap-net-amount').value===''&&document.querySelector('#swap-net-effective').value===''&&document.querySelector('#swap-net-reference').value===''&&[...document.querySelectorAll('#swap-net-selection tbody tr')].every(r=>!r.querySelector('input[type=checkbox]').checked&&r.querySelector('input[type=text]').value==='')&&document.querySelector('#swap-settlement-cash').textContent.includes('Synthetic browser net settlement checked later')&&document.querySelector('#swap-settlement-amount').value==='7'`,`[document.querySelector('#swap-net-amount').value,document.querySelector('#swap-net-reference').value,${candidates},document.querySelector('#swap-settlement-amount').value]`);
 const openingFrame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-15')).id;})()");
 await set('#swap-pnl-opening-snapshot',openingFrame);await click('#swap-pnl-capture-opening');await wait(idle);await set('#swap-pnl-contract-policy','closing_context_contract_changes_v1');await click('#swap-settlement-reconcile',60000);await wait(idle);
 await check('reporting captures an opening before booking and explicitly measures new-trade economics at closing',"!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement-bridge').textContent.includes('New-trade economics at closing')&&document.querySelector('#swap-settlement-bridge').textContent.includes('50')","document.querySelector('#swap-settlement').textContent.slice(-4000)");
 await figuresShown(['#swap-settlement-bridge','#swap-settlement-payments','#swap-settlement-balances'],'in the wide panel the bridge\'s amounts are read without scrolling sideways');
 await tablesFit('and no settlement table there scrolls sideways');
 await check('the period transitions\' stacked column has no column filter either, and their figure columns keep theirs',"(()=>{const cells=document.querySelectorAll('#swap-settlement-bridge .column-filter-row')[1]?.querySelectorAll('th');return !!cells&&!cells[0].querySelector('select')&&!!cells[1].querySelector('select');})()");
 await evaluate("document.querySelector('#swap-settlement-bridge').scrollIntoView({block:'start'})");await shot('contract-pnl-desktop');await copyFile(join(dirname(process.argv[2]),'contract-pnl-desktop.png'),join(artifacts,'pnl-desktop.png'));
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait("document.querySelector('#side-panel').dataset.placement==='overlay'");
 await evaluate("document.querySelector('#swap-settlement-bridge').scrollIntoView({block:'start'})");await shot('contract-pnl-narrow');await copyFile(join(dirname(process.argv[2]),'contract-pnl-narrow.png'),join(artifacts,'pnl-narrow.png'));
 await figuresReachable('in the panel\'s sheet each figure and action can be scrolled to in full');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 // Real-sized figures: with 17-character amounts and an unbroken cash reference, the wide panel shows every settlement table without
 // sideways scroll at 1440 and at 1280 pixels, and on a phone each figure and action can be scrolled to in full.
 const big='-1234567890123.45',unbroken='Synthetic'+'X'.repeat(120);
 await evaluate(`(()=>{const send=window.fetch,big=${JSON.stringify(big)},unbroken=${JSON.stringify(unbroken)},swap=(o,keys)=>{for(const k of keys)if(o&&typeof o[k]==='string')o[k]=big;};window.__realSize=send;
  window.fetch=async(input,init)=>{const response=await send(input,init),path=String(input);if(!response.ok||!/\\/api\\/swaps\\/settlement\\/(inspect|net-preview|pnl)$/.test(path))return response;const body=await response.clone().json();
   if(path.endsWith('/inspect')){for(const k of Object.keys(body.valuation))if(typeof body.valuation[k]==='string')body.valuation[k]=big;for(const p of body.valuation.payments)swap(p,['contractualAmount','remainingValue']);for(const b of body.balances)swap(b,['amount','applied','outstanding']);for(const c of body.cashMovements){swap(c,['amount']);c.reference=unbroken;}}
   if(path.endsWith('/net-preview')){swap(body,['amount','grossPaid','grossReceived']);for(const a of body.allocations)swap(a,['amount','remaining']);}
   if(path.endsWith('/pnl'))for(const t of body.report.trades){for(const k of Object.keys(t.bridge))t.bridge[k]=big;for(const p of t.transitions)swap(p,['openingPv','closingPv']);}
   return new Response(JSON.stringify(body),{status:response.status,headers:{'Content-Type':'application/json'}});};})()`);
 await click('#swap-settlement-refresh');await wait(idle);
 await evaluate("document.querySelectorAll('#swap-net-selection tbody tr').forEach(row=>{const selected=row.querySelector('input[type=checkbox]'),amount=row.querySelector('input[type=text]');selected.checked=true;selected.dispatchEvent(new Event('change',{bubbles:true}));amount.value=row.textContent.includes('Side 1')?'-10':'15';amount.dispatchEvent(new Event('input',{bubbles:true}));})");
 await set('#swap-net-amount','5');await set('#swap-net-effective','2021-01-15T08:00:00Z');await set('#swap-net-reference','Synthetic real-size review');await submit('#swap-net-form');await wait(idle);
 await click('#swap-settlement-reconcile',60000);await wait(idle);
 await check('the settlement view shows the real-sized amounts and the unbroken reference',`['#swap-settlement-payments','#swap-settlement-balances','#swap-net-review','#swap-settlement-bridge','#swap-settlement-cash'].every(id=>document.querySelector(id).textContent.includes(${JSON.stringify(big)}))&&document.querySelector('#swap-settlement-cash').textContent.includes(${JSON.stringify(unbroken)})`,"[...['#swap-settlement-payments','#swap-settlement-balances','#swap-net-review','#swap-settlement-bridge','#swap-settlement-cash'].map(id=>document.querySelector(id).textContent.slice(0,120)),document.querySelector('#swap-settlement [role=alert]').textContent]");
 for(const [width,height] of [[1440,900],[1280,800]]){
  await command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});await wait(panelIs('wide')+'&&'+idle);
  await tablesFit(`at ${width}×${height} real-sized amounts and an unbroken reference leave no settlement table scrolling sideways`);
  if(width===1440){await evaluate("document.querySelector('#swap-settlement-payments').scrollIntoView({block:'start'})");await shot('netting-real-size-desktop');await copyFile(join(dirname(process.argv[2]),'netting-real-size-desktop.png'),join(artifacts,'real-size-desktop.png'));}
  await figuresShown(['#swap-net-review','#swap-settlement-bridge','#swap-settlement-payments','#swap-settlement-balances','#swap-settlement-cash'],`and every figure and action there is read in place`);
 }
 // At about 800 pixels the drawer is as wide as the window beside the navigation allows, which stays in view and in reach.
 await command('Emulation.setDeviceMetricsOverride',{width:800,height:900,deviceScaleFactor:1,mobile:false});await wait("document.querySelector('#side-panel').hasAttribute('data-wide')&&"+idle);
 await check('at 800 pixels the wide drawer leaves the navigation uncovered',"(()=>{const panel=document.querySelector('#side-panel').getBoundingClientRect(),nav=document.querySelector('.sidebar').getBoundingClientRect();return nav.width>0&&nav.right<=panel.left+0.5&&panel.right>=innerWidth-0.5;})()","[document.querySelector('#side-panel').getBoundingClientRect().left,document.querySelector('.sidebar').getBoundingClientRect().right,innerWidth]");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait(panelIs('sheet'));
 await figuresReachable('on a phone each real-sized figure and action can be scrolled to in full');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await evaluate("window.fetch=window.__realSize");await click('#swap-settlement-refresh');await wait(idle);
 // Net terms entered for one trade do not follow the panel to another.
 await evaluate("(()=>{const box=document.querySelector('#swap-net-selection input[type=checkbox]');box.checked=true;box.dispatchEvent(new Event('change',{bubbles:true}));})()");
 await set('#swap-net-amount','7');await set('#swap-net-effective','2021-01-15T08:00:00Z');await set('#swap-net-reference','Synthetic abandoned net terms');
 await openView(selected.otherReference);
 await check('opening another trade clears net header terms, selections and review',"document.querySelector('#swap-net-amount').value===''&&document.querySelector('#swap-net-effective').value===''&&document.querySelector('#swap-net-reference').value===''&&!document.querySelector('#swap-net-record')&&[...document.querySelectorAll('#swap-net-selection input[type=checkbox]')].every(n=>!n.checked)");
 await check('and with no contractual-change policy or opening snapshot chosen',"document.querySelector('#swap-pnl-contract-policy').value===''&&document.querySelector('#swap-pnl-opening-snapshot').value===''","[document.querySelector('#swap-pnl-contract-policy').value,document.querySelector('#swap-pnl-opening-snapshot').value]");
 // The wide panel keeps the panel's keys: Escape closes it when it holds nothing unsaved.
 await check('the other trade\'s settlement view has the wide panel',panelIs('wide'));
 await evaluate("document.querySelector('#side-panel').focus()");
 for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key:'Escape',code:'Escape',windowsVirtualKeyCode:27,nativeVirtualKeyCode:27});
 await wait(idle);
 await check('Escape closes the wide panel when it holds nothing unsaved',"document.querySelector('#side-panel').hidden","[document.querySelector('#side-panel').hidden,document.activeElement?.id]");
 // Leaving the settlement view another way, by another position's row or by New trade, gives the panel back its own width.
 const ownWidth="(()=>{const p=document.querySelector('#side-panel');return !p.hidden&&!p.hasAttribute('data-wide')&&p.dataset.placement==='docked'&&Math.abs(p.getBoundingClientRect().width-340)<=2;})()";
 const panelWidth="[document.querySelector('#side-panel').getBoundingClientRect().width,document.querySelector('#side-panel').hasAttribute('data-wide'),document.querySelector('#side-panel').dataset.mode]";
 await openView(selected.reference);
 await set('#blotter-filters .table-search',selected.otherReference);await wait(idle);
 await wait(`(()=>{const rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length);return rows.length===1&&rows[0].textContent.includes(${JSON.stringify(selected.otherReference)});})()`);
 await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait(`document.querySelector('#side-panel-head .side-panel-reference')?.textContent.includes(${JSON.stringify(selected.otherReference)})&&`+idle);
 await check('another position opened from the desk over the wide settlement view has the panel\'s own width',`${ownWidth}&&!${open}`,panelWidth);
 await click('#side-panel-tabs-terms');await wait("!!document.querySelector('#side-panel-settlement')&&"+idle);await click('#side-panel-settlement');await wait(open);await wait(idle);
 await click('#futures-new');await wait("document.querySelector('#side-panel').dataset.mode==='new'&&!!document.querySelector('#new-trade-product')&&"+idle);
 await check('New trade over the wide settlement view opens its chooser at the panel\'s own width',ownWidth,panelWidth);
}
