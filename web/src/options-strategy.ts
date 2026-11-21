import {amountText as managedAmountText} from './amount-display.js';
import {measureReason,significantText,type Cell} from './options-chain.js';
import {type Figure} from './options-positions.js';
import {decimalText,type FuturesReadRequest,type Measure} from './vol-surface-view.js';

// A strategy of option legs as the Options page shows it. Every premium, payoff, breakeven, curve and sensitivity is the
// backend's: this module keeps the legs a person is assembling, sends back the selection the backend offered, and formats
// what an analysis states. It computes none of it.

export type Side='buy'|'sell';
/** A leg as a person assembles it: lots and entry price are text as typed until an analysis accepts them. */
export type StrategyLeg={series:string;right:'call'|'put';strike:string;side:Side;lots:string;entryPrice:string};
export type TemplateLeg={series:string;right:'call'|'put';strike:string;side:Side;lots:number;entryPrice:string|null};
export type StrategyTemplate={id:string;name:string;legs:TemplateLeg[]|null;refusal:{code:string;message:string}|null};
export type PriceRange={lower:string;upper:string;points:number};
/** What an analysis may select, as the backend offers it for sending back. */
export type StrategyOffer={prices:PriceRange|null;pointCounts:number[];instants:string[];instantsComplete:boolean;selectedInstants:string[];volatilityShiftPoints:string;
 valuation:Record<string,unknown>;sensitivities:Record<string,unknown>;expiryRule:string};
export type StrategyLimits={legs:number;points:number;curves:number;lots:number;volatilityShiftPoints:number};
export type StrategySetup={product:string;model:string;expiryAt:string;state:'fitted'|'notFitted';limits:StrategyLimits;underlyingSeries?:string;futures?:string|null;atTheMoneyStrike?:string|null;
 templateRule?:string;templates?:StrategyTemplate[];clock?:{source:string;zone:string}|null;offered?:StrategyOffer};

export type AnalysedLeg={series:string;right:'call'|'put';strike:string;side:Side;lots:string;entryPrice:string;multiplier:string;premium:string;state:'solved'|'at_intrinsic'|'unavailable';
 reason:string|null;settlement:string|null;impliedVolatility:string|null;value:string|null;delta:Measure};
export type Extreme={amount:string|null;bounded:boolean};
export type Breakeven={lower:string;upper:string|null};
export type CurvePoint={price:string;profitAndLoss:string};
export type StrategyAnalysis={product:string;expiryAt:string;valuationAt:string;underlyingSeries:string;currency:string;priceUnit:string;quantityUnit:string|null;futures:string|null;
 request:{prices:PriceRange;instants:string[];volatilityShiftPoints:string};legs:AnalysedLeg[];
 expiry:{rule:string;netPremium:{amount:string;direction:'debit'|'credit'|'none'};maximumProfit:Extreme;maximumLoss:Extreme;breakevens:Breakeven[];curve:CurvePoint[]};
 dated:{state:'valued'|'refused';reason:string|null;message:string|null;curves:{at:string;points:(CurvePoint&{value:string})[]}[]};
 strategy:{value:Figure;delta:Figure;gamma:Figure;vega:Figure;theta:Figure;dayTo:string|null};
 policies:{sensitivities:{policy:string};expiryRule:string};limits:StrategyLimits;work:{calibrationTreePrices:number;valuationTreePrices:number;treePrices:number;curveSets:number;elapsedMs:number}};

/** What a person has chosen for one analysis: the legs and the scenario controls, as text where they type it. */
export type StrategyChoice={legs:StrategyLeg[];lower:string;upper:string;points:number;instants:string[];volatilityShiftPoints:string};

const decimal=/^\d+(\.\d{1,18})?$/,wholeLots=/^[1-9]\d*$/;
const trimmed=(value:string)=>value.includes('.')?value.replace(/0+$/,'').replace(/\.$/,''):value;
const reasons:Record<string,string>={
 strategy_leg_uncalibrated:'A leg did not calibrate at this close',
 option_settlement_missing:'This series has no settlement at this close',
 option_underlying_missing:'The underlying future has no settlement at this close',
 option_expired:'This series is at or past its expiry',
 scenario_volatility:'The shifted volatility is outside what the option model prices',
 valuation_time_coverage:'A curve’s instant reaches the legs’ expiry or instruction cutoff'};
/** Why a strategy figure or its dated curves are missing, in words where the page knows the code. */
export const strategyReason=(code:string)=>Object.hasOwn(reasons,code)?reasons[code]:measureReason(code);

/** A template's legs as a person may then edit them: its lots and the settlement the backend offers as each entry price. */
export const templateLegs=(template:StrategyTemplate):StrategyLeg[]=>{
 if(!template.legs)throw Error(template.refusal?.message??'This template has no legs at this expiry.');
 return template.legs.map(leg=>({series:leg.series,right:leg.right,strike:leg.strike,side:leg.side,lots:String(leg.lots),entryPrice:leg.entryPrice===null?'':trimmed(leg.entryPrice)}));
};

/** The choice a setup offers before a person changes it: no legs, the offered range and curves, and no volatility shift. */
export const offeredChoice=(offer:StrategyOffer):StrategyChoice=>({legs:[],lower:offer.prices?trimmed(offer.prices.lower):'',upper:offer.prices?trimmed(offer.prices.upper):'',
 points:offer.prices?.points??offer.pointCounts[0],instants:[...offer.selectedInstants],volatilityShiftPoints:offer.volatilityShiftPoints});

/** What stops a choice being sent, said for the person to fix; nothing is corrected for them. The backend decides the rest. */
export function choiceProblem(choice:StrategyChoice,limits:StrategyLimits):string|null{
 if(!choice.legs.length)return 'Add a leg from the chain or choose a template.';
 if(choice.legs.length>limits.legs)return `A strategy takes at most ${limits.legs} legs.`;
 if(choice.legs.some(leg=>!wholeLots.test(leg.lots)||Number(leg.lots)>limits.lots))return `Each leg needs a whole number of lots from 1 to ${limits.lots}.`;
 if(choice.legs.some(leg=>!decimal.test(leg.entryPrice)))return 'Each leg needs an entry price, as a plain decimal that is not negative.';
 if(!decimal.test(choice.lower)||!decimal.test(choice.upper))return 'State the lower and upper futures price as plain decimals.';
 if(choice.instants.length>limits.curves)return `Choose at most ${limits.curves} dated curves.`;
 if(!/^-?\d+(\.\d{1,2})?$/.test(choice.volatilityShiftPoints))return 'State the volatility shift in points, such as 5 or -2.5.';
 return null;
}

/** The analysis request: the person's choice with the policies the setup offered, sent back as they were given. */
export function analysisRequest(context:FuturesReadRequest,setup:StrategySetup,choice:StrategyChoice,definitionRevisionId:string|undefined){
 if(!setup.offered)throw Error('This close offers no strategy analysis.');
 return {context,product:setup.product,expiryAt:setup.expiryAt,definitionRevisionId,
  legs:choice.legs.map(leg=>({series:leg.series,side:leg.side,lots:Number(leg.lots),entryPrice:leg.entryPrice})),
  prices:{lower:choice.lower,upper:choice.upper,points:choice.points},instants:[...choice.instants].sort((a,b)=>Date.parse(a)-Date.parse(b)),
  volatilityShiftPoints:choice.volatilityShiftPoints,valuation:setup.offered.valuation,sensitivities:setup.offered.sensitivities,expiryRule:setup.offered.expiryRule};
}

/** The analysed figures of each leg by series: implied volatility and delta as a chain shows them, or a dash that says why. */
export function legFigures(analysis:StrategyAnalysis):Map<string,{impliedVolatility:Cell;delta:Cell}>{
 return new Map(analysis.legs.map(leg=>{
  const missing=leg.reason??leg.delta.missing??'incomplete';
  return [leg.series,{impliedVolatility:leg.impliedVolatility!==null?{text:decimalText(leg.impliedVolatility,{shift:2,places:1})+'%',missing:null}:{text:'—',missing},
   delta:leg.delta.value!=null?{text:decimalText(leg.delta.value,{places:2}),missing:null}:{text:'—',missing}}];
 }));
}

const money=(amount:string,signed=false)=>(signed&&!amount.startsWith('-')&&/[1-9]/.test(managedAmountText(amount,'money'))?'+':'')+managedAmountText(amount,'money');
// A price to six significant figures, without the zeros that would pad it.
const price=(value:string)=>trimmed(significantText(value,6));
/** A breakeven as text: one price, a range of prices, or every price from one upward. */
export const breakevenText=(breakeven:Breakeven)=>breakeven.upper===null?`${price(breakeven.lower)} and above`:
 breakeven.upper===breakeven.lower?price(breakeven.lower):`${price(breakeven.lower)} – ${price(breakeven.upper)}`;

export type AnalysisLine={term:string;value:string;tone:'gain'|'loss'|'none';note?:string};
/** The analysis card: the premium, the extremes of the payoff at expiry and its breakevens, each as the backend states it. */
export function analysisLines(analysis:StrategyAnalysis):AnalysisLine[]{
 const {netPremium,maximumProfit,maximumLoss,breakevens}=analysis.expiry,currency=analysis.currency;
 const extreme=(value:Extreme,term:string):AnalysisLine=>value.amount===null?{term,value:'Unbounded',tone:'none',note:'It has no bound over futures prices from zero upward'}:
  {term,value:`${money(value.amount,true)} ${currency}`,tone:value.amount.startsWith('-')?'loss':/[1-9]/.test(value.amount)?'gain':'none'};
 return [
  {term:'Net premium',value:netPremium.direction==='none'?`${managedAmountText('0','money')} ${currency}`:`${netPremium.direction==='debit'?'Debit':'Credit'} ${money(netPremium.amount)} ${currency}`,tone:'none',
   note:netPremium.direction==='debit'?'Paid to enter':netPremium.direction==='credit'?'Received to enter':'Nothing paid or received'},
  extreme(maximumProfit,'Maximum profit'),extreme(maximumLoss,'Maximum loss'),
  {term:breakevens.length===1?'Breakeven':'Breakevens',value:breakevens.length?breakevens.map(breakevenText).join(' · '):'None',tone:'none',note:'Futures price at expiry'}];
}

export type Tile={term:string;value:string;unit:string;missing:string|null};
/** The strategy's value and sensitivities with the unit each is stated in. A sum with a missing member is a dash that says why. */
export function sensitivityTiles(analysis:StrategyAnalysis):Tile[]{
 const {strategy,currency}=analysis,quantity=analysis.quantityUnit??'underlying units';
 const tile=(term:string,figure:Figure,unit:string,places:number):Tile=>figure.amount!==null?{term,value:managedAmountText(figure.amount,places===2?'money':'quantity'),unit,missing:null}:
  {term,value:'—',unit,missing:strategyReason(figure.missing??'incomplete')};
 return [tile('Value',strategy.value,currency,2),tile('Delta',strategy.delta,quantity,0),tile('Gamma',strategy.gamma,`${quantity} per (${analysis.priceUnit})`,0),
  tile('Vega',strategy.vega,`${currency} per volatility point`,2),tile('Theta',strategy.theta,`${currency} for one day`,2)];
}

/** A drawn point: where it is plotted, and the price and profit and loss as text for a readout to state exactly. */
export type ChartPoint={x:number;y:number;price:string;profitAndLoss:string};
export type ChartCurve={id:string;name:string;kind:'expiry'|'dated';points:ChartPoint[]};
export type ChartMark={kind:'futures'|'breakeven';price:number;label:string};
export type StrategyChart={priceUnit:string;currency:string;curves:ChartCurve[];marks:ChartMark[]};
/**
 * The profit-and-loss chart: the expiry payoff through every point the backend states, with each breakeven inside the
 * range added at zero so the gain and loss fills meet the axis there, then one curve per dated instant. Numbers are for
 * plotting only; a readout states the text beside them.
 */
export function strategyChart(analysis:StrategyAnalysis,instantLabel:(at:string)=>string):StrategyChart{
 const point=(p:CurvePoint):ChartPoint=>({x:Number(p.price),y:Number(p.profitAndLoss),price:trimmed(p.price),profitAndLoss:p.profitAndLoss});
 const stated=analysis.expiry.curve.map(point);
 const [low,high]=[stated[0].x,stated[stated.length-1].x];
 const within=(value:string)=>Number(value)>low&&Number(value)<high;
 const crossings=analysis.expiry.breakevens.flatMap(b=>b.upper!==null&&b.upper!==b.lower?[b.lower,b.upper]:[b.lower]).filter(within)
  .map(value=>({x:Number(value),y:0,price:price(value),profitAndLoss:'0'}));
 const expiry=[...stated,...crossings.filter(({x})=>!stated.some(at=>at.x===x))].sort((a,b)=>a.x-b.x);
 const marks:ChartMark[]=analysis.expiry.breakevens.flatMap(b=>b.upper!==null&&b.upper!==b.lower?[b.lower,b.upper]:[b.lower]).filter(value=>Number(value)>=low&&Number(value)<=high)
  .map(value=>({kind:'breakeven' as const,price:Number(value),label:`Breakeven ${price(value)}`}));
 if(analysis.futures!==null&&Number(analysis.futures)>=low&&Number(analysis.futures)<=high)marks.unshift({kind:'futures',price:Number(analysis.futures),label:`Futures ${price(analysis.futures)}`});
 return {priceUnit:analysis.priceUnit,currency:analysis.currency,marks,curves:[{id:'expiry',name:'At expiry',kind:'expiry',points:expiry},
  ...analysis.dated.curves.map(curve=>({id:curve.at,name:instantLabel(curve.at),kind:'dated' as const,points:curve.points.map(point)}))]};
}
