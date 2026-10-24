import {curveName,displayInstant,displayDate,publicText} from './market-view.js';
import {firstTradingText,futuresSelection,futuresContractKey,type FuturesContract} from './futures-contract.js';
import {createAvailableDatePicker} from './available-date-picker.js';

// Product-specific presentation shared by booking and amendment. Catalog terms
// and computed quantities/prices originate in the managed operations.
export function futuresInstrumentEditor(host:HTMLElement,ids:{contract:string;lots:string;price:string;terms:string}){
 const heading=document.createElement('h3');heading.textContent='Futures instrument';
 function field(id:string,label:string,input:boolean){const wrap=document.createElement('div'),caption=document.createElement('label');caption.htmlFor=id;caption.textContent=label;const control=document.createElement(input?'input':'select');control.id=id;control.required=true;wrap.append(caption,control);return{wrap,control};}
 const contract=field(ids.contract,'Pricing curve for delivery date',false),lots=field(ids.lots,'Quantity · signed lots',true),price=field(ids.price,'Execution price · quote units',true);
 const commodity=field(ids.contract+'-commodity','Commodity',false),exchange=field(ids.contract+'-exchange','Exchange',false),product=field(ids.contract+'-product','Product',false);
 const filters=document.createElement('div');filters.className='schedule-fields';filters.append(commodity.wrap,exchange.wrap,product.wrap);
 const dateHost=document.createElement('div');
 let catalog:FuturesContract[]=[],locked=false;
 const select=contract.control as HTMLSelectElement;
 const date=createAvailableDatePicker(dateHost,{id:ids.contract+'-date',label:'Delivery starts',busy:()=>locked,onChange:()=>{refresh();changed();}});
 function choose(control:HTMLSelectElement,rows:{value:string;name:string}[],selected:string){control.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(publicText(r.name),r.value)));control.value=rows.some(r=>r.value===selected)?selected:'';control.disabled=!rows.length;}
 function refresh(){
  const c=commodity.control as HTMLSelectElement,e=exchange.control as HTMLSelectElement,p=product.control as HTMLSelectElement;
  let choices=futuresSelection(catalog,{commodity:c.value,exchange:e.value,product:p.value,date:date.value()});
  choose(c,choices.commodities,c.value);choose(e,choices.exchanges,e.value);choose(p,choices.products,p.value);
  choices=futuresSelection(catalog,{commodity:c.value,exchange:e.value,product:p.value,date:date.value()});
  const selectedDate=choices.dates.includes(date.value())?date.value():'';date.set(choices.dates,selectedDate);
  choose(select,choices.contracts.map(c=>({value:futuresContractKey(c),name:curveName(c.curve)})),select.value);
 }
 function changed(){select.dispatchEvent(new Event('input',{bubbles:true}));select.dispatchEvent(new Event('change',{bubbles:true}));}
 for(const [i,control] of [commodity.control,exchange.control,product.control].entries())control.addEventListener('change',()=>{for(const downstream of [commodity.control,exchange.control,product.control].slice(i+1))(downstream as HTMLSelectElement).value='';select.value='';date.set([],'');refresh();changed();});
 const quantity=lots.control as HTMLInputElement;quantity.type='text';quantity.inputMode='numeric';quantity.pattern='-?[0-9]+';quantity.maxLength=7;
 const execution=price.control as HTMLInputElement;execution.type='text';execution.inputMode='decimal';execution.maxLength=45;
 const fields=document.createElement('div');fields.className='schedule-fields';fields.append(lots.wrap,price.wrap);
 const details=document.createElement('details'),summary=document.createElement('summary'),terms=document.createElement('dl');summary.textContent='Contract terms';terms.id=ids.terms;terms.className='side-terms';details.append(summary,terms);host.classList.add('instrument-editor');host.dataset.instrumentType='futures';host.replaceChildren(heading,filters,dateHost,contract.wrap,fields,details);
 return{sync(disabled=false){locked=disabled;for(const control of [commodity.control,exchange.control,product.control,select]){const choice=control as HTMLSelectElement;choice.disabled=locked||choice.options.length<=1;}date.sync();},populate(rows:FuturesContract[],selected:string){
  catalog=rows;const current=rows.find(c=>futuresContractKey(c)===selected);
  if(current){choose(commodity.control as HTMLSelectElement,[{value:current.commodity,name:current.commodityName}],current.commodity);choose(exchange.control as HTMLSelectElement,[{value:current.exchange,name:current.exchangeName}],current.exchange);choose(product.control as HTMLSelectElement,[{value:current.product,name:current.name}],current.product);date.set([current.deliveryFrom],current.deliveryFrom);}
  refresh();select.value=current?selected:'';
 },clear(){catalog=[];for(const control of [commodity.control,exchange.control,product.control,select])(control as HTMLSelectElement).value='';date.set([],'');refresh();},render(c:FuturesContract|undefined,zone:string,mark:(value:string,tick:string)=>string){
  const rows=c?futuresTermRows(c,zone,mark):[];
  terms.replaceChildren(...rows.map(([label,value])=>{const row=document.createElement('div'),name=document.createElement('dt'),content=document.createElement('dd');row.className='side-term';name.textContent=label;content.textContent=publicText(value);row.append(name,content);return row;}));
 }};
}

/** A dated contract's terms as the editor and the position panel list them, each exact; `mark` formats the contract's curve price. */
export function futuresTermRows(c:FuturesContract,zone:string,mark:(value:string,tick:string)=>string):(readonly [string,string])[]{
  return [
   ['Product',c.name],['Curve',curveName(c.curve)],['Delivery start',c.deliveryFrom],['Delivery end (exclusive)',c.deliveryTo],
   ['Contract size',`${c.contractSize} ${c.unit} per lot`],['Nominal size',`${c.nominalSize} ${c.nominalUnit}`],['Quantity basis',c.lotBasis],
   ['Currency',c.currency],['Settlement',c.settlementMethod],['Settlement index',c.index??'Physical delivery contract'],['Quote type',c.quoteKind??'outright'],
   ...(c.basisSettlement?[["Basis benchmark",c.basisSettlement.benchmarkSeries.code] as const]:[]),
   ['Eligibility source',c.eligibilityNote??'No additional source note supplied'],['Lot factor',c.lotFactor??'Fixed contract total'],['Specification source',c.sourceUrl??'Source URL unavailable'],
   ['Quote multiplier',c.quoteMultiplier],['Execution tick',c.tick],['Settlement-price increment',c.clearingIncrement],['Minimum price',c.minimumPrice],['Maximum price',c.maximumPrice],
   [c.firstTrading===null&&c.firstTradingDate?'First trading date':c.firstTrading?`Actual first trading · ${zone}`:'Actual first trading',c.firstTrading===null&&c.firstTradingDate?`${displayDate(c.firstTradingDate)} · exact time unknown`:firstTradingText(c.firstTrading,zone)],[`Trading eligibility from · ${zone}`,displayInstant(c.eligibleFrom,zone)],[`Last trading (exclusive) · ${zone}`,displayInstant(c.lastTrading,zone)],[c.priceSource?.kind==='synthetic'?'Generated price':'Selected curve price',mark(c.mark,c.clearingIncrement)]
  ];
}
