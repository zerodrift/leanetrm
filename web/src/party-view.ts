import {renderRegularTable} from './regular-table.js';
import {displayInstant} from './market-view.js';
import {zonedInput} from './user-time.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {trackEdits} from './edit-tracking.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Party={id:string;revision:number;code:string;name:string;effectiveAt:string;knownAt?:string};
type Directory={versions:Party[];canEdit:boolean};
export function knownPartyRejection(code:string|undefined):boolean{
 return !!code&&['party_conflict','party_input','party_limit','trade_party','access_denied','request_format','request_too_large','request_token','historical_read_only','definition_rejected'].includes(code);
}
export function setupPartyEditors(o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;profile:()=>string;source:()=>string}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const effective=zonedInput(get<HTMLInputElement>('party-effective'),o.zone,o.api);
 const pending=new PendingCommand(knownPartyRejection),edits=trackEdits(get('party-form'));let directory:Directory|undefined,selected:Party|undefined,generation=0;
 const storageKey=()=>`openetrm.parties.request.${o.profile()}`;
 function store(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* The command remains available in memory for this visit. */}}
 function sync(){get<HTMLFieldSetElement>('party-fields').disabled=o.busy()||!directory?.canEdit||!!pending.command;field('party-code').disabled=!!selected;get<HTMLButtonElement>('party-new').disabled=o.busy()||!directory?.canEdit||!!pending.command;field('party-kind').disabled=o.busy()||!!pending.command;get('party-pending').hidden=!directory||!pending.command;}
 function fill(party?:Party){
  selected=party;edits.clear();field('party-code').value=party?.code??'';field('party-code').disabled=!!party;
  field('party-name').value=party?.name??'';effective.clear();
  get('party-edit-title').textContent=party?`Edit ${party.name} · revision ${party.revision}`:'New party';
  renderRegularTable(get('party-history'),'party-history',JSON.stringify([o.source(),field('party-kind').value,party?.id]),
   ['Revision','Code','Name',`Effective · ${o.zone()}`,`Known · ${o.zone()}`],
   (party?directory?.versions.filter(p=>p.id===party.id)??[]:[]).map(p=>[String(p.revision),p.code,p.name,displayInstant(p.effectiveAt,o.zone()),p.knownAt?displayInstant(p.knownAt,o.zone()):'']),[0],o.busy);
  sync();
 }
 function render(){
  if(!directory)return;
  const latest=[...new Map([...directory.versions].reverse().map(p=>[p.id,p])).values()];
  renderRegularTable(get('party-list'),'party-list',JSON.stringify([o.source(),field('party-kind').value]),
   ['Code','Name','Revision',`Effective · ${o.zone()}`,''],latest.map(p=>{
    const button=document.createElement('button');button.type='button';button.className='secondary';button.dataset.partyCode=p.code;button.textContent=directory?.canEdit?'Edit / history':'History';button.onclick=()=>{if(!o.busy()&&!pending.command)fill(p);};
    return[p.code,p.name,String(p.revision),displayInstant(p.effectiveAt,o.zone()),button];
   }),[2],o.busy);
  get('party-read-only').hidden=directory.canEdit;
  get<HTMLButtonElement>('party-new').disabled=!directory.canEdit||!!pending.command;
  fill(selected&&latest.find(p=>p.id===selected?.id));
 }
 async function load(){
  const started=++generation,selectedId=selected?.id,kind=field('party-kind').value;
  selected=undefined;
  directory=undefined;get('party-list').replaceChildren();get('party-history').replaceChildren();get<HTMLFormElement>('party-form').hidden=true;
  get('party-saved').textContent='';get('party-edit-title').textContent='New party';field('party-code').value='';field('party-name').value='';effective.clear();get('party-pending').hidden=true;
  const snapshotId=o.source();
  const response=await o.api<Directory>('parties/catalog',{snapshotId,kind});
  if(started!==generation||snapshotId!==o.source()||kind!==field('party-kind').value)return false;
  directory=response;selected=directory.versions.find(p=>p.id===selectedId);
  try{const stored=sessionStorage.getItem(storageKey());pending.command=stored?{...JSON.parse(stored),uncertain:true}:undefined;}catch{pending.clear();}
  get<HTMLFormElement>('party-form').hidden=false;render();return true;
 }
 async function send(command:RequestCommand){
  const started=generation,owner=o.profile(),previouslyUncertain=pending.sending();store();let result:Party;
  try{
   result=await o.api<Party>(command.path,{...command.body,operationId:command.operationId});
   if(started!==generation||owner!==o.profile()||pending.command!==command)return;
  }catch(error){
   if(started!==generation||owner!==o.profile()||pending.command!==command)return;
   pending.failed((error as {code?:string}).code,previouslyUncertain);store();sync();throw error;
  }
  pending.clear();store();selected=result;
  if(await load())get('party-saved').textContent=`Saved ${result.name} · revision ${result.revision}. Trade entry uses versions effective at its execution time.`;
 }
 get<HTMLFormElement>('party-form').onsubmit=e=>{e.preventDefault();void o.act(async()=>{
  if(!directory?.canEdit)throw Error('Return to working data with party edit permission.');
  const at=await effective.instant();
  const command=pending.begin('parties/save',{kind:field('party-kind').value,id:selected?.id??null,expectedRevision:selected?.revision??null,code:field('party-code').value,name:field('party-name').value,effectiveAt:at});
  store();sync();await send(command);
 });};
 get('party-new').onclick=()=>{if(!o.busy()&&!pending.command)fill();};
 get('party-refresh').onclick=()=>void o.act(async()=>{await load();});
 field('party-kind').onchange=()=>{if(!pending.command){selected=undefined;void o.act(async()=>{await load();});}};
 get('party-retry').onclick=()=>void o.act(async()=>{if(pending.command)await send(pending.command);});
 get('party-discard').onclick=()=>{if(!o.busy()){pending.clear();store();sync();}};
 return{load,sync,
  // Party edits not yet saved, or a save whose outcome is unconfirmed.
  unsaved:()=>!!pending.command||edits.edited()&&!get('party-form').hidden,
  reset(){generation++;directory=undefined;selected=undefined;pending.clear();edits.clear();effective.clear();field('party-code').value='';field('party-name').value='';get('party-edit-title').textContent='New party';get('party-list').replaceChildren();get('party-history').replaceChildren();get('party-saved').textContent='';get<HTMLFormElement>('party-form').hidden=true;get('party-pending').hidden=true;},refresh(){effective.refresh();if(directory)render();}};
}
