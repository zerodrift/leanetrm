import {setupRisk,type RiskJob,type RunView} from './risk-view.js';
import {setupRiskJobs} from './risk-jobs-view.js';
import {copyRiskEditor,setupRiskResults} from './risk-results-view.js';
import {recentCompletedRuns,riskAddress} from './risk-results.js';
import {askInPanel} from './side-panel.js';
import {publicText} from './market-view.js';

type Source={snapshotId:string;context?:{contextId?:string}};
type Scope={bookIds:string[];portfolioIds:string[]};
type Directory={kind:string;asOf:string;context:{contextId:string;knownAt:string};books:{id:string;name:string;portfolio_id:string;portfolio:string}[]};
type Options={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;
 identity:()=>string;canRun:()=>boolean;instant:(value:string)=>string;source:()=>Source;changed:()=>void};
/** Risk owns its setup and report, while using the same editor, request contracts and result widget as the Blotter. */
export function setupRiskWorkspace(o:Options){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const prefix='workspace-',host=get('risk-page'),editor=get<HTMLDetailsElement>('risk-workspace-editor');
 copyRiskEditor(get('risk-workspace-editor-body'),prefix);
 const results=setupRiskResults(get('risk-workspace-results'),o.zone);
 let epoch=0,directory:Directory|undefined,selection:Source|undefined,selectedJob:string|undefined,owner='',engine:ReturnType<typeof setupRisk>|undefined;
 let shown:RunView|undefined,question:AbortController|undefined,scope:Scope={bookIds:[],portfolioIds:[]},unreadRecovery=false;
 const storedKey=()=>`openetrm.risk.workspace.submission.${o.identity()}`;
 const scopeValid=()=>!!directory&&scope.bookIds.every(id=>directory!.books.some(b=>b.id===id))&&scope.portfolioIds.every(id=>directory!.books.some(b=>b.portfolio_id===id));
 function retain(){
  if(!engine||owner!==o.identity()||unreadRecovery)return;
  const command=engine.handOff();
  try{if(command)sessionStorage.setItem(storedKey(),JSON.stringify(command));else sessionStorage.removeItem(storedKey());}
  catch{if(command)get('risk-workspace-note').textContent='This browser could not retain the unconfirmed submission across reload. Keep this page open and reconcile it.';}
 }
 function changed(){
  if(!engine)return;retain();const run=engine.current();
  const idsOfJobs=run?.retained?run.parts.flatMap(p=>p.job?.id?[p.job.id]:[]):[];
  const next=selectedJob&&idsOfJobs.includes(selectedJob)?selectedJob:idsOfJobs[0];
  if(next!==selectedJob){const before=selectedJob;selectedJob=next;if(!host.hidden&&(next||location.hash==='#risk/job/'+before))window.history.replaceState(null,'',location.pathname+location.search+(next?'#risk/job/'+encodeURIComponent(next):''));}
  const different=run?.key!==shown?.key||run?.running!==shown?.running||run?.parts.some((p,i)=>{
   const before=shown?.parts[i];return p.report!==before?.report||p.job?.state!==before?.job?.state||p.failure!==before?.failure||p.unknown!==before?.unknown||p.unlisted!==before?.unlisted;
  });
  if(different){shown=run;results.show(run);}
  const first=run?.parts.find(p=>p.report)?.report;
  const ids=(values:readonly string[])=>JSON.stringify([...values].sort());
  const differentScope=run?.scope&&(ids(run.scope.bookIds)!==ids(scope.bookIds)||ids(run.scope.portfolioIds)!==ids(scope.portfolioIds));
  get('risk-workspace-context-note').textContent=first&&selection&&first.snapshotId!==selection.snapshotId?'Retained report from a different close than the header. Its captured inputs are shown below. Run a new calculation to use the header selection.':differentScope?'Retained report from a different book or portfolio selection. Its captured scope is shown in the report; the filters above apply to a new run.':run?.retained?'Retained report: values use the captured context below, independently of the current header.':'';
  get<HTMLButtonElement>('risk-workspace-export').disabled=o.busy()||!run||!run.parts.every(p=>p.report);o.changed();
 }
 engine=setupRisk({api:o.api,act:o.act,busy:o.busy,zone:o.zone,profile:o.identity,canRun:o.canRun,prefix,onDenied:reset,onChange:changed,edited:o.changed,scope:()=>{
  if(!directory||!selection||!scopeValid()||JSON.stringify(o.source())!==JSON.stringify(selection))throw Error('Choose a market close and refresh the Risk scope first.');
  return {...scope,context:{snapshotId:selection.snapshotId,convention:'trading_workspace_v1',knowledgeMode:'retained',contextId:directory.context.contextId}};
 }});
 const jobs=setupRiskJobs({api:o.api,act:o.act,instant:o.instant,openReport:openJob});
 function clear(){
  epoch++;question?.abort();question=undefined;directory=undefined;selection=undefined;selectedJob=undefined;shown=undefined;
  results.reset();engine?.reset();jobs.reset();editor.open=false;get('risk-workspace-note').textContent='';get('risk-workspace-context-note').textContent='';
  for(const id of ['risk-workspace-books','risk-workspace-portfolios'])get(id).replaceChildren();
 }
 function reset(){clear();scope={bookIds:[],portfolioIds:[]};owner='';unreadRecovery=false;}
 async function leave(){
  if(!engine!.edited())return true;if(question)return false;
  const asking=question=new AbortController();host.tabIndex=-1;
  try{const go=await askInPanel(host,{text:'The risk setup has unsaved choices. Leaving discards those edits. An unconfirmed submission stays available for reconciliation.',keep:'Keep editing',go:'Discard setup edits'})(asking.signal);if(go)engine!.dropEdits();return go;}
  finally{if(question===asking)question=undefined;host.removeAttribute('tabindex');}
 }
 async function newRun(){
  if(unreadRecovery)throw Error('Inspect Reports and runs before resolving the unreadable retained submission.');
  if(!await leave())return;
  if(!engine!.unconfirmed()){engine!.reset();selectedJob=undefined;window.history.replaceState(null,'',location.pathname+location.search);}
  editor.open=true;engine!.mountInto(get('risk-workspace-editor-body'));await engine!.open();
 }
 async function openJob(id:string,anchor?:RiskJob){
  const version=epoch;if(!await leave()||version!==epoch)return;
  results.reset();shown=undefined;selectedJob=undefined;
  try{
   await engine!.reviewJob(id,anchor);if(version!==epoch)return;
   selectedJob=id;editor.open=true;engine!.mountInto(get('risk-workspace-editor-body'));
   window.history.replaceState(null,'',location.pathname+location.search+'#risk/job/'+encodeURIComponent(id));changed();
  }catch(error){if(version===epoch){results.reset();get('risk-workspace-note').textContent='The retained report could not be read under current authority. Refresh to try again.';}throw error;}
 }
 function drawScope(){
  if(!directory)return;
  const books=directory.books.map(b=>({id:b.id,name:`${b.portfolio} · ${b.name}`})),portfolios=[...new Map(directory.books.map(b=>[b.portfolio_id,b.portfolio]))].map(([id,name])=>({id,name}));
  for(const [id,rows,selected] of [['risk-workspace-books',books,scope.bookIds],['risk-workspace-portfolios',portfolios,scope.portfolioIds]] as const){
   const control=get<HTMLSelectElement>(id);control.replaceChildren(...rows.map(row=>new Option(row.name,row.id)),...selected.filter(value=>!rows.some(row=>row.id===value)).map(value=>new Option('Selected scope no longer readable',value)));
   control.selectedIndex=-1;for(const option of control.options)option.selected=selected.includes(option.value);
  }
 }
 async function load(selectedAddress?:string){
  if(owner!==o.identity())scope={bookIds:[],portfolioIds:[]};
  clear();owner=o.identity();const version=epoch;unreadRecovery=false;
  results.show(undefined);get('risk-workspace-note').textContent='Reading the selected scope and recent reports…';
  // A retained report does not depend on the current header being priceable.
  await jobs.load();if(version!==epoch)return;
  try{
   selection=structuredClone(o.source());const source=selection;
   const read=await o.api<Directory>('trading/directory',{snapshotId:source.snapshotId,convention:'trading_workspace_v1',knowledgeMode:source.context?'retained':'current',...(source.context?{contextId:source.context.contextId}:{})});
   if(version!==epoch)return;
   if(read.kind!=='directory'||!read.context?.contextId||!Array.isArray(read.books))throw Error('The selected scope directory is inconsistent.');
   directory=read;drawScope();get('risk-workspace-note').textContent=scopeValid()?'No book or portfolio selection means all readable books. Reports retain their own exact context.':'The selected scope is no longer readable. Choose an available scope before running risk.';
  }catch(error){
   if(version!==epoch)return;if((error as {code?:string}).code==='access_denied')throw error;
   directory=undefined;get('risk-workspace-note').textContent=publicText(`Run setup unavailable: ${error instanceof Error?error.message:'Choose a market close.'} Retained reports can still be opened.`);
  }
  if(version!==epoch)return;
  try{const kept=sessionStorage.getItem(storedKey());if(kept&&!engine!.unconfirmed())engine!.adopt(JSON.parse(kept));}catch(error){unreadRecovery=true;get('risk-workspace-note').textContent='A retained submission could not be read. Inspect Reports and runs before submitting another request.';throw error;}
  if(engine!.unconfirmed()){editor.open=true;engine!.mountInto(get('risk-workspace-editor-body'));await engine!.open();return;}
  const address=selectedAddress??riskAddress(location.hash);if(address){await openJob(address);return;}
  if(directory&&selection&&scopeValid()){
   for(const candidate of recentCompletedRuns(jobs.recent(),scope,directory.asOf)){
    const job=await o.api<RiskJob>('portfolio/risk/jobs/'+encodeURIComponent(candidate.id));if(version!==epoch)return;
    if(job.result?.snapshotId!==selection.snapshotId)continue;
    await openJob(job.id,job);return;
   }
   get('risk-workspace-note').textContent='No completed recent report matches these books and this close. Run risk here, or open a retained report from Reports and runs.';
  }
 }
 for(const id of ['risk-workspace-books','risk-workspace-portfolios'])get(id).onchange=()=>void o.act(async()=>{
  const next={bookIds:Array.from(get<HTMLSelectElement>('risk-workspace-books').selectedOptions).map(o=>o.value),portfolioIds:Array.from(get<HTMLSelectElement>('risk-workspace-portfolios').selectedOptions).map(o=>o.value)};
  if(!await leave()){drawScope();return;}scope=next;engine!.inputsChanged(false);results.show(undefined);shown=undefined;selectedJob=undefined;
  window.history.replaceState(null,'',location.pathname+location.search);get('risk-workspace-note').textContent='Scope changed. Run risk for this selection or open a retained report.';
  if(editor.open)await engine!.open();
 });
 get('risk-jobs-new').onclick=()=>void o.act(newRun);
 get('risk-workspace-refresh').onclick=()=>void (async()=>{if(!o.busy()&&await leave())await o.act(load);})();
 get('risk-workspace-export').onclick=()=>void o.act(()=>engine!.download());
 return {load,reset,leave,openJob,refresh:()=>{if(!host.hidden)results.refresh();engine!.refresh();},
  sync(){engine!.sync();get<HTMLButtonElement>('risk-jobs-new').disabled=o.busy()||!o.canRun()||!scopeValid()||unreadRecovery;},
  contextChanged(){epoch++;engine!.inputsChanged(engine!.edited());results.reset();shown=undefined;directory=undefined;selection=undefined;get('risk-workspace-note').textContent='The selected market context changed. Refresh Risk to read the new scope.';},
  edited:()=>engine!.edited(),unconfirmed:()=>engine!.unconfirmed(),
  address:()=>selectedJob?'#risk/job/'+encodeURIComponent(selectedJob):'',
  assistant:()=>selectedJob&&engine!.current()?.retained&&engine!.current()?.parts.some(p=>p.job?.id===selectedJob)?{operation:'portfolio.risk.jobs.id_v1',inputJson:JSON.stringify({body:null,parameters:{id:selectedJob}})}:undefined};
}
