import {mountPriceSettlement,type PriceSettlement,type SettlementIndex} from './price-settlement.js';
import {explainUnavailableValue,markUnavailableValue,renderChangeReview,unavailableValueLabel,type ChangeReview} from './saved-trade-terms.js';
import {allocations,deliveryProducts,quantityBases,renderScheduledReview,retainedIntervals,setupScheduledSwapEditor,type QuantityReview,type Scheduled} from './scheduled-swap-editor.js';
import {lifecycleMeasures,setupSwapSettlement} from './swap-settlement.js';
import {renderSwapTradePayments,sideDirection} from './swap-trade-review.js';
import {mountIntervalDataWidget} from './interval-data-widget.js';
import {sideScheduleData} from './side-schedule.js';
import {linkedValue,quantityChoice,filterSwapIndices,deliveryChoices,marketReferenceChoices} from './swap-selection.js';
import {createAvailableDatePicker} from './available-date-picker.js';
import {draftValuation} from './draft-valuation.js';
import type {DraftMarket} from './curve-shapes.js';
import {bookingAllowed} from './new-trade.js';
import {mountSwapSideEditor,mountFirstFloatingEditor,arrangeSwapSections} from './swap-side-editor.js';
import {dealFormatEditor,dealFormats,type DealFormats} from './deal-format-editor.js';
import {tradeAnnotations,bookingRows,historyBooking,type PartyCatalog,type Trader,type Annotation} from './trade-annotations.js';
import {createScheduleDatePicker} from './schedule-date-picker.js';
import type {Context} from './date-editor.js';
import {PaymentDateRule,PaymentDayBasis,DateAdjustment,Discounting,type Choice,type PaymentInput,type PaymentOptions} from './swap-contract.js';
import {minus,priceUnit,type CurveShape,type Holding,type HoldingFormats} from './desk-model.js';
import type {AmendState,DetailRead,DetailView,TermSection} from './position-panel.js';
import {trackEdits} from './edit-tracking.js';
import {projectionReference,type CustomProjection,type ProjectionChoice} from './swap-projections.js';
import {publicText,displayInstant,deliveryLabel,curveName,friendly,timeLabels} from './market-view.js';
import {renderRegularTable} from './regular-table.js';
import type {RequestCommand} from './pending-command.js';
import {renderSwapObservationInspection,setupSwapObservationControls} from './swap-observations-view.js';
import type {SamplingChoice,ObservationInspection,ObservationOptions,ObservationSchedule} from './swap-observations.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
export type Index={settlementIndices:SettlementIndex[];primitiveEntrySupported:boolean;definitionRevisionId:string;curve:string;index:string;commodity:string;currencyCode:string;currency:string;unit:string;quoteMultiplier:string;quantityCode:string;powerQuantitySupported:boolean;quantityChoices:{specification:{code:string};unit:string}[];defaultDiscountCurve:string|null;fixingConvention:'published_index_observations_v1'|null;fixingUnavailableReason:string|null;customProjections:ProjectionChoice[];averaging:string[];samplings:SamplingChoice[];contracts:{deliveryFrom:string;deliveryTo:string}[]};
export type Catalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string}[];indices:Index[];referenceChoices?:ReferenceChoices[];commodities:{code:string;name:string}[];paymentCalendars:{code:string;definitionRevisionId:string}[];paymentOptions:PaymentOptions;entryTemplate:{version:string;firstCoefficient:string;spread:string;description:string;payment:PaymentInput};parRateOptions:{implementation:string;initialDecimalPlaces:number;tieRule:string};asOf:string;context:Context;canBook?:boolean;bookingUnavailableReason?:string|null};
export type ReferenceChoices={definitionRevisionId:string;commodities:{code:string;name:string}[];paymentCalendars:{code:string;reference:import('./swap-observations.js').ReferenceKey;from:string;to:string}[];powerQuantities:{specification:import('./swap-observations.js').ReferenceKey;unit:string;commodity:string}[];clocks:{reference:import('./swap-observations.js').ReferenceKey;label:string}[]};
type FloatingTerms={settlement:PriceSettlement;curve:string;index:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null;firstFixingAt?:string|null;customProjection?:CustomProjection|null;observationSchedule?:ObservationSchedule;resetFrequency:string;averagingMethod:string;spread:string};
type Terms={scheduled?:Scheduled|null;swapType:string;firstCoefficient:string|null;firstFloating:FloatingTerms|null;commodity:string;definitionRevisionId:string;bookId:string;executedAt:string;quantity:string;fixedCoefficient:string;discounting:boolean;fixed:{price:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null}|null;floating:FloatingTerms};
type State=Annotation&{tradeId:string;tradeReference:string;instrumentId:string;instrumentReference:string;revision:number;quantity:string;kind:string;reason:string;effectiveAt:string;recordedAt:string;terms:Terms};
export type Valuation={presentValue:string;instrumentPresentValue:string;quantity:string;sides:{slot:number;kind:string;coefficient:string;presentValue:string;contribution:string;payments:{deliveryFrom:string;deliveryTo:string;paymentDate:string;resolvedQuantity:string;tradeCashAmount:string;tradePresentValue:string;cashAmount:string;discountFactor:string;presentValue:string;discountMappingId:string|null;discountCurve:unknown;discountPolicy:string;pillars:unknown;observationSchedule:ObservationInspection|null;resets:{currency:{code:string};quantity:{code:string};multiplier:string;spread:string|null;lookup:unknown;lineage:unknown;from:string;to:string;price:string;weight:string;cashContribution:string;fixingState:string;fixing?:unknown}[]}[]}[]};
type Detail={state:State;history:State[];catalog:Catalog&{scheduledOptions?:{powerQuantities:{specification:{code:string};unit:string}[];clocks:{reference:{code:string};label:string}[]}};
 /** The pure valuation of the revision; none while its settlement lifecycle values it, which its own measures state. */
 valuation:Valuation|null;settlementValuation?:Record<string,unknown>;
 /** Each membership's side kind and direction, which the read states when it has no pure valuation to state them in. */
 memberships?:{slot:number;kind:string;coefficient:string}[];quantityInputs?:QuantityReview[];canAmend:boolean;amendUnavailableReason:string|null;asOf:string;context:Context};
export type SwapDetail=Detail;
const convention='independent_side_financial_swap_v2';
/** The editor's read of one swap revision (null: the latest), in the market context the page reads in. */
export const readSwapDetail=(api:Api,context:Record<string,unknown>,tradeId:string,revision:number|null)=>api<Detail>('swaps/editor',{context:{...context,convention},tradeId,revision});
/** A side's title, as the editor and the position panel name it: which side, and whether it is paid or received. A coefficient of another sign is shown as it is. */
export const sideTitle=(side:{slot:number;kind:string;coefficient:string},floatingPair:boolean)=>
 `${side.kind==='fixed'?'Fixed':floatingPair?(side.slot===0?'First floating':'Second floating'):'Floating'} side · ${sideDirection(side.coefficient)}`;
const swapTypeLabels:Readonly<Record<string,string>>={financial_fixed_floating:'Fixed for floating',financial_floating_floating:'Floating for floating'};
const discountingLabels:Readonly<Record<Discounting,string>>={[Discounting.CurrencyDefault]:'Currency default curve',[Discounting.None]:'Explicitly undiscounted'};
/**
 * The revisions of a swap as the History tab lists them: each with the instrument it was of, when it took effect and was recorded, and
 * its facts, its quantity written as Terms writes it and in the unit of that revision's own terms.
 */
export function swapHistory(detail:Detail,zone:string,formats:HoldingFormats):TermSection[]{
 const labels=timeLabels(detail.history.flatMap(s=>[s.effectiveAt,s.recordedAt]),zone);
 const quantity=(s:State)=>{
  if(s.terms.scheduled)return byPeriod;
  const index=detail.catalog.indices.find(i=>i.definitionRevisionId===s.terms.definitionRevisionId&&i.curve===s.terms.floating.curve&&i.index===s.terms.floating.index);
  return `${minus(formats.quantity(s.quantity))} ${index?.unit??'· unit unavailable: the index of this revision is not in the catalog'}`;
 };
 return detail.history.map((s,i)=>({title:`Revision ${s.revision} · ${friendly(s.kind)}`,rows:[['Effective',labels[2*i]],['Recorded',labels[2*i+1]],['Instrument',s.instrumentReference],['Quantity',quantity(s)],...historyBooking(s),['Reason',s.reason]]}));
}
const choiceLabel=(choices:Choice[],value:string)=>choices.find(c=>c.value===value)?.label??friendly(value);
// A payment schedule's terms, in the words of the catalog's own payment choices.
function paymentRows(payment:PaymentInput,options:PaymentOptions):(readonly [string,string])[]{
 const rule=payment.rule===PaymentDateRule.DaysAfterPeriodEnd&&payment.days!==null?` · ${payment.days} days`:payment.rule===PaymentDateRule.DayOfFollowingMonth&&payment.dayOfMonth!==null?` · day ${payment.dayOfMonth}`:'';
 return [['Payment frequency',choiceLabel(options.frequencies,payment.frequency)],['Payment rule',choiceLabel(options.rules,payment.rule)+rule],
  ...(payment.dayBasis?[['Day basis',choiceLabel(options.dayBases,payment.dayBasis)] as const]:[]),...(payment.shortMonth?[['Short months',choiceLabel(options.shortMonths,payment.shortMonth)] as const]:[]),
  ['Adjustment',choiceLabel(options.adjustments,payment.adjustment)],...(payment.calendar?[['Calendar',friendly(payment.calendar)] as const]:[])];
}
/** The exact index definition a retained swap's floating side names, which the catalog must still hold. */
export function retainedIndex(catalog:Catalog,terms:Terms,side:{curve:string;index:string}){
 const found=catalog.indices.find(i=>i.definitionRevisionId===terms.definitionRevisionId&&i.curve===side.curve&&i.index===side.index);
 if(!found)throw Error('The retained exact index definition is unavailable.');
 return found;
}
const byPeriod='By period';
const settlementLabel=(selection:PriceSettlement)=>selection.mode==='curve_target'?'Curve target index':`Different index · ${friendly(selection.index!)} · forward curve projection, basis unmodelled`;
const choice=(choices:readonly {value:string;label:string}[],value:string|null)=>choices.find(c=>c.value===value)?.label??friendly(value??'');
const priceApplications:Readonly<Record<string,string>>={period_average_times_quantity_v1:'Period-average price × period quantity'};
// A revision's trade section: its references, revision, book and booking facts.
function tradeSection(detail:Detail,zone:string):TermSection{
 const s=detail.state,book=detail.catalog.books.find(b=>b.id===s.terms.bookId);
 return {title:'Trade',rows:[['Trade',`${s.tradeReference} · ${s.instrumentReference}`],['Revision',`${s.revision} · ${friendly(s.kind)}`],['Book',book?`${book.portfolio} · ${book.name}`:'Unavailable book'],...bookingRows(s,zone)]};
}
/**
 * A swap's sides with their kind and direction, as the backend states them: in its valuation, or, for a revision valued by its
 * settlement lifecycle, which has no pure valuation, in the read's memberships. None is worked out from the terms here.
 */
function sidesOf(detail:Detail):{slot:number;kind:string;coefficient:string}[]{
 const sides=detail.valuation?.sides??detail.memberships;
 if(!sides)throw Error('The swap read did not state its sides.');
 return sides;
}
/**
 * The terms of a scheduled swap revision (D214): its structure, with no single quantity, and each side with its quantity basis, unit
 * and every period's quantity and price or index. A term every period of a side shares is stated once, and one they do not share is
 * said to be by period; each period's resolved quantity, cash and dates are in the review drawn beneath.
 */
function scheduledTerms(detail:Detail,zone:string,formats:HoldingFormats):TermSection[]{
 const t=detail.state.terms,scheduled=t.scheduled!,catalog=detail.catalog;
 const indexOf=(side:{curve:string;index:string})=>retainedIndex(catalog,t,side),settlement=indexOf(scheduled.second.periods[0].floating!);
 const references=marketReferenceChoices(catalog.referenceChoices,t.definitionRevisionId)??catalog.scheduledOptions;
 const unitOf=(reference:{code:string})=>[...catalog.indices.filter(i=>i.definitionRevisionId===t.definitionRevisionId).flatMap(i=>i.quantityChoices),...references?.powerQuantities??[]].find(q=>q.specification.code===reference.code)?.unit??'unit unavailable';
 const clockOf=(reference:{code:string})=>references?.clocks.find(c=>c.reference.code===reference.code)?.label??reference.code;
 const shared=<T,>(values:readonly T[])=>values.every(v=>JSON.stringify(v)===JSON.stringify(values[0]))?values[0]:undefined;
 const [firstSide,secondSide]=sidesOf(detail),fixedFirst=!!scheduled.first.periods[0]?.fixed;
 const section=(side:Scheduled['first'],title:string):TermSection=>{
  const unit=unitOf(side.quantity),rows:(readonly [string,string])[]=[['Quantity basis',side.basis==='factor_of_first_side_v1'?'Derived from first-side quantity × factor':choice(quantityBases,side.basis)],['Quantity unit',unit]];
  if(side.basis==='contract_total')rows.push(['Contract total',`${minus(formats.quantity(side.amount??''))} ${unit} · ${choice(allocations,side.allocation)}`]);
  if(side.power)rows.push(['Delivery hours',`${choice(deliveryProducts,side.power.product)}${side.power.product==='weekday_block'?` · ${side.power.startHour}:00–${side.power.endHour}:00${side.power.saturday?' · Saturdays':''}`:''} · clock ${clockOf(side.power.clock)}`]);
  const periods=side.periods.map(p=>({...p,terms:(p.fixed??p.floating)!}));
  rows.push(['Periods',`${periods.length} monthly · ${deliveryLabel(periods[0].terms.deliveryFrom,periods[periods.length-1].terms.deliveryTo)}`]);
  for(const p of periods){
   const amount=side.basis==='factor_of_first_side_v1'?'Derived quantity':side.basis==='contract_total'?null:`${minus(formats.quantity(p.amount??''))} ${unit}${side.basis==='calendar_day_rate'?' per day':''}`;
   const price=p.fixed?`fixed ${p.fixed.price===null?'None':minus(formats.price(p.fixed.price))} ${priceUnit(settlement)}`:`${friendly(p.floating!.index)} · ${curveName(p.floating!.curve)}`;
   rows.push([deliveryLabel(p.terms.deliveryFrom,p.terms.deliveryTo),[amount,price].filter(Boolean).join(' · ')]);
  }
  const floating=periods.flatMap(p=>p.floating?[p.floating]:[]);
  if(floating.length){
   const selection=shared(floating.map(f=>f.settlement));rows.push(['Price settlement',selection?settlementLabel(selection):byPeriod]);
   const reset=shared(floating.map(f=>f.resetFrequency)),averaging=shared(floating.map(f=>f.averagingMethod)),spread=shared(floating.map(f=>f.spread));
   rows.push(['Reset frequency',reset===undefined?byPeriod:friendly(reset)],['Averaging',averaging===undefined?byPeriod:friendly(averaging)],
    ['Spread',spread===undefined?byPeriod:`${spread} ${priceUnit(indexOf(floating[0]))}`]);
  }
  const payment=shared(periods.map(p=>p.terms.paymentSchedule));
  rows.push(...(payment===undefined?[['Payment rules',byPeriod] as const]:paymentRows(payment,catalog.paymentOptions)));
  return {title,rows};
 };
 return [tradeSection(detail,zone),
  {title:'Structure',rows:[['Type',`Scheduled ${fixedFirst?'fixed':'floating'} for floating`],['Commodity',catalog.commodities.find(c=>c.code===t.commodity)?.name??friendly(t.commodity)],['Quantity',byPeriod],
   ...(scheduled.factor?[['Contractual factor',`${scheduled.factor.value} ${unitOf(scheduled.factor.numeratorQuantity)} / ${unitOf(scheduled.factor.denominatorQuantity)}`] as const]:[]),
   ['Price application',priceApplications[scheduled.first.priceApplication]??friendly(scheduled.first.priceApplication)],['Discounting',discountingLabels[t.discounting?Discounting.CurrencyDefault:Discounting.None]]]},
  section(scheduled.first,`First side · ${sideDirection(firstSide.coefficient)}`),section(scheduled.second,`Second side · ${sideDirection(secondSide.coefficient)}`)];
}
// What a settlement lifecycle states of a swap valued by it (D215), each measure in the swap's currency.
function lifecycleSection(detail:Detail,formats:HoldingFormats):TermSection{
 const t=detail.state.terms,lifecycle=detail.settlementValuation!,floating=t.scheduled?t.scheduled.second.periods[0].floating!:t.floating;
 const currency=detail.catalog.indices.find(i=>i.currencyCode===floating.currency)?.currency??floating.currency;
 return {title:'Settlement lifecycle',rows:lifecycleMeasures.map(([name,field])=>{
  const value=lifecycle[field];if(typeof value!=='string')throw Error('The settlement lifecycle did not state its measures.');
  return [name,`${minus(formats.money(value))} ${currency}`] as const;})};
}
/**
 * The terms of a swap revision as a read view lists them: its booking facts, its structure, and each of its sides with its own terms,
 * those of a scheduled swap by period; and for one valued by its settlement lifecycle, that lifecycle's measures.
 */
export function swapTerms(detail:Detail,zone:string,formats:HoldingFormats):TermSection[]{
 const sections=detail.state.terms.scheduled?scheduledTerms(detail,zone,formats):singlePeriodTerms(detail,zone,formats);
 return detail.settlementValuation?[...sections,lifecycleSection(detail,formats)]:sections;
}
// The terms of a single-period swap revision: its structure, and each of its sides with its own terms.
function singlePeriodTerms(detail:Detail,zone:string,formats:HoldingFormats):TermSection[]{
 const s=detail.state,t=s.terms,catalog=detail.catalog,pair=t.swapType==='financial_floating_floating';
 const indexOf=(side:FloatingTerms)=>retainedIndex(catalog,t,side);
 const currency=(code:string)=>catalog.indices.find(i=>i.currencyCode===code)?.currency??code;
 // Every price of a swap is per its instrument quantity unit, which is the unit of its second floating side's index.
 const quantityIndex=indexOf(t.floating),payments=(payment:PaymentInput|null)=>payment?paymentRows(payment,catalog.paymentOptions):[];
 const common=(terms:{currency:string;deliveryFrom:string;deliveryTo:string;paymentSchedule:PaymentInput|null})=>[['Currency',currency(terms.currency)] as const,['Delivery',deliveryLabel(terms.deliveryFrom,terms.deliveryTo)] as const,...payments(terms.paymentSchedule)];
 const sides=sidesOf(detail).map(side=>{
  const title=sideTitle(side,pair);
  if(side.kind==='fixed'){
   const fixed=t.fixed;if(!fixed)throw Error('The retained swap terms do not hold a side that its valuation does.');
   const unit=priceUnit({currency:currency(fixed.currency),unit:quantityIndex.unit,quoteMultiplier:quantityIndex.quoteMultiplier});
   return {title,rows:[['Fixed price',`${minus(formats.price(fixed.price))} ${unit}`] as const,...common(fixed)]};
  }
  const floating=pair&&side.slot===0?t.firstFloating:t.floating;if(!floating)throw Error('The retained swap terms do not hold a side that its valuation does.');
  const index=indexOf(floating),unit=priceUnit({currency:currency(floating.currency),unit:index.unit,quoteMultiplier:index.quoteMultiplier});
  return {title,rows:[['Curve target index',friendly(floating.index)] as const,['Price settlement',settlementLabel(floating.settlement)] as const,['Projection curve',curveName(floating.curve)] as const,['Reset frequency',friendly(floating.resetFrequency)] as const,['Averaging',friendly(floating.averagingMethod)] as const,
   ['Spread',`${floating.spread??'None'} ${unit}`] as const,['Projected prices',floating.customProjection?`Retained custom output · v${floating.customProjection.version}`:'Contractual projection curve'] as const,...common(floating)]};
 });
 return [
  tradeSection(detail,zone),
  {title:'Structure',rows:[['Type',swapTypeLabels[t.swapType]??friendly(t.swapType)],['Commodity',catalog.commodities.find(c=>c.code===t.commodity)?.name??friendly(t.commodity)],['Quantity',`${minus(formats.quantity(s.quantity))} ${quantityIndex.unit}`],
   ['Discounting',discountingLabels[t.discounting?Discounting.CurrencyDefault:Discounting.None]]]},
  ...sides,
 ];
}
/** What the position panel's Terms and History tabs show of a swap revision: the same read the editor makes, and its schedules when it has them. */
export const swapDetailView=(detail:Detail,zone:string,formats:HoldingFormats,schedules?:(host:HTMLElement)=>()=>void):DetailView=>({terms:swapTerms(detail,zone,formats),history:swapHistory(detail,zone,formats),...(schedules?{schedules}:{})});
export const swapAmendState=(detail:Detail):AmendState=>detail.canAmend?{allowed:true,reason:null}:{allowed:false,reason:detail.amendUnavailableReason??'Read-only revision'};
/**
 * The markets a swap draft is for: the curve of each floating side's index with that side's delivery, in the revision of the market
 * definitions the draft was entered against, the first floating side first. A fixed side has no market of its own, and two sides of one
 * market and one delivery period are one market.
 */
export function swapDraftMarkets(draft:{definitionRevisionId:string;floating:Pick<FloatingTerms,'curve'|'deliveryFrom'|'deliveryTo'>;firstFloating:Pick<FloatingTerms,'curve'|'deliveryFrom'|'deliveryTo'>|null}):DraftMarket[]{
 const markets=[draft.firstFloating,draft.floating].flatMap(side=>side?[{curve:side.curve,deliveryFrom:side.deliveryFrom,deliveryTo:side.deliveryTo,definitionRevisionId:draft.definitionRevisionId}]:[]);
 return markets.filter((market,i)=>markets.findIndex(m=>m.curve===market.curve&&m.deliveryFrom===market.deliveryFrom&&m.deliveryTo===market.deliveryTo)===i);
}
/**
 * A swap's editor state names its instrument, not its binding, so an editor read must hold the instrument the position is in: a
 * trade's other positions are not shown from it.
 */
export function holdsInstrument(detail:Detail,h:Pick<Holding,'contractId'>){
 if(detail.state.instrumentId!==h.contractId)throw Error('The editor read holds another instrument of the trade than the position.');
}
const anotherInstrument="This revision belongs to another instrument of the trade than this position, so only the position's current revision can be amended.";
/**
 * What a revision read for a position may be used for. The position's own revision must be of its instrument, and a read of another is
 * refused. An earlier revision may be of another instrument, which an amendment of its price, dates or payment terms gives a trade, and
 * is read as it is; only the position's instrument can be amended from the position.
 */
export function swapAtPosition(detail:Detail,position:Pick<Holding,'contractId'|'revision'>,requested:number|null):Detail{
 if(requested===null||requested===Number(position.revision))holdsInstrument(detail,position);
 return detail.state.instrumentId===position.contractId?detail:{...detail,canAmend:false,amendUnavailableReason:detail.amendUnavailableReason??anotherInstrument};
}

type AmountText=(value:string,kind:'money'|'price'|'quantity'|'factor',role?:string)=>string;
type Priced={currency:string;unit:string;quoteMultiplier:string};
/** What a swap's schedules are drawn with: how its sides are priced and its amounts written, and the zone and busy state of the page. */
type ScheduleContext={pair:boolean;index:Priced|undefined;sourceOf:(side:{kind:string;slot:number})=>Priced|undefined;text:AmountText;zone:string;busy:()=>boolean};
const unitOf=(i:Pick<Priced,'currency'|'unit'>|undefined)=>`${i?.currency??''}/${i?.unit??''}`;
/** The line above a swap's schedules: its present value, and its notional, which applies once to each side's delivery period (D212), as the caller writes them. */
export function valuationLine(value:Valuation,index:Pick<Priced,'currency'|'unit'>|undefined,text:AmountText){
 return `Trade PV: ${text(value.presentValue,'money')} ${index?.currency??''} · notional: ${text(value.quantity,'quantity')} ${index?.unit??''} total for each side’s delivery period.`;
}
/** The line above one side's schedule: its present value per normalized unit and what it contributes. */
export function sideLine(side:Valuation['sides'][number],index:Pick<Priced,'currency'|'unit'>|undefined,text:AmountText){
 return `Side unit PV: ${text(side.presentValue,'money',side.kind)} ${unitOf(index)}; signed contribution: ${text(side.contribution,'money',side.kind)} ${unitOf(index)}.`;
}
/**
 * Draws each side's schedule of a valuation into `host`: its trade payments, with each payment's resolved quantity, signed cash and
 * present value; a floating side's contractual prices and their states; the observation behind each payment; and, folded beneath, its
 * normalized unit calculations (payments chart and table, the price lookups of each reset) and the evidence under them. Returns the
 * widgets that hold charts, for the caller to dispose.
 */
function renderSchedules(host:HTMLElement,value:Valuation,c:ScheduleContext){
 const i=c.index,widgets:ReturnType<typeof mountIntervalDataWidget>[]=[];
 for(const side of value.sides){
  const section=document.createElement('section');section.className='swap-schedule';const title=document.createElement('h3');title.textContent=sideTitle(side,c.pair);section.append(title);
  renderSwapTradePayments(section,'swap',side,i,c.busy,(amount,kind,role)=>c.text(amount,kind,role));
  const sum=document.createElement('p');sum.className='field-help';sum.textContent=sideLine(side,i,c.text);
  const payments=document.createElement('div');payments.className='table-wrap';
  const normalized=document.createElement('details'),normalLabel=document.createElement('summary');normalized.className='swap-normalized';normalLabel.textContent='Normalized unit calculations';normalized.append(normalLabel,sum,payments);section.append(normalized);
  const chart=document.createElement('div');normalized.insertBefore(chart,payments);host.append(section);
  const widget=mountIntervalDataWidget({table:payments,chart,key:`swap-${side.slot}-payments`,busy:c.busy});widgets.push(widget);
  widget.show(sideScheduleData(side.payments,JSON.stringify(side),unitOf(i),(amount,kind)=>c.text(amount,kind,side.kind)));
  const source=c.sourceOf(side),price=`${source?.currency}/${source?.unit}${source?.quoteMultiplier==='1'?'':' × '+source?.quoteMultiplier}`;
  if(side.kind==='floating'){
   const prices=document.createElement('div');prices.className='table-wrap swap-contract-prices';section.append(prices);
   const rows=side.payments.flatMap(p=>p.resets);
   renderRegularTable(prices,`swap-${side.slot}-contract-prices`,JSON.stringify(rows),[`Price · ${price}`,'State','Observation start','Observation end · exclusive'],
    rows.map(r=>[r.price,r.fixingState,r.from,r.to]),[0],c.busy,{displayRows:rows.map(r=>[c.text(r.price,'price',side.kind),r.fixingState,r.from,r.to])});
  }
  side.payments.forEach((payment,n)=>{if(payment.observationSchedule)renderSwapObservationInspection(section,payment.observationSchedule,payment.paymentDate,`swap-${side.slot}-${n}`,c.zone,{price,cash:unitOf(i),quantity:source?.unit??''},c.busy);});
  const resets=document.createElement('details'),label=document.createElement('summary'),table=document.createElement('div');label.textContent=`${side.payments.flatMap(p=>p.resets).length} ${side.kind==='fixed'?'fixed price row':'reset rows'} · inspect price lookups`;table.className='table-wrap';resets.append(label,table);normalized.append(resets);
  renderRegularTable(table,`swap-${side.slot}-resets`,JSON.stringify(side.payments),['Observation start','Observation end · exclusive',`Price · ${price}`,'Weight',`Cash · ${unitOf(i)}`,'State'],side.payments.flatMap(p=>p.resets.map(r=>[r.from,r.to,r.price,r.weight,r.cashContribution,r.fixingState])),[2,3,4],c.busy,{displayRows:side.payments.flatMap(p=>p.resets.map(r=>[r.from,r.to,c.text(r.price,'price',side.kind),c.text(r.weight,'quantity',side.kind),c.text(r.cashContribution,'money',side.kind),r.fixingState]))});
  const evidence=document.createElement('details'),evidenceLabel=document.createElement('summary'),evidenceBody=document.createElement('pre');evidenceLabel.textContent='Exact price lookups, observation lineage and discount inputs';evidenceBody.className='swap-evidence';evidenceBody.textContent=JSON.stringify(side.payments.map(p=>({paymentDate:p.paymentDate,discountMappingId:p.discountMappingId,discountCurve:p.discountCurve,discountPolicy:p.discountPolicy,pillars:p.pillars,resets:p.resets.map(r=>({from:r.from,to:r.to,spread:r.spread,lookup:r.lookup,lineage:r.lineage,fixingState:r.fixingState,fixing:r.fixing}))})),null,2);evidence.append(evidenceLabel,evidenceBody);section.append(evidence);
 }
 return widgets;
}
/**
 * The valuation and schedules of a single-period swap revision as the position panel's read view shows them, in `host` after its terms,
 * in the deal's own display formats. Returns what takes them down.
 */
function renderValuation(host:HTMLElement,detail:Detail,valuation:Valuation,deal:DealFormats,zone:string,busy:()=>boolean):()=>void{
 const terms=detail.state.terms,catalog=detail.catalog,pair=terms.swapType==='financial_floating_floating',index=retainedIndex(catalog,terms,terms.floating);
 const c:ScheduleContext={pair,index,sourceOf:side=>side.kind==='floating'&&pair&&side.slot===0?retainedIndex(catalog,terms,terms.firstFloating!):index,text:deal.text,zone,busy};
 const section=document.createElement('section'),heading=document.createElement('h3'),line=document.createElement('p'),schedules=document.createElement('div');
 section.className='side-panel-section';heading.textContent='Valuation & schedules';line.className='field-help';line.textContent=publicText(valuationLine(valuation,index,c.text));
 section.append(heading,line,schedules);host.append(section);
 const widgets=renderSchedules(schedules,valuation,c);
 return()=>{for(const widget of widgets)widget.dispose();};
}
/** What the read view of a swap position works with beside its read: the page's busy state and action, and the position's settlement view. */
export type SwapReadOptions={busy:()=>boolean;act:(work:()=>Promise<void>)=>Promise<void>;settle:()=>void};
/**
 * The editor's read for a position of the Blotter, which must hold the instrument that position is in, with the display formats of its
 * deal, which its valuation is written in: a single-period swap's schedules or a scheduled swap's review (D214) are drawn beneath its
 * terms, and a swap valued by its settlement lifecycle (D215), which has no pure valuation, lists that lifecycle's measures with them.
 * Every swap's Terms offer its settlement and P&L view.
 */
export async function readSwapView(api:Api,context:Record<string,unknown>,h:Pick<Holding,'tradeId'|'contractId'|'revision'>,formats:HoldingFormats,o:SwapReadOptions):Promise<DetailRead>{
 const detail=await readSwapDetail(api,context,h.tradeId,Number(h.revision));
 holdsInstrument(detail,h);
 const deal=dealFormats(api);deal.use(await deal.read(h.tradeId,{...context,convention}));
 const valuation=detail.valuation,scheduled=detail.state.terms.scheduled;
 if(valuation){await deal.prepare(valuation);for(const side of valuation.sides)await deal.prepare(side,side.kind);}
 const actions=[{id:'side-panel-settlement',label:'Settlement and P&L',run:o.settle}];
 const schedules=(zone:string)=>!valuation?undefined:scheduled?(host:HTMLElement)=>{
  const review=document.createElement('section');review.className='side-panel-section scheduled-review';host.append(review);
  renderScheduledReview(review,valuation,detail.quantityInputs,{indices:detail.catalog.indices.filter(i=>i.definitionRevisionId===detail.state.terms.definitionRevisionId),clocks:(marketReferenceChoices(detail.catalog.referenceChoices,detail.state.terms.definitionRevisionId)??detail.catalog.scheduledOptions)?.clocks??[],text:deal.text,zone,busy:o.busy,act:o.act,
   intervals:scheduled.first.power||scheduled.second.power?retainedIntervals(api,{...context,convention},detail):undefined});
  return()=>review.remove();
 }:(host:HTMLElement)=>renderValuation(host,detail,valuation,deal,zone,o.busy);
 return {amend:swapAmendState(detail),view:zone=>({...swapDetailView(detail,zone,formats,schedules(zone)),actions})};
}
type Position=Pick<Holding,'tradeId'|'contractId'|'revision'>;
export function setupSwapEditor(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Record<string,unknown>;queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;onDenied:()=>void;host:()=>HTMLElement;
 /** Asks whether to give up what the panel holds unsaved, when it holds any: false means the person kept it. */
 leave:()=>Promise<boolean>;
 /** Asks the person a question of a ticket's own in the panel, before the page is busy: true means they chose to go on. */
 ask:(question:{text:string;keep:string;go:string})=>Promise<boolean>;
 /** The shape of a market's curve at the close, for the form's Price before booking. */
 shape:(market:DraftMarket)=>Promise<CurveShape|string>;
 /** Hears each time the form starts or stops holding unsaved edits. */
 unsavedChanged:()=>void}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const text=(id:string,value:string)=>get(id).textContent=publicText(value);
 // The form is kept on the page's shelf until a host takes it, and goes back there when it is reset.
 const root=get('swap-editing'),shelf=root.parentElement!,edits=trackEdits(get('swap-form'),[],options.unsavedChanged);
 const mounted=()=>root.parentElement!==shelf;
 const mount=()=>{options.host().append(root);root.hidden=false;};
 mountSwapSideEditor('fixed');mountSwapSideEditor('floating');mountFirstFloatingEditor();arrangeSwapSections();
 const annotations=tradeAnnotations('swap',options.zone);
 // A trade's settlement and P&L view, and the scheduled swap ticket (D214), take the panel's form part as the swap ticket does (D221),
 // each in place of the others. The page opens another revision of a scheduled trade, and the settlement view from a ticket, once it
 // has asked about what the ticket holds.
 const settlement=setupSwapSettlement(options);
 const scheduled=setupScheduledSwapEditor({...options,reopen:revision=>replacing(()=>revision,()=>scheduled.keepRevision()),onSettlement:()=>settleFromForm()});
 const settlementButton=document.createElement('button');settlementButton.id='swap-settlement-open';settlementButton.type='button';settlementButton.className='secondary';settlementButton.textContent='Settlement and P&L';settlementButton.hidden=true;get('swap-mode').after(settlementButton);
 settlementButton.onclick=()=>settleFromForm();
 const scheduledButton=document.createElement('button');scheduledButton.id='swap-scheduled-open';scheduledButton.type='button';scheduledButton.className='secondary';scheduledButton.textContent='Monthly schedules · one or more months';get('swap-type-choices').after(scheduledButton);
 scheduledButton.onclick=()=>scheduledFromForm();
 let visibleValuation:Valuation|undefined;
 const quoteUnits=new Map<string,string>();
 let failedLiveSelection:{position:Position;revision:number}|undefined,position:Position|undefined;
 const display=dealFormatEditor(get('swap-amount-format'),{api:options.api,act:options.act,busy:options.busy,changed:async()=>{const value=visibleValuation,version=previewGeneration;if(value){await prepare(value);if(value===visibleValuation&&version===previewGeneration)valuation(value);}}});
 async function prepare(value:Valuation){await display.prepare(value);for(const side of value.sides)await display.prepare(side,side.kind);}
 let catalog:Catalog|undefined,detail:Detail|undefined,prepared:RequestCommand|undefined,generation=0,previewGeneration=0;
 const dates=createScheduleDatePicker(get('swap-date-tools'),{id:'swap-date-lookup',api:options.api,run:work=>options.act(()=>safe(work)),context:()=>catalog?.context,zone:options.zone,busy:options.busy,enabled:()=>!options.pending()&&!!catalog&&(!detail?!!catalog:detail.canAmend),targets:['fixed','first','floating'].flatMap(side=>['from','to'].map(suffix=>({input:get<HTMLInputElement>(`swap-${side}-${suffix}`),available:()=>!!field('swap-type').value&&activeSides().includes(side),acceptsDate:date=>side==='fixed'||sideDateChoices(side)[suffix==='from'?'starts':'ends'].includes(date),label:`${side==='fixed'?'Fixed':side==='first'?'First floating':'Floating'} side · delivery ${suffix==='from'?'start':'end (exclusive)'}`}))),edited:()=>edits.mark()});
 const key=()=>JSON.stringify(options.context());
 const context=()=>({...options.context(),convention});
 const indexKey=(i:Pick<Index,'definitionRevisionId'|'curve'|'index'>)=>JSON.stringify([i.definitionRevisionId,i.curve,i.index]);
 const floatingPair=()=>field('swap-type').value==='financial_floating_floating';
 const observationId=(side:string,suffix:string)=>`swap-${side==='first'?'first-':''}${suffix}`;
 const selectedIndex=(side='floating')=>catalog?.indices.find(i=>indexKey(i)===field(observationId(side,'projection')).value);
 const availableIndices=(side='floating')=>filterSwapIndices(catalog?.indices.filter(i=>i.primitiveEntrySupported)??[],field(observationId(side,'commodity')).value,field(observationId(side,'index-search')).value,side==='first'?selectedIndex()?.definitionRevisionId:undefined);
 const sources=(side='floating')=>availableIndices(side).filter(i=>!field(observationId(side,'index')).value||i.index===field(observationId(side,'index')).value);
 const projectionChoices=(side='floating')=>selections[side]?.override()?[]:selectedIndex(side)?.customProjections.filter(p=>p.samplings.some(s=>s.resetFrequency===field(observationId(side,'reset-frequency')).value&&s.averagingMethod===field(observationId(side,'averaging')).value))??[];
 const selections:Record<string,ReturnType<typeof mountPriceSettlement>>={};
 for(const side of ['floating','first'])selections[side]=mountPriceSettlement(get(`swap-${side}-observations`),side==='first'?'swap-first':'swap',{enabled:()=>!!field('swap-type').value&&(side==='floating'||floatingPair()),indices:()=>{const i=selectedIndex(side);return i?i.settlementIndices:[];},changed:()=>{observationOptions[side]=undefined;invalidate();priceSourceControls(side);}});
 let observationOptions:Record<string,ObservationOptions|undefined>={};
 const samplingRows=(side:string)=>observationOptions[side]?.samplings??selectedIndex(side)?.samplings??sources(side).flatMap(index=>index.samplings);
 const selectedSampling=(side:string)=>samplingRows(side).find(choice=>choice.resetFrequency===field(observationId(side,'reset-frequency')).value&&choice.averagingMethod===field(observationId(side,'averaging')).value)??selectedIndex(side)?.samplings.find(choice=>choice.resetFrequency===field(observationId(side,'reset-frequency')).value&&choice.averagingMethod===field(observationId(side,'averaging')).value);
 const observations:Record<string,ReturnType<typeof setupSwapObservationControls>>={};
 for(const side of ['floating','first'])observations[side]=setupSwapObservationControls({prefix:side==='first'?'swap-first':'swap',enabled:()=>!!field('swap-type').value&&(side==='floating'||floatingPair()),api:options.api,busy:options.busy,choice:()=>selectedSampling(side),unit:()=>selectedIndex(side)?.unit??'',
  request:()=>{const i=selectedIndex(side),definition=side==='first'?selectedIndex()??i:i,custom=projectionChoices(side).find(choice=>JSON.stringify(choice.reference)===field(observationId(side,'price-source')).value);return {context:{...context(),knowledgeMode:'retained',contextId:catalog?.context.contextId},
   ...(definition?{definitionRevisionId:definition.definitionRevisionId}:{}),...(i?{curve:i.curve,settlement:selections[side].selected()}:{}),commodity:field(observationId(side,'commodity')).value||undefined,index:field(observationId(side,'index')).value||undefined,
   resetFrequency:field(observationId(side,'reset-frequency')).value||undefined,averagingMethod:field(observationId(side,'averaging')).value||undefined,
   deliveryFrom:field(`swap-${side}-from`).value||undefined,deliveryTo:field(`swap-${side}-to`).value||undefined,
   ...(custom?{customProjection:custom.reference}:{}),observationCalendar:observations[side].calendar()};},
  apply:response=>{if((response.context as Context)?.contextId!==catalog?.context.contextId)throw Error('The observation choices belong to a different market context. Reload the editor.');observationOptions[side]=response;if(syncDeliveryDates(true,side)){deliveryRefreshNeeded=true;return;}indexChoices(side);projectionControls(side);syncDeliveryDates();},
 });
 let deliveryRefreshNeeded=false;
 async function refreshObservations(){const started=generation,source=key();for(const side of ['floating',...(floatingPair()?['first']:[])]){deliveryRefreshNeeded=false;await observations[side].refresh();if(started!==generation||source!==key())return;if(deliveryRefreshNeeded){await observations[side].refresh();if(started!==generation||source!==key())return;}}}
 const retainedFloating=(side:string)=>detail?.state.terms.commodity===field(observationId(side,'commodity')).value?(side==='first'?detail.state.terms.firstFloating:detail.state.terms.floating):undefined;
 const activeSides=()=>[floatingPair()?'first':'fixed','floating'];
 function sideDateChoices(side:string){const source=side==='fixed'?'floating':side;return deliveryChoices(observationOptions[source]?.deliveryPeriods??[],field(observationId(source,'reset-frequency')).value,field(observationId(source,'averaging')).value,field(`swap-${side}-from`).value);}
 function dateReady(side:string){const source=side==='fixed'?'floating':side,mode=selectedSampling(source);return !!selectedIndex(source)&&!!selections[source].selected()&&!!mode&&(!mode.requiresObservationCalendar||!!observations[source].calendar())&&!!observationOptions[source];}
 const deliveryPickers=['first','floating'].flatMap(side=>['from','to'].map(suffix=>{
  const input=get<HTMLInputElement>(`swap-${side}-${suffix}`),host=document.createElement('div');input.hidden=true;input.required=false;input.after(host);
  const picker=createAvailableDatePicker(host,{id:input.id+'-calendar',label:`${side==='first'?'First floating':side==='fixed'?'Fixed':'Floating'} delivery ${suffix==='from'?'start':'end (exclusive)'}`,busy:()=>options.busy()||!!options.pending()||!catalog||!!detail&&!detail.canAmend||!activeSides().includes(side)||!dateReady(side),onChange:date=>{input.value=date;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));edits.mark();}});
  const retained=document.createElement('p');retained.className='field-help';retained.hidden=true;host.append(retained);return{side,suffix,input,picker,retained};
 }));
 function syncDeliveryDates(clear=false,source?:string){let changed=false;
  for(const {side,suffix,input,picker,retained} of deliveryPickers){const choices=sideDateChoices(side)[suffix==='from'?'starts':'ends'];if(clear&&!detail&&dateReady(side)&&(!source||(side==='fixed'?'floating':side)===source)&&input.value&&!choices.includes(input.value)){input.value='';changed=true;}const unavailable=!!input.value&&!choices.includes(input.value);retained.hidden=!unavailable;retained.textContent=unavailable?`Entered date: ${input.value} · select compatible observation terms to verify availability.`:'';picker.set(detail&&!detail.canAmend&&unavailable?[...choices,input.value]:choices,input.value);}
  return changed;
 }

 function typeControls(){
  const type=field('swap-type').value;
  get('swap-type-choices').querySelectorAll<HTMLButtonElement>('button').forEach(button=>{button.setAttribute('aria-pressed',String(button.dataset.swapType===type));button.disabled=options.busy()||!!detail||!!options.pending();});
  for(const side of ['fixed','first','floating']){const on=!!type&&(side==='floating'||side===(floatingPair()?'first':'fixed'));for(const section of ['editor','delivery','observations']){get(`swap-${side}-${section}`).hidden=!on;get(`swap-${side}-${section}`).querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select').forEach(n=>n.disabled=!on);}if(on)paymentControls(side);}
  text('swap-floating-title',floatingPair()?'Second floating side':'Floating side');get('swap-side-compatibility').hidden=!floatingPair();direction();priceControls();
 }
 function priceControls(){
  const fixed=!!field('swap-type').value&&!floatingPair(),par=field('swap-price-mode').value==='par';
  get('swap-par-controls').hidden=!par;
  const precision=get<HTMLInputElement>('swap-par-places');precision.required=fixed&&par;precision.disabled=!fixed||!par;
  const price=get<HTMLInputElement>('swap-price');price.required=fixed&&!par;price.hidden=par;price.disabled=!fixed||par;
 }
 function applyPar(result:{resolvedFixedPrice?:string;parRate?:{decimalPlaces:number}|null}){
  if(result.resolvedFixedPrice!==undefined&&result.parRate){field('swap-price').value=result.resolvedFixedPrice;text('swap-par-result',`Par fixed rate: ${result.resolvedFixedPrice} · ${result.parRate.decimalPlaces} decimal places, ties to even. Review this fixed rate before booking.`);}
 }
 async function floatingDraft(side='floating'){
  const i=selectedIndex(side);if(!i)throw Error('Choose each exact floating index and projection curve.');
  const customProjection=projectionReference(field(observationId(side,'price-source')).value,projectionChoices(side));
  const observationSchedule=observations[side].read();
  return{curve:i.curve,index:i.index,settlement:selections[side].read(),currency:field(`swap-${side}-currency`).value,deliveryFrom:field(`swap-${side}-from`).value,deliveryTo:field(`swap-${side}-to`).value,paymentSchedule:paymentInput(side),...(customProjection?{customProjection}:{}),...(observationSchedule?{observationSchedule}:{}),resetFrequency:field(observationId(side,'reset-frequency')).value,averagingMethod:field(observationId(side,'averaging')).value,spread:field(observationId(side,'spread')).value};
 }
 async function instrumentDraft(){
  const i=selectedIndex();if(!i)throw Error('Choose the exact index and projection curve.');
  if(!field('swap-type').value)throw Error('Choose a swap type.');
  for(const side of activeSides().filter(side=>side!=='fixed')){const choices=sideDateChoices(side);if(!choices.starts.includes(field(`swap-${side}-from`).value)||!choices.ends.includes(field(`swap-${side}-to`).value))throw Error('Choose an available delivery start and end for each side.');}
  if(!floatingPair()&&field('swap-price-mode').value==='par'&&(!field('swap-par-places').value||!Number.isInteger(Number(field('swap-par-places').value))||Number(field('swap-par-places').value)<0||Number(field('swap-par-places').value)>18))throw Error('Choose 0–18 decimal places for the par rate.');
  if(!floatingPair()&&!['agreed','par'].includes(field('swap-price-mode').value))throw Error('Choose agreed or par fixed pricing.');
  if(!floatingPair()&&field('swap-price-mode').value==='agreed'&&!field('swap-price').value.trim())throw Error('Enter the agreed fixed price.');
  if(!Object.values(Discounting).includes(field('swap-discounting').value as Discounting))throw Error('Choose discounting explicitly.');
  if(!field('swap-quantity-unit').value)throw Error('Choose an instrument quantity unit.');
  if(floatingPair()&&selectedIndex('first')?.definitionRevisionId!==i.definitionRevisionId)throw Error('Both floating sides must use the same exact market definition revision.');
  return{context:context(),definitionRevisionId:i.definitionRevisionId,commodity:field('swap-commodity').value,swapType:field('swap-type').value,
   quantity:{value:field('swap-quantity').value,specification:JSON.parse(field('swap-quantity-unit').value)},fixedCoefficient:floatingPair()?null:field('swap-direction').value,firstCoefficient:floatingPair()?field('swap-first-direction').value:null,discounting:field('swap-discounting').value===Discounting.CurrencyDefault,
   fixed:floatingPair()?null:{price:field('swap-price-mode').value==='par'?'':field('swap-price').value,currency:field('swap-fixed-currency').value,deliveryFrom:field('swap-fixed-from').value,deliveryTo:field('swap-fixed-to').value,paymentSchedule:paymentInput('fixed')},
   firstFloating:floatingPair()?await floatingDraft('first'):null,floating:await floatingDraft(),
   parRatePolicy:!floatingPair()&&field('swap-price-mode').value==='par'?{implementation:catalog!.parRateOptions.implementation,decimalPlaces:Number(field('swap-par-places').value),tieRule:catalog!.parRateOptions.tieRule}:null};
 }
 const draft=draftValuation(get('swaps-draft-valuation'),{product:'swaps',api:options.api,act:options.act,busy:options.busy,key,denied:options.onDenied,draft:instrumentDraft,markets:swapDraftMarkets,shape:options.shape,priced:async result=>{applyPar(result);if(result.valuation){const value=result.valuation as Valuation,version=previewGeneration,started=generation;await prepare(value);if(version===previewGeneration&&started===generation)valuation(value);}}});
 const scheduleWidgets:ReturnType<typeof mountIntervalDataWidget>[]=[];
 function clearSchedules(){for(const widget of scheduleWidgets)widget.dispose();scheduleWidgets.length=0;get('swap-schedules').replaceChildren();}
 // What was prepared for a booking, its preview and its confirmation, is dropped: the booking has to be prepared again from the form.
 function unprepare(){renderChangeReview(get('swap-change-review'),null);visibleValuation=undefined;previewGeneration++;prepared=undefined;get('swap-result').hidden=true;get('swap-confirmation').hidden=true;get<HTMLInputElement>('swap-confirm').checked=false;sync();}
 function invalidate(){text('swap-par-result','');draft.reset();unprepare();}
 function sync(){settlement.sync();settlementButton.hidden=!detail;settlementButton.disabled=options.busy()||!!options.pending();scheduled.sync();scheduledButton.hidden=!!detail;typeControls();syncDeliveryDates();Object.values(observations).forEach(controls=>controls.sync());Object.values(selections).forEach(controls=>controls.sync());field('swap-type').disabled=options.busy()||!!detail;draft.sync();display.sync();dates.sync();get<HTMLFieldSetElement>('swap-fields').disabled=options.busy()||!!options.pending()||(!detail? !catalog:!detail.canAmend);get<HTMLButtonElement>('swap-preview').disabled=options.busy()||(!detail?!catalog?.canBook:!detail.canAmend);get<HTMLButtonElement>('swap-submit').disabled=options.busy()||!!options.pending()||!prepared||!get<HTMLInputElement>('swap-confirm').checked;field('swap-revision').disabled=options.busy()||!!options.pending();get<HTMLButtonElement>('swap-latest').disabled=options.busy()||!!options.pending();}
 function reset(){settlement.reset();scheduled.reset();resetTicket();}
 function resetTicket(){root.querySelectorAll('.booked-value-note').forEach(note=>note.remove());quoteUnits.clear();text('swap-template-note','Choose a structure to set up its sides.');observationOptions={};Object.values(observations).forEach(controls=>controls.reset());Object.values(selections).forEach(controls=>controls.reset());failedLiveSelection=undefined;display.reset();visibleValuation=undefined;annotations.reset();generation++;shelf.append(root);root.hidden=true;position=undefined;detail=undefined;catalog=undefined;dates.reset();invalidate();get<HTMLFormElement>('swap-form').reset();edits.clear();for(const id of ['swap-book','swap-commodity','swap-first-commodity','swap-index','swap-projection','swap-reset-frequency','swap-averaging','swap-fixed-currency','swap-floating-currency','swap-quantity-unit','swap-fixed-payment-calendar','swap-floating-payment-calendar','swap-revision','swap-first-index','swap-first-projection','swap-first-reset-frequency','swap-first-averaging','swap-first-currency','swap-first-payment-calendar','swap-price-source','swap-first-price-source'])field(id).replaceChildren();for(const id of ['swap-reference','swap-mode','swap-unit','swap-price-unit','swap-coverage','swap-projection-note','swap-first-projection-note','swap-legacy-fixing','swap-first-legacy-fixing','swap-pv','swap-effective','swap-error'])text(id,'');get('swap-error').hidden=true;for(const side of ['floating','first'])get(observationId(side,'legacy-fixing')).hidden=true;clearSchedules();get('swap-revisions').hidden=true;get('swap-amendment').hidden=true;for(const side of ['fixed','first','floating'])paymentControls(side);typeControls();}
 async function safe(work:()=>Promise<void>){const started=generation;try{get('swap-error').hidden=true;await work();}catch(error){if(started!==generation)return;text('swap-error',error instanceof Error?error.message:'The financial swap operation failed.');get('swap-error').hidden=false;if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}throw error;}}
 function choose(id:string,rows:{value:string;label:string}[],value='',retained?:string,linked=false){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(publicText(r.label),r.value)));if(value&&retained&&!rows.some(row=>row.value===value)){const option=new Option(publicText(unavailableValueLabel(retained)),value);markUnavailableValue(option);select.append(option);}select.value=[...select.options].some(row=>row.value===value)&&value?value:linked?linkedValue(rows.map(row=>row.value)):'';explainUnavailableValue(select);}
 function restoreSelection(id:string,value:string,label:string){
  const select=get<HTMLSelectElement>(id);
  if(![...select.options].some(option=>option.value===value)){const option=new Option(publicText(unavailableValueLabel(label)),value);markUnavailableValue(option);select.append(option);}
  select.value=value;explainUnavailableValue(select);
 }
 function direction(){const value=field(floatingPair()?'swap-first-direction':'swap-direction').value;text('swap-floating-direction',value==='-1'?'Receive floating':value==='1'?'Pay floating':'Choose the first-side direction.');}
 function samplingControls(side='floating'){const frequency=field(observationId(side,'reset-frequency')).value,rows=samplingRows(side).filter(s=>!frequency||s.resetFrequency===frequency),unique=[...new Map(rows.map(row=>[row.averagingMethod,row])).values()],value=field(observationId(side,'averaging')).value;choose(observationId(side,'averaging'),unique.map(s=>({value:s.averagingMethod,label:s.averagingMethod==='none'?'No averaging · one index price':s.label})),value,retainedFloating(side)?.averagingMethod===value?friendly(value):undefined);}
 function priceSourceControls(side='floating',retained?:CustomProjection|null){
  const choices=projectionChoices(side),value=retained===null?'ordinary':retained?JSON.stringify(retained):field(observationId(side,'price-source')).value;
  choose(observationId(side,'price-source'),selectedIndex(side)?[{value:'ordinary',label:'Contractual projection curve'},...choices.map(p=>({value:JSON.stringify(p.reference),label:p.label}))]:[],value);
  if(retained&&!choices.some(p=>JSON.stringify(p.reference)===value)){
   const unavailable=new Option(unavailableValueLabel(`Saved custom output · v${retained.version}`),value);markUnavailableValue(unavailable);
   get<HTMLSelectElement>(observationId(side,'price-source')).append(unavailable);field(observationId(side,'price-source')).value=value;
  }
  explainUnavailableValue(get<HTMLSelectElement>(observationId(side,'price-source')));
  const selected=choices.find(p=>JSON.stringify(p.reference)===field(observationId(side,'price-source')).value);
  text(observationId(side,'projection-note'),selected?`${selected.label}. This exact published output supplies projected prices; actual fixings remain index observations.`:field(observationId(side,'price-source')).value==='ordinary'?'Projected prices come from the selected contractual curve.':field(observationId(side,'price-source')).value?'The forecast source saved on this trade is unavailable in this context. Changing the saved source requires an amendment.':'Choose a projected price source. Published outputs are offered only for the selected index and sampling.');
 }
 function quantityUnitLabel(){text('swap-unit',get<HTMLSelectElement>('swap-quantity-unit').selectedOptions[0]?.textContent??'choose quantity unit');}
 function projectionControls(side='floating'){
  const candidates=sources(side).filter(i=>i.settlementIndices.length>0||(observationOptions[side]?observationOptions[side]!.indices.some(candidate=>indexKey(candidate)===indexKey(i)):i.samplings.some(s=>(!field(observationId(side,'reset-frequency')).value||s.resetFrequency===field(observationId(side,'reset-frequency')).value)&&(!field(observationId(side,'averaging')).value||s.averagingMethod===field(observationId(side,'averaging')).value))));
  const projection=field(observationId(side,'projection')).value,retained=retainedFloating(side);
  choose(observationId(side,'projection'),candidates.map(i=>({value:indexKey(i),label:curveName(i.curve)})),projection,retained&&projection===JSON.stringify([detail!.state.terms.definitionRevisionId,retained.curve,retained.index])?curveName(retained.curve):undefined);
  const frequency=field(observationId(side,'reset-frequency')).value;choose(observationId(side,'reset-frequency'),[...new Set(samplingRows(side).map(s=>s.resetFrequency))].map(value=>({value,label:friendly(value)})),frequency,retained?.resetFrequency===frequency?friendly(frequency):undefined);
  const i=selectedIndex(side);selections[side].sync();samplingControls(side);priceSourceControls(side);
  const bases=[...new Set((i?[i]:sources(side)).map(candidate=>JSON.stringify([candidate.currencyCode,candidate.quantityCode,candidate.quoteMultiplier])))];
  const basis=bases.length===1?bases[0]:undefined,prior=quoteUnits.get(side);
  if(prior&&prior!==basis){field(observationId(side,'spread')).value='';if(side==='floating')field('swap-price').value='';}
  if(basis)quoteUnits.set(side,basis);else quoteUnits.delete(side);
  text(observationId(side,'fixing-convention'),!i?'Choose an index and projection curve to inspect its fixing convention.':i.fixingConvention==='published_index_observations_v1'?'Fixings follow the selected index’s published observation schedule. Pricing validates the selected dates; inspect exact fixing sources and instants with each valued reset.':i.fixingUnavailableReason??'This index has no qualified fixing schedule.');text(observationId(side,'spread-unit'),i?`${i.currency}/${i.unit}${i.quoteMultiplier==='1'?'':' × '+i.quoteMultiplier}`:'choose projection curve');
  if(side==='floating'){
   const compatible=i?[i]:sources(side),units=[...new Map(compatible.flatMap(candidate=>candidate.quantityChoices).map(choice=>[JSON.stringify(choice.specification),choice])).values()],codes=[...new Set(compatible.map(candidate=>candidate.quantityCode))],old=field('swap-quantity-unit').value;if(old&&!units.some(u=>JSON.stringify(u.specification)===old))field('swap-quantity').value='';choose('swap-quantity-unit',units.map(u=>({value:JSON.stringify(u.specification),label:u.unit})),quantityChoice(units,codes.length===1?codes[0]:'',old));quantityUnitLabel();text('swap-price-unit',i?`${i.currency}/${i.unit}${i.quoteMultiplier==='1'?'':' × '+i.quoteMultiplier}`:'choose projection curve');
   text('swap-coverage',i?`${i.currency}/${i.unit} × ${i.quoteMultiplier}. Projection curve: ${curveName(i.curve)}. Available delivery periods: ${i.contracts.map(c=>deliveryLabel(c.deliveryFrom,c.deliveryTo)).join(', ')}. Default discount curve: ${i.defaultDiscountCurve?curveName(i.defaultDiscountCurve):'not configured'}.`:'Choose the floating index, reset frequency and exact projection curve.');
  }
  const currencies=[...new Map((i?[i]:sources(side)).map(i=>[i.currencyCode,{value:i.currencyCode,label:i.currency}])).values()];
  for(const slot of side==='first'?['first']:['fixed','floating'])choose(`swap-${slot}-currency`,currencies,field(`swap-${slot}-currency`).value,undefined,true);
  const calendars=marketReferenceChoices(catalog?.referenceChoices,i?.definitionRevisionId)?.paymentCalendars??catalog?.paymentCalendars??[];
  for(const slot of side==='first'?['first']:['fixed','floating'])choose(`swap-${slot}-payment-calendar`,calendars.map(c=>({value:c.code,label:friendly(c.code)})),field(`swap-${slot}-payment-calendar`).value);
 }
 function indexControls(side='floating'){
  observationOptions[side]=undefined;choose(observationId(side,'reset-frequency'),[...new Set(sources(side).flatMap(i=>i.samplings.map(s=>s.resetFrequency)))].map(value=>({value,label:friendly(value)})),field(observationId(side,'reset-frequency')).value);projectionControls(side);
 }
 function indexChoices(side:string,linked=false){
  const indices=availableIndices(side).filter(index=>index.settlementIndices.length>0||!observationOptions[side]||observationOptions[side]!.indices.some(candidate=>indexKey(candidate)===indexKey(index)));
  const value=field(observationId(side,'index')).value;choose(observationId(side,'index'),[...new Set(indices.map(index=>index.index))].map(value=>({value,label:friendly(value)})),value,retainedFloating(side)?.index===value?friendly(value):undefined,linked);
 }
 function commodityControls(selected?:string){
  for(const side of selected?[selected]:['floating','first']){
   observationOptions[side]=undefined;observations[side].reset();
   for(const suffix of ['index-search','index','projection','reset-frequency','averaging','price-source'])field(observationId(side,suffix)).value='';
   indexChoices(side,true);indexControls(side);
  }
 }
 function applyEntryTemplate(){
  if(!catalog||detail)return;
  const template=catalog.entryTemplate;
  const coefficient=floatingPair()?'swap-first-direction':'swap-direction';
  if(!field(coefficient).value)field(coefficient).value=template.firstCoefficient;
  for(const side of activeSides()){
   if(['payment-frequency','payment-rule','payment-number','day-basis','short-month','payment-adjustment','payment-calendar'].every(key=>!field(`swap-${side}-${key}`).value))setPayment(side,template.payment);
   if(side!=='fixed'&&!field(observationId(side,'spread')).value)field(observationId(side,'spread')).value=template.spread;
  }
  text('swap-template-note',template.description);direction();
 }
 function populateCatalog(){if(!catalog)return;
  annotations.populate(catalog.bookingActor,catalog,detail?.state);
  const policies=catalog.paymentOptions;field('swap-par-places').value=String(catalog.parRateOptions.initialDecimalPlaces);
  for(const side of ['fixed','first','floating'])for(const [suffix,choices] of [['payment-frequency',policies.frequencies],['payment-rule',policies.rules],['day-basis',policies.dayBases],['short-month',policies.shortMonths],['payment-adjustment',policies.adjustments]] as const)choose(`swap-${side}-${suffix}`,choices);
 choose('swap-book',catalog.books.map(b=>({value:b.id,label:`${b.portfolio} · ${b.name}`})));for(const side of ['floating','first'])choose(observationId(side,'commodity'),catalog.commodities.filter(c=>catalog!.indices.some(i=>i.commodity===c.code)).map(c=>({value:c.code,label:c.name})));commodityControls();}
 function paymentControls(side:string){
  const rule=field(`swap-${side}-payment-rule`).value,lag=rule===PaymentDateRule.DaysAfterPeriodEnd,next=rule===PaymentDateRule.DayOfFollowingMonth;
  text(`swap-${side}-payment-number-label`,lag?'Days after last delivery day':next?'Day of following month':'Rule parameter');
  const number=get<HTMLInputElement>(`swap-${side}-payment-number`);number.disabled=!lag&&!next;number.min=lag?'0':'1';number.max=lag?'366':'31';
  for(const [key,on] of [['day-basis',lag],['short-month',next]] as const){get(`swap-${side}-${key}-field`).hidden=!on;field(`swap-${side}-${key}`).disabled=!on;get<HTMLSelectElement>(`swap-${side}-${key}`).required=on;}
  const adjustment=field(`swap-${side}-payment-adjustment`).value;
  const usesCalendar=lag&&field(`swap-${side}-day-basis`).value===PaymentDayBasis.BusinessDays||!!adjustment&&adjustment!==DateAdjustment.Unadjusted;
  get(`swap-${side}-payment-calendar-field`).hidden=!usesCalendar;field(`swap-${side}-payment-calendar`).disabled=!usesCalendar;get<HTMLSelectElement>(`swap-${side}-payment-calendar`).required=usesCalendar;
 }
 function paymentInput(side:string):PaymentInput{
  const rule=field(`swap-${side}-payment-rule`).value,lag=rule===PaymentDateRule.DaysAfterPeriodEnd;
  const adjustment=field(`swap-${side}-payment-adjustment`).value;
  return{frequency:field(`swap-${side}-payment-frequency`).value,rule,days:lag?Number(field(`swap-${side}-payment-number`).value):null,dayOfMonth:lag?null:Number(field(`swap-${side}-payment-number`).value),dayBasis:lag?field(`swap-${side}-day-basis`).value:null,shortMonth:lag?null:field(`swap-${side}-short-month`).value,adjustment,calendar:lag&&field(`swap-${side}-day-basis`).value===PaymentDayBasis.BusinessDays||adjustment!==DateAdjustment.Unadjusted?field(`swap-${side}-payment-calendar`).value:null,implementation:catalog!.paymentOptions.implementation} as PaymentInput;
 }
 function setPayment(side:string,value:PaymentInput|null){
  for(const [id,v] of [['payment-frequency',value?.frequency],['payment-rule',value?.rule],['payment-number',value?.days??value?.dayOfMonth],['day-basis',value?.dayBasis],['short-month',value?.shortMonth],['payment-adjustment',value?.adjustment],['payment-calendar',value?.calendar]])field(`swap-${side}-${id}`).value=v===null||v===undefined?'':String(v);
  paymentControls(side);
 }
 function valuation(value:Valuation){
  visibleValuation=value;
  const i=selectedIndex(),first=selectedIndex('first');
  const c:ScheduleContext={pair:floatingPair(),index:i,sourceOf:side=>side.kind==='floating'&&floatingPair()&&side.slot===0?first:i,text:(amount,kind,role)=>display.text(amount,kind,role),zone:options.zone(),busy:options.busy};
  text('swap-pv',valuationLine(value,i,c.text));
  clearSchedules();scheduleWidgets.push(...renderSchedules(get('swap-schedules'),value,c));
  get('swap-result').hidden=false;
 }
 function renderDetail(value:Detail&{valuation:Valuation}){detail=value;catalog={...value.catalog,context:value.context,asOf:value.asOf};dates.invalidate();populateCatalog();const state=value.state,t=state.terms;field('swap-type').value=t.swapType;typeControls();for(const side of ['floating','first'])field(observationId(side,'commodity')).value=t.commodity;commodityControls();text('swap-template-note','Retained trade terms. Each side shows its saved values.');
  field('swap-book').value=t.bookId;field('swap-quantity').value=t.quantity;field(floatingPair()?'swap-first-direction':'swap-direction').value=t.firstCoefficient??t.fixedCoefficient;
  for(const side of ['floating',...(floatingPair()?['first']:[])]){
   const terms=side==='first'?t.firstFloating!:t.floating;
   observations[side].restore(terms.observationSchedule);
   const i=retainedIndex(catalog,t,terms);
   restoreSelection(observationId(side,'index'),i.index,friendly(i.index));indexControls(side);restoreSelection(observationId(side,'reset-frequency'),terms.resetFrequency,friendly(terms.resetFrequency));restoreSelection(observationId(side,'projection'),indexKey(i),curveName(i.curve));selections[side].restore(terms.settlement);restoreSelection(observationId(side,'averaging'),terms.averagingMethod,friendly(terms.averagingMethod));projectionControls(side);field(observationId(side,'spread')).value=terms.spread??'0';priceSourceControls(side,terms.customProjection??null);text(observationId(side,'legacy-fixing'),terms.firstFixingAt?`Retained legacy first fixing boundary: ${displayInstant(terms.firstFixingAt,options.zone())}. New pricing uses the selected index convention.`:'');get(observationId(side,'legacy-fixing')).hidden=!terms.firstFixingAt;
  }
  if(t.fixed){field('swap-price').value=t.fixed.price;field('swap-price-mode').value='agreed';}
  for(const side of activeSides()){const terms=side==='first'?t.firstFloating!:side==='fixed'?t.fixed!:t.floating;field(`swap-${side}-currency`).value=terms.currency;field(`swap-${side}-from`).value=terms.deliveryFrom;field(`swap-${side}-to`).value=terms.deliveryTo;setPayment(side,terms.paymentSchedule);}
  field('swap-discounting').value=t.discounting?Discounting.CurrencyDefault:Discounting.None;field('swap-reason').value='';direction();
  text('swap-reference',`${state.tradeReference} · ${state.instrumentReference} · Revision ${state.revision}`);text('swap-mode',value.canAmend?'Latest booked revision · an amendment creates a new revision':value.amendUnavailableReason??'Read-only revision');text('swap-effective',`Amendment effective: ${displayInstant(value.asOf,options.zone())}. Prior revisions remain unchanged.`);
  choose('swap-revision',value.history.map(s=>({value:String(s.revision),label:`Revision ${s.revision} · ${s.kind}`})),String(state.revision));get('swap-revisions').hidden=false;get('swap-amendment').hidden=false;get<HTMLInputElement>('swap-reason').required=value.canAmend;
  prepared=undefined;get('swap-confirmation').hidden=true;valuation(value.valuation);sync();
 }
 /**
  * Opens a new swap in the host once its catalog and observation choices are read, and says whether it did: another open, a reset or a
  * change of context while they were read leaves it closed. Booking is the backend's to allow, so where it does not, no field opens.
  */
 async function openNew():Promise<boolean>{
  reset();const started=generation,source=key();let opened=false;
  await safe(async()=>{
   const response=await options.api<Catalog>('swaps/catalog',context());if(started!==generation||source!==key())return;
   const control=bookingAllowed({canBook:response.canBook===true,bookingUnavailableReason:response.bookingUnavailableReason});
   if(!control.enabled)throw Error(control.reason);
   catalog=response;populateCatalog();await refreshObservations();if(started!==generation||source!==key())return;
   text('swap-mode','Choose a swap type and enter each independent side.');typeControls();get<HTMLInputElement>('swap-reason').required=false;sync();mount();opened=true;
  });
  return opened;
 }
 /**
  * Opens a position's swap at one of its revisions, the latest for none, in the host. Its own revision must hold the position's instrument;
  * an earlier one is read as it is. A trade whose settlement is under way opens its settlement view, and a scheduled trade its own ticket.
  */
 async function open(at:Position,revision:number|null){reset();position=at;const started=++generation,source=key();invalidate();await safe(async()=>{const read=await readSwapDetail(options.api,options.context(),at.tradeId,revision);if(started!==generation||source!==key())return;const response=swapAtPosition(read,at,revision);if(response.settlementValuation){await settlement.open(at.tradeId);return;}if(response.state.terms.scheduled){await scheduled.openDetail(response);return;}const valuation=response.valuation;if(!valuation)throw Error('This swap is valued by its settlement lifecycle: open its settlement view.');await display.load(response.state.tradeId,context(),response.canAmend,response.state.terms.firstFloating?['trade','floating']:['trade','fixed','floating']);await prepare(valuation);if(started!==generation||source!==key())return;renderDetail({...response,valuation});await refreshObservations();if(started!==generation||source!==key())return;mount();});}
 /** Opens the settlement and P&L view of a position in the host, in place of any ticket. */
 async function openSettlement(at:Position){reset();position=at;await safe(()=>settlement.open(at.tradeId));}
 // The settlement view replaces the ticket of the position it is open for, which is asked about first, before the page is busy.
 function settleFromForm(){
  if(!position||options.busy())return;
  const at=position;
  void (async()=>{if(await options.leave())await options.act(()=>openSettlement(at));})();
 }
 // A new scheduled swap's ticket replaces a new swap's, which is asked about first. The new swap's ticket stays until the scheduled one
 // has been read and is in the host, so that a read that fails leaves the panel with a ticket.
 function scheduledFromForm(){
  if(position||options.busy())return;
  void (async()=>{if(await options.leave())await options.act(()=>safe(async()=>{await scheduled.openNew();if(scheduled.isOpen())resetTicket();}));})();
 }
 field('swap-quantity-unit').onchange=quantityUnitLabel;
 field('swap-price-mode').onchange=()=>{field('swap-price').value='';priceControls();};
 get('swap-type-choices').querySelectorAll<HTMLButtonElement>('button').forEach(button=>button.onclick=()=>{field('swap-type').value=button.dataset.swapType!;field('swap-type').dispatchEvent(new Event('change',{bubbles:true}));});
 field('swap-type').onchange=()=>{invalidate();typeControls();applyEntryTemplate();dates.sync();};
 for(const side of ['floating','first'])field(observationId(side,'commodity')).onchange=()=>{invalidate();if(side==='floating')field('swap-price').value='';const spread=field(observationId(side,'spread'));if(spread.value!==catalog?.entryTemplate.spread)spread.value='';commodityControls(side);};
 for(const side of ['first','floating']){
  field(observationId(side,'index-search')).oninput=()=>{observationOptions[side]=undefined;invalidate();indexChoices(side);indexControls(side);};
  field(observationId(side,'averaging')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};field(observationId(side,'price-source')).onchange=()=>{invalidate();priceSourceControls(side);};
  field(observationId(side,'index')).onchange=()=>{invalidate();indexControls(side);};field(observationId(side,'reset-frequency')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};field(observationId(side,'projection')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};
 }
 for(const side of ['fixed','first','floating'])for(const id of ['payment-rule','day-basis','payment-adjustment'])field(`swap-${side}-${id}`).addEventListener('change',()=>paymentControls(side));field('swap-direction').onchange=direction;field('swap-first-direction').onchange=direction;
 get<HTMLFormElement>('swap-form').addEventListener('change',event=>{invalidate();const id=(event.target as HTMLElement).id;if(['swap-type','swap-commodity','swap-first-commodity','swap-fixed-from','swap-fixed-to',...['first','floating'].flatMap(side=>['index','index-search','projection','reset-frequency','averaging','price-source','observation-calendar','settlement-mode','settlement-index','settlement-policy'].map(suffix=>observationId(side,suffix)).concat([`swap-${side}-from`,`swap-${side}-to`]))].includes(id)){void options.act(()=>safe(refreshObservations));}});
 get<HTMLFormElement>('swap-form').addEventListener('input',event=>{invalidate();if(!(event.target as Element).closest('.schedule-date-picker'))dates.invalidate();});
 get<HTMLFormElement>('swap-form').onsubmit=event=>{event.preventDefault();void options.act(()=>safe(async()=>{
  const i=selectedIndex();if(!i||!catalog)throw Error('Choose a supported exact index and curve.');if(detail&&!detail.canAmend)throw Error('Only the latest eligible trade revision can be amended.');
  const started=generation,preview=previewGeneration,source=key();const instrument=await instrumentDraft();const priced=await options.api<{quantity:{amount:string};resolvedFixedPrice?:string;parRate?:{decimalPlaces:number}|null}>('swaps/price',instrument);if(!priced.quantity)throw Error('The instrument quantity conversion is incomplete.');if(started!==generation||preview!==previewGeneration||source!==key())return;
  applyPar(priced);
  const {context:unused,quantity:entered,parRatePolicy,...terms}=instrument;
  const body={...context(),...annotations.read(),...terms,knowledgeMode:'current',bookId:field('swap-book').value,quantity:priced.quantity.amount,fixed:instrument.fixed?{...instrument.fixed,price:priced.resolvedFixedPrice??instrument.fixed.price}:null,
   ...(detail?{tradeId:detail.state.tradeId,expectedRevision:detail.state.revision,effectiveAt:detail.asOf,reason:field('swap-reason').value}:{})};
  if(!Object.values(Discounting).includes(field('swap-discounting').value as Discounting))throw Error('Choose discounting explicitly.');const command:RequestCommand={operationId:crypto.randomUUID(),path:detail?'swaps/amend':'swaps/book',label:detail?`Swap amendment of ${detail.state.tradeReference}`:'New swap',body};
  const response=await options.api<{valuation:Valuation;changeReview:ChangeReview|null}>('swaps/preview',{...body,operationId:command.operationId});if(started!==generation||preview!==previewGeneration||source!==key())return;
  await prepare(response.valuation);if(started!==generation||preview!==previewGeneration||source!==key())return;if(detail&&!response.changeReview)throw Error('Preview the amendment again to review its changes.');renderChangeReview(get('swap-change-review'),response.changeReview);if(response.changeReview?.action==='keep_booked_terms'){prepared=undefined;get('swap-confirmation').hidden=true;get<HTMLInputElement>('swap-confirm').checked=false;valuation(response.valuation);sync();return;}prepared=command;valuation(response.valuation);get('swap-confirmation').hidden=false;get<HTMLInputElement>('swap-confirm').checked=false;text('swap-confirm-label',detail?'I reviewed the booked and proposed terms and will submit a trade amendment.':'Commit these exact reviewed terms');text('swap-submit',detail?'Submit trade amendment':'Book financial swap');sync();
 }));};
 get<HTMLInputElement>('swap-confirm').onchange=sync;
 get('swap-submit').onclick=()=>void options.act(()=>safe(async()=>{if(!prepared||!get<HTMLInputElement>('swap-confirm').checked)throw Error('Validate schedules and confirm the exact terms first.');await options.queue(prepared);}));
 // Another revision replaces what the form holds, which is asked about first, before the page is busy, as every other way out of it is.
 function replacing(revision:()=>number|null,kept=()=>{field('swap-revision').value=String(detail?.state.revision??'');}){
  if(!position||options.busy())return;
  const at=position;
  void (async()=>{if(await options.leave())await options.act(()=>open(at,revision()));else kept();})();
 }
 field('swap-revision').onchange=()=>replacing(()=>Number(field('swap-revision').value));get('swap-latest').onclick=()=>replacing(()=>null);
 return{open,openNew,openSettlement,reset,sync,
  // A live correction reads again what the panel's form part shows of a position: its settlement view, or its ticket at the revision shown.
  async liveRefresh(){
   if(settlement.isOpen()){await settlement.liveRefresh();return;}
   const shown=scheduled.isOpen()?scheduled.revision():mounted()&&detail?Number(field('swap-revision').value):undefined;
   const requested=failedLiveSelection??(position&&shown!==undefined?{position,revision:shown}:undefined);
   if(requested){try{await open(requested.position,requested.revision);}catch(error){if((error as {code?:string}).code!=='access_denied')failedLiveSelection=requested;throw error;}}
   else if(mounted()){catalog=await options.api<Catalog>('swaps/catalog',context());await refreshObservations();previewGeneration++;prepared=undefined;get('swap-result').hidden=true;get('swap-confirmation').hidden=true;dates.invalidate();dates.sync();await draft.liveRefresh();sync();}
  },
  refresh(){settlement.refresh();scheduled.refresh();annotations.refresh();dates.invalidate();dates.sync();invalidate();},
  /** Reads the settlement view's state again when it is open, after the settlement command `operationId` went through, which it may have sent. */
  async reloadSettlement(operationId:string){if(settlement.isOpen())await settlement.liveRefresh(operationId);},
  /** Whether the settlement view is in the host. */
  settlementOpen:()=>settlement.isOpen(),
  // The position whose ticket or settlement view is in the host (none for a new swap), and the revision it shows.
  shown:()=>mounted()||scheduled.isOpen()||settlement.isOpen()?position:undefined,
  displayedRevision:()=>mounted()?detail?.state.revision:scheduled.revision()??settlement.revision(),
  /** A booking the backend refused for good: what was prepared for it is dropped, and the ticket, with what was typed in it, is kept to be corrected and prepared again. */
  unprepare(){unprepare();scheduled.unprepare();},
  unsaved:()=>settlement.unsaved()||scheduled.unsaved()||mounted()&&edits.edited()};
}
