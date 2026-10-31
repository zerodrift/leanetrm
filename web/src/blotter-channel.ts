// The message channel between the main window and a popped-out Blotter (docs/blotter-workbench.md, Pop-out Blotter).
// Messages carry selections only, never figures: the pop-out reads every value itself under its own session cookie. Both
// ends read each message strictly (own session, version 1, known kind, no extra fields) and drop what they refuse.
import {parseBlotterViews,type BlotterView} from './blotter-layout.js';
import type {PageState} from './assistant-pages.js';
import type {GridLayout} from './data-grid.js';

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
// Every parser returns a fresh value built from what it checked, never the received object, so nothing unchecked (extra
// properties, odd prototypes, holes) travels on to a receiver.
type Parse<T>=(x:unknown)=>T;
const maxText=200;
const str:Parse<string>=x=>typeof x==='string'&&x.length<=maxText?x:refuse('text is missing or too long');
const bool:Parse<boolean>=x=>typeof x==='boolean'?x:refuse('a flag is not true or false');
const revision:Parse<number>=x=>Number.isSafeInteger(x)&&(x as number)>=1?x as number:refuse('a revision is not a positive whole number');
const oneOf=<T extends string>(...allowed:T[]):Parse<T>=>x=>allowed.includes(x as T)?x as T:refuse('a choice is unknown');
const orNull=<T>(parse:Parse<T>):Parse<T|null>=>x=>x===null?null:parse(x);
const list=<T>(parse:Parse<T>):Parse<T[]>=>x=>Array.isArray(x)&&Object.keys(x).length===x.length?x.map(parse):refuse('a list is malformed');
/** An object with exactly these own fields, each parsed. */
const shape=<T extends Record<string,unknown>>(fields:{[K in keyof T]:Parse<T[K]>}):Parse<T>=>x=>{
  const names=Object.keys(fields);
  if(!isObject(x)||Object.keys(x).length!==names.length||!names.every(k=>Object.hasOwn(x,k)))refuse('an object has missing or extra fields');
  return Object.fromEntries(names.map(k=>[k,(fields as Record<string,Parse<unknown>>)[k]((x as Record<string,unknown>)[k])])) as T;
};
const record=<T>(parse:Parse<T>):Parse<Record<string,T>>=>x=>isObject(x)?Object.fromEntries(Object.keys(x).map(k=>[k,parse(x[k])])):refuse('a map is malformed');
const strings=list(str);

const selection=shape({mode:oneOf('trades','pivot'),bookIds:strings,portfolioIds:strings,groupBy:strings,measures:strings});
const tradeShown=shape({tradeId:str,instrumentType:oneOf('future','financial_swap','heat_rate','ppa'),revision,reference:str});
const gridLayout:Parse<GridLayout>=shape({order:strings,widths:record(x=>Number.isSafeInteger(x)?x as number:refuse('a width is not a whole number')),sort:list(shape({key:str,direction:oneOf('asc','desc')}))});
// One entry per PageState variant (assistant-pages.ts); the fields besides `page` are parsed exactly.
const pageStates:Record<string,Parse<object>>={
  market:shape({curve:orNull(str),tab:oneOf('prices','dependencies','inputs')}),power:shape({frame:bool}),configuration:shape({}),schedules:shape({}),
  reference:shape({table:str,search:str}),parties:shape({kind:str}),
  futures:shape({risk:bool,trade:orNull(tradeShown),entry:orNull(shape({product:orNull(oneOf('futures','swaps'))})),lens:oneOf('values','risk'),view:selection}),
  jobs:shape({job:orNull(str)}),ppa:shape({trade:orNull(shape({tradeId:str,revision})),book:orNull(str)}),'custom-curves':shape({curve:orNull(str),unsaved:bool}),
  delivery:shape({}),'interval-series':shape({}),'interval-curves':shape({}),admin:shape({}),ai:shape({})};
function pageState(state:unknown):PageState{
  if(!isObject(state)||typeof state.page!=='string'||!Object.hasOwn(pageStates,state.page))refuse('the page state is unknown');
  const {page,...fields}=state as Record<string,unknown>;
  return {page,...pageStates[page as string](fields)} as PageState;
}
// Only plain data: no Date, Map, class instance or odd prototype that JSON would flatten into something valid.
const isPlain=(x:unknown):boolean=>x===null||typeof x!=='object'?typeof x!=='function'&&typeof x!=='symbol'&&typeof x!=='bigint'&&typeof x!=='undefined'
  :Array.isArray(x)?x.every(isPlain):Object.getPrototypeOf(x)===Object.prototype&&Object.values(x).every(isPlain);
const canonical=(x:unknown):unknown=>Array.isArray(x)?x.map(canonical):isObject(x)?Object.fromEntries(Object.keys(x).sort().map(k=>[k,canonical(x[k])])):x;
/** A view exactly as the saved-view rules keep it, with an exact grid: anything they would drop, fix or pass through unchecked is refused. */
function view(value:unknown):BlotterView{
  if(!isPlain(value))refuse('the view is not plain data');
  const kept=parseBlotterViews(JSON.stringify([value]))[0];
  if(JSON.stringify(canonical(kept))!==JSON.stringify(canonical(value)))refuse('the view is not in its saved form');
  return {...kept,grid:gridLayout((value as BlotterView).grid)};
}

const knowledgeModes=['current','retained'] as const;
// What follows `v`, `session` and `kind`, parsed exactly.
const bodies:Record<string,Parse<Record<string,unknown>>>={
  context:shape({context:x=>{
    const c=shape({snapshotId:str,knowledgeMode:oneOf(...knowledgeModes),contextId:orNull(str),profile:str})(x);
    // A retained read names its context and a current one names none.
    if((c.knowledgeMode==='retained')!==(c.contextId!==null))refuse('the knowledge mode and context id disagree');
    return c;}}),
  view:shape({view}),opened:shape({}),closed:shape({}),
  ended:shape({reason:oneOf<EndReason>('signed_out','account_changed','main_closed')}),
  pageReport:shape({state:pageState}),askAbout:shape({key:str})};

/** The message if it is version 1, for this session, of a known kind and exactly shaped; otherwise throws and nothing is applied. The result is a new object. */
export function readBlotterMessage(value:unknown,session:string):BlotterMessage{
  if(!isObject(value))refuse('it is not an object');
  const m=value as Record<string,unknown>;
  if(m.v!==1)refuse('it is not version 1');
  if(m.session!==session)refuse('it belongs to another session');
  if(typeof m.kind!=='string'||!Object.hasOwn(bodies,m.kind))refuse('its kind is unknown');
  const {v,session:_,kind,...body}=m;
  return {v:1,session,kind,...bodies[kind as string](body)} as BlotterMessage;
}

/** Opens the pop-out channel for one session. A message that cannot be read is dropped silently; a receiver that throws is reported; the channel stays open and nothing reaches its event loop. */
export function openBlotterChannel(session:string,receive:(m:BlotterMessage)=>void):{send(m:BlotterMessage):void;close():void}{
  const channel=new BroadcastChannel('openetrm-blotter');
  channel.onmessage=e=>{
    let m:BlotterMessage;
    try{m=readBlotterMessage(e.data,session);}catch{return;/* refused: not delivered */}
    try{receive(m);}catch(error){console.error('The Blotter window message handler failed.',error);}
  };
  return {send:m=>channel.postMessage(readBlotterMessage(m,session)),close:()=>{channel.onmessage=null;channel.close();}};
}
