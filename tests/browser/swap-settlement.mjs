import {mkdtemp,copyFile} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {tmpdir} from 'node:os';
// A financial swap's settlement and P&L view (D215) opens from the Terms of its position in the Blotter's side panel, in the panel's form
// part (D218), and works there: determination, cash, reversal and the P&L bridge, with a stale preview dropped on a context change. What is
// entered in it is an edit the panel asks about, a settlement clears risk results run before it, and a command whose outcome is not known
// is kept, across a reload too, and settled from the panel with the view left open.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url}){
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);
 // An account that may both settle and run risk, so that a settlement made while risk results show is seen to clear them.
 await persona('admin');
 await evaluate(`(async()=>{const b=await(await fetch('/api/bootstrap')).json();const r=await fetch('/api/admin/create',{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':b.csrfToken},body:JSON.stringify({operationId:crypto.randomUUID(),login:'settlement_risk_browser',name:'Synthetic settlement and risk account',kind:'human',roles:['trader','risk_analyst']})});if(!r.ok)throw Error(await r.text());})()`);
 await persona('settlement_risk_browser');
 // A request can be refused, or reach the backend and have its answer lost, on demand.
 const intercept=`(()=>{const send=window.fetch;window.fetch=async(input,init)=>{const path=typeof input==='string'?input:input.url;if(path===window.__refuse){window.__refuse=undefined;return new Response(JSON.stringify({code:'settlement_stale_preview',message:'Synthetic refusal: the preview changed.'}),{status:409,headers:{'Content-Type':'application/json'}});}const response=await send(input,init);if(path===window.__lose){window.__lose=undefined;return new Response('incomplete',{status:502});}return response;};})()`;
 await evaluate(intercept);
 const typeInto=async(selector,text)=>{await evaluate(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});n.focus();n.select();})()`);await command('Input.insertText',{text});};
 const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2021-01-15T08:00:00')).id;})()");
 await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait(idle);
 const selected=await evaluate(`(async()=>{const token=(await(await fetch('/api/bootstrap')).json()).csrfToken;window.__settlementPost=async(path,body)=>{const r=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});if(!r.ok)throw Error(await r.text());return r.json();};window.__settlementContext={snapshotId:${JSON.stringify(frame)},convention:'independent_side_financial_swap_v2',knowledgeMode:'current'};const report=await window.__settlementPost('trading/blotter',{context:{...window.__settlementContext,convention:'trading_workspace_v1'},view:{mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[],pivotConvention:null}});const trade=report.holdings.find(h=>h.instrumentType==='financial_swap'&&h.settlement==='Projection'&&h.fixedPrice==='3');window.__settlementTrade=trade.tradeId;return {tradeId:trade.tradeId,reference:trade.tradeReference};})()`);
 // A risk report run at this close, whose results a settlement made afterwards no longer holds for.
 await click('#desk-risk-run');await wait("[...document.querySelector('#risk-factor').options].some(o=>o.value)&&"+idle);
 await set('#risk-factor',await evaluate("[...document.querySelector('#risk-factor').options].find(o=>o.value).value"));await set('#risk-bump','0.1');await submit('#risk-form');
 await wait("!!document.querySelector('#risk-results .tabulator-row')");
 // The settlement view is in the panel's form part, open; it is opened from the Terms of the position's row.
 const open="(()=>{const view=document.querySelector('#swap-settlement'),form=document.querySelector('#side-panel-form');return !!view&&!view.hidden&&form.contains(view)&&!form.hidden;})()";
 async function openView(){
  await set('#blotter-filters .table-search',selected.reference);await wait("[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length).length===1");
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#side-panel').dataset.mode==='position'&&"+idle);
  await click('#side-panel-tabs-terms');await wait("!!document.querySelector('#side-panel-settlement')&&"+idle);
  await click('#side-panel-settlement');await wait(open);await wait(idle);
 }
 await openView();
 await check('a swap\'s Terms offer its settlement and P&L view, which takes the panel\'s form part in place of its terms, with the head\'s Close editor',"document.querySelector('#side-panel-tabs-terms').getAttribute('aria-selected')==='true'&&document.querySelector('#side-panel-view').children.length===0&&document.querySelector('#side-panel-amend').textContent==='Close editor'&&document.querySelector('#swap-settlement-title').textContent.includes("+JSON.stringify(selected.reference)+")");
 await check('settlement inspection shows gross occurrences, separate balances and P&L measures',"document.querySelector('#swap-settlement').textContent.includes('Remaining contractual value')&&document.querySelector('#swap-settlement').textContent.includes('Actual net cash received')&&document.querySelector('#swap-settlement').textContent.includes('ties to even')");
 await click('#swap-settlement button[data-membership="0"][data-payment="0"]');await wait("!!document.querySelector('#swap-settlement-determine')");await wait(idle);
 await check('final payment review retains exact price evidence before determination',"document.querySelector('#swap-settlement').textContent.includes('Final gross payment calculation')&&document.querySelector('#swap-settlement details pre').textContent.includes('contractHash')");
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-settlement-visual-'));console.log('SETTLEMENT_VISUAL_ARTIFACTS='+artifacts);
 await shot('settlement-desktop');await copyFile(join(dirname(process.argv[2]),'settlement-desktop.png'),join(artifacts,'desktop.png'));
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait("document.querySelector('#side-panel').dataset.placement==='overlay'");await shot('settlement-narrow');await copyFile(join(dirname(process.argv[2]),'settlement-narrow.png'),join(artifacts,'narrow.png'));
 await check('the settlement view fits a narrow viewport in the panel\'s sheet',"document.documentElement.scrollWidth<=document.documentElement.clientWidth+1&&document.querySelector('#swap-settlement').scrollWidth<=document.querySelector('#swap-settlement').clientWidth+1","({root:[document.documentElement.clientWidth,document.documentElement.scrollWidth],dialog:[document.querySelector('#swap-settlement').clientWidth,document.querySelector('#swap-settlement').scrollWidth],overflow:[...document.querySelectorAll('#swap-settlement *')].filter(n=>n.scrollWidth>n.clientWidth+2).map(n=>[n.tagName,n.id,n.className,n.clientWidth,n.scrollWidth]).slice(0,15)})");
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await click('#swap-settlement-determine');await wait(idle);
 await check('determine action creates an immutable original contribution',"!document.querySelector('#swap-settlement').hidden&&document.querySelector('#side-panel-form').contains(document.querySelector('#swap-settlement'))&&!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement-contribution').options.length>1&&!document.querySelector('#swap-settlement-determine')");
 const riskCleared='Risk results cleared · a settlement record changed the values they were run on';
 await check('a settlement that goes through clears the risk results run before it, and the page says so',`!document.querySelector('#risk-results .tabulator-row')&&document.querySelector('#desk-tile-risk .desk-risk-state').textContent==='Not run at this close'&&!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes(${JSON.stringify(riskCleared)})`,"[document.querySelector('#notice').textContent,document.querySelector('#desk-tile-risk').textContent,!!document.querySelector('#risk-results .tabulator-row')]");
 const balance=await evaluate("(async()=>{const r=await window.__settlementPost('swaps/settlement/inspect',{context:window.__settlementContext,tradeId:window.__settlementTrade,revision:null,duePolicy:'swap_due_nominal_v1',recognitionPolicy:'swap_completed_period_trading_pnl_v1'});return r.balances[0];})()");
 await set('#swap-settlement-contribution',balance.determinationId);await set('#swap-settlement-amount',balance.outstanding);await set('#swap-settlement-effective','2021-01-15T08:00:00Z');await set('#swap-settlement-reference','Synthetic browser cash');await submit('#swap-settlement-payment-form');await wait(idle);
 await check('manual full payment appears in cash history and reduces the selected balance',"!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement').textContent.includes('Synthetic browser cash')&&document.querySelector('#swap-settlement').textContent.includes('Paid')","({error:document.querySelector('#swap-settlement [role=alert]').textContent,text:document.querySelector('#swap-settlement').textContent.slice(-3000),invalid:[...document.querySelectorAll('#swap-settlement-payment-form :invalid')].map(n=>[n.id,n.validationMessage]),values:[...document.querySelectorAll('#swap-settlement-payment-form input')].map(n=>[n.id,n.value])})");
 const reversal=await evaluate("[...document.querySelector('#swap-settlement-reversal').options].find(o=>o.value).value");await set('#swap-settlement-reversal',reversal);await set('#swap-settlement-effective','2021-01-15T08:00:00Z');await set('#swap-settlement-reference','Synthetic browser reversal');await click('#swap-settlement-reverse');await wait(idle);
 await check('payment reversal retains original and reverse cash movements',"!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement').textContent.includes('Synthetic browser reversal')&&document.querySelector('#swap-settlement').textContent.includes('Synthetic browser cash')");
 await click('#swap-settlement-opening');await click('#swap-settlement-reconcile');await wait(idle);
 await check('P&L reconciliation uses retained opening and closing contexts',"!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement').textContent.includes('Economic P&L')&&document.querySelector('#swap-settlement').textContent.includes('retained knowledge context')");

 // A correction's due instant is an edit like any other: the panel asks before the view is closed, and keeping it keeps it.
 const prompt="document.querySelector('#side-panel-prompt')";
 await click('#swap-settlement button[data-membership="0"][data-payment="0"]');await wait("!!document.querySelector('#swap-settlement-correction-due')&&"+idle);
 await typeInto('#swap-settlement-correction-due','2021-03-20T00:00:00Z');await click('#side-panel-amend');
 await check('a correction due instant typed in the settlement view makes the panel ask before the view is closed',`!${prompt}.hidden&&${prompt}.textContent.includes('unsaved changes')`,`[${prompt}.hidden,${prompt}.textContent,document.querySelector('#side-panel-amend').textContent]`);
 await click('#side-panel-prompt button.secondary');
 await check('keeping it leaves the view open with the due instant as it was typed',`${open}&&${prompt}.hidden&&document.querySelector('#swap-settlement-correction-due').value==='2021-03-20T00:00:00Z'`);
 // A determination the backend refuses keeps the calculation it was asked from, with the reason.
 await click('#swap-settlement button[data-membership="1"][data-payment="0"]');await wait("!!document.querySelector('#swap-settlement-determine')&&!document.querySelector('#swap-settlement-determine').disabled&&"+idle);
 await evaluate("window.__refuse='/api/swaps/settlement/determine'");await click('#swap-settlement-determine');await wait(idle);
 await check('a determination the backend refuses keeps its calculation and evidence in the view, and says why',`${open}&&!!document.querySelector('#swap-settlement-determine')&&document.querySelector('#swap-settlement').textContent.includes('Final gross payment calculation')&&document.querySelector('#swap-settlement [role=alert]').textContent.includes('Synthetic refusal')`,"[document.querySelector('#swap-settlement [role=alert]').textContent,!!document.querySelector('#swap-settlement-determine'),document.querySelector('#futures-pending').hidden]");
 // A settlement command whose outcome is not known is kept in the panel's card, named for what it was; Check status settles it there
 // and leaves the view open, reading its state again.
 const card=label=>`(()=>{const card=document.querySelector('#futures-pending');return !card.hidden&&card.getClientRects().length>0&&card.textContent.includes(${JSON.stringify(label)})&&!document.querySelector('#futures-reconcile').disabled;})()`;
 await click('#swap-settlement button[data-membership="1"][data-payment="0"]');await wait("!!document.querySelector('#swap-settlement-determine')&&!document.querySelector('#swap-settlement-determine').disabled&&"+idle);
 await evaluate("window.__lose='/api/swaps/settlement/determine'");await click('#swap-settlement-determine');await wait("!document.querySelector('#futures-pending').hidden&&"+idle);
 await check('a settlement determination whose outcome is not known is kept in the panel\'s card, named for it, with Check status and Retry',`${card('Settlement determination of '+selected.reference)}&&document.querySelector('#side-panel').contains(document.querySelector('#futures-pending'))&&!document.querySelector('#futures-retry').disabled`,"[document.querySelector('#futures-pending').textContent,document.querySelector('#futures-retry').disabled]");
 await click('#futures-reconcile');await wait("document.querySelector('#futures-pending').hidden&&"+idle);
 await check('Check status settles it and leaves the settlement view open, which shows the determination',`${open}&&document.querySelector('#swap-settlement-contribution').options.length>2`,"[document.querySelector('#side-panel').hidden,document.querySelector('#side-panel').dataset.mode,!!document.querySelector('#swap-settlement')&&document.querySelector('#swap-settlement').hidden,document.querySelector('#swap-settlement-contribution')?.options.length]");
 // A cash record whose outcome is not known is still kept after a reload, and Retry sends it again from the reopened view, which stays.
 const outstanding=await evaluate("(async()=>{const r=await window.__settlementPost('swaps/settlement/inspect',{context:window.__settlementContext,tradeId:window.__settlementTrade,revision:null,duePolicy:'swap_due_nominal_v1',recognitionPolicy:'swap_completed_period_trading_pnl_v1'});return r.balances.find(b=>b.membershipOrdinal===1&&b.outstanding!=='0');})()");
 await set('#swap-settlement-contribution',outstanding.determinationId);await set('#swap-settlement-amount',outstanding.outstanding);await set('#swap-settlement-effective','2021-01-15T08:00:00Z');await set('#swap-settlement-reference','Synthetic lost browser cash');
 await evaluate("window.__lose='/api/swaps/settlement/payment'");await submit('#swap-settlement-payment-form');await wait("!document.querySelector('#futures-pending').hidden&&"+idle);
 await command('Page.reload');await wait(idle);await evaluate(intercept);
 await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait(idle);
 await check('after a reload the cash record whose outcome is not known is still kept, named for it',card('Settlement cash of '+selected.reference),"[document.querySelector('#futures-pending').hidden,document.querySelector('#futures-pending').textContent]");
 await openView();
 await click('#futures-retry');await wait("document.querySelector('#futures-pending').hidden&&"+idle);
 await check('Retry sends it again and leaves the settlement view open, which shows the cash',`${open}&&document.querySelector('#swap-settlement').textContent.includes('Synthetic lost browser cash')`,"[document.querySelector('#side-panel').hidden,!!document.querySelector('#swap-settlement')&&document.querySelector('#swap-settlement').hidden,document.querySelector('#error').textContent]");

 await click('#swap-settlement button[data-membership="1"][data-payment="0"]');await wait("!!document.querySelector('#swap-settlement-determine')");
 const oldFrame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");await set('#snapshot',oldFrame);await wait(idle);
 await check('a financial context change discards a stale settlement preview',"!document.querySelector('#swap-settlement-determine')&&(document.querySelector('#swap-settlement').hidden||document.querySelector('#swap-settlement [role=status]').textContent.includes('context changed'))");
}
