import type {Holding} from './desk-model.js';
import {displayInstant} from './market-view.js';
import type {DetailRead} from './position-panel.js';
import {valuationReason,type ValuationReason} from './valuation-status.js';

type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
const product='heat_rate_call',convention='heat_rate_call_trade_v1';
type Exercise={delivery_day:string;exercise_at:string;payment_at:string;energy_per_unit:string};
type Position={tradeId:string;bindingId:string;instrumentType:string;quantity:string;unit:string;direction:string;curve:string;fuelCurve:string;
 heatRate:string;heatRateUnit:string;strike:string;currency:string;exerciseStyle:string;model:string;deliveryFrom:string;deliveryTo:string;
 measureLabel:string;unavailableReason:ValuationReason|null};
type Read={position:Position;exercises:Exercise[]};
const styles:Readonly<Record<string,string>>={daily_automatic_cash_exercise_v1:'Daily, automatic, cash settled'};
const models:Readonly<Record<string,string>>={spread_call_kirk_lognormal_v1:'Two-price spread call (Kirk)'};
const named=(names:Readonly<Record<string,string>>,code:string)=>{if(!Object.hasOwn(names,code))throw Error('The option names a version this page does not know. Refresh the Blotter.');return names[code];};

/** The retained terms and dated exercises of one booked heat-rate call option. Every value shown is the backend's. */
export async function readHeatRateCallView(api:Api,context:Record<string,unknown>,h:Holding):Promise<DetailRead>{
 if(h.instrumentType!==product)throw Error('Select an exact heat-rate call option.');
 const {position:p,exercises}=await api<Read>('heat-rate-calls/get',{context:{...context,convention},tradeId:h.tradeId});
 if(p?.tradeId!==h.tradeId||p.bindingId!==h.bindingId||p.instrumentType!==product||!Array.isArray(exercises)||!exercises.length)
  throw Error('The option response does not match its selected trade. Refresh the Blotter.');
 const unavailable=p.unavailableReason===null?null:valuationReason(p.unavailableReason,product);
 return {amend:{allowed:false,reason:'Amending a booked heat-rate call option is Coming soon.'},view:zone=>({
  termsLabel:'Option terms',
  terms:[
   {title:'Heat-rate call option',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],
    ['Side',p.direction==='buy'?'Bought':'Written'],['Capacity',`${p.quantity.replace(/^-/,'')} ${p.unit}`],
    ['Power curve',p.curve],['Fuel curve',p.fuelCurve],['Heat rate',`${p.heatRate} ${p.heatRateUnit}`],['Strike',`${p.strike} ${p.currency}`],
    ['Exercise',named(styles,p.exerciseStyle)],['Model',named(models,p.model)],['Value measure',p.measureLabel],
    ...unavailable?[['Value unavailable',unavailable] as [string,string]]:[]]},
   {title:`Exercise days (${exercises.length})`,rows:exercises.map(e=>[e.delivery_day,
    `${e.energy_per_unit} per ${p.unit} · exercise ${displayInstant(e.exercise_at,zone)} · paid ${displayInstant(e.payment_at,zone)}`] as [string,string])}],
  history:[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]}],
 })};
}
