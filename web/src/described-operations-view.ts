import {mountOperationFields,type OperationField} from './operation-fields.js';
import {PendingCommand,actorTransport,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {zonedInput,sameUtcInstant} from './user-time.js';
import {renderRegularTable} from './regular-table.js';
import {displayExactInstant,displayInstant} from './market-view.js';
import type {FacilityContext} from './facility-quantities-view.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Section='definitions'|'activity'|'all';
export type OperationProtocol={kind:'facility'|'transport'|'route';path:'facilities/operations'|'gas/transport/capacity'|'gas/transport/routes';scopeKey:'facilityId'|'entitlementId'|'routeId';listKey:'facilities'|'entitlements'|'routes';data:'facility-ops'|'transport-capacity'|'transport-routes';commandData:'facilityCommand'|'transportCommand'|'routeCommand';storage:string;configurationPermission:'ConfigureFacilityLimits'|'ConfigureGasServices';label:string;plural:string;configurationHeading:string;operationsHeading:string;boundary:string};
type Form={label:string;permission:string;base:Record<string,unknown>;fields:OperationField[]};
type Inspection={overviewSections:{label:string;values:{label:string;value:string}[]}[];tables:{label:string;columns:string[];rows:string[][];instantColumns?:number[]}[]};
type Context={contextId:string;effectiveOn:string;knownAt:string};
type Resource={id:string;label:string;versionId?:string;revision:number};
type View=Inspection&{ownerId:string;context:Context;at:string;facilityId?:string;facilities?:Resource[];entitlements?:Resource[];entitlementId?:string;routes?:Resource[];routeId?:string;hasMore?:boolean;forms:Record<string,Form>;configuration:Record<string,Form>;canRecord:boolean;canConfigure:boolean;historical:boolean;allocationLinks?:{ledgerId:string;label:string}[];sourceSearch?:string;sourceChoicesTruncated?:boolean};
type Preview=Inspection&{ownerId:string;context:Context;operationId:string;facilityId?:string;entitlementId?:string;routeId?:string;kind:string;command:Record<string,unknown>;effectiveAt:string};
type Outcome={found?:true;operationId:string;facilityId?:string;entitlementId?:string;routeId?:string;kind:string;outcome:Record<string,unknown>;command?:Record<string,unknown>;effectiveAt?:string};
export type DescribedOperationsOptions={api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;pendingElsewhere?:()=>boolean;zone:()=>string;identity:()=>string;context:()=>FacilityContext;permission:(name:string)=>boolean;changed:()=>void;openAllocations?:(ledgerId:string)=>Promise<void>};
const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
const canonical=(value:unknown):string=>JSON.stringify(value,(_key,item)=>item&&typeof item==='object'&&!Array.isArray(item)?Object.fromEntries(Object.entries(item).sort(([a],[b])=>a.localeCompare(b))):item);

/** The installed operating consumers share described entry and retained
 * commands. Native operations own every quantity, policy and assessment. */
export function setupDescribedOperations(root:HTMLElement,o:DescribedOperationsOptions,p:OperationProtocol){
 const physical=p.kind==='facility',noun=physical?'facility':p.kind==='route'?'transport route':'transport capacity',scope=(value:{facilityId?:string;entitlementId?:string;routeId?:string})=>value[p.scopeKey];
 root.classList.add('facility-operations');
 root.innerHTML=`<div class="card"><div class="card-heading"><h2 data-${p.data}="heading">${p.configurationHeading}</h2><button data-${p.data}="refresh" type="button" class="secondary">Refresh</button></div><p data-${p.data}="context" class="field-help"></p><label>${p.label}<select data-${p.data}="${physical?'facility':p.kind==='route'?'route':'entitlement'}"><option value="">New ${noun} definition</option></select></label><p class="field-help">${p.boundary}</p></div>
 <div data-${p.data}="pending" class="operations-recovery" hidden><p data-${p.data}="pending-text" role="status"></p><button data-${p.data}="status" type="button">Check original request status</button><button data-${p.data}="retry" type="button" class="secondary">Retry original request</button><button data-${p.data}="dismiss" type="button" class="secondary" disabled>Dismiss checked request</button></div>
 ${physical?`<form data-${p.data}="source-search" class="card" hidden><label>Find reported flow<input data-${p.data}="source-query" type="search" maxlength="200" placeholder="Pipeline, point, operating date or source identity"></label><button type="submit" class="secondary">Find reported flow</button><p data-${p.data}="source-hint" class="field-help" role="status"></p></form>`:''}
 <p data-${p.data}="message" role="status"></p><section data-${p.data}="inspection"></section><div data-${p.data}="actions" class="actions"></div><form data-${p.data}="editor" class="card" hidden></form><section data-${p.data}="review-host"></section>`;
 const get=<T extends HTMLElement>(name:string)=>root.querySelector<T>(`[data-${p.data}="${name}"]`)!;
 const select=get<HTMLSelectElement>(physical?'facility':p.kind==='route'?'route':'entitlement'),form=get<HTMLFormElement>('editor');
 const searchForm=physical?get<HTMLFormElement>('source-search'):undefined,searchInput=physical?get<HTMLInputElement>('source-query'):undefined;
 const pending=new PendingCommand(code=>!!code&&((physical?/^(storage_facility_|facility_operation_|facility_quantity_|temporal_)/:p.kind==='route'?/^(gas_transport_route_|temporal_)/:/^(gas_transport_capacity_|temporal_)/).test(code)||['access_denied','historical_read_only','request_format','revision_conflict'].includes(code)));
 const transport=actorTransport<RequestCommand>(()=>p.storage+o.identity(),()=>sessionStorage);
 let section:Section=physical?'definitions':'all',selected='',sourceSearch='',queryScope='',actor='',generation=0,draftVersion=0,reading=false,dirty=false;
 let directory:View|undefined,view:View|undefined,definition:Form|undefined,editor:ReturnType<typeof mountOperationFields>|undefined;
 let effective:ReturnType<typeof zonedInput>|undefined,reviewed:{value:Preview;body:Record<string,unknown>}|undefined;
 let checkedAbsent:{command:RequestCommand;actor:string}|undefined;
 const canDismiss=()=>!!pending.command&&checkedAbsent?.command===pending.command&&checkedAbsent.actor===o.identity();
 const stamp=()=>{try{return o.identity()+canonical(o.context());}catch{return '';}};
 const fence=()=>({generation,stamp:stamp()}),current=(at:ReturnType<typeof fence>)=>at.generation===generation&&at.stamp===stamp();
 const source=()=>view??directory;
 const blocked=()=>o.busy()||reading||!!pending.command||!!o.pendingElsewhere?.();
 function workingContext(){try{const context=o.context();return context.knowledgeMode==='current'&&context.retained===null;}catch{return false;}}
 const working=()=>workingContext()&&source()?.historical===false;
 const allowed=(f:Form)=>working()&&o.permission(f.permission)&&(f.permission===p.configurationPermission?source()?.canConfigure:source()?.canRecord)===true;
 function retain(){transport.write(pending.command);o.changed();}
 function restore(){if(actor===o.identity())return;actor=o.identity();checkedAbsent=undefined;pending.clear();const saved=restoreTradeRequest(transport.read());if(saved?.path===p.path+'/record')pending.command={...saved,uncertain:true};}
 function invalidate(){draftVersion++;reviewed=undefined;get('review-host').replaceChildren();dirty=true;o.changed();sync();}
 function clearEditor(){draftVersion++;definition=undefined;editor=undefined;effective=undefined;reviewed=undefined;dirty=false;form.hidden=true;form.replaceChildren();get('review-host').replaceChildren();}
 function inspect(host:HTMLElement,value:Inspection){
  host.replaceChildren();
  if(!Array.isArray(value.overviewSections)||!Array.isArray(value.tables))throw Error('The '+noun+' inspection is incomplete. Refresh its exact source.');
  for(const section of value.overviewSections){const card=node('section'),list=node('dl');card.className='card';card.append(node('h3',section.label));list.className='lifecycle-review-facts';for(const fact of section.values){if(typeof fact.label!=='string'||typeof fact.value!=='string')throw Error('The '+noun+' facts are incomplete.');list.append(node('dt',fact.label),node('dd',fact.value));}card.append(list);host.append(card);}
  for(const [index,table]of value.tables.entries()){
   const instants=table.instantColumns??[];
   if(!Array.isArray(instants)||instants.some(column=>!Number.isSafeInteger(column)||column<0||column>=table.columns.length))throw Error('The '+noun+' table time columns are incomplete.');
   const card=node('section'),grid=node('div');card.className='card';grid.className='table-wrap';card.append(node('h3',table.label),grid);host.append(card);
   const displayRows=table.rows.map(row=>row.map((value,column)=>instants.includes(column)&&value!==''?displayExactInstant(value,o.zone()):value));
   renderRegularTable(grid,(physical?'facility-operations':p.data)+'-'+index,stamp()+selected,table.columns,table.rows,[],o.busy,{displayRows});
  }
 }
 function drawInspection(){
  const value=source();if(!value)return;inspect(get('inspection'),value);
  if(physical&&view?.allocationLinks?.length){const links=node('section');links.className='card';links.append(node('h3','Contract allocation links'));for(const link of view.allocationLinks){if(!link.ledgerId||!link.label)throw Error('The physical source allocation link is incomplete.');const button=node('button','Open quantity allocations · '+link.label);button.type='button';button.className='secondary';button.dataset.facilityAllocation=link.ledgerId;button.onclick=()=>void o.act(async()=>{await o.openAllocations?.(link.ledgerId);});links.append(button);}get('inspection').append(links);}
 }
 function drawContext(){
  const value=source(),host=get('context');
  host.title=value?.context.knownAt??'';
  host.textContent=value?`Business date ${value.context.effectiveOn} · ${value.historical?'Historical, read only':'Working data'} · known as of ${displayInstant(value.context.knownAt,o.zone())} · ${o.zone()}`:'';
 }
 function validate(value:View,context:FacilityContext){
  if(!value||typeof value.ownerId!=='string'||!value.ownerId||!value.context?.contextId||typeof value.context.knownAt!=='string'||value.context.effectiveOn!==context.effectiveOn||context.knowledgeMode==='retained'&&value.context.contextId!==context.retained?.contextId||
   !value.forms||!value.configuration||typeof value.canRecord!=='boolean'||typeof value.canConfigure!=='boolean'||typeof value.historical!=='boolean')throw Error('The '+noun+' fields did not retain the selected context. Refresh.');
  displayExactInstant(value.context.knownAt,o.zone());
 }
 function sync(){
  restore();select.disabled=blocked()||!directory;get<HTMLButtonElement>('refresh').disabled=blocked();
  if(searchForm)searchForm.hidden=section!=='activity'||!selected||!view;
  for(const input of searchForm?.querySelectorAll<HTMLInputElement|HTMLButtonElement>('input,button')??[])input.disabled=blocked();
  if(physical)get('source-hint').textContent=view?.sourceChoicesTruncated?'More reported flows match this search. Refine the query to find the exact source.':'';
  for(const button of get('actions').querySelectorAll<HTMLButtonElement>('button')){const f=activeForms()[button.dataset[p.commandData]!];button.disabled=blocked()||!f||!allowed(f);}
  const fields=form.querySelector<HTMLFieldSetElement>('fieldset');if(fields)fields.disabled=blocked()||!definition||!allowed(definition);
  get('pending').hidden=!pending.command;get('pending-text').textContent=pending.command?`${pending.command.label??p.label+' action'} has an unconfirmed outcome. Status and Retry keep its original identity and source.`:'';
  get<HTMLButtonElement>('status').disabled=o.busy()||!pending.command;get<HTMLButtonElement>('retry').disabled=o.busy()||!pending.command||!workingContext();
  get<HTMLButtonElement>('dismiss').disabled=o.busy()||!canDismiss();
  const record=root.querySelector<HTMLButtonElement>(`[data-${p.data}="record"]`);if(record)record.disabled=blocked()||!reviewed||!definition||!allowed(definition);
  for(const link of root.querySelectorAll<HTMLButtonElement>('[data-facility-allocation]'))link.disabled=blocked()||!o.openAllocations;
 }
 function activeForms(){return section==='definitions'?source()?.configuration??{}:section==='all'?{...source()?.configuration,...source()?.forms}:selected?view?.forms??{}:{};}
 function actions(){
  get('heading').textContent=section==='definitions'?p.configurationHeading:section==='all'?p.label:p.operationsHeading;
  get('actions').replaceChildren();
  const groups=section==='all'?[[p.configurationHeading,source()?.configuration??{}],[p.operationsHeading,source()?.forms??{}]] as const:[['',activeForms()]] as const;
  for(const [label,forms]of groups){
   if(!Object.keys(forms).length)continue;
   const host=label?node('section'):get('actions');if(label){host.className='card';host.append(node('h3',label));get('actions').append(host);}
   for(const [id,f]of Object.entries(forms)){const button=node('button',f.label);button.type='button';button.dataset[p.commandData]=id;button.onclick=()=>{if(blocked()||!allowed(f))return;if(dirty&&!confirm('Discard unsaved '+noun+' inputs?'))return;mount(f);};host.append(button);}
  }
  sync();
 }
 function mount(f:Form){
  clearEditor();definition=f;form.hidden=false;form.append(node('h3',f.label));const fields=node('fieldset'),label=node('label','Record effective at · '+o.zone()),time=node('input');time.name='operationEffectiveAt';time.type='datetime-local';time.step='1';time.required=true;label.append(time);fields.append(label);form.append(fields);
  effective=zonedInput(time,o.zone,o.api);const host=node('div');host.className='schedule-fields';fields.append(host);editor=mountOperationFields(host,f.fields,{api:o.api,act:o.act,zone:o.zone,current:()=>!!definition&&working(),changed:invalidate});
  const preview=node('button','Review operation');preview.type='submit';preview.setAttribute('data-'+p.data,'preview');fields.append(preview);dirty=false;sync();
 }
 async function load(id=selected,query=sourceSearch){
  restore();generation++;const at=fence(),context=structuredClone(o.context()),searching=physical&&section==='activity';sourceSearch=searching&&id===selected&&queryScope===at.stamp?query:'';queryScope=at.stamp;if(searchInput)searchInput.value=sourceSearch;const requestedSearch=sourceSearch;reading=true;view=undefined;directory=undefined;clearEditor();select.replaceChildren(new Option('New '+noun+' definition',''));drawContext();get('inspection').replaceChildren();get('actions').replaceChildren();get('message').textContent='Reading '+noun+' records…';sync();
  try{const result=await o.api<View>(p.path+'/directory',{context});if(!current(at))return;validate(result,context);if(!Array.isArray(result[p.listKey]))throw Error('The '+noun+' directory is incomplete.');directory=result;
   select.replaceChildren(new Option('New '+noun+' definition',''),...result[p.listKey]!.map(f=>new Option(`${f.label} · version ${f.revision}`,f.id)));if(id&&!result[p.listKey]!.some(f=>f.id===id))select.append(new Option('Previously selected '+noun,id));selected=id;select.value=id;
   if(id){const read=await o.api<View>(p.path+'/read',{context,[p.scopeKey]:id,...(searching?{sourceSearch:requestedSearch}:{})});if(!current(at))return;validate(read,context);if(read.ownerId!==result.ownerId)throw Error('The operating account changed. Refresh before continuing.');
    if(physical&&(read.sourceSearch!==requestedSearch||typeof read.sourceChoicesTruncated!=='boolean'))throw Error('The reported-flow choices did not retain this exact search. Refresh.');
    if(scope(read)!==id||Object.values({...read.forms,...read.configuration}).some(form=>form.base?.[p.scopeKey]!==id))throw Error('The read did not retain the selected '+noun+' and its exact command targets. Refresh.');view=read;}
   drawInspection();
   drawContext();
   get('message').textContent=[section==='activity'&&!selected?'Choose a facility to inspect its opening evidence, independent plan and measured activity.':'',
    directory.hasMore?'More '+p.plural+' exist than this directory displays.':''].filter(Boolean).join(' ');actions();
  }catch(error){if(!current(at))return;directory=undefined;view=undefined;get('inspection').replaceChildren();get('actions').replaceChildren();get('message').textContent=String(error);throw error;}finally{if(current(at)){reading=false;sync();}}
 }
 form.addEventListener('input',invalidate);form.addEventListener('change',invalidate);
 form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{
  if(!definition||!editor||!effective||!allowed(definition)||pending.command)throw Error('Choose an authorized '+noun+' operation in working data.');
  const at=fence(),version=draftVersion,f=definition,context=structuredClone(o.context()),operationId=crypto.randomUUID();
  const [fields,effectiveAt]=await Promise.all([editor.read(),effective.instant()]);if(!current(at)||version!==draftVersion||f!==definition)return;
  const body={context,ownerId:source()!.ownerId,operationId,effectiveAt,command:{...f.base,...fields}},value=await o.api<Preview>(p.path+'/preview',body);if(!current(at)||version!==draftVersion||f!==definition)return;
  if(value.operationId!==operationId||value.ownerId!==body.ownerId||value.kind!==body.command.kind||!scope(value)||!value.command||value.command.kind!==value.kind||value.command[p.scopeKey]!==scope(value)||
   typeof body.command[p.scopeKey]==='string'&&scope(value)!==body.command[p.scopeKey]||value.command.expectedStateVersion!==body.command.expectedStateVersion||
   !sameUtcInstant(value.effectiveAt,effectiveAt)||value.context?.effectiveOn!==context.effectiveOn)throw Error('The review did not retain this exact '+noun+' request. Review it again.');
  reviewed={value,body:{context,ownerId:body.ownerId,effectiveAt:value.effectiveAt,command:structuredClone(value.command)}};
  const review=node('section');review.setAttribute('data-'+p.data,'review');review.append(node('h3','Review '+f.label));const facts=node('div');inspect(facts,value);review.append(facts);const record=node('button','Record reviewed operation');record.type='button';record.setAttribute('data-'+p.data,'record');record.onclick=()=>void o.act(async()=>{if(!reviewed)return;const r=reviewed;const command=pending.begin(p.path+'/record',r.body,()=>r.value.operationId,f.label);retain();sync();await send(command);});review.append(record);get('review-host').replaceChildren(review);sync();
 });};
 function receipt(command:RequestCommand,value:Outcome,complete=false){
  const expected=command.body.command as Record<string,unknown>;
  if(value.operationId!==command.operationId||scope(value)!==expected[p.scopeKey]||value.kind!==expected.kind||!value.outcome||typeof value.outcome!=='object'||complete&&(canonical(value.command)!==canonical(expected)||!value.effectiveAt||!sameUtcInstant(value.effectiveAt,String(command.body.effectiveAt))))throw Error('The reply did not identify the original '+noun+' action. Keep the request and check its status.');
 }
 async function confirmed(command:RequestCommand,value:Outcome){receipt(command,value);checkedAbsent=undefined;pending.clear();retain();clearEditor();selected=scope(value)!;await load(selected);get('message').textContent=p.label+' action recorded. Exact records and assessments were refreshed.';}
 async function send(command:RequestCommand){checkedAbsent=undefined;const at=fence(),uncertain=pending.sending();retain();sync();try{const result=await o.api<Outcome>(command.path,{...command.body,operationId:command.operationId});if(current(at)&&pending.command===command)await confirmed(command,result);}catch(error){if(current(at)&&pending.command===command){pending.failed((error as {code?:string}).code,uncertain);retain();sync();}throw error;}}
 select.onchange=()=>{const id=select.value;if(dirty&&!confirm('Discard unsaved '+noun+' inputs?')){select.value=selected;return;}void o.act(()=>load(id));};
 if(searchForm)searchForm.onsubmit=event=>{event.preventDefault();if(blocked()||section!=='activity'||!selected)return;if(dirty&&!confirm('Discard unsaved '+noun+' inputs?'))return;const query=searchInput!.value;void o.act(()=>load(selected,query));};
 get('refresh').onclick=()=>{if(!dirty||confirm('Discard unsaved '+noun+' inputs and refresh?'))void o.act(()=>load());};
 get('retry').onclick=()=>void o.act(async()=>{if(pending.command)await send(pending.command);});
 get('status').onclick=()=>void o.act(async()=>{const command=pending.command;if(!command)return;checkedAbsent=undefined;sync();const at=fence(),value=await o.api<Outcome|{found:false}>(p.path+'/status',{ownerId:command.body.ownerId,operationId:command.operationId});if(!current(at)||pending.command!==command)return;if(value.found===false){checkedAbsent={command,actor:o.identity()};get('message').textContent='No original receipt is available yet. Retry the original request, or explicitly dismiss this checked local recovery.';sync();return;}receipt(command,value,true);await confirmed(command,value);});
 get('dismiss').onclick=()=>void o.act(async()=>{
  if(!canDismiss())throw Error('Check the original request status under this account before dismissing local recovery.');
  if(!confirm('Dismiss this checked request? This clears only local recovery. It does not cancel the request or prove absence of a fact. Records will be refreshed before another review.'))return;
  checkedAbsent=undefined;pending.clear();retain();clearEditor();await load();get('message').textContent='Local recovery dismissed. Records were refreshed; the original request outcome remains queryable by its identity.';
 });
 return{load,sync,show(next:Section){section=next;clearEditor();actions();},selected:()=>selected||null,edited:()=>dirty||!!pending.command,unconfirmed:()=>!!pending.command,
  recoverySection:():'definitions'|'activity'=>(pending.command?.body.command as Record<string,unknown>|undefined)?.kind==='define'?'definitions':'activity',
  leave:()=>!dirty||confirm('Discard unsaved '+noun+' inputs?'),refresh(){effective?.refresh();editor?.refresh();drawInspection();drawContext();if(reviewed){reviewed=undefined;get('review-host').replaceChildren();}sync();},reset(){generation++;reading=false;directory=undefined;view=undefined;selected='';sourceSearch='';queryScope='';if(searchInput)searchInput.value='';actor='';pending.clear();clearEditor();select.replaceChildren(new Option('New '+noun+' definition',''));get('inspection').replaceChildren();get('actions').replaceChildren();drawContext();get('message').textContent='';sync();}};
}
