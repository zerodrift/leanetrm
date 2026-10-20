import {draftValuation} from './draft-valuation.js';
import {mountSwapSideEditor} from './swap-side-editor.js';
import {dealFormatEditor} from './deal-format-editor.js';
import {amountText} from './amount-display.js';
import {tradeAnnotations,type PartyCatalog,type Trader,type Annotation} from './trade-annotations.js';
import {createScheduleDatePicker} from './schedule-date-picker.js';
import type {Context} from './date-editor.js';
import {PaymentDateRule,PaymentDayBasis,DateAdjustment,Discounting,type PaymentInput,type PaymentOptions} from './swap-contract.js';
import {publicText,displayInstant,deliveryLabel,curveName,friendly} from './market-view.js';
import {renderRegularTable} from './regular-table.js';
import type {RequestCommand} from './pending-command.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Index={definitionRevisionId:string;curve:string;index:string;commodity:string;currencyCode:string;currency:string;unit:string;quoteMultiplier:string;quantityChoices:{specification:unknown;unit:string}[];defaultDiscountCurve:string|null;fixingConvention:'published_index_observations_v1'|null;fixingUnavailableReason:string|null;averaging:string[];samplings:{resetFrequency:string;averagingMethod:string;label:string}[];contracts:{deliveryFrom:string;deliveryTo:string}[]};
type Catalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string}[];indices:Index[];commodities:{code:string;name:string}[];paymentCalendars:{code:string}[];paymentOptions:PaymentOptions;asOf:string;context:Context;canBook?:boolean};
type Terms={commodity:string;definitionRevisionId:string;bookId:string;executedAt:string;quantity:string;fixedCoefficient:string;discounting:boolean;fixed:{price:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null};floating:{curve:string;index:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentAt:string|null;paymentSchedule:PaymentInput|null;firstFixingAt?:string|null;resetFrequency:string;averagingMethod:string}};
type State=Annotation&{tradeId:string;tradeReference:string;instrumentReference:string;revision:number;quantity:string;kind:string;reason:string;effectiveAt:string;recordedAt:string;terms:Terms};
type Valuation={presentValue:string;instrumentPresentValue:string;quantity:string;sides:{kind:string;coefficient:string;presentValue:string;contribution:string;payments:{deliveryFrom:string;deliveryTo:string;paymentDate:string;cashAmount:string;discountFactor:string;presentValue:string;discountMappingId:string|null;discountCurve:unknown;discountPolicy:string;pillars:unknown;resets:{lookup:unknown;lineage:unknown;from:string;to:string;price:string;weight:string;cashContribution:string;fixingState:'Actual'|'Projected'|'Fixed';fixing?:{index:unknown;source:unknown;series:unknown;fixingAt:string;implementation:string}|null}[]}[]}[]};
type Detail={state:State;history:State[];catalog:Catalog;valuation:Valuation;canAmend:boolean;amendUnavailableReason:string|null;asOf:string;context:Context};
const convention='independent_side_financial_swap_v2';
export function setupSwapEditor(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Record<string,unknown>;queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;onDenied:()=>void}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const text=(id:string,value:string)=>get(id).textContent=publicText(value);
 const dialog=get<HTMLDialogElement>('swap-editor');
 mountSwapSideEditor('fixed');mountSwapSideEditor('floating');
 const annotations=tradeAnnotations('swap',options.zone);
 let visibleValuation:Valuation|undefined;
 const display=dealFormatEditor(get('swap-amount-format'),{api:options.api,act:options.act,busy:options.busy,changed:async()=>{if(visibleValuation){await prepare(visibleValuation);valuation(visibleValuation);}}});
 async function prepare(value:Valuation){await display.prepare(value);for(const side of value.sides)await display.prepare(side,side.kind);}
 let catalog:Catalog|undefined,detail:Detail|undefined,prepared:RequestCommand|undefined,generation=0,previewGeneration=0;
 const dates=createScheduleDatePicker(get('swap-date-tools'),{id:'swap-date-lookup',api:options.api,run:work=>options.act(()=>safe(work)),context:()=>catalog?.context,zone:options.zone,busy:options.busy,enabled:()=>!options.pending()&&!!catalog&&(!detail?!!catalog:detail.canAmend),targets:['fixed','floating'].flatMap(side=>['from','to'].map(suffix=>({input:get<HTMLInputElement>(`swap-${side}-${suffix}`),label:`${side==='fixed'?'Fixed':'Floating'} side · delivery ${suffix==='from'?'start':'end (exclusive)'}`})))});
 const key=()=>JSON.stringify(options.context());
 const context=()=>({...options.context(),convention});
 const indexKey=(i:Index)=>JSON.stringify([i.definitionRevisionId,i.curve,i.index]);
 const selectedIndex=()=>catalog?.indices.find(i=>indexKey(i)===field('swap-projection').value);
 const sources=()=>catalog?.indices.filter(i=>i.commodity===field('swap-commodity').value&&i.index===field('swap-index').value)??[];
 async function instrumentDraft(){
  const i=selectedIndex();if(!i)throw Error('Choose the exact index and projection curve.');
  if(!Object.values(Discounting).includes(field('swap-discounting').value as Discounting))throw Error('Choose discounting explicitly.');
  if(!field('swap-quantity-unit').value)throw Error('Choose an instrument quantity unit.');
  return{context:context(),definitionRevisionId:i.definitionRevisionId,commodity:field('swap-commodity').value,
   quantity:{value:field('swap-quantity').value,specification:JSON.parse(field('swap-quantity-unit').value)},fixedCoefficient:field('swap-direction').value,discounting:field('swap-discounting').value===Discounting.CurrencyDefault,
   fixed:{price:field('swap-price').value,currency:field('swap-fixed-currency').value,deliveryFrom:field('swap-fixed-from').value,deliveryTo:field('swap-fixed-to').value,paymentSchedule:paymentInput('fixed')},
   floating:{curve:i.curve,index:i.index,currency:field('swap-floating-currency').value,deliveryFrom:field('swap-floating-from').value,deliveryTo:field('swap-floating-to').value,paymentSchedule:paymentInput('floating'),resetFrequency:field('swap-reset-frequency').value,averagingMethod:field('swap-averaging').value}};
 }
 const draft=draftValuation(get('swaps-draft-valuation'),{product:'swaps',api:options.api,act:options.act,busy:options.busy,key,denied:options.onDenied,draft:instrumentDraft});
 function invalidate(){draft.reset();previewGeneration++;prepared=undefined;get('swap-result').hidden=true;get('swap-confirmation').hidden=true;get<HTMLInputElement>('swap-confirm').checked=false;sync();}
 function sync(){draft.sync();display.sync();dates.sync();get<HTMLFieldSetElement>('swap-fields').disabled=options.busy()||!!options.pending()||(!detail? !catalog:!detail.canAmend);get<HTMLButtonElement>('swap-preview').disabled=options.busy()||(!detail?!catalog?.canBook:!detail.canAmend);get<HTMLButtonElement>('swap-submit').disabled=options.busy()||!!options.pending()||!prepared||!get<HTMLInputElement>('swap-confirm').checked;field('swap-revision').disabled=options.busy()||!!options.pending();get<HTMLButtonElement>('swap-latest').disabled=options.busy()||!!options.pending();}
 function reset(){display.reset();visibleValuation=undefined;annotations.reset();generation++;if(dialog.open)dialog.close();detail=undefined;catalog=undefined;dates.reset();invalidate();get<HTMLFormElement>('swap-form').reset();for(const id of ['swap-book','swap-commodity','swap-index','swap-projection','swap-reset-frequency','swap-averaging','swap-fixed-currency','swap-floating-currency','swap-quantity-unit','swap-fixed-payment-calendar','swap-floating-payment-calendar','swap-revision'])field(id).replaceChildren();for(const id of ['swap-reference','swap-mode','swap-unit','swap-price-unit','swap-coverage','swap-legacy-fixing','swap-pv','swap-effective','swap-error'])text(id,'');get('swap-error').hidden=true;get('swap-legacy-fixing').hidden=true;text('swap-fixing-convention','Choose an index and projection curve to inspect its fixing convention.');get('swap-schedules').replaceChildren();get('swap-history').replaceChildren();get('swap-revisions').hidden=true;get('swap-history-panel').hidden=true;get('swap-amendment').hidden=true;for(const side of ['fixed','floating'])paymentControls(side);}
 async function safe(work:()=>Promise<void>){const started=generation;try{get('swap-error').hidden=true;await work();}catch(error){if(started!==generation)return;text('swap-error',error instanceof Error?error.message:'The financial swap operation failed.');get('swap-error').hidden=false;if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}throw error;}}
 function choose(id:string,rows:{value:string;label:string}[],value=''){const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(publicText(r.label),r.value)));select.value=rows.some(r=>r.value===value)?value:'';}
 function direction(){text('swap-floating-direction',field('swap-direction').value==='-1'?'Receive floating':field('swap-direction').value==='1'?'Pay floating':'Choose the fixed-side direction.');}
 function samplingControls(){const rows=selectedIndex()?.samplings.filter(s=>s.resetFrequency===field('swap-reset-frequency').value)??[];choose('swap-averaging',rows.map(s=>({value:s.averagingMethod,label:s.averagingMethod==='none'?'No averaging · one index price':s.label})),field('swap-averaging').value);}
 function quantityUnitLabel(){text('swap-unit',get<HTMLSelectElement>('swap-quantity-unit').selectedOptions[0]?.textContent??'choose quantity unit');}
 function projectionControls(){
  const candidates=sources().filter(i=>i.samplings.some(s=>s.resetFrequency===field('swap-reset-frequency').value));
  choose('swap-projection',candidates.map(i=>({value:indexKey(i),label:curveName(i.curve)})),field('swap-projection').value);
  const i=selectedIndex(),units=i?.quantityChoices??[],old=field('swap-quantity-unit').value;
  const retained=units.some(u=>JSON.stringify(u.specification)===old);
  if(old&&!retained)field('swap-quantity').value='';
  choose('swap-quantity-unit',units.map(u=>({value:JSON.stringify(u.specification),label:u.unit})),retained?old:units[0]?JSON.stringify(units[0].specification):'');
  quantityUnitLabel();text('swap-price-unit',i?`${i.currency}/${i.unit}${i.quoteMultiplier==='1'?'':' × '+i.quoteMultiplier}`:'choose projection curve');samplingControls();
  const currencies=[...new Map(sources().map(i=>[i.currencyCode,{value:i.currencyCode,label:i.currency}])).values()];
  for(const side of ['fixed','floating'])choose(`swap-${side}-currency`,currencies,field(`swap-${side}-currency`).value);
  text('swap-fixing-convention',!i?'Choose an index and projection curve to inspect its fixing convention.':i.fixingConvention==='published_index_observations_v1'?'Fixings follow the selected index’s published observation schedule. Pricing validates the selected dates; inspect exact fixing sources and instants with each valued reset.':i.fixingUnavailableReason??'This index has no qualified fixing schedule for the selected observations.');
  text('swap-coverage',i?`${i.currency}/${i.unit} × ${i.quoteMultiplier}. Projection curve: ${curveName(i.curve)}. Available delivery periods: ${i.contracts.map(c=>deliveryLabel(c.deliveryFrom,c.deliveryTo)).join(', ')}. Default discount curve: ${i.defaultDiscountCurve?curveName(i.defaultDiscountCurve):'not configured'}.`:'Choose the floating index, reset frequency and exact projection curve. Frequencies are offered only where the selected market data supplies the observations.');
 }
 function indexControls(){
  choose('swap-reset-frequency',[...new Set(sources().flatMap(i=>i.samplings.map(s=>s.resetFrequency)))].map(value=>({value,label:friendly(value)})),field('swap-reset-frequency').value);projectionControls();
 }
 function commodityControls(){
  choose('swap-index',[...new Set((catalog?.indices.filter(i=>i.commodity===field('swap-commodity').value)??[]).map(i=>i.index))].map(value=>({value,label:friendly(value)})));
  field('swap-price').value='';field('swap-quantity').value='';for(const side of ['fixed','floating'])field(`swap-${side}-currency`).value='';indexControls();
 }
 function populateCatalog(){if(!catalog)return;
  annotations.populate(catalog.bookingActor,catalog,detail?.state);
  const policies=catalog.paymentOptions;
  for(const side of ['fixed','floating'])for(const [suffix,choices] of [['payment-frequency',policies.frequencies],['payment-rule',policies.rules],['day-basis',policies.dayBases],['short-month',policies.shortMonths],['payment-adjustment',policies.adjustments]] as const)choose(`swap-${side}-${suffix}`,choices);
 choose('swap-book',catalog.books.map(b=>({value:b.id,label:`${b.portfolio} · ${b.name}`})));choose('swap-commodity',catalog.commodities.filter(c=>catalog!.indices.some(i=>i.commodity===c.code)).map(c=>({value:c.code,label:c.name})));commodityControls();for(const side of ['fixed','floating'])choose(`swap-${side}-payment-calendar`,catalog.paymentCalendars.map(c=>({value:c.code,label:friendly(c.code)})));}
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
  for(const side of value.sides){const section=document.createElement('section');section.className='swap-schedule';const title=document.createElement('h3');title.textContent=`${side.kind==='fixed'?'Fixed':'Floating'} side · ${side.coefficient==='-1'?'Pay':'Receive'}`;const sum=document.createElement('p');sum.className='field-help';sum.textContent=`Side unit PV: ${display.text(side.presentValue,'money',side.kind)} ${i?.currency??''}/${i?.unit??''}; signed contribution: ${display.text(side.contribution,'money',side.kind)} ${i?.currency??''}/${i?.unit??''}.`;const payments=document.createElement('div');payments.className='table-wrap';section.append(title,sum,payments);
   renderRegularTable(payments,`swap-${side.kind}-payments`,JSON.stringify(side),['Delivery start','Delivery end · exclusive','Payment date',`Cash · ${i?.currency}/${i?.unit}`,'DF',`PV · ${i?.currency}/${i?.unit}`],side.payments.map(p=>[p.deliveryFrom,p.deliveryTo,p.paymentDate,p.cashAmount,p.discountFactor,p.presentValue]),[3,4,5],options.busy,{displayRows:side.payments.map(p=>[p.deliveryFrom,p.deliveryTo,p.paymentDate,display.text(p.cashAmount,'money',side.kind),display.text(p.discountFactor,'factor',side.kind),display.text(p.presentValue,'money',side.kind)])});
   const resets=document.createElement('details'),label=document.createElement('summary'),table=document.createElement('div');label.textContent=`${side.payments.flatMap(p=>p.resets).length} ${side.kind==='fixed'?'fixed price row':'reset rows'} · inspect price lookups`;table.className='table-wrap';resets.append(label,table);section.append(resets);host.append(section);
   renderRegularTable(table,`swap-${side.kind}-resets`,JSON.stringify(side.payments),['Observation start','Observation end · exclusive',`Price · ${i?.currency}/${i?.unit}${i?.quoteMultiplier==='1'?'':' × '+i?.quoteMultiplier}`,'Weight',`Cash · ${i?.currency}/${i?.unit}`,'State'],side.payments.flatMap(p=>p.resets.map(r=>[r.from,r.to,r.price,r.weight,r.cashContribution,r.fixingState])),[2,3,4],options.busy,{displayRows:side.payments.flatMap(p=>p.resets.map(r=>[r.from,r.to,display.text(r.price,'price',side.kind),display.text(r.weight,'quantity',side.kind),display.text(r.cashContribution,'money',side.kind),r.fixingState]))});
   const evidence=document.createElement('details'),evidenceLabel=document.createElement('summary'),evidenceBody=document.createElement('pre');evidenceLabel.textContent='Exact price lookups, observation lineage and discount inputs';evidenceBody.className='swap-evidence';evidenceBody.textContent=JSON.stringify(side.payments.map(p=>({paymentDate:p.paymentDate,discountMappingId:p.discountMappingId,discountCurve:p.discountCurve,discountPolicy:p.discountPolicy,pillars:p.pillars,resets:p.resets.map(r=>({from:r.from,to:r.to,lookup:r.lookup,lineage:r.lineage,fixingState:r.fixingState,fixing:r.fixing}))})),null,2);evidence.append(evidenceLabel,evidenceBody);section.append(evidence);
  }
  get('swap-result').hidden=false;
 }
 function renderDetail(value:Detail){detail=value;catalog={...value.catalog,context:value.context,asOf:value.asOf};dates.invalidate();populateCatalog();const state=value.state,t=state.terms;field('swap-commodity').value=t.commodity;commodityControls();
  field('swap-book').value=t.bookId;field('swap-quantity').value=t.quantity;field('swap-direction').value=t.fixedCoefficient;
  const i=catalog.indices.find(i=>i.definitionRevisionId===t.definitionRevisionId&&i.curve===t.floating.curve&&i.index===t.floating.index);if(!i)throw Error('The retained exact index definition is unavailable.');field('swap-index').value=i.index;indexControls();field('swap-reset-frequency').value=t.floating.resetFrequency;projectionControls();field('swap-projection').value=indexKey(i);projectionControls();
  field('swap-price').value=t.fixed.price;for(const side of ['fixed','floating'] as const){field(`swap-${side}-currency`).value=t[side].currency;field(`swap-${side}-from`).value=t[side].deliveryFrom;field(`swap-${side}-to`).value=t[side].deliveryTo;setPayment(side,t[side].paymentSchedule);}
  const legacy=t.floating.firstFixingAt;text('swap-legacy-fixing',legacy?`Retained legacy first fixing boundary: ${displayInstant(legacy,options.zone())}. New pricing and amendments use the selected index’s fixing convention.`:'');get('swap-legacy-fixing').hidden=!legacy;field('swap-reset-frequency').value=t.floating.resetFrequency;samplingControls();field('swap-averaging').value=t.floating.averagingMethod;field('swap-discounting').value=t.discounting?Discounting.CurrencyDefault:Discounting.None;field('swap-reason').value='';direction();
  text('swap-reference',`${state.tradeReference} · ${state.instrumentReference} · Revision ${state.revision}`);text('swap-mode',value.canAmend?'Latest revision · amendment appends a retained event':value.amendUnavailableReason??'Read-only revision');text('swap-effective',`Amendment effective: ${displayInstant(value.asOf,options.zone())}. Prior revisions remain unchanged.`);
  choose('swap-revision',value.history.map(s=>({value:String(s.revision),label:`Revision ${s.revision} · ${s.kind}`})),String(state.revision));get('swap-revisions').hidden=false;get('swap-amendment').hidden=false;get<HTMLInputElement>('swap-reason').required=value.canAmend;
  renderRegularTable(get('swap-history'),'swap-history',JSON.stringify(value.history),['Revision','Event','Effective','Recorded','Quantity','Trader ID','Memo','Trade date','Legal entity','Counterparty','Reason'],value.history.map(s=>[String(s.revision),s.kind,displayInstant(s.effectiveAt,options.zone()),displayInstant(s.recordedAt,options.zone()),s.quantity,s.traderCode??'',s.memo??'',s.booking?.tradeDate??'',s.booking?.legalEntity??'',s.booking?.counterparty??'',s.reason]),[0,4],options.busy);get('swap-history-panel').hidden=false;
  prepared=undefined;get('swap-confirmation').hidden=true;valuation(value.valuation);sync();
 }
 async function openNew(){reset();const started=generation,source=key();await safe(async()=>{const response=await options.api<Catalog>('swaps/catalog',context());if(started!==generation||source!==key())return;catalog=response;populateCatalog();text('swap-mode','New financial swap · both sides value independently');get<HTMLInputElement>('swap-reason').required=false;sync();dialog.showModal();});}
 async function open(tradeId:string,revision:number|null){reset();const started=++generation,source=key();invalidate();await safe(async()=>{const response=await options.api<Detail>('swaps/editor',{context:context(),tradeId,revision});if(started!==generation||source!==key())return;await display.load(response.state.tradeId,context(),response.canAmend,['trade','fixed','floating']);await prepare(response.valuation);if(started!==generation||source!==key())return;renderDetail(response);if(!dialog.open)dialog.showModal();});}
 get('swap-close').onclick=()=>{if(!options.busy())reset();};dialog.addEventListener('cancel',reset);
 field('swap-quantity-unit').onchange=quantityUnitLabel;
 field('swap-commodity').onchange=()=>{invalidate();commodityControls();};field('swap-index').onchange=()=>{invalidate();indexControls();};field('swap-reset-frequency').onchange=()=>{invalidate();projectionControls();};field('swap-projection').onchange=()=>{invalidate();projectionControls();};
 for(const side of ['fixed','floating'])for(const id of ['payment-rule','day-basis','payment-adjustment'])field(`swap-${side}-${id}`).addEventListener('change',()=>paymentControls(side));field('swap-direction').onchange=direction;
 for(const suffix of ['from','to','currency'])for(const side of ['fixed','floating'])field(`swap-${side}-${suffix}`).addEventListener('change',()=>{const other=field(`swap-${side==='fixed'?'floating':'fixed'}-${suffix}`);if(!other.value)other.value=field(`swap-${side}-${suffix}`).value;});
 get<HTMLFormElement>('swap-form').addEventListener('input',event=>{invalidate();if(!(event.target as Element).closest('.schedule-date-picker'))dates.invalidate();});
 get<HTMLFormElement>('swap-form').onsubmit=event=>{event.preventDefault();void options.act(()=>safe(async()=>{
  const i=selectedIndex();if(!i||!catalog)throw Error('Choose a supported exact index and curve.');if(detail&&!detail.canAmend)throw Error('Only the latest eligible trade revision can be amended.');
  const started=generation,preview=previewGeneration,source=key();const instrument=await instrumentDraft();const priced=await options.api<{quantity:{amount:string}}>('swaps/price',instrument);if(!priced.quantity)throw Error('The instrument quantity conversion is incomplete.');if(started!==generation||preview!==previewGeneration||source!==key())return;
  const body={...context(),...annotations.read(),knowledgeMode:'current',commodity:field('swap-commodity').value,definitionRevisionId:i.definitionRevisionId,bookId:field('swap-book').value,quantity:priced.quantity.amount,fixedCoefficient:field('swap-direction').value,discounting:field('swap-discounting').value===Discounting.CurrencyDefault,
   fixed:instrument.fixed,floating:instrument.floating,
   ...(detail?{tradeId:detail.state.tradeId,expectedRevision:detail.state.revision,effectiveAt:detail.asOf,reason:field('swap-reason').value}:{})};
  if(!Object.values(Discounting).includes(field('swap-discounting').value as Discounting))throw Error('Choose discounting explicitly.');const command={operationId:crypto.randomUUID(),path:detail?'swaps/amend':'swaps/book',body};
  const response=await options.api<{valuation:Valuation}>('swaps/preview',{...body,operationId:command.operationId});if(started!==generation||preview!==previewGeneration||source!==key())return;
  prepared=command;await prepare(response.valuation);valuation(response.valuation);get('swap-confirmation').hidden=false;get<HTMLInputElement>('swap-confirm').checked=false;text('swap-submit',detail?'Submit amendment':'Book financial swap');sync();
 }));};
 get<HTMLInputElement>('swap-confirm').onchange=sync;
 get('swap-submit').onclick=()=>void options.act(()=>safe(async()=>{if(!prepared||!get<HTMLInputElement>('swap-confirm').checked)throw Error('Validate schedules and confirm the exact terms first.');await options.queue(prepared);}));
 field('swap-revision').onchange=()=>{if(detail)void options.act(()=>open(detail!.state.tradeId,Number(field('swap-revision').value)));};get('swap-latest').onclick=()=>{if(detail)void options.act(()=>open(detail!.state.tradeId,null));};
 return{open,openNew,reset,sync,refresh(){annotations.refresh();dates.invalidate();dates.sync();invalidate();}};
}
