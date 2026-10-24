import {amountText,prepareAmountDisplay} from './amount-display.js';
import {renderRegularTable} from './regular-table.js';
import {curveName,deliveryLabel,publicText} from './market-view.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type DraftPrice={context:{contextId:string};canRunDealRisk:boolean;unbooked:boolean;measure:string;currency:string;executionPnl?:string;valuation?:{presentValue:string};quantity?:{amount:string;specification:unknown};bumpPolicy:string;numericalPolicy:string;resolvedFixedPrice?:string;parRate?:{price:{value:string};decimalPlaces:number;policy:string}|null};
type Draft={context:Record<string,unknown>;[key:string]:unknown};
type Risk={measure:string;currency:string;factors:{factor:{curves:string[];deliveryFrom:string;deliveryTo:string};bump:string;basePv:string;upPnl:string;downPnl:string;delta:string;gamma:string;deltaUnit:string;gammaUnit:string}[]};

// Presentation only: prices, conversions and scenarios are authorized backend
// operations. Risk uses the exact context returned by Price, with no market roll.
export function draftValuation(host:HTMLElement,o:{product:'futures'|'swaps';api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;draft:()=>Promise<Draft>;key:()=>string;priced?:(result:DraftPrice)=>Promise<void>;denied:()=>void}){
 let generation=0,retained:unknown,canRisk=false,requested=false;
 const actions=document.createElement('div'),price=document.createElement('button'),risk=document.createElement('button'),summary=document.createElement('p'),note=document.createElement('p'),rows=document.createElement('div');
 actions.className='schedule-actions';price.type=risk.type='button';price.textContent='Price';risk.textContent='Risk';risk.className='secondary';summary.setAttribute('role','status');note.className='field-help';rows.className='table-wrap';actions.append(price,risk);host.replaceChildren(actions,summary,note,rows);
 price.id=o.product+'-draft-price';risk.id=o.product+'-draft-risk';summary.id=o.product+'-draft-value';rows.id=o.product+'-draft-exposure';
 function sync(){price.disabled=o.busy();risk.disabled=o.busy()||!retained||!canRisk;}
 function reset(clearRequest=true){if(clearRequest)requested=false;generation++;retained=undefined;canRisk=false;summary.textContent='';note.textContent='';rows.replaceChildren();sync();}
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
  await o.priced?.(result);if(started===generation&&key===o.key())sync();
 }
 price.onclick=()=>void o.act(()=>safe(priceCurrent));
 risk.onclick=()=>void o.act(()=>safe(async()=>{
  if(!retained)throw Error('Price the current instrument first.');const started=generation,key=o.key();rows.replaceChildren();
  const result=await o.api<Risk>(o.product+'/draft-risk',retained);if(started!==generation||key!==o.key())return;
  await prepareAmountDisplay(result,body=>o.api<string[]>('amounts/display',body));if(started!==generation||key!==o.key())return;
  const raw=result.factors.map(r=>[r.factor.curves.map(curveName).join(' / '),deliveryLabel(r.factor.deliveryFrom,r.factor.deliveryTo),r.delta,r.deltaUnit,r.gamma,r.gammaUnit,r.basePv,r.upPnl,r.downPnl]);
  const display=raw.map((values,i)=>[...values.slice(0,2),amountText(result.factors[i].delta,'factor'),values[3],amountText(result.factors[i].gamma,'factor'),values[5],amountText(result.factors[i].basePv,'money'),amountText(result.factors[i].upPnl,'money'),amountText(result.factors[i].downPnl,'money')]);
  renderRegularTable(rows,o.product+'-draft-risk',key,['Market factor','Contract','Exposure','Exposure unit','Gamma','Gamma unit',`Base (${result.currency})`,`Up change (${result.currency})`,`Down change (${result.currency})`],raw,[2,4,6,7,8],o.busy,{displayRows:display});
 }));
 sync();return{reset:()=>reset(),sync,async liveRefresh(){const priced=requested;reset(false);if(priced)await safe(priceCurrent);}};
}
