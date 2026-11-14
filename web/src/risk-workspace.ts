import {setupFactorHistory} from './factor-history-view.js';
import {setupRisk,type RiskJob,type RunView} from './risk-view.js';
import {setupRiskJobs} from './risk-jobs-view.js';
import {copyRiskEditor,setupRiskResults} from './risk-results-view.js';
import {recentCompletedRuns,riskAddress} from './risk-results.js';
import {askInPanel} from './side-panel.js';
import {prepareAmountDisplay,riskReportRead} from './amount-display.js';
import {publicText} from './market-view.js';
import {riskScopeSummary,sameRiskScope} from './risk-scope.js';
import type {RiskView} from './risk-views.js';
import {setupBookScopePicker} from './scope-picker.js';
import {setupPnlView} from './pnl-view.js';
import {setupEconomicRisk} from './economic-risk-view.js';
import type {EconomicJob} from './economic-risk.js';

type Source={snapshotId:string;context?:{contextId?:string}};
type Scope={bookIds:string[];portfolioIds:string[];tradeIds:string[]|null};
type Trade={tradeId:string;bookId:string;tradeReference:string;product:string;available:boolean};
type TradePage={trades:Trade[];selectedTrades:Trade[];total:number;allAvailable:boolean;nextOffset:number|null;contextId:string;snapshotId:string;maxSelectedTrades:number};
type Directory={kind:string;asOf:string;context:{contextId:string;knownAt:string};books:{id:string;name:string;portfolio_id:string;portfolio:string}[]};
type Options={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;
 identity:()=>string;canRun:()=>boolean;instant:(value:string)=>string;source:()=>Source;frames:()=>{id:string;at:string;label:string;session:string|null}[];changed:()=>void;view:(view:RiskView)=>void};
/** Risk owns its setup and report, while using the same editor, request contracts and result widget as the Blotter. */
export function setupRiskWorkspace(o:Options){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const prefix='workspace-',host=get('risk-page'),editor=get<HTMLDetailsElement>('risk-workspace-editor');
 copyRiskEditor(get('risk-workspace-editor-body'),prefix);
 const start=(view:RiskView)=>()=>{if(!o.busy())o.view(view);};
 const results=setupRiskResults(get('risk-workspace-results'),o.zone,undefined,[{label:'Run scenarios & Greeks',go:start('risk-scenarios')},{label:'Run VaR / ES',go:start('risk-var')}]);
 const scopePicker=setupBookScopePicker({books:get('risk-workspace-books'),portfolios:get('risk-workspace-portfolios'),busy:o.busy});
 let epoch=0,directory:Directory|undefined,selection:Source|undefined,selectedJob:string|undefined,owner='',engine:ReturnType<typeof setupRisk>|undefined;
 let shown:RunView|undefined,question:AbortController|undefined,scope:Scope={bookIds:[],portfolioIds:[],tradeIds:null},unreadRecovery=false;
 let tradeChoices=new Map<string,Trade>(),tradesReady=false,allTradesAvailable=false,totalTrades=0,nextTradeOffset:number|null=null,maxTrades=256;
 let history:ReturnType<typeof setupFactorHistory>|undefined,economic:ReturnType<typeof setupEconomicRisk>|undefined,view:RiskView='risk',entering=false;
 const storedKey=()=>`openetrm.risk.workspace.submission.${o.identity()}`;
 const parentsValid=()=>!!directory&&directory.books.length>0&&scope.bookIds.every(id=>directory!.books.some(b=>b.id===id&&(!scope.portfolioIds.length||scope.portfolioIds.includes(b.portfolio_id))))&&scope.portfolioIds.every(id=>directory!.books.some(b=>b.portfolio_id===id));
 const scopeValid=()=>parentsValid()&&tradesReady&&(scope.tradeIds===null?allTradesAvailable:scope.tradeIds.length>0&&scope.tradeIds.length<=maxTrades&&scope.tradeIds.every(id=>tradeChoices.get(id)?.available));
 function retain(){
  if(!engine||owner!==o.identity()||unreadRecovery)return;
  const command=engine.handOff();
  try{if(command)sessionStorage.setItem(storedKey(),JSON.stringify(command));else sessionStorage.removeItem(storedKey());}
  catch{if(command)get('risk-workspace-note').textContent='This browser could not retain the unconfirmed submission across reload. Keep this page open and reconcile it.';}
 }
 // Risk shows its reports' amounts at the display precision, so its own reads of them prepare that text before they are drawn. A report
 // whose display text cannot be prepared is still drawn, with its exact amounts: formatting never hides a result.
 const reportApi=async<T>(path:string,body?:unknown):Promise<T>=>{
  const value=await o.api<T>(path,body);
  if(riskReportRead(path))await prepareAmountDisplay(value,request=>o.api<string[]>('amounts/display',request)).catch(()=>undefined);
  return value;
 };
 function changed(){
  if(!engine)return;retain();const run=engine.current();
  const idsOfJobs=run?.retained?run.parts.flatMap(p=>p.job?.id?[p.job.id]:[]):[];
  const next=selectedJob&&idsOfJobs.includes(selectedJob)?selectedJob:idsOfJobs[0];
  if(next!==selectedJob){const before=selectedJob;selectedJob=next;if(!host.hidden&&(next||location.hash==='#risk/job/'+before))window.history.replaceState(null,'',location.pathname+location.search+(next?'#risk/job/'+encodeURIComponent(next):''));}
  const different=run?.key!==shown?.key||run?.running!==shown?.running||run?.parts.some((p,i)=>{
   const before=shown?.parts[i];return p.report!==before?.report||p.job?.state!==before?.job?.state||p.failure!==before?.failure||p.unknown!==before?.unknown||p.unlisted!==before?.unlisted;
  });
  const finished=!!shown?.running&&!!run&&!run.running&&!engine.unconfirmed();
  // A report on the page answers the scope status that asked for one.
  if(different){shown=run;if(run){economic?.clearReport();get('risk-workspace-results').hidden=false;}results.show(run);if(run&&!engine.unconfirmed())get('risk-workspace-note').textContent='';}
  if(finished)o.view('risk');
  const first=run?.parts.find(p=>p.report)?.report;
  const differentScope=run?.scope&&!sameRiskScope(run.scope,scope);
  get('risk-workspace-context-note').textContent=first&&selection&&first.snapshotId!==selection.snapshotId?'Retained report from a different close than the header. Its captured inputs are shown below. Run a new calculation to use the header selection.':differentScope?'Retained report from a different portfolio, book or trade selection. Its captured scope is shown in the report; the filters above apply to a new run.':run?.retained?'Retained report: values use the captured context below, independently of the current header.':'';
  get<HTMLButtonElement>('risk-workspace-export').disabled=o.busy()||!economic?.canDownload()&&(!run||!run.parts.every(p=>p.report));o.changed();
 }
 function requestScope(){
  if(!directory||!selection||!scopeValid()||JSON.stringify(o.source())!==JSON.stringify(selection))throw Error('Choose a market close and refresh the Risk scope first.');
  return {...scope,context:{snapshotId:selection.snapshotId,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:directory.context.contextId}};
 }
 const pnl=setupPnlView(get('risk-pnl-view'),{...o,scope:()=>{const selected=requestScope();return {bookIds:selected.bookIds,portfolioIds:selected.portfolioIds,tradeIds:selected.tradeIds};},
  knownAt:()=>directory?.context.knownAt??'',
  // The header's close, when it can be read: the P&L page starts its comparison from it.
  selected:()=>{try{return o.source().snapshotId;}catch{return undefined;}}});
 economic=setupEconomicRisk(get('risk-economic-body'),get('risk-economic-results'),{...o,scope:requestScope,
  unconfirmed:()=>!!engine?.unconfirmed()||unreadRecovery,onShow(id){
   // Stop the prior static run's view polling; its accepted native job remains
   // in Run history. Unknown submissions stay in the engine's actor custody.
   engine?.reset();
   results.reset();get('risk-workspace-results').hidden=true;shown=undefined;selectedJob=id;
   window.history.replaceState(null,'',location.pathname+location.search+(id?'#risk/job/'+encodeURIComponent(id):''));
   get('risk-workspace-context-note').textContent='Event-aware report: its captured opening source, knowledge cutoff and common horizon are shown below.';
   o.view('risk');get<HTMLButtonElement>('risk-workspace-export').disabled=o.busy()||!economic?.canDownload();
  }});
 engine=setupRisk({api:reportApi,act:o.act,busy:o.busy,zone:o.zone,profile:o.identity,canRun:o.canRun,prefix,onDenied:reset,onChange:changed,ranNow:()=>o.view('risk'),edited:o.changed,scope:requestScope});
 history=setupFactorHistory({api:o.api,act:o.act,busy:o.busy,identity:o.identity,scope:requestScope,instant:o.instant,unconfirmed:()=>!!engine?.unconfirmed()||!!economic?.unconfirmed()||unreadRecovery,async calculate(request,metadata,queued){
  if(unreadRecovery)throw Error('Inspect Reports and runs before resolving the unreadable retained submission.');
  if(!await leave())return;
  economic?.clearReport();get('risk-workspace-results').hidden=false;
  editor.open=true;engine!.mountInto(get('risk-workspace-editor-body'));
  await engine!.calculate({...requestScope(),pricingPolicy:metadata.pricingPolicy,convention:metadata.convention,numericalPolicy:metadata.numericalPolicy,detail:request.historical||request.monteCarlo?'holdings':'books',bumps:[],stress:[],...request},queued,metadata.context,metadata.asOf);
  changed();o.view('risk');get('risk-workspace-results').scrollIntoView({block:'start'});
 }});
 const jobs=setupRiskJobs({api:o.api,act:o.act,instant:o.instant,openReport:openJob});
 function clear(){
  epoch++;question?.abort();question=undefined;directory=undefined;selection=undefined;selectedJob=undefined;shown=undefined;
  history?.reset();economic?.reset();results.reset();get('risk-workspace-results').hidden=false;engine?.reset();jobs.reset();editor.open=false;get('risk-workspace-note').textContent='';get('risk-workspace-context-note').textContent='';
  for(const id of ['risk-workspace-books','risk-workspace-portfolios'])get(id).replaceChildren();
  tradesReady=false;tradeChoices=new Map();totalTrades=0;nextTradeOffset=null;drawTrades();
 }
 function reset(){clear();pnl.reset();scope={bookIds:[],portfolioIds:[],tradeIds:null};drawTrades();owner='';unreadRecovery=false;}
 async function leave(){
  if(!engine!.edited()&&!economic?.edited())return true;if(question)return false;
  const asking=question=new AbortController();host.tabIndex=-1;
  try{const go=await askInPanel(host,{text:'The risk setup has unsaved choices. Leaving discards those edits. An unconfirmed submission stays available for reconciliation.',keep:'Keep editing',go:'Discard setup edits'})(asking.signal);if(go){engine!.dropEdits();economic?.dropEdits();}return go;}
  finally{if(question===asking)question=undefined;host.removeAttribute('tabindex');}
 }
 async function newRun(){
  o.view('risk-scenarios');
  if(unreadRecovery)throw Error('Inspect Reports and runs before resolving the unreadable retained submission.');
  if(economic?.unconfirmed())throw Error('Resolve the unconfirmed event-aware submission before starting another risk run.');
  economic?.clearReport();get('risk-workspace-results').hidden=false;
  // A setup with choices holds the run being prepared: show it, rather than ask to discard them.
  editor.open=true;
  if(!engine!.edited()){
   if(!engine!.unconfirmed()){engine!.reset();selectedJob=undefined;window.history.replaceState(null,'',location.pathname+location.search);}
   engine!.mountInto(get('risk-workspace-editor-body'));await engine!.open();
  }
  // The run setup sits below the report; bring it to the person who asked for it.
  editor.scrollIntoView({block:'start',behavior:'smooth'});editor.querySelector<HTMLElement>('summary')?.focus({preventScroll:true});
 }
 async function openJob(id:string,anchor?:RiskJob,chosen=true){
  const version=epoch;if(!await leave()||version!==epoch)return;
  economic?.clearReport();get('risk-workspace-results').hidden=false;
  results.reset();shown=undefined;selectedJob=undefined;
  try{
   const receipt=anchor??await reportApi<RiskJob>('portfolio/risk/jobs/'+encodeURIComponent(id));if(version!==epoch)return;
   if(receipt.kind==='economic_lifecycle'){
    await economic!.openJob(id,receipt as unknown as EconomicJob);return;
   }
   await engine!.reviewJob(id,receipt);if(version!==epoch)return;
   selectedJob=id;editor.open=true;engine!.mountInto(get('risk-workspace-editor-body'));
   window.history.replaceState(null,'',location.pathname+location.search+'#risk/job/'+encodeURIComponent(id));changed();if(chosen)o.view('risk');
  }catch(error){if(version===epoch){results.reset();get('risk-workspace-note').textContent='The retained report could not be read under current authority. Refresh to try again.';}throw error;}
 }
 function drawSummary(){
  const books=directory?.books??[],names=(ids:readonly string[],name:(id:string)=>string|undefined)=>ids.map(id=>name(id)??id);
  get('risk-scope-summary').textContent=riskScopeSummary({portfolios:names(scope.portfolioIds,id=>books.find(b=>b.portfolio_id===id)?.portfolio),books:names(scope.bookIds,id=>books.find(b=>b.id===id)?.name),trades:scope.tradeIds===null?null:scope.tradeIds.length});
 }
 function drawScope(){
  scopePicker.set(directory?.books??[],scope);drawSummary();
 }
 function drawTrades(){
  drawSummary();
  get<HTMLSelectElement>('risk-workspace-trade-mode').value=scope.tradeIds===null?'all':'selected';
  const groups=new Map<string,HTMLOptGroupElement>();
  const products:Record<string,string>={future:'Futures',financial_swap:'Swap',ppa:'PPA',physical_gas:'Gas',physical_crude:'Crude',physical_lng:'LNG',futures_option:'Futures option'};
  for(const trade of tradeChoices.values()){
   let group=groups.get(trade.bookId);
   if(!group){group=document.createElement('optgroup');const book=directory?.books.find(b=>b.id===trade.bookId);group.label=book?`${book.portfolio} · ${book.name}`:'Selected book';groups.set(trade.bookId,group);}
   const option=new Option(`${trade.tradeReference} · ${products[trade.product]??trade.product}`,trade.tradeId);option.selected=scope.tradeIds?.includes(trade.tradeId)??false;option.disabled=!trade.available;group.append(option);
  }
  get<HTMLSelectElement>('risk-workspace-trades').replaceChildren(...groups.values());
  get('risk-workspace-trade-help').textContent=!tradesReady?'Choose an available portfolio/book scope to read its trades.':scope.tradeIds===null&&!allTradesAvailable?'Some trade identities are unavailable. Restore access or choose available individual trades before running risk.':scope.tradeIds===null?
   `All ${totalTrades} trades in these books are included. Select trades below to switch to an individual selection.`:
   scope.tradeIds.length?`${scope.tradeIds.length} selected ${scope.tradeIds.length===1?'trade':'trades'} · choose at most ${maxTrades}. Every instrument binding of each selected trade is included.`:'Select at least one trade before running risk. An empty selection does not mean the whole book.';
  get('risk-workspace-trades-more').hidden=nextTradeOffset===null;
 }
 async function readTrades(resetPage:boolean){
  if(!directory||!selection||!parentsValid()){tradesReady=false;tradeChoices.clear();nextTradeOffset=null;drawTrades();return;}
  const version=epoch,offset=resetPage?0:nextTradeOffset;if(offset===null)return;
  tradesReady=false;get('risk-workspace-trade-help').textContent='Reading trades in these books…';
  try{
   const page=await o.api<TradePage>('portfolio/risk/scope',{context:{snapshotId:selection.snapshotId,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:directory.context.contextId},
    ...scope,offset,limit:100});if(version!==epoch)return;
   if(page.contextId!==directory.context.contextId||page.snapshotId!==selection.snapshotId||!Array.isArray(page.trades)||!Array.isArray(page.selectedTrades))throw Error('The trade directory does not match the selected context.');
   if(resetPage)tradeChoices.clear();for(const trade of [...page.trades,...page.selectedTrades])tradeChoices.set(trade.tradeId,trade);
   totalTrades=page.total;nextTradeOffset=page.nextOffset;maxTrades=page.maxSelectedTrades;allTradesAvailable=page.allAvailable;tradesReady=true;drawTrades();
  }catch(error){if(version!==epoch)return;tradesReady=false;tradeChoices.clear();nextTradeOffset=null;drawTrades();get('risk-workspace-trade-help').textContent='The trade selection could not be read. Choose all trades or adjust the books, then refresh. Your selection has not been widened.';if((error as {code?:string}).code==='access_denied')throw error;}
 }
 async function load(selectedAddress?:string){
  if(owner!==o.identity())scope={bookIds:[],portfolioIds:[],tradeIds:null};
  // The scope is read again: a P&L result, and the cutoff it was read at, belong to the scope as it was.
  clear();pnl.reset();owner=o.identity();const version=epoch;unreadRecovery=false;
  results.show(undefined);get('risk-workspace-note').textContent='Reading the selected scope and recent reports…';
  // A retained report does not depend on the current header being priceable.
  await jobs.load();if(version!==epoch)return;
  try{
   selection=structuredClone(o.source());const source=selection;
   const read=await o.api<Directory>('trading/directory',{snapshotId:source.snapshotId,convention:'trading_workspace_v1',knowledgeMode:source.context?'retained':'current',...(source.context?{contextId:source.context.contextId}:{})});
   if(version!==epoch)return;
   if(read.kind!=='directory'||!read.context?.contextId||!Array.isArray(read.books))throw Error('The selected scope directory is inconsistent.');
   directory=read;drawScope();await readTrades(true);if(version!==epoch)return;
   get('risk-workspace-note').textContent=scopeValid()?'Select portfolios, their books, and either all trades or individual trades. Reports retain their own exact context.':'Choose an available scope and at least one trade when using an individual selection.';
  }catch(error){
   if(version!==epoch)return;if((error as {code?:string}).code==='access_denied')throw error;
   directory=undefined;get('risk-workspace-note').textContent=publicText(`Run setup unavailable: ${error instanceof Error?error.message:'Choose a market close.'} Retained reports can still be opened.`);
  }
  if(version!==epoch)return;
  try{const kept=sessionStorage.getItem(storedKey());if(kept&&!engine!.unconfirmed())engine!.adopt(JSON.parse(kept));}catch(error){unreadRecovery=true;get('risk-workspace-note').textContent='A retained submission could not be read. Inspect Reports and runs before submitting another request.';throw error;}
  if(engine!.unconfirmed()){editor.open=true;engine!.mountInto(get('risk-workspace-editor-body'));await engine!.open();o.view('risk-scenarios');return;}
  // Opening a retained report ends the load without entering the view; P&L explain still reads its closes for the header now shown.
  const openRetained=async(open:()=>Promise<void>)=>{await open();if(version===epoch&&view==='risk-pnl')await pnl.load();};
  const address=selectedAddress??riskAddress(location.hash);if(address){await openRetained(()=>openJob(address));return;}
  if(directory&&selection&&scopeValid()){
   for(const candidate of recentCompletedRuns(jobs.recent(),scope,directory.asOf)){
    const job=await o.api<RiskJob>('portfolio/risk/jobs/'+encodeURIComponent(candidate.id));if(version!==epoch)return;
    if(job.result?.snapshotId!==selection.snapshotId)continue;
    await openRetained(()=>openJob(job.id,job,false));return;
   }
   get('risk-workspace-note').textContent='No completed recent report matches these books and this close. Run risk here, or open a retained report from Reports and runs.';
  }
  entering=true;
 }
 async function changeScope(next:Scope){
  if(!await leave()){drawScope();drawTrades();return;}const version=++epoch;scope=next;pnl.reset();history?.reset();economic?.reset();engine!.inputsChanged(false);results.show(undefined);shown=undefined;selectedJob=undefined;
  window.history.replaceState(null,'',location.pathname+location.search);get('risk-workspace-note').textContent='Scope changed. Run risk for this selection or open a retained report.';
  drawScope();await readTrades(true);if(version!==epoch)return;if(editor.open&&scopeValid())await engine!.open();o.changed();
 }
 for(const id of ['risk-workspace-books','risk-workspace-portfolios'])get(id).onchange=()=>void o.act(()=>changeScope({
  bookIds:Array.from(get<HTMLSelectElement>('risk-workspace-books').selectedOptions).map(o=>o.value),portfolioIds:Array.from(get<HTMLSelectElement>('risk-workspace-portfolios').selectedOptions).map(o=>o.value),tradeIds:scope.tradeIds===null?null:[]}));
 get('risk-workspace-trade-mode').onchange=()=>void o.act(()=>changeScope({...scope,tradeIds:get<HTMLSelectElement>('risk-workspace-trade-mode').value==='all'?null:[]}));
 get('risk-workspace-trades').onchange=()=>void o.act(()=>changeScope({...scope,tradeIds:Array.from(get<HTMLSelectElement>('risk-workspace-trades').selectedOptions).map(o=>o.value)}));
 get('risk-workspace-trades-more').onclick=()=>void o.act(()=>readTrades(false));
 get('risk-jobs-new').onclick=()=>void o.act(newRun);
 // The view shows its panels at once; what it opens waits for the scope and for the application to be free.
 function enterView(){entering=false;if(view==='risk-pnl')void o.act(()=>pnl.load());else if(view==='risk-var')history?.prepare();else if(view==='risk-scenarios'&&!editor.open&&!get<HTMLButtonElement>('risk-jobs-new').disabled)void o.act(newRun);}
 function showView(next:RiskView){view=next;for(const part of host.querySelectorAll<HTMLElement>('[data-risk-view]'))part.classList.toggle('risk-view-off',part.dataset.riskView!==next);entering=true;if(!o.busy()&&directory)enterView();}
 get('risk-workspace-refresh').onclick=()=>void (async()=>{if(!o.busy()&&await leave())await o.act(load);})();
 get('risk-workspace-export').onclick=()=>void o.act(async()=>{if(economic?.shown())economic.download();else await engine!.download();});
 return {load,reset,leave,openJob,refresh:()=>{if(!host.hidden)results.refresh();engine!.refresh();economic?.refresh();void pnl.refresh();},
  showView,sync(){pnl.sync(scopeValid());history?.sync(scopeValid()&&o.canRun());economic?.sync(scopeValid()&&o.canRun());engine!.sync();get<HTMLButtonElement>('risk-jobs-new').disabled=o.busy()||!o.canRun()||!scopeValid()||unreadRecovery||!!economic?.unconfirmed();if(entering&&!o.busy()&&directory)enterView();
   get<HTMLButtonElement>('risk-workspace-export').disabled=o.busy()||!(economic?.shown()?economic.canDownload():engine!.current()?.parts.every(p=>p.report));
   for(const id of ['risk-workspace-portfolios','risk-workspace-books','risk-workspace-trade-mode','risk-workspace-trades'])get<HTMLSelectElement>(id).disabled=o.busy()||!directory;
   get<HTMLButtonElement>('risk-workspace-trades-more').disabled=o.busy()||!tradesReady;scopePicker.sync();},
  financialChanged(){
   epoch++;history?.reset();economic?.financialChanged();
   if(!engine!.current()?.retained){engine!.inputsChanged(engine!.edited());results.reset();shown=undefined;}
   directory=undefined;selection=undefined;tradesReady=false;tradeChoices.clear();drawTrades();
   get('risk-workspace-note').textContent='Lifecycle facts changed. Refresh Risk scope before a new run. Captured reports and original submissions retain their inputs.';
   get('risk-workspace-context-note').textContent=shown?.retained||economic?.shown()?'This captured report predates the lifecycle change and retains its original source.':'';
   o.changed();
  },
  contextChanged(){epoch++;pnl.reset();history?.reset();economic?.reset();engine!.inputsChanged(engine!.edited());results.reset();shown=undefined;directory=undefined;selection=undefined;scopePicker.set([],{bookIds:[],portfolioIds:[]});tradesReady=false;tradeChoices.clear();drawTrades();get('risk-workspace-note').textContent='The selected market context changed. Refresh Risk to read the new scope.';},
  edited:()=>engine!.edited()||!!economic?.edited(),unconfirmed:()=>engine!.unconfirmed()||pnl.unconfirmed()||!!economic?.unconfirmed(),
  address:()=>selectedJob?'#risk/job/'+encodeURIComponent(selectedJob):'',
  assistant:()=>selectedJob&&(economic?.jobId()===selectedJob||engine!.current()?.retained&&engine!.current()?.parts.some(p=>p.job?.id===selectedJob))?{operation:'portfolio.risk.jobs.id_v1',inputJson:JSON.stringify({body:null,parameters:{id:selectedJob}})}:undefined};
}
