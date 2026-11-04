import {priceMeasureChoices} from './blotter-layout.js';
import {exactNumber} from './exact-number.js';
// The backend formats decimal/rational strings; the browser never rounds amounts.
export type DisplayPrecision={money:number;price:number;quantity:number;factor:number};
export const initialDisplayPrecision=():DisplayPrecision=>({money:3,price:4,quantity:3,factor:6});
export function displayPrecision(value:unknown):DisplayPrecision{
 const v=value as DisplayPrecision;
 if(!v||[v.money,v.price,v.quantity,v.factor].some(n=>!Number.isInteger(n)||n<0||n>18))throw Error('Choose 0–18 decimal places for each amount display.');
 return {money:v.money,price:v.price,quantity:v.quantity,factor:v.factor};
}
type Kind=keyof DisplayPrecision;
type Request={value:string;decimalPlaces:number;tick:string|null;trimTrailingZeros:boolean};
const rendered=new Map<string,string>();let precision=initialDisplayPrecision();
export function selectDisplayPrecision(value:DisplayPrecision){precision=displayPrecision(value);rendered.clear();}
const key=(value:string,kind:Kind,tick?:string,policy=precision)=>JSON.stringify([value,policy[kind],tick??null,kind!=='money']);
export function amountText(value:string,kind:Kind,tick?:string,policy=precision){return rendered.get(key(value,kind,tick,policy))??value;}
const money=new Set(['valueAtRisk','expectedShortfall','standardDeviation','mean','valueExcludingOverdue','completePv','overdueKnownBalance','overdueEstimated','amount','tradeCashAmount','tradePresentValue','presentValue','instrumentPresentValue','cashAmount','contribution','cashContribution','executionPnl','upPnl','downPnl','basePv','upPv','downPv','change','base','baseValue','loss','losses','scenarioValues','lower','upper']);
const prices=new Set(['price','fixedPrice','executionPrice','mark','markMinusPrice','value','annualRate',...priceMeasureChoices.map(c=>c[0])]);
const quantities=new Set(['resolvedQuantity','quantity','physicalQuantity','totalQuantity','weight']);
const factors=new Set(['discountFactor','factor','multiplier','delta','gamma']);
/** The part of an API read whose amounts a page shows, by the read's path; undefined for a read whose amounts no page shows. A workspace
 *  read's report and its desk summaries are both shown, its catalog is not; a curve's points are. */
// An exact amount a read states as {numerator,denominator}, as the rational text the backend formats; null for anything else.
export function rationalText(value:unknown):string|null{
 const v=value as {numerator?:unknown;denominator?:unknown};
 return v&&typeof v==='object'&&typeof v.numerator==='string'&&typeof v.denominator==='string'?(v.denominator==='1'?v.numerator:`${v.numerator}/${v.denominator}`):null;
}
// A risk report or retained job. The page that shows one prepares its amounts itself, after it knows the run is still the one on screen,
// so the Blotter's run is never held waiting on the display of a report it may no longer show.
export const riskReportRead=(path:string)=>/^portfolio\/risk(\/jobs\/[^/]+)?$/.test(path);
export function shownAmounts(path:string,payload:unknown):unknown{
 if(!/^(trading\/(blotter|workspace|position)|swaps\/(preview|editor)|futures\/(holdings|editor|amend-preview)|crude\/(preview|editor)|ppa\/(preview|inspect|break-even)|evaluate|history|power\/preview)$/.test(path))return undefined;
 const read=payload as {blotter?:unknown;summaries?:unknown;points?:unknown};
 return path==='trading/workspace'?{blotter:read.blotter,summaries:read.summaries}:path==='evaluate'||path==='history'?{points:read.points}:payload;
}
export async function prepareAmountDisplay(payload:unknown,format:(body:Request[])=>Promise<string[]>,initialTick?:string,initialPolicy=precision){
 const requests=new Map<string,Request>();
 function visit(value:unknown,tick?:string,inherited=initialPolicy){
  if(!value||typeof value!=='object')return;
  if(Array.isArray(value)){value.forEach(v=>visit(v,tick,inherited));return;}
  const row=value as Record<string,unknown>,priceTick=typeof row.priceTick==='string'?row.priceTick:tick,policy=row.amountFormat?displayPrecision(row.amountFormat):inherited;
  for(const [field,raw] of Object.entries(row)){
   const kind:Kind|undefined=money.has(field)?'money':prices.has(field)?'price':quantities.has(field)?'quantity':factors.has(field)?'factor':undefined;
   const text=typeof raw==='string'?raw:rationalText(raw);
   // A money column of exact amounts, such as a scenario matrix row, displays each one.
   if(kind==='money'&&Array.isArray(raw)&&raw.every(x=>typeof x==='string'&&exactNumber(x))){for(const x of raw as string[]){const k=key(x,kind,undefined,policy);if(!rendered.has(k))requests.set(k,{value:x,decimalPlaces:policy.money,tick:null,trimTrailingZeros:false});}continue;}
   if(kind&&text!==null&&exactNumber(text)){
    const selectedTick=kind==='price'?(['mark','markMinusPrice'].includes(field)&&typeof row.markTick==='string'?row.markTick:priceTick):undefined,selectedPolicy=field==='fixedPrice'&&row.fixedAmountFormat?displayPrecision(row.fixedAmountFormat):policy,k=key(text,kind,selectedTick,selectedPolicy);
    if(!rendered.has(k))requests.set(k,{value:text,decimalPlaces:selectedPolicy[kind],tick:selectedTick??null,trimTrailingZeros:kind!=='money'});
   }else visit(raw,priceTick,policy);
  }
 }
 visit(payload,initialTick);const rows=[...requests];
 for(let i=0;i<rows.length;i+=128){const batch=rows.slice(i,i+128),values=await format(batch.map(([,r])=>r));if(values.length!==batch.length)throw Error('Amount display response is incomplete.');batch.forEach(([k],j)=>rendered.set(k,values[j]));}
}
