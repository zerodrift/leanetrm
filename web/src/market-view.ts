export type Row = Record<string, string | number | null>;
export type ReferenceRevision={catalogId:string;revisionId:string};
export type ReferenceDisplay={sourceRevision:ReferenceRevision;displayRevision:ReferenceRevision;version:number;references:Record<string,Row[]>};
export type Frame = {id:string; name:string; at:string; originCloseId?:string|null;observationIds:string[];curveContracts:Record<string,string[]>};
export type Workspace = {futuresConvention:string;curveNames?:{code:string;name:string}[]|null;powerCurveCodes:string[];unitCatalogError?:string|null;unitCatalog?:{revision:unknown;references:Record<string,Row[]>};referenceDisplay?:ReferenceDisplay|null;unitReferenceDisplay?:ReferenceDisplay|null;profile:string;example:{at:string;curve:string}|null; definitions:Record<string,Row[]>; references:Record<string,Row[]>;referenceScope?:'definition_closure'|'all_visible';observationScope?:'on_demand';observations:Row[]; frames:Frame[]; initialFrameIds:string[]};
import type {PricingNode} from './pricing-view.js';
import type {FrameSummary} from './market-navigation.js';

export type Point = {priceTick?:string;contractId?:string; seriesId?:string; maturity?:string; deliveryFrom?:string; deliveryTo?:string; expiry?:string; value:string; quote?:string; lineage:string[]; discountFactor?:string; displayDiscountFactor?:string; annualRate?:string; actualDays?:number};
export type CurveFront={contractId:string;deliveryFrom:string;deliveryTo:string;price:string};
/** A curve's counts and front, stated by the backend (D233): contracts for futures and forwards, maturities for rates. */
export type CurveSummary={kind:'contracts';contractCount:number;front:CurveFront|null;availability:'no_remaining_contracts'|'no_priced_contract'|null}|{kind:'rates';maturityCount:number;firstMaturity:string|null;firstZeroRate:string|null;dayCount:string};
export type CurveResult = {summary?:CurveSummary;kind?:'rates';convention?:{dayCount:string;compounding:string};availability?:'no_remaining_contracts';trace?:PricingNode[];observationScope?:'selected_frame';observations?:Row[];snapshotId:string; curve:string; at:string; points:Point[]; evaluated:number; numericalPolicy:string};
/** A close's sample of a contract's history, with the observations behind it and when they were known (null until certified). */
export type HistoryPoint = {at:string; value:string|null; snapshotId:string;observationIds:string[];unavailableReason?:string|null;knownAt:string|null};
export const val = (row:Row,key:string) => String(row[key] ?? '—');
const UUID=/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;
export const publicText = (value:unknown) => String(value).replace(UUID,'internal record');
export const friendly = (code:unknown) => publicText(code).replace(/^test_/,'').replaceAll('_',' ').replace(/\b\w/g,c=>c.toUpperCase());
export function exactRow(rows:Row[],key:string,value:string):Row {
  const matches=rows.filter(r=>r[key]===value);
  if(matches.length!==1)throw Error('Some market details are missing or inconsistent. Refresh and try again.');
  return matches[0];
}
// The name a reference row is shown by: its name, else its symbol or display code, else its code made readable.
export function referenceLabel(row:Row){if(typeof row.name==='string')return row.name.replace(/^Synthetic /,'');if(typeof row.symbol==='string')return row.symbol;if(typeof row.display_code==='string')return row.display_code;return friendly(row.code);}
// Overlay rows are for presentation only; callers retain the original financial rows.
export function displayReferenceRows(table:string,rows:Row[],display:ReferenceDisplay|null|undefined,source:ReferenceRevision):Row[]{
  if(!display)return rows;
  if(display.sourceRevision.catalogId!==source.catalogId||display.sourceRevision.revisionId!==source.revisionId)throw Error('Reference display details are inconsistent. Refresh and try again.');
  const labels=display.references[table]??[],byCode=new Map<string,Row>();
  if(!labels.length)return rows;
  for(const label of labels){if(typeof label.code!=='string'||byCode.has(label.code))throw Error('Reference display details are inconsistent. Refresh and try again.');byCode.set(label.code,label);}
  return rows.map(row=>({...row,...byCode.get(String(row.code))}));
}
export function presentedReferences(workspace:Workspace){
  if(!workspace.referenceDisplay)return workspace.references;
  const revision=workspace.definitions.revisions[0],source={catalogId:val(revision,'reference_catalog_id'),revisionId:val(revision,'reference_revision_id')};
  return Object.fromEntries(Object.entries(workspace.references).map(([table,rows])=>[table,displayReferenceRows(table,rows,workspace.referenceDisplay,source)]));
}
export function commodityName(code:string,workspace:Workspace){const row=exactRow(presentedReferences(workspace).commodities,'code',code);const name=publicText(val(row,'name').replace(/^Synthetic /,''));return name.charAt(0).toUpperCase()+name.slice(1);}
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
export function displayDate(value:string){return new Intl.DateTimeFormat('en',{year:'numeric',month:'short',day:'numeric',timeZone:'UTC'}).format(new Date(value+'T00:00:00Z'));}
export function pointDate(point:Point){const date=point.maturity??point.deliveryFrom;if(!date)throw Error('This sample has no date.');return date;}
export function pointIdentity(point:Point){const id=point.seriesId??point.contractId;if(!id)throw Error('This sample is unavailable.');return id;}
// Building a formatter costs far more than formatting with one, and a table can label tens of thousands
// of instants, so each set of options builds one formatter, reused for the rest of the page's life.
const formatters=new Map<string,Intl.DateTimeFormat>();
function formatter(options:Intl.DateTimeFormatOptions){
  const key=JSON.stringify(options);
  let built=formatters.get(key);
  if(!built){built=new Intl.DateTimeFormat('en',options);formatters.set(key,built);}
  return built;
}
export function displayInstant(value:string,zone:string,seconds=false){return formatter({year:'numeric',month:'short',day:'numeric',hour:'numeric',minute:'2-digit',...(seconds?{second:'2-digit'}:{}),timeZone:zone}).format(new Date(value));}
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
// Display minutes (or seconds) without rounding or changing the underlying instant. Repeated
// local clocks get an offset only where needed (e.g. the autumn DST fold).
export function timeLabels(values:string[],zone:string,seconds=false){
  const labels=values.map(v=>displayInstant(v,zone,seconds)),units=values.map(v=>Math.floor(new Date(v).getTime()/(seconds?1000:60000)));
  const identities=new Map<string,Set<number>>();
  labels.forEach((label,i)=>{if(!identities.has(label))identities.set(label,new Set());identities.get(label)!.add(units[i]);});
  return labels.map((label,i)=>identities.get(label)!.size<2?label:`${label} ${formatter({timeZone:zone,timeZoneName:'shortOffset'}).formatToParts(new Date(values[i])).find(p=>p.type==='timeZoneName')!.value}`);
}
export function frameOptions(frames:FrameSummary[],zone:string){
  const totals=new Map<number,number>(),counts=new Map<number,number>(),dates=timeLabels(frames.map(f=>f.at),zone);
  const minutes=frames.map(f=>Math.floor(new Date(f.at).getTime()/60000));
  minutes.forEach(m=>totals.set(m,(totals.get(m)??0)+1));
  return frames.map((f,i)=>{const minute=minutes[i],number=(counts.get(minute)??0)+1;counts.set(minute,number);const version=number===1?'Original':f.name==='Edited inputs'?`Correction ${number-1}`:`Saved version ${number}`;return {value:f.id,name:`${dates[i]}${totals.get(minute)!>1?' · '+version:''}`};});
}
export function marketTypeName(kind:MarketType){return {futures:'Futures',forwards:'Forward curves',mixed:'Mixed curves',rates:'Interest rates'}[kind];}
export function quoteUnit(quoteCode:string,workspace:Workspace){
  const refs=presentedReferences(workspace),quote=exactRow(refs.quote_specifications,'code',quoteCode),quantity=exactRow(refs.quantity_specifications,'code',val(quote,'quantity_code'));
  const currency=exactRow(refs.currencies,'code',val(quote,'currency_code')),unit=exactRow(refs.units,'code',val(quantity,'unit_code'));
  return `${val(currency,'display_code')} / ${val(unit,'symbol')}${quote.multiplier==='1'?'':` × ${val(quote,'multiplier')}`}`;
}
export const fieldNames:Record<string,string>={eligible_from:'Trading eligibility from',qualification_source_code:'Qualification source',source_series_code:'Source contract code',usage_policy:'Usage policy',execution_venue:'Execution venue',eligibility_rule:'Eligibility rule',eligibility_offset_seconds:'Eligibility offset (seconds)',eligibility_tzdb_version:'Eligibility time-zone release',latitude:'Latitude (°)',longitude:'Longitude (°)',day_count:'Day count',compounding:'Compounding',display_code:'Code',display_minor_units:'Decimal places',code:'Name',name:'Description',symbol:'Symbol',dimension_code:'Measurement',commodity_code:'Commodity',basis_code:'Measurement basis',unit_code:'Unit',quantity_code:'Quantity',currency_code:'Currency',multiplier:'Price multiplier',trading_tick:'Trading tick',clearing_increment:'Settlement increment',minimum:'Minimum',maximum:'Maximum',kind:'Type',numerator_quantity_code:'Input quantity',denominator_quantity_code:'Output quantity',location_code:'Location',profile_code:'Delivery pattern',clock_code:'Market clock',calendar_code:'Calendar',type_code:'Location type',delivery_from:'Delivery starts',delivery_to:'Delivery ends',event_at:'Date and time',event_date:'Date',milestone:'Event',value_state:'Availability',definition_state:'Status',product_code:'Product',series_code:'Contract',series_label:'Contract month',exchange_code:'Exchange',source_code:'Source',lot_unit_code:'Contract unit',contract_size:'Contract size',lot_basis:'Quantity basis',quote_currency_code:'Price currency',quote_unit_code:'Price unit',quote_multiplier:'Price multiplier',outright_tick_size:'Trading tick',market_clock_code:'Market clock',trading_calendar_code:'Trading calendar',delivery_calendar_code:'Delivery calendar',settlement_calendar_code:'Settlement calendar',settlement_method:'Settlement method',clearing_house_code:'Clearing house',numerator:'Numerator',denominator:'Denominator',increment:'Rounding increment',tie_rule:'Rounding method',stage:'Applies to',retrieved_at:'Retrieved',locator:'Source',evidence:'Evidence',index_a_code:'First index',index_b_code:'Second index',quantity_basis:'Quantity basis',interval_minutes:'Interval (minutes)',utc_offset_minutes:'UTC offset (minutes)',zone_name:'Time zone',iana_zone:'Time zone',tzdb_version:'tzdb release',fixed_offset_seconds:'Fixed offset (seconds)',weekend_mask:'Weekend mask (bit 0 = Monday)',coverage_from:'Covers from',coverage_to:'Covers until (excluded)',peak_start_hour:'Peak starts (hour beginning)',peak_end_hour:'Peak ends (hour beginning, excluded)',peak_saturday:'Saturday peak',business_date:'Date',date:'Date',is_business_day:'Business day'};
export function publicColumns(rows:Row[]){return rows.length?Object.keys(rows[0]).filter(k=>!k.endsWith('_id')&&k!=='id'&&k!=='implementation'&&!rows.some(r=>typeof r[k]==='string'&&String(r[k]).match(UUID)!==null)).map(key=>({key,name:fieldNames[key]??friendly(key)})):[];}

const summaryOf=(result:Pick<CurveResult,'summary'>)=>result.summary??(()=>{throw Error('This curve has no summary. Refresh and try again.');})();
/** The summary tiles of a selected curve, from the backend's summary; the browser neither counts points nor reads the first one. */
export function summaryTiles(result:Pick<CurveResult,'summary'>,settlement:string){
  const summary=summaryOf(result),tiles:{name:string;value:string;numeric?:boolean}[]=[];
  if(summary.kind==='rates'){
    tiles.push({name:'Maturities',value:String(summary.maturityCount)});
    if(summary.firstMaturity!==null&&summary.firstZeroRate!==null)tiles.push({name:'First maturity',value:displayDate(summary.firstMaturity)},{name:'First zero rate (%)',value:summary.firstZeroRate,numeric:true});
    return [...tiles,{name:'Day count',value:summary.dayCount}];
  }
  if(summary.front)tiles.push({name:'Contracts',value:String(summary.contractCount)},{name:'Front contract',value:deliveryLabel(summary.front.deliveryFrom,summary.front.deliveryTo)},{name:'Front price',value:summary.front.price,numeric:true});
  else tiles.push({name:'Contracts',value:summary.availability==='no_priced_contract'?'None priced':'None remaining'});
  return [...tiles,{name:'Settlement',value:settlement}];
}
/** The count under a curve's chart, from the backend's summary. */
export function contractCountText(result:Pick<CurveResult,'summary'>){
  const summary=summaryOf(result),count=summary.kind==='rates'?summary.maturityCount:summary.contractCount;
  return `${count} ${summary.kind==='rates'?(count===1?'rate sample':'rate samples'):(count===1?'contract':'contracts')}`;
}
