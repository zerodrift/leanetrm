import {type Row,type ReferenceDisplay,type ReferenceRevision,displayReferenceRows,exactRow,referenceLabel,fieldNames,friendly} from './market-view.js';
// Presentation only: the futures product page's layout of one backend read. Every value is the read's own; a term it does not
// supply reads "Not supplied", and a code it names without the referenced row, or a row it does not account for, is an error.
export type FuturesProductRead={product:Row;series:Row[];milestones:Row[];executionPolicies:Row[];references:Record<string,Row[]>;referenceRevision:ReferenceRevision;referenceDisplay?:ReferenceDisplay|null};
export type ProductField={key:string;label:string;value:string;linkTo?:{table:string;code:string}};

const notSupplied='Not supplied';
const linked:Record<string,string>={exchange_code:'exchanges',clearing_house_code:'clearing_houses',commodity_code:'commodities',location_code:'locations',market_clock_code:'clock_rules',
  lot_unit_code:'units',quote_currency_code:'currencies',quote_unit_code:'units',trading_calendar_code:'calendars',delivery_calendar_code:'calendars',settlement_calendar_code:'calendars'};
const worded=new Set(['settlement_method','lot_basis']);
const sections=[
  ['Venue',['exchange_code','clearing_house_code','settlement_method']],
  ['Underlying',['commodity_code','location_code','market_clock_code']],
  ['Economics',['contract_size','lot_unit_code','lot_basis','quote_currency_code','quote_unit_code','quote_multiplier','outright_tick_size']],
  ['Calendars',['trading_calendar_code','delivery_calendar_code','settlement_calendar_code']]
] as const;
// Milestone columns in the order of a contract's life.
const milestoneLabels:Record<string,string>={first_trading:'First trading',last_trading:'Last trading',first_notice:'First notice',last_notice:'Last notice',
  final_settlement:'Final settlement',delivery_start:'Delivery start',delivery_end:'Delivery end'};
const inconsistent=()=>Error('The futures product details are missing or inconsistent. Refresh and try again.');
const date=(value:Row[string])=>{if(value===null)return notSupplied;if(typeof value!=='string')throw inconsistent();return value;};
const shown=(read:FuturesProductRead,table:string,rows:Row[])=>displayReferenceRows(table,rows,read.referenceDisplay,read.referenceRevision);

export function productHeader(read:FuturesProductRead){
  const [product]=shown(read,'futures_products',[read.product]);
  return {name:String(product.name),state:friendly(product.definition_state),code:String(product.code)};
}

export function productSections(read:FuturesProductRead){
  const field=(key:string):ProductField=>{
    if(!(key in read.product))throw inconsistent();
    const value=read.product[key],label=fieldNames[key];
    if(value===null)return {key,label,value:notSupplied};
    const table=linked[key],code=String(value);
    if(table)return {key,label,value:referenceLabel(exactRow(shown(read,table,read.references[table]??[]),'code',code)),linkTo:{table,code}};
    return {key,label,value:worded.has(key)?friendly(code):code};
  };
  return sections.map(([title,keys])=>({title,fields:keys.map(field)}));
}

// The rows of one contract's column set, keyed by contract; a row naming another contract, or repeating one, is an error.
function byContract(contracts:ReadonlySet<string>,rows:Row[],key:(row:Row)=>string){
  const found=new Map<string,Map<string,Row>>();
  for(const row of rows){
    const contract=String(row.series_code),within=found.get(contract)??new Map<string,Row>();
    if(!contracts.has(contract)||within.has(key(row)))throw inconsistent();
    found.set(contract,within.set(key(row),row));
  }
  return found;
}

export function milestoneColumns(read:FuturesProductRead){
  const present=new Set(read.milestones.map(m=>String(m.milestone)));
  return Object.entries(milestoneLabels).filter(([key])=>present.has(key)).map(([key,label])=>({key,label}));
}

function milestoneText(row:Row){
  switch(row.value_state){
    case 'known':if(row.business_date===null)throw inconsistent();return date(row.business_date);
    case 'unknown':return 'Unknown';
    case 'not_applicable':return 'Not applicable';
    default:throw inconsistent();
  }
}

// One row per contract in the read's order, with a value for every milestone column. `instant` writes an eligibility instant in the person's time zone.
export function monthlyRows(read:FuturesProductRead,instant:(value:string)=>string){
  const series=shown(read,'futures_series',read.series),contracts=new Set(series.map(s=>String(s.code)));
  if(contracts.size!==series.length)throw inconsistent();
  for(const m of read.milestones)if(!(String(m.milestone) in milestoneLabels))throw inconsistent();
  const columns=milestoneColumns(read),milestones=byContract(contracts,read.milestones,m=>String(m.milestone)),policies=byContract(contracts,read.executionPolicies,p=>String(p.implementation));
  return series.map(s=>{
    const code=String(s.code),eligible=[...(policies.get(code)?.values()??[])];
    const eligibility=eligible.length?eligible.map(p=>`From ${instant(String(p.eligible_from))}${eligible.length>1?` · ${friendly(p.implementation)}`:''}`).join('; '):'No execution policy';
    return {code,label:String(s.series_label),deliveryFrom:date(s.delivery_from),deliveryTo:date(s.delivery_to),
      milestones:Object.fromEntries(columns.map(({key})=>{const m=milestones.get(code)?.get(key);return [key,m?milestoneText(m):notSupplied];})),eligibility};
  });
}
