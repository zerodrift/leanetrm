import {displayPaymentDate} from './payment-date.js';
import type {TermSection} from './position-panel.js';
import {priceUnit} from './desk-model.js';
import {displayInstant,displayExactInstant,friendly} from './market-view.js';
export function renderPhysicalSections(host:HTMLElement,sections:readonly TermSection[]){
 const root=document.createElement('div');root.className='physical-instrument';
 for(const section of sections){const box=document.createElement('section');box.className='side-panel-section';const title=document.createElement('h3');title.textContent=section.title;const list=document.createElement('dl');list.className='side-terms-list';for(const [name,value,exact] of section.rows){const row=document.createElement('div');row.className='side-term';row.dataset.field=name;const key=document.createElement('dt'),text=document.createElement('dd');key.textContent=name;text.textContent=value;if(exact!==undefined)text.title=exact;row.append(key,text);list.append(row);}box.append(title,list);root.append(box);}
 host.append(root);return()=>root.remove();
}
export type PhysicalOverviewSection={label:string;values:{label:string;value:string;kind?:'instant'}[]};
// Only backend-declared instants are formatted. Plain evidence, including text
// that resembles an ISO timestamp, remains unchanged.
export function overviewTermSections(sections:readonly PhysicalOverviewSection[],zone:string):TermSection[]{
 const bad=()=>Error('Reread the complete native overview and its marked instants.');
 if(!Array.isArray(sections))throw bad();
 return sections.map(section=>{
  if(!section||typeof section.label!=='string'||!section.label||!Array.isArray(section.values))throw bad();
  return{title:section.label,rows:section.values.map((row:PhysicalOverviewSection['values'][number])=>{
   if(!row||typeof row.label!=='string'||!row.label||typeof row.value!=='string'||Object.hasOwn(row,'kind')&&row.kind!=='instant')throw bad();
   if(row.kind==='instant'){
    try{return[row.label,displayExactInstant(row.value,zone),row.value] as const;}catch{throw bad();}
   }
   return[row.label,row.value] as const;
  })};
 });
}
export type GasIndexedCashFacts={slot:string;index_code:string;observation_convention:string;payment_convention:string;payment_day:string;spread:string;projection_id:string|null};
export type GasInstrumentFacts={head:Record<string,string|number|boolean|null>;physical:Record<string,string|number|null>[];components:Record<string,string|number|null>[];confirmations?:{agreements:string;purchaseTradeId:string;saleTradeId:string;purchaseReference:string;saleReference:string}[];indexedCash?:GasIndexedCashFacts[]};
function gasChargeSection(member:GasInstrumentFacts['components'][number],facts:GasInstrumentFacts):TermSection{
 const missing=()=>Error('The charge Side is missing its retained terms.');
 const term=(field:string)=>{const value=member[field];if(value==null)throw missing();return String(value);};
 const clockSlot=term('clock_slot'),clock=facts.components.find(side=>side.role==='physical'&&String(side.slot)===clockSlot);
 const terms=facts.physical.find(side=>String(side.slot)===clockSlot);
 if(clock?.code==null||terms?.currency==null||terms.unit==null)throw missing();
 const title=term('code');
 const rows:TermSection['rows']=[['Side','Charge'],['Direction',Number(term('direction'))===1?'Receive':'Pay'],['Gas day of',String(clock.code)]];
 if(member.charge_basis==='lump_sum')return{title,rows:[...rows,
  ['Charged on','A lump sum'],['Amount',`${term('price')} ${terms.currency}`],['Payment date',displayPaymentDate(term('payment_at'))]]};
 if(member.charge_basis!=='capacity')throw missing();
 const numerator=term('charge_quantity_n'),denominator=term('charge_quantity_d');
 return{title,rows:[...rows,['Charged on','A daily capacity'],['Rate per unit per day',`${term('price')} ${terms.currency}/${terms.unit}/day`],
  ['Daily quantity',`${denominator==='1'?numerator:`${numerator}/${denominator}`} ${terms.unit}`],['From',term('delivery_from')],['To · exclusive',term('delivery_to')],
  ['Payment day',term('payment_day')],['Payment convention',term('payment_convention')]]};
}
function gasCashoutRows(member:GasInstrumentFacts['components'][number],source:string|undefined):TermSection['rows']{
 const missing=()=>Error('The cash-out Side is missing its retained terms.');
 const choice=(field:string,words:Record<string,string>)=>{const value=member[field];if(typeof value!=='string'||!Object.hasOwn(words,value))throw missing();return words[value];};
 if(!source||member.cashout_profile!=='daily_index_cashout_v1')throw missing();
 return [['Side','Imbalance cash-out'],['Direction',Number(member.direction)===1?'Receive':'Pay'],['Quantity source',source+' · reported service-account balance'],
  ['Balance side',choice('balance_side',{owed_to_us:'Owed to us',owed_by_us:'Owed by us'})],
  ['A statement reports',choice('statement_basis',{daily_imbalance_v1:"That day's imbalance",month_to_date_balance_v1:'The month-to-date balance'})],
  ['Several accounts',choice('account_netting',{net_accounts_v1:'Net them, then settle',each_account_v1:'Settle each on its own'})],
  ['Statements used',choice('statement_policy',{final_statement_balance_v1:'Final only',reported_statement_balance_v1:'Provisional and final'})]];
}
export function gasInstrumentSections(f:GasInstrumentFacts,zone:string):TermSection[]{
 const quote=(p:GasInstrumentFacts['physical'][number]|undefined)=>p?.currency!=null&&p.unit!=null&&p.multiplier!=null?priceUnit({currency:String(p.currency),unit:String(p.unit),quoteMultiplier:String(p.multiplier)}):'Unavailable';
 if(!f.components.length)return [...f.physical.map(p=>({title:'Physical delivery',rows:[['Pipeline',String(p.pipeline_code)],['Zone',String(p.zone_code)],['Delivery point',String(p.point_code)],['Product',String(p.product_code)],['Direction',Number(f.head.direction)===1?'Receive':'Deliver'],['Delivery',`${p.delivery_from} to ${p.delivery_to} · exclusive`],['Quantity basis',friendly(f.head.quantity_basis)],['Quantity',`${f.head.quantity} ${p.unit}`],['Pricing reference',String(p.curve_name)]] as [string,string][]})),
  {title:'Fixed consideration',rows:[['Fixed unit price',String(f.head.fixed_price)],['Price unit',quote(f.physical[0])],['Payment',displayPaymentDate(String(f.head.payment_at))],['Usage fee',f.head.usage_fee===null?'None':String(f.head.usage_fee)]]}];
 const confirmations:TermSection[]=(f.confirmations??[]).map(pair=>({title:'Two-location gas',rows:pair.agreements==='one_agreement'?
  [['Confirmation','One bilateral agreement']]:[['Purchase confirmation',pair.purchaseReference],['Sale confirmation',pair.saleReference]]}));
 return confirmations.concat(f.components.map<TermSection>(m=>{
  if(m.role==='charge')return gasChargeSection(m,f);
  const source=f.physical.find(s=>Number(s.slot)===Number(m.source_slot));
  const sourceCode=f.components.find(s=>String(s.slot)===String(m.source_slot))?.code;
  const cash:TermSection['rows']=m.role==='imbalance_cashout'?gasCashoutRows(m,sourceCode==null?undefined:String(sourceCode)):[['Side',friendly(m.role)],['Direction',Number(m.direction)===1?'Receive':'Pay'],['Quantity source',String(sourceCode??'Unavailable')],['Quantity stage',friendly(m.stage)]];
  if(m.role==='indexed_consideration'||m.role==='imbalance_cashout'){
   const index=f.indexedCash?.find(i=>Number(i.slot)===Number(m.slot));if(!index)throw Error('The indexed gas Side is missing its retained terms.');
   return{title:String(m.code),rows:[...cash,['Pricing index',index.index_code],['Observation convention',index.observation_convention],['Spread',index.spread],['Price unit',quote(source)],
    ['Forecast projection',m.role==='imbalance_cashout'?'Named index curve':index.projection_id??'Assigned physical reference'],['Payment convention',index.payment_convention],['Payment day',String(index.payment_day)]]};
  }
  const p=f.physical.find(p=>Number(p.slot)===Number(m.slot));const quantity:TermSection['rows']=m.independent_quantity!==null?[['Quantity',`${m.independent_quantity} ${p?.unit}`]]:[['Quantity source',String(f.components.find(s=>s.slot===m.source_slot)?.code??'Unavailable')],['Link formula',String(m.formula)],['Link fraction',String(m.fraction)]];return{title:String(m.code),rows:p?[
  ['Side','Physical delivery'],['Pipeline',String(p.pipeline_code)],['Zone',String(p.zone_code)],['Delivery point',String(p.point_code)],['Product',String(p.product_code)],
  ['Direction',Number(m.direction)===1?'Receive':'Deliver'],['Delivery',`${m.delivery_from} to ${m.delivery_to} · exclusive`],['Quantity basis',friendly(m.quantity_basis)],...quantity,['Unit',String(p.unit)],['Pricing reference',String(p.curve_name)]
 ]:[...cash,[m.tariff_code?'Tariff':'Fixed unit price',String(m.tariff_code??m.price)],['Price unit',quote(source)],['Payment',displayPaymentDate(String(m.payment_at))]]};}));
}
