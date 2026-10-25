import type {CustomProjection} from './swap-projections.js';
import {tradeAnnotations,type Annotation} from './trade-annotations.js';
import {trackEdits} from './edit-tracking.js';
import {renderRegularTable} from './regular-table.js';
import {dealFormatEditor} from './deal-format-editor.js';
import {renderSwapTradePayments,sideDirection,swapPaymentQuote} from './swap-trade-review.js';
import {renderSwapObservationInspection} from './swap-observations-view.js';
import type {Catalog,Index,Valuation} from './swap-editor.js';
import type {PaymentInput} from './swap-contract.js';
import type {ReferenceKey,ObservationSchedule} from './swap-observations.js';
import type {RequestCommand} from './pending-command.js';
import {publicText,curveName} from './market-view.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Period={amount:string|null;fixed:{price:string|null;currency:string;deliveryFrom:string;deliveryTo:string;paymentSchedule:PaymentInput}|null;floating:{customProjection?:CustomProjection|null;observationSchedule?:ObservationSchedule|null;curve:string;index:string;currency:string;deliveryFrom:string;deliveryTo:string;paymentSchedule:PaymentInput;resetFrequency:string;averagingMethod:string;spread:string}|null};
type QuantitySide={basis:string;quantity:ReferenceKey;amount:string|null;allocation:string|null;priceApplication:string;power:{clock:ReferenceKey;product:string;startHour:number|null;endHour:number|null;saturday:boolean|null;calendar:ReferenceKey|null}|null;periods:Period[]};
export type Scheduled={implementation:string;first:QuantitySide;second:QuantitySide};
type ExtendedCatalog=Omit<Catalog,'paymentCalendars'>&{scheduledOptions:{implementation:string;priceApplication:string;maximumPeriods:number;powerQuantities:{specification:ReferenceKey;unit:string;commodity:string}[];clocks:{reference:ReferenceKey;label:string}[]};paymentCalendars:{code:string;reference:ReferenceKey}[]};
export type QuantityReview={slot:number;basis:string;power:{product:string;clock:ReferenceKey|string;clockFingerprint?:string}|null;periods:{amount:string|null;deliveryHours?:string;clockFingerprint?:string}[]};
type Detail={quantityInputs?:QuantityReview[];state:Partial<Annotation>&{tradeId:string;revision:number;tradeReference:string;terms:Record<string,unknown>&{scheduled:Scheduled;bookId:string;discounting:boolean;commodity:string;firstCoefficient:string};[key:string]:unknown};history:{revision:number;kind:string}[];catalog:ExtendedCatalog;valuation:Valuation;canAmend:boolean;canBook?:boolean;asOf:string;context:unknown;amendUnavailableReason:string|null};
const convention='independent_side_financial_swap_v2';
/** The quantity bases, allocations and delivery products of a scheduled side (D214), as the ticket offers them and the panel's Terms name them. */
export const quantityBases=[{value:'period_total',label:'Explicit total for each period'},{value:'calendar_day_rate',label:'Quantity per calendar delivery day'},{value:'contract_total',label:'One total for this side’s whole contract'},{value:'selected_interval_power',label:'MW throughout selected delivery hours'}] as const;
export const allocations=[{value:'equal_periods',label:'Equal quantities in each monthly period'},{value:'calendar_days',label:'Proportion to calendar days in each monthly period'}] as const;
export const deliveryProducts=[{value:'all_hours',label:'All actual delivery hours'},{value:'weekday_block',label:'Selected weekday block, excluding calendar holidays'}] as const;
type IntervalRow={slot:number;period:number;ordinal:number;start:string;end:string;operatingDate:string;energyNumerator:string;energyDenominator:string;energyQuantity:string};
/** What a scheduled swap's review is drawn with: the catalog's indices and clocks, the deal's own amount text, the person's zone, and the page's busy state and action. */
export type ScheduledReview={indices:Index[];clocks:readonly {reference:{code:string};label:string}[];text:(value:string,kind:'money'|'price'|'quantity'|'factor',role?:string)=>string;zone:string;busy:()=>boolean;
  act:(work:()=>Promise<void>)=>Promise<void>;
  /** Reads a power schedule's retained UTC delivery intervals, for a swap that has one. */
  intervals?:()=>Promise<IntervalRow[]>};
/** The read of a retained scheduled revision's UTC delivery intervals, in the certified context it was read in. */
export const retainedIntervals=(api:Api,context:Record<string,unknown>,detail:{state:{tradeId:string;revision:number};context:unknown})=>async()=>
 (await api<{intervals:IntervalRow[]}>('swaps/quantity-schedule',{context:{...context,knowledgeMode:'retained',contextId:(detail.context as {contextId:string}).contextId},tradeId:detail.state.tradeId,revision:detail.state.revision})).intervals;
/**
 * A scheduled swap's valuation review (D214), as its ticket and the side panel's Terms show it: its trade PV, and for each side its
 * direction, a power schedule's delivery hours, its resolved period quantities with signed cash, dates, discount factors and PV, its
 * contractual prices and their states, the observation behind each payment, and the normalized calculations and exact lineage beneath.
 * A power schedule's retained UTC intervals can be inspected.
 */
export function renderScheduledReview(host:HTMLElement,value:Valuation,quantities:QuantityReview[]|undefined,c:ScheduledReview){
 const node=<K extends keyof HTMLElementTagNameMap>(tag:K,label?:string)=>{const n=document.createElement(tag);if(label!==undefined)n.textContent=publicText(label);return n;};
 const settlement=swapPaymentQuote(value.sides[value.sides.length-1],c.indices);
 host.append(node('h3',`Trade PV · ${c.text(value.presentValue,'money')} ${settlement.currency}`));
 for(const side of value.sides){const section=node('section'),i=swapPaymentQuote(side,c.indices);host.append(section);section.append(node('h4',`${side.slot===0?'First':'Second'} side · ${sideDirection(side.coefficient)}`));const quantity=quantities?.find(q=>q.slot===side.slot);if(quantity?.power){const clock=quantity.power.clock,clockCode=typeof clock==='string'?clock:clock.code,clockLabel=c.clocks.find(k=>k.reference.code===clockCode)?.label??clockCode;section.append(node('p',`Delivery product: ${quantity.power.product.replaceAll('_',' ')} · clock ${clockLabel}. Period-average price × integrated period energy.`));quantity.periods.forEach((p,n)=>{if(p.deliveryHours)section.append(node('p',`${side.payments[n]?.deliveryFrom??''}: ${p.deliveryHours} selected delivery hours · entered ${p.amount??''} MW.`));});}renderSwapTradePayments(section,'scheduled',side,i,c.busy,(amount,kind,selected)=>c.text(amount,kind,selected));
  const prices=node('div');prices.className='table-wrap';section.append(prices);const rows=side.payments.flatMap(p=>p.resets);renderRegularTable(prices,`scheduled-${side.slot}-prices`,JSON.stringify(rows),[`Contractual price · ${i?.currency}/${i?.unit} × ${i?.quoteMultiplier}`,'State','Observation start','Observation end · exclusive'],rows.map(r=>[r.price,r.fixingState,r.from,r.to]),[0],c.busy,{displayRows:rows.map(r=>[c.text(r.price,'price',side.kind),r.fixingState,r.from,r.to])});
  for(const p of side.payments)if(p.observationSchedule)renderSwapObservationInspection(section,p.observationSchedule,p.paymentDate,`scheduled-${side.slot}-${p.deliveryFrom}`,c.zone,{price:`${i?.currency}/${i?.unit}`,cash:i?.currency??'',quantity:i?.unit??''},c.busy);
  const technical=node('details'),summary=node('summary','Normalized unit calculations and exact fixing / discount lineage'),evidence=node('pre');evidence.className='swap-evidence';evidence.textContent=JSON.stringify({side,quantityInputs:quantity},null,2);technical.append(summary,evidence);section.append(technical);
 }
 const read=c.intervals;if(!read)return;
 const button=node('button','Inspect retained UTC delivery intervals');button.type='button';button.className='secondary';host.append(button);
 button.onclick=()=>void c.act(async()=>{const intervals=await read();if(!button.isConnected)return;const table=node('div');table.className='table-wrap';host.append(table);
  renderRegularTable(table,'scheduled-delivery-intervals',JSON.stringify(intervals),['Side','Period','UTC start','UTC end','Operating date','Exact energy','Quantity reference'],intervals.map(p=>[String(p.slot+1),String(p.period+1),p.start,p.end,p.operatingDate,p.energyDenominator==='1'?p.energyNumerator:p.energyNumerator+'/'+p.energyDenominator,p.energyQuantity]),[0,1,5],c.busy);});
}

/**
 * The scheduled swap ticket (D214), which the side panel hosts as it hosts the swap ticket (D219): a new scheduled swap, or the
 * amendment or read of a scheduled trade's revision. `host` is the panel's form part; the ticket is kept on the page's shelf until
 * it opens there, and goes back when it is reset. Another revision and the trade's settlement view are the page's to open, after
 * it has asked about what the ticket holds: `reopen` and `onSettlement`.
 */
export function setupScheduledSwapEditor(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Record<string,unknown>;queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;onDenied:()=>void;
 host:()=>HTMLElement;unsavedChanged:()=>void;reopen:(revision:number)=>void;onSettlement:()=>void;ask:(question:{text:string;keep:string;go:string})=>Promise<boolean>}){
 const node=<K extends keyof HTMLElementTagNameMap>(tag:K,label?:string)=>{const n=document.createElement(tag);if(label!==undefined)n.textContent=publicText(label);return n;};
 const shelf=document.getElementById('editor-shelf')!,root=node('div');root.id='scheduled-swap-editor';root.className='editing scheduled-swap-editing';root.hidden=true;root.setAttribute('aria-labelledby','scheduled-swap-title');
 const mounted=()=>root.parentElement!==shelf,mount=()=>{options.host().append(root);root.hidden=false;};
 const heading=node('div');heading.className='scheduled-swap-heading';const title=node('h3','Scheduled financial swap');title.id='scheduled-swap-title';const settlementButton=node('button','Settlement and P&L');settlementButton.id='scheduled-settlement-open';settlementButton.type='button';settlementButton.className='secondary';settlementButton.hidden=true;settlementButton.onclick=()=>{if(detail&&!options.busy())options.onSettlement();};heading.append(title,settlementButton);
 const message=node('p');message.setAttribute('role','status');const error=node('p');error.setAttribute('role','alert');
 const form=node('form'),fields=node('fieldset'),review=node('section'),confirmation=node('div');form.id='scheduled-swap-form';
 const ownership=node('section');ownership.id='scheduled-trade-terms';ownership.className='trade-booking-terms';
 const product=node('section'),delivery=node('div');delivery.className='scheduled-side-columns';
 const annotationsHost=ownership;shelf.append(root);root.append(heading,message,error,form);fields.append(product,delivery,annotationsHost);form.append(fields,review,confirmation);
 const annotations=tradeAnnotations('scheduled',options.zone),edits=trackEdits(form,[],options.unsavedChanged);ownership.querySelector('h3')!.textContent='Ownership and parties';
 let catalog:ExtendedCatalog|undefined,detail:Detail|undefined,prepared:RequestCommand|undefined,generation=0;
 let sideEditors:ReturnType<typeof sideEditor>[]=[];
 let visibleValuation:Valuation|undefined,visibleQuantities:QuantityReview[]|undefined;
 const formatHost=node('section');formatHost.id='scheduled-amount-format';fields.append(formatHost);
 const display=dealFormatEditor(formatHost,{api:options.api,act:options.act,busy:options.busy,changed:async()=>{const version=generation,value=visibleValuation;if(value){await prepare(value);if(version===generation&&value===visibleValuation)render(value,visibleQuantities);}}});
 async function prepare(value:Valuation){await display.prepare(value);for(const side of value.sides)await display.prepare(side,side.kind);}
 const context=()=>({...options.context(),convention});
 const field=(host:HTMLElement,label:string,input:HTMLInputElement|HTMLSelectElement)=>{const wrap=node('div'),caption=node('label',label);input.id=input.id||'scheduled-field-'+crypto.randomUUID();caption.htmlFor=input.id;wrap.append(caption,input);host.append(wrap);return input;};
 const identify=(field:HTMLInputElement|HTMLSelectElement,id:string)=>{field.id=id;field.parentElement!.querySelector('label')!.htmlFor=id;};
 const input=(host:HTMLElement,label:string,type='text',value='')=>{const n=node('input');n.type=type;n.value=value;if(type==='text')n.inputMode='decimal';field(host,label,n);return n;};
 const select=(host:HTMLElement,label:string,choices:readonly {value:string;label:string}[],value='')=>{const n=node('select');n.append(new Option('Choose…',''),...choices.map(c=>new Option(publicText(c.label),c.value)));n.value=value;field(host,label,n);return n;};
 const button=(host:HTMLElement,label:string,work:()=>Promise<void>|void)=>{const n=node('button',label);n.type='button';n.onclick=()=>void run(work);host.append(n);return n;};
 async function run(work:()=>Promise<void>|void){await options.act(async()=>{error.textContent='';try{await work();}catch(e){invalidate();error.textContent=(e as Error).message;if((e as {code?:string}).code==='access_denied'){reset();options.onDenied();}}});sync();}
 const invalidate=()=>{generation++;prepared=undefined;confirm.checked=false;visibleValuation=undefined;visibleQuantities=undefined;review.replaceChildren();confirmation.hidden=true;};
 product.append(node('h3','Product and commodity'));
 const kind=select(product,'Swap structure',[{value:'fixed_floating',label:'Fixed for floating'},{value:'floating_floating',label:'Floating for floating'}]);identify(kind,'scheduled-kind');
 const commodity=select(product,'Commodity',[]);identify(commodity,'scheduled-commodity');
 const market=select(product,'Available price-curve set',[]);identify(market,'scheduled-market');
 const direction=select(product,'First side direction',[{value:'-1',label:'Pay first / receive second'},{value:'1',label:'Receive first / pay second'}]);identify(direction,'scheduled-direction');
 const discount=select(product,'Discounting',[{value:'none',label:'No discounting · factor 1'},{value:'currency_default',label:'Selected currency default curve'}]);identify(discount,'scheduled-discounting');
 const pricing=select(product,'Fixed price',[{value:'agreed',label:'Enter agreed fixed prices'},{value:'par',label:'Calculate one common par fixed price'}]);identify(pricing,'scheduled-price-mode');
 const precision=input(product,'Par precision · decimal places','number');precision.min='0';precision.max='18';identify(precision,'scheduled-par-precision');
 const revision=select(product,'Retained revision',[]);identify(revision,'scheduled-revision');root.insertBefore(revision.parentElement!,form);revision.parentElement!.hidden=true;
 const reason=input(ownership,'Amendment reason');reason.inputMode='text';identify(reason,'scheduled-reason');reason.maxLength=500;
 const valuationTerms=node('section');valuationTerms.append(node('h3','Valuation review and booking'),discount.parentElement!);fields.append(valuationTerms);
 const preview=node('button','Calculate and review');preview.type='submit';preview.id='scheduled-preview';form.append(preview);
 const confirm=node('input');confirm.type='checkbox';confirm.id='scheduled-confirm';const confirmLabel=node('label','I reviewed these exact quantities, prices, payment dates and signed cashflows.');confirmLabel.htmlFor=confirm.id;
 const submit=node('button','Book scheduled swap');submit.type='button';submit.id='scheduled-submit';confirmation.append(confirm,confirmLabel,submit);confirmation.hidden=true;
 const available=()=>catalog?.indices.filter(i=>i.commodity===commodity.value&&i.definitionRevisionId===market.value)??[];
 const key=(i:Index)=>JSON.stringify([i.definitionRevisionId,i.curve,i.index]);
 function paymentEditor(host:HTMLElement,initial?:PaymentInput|null){
  const p=node('fieldset');p.append(node('legend','Payment rule'));host.append(p);p.append(node('p','Monthly frequency. Payment rules generate dates independently of quantity and price observations.'));
  const rule=select(p,'Payment date rule',catalog!.paymentOptions.rules,initial?.rule),days=input(p,'Lag days','number',initial?.days?.toString()),day=input(p,'Day of following month','number',initial?.dayOfMonth?.toString());
  const basis=select(p,'Lag day basis',catalog!.paymentOptions.dayBases,initial?.dayBasis??''),short=select(p,'Missing day',catalog!.paymentOptions.shortMonths,initial?.shortMonth??''),adjust=select(p,'Date adjustment',catalog!.paymentOptions.adjustments,initial?.adjustment),calendar=select(p,'Payment calendar',catalog!.paymentCalendars.map(c=>({value:c.code,label:c.code})),initial?.calendar??'');
  const needsCalendar=()=>adjust.value!=='unadjusted'||rule.value==='days_after_period_end'&&basis.value==='business_days';
  const toggle=()=>{const lag=rule.value==='days_after_period_end',locked=options.busy()||!!options.pending()||!!detail&&!detail.canAmend;days.required=lag;day.required=!lag;basis.required=lag;short.required=!lag;adjust.required=true;days.disabled=locked||!lag;day.disabled=locked||lag;basis.disabled=locked||!lag;short.disabled=locked||lag;calendar.required=needsCalendar();calendar.disabled=locked||!needsCalendar();};rule.onchange=toggle;basis.onchange=toggle;adjust.onchange=toggle;toggle();
  const read=():PaymentInput=>({frequency:'monthly',rule:rule.value,days:rule.value==='days_after_period_end'&&days.value?Number(days.value):null,dayOfMonth:rule.value==='day_of_following_month'&&day.value?Number(day.value):null,dayBasis:rule.value==='days_after_period_end'?basis.value:null,shortMonth:rule.value==='day_of_following_month'?short.value:null,adjustment:adjust.value,calendar:needsCalendar()?calendar.value||null:null,implementation:catalog!.paymentOptions.implementation} as PaymentInput);
  button(p,'Apply displayed starting payment terms',()=>{const start=catalog!.entryTemplate.payment;rule.value=start.rule;days.value=String(start.days??'');day.value=String(start.dayOfMonth??'');basis.value=start.dayBasis??'';short.value=start.shortMonth??'';adjust.value=start.adjustment;calendar.value=start.calendar??'';toggle();invalidate();});
  return{read,sync:toggle};
 }
 function periodEditor(host:HTMLElement,slot:number,from:string,to:string,saved?:Period){
  const card=node('fieldset');card.className='scheduled-period';card.append(node('legend',`${from} to ${to} · exclusive end`));host.append(card);
  const amount=input(card,'Period quantity','text',saved?.amount??'');amount.dataset.role='period-amount';
  const fixed=slot===0&&kind.value==='fixed_floating';let read:()=>Period;let syncPayment=()=>{};
  if(fixed){
   const price=input(card,'Agreed price · choose the settlement index to resolve its quote','text',saved?.fixed?.price??'');price.dataset.role='fixed-price';
   const pay=paymentEditor(card,saved?.fixed?.paymentSchedule);syncPayment=pay.sync;
   read=()=>({amount:amount.value||null,floating:null,fixed:{price:pricing.value==='par'?null:price.value,currency:settlementIndex()?.currencyCode??'',deliveryFrom:from,deliveryTo:to,paymentSchedule:pay.read()}});
  }else{
   let selected=saved?.floating?available().find(i=>i.curve===saved.floating!.curve&&i.index===saved.floating!.index):undefined;
   const periodIndices=available().filter(i=>i.contracts.some(p=>p.deliveryFrom===from&&p.deliveryTo===to)||selected&&key(i)===key(selected));
   const index=select(card,'Index and projection curve',periodIndices.map(i=>({value:key(i),label:`${i.index} · ${i.curve} · ${i.currency}/${i.unit} × ${i.quoteMultiplier}`})),selected?key(selected):'');index.dataset.role='period-index';
   const reset=select(card,'Reset frequency',[]),averaging=select(card,'Price averaging',[]),spread=input(card,'Agreed floating spread','text',saved?.floating?.spread??'');
   const note=node('p');note.className='field-help';card.append(note);
   const averagingChoices=()=>{const i=available().find(i=>key(i)===index.value);averaging.replaceChildren(new Option('Choose…',''),...(i?.samplings.filter(s=>s.resetFrequency===reset.value&&(s.averagingMethod==='none'||s.averagingMethod==='arithmetic')).map(s=>new Option(s.averagingMethod==='none'?'Single published monthly index':'Equal calendar-day price averaging',s.averagingMethod))??[]));};
   const populate=()=>{const i=available().find(i=>key(i)===index.value);reset.replaceChildren(new Option('Choose…',''),...[...new Set(i?.samplings.filter(s=>s.averagingMethod==='none'||s.averagingMethod==='arithmetic').map(s=>s.resetFrequency)??[])].map(value=>new Option(value,value)));averagingChoices();note.textContent=i?`Price: ${i.currency}/${i.unit} × ${i.quoteMultiplier}. Fixing boundaries and required final source follow the qualified index. Inspect exact instants and revisions in the valued observations.`:'';};
   populate();if(saved?.floating){reset.value=saved.floating.resetFrequency;averagingChoices();averaging.value=saved.floating.averagingMethod;}
   reset.onchange=()=>{averagingChoices();invalidate();};
   index.onchange=()=>{const current=available().find(i=>key(i)===index.value);if(selected&&(!current||selected.currencyCode!==current.currencyCode||selected.quantityCode!==current.quantityCode||selected.quoteMultiplier!==current.quoteMultiplier))spread.value='';selected=current;populate();syncFixedQuotes(true);invalidate();};

   if(saved?.floating&&(saved.floating.observationSchedule!=null||saved.floating.customProjection!=null)){index.disabled=true;reset.disabled=true;averaging.disabled=true;if(!averaging.value){averaging.append(new Option(saved.floating.averagingMethod,saved.floating.averagingMethod));averaging.value=saved.floating.averagingMethod;}if(!reset.value){reset.append(new Option(saved.floating.resetFrequency,saved.floating.resetFrequency));reset.value=saved.floating.resetFrequency;}note.append(' Retained specialized observation/projection terms are preserved; replace a period explicitly to author different terms.');}
   const pay=paymentEditor(card,saved?.floating?.paymentSchedule);syncPayment=()=>{pay.sync();
    if(saved?.floating&&(saved.floating.observationSchedule!=null||saved.floating.customProjection!=null))
     for(const field of [index,reset,averaging])field.disabled=true;
   };
   read=()=>{const i=available().find(i=>key(i)===index.value);if(!i||!reset.value||!averaging.value)throw Error('Choose a qualified index, reset frequency and averaging convention for every floating period.');const resetFrequency=reset.value,averagingMethod=averaging.value;return{amount:amount.value||null,fixed:null,floating:{...saved?.floating,curve:i.curve,index:i.index,currency:i.currencyCode,deliveryFrom:from,deliveryTo:to,paymentSchedule:pay.read(),resetFrequency,averagingMethod,spread:spread.value}};};
  }
  return{read,card,amount,from,to,sync:()=>syncPayment()};
 }
 function sideEditor(slot:number,saved?:QuantitySide){
  const host=node('section');host.className='instrument-side';delivery.append(host);host.append(node('h3',slot===0?'First side':'Second side'),node('p','Keep one index and quote basis within each side. Enter each period quantity, price observations and payment rule explicitly.'));
  const from=input(host,'Contract delivery start','date',saved?.periods[0]?.fixed?.deliveryFrom??saved?.periods[0]?.floating?.deliveryFrom??'');from.dataset.role='delivery-from';
  const last=saved?.periods.at(-1),to=input(host,'Contract delivery end · exclusive','date',last?.fixed?.deliveryTo??last?.floating?.deliveryTo??'');to.dataset.role='delivery-to';
  const quantity=node('fieldset');quantity.append(node('legend','Quantity amount, unit and application'));host.append(quantity);
  const powerOffered=available().some(i=>i.powerQuantitySupported)&&catalog!.scheduledOptions.powerQuantities.some(q=>q.commodity===commodity.value);
  const basis=select(quantity,'Application basis',quantityBases.filter(b=>powerOffered||b.value!=='selected_interval_power'),saved?.basis);basis.dataset.role='quantity-basis';
  const units=select(quantity,'Quantity unit',[],saved?JSON.stringify(saved.quantity):'');units.dataset.role='quantity-unit';
  const total=input(quantity,'Total for this side’s whole contract','text',saved?.amount??'');total.dataset.role='contract-amount';
  const allocation=select(quantity,'Allocate the contract total',allocations,saved?.allocation??'');
  const power=node('fieldset');power.append(node('legend','Power delivery hours'));quantity.append(power);
  const clock=select(power,'Contract time zone / market clock',catalog!.scheduledOptions.clocks.map(c=>({value:JSON.stringify(c.reference),label:c.label})),saved?.power?JSON.stringify(saved.power.clock):'');
  const product=select(power,'Delivery product',deliveryProducts,saved?.power?.product);
  const start=input(power,'Local hour beginning · inclusive','number',saved?.power?.startHour?.toString()),end=input(power,'Local hour end · exclusive','number',saved?.power?.endHour?.toString());
  const saturday=select(power,'Include Saturdays',[{value:'false',label:'Monday through Friday'},{value:'true',label:'Monday through Saturday'}],saved?.power?.saturday?.toString());
  const calendar=select(power,'Holiday calendar',catalog!.paymentCalendars.map(c=>({value:JSON.stringify(c.reference),label:c.code})),saved?.power?.calendar?JSON.stringify(saved.power.calendar):'');
  const quantityHelp=node('p');quantityHelp.className='field-help';quantity.append(quantityHelp);
  if(slot===0)host.append(direction.parentElement!,pricing.parentElement!,precision.parentElement!);
  const cards=node('div');host.append(cards);let rows:ReturnType<typeof periodEditor>[]=[];
  const unitChoices=()=>basis.value==='selected_interval_power'?catalog!.scheduledOptions.powerQuantities.filter(q=>q.commodity===commodity.value):[...new Map(available().flatMap(i=>i.quantityChoices).map(q=>[JSON.stringify(q.specification),q])).values()];
  const toggle=()=>{const old=units.value;units.replaceChildren(new Option('Choose…',''),...unitChoices().map(q=>new Option(q.unit,JSON.stringify(q.specification))));units.value=old;total.parentElement!.hidden=basis.value!=='contract_total';allocation.parentElement!.hidden=basis.value!=='contract_total';power.hidden=basis.value!=='selected_interval_power';for(const row of rows){row.amount.parentElement!.hidden=basis.value==='contract_total';row.amount.parentElement!.querySelector('label')!.textContent=basis.value==='calendar_day_rate'?'Quantity per calendar delivery day':basis.value==='selected_interval_power'?'Power throughout selected delivery hours · MW':'Total quantity for this period';}quantityHelp.textContent=(basis.value==='calendar_day_rate'?'Count every calendar delivery day, including weekends and holidays. ':basis.value==='selected_interval_power'?'Integrate MW over actual selected UTC durations. ':basis.value==='contract_total'?'Allocate this side’s total once using the selected rule. ':'Apply each entered total once to its period. ')+'The independently agreed period-average price multiplies the resolved period quantity.';const block=product.value==='weekday_block';for(const n of [start,end,saturday,calendar]){n.parentElement!.hidden=!block;n.required=basis.value==='selected_interval_power'&&block;}clock.required=basis.value==='selected_interval_power';product.required=clock.required;};
  // Rows that hold entries are replaced only when the person says so, which the panel asks before the page is busy.
  const generate=node('button','Generate monthly period rows');generate.type='button';host.append(generate);
  generate.onclick=()=>void (async()=>{if(options.busy()||rows.length&&!await options.ask({text:'Replace the current period entries with empty rows?',keep:'Keep the entries',go:'Replace them'}))return;await run(generateRows);})();
  async function generateRows(){const version=generation,selection=JSON.stringify(options.context());const response=await options.api<{periods:{deliveryFrom:string;deliveryTo:string}[]}>('swaps/scheduled-periods',{context:context(),from:from.value,to:to.value});if(version!==generation||selection!==JSON.stringify(options.context()))return;cards.replaceChildren();rows=response.periods.map(p=>periodEditor(cards,slot,p.deliveryFrom,p.deliveryTo));invalidate();toggle();}
  if(saved)rows=saved.periods.map(p=>periodEditor(cards,slot,p.fixed?.deliveryFrom??p.floating!.deliveryFrom,p.fixed?.deliveryTo??p.floating!.deliveryTo,p));
  const clearAmounts=()=>{total.value='';rows.forEach(r=>r.amount.value='');message.textContent='Quantity basis or unit changed. Enter the amounts in the selected basis and unit.';invalidate();};basis.onchange=()=>{clearAmounts();toggle();};units.onchange=()=>{clearAmounts();toggle();};product.onchange=toggle;toggle();if(saved)units.value=JSON.stringify(saved.quantity);
  return{host,sync:()=>rows.forEach(row=>row.sync()),read:():QuantitySide=>{if(!basis.value||!units.value)throw Error('Choose a quantity unit and application basis on each side.');if(!rows.length||from.value!==rows[0].from||to.value!==rows.at(-1)!.to)throw Error('Generate the period rows for the currently entered delivery dates.');return{basis:basis.value,quantity:JSON.parse(units.value),amount:basis.value==='contract_total'?total.value:null,allocation:basis.value==='contract_total'?allocation.value:null,priceApplication:catalog!.scheduledOptions.priceApplication,
   power:basis.value==='selected_interval_power'?{clock:JSON.parse(clock.value),product:product.value,startHour:product.value==='weekday_block'?Number(start.value):null,endHour:product.value==='weekday_block'?Number(end.value):null,saturday:product.value==='weekday_block'?saturday.value==='true':null,calendar:product.value==='weekday_block'?JSON.parse(calendar.value):null}:null,
   periods:rows.map(r=>({...r.read(),amount:basis.value==='contract_total'?null:r.amount.value}))};}};
 }
 let fixedQuote:string|undefined;
 function syncFixedQuotes(clearChanged=false){const i=settlementIndex(),signature=i?JSON.stringify([i.currencyCode,i.quantityCode,i.quoteMultiplier]):undefined;const changed=clearChanged&&fixedQuote!==undefined&&signature!==fixedQuote;fixedQuote=signature;for(const price of root.querySelectorAll<HTMLInputElement>('[data-role=fixed-price]')){if(changed)price.value='';price.disabled=!i||pricing.value==='par';price.required=!!i&&pricing.value==='agreed';price.parentElement!.querySelector('label')!.textContent=i?`Agreed price · ${i.currency}/${i.unit} × ${i.quoteMultiplier}`:'Agreed price · choose the settlement index to resolve its quote';}}
 function settlementIndex(){const select=sideEditors[1]?.host.querySelector<HTMLSelectElement>('[data-role=period-index]');return available().find(i=>key(i)===select?.value);}
 function syncPricing(){const fixed=kind.value==='fixed_floating',par=fixed&&pricing.value==='par';pricing.disabled=!fixed;pricing.parentElement!.hidden=!fixed;precision.parentElement!.hidden=!par;precision.disabled=!par;precision.required=par;}
 function rebuild(saved?:Scheduled){delivery.hidden=!kind.value||!commodity.value||!market.value;pricing.parentElement!.hidden=kind.value!=='fixed_floating';delivery.replaceChildren();sideEditors=[sideEditor(0,saved?.first),sideEditor(1,saved?.second)];syncPricing();syncFixedQuotes();sync();}
 function render(value:Valuation,quantities?:QuantityReview[]){
  visibleValuation=value;visibleQuantities=quantities;review.replaceChildren();
  renderScheduledReview(review,value,quantities,{indices:catalog?.indices??[],clocks:catalog?.scheduledOptions.clocks??[],text:(amount,kind,role)=>display.text(amount,kind,role),zone:options.zone(),busy:options.busy,act:run,
   intervals:detail&&(detail.state.terms.scheduled.first.power||detail.state.terms.scheduled.second.power)?retainedIntervals(options.api,context(),detail):undefined});
 }
 function body(){const first=sideEditors[0].read(),second=sideEditors[1].read(),i=settlementIndex();if(!i||!direction.value||!discount.value||!kind.value)throw Error('Choose complete product, direction, pricing and discounting terms.');if(kind.value==='fixed_floating'&&!pricing.value)throw Error('Choose agreed fixed prices or calculate par.');return{...context(),...annotations.read(),knowledgeMode:'current',operationId:crypto.randomUUID(),bookId:(document.getElementById('scheduled-book') as HTMLSelectElement).value,executedAt:null,quantity:null,fixedCoefficient:null,discounting:discount.value==='currency_default',fixed:null,floating:null,definitionRevisionId:i.definitionRevisionId,commodity:commodity.value,swapType:'scheduled_financial_swap',firstCoefficient:direction.value,scheduled:{implementation:catalog!.scheduledOptions.implementation,first,second},...(detail?{tradeId:detail.state.tradeId,expectedRevision:detail.state.revision,effectiveAt:detail.asOf,reason:reason.value}:{})};}
 form.oninput=event=>{if(fields.contains(event.target as Node))invalidate();};form.onchange=event=>{if(fields.contains(event.target as Node))invalidate();};
 form.onsubmit=event=>{event.preventDefault();void run(async()=>{const request=body(),version=generation,selection=JSON.stringify(options.context());const response=await options.api<{terms:Record<string,unknown>;valuation:Valuation;quantityInputs:QuantityReview[];parRate:{price:{value:string}}|null}>('swaps/scheduled-price',{terms:request,priceMode:kind.value==='fixed_floating'?pricing.value:'agreed',parRatePolicy:pricing.value==='par'&&kind.value==='fixed_floating'?{implementation:catalog!.parRateOptions.implementation,decimalPlaces:Number(precision.value),tieRule:'to_even'}:null});if(version!==generation||selection!==JSON.stringify(options.context()))return;await prepare(response.valuation);if(version!==generation||selection!==JSON.stringify(options.context()))return;prepared={operationId:request.operationId,path:detail?'swaps/amend':'swaps/book',label:detail?`Swap amendment of ${detail.state.tradeReference}`:'New swap',body:{...response.terms,operationId:request.operationId}};render(response.valuation,response.quantityInputs);confirmation.hidden=false;confirm.checked=false;message.textContent=response.parRate?`Calculated par price: ${response.parRate.price.value}. Review the retained rounded price and resulting trade PV.`:'Review the authoritative trade cashflows before booking.';});};
 submit.onclick=()=>void run(async()=>{if(!prepared||!confirm.checked)throw Error('Calculate, review and confirm the current contract first.');await options.queue(prepared);});confirm.onchange=sync;
 kind.onchange=()=>{invalidate();pricing.disabled=kind.value!=='fixed_floating';if(catalog)rebuild();};commodity.onchange=()=>{invalidate();market.replaceChildren(new Option('Choose…',''),...[...new Set(catalog?.indices.filter(i=>i.commodity===commodity.value).map(i=>i.definitionRevisionId))].map(value=>new Option([...new Set((catalog?.indices??[]).filter(i=>i.definitionRevisionId===value&&i.commodity===commodity.value).map(i=>curveName(i.curve)))].join(', '),value)));if(catalog)rebuild();message.textContent='Commodity changed. Select compatible units and re-enter the independent period terms.';};market.onchange=()=>{invalidate();if(catalog)rebuild();};pricing.onchange=()=>{invalidate();syncPricing();for(const field of root.querySelectorAll<HTMLInputElement>('[data-role=fixed-price]'))field.value='';syncFixedQuotes();};
 revision.onchange=()=>{if(detail)options.reopen(Number(revision.value));};
 function sync(){display.sync();settlementButton.hidden=!detail;settlementButton.disabled=options.busy()||!!options.pending();const locked=options.busy()||!!options.pending()||!!detail&&!detail.canAmend;fields.disabled=locked;sideEditors.forEach(side=>side.sync());syncFixedQuotes();revision.disabled=options.busy()||!!options.pending();reason.required=!!detail?.canAmend;preview.disabled=locked||!catalog||!kind.value||!commodity.value||!market.value||(!detail&&!catalog.canBook);submit.disabled=locked||!prepared||!confirm.checked;}
 function reset(){generation++;display.reset();visibleValuation=undefined;visibleQuantities=undefined;fixedQuote=undefined;prepared=undefined;catalog=undefined;detail=undefined;shelf.append(root);root.hidden=true;form.reset();review.replaceChildren();confirmation.hidden=true;error.textContent='';message.textContent='';edits.clear();}
 async function populate(c:ExtendedCatalog,saved?:Detail){invalidate();const version=generation;catalog=c;detail=saved;annotations.populate(c.bookingActor,c,saved?.state);const book=document.getElementById('scheduled-book') as HTMLSelectElement;book.replaceChildren(new Option('Choose…',''),...c.books.map(b=>new Option(`${b.portfolio} / ${b.name}`,b.id)));if(saved)book.value=saved.state.terms.bookId;commodity.replaceChildren(new Option('Choose…',''),...c.commodities.map(v=>new Option(v.name,v.code)));if(saved){const t=saved.state.terms;commodity.value=t.commodity;market.replaceChildren(...[...new Set(c.indices.filter(i=>i.commodity===t.commodity).map(i=>i.definitionRevisionId))].map(value=>new Option([...new Set((catalog?.indices??[]).filter(i=>i.definitionRevisionId===value&&i.commodity===commodity.value).map(i=>curveName(i.curve)))].join(', '),value)));market.value=t.definitionRevisionId as string;kind.value=t.scheduled.first.periods[0].fixed?'fixed_floating':'floating_floating';direction.value=t.firstCoefficient;discount.value=t.discounting?'currency_default':'none';pricing.value=kind.value==='fixed_floating'?'agreed':'';revision.replaceChildren(...saved.history.map(v=>new Option(`Revision ${v.revision} · ${v.kind}`,String(v.revision))));revision.value=String(saved.state.revision);message.textContent=saved.canAmend?'Latest retained revision · amendments append a new revision.':saved.amendUnavailableReason??'Historical read-only view';}else message.textContent='Enter each side independently. Period quantities, price observations and payment rules are distinct contractual terms.';revision.parentElement!.hidden=!saved;reason.parentElement!.hidden=!saved;rebuild(saved?.state.terms.scheduled);syncPricing();syncFixedQuotes();if(saved){await display.load(saved.state.tradeId,context(),saved.canAmend,saved.state.terms.scheduled.first.periods[0].fixed?['trade','fixed','floating']:['trade','floating']);await prepare(saved.valuation);if(version!==generation)return;render(saved.valuation,saved.quantityInputs);}edits.clear();sync();mount();}
 async function openNew(){reset();const version=generation,selection=JSON.stringify(options.context());const c=await options.api<ExtendedCatalog>('swaps/catalog',context());if(version!==generation||selection!==JSON.stringify(options.context()))return;await populate(c);}
 return{openNew,openDetail:(value:unknown)=>populate((value as Detail).catalog,value as Detail),reset,sync,refresh(){invalidate();annotations.refresh();sync();},
  /** A booking or amendment the backend refused for good: what was prepared for it is dropped, and the ticket, with what was typed in it, stays to be corrected. */
  unprepare(){invalidate();sync();},
  /** Puts the revision select back on the revision the ticket shows, when the person kept the ticket rather than read another. */
  keepRevision(){if(detail)revision.value=String(detail.state.revision);},
  revision:()=>mounted()?detail?.state.revision:undefined,unsaved:()=>mounted()&&edits.edited(),isOpen:mounted};
}
