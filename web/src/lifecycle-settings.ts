import {zonedInput} from './user-time.js';
import type {LifecycleDiscovery,LifecycleOffer,LifecycleMarginPolicy,LifecycleFutureSetup} from './lifecycle-model.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
const element=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};
const labels:Record<string,string>={manual:'Manual',review:'Review before recording',automatic:'Automatic',include_opening_instant:'Include settlement at execution time',after_opening_instant:'Start after execution time',to_even:'Nearest increment · ties to even',away_from_zero:'Nearest increment · ties away from zero',payment_total:'Round the held payment total',held_binding_total_v1:'Each held binding total',held_binding_difference_payment_total_v1:'Changes in each held binding’s rounded payment total',nominal_mark_v1:'Nominal marked value',currency_curve_v1:'Discount with the captured currency curve',gas_future_physical_delivery_v1:'Gas delivery with separate ownership and principal cash',delivery_cumulative_principal_total_v1:'Round cumulative delivered consideration',contractual_principal_due_v1:'Keep the contractual principal due time',daily:'Daily settlement',final:'Final settlement',native_execution_inception_v1:'Start from the booked execution',currency_minor_units_to_even_v1:'Currency minor units · ties to even',swap_due_nominal_v1:'Contractual due time · nominal when overdue',swap_completed_period_trading_pnl_v1:'Recognize the completed payment period',swap_explicit_correction_due_v1:'Choose a due time for each correction'};
const label=(value:string)=>labels[value]??value.replaceAll('_',' ');

/** Typed settings over native source choices. This component edits selections;
 * the backend validates schedules, units, eligibility and financial conventions. */
export function mountLifecycleSettings(root:HTMLElement,data:LifecycleDiscovery,o:{api:Api;zone:()=>string;permission:(name:string)=>boolean;act:(work:()=>Promise<void>)=>Promise<void>;queue:(path:string,body:Record<string,unknown>,label:string)=>Promise<void>;current:()=>boolean;confirmDiscard:()=>Promise<boolean>;changed:()=>void},initial?:LifecycleOffer){
 const times:ReturnType<typeof zonedInput>[]=[];let edited=false,version=0;
 const instant=(at:string)=>new Intl.DateTimeFormat(undefined,{timeZone:o.zone(),dateStyle:'medium',timeStyle:'short'}).format(new Date(at));
 function field(name:string,input:HTMLElement,parent:HTMLElement){const wrapper=element('label',name);wrapper.append(input);parent.append(wrapper);return input;}
 function select(name:string,values:{value:string;label:string}[],parent:HTMLElement,value=''){const input=element('select');input.required=true;input.append(new Option('Choose…',''),...values.map(v=>new Option(v.label,v.value)));input.value=value;field(name,input,parent);return input;}
 const policyLabel=(p:LifecycleMarginPolicy)=>`${p.series_code} · ${p.settlement_curve_name} · revision ${p.revision} · ${instant(p.from_at)} to ${instant(p.until_at)}`;
 const native=(name:string,values:string[],parent:HTMLElement,value='')=>select(name,values.map(value=>({value,label:label(value)})),parent,value);
 function input(name:string,parent:HTMLElement,value=''){const control=element('input');control.value=value;control.required=true;field(name,control,parent);return control;}
 function time(name:string,parent:HTMLElement,value?:string){const control=input(name,parent);control.type='datetime-local';control.step='1';const resolved=zonedInput(control,o.zone,o.api);times.push(resolved);if(value)resolved.set(value);control.parentElement!.append(element('small',o.zone()));return resolved;}
 function save(form:HTMLFormElement,text:string,work:(current:()=>boolean)=>Promise<void>){const button=element('button',text);button.type='submit';button.disabled=!form.dataset.lifecyclePermission||!o.permission(form.dataset.lifecyclePermission);form.inert=button.disabled;form.append(button);form.onsubmit=event=>{event.preventDefault();const revision=version,current=()=>o.current()&&revision===version;void o.act(async()=>{if(current()&&form.dataset.lifecyclePermission&&o.permission(form.dataset.lifecyclePermission))await work(current);});};}
 function form(parent:HTMLElement,permission:string){const result=element('form');result.className='lifecycle-form';result.dataset.lifecyclePermission=permission;parent.append(result);result.addEventListener('input',()=>{version++;edited=true;o.changed();});result.addEventListener('change',()=>{version++;edited=true;o.changed();});return result;}
 function fields(parent:HTMLElement){const result=element('div');result.className='schedule-fields';parent.append(result);return result;}
 function actionPolicy(action:string,parent:HTMLElement,id?:unknown,revision?:unknown){return select('Action policy',data.policies.filter(p=>p.action===action).map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${label(p.mode)} · revision ${p.revision} · ${instant(p.from_at)} to ${instant(p.until_at)}`})),parent,id&&revision?JSON.stringify([id,Number(revision)]):'');}
 function chosenPolicy(control:HTMLSelectElement){if(!control.value)throw Error('Choose an exact saved action policy.');const [policyId,policyRevision]=JSON.parse(control.value);return{policyId,policyRevision};}
 function switchDraft(control:HTMLSelectElement,previous:string,render:(next:string)=>void){const next=control.value;control.value=previous;if(next===previous)return;
  const apply=()=>{version++;control.value=next;render(next);edited=false;o.changed();};
  if(!edited){apply();return;}const revision=version;void o.act(async()=>{if(await o.confirmDiscard()&&o.current()&&revision===version)apply();});
 }
 const marginPolicies=data.marginPolicies as LifecycleMarginPolicy[];
 root.append(element('h2','Product lifecycle settings'));
 const offers=[...new Map(data.actions.map(offer=>[JSON.stringify([offer.target,offer.action]),offer])).values()];
 const futureChoices=(data.futuresSetup??[]).map(future=>({future,reference:`TR-${String(future.trade_reference).padStart(6,'0')}`}));
 const target=select('Holding and action',[
  ...offers.filter(a=>a.target.handler!=='futures').map((offer,index)=>({value:'action:'+index,label:`${offer.subject?.reference??'Holding'} · ${offer.label}`})),
  ...futureChoices.map((choice,index)=>({value:'future:'+index,label:`${choice.reference} · ${choice.future.product_name} · futures settlement`}))
 ],root);
 const content=element('section');root.append(content);
 function option(offer:LifecycleOffer){
  const latest=data.optionProfiles.filter(p=>p.trade_id===offer.target.scopeId&&p.action===offer.action).sort((a,b)=>Number(b.revision)-Number(a.revision))[0];
  const saved=form(content,data.choices.actions.find(a=>a.code===offer.action)?.configurationPermission??''),row=fields(saved);const policy=actionPolicy(offer.action,row,latest?.policy_id,latest?.policy_revision);
  const sources=(data.optionSources??[]).filter(s=>s.trade_id===offer.target.scopeId);
  const margin=select('Generated future margin policy',marginPolicies.filter(p=>sources.some(s=>s.definition_revision_id===p.definition_revision_id&&s.underlying_contract_id===p.contract_id)).map(p=>({value:JSON.stringify([p.id,p.revision]),label:policyLabel(p)})),row,latest?JSON.stringify([latest.margin_policy_id,Number(latest.margin_policy_revision)]):'');
  const mark=select('Generated future mark curve',sources.filter(s=>s.future_mark_eligible).map(s=>({value:s.curve_code,label:s.curve_name})),row,String(latest?.future_mark_curve??''));
  const expiry=offer.action==='option.expiry'?select('Official expiry settlement curve',sources.filter(s=>s.expiry_eligible).map(s=>({value:s.curve_code,label:s.curve_name})),row,String(latest?.expiry_curve??'')):null;
  save(saved,'Save option settings',async()=>{if(!margin.value||!mark.value||expiry&&!expiry.value)throw Error('Choose each exact linked future and expiry source.');const [marginPolicyId,marginPolicyRevision]=JSON.parse(margin.value);await o.queue('lifecycle/options/configure',{operationId:crypto.randomUUID(),tradeId:offer.target.scopeId,action:offer.action,expectedRevision:Number(latest?.revision??0),...chosenPolicy(policy),marginPolicyId,marginPolicyRevision,futureMarkCurve:mark.value,expiryCurve:expiry?.value??null},'Save option lifecycle settings');});
 }
 function swap(offer:LifecycleOffer){
  const saved=form(content,data.choices.actions.find(a=>a.code===offer.action)?.configurationPermission??''),row=fields(saved);const periods=(data.swapOccurrences??[]).filter(p=>p.trade_id===offer.target.scopeId&&p.binding_id===offer.target.bindingId);
  const period=select('Payment period',periods.map((p,index)=>({value:String(index),label:`Side ${Number(p.membership_ordinal)+1} · ${p.delivery_from} to ${p.delivery_to} · due ${instant(p.payment_at)}`})),row);
  const detail=element('div');saved.append(detail);
  let currentPeriod='';period.onchange=event=>{event.stopPropagation();switchDraft(period,currentPeriod,next=>{currentPeriod=next;renderPeriod();});};
  function renderPeriod(){detail.replaceChildren();saved.onsubmit=null;for(const button of saved.querySelectorAll('button[type=submit]'))button.remove();const occurrence=periods[Number(period.value)];if(!period.value||!occurrence)return;
   const latest=data.swapProfiles.filter(p=>p.trade_id===offer.target.scopeId&&p.action===offer.action&&p.occurrence_id===occurrence.occurrence_id).sort((a,b)=>Number(b.revision)-Number(a.revision))[0];
   const selected=fields(detail),policy=actionPolicy(offer.action,selected,latest?.policy_id,latest?.policy_revision),rounding=native('Cash rounding',data.choices.swapRounding,selected,String(latest?.rounding_policy??'')),due=native('Due payment treatment',data.choices.swapDue,selected,String(latest?.due_policy??'')),recognition=native('Recognition',data.choices.swapRecognition,selected,String(latest?.recognition_policy??'')),correction=native('Corrections',data.choices.swapCorrection,selected,String(latest?.correction_policy??''));
   saveButton();
   function saveButton(){for(const prior of saved.querySelectorAll('button[type=submit]'))prior.remove();save(saved,'Save payment period settings',async()=>{for(const control of [rounding,due,recognition,correction])if(!control.value)throw Error('Choose every payment policy.');await o.queue('lifecycle/swaps/configure',{operationId:crypto.randomUUID(),profileId:latest?.id??crypto.randomUUID(),tradeId:offer.target.scopeId,expectedRevision:Number(latest?.revision??0),originRevision:Number(occurrence.origin_revision),action:offer.action,...chosenPolicy(policy),membershipOrdinal:Number(occurrence.membership_ordinal),paymentOrdinal:Number(occurrence.payment_ordinal),deliveryFrom:occurrence.delivery_from,deliveryTo:occurrence.delivery_to,referenceCatalogId:occurrence.reference_catalog_id,referenceRevisionId:occurrence.reference_revision_id,currency:occurrence.currency_code,decimalPlaces:Number(occurrence.decimal_places),roundingPolicy:rounding.value,duePolicy:due.value,recognitionPolicy:recognition.value,correctionPolicy:correction.value},'Save swap payment period settings');});}
  }
 }
 function future(future:LifecycleFutureSetup){
  content.append(element('p',`${future.book_name} · ${future.series_code} · booked lots ${future.lots}`));
  const tasks=[{value:'adopt',label:'Use a saved margin policy'},
   ...o.permission('WriteMarketDefinitions')?[{value:'margin',label:'Create a margin policy'}]:[],
   ...future.settlement_method==='physical'&&o.permission('ConfigureGasOwnedHoldings')?[{value:'delivery',label:'Create or revise physical delivery terms'}]:[]];
  const task=select('Setup task',tasks,content),slot=element('section');content.append(slot);let currentTask='';
  task.onchange=()=>switchDraft(task,currentTask,next=>{currentTask=next;slot.replaceChildren();if(!tasks.some(t=>t.value===next))return;if(next==='adopt')adoptionSetup();else if(next==='margin')marginSetup();else if(next==='delivery')deliverySetup();});
  function adoptionSetup(){
  const adopt=form(slot,'AmendTrade'),row=fields(adopt);row.append(element('p',`${future.book_name} · ${future.series_code} · booked lots ${future.lots}`));
  const available=marginPolicies.filter(p=>p.definition_revision_id===future.definition_revision_id&&p.contract_id===future.contract_id);
  const policy=select('Saved margin policy',available.map(p=>({value:JSON.stringify([p.id,p.revision]),label:policyLabel(p)})),row,future.adopted_policy_id?JSON.stringify([future.adopted_policy_id,Number(future.adopted_policy_revision)]):'');
  const adoption=native('Settlement basis',data.choices.futures?.adoption??[],row);
  save(adopt,'Adopt saved margin policy',async()=>{if(!policy.value||!adoption.value)throw Error('Choose the saved policy and execution basis.');const [policyId,policyRevision]=JSON.parse(policy.value);await o.queue('lifecycle/futures/adopt',{operationId:crypto.randomUUID(),context:data.context,tradeId:future.trade_id,bindingId:future.binding_id,expectedRevision:Number(future.revision),policyId,policyRevision,adoption:adoption.value},'Adopt futures margin policy');});

  }
  function marginSetup(){
  if(!['cash','physical'].includes(future.settlement_method)){slot.append(element('p','Refresh the exact futures settlement terms before creating a margin policy.'));return;}
  slot.append(element('h3','Create a margin policy'));const edit=form(slot,'WriteMarketDefinitions'),settings=fields(edit),source=select('Official settlement curve',future.sources.map((s,index)=>({value:String(index),label:s.curve_name})),settings);
  const continuation=select('Settlement continuation',future.settlement_method==='cash'?[{value:'cash',label:'Cash settlement · no physical delivery'}]:(future.deliveryProfiles??[]).map(p=>({value:JSON.stringify([p.profile.id,p.profile.revision]),label:`${p.profile.name} · revision ${p.profile.revision}`})),settings);
  if(future.settlement_method==='physical')edit.append(element('p','Final settlement creates a delivery obligation. Title and physical performance require separately evidenced actions under the account’s current execution policy.'));
  const from=time('Available for adoption from',settings),until=time('Available for adoption until (exclusive)',settings),opening=native('First settlement',data.choices.futures?.opening??[],settings),increment=input('Cash rounding increment',settings),tie=native('Rounding ties',data.choices.futures?.ties??[],settings),stage=native('Rounding stage',data.choices.futures?.roundingStages??[],settings),scope=native('Rounding scope',data.choices.futures?.roundingScopes??[],settings);
  const occurrences=element('fieldset');occurrences.append(element('legend','Settlement occurrences'));edit.append(occurrences);
  const rows:{id:string;at:ReturnType<typeof zonedInput>;due:ReturnType<typeof zonedInput>;phase:HTMLSelectElement}[]=[];
  const add=element('button','Add settlement occurrence');add.type='button';add.className='secondary';add.onclick=()=>{const row=fields(occurrences),at=time('Settlement time',row),due=time('Payment due time',row),phase=native('Settlement type',data.choices.futures?.phases??[],row),item={id:crypto.randomUUID(),at,due,phase};rows.push(item);const remove=element('button','Remove');remove.type='button';remove.onclick=()=>{rows.splice(rows.indexOf(item),1);row.remove();version++;edited=true;o.changed();};row.append(remove);version++;edited=true;o.changed();};occurrences.append(add);
  save(edit,'Save new margin policy',async(current)=>{if(!source.value||!continuation.value||![opening,increment,tie,stage,scope].every(control=>control.value)||!rows.length)throw Error('Choose every settlement policy and add its complete explicit occurrences.');const selected=future.sources[Number(source.value)],occurrences=[];for(const row of rows){if(!row.phase.value)throw Error('Choose each settlement type.');occurrences.push({id:row.id,at:await row.at.instant(),dueAt:await row.due.instant(),phase:row.phase.value});}const policy={id:crypto.randomUUID(),definitionRevisionId:future.definition_revision_id,contractId:future.contract_id,settlementCurve:selected.curve_code,referenceCatalogId:selected.reference_catalog_id,referenceRevisionId:selected.reference_revision_id,currency:selected.currency_code,fromAt:await from.instant(),untilAt:await until.instant(),opening:opening.value,increment:increment.value,tie:tie.value,stage:stage.value,roundingScope:scope.value,implementation:data.choices.futures!.policyImplementation,occurrences,delivery:continuation.value==='cash'?null:(()=>{const [id,revision]=JSON.parse(continuation.value);return{id,revision};})()};if(!current())return;await o.queue('lifecycle/futures/policies/save',{operationId:crypto.randomUUID(),expectedRevision:0,context:data.context,policy},'Save futures margin policy');});
  }
  function deliverySetup(){
   const choices=data.choices.futures?.delivery;
   if(!choices){slot.append(element('p','Refresh the physical delivery settings.'));return;}
   const profiles=[...new Map([...(future.deliveryProfiles??[])].sort((a,b)=>a.profile.revision-b.profile.revision).map(p=>[p.profile.id,p])).values()];
   const selected=select('Delivery profile',[{value:'new',label:'Create new delivery terms'},...profiles.map(p=>({value:p.profile.id,label:`${p.profile.name} · revision ${p.profile.revision}`}))],slot,'new');
   const body=element('section');slot.append(body);let currentProfile='new';
   selected.onchange=()=>switchDraft(selected,currentProfile,next=>{currentProfile=next;renderProfile();});
   function renderProfile(){
    body.replaceChildren();const retained=profiles.find(p=>p.profile.id===selected.value),profile=retained?.profile,terms=retained?.gas;
    const edit=form(body,'ConfigureGasOwnedHoldings'),row=fields(edit),name=input('Delivery name',row,String(profile?.name??''));
    const implementation=native('Delivery implementation',choices!.implementations,row,String(profile?.implementation??''));
    const account=select('Ownership account',(future.deliveryAccounts??[]).map(a=>({value:a.id,label:`${a.code} · ${a.legal_entity_name} · ${a.book_name}`})),row,String(terms?.account_id??''));
    const from=time('Delivery starts',row,terms?.delivery_from as string|undefined),to=time('Delivery ends (exclusive)',row,terms?.delivery_to as string|undefined),due=time('Principal payment due',row,terms?.cash_due_at as string|undefined);
    const carrying=native('Delivery carrying value',choices!.carrying,row,String(terms?.carrying??'')),increment=input('Principal cash increment',row,String(terms?.principal_increment??'')),tie=native('Principal rounding ties',data.choices.futures!.ties,row,String(terms?.principal_tie??''));
    const scope=native('Principal calculation',choices!.principalScopes,row,String(terms?.principal_scope??'')),correction=native('Principal corrections',choices!.correctionDuePolicies,row,String(terms?.correction_due_policy??''));
    const policy=select('Enrollment policy',data.policies.filter(p=>p.action==='gas.futures.delivery').map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${label(p.mode)} · revision ${p.revision} · ${instant(p.from_at)} to ${instant(p.until_at)}`})),row,terms?JSON.stringify([terms.action_policy_id,Number(terms.action_policy_revision)]):'');
    edit.append(element('p','These terms describe a delivery obligation. They grant no title or execution authority. Performance uses the ownership account’s separately selected current action policy.'));
    save(edit,'Save delivery terms',async(current)=>{
     if(![name,implementation,account,carrying,increment,tie,scope,correction,policy].every(control=>control.value))throw Error('Choose every delivery term, ownership account and enrollment policy.');
     const action=chosenPolicy(policy),value={id:profile?.id??crypto.randomUUID(),name:name.value,implementation:implementation.value,definitionRevisionId:future.definition_revision_id,contractId:future.contract_id,accountId:account.value,
      fromAt:await from.instant(),toAt:await to.instant(),cashDueAt:await due.instant(),carrying:carrying.value,principalIncrement:increment.value,principalTie:tie.value,principalScope:scope.value,correctionDuePolicy:correction.value,actionPolicyId:action.policyId,actionPolicyRevision:action.policyRevision};
     if(!current())return;await o.queue('lifecycle/futures/delivery/configure',{operationId:crypto.randomUUID(),expectedRevision:Number(profile?.revision??0),context:data.context,profile:value},'Save physical futures delivery terms');
    });
   }
   renderProfile();
  }
 }
 let currentTarget='';target.onchange=()=>switchDraft(target,currentTarget,next=>{currentTarget=next;content.replaceChildren();const [kind,key]=next.split(':');if(kind==='future'){const selected=futureChoices[Number(key)];if(selected)future(selected.future);}else if(kind==='action'){const offer=offers.filter(a=>a.target.handler!=='futures')[Number(key)];if(offer?.target.handler==='listed_option')option(offer);else if(offer?.target.handler==='financial_swap')swap(offer);else content.append(element('p','This holding uses its native operational settings.'));}});
 if(initial){const index=offers.filter(a=>a.target.handler!=='futures').findIndex(a=>a.target.scopeId===initial.target.scopeId&&a.action===initial.action);if(index>=0){target.value='action:'+index;target.onchange(new Event('change'));}}
 return{edited:()=>edited,refresh(){for(const time of times)time.refresh();}};
}
