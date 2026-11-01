// The Blotter in a window of its own (docs/blotter-workbench.md, Pop-out Blotter), tied to the main window that opened it. It takes the view
// the main window hands over and the market selection its header makes, and reads every value itself through the API under the session
// both windows share: it verifies on each read that the account and workspace are still the ones the main window named, and stops showing
// values, saying why, when they are not, or when the main window signs out, changes account or closes. As it closes it hands its view back.
import type {ProductTickets} from './product-tickets.js';
import {apiRefusal} from './api-refusal.js';
import {prepareAmountDisplay,shownAmounts} from './amount-display.js';
import {openBlotterChannel,type EndReason} from './blotter-channel.js';
import {readBlotterWindowUrl,retainedActions,tiePopoutWindow,type BlotterContext} from './blotter-popout.js';
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
const ends:Record<Exclude<EndReason,'brought_back'>,string>={signed_out:'Signed out in the main window',account_changed:'The account changed in the main window',main_closed:'The main window was closed'};

// The session and example come from the fragment, which is taken off the address at once: a reload or a copy of this window is not tied.
let tied:{session:string;example:'baseline'|'exchange'}|undefined,untied:string|undefined;
try{tied=readBlotterWindowUrl(location.href);}catch(error){untied=(error as Error).message;}
history.replaceState(null,'',location.pathname);
if(tied&&!window.opener)untied='This Blotter window has no main window. Close it, and pop the Blotter out from the main window.';
const apiRoot=tied?.example==='exchange'?'/examples/exchange/api/':'/api/';

// `ended`: the tie is over and no value is shown; `retaining`: with the main window gone, only an unconfirmed trade request is kept, with its controls.
let busy=false,ended=false,retaining=false,hosted=false,csrf='',permissions:string[]=[],profile='',scope='',navigation:MarketNavigation|undefined;
let prefs:Preferences=newPreferences(Intl.DateTimeFormat().resolvedOptions().timeZone);
// The context the main window last sent, and the one this window's Blotter reads at.
let latest:BlotterContext|undefined,applied:BlotterContext|undefined,handed:BlotterView|undefined,started=false;
let liveness:ReturnType<typeof setInterval>|undefined;
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
  if(!response.ok)throw await apiRefusal(path,response,hosted);
  return response.json() as Promise<T>;
}
// Every read is followed by a check of the account: an action's at its end, and one made in the background, as a risk job's timer makes, at once.
async function api<T>(path:string,body?:unknown,signal?:AbortSignal):Promise<T>{
  const payload=await transport<T>(path,body,signal),displayed=shownAmounts(path,payload);
  if(displayed!==undefined)await prepareAmountDisplay(displayed,request=>transport<string[]>('amounts/display',request,signal));
  if(!busy)checkAccount();
  return payload;
}
let checking=false;
function checkAccount(){
  if(checking||ended||!started)return;
  checking=true;void verify().then(report,error=>{if(!ended)failed(error);}).finally(()=>{checking=false;});
}

function syncControls(){
  // A view that keeps its controls' states itself (data-own-controls) is left as it drew them until the window ends.
  document.querySelectorAll<HTMLButtonElement|HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement>('button,input,select,textarea').forEach(n=>{if(!ended&&n.closest('[data-own-controls]'))return;n.disabled=busy||ended&&!(retaining&&n.closest('#futures-retained'));});
  $<HTMLButtonElement>('blotter-window-close').disabled=false;
  futures.sync();document.body.setAttribute('aria-busy',String(busy));
}
// Actions run one at a time. Each ends by checking that the account is still the main window's, and then reports what the window shows.
async function act(work:()=>Promise<void>){
  if(busy||ended&&!retaining)return;
  busy=true;syncControls();$('error').hidden=true;$('notice').hidden=true;text('status','Loading…');
  // A window keeping only its request checks the account before each action on it, as no other check is made once it shows no values.
  try{if(retaining)await verify();await work();text('status','Blotter · its own window, tied to the main window.');}
  catch(error){if(!ended)failed(error);}
  finally{busy=false;syncControls();}
  checkAccount();
}
const whenIdle=(work:()=>void)=>{if(busy)setTimeout(()=>whenIdle(work),50);else work();};

// The account, workspace and permissions of this window's session, which must be those the main window named.
async function verify(){
  const boot=await transport<{mode:string;csrfToken:string;profile:string|null;runId:string;tenantId:string;workspaceId:string;access:{profile:string;permissions:string[]}|null}>('bootstrap?includeFrames=false&includeAccess=true');
  // The workspace's mode decides how a refusal is worded, as the main window's does; an unknown one is refused.
  if(boot.mode!=='hosted-pilot'&&boot.mode!=='local-synthetic-preview')throw Error('The workspace mode could not be verified. Close this window and pop the Blotter out again.');
  hosted=boot.mode==='hosted-pilot';
  const identity=[boot.runId,boot.tenantId,boot.workspaceId,boot.profile].join(':');
  if(!boot.profile||!boot.csrfToken||!boot.access||boot.access.profile!==boot.profile||!Array.isArray(boot.access.permissions))throw Error('This window could not verify its account. Close it and pop the Blotter out again.');
  if(identity!==latest?.profile){end('This window’s account is not the main window’s, so it shows no values');throw Error('This window’s account is not the main window’s, so it acts on nothing here.');}
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
  return next;
}
// The selection and the navigation read for it are applied together, once both are known to agree.
function apply(context:BlotterContext,next:MarketNavigation){applied=context;navigation=next;setCurveNames(next.curveNames);}

// PPA and physical gas trades are entered and inspected in the main window, whose editors this window does not host.
const mainWindowTickets:ProductTickets={async open(product){throw Error(`${product==='ppa'?'PPA':'Physical gas'} trades are entered and inspected in the main window.`);},
  detach:()=>undefined,active:()=>undefined,unsaved:()=>false,pending:()=>[],sync:()=>undefined};
const futures=setupFutures({window:{kind:'popout',close:()=>bringBack(),example:tied?.example},api,act,busy:()=>busy,zone:()=>prefs.timeZone,profile:()=>scope,knowledge,
  narrow:()=>main.clientWidth<dockedGroupsFrom,canRunRisk:()=>can('RunScenario'),bookingUnavailableReason:()=>bookingFallback({contextDirty:false,view:knowledge(),canBook:can('BookTrade')}),
  onReadContext:(at,known)=>text('header-calculation-time',`As of ${instant(at)} · knowledge cutoff ${instant(known)}`),failed,failureCleared:()=>{$('error').hidden=true;},notice,
  definitionRevision:()=>{if(!navigation)throw Error('The market navigation is still loading.');return navigation.definitionRevision.revisionId;},
  // This window has no navigation of saved views, Curves & prices, or PPA and physical gas editors: those are the main window's.
  onViews:()=>undefined,curveListed:()=>false,openCurve:async()=>{throw Error('Curves & prices opens in the main window.');},tickets:mainWindowTickets,
  source:()=>{
    if(!applied||!navigation)throw Error('The main window has not handed over the Blotter yet.');
    return {snapshotId:applied.snapshotId,convention:navigation.futuresConvention,...(applied.contextId===null?{}:{context:{contextId:applied.contextId}})};
  },
  reported:report,unsavedChanged:report,
  assistant:{available:()=>can('UseAgents'),about:({key,bookIds,portfolioIds})=>{try{tie?.ask(key,{bookIds,portfolioIds});window.opener?.focus();}catch(error){failed(error);}}}});

// What the Blotter shows here, and whether its details hold unsaved edits, for the main window: its assistant, and the question it asks
// before a change of its own gives them up.
function report(){if(!ended&&started)try{tie?.report({page:'futures',...futures.report()},{edits:futures.editing(),requests:futures.retained()});}catch(error){console.error('The Blotter window could not report what it shows.',error);}}
// The Blotter read afresh at the main window's selection, once the account is checked and the navigation read for it.
async function readAt(context:BlotterContext){
  apply(context,await readNavigation(context));
  $('futures-page').hidden=false;
  futures.reset('inputs');await futures.load();
}
function start(){
  if(started||ended||!handed||!latest)return;
  started=true;const view=handed;
  void act(async()=>{await verify();futures.adopt(view);await readAt(latest!);});
}
// A selection made in the main window is applied here once this window is idle. A new generation of the live corrections followed is read as
// the main window reads one, keeping what the details hold (D222); any other change gives up the details' edits, which the main window asked
// about first, and which is said here.
function follow(context:BlotterContext){
  latest=context;
  if(!started){start();return;}
  whenIdle(()=>{
    if(ended||JSON.stringify(latest)===JSON.stringify(applied))return;
    const next=latest!;
    void act(async()=>{
      try{
        await verify();
        const read=await readNavigation(next),live=navigation?.live?.id;
        if(live!==undefined&&read.live?.id===live){apply(next,read);await futures.liveRefresh();return;}
        const edits=futures.editing();
        futures.leavePage();apply(next,read);
        $('futures-page').hidden=false;futures.reset('inputs');await futures.load();
        if(edits)notice('The main window changed the market selection, so the edits in this window’s details were given up.');
      }catch(error){futures.reset('inputs');throw error;}
    });
  });
}
// The trade request and risk submission this window awaits the outcome of go to the main window, which then owns them, while there is one
// to take them. Undefined once they are handed over or there are none; otherwise why they stay here.
// A main window that said it closed is not handed anything until another page has loaded in its place, as after a reload: a tab being closed
// still answers for a moment, and storage handed to it then would go with it.
// The page this window was popped out of, as it opened it, is the one that says it closed: by the time that word arrives, a reload may already
// have put another page in its place.
let goneDocument:Document|undefined;
// An opener that cannot be read, as one moved to another site, is gone: this is the one place it is read, which never throws.
function readOpener():{window:Window;document:Document}|undefined{
  try{const opener=window.opener as Window|null;return opener&&!opener.closed?{window:opener,document:opener.document}:undefined;}catch{return undefined;}
}
const openedBy=readOpener()?.document;
const mainWindow=()=>{const opener=readOpener();return opener&&(!goneDocument||opener.document!==goneDocument)?opener.window:undefined;};
// What this window keeps until a main window can take it, and what it can do with each: a trade request has its Check status and Retry here;
// a risk submission is reconciled from Recent risk jobs, which needs a main window.
function keptHere(){
  const kinds=futures.retainedKinds(),{trade,risk}=kinds;
  return `this window keeps its unconfirmed ${trade&&risk?'trade request and risk submission until a main window can take them':risk?'risk submission until a main window can take it':'trade request until a main window can take it'}: ${retainedActions(kinds)}`;
}
function handOver(){
  if(!futures.retained())return undefined;
  const target=mainWindow();
  if(!target)return `The main window is gone, so ${keptHere()}.`;
  try{return futures.handOver(target.sessionStorage);}catch{return `The main window cannot be reached, so ${keptHere()}.`;}
}
// Bring back, from this window or the main one: its requests go first, and a window that cannot hand them over stays open on them, saying
// why, and the Blotter stays here. The details' edits were asked about and are given up.
function bringBack(){
  const why=handOver();
  if(why)throw Error(why);
  futures.leavePage();
  try{tie?.leave(started?futures.handOff():undefined);}finally{ended=true;clearInterval(liveness);window.close();}
}
function broughtBack(){if(!ended)try{bringBack();}catch(error){failed(error);}}
// The window stops showing values and says why; it keeps nothing it read. One that ends for a reason of its own first hands back its view
// and says it closed, so the main window no longer relays what it showed. Requests it cannot hand over stay here alone, with Check status and
// Retry, until a main window can take them: one that closed and is gone never can; one that reloaded can once it is back.
function end(why:string,mainClosed=false){
  if(ended)return;
  if(mainClosed)goneDocument=openedBy;
  const kept=mainClosed&&futures.retained()?keptHere():handOver();
  if(started)try{tie?.leave(futures.handOff());}catch(error){console.error('The Blotter window could not hand its view back.',error);}
  tie?.close();ended=true;
  futures.leavePage();
  retaining=kept!==undefined;
  if(retaining)futures.keepOnlyRetained();else{clearInterval(liveness);futures.reset(null);$('futures-page').hidden=true;}
  setCurveNames([]);text('header-calculation-time','');$('error').hidden=true;$('notice').hidden=true;
  text('blotter-window-ended',retaining?`${why} · ${kept}`:why);$('blotter-window-ended').hidden=false;$('blotter-window-close').hidden=false;text('status','');
  syncControls();
}
// While this window keeps requests alone, a main window that can take them, as one reloaded, is handed them.
function handLater(){
  if(!retaining||busy||!mainWindow())return;
  const why=handOver();if(why)return;
  retaining=false;clearInterval(liveness);futures.reset(null);$('futures-page').hidden=true;
  text('blotter-window-ended',`${$('blotter-window-ended').textContent?.split(' · ')[0]} · its unconfirmed request went to the main window`);syncControls();
}

const tie=tied&&!untied?tiePopoutWindow({session:tied.session,instance:crypto.randomUUID(),channel:openBlotterChannel,
  handed:view=>{handed=view;start();},context:follow,ended:reason=>{if(reason!=='brought_back')end(ends[reason],reason==='main_closed');},broughtBack}):undefined;
if(!tie)end(untied!);
// A main window gone without a word, as one that crashed, is noticed too.
if(tie)liveness=setInterval(()=>{if(retaining)handLater();else if(!mainWindow())end(ends.main_closed,true);},1000);
// Closing hands the view and unconfirmed requests back. The browser asks first while the details hold edits, or a request awaits its outcome
// here and cannot be handed over.
window.addEventListener('pagehide',()=>{handOver();if(!ended)tie?.leave(started?futures.handOff():undefined);ended=true;clearInterval(liveness);});
window.addEventListener('beforeunload',event=>{
  let stranded=futures.retained();
  try{const target=mainWindow();stranded&&=!target||futures.handOverRefusal(target.sessionStorage)!==undefined;}catch{/* A main window that cannot be read takes nothing: the request is stranded. */}
  if(!ended&&futures.editing()||stranded){event.preventDefault();event.returnValue='';}
});
$('blotter-window-close').onclick=()=>window.close();
syncControls();
