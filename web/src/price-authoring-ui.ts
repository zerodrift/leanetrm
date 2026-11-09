import {PendingCommand,type RequestCommand} from './pending-command.js';
import {restoreAuthoringRequest,authoringLabel} from './price-authoring-model.js';
import {askInPanel} from './side-panel.js';

export type PriceAuthoringOptions={
 api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;
 busy:()=>boolean;identity:()=>string;snapshot:()=>string;historical:()=>boolean;
 unsavedChanged?:()=>void;
};
export function element<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;}
export function button(text:string,run:()=>void,id?:string){const node=element('button',text);node.type='button';node.className='secondary';if(id)node.id=id;node.onclick=run;return node;}
export function field(name:string,control:HTMLElement){const node=element('label',name);node.append(control);return node;}
export function input(id:string,type='text'){const node=element('input');node.id=id;node.type=type;return node;}
export function select(id:string){const node=element('select');node.id=id;return node;}
export function choices(control:HTMLSelectElement,values:readonly {value:string;label:string}[],selected='',prompt='Choose…'){
 control.replaceChildren(new Option(prompt,''),...values.map(v=>new Option(v.label,v.value)));control.value=selected;
}
export function rules(control:HTMLSelectElement,values:readonly string[],selected=''){choices(control,values.map(value=>({value,label:authoringLabel(value)})),selected);}
export function help(text:string){const node=element('p',text);node.className='field-help';return node;}
export function group(...children:HTMLElement[]){const node=element('div');node.className='price-authoring-fields';node.append(...children);return node;}
export function resultTable(host:HTMLElement,columns:string[],rows:(string|HTMLElement|null)[][]){
 const table=element('table');table.className='price-authoring-table';const head=element('thead'),tr=element('tr');
 columns.forEach(c=>tr.append(element('th',c)));head.append(tr);const body=element('tbody');
 rows.forEach(row=>{const tr=element('tr');row.forEach(value=>{const td=element('td');if(value===null)td.textContent='Unavailable';else if(typeof value==='string')td.textContent=value;else td.append(value);tr.append(td);});body.append(tr);});table.append(head,body);host.replaceChildren(table);
}

/** One actor-scoped retained command. Changing context never changes an unconfirmed write's payload. */
export function authoringSession(host:HTMLElement,o:PriceAuthoringOptions,path:string){
 let dirty=false,revision=0,owner='',abandoned=new AbortController(),leaving:Promise<boolean>|undefined;
 const pending=new PendingCommand(code=>!!code&&/^(publication_(definition|version|conflict|reference|request|command|revision_conflict|request_conflict|occurrence_conflict|occurrence_ambiguity|version_read_only|publisher_conflict|legacy_boundary|target|correction|missing|release|index|coverage|time|occurrence|source|unsupported|denied|historical_write)|contract_price_(terms|mapping|conflict|request)|custom_curve_(conflict|definition)|price_settlement_selection|index_fixing_(convention_missing|definition)|price_authoring_historical|access_denied|request_token|request_format|request_too_large)$/.test(code));
 host.tabIndex=-1;
 const prompt=element('div');prompt.id=host.id+'-prompt';prompt.className='side-panel-prompt';prompt.hidden=true;prompt.setAttribute('role','alertdialog');prompt.setAttribute('aria-label','Unsaved changes');
 const receipt=element('div');receipt.className='mode-banner price-authoring-receipt';receipt.hidden=true;
 const receiptText=element('p');const retry=button('Retry original save',()=>void o.act(async()=>{if(pending.command)await send(pending.command);}));
 const discard=button('Discard request after checking',()=>{if(o.busy())return;pending.clear();persist();sync();});
 receipt.append(receiptText,retry,discard);host.append(prompt,receipt);
 let saved:(value:unknown,command:RequestCommand)=>Promise<void>=async()=>{},changed:()=>void=()=>{},validateReceipt:(value:unknown,command:RequestCommand)=>boolean=()=>true;
 const storageKey=()=>`openetrm.price-authoring.${path}.${owner}`;
 function persist(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* Retained in memory if storage is unavailable. */}}
 function identity(){if(owner===o.identity())return;owner=o.identity();pending.clear();try{pending.command=restoreAuthoringRequest(JSON.parse(sessionStorage.getItem(storageKey())??'null'),path);}catch{/* Invalid stored transport state has no authority. */}}
 function sync(){identity();receipt.hidden=!pending.command;receiptText.textContent=pending.command?`The result of “${pending.command.label??'Save'}” is unconfirmed. Inspect saved versions before discarding, or retry the unchanged request.`:'';retry.disabled=o.busy()||o.historical()||!pending.command;discard.disabled=o.busy();changed();o.unsavedChanged?.();}
 async function send(command:RequestCommand){
  const actor=owner,at=revision,previous=pending.sending();persist();sync();
  try{const result=await o.api(command.path,{...command.body,requestId:command.operationId});if(owner!==actor||o.identity()!==actor)return;
   if(!validateReceipt(result,command))throw Error('The save response did not match the original request. Retry the original save to confirm its result.');
   pending.clear();persist();if(revision===at){dirty=false;await saved(result,command);}}
  catch(error){if(owner===actor){pending.failed((error as {code?:string}).code,previous);persist();}throw error;}
  finally{sync();}
 }
 return {
  changed(callback:()=>void){changed=callback;},saved(callback:typeof saved){saved=callback;},validateReceipt(callback:typeof validateReceipt){validateReceipt=callback;},sync,
  mark(){dirty=true;revision++;changed();o.unsavedChanged?.();},clean(){dirty=false;revision++;o.unsavedChanged?.();},unsaved:()=>dirty,
  blocked:()=>!!pending.command,
  ticket(){identity();const actor=owner,at=revision,snapshot=o.snapshot();return()=>{try{return owner===actor&&o.identity()===actor&&revision===at&&o.snapshot()===snapshot;}catch{return false;}};},
  async save(body:Record<string,unknown>,label:string){identity();await send(pending.begin(path,body,undefined,label));},
  async leave(){if(leaving)return leaving;if(!dirty)return true;leaving=askInPanel(host,{text:'This editor has unsaved changes. Leaving discards them.',keep:'Keep editing',go:'Discard changes'})(abandoned.signal).then(yes=>{if(yes){dirty=false;revision++;o.unsavedChanged?.();}return yes;}).finally(()=>{leaving=undefined;});return leaving;},
  reset(){abandoned.abort();abandoned=new AbortController();dirty=false;revision++;identity();sync();}
 };
}
