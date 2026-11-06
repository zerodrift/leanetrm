import type {TermSection} from './position-panel.js';
import {priceUnit} from './desk-model.js';
import {displayInstant,friendly} from './market-view.js';
export function renderPhysicalSections(host:HTMLElement,sections:readonly TermSection[]){
 const root=document.createElement('div');root.className='physical-instrument';
 for(const section of sections){const box=document.createElement('section');box.className='side-panel-section';const title=document.createElement('h3');title.textContent=section.title;const list=document.createElement('dl');list.className='side-terms-list';for(const [name,value] of section.rows){const row=document.createElement('div');row.className='side-term';row.dataset.field=name;const key=document.createElement('dt'),text=document.createElement('dd');key.textContent=name;text.textContent=value;row.append(key,text);list.append(row);}box.append(title,list);root.append(box);}
 host.append(root);return()=>root.remove();
}
export type GasIndexedCashFacts={slot:string;index_code:string;observation_convention:string;payment_convention:string;payment_day:string;spread:string;projection_id:string|null};
export type GasInstrumentFacts={head:Record<string,string|number|boolean|null>;physical:Record<string,string|number|null>[];components:Record<string,string|number|null>[];indexedCash?:GasIndexedCashFacts[]};
export function gasInstrumentSections(f:GasInstrumentFacts,zone:string):TermSection[]{
 const quote=(p:GasInstrumentFacts['physical'][number]|undefined)=>p?.currency!=null&&p.unit!=null&&p.multiplier!=null?priceUnit({currency:String(p.currency),unit:String(p.unit),quoteMultiplier:String(p.multiplier)}):'Unavailable';
 if(!f.components.length)return [...f.physical.map(p=>({title:'Physical delivery',rows:[['Pipeline',String(p.pipeline_code)],['Zone',String(p.zone_code)],['Delivery point',String(p.point_code)],['Product',String(p.product_code)],['Direction',Number(f.head.direction)===1?'Receive':'Deliver'],['Delivery',`${p.delivery_from} to ${p.delivery_to} · exclusive`],['Quantity basis',friendly(f.head.quantity_basis)],['Quantity',`${f.head.quantity} ${p.unit}`],['Pricing reference',String(p.curve_name)]] as [string,string][]})),
  {title:'Fixed consideration',rows:[['Fixed unit price',String(f.head.fixed_price)],['Price unit',quote(f.physical[0])],['Payment',displayInstant(String(f.head.payment_at),zone)],['Usage fee',f.head.usage_fee===null?'None':String(f.head.usage_fee)]]}];
 return f.components.map<TermSection>(m=>{
  const source=f.physical.find(s=>Number(s.slot)===Number(m.source_slot));
  const cash:TermSection['rows']=[['Side',friendly(m.role)],['Direction',Number(m.direction)===1?'Receive':'Pay'],['Quantity source',String(f.components.find(s=>s.slot===m.source_slot)?.code??'Unavailable')],['Quantity stage',friendly(m.stage)]];
  if(m.role==='indexed_consideration'){
   const index=f.indexedCash?.find(i=>Number(i.slot)===Number(m.slot));if(!index)throw Error('The indexed gas Side is missing its retained terms.');
   return{title:String(m.code),rows:[...cash,['Pricing index',index.index_code],['Observation convention',index.observation_convention],['Spread',index.spread],['Price unit',quote(source)],
    ['Forecast projection',index.projection_id??'Assigned physical reference'],['Payment convention',index.payment_convention],['Payment day',String(index.payment_day)]]};
  }
  const p=f.physical.find(p=>Number(p.slot)===Number(m.slot));const quantity:TermSection['rows']=m.independent_quantity!==null?[['Quantity',`${m.independent_quantity} ${p?.unit}`]]:[['Quantity source',String(f.components.find(s=>s.slot===m.source_slot)?.code??'Unavailable')],['Link formula',String(m.formula)],['Link fraction',String(m.fraction)]];return{title:String(m.code),rows:p?[
  ['Side','Physical delivery'],['Pipeline',String(p.pipeline_code)],['Zone',String(p.zone_code)],['Delivery point',String(p.point_code)],['Product',String(p.product_code)],
  ['Direction',Number(m.direction)===1?'Receive':'Deliver'],['Delivery',`${m.delivery_from} to ${m.delivery_to} · exclusive`],['Quantity basis',friendly(m.quantity_basis)],...quantity,['Unit',String(p.unit)],['Pricing reference',String(p.curve_name)]
 ]:[...cash,[m.tariff_code?'Tariff':'Fixed unit price',String(m.tariff_code??m.price)],['Price unit',quote(source)],['Payment',displayInstant(String(m.payment_at),zone)]]};});
}
