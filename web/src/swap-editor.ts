import {mountIntervalDataWidget} from './interval-data-widget.js';
import {sideScheduleData} from './side-schedule.js';
import {linkedValue,quantityChoice,filterSwapIndices,deliveryChoices} from './swap-selection.js';
import {createAvailableDatePicker} from './available-date-picker.js';
import {draftValuation} from './draft-valuation.js';
import {mountSwapSideEditor,mountFirstFloatingEditor} from './swap-side-editor.js';
import {dealFormatEditor,dealFormats,type DealFormats} from './deal-format-editor.js';
import {tradeAnnotations,bookingRows,historyBooking,type PartyCatalog,type Trader,type Annotation} from './trade-annotations.js';
import {createScheduleDatePicker} from './schedule-date-picker.js';
import type {Context} from './date-editor.js';
import {PaymentDateRule,PaymentDayBasis,DateAdjustment,Discounting,type Choice,type PaymentInput,type PaymentOptions} from './swap-contract.js';
import {minus,priceUnit,type Holding,type HoldingFormats} from './desk-model.js';
import type {AmendState,DetailRead,DetailView,TermSection} from './position-panel.js';
import {trackEdits} from './edit-tracking.js';
import {projectionReference,type CustomProjection,type ProjectionChoice} from './swap-projections.js';
import {publicText,displayInstant,deliveryLabel,curveName,friendly,timeLabels} from './market-view.js';
import {renderRegularTable} from './regular-table.js';
import type {RequestCommand} from './pending-command.js';
import {renderSwapObservationInspection,setupSwapObservationControls} from './swap-observations-view.js';
import type {SamplingChoice,ObservationInspection,ObservationOptions,ObservationSchedule} from './swap-observations.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Index={definitionRevisionId:string;curve:string;index:string;commodity:string;currencyCode:string;currency:string;unit:string;quoteMultiplier:string;quantityCode:string;quantityChoices:{specification:{code:string};unit:string}[];defaultDiscountCurve:string|null;fixingConvention:'published_index_observations_v1'|null;fixingUnavailableReason:string|null;customProjections:ProjectionChoice[];averaging:string[];samplings:SamplingChoice[];contracts:{deliveryFrom:string;deliveryTo:string}[]};
type Catalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string}[];indices:Index[];commodities:{code:string;name:string}[];paymentCalendars:{code:string}[];paymentOptions:PaymentOptions;entryTemplate:{version:string;firstCoefficient:string;spread:string;description:string;payment:PaymentInput};parRateOptions:{implementation:string;initialDecimalPlaces:number;tieRule:string};asOf:string;context:Context;canBook?:boolean};
type FloatingTerms={curve:string;index:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null;firstFixingAt?:string|null;customProjection?:CustomProjection|null;observationSchedule?:ObservationSchedule;resetFrequency:string;averagingMethod:string;spread:string};
type Terms={swapType:string;firstCoefficient:string|null;firstFloating:FloatingTerms|null;commodity:string;definitionRevisionId:string;bookId:string;executedAt:string;quantity:string;fixedCoefficient:string;discounting:boolean;fixed:{price:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null}|null;floating:FloatingTerms};
type State=Annotation&{tradeId:string;tradeReference:string;instrumentId:string;instrumentReference:string;revision:number;quantity:string;kind:string;reason:string;effectiveAt:string;recordedAt:string;terms:Terms};
type Valuation={presentValue:string;instrumentPresentValue:string;quantity:string;sides:{slot:number;kind:string;coefficient:string;presentValue:string;contribution:string;payments:{deliveryFrom:string;deliveryTo:string;paymentDate:string;cashAmount:string;discountFactor:string;presentValue:string;discountMappingId:string|null;discountCurve:unknown;discountPolicy:string;pillars:unknown;observationSchedule:ObservationInspection|null;resets:{spread:string|null;lookup:unknown;lineage:unknown;from:string;to:string;price:string;weight:string;cashContribution:string;fixingState:string;fixing?:unknown}[]}[]}[]};
type Detail={state:State;history:State[];catalog:Catalog;valuation:Valuation;canAmend:boolean;amendUnavailableReason:string|null;asOf:string;context:Context};
export type SwapDetail=Detail;
const convention='independent_side_financial_swap_v2';
/** The editor's read of one swap revision (null: the latest), in the market context the page reads in. */
export const readSwapDetail=(api:Api,context:Record<string,unknown>,tradeId:string,revision:number|null)=>api<Detail>('swaps/editor',{context:{...context,convention},tradeId,revision});
/** A side's title, as the editor and the position panel name it: which side, and whether it is paid or received. A coefficient of another sign is shown as it is. */
export const sideTitle=(side:{slot:number;kind:string;coefficient:string},floatingPair:boolean)=>
 `${side.kind==='fixed'?'Fixed':floatingPair?(side.slot===0?'First floating':'Second floating'):'Floating'} side · ${side.coefficient==='-1'?'Pay':side.coefficient==='1'?'Receive':`coefficient ${side.coefficient}`}`;
const swapTypeLabels:Readonly<Record<string,string>>={financial_fixed_floating:'Fixed for floating',financial_floating_floating:'Floating for floating'};
const discountingLabels:Readonly<Record<Discounting,string>>={[Discounting.CurrencyDefault]:'Currency default curve',[Discounting.None]:'Explicitly undiscounted'};
/**
 * The revisions of a swap as the History tab lists them: each with the instrument it was of, when it took effect and was recorded, and
 * its facts, its quantity written as Terms writes it and in the unit of that revision's own terms.
 */
export function swapHistory(detail:Detail,zone:string,formats:HoldingFormats):TermSection[]{
 const labels=timeLabels(detail.history.flatMap(s=>[s.effectiveAt,s.recordedAt]),zone);
 const quantity=(s:State)=>{
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
/** The terms of a swap revision as a read view lists them: its booking facts, its structure, and each of its sides with its own terms. */
export function swapTerms(detail:Detail,zone:string,formats:HoldingFormats):TermSection[]{
 const s=detail.state,t=s.terms,catalog=detail.catalog,book=catalog.books.find(b=>b.id===t.bookId),pair=t.swapType==='financial_floating_floating';
 const indexOf=(side:FloatingTerms)=>retainedIndex(catalog,t,side);
 const currency=(code:string)=>catalog.indices.find(i=>i.currencyCode===code)?.currency??code;
 // Every price of a swap is per its instrument quantity unit, which is the unit of its second floating side's index.
 const quantityIndex=indexOf(t.floating),payments=(payment:PaymentInput|null)=>payment?paymentRows(payment,catalog.paymentOptions):[];
 const common=(terms:{currency:string;deliveryFrom:string;deliveryTo:string;paymentSchedule:PaymentInput|null})=>[['Currency',currency(terms.currency)] as const,['Delivery',deliveryLabel(terms.deliveryFrom,terms.deliveryTo)] as const,...payments(terms.paymentSchedule)];
 const sides=detail.valuation.sides.map(side=>{
  const title=sideTitle(side,pair);
  if(side.kind==='fixed'){
   const fixed=t.fixed;if(!fixed)throw Error('The retained swap terms do not hold a side that its valuation does.');
   const unit=priceUnit({currency:currency(fixed.currency),unit:quantityIndex.unit,quoteMultiplier:quantityIndex.quoteMultiplier});
   return {title,rows:[['Fixed price',`${minus(formats.price(fixed.price))} ${unit}`] as const,...common(fixed)]};
  }
  const floating=pair&&side.slot===0?t.firstFloating:t.floating;if(!floating)throw Error('The retained swap terms do not hold a side that its valuation does.');
  const index=indexOf(floating),unit=priceUnit({currency:currency(floating.currency),unit:index.unit,quoteMultiplier:index.quoteMultiplier});
  return {title,rows:[['Index',friendly(floating.index)] as const,['Projection curve',curveName(floating.curve)] as const,['Reset frequency',friendly(floating.resetFrequency)] as const,['Averaging',friendly(floating.averagingMethod)] as const,
   ['Spread',`${floating.spread??'None'} ${unit}`] as const,['Projected prices',floating.customProjection?`Retained custom output · v${floating.customProjection.version}`:'Contractual projection curve'] as const,...common(floating)]};
 });
 return [
  {title:'Trade',rows:[['Trade',`${s.tradeReference} · ${s.instrumentReference}`],['Revision',`${s.revision} · ${friendly(s.kind)}`],['Book',book?`${book.portfolio} · ${book.name}`:'Unavailable book'],...bookingRows(s,zone)]},
  {title:'Structure',rows:[['Type',swapTypeLabels[t.swapType]??friendly(t.swapType)],['Commodity',catalog.commodities.find(c=>c.code===t.commodity)?.name??friendly(t.commodity)],['Quantity',`${minus(formats.quantity(s.quantity))} ${quantityIndex.unit}`],
   ['Discounting',discountingLabels[t.discounting?Discounting.CurrencyDefault:Discounting.None]]]},
  ...sides,
 ];
}
/** What the position panel's Terms and History tabs show of a swap revision: the same read the editor makes, and its schedules when it has them. */
export const swapDetailView=(detail:Detail,zone:string,formats:HoldingFormats,schedules?:(host:HTMLElement)=>()=>void):DetailView=>({terms:swapTerms(detail,zone,formats),history:swapHistory(detail,zone,formats),...(schedules?{schedules}:{})});
export const swapAmendState=(detail:Detail):AmendState=>detail.canAmend?{allowed:true,reason:null}:{allowed:false,reason:detail.amendUnavailableReason??'Read-only revision'};
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
/** The line above a swap's schedules: its present value, per instrument unit and in all, and its quantity, as the caller writes them. */
export function valuationLine(value:Valuation,index:Pick<Priced,'currency'|'unit'>|undefined,text:AmountText){
 return `Trade PV: ${text(value.presentValue,'money')} ${index?.currency??''} · instrument unit PV: ${text(value.instrumentPresentValue,'money')} ${unitOf(index)} · trade quantity: ${text(value.quantity,'quantity')} ${index?.unit??''}. Side values below are per normalized unit.`;
}
/** The line above one side's schedule: its present value per normalized unit and what it contributes. */
export function sideLine(side:Valuation['sides'][number],index:Pick<Priced,'currency'|'unit'>|undefined,text:AmountText){
 return `Side unit PV: ${text(side.presentValue,'money',side.kind)} ${unitOf(index)}; signed contribution: ${text(side.contribution,'money',side.kind)} ${unitOf(index)}.`;
}
/**
 * Draws each side's schedule of a valuation into `host`: its payments in a chart and a table, the observation behind each, the price
 * lookups of each reset and the evidence under them. Returns the widgets that hold charts, for the caller to dispose.
 */
function renderSchedules(host:HTMLElement,value:Valuation,c:ScheduleContext){
 const i=c.index,widgets:ReturnType<typeof mountIntervalDataWidget>[]=[];
 for(const side of value.sides){
  const section=document.createElement('section');section.className='swap-schedule';const title=document.createElement('h3');title.textContent=sideTitle(side,c.pair);
  const sum=document.createElement('p');sum.className='field-help';sum.textContent=sideLine(side,i,c.text);
  const payments=document.createElement('div');payments.className='table-wrap';section.append(title,sum,payments);
  const chart=document.createElement('div');section.insertBefore(chart,payments);host.append(section);
  const widget=mountIntervalDataWidget({table:payments,chart,key:`swap-${side.slot}-payments`,busy:c.busy});widgets.push(widget);
  widget.show(sideScheduleData(side.payments,JSON.stringify(side),unitOf(i),(amount,kind)=>c.text(amount,kind,side.kind)));
  const source=c.sourceOf(side),price=`${source?.currency}/${source?.unit}${source?.quoteMultiplier==='1'?'':' × '+source?.quoteMultiplier}`;
  side.payments.forEach((payment,n)=>{if(payment.observationSchedule)renderSwapObservationInspection(section,payment.observationSchedule,payment.paymentDate,`swap-${side.slot}-${n}`,c.zone,{price,cash:unitOf(i),quantity:source?.unit??''},c.busy);});
  const resets=document.createElement('details'),label=document.createElement('summary'),table=document.createElement('div');label.textContent=`${side.payments.flatMap(p=>p.resets).length} ${side.kind==='fixed'?'fixed price row':'reset rows'} · inspect price lookups`;table.className='table-wrap';resets.append(label,table);section.append(resets);
  renderRegularTable(table,`swap-${side.slot}-resets`,JSON.stringify(side.payments),['Observation start','Observation end · exclusive',`Price · ${price}`,'Weight',`Cash · ${unitOf(i)}`,'State'],side.payments.flatMap(p=>p.resets.map(r=>[r.from,r.to,r.price,r.weight,r.cashContribution,r.fixingState])),[2,3,4],c.busy,{displayRows:side.payments.flatMap(p=>p.resets.map(r=>[r.from,r.to,c.text(r.price,'price',side.kind),c.text(r.weight,'quantity',side.kind),c.text(r.cashContribution,'money',side.kind),r.fixingState]))});
  const evidence=document.createElement('details'),evidenceLabel=document.createElement('summary'),evidenceBody=document.createElement('pre');evidenceLabel.textContent='Exact price lookups, observation lineage and discount inputs';evidenceBody.className='swap-evidence';evidenceBody.textContent=JSON.stringify(side.payments.map(p=>({paymentDate:p.paymentDate,discountMappingId:p.discountMappingId,discountCurve:p.discountCurve,discountPolicy:p.discountPolicy,pillars:p.pillars,resets:p.resets.map(r=>({from:r.from,to:r.to,spread:r.spread,lookup:r.lookup,lineage:r.lineage,fixingState:r.fixingState,fixing:r.fixing}))})),null,2);evidence.append(evidenceLabel,evidenceBody);section.append(evidence);
 }
 return widgets;
}
/**
 * The valuation and schedules of a swap revision as the position panel's read view shows them, in `host` after its terms, in the
 * deal's own display formats. Returns what takes them down.
 */
function renderValuation(host:HTMLElement,detail:Detail,deal:DealFormats,zone:string,busy:()=>boolean):()=>void{
 const terms=detail.state.terms,catalog=detail.catalog,pair=terms.swapType==='financial_floating_floating',index=retainedIndex(catalog,terms,terms.floating);
 const c:ScheduleContext={pair,index,sourceOf:side=>side.kind==='floating'&&pair&&side.slot===0?retainedIndex(catalog,terms,terms.firstFloating!):index,text:deal.text,zone,busy};
 const section=document.createElement('section'),heading=document.createElement('h3'),line=document.createElement('p'),schedules=document.createElement('div');
 section.className='side-panel-section';heading.textContent='Valuation & schedules';line.className='field-help';line.textContent=publicText(valuationLine(detail.valuation,index,c.text));
 section.append(heading,line,schedules);host.append(section);
 const widgets=renderSchedules(schedules,detail.valuation,c);
 return()=>{for(const widget of widgets)widget.dispose();};
}
/**
 * The editor's read for a position of the Blotter, which must hold the instrument that position is in, with the display formats of its
 * deal, which its valuation and schedules are written in.
 */
export async function readSwapView(api:Api,context:Record<string,unknown>,h:Pick<Holding,'tradeId'|'contractId'|'revision'>,formats:HoldingFormats,busy:()=>boolean):Promise<DetailRead>{
 const detail=await readSwapDetail(api,context,h.tradeId,Number(h.revision));
 holdsInstrument(detail,h);
 const deal=dealFormats(api);deal.use(await deal.read(h.tradeId,{...context,convention}));
 await deal.prepare(detail.valuation);for(const side of detail.valuation.sides)await deal.prepare(side,side.kind);
 return {amend:swapAmendState(detail),view:zone=>swapDetailView(detail,zone,formats,host=>renderValuation(host,detail,deal,zone,busy))};
}
type Position=Pick<Holding,'tradeId'|'contractId'|'revision'>;
export function setupSwapEditor(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Record<string,unknown>;queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;onDenied:()=>void;host:()=>HTMLElement;
 /** Asks whether to give up what the panel holds unsaved, when it holds any: false means the person kept it. */
 leave:()=>Promise<boolean>}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const text=(id:string,value:string)=>get(id).textContent=publicText(value);
 // The form is kept on the page's shelf until a host takes it, and goes back there when it is reset.
 const root=get('swap-editing'),shelf=root.parentElement!,edits=trackEdits(get('swap-form'));
 const mounted=()=>root.parentElement!==shelf;
 const mount=()=>{options.host().append(root);root.hidden=false;};
 mountSwapSideEditor('fixed');mountSwapSideEditor('floating');mountFirstFloatingEditor();
 const annotations=tradeAnnotations('swap',options.zone);
 let visibleValuation:Valuation|undefined;
 let failedLiveSelection:{position:Position;revision:number}|undefined,position:Position|undefined;
 const display=dealFormatEditor(get('swap-amount-format'),{api:options.api,act:options.act,busy:options.busy,changed:async()=>{if(visibleValuation){await prepare(visibleValuation);valuation(visibleValuation);}}});
 async function prepare(value:Valuation){await display.prepare(value);for(const side of value.sides)await display.prepare(side,side.kind);}
 let catalog:Catalog|undefined,detail:Detail|undefined,prepared:RequestCommand|undefined,generation=0,previewGeneration=0;
 const dates=createScheduleDatePicker(get('swap-date-tools'),{id:'swap-date-lookup',api:options.api,run:work=>options.act(()=>safe(work)),context:()=>catalog?.context,zone:options.zone,busy:options.busy,enabled:()=>!options.pending()&&!!catalog&&(!detail?!!catalog:detail.canAmend),targets:['fixed','first','floating'].flatMap(side=>['from','to'].map(suffix=>({input:get<HTMLInputElement>(`swap-${side}-${suffix}`),available:()=>!!field('swap-type').value&&activeSides().includes(side),acceptsDate:date=>sideDateChoices(side)[suffix==='from'?'starts':'ends'].includes(date),label:`${side==='fixed'?'Fixed':side==='first'?'First floating':'Floating'} side · delivery ${suffix==='from'?'start':'end (exclusive)'}`}))),edited:()=>edits.mark()});
 const key=()=>JSON.stringify(options.context());
 const context=()=>({...options.context(),convention});
 const indexKey=(i:Pick<Index,'definitionRevisionId'|'curve'|'index'>)=>JSON.stringify([i.definitionRevisionId,i.curve,i.index]);
 const floatingPair=()=>field('swap-type').value==='financial_floating_floating';
 const observationId=(side:string,suffix:string)=>`swap-${side==='first'?'first-':''}${suffix}`;
 const selectedIndex=(side='floating')=>catalog?.indices.find(i=>indexKey(i)===field(observationId(side,'projection')).value);
 const availableIndices=(side='floating')=>filterSwapIndices(catalog?.indices??[],field(observationId(side,'commodity')).value,field(observationId(side,'index-search')).value,side==='first'?selectedIndex()?.definitionRevisionId:undefined);
 const sources=(side='floating')=>availableIndices(side).filter(i=>!field(observationId(side,'index')).value||i.index===field(observationId(side,'index')).value);
 const projectionChoices=(side='floating')=>selectedIndex(side)?.customProjections.filter(p=>p.samplings.some(s=>s.resetFrequency===field(observationId(side,'reset-frequency')).value&&s.averagingMethod===field(observationId(side,'averaging')).value))??[];
 let observationOptions:Record<string,ObservationOptions|undefined>={};
 const samplingRows=(side:string)=>observationOptions[side]?.samplings??selectedIndex(side)?.samplings??sources(side).flatMap(index=>index.samplings);
 const selectedSampling=(side:string)=>samplingRows(side).find(choice=>choice.resetFrequency===field(observationId(side,'reset-frequency')).value&&choice.averagingMethod===field(observationId(side,'averaging')).value)??selectedIndex(side)?.samplings.find(choice=>choice.resetFrequency===field(observationId(side,'reset-frequency')).value&&choice.averagingMethod===field(observationId(side,'averaging')).value);
 const observations:Record<string,ReturnType<typeof setupSwapObservationControls>>={};
 for(const side of ['floating','first'])observations[side]=setupSwapObservationControls({prefix:side==='first'?'swap-first':'swap',enabled:()=>!!field('swap-type').value&&(side==='floating'||floatingPair()),api:options.api,busy:options.busy,choice:()=>selectedSampling(side),unit:()=>selectedIndex(side)?.unit??'',
  request:()=>{const i=selectedIndex(side),definition=side==='first'?selectedIndex()??i:i,custom=projectionChoices(side).find(choice=>JSON.stringify(choice.reference)===field(observationId(side,'price-source')).value);return {context:{...context(),knowledgeMode:'retained',contextId:catalog?.context.contextId},
   ...(definition?{definitionRevisionId:definition.definitionRevisionId}:{}),...(i?{curve:i.curve}:{}),commodity:field(observationId(side,'commodity')).value||undefined,index:field(observationId(side,'index')).value||undefined,
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
 function dateReady(side:string){const source=side==='fixed'?'floating':side,mode=selectedSampling(source);return !!selectedIndex(source)&&!!mode&&(!mode.requiresObservationCalendar||!!observations[source].calendar())&&!!observationOptions[source];}
 const deliveryPickers=['fixed','first','floating'].flatMap(side=>['from','to'].map(suffix=>{
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
  for(const side of ['fixed','first','floating']){const on=!!type&&(side==='floating'||side===(floatingPair()?'first':'fixed'));get(`swap-${side}-editor`).hidden=!on;get(`swap-${side}-editor`).querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select').forEach(n=>n.disabled=!on);if(on)paymentControls(side);}
  text('swap-floating-title',floatingPair()?'Second floating side':'Floating side');get('swap-side-compatibility').hidden=!floatingPair();direction();
 }
 function applyPar(result:{resolvedFixedPrice?:string;parRate?:{decimalPlaces:number}|null}){
  if(result.resolvedFixedPrice!==undefined&&result.parRate){field('swap-price').value=result.resolvedFixedPrice;text('swap-par-result',`Par fixed rate: ${result.resolvedFixedPrice} · ${result.parRate.decimalPlaces} decimal places, ties to even. Review this fixed rate before booking.`);}
 }
 async function floatingDraft(side='floating'){
  const i=selectedIndex(side);if(!i)throw Error('Choose each exact floating index and projection curve.');
  const customProjection=projectionReference(field(observationId(side,'price-source')).value,projectionChoices(side));
  const observationSchedule=observations[side].read();
  return{curve:i.curve,index:i.index,currency:field(`swap-${side}-currency`).value,deliveryFrom:field(`swap-${side}-from`).value,deliveryTo:field(`swap-${side}-to`).value,paymentSchedule:paymentInput(side),...(customProjection?{customProjection}:{}),...(observationSchedule?{observationSchedule}:{}),resetFrequency:field(observationId(side,'reset-frequency')).value,averagingMethod:field(observationId(side,'averaging')).value,spread:field(observationId(side,'spread')).value};
 }
 async function instrumentDraft(){
  const i=selectedIndex();if(!i)throw Error('Choose the exact index and projection curve.');
  if(!field('swap-type').value)throw Error('Choose a swap type.');
  for(const side of activeSides()){const choices=sideDateChoices(side);if(!choices.starts.includes(field(`swap-${side}-from`).value)||!choices.ends.includes(field(`swap-${side}-to`).value))throw Error('Choose an available delivery start and end for each side.');}
  if(!floatingPair()&&!field('swap-price').value.trim()&&(!field('swap-par-places').value||!Number.isInteger(Number(field('swap-par-places').value))||Number(field('swap-par-places').value)<0||Number(field('swap-par-places').value)>18))throw Error('Choose 0–18 decimal places for the par rate.');
  if(!Object.values(Discounting).includes(field('swap-discounting').value as Discounting))throw Error('Choose discounting explicitly.');
  if(!field('swap-quantity-unit').value)throw Error('Choose an instrument quantity unit.');
  if(floatingPair()&&selectedIndex('first')?.definitionRevisionId!==i.definitionRevisionId)throw Error('Both floating sides must use the same exact market definition revision.');
  return{context:context(),definitionRevisionId:i.definitionRevisionId,commodity:field('swap-commodity').value,swapType:field('swap-type').value,
   quantity:{value:field('swap-quantity').value,specification:JSON.parse(field('swap-quantity-unit').value)},fixedCoefficient:floatingPair()?null:field('swap-direction').value,firstCoefficient:floatingPair()?field('swap-first-direction').value:null,discounting:field('swap-discounting').value===Discounting.CurrencyDefault,
   fixed:floatingPair()?null:{price:field('swap-price').value,currency:field('swap-fixed-currency').value,deliveryFrom:field('swap-fixed-from').value,deliveryTo:field('swap-fixed-to').value,paymentSchedule:paymentInput('fixed')},
   firstFloating:floatingPair()?await floatingDraft('first'):null,floating:await floatingDraft(),
   parRatePolicy:!floatingPair()&&!field('swap-price').value.trim()?{implementation:catalog!.parRateOptions.implementation,decimalPlaces:Number(field('swap-par-places').value),tieRule:catalog!.parRateOptions.tieRule}:null};
 }
 const draft=draftValuation(get('swaps-draft-valuation'),{product:'swaps',api:options.api,act:options.act,busy:options.busy,key,denied:options.onDenied,draft:instrumentDraft,priced:async result=>applyPar(result)});
 const scheduleWidgets:ReturnType<typeof mountIntervalDataWidget>[]=[];
 function clearSchedules(){for(const widget of scheduleWidgets)widget.dispose();scheduleWidgets.length=0;get('swap-schedules').replaceChildren();}
 function invalidate(){text('swap-par-result','');draft.reset();previewGeneration++;prepared=undefined;get('swap-result').hidden=true;get('swap-confirmation').hidden=true;get<HTMLInputElement>('swap-confirm').checked=false;sync();}
 function sync(){typeControls();syncDeliveryDates();Object.values(observations).forEach(controls=>controls.sync());field('swap-type').disabled=options.busy()||!!detail;draft.sync();display.sync();dates.sync();get<HTMLFieldSetElement>('swap-fields').disabled=options.busy()||!!options.pending()||(!detail? !catalog:!detail.canAmend);get<HTMLButtonElement>('swap-preview').disabled=options.busy()||(!detail?!catalog?.canBook:!detail.canAmend);get<HTMLButtonElement>('swap-submit').disabled=options.busy()||!!options.pending()||!prepared||!get<HTMLInputElement>('swap-confirm').checked;field('swap-revision').disabled=options.busy()||!!options.pending();get<HTMLButtonElement>('swap-latest').disabled=options.busy()||!!options.pending();}
 function reset(){text('swap-template-note','Choose a structure to set up its sides.');observationOptions={};Object.values(observations).forEach(controls=>controls.reset());failedLiveSelection=undefined;display.reset();visibleValuation=undefined;annotations.reset();generation++;shelf.append(root);root.hidden=true;position=undefined;detail=undefined;catalog=undefined;dates.reset();invalidate();get<HTMLFormElement>('swap-form').reset();edits.clear();for(const id of ['swap-book','swap-commodity','swap-first-commodity','swap-index','swap-projection','swap-reset-frequency','swap-averaging','swap-fixed-currency','swap-floating-currency','swap-quantity-unit','swap-fixed-payment-calendar','swap-floating-payment-calendar','swap-revision','swap-first-index','swap-first-projection','swap-first-reset-frequency','swap-first-averaging','swap-first-currency','swap-first-payment-calendar','swap-price-source','swap-first-price-source'])field(id).replaceChildren();for(const id of ['swap-reference','swap-mode','swap-unit','swap-price-unit','swap-coverage','swap-projection-note','swap-first-projection-note','swap-legacy-fixing','swap-first-legacy-fixing','swap-pv','swap-effective','swap-error'])text(id,'');get('swap-error').hidden=true;for(const side of ['floating','first'])get(observationId(side,'legacy-fixing')).hidden=true;clearSchedules();get('swap-revisions').hidden=true;get('swap-amendment').hidden=true;for(const side of ['fixed','first','floating'])paymentControls(side);typeControls();}
 async function safe(work:()=>Promise<void>){const started=generation;try{get('swap-error').hidden=true;await work();}catch(error){if(started!==generation)return;text('swap-error',error instanceof Error?error.message:'The financial swap operation failed.');get('swap-error').hidden=false;if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}throw error;}}
 function choose(id:string,rows:{value:string;label:string}[],value='',retained?:string,linked=false){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(publicText(r.label),r.value)));if(value&&retained&&!rows.some(row=>row.value===value)){const option=new Option(publicText(retained+' · retained, unavailable for new pricing'),value);option.disabled=true;select.append(option);}select.value=[...select.options].some(row=>row.value===value)&&value?value:linked?linkedValue(rows.map(row=>row.value)):'';}
 function restoreSelection(id:string,value:string,label:string){
  const select=get<HTMLSelectElement>(id);
  if(![...select.options].some(option=>option.value===value)){const option=new Option(publicText(label+' · retained, unavailable for new pricing'),value);option.disabled=true;select.append(option);}
  select.value=value;
 }
 function direction(){const value=field(floatingPair()?'swap-first-direction':'swap-direction').value;text('swap-floating-direction',value==='-1'?'Receive floating':value==='1'?'Pay floating':'Choose the first-side direction.');}
 function samplingControls(side='floating'){const frequency=field(observationId(side,'reset-frequency')).value,rows=samplingRows(side).filter(s=>!frequency||s.resetFrequency===frequency),unique=[...new Map(rows.map(row=>[row.averagingMethod,row])).values()],value=field(observationId(side,'averaging')).value;choose(observationId(side,'averaging'),unique.map(s=>({value:s.averagingMethod,label:s.averagingMethod==='none'?'No averaging · one index price':s.label})),value,retainedFloating(side)?.averagingMethod===value?friendly(value):undefined);}
 function priceSourceControls(side='floating',retained?:CustomProjection|null){
  const choices=projectionChoices(side),value=retained===null?'ordinary':retained?JSON.stringify(retained):field(observationId(side,'price-source')).value;
  choose(observationId(side,'price-source'),selectedIndex(side)?[{value:'ordinary',label:'Contractual projection curve'},...choices.map(p=>({value:JSON.stringify(p.reference),label:p.label}))]:[],value);
  if(retained&&!choices.some(p=>JSON.stringify(p.reference)===value)){
   const unavailable=new Option(`Retained custom output · v${retained.version} · unavailable in this context`,value);unavailable.disabled=true;
   get<HTMLSelectElement>(observationId(side,'price-source')).append(unavailable);field(observationId(side,'price-source')).value=value;
  }
  const selected=choices.find(p=>JSON.stringify(p.reference)===field(observationId(side,'price-source')).value);
  text(observationId(side,'projection-note'),selected?`${selected.label}. This exact published output supplies projected prices; actual fixings remain index observations.`:field(observationId(side,'price-source')).value==='ordinary'?'Projected prices come from the selected contractual curve.':field(observationId(side,'price-source')).value?'This retained projection is unavailable. Select an offered source before repricing.':'Choose a projected price source. Published outputs are offered only for the selected index and sampling.');
 }
 function quantityUnitLabel(){text('swap-unit',get<HTMLSelectElement>('swap-quantity-unit').selectedOptions[0]?.textContent??'choose quantity unit');}
 function projectionControls(side='floating'){
  const candidates=sources(side).filter(i=>observationOptions[side]?observationOptions[side]!.indices.some(candidate=>indexKey(candidate)===indexKey(i)):i.samplings.some(s=>(!field(observationId(side,'reset-frequency')).value||s.resetFrequency===field(observationId(side,'reset-frequency')).value)&&(!field(observationId(side,'averaging')).value||s.averagingMethod===field(observationId(side,'averaging')).value)));
  const projection=field(observationId(side,'projection')).value,retained=retainedFloating(side);
  choose(observationId(side,'projection'),candidates.map(i=>({value:indexKey(i),label:curveName(i.curve)})),projection,retained&&projection===JSON.stringify([detail!.state.terms.definitionRevisionId,retained.curve,retained.index])?curveName(retained.curve):undefined);
  const frequency=field(observationId(side,'reset-frequency')).value;choose(observationId(side,'reset-frequency'),[...new Set(samplingRows(side).map(s=>s.resetFrequency))].map(value=>({value,label:friendly(value)})),frequency,retained?.resetFrequency===frequency?friendly(frequency):undefined);
  const i=selectedIndex(side);samplingControls(side);priceSourceControls(side);
  text(observationId(side,'fixing-convention'),!i?'Choose an index and projection curve to inspect its fixing convention.':i.fixingConvention==='published_index_observations_v1'?'Fixings follow the selected index’s published observation schedule. Pricing validates the selected dates; inspect exact fixing sources and instants with each valued reset.':i.fixingUnavailableReason??'This index has no qualified fixing schedule.');text(observationId(side,'spread-unit'),i?`${i.currency}/${i.unit}${i.quoteMultiplier==='1'?'':' × '+i.quoteMultiplier}`:'choose projection curve');
  if(side==='floating'){
   const units=i?.quantityChoices??[],old=field('swap-quantity-unit').value;if(old&&!units.some(u=>JSON.stringify(u.specification)===old))field('swap-quantity').value='';choose('swap-quantity-unit',units.map(u=>({value:JSON.stringify(u.specification),label:u.unit})),quantityChoice(units,i?.quantityCode??'',old));quantityUnitLabel();text('swap-price-unit',i?`${i.currency}/${i.unit}${i.quoteMultiplier==='1'?'':' × '+i.quoteMultiplier}`:'choose projection curve');
   text('swap-coverage',i?`${i.currency}/${i.unit} × ${i.quoteMultiplier}. Projection curve: ${curveName(i.curve)}. Available delivery periods: ${i.contracts.map(c=>deliveryLabel(c.deliveryFrom,c.deliveryTo)).join(', ')}. Default discount curve: ${i.defaultDiscountCurve?curveName(i.defaultDiscountCurve):'not configured'}.`:'Choose the floating index, reset frequency and exact projection curve.');
  }
  const currencies=[...new Map((i?[i]:sources(side)).map(i=>[i.currencyCode,{value:i.currencyCode,label:i.currency}])).values()];
  for(const slot of side==='first'?['first']:['fixed','floating'])choose(`swap-${slot}-currency`,currencies,field(`swap-${slot}-currency`).value,undefined,true);
 }
 function indexControls(side='floating'){
  observationOptions[side]=undefined;choose(observationId(side,'reset-frequency'),[...new Set(sources(side).flatMap(i=>i.samplings.map(s=>s.resetFrequency)))].map(value=>({value,label:friendly(value)})),field(observationId(side,'reset-frequency')).value);projectionControls(side);
 }
 function indexChoices(side:string,linked=false){
  const indices=availableIndices(side).filter(index=>!observationOptions[side]||observationOptions[side]!.indices.some(candidate=>indexKey(candidate)===indexKey(index)));
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
 choose('swap-book',catalog.books.map(b=>({value:b.id,label:`${b.portfolio} · ${b.name}`})));for(const side of ['floating','first'])choose(observationId(side,'commodity'),catalog.commodities.filter(c=>catalog!.indices.some(i=>i.commodity===c.code)).map(c=>({value:c.code,label:c.name})));commodityControls();for(const side of ['fixed','first','floating'])choose(`swap-${side}-payment-calendar`,catalog.paymentCalendars.map(c=>({value:c.code,label:friendly(c.code)})));}
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
 function renderDetail(value:Detail){detail=value;catalog={...value.catalog,context:value.context,asOf:value.asOf};dates.invalidate();populateCatalog();const state=value.state,t=state.terms;field('swap-type').value=t.swapType;typeControls();for(const side of ['floating','first'])field(observationId(side,'commodity')).value=t.commodity;commodityControls();text('swap-template-note','Retained trade terms. Each side shows its saved values.');
  field('swap-book').value=t.bookId;field('swap-quantity').value=t.quantity;field(floatingPair()?'swap-first-direction':'swap-direction').value=t.firstCoefficient??t.fixedCoefficient;
  for(const side of ['floating',...(floatingPair()?['first']:[])]){
   const terms=side==='first'?t.firstFloating!:t.floating;
   observations[side].restore(terms.observationSchedule);
   const i=retainedIndex(catalog,t,terms);
   restoreSelection(observationId(side,'index'),i.index,friendly(i.index));indexControls(side);restoreSelection(observationId(side,'reset-frequency'),terms.resetFrequency,friendly(terms.resetFrequency));restoreSelection(observationId(side,'projection'),indexKey(i),curveName(i.curve));restoreSelection(observationId(side,'averaging'),terms.averagingMethod,friendly(terms.averagingMethod));projectionControls(side);field(observationId(side,'spread')).value=terms.spread??'0';priceSourceControls(side,terms.customProjection??null);text(observationId(side,'legacy-fixing'),terms.firstFixingAt?`Retained legacy first fixing boundary: ${displayInstant(terms.firstFixingAt,options.zone())}. New pricing uses the selected index convention.`:'');get(observationId(side,'legacy-fixing')).hidden=!terms.firstFixingAt;
  }
  if(t.fixed)field('swap-price').value=t.fixed.price;
  for(const side of activeSides()){const terms=side==='first'?t.firstFloating!:side==='fixed'?t.fixed!:t.floating;field(`swap-${side}-currency`).value=terms.currency;field(`swap-${side}-from`).value=terms.deliveryFrom;field(`swap-${side}-to`).value=terms.deliveryTo;setPayment(side,terms.paymentSchedule);}
  field('swap-discounting').value=t.discounting?Discounting.CurrencyDefault:Discounting.None;field('swap-reason').value='';direction();
  text('swap-reference',`${state.tradeReference} · ${state.instrumentReference} · Revision ${state.revision}`);text('swap-mode',value.canAmend?'Latest revision · amendment appends a retained event':value.amendUnavailableReason??'Read-only revision');text('swap-effective',`Amendment effective: ${displayInstant(value.asOf,options.zone())}. Prior revisions remain unchanged.`);
  choose('swap-revision',value.history.map(s=>({value:String(s.revision),label:`Revision ${s.revision} · ${s.kind}`})),String(state.revision));get('swap-revisions').hidden=false;get('swap-amendment').hidden=false;get<HTMLInputElement>('swap-reason').required=value.canAmend;
  prepared=undefined;get('swap-confirmation').hidden=true;valuation(value.valuation);sync();
 }
 async function openNew(){reset();const started=generation,source=key();await safe(async()=>{const response=await options.api<Catalog>('swaps/catalog',context());if(started!==generation||source!==key())return;catalog=response;populateCatalog();await refreshObservations();if(started!==generation||source!==key())return;text('swap-mode','Choose a swap type and enter each independent side.');typeControls();get<HTMLInputElement>('swap-reason').required=false;sync();mount();});}
 /** Opens a position's swap at one of its revisions, the latest for none, in the host. Its own revision must hold the position's instrument; an earlier one is read as it is. */
 async function open(at:Position,revision:number|null){reset();position=at;const started=++generation,source=key();invalidate();await safe(async()=>{const read=await readSwapDetail(options.api,options.context(),at.tradeId,revision);if(started!==generation||source!==key())return;const response=swapAtPosition(read,at,revision);await display.load(response.state.tradeId,context(),response.canAmend,response.state.terms.firstFloating?['trade','floating']:['trade','fixed','floating']);await prepare(response.valuation);if(started!==generation||source!==key())return;renderDetail(response);await refreshObservations();if(started!==generation||source!==key())return;mount();});}
 field('swap-quantity-unit').onchange=quantityUnitLabel;
 get('swap-type-choices').querySelectorAll<HTMLButtonElement>('button').forEach(button=>button.onclick=()=>{field('swap-type').value=button.dataset.swapType!;field('swap-type').dispatchEvent(new Event('change',{bubbles:true}));});
 field('swap-type').onchange=()=>{invalidate();typeControls();applyEntryTemplate();dates.sync();};
 for(const side of ['floating','first'])field(observationId(side,'commodity')).onchange=()=>{invalidate();if(side==='floating')field('swap-price').value='';const spread=field(observationId(side,'spread'));if(spread.value!==catalog?.entryTemplate.spread)spread.value='';commodityControls(side);};
 for(const side of ['first','floating']){
  field(observationId(side,'index-search')).oninput=()=>{observationOptions[side]=undefined;invalidate();indexChoices(side);indexControls(side);};
  field(observationId(side,'averaging')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};field(observationId(side,'price-source')).onchange=()=>{invalidate();priceSourceControls(side);};
  field(observationId(side,'index')).onchange=()=>{invalidate();indexControls(side);};field(observationId(side,'reset-frequency')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};field(observationId(side,'projection')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};
 }
 for(const side of ['fixed','first','floating'])for(const id of ['payment-rule','day-basis','payment-adjustment'])field(`swap-${side}-${id}`).addEventListener('change',()=>paymentControls(side));field('swap-direction').onchange=direction;field('swap-first-direction').onchange=direction;
 for(const suffix of ['from','to','currency'])for(const side of ['fixed','first','floating'])field(`swap-${side}-${suffix}`).addEventListener('change',()=>{for(const otherSide of activeSides().filter(s=>s!==side)){const other=field(`swap-${otherSide}-${suffix}`);if(!other.value)other.value=field(`swap-${side}-${suffix}`).value;}syncDeliveryDates(true);});
 get<HTMLFormElement>('swap-form').addEventListener('change',event=>{const id=(event.target as HTMLElement).id;if(['swap-type','swap-commodity','swap-first-commodity','swap-fixed-from','swap-fixed-to',...['first','floating'].flatMap(side=>['index','index-search','projection','reset-frequency','averaging','price-source','observation-calendar'].map(suffix=>observationId(side,suffix)).concat([`swap-${side}-from`,`swap-${side}-to`]))].includes(id)){void options.act(()=>safe(refreshObservations));}});
 get<HTMLFormElement>('swap-form').addEventListener('input',event=>{invalidate();if(!(event.target as Element).closest('.schedule-date-picker'))dates.invalidate();});
 get<HTMLFormElement>('swap-form').onsubmit=event=>{event.preventDefault();void options.act(()=>safe(async()=>{
  const i=selectedIndex();if(!i||!catalog)throw Error('Choose a supported exact index and curve.');if(detail&&!detail.canAmend)throw Error('Only the latest eligible trade revision can be amended.');
  const started=generation,preview=previewGeneration,source=key();const instrument=await instrumentDraft();const priced=await options.api<{quantity:{amount:string};resolvedFixedPrice?:string;parRate?:{decimalPlaces:number}|null}>('swaps/price',instrument);if(!priced.quantity)throw Error('The instrument quantity conversion is incomplete.');if(started!==generation||preview!==previewGeneration||source!==key())return;
  applyPar(priced);
  const {context:unused,quantity:entered,parRatePolicy,...terms}=instrument;
  const body={...context(),...annotations.read(),...terms,knowledgeMode:'current',bookId:field('swap-book').value,quantity:priced.quantity.amount,fixed:instrument.fixed?{...instrument.fixed,price:priced.resolvedFixedPrice??instrument.fixed.price}:null,
   ...(detail?{tradeId:detail.state.tradeId,expectedRevision:detail.state.revision,effectiveAt:detail.asOf,reason:field('swap-reason').value}:{})};
  if(!Object.values(Discounting).includes(field('swap-discounting').value as Discounting))throw Error('Choose discounting explicitly.');const command:RequestCommand={operationId:crypto.randomUUID(),path:detail?'swaps/amend':'swaps/book',label:detail?`Swap amendment of ${detail.state.tradeReference}`:'New swap',body};
  const response=await options.api<{valuation:Valuation}>('swaps/preview',{...body,operationId:command.operationId});if(started!==generation||preview!==previewGeneration||source!==key())return;
  await prepare(response.valuation);if(started!==generation||preview!==previewGeneration||source!==key())return;prepared=command;valuation(response.valuation);get('swap-confirmation').hidden=false;get<HTMLInputElement>('swap-confirm').checked=false;text('swap-submit',detail?'Submit amendment':'Book financial swap');sync();
 }));};
 get<HTMLInputElement>('swap-confirm').onchange=sync;
 get('swap-submit').onclick=()=>void options.act(()=>safe(async()=>{if(!prepared||!get<HTMLInputElement>('swap-confirm').checked)throw Error('Validate schedules and confirm the exact terms first.');await options.queue(prepared);}));
 // Another revision replaces what the form holds, which is asked about first, before the page is busy, as every other way out of it is.
 function replacing(revision:()=>number|null){
  if(!position||options.busy())return;
  const at=position;
  void (async()=>{if(await options.leave())await options.act(()=>open(at,revision()));else field('swap-revision').value=String(detail?.state.revision??'');})();
 }
 field('swap-revision').onchange=()=>replacing(()=>Number(field('swap-revision').value));get('swap-latest').onclick=()=>replacing(()=>null);
 return{open,openNew,reset,sync,async liveRefresh(){const requested=failedLiveSelection??(mounted()&&detail&&position?{position,revision:Number(field('swap-revision').value)}:undefined);if(requested){try{await open(requested.position,requested.revision);}catch(error){if((error as {code?:string}).code!=='access_denied')failedLiveSelection=requested;throw error;}}else if(mounted()){catalog=await options.api<Catalog>('swaps/catalog',context());await refreshObservations();previewGeneration++;prepared=undefined;get('swap-result').hidden=true;get('swap-confirmation').hidden=true;dates.invalidate();dates.sync();await draft.liveRefresh();sync();}},refresh(){annotations.refresh();dates.invalidate();dates.sync();invalidate();},
  // The position whose amendment form is in the host (none for a new swap), and booking or amendment edits not yet sent.
  shown:()=>mounted()?position:undefined,
  displayedRevision:()=>mounted()?detail?.state.revision:undefined,
  unsaved:()=>mounted()&&edits.edited()};
}
