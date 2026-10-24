import {parseFixingBoundaries,type FixingTerms} from './swap-fixings.js';
import {createAvailableDatePicker} from './available-date-picker.js';
import {linkedValue,availableSwapIndices,quantityChoice,deliveryChoices,type DeliveryPeriod} from './swap-selection.js';
import {draftValuation} from './draft-valuation.js';
import {mountSwapSideEditor,mountFirstFloatingEditor,arrangeSwapSections} from './swap-side-editor.js';
import {dealFormatEditor} from './deal-format-editor.js';
import {tradeAnnotations,type PartyCatalog,type Trader,type Annotation} from './trade-annotations.js';
import {createScheduleDatePicker} from './schedule-date-picker.js';
import type {Context} from './date-editor.js';
import {PaymentDateRule,PaymentDayBasis,DateAdjustment,Discounting,type PaymentInput,type PaymentOptions} from './swap-contract.js';
import {zonedInput} from './user-time.js';
import {publicText,displayInstant,deliveryLabel,curveName,friendly} from './market-view.js';
import {renderRegularTable} from './regular-table.js';
import type {RequestCommand} from './pending-command.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Index={definitionRevisionId:string;curve:string;index:string;commodity:string;currencyCode:string;currency:string;unit:string;quoteMultiplier:string;quantityCode:string;quantityChoices:{specification:{code:string};unit:string}[];defaultDiscountCurve:string|null;averaging:string[];samplings:{resetFrequency:string;averagingMethod:string;label:string}[];contracts:{deliveryFrom:string;deliveryTo:string}[];deliveryPeriods:DeliveryPeriod[];fixingDeliveryPeriods:DeliveryPeriod[]};
type Catalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string}[];indices:Index[];commodities:{code:string;name:string}[];paymentCalendars:{code:string}[];paymentOptions:PaymentOptions;fixingOptions:{implementation:string;revisionPolicy:string;sources:{code:string}[]};parRateOptions:{implementation:string;initialDecimalPlaces:number;tieRule:string};asOf:string;context:Context;canBook?:boolean};
type FloatingTerms={curve:string;index:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null;firstFixingAt:string;resetFrequency:string;averagingMethod:string;spread:string;fixingTerms?:FixingTerms|null};
type Terms={swapType:string;firstCoefficient:string|null;firstFloating:FloatingTerms|null;commodity:string;definitionRevisionId:string;bookId:string;executedAt:string;quantity:string;fixedCoefficient:string;discounting:boolean;fixed:{price:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null}|null;floating:FloatingTerms};
type State=Annotation&{tradeId:string;tradeReference:string;instrumentReference:string;revision:number;quantity:string;kind:string;reason:string;effectiveAt:string;recordedAt:string;terms:Terms};
type Valuation={presentValue:string;instrumentPresentValue:string;quantity:string;sides:{slot:number;kind:string;coefficient:string;presentValue:string;contribution:string;payments:{deliveryFrom:string;deliveryTo:string;paymentDate:string;resolvedQuantity:string;tradeCashAmount:string;tradePresentValue:string;cashAmount:string;discountFactor:string;presentValue:string;discountMappingId:string|null;discountCurve:unknown;discountPolicy:string;pillars:unknown;resets:{spread:string|null;lookup:unknown;lineage:unknown;from:string;to:string;price:string;weight:string;cashContribution:string;fixingState:string;fixing?:unknown;fixingLineage?:unknown}[]}[]}[]};
type Detail={valuationReceiptId?:string|null;state:State;history:State[];catalog:Catalog;valuation:Valuation;canAmend:boolean;amendUnavailableReason:string|null;asOf:string;context:Context};
const convention='independent_side_financial_swap_v2';
export function setupSwapEditor(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Record<string,unknown>;queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;onDenied:()=>void}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement>(id);
 const text=(id:string,value:string)=>get(id).textContent=publicText(value);
 const dialog=get<HTMLDialogElement>('swap-editor');
 mountSwapSideEditor('fixed');mountSwapSideEditor('floating');mountFirstFloatingEditor();arrangeSwapSections();
 const annotations=tradeAnnotations('swap',options.zone);
 get('swap-trade-terms').querySelector('h3')!.textContent='Ownership & parties';
 let visibleValuation:Valuation|undefined,unitNeedsReview=false;
 const quoteUnits=new Map<string,string>();
 const display=dealFormatEditor(get('swap-amount-format'),{api:options.api,act:options.act,busy:options.busy,changed:async()=>{const value=visibleValuation,version=previewGeneration;if(value){await prepare(value);if(value===visibleValuation&&version===previewGeneration)valuation(value);}}});
 async function prepare(value:Valuation){await display.prepare(value);for(const side of value.sides)await display.prepare(side,side.kind);}
 const clocks={fixing:zonedInput(get<HTMLInputElement>('swap-fixing'),options.zone,options.api),first:zonedInput(get<HTMLInputElement>('swap-first-fixing'),options.zone,options.api)};
 let catalog:Catalog|undefined,detail:Detail|undefined,prepared:RequestCommand|undefined,generation=0,previewGeneration=0;
 const dates=createScheduleDatePicker(get('swap-date-tools'),{id:'swap-date-lookup',api:options.api,run:work=>options.act(()=>safe(work)),context:()=>catalog?.context,zone:options.zone,busy:options.busy,enabled:()=>!options.pending()&&!!catalog&&(!detail?!!catalog:detail.canAmend),targets:['fixed','first','floating'].flatMap(side=>['from','to'].map(suffix=>({input:get<HTMLInputElement>(`swap-${side}-${suffix}`),available:()=>!!field('swap-type').value&&activeSides().includes(side),acceptsDate:date=>side==='fixed'||sideDateChoices(side)[suffix==='from'?'starts':'ends'].includes(date),label:`${side==='fixed'?'Fixed':side==='first'?'First floating':'Floating'} side · delivery ${suffix==='from'?'start':'end (exclusive)'}`})))});
 const key=()=>JSON.stringify(options.context());
 const context=()=>({...options.context(),convention});
 const indexKey=(i:Index)=>JSON.stringify([i.definitionRevisionId,i.curve,i.index]);
 const floatingPair=()=>field('swap-type').value==='financial_floating_floating';
 const observationId=(side:string,suffix:string)=>`swap-${side==='first'?'first-':''}${suffix}`;
 const selectedIndex=(side='floating')=>catalog?.indices.find(i=>indexKey(i)===field(observationId(side,'projection')).value);
 function periodsFor(index:Index,side:string){const mode=field(observationId(side,'fixing-mode')).value;return mode==='actual_projected'?index.fixingDeliveryPeriods:mode==='projection_only'?index.deliveryPeriods:[...index.deliveryPeriods,...index.fixingDeliveryPeriods];}
 const available=(side='floating')=>availableSwapIndices(catalog?.indices??[],field('swap-commodity').value,field(observationId(side,'index-search')).value,side==='first'?selectedIndex()?.definitionRevisionId:undefined).filter(i=>periodsFor(i,side).length>0);
 const sources=(side='floating')=>available(side).filter(i=>i.index===field(observationId(side,'index')).value);
 const activeSides=()=>[floatingPair()?'first':'fixed','floating'];
 function sideDateChoices(side:string){const i=selectedIndex(side),periods=i?periodsFor(i,side):available(side).flatMap(i=>periodsFor(i,side));return deliveryChoices(periods,field(observationId(side,'reset-frequency')).value||'fixed',field(`swap-${side}-from`).value);}
 const deliveryPickers=['first','floating'].flatMap(side=>['from','to'].map(suffix=>{
  const input=get<HTMLInputElement>(`swap-${side}-${suffix}`),host=document.createElement('div');input.hidden=true;input.required=false;input.after(host);
  const picker=createAvailableDatePicker(host,{id:input.id+'-calendar',label:`${side==='first'?'First floating':side==='fixed'?'Fixed':'Floating'} delivery ${suffix==='from'?'start':'end (exclusive)'}`,busy:()=>options.busy()||!!options.pending()||!catalog||!!detail&&!detail.canAmend||!activeSides().includes(side),onChange:date=>{input.value=date;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));}});
  return{side,suffix,input,picker};
 }));
 function syncDeliveryDates(){
  for(const {side,suffix,input,picker} of deliveryPickers){let choices=sideDateChoices(side)[suffix==='from'?'starts':'ends'];if(detail&&!detail.canAmend&&input.value)choices=[...choices,input.value];else if(input.value&&!choices.includes(input.value))input.value='';picker.set(choices,input.value);}
 }

 function typeControls(){
  const type=field('swap-type').value;
  for(const side of ['fixed','first','floating']){const on=!!type&&(side==='floating'||side===(floatingPair()?'first':'fixed'));for(const section of ['editor','delivery','observations']){get(`swap-${side}-${section}`).hidden=!on;get(`swap-${side}-${section}`).querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLTextAreaElement>('input,select,textarea').forEach(n=>n.disabled=!on);}if(on)paymentControls(side);if(side!=='fixed')fixingControls(side);}
  text('swap-floating-title',floatingPair()?'Second floating side':'Floating side');direction();priceControls();
 }
 function priceControls(){
  const par=field('swap-price-mode').value==='par',fixed=!!field('swap-type').value&&!floatingPair();
  get('swap-par-controls').hidden=!par;
  get<HTMLInputElement>('swap-par-places').required=fixed&&par;field('swap-par-places').disabled=!fixed||!par;
  get<HTMLInputElement>('swap-price').required=fixed&&!par;field('swap-price').disabled=!fixed||par;field('swap-price').hidden=par;
 }
 function fixingControls(side:string){
  const active=!!field('swap-type').value&&activeSides().includes(side),actual=field(observationId(side,'fixing-mode')).value==='actual_projected';
  const prefix=side==='first'?'swap-first-':'swap-';
  get(prefix+'fixing-terms').hidden=!actual;
  for(const suffix of ['source','boundaries']){
   const input=field(prefix+'fixing-'+suffix);input.disabled=!active||!actual;(input as HTMLInputElement).required=active&&actual;
  }
  const first=get<HTMLInputElement>(prefix+'fixing');first.disabled=!active||actual;first.required=active&&!actual;first.parentElement!.hidden=actual;
  get<HTMLButtonElement>(prefix+'fixing-load').disabled=options.busy()||!active||!actual||!selectedIndex(side)||!field(`swap-${side}-from`).value||!field(`swap-${side}-to`).value;
 }
 function receipt(id?:string|null){text('swap-receipt',id?`Valuation receipt ${id}. The retained result includes its exact context, market pins and fixing revision lineage.`:'');get('swap-receipt-panel').hidden=!id;}
 function applyPar(result:{resolvedFixedPrice?:string;parRate?:{decimalPlaces:number}|null}){
  if(result.resolvedFixedPrice!==undefined&&result.parRate){field('swap-price').value=result.resolvedFixedPrice;text('swap-par-result',`Par fixed rate: ${result.resolvedFixedPrice} · ${result.parRate.decimalPlaces} decimal places, ties to even. Review this fixed rate before booking.`);}
 }
 async function floatingDraft(side='floating'){
  const i=selectedIndex(side);if(!i)throw Error('Choose each exact floating index and projection curve.');
  const mode=field(observationId(side,'fixing-mode')).value;
  if(!['projection_only','actual_projected'].includes(mode))throw Error('Choose the observation resolution version for each floating side.');
  const actual=mode==='actual_projected';
  const firstFixingAt=actual?null:await(side==='first'?clocks.first:clocks.fixing).instant();
  const fixingTerms=actual?{implementation:catalog!.fixingOptions.implementation,revisionPolicy:catalog!.fixingOptions.revisionPolicy,
   source:field(observationId(side,'fixing-source')).value,boundaries:parseFixingBoundaries(field(observationId(side,'fixing-boundaries')).value)}:undefined;
  if(actual&&!fixingTerms?.source)throw Error('Choose an explicit index fixing source.');
  return{curve:i.curve,index:i.index,currency:field(`swap-${side}-currency`).value,deliveryFrom:field(`swap-${side}-from`).value,deliveryTo:field(`swap-${side}-to`).value,paymentSchedule:paymentInput(side),firstFixingAt,resetFrequency:field(observationId(side,'reset-frequency')).value,averagingMethod:field(observationId(side,'averaging')).value,spread:field(observationId(side,'spread')).value,fixingTerms};

 }
 async function instrumentDraft(){
  const i=selectedIndex();if(!i)throw Error('Choose the exact index and projection curve.');
  if(!field('swap-type').value)throw Error('Choose a swap type.');
  for(const side of activeSides().filter(side=>side!=='fixed')){const available=sideDateChoices(side);if(!available.starts.includes(field(`swap-${side}-from`).value)||!available.ends.includes(field(`swap-${side}-to`).value))throw Error('Choose an available delivery start and end for each side.');}
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
 const draft=draftValuation(get('swaps-draft-valuation'),{product:'swaps',api:options.api,act:work=>options.act(()=>safe(work)),busy:options.busy,key,denied:options.onDenied,draft:instrumentDraft,priced:async result=>{applyPar(result);if(result.valuation){const value=result.valuation as Valuation,version=previewGeneration,started=generation;await prepare(value);if(version===previewGeneration&&started===generation){valuation(value);receipt(result.valuationReceiptId);}}}});
 function invalidate(){visibleValuation=undefined;receipt();text('swap-par-result','');draft.reset();previewGeneration++;prepared=undefined;get('swap-result').hidden=true;get('swap-confirmation').hidden=true;get<HTMLInputElement>('swap-confirm').checked=false;sync();}
 function sync(){typeControls();syncDeliveryDates();field('swap-type').disabled=options.busy()||!!detail;draft.sync();display.sync();dates.sync();get<HTMLFieldSetElement>('swap-fields').disabled=options.busy()||!!options.pending()||(!detail? !catalog:!detail.canAmend);get<HTMLButtonElement>('swap-preview').disabled=options.busy()||(!detail?!catalog?.canBook:!detail.canAmend);get<HTMLButtonElement>('swap-submit').disabled=options.busy()||!!options.pending()||!prepared||!get<HTMLInputElement>('swap-confirm').checked;field('swap-revision').disabled=options.busy()||!!options.pending();get<HTMLButtonElement>('swap-latest').disabled=options.busy()||!!options.pending();}
 function reset(){quoteUnits.clear();unitNeedsReview=false;display.reset();visibleValuation=undefined;annotations.reset();generation++;if(dialog.open)dialog.close();detail=undefined;catalog=undefined;dates.reset();invalidate();get<HTMLFormElement>('swap-form').reset();Object.values(clocks).forEach(c=>c.clear());for(const id of ['swap-book','swap-commodity','swap-index','swap-projection','swap-reset-frequency','swap-averaging','swap-fixed-currency','swap-floating-currency','swap-quantity-unit','swap-fixed-payment-calendar','swap-floating-payment-calendar','swap-revision','swap-first-index','swap-first-projection','swap-first-reset-frequency','swap-first-averaging','swap-first-currency','swap-first-payment-calendar','swap-fixing-source','swap-first-fixing-source'])field(id).replaceChildren();for(const id of ['swap-reference','swap-mode','swap-unit','swap-price-unit','swap-coverage','swap-pv','swap-effective','swap-error'])text(id,'');get('swap-error').hidden=true;get('swap-schedules').replaceChildren();get('swap-history').replaceChildren();get('swap-revisions').hidden=true;get('swap-history-panel').hidden=true;get('swap-amendment').hidden=true;for(const side of ['fixed','first','floating'])paymentControls(side);typeControls();}
 async function safe(work:()=>Promise<void>){const started=generation;try{get('swap-error').hidden=true;await work();}catch(error){if(started!==generation)return;text('swap-error',error instanceof Error?error.message:'The financial swap operation failed.');get('swap-error').hidden=false;if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}throw error;}}
 function choose(id:string,rows:{value:string;label:string}[],value='',linked=false){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(publicText(r.label),r.value)));select.value=linked?linkedValue(rows.map(r=>r.value),value):rows.some(r=>r.value===value)?value:'';}
 function direction(){const value=field(floatingPair()?'swap-first-direction':'swap-direction').value;text('swap-floating-direction',value==='-1'?'Receive floating':value==='1'?'Pay floating':'Choose the first-side direction.');}
 function samplingControls(side='floating'){const selected=selectedIndex(side),rows=selected?.samplings.filter(s=>periodsFor(selected,side).some(p=>p.resetFrequency===s.resetFrequency)&&s.resetFrequency===field(observationId(side,'reset-frequency')).value)??[];choose(observationId(side,'averaging'),rows.map(s=>({value:s.averagingMethod,label:s.averagingMethod==='none'?'No averaging · one index price':s.label})),field(observationId(side,'averaging')).value,true);}
 function quantityUnitLabel(){text('swap-unit',get<HTMLSelectElement>('swap-quantity-unit').selectedOptions[0]?.textContent??'choose quantity unit');}
 function projectionControls(side='floating'){
  const candidates=sources(side).filter(i=>!field(observationId(side,'reset-frequency')).value||i.samplings.some(s=>s.resetFrequency===field(observationId(side,'reset-frequency')).value));
  choose(observationId(side,'projection'),candidates.map(i=>({value:indexKey(i),label:curveName(i.curve)})),field(observationId(side,'projection')).value,true);
  const i=selectedIndex(side),unit=i?JSON.stringify([i.definitionRevisionId,i.currencyCode,i.quantityCode,i.quoteMultiplier]):'';
  const prior=quoteUnits.get(side);
  if(prior&&unit!==prior){field(observationId(side,'spread')).value='';if(side==='floating')field('swap-price').value='';}
  if(unit)quoteUnits.set(side,unit);
  samplingControls(side);text(observationId(side,'spread-unit'),i?`${i.currency}/${i.unit}${i.quoteMultiplier==='1'?'':' × '+i.quoteMultiplier}`:'choose projection curve');
  if(side==='floating'){
   quantityControls();text('swap-price-unit',i?`${i.currency}/${i.unit}${i.quoteMultiplier==='1'?'':' × '+i.quoteMultiplier}`:'choose projection curve');
   text('swap-coverage',i?`${i.currency}/${i.unit} × ${i.quoteMultiplier}. Projection curve: ${curveName(i.curve)}. Defined delivery periods (validated for the selected resolution): ${i.contracts.map(c=>deliveryLabel(c.deliveryFrom,c.deliveryTo)).join(', ')}. Default discount curve: ${i.defaultDiscountCurve?curveName(i.defaultDiscountCurve):'not configured'}.`:'Choose the floating index, reset frequency and exact projection curve.');
  }
  const currencies=[...new Map((i?[i]:candidates).map(i=>[i.currencyCode,{value:i.currencyCode,label:i.currency}])).values()];
  for(const slot of side==='first'?['first']:['fixed','floating'])choose(`swap-${slot}-currency`,currencies,field(`swap-${slot}-currency`).value,true);
 }
 function indexControls(side='floating'){
  choose(observationId(side,'reset-frequency'),[...new Set(sources(side).flatMap(i=>i.samplings.filter(s=>periodsFor(i,side).some(p=>p.resetFrequency===s.resetFrequency)).map(s=>s.resetFrequency)))].map(value=>({value,label:friendly(value)})),field(observationId(side,'reset-frequency')).value,true);projectionControls(side);
 }
 function availableIndexControls(side:string,preserve=true){
  const indices=available(side);
  choose(observationId(side,'index'),[...new Set(indices.map(i=>i.index))].map(value=>({value,label:friendly(value)})),preserve?field(observationId(side,'index')).value:'',true);indexControls(side);
 }
 function quantityControls(){
  const i=selectedIndex(),units=[...new Map((i?[i]:availableSwapIndices(catalog?.indices??[],field('swap-commodity').value)).flatMap(row=>row.quantityChoices).map(u=>[JSON.stringify(u.specification),u])).values()],old=field('swap-quantity-unit').value;
  let selected=quantityChoice(units,i?.quantityCode??'',old);if(old&&!selected&&field('swap-quantity').value)unitNeedsReview=true;if(unitNeedsReview)selected='';choose('swap-quantity-unit',units.map(u=>({value:JSON.stringify(u.specification),label:u.unit})),selected);quantityUnitLabel();
  text('swap-quantity-warning',unitNeedsReview?'The previous unit is incompatible. Amount retained; choose a compatible unit and review the quantity.':'');
 }
 function commodityControls(){
  for(const side of ['first','floating'])field(observationId(side,'index-search')).value='';
  for(const side of ['fixed','first','floating'])field(`swap-${side}-currency`).value='';
  availableIndexControls('floating',false);availableIndexControls('first',false);
  field('swap-price').value='';
  for(const side of ['first','floating'])if(quoteUnits.has(side)){field(observationId(side,'spread')).value='';}
 }
 function populateCatalog(){if(!catalog)return;
  annotations.populate(catalog.bookingActor,catalog,detail?.state);
  for(const side of ['first','floating'])choose(observationId(side,'fixing-source'),catalog.fixingOptions.sources.map(s=>({value:s.code,label:friendly(s.code)})));
  const policies=catalog.paymentOptions;field('swap-par-places').value=String(catalog.parRateOptions.initialDecimalPlaces);
  for(const side of ['fixed','first','floating'])for(const [suffix,choices] of [['payment-frequency',policies.frequencies],['payment-rule',policies.rules],['day-basis',policies.dayBases],['short-month',policies.shortMonths],['payment-adjustment',policies.adjustments]] as const)choose(`swap-${side}-${suffix}`,choices);
 choose('swap-book',catalog.books.map(b=>({value:b.id,label:`${b.portfolio} · ${b.name}`})));choose('swap-commodity',catalog.commodities.filter(c=>availableSwapIndices(catalog!.indices,c.code).length>0).map(c=>({value:c.code,label:c.name})));commodityControls();for(const side of ['fixed','first','floating'])choose(`swap-${side}-payment-calendar`,catalog.paymentCalendars.map(c=>({value:c.code,label:friendly(c.code)})));}
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
  const i=selectedIndex();
  text('swap-pv',`Trade PV: ${display.text(value.presentValue,'money')} ${i?.currency??''} · notional: ${display.text(value.quantity,'quantity')} ${i?.unit??''} total for each side’s delivery period.`);
  const host=get('swap-schedules');host.replaceChildren();
  for(const side of value.sides){
   const sideIndex=selectedIndex(side.slot===0&&floatingPair()?'first':'floating')??i;
   const priceUnit=`${sideIndex?.currency}/${sideIndex?.unit}${sideIndex?.quoteMultiplier==='1'?'':' × '+sideIndex?.quoteMultiplier}`;
   const direction=side.coefficient==='-1'?'Pay':'Receive';
   const observations=side.payments.flatMap(p=>p.resets);
   const section=document.createElement('section'),title=document.createElement('h3');
   section.className='swap-schedule';
   title.textContent=`${side.kind==='fixed'?'Fixed':floatingPair()?(side.slot===0?'First floating':'Second floating'):'Floating'} side · ${direction}`;
   const payments=document.createElement('div');payments.className='table-wrap swap-payment-table';section.append(title,payments);
   const paymentRows=side.payments.map(p=>[p.deliveryFrom,p.deliveryTo,p.resolvedQuantity,sideIndex?.unit??'',direction,p.paymentDate,p.tradeCashAmount,p.discountFactor,p.tradePresentValue]);
   renderRegularTable(payments,`swap-${side.slot}-payments`,JSON.stringify(side),
    ['Delivery start','Delivery end · exclusive','Total quantity','Unit','Direction','Payment date',`Signed cash · ${sideIndex?.currency}`,'DF',`Trade PV · ${sideIndex?.currency}`],
    paymentRows,[2,6,7,8],options.busy,{displayRows:side.payments.map((p,n)=>[
     ...paymentRows[n].slice(0,2),display.text(p.resolvedQuantity,'quantity'),...paymentRows[n].slice(3,6),
     display.text(p.tradeCashAmount,'money',side.kind),display.text(p.discountFactor,'factor',side.kind),display.text(p.tradePresentValue,'money',side.kind)
    ])});
   const compact=document.createElement('div');compact.className='swap-payment-summary';
   for(const p of side.payments){
    const row=document.createElement('p');
    row.textContent=`${p.deliveryFrom} to ${p.deliveryTo} (exclusive) · total ${display.text(p.resolvedQuantity,'quantity')} ${sideIndex?.unit}. ${direction} · signed cash ${display.text(p.tradeCashAmount,'money',side.kind)} ${sideIndex?.currency} on ${p.paymentDate}. Discount factor ${display.text(p.discountFactor,'factor',side.kind)} · PV ${display.text(p.tradePresentValue,'money',side.kind)} ${sideIndex?.currency}.`;
    compact.append(row);
   }
   section.append(compact);

   const normalized=document.createElement('details'),normalizedLabel=document.createElement('summary');
   const sum=document.createElement('p'),unitRows=document.createElement('p');sum.className='field-help';
   normalizedLabel.textContent='Normalized unit calculations';
   sum.textContent=`Side unit PV: ${display.text(side.presentValue,'money',side.kind)} ${sideIndex?.currency??''}/${sideIndex?.unit??''}; signed contribution: ${display.text(side.contribution,'money',side.kind)} ${sideIndex?.currency??''}/${sideIndex?.unit??''}.`;
   unitRows.textContent=side.payments.map(p=>`Unit cash: ${p.cashAmount}; unit PV: ${p.presentValue}`).join('; ');
   normalized.append(normalizedLabel,sum,unitRows);section.append(normalized);

   const prices=document.createElement('p'),priceTable=document.createElement('div');
   prices.className='field-help';priceTable.className='table-wrap';
   prices.textContent=`Contractual prices & fixing states · ${priceUnit}`;section.append(prices,priceTable);
   renderRegularTable(priceTable,`swap-${side.slot}-prices`,JSON.stringify(observations),
    ['Observation start','Observation end · exclusive','Price','State'],observations.map(r=>[r.from,r.to,r.price,r.fixingState]),[2],options.busy,
    {displayRows:observations.map(r=>[r.from,r.to,display.text(r.price,'price',side.kind),r.fixingState])});

   const resets=document.createElement('details'),label=document.createElement('summary'),table=document.createElement('div');
   label.textContent=`${observations.length} ${side.kind==='fixed'?'fixed price row':'reset rows'} · inspect price lookups`;
   table.className='table-wrap';resets.append(label,table);normalized.append(resets);host.append(section);
   renderRegularTable(table,`swap-${side.slot}-resets`,JSON.stringify(side.payments),
    ['Observation start','Observation end · exclusive',`Price · ${priceUnit}`,'Weight',`Cash · ${sideIndex?.currency}/${sideIndex?.unit}`,'State'],
    observations.map(r=>[r.from,r.to,r.price,r.weight,r.cashContribution,r.fixingState]),[2,3,4],options.busy,
    {displayRows:observations.map(r=>[r.from,r.to,display.text(r.price,'price',side.kind),display.text(r.weight,'quantity',side.kind),display.text(r.cashContribution,'money',side.kind),r.fixingState])});

   const evidence=document.createElement('details'),evidenceLabel=document.createElement('summary'),evidenceBody=document.createElement('pre');
   evidenceLabel.textContent='Exact price lookups, observation lineage and discount inputs';
   evidenceBody.className='swap-evidence';evidenceBody.textContent=JSON.stringify(side.payments.map(p=>({
    paymentDate:p.paymentDate,discountMappingId:p.discountMappingId,discountCurve:p.discountCurve,discountPolicy:p.discountPolicy,pillars:p.pillars,
    resets:p.resets.map(r=>({from:r.from,to:r.to,spread:r.spread,lookup:r.lookup,lineage:r.lineage,fixing:r.fixing,fixingLineage:r.fixingLineage}))
   })),null,2);
   evidence.append(evidenceLabel,evidenceBody);section.append(evidence);
  }
  get('swap-result').hidden=false;
 }
 function renderDetail(value:Detail){detail=value;catalog={...value.catalog,context:value.context,asOf:value.asOf};dates.invalidate();populateCatalog();const state=value.state,t=state.terms;field('swap-type').value=t.swapType;typeControls();field('swap-commodity').value=t.commodity;commodityControls();
  field('swap-book').value=t.bookId;field('swap-quantity').value=t.quantity;field(floatingPair()?'swap-first-direction':'swap-direction').value=t.firstCoefficient??t.fixedCoefficient;
  for(const side of ['floating',...(floatingPair()?['first']:[])]){
   const terms=side==='first'?t.firstFloating!:t.floating;
   const i=catalog.indices.find(i=>i.definitionRevisionId===t.definitionRevisionId&&i.curve===terms.curve&&i.index===terms.index);if(!i)throw Error('The retained exact index definition is unavailable.');
   field(observationId(side,'fixing-mode')).value=terms.fixingTerms?'actual_projected':'projection_only';field(observationId(side,'fixing-source')).value=terms.fixingTerms?.source??'';field(observationId(side,'fixing-boundaries')).value=terms.fixingTerms?.boundaries.map(b=>[b.from,b.to,b.fixingAt].join(',')).join('\n')??'';field(observationId(side,'index')).value=i.index;indexControls(side);field(observationId(side,'reset-frequency')).value=terms.resetFrequency;projectionControls(side);field(observationId(side,'projection')).value=indexKey(i);projectionControls(side);field(observationId(side,'averaging')).value=terms.averagingMethod;field(observationId(side,'spread')).value=terms.spread??'0';(side==='first'?clocks.first:clocks.fixing).set(terms.firstFixingAt);
  }
  if(t.fixed){field('swap-price').value=t.fixed.price;field('swap-price-mode').value='agreed';}
  for(const side of activeSides()){const terms=side==='first'?t.firstFloating!:side==='fixed'?t.fixed!:t.floating;field(`swap-${side}-currency`).value=terms.currency;field(`swap-${side}-from`).value=terms.deliveryFrom;field(`swap-${side}-to`).value=terms.deliveryTo;setPayment(side,terms.paymentSchedule);}
  field('swap-discounting').value=t.discounting?Discounting.CurrencyDefault:Discounting.None;field('swap-reason').value='';direction();
  text('swap-reference',`${state.tradeReference} · ${state.instrumentReference} · Revision ${state.revision}`);text('swap-mode',value.canAmend?'Latest revision · amendment appends a retained event':value.amendUnavailableReason??'Read-only revision');text('swap-effective',`Amendment effective: ${displayInstant(value.asOf,options.zone())}. Prior revisions remain unchanged.`);
  choose('swap-revision',value.history.map(s=>({value:String(s.revision),label:`Revision ${s.revision} · ${s.kind}`})),String(state.revision));get('swap-revisions').hidden=false;get('swap-amendment').hidden=false;get<HTMLInputElement>('swap-reason').required=value.canAmend;
  renderRegularTable(get('swap-history'),'swap-history',JSON.stringify(value.history),['Revision','Event','Effective','Recorded','Quantity','Trader ID','Memo','Trade date','Legal entity','Counterparty','Reason'],value.history.map(s=>[String(s.revision),s.kind,displayInstant(s.effectiveAt,options.zone()),displayInstant(s.recordedAt,options.zone()),s.quantity,s.traderCode??'',s.memo??'',s.booking?.tradeDate??'',s.booking?.legalEntity??'',s.booking?.counterparty??'',s.reason]),[0,4],options.busy);get('swap-history-panel').hidden=false;
  prepared=undefined;get('swap-confirmation').hidden=true;valuation(value.valuation);receipt(value.valuationReceiptId);sync();
 }
 async function openNew(){reset();const started=generation,source=key();await safe(async()=>{const response=await options.api<Catalog>('swaps/catalog',context());if(started!==generation||source!==key())return;catalog=response;populateCatalog();text('swap-mode','Choose a swap type and enter each independent side.');typeControls();get<HTMLInputElement>('swap-reason').required=false;sync();dialog.showModal();});}
 async function open(tradeId:string,revision:number|null){reset();const started=++generation,source=key();invalidate();await safe(async()=>{const response=await options.api<Detail>('swaps/editor',{context:context(),tradeId,revision});if(started!==generation||source!==key())return;await display.load(response.state.tradeId,context(),response.canAmend,response.state.terms.firstFloating?['trade','floating']:['trade','fixed','floating']);await prepare(response.valuation);if(started!==generation||source!==key())return;renderDetail(response);if(!dialog.open)dialog.showModal();});}
 get('swap-close').onclick=()=>{if(!options.busy())reset();};dialog.addEventListener('cancel',reset);
 field('swap-quantity-unit').onchange=()=>{unitNeedsReview=false;text('swap-quantity-warning','');quantityUnitLabel();};
 field('swap-price-mode').onchange=()=>{field('swap-price').value='';priceControls();};
 field('swap-type').onchange=()=>{invalidate();typeControls();dates.sync();};
 field('swap-commodity').onchange=()=>{invalidate();commodityControls();syncDeliveryDates();dates.invalidate();};
 for(const side of ['first','floating']){
  get<HTMLButtonElement>(observationId(side,'fixing-load')).onclick=()=>void options.act(()=>safe(async()=>{
   invalidate();const version=previewGeneration,started=generation,i=selectedIndex(side);
   if(!i||!catalog)throw Error('Choose the exact projection definition and delivery period.');
   const response=await options.api<{observations:{from:string;to:string}[]}>('swaps/fixing-observations',{
    context:{...context(),knowledgeMode:'retained',contextId:catalog.context.contextId},definitionRevisionId:i.definitionRevisionId,curve:i.curve,
    from:field(`swap-${side}-from`).value,to:field(`swap-${side}-to`).value,resetFrequency:field(observationId(side,'reset-frequency')).value});
   if(version!==previewGeneration||started!==generation)return;
   field(observationId(side,'fixing-boundaries')).value=response.observations.map(p=>`${p.from},${p.to},`).join('\n');invalidate();
  }));
 }
 for(const side of ['first','floating']){
  field(observationId(side,'fixing-mode')).onchange=()=>{invalidate();availableIndexControls(side);syncDeliveryDates();dates.invalidate();};
  const changed=(work:()=>void)=>{invalidate();work();if(side==='floating')availableIndexControls('first');syncDeliveryDates();dates.invalidate();};
  field(observationId(side,'index-search')).oninput=()=>changed(()=>availableIndexControls(side));
  field(observationId(side,'index')).onchange=()=>changed(()=>indexControls(side));field(observationId(side,'reset-frequency')).onchange=()=>changed(()=>projectionControls(side));field(observationId(side,'projection')).onchange=()=>changed(()=>projectionControls(side));
 }
 for(const side of ['fixed','first','floating'])for(const id of ['payment-rule','day-basis','payment-adjustment'])field(`swap-${side}-${id}`).addEventListener('change',()=>paymentControls(side));field('swap-direction').onchange=direction;field('swap-first-direction').onchange=direction;
 get<HTMLFormElement>('swap-form').addEventListener('change',()=>invalidate());
 get<HTMLFormElement>('swap-form').addEventListener('input',event=>{invalidate();if(!(event.target as Element).closest('.schedule-date-picker'))dates.invalidate();});
 get<HTMLFormElement>('swap-form').onsubmit=event=>{event.preventDefault();void options.act(()=>safe(async()=>{
  const i=selectedIndex();if(!i||!catalog)throw Error('Choose a supported exact index and curve.');if(detail&&!detail.canAmend)throw Error('Only the latest eligible trade revision can be amended.');
  const started=generation,preview=previewGeneration,source=key();const instrument=await instrumentDraft();const priced=await options.api<{quantity:{amount:string};resolvedFixedPrice?:string;parRate?:{decimalPlaces:number}|null}>('swaps/price',instrument);if(!priced.quantity)throw Error('The instrument quantity conversion is incomplete.');if(started!==generation||preview!==previewGeneration||source!==key())return;
  applyPar(priced);
  const {context:unused,quantity:entered,parRatePolicy,...terms}=instrument;
  const body={...context(),...annotations.read(),...terms,knowledgeMode:'current',bookId:field('swap-book').value,quantity:priced.quantity.amount,fixed:instrument.fixed?{...instrument.fixed,price:priced.resolvedFixedPrice??instrument.fixed.price}:null,
   ...(detail?{tradeId:detail.state.tradeId,expectedRevision:detail.state.revision,effectiveAt:detail.asOf,reason:field('swap-reason').value}:{})};
  if(!Object.values(Discounting).includes(field('swap-discounting').value as Discounting))throw Error('Choose discounting explicitly.');const command={operationId:crypto.randomUUID(),path:detail?'swaps/amend':'swaps/book',body};
  const response=await options.api<{valuation:Valuation;valuationReceiptId?:string|null}>('swaps/preview',{...body,operationId:command.operationId});if(started!==generation||preview!==previewGeneration||source!==key())return;
  await prepare(response.valuation);if(started!==generation||preview!==previewGeneration||source!==key())return;prepared=command;valuation(response.valuation);receipt(response.valuationReceiptId);get('swap-confirmation').hidden=false;get<HTMLInputElement>('swap-confirm').checked=false;text('swap-submit',detail?'Submit amendment':'Book financial swap');sync();
 }));};
 get<HTMLInputElement>('swap-confirm').onchange=sync;
 get('swap-submit').onclick=()=>void options.act(()=>safe(async()=>{if(!prepared||!get<HTMLInputElement>('swap-confirm').checked)throw Error('Validate schedules and confirm the exact terms first.');await options.queue(prepared);}));
 field('swap-revision').onchange=()=>{if(detail)void options.act(()=>open(detail!.state.tradeId,Number(field('swap-revision').value)));};get('swap-latest').onclick=()=>{if(detail)void options.act(()=>open(detail!.state.tradeId,null));};
 return{open,openNew,reset,sync,refresh(){annotations.refresh();Object.values(clocks).forEach(c=>c.refresh());dates.invalidate();dates.sync();invalidate();}};
}
