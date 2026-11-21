import {decimalText,liveSurface,type Measure,type SurfaceExpiry,type SurfaceSeries,type VolSurfaceResult} from './vol-surface-view.js';

// The option chain of one expiry as the Options page draws it. Every figure is the stored surface's: this module orders,
// selects and formats them for display and computes none.

/** One displayed figure, or a dash with the code of why there is none. */
export type Cell={text:string;missing:string|null};
/** One series of a chain row: its settlement and implied volatility, whether it is in the money, the lots held, and its sensitivities. */
export type ChainSide={code:string;settlement:string|null;impliedVolatility:Cell;inTheMoney:boolean|null;heldLots:string|null;delta:Cell;gamma:Cell;vega:Cell;theta:Cell};
export type ChainRow={strike:string;atTheMoney:boolean;call:ChainSide|null;put:ChainSide|null};

const dash='—';
const absent=(missing:string):Cell=>({text:dash,missing});

/**
 * Exact decimal text kept to `digits` significant figures, rounding half to even. Display only: sensitivities span several
 * orders of magnitude across products, so a fixed number of decimals would hide the small ones.
 */
export function significantText(value:string,digits:number):string{
 const match=/^-?(\d+)(?:\.(\d+))?$/.exec(value);
 if(!match)throw Error('The surface returned a value that is not exact decimal text.');
 const whole=match[1].replace(/^0+/,''),fraction=match[2]??'';
 if(!whole&&!/[1-9]/.test(fraction))return '0';
 const places=whole?Math.max(0,digits-whole.length):fraction.search(/[1-9]/)+digits;
 const text=decimalText(value,{places});
 // Rounding can carry into a new leading digit (0.99995 to 1.0000): one decimal fewer keeps the figure count.
 return places>0&&text.replace(/^-/,'').replace('.','').replace(/^0+/,'').length>digits?decimalText(text,{places:places-1}):text;
}

const reasons:Record<string,string>={
 option_at_intrinsic:'Valued at intrinsic, so it has no sensitivities',
 sensitivity_unstable:'Estimates at the half, whole and double bump did not agree',
 valuation_time_coverage:'The next day reaches this option’s expiry',
 option_sensitivity_clock:'No calendar clock could be read for this close',
 not_calibrated:'Not calibrated at this close',
 series_not_fitted:'This series is not in the stored surface',
 incomplete:'A position in this sum has no figure',
 theta_days_differ:'These thetas run to different days, so they are not one sum',
 market_inputs_missing:'This close has no market inputs for the product’s definition'};
/** Why a figure is missing, in words where the page knows the code. */
export const measureReason=(code:string)=>Object.hasOwn(reasons,code)?reasons[code]:code;

/** The expiries a chain can be drawn for: every one that has not expired, in the surface's order. */
export const chainExpiries=(surface:VolSurfaceResult):SurfaceExpiry[]=>liveSurface(surface).surface.expiries;

function side(series:SurfaceSeries,held:ReadonlyMap<string,string>):ChainSide{
 const analytics=series.analytics,outcome=series.series.outcome;
 const figure=(measure:Measure|undefined,format:(value:string)=>string):Cell=>
  measure?.value!=null?{text:format(measure.value),missing:null}:absent(measure?.missing??outcome);
 return {code:series.series.code,settlement:series.series.settlement,inTheMoney:series.inTheMoney,heldLots:held.get(series.series.code)??null,
  impliedVolatility:series.series.marketVolatility!==null?{text:decimalText(series.series.marketVolatility,{shift:2,places:1})+'%',missing:null}:absent(outcome),
  delta:figure(analytics?.delta,value=>decimalText(value,{places:2})),gamma:figure(analytics?.gamma,value=>significantText(value,4)),
  vega:figure(analytics?.vega,value=>significantText(value,4)),theta:figure(analytics?.theta,value=>significantText(value,4))};
}

/** One row per strike the expiry lists, in the backend's strike order, each with its call and its put where one is listed. */
export function chainRows(surface:VolSurfaceResult,expiryAt:string,held:ReadonlyMap<string,string>,underlying?:string):ChainRow[]{
 const expiry=surface.expiries.find(e=>e.expiryAt===expiryAt&&(!underlying||e.underlyingCode===underlying));
 if(!expiry)throw Error('The surface has no such expiry.');
 const listed=new Map<string,SurfaceSeries>();
 for(const series of surface.series.filter(s=>s.series.expiryAt===expiryAt&&(!underlying||s.series.underlyingCode===underlying))){
  const key=series.series.strike+'|'+series.series.right;
  if(listed.has(key))throw Error('The surface lists a series twice.');
  if(!surface.strikeAxis.includes(series.series.strike))throw Error('The surface lists a series outside its strike axis.');
  listed.set(key,series);
 }
 return surface.strikeAxis.filter(strike=>listed.has(strike+'|call')||listed.has(strike+'|put')).map(strike=>{
  const call=listed.get(strike+'|call'),put=listed.get(strike+'|put');
  return {strike,atTheMoney:strike===expiry.atTheMoneyStrike,call:call?side(call,held):null,put:put?side(put,held):null};
 });
}
