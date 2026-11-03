import {PendingCommand,actorTransport,type RequestCommand} from './pending-command.js';

type Field={key:string;label:string;min:number;max:number};
type Revision={version:number;revisionId:string;values:Record<string,number>;recordedAt:string;origin:string};
type Policy={kind:'assistant'|'access';fields:Field[];current:Revision;history:Revision[]};
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export function policyValues(fields:Field[],entered:Record<string,string>):Record<string,number>{
 return Object.fromEntries(fields.map(field=>{
  const text=entered[field.key],value=typeof text==='string'&&/^\d+$/.test(text)?Number(text):Number.NaN;
  if(!Number.isSafeInteger(value)||value<field.min||value>field.max)throw Error(`Supply ${field.label} from ${field.min} to ${field.max}.`);
  return [field.key,value];
 }));
}

/** The same bounded form serves two separately authorized operating policies. */
export function setupOperatingPolicy(root:HTMLElement,kind:Policy['kind'],o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;identity:()=>string;can:()=>boolean;busy:()=>boolean}){
 const pending=new PendingCommand(code=>['operating_policy_input','operating_policy_conflict'].includes(code??''));
 const transport=actorTransport<RequestCommand>(()=>`openetrm.operating-policy.${kind}.${o.identity()}`,()=>sessionStorage);
 root.dataset.ownControls='';
 function sync(){
  root.querySelectorAll<HTMLInputElement|HTMLButtonElement>('input,button').forEach(input=>input.disabled=o.busy()||(input instanceof HTMLButtonElement&&input.type==='submit'&&!!pending.command));
 }
 let generation=0;
 const make=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
 async function load(){
  const version=++generation,owner=o.identity();root.replaceChildren();root.hidden=!o.can();if(root.hidden)return;
  const detail=make('details'),summary=make('summary',kind==='assistant'?'Assistant operating policy':'Access policy');detail.className='card';detail.append(summary);root.append(detail);
  const directory=await o.api<{policies:Policy[]}>('settings/operating-policies');if(version!==generation||owner!==o.identity())return;
  const policy=directory.policies.find(p=>p.kind===kind);if(!policy)throw Error('The operating policy is unavailable for this account.');
  const kept=transport.read();pending.command=kept?.path==='settings/operating-policies'&&kept.body.kind===kind?kept:undefined;
  detail.append(make('p',kind==='assistant'?'New turns retain the policy admitted with their request. Existing turns and grants keep their recorded terms.':'Changes apply to new sign-ins, lockouts, passwords and invitations. Existing sessions and invitations keep their recorded expiry.'));
  const form=make('form'),fields=make('div');fields.className='schedule-fields';const inputs=new Map<string,HTMLInputElement>();
  for(const field of policy.fields){
   const label=make('label',field.label),input=make('input');input.type='number';input.required=true;input.min=String(field.min);input.max=String(field.max);input.step='1';input.value=String(policy.current.values[field.key]);input.dataset.policyField=field.key;label.append(input);fields.append(label);inputs.set(field.key,input);
  }
  const actions=make('div');actions.className='schedule-actions';const save=make('button','Save policy'),retry=make('button','Retry original request'),reload=make('button','Reload policy');save.type='submit';retry.type=reload.type='button';retry.hidden=!pending.command;reload.className=retry.className='secondary';save.disabled=!!pending.command;
  const status=make('p',`Version ${policy.current.version} · ${policy.current.recordedAt}`);status.setAttribute('role','status');actions.append(save,retry,reload);form.append(fields,actions,status);detail.append(form);
  const history=make('details'),heading=make('summary','Recent policy revisions');history.append(heading);
  for(const revision of policy.history)history.append(make('p',`Version ${revision.version} · ${revision.recordedAt} · ${revision.origin==='carried_forward'?'Carried-forward operating baseline':'Configured'}`));detail.append(history);
  async function send(){
   if(version!==generation||owner!==o.identity())throw Error('The account changed. Reopen its settings.');
   const command=pending.command!;const uncertain=pending.sending();transport.write(pending.command);save.disabled=retry.disabled=true;
   try{
    await o.api(command.path,{...command.body,operationId:command.operationId});
    if(version!==generation||owner!==o.identity())return;
    pending.clear();transport.write(undefined);await load();
   }catch(error){
    if(version!==generation||owner!==o.identity())throw error;
    pending.failed((error as {code?:string}).code,uncertain);transport.write(pending.command);retry.hidden=!pending.command;save.disabled=!!pending.command;retry.disabled=false;
    status.textContent=error instanceof Error?error.message:'The policy save could not be confirmed. Retry the original request.';throw error;
   }
  }
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{const values=policyValues(policy.fields,Object.fromEntries([...inputs].map(([key,input])=>[key,input.value])));pending.begin('settings/operating-policies',{kind,expectedVersion:policy.current.version,values});await send();});};
  retry.onclick=()=>void o.act(send);reload.onclick=()=>void o.act(load);
 }
 return{load,sync,reset(){generation++;pending.clear();root.replaceChildren();root.hidden=true;},unconfirmed:()=>!!pending.command};
}
