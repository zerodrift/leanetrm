import {renderRegularTable} from './regular-table.js';
import {displayInstant} from './market-view.js';
import {zonedInput} from './user-time.js';
import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {trackEdits} from './edit-tracking.js';
import {setupMultiSelect} from './scope-picker.js';
import type {Context} from './date-editor.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type FacilityContext={effectiveOn:string;knowledgeMode:'current'|'retained';retained:Context|null;convention:'facility_business_date_end_utc_v1'};
type Choice={obligationId:string;tradeId:string;tradeRevision:number;pointCode:string;point:string;commodity:string;day:string;sideCode:string;book:string;portfolio:string;counterparty:string;unit:string;direction:string;maximum:string;start:string;end:string};
type Header={id:string;facility:string;point:string;commodity:string;unit:string;legal_entity:string;day:string};
type Revision={id:string;revision:number;quantity:string|null;evidence:string;effective_at:string;source_revision_id?:string};
type Allocation={obligationId:string;tradeId:string;tradeRevision:number;quantity:string;book:string;portfolio:string;counterparty:string;sideCode:string};
type Facts={ledger:{id:string;native_from:string;native_to:string};labels:{facility:string;point:string;commodity:string;unit:string;legalEntity:string;day:string};source:Revision|null;plan:Revision|null;limit:Revision|null;allocations:Allocation[];sourceHistory:Revision[];planHistory:Revision[];limitHistory:Revision[];requiresReconciliation:boolean;adoption:{id:string;revision:number;plan_id:string;source_revision_id:string;state:'adopted'|'withdrawn'}|null;requiresAdoptionReconciliation:boolean};
type Balance={allocated:string;available:string|null;excess:string|null;constraint:'unassessed'|'within_limit'|'breached'};
type Directory={ownerId:string;context:Context;at:string;ledgers:Header[];choices:Choice[];hasMore:boolean;choicesTruncated:boolean;canRecord:boolean;canConfigure:boolean};
type View={ownerId:string;context:Context;at:string;facts:Facts;choices:Choice[];choicesTruncated:boolean;balance:Balance;canRecord:boolean;canConfigure:boolean;canAdopt:boolean};
type Outcome={operationId:string;outcome:{ledgerId:string;revisionId?:string;revision?:number;adoptionId?:string;state?:string}};

export function setupFacilityQuantities(o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string;context:()=>FacilityContext;changed:()=>void;tradeInspection:()=>string|null;openTrade:(id:string,revision:number)=>Promise<void>}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const input=(id:string)=>get<HTMLInputElement>(id),select=(id:string)=>get<HTMLSelectElement>(id);
 const set=(id:string,value:string)=>get(id).textContent=value;
 const effective=zonedInput(input('facility-effective'),o.zone,o.api),edits=trackEdits(get('facility-forms'),['.scope-search','.table-filters','.column-filter-row'],o.changed);
 const formEdits=new Map(['create','source','limit','plan','adoption'].map(kind=>[kind,trackEdits(get(`facility-${kind}-form`),['.scope-search','.table-filters','.column-filter-row'],o.changed)]));
 function clearEdits(){edits.clear();for(const form of formEdits.values())form.clear();}
 const pending=new PendingCommand(code=>!!code&&(/^(facility_quantity_|temporal_)/.test(code)||['access_denied','historical_read_only','request_format','revision_conflict'].includes(code)));
 let directory:Directory|undefined,view:View|undefined,selected='',generation=0,pendingStatus='';
 const amounts=new Map<string,string>();
 const key=(kind:string)=>`openetrm.facilities.${kind}.${o.identity()}`;
 const transport=actorTransport<RequestCommand>(()=>key('request'),()=>sessionStorage);
 const stamp=()=>{try{return o.identity()+JSON.stringify(o.context());}catch{return '';}};
 const fence=()=>({generation,scope:stamp()}),current=(at:ReturnType<typeof fence>)=>at.generation===generation&&at.scope===stamp();
 const targetPicker=setupMultiSelect(select('facility-targets'),{label:'Allocation targets',all:()=> 'No allocation targets',busy:()=>o.busy()||!!pending.command||!view?.canRecord});
 const createKey=(c:Choice)=>c.obligationId+'|'+c.day;
 const choiceText=(c:Choice)=>`${c.point} · ${c.day} · ${c.commodity} · ${c.direction} · ${c.counterparty} · ${c.book} · ${c.sideCode}`;
 function store(){transport.write(pending.command);}
 function clearView(){view=undefined;amounts.clear();get('facility-editor').hidden=true;get('facility-balance').replaceChildren();get('facility-allocation-table').replaceChildren();select('facility-targets').replaceChildren();for(const id of ['facility-source-history','facility-plan-history','facility-limit-history'])get(id).replaceChildren();for(const id of ['facility-source-quantity','facility-source-evidence','facility-limit-quantity','facility-limit-evidence','facility-plan-evidence','facility-adoption-evidence'])input(id).value='';for(const id of ['facility-title','facility-period','facility-preview-result','facility-exception','facility-adoption-state'])set(id,'');document.querySelectorAll<HTMLElement>('[data-facility-unit]').forEach(n=>n.textContent='');}
 function withhold(){clearView();directory=undefined;select('facility-ledger').replaceChildren(new Option('Choose a ledger…',''));select('facility-create-target').replaceChildren(new Option('Choose a booked delivery…',''));set('facility-context','');}
 function sync(){
  const blocked=o.busy()||!!pending.command;
  get<HTMLButtonElement>('facility-refresh').disabled=blocked;
  select('facility-ledger').disabled=blocked;
  input('facility-effective').disabled=blocked||!(directory?.canRecord||directory?.canConfigure);
  get<HTMLFieldSetElement>('facility-create-fields').disabled=blocked||!directory?.canRecord;
  get<HTMLFieldSetElement>('facility-source-fields').disabled=blocked||!view?.canRecord;
  get<HTMLFieldSetElement>('facility-plan-fields').disabled=blocked;
  for(const id of ['facility-preview','facility-plan-save'])get<HTMLButtonElement>(id).disabled=blocked||!view?.canRecord||!view.facts.source;
  input('facility-plan-evidence').disabled=blocked||!view?.canRecord||!view.facts.source;
  get<HTMLFieldSetElement>('facility-limit-fields').disabled=blocked||!view?.canConfigure;
  get<HTMLFieldSetElement>('facility-adoption-fields').disabled=blocked||!view?.canAdopt;
  get<HTMLButtonElement>('facility-adopt').disabled=blocked||!view?.canAdopt||!view.facts.plan||view.facts.requiresReconciliation||!!formEdits.get('plan')?.edited()||!!formEdits.get('source')?.edited();
  get<HTMLButtonElement>('facility-retract-adoption').disabled=blocked||!view?.canAdopt||view.facts.adoption?.state!=='adopted';
  for(const field of get('facility-allocation-table').querySelectorAll<HTMLInputElement>('[data-facility-allocation]'))
   field.disabled=blocked||!view?.canRecord||!view.choices.some(c=>c.obligationId===field.dataset.facilityAllocation);
  const inspection=o.tradeInspection();set('facility-trade-help',inspection??'');get('facility-trade-help').hidden=!inspection;
  for(const button of get('facility-allocation-table').querySelectorAll<HTMLButtonElement>('[data-facility-trade]')){button.disabled=blocked||!!inspection;button.title=inspection??'Inspect this trade in the Blotter.';}
  for(const control of get('facilities-page').querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('.table-filters input,.table-filters button,.column-filter-row select'))control.disabled=blocked;
  input('facility-limit-quantity').required=!input('facility-limit-remove').checked;
  input('facility-limit-quantity').disabled=blocked||!view?.canConfigure||input('facility-limit-remove').checked;
  get('facility-pending').hidden=!pending.command;
  set('facility-pending-description',pending.command?(pendingStatus||`${pending.command.label??'Facility request'} · ${String(pending.command.body.effectiveAt)}. Keep its original identity until its outcome is known.`):'');
  for(const id of ['facility-status','facility-retry','facility-discard'])get<HTMLButtonElement>(id).disabled=o.busy()||!pending.command;
  if(pending.command){let working=false;try{working=o.context().knowledgeMode==='current';}catch{/* Unapplied context. */}if(!working)get<HTMLButtonElement>('facility-retry').disabled=true;}
  targetPicker.sync();o.changed();
 }
 function history(){
  if(!view)return;
  const scope=stamp()+view.facts.ledger.id;
  for(const [kind,rows] of [['source',view.facts.sourceHistory],['plan',view.facts.planHistory],['limit',view.facts.limitHistory]] as const){
   renderRegularTable(get(`facility-${kind}-history`),`facility-${kind}-history`,scope,
    ['Revision',kind==='plan'?'Source revision':'Quantity',`Effective · ${o.zone()}`,'Evidence'],rows.map(row=>[
     String(row.revision),kind==='plan'?String(view!.facts.sourceHistory.find(r=>r.id===row.source_revision_id)?.revision??'Earlier retained source'):row.quantity??'Removed',
     displayInstant(row.effective_at,o.zone()),row.evidence]),[0],o.busy);
  }
 }
 function renderAllocations(){
  if(!view)return;
  const ids=Array.from(select('facility-targets').selectedOptions).map(option=>option.value);
  renderRegularTable(get('facility-allocation-table'),'facility-allocations',stamp()+view.facts.ledger.id,
   ['Counterparty','Portfolio / book','Physical Side','Booked native quantity',`Allocation · ${view.facts.labels.unit}`,''],ids.map(id=>{
    const choice=view!.choices.find(c=>c.obligationId===id),prior=view!.facts.allocations.find(a=>a.obligationId===id);
    const quantity=document.createElement('input');quantity.inputMode='decimal';quantity.required=true;quantity.autocomplete='off';quantity.value=amounts.get(id)??'';quantity.dataset.facilityAllocation=id;
    quantity.setAttribute('aria-label',`Allocation for ${choice?.counterparty??prior?.counterparty??'retained target'} · ${choice?.sideCode??prior?.sideCode??''}`);
    quantity.disabled=!choice||!view!.canRecord||o.busy()||!!pending.command;
    quantity.oninput=()=>{amounts.set(id,quantity.value);set('facility-preview-result','');};
    const open=document.createElement('button');open.type='button';open.className='secondary';open.textContent='Open trade';open.dataset.facilityTrade=id;open.disabled=o.busy()||!!pending.command;
    open.onclick=()=>{const target=choice??prior;if(target)void o.openTrade(target.tradeId,target.tradeRevision);};
    return[choice?.counterparty??prior?.counterparty??'Retained target',`${choice?.portfolio??prior?.portfolio??''} / ${choice?.book??prior?.book??''}`,choice?.sideCode??prior?.sideCode??'',choice?`${choice.maximum} ${choice.unit}`:'Current terms unavailable',quantity,open];
   }),[],o.busy);
  targetPicker.sync();
 }
 function renderView(){
  if(!view)return;
  const {facts,balance}=view,labels=facts.labels;
  get('facility-editor').hidden=false;get('facility-empty').hidden=true;
  set('facility-title',`${labels.facility} · ${labels.day}`);
  set('facility-period',`${labels.commodity} · ${labels.legalEntity} · ${displayInstant(facts.ledger.native_from,o.zone())} – ${displayInstant(facts.ledger.native_to,o.zone())}`);
  document.querySelectorAll<HTMLElement>('[data-facility-unit]').forEach(n=>n.textContent=labels.unit);
  get('facility-balance').replaceChildren(...[['Reported',facts.source?.quantity??'Not recorded'],['Allocated',balance.allocated],['Unallocated',balance.available??'Unassessed']].map(([label,value])=>{
   const cell=document.createElement('div'),caption=document.createElement('span'),amount=document.createElement('strong');caption.textContent=label;amount.textContent=`${value} ${labels.unit}`;cell.append(caption,amount);return cell;
  }));
  const exception=get('facility-exception');exception.hidden=false;exception.dataset.state=balance.constraint;
  exception.textContent=balance.constraint==='breached'?`Operating limit breached by ${balance.excess} ${labels.unit}. The reported quantity is retained.`:
   balance.constraint==='unassessed'?'Operating limit unassessed · a reported quantity and an explicit limit are required.':'Reported quantity is within the configured native-period maximum.';
  get('facility-reconcile').hidden=!facts.requiresReconciliation;
  input('facility-source-quantity').value=facts.source?.quantity??'';input('facility-source-evidence').value='';
  input('facility-limit-quantity').value=facts.limit?.quantity??'';input('facility-limit-evidence').value='';input('facility-limit-remove').checked=false;
  input('facility-adoption-evidence').value='';
  set('facility-adoption-state',facts.adoption?`Final input decision ${facts.adoption.revision}: ${facts.adoption.state==='adopted'?'accepted':'withdrawn'}${facts.requiresAdoptionReconciliation?' · the current source or plan differs; accepted inputs keep their exact earlier source.':''}`:'No final facility inputs accepted.');
  input('facility-plan-evidence').value='';amounts.clear();for(const row of facts.allocations)amounts.set(row.obligationId,row.quantity);
  select('facility-targets').replaceChildren(...view.choices.map(c=>new Option(`${c.counterparty} · ${c.portfolio} / ${c.book} · ${c.sideCode}`,c.obligationId,false,amounts.has(c.obligationId))),
   ...facts.allocations.filter(a=>!view!.choices.some(c=>c.obligationId===a.obligationId)).map(a=>{const option=new Option(`${a.counterparty} · retained target`,a.obligationId,false,true);option.disabled=true;return option;}));
  renderAllocations();history();clearEdits();sync();
 }
 async function loadView(id:string,at:ReturnType<typeof fence>){
  const result=await o.api<View>('facilities/quantities/read',{context:o.context(),ledgerId:id});if(!current(at))return;
  view=result;selected=id;renderView();set('facility-context',`Business date ${result.context.effectiveOn} · effective through ${displayInstant(result.at,o.zone())} · known through ${displayInstant(result.context.knownAt!,o.zone())}`);
 }
 async function load(id=selected){
  generation++;const at=fence();withhold();set('facility-saved','');sync();
  try{
   const result=await o.api<Directory>('facilities/quantities/directory',{context:o.context()});if(!current(at))return;
   directory=result;
   if(!id)try{id=sessionStorage.getItem(key('selection'))??'';}catch{/* No saved choice. */}
   select('facility-ledger').replaceChildren(new Option('Choose a ledger…',''),...result.ledgers.map(l=>new Option(`${l.facility} · ${l.day} · ${l.commodity} · ${l.legal_entity}`,l.id)));
   if(id&&!result.ledgers.some(l=>l.id===id))select('facility-ledger').append(new Option('Previously selected facility day',id));
   select('facility-ledger').value=id;
   select('facility-create-target').replaceChildren(new Option('Choose a booked delivery…',''),...result.choices.map(c=>new Option(choiceText(c),createKey(c))));
   const stored=restoreTradeRequest(transport.read());pending.command=stored?.path==='facilities/quantities/record'?{...stored,uncertain:true}:undefined;
   get('facility-empty').hidden=!!id;
   set('facility-context',`Business date ${result.context.effectiveOn} · records through the end of that UTC date${result.hasMore?' · latest 200 facility days shown':''}${result.choicesTruncated?' · first 1,000 delivery choices shown':''}.`);
   if(id)await loadView(id,at);
   if(current(at)){clearEdits();sync();}
  }catch(error){if(!current(at))return;withhold();sync();throw error;}
 }
 function allocations(){
  if(!view)throw Error('Choose a facility ledger first.');
  return Array.from(select('facility-targets').selectedOptions).map(option=>{
   const target=view!.choices.find(c=>c.obligationId===option.value),quantity=amounts.get(option.value)?.trim();
   if(!target)throw Error('Remove an unavailable target before saving a replacement plan.');
   if(!quantity)throw Error(`Enter a native allocation for ${target.counterparty} · ${target.sideCode}.`);
   return{obligationId:target.obligationId,tradeRevision:target.tradeRevision,quantity};
  });
 }
 async function confirmed(command:RequestCommand,result:Outcome){
  if(result.operationId!==command.operationId||typeof result.outcome?.ledgerId!=='string'||
   ((command.body.command as {ledgerId?:string}).ledgerId&&result.outcome.ledgerId!==(command.body.command as {ledgerId?:string}).ledgerId))throw Error('The request outcome could not be verified. Check its status before retrying.');
  pending.clear();pendingStatus='';store();clearEdits();selected=result.outcome.ledgerId;
  try{sessionStorage.setItem(key('selection'),selected);}catch{/* The selected id remains in memory. */}
  await load(selected);set('facility-saved',result.outcome.adoptionId?'Final input decision recorded.':result.outcome.revision?`Saved revision ${result.outcome.revision}.`:'Facility ledger opened.');o.changed();
 }
 async function send(command:RequestCommand){
  if(command.path!=='facilities/quantities/record')throw Error('The retained facility request is invalid. Check the ledger history.');
  pendingStatus='';const at=fence(),wasUncertain=pending.sending();store();sync();
  try{const result=await o.api<Outcome>(command.path,{...command.body,operationId:command.operationId});if(current(at)&&pending.command===command)await confirmed(command,result);}
  catch(error){if(!current(at))return;pending.failed((error as {code?:string}).code,wasUncertain);store();if(['access_denied','facility_quantity_denied'].includes((error as {code?:string}).code??''))withhold();sync();throw error;}
 }
 async function record(command:Record<string,unknown>,label:string){
  const kind=command.kind==='allocate'?'plan':['adopt','retract_adoption'].includes(String(command.kind))?'adoption':command.kind;
  if([...formEdits].some(([name,form])=>name!==kind&&form.edited())&&!confirm('Saving this record reloads the ledger and discards unsaved changes in the other facility forms. Continue?'))return;
  if(!directory)throw Error('Load the facility account before recording.');
  const body={ownerId:directory.ownerId,context:o.context(),effectiveAt:await effective.instant(),command};
  const request=pending.begin('facilities/quantities/record',body,undefined,label);store();sync();await send(request);
 }
 get<HTMLFormElement>('facility-create-form').onsubmit=e=>{e.preventDefault();void o.act(async()=>{
  const target=directory?.choices.find(c=>createKey(c)===select('facility-create-target').value);if(!target)throw Error('Choose a booked delivery cell first.');
  await record({kind:'create',obligationId:target.obligationId,day:target.day,facility:target.pointCode},'Create facility ledger');
 });};
 for(const kind of ['source','limit'] as const)get<HTMLFormElement>(`facility-${kind}-form`).onsubmit=e=>{e.preventDefault();void o.act(async()=>{
  if(!view)throw Error('Choose a facility ledger first.');
  await record({kind,ledgerId:view.facts.ledger.id,expectedPreviousId:view.facts[kind]?.id??null,
   quantity:kind==='limit'&&input('facility-limit-remove').checked?null:input(`facility-${kind}-quantity`).value,evidence:input(`facility-${kind}-evidence`).value},kind==='source'?'Record facility quantity':'Configure operating limit');
 });};
 get<HTMLFormElement>('facility-plan-form').onsubmit=e=>{e.preventDefault();void o.act(async()=>{
  if(!view?.facts.source)throw Error('Record a reported quantity first.');
  await record({kind:'allocate',ledgerId:view.facts.ledger.id,expectedPreviousId:view.facts.plan?.id??null,sourceRevisionId:view.facts.source.id,
   allocations:allocations(),evidence:input('facility-plan-evidence').value},'Replace allocation plan');
 });};
 async function adoption(withdraw:boolean){
  if(!view?.facts.plan||!view.facts.source)throw Error('Load a saved allocation plan first.');
  const {facts}=view,prior=facts.adoption;
  if(!withdraw&&(formEdits.get('plan')!.edited()||formEdits.get('source')!.edited()))throw Error('Save or discard reported-quantity and allocation edits before accepting final inputs.');
  if(withdraw&&prior?.state!=='adopted')throw Error('There is no accepted final input to withdraw.');
  if(!input('facility-adoption-evidence').reportValidity())return;
  const review=withdraw?'Withdraw the current final inputs for every trade in the accepted plan? The operational plan stays recorded.':`Accept saved plan revision ${facts.plan!.revision} with ${facts.allocations.length} allocations as final quantity inputs? Saved allocations:\n${facts.allocations.map(a=>`${a.counterparty} · ${a.sideCode}: ${a.quantity} ${facts.labels.unit}`).join('\n')}`;
  if(!confirm(review))return;
  await record({kind:withdraw?'retract_adoption':'adopt',ledgerId:facts.ledger.id,planRevisionId:withdraw?prior!.plan_id:facts.plan!.id,
   sourceRevisionId:withdraw?prior!.source_revision_id:facts.source!.id,expectedPreviousId:prior?.id??null,evidence:input('facility-adoption-evidence').value},withdraw?'Withdraw final acceptance':'Accept final quantities');
 }
 get<HTMLFormElement>('facility-adoption-form').onsubmit=e=>{e.preventDefault();void o.act(()=>adoption(false));};
 get('facility-retract-adoption').onclick=()=>void o.act(()=>adoption(true));
 get('facility-preview').onclick=()=>void o.act(async()=>{
  if(!view?.facts.source)throw Error('Record a reported quantity first.');const at=fence();
  const result=await o.api<{balance:Balance}>('facilities/quantities/preview',{ownerId:view.ownerId,context:o.context(),ledgerId:view.facts.ledger.id,sourceRevisionId:view.facts.source.id,expectedPreviousId:view.facts.plan?.id??null,allocations:allocations()});
  if(current(at))set('facility-preview-result',`Allocated ${result.balance.allocated} · unallocated ${result.balance.available} ${view.facts.labels.unit}. Operating assessment: ${result.balance.constraint.replaceAll('_',' ')}.`);
 });
 select('facility-targets').addEventListener('change',()=>{edits.mark();formEdits.get('plan')!.mark();renderAllocations();set('facility-preview-result','');o.changed();});
 input('facility-limit-remove').onchange=sync;
 select('facility-ledger').onchange=()=>{const id=select('facility-ledger').value;if(edits.edited()&&!confirm('Discard unsaved facility edits?')){select('facility-ledger').value=selected;return;}selected=id;effective.clear();void o.act(()=>load(id));};
 get('facility-refresh').onclick=()=>{if(!edits.edited()||confirm('Discard unsaved facility edits and refresh?'))void o.act(()=>load());};
 get('facility-retry').onclick=()=>void o.act(async()=>{if(pending.command)await send(pending.command);});
 get('facility-status').onclick=()=>void o.act(async()=>{
  const command=pending.command;if(!command)return;const at=fence();
  const result=await o.api<Outcome|{found:false}>('facilities/quantities/status',{ownerId:command.body.ownerId,operationId:command.operationId});if(!current(at)||pending.command!==command)return;
  if('found'in result){pendingStatus='No completed receipt is available yet. Retry the original request with its retained identity.';sync();}else await confirmed(command,result);
 });
 get('facility-discard').onclick=()=>{if(!o.busy()&&confirm('Discard this request only after checking its status and the ledger history. Continue?')){pending.clear();pendingStatus='';store();sync();}};
 get('facility-forms').addEventListener('input',()=>{sync();o.changed();});get('facility-forms').addEventListener('change',()=>{sync();o.changed();});
 return{load,sync,edited:()=>edits.edited()||!!pending.command,
  leave:()=>!!pending.command||!edits.edited()||confirm('Discard unsaved facility edits?'),
  refresh(){effective.refresh();if(view){renderAllocations();history();}},
  reset(){generation++;directory=undefined;selected='';pending.clear();pendingStatus='';clearEdits();effective.clear();clearView();select('facility-ledger').replaceChildren(new Option('Choose a ledger…',''));select('facility-create-target').replaceChildren(new Option('Choose a booked delivery…',''));set('facility-context','');set('facility-saved','');get('facility-empty').hidden=false;sync();}};
}
