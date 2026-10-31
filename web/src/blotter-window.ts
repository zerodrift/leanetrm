// The Blotter in a window of its own (docs/blotter-workbench.md, Pop-out Blotter), tied to the main window that opened it. It takes the view
// the main window hands over and the market selection its header makes, and reads every value itself through the API under the session
// both windows share: it verifies on each read that the account and workspace are still the ones the main window named, and stops showing
// values, saying why, when they are not, or when the main window signs out, changes account or closes. As it closes it hands its view back.
import {apiRefusal} from './api-refusal.js';
import {prepareAmountDisplay,shownAmounts} from './amount-display.js';
import {openBlotterChannel,type EndReason} from './blotter-channel.js';
import {readBlotterWindowUrl,tiePopoutWindow,type BlotterContext} from './blotter-popout.js';
import type {BlotterView} from './blotter-layout.js';
import {dockedGroupsFrom} from './dock-model.js';
import {setupFutures} from './futures-view.js';
import {validateMarketNavigation,type MarketNavigation} from './market-navigation.js';
import {displayInstant,publicText,setCurveNames} from './market-view.js';
import {bookingFallback} from './new-trade.js';
import {withNotice} from './notice-text.js';
import {loadPreferences,newPreferences,type Preferences} from './preferences.js';

const $=<T extends HTMLElement=HTMLElement>(id:string):T=>{const node=document.getElementById(id);if(!node)throw Error('This window could not load. Close it and pop the Blotter out again.');return node as T;};
const text=(id:string,value:string)=>{$(id).textContent=publicText(value);};
const ends:Record<EndReason,string>={signed_out:'Signed out in the main window',account_changed:'The account changed in the main window',main_closed:'The main window was closed'};

// The session and example come from the fragment, which is taken off the address at once: a reload or a copy of this window is not tied.
let tied:{session:string;example:'baseline'|'exchange'}|undefined,untied:string|undefined;
try{tied=readBlotterWindowUrl(location.href);}catch(error){untied=(error as Error).message;}
history.replaceState(null,'',location.pathname);
if(tied&&!window.opener)untied='This Blotter window has no main window. Close it, and pop the Blotter out from the main window.';
const apiRoot=tied?.example==='exchange'?'/examples/exchange/api/':'/api/';

let busy=false,ended=false,csrf='',permissions:string[]=[],profile='',scope='',navigation:MarketNavigation|undefined;
let prefs:Preferences=newPreferences(Intl.DateTimeFormat().resolvedOptions().timeZone);
// The context the main window last sent, and the one this window's Blotter reads at.
let latest:BlotterContext|undefined,applied:BlotterContext|undefined,handed:BlotterView|undefined,started=false;
let liveness:ReturnType<typeof setInterval>|undefined,asking=false;
const main=document.querySelector('main')!;
const instant=(value:string)=>displayInstant(value,prefs.timeZone);
const can=(permission:string)=>permissions.includes(permission);
const knowledge=()=>navigation?.live?'live' as const:applied?.knowledgeMode==='retained'?'historical' as const:'working' as const;
function notice(message:string){const shown=$('notice');text('notice',withNotice(shown.hidden?'':shown.textContent??'',publicText(message)));shown.hidden=false;}
function failed(error:unknown){text('error',error instanceof Error?error.message:'Something went wrong in this window.');$('error').hidden=false;text('status','Please check the message above.');}

async function transport<T>(path:string,body?:unknown,signal?:AbortSignal):Promise<T>{
  let response:Response;
  try{response=await fetch(apiRoot+path,{method:body===undefined?'GET':'POST',credentials:'same-origin',headers:body===undefined?{}:{'Content-Type':'application/json','X-Preview-CSRF':csrf},
    body:body===undefined?undefined:JSON.stringify(body),signal:signal?AbortSignal.any([signal,AbortSignal.timeout(20000)]):AbortSignal.timeout(20000)});}
  catch{throw Object.assign(Error(body!==undefined?'We could not confirm the result. Check the Blotter before trying again.':'This window could not connect.'),{code:'response_unknown'});}
  if(response.status===401){end('Signed out · sign in again in the main window');throw Object.assign(Error('Sign in to continue.'),{code:'sign_in_required'});}
  if(!response.ok)throw await apiRefusal(path,response,false);
  return response.json() as Promise<T>;
}
async function api<T>(path:string,body?:unknown,signal?:AbortSignal):Promise<T>{
  const payload=await transport<T>(path,body,signal),displayed=shownAmounts(path,payload);
  if(displayed!==undefined)await prepareAmountDisplay(displayed,request=>transport<string[]>('amounts/display',request,signal));
  return payload;
}

function syncControls(){
  document.querySelectorAll<HTMLButtonElement|HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement>('button,input,select,textarea').forEach(n=>n.disabled=busy||ended);
  $<HTMLButtonElement>('blotter-window-close').disabled=false;
  futures.sync();document.body.setAttribute('aria-busy',String(busy));
}
// Actions run one at a time. Each ends by checking that the account is still the main window's, and then reports what the window shows.
async function act(work:()=>Promise<void>){
  if(busy||ended)return;
  busy=true;syncControls();$('error').hidden=true;$('notice').hidden=true;text('status','Loading…');
  try{await work();text('status','Blotter · its own window, tied to the main window.');}
  catch(error){if(!ended)failed(error);}
  finally{busy=false;syncControls();}
  if(!ended&&started)void verify().then(report).catch(error=>{if(!ended)failed(error);});
}
const whenIdle=(work:()=>void)=>{if(busy)setTimeout(()=>whenIdle(work),50);else work();};

// The account, workspace and permissions of this window's session, which must be those the main window named.
async function verify(){
  const boot=await transport<{csrfToken:string;profile:string|null;runId:string;tenantId:string;workspaceId:string;access:{profile:string;permissions:string[]}|null}>('bootstrap?includeFrames=false&includeAccess=true');
  const identity=[boot.runId,boot.tenantId,boot.workspaceId,boot.profile].join(':');
  if(!boot.profile||!boot.csrfToken||!boot.access||boot.access.profile!==boot.profile||!Array.isArray(boot.access.permissions))throw Error('This window could not verify its account. Close it and pop the Blotter out again.');
  if(identity!==latest?.profile){end('This window’s account is not the main window’s, so it shows no values');throw Error('The account changed.');}
  if(profile!==boot.profile){
    profile=boot.profile;
    try{prefs=loadPreferences(localStorage,tied!.example==='baseline'?profile:tied!.example+':'+profile,Intl.DateTimeFormat().resolvedOptions().timeZone);}
    catch{prefs=newPreferences(Intl.DateTimeFormat().resolvedOptions().timeZone);notice('Your saved profile could not be loaded. This window uses your browser’s time zone.');}
  }
  csrf=boot.csrfToken;scope=identity;permissions=[...boot.access.permissions];
}
// The market navigation this window reads, which must name the close and the knowledge the main window selects.
async function readNavigation(context:BlotterContext){
  const next=validateMarketNavigation(await api<MarketNavigation>('workspace/navigation'));
  const sameKnowledge=context.knowledgeMode==='retained'?next.context?.contextId===context.contextId:next.context===null;
  if(next.profile!==profile||!sameKnowledge||!next.frames.some(f=>f.id===context.snapshotId))
    throw Error('The market date or context the main window shows could not be read in this window. Choose it again in the main window.');
  navigation=next;setCurveNames(next.curveNames);
}

const futures=setupFutures({window:{kind:'popout',close:()=>window.close()},api,act,busy:()=>busy,zone:()=>prefs.timeZone,profile:()=>scope,knowledge,
  narrow:()=>main.clientWidth<dockedGroupsFrom,canRunRisk:()=>can('RunScenario'),bookingUnavailableReason:()=>bookingFallback({contextDirty:false,view:knowledge(),canBook:can('BookTrade')}),
  onReadContext:(at,known)=>text('header-calculation-time',`As of ${instant(at)} · knowledge cutoff ${instant(known)}`),failed,failureCleared:()=>{$('error').hidden=true;},notice,
  definitionRevision:()=>{if(!navigation)throw Error('The market navigation is still loading.');return navigation.definitionRevision.revisionId;},
  // This window has no navigation of saved views, Curves & prices or PPA bookings: those are the main window's.
  onViews:()=>undefined,curveListed:()=>false,openCurve:async()=>{throw Error('Curves & prices opens in the main window.');},newPpa:async()=>{throw Error('PPA bookings open in the main window.');},
  source:()=>{
    if(!applied||!navigation)throw Error('The main window has not handed over the Blotter yet.');
    return {snapshotId:applied.snapshotId,convention:navigation.futuresConvention,...(applied.contextId===null?{}:{context:{contextId:applied.contextId}})};
  },
  reported:report,unsavedChanged:syncUnloadQuestion,
  assistant:{available:()=>can('UseAgents'),about:({key})=>{try{tie?.ask(key);window.opener?.focus();}catch(error){failed(error);}}}});

// What the Blotter shows here, for the main window's assistant.
function report(){if(!ended&&started)try{tie?.report({page:'futures',...futures.report()});}catch(error){console.error('The Blotter window could not report what it shows.',error);}}
// The Blotter at the main window's selection, once the account is checked: the navigation read for it, and the Blotter read afresh at it.
async function readAt(context:BlotterContext){
  applied=context;await readNavigation(context);
  $('futures-page').hidden=false;
  futures.reset('inputs');await futures.load();
}
function start(){
  if(started||ended||!handed||!latest)return;
  started=true;const view=handed;
  void act(async()=>{await verify();futures.adopt(view);await readAt(latest!);});
}
// A selection made in the main window is applied here at once, once this window is idle; edits a form here held at the last one are given up, which is said.
function follow(context:BlotterContext){
  latest=context;
  if(!started){start();return;}
  whenIdle(()=>{
    if(ended||JSON.stringify(latest)===JSON.stringify(applied))return;
    const edits=futures.editing(),next=latest!;
    futures.leavePage();
    void act(async()=>{
      try{await verify();await readAt(next);}catch(error){futures.reset('inputs');throw error;}
      if(edits)notice('The main window changed the market selection, so the edits in this window’s details were given up.');
    });
  });
}
// The window stops showing values and says why; it keeps nothing it read.
function end(why:string){
  if(ended)return;
  ended=true;tie?.close();clearInterval(liveness);
  futures.leavePage();futures.reset(null);setCurveNames([]);navigation=undefined;applied=undefined;
  $('futures-page').hidden=true;text('header-calculation-time','');$('error').hidden=true;$('notice').hidden=true;
  text('blotter-window-ended',why);$('blotter-window-ended').hidden=false;$('blotter-window-close').hidden=false;text('status','');
  syncUnloadQuestion();syncControls();
}

const tie=tied&&!untied?tiePopoutWindow({session:tied.session,instance:crypto.randomUUID(),channel:openBlotterChannel,
  handed:view=>{handed=view;start();},context:follow,ended:reason=>end(ends[reason])}):undefined;
if(!tie)end(untied!);
// A main window gone without a word, as one that crashed, is noticed too.
if(tie)liveness=setInterval(()=>{if(!window.opener||window.opener.closed)end(ends.main_closed);},1000);
// Closing hands the view back; the browser asks first while the details hold edits or a trade request awaits its outcome here.
window.addEventListener('pagehide',()=>{if(!ended)tie?.leave(started?futures.handOff():undefined);ended=true;clearInterval(liveness);});
function askBeforeUnload(event:BeforeUnloadEvent){event.preventDefault();event.returnValue='';}
function syncUnloadQuestion(){
  const holds=!ended&&(futures.editing()||futures.unsaved());
  if(holds===asking)return;
  asking=holds;
  if(holds)window.addEventListener('beforeunload',askBeforeUnload);else window.removeEventListener('beforeunload',askBeforeUnload);
}
$('blotter-window-close').onclick=()=>window.close();
syncControls();
