import {compareDecimalStrings} from './analytics-model.js';
import {deliveryLabel} from './market-view.js';
import {bookingAllowed,unappliedContext} from './new-trade.js';

// The option ticket's selection of a listed series (D288): exchange and commodity, then the underlying future, then call or put, then
// strike. Each list offers only what the catalog lists under the selections above it, in the catalog's order; nothing is chosen for the
// person, and a changed selection clears every selection below it. The browser groups nothing the catalog does not state.
type Named={code:string;name:string};
/** One listed series as options/catalog returns it. */
export type OptionSeries={series:string;instrumentId:string;contractId:string;product:string;name:string;exchange:Named;commodity:Named;underlyingSeries:string;
 right:string;strike:string;expiryAt:string;exerciseStyle:string;premiumStyle:string;contraryInstructionsAllowed:boolean;multiplier:string;currency:string;
 premiumTick:string;minimumPremium:string;maximumPremium:string;curve:string;settlementPrice:string};
/** An underlying future the catalog's series exercise into, with its product's name and delivery period. */
export type OptionUnderlying={series:string;name:string;deliveryFrom:string;deliveryTo:string};
export type OptionCatalog={options:readonly OptionSeries[];underlyings:readonly OptionUnderlying[]};
export const optionLevels=['exchange','commodity','underlying','right','strike'] as const;
export type OptionLevel=typeof optionLevels[number];
export type OptionPick=Record<OptionLevel,string>;
export type OptionChoice={value:string;label:string};
export type OptionChoices=Record<OptionLevel,OptionChoice[]>&{series:OptionSeries|null};
export const noOptionPick:OptionPick=Object.freeze({exchange:'',commodity:'',underlying:'',right:'',strike:''});

const valueOf:Record<OptionLevel,(s:OptionSeries)=>string>={exchange:s=>s.exchange.code,commodity:s=>s.commodity.code,underlying:s=>s.underlyingSeries,right:s=>s.right,strike:s=>s.strike};
const rightLabel=(right:string)=>right.charAt(0).toUpperCase()+right.slice(1);

/** An underlying future as a person names it: its delivery period and its product's name. */
export const underlyingLabel=(deliveryFrom:string,deliveryTo:string,name:string)=>`${deliveryLabel(deliveryFrom,deliveryTo)} · ${name}`;
function labelOf(catalog:OptionCatalog,level:OptionLevel,s:OptionSeries):string{
 switch(level){
  case 'exchange':return s.exchange.name;
  case 'commodity':return s.commodity.name;
  case 'right':return rightLabel(s.right);
  case 'strike':return s.strike;
  case 'underlying':{
   const found=catalog.underlyings.filter(u=>u.series===s.underlyingSeries);
   if(found.length!==1)throw Error(`The option catalog does not describe the underlying future ${s.underlyingSeries} exactly once.`);
   return underlyingLabel(found[0].deliveryFrom,found[0].deliveryTo,found[0].name);
  }
 }
}

// The series under every selection above a level, each made; and the level's own choices among them, once each.
function under(catalog:OptionCatalog,pick:OptionPick,level:OptionLevel){
 const above=optionLevels.slice(0,optionLevels.indexOf(level));
 if(above.some(l=>!pick[l]))return [];
 return catalog.options.filter(s=>above.every(l=>valueOf[l](s)===pick[l]));
}
function choicesAt(catalog:OptionCatalog,pick:OptionPick,level:OptionLevel):OptionChoice[]{
 const seen=new Map<string,OptionChoice>();
 for(const s of under(catalog,pick,level)){const value=valueOf[level](s);if(!seen.has(value))seen.set(value,{value,label:labelOf(catalog,level,s)});}
 const listed=[...seen.values()];
 return level==='strike'?listed.sort((a,b)=>compareDecimalStrings(a.value,b.value)):listed;
}

/** What each list offers under the selections made, and the one series a complete selection names. */
export function optionChoices(catalog:OptionCatalog,pick:OptionPick):OptionChoices{
 const lists=Object.fromEntries(optionLevels.map(level=>[level,choicesAt(catalog,pick,level)])) as Record<OptionLevel,OptionChoice[]>;
 let series:OptionSeries|null=null;
 if(optionLevels.every(l=>pick[l])){
  const named=catalog.options.filter(s=>optionLevels.every(l=>valueOf[l](s)===pick[l]));
  if(named.length>1)throw Error('The option catalog lists more than one series for this selection.');
  series=named[0]??null;
 }
 return {...lists,series};
}

/** The selection with one level set, or cleared with '', and every level below it cleared. A value the list does not offer is refused. */
export function pickOption(catalog:OptionCatalog,pick:OptionPick,level:OptionLevel,value:string):OptionPick{
 const at=optionLevels.indexOf(level),next={...pick,[level]:value};
 for(const below of optionLevels.slice(at+1))next[below]='';
 if(value&&!choicesAt(catalog,next,level).some(c=>c.value===value))throw Error(`The ${level} '${value}' is not offered under the selections above it.`);
 return next;
}

/** A selection after the catalog was read again: each level it still offers is kept, and from the first it does not, every level is cleared. */
export function keepOffered(catalog:OptionCatalog,pick:OptionPick):OptionPick{
 let kept={...noOptionPick};
 for(const level of optionLevels){
  if(!pick[level]||!choicesAt(catalog,kept,level).some(c=>c.value===pick[level]))break;
  kept={...kept,[level]:pick[level]};
 }
 return kept;
}

/** Whether option lifecycle events may be recorded from a view, and why not when they may not. */
export type LifecycleWrites={enabled:true;reason:null}|{enabled:false;reason:string};
/**
 * Lifecycle events are recorded at current knowledge, so only a view of working data at current knowledge offers them, and only to a
 * person the backend lets book (`canBook`, with its own `reason` when not). A changed historical context not yet applied holds them back,
 * as it holds booking back. Once an event is recorded in a view, that view reads the holding as it was, so it offers none until the trade
 * is opened again.
 */
export function lifecycleWrites(state:{view:'working'|'historical'|'live'|'read_only';canBook:boolean;reason:string|null|undefined;contextDirty:boolean;recorded:boolean}):LifecycleWrites{
 if(state.recorded)return {enabled:false,reason:'An event was recorded here. Open the trade again from the Blotter to read it at current knowledge.'};
 if(state.contextDirty)return {enabled:false,reason:unappliedContext};
 if(state.view!=='working')return {enabled:false,reason:'Lifecycle events are recorded from working data at current knowledge. Return to working data to record one.'};
 if(!state.canBook)return {enabled:false,reason:state.reason??'Booking is not available in this view, and the backend did not say why.'};
 return {enabled:true,reason:null};
}
// What the option ticket says before a series is picked: why it cannot book, including when the close lists no series at all.
export function optionTicketStatus(catalog:OptionCatalog&{canBook:boolean;bookingUnavailableReason?:string|null}):string{
 const allowed=bookingAllowed(catalog);
 if(!allowed.enabled)return allowed.reason;
 return catalog.options.length?'Pick the listed series, then enter the execution.':'No options are listed at this close, so none can be booked. Choose another close or publish listed series.';
}
