import {displayInstant,publicText} from './market-view.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {modelLabel} from './agent-settings.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type Vendor={code:string;name:string};
type Model={vendor:string;identifier:string;displayName:string};
// A person's own grant in force, which Revoke names by its id (docs/ai-assistant.md §7.1).
export type Grant=Model&{id:string;agentId:string;agentName:string;version:number;modelId:string;modelVersion:number;permissions:string[];grantedAt:string;
  expiresAt:string};
// A workspace's grants, for those who administer its access: every grant in force, and the latest grant audit records.
export type GrantInForce=Model&{operationId:string;delegator:string;agentName:string;version:number;permissions:string[];grantedAt:string;expiresAt:string};
export type GrantRecord=Model&{operationId:string;at:string;outcome:string;delegator:string;actor:string;agentName:string;version:number;permissions:string[];
  expiresAt:string};
export type WorkspaceGrants={current:GrantInForce[];history:GrantRecord[];vendors:Vendor[]};

const inconsistent=()=>Error('The agent grants are inconsistent. Refresh them.');
// What a grant lets its agent read, named by the permission each read needs, in a fixed order.
const reads:[string,string][]=[['ReadTrades','trades'],['ReadTradeParties','trade parties'],['ReadMarketData','market data'],['ReadJobs','your jobs']];
export function readsPhrase(permissions:string[]){
  if(new Set(permissions).size!==permissions.length||!permissions.every(p=>reads.some(([code])=>code===p)))throw inconsistent();
  const named=reads.filter(([code])=>permissions.includes(code)).map(([,name])=>name);
  return named.length>1?`${named.slice(0,-1).join(', ')} and ${named.at(-1)}`:named[0]??'no workspace data';
}
const outcomes:Record<string,string>={granted:'Granted',replaced:'Replaced by a newer grant',revoked:'Revoked',retired:'Ended by Retire'};
export function grantOutcome(outcome:string){if(!Object.hasOwn(outcomes,outcome))throw inconsistent();return outcomes[outcome];}
// The model a grant was given for, labelled as the Agents tab labels it; a vendor the read does not name is an inconsistency.
function model(vendors:Vendor[],m:Model){if(!vendors.some(v=>v.code===m.vendor))throw inconsistent();return modelLabel({vendors},m);}
const agent=(g:{agentName:string;version:number})=>`${g.agentName} · version ${g.version}`;
export function grantItem(grant:Grant,vendors:Vendor[],instant:(at:string)=>string){
  return {title:agent(grant),model:model(vendors,grant),reads:`Reads ${readsPhrase(grant.permissions)}`,
    term:`Allowed ${instant(grant.grantedAt)} · until ${instant(grant.expiresAt)}`,revoke:`Revoke ${grant.agentName}`};
}
export function inForceRow(g:GrantInForce,vendors:Vendor[],instant:(at:string)=>string){
  return [g.delegator,agent(g),model(vendors,g),readsPhrase(g.permissions),instant(g.grantedAt),instant(g.expiresAt)];
}
export function historyRow(r:GrantRecord,vendors:Vendor[],instant:(at:string)=>string){
  return [instant(r.at),r.delegator,agent(r),model(vendors,r),readsPhrase(r.permissions),instant(r.expiresAt),grantOutcome(r.outcome),r.actor];
}
// Refusals that prove a first Revoke wrote nothing, so its kept request can be released.
const rejections=['grant_input','grant_unknown','grant_conflict','access_denied','request_format','request_too_large','request_token'];
export const knownGrantRejection=(code:string|undefined)=>!!code&&rejections.includes(code);
const messages:Record<string,string>={grant_unknown:'You have no such grant. Your agent grants have been read again.',
  grant_conflict:'This request identity was used for another grant change, so nothing was revoked. Revoke again to send a new request.',
  access_denied:'Your current access cannot read or revoke agent grants.'};
export function grantMessage(code:string|undefined){return code&&Object.hasOwn(messages,code)?messages[code]:undefined;}
const unconfirmed='We could not confirm the revocation. Retry sends the same request.';
const revokePath='assistant/grants/revoke';
export type KeptRevoke={command:RequestCommand;agent:string};
// A kept Revoke as this page stores it, with the agent it names for its note: only that form is read back.
export function keptRevoke(stored:string|null):KeptRevoke|undefined{
  try{
    const v=JSON.parse(stored??'null');
    return v&&typeof v.operationId==='string'&&v.path===revokePath&&typeof v.agent==='string'&&v.body&&typeof v.body.grantId==='string'&&Object.keys(v.body).length===1
      ?{command:{operationId:v.operationId,path:revokePath,body:{grantId:v.body.grantId},uncertain:true},agent:v.agent}:undefined;
  }catch{return undefined;/* Invalid browser state cannot grant authority. */}
}
export const keptNote=(agentName:string)=>`A revocation of your grant to ${agentName} has no confirmed outcome. Retry sends the same request: it reads the `
  +'stored outcome if the grant was revoked, and otherwise revokes it once. Discard forgets the request without sending it; a grant ends at most once either way.';

// Agent access in the profile dialog (docs/ai-assistant.md §7.1): a person's own grants in force, read when asked, with
// Revoke, which needs no UseAgents.
export function setupAgentGrants(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
  const pending=new PendingCommand(knownGrantRejection);
  const storage=()=>`openetrm.assistant.grants.request.${options.identity()}`;
  // A read or Revoke answers only the generation it started in; an identity change or a closed dialog starts another.
  let listed:{grants:Grant[];vendors:Vendor[]}|undefined,kept='',generation=0;
  function store(){
    try{if(pending.command)sessionStorage.setItem(storage(),JSON.stringify({...pending.command,agent:kept}));else sessionStorage.removeItem(storage());}
    catch{/* The in-memory request is kept for this visit. */}
  }
  function setError(message=''){text('profile-grants-error',message);get('profile-grants-error').hidden=!message;}
  // Nothing read for an identity, or before its access ended, stays on screen.
  function clear(){generation++;listed=undefined;get('profile-grants-list').replaceChildren();text('profile-grants-status','');text('profile-grants-show','Show agent access');}
  function render(notice:string){
    if(!listed)return;const shown=listed,instant=(at:string)=>displayInstant(at,options.zone());
    get('profile-grants-list').replaceChildren(...shown.grants.map(grant=>{
      const facts=grantItem(grant,shown.vendors,instant),item=document.createElement('li'),title=document.createElement('strong'),revoke=document.createElement('button');
      title.textContent=publicText(facts.title);item.append(title);
      for(const line of [facts.model,facts.reads,facts.term]){const span=document.createElement('span');span.textContent=publicText(line);item.append(span);}
      revoke.type='button';revoke.className='secondary';revoke.textContent='Revoke';revoke.setAttribute('aria-label',publicText(facts.revoke));
      revoke.onclick=()=>{if(!options.busy())void options.act(()=>revokeGrant(grant)).then(settle);};item.append(revoke);return item;
    }));
    const count=shown.grants.length?`You have ${shown.grants.length} agent grant${shown.grants.length===1?'':'s'} in force.`:'You have no agent grants in force.';
    text('profile-grants-status',notice?`${notice} ${count}`:count);text('profile-grants-show','Refresh agent access');
  }
  function sync(){
    const busy=options.busy(),held=!!pending.command;
    for(const button of get('profile-grants-list').querySelectorAll<HTMLButtonElement>('button'))button.disabled=busy||held;
    get('profile-grants-pending').hidden=!held;text('profile-grants-pending-text',held?keptNote(kept):'');
  }
  // A read the page cannot show, or one refused, leaves no grant on screen and says why.
  async function load(notice=''){
    clear();setError();text('profile-grants-status','Reading your agent grants…');
    const request=generation,identity=options.identity(),current=()=>request===generation&&identity===options.identity();
    try{
      const answer=await options.api<{grants:Grant[];vendors:Vendor[]}>('assistant/grants');
      if(current()){listed=answer;render(notice);}
    }catch(error){
      if(current()){clear();setError(grantMessage((error as {code?:string}).code)??(error instanceof Error?error.message:'Your agent grants could not be read.'));}
    }
  }
  // Once sent, a Revoke keeps its identity until it is confirmed, refused before any write, or discarded.
  async function send(){
    const command=pending.command,identity=options.identity(),started=generation;if(!command)return;
    const previouslyUncertain=pending.sending();store();setError();
    let ended:{revokedAt:string};
    try{
      ended=await options.api<{revokedAt:string}>(command.path,{...command.body,operationId:command.operationId});
      if(identity!==options.identity()||pending.command!==command)return;
    }catch(error){
      if(identity!==options.identity()||pending.command!==command)return;
      const code=(error as {code?:string}).code;
      pending.failed(code,previouslyUncertain);store();
      const message=pending.command?unconfirmed:grantMessage(code)??(error instanceof Error?error.message:unconfirmed);
      // After an unconfirmed outcome the list shows what is in force; a refused one keeps nothing.
      if(code==='access_denied')clear();else if(started===generation)await load();
      setError(message);return;
    }
    const agentName=kept;pending.clear();kept='';store();
    if(started!==generation)return;
    // The answer is about this grant: a newer grant to the same agent, if any, is still listed.
    await load(`Your grant to ${agentName} ended ${displayInstant(ended.revokedAt,options.zone())}.`);
  }
  // Once a Revoke settles and the page's controls are enabled again, focus moves to Retry while a request is kept, and
  // otherwise to Agent access: the Revoke used is gone, and a disabled control cannot take focus.
  function settle(){get(pending.command?'profile-grants-retry':'profile-grants-title').focus();}
  async function revokeGrant(grant:Grant){
    if(pending.command){setError('Retry or discard the kept revocation before revoking another grant.');return;}
    kept=grant.agentName;pending.begin(revokePath,{grantId:grant.id});store();await send();
  }
  get('profile-grants-show').onclick=()=>void options.act(()=>load());
  get('profile-grants-retry').onclick=()=>void options.act(send).then(settle);
  get('profile-grants-discard').onclick=()=>{if(options.busy())return;pending.clear();kept='';store();sync();};
  return {sync,
    // Closing the dialog keeps a kept request but no grant.
    hide(){clear();setError();sync();},
    // This identity's kept request stays offered whether or not its grants can be read.
    reset(){
      clear();setError();pending.clear();kept='';
      let stored:string|null=null;try{stored=sessionStorage.getItem(storage());}catch{/* Nothing is kept. */}
      const restored=keptRevoke(stored);if(restored){pending.command=restored.command;kept=restored.agent;}
      sync();
    }};
}
