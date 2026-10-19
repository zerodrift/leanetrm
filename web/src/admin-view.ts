import {renderRegularTable} from './regular-table.js';
import {displayInstant,publicText,friendly} from './market-view.js';
import {PendingCommand as PendingAdministration} from './pending-command.js';
import type {RequestCommand as AdministrativeCommand} from './pending-command.js';
export {PendingCommand as PendingAdministration} from './pending-command.js';
export type {RequestCommand as AdministrativeCommand} from './pending-command.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Member={id:string;login:string;name:string;kind:string;enabled:boolean;version:number;roles:string[]};
type Audit={operationId:string;at:string;actor:string;action:string;before:Member|null;after:Member};
type Directory={members:Member[];roles:{code:string;name:string;permissions:string[]}[];audit:Audit[];kinds:{code:string;name:string}[];canCreate?:boolean};

export function knownAdministrationRejection(code:string|undefined):boolean{
  return !!code&&['admin_conflict','admin_input','last_administrator','self_access_change','admin_limit',
    'access_denied','request_format','request_token','definition_rejected'].includes(code);
}
export function accessChange(before:Member|null,after:Member):string{
  if(!before)return 'Account created';
  const changes=[];
  if(before.name!==after.name)changes.push('Name changed');
  if(before.enabled!==after.enabled)changes.push(after.enabled?'Access restored':'Access suspended');
  if(JSON.stringify(before.roles)!==JSON.stringify(after.roles))changes.push('Roles changed');
  return changes.join(' · ')||'Details saved';
}
export function setupAdministration(run:(work:()=>Promise<void>)=>Promise<void>,api:Api,busy:()=>boolean,zone:()=>string,profile:()=>string,updated:()=>Promise<void>){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
  const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=publicText(text);return node;};
  let directory:Directory|undefined,selected:Member|undefined;
  const pending=new PendingAdministration();
  const storageKey=()=>`openetrm.admin.request.${profile()}`;
  function store(){try{if(pending.command)sessionStorage.setItem(storageKey(),JSON.stringify(pending.command));else sessionStorage.removeItem(storageKey());}catch{/* In-memory retry identity is still retained for this visit. */}}
  function pendingNote(){get('admin-pending').hidden=!pending.command;}
  function fill(member?:Member){
    selected=member;get<HTMLFormElement>('admin-form').reset();
    get('admin-form').hidden=directory?.canCreate===false&&!member;
    field('admin-name').value=member?.name??'';field('admin-login').value=member?.login??'';field('admin-kind').value=member?.kind??'';
    get('admin-new-fields').hidden=!!member;get('admin-enabled-field').hidden=!member;
    get<HTMLInputElement>('admin-enabled').checked=member?.enabled??true;
    get('admin-form-title').textContent=member?'Edit account':'Create account';
    field('admin-login').required=!member;field('admin-kind').required=!member;
    for(const box of get('admin-roles').querySelectorAll<HTMLInputElement>('input'))box.checked=member?.roles.includes(box.value)??false;
  }
  // Role labels come from the directory; an unknown granted role is an inconsistency, not a guess.
  function kindName(code:string){const kind=directory?.kinds.find(k=>k.code===code);if(!kind)throw Error('The account directory is inconsistent. Refresh accounts.');return kind.name;}
  function roleName(code:string){const role=directory?.roles.find(r=>r.code===code);if(!role)throw Error('The account directory is inconsistent. Refresh accounts.');return role.name;}
  function button(label:string,work:()=>void){const b=el('button',label);b.type='button';b.className='secondary';b.onclick=()=>{if(!busy())work();};return b;}
  function render(){
    if(!directory)return;
    get('admin-new').hidden=directory.canCreate===false;
    const kind=field('admin-kind').value,first=el('option','Choose a kind…');first.value='';
    get('admin-kind').replaceChildren(first,...directory.kinds.map(k=>{const o=el('option',k.name);o.value=k.code;return o;}));field('admin-kind').value=directory.kinds.some(k=>k.code===kind)?kind:'';
    get('admin-roles').replaceChildren(...directory.roles.map(role=>{
      const block=el('div'),label=el('label'),box=el('input');box.type='checkbox';box.value=role.code;box.checked=selected?.roles.includes(role.code)??false;
      label.append(box,document.createTextNode(' '+role.name));const details=el('details');details.append(el('summary','Permissions'),el('p',role.permissions.map(friendly).join(' · ')));block.className='admin-role';block.append(label,details);return block;
    }));
    renderRegularTable(get('admin-members'),'admin-members',profile(),['Name','Kind','Workspace access','Roles',''],directory.members.map(m=>[m.name,kindName(m.kind),m.enabled?'Active':'Suspended',m.roles.map(roleName).join(', ')||'No roles',button('Edit',()=>fill(m))]),[],busy);
    renderRegularTable(get('admin-audit'),'admin-audit',profile(),['Saved','By','Account','Change'],directory.audit.map(a=>[displayInstant(a.at,zone()),a.actor,a.after.name,accessChange(a.before,a.after)]),[],busy);
    fill(selected&&directory.members.find(m=>m.id===selected?.id));pendingNote();
  }
  async function load(){
    // A denied reload must not leave a cached directory visible.
    const selectedId=selected?.id;selected=undefined;get<HTMLFormElement>('admin-form').reset();get('admin-form').hidden=true;get('admin-pending').hidden=true;
    directory=undefined;get('admin-members').replaceChildren();get('admin-audit').replaceChildren();get('admin-roles').replaceChildren();
    directory=await api<Directory>('admin');
    selected=directory.members.find(m=>m.id===selectedId);get('admin-form').hidden=false;
    try{const stored=sessionStorage.getItem(storageKey());pending.command=stored?JSON.parse(stored):undefined;}catch{pending.clear();}
    render();
  }
  async function send(command:AdministrativeCommand){
    try{
      const member=await api<Member>(command.path,{...command.body,operationId:command.operationId});
      pending.clear();store();selected=member;await updated();await load();
    }catch(error){
      const code=(error as {code?:string}).code;
      if(knownAdministrationRejection(code)){pending.clear();store();}
      pendingNote();throw error;
    }
  }
  get<HTMLFormElement>('admin-form').onsubmit=e=>{e.preventDefault();void run(async()=>{
    const roles=[...get('admin-roles').querySelectorAll<HTMLInputElement>('input:checked')].map(n=>n.value).sort();
    const body:Record<string,unknown>=selected?{id:selected.id,expectedVersion:selected.version,name:field('admin-name').value,enabled:get<HTMLInputElement>('admin-enabled').checked,roles}:{login:field('admin-login').value,name:field('admin-name').value,kind:field('admin-kind').value,roles};
    const command=pending.begin(selected?'admin/update':'admin/create',body);store();pendingNote();await send(command);
  });};
  get('admin-retry').onclick=()=>void run(async()=>{if(pending.command)await send(pending.command);});
  get('admin-discard').onclick=()=>{if(busy())return;pending.clear();store();pendingNote();};
  get('admin-new').onclick=()=>{if(!busy())fill();};get('admin-refresh').onclick=()=>void run(load);
  return {load,reset(){directory=undefined;selected=undefined;pending.clear();get('admin-members').replaceChildren();get('admin-audit').replaceChildren();get('admin-roles').replaceChildren();get<HTMLFormElement>('admin-form').reset();},refresh(){render();}};
}
