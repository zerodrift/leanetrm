import {readGasView} from './gas-trade-view.js';
import {prepareAmountDisplay} from './amount-display.js';
import {commodityColors,holdingFormats,positionRows,type Holding} from './desk-model.js';
import {displayInstant,publicText,setCurveNames} from './market-view.js';
import {readSelectedPosition} from './position-facts.js';
import {setupPositionPanel,type DetailRead} from './position-panel.js';
import {positionWindowRequest,readPositionWindowUrl,positionWindowNames,type PositionWindowSelection} from './position-window-selection.js';
import {readPpaView} from './ppa-trade-view.js';
import {readSwapView} from './swap-editor.js';
import {readTradeView} from './trade-editor.js';

const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
const error=get('position-window-error'),detail=get('position-window-detail'),retry=get<HTMLButtonElement>('position-window-retry');
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
const reads=new Set(['trading/gas','trading/position','futures/editor','swaps/editor','swaps/quantity-schedule','trade-formats/read','ppa/editor','ppa/preview','ppa/inspect','amounts/display']);
let controller:AbortController|undefined,generation=0,loading=false,selection:PositionWindowSelection|undefined,reader:Api;
const panel=setupPositionPanel({element:name=>get('position-window-'+name),form:on=>{if(on)throw Error('This trade window is read only.');},reveal(){},focusForm(){}},
 {zone:()=>selection!.zone,busy:()=>loading,canAmend:()=>false,tabs:['terms','history'],curveListed:()=>false,context:()=>JSON.stringify(selection),
  read:async(h,formats)=>{
   const api=reader,context={...positionWindowRequest(selection!).context,
    convention:h.instrumentType==='future'?'contract_lot_execution_pnl_v2':'independent_side_financial_swap_v2'};
   const value:DetailRead=await(h.instrumentType==='physical_gas'?readGasView(api,context,h,()=>{}):h.instrumentType==='ppa'?readPpaView(api,context,h,selection!.context.knownAt):h.instrumentType==='future'?
    readTradeView(api,context,h,formats):readSwapView(api,context,h,formats,{busy:()=>loading,act:async work=>{await work();},settle(){throw Error('This trade window is read only.');}}));
   return {...value,view:zone=>({...value.view(zone),actions:undefined})};
  },amendment:{open(){throw Error('This trade window is read only.');},shown:()=>false,close:async()=>{}},onDenied:()=>fail('Current authority no longer permits this trade read.')});

function clearTrade(){
 panel.clear();detail.hidden=true;setCurveNames([]);document.title='Trade details · LeanETRM';get('position-window-title').textContent='Trade details';get('position-window-context').textContent='';
 for(const name of ['context','knownAt','asOf'])delete detail.dataset[name];
}
function fail(message:string){clearTrade();error.textContent=publicText(message);error.hidden=false;}
async function load(){
 const started=++generation;controller?.abort();const current=controller=new AbortController();clearTrade();error.textContent='';error.hidden=true;
 loading=true;retry.disabled=true;document.body.setAttribute('aria-busy','true');
 try{
  selection=readPositionWindowUrl(location.href);const picked=selection,root=picked.example==='exchange'?'/examples/exchange/api/':'/api/';let csrf='';
  async function request<T>(path:string,body?:unknown):Promise<T>{
   const response=await fetch(root+path,{method:body===undefined?'GET':'POST',credentials:'same-origin',headers:body===undefined?{}:{'Content-Type':'application/json','X-Preview-CSRF':csrf},
    body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.any([current.signal,AbortSignal.timeout(20000)])});
   if(!response.ok){const refusal=await response.json().catch(()=>({}));const message=response.status===401?'Sign in to the workspace, then read this trade again.':refusal.message??'The retained trade could not be read.';
    if(started===generation&&(response.status===401||response.status===403))fail(message);
    throw Object.assign(Error(message),{code:refusal.code??'request_failed'});}
   return await response.json() as T;
  }
  const api:Api=async<T>(path:string,body?:unknown)=>{
   if(!reads.has(path))throw Error('This trade window supports authorized reads only.');
   const value=await request<T>(path,body);
   if(path!=='amounts/display')await prepareAmountDisplay(value,body=>request<string[]>('amounts/display',body));
   return value;
  };
  const boot=await request<{csrfToken:string;profile:string|null}>('bootstrap?includeFrames=false&includeAccess=true');
  if(!boot.profile||!boot.csrfToken)throw Error('Open this trade from an authorized Blotter session.');csrf=boot.csrfToken;
  const response=await api<{curveNames:unknown}>('trading/position',positionWindowRequest(picked));
  const read=readSelectedPosition(response,picked.context,picked.asOf,picked),h:Holding=read.holding;
  if(started!==generation||current.signal.aborted)return;
  setCurveNames(positionWindowNames(response.curveNames));
  reader=api;loading=false;document.title=`${h.tradeReference} · Trade details · LeanETRM`;get('position-window-title').textContent=publicText(h.tradeReference);
  get('position-window-context').textContent=`As of ${displayInstant(read.asOf,picked.zone)} · Known at ${displayInstant(read.context.knownAt,picked.zone)} · Read only`;
  detail.dataset.context=read.context.contextId;detail.dataset.knownAt=read.context.knownAt;detail.dataset.asOf=read.asOf;
  detail.hidden=false;panel.show({holding:h,row:positionRows([h])[0],formats:holdingFormats(h),shape:'This window explores retained trade terms and schedules.',
   color:commodityColors([h.commodity.code]).get(h.commodity.code)!,risk:{state:'none'}});
 }catch(refusal){if(started===generation&&!current.signal.aborted)fail(refusal instanceof Error?refusal.message:'The retained trade could not be read.');}
 finally{if(started===generation){loading=false;retry.disabled=false;document.body.setAttribute('aria-busy','false');panel.sync();}}
}
retry.onclick=()=>void load();get('position-window-close').onclick=()=>window.close();
window.addEventListener('hashchange',()=>void load());
window.addEventListener('pagehide',()=>{generation++;controller?.abort();panel.clear();},{once:true});
void load();
