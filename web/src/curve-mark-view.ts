import type {CatalogueSource,FrameSummary} from './market-navigation.js';
import {actorTransport,PendingCommand,type RequestCommand} from './pending-command.js';
import {carryReadReferences} from './read-references.js';
import {correlationScale} from './curve-mark-charts.js';
import {setupMarkGrid} from './curve-mark-grid.js';
import {markDetail,setupMarkInspector} from './curve-mark-inspector.js';
import {
 addressId,currentMarkFence,isUtcInstant,keyId,keyLabel,markFence,markPath,markPolicy,requireCapture,requireHistory,requireReceipt,requireReview,
 requireSetup,requireStatus,requireValidation,restoreMarkCommand,sameInstant,sourceId,
 type Batch,type CellKey,type Draft,type LayoutCell,type MarkChoice,type MarkCommand,type MarkContext,type MarkEdit,type MarkKind,type MarkScope,type MarkView,
 type Receipt,type Review,type Setup,
} from './curve-mark-model.js';

export type MarkClock={
 stamp:string; unavailable:boolean; frame?:FrameSummary;
 retained?:{contextId?:string;effectiveOn:string;knownAt?:string};
};
type Options={
 api:<T>(path:string,body:unknown)=>Promise<T>;actor:()=>string;clock:()=>MarkClock;active:()=>boolean;busy:()=>boolean;
 permission:(name:string)=>boolean;sources:()=>{source:CatalogueSource;label:string}[];changed:()=>void;
};
const transports=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
function transport(actor:string){
 let entry=transports.get(actor);
 if(!entry){entry=actorTransport<RequestCommand>(()=>'curve-mark-publication:'+actor,()=>sessionStorage);transports.set(actor,entry);}
 return entry;
}
function node<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){
 const result=document.createElement(tag);
 if(text!==undefined)result.textContent=text;
 return result;
}
const problem=(error:unknown)=>error instanceof Error?error.message:'The curve mark operation could not be confirmed.';
const refusal=(error:unknown)=>error&&typeof error==='object'&&'code' in error&&typeof error.code==='string'?error.code:undefined;
const writeRefused=(code:string|undefined)=>['request_format','request_token','curve_mark_input','curve_mark_stale','curve_mark_bounds','curve_mark_duplicate',
 'curve_mark_denied','curve_mark_read_only','curve_mark_time','curve_mark_owner_changed','curve_mark_context','curve_mark_source','curve_mark_pair','curve_mark_value'].includes(code??'');
const pageSize=50;
const typeName=(kind:MarkKind)=>kind==='volatility'?'Volatility':'Correlation';
const formatSource=(source:CatalogueSource)=>`Definition ${source.definitionRevisionId} · Reference catalogue ${source.referenceCatalogId} · Reference revision ${source.referenceRevisionId}`;

export function setupCurveMarks(root:HTMLElement,o:Options){
 root.classList.add('curve-marks');
 root.innerHTML=`
 <section class="mark-context" aria-label="Curve mark context">
  <div class="mark-context-summary">
   <div><span>Source</span><strong data-mark="scope-source">Choose a source</strong><small data-mark="scope-revisions" class="mark-source-revisions" hidden></small></div>
   <div><span>Business instant</span><strong data-mark="scope-asof">—</strong></div>
   <div><span>Knowledge cutoff</span><strong data-mark="scope-known">—</strong></div>
   <div><span>Context</span><strong data-mark="scope-mode" class="mark-pill">Working</strong></div>
  </div>
  <details data-mark="clock-panel" class="mark-clock-panel" open><summary>Select mark source &amp; time</summary>
   <form data-mark="clock-form" class="mark-clock-form">
    <label>Exact source<select name="markSource" required></select></label>
    <label>Business date<input name="markDate" type="date" required></label>
    <label>Effective instant (UTC)<input name="markAsOf" type="text" required placeholder="2026-01-01T00:00:00Z" spellcheck="false"></label>
    <button data-mark="frame" type="button" class="secondary">Use selected frame</button>
    <button type="submit">Read curves</button>
   </form>
   <p data-mark="source-detail" class="mark-source-detail"></p>
  </details>
 </section>
 <section data-mark="pending" class="mark-recovery" hidden>
  <div><h3>Original publication</h3><p data-mark="pending-status" role="status">The result is unconfirmed. The exact original command is retained.</p></div>
  <div class="mark-action-row"><button data-mark="status" type="button">Check status</button><button data-mark="retry" type="button" class="secondary">Retry original</button><button data-mark="dismiss" type="button" class="secondary">Dismiss checked request</button></div>
 </section>
 <p data-mark="message" class="mark-message" role="status" hidden></p>
 <section class="mark-workspace" aria-label="Curve mark workspace">
  <div class="mark-toolbar">
   <div class="mark-kind-tabs" role="group" aria-label="Mark type">
    <button type="button" data-mark-kind="volatility" aria-pressed="true"><span aria-hidden="true">σ</span> Volatility</button>
    <button type="button" data-mark-kind="correlation" aria-pressed="false"><span aria-hidden="true">ρ</span> Correlation</button>
   </div>
   <span class="mark-convention" data-mark="convention"></span>
  </div>
  <details data-mark="selector" class="mark-selector" hidden open><summary><span data-mark="selector-summary">Select curves &amp; months</span></summary>
   <div class="mark-scope-editor">
    <form data-mark="search-form" class="mark-curve-search">
     <label>Find curves<input name="markSearch" type="search" autocomplete="off" placeholder="Curve name or code"></label>
     <button type="submit" class="secondary">Find</button>
     <button data-mark="curve-prev" type="button" class="secondary" aria-label="Previous curve page">‹</button>
     <button data-mark="curve-next" type="button" class="secondary" aria-label="Next curve page">›</button>
    </form>
    <div data-mark="curves" class="mark-choices" aria-label="Available curves"></div>
    <div data-mark="chosen" class="mark-chosen" aria-label="Selected curves"></div>
    <form data-mark="view-form" class="mark-view-form">
     <label data-mark="from-label">From month<input name="markFrom" type="month" min="1901-01" max="2199-12" required></label>
     <label data-mark="to-label">Through month<input name="markTo" type="month" min="1901-01" max="2199-12" required></label>
     <button type="submit" data-mark="read-view">Read grid</button>
     <button data-mark="grid-scope" type="button" class="secondary" hidden>Choose grid scope</button>
    </form>
   </div>
  </details>
  <div data-mark="desk" class="mark-desk" hidden>
   <section class="mark-matrix-panel" aria-label="Curve marks grid">
    <div class="mark-grid-header"><div><h3 data-mark="grid-title"></h3><p data-mark="grid-summary" class="mark-muted"></p></div>
     <button type="button" data-mark="inspect" class="secondary mark-inspect-button" disabled>Inspect cell</button>
     <div data-mark="heat-legend" class="mark-heat-legend" aria-label="Correlation heat scale from minus one to plus one" hidden>
      <span>Correlation</span><span class="mark-heat-bar"></span>
      <div class="mark-heat-labels">${correlationScale.labels.map(item=>`<span>${item}</span>`).join('')}</div>
     </div>
    </div>
    <div data-mark="metrics" class="mark-metrics" aria-label="Scope coverage"></div>
    <div data-mark="grid" class="mark-grid-scroll" role="region" aria-label="Scrollable mark cells"></div>
    <p data-mark="grid-hint" class="mark-muted mark-grid-hint"></p>
   </section>
   <aside data-mark="inspector" class="mark-inspector" aria-label="Cell inspector"></aside>
  </div>
  <div data-mark="empty" class="mark-empty"><h3>Choose a mark scope</h3><p>Select an exact source and instant, then choose the curves and delivery months to read.</p></div>
 </section>
 <section data-mark="receipt" class="mark-receipt" aria-label="Publication receipt" hidden></section>
 <div data-mark="draft-bar" class="mark-draft-bar" hidden>
  <div><strong data-mark="draft-total"></strong><span data-mark="draft-progress" aria-live="polite"></span></div>
  <div class="mark-action-row"><button data-mark="discard" type="button" class="secondary">Discard draft</button><button data-mark="review" type="button">Review draft</button></div>
 </div>
 <dialog data-mark="guard" class="mark-dialog" aria-label="Unsaved curve mark draft">
  <h3>Keep this draft?</h3><p>The draft belongs to the displayed source, clock and cells.</p>
  <form method="dialog" class="mark-action-row"><button value="review" class="secondary">Review</button><button value="discard" class="secondary">Discard draft</button><button value="keep" autofocus>Keep editing</button></form>
 </dialog>
 <dialog data-mark="review-dialog" class="mark-dialog mark-review-dialog" aria-labelledby="mark-review-title">
  <div class="mark-dialog-head"><h3 id="mark-review-title">Review mark publication</h3><form method="dialog"><button class="secondary">Close</button></form></div>
  <form data-mark="reason-form" class="mark-reason-form"><label>Publication reason<textarea name="markReason" rows="2" required aria-describedby="mark-reason-hint"></textarea></label>
   <small id="mark-reason-hint">Required · up to 500 characters</small><button type="submit" class="secondary">Validate &amp; review</button></form>
  <p data-mark="review-status" role="status"></p><div data-mark="review-details"></div>
 </dialog>`;
 const el=<T extends HTMLElement=HTMLElement>(name:string)=>root.querySelector<T>(`[data-mark="${name}"]`)!;
 const form=<T extends HTMLElement=HTMLFormElement>(name:string)=>el<T>(name);
 const input=(name:string)=>root.querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
 const button=(name:string)=>el<HTMLButtonElement>(name);
 const sourceField=root.querySelector<HTMLSelectElement>('select[name="markSource"]')!;
 const dateField=input('markDate'),instantField=input('markAsOf'),searchField=input('markSearch'),fromField=input('markFrom'),toField=input('markTo');
 const reasonField=root.querySelector<HTMLTextAreaElement>('textarea[name="markReason"]')!;
 const guard=el<HTMLDialogElement>('guard'),reviewDialog=el<HTMLDialogElement>('review-dialog');
 const draftEdits=new Map<string,{key:CellKey;value:string}>();
 const chosen=new Map<string,MarkChoice>();
 const seenChoices=new Map<string,MarkChoice>();
 const pending=new PendingCommand(writeRefused);
 let actor='',appStamp='',generation=0,draftVersion=0,historySequence=0,sendSequence=0,validationSequence=0;
 let source:CatalogueSource|undefined,context:MarkContext|undefined,setup:Setup|undefined,catalogue:Setup|undefined;
 let setupDraft:Draft|null=null;
 let sourceOptions=new Map<string,{source:CatalogueSource;label:string}>();
 let kind:MarkKind='volatility',selection:LayoutCell|undefined,mode:MarkView['mode']='grid',frameHint:FrameSummary|undefined;
 let modeOfRead:'working'|'historical'='working',reading=false,sending=false,validating=false,reviewing=false,deskWritable=false;
 let query='',offset=0,historyOffset=0,checkedAbsent=false;
 let reviewed:{batch:Batch;version:number;fence:ReturnType<typeof fence>}|undefined;
 let receipt:Receipt|undefined,guarding:Promise<boolean>|undefined;
 const fence=()=>markFence(o.actor(),`${o.clock().stamp}|${context?.contextId??''}`,source?sourceId(source):'',generation);
 const current=(at:ReturnType<typeof fence>)=>o.active()&&appStamp===o.clock().stamp&&currentMarkFence(at,fence());
 const unsaved=()=>!!draftEdits.size||grid.active();
 const edits=()=>new Map([...draftEdits].map(([id,edit])=>[id,edit.value]));
 const scope=():MarkScope=>({context:context!,source:source!});
 function pendingCells(){
  const batch=(pending.command as MarkCommand|undefined)?.body;
  if(!batch||!source||batch.context.contextId!==context?.contextId||sourceId(batch.source)!==sourceId(source)||batch.kind!==setup?.view?.kind)return new Map<string,string>();
  return new Map(batch.changes.map(change=>[keyId(change),change.value]));
 }
 const permission=(name:string)=>o.permission(name);
 const currentApp=()=>appStamp===o.clock().stamp&&!o.clock().unavailable;
 const writable=()=>currentApp()&&permission('ReadMarketData')&&permission('ImportObservations')&&modeOfRead==='working'&&
  !o.clock().retained&&!pending.command&&!reading&&!sending&&!o.busy()&&setup?.canPublish===true;
 const mayEdit=(cell:LayoutCell)=>{
  if(!writable()||!cell.editable||!cell.key)return false;
  const row=setup!.draft!.cells.find(candidate=>keyId(candidate.state.key)===keyId(cell.key!));
  return !!row&&row.state.expectedRevision!==null&&row.state.editBlock===null;
 };
 const grid=setupMarkGrid(el('grid'),{mayEdit,selected:selectCell,edited:editCell,editing:changed});
 const inspector=setupMarkInspector(el('inspector'),{
  reset:()=>{if(selection?.key&&draftEdits.delete(keyId(selection.key)))onDraftChange();},
  edit:()=>{if(selection){grid.focus(selection.address);grid.edit(selection.address);setDrawer(false);}},
  strip:chooseStrip,next:()=>void history(historyOffset+pageSize),previous:()=>void history(Math.max(0,historyOffset-pageSize)),close:()=>closeDrawer(),
 });

 function message(value:string){
  el('message').textContent=value;el('message').hidden=!value;
 }
 function changed(){drawControls();o.changed();}
 function store(){
  transport(actor).write(pending.command);drawDesk();changed();
 }
 function setDrawer(open:boolean){
  el('inspector').toggleAttribute('data-open',open);
  if(open){
   const close=el('inspector').querySelector<HTMLButtonElement>('.mark-drawer-close')!;
   if(close.getClientRects().length)close.focus({preventScroll:true});
   inspector.update(selection,setup?{...setup,curves:[...seenChoices.values()]}:undefined,edits(),!!selection&&mayEdit(selection));
  }
 }
 function closeDrawer(){
  setDrawer(false);
  if(selection)grid.focus(selection.address);
 }
 function resetDraft(){
  grid.cancel();draftEdits.clear();draftVersion++;validating=reviewing=false;reviewed=undefined;reasonField.value='';
  if(reviewDialog.open)reviewDialog.close();
  clearReview();
  if(setup)setup={...setup,draft:setupDraft};
  drawDesk();changed();
 }
 function installSetup(value?:Setup){
  setup=value;
  setupDraft=value?.draft??null;
 }
 function resetScope(){
  generation++;sendSequence++;reading=sending=false;
  grid.cancel();draftEdits.clear();draftVersion++;validating=reviewing=false;reviewed=undefined;reasonField.value='';
  if(reviewDialog.open)reviewDialog.close();if(guard.open)guard.close('keep');clearReview();
  source=undefined;context=undefined;installSetup();catalogue=undefined;selection=undefined;chosen.clear();seenChoices.clear();
  mode='grid';query='';offset=0;searchField.value='';fromField.value='';toField.value='';historyOffset=0;frameHint=undefined;
  sourceField.value='';dateField.value='';instantField.value='';
  el<HTMLDetailsElement>('clock-panel').open=true;el<HTMLDetailsElement>('selector').open=true;
  setDrawer(false);drawChoices();drawChosen();drawDesk();inspector.revisions([],0,false,false);
 }
 function reset(){
  resetScope();sourceOptions.clear();sourceField.replaceChildren();receipt=undefined;pending.clear();checkedAbsent=false;actor='';appStamp='';kind='volatility';
  inspector.releaseChart();drawReceipt();message('');
 }
 function sync(){
  if(actor!==o.actor()){
   reset();actor=o.actor();appStamp=o.clock().stamp;
   pending.command=restoreMarkCommand(transport(actor).read());
  }
  if(!o.active())return;
  const stamp=o.clock().stamp;
  if(appStamp!==stamp&&!unsaved()){
   resetScope();appStamp=stamp;receipt=undefined;drawReceipt();
   message('');
  }
  drawClocks();
  if(writable()!==deskWritable)drawDesk();
  drawControls();
 }
 function drawClocks(){
  const clock=o.clock(),options=new Map(o.sources().map(option=>[sourceId(option.source),option]));
  const selectedSource=sourceField.value;
  if(JSON.stringify([...options])!==JSON.stringify([...sourceOptions])){
   sourceOptions=options;
   const placeholder=node('option','Choose an exact source');placeholder.value='';
   sourceField.replaceChildren(placeholder,...[...options].map(([id,option])=>{
    const item=node('option',option.label);item.value=id;item.title=formatSource(option.source);return item;
   }));
   sourceField.value=options.has(selectedSource)?selectedSource:'';
  }
  const picked=sourceOptions.get(sourceField.value)?.source;
  el('source-detail').textContent=picked?formatSource(picked):'';
  dateField.readOnly=!!clock.retained;
  if(clock.retained&&!context)dateField.value=clock.retained.effectiveOn;
  const summary=source?sourceOptions.get(sourceId(source))?.label??'Exact saved source':'Choose a source';
  el('scope-source').textContent=summary;el('scope-source').title=source?`${summary} · ${formatSource(source)}`:'';
  const revisions=el('scope-revisions');
  revisions.hidden=!source;
  revisions.textContent=source?`Def …${source.definitionRevisionId.slice(-8)} · Ref …${source.referenceRevisionId.slice(-8)}`:'';
  revisions.title=source?formatSource(source):'';
  el('scope-asof').textContent=context?.asOf??'—';
  el('scope-known').textContent=catalogue?.context.knownAt??'—';
  el('scope-mode').textContent=clock.unavailable?'Apply app context':setup?.context.mode==='historical'||clock.retained?'Historical':'Working';
  el('scope-mode').dataset.mode=clock.retained?'historical':'working';
 }
 function drawControls(){
  const clock=o.clock(),blocked=o.busy()||reading||sending||!!pending.command;
  const disabledRead=blocked||!currentApp()||!permission('ReadMarketData');
  for(const element of form('clock-form').querySelectorAll<HTMLInputElement|HTMLButtonElement|HTMLSelectElement>('input,button,select'))element.disabled=disabledRead;
  button('frame').disabled=disabledRead||!clock.frame||!!clock.retained&&clock.frame.businessDate!==clock.retained.effectiveOn;
  el('convention').textContent=catalogue?.convention??'';
  el('selector').hidden=!catalogue;
  el('desk').hidden=!setup;
  button('inspect').disabled=!selection;
  el('empty').hidden=!!setup;
  for(const element of root.querySelectorAll<HTMLButtonElement>('[data-mark-kind]')){
   element.setAttribute('aria-pressed',String(element.dataset.markKind===kind));element.disabled=blocked;
  }
  for(const element of el('selector').querySelectorAll<HTMLInputElement|HTMLButtonElement>('input,button'))element.disabled=disabledRead;
  button('curve-prev').disabled=disabledRead||offset===0;
  button('curve-next').disabled=disabledRead||!catalogue?.hasMoreCurves;
  button('read-view').disabled=disabledRead||!chosen.size;
  const busyDraft=sending||reading||o.busy()||!!pending.command;
  el('draft-bar').hidden=!unsaved()&&!pending.command;
  el('draft-total').textContent=pending.command?'Publication retained':`${draftEdits.size} draft ${draftEdits.size===1?'cell':'cells'}`;
  el('draft-progress').textContent=pending.command?'Check the original outcome':validating?'Validating changes…':draftEdits.size?'Review exact changes before publishing':'Editing a cell';
  button('discard').disabled=busyDraft;button('review').disabled=!writable()||!unsaved();
  el('pending').hidden=!pending.command;
  button('status').disabled=!pending.command||sending||o.busy()||!permission('ReadMarketData');
  button('retry').disabled=!pending.command||sending||o.busy()||!currentApp()||!!clock.retained||!permission('ReadMarketData')||!permission('ImportObservations');
  button('dismiss').disabled=!pending.command||!checkedAbsent||sending;
  if(!pending.command)el('pending-status').textContent='The result is unconfirmed. The exact original command is retained.';
  form('reason-form').querySelector<HTMLButtonElement>('button')!.disabled=!writable()||reviewing;
  const publish=el<HTMLButtonElement>('publish');
  if(publish)publish.disabled=!writable()||!reviewed||reviewing||validating||!current(reviewed.fence)||reviewed.version!==draftVersion;
 }
 function drawChoices(){
  const container=el('curves');container.replaceChildren();
  for(const choice of catalogue?.curves??[]){
   const item=node('label'),check=node('input'),label=node('span',choice.name),hint=node('small',`${choice.code} · ${choice.currency}`);
   check.type='checkbox';check.value=choice.code;check.checked=chosen.has(choice.code);
   item.title=`${choice.name} · ${choice.code} · ${choice.currency}`;
   item.append(check,label,hint);container.append(item);
   check.onchange=()=>{
    if(check.checked)chosen.set(choice.code,choice);else chosen.delete(choice.code);
    drawChosen();drawControls();
   };
  }
  if(!container.children.length)container.append(node('p','No eligible curves match this search.'));
 }
 function drawChosen(){
  const container=el('chosen');container.replaceChildren();
  const count=chosen.size;
  const label=node('span',`${count} selected`);label.className='mark-muted';container.append(label);
  for(const choice of chosen.values()){
   const chip=node('button',`${choice.name} ×`);chip.type='button';chip.className='secondary mark-chip';
   chip.title=`${choice.name} · ${choice.code}`;chip.setAttribute('aria-label',`Remove ${choice.name}`);
   chip.onclick=()=>{chosen.delete(choice.code);drawChosen();drawChoices();drawControls();};container.append(chip);
  }
  const matrix=kind==='correlation'&&mode==='grid';
  el('to-label').hidden=matrix;toField.required=!matrix;
  el('from-label').firstChild!.textContent=matrix?'Delivery month':'From month';
  button('grid-scope').hidden=mode!=='strip';
  button('read-view').textContent=mode==='strip'?'Read strip':'Read grid';
  const limit=catalogue?.limits;
  el('selector-summary').textContent=mode==='strip'?`Select strip months · up to ${limit?.stripMonths??240}`:
   matrix?`Select curves & month · up to ${limit?.gridCurves??20} curves`:
   `Select curves & months · up to ${limit?.gridCurves??20} curves × ${limit?.volatilityGridMonths??12} months`;
 }
 function drawMetrics(){
  const host=el('metrics');host.replaceChildren();
  if(!setup?.draft)return;
  const counts=setup.draft.coverage;
  const awaitingDraft=validating||setup.draft.cells.some(cell=>(draftEdits.get(keyId(cell.state.key))?.value??null)!==cell.enteredValue);
  for(const [key,label] of [['marked','Marked'],['missing','Missing'],['changed','Changed'],['invalid','Invalid']] as const){
   const card=node('div');card.dataset.metric=key;
   const value=node('strong',awaitingDraft&&(key==='changed'||key==='invalid')?'—':String(counts[key]));
   card.append(node('span',label),value);host.append(card);
  }
 }
 function drawDesk(preserve=true){
  deskWritable=writable();
  const view=setup?.view;el('desk').hidden=!view;el('empty').hidden=!!view;
  const displaySetup=setup?{...setup,curves:[...seenChoices.values()]}:undefined;
  if(view&&setup?.layout){
   const strip=view.mode==='strip',matrix=view.kind==='correlation'&&!strip;
   el('grid-title').textContent=strip?`${typeName(view.kind)} strip`:matrix?'Correlation matrix':'Volatility grid';
   el('grid-summary').textContent=strip?`${setup.layout.rowCount} months · ${view.fromMonth} through ${view.toMonth}`:
    matrix?`${view.curves.length} curves · ${view.fromMonth}`:`${view.curves.length} curves × ${setup.layout.columnCount} months · Annualized decimal fraction`;
   el('heat-legend').hidden=!matrix;
   el('grid-hint').textContent=matrix?'Upper triangle edits · Lower triangle mirrors · Enter or double-click to edit':
    view.kind==='volatility'?'0.50 = 50% annualized · Enter or double-click to edit':'Enter or double-click to edit';
  }
  drawMetrics();grid.draw(displaySetup,edits(),pendingCells(),preserve);
  inspector.update(selection,displaySetup,edits(),!!selection&&mayEdit(selection));
 }
 function selectCell(cell:LayoutCell){
  if(!cell.key)return;
  const before=selection?.key;
  if(selection&&addressId(selection.address)===addressId(cell.address))return;
  selection=cell;button('inspect').disabled=false;
  inspector.update(cell,setup?{...setup,curves:[...seenChoices.values()]}:undefined,edits(),mayEdit(cell));
  if(!before||keyId(before)!==keyId(cell.key))void history(0);
 }
 async function history(from:number){
  const key=selection?.key,view=setup?.view;if(!key||!view||!context||!source)return;
  const at=fence(),serial=++historySequence,requested={...scope(),kind:view.kind,key,offset:from,limit:pageSize};
  historyOffset=from;inspector.revisions([],from,false,true);
  try{
   const reply=await o.api<unknown>(markPath+'history',requested);
   if(!current(at)||serial!==historySequence||!setup)return;
   const value=requireHistory(reply,requested,view.kind,key,setup.context);
   inspector.revisions(value.revisions,from,value.hasMore,false);
  }catch(error){if(current(at)&&serial===historySequence)inspector.revisions([],from,false,false,problem(error));}
 }
 function action(work:()=>Promise<void>){
  const selectedActor=o.actor();void work().catch(error=>{if(o.active()&&selectedActor===o.actor())message(problem(error));});
 }
 async function capture(businessDate:string,asOf:string,snapshotId:string|null){
  const retained=o.clock().retained;
  if(retained){
   if(retained.effectiveOn!==businessDate||!retained.contextId||!isUtcInstant(retained.knownAt))throw Error('Select an exact historical business date and knowledge cutoff in the app.');
   return {context:carryReadReferences({contextId:retained.contextId,asOf,businessDate,snapshotId},retained),knownAt:retained.knownAt};
  }
  const value=requireCapture(await o.api('date-schedules/context',{effectiveOn:businessDate,mode:'current'}),businessDate);
  return {context:carryReadReferences({contextId:value.contextId,asOf,businessDate,snapshotId},value),knownAt:value.knownAt};
 }
 async function getSetup(args:MarkScope,view:MarkView|null,search:string,start:number,mode:typeof modeOfRead){
  const request={...args,view,search,offset:start,limit:pageSize};
  return requireSetup(await o.api(markPath+'setup',request),request,mode);
 }
 function applyCatalogue(value:Setup){
  catalogue=value;for(const item of value.curves)seenChoices.set(item.code,item);drawChoices();drawChosen();drawClocks();
 }
 async function chooseSource(){
  const option=sourceOptions.get(sourceField.value),businessDate=dateField.value,asOf=instantField.value.trim();
  if(!option||!businessDate||!isUtcInstant(asOf))throw Error('Select an exact source, business date and UTC effective instant.');
  if(!await leave())return;
  const snapshotId=frameHint&&frameHint.businessDate===businessDate&&sameInstant(frameHint.at,asOf)?frameHint.id:null;
  const at=fence(),newMode=o.clock().retained?'historical':'working';
  let requestFence=at;
  reading=true;drawControls();
  try{
   const {context:next,knownAt}=await capture(businessDate,asOf,snapshotId);
   if(!current(at))return;
   const value=await getSetup({context:next,source:option.source},null,'',0,newMode);
   if(!current(at))return;
   if(!sameInstant(value.context.knownAt,knownAt))throw Error('The source read did not match the captured knowledge cutoff.');
   generation++;source=option.source;context=next;modeOfRead=newMode;
   requestFence=fence();
   installSetup();selection=undefined;chosen.clear();seenChoices.clear();mode='grid';query='';offset=0;
   receipt=undefined;drawReceipt();
   searchField.value='';fromField.value='';toField.value='';setDrawer(false);
   applyCatalogue(value);drawDesk(false);
   el<HTMLDetailsElement>('clock-panel').open=false;
   el<HTMLDetailsElement>('selector').open=true;message('');
  }catch(error){if(current(at))message(problem(error));}
  finally{if(current(requestFence)){reading=false;drawControls();}}
 }
 async function search(search:string,from:number){
  if(!catalogue||!context||!source)return;
  const at=fence();reading=true;drawControls();
  try{
   const value=await getSetup(scope(),null,search,from,modeOfRead);
   if(!current(at))return;
   if(value.ownerId!==catalogue.ownerId||!sameInstant(value.context.knownAt,catalogue.context.knownAt))throw Error('The catalogue context changed. Read the exact source again.');
   query=search;offset=from;applyCatalogue(value);message('');
  }catch(error){if(current(at))message(problem(error));}
  finally{if(current(at)){reading=false;drawControls();}}
 }
 async function readView(){
  if(!catalogue||!context||!source||!await leave())return;
  const matrix=kind==='correlation'&&mode==='grid';
  const view:MarkView={kind,mode,curves:[...chosen.keys()],fromMonth:fromField.value,toMonth:matrix?fromField.value:toField.value};
  const at=fence();let requestFence=at;reading=true;drawControls();
  try{
   const value=await getSetup(scope(),view,query,offset,modeOfRead);
   if(!current(at))return;
   if(value.ownerId!==catalogue.ownerId||!sameInstant(value.context.knownAt,catalogue.context.knownAt))throw Error('The view context changed. Read the exact source again.');
   generation++;requestFence=fence();installSetup(value);selection=undefined;setDrawer(false);drawDesk(false);
   el<HTMLDetailsElement>('selector').open=false;message('');
  }catch(error){if(current(at))message(problem(error));}
  finally{if(current(requestFence)){reading=false;drawDesk();drawControls();}}
 }
 function chooseStrip(){
  if(!selection?.key||!setup?.view)return;
  const {firstCurve,secondCurve}=selection.key;
  mode='strip';chosen.clear();
  for(const code of [firstCurve,secondCurve])if(code)chosen.set(code,seenChoices.get(code)??{code,name:code,currency:''});
  fromField.value=setup.view.fromMonth;toField.value=setup.view.toMonth;
  el<HTMLDetailsElement>('selector').open=true;
  setDrawer(false);drawChoices();drawChosen();drawControls();fromField.focus();
 }
 function editCell(key:CellKey,value:string){
  draftEdits.set(keyId(key),{key,value});
  onDraftChange();
 }
 function onDraftChange(){
  draftVersion++;reviewed=undefined;clearReview();
  if(!draftEdits.size){
   validating=false;
   if(setup)setup={...setup,draft:setupDraft};
  }
  drawDesk();changed();
  if(draftEdits.size)void validateDraft();
 }
 async function validateDraft():Promise<Draft|undefined>{
  if(!context||!source||!setup?.view||!setup.layout||!draftEdits.size)return;
  const at=fence(),version=draftVersion,serial=++validationSequence,view=setup.view,layout=setup.layout,clock=setup.context;
  const snapshot:MarkEdit[]=[...draftEdits.values()];
  validating=true;drawMetrics();changed();
  const stillCurrent=()=>current(at)&&version===draftVersion&&serial===validationSequence;
  try{
   const request={...scope(),view,edits:snapshot};
   const answer=await o.api(markPath+'validate',request);
   if(!stillCurrent()||!setup)return;
   const value=requireValidation(answer,request,view,layout,clock);
   const submitted=new Map(snapshot.map(edit=>[keyId(edit.key),edit.value]));
   if(value.addressed.length||value.draft.cells.some(cell=>(submitted.get(keyId(cell.state.key))??null)!==cell.enteredValue))
    throw Error('The validated draft did not match the submitted edits.');
   setup={...setup,draft:value.draft};message('');return value.draft;
  }catch(error){if(stillCurrent())message(problem(error));}
  finally{if(stillCurrent()){validating=false;drawDesk();changed();}}
 }
 function clearReview(){
  el('review-details').replaceChildren();el('review-status').textContent='';
 }
 function showReview(){
  grid.commit();
  if(pending.command){
   el('pending').scrollIntoView({block:'nearest'});
   button('status').focus({preventScroll:true});
   return;
  }
  if(!draftEdits.size||!writable())return;
  if(!reviewDialog.open)reviewDialog.showModal();
  if(!reviewed)reasonField.focus();
 }
 function markRows(headings:string[],rows:string[][],label:string){
  const table=node('table');table.className='mark-evidence-table';table.setAttribute('aria-label',label);
  const head=table.createTHead().insertRow();
  for(const heading of headings){const th=node('th',heading);th.scope='col';head.append(th);}
  const body=table.createTBody();for(const row of rows){const line=body.insertRow();for(const [index,value] of row.entries()){const cell=line.insertCell();cell.textContent=value;cell.dataset.markColumn=headings[index];}}
  return table;
 }
 function drawReview(value:Review,batch:Batch){
  const host=el('review-details');host.replaceChildren();
  const note=node('p',`Effective ${batch.context.asOf} · ${value.changes.length} reviewed ${value.changes.length===1?'cell':'cells'}`);
  note.className='mark-note';host.append(note);
  const validated=new Map(setup?.draft?.cells.map(cell=>[keyId(cell.state.key),cell]));
  const rows=value.changes.map(change=>{
   const head=change.headAfterReview;
   const draft=validated.get(keyId(change.key));
   return [keyLabel(change.key),change.key.month,change.selectedValue??'Missing',change.proposedValue,draft?.isNewMark?'New mark':draft?.difference??'—',
    String(change.expectedRevision),change.wouldChange?'Record revision':`Acknowledge revision ${change.acknowledgedRevision}`,
    head?`Revision ${head.revision} · ${head.value} · ${head.effectiveAt}`:'—'];
  });
  host.append(markRows(['Curve / pair','Month','Selected','Proposed','Draft − selected','Expected head','Outcome','Head after review'],rows,'Reviewed publication changes'));
  if(value.alreadyRecorded){
   const note=node('p','This operation was already recorded. Check the original publication status for its accepted facts and heads at publication.');
   host.append(note);
  }
  const publish=node('button',value.alreadyRecorded?'Check original status':'Publish reviewed marks');
  publish.type='button';publish.dataset.mark='publish';publish.className='mark-publish';host.append(publish);
  publish.onclick=()=>action(async()=>{
   if(!reviewed||reviewed.version!==draftVersion||!current(reviewed.fence)||!writable()||reviewed.batch!==batch)return;
   const command=pending.begin(markPath+'record',batch,()=>batch.operationId) as MarkCommand;
   if(value.alreadyRecorded){store();reviewDialog.close();await reconcile(false);}
   else await send(command);
  });
 }
 async function review(){
  const reason=reasonField.value.trim();
  if(!reason){el('review-status').textContent='Enter a publication reason.';reasonField.focus();return;}
  const at=fence(),version=draftVersion;
  const currentReview=()=>current(at)&&version===draftVersion&&reason===reasonField.value.trim();
  reviewed=undefined;reviewing=true;clearReview();el('review-status').textContent='Validating draft…';drawControls();
  try{
   const draft=await validateDraft();
   if(!currentReview())return;
   if(!draft||draft.coverage.invalid){el('review-status').textContent='Resolve invalid or unconfirmed cells before review.';return;}
   if(!writable()||!setup?.view||!context||!source)return;
   const changed=draft.cells.filter(cell=>cell.enteredValue!==null);
   if(changed.length>setup.limits.batchCells)throw Error(`A publication holds at most ${setup.limits.batchCells} cells.`);
   const batch:Batch={...scope(),policy:markPolicy,ownerId:setup.ownerId,operationId:crypto.randomUUID(),kind:setup.view.kind,reason,
    changes:changed.map(cell=>({...cell.state.key,expectedRevision:cell.state.expectedRevision!,value:cell.proposedValue!}))};
   el('review-status').textContent='Checking exact publication predecessors…';
   const value=requireReview(await o.api(markPath+'review',batch),batch);
   if(!currentReview())return;
   reviewed={batch,version,fence:at};el('review-status').textContent='Review ready';drawReview(value,batch);
  }catch(error){if(currentReview())el('review-status').textContent=problem(error);}
  finally{if(current(at)){reviewing=false;drawControls();}}
 }
 function drawReceipt(){
  const host=el('receipt');host.replaceChildren();host.hidden=!receipt;
  if(!receipt)return;
  const label=node('h3','Publication receipt');
  const note=node('p',`Effective ${receipt.effectiveAt} · ${typeName(receipt.kind)} · ${receipt.reason} · Operation ${receipt.operationId}`);
  note.className='mark-note';note.title=formatSource(receipt.source);
  host.append(label,note,markRows(['Curve / pair','Month','Accepted fact','Accepted value','Head at publication'],
   receipt.cells.map(cell=>[keyLabel(cell.key),cell.key.month,`${cell.changed?'Recorded':'Acknowledged'} revision ${cell.revision}\n${cell.markId}`,cell.value,
    `Revision ${cell.headAtPublication.revision} · ${cell.headAtPublication.value} · ${cell.headAtPublication.effectiveAt}\n${cell.headAtPublication.markId}`]),'Accepted facts and heads at publication'));
 }
 async function accept(value:Receipt,command:MarkCommand,at:ReturnType<typeof fence>){
  receipt=value;pending.clear();checkedAbsent=false;resetDraft();store();drawReceipt();
  const original=command.body,view=setup?.view;
  if(o.clock().retained||!view||!source||!context||sourceId(source)!==sourceId(original.source)||context.contextId!==original.context.contextId){
   message('Original publication confirmed. Read an exact scope to see current marks.');return;
  }
  message('Publication confirmed. Refreshing mark knowledge for this source and instant…');
  try{
   const {context:next,knownAt}=await capture(original.context.businessDate,original.context.asOf,original.context.snapshotId??null);
   if(!current(at))return;
   const refreshed=await getSetup({source:original.source,context:next},view,query,offset,'working');
   if(!current(at))return;
   if(refreshed.ownerId!==original.ownerId||!sameInstant(refreshed.context.knownAt,knownAt))throw Error('The refresh did not match the publication owner and captured knowledge cutoff.');
   generation++;context=next;installSetup(refreshed);applyCatalogue(refreshed);drawDesk();
   if(selection)void history(0);
   message('Publication confirmed. Marks refreshed at current knowledge. Financial inputs refresh separately.');
  }catch(error){if(current(at))message('Publication confirmed. Mark refresh failed. '+problem(error));}
 }
 async function send(command:MarkCommand){
  const at=fence(),serial=++sendSequence,previouslyUncertain=pending.sending();
  sending=true;checkedAbsent=false;store();
  try{
   const result=requireReceipt(await o.api(command.path,command.body),command.body);
   if(current(at)&&pending.command===command)await accept(result,command,at);
  }catch(error){
   if(!current(at)||pending.command!==command)return;
   pending.failed(refusal(error),previouslyUncertain);store();
   reviewed=undefined;clearReview();el('review-status').textContent=problem(error);
   if(pending.command){if(reviewDialog.open)reviewDialog.close();el('pending-status').textContent='The original result is unconfirmed. '+problem(error);}
   else message(problem(error));
  }finally{if(serial===sendSequence&&o.active()){sending=false;drawDesk();changed();}}
 }
 async function reconcile(retry:boolean){
  const command=pending.command as MarkCommand|undefined;
  if(!command)return;
  if(retry){await send(command);return;}
  const at=fence(),serial=++sendSequence,{ownerId,operationId,kind}=command.body;
  sending=true;checkedAbsent=false;drawControls();
  try{
   const result=requireStatus(await o.api(markPath+'status',{ownerId,operationId,kind}),command.body);
   if(!current(at)||pending.command!==command)return;
   if(result)await accept(result,command,at);
   else{
    checkedAbsent=true;
    el('pending-status').textContent='The server found no committed original publication. Retry the exact command or dismiss this checked request.';
   }
  }catch(error){if(current(at))el('pending-status').textContent='The original outcome remains unconfirmed. '+problem(error);}
  finally{if(serial===sendSequence&&o.active()){sending=false;drawDesk();changed();}}
 }
 async function leave(){
  grid.commit();
  if(!unsaved())return true;
  if(guarding)return guarding;
  guarding=new Promise<boolean>(resolve=>{
   guard.returnValue='keep';guard.showModal();
   guard.addEventListener('close',()=>{
    const choice=guard.returnValue;
    if(choice==='discard'){resetDraft();resolve(true);}
    else{if(choice==='review')showReview();resolve(false);}
   },{once:true});
  }).finally(()=>{guarding=undefined;});
  return guarding;
 }

 sourceField.onchange=drawClocks;
 button('frame').onclick=()=>{
  const frame=o.clock().frame;if(!frame)return;
  dateField.value=frame.businessDate;instantField.value=frame.at;frameHint=frame;
 };
 form('clock-form').onsubmit=event=>{event.preventDefault();action(chooseSource);};
 form('search-form').onsubmit=event=>{event.preventDefault();action(()=>search(searchField.value.trim(),0));};
 button('curve-prev').onclick=()=>action(()=>search(query,Math.max(0,offset-pageSize)));
 button('curve-next').onclick=()=>action(()=>search(query,offset+pageSize));
 form('view-form').onsubmit=event=>{event.preventDefault();action(readView);};
 button('grid-scope').onclick=()=>{mode='grid';drawChosen();drawControls();};
 for(const item of root.querySelectorAll<HTMLButtonElement>('[data-mark-kind]'))item.onclick=()=>action(async()=>{
  const next=item.dataset.markKind as MarkKind;
  if(next===kind||!await leave())return;
  generation++;kind=next;mode='grid';installSetup();selection=undefined;setDrawer(false);
  drawChosen();drawDesk(false);drawControls();el<HTMLDetailsElement>('selector').open=true;
 });
 button('inspect').onclick=()=>setDrawer(true);
 el('inspector').onkeydown=event=>{if(event.key==='Escape'&&el('inspector').hasAttribute('data-open')){event.stopPropagation();closeDrawer();}};
 button('discard').onclick=()=>{resetDraft();message('Draft discarded.');};
 button('review').onclick=showReview;
 reasonField.oninput=()=>{reviewed=undefined;clearReview();drawControls();};
 form('reason-form').onsubmit=event=>{event.preventDefault();action(review);};
 button('status').onclick=()=>action(()=>reconcile(false));
 button('retry').onclick=()=>action(()=>reconcile(true));
 button('dismiss').onclick=()=>{if(checkedAbsent){pending.clear();checkedAbsent=false;reviewed=undefined;store();message('The checked original request was dismissed.');drawDesk();}};
 return {sync,load:()=>{sync();drawDesk();drawControls();},leave,unsaved,pending:()=>!!pending.command,reset,
  deactivate:()=>{generation++;sendSequence++;reading=sending=validating=reviewing=false;inspector.releaseChart();setDrawer(false);}};
}
