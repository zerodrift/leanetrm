import {draftValuation} from './draft-valuation.js';
import {mountSwapSideEditor,mountFirstFloatingEditor} from './swap-side-editor.js';
import {dealFormatEditor} from './deal-format-editor.js';
import {amountText} from './amount-display.js';
import {tradeAnnotations,type PartyCatalog,type Trader,type Annotation} from './trade-annotations.js';
import {createScheduleDatePicker} from './schedule-date-picker.js';
import type {Context} from './date-editor.js';
import {PaymentDateRule,PaymentDayBasis,DateAdjustment,Discounting,type PaymentInput,type PaymentOptions} from './swap-contract.js';
import {trackEdits} from './edit-tracking.js';
import {projectionReference,type CustomProjection,type ProjectionChoice} from './swap-projections.js';
import {publicText,displayInstant,deliveryLabel,curveName,friendly} from './market-view.js';
import {renderRegularTable} from './regular-table.js';
import type {RequestCommand} from './pending-command.js';
import {renderSwapObservationInspection,setupSwapObservationControls} from './swap-observations-view.js';
import type {SamplingChoice,ObservationInspection,ObservationOptions,ObservationSchedule} from './swap-observations.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Index={definitionRevisionId:string;curve:string;index:string;commodity:string;currencyCode:string;currency:string;unit:string;quoteMultiplier:string;quantityChoices:{specification:unknown;unit:string}[];defaultDiscountCurve:string|null;fixingConvention:'published_index_observations_v1'|null;fixingUnavailableReason:string|null;customProjections:ProjectionChoice[];averaging:string[];samplings:SamplingChoice[];contracts:{deliveryFrom:string;deliveryTo:string}[]};
type Catalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string}[];indices:Index[];commodities:{code:string;name:string}[];paymentCalendars:{code:string}[];paymentOptions:PaymentOptions;entryTemplate:{version:string;firstCoefficient:string;spread:string;description:string;payment:PaymentInput};parRateOptions:{implementation:string;initialDecimalPlaces:number;tieRule:string};asOf:string;context:Context;canBook?:boolean};
type FloatingTerms={curve:string;index:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null;firstFixingAt?:string|null;customProjection?:CustomProjection|null;observationSchedule?:ObservationSchedule;resetFrequency:string;averagingMethod:string;spread:string};
type Terms={swapType:string;firstCoefficient:string|null;firstFloating:FloatingTerms|null;commodity:string;definitionRevisionId:string;bookId:string;executedAt:string;quantity:string;fixedCoefficient:string;discounting:boolean;fixed:{price:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null}|null;floating:FloatingTerms};
type State=Annotation&{tradeId:string;tradeReference:string;instrumentReference:string;revision:number;quantity:string;kind:string;reason:string;effectiveAt:string;recordedAt:string;terms:Terms};
type Valuation={presentValue:string;instrumentPresentValue:string;quantity:string;sides:{slot:number;kind:string;coefficient:string;presentValue:string;contribution:string;payments:{deliveryFrom:string;deliveryTo:string;paymentDate:string;cashAmount:string;discountFactor:string;presentValue:string;discountMappingId:string|null;discountCurve:unknown;discountPolicy:string;pillars:unknown;observationSchedule:ObservationInspection|null;resets:{spread:string|null;lookup:unknown;lineage:unknown;from:string;to:string;price:string;weight:string;cashContribution:string;fixingState:string;fixing?:unknown}[]}[]}[]};
type Detail={state:State;history:State[];catalog:Catalog;valuation:Valuation;canAmend:boolean;amendUnavailableReason:string|null;asOf:string;context:Context};
const convention='independent_side_financial_swap_v2';
export function setupSwapEditor(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Record<string,unknown>;queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;onDenied:()=>void}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const text=(id:string,value:string)=>get(id).textContent=publicText(value);
 const dialog=get<HTMLDialogElement>('swap-editor'),edits=trackEdits(get('swap-form'));
 mountSwapSideEditor('fixed');mountSwapSideEditor('floating');mountFirstFloatingEditor();
 const annotations=tradeAnnotations('swap',options.zone);
 let visibleValuation:Valuation|undefined;
 let failedLiveSelection:{tradeId:string;revision:number}|undefined;
 const display=dealFormatEditor(get('swap-amount-format'),{api:options.api,act:options.act,busy:options.busy,changed:async()=>{if(visibleValuation){await prepare(visibleValuation);valuation(visibleValuation);}}});
 async function prepare(value:Valuation){await display.prepare(value);for(const side of value.sides)await display.prepare(side,side.kind);}
 let catalog:Catalog|undefined,detail:Detail|undefined,prepared:RequestCommand|undefined,generation=0,previewGeneration=0;
 const dates=createScheduleDatePicker(get('swap-date-tools'),{id:'swap-date-lookup',api:options.api,run:work=>options.act(()=>safe(work)),context:()=>catalog?.context,zone:options.zone,busy:options.busy,enabled:()=>!options.pending()&&!!catalog&&(!detail?!!catalog:detail.canAmend),targets:['fixed','first','floating'].flatMap(side=>['from','to'].map(suffix=>({input:get<HTMLInputElement>(`swap-${side}-${suffix}`),available:()=>!!field('swap-type').value&&activeSides().includes(side),label:`${side==='fixed'?'Fixed':side==='first'?'First floating':'Floating'} side · delivery ${suffix==='from'?'start':'end (exclusive)'}`})))});
 const key=()=>JSON.stringify(options.context());
 const context=()=>({...options.context(),convention});
 const indexKey=(i:Pick<Index,'definitionRevisionId'|'curve'|'index'>)=>JSON.stringify([i.definitionRevisionId,i.curve,i.index]);
 const floatingPair=()=>field('swap-type').value==='financial_floating_floating';
 const observationId=(side:string,suffix:string)=>`swap-${side==='first'?'first-':''}${suffix}`;
 const selectedIndex=(side='floating')=>catalog?.indices.find(i=>indexKey(i)===field(observationId(side,'projection')).value);
 const sources=(side='floating')=>catalog?.indices.filter(i=>i.commodity===field(observationId(side,'commodity')).value&&(!field(observationId(side,'index')).value||i.index===field(observationId(side,'index')).value)&&(side!=='first'||!selectedIndex()||i.definitionRevisionId===selectedIndex()!.definitionRevisionId))??[];
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
  apply:response=>{if((response.context as Context)?.contextId!==catalog?.context.contextId)throw Error('The observation choices belong to a different market context. Reload the editor.');observationOptions[side]=response;indexChoices(side);projectionControls(side);},
 });
 async function refreshObservations(){const started=generation,source=key();await observations.floating.refresh();if(started!==generation||source!==key())return;if(floatingPair())await observations.first.refresh();}
 const retainedFloating=(side:string)=>detail?.state.terms.commodity===field(observationId(side,'commodity')).value?(side==='first'?detail.state.terms.firstFloating:detail.state.terms.floating):undefined;
 const activeSides=()=>[floatingPair()?'first':'fixed','floating'];
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
 function invalidate(){text('swap-par-result','');draft.reset();previewGeneration++;prepared=undefined;get('swap-result').hidden=true;get('swap-confirmation').hidden=true;get<HTMLInputElement>('swap-confirm').checked=false;sync();}
 function sync(){typeControls();Object.values(observations).forEach(controls=>controls.sync());field('swap-type').disabled=options.busy()||!!detail;draft.sync();display.sync();dates.sync();get<HTMLFieldSetElement>('swap-fields').disabled=options.busy()||!!options.pending()||(!detail? !catalog:!detail.canAmend);get<HTMLButtonElement>('swap-preview').disabled=options.busy()||(!detail?!catalog?.canBook:!detail.canAmend);get<HTMLButtonElement>('swap-submit').disabled=options.busy()||!!options.pending()||!prepared||!get<HTMLInputElement>('swap-confirm').checked;field('swap-revision').disabled=options.busy()||!!options.pending();get<HTMLButtonElement>('swap-latest').disabled=options.busy()||!!options.pending();}
 function reset(){text('swap-template-note','Choose a structure to set up its sides.');observationOptions={};Object.values(observations).forEach(controls=>controls.reset());failedLiveSelection=undefined;display.reset();visibleValuation=undefined;annotations.reset();generation++;if(dialog.open)dialog.close();detail=undefined;catalog=undefined;dates.reset();invalidate();get<HTMLFormElement>('swap-form').reset();edits.clear();for(const id of ['swap-book','swap-commodity','swap-first-commodity','swap-index','swap-projection','swap-reset-frequency','swap-averaging','swap-fixed-currency','swap-floating-currency','swap-quantity-unit','swap-fixed-payment-calendar','swap-floating-payment-calendar','swap-revision','swap-first-index','swap-first-projection','swap-first-reset-frequency','swap-first-averaging','swap-first-currency','swap-first-payment-calendar','swap-price-source','swap-first-price-source'])field(id).replaceChildren();for(const id of ['swap-reference','swap-mode','swap-unit','swap-price-unit','swap-coverage','swap-projection-note','swap-first-projection-note','swap-legacy-fixing','swap-first-legacy-fixing','swap-pv','swap-effective','swap-error'])text(id,'');get('swap-error').hidden=true;for(const side of ['floating','first'])get(observationId(side,'legacy-fixing')).hidden=true;get('swap-schedules').replaceChildren();get('swap-history').replaceChildren();get('swap-revisions').hidden=true;get('swap-history-panel').hidden=true;get('swap-amendment').hidden=true;for(const side of ['fixed','first','floating'])paymentControls(side);typeControls();}
 async function safe(work:()=>Promise<void>){const started=generation;try{get('swap-error').hidden=true;await work();}catch(error){if(started!==generation)return;text('swap-error',error instanceof Error?error.message:'The financial swap operation failed.');get('swap-error').hidden=false;if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}throw error;}}
 function choose(id:string,rows:{value:string;label:string}[],value='',retained?:string){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(publicText(r.label),r.value)));if(value&&retained&&!rows.some(row=>row.value===value)){const option=new Option(publicText(retained+' · retained, unavailable for new pricing'),value);option.disabled=true;select.append(option);}select.value=[...select.options].some(row=>row.value===value)?value:'';}
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
   const units=i?.quantityChoices??[],old=field('swap-quantity-unit').value;if(old&&!units.some(u=>JSON.stringify(u.specification)===old))field('swap-quantity').value='';choose('swap-quantity-unit',units.map(u=>({value:JSON.stringify(u.specification),label:u.unit})),units.some(u=>JSON.stringify(u.specification)===old)?old:units[0]?JSON.stringify(units[0].specification):'');quantityUnitLabel();text('swap-price-unit',i?`${i.currency}/${i.unit}${i.quoteMultiplier==='1'?'':' × '+i.quoteMultiplier}`:'choose projection curve');
   text('swap-coverage',i?`${i.currency}/${i.unit} × ${i.quoteMultiplier}. Projection curve: ${curveName(i.curve)}. Available delivery periods: ${i.contracts.map(c=>deliveryLabel(c.deliveryFrom,c.deliveryTo)).join(', ')}. Default discount curve: ${i.defaultDiscountCurve?curveName(i.defaultDiscountCurve):'not configured'}.`:'Choose the floating index, reset frequency and exact projection curve.');
  }
  const currencies=[...new Map(sources(side).map(i=>[i.currencyCode,{value:i.currencyCode,label:i.currency}])).values()];
  for(const slot of side==='first'?['first']:['fixed','floating'])choose(`swap-${slot}-currency`,currencies,field(`swap-${slot}-currency`).value);
 }
 function indexControls(side='floating'){
  observationOptions[side]=undefined;choose(observationId(side,'reset-frequency'),[...new Set(sources(side).flatMap(i=>i.samplings.map(s=>s.resetFrequency)))].map(value=>({value,label:friendly(value)})),field(observationId(side,'reset-frequency')).value);projectionControls(side);
 }
 function indexChoices(side:string){
  const indices=catalog?.indices.filter(index=>index.commodity===field(observationId(side,'commodity')).value&&(!observationOptions[side]||observationOptions[side]!.indices.some(candidate=>indexKey(candidate)===indexKey(index)))&&(side!=='first'||!selectedIndex()||index.definitionRevisionId===selectedIndex()!.definitionRevisionId))??[];
  const value=field(observationId(side,'index')).value;choose(observationId(side,'index'),[...new Set(indices.map(index=>index.index))].map(value=>({value,label:friendly(value)})),value,retainedFloating(side)?.index===value?friendly(value):undefined);
 }
 function commodityControls(selected?:string){
  for(const side of selected?[selected]:['floating','first']){
   observationOptions[side]=undefined;observations[side].reset();
   for(const suffix of ['index','projection','reset-frequency','averaging','price-source'])field(observationId(side,suffix)).value='';
   indexChoices(side);indexControls(side);
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
  const i=selectedIndex();text('swap-pv',`Trade PV: ${display.text(value.presentValue,'money')} ${i?.currency??''} · instrument unit PV: ${display.text(value.instrumentPresentValue,'money')} ${i?.currency??''}/${i?.unit??''} · trade quantity: ${display.text(value.quantity,'quantity')} ${i?.unit??''}. Side values below are per normalized unit.`);
  const host=get('swap-schedules');host.replaceChildren();
  for(const side of value.sides){const section=document.createElement('section');section.className='swap-schedule';const title=document.createElement('h3');title.textContent=`${side.kind==='fixed'?'Fixed':floatingPair()?(side.slot===0?'First floating':'Second floating'):'Floating'} side · ${side.coefficient==='-1'?'Pay':'Receive'}`;const sum=document.createElement('p');sum.className='field-help';sum.textContent=`Side unit PV: ${display.text(side.presentValue,'money',side.kind)} ${i?.currency??''}/${i?.unit??''}; signed contribution: ${display.text(side.contribution,'money',side.kind)} ${i?.currency??''}/${i?.unit??''}.`;const payments=document.createElement('div');payments.className='table-wrap';section.append(title,sum,payments);
   renderRegularTable(payments,`swap-${side.slot}-payments`,JSON.stringify(side),['Delivery start','Delivery end · exclusive','Payment date',`Cash · ${i?.currency}/${i?.unit}`,'DF',`PV · ${i?.currency}/${i?.unit}`],side.payments.map(p=>[p.deliveryFrom,p.deliveryTo,p.paymentDate,p.cashAmount,p.discountFactor,p.presentValue]),[3,4,5],options.busy,{displayRows:side.payments.map(p=>[p.deliveryFrom,p.deliveryTo,p.paymentDate,display.text(p.cashAmount,'money',side.kind),display.text(p.discountFactor,'factor',side.kind),display.text(p.presentValue,'money',side.kind)])});
   const source=side.kind==='floating'&&floatingPair()&&side.slot===0?selectedIndex('first'):i;
   side.payments.forEach((payment,n)=>{if(payment.observationSchedule)renderSwapObservationInspection(section,payment.observationSchedule,payment.paymentDate,`swap-${side.slot}-${n}`,options.zone(),{price:`${source?.currency}/${source?.unit}${source?.quoteMultiplier==='1'?'':' × '+source?.quoteMultiplier}`,cash:`${i?.currency}/${i?.unit}`,quantity:source?.unit??''},options.busy);});
   const resets=document.createElement('details'),label=document.createElement('summary'),table=document.createElement('div');label.textContent=`${side.payments.flatMap(p=>p.resets).length} ${side.kind==='fixed'?'fixed price row':'reset rows'} · inspect price lookups`;table.className='table-wrap';resets.append(label,table);section.append(resets);host.append(section);
   renderRegularTable(table,`swap-${side.slot}-resets`,JSON.stringify(side.payments),['Observation start','Observation end · exclusive',`Price · ${source?.currency}/${source?.unit}${source?.quoteMultiplier==='1'?'':' × '+source?.quoteMultiplier}`,'Weight',`Cash · ${i?.currency}/${i?.unit}`,'State'],side.payments.flatMap(p=>p.resets.map(r=>[r.from,r.to,r.price,r.weight,r.cashContribution,r.fixingState])),[2,3,4],options.busy,{displayRows:side.payments.flatMap(p=>p.resets.map(r=>[r.from,r.to,display.text(r.price,'price',side.kind),display.text(r.weight,'quantity',side.kind),display.text(r.cashContribution,'money',side.kind),r.fixingState]))});
   const evidence=document.createElement('details'),evidenceLabel=document.createElement('summary'),evidenceBody=document.createElement('pre');evidenceLabel.textContent='Exact price lookups, observation lineage and discount inputs';evidenceBody.className='swap-evidence';evidenceBody.textContent=JSON.stringify(side.payments.map(p=>({paymentDate:p.paymentDate,discountMappingId:p.discountMappingId,discountCurve:p.discountCurve,discountPolicy:p.discountPolicy,pillars:p.pillars,resets:p.resets.map(r=>({from:r.from,to:r.to,spread:r.spread,lookup:r.lookup,lineage:r.lineage,fixingState:r.fixingState,fixing:r.fixing}))})),null,2);evidence.append(evidenceLabel,evidenceBody);section.append(evidence);
  }
  get('swap-result').hidden=false;
 }
 function renderDetail(value:Detail){detail=value;catalog={...value.catalog,context:value.context,asOf:value.asOf};dates.invalidate();populateCatalog();const state=value.state,t=state.terms;field('swap-type').value=t.swapType;typeControls();for(const side of ['floating','first'])field(observationId(side,'commodity')).value=t.commodity;commodityControls();text('swap-template-note','Retained trade terms. Each side shows its saved values.');
  field('swap-book').value=t.bookId;field('swap-quantity').value=t.quantity;field(floatingPair()?'swap-first-direction':'swap-direction').value=t.firstCoefficient??t.fixedCoefficient;
  for(const side of ['floating',...(floatingPair()?['first']:[])]){
   const terms=side==='first'?t.firstFloating!:t.floating;
   observations[side].restore(terms.observationSchedule);
   const i=catalog.indices.find(i=>i.definitionRevisionId===t.definitionRevisionId&&i.curve===terms.curve&&i.index===terms.index);if(!i)throw Error('The retained exact index definition is unavailable.');
   restoreSelection(observationId(side,'index'),i.index,friendly(i.index));indexControls(side);restoreSelection(observationId(side,'reset-frequency'),terms.resetFrequency,friendly(terms.resetFrequency));restoreSelection(observationId(side,'projection'),indexKey(i),curveName(i.curve));restoreSelection(observationId(side,'averaging'),terms.averagingMethod,friendly(terms.averagingMethod));projectionControls(side);field(observationId(side,'spread')).value=terms.spread??'0';priceSourceControls(side,terms.customProjection??null);text(observationId(side,'legacy-fixing'),terms.firstFixingAt?`Retained legacy first fixing boundary: ${displayInstant(terms.firstFixingAt,options.zone())}. New pricing uses the selected index convention.`:'');get(observationId(side,'legacy-fixing')).hidden=!terms.firstFixingAt;
  }
  if(t.fixed)field('swap-price').value=t.fixed.price;
  for(const side of activeSides()){const terms=side==='first'?t.firstFloating!:side==='fixed'?t.fixed!:t.floating;field(`swap-${side}-currency`).value=terms.currency;field(`swap-${side}-from`).value=terms.deliveryFrom;field(`swap-${side}-to`).value=terms.deliveryTo;setPayment(side,terms.paymentSchedule);}
  field('swap-discounting').value=t.discounting?Discounting.CurrencyDefault:Discounting.None;field('swap-reason').value='';direction();
  text('swap-reference',`${state.tradeReference} · ${state.instrumentReference} · Revision ${state.revision}`);text('swap-mode',value.canAmend?'Latest revision · amendment appends a retained event':value.amendUnavailableReason??'Read-only revision');text('swap-effective',`Amendment effective: ${displayInstant(value.asOf,options.zone())}. Prior revisions remain unchanged.`);
  choose('swap-revision',value.history.map(s=>({value:String(s.revision),label:`Revision ${s.revision} · ${s.kind}`})),String(state.revision));get('swap-revisions').hidden=false;get('swap-amendment').hidden=false;get<HTMLInputElement>('swap-reason').required=value.canAmend;
  renderRegularTable(get('swap-history'),'swap-history',JSON.stringify(value.history),['Revision','Event','Effective','Recorded','Quantity','Trader ID','Memo','Trade date','Legal entity','Counterparty','Reason'],value.history.map(s=>[String(s.revision),s.kind,displayInstant(s.effectiveAt,options.zone()),displayInstant(s.recordedAt,options.zone()),s.quantity,s.traderCode??'',s.memo??'',s.booking?.tradeDate??'',s.booking?.legalEntity??'',s.booking?.counterparty??'',s.reason]),[0,4],options.busy);get('swap-history-panel').hidden=false;
  prepared=undefined;get('swap-confirmation').hidden=true;valuation(value.valuation);sync();
 }
 async function openNew(){reset();const started=generation,source=key();await safe(async()=>{const response=await options.api<Catalog>('swaps/catalog',context());if(started!==generation||source!==key())return;catalog=response;populateCatalog();await refreshObservations();if(started!==generation||source!==key())return;text('swap-mode','Choose a swap type and enter each independent side.');typeControls();get<HTMLInputElement>('swap-reason').required=false;sync();dialog.showModal();});}
 async function open(tradeId:string,revision:number|null){reset();const started=++generation,source=key();invalidate();await safe(async()=>{const response=await options.api<Detail>('swaps/editor',{context:context(),tradeId,revision});if(started!==generation||source!==key())return;await display.load(response.state.tradeId,context(),response.canAmend,response.state.terms.firstFloating?['trade','floating']:['trade','fixed','floating']);await prepare(response.valuation);if(started!==generation||source!==key())return;renderDetail(response);await refreshObservations();if(started!==generation||source!==key())return;if(!dialog.open)dialog.showModal();});}
 get('swap-close').onclick=()=>{if(!options.busy())reset();};dialog.addEventListener('cancel',reset);
 field('swap-quantity-unit').onchange=quantityUnitLabel;
 get('swap-type-choices').querySelectorAll<HTMLButtonElement>('button').forEach(button=>button.onclick=()=>{field('swap-type').value=button.dataset.swapType!;field('swap-type').dispatchEvent(new Event('change',{bubbles:true}));});
 field('swap-type').onchange=()=>{invalidate();typeControls();applyEntryTemplate();dates.sync();};
 for(const side of ['floating','first'])field(observationId(side,'commodity')).onchange=()=>{invalidate();if(side==='floating')field('swap-price').value='';const spread=field(observationId(side,'spread'));if(spread.value!==catalog?.entryTemplate.spread)spread.value='';commodityControls(side);};
 for(const side of ['first','floating']){
  field(observationId(side,'averaging')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};field(observationId(side,'price-source')).onchange=()=>{invalidate();priceSourceControls(side);};
  field(observationId(side,'index')).onchange=()=>{invalidate();indexControls(side);};field(observationId(side,'reset-frequency')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};field(observationId(side,'projection')).onchange=()=>{observationOptions[side]=undefined;invalidate();projectionControls(side);};
 }
 for(const side of ['fixed','first','floating'])for(const id of ['payment-rule','day-basis','payment-adjustment'])field(`swap-${side}-${id}`).addEventListener('change',()=>paymentControls(side));field('swap-direction').onchange=direction;field('swap-first-direction').onchange=direction;
 for(const suffix of ['from','to','currency'])for(const side of ['fixed','first','floating'])field(`swap-${side}-${suffix}`).addEventListener('change',()=>{for(const otherSide of activeSides().filter(s=>s!==side)){const other=field(`swap-${otherSide}-${suffix}`);if(!other.value)other.value=field(`swap-${side}-${suffix}`).value;}});
 get<HTMLFormElement>('swap-form').addEventListener('change',event=>{const id=(event.target as HTMLElement).id;if(['swap-type','swap-commodity','swap-first-commodity','swap-fixed-from','swap-fixed-to',...['first','floating'].flatMap(side=>['index','projection','reset-frequency','averaging','price-source','observation-calendar'].map(suffix=>observationId(side,suffix)).concat([`swap-${side}-from`,`swap-${side}-to`]))].includes(id)){void options.act(()=>safe(refreshObservations));}});
 get<HTMLFormElement>('swap-form').addEventListener('input',event=>{invalidate();if(!(event.target as Element).closest('.schedule-date-picker'))dates.invalidate();});
 get<HTMLFormElement>('swap-form').onsubmit=event=>{event.preventDefault();void options.act(()=>safe(async()=>{
  const i=selectedIndex();if(!i||!catalog)throw Error('Choose a supported exact index and curve.');if(detail&&!detail.canAmend)throw Error('Only the latest eligible trade revision can be amended.');
  const started=generation,preview=previewGeneration,source=key();const instrument=await instrumentDraft();const priced=await options.api<{quantity:{amount:string};resolvedFixedPrice?:string;parRate?:{decimalPlaces:number}|null}>('swaps/price',instrument);if(!priced.quantity)throw Error('The instrument quantity conversion is incomplete.');if(started!==generation||preview!==previewGeneration||source!==key())return;
  applyPar(priced);
  const {context:unused,quantity:entered,parRatePolicy,...terms}=instrument;
  const body={...context(),...annotations.read(),...terms,knowledgeMode:'current',bookId:field('swap-book').value,quantity:priced.quantity.amount,fixed:instrument.fixed?{...instrument.fixed,price:priced.resolvedFixedPrice??instrument.fixed.price}:null,
   ...(detail?{tradeId:detail.state.tradeId,expectedRevision:detail.state.revision,effectiveAt:detail.asOf,reason:field('swap-reason').value}:{})};
  if(!Object.values(Discounting).includes(field('swap-discounting').value as Discounting))throw Error('Choose discounting explicitly.');const command={operationId:crypto.randomUUID(),path:detail?'swaps/amend':'swaps/book',body};
  const response=await options.api<{valuation:Valuation}>('swaps/preview',{...body,operationId:command.operationId});if(started!==generation||preview!==previewGeneration||source!==key())return;
  await prepare(response.valuation);if(started!==generation||preview!==previewGeneration||source!==key())return;prepared=command;valuation(response.valuation);get('swap-confirmation').hidden=false;get<HTMLInputElement>('swap-confirm').checked=false;text('swap-submit',detail?'Submit amendment':'Book financial swap');sync();
 }));};
 get<HTMLInputElement>('swap-confirm').onchange=sync;
 get('swap-submit').onclick=()=>void options.act(()=>safe(async()=>{if(!prepared||!get<HTMLInputElement>('swap-confirm').checked)throw Error('Validate schedules and confirm the exact terms first.');await options.queue(prepared);}));
 field('swap-revision').onchange=()=>{if(detail)void options.act(()=>open(detail!.state.tradeId,Number(field('swap-revision').value)));};get('swap-latest').onclick=()=>{if(detail)void options.act(()=>open(detail!.state.tradeId,null));};
 return{open,openNew,reset,sync,async liveRefresh(){const requested=failedLiveSelection??(dialog.open&&detail?{tradeId:detail.state.tradeId,revision:Number(field('swap-revision').value)}:undefined);if(requested){try{await open(requested.tradeId,requested.revision);}catch(error){if((error as {code?:string}).code!=='access_denied')failedLiveSelection=requested;throw error;}}else if(dialog.open){catalog=await options.api<Catalog>('swaps/catalog',context());await refreshObservations();previewGeneration++;prepared=undefined;get('swap-result').hidden=true;get('swap-confirmation').hidden=true;dates.invalidate();dates.sync();await draft.liveRefresh();sync();}},refresh(){annotations.refresh();dates.invalidate();dates.sync();invalidate();},
  // The swap the dialog shows, and booking or amendment edits not yet sent.
  shown:()=>detail&&dialog.open?{tradeId:detail.state.tradeId,instrumentType:'financial_swap' as const,revision:detail.state.revision,reference:detail.state.tradeReference}:undefined,
  unsaved:()=>dialog.open&&edits.edited()};
}
