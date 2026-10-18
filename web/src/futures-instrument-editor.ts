import {curveName,displayInstant,publicText} from './market-view.js';
import type {FuturesContract} from './futures-contract.js';

// Product-specific presentation shared by booking and amendment. Catalog terms
// and computed quantities/prices originate in the managed operations.
export function futuresInstrumentEditor(host:HTMLElement,ids:{contract:string;lots:string;price:string;terms:string}){
 const heading=document.createElement('h3');heading.textContent='Futures instrument';
 function field(id:string,label:string,input:boolean){const wrap=document.createElement('div'),caption=document.createElement('label');caption.htmlFor=id;caption.textContent=label;const control=document.createElement(input?'input':'select');control.id=id;control.required=true;wrap.append(caption,control);return{wrap,control};}
 const contract=field(ids.contract,'Dated futures contract',false),lots=field(ids.lots,'Quantity · signed lots',true),price=field(ids.price,'Execution price · quote units',true);
 const quantity=lots.control as HTMLInputElement;quantity.type='text';quantity.inputMode='numeric';quantity.pattern='-?[0-9]+';quantity.maxLength=7;
 const execution=price.control as HTMLInputElement;execution.type='text';execution.inputMode='decimal';execution.maxLength=45;
 const fields=document.createElement('div');fields.className='schedule-fields';fields.append(lots.wrap,price.wrap);
 const details=document.createElement('details'),summary=document.createElement('summary'),terms=document.createElement('dl');summary.textContent='Contract terms';terms.id=ids.terms;terms.className='side-terms';details.append(summary,terms);host.classList.add('instrument-editor');host.dataset.instrumentType='futures';host.replaceChildren(heading,contract.wrap,fields,details);
 return{render(c:FuturesContract|undefined,zone:string,mark:(value:string,tick:string)=>string){
  const rows:readonly (readonly [string,string])[]=c?[
   ['Product',c.name],['Curve',curveName(c.curve)],['Delivery start',c.deliveryFrom],['Delivery end (exclusive)',c.deliveryTo],
   ['Contract size',`${c.contractSize} ${c.unit} per lot`],['Nominal size',`${c.nominalSize} ${c.nominalUnit}`],['Quantity basis',c.lotBasis],
   ['Currency',c.currency],['Settlement',c.settlementMethod],['Settlement index',c.index??'Physical delivery contract'],['Quote type',c.quoteKind??'outright'],
   ...(c.basisSettlement?[["Basis benchmark",c.basisSettlement.benchmarkSeries.code] as const]:[]),
   ['Example admission',c.eligibilityNote??'Project-authored synthetic terms'],['Lot factor',c.lotFactor??'Fixed contract total'],['Specification source',c.sourceUrl??'Project-authored synthetic specification'],
   ['Quote multiplier',c.quoteMultiplier],['Execution tick',c.tick],['Settlement-price increment',c.clearingIncrement],['Minimum price',c.minimumPrice],['Maximum price',c.maximumPrice],
   [`First trading · ${zone}`,displayInstant(c.firstTrading,zone)],[`Last trading (exclusive) · ${zone}`,displayInstant(c.lastTrading,zone)],['Selected curve price',mark(c.mark,c.clearingIncrement)]
  ]:[];
  terms.replaceChildren(...rows.map(([label,value])=>{const row=document.createElement('div'),name=document.createElement('dt'),content=document.createElement('dd');row.className='side-term';name.textContent=label;content.textContent=publicText(value);row.append(name,content);return row;}));
 }};
}
