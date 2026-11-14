import {renderDataGrid,emptyGridLayout,type GridHandle,type GridLayout} from './data-grid.js';
import {actorTransport,PendingCommand,type RequestCommand} from './pending-command.js';
import {askInPanel} from './side-panel.js';
import {zonedInput} from './user-time.js';
import {mountLifecycleSettings} from './lifecycle-settings.js';
import {lifecycleKey,lifecycleRows,lifecycleReason,lifecycleRequest,restoreLifecycleRequest,type LifecycleDiscovery,type LifecycleOffer,type LifecycleDraft,type LifecycleRequest} from './lifecycle-model.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Review={hash:string;offer:LifecycleOffer;details:Record<string,unknown>};
const words=(value:string)=>value.replaceAll('_',' ').replaceAll('.',' · ').replace(/([a-z])([A-Z])/g,'$1 $2');
const title=(value:string)=>{const text=words(value);return text.charAt(0).toUpperCase()+text.slice(1);};
const knownRejection=(code:string|undefined)=>!!code&&/^(lifecycle_|option_|futures_|swap_|gas_|settlement_|historical_|access_denied$|request_denied$)/.test(code);
const element=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};

export function setupLifecycle(root:HTMLElement,o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;context:()=>Record<string,unknown>;historical:()=>boolean;zone:()=>string;permission:(name:string)=>boolean;changed:()=>void}){
 root.dataset.ownControls='true';root.classList.add('lifecycle-workspace');
 root.innerHTML=`<div id="${root.id}-prompt" class="side-panel-prompt" role="alertdialog" hidden></div>
 <div class="operations-directory-heading"><div><h2>Lifecycle actions</h2><p>Actions and settings at the selected market close.</p></div><button data-lifecycle="refresh" type="button" class="secondary">Refresh</button></div>
 <div class="lifecycle-tools"><label><input data-lifecycle="inactive" type="checkbox"> Show closed and inactive actions</label><div><button data-lifecycle="products" type="button" class="secondary">Product settings</button> <button data-lifecycle="settings" type="button" class="secondary">Action policies</button></div></div>
 <div data-lifecycle="pending" class="operations-recovery" hidden><p data-lifecycle="pending-text" role="status"></p><button data-lifecycle="retry" type="button">Retry original request</button></div>
 <p data-lifecycle="message" role="status"></p><div data-lifecycle="rows"></div><section data-lifecycle="panel" class="lifecycle-panel" aria-live="polite"></section>`;
 const get=<T extends HTMLElement>(name:string)=>root.querySelector<T>(`[data-lifecycle="${name}"]`)!;
 const panel=get('panel'),message=get('message'),host=get('rows');
 const pending=new PendingCommand(knownRejection),transports=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
 let identity='',generation=0,draftVersion=0,panelVersion=0,reading=false,saving=false,edited=false,discovery:LifecycleDiscovery|undefined,selected:LifecycleOffer|undefined,shown:LifecycleOffer[]=[],reviewed:{request:LifecycleRequest;review:Review}|undefined;
 let grid:GridHandle|undefined,layout:GridLayout=emptyGridLayout(),filters:{query:string;columns:(string|null)[]}|undefined,asking:AbortController|undefined;
 let timeControls:ReturnType<typeof zonedInput>[]=[];
 let productSettings:ReturnType<typeof mountLifecycleSettings>|undefined;
 const busy=()=>o.busy()||reading||saving;
 const instant=(at:string)=>new Intl.DateTimeFormat(undefined,{timeZone:o.zone(),dateStyle:'medium',timeStyle:'short'}).format(new Date(at));
 function transport(){const actor=o.identity();let value=transports.get(actor);if(!value)transports.set(actor,value=actorTransport<RequestCommand>(()=>'lifecycle-request:'+actor,()=>sessionStorage));return value;}
 function restore(){if(identity===o.identity())return;pending.clear();identity=o.identity();layout=emptyGridLayout();filters=undefined;const saved=restoreLifecycleRequest(transport().read());if(saved)pending.command={...saved,uncertain:true};}
 function store(){transport().write(pending.command);o.changed();}
 const current=(epoch:number,actor:string)=>generation===epoch&&o.identity()===actor;
 const canExecute=()=>!!selected&&!!discovery?.choices.actions.some(a=>a.code===selected!.action&&o.permission(a.executionPermission));
 function invalidate(){draftVersion++;reviewed=undefined;edited=true;o.changed();panel.querySelector('[data-lifecycle-review]')?.remove();sync();}
 function field(label:string,input:HTMLElement,parent:HTMLElement){const wrapper=element('label',label);wrapper.append(input);parent.append(wrapper);return input;}
 function choices(label:string,values:{value:string;label:string}[],parent:HTMLElement,value=''){const input=element('select');input.required=true;input.append(new Option('Choose…',''),...values.map(v=>new Option(v.label,v.value)));input.value=value;field(label,input,parent);return input;}
 function textField(label:string,parent:HTMLElement,value='',type='text'){const input=element('input');input.type=type;input.value=value;field(label,input,parent);return input;}
 function timeField(label:string,parent:HTMLElement){const input=textField(label,parent,'','datetime-local');input.step='1';const control=zonedInput(input,o.zone,o.api);timeControls.push(control);const zone=element('small',o.zone());input.parentElement!.append(zone);return{input,control};}
 function clearPanel(){draftVersion++;panelVersion++;productSettings=undefined;panel.replaceChildren();timeControls=[];reviewed=undefined;edited=false;selected=undefined;o.changed();}
 function sync(){restore();get('pending').hidden=!pending.command;if(pending.command)get('pending-text').textContent=`${pending.command.label??'Lifecycle change'} has an unconfirmed outcome. Retry sends the original request.`;
  get<HTMLButtonElement>('retry').disabled=busy()||!pending.command||o.historical();get<HTMLButtonElement>('refresh').disabled=busy()||!!pending.command;get<HTMLButtonElement>('settings').disabled=busy()||!!pending.command||!discovery;
  get<HTMLButtonElement>('products').disabled=busy()||!!pending.command||!discovery;
  get<HTMLInputElement>('inactive').disabled=busy()||!!pending.command;host.inert=busy()||!!pending.command;
  const denied=(node:HTMLElement)=>{const permission=node.closest<HTMLElement>('[data-lifecycle-permission]')?.dataset.lifecyclePermission;return permission!==undefined&&(!permission||!o.permission(permission));};
  for(const button of panel.querySelectorAll<HTMLButtonElement>('button'))button.disabled=busy()||!!pending.command||o.historical()||denied(button);
  const execute=panel.querySelector<HTMLButtonElement>('[data-lifecycle-execute]');if(execute)execute.disabled=busy()||!!pending.command||!reviewed||o.historical()||!canExecute();
  for(const form of panel.querySelectorAll('form'))form.inert=busy()||!!pending.command||o.historical()||denied(form);
 }
 async function confirmDiscard(){if(asking)return false;const controller=asking=new AbortController();try{return await askInPanel(root,{text:'Discard the lifecycle edits? An unconfirmed request remains retained.',keep:'Keep editing',go:'Discard edits'})(controller.signal);}finally{if(asking===controller)asking=undefined;}}
 async function leave(){if(!edited&&!productSettings?.edited())return true;const go=await confirmDiscard();if(go)clearPanel();return go;}
 function clearGrid(){if(grid){filters=grid.state();if(grid.ready())layout=grid.layout();grid.destroy();grid=undefined;}host.replaceChildren();}
 function draw(){clearGrid();shown=(discovery?.actions??[]).filter(offer=>get<HTMLInputElement>('inactive').checked||!['option_holding_closed','lifecycle_action_window','option_instruction_cutoff','option_instruction_closed','option_exercise_window'].includes(offer.reason??''));
  if(!shown.length){host.textContent='No lifecycle actions are available in this context.';return;}
  const rows=lifecycleRows(shown,instant);
  grid=renderDataGrid(host,'lifecycle-actions',o.identity(),['Holding','Product','Book','Action','Event time','Status'],rows,[],busy,{keys:['holding','product','book','action','at','status'],rowKeys:shown.map(lifecycleKey),layout,initial:filters,onLayout:next=>layout=next,onRowOpen:async index=>{if(!await leave())return false;showAction(shown[index]);return true;},cells:(index,column)=>{
   if(shown[index].eligible)return null;const text=element('span',rows[index][column]);text.className='lifecycle-inactive';text.title=lifecycleReason(shown[index].reason);return text;
  }});
 }
 async function send(command:RequestCommand){const actor=o.identity(),epoch=generation,wasUncertain=pending.sending();saving=true;store();sync();try{
   const outcome=await o.api<{operationId?:string;changed?:boolean;semanticAcknowledgement?:boolean;acknowledgedRevision?:number;acknowledgedVersion?:number;currentRevision?:number;currentVersion?:number}>(command.path,command.body);
   if(outcome.operationId!==command.operationId)throw Error('The reply did not identify the retained request. Retry the original request.');
   if(!current(epoch,actor))return;pending.clear();store();clearPanel();
   const accepted=outcome.acknowledgedRevision??outcome.acknowledgedVersion,head=outcome.currentRevision??outcome.currentVersion;
   message.textContent=outcome.semanticAcknowledgement===true||outcome.changed===false?
    `Existing fact acknowledged${accepted!==undefined?' · revision '+accepted:''}${head!==undefined?' · current revision '+head+' retained':''}.`:'Lifecycle change recorded.';
   saving=false;await load(false);
  }catch(error){if(current(epoch,actor)){pending.failed((error as {code?:string}).code,wasUncertain);store();if(!pending.command)invalidate();message.textContent=String(error);}throw error;}finally{if(o.identity()===actor){saving=false;sync();}}
 }
 async function queue(path:string,body:Record<string,unknown>,label:string){restore();if(pending.command)throw Error('Reconcile the retained lifecycle request first.');pending.command={path,operationId:String(body.operationId),body:structuredClone(body),label};store();await send(pending.command);}
 function showReview(review:Review,request:LifecycleRequest){panel.querySelector('[data-lifecycle-review]')?.remove();const section=element('section');section.dataset.lifecycleReview='';section.className='lifecycle-review';section.append(element('h3','Review '+(selected?.label??'action')));
  const list=element('dl');list.className='lifecycle-review-facts';
  const add=(label:string,value:unknown)=>{if(value===null||value===undefined||value==='')return;list.append(element('dt',label),element('dd',String(value)));};
  add('Holding',selected?.subject?.reference);add('Business date',request.context.businessDate);add('As of',instant(request.context.asOf));
  const d=review.details;for(const [key,label] of [['decision','Decision'],['openLots','Open lots'],['lots','Lots affected'],['futureLots','Linked futures lots'],['strike','Strike'],['settlement','Settlement'],['amount','Amount'],['roundedTotal','Rounded payment'],['exactTotal','Exact payment'],['reference','Evidence reference']] as const)add(label,d[key]);
  section.append(list);
  const steps=Array.isArray(d.steps)?d.steps:[];
  if(steps.length){const table=element('table');table.className='regular-table';const head=element('tr');for(const label of ['Occurrence','Variation','Adjustment','Due'])head.append(element('th',label));table.append(head);
   for(const step of steps as Record<string,unknown>[]){const row=element('tr'),occurrence=step.occurrence as Record<string,unknown>|undefined,variation=step.variation as Record<string,unknown>|undefined;for(const value of [typeof occurrence?.at==='string'?instant(occurrence.at):'',variation?.amount??'',step.adjustment??'',typeof step.adjustmentDue==='string'?instant(step.adjustmentDue):''])row.append(element('td',typeof value==='object'?exactText(value):String(value)));table.append(row);}section.append(table);
  }
  const principal=d.physicalPrincipal as {originalQuantity?:unknown;finalPrice?:unknown;currency?:{code?:string};principalDueAt?:string;performances?:Record<string,unknown>[]} | undefined;
  if(principal){
   section.append(element('h4','Physical delivery principal'));
   const currency=principal.currency?.code?.toUpperCase();
   const terms=element('p',`Original quantity ${String(principal.originalQuantity??'')} · final price ${String(principal.finalPrice??'')}${currency?' '+currency:''} · principal due ${principal.principalDueAt?instant(principal.principalDueAt):''}`);section.append(terms);
   if(principal.performances?.length){const table=element('table');table.className='regular-table';const head=element('tr');for(const label of ['Performance','Effective','Signed quantity','Principal','Adjustment'])head.append(element('th',label));table.append(head);
    for(const performance of principal.performances){const row=element('tr');for(const value of [performance.businessReference,typeof performance.at==='string'?instant(performance.at):'',performance.quantity,performance.contribution,performance.adjustment])row.append(element('td',String(value??'')));table.append(row);}section.append(table);
   }else section.append(element('p','No accepted performance: this final creates or updates the delivery obligation without recording title.'));
  }
  const execute=element('button','Record reviewed action');execute.type='button';execute.dataset.lifecycleExecute='';execute.onclick=()=>void o.act(async()=>{if(!reviewed)return;await queue('lifecycle/execute',reviewed.request,`${selected?.label??'Lifecycle action'} · ${selected?.subject?.reference??'holding'}`);});section.append(execute);panel.append(section);sync();
 }
 function showAction(offer:LifecycleOffer){clearPanel();selected=offer;panel.append(element('h2',`${offer.label} · ${offer.subject?.reference??'Holding'}`));panel.append(element('p',lifecycleReason(offer.reason)));
  if(!offer.selection){const setup=element('button','Configure lifecycle settings');setup.type='button';setup.onclick=()=>showProductSettings(offer);panel.append(setup);sync();return;}
  if(!canExecute()){panel.append(element('p','Your current permissions allow inspection of this action. Recording it requires the action’s execution permission.'));sync();return;}
  const form=element('form');form.className='lifecycle-form';form.dataset.lifecyclePermission=discovery!.choices.actions.find(a=>a.code===offer.action)!.executionPermission;const fields=element('div');fields.className='schedule-fields';form.append(fields);panel.append(form);
  const policies=(discovery?.policies??[]).filter(p=>p.action===offer.action);
  const policy=choices('Action policy',policies.map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${title(p.mode)} · ${instant(p.from_at)} to ${instant(p.until_at)} · revision ${p.revision}`})),fields,offer.policyId&&offer.policyRevision?JSON.stringify([offer.policyId,offer.policyRevision]):'');
  const inputs=new Map<string,HTMLInputElement|HTMLSelectElement>(),times=new Map<string,ReturnType<typeof zonedInput>>();
  for(const name of offer.requiredInputs){if(['policyId','correctionDue'].includes(name))continue;
   if(name==='correctionDueAt'){const timing=timeField('Correction due time (leave blank for an original determination)',fields);inputs.set(name,timing.input);times.set(name,timing.control);continue;}
   if(name==='effectiveAt'){const timing=timeField('Observed payment time',fields);inputs.set(name,timing.input);times.set(name,timing.control);continue;}
   const labels:Record<string,string>={evidenceId:'Observed evidence ID',lots:`Lots${offer.availableQuantity?' · open '+offer.availableQuantity:''}`,determinationId:'Determination',amount:'Observed signed payment',reference:'Payment reference'};
   const available=offer.inputChoices?.[name],input=available?choices(labels[name]??title(name),available,fields):textField(labels[name]??title(name),fields);
   input.required=true;inputs.set(name,input);
  }
  const correctionRows:{select:HTMLSelectElement;time:ReturnType<typeof zonedInput>}[]=[];
  if(offer.requiredInputs.includes('correctionDue')){const box=element('fieldset'),legend=element('legend','Correction due times');box.append(legend,element('p','For a correction, include each affected occurrence whose cash changes. An original run needs no correction rows.'));
   const add=element('button','Add correction');add.type='button';add.className='secondary';add.onclick=()=>{const row=element('div');row.className='schedule-fields';const options=(discovery?.actions??[]).filter(a=>a.target.scopeId===offer.target.scopeId&&a.target.bindingId===offer.target.bindingId&&a.action===offer.action);const selected=choices('Occurrence',options.map(a=>({value:a.occurrenceId,label:instant(a.at)})),row);const timing=timeField('Correction due',row);const remove=element('button','Remove');remove.type='button';remove.onclick=()=>{correctionRows.splice(correctionRows.indexOf(item),1);row.remove();invalidate();};row.append(remove);box.insertBefore(row,add);const item={select:selected,time:timing.control};correctionRows.push(item);invalidate();};box.append(add);form.append(box);
  }
  const preview=element('button','Review action');preview.type='submit';form.append(preview);form.addEventListener('input',invalidate);form.addEventListener('change',invalidate);
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{if(!discovery)throw Error('Refresh lifecycle actions.');const source=discovery.context,actor=o.identity(),epoch=generation,version=draftVersion;const [policyId,policyRevision]=policy.value?JSON.parse(policy.value):['',0];const draft:LifecycleDraft={policyId,policyRevision,correctionDue:[]};
   for(const [name,input] of inputs){(draft as unknown as Record<string,unknown>)[name]=times.has(name)?input.value?await times.get(name)!.instant():null:input.value;}
   for(const row of correctionRows)draft.correctionDue!.push({occurrenceId:row.select.value,dueAt:await row.time.instant()});
   if(!current(epoch,actor)||selected!==offer||version!==draftVersion)return;const request=lifecycleRequest(source,offer,draft,crypto.randomUUID());const result=await o.api<Review>('lifecycle/preview',request);if(!current(epoch,actor)||selected!==offer||version!==draftVersion)return;
   if(!result.offer||lifecycleKey(result.offer)!==lifecycleKey(offer)||result.offer.eligible!==true)throw Error('The review did not retain the selected native action and occurrence. Refresh and review again.');
   if(!/^[a-f0-9]{64}$/.test(result.hash))throw Error('The review did not retain its source hash.');request.previewHash=result.hash;reviewed={request,review:result};showReview(result,request);
  });};sync();
 }
 function showPolicies(){clearPanel();if(!discovery)return;panel.append(element('h2','Action policy'));const form=element('form');form.className='lifecycle-form';form.dataset.lifecyclePermission='';const fields=element('div');fields.className='schedule-fields';form.append(fields);panel.append(form);
  const available=discovery.choices.actions.filter(a=>o.permission(a.configurationPermission));const action=choices('Action',available.map(a=>({value:a.code,label:title(a.code)})),fields);
  const saved=choices('Policy',[],fields),mode=choices('Execution',[],fields),from=timeField('Available from',fields),until=timeField('Available until (exclusive)',fields);
  function policyChoices(){form.dataset.lifecyclePermission=available.find(a=>a.code===action.value)?.configurationPermission??'';saved.replaceChildren(new Option('Create a new policy','new'),...(discovery?.policies??[]).filter(p=>p.action===action.value).map(p=>new Option(`${title(p.mode)} · ${instant(p.from_at)} to ${instant(p.until_at)} · revision ${p.revision}`,JSON.stringify([p.id,p.revision]))));saved.value='new';const automatic=available.find(a=>a.code===action.value)?.automatic;mode.replaceChildren(new Option('Choose…',''),...(discovery?.choices.modes??[]).filter(m=>m!=='automatic'||automatic&&o.permission('RunJobs')).map(m=>new Option(title(m),m)));from.control.clear();until.control.clear();invalidate();}
  action.onchange=policyChoices;saved.onchange=()=>{if(saved.value==='new'){from.control.clear();until.control.clear();mode.value='';return;}const [id,revision]=JSON.parse(saved.value),p=discovery?.policies.find(p=>p.id===id&&p.revision===revision);if(p){mode.value=p.mode;from.control.set(p.from_at);until.control.set(p.until_at);}invalidate();};
  const save=element('button','Save policy');save.type='submit';form.append(save);form.addEventListener('input',invalidate);form.addEventListener('change',invalidate);
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{if(!discovery)throw Error('Refresh lifecycle actions.');if(!action.value||!mode.value)throw Error('Choose the action and execution policy.');const actor=o.identity(),epoch=generation,version=draftVersion,implementation=discovery.choices.policyImplementation,[policyId,expectedRevision]=saved.value==='new'?[crypto.randomUUID(),0]:JSON.parse(saved.value);const body={operationId:crypto.randomUUID(),policyId,expectedRevision,action:action.value,mode:mode.value,fromAt:await from.control.instant(),untilAt:await until.control.instant(),implementation};if(!current(epoch,actor)||version!==draftVersion)return;await queue('lifecycle/policies/save',body,'Save '+title(action.value)+' policy');});};
  panel.append(element('p','Saving a policy does not assign it to a holding. Product settings select the exact saved policy version.'));edited=false;sync();
 }
 function showProductSettings(offer?:LifecycleOffer){clearPanel();if(!discovery)return;const actor=o.identity(),epoch=generation,view=panelVersion;
  productSettings=mountLifecycleSettings(panel,discovery,{api:o.api,act:o.act,zone:o.zone,permission:o.permission,current:()=>current(epoch,actor)&&view===panelVersion,confirmDiscard,changed:o.changed,
   queue:async(path,body,label)=>{if(current(epoch,actor)&&view===panelVersion)await queue(path,body,label);}},offer);sync();
 }
 async function load(announce=true){restore();const actor=o.identity(),epoch=++generation;reading=true;clearPanel();clearGrid();discovery=undefined;if(announce)message.textContent='Loading lifecycle actions…';sync();try{
   const value=await o.api<LifecycleDiscovery>('lifecycle/view',o.context());if(!current(epoch,actor))return;discovery=value;draw();if(announce)message.textContent='';
  }catch(error){if(current(epoch,actor)){message.textContent=String(error);host.textContent='';}throw error;}finally{if(current(epoch,actor)){reading=false;sync();}}
 }
 get('refresh').onclick=()=>void o.act(async()=>{if(await leave())await load();});get('settings').onclick=()=>void o.act(async()=>{if(await leave())showPolicies();});get('products').onclick=()=>void o.act(async()=>{if(await leave())showProductSettings();});get<HTMLInputElement>('inactive').onchange=draw;
 get('retry').onclick=()=>void o.act(async()=>{if(pending.command)await send(pending.command);});
 return {load,sync,leave,edited:()=>edited||!!productSettings?.edited()||!!pending.command,shown:()=>selected?{target:selected.target,action:selected.action}:undefined,
  reset(){generation++;reading=false;saving=false;asking?.abort();asking=undefined;clearGrid();clearPanel();discovery=undefined;pending.clear();identity='';message.textContent='';},
  async refresh(){for(const time of timeControls)time.refresh();productSettings?.refresh();if(await leave())await load();}};
}
function exactText(value:unknown):string{if(value===null||value===undefined)return '';if(typeof value!=='object')return String(value);const v=value as Record<string,unknown>;if(typeof v.amount==='string')return v.amount;if(v.numerator!==undefined&&v.denominator!==undefined)return `${v.numerator}/${v.denominator}`;return '';}
