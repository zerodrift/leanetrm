import {zonedInput} from './user-time.js';
import type {LifecycleContext,LifecycleOffer} from './lifecycle-model.js';

export type GasChoice={id:string;label:string;value?:unknown};
export type GasField={name:string;label:string;kind:'text'|'integer'|'instant'|'select'|'reference'|'object'|'array';nullable:boolean;
 choices?:GasChoice[];fields?:GasField[];members?:string[]};
export type GasForm={label:string;permission:string;fields:GasField[]};
export type GasPolicyChoice=GasChoice&{revision:number;action:string;accountId:string};
export type GasOwnedSetup={context:LifecycleContext;accounts:GasChoice[];policies:GasPolicyChoice[];forms:Record<string,GasForm>;configuration:Record<string,GasForm>};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type GasOwnedUiOptions={api:Api;zone:()=>string;permission:(name:string)=>boolean;current:()=>boolean;changed:()=>void;
 act:(work:()=>Promise<void>)=>Promise<void>;queue:(path:string,body:Record<string,unknown>,label:string)=>Promise<void>;confirmDiscard:()=>Promise<boolean>};
export type GasOwnedActionDraft={policyId:string;policyRevision:number;selection:Record<string,unknown>};
const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const value=document.createElement(tag);if(text!==undefined)value.textContent=text;return value;};

// The backend supplies closed product fields and exact opaque reference choices.
// This editor resolves user clocks and transports observations; it derives no money.
function fieldsEditor(root:HTMLElement,fields:GasField[],o:GasOwnedUiOptions){
 function requireFields(items:GasField[]){
  if(!Array.isArray(items))throw Error('Reread the complete gas field metadata.');
  const names=new Set<string>();
  for(const field of items){
   if(!field||typeof field.name!=='string'||!field.name||typeof field.label!=='string'||typeof field.nullable!=='boolean'||
    !['text','integer','instant','select','reference','object','array'].includes(field.kind))throw Error('Reread the supported gas field metadata.');
   if(['object','array'].includes(field.kind)){if(!Array.isArray(field.fields)||!field.fields.length)throw Error('Reread the nested gas field metadata.');requireFields(field.fields);}
   if(['select','reference'].includes(field.kind)&&!Array.isArray(field.choices))throw Error('Reread the exact gas field choices.');
   if(field.kind==='reference'&&(!Array.isArray(field.members)||!field.members.length))throw Error('Reread the exact gas reference field members.');
   for(const member of field.kind==='reference'?field.members!:[field.name]){if(!member||names.has(member))throw Error('Reread the distinct closed gas field metadata.');names.add(member);}
  }
 }
 requireFields(fields);
 let version=0,dirty=false;const clocks:{input:HTMLInputElement;control:ReturnType<typeof zonedInput>}[]=[];
 const references:{label:string;input:HTMLSelectElement}[]=[];
 function changed(){version++;dirty=true;o.changed();}
 const select=(host:HTMLElement,name:string,options:{id:string;label:string}[])=>{const input=node('select');input.name=name;input.required=true;input.append(new Option('Choose…',''),...options.map(c=>new Option(c.label,c.id)));host.append(input);return input;};
 function build(host:HTMLElement,items:GasField[]):()=>Promise<Record<string,unknown>>{
  const readers=items.map(field=>{
   const label=node('label',field.label),box=node('div');box.className='gas-lifecycle-field';label.append(box);host.append(label);
   if(field.kind==='object'){
    const mode=field.nullable?select(box,field.name,[{id:'none',label:'Not applicable'},{id:'terms',label:'Supply these terms'}]):undefined;
    const body=node('div');body.className='schedule-fields';box.append(body);let read:(()=>Promise<Record<string,unknown>>)|undefined;
    const render=()=>{body.replaceChildren();read=mode&&mode.value!=='terms'?undefined:build(body,field.fields!);};render();if(mode)mode.onchange=()=>{render();changed();};
    return async()=>{if(mode&&!mode.value)throw Error('Choose '+field.label+'.');return {[field.name]:mode?.value==='none'?null:await read!()};};
   }
   if(field.kind==='array'){
    const mode=select(box,field.name,[{id:'empty',label:'No entries'},{id:'entries',label:'Select entries'}]),body=node('div'),add=node('button','Add entry');add.type='button';box.append(body,add);
    const rows:{host:HTMLElement;read:()=>Promise<Record<string,unknown>>}[]=[];
    const render=()=>{body.replaceChildren();rows.length=0;add.hidden=mode.value!=='entries';};render();mode.onchange=()=>{render();changed();};
    add.onclick=()=>{const host=node('fieldset'),remove=node('button','Remove entry');remove.type='button';const row={host,read:build(host,field.fields!)};rows.push(row);remove.onclick=()=>{rows.splice(rows.indexOf(row),1);host.remove();changed();};host.append(remove);body.append(host);changed();};
    return async()=>{if(!mode.value||mode.value==='entries'&&!rows.length)throw Error('Choose '+field.label+' and add its entries.');return{[field.name]:mode.value==='empty'?[]:await Promise.all(rows.map(row=>row.read()))};};
   }
   if(field.kind==='select'||field.kind==='reference'){
    const choices=field.choices!,input=select(box,field.name,[...(field.nullable?[{id:'@null',label:'Not applicable'}]:[]),...choices]);
    references.push({label:field.label,input});
    return async()=>{
     if(input.value==='@null'&&field.nullable)return field.kind==='reference'?Object.fromEntries(field.members!.map(key=>[key,null])):{[field.name]:null};
     const choice=choices.find(c=>c.id===input.value);if(!choice)throw Error('Choose '+field.label+'.');
     const value=structuredClone(choice.value===undefined?choice.id:choice.value);
     if(field.kind==='reference'){
      if(!value||typeof value!=='object'||Array.isArray(value)||!field.members||Object.keys(value).length!==field.members.length||field.members.some(key=>!Object.hasOwn(value,key)))throw Error('Reread the exact '+field.label+' reference.');
      return value as Record<string,unknown>;
     }
     return{[field.name]:value};
    };
   }
   const mode=field.nullable?select(box,field.name+'-disposition',[{id:'none',label:'Not applicable'},{id:'value',label:'Supply a value'}]):undefined;
   const input=node('input');input.name=field.name;input.required=true;input.type=field.kind==='instant'?'datetime-local':field.kind==='integer'?'number':'text';input.autocomplete='off';
   if(field.kind==='integer')input.step='1';if(field.kind==='instant')input.step='1';box.append(input);
   const time=field.kind==='instant'?zonedInput(input,o.zone,o.api):undefined;if(time){clocks.push({input,control:time});box.append(node('small',o.zone()));}
   if(mode){input.hidden=input.disabled=true;mode.onchange=()=>{input.hidden=input.disabled=mode.value!=='value';changed();};}
   return async()=>{
    if(mode&&!mode.value)throw Error('Choose '+field.label+'.');if(mode?.value==='none')return{[field.name]:null};
    const text=input.value;if(!text.trim())throw Error('Enter '+field.label+'.');
    if(time)return{[field.name]:await time.instant()};
    if(field.kind==='integer'){const value=Number(text);if(!Number.isSafeInteger(value))throw Error('Enter an integer '+field.label+'.');return{[field.name]:value};}
    return{[field.name]:text};
   };
  });
  return async()=>Object.assign(Object.create(null),...await Promise.all(readers.map(read=>read())));
 }
 const read=build(root,fields);root.addEventListener('input',changed);root.addEventListener('change',changed);
 return{dirty:()=>dirty,references:()=>references.filter(r=>r.input.isConnected&&r.input.value).map(r=>({label:r.label,value:r.input.selectedOptions[0].text})),refresh(){for(const time of clocks)if(time.input.isConnected)time.control.refresh();},async read(){
  const before=version,zone=o.zone();const value=await read();if(!o.current()||before!==version||zone!==o.zone())throw Error('The gas draft or current source changed. Review its current inputs again.');return value;
 }};
}

export function mountGasOwnedAction(root:HTMLElement,offer:LifecycleOffer,setup:GasOwnedSetup,o:GasOwnedUiOptions):{read:()=>Promise<GasOwnedActionDraft>;refresh:()=>void;dirty:()=>boolean;references:()=>{label:string;value:string}[]}{
 const form=setup.forms[offer.action];if(!form)throw Error('Read the supported gas action fields for this source.');
 const label=node('label','Action policy'),policy=node('select');label.className='gas-lifecycle-policy';policy.name='policyId';policy.required=true;
 const policies=setup.policies.filter(p=>p.action===offer.action&&p.accountId===offer.target.scopeId);
 policy.append(new Option('Choose…',''),...policies.map(p=>new Option(p.label,p.id+':'+p.revision)));label.append(policy);root.append(label);
 const body=node('fieldset');body.className='schedule-fields gas-lifecycle-fields';body.dataset.lifecyclePermission=form.permission;body.disabled=!o.permission(form.permission);root.append(body);const editor=fieldsEditor(body,form.fields,o);
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
 const content=node('div');root.append(content);let editor:ReturnType<typeof fieldsEditor>|undefined,view=0,navigation=0,previous='',accountEdited=false;
 command.onchange=()=>{const selected=command.value,attempt=++navigation;command.value=previous;void o.act(async()=>{
  if((editor?.dirty()||accountEdited)&&!await o.confirmDiscard()||!o.current()||attempt!==navigation)return;command.value=previous=selected;const generation=++view;content.replaceChildren();editor=undefined;accountEdited=false;
  const definition=setup.configuration[selected];if(!definition)return;
  const form=node('form'),account=node('select');form.dataset.lifecyclePermission=definition.permission;account.required=true;account.name='accountId';account.append(new Option('Choose account…',''),...(selected==='account'?[new Option('Create an independent account','@new')]:setup.accounts.map(a=>new Option(a.label,a.id))));
  let accountVersion=0;account.onchange=()=>{accountVersion++;accountEdited=true;o.changed();};
  const label=node('label','Account');label.append(account);form.append(label);const fields=node('fieldset');fields.className='schedule-fields';fields.disabled=!o.permission(definition.permission);form.append(fields);
  const currentEditor=editor=fieldsEditor(fields,definition.fields,{...o,current:()=>o.current()&&generation===view});
  const save=node('button','Save '+definition.label.toLowerCase());save.type='submit';save.disabled=!o.permission(definition.permission);form.append(save);content.append(form);const created=crypto.randomUUID();
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{
   if(!account.value||!o.permission(definition.permission)||!o.permission('RecordGasOwnedHoldings'))throw Error('Choose an account and restore its configuration permission.');const accountChoice=account.value,version=accountVersion,accountId=accountChoice==='@new'?created:accountChoice;
   const selection=await currentEditor.read();if(accountChoice!==account.value||version!==accountVersion)throw Error('The gas account changed. Review its current inputs again.');if(!o.current()||generation!==view)return;
   await o.queue('lifecycle/gas/configure',{operationId:crypto.randomUUID(),context:structuredClone(setup.context),accountId,action:selected,selection},'Save '+definition.label.toLowerCase());
  });};
 });};
 return{edited:()=>accountEdited||!!editor?.dirty(),refresh:()=>editor?.refresh()};
}
