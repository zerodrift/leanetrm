import {projectionPolicyLabels,type ProjectionPolicyReceipt} from './projection-policy.js';
import {amountText,prepareAmountDisplay} from './amount-display.js';
import {clearChart,renderChart} from './chart-view.js';
import {curveChart,type CurveShape} from './desk-model.js';
import type {DraftMarket} from './curve-shapes.js';
import {renderRegularTable} from './regular-table.js';
import {curveName,deliveryLabel,publicText} from './market-view.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type DraftPrice={context:{contextId:string};canRunDealRisk:boolean;unbooked:boolean;measure:string;currency:string;executionPnl?:string;valuation?:{presentValue:string};quantity?:{amount:string;specification:unknown};bumpPolicy:string;numericalPolicy:string;resolvedFixedPrice?:string;parRate?:{price:{value:string};decimalPlaces:number;policy:string}|null};
type Draft={context:Record<string,unknown>;[key:string]:unknown};
type Risk={projectionPolicies:ProjectionPolicyReceipt[];measure:string;currency:string;factors:{factor:{curves:string[];deliveryFrom:string;deliveryTo:string};bump:string;basePv:string;upPnl:string;downPnl:string;delta:string;gamma:string;deltaUnit:string;gammaUnit:string}[]};

// Presentation only: prices, conversions and scenarios are authorized backend
// operations. Risk uses the exact context returned by Price, with no market roll.
// Price before booking (blotter-desk.md, Side panel): what the unbooked deal is worth at the close, with the curve of each market it is
// for and the delivery period it marks on it. `markets` names them from the draft, and `shape` gives each one's curve shape at the close,
// or why there is none: the page reads it, from the desk's own read when it can, so that a curve is never read for the draft needlessly.
export function draftValuation<D extends Draft>(host:HTMLElement,o:{product:'futures'|'swaps';api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;draft:()=>Promise<D>;key:()=>string;priced?:(result:DraftPrice)=>Promise<void>;denied:()=>void;
 markets:(draft:D)=>DraftMarket[];shape:(market:DraftMarket)=>Promise<CurveShape|string>}){
 let generation=0,retained:unknown,canRisk=false,requested=false;
 const title=document.createElement('h3'),actions=document.createElement('div'),price=document.createElement('button'),risk=document.createElement('button'),summary=document.createElement('p'),note=document.createElement('p'),curves=document.createElement('div'),rows=document.createElement('div'),policies=document.createElement('div');
 title.className='draft-title';title.textContent='Price before booking';curves.className='draft-curves';
 actions.className='schedule-actions';price.type=risk.type='button';price.textContent='Price';risk.textContent='Risk';risk.className='secondary';summary.setAttribute('role','status');note.className='field-help';rows.className='table-wrap';actions.append(price,risk);host.replaceChildren(title,actions,summary,note,curves,policies,rows);policies.id=o.product+'-draft-projection-policies';
 price.id=o.product+'-draft-price';risk.id=o.product+'-draft-risk';summary.id=o.product+'-draft-value';rows.id=o.product+'-draft-exposure';curves.id=o.product+'-draft-curves';
 function sync(){price.disabled=o.busy();risk.disabled=o.busy()||!retained||!canRisk;}
 function takeDownCurves(){for(const plot of curves.querySelectorAll<HTMLElement>('.chart'))clearChart(plot);curves.replaceChildren();}
 function reset(clearRequest=true){if(clearRequest)requested=false;generation++;retained=undefined;canRisk=false;summary.textContent='';note.textContent='';policies.replaceChildren();takeDownCurves();rows.replaceChildren();sync();}
 async function safe(work:()=>Promise<void>){try{await work();}catch(error){reset(false);if((error as {code?:string}).code==='access_denied')o.denied();throw error;}}
 async function priceCurrent(){
  requested=true;reset(false);const started=generation,key=o.key(),draft=await o.draft();if(started!==generation||key!==o.key())return;
  const result=await o.api<DraftPrice>(o.product+'/price',draft);if(started!==generation||key!==o.key())return;
  await prepareAmountDisplay(result,body=>o.api<string[]>('amounts/display',body));if(started!==generation||key!==o.key())return;
  const pv=result.valuation?.presentValue??result.executionPnl;if(pv===undefined)throw Error('The draft valuation is incomplete.');
  summary.textContent=`${result.measure==='present_value'?'PV':'Execution P&L'}: ${amountText(pv,'money')} ${result.currency}`;
  note.textContent=result.measure==='present_value'?'Unbooked estimate. Risk uses ±one tick per price factor; interest rates stay fixed.':'Unbooked execution P&L and open-price exposure. Margin and settlement PV are not processed.';
  const resolved=result.resolvedFixedPrice!==undefined&&draft.fixed?{...draft,fixed:{...(draft.fixed as Record<string,unknown>),price:result.resolvedFixedPrice},parRatePolicy:null}:draft;
  retained={draft:{...resolved,context:{...draft.context,knowledgeMode:'retained',contextId:result.context.contextId}},bumpPolicy:result.bumpPolicy,numericalPolicy:result.numericalPolicy};canRisk=result.canRunDealRisk;
  await o.priced?.(result);if(started!==generation||key!==o.key())return;
  await drawCurves(draft,started,key);if(started===generation&&key===o.key())sync();
 }
 // The curve of each market the draft is for, with the draft's delivery period marked on it. A curve that cannot be drawn says why beside the price, and never fails it.
 async function drawCurves(draft:D,started:number,key:string){
  const current=()=>started===generation&&key===o.key();
  let markets:DraftMarket[];
  try{markets=o.markets(draft);}catch(error){curves.append(line(whyNot(error)));return;}
  for(const market of markets){
   try{
    const shape=await o.shape(market);if(!current())return;
    const view=typeof shape==='string'?{state:'unavailable' as const,reason:shape}:curveChart(shape,{market:curveName(market.curve),delivery:deliveryLabel(market.deliveryFrom,market.deliveryTo),deliveryFrom:market.deliveryFrom,deliveryTo:market.deliveryTo});
    if(view.state==='unavailable'){curves.append(line(view.reason));continue;}
    const plot=document.createElement('div');plot.className='chart draft-chart';curves.append(plot);
    try{renderChart(plot,view.spec);}catch(error){plot.remove();throw error;}
    if(!view.marked)curves.append(line('The delivery period is not on the curve at this close.'));
   }catch(error){if(!current())return;curves.append(line(whyNot(error)));}
  }
 }
 const whyNot=(error:unknown)=>`Curve could not be drawn · ${error instanceof Error?error.message:'it failed for a reason not given'}`;
 function line(text:string){const p=document.createElement('p');p.className='field-help';p.textContent=publicText(text);return p;}
 price.onclick=()=>void o.act(()=>safe(priceCurrent));
 risk.onclick=()=>void o.act(()=>safe(async()=>{
  if(!retained)throw Error('Price the current instrument first.');const started=generation,key=o.key();rows.replaceChildren();policies.replaceChildren();
  const result=await o.api<Risk>(o.product+'/draft-risk',retained);if(started!==generation||key!==o.key())return;
  await prepareAmountDisplay(result,body=>o.api<string[]>('amounts/display',body));if(started!==generation||key!==o.key())return;
  policies.replaceChildren(...projectionPolicyLabels(result.projectionPolicies).map(line));
  const raw=result.factors.map(r=>[r.factor.curves.map(curveName).join(' / '),deliveryLabel(r.factor.deliveryFrom,r.factor.deliveryTo),r.delta,r.deltaUnit,r.gamma,r.gammaUnit,r.basePv,r.upPnl,r.downPnl]);
  const display=raw.map((values,i)=>[...values.slice(0,2),amountText(result.factors[i].delta,'factor'),values[3],amountText(result.factors[i].gamma,'factor'),values[5],amountText(result.factors[i].basePv,'money'),amountText(result.factors[i].upPnl,'money'),amountText(result.factors[i].downPnl,'money')]);
  renderRegularTable(rows,o.product+'-draft-risk',key,['Market factor','Contract','Exposure','Exposure unit','Gamma','Gamma unit',`Base (${result.currency})`,`Up change (${result.currency})`,`Down change (${result.currency})`],raw,[2,4,6,7,8],o.busy,{displayRows:display});
 }));
 sync();return{reset:()=>reset(),sync,async liveRefresh(){const priced=requested;reset(false);if(priced)await safe(priceCurrent);}};
}
