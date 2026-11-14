import {zonedInput} from './user-time.js';
import type {LifecycleDiscovery,LifecycleOffer} from './lifecycle-model.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
const element=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};

/** Task metadata and explicit selections only; the native handler derives all
 * financial inputs when the scheduler owns the occurrence. */
export function mountLifecycleSchedule(root:HTMLElement,data:LifecycleDiscovery,offer:LifecycleOffer,o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;queue:(path:string,body:Record<string,unknown>,label:string)=>Promise<void>;current:()=>boolean;zone:()=>string;changed:()=>void}){
 let edited=false,version=0;const times:ReturnType<typeof zonedInput>[]=[];
 root.replaceChildren(element('h2','Schedule this occurrence'));
 const settings=data.scheduling;
 if(!data.recordedCloseId||!offer.scheduleSettings||!settings){root.append(element('p','Record this market close and select saved product settings before scheduling an occurrence.'));return{edited:()=>false,refresh(){}};}
 const close=data.recordedCloseId,selectedSettings=structuredClone(offer.scheduleSettings);
 const form=element('form');form.className='lifecycle-form';form.dataset.lifecyclePermission='RunJobs';const fields=element('div');fields.className='schedule-fields';form.append(fields);root.append(form);
 function field(label:string,input:HTMLInputElement|HTMLSelectElement){const wrapper=element('label',label);wrapper.append(input);fields.append(wrapper);return input;}
 function input(label:string,type='text'){const control=element('input');control.type=type;control.required=true;field(label,control);return control;}
 function choices(label:string,values:{value:string;label:string}[]){const control=element('select');control.required=true;control.append(new Option('Choose…',''),...values.map(v=>new Option(v.label,v.value)));field(label,control);return control;}
 function time(label:string){const control=input(label,'datetime-local');control.step='1';const value=zonedInput(control,o.zone,o.api);times.push(value);control.parentElement!.append(element('small',o.zone()));return value;}
 const name=input('Task name');name.maxLength=settings.maximumNameLength;
 const policy=choices('Action policy',data.policies.filter(p=>p.action===offer.action&&(!offer.policyId||p.id===offer.policyId&&p.revision===offer.policyRevision)).map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${p.mode[0].toUpperCase()+p.mode.slice(1)} · revision ${p.revision}`})));
 const mode=element('p');mode.className='field-help';form.append(mode);
 policy.onchange=()=>{const selected=policy.value?JSON.parse(policy.value):[];const saved=data.policies.find(p=>p.id===selected[0]&&p.revision===selected[1]);mode.textContent=saved?.mode==='automatic'?'The task will review and execute source-derived actions under this saved automatic policy.':saved?.mode==='review'?'The task will prepare a proposal for review. It will not execute it.':saved?.mode==='manual'?'The task will record that manual action is required.':'';};
 const due=time('Run at'),expires=time('Stop trying at');
 const knowledge=choices('Knowledge',settings.knowledge),product=choices('Product settings',settings.settings),inputs=choices('Action inputs',settings.inputs);
 const timeout=input('Execution timeout (seconds)','number');timeout.min='1';timeout.max=String(settings.maximumTimeoutSeconds);timeout.step='1';
 const attempts=choices('Maximum attempts',Array.from({length:settings.maximumAttempts},(_,n)=>({value:String(n+1),label:String(n+1)})));
 form.append(element('p','This runs once against the selected recorded close. Observed title, assignment and payment facts require separate input. Corrections that need a cash due time stop for review.'));
 const save=element('button','Schedule occurrence');save.type='submit';form.append(save);
 for(const event of ['input','change'])form.addEventListener(event,()=>{version++;edited=true;o.changed();});
 form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{
  if(!name.value.trim()||![policy,knowledge,product,inputs,attempts].every(control=>control.value)||!timeout.value)throw Error('Choose every task policy and execution limit.');
  const revision=version,[policyId,policyRevision]=JSON.parse(policy.value),requestId=crypto.randomUUID();
  const schedule={requestId,name:name.value,operation:settings.operation,dueAt:await due.instant(),expiresAt:await expires.instant(),intervalSeconds:null,timeZone:settings.timeZone,misfire:settings.misfire,overlap:settings.overlap,timeoutSeconds:Number(timeout.value),maximumAttempts:Number(attempts.value),profileId:null,profileVersion:null};
  if(!o.current()||revision!==version)return;
  const definition={closeId:close,target:structuredClone(offer.target),action:offer.action,occurrenceId:offer.occurrenceId,policyId,policyRevision,knowledgePolicy:knowledge.value,settingsPolicy:product.value,inputPolicy:inputs.value,settings:selectedSettings};
  await o.queue('jobs/lifecycle/create',{schedule,definition},'Schedule '+offer.label);
 });};
 return{edited:()=>edited,refresh(){for(const time of times)time.refresh();}};
}
