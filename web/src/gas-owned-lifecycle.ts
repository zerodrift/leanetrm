import {sameUtcInstant} from './user-time.js';
import {mountOperationFields,type OperationChoice,type OperationField} from './operation-fields.js';
import type {LifecycleContext,LifecycleOffer,LifecycleTarget} from './lifecycle-model.js';

export type GasChoice=OperationChoice;
export type GasField=OperationField;
export type GasForm={label:string;permission:string;fields:GasField[]};
export type GasPolicyChoice=GasChoice&{revision:number;action:string;accountId:string};
export type GasOwnedSetup={context:LifecycleContext;target?:LifecycleTarget;ownerId?:string;accounts:GasChoice[];policies:GasPolicyChoice[];forms:Record<string,GasForm>;configuration:Record<string,GasForm>};
export const gasSetupPath=(handler:string|undefined)=>handler==='gas_transport_financial'?'gas/transport/financial/setup':handler==='gas_park_custody'?'gas/park-custody/setup':'lifecycle/gas/setup';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type GasOwnedUiOptions={api:Api;zone:()=>string;permission:(name:string)=>boolean;current:()=>boolean;changed:()=>void;
 act:(work:()=>Promise<void>)=>Promise<void>;queue:(path:string,body:Record<string,unknown>,label:string)=>Promise<void>;confirmDiscard:()=>Promise<boolean>};
export type GasOwnedActionDraft={policyId:string;policyRevision:number;selection:Record<string,unknown>};
const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const value=document.createElement(tag);if(text!==undefined)value.textContent=text;return value;};


export function mountGasOwnedAction(root:HTMLElement,offer:LifecycleOffer,setup:GasOwnedSetup,o:GasOwnedUiOptions):{read:()=>Promise<GasOwnedActionDraft>;refresh:()=>void;dirty:()=>boolean;references:()=>{label:string;value:string;key?:string}[]}{
 const form=setup.forms[offer.action];if(!form)throw Error('Read the supported gas action fields for this source.');
 const label=node('label','Action policy'),policy=node('select');label.className='gas-lifecycle-policy';policy.name='policyId';policy.required=true;
 const policies=setup.policies.filter(p=>p.action===offer.action&&p.accountId===offer.target.scopeId);
 policy.append(new Option('Choose…',''),...policies.map(p=>new Option(p.label,p.id+':'+p.revision)));label.append(policy);root.append(label);
 const body=node('fieldset');body.className='schedule-fields gas-lifecycle-fields';body.dataset.lifecyclePermission=form.permission;body.disabled=!o.permission(form.permission);root.append(body);
 const editor=mountOperationFields(body,form.fields,o,async(target,action)=>{
  const sourceAction=action??offer.action;
  const read=await o.api<GasOwnedSetup>(gasSetupPath(target.handler),{context:structuredClone(setup.context),target:structuredClone(target)});
  if(read.target?.handler!==target.handler||read.target.scopeId!==target.scopeId||read.target.bindingId!==target.bindingId||
   read.context?.contextId!==setup.context.contextId||read.context.businessDate!==setup.context.businessDate||read.context.snapshotId!==setup.context.snapshotId||!sameUtcInstant(read.context.asOf,setup.context.asOf)||
   read.forms?.[sourceAction]?.permission!==form.permission)throw Error('The member fields do not match this exact observed target and source.');
  return read.forms[sourceAction].fields;
 });
 let policyVersion=0;policy.onchange=()=>{policyVersion++;o.changed();};
 return{dirty:()=>!!policy.value||editor.dirty(),refresh:editor.refresh,references:()=>[{label:'Action policy',value:policy.selectedOptions[0].text},...editor.references()],async read(){
  if(!o.permission(form.permission))throw Error('Current permission is required to record this gas action.');
  const selected=policies.find(p=>p.id+':'+p.revision===policy.value),version=policyVersion;if(!selected)throw Error('Choose the exact gas action policy.');
  const selection=await editor.read();if(!o.current()||version!==policyVersion)throw Error('The gas policy or current source changed.');return{policyId:selected.id,policyRevision:selected.revision,selection};
 }};
}

export function mountGasOwnedSettings(root:HTMLElement,setup:GasOwnedSetup,o:GasOwnedUiOptions){
 root.replaceChildren(node('h2','Gas ownership settings'));
 const command=node('select');command.required=true;command.append(new Option('Choose settings…',''),...Object.entries(setup.configuration).map(([id,f])=>new Option(f.label,id)));root.append(command);
 const content=node('div');root.append(content);let editor:ReturnType<typeof mountOperationFields>|undefined,view=0,navigation=0,previous='',accountEdited=false;
 command.onchange=()=>{const selected=command.value,attempt=++navigation;command.value=previous;void o.act(async()=>{
  if((editor?.dirty()||accountEdited)&&!await o.confirmDiscard()||!o.current()||attempt!==navigation)return;command.value=previous=selected;const generation=++view;content.replaceChildren();editor=undefined;accountEdited=false;
  const definition=setup.configuration[selected];if(!definition)return;
  const form=node('form'),account=node('select');form.dataset.lifecyclePermission=definition.permission;account.required=true;account.name='accountId';account.append(new Option('Choose account…',''),...(selected==='account'?[new Option('Create an independent account','@new')]:setup.accounts.map(a=>new Option(a.label,a.id))));
  let accountVersion=0;account.onchange=()=>{accountVersion++;accountEdited=true;o.changed();};
  const label=node('label','Account');label.append(account);form.append(label);const fields=node('fieldset');fields.className='schedule-fields';fields.disabled=!o.permission(definition.permission);form.append(fields);
  const currentEditor=editor=mountOperationFields(fields,definition.fields,{...o,current:()=>o.current()&&generation===view});
  const save=node('button','Save '+definition.label.toLowerCase());save.type='submit';save.disabled=!o.permission(definition.permission);form.append(save);content.append(form);const created=crypto.randomUUID();
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{
   if(!account.value||!o.permission(definition.permission)||!o.permission('RecordGasOwnedHoldings'))throw Error('Choose an account and restore its configuration permission.');const accountChoice=account.value,version=accountVersion,accountId=accountChoice==='@new'?created:accountChoice;
   const selection=await currentEditor.read();if(accountChoice!==account.value||version!==accountVersion)throw Error('The gas account changed. Review its current inputs again.');if(!o.current()||generation!==view)return;
   await o.queue('lifecycle/gas/configure',{operationId:crypto.randomUUID(),context:structuredClone(setup.context),accountId,action:selected,selection},'Save '+definition.label.toLowerCase());
  });};
 });};
 return{edited:()=>accountEdited||!!editor?.dirty(),refresh:()=>editor?.refresh()};
}
