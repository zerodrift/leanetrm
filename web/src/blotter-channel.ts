// The message channel between the main window and a popped-out Blotter (docs/blotter-workbench.md, Pop-out Blotter).
// Messages carry selections only, never figures: the pop-out reads every value itself under its own session cookie. Both
// ends read each message strictly (own session, version 1, known kind, no extra fields) and drop what they refuse.
import {parseBlotterViews,type BlotterView} from './blotter-layout.js';
import type {PageState} from './assistant-pages.js';

export type EndReason='signed_out'|'account_changed'|'main_closed';
export type BlotterMessage=
  {v:1;session:string;kind:'context';context:{snapshotId:string;knowledgeMode:string;contextId:string|null;profile:string}}
  |{v:1;session:string;kind:'view';view:BlotterView}
  |{v:1;session:string;kind:'opened'}|{v:1;session:string;kind:'closed'}
  |{v:1;session:string;kind:'ended';reason:EndReason}
  |{v:1;session:string;kind:'pageReport';state:PageState}
  |{v:1;session:string;kind:'askAbout';key:string};

const refuse=(why:string):never=>{throw Error(`Blotter window message refused: ${why}.`);};
const isObject=(x:unknown):x is Record<string,unknown>=>typeof x==='object'&&x!==null&&!Array.isArray(x);
type Check=(x:unknown)=>boolean;
const str:Check=x=>typeof x==='string',bool:Check=x=>typeof x==='boolean',int:Check=x=>Number.isInteger(x);
const oneOf=(...allowed:string[]):Check=>x=>typeof x==='string'&&allowed.includes(x);
const orNull=(check:Check):Check=>x=>x===null||check(x);
const strings:Check=x=>Array.isArray(x)&&x.every(str);
/** An object with exactly these fields, each passing its check. */
const shape=(fields:Record<string,Check>):Check=>x=>isObject(x)&&Object.keys(x).length===Object.keys(fields).length&&Object.entries(fields).every(([k,check])=>k in x&&check(x[k]));
const check=(what:string,ok:boolean)=>{if(!ok)refuse(what);};

const selection=shape({mode:oneOf('trades','pivot'),bookIds:strings,portfolioIds:strings,groupBy:strings,measures:strings});
const tradeShown=shape({tradeId:str,instrumentType:oneOf('future','financial_swap','heat_rate','ppa'),revision:int,reference:str});
// One entry per PageState variant (assistant-pages.ts); the fields besides `page` are checked exactly.
const pageStates:Record<string,Record<string,Check>>={
  market:{curve:orNull(str),tab:oneOf('prices','dependencies','inputs')},power:{frame:bool},configuration:{},schedules:{},
  reference:{table:str,search:str},parties:{kind:str},
  futures:{risk:bool,trade:orNull(tradeShown),entry:orNull(shape({product:orNull(oneOf('futures','swaps'))})),lens:oneOf('values','risk'),view:selection},
  jobs:{job:orNull(str)},ppa:{trade:orNull(shape({tradeId:str,revision:int})),book:orNull(str)},'custom-curves':{curve:orNull(str),unsaved:bool},
  delivery:{},'interval-series':{},'interval-curves':{},admin:{},ai:{}};
function pageState(state:unknown):PageState{
  check('the page state is not an object',isObject(state)&&typeof state.page==='string'&&Object.hasOwn(pageStates,state.page));
  const {page,...fields}=state as Record<string,unknown>;
  check(`the ${page} page state is malformed`,shape(pageStates[page as string])(fields));
  return state as PageState;
}
/** A view exactly as the saved-view rules would keep it: unknown fields or values they would drop or fix are refused. */
function view(value:unknown):BlotterView{
  const kept=parseBlotterViews(JSON.stringify([value]))[0];
  check('the view is not in its saved form',JSON.stringify(canonical(kept))===JSON.stringify(canonical(JSON.parse(JSON.stringify(value)))));
  return kept;
}
const canonical=(x:unknown):unknown=>Array.isArray(x)?x.map(canonical):isObject(x)?Object.fromEntries(Object.keys(x).sort().map(k=>[k,canonical(x[k])])):x;

const bodies:Record<string,(m:Record<string,unknown>)=>void>={
  context:m=>check('the context is malformed',shape({snapshotId:str,knowledgeMode:str,contextId:orNull(str),profile:str})(m.context)),
  view:m=>{view(m.view);},
  opened:()=>{},closed:()=>{},
  ended:m=>check('the end reason is unknown',oneOf('signed_out','account_changed','main_closed')(m.reason)),
  pageReport:m=>{pageState(m.state);},
  askAbout:m=>check('the position key is not text',str(m.key))};
const fieldsOf:Record<string,string[]>={context:['context'],view:['view'],opened:[],closed:[],ended:['reason'],pageReport:['state'],askAbout:['key']};

/** The message if it is version 1, for this session, of a known kind and exactly shaped; otherwise throws and nothing is applied. */
export function readBlotterMessage(value:unknown,session:string):BlotterMessage{
  check('it is not an object',isObject(value));
  const m=value as Record<string,unknown>;
  check('it is not version 1',m.v===1);
  check('it belongs to another session',m.session===session);
  check('its kind is unknown',typeof m.kind==='string'&&Object.hasOwn(bodies,m.kind));
  const kind=m.kind as string;
  check('it has extra fields',Object.keys(m).length===3+fieldsOf[kind].length&&fieldsOf[kind].every(k=>k in m));
  bodies[kind](m);
  return m as BlotterMessage;
}

/** Opens the pop-out channel for one session. A message that cannot be read, or whose receiver throws, is dropped; the channel stays open. */
export function openBlotterChannel(session:string,receive:(m:BlotterMessage)=>void):{send(m:BlotterMessage):void;close():void}{
  const channel=new BroadcastChannel('openetrm-blotter');
  channel.onmessage=e=>{try{receive(readBlotterMessage(e.data,session));}catch{/* refused or failed: not delivered, and nothing reaches the event loop */}};
  return {send:m=>channel.postMessage(readBlotterMessage(m,session)),close:()=>{channel.onmessage=null;channel.close();}};
}
