import {carryReadReferences,cloneRead} from './read-references.js';
import {mountOperationFields} from './operation-fields.js';
import {actorTransport,PendingCommand} from './pending-command.js';
import {overviewTermSections,renderPhysicalSections} from './physical-instrument.js';
import type {CatalogueSource,FrameSummary} from './market-navigation.js';
import {askInPanel} from './side-panel.js';
import {zonedInput} from './user-time.js';
import {pointEditedSelection,pointPublicationRejection,requirePointInputs,requirePointReceipt,requirePointSetup,restorePointRequest,reviewedPointRequest,
 type AvailablePointSelection,type PointContext,type PointPending,type PointQuoteFilter,type PointReply,type PointRequest,type PointSetup} from './point-trader-mark-model.js';

export type PointValueSelection={stamp:string;source:CatalogueSource;curveCode?:string;contractId?:string;label?:string;frame?:FrameSummary|null;quoteFilter?:PointQuoteFilter|null;eligibility?:string|null};
export type PointTraderMarkOptions={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;
 identity:()=>string;zone:()=>string;permission:(name:string)=>boolean;historical:()=>boolean;selection:()=>PointValueSelection|undefined;changed:()=>void;
 applyFrame:(frame:FrameSummary,current:()=>boolean)=>Promise<void>};
const transports=new Map<string,ReturnType<typeof actorTransport<PointPending>>>();
function transport(actor:string){let found=transports.get(actor);if(!found){found=actorTransport<PointPending>(()=>'point-trader-mark-request:'+actor,()=>sessionStorage);transports.set(actor,found);}return found;}
const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const element=document.createElement(tag);if(text!==undefined)element.textContent=text;return element;};

/** One ordinary market publisher. Exact sources, prices and revision identities
 * are supplied and validated by the backend; input application is separate. */
export function setupPointTraderMark(root:HTMLElement,o:PointTraderMarkOptions){
 root.tabIndex=-1;root.classList.add('price-authoring');
 root.innerHTML=`<h3>Record market value</h3><p data-point-value="source" class="field-help"></p>
 <p class="field-help">Publish a final source value. Financial mark adoption is a separate reviewed operation.</p>
 <div class="side-panel-prompt" role="alertdialog" aria-label="Retained market request" hidden></div>
 <section data-point-value="pending" class="operations-recovery" hidden><p data-point-value="pending-text" role="status"></p><button data-point-value="status" type="button">Check original publication status</button><button data-point-value="retry" type="button" class="secondary">Retry original publication</button><button data-point-value="dismiss" type="button" class="secondary" disabled>Dismiss checked request</button></section>
 <p data-point-value="message" role="status"></p>
 <form data-point-value="read-form" class="lifecycle-form"><fieldset class="schedule-fields">
 <label>Business date<input name="businessDate" type="date" required></label><label>Value time<input name="valueAt" type="datetime-local" step="1" required><small data-point-value="zone"></small></label>
 <label>Knowledge<select name="knowledgeMode" required><option value="">Choose…</option><option value="current">Current knowledge</option><option value="at">Exact knowledge cutoff</option></select></label>
 <label data-point-value="known-field" hidden>As known at<input name="knownAt" type="datetime-local" step="1" disabled><small data-point-value="known-zone"></small></label>
 </fieldset><button type="submit">Read eligible points</button><button data-point-value="refresh" type="button" class="secondary">Refresh point choices</button></form>
 <form data-point-value="search-form" class="lifecycle-form" hidden><label>Find a point<input name="search" type="search" autocomplete="off"></label><button type="submit" class="secondary">Find points</button><button data-point-value="previous" type="button" class="secondary">Previous page</button><button data-point-value="next" type="button" class="secondary">Next page</button><p data-point-value="search-hint" class="field-help"></p></form>
 <section data-point-value="unavailable"></section><form data-point-value="entry" class="lifecycle-form" hidden></form><section data-point-value="review-host"></section><section data-point-value="receipt" hidden></section>`;
 root.querySelector<HTMLElement>('.side-panel-prompt')!.id=root.id+'-prompt';
 const get=<T extends HTMLElement>(name:string)=>root.querySelector<T>(`[data-point-value="${name}"]`)!;
 const field=<T extends HTMLInputElement|HTMLSelectElement>(name:string)=>root.querySelector<T>(`[name="${name}"]`)!;
 const readForm=get<HTMLFormElement>('read-form'),searchForm=get<HTMLFormElement>('search-form'),entry=get<HTMLFormElement>('entry');
 const atInput=field<HTMLInputElement>('valueAt'),knownInput=field<HTMLInputElement>('knownAt'),mode=field<HTMLSelectElement>('knowledgeMode');
 const atClock=zonedInput(atInput,o.zone,o.api),knownClock=zonedInput(knownInput,o.zone,o.api);
 const pending=new PendingCommand(pointPublicationRejection);
 let actor='',generation=0,draftVersion=0,lastScope='',dirty=false,entryDirty=false,criteriaDirty=false,reading=false,saving=false,query='',offset=0;
 let context:PointContext|undefined,setup:PointSetup|undefined,editor:ReturnType<typeof mountOperationFields>|undefined;
 let reviewed:{request:PointRequest;reply:PointReply;serial:number;stamp:string}|undefined;
 let receipt:{request:PointRequest;reply:PointReply;stamp?:string}|undefined,offer:AvailablePointSelection|undefined;
 let checkedAbsent:{command:PointPending;actor:string}|undefined,abandoned=new AbortController();
 const stamp=()=>{const selected=o.selection();return JSON.stringify(selected?{stamp:selected.stamp,source:selected.source,curveCode:selected.curveCode??null,contractId:selected.contractId??null,
  frame:selected.frame?{id:selected.frame.id,at:selected.frame.at,businessDate:selected.frame.businessDate}:null,quoteFilter:selected.quoteFilter??null,eligibility:selected.eligibility??null,historical:o.historical()}:null);};
 const fence=()=>({actor:o.identity(),generation,stamp:stamp()});
 const current=(value:ReturnType<typeof fence>)=>value.actor===o.identity()&&value.generation===generation&&value.stamp===stamp();
 const blocked=()=>o.busy()||reading||saving||!!pending.command;
 const writeAdmitted=()=>!criteriaDirty&&!reading&&!saving&&!pending.command&&!o.historical()&&o.permission('ReadMarketData')&&o.permission('ImportObservations')&&setup?.canPublish===true;
 const canWrite=()=>!o.busy()&&writeAdmitted();
 const canDismiss=()=>!!pending.command&&checkedAbsent?.command===pending.command&&checkedAbsent.actor===o.identity();
 function store(){transport(actor).write(pending.command as PointPending|undefined);o.changed();}
 function invalidate(){draftVersion++;reviewed=undefined;get('review-host').replaceChildren();}
 function clearSetup(){setup=undefined;editor=undefined;entry.hidden=true;entry.replaceChildren();get('unavailable').replaceChildren();invalidate();}
 function prefill(){readForm.reset();knownClock.clear();atClock.clear();const frame=o.selection()?.frame;if(frame){field<HTMLInputElement>('businessDate').value=frame.businessDate;atClock.set(frame.at);}knownVisibility();}
 function restore(){
  if(actor===o.identity())return;
  abandoned.abort();abandoned=new AbortController();actor=o.identity();generation++;reading=saving=false;dirty=entryDirty=criteriaDirty=false;context=undefined;receipt=undefined;offer=undefined;checkedAbsent=undefined;query='';offset=0;clearSetup();
  const saved=restorePointRequest(transport(actor).read());pending.command=saved?{...saved,uncertain:true}:undefined;lastScope=stamp();prefill();get('receipt').hidden=true;
 }
 function scopeChanged(){const now=stamp();if(lastScope===now)return;generation++;reading=false;lastScope=now;context=undefined;dirty=entryDirty=criteriaDirty=false;query='';offset=0;clearSetup();prefill();}
 function knownVisibility(){const show=mode.value==='at';get('known-field').hidden=!show;knownInput.disabled=!show;knownInput.required=show;}
 function sync(){
  restore();scopeChanged();const selected=o.selection();get('source').textContent=selected?`${selected.label??'Selected curve'} · exact published source`:'Choose a curve to inspect or publish a market value.';
  get('zone').textContent=get('known-zone').textContent=o.zone();
  get('pending').hidden=!pending.command;get('pending-text').textContent=pending.command?'The publication result is unconfirmed. The original request is retained; check its status or retry it unchanged.':'';
  get<HTMLButtonElement>('status').disabled=o.busy()||saving||!pending.command||!o.permission('ReadMarketData');
  get<HTMLButtonElement>('retry').disabled=o.busy()||saving||!pending.command||!o.permission('ReadMarketData')||!o.permission('ImportObservations')||o.historical();
  get<HTMLButtonElement>('dismiss').disabled=o.busy()||saving||!canDismiss();
  readForm.querySelector<HTMLFieldSetElement>('fieldset')!.disabled=blocked()||!selected||!o.permission('ReadMarketData');
  for(const button of readForm.querySelectorAll<HTMLButtonElement>('button'))button.disabled=blocked()||!selected||!o.permission('ReadMarketData');
  searchForm.hidden=!context;for(const control of searchForm.querySelectorAll<HTMLInputElement|HTMLButtonElement>('input,button'))control.disabled=blocked()||criteriaDirty||!o.permission('ReadMarketData');
  get<HTMLButtonElement>('previous').disabled=blocked()||criteriaDirty||!setup||setup.page.offset===0;get<HTMLButtonElement>('next').disabled=blocked()||criteriaDirty||!setup?.page.hasMore;
  entry.querySelector<HTMLFieldSetElement>('fieldset')?.toggleAttribute('disabled',!canWrite());const review=entry.querySelector<HTMLButtonElement>('button[type=submit]');if(review)review.disabled=!canWrite();
  const publish=root.querySelector<HTMLButtonElement>('[data-point-value=publish]');if(publish)publish.disabled=!canWrite();
  const apply=root.querySelector<HTMLButtonElement>('[data-point-value=inputs]');if(apply)apply.disabled=blocked()||!receipt||receipt.stamp!==stamp()||!offer||o.historical()||!o.permission('ReadMarketData');
 }
 async function leave(){if(!dirty)return true;const agreed=await askInPanel(root,{text:'Discard these unsaved market-value edits? An unconfirmed publication remains retained.',keep:'Keep editing',go:'Discard edits'})(abandoned.signal);if(agreed){dirty=entryDirty=false;invalidate();o.changed();}return agreed;}
 function change(){dirty=entryDirty=true;invalidate();o.changed();sync();}
 function action(work:()=>Promise<void>){const owner=o.identity();void o.act(async()=>{try{await work();}catch(error){if(owner!==o.identity())return;get('message').textContent=error instanceof Error?error.message:'The market operation could not be confirmed.';throw error;}});}

 async function readSetup(selectedContext:PointContext,search=query,page=0){
  restore();if(pending.command)throw Error('Reconcile the original publication before reading new point choices.');
  const selected=o.selection();if(!selected||!o.permission('ReadMarketData'))throw Error('Select an authorized exact market source.');
  generation++;const started=fence();reading=true;query=search;offset=page;context=cloneRead(selectedContext);clearSetup();field<HTMLInputElement>('search').value=query;sync();
  const request={context:cloneRead(selectedContext),source:cloneRead(selected.source),curveCode:selected.curveCode??null,contractId:selected.contractId??null,
   search,offset:page,limit:50,quoteFilter:selected.quoteFilter??null,eligibility:selected.eligibility??null};
  try{const value=await o.api<unknown>('market-values/setup',request);if(!current(started))return;setup=requirePointSetup(request,value);drawFields();dirty=entryDirty=criteriaDirty=false;get('message').textContent=o.historical()?'This header selection is read only. Use working inputs with current knowledge to publish.':'';}
  finally{if(current(started)){reading=false;sync();o.changed();}}
 }
 async function readCoordinates(){
  if(!o.permission('ReadMarketData'))throw Error('Restore market-read authority to inspect point choices.');
  const selected=o.selection();if(!selected)throw Error('Choose an exact market source.');
  const businessDate=field<HTMLInputElement>('businessDate').value,knowledge=mode.value;
  if(!businessDate||!['current','at'].includes(knowledge))throw Error('Choose the business date, value time and knowledge mode explicitly.');
  const started=fence(),asOf=await atClock.instant(),knownAt=knowledge==='at'?await knownClock.instant():null;if(!current(started))return;
  const captured=await o.api<{context:{contextId:string;effectiveOn:string;knownAt:string}}>('date-schedules/context',{effectiveOn:businessDate,knownAt,mode:knowledge});if(!current(started))return;
  if(captured.context?.effectiveOn!==businessDate||!captured.context.contextId||!captured.context.knownAt)throw Error('The market context did not retain this exact business date.');
  await readSetup(carryReadReferences({contextId:captured.context.contextId,asOf,businessDate,...selected.frame?{snapshotId:selected.frame.id}:{}},captured.context),query);
 }
 function drawFields(){
  if(!setup)return;entry.replaceChildren();entry.hidden=false;
  const fields=node('fieldset');fields.className='schedule-fields';entry.append(fields);
  editor=mountOperationFields(fields,[{name:'point',label:'Point',kind:'reference',nullable:false,members:['target'],choices:setup.choices.map((choice,index)=>({id:String(index),label:choice.label,value:{target:choice.target}}))},
   {name:'policy',label:'Publication policy',kind:'select',nullable:false,choices:[{id:setup.policy.code,label:setup.policy.label,value:setup.policy.code}]},
   {name:'value',label:'Signed market value',kind:'text',nullable:false}],{api:o.api,zone:o.zone,act:o.act,current:()=>lastScope===stamp()&&actor===o.identity(),changed:change});
  const evidenceLabel=node('label','Evidence'),evidence=node('textarea');evidence.name='evidence';evidence.required=true;evidence.rows=3;evidenceLabel.append(evidence);fields.append(evidenceLabel);
  const unit=node('p');unit.className='field-help';unit.dataset.pointValue='selected-unit';unit.id=root.id+'-selected-unit';unit.hidden=true;fields.append(unit);
  field<HTMLInputElement>('value').setAttribute('aria-describedby',unit.id);
  const previous=node('p');previous.className='field-help';fields.append(previous);
  field<HTMLSelectElement>('point').addEventListener('change',()=>{
   field<HTMLInputElement>('value').value='';evidence.value='';const selected=field<HTMLSelectElement>('point').value,choice=selected?setup?.choices[Number(selected)]:undefined;
   unit.hidden=!choice;unit.textContent=choice?`Currency: ${choice.currency} · Quote unit: ${choice.valueUnit}`:'';
   previous.textContent=choice?choice.previous?`Previous value: ${choice.previous.value} ${choice.valueUnit}`:'No existing value at this point.':'';
  });
  const review=node('button','Review publication');review.type='submit';entry.append(review);
  get('search-hint').textContent=setup.page.hasMore?'More matches are available. Refine the search or open the next page.':'All matches on this page are shown.';
  get('unavailable').replaceChildren(...setup.unavailable.map(row=>node('p',row.label+' · '+row.reason)));
 }

 function drawReview(){
  const host=get('review-host');host.replaceChildren();if(!reviewed)return;
  const selected=reviewed,acknowledgement=selected.reply.acknowledgement,box=node('section');box.className='lifecycle-review';box.dataset.pointValue='review';box.append(node('h4',acknowledgement?'Review existing value acknowledgement':'Review market publication'));
  renderPhysicalSections(box,overviewTermSections(selected.reply.overviewSections,o.zone()));
  const publish=node('button',acknowledgement?'Acknowledge reviewed value':'Publish reviewed value');publish.type='button';publish.dataset.pointValue='publish';box.append(publish);host.append(box);
  publish.onclick=()=>action(async()=>{
   if(reviewed!==selected||selected.serial!==draftVersion||selected.stamp!==stamp()||!writeAdmitted())throw Error('Review the current exact market value before publishing.');
   const command=pending.begin('market-values/record',cloneRead(selected.request),()=>selected.request.operationId,'Publish market value') as PointPending;
   command.scopeStamp=selected.stamp;dirty=false;await send(command);
  });sync();
 }
 function drawReceipt(){
  const host=get('receipt');host.replaceChildren();host.hidden=!receipt;offer=undefined;if(!receipt)return;
  const accepted=receipt;
  host.append(node('h4',accepted.reply.acknowledgement?'Existing market value acknowledged':'Market value published'));
  const fact=node('p','The observation is recorded. Publishing does not adopt a financial mark.');fact.title=accepted.reply.outcome.observationId;host.append(fact);
  try{renderPhysicalSections(host,overviewTermSections(accepted.reply.overviewSections,o.zone()));}catch{host.append(node('p','The publication is recorded; its formatted review details are unavailable.'));}
  if(accepted.reply.editedSelection!==undefined){
   try{
    const edited=pointEditedSelection(accepted.request,accepted.reply.editedSelection);
    if(edited.state==='available')offer=edited;else host.append(node('p',edited.reason));
   }catch(error){host.append(node('p',error instanceof Error?error.message:'The edited input offer could not be verified.'));}
  }
  if(offer){
   const selected=offer,use=node('button','Use these working inputs');use.type='button';use.dataset.pointValue='inputs';host.append(use);
   use.onclick=()=>action(async()=>{
    const same=()=>receipt===accepted&&offer===selected&&accepted.stamp===stamp()&&actor===o.identity()&&!o.historical();
    if(!same()||pending.command||!o.permission('ReadMarketData'))throw Error('The header or account changed. The observation remains recorded; current inputs were not changed.');
    if(!await leave()||!same())return;
    const value=await o.api<unknown>('market-values/inputs',{ownerId:accepted.request.ownerId,operationId:accepted.request.operationId,selectionId:selected.selectionId,
     originalSnapshotId:selected.originalSnapshotId,fingerprint:selected.fingerprint});
    const frame=requirePointInputs(accepted.request,selected,value);if(!same())return;
    await o.applyFrame(frame,same);
   });
   if(accepted.stamp!==stamp())host.append(node('p','The header or account changed. This published value has not changed the current inputs.'));
  }
  sync();
 }
 function accept(command:PointPending,value:unknown){
  const request=command.body as PointRequest;requirePointReceipt(request,value);
  pending.clear();checkedAbsent=undefined;store();clearSetup();dirty=entryDirty=criteriaDirty=false;
  receipt={request:cloneRead(request),reply:value,stamp:command.scopeStamp};drawReceipt();
  get('message').textContent=value.acknowledgement?'The existing observation was acknowledged. Current inputs were not changed.':'The market value was published. Use an offered working input selection explicitly; financial adoption remains separate.';
 }
 async function send(command:PointPending){
  if(saving||pending.command!==command||!o.permission('ReadMarketData')||!o.permission('ImportObservations')||o.historical())throw Error('Restore publication authority before retrying the original request.');
  const owner=actor,previous=pending.sending();checkedAbsent=undefined;saving=true;store();sync();
  try{const value=await o.api<unknown>(command.path,cloneRead(command.body));if(owner!==o.identity()||pending.command!==command)return;accept(command,value);}
  catch(error){if(owner===o.identity()&&pending.command===command){pending.failed((error as {code?:string}).code,previous);store();if(!pending.command)clearSetup();}throw error;}
  finally{if(owner===o.identity()){saving=false;sync();}}
 }
 async function status(){
  restore();const command=pending.command as PointPending|undefined;if(!command||saving||!o.permission('ReadMarketData'))return;
  const owner=actor;checkedAbsent=undefined;saving=true;sync();
  try{
   const value=await o.api<{found?:boolean}>('market-values/status',{ownerId:command.body.ownerId,operationId:command.operationId});
   if(owner!==o.identity()||pending.command!==command)return;
   if(value?.found===false){checkedAbsent={command,actor:owner};get('message').textContent='No recorded outcome was found. The original publication remains retained for status recovery or an identical authorized retry.';return;}
   if(value?.found!==true)throw Error('The status did not attest the original publication. Keep the retained request.');
   accept(command,value);
  }finally{if(owner===o.identity()){saving=false;sync();}}
 }
 async function dismiss(){
  const checked=checkedAbsent,command=pending.command;if(!checked||!canDismiss())return;
  const yes=await askInPanel(root,{text:'Dismiss only this local recovery request? This does not cancel the original publication or prove it cannot complete. Read the market point records again before another publication.',keep:'Keep request',go:'Dismiss local request'})(abandoned.signal);
  if(!yes||checkedAbsent!==checked||pending.command!==command||checked.actor!==o.identity())return;
  pending.clear();checkedAbsent=undefined;store();clearSetup();context=undefined;dirty=entryDirty=criteriaDirty=false;
  get('message').textContent='Local recovery was dismissed. The original may still complete. Read current point choices before another publication.';sync();
 }
 entry.onsubmit=event=>{event.preventDefault();action(async()=>{
  if(!writeAdmitted()||!editor||!setup||!context)throw Error('Read an authorized current point and choose its publication terms.');
  invalidate();const serial=draftVersion,started=fence(),fields=await editor.read();if(!current(started)||serial!==draftVersion)return;
  const evidence=entry.querySelector<HTMLTextAreaElement>('[name=evidence]')!.value;
  if(!evidence)throw Error('Enter the publication evidence.');
  const request:PointRequest={operationId:crypto.randomUUID(),ownerId:setup.ownerId,context:{...cloneRead(context),snapshotId:setup.originalInputs?.snapshotId??null},policy:fields.policy as string,target:fields.target as PointRequest['target'],value:fields.value as string,evidence,originalInputs:cloneRead(setup.originalInputs)};
  const value=await o.api<unknown>('market-values/preview',request);if(!current(started)||serial!==draftVersion)return;
  const accepted=reviewedPointRequest(request,value),reply=value as PointReply;
  overviewTermSections(reply.overviewSections,o.zone());reviewed={request:accepted,reply,serial,stamp:started.stamp};drawReview();
 });};
 readForm.onsubmit=event=>{event.preventDefault();action(async()=>{if(entryDirty&&!await leave())return;await readCoordinates();});};
 function criteriaChanged(){generation++;reading=false;knownVisibility();invalidate();dirty=criteriaDirty=true;o.changed();sync();}
 readForm.addEventListener('input',criteriaChanged);readForm.addEventListener('change',criteriaChanged);
 searchForm.onsubmit=event=>{event.preventDefault();action(async()=>{if(!context||!await leave())return;await readSetup(context,field<HTMLInputElement>('search').value);});};
 get('refresh').onclick=()=>action(async()=>{if(!await leave())return;if(mode.value)await readCoordinates();else if(context)await readSetup(context,query,offset);else throw Error('Select the value clock and knowledge explicitly before reading points.');});
 for(const [name,direction]of [['previous',-1],['next',1]] as const)get(name).onclick=()=>action(async()=>{if(!setup||!context||!await leave())return;await readSetup(context,query,setup.page.offset+direction*setup.page.limit);});
 get('status').onclick=()=>action(status);get('retry').onclick=()=>action(async()=>{const command=pending.command as PointPending|undefined;if(command)await send(command);});get('dismiss').onclick=()=>action(dismiss);
 function reset(){abandoned.abort();abandoned=new AbortController();generation++;reading=false;dirty=entryDirty=criteriaDirty=false;context=undefined;checkedAbsent=undefined;receipt=undefined;offer=undefined;clearSetup();restore();lastScope=stamp();prefill();get('receipt').hidden=true;sync();}
 async function load(selectedContext?:PointContext){
  restore();scopeChanged();if(pending.command){sync();return;}
  if(selectedContext){field<HTMLInputElement>('businessDate').value=selectedContext.businessDate;atClock.set(selectedContext.asOf);await readSetup(selectedContext,query,offset);}
  else{context=undefined;clearSetup();prefill();sync();}
 }
 reset();
 return{load,sync,leave,unsaved:()=>dirty,pending:()=>!!pending.command,
  refresh(){atClock.refresh();knownClock.refresh();if(reviewed)drawReview();if(receipt)drawReceipt();sync();},reset};
}
