export type Row = Record<string, string | number | null>;
export type Frame = {id:string; name:string; at:string; observationIds:string[];curveContracts:Record<string,string[]>};
export type Workspace = {futuresConvention:string;unitCatalogError?:string|null;unitCatalog?:{revision:unknown;references:Record<string,Row[]>};profile:string;example:{at:string;curve:string}|null; definitions:Record<string,Row[]>; references:Record<string,Row[]>; observations:Row[]; frames:Frame[]; initialFrameIds:string[]};
import type {PricingNode} from './pricing-view.js';

export type Point = {priceTick?:string;contractId?:string; seriesId?:string; maturity?:string; deliveryFrom?:string; deliveryTo?:string; expiry?:string; value:string; quote?:string; lineage:string[]; discountFactor?:string; displayDiscountFactor?:string; annualRate?:string; actualDays?:number};
export type CurveResult = {kind?:'rates';convention?:{dayCount:string;compounding:string};availability?:'no_remaining_contracts';trace?:PricingNode[];snapshotId:string; curve:string; at:string; points:Point[]; evaluated:number; numericalPolicy:string};
export type HistoryPoint = {at:string; value:string|null; snapshotId:string;unavailableReason?:string|null};
export type PriceHistory = {points:HistoryPoint[]};
export const val = (row:Row,key:string) => String(row[key] ?? '—');
const UUID=/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;
export const publicText = (value:unknown) => String(value).replace(UUID,'internal record');
export const friendly = (code:unknown) => publicText(code).replace(/^test_/,'').replaceAll('_',' ').replace(/\b\w/g,c=>c.toUpperCase());
export function exactRow(rows:Row[],key:string,value:string):Row {
  const matches=rows.filter(r=>r[key]===value);
  if(matches.length!==1)throw Error('Some market details are missing or inconsistent. Refresh and try again.');
  return matches[0];
}
export function commodityName(code:string,workspace:Workspace){const row=exactRow(workspace.references.commodities,'code',code);const name=publicText(val(row,'name').replace(/^Synthetic /,''));return name.charAt(0).toUpperCase()+name.slice(1);}
export type MarketType='futures'|'forwards'|'mixed'|'rates';
export const allCurves=(workspace:Workspace)=>[...workspace.definitions.curves,...(workspace.definitions.rate_curves??[])];
// Display names are stored with each curve definition and served as a directory for
// the catalog. A missing or conflicting name is an error, never a code-derived guess.
const curveNames=new Map<string,string|null>();
export function setCurveNames(rows:{code:string;name:string}[]){
  curveNames.clear();
  for(const row of rows)curveNames.set(row.code,curveNames.has(row.code)&&curveNames.get(row.code)!==row.name?null:row.name);
}
export function curveName(code:unknown){const name=curveNames.get(String(code));if(typeof name!=='string'||!name)throw Error('Some market details are missing or inconsistent. Refresh and try again.');return publicText(name);}
export const marketCategory=(curve:Row)=>typeof curve.interest_rate_code==='string'?'rates':val(curve,'commodity_code');
export const categoryName=(code:string,workspace:Workspace)=>code==='rates'?'Interest rates':commodityName(code,workspace);
export function marketType(curve:Row,workspace:Workspace):MarketType {
  if(typeof curve.interest_rate_code==='string')return 'rates';
  const members=workspace.definitions.curve_members.filter(m=>m.curve_code===curve.code);
  if(!members.length)throw Error('This curve has no contracts.');
  const kinds=new Set(members.map(m=>exactRow(workspace.definitions.contracts,'id',val(m,'contract_id')).kind));
  if(kinds.size===1&&kinds.has('future'))return 'futures';
  if(kinds.size===1&&kinds.has('forward'))return 'forwards';
  if([...kinds].every(k=>k==='future'||k==='forward'))return 'mixed';
  throw Error('This contract type is not supported yet.');
}
export function marketGroups(workspace:Workspace,by:'commodity'|'marketType',commodity:string,type:string){
  const curves=allCurves(workspace).filter(c=>(commodity==='all'||marketCategory(c)===commodity)&&(type==='all'||marketType(c,workspace)===type));
  const groups=new Map<string,Row[]>();
  for(const curve of curves){const name=by==='commodity'?categoryName(marketCategory(curve),workspace):marketTypeName(marketType(curve,workspace));if(!groups.has(name))groups.set(name,[]);groups.get(name)!.push(curve);}
  return [...groups.entries()].sort(([a],[b])=>a.localeCompare(b)).map(([name,curves])=>({name,curves}));
}
export function displayDate(value:string){return new Intl.DateTimeFormat('en',{year:'numeric',month:'short',day:'numeric',timeZone:'UTC'}).format(new Date(value+'T00:00:00Z'));}
export function pointDate(point:Point){const date=point.maturity??point.deliveryFrom;if(!date)throw Error('This sample has no date.');return date;}
export function pointIdentity(point:Point){const id=point.seriesId??point.contractId;if(!id)throw Error('This sample is unavailable.');return id;}
export function displayInstant(value:string,zone:string){return new Intl.DateTimeFormat('en',{year:'numeric',month:'short',day:'numeric',hour:'numeric',minute:'2-digit',timeZone:zone}).format(new Date(value));}
export function deliveryMonth(value:string){return new Intl.DateTimeFormat('en',{month:'short',year:'numeric',timeZone:'UTC'}).format(new Date(value+'T00:00:00Z'));}
export function deliveryLabel(from:string,to:string){
  const next=new Date(from+'T00:00:00Z'),end=new Date(to+'T00:00:00Z');
  if(!Number.isFinite(next.getTime())||!Number.isFinite(end.getTime())||next>=end)throw Error('The delivery period is invalid.');
  next.setUTCDate(next.getUTCDate()+1);if(next.getTime()===end.getTime())return displayDate(from);
  next.setTime(new Date(from+'T00:00:00Z').getTime());next.setUTCMonth(next.getUTCMonth()+1);
  if(from.endsWith('-01')&&next.getTime()===end.getTime())return deliveryMonth(from);
  end.setUTCDate(end.getUTCDate()-1);return `${displayDate(from)} – ${displayDate(end.toISOString().slice(0,10))}`;
}
export function historyPriceLabel(point:HistoryPoint){
  if(point.value!==null){if(point.unavailableReason)throw Error('The price history is inconsistent.');return point.value;}
  if(point.unavailableReason==='source_expired')return 'Past marking cutoff';
  if(point.unavailableReason==='missing_observation')return 'No saved price';
  throw Error('The price history availability is missing.');
}
// Display minutes without rounding or changing the underlying instant. Repeated
// local clocks get an offset only where needed (e.g. the autumn DST fold).
export function timeLabels(values:string[],zone:string){
  const labels=values.map(v=>displayInstant(v,zone)),minutes=values.map(v=>Math.floor(new Date(v).getTime()/60000));
  const identities=new Map<string,Set<number>>();
  labels.forEach((label,i)=>{if(!identities.has(label))identities.set(label,new Set());identities.get(label)!.add(minutes[i]);});
  return labels.map((label,i)=>identities.get(label)!.size<2?label:`${label} ${new Intl.DateTimeFormat('en',{timeZone:zone,timeZoneName:'shortOffset'}).formatToParts(new Date(values[i])).find(p=>p.type==='timeZoneName')!.value}`);
}
export function frameOptions(frames:Frame[],zone:string){
  const totals=new Map<number,number>(),counts=new Map<number,number>(),dates=timeLabels(frames.map(f=>f.at),zone);
  const minutes=frames.map(f=>Math.floor(new Date(f.at).getTime()/60000));
  minutes.forEach(m=>totals.set(m,(totals.get(m)??0)+1));
  return frames.map((f,i)=>{const minute=minutes[i],number=(counts.get(minute)??0)+1;counts.set(minute,number);const version=number===1?'Original':f.name==='Edited inputs'?`Correction ${number-1}`:`Saved version ${number}`;return {value:f.id,name:`${dates[i]}${totals.get(minute)!>1?' · '+version:''}`};});
}
export function marketTypeName(kind:MarketType){return {futures:'Futures',forwards:'Forward curves',mixed:'Mixed curves',rates:'Interest rates'}[kind];}
export function quoteUnit(quoteCode:string,workspace:Workspace){
  const refs=workspace.references,quote=exactRow(refs.quote_specifications,'code',quoteCode),quantity=exactRow(refs.quantity_specifications,'code',val(quote,'quantity_code'));
  const currency=exactRow(refs.currencies,'code',val(quote,'currency_code')),unit=exactRow(refs.units,'code',val(quantity,'unit_code'));
  return `${val(currency,'display_code')} / ${val(unit,'symbol')}${quote.multiplier==='1'?'':` × ${val(quote,'multiplier')}`}`;
}
export const fieldNames:Record<string,string>={latitude:'Latitude (°)',longitude:'Longitude (°)',day_count:'Day count',compounding:'Compounding',display_code:'Code',display_minor_units:'Decimal places',code:'Name',name:'Description',symbol:'Symbol',dimension_code:'Measurement',commodity_code:'Commodity',basis_code:'Measurement basis',unit_code:'Unit',quantity_code:'Quantity',currency_code:'Currency',multiplier:'Price multiplier',trading_tick:'Trading tick',clearing_increment:'Settlement increment',minimum:'Minimum',maximum:'Maximum',kind:'Type',numerator_quantity_code:'Input quantity',denominator_quantity_code:'Output quantity',location_code:'Location',profile_code:'Delivery pattern',clock_code:'Market clock',calendar_code:'Calendar',type_code:'Location type',delivery_from:'Delivery starts',delivery_to:'Delivery ends',event_at:'Date and time',event_date:'Date',milestone:'Event',value_state:'Availability',definition_state:'Status',product_code:'Product',series_code:'Contract',series_label:'Contract month',exchange_code:'Exchange',source_code:'Source',lot_unit_code:'Contract unit',contract_size:'Contract size',lot_basis:'Quantity basis',quote_currency_code:'Price currency',quote_unit_code:'Price unit',quote_multiplier:'Price multiplier',outright_tick_size:'Trading tick',market_clock_code:'Market clock',trading_calendar_code:'Trading calendar',clearing_house_code:'Clearing house',numerator:'Numerator',denominator:'Denominator',increment:'Rounding increment',tie_rule:'Rounding method',stage:'Applies to',retrieved_at:'Retrieved',locator:'Source',evidence:'Evidence',index_a_code:'First index',index_b_code:'Second index',quantity_basis:'Quantity basis',interval_minutes:'Interval (minutes)',utc_offset_minutes:'UTC offset (minutes)',zone_name:'Time zone',iana_zone:'Time zone',tzdb_version:'tzdb release',fixed_offset_seconds:'Fixed offset (seconds)',weekend_mask:'Weekend mask (bit 0 = Monday)',coverage_from:'Covers from',coverage_to:'Covers until (excluded)',peak_start_hour:'Peak starts (hour beginning)',peak_end_hour:'Peak ends (hour beginning, excluded)',peak_saturday:'Saturday peak',business_date:'Date',date:'Date',is_business_day:'Business day'};
export function publicColumns(rows:Row[]){return rows.length?Object.keys(rows[0]).filter(k=>!k.endsWith('_id')&&k!=='id'&&k!=='implementation'&&!rows.some(r=>typeof r[k]==='string'&&String(r[k]).match(UUID)!==null)).map(key=>({key,name:fieldNames[key]??friendly(key)})):[];}
